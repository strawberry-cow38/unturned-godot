namespace SDG.Unturned
{
    /// <summary>Retail's EPlayerGesture, in retail's ORDER, because the value goes on the wire as a byte
    /// (PlayerAnimator's [NetEnum]) and a reordering would silently mean something else on the other end.</summary>
    public enum EPlayerGesture : byte
    {
        NONE,
        INVENTORY_START,
        INVENTORY_STOP,
        PICKUP,
        PUNCH_LEFT,
        PUNCH_RIGHT,
        SURRENDER_START,
        SURRENDER_STOP,
        POINT,
        WAVE,
        SALUTE,
        ARREST_START,
        ARREST_STOP,
        REST_START,
        REST_STOP,
        FACEPALM,
        T_POSE_START,
        T_POSE_STOP,
    }

    /// <summary>Everything about one gesture, in ONE place: what it looks like, whether it is a state you stay
    /// in, who is allowed to ask for it, and whether anyone else sees it.
    ///
    /// Split tables are how this codebase keeps hurting itself -- the equip chain had three copies of "what is
    /// holdable" and four features fell out of two of them on 2026-09-11. So the clip NAME lives here beside the
    /// rules rather than in a content-side lookup of its own, even though core is otherwise engine-free: it is a
    /// string, and one list that cannot disagree with itself is worth more than the tidiness.</summary>
    public sealed class GestureDef
    {
        public EPlayerGesture Id;
        /// <summary>rig.json clip (tools/extract_gesture_anims.py). Null = no clip: T_POSE is the bind pose
        /// itself, and the PUNCHes reuse the Attack_n clips the rig already had.</summary>
        public string Clip;
        /// <summary>A STATE you stay in until its Stop arrives (retail plays these with loop=true), rather than
        /// a one-shot that ends itself.</summary>
        public bool Loops;
        /// <summary>The gesture that ends this one, or NONE if it ends itself.</summary>
        public EPlayerGesture Stops;
        /// <summary>Can a PLAYER ask for this? PlayerAnimator.ReceiveGestureRequest:830-841 lists exactly which
        /// requests it will act on; PICKUP, the PUNCHes and the ARRESTs are system-driven and a client asking
        /// for them is ignored -- which is the whole reason that list is a whitelist and not a blacklist.</summary>
        public bool PlayerRequestable;
        /// <summary>Do other players see it? Everything except the INVENTORY pair, which is a local notification
        /// that you opened your bag (ReceiveGestureRequest:843).</summary>
        public bool Broadcast;
        /// <summary>Stance this gesture demands, or null. Source only pins two (PlayerAnimator:868-877).</summary>
        public EPlayerStance? RequiredStance;
    }

    /// <summary>The gesture table and the rules for entering one. Engine-free so the server can run the same
    /// admission test the client does rather than a lookalike of it.</summary>
    public static class GestureRules
    {
        static GestureDef D(EPlayerGesture id, string clip, bool loops, EPlayerGesture stops,
                            bool req, bool bcast, EPlayerStance? stance = null)
            => new GestureDef { Id = id, Clip = clip, Loops = loops, Stops = stops,
                                PlayerRequestable = req, Broadcast = bcast, RequiredStance = stance };

        static readonly GestureDef[] Table =
        {
            D(EPlayerGesture.NONE,            null,                false, EPlayerGesture.NONE, false, false),
            // The bag. NOT broadcast: it tells your own client you are in the inventory, and retail uses it to
            // close a storage you walked away from -- nobody else needs to watch you rummage.
            D(EPlayerGesture.INVENTORY_START, "Gesture_Inventory", true,  EPlayerGesture.INVENTORY_STOP, true,  false),
            D(EPlayerGesture.INVENTORY_STOP,  null,                false, EPlayerGesture.NONE, true,  false),
            // System-driven: ItemManager fires this when you actually pick something up (ItemManager.cs:375).
            D(EPlayerGesture.PICKUP,          "Gesture_Pickup",    false, EPlayerGesture.NONE, false, true),
            // The punches have no Gesture_ clip -- they reuse the Attack_n the rig already carries.
            D(EPlayerGesture.PUNCH_LEFT,      null,                false, EPlayerGesture.NONE, false, true),
            D(EPlayerGesture.PUNCH_RIGHT,     null,                false, EPlayerGesture.NONE, false, true),
            // HANDS UP. The one gesture with mechanical weight: it is the precondition for being handcuffed
            // (UseableArrestStart requires the target be in SURRENDER_START) and a sentry holds fire on someone
            // in it (InteractableSentry.cs:433). So it is not decoration, and it is why this system exists.
            D(EPlayerGesture.SURRENDER_START, "Gesture_Surrender", true,  EPlayerGesture.SURRENDER_STOP, true, true),
            D(EPlayerGesture.SURRENDER_STOP,  null,                false, EPlayerGesture.NONE, true,  true),
            D(EPlayerGesture.POINT,           "Gesture_Point",     false, EPlayerGesture.NONE, true,  true),
            D(EPlayerGesture.WAVE,            "Gesture_Wave",      false, EPlayerGesture.NONE, true,  true),
            D(EPlayerGesture.SALUTE,          "Gesture_Salute",    false, EPlayerGesture.NONE, true,  true),
            // CUFFED. Applied BY a captor, never requested, and while you are in it you can make no other
            // gesture at all (ReceiveGestureRequest:815) -- the first rule in the handler, before anything else.
            D(EPlayerGesture.ARREST_START,    "Gesture_Arrest",    true,  EPlayerGesture.ARREST_STOP, false, true),
            D(EPlayerGesture.ARREST_STOP,     null,                false, EPlayerGesture.NONE, false, true),
            // Sitting down on the spot. Crouch-only in source, which is the stance the clip was authored from.
            D(EPlayerGesture.REST_START,      "Gesture_Rest",      true,  EPlayerGesture.REST_STOP, true, true, EPlayerStance.CROUCH),
            D(EPlayerGesture.REST_STOP,       null,                false, EPlayerGesture.NONE, true,  true),
            D(EPlayerGesture.FACEPALM,        "Gesture_Facepalm",  false, EPlayerGesture.NONE, true,  true),
            // T-pose has no clip: it IS the bind pose, which is what you get by playing nothing.
            D(EPlayerGesture.T_POSE_START,    null,                true,  EPlayerGesture.T_POSE_STOP, true, true, EPlayerStance.STAND),
            D(EPlayerGesture.T_POSE_STOP,     null,                false, EPlayerGesture.NONE, true,  true),
        };

        public static GestureDef Of(EPlayerGesture g)
        {
            int i = (int)g;
            return (uint)i < (uint)Table.Length ? Table[i] : Table[0];
        }

        /// <summary>The table is indexed by the enum value, so it only works while the two agree. Asserted by a
        /// test rather than trusted: inserting a gesture in the middle would otherwise shift every rule by one
        /// and the symptom would be "salutes make you surrender", which nobody would guess at.</summary>
        public static bool TableIsAligned()
        {
            for (int i = 0; i < Table.Length; i++) if ((int)Table[i].Id != i) return false;
            return Table.Length == System.Enum.GetValues(typeof(EPlayerGesture)).Length;
        }

        public static string ClipOf(EPlayerGesture g) => Of(g).Clip;
        public static bool Loops(EPlayerGesture g) => Of(g).Loops;

        /// <summary>Why a gesture request is refused, or null if it is allowed. The ORDER is source's order
        /// (ReceiveGestureRequest 815-841), and it is observable: an arrested player holding a gun is told they
        /// are cuffed rather than told to put the gun away, because the cuffs are the thing they cannot fix.</summary>
        public static string RefusalFor(EPlayerGesture want, EPlayerGesture current, EPlayerStance stance, bool hasUseable)
        {
            var def = Of(want);
            if (!def.PlayerRequestable) return "That one isn't yours to ask for.";
            if (current == EPlayerGesture.ARREST_START) return "You're cuffed.";
            if (hasUseable) return "Not with something in your hands.";
            if (stance == EPlayerStance.PRONE || stance == EPlayerStance.DRIVING || stance == EPlayerStance.SITTING)
                return "Not from there.";
            if (def.RequiredStance.HasValue && stance != def.RequiredStance.Value)
                return $"Only while {def.RequiredStance.Value.ToString().ToLowerInvariant()}.";
            return null;
        }

        public static bool CanRequest(EPlayerGesture want, EPlayerGesture current, EPlayerStance stance, bool hasUseable)
            => RefusalFor(want, current, stance, hasUseable) == null;

        /// <summary>What the gesture state becomes after this one is accepted. A STOP only lands if you are in
        /// the matching START -- otherwise it is a no-op, which is what keeps a stray SURRENDER_STOP from
        /// clearing the ARREST you are actually in.</summary>
        public static EPlayerGesture Apply(EPlayerGesture current, EPlayerGesture accepted)
        {
            var def = Of(accepted);
            if (def.Loops) return accepted;                       // entering a state
            foreach (var d in Table)                              // a STOP: only ends its own START
                if (d.Stops == accepted) return current == d.Id ? EPlayerGesture.NONE : current;
            return current;                                       // a one-shot leaves the state alone
        }
    }
}
