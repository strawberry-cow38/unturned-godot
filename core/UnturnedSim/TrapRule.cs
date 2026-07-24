namespace SDG.Unturned
{
    // What walked into the trap's trigger. Mirrors the src tag checks in InteractableTrap.NotifyTrapEntered
    // ("Player" / "Agent" -> zombie, else the animal lookup); Other = anything the src's lookups reject.
    public enum TrapTarget { Other, Player, Zombie, Animal }

    // What the contact does. None = the trap noticed but did nothing (still burns the cooldown -- see Consumed).
    public enum TrapAction { None, Explode, ShredPlayer, DamageZombie, DamageAnimal }

    public struct TrapDecision
    {
        public TrapAction Action;
        public bool Consumed;    // did this contact latch lastTriggered? (src sets it BEFORE deciding what to do)
        public float SelfWear;   // Trap_Wear_And_Tear damage the trap deals to ITSELF for this contact
        public bool BreakLegs;   // non-explosive `Broken` trap (Snare) also breaks the victim's legs
    }

    // The engine-free trigger rule for a trap barricade -- a 1:1 port of the gate ladder + branch selection in
    // src InteractableTrap.NotifyTrapEntered (U3-SDK Assets/Runtime/Assembly-CSharp/Unturned/Interactable/
    // InteractableTrap.cs:117-267). Kept out of the Godot node so the ordering (which is subtle: the cooldown
    // latches BEFORE the PvP decision, so a non-PvP player contact still eats the cooldown) is unit-testable
    // with no scene. The caller reduces the live collider/asset to these booleans.
    public static class TrapRule
    {
        // src BarricadeManager.damage(transform, 5.0f/10.0f, ...) with EDamageOrigin.Trap_Wear_And_Tear
        public const float WearNormal = 5f;
        public const float WearHyperZombie = 10f;
        // src: `explosionLaunchSpeed > 0.01f` -- a launcher trap fires even at a non-PvP player (it only shoves them)
        public const float LaunchSpeedEpsilon = 0.01f;

        public static TrapDecision Evaluate(
            TrapTarget target,
            bool otherIsTrigger, bool isSelfOrChild,
            float now, float lastActive, float setupDelay,
            float lastTriggered, float cooldown,
            bool requiresPower, bool isWired,
            bool isExplosive, bool isBroken, float explosionLaunchSpeed,
            bool isPvP, bool targetRidingVehicle, bool zombieIsHyper)
        {
            var d = new TrapDecision { Action = TrapAction.None, Consumed = false, SelfWear = 0f, BreakLegs = false };

            // --- the gate ladder, in src order (each returns before latching the cooldown) ---
            if (otherIsTrigger) return d;                                  // src: other.isTrigger
            if (now - lastActive < setupDelay) return d;                   // src: setup delay since the trap went live
            if (requiresPower && !isWired) return d;                       // src: an unpowered powered-trap is inert
            if (isSelfOrChild) return d;                                   // src: other.transform.IsChildOf(transform)
            if (now - lastTriggered < cooldown) return d;                  // src: per-trap re-arm interval

            // src latches lastTriggered HERE -- before any PvP/target decision. So a contact that ends up doing
            // NOTHING (non-PvP player on a non-launcher trap) still consumes the cooldown window.
            d.Consumed = true;

            if (isExplosive)
            {
                // src: a player only sets it off if PvP is on and they aren't riding a vehicle -- UNLESS the trap
                // launches, which fires for anyone. Everything else (zombie/animal/vehicle/prop) always detonates.
                bool shouldExplode = target == TrapTarget.Player
                    ? ((isPvP && !targetRidingVehicle) || explosionLaunchSpeed > LaunchSpeedEpsilon)
                    : true;

                if (shouldExplode)
                {
                    d.Action = TrapAction.Explode;
                    // src damages the barricade FIRST so an explosive trap dies even when the server's barricade
                    // armor multiplier zeroes the blast's self-damage (Nelson 2025-08-25, public issue #5188).
                    d.SelfWear = WearNormal;
                }
                return d;
            }

            switch (target)
            {
                case TrapTarget.Player:
                    if (isPvP && !targetRidingVehicle)
                    {
                        d.Action = TrapAction.ShredPlayer;      // src EDeathCause.SHRED, ELimb.SPINE
                        d.BreakLegs = isBroken;                 // src: `Broken` trap -> life.breakLegs()
                        d.SelfWear = WearNormal;
                    }
                    return d;

                case TrapTarget.Zombie:
                    d.Action = TrapAction.DamageZombie;
                    d.SelfWear = zombieIsHyper ? WearHyperZombie : WearNormal;   // src: hyper zombies chew the trap twice as fast
                    return d;

                case TrapTarget.Animal:
                    d.Action = TrapAction.DamageAnimal;
                    d.SelfWear = WearNormal;
                    return d;

                default:
                    return d;   // src: the Player/Agent/Animal lookups all miss -> nothing happens (but the cooldown is spent)
            }
        }
    }
}
