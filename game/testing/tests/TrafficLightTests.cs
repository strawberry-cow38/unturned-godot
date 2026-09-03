using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // Traffic_Light_0's red/amber/green lenses are already modelled -- three coplanar quads on each of the two signal
    // heads -- and were drawn with the same material as the yellow housing. ObjMesh.SplitTrafficLenses carves them
    // onto three surfaces so one can be lit at a time.
    //
    // This is a THREE-way split where the streetlight's was binary, and the extra ways to get it wrong are all
    // invisible in a screenshot of a red light:
    //
    //  1. Mixing two colours into one surface. A predicate that is slightly too wide swallows its neighbour's texel
    //     and the signal lights two aspects at once -- which reads as "red works" right up until it shows red+amber.
    //     So this asserts the partition four ways: the three lens surfaces plus the body sum to the source, and each
    //     lens surface samples ONLY its own palette texel.
    //  2. Losing a head. Both heads' red lenses share a palette texel, so this colour-only split must carry FOUR
    //     triangles -- a quad from each head -- not two. (Separating the heads is a SECOND cut on the mast axis, in
    //     props.traffic_light_head_split; this stage must not do it by accident.)
    //  3. Getting the colours in the wrong ORDER. Nothing in a partition check notices red and green swapped, so the
    //     texels are asserted against the values actually sampled out of the PNG.
    public sealed class TrafficLightLensSplitTests : GameTest
    {
        public override string Name => "props.traffic_light_lens_split";

        // The real 4x2 palette, read off Traffic_Light_0_tex.png. After ObjMesh's V-flip, Godot v<0.5 is the top
        // image row. Red (2,0) and amber (3,0) sit on that row; green is (2,1) below them; the housing is the
        // saturated yellow at (1,1). Asserted rather than assumed -- these are what make the split MEAN something.
        static readonly (float u0, float u1, float v0, float v1)[] Cells =
        {
            (0.50f, 0.75f, 0.0f, 0.5f),   // red
            (0.75f, 1.01f, 0.0f, 0.5f),   // amber
            (0.50f, 1.01f, 0.5f, 1.0f),   // green
        };
        static readonly string[] ColourName = { "red", "amber", "green" };

        static int TriCount(ArrayMesh m)
            => m == null ? 0 : m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length / 3;

        // Triangles whose every corner samples the given palette cell.
        static int TrisInCell(ArrayMesh m, (float u0, float u1, float v0, float v1) c)
        {
            if (m == null) return 0;
            var u = m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            int n = 0;
            for (int i = 0; i + 2 < u.Length; i += 3)
            {
                bool all = true;
                for (int k = 0; k < 3; k++)
                {
                    var t = u[i + k];
                    if (t.X < c.u0 || t.X >= c.u1 || t.Y < c.v0 || t.Y >= c.v1) { all = false; break; }
                }
                if (all) n++;
            }
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var src = ObjMesh.Load(dir + "Traffic_Light_0.obj");
            T.Check("the traffic light prop mesh loads", src != null);
            if (src == null) yield break;

            var parts = ObjMesh.SplitTrafficLenses(src);
            T.Check("the split yields red/amber/green/body", parts != null && parts.Length == 4);
            if (parts == null || parts.Length != 4) yield break;
            T.Check("the body survives the split", parts[3] != null);
            if (parts[3] == null) yield break;

            // (1) a PARTITION over all four surfaces -- nothing duplicated, nothing dropped.
            int srcTris = TriCount(src);
            int sum = 0;
            for (int i = 0; i < 4; i++) sum += TriCount(parts[i]);
            T.Check($"the four surfaces account for every source triangle ({sum} = {srcTris})", sum == srcTris);

            // (2) each lens surface carries its OWN texel and nothing else -- the check that catches a predicate
            //     wide enough to swallow the neighbouring aspect.
            for (int i = 0; i < 3; i++)
            {
                int own = TrisInCell(parts[i], Cells[i]);
                int total = TriCount(parts[i]);
                T.Check($"the {ColourName[i]} surface exists and is non-empty ({total} tri)", total > 0);
                T.Check($"...every {ColourName[i]} triangle samples the {ColourName[i]} texel ({own}/{total})", own == total);
                for (int j = 0; j < 3; j++)
                {
                    if (i == j) continue;
                    T.Check($"...and none of it samples the {ColourName[j]} texel", TrisInCell(parts[i], Cells[j]) == 0);
                }
            }

            // (3) BOTH HEADS on one surface. The mast carries two signal heads; each colour is a quad on each head,
            //     so four triangles. Two would mean the split ran per head and the heads can disagree.
            for (int i = 0; i < 3; i++)
                T.Check($"the {ColourName[i]} surface spans both signal heads (4 tri, got {TriCount(parts[i])})", TriCount(parts[i]) == 4);

            // (4) no lens geometry stranded in the body -- it would render unlit underneath the emissive copy.
            for (int i = 0; i < 3; i++)
                T.Check($"no {ColourName[i]} geometry is left behind in the body", TrisInCell(parts[3], Cells[i]) == 0);

            // (5) cached: 21 placements share one source mesh, so re-splitting per placement rebuilds 21 copies.
            var again = ObjMesh.SplitTrafficLenses(src);
            T.Check("splitting the same mesh twice reuses the result",
                ReferenceEquals(again[0], parts[0]) && ReferenceEquals(again[3], parts[3]));
        }
    }

    // PER-HEAD split. A mast carries two signal heads and strawberry wants them on independent timers, which a UV
    // split alone cannot deliver: both heads' lenses of a colour share one palette texel, so they land on the same
    // surface. ObjMesh cuts again on the gap along the mast axis.
    //
    // The failure here is quiet and asymmetric -- a mis-cut gives one head four lenses and the other two, so ONE head
    // works perfectly and the other has an aspect that never lights. Whichever head you happen to look at decides
    // whether you notice. So this asserts the split is even and that the heads are physically separated.
    public sealed class TrafficLightHeadSplitTests : GameTest
    {
        public override string Name => "props.traffic_light_head_split";

        static int Tris(ArrayMesh m)
            => m == null ? 0 : m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length / 3;

        public override IEnumerable<Step> Run()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var src = ObjMesh.Load(dir + "Traffic_Light_0.obj");
            T.Check("the traffic light prop mesh loads", src != null);
            if (src == null) yield break;

            var (heads, body) = ObjMesh.SplitTrafficLensesPerHead(src);
            T.Check("the per-head split yields heads", heads != null);
            T.Check("...and a body", body != null);
            if (heads == null || body == null) yield break;
            T.Check($"Traffic_Light_0 has TWO signal heads ({heads.Length})", heads.Length == 2);
            if (heads.Length != 2) yield break;

            // Each head gets one 2-tri quad per aspect. Four would mean the axis cut did nothing and both heads are
            // still fused -- which looks completely correct until you notice both heads always agree.
            for (int h = 0; h < 2; h++)
                for (int c = 0; c < 3; c++)
                    T.Check($"head {h} aspect {c} is one quad ({Tris(heads[h][c])} tri)", Tris(heads[h][c]) == 2);

            // Total conservation against the colour-only split: nothing gained or dropped in the second cut.
            var flat = ObjMesh.SplitTrafficLenses(src);
            for (int c = 0; c < 3; c++)
                T.Check($"aspect {c} conserves triangles across the head cut ({Tris(heads[0][c])}+{Tris(heads[1][c])} = {Tris(flat[c])})",
                    Tris(heads[0][c]) + Tris(heads[1][c]) == Tris(flat[c]));

            // The heads must be SEPARATED in space, or "two heads" is a bookkeeping fiction and both TrafficLights
            // would put their emitters in the same place.
            float c0 = heads[0][0].GetAabb().GetCenter().Y, c1 = heads[1][0].GetAabb().GetCenter().Y;
            T.Check($"the two heads sit apart along the mast ({Mathf.Abs(c1 - c0):F2}m)", Mathf.Abs(c1 - c0) > 1f);
            // ...and ordered low-to-high, which is what makes the head index stable across a reload.
            T.Check("head 0 is the lower one", c0 < c1);

            // Cached: 21 placements share one source mesh.
            var again = ObjMesh.SplitTrafficLensesPerHead(src);
            T.Check("splitting the same mesh twice reuses the result", ReferenceEquals(again.Heads, heads));
        }
    }

    // The side-road flags are DATA matched by POSITION, and the failure mode is silent: a key mismatch flags nothing,
    // every junction flashes amber, and that is indistinguishable from the file being absent. It shipped that way --
    // placements.txt stores raw Unity coordinates while WorldBuilder negates Z for Godot, so the lookup matched 0 of
    // 21 and only a placement-count print caught it.
    //
    // So this asserts against the REAL files using the SAME key transform WorldBuilder uses. A change that
    // reintroduces the bug drops the intersection to zero and fails here, rather than in a screenshot nobody takes at
    // the exact moment of a blackout.
    //
    // The file now ships EMPTY -- strawberry checked the geometric guess against the map and a lot of it was wrong, so
    // the default is every signal blinking amber and a human assigns the rest later. That would normally gut this
    // test: with nothing flagged, "every flag matches a placement" is vacuously true and the regression guard is gone.
    // So the COMMENTED seed coordinates are validated too. They are real placement coordinates kept as a starting
    // point to uncomment, which means they keep the Z-convention teeth even while nothing is active.
    public sealed class TrafficLightSideRoadDataTests : GameTest
    {
        public override string Name => "props.traffic_light_side_road_data";

        public override IEnumerable<Step> Run()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            const string Guid = "42229938f39b4ccd9f0228c4d0ef972c";   // Traffic_Light_0, from guid_mesh.txt

            // Each placement is turned into a key the way WORLDBUILDER does it: build the same gpos it builds
            // (px, py, -pz) and hand it to WorldBuilder.SideRoadKey. Re-deriving the transform here instead would
            // produce a test that agrees with itself and passed happily against the shipped bug.
            var placed = new HashSet<(int, int)>();
            foreach (var line in System.IO.File.ReadLines(dir + "placements.txt"))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4 || p[0] != Guid) continue;
                if (float.TryParse(p[1], out var x) && float.TryParse(p[2], out var y) && float.TryParse(p[3], out var z))
                    placed.Add(WorldBuilder.SideRoadKey(new Vector3(x, y, -z)));   // -z: exactly what PlaceObject does
            }
            T.Check($"placements.txt carries the 21 traffic signals ({placed.Count})", placed.Count == 21);

            var flagged = new HashSet<(int, int)>();     // ACTIVE flags -- signals that blink red
            var seeded = new HashSet<(int, int)>();      // COMMENTED coordinates -- the guess kept for editing
            string sideFile = dir + "traffic_side_roads.txt";
            T.Check("the side-road data file ships", System.IO.File.Exists(sideFile));
            if (!System.IO.File.Exists(sideFile)) yield break;
            foreach (var raw in System.IO.File.ReadLines(sideFile))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                bool commented = line[0] == '#';
                if (commented) line = line.TrimStart('#').Trim();
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                // A prose comment must not be mistaken for data -- only take lines that are exactly two numbers.
                if (p.Length != 2 || !float.TryParse(p[0], out var sx) || !float.TryParse(p[1], out var sz)) continue;
                (commented ? seeded : flagged).Add((Mathf.RoundToInt(sx * 10f), Mathf.RoundToInt(sz * 10f)));
            }

            // The DEFAULT strawberry asked for: nothing flagged, every signal slow-blinks amber. Pinned so that
            // re-introducing flags is a deliberate edit rather than something that drifts back in.
            T.Check($"ships with no signal flagged -- all-amber blink is the default ({flagged.Count} active)",
                flagged.Count == 0);

            // Any ACTIVE flag must name a real placement. Zero of them is fine; a typo'd one is not.
            int matched = 0;
            foreach (var k in flagged) if (placed.Contains(k)) matched++;
            T.Check($"every active flag matches a real placement ({matched}/{flagged.Count})", matched == flagged.Count);

            // THE TEETH, and the reason the commented block is parsed at all: these are real coordinates waiting to be
            // uncommented, so they exercise the same key transform. Reverting the Z negation drops this to 0/9.
            int seedMatched = 0;
            foreach (var k in seeded) if (placed.Contains(k)) seedMatched++;
            T.Check($"the commented seed coordinates are real placements ({seedMatched}/{seeded.Count})",
                seeded.Count > 0 && seedMatched == seeded.Count);
            // ...and they are a proper subset, or uncommenting the block would flash every junction red.
            T.Check($"the seed is a proper subset of the signals ({seeded.Count} of {placed.Count})",
                seeded.Count > 0 && seeded.Count < placed.Count);
            yield break;
        }
    }

    // The signal's own logic: a dumb per-prop timer (strawberry's explicit call -- no junction sync), a backup flash
    // when the grid dies, and a battery that eventually gives out.
    public sealed class TrafficLightCycleTests : GameTest
    {
        public override string Name => "props.traffic_light_cycle";

        static MeshInstance3D Lens() => new MeshInstance3D
        {
            Mesh = new BoxMesh(),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.6f, 0.6f, 0.6f) },   // the prop's own = unlit
        };

        public override IEnumerable<Step> Run()
        {
            // ---- pure phase logic, swept rather than sampled -----------------------------------------------------
            // A signal that skips amber, or shows two aspects in one instant, is a bug you cannot see by eye in a
            // 13s cycle. Sweep the whole thing at fine resolution and assert exactly one aspect at every step.
            float cyc = TrafficLight.CycleSec;
            T.Check($"the cycle is green+amber+allred ({cyc}s)",
                Mathf.IsEqualApprox(cyc, TrafficLight.GreenSec + TrafficLight.AmberSec + TrafficLight.AllRedSec));

            var seen = new HashSet<TrafficLight.Phase>();
            bool everyStepHasExactlyOne = true;
            for (float t = 0f; t < cyc * 3f; t += 0.05f)
            {
                var p = TrafficLight.PhaseAt(t, 0f);
                seen.Add(p);
                if (TrafficLight.LensIndexFor(p) < 0) everyStepHasExactlyOne = false;
            }
            T.Check("a powered signal always shows exactly one aspect, never dark", everyStepHasExactlyOne);
            T.Check("the cycle visits green, amber AND red", seen.Count == 3
                && seen.Contains(TrafficLight.Phase.Green) && seen.Contains(TrafficLight.Phase.Amber)
                && seen.Contains(TrafficLight.Phase.Red));

            // Amber must be entered from green and left into red -- an ordering a phase histogram cannot see.
            var order = new List<TrafficLight.Phase>();
            for (float t = 0f; t < cyc; t += 0.02f)
            {
                var p = TrafficLight.PhaseAt(t, 0f);
                if (order.Count == 0 || order[order.Count - 1] != p) order.Add(p);
            }
            T.Check($"the order is green -> amber -> red ({string.Join(">", order)})",
                order.Count == 3 && order[0] == TrafficLight.Phase.Green
                && order[1] == TrafficLight.Phase.Amber && order[2] == TrafficLight.Phase.Red);

            // THE REGRESSION. The flash phases have to map back onto the ordinary lenses. When that mapping lived in
            // three hand-written copies, two of them disagreed: FlashRed lit the red lens while the mast glow fell
            // through a ternary chain to GREEN, and the blink's dark beat left the glow burning.
            T.Check("FlashRed lights the RED lens", TrafficLight.LensIndexFor(TrafficLight.Phase.FlashRed) == 0);
            T.Check("FlashAmber lights the AMBER lens", TrafficLight.LensIndexFor(TrafficLight.Phase.FlashAmber) == 1);
            T.Check("Off lights nothing at all", TrafficLight.LensIndexFor(TrafficLight.Phase.Off) < 0);

            // Dumb per-prop timers: two signals at different positions must NOT march in step. If Make ever stopped
            // hashing position, every signal in town would flip together and the whole point would be lost.
            var a = TrafficLight.Make(new Vector3(10f, 0f, 20f), 0f, Lens(), Lens(), Lens());
            var b = TrafficLight.Make(new Vector3(-40f, 0f, 133f), 90f, Lens(), Lens(), Lens());
            T.Check($"two signals get different phase offsets ({a.OffsetForTest:F2} vs {b.OffsetForTest:F2})",
                !Mathf.IsEqualApprox(a.OffsetForTest, b.OffsetForTest));
            // The offsets differing is not enough on its own -- they must produce a genuinely different aspect at
            // some point in the cycle, which is what "dumb independent timers" actually buys.
            bool everDisagree = false;
            for (float t = 0f; t < cyc; t += 0.1f)
                if (TrafficLight.PhaseAt(t, a.OffsetForTest) != TrafficLight.PhaseAt(t, b.OffsetForTest)) { everDisagree = true; break; }
            T.Check("...enough to actually show different aspects during the cycle", everDisagree);
            var again = TrafficLight.Make(new Vector3(10f, 0f, 20f), 0f, Lens(), Lens(), Lens());
            T.Check("...and the offset is deterministic from position, so peers agree",
                Mathf.IsEqualApprox(a.OffsetForTest, again.OffsetForTest));
            a.QueueFree(); b.QueueFree(); again.QueueFree();

            // ---- live node, with a clock we own ------------------------------------------------------------------
            // ExternalTime stops the cycle free-running so Day/Time are ours to set; TrafficLight derives its clock
            // from them, which is also what makes the phase agree across peers with nothing replicated.
            var cycle = new DayNightCycle { ExternalTime = true, VisualsEnabled = false, DayLength = 120f, Day = 0, Time = 0f };
            World.AddChild(cycle);
            var red = Lens(); var amber = Lens(); var green = Lens();
            var host = new Node3D();
            World.AddChild(host);
            host.AddChild(red); host.AddChild(amber); host.AddChild(green);
            var tl = TrafficLight.Make(new Vector3(0f, 0f, 0f), 0f, red, amber, green);
            World.AddChild(tl);
            yield return Ticks(2);

            // The lens is real prop geometry: it must keep RENDERING when dark or the head has holes punched in it.
            T.Check("all three lenses stay in the scene regardless of aspect",
                red.Visible && amber.Visible && green.Visible);

            // Grid down -> backup flash, NOT dark. A main-road mast flashes amber.
            tl.SetPowered(false);
            yield return Ticks(2);
            // BREATHING, not blinking (strawberry: "more of a slow fade in/out breathing effect"). The phase stays on
            // the amber aspect the whole time and the LEVEL moves -- so the old assertions, which watched for a
            // Phase.Off dark beat, would now fail against correct code. Sample the level across a full flash period
            // and assert three things a square wave could not satisfy: it reaches the top, reaches the bottom, and
            // passes through the middle. A hard on/off never lands mid-range.
            bool sawAmberLit = false; float lo = 2f, hi = -1f; int midSamples = 0, total = 0;
            for (int i = 0; i < 150; i++)               // ~1.5 flash periods at FlashHz -- enough to see a whole breath
            {
                cycle.Time += 0.04f / cycle.DayLength;   // 40ms of game clock per step
                yield return Ticks(1);
                if (tl.CurrentPhase == TrafficLight.Phase.FlashAmber) sawAmberLit = true;
                float l = tl.LevelForTest;
                lo = Mathf.Min(lo, l); hi = Mathf.Max(hi, l); total++;
                if (l > 0.2f && l < 0.8f) midSamples++;
            }
            T.Check("an unpowered main-road signal shows AMBER", sawAmberLit);
            T.Check($"...breathing up to full brightness ({hi:F2})", hi > 0.9f);
            T.Check($"...and down to dark ({lo:F2})", lo < 0.1f);
            T.Check($"...FADING through the mid-range, not snapping ({midSamples}/{total} samples mid)", midSamples > total / 8);
            T.Check("...never showing green while unpowered", tl.CurrentPhase != TrafficLight.Phase.Green);

            // A side-road mast flashes RED instead -- all-amber at a 4-way is a collision, not a style.
            var red2 = Lens(); var amber2 = Lens(); var green2 = Lens();
            var host2 = new Node3D();
            World.AddChild(host2);
            host2.AddChild(red2); host2.AddChild(amber2); host2.AddChild(green2);
            var side = TrafficLight.Make(new Vector3(4f, 0f, 4f), 90f, red2, amber2, green2);
            side.SideRoad = true;
            World.AddChild(side);
            yield return Ticks(2);
            side.SetPowered(false);
            bool sawRedFlash = false, sawAmberOnSide = false;
            for (int i = 0; i < 120; i++)
            {
                cycle.Time += 0.05f / cycle.DayLength;
                yield return Ticks(1);
                if (side.CurrentPhase == TrafficLight.Phase.FlashRed) sawRedFlash = true;
                if (side.CurrentPhase == TrafficLight.Phase.FlashAmber) sawAmberOnSide = true;
            }
            T.Check("an unpowered SIDE-road signal flashes RED", sawRedFlash);
            T.Check("...and never amber", !sawAmberOnSide);

            // The battery is finite (strawberry: "a couple in game days"), then the junction goes properly dark.
            T.Check("the battery is not dead yet", !tl.BatteryDeadForTest);
            cycle.Day += Mathf.CeilToInt(TrafficLight.BatteryDays) + 1;
            // WAIT ON THE STATE, NOT A FRAME COUNT. A clock jump has no event to hook -- unlike SetPowered, which
            // re-evaluates the aspect itself -- so the drain is only noticed on the signal's next HUB tick. The old
            // `Ticks(3)` spanned one of those only while a frame took longer than a hub period, i.e. it passed
            // because this box is slow, and would start flaking the moment one got faster or the cadence changed
            // again (it has, twice: per-frame -> 10 Hz -> 30 Hz). Every real bug this suite's history records was a
            // fixed-time constant in a TEST, so wait for the condition and assert the consequences separately --
            // the checks below still have teeth, because "battery dead" is not "phase Off and dark".
            // Wait on the LAMPS, not on a frame count and not on the battery flag. BatteryDeadForTest is a pure
            // clock comparison (ClockSeconds() >= _batteryDeadAt), so it is already true the instant the day is
            // jumped -- waiting on it proves nothing and races the hub. DarkForTest is LitIndex < 0, which only
            // changes when the signal next TICKS, and that is the thing with the latency. Until fails the test
            // itself if the lamps never go out, so darkness and the phase machine stay two separate assertions:
            // a junction that is dark while its phase still says Green is exactly the bug worth catching.
            yield return Until(() => tl.DarkForTest);
            T.Check("...and the phase machine agrees it is Off, not just the lamps",
                tl.CurrentPhase == TrafficLight.Phase.Off);
            T.Check("...and the lenses are still present, not deleted", red.Visible && amber.Visible && green.Visible);

            // Power back = recharged, and straight back into the cycle rather than staying dark.
            tl.SetPowered(true);
            yield return Ticks(3);
            T.Check("restoring power recharges the battery", !tl.BatteryDeadForTest);
            T.Check("...and the signal resumes its cycle", TrafficLight.LensIndexFor(tl.CurrentPhase) >= 0);

            // BROWNOUT: a signal must RIDE IT THROUGH, unlike every other grid consumer (strawberry: "they are on a
            // battery for a reason"). The cabinet's BBS sits between the grid and the lamps, so a sag never reaches
            // them -- a stuttering junction would be advertising that its battery doesn't work. Driven through
            // DayNightCycle.TriggerGlobalBrownout, because the thing that would break this is someone adding a
            // traffic_lights line to that sweep alongside the streetlights and lamps.
            cycle.TriggerGlobalBrownout(0.6f);
            bool wentDark = false;
            for (int i = 0; i < 90; i++)
            {
                cycle.Time += 0.005f / cycle.DayLength;   // small steps: stay inside one aspect so a phase CHANGE isn't mistaken for a flicker
                yield return Ticks(1);
                if (tl.CurrentPhase == TrafficLight.Phase.Off) wentDark = true;
            }
            T.Check("a brownout does NOT make a signal stutter -- the cabinet battery rides it through", !wentDark);
            T.Check("...and it is still showing a real aspect afterwards", TrafficLight.LensIndexFor(tl.CurrentPhase) >= 0);

            // A SMASHED signal is dark even with the grid up -- no power and no fixture are different states.
            tl.SetBroken(true);
            yield return Ticks(2);
            T.Check("a smashed signal is dark despite grid power", tl.DarkForTest);

        }
    }

    // toggleBbat OFF. Its own test rather than a tail on the cycle one: each in-engine test gets a 15s watchdog and
    // these are all frame-yield loops, so piling another 120 frames onto an already-long test times it out on a
    // machine having a slow minute -- a failure that says nothing about the code.
    public sealed class TrafficLightNoBatteryTests : GameTest
    {
        public override string Name => "props.traffic_light_no_battery";

        static MeshInstance3D Lens() => new MeshInstance3D
        {
            Mesh = new BoxMesh(),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.6f, 0.6f, 0.6f) },
        };

        public override IEnumerable<Step> Run()
        {
            // BatteryBackup is a STATIC. Leaking it off would silently change every later test in the run and the
            // failure would land somewhere unrelated, so it is restored before the last assertion -- and that
            // restoration is itself asserted, because a restore that quietly stopped happening looks like nothing.
            bool saved = TrafficLight.BatteryBackup;
            TrafficLight.BatteryBackup = false;

            var cycle = new DayNightCycle { ExternalTime = true, VisualsEnabled = false, DayLength = 120f, Day = 0, Time = 0f };
            World.AddChild(cycle);
            var host = new Node3D();
            World.AddChild(host);
            var red = Lens(); var amber = Lens(); var green = Lens();
            host.AddChild(red); host.AddChild(amber); host.AddChild(green);
            var tl = TrafficLight.Make(new Vector3(9f, 0f, 9f), 0f, red, amber, green);
            World.AddChild(tl);
            yield return Ticks(2);
            T.Check("with no battery fitted a POWERED signal still runs its cycle",
                TrafficLight.LensIndexFor(tl.CurrentPhase) >= 0);

            tl.SetPowered(false);
            bool everBreathed = false;
            for (int i = 0; i < 80; i++)
            {
                cycle.Time += 0.03f / cycle.DayLength;
                yield return Ticks(1);
                if (tl.CurrentPhase is TrafficLight.Phase.FlashAmber or TrafficLight.Phase.FlashRed) everBreathed = true;
            }
            T.Check("...but losing the grid kills it outright -- no backup flash", !everBreathed);
            T.Check("...and it ends up dark", tl.DarkForTest);

            TrafficLight.BatteryBackup = saved;
            T.Check("the battery-backup static is restored for the rest of the run", TrafficLight.BatteryBackup);
        }
    }

    // Shooting out an individual aspect, and the two things that made this worth its own test: a dead lens must not
    // stop the head cycling (strawberry wants to shoot ONE light piece, not kill the mast), and a shot at an
    // already-dead lens must NOT be consumed -- otherwise a player emptying a magazine into a broken green can never
    // damage the prop, because every round is silently eaten by a lens that is already out.
    public sealed class TrafficLightShootOutTests : GameTest
    {
        public override string Name => "props.traffic_light_shoot_out";

        public override IEnumerable<Step> Run()
        {
            var host = new Node3D();
            World.AddChild(host);
            // Lenses at KNOWN, well-separated positions so a hit point can be aimed at one and verified not to
            // resolve to its neighbours -- the failure that matters is a shot at green killing amber.
            var mis = new MeshInstance3D[3];
            var at = new[] { new Vector3(0f, 6f, 0f), new Vector3(0f, 5f, 0f), new Vector3(0f, 4f, 0f) };
            for (int i = 0; i < 3; i++)
            {
                mis[i] = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.3f, 0.3f, 0.3f) }, Position = at[i],
                                              MaterialOverride = new StandardMaterial3D() };
                host.AddChild(mis[i]);
            }
            var tl = TrafficLight.Make(Vector3.Zero, 0f, mis[0], mis[1], mis[2]);
            World.AddChild(tl);
            yield return Ticks(2);

            // The emitter must sit at the LENSES, not at the prop origin (strawberry: "the actual point light emitter
            // is at the prop's 0,0 not on the actual light thing"). The node is TopLevel at the mast base, so a child
            // left at local zero ends up in the road. Lenses here average y=5.
            var glow = tl.GetChildren().OfType<OmniLight3D>().FirstOrDefault();
            T.Check("the head has a light emitter", glow != null);
            if (glow != null)
                T.Check($"...positioned at the lenses, not the prop origin (y={glow.GlobalPosition.Y:F2})",
                    Mathf.Abs(glow.GlobalPosition.Y - 5f) < 0.6f);

            // A point on a lens resolves to THAT lens and no other.
            for (int i = 0; i < 3; i++)
                T.Check($"a hit on lens {i} resolves to lens {i} (got {tl.LensHit(at[i])})", tl.LensHit(at[i]) == i);
            T.Check("a hit on the pole resolves to no lens", tl.LensHit(new Vector3(0f, 1f, 0f)) < 0);

            T.Check("no lens starts out shot", !tl.LensOutForTest(0) && !tl.LensOutForTest(1) && !tl.LensOutForTest(2));
            T.Check("shooting the red lens registers", tl.ShootOutLens(0));
            T.Check("...and only the red lens is out", tl.LensOutForTest(0) && !tl.LensOutForTest(1) && !tl.LensOutForTest(2));
            // THE ONE THAT MATTERS: a second shot on a dead lens returns false, so the caller lets it fall through to
            // the prop's health instead of eating it. Returning true here makes the mast bulletproof at that spot.
            T.Check("a second shot on a dead lens is NOT consumed", !tl.ShootOutLens(0));

            // The head keeps cycling with a dead aspect -- it just cannot show that one.
            tl.ForcePhase(TrafficLight.Phase.Red);
            yield return Ticks(1);
            T.Check("a shot-out red shows dark on its red phase", tl.DarkForTest);
            tl.ForcePhase(TrafficLight.Phase.Green);
            yield return Ticks(1);
            T.Check("...while green still lights normally", !tl.DarkForTest && tl.LitForTest(TrafficLight.Phase.Green));
            // Dead means dead: the lens geometry stays in the head, it simply never lights again.
            T.Check("...and the dead lens is still in the scene, not deleted", mis[0].Visible);

            // A rubble RESET rebuilds the mast, so it must also un-shoot the lenses. Without this you get a
            // pristine-looking signal with permanently blind aspects and no way to repair it.
            tl.SetBroken(true);
            yield return Ticks(1);
            T.Check("smashing the mast darkens it", tl.DarkForTest);
            tl.SetBroken(false);
            yield return Ticks(1);
            T.Check("a rubble reset clears shot-out lenses", !tl.LensOutForTest(0));
            tl.ForcePhase(TrafficLight.Phase.Red);
            yield return Ticks(1);
            T.Check("...so the repaired red lights again", tl.LitForTest(TrafficLight.Phase.Red));
        }
    }
}
