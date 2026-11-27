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
| `BridgeProfilerTests.cs` | `ServesAConfiguredPageWithLockedDownHeaders` | A configured page is served with CSP (`connect-src 'self'`, `frame-ancestors 'none'`) and `nosniff` |
| | `PageUrlCarriesTheTokenOnlyInTheFragment` | The printed link has no query string; the token is after `#` |
| | `ServesNoPagesByDefault` | The shipping bridge returns 404 for `/profiler` |
| | `OwnOriginIsAllowedOnlyWhenServingPages` | `http://127.0.0.1:port` and `localhost` connect only when pages are on; a foreign origin is still refused |
| | `ALateClientGetsTheSessionAndSchemasFirst` | A client connecting after the session started receives `SessionStarted` and the schema before anything else |
| | `SnapshotForgetsEndedSessionsAndKeepsTheLatestSchema` | A re-published schema replaces the old one; `SessionEnded` clears the session |
| | `SnapshotIsBoundedToTheNewestSessions` | Past 32 sessions the oldest are evicted |
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
| `HierarchyTests.cs` | `ChildInheritsAddsReplacesAndDisables` | A child inherits, adds, replaces and disables events; composites of disabled events go; the capture policy only gets stricter |
| | `InvalidTreesAreRejected` (4 cases) | Missing parent, id without the parent prefix, one region name with two rectangles, a mode under a mode |
| | `BuiltInTreeIsValid` | The shipped Roblox and Overwatch trees load, and no Roblox experience can read memory |
| | `HintDecidesTheSubgameOutright` | A place id in the client log picks the experience at once, with evidence |
| | `FingerprintSwitchesOnlyAfterItHoldsForTheHoldTime` | One stray frame changes nothing; a real change switches after the hold, straight from sibling to sibling |
| | `OnlyTheActiveProfilesRulesFire` | Rules of an inactive experience never fire; events carry the profile active when they fired |
| | `CandidatesMergeOverlappingEventsAndSplitDistantOnes` | Overlapping events become one candidate with summed score and the strongest event as peak |
| | `CandidatesNeverExceedTheMaximumLength` | A long run of events is split into candidates no longer than the maximum |
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
| `HierarchyDatasetTests.cs` | `LearnsFingerprintsThenDetectsSubgamesAndScoresCandidates` | On generated footage of two sub-games that differ only by a HUD box's colour: fingerprints are learned from the box region, both sub-games are then detected on their own, and the flash candidates match the labelled clips |
| | `CandidateScoringUsesTimeOverlap` | Intersection over union, one-to-one matching |
| | `ProfileSecondsCountsOnlyTheExpectedProfile` | Detection accuracy counts only time spent in the expected profile |
| `BrowserTests.cs` | `BuiltInProfilesOnlyReadSignalsThePipelineProduces` | Every signal a built-in profile reads is one the pipeline produces |
| | `ChildShowsWhatItInheritsAddsAndSwitchesOff` | A child profile's page attributes each event and region to the profile that defines it |
| | `ManifestsAreCheckedAndResultsJoinedToTheirEntries` | Manifest problems are flagged and `scores.json` results land on the right entries |
| | `ManifestTextCannotBreakOutOfThePage` | A `</script>` in a manifest cannot inject script |
| | `BrokenManifestIsReportedNotFatal` | A manifest that fails to parse is shown as a problem; the rest still loads |
| `ProfileTests.cs` | `GenericProfileFindsTheFlashAsAnImpact` | Exactly one `impact` at 2.0 s, with its evidence; events and rule lanes reach the report |
| | `OverwatchProfileAddsRegionSignalsAndCalibrationOverlay` | Region signals are produced, no missing-signal warnings, region boxes are in the page |
| | `PngHasValidSignatureChunksAndPixels` | The HUD-mask PNG has a valid signature and CRCs (checked by an independent CRC) and decodes to the input pixels |
| | `ScoringMatchesOneToOneWithinTolerance` | Two detections of one kill count as one hit and one false positive; per-entry scores add up |
| | `ManifestParsesAndRejectsEphemeral` | Comments and trailing commas are fine; a dataset entry cannot be ephemeral |
| | `DatasetRunScoresAgainstLabels` | A one-entry dataset labelled with the flash scores a true positive |
| `LiveTests.cs` | `RelayReleasesInFootageOrderAtFootageTime` | Out-of-order envelopes come out in QPC order, each at its footage time scaled by speed (manual clock) |
| | `LivePlaysTheClipIntoTheProfilerSocket` | `live` with the generic profile: a socket client with the page's origin gets `SessionStarted` first, three schemas (video, audio, rules), every frame and hop, and the end, paced over real time |
| | `LiveRefusesPermissionedFootageWithoutAReference` | Retention is checked before anything starts |

## `Lookback.Capture.Tests`

Windows only (they skip elsewhere). Encoder tests also need ffmpeg/ffprobe.
The hook tests build `native/` with CMake when its output is missing, and the
capture ones need a hardware GPU (they skip on CI runners; the `native` CI
job runs the rest). See [Live Capture](Live-Capture#tests) and
[Present Hook](Present-Hook#tests).

| File | Test | Proves |
|---|---|---|
| `DownscalerTests.cs` | `Sixteen_by_nine_fills_the_frame_in_bgra_order` | The WARP compute downscale keeps BGRA order and fills 384x216 from 16:9 |
| | `Four_by_three_is_pillarboxed_like_file_source` | 4:3 is pillarboxed with the same bars as FileSource |
| `EncoderTests.cs` | `Software_h264_on_warp_round_trips_through_the_muxer` | Software H.264 on WARP takes NV12 DXGI surface buffers; 120 frames through the ring and `ClipWriter` read back as 120 frames, keyframes at 0 and 2 s, no B-frames, no decode errors |
| | `Software_h264_on_the_gpu_reads_converter_output` | The same from `Nv12Converter` output on a real adapter |
| | `Hardware_encoder_round_trips_through_the_muxer` (H.264, HEVC) | Every display adapter's hardware MFT: all frames out, `Dispose` drains in under 3 s, same clip checks |
| `HookTests.cs` | `Layout_matches_the_native_header` | The C# protocol structs have the sizes and offsets the C++ header `static_assert`s |
| | `Notepad_is_refused_before_anything_starts` | The injector refuses `notepad.exe` ("not a Lookback test app") and starts nothing |
| | `A_process_the_injector_did_not_start_is_refused` | A real test app started by someone else, the test host and System are refused by `Attach` |
| | `A_capture_policy_without_allowPresentHook_is_refused` | `allowPresentHook` false refuses before launching |
| | `A_test_app_copied_out_of_the_build_output_is_refused` | The same binary outside `native/out/bin/Release` fails the path check |
| | `Only_the_test_apps_carry_the_marker` | The PE export reader finds `LookbackTestAppMarker` in both apps only |
| | `Test_app_frames_arrive_numbered_in_order_with_monotonic_qpc` (D3D11, D3D12 × Present, Present1) | ≥ 120 frames from each test app; monotonic QPC; every frame-number gap counted as a hook drop or receiver skip; pixels carry the same frame number as the metadata |
| | `Capture_survives_a_resize_and_a_recreated_device` (D3D11, D3D12) | Resize, then device recreation (a real `RemoveDevice` on D3D12): capture continues, textures reopen, accounting holds |
| | `Source_stops_when_the_hook_stops_producing` | A frozen test app makes the source raise `Stopped` after the stall timeout |
| | `Source_stops_when_the_test_app_exits` | The app exiting raises `Stopped` once |
| `EncodeTickerTests.cs` | `Irregular_capture_becomes_constant_rate_packets` | Slow, bursty, then absent capture still gives one packet per tick at constant spacing, repeats counted, nothing skipped or busy |

## Testing approach

- **Deterministic time.** `ManualClock` and hand-set `qpc` values; no test
  depends on wall-clock timing for correctness.
- **Real transports.** Pipe and WebSocket tests use real pipes and a real
  Kestrel server on loopback, not mocks.
- **Generated media.** Footage tests build their own clip with ffmpeg, so no
  third-party media is ever checked in.
- **Failure paths first.** Torn frames, hostile lengths, crashes mid-write,
  slow consumers and rejected origins are each tested explicitly.
