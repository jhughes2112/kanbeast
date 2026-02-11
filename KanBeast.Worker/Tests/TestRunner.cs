using System.Reflection;

namespace KanBeast.Worker.Tests;

// Lightweight in-process test harness. Run with --test before any other initialization.
public class TestContext
{
	private int _passed;
	private int _failed;

	public int Passed => _passed;
	public int Failed => _failed;

	public void Assert(bool condition, string testName)
	{
		if (condition)
		{
			_passed++;
		}
		else
		{
			_failed++;
			Console.WriteLine($"  FAIL: {testName}");
		}
	}

	public void AssertEqual<T>(T expected, T actual, string testName)
	{
		bool equal = (expected == null && actual == null) || (expected != null && expected.Equals(actual));
		Assert(equal, $"{testName} — expected '{expected}' got '{actual}'");
	}

	public void AssertNull(object? value, string testName)
	{
		Assert(value == null, $"{testName} — expected null got '{value}'");
	}

	public void AssertNotNull(object? value, string testName)
	{
		Assert(value != null, $"{testName} — expected non-null");
	}
}

// Reflection helper to invoke private methods without modifying the target classes.
public static class Reflect
{
	public static object? Instance(object target, string methodName, Type[] parameterTypes, object[] arguments)
	{
		MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance, null, parameterTypes, null)
			?? throw new InvalidOperationException($"Instance method '{methodName}' not found on {target.GetType().Name}");
		return method.Invoke(target, arguments);
	}

	public static object? Static(Type type, string methodName, Type[] parameterTypes, object[] arguments)
	{
		MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static, null, parameterTypes, null)
			?? throw new InvalidOperationException($"Static method '{methodName}' not found on {type.Name}");
		return method.Invoke(null, arguments);
	}
}

public static class TestRunner
{
	public static int RunAll()
	{
		Console.WriteLine("=== Running Tests ===");
		TestContext ctx = new TestContext();

		LlmServiceTests.Test(ctx);
		ToolHelperTests.Test(ctx);
		LlmMemoriesTests.Test(ctx);
		FileToolsTests.Test(ctx);
		ShellToolsTests.Test(ctx);
		WebToolsTests.Test(ctx);

		Console.WriteLine($"=== Tests Complete: {ctx.Passed} passed, {ctx.Failed} failed ===");
		int exitCode = ctx.Failed > 0 ? 1 : 0;
		return exitCode;
	}
}
