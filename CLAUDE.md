# Lookback: notes for contributors and agents

- C# on .NET 10, `Lookback.slnx`. Build: `dotnet build Lookback.slnx`. Test: `dotnet test --solution Lookback.slnx` (Microsoft.Testing.Platform, xUnit v3).
- Warnings are errors; analyzers at `latest-recommended`. Library code uses `ConfigureAwait(false)`.
- Hot paths (capture, audio, signals): no per-frame managed allocations. Use `SpscRing<T>`, `ArrayPool`, `Span`.
- All timestamps are raw QPC ticks from `IClock`. Never mix in `DateTime` for event time.
- Anything that writes session data to disk must go through `RetentionRules`. Official-player sources are ephemeral; never add a path that persists them.
- Schema changes go in `proto/lookback/v1/events.proto`; never renumber or reuse field numbers.
- Do not add game-specific APIs or memory reading to the clipping path; signals stay game-agnostic.
- Original project: no other company's code, branding or assets.
