using System;
using System.Collections.Generic;
using SDG.NetPak;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>v59 (id 69): "drink the container at (Page,X,Y)" -- the held bottle's LMB chug. The id rides along so a
    /// stale grid cannot drink whatever has since moved into that cell.</summary>
    public struct DrinkFluidCommand
    {
        public byte Page, X, Y;
        public ushort Id;
        public void Write(NetPakWriter w) { w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt16(Id); }
        public static bool TryRead(NetPakReader r, out DrinkFluidCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y) || !r.ReadUInt16(out ushort id)) return false;
            cmd = new DrinkFluidCommand { Page = p, X = x, Y = y, Id = id };
            return true;
        }
    }

    /// <summary>v59 (id 70): "fill the container at (Page,X,Y) at the tap I am standing at". The tap is not named -- the
    /// server checks that one is running within reach of where it has the player, which is the only part a client
    /// could lie about.</summary>
    public struct FillAtTapCommand
    {
        public byte Page, X, Y;
        public ushort Id;
        public void Write(NetPakWriter w) { w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt16(Id); }
        public static bool TryRead(NetPakReader r, out FillAtTapCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte p) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y) || !r.ReadUInt16(out ushort id)) return false;
            cmd = new FillAtTapCommand { Page = p, X = x, Y = y, Id = id };
            return true;
        }
    }

    /// <summary>
    /// AUTODRINK, on the server. The per-bottle opt-in and the "first safe bottle in your own pages" rule are unchanged
    /// (FluidRules.ActiveAutoDrink); what moved is WHO drinks. It used to run in the client's UpdateVitals, after the
    /// early return that hands vitals to the server -- so in every game that has a server, which is all of them, it
    /// never ran at all, while the bag still showed the droplet saying it would.
    ///
    /// A sip of <see cref="FluidRules.SipML"/> every <see cref="Interval"/> seconds while Water is under
    /// <see cref="Floor"/>, the same numbers the client used.
    /// </summary>
    public sealed class ServerAutoDrink
    {
        public const float Floor = 0.5f;
        public const float Interval = 0.7f;

        readonly InventoryReplication _inventories;
        readonly PlayerVitalsReplication _vitals;
        readonly Dictionary<ushort, float> _cooldown = new Dictionary<ushort, float>();

        /// <summary>Is this player alive? A corpse does not drink. Unset = everyone counts.</summary>
        public Func<ushort, bool> IsAlive;

        /// <summary>Sips taken, all players -- for tests and the net log.</summary>
        public long Sips;
        /// <summary>WHY a player was passed over, per branch, so a zero names its cause instead of hiding it.</summary>
        public long Steps, OnCooldown, NotThirsty, Dead, NoBottle, Refused;

        public ServerAutoDrink(InventoryReplication inventories, PlayerVitalsReplication vitals)
        {
            _inventories = inventories; _vitals = vitals;
        }

        public void Step(float dt, long tick)
        {
            if (dt <= 0f || _vitals == null) return;
            Steps++;
            foreach (var e in _inventories.Owners)
            {
                if (e?.Inventory == null) continue;
                ushort pid = e.OwnerPlayerId;
                _cooldown.TryGetValue(pid, out float cd);
                if (cd > 0f) { _cooldown[pid] = cd - dt; OnCooldown++; continue; }
                if (!_vitals.TryGet(pid, out var v) || v.Sim.Water >= Floor) { NotThirsty++; continue; }
                if (IsAlive != null && !IsAlive(pid)) { Dead++; continue; }
                var bottle = FluidRules.ActiveAutoDrink(e.Inventory);
                if (bottle == null) { NoBottle++; continue; }
                if (FluidRules.Sip(bottle, Assets.find(bottle.id), out float hydration, out _) <= 0f) { Refused++; continue; }
                _vitals.ServerRaise(pid, 0f, hydration, 0f, 0f, false, false, tick);
                _inventories.ServerMarkDirty(pid);   // a bare field write raises no grid event; without this the sip never echoes
                _cooldown[pid] = Interval;
                Sips++;
            }
        }
    }
}
