using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The two-post car lift (Car_Lift_0), with the platform it has been missing and a power input
    /// (master 2026-09-10: "on the main menu it has its ramp, but in game it doesnt. give it a power io input").
    ///
    /// WHY THE RAMP WAS MISSING, since it explains a whole class of absent geometry: the retail prefab is
    ///     Object [MeshCollider, LODGroup, AudioSource]
    ///       Root [Animation]
    ///         Hinge_0  [SkinnedMeshRenderer]   <- the platform
    ///         Model_0  [MeshFilter, MeshRenderer]   <- the frame, and the only thing we extracted
    ///         Skeleton/Hinge [BoxCollider x2]
    ///         Model_1  [MeshFilter, MeshRenderer]   <- LOD1
    /// extract_objects_v2 walks the LODGroup for MeshFilter/MeshRenderer LOD0, and a SkinnedMeshRenderer has
    /// no MeshFilter at all -- so the platform was never in the object rip. The main menu diorama comes
    /// through a different path (Unity Mesh .asset -> OBJ), which is why it has a ramp the world does not.
    /// tools/extract_doors.py already discovers skinned leaves generically, so it produced the mesh.
    ///
    /// AND IT IS NOT A DOOR, which the extractor found out the hard way: it crashed deriving a hinge axis,
    /// because the Hinge bone's ROTATION curve is two identical keys and does not rotate at all. What moves
    /// is its POSITION -- 60 keys along the bone's local Z, and the prop OBJ convention is Z-up, so the
    /// platform simply rises. Straight off the clips:
    ///     travel 1.15 m, duration 1.967 s, `Open` lowers and `Close` raises.
    /// A lift, not a hinge. Anything that tried to swing this would have been wrong in a way no build catches.</summary>
    public partial class CarLift : StaticBody3D, IPowerDevice
    {
        /// <summary>Source: the Skeleton/Hinge position curve runs 1.15 m end to end.</summary>
        public const float TravelMetres = 1.15f;
        /// <summary>Source: 60 keys at a 30 Hz sample rate.</summary>
        public const float TravelSeconds = 1.967f;
        /// <summary>A hoist under load. Between the purifier's 750 W and nothing at all.</summary>
        public const float LiftWatts = 1200f;

        public const string HitMeta = "carlift";

        public uint NetId;             // MP replica id (0 = SP/local)
        public bool DebugForcePower;   // headless tests: pretend it is wired + powered

        readonly List<ConnectionPort> _powerPorts = new();
        ConnectionPort _powerInput;
        StaticBody3D _ramp;
        float _height;                 // metres above the down position
        bool _rising;                  // which way it is travelling when moving

        public bool Raised => _height >= TravelMetres - 0.001f;
        public bool Moving { get; private set; }
        public float Height => _height;
        public bool IsPowered => DebugForcePower
                              || (_powerInput != null && GodotObject.IsInstanceValid(_powerInput) && _powerInput.Powered);

        /// <summary>Build the platform + the power port. The frame itself is the ordinary world prop mesh --
        /// this node adds only what the rip left out and what master asked for.</summary>
        public static CarLift Spawn(Node parent, Vector3 pos, Basis basis, Mesh rampMesh, Material mat)
        {
            var l = new CarLift { Transform = new Transform3D(basis, pos) };
            parent.AddChild(l);
            if (rampMesh != null)
            {
                // The platform is its own BODY, not a child mesh of the lift node: it has to carry a collider
                // that MOVES with it, and a StaticBody3D whose transform we drive is the only shape here that
                // both holds a player up and rises out from under nothing. A lift you cannot stand on is a
                // decoration.
                l._ramp = new StaticBody3D { CollisionLayer = 1u << 0 };
                l.AddChild(l._ramp);
                var mi = new MeshInstance3D { Mesh = rampMesh };
                if (mat != null) mi.MaterialOverride = mat;
                l._ramp.AddChild(mi);
                var ab = rampMesh.GetAabb();
                l._ramp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = ab.Size }, Position = ab.GetCenter() });
                l._ramp.SetMeta(HitMeta, l);   // the look ray hits the PLATFORM -- that is what you aim at
            }
            l.SetMeta(HitMeta, l);
            return l;
        }

        public override void _Ready()
        {
            _powerInput = ConnectionPort.Create(this, new DeployableDef.Port
            {
                Kind = DeployableDef.PortKind.Consumer,
                Pos = new Vector3(0f, 1.0f, -1.9f),   // on the frame's near post, at hand height
                Watts = LiftWatts,
            }, "Car Lift");
            _powerPorts.Add(_powerInput);
            AddChild(_powerInput);
            AddToGroup("deployables");   // PowerNet reads this group (keyed on IPowerDevice)
            SetProcess(true);
        }

        /// <summary>F toggles it, and ONLY with power: a two-post lift is a hydraulic ram, not something you
        /// shove by hand. An unpowered lift says so rather than silently ignoring the key.</summary>
        public bool Toggle()
        {
            if (!IsPowered) return false;
            if (Moving) return false;      // let a cycle finish; a lift that reverses mid-travel is a trapped car
            _rising = !Raised;
            Moving = true;
            return true;
        }

        public override void _Process(double delta)
        {
            if (!Moving) return;
            // A hydraulic lift travels at a steady rate -- the source curve eases slightly at both ends, which
            // is worth having but is not what makes this read correctly, so the constant rate is used and the
            // easing is the flagged approximation rather than a silently invented curve.
            float step = TravelMetres / TravelSeconds * (float)delta;
            _height = Mathf.Clamp(_height + (_rising ? step : -step), 0f, TravelMetres);
            if (_ramp != null) _ramp.Position = new Vector3(0f, _height, 0f);
            if ((_rising && _height >= TravelMetres) || (!_rising && _height <= 0f)) Moving = false;
        }

        // picked up / removed -> free any wire plugged into the power input, then re-solve
        public void ReleaseWires()
        {
            foreach (var n in GetTree().GetNodesInGroup("wires"))
                if (n is Wire w && GodotObject.IsInstanceValid(w) && (w.Source == _powerInput || w.Consumer == _powerInput))
                { w.RemoveFromGroup("wires"); w.QueueFree(); }
            PowerNet.MarkDirty();
        }

        // IPowerDevice -- a pure consumer, like the purifier and the pump jack
        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => NetId;
        public IReadOnlyList<ConnectionPort> PowerPorts => _powerPorts;
    }
}
