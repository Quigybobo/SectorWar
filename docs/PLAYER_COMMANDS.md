# SectorWar — player commands + arena menu reference

Authoritative list of every chat command, plus the structure of the `?menu`
SelectBox UI.

**Command-name convention**: Nexus uses **spaces** between the verb and noun
(`?buy pylon`, `?shop buy`, `?ship info` — not `?buypylon` / `?shopbuy` /
`?shipinfo`). The cmd_* capability is the first token only (`cmd_buy`,
`cmd_shop`, `cmd_ship`).

Last verified against commit `8aa3cf5` (2026-05-17).

---

## Player commands (default group — anyone can run)

### RPG progression
| Command | What it does |
|---|---|
| `?sector war` | Show your level / XP / credits summary |
| `?level` | Show your current level |
| `?xp` | Show your XP progress toward next level |
| `?prestige` | Reset to level 1 for permanent +10% XP/credit gains (requires max level) |
| `?bal`, `?balance` | Show your credit balance |
| `?pay <player> <amount>` | Transfer credits (5% fee) |
| `?bal top`, `?top` | Show the wealthiest online players |
| `?ship info` | Show your current ship's stats |
| `?floor` | Show the floor (baseline) values for your current ship |

### Economy / casino
| Command | What it does |
|---|---|
| `?market` | Show the 5 ticker prices (read-only) |
| `?invest <ticker> <amount>` | Buy shares of a ticker |
| `?divest <ticker> <amount>` | Sell shares |
| `?portfolio` | Show your current holdings + value |
| `?dice <amount>` | Single-bet dice gamble (50/50, 5% house edge) |
| `?jackpot` | Show the current jackpot pool size |

### Shop + inventory (text-mode fallback for ?menu)
| Command | What it does |
|---|---|
| `?shop` | List all catalog items (text) |
| `?shop buy <id>` | Buy by item ID — spec only |
| `?shop sell <backpack#>` | Sell backpack item at 50% refund |
| `?inv`, `?inventory` | Show equipped + backpack contents |
| `?equip <backpack#> <ship>` | Equip a backpack item to a ship slot |
| `?unequip <ship> <slot>` | Unequip an item |
| `?hq` | **Open the SectorWar HQ menu (dialog UI in spec, text menu in flight) — was ?menu, renamed to avoid Nexus collision** |

### Deployable structures
| Command | What it does |
|---|---|
| `?buy pylon` | Buy a pylon (power source + claim point) |
| `?buy outpost` | Buy an outpost (5 turrets) |
| `?buy warstation` | Buy a war station (9 turrets) |
| `?deploy shop` | List what's for sale from the deployable shop |
| `?claim` | Show this arena's current pylon-claim state |

### GunTurret (player-attached fake turrets that fire when you fire)
| Command | What it does |
|---|---|
| `?list turrets` | List your equipped gun turrets |
| `?clear turrets` | Clear all your gun turrets |

### Misc
| Command | What it does |
|---|---|
| `?motd` | Show the Message of the Day |
| **`?start war`** | **Spawn both team HQs and begin a round (NEW)** |

---

## Smod / sysop admin commands

### Smod (in addition to default)
| Command | What it does |
|---|---|
| `?give <player> <amount>` | Grant or take credits (negative = take; floors at 0) |
| `?wipe arena` | Despawn all pylons + structures + turret bots + HQ state |
| `?war recycle` | End all fake players and recycle the arena (bypasses framework refusal) |
| `?start war` | Same as default |

### Sysop only (NOT in smod)
| Command | What it does |
|---|---|
| `?set motd`, `?add motd` | Replace / append to the MOTD |
| `?set ship <ship>` | Force-spawn into a specific ship |
| `?set test`, `?set show`, `?set reset` | Dev: temporary per-ship-per-player setting overrides |
| `?lvz test <id>` | Dev: toggle an LVZ object |
| `?dam test`, `?dam clear` | Dev: damage subsystem debug |
| `?deploy pylon`, `?despawn pylons`, `?list pylons`, `?upgrade pylon` | Pylon admin |
| `?deploy structure`, `?despawn structures`, `?list structures`, `?upgrade structure` | Structure admin |
| `?add turret`, `?reset turrets` | Static turret admin |
| `?sector status`, `?claim all` | Sector claim admin |
| `?capital on`, `?capital off` | Per-player capital LVZ overlay (legacy — superseded by ModularShip) |
| `?capital test`, `?capital status`, `?capital clear` | CompositeHitbox debug |
| `?module build`, `?module clear` | ModularShip debug |

---

## Arena menu (`?hq`) — UI tree

Open in spectator mode for the dialog interface (arrow keys + Enter + Esc).
Flying players get a text-mode fallback printed to chat.

```
TopMenu
├── Shop
│   ├── Engines (Mk.1 — Mk.100)
│   ├── Shields (Mk.1 — Mk.100)
│   ├── Bullets (Mk.1 — Mk.100)
│   ├── Hull (Mk.1 — Mk.100)
│   │   └── for each category:
│   │       ├── [*]/[ ] View: All
│   │       ├── [*]/[ ] View: Affordable only
│   │       ├── [*]/[ ] View: Not owned only
│   │       └── (item list per view filter)
│   └── <- Back to menu
├── Deployables (Pylons / Structures)
│   └── (list of items from IDeployableShop — pylon, outpost, warstation)
├── Inventory
│   ├── (per-ship loadout grid: which item is equipped to each of 4 slots × 8 ships)
│   ├── Backpack items (each branches to Equip → ship-picker, or Sell at 50%)
│   └── View filter: All / Equipped / Storage
├── My Stats
│   ├── Level, XP toward next, credits, prestige tier
│   └── (entry to "do prestige" if you've hit RpgPrestigeRequiredLevel)
├── Leaderboard
│   └── Top 10 wealthiest online players
├── Casino
│   ├── Dice 100 cr  (50/50, win = +90)
│   ├── Dice 1,000 cr (win = +900)
│   ├── Dice 10,000 cr (win = +9,000)
│   └── Dice 100,000 cr (win = +90,000)
├── Market
│   └── Read-only ticker view (trades via ?invest / ?divest)
└── Close menu
```

**Menu controls**: arrow keys navigate, Enter selects, Esc closes. Sub-menus all
have `<- Back to menu` as the last item.

**Spec requirement**: dialog UI requires spectator mode (Continuum's SelectBox
channel is gated on spec). Flying players see a text-mode fallback for `?shop`
and `?inv`; other items just print "spec to use this".

---

## Capability gating

SS.NET silently denies any command without a matching `cmd_<verb>` line in the
player's group capability file. The capability is the **first token** of the
command — so `?buy pylon` is gated by `cmd_buy`, not `cmd_buypylon`.

### `conf/groupdef.dir/default` (all players)
```
cmd_sector  cmd_level  cmd_xp  cmd_prestige
cmd_bal  cmd_balance  cmd_pay  cmd_top
cmd_ship  cmd_floor
cmd_market  cmd_invest  cmd_divest  cmd_portfolio
cmd_dice  cmd_jackpot
cmd_shop  cmd_inv  cmd_inventory
cmd_equip  cmd_unequip  cmd_hq
cmd_buy  cmd_deploy  cmd_claim
cmd_list  cmd_clear
cmd_motd  cmd_start
```

### `conf/groupdef.dir/smod` (in addition to default — SS.NET groups don't auto-inherit)
```
cmd_give  privcmd_give
cmd_wipe  cmd_war
```

### `conf/groupdef.dir/sysop` (in addition to smod)
```
cmd_set  cmd_add  cmd_lvz  cmd_dam
cmd_despawn  cmd_upgrade  cmd_reset
cmd_sector  cmd_capital  cmd_module
```

**SS.NET groups do NOT inherit by default** — every cap must be listed
explicitly in each group's file unless you've configured inheritance.

**Caveat**: granting `cmd_buy` to default gives access to `?buy pylon`, `?buy
outpost`, AND `?buy warstation` (every command starting with `buy `). If you
need finer control (e.g. let default `?buy pylon` but not `?buy warstation`),
the buy commands would need to be split into separate registered commands
with distinct verbs, or the buy command's handler can do its own per-arg
capability check.

---

## Updating the tutorial poster LVZ

In-game tutorial posters (`tutorial_poster.bmp` + `tutorial_poster_b.bmp`,
both 768×768 8-bit BMPs at LVZ image indices 25 + 190) are generated by
`c:\Users\ezraj\my-zone\tools\lvz_tutorial_poster.py`. To refresh after a
command change:

1. Edit the `PAGE_A` / `PAGE_B` dicts at the top of
   `lvz_tutorial_poster.py` so the command lists match THIS doc.
2. Run: `python c:/Users/ezraj/my-zone/tools/lvz_tutorial_poster.py`
   (or `--dry-run` to preview BMPs in `c:/tmp/` without touching the LVZ).
3. The script repacks `sectorwar.lvz` in place — all other sections
   (HQ baseplate, cannon overlays, rotation atlases, ship7.png) are
   preserved verbatim.
4. Re-upload `sectorwar.lvz` to the Nexus website's LVZ slot.
