# SectorWar conf reference

The plugin reads ONE arena.conf section: `[SectorWar]`. Almost every gameplay
knob (XP curve, transfer fees, wealth tax, claim percentages, boss HP, …)
lives there with a subsystem-prefix on the key name.

Some sections stay in their SS.NET-conventional locations because the host
server (or its core modules) own them — see [Documented exceptions](#documented-exceptions)
below.

---

## `[SectorWar]` keys (consolidated)

Each subsystem prefixes its keys with its name so they don't collide. Subsystems
that read no conf at all are omitted from this list.

### RPG core (XP, levels, credits, prestige)

| Key | Default | Meaning |
|---|---|---|
| `XpPerKill` | `100` | XP awarded to the killer on each player kill. |
| `XpPerGreen` | `5` | XP awarded for each green prize picked up. |
| `BaseXpForLevel` | `250` | XP needed to reach level N is `BaseXpForLevel * (N-1)^2`. So at default, level 2 = 250 XP, level 3 = 1000, level 4 = 2250. |
| `CreditsPerKill` | `50` | Credits awarded per kill. |
| `CreditsPerGreen` | `2` | Credits awarded per green. |
| `TransferFeePercent` | `5` | Fee taken on `?pay` transfers. Fee vanishes (sink). |

### MoneySinks

Note: these keys live under `[SectorWar]` with the `MoneySinks` prefix. A
separate `[SectorWar.MoneySinks]` section is **silently ignored** by the
consolidated subsystem — the keys must be in `[SectorWar]` or they fall back
to the defaults below. See [Migration](#migration-from-a-legacy-aphelionrpg-or-sectorwarmoneysinks-zone).

| Key | Default | Meaning |
|---|---|---|
| `MoneySinksWealthTaxIntervalSeconds` | `3600` | Period between wealth-tax sweeps. |
| `MoneySinksWealthTaxPercent` | `1` | Percent of EXCESS-over-threshold taxed. |
| `MoneySinksWealthTaxThresholdCredits` | `1000000` | Players with credits over this threshold pay tax on the excess. |

### Promotion (kill-streak crown)

| Key | Default | Meaning |
|---|---|---|
| `PromotionKillsForPromotion` | `5` | Streak length needed to earn the crown. |
| `PromotionPrizes` | _(empty)_ | Space-separated `Prize` enum ints; given on promotion alongside the crown. |

### AutoBrick (periodic wall drops)

| Key | Default | Meaning |
|---|---|---|
| `AutoBrickBrick0` … `AutoBrickBrick31` | _(empty)_ | `x1,y1,x2,y2` brick coords (max 32). |
| `AutoBrickTeam0` … `AutoBrickTeam31` | `[Team] SpectatorFrequency` | Per-brick freq tint. |

### CtfGame, ShipSettings, PerShipLvz

These read from non-`[SectorWar]` sections — see exceptions below.

### Other subsystems

Boss tuning, claim percentages, pylon power radius, etc. are still hardcoded
in the first slice but will move under `[SectorWar]` with their subsystem
prefix as they're extracted to conf. Until then, change them in-source.

---

## Documented exceptions (sections NOT moved to `[SectorWar]`)

| Section | Why it stays |
|---|---|
| `[<ShipName>]` (e.g. `[Warbird]`, `[Spider]`) | SS.NET-conventional. Per-ship base stats, `BulletSpeed`, `BombSpeed`, `Radius`, `MaximumEnergy`, etc. Forcing under `[SectorWar]` would worsen the migration story for zone admins who already organise around per-ship sections. |
| `[<ShipName>.Floor]` | Ship-floor framework subsection that pairs with the ship section. Holds floor (baseline) values that items add to. Stays sibling of `[<ShipName>]`. |
| `[<ShipName>] ShowLvz` | PerShipLvz reads this where SS.NET expects ship-specific LVZ keys. |
| `[Bullet]` | SS.NET game-core's bullet-tuning section (`BulletDamageLevel`, `BulletAliveTime`, etc.). |
| `[Bomb]` | SS.NET game-core's bomb-tuning section. |
| `[Brick]` | SS.NET Bricks core's `BrickTime` lives here. AutoBrick consumes it. |
| `[Team]` | SS.NET FreqManager's `SpectatorFrequency` lives here. |
| `[CTF]` | Flag-game's own well-known surface (`Teams`, `WinCaptures`, `NeutAfterKill`, per-team `TeamN-X/-Y/-Region/-Name`). |
| `[staticturret_<key>]` | Per-turret-type registry sections — one section per turret kind in the catalogue. Renaming would break turret-type lookup. |

---

## Migration from a legacy `[Aphelion.RPG]` or `[SectorWar.MoneySinks]` zone

Zones that ran the previous standalone-modules build had two sections:
`[Aphelion.RPG]` (RPG core) and `[Aphelion.RPG.MoneySinks]` (wealth tax).
Migration:

```diff
- [Aphelion.RPG]
+ [SectorWar]
  XpPerKill = 100
  XpPerGreen = 5
  BaseXpForLevel = 250
  CreditsPerKill = 50
  CreditsPerGreen = 2
  TransferFeePercent = 5

- [Aphelion.RPG.MoneySinks]
- WealthTaxIntervalSeconds = 3600
- WealthTaxPercent = 1
- WealthTaxThresholdCredits = 1000000
+ [SectorWar]
+ MoneySinksWealthTaxIntervalSeconds = 3600
+ MoneySinksWealthTaxPercent = 1
+ MoneySinksWealthTaxThresholdCredits = 1000000
```

The MoneySinks consolidated subsystem reads `MoneySinks*`-prefixed keys from
`[SectorWar]`. It does **not** read a separate `[SectorWar.MoneySinks]`
section — keys placed there are silently ignored and the subsystem falls back
to defaults.

---

## Capability entries

Every `?<command>` registered by SectorWar needs a matching `cmd_<command>`
line in `conf/groupdef.dir/<group>` or SS.NET silently denies it with no log.
The complete list lives in [README.md](../README.md#install).
