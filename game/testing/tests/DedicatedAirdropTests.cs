using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // A dedicated server ran ZERO airdrops. NetWorldHost skips the whole schedule while
    // PickAirdropTarget is null, and only MpLoopback (the listen-server host) ever set it -- so the
    // event silently did not exist in the one configuration that matters for a real server.
    //
    // The reason it survived is worth stating: the L0 net test for airdrops assigns
    // `h.Server.PickAirdropTarget = () => where` itself. It passes because it injects the exact thing
    // production was missing, which is a test proving the machinery works and nothing at all about
    // whether anyone starts it. This one asserts the WIRING.
    public class DedicatedServerSchedulesAirdrops : GameTest
    {
        public override string Name => "net.dedicated_airdrops_wired";
        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("dedicated world ready", world.Ready);

            var net = new MemNetwork(4243);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net) };
            World.AddChild(ded);
            yield return Ticks(2);

            // The regression itself: null here means no drop ever happens, and nothing logs a word.
            T.Check("the dedicated host has a target picker at all", ded.Server.PickAirdropTarget != null);
            if (ded.Server.PickAirdropTarget == null) yield break;

            // And that it picks map nodes rather than the origin scatter.
            var nodes = MapNodes.AirdropNodes;
            T.Check($"node table loaded ({nodes.Count})", nodes.Count > 0);
            bool allOnNodes = true;
            var seen = new HashSet<int>();
            for (int i = 0; i < 40; i++)
            {
                var t = ded.Server.PickAirdropTarget();
                int hit = nodes.FindIndex(n => Mathf.Abs(n.X - t.x) < 0.01f && Mathf.Abs(n.Z - t.z) < 0.01f);
                if (hit < 0) { allOnNodes = false; T.Check($"picked ({t.x:0}, {t.z:0}), not a node", false); break; }
                seen.Add(hit);
            }
            T.Check("every server-side pick lands on an authored node", allOnNodes);
            T.Check($"and it varies ({seen.Count} distinct in 40 picks)", seen.Count >= 4);
        }
    }
}
