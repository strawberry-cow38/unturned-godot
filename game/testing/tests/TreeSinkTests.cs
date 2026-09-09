using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // TREES SIT LOWER (strawberry: "lower all tree models on their positions by a little bit").
    //
    // The offset itself is one subtraction. What makes it worth a suite is that a tree is TWO things in the world -- a
    // MultiMesh instance you can see and a trunk cylinder you walk into -- built from the same transform list by two
    // separate loops. Sink one and not the other and the tree looks correctly seated while its collider stands proud
    // of the ground: an invisible bug, met only by walking into a trunk that is not where the trunk is. So the check
    // that matters is not "did it move down", it is "did BOTH move down together".
    //
    // The offset is also scaled by the instance's Y-scale, because these spawns run from saplings to full canopy off
    // the same baked origin. A flat nudge that seats a big pine leaves a small one hovering.
    public sealed class TreeSinkTests : GameTest
    {
        public override string Name => "world.tree_sink";

        public override IEnumerable<Step> Run()
        {
            // ---- THE MATHS, on transforms we control -- the shipped .bin has no known-good baseline to compare to.
            var xf = new List<Transform3D>
            {
                new(Basis.Identity, new Vector3(10f, 5f, -3f)),                                  // scale 1
                new(Basis.Identity.Scaled(new Vector3(2f, 2f, 2f)), new Vector3(0f, 0f, 0f)),    // a big one
                new(Basis.Identity.Scaled(new Vector3(0.5f, 0.5f, 0.5f)), new Vector3(-7f, 12f, 4f)),
                new(Basis.Identity.Scaled(Vector3.Zero), new Vector3(1f, 1f, 1f)),               // degenerate
            };
            var before = new List<Transform3D>(xf);
            ResourceField.SinkTrees(xf);

            T.Check($"a unit-scale tree drops by exactly TreeSink ({before[0].Origin.Y - xf[0].Origin.Y:0.###} vs {ResourceField.TreeSink:0.###})",
                Mathf.IsEqualApprox(before[0].Origin.Y - xf[0].Origin.Y, ResourceField.TreeSink));
            T.Check($"a double-scale tree drops twice as far ({before[1].Origin.Y - xf[1].Origin.Y:0.###})",
                Mathf.IsEqualApprox(before[1].Origin.Y - xf[1].Origin.Y, ResourceField.TreeSink * 2f));
            T.Check($"a half-scale one drops half ({before[2].Origin.Y - xf[2].Origin.Y:0.###})",
                Mathf.IsEqualApprox(before[2].Origin.Y - xf[2].Origin.Y, ResourceField.TreeSink * 0.5f));
            // A zero scale must not resolve to "sink by nothing" OR to a division blowing up -- it falls back to 1.
            T.Check($"a degenerate scale still moves, and finitely ({before[3].Origin.Y - xf[3].Origin.Y:0.###})",
                Mathf.IsEqualApprox(before[3].Origin.Y - xf[3].Origin.Y, ResourceField.TreeSink));

            // Down ONLY. "Lower on their positions" is one axis; drifting X or Z would move trees off the spots the map
            // was authored around -- into roads, through walls -- and would look like a placement bug, not this change.
            for (int i = 0; i < xf.Count; i++)
                T.Check($"[{i}] moved straight down, not sideways ({xf[i].Origin.X - before[i].Origin.X:0.###}, {xf[i].Origin.Z - before[i].Origin.Z:0.###})",
                    Mathf.IsEqualApprox(xf[i].Origin.X, before[i].Origin.X) && Mathf.IsEqualApprox(xf[i].Origin.Z, before[i].Origin.Z));
            T.Check("...and none of them rotated or rescaled",
                xf[0].Basis.IsEqualApprox(before[0].Basis) && xf[1].Basis.IsEqualApprox(before[1].Basis));

            // "A LITTLE BIT". A sink deep enough to swallow the trunk flare would read as trees growing out of a hole.
            T.Check($"the sink is small ({ResourceField.TreeSink:0.##} m at unit scale)",
                ResourceField.TreeSink > 0f && ResourceField.TreeSink < 0.6f);

            // ---- AND THE COLLIDER WENT WITH IT. The real claim. Built from the committed content, because the two
            // consumers only diverge in the production path -- the unit checks above cannot see it at all.
            var field = new ResourceField();
            World.AddChild(field);
            field.LoadResources("NONE");
            yield return Ticks(2);
            T.Check($"the committed content loaded ({field.InstanceCount} instances)", field.InstanceCount > 0);

            int checkedTrees = 0, agreed = 0;
            for (int i = 0; i < field.InstanceCount && checkedTrees < 40; i++)
            {
                var trunk = field.DebugTrunk(i);
                if (trunk == null) continue;   // not a tree
                checkedTrees++;
                if (Mathf.Abs(trunk.Transform.Origin.Y - field.DebugInstanceXf(i).Origin.Y) < 1e-3f) agreed++;
            }
            T.Check($"there are trees with trunk colliders to check ({checkedTrees})", checkedTrees > 0);
            T.Check($"every trunk collider sits at its instance's height ({agreed}/{checkedTrees})",
                checkedTrees > 0 && agreed == checkedTrees);

            yield break;
        }
    }
}
