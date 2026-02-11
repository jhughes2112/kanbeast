using System.Reflection;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Tests;

public static class WebToolsTests
{
	public static void Test(TestContext ctx)
	{
		Console.WriteLine("  WebToolsTests");

		TestStripHtmlTags(ctx);
		TestParseGoogleResults(ctx);
		TestFormatSearchResults(ctx);
	}

	private static void TestStripHtmlTags(TestContext ctx)
	{
		Type[] types = [typeof(string)];

		// Basic tag stripping.
		string basic = (string)Reflect.Static(typeof(WebTools), "StripHtmlTags", types, ["<p>Hello World</p>"])!;
		ctx.Assert(basic.Contains("Hello World"), "StripHtmlTags: basic tags stripped");
		ctx.Assert(!basic.Contains("<p>"), "StripHtmlTags: no tags remain");

		// Script tags removed entirely.
		string script = (string)Reflect.Static(typeof(WebTools), "StripHtmlTags", types, ["before<script>alert('xss')</script>after"])!;
		ctx.Assert(!script.Contains("alert"), "StripHtmlTags: script content removed");
		ctx.Assert(script.Contains("before"), "StripHtmlTags: text before script preserved");
		ctx.Assert(script.Contains("after"), "StripHtmlTags: text after script preserved");

		// Style tags removed entirely.
		string style = (string)Reflect.Static(typeof(WebTools), "StripHtmlTags", types, ["text<style>.x{color:red}</style>more"])!;
		ctx.Assert(!style.Contains("color"), "StripHtmlTags: style content removed");
		ctx.Assert(style.Contains("text"), "StripHtmlTags: text around style preserved");

		// HTML entity decoding.
		string entity = (string)Reflect.Static(typeof(WebTools), "StripHtmlTags", types, ["<span>&amp; &lt; &gt;</span>"])!;
		ctx.Assert(entity.Contains("&"), "StripHtmlTags: &amp; decoded");
		ctx.Assert(entity.Contains("<"), "StripHtmlTags: &lt; decoded");

		// Whitespace collapsing.
		string spaces = (string)Reflect.Static(typeof(WebTools), "StripHtmlTags", types, ["<div>  hello   world  </div>"])!;
		ctx.Assert(!spaces.Contains("  "), "StripHtmlTags: multiple spaces collapsed");

		// Empty input.
		string empty = (string)Reflect.Static(typeof(WebTools), "StripHtmlTags", types, [""])!;
		ctx.AssertEqual("", empty, "StripHtmlTags: empty input");

		// Nested tags.
		string nested = (string)Reflect.Static(typeof(WebTools), "StripHtmlTags", types, ["<div><p><b>deep</b></p></div>"])!;
		ctx.Assert(nested.Contains("deep"), "StripHtmlTags: nested tags stripped");
		ctx.Assert(!nested.Contains("<"), "StripHtmlTags: no angle brackets in output");
	}

	private static void TestParseGoogleResults(TestContext ctx)
	{
		Type[] parseTypes = [typeof(string), typeof(int)];

		// Valid JSON with items.
		string validJson = "{\"items\":[{\"title\":\"Test Page\",\"link\":\"https://example.com\",\"snippet\":\"A test snippet\"},{\"title\":\"Page Two\",\"link\":\"https://two.com\",\"snippet\":\"Second result\"}]}";
		object? parsed = Reflect.Static(typeof(WebTools), "ParseGoogleResults", parseTypes, [validJson, 10]);
		System.Collections.IList? resultList = parsed as System.Collections.IList;
		ctx.AssertNotNull(resultList, "ParseGoogleResults: valid JSON returns list");
		ctx.AssertEqual(2, resultList?.Count ?? -1, "ParseGoogleResults: two items parsed");

		// maxResults limits output.
		object? limited = Reflect.Static(typeof(WebTools), "ParseGoogleResults", parseTypes, [validJson, 1]);
		System.Collections.IList? limitedList = limited as System.Collections.IList;
		ctx.AssertEqual(1, limitedList?.Count ?? -1, "ParseGoogleResults: maxResults respected");

		// Missing items key returns empty.
		object? noItems = Reflect.Static(typeof(WebTools), "ParseGoogleResults", parseTypes, ["{\"searchInformation\":{}}", 10]);
		System.Collections.IList? noItemsList = noItems as System.Collections.IList;
		ctx.AssertEqual(0, noItemsList?.Count ?? -1, "ParseGoogleResults: no items key returns empty");

		// Empty items array.
		object? emptyItems = Reflect.Static(typeof(WebTools), "ParseGoogleResults", parseTypes, ["{\"items\":[]}", 10]);
		System.Collections.IList? emptyList = emptyItems as System.Collections.IList;
		ctx.AssertEqual(0, emptyList?.Count ?? -1, "ParseGoogleResults: empty items returns empty");

		// Items missing title or link are skipped.
		string partialJson = "{\"items\":[{\"title\":\"\",\"link\":\"https://a.com\",\"snippet\":\"x\"},{\"title\":\"Valid\",\"link\":\"https://b.com\",\"snippet\":\"y\"}]}";
		object? partial = Reflect.Static(typeof(WebTools), "ParseGoogleResults", parseTypes, [partialJson, 10]);
		System.Collections.IList? partialList = partial as System.Collections.IList;
		ctx.AssertEqual(1, partialList?.Count ?? -1, "ParseGoogleResults: items without title skipped");
	}

	private static void TestFormatSearchResults(TestContext ctx)
	{
		// Get the private SearchResult type to build the correct List<T> type for reflection.
		Type searchResultType = typeof(WebTools).GetNestedType("SearchResult", BindingFlags.NonPublic)!;
		Type listType = typeof(List<>).MakeGenericType(searchResultType);
		Type[] formatTypes = [listType];

		// Parse real results then format them (integration test through both methods).
		string json = "{\"items\":[{\"title\":\"Example\",\"link\":\"https://example.com\",\"snippet\":\"A snippet\"}]}";
		object? parsed = Reflect.Static(typeof(WebTools), "ParseGoogleResults", [typeof(string), typeof(int)], [json, 10]);

		string? formatted = (string?)Reflect.Static(typeof(WebTools), "FormatSearchResults", formatTypes, [parsed!]);
		ctx.AssertNotNull(formatted, "FormatSearchResults: returns string");
		ctx.Assert(formatted!.Contains("Example"), "FormatSearchResults: contains title");
		ctx.Assert(formatted.Contains("https://example.com"), "FormatSearchResults: contains URL");
		ctx.Assert(formatted.Contains("A snippet"), "FormatSearchResults: contains snippet");
		ctx.Assert(formatted.Contains("1."), "FormatSearchResults: contains numbering");

		// Empty list.
		object emptyList = Activator.CreateInstance(listType)!;
		string? emptyFormatted = (string?)Reflect.Static(typeof(WebTools), "FormatSearchResults", formatTypes, [emptyList]);
		ctx.Assert(emptyFormatted != null && emptyFormatted.Contains("No search results"), "FormatSearchResults: empty list message");
	}
}
