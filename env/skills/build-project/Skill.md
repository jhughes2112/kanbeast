---
name: Build Project
description: Build .NET solutions and projects with structured error parsing. Validates code compiles before proceeding.
dependencies: dotnet
---

## Overview
This skill provides standardized build execution and error parsing for .NET projects. Use it to verify code compiles and identify build errors.

## When to Use
- After making code changes
- Before running tests
- Before committing changes
- Diagnosing compilation errors

## Commands

### Build Entire Solution
```bash
dotnet build
```

### Build Specific Project
```bash
dotnet build <path/to/Project.csproj>
```

### Build with Detailed Output
```bash
dotnet build --verbosity detailed
```

### Clean Build
When incremental build has issues:
```bash
dotnet clean
dotnet build
```

### Release Build
```bash
dotnet build --configuration Release
```

## Output Parsing

### Success Pattern
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Error Pattern
```
error CS1002: ; expected
   path/to/File.cs(42,15): error CS1002: ; expected
```

Error format: `<file>(<line>,<column>): error <code>: <message>`

### Warning Pattern
```
warning CS0168: The variable 'x' is declared but never used
   path/to/File.cs(35,12): warning CS0168: ...
```

## Structured Result Format
After building, report results in this format:

### Success
```
## Build Result: ✅ Success

- **Configuration:** Debug
- **Warnings:** <N>
- **Errors:** 0

### Warnings (if any)
| File | Line | Warning |
|------|------|---------|
| `<file>` | <line> | <message> |
```

### Failure
```
## Build Result: ❌ Failed

- **Configuration:** Debug
- **Warnings:** <N>
- **Errors:** <N>

### Errors
| File | Line | Error |
|------|------|-------|
| `<file>` | <line> | `<code>`: <message> |

### Fix Suggestions
- <Error 1>: <Suggested fix>
- <Error 2>: <Suggested fix>
```

## Common Error Codes

| Code | Meaning | Common Fix |
|------|---------|------------|
| CS0103 | Name does not exist | Missing using statement or typo |
| CS0246 | Type not found | Missing reference or using statement |
| CS1002 | ; expected | Missing semicolon |
| CS1061 | Member not found | Method doesn't exist on type |
| CS0029 | Cannot convert type | Type mismatch, need cast or fix |
| CS0117 | Type does not contain definition | Wrong method name or missing method |
| CS0535 | Interface not implemented | Add missing interface members |

## Restore Dependencies
If build fails due to missing packages:
```bash
dotnet restore
dotnet build
```

## Rules
- Always build before running tests
- Fix all errors before proceeding (warnings can be deferred)
- Report exact error locations for quick fixes
- Use clean build if incremental build behaves unexpectedly
