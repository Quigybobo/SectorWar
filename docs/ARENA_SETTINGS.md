# SectorWar arena settings reference

Authoritative list of every arena-scoped conf value the SectorWar umbrella
plugin reads. Built for zone admins who want to set guardrails on user-supplied
arena conf values (a la phong's cap of `BulletAliveTime` at 99999).

> **Maintenance rule.** This doc must stay in sync with the code. Any PR
> that adds, removes, renames, or changes the type / default / range of a
> conf value SectorWar reads must update this doc in the same PR. See the
> root `CLAUDE.md` for the rule and `[ConfigHelp<T>]` attributes for the
> in-source mirror.

> **Status of Min/Max columns.** Every Min/Max in this doc is a **proposed
> guardrail**, mirrored into `[ConfigHelp]` attributes in the source. Numbers
> are categorised: timers in `99999`, percents in `100`, tile coords in
> `1023`, etc. — see the [Guardrail categories](#guardrail-categories)
> section. Red-line individual rows as needed; updates must change both
> this doc and the matching attribute (CLAUDE.md rule).
>
> **`[ConfigHelp]` Min/Max is display-only, not runtime-enforced.** The
> framework surfaces these values to admin tooling (see
> `SubspaceServer/src/Core/Modules/Quickfix.cs`) but does not clamp values
> at conf-load time. A zone admin pasting `999999999999` into arena.conf
> will still get `999999999999` returned by `IConfigManager.GetInt` —
> consumer code is responsible for clamping (e.g. `Math.Clamp` or
> `Math.Max(1, …)`), and SectorWar already does this in a few hot spots
> (CTF Teams 1..9, WinCaptures min 1, StaticTurret Ship 1..8). Adding
> additional runtime clamps where guardrails matter is tracked as a
> follow-up.

> **Scope.** This doc covers **arena**-scoped settings. SectorWar also
> reads two **global** keys (`[SectorWar] BossesEnabled`,
> `[SectorWar] LinkedArenas`) that aren't arena-scoped and are listed
> separately at the bottom for completeness.

---

## Section map

SectorWar reads from five families of sections:

| Section family | Owner | Settings here |
|---|---|---|
| `[SectorWar]` | SectorWar umbrella | RPG, MoneySinks, Promotion, AutoBrick (indexed), StaticTurret (umbrella + indexed) |
| `[staticturret_<key>]` | SectorWar (per-turret-type registry) | One section per turret type listed in `StaticTurretTurret{N}` |
| `[<ShipName>]` and `[<ShipName>.Floor]` | SS.NET-conventional ship sections | Per-ship base stats (BulletSpeed, Radius, …) and ShipSettings floor/cap framework |
| `[Bullet] [Bomb] [Brick] [Damage] [Team]` | SS.NET game core | Projectile / brick / damage / team-spec defaults that SectorWar consumes |
| `[CTF]` | SS.NET FlagGame | CTF round configuration |

Ship section names recognised: `Warbird`, `Javelin`, `Spider`, `Leviathan`,
`Terrier`, `Weasel`, `Lancaster`, `Shark` — and `Spectator` (PerShipLvz only).

---

## `[SectorWar]` (umbrella section)

### RPG core

| Key | Type | Default | Min | Max | Nullable | Description | Source |
|---|---|---|---|---|---|---|---|
| `XpPerKill` | int | 100 | 0 | 1000000 | no | XP awarded to the killer on each player kill. | [SectorWar.Rpg.cs:226](../src/SectorWar/Modules/SectorWar.Rpg.cs#L226) |
| `XpPerGreen` | int | 5 | 0 | 100000 | no | XP awarded for each green prize picked up. | [SectorWar.Rpg.cs:238](../src/SectorWar/Modules/SectorWar.Rpg.cs#L238) |
| `BaseXpForLevel` | int | 250 | 1 | 1000000 | no | XP-curve coefficient. XP needed to reach level N is `BaseXpForLevel * (N-1)^2`. | [SectorWar.Rpg.cs:271](../src/SectorWar/Modules/SectorWar.Rpg.cs#L271) |
| `CreditsPerKill` | int | 50 | 0 | 1000000 | no | Credits awarded per kill. | [SectorWar.Rpg.cs:227](../src/SectorWar/Modules/SectorWar.Rpg.cs#L227) |
| `CreditsPerGreen` | int | 2 | 0 | 100000 | no | Credits awarded per green. | [SectorWar.Rpg.cs:239](../src/SectorWar/Modules/SectorWar.Rpg.cs#L239) |
| `TransferFeePercent` | int (%) | 5 | 0 | 100 | no | Fee on `?pay` transfers; vanishes (sink). | [SectorWar.Rpg.cs:395](../src/SectorWar/Modules/SectorWar.Rpg.cs#L395) |

### MoneySinks (wealth tax)

These keys live under `[SectorWar]` with the `MoneySinks` prefix. A separate
`[SectorWar.MoneySinks]` section is **silently ignored**.

| Key | Type | Default | Min | Max | Nullable | Description | Source |
|---|---|---|---|---|---|---|---|
| `MoneySinksWealthTaxIntervalSeconds` | int (sec) | 3600 | 60 | 604800 | no | Seconds between wealth-tax sweeps. (60s..1wk.) | [SectorWar.MoneySinks.cs:269](../src/SectorWar/Modules/SectorWar.MoneySinks.cs#L269) |
| `MoneySinksWealthTaxPercent` | int (%) | 1 | 0 | 100 | no | Percent of EXCESS-over-threshold taxed. | [SectorWar.MoneySinks.cs:271](../src/SectorWar/Modules/SectorWar.MoneySinks.cs#L271) |
| `MoneySinksWealthTaxThresholdCredits` | int | 1000000 | 0 | 2000000000 | no | Credit balance over this is taxed on the excess. | [SectorWar.MoneySinks.cs:273](../src/SectorWar/Modules/SectorWar.MoneySinks.cs#L273) |

### Promotion (kill-streak crown)

| Key | Type | Default | Min | Max | Nullable | Description | Source |
|---|---|---|---|---|---|---|---|
| `PromotionKillsForPromotion` | int | 5 | 1 | 999 | no | Streak length needed to earn the crown. | [SectorWar.Promotion.cs:157](../src/SectorWar/Modules/SectorWar.Promotion.cs#L157) |
| `PromotionPrizes` | string | _(empty)_ | — | — | **yes** | Space-separated `Prize` enum ints awarded on each promotion. Empty / null = no prizes. | [SectorWar.Promotion.cs:160](../src/SectorWar/Modules/SectorWar.Promotion.cs#L160) |

### AutoBrick (indexed periodic wall drops)

`{N}` is `0..31` (max 32 brick slots). Indexed key — declare per-slot as needed.

| Key pattern | Type | Default | Min | Max | Nullable | Description | Source |
|---|---|---|---|---|---|---|---|
| `AutoBrickBrick{N}` | string `x1,y1,x2,y2` | _(empty)_ | tile 0 | tile 1023 (per coord) | **yes** | Brick line endpoints in tile coords. Empty / null = slot disabled. | [SectorWar.AutoBrick.cs:149](../src/SectorWar/Modules/SectorWar.AutoBrick.cs#L149) |
| `AutoBrickTeam{N}` | int (freq) | `[Team] SpectatorFrequency` (8025) | 0 | 9999 | no | Brick freq tint for slot N. | [SectorWar.AutoBrick.cs:159](../src/SectorWar/Modules/SectorWar.AutoBrick.cs#L159) |

### StaticTurret (umbrella keys)

| Key | Type | Default | Min | Max | Nullable | Description | Source |
|---|---|---|---|---|---|---|---|
| `StaticTurretHosted` | int (bool) | 0 | 0 | 1 | no | 1 = bots are player-hosted; 0 = NPC-managed. | [SectorWar.StaticTurret.cs:460](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L460) |
| `StaticTurretMaxBots` | int | -1 | -1 | 999 | no | Max concurrent turret bots. -1 = unlimited. | [SectorWar.StaticTurret.cs:461](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L461) |
| `StaticTurretShipFavour` | int (1-based) | 0 | 0 | 8 | no | Preferred ship index for spawn. 0 = no preference; 1..8 = Warbird..Shark. | [SectorWar.StaticTurret.cs:463](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L463) |
| `StaticTurretTurretPlacementRange` | int (px) | 0 | 0 | 16384 | no | Max tile distance from host where turrets may be placed. | [SectorWar.StaticTurret.cs:465](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L465) |

### StaticTurret (indexed registry)

`{N}` is the index of a turret-type slot. The string value is a section
suffix that becomes `[staticturret_<value>]` for type-specific tuning.

| Key pattern | Type | Default | Min | Max | Nullable | Description | Source |
|---|---|---|---|---|---|---|---|
| `StaticTurretTurret{N}` | string | _(empty)_ | — | — | **yes** | Section suffix for turret type N. End-of-list when null. | [SectorWar.StaticTurret.cs:473](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L473) |
| `StaticTurretSpawn{N}` | string `typekey\|x\|y\|freq` | _(empty)_ | — | — | **yes** | Auto-spawn entry. Empty / null = end of list. | [SectorWar.StaticTurret.cs:698](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L698) |

---

## `[staticturret_<key>]` (per-turret-type sections)

One section per entry in `[SectorWar] StaticTurretTurret{N}`. Section name is
`staticturret_` plus the value of that key (e.g. `[staticturret_sentry]`).

| Key | Type | Default | Min | Max | Nullable | Description | Source |
|---|---|---|---|---|---|---|---|
| `Name` | string | `~Turret` | — | — | **yes** | Display name (null falls back to `~Turret`). | [SectorWar.StaticTurret.cs:490](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L490) |
| `Energy` | int | 1000 | 0 | 32767 | no | Starting energy capacity. | [SectorWar.StaticTurret.cs:492](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L492) |
| `Recharge` | int | 1150 | 0 | 32767 | no | Per-tick energy recharge. | [SectorWar.StaticTurret.cs:493](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L493) |
| `BuildSpeed` | int | 1000 | 0 | 32767 | no | Recharge during build sequence. | [SectorWar.StaticTurret.cs:494](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L494) |
| `WeaponType` | int (enum) | 0 (Bullet) | 0 | 6 | no | Weapon code: 0=Bullet, 1=Bomb, 3=Mine, 4=Thor, 5=Bounce, 6=Prox. Out-of-range falls back to Bullet. | [SectorWar.StaticTurret.cs:495](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L495) |
| `WeaponLevel` | int (1-based) | 1 | 1 | 4 | no | Weapon damage level (1..4 internally 0..3). | [SectorWar.StaticTurret.cs:496](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L496) |
| `WeaponDelay` | int (ms) | 100 | 1 | 99999 | no | Milliseconds between fire events. | [SectorWar.StaticTurret.cs:497](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L497) |
| `WeaponFireEnergy` | int | 0 | 0 | 32767 | no | Energy cost per fire. 0 = free. | [SectorWar.StaticTurret.cs:498](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L498) |
| `WeaponSightPixels` | int (px) | 160 | 0 | 16384 | no | Target acquisition range in pixels. | [SectorWar.StaticTurret.cs:499](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L499) |
| `WeaponShrapnelLevel` | int (1-based) | 1 | 1 | 4 | no | Shrapnel burst level. | [SectorWar.StaticTurret.cs:500](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L500) |
| `WeaponShrapnelCount` | int | 0 | 0 | 31 | no | Pellets per burst. 0 = no shrapnel. | [SectorWar.StaticTurret.cs:501](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L501) |
| `WeaponShrapnelBouncing` | int (bool) | 0 | 0 | 1 | no | 1 = shrapnel bounces. | [SectorWar.StaticTurret.cs:502](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L502) |
| `WeaponMultifire` | int (bool) | 0 | 0 | 1 | no | 1 = fire multiple times per trigger. | [SectorWar.StaticTurret.cs:503](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L503) |
| `WeaponWaitForGoodShot` | int (bool) | 0 | 0 | 1 | no | 1 = withhold fire until high-confidence shot. | [SectorWar.StaticTurret.cs:504](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L504) |
| `RotationSpeed` | int (deg/tick) | -1 | -1 | 360 | no | -1 = instant spin-to-target; otherwise degrees per tick. | [SectorWar.StaticTurret.cs:505](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L505) |
| `Timeout` | int (ms) | 1500 | 0 | 99999 | no | Auto-despawn delay if idle (no target). | [SectorWar.StaticTurret.cs:506](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L506) |
| `RespawnDelay` | int (ms) | 6000 | 0 | 999999 | no | Delay before destroyed turret respawns. | [SectorWar.StaticTurret.cs:507](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L507) |
| `XRadar` | int (bool) | 1 | 0 | 1 | no | 1 = X-radar vision (sees cloaks). | [SectorWar.StaticTurret.cs:508](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L508) |
| `RequiredPower` | int | 0 | 0 | 999 | no | Power-grid units consumed per turret. 0 = no requirement. | [SectorWar.StaticTurret.cs:509](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L509) |
| `RespawnCount` | int | 1 | -1 | 999 | no | Max respawns. -1 = infinite. | [SectorWar.StaticTurret.cs:510](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L510) |
| `Ufo` | int (bool) | 0 | 0 | 1 | no | 1 = invisible on radar. | [SectorWar.StaticTurret.cs:511](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L511) |
| `Bounty` | int | 0 | 0 | 9999 | no | Kill bounty (flags) when destroyed. | [SectorWar.StaticTurret.cs:512](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L512) |
| `MaxBots` | int | -1 | -1 | 999 | no | Max concurrent instances of this type. -1 = unlimited. | [SectorWar.StaticTurret.cs:513](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L513) |
| `DoBuildSequence` | int (bool) | 0 | 0 | 1 | no | 1 = play build animation on spawn. | [SectorWar.StaticTurret.cs:514](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L514) |
| `Ship` | int (1-based) | 1 | 1 | 8 | no | Visual ship type. 1=Warbird … 8=Shark. Out-of-range clamped. | [SectorWar.StaticTurret.cs:515](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L515) |
| `ShipRadius` | int (px) | 14 | 1 | 256 | no | Collision-radius override. | [SectorWar.StaticTurret.cs:527](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L527) |
| `OverlayImageIndex` | int (lvz id) | -1 | -1 | 32767 | no | LVZ image index to overlay on the turret. -1 = no overlay. | [SectorWar.StaticTurret.cs:533](../src/SectorWar/Modules/SectorWar.StaticTurret.cs#L533) |

---

## Per-ship sections — `[Warbird] [Javelin] [Spider] [Leviathan] [Terrier] [Weasel] [Lancaster] [Shark]`

SectorWar reads SS.NET-conventional ship-section keys directly. These are
shared with the host server's framework, so the framework's own
`[ConfigHelp]` declarations may already cap them — values below should
*agree with* (and not exceed) framework limits.

| Key | Type | Default | Min | Max | Nullable | Description | Source |
|---|---|---|---|---|---|---|---|
| `BulletSpeed` | int (px/sec) | 2000 | 0 | 10000 | no | Bullet projectile speed. (See conflict note below.) | [SectorWar.Damage.cs:221](../src/SectorWar/Modules/SectorWar.Damage.cs#L221) |
| `BombSpeed` | int (px/sec) | 2000 | 0 | 10000 | no | Bomb projectile speed. | [SectorWar.Damage.cs:222](../src/SectorWar/Modules/SectorWar.Damage.cs#L222) |
| `Radius` | int (px) | 14 | 1 | 256 | no | Collision radius. | [SectorWar.Damage.cs:223](../src/SectorWar/Modules/SectorWar.Damage.cs#L223) |
| `InitialEnergy` | int | 1000 | 0 | 32767 | no | Spawn energy. | [SectorWar.Damage.cs:257](../src/SectorWar/Modules/SectorWar.Damage.cs#L257) |
| `MaximumRecharge` | int | 1150 | 0 | 32767 | no | Max recharge rate per tick. | [SectorWar.Damage.cs:260](../src/SectorWar/Modules/SectorWar.Damage.cs#L260) |
| `ShowLvz` | int (lvz id) | -1 | -1 | 32767 | no | LVZ object id to toggle on when entering this ship. -1 = none. Also read for `[Spectator]`. | [SectorWar.PerShipLvz.cs:147](../src/SectorWar/Modules/SectorWar.PerShipLvz.cs#L147) |

**ShipSettings cap reads** (also from `[<ShipName>]`): the floor-cap framework
reads any of `MaximumEnergy`, `MaximumRecharge`, `MaximumThrust`,
`MaximumSpeed`, `MaximumRotation`, `BulletSpeed`, `BombSpeed`, `Radius` as
the **upper cap** (default `int.MaxValue`). Source:
[SectorWar.ShipSettings.cs:294](../src/SectorWar/Modules/SectorWar.ShipSettings.cs#L294).

> **⚠ Conflict note: `[<Ship>] BulletSpeed` default.** `SectorWar.Damage.cs`
> uses default `2000` and `SectorWar.StaticTurret.cs:542` uses default
> `3000` for the same `[<Ship>] BulletSpeed` key when the explicit value is
> missing. If a zone admin omits the key, the two subsystems disagree.
> Track for normalisation in a follow-up — guardrails should still allow
> the full `0..10000` range.

### Per-ship floor sections — `[Warbird.Floor]` etc.

ShipSettings reads the same eight tracked keys (`MaximumEnergy`,
`MaximumRecharge`, `MaximumThrust`, `MaximumSpeed`, `MaximumRotation`,
`BulletSpeed`, `BombSpeed`, `Radius`) from the floor sibling section as the
**lower floor** (default `-1` = no floor for that key). Source:
[SectorWar.ShipSettings.cs:291](../src/SectorWar/Modules/SectorWar.ShipSettings.cs#L291),
[L357](../src/SectorWar/Modules/SectorWar.ShipSettings.cs#L357).

| Key | Type | Default | Min | Max | Nullable | Description |
|---|---|---|---|---|---|---|
| any of the 8 tracked keys | int | -1 (no floor) | -1 | 32767 | no | Lower bound that items/modifiers add to. -1 disables the floor for this (ship, key). |

---

## SS.NET game-core sections — `[Bullet] [Bomb] [Brick] [Damage] [Team]`

These are framework-owned but SectorWar reads them. The framework already
declares `[ConfigHelp]` for most — the values below must agree with the
framework's bounds. (Verify against
`SubspaceServer/src/Core/` before adding our own attributes.)

| Section | Key | Type | Default | Min | Max | Nullable | Description | Source |
|---|---|---|---|---|---|---|---|---|
| `Bullet` | `BulletDamageLevel` | int | 200 | 0 | 32767 | no | Base damage on bullet hit. | [SectorWar.Damage.cs:212](../src/SectorWar/Modules/SectorWar.Damage.cs#L212) |
| `Bullet` | `BulletDamageUpgrade` | int | 100 | 0 | 32767 | no | Damage multiplier per weapon upgrade level. | [SectorWar.Damage.cs:213](../src/SectorWar/Modules/SectorWar.Damage.cs#L213) |
| `Bullet` | `BulletAliveTime` | int (ms) | 550 | 0 | 99999 | no | **(phong's reference cap.)** Bullet projectile lifetime. | [SectorWar.Damage.cs:214](../src/SectorWar/Modules/SectorWar.Damage.cs#L214) |
| `Bomb` | `BombDamageLevel` | int | 750 | 0 | 32767 | no | Base damage on bomb hit. | [SectorWar.Damage.cs:215](../src/SectorWar/Modules/SectorWar.Damage.cs#L215) |
| `Bomb` | `BombAliveTime` | int (ms) | 800 | 0 | 99999 | no | Bomb projectile lifetime. | [SectorWar.Damage.cs:216](../src/SectorWar/Modules/SectorWar.Damage.cs#L216) |
| `Brick` | `BrickTime` | int (ms) | 12000 | 100 | 999999 | no | Brick wall persistence. AutoBrick refresh schedule depends on this. | [SectorWar.AutoBrick.cs:174](../src/SectorWar/Modules/SectorWar.AutoBrick.cs#L174) |
| `Damage` | `IgnoreTeamDamage` | int (bool) | 0 | 0 | 1 | no | 1 = team-damaged players don't damage SectorWar fakes. | [SectorWar.Damage.cs:211](../src/SectorWar/Modules/SectorWar.Damage.cs#L211) |
| `Team` | `SpectatorFrequency` | int (freq) | 8025 | 0 | 9999 | no | Default freq for AutoBrickTeam{N}. Read indirectly. | [SectorWar.AutoBrick.cs:143](../src/SectorWar/Modules/SectorWar.AutoBrick.cs#L143) |

---

## `[CTF]` (FlagGame conventional)

| Key | Type | Default | Min | Max | Nullable | Description | Source |
|---|---|---|---|---|---|---|---|
| `Teams` | int | 2 | 1 | 9 | no | Number of CTF teams. **Already clamped 1..9 in code.** | [SectorWar.Ctf.cs:166](../src/SectorWar/Modules/SectorWar.Ctf.cs#L166) |
| `WinCaptures` | int | 3 | 1 | 999 | no | Captures needed to win. **Already `Math.Max(1, …)` in code.** | [SectorWar.Ctf.cs:168](../src/SectorWar/Modules/SectorWar.Ctf.cs#L168) |
| `NeutAfterKill` | int (bool) | 0 | 0 | 1 | no | 1 = flags become neutral when carrier dies. | [SectorWar.Ctf.cs:169](../src/SectorWar/Modules/SectorWar.Ctf.cs#L169) |
| `Team{N}-X` | int (tile) | 502 | 0 | 1023 | no | Per-team flag spawn tile X. `{N}` is `0..(Teams-1)`. | [SectorWar.Ctf.cs:177](../src/SectorWar/Modules/SectorWar.Ctf.cs#L177) |
| `Team{N}-Y` | int (tile) | 512 | 0 | 1023 | no | Per-team flag spawn tile Y. | [SectorWar.Ctf.cs:178](../src/SectorWar/Modules/SectorWar.Ctf.cs#L178) |
| `Team{N}-Region` | string | _(none)_ | — | — | **yes** | Region name for team's base. Null = no region. | [SectorWar.Ctf.cs:179](../src/SectorWar/Modules/SectorWar.Ctf.cs#L179) |
| `Team{N}-Name` | string | `Team {N}` | — | — | **yes** | Team display name. Null falls back to `Team {N}`. | [SectorWar.Ctf.cs:182](../src/SectorWar/Modules/SectorWar.Ctf.cs#L182) |

---

## Bool-as-int flags

Per phong: "alot of times we just use int as bool as well (0, 1 or null)."
The following int settings are checked via `!= 0` in code, so any non-zero
value is treated as true. Min 0, Max 1 in admin tooling unless you want to
preserve the legacy "any non-zero is true" semantic.

- `[CTF] NeutAfterKill`
- `[Damage] IgnoreTeamDamage`
- `[SectorWar] StaticTurretHosted`
- `[staticturret_*] WeaponShrapnelBouncing`
- `[staticturret_*] WeaponMultifire`
- `[staticturret_*] WeaponWaitForGoodShot`
- `[staticturret_*] XRadar`
- `[staticturret_*] Ufo`
- `[staticturret_*] DoBuildSequence`
- `[SectorWar] BossesEnabled` (global — see below)

---

## Guardrail categories (proposed Min/Max rationale)

| Category | Min | Max | Examples |
|---|---|---|---|
| Timer (ms, projectile/anim) | 0 | 99999 | `BulletAliveTime`, `BombAliveTime`, `WeaponDelay`, `Timeout` |
| Timer (ms, long-lived) | 100 | 999999 | `BrickTime`, `RespawnDelay` |
| Timer (sec) | 60 | 604800 | `MoneySinksWealthTaxIntervalSeconds` |
| Count (small) | 0 | 999 | `RespawnCount`, `RequiredPower`, `MaxBots`, `WinCaptures`, `PromotionKillsForPromotion` |
| Count (medium, currency) | 0 | 1000000 | `XpPerKill`, `CreditsPerKill`, `BaseXpForLevel` |
| Count (large, currency) | 0 | 2000000000 | `MoneySinksWealthTaxThresholdCredits` |
| Percent | 0 | 100 | `TransferFeePercent`, `MoneySinksWealthTaxPercent` |
| Tile coord (subspace 1024×1024 map) | 0 | 1023 | `Team{N}-X/-Y`, `AutoBrickBrick{N}` parts |
| Pixel range (subspace ~16384×16384 px) | 0 | 16384 | `WeaponSightPixels`, `StaticTurretTurretPlacementRange` |
| Pixel speed (px/sec) | 0 | 10000 | `BulletSpeed`, `BombSpeed` |
| Pixel radius | 1 | 256 | `Radius`, `ShipRadius` |
| Energy / damage / recharge (signed short) | 0 | 32767 | `Energy`, `Recharge`, `BulletDamageLevel`, `MaximumRecharge` |
| LVZ object id (signed short) | -1 | 32767 | `ShowLvz`, `OverlayImageIndex` |
| Frequency | 0 | 9999 | `AutoBrickTeam{N}`, `SpectatorFrequency` |
| Bool (int 0/1) | 0 | 1 | see [Bool-as-int flags](#bool-as-int-flags) |
| Ship index (1-based 1..8) | 1 | 8 | `[staticturret_*] Ship`, `StaticTurretShipFavour` (0..8 with 0=none) |
| Weapon enum | 0 | 6 | `[staticturret_*] WeaponType` |
| Weapon level (1-based) | 1 | 4 | `WeaponLevel`, `WeaponShrapnelLevel` |
| Shrapnel pellets | 0 | 31 | `WeaponShrapnelCount` |

These are starting points to be red-lined per row before they become
`[ConfigHelp]` attributes in code.

---

## Out of arena scope: global `[SectorWar]` keys

Two `[SectorWar]` keys are read from **global** scope (zone-wide), not
per-arena. Listed here for completeness — they don't belong in arena.conf.

| Section | Key | Type | Default | Min | Max | Nullable | Description | Source |
|---|---|---|---|---|---|---|---|---|
| `SectorWar` (global) | `BossesEnabled` | int (bool) | 0 | 0 | 1 | no | Master gate for boss encounters across the zone. | [SectorWar.State.cs:132](../src/SectorWar/Modules/SectorWar.State.cs#L132) |
| `SectorWar` (global) | `LinkedArenas` | string | _(hardcoded list)_ | — | — | **yes** | Comma-separated arena names tracked by SectorWarState. Null falls back to the built-in list. | [SectorWar.State.cs:196](../src/SectorWar/Modules/SectorWar.State.cs#L196) |

---

## Subsystems that read NO conf

For completeness, the following subsystems read no arena (or global) conf
values today: ArenaDefenses, BossEncounter, CompositeHitbox, DeployableShop,
DevCommands, FreqChangeWarp, GunTurret, HullVisuals, Inventory, Market,
ModularShip, Motd, PowerGrid, Pylon, SectorClaim, SectorClaimVisual,
StationDeployer, WarpIn. Any conf they grow in the future must be added to
this doc in the same PR (see `CLAUDE.md`).
