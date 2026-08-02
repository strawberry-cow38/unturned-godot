using Godot;
using System.Collections.Generic;

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
    //  2. Splitting per head. Both heads' red lenses share a palette texel, so one surface must carry FOUR triangles
    //     spanning both heads, not two. A mast arm showing red on one head and green on the other is a crash.
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

    // The side-road flags are DATA matched by POSITION, and the failure mode is silent: a key mismatch flags nothing,
    // every junction flashes amber, and that is indistinguishable from the file being absent. It shipped that way --
    // placements.txt stores raw Unity coordinates while WorldBuilder negates Z for Godot, so the lookup matched 0 of
    // 21 and only a placement-count print caught it.
    //
    // So this asserts against the REAL files using the SAME key transform WorldBuilder uses. A change that
    // reintroduces the bug drops the intersection to zero and fails here, rather than in a screenshot nobody takes at
    // the exact moment of a blackout.
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

            var flagged = new HashSet<(int, int)>();
            string sideFile = dir + "traffic_side_roads.txt";
            T.Check("the side-road data file ships", System.IO.File.Exists(sideFile));
            if (!System.IO.File.Exists(sideFile)) yield break;
            foreach (var line in System.IO.File.ReadLines(sideFile))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2 && float.TryParse(p[0], out var sx) && float.TryParse(p[1], out var sz))
                    flagged.Add((Mathf.RoundToInt(sx * 10f), Mathf.RoundToInt(sz * 10f)));
            }

            int matched = 0;
            foreach (var k in flagged) if (placed.Contains(k)) matched++;
            // The teeth: with the Z-convention bug this is 0.
            T.Check($"every flagged coordinate matches a real placement ({matched}/{flagged.Count})",
                flagged.Count > 0 && matched == flagged.Count);
            // ...and not ALL of them, or the flag carries no information and every junction flashes red instead.
            T.Check($"the flags are a proper subset -- both aspects exist ({matched} of {placed.Count})",
                matched > 0 && matched < placed.Count);
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
            bool sawAmberLit = false, sawFullyDark = false;
            for (int i = 0; i < 240; i++)
            {
                cycle.Time += 0.05f / cycle.DayLength;   // 50ms of game clock per step
                yield return Ticks(1);
                if (tl.CurrentPhase == TrafficLight.Phase.FlashAmber) sawAmberLit = true;
                if (tl.CurrentPhase == TrafficLight.Phase.Off) sawFullyDark = true;
                if (sawAmberLit && sawFullyDark) break;
            }
            T.Check("an unpowered main-road signal flashes AMBER", sawAmberLit);
            T.Check("...and blinks, rather than sitting solid", sawFullyDark);
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
            for (int i = 0; i < 240; i++)
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
            yield return Ticks(3);
            T.Check("after the battery drains the signal is DARK", tl.CurrentPhase == TrafficLight.Phase.Off && tl.DarkForTest);
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
            var beforeBrownout = tl.CurrentPhase;
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
}
