# C# coding standards

Target is C# 14 / .NET 10: reach for the newest construct that fits.

## Naming

- Private fields are camelCase with no prefix: `private string name;`. This is a hard rule; `_name` is never used.
- Names reveal intent, are pronounceable and searchable; single letters only in tiny scopes.
- Names tell the truth: `accountList` only if it is a `List`.
- Classes are nouns, methods are verbs. One word per concept: pick one of get/fetch/retrieve and keep it.

## Functions

- Small, doing one thing at one level of abstraction.
- Split a boolean flag parameter into two functions.
- Command/query separation: a function either acts or answers, and its name declares every side effect.
- Signal failures with exceptions, with the try/catch in its own function, and catch specific exception types. Expected business outcomes are return values.

## Structure

- Wrap third-party APIs at the edges of the system.
- DRY: each piece of knowledge has one authoritative home.
- Write object mappings by hand.
- Boy Scout rule: leave touched code cleaner than you found it.

## Language features

- Primary constructors for DI and simple types; records for DTOs and commands.
- `field` keyword for property logic instead of a hand-written backing field.
- Collection expressions: `int[] all = [..a, ..b];`.
- `System.Threading.Lock` for lock objects.
- Null-conditional assignment: `customer?.Order = order;`.
- `extension(...)` blocks for extension members.
- `using Point = (int X, int Y);` aliases for complex types.
- Raw string literals over escape sequences.
- `Span<T>`/`ReadOnlySpan<T>` and `params ReadOnlySpan<T>` in hot paths.
- Pass `CancellationToken` through every async call.

## Layout

- File-scoped namespaces; `using` directives outside the namespace, sorted.
- Braces on every block, including single-statement bodies.
- One statement and one declaration per line.
- `var` only when the right-hand side shows the type; spell out built-in types (`int count = 10;`).
- Call static members through the class name.

## Comments

- Express intent in code first. Comment only intent, warnings, TODOs, and public API (XML docs on public members).
- `// ` comments sit on their own line with a blank line above, never trailing code.
- Delete dead code rather than commenting it out.
