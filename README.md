# Beholder

A BepInEx mod for Demeo: a floating eye with deadly rays joins the monsters. The **Beholder** is built on the game's own all-seeing eye and fires eye rays inspired by the classic fantasy beholder.

Only the host needs the mod. Guests, including players on standalone Quest, join without installing anything.

> **Testing build.** This is an early version made for testing. It has had little play so far, so expect rough edges. Problems and ideas are welcome as GitHub issues.

## Features

- **A new enemy:** the Beholder joins each floor's monster deck from the second floor on, so the game places it like any other monster. Some floors have none.
- **Its eye rays:**

  | Ray | What it does | Ready every |
  |-----|--------------|-------------|
  | **Death Beam** | The Mad Elven King's beam, in a line (5 damage at full strength) | 3 turns |
  | **Draining Kiss** | Drains a hero's life | 2 turns |
  | **Shockwave** | The Elven Queen's force blast; knocks heroes back | 3 turns |
  | **Frostball** | Ice that can freeze a hero | 2 turns |
  | **Flashbang** | A blinding flash | 2 turns |
  | **Lightning Bolt** | A bolt of lightning | 2 turns |

- **No bite:** it is an eye. 20 health, 1 armor, moves 3, sees 20 tiles.
- **It plays like a caster:** it wanders, fires a ray whenever one is ready and in reach, never fires the same ray twice in a turn, and keeps its distance like an archer.
- **The look:** a brief healing-aura flash each time it fires a ray, visual only.
- **Your eyes are safe:** the party's own all-seeing eyes (Detect Enemies, or the Scrying Beacon in Demeo Revolutions) are untouched.

## What guests see

The eye's model and its name, Eye of Avalon. Its rays are the game's own abilities, played on every machine, and its health reaches guests through the game's own syncs.

## Requirements

- Demeo on PC with BepInEx 5. Tested with Demeo 1.42 (PC VR) and BepInEx 5.4.22.
- Works on its own, or alongside other mods such as HouseRules rulesets (including Demeo Revolutions, whose monster deck it joins).

## Installation

1. Install BepInEx 5 in your Demeo folder, as for other Demeo mods.
2. With Demeo closed, put `Beholder.dll` in `BepInEx\plugins`.

## Settings

Everything is in `BepInEx\config\umbra.beholder.cfg`, created on first launch:

- **General:** `Enabled`, the master switch.
- **Beholder:**
  - `DeckCount`: how many go into each floor's deck (default 1)
  - `FromFloor`: the first floor it can appear on (default 2)
  - `Health` 20, `Armor` 1, `Attack` 4, `CritDamage` 6, `Move` 3, `Vision` 20 (health at full strength; the game scales monsters by party size)
  - `Abilities`, `Behaviours`: its rays and its AI, by the game's names
  - `Cooldowns`: its own cooldowns in its turns, `Ability: turns` (0 = none); any ray not listed gets 1
  - `RangedTags`: rays its AI should treat as ranged attacks while it is on the board (Death Beam and Flashbang)
  - `DeathBeamDamage` (5) and `DeathBeamCooldown` (-1 = the game's own): Death Beam is shared with the Mad Elven King, so these apply only while a beholder is on the board and are put back after
  - `SkipAbilityChance`: it uses a ready ray whenever one is in reach (default on)
- **Looks:**
  - `CastLook`: a look shown during each ray (default `BossHandHealState`, a healing-aura flash; not shown on the Mad Elven King's floor, where he reacts to it)
  - `Looks`: always-on looks (default none)

## How it works

When the game creates the all-seeing eye on the monsters' team, the host gives it the beholder setup: its rays, stats and AI, and it can move and be attacked. The eye already counts as a monster in every player's game, so the game's syncs read the same for guests. Its cooldowns are kept on the beholder alone, since abilities are shared with other monsters.

## Logs

`BepInEx\LogOutput.log` in the Demeo folder records its setup, each floor it is added to, and each time it appears.

## Building

```powershell
dotnet build .\Beholder\Beholder.csproj -c Release -p:GameDir="<your Demeo folder>"
```

## Credits

- Made by Umbra (Umbra13-ai).
- Built on BepInEx and Harmony.
- The beholder is a classic fantasy monster; no text or art from any game book is used.
- Demeo is made by Resolution Games. This is an unofficial fan mod, not made or endorsed by Resolution Games.

## License

MIT License. See [LICENSE](LICENSE).
