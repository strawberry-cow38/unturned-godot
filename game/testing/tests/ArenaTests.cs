using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // The arena server's two visible promises (strawberry 2026-09-02): players land on the generated arena
    // ring instead of the map's spawns, and the match does not START until more than one player is on.
    //
    // The gate rides ServerCombat.PvPEnabled because that is the only thing the server can actually withhold
    // from a client that owns its own movement -- so "WAITING" means nobody can damage anybody, which is a
    // property a test can fail on. A gate that only printed a line would pass this test with the gate deleted.
    //
    // The ring points are placed 100 m out on purpose: the no-map fallback world spawns on a demo line at the
    // origin, so "the avatar is near a ring point" cannot be satisfied by the default path it is replacing.
    public class ArenaMatchGate : GameTest
    {
        public override string Name => "net.arena_match_gate";

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("dedicated world ready", world.Ready);

            var ring = new List<(Vector3 Pos, float Yaw)>
            {
                (new Vector3(100f, 0f, 100f), 0f),
                (new Vector3(-100f, 0f, 100f), 1.57f),
                (new Vector3(100f, 0f, -100f), 3.14f),
            };

            var net = new MemNetwork(4343);
            var a = new NetWorldClient(new MemClientTransport(net), "a", contentHash: NetContent.Hash);
            var b = new NetWorldClient(new MemClientTransport(net), "b", contentHash: NetContent.Hash);
            world.Sim.Sim.Add(new DelegateSimStep((t, dt) => { net.Tick(); a.Tick(); b.Tick(); }, "l1.arenapump"));

            var ded = new DedicatedServer
            {
                Driver = world.Sim, TransportOverride = new MemServerTransport(net),
                Arena = true, ArenaSpawns = ring, ArenaMinPlayers = 2,
            };
            World.AddChild(ded);

            // (a) an arena server with nobody on it is HELD, it does not default to live
            yield return Ticks(2);
            T.Check($"empty arena server starts WAITING (MatchLive={ded.MatchLive}, PvP={ded.Server.Combat.PvPEnabled})",
                    !ded.MatchLive && !ded.Server.Combat.PvPEnabled);

            // (b) ONE player is still not a match
            a.Connect();
            yield return Until(() => a.State == NetSessionState.Connected, 5);
            yield return Ticks(4);
            T.Check($"1 player connected: still WAITING ({ded.Server.Session.Peers.Count} peer, PvP={ded.Server.Combat.PvPEnabled})",
                    ded.Server.Session.Peers.Count == 1 && !ded.MatchLive && !ded.Server.Combat.PvPEnabled);

            // (c) the spawn came off the arena ring, not the fallback demo line at the origin
            yield return Until(() => a.Players.TryGetByOwner(a.PlayerId, out var _), 5);
            bool got = a.Players.TryGetByOwner(a.PlayerId, out var me);
            float best = 1e9f;
            if (got) foreach (var (p, _) in ring)
            {
                float dx = me.Pos.x - p.X, dz = me.Pos.z - p.Z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < best) best = d;
            }
            T.Check($"the avatar spawned on an arena ring point, not the origin (nearest ring point {best:0.0} m, |pos| {(got ? Mathf.Sqrt(me.Pos.x * me.Pos.x + me.Pos.z * me.Pos.z) : -1f):0.0} m)",
                    got && best < 3f);

            // (d) the SECOND player starts the match
            b.Connect();
            yield return Until(() => b.State == NetSessionState.Connected, 5);
            yield return Ticks(4);
            T.Check($"2 players connected: match LIVE and PvP armed ({ded.Server.Session.Peers.Count} peers, MatchLive={ded.MatchLive}, PvP={ded.Server.Combat.PvPEnabled})",
                    ded.Server.Session.Peers.Count == 2 && ded.MatchLive && ded.Server.Combat.PvPEnabled);

            // (e) and it goes back when it drops under the threshold -- one player left is an empty arena,
            //     not a victory, because there is no round/win logic here yet
            b.Disconnect();
            yield return Until(() => ded.Server.Session.Peers.Count == 1, 5);
            yield return Ticks(4);
            T.Check($"dropping under the threshold returns to WAITING (MatchLive={ded.MatchLive}, PvP={ded.Server.Combat.PvPEnabled})",
                    !ded.MatchLive && !ded.Server.Combat.PvPEnabled);

            a.Disconnect();
        }
    }
}
