# SectorWar — groupdef command list

Paste-ready groupdef entries for the SectorWar command surface. Every
command is registered at **arena scope** (only visible in arenas where
SectorWar is attached), so a player only needs the `cmd_X` /
`privcmd_X` permission in groupdefs that apply to those arenas.

- `cmd_X` — public form (`?X` or `.X` in arena chat).
- `privcmd_X` — private-message form (`/X` to a specific player).

Both forms are listed for every command. Strip whichever ones your
groupdef doesn't grant. Commands that fundamentally don't make sense
as private messages (e.g. zone-wide `?wipearena`) still have their
`privcmd_` line for completeness — SS.NET's command dispatcher
gates on this attribute regardless of handler semantics.

Grouped by subsystem (alphabetical within each group). Total:
**64 commands**.

---

## CompositeHitbox

```
; CompositeHitbox (capital ship damage model)
cmd_capitaltest
privcmd_capitaltest
cmd_capitalstatus
privcmd_capitalstatus
cmd_capitalclear
privcmd_capitalclear
```

## DeployableShop

```
; DeployableShop (`?buy` shop + per-deployable buys)
cmd_buy
privcmd_buy
cmd_buypylon
privcmd_buypylon
cmd_buyoutpost
privcmd_buyoutpost
cmd_buywarstation
privcmd_buywarstation
cmd_deployshop
privcmd_deployshop
```

## DevCommands

```
; DevCommands (admin / dev diagnostic surface)
cmd_settest
privcmd_settest
cmd_setshow
privcmd_setshow
cmd_setreset
privcmd_setreset
cmd_setship
privcmd_setship
cmd_lvztest
privcmd_lvztest
cmd_damtest
privcmd_damtest
cmd_damclear
privcmd_damclear
```

## GunTurret

```
; GunTurret (player-attached gun turrets)
cmd_addturret
privcmd_addturret
cmd_resetturrets
privcmd_resetturrets
cmd_clearturrets
privcmd_clearturrets
cmd_listturrets
privcmd_listturrets
```

## HullVisuals

```
; HullVisuals (per-player Capital sprite overlay)
cmd_capitalon
privcmd_capitalon
cmd_capitaloff
privcmd_capitaloff
```

## Inventory

```
; Inventory (shop + equip + per-ship loadouts)
cmd_shop
privcmd_shop
cmd_shopbuy
privcmd_shopbuy
cmd_shopsell
privcmd_shopsell
cmd_equip
privcmd_equip
cmd_unequip
privcmd_unequip
cmd_inv
privcmd_inv
cmd_inventory
privcmd_inventory
cmd_menu
privcmd_menu
```

## Market

```
; Market (zone-wide stock-style ticker)
cmd_market
privcmd_market
cmd_invest
privcmd_invest
cmd_divest
privcmd_divest
cmd_portfolio
privcmd_portfolio
```

## ModularShip

```
; ModularShip (per-player Capital ship LVZ overlay)
cmd_modulebuild
privcmd_modulebuild
cmd_moduleclear
privcmd_moduleclear
```

## MoneySinks

```
; MoneySinks (?dice + ?jackpot)
cmd_dice
privcmd_dice
cmd_jackpot
privcmd_jackpot
```

## Motd

```
; Motd (message-of-the-day greeter)
cmd_motd
privcmd_motd
cmd_setmotd
privcmd_setmotd
cmd_addmotd
privcmd_addmotd
```

## Pylon

```
; Pylon (player-deployable power nodes)
cmd_deploypylon
privcmd_deploypylon
cmd_despawnpylons
privcmd_despawnpylons
cmd_listpylons
privcmd_listpylons
cmd_upgradepylon
privcmd_upgradepylon
cmd_wipearena
privcmd_wipearena
```

## Rpg

```
; Rpg (XP / levels / currency / prestige / pay)
cmd_sectorwar
privcmd_sectorwar
cmd_level
privcmd_level
cmd_xp
privcmd_xp
cmd_shipinfo
privcmd_shipinfo
cmd_bal
privcmd_bal
cmd_balance
privcmd_balance
cmd_baltop
privcmd_baltop
cmd_top
privcmd_top
cmd_pay
privcmd_pay
cmd_prestige
privcmd_prestige
cmd_give
privcmd_give
```

## SectorClaim

```
; SectorClaim (claim sectors by pylon majority)
cmd_claim
privcmd_claim
cmd_claimall
privcmd_claimall
```

## SectorWarState

```
; SectorWarState (cross-arena sector status)
cmd_sectorstatus
privcmd_sectorstatus
```

## ShipSettings

```
; ShipSettings (per-ship floor/cap diagnostic)
cmd_floor
privcmd_floor
```

## StationDeployer

```
; StationDeployer (player-deployable structures: outposts, war stations)
cmd_deploystructure
privcmd_deploystructure
cmd_despawnstructures
privcmd_despawnstructures
cmd_liststructures
privcmd_liststructures
cmd_upgradestructure
privcmd_upgradestructure
```

---

## All commands, alphabetical (single dump for global paste)

```
cmd_addmotd
privcmd_addmotd
cmd_addturret
privcmd_addturret
cmd_bal
privcmd_bal
cmd_balance
privcmd_balance
cmd_baltop
privcmd_baltop
cmd_buy
privcmd_buy
cmd_buyoutpost
privcmd_buyoutpost
cmd_buypylon
privcmd_buypylon
cmd_buywarstation
privcmd_buywarstation
cmd_capitalclear
privcmd_capitalclear
cmd_capitaloff
privcmd_capitaloff
cmd_capitalon
privcmd_capitalon
cmd_capitalstatus
privcmd_capitalstatus
cmd_capitaltest
privcmd_capitaltest
cmd_claim
privcmd_claim
cmd_claimall
privcmd_claimall
cmd_clearturrets
privcmd_clearturrets
cmd_damclear
privcmd_damclear
cmd_damtest
privcmd_damtest
cmd_deploypylon
privcmd_deploypylon
cmd_deployshop
privcmd_deployshop
cmd_deploystructure
privcmd_deploystructure
cmd_despawnpylons
privcmd_despawnpylons
cmd_despawnstructures
privcmd_despawnstructures
cmd_dice
privcmd_dice
cmd_divest
privcmd_divest
cmd_equip
privcmd_equip
cmd_floor
privcmd_floor
cmd_give
privcmd_give
cmd_inv
privcmd_inv
cmd_inventory
privcmd_inventory
cmd_invest
privcmd_invest
cmd_jackpot
privcmd_jackpot
cmd_level
privcmd_level
cmd_listpylons
privcmd_listpylons
cmd_liststructures
privcmd_liststructures
cmd_listturrets
privcmd_listturrets
cmd_lvztest
privcmd_lvztest
cmd_market
privcmd_market
cmd_menu
privcmd_menu
cmd_modulebuild
privcmd_modulebuild
cmd_moduleclear
privcmd_moduleclear
cmd_motd
privcmd_motd
cmd_pay
privcmd_pay
cmd_portfolio
privcmd_portfolio
cmd_prestige
privcmd_prestige
cmd_resetturrets
privcmd_resetturrets
cmd_sectorstatus
privcmd_sectorstatus
cmd_sectorwar
privcmd_sectorwar
cmd_setmotd
privcmd_setmotd
cmd_setreset
privcmd_setreset
cmd_setship
privcmd_setship
cmd_setshow
privcmd_setshow
cmd_settest
privcmd_settest
cmd_shipinfo
privcmd_shipinfo
cmd_shop
privcmd_shop
cmd_shopbuy
privcmd_shopbuy
cmd_shopsell
privcmd_shopsell
cmd_top
privcmd_top
cmd_unequip
privcmd_unequip
cmd_upgradepylon
privcmd_upgradepylon
cmd_upgradestructure
privcmd_upgradestructure
cmd_wipearena
privcmd_wipearena
```

---

## Notes for groupdef admins

- **Admin-only commands** (kept dev-restricted in our reference confs):
  `settest`, `setshow`, `setreset`, `setship`, `lvztest`, `damtest`,
  `damclear`, `setmotd`, `addmotd`, `wipearena`, `give`. These should
  typically go in `staff` / `dev` / `sysop` groups, not `default`.
- **Currency-affecting commands**: `pay`, `give`, `invest`, `divest`,
  `shopbuy`, `shopsell`, `buypylon`, `buyoutpost`, `buywarstation`,
  `dice`, `jackpot`, `upgradepylon`, `upgradestructure`. These mutate
  player balances or inventory — grant them only to groups that should
  be able to spend.
- **Deployment commands** (place world objects):
  `deploypylon`, `deploystructure`, `deployshop`, `despawnpylons`,
  `despawnstructures`, `addturret`, `clearturrets`, `resetturrets`.
- **Read-only / diagnostic** (safe for `default`):
  `bal`, `balance`, `baltop`, `top`, `xp`, `level`, `shipinfo`, `menu`,
  `inv`, `inventory`, `floor`, `motd`, `market`, `portfolio`,
  `listpylons`, `liststructures`, `listturrets`, `sectorstatus`,
  `sectorwar`, `capitalstatus`.
- **Player-facing core** (typical `default` set):
  `buy`, `shop`, `equip`, `unequip`, `claim`, `claimall`,
  `capitalon`, `capitaloff`, `modulebuild`, `moduleclear`,
  `prestige`.

Last sync: every entry above resolves to a `_commandManager.AddCommand`
call in `src/SectorWar/Modules/SectorWar.*.cs`. If a new subsystem adds
a command, append the `cmd_X` / `privcmd_X` pair under its subsystem
heading AND in the alphabetical dump, then bump this note.
