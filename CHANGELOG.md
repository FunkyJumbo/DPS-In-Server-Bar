# Changelog

<!-- One "## <version>" section per release. The Release workflow publishes the section of the tagged
     version as the GitHub release notes; the same notes go into pluginmaster.json (shown in Dalamud). -->

## Unreleased

- History: filter pulls by kind of content (Dungeons, Trials, Extreme, Raids, Savage, Ultimate, Other), one or several
  at once. Works for pulls already recorded.
- Fix: the default AAC Heavyweight M1/M2 (Savage) party checks pointed at the wrong zones (Abyssos); existing
  settings are corrected automatically.

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
