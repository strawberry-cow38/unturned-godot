using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // Chopping is server-authoritative and its sim is covered engine-free (ResourceHarvestSimTests,
    // ServerChopTests). What can only break HERE is the join between a physics body a ray hit and the
    // index the server understands -- and it broke immediately: the trunk's stamped index was the
    // per-TYPE loop counter, not the load-order index, so every Maple and Pine trunk claimed to be some
    // Birch. Nothing about that is visible from the sim's side; the tests all passed.
    public class TreeIndexMetaIsTheWireIndex : GameTest
    {
        public override string Name => "tree.index_meta_is_wire_index";
        public override IEnumerable<Step> Run()
        {
            var field = new ResourceField { VisualInstances = false };
            World.AddChild(field);
            field.LoadResources("NONE");

            int trunks = 0, wrong = 0, firstWrong = -1;
            var types = new HashSet<string>();
            for (int i = 0; i < field.InstanceCount; i++)
            {
                var trunk = field.DebugTrunk(i);
                if (trunk == null) continue;
                trunks++;
                types.Add(field.TypeNameOf(i));
                int stamped = ResourceField.IndexOfCollider(trunk);
                if (stamped == i) continue;
                wrong++;
                if (firstWrong < 0) firstWrong = i;
            }

            T.Check($"the world has tree trunks to test ({trunks})", trunks > 0);
            // The bug only exists PAST the first type in the manifest, so a single-type world would pass
            // either way -- assert the coverage that makes the check meaningful.
            T.Check($"across more than one tree type ({types.Count}: {string.Join(", ", types.Take(4))}...)", types.Count > 1);
            T.Check($"every trunk collider resolves back to its OWN index ({wrong} wrong"
                    + (firstWrong >= 0 ? $", first at {firstWrong} ({field.TypeNameOf(firstWrong)})" : "") + ")",
                    wrong == 0);

            // And the round trip the melee path actually walks: collider -> index -> type.
            for (int i = 0; i < field.InstanceCount && trunks > 0; i++)
            {
                var trunk = field.DebugTrunk(i);
                if (trunk == null) continue;
                int idx = ResourceField.IndexOfCollider(trunk);
                T.Check($"the ray-hit trunk names its own type ({field.TypeNameOf(idx)})",
                        field.TypeNameOf(idx) == field.TypeNameOf(i));
                break;
            }
            yield break;
        }
    }

    // The look-at tree bar. The rule under test is what the panel is allowed to CLAIM: a tree this player
    // has never hit has no known health, and drawing a full bar for it would be a guess rendered as fact.
    public class TreeHealthBarOnlyShowsWhatTheServerSaid : GameTest
    {
        public override string Name => "tree.health_bar";

        static int FirstTree(ResourceField f)
        {
            for (int i = 0; i < f.InstanceCount; i++) if (f.DebugTrunk(i) != null) return i;
            return -1;
        }

        public override IEnumerable<Step> Run()
        {
            var field = new ResourceField { VisualInstances = false };
            World.AddChild(field);
            field.LoadResources("NONE");
            yield return Ticks(1);

            int tree = FirstTree(field);
            T.Check($"found a tree to look at (index {tree})", tree >= 0);
            if (tree < 0) yield break;

            field.ShowInfoFor(tree);
            var info = field.DebugInfo;
            T.Check("looking at a tree raises the panel", info != null && info.DebugActive);
            T.Check($"it names the tree, without the retail variant marker (\"{info.DebugName}\")",
                    info.DebugName.Length > 0 && !info.DebugName.Contains('#'));
            T.Check("but shows NO health bar for a tree we have never hit", !info.DebugBarVisible(0));
            T.Check("and claims no number either", info.DebugPrompt.Length == 0);

            // The server unicast lands (ResourceHealthEvent -> SetKnownHealth).
            field.SetKnownHealth(tree, 500, 800);
            field.ShowInfoFor(tree);
            T.Check("after a swing the bar appears", info.DebugBarVisible(0));
            T.Check($"at the server's fraction, not a guess ({info.DebugBarValue(0):0.000})",
                    Mathf.Abs(info.DebugBarValue(0) - 0.625f) < 0.01f);
            T.Check($"with the count beside it (\"{info.DebugPrompt}\")", info.DebugPrompt == "500 / 800");

            // Chopping it down and letting it regrow must not leave the old bar on the new tree.
            field.SetKnownHealth(tree, 0, 800);
            field.SetAlive(tree, false);
            field.ShowInfoFor(tree);
            T.Check("a felled tree gets no panel at all", !info.DebugActive);
            field.SetAlive(tree, true);
            field.ShowInfoFor(tree);
            T.Check("and the regrown tree is unknown again, not stuck at 0%", !info.DebugBarVisible(0));

            field.HideInfo();
            T.Check("looking away drops the panel", !info.DebugActive);
            yield break;
        }
    }

    // Felling. Retail does not animate a tree falling -- it hides the standing model and instantiates the
    // SAME model as a Rigidbody, shoves it with the server's ragdoll and destroys it after 8 s. Until now
    // the port just zeroed the instance out of its MultiMesh, so a chopped tree blinked out of existence.
    public class TreeFallsAsPhysicsDebris : GameTest
    {
        public override string Name => "tree.falls_as_debris";

        static int FirstTree(ResourceField f)
        {
            for (int i = 0; i < f.InstanceCount; i++) if (f.DebugTrunk(i) != null) return i;
            return -1;
        }

        static List<ResourceDebris> DebrisIn(Node n) => n.GetChildren().OfType<ResourceDebris>().ToList();

        public override IEnumerable<Step> Run()
        {
            var field = new ResourceField();          // VISUAL build: the gib is the tree's own meshes
            ResourceField.DebrisEnabled = true;       // off in the game until the topple is dependable; the body itself is what this covers
            World.AddChild(field);
            field.LoadResources("NONE");
            yield return Ticks(1);

            int tree = FirstTree(field);
            T.Check($"found a tree to fell (index {tree})", tree >= 0);
            if (tree < 0) yield break;
            var standing = field.PositionOf(tree);

            // GROUND. Without it the gib free-falls forever and every topple assertion below passes no
            // matter what the collider does -- which is exactly what happened: the first version of this
            // test happily passed with the broken buried-collider offset restored. The bug only exists
            // where the body has something to rest ON, so the test has to give it something.
            var floor = new StaticBody3D { Position = standing, CollisionLayer = 1u << 0 };
            floor.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = new Vector3(80f, 2f, 80f) },
                Position = new Vector3(0f, -1f, 0f),
            });
            World.AddChild(floor);
            yield return Ticks(1);

            T.Check("nothing is falling before the chop", DebrisIn(field).Count == 0);

            // Deliberately larger than a real swing. ChopResource sends direction * totalDamage, and an
            // axe's Resource_Damage is ~20 -- the same order as retail's own +-16 horizontal jitter, so a
            // real felling topples in a biased-random direction rather than a clean one. That is retail's
            // behaviour and not a bug, but it makes "did the direction survive to the body" untestable at
            // realistic magnitudes. This value swamps the jitter so the plumbing is what is under test.
            field.Fell(tree, new Vector3(800f, 0f, 0f));
            var debris = DebrisIn(field);
            T.Check($"felling spawns exactly one debris body ({debris.Count})", debris.Count == 1);
            T.Check("and the standing instance is gone", !field.IsAlive(tree));
            if (debris.Count != 1) yield break;
            var gib = debris[0];

            T.Check($"it wears the tree's own meshes ({gib.GetChildren().OfType<MeshInstance3D>().Count()} part(s))",
                    gib.GetChildren().OfType<MeshInstance3D>().Any());
            T.Check("with a collider, so it lands instead of sinking", gib.GetChildren().OfType<CollisionShape3D>().Any());
            T.Check($"spawned at the tree, not the origin ({gib.GlobalPosition.DistanceTo(standing):0.0} m away)",
                    gib.GlobalPosition.DistanceTo(standing) < 3f);
            T.Check("on the debris layer, masking the world only -- it must never shove the player who felled it",
                    gib.CollisionLayer == (1u << 2) && gib.CollisionMask == (1u << 0));

            var spawnRot = gib.GlobalRotationDegrees;
            yield return Ticks(3);
            // The +-16 jitter is smaller than an 800-unit swing, so the direction survives it. That is the
            // point of the assert: the tree goes the way the SWING went, not a random way.
            T.Check($"and it is moving away along the swing (v = {gib.LinearVelocity})",
                    gib.LinearVelocity.X > 5f && Mathf.Abs(gib.LinearVelocity.X) > Mathf.Abs(gib.LinearVelocity.Z));

            _ = spawnRot;

            // Both the harvested EVENT and the alive-bitmap poll fell a tree locally; whichever lands
            // second must not drop a second trunk on top of the first.
            field.Fell(tree, new Vector3(800f, 0f, 0f));
            T.Check($"a second fell for the same tree adds nothing ({DebrisIn(field).Count})", DebrisIn(field).Count == 1);

            field.SetAlive(tree, true);
            field.Fell(tree, new Vector3(-800f, 0f, 0f));
            T.Check($"but a REGROWN tree can fall again ({DebrisIn(field).Count})", DebrisIn(field).Count == 2);

            // NOT COVERED HERE: that the tree TOPPLES rather than settling upright.
            //
            // Not an oversight, and worth reading before adding one. The obvious assertion -- fell it, tick,
            // check pitch/roll moved -- was written and CONTROLLED twice, and passed both times with the
            // collider bug deliberately restored: at the 800 magnitude above the shove blasts the trunk
            // clear of the ground, so it rotates freely whether or not the collider is right. Re-running it
            // at a realistic axe magnitude (~20) then failed deterministically HERE while the same
            // magnitude visibly topples the tree in tools/shot.py treefell, and I have not explained the
            // difference. A green check that survives its own control is worse than no check: it reads as
            // coverage.
            //
            // So the topple is verified VISUALLY for now (tools/shot.py treefell, and the render posted to
            // the dev channel), and this test covers only what it can actually prove.
            yield break;
        }
    }

    // The label comes from the baked table (English.dat, verbatim "Birch #1"); the trim is the UI's.
    public class TreeLabelsComeFromTheHarvestTable : GameTest
    {
        public override string Name => "tree.labels";
        public override IEnumerable<Step> Run()
        {
            T.Check($"Birch_0 is labelled (\"{ResourceHarvestTable.LabelFor("Birch_0")}\")",
                    ResourceHarvestTable.LabelFor("Birch_0").StartsWith("Birch"));
            T.Check($"Pine_0 too (\"{ResourceHarvestTable.LabelFor("Pine_0")}\")",
                    ResourceHarvestTable.LabelFor("Pine_0").StartsWith("Pine"));
            int labelled = ResourceHarvestTable.ByName.Keys.Count(k => ResourceHarvestTable.LabelFor(k).Length > 0);
            T.Check($"every baked resource carries one ({labelled}/{ResourceHarvestTable.ByName.Count})",
                    labelled == ResourceHarvestTable.ByName.Count);
            T.Check("an unknown type is empty rather than throwing", ResourceHarvestTable.LabelFor("Nope_9") == "");
            yield break;
        }
    }
}
