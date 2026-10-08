# C# coding standards

Target is C# 14 / .NET 10: reach for the newest construct that fits.

## Naming

- Private fields are camelCase with no prefix: `private string name;`. This is a hard rule; `_name` is never used.

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
- Catch specific exception types.
- Write object mappings by hand.

## Layout

- File-scoped namespaces; `using` directives outside the namespace, sorted.
- Braces on every block, including single-statement bodies.
- One statement and one declaration per line.
- `var` only when the right-hand side shows the type; spell out built-in types (`int count = 10;`).
- Call static members through the class name.

## Comments

- XML docs on public members.
- `// ` comments sit on their own line with a blank line above, never trailing code.
