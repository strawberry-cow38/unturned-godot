using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>⭐ FENCES AS SPLINES: a fence laid along any path, solved in engine from the split pieces.
    ///
    /// Master 2026-10-14: "work on getting fences as splines, might wanna split the models into the individual pieces
    /// first and then solving them in engine?". tools/split_fence_parts.py cut every fence prop into its POST and one BAY
    /// of infill (rails and boards, pickets, a chain-link panel, a stone wall); this strings them along a line.
    ///
    /// ⭐⭐ POSTS STAND, BAYS FIT. A post goes on every corner of the path and then at the style's own pitch between, PLUMB
    /// whatever the ground does, and never changes shape. The bay between two posts is fitted to whatever distance they
    /// ended up apart: stretched a little (MinStretch..MaxStretch) where a whole number of bays nearly fits, and
    /// otherwise laid at its native size with the last one CUT SHORT, the way a real fence ends in a ripped panel. So a
    /// run of any length closes exactly on its end post, with no doubled post at a joint and no hole -- the two things
    /// tiling the whole prop could never avoid (EditorFenceRoad's guardrail history).
    ///
    /// ⭐ RACKED, NOT STEPPED. A bay's rails run from the ground at one post to the ground at the next and its boards
    /// stay vertical: the bay goes in through a SHEAR, its length axis carried along the slope while its up axis stays
    /// world-up. That is how a real fence follows a hill, and it is one affine map per bay -- no per-board solve.
    ///
    /// ⚠ ONE MESH PER STYLE PER 64 m CELL, NOT A PROP PER PIECE. A town's yard fences are a kilometre or more: as props
    /// that is thousands of nodes, each with its own trimesh collider. Here the pieces' triangles are copied into a few
    /// merged meshes and each bay and post gets a convex collider in one static body per cell.</summary>
    public static class FenceSpline
    {
        /// <summary>One fence family. `Pitch` is post centre to post centre, as split_fence_parts.py measured it off the
        /// prop: the bay's own extent is NOT it (Fence_Wood_2's pickets stop 0.1 m short of each post centre).</summary>
        public sealed class Style
        {
            public readonly string Name;
            public readonly float Pitch;
            /// <summary>The style's WRECKED section (split bay-only by split_fence_parts.py), or null, and how many of this
            /// style's bays it spans post to post: Fence_Wood_Broken_0 is 4 m of a 2 m-pitch fence, so two.</summary>
            public readonly string Wreck;
            public readonly int WreckBays;
            public Style(string name, float pitch, string wreck = null, int wreckBays = 1)
            { Name = name; Pitch = pitch; Wreck = wreck; WreckBays = wreck != null ? wreckBays : 0; }
            public string PostPiece => Name + "_Post";
            public string BayPiece => Name + "_Bay";
            public string WreckPiece => Wreck + "_Bay";
            public override string ToString() => Name;
        }

        public static readonly Style WoodPrivacy = new("Fence_Wood_0", 2.0f, "Fence_Wood_Broken_0", 2);   // 2.3 m posts, eight boards, two rails
        public static readonly Style WoodRanch = new("Fence_Wood_1", 2.0f);         // 1.25 m posts, three rails
        public static readonly Style WoodPicket = new("Fence_Wood_2", 3.8718f);     // white pickets, scalloped top
        public static readonly Style WoodPalisade = new("Fence_Wood_3", 6.0f, "Fence_Wood_Broken_3");   // 3.75 m posts, a solid panel
        public static readonly Style ChainLink = new("Fence_Metal_0", 4.0f, "Fence_Metal_Broken_0");    // 2.3 m, cutout mesh
        public static readonly Style ChainLinkTall = new("Fence_Metal_1", 6.0f, "Fence_Metal_Broken_1"); // 3.9 m, angled top
        public static readonly Style StoneWall = new("Fence_Stone_0", 4.0f);        // 2 m wall, 0.7 m pillars
        public static readonly Style StoneWallTall = new("Fence_Stone_1", 6.0f);    // 4.4 m wall, 0.7 m pillars
        public static readonly Style[] All =
            { WoodPrivacy, WoodRanch, WoodPicket, WoodPalisade, ChainLink, ChainLinkTall, StoneWall, StoneWallTall };

        /// <summary>The style named `name` (the source prop's name), or null.</summary>
        public static Style ByName(string name)
        {
            foreach (var s in All) if (s.Name == name) return s;
            return null;
        }

        /// <summary>How far a bay may stretch or squeeze before the next one is laid at native size and the last cut short.
        /// ±15-20% reads as the same fence; a 1.5x board does not.</summary>
        public const float MinStretch = 0.85f, MaxStretch = 1.2f;
        /// <summary>A remainder under this share of a bay is absorbed by stretching the whole ones instead of cutting a sliver.</summary>
        public const float MinCut = 0.2f;
        /// <summary>The shortest stretch worth any infill: under it the posts at its ends stand alone.</summary>
        public const float MinBay = 0.25f;
        /// <summary>How far a post goes into the ground below the LOWEST point under its footprint, so a post on a slope never
        /// shows daylight under its downhill side.</summary>
        public const float PostSink = 0.05f;
        /// <summary>Two ends within this are one post -- runs meeting at a corner share it.</summary>
        public const float PostWeld = 0.2f;
        /// <summary>The merged meshes and their colliders are cut into cells this size, so the renderer can cull what is
        /// behind the camera and a collider query only meets the fences nearby.</summary>
        public const float CellSize = 64f;

        /// <summary>One bay: the posts it runs between and how much of the native bay it lays -- 1 = whole (fitted to the
        /// gap), under 1 = cut short at B. A WRECK spans Span bays' worth of run between its two posts, with none between.</summary>
        public struct Bay { public int A, B; public float Keep; public bool Wreck; public int Span; }

        /// <summary>How far from a dropped marker a wreck may land: the group of bays nearest it, within this.</summary>
        public const float WreckSnap = 6f;
        /// <summary>The sharpest a curve may turn across a wreck that spans more than one bay: it is one rigid piece, a chord
        /// across what would have been a post.</summary>
        public const float WreckMaxTurnDeg = 6f;

        /// <summary>Every fence of one style laid so far: its posts (each on the ground at its centre) and its bays.</summary>
        public sealed class Run
        {
            public readonly Style Style;
            public readonly List<Vector3> Posts = new();
            public readonly List<Bay> Bays = new();
            public Run(Style s) { Style = s; }
        }

        /// <summary>The cuts along a stretch `len` long: (start, end, keep) per bay, by the rule in the class summary.</summary>
        public static void Fit(float len, float pitch, List<(float s0, float s1, float keep)> into)
        {
            into.Clear();
            if (len < MinBay || pitch <= 0f) return;
            float n = len / pitch;
            if (n < 1f) { into.Add((0f, len, n)); return; }                       // one bay, cut short
            int whole = Mathf.Max(1, Mathf.RoundToInt(n));
            float stretch = len / (whole * pitch);
            if (stretch >= MinStretch && stretch <= MaxStretch)
            {
                for (int i = 0; i < whole; i++) into.Add((len * i / whole, len * (i + 1) / whole, 1f));
                return;
            }
            int full = Mathf.FloorToInt(n);
            float rest = len - full * pitch;
            if (rest < pitch * MinCut)
            {
                for (int i = 0; i < full; i++) into.Add((len * i / full, len * (i + 1) / full, 1f));
                return;
            }
            for (int i = 0; i < full; i++) into.Add((i * pitch, (i + 1) * pitch, 1f));
            into.Add((full * pitch, len, rest / pitch));
        }

        /// <summary>Collects fence lines of any styles, sharing posts where lines meet, then builds them in one go.</summary>
        public sealed class Builder
        {
            readonly Func<float, float, float> _ground;
            readonly Dictionary<Style, Run> _runs = new();
            readonly Dictionary<(Style, int, int), List<int>> _postCells = new();
            readonly List<(float s0, float s1, float keep)> _fit = new();
            public int Lines { get; private set; }
            public float Length { get; private set; }

            /// <param name="ground">The ground height at (x, z); null keeps each point's own Y.</param>
            public Builder(Func<float, float, float> ground) { _ground = ground; }

            public IEnumerable<Run> Runs => _runs.Values;

            Run RunFor(Style s)
            {
                if (!_runs.TryGetValue(s, out var r)) _runs[s] = r = new Run(s);
                return r;
            }

            float GroundAt(Vector3 p) => _ground != null ? _ground(p.X, p.Z) : p.Y;

            /// <summary>The post at `p`, or a new one: any existing post of this style within PostWeld is the same post.</summary>
            int PostAt(Run r, Vector3 p)
            {
                int cx = Mathf.FloorToInt(p.X / PostWeld), cz = Mathf.FloorToInt(p.Z / PostWeld);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (_postCells.TryGetValue((r.Style, cx + dx, cz + dz), out var list))
                            foreach (int i in list)
                            {
                                var q = r.Posts[i];
                                if (new Vector2(q.X - p.X, q.Z - p.Z).Length() < PostWeld) return i;
                            }
                int k = r.Posts.Count;
                r.Posts.Add(new Vector3(p.X, GroundAt(p), p.Z));
                if (!_postCells.TryGetValue((r.Style, cx, cz), out var cell)) _postCells[(r.Style, cx, cz)] = cell = new List<int>();
                cell.Add(k);
                return k;
            }

            /// <summary>One straight leg of a line, or the whole of a curve: where arc length s is, and its fitted bays.</summary>
            sealed class Leg
            {
                public Func<float, Vector3> At; public float Len; public bool Curved;
                public readonly List<(float s0, float s1, float keep)> Fit = new();
                public readonly HashSet<int> WreckStart = new();
            }

            /// <summary>Wrecks laid so far.</summary>
            public int Wrecks { get; private set; }

            /// <summary>A fence along `pts`. `smooth` = a curve THROUGH the points (Catmull-Rom, as every spline tool here
            /// draws one); otherwise straight legs with a post on every corner.
            ///
            /// ⭐ WRECKS GO IN ON THE POST RHYTHM (strawberry 2026-10-14: "add support for the fence broken props to be inserted
            /// along the spline (lining up with the fences ofc)"). A wreck replaces whole bays -- WreckBays of them, never a
            /// cut one -- between two of the run's own posts, fitted exactly as the bays either side of it are: so it starts
            /// and ends on the same posts they do. `wreckAt`: put one in the group of bays nearest each point (within
            /// WreckSnap). `wear`: also break each eligible group with that chance, from `seed`.</summary>
            public void AddLine(Style s, IReadOnlyList<Vector3> pts, bool smooth = false,
                                IReadOnlyList<Vector3> wreckAt = null, float wear = 0f, ulong seed = 0)
            {
                if (s == null || pts == null || pts.Count < 2) return;
                var r = RunFor(s);
                Lines++;
                var legs = new List<Leg>();
                if (!smooth || pts.Count < 3)
                {
                    for (int k = 0; k + 1 < pts.Count; k++)
                    {
                        var a = pts[k]; var b = pts[k + 1];
                        float len = new Vector2(b.X - a.X, b.Z - a.Z).Length();
                        if (len < 1e-3f) continue;
                        legs.Add(new Leg { At = t => a.Lerp(b, Mathf.Clamp(t / len, 0f, 1f)), Len = len });
                    }
                }
                else
                {
                    var curve = SplineTiling.CurveThrough(pts, true);
                    float total = curve.GetBakedLength();
                    if (total >= 1e-3f) legs.Add(new Leg { At = t => curve.SampleBaked(Mathf.Clamp(t, 0f, total), true), Len = total, Curved = true });
                }
                foreach (var leg in legs) { Fit(leg.Len, s.Pitch, _fit); leg.Fit.AddRange(_fit); Length += leg.Len; }
                if (s.Wreck != null && (wreckAt != null || wear > 0f)) MarkWrecks(s, legs, wreckAt, wear, seed);
                foreach (var leg in legs)
                {
                    int prev = PostAt(r, leg.At(0f));
                    for (int i = 0; i < leg.Fit.Count;)
                    {
                        bool wreck = leg.WreckStart.Contains(i);
                        int span = wreck ? s.WreckBays : 1;
                        var (_, s1, keep) = leg.Fit[i + span - 1];
                        int next = PostAt(r, leg.At(s1));
                        if (next != prev) r.Bays.Add(new Bay { A = prev, B = next, Keep = wreck ? 1f : keep, Wreck = wreck, Span = span });
                        if (wreck) Wrecks++;
                        prev = next;
                        i += span;
                    }
                    if (leg.Fit.Count == 0) PostAt(r, leg.At(leg.Len));
                }
            }

            /// <summary>Choose the bays that are wrecked: whole ones only, WreckBays in a row, and -- on a curve, where a wreck
            /// spanning several bays is a rigid chord across their posts -- only where the line hardly turns.</summary>
            static void MarkWrecks(Style s, List<Leg> legs, IReadOnlyList<Vector3> wreckAt, float wear, ulong seed)
            {
                int k = s.WreckBays;
                bool Eligible(Leg leg, int i)
                {
                    if (i < 0 || i + k > leg.Fit.Count) return false;
                    for (int j = i; j < i + k; j++)
                        if (leg.Fit[j].keep < 0.999f || leg.WreckStart.Contains(j)) return false;
                    for (int j = i - k + 1; j < i; j++) if (j >= 0 && leg.WreckStart.Contains(j)) return false;   // inside another
                    if (k > 1 && leg.Curved)
                    {
                        var p0 = leg.At(leg.Fit[i].s0); var p1 = leg.At(leg.Fit[i].s1); var p2 = leg.At(leg.Fit[i + k - 1].s1);
                        var d0 = new Vector2(p1.X - p0.X, p1.Z - p0.Z); var d1 = new Vector2(p2.X - p1.X, p2.Z - p1.Z);
                        if (d0.LengthSquared() < 1e-6f || d1.LengthSquared() < 1e-6f) return false;
                        if (Mathf.RadToDeg(Mathf.Abs(d0.AngleTo(d1))) > WreckMaxTurnDeg) return false;
                    }
                    return true;
                }
                if (wreckAt != null)
                    foreach (var m in wreckAt)
                    {
                        Leg best = null; int bi = -1; float bd = WreckSnap;
                        foreach (var leg in legs)
                            for (int i = 0; i + k <= leg.Fit.Count; i++)
                            {
                                if (!Eligible(leg, i)) continue;
                                var mid = leg.At((leg.Fit[i].s0 + leg.Fit[i + k - 1].s1) * 0.5f);
                                float d = new Vector2(mid.X - m.X, mid.Z - m.Z).Length();
                                if (d < bd) { bd = d; best = leg; bi = i; }
                            }
                        best?.WreckStart.Add(bi);
                    }
                if (wear <= 0f) return;
                ulong h = seed ^ 0x9E3779B97F4A7C15UL;
                foreach (var leg in legs)
                    for (int i = 0; i + k <= leg.Fit.Count; i++)
                    {
                        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h += 0x632BE59BD9B4E5C1UL;
                        if ((h >> 40) / (float)(1UL << 24) >= wear || !Eligible(leg, i)) continue;
                        leg.WreckStart.Add(i);
                        i += k;                                   // (and a whole bay before the next can start)
                    }
            }

            /// <summary>Build every line added so far under `parent`: merged meshes and convex colliders, one of each per
            /// style per `cellSize` cell -- or ONE per style when `cellSize` is 0, centred on its first post, which is what a
            /// single fence the editor lays wants (one thing to pick, move and delete). Returns the nodes made.</summary>
            public int Build(Node3D parent, EditorObjects objs, string name = "Fences", float cellSize = CellSize)
            {
                if (parent == null || objs == null) return 0;
                int made = 0, posts = 0, bays = 0, cut = 0, wrecks = 0;
                foreach (var r in _runs.Values)
                {
                    var post = Piece.Of(objs, r.Style.PostPiece);
                    var bay = Piece.Of(objs, r.Style.BayPiece);
                    var wreck = r.Style.Wreck != null ? Piece.Of(objs, r.Style.WreckPiece) : null;
                    if (post == null || bay == null)
                    {
                        Log.Print($"[fence] {r.Style}: piece meshes missing ({r.Style.PostPiece}/{r.Style.BayPiece}) -- run tools/split_fence_parts.py");
                        continue;
                    }
                    var cells = new Dictionary<(int, int), Cell>();
                    Cell CellAt(Vector3 p)
                    {
                        var key = cellSize > 0f ? (Mathf.FloorToInt(p.X / cellSize), Mathf.FloorToInt(p.Z / cellSize)) : (0, 0);
                        if (!cells.TryGetValue(key, out var c))
                            cells[key] = c = new Cell(cellSize > 0f
                                ? new Vector3((key.Item1 + 0.5f) * cellSize, 0f, (key.Item2 + 0.5f) * cellSize)
                                : new Vector3(r.Posts[0].X, 0f, r.Posts[0].Z));
                        return c;
                    }
                    // each post faces along the fence: the axial mean of the bays it holds (so a straight run's posts are
                    // square to it and a corner post splits the angle)
                    // ⚠ AND SIGNED BY THE FIRST BAY. The axial mean has no sign, and two styles are not symmetric across the
                    // line -- Fence_Metal_1's posts carry their angled arm, and Fence_Stone_1's pillars their buttress, on
                    // local +Y, the same side as their bays' tops. A post turned 180 would put its arm on the other side.
                    var axis = new Vector2[r.Posts.Count];
                    var first = new Vector2[r.Posts.Count];
                    foreach (var b in r.Bays)
                    {
                        var d = new Vector2(r.Posts[b.B].X - r.Posts[b.A].X, r.Posts[b.B].Z - r.Posts[b.A].Z);
                        if (d.LengthSquared() < 1e-8f) continue;
                        d = d.Normalized();
                        var dbl = new Vector2(d.X * d.X - d.Y * d.Y, 2f * d.X * d.Y);   // doubled angle: a direction is an AXIS
                        axis[b.A] += dbl; axis[b.B] += dbl;
                        if (first[b.A] == Vector2.Zero) first[b.A] = d;
                        if (first[b.B] == Vector2.Zero) first[b.B] = d;
                    }
                    for (int i = 0; i < r.Posts.Count; i++)
                    {
                        var a = axis[i];
                        float ang = a.LengthSquared() < 1e-8f ? Mathf.Atan2(first[i].Y, first[i].X) : Mathf.Atan2(a.Y, a.X) * 0.5f;
                        var along = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                        if (along.X * first[i].X + along.Z * first[i].Y < 0f) along = -along;
                        var c = CellAt(r.Posts[i]);
                        c.AddPost(post, r.Posts[i], along, _ground);
                        posts++;
                    }
                    foreach (var b in r.Bays)
                    {
                        var pa = r.Posts[b.A]; var pb = r.Posts[b.B];
                        // ⭐ a wreck is the broken prop's infill over its whole span, and NO collider: what is left of it is
                        // boards on the ground and a panel hanging off a post -- the gap is the point of a broken fence
                        if (b.Wreck && wreck != null) { CellAt((pa + pb) * 0.5f).AddBay(wreck, pa, pb, 1f, r.Style.Pitch * b.Span, false); wrecks++; }
                        else CellAt((pa + pb) * 0.5f).AddBay(bay, pa, pb, b.Keep, r.Style.Pitch * Mathf.Max(1, b.Span));
                        bays++; if (b.Keep < 0.999f) cut++;
                    }
                    var mat = objs.PreviewMaterial(r.Style.Name);
                    var surf = (int)PropSurfaces.SurfForProp(r.Style.Name);
                    // big opaque walls block the item line-of-sight like any big structure; the rest are see-through to
                    // it and solid to the player (EditorObjects.WorldLayerFor's rule, by the bay's size)
                    uint layer = r.Style.Pitch >= 5f && mat != null && mat.Transparency == BaseMaterial3D.TransparencyEnum.Disabled
                        ? 1u << 0 : 1u << 6;
                    foreach (var (key, c) in cells)
                    {
                        var node = c.Commit($"{name}_{r.Style.Name}_{key.Item1}_{key.Item2}", mat, layer, surf);
                        if (node == null) continue;
                        parent.AddChild(node);
                        made++;
                    }
                }
                Log.Print($"[fence] {Lines} line(s), {Length:0} m: {posts} posts, {bays} bays ({cut} cut short, {wrecks} wrecked) in {made} cell(s)"
                        + $" -- {string.Join(", ", ListStyles())}");
                return made;
            }

            IEnumerable<string> ListStyles()
            {
                foreach (var r in _runs.Values) yield return $"{r.Style.Name} {r.Posts.Count}p/{r.Bays.Count}b";
            }
        }

        /// <summary>A piece's triangles, read once out of the mesh the prop loader built (so they are exactly what a placed
        /// prop draws: same axes, same UVs, same winding), with its bounds.</summary>
        sealed class Piece
        {
            public Vector3[] V, N; public Vector2[] UV; public Color[] C;
            public Aabb Box;
            static readonly Dictionary<string, Piece> _cache = new();

            public static Piece Of(EditorObjects objs, string name)
            {
                if (_cache.TryGetValue(name, out var hit)) return hit;
                Piece p = null;
                if (objs.PreviewMesh(name) is ArrayMesh m && m.GetSurfaceCount() > 0)
                {
                    var arr = m.SurfaceGetArrays(0);
                    var v = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var n = arr[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? null : arr[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    var uv = arr[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? null : arr[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                    var c = arr[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.Nil ? null : arr[(int)Mesh.ArrayType.Color].AsColorArray();
                    var idx = arr[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : arr[(int)Mesh.ArrayType.Index].AsInt32Array();
                    int count = idx != null ? idx.Length : v.Length;
                    p = new Piece { V = new Vector3[count], N = new Vector3[count], UV = new Vector2[count], C = new Color[count] };
                    for (int i = 0; i < count; i++)
                    {
                        int k = idx != null ? idx[i] : i;
                        p.V[i] = v[k];
                        p.N[i] = n != null ? n[k] : Vector3.Up;
                        p.UV[i] = uv != null ? uv[k] : Vector2.Zero;
                        p.C[i] = c != null ? c[k] : Colors.White;
                    }
                    var box = new Aabb(p.V[0], Vector3.Zero);
                    foreach (var q in p.V) box = box.Expand(q);
                    p.Box = box;
                }
                _cache[name] = p;
                return p;
            }
        }

        /// <summary>One cell's worth of one style: the merged triangles and the colliders, around a local origin (the cell's
        /// middle) so neither holds kilometre-sized coordinates.</summary>
        sealed class Cell
        {
            readonly Vector3 _origin;
            readonly List<Vector3> _v = new(), _n = new();
            readonly List<Vector2> _uv = new();
            readonly List<Color> _c = new();
            readonly List<Vector3[]> _hulls = new();
            public Cell(Vector3 origin) { _origin = origin; }

            /// <summary>A post at ground point `g` (its centre), turned along `along`: plumb, its foot PostSink under the
            /// lowest ground beneath it, its top at its native height over the ground at its centre.</summary>
            public void AddPost(Piece p, Vector3 g, Vector3 along, Func<float, float, float> ground)
            {
                var perp = Vector3.Up.Cross(along);
                float low = g.Y;
                if (ground != null)
                    foreach (var (sx, sy) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                    {
                        var q = g + along * (sx * Mathf.Max(Mathf.Abs(p.Box.Position.X), Mathf.Abs(p.Box.End.X)))
                                  + perp * (sy > 0 ? p.Box.End.Y : p.Box.Position.Y);
                        low = Mathf.Min(low, ground(q.X, q.Z));
                    }
                float z0 = p.Box.Position.Z, z1 = p.Box.End.Z;
                float foot = low - PostSink, top = g.Y + z1;
                float zs = (top - foot) / Mathf.Max(1e-3f, z1 - z0);
                var basis = new Basis(along, perp, Vector3.Up * zs);
                var origin = new Vector3(g.X, foot - z0 * zs, g.Z);
                Append(p, new Transform3D(basis, origin), float.PositiveInfinity);
                _hulls.Add(Hull(p.Box, new Transform3D(basis, origin), 0.05f));
            }

            /// <summary>A bay from post ground point `a` to `b`, laying `keep` of the native bay: the piece's local x from
            /// -pitch/2 runs to a, its far end (cut at -pitch/2 + keep*pitch) to b, racked so its bottom follows the line
            /// between the two grounds and its verticals stay vertical.</summary>
            public void AddBay(Piece p, Vector3 a, Vector3 b, float keep, float pitch, bool collide = true)
            {
                var chord = b - a;
                var flat = new Vector3(chord.X, 0f, chord.Z);
                if (flat.LengthSquared() < 1e-6f) return;
                var d = flat.Normalized();
                var perp = Vector3.Up.Cross(d);
                float span = Mathf.Max(1e-3f, keep * pitch);
                float half = pitch * 0.5f;
                var basis = new Basis(chord / span, perp, Vector3.Up);
                var xf = new Transform3D(basis, a + chord * (half / span));
                float cutAt = keep >= 0.999f ? float.PositiveInfinity : -half + span;
                Append(p, xf, cutAt);
                if (!collide) return;
                var box = p.Box;
                float x0 = Mathf.Max(box.Position.X, -half), x1 = Mathf.Min(box.End.X, cutAt);
                if (x1 > x0)
                    _hulls.Add(Hull(new Aabb(new Vector3(x0, box.Position.Y, box.Position.Z),
                                             new Vector3(x1 - x0, box.Size.Y, box.Size.Z)), xf, 0.05f));
            }

            /// <summary>The piece's triangles through `xf`, the part with local x past `cutAt` clipped away. Normals go through
            /// the inverse transpose: a racked bay is SHEARED, and the plain basis would tilt every board's face normal
            /// along the slope.</summary>
            void Append(Piece p, Transform3D xf, float cutAt)
            {
                var nb = xf.Basis.Inverse().Transposed();
                var local = new Transform3D(xf.Basis, xf.Origin - _origin);
                for (int t = 0; t + 2 < p.V.Length; t += 3)
                {
                    float ax = p.V[t].X, bx = p.V[t + 1].X, cx = p.V[t + 2].X;
                    if (ax <= cutAt && bx <= cutAt && cx <= cutAt)
                    {
                        for (int k = 0; k < 3; k++) Emit(p.V[t + k], p.N[t + k], p.UV[t + k], p.C[t + k], local, nb);
                        continue;
                    }
                    if (ax > cutAt && bx > cutAt && cx > cutAt) continue;
                    // Sutherland-Hodgman against x <= cutAt: at most four corners, fanned back into two triangles
                    var poly = new List<(Vector3 v, Vector3 n, Vector2 uv, Color c)>(4);
                    for (int k = 0; k < 3; k++)
                    {
                        int i0 = t + k, i1 = t + (k + 1) % 3;
                        bool in0 = p.V[i0].X <= cutAt, in1 = p.V[i1].X <= cutAt;
                        if (in0) poly.Add((p.V[i0], p.N[i0], p.UV[i0], p.C[i0]));
                        if (in0 != in1)
                        {
                            float f = (cutAt - p.V[i0].X) / (p.V[i1].X - p.V[i0].X);
                            poly.Add((p.V[i0].Lerp(p.V[i1], f), p.N[i0].Lerp(p.N[i1], f).Normalized(),
                                      p.UV[i0].Lerp(p.UV[i1], f), p.C[i0].Lerp(p.C[i1], f)));
                        }
                    }
                    for (int k = 1; k + 1 < poly.Count; k++)
                        foreach (var q in new[] { poly[0], poly[k], poly[k + 1] })
                            Emit(q.v, q.n, q.uv, q.c, local, nb);
                }
            }

            void Emit(Vector3 v, Vector3 n, Vector2 uv, Color c, Transform3D local, Basis nb)
            {
                _v.Add(local * v);
                _n.Add((nb * n).Normalized());
                _uv.Add(uv);
                _c.Add(c);
            }

            /// <summary>The eight corners of `box` through `xf`, relative to the cell, thickened to at least `minThick` across
            /// (a chain-link panel is a plane, and a hull of a plane is no collider).</summary>
            Vector3[] Hull(Aabb box, Transform3D xf, float minThick)
            {
                var lo = box.Position; var hi = box.End;
                if (hi.Y - lo.Y < minThick) { float m = (lo.Y + hi.Y) * 0.5f; lo.Y = m - minThick * 0.5f; hi.Y = m + minThick * 0.5f; }
                var pts = new Vector3[8];
                int i = 0;
                foreach (float x in new[] { lo.X, hi.X })
                    foreach (float y in new[] { lo.Y, hi.Y })
                        foreach (float z in new[] { lo.Z, hi.Z })
                            pts[i++] = xf * new Vector3(x, y, z) - _origin;
                return pts;
            }

            public Node3D Commit(string name, Material mat, uint layer, int surf)
            {
                if (_v.Count < 3) return null;
                var arr = new Godot.Collections.Array();
                arr.Resize((int)Mesh.ArrayType.Max);
                arr[(int)Mesh.ArrayType.Vertex] = _v.ToArray();
                arr[(int)Mesh.ArrayType.Normal] = _n.ToArray();
                arr[(int)Mesh.ArrayType.TexUV] = _uv.ToArray();
                arr[(int)Mesh.ArrayType.Color] = _c.ToArray();
                var mesh = new ArrayMesh();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
                var root = new Node3D { Name = name, Position = _origin };
                root.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh, MaterialOverride = mat });
                var body = new StaticBody3D { Name = "Body", CollisionLayer = layer, CollisionMask = 0 };
                body.SetMeta(PlayerController.SurfMeta, surf);
                foreach (var h in _hulls) body.AddChild(new CollisionShape3D { Shape = new ConvexPolygonShape3D { Points = h } });
                root.AddChild(body);
                return root;
            }
        }
    }
}
