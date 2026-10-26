# Changelog

<!-- One "## <version>" section per release. The Release workflow publishes the section of the tagged
     version as the GitHub release notes; the same notes go into pluginmaster.json (shown in Dalamud). -->

## Unreleased

- Chat summary: on a wipe, the ability that killed you, its source and its damage, e.g.
  `Killed by Hydrothermal Missile (Living Liquid, 98,432)`. Also in the ultimate phase summaries. New "Killed by"
  value in Settings > Pulls (on by default).

## 1.0.6.0

- Ultimates: TEA now goes P1 → Limit Cut → P2. Limit Cut (a transition: no boss, no DPS check) starts when Living
  Liquid dies and lasts until Brute Justice and Cruise Chaser are both targetable.
- Ultimates: only the phase summaries are printed in chat, no more pull summary on top of them.
- Server bar: once the fight is over, every element keeps its last value (ETA, enrage timer, pace, phase values...)
  until the next fight.

## 1.0.5.0

- Party DPS check: only the pDPS value is colored (green / red), now with its gap to the required DPS:
  `182,345 pDPS (+3.2%)`. The other elements of the bar keep their normal color.
- Ultimates: The Epic of Alexander P2 (Brute Justice & Cruise Chaser) is supported; it starts when both bosses are
  targetable.
- Fix: ultimate phases now end as soon as the next phase starts. Invisible copies of a boss no longer keep a phase
  alive or skew its HP.
- Ultimates: chat summary at the end of each phase (phase time, DPS on the phase's bosses vs the DPS needed, enrage
  margin, GCD), or HP left when the pull ends during the phase. Option in Settings > Pulls.
- Ultimates: new bar element "Ultimate phase boss DPS" (`Boss 145,000 (+3.6%)`): DPS on the current phase's bosses
  only, green / red against the DPS needed for the phase enrage.
- Ultimates tab: combined HP of each phase's bosses (e.g. Brute Justice + Cruise Chaser for TEA P2), read in game the
  first time the phase is reached or entered by hand, and the resulting required DPS (HP / enrage).

## 1.0.4.1

- Fix: the plugin icon now shows in Dalamud once the plugin is installed (the packaged manifest had no icon URL).

## 1.0.4.0

- History: filter pulls by kind of content (Dungeons, Trials, Extreme, Raids, Savage, Ultimate, Other), one or several
  at once. Works for pulls already recorded.
- Fix: the default AAC Heavyweight M1/M2 (Savage) party checks pointed at the wrong zones (Abyssos); existing
  settings are corrected automatically.
- The pace vs your best kill now reads `Best +4%` in the server bar.
- Live and History: the player tables fit the number of players (no scrollbar, no empty space in smaller groups).
- Chat summary: choose the values shown (job, DPS, rDPS, FFLogs %, GCD, pDPS, deaths), separated by `|`; rDPS now has its
  rank; DPS, rDPS, GCD and pDPS can each show `NEW BEST (+2.1%)` (with the gain) or the gap to your best kill.

## 1.0.3.0

- New option: separator between the server bar elements (space, `|`, `/`, `-` or your own text).
- Boss HP is now labelled: `HP 54%`.

## 1.0.2.0

- Ultimate mode: each phase is tracked on its own, with its own timer, boss HP, projected kill and enrage,
  even though the fight never stops. Adds don't count for the DPS check.
  First supported phase: The Epic of Alexander P1 (Living Liquid).
- New Ultimates settings tab: enrage time of each phase, or take it from your last wipe in one click.
- GCD uptime: share of the time an enemy is targetable during which your GCD is rolling. Deaths count as lost
  time; per phase in ultimates. Shown in a new bar element, the tooltip, the chat summary and the history.
- Uptime graph in the Live and History tabs, with the periods where nothing was targetable shaded.

## 1.0.1.0

- New bar element: enrage timer, counting down from the enrage time set in Party checks.
- History: "Set as enrage time" uses a pull's duration as the fight's enrage time (handy after a wipe to the enrage).

## 1.0.0.0

- Full rewrite.
- Built-in tracker: works without ACT, or next to it.
- FFLogs-style rDPS.
- Reorderable server bar elements: job icon, rank, rank in your role, pace vs your best kill, boss HP,
  projected kill, FFLogs percentile.
- Main window: live breakdown, DPS graphs, pull history with comparisons.
- Party DPS checks from boss HP and enrage time.
- Chat summary after each pull.
- Optional FFLogs percentile estimate.
