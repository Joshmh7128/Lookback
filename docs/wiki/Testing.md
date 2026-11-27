# Testing

xUnit v3 on Microsoft.Testing.Platform. Run everything with
`dotnet test --solution Lookback.slnx`. CI runs the suite on Windows and Linux.

## `Lookback.Core.Tests`

| File | Test | What it proves |
|---|---|---|
| `TopicsTests.cs` | `Matches` (6 cases) | Exact, `*`, and prefix patterns; `signals.*` does not match `signals` or `signalsx.video` |
| `RetentionRulesTests.cs` | `OfficialPlayerMustBeEphemeral` | An official-player session marked `OWNED` is invalid and cannot persist |
| | `OfficialPlayerEphemeralIsValidButNotPersistable` | The allowed configuration is valid but still writes nothing |
| | `ThirdPartyFootageNeedsAPermissionReference` | `PERMISSIONED` and `OPEN_LICENSE` persist only with a `permission_ref` |
| | `UnspecifiedRetentionIsRejected` | Forgetting to set retention fails closed |
| | `OwnedLiveGameCanPersist` | The normal case works |
| `QpcTimeTests.cs` | `RoundTripsAtTypicalFrequency` | Ticks and `TimeSpan` convert both ways exactly |
| | `DoesNotOverflowAfterWeeksOfUptime` | 30 days at 3 GHz converts correctly |
| | `RescalesAudioSamplesToQpc` | 48 000 samples at 48 kHz is one second of QPC |
| | `ManualClockAdvances` | The replay clock moves only when told |
| `SpscRingTests.cs` | `RoundsCapacityUpAndRefusesWhenFull` | Capacity 5 becomes 8; a full ring refuses writes and accepts again after a read |
| | `PreservesOrderAcrossThreads` | Two million values cross threads in order with none lost or duplicated |
| `InProcessEventBusTests.cs` | `RoutesByTopicPattern` | Subscribers see only their topics, in order |
| | `SlowSubscriberDropsOldestWithoutBlockingPublisher` | Ten publishes into a queue of four: publish never fails, the newest four survive, `Dropped` is 6 |
| | `UnsubscribedReaderStopsReceiving` | Disposed subscriptions get nothing |
| | `DisposeCompletesReaders` | Shutdown lets readers drain and finish; later publishes return false |

## `Lookback.Stream.Tests`

| File | Test | What it proves |
|---|---|---|
| `FramingTests.cs` | `RoundTripsSeveralFrames` | Frames written back to back read back intact, then a clean end |
| | `TornFrameThrowsEndOfStream` | A frame cut short is reported, not misread |
| | `HostileLengthIsRejectedBeforeAllocating` | A 2 GB length prefix is refused |
| `PipeTransportTests.cs` | `ServerStreamsBusEventsToClient` | Bus events reach a pipe client, filtered by topic |
| | `ClientPublishesOntoBusWithoutEcho` | A client's event reaches the bus and other clients but not itself |
| `WebSocketBridgeTests.cs` | `StreamsJsonToAClientWithTheToken` | A client with the token receives proto3 JSON |
| | `RejectsMissingToken` | No token, no connection |
| | `RejectsForeignBrowserOrigin` | A page from another origin is refused even with the token |
| `SessionLogTests.cs` | `EphemeralSessionsNeverTouchDisk` | An official-player session creates no file at all |
| | `OfficialPlayerCannotBeMarkedOwned` | Mislabelling it fails and also creates nothing |
| | `PermissionedFootageWithoutReferenceIsRefused` | Third-party footage needs its permission reference |
| | `WritesReadsAndSeeksWithOutOfOrderRecords` | 1 000 records with neighbours swapped; seeking from seven different times returns exactly the records a full scan would, and the index really skips ahead |
| | `ToleratesTornTail` | A log cut mid-record by a crash reads up to the last complete record |
| | `RecorderCapturesOnlyItsSessionFromTheBus` | The bus-to-disk recorder keeps its own session's events, all of them, and nothing else |

## `Lookback.Signals.Tests`

Synthetic frames and audio with known answers (`Synth.cs`).

| File | Test | What it proves |
|---|---|---|
| `VideoSignalTests.cs` | `LumaUsesBt709Weights` | White, pure green and pure blue give BT.709 luma within one 8-bit step |
| | `BlackFrameIsFlagged` | Near-black is black; mid-grey is not |
| | `MotionMeasuresChangeAndCountsFrozenTime` | Noise frames give high energy and coverage; five identical frames at 10 fps give 0.5 s frozen |
| | `FlashFiresOnSuddenBrighteningOnly` | A jump from a steady baseline fires; darkening does not |
| | `RedEdgeDetectsDamageVignette` | A red border on a grey centre scores high; uniform grey scores zero |
| | `SceneCutSeparatesCutsFromSmallChanges` | A small patch scores low; a whole-picture change scores > 0.9 |
| | `HudMaskFindsStaticOverlayAndWatchesIt` | Over noisy "gameplay" the mask is exactly the static bar; noise outside is ignored, the bar dropping is not |
| | `HudIgnoresLetterboxBars` | Static black bars never become HUD |
| | `BankConcatenatesSignalsInOrder` | One vector per frame, unique names, stable order |
| | `BankDoesNotAllocatePerFrame` | 50 frames at 384×216 allocate zero bytes |
| `AudioSignalTests.cs` | `FftMatchesNaiveDft` | The FFT equals a direct DFT to three decimals |
| | `FftRejectsNonPowerOfTwo` | Invalid sizes fail fast |
| | `MelScaleRoundTrips` | Hz ↔ mel conversions are inverses; 1 kHz ≈ 1000 mel |
| | `LoudnessOfAKnownSine` | Amplitude 0.5 gives −6.02 dBFS peak and −9.03 dBFS RMS |
| | `SilenceAndClippingAreFlagged` | Zeros are silent; full scale is clipped |
| | `CentroidAndMelPeakFollowTheTone` | A 1 kHz tone puts the centroid and the loudest mel band near 1 kHz |
| | `OnsetFiresOnATransientNotOnASteadyTone` | 100 hops of steady tone give no onsets; a noise burst gives one |
| | `BankDoesNotAllocatePerHop` | 100 hops allocate zero bytes |

## `Lookback.Profiles.Tests`

| File | Test | What it proves |
|---|---|---|
| `MergerTests.cs` | `ReleasesInTimeOrderOnlyBelowTheWatermark` | Video and audio from two streams come out in time order, and nothing is released past the slower stream |
| | `TiesKeepArrivalOrder` | Equal timestamps keep the order they arrived in |
| | `StreamFarBehindInFootageTimeStillHoldsTheWatermark` | Audio five seconds of footage behind video still holds the watermark, so nothing is released out of order |
| | `SilentStreamStopsHoldingBackAfterTheWallClockTimeout` | A stream that never sends (no audio device) holds the others back only until the wall-clock timeout |
| | `DetectorOutputDoesNotDependOnArrivalSkew` | The same footage gives identical events whether audio arrives in step, one second late or five seconds late |
| `DetectorTests.cs` | `AboveFiresOnceThenRespectsCooldown` | A held condition fires once, then again only after the cooldown |
| | `BelowAndAllConditionsMustHoldTogether` | `when` needs every condition on the same frame |
| | `RiseComparesWithTheBaselineNotAnAbsoluteLevel` | A constant bright value never fires; a step up does, on the step |
| | `AnyNeedsOnlyOneOfItsConditions` | `whenAny` fires on any one condition |
| | `MissingSignalNeverHolds` | A signal nobody produces gives a −∞ margin, never an event |
| | `BoostsAddConfidenceFromEitherSideOfTheTrigger` | An audio onset 150 ms after the visual trigger adds confidence; one 500 ms away does not |
| | `ClipWindowSurroundsTheMoment` | The event's window is `[at − pre, at + post]` |
| | `CompositeFiresOncePerStreakAndCoversIt` | Two kills in a second make one `multi`, whose clip starts before the first kill |
| `LoaderTests.cs` | `BuiltInProfilesAreEmbedded` | `generic` and `overwatch` ship inside the assembly |
| | `BuiltInProfilesAreValidAndUseOnlyRealSignals` | Each built-in profile passes validation and every signal it names exists |
| | `OverwatchIsVisionAndAudioOnly` | No present hook, no memory reading, and marked uncalibrated |
| | `UnknownFieldsAreRejected` | A misspelt or unsupported field (such as `memoryOffsets`) is an error, not silently ignored |
| | `InvalidProfilesAreRejected` (5 cases) | Bad id, region off screen, ambiguous condition, rule with no conditions, composite of an unknown event |
| `RegionSignalTests.cs` | `MeasuresOnlyInsideTheRegion` | Luma, activity, saturation and red are measured inside each region and nowhere else |
| | `DoesNotAllocatePerFrame` | 100 frames allocate zero bytes |

## `Lookback.Testbench.Tests`

Uses a 3-second clip generated by ffmpeg's built-in test sources, with a
flash at 2.0 to 2.2 s and a space in its file name (`Fixture.cs`).

| File | Test | What it proves |
|---|---|---|
| `FileSourceTests.cs` | `ProbeReadsStreams` | ffprobe reports size, rate, duration and both streams |
| | `DecodesConstantRateFramesWithExactTimestamps` | Frames arrive at the analysis size, frame N stamped exactly N/fps |
| | `DecodesAudioInHops` | ~300 hops of 10 ms with increasing sample positions and real signal |
| | `StoppingEarlyDoesNotHang` | Abandoning the stream kills ffmpeg |
| | `MissingFileIsReported` | A clear error, not an ffmpeg exit code |
| `TestbenchRunnerTests.cs` | `OwnedFootageProducesLogAndReport` | Every frame and hop, both schemas and the end marker reach the log; the report exists |
| | `FlashInTheFootageShowsUpInTheTimeline` | The `flash.brighten` peak lands at 2.0 to 2.2 s |
| | `EphemeralWritesNothing` | It analyses, and the output folder is never created |
| | `PermissionedWithoutReferenceIsRefusedBeforeAnyWork` | Refused up front; nothing created |
| | `RelativeHrefIsEscaped` | The viewer's link to the footage survives spaces and `#` |
| | `SessionIdIsFileNameSafe` | Odd file names give safe session ids |
| `ReportWriterTests.cs` | `TitleCannotBreakOutOfThePage` | A `</script>` title cannot inject script |
| `ProfileTests.cs` | `GenericProfileFindsTheFlashAsAnImpact` | Exactly one `impact` at 2.0 s, with its evidence; events and rule lanes reach the report |
| | `OverwatchProfileAddsRegionSignalsAndCalibrationOverlay` | Region signals are produced, no missing-signal warnings, region boxes are in the page |
| | `PngHasValidSignatureChunksAndPixels` | The HUD-mask PNG has a valid signature and CRCs (checked by an independent CRC) and decodes to the input pixels |
| | `ScoringMatchesOneToOneWithinTolerance` | Two detections of one kill count as one hit and one false positive; per-entry scores add up |
| | `ManifestParsesAndRejectsEphemeral` | Comments and trailing commas are fine; a dataset entry cannot be ephemeral |
| | `DatasetRunScoresAgainstLabels` | A one-entry dataset labelled with the flash scores a true positive |

## Testing approach

- **Deterministic time.** `ManualClock` and hand-set `qpc` values; no test
  depends on wall-clock timing for correctness.
- **Real transports.** Pipe and WebSocket tests use real pipes and a real
  Kestrel server on loopback, not mocks.
- **Generated media.** Footage tests build their own clip with ffmpeg, so no
  third-party media is ever checked in.
- **Failure paths first.** Torn frames, hostile lengths, crashes mid-write,
  slow consumers and rejected origins are each tested explicitly.
