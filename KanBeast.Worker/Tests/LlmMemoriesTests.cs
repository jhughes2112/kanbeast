using KanBeast.Shared;
using KanBeast.Worker.Services;

namespace KanBeast.Worker.Tests;

public static class LlmMemoriesTests
{
	public static void Test(TestContext ctx)
	{
		Console.WriteLine("  LlmMemoriesTests");

		TestAddAndFormat(ctx);
		TestRemove(ctx);
		TestGetCommonPrefixLength(ctx);
	}

	private static void TestAddAndFormat(TestContext ctx)
	{
		ConversationMemories memories = new ConversationMemories();

		// Empty format.
		string emptyFormat = memories.Format();
		ctx.Assert(emptyFormat.Contains("None yet"), "Memories: empty format contains 'None yet'");

		// Add items across labels.
		memories.Add("INVARIANT", "The sky is blue");
		memories.Add("DECISION", "Use JSON format");
		string formatted = memories.Format();
		ctx.Assert(formatted.Contains("INVARIANT"), "Memories: format contains INVARIANT label");
		ctx.Assert(formatted.Contains("The sky is blue"), "Memories: format contains memory text");
		ctx.Assert(formatted.Contains("DECISION"), "Memories: format contains DECISION label");

		// Duplicate is ignored by HashSet.
		memories.Add("INVARIANT", "The sky is blue");
		ctx.AssertEqual(1, memories.Backing["INVARIANT"].Count, "Memories: duplicate ignored");

		// Blank inputs ignored.
		memories.Add("", "something");
		memories.Add("LABEL", "");
		memories.Add("  ", "  ");
		ctx.Assert(!memories.Backing.ContainsKey(""), "Memories: blank label ignored");
		ctx.Assert(!memories.Backing.ContainsKey("LABEL"), "Memories: blank memory ignored");
	}

	private static void TestRemove(TestContext ctx)
	{
		ConversationMemories memories = new ConversationMemories();
		memories.Add("CONSTRAINT", "Always validate inputs before processing");

		// Prefix match removal.
		bool removed = memories.Remove("CONSTRAINT", "Always validate inputs");
		ctx.Assert(removed, "Memories: prefix match removes entry");
		ctx.Assert(!memories.Backing.ContainsKey("CONSTRAINT"), "Memories: empty label removed from dictionary");

		// Remove from non-existent label.
		bool removedMissing = memories.Remove("MISSING", "something long enough");
		ctx.Assert(!removedMissing, "Memories: remove from missing label returns false");

		// Short search text rejected.
		memories.Add("REFERENCE", "Some reference data here");
		bool removedShort = memories.Remove("REFERENCE", "ab");
		ctx.Assert(!removedShort, "Memories: short search text rejected");
	}

	private static void TestGetCommonPrefixLength(TestContext ctx)
	{
		Type[] types = [typeof(string), typeof(string)];

		int partial = (int)Reflect.Static(typeof(ConversationMemories), "GetCommonPrefixLength", types, ["hello world", "hello there"])!;
		ctx.AssertEqual(6, partial, "GetCommonPrefixLength: partial match");

		int none = (int)Reflect.Static(typeof(ConversationMemories), "GetCommonPrefixLength", types, ["abc", "xyz"])!;
		ctx.AssertEqual(0, none, "GetCommonPrefixLength: no match");

		int emptyStr = (int)Reflect.Static(typeof(ConversationMemories), "GetCommonPrefixLength", types, ["", "hello"])!;
		ctx.AssertEqual(0, emptyStr, "GetCommonPrefixLength: empty string");

		int identical = (int)Reflect.Static(typeof(ConversationMemories), "GetCommonPrefixLength", types, ["same", "same"])!;
		ctx.AssertEqual(4, identical, "GetCommonPrefixLength: identical strings");
	}
}
