---
name: Testing
description: User expectations for how to perform testing.
---

## Overview
Expectations for good testing.

## All core classes should be testable.
- Use dependency injection for external dependencies to verify logic where required.
- Tests should throw an exception if axiomatic specifications are not met.
- Known error cases should be verified to be errors.
- No side effects, state leakage, or logging should occur unless a problem is detected and the program should halt.
- Always write tests in a separate class and separate file.  For example, if a class is named DoesFoo, DoesFoo.cs should be its filename, DoesFoo_Test should be the test class and DoesFoo_Test.cs should be the test file.
- There should be one public static function called Test, and it should manage the testing via private static functions.
- No extremely slow or expensive "rigorous" tests.  These should be verifying every if and else of the code logic, but not exercising big-O notation types of tests.

## Rules
- Tests should be written to verify as much of the class as possible.
- Attempt to reach 100% code coverage for testing.
- Leave a comment in the test class describing the functions that are NOT tested in the class being instrumented.
- The test harness should be a single function in Program that calls Program_Test.Test which then calls the test functions on each of the classes in the codebase.  This should run every time the program starts.