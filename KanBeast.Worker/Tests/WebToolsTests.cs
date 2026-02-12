using System.Reflection;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Tests;

public static class WebToolsTests
{
	public static void Test(TestContext ctx)
	{
		Console.WriteLine("  WebToolsTests");

		TestStripHtmlTags(ctx);
		TestParseDuckDuckGoResults(ctx);
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

	private static void TestParseDuckDuckGoResults(TestContext ctx)
	{
		Type[] parseTypes = [typeof(string), typeof(int)];

		// Build a minimal DDG HTML snippet with result__a and result__snippet classes.
		string html = "<a class=\"result__a\" href=\"https://example.com\">Example Title</a><a class=\"result__snippet\">A snippet</a>"
			+ "<a class=\"result__a\" href=\"https://two.com\">Page Two</a><a class=\"result__snippet\">Second</a>";

		object? parsed = Reflect.Static(typeof(WebTools), "ParseDuckDuckGoResults", parseTypes, [html, 10]);
		System.Collections.IList? resultList = parsed as System.Collections.IList;
		ctx.AssertNotNull(resultList, "ParseDuckDuckGoResults: valid HTML returns list");
		ctx.AssertEqual(2, resultList?.Count ?? -1, "ParseDuckDuckGoResults: two items parsed");

		// maxResults limits output.
		object? limited = Reflect.Static(typeof(WebTools), "ParseDuckDuckGoResults", parseTypes, [html, 1]);
		System.Collections.IList? limitedList = limited as System.Collections.IList;
		ctx.AssertEqual(1, limitedList?.Count ?? -1, "ParseDuckDuckGoResults: maxResults respected");

		// No matching elements returns empty.
		object? noResults = Reflect.Static(typeof(WebTools), "ParseDuckDuckGoResults", parseTypes, ["<div>no results here</div>", 10]);
		System.Collections.IList? emptyList = noResults as System.Collections.IList;
		ctx.AssertEqual(0, emptyList?.Count ?? -1, "ParseDuckDuckGoResults: no matches returns empty");
	}

	private static void TestFormatSearchResults(TestContext ctx)
	{
		// Get the private SearchResult type to build the correct List<T> type for reflection.
		Type searchResultType = typeof(WebTools).GetNestedType("SearchResult", BindingFlags.NonPublic)!;
		Type listType = typeof(List<>).MakeGenericType(searchResultType);
		Type[] formatTypes = [listType];

		// Build a result via ParseDuckDuckGoResults then format it.
		string html = "<a class=\"result__a\" href=\"https://example.com\">Example</a><a class=\"result__snippet\">A snippet</a>";
		object? parsed = Reflect.Static(typeof(WebTools), "ParseDuckDuckGoResults", [typeof(string), typeof(int)], [html, 10]);

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
