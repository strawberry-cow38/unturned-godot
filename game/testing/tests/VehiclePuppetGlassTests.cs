using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // "theres a lot of stuff that exists on singleplayer loopback but not on the vox server... vehicle glass"
    // (strawberry 2026-09-17). Other players' cars had no windows: AddGlassOverlay was reachable only from
    // Vehicle.Build, and BuildPuppetByName built a body, headlights and taillights and stopped.
    //
    // ⚠ This is the SECOND time the puppet has been caught missing something the real builder does -- v21
    // found it had no lamps, for the same reason: two builders, one of them forgotten. So the check that
    // matters is not "a puppet has some glass" but "a puppet has the SAME panes the real vehicle does",
    // which is the property that goes wrong when the two drift apart again.
    public sealed class VehiclePuppetGlassTests : GameTest
    {
        public override string Name => "vehicle.puppet_glass";
        public override int Tier => 1;

        static List<string> GlassNames(Node n)
        {
            var found = new List<string>();
            foreach (var c in n.GetChildren())
            {
                if (c is MeshInstance3D mi && mi.Name.ToString().StartsWith("Glass")) found.Add(mi.Name.ToString());
                found.AddRange(GlassNames(c));
            }
            found.Sort();
            return found;
        }

        public override IEnumerable<Step> Run()
        {
            // A sedan, because its spec names a per-pane glass mesh (sedan_glass.txt) -- the multi-pane path,
            // not the single-canopy fallback, so this exercises the shape most vehicles use.
            var puppet = Vehicle.BuildPuppetByName("sedan", 0);
            T.Check("puppet built", puppet != null);
            if (puppet == null) yield break;
            World.AddChild(puppet);
            yield return Ticks(2);

            var puppetGlass = GlassNames(puppet);
            T.Check($"the puppet has window panes ({puppetGlass.Count}: {string.Join(", ", puppetGlass)})",
                    puppetGlass.Count > 0);

            // THE REAL COMPARISON. A real sedan through the ordinary build path, same spec, same panes.
            var real = Vehicle.BuildByName("sedan");
            T.Check("real vehicle built", real != null);
            if (real == null) { puppet.QueueFree(); yield break; }
            World.AddChild(real);
            yield return Ticks(2);

            var realGlass = GlassNames(real);
            T.Check($"the real vehicle has panes too ({realGlass.Count})", realGlass.Count > 0);
            // ⚠ Set equality, not a count: two builders can agree on HOW MANY panes while disagreeing about
            // which ones, and a missing rear windscreen that happens to be balanced by a duplicated door
            // window would pass a count check while looking wrong.
            T.Check($"puppet panes match the real vehicle's exactly (puppet [{string.Join(",", puppetGlass)}] vs real [{string.Join(",", realGlass)}])",
                    puppetGlass.Count == realGlass.Count && string.Join(",", puppetGlass) == string.Join(",", realGlass));

            puppet.QueueFree();
            real.QueueFree();
            yield break;
        }
    }
}
