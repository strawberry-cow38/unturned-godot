using Godot;

namespace UnturnedGodot
{
    // The heavy-lift electromagnet a sky-crane dangles on its winch cable (strawberry 2026-08-17: "big stinking circle
    // electromagnet. dangles below the heli when in flight. shift to magnetize"). Deliberately NOT the earlier
    // container-under-the-belly approach: nothing has to fit in the leg bay if the load hangs on a cable, so the
    // 0.66 m gear clearance that killed that idea stops mattering.
    //
    // It is its own RigidBody so it SWINGS -- the cable pull lives in Vehicle.UpdateSling as a pull-only damped spring
    // (the tow rope's model), which means the pendulum is emergent rather than animated: yaw hard and the load lags,
    // stop and it keeps going. Grabs are a locked 6DOF joint rather than reparenting, so the load keeps its own mass
    // and inertia and the aircraft genuinely feels it.
    public partial class SlingMagnet : RigidBody3D
    {
        public const float Radius = 1.35f;        // "big stinking circle" -- wide enough to read from the cockpit
        const float Thickness = 0.45f;
        public const float MagnetMass = 260f;     // heavy enough to hang the cable taut, light enough not to fly the aircraft

        // A magnetised coil only bites FERROUS things. Everything grabbable here is a physics body on the vehicle or
        // prop layers; the reach is measured from the coil FACE (the underside), not the centre, so a load is caught by
        // touching the magnet rather than by intersecting it.
        public const float GrabReach = 1.10f;

        public bool Magnetized { get; private set; }
        public RigidBody3D Held { get; private set; }

        Generic6DofJoint3D _weld;
        MeshInstance3D _coil;
        OmniLight3D _glow;
        StandardMaterial3D _coilMat;

        static readonly Color CoilOff = new Color(0.20f, 0.21f, 0.24f);   // dead iron
        static readonly Color CoilOn = new Color(1.00f, 0.42f, 0.10f);    // energised

        public override void _Ready()
        {
            Mass = MagnetMass;
            CollisionLayer = 1u << 5;              // vehicles: it is aircraft equipment, not scenery
            CollisionMask = (1u << 0) | (1u << 6); // world + props, so it lands on terrain and can bump what it lifts
            ContinuousCd = true;                   // it swings fast on a long cable; don't tunnel through the ground
            LinearDamp = 0.6f; AngularDamp = 2.0f; // a dead weight on a wire, not a pendulum that rings forever

            AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = Radius, Height = Thickness } });

            _coilMat = new StandardMaterial3D { AlbedoColor = CoilOff, Metallic = 0.85f, Roughness = 0.42f };
            _coil = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Radius, BottomRadius = Radius, Height = Thickness }, MaterialOverride = _coilMat };
            AddChild(_coil);
            // A narrower cap on top so the silhouette reads as a winch head rather than a floating coin.
            AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = Radius * 0.30f, BottomRadius = Radius * 0.62f, Height = Thickness * 1.5f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.17f, 0.19f), Metallic = 0.7f, Roughness = 0.55f },
                Position = new Vector3(0f, Thickness * 0.9f, 0f),
            });
            _glow = new OmniLight3D { LightColor = CoilOn, LightEnergy = 0f, OmniRange = Radius * 5f, Position = new Vector3(0f, -Thickness, 0f) };
            AddChild(_glow);
        }

        // Shift toggles the coil. De-energising is also how you PUT SOMETHING DOWN, so it always releases.
        public void SetMagnetized(bool on)
        {
            if (Magnetized == on) return;
            Magnetized = on;
            if (_coilMat != null)
            {
                _coilMat.AlbedoColor = on ? CoilOn : CoilOff;
                _coilMat.EmissionEnabled = on;
                _coilMat.Emission = CoilOn;
                _coilMat.EmissionEnergyMultiplier = on ? 1.6f : 0f;
            }
            if (_glow != null) _glow.LightEnergy = on ? 2.2f : 0f;
            if (!on) Release();
        }

        // The coil FACE, where a load actually makes contact -- the underside of the disc in world space.
        public Vector3 FaceWorld => GlobalPosition + GlobalBasis.Y * -(Thickness * 0.5f);

        // Latch a body onto the coil with a fully locked 6DOF joint (a weld). Cheaper and far more stable than
        // reparenting: the load keeps its own mass, so the aircraft feels the weight through the cable spring.
        public bool Grab(RigidBody3D body)
        {
            if (!Magnetized || Held != null || body == null || body == this || !IsInstanceValid(body)) return false;
            if (body.Freeze) body.Freeze = false;   // a parked/settled body must go dynamic or the weld drags a statue
            body.Sleeping = false;

            // SNAP IT TO THE FACE FIRST. The weld locks whatever separation exists at the instant it is created, and
            // the coil bites from up to GrabReach away -- so welding in place leaves the load hanging in mid-air below
            // the magnet with a visible gap, which reads as telekinesis rather than magnetism. Lift the body until its
            // top touches the coil face, then weld, so contact is what the joint preserves.
            var ab = BodyAabb(body);
            if (ab.HasValue)
            {
                float gap = FaceWorld.Y - ab.Value.End.Y;
                if (gap > 0f) body.GlobalPosition += new Vector3(0f, gap, 0f);
                body.LinearVelocity = LinearVelocity;   // and match speeds, or the weld starts by absorbing a relative slam
                body.AngularVelocity = Vector3.Zero;
            }

            var j = new Generic6DofJoint3D { Name = "MagnetWeld" };
            AddChild(j);
            j.GlobalTransform = new Transform3D(GlobalBasis, FaceWorld);
            j.NodeA = GetPath();
            j.NodeB = body.GetPath();
            // upper == lower == 0 on every axis: the load is rigidly stuck to the face, no slop, no spin.
            j.SetParamX(Generic6DofJoint3D.Param.LinearLowerLimit, 0f);
            j.SetParamX(Generic6DofJoint3D.Param.LinearUpperLimit, 0f);
            j.SetParamY(Generic6DofJoint3D.Param.LinearLowerLimit, 0f);
            j.SetParamY(Generic6DofJoint3D.Param.LinearUpperLimit, 0f);
            j.SetParamZ(Generic6DofJoint3D.Param.LinearLowerLimit, 0f);
            j.SetParamZ(Generic6DofJoint3D.Param.LinearUpperLimit, 0f);
            j.SetFlagX(Generic6DofJoint3D.Flag.EnableLinearLimit, true);
            j.SetFlagY(Generic6DofJoint3D.Flag.EnableLinearLimit, true);
            j.SetFlagZ(Generic6DofJoint3D.Flag.EnableLinearLimit, true);
            j.SetFlagX(Generic6DofJoint3D.Flag.EnableAngularLimit, true);
            j.SetFlagY(Generic6DofJoint3D.Flag.EnableAngularLimit, true);
            j.SetFlagZ(Generic6DofJoint3D.Flag.EnableAngularLimit, true);
            _weld = j; Held = body;
            AddCollisionExceptionWith(body);
            return true;
        }

        // World-space visual bounds of a body, used to seat a load against the coil face.
        static Aabb? BodyAabb(Node n)
        {
            Aabb? acc = null;
            void Walk(Node k)
            {
                if (k is VisualInstance3D vi && vi.Visible)
                {
                    var a = vi.GlobalTransform * vi.GetAabb();
                    acc = acc.HasValue ? acc.Value.Merge(a) : a;
                }
                foreach (var c in k.GetChildren()) Walk(c);
            }
            Walk(n);
            return acc;
        }

        public void Release()
        {
            if (_weld != null && IsInstanceValid(_weld)) _weld.QueueFree();
            _weld = null;
            if (Held != null && IsInstanceValid(Held))
            {
                RemoveCollisionExceptionWith(Held);
                Held.Sleeping = false;
            }
            Held = null;
        }

        // Sweep for something to bite. Called from the carrier's tick while energised and empty-handed: a short sphere
        // cast off the coil face, nearest first, so brushing the magnet over a car picks THAT car and not a fence 3 m on.
        public RigidBody3D FindGrabTarget(Godot.Collections.Array<Rid> ignore)
        {
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return null;
            var shape = new SphereShape3D { Radius = GrabReach };
            var q = new PhysicsShapeQueryParameters3D
            {
                ShapeRid = shape.GetRid(),
                Transform = new Transform3D(Basis.Identity, FaceWorld),
                CollisionMask = (1u << 5) | (1u << 6),   // vehicles + props: the things worth airlifting
                CollideWithBodies = true,
                Exclude = ignore,
            };
            RigidBody3D best = null; float bestD = float.MaxValue;
            foreach (var hit in space.IntersectShape(q, 16))
            {
                if (hit["collider"].Obj is not RigidBody3D rb || rb == this) continue;
                float d = rb.GlobalPosition.DistanceTo(FaceWorld);
                if (d < bestD) { bestD = d; best = rb; }
            }
            return best;
        }
    }
}
