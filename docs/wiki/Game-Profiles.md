# Game profiles

A game profile is everything Lookback knows about one game, written as data.
It never adds a new kind of measurement. It points the game-agnostic signals
at the parts of the screen that matter in that game, and turns their output
into named moments with rules. Adding a game means writing a JSON file, not
code.

Profiles live in `profiles/<game>/*.json` (one folder per game tree) and
are embedded into `Lookback.Profiles` at build time. A recorder started with
`--Recorder:ProfilesDirectory` (the desktop app passes the checkout's
`profiles/`) runs on that folder instead, so an edit applies without a
rebuild (see [Editing weights and thresholds](#editing-weights-and-thresholds)).
Run `lookback-testbench profiles` to print the tree:

| Profile | Level | Purpose |
|---|---|---|
| `generic` | game | Any game. No regions; rules over the whole-frame signals only. The fallback, and the baseline a game profile has to beat. |
| `overwatch` | game | The first game profile. Vision and audio only. |
| `overwatch.control`, `.escort`, `.push` | mode | Mode stubs, ready for fingerprints learned from footage. |
| `roblox` | game | What every Roblox experience shares: the client's interface and generic moments. |
| `roblox.<experience>` (16) | sub-game | Steal an Egg, Murder Mystery 2, Brookhaven RP, Adopt Me!, 99 Nights in the Forest, Blox Fruits, RIVALS, Steal a Brainrot, Jujutsu Shenanigans, +1 Speed Keyboard Escape, BedWars, Slap Battles, Fisch, Arsenal, Jailbreak, DOORS. Each has its own regions, rules, tags and Roblox ids. |
| `roblox.<experience>.lobby`, `.match`, `.round`, `.duel`, `.run`, `.day`, `.night` | mode | Lobbies switch off the experience's combat rules; 99 Nights tells day from night by brightness. |

## Why Overwatch first

Overwatch has no public game-state API, and it runs anti-cheat. That makes it
the honest test of the design: everything has to come from what a player
sees and hears. The profile's capture policy says so explicitly:

```json
"capturePolicy": { "allowPresentHook": false, "allowMemoryReading": false }
```

Live capture (M3) reads this policy. For a title like this it will use
Windows.Graphics.Capture from outside the process: no injected present hook,
no reading the game's memory. Executable names in the profile are used only
to recognise that the game is running ([Game Detection](Game-Detection)), and
the detector hands this policy to the recorder unchanged.

## The profile tree

Some games are one game. Others are a platform of many: Roblox runs
thousands of experiences in one executable, and an elimination in an arena
shooter looks nothing like a robbery in an open-world one. Many games also
have modes that change what matters. So profiles form a tree:

```mermaid
flowchart TB
    R[roblox<br/>game · RobloxPlayerBeta.exe] --> RA[roblox.arsenal<br/>sub-game]
    R --> RJ[roblox.jailbreak<br/>sub-game]
    R --> RD[roblox.doors<br/>sub-game]
    R --> RB[roblox.blox-fruits<br/>sub-game]
    O[overwatch<br/>game · Overwatch.exe] --> OC[overwatch.control<br/>mode]
    O --> OE[overwatch.escort<br/>mode]
    O --> OP[overwatch.push<br/>mode]
    RA -.-> RAM[roblox.arsenal.‹mode›<br/>mode, when needed]
```

A child names its `parent`, its `level` (`subgame` or `mode`), and an id that
starts with the parent's id and a dot. It **inherits everything** and adds to it:

| What | Rule |
|---|---|
| Regions | Added. A region name means one rectangle across the whole tree, because one `RegionSignal` measures them all; a child names new regions instead of moving the parent's. |
| Events and composites | Added, or replaced by giving the same name. `disable` removes inherited ones (and composites built on them). |
| Capture policy | Can only get **stricter**. A Roblox experience can never allow the hook or memory reading its game forbids. |
| Executables | Games only (with `executableTitles`); children are detected beneath their game. See [Game Detection](Game-Detection). |

The detector loads the whole tree under the game at once, so the rule-margin
schema never changes mid-session, and evaluates only the rules of the
**active** profile. Inactive rules keep their baselines up to date, so they
are ready the moment their profile becomes active. Every `GameEvent` carries
the profile that was active when it fired.

## Detecting the sub-game and mode

```mermaid
flowchart TB
    EXE[Executable matches a game] --> FLOOR[Floor = game]
    MAN[Chosen by hand<br/>--profile roblox.arsenal] --> FLOOR2[Floor = that profile]
    HINT[Hint matches<br/>launch args · window title · client log] --> FLOOR3[Floor = proven profile<br/>method HINT, confidence 1]
    FLOOR & FLOOR2 & FLOOR3 --> WALK[Each video frame: walk down from the floor]
    WALK --> FP[At each level, the child whose fingerprint<br/>averages ≥ minScore over its window wins]
    FP --> HOLD[New answer held for holdSeconds?]
    HOLD -->|yes| PUB[Publish ProfileDetected on game.profile]
    HOLD -->|no| WALK
```

1. **The floor.** Detection never rises above where it starts: the game
   found by its executable, a profile chosen by hand, or one a hint proved.
2. **Hints are exact.** A hint is a regular expression over facts the
   platform exposes *outside* the game process: the launch arguments, the
   window title, or new lines in the launcher's own log file. With `values`,
   the first capture group must equal one of them, for example a Roblox
   place id. A match decides the sub-game outright and becomes the floor.
   Hints need live capture (M3); footage has none, so footage relies on
   fingerprints.
3. **Fingerprints are learned.** A fingerprint is a few `above`/`below`
   conditions on video signals, usually the region signals. Each frame
   scores the share of conditions that hold; the score is averaged over
   `windowSeconds`. Walking down from the floor, the best child above its
   `minScore` wins at each level.
4. **Hysteresis.** A new answer must persist for `holdSeconds` before the
   active profile switches. Stepping back *up* to a parent waits a full
   window, so a switch between two siblings never detours through the parent.

## Roblox

The `roblox` game profile covers what every experience shares: the client's
top bar, player list, chat, the centre of the screen where most experiences
put pop-ups and banners, and the hotbar. Its events are deliberately generic
(`impact`, `centre_popup`, `respawn_or_teleport`, `loud_moment`), so an
experience with no profile yet still gets reasonable clips.

Each experience profile adds its own regions and rules. Arsenal adds a kill
feed and elimination notice with multikill and killstreak composites;
Jailbreak adds its notification banner and a chase rule; DOORS adds
jumpscare and death; Blox Fruits adds big abilities, level-ups and combos.
Each experience lists its Roblox ids: the **universe id** (one per
experience, stable across all its places, the key to categorise by) and its
**place ids** (lobby, worlds, matches). The `roblox` game profile's
`idHints` read those ids from the client's own log and launch arguments, so
a running experience is identified exactly, without fingerprints.

`lookback-testbench roblox-ids --write true` fills in and verifies the ids
with Roblox's public web API. It resolves listed place ids to universes,
searches by name where none is listed, and confirms each universe's name,
then edits only the `"ids"` line of each profile where the names match.
`lookback-testbench profiles --id roblox.universe=<id>` finds the profile for
an id, and `--tag` lists a category. All regions are estimates until
calibrated.

The client ships anti-tamper protection, so the Roblox capture policy is
external capture only, and every experience inherits it.

## Clip candidates

With the [clipper](Clipper) running (the testbench always runs it), events
are fused with peaks of the game-agnostic excitement curve into one
deduplicated candidate set, each candidate with a QA report; the clipper
page describes how. A profile's optional `clipper` block (below) tunes that
curve for the game, and children inherit it.

On its own (`ProfileRunner(..., publishCandidates: true)`, the default, as
the official-player calibration runs it), the profile runner turns events
into `ClipCandidate`s itself:

- Events whose clip windows overlap, or come within 1.5 s, merge into one
  candidate, up to 60 s long. A team fight with five eliminations becomes one
  clip, not five overlapping ones.
- **Score** is the sum of weight × confidence over the events inside;
  **peak** is the event that contributed most; the candidate belongs to that
  event's profile.
- **Highlight probability** is 1 − e^(−score) for now: bounded, and monotonic
  in the score. Ratings calibrate it in M4.

The report shows a **profile** lane (which profile was active when, with how
and why on hover) and a **candidates** lane (darker is a higher score).

## Menu states

A full-screen change, such as opening the pause menu or a loading screen
starting, looks exciting to every whole-frame signal at once, and used to
make clip candidates of nothing but a menu. A profile can name the screens
of its game that are not play, so the [clipper](Clipper#4b--menus-are-not-play)
ignores what happens on them and the change into or out of them:

```jsonc
"menus": {
  "enterMs": 400,                      // a screen must match this long before the state changes (default 400)
  "exitMs": 400,                       // play must hold this long before a menu state ends (default 400)
  "states": [
    {
      "name": "pause",                 // lower-case letters, digits, underscores; "play" is reserved
      "colors": [                      // 24 cells, 6 columns x 4 rows, row-major from the top left
        "#0d1438", "#0f1840", "#121c4a", "#141f50", "#121c4a", "#0e1640",
        "#0d1438", "#3a3f63", null,      "#9ea3bd", "#121c4a", "#0e1640",   // null: not compared
        "…"
      ],
      "maxDistance": 0.07,             // match when the mean |difference| over compared cells' r, g, b (0..1) is at most this
      "notes": "ESC menu during a match"
    }
  ]
}
```

- **The measurement is game-agnostic.** `ColorGridSignal` publishes the mean
  colour of each of the 24 cells on every analysis frame
  (`color.r<row>c<col>.<r|g|b>`, see [Signals Library](Signals-Library#video-signals));
  a menu state is data over it, so a profile adds no new kind of measurement.
- **The detector** (`MenuStateDetector`) compares each frame with every
  state of the active profile: the closest state within its `maxDistance`
  (closest relative to its own tolerance) is the frame's match, or play when
  none matches. The held state changes only after a new match lasts
  `enterMs` (into a menu, or from one menu to another) or `exitMs` (back to
  play), and remembers when the screen actually started changing.
- **Published** by the profile runner on `signals.rules`, after the rule
  margins: `menu.<state>` (1 while held), `menu.<state>.margin` (positive when
  the screen matches) and `menu.any`, so the timeline report and the live
  profiler show them as lanes (see [Event Schema](Event-Schema#topics)).
- **Inherited** like other profile data: a child adds states or replaces one
  by name, `disable` removes an inherited state, unset `enterMs`/`exitMs`
  come from the parent. A tree with no states has no `menu.*` values and the
  clipper behaves exactly as without menus.
- **Null cells** are not compared: use them for parts of a menu that show the
  game behind it, an animated hero or a timer.

### Learning menu states from footage

Signatures are learned, not hand-written. Label the menu screens in a dataset
manifest with `screens`; the rest of each labelled file is play, and
`ignore` marks footage that is neither (a cinematic, a map title card):

```jsonc
{
  "file": "media/session-01.mp4", "retention": "owned",
  "screens": [
    { "state": "title",     "start": 1,     "end": 10 },
    { "state": "main_menu", "start": 10,    "end": 19 },
    { "state": "ignore",    "start": 101,   "end": 104 },   // map title card
    { "state": "pause",     "start": 251.5, "end": 286 }
  ]
}
```

`lookback-testbench menus <manifest> --out <dir>` analyses each labelled
file from the game profile and, for every state:

1. takes the per-channel **median** of its frames as the signature (robust to
   a few mislabelled frames),
2. leaves out **busy cells**, those whose mean deviation from the median is
   above 0.05 (an animated hero, the game seen through the menu), keeping at
   least the six steadiest,
3. puts `maxDistance` in the **gap** between its own frames (95th percentile
   distance) and every other frame, play or another state (1st percentile),
   at most 0.06 above its own frames. With no gap the state is reported and
   not written.

Frames within 0.75 s of a label's edge (fades) are left out of both sides.
It prints how many of each state's frames match and how many play and
other-state frames match by mistake, and writes `<out>/learned/<game>.json`.
Check it on footage it did not learn from with
`menus <other-manifest> --profiles <out>/learned --check`, then copy the
`menus` block into the profile. Overwatch's states were learned this way
(below); `datasets/overwatch/manifest.menus.json` keeps the labels (the
footage itself is never committed).

## Training profiles from footage

```mermaid
flowchart LR
    L[Label footage:<br/>which profile it shows,<br/>moments, good clips] --> F[fingerprint command]
    F --> P[(learned profiles<br/>detect.visual filled in)]
    P --> D[dataset command<br/>--profiles learned]
    D --> S[Detection accuracy<br/>event precision and recall<br/>candidate precision and recall]
    S -->|adjust regions, rules,<br/>add footage| L
```

`fingerprint <manifest>` analyses every entry from the game down, so every
region signal in the tree is measured. For each sub-game or mode, frames of
its footage are compared with frames of its siblings' footage (or of the
parent alone):

1. Each video signal gets a **separation**: the gap between the two means
   over the sum of the two standard deviations.
2. The best separated signals (up to three, separation ≥ 1 and at least half
   the best) become conditions. The threshold is where the two distributions
   cross, kept at least a tenth of the gap from either mean.
3. `minScore` is set halfway between the share of conditions that hold on
   its own frames and on the others'.

It prints each condition with how often it holds on each side, and writes
complete profile files to `<out>/learned/`. Load them with
`--profiles <out>/learned` on `analyze` or `dataset`, check the numbers, then
copy the `detect.visual` block into the profile in `profiles/`. When nothing
separates a profile, it says so: add a region over something only that
experience or mode shows.

## How a profile becomes events

```mermaid
flowchart LR
    F[Frames 384×216] --> VB[Video signal bank]
    P[(profile.json)] --> RS[RegionSignal<br/>one per profile]
    F --> RS
    A[Audio hops 10 ms] --> AB[Audio signal bank]
    VB --> V[signals.video]
    RS --> V
    AB --> AU[signals.audio]
    V & AU --> M[TimeOrderedMerger]
    M --> D[ProfileEventDetector]
    P --> D
    D --> GE[game.event]
    D --> RU[signals.rules<br/>condition margins]
```

1. **Regions.** `RegionSignal` measures four values inside each region of
   the profile: `region.<name>.luma`, `.activity` (mean change since the
   last frame), `.saturation` and `.red`. They are published with the other
   video signals, so they appear in the timeline and the session log like
   any other signal. Allocation-free per frame.
2. **Merge.** Video frames and audio hops arrive on different threads at
   different speeds. The merger releases them in time order, holding each
   item until every stream has moved past it (the watermark). The output
   depends only on timestamps, so a replay at 8x (where the audio decoder
   can run seconds of footage behind video) gives exactly the same events
   as one at 1x. Only a stream that has sent nothing for two seconds of
   wall time (no audio device) stops holding the others back.
3. **Rules.** On every video frame the detector checks each rule. A rule
   that holds and is out of its cooldown opens a pending event.
4. **Boosts.** Corroborating evidence near the trigger, such as an audio
   onset within 250 ms of a kill-feed change, adds confidence. Because a
   boost can come just after the trigger, the event is held until time has
   passed its widest boost window, then published.
5. **Composites.** Published events feed composite rules: two eliminations
   within four seconds is a `multikill`.

Every event carries `evidence`: the conditions and boosts that made it fire,
in words, with the values seen. The runner also publishes each condition's
margin (value minus threshold) on `signals.rules`, so a viewer can show why
a rule did or did not fire.

## Profile format

```jsonc
{
  "id": "overwatch",                       // lower case; used in events and file names
  "displayName": "Overwatch",
  "version": 1,
  "executables": ["Overwatch.exe"],        // detection only (Game Detection)
  "executableTitles": {},                  // for a shared executable: { "javaw.exe": "^Minecraft\b" }, the window title must match
  "capturePolicy": { "allowPresentHook": false, "allowMemoryReading": false, "notes": "…" },
  "reference": { "aspect": "16:9", "calibrated": false, "notes": "…" },
  "regions": [
    { "name": "kill_feed", "x": 0.72, "y": 0.02, "w": 0.27, "h": 0.20, "purpose": "…" }
  ],
  "events": [
    {
      "name": "elimination",
      "weight": 1.0,                       // how much the moment matters to the clipper
      "baseConfidence": 0.5,
      "cooldownMs": 400,
      "when":    [ /* every condition must hold */ ],
      "whenAny": [ { "signal": "region.elimination_notice.activity", "rise": 0.05 } ],
      "boosts":  [ { "signal": "spectral.onset", "above": 0.5, "withinMs": 250, "add": 0.25 } ],
      "clip": { "preMs": 8000, "postMs": 3000 }
    }
  ],
  "composites": [
    { "name": "multikill", "of": "elimination", "atLeast": 2, "withinMs": 4000, "weight": 2.5 }
  ],
  "clipper": {                             // optional: tunes the clipper's excitement curve for this game
    "components": [ { "signal": "region.kill_feed.activity", "weight": 1.5 } ],
    "high": 1.8, "low": 0.9, "preMs": 8000, "postMs": 3000
  }
}
```

The `clipper` block is described on the [Clipper](Clipper#weights-are-data)
page. Anything it leaves out comes from the parent profile, then from the
clipper's defaults.

A condition sets exactly one of:

| Test | Holds when | Use it for |
|---|---|---|
| `above` | value > threshold | Signals with a fixed meaning, such as `flash.red_edge` |
| `below` | value < threshold | Absence, such as a grey (unsaturated) death screen |
| `rise` | value − baseline > amount | HUD changes. The baseline is a slow moving average of the signal's own past (`baselineAlpha`, default 0.05), so it adapts to a game's palette and brightness |

The loader rejects anything it does not understand: unknown fields
(so a misspelt threshold is an error, not silently ignored), regions outside
the frame, a condition with zero or two tests, composites of unknown events.
When a profile is run, every signal it names is checked against the signals
actually produced, and any missing one is reported as a warning.

## The Overwatch profile

```mermaid
flowchart TB
    subgraph Screen["16:9 frame (regions are estimates until calibrated)"]
        OBJ[objective · top centre]
        KF[kill_feed · top right]
        BAN[banner · centre]
        EN[elimination_notice · below crosshair]
        HP[health · bottom left]
        ULT[ultimate · bottom centre]
        AB[abilities · bottom right]
    end
    EN -->|activity rises| ELIM[elimination]
    KF -->|activity rises| KFA[kill_feed_activity]
    ULT -->|activity rises| UU[ultimate_used]
    RE[flash.red_edge] --> LH[low_health]
    SC[scene.cut + banner grey] --> DEATH[death]
    BAN -->|bright, changing| RR[round_result]
    ELIM -->|2 in 4 s| MK[multikill]
    ELIM -->|3 in 6 s| TK[triple_kill]
    KFA -->|3 in 8 s| TF[team_fight]
```

| Event | Rule | Boosts | Weight |
|---|---|---|---|
| `elimination` | The notice under the crosshair changes | Audio onset, kill-feed change | 1.0 |
| `kill_feed_activity` | Any kill-feed change (anyone's elimination) | | 0.3 |
| `ultimate_used` | The ultimate meter changes sharply | Flash, onset | 1.2 |
| `low_health` | Red vignette at the screen edge | | 0.2 |
| `death` | Scene cut while the centre goes grey | | 0.2 |
| `round_result` | The centre banner is bright and changing | | 0.8 |
| `multikill`, `triple_kill` | 2 or 3 eliminations close together | | 2.5, 4 |
| `team_fight` | 3 kill-feed changes in 8 s | | 1.5 |

`reference.calibrated` is **false**. The region positions are estimates of
the default HUD at 16:9 and 100% HUD scale. Ultrawide, a changed HUD scale or
a game update moves them. They have to be checked against real footage, which
is what the calibration view is for.

### Overwatch menu states

Learned with `lookback-testbench menus` from two of the owner's sessions
(10.5 and 7 minutes, 2560 × 1440, recorded by Lookback on 2026-11-27;
labels in `datasets/overwatch/manifest.menus.json`), then checked with
`--check` on two more sessions they were not learned from
(`manifest.menus-holdout.json`). Numbers are frames at the 30 fps analysis rate.

| State | Screen | `maxDistance` | Learned on: own frames matched, play frames matched | Held out: own frames matched, play frames matched |
|---|---|---|---|---|
| `title` | Start-up logo, connecting / entering game | 0.044 | 480 / 480, 0 of 19 794 | 480 / 480, 0 of 3 450 |
| `mode_select` | The game-mode grid | 0.087 | 2 010 / 2 010, 0 of 19 794 | 165 / 165, 0 of 3 450 |
| `settings` | The options screen | 0.087 | 105 / 105, 0 of 19 794 | (none in that footage) |
| `loading` | A dark band over the blurred menu ("entering practice range") | 0.028 | 417 / 417, 0 of 19 794 | 114 / 114, 0 of 3 450 |
| `loading_black` | A dark band over black ("entering game") | 0.037 | 60 / 60, 0 of 19 794 | 36 / 36, 0 of 3 450 |
| `pause` | The Esc menu during a match | 0.044 | 996 / 996, 0 of 19 794 | 220 / 220, 0 of 3 450 |

Not there, because that footage gave them no stable colour signature: the
**main menu** (its backdrop is rotating hero art; two different heroes
appeared on the same day), **hero select** (its backdrop is the hero and the
map) and the **post-game** screens (three seconds of footage). The intro
cinematic, event pop-ups and map title cards were labelled `ignore`. Label
more footage and rerun the learner to add them; check a new season's screens
with `--check` before trusting the old signatures. `settings` was learned
from five seconds only.

On the same four sessions, the clipper's candidates with and without these
states (`analyze --profile overwatch`; "menu" is the share of a candidate's
window on labelled non-play screens):

CANDIDATE_TABLE

## Editing weights and thresholds

The desktop app's **Profiles** tab shows a profile resolved, the way the
recorder runs it, with the source of every number: the profile whose file
sets it, or `defaults` for the clipper's defaults
(`src/Lookback.Clipper/clipper.defaults.json`). It edits:

| What | Where the change is saved |
|---|---|
| An event's `weight`, and each condition's threshold (`rise`, `above` or `below`) | The file that defines the rule (the nearest profile on the path that defines it; a child that redefines a rule owns it). An inherited rule therefore changes for every profile that inherits it |
| A composite's `weight` (`multikill`, `ace`, ...) | The file that defines the composite |
| The clipper's `high`, `low`, `preMs`, `postMs`, `refractoryMs`, `peakScore` | An override in the edited profile's own `clipper` block; **Reset** removes it, so the value is inherited again |
| A curve component's `weight` | An override component in the edited profile; **Add** picks any signal the game's tree already produces (whole-frame, audio and its region signals), weight 0 switches an inherited one off, **Reset** removes the override |

Nothing else can change: not a region, a signal, a rule's conditions, a
`minSpread` or the capture policy. The app runs
`lookback-testbench profile-view` and `profile-save` against the checkout's
`profiles/` folder (`ProfileEditor` in `Lookback.Calibration`), which:

1. edits the text in place, like `CalibrationWriter`: numbers are replaced
   where they stand, and an override is added or removed on a line of its
   own (with the file's indentation), so `git diff profiles/` shows exactly
   what changed, and removing every override restores the file byte for byte;
2. loads the whole folder with the edit the way the recorder will
   (`ProfileFolder.Load`): the loader's checks (unknown fields, negative
   weights, `low >= high` after inheritance), plus each profile's clipper
   settings over the clipper's defaults (`low` below `high`, a window to
   keep) and only signals the game produces in its components;
3. checks that the parsed profiles differ only in weights, thresholds and
   the `clipper` block, and then writes each changed file atomically. Any
   failure refuses the whole edit with the loader's message and writes
   nothing.

These are the user's own edits in their checkout, not numbers derived from
a watched session, so they are not a `RetentionRules` derived write (unlike
calibration's `CalibrationWriter`).

**When it takes effect.** The recorder loads its profiles from
`Recorder:ProfilesDirectory` (`ProfileSource`): when a recording starts or
footage is analysed, it reloads the folder if any file in it changed. An
edit therefore applies from the next game it records (a recording in
progress keeps the settings it started with). If the folder does not load
(a hand edit with a typo), the recorder logs why and falls back to the
built-in profiles rather than stop recording. Game detection matches
executables against the profiles as they were when the recorder started, so
a new game's `executables` need a recorder restart. Without the option (a
recorder run by hand), the built-in profiles are used and edits need a
rebuild (`launch.bat` rebuilds).

The same works from a terminal:

```
lookback-testbench profile-view --profiles profiles --id valorant.standard
echo {"clipper":{"high":1.8},"components":{"scene.cut":0}} | lookback-testbench profile-save --profiles profiles --id valorant.standard --edits -
```

## Calibration view

```
dotnet run --project src/Lookback.Testbench -- analyze my-overwatch.mp4 --profile overwatch
```

The report then shows, over the video:

- **Profile regions**: the profile's boxes, labelled. Hover a box for its purpose.
- **Detected HUD mask**: the pixels the HUD signal learned are static
  overlay in *this* footage, in teal.

Where the teal mask and the boxes disagree, the boxes are wrong for this
footage. Edit `x`, `y`, `w`, `h` in the JSON, rerun, and look again. Each can
be toggled. Below the video, the timeline gains:

- an **events** lane: a tick per event (height is confidence), a band for its
  clip window; hover for the evidence, click to seek to the moment;
- a **rules** lane per condition: its margin over the threshold, with a
  dashed zero line. Above the line the condition holds. This is the fastest
  way to choose a threshold: look at the margin during a real elimination
  and during a false one.

Pass a path instead of an id (`--profile ./my-overwatch.json`) to try an
edited copy without rebuilding.

## Datasets and scoring

Each game gets a dataset: footage plus hand-marked moments, described by a
manifest at `datasets/<game>/manifest.json`. Each entry can also say which
profile the footage shows (`"profile": "roblox.arsenal"`), which scores
detection and teaches fingerprints, and which stretches a person would clip
(`"clips": [{ "start": 40, "end": 52 }]`), which scores candidates. Media is never committed; only
manifests are (see `.gitignore`). An example is in
`datasets/overwatch/manifest.example.json`.

```mermaid
flowchart LR
    MF[(manifest.json)] --> DR[DatasetRunner]
    DR -->|each entry| TB[Testbench analyze<br/>with the game's profile]
    TB --> EV[Detected events]
    MF --> LB[Labels]
    EV & LB --> SC[EventScoring<br/>one-to-one, within tolerance]
    SC --> OUT[Precision and recall<br/>per event, per entry, overall<br/>scores.json]
```

Every entry records where the footage came from and its retention:
`owned` (your recordings), `permissioned` or `open-license`, the last two
with a `permissionRef` saying where the permission or licence is recorded.
`ephemeral` is not allowed in a dataset, because a dataset is stored by
definition. Official-player sessions are never a dataset source: they stay
ephemeral, enforced by [RetentionRules](Data-Retention).

```
dotnet run --project src/Lookback.Testbench -- dataset datasets/overwatch/manifest.json --tolerance 1.0
```

```
arsenal-01.mp4: 23 events, detected roblox.arsenal (expected roblox.arsenal, right 91% of the time)

detection  right profile 88% of labelled time
candidates 14 match labelled clips, 3 extra, 2 missed: precision 82%, recall 88%, mean overlap 0.61

event                       TP    FP    FN  precision   recall  (tolerance 1 s)
elimination                 41     6     9        87%      82%
multikill                    7     1     2        88%      78%
```

(Illustrative output.) A detection matches a label of the same name within
the tolerance. Each label and each detection is used at most once, closest
pairs first, so firing twice on one elimination scores one hit and one false
positive. Candidates match labelled clips one-to-one when their time ranges
overlap by at least 0.3 (intersection over union). Detection accuracy is the
share of labelled time during which the active profile was the one the entry
names. Scores are counted per entry and then summed, so a label in one
file can never match a detection in another.

This is the per-game QA loop for profile rules: change a threshold or a
region, rerun the dataset, and see precision and recall move. The same
labels later train the learned scorer (M4).

## Source files

| File | What it does |
|---|---|
| `Model/GameProfile.cs` | The records above, with defaults |
| `Model/ProfileCatalog.cs` | The tree: parents, children, inheritance, tree-wide checks |
| `Detection/ProfileTracker.cs` | Hints, fingerprints and hysteresis: which profile is active |
| `Candidates/CandidateBuilder.cs` | Events to clip candidates |
| `Model/GameProfileLoader.cs` | Loads by id (embedded) or path, rejects unknown fields, validates structure and signal names |
| `Regions/RegionSignal.cs` | The per-region measurements, as an `IVideoSignal` |
| `Lookback.Calibration/Profiles/ProfileEditor.cs` | The profile editor: the resolved view with sources, and validated in-place saves of weights, thresholds and clipper overrides |
| `Lookback.Calibration/Profiles/ProfileFolder.cs` | Loads a profiles folder as the recorder runs it, with the clipper-over-defaults and signal checks |
| `Lookback.Calibration/Profiles/ProfileTextPatcher.cs` | Edits profile JSON text in place (replace, add, remove) so diffs stay minimal |
| `Lookback.Recorder/ProfileSource.cs` | The recorder's catalog: the profiles folder, reloaded when it changes, else the built-in profiles |
| `Detection/TimeOrderedMerger.cs` | Watermark merge of video and audio |
| `Detection/ProfileEventDetector.cs` | Conditions, baselines, cooldowns, boosts, composites, margins |
| `Detection/ProfileRunner.cs` | Subscribes to one session's signals on the bus, publishes `game.event` and `signals.rules` |
| `Model/MenuSpec.cs` | Menu states, their colour signatures and the distance between a frame and a signature |
| `Detection/MenuStateDetector.cs` | The held menu state with hysteresis; `ColorGridReader` finds the grid in signal frames |
| `Detection/MenuTracker.cs` | Menu states across a game tree for the active profile, and their `menu.*` values |
| `Testbench/Datasets/*` | Manifest, event, detection and candidate scoring, the dataset runner, the fingerprint learner, the menu learner |
