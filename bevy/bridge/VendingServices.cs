using System;
using System.Collections.Generic;
using System.Linq;
using UnturnedGodot;
using UnturnedGodot.Net;
using System.Text.Json;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedBevy;

/// <summary>Source <c>MpLoopback</c> vending seams (<c>NetVendPay</c>/<c>NetVendDrop</c>): the singleplayer
/// listen host spends the dollar and spawns the can on the SERVER, so the owner echo confirms the purchase.
/// A joined client has no seam in the source (<c>PlayerController.CanVend</c> is false, the machine reads
/// "Out of order"), and none here: no command id exists for a vend.</summary>
public sealed partial class BridgeHost
{
    /// <summary>VendingMachine.Price: dollars, i.e. Currency.StackId amount.</summary>
    internal const int VendPrice = 1;
    /// <summary>VendingMachine.ColaId (Vendor_0, red) and SodaId (Vendor_1, blue).</summary>
    internal const ushort VendCola = 80, VendSoda = 465;

    /// <summary>The source pays from <c>PlayerController</c> within its 2.6 m look reach of the machine; the
    /// server's bound is that reach plus the tray's offset and the eye height, from the accepted feet.</summary>
    internal const float VendReach = 4f;

    /// <summary>The map's machines, from the placement manifest (absolute frame).</summary>
    IReadOnlyDictionary<int, MapServices.VendMachine> _vendors = new Dictionary<int, MapServices.VendMachine>();
    /// <summary>Cans paid for and not yet dropped, per machine. The source drop seam trusts its own in-process
    /// caller; the bridge's JSON boundary does not, so a drop must follow a paid vend at that machine, and the
    /// drink and the tray are the server's, never the request's.</summary>
    readonly Dictionary<int, int> _vendPending = new();
    public int VendPendingForTest => _vendPending.Values.Sum();

    void AdoptVendors(MapServices.Parsed parsed)
    {
        if (parsed.Vendors != null) _vendors = parsed.Vendors;
    }

    void ResetVending()
    {
        _vendPending.Clear();
        _vendors = new Dictionary<int, MapServices.VendMachine>();
    }

    /// <summary>The tray in the current server frame (absolute minus the origin).</summary>
    Vector3 VendTray(MapServices.VendMachine machine) =>
        new(machine.Tray[0] - (float)_originX, machine.Tray[1], machine.Tray[2] - (float)_originZ);

    object Vend(string name, JsonElement args)
    {
        // A dedicated host has no local player: every player there is a joined client, so nobody vends
        // (CheckDedicatedOperation already refuses "action"; this keeps the seam off _client regardless).
        if (_dedicated) return new { queued = false, reason = "dedicated" };
        if (IsRemote || _server == null) return new { queued = false, reason = "remote" };
        int index = Int(args, "sourceIndex", -1);
        if (!_vendors.TryGetValue(index, out var machine)) return new { queued = false, reason = "no machine" };
        var tray = VendTray(machine);
        if (name == "vendPay")
        {
            if (!_server.Players.TryGetByOwner(_client.PlayerId, out var player) || (player.Pos - tray).magnitude > VendReach)
                return new { queued = false, reason = "reach" };
            if (!_server.Inventories.TryGet(_client.PlayerId, out var entry)) return new { queued = false };
            var bag = entry.Inventory;
            if (bag == null || bag.getItemCount(Currency.StackId) < VendPrice) return new { queued = false };
            bag.removeItemAmount(Currency.StackId, VendPrice);
            // removeItemAmount raises no grid event: mark it dirty or the spend never echoes.
            _server.Inventories.ServerMarkDirty(_client.PlayerId);
            _vendPending[index] = _vendPending.GetValueOrDefault(index) + 1;
            return new { queued = true };
        }
        if (_vendPending.GetValueOrDefault(index) <= 0) return new { queued = false };
        if (--_vendPending[index] == 0) _vendPending.Remove(index);
        _server.Transactions.SpawnWorldItem(new Item(machine.Drink), tray, Vector3.zero);
        return new { queued = true };
    }
}
