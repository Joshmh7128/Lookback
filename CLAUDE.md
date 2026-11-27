# Lookback: notes for contributors and agents

- C# on .NET 10, `Lookback.slnx`. Build: `dotnet build Lookback.slnx`. Test: `dotnet test --solution Lookback.slnx` (Microsoft.Testing.Platform, xUnit v3).
- Warnings are errors; analyzers at `latest-recommended`. Library code uses `ConfigureAwait(false)`.
- Hot paths (capture, audio, signals): no per-frame managed allocations. Use `SpscRing<T>`, `ArrayPool`, `Span`.
- All timestamps are raw QPC ticks from `IClock`. Never mix in `DateTime` for event time.
- Anything that writes session data to disk must go through `RetentionRules`. Official-player sources are ephemeral; never add a path that persists them. The one derived write is profile numbers (region rectangles, rule thresholds) the user confirms, gated by `RetentionRules.ValidateDerived` and written only by `CalibrationWriter`; never widen it to frames, audio, signal series, times or video ids.
- Schema changes go in `proto/lookback/v1/events.proto`; never renumber or reuse field numbers.
- Do not add game-specific APIs or memory reading to the clipping path; signals stay game-agnostic.
- Game knowledge lives in `profiles/*.json` as data (HUD regions plus rules over existing signals). A profile never adds a new kind of measurement; if a game needs one, add a game-agnostic signal instead. Profiles for anti-cheat titles keep `allowPresentHook` and `allowMemoryReading` false.
- Profiles form a tree (game, sub-game, mode) in `profiles/<game>/`. A child inherits and adds; it may only make the capture policy stricter. Sub-game detection reads only what the platform exposes outside the game process (launch arguments, window title, the launcher's own log) or the screen.
- Dataset media is never committed: only `datasets/<game>/manifest*.json`, with provenance and a permission reference for third-party footage.
- Original project: no other company's code, branding or assets.
