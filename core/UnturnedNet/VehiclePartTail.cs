using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// v80 (row 15A): the host's per-part vehicle health and fire lifecycle, appended to the System 9
    /// entity behind ONE presence bit (the v78 System 6 Energy/Wind precedent). The host keeps the exact
    /// values (bridge VehicleServices parts and BodyCombatServices fuse/burn); a remote previously had
    /// none of them and drew every car with a healthy engine and rotors, no fire and no burn clock.
    ///
    ///   EnginePart / MainRotorPart / TailRotorPart  u8 fraction of the part's spec maximum. 255 = full or
    ///       the vehicle has no such part; 0 = exactly dead. Any live remainder is at least 1, so "engine
    ///       dead" can never be produced by rounding.
    ///   EngineDrowned  the source Vehicle swamp latch (engine killed by water).
    ///   FuseLit        hull at zero, explosion fuse burning (BodyCombatServices Life.Fuse).
    ///   BurnTick       server tick the wreck started burning (Life.Burn), signed: a save restored early
    ///       can place it before tick 0. A replica derives burn seconds from its applied snapshot tick.
    ///
    /// An untouched vehicle costs the one presence bit, exactly as before.
    /// </summary>
    public sealed partial class VehicleReplication
    {
        public const byte PartFull = 255;

        /// <summary>Exact part health -> wire fraction (see the class note for the 0/1/255 rules).</summary>
        public static byte QuantizePart(float health, float maximum)
        {
            if (!(maximum > 0f) || health >= maximum) return PartFull;
            if (!(health > 0f)) return 0;
            int q = (int)System.Math.Round(health / maximum * 255f);
            return (byte)System.Math.Clamp(q, 1, 254);
        }

        /// <summary>Wire fraction -> absolute health against the same spec maximum.</summary>
        public static float PartHealth(byte part, float maximum) => maximum > 0f ? maximum * part / 255f : 0f;

        /// <summary>Host write. Dirties only on a real change of the quantized tail.</summary>
        public void ServerSetParts(NetId id, byte engine, byte mainRotor, byte tailRotor, bool drowned, bool fuseLit, int? burnTick, long tick)
        {
            if (!_vehicles.TryGet(id, out var e)) return;
            if (e.EnginePart == engine && e.MainRotorPart == mainRotor && e.TailRotorPart == tailRotor
                && e.EngineDrowned == drowned && e.FuseLit == fuseLit && e.BurnTick == burnTick) return;
            e.EnginePart = engine; e.MainRotorPart = mainRotor; e.TailRotorPart = tailRotor;
            e.EngineDrowned = drowned; e.FuseLit = fuseLit; e.BurnTick = burnTick;
            e.LastChangedTick = tick;
        }

        static bool HasPartTail(VehicleEntity e) => e.EnginePart != PartFull || e.MainRotorPart != PartFull
            || e.TailRotorPart != PartFull || e.EngineDrowned || e.FuseLit || e.BurnTick.HasValue;

        static void WritePartTail(NetPakWriter w, VehicleEntity e)
        {
            bool present = HasPartTail(e);
            w.WriteBit(present);
            if (!present) return;
            w.WriteUInt8(e.EnginePart); w.WriteUInt8(e.MainRotorPart); w.WriteUInt8(e.TailRotorPart);
            w.WriteBit(e.EngineDrowned); w.WriteBit(e.FuseLit);
            w.WriteBit(e.BurnTick.HasValue);
            if (e.BurnTick.HasValue) w.WriteInt32(e.BurnTick.Value);
        }

        static bool ReadPartTail(NetPakReader r, out (byte engine, byte main, byte tail, bool drowned, bool fuse, int? burn) tail)
        {
            tail = (PartFull, PartFull, PartFull, false, false, null);
            if (!r.ReadBit(out bool present)) return false;
            if (!present) return true;
            if (!r.ReadUInt8(out byte engine) || !r.ReadUInt8(out byte main) || !r.ReadUInt8(out byte rotor)
                || !r.ReadBit(out bool drowned) || !r.ReadBit(out bool fuse) || !r.ReadBit(out bool burning)) return false;
            int burn = 0;
            if (burning && !r.ReadInt32(out burn)) return false;
            tail = (engine, main, rotor, drowned, fuse, burning ? burn : null);
            return true;
        }

        static ulong MixPartTail(ulong h, VehicleEntity e)
        {
            h = NetHash.MixByte(h, e.EnginePart); h = NetHash.MixByte(h, e.MainRotorPart); h = NetHash.MixByte(h, e.TailRotorPart);
            h = NetHash.MixByte(h, (byte)((e.EngineDrowned ? 1 : 0) | (e.FuseLit ? 2 : 0) | (e.BurnTick.HasValue ? 4 : 0)));
            return NetHash.MixUInt32(h, unchecked((uint)(e.BurnTick ?? 0)));
        }
    }
}
