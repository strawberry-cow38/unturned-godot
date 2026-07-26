using System;

namespace SDG.Unturned
{
    /// <summary>Why a door refused to move. The caller turns this into a hint/prompt; keeping it a reason
    /// rather than a bare bool is what lets the UI say "locked" instead of nothing happening.</summary>
    public enum DoorRefusal : byte
    {
        None = 0,
        Cooldown,     // toggled a moment ago -- the leaf is still swinging
        Locked,       // owned by someone else and locked
        Obstructed,   // something is standing in the arc
    }

    /// <summary>
    /// The decision half of a door, with no engine in it: may this player toggle this door right now?
    ///
    /// Doors in the source are barricades carrying an owner and a group, a locked flag, and a short
    /// re-toggle cooldown; the server additionally refuses to swing a leaf through anything standing in
    /// its arc. All of that is a pure predicate over state, so it lives here and is L0-tested, while the
    /// Godot side owns hinges, collision and audio.
    /// </summary>
    public static class DoorLogic
    {
        /// <summary>Seconds a door must settle before it can be toggled again. Matches the source's
        /// re-openable window, and stops a held key from strobing the leaf.</summary>
        public const float ToggleCooldown = 0.75f;

        /// <summary>How far a door's swing is heard. The source alerts at radius 8 on every toggle, which
        /// is why opening a door in a POI pulls zombies onto you.</summary>
        public const float ToggleLoudness = 8f;

        public struct DoorState
        {
            public ulong Owner;        // 0 = unowned
            public ulong Group;        // 0 = no group
            public bool Locked;
            public bool IsOpen;
            public double LastToggled; // sim seconds
        }

        /// <summary>Ownership test, split out because locking, salvaging and re-keying all share it.
        /// An unowned door answers to anyone -- the source treats CSteamID.Nil as "no claim".</summary>
        public static bool HasAccess(in DoorState door, ulong player, ulong group)
        {
            if (!door.Locked) return true;
            if (door.Owner == 0UL) return true;
            if (player == door.Owner) return true;
            return door.Group != 0UL && group == door.Group;
        }

        /// <summary>May this player toggle the door at <paramref name="now"/>?
        /// <paramref name="arcBlocked"/> is the caller's overlap test -- the sim cannot see the world, so
        /// the engine answers "is something standing in the swing" and the rule lives here.</summary>
        public static bool CanToggle(in DoorState door, ulong player, ulong group, double now,
                                     bool arcBlocked, out DoorRefusal why)
        {
            if (now - door.LastToggled < ToggleCooldown) { why = DoorRefusal.Cooldown; return false; }
            if (!HasAccess(door, player, group)) { why = DoorRefusal.Locked; return false; }
            // Only closing can trap someone; an open door swinging shut is the case that needs the check.
            if (arcBlocked) { why = DoorRefusal.Obstructed; return false; }
            why = DoorRefusal.None;
            return true;
        }

        /// <summary>Apply a toggle. Returns the new state; the caller is expected to have asked
        /// <see cref="CanToggle"/> first, and this does not re-check so that a server applying a
        /// validated command cannot disagree with the validation.</summary>
        public static DoorState Toggle(DoorState door, double now)
        {
            door.IsOpen = !door.IsOpen;
            door.LastToggled = now;
            return door;
        }

        /// <summary>Lock or unlock. Only the owner may; an unowned door cannot be locked, because a lock
        /// with no key holder would be a door nobody can ever open.</summary>
        public static bool TrySetLocked(ref DoorState door, ulong player, bool locked)
        {
            if (door.Owner == 0UL || player != door.Owner) return false;
            door.Locked = locked;
            return true;
        }
    }
}
