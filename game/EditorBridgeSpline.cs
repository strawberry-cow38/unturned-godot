using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>"Bridge": tile the cut deck unit along a road spline and drop piers where the ground falls away.
    ///
    /// Master 2026-10-09: "turning the bridge line/bridge line caps into broken-apart props", then "idea is to
    /// turn these into splines", then -- asked whether bridges have to curve -- "yes they need to bend", then
    /// "yes wire it".
    ///
    /// ⭐⭐ A WHOLE SPAN CANNOT BEND, WHICH IS WHY THIS TILES A CUT UNIT. Bridge_Line_1 is 48 m long and 17 m
    /// wide, and two rigid tiles meeting at a turn part at their outer corners by HalfWidth*pitch/R -- 2.04 m
    /// at R=200 for a whole span. tinyclaw measured the real highway for it: 355 bridge stretches, tightest
    /// radius 301 m, median 1979 m, only 78 straight enough for an uncut span. So the deck is laid as the
    /// 7.835 m repeat unit (tools/cut_bridge_deck_unit.py) with SplineTiling.OverlapFor closing every joint.
    ///
    /// ⚠⚠ THE CONVENTIONS ARE THE OPPOSITE OF THE RAIL TOOL'S, and mixing them up is the single most likely
    /// way to break this. EditorRailSpline builds its basis DIRECTLY from the direction because astraclaw
    /// authored that unit +Y up with its length on +Z. These are RIPPED props: Z-up, length on local +Y, stood
    /// up with a baked ex=270 through EditorObjects.FromEuler -- and the yaw goes through
    /// ProcIsland.YawForDir, which takes PROC-frame coordinates where +Y is world −Z. Feeding it a world
    /// direction mirrors the run, and a straight run has dz = 0 so it looks perfect until the first curve.
    /// That exact bug shipped in the fence tool. [[reference_unturned_coord_znegate]]</summary>
    public partial class EditorBridgeSpline : Node3D
    {
        readonly Editor _editor;
        readonly Camera3D _cam;
        readonly EditorObjects _objects;
        readonly Terrain _terr;
        readonly RoadField _roads;

        public const string DeckUnit = "Bridge_Line_1_Deck_Unit";
        public const string PierUnit = "Bridge_Line_1_Pier";
        public const string DeckCap = "Bridge_Line_1_Deck_Cap";

        /// <summary>⭐ 7.8349 m, AND IT CAME OFF THE PAINT RATHER THAN THE GEOMETRY. tinyclaw and I had
        /// settled on a round 4 m from the curve budget alone; then the texture turned out to carry a DASHED
        /// lane marking -- 12 of its 256 columns vary down the image, in runs of 32 texels painted and 32
        /// blank. The roadway maps 392 texels over 48 m, so one dash period is 7.8349 m on the ground, and
        /// identical tiles each showing 4 m of that pattern do not reproduce it, they HALVE it.</summary>
        public const float Pitch = 7.8349f;

        /// <summary>Half the deck's width, measured off the mesh (X -8.5..+8.5). Sets how much each joint has
        /// to close by on a bend -- see SplineTiling.OverlapFor.</summary>
        public const float HalfWidth = 8.5f;

        /// <summary>The deck's own local Z extents, read off the cut unit: roadway at 0, soffit at -4.00,
        /// parapet top at +1.25. Named rather than inlined because the pier rule below is derived from the
        /// thickness and would otherwise look like three magic numbers.</summary>
        public const float DeckSoffit = -4.00f, DeckParapetTop = 1.25f;
        public const float DeckThickness = DeckParapetTop - DeckSoffit;

        /// <summary>The pier's own local Z extents, likewise: it reaches from -2.98 (up inside the deck, so
        /// the joint is never exposed) down to -52.98. 50 m of column.</summary>
        public const float PierTop = -2.98f, PierBottom = -52.98f;
        public const float PierSpan = PierTop - PierBottom;          // 50 m of column

        /// <summary>⭐ Don't pier a drop shallower than the deck is thick. Derived, not picked: below that the
        /// column is shorter than the thing it is holding up and reads as a lump under the deck rather than a
        /// support -- and an embankment, not a bridge, is the right answer there anyway.</summary>
        public const float MinPierDrop = DeckThickness;

        /// <summary>⭐ EVERY 6 UNITS, WHICH IS RETAIL'S OWN SPACING. Bridge_Line_1 ships as a 48 m span
        /// carrying exactly one pier pair, and 48 m is 6.126 of our 7.8349 m units -- so 6 units (47.0 m)
        /// reproduces the shipped rhythm to within a metre instead of inventing a number.</summary>
        public const int PierEveryUnits = 6;

        /// <summary>L1 seam: lay with the joint overlap disabled, i.e. the placement rule before the seam
        /// fix. The curve test needs a control that re-runs the real path with only the fix removed.</summary>
        public static bool DebugNoJointOverlap;

        bool _on;

        public bool Active => _on;
        public string ModeText => _on
            ? "BRIDGE · B = tile the deck along the road spline under the cursor, piers where it falls away · Shift+B = off"
            : "Shift+B = bridge";

        public EditorBridgeSpline(Editor editor, Camera3D cam, EditorObjects objects, Terrain terr, RoadField roads)
        {
            _editor = editor; _cam = cam; _objects = objects; _terr = terr; _roads = roads;
        }

        public void SetActive(bool on) { if (_on != on) _on = on; }

        public override void _UnhandledInput(InputEvent ev)
        {
            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.B } && Input.IsKeyPressed(Key.Shift))
            { _on = !_on; GetViewport().SetInputAsHandled(); return; }
            if (!_on || _roads == null) return;

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Z } && Input.IsKeyPressed(Key.Ctrl)) { _editor.Undo(); return; }

            if (ev is InputEventKey { Pressed: true, Echo: false, Keycode: Key.B } && !Input.IsKeyPressed(Key.Shift))
            {
                if (!RaycastTerrain(GetViewport().GetMousePosition(), out var at)) return;
                if (!_roads.NearestRoad(at, out int road, out _))
                { Log.Print("[editor-bridge] no road spline nearby -- draw one with the road tool first"); return; }
                var placed = new List<Node3D>();
                if (LayAlong(_objects, _terr, _roads, road, placed) == 0) return;
                _editor.PushUndo("lay bridge", () => _objects.RemovePlaced(placed));
                _editor.MarkDirty();
            }
        }

        /// <summary>Tile the deck along a road spline, piering it where the ground drops away. Returns the
        /// number of deck units laid.</summary>
        public static int LayAlong(EditorObjects objects, Terrain terr, RoadField roads, int road,
                                   List<Node3D> placed)
        {
            if (objects == null || roads == null) return 0;
            float total = roads.RoadLength(road);
            if (total < Pitch) { Log.Print($"[editor-bridge] {total:0.#} m is under one {Pitch:0.###} m deck unit -- nothing laid"); return 0; }

            // ⚠⚠ snapTerrain: false, AND THAT IS THE WHOLE POINT OF A BRIDGE. EvaluateAlong defaults to
            // dropping the sample onto the heightmap, which for a road crossing a valley returns the valley
            // floor -- the deck would follow the ground it is supposed to span and there would be nothing to
            // pier. The spline's own Y is the road's designed grade, which is exactly the deck line.
            float lift = roads.RoadSurfaceOffset(road);
            float tightest = float.PositiveInfinity, totalOverlap = 0f, deepest = 0f;
            int n = 0, piers = 0;
            float s = 0f;
            Vector3 firstMid = Vector3.Zero, firstDir = Vector3.Zero, lastMid = Vector3.Zero, lastDir = Vector3.Zero;

            while (s + Pitch <= total + 1e-3f)
            {
                if (!roads.EvaluateAlong(road, s, out var p0, out _, snapTerrain: false)) break;
                if (!roads.EvaluateAlong(road, s + Pitch, out var p1, out _, snapTerrain: false)) break;
                var span = p1 - p0;
                if (span.LengthSquared() < 1e-8f) break;
                var dir = span.Normalized();

                // ⚠ THE UNIT IS CENTRED ON ITS LENGTH (Y -3.92..+3.92), unlike the rail unit which starts at
                // its datum. Its root therefore belongs at the MIDDLE of the station it occupies; placing it
                // at the station start would shift the whole bridge half a unit off the spline.
                var mid = (p0 + p1) * 0.5f;
                mid.Y += lift;                       // the deck's roadway is its local Z=0, so sit it ON the road surface
                var basis = DeckBasis(dir);
                var d = objects.Place(DeckUnit, mid, basis);
                if (d != null) placed?.Add(d);
                if (n == 0) { firstMid = mid; firstDir = dir; }
                lastMid = mid; lastDir = dir;

                // A PIER PAIR on retail's rhythm, but only where there is actually a drop to span.
                if (terr != null && n % PierEveryUnits == 0)
                {
                    float ground = terr.SampleHeight(mid.X, mid.Z);
                    float drop = (mid.Y + DeckSoffit) - ground;
                    if (drop >= MinPierDrop)
                    {
                        deepest = Mathf.Max(deepest, drop);
                        // ⭐ STRETCH THE COLUMN TO THE GROUND, KEEPING RETAIL'S OWN JOINT. In the shipped prop
                        // the deck and the pier share an origin, so the pier's top at -2.98 sits 1.02 m UP
                        // INSIDE the deck (whose soffit is -4.00) and the joint is never visible. Solving for
                        // both ends preserves that: top at deckY + PierTop, foot on the ground.
                        //
                        //   top  = R + PierTop*k    = deckY + PierTop
                        //   foot = R + PierBottom*k = ground          ->  k = (deckY + PierTop - ground)/PierSpan
                        //
                        // ⚠ Scaling alone cannot do it. A Basis has no translation, so scaling about the prop
                        // origin drags the TOP down with the foot -- my first cut divided the drop by the
                        // column's 50 m length and left the joint hanging below the soffit on anything deep.
                        // The root has to move too, which is what R is.
                        //
                        // ⚠⚠ AND THE COLUMN IS SCALED ON ITS OWN AXIS, NOT Basis.Scaled. Godot's
                        // Basis.Scaled multiplies the basis ROWS, which scales in the PARENT frame -- on a
                        // graded deck that stretches world Y and the foot lands 24 m out. Scaling the Z
                        // COLUMN is the local-axis scale this wants.
                        //
                        // ⭐ A PIER ALSO STANDS UP, NOT ALONG THE GRADE. It carries the deck; it is not part
                        // of it. StandBasis is the deck's basis without the grade tilt, which is both
                        // physically right and what makes the foot arithmetic exact.
                        float k = (mid.Y + PierTop - ground) / PierSpan;
                        var up = StandBasis(dir);
                        var at = new Vector3(mid.X, ground - PierBottom * k, mid.Z);
                        var p = objects.Place(PierUnit, at, new Basis(up.X, up.Y, up.Z * k));
                        if (p != null) { placed?.Add(p); piers++; }
                    }
                }

                n++;

                float turn = 0f;
                if (roads.EvaluateAlong(road, s + Pitch, out var q0, out _, snapTerrain: false)
                    && roads.EvaluateAlong(road, s + Pitch * 2f, out var q1, out _, snapTerrain: false))
                {
                    var nd = q1 - q0;
                    if (nd.LengthSquared() > 1e-8f) turn = Mathf.Abs(dir.AngleTo(nd.Normalized()));
                }
                if (turn > 1e-4f) tightest = Mathf.Min(tightest, Pitch / turn);

                float overlap = DebugNoJointOverlap ? 0f : SplineTiling.OverlapFor(HalfWidth, Pitch, turn);
                totalOverlap += overlap;
                s += Pitch - overlap;
            }

            if (n == 0) { Log.Print($"[editor-bridge] {total:0.#} m laid nothing -- the spline could not be walked"); return 0; }

            // ⭐ CLOSE BOTH ENDS. Master, on the first bridge render: "close up the ends." Retail's own
            // Bridge_Line_1 is an open-ended TUBE -- zero faces at either end plane -- because retail closes a
            // run with its Cap props rather than capping each span, so a tiled run of them ends in a hole you
            // see straight into. The cap is the deck's own section, ear-clipped (tools/cut_bridge_deck_unit.py)
            // and area-checked against the section, so it cannot spill or leave a gap.
            //
            // ⚠ ONE AT EACH END, FACING OUT. The cap's normal is its local +Y, which the stand-up maps onto
            // the run direction -- so the far end takes the run's own basis and the near end takes the
            // REVERSED direction, which is a 180 degree turn about the vertical rather than a mirror.
            var capEnd = objects.Place(DeckCap, lastMid + lastDir * (Pitch * 0.5f), DeckBasis(lastDir));
            if (capEnd != null) placed?.Add(capEnd);
            var capStart = objects.Place(DeckCap, firstMid - firstDir * (Pitch * 0.5f), DeckBasis(-firstDir));
            if (capStart != null) placed?.Add(capStart);

            // ⭐ AND RETIRE THE PAINTED STRIP, same reason the rail does: the deck carries its own roadway, and
            // leaving the ribbon drawn puts the old paint inside the new deck. The road keeps its material, so
            // anything that looks roads up by type still finds it.
            roads.SetRoadRibbonVisible(road, false);

            Log.Print($"[editor-bridge] road {road}: {total:0.#} m -> {n} deck unit(s) at {Pitch:0.###} m + {piers} pier(s) + 2 end cap(s)"
                    + (deepest > 0f ? $", deepest {deepest:0.#} m" : ", none deep enough to pier")
                    + ", painted ribbon hidden"
                    + (float.IsPositiveInfinity(tightest) ? ", straight (no joint overlap needed)"
                       : $", tightest bend ~{tightest:0} m, joints closed by {totalOverlap / n * 100f:0.0} cm avg"));
            return n;
        }

        /// <summary>Stand the Z-up deck on the run and PITCH IT TO THE GRADE.
        ///
        /// ⚠ Not SeatedBasis. The fence tilts its posts to the terrain normal because a post stands on the
        /// ground; a bridge deck explicitly does not -- it follows the road's own grade across whatever the
        /// ground is doing underneath, which is the entire reason it is a bridge.</summary>
        public static Basis DeckBasis(Vector3 dir)
        {
            var stand = StandBasis(dir);
            var flat = new Vector3(dir.X, 0f, dir.Z);
            if (flat.LengthSquared() < 1e-8f) return stand;
            flat = flat.Normalized();
            float grade = Mathf.Asin(Mathf.Clamp(dir.Normalized().Y, -1f, 1f));
            if (Mathf.Abs(grade) < 1e-5f) return stand;
            var lat = Vector3.Up.Cross(flat);
            return lat.LengthSquared() < 1e-8f ? stand : new Basis(lat.Normalized(), -grade) * stand;
        }

        /// <summary>The Z-up stand-up and yaw alone, with no grade tilt -- what a vertical member wants.</summary>
        public static Basis StandBasis(Vector3 dir)
        {
            var flat = new Vector3(dir.X, 0f, dir.Z);
            if (flat.LengthSquared() < 1e-8f) return EditorObjects.FromEuler(270f, 0f, 0f);
            flat = flat.Normalized();
            return EditorObjects.FromEuler(270f, ProcIsland.YawForDir(flat.X, -flat.Z), 0f);
        }

        bool RaycastTerrain(Vector2 screen, out Vector3 point)
        {
            point = Vector3.Zero;
            var from = _cam.ProjectRayOrigin(screen);
            var to = from + _cam.ProjectRayNormal(screen) * 4000f;
            var q = PhysicsRayQueryParameters3D.Create(from, to, 1u << 0);
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            point = (Vector3)hit["position"];
            return true;
        }
    }
}
