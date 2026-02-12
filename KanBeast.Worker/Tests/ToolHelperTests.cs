using System.Text.Json.Nodes;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Tests;

public static class ToolHelperTests
{
	public static void Test(TestContext ctx)
	{
		Console.WriteLine("  ToolHelperTests");

		TestToSnakeCase(ctx);
		TestGetJsonType(ctx);
		TestGetDefaultValue(ctx);
		TestTruncateResponse(ctx);
		TestConvertJsonValue(ctx);
	}

	private static void TestToSnakeCase(TestContext ctx)
	{
		Type[] types = [typeof(string)];

		string pascal = (string)Reflect.Static(typeof(ToolHelper), "ToSnakeCase", types, ["HelloWorld"])!;
		ctx.AssertEqual("hello_world", pascal, "ToSnakeCase: PascalCase");

		string camel = (string)Reflect.Static(typeof(ToolHelper), "ToSnakeCase", types, ["readFile"])!;
		ctx.AssertEqual("read_file", camel, "ToSnakeCase: camelCase");

		string empty = (string)Reflect.Static(typeof(ToolHelper), "ToSnakeCase", types, [""])!;
		ctx.AssertEqual("", empty, "ToSnakeCase: empty string");

		string single = (string)Reflect.Static(typeof(ToolHelper), "ToSnakeCase", types, ["A"])!;
		ctx.AssertEqual("a", single, "ToSnakeCase: single char");

		string consecutive = (string)Reflect.Static(typeof(ToolHelper), "ToSnakeCase", types, ["ABCDef"])!;
		ctx.AssertEqual("a_b_c_def", consecutive, "ToSnakeCase: consecutive uppercase");
	}

	private static void TestGetJsonType(TestContext ctx)
	{
		Type[] types = [typeof(Type)];

		string strType = (string)Reflect.Static(typeof(ToolHelper), "GetJsonType", types, [typeof(string)])!;
		ctx.AssertEqual("string", strType, "GetJsonType: string");

		string intType = (string)Reflect.Static(typeof(ToolHelper), "GetJsonType", types, [typeof(int)])!;
		ctx.AssertEqual("integer", intType, "GetJsonType: int");

		string longType = (string)Reflect.Static(typeof(ToolHelper), "GetJsonType", types, [typeof(long)])!;
		ctx.AssertEqual("integer", longType, "GetJsonType: long");

		string boolType = (string)Reflect.Static(typeof(ToolHelper), "GetJsonType", types, [typeof(bool)])!;
		ctx.AssertEqual("boolean", boolType, "GetJsonType: bool");

		string doubleType = (string)Reflect.Static(typeof(ToolHelper), "GetJsonType", types, [typeof(double)])!;
		ctx.AssertEqual("number", doubleType, "GetJsonType: double");

		string arrayType = (string)Reflect.Static(typeof(ToolHelper), "GetJsonType", types, [typeof(string[])])!;
		ctx.AssertEqual("array", arrayType, "GetJsonType: array");
	}

	private static void TestGetDefaultValue(TestContext ctx)
	{
		Type[] types = [typeof(Type)];

		object? strDefault = Reflect.Static(typeof(ToolHelper), "GetDefaultValue", types, [typeof(string)]);
		ctx.AssertEqual(string.Empty, (string?)strDefault, "GetDefaultValue: string is empty");

		object? intDefault = Reflect.Static(typeof(ToolHelper), "GetDefaultValue", types, [typeof(int)]);
		ctx.AssertEqual(0, (int?)intDefault, "GetDefaultValue: int is 0");

		object? objDefault = Reflect.Static(typeof(ToolHelper), "GetDefaultValue", types, [typeof(object)]);
		ctx.AssertNull(objDefault, "GetDefaultValue: reference type is null");
	}

	private static void TestTruncateResponse(TestContext ctx)
	{
		Type[] types = [typeof(string)];

		string shortResult = (string)Reflect.Static(typeof(ToolHelper), "TruncateResponse", types, ["hello"])!;
		ctx.AssertEqual("hello", shortResult, "TruncateResponse: short string unchanged");

		string emptyResult = (string)Reflect.Static(typeof(ToolHelper), "TruncateResponse", types, [""])!;
		ctx.AssertEqual("", emptyResult, "TruncateResponse: empty unchanged");

		// MaxResponseLength is 160000, so use 200000 to trigger truncation
		string longInput = new string('x', 200000);
		string longResult = (string)Reflect.Static(typeof(ToolHelper), "TruncateResponse", types, [longInput])!;
		ctx.Assert(longResult.Length < 200000, "TruncateResponse: long string truncated");
		ctx.Assert(longResult.Contains("characters omitted"), "TruncateResponse: contains omission marker");
	}

	private static void TestConvertJsonValue(TestContext ctx)
	{
		Type[] types = [typeof(JsonNode), typeof(Type)];

		object? strResult = Reflect.Static(typeof(ToolHelper), "ConvertJsonValue", types, [JsonValue.Create("hello")!, typeof(string)]);
		ctx.AssertEqual("hello", (string?)strResult, "ConvertJsonValue: string");

		object? intResult = Reflect.Static(typeof(ToolHelper), "ConvertJsonValue", types, [JsonValue.Create(42)!, typeof(int)]);
		ctx.AssertEqual(42, (int?)intResult, "ConvertJsonValue: int");

		object? boolResult = Reflect.Static(typeof(ToolHelper), "ConvertJsonValue", types, [JsonValue.Create(true)!, typeof(bool)]);
		ctx.AssertEqual(true, (bool?)boolResult, "ConvertJsonValue: bool");

		object? doubleResult = Reflect.Static(typeof(ToolHelper), "ConvertJsonValue", types, [JsonValue.Create(3.14)!, typeof(double)]);
		ctx.AssertEqual(3.14, (double?)doubleResult, "ConvertJsonValue: double");
	}
}
