using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The MUNICIPAL WATER MAINS (strawberry): fire hydrants, water towers and sinks are infinite sources while
    // toggleGlobalWater is on, and dead once it isn't.
    //
    // The assertion that matters is NOT "the flag flipped" -- it's that flipping it actually stops water arriving at
    // the far end of a hose. A gate that sets a bool but leaves the solver supplying reads as working in every check
    // that doesn't move real fluid, which is exactly the shape of bug that has bitten this repo repeatedly tonight.
    // So this hoses a source to a tank and measures the tank.
    public sealed class WaterMainsGateTests : GameTest
    {
        public override string Name => "fluid.water_mains_gate";

        // A plain storage tank to catch what the source pushes. Its intake must be AT OR BELOW one hydrant outlet
        // (80/s): the solver only fills a storage whose input is Flowing, i.e. actually getting its full demand, so a
        // tank that asks for 500/s off an 80/s tap reads as zero flow rather than as a slow trickle. That is the
        // existing solver's semantics, not something this feature changed -- but it makes a hungry tank look exactly
        // like a dead main, so the number here is deliberate.
        static FluidContainer Catcher() => FluidContainer.Make(FluidRole.Storage,
            new FluidTank(FluidType.Water, 5000f, 0f, WaterQuality.Clean), 50f);

        static void Hose(Node parent, FluidContainer src, FluidContainer dst)
        {
            var h = new Hose { Source = src.Ports[0], Consumer = dst.Ports[0] };
            parent.AddChild(h);
        }

        public override IEnumerable<Step> Run()
        {
            bool saved = FluidNet.GlobalWater;
            FluidNet.SetGlobalWater(true);
            World.AddChild(new FluidManager());

            // The tank sits BELOW the hydrant. Fluid here is gravity-driven: FluidNet only conducts a hose whose
            // consumer end is lower than its source (or inside a powered pump's lift), so two containers dropped at
            // the same point never flow and the test would read as "the mains are broken" when they are fine.
            var hydrant = FireHydrantSource.Make();
            var tank = Catcher();
            hydrant.Position = new Vector3(0f, 4f, 0f);
            tank.Position = new Vector3(2f, 0f, 0f);
            World.AddChild(hydrant); World.AddChild(tank);
            yield return Ticks(2);
            Hose(World, hydrant, tank);
            // Until, NOT Ticks: FluidManager solves in _Process (a RENDER frame) and Ticks advances PHYSICS ticks --
            // the same mismatch that made props.global_blackout pass in a crowd and fail alone (fixed in ec944897).
            // A fixed tick count here reads as "the mains don't work" when the solver simply hasn't run yet.
            yield return Until(() => tank.Tank.Amount > 0.01f);

            float withWater = tank.Tank.Amount;
            T.Check($"a hydrant on live mains fills a hosed tank ({withWater:F0} units)", withWater > 0.01f);

            // THE GATE. Same graph, same hose, water off -> nothing more arrives.
            FluidNet.SetGlobalWater(false);
            yield return Ticks(2);
            float atCut = tank.Tank.Amount;
            yield return Ticks(15);
            T.Check($"cutting the mains stops the flow ({atCut:F0} -> {tank.Tank.Amount:F0})",
                Mathf.Abs(tank.Tank.Amount - atCut) < 0.01f);

            // INFINITE is orthogonal to the gate: the reservoir doesn't drain while it's shut off, it just stops
            // being fed to you. Asserting this pins the design -- "off" must not be implemented by emptying the tank,
            // because a source reading empty still advertises a port and leaves pumps on its line awake.
            T.Check("...and the source is still full, not drained", hydrant.Tank.Amount > 1f);
            T.Check("...and still flagged infinite", hydrant.Infinite);

            // Restoring brings it back without re-hosing.
            FluidNet.SetGlobalWater(true);
            yield return Until(() => tank.Tank.Amount > atCut + 0.01f);
            T.Check($"restoring the mains resumes flow ({atCut:F0} -> {tank.Tank.Amount:F0})", tank.Tank.Amount > atCut + 0.01f);

            FluidNet.SetGlobalWater(saved);
        }
    }

    // Shape of the three municipal sources: port counts and water quality. Cheap, and it pins the two things
    // strawberry specified by number ("4 hose IO ports", "tainted", "sinks supply clean water") so a later tweak to
    // the base Source port layout can't quietly turn a hydrant back into a single-spigot prop.
    public sealed class WaterMainsShapeTests : GameTest
    {
        public override string Name => "fluid.water_mains_shape";

        public override IEnumerable<Step> Run()
        {
            var hydrant = FireHydrantSource.Make();
            var tower = WaterTowerSource.Make();
            var sink = SinkSource.Make();
            World.AddChild(hydrant); World.AddChild(tower); World.AddChild(sink);
            yield return Ticks(2);

            // FOUR outlets, and all of them Source kind -- the base Source case builds exactly one port, so this
            // catches BuildPorts being dropped or calling base and leaving a fifth port inside the barrel.
            T.Check($"a hydrant has {FireHydrantSource.Outlets} hose ports ({hydrant.Ports.Count})",
                hydrant.Ports.Count == FireHydrantSource.Outlets);
            int sourceKind = 0;
            foreach (var p in hydrant.Ports) if (p.Kind == SDG.Unturned.FluidPortKind.Source) sourceKind++;
            T.Check($"...all of them outlets, none an inlet ({sourceKind}/{hydrant.Ports.Count})", sourceKind == hydrant.Ports.Count);
            T.Check($"...each with a physical hose cube ({hydrant.PortNodes.Count})", hydrant.PortNodes.Count == hydrant.Ports.Count);

            // The outlets must be at DISTINCT positions -- four ports stacked on one point is four ports you cannot
            // aim a hose at individually, which looks identical to one port in every count-based check.
            var seen = new HashSet<(int, int, int)>();
            foreach (var n in hydrant.PortNodes)
                seen.Add((Mathf.RoundToInt(n.Position.X * 100f), Mathf.RoundToInt(n.Position.Y * 100f), Mathf.RoundToInt(n.Position.Z * 100f)));
            T.Check($"...spread around the barrel, not stacked ({seen.Count} distinct positions)", seen.Count == hydrant.Ports.Count);

            T.Check("the tower is a single spigot", tower.Ports.Count == 1);
            T.Check("a sink is a single tap", sink.Ports.Count == 1);

            // WATER QUALITY is the whole economy: mains water is tainted and must be purified or bottled; the sink is
            // the one clean tap, and it goes away with the mains.
            T.Check("hydrant water is TAINTED", hydrant.Tank.Quality == WaterQuality.Tainted);
            T.Check("tower water is TAINTED", tower.Tank.Quality == WaterQuality.Tainted);
            T.Check("sink water is CLEAN", sink.Tank.Quality == WaterQuality.Clean);

            // All three ride the mains gate. Checked on the instances rather than by reading the source, so a class
            // that forgets the override fails here instead of silently staying live through a shutoff.
            bool saved = FluidNet.GlobalWater;
            FluidNet.SetGlobalWater(true);
            T.Check("all three supply while the mains are up",
                hydrant.SupplyEnabled && tower.SupplyEnabled && sink.SupplyEnabled);
            FluidNet.SetGlobalWater(false);
            T.Check("...and all three go inert when the mains are cut",
                !hydrant.SupplyEnabled && !tower.SupplyEnabled && !sink.SupplyEnabled);
            FluidNet.SetGlobalWater(saved);

            // An ordinary tank must NOT be gated -- the mains switch is about municipal supply, not about every
            // barrel in the world going dry.
            var barrel = FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 1000f, 500f, WaterQuality.Tainted), 50f);
            World.AddChild(barrel);
            yield return Ticks(1);
            FluidNet.SetGlobalWater(false);
            T.Check("a plain storage tank is NOT gated by the mains", barrel.SupplyEnabled);
            FluidNet.SetGlobalWater(saved);
        }
    }

    // WHICH PROPS ARE SINKS is derived from the meshes, and the derivation is the fragile part: a wrong answer here
    // puts taps on plain counters or leaves the real sinks dry, and either reads as "the sink feature doesn't work".
    // A previous comment in ContainerShelf claimed "Counter_3/4 are SINKS" -- there is no Counter_4 on this map at
    // all, and Counter_1 is a sink too. So this asserts the classification against the actual .obj files.
    //
    // This test has already earned its keep: it failed on its first run against a colour-based rule I had written
    // into the classifier's comments, because Counter_2 is a plain counter with an all-grey steel palette. The
    // conclusion (1 and 3 are the sinks) was right; the stated reason was wrong, which is the kind of thing that
    // survives every outcome check and then misleads whoever extends it.
    public sealed class SinkPropIdentityTests : GameTest
    {
        public override string Name => "fluid.sink_prop_identity";

        // GEOMETRY, not colour. My first pass here used "has a grey/metal texel" and this test caught it: Counter_2
        // is a plain counter with an entirely GREY palette (a steel-finish counter), byte-identical in colour to the
        // steel sink Counter_3. Palette says nothing -- the pairing is wood/steel FINISH (0 vs 2, 1 vs 3), and what
        // actually separates a sink is the extra fitting standing ABOVE the counter surface:
        //
        //   Counter_0 / Counter_2   116 verts, geometry stops at the top face Z 1.35     -> plain counter
        //   Counter_1 / Counter_3   180 verts, extra group reaching Z 1.83               -> basin + tap
        //
        // So: anything with geometry meaningfully above the counter top is a sink.
        const float CounterTopZ = 1.35f;

        static bool HasTapAboveCounter(string dir, string mesh)
        {
            string obj = dir + mesh + ".obj";
            if (!System.IO.File.Exists(obj)) return false;
            foreach (var line in System.IO.File.ReadLines(obj))
            {
                if (line.Length < 2 || line[0] != 'v' || line[1] != ' ') continue;
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4 && float.TryParse(p[3], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var z) && z > CounterTopZ + 0.05f)
                    return true;
            }
            return false;
        }

        public override IEnumerable<Step> Run()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");

            // The classification WorldBuilder actually uses, against the meshes it actually ships.
            foreach (var mesh in new[] { "Counter_0", "Counter_1", "Counter_2", "Counter_3" })
            {
                bool hasTap = HasTapAboveCounter(dir, mesh);
                bool claimed = WorldBuilder.IsSinkProp(mesh);
                T.Check($"{mesh}: sink={claimed} matches its geometry (fitting above the counter top={hasTap})", claimed == hasTap);
            }

            // The stale comment named a Counter_4. Pin that it does not exist, so nobody re-derives from it.
            T.Check("there is no Counter_4 on this map", !System.IO.File.Exists(dir + "Counter_4.obj"));
            yield break;
        }
    }
}
