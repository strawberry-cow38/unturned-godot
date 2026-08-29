using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>Translucent preview of what a build tool is about to place, red when it would land inside
    /// something. strawberry_cow: "prevent placing overlapping stuff, showing the ghost as red. give
    /// everything that doesnt have a ghost a ghost."
    ///
    /// ONE OF THESE, SHARED BY EVERY TOOL. The stairs tool grew its own version of this first, and the
    /// obvious way to satisfy "give everything a ghost" was to grow four more -- at which point the clash
    /// rule, the tint, the depth-test flag and the pooling are written five times and drift apart one edit
    /// at a time. That failure mode is well attested in this file: a gable gate written three times, five
    /// hand-written tool-clearing branches, three copies of WallPlan construction. Stairs now feed this
    /// like everything else.
    ///
    /// Boxes are POOLED. A tool re-feeds the whole set every mouse-move, and allocating a MeshInstance3D
    /// per frame at 60 Hz is the "million rebuilds each frame" strawberry_cow warned about.</summary>
    public sealed partial class PlacementGhost : Node3D
    {
        public readonly struct Box
        {
            public readonly Vector3 Centre, Size;
            public readonly float Yaw;
            public Box(Vector3 centre, Vector3 size, float yaw = 0f) { Centre = centre; Size = size; Yaw = yaw; }

            /// <summary>The world-axis-aligned bounds of this (possibly yawed) box.
            ///
            /// Approximate ON PURPOSE, and generous rather than tight: the ghost is a WARNING, and paying a
            /// physics query per mouse-move to be exact about a corner nobody is looking at is the wrong
            /// trade. Rotating the half-extents is still strictly better than the axis-aligned guess the
            /// stair ghost used, which under-reported every diagonal flight.</summary>
            public Aabb Bounds()
            {
                float c = Mathf.Abs(Mathf.Cos(Mathf.DegToRad(Yaw)));
                float s = Mathf.Abs(Mathf.Sin(Mathf.DegToRad(Yaw)));
                var half = new Vector3((Size.X * c + Size.Z * s) * 0.5f,
                                       Size.Y * 0.5f,
                                       (Size.X * s + Size.Z * c) * 0.5f);
                return new Aabb(Centre - half, half * 2f);
            }
        }

        static readonly Color Clear = new(0.35f, 0.9f, 1f, 0.4f);
        static readonly Color Clash = new(1f, 0.25f, 0.2f, 0.55f);

        readonly List<MeshInstance3D> _pool = new();

        /// <summary>How many boxes are actually on screen. Not _pool.Count: the pool is retained across
        /// frames and its tail is hidden rather than freed, so the pool size is a high-water mark and
        /// asserting on it would pass whatever the tool is currently showing.</summary>
        public int VisibleCount
        {
            get { int n = 0; foreach (var t in _pool) if (t.Visible) n++; return n; }
        }

        /// <summary>The tint the boxes are actually wearing, for tests that need to distinguish "not red"
        /// from "not drawn" -- a hidden ghost reports no clash, which is the same answer a clear one gives.</summary>
        public Color Tint =>
            _pool.Count > 0 && _pool[0].MaterialOverride is StandardMaterial3D m ? m.AlbedoColor : default;

        /// <summary>True when the last Show() found a clash. Read by the readout so the warning is not only
        /// a colour -- a red translucent box over a red brick wall is not a signal.</summary>
        public bool Clashing { get; private set; }

        /// <summary>Draw these boxes, tinting them ALL red if ANY of them clashes.
        ///
        /// All-or-nothing on purpose: a staircase with one red tread and five blue ones reads as "mostly
        /// fine", when the truth is that the flight cannot go there. The unit the user is placing is the
        /// whole set, so the whole set is what answers.</summary>
        public void Show(IReadOnlyList<Box> boxes, System.Func<Aabb, bool> overlaps)
        {
            if (boxes == null || boxes.Count == 0) { HideAll(); return; }

            bool clash = false;
            if (overlaps != null)
                foreach (var b in boxes) if (overlaps(b.Bounds())) { clash = true; break; }
            Clashing = clash;
            var tint = clash ? Clash : Clear;

            for (int i = 0; i < boxes.Count; i++)
            {
                if (i >= _pool.Count)
                {
                    var mi = new MeshInstance3D
                    {
                        Mesh = new BoxMesh(),
                        MaterialOverride = new StandardMaterial3D
                        {
                            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                        },
                        // A ghost is a preview, not a prop: it must never cast a shadow, block a raycast or
                        // turn up in a render. It is parented under the editor tool, which is not saved.
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    };
                    AddChild(mi);
                    _pool.Add(mi);
                }
                var t = _pool[i];
                ((BoxMesh)t.Mesh).Size = new Vector3(Mathf.Max(0.02f, boxes[i].Size.X),
                                                     Mathf.Max(0.02f, boxes[i].Size.Y),
                                                     Mathf.Max(0.02f, boxes[i].Size.Z));
                t.GlobalPosition = boxes[i].Centre;
                t.GlobalRotationDegrees = new Vector3(0f, boxes[i].Yaw, 0f);
                t.Visible = true;
                if (t.MaterialOverride is StandardMaterial3D m) m.AlbedoColor = tint;
            }
            for (int i = boxes.Count; i < _pool.Count; i++) _pool[i].Visible = false;
            Visible = true;
        }

        /// <summary>Not named Hide(): that would SHADOW Node3D.Hide(), and a call through a base
        /// reference would then silently skip clearing the pool and the clash flag.</summary>
        public void HideAll()
        {
            Clashing = false;
            foreach (var t in _pool) t.Visible = false;
            Visible = false;
        }
    }
}
