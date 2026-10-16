using System;
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

    /// <summary>Cans paid for and not yet dropped. The source drop seam trusts its own in-process caller; the
    /// bridge's JSON boundary does not, so a drop must follow a paid vend and name one of the two drinks.</summary>
    int _vendPending;
    public int VendPendingForTest => _vendPending;

    object Vend(string name, JsonElement args)
    {
        if (IsRemote || _server == null) return new { queued = false, reason = "remote" };
        if (name == "vendPay")
        {
            if (!_server.Inventories.TryGet(_client.PlayerId, out var entry)) return new { queued = false };
            var bag = entry.Inventory;
            if (bag == null || bag.getItemCount(Currency.StackId) < VendPrice) return new { queued = false };
            bag.removeItemAmount(Currency.StackId, VendPrice);
            // removeItemAmount raises no grid event: mark it dirty or the spend never echoes.
            _server.Inventories.ServerMarkDirty(_client.PlayerId);
            _vendPending++;
            return new { queued = true };
        }
        ushort drink = checked((ushort)Int(args, "drinkId"));
        if (drink is not (VendCola or VendSoda) || _vendPending <= 0) return new { queued = false };
        _vendPending--;
        _server.Transactions.SpawnWorldItem(new Item(drink), Vec(args, "position"), Vector3.zero);
        return new { queued = true };
    }
}
