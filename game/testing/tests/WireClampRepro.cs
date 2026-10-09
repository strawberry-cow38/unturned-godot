using System.Collections.Generic;
using Godot;
using SDG.Unturned;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // REPRO (not for main): the wire clamps player XZ to [-1024, 1024) -- WriteClampedFloat's range is 1 << (intBits-1),
    // and NetQuantization's comment says 11 bits is "+-2048". PEI's terrain runs to +-2048. Does a JOINED player standing
    // past 1024 m get recorded on the server where they actually are? Control: the same teleport to -1000.
    public sealed class WireClampRepro : GameTest
    {
        public override string Name => "net.wire_clamp_repro";
        public override double TimeoutSimSeconds => 60;

        Step Wait(System.Func<bool> c, double seconds) { int n = 0, max = (int)(seconds * 50); return Until(() => c() || ++n >= max, seconds + 1); }

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt", syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            Rigs.Ground(World);
            var net = new MemNetwork(20261009);
            world.Sim.Sim.Add(new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump"));
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "far" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            yield return Wait(() => sess.Shell != null && sess.Shell.NetFineVitalsAdopted, 8);
            T.Check("joined", sess.Shell != null);
            if (sess.Shell == null) yield break;
            ushort pid = sess.Client.PlayerId;
            // envelope OFF so a teleport claim adopts verbatim: nothing between the client's word and the server's record
            // but the wire. (In real play you WALK there, in steps the envelope accepts -- same claims, same wire.)
            ded.Server.PlayerHost.DisableEnvelope = true;

            foreach (var x in new[] { -1000f, -1200f, 1500f, -1000f })
            {
                sess.Shell.TeleportTo(new Vector3(x, 1f, 0f));
                yield return Ticks(100);
                ded.Server.Players.TryGetByOwner(pid, out var e);
                var cp = sess.Shell.GlobalPosition;
                Log.Print($"[wireclamp] target x {x}: client stands at x {cp.X:0.00}, server records x {e?.Pos.x:0.00}");
                T.Check($"target {x}: server records the client's x ({e?.Pos.x:0.00} vs client {cp.X:0.00})",
                    e != null && Mathf.Abs(e.Pos.x - cp.X) < 0.5f);
            }
        }
    }
}
