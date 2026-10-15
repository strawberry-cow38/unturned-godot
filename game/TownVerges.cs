using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // ==============================================================================================
    // STREET PROFILES AND STREET TREES (strawberry 2026-10-15: "add support for square cut versions of the street parking
    // bays. support for kerb but no sidewalk for roads, as well as kerb-grass median-sidewalk, as well as square grass
    // "planters" for trees on sidewalks").
    //
    // Every street keeps the same frontage -- kerb face to plot line is PavementWidth, whatever fills it -- so the plots,
    // the lamps and the bays' arithmetic never learn about profiles. What changes is what fills the band:
    //   Pavement -- paved to the plot line (as before), some with square grass planters and a tree in each along the kerb;
    //   Verge    -- a kerb strip, a grass verge with trees in it, then the footway;
    //   KerbOnly -- a kerb stone, and grass falling away from it to the plot line: no footway.
    // A profile belongs to a street LINE, the same from junction to junction down its length. The mains, and any line with a
    // shop, apartments or a parking lot on it, are always paved; only a paved street has parking bays.
    // ==============================================================================================
    public sealed partial class TownRoadGrid
    {
        public enum StreetProfile { Pavement, Verge, KerbOnly }
        /// <summary>UG_TOWNPROFILE=pavement|verge|kerb: every side street that profile -- the renders' control. Otherwise the
        /// seed's mix.</summary>
        public string ProfileForce = System.Environment.GetEnvironmentVariable("UG_TOWNPROFILE");
        /// <summary>The share of side-street lines given a grass verge, and a kerb alone; the rest are paved.</summary>
        public float VergeShare = 0.35f, KerbOnlyShare = 0.2f;
        /// <summary>The concrete strip left showing in front of a verge -- and a kerb-only street's kerb stone -- and the
        /// verge's width (the footway is what is left of PavementWidth).</summary>
        public float KerbBand = 0.35f, VergeWidth = 1.6f;
        /// <summary>How far short of a lane's mouth (or a lot's way in) the grass stops, the run paved full width there to meet
        /// the kerb return. At a junction it is the crossing's dropped kerb that sets it (EndLanding).</summary>
        public float GrassEndClear = 3f;
        /// <summary>How high the verge's grass sits over the paving it is laid on: a slab of turf, not a decal.</summary>
        public float VergeLift = 0.04f;
        /// <summary>The flat green the turf was drawn in before it took the terrain's grass -- now only the fallback when the
        /// terrain's textures cannot be had.</summary>
        public static readonly Color StreetGrassColour = new Color(0.40f, 0.58f, 0.26f);

        // ---- THE TERRAIN'S OWN GRASS (strawberry 2026-10-15: "make grass medians everywhere use the real grass terrain texture") ----
        // Every patch of turf the town lays -- verges, kerb-only lawns, planters, a lot's islands and edges, the boulevard's
        // median -- is drawn with the terrain's grass: the SAME albedo array (layer 2), tiled the same 16 m in world space off
        // the same floating-origin position, so where a lawn meets the ground the texture runs straight on; and it takes the
        // terrain's snow the way the terrain's grass does. Grass never takes the terrain's wet look, so neither does this.
        // ⚠ GLSL IN A C# @-STRING: a double quote ends the literal (Terrain.cs learned it with a 19-error build). None here.
        const string GroundGrassShader = @"
shader_type spatial;
uniform sampler2DArray albedos : source_color, filter_linear_mipmap, repeat_enable;
uniform float tileWorld = 16.0;
global uniform vec2 ug_origin;
global uniform float snow_cover;
varying vec3 wpos;
void vertex() { wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
float chashv(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 cgrad(vec2 ip) { float h = chashv(ip) * 6.2831853; return vec2(cos(h), sin(h)); }
float cnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p); vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = dot(cgrad(i), f), b = dot(cgrad(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
    float c = dot(cgrad(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0)), d = dot(cgrad(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y) * 0.8 + 0.5;
}
void fragment() {
    vec2 pxz = wpos.xz + ug_origin;
    vec2 tuv = pxz / tileWorld;
    ALBEDO = texture(albedos, vec3(tuv, 2.0)).rgb;
    ROUGHNESS = 1.0;
    if (snow_cover > 0.0) {
        float sn = cnoise(pxz * 0.07);
        float snowAmt = clamp(smoothstep(0.35, 0.95, snow_cover * 1.45 - sn * 0.45), 0.0, 1.0);
        if (snowAmt > 0.0) {
            ALBEDO = mix(ALBEDO, texture(albedos, vec3(tuv, 6.0)).rgb, snowAmt);
            ROUGHNESS = mix(ROUGHNESS, 0.82, snowAmt);
        }
    }
}
";
        static ShaderMaterial _groundGrass; static Texture2DArray _groundGrassFrom;
        /// <summary>The terrain's grass, for turf (above). One material, shared; the flat green if the terrain's textures are missing.</summary>
        public static Material GroundGrassMaterial()
        {
            var arr = Terrain.GroundAlbedos();
            if (arr == null) return new StandardMaterial3D { AlbedoColor = StreetGrassColour, Roughness = 1f };
            if (_groundGrass != null && ReferenceEquals(_groundGrassFrom, arr)) return _groundGrass;
            RainSystem3D.EnsureGlobals();       // its globals (ug_origin, snow_cover) must exist before it compiles, as the terrain's do
            _groundGrass = new ShaderMaterial { Shader = new Shader { Code = GroundGrassShader } };
            _groundGrass.SetShaderParameter("albedos", arr);
            _groundGrass.SetShaderParameter("tileWorld", 16f);
            _groundGrassFrom = arr;
            return _groundGrass;
        }

        /// <summary>Where a tree's origin goes to stand at `at` the way the world's trees stand on the terrain: let into the ground
        /// ResourceField.TreeSink per unit of its scale, its root flare under the surface rather than sat on top of it
        /// (strawberry 2026-10-15: "make sure trees placed in those planters sit at the correct height, like how they are on
        /// normal terrain"). Every tree the town plants goes through here -- a placed prop never had the sink ResourceField gives
        /// the world's own.</summary>
        public static Vector3 SeatedTree(Vector3 at, float scale) => new Vector3(at.X, at.Y - ResourceField.TreeSink * Mathf.Abs(scale), at.Z);
        readonly Dictionary<int, StreetProfile> _streetProfile = new();
        /// <summary>What the last PlanProfiles gave: streets (roads, not lines) of each profile.</summary>
        public int PavedStreets, VergeStreets, KerbOnlyStreets;

        /// <summary>Where the renders look at each of these (UG_TOWNCLOSE=vg|ko|tp|sq): the example nearest the town's centre --
        /// a point at the kerb, the street's direction there, and out across the pavement.</summary>
        public readonly Dictionary<string, (Vector3 at, Vector3 along, Vector3 outw)> Shots = new();
        void OfferShot(string key, Vector3 at, Vector3 along, Vector3 outw)
        {
            if (Shots.TryGetValue(key, out var was) && Flat(was.at, Centre) <= Flat(at, Centre)) return;
            Shots[key] = (at, along, outw);
        }

        /// <summary>The profile PlanProfiles gave road `r` -- Pavement for anything it did not see (a lane, or before it ran).</summary>
        public StreetProfile ProfileOf(int r) => _streetProfile.TryGetValue(r, out var p) ? p : StreetProfile.Pavement;

        /// <summary>Which line of the grid a street lies on: (0, v) running along u, (1, u) along v, (2, 0) anything else (the
        /// diagonal, a rounded corner) -- so a profile holds from junction to junction down the whole line. Buildable's frame.</summary>
        (int axis, int line) LineOf(RoadField roads, int r)
        {
            float L = roads.RoadLength(r);
            if (!roads.EvaluateAlong(r, L * 0.5f, out var p, out var t, snapTerrain: false)) return (3, r);
            float c = Mathf.Cos(Bearing), s = Mathf.Sin(Bearing);
            var tn = new Vector2(t.X, t.Z).Normalized();
            float dx = p.X - Centre.X, dz = p.Z - Centre.Z;
            float u = dx * c + dz * s, v = -dx * s + dz * c;
            if (Mathf.Abs(tn.X * c + tn.Y * s) > 0.95f) return (0, Mathf.RoundToInt(v / Mathf.Max(1f, Spacing)));
            if (Mathf.Abs(-tn.X * s + tn.Y * c) > 0.95f) return (1, Mathf.RoundToInt(u / Mathf.Max(1f, Spacing)));
            return (2, 0);
        }
        ulong LineSeed(RoadField roads, int r, ulong salt)
        {
            var (axis, line) = LineOf(roads, r);
            return Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)axis << 40) ^ (ulong)(uint)(line + 100000) ^ salt));
        }

        /// <summary>Give every street its profile. Once the plots are laid out -- a shop or a lot decides its street -- and
        /// before the bays, which only a paved street has.</summary>
        public void PlanProfiles(RoadField roads)
        {
            _streetProfile.Clear(); PavedStreets = VergeStreets = KerbOnlyStreets = 0;
            if (roads == null) return;
            var busy = new HashSet<(int, int)>();
            foreach (var pl in Plots)
                if (pl.Kind is PlotKind.Shop or PlotKind.Apartments or PlotKind.Parking)
                    foreach (int rr in new[] { pl.Road, pl.Road2 }) if (rr >= 0) busy.Add(LineOf(roads, rr));
            foreach (int r in Built)
            {
                var prof = StreetProfile.Pavement;
                if (!IsMain(roads, r) && !busy.Contains(LineOf(roads, r)))
                {
                    if (ProfileForce == "verge") prof = StreetProfile.Verge;
                    else if (ProfileForce == "kerb") prof = StreetProfile.KerbOnly;
                    else if (ProfileForce != "pavement")
                    {
                        float u = Unit(LineSeed(roads, r, 0x9E4CEUL), 0);
                        prof = u < VergeShare ? StreetProfile.Verge : u < VergeShare + KerbOnlyShare ? StreetProfile.KerbOnly : StreetProfile.Pavement;
                    }
                }
                _streetProfile[r] = prof;
                if (prof == StreetProfile.Verge) VergeStreets++; else if (prof == StreetProfile.KerbOnly) KerbOnlyStreets++; else PavedStreets++;
            }
            Log.Print($"[towngrid] street profiles: {PavedStreets} paved, {VergeStreets} with a grass verge, {KerbOnlyStreets} kerb only"
                    + $" ({busy.Count} line(s) kept paved for their shops, flats or lots{(string.IsNullOrEmpty(ProfileForce) ? "" : $"; UG_TOWNPROFILE={ProfileForce}")})");
        }

        // ---- the runs of pavement LaySidewalks lays, which the grass and the trees follow ---------------------------

        /// <summary>A run of one side's pavement, in arc metres along its street, and whether each end is at a junction (the
        /// corner's pavement and a crossing come in) rather than at a lane's mouth or a lot's way in (a kerb return).</summary>
        public readonly struct WalkRun
        {
            public readonly int Side; public readonly float A, B; public readonly bool JunctionA, JunctionB;
            public WalkRun(int side, float a, float b, bool ja, bool jb) { Side = side; A = a; B = b; JunctionA = ja; JunctionB = jb; }
        }
        /// <summary>Each side's runs of road `r`, cut where LaySidewalks cuts them (the same trims, the same mouths).</summary>
        public List<WalkRun> WalkRuns(RoadField roads, int r)
        {
            var runs = new List<WalkRun>();
            float L = roads.RoadLength(r);
            float t0 = TrimFor(roads, r, false, CornerRadius), t1 = L - TrimFor(roads, r, true, CornerRadius);
            foreach (int sd in new[] { 1, -1 })
            {
                var cuts = new List<LaneMouth>();
                foreach (var m in LaneMouths) if (m.Road == r && m.Side == sd) cuts.Add(m);
                cuts.Sort((x, y) => x.From.CompareTo(y.From));
                float at = t0; bool jAt = true;
                foreach (var m in cuts) { if (m.From > at) runs.Add(new WalkRun(sd, at, m.From, jAt, false)); at = m.To; jAt = false; }
                if (t1 > at) runs.Add(new WalkRun(sd, at, t1, jAt, true));
            }
            return runs;
        }
        /// <summary>How much of a run's end stays paved full width: a junction's end the crossing's dropped kerb and its ramp
        /// (and a little), a mouth's end GrassEndClear.</summary>
        float EndLanding(bool junction, float dipRamp) => junction ? Mathf.Max(GrassEndClear, CrossingDipLength + dipRamp + 0.5f) : GrassEndClear;

        /// <summary>Is arc `d` on `side` of road `r` within `pad` of a parking bay -- its kerb's step and its pavement's, both?</summary>
        bool NearBay(int r, int side, float d, float pad)
        {
            foreach (var bay in Bays)
                if (bay.Road == r && bay.Side == side && d > Mathf.Min(bay.A, bay.Front0) - pad && d < Mathf.Max(bay.B, bay.Front1) + pad) return true;
            return false;
        }

        /// <summary>LaySidewalks' piece for a kerb-only street: a kerb stone KerbBand wide, paved full width only on its
        /// landings at each end.</summary>
        int LayKerbOnly(EditorSidewalk walk, RoadField roads, int road, EditorSidewalk.Side side, float from, float toEnd,
                        string piece, float min, bool junctionA, bool junctionB)
        {
            float a = from, b = roads.RoadLength(road) - toEnd;
            float la = EndLanding(junctionA, walk.DipRamp), lb = EndLanding(junctionB, walk.DipRamp);
            var steps = new List<float>();
            if (b - lb > a + la + 0.05f) steps.AddRange(new[] { a + la - 0.005f, a + la + 0.005f, b - lb - 0.005f, b - lb + 0.005f });
            float Landing(float d) => d <= a + la || d >= b - lb ? PavementWidth - KerbBand : 0f;
            float keep = walk.Width;
            walk.Width = KerbBand;
            int n = walk.LayAlongRoad(walk, roads, road, side, null, from, toEnd, null, null, steps, Landing, Landing, piece: piece, minLength: min);
            walk.Width = keep;
            return n;
        }

        /// <summary>The grass of the verge and kerb-only streets, after LaySidewalks: a turf slab along each verge, on the paving
        /// between the kerb strip and the footway; and on a kerb-only street, grass from the back of the kerb stone falling to
        /// the ground at the plot line. Both stop on the runs' landings. Returns the meshes made.</summary>
        public int BuildStreetGrass(Node3D parent, RoadField roads, EditorSidewalk walk)
        {
            if (parent == null || roads == null || walk == null) return 0;
            var terr = Terrain.Active;
            var verge = new SurfaceTool(); verge.Begin(Mesh.PrimitiveType.Triangles);
            var apron = new SurfaceTool(); apron.Begin(Mesh.PrimitiveType.Triangles);
            int vergeRuns = 0, apronRuns = 0; float vergeLen = 0f, apronLen = 0f;
            // a quad, wound to face `want`
            void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 want)
            {
                foreach (var (p, q, s) in new[] { (a, b, c), (a, c, d) })
                {
                    bool keep = (s - p).Cross(q - p).Dot(want) >= 0f;     // clockwise seen from `want`: Godot's front (BuildMedians' Tri)
                    st.SetNormal(want.Normalized()); st.AddVertex(p);
                    st.SetNormal(want.Normalized()); st.AddVertex(keep ? q : s);
                    st.SetNormal(want.Normalized()); st.AddVertex(keep ? s : q);
                }
            }
            foreach (int r in Built)
            {
                var prof = ProfileOf(r);
                if (prof == StreetProfile.Pavement) continue;
                float half = roads.RoadHalfWidth(r);
                float off = Mathf.Max(0.1f, half + PavementGap);           // the kerb face: exactly what LayAlongRoad uses (LaySidewalks' RoadGap)
                // ...and a verge is paved along a parking bay cut through it, a little past each end: the cars' doors open onto it
                var stretches = new List<(int side, float a, float b)>();
                foreach (var run in WalkRuns(roads, r))
                {
                    var pieces = new List<(float a, float b)> { (run.A + EndLanding(run.JunctionA, walk.DipRamp), run.B - EndLanding(run.JunctionB, walk.DipRamp)) };
                    foreach (var bay in Bays)
                    {
                        if (bay.Road != r || bay.Side != run.Side) continue;
                        float c0 = Mathf.Min(bay.A, bay.Front0) - 1f, c1 = Mathf.Max(bay.B, bay.Front1) + 1f;
                        if (prof == StreetProfile.Verge && roads.EvaluateAlong(r, bay.A, out var vbp, out var vbt, snapTerrain: false))
                        {
                            var vbr = new Vector3(vbt.Z, 0f, -vbt.X).Normalized() * run.Side;
                            OfferShot("vb", vbp + vbr * off, new Vector3(vbt.X, 0f, vbt.Z).Normalized(), vbr);
                        }
                        var cut = new List<(float a, float b)>();
                        foreach (var (pa, pb) in pieces)
                        {
                            if (c1 <= pa || c0 >= pb) { cut.Add((pa, pb)); continue; }
                            if (c0 > pa) cut.Add((pa, c0));
                            if (c1 < pb) cut.Add((c1, pb));
                        }
                        pieces = cut;
                    }
                    foreach (var (pa, pb) in pieces) stretches.Add((run.Side, pa, pb));
                }
                foreach (var (runSide, a, b) in stretches)
                {
                    if (b - a < 1f) continue;
                    int n = Mathf.Max(1, Mathf.CeilToInt((b - a) / 1.5f));
                    Vector3? pIn = null, pOut = null, pInLo = null, pOutLo = null;
                    for (int i = 0; i <= n; i++)
                    {
                        float d = Mathf.Lerp(a, b, (float)i / n);
                        // ⚠ THE RIBBON'S OWN CALL (snapping as it snaps), so the turf lies on the paving it was laid over
                        if (!roads.EvaluateAlong(r, d, out var p, out var t)) { pIn = null; continue; }
                        var right = new Vector3(t.Z, 0f, -t.X).Normalized() * runSide;
                        Vector3 qIn, qOut, qInLo, qOutLo;
                        if (prof == StreetProfile.Verge)
                        {
                            float top = p.Y + PavementKerb;
                            qIn = new Vector3(p.X, top + VergeLift, p.Z) + right * (off + KerbBand);
                            qOut = new Vector3(p.X, top + VergeLift, p.Z) + right * (off + KerbBand + VergeWidth);
                            qInLo = qIn - Vector3.Up * (VergeLift + 0.01f); qOutLo = qOut - Vector3.Up * (VergeLift + 0.01f);
                        }
                        else
                        {
                            // from just under the back of the kerb stone to the ground at the plot line
                            float top = p.Y + PavementKerb - 0.01f;
                            qIn = new Vector3(p.X, top, p.Z) + right * (off + KerbBand);
                            var o = p + right * (off + PavementWidth);
                            float g = terr != null ? terr.SampleHeight(o.X, o.Z) : p.Y;
                            qOut = new Vector3(o.X, Mathf.Min(g + 0.03f, top), o.Z);
                            qInLo = qIn; qOutLo = qOut;
                        }
                        if (pIn is { } i0 && pOut is { } o0)
                        {
                            var st = prof == StreetProfile.Verge ? verge : apron;
                            Quad(st, i0, qIn, qOut, o0, Vector3.Up);
                            if (prof == StreetProfile.Verge && pInLo is { } il && pOutLo is { } ol)
                            {
                                // the turf's two long edges, so it reads as a slab from low down
                                Quad(st, il, qInLo, qIn, i0, -right);
                                Quad(st, ol, qOutLo, qOut, o0, right);
                            }
                        }
                        pIn = qIn; pOut = qOut; pInLo = qInLo; pOutLo = qOutLo;
                    }
                    if (prof == StreetProfile.Verge) { vergeRuns++; vergeLen += b - a; } else { apronRuns++; apronLen += b - a; }
                    if (b - a > 12f && roads.EvaluateAlong(r, (a + b) * 0.5f, out var mp, out var mt, snapTerrain: false))
                    {
                        var mr = new Vector3(mt.Z, 0f, -mt.X).Normalized() * runSide;
                        OfferShot(prof == StreetProfile.Verge ? "vg" : "ko", mp + mr * off, new Vector3(mt.X, 0f, mt.Z).Normalized(), mr);
                    }
                }
            }
            int made = 0;
            var grassMat = GroundGrassMaterial();
            foreach (var (st, name) in new[] { (verge, "Street_Verges"), (apron, "Street_KerbGrass") })
            {
                var mesh = st.Commit();
                if (mesh == null || mesh.GetSurfaceCount() == 0) continue;
                var mi = new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = grassMat };
                parent.AddChild(mi);
                made++;
            }
            Log.Print($"[towngrid] street grass: {vergeRuns} verge run(s), {vergeLen:0} m; {apronRuns} kerb-only run(s), {apronLen:0} m");
            return made;
        }

        // ---- street trees: in square planters along a paved street, and down the verges --------------------------------

        /// <summary>The share of paved side-street lines (and mains) planted with square planters along their kerbs, and of
        /// verge lines with trees down the verge. UG_TOWNPLANTERS=0|1 takes the planters out, or puts them on every paved street.</summary>
        public float PlanterShare = 0.55f, VergeTreeShare = 0.75f;
        public string PlanterForce = System.Environment.GetEnvironmentVariable("UG_TOWNPLANTERS");
        /// <summary>A planter: its side, its concrete rim's width and height over the paving, how far its kerb-side rim stands
        /// from the kerb face, and the trees' spacing along a street.</summary>
        public float PlanterSize = 1.7f, PlanterRim = 0.14f, PlanterRimHeight = 0.15f, PlanterKerbGap = 0.4f, StreetTreeSpacing = 13f;
        /// <summary>How near a lamp, hydrant, meter, sign or signal a tree may stand.</summary>
        public float StreetTreeClear = 2.4f;
        /// <summary>The trees' scale: in a verge as the boulevard's, smaller in a planter so the turf round the trunk shows.</summary>
        public float StreetTreeScale = 0.6f, PlanterTreeScale = 0.45f;
        /// <summary>The planters' concrete: a shade darker than the paving round it, so the square reads.</summary>
        public static readonly Color PlanterRimColour = new Color(0.62f, 0.60f, 0.57f);
        public int PlantersLaid, VergeTreesPlanted;
        /// <summary>Where the last PlantStreetTrees put each tree, and whether it stands in a planter.</summary>
        public readonly List<(Vector3 at, bool planter, int road)> StreetTrees = new();

        /// <summary>Plant the streets, after PlaceLights (whose props they keep clear of: `kerbside`). Planters are meshes under
        /// `parent`, the trees placed things. Returns the trees planted.</summary>
        public int PlantStreetTrees(Node3D parent, RoadField roads, EditorObjects objs, EditorSidewalk walk, IReadOnlyList<Node3D> kerbside)
        {
            StreetTrees.Clear(); PlantersLaid = VergeTreesPlanted = 0;
            if (parent == null || roads == null || objs == null || walk == null) return 0;
            var standing = new List<Vector3>();
            if (kerbside != null) foreach (var n in kerbside) if (n != null && GodotObject.IsInstanceValid(n)) standing.Add(n.GlobalPosition);
            var rims = new SurfaceTool(); rims.Begin(Mesh.PrimitiveType.Triangles);
            var turf = new SurfaceTool(); turf.Begin(Mesh.PrimitiveType.Triangles);
            void Box(SurfaceTool st, Vector3 c, Vector3 ax, Vector3 az, float hx, float hz, float y0, float y1, bool sides)
            {
                Vector3 P(float sx, float sz, float y) => new Vector3(c.X, y, c.Z) + ax * (sx * hx) + az * (sz * hz);
                void F(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Vector3 n)
                {
                    foreach (var (p, q, s) in new[] { (a, b, cc), (a, cc, d) })
                    {
                        bool keep = (s - p).Cross(q - p).Dot(n) >= 0f;
                        st.SetNormal(n); st.AddVertex(p); st.SetNormal(n); st.AddVertex(keep ? q : s); st.SetNormal(n); st.AddVertex(keep ? s : q);
                    }
                }
                F(P(-1, -1, y1), P(1, -1, y1), P(1, 1, y1), P(-1, 1, y1), Vector3.Up);
                if (!sides) return;
                F(P(-1, -1, y0), P(1, -1, y0), P(1, -1, y1), P(-1, -1, y1), -az);
                F(P(1, 1, y0), P(-1, 1, y0), P(-1, 1, y1), P(1, 1, y1), az);
                F(P(1, -1, y0), P(1, 1, y0), P(1, 1, y1), P(1, -1, y1), ax);
                F(P(-1, 1, y0), P(-1, -1, y0), P(-1, -1, y1), P(-1, 1, y1), -ax);
            }
            bool Crowded(Vector3 at) { foreach (var q in standing) if (Flat(q, at) < StreetTreeClear) return true; return false; }
            void Tree(Vector3 at, ulong h, bool planter, int road)
            {
                string tree = MedianTrees[(int)(Unit(h, 0) * MedianTrees.Length) % MedianTrees.Length];
                var turn = new Basis(Vector3.Up, Unit(h, 16) * Mathf.Tau);
                float sc = planter ? PlanterTreeScale : StreetTreeScale;
                var node = objs.Place(tree, SeatedTree(at, sc), turn.Scaled(Vector3.One * sc));
                if (node == null) return;
                StreetTrees.Add((at, planter, road));
                standing.Add(at);
            }
            foreach (int r in Built)
            {
                var prof = ProfileOf(r);
                if (prof == StreetProfile.KerbOnly) continue;
                bool planters = prof == StreetProfile.Pavement
                                && (PlanterForce == "1" || (PlanterForce != "0" && Unit(LineSeed(roads, r, 0x91A47EUL), 0) < PlanterShare));
                bool verge = prof == StreetProfile.Verge && Unit(LineSeed(roads, r, 0x7EE7EUL), 0) < VergeTreeShare;
                if (!planters && !verge) continue;
                float half = roads.RoadHalfWidth(r);
                float off = Mathf.Max(0.1f, half + PavementGap);
                ulong rh = Mix(((ulong)(uint)Seed << 32) ^ Mix((ulong)(uint)r ^ 0x57EE7UL));
                foreach (var run in WalkRuns(roads, r))
                {
                    float a = run.A + EndLanding(run.JunctionA, walk.DipRamp) + PlanterSize, b = run.B - EndLanding(run.JunctionB, walk.DipRamp) - PlanterSize;
                    if (b - a < 0.5f) continue;
                    int n = Mathf.Max(1, Mathf.FloorToInt((b - a) / StreetTreeSpacing) + 1);
                    float step = n > 1 ? (b - a) / (n - 1) : 0f;
                    for (int k = 0; k < n; k++)
                    {
                        float d = n > 1 ? a + k * step : (a + b) * 0.5f;
                        float reach = PlanterSize * 0.5f + 0.5f;
                        // never round a bay (its kerb stepped out, its meters) nor across a mouth or a lot's way in
                        if (NearBay(r, run.Side, d, reach + 1f)) continue;
                        if (MouthAt(r, run.Side, d - reach - 1f, d + reach + 1f) != null || EntranceAt(r, run.Side, d - reach - 1f, d + reach + 1f) != null) continue;
                        if (!roads.EvaluateAlong(r, d, out var p, out var t)) continue;
                        var along = new Vector3(t.X, 0f, t.Z).Normalized();
                        var right = new Vector3(t.Z, 0f, -t.X).Normalized() * run.Side;
                        float top = p.Y + PavementKerb;
                        ulong h = Mix(rh ^ ((ulong)(uint)(run.Side + 2) << 48) ^ ((ulong)(uint)k * 0x9E3779B9UL) ^ (ulong)(uint)Mathf.RoundToInt(d * 10f));
                        if (planters)
                        {
                            var c = new Vector3(p.X, top, p.Z) + right * (off + PlanterKerbGap + PlanterSize * 0.5f);
                            if (Crowded(c)) continue;
                            float hs = PlanterSize * 0.5f;
                            // the rim: four kerbs round the square, and the turf inside a little under their top
                            Box(rims, c + right * (hs - PlanterRim * 0.5f), along, right, hs, PlanterRim * 0.5f, top - 0.02f, top + PlanterRimHeight, true);
                            Box(rims, c - right * (hs - PlanterRim * 0.5f), along, right, hs, PlanterRim * 0.5f, top - 0.02f, top + PlanterRimHeight, true);
                            Box(rims, c + along * (hs - PlanterRim * 0.5f), along, right, PlanterRim * 0.5f, hs - PlanterRim, top - 0.02f, top + PlanterRimHeight, true);
                            Box(rims, c - along * (hs - PlanterRim * 0.5f), along, right, PlanterRim * 0.5f, hs - PlanterRim, top - 0.02f, top + PlanterRimHeight, true);
                            Box(turf, c, along, right, hs - PlanterRim, hs - PlanterRim, top, top + PlanterRimHeight * 0.5f, false);
                            PlantersLaid++;
                            OfferShot("tp", new Vector3(p.X, top, p.Z) + right * off, along, right);
                            Tree(new Vector3(c.X, top + PlanterRimHeight * 0.5f, c.Z), h, true, r);
                        }
                        else
                        {
                            var c = new Vector3(p.X, top + VergeLift, p.Z) + right * (off + KerbBand + VergeWidth * 0.5f);
                            if (Crowded(c)) continue;
                            VergeTreesPlanted++;
                            Tree(c, h, false, r);
                        }
                    }
                }
            }
            var rm = rims.Commit();
            if (rm != null && rm.GetSurfaceCount() > 0)
                parent.AddChild(new MeshInstance3D { Name = "Street_Planters", Mesh = rm,
                                                     MaterialOverride = new StandardMaterial3D { AlbedoColor = PlanterRimColour, Roughness = 1f } });
            var tm = turf.Commit();
            if (tm != null && tm.GetSurfaceCount() > 0)
                parent.AddChild(new MeshInstance3D { Name = "Street_Planters_Turf", Mesh = tm, MaterialOverride = GroundGrassMaterial() });
            Log.Print($"[towngrid] street trees: {PlantersLaid} in square planters, {VergeTreesPlanted} down verges ({StreetTrees.Count} placed)");
            return StreetTrees.Count;
        }
    }
}
