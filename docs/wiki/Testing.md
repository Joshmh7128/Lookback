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
| `BridgeControlTests.cs` | `A_reply_goes_to_the_sender_only`, `Replies_and_streamed_events_never_interleave`, `Oversized_messages_are_dropped_and_the_socket_carries_on`, `Without_a_handler_inbound_messages_are_ignored` | The bridge's control hook: per-client replies, whole frames under concurrent event streaming, a size cap, and the old read-only behaviour without a handler |

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

## `Lookback.Clipper.Tests`

Synthetic signal frames with known answers (`Synth.cs`): 30 fps video and
10 ms audio hops over a quiet, slightly noisy baseline, with flashes and
loud bursts where a test puts them. No ffmpeg needed.

| File | Test | What it proves |
|---|---|---|
| `EngineTests.cs` | `AFlashWithABurstIsOneCandidateWithThePeakWindow` | One candidate, peak at the spike, window exactly 6 s before and 3 s after, score in 0..1, reasons naming the components that drove it, QA passed with all four checks |
| | `QuietFootageAndTheWarmUpProduceNothing` | No candidates from quiet footage, nor from a spike inside the 5 s warm-up |
| | `OverlappingPeaksMergeAndDistantOnesDoNot` | Peaks 4 s apart merge into one candidate (`excitement ×2`); 1.5 s apart they fold into one peak; 20 s apart they are two candidates in time order |
| | `BlackOrSilentStretchesFailQa` | A mostly black window fails `black`; audio silent apart from the burst is not "silent throughout" |
| | `FrozenFramesFailQa` | Frames static for over 0.5 s fail `frozen`, with the measured share as its value |
| | `ShortWindowsFailTheLengthGate` | A window under the minimum length fails `length` |
| | `EventAndCurveWindowsDedupeIntoOneCandidate` | Two events and a curve peak at the same moment are one candidate whose reasons list both sources, window covering all three, peak and profile from the strongest; a distant event is its own candidate with score 1 − e^(−weight × confidence) |
| | `FollowingAProfileWaitsForItsEventsSoArrivalTimeDoesNotMatter` | An event delivered up front and the same event delivered half a second of footage late give identical candidates |
| | `WeightsAndThresholdsFollowTheActiveProfile` | A mode that raises `high` out of reach silences the curve from the moment it becomes active; a weight of 0 removes a component |
| | `ProfilesCanAddComponentsFromExistingSignals` | A profile can weigh a region signal; it becomes a component and a curve lane |
| | `EachCandidateCarriesItsFeaturesByName` | Every candidate's features follow the engine's name list and add up: sources, event counts and confidences, curve summary over the window, the clamped z of a flash, the QA black fraction |
| | `SteadyFramesAllocateNothing` | Thousands of frames after warm-up allocate zero bytes |
| `PartsTests.cs` | `SelectMatchesSortingOnRandomInput` | Quickselect and the median agree with sorting on 200 random arrays |
| | `NormaliserUsesMedianAndMadAfterWarmUp` | Warm-up, sampling interval, median and MAD with an outlier, the spread floor, and the window rolling |
| | `PeaksUseHysteresisAndTheRefractoryPeriod` | Dips between `low` and `high` do not end a peak; its time is the maximum; it waits out the refractory period; two crossings 600 ms apart fold into one with a window covering both |
| | `FuserMergesTouchingWindowsUpToTheMaximumLength` | Windows within the gap merge, distant ones do not, the maximum length is respected, a bridging window joins two groups, and a group closes only once the horizon passes it |
| | `FlagHistoryCountsAWindowAcrossTheRingWrap` | Black, frozen and silent counts are right for any window, including after the ring wraps |
| | `ProfileClipperBlocksInheritAndAreValidated` | A child's components replace the parent's by signal and inherit unset values; bad blocks and typos are rejected; `low ≥ high` is caught after inheritance; unknown signals are reported |
| | `DefaultsAreCompleteAndGameAgnostic` | The defaults set everything, use no region signals, and keep more pre-roll than post-roll |
| `RunnerTests.cs` | `FollowsTheProfileRunnerAndPublishesOneCandidateSetWithQa` | On a real bus beside a `ProfileRunner` with `publishCandidates: false`: one candidate from the clipper with both the event and the curve as reasons, stamped with its peak, followed by its QA report; the curve at 10 Hz on `signals.clipper`; another session's events ignored |
| | `TheHostCanAnnotateACandidateBeforeItIsPublished` (2 cases) | An annotator's model score, probability and QA line are on the published candidate and report; one that throws halfway leaves them exactly as the clipper made them, counted in `AnnotationFailures` |
| | `WithoutAProfileTheCurveAloneMakesCandidates` | The curve-only runner publishes a candidate and its QA report, and raises `CandidateFinished` with the same candidate and its features |

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
| `TestbenchRunnerTests.cs` | `OwnedFootageProducesLogAndReport` | Every frame and hop, both schemas (plus the clipper's curve schema) and the end marker reach the log; the report exists |
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
| `ClipperFootageTests.cs` | `AFlashWithALoudBurstIsOneCandidateWithTheRightWindow` | On a 20 s generated clip with one flash and noise burst: one candidate, window 6 s before and 3 s after the peak, QA passed; the report has the candidate with its QA and the excitement lane |
| | `OverlappingPeaksMergeIntoOneCandidate` | Two moments 4 s apart give one candidate covering both |
| | `ABlackStretchFailsQa` | A burst inside a black stretch: its candidate fails `black`, not `silence` |
| | `AFrozenStretchFailsQaButIsNotBlack` | A burst over a still grey frame: fails `frozen`, passes `black` |
| | `SilentAudioFailsQa` | A flash over digital silence: fails `silence` |
| | `ProfileEventsAndCurvePeaksDedupe` | With the `generic` profile, the `impact` event and the curve peak are one candidate with both reasons and the profile's id |
| `LiveTests.cs` | `RelayReleasesInFootageOrderAtFootageTime` | Out-of-order envelopes come out in QPC order, each at its footage time scaled by speed (manual clock) |
| | `LivePlaysTheClipIntoTheProfilerSocket` | `live` with the generic profile: a socket client with the page's origin gets `SessionStarted` first, four schemas (video, audio, rules, the clipper's curve), every frame and hop, and the end, paced over real time |
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
| | `A_check_rating_keeps_the_stars_and_numbers_and_nothing_of_the_video` | A clipper-check rating goes to `check-ratings.jsonl` (never `ratings.jsonl`) with the run id, times 0, the clipper's features mapped and audio features missing; the line equals `ml/tests/data/check_rating_sample.jsonl` |
| | `The_generic_curve_files_under_generic_and_a_clear_is_a_new_line` | No profile files under `generic`; 0 stars appends a clear |
| | `Check_ratings_that_could_carry_more_are_refused` (4 cases) | A run id that is not random hex (could carry a video id), an unknown platform, or stars above 5: refused, nothing created |
| | `Check_ratings_need_a_valid_official_player_session` | No session, owned footage, or an official player marked owned: refused; the official session's own `Append` is still refused |
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
| `ClipperFeatureMapTests.cs` | `Every_mapping_reads_a_documented_clipper_name_into_a_schema_feature` | Each mapped name is one Clipper v1 documents for `clipper-features/1`, and each target is in our schema |
| | `Full_clipper_features_fill_all_but_what_the_clipper_does_not_measure` | Direct copies, the two derived features, and exactly onset rate, scene cuts and coverage left NaN |
| | `Curve_only_candidate_has_zero_mean_contribution_and_missing_names_stay_nan` | No events gives 0 contribution and density; absent or infinite values stay missing |
| | `Mapped_features_score_through_the_fixture_model` | A mapped vector scores with the model |
| `ClipFeatureSchemaTests.cs` | `Matches_ml_feature_schema_json` | The C# feature list is exactly `ml/feature_schema.json` |
| | `Floor_features_come_from_the_candidate_alone`, `Attach_sets_the_schema_and_rejects_the_wrong_length` | The vector helpers |
| | `New_fields_survive_a_round_trip` | `ClipRating`, `ClipCandidate.features` and `QaReport.highlight_probability` serialise and parse |

## `ml/` (Python, CI job `ml`)

Run with `python -m pytest` in `ml/` (see [Clip Scoring](Clip-Scoring#running-it)).

| File | What it proves |
|---|---|
| `test_data.py` | Rows with retention other than owned, permissioned or open-license, or third-party rows without a `permissionRef`, or rows filed under another game's folder, stop training; last line wins and 0 clears; torn lines are skipped; missing features load as NaN; too few ratings, sessions or examples of each class are refused; the C#-written sample line loads; the C#-written check line loads beside your own and `include_check=False` leaves it out; check lines with a time, an unknown key, a non-random session id, another retention, no `derivedWrite` or an unknown platform stop training; a check line in `ratings.jsonl` stops training |
| `test_metrics_and_calibration.py` | AUC (with ties), log loss, Brier, ECE and precision@k on hand-checked inputs; Platt recovers known parameters; few ratings stay near the parent; lookup is deepest profile first |
| `test_train.py` | On synthetic ratings the model beats the heuristic on held-out sessions and calibration lowers log loss and Brier; ONNX matches scikit-learn; games with too few ratings keep the heuristic; the CLI trains, and writes nothing when it refuses; the committed fixture still reproduces `expected.json`; the fixture regenerates |

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

## `Lookback.Audio.Tests`

Windows-only tests skip elsewhere. The device tests also skip without a
default render or capture device, and CI runners have none, so on CI the AAC
tests run and the WASAPI ones skip. On AETHER everything runs, process loopback
included (build 19045), except the pre-19041 fallback test. See [Live Capture](Live-Capture#live-audio).

| File | Test | What it proves |
|---|---|---|
| `AacEncoderTests.cs` | `Sine_round_trips_through_ring_and_muxer` (128, 192 kbps) | Sine → `MfAacEncoder` → `ReplayRing` → `ClipWriter.Mux`: ffprobe reads `aac,48000,2`, the duration matches the input within a frame, ffmpeg decodes with no errors, and the left channel decodes back to 440 Hz at the input level |
| | `Decoded_onset_lands_at_its_capture_time` | A tone after 0.5 s of silence decodes at the sample its QPC says, within 1 ms (this pins `EncoderDelay`) |
| | `Late_input_is_preceded_by_silence_and_early_input_trimmed` | A 200 ms capture gap becomes 200 ms of silence, input that runs ahead is dropped, and the track stays the right length |
| | `Encoding_allocates_nothing_after_the_first_packet` | 0 bytes allocated per `Encode` call |
| `LoopbackTests.cs` | `Endpoint_loopback_hears_a_rendered_tone` | A quiet 997 Hz tone played through a render stream comes back through endpoint loopback at the right pitch; QPC stamps are current and monotonic; the clock fit is within 0.1% of nominal; hops cover the capture; 0 B per packet |
| | `Default_device_change_reopens_and_the_timeline_carries_on` | A default-device notification re-opens the stream; the gap is filled and hops stay continuous |
| | `Process_loopback_falls_back_only_where_allowed` | Before build 19041 a process-loopback request falls back to the endpoint and says why, or is refused when fallback isn't allowed (skips on 19041+) |
| | `Process_loopback_hears_this_process` | On 19041+ it hears this process's tone at its level; counted frames keep the clock fit (Windows 10 reports position 0); stamps current and monotonic; 0 B per packet |
| | `Process_loopback_hears_only_the_target_process` | A child powershell plays 600 Hz while this process plays 997 Hz: process loopback of the child hears the 600 Hz tone and not the 997 Hz one |
| | `Silent_process_loopback_falls_back_after_the_probe_only_where_allowed` (allowed, not allowed) | The target is silent while the endpoint plays: after the probe it switches to endpoint loopback (and hears the tone), or, with no fallback allowed (official players), stays on the process and hears nothing; `FallbackReason` and `Log` say why |
| | `Process_loopback_of_an_exited_process_never_hangs` | Start returns at once and the source ends up on the endpoint |
| | `Microphone_track_opens_and_streams` | The default mic streams at 48 kHz with current stamps and 0 B per packet |
| `ResamplerTests.cs` | 5 tests | A unity ratio is a two-frame delay; 44.1, 96 and 47.99 kHz resample to 48 kHz keeping pitch, level and length; surround downmix cannot clip and drops LFE; PCM16, PCM24 and float decode |
| `PolicyTests.cs` | 5 tests | Process loopback is tried from 19041; official player: never system loopback, encoder or mic, process loopback on Windows 10 with no fallback; a live game gets process loopback on Windows 10 with a fallback, and system loopback before 19041 (and says so); ephemeral is analysis only; invalid retention is refused |

## `Lookback.Calibration.Tests`: the clipper check

| File | Test | What it proves |
|---|---|---|
| `ClipperCheckTests.cs` | `Precision_recall_and_breakdowns_follow_the_judgments` | Precision, recall (marked moments found or missed), mean rating, per-reason and per-score breakdowns on hand-made candidates; duplicates ignored; QA failures shown |
| | `Tolerance_clearing_and_reset` | A mark just past a window still counts as covered; 0 clears a rating; reset forgets everything |
| | `The_clipper_runs_on_player_frames_and_its_candidates_are_judged_in_memory_only` | The real clipper on an official-player session's frames (video only) proposes candidates for flashes; judging and missed marks work through the panel's commands and show in its state; with no rating store nothing is written; stopping the player clears the check |
| | `With_a_rating_store_your_ratings_are_saved_as_numbers_only` | Judging through the panel saves one line: stars, the clipper's features, a random run id, no times and not the candidate id; the panel shows it saved; profiles untouched; a reset starts a new run |

## `Lookback.Recorder.Tests`

| File | Test | What it proves |
|---|---|---|
| `ControlServiceTests.cs` | `A_rating_of_an_owned_session_is_stored_and_published` | `rate` on a known candidate writes one line to `datasets/<game>/ratings.jsonl`, acks `stored`, and publishes `ClipRating` on the bus |
| | `Official_player_ratings_are_refused_and_nothing_is_written` | An ephemeral session's rating gets `ephemeral` and no file is created |
| | `Unknown_sessions_and_candidates_are_refused` | `unknown-session` and `unknown-candidate`, nothing written |
| | `Malformed_commands_get_bad_request` | Bad stars, missing fields, unknown types, non-JSON and non-objects all get `bad-request` |
| | `Save_is_refused_until_live_capture_lands` | `save` acks `unsupported` |
| | `Analyze_refuses_what_it_cannot_run` | Relative or missing paths, unknown profiles, permissioned footage without a reference, unknown retention |
| `CaptureServiceTests.cs` | `A_detected_game_is_recorded_a_candidate_is_saved_and_exit_ends_the_session` | Windows with a GPU: our stand-in game window, allowed through a temporary detection list, is detected and recorded; a candidate's window is saved and probes as a 3.5–7 s clip with video; storage status counts the clip and the footage; closing the window ends the session |
| | `Footage_is_analysed_into_a_file_session_whose_candidates_can_be_rated` | A generated clip becomes a FILE session titled with its exact path, with candidates and no `signals.*` envelopes on the bus; one of its candidates is then rated and stored |

## Testing approach

- **Deterministic time.** `ManualClock` and hand-set `qpc` values; no test
  depends on wall-clock timing for correctness.
- **Real transports.** Pipe and WebSocket tests use real pipes and a real
  Kestrel server on loopback, not mocks.
- **Generated media.** Footage tests build their own clip with ffmpeg, so no
  third-party media is ever checked in.
- **Failure paths first.** Torn frames, hostile lengths, crashes mid-write,
  slow consumers and rejected origins are each tested explicitly.
