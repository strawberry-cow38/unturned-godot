using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>"Bridge": the cut deck unit tiled along a road spline, piered where the ground falls away.
    ///
    /// Master 2026-10-09: "idea is to turn these into splines" ... "yes they need to bend" ... "yes wire it".
    ///
    /// ⭐ THE CHECKS ARE AGAINST THINGS THAT FAIL QUIETLY. A mirrored run looks perfect on a straight line
    /// because dz = 0 (the fence tool shipped exactly that), a deck that terrain-snaps looks fine until it is
    /// over a valley, and a pier whose foot misses the ground is invisible from the road deck. Each of those
    /// gets an assertion that can only pass if the thing is actually right.</summary>
    public sealed class BridgeSplineTests : GameTest
    {
        public override string Name => "editor.bridge_spline";
        public override double TimeoutSimSeconds => 30;

        /// <summary>Worst GAP between consecutive deck units, and worst OVERLAP, in metres.
        ///
        /// ⚠⚠ SIGNED. An unsigned corner distance cannot tell daylight from solid-inside-solid, and since
        /// closing the outer corner necessarily buries the inner one twice as deep, it fails the fix and
        /// passes the bug -- which is exactly what happened on the rail. Project onto the leading tile's own
        /// forward axis: + is daylight, - is buried.</summary>
        static (float gap, float overlap) WorstJoint(List<Node3D> decks)
        {
            float g = 0f, o = 0f;
            const float half = EditorBridgeSpline.Pitch * 0.5f, hw = EditorBridgeSpline.HalfWidth;
            for (int i = 1; i < decks.Count; i++)
            {
                var a = decks[i - 1]; var b = decks[i];
                // the unit is CENTRED, so its ends are +/- half a pitch along its own length axis.
                var af = a.GlobalTransform.Basis.Y.Normalized();   // local +Y is the deck's length (Z-up prop)
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    var endC = a.GlobalTransform * new Vector3(sgn * hw, half, 0f);
                    var startC = b.GlobalTransform * new Vector3(sgn * hw, -half, 0f);
                    float along = af.Dot(startC - endC);
                    g = Mathf.Max(g, along); o = Mathf.Max(o, -along);
                }
            }
            return (g, o);
        }

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var cam = new Camera3D();
            World.AddChild(cam);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            var field = new RoadField();
            World.AddChild(field);
            // ⚠ WITHOUT THIS THERE IS NO MATERIAL 0, so BuildRoadNode never runs, no ribbon MeshInstance is
            // ever created, and both "the ribbon is hidden" and its control are assertions about nothing.
            field.DebugSetMaterialWidth(0, 8.5f);
            field.DebugSetMaterialDepth(0, 0.3f);
            var terr = Terrain.CreateFlat(1, 1, withCollider: false);
            World.AddChild(terr);
            field.Terr = terr;
            yield return Ticks(2);

            // ⚠ THE FLAT TERRAIN IS NOT AT ZERO -- CreateFlat sits at 30 m. I wrote the first version of this
            // test with the road hardcoded 30 m "up", which put it exactly ON the ground, so every pier check
            // passed vacuously with zero piers and the tool looked broken. Take the datum from the terrain.
            float g0 = terr.SampleHeight(50f, 0f);

            // The ribbon control is created HERE, before anything is bridged, so that "it is still visible at
            // the end" is a real control rather than a claim about a road whose ribbon may never have existed.
            int untouched = field.AddRoadFromPolyline(new List<Vector3>
                { new Vector3(0f, g0, 1200f), new Vector3(100f, g0, 1200f) }, 0);
            yield return Ticks(2);
            T.Check("fixture: a freshly drawn road's ribbon IS visible -- else the control below is empty",
                    field.RoadRibbonVisible(untouched));

            // ---- 0. THE CUT ASSETS ARE WHAT THE TOOL THINKS THEY ARE. Every constant below is a number read
            // off these meshes; if the obj were re-cut at a different length they would all be wrong at once.
            var deck = ContentProvider.ParseObj($"res://content/objects/{EditorBridgeSpline.DeckUnit}.obj");
            var pier = ContentProvider.ParseObj($"res://content/objects/{EditorBridgeSpline.PierUnit}.obj");
            T.Check("the deck unit and pier meshes load", deck != null && pier != null
                 && deck.GetSurfaceCount() > 0 && pier.GetSurfaceCount() > 0);
            if (deck == null || pier == null) yield break;
            var db = deck.GetAabb();
            T.Check($"the deck unit is {EditorBridgeSpline.Pitch:0.###} m along its length and centred "
                  + $"(Y {db.Position.Y:0.###}..{db.End.Y:0.###})",
                    Mathf.Abs((db.End.Y - db.Position.Y) - EditorBridgeSpline.Pitch) < 0.01f
                 && Mathf.Abs(db.Position.Y + db.End.Y) < 0.01f);
            T.Check($"...{EditorBridgeSpline.HalfWidth * 2f:0.#} m wide with its roadway at local Z=0 "
                  + $"(Z {db.Position.Z:0.##}..{db.End.Z:0.##})",
                    Mathf.Abs(db.Position.X + EditorBridgeSpline.HalfWidth) < 0.02f
                 && Mathf.Abs(db.Position.Z - EditorBridgeSpline.DeckSoffit) < 0.02f
                 && Mathf.Abs(db.End.Z - EditorBridgeSpline.DeckParapetTop) < 0.02f);
            var pb = pier.GetAabb();
            T.Check($"the pier spans {EditorBridgeSpline.PierSpan:0.#} m and its top sits UP INSIDE the deck "
                  + $"(Z {pb.Position.Z:0.##}..{pb.End.Z:0.##} vs soffit {EditorBridgeSpline.DeckSoffit:0.##})",
                    Mathf.Abs(pb.Position.Z - EditorBridgeSpline.PierBottom) < 0.02f
                 && Mathf.Abs(pb.End.Z - EditorBridgeSpline.PierTop) < 0.02f
                 && pb.End.Z > EditorBridgeSpline.DeckSoffit);

            // ---- 1. A STRAIGHT RUN HIGH OVER FLAT GROUND: the deck follows the ROAD, not the terrain.
            // ⚠⚠ This is the check that catches a terrain snap. The spline is drawn 30 m up over ground at 0;
            // if LayAlong let EvaluateAlong default to snapTerrain the whole bridge would sit on the dirt and
            // every other check here would still pass.
            var flat = new List<Vector3>();
            for (int k = 0; k <= 10; k++) flat.Add(new Vector3(k * 12f, g0 + 30f, 0f));
            int road = field.AddRoadFromPolyline(flat, 0, false, true);
            yield return Ticks(1);                       // the ribbon MeshInstance is built on the rebuild
            // FIXTURE, stated rather than assumed: the pier rule is (deckY + soffit) - ground, so if either
            // term is not what this test thinks it is the pier checks below go quietly vacuous.
            bool onSpline = field.EvaluateAlong(road, 50f, out var probe, out _, snapTerrain: false);
            T.Check($"fixture: the spline runs 30 m above the ground ({(onSpline ? probe.Y - g0 : -999f):0.##} m up "
                  + $"from {g0:0.#})", onSpline && Mathf.Abs(probe.Y - g0 - 30f) < 0.5f);
            var laid = new List<Node3D>();
            int n = EditorBridgeSpline.LayAlong(objs, terr, field, road, laid);
            T.Check($"laid {n} deck unit(s) over {field.RoadLength(road):0.#} m",
                    n >= 10);

            var decks = new List<Node3D>();
            foreach (var d in objs.PlacedOfNodes(EditorBridgeSpline.DeckUnit)) decks.Add(d);
            float minY = float.MaxValue;
            foreach (var d in decks) minY = Mathf.Min(minY, d.GlobalPosition.Y);
            T.Check($"the deck rides the road's own grade, not the ground ({minY - g0:0.#} m up over terrain at "
                  + $"{g0:0.#})", minY - g0 > 25f);

            // ⭐ AND IT IS NOT MIRRORED. A reflected basis has a negative determinant, and on a straight run
            // (dz = 0) a mirror is otherwise invisible -- the exact bug the fence tool shipped.
            bool handed = true, aligned = true;
            foreach (var d in decks)
            {
                var b = d.GlobalTransform.Basis;
                if (b.Determinant() <= 0f) handed = false;
                if (Mathf.Abs(b.Y.Normalized().Dot(Vector3.Right)) < 0.99f) aligned = false;   // run is +X
            }
            T.Check($"every unit is right-handed, not mirrored ({decks.Count} checked)", handed && decks.Count > 0);
            T.Check("...and its length axis lies along the run", aligned);

            // ---- 2. PIERS, AND ONLY WHERE THERE IS A DROP.
            var piers = new List<Node3D>();
            foreach (var p in objs.PlacedOfNodes(EditorBridgeSpline.PierUnit)) piers.Add(p);
            T.Check($"piered on retail's rhythm: {piers.Count} pier(s) for {n} units at one per "
                  + $"{EditorBridgeSpline.PierEveryUnits}", piers.Count == (n + EditorBridgeSpline.PierEveryUnits - 1) / EditorBridgeSpline.PierEveryUnits);

            // ⭐ THE FOOT HAS TO REACH THE GROUND, which is the whole reason the pier is scaled at all.
            float worstFoot = 0f, worstTop = 0f;
            foreach (var p in piers)
            {
                var t = p.GlobalTransform;
                float foot = (t * new Vector3(0f, 0f, EditorBridgeSpline.PierBottom)).Y;
                float top = (t * new Vector3(0f, 0f, EditorBridgeSpline.PierTop)).Y;
                worstFoot = Mathf.Max(worstFoot, Mathf.Abs(foot - terr.SampleHeight(t.Origin.X, t.Origin.Z)));
                float deckY = g0 + 30f + field.RoadSurfaceOffset(road);
                worstTop = Mathf.Max(worstTop, Mathf.Abs(top - (deckY + EditorBridgeSpline.PierTop)));
            }
            T.Check($"every pier's foot lands on the ground (worst {worstFoot * 100f:0.#} cm off)",
                    piers.Count > 0 && worstFoot < 0.05f);
            T.Check($"...and its top stays up inside the deck, retail's own joint (worst {worstTop * 100f:0.#} cm off)",
                    piers.Count > 0 && worstTop < 0.05f);

            // ⭐ CONTROL: the same road laid ON the ground must pier NOTHING. Without this, "piers appear"
            // would pass just as well on a tool that piers unconditionally.
            var ground = new List<Vector3>();
            for (int k = 0; k <= 10; k++) ground.Add(new Vector3(k * 12f, g0, 400f));
            int flatRoad = field.AddRoadFromPolyline(ground, 0, false, true);
            int before = 0;
            foreach (var _ in objs.PlacedOfNodes(EditorBridgeSpline.PierUnit)) before++;
            EditorBridgeSpline.LayAlong(objs, terr, field, flatRoad, null);
            int after = 0;
            foreach (var _ in objs.PlacedOfNodes(EditorBridgeSpline.PierUnit)) after++;
            T.Check($"control: a bridge laid ON the ground gets no piers ({after - before} added)",
                    after == before);

            // ---- 3. THE SEAM. Same chord wedge as the rail, far worse at this width: the deck is 17 m
            // across, so a joint on a 300 m bend opens HalfWidth*Pitch/R = 22 cm without the fix.
            var arc = new List<Vector3>();
            const float ArcR = 301f;                                  // tinyclaw's tightest real bridge curve
            for (int k = 0; k <= 24; k++)
            {
                float ang = k * 0.012f;
                arc.Add(new Vector3(ArcR * Mathf.Sin(ang), g0 + 30f, 800f + ArcR * (1f - Mathf.Cos(ang))));
            }
            int ctlRoad = field.AddRoadFromPolyline(arc, 0, false, true);
            var ctlDecks = new List<Node3D>();
            EditorBridgeSpline.DebugNoJointOverlap = true;
            int ctlN = EditorBridgeSpline.LayAlong(objs, terr, field, ctlRoad, ctlDecks);
            EditorBridgeSpline.DebugNoJointOverlap = false;
            ctlDecks.RemoveAll(x => (string)x.GetMeta("obj_name", "") != EditorBridgeSpline.DeckUnit);
            var ctl = WorstJoint(ctlDecks);
            T.Check($"control: WITHOUT the overlap, a {ArcR:0} m bend opens {ctl.gap * 100f:0.0} cm of daylight "
                  + $"over {ctlN} units", ctlN > 5 && ctl.gap > 0.05f);

            int fixRoad = field.AddRoadFromPolyline(arc, 0, false, true);
            var fixDecks = new List<Node3D>();
            int fixN = EditorBridgeSpline.LayAlong(objs, terr, field, fixRoad, fixDecks);
            fixDecks.RemoveAll(x => (string)x.GetMeta("obj_name", "") != EditorBridgeSpline.DeckUnit);
            var fx = WorstJoint(fixDecks);
            T.Check($"...and WITH it the same bend closes to {fx.gap * 1000f:0.#} mm over {fixN} units",
                    fixN > 5 && fx.gap < 0.01f);
            T.Check($"...paid for on the inside, buried not gapped ({fx.overlap * 100f:0.0} cm vs "
                  + $"{ctl.overlap * 100f:0.0} cm before)", fx.overlap > ctl.overlap + 0.01f);

            // ---- 4. THE PAINTED RIBBON IS RETIRED where the deck replaces it, and ONLY there.
            T.Check("the bridged road's painted ribbon is hidden", !field.RoadRibbonVisible(road));
            T.Check("control: the road never bridged STILL has its ribbon", field.RoadRibbonVisible(untouched));

            // ---- 5. NearestRoad finds a plain road where NearestTrack cannot -- the lookup the tool needs.
            bool anyFound = field.NearestRoad(new Vector3(50f, g0 + 30f, 0f), out int nr, out _);
            T.Check($"NearestRoad finds a plain (material 0) road -> {(anyFound ? nr.ToString() : "none")}",
                    anyFound && field.RoadMaterialOf(nr) == 0);
            T.Check("control: NearestTrack does NOT, since none of these are Tracks",
                    !field.NearestTrack(new Vector3(50f, g0 + 30f, 0f), out _, out _));
        }
    }
}
