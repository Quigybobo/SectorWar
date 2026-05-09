using SS.SectorWar.Interfaces;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — DevCommands subsystem (sysop debug toolkit).
// =============================================================================
//
// PURPOSE
// -------
// Sysop-only debug commands for testing per-ship per-player setting overrides,
// LVZ toggles, ship swaps, and the Damage subsystem's bullet pipeline. Used
// during development to validate the upgrade-model spine without restarting
// the server.
//
// SOURCE
// ------
// Standalone module `Modules/DevCommands.cs` stays as a library copy.
//
// COMMANDS (all sysop-gated via groupdef.dir/sysop)
//   ?settest <Section> <Key> <Value>  — override a per-player ClientSetting
//   ?setshow <Section> <Key>          — show effective value (override or default)
//   ?setreset <Section> <Key>         — clear an override; revert to arena default
//   ?setship <1-8>                    — hot-swap caller's ship class
//   ?lvztest <id> [on|off]            — toggle LVZ object arena-wide
//   ?damtest [hp]                     — spawn a damage-tracked fake target
//   ?damclear                         — despawn all your damtest fakes
//
// RUNTIME OWNERSHIP
//   - Owned state: damtest fake list per player + LVZ toggle cache (both
//                  lock-protected).
//   - Conf keys read: NONE.
//   - Persisted data: NONE.
//   - Fakes registered: only ?damtest spawns + tears them down.
//   - Timers scheduled: NONE.
//   - Commands registered: 7 sysop commands.
//   - Broker interfaces published: NONE.
//
// CALLBACKS HOOKED: NONE.
//
// THREADING
// ---------
// Mainloop. IDamage / IFake APIs are mainloop-only.
// =============================================================================

public sealed partial class SectorWar
{
    private const string DevCommandsSetTestCommand = "settest";
    private const string DevCommandsSetShowCommand = "setshow";
    private const string DevCommandsSetResetCommand = "setreset";
    private const string DevCommandsLvzTestCommand = "lvztest";
    private const string DevCommandsDamTestCommand = "damtest";
    private const string DevCommandsDamClearCommand = "damclear";
    private const string DevCommandsSetShipCommand = "setship";

    /// <summary>One DamTest fake — tracked so ?damclear can tear it down.</summary>
    private sealed class DevCommandsDamTestFake
    {
        public Player Fake = null!;
        public int Hp;
    }

    /// <summary>Per-player active fake list. Lock-protected.</summary>
    private readonly Dictionary<Player, List<DevCommandsDamTestFake>> _devCommandsDamTestFakes = new();
    private readonly Lock _devCommandsDamTestLock = new();

    /// <summary>?lvztest toggle cache so a bare `?lvztest <id>` flips state.</summary>
    private readonly Dictionary<short, bool> _devCommandsLvzState = new();
    private readonly Lock _devCommandsLvzStateLock = new();

    /// <summary>Cached broker for IDamage lookups in damtest paths.</summary>
    private IComponentBroker? _devCommandsBroker;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadDevCommands(IComponentBroker broker)
    {
        _devCommandsBroker = broker;
        _commandManager.AddCommand(DevCommandsSetTestCommand, Command_DevCommandsSetTest);
        _commandManager.AddCommand(DevCommandsSetShowCommand, Command_DevCommandsSetShow);
        _commandManager.AddCommand(DevCommandsSetResetCommand, Command_DevCommandsSetReset);
        _commandManager.AddCommand(DevCommandsLvzTestCommand, Command_DevCommandsLvzTest);
        _commandManager.AddCommand(DevCommandsDamTestCommand, Command_DevCommandsDamTest);
        _commandManager.AddCommand(DevCommandsDamClearCommand, Command_DevCommandsDamClear);
        _commandManager.AddCommand(DevCommandsSetShipCommand, Command_DevCommandsSetShip);
        _logManager.LogM(LogLevel.Info, LogCategory, "DevCommands subsystem loaded.");
    }

    private void UnloadDevCommands(IComponentBroker broker)
    {
        _commandManager.RemoveCommand(DevCommandsSetTestCommand, Command_DevCommandsSetTest);
        _commandManager.RemoveCommand(DevCommandsSetShowCommand, Command_DevCommandsSetShow);
        _commandManager.RemoveCommand(DevCommandsSetResetCommand, Command_DevCommandsSetReset);
        _commandManager.RemoveCommand(DevCommandsLvzTestCommand, Command_DevCommandsLvzTest);
        _commandManager.RemoveCommand(DevCommandsDamTestCommand, Command_DevCommandsDamTest);
        _commandManager.RemoveCommand(DevCommandsDamClearCommand, Command_DevCommandsDamClear);
        _commandManager.RemoveCommand(DevCommandsSetShipCommand, Command_DevCommandsSetShip);
        _devCommandsBroker = null;
    }

    private void AttachDevCommands(Arena arena) { /* zone-wide */ }
    private void DetachDevCommands(Arena arena) { /* zone-wide */ }

    // -------------------------------------------------------------------------
    // ?settest — per-player setting override
    // -------------------------------------------------------------------------

    private void Command_DevCommandsSetTest(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        Player victim = target.TryGetPlayerTarget(out Player? p) ? p : player;

        Span<Range> ranges = stackalloc Range[3];
        int n = parameters.Split(ranges, ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (n < 3)
        {
            _chat.SendMessage(player,
                "Usage: ?settest <Section> <Key> <Value>  (e.g. ?settest Warbird Radius 28)");
            return;
        }

        ReadOnlySpan<char> section = parameters[ranges[0]];
        ReadOnlySpan<char> key = parameters[ranges[1]];
        ReadOnlySpan<char> valueStr = parameters[ranges[2]];

        if (!int.TryParse(valueStr, out int value))
        {
            _chat.SendMessage(player, $"Could not parse value '{valueStr.ToString()}' as integer.");
            return;
        }

        if (!_clientSettings.TryGetSettingsIdentifier(section, key, out ClientSettingIdentifier id))
        {
            _chat.SendMessage(player, $"Unknown setting: [{section.ToString()}] {key.ToString()}");
            return;
        }

        _clientSettings.OverrideSetting(victim, id, value);
        _clientSettings.SendClientSettings(victim);

        _chat.SendMessage(player,
            $"OK: set [{section.ToString()}] {key.ToString()} = {value} on {victim.Name}");
        _logManager.LogP(LogLevel.Info, LogCategory, player,
            $"settest [{section.ToString()}] {key.ToString()} = {value} on {victim.Name}");
    }

    // -------------------------------------------------------------------------
    // ?setshow — show effective value
    // -------------------------------------------------------------------------

    private void Command_DevCommandsSetShow(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        Player victim = target.TryGetPlayerTarget(out Player? p) ? p : player;

        Span<Range> ranges = stackalloc Range[2];
        int n = parameters.Split(ranges, ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (n < 2)
        {
            _chat.SendMessage(player, "Usage: ?setshow <Section> <Key>");
            return;
        }

        ReadOnlySpan<char> section = parameters[ranges[0]];
        ReadOnlySpan<char> key = parameters[ranges[1]];

        if (!_clientSettings.TryGetSettingsIdentifier(section, key, out ClientSettingIdentifier id))
        {
            _chat.SendMessage(player, $"Unknown setting: [{section.ToString()}] {key.ToString()}");
            return;
        }

        int effective = _clientSettings.GetSetting(victim, id);
        _chat.SendMessage(player, $"{victim.Name}: [{section.ToString()}] {key.ToString()} = {effective}");
    }

    // -------------------------------------------------------------------------
    // ?setreset — clear override
    // -------------------------------------------------------------------------

    private void Command_DevCommandsSetReset(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        Player victim = target.TryGetPlayerTarget(out Player? p) ? p : player;

        Span<Range> ranges = stackalloc Range[2];
        int n = parameters.Split(ranges, ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (n < 2)
        {
            _chat.SendMessage(player, "Usage: ?setreset <Section> <Key>");
            return;
        }

        ReadOnlySpan<char> section = parameters[ranges[0]];
        ReadOnlySpan<char> key = parameters[ranges[1]];

        if (!_clientSettings.TryGetSettingsIdentifier(section, key, out ClientSettingIdentifier id))
        {
            _chat.SendMessage(player, $"Unknown setting: [{section.ToString()}] {key.ToString()}");
            return;
        }

        _clientSettings.UnoverrideSetting(victim, id);
        _clientSettings.SendClientSettings(victim);

        _chat.SendMessage(player,
            $"Cleared override on [{section.ToString()}] {key.ToString()} for {victim.Name}.");
    }

    // -------------------------------------------------------------------------
    // ?setship — hot-swap ship class
    // -------------------------------------------------------------------------

    private void Command_DevCommandsSetShip(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (!byte.TryParse(parameters.Trim(), out byte n) || n < 1 || n > 8)
        {
            _chat.SendMessage(player, "Usage: ?setship <1-8>  (1=Warbird ... 8=Shark)");
            return;
        }

        ShipType ship = (ShipType)(n - 1);
        _game.SetShip(player, ship);
        _chat.SendMessage(player, $"Switched to {ship}.");
    }

    // -------------------------------------------------------------------------
    // ?lvztest — toggle LVZ object
    // -------------------------------------------------------------------------

    private void Command_DevCommandsLvzTest(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        Arena? arena = player.Arena;
        if (arena is null) return;

        Span<Range> ranges = stackalloc Range[2];
        int n = parameters.Split(ranges, ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (n < 1)
        {
            _chat.SendMessage(player, "Usage: ?lvztest <id> [on|off]");
            return;
        }

        if (!short.TryParse(parameters[ranges[0]], out short id))
        {
            _chat.SendMessage(player, "Object id must be an integer.");
            return;
        }

        bool enable;
        if (n >= 2)
        {
            ReadOnlySpan<char> arg = parameters[ranges[1]];
            if (arg.Equals("on", StringComparison.OrdinalIgnoreCase)) enable = true;
            else if (arg.Equals("off", StringComparison.OrdinalIgnoreCase)) enable = false;
            else { _chat.SendMessage(player, "Second arg must be 'on' or 'off'."); return; }
        }
        else
        {
            // Toggle from cached state. Default = visible (on), so first
            // ?lvztest on a fresh id flips it off.
            lock (_devCommandsLvzStateLock)
            {
                bool current = !_devCommandsLvzState.TryGetValue(id, out bool s) || s;
                enable = !current;
                _devCommandsLvzState[id] = enable;
            }
        }

        _lvzObjects.Toggle(arena, id, enable);
        _chat.SendMessage(player, $"LVZ object {id}: {(enable ? "ON" : "OFF")} (arena-wide).");
    }

    // -------------------------------------------------------------------------
    // ?damtest / ?damclear — Damage subsystem end-to-end test
    // -------------------------------------------------------------------------

    private void Command_DevCommandsDamTest(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        Arena? arena = player.Arena;
        if (arena is null) { _chat.SendMessage(player, "Must be in an arena."); return; }
        if (player.Ship == ShipType.Spec)
        {
            _chat.SendMessage(player, "Get into a ship first."); return;
        }

        IDamage? damage = _devCommandsBroker?.GetInterface<IDamage>();
        if (damage is null)
        {
            _chat.SendMessage(player, "Damage subsystem not loaded."); return;
        }

        try
        {
            int hp = 1000;
            if (!parameters.IsEmpty && int.TryParse(parameters.Trim(), out int parsedHp))
                hp = Math.Clamp(parsedHp, 100, 100000);

            // Spawn on freq+1 so player's bullets hit (different freq → no
            // friendly-fire skip).
            short targetFreq = (short)((player.Freq + 1) % 9999);
            string name = $"~DamTest{Random.Shared.Next(1000, 9999)}";
            Player? fake = _fake.CreateFakePlayer(name, arena, ShipType.Warbird, targetFreq);
            if (fake is null) { _chat.SendMessage(player, "Failed to create fake."); return; }

            // Place ~80px in front of the player (in player heading direction).
            int rot = ((player.Position.Rotation % 40) + 40) % 40;
            double theta = rot * (Math.PI * 2.0 / 40.0);
            double dxF = -Math.Sin(theta) * 80.0;
            double dyF = Math.Cos(theta) * 80.0 * -1.0;  // -Y is forward in screen space
            short tx = (short)(player.Position.X + (int)Math.Round(dxF));
            short ty = (short)(player.Position.Y + (int)Math.Round(dyF));

            C2S_PositionPacket pos = default;
            pos.Type = 0x03;
            pos.X = tx;
            pos.Y = ty;
            pos.Rotation = 0;
            pos.Time = ServerTick.Now;
            pos.Energy = 1000;
            _game.FakePosition(fake, ref pos);

            var entry = new DevCommandsDamTestFake { Fake = fake, Hp = hp };
            lock (_devCommandsDamTestLock)
            {
                if (!_devCommandsDamTestFakes.TryGetValue(player, out var list))
                {
                    list = new List<DevCommandsDamTestFake>();
                    _devCommandsDamTestFakes[player] = list;
                }
                list.Add(entry);
            }

            damage.AddFake(fake, ref pos, manageEnergy: false,
                killFunc: (f, killer, clos) => OnDevCommandsDamTestKilled(player, entry),
                respawnFunc: null,
                damageFunc: (f, firedBy, dist, dmg, wt, lvl, b, emp, clos) =>
                    OnDevCommandsDamTestHit(player, entry, dmg, firedBy),
                closure: entry);

            _chat.SendMessage(player,
                $"DamTest: spawned {name} (HP {hp}) ~80px ahead on freq {targetFreq}.");
        }
        finally
        {
            _devCommandsBroker?.ReleaseInterface(ref damage);
        }
    }

    private void OnDevCommandsDamTestHit(Player owner, DevCommandsDamTestFake entry,
        int dmg, Player? firedBy)
    {
        entry.Hp -= dmg;
        string firer = firedBy?.Name ?? "?";
        _chat.SendMessage(owner,
            $"DamTest hit by {firer}: -{dmg} HP, remaining {Math.Max(entry.Hp, 0)}.");

        if (entry.Hp <= 0)
        {
            // Manual KillFake — manageEnergy=false means damage subsystem
            // doesn't auto-fire it.
            IDamage? damage = _devCommandsBroker?.GetInterface<IDamage>();
            try { damage?.KillFake(entry.Fake, firedBy ?? owner); }
            finally { _devCommandsBroker?.ReleaseInterface(ref damage); }
        }
    }

    private void OnDevCommandsDamTestKilled(Player owner, DevCommandsDamTestFake entry)
    {
        _chat.SendMessage(owner, $"DamTest: {entry.Fake.Name} destroyed.");

        IDamage? damage = _devCommandsBroker?.GetInterface<IDamage>();
        try { damage?.RemoveFake(entry.Fake); }
        finally { _devCommandsBroker?.ReleaseInterface(ref damage); }
        _fake.EndFaked(entry.Fake);

        lock (_devCommandsDamTestLock)
        {
            if (_devCommandsDamTestFakes.TryGetValue(owner, out var list))
            {
                list.Remove(entry);
                if (list.Count == 0) _devCommandsDamTestFakes.Remove(owner);
            }
        }
    }

    private void Command_DevCommandsDamClear(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        List<DevCommandsDamTestFake>? list;
        lock (_devCommandsDamTestLock)
        {
            _devCommandsDamTestFakes.TryGetValue(player, out list);
            _devCommandsDamTestFakes.Remove(player);
        }
        if (list is null || list.Count == 0)
        {
            _chat.SendMessage(player, "No DamTest fakes active."); return;
        }

        IDamage? damage = _devCommandsBroker?.GetInterface<IDamage>();
        try
        {
            foreach (var entry in list)
            {
                damage?.RemoveFake(entry.Fake);
                _fake.EndFaked(entry.Fake);
            }
        }
        finally { _devCommandsBroker?.ReleaseInterface(ref damage); }
        _chat.SendMessage(player, $"Cleared {list.Count} DamTest fake(s).");
    }
}
