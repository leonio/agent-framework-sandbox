# C# coding standards

## Language level
Target .NET 10 and C# 14. Prefer file-scoped namespaces, records for data,
primary constructors for services, collection expressions (`[]`) and pattern
matching (`is { Status: Open }`) over long `if` chains.

## Nullability
Nullable reference types are enabled and warnings are errors. Never use the
null-forgiving operator `!` to silence a warning without a comment saying why.

## Async
All I/O is async and accepts a `CancellationToken`. Never block with `.Result`
or `.Wait()`. Use `ValueTask` only for hot paths that usually complete
synchronously.

## Console apps
Keep `Program.cs` thin: parse arguments, build services, call one entry point.
Put logic in classes that do not touch `Console` so they can be unit tested.

## Errors
Validate input at the edge and return a friendly message plus a non-zero exit
code. Do not catch `Exception` except at the top level.

## Storage
For small apps a JSON file via `System.Text.Json` is fine. Use
`JsonSerializerDefaults.Web` and write atomically (temp file + move).
