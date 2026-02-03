---
name: Run Tests
description: Execute .NET test suites with structured output parsing. Supports full suite, project-specific, and filtered test runs.
dependencies: dotnet
---

## Overview
This skill provides standardized test execution and result parsing for .NET projects. Use it to run tests and get structured pass/fail information.

## When to Use
- Verifying implementation work is complete
- Running tests after rebasing
- Diagnosing test failures
- Checking for regressions

## Commands

### Run All Tests
```bash
dotnet test --verbosity normal
```

### Run Specific Test Project
```bash
dotnet test <path/to/TestProject.csproj> --verbosity normal
```

### Run Filtered Tests
By test name:
```bash
dotnet test --filter "FullyQualifiedName~TestClassName"
```

By test method:
```bash
dotnet test --filter "FullyQualifiedName~TestClassName.TestMethodName"
```

By trait/category:
```bash
dotnet test --filter "Category=Unit"
```

### Run with Detailed Output
For debugging failures:
```bash
dotnet test --verbosity detailed --logger "console;verbosity=detailed"
```

## Output Parsing

### Success Output Pattern
```
Passed!  - Failed:     0, Passed:    42, Skipped:     0, Total:    42
```

### Failure Output Pattern
```
Failed!  - Failed:     2, Passed:    40, Skipped:     0, Total:    42
```

Look for failure details:
```
Failed TestClassName.TestMethodName [42 ms]
  Error Message:
   Assert.Equal() Failure
   Expected: 5
   Actual:   3
  Stack Trace:
   at TestClassName.TestMethodName() in /path/to/Test.cs:line 25
```

## Structured Result Format
After running tests, report results in this format:
```
## Test Results

### Summary
- **Status:** ✅ Passed | ❌ Failed
- **Total:** <N>
- **Passed:** <N>
- **Failed:** <N>
- **Skipped:** <N>
- **Duration:** <time>

### Failures (if any)
| Test | Error | Location |
|------|-------|----------|
| `<ClassName.Method>` | <Error message> | `<File>:line <N>` |

### Skipped (if any)
| Test | Reason |
|------|--------|
| `<ClassName.Method>` | <Skip reason> |
```

## Common Issues

### Tests Won't Run
1. Ensure solution builds: `dotnet build`
2. Check test project references
3. Verify test framework packages are installed

### Flaky Tests
If a test passes sometimes and fails others:
1. Run it in isolation: `--filter "FullyQualifiedName~TestName"`
2. Look for shared state, timing issues, or external dependencies
3. Report as a potential issue

### Slow Tests
Use the `--blame-hang-timeout 60s` flag to identify hanging tests:
```bash
dotnet test --blame-hang-timeout 60s
```

## Rules
- Always run a full test suite before merging (not just filtered tests)
- Report exact failure messages, not summaries
- If tests were skipped, note why
- Never mark tests as passing if any failed
