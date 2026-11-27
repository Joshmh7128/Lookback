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
| `StallWatchdogTests.cs` | `ReportsABusyStepOnceItOutlivesTheThreshold`, `OneEpisodeIsReportedOnceAndANewEpisodeRearms`, `IdleThreadsAndShortStepsAreNeverReported`, `DisposedWatchesStopReporting`, `TheThreadedWatchdogFiresOnItsOwn` | The stall watchdog on a manual clock: reports once per stuck step, ignores idle threads and short steps |

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
See [Live Capture](Live-Capture#tests).

| File | Test | Proves |
|---|---|---|
| `DownscalerTests.cs` | `Sixteen_by_nine_fills_the_frame_in_bgra_order` | The WARP compute downscale keeps BGRA order and fills 384x216 from 16:9 |
| | `Four_by_three_is_pillarboxed_like_file_source` | 4:3 is pillarboxed with the same bars as FileSource |
| `EncoderTests.cs` | `Software_h264_on_warp_round_trips_through_the_muxer` | Software H.264 on WARP takes NV12 DXGI surface buffers; 120 frames through the ring and `ClipWriter` read back as 120 frames, keyframes at 0 and 2 s, no B-frames, no decode errors |
| | `Software_h264_on_the_gpu_reads_converter_output` | The same from `Nv12Converter` output on a real adapter |
| | `Hardware_encoder_round_trips_through_the_muxer` (H.264, HEVC) | Every display adapter's hardware MFT: all frames out, `Dispose` drains in under 3 s, same clip checks |
| `EncodeTickerTests.cs` | `Irregular_capture_becomes_constant_rate_packets` | Slow, bursty, then absent capture still gives one packet per tick at constant spacing, repeats counted, nothing skipped or busy |
| `EncodeTickerFaultTests.cs` | `A_hung_encoder_is_abandoned_and_the_next_one_takes_over_on_the_same_grid` | An encoder stuck inside a call is abandoned (never waited on), the next one continues on the original tick grid, and the stalled one is blocklisted |
| | `An_encoder_that_takes_frames_but_never_outputs_is_replaced` | Ticks due with no packets out count as a stall |
| | `An_encoder_that_refuses_all_input_is_replaced` | An encoder that is always busy counts as a stall |
| | `When_every_encoder_stalls_the_process_is_ended_instead_of_hanging` | An exhausted fallback chain calls Fatal with the reason |
| | `Too_many_swaps_in_one_session_end_the_process` | More than `MaxRecoveries` swaps calls Fatal |
| | `Dispose_returns_promptly_even_with_a_stuck_encoder` | Teardown never hangs on a stuck encoder |
| | `The_blocklist_survives_a_restart` | Stalled encoders are remembered per adapter and driver version, and a driver update gets a fresh chance |

## `Lookback.Detection.Tests`

Platform-neutral: a fake machine (`FakeProbe`) the test edits between polls,
and a manual clock. See [Game Detection](Game-Detection).

| File | Test | Proves |
|---|---|---|
| `MatcherTests.cs` | `A_listed_executable_is_its_game_ignoring_case` | `overwatch.EXE` is Overwatch, method EXECUTABLE, confidence 1 |
| | `Anti_cheat_policy_is_surfaced_unchanged` | Overwatch, VALORANT, Roblox and CS2 come with no present hook and no memory reading |
| | `A_profile_that_allows_more_keeps_its_policy_only_when_matched_by_its_own_executable` | An opt-in into a permissive profile still gets external capture only |
| | `An_unknown_executable_is_not_a_game_until_opted_in_and_then_gets_generic` | Unknown executables are not even looked up until the user opts them in; then `generic`, MANUAL |
| | `An_executable_two_profiles_list_is_ambiguous_until_opted_in` | Ambiguity is never guessed; an opt-in naming the profile settles it |
| | `Deny_wins_over_a_profile_and_over_allow` | A denied executable is never a game |
| | `A_path_entry_covers_only_that_copy` | Full-path entries match that file only |
| | `A_shared_runtime_needs_the_window_title` | `javaw.exe` is Minecraft only with a Minecraft window title |
| | `Launch_args_are_asked_for_only_where_a_profile_reads_them` | Roblox yes, Overwatch no |
| | `Settings_round_trip_and_missing_file_is_empty` | Save and load agree; a misspelt field is rejected |
| | `Engine_is_guessed_from_files_beside_the_executable` | Unity and Unreal layouts on disk |
| `LifecycleTests.cs` | `A_game_with_a_steady_window_is_announced_once_after_the_start_hold_then_focused` | DETECTED then FOCUSED once, after 3 s, with every field and the raw QPC of the confirming poll |
| | `A_process_without_a_window_or_with_a_short_splash_is_never_announced` | Launcher stubs and a 2 s splash publish nothing |
| | `An_alt_tab_shorter_than_the_focus_hold_does_not_flap_but_a_real_one_does` | Focus is debounced |
| | `Exit_is_announced_after_the_grace_with_the_last_state` | EXITED 5 s after the process ends, in the same session |
| | `A_relaunch_within_the_grace_continues_the_same_run_as_a_window_change` | A new pid of the same game continues the run: one WINDOW_CHANGED, no EXITED |
| | `A_new_window_monitor_or_mode_that_lasts_changes_the_target` | Splash to game, another monitor (new adapter LUID), and a mode flicker that is ignored |
| | `Title_changes_refresh_hints_without_publishing` | Roblox launch arguments are read once; a new title raises `HintsChanged` only |
| | `Only_candidate_processes_are_looked_up` | 50 unrelated processes open no handles |
| | `A_failing_probe_or_subscriber_does_not_stop_detection` | Errors land in `LastError` and the next poll works |

## `Lookback.Detection.Windows.Tests`

Windows only (they skip elsewhere). They start `lookback-testwindow.exe`, a
plain Win32 window built with the tests, and list it in a temporary profile.

| File | Test | Proves |
|---|---|---|
| `RealProcessTests.cs` | `The_probe_sees_the_process_its_path_launch_args_and_window` | Toolhelp, `QueryFullProcessImageName`, the command line and `EnumWindows` all see the child: pid, path, title, size, monitor, windowed |
| | `The_probe_answers_for_processes_it_cannot_open_without_throwing` | System and missing pids return nothing instead of failing |
| | `The_runner_announces_start_and_stop_of_a_real_process_with_its_capture_target` | DETECTED with the right window, launch args and policy, a `CaptureTarget` for that window on the same adapter, then EXITED in the same session |

## Testing approach

- **Deterministic time.** `ManualClock` and hand-set `qpc` values; no test
  depends on wall-clock timing for correctness.
- **Real transports.** Pipe and WebSocket tests use real pipes and a real
  Kestrel server on loopback, not mocks.
- **Generated media.** Footage tests build their own clip with ffmpeg, so no
  third-party media is ever checked in.
- **Failure paths first.** Torn frames, hostile lengths, crashes mid-write,
  slow consumers and rejected origins are each tested explicitly.
