<p align="center">
  <img src="DpsInServerBar/icon.png" width="96" alt="Raid Companion icon">
</p>

<h1 align="center">Raid Companion</h1>

<p align="center">
  Your live DPS, rDPS and party DPS — right in FFXIV's server info bar.<br>
  A <a href="https://github.com/goatcorp/Dalamud">Dalamud</a> plugin. Works with ACT, or on its own.
</p>

---

<p align="center">
  <img src="docs/images/bar.png" alt="Raid Companion entry showing 46,209 rDPS in the server info bar"><br>
  <sub>Your rDPS, right next to the clock.</sub>
</p>

<!-- More screenshots / GIFs go to docs/images/ (e.g. the Live window). -->

## Why

The aim of this project is a clean DPS meter, built into the game's UI, that shows the essentials at a glance.

Full overlays are great, but sometimes you want to keep your UI clean and still see what matters where your
eyes already are. Raid Companion puts your DPS in the server info bar, at the top right of the screen.
At a glance you also see your place in the party, the gap to the top player (or to the second one when you
are first), and whether the party's DPS is on track for the boss' enrage.

When you need more, the full picture is one click away in a classic window: per-player breakdown, graphs,
pull history and an FFLogs-style rDPS.

This plugin doesn't reinvent the DPS meter: it brings it into the game's UI in a clean and meaningful way.

## Installation

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add:
   ```
   https://raw.githubusercontent.com/FunkyJumbo/DPS-In-Server-Bar/master/pluginmaster.json
   ```
3. Save, then install **Raid Companion** from `/xlplugins`.

## Features

### In the server bar

A single compact entry, for example `[icon] DRK #1 (+8%) 21,234 rDPS ~92`. Every piece is optional
and can be **reordered**, with the separator of your choice between them (space, `|`, `/`, `-` or custom):

| Element | Example | What it shows |
|---|---|---|
| Job icon / name | `[icon] DRK` | Your job, with the game's own icon |
| Rank | `#2` | Your rank in the party |
| Rank in your role | `#1 (+8%)` | Rank among tanks, healers or DPS only, and the gap to #1 (or to #2 when you are first) |
| Value | `21,234 DPS` | Personal DPS, party DPS (pDPS) or rDPS |
| Pace | `Best +4%` | Damage so far vs. your best **kill** of this fight, at the same second |
| Boss HP | `HP 54%` | HP of the boss, read from the game |
| Enrage timer | `Enrage 3:12` | Time left before the enrage entered for this fight — red for the last 30 seconds |
| Ultimate phase | `P1` | Current phase in a supported ultimate |
| Ultimate phase boss DPS | `Boss 145,000 (+3.6%)` | DPS on the current phase's bosses only, and its gap to the DPS needed for the phase enrage |
| GCD uptime | `GCD 98.7%` | Share of the time an enemy was targetable during which your GCD was rolling — per phase in an ultimate |
| Fight duration | `3:42` | Time since the pull |
| FFLogs percentile | `~92` | Estimated FFLogs percentile of your rDPS, in FFLogs colors |

- **Left click** cycles DPS → pDPS → rDPS. **Right click** opens the main window.
- **Presets**: save several setups (value, format, elements and order), rename them, and switch with
  `/dpsbar preset 2`, handy in a macro.
- Once the fight is over, every element keeps its last value until the next fight.
- **Hover** for a summary: encounter, rDPS, rank in your role, crit/DH rates, party DPS check, boss HP, projected kill, time to enrage, GCD uptime (per phase in an ultimate), best kill, percentile.
- Colors: with a DPS check for the fight, only the pDPS value turns green/red, with its gap to the required DPS
  (`182,345 pDPS (+3.2%)`); everything turns grey once the fight is over.

### Main window

- **Live** — every player's DPS, rDPS, damage %, crit, direct hit and deaths, sortable by DPS or rDPS.
  Hover an rDPS value to see what the player gave to / received from buffs.
  At the top, cards of the same size: your DPS and rDPS with your rank, the party DPS with its gap to the enrage (when
  the fight has one: the DPS still needed for the boss HP left in the time left), the boss HP with how long before (or
  after) the enrage the boss would die at this pace, and the pace against your best pull. Then a DPS graph (rolling and
  average, personal or party, compared with your best kill).
  Switch the graph to **Uptime** to see exactly when you lost GCD uptime; periods where nothing was targetable are shaded.
  A **Personal** section, between the cards and the party, gathers your own play (only what your job has):
  **GCD uptime** (hover for the time lost); the full GCD analysis is in History.
  **GCD analysis** (History): the lost GCD time split by cause: clipping (an oGCD's animation lock held you
  with the GCD ready), late (free to act, the GCD came late), no GCD (a whole GCD or more without one: movement,
  mechanics) and dead, plus the number of GCDs it represents. Hover the ? for details.
  The gaps are also marked at the bottom of the **Uptime** graph (red: no GCD, orange: clipping, grey: late; hover one for
  its time and length).
  **Gauge overcap**: the job gauge lost because it was too full when an action filled it (Blood, Beast Gauge, Cartridges,
  Kenki, Ninki, Soul, Shroud, Heat, Battery, mana, Palette...), each with the icon of the action that spends it, and the actions that overcapped it on hover. The gain of
  each action is learned from your own uses, so its first uses are not counted. Lilies, Addersgall and Polyglot: time
  spent full, in stacks lost. Gauges filled by crits or by chance (Chakra, Esprit, Soul Voice...) are not tracked.
  **Positionals** (melee jobs): how many of your positionals landed, read from the bonus the game gave each hit (True
  North and bosses without positionals count), with the icon of each action you missed and when on hover.
  **Combo errors**: combo steps used without the step before them (wrong button), combos restarted before they were
  finished, and combos whose timer ran out before the next step (not counted while dead), by the icon of the action used.
  **DoT uptime** (jobs with a DoT): how long each of your DoTs stayed on the boss while it could be attacked (a dummy is
  not a boss: test it on a real fight).
  **Overheal** (healers): the share of each healer's healing that went over full HP, for information (in Live, a foldable
  line with yours, the other healers inside).
  **Tank swaps** (tanks): a foldable list of the boss going from one tank to another, with why: death, Provoke,
  Shirk or enmity from damage.
  **Failed mechanics**: each penalty debuff an enemy action puts on a party member (Vulnerability Up, Damage Down,
  Down for the Count), counted per player with the debuffs taken (hover a row for each one: when, which debuff, from which action). Tankbuster vulnerabilities on tanks are ignored by
  default (Settings > Pulls history).
  **Deaths**: every death of the party with its cause (the hit that took the last HP, a debuff running out, a fall
  from the arena); hover a death for the last hits taken before it. Deaths are also marked on the graph.
  **Compact view** opens a small rounded pop-up without a title bar: only the party meter (DPS or rDPS, share,
  deaths, ranked by the chosen metric), sized to its content, next to the main window. Drag it by its background; click
  the metric header to switch DPS / rDPS; right click for the menu (background opacity, main window, history,
  settings, close). Its columns (%, crit, direct hit, deaths), header and background opacity are set in Settings > Display.
- **History** — every pull is saved with its result (Kill / Wipe), duration, DPS, rDPS, rank and timeline.
  Filter by kind of content (Dungeons, Trials, Extreme, Raids, Savage, Criterion, Ultimate, Other — several at once, read from the game data)
  and by encounter (type in the list to search a fight). The pull list can be made taller by dragging the grip under it
  (double-click to reset). Open a pull to see its breakdown and graph, tick another one to **compare** them.
  **Progression** (its own tab, next to Live and History; its filters keep to Extreme, Savage, Criterion and Ultimate): pick an instance (by its name, with the
  fight's name when it has two) to see how far each pull got (boss HP taken, or phase and
  HP in an ultimate) pull after pull, with the best so far, the best pull, wipes per phase and what wiped the party
  most (the main cause of death at the end of each wipe), and the furthest mechanic seen (the last enemy cast of the
  furthest wipe). The graph is per pull or per time spent in the instance; in an
  ultimate each phase has its band, and pulls are listed under the ultimate's name. Click a pull on the graph to open it.
  Choose a **group** (found from the players of each pull, searchable by name or player) to follow only your static;
  **Save group** gives it a name (renamed or forgotten later), and saved groups come first in the list. Untick pulls in **Pulls**
  to leave some out (a night with another group, a test pull); Shift+click ticks or unticks a whole range.
  Each pull has the same **Personal** section as Live, each line foldable for its detail: GCD uptime (with the number of
  GCDs; inside, the uptime per ultimate phase, the GCD analysis and the clipping timeline showing the
  oGCD behind each clip by its icon), gauge overcap (inside: each gauge with the actions that overcapped it), positionals
  and combo errors (inside: a list of each miss with its time and action), DoT uptime (inside: each time a DoT stayed off
  the boss a second or more, and for how long), overheal (inside: every healer, and your overheal per heal action and HoT)
  and tank swaps, and your mitigation (inside: how many times you used each mitigation and defensive buff). Under the
  deaths, a **Mitigation** section lists, for each party member (one foldable line each), every mitigation and defensive buff used with the time
  of each use (GCD shields left out). It also keeps its uptime graph, the deaths of the party with their cause, and its failed mechanics with who took what and from which
  attack. A **timeline** places along the pull what you tick above it, one lane each: deaths, tank swaps,
  failed mechanics and mitigation (one lane per player, by the icon of each action; it grows with what is shown). Hover a
  mark for its detail. Deaths are marked on the graphs.

### After each pull

An optional chat summary, with the values of your choice (job, DPS, rDPS, FFLogs percentile, GCD uptime, pDPS, deaths,
the ability that killed you, and the mechanics you failed with the party's count):

```
[Recap] Brute Bomber (4:32) Kill | DRK | DPS 21,234 #2/8 NEW BEST (+2.1%) | rDPS 20,511 #3/8 (best -1.2%) | FFLogs ~87 | GCD 98.7% (best -0.4 pts) | pDPS 182,345 (check OK) NEW BEST (+0.8%)
```

DPS, rDPS, GCD and pDPS can each be compared with your best kill of the fight (`NEW BEST (+2.1%)` or the gap).
If you died during the pull (any content, wipe or kill), `Killed by Hydrothermal Missile (Living Liquid, 98,432)` tells the
last hit you took before dying, auto-attacks included (also in the ultimate phase summaries; read by the built-in tracker).
Only a hit that could take the HP you had counts; without one it guesses: `falling off the arena`, a debuff that ran out (`Doom (debuff)`) or
`instant death (arena edge?)`.
Comparisons are only made between **kills** — a wipe or a prog pull says nothing about your DPS.
The summary can be turned off in dungeons.

### DPS checks

The **DPS check** tab lists every **ultimate, savage raid and extreme trial** from the game data, most recent first,
one row each (a search box finds a fight quickly). For each fight you can enter the **boss HP** (one click reads it from the game) and the **enrage time**:
the minimum party DPS is then `HP / enrage`. The enrage time also drives the enrage timer in the bar.
A manual minimum can be used instead. Any other zone or a single
encounter can be added under **Other / custom**.

The Stormblood to Dawntrail **extremes and savage raids come filled in**: boss HP and enrage time read from FFLogs (the
HP from recent logs, the enrage from the wipes that ended together at the enrage after release), shown greyed with
"built-in". They apply until you enter your own values. Some enrages are unknown (too few wipes to the enrage), and a
fight with several bosses only counts one of them.

### Ultimates

In an ultimate the fight never stops between phases and several bosses are often up at once.
Each supported phase is tracked on its own: its timer starts when its boss becomes targetable, only its own
boss counts for the DPS check (adds are ignored), and the Boss HP and Enrage timer elements
follow the current phase. GCD uptime is also measured per phase. The **Ultimate phase boss DPS** element shows the DPS on the phase's bosses only (not the whole fight's pDPS), against the DPS needed for its enrage, and a chat summary is printed at the end of each phase. Enter each phase's enrage time once, or take it from your last wipe in one click.
The combined HP of each phase's bosses (all of them must die) is read in game the first time the phase is reached and
shown under the ultimate in the DPS check tab with the resulting required DPS; it can be overridden by hand.

Supported so far: **The Epic of Alexander — P1 (Living Liquid), Limit Cut (transition) and P2 (Brute Justice & Cruise Chaser)**.
A boss phase ends when its bosses die (then the transition starts) or as soon as the next boss phase starts (for P2: when
both bosses are targetable). In a supported ultimate, the phase summaries replace the usual pull summary. More phases and ultimates will be added one at a time.

### rDPS

An approximation of [FFLogs' rDPS](https://www.fflogs.com/help/rdps): damage gained from a raid buff
is taken from the player who dealt it and credited to the player who gave the buff.
See [How rDPS is computed](#how-rdps-is-computed).

### Opener practice

Pick an opener of your current job: every job has The Balance's level 100 openers (several for some jobs, e.g. 6 for
Monk, 3 for Bard and Red Mage), with the guide's notes, press **Start** for a countdown, start an
in-game `/countdown` (the run follows it, a cancelled countdown resets it) or just use its first action, then play it: each action used in the right order is greyed out and checked. GCDs are the big
boxes on the track, oGCDs the small ones above; the oGCDs of a weave window can come in any order. A wrong action
flashes the expected box in red, a GCD used before its oGCDs marks them missed. Each GCD shows the time it was used,
and the run ends with the total time, mistakes and missed actions. The pre-pull actions and the tincture can each be left out. **Auto close** (option) closes the window 3 s after the opener is done, or a chosen time (40 s by default) after its first action. Stretch the window sideways to fit a long opener on fewer rows. **Compact** switches to a small pop-up (no title bar, sized to its content) with only the icons, Start / Reset, **Full view** and **Close**; right click it for the background opacity. The background opacity slider only fades the background, never the icons. Before the pull, a line tells whether you are ready (out of combat, full MP, the opener's cooldowns and tincture back up) or what is still on cooldown. **Start** launches the in-game `/countdown` (option), or a countdown in the window when in combat, where the game refuses it. `/dpsbar opener` or the Live tab.

### FFLogs percentile (optional)

With your own FFLogs API credentials, the plugin downloads the rDPS rankings of your job for the current fight
and places your rDPS among them. See [FFLogs setup](#fflogs-setup).

## Data sources

| Mode | How it works |
|---|---|
| **Built-in only** (default, recommended) | The plugin reads your party's damage directly from the game — no ACT needed |
| **Auto** | ACT when it is reachable, the built-in tracker otherwise |
| **ACT only** | Numbers come from ACT via OverlayPlugin's WebSocket server (WSServer started, default `ws://127.0.0.1:10501/ws`) |

The built-in tracker merges pets into their owner and counts the Limit Break separately. It is slightly less
exhaustive than ACT (a DoT whose owner cannot be determined is skipped). rDPS always comes from the built-in
tracker, which always runs next to ACT; ACT's numbers are then adjusted with it.

## Commands

| Command | Action |
|---|---|
| `/dpsbar` | Open / close the main window |
| `/dpsbar config` | Settings |
| `/dpsbar history` | Main window on the History tab |
| `/dpsbar toggle` | Cycle DPS → pDPS → rDPS |
| `/dpsbar compact` | Open / close the compact meter |
| `/dpsbar opener` | Open / close the opener practice (in the view used last) |
| `/dpsbar opener compact` | Open / close the opener practice in its compact view |
| `/dpsbar opener full` | Open / close the opener practice in its full window |
| `/dpsbar reconnect` | Reconnect to ACT |
| `/dpsbar preset <n\|name\|next>` | Switch the server bar to a saved preset: by number, by name, or the next one |

## Settings

| Tab | Contents |
|---|---|
| **DPS in server bar** | Presets (renamable, switchable with `/dpsbar preset N`), metric, number format (`12345` / `12,345` / `12.3k`), bar elements and their order, separator, **bar preview** (placeholder values for 10 s), auto-hide, colors |
| **Chat summary** | On/off, in dungeons, at the end of each ultimate phase, values shown, comparison with your best kill, **preview in chat** |
| **Display** | Compact meter (columns, header, background opacity), DPS graphs in the main window, Limit Break in party DPS |
| **Pulls history** | Record only in duties, minimum pull length, history size, graph window |
| **Appearance** | Theme presets, saved custom themes (renamable), background opacity, custom colors, preview |
| **DPS check** (WIP) | Still a work in progress: the values may be incomplete or wrong for now. Ultimates, savage raids and extreme trials (most recent first): boss HP, enrage time, minimum party DPS; ultimate phase tracking with each phase's HP, enrage and required DPS; other zones / encounters |
| **Data source** | Built-in (recommended) / Auto / ACT, ACT address, connection status |
| **FFLogs** | Percentile estimate, API credentials with a connection test, manual encounter id |

## How rDPS is computed

The plugin follows FFLogs' published method, as documented in
[*FFLogs Buff Allocation Math*](https://docs.google.com/document/d/1pWVsDaroDHpvh1IuB42bWLBE2TMru2CrWnjTDjeAvJY/view) by joncho:

- **% damage buffs** — the gain `N − N/M` (M = all external multipliers) is split between buffs in proportion to `ln(multiplier)`.
- **Crit / direct hit rate buffs** — a crit (or DH) is split by the probability that it came from the buffs, `(buffed − own rate) / buffed`;
  that part credits the crit (DH) multiplier to the rate buffers, in proportion to their rate.
- **Guaranteed crits / DHs** (Inner Release, Reassemble, Life Surge…) — rate buffs act as a damage multiplier, as in the game since 6.2.
- **DoTs** — buffs are snapshotted when the DoT is applied; ticks are split over all crit/DH outcomes by probability.
- Self buffs are never redistributed.

Raid buff values for the current expansion are in [`Rdps/RaidBuffs.cs`](DpsInServerBar/Rdps/RaidBuffs.cs)
(status ids come from the game data, percentages must be kept up to date by hand).

**Accuracy.** The game does not tell the client other players' crit and direct hit rates, which FFLogs knows.
The plugin estimates them from each player's hits (yours come from your real stats at level 100).
In tests against FFLogs' own meter, most players were within ±0.3%; a player receiving a large crit buff
(e.g. a Dancer's partner) can be off by about 1% on a single pull, less after a few pulls with the same group.

## FFLogs setup

1. On fflogs.com, open **[API Clients](https://www.fflogs.com/api/clients)** and create a client
   (any name, redirect URL `http://localhost`).
2. In the plugin settings, **FFLogs** tab, paste the **Client ID** and **Client Secret**.
   (The old "V1 Client Key" does not work: the plugin uses API v2.)
3. Enable the **FFLogs percentile** element in DPS in server bar → Server bar elements.

The fight is matched to FFLogs by the boss' English name; if that fails, enter the FFLogs encounter id for the zone.
Rankings are cached for 24 hours. The percentile compares your rDPS with each character's best parse for your job
in the current partition — close to FFLogs' *rank %*. During a fight it is only a trend, since rDPS moves with burst windows.

## Privacy

- Pull history (`history.json`) and the FFLogs cache stay on your computer, in the plugin's configuration folder.
- The plugin only talks to ACT on your machine and, if you enable it, to the FFLogs API with your own credentials.
- FFLogs credentials are stored in the plugin's configuration file, like any other setting.

## Known limitations

- rDPS is an estimate (see [Accuracy](#how-rdps-is-computed)); raid buff values need updating when a patch changes them.
- Only the guaranteed crit/DH effects listed in `RaidBuffs.cs` are recognised.
- Enrage times are not in the game data: the built-in ones come from FFLogs, the others must be entered by hand (DPS check tab).
- GCD uptime: an enemy that stays targetable while invulnerable still counts as available time. Only your own uptime is measured (the game does not expose other players' GCD).
- Ultimate mode only covers the phases listed in [Ultimates](#ultimates); later phases show as "not supported yet".
- For a fight with several bosses or HP pools, the boss HP shown is the biggest enemy's; add the pools up by hand for the DPS check.
- The FFLogs percentile depends on matching the fight by name and on FFLogs' API being reachable.

## Credits

- [Dalamud](https://github.com/goatcorp/Dalamud) and [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs).
- joncho's *FFLogs Buff Allocation Math* for the rDPS method.
- [FFLogs](https://www.fflogs.com/) for rDPS and the rankings API.
- [LootView](https://github.com/GitHixy/LootView), whose midnight-blue and gold interface inspired ours.

This project is not affiliated with FFLogs, Archon or Square Enix.

## License

[AGPL-3.0-or-later](LICENSE.md)
