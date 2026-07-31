using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // Drops used to land within 120 m of the world origin because the port never loaded the map's
    // airdrop nodes -- they live in Level.hierarchy, not the Nodes.dat the existing extractor reads, so
    // "nodes.tsv has no airdrops" looked like "this map has none". These pin the fix at the level that
    // would actually regress: what PickTarget RETURNS, not whether the file parses.
    public class AirdropNodesLoaded : GameTest
    {
        public override string Name => "airdrop.nodes_loaded";
        public override IEnumerable<Step> Run()
        {
            var nodes = MapNodes.AirdropNodes;
            T.Check($"PEI's 14 airdrop nodes loaded (got {nodes.Count})", nodes.Count == 14);

            // Spread across the island, not clustered. The old behaviour put every drop inside 120 m of
            // the origin, so "at least one node well outside that" is the thing that was broken.
            int farOut = nodes.Count(n => n.X * n.X + n.Z * n.Z > 400f * 400f);
            T.Check($"nodes are spread over the map ({farOut} of {nodes.Count} beyond 400 m from origin)", farOut >= 8);
            yield break;
        }
    }

    public class AirdropPicksAMapNode : GameTest
    {
        public override string Name => "airdrop.picks_a_map_node";
        public override IEnumerable<Step> Run()
        {
            var field = new AirdropField();     // no Terr: Grounded passes the authored height through
            World.AddChild(field);
            yield return Ticks(1);

            var nodes = MapNodes.AirdropNodes;
            var seen = new HashSet<int>();
            bool allOnNodes = true;
            for (int i = 0; i < 60; i++)
            {
                var t = field.PickTarget();
                int hit = nodes.FindIndex(n => Mathf.Abs(n.X - t.x) < 0.01f && Mathf.Abs(n.Z - t.z) < 0.01f);
                if (hit < 0) { allOnNodes = false; T.Check($"target ({t.x:0}, {t.z:0}) is not any node", false); break; }
                seen.Add(hit);
            }
            T.Check("every rolled target lands on an authored node", allOnNodes);
            // Uniform random over 14 nodes, 60 rolls: seeing only a couple would mean it is not really
            // choosing. Loose bound -- this is checking it is not pinned, not the RNG's distribution.
            T.Check($"it actually varies ({seen.Count} distinct nodes in 60 rolls)", seen.Count >= 5);
        }
    }

    public class AirdropTargetOnceIsConsumed : GameTest
    {
        public override string Name => "airdrop.target_once_is_consumed";
        public override IEnumerable<Step> Run()
        {
            var field = new AirdropField();
            World.AddChild(field);
            yield return Ticks(1);

            // The console's summon verbs set this. If it were sticky instead of one-shot, typing
            // `airdrop` once would nail every later SCHEDULED drop to that spot for the rest of the
            // session -- a bug you would not notice until you wondered why drops stopped moving.
            field.TargetOnce = new UnityEngine.Vector3(123f, 0f, -456f);
            var first = field.PickTarget();
            T.Check($"the summoned spot is used ({first.x:0}, {first.z:0})",
                    Mathf.Abs(first.x - 123f) < 0.01f && Mathf.Abs(first.z + 456f) < 0.01f);

            var second = field.PickTarget();
            T.Check($"and only once -- the next drop goes back to the nodes ({second.x:0}, {second.z:0})",
                    Mathf.Abs(second.x - 123f) > 0.01f || Mathf.Abs(second.z + 456f) > 0.01f);
        }
    }
}
