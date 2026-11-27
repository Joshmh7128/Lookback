# Non-shooter game profiles

Eight game trees beyond the shooters, built for the demo. Like every profile
they are data only: HUD regions plus rules over the game-agnostic signals,
read from the screen and the sound. No game APIs, no memory reading, no
hooks. Every capture policy is external-only, and every region is an
**estimate** (`"calibrated": false`) until it has been checked against real
footage with the calibration view
(`analyze <footage> --profile <id>`). See [Game profiles](Game-Profiles) for
the format and the tree.

| Game | Executables | Tree | How the child is chosen |
|---|---|---|---|
| GTA V `gta-v` | `GTA5.exe`, `GTA5_Enhanced.exe` | `.story`, `.online` → `.online.heist`, `.online.race`, `.online.deathmatch` | Learned fingerprint (no reliable outside hint: players move between story and Online in one session) |
| Minecraft `minecraft` | `Minecraft.Windows.exe`, `javaw.exe` | modes `.survival`, `.hardcore`, `.creative`; minigames `.bedwars`, `.skywars` | Learned fingerprint (hardcore hearts, missing vitals in creative, minigame sidebars) |
| Rocket League `rocket-league` | `RocketLeague.exe` | modes `.soccar`, `.hoops`, `.rumble`, `.dropshot`, `.snow-day` | Learned fingerprint |
| League of Legends `league-of-legends` | `League of Legends.exe` | modes `.summoners-rift`, `.aram`, `.arena`; sub-game `.tft` | **Id hint**: the map id in the game client's own log (`league.map`: 11, 12, 30, 22) |
| R.E.P.O. `repo` | `REPO.exe` | game only | |
| Lethal Company `lethal-company` | `Lethal Company.exe` | game only | |
| PEAK `peak` | `PEAK.exe` | game only | |
| Dead by Daylight `dead-by-daylight` | Steam, Epic and Microsoft Store shipping executables | roles `.survivor`, `.killer` | Learned fingerprint (skill-check area, killer power gauge) |

## What each one listens for

| Game | Regions | Key events (weight) | Composites |
|---|---|---|---|
| GTA V | minimap, wanted stars, money, weapon, centre banner, view, help text, subtitles | `wasted` (0.9): view goes grey while the banner changes; `mission_result` (1.0, 2.0 in heists); `wanted_level_change`; `police_chase`: radar turns red; `explosion` | `mayhem`, `escalating_chase`; Online adds `player_kill` from the feed above the radar and `online_multikill`; races add `position_change`, `crash` |
| Minecraft | hotbar, xp bar, hearts and hunger, boss bar, toast, chat, title, sidebar, view | `death`: red tint plus title; `advancement`: toast plus chime; `boss_fight`; `explosion`; `level_up` | `chain_explosion`, `close_call`; hardcore makes death worth 2.5; Bed Wars adds `bed_destroyed` and chat kills |
| Rocket League | scoreboard, feed, stat pop-up, boost, banner | `goal` (2.0): scoreboard changes, boosted by the goal-explosion flash; `stat_notice` (saves, shots); `demolition`; `match_moment` | `demo_chain`, `big_defence`, `quick_goals`; Rumble adds `power_up_used` |
| League of Legends | announcement, kill feed, K/D/A, minimap, abilities, ultimate slot, view, end banner | `champion_kill`: your K/D/A changes while the picture is in colour; `announcement`; `ultimate_used`; `death`: picture turns grey; `match_result` | `multikill` (League's 10 s window), `rampage`, `team_fight`; TFT switches the MOBA rules off and adds `combat_result`, `board_fight` |
| R.E.P.O. | vitals, haul, centre text, view | `monster_scare`: loudness jumps 15 dB above its baseline; `valuable_damaged`: haul drops with a crash; `death`; `extraction` | `expensive_disaster`, `hunted` |
| Lethal Company | vitals, clock, inventory, centre text, view | `jumpscare`; `damage_taken`; `death`: a cut, boosted by the vitals changing; `ship_message` | `close_call`, `nightmare` |
| PEAK | stamina bar, inventory, centre text, view | `fall`: the whole picture tumbles, boosted by red appearing in the stamina bar; `injury`; `passed_out`; `milestone` | `rough_climb`, `tumble` |
| Dead by Daylight | survivor status, perks, prompt, view | `status_change`: someone downed, hooked or killed, or a generator done; `chase`: music swells while moving fast; `impact` | `long_chase`, `scramble`; survivors add `skill_check`, `hit_by_killer`; killers add `power_used`, `stunned` and `snowball` (3 status changes in 45 s, worth 2.5) |

## Known gaps

- **`javaw.exe`** is the Java runtime, not Minecraft. Java Edition has no
  executable of its own, so any Java app in the foreground is taken for
  Minecraft until game detection can also check the window title.
- **Chat-based hints are deliberately not used** for Minecraft minigames, and
  GTA's `-StraightIntoFreemode` launch argument is not used for Online: a hint
  fixes the sub-game for the rest of the session, and players hop between
  them without restarting.
- **League map ids and log line format** need checking against a current
  client log. Riot has changed map ids for rotating modes before.
- **Horror co-op games** (R.E.P.O., Lethal Company, PEAK) have minimal HUDs, so
  they lean on audio (sudden loudness, onsets) and whole-frame signals. Expect
  their thresholds to move the most once real footage is scored.
- **Dead by Daylight executables** differ per store; check Task Manager on
  your install.

## Datasets

Each tree has `datasets/<game>/manifest.example.json`: one entry per sub-game
or mode, with placeholder labels and clips that show the shape (event names
match the profile), and one permissioned third-party entry showing where the
`permissionRef` goes. Copy it to `manifest.json` beside your recordings, label
real times, then:

```
lookback-testbench fingerprint datasets/<game>/manifest.json
lookback-testbench dataset datasets/<game>/manifest.json --profiles fingerprint-out/learned
```
