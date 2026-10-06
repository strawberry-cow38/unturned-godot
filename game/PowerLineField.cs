using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>The wires strung between power-line poles, and the record of which poles are joined.
    ///
    /// Master 2026-10-06: "a map editor tool for power line wire splines, theres 4 wire connection points on them
    /// (gray) should draw a line and the 4 wires should connect. sways very slightly in the wind."
    ///
    /// ⭐ THE FOUR ANCHORS ARE MEASURED OFF THE MESH, NOT EYEBALLED. `Power_Line_0_tex.png` is a 2x2 palette --
    /// four texels, two brown (#6A5848, #5A4B3E) and two grey (#969696, #7B7B7B) -- so "the grey parts" is a
    /// question with an exact answer: which faces sample a grey texel. Doing that over the 100 faces and
    /// clustering the result gives FOUR groups and nothing else, which is the confirmation that the clusters are
    /// the connection points rather than four things that happened to be grey:
    ///
    ///     ( 1.658, -0.331, 7.335)   ( 0.880, -0.331, 7.335)      <- upper crossarm, GREY_DARK
    ///     (-0.880, -0.331, 6.941)   (-1.658, -0.331, 6.941)      <- lower crossarm, GREY_LIGHT
    ///
    /// ⚠⚠ AND THEY ARE USED RAW, WHICH I NEARLY GOT BACKWARDS. [[reference_unturned_coord_znegate]] says map
    /// placements negate Z, and applying that here felt automatic -- but `ObjMesh.CONV` defaults to **1, raw Unity
    /// geometry**. The negate-Z layout is the OLD convention (CONV 0), abandoned because it reflected every mesh
    /// and made them chiral. Checking it instead of assuming: PEI places this prop at ex=270, and a rotation of
    /// 270 about X maps local (x,y,z) to (x,z,-y) -- so a raw anchor at z=7.335 lands 7.3 m up an 8 m pole, and a
    /// Z-negated one would have put all four wire points 7.3 m UNDERGROUND. The arithmetic says which rule applies;
    /// the remembered rule does not.
    ///
    /// Everything after that is in the mesh's own frame, which is the point: anchors transformed by the pole's own
    /// placement basis need no knowledge of the ex=270 stand-up or the 180-ey yaw.
    ///
    /// ⭐ ONE MESH, SWAY IN THE SHADER. A span is static geometry -- the sag is baked -- and the wind is a vertex
    /// displacement in powerline_wire.gdshader driven by the same `wind_vec` global the foliage and flags already
    /// read. So a map's whole grid is one draw call with no per-frame CPU at all, instead of N nodes each ticking
    /// themselves. See [[reference_unturned_flags]].</summary>
    public partial class PowerLineField : Node3D
    {
        /// <summary>The prop these anchors were measured from. Anything else has no wire points.</summary>
        public const string PoleMesh = "Power_Line_0";

        /// <summary>The four grey pads, in the loaded mesh's local frame (raw OBJ -- see the note above).
        /// Order is deliberate and load-bearing: a span joins anchor i to anchor i, so two poles facing OPPOSITE
        /// ways still pair outer-to-outer and inner-to-inner rather than crossing the wires over.</summary>
        public static readonly Vector3[] AnchorsLocal =
        {
            new Vector3( 1.658f, -0.331f, 7.335f),   // upper crossarm, outer
            new Vector3( 0.880f, -0.331f, 7.335f),   // upper crossarm, inner
            new Vector3(-0.880f, -0.331f, 6.941f),   // lower crossarm, inner
            new Vector3(-1.658f, -0.331f, 6.941f),   // lower crossarm, outer
        };

        /// <summary>A pole that can carry wires: its placement transform, and where it is for picking.</summary>
        public struct Pole
        {
            public Transform3D Xform;
            public Vector3 Origin => Xform.Origin;
        }

        /// <summary>Two pole indices. Stored as an unordered pair -- A always the lower index -- so connecting
        /// B to A cannot produce a second, duplicate span lying inside the first.</summary>
        public struct Span
        {
            public int A, B;
            public Span(int a, int b) { A = Mathf.Min(a, b); B = Mathf.Max(a, b); }
        }

        readonly List<Pole> _poles = new();
        readonly List<Span> _spans = new();
        MeshInstance3D _wires;
        ShaderMaterial _mat;

        public int PoleCount => _poles.Count;
        public int SpanCount => _spans.Count;
        public IReadOnlyList<Span> Spans => _spans;
        public Vector3 PoleOrigin(int i) => _poles[i].Origin;
        public Transform3D PoleXform(int i) => _poles[i].Xform;

        /// <summary>Sag of a span's midpoint below the straight line, as a FRACTION of its length. Real
        /// distribution lines hang a couple of percent; this is a look, not a derivation, and it is a fraction
        /// rather than metres so a long span across a valley sags more than a short one between two roadside
        /// poles instead of every span drooping by the same absolute amount.</summary>
        public const float SagFraction = 0.035f;
        public const float WireRadius = 0.045f;
        /// <summary>Samples along a span. The sag is a smooth curve and this is what makes it look like one;
        /// below about 8 the catenary reads as a bent stick.</summary>
        public const int SpanSamples = 14;
        /// <summary>Longest span the tool will let you make, metres. Beyond this the sag and the sway stop looking
        /// like a wire and start looking like a rope bridge, and it is nearly always a misclick on a distant pole.</summary>
        public const float MaxSpan = 140f;

        public override void _Ready()
        {
            // ⚠⚠ REGISTER THE GLOBAL BEFORE THE MATERIAL LINKS IT. A Godot material that binds a `global uniform`
            // which has not been registered yet binds it INVALID and never re-binds -- the shader then reads 0
            // forever and the wires hang dead still, with no error anywhere. Exactly the rule
            // ResourceField.MakeSwayMat and FoliageField already carry, and I walked into it anyway: the first
            // sway test produced two BYTE-IDENTICAL frames six seconds apart.
            GrassDisplacers.EnsureGlobals();
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/powerline_wire.gdshader") };
            _wires = new MeshInstance3D
            {
                MaterialOverride = _mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,   // a 9 cm wire casts a shadow nobody can see and every span would pay for it
                Name = "Wires",
            };
            AddChild(_wires);
        }

        /// <summary>Keep the wind alive in worlds that have no player -- the render harnesses and the showcase map.
        /// `PushGlobalsIfIdle` defers to the player whenever there is one, so this never competes in a live game;
        /// it exists so that "the wires sway" is true of every world they appear in rather than only the playable
        /// one. The editor drives it too, from its own camera.</summary>
        public override void _Process(double delta)
        {
            var cam = GetViewport()?.GetCamera3D();
            WindField.PushGlobalsIfIdle(cam != null ? cam.GlobalPosition : GlobalPosition, delta);
        }

        /// <summary>Register a pole. Called by WorldBuilder as it places props, and by the editor when one is
        /// placed or moved, so the two paths cannot disagree about which poles exist.</summary>
        public int AddPole(Transform3D xform)
        {
            _poles.Add(new Pole { Xform = xform });
            return _poles.Count - 1;
        }

        public void ClearPoles() { _poles.Clear(); _spans.Clear(); }

        /// <summary>The four wire points of pole `i`, in WORLD space.</summary>
        public void AnchorsWorld(int i, Vector3[] into)
        {
            var x = _poles[i].Xform;
            for (int k = 0; k < 4; k++) into[k] = x * AnchorsLocal[k];
        }

        /// <summary>Join two poles. Returns false (and changes nothing) if they are the same pole, already
        /// joined, or too far apart -- so a caller can report WHY rather than silently doing nothing.</summary>
        public bool Connect(int a, int b, out string why)
        {
            why = null;
            if (a == b) { why = "a pole cannot be wired to itself"; return false; }
            if (a < 0 || b < 0 || a >= _poles.Count || b >= _poles.Count) { why = "no such pole"; return false; }
            var s = new Span(a, b);
            foreach (var e in _spans) if (e.A == s.A && e.B == s.B) { why = "those poles are already wired"; return false; }
            float d = _poles[a].Origin.DistanceTo(_poles[b].Origin);
            if (d > MaxSpan) { why = $"too far apart ({d:0} m, max {MaxSpan:0})"; return false; }
            _spans.Add(s);
            return true;
        }

        public bool Disconnect(int a, int b)
        {
            var s = new Span(a, b);
            for (int i = 0; i < _spans.Count; i++)
                if (_spans[i].A == s.A && _spans[i].B == s.B) { _spans.RemoveAt(i); return true; }
            return false;
        }

        /// <summary>Every span touching this pole, removed. Used when a pole is deleted -- a span to a pole that
        /// no longer exists would index past the end of the list on the next rebuild.</summary>
        public int DisconnectAll(int pole)
        {
            int n = _spans.RemoveAll(s => s.A == pole || s.B == pole);
            return n;
        }

        /// <summary>The nearest pole to a world point within `maxDist`, or -1. Picking by POLE rather than by
        /// anchor: master asked for the four wires to connect as a set, so the thing you click is the pole.</summary>
        public int PickPole(Vector3 world, float maxDist = 6f)
        {
            int best = -1; float bestD = maxDist * maxDist;
            for (int i = 0; i < _poles.Count; i++)
            {
                float d = _poles[i].Origin.DistanceSquaredTo(world);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>One sample along a span's hanging curve, 0..1.
        ///
        /// ⭐ A PARABOLA, NOT A COSH. A real catenary and a parabola are indistinguishable at the sag a power line
        /// actually has (a couple of percent of span), and the parabola has no transcendental to solve for the
        /// sag parameter -- cosh needs a numeric solve to hit a TARGET sag, which is the thing being authored here.
        /// The sag is applied along WORLD DOWN rather than along the span's own normal, because gravity does not
        /// care which way the wire runs.</summary>
        public static Vector3 SpanPoint(Vector3 a, Vector3 b, float t)
        {
            float sag = a.DistanceTo(b) * SagFraction;
            return a.Lerp(b, t) + Vector3.Down * (sag * 4f * t * (1f - t));
        }

        /// <summary>Rebuild the whole wire mesh. Cheap enough to call on every edit: a map's grid is a few hundred
        /// spans and this is pure arithmetic, no scene nodes.</summary>
        public void Rebuild()
        {
            if (_wires == null) return;
            if (_spans.Count == 0) { _wires.Mesh = null; return; }

            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            var an = new Vector3[4];
            var bn = new Vector3[4];
            foreach (var s in _spans)
            {
                AnchorsWorld(s.A, an);
                AnchorsWorld(s.B, bn);
                for (int w = 0; w < 4; w++) AddWire(st, an[w], bn[w]);
            }
            st.GenerateNormals();
            _wires.Mesh = st.Commit();
            // The mesh is built in WORLD space (anchors come out of the poles' own transforms), so the holder must
            // sit at the origin or every wire would be offset by it.
            _wires.Transform = Transform3D.Identity;
            GlobalTransform = Transform3D.Identity;
        }

        /// <summary>One wire: a 4-sided tube following the hanging curve.
        ///
        /// ⚠ The span fraction `t` is written into UV.x and the shader reads it to decide how far THIS vertex is
        /// allowed to move in the wind -- the ends are pinned to their insulators and the middle is free. Baking it
        /// here is what lets the whole grid be one static mesh: without it the shader would have to know where the
        /// span's endpoints were, which is per-span data a single combined mesh cannot carry in a uniform.</summary>
        static void AddWire(SurfaceTool st, Vector3 a, Vector3 b)
        {
            Vector3 prev = default, prevN1 = default, prevN2 = default, prevTan = (b - a).Normalized();
            float prevT = 0f;
            bool have = false;
            for (int i = 0; i < SpanSamples; i++)
            {
                float t = i / (float)(SpanSamples - 1);
                Vector3 p = SpanPoint(a, b, t);
                // Tangent from the neighbouring sample so the ring stays perpendicular to the CURVE rather than to
                // the chord -- otherwise the tube pinches where the sag is steepest, which is at the two ends.
                Vector3 tan = i == 0
                    ? (SpanPoint(a, b, 1f / (SpanSamples - 1)) - p)
                    : (p - prev);
                tan = tan.LengthSquared() > 1e-10f ? tan.Normalized() : prevTan;
                prevTan = tan;
                Vector3 up = Mathf.Abs(tan.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
                Vector3 n1 = tan.Cross(up).Normalized() * WireRadius;
                Vector3 n2 = tan.Cross(n1).Normalized() * WireRadius;
                if (have) Ring(st, prev, prevN1, prevN2, p, n1, n2, prevT, t);
                prev = p; prevN1 = n1; prevN2 = n2; prevT = t; have = true;
            }
        }

        static void Ring(SurfaceTool st, Vector3 p0, Vector3 a0, Vector3 b0, Vector3 p1, Vector3 a1, Vector3 b1, float t0, float t1)
        {
            // Four quads around the tube. Written out rather than looped so the winding is visible at a glance --
            // [[reference_godot_traps_index]], a wrongly wound tube is invisible from outside and solid from within.
            Quad(st, p0 + a0, p0 + b0, p1 + b1, p1 + a1, t0, t1);
            Quad(st, p0 + b0, p0 - a0, p1 - a1, p1 + b1, t0, t1);
            Quad(st, p0 - a0, p0 - b0, p1 - b1, p1 - a1, t0, t1);
            Quad(st, p0 - b0, p0 + a0, p1 + a1, p1 - b1, t0, t1);
        }

        static void Quad(SurfaceTool st, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, float t0, float t1)
        {
            st.SetUV(new Vector2(t0, 0f)); st.AddVertex(v0);
            st.SetUV(new Vector2(t0, 1f)); st.AddVertex(v1);
            st.SetUV(new Vector2(t1, 1f)); st.AddVertex(v2);
            st.SetUV(new Vector2(t0, 0f)); st.AddVertex(v0);
            st.SetUV(new Vector2(t1, 1f)); st.AddVertex(v2);
            st.SetUV(new Vector2(t1, 0f)); st.AddVertex(v3);
        }

        // ---- save / load -------------------------------------------------------------------------------------
        //
        // ⚠ SPANS ARE SAVED AS POLE POSITIONS, NOT AS INDICES. An index is only meaningful against the exact pole
        // list that produced it, and that list is rebuilt from the map's props every load -- add one prop upstream
        // and every stored index now names a different pole. Positions survive that, and are what the editor's own
        // object save already records.

        public string SavePath(string mapName) => $"res://content/powerlines/editor_{mapName}_wires.txt";

        public void Save(string mapName)
        {
            var dir = "res://content/powerlines";
            if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(dir)))
                DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(dir));
            using var f = FileAccess.Open(SavePath(mapName), FileAccess.ModeFlags.Write);
            if (f == null) { Log.Err($"[powerline] cannot write {SavePath(mapName)}"); return; }
            f.StoreLine("# power line spans: ax ay az bx by bz (pole ORIGINS, not indices -- see PowerLineField)");
            foreach (var s in _spans)
            {
                var a = _poles[s.A].Origin; var b = _poles[s.B].Origin;
                f.StoreLine($"{a.X:0.###} {a.Y:0.###} {a.Z:0.###} {b.X:0.###} {b.Y:0.###} {b.Z:0.###}");
            }
            Log.Print($"[powerline] saved {_spans.Count} span(s) to {SavePath(mapName)}");
        }

        /// <summary>Load spans by matching each saved endpoint back to the nearest pole. Returns how many were
        /// restored and how many could not find a pole -- a count that is reported rather than swallowed, because
        /// silently dropping half a grid looks exactly like the save having failed.</summary>
        public int Load(string mapName, out int orphaned)
        {
            orphaned = 0;
            string path = SavePath(mapName);
            if (!FileAccess.FileExists(path)) return 0;
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (f == null) return 0;
            int got = 0;
            while (!f.EofReached())
            {
                string ln = f.GetLine().Trim();
                if (ln.Length == 0 || ln.StartsWith("#")) continue;
                var p = ln.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 6) continue;
                var a = new Vector3(Pf(p[0]), Pf(p[1]), Pf(p[2]));
                var b = new Vector3(Pf(p[3]), Pf(p[4]), Pf(p[5]));
                int ia = PickPole(a, 2f), ib = PickPole(b, 2f);
                if (ia < 0 || ib < 0) { orphaned++; continue; }
                if (Connect(ia, ib, out _)) got++;
            }
            if (got > 0 || orphaned > 0) Log.Print($"[powerline] loaded {got} span(s), {orphaned} orphaned");
            return got;
        }

        static float Pf(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }
}
