using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Tests;

public static class LlmServiceTests
{
	public static void Test(TestContext ctx)
	{
		Console.WriteLine("  LlmServiceTests");

		LLMConfig config = new LLMConfig
		{
			ApiKey = "test-key",
			Model = "test-model",
			Endpoint = "https://test.example.com/v1"
		};
		LlmService service = new LlmService(config);
		List<Tool> tools = BuildTestTools();

		TestEpochToSecondsFromNow(ctx);
		TestGetFirstHeaderValue(ctx);
		TestXmlToolCallParsing(ctx, service, tools);
		TestIsRateLimited(ctx, service);
		TestParseRateLimitSecondsFromErrorBody(ctx, service);
		TestTryAdaptToError(ctx);
	}

	private static List<Tool> BuildTestTools()
	{
		List<Tool> tools = new List<Tool>();

		tools.Add(new Tool
		{
			Definition = new ToolDefinition
			{
				Type = "function",
				Function = new FunctionDefinition
				{
					Name = "read_file",
					Description = "Read a file",
					Parameters = new JsonObject
					{
						["type"] = "object",
						["properties"] = new JsonObject
						{
							["path"] = new JsonObject { ["type"] = "string" },
							["encoding"] = new JsonObject { ["type"] = "string" }
						}
					}
				}
			},
			Handler = (JsonObject args, ToolContext ctx2) => Task.FromResult(new ToolResult("file content", false, "read_file"))
		});

		tools.Add(new Tool
		{
			Definition = new ToolDefinition
			{
				Type = "function",
				Function = new FunctionDefinition
				{
					Name = "write_file",
					Description = "Write a file",
					Parameters = new JsonObject
					{
						["type"] = "object",
						["properties"] = new JsonObject
						{
							["path"] = new JsonObject { ["type"] = "string" },
							["content"] = new JsonObject { ["type"] = "string" }
						}
					}
				}
			},
			Handler = (JsonObject args, ToolContext ctx2) => Task.FromResult(new ToolResult("ok", false, "write_file"))
		});

		return tools;
	}

	private static void TestEpochToSecondsFromNow(TestContext ctx)
	{
		Type[] types = [typeof(long)];

		// Future epoch should return positive seconds.
		long futureEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
		int futureResult = (int)Reflect.Static(typeof(LlmService), "EpochToSecondsFromNow", types, [futureEpoch])!;
		ctx.Assert(futureResult >= 59 && futureResult <= 62, "EpochToSecondsFromNow: future epoch returns ~61");

		// Past epoch should return 0.
		long pastEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
		int pastResult = (int)Reflect.Static(typeof(LlmService), "EpochToSecondsFromNow", types, [pastEpoch])!;
		ctx.AssertEqual(0, pastResult, "EpochToSecondsFromNow: past epoch returns 0");

		// Millisecond epoch should be normalized to seconds.
		long msEpoch = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120) * 1000;
		int msResult = (int)Reflect.Static(typeof(LlmService), "EpochToSecondsFromNow", types, [msEpoch])!;
		ctx.Assert(msResult >= 119 && msResult <= 122, "EpochToSecondsFromNow: millisecond epoch normalized");
	}

	private static void TestGetFirstHeaderValue(TestContext ctx)
	{
		Type[] types = [typeof(HttpResponseHeaders), typeof(string)];

		HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK);
		response.Headers.TryAddWithoutValidation("X-Custom", "value1");

		string? found = (string?)Reflect.Static(typeof(LlmService), "GetFirstHeaderValue", types, [response.Headers, "X-Custom"]);
		ctx.AssertEqual("value1", found, "GetFirstHeaderValue: finds present header");

		string? missing = (string?)Reflect.Static(typeof(LlmService), "GetFirstHeaderValue", types, [response.Headers, "X-Missing"]);
		ctx.AssertNull(missing, "GetFirstHeaderValue: returns null for missing header");
	}

	private static void TestXmlToolCallParsing(TestContext ctx, LlmService service, List<Tool> tools)
	{
		Type[] types = [typeof(string), typeof(List<Tool>)];

		// Valid single tool_call tag.
		string validContent = "Some text\n<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"/test.txt\"}}\n</tool_call>";
		List<ToolCallMessage>? valid = (List<ToolCallMessage>?)Reflect.Instance(service, "TryParseXmlToolCalls", types, [validContent, tools]);
		ctx.AssertNotNull(valid, "XmlToolCalls: valid tool_call parsed");
		ctx.AssertEqual(1, valid?.Count ?? -1, "XmlToolCalls: single call found");
		ctx.AssertEqual("read_file", valid?[0].Function.Name ?? "", "XmlToolCalls: correct tool name");

		// function_call variant.
		string fcContent = "<function_call>{\"name\": \"write_file\", \"arguments\": {\"path\": \"a.txt\", \"content\": \"hello\"}}</function_call>";
		List<ToolCallMessage>? fc = (List<ToolCallMessage>?)Reflect.Instance(service, "TryParseXmlToolCalls", types, [fcContent, tools]);
		ctx.AssertNotNull(fc, "XmlToolCalls: function_call variant parsed");
		ctx.AssertEqual("write_file", fc?[0].Function.Name ?? "", "XmlToolCalls: function_call correct name");

		// Multiple tool calls in one response.
		string multiContent = "<tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a\"}}</tool_call>\n<tool_call>{\"name\": \"write_file\", \"arguments\": {\"path\": \"b\", \"content\": \"c\"}}</tool_call>";
		List<ToolCallMessage>? multi = (List<ToolCallMessage>?)Reflect.Instance(service, "TryParseXmlToolCalls", types, [multiContent, tools]);
		ctx.AssertEqual(2, multi?.Count ?? -1, "XmlToolCalls: multiple calls found");

		// No tags returns null.
		List<ToolCallMessage>? noTags = (List<ToolCallMessage>?)Reflect.Instance(service, "TryParseXmlToolCalls", types, ["Just a regular response", tools]);
		ctx.AssertNull(noTags, "XmlToolCalls: no tags returns null");

		// Unknown tool name returns null.
		string unknownTool = "<tool_call>{\"name\": \"unknown_tool\", \"arguments\": {}}</tool_call>";
		List<ToolCallMessage>? unknown = (List<ToolCallMessage>?)Reflect.Instance(service, "TryParseXmlToolCalls", types, [unknownTool, tools]);
		ctx.AssertNull(unknown, "XmlToolCalls: unknown tool returns null");

		// Extra argument not in definition is rejected.
		string extraArg = "<tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a\", \"bogus\": \"b\"}}</tool_call>";
		List<ToolCallMessage>? extra = (List<ToolCallMessage>?)Reflect.Instance(service, "TryParseXmlToolCalls", types, [extraArg, tools]);
		ctx.AssertNull(extra, "XmlToolCalls: extra arg rejected");

		// Invalid JSON inside tags returns null.
		string invalidJson = "<tool_call>not valid json at all</tool_call>";
		List<ToolCallMessage>? invalid = (List<ToolCallMessage>?)Reflect.Instance(service, "TryParseXmlToolCalls", types, [invalidJson, tools]);
		ctx.AssertNull(invalid, "XmlToolCalls: invalid JSON returns null");

		// Empty arguments is valid.
		string emptyArgs = "<tool_call>{\"name\": \"read_file\", \"arguments\": {}}</tool_call>";
		List<ToolCallMessage>? empty = (List<ToolCallMessage>?)Reflect.Instance(service, "TryParseXmlToolCalls", types, [emptyArgs, tools]);
		ctx.AssertNotNull(empty, "XmlToolCalls: empty args is valid");

		// parameters key accepted as alias for arguments.
		string paramsKey = "<tool_call>{\"name\": \"read_file\", \"parameters\": {\"path\": \"test\"}}</tool_call>";
		List<ToolCallMessage>? paramsResult = (List<ToolCallMessage>?)Reflect.Instance(service, "TryParseXmlToolCalls", types, [paramsKey, tools]);
		ctx.AssertNotNull(paramsResult, "XmlToolCalls: parameters key accepted");

		// Case-insensitive tags.
		string upperCase = "<TOOL_CALL>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a\"}}</TOOL_CALL>";
		List<ToolCallMessage>? upper = (List<ToolCallMessage>?)Reflect.Instance(service, "TryParseXmlToolCalls", types, [upperCase, tools]);
		ctx.AssertNotNull(upper, "XmlToolCalls: case insensitive tags");

		// Missing name field returns null.
		string noName = "<tool_call>{\"arguments\": {\"path\": \"a\"}}</tool_call>";
		List<ToolCallMessage>? noNameResult = (List<ToolCallMessage>?)Reflect.Instance(service, "TryParseXmlToolCalls", types, [noName, tools]);
		ctx.AssertNull(noNameResult, "XmlToolCalls: missing name returns null");

		// Generated IDs start with xmltc_ prefix.
		ctx.Assert(valid != null && valid[0].Id.StartsWith("xmltc_"), "XmlToolCalls: generated ID has xmltc_ prefix");
	}

	private static void TestIsRateLimited(TestContext ctx, LlmService service)
	{
		Type[] types = [typeof(HttpResponseMessage), typeof(string)];

		// 429 status code.
		HttpResponseMessage r429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
		bool is429 = (bool)Reflect.Instance(service, "IsRateLimited", types, [r429, ""])!;
		ctx.Assert(is429, "IsRateLimited: 429 status detected");

		// Retry-After header present.
		HttpResponseMessage rRetry = new HttpResponseMessage(HttpStatusCode.OK);
		rRetry.Headers.TryAddWithoutValidation("Retry-After", "30");
		bool isRetry = (bool)Reflect.Instance(service, "IsRateLimited", types, [rRetry, ""])!;
		ctx.Assert(isRetry, "IsRateLimited: Retry-After header detected");

		// Normal 200 is not rate limited.
		HttpResponseMessage rOk = new HttpResponseMessage(HttpStatusCode.OK);
		bool isOk = (bool)Reflect.Instance(service, "IsRateLimited", types, [rOk, ""])!;
		ctx.Assert(!isOk, "IsRateLimited: normal 200 not rate limited");

		// Body containing code 429.
		HttpResponseMessage rBody = new HttpResponseMessage(HttpStatusCode.OK);
		bool isBody = (bool)Reflect.Instance(service, "IsRateLimited", types, [rBody, "{\"code\":429}"])!;
		ctx.Assert(isBody, "IsRateLimited: body code 429 detected");

		// X-RateLimit-Remaining = 0.
		HttpResponseMessage rRemaining = new HttpResponseMessage(HttpStatusCode.OK);
		rRemaining.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
		bool isRemaining = (bool)Reflect.Instance(service, "IsRateLimited", types, [rRemaining, ""])!;
		ctx.Assert(isRemaining, "IsRateLimited: X-RateLimit-Remaining 0 detected");
	}

	private static void TestParseRateLimitSecondsFromErrorBody(TestContext ctx, LlmService service)
	{
		Type[] types = [typeof(string)];

		// Valid nested error body with X-RateLimit-Reset.
		long futureEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
		string validBody = $"{{\"error\":{{\"metadata\":{{\"headers\":{{\"X-RateLimit-Reset\":\"{futureEpoch}\"}}}}}}}}";
		int validResult = (int)Reflect.Instance(service, "ParseRateLimitSecondsFromErrorBody", types, [validBody])!;
		ctx.Assert(validResult >= 59 && validResult <= 62, "ParseRateLimitSecondsFromErrorBody: valid body parsed");

		// Invalid JSON returns 0.
		int invalidResult = (int)Reflect.Instance(service, "ParseRateLimitSecondsFromErrorBody", types, ["not json at all"])!;
		ctx.AssertEqual(0, invalidResult, "ParseRateLimitSecondsFromErrorBody: invalid JSON returns 0");

		// Missing nested fields returns 0.
		int missingResult = (int)Reflect.Instance(service, "ParseRateLimitSecondsFromErrorBody", types, ["{\"error\":{}}"])!;
		ctx.AssertEqual(0, missingResult, "ParseRateLimitSecondsFromErrorBody: missing fields returns 0");
	}

	private static void TestTryAdaptToError(TestContext ctx)
	{
		// Fresh instance so _toolChoiceMode starts at Required.
		LLMConfig config = new LLMConfig
		{
			ApiKey = "test-key",
			Model = "test-model",
			Endpoint = "https://test.example.com/v1"
		};
		LlmService freshService = new LlmService(config);
		Type[] types = [typeof(HttpResponseMessage), typeof(string)];

		HttpResponseMessage r400 = new HttpResponseMessage(HttpStatusCode.BadRequest);
		string toolChoiceBody = "{\"error\": \"tool_choice is not supported\"}";

		// First call: Required → Auto, returns true.
		bool first = (bool)Reflect.Instance(freshService, "TryAdaptToError", types, [r400, toolChoiceBody])!;
		ctx.Assert(first, "TryAdaptToError: Required→Auto returns true");

		// Second call: Auto → Omit, returns true.
		bool second = (bool)Reflect.Instance(freshService, "TryAdaptToError", types, [r400, toolChoiceBody])!;
		ctx.Assert(second, "TryAdaptToError: Auto→Omit returns true");

		// Third call: already Omit, returns false.
		bool third = (bool)Reflect.Instance(freshService, "TryAdaptToError", types, [r400, toolChoiceBody])!;
		ctx.Assert(!third, "TryAdaptToError: Omit stays false");

		// 500 status is out of range.
		HttpResponseMessage r500 = new HttpResponseMessage(HttpStatusCode.InternalServerError);
		LlmService freshService2 = new LlmService(config);
		bool serverErr = (bool)Reflect.Instance(freshService2, "TryAdaptToError", types, [r500, toolChoiceBody])!;
		ctx.Assert(!serverErr, "TryAdaptToError: 500 status returns false");

		// 429 is explicitly excluded.
		HttpResponseMessage r429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
		bool rateLimit = (bool)Reflect.Instance(freshService2, "TryAdaptToError", types, [r429, toolChoiceBody])!;
		ctx.Assert(!rateLimit, "TryAdaptToError: 429 status returns false");

		// 400 without tool_choice in body returns false.
		bool noMatch = (bool)Reflect.Instance(freshService2, "TryAdaptToError", types, [r400, "{\"error\": \"something else\"}"])!;
		ctx.Assert(!noMatch, "TryAdaptToError: body without tool_choice returns false");
	}
}
