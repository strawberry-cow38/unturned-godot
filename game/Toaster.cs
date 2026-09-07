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

        // ---- THE LEVER (strawberry 2026-09-07: "make the toaster's lever move up when looking in the container,
        // and down when not. with a smooth glide down and a spring up").
        //
        // Toaster_0.obj is TWO connected components and the second one is the lever: a plain 8-corner box on the
        // front face, x +-0.2012, y 0.3827..0.5359, z 0.1027..0.2274 -- measured, and it is the only geometry that
        // protrudes past the body's front wall at y 0.474. The box below is that measurement plus 5 mm of slack.
        //
        // AS MODELLED IS THE DOWN POSE and the lever only ever travels UP from there. That is a deliberate choice
        // of direction rather than a coin flip: the box sits in the bottom third of a 0.625-tall body, so sinking
        // it a lever-height would put its underside at z -0.022, through the counter the toaster stands on. Going
        // up has 0.27 of clear body above it and cannot clip anything.
        //
        // The LOD is a bare 12-face box with no lever in it at all, so splitting this out cannot leave a second
        // copy drawing at range -- the trap that made an open door draw twice on Barbecue_1. It does mean the
        // lever simply is not there past the LOD switch, which is already true today and invisible on a SMALL prop.
        static bool InLeverBox(Vector3 v) =>
            Mathf.Abs(v.X) <= 0.2062f && v.Y >= 0.3777f && v.Y <= 0.5409f && v.Z >= 0.0977f && v.Z <= 0.2324f;

        const float LeverTravel = 0.1247f;   // the lever's OWN height (0.2274 - 0.1027): a full body-height of throw, taken from the mesh rather than typed

        // Two parameter sets for ONE integrator, because "glide down, spring up" is the same equation twice.
        // a = k*(target - x) - c*v, and the damping ratio is the whole difference: DOWN is critically damped so
        // it eases to a stop and never passes its mark, UP is deliberately under-damped so it pops past and settles.
        const float DownK = 90f,  DownC = 18.97f;   // zeta = 1.00 -> no overshoot, ~0.3 s
        const float UpK   = 420f, UpC   = 14.3f;    // zeta = 0.35 -> overshoots, snaps

        MeshInstance3D _lever;      // the split-out box, a child of the body instance so it rides the upright basis
        float _leverX, _leverV;     // position along the mesh's own +Z (0 = down, LeverTravel = up) and its velocity
        bool _leverUp;

        /// <summary>Split the lever off `bodyMi`'s mesh, leave the body on it, and return the lever instance --
        /// or null if this mesh has no lever, in which case the body is handed back untouched and the toaster
        /// simply does not animate. Degrading to "no animation" matters more than it sounds: a re-rip that welds
        /// the two components would otherwise take the lever's ten triangles OUT of the body and put them
        /// nowhere.</summary>
        static MeshInstance3D SplitLever(MeshInstance3D bodyMi)
        {
            if (bodyMi?.Mesh is not ArrayMesh am) return null;
            var (body, lever) = ObjMesh.SplitByFaceVerts(am, (a, b, c) => InLeverBox(a) && InLeverBox(b) && InLeverBox(c));
            if (lever == null || body == null) return null;
            // GUARD WITH TEETH, not a null check: the predicate is a hardcoded box, so the way this fails on a
            // changed asset is by matching SOME of the toaster rather than none. The lever is 10 triangles of 58
            // and its own bounds are 0.40 x 0.15 x 0.125 -- anything materially bigger is the body being carved up,
            // and handing that back would delete a chunk of the toaster while looking like a successful split.
            var ab = lever.GetAabb();
            if (ab.Size.X > 0.55f || ab.Size.Y > 0.30f || ab.Size.Z > 0.25f) return null;
            bodyMi.Mesh = body;
            var mi = new MeshInstance3D { Mesh = lever, MaterialOverride = bodyMi.MaterialOverride };
            bodyMi.AddChild(mi);   // child of the BODY instance: inherits its upright basis, so local +Z is world up
            return mi;
        }

        /// <summary>Container open = lever up (popped), closed = down. Idempotent -- the open/close path fires this
        /// on every state change and the replica view re-asserts it on a joiner's first pass.</summary>
        public void SetLeverUp(bool up) => _leverUp = up;

        public override void _PhysicsProcess(double delta)
        {
            if (_lever == null) return;
            float target = _leverUp ? LeverTravel : 0f;
            if (Mathf.IsEqualApprox(_leverX, target) && Mathf.Abs(_leverV) < 1e-4f) return;
            // Clamped so a hitch cannot integrate the stiff UP spring into a divergence -- k*dt^2 > 4 is unstable
            // for this form, which at k=420 is dt > 0.098.
            float dt = Mathf.Min((float)delta, 0.033f);
            float k = _leverUp ? UpK : DownK, c = _leverUp ? UpC : DownC;
            _leverV += (k * (target - _leverX) - c * _leverV) * dt;
            _leverX += _leverV * dt;
            if (Mathf.Abs(target - _leverX) < 0.0004f && Mathf.Abs(_leverV) < 0.01f) { _leverX = target; _leverV = 0f; }
            _lever.Position = new Vector3(0f, 0f, _leverX);
        }

        public float DebugLeverOffset => _leverX;
        public bool DebugLeverUp => _leverUp;
        public bool DebugHasLever => _lever != null;
        public const float DebugLeverTravel = LeverTravel;
        /// <summary>L1: step the lever without the engine's own callback, the same hand-tick the door leaves use.</summary>
        public void TickLeverForTest(double delta) => _PhysicsProcess(delta);

        public static Toaster Make(MeshInstance3D bodyMi)
        {
            var t = new Toaster { Transform = bodyMi.Transform };
            t._lever = SplitLever(bodyMi);
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
