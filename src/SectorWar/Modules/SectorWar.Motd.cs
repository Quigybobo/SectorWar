using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System.Threading;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — Motd subsystem (partial-class file).
// =============================================================================
//
// PURPOSE
// -------
// Show a Message of the Day on first arena entry per player. Sysops can set
// or append text via `?setmotd` / `?addmotd`. Long messages are auto-chunked
// into 200-char segments to fit Continuum's chat line limit.
//
// SOURCE
// ------
// Port of JoWie's motd.c (ASSS, 2007–2009): bitbucket.org/jowie/asss-motd.
// v1 simplification: MOTD lives in module memory only (lost on restart). The
// original used cfg->SetStr to persist via config-file writeback. Phase 2
// could re-add persistence via IConfigManager once we settle on a storage
// path; the simplification is intentional — most operators set MOTD rarely
// and a session-only MOTD is acceptable.
//
// RELATIONSHIP TO STANDALONE `Motd.cs`
// ------------------------------------
// The standalone module at Modules/Motd.cs stays in place as a library copy.
// Both compile into the same SectorWar.dll for now. Modules.config still
// loads the standalone; the umbrella subsystem is dormant until Phase 1
// flips registration.
//
// RUNTIME OWNERSHIP
// -----------------
//   - Owned state: _motdText, _motdAuthor, _motdSetAt (zone-wide),
//                  _pdKey + MotdPlayerData (per-player HasSeenMotd flag).
//   - Conf keys read: NONE.
//   - Persisted data: NONE.
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE.
//   - Commands registered: cmd_motd (default), cmd_setmotd / cmd_addmotd
//                          (sysop) — capability gating in groupdef.dir.
//
// CALLBACKS HOOKED (zone-wide via broker, NOT per-arena)
//   - PlayerActionCallback → OnPlayerAction_Motd
//
// THREADING
// ---------
//   - PlayerActionCallback fires on the mainloop. _motdText reads use the
//     _motdLock for safety because command handlers may run on a worker
//     thread (CommandManager doesn't guarantee mainloop dispatch).
//   - SetMotd / ShowMotd both acquire _motdLock for the read or write.
//   - Lock ordering: _motdLock is a leaf — never acquire it while holding any
//     other umbrella lock.
//
// WAVE-FIXES PRESERVED
// --------------------
// None Wave-specific — this module wasn't touched in the 13-wave cleanup
// (no NREs, no slot-reuse races; pure read/write of in-memory text + a
// Continuum chat dispatch).
// =============================================================================

public sealed partial class SectorWar
{
    // -------------------------------------------------------------------------
    // CONSTANTS — command names
    // -------------------------------------------------------------------------

    /// <summary>`?motd` — anyone can run; shows the current MOTD on demand.</summary>
    private const string MotdCommand = "motd";

    /// <summary>`?setmotd &lt;text&gt;` — sysop. Replaces MOTD entirely.</summary>
    private const string SetMotdCommand = "setmotd";

    /// <summary>`?addmotd &lt;text&gt;` — sysop. Appends to MOTD with " | " separator.</summary>
    private const string AddMotdCommand = "addmotd";

    // -------------------------------------------------------------------------
    // SUBSYSTEM-OWNED STATE
    // -------------------------------------------------------------------------

    /// <summary>Per-player flag preventing the MOTD from showing twice in
    /// a single connection. Reset on player disconnect via
    /// <see cref="MotdPlayerData.TryReset"/>.</summary>
    private PlayerDataKey<MotdPlayerData> _motdPdKey;

    /// <summary>Current MOTD body. Empty string = no MOTD configured (silently
    /// suppressed in <see cref="ShowMotd"/>).</summary>
    private string _motdText = "";

    /// <summary>Player who last set the MOTD (for audit logging).</summary>
    private string _motdAuthor = "";

    /// <summary>UTC timestamp of last set. Reserved for a future `?motd info`
    /// admin command.</summary>
    private DateTime _motdSetAt = DateTime.MinValue;

    /// <summary>Guards <see cref="_motdText"/>/Author/SetAt across the
    /// command-thread → mainloop boundary.</summary>
    private readonly Lock _motdLock = new();

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Allocates the per-player data key, registers commands, hooks
    /// <see cref="PlayerActionCallback"/>. Zone-wide — Motd does not require
    /// arena attachment because the MOTD is global to the zone, not per-arena.
    /// </summary>
    /// <remarks>
    /// Threading: mainloop. <see cref="ICommandManager.AddCommand"/> and
    /// <see cref="PlayerActionCallback.Register"/> are mainloop-safe.
    /// </remarks>
    private void LoadMotd(IComponentBroker broker)
    {
        _motdPdKey = _playerData.AllocatePlayerData<MotdPlayerData>();
        PlayerActionCallback.Register(broker, OnPlayerAction_Motd);
        _commandManager.AddCommand(MotdCommand, Command_Motd);
        _commandManager.AddCommand(SetMotdCommand, Command_SetMotd);
        _commandManager.AddCommand(AddMotdCommand, Command_AddMotd);
        _logManager.LogM(LogLevel.Info, LogCategory, "Motd subsystem loaded.");
    }

    /// <summary>Reverse of Load. Symmetric command + callback removal so a
    /// hot-reload doesn't leak handlers into the next instance.</summary>
    private void UnloadMotd(IComponentBroker broker)
    {
        _commandManager.RemoveCommand(MotdCommand, Command_Motd);
        _commandManager.RemoveCommand(SetMotdCommand, Command_SetMotd);
        _commandManager.RemoveCommand(AddMotdCommand, Command_AddMotd);
        PlayerActionCallback.Unregister(broker, OnPlayerAction_Motd);
        _playerData.FreePlayerData(ref _motdPdKey);
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH
    //
    // Motd is zone-wide, not arena-scoped. Attach/Detach are no-ops; included
    // for symmetry with the umbrella's lifecycle pattern.
    // -------------------------------------------------------------------------

    private void AttachMotd(Arena arena) { /* zone-wide, no per-arena work */ }
    private void DetachMotd(Arena arena) { /* zone-wide, no per-arena work */ }

    // -------------------------------------------------------------------------
    // CALLBACK
    // -------------------------------------------------------------------------

    /// <summary>
    /// Show MOTD on first EnterArena per player-connection. The
    /// <c>HasSeenMotd</c> flag survives arena hops so re-entering the same
    /// arena does NOT re-show the MOTD; it resets on disconnect.
    /// </summary>
    private void OnPlayerAction_Motd(Player player, PlayerAction action, Arena? arena)
    {
        if (player is null || arena is null || player.Arena != arena) return;
        if (action != PlayerAction.EnterArena) return;
        if (!player.TryGetExtraData(_motdPdKey, out MotdPlayerData? pd)) return;

        if (!pd.HasSeenMotd)
        {
            pd.HasSeenMotd = true;
            ShowMotd(player);
        }
    }

    // -------------------------------------------------------------------------
    // COMMAND HANDLERS
    //
    // CommandManager.AddCommand is name-keyed; capability gating happens in
    // CapabilityManager via cmd_<name> entries in groupdef.dir/<group>. Any
    // operator deploying the consolidated SectorWar must keep cmd_motd in
    // `default` and cmd_setmotd/cmd_addmotd in `sysop` (or wherever they want).
    // -------------------------------------------------------------------------

    [CommandHelp(Targets = CommandTarget.None, Args = null,
        Description = "Display the Message of the Day.")]
    private void Command_Motd(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters,
        Player player, ITarget target)
    {
        ShowMotd(player);
    }

    [CommandHelp(Targets = CommandTarget.None, Args = "<message>",
        Description = "Sysop: replace the MOTD entirely.")]
    private void Command_SetMotd(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters,
        Player player, ITarget target)
    {
        if (parameters.IsWhiteSpace())
        {
            _chat.SendMessage(player, "Usage: ?setmotd <message>");
            return;
        }
        SetMotd(parameters.ToString(), player.Name ?? "?");
        _chat.SendMessage(player, "MOTD set.");
    }

    [CommandHelp(Targets = CommandTarget.None, Args = "<message>",
        Description = "Sysop: append text to the existing MOTD with a pipe separator.")]
    private void Command_AddMotd(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters,
        Player player, ITarget target)
    {
        if (parameters.IsWhiteSpace())
        {
            _chat.SendMessage(player, "Usage: ?addmotd <message>");
            return;
        }

        // Read current under lock, build new text outside the lock, then
        // write under lock. Keeps the critical section tight (just the field
        // copy/assign) and avoids holding the lock during string concatenation.
        string current;
        lock (_motdLock) { current = _motdText; }

        string combined = string.IsNullOrEmpty(current)
            ? parameters.ToString()
            : $"{current} | {parameters}";

        SetMotd(combined, player.Name ?? "?");
        _chat.SendMessage(player, "MOTD set.");
    }

    // -------------------------------------------------------------------------
    // INTERNAL HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Atomic write of MOTD text/author/timestamp under <see cref="_motdLock"/>.
    /// Logs the change for audit (sysop changing zone MOTD is the kind of
    /// thing that needs to be recoverable from logs).
    /// </summary>
    private void SetMotd(string text, string author)
    {
        lock (_motdLock)
        {
            _motdText = text;
            _motdAuthor = author;
            _motdSetAt = DateTime.UtcNow;
        }
        _logManager.LogM(LogLevel.Info, LogCategory, $"MOTD set by {author}: {text}");
    }

    /// <summary>
    /// Send the MOTD to one player, chunked into 200-char segments. The
    /// chunk size matches the original ASSS module and fits within Continuum's
    /// per-chat-line limit. The first chunk is prefixed with "MOTD: " so it
    /// stands out; subsequent chunks are bare text (avoids "MOTD: ...MOTD: ..."
    /// duplication for long messages).
    /// </summary>
    private void ShowMotd(Player player)
    {
        string text;
        lock (_motdLock) { text = _motdText; }
        if (string.IsNullOrEmpty(text)) return;

        const int ChunkSize = 200;
        for (int i = 0; i < text.Length; i += ChunkSize)
        {
            int len = Math.Min(ChunkSize, text.Length - i);
            string chunk = text.Substring(i, len);
            string line = i == 0 ? $"MOTD: {chunk}" : chunk;
            _chat.SendMessage(player, line);
        }
    }

    // -------------------------------------------------------------------------
    // PER-PLAYER DATA
    // -------------------------------------------------------------------------

    /// <summary>
    /// Per-player flag tracking whether the MOTD has been shown to this
    /// player during the current connection. Reset to <c>false</c> when the
    /// player slot is recycled (i.e. they reconnect), so reconnects DO see
    /// the latest MOTD.
    /// </summary>
    private sealed class MotdPlayerData : IResettable
    {
        public bool HasSeenMotd;

        bool IResettable.TryReset()
        {
            HasSeenMotd = false;
            return true;
        }
    }
}
