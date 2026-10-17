using SDG.NetPak;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    /// <summary>
    /// v80 (row 15A): the host's weather and calendar as a singleton snapshot block (SystemId 21).
    ///
    /// The Godot source never replicated weather: every process seeded its own WeatherManager
    /// (<c>seed != 0 ? seed : GD.Randi()</c>) and its own day count, so a joined client rained on its own
    /// schedule and its body-temperature band read a day/weather the server never had. The hybrid host owns
    /// one <c>WeatherSim</c> (bridge WorldEnvironment); this block carries exactly what a remote needs to
    /// rebuild the same <c>State()</c> and temperature inputs without a second simulation:
    ///
    ///   seed + base day       -> WeatherOutlook.ForDay / DayAt (pure functions of these and the clock)
    ///   weather type index    -> name, lightning, wind/fog scalars from the shared, content-hashed type table
    ///                            (behind a presence bit: clear sky has no type)
    ///   stage                 -> WeatherStage (2 bits)
    ///   blend, puddle, snow   -> u8 fractions of [0,1]
    ///
    /// Values are quantized on the server before they are stored, so authority and replicas hash
    /// identically; the block dirties only on a REAL change. During a 20 s fade the blend byte moves about
    /// 13 times a second, otherwise the block costs one bit per delta snapshot.
    /// </summary>
    public sealed class EnvironmentReplication : IReplicatedSystem
    {
        public const int StageBits = 2;

        public bool HasEnvironment { get; private set; }
        public int Seed { get; private set; }
        public int BaseDay { get; private set; }
        /// <summary>Index into the shared weather type table, or -1 for none (clear).</summary>
        public int WeatherType { get; private set; } = -1;
        public WeatherStage Stage { get; private set; }
        public byte Blend { get; private set; }
        public byte Puddle { get; private set; }
        public byte Snow { get; private set; }
        public long LastChangedTick { get; private set; }

        public float BlendAlpha => Blend / 255f;
        public float PuddleLevel => Puddle / 255f;
        public float SnowCover => Snow / 255f;

        public byte SystemId => ReplicationIds.SystemEnvironment;

        // see DeployableReplication.Stamp: mutation stamps are tick+1 (compose-boundary off-by-one)
        static long Stamp(long tick) => tick + 1;

        /// <summary>[0,1] -> u8, round to nearest. Exact 0 and exact 1 survive, so "clear" stays clear.</summary>
        public static byte Quantize01(float value)
        {
            if (!(value > 0f)) return 0;          // also maps NaN to 0
            if (value >= 1f) return 255;
            return (byte)System.Math.Round(value * 255f);
        }

        /// <summary>Server side. Dirties only when a quantized field actually changes.</summary>
        public void ServerSet(int seed, int baseDay, int weatherType, WeatherStage stage,
                              float blend, float puddle, float snow, long tick)
        {
            if (weatherType < -1 || weatherType > byte.MaxValue) weatherType = -1;
            byte b = Quantize01(blend), p = Quantize01(puddle), s = Quantize01(snow);
            if (HasEnvironment && seed == Seed && baseDay == BaseDay && weatherType == WeatherType && stage == Stage
                && b == Blend && p == Puddle && s == Snow) return;
            HasEnvironment = true;
            Seed = seed; BaseDay = baseDay; WeatherType = weatherType; Stage = stage;
            Blend = b; Puddle = p; Snow = s;
            LastChangedTick = Stamp(tick);
        }

        public void WriteFull(NetPakWriter w, in ReplicationContext ctx)
        {
            w.WriteBit(HasEnvironment);
            if (HasEnvironment) WritePayload(w);
        }

        public void WriteDelta(NetPakWriter w, in ReplicationContext ctx, long baselineTick)
        {
            bool changed = HasEnvironment && LastChangedTick > baselineTick;
            w.WriteBit(changed);
            if (changed) WritePayload(w);
        }

        public void ReadSnapshot(NetPakReader r, bool full)
        {
            if (!r.ReadBit(out bool present)) return;
            if (!present)
            {
                if (full) { HasEnvironment = false; WeatherType = -1; }   // full: "none configured"; delta: unchanged
                return;
            }
            // Parse the whole payload before adopting any of it: a truncated block must not leave a half-new state.
            if (!r.ReadInt32(out int seed) || !r.ReadInt32(out int baseDay)) return;
            if (!r.ReadBit(out bool hasType)) return;
            uint type = 0;
            if (hasType && !r.ReadBits(8, out type)) return;
            if (!r.ReadBits(StageBits, out uint stage)) return;
            if (!r.ReadUInt8(out byte blend) || !r.ReadUInt8(out byte puddle) || !r.ReadUInt8(out byte snow)) return;
            HasEnvironment = true;
            Seed = seed; BaseDay = baseDay; WeatherType = hasType ? (int)type : -1; Stage = (WeatherStage)stage;
            Blend = blend; Puddle = puddle; Snow = snow;
        }

        public ulong StateHash()
        {
            ulong h = NetHash.FnvOffset;
            h = NetHash.MixByte(h, HasEnvironment ? (byte)1 : (byte)0);
            if (!HasEnvironment) return h;
            h = NetHash.MixUInt32(h, unchecked((uint)Seed));
            h = NetHash.MixUInt32(h, unchecked((uint)BaseDay));
            h = NetHash.MixUInt32(h, unchecked((uint)WeatherType));
            h = NetHash.MixByte(h, (byte)Stage);
            h = NetHash.MixByte(h, Blend);
            h = NetHash.MixByte(h, Puddle);
            h = NetHash.MixByte(h, Snow);
            return h;
        }

        void WritePayload(NetPakWriter w)
        {
            w.WriteInt32(Seed);
            w.WriteInt32(BaseDay);
            w.WriteBit(WeatherType >= 0);
            if (WeatherType >= 0) w.WriteBits((uint)WeatherType, 8);
            w.WriteBits((uint)Stage & ((1u << StageBits) - 1), StageBits);
            w.WriteUInt8(Blend);
            w.WriteUInt8(Puddle);
            w.WriteUInt8(Snow);
        }
    }

    public sealed partial class NetWorldServer
    {
        /// <summary>v80 SystemId 21: host weather/calendar (EnvironmentReplication). Published by the host's
        /// weather owner each tick; dirties only on a quantized change.</summary>
        public readonly EnvironmentReplication Environment = new EnvironmentReplication();
    }

    public sealed partial class NetWorldClient
    {
        public readonly EnvironmentReplication Environment = new EnvironmentReplication();
    }
}
