using Godot;

namespace UnturnedGodot
{
    // Toaster_0 (strawberry: "make the toaster take 2 shots to break. the first shot has a chance to eject a piece of
    // bread or two out the top, launch with velocity").
    //
    // The two-shot part is a health override in DestructibleField -- retail ships the toaster at 25 hp, which is
    // exactly one Eaglefire Object_Damage, so it burst on the first bullet and there was no "first shot" for anything
    // to happen on. 50 hp buys the second.
    //
    // This node owns the bread. It hangs off the placed prop exactly like TVDevice does: a meta on the body collider
    // routes a bullet to it, and the destructible's onAlive hook resets it when the rubble respawns.
    public partial class Toaster : Node3D
    {
        /// <summary>Collider meta carrying the device, so a bullet landing on a Toaster_0 body can find it.</summary>
        public static readonly StringName HitMeta = "toaster";

        public const ushort BreadItemId = 460;   // "Bread" in items_catalog.tsv
        const float PopChance = 0.6f;            // per intact toaster, rolled once -- see _popped
        const float LaunchUp = 5.4f;             // m/s straight up: clears the counter and lands nearby, not across the room
        const float LaunchSpread = 1.3f;         // lateral scatter, so two slices do not fly as one

        bool _popped;      // ONE pop per intact toaster. Without this every hit re-rolls, and a toaster with 50 hp shot
                           //  by a low-damage weapon becomes a bread fountain -- the ask was "the first shot".
        bool _broken;
        Vector3 _slotLocal = new(0f, 0.35f, 0f);   // where the slices come out, set from the prop's own bounds

        public static Toaster Make(MeshInstance3D bodyMi)
        {
            var t = new Toaster { Transform = bodyMi.Transform };
            var aabb = bodyMi.Mesh?.GetAabb() ?? new Aabb();
            // The TOP of the prop in its own local frame. Measured rather than guessed: these props are authored Z-up
            // and the placement basis stands them upright, so the "top" is the max corner along the local axis that
            // ends up pointing at world up -- taking bodyMi's own basis rather than assuming Y.
            var localUp = bodyMi.Transform.Basis.Orthonormalized().Inverse() * Vector3.Up;
            if (localUp.LengthSquared() < 1e-6f) localUp = Vector3.Up;
            localUp = localUp.Normalized();
            float hi = float.MinValue;
            for (int i = 0; i < 8; i++) hi = Mathf.Max(hi, aabb.GetEndpoint(i).Dot(localUp));
            var c = aabb.GetCenter();
            t._slotLocal = c + localUp * (hi - c.Dot(localUp) + 0.04f);   // just clear of the slot, not inside it
            return t;
        }

        /// <summary>A bullet hit the toaster and it is still standing. Returns the slices to spawn (0, 1 or 2).
        ///
        /// Pure apart from the roll, and separated from the spawning so the POLICY is testable without a world: the
        /// interesting rules are "only while intact", "only once", and "never on the shot that kills it", none of which
        /// are observable from a screenshot of bread on the floor.</summary>
        internal static int SlicesFor(bool intact, bool alreadyPopped, float roll)
        {
            if (!intact || alreadyPopped) return 0;
            if (roll >= PopChance) return 0;
            // Re-use the same roll for the count rather than drawing a second: inside the pop band, the lower half
            // throws two. Keeps the whole outcome a function of ONE number, which is what makes it reproducible in a
            // test without threading an RNG through.
            return roll < PopChance * 0.5f ? 2 : 1;
        }

        /// <summary>Fire the pop if it is due. Called from the bullet path on a hit that the prop survives.</summary>
        public void OnShot()
        {
            int slices = SlicesFor(!_broken, _popped, GD.Randf());
            _popped = true;   // latched even on a failed roll: the ask was a chance on the FIRST shot, not a chance
                              //  on every shot until it happens.
            if (slices <= 0) return;

            var parent = GetParent() ?? this;
            for (int i = 0; i < slices; i++)
            {
                var item = SDG.Unturned.Assets.makeLoot(BreadItemId);
                if (item == null) return;
                var at = ToGlobal(_slotLocal) + new Vector3(GD.Randf() * 0.06f - 0.03f, 0.02f * i, GD.Randf() * 0.06f - 0.03f);
                var wi = WorldItem.Spawn(parent, item, at);
                if (wi == null) continue;
                // Launched, not dropped (strawberry: "launch with velocity"). WorldItem is a RigidBody3D, so this is
                // its own velocity rather than an animation -- it arcs, bounces and lands where physics puts it.
                wi.LinearVelocity = new Vector3(
                    (GD.Randf() * 2f - 1f) * LaunchSpread,
                    LaunchUp + GD.Randf() * 0.8f,
                    (GD.Randf() * 2f - 1f) * LaunchSpread);
            }
        }

        /// <summary>Rubble break/reset. A reset toaster is a NEW one, so it gets its bread back -- same reasoning as a
        /// reset television coming back whole and switched on.</summary>
        public void SetBroken(bool broken)
        {
            if (_broken == broken) return;
            _broken = broken;
            if (!broken) _popped = false;
        }

        public bool DebugPopped => _popped;
        public bool DebugBroken => _broken;
        public Vector3 DebugSlotWorld => ToGlobal(_slotLocal);
    }
}
