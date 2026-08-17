using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A shipping container that is a PHYSICS BODY rather than scenery, with its retail openable doors and a fixed
    // point for the sky-crane's electromagnet to grab (strawberry 2026-08-17: "clone of the prop (openable doors),
    // now with physics! has a fixed magnet point in the center").
    //
    // Why not just add a RigidBody to the existing doored prop: ObjectDoor is a StaticBody3D, and a static body is
    // the one thing that must not move -- parented under a RigidBody it would follow visually while its collision
    // stayed wrong. So the leaves here are pivoted MESHES with no collision of their own, and the container's single
    // box hull does all the colliding. The swing itself is the same catalog data ObjectDoor uses (doors.txt: hinge
    // pivot, axis, angle, duration), read through the same loader, so the doors open the way the world's do.
    //
    // Frame: the OBJ is authored Z-up, so `_prop` carries SinkSource.UprightPlacement and everything under it --
    // body mesh, hinge pivots, leaf meshes -- stays in raw MESH coordinates. That is exactly the convention
    // ObjectDoor.Spawn already uses (prop transform on the node, pivot/axis untouched from the catalog), and
    // matching it means the catalog numbers need no conversion and cannot drift out of step with the world's doors.
    public partial class MagnetableContainer : RigidBody3D, SlingMagnet.IMagnetAttachPoint
    {
        public const string PropName = "Container_0";
        // SIZED AGAINST THE CRANE THAT LIFTS IT. The buffed sky-crane's spare thrust is (16.5-9.8)*900 = 6030 N,
        // about 615 kg, minus the 12 kg magnet. An 800 kg container (my first number, picked for "heavy freight")
        // is simply unliftable by the only aircraft with a magnet, which would have made the whole object a prop
        // that mocks you. 450 kg leaves real climb margin while still feeling like freight.
        public const float ContainerMass = 450f;

        Node3D _prop;
        readonly List<(Node3D pivot, Vector3 axis, float angleDeg)> _leaves = new();
        float _swing, _swingTarget;   // 0 = shut, 1 = open
        float _swingRate = 1f / 0.4667f;
        Aabb _localBounds;            // node-space bounds of the body mesh, used for the hull and the magnet point

        /// <summary>Where the electromagnet latches: the CENTRE of the container's top face. Fixed, so a grab always
        /// hangs it level and centred instead of wherever the coil happened to brush it.</summary>
        public Vector3 MagnetPointWorld => ToGlobal(new Vector3(0f, _localBounds.End.Y, 0f));

        public bool DoorsOpen => _swingTarget > 0.5f;

        public static MagnetableContainer Spawn(Node parent, Vector3 at)
        {
            var c = new MagnetableContainer { Name = "MagnetableContainer" };
            parent.AddChild(c);
            c.GlobalPosition = at;
            return c;
        }

        public override void _Ready()
        {
            Mass = ContainerMass;
            // PROPS layer, so the magnet's grab sweep (which masks vehicles + props) can find it and the terrain
            // holds it up. Matches the loose-prop convention rather than inventing a layer for one object.
            CollisionLayer = 1u << 6;
            CollisionMask = (1u << 0) | (1u << 5) | (1u << 6);
            ContinuousCd = true;   // dropped from a crane at height; do not let it tunnel the ground

            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            _prop = new Node3D { Basis = SinkSource.UprightPlacement };
            AddChild(_prop);

            var body = ObjMesh.Load(dir + PropName + ".obj");
            var mat = new StandardMaterial3D { Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var img = new Image();
            if (img.Load(dir + PropName + "_tex.png") == Error.Ok) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }
            _prop.AddChild(new MeshInstance3D { Mesh = body, MaterialOverride = mat });

            // Hull from the mesh's own bounds, transformed into node space. Deriving it rather than typing numbers
            // keeps it honest if the prop is ever swapped, and the same bounds define the magnet point.
            _localBounds = new Transform3D(SinkSource.UprightPlacement, Vector3.Zero) * body.GetAabb();
            var hull = new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = _localBounds.Size },
                Position = _localBounds.Position + _localBounds.Size * 0.5f,
            };
            AddChild(hull);

            // The retail leaves, straight out of doors.txt -- the SAME catalog the world's containers read.
            var cat = WorldBuilder.LoadDoorCatalog(dir);
            if (cat != null && cat.TryGetValue(PropName, out var entries))
                foreach (var e in entries)
                {
                    var leaf = ObjMesh.Load(dir + e.MeshFile);
                    if (leaf == null) continue;
                    var pivot = new Node3D { Position = e.Pivot };
                    _prop.AddChild(pivot);
                    // Offset the leaf by -pivot so an identity pivot basis reproduces its extracted rest pose exactly
                    // and rotating the pivot swings it about the hinge (ObjectDoor's trick, same reasoning).
                    pivot.AddChild(new MeshInstance3D { Mesh = leaf, MaterialOverride = mat, Position = -e.Pivot });
                    _leaves.Add((pivot, e.Axis.LengthSquared() > 1e-6f ? e.Axis.Normalized() : Vector3.Back, e.AngleDeg));
                    _swingRate = 1f / Mathf.Max(0.05f, e.DurationSec);
                }
            ApplySwing();
            // Say how many leaves were actually found. A container that silently loaded ZERO doors still renders as a
            // perfectly good container, so "it looks right" cannot distinguish working doors from no doors at all.
            GD.Print($"[MAGCONTAINER] {PropName}: {_leaves.Count} door leaf/leaves, mass {Mass:0} kg, bounds {_localBounds.Size}, magnet point local (0, {_localBounds.End.Y:0.00}, 0)");
            SetPhysicsProcess(_leaves.Count > 0);
        }

        public void ToggleDoors() => SetDoorsOpen(!DoorsOpen);

        public void SetDoorsOpen(bool open)
        {
            _swingTarget = open ? 1f : 0f;
            SetPhysicsProcess(_leaves.Count > 0);   // wake the swing; _PhysicsProcess turns itself back off at rest
        }

        public override void _PhysicsProcess(double delta)
        {
            if (Mathf.IsEqualApprox(_swing, _swingTarget)) { SetPhysicsProcess(false); return; }
            _swing = Mathf.MoveToward(_swing, _swingTarget, _swingRate * (float)delta);
            ApplySwing();
        }

        void ApplySwing()
        {
            // Smoothstep rather than the catalog's sampled retail curve: those clips are loaded by ObjectDoor for
            // world props and are not worth threading through a spawned physics object. Doors that ease are the
            // point; matching the retail overshoot frame-for-frame is not.
            float t = _swing * _swing * (3f - 2f * _swing);
            foreach (var (pivot, axis, angleDeg) in _leaves)
                pivot.Basis = new Basis(axis, Mathf.DegToRad(angleDeg) * t);
        }
    }
}
