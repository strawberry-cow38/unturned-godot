namespace SDG.Unturned
{
    /// <summary>The three restraint items, from their .dat on the box -- Bundles/Items/Arrest_Starts and
    /// Arrest_Ends. Read, not assumed: the whole design difference between cuffs and a zip tie is two numbers
    /// and one missing key, and inventing either would have produced two items that behave the same.</summary>
    public static class ArrestDef
    {
        public const ushort Handcuffs = 1195;      // Arrest_Start, Strength 128
        public const ushort HandcuffsKey = 1196;   // Arrest_End,   Recover 1195
        public const ushort CableTie = 1197;       // Arrest_Start, Strength 64

        /// <summary>How many struggles this restraint takes to break, or 0 if the item is not a restraint.
        /// Handcuffs 128, cable tie 64 -- a tie is exactly half the work.</summary>
        public static ushort StrengthOf(ushort itemId) => itemId switch
        {
            Handcuffs => 128,
            CableTie => 64,
            _ => (ushort)0,
        };

        public static bool IsRestraint(ushort itemId) => StrengthOf(itemId) > 0;

        /// <summary>An Arrest_End item (a key), and what it hands BACK when used -- the .dat's `Recover`.
        /// The handcuffs key returns the cuffs; a cable tie has no key at all, which is the other half of the
        /// trade: cuffs are twice as strong but someone with the key can undo them in a second, and a tie can
        /// only ever be struggled out of.</summary>
        public static bool IsKey(ushort itemId) => itemId == HandcuffsKey;
        public static ushort RecoversItem(ushort keyId) => keyId == HandcuffsKey ? Handcuffs : (ushort)0;
    }

    /// <summary>Who is cuffed, with what, and how close they are to being out of it.
    ///
    /// Engine-free and server-owned. Every rule here is one a client must not be able to decide for itself:
    /// arresting someone who never surrendered, arresting yourself so nobody else can, or announcing that you
    /// are free. It is deliberately a separate object from the gesture state -- the GESTURE is what everyone
    /// SEES (ARREST_START on the wire), and this is the bookkeeping behind it that no one but the server needs.
    ///
    /// RETAIL'S ESCAPE, which is better than anything I would have invented: you struggle out by LEANING.
    /// PlayerAnimator.cs:1144-1172 decrements captorStrength on every lean CHANGE to a side, and at zero the
    /// cuffs come off with a metal clatter. So a cable tie is 64 flicks of Q/E and handcuffs are 128 -- the
    /// numbers in the .dat are not flavour, they are the escape timer.</summary>
    public sealed class ArrestSim
    {
        readonly System.Collections.Generic.Dictionary<ushort, Record> _byPlayer = new();

        public struct Record
        {
            public ushort CaptorId;      // who put them in it
            public ushort ItemId;        // what they were cuffed WITH -- decides the strength and what a key returns
            public int Strength;         // struggles remaining
        }

        public bool IsArrested(ushort playerId) => _byPlayer.ContainsKey(playerId);
        public bool TryGet(ushort playerId, out Record rec) => _byPlayer.TryGetValue(playerId, out rec);

        /// <summary>Cuff someone. Refuses unless the target is SURRENDERING -- retail gates on the gesture
        /// (UseableArrestStart: `info.player.animator.gesture == SURRENDER_START`), which is what stops this
        /// from being "aim at anyone and click". Refuses a second arrest, and refuses arresting yourself: a
        /// self-arrest would be a way to become uncuffable, since an arrested player cannot be arrested again.</summary>
        public bool TryArrest(ushort captorId, ushort targetId, ushort restraintId, bool targetIsSurrendering)
        {
            if (captorId == targetId) return false;
            if (!targetIsSurrendering) return false;
            if (!ArrestDef.IsRestraint(restraintId)) return false;
            if (_byPlayer.ContainsKey(targetId)) return false;
            _byPlayer[targetId] = new Record { CaptorId = captorId, ItemId = restraintId, Strength = ArrestDef.StrengthOf(restraintId) };
            return true;
        }

        /// <summary>A lean flicked to one side. Returns true when THAT struggle was the one that broke it.
        /// Counts CHANGES, so holding the key down is worth one -- source compares against lastLean, and the
        /// alternative (counting frames held) would make a cable tie a quarter-second rather than a fight.</summary>
        public bool Struggle(ushort playerId)
        {
            if (!_byPlayer.TryGetValue(playerId, out var rec)) return false;
            rec.Strength--;
            if (rec.Strength > 0) { _byPlayer[playerId] = rec; return false; }
            _byPlayer.Remove(playerId);
            return true;
        }

        /// <summary>Unlock someone with a key. Returns the item to hand back (the .dat's Recover) or 0 if this
        /// did not free anybody. Note it does NOT require the freer to be the captor: retail lets anyone with
        /// the key undo the cuffs, which is what makes the key worth carrying and worth taking off a body.</summary>
        public ushort TryUnlock(ushort targetId, ushort keyId)
        {
            if (!ArrestDef.IsKey(keyId)) return 0;
            if (!_byPlayer.TryGetValue(targetId, out var rec)) return 0;
            _byPlayer.Remove(targetId);
            // What comes back is what they were cuffed WITH, not what the key nominally recovers: a key used on
            // a cable tie must not conjure handcuffs out of it. Today Recover and the restraint agree (only the
            // cuffs have a key), and this is written so that stays true if a second key is ever added.
            return rec.ItemId == ArrestDef.RecoversItem(keyId) ? rec.ItemId : (ushort)0;
        }

        /// <summary>Drop a disconnecting player's record. Also releases anyone THEY were holding? No -- the
        /// cuffs stay on. A captor logging off must not free their prisoner, or disconnecting becomes the
        /// fastest way to undo your own arrest.</summary>
        public void Forget(ushort playerId) => _byPlayer.Remove(playerId);

        public int Count => _byPlayer.Count;
        public void ClearForTest() => _byPlayer.Clear();
    }
}
