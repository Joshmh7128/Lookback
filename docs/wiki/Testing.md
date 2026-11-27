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
| `RateTests.cs` | `Stars_skip_play_clear_and_quit` | The `rate` loop: digits store stars, Enter skips, `p` plays the candidate's window, `0` clears, anything else repeats the help, `q` stops |
| | `Already_rated_candidates_are_skipped_unless_again` | A second pass asks only about unrated candidates; `--again` asks about all of them and the last answer wins |
| | `Ephemeral_sessions_are_refused_and_nothing_is_written` | A rating of an official-player session is refused through `RatingStore` and the folder stays empty |
| | `Rates_candidates_from_a_manifest` | Generated footage with a flash: one candidate, rated 5, lands in `datasets/boxgame/ratings.jsonl` as `owned` with label 1 and `clip-features/1` features; the next pass has nothing left to ask |

## `Lookback.Scoring.Tests`

The learned scorer and ratings. Platform-neutral, so they run on Windows and Linux.

| File | Test | What it proves |
|---|---|---|
| `RatingStoreTests.cs` | `Owned_session_rating_is_appended_with_features_and_label` | A rating of owned footage is stored with its soft label (4 stars is 0.75), profile and features; NaN features become null |
| | `Line_matches_the_sample_the_ml_tests_read` | The exact line written equals `ml/tests/data/rating_sample.jsonl`, which the Python tests load, so the two sides agree on the format |
| | `Sessions_that_may_not_persist_are_refused_and_nothing_touches_disk` (7 cases) | Ephemeral (official player, file, live), an official player marked owned, unspecified retention, and third-party footage without a permission reference are all refused before any folder is created |
| | `Third_party_footage_with_a_permission_reference_is_stored` | Permissioned and open-licence footage with a reference is stored, reference included |
| | `Unknown_session_is_refused` | No session header, no write: retention is never guessed |
| | `Malformed_ratings_are_refused` | Stars above 5, missing ids, or a candidate that is not the rated one |
| | `Game_ids_that_are_not_safe_folder_names_are_refused` | `../escape`, spaces, drive paths and empty ids never become folders |
| | `Candidate_without_features_gets_the_floor_features` | Heuristic score, duration and peak position are filled from the candidate alone |
| | `Last_line_wins_and_zero_stars_clears` | Append-only history, current rating per candidate |
| | `Torn_last_line_is_skipped_on_read` | A half-written line is counted and skipped |
| | `Concurrent_appends_each_land_on_their_own_line` | 200 parallel appends give 200 whole lines |
| | `Rows_hold_numbers_and_ids_only` | The line's keys are exactly the documented ones |
| `RatingSinkTests.cs` | `Persists_a_rating_joined_to_its_candidate_and_session` | `session`, `clip.candidate` then `clip.rating` on the bus become one stored line with the candidate's scores |
| | `Official_player_session_ratings_are_refused_and_nothing_is_written` | The sink refuses and counts the refusal; the folder stays empty |
| | `Unknown_session_is_refused_unless_the_lookup_knows_it` | A rating for a session not seen on the bus is refused unless `SessionLookup` supplies its header |
| | `Rating_without_a_session_id_takes_the_envelopes` | An empty `session_id` falls back to the envelope's |
| | `Republished_candidate_replaces_the_remembered_one` | A candidate published again (say, with features) is the one joined |
| | `Forgets_the_oldest_candidates_past_the_limit` | Memory is bounded; a forgotten candidate's rating is stored without features |
| `LearnedClipScorerTests.cs` | `Inference_and_calibration_match_python` | The committed fixture model gives, through ONNX Runtime in C#, the raw and calibrated probabilities Python recorded in `expected.json` (within 1e-5), including imputed missing values and games the model does not cover |
| | `Calibration_lookup_is_deepest_profile_first` | Mode, then game; a game the model was not trained on has none |
| | `Platt_identity_and_clamping` | Identity calibration changes nothing; 0 and 1 are clamped |
| | `Heuristic_stands_without_a_model` | No model folder: the heuristic probability stays and a stale `model_score` is cleared |
| | `Broken_model_is_reported_and_skipped` | A corrupt `model.onnx` or a wrong format is reported and clipping carries on with the heuristic |
| | `Wrong_schema_keeps_the_heuristic_but_floor_features_score` | Features of another schema are not read; a bare candidate is scored from its floor features when the clock frequency is known |
| | `Qa_report_gets_the_highlight_probability` | `QaReport.highlight_probability` copies the scored value |
| | `Scoring_is_thread_safe` | 64 parallel scores agree |
| `ClipFeatureSchemaTests.cs` | `Matches_ml_feature_schema_json` | The C# feature list is exactly `ml/feature_schema.json` |
| | `Floor_features_come_from_the_candidate_alone`, `Attach_sets_the_schema_and_rejects_the_wrong_length` | The vector helpers |
| | `New_fields_survive_a_round_trip` | `ClipRating`, `ClipCandidate.features` and `QaReport.highlight_probability` serialise and parse |

## `ml/` (Python, CI job `ml`)

Run with `python -m pytest` in `ml/` (see [Clip Scoring](Clip-Scoring#running-it)).

| File | What it proves |
|---|---|
| `test_data.py` | Rows with retention other than owned, permissioned or open-license, or third-party rows without a `permissionRef`, or rows filed under another game's folder, stop training; last line wins and 0 clears; torn lines are skipped; missing features load as NaN; too few ratings, sessions or examples of each class are refused; the C#-written sample line loads |
| `test_metrics_and_calibration.py` | AUC (with ties), log loss, Brier, ECE and precision@k on hand-checked inputs; Platt recovers known parameters; few ratings stay near the parent; lookup is deepest profile first |
| `test_train.py` | On synthetic ratings the model beats the heuristic on held-out sessions and calibration lowers log loss and Brier; ONNX matches scikit-learn; games with too few ratings keep the heuristic; the CLI trains, and writes nothing when it refuses; the committed fixture still reproduces `expected.json`; the fixture regenerates |

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

## Testing approach

- **Deterministic time.** `ManualClock` and hand-set `qpc` values; no test
  depends on wall-clock timing for correctness.
- **Real transports.** Pipe and WebSocket tests use real pipes and a real
  Kestrel server on loopback, not mocks.
- **Generated media.** Footage tests build their own clip with ffmpeg, so no
  third-party media is ever checked in.
- **Failure paths first.** Torn frames, hostile lengths, crashes mid-write,
  slow consumers and rejected origins are each tested explicitly.
