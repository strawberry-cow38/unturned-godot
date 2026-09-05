using System.Collections.Generic;

namespace SDG.Unturned
{
    /// <summary>What a throwable DOES when its fuse runs out. Retail's ItemThrowableAsset carries these as
    /// independent flags (isExplosive / isFlash / isSticky) rather than an enum; this is an enum because the
    /// three families strawberry asked for are mutually exclusive and the value has to cross the WIRE, where a
    /// flag set is three bits that can disagree with each other.</summary>
    public enum EThrowableKind : byte { Explosive = 0, Smoke = 1, Flare = 2 }

    /// <summary>One throwable's numbers. Damage/radius are READ OFF THE RETAIL .dat
    /// (Bundles/Items/Throwables/&lt;name&gt;/&lt;name&gt;.dat) -- nothing here is invented except the two effect
    /// durations, which retail hard-codes in UseableThrowable rather than exposing as data.</summary>
    public sealed class ThrowableDef
    {
        public ushort Id;
        public string Name;
        public EThrowableKind Kind;
        public float PlayerDamage, ZombieDamage, AnimalDamage, VehicleDamage;
        public float Radius;          // .dat Range -- the blast radius for an explosive, the cloud radius for smoke
        public float EffectSeconds;   // how long a smoke cloud / flare burns before it is gone. 0 for an explosive.
        public bool Explosive => Kind == EThrowableKind.Explosive;
    }

    /// <summary>The throwable table, shared by BOTH sides: the game layer equips/throws off it and
    /// ServerCombat resolves an incoming throw against it, so a client and a server can never disagree about
    /// what a given item id does.
    ///
    /// GROUNDED IN THE RIP, deliberately. Every damage number and radius below was read out of the retail
    /// .dat on this box, not guessed -- and reading them settled two things a guess would have got wrong: the
    /// Makeshift Grenade has NO Vehicle_Damage key at all (so it does nothing to a car, where the frag does
    /// 100), and the smoke and flare .dats carry no damage keys and no `Explosive` flag whatsoever, which is
    /// the source's own statement that they are pure effect.
    ///
    /// WHAT IS NOT HERE: the Flashbang (1346, `Flash`), Sticky Grenade (1100), Impact Grenade (1520), Bounce
    /// Grenade (1838) and Snowball (1132). Each is a DIFFERENT mechanic -- a screen-white disorient, a stick-
    /// on-contact fuse, a detonate-on-contact fuse, a bounce-then-launch -- and strawberry asked for grenades,
    /// smoke and flares. Shipping a "Sticky Grenade" that is a plain frag would be a lie the player can only
    /// discover by dying to it, so those ids stay unequippable until their mechanic exists.</summary>
    public static class Throwables
    {
        /// <summary>Fuse for every throwable, in seconds. THREE, not retail's 2.5: strawberry 2026-09-05,
        /// "3s fuse before detonation". ItemThrowableAsset.fuseLength defaults to 2.5 and none of the .dats
        /// override it, so this is a deliberate game-feel change rather than a value read off the rip.</summary>
        public const float FuseSeconds = 3f;
        /// <summary>The same fuse on the server's 50 Hz combat clock.</summary>
        public const int FuseTicks = 150;

        static readonly Dictionary<ushort, ThrowableDef> _byId = Build();

        static Dictionary<ushort, ThrowableDef> Build()
        {
            var d = new Dictionary<ushort, ThrowableDef>();

            // ---- explosives (Throwables/Grenade, Throwables/Grenade_Makeshift) ----
            Add(d, new ThrowableDef { Id = 254,  Name = "Fragmentation Grenade", Kind = EThrowableKind.Explosive,
                                      PlayerDamage = 175f, ZombieDamage = 175f, AnimalDamage = 175f, VehicleDamage = 100f, Radius = 8f });
            Add(d, new ThrowableDef { Id = 1242, Name = "Makeshift Grenade",     Kind = EThrowableKind.Explosive,
                                      PlayerDamage = 150f, ZombieDamage = 150f, AnimalDamage = 150f, VehicleDamage = 0f,   Radius = 6f });

            // ---- flares (Throwables/Flare_*) : no damage keys in the .dat, so none here ----
            // 45 s of burn is a choice, not a ripped value -- retail's flare effect length lives in code.
            for (ushort id = 255; id <= 260; id++)
                Add(d, new ThrowableDef { Id = id, Name = FlareName(id), Kind = EThrowableKind.Flare, Radius = 12f, EffectSeconds = 45f });

            // ---- smoke (Throwables/Smoke_*) : likewise no damage, pure effect ----
            for (ushort id = 261; id <= 268; id++)
                Add(d, new ThrowableDef { Id = id, Name = SmokeName(id), Kind = EThrowableKind.Smoke, Radius = 6f, EffectSeconds = 22f });

            return d;
        }

        static void Add(Dictionary<ushort, ThrowableDef> d, ThrowableDef t) => d[t.Id] = t;

        static string FlareName(ushort id) => id switch
        {
            255 => "Blue Flare", 256 => "Green Flare", 257 => "Orange Flare",
            258 => "Purple Flare", 259 => "Red Flare", _ => "Yellow Flare",
        };

        static string SmokeName(ushort id) => id switch
        {
            261 => "Black Smoke", 262 => "Blue Smoke", 263 => "Green Smoke", 264 => "Orange Smoke",
            265 => "Purple Smoke", 266 => "Red Smoke", 267 => "White Smoke", _ => "Yellow Smoke",
        };

        public static ThrowableDef Find(ushort id) => _byId.TryGetValue(id, out var t) ? t : null;
        public static bool Is(ushort id) => _byId.ContainsKey(id);
        public static IEnumerable<ThrowableDef> All => _byId.Values;
        public static int Count => _byId.Count;
    }
}
