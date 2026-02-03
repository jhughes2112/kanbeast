---
name: run-tests
description: This is how we structure testing to determine if a feature is working or not.
dependencies: dotnet
---

## Overview

This skill defines a single testing system with one authoritative entrypoint and two execution contexts:

* automatic execution at program startup
* external execution via dotnet test for agents, CI, and diagnostics

All tests ultimately run through the same code path and produce a binary result: success or failure.

## Single Test Entrypoint

There is exactly one public test entrypoint:

Program_Test.Test

This function orchestrates the entire test suite and is the only place tests are invoked.

It is called from:

* Program.Main, unconditionally on startup
* a dotnet test adapter that invokes the same function

There must be no other test execution paths.

## Startup Execution

Program.Main calls Program_Test.Test before any application logic.

If any test throws:

* startup halts immediately
* process exits non zero
* no application logic runs

A clean startup implies all tests passed.

## External Execution via dotnet test

dotnet test must invoke Program_Test.Test through a minimal adapter test project.

dotnet test success implies startup tests will pass.

dotnet test failure blocks merge regardless of whether startup execution was attempted.

## When to Use External Test Runs

* verifying implementation work is complete
* running tests after rebasing
* diagnosing failures
* checking for regressions

## Commands

### Run All Tests

Runs the full suite through the single entrypoint.

```bash
dotnet test --verbosity normal
```

### Run Specific Test Project

```bash
dotnet test <path/to/TestProject.csproj> --verbosity normal
```

### Filtered Runs for Diagnosis Only

Filtered runs are allowed only for debugging and do not count as validation.

By test class:

```bash
dotnet test --filter "FullyQualifiedName~TestClassName"
```

By test method:

```bash
dotnet test --filter "FullyQualifiedName~TestClassName.TestMethodName"
```

By category:

```bash
dotnet test --filter "Category=Unit"
```

### Detailed Output

```bash
dotnet test --verbosity detailed --logger "console;verbosity=detailed"
```

## Test Structure

* One test class per production class
* Separate file, _Test suffix
* One public static Test method
* Private static methods contain assertions
* Tests throw on failure
* No logging, retries, or recovery

Untested functions must be listed in a comment with justification.

## Output Interpretation

The agent reduces all output to a single bit:

* pass if zero failures
* fail if any failure

Typical patterns:

Success:

```
Passed!  - Failed: 0
```

Failure:

```
Failed!  - Failed: 1
```

Failure details are used only to locate and fix the defect.

## Common Issues

Tests will not run:

* ensure solution builds
* verify test project references
* confirm test framework packages

Flaky tests:

* isolate with filters
* remove shared state or timing dependencies
* treat as defects

Slow or hanging tests:

```bash
dotnet test --blame-hang-timeout 60s
```

## Rules

Exactly one test entrypoint.
Tests always run on startup.
dotnet test invokes the same entrypoint.
Full suite required before merge.
Filtered runs never count as validation.
Any failure aborts execution and blocks merge.
