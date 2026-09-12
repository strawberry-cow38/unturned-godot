using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    // ---------------------------------------------------------------------------------------------------
    // Inventory (MP_PLAN §3.3): the server owns every PlayerInventory; all mutations are commands validated
    // against the SERVER grid -- the ported tryFindSpace/checkSpaceDrag/TryDrag logic IS the validator, so
    // an illegal move is rejected by the same cell math that makes it illegal in single-player. The owner
    // gets an owner-only Snap block: the FULL inventory, re-sent when dirty (keyed on the model layer's
    // existing onStateUpdated events) -- inventories are small and change on discrete player actions, so
    // whole-state-on-dirty is the honest delta.
    // ---------------------------------------------------------------------------------------------------

    public struct MoveItemCommand
    {
        public byte Page0, X0, Y0;
        public byte Page1, X1, Y1, Rot1;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt8(Page0); w.WriteUInt8(X0); w.WriteUInt8(Y0);
            w.WriteUInt8(Page1); w.WriteUInt8(X1); w.WriteUInt8(Y1); w.WriteUInt8(Rot1);
        }

        public static bool TryRead(NetPakReader r, out MoveItemCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p0) || !r.ReadUInt8(out byte x0) || !r.ReadUInt8(out byte y0)) return false;
            if (!r.ReadUInt8(out byte p1) || !r.ReadUInt8(out byte x1) || !r.ReadUInt8(out byte y1) || !r.ReadUInt8(out byte rot)) return false;
            cmd = new MoveItemCommand { Page0 = p0, X0 = x0, Y0 = y0, Page1 = p1, X1 = x1, Y1 = y1, Rot1 = rot };
            return true;
        }
    }

    public struct DropItemCommand
    {
        public byte Page, X, Y;
        public void Write(NetPakWriter w) { w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); }
        public static bool TryRead(NetPakReader r, out DropItemCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y)) return false;
            cmd = new DropItemCommand { Page = p, X = x, Y = y };
            return true;
        }
    }

    public struct PickupItemCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out PickupItemCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new PickupItemCommand { NetId = id };
            return true;
        }
    }

    public struct EquipItemCommand
    {
        public byte FromPage, X, Y, Slot;   // Slot: 0 primary / 1 secondary
        public void Write(NetPakWriter w) { w.WriteUInt8(FromPage); w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt8(Slot); }
        public static bool TryRead(NetPakReader r, out EquipItemCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y) || !r.ReadUInt8(out byte s)) return false;
            cmd = new EquipItemCommand { FromPage = p, X = x, Y = y, Slot = s };
            return true;
        }
    }

    public struct CraftCommand
    {
        public ushort BlueprintIndex;   // index into the host-registered blueprint catalog (same list both sides)
        public void Write(NetPakWriter w) => w.WriteUInt16(BlueprintIndex);
        public static bool TryRead(NetPakReader r, out CraftCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort idx)) return false;
            cmd = new CraftCommand { BlueprintIndex = idx };
            return true;
        }
    }

    /// <summary>Cancel one queued craft job and refund it. Addressed by SLOT (the job's position in the
    /// owner's server-side queue, oldest first) rather than by blueprint index, because a player who queued
    /// three of the same recipe means a specific tile and not "one of these three" -- and because a blueprint
    /// index would silently cancel the wrong unit the moment the first one finished between click and arrival.</summary>
    public struct CraftCancelCommand
    {
        public byte Slot;
        public void Write(NetPakWriter w) => w.WriteUInt8(Slot);
        public static bool TryRead(NetPakReader r, out CraftCancelCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte slot)) return false;
            cmd = new CraftCancelCommand { Slot = slot };
            return true;
        }
    }

    public struct ConsumeCommand
    {
        public byte Page, X, Y;
        public void Write(NetPakWriter w) { w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); }
        public static bool TryRead(NetPakReader r, out ConsumeCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y)) return false;
            cmd = new ConsumeCommand { Page = p, X = x, Y = y };
            return true;
        }
    }

    /// <summary>"I fitted the item in this cell onto my gun -- spend it." Carries the expected item ID as well
    /// as the cell so the server can refuse a stale address rather than deleting whatever happens to be there
    /// now: the client's grid can shift between the click and the packet.</summary>
    public struct FitAttachmentCommand
    {
        public byte Page, X, Y;
        public ushort Id;
        public void Write(NetPakWriter w) { w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt16(Id); }
        public static bool TryRead(NetPakReader r, out FitAttachmentCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y) || !r.ReadUInt16(out ushort id)) return false;
            cmd = new FitAttachmentCommand { Page = p, X = x, Y = y, Id = id };
            return true;
        }
    }

    /// <summary>Loading or unloading ONE round of a magazine, as an intent (v15, id 39).
    ///
    /// One command per round rather than one per operation, deliberately. The client paces the fill wheel
    /// at half a second a round and the player can drop the drag or close the bag mid-fill, so an
    /// all-or-nothing "load N" would either overshoot what they meant or need a cancel message to unwind.
    /// Per-round means an abandoned operation simply stops, and the server's inventory is correct at every
    /// intermediate point rather than only at the end.
    ///
    /// The magazine is addressed by GRID SLOT plus its item id, not by object reference: the two sides do
    /// not share objects, and the id check is what stops a stale slot from loading rounds into whatever
    /// happens to be sitting there now.</summary>
    public struct MagLoadCommand
    {
        public byte MagPage, MagX, MagY;
        public byte RoundPage, RoundX, RoundY;   // ignored when unloading -- the server picks the destination
        public ushort MagId, RoundId;
        public bool Unloading;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt8(MagPage); w.WriteUInt8(MagX); w.WriteUInt8(MagY);
            w.WriteUInt8(RoundPage); w.WriteUInt8(RoundX); w.WriteUInt8(RoundY);
            w.WriteUInt16(MagId); w.WriteUInt16(RoundId);
            w.WriteBit(Unloading);
        }

        public static bool TryRead(NetPakReader r, out MagLoadCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte mp) || !r.ReadUInt8(out byte mx) || !r.ReadUInt8(out byte my)) return false;
            if (!r.ReadUInt8(out byte rp) || !r.ReadUInt8(out byte rx) || !r.ReadUInt8(out byte ry)) return false;
            if (!r.ReadUInt16(out ushort mid) || !r.ReadUInt16(out ushort rid)) return false;
            if (!r.ReadBit(out bool un)) return false;
            cmd = new MagLoadCommand
            {
                MagPage = mp, MagX = mx, MagY = my,
                RoundPage = rp, RoundX = rx, RoundY = ry,
                MagId = mid, RoundId = rid, Unloading = un,
            };
            return true;
        }
    }

    /// <summary>Reloading, as an intent. The client picked a magazine and knows how many rounds came out of the
    /// gun; the server owns whether that magazine is really there.
    ///
    /// SpentId/SpentAmount are the outgoing magazine, and they are the one client-supplied quantity here -- the
    /// server has no gun state to derive them from (nothing on the wire carries gunAmmo). OnReload clamps the
    /// amount to the magazine asset's real capacity, so the worst a lying client gets is a full magazine back
    /// instead of a partial one, not an arbitrary stack. SpentId 0 = nothing to return (reloading from empty).</summary>
    public struct ReloadSwapCommand
    {
        public byte Page, X, Y;      // the fresh magazine being loaded
        public ushort SpentId;       // the magazine coming out (0 = none)
        public byte SpentAmount;     // rounds left in it
        public void Write(NetPakWriter w) { w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt16(SpentId); w.WriteUInt8(SpentAmount); }
        public static bool TryRead(NetPakReader r, out ReloadSwapCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y)
                || !r.ReadUInt16(out ushort sid) || !r.ReadUInt8(out byte samt)) return false;
            cmd = new ReloadSwapCommand { Page = p, X = x, Y = y, SpentId = sid, SpentAmount = samt };
            return true;
        }
    }

    /// <summary>Unload loose rounds from the gun at (Page,X,Y) into the bag -- see
    /// ReplicationIds.CommandGunUnload. Unlike ReloadSwapCommand's SpentAmount, Count is CHECKED: the server
    /// holds this gun's ammo (v16) and refuses a claim larger than what it is holding.</summary>
    public struct GunUnloadCommand
    {
        public byte Page, X, Y;      // where the gun is
        public ushort RoundId;       // the loose round coming out
        public byte Count;           // how many
        public void Write(NetPakWriter w) { w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt16(RoundId); w.WriteUInt8(Count); }
        public static bool TryRead(NetPakReader r, out GunUnloadCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y)
                || !r.ReadUInt16(out ushort rid) || !r.ReadUInt8(out byte n)) return false;
            cmd = new GunUnloadCommand { Page = p, X = x, Y = y, RoundId = rid, Count = n };
            return true;
        }
    }

    /// <summary>The client-owned gun state for the item at (Page,X,Y) -- see ReplicationIds.CommandGunState
    /// for why the server needs telling at all. Id is carried so a command that arrives after the player has
    /// moved something else into that cell lands on nothing rather than stamping a rifle's magazine onto a can
    /// of beans; the address alone is not an identity.
    ///
    /// The four per-slot attachment ids ride along with the mask. They are the same class of field (client-only
    /// writer, no server writer) and they die on the same echo, so splitting them into their own command would
    /// mean two commands that must not be reordered against each other for one logical state.
    ///
    /// gunChamberedType is NOT here: it is a string, this stack has no string primitive, and ReadJar already
    /// re-derives it from the loaded magazine id.</summary>
    public struct GunStateCommand
    {
        public byte Page, X, Y;
        public ushort Id;            // the gun's item id -- identity, so a stale address cannot stamp the wrong item
        public short Ammo;
        public bool Chambered;
        public sbyte Firemode;
        public int MagId;
        public int Attach;           // the viewmodel's attach mask
        public int Sight, Barrel, Grip, Tactical;
        public bool AttachSeeded;

        public void Write(NetPakWriter w)
        {
            w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y);
            w.WriteUInt16(Id);
            w.WriteInt16(Ammo);
            w.WriteBit(Chambered);
            w.WriteInt8(Firemode);
            w.WriteInt32(MagId);
            w.WriteInt32(Attach);
            w.WriteInt32(Sight); w.WriteInt32(Barrel); w.WriteInt32(Grip); w.WriteInt32(Tactical);
            w.WriteBit(AttachSeeded);
        }

        public static bool TryRead(NetPakReader r, out GunStateCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y)) return false;
            if (!r.ReadUInt16(out ushort id)) return false;
            if (!r.ReadInt16(out short ammo)) return false;
            if (!r.ReadBit(out bool ch)) return false;
            if (!r.ReadInt8(out sbyte fm)) return false;
            if (!r.ReadInt32(out int mag)) return false;
            if (!r.ReadInt32(out int att)) return false;
            if (!r.ReadInt32(out int sight) || !r.ReadInt32(out int barrel)
                || !r.ReadInt32(out int grip) || !r.ReadInt32(out int tac)) return false;
            if (!r.ReadBit(out bool seeded)) return false;
            cmd = new GunStateCommand
            {
                Page = p, X = x, Y = y, Id = id, Ammo = ammo, Chambered = ch, Firemode = fm,
                MagId = mag, Attach = att, Sight = sight, Barrel = barrel, Grip = grip, Tactical = tac,
                AttachSeeded = seeded,
            };
            return true;
        }
    }

    /// <summary>Set the autodrink flag on the item at (Page,X,Y). Id is identity, as in GunStateCommand: an
    /// address alone would let a command that arrives after a swap set the flag on whatever landed there.</summary>
    /// <summary>Flip a cooking appliance on or off (strawberry 2026-09-05: "a new on/off button for
    /// cooking"). Addressed by the appliance's crate NetId, which is what the player already has open --
    /// and the server checks it IS a registered cooker, so a forged id for an arbitrary crate does nothing
    /// rather than conjuring an oven.</summary>
    /// <summary>v45: fit a spare to one wheel. The car and the wheel index; the tire item comes off the
    /// sender's hand server-side.</summary>
    public struct FitTireCommand
    {
        public uint VehicleNetId;
        public byte WheelIndex;
        public void Write(NetPakWriter w) { w.WriteUInt32(VehicleNetId); w.WriteUInt8(WheelIndex); }
        public static bool TryRead(NetPakReader r, out FitTireCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id) || !r.ReadUInt8(out byte wheel)) return false;
            cmd = new FitTireCommand { VehicleNetId = id, WheelIndex = wheel };
            return true;
        }
    }

    /// <summary>v45: jack a vehicle upright. The car only -- the force is the server's.</summary>
    public struct CarjackCommand
    {
        public uint VehicleNetId;
        public void Write(NetPakWriter w) { w.WriteUInt32(VehicleNetId); }
        public static bool TryRead(NetPakReader r, out CarjackCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new CarjackCommand { VehicleNetId = id };
            return true;
        }
    }

    /// <summary>v43: cuff / unlock. The TARGET only: what you are doing it with is read off the hand the
    /// server already replicates, so the client cannot nominate a restraint it is not holding.</summary>
    public struct ArrestTargetCommand
    {
        public ushort TargetPlayerId;
        public void Write(NetPakWriter w) { w.WriteUInt16(TargetPlayerId); }
        public static bool TryRead(NetPakReader r, out ArrestTargetCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt16(out ushort t)) return false;
            cmd = new ArrestTargetCommand { TargetPlayerId = t };
            return true;
        }
    }

    /// <summary>v43: one wriggle. `Side` is 0 or 1 -- which way you leaned -- and the server only counts it when
    /// it DIFFERS from the last side it took from you, which is retail's `lastLean != lean` written down.</summary>
    public struct StruggleCommand
    {
        public byte Side;
        public void Write(NetPakWriter w) { w.WriteBit(Side != 0); }
        public static bool TryRead(NetPakReader r, out StruggleCommand cmd)
        {
            cmd = default;
            if (!r.ReadBit(out bool side)) return false;
            cmd = new StruggleCommand { Side = (byte)(side ? 1 : 0) };
            return true;
        }
    }

    /// <summary>v42: "I would like to be doing this gesture." One byte -- an EPlayerGesture -- and nothing
    /// else: the server already knows who asked, where they are standing and what is in their hands, and
    /// anything the client could add here is something it could lie about.</summary>
    public struct RequestGestureCommand
    {
        public byte Gesture;
        public void Write(NetPakWriter w) { w.WriteUInt8(Gesture); }
        public static bool TryRead(NetPakReader r, out RequestGestureCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte g)) return false;
            cmd = new RequestGestureCommand { Gesture = g };
            return true;
        }
    }

    public struct SetCookerOnCommand
    {
        public uint NetId;
        public bool On;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteBit(On); }
        public static bool TryRead(NetPakReader r, out SetCookerOnCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint netId)) return false;
            if (!r.ReadBit(out bool on)) return false;
            cmd = new SetCookerOnCommand { NetId = netId, On = on };
            return true;
        }
    }

    public struct SetAutoDrinkCommand
    {
        public byte Page, X, Y;
        public ushort Id;
        public bool AutoDrink;
        public void Write(NetPakWriter w)
        {
            w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt16(Id); w.WriteBit(AutoDrink);
        }
        public static bool TryRead(NetPakReader r, out SetAutoDrinkCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y)) return false;
            if (!r.ReadUInt16(out ushort id)) return false;
            if (!r.ReadBit(out bool on)) return false;
            cmd = new SetAutoDrinkCommand { Page = p, X = x, Y = y, Id = id, AutoDrink = on };
            return true;
        }
    }

    /// <summary>Wear the garment at (Page,X,Y) into clothing slot Slot (an EItemType). The displaced garment, if
    /// any, goes back to the grid -- the server does the whole swap, because doing half of it locally is what made
    /// a dragged-on backpack un-equip itself on the next echo.</summary>
    public struct WearClothingCommand
    {
        public byte Page, X, Y, Slot;
        public void Write(NetPakWriter w) { w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt8(Slot); }
        public static bool TryRead(NetPakReader r, out WearClothingCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y) || !r.ReadUInt8(out byte s)) return false;
            cmd = new WearClothingCommand { Page = p, X = x, Y = y, Slot = s };
            return true;
        }
    }

    /// <summary>Take clothing slot Slot off, back into the grid.</summary>
    public struct UnwearClothingCommand
    {
        public byte Slot;
        // WHERE THE GARMENT LANDS (strawberry 2026-09-10: "if i drag a clothing item off, it should go into the
        // slot i dragged it to, not just the top of my entire inventory"). The command said only WHICH slot to
        // empty, so the server rehomed with tryAddItem -- first free space from the top -- and the cell you
        // actually dropped on was never sent. Page 255 = unaddressed (the hotkey/menu unequip, which genuinely
        // has no target) and keeps the old find-anywhere behaviour.
        public byte Page, X, Y;
        public void Write(NetPakWriter w) { w.WriteUInt8(Slot); w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); }
        public static bool TryRead(NetPakReader r, out UnwearClothingCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte s)) return false;
            if (!r.ReadUInt8(out byte pg) || !r.ReadUInt8(out byte px) || !r.ReadUInt8(out byte py)) return false;
            cmd = new UnwearClothingCommand { Slot = s, Page = pg, X = px, Y = py };
            return true;
        }
    }

    public struct OpenStorageCommand
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out OpenStorageCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id)) return false;
            cmd = new OpenStorageCommand { NetId = id };
            return true;
        }
    }

    /// <summary>Take the item in ONE cell of a container's grid straight into the sender's bag (v34, id 45).
    ///
    /// Addressed by crate NetId + cell, NOT by item id: two identical cans on a shelf are two different objects
    /// and the player pressed F on one of them. The server still checks what is actually in that cell, so a
    /// stale cell (someone else took it a tick earlier) is a refusal rather than a grab of whatever moved in.</summary>
    public struct TakeFromStorageCommand
    {
        public uint NetId;
        public byte X, Y;
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteUInt8(X); w.WriteUInt8(Y); }
        public static bool TryRead(NetPakReader r, out TakeFromStorageCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt32(out uint id) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y)) return false;
            cmd = new TakeFromStorageCommand { NetId = id, X = x, Y = y };
            return true;
        }
    }

    public struct CloseStorageCommand
    {
        public void Write(NetPakWriter w) { }
        public static bool TryRead(NetPakReader r, out CloseStorageCommand cmd) { cmd = default; return true; }
    }

    /// <summary>v28 gains IsCooker/CookerKind/CookerOn; v29 adds CookerFuel so the bar is already at the right
    /// height on the frame the panel opens, instead of snapping there on the first EventCookerState tick.
    /// The SERVER is the authority on whether a container
    /// cooks -- the client would otherwise have to guess from a mesh name it does not reliably have -- and the
    /// moment it needs to know is exactly when it opens the thing and has to decide whether to draw an on/off
    /// button under the grid.</summary>
    public struct StorageOpenedEvent
    {
        public uint NetId;
        public byte Width, Height;
        public bool IsCooker;
        public byte CookerKind;   // ECookerKind, meaningful only when IsCooker
        public bool CookerOn;
        public byte CookerFuel;   // v29: 0..255 of the CURRENT fuel item's burn, 0 when nothing is lit
        public byte FreezerWidth, FreezerHeight;   // v30: 0x0 = this container has no freezer compartment
        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(NetId); w.WriteUInt8(Width); w.WriteUInt8(Height);
            w.WriteBit(IsCooker);
            if (IsCooker) { w.WriteUInt8(CookerKind); w.WriteBit(CookerOn); w.WriteUInt8(CookerFuel); }
            bool freezer = FreezerWidth > 0 && FreezerHeight > 0;
            w.WriteBit(freezer);
            if (freezer) { w.WriteUInt8(FreezerWidth); w.WriteUInt8(FreezerHeight); }
        }
        public static bool TryRead(NetPakReader r, out StorageOpenedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id) || !r.ReadUInt8(out byte width) || !r.ReadUInt8(out byte height)) return false;
            if (!r.ReadBit(out bool isCooker)) return false;
            byte kind = 0; bool on = false; byte fuel = 0;
            if (isCooker) { if (!r.ReadUInt8(out kind)) return false; if (!r.ReadBit(out on)) return false; if (!r.ReadUInt8(out fuel)) return false; }
            byte fw = 0, fh = 0;
            if (!r.ReadBit(out bool freezer)) return false;
            if (freezer) { if (!r.ReadUInt8(out fw)) return false; if (!r.ReadUInt8(out fh)) return false; }
            evt = new StorageOpenedEvent { NetId = id, Width = width, Height = height, IsCooker = isCooker, CookerKind = kind, CookerOn = on, CookerFuel = fuel,
                                           FreezerWidth = fw, FreezerHeight = fh };
            return true;
        }
    }

    /// <summary>v29: how much of the burning fuel item is left, pushed to the ONE player who has the appliance
    /// open (strawberry 2026-09-06: "as each fuel item burns, show a progress bar before its consumed").
    ///
    /// This could not ride the inventory delta, and the reason is the feature: the bar shows the item that is
    /// ALREADY BURNING, and a lit log is gone from the grid -- ServerCooking consumes it the moment it catches.
    /// There is no jar left whose fields could carry the countdown. So the value is a property of the appliance,
    /// and it travels as one.
    ///
    /// `On` rides along because a campfire that runs out of wood switches itself off server-side, and without
    /// the flag the button would keep saying ON under an empty bar until the player closed and reopened it.</summary>
    /// <summary>v31: the owner's pending craft jobs, newest last. Capped at 8 on the wire -- a queue longer
    /// than that is not readable on screen anyway, and the count is sent unclamped so the UI can say "+3 more"
    /// rather than silently lying about how much is pending.</summary>
    public struct CraftQueueEvent
    {
        public const int MaxSent = 8;
        public byte Total;                       // real length, which may exceed what follows
        public (ushort Bp, float Left, float Of)[] Jobs;
        public void Write(NetPakWriter w)
        {
            var jobs = Jobs ?? System.Array.Empty<(ushort, float, float)>();
            byte n = (byte)System.Math.Min(jobs.Length, MaxSent);
            w.WriteUInt8(Total); w.WriteUInt8(n);
            for (int i = 0; i < n; i++)
            {
                w.WriteUInt16(jobs[i].Bp);
                w.WriteClampedFloat(jobs[i].Left, 12, 3);   // seconds at 1/8 s -- finer than the bar can show, so it never steps
                w.WriteClampedFloat(jobs[i].Of, 12, 3);
            }
        }
        public static bool TryRead(NetPakReader r, out CraftQueueEvent evt)
        {
            evt = default;
            if (!r.ReadUInt8(out byte total) || !r.ReadUInt8(out byte n)) return false;
            if (n > MaxSent) return false;
            var jobs = new (ushort, float, float)[n];
            for (int i = 0; i < n; i++)
            {
                if (!r.ReadUInt16(out ushort bp)) return false;
                if (!r.ReadClampedFloat(12, 3, out float left)) return false;
                if (!r.ReadClampedFloat(12, 3, out float of)) return false;
                jobs[i] = (bp, left, of);
            }
            evt = new CraftQueueEvent { Total = total, Jobs = jobs };
            return true;
        }
    }

    public struct CookerStateEvent
    {
        public uint NetId;
        public bool On;
        public byte Fuel;   // 0..255 of the current fuel item's total burn; 0 = nothing lit
        public void Write(NetPakWriter w) { w.WriteUInt32(NetId); w.WriteBit(On); w.WriteUInt8(Fuel); }
        public static bool TryRead(NetPakReader r, out CookerStateEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id) || !r.ReadBit(out bool on) || !r.ReadUInt8(out byte fuel)) return false;
            evt = new CookerStateEvent { NetId = id, On = on, Fuel = fuel };
            return true;
        }
    }

    public struct StorageClosedEvent
    {
        public uint NetId;
        public void Write(NetPakWriter w) => w.WriteUInt32(NetId);
        public static bool TryRead(NetPakReader r, out StorageClosedEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id)) return false;
            evt = new StorageClosedEvent { NetId = id };
            return true;
        }
    }

    /// <summary>DevConsole mutations as a command (§2.3 "all state mutation goes through commands --
    /// including DevConsole cheats"): the raw line crosses the wire; the SERVER parses, whitelists, and
    /// applies against its own authoritative state. A client build can no longer grant itself anything.</summary>
    public struct ConsoleCommand
    {
        public string Text;
        public void Write(NetPakWriter w) => w.WriteString(Text ?? "");
        public static bool TryRead(NetPakReader r, out ConsoleCommand cmd)
        {
            cmd = default;
            if (!r.ReadString(out string text)) return false;
            cmd = new ConsoleCommand { Text = text };
            return true;
        }
    }

    /// <summary>Client -> server: "say this in global chat" (v49). Text ONLY. The speaker is the peer the
    /// packet arrived on, never a field in it -- a sender id on the wire is a licence to speak as anyone,
    /// including as the server.</summary>
    public struct ChatSendCommand
    {
        public string Text;
        public void Write(NetPakWriter w) => w.WriteString(Text ?? "");
        public static bool TryRead(NetPakReader r, out ChatSendCommand cmd)
        {
            cmd = default;
            if (!r.ReadString(out string text)) return false;
            cmd = new ChatSendCommand { Text = text };
            return true;
        }
    }

    /// <summary>Server -> every peer: one line of chat (v49).
    ///
    /// The NAME travels with the line rather than being looked up client-side by id. A player who leaves
    /// still has to be attributable in the scrollback, and an id whose profile has already been dropped
    /// would render as blank or, worse, as whoever next occupies that id.</summary>
    public struct ChatMessageEvent
    {
        public byte Channel;        // SDG.Unturned.ChatChannel
        public ushort SpeakerId;    // 0 for a server line
        public string Name;         // already sanitised by the server
        public string Text;         // already sanitised by the server

        public void Write(NetPakWriter w)
        {
            w.WriteUInt8(Channel);
            w.WriteUInt16(SpeakerId);
            w.WriteString(Name ?? "");
            w.WriteString(Text ?? "");
        }

        public static bool TryRead(NetPakReader r, out ChatMessageEvent evt)
        {
            evt = default;
            if (!r.ReadUInt8(out byte channel)) return false;
            if (!r.ReadUInt16(out ushort speaker)) return false;
            if (!r.ReadString(out string name)) return false;
            if (!r.ReadString(out string text)) return false;
            evt = new ChatMessageEvent { Channel = channel, SpeakerId = speaker, Name = name, Text = text };
            return true;
        }
    }

    public struct ConsoleResultEvent
    {
        public string Text;
        public void Write(NetPakWriter w) => w.WriteString(Text ?? "");
        public static bool TryRead(NetPakReader r, out ConsoleResultEvent evt)
        {
            evt = default;
            if (!r.ReadString(out string text)) return false;
            evt = new ConsoleResultEvent { Text = text };
            return true;
        }
    }

    /// <summary>
    /// Player inventories as an IReplicatedSystem (SystemId 7) -- the second owner-only block (§2.6): each
    /// client's snapshot carries at most ONE entry, its own full inventory (9 pages of jars + worn
    /// clothing). Other players' inventories never reach you (their look is the appearance snapshot's job,
    /// a deferred system). Server side holds a real PlayerInventory per player plus the storage-crate pages
    /// (an Items page addressed by the crate's NetId, §3.7) with one-opener-at-a-time arbitration; opening
    /// mirrors the SP mechanic exactly -- the crate grid is loaded into the opener's STORAGE page (7), so
    /// MoveItem needs no special crate addressing and the owner block carries the view for free.
    /// </summary>
    public sealed class InventoryReplication : IReplicatedSystem
    {
        public sealed class PlayerEntry
        {
            public ushort OwnerPlayerId;
            public PlayerInventory Inventory = new PlayerInventory();
            public uint OpenCrateId;      // 0 = none (server-side arbitration state)
            public long LastChangedTick;
            internal bool Dirty;          // set by the model's onStateUpdated; stamped to a tick by ServerCommitDirty
        }

        public sealed class CrateEntry
        {
            public uint NetIdValue;
            public Items Storage;
            public byte Width, Height;
            public Vector3 Pos;
            /// <summary>EVERYONE with this container open (master 2026-09-07: "allow multiple people to move,
            /// grab, add items to the containers at the same time"). Was a single `OpenBy` ushort with
            /// server-side arbitration -- one opener at a time (§3.7) -- so the second player to reach a fridge
            /// was simply refused. Order is open-order; it is a handful of players, so a list beats a set.</summary>
            public readonly List<ushort> Viewers = new List<ushort>();
            public bool IsOpen => Viewers.Count > 0;

            /// <summary>A fridge's FREEZER compartment: a second, independent grid shown above the main one
            /// (strawberry 2026-09-06: "in fridges, add a second 'container' to the inventory ui above the
            /// fridge container"). Null on every container that is not a fridge, which is what the client reads
            /// to decide whether to draw the second grid at all.</summary>
            public Items Freezer;
            public byte FreezerWidth, FreezerHeight;
            public bool HasFreezer => Freezer != null && FreezerWidth > 0 && FreezerHeight > 0;

            /// <summary>Does this container keep food cold WITHOUT freezing it -- a powered fridge? Drives the
            /// slowed spoilage rate; the freezer compartment above is a separate, stronger thing.</summary>
            public bool Refrigerates;

            /// <summary>THE WHOLE BOX IS THE FREEZER (master 2026-09-06: "turn the ice box into a smart
            /// container that acts as a freezer"). A fridge has a small freezer compartment above a body that
            /// merely chills -- an ice merchandiser has no warm half, so this freezes `Storage` itself rather
            /// than adding a second grid. Distinct from HasFreezer for exactly that reason: one is a container
            /// with a freezer IN it, this is a container that IS one.</summary>
            public bool BodyFreezes;
        }

        /// <summary>Server-side crate interaction reach. SP opens at 2.5 m (OpenNearestCrate); the server
        /// allows slack for replication-grid rounding and eye-vs-feet geometry.</summary>
        public const float StorageReach = 4f;

        public byte SystemId => ReplicationIds.SystemInventory;

        readonly Dictionary<ushort, PlayerEntry> _byOwner = new Dictionary<ushort, PlayerEntry>();
        readonly Dictionary<uint, CrateEntry> _crates = new Dictionary<uint, CrateEntry>();

        /// <summary>Client side: fires after ReadSnapshot rebuilt my replica (UI refresh hook).</summary>
        public event Action<ushort> ReplicaUpdated;

        public int Count => _byOwner.Count;

        public bool TryGet(ushort ownerPlayerId, out PlayerEntry entry) => _byOwner.TryGetValue(ownerPlayerId, out entry);

        public bool TryGetCrate(uint netId, out CrateEntry crate) => _crates.TryGetValue(netId, out crate);
        /// <summary>Every registered container, for the systems that sweep all of them (freezing, spoilage).</summary>
        public IEnumerable<CrateEntry> Crates => _crates.Values;
        /// <summary>Every tracked player's entry, same reason.</summary>
        public IEnumerable<PlayerEntry> Owners => _byOwner.Values;

        /// <summary>Shut everyone standing in this crate. Kept split out from ServerRemoveCrate, but the reason
        /// it exists has WEAKENED, deliberately: closing used to be what made crate.Storage current (it copied
        /// the opener's page back), so removing the crate first turned that copy into a silent no-op and lost
        /// everything the player had just dragged in. Edits are written through as they happen now, so the
        /// crate is current whether or not anyone closes -- but a caller that wants to read the contents should
        /// still close first, because a viewer left holding a page for a crate that no longer exists has a
        /// dashboard open onto nothing.</summary>
        public void ServerCloseCrateViewers(uint netId, long tick)
        {
            // _byOwner is not structurally modified by ServerCloseStorage (it clears a field on the entry),
            // so iterating it directly is safe.
            foreach (var kv in _byOwner)
                if (kv.Value.OpenCrateId == netId) ServerCloseStorage(kv.Key, tick);
        }

        /// <summary>Drop a crate. Map containers live for the session, so nothing needed this until a PLACED
        /// container could be salvaged or picked up -- leaving the grid behind would keep its contents
        /// addressable by a NetId with no object left in the world to stand next to.</summary>
        public void ServerRemoveCrate(uint netId, long tick)
        {
            ServerCloseCrateViewers(netId, tick);   // before the removal, or the copy-back is skipped
            _crates.Remove(netId);
        }

        // ---- server side ----

        public PlayerEntry ServerAdd(ushort ownerPlayerId, long tick)
        {
            var e = new PlayerEntry { OwnerPlayerId = ownerPlayerId, LastChangedTick = tick };
            for (byte p = 0; p < PlayerInventory.PAGES; p++)
            {
                // any grid mutation marks the owner dirty -- the same onStateUpdated dirtiness SP's UI keys on
                byte pg = p;
                e.Inventory.items[p].onStateUpdated += () =>
                {
                    e.Dirty = true;
                    // ...and an edit to one of the two CONTAINER VIEW pages is an edit to the container itself.
                    // Hooked here rather than at each command handler on purpose: ten handlers can reach page 7
                    // (move, drop, wear, reload-swap, mag-load, consume...) and the one that gets forgotten is
                    // the one that silently edits a private copy nobody else ever sees.
                    if (pg == PlayerInventory.STORAGE || pg == PlayerInventory.FREEZER) ServerPushView(e, pg);
                };
            }
            _byOwner[ownerPlayerId] = e;
            return e;
        }

        public void ServerRemove(ushort ownerPlayerId, long tick)
        {
            if (_byOwner.TryGetValue(ownerPlayerId, out var e) && e.OpenCrateId != 0)
                ServerCloseStorage(ownerPlayerId, tick);   // a vanishing opener must not wedge the crate shut
            _byOwner.Remove(ownerPlayerId);
        }

        /// <summary>Mark an owner's inventory changed when the change was a BARE FIELD WRITE on an Item rather
        /// than a grid operation -- a decremented stack, a gas can's fuelLevel, a bottle's contents.
        ///
        /// Dirty is otherwise raised only by Items.onStateUpdated (add / remove / resize) and the storage
        /// open/close pair, so writing a field on an Item fired nothing and WriteDelta emitted an empty block:
        /// the change never reached the owner until some UNRELATED grid edit happened to dirty the entry. That is
        /// why a pump-filled gas can read empty on the client while the server's copy was full. Review 2026-08-16.</summary>
        public void ServerMarkDirty(ushort ownerPlayerId)
        {
            if (_byOwner.TryGetValue(ownerPlayerId, out var e)) e.Dirty = true;
        }

        /// <summary>Stamp this tick onto every entry the last dispatch round dirtied. Call once per server
        /// tick, after command dispatch, so the delta baseline math sees a real tick number.</summary>
        /// <summary>Re-entrancy guard for the view sync below. A projection INTO a viewer's page fires that
        /// page's onStateUpdated once per item added, and without this the first of those reads back as "that
        /// viewer just edited the container" -- pushing a HALF-REBUILT page into the crate. `clear()` is
        /// deliberately silent, so the first add would publish a ONE-ITEM crate. That is the whole container
        /// deleted, on every open, for everyone.</summary>
        bool _viewSyncing;

        /// <summary>Raised when a container goes from nobody-looking to somebody-looking or back. The game layer
        /// turns it into the DOOR: a fridge whose door swings only on the opener's screen is a fridge that looks
        /// shut to the person standing next to it (master 2026-09-07). Core owns WHO has it open; it does not
        /// know a door exists.</summary>
        public System.Action<uint, bool> CrateOpenChanged;

        /// <summary>One viewer's page just changed, so THAT PAGE is the edit: it becomes the crate's contents,
        /// and every other viewer is repainted from it.
        ///
        /// ⚠ IMMEDIATE, not batched to end-of-tick, and that is the correctness of the whole feature. Commands
        /// dispatch one at a time, so writing through here means the NEXT player's drag is validated against a
        /// page that already contains the previous player's edit. Deferring the reconcile to the tick boundary
        /// would have two players each editing a stale private copy and one of them silently winning -- which
        /// for a container is not a lost move, it is a duplicated or destroyed item.
        ///
        /// The copy re-seats the SAME Item references rather than cloning (see the CopyPage note in
        /// ServerCooking): a steak being cooked in an oven two players are both watching stays ONE object.</summary>
        void ServerPushView(PlayerEntry e, byte page)
        {
            if (_viewSyncing || e == null || e.OpenCrateId == 0) return;
            if (!_crates.TryGetValue(e.OpenCrateId, out var crate)) return;
            bool freezer = page == PlayerInventory.FREEZER;
            if (freezer && !crate.HasFreezer) return;
            if (!freezer && page != PlayerInventory.STORAGE) return;
            _viewSyncing = true;
            try
            {
                var mine = e.Inventory.items[page];
                byte w = freezer ? crate.FreezerWidth : crate.Width;
                byte h = freezer ? crate.FreezerHeight : crate.Height;
                if (mine.width != w || mine.height != h) return;   // mid open/close resize -- not an edit
                CopyPage(mine, freezer ? crate.Freezer : crate.Storage, w, h);
                foreach (var pid in crate.Viewers)
                {
                    if (pid == e.OwnerPlayerId || !_byOwner.TryGetValue(pid, out var other)) continue;
                    CopyPage(freezer ? crate.Freezer : crate.Storage, other.Inventory.items[page], w, h);
                    other.Dirty = true;
                }
            }
            finally { _viewSyncing = false; }
        }

        /// <summary>Repaint every viewer of this crate from the crate itself -- for a change made to the GRID
        /// directly rather than through somebody's page (a shelf grab, a server-side spawn).</summary>
        void ServerRepaintViewers(CrateEntry crate)
        {
            if (crate == null || crate.Viewers.Count == 0) return;
            _viewSyncing = true;
            try
            {
                foreach (var pid in crate.Viewers)
                {
                    if (!_byOwner.TryGetValue(pid, out var v)) continue;
                    CopyPage(crate.Storage, v.Inventory.items[PlayerInventory.STORAGE], crate.Width, crate.Height);
                    if (crate.HasFreezer) CopyPage(crate.Freezer, v.Inventory.items[PlayerInventory.FREEZER], crate.FreezerWidth, crate.FreezerHeight);
                    v.Dirty = true;
                }
            }
            finally { _viewSyncing = false; }
        }

        public void ServerCommitDirty(long tick)
        {
            foreach (var e in _byOwner.Values)
                if (e.Dirty) { e.Dirty = false; e.LastChangedTick = tick; }
        }

        public CrateEntry ServerRegisterCrate(NetId id, byte width, byte height, Vector3 pos)
        {
            var c = new CrateEntry { NetIdValue = id.Value, Width = width, Height = height, Pos = pos, Storage = new Items(PlayerInventory.STORAGE) };
            c.Storage.loadSize(width, height);
            _crates[id.Value] = c;
            return c;
        }

        /// <summary>Give a registered crate a freezer compartment. Separate from ServerRegisterCrate because the
        /// side that knows a prop is a FRIDGE (a mesh name) is the game layer, not this one.</summary>
        /// <summary>Make a whole container a freezer: everything in its main grid freezes rather than thaws.</summary>
        public bool ServerMakeFreezerBody(uint crateId)
        {
            if (!_crates.TryGetValue(crateId, out var c)) return false;
            c.BodyFreezes = true; c.Refrigerates = true;
            return true;
        }

        public bool ServerAddFreezer(uint crateId, byte width, byte height)
        {
            if (!_crates.TryGetValue(crateId, out var c) || width == 0 || height == 0) return false;
            c.Freezer = new Items(PlayerInventory.FREEZER);
            c.Freezer.loadSize(width, height);
            c.FreezerWidth = width; c.FreezerHeight = height;
            c.Refrigerates = true;
            return true;
        }

        /// <summary>Open arbitration (§3.7: one opener at a time, server-enforced). On success the crate
        /// grid is copied into the opener's STORAGE page -- the exact SP OpenNearestCrate mechanic.</summary>
        public bool ServerOpenStorage(ushort ownerPlayerId, uint crateId, Vector3 senderPos, long tick)
        {
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e)) return false;
            if (!_crates.TryGetValue(crateId, out var crate)) return false;
            // NO EXCLUSIVITY ANY MORE (master 2026-09-07: several people in one container at once). This used
            // to refuse when crate.OpenBy named somebody else, which was the honest answer while open COPIED the
            // grid out and close COPIED it back: two players editing private copies and both copying back is
            // last-writer-wins over a whole grid, i.e. duplicated and destroyed items, not a lost drag. What
            // makes sharing safe is that the copy-back is GONE -- the crate is authoritative at every instant
            // and each viewer's page is a view that is rewritten the moment anyone changes anything.
            if ((crate.Pos - senderPos).magnitude > StorageReach) return false;
            if (e.OpenCrateId != 0 && e.OpenCrateId != crateId) ServerCloseStorage(ownerPlayerId, tick);   // one container at a time, per player

            bool wasOpen = crate.IsOpen;
            if (!crate.Viewers.Contains(ownerPlayerId)) crate.Viewers.Add(ownerPlayerId);
            e.OpenCrateId = crateId;
            // GUARDED: these copies fire onStateUpdated per item, which would otherwise read back as this player
            // editing the container and push a half-built page into it. See ServerPushView.
            _viewSyncing = true;
            try
            {
                CopyPage(crate.Storage, e.Inventory.items[PlayerInventory.STORAGE], crate.Width, crate.Height);
                // The freezer rides along as its own page, so both compartments are open at once and an item can be
                // dragged straight from one to the other -- which is the entire interaction a freezer exists for.
                if (crate.HasFreezer)
                    CopyPage(crate.Freezer, e.Inventory.items[PlayerInventory.FREEZER], crate.FreezerWidth, crate.FreezerHeight);
            }
            finally { _viewSyncing = false; }
            e.Dirty = true;
            if (!wasOpen) CrateOpenChanged?.Invoke(crateId, true);   // first one in swings the door for everybody
            return true;
        }

        /// <summary>Take ONE item out of a container and put it in the sender's own bag -- the server half of
        /// pressing F on an item sitting on a shelf.
        ///
        /// Deliberately does NOT open the crate. Open is arbitration ("this container is mine until I close
        /// it"), and reaching past a shelf's front to lift one tin is not that: it must not evict whoever has
        /// the container open, and it must not leave the taker holding it. It DOES refuse while somebody else
        /// has it open, because that player is editing a copy of this grid in their STORAGE page and close
        /// copies the whole page back -- taking from underneath them would be undone, or worse, put back.
        ///
        /// Reach is checked against the same StorageReach the open path uses: the client picks the cell off a
        /// model it can see, and a model can be seen from further than an arm reaches.</summary>
        public bool ServerTakeFromStorage(ushort ownerPlayerId, uint crateId, byte x, byte y, Vector3 senderPos, long tick)
        {
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e)) return false;
            if (!_crates.TryGetValue(crateId, out var crate) || crate.Storage == null) return false;
            // Used to refuse while somebody else had it open, because that player was editing a COPY that would
            // be written back over this. There is no copy any more -- viewers are repainted from the crate the
            // moment it changes -- so a grab off the shelf is fine with a crowd around it.
            if ((crate.Pos - senderPos).magnitude > StorageReach) return false;
            for (byte i = 0; i < crate.Storage.getItemCount(); i++)
            {
                var jar = crate.Storage.getItem(i);
                if (jar == null || jar.x != x || jar.y != y || jar.item == null) continue;
                // ADD FIRST, REMOVE ONLY ON SUCCESS. A full bag has to leave the item on the shelf; taking it
                // out and finding nowhere to put it is how an item stops existing (the same order OnPickupItem
                // uses, and the reason FitAttachmentTo was rewritten).
                if (e.Inventory.tryAddItemAuto(jar.item, out _) == PlayerInventory.AutoPlace.None) return false;
                crate.Storage.removeItem(i);
                // The taker's own bag AND the container's display digest both have to move: the first rides the
                // owner echo from this flag, the second is re-projected by ContainerNetSync off the changed grid.
                e.Dirty = true;
                ServerRepaintViewers(crate);   // ...and anyone standing IN the container watches it leave the grid
                return true;
            }
            return false;   // nothing in that cell -- a stale click, not a licence to take the neighbour
        }

        /// <summary>Close = save the STORAGE page back into the crate and clear the view (SP CloseCrate).</summary>
        public bool ServerCloseStorage(ushort ownerPlayerId, long tick)
        {
            if (!_byOwner.TryGetValue(ownerPlayerId, out var e) || e.OpenCrateId == 0) return false;
            uint closedId = e.OpenCrateId;
            if (_crates.TryGetValue(closedId, out var crate))
            {
                // ⚠ NO COPY-BACK. It used to save this player's page into the crate here, which is what made a
                // second viewer impossible: the last person to close would overwrite the whole grid with their
                // own snapshot of it, undoing everything anyone else did while they stood there. Every edit is
                // now written through the instant it happens (ServerPushView), so by the time anybody closes,
                // the crate has been current for a while and there is nothing left to save.
                crate.Viewers.Remove(ownerPlayerId);
            }
            // ⚠ DROP THE LATCH BEFORE TEARING THE VIEW DOWN. Once you have left the viewer set your page is no
            // longer a view of anything, and the teardown below must not read back as you emptying the container.
            // It happens to be safe as written -- clear() is silent and loadSize(0,0) then has nothing to
            // announce -- but that is a property of two other functions, not of this one, and ServerPushView's
            // dimension guard is the only other thing standing between a close and a wiped container.
            e.OpenCrateId = 0;
            var s = e.Inventory.items[PlayerInventory.STORAGE];
            s.clear();
            s.loadSize(0, 0);
            // The freezer view is cleared unconditionally, not just when the crate had one: a player who opens a
            // fridge and then a plain crate must not be left staring at the fridge's freezer contents.
            var fz = e.Inventory.items[PlayerInventory.FREEZER];
            fz.clear();
            fz.loadSize(0, 0);
            if (crate != null && !crate.IsOpen) CrateOpenChanged?.Invoke(closedId, false);   // last one out shuts the door
            e.Dirty = true;
            return true;
        }

        // the SP page copy (PlayerController.CopyPage): clear + resize + re-seat every jar cell-for-cell
        static void CopyPage(Items from, Items to, byte w, byte h)
        {
            to.clear();
            to.loadSize(w, h);
            for (byte i = 0; i < from.getItemCount(); i++)
            {
                var j = from.getItem(i);
                to.addItem(j.x, j.y, j.rot, j.item);
            }
        }

        // ---- IReplicatedSystem (owner-only, full-on-dirty) ----

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx) => WriteOwnerBlock(w, ctx.ClientPlayerId, always: true);

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            bool dirty = _byOwner.TryGetValue(ctx.ClientPlayerId, out var e) && e.LastChangedTick > baselineTick;
            WriteOwnerBlock(w, ctx.ClientPlayerId, always: dirty);
        }

        void WriteOwnerBlock(NetPakWriter w, ushort clientPlayerId, bool always)
        {
            if (!always || !_byOwner.TryGetValue(clientPlayerId, out var e)) { w.WriteUInt8(0); return; }
            var inv = e.Inventory;
            w.WriteUInt8(1);
            w.WriteUInt16(e.OwnerPlayerId);
            for (byte p = 0; p < PlayerInventory.PAGES; p++)
            {
                var page = inv.items[p];
                w.WriteUInt8(page.width);
                w.WriteUInt8(page.height);
                w.WriteUInt8(page.getItemCount());
                for (byte i = 0; i < page.getItemCount(); i++) WriteJar(w, page.getItem(i));
            }
            WriteWorn(w, inv.wornHat); WriteWorn(w, inv.wornGlasses); WriteWorn(w, inv.wornMask);
            WriteWorn(w, inv.wornShirt); WriteWorn(w, inv.wornVest); WriteWorn(w, inv.wornBackpack); WriteWorn(w, inv.wornPants);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadUInt8(out byte count) || count == 0) return;
            if (!r.ReadUInt16(out ushort owner)) return;

            // rebuild the whole replica from the wire (full-on-dirty: the block IS the state)
            var inv = new PlayerInventory();
            for (byte p = 0; p < PlayerInventory.PAGES; p++)
            {
                if (!r.ReadUInt8(out byte width) || !r.ReadUInt8(out byte height) || !r.ReadUInt8(out byte itemCount)) return;
                var page = inv.items[p];
                page.loadSize(width, height);
                for (byte i = 0; i < itemCount; i++)
                {
                    if (!ReadJar(r, out byte x, out byte y, out byte rot, out Item item)) return;
                    page.addItem(x, y, rot, item);
                }
            }
            if (!ReadWorn(r, out var hat) || !ReadWorn(r, out var glasses) || !ReadWorn(r, out var mask)) return;
            if (!ReadWorn(r, out var shirt) || !ReadWorn(r, out var vest) || !ReadWorn(r, out var backpack) || !ReadWorn(r, out var pants)) return;
            inv.wornHat = hat; inv.wornGlasses = glasses; inv.wornMask = mask;
            inv.wornShirt = shirt; inv.wornVest = vest; inv.wornBackpack = backpack; inv.wornPants = pants;

            if (!_byOwner.TryGetValue(owner, out var e))
            {
                e = new PlayerEntry { OwnerPlayerId = owner };
                _byOwner[owner] = e;
            }
            e.Inventory = inv;
            ReplicaUpdated?.Invoke(owner);
        }

        static void WriteJar(NetPakWriter w, ItemJar j)
        {
            w.WriteUInt8(j.x); w.WriteUInt8(j.y); w.WriteUInt8(j.rot);
            w.WriteUInt16(j.item?.id ?? 0);
            w.WriteUInt8(j.item?.amount ?? 0);
            w.WriteUInt8(j.item?.quality ?? 0);
            // gun state travels so a dropped-in-grid gun keeps its mag/firemode on the replica (Item fields)
            w.WriteInt16((short)(j.item?.gunAmmo ?? -1));
            w.WriteInt8((sbyte)(j.item?.gunFiremode ?? -1));
            w.WriteInt32(j.item?.gunMagId ?? -1);
            w.WriteInt32(j.item?.gunAttach ?? -1);
            // PER-SLOT ATTACHMENTS. These were added to Item after the schema was written and never joined it, so
            // every owner echo rebuilt the jar WITHOUT them: fitting a scope really did delete it from the server
            // grid (OnFitAttachment works), then the echo handed back a gun with no scope on it. The scope was
            // gone from the gun AND from the bag -- destroyed, the mirror image of the dupe we fixed in 076879ab.
            // gunAttachSeeded has to travel too, or AttachmentFit.SeedDefaults re-installs a gun's factory irons
            // on the next equip and silently undoes a detach. Review 2026-08-16.
            //
            // Gated behind one bit because the overwhelming majority of jars are not guns: a bandage costs 1 bit
            // here rather than 16 bytes. The bit is the ONLY thing that decides whether the four ids follow, on
            // both sides -- keep this block and ReadJar's edited together.
            bool att = j.item != null && (j.item.gunSightId >= 0 || j.item.gunBarrelId >= 0 || j.item.gunGripId >= 0
                                          || j.item.gunTacticalId >= 0 || j.item.gunChambered || j.item.gunAttachSeeded);
            w.WriteBit(att);
            if (att)
            {
                w.WriteInt32(j.item.gunSightId);
                w.WriteInt32(j.item.gunBarrelId);
                w.WriteInt32(j.item.gunGripId);
                w.WriteInt32(j.item.gunTacticalId);
                w.WriteBit(j.item.gunChambered);
                w.WriteBit(j.item.gunAttachSeeded);
            }
            // fuel-container level (gas can): server-owned -- a pump extract fills the can SERVER-side, and the
            // owner-inventory echo re-adopts it, so the level MUST ride the wire or a filled can shows empty on the
            // client ("can won't fill", the unified-SP regression from the old local-fill path). -1 (non-fuel /
            // fresh) clamps to 0; only fuel containers ever read it back (Mathf.Max(0) on use).
            w.WriteClampedFloat(Mathf.Max(0f, j.item?.fuelLevel ?? 0f), 12, 2);
            // fluid-CONTAINER contents (water bottle / canteen / soda / …): type + mL + quality, server-owned exactly like
            // fuelLevel. MUST ride the wire — the consuming loopback re-adopts the owner inventory through this schema, so
            // if the contents didn't travel, a drunk-empty bottle would round-trip to fluidAmount -1 and lazily REFILL to
            // full every sync (infinite drinking) / a filled canteen would reset to empty. -1 (fresh) round-trips faithfully
            // (WriteClampedFloat is signed) so the lazy-init sentinel is preserved; 20 int bits covers any container mL.
            w.WriteUInt8(j.item?.fluidType ?? 0);
            w.WriteClampedFloat(j.item?.fluidAmount ?? -1f, 20, 1);
            w.WriteUInt8(j.item?.fluidQuality ?? 0);
            w.WriteBit(j.item?.autoDrink ?? true);   // autodrink toggle (default on)
            // MAGAZINE CARTRIDGE LOCK. Item.magLoadedRound was added for the magazine load/unload and never
            // joined this schema -- the identical mistake the per-slot attachment block above documents,
            // repeated on a new field. Without it every owner echo rebuilds the jar with no lock, so a
            // part-loaded magazine forgets which cartridge it holds and will happily accept a mix on the
            // next drag. One byte, unconditional: gating it behind a bit would cost 9 bits for magazines to
            // save 8 for everything else, which is the wrong trade at one byte.
            w.WriteUInt8(Assets.MagRoundToId(j.item?.magLoadedRound));
            // COOKING. Item.cooked / Item.cookStyle -- the fourth and fifth fields to be added to Item, and the
            // first ones to join this schema in the SAME change that adds them rather than three features later.
            // The two blocks above are the record of what happens otherwise: an owner echo rebuilds the jar
            // without them, so a roast taken out of the oven and dragged one cell would come back raw and
            // unlabelled, exactly as a fitted scope came back missing and a part-loaded magazine came back
            // unlocked.
            //
            // GATED behind one bit, unlike magLoadedRound's unconditional byte, and the trade genuinely differs:
            // a cartridge lock is one byte against magazines-only, while this is TWO bytes against a set that is
            // even smaller (cooked FOOD, not all food). One bit for every bandage and bullet, 17 for a steak.
            bool cook = j.item != null && (j.item.cooked != 0 || j.item.cookStyle != 0);
            w.WriteBit(cook);
            if (cook) { w.WriteUInt8(j.item.cooked); w.WriteUInt8(j.item.cookStyle); }
            // FROZEN (v30), gated on the same argument as cooking and an even smaller set: only food that has
            // actually been in a freezer pays the byte, everything else pays the bit. SERVER-OWNED like `cooked`,
            // and for a sharper reason -- frozen food never spoils, so a client that could assert it could
            // preserve its stockpile for free.
            bool froz = j.item != null && j.item.frozen != 0;
            w.WriteBit(froz);
            if (froz) w.WriteUInt8(j.item.frozen);
        }

        static bool ReadJar(NetPakReader r, out byte x, out byte y, out byte rot, out Item item)
        {
            item = null;
            x = y = rot = 0;
            if (!r.ReadUInt8(out x) || !r.ReadUInt8(out y) || !r.ReadUInt8(out rot)) return false;
            if (!r.ReadUInt16(out ushort id)) return false;
            if (!r.ReadUInt8(out byte amount)) return false;
            if (!r.ReadUInt8(out byte quality)) return false;
            if (!r.ReadInt16(out short gunAmmo)) return false;
            if (!r.ReadInt8(out sbyte gunFiremode)) return false;
            if (!r.ReadInt32(out int gunMagId)) return false;
            if (!r.ReadInt32(out int gunAttach)) return false;
            // Per-slot attachments -- symmetric with WriteJar's `att` block; see the note there.
            int sight = -1, barrel = -1, grip = -1, tactical = -1;
            bool chambered = false, attachSeeded = false;
            if (!r.ReadBit(out bool att)) return false;
            if (att)
            {
                if (!r.ReadInt32(out sight)) return false;
                if (!r.ReadInt32(out barrel)) return false;
                if (!r.ReadInt32(out grip)) return false;
                if (!r.ReadInt32(out tactical)) return false;
                if (!r.ReadBit(out chambered)) return false;
                if (!r.ReadBit(out attachSeeded)) return false;
            }
            if (!r.ReadClampedFloat(12, 2, out float fuelLevel)) return false;   // gas-can fuel level (server-filled)
            if (!r.ReadUInt8(out byte fluidType)) return false;                  // fluid-container contents (server-owned)
            if (!r.ReadClampedFloat(20, 1, out float fluidAmount)) return false;
            if (!r.ReadUInt8(out byte fluidQuality)) return false;
            if (!r.ReadBit(out bool autoDrink)) return false;                    // autodrink toggle
            if (!r.ReadUInt8(out byte magRoundId)) return false;                 // magazine cartridge lock
            byte cooked = 0, cookStyle = 0;                                      // cooking -- symmetric with WriteJar's `cook` bit
            if (!r.ReadBit(out bool cook)) return false;
            if (cook) { if (!r.ReadUInt8(out cooked)) return false; if (!r.ReadUInt8(out cookStyle)) return false; }
            byte frozen = 0;                                                     // freezing -- symmetric with WriteJar's `froz` bit
            if (!r.ReadBit(out bool froz)) return false;
            if (froz) { if (!r.ReadUInt8(out frozen)) return false; }
            item = new Item(id, amount, quality) { gunAmmo = gunAmmo, gunFiremode = gunFiremode, gunMagId = gunMagId, gunAttach = gunAttach, fuelLevel = fuelLevel,
                                                   fluidType = fluidType, fluidAmount = fluidAmount, fluidQuality = fluidQuality, autoDrink = autoDrink,
                                                   gunSightId = sight, gunBarrelId = barrel, gunGripId = grip, gunTacticalId = tactical,
                                                   gunChambered = chambered, gunAttachSeeded = attachSeeded,
                                                   magLoadedRound = Assets.MagRoundFromId(magRoundId),
                                                   cooked = cooked, cookStyle = cookStyle, frozen = frozen };
            // The chambered round's AMMO TYPE is re-derived from the loaded magazine rather than sent: it is a
            // string, this stack has no string primitive, and the mag id it comes from is already on the wire.
            // One case loses fidelity by doing it this way and it is worth naming: after a TACTICAL swap the
            // chambered round keeps the PREVIOUS magazine's type, and that distinction does not survive an echo.
            if (chambered && gunMagId >= 0) item.gunChamberedType = Assets.find((ushort)gunMagId)?.ammoType;
            return true;
        }

        static void WriteWorn(NetPakWriter w, Item item)
        {
            w.WriteBit(item != null);
            if (item == null) return;
            w.WriteUInt16(item.id);
            w.WriteUInt8(item.amount);
            w.WriteUInt8(item.quality);
        }

        static bool ReadWorn(NetPakReader r, out Item item)
        {
            item = null;
            if (!r.ReadBit(out bool has)) return false;
            if (!has) return true;
            if (!r.ReadUInt16(out ushort id)) return false;
            if (!r.ReadUInt8(out byte amount)) return false;
            if (!r.ReadUInt8(out byte quality)) return false;
            item = new Item(id, amount, quality);
            return true;
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            var owners = new List<ushort>(_byOwner.Keys);
            owners.Sort();
            foreach (ushort id in owners) h = MixEntry(h, _byOwner[id]);
            return h;
        }

        /// <summary>Owner-only parity hash (same contract as SkillsReplication.StateHashFor).</summary>
        public ulong StateHashFor(ushort ownerPlayerId)
        {
            ulong h = NetHash.FnvOffset;
            if (_byOwner.TryGetValue(ownerPlayerId, out var e)) h = MixEntry(h, e);
            return h;
        }

        static ulong MixEntry(ulong h, PlayerEntry e)
        {
            h = NetHash.MixUInt32(h, e.OwnerPlayerId);
            for (byte p = 0; p < PlayerInventory.PAGES; p++)
            {
                var page = e.Inventory.items[p];
                h = NetHash.MixByte(h, page.width);
                h = NetHash.MixByte(h, page.height);
                h = NetHash.MixByte(h, page.getItemCount());
                for (byte i = 0; i < page.getItemCount(); i++)
                {
                    var j = page.getItem(i);
                    h = NetHash.MixByte(h, j.x); h = NetHash.MixByte(h, j.y); h = NetHash.MixByte(h, j.rot);
                    h = NetHash.MixUInt32(h, j.item?.id ?? 0u);
                    h = NetHash.MixByte(h, j.item?.amount ?? (byte)0);
                    h = NetHash.MixByte(h, j.item?.quality ?? (byte)0);
                    h = NetHash.MixUInt64(h, (ulong)(long)(j.item?.gunAmmo ?? -1));
                    h = NetHash.MixUInt64(h, (ulong)(long)(j.item?.gunFiremode ?? -1));
                    h = NetHash.MixUInt64(h, (ulong)(long)(j.item?.gunMagId ?? -1));
                    h = NetHash.MixUInt64(h, (ulong)(long)(j.item?.gunAttach ?? -1));
                    h = NetHash.MixFloat(h, NetQuantization.QuantizeClampedFloat(Mathf.Max(0f, j.item?.fuelLevel ?? 0f), 12, 2));   // gas-can fuel level, quantized to the wire value so both sides mix the same
                    h = NetHash.MixByte(h, j.item?.fluidType ?? (byte)0);   // fluid-container contents ride the parity hash too (quantized to the wire values)
                    h = NetHash.MixFloat(h, NetQuantization.QuantizeClampedFloat(j.item?.fluidAmount ?? -1f, 20, 1));
                    h = NetHash.MixByte(h, j.item?.fluidQuality ?? (byte)0);
                    h = NetHash.MixByte(h, (byte)((j.item?.autoDrink ?? true) ? 1 : 0));
                }
            }
            foreach (var worn in new[] { e.Inventory.wornHat, e.Inventory.wornGlasses, e.Inventory.wornMask,
                                         e.Inventory.wornShirt, e.Inventory.wornVest, e.Inventory.wornBackpack, e.Inventory.wornPants })
            {
                h = NetHash.MixByte(h, worn != null ? (byte)1 : (byte)0);
                if (worn != null)
                {
                    h = NetHash.MixUInt32(h, worn.id);
                    h = NetHash.MixByte(h, worn.amount);
                    h = NetHash.MixByte(h, worn.quality);
                }
            }
            return h;
        }
    }
}
