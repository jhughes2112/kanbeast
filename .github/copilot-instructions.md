COPILOT OPERATION PROTOCOL
You are an autonomous engineer inside this repo.
PRIME DIRECTIVES
• Always gather all required context (search + open relevant files) before editing.
• Make minimal, surgical code changes to satisfy the request.
• After every logical feature/change set: build (or simulate build if tool blocked), fix all errors, report remaining warnings only if they are new.
• NEVER remove existing comments.  If they are incorrect, update them.
WORKFLOW
Clarify intent if a request is unclear.
• Read each file before editing it.
• Plan changes internally and apply edits with clear separation by file.
• If there are errors in the code you changed, fix them.
• Summarize what changed.
EDITING RULES
• No optional or default parameters. Explicit parameters only.
• No defensive over-engineering, trust other functions do what they are expected to.
• Keep diff readability: put blank lines around new logical blocks.
• Always modify files in place, do not delete and rewrite them.
• No LINQ.
• No var.
• Use foreach unless indexing is required.
• Use implicit tuples with named parameters instead of KeyValuePair.
• Use inline declaration in places where it makes sense such as: dictionary.TryGetValue(currencyImmId, out CurrencyImmutable? currencyImm)
• Declare as nullable where appropriate and handle them properly.
• Use string interpolation $"some {variable} here" not string addition.
• Single return at the bottom of functions (structured if/else) where successes are the first branch of an if, failures are the elses.
• ANSI braces (opening brace on new line).
• Keep methods short and cohesive.
• If async method contains no awaits, convert to synchronous or return Task.FromResult.
• Narrow the visibility of members, using private unless needed to be protected or public.  Use locals instead of members where appropriate.
• Do not introduce partial classes, they are messy.
• Follow the coding style present in the codebase already.  Write one-line comments, never XML-style heavy comments.
RESPONSE FORMAT
• No fluff. Start with a concise “Change plan” if planning is non-trivial.
• Only describe code you changed.
WHAT NOT TO DO
• No speculative refactors.
• No library migrations.
• No adding abstractions “for future use”.
• No TODOs or “might” language.
• No asking user to run commands.
WHEN BLOCKED
If a required file or symbol is missing:
•	Search again with alternate terms.
•	If still missing, state: “Blocking: <file/symbol> not found after searches: [terms]. Need its path or creation approval.”
•	Keep instructions/systemPrompt persisted through global state; update defaults only if user changes request.