---
name: CSharp
description: Coding conventions and preferences when the programming language is C#
---
### PRIME DIRECTIVES
- Make minimal, surgical code changes to satisfy the request.
- NEVER remove existing comments. If they are incorrect, update them.

## EDITING RULES
- No optional or default parameters. Explicit parameters only. Avoid default parameter values and implicit defaults.
- No defensive over-engineering; trust other functions do what they are expected to.
- Keep diff readability: put blank lines around new logical blocks.
- Always modify files in place; do not delete and rewrite them.
- No LINQ.
- No var.
- Use foreach unless indexing is required.
- Use implicit tuples with named parameters instead of KeyValuePair.
- Use inline declaration in places where it makes sense such as: `dictionary.TryGetValue(currencyImmId, out CurrencyImmutable? currencyImm)`.
- Declare as nullable where appropriate and handle them properly.
- Use string interpolation `$"some {variable} here"` not string addition.
- Single return at the bottom of functions (structured if/else) where successes are the first branch of an if, failures are the elses.
- ANSI braces (opening brace on new line).
- Keep methods short and cohesive.
- If async method contains no awaits, convert to synchronous or return `Task.FromResult`.
- Narrow the visibility of members, using private unless needed to be protected or public. Use locals instead of members where appropriate.
- Do not introduce partial classes; they are messy.
- Follow the coding style present in the codebase already. Write one-line comments, never XML-style heavy comments.

## Project Guidelines
- User prefers ImplicitUsings disabled in .NET projects. They consider implicit usings a bad habit and prefer explicit using directives.
- User prefers minimal/simplified namespacing and does not like tons of separate namespaces in a project.
- User prefers to never use default parameters, var, Linq, or regex to change code. Always edit files directly and avoid using regex.
- User prefers not to add setters to classes; instead, mutate internally or create new immutable objects when lightweight.
- User emphasizes reducing error surfaces by writing explicit code with fewer side effects.

## RESPONSE FORMAT
- No fluff. Start with a concise “Change plan” if planning is non-trivial.
- Only describe code you changed.

## WHAT NOT TO DO
- No speculative refactors.
- No library migrations.
- No adding abstractions “for future use”.
- No TODOs or “might” language.
- No asking user to run commands.

## WHEN BLOCKED
If a required file or symbol is missing:
- Search again with alternate terms.
- If still missing, state: “Blocking: <file/symbol> not found after searches: [terms]. Need its path or creation approval.”
- Keep instructions/systemPrompt persisted through global state; update defaults only if user changes request.