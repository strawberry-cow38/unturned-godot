using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // SINK FLUID IO (strawberry: "add hose io port to sinks, connects at the spout of the faucet. add a water input,
    // for using sinks after water shutoff").
    //
    // The request has two halves and they fail in completely different ways, so they're tested separately:
    //   - the SPOUT is a PLACEMENT problem. A port at the wrong offset is a fully working port you cannot see or aim
    //     at, floating beside the counter. Nothing errors, every count-based check passes, and the only symptom is a
    //     player saying "the sink doesn't have a hose thing". The old hand-guessed value was 21 cm below the spout.
    //   - the INLET is a SOLVER-SEMANTICS problem, and the trap is that the obvious implementation defeats itself:
    //     while the sink was `SupplyEnabled => GlobalWater`, adding an inlet would have produced a container that
    //     accepts water after a shutoff and can never give it back. An inlet that fills a dead sink LOOKS like the
    //     feature works right up until you try to draw from it.
    //
    // So the inlet test measures water arriving at the FAR END of a second hose, with the mains off. Anything less
    // than that is satisfied by the broken version.
    public sealed class SinkWaterInputTests : GameTest
    {
        public override string Name => "fluid.sink_water_input";

        // A catcher tank whose intake is AT OR BELOW the sink's tap rate. The solver only fills a storage whose input
        // is Flowing -- i.e. actually getting its full demand -- so a tank asking for more than the tap provides reads
        // as ZERO flow rather than a slow trickle. That's existing solver semantics, not something this feature
        // changed, but it makes a hungry tank look exactly like a dead tap, so the number is deliberate.
        static FluidContainer Tank(float amount, float rate = SinkSource.TapRate)
            => FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.Water, 20000f, amount, WaterQuality.Clean), rate);

        static void Hose(Node parent, FluidPortNode src, FluidPortNode dst)
            => parent.AddChild(new Hose { Source = src, Consumer = dst });

        public override IEnumerable<Step> Run()
        {
            bool saved = FluidNet.GlobalWater;
            World.AddChild(new FluidManager());

            // Heights matter: FluidNet only conducts a hose whose consumer end sits BELOW its source (or inside a
            // powered pump's lift). Feeder above the sink, sink above the catcher, so both hops run on gravity alone
            // and a failure means the feature is broken rather than that the water had nowhere to go.
            // The feeder must supply AT LEAST the sink's inlet rate, or the inlet never counts as Flowing and the
            // whole test reads as "the water input doesn't work" when it was simply never fed. Same full-demand rule
            // that makes a hungry catcher look like a dead tap, one hop upstream.
            var feeder = Tank(20000f, SinkSource.InletRate + 20f);   // a full barrel on a shelf -- the "I hosed water in" side
            var sink = SinkSource.Make();
            var catcher = Tank(0f);                // what comes out of the tap
            feeder.Position = new Vector3(0f, 8f, 0f);
            sink.Position = new Vector3(0f, 4f, 0f);
            catcher.Position = new Vector3(2f, 0f, 0f);
            World.AddChild(feeder); World.AddChild(sink); World.AddChild(catcher);
            yield return Ticks(2);

            T.Check($"a sink has two ports -- an inlet and a spout ({sink.Ports.Count})", sink.Ports.Count == 2);
            T.Check("...Ports[0] is the INLET (a consumer)", sink.Ports[0].Kind == SDG.Unturned.FluidPortKind.Consumer);
            T.Check("...Ports[1] is the SPOUT (a source)", sink.Ports[1].Kind == SDG.Unturned.FluidPortKind.Source);
            T.Check($"...and each has a physical hose cube ({sink.PortNodes.Count})", sink.PortNodes.Count == 2);

            // ---- 1. MAINS UP: the tap runs, and the basin stays full while it does. -------------------------------
            // The "stays full" half is the actual mechanism: the sink is a 5 L Storage, not an infinite source, so a
            // tap that keeps running can only mean the mains are topping it back up every tick. Assert the mechanism,
            // not just the outcome -- an Infinite flag would produce the same outcome and a different sink.
            FluidNet.SetGlobalWater(true);
            Hose(World, sink.Ports[1], catcher.Ports[0]);
            yield return Until(() => catcher.Tank.Amount > 1f);
            T.Check($"on live mains the spout fills a hosed tank ({catcher.Tank.Amount:F0} mL)", catcher.Tank.Amount > 1f);
            yield return Ticks(10);
            T.Check($"...and the basin stays full while supplying, i.e. the mains refill it ({sink.Tank.Amount:F0}/{SinkSource.BasinCapacity:F0})",
                sink.Tank.Amount > SinkSource.BasinCapacity - 1f);

            // ---- 2. MAINS CUT: the basin is what's left, and then the tap is dry. ---------------------------------
            FluidNet.SetGlobalWater(false);
            yield return Ticks(2);
            T.Check("cutting the mains does NOT make the sink inert -- it still has what's in the basin",
                sink.SupplyEnabled && sink.Tank.Amount > 1f);

            // Drain the basin directly rather than waiting 5 L / 30 mL/s of sim time. This ARRANGES the dry-sink
            // state; it doesn't stand in for any mechanism the test then asserts.
            sink.Tank.Drain(sink.Tank.Amount);
            yield return Ticks(3);
            float dryMark = catcher.Tank.Amount;
            yield return Ticks(15);
            T.Check($"a dry sink on dead mains gives nothing ({dryMark:F0} -> {catcher.Tank.Amount:F0} mL)",
                Mathf.Abs(catcher.Tank.Amount - dryMark) < 0.01f);

            // ---- 3. THE REQUEST: hose water into the inlet and the tap runs again, mains still off. ---------------
            float feederStart = feeder.Tank.Amount;
            Hose(World, feeder.Ports[1], sink.Ports[0]);   // a storage's Ports[1] is its OUTPUT
            yield return Until(() => catcher.Tank.Amount > dryMark + 1f);
            T.Check($"feeding the inlet makes the spout run again with the mains OFF ({dryMark:F0} -> {catcher.Tank.Amount:F0} mL)",
                catcher.Tank.Amount > dryMark + 1f);
            // Conservation, and the reason it's checked: a sink that fabricated the water instead of passing it
            // through would satisfy the line above exactly. The feeder has to actually go down.
            T.Check($"...and the water came FROM the feeder, not from nowhere ({feederStart:F0} -> {feeder.Tank.Amount:F0} mL)",
                feeder.Tank.Amount < feederStart - 0.5f);

            // ---- 4. Quality travels through. The sink is the map's only clean tap; that must not launder tainted
            // water hosed in during an outage, or the shutoff stops meaning anything.
            // Taint the EXISTING feeder rather than hosing in a second one: a consumer port takes one hose, and two
            // would make the quality resolution depend on which the tree happened to return first.
            feeder.Tank.Quality = WaterQuality.Tainted;
            yield return Until(() => sink.Tank.Quality != WaterQuality.Clean);
            T.Check($"tainted water hosed into a sink does NOT come out clean ({sink.Tank.Quality})",
                sink.Tank.Quality == WaterQuality.Tainted);

            // ...and the mains flush it when they come back. Otherwise one bad hose permanently ruins a fixture the
            // player can't repair.
            FluidNet.SetGlobalWater(true);
            yield return Until(() => sink.Tank.Quality == WaterQuality.Clean);
            T.Check("restoring the mains flushes the basin clean again", sink.Tank.Quality == WaterQuality.Clean);
            T.Check($"...and refills it ({sink.Tank.Amount:F0} mL)", sink.Tank.Amount > SinkSource.BasinCapacity - 1f);

            FluidNet.SetGlobalWater(saved);
        }
    }

    // WHERE THE SPOUT PORT SITS, checked against the .obj rather than against the constant. A port cube is invisible
    // until the hose tool is out and has no collision with the world, so a wrong offset produces no error, no visual
    // artefact and no failing count -- it just quietly isn't where the faucet is. The only way to catch that in a
    // headless test is to re-derive the feature's position from the mesh and compare.
    public sealed class SinkSpoutAnchorTests : GameTest
    {
        public override string Name => "fluid.sink_spout_anchor";

        const float CounterTopZ = 1.35f;    // everything above this in Counter_1/_3 belongs to the tap
        const float ArmForwardY = -0.28f;   // the half of the gooseneck that overhangs the basin

        // The spout MOUTH: of the tap geometry, take the forward half of the arm and its lowest ring -- the downward
        // opening water leaves from. Derived here the same way the constant was, from the file that ships.
        static bool SpoutMouth(string obj, out Vector3 centre, out int ringPoints)
        {
            centre = Vector3.Zero; ringPoints = 0;
            if (!System.IO.File.Exists(obj)) return false;
            var tap = new List<Vector3>();
            foreach (var line in System.IO.File.ReadLines(obj))
            {
                if (line.Length < 2 || line[0] != 'v' || line[1] != ' ') continue;
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4) continue;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out var x)) continue;
                if (!float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out var y)) continue;
                if (!float.TryParse(p[3], System.Globalization.NumberStyles.Float, ci, out var z)) continue;
                if (z > CounterTopZ + 0.05f && y > ArmForwardY) tap.Add(new Vector3(x, y, z));
            }
            if (tap.Count == 0) return false;
            float lowest = float.MaxValue;
            foreach (var v in tap) if (v.Z < lowest) lowest = v.Z;
            var sum = Vector3.Zero;
            foreach (var v in tap) if (Mathf.Abs(v.Z - lowest) < 1e-3f) { sum += v; ringPoints++; }
            centre = sum / ringPoints;
            return true;
        }

        public override IEnumerable<Step> Run()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");

            // Both sink variants are the same model in two finishes, so one constant is expected to serve both -- and
            // if that ever stops being true this is where it shows, rather than in six silently misplaced ports.
            foreach (var mesh in new[] { "Counter_1", "Counter_3" })
            {
                bool ok = SpoutMouth(dir + mesh + ".obj", out var mouth, out int ring);
                T.Check($"{mesh}: the tap's forward arm has a spout mouth ring ({ring} points)", ok && ring >= 4);
                T.Check($"{mesh}: SinkSource.SpoutMeshLocal is ON it (mesh {mouth} vs {SinkSource.SpoutMeshLocal})",
                    ok && mouth.DistanceTo(SinkSource.SpoutMeshLocal) < 0.01f);
            }

            // The mouth must be ABOVE the counter top and OVER the basin, not merely at some vertex on the prop --
            // the two ways a plausible-looking wrong constant fails.
            T.Check($"the spout sits above the counter top (Z {SinkSource.SpoutMeshLocal.Z:F3} > {CounterTopZ})",
                SinkSource.SpoutMeshLocal.Z > CounterTopZ);
            T.Check($"...and overhangs the basin (mesh Y {SinkSource.SpoutMeshLocal.Y:F3}, basin spans -0.300..0.300)",
                SinkSource.SpoutMeshLocal.Y > -0.30f && SinkSource.SpoutMeshLocal.Y < 0.30f);
            // The inlet is the opposite: below the counter top, on the FRONT face, under the overhanging lip.
            T.Check($"the inlet is under the counter (Z {SinkSource.InletMeshLocal.Z:F3} < {CounterTopZ})",
                SinkSource.InletMeshLocal.Z < CounterTopZ);
            T.Check($"...on the front face, proud of the door and under the lip (mesh Y {SinkSource.InletMeshLocal.Y:F3} in 0.50..0.625)",
                SinkSource.InletMeshLocal.Y >= 0.50f && SinkSource.InletMeshLocal.Y <= 0.625f);

            // ---- THE TRANSFORM. Mesh coords are Z-up; the prop is stood upright by euler X=270 while the fluid node
            // gets yaw only, so a port has to be un-yawed out of the full placement basis. For an upright prop that
            // reduces to (x, z, -y) at ANY yaw -- pinned here because it's the case 27 of 28 counters take.
            //
            // Note the placement passed in INCLUDES the yaw, exactly as WorldBuilder's does (`Basis(Y, 180-ey) *
            // Basis(X, ex) * Basis(Z, -ez)`); the yawDeg argument is what gets divided back out. Handing in a
            // yaw-less basis alongside a non-zero yawDeg un-rotates something that was never rotated -- which is what
            // this loop did on its first run, and it failed, correctly.
            foreach (float yaw in new[] { 0f, 37f, 129f, -84f })
            {
                var placed = new Basis(Vector3.Up, Mathf.DegToRad(yaw)) * SinkSource.UprightPlacement;
                var got = SinkSource.MeshToNode(placed, yaw, SinkSource.SpoutMeshLocal);
                var want = new Vector3(SinkSource.SpoutMeshLocal.X, SinkSource.SpoutMeshLocal.Z, -SinkSource.SpoutMeshLocal.Y);
                T.Check($"upright counter @ yaw {yaw}: mesh->node is (x, z, -y) ({got} vs {want})", got.DistanceTo(want) < 1e-3f);
            }

            // ...and the 28th. One Counter_3 on PEI is placed at euler (277.289, *, 237.977): pitched AND rolled. The
            // whole reason MeshToNode takes a basis instead of doing the swizzle inline is this instance, so assert
            // that it actually diverges -- otherwise the per-instance path is untested machinery that could be
            // deleted without any test noticing, which is the same as not having it.
            const float ex = 277.289f, ez = 237.977f, ey = 51f;
            var rolled = new Basis(Vector3.Up, Mathf.DegToRad(180f - ey))
                       * new Basis(Vector3.Right, Mathf.DegToRad(ex))
                       * new Basis(Vector3.Back, Mathf.DegToRad(-ez));
            var rolledLocal = SinkSource.MeshToNode(rolled, 180f - ey, SinkSource.SpoutMeshLocal);
            var naive = new Vector3(SinkSource.SpoutMeshLocal.X, SinkSource.SpoutMeshLocal.Z, -SinkSource.SpoutMeshLocal.Y);
            T.Check($"the pitched+rolled counter needs its own transform ({rolledLocal} vs the naive {naive})",
                rolledLocal.DistanceTo(naive) > 0.1f);

            // And it must still land on the tap: whatever the rotation, the port is the same physical point of the
            // same prop, so its distance from the prop origin can't change.
            T.Check($"...and it's still the same point on the prop ({rolledLocal.Length():F3} vs {naive.Length():F3} from origin)",
                Mathf.Abs(rolledLocal.Length() - naive.Length()) < 1e-2f);

            // A sink built through the world path puts its ports where the transform says.
            var sink = SinkSource.Make(SinkSource.UprightPlacement, 0f);
            World.AddChild(sink);
            yield return Ticks(1);
            T.Check($"the spout PORT CUBE is at the spout ({sink.PortNodes[1].Position})",
                sink.PortNodes[1].Position.DistanceTo(naive) < 1e-3f);
            T.Check($"the inlet port cube is under the counter, not stacked on the spout ({sink.PortNodes[0].Position})",
                sink.PortNodes[0].Position.DistanceTo(sink.PortNodes[1].Position) > 0.5f);
        }
    }
}
