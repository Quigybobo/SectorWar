# SectorWar — player commands + arena menu reference

Authoritative list of every chat command the SectorWar plugin registers,
plus the structure of the `?menu` SelectBox UI. Source of truth: this file
is generated from `_commandManager.AddCommand` call sites and SelectBox
item registrations across `src/SectorWar/Modules/*.cs`. **If a command in
the in-game tutorial poster doesn't appear here, the tutorial is stale.**

Last verified against commit `66963dd` (2026-05-17).

---

## Player commands (default group — anyone can run)

### RPG progression
| Command | What it does |
|---|---|
| `?sectorwar` | Show your level / XP / credits summary |
| `?level` | Show your current level |
| `?xp` | Show your XP progress toward next level |
| `?prestige` | Reset to level 1 for permanent +10% XP/credit gains (requires max level) |
| `?bal`, `?balance` | Show your credit balance |
| `?pay <player> <amount>` | Transfer credits (5% fee) |
| `?baltop`, `?top` | Show the wealthiest online players |
| `?shipinfo` | Show your current ship's stats |
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
| `?shopbuy <id>` | Buy by item ID — spec only |
| `?shopsell <backpack#>` | Sell backpack item at 50% refund |
| `?inv`, `?inventory` | Show equipped + backpack contents |
| `?equip <backpack#> <ship>` | Equip a backpack item to a ship slot |
| `?unequip <ship> <slot>` | Unequip an item |
| `?menu` | **Open the full dialog UI (see Arena menu below)** |

### Deployable structures
| Command | What it does |
|---|---|
| `?buypylon` | Buy a pylon (power source + claim point) |
| `?buyoutpost` | Buy an outpost (5 turrets) |
| `?buywarstation` | Buy a war station (9 turrets) |
| `?deployshop` | List what's for sale from the deployable shop |
| `?claim` | Show this arena's current pylon-claim state |

### GunTurret (player-attached fake turrets that fire when you fire)
| Command | What it does |
|---|---|
| `?listturrets` | List your equipped gun turrets |
| `?clearturrets` | Clear all your gun turrets |

### Misc
| Command | What it does |
|---|---|
| `?motd` | Show the Message of the Day |
| **`?startwar`** | **Spawn both team HQs and begin a round (NEW)** |

---

## Smod / sysop admin commands

### Smod (in addition to default)
| Command | What it does |
|---|---|
| `?give <player> <amount>` | Grant or take credits (negative = take; floors at 0) |
| `?wipearena` | Despawn all pylons + structures + turret bots + HQ state |
| `?warrecycle` | End all fake players and recycle the arena (bypasses framework refusal) |
| `?startwar` | Same as default |

### Sysop only (NOT in smod)
| Command | What it does |
|---|---|
| `?setmotd`, `?addmotd` | Replace / append to the MOTD |
| `?setship <ship>` | Force-spawn into a specific ship |
| `?settest`, `?setshow`, `?setreset` | Dev: temporary per-ship-per-player setting overrides |
| `?lvztest <id>` | Dev: toggle an LVZ object |
| `?damtest`, `?damclear` | Dev: damage subsystem debug |
| `?deploypylon`, `?despawnpylons`, `?listpylons`, `?upgradepylon` | Pylon admin |
| `?deploystructure`, `?despawnstructures`, `?liststructures`, `?upgradestructure` | Structure admin |
| `?addturret`, `?resetturrets` | Static turret admin |
| `?sectorstatus`, `?claimall` | Sector claim admin |
| `?capitalon`, `?capitaloff` | Per-player capital LVZ overlay (legacy — superseded by ModularShip) |
| `?capitaltest`, `?capitalstatus`, `?capitalclear` | CompositeHitbox debug |
| `?modulebuild`, `?moduleclear` | ModularShip debug |

---

## Arena menu (`?menu`) — UI tree

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

SS.NET silently denies any command without a matching `cmd_<name>` line in the
player's group capability file. The minimum entries needed in each group:

### `conf/groupdef.dir/default` (all players)
```
cmd_sectorwar  cmd_level  cmd_xp  cmd_prestige
cmd_bal  cmd_balance  cmd_pay  cmd_baltop  cmd_top
cmd_shipinfo  cmd_floor
cmd_market  cmd_invest  cmd_divest  cmd_portfolio
cmd_dice  cmd_jackpot
cmd_shop  cmd_shopbuy  cmd_shopsell  cmd_inv  cmd_inventory
cmd_equip  cmd_unequip  cmd_menu
cmd_buypylon  cmd_buyoutpost  cmd_buywarstation  cmd_deployshop
cmd_claim  cmd_listturrets  cmd_clearturrets
cmd_motd  cmd_startwar
```

### `conf/groupdef.dir/smod` (add to inherited default)
```
cmd_give  privcmd_give
cmd_wipearena  cmd_warrecycle
```

### `conf/groupdef.dir/sysop` (add to smod)
```
cmd_setmotd  cmd_addmotd
cmd_addturret  cmd_resetturrets
cmd_setship  cmd_settest  cmd_setshow  cmd_setreset  cmd_lvztest
cmd_damtest  cmd_damclear
cmd_deploypylon  cmd_despawnpylons  cmd_listpylons  cmd_upgradepylon
cmd_deploystructure  cmd_despawnstructures  cmd_liststructures  cmd_upgradestructure
cmd_sectorstatus  cmd_claimall
cmd_capitalon  cmd_capitaloff
cmd_capitaltest  cmd_capitalstatus  cmd_capitalclear
cmd_modulebuild  cmd_moduleclear
```

**SS.NET groups do NOT inherit by default** — every cap must be listed
explicitly in each group's file unless you've configured inheritance.

---

## Updating the tutorial poster LVZ

The in-game tutorial poster (`tutorial_poster.bmp` + `tutorial_poster_b.bmp`,
both 768×768 8-bit BMPs at LVZ image indices 25 + 190) is a hand-authored
image — there is no generator script in `c:\Users\ezraj\my-zone\tools\`. To
update the command list shown on it, you need to:

1. Extract the current poster from the LVZ (or grab `c:/tmp/steam_sectorwar_extract/tutorial_poster.bmp`)
2. Open in an image editor (GIMP / Photoshop / Aseprite — anything that exports
   indexed BMP). Update the command list to match this doc.
3. Re-export as **256-color (8-bit) BMP, 768×768**, same color palette
4. Repack into `sectorwar.lvz` (replace the existing section using a tool similar
   to `c:\tmp\merge_ship7.py` — same script pattern, different section)
5. Re-upload the LVZ via the Nexus website

Want this automated? Ask for a `lvz_tutorial_poster.py` generator that bakes
the command list into the BMP from a text template — same approach as
`lvz_warbird_capital.py` does for the ship sprite. ~150 lines of Python.
