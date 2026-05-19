using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace SS.SectorWar.Modules;

// =============================================================================
// SectorWar — SelectBox subsystem (absorbed from SS.Core.Modules.SelectBox).
// =============================================================================
//
// PURPOSE
// -------
// Provides the S2C SelectBox dialog packet used by ?hq / ?shop / ?inv to
// display the arrow-key navigation UI in Continuum. Absorbed into the
// umbrella so SectorWar plugins don't need SS.Core.Modules.SelectBox attached
// as a separate module on the host zone.
//
// Phong's rule 1 says SectorWar registers ONE module. Anything Inventory
// needs at runtime should be inside this umbrella, not an extra module
// dependency. This subsystem fulfills that for the SelectBox dialog UI.
//
// SOURCE
// ------
// Direct port of SS.Core.Modules.SelectBox.cs (in the SubspaceServer Core
// project). Same packet format, same packet-size limits, same command
// dispatch. The ?select command + SelectBoxItemSelectedCallback firing logic
// are identical.
//
// RUNTIME OWNERSHIP
//   - Owned state: NONE (stateless packet builder).
//   - Conf keys read: NONE.
//   - Persisted data: NONE.
//   - Fakes registered: NONE.
//   - Timers scheduled: NONE.
//   - Commands registered: ?select (zone-wide, via _commandManager.AddCommand
//     with no arena arg — the Continuum client sends ?select <itemValue>
//     when a player picks an item from the dialog, and this needs to work
//     regardless of which arena they're in).
//   - Broker interfaces published: ISelectBox (registered zone-wide).
//
// CALLBACKS HOOKED: NONE (we only FIRE SelectBoxItemSelectedCallback when
// ?select is invoked; Inventory subscribes to that callback).
//
// THREADING
// ---------
// ISelectBox.Open is called on the mainloop (from Inventory's command
// handlers). Packet send uses INetwork.SendToSet which is mainloop-safe.
// The Command_select handler fires on the mainloop as well.
// =============================================================================

public sealed partial class SectorWar
{
    /// <summary>Continuum's hard cap on the S2C SelectBox packet (8 KB).</summary>
    private const int SelectBoxMaxPacketLength = 8192;

    /// <summary>Title field max byte count INCLUDING the null terminator.</summary>
    private const int SelectBoxMaxTitleLength = 64;

    /// <summary>Per-item text max byte count INCLUDING the null terminator.</summary>
    private const int SelectBoxMaxItemTextLength = 128;

    private InterfaceRegistrationToken<ISelectBox>? _selectBoxRegistrationToken;

    // -------------------------------------------------------------------------
    // SUBSYSTEM LOAD / UNLOAD HOOKS
    // -------------------------------------------------------------------------

    private void LoadSelectBox(IComponentBroker broker)
    {
        _commandManager.AddCommand("select", Command_SelectBox);
        _selectBoxRegistrationToken = broker.RegisterInterface<ISelectBox>(this);
        _logManager.LogM(LogLevel.Info, LogCategory,
            "SelectBox subsystem loaded (absorbed from SS.Core.Modules.SelectBox).");
    }

    private void UnloadSelectBox(IComponentBroker broker)
    {
        if (_selectBoxRegistrationToken is not null)
            broker.UnregisterInterface(ref _selectBoxRegistrationToken);
        _commandManager.RemoveCommand("select", Command_SelectBox);
    }

    // -------------------------------------------------------------------------
    // PER-ARENA ATTACH / DETACH (no-op — SelectBox is zone-wide stateless)
    // -------------------------------------------------------------------------

    private void AttachSelectBox(Arena arena) { /* nothing per-arena */ }
    private void DetachSelectBox(Arena arena) { /* nothing per-arena */ }

    // -------------------------------------------------------------------------
    // ISelectBox IMPLEMENTATION
    // -------------------------------------------------------------------------

    /// <summary>
    /// Build the S2CPacketType.SelectBox packet and send it to every player
    /// in the target set that has the SelectBox client feature. Items past the
    /// 8 KB packet limit get silently truncated; item-text past 127 bytes
    /// truncates at the boundary (preserving the null-terminator).
    /// </summary>
    void ISelectBox.Open(ITarget target, ReadOnlySpan<char> title,
        IReadOnlyList<SelectBoxItem> items)
    {
        // Truncate title to fit the field (incl. null terminator).
        title = StringUtils.TruncateForEncodedByteLimit(title, SelectBoxMaxTitleLength - 1);

        // Compute the final packet length so we can rent a buffer of exactly
        // the right size. Drop items past the packet-size limit.
        int length = 1 + StringUtils.DefaultEncoding.GetByteCount(title) + 1; // type + title + null
        for (int i = 0; i < items.Count; i++)
        {
            (short itemValue, ReadOnlyMemory<char> itemText) = items[i];
            int itemTextByteCount = StringUtils.DefaultEncoding.GetByteCount(itemText.Span);
            if (itemTextByteCount >= SelectBoxMaxItemTextLength)
                itemTextByteCount = SelectBoxMaxItemTextLength - 1;

            int additional = 2 + itemTextByteCount + 1; // itemValue + itemText + null
            if (length + additional > SelectBoxMaxPacketLength)
                break;
            length += additional;
        }

        byte[]? packetArray = null;
        try
        {
            Span<byte> buffer = length <= 1024
                ? stackalloc byte[length]
                : (packetArray = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);

            // Type byte
            buffer[0] = (byte)S2CPacketType.SelectBox;
            Span<byte> remaining = buffer[1..];
            length = 1;

            // Title (null-terminated)
            if (!StringUtils.DefaultEncoding.TryGetBytes(title, remaining, out int bytesWritten))
                return;
            remaining = remaining[bytesWritten..];
            remaining[0] = 0;
            remaining = remaining[1..];
            length += bytesWritten + 1;

            // Items
            for (int i = 0; i < items.Count; i++)
            {
                (short itemValue, ReadOnlyMemory<char> itemText) = items[i];

                if (remaining.Length < 2) break;
                BinaryPrimitives.WriteInt16LittleEndian(remaining, itemValue);
                remaining = remaining[2..];

                ReadOnlySpan<char> itemTextSpan = StringUtils.TruncateForEncodedByteLimit(
                    itemText.Span, SelectBoxMaxItemTextLength - 1);
                if (!StringUtils.DefaultEncoding.TryGetBytes(itemTextSpan, remaining, out bytesWritten))
                    break;

                remaining = remaining[bytesWritten..];
                remaining[0] = 0;
                remaining = remaining[1..];
                length += 2 + bytesWritten + 1;
            }

            // Resolve the target to a player set, filter by SelectBox feature bit,
            // and reliable-send the packet.
            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                _playerData.TargetToSet(target, players,
                    static p => (p.ClientFeatures & ClientFeatures.SelectBox) != 0);
                _network.SendToSet(players, buffer[..length], NetSendFlags.Reliable);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(players);
            }
        }
        finally
        {
            if (packetArray is not null)
                ArrayPool<byte>.Shared.Return(packetArray);
        }
    }

    // -------------------------------------------------------------------------
    // ?select command — Continuum client sends ?select <itemValue> when a player
    // picks from a dialog. Fires SelectBoxItemSelectedCallback so subscribers
    // (Inventory's menu logic) can dispatch the chosen action.
    // -------------------------------------------------------------------------

    private void Command_SelectBox(ReadOnlySpan<char> commandName,
        ReadOnlySpan<char> parameters, Player player, ITarget target)
    {
        if (player is null) return;
        Arena? arena = player.Arena;
        if (arena is null) return;

        Span<Range> tokens = stackalloc Range[2];
        int tokenCount = parameters.Split(tokens, ' ', StringSplitOptions.TrimEntries);
        if (tokenCount < 1) return;

        if (!short.TryParse(parameters[tokens[0]], out short itemValue))
            return;

        ReadOnlySpan<char> itemText = tokenCount == 2 ? parameters[tokens[1]] : [];
        SelectBoxItemSelectedCallback.Fire(arena, player, itemValue, itemText);
    }
}
