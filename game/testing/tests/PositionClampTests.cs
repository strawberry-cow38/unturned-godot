using System.Collections.Generic;
using Godot;
using SDG.Unturned;
using SDG.NetTransport.Mem;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // POSITIONS PAST 1 KM (strawberry/cow tools 2026-10-09). The wire clamped player XZ to [-1024, 1024): the int bits
    // include the sign, and NetQuantization's comment said 11 of them was +-2048. PEI's terrain runs to +-2048 and the
    // bridge walks you out to x -1360, so a JOINED player standing out there was recorded on the server pinned to the
    // line while their own client walked on -- everyone else watched them stand still. Through a real joined client,
    // because the client's own position never showed it.
    public sealed class PositionClampTests : GameTest
    {
        public override string Name => "net.position_clamp_covers_the_map";
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
            // but the wire. (In play you WALK there, in steps the envelope accepts -- same claims, same wire.)
            ded.Server.PlayerHost.DisableEnvelope = true;

            // -1000 is the CONTROL: inside the old clamp, so it must pass on both builds -- if it fails, the teleport or the
            // envelope is what broke, not the range. -1200 / +1500 are the bridge and the far coast; +-2040 the map's edge.
            foreach (var (x, z) in new[] { (-1000f, 0f), (-1200f, 0f), (1500f, 0f), (-2040f, 2040f), (2040f, -2040f) })
            {
                sess.Shell.TeleportTo(new Vector3(x, 1f, z));
                yield return Ticks(100);
                ded.Server.Players.TryGetByOwner(pid, out var e);
                var cp = sess.Shell.GlobalPosition;
                // the server record is UNITY-space (z flipped): compare magnitudes per axis against the client's own
                bool ok = e != null && Mathf.Abs(e.Pos.x - cp.X) < 0.5f && Mathf.Abs(Mathf.Abs(e.Pos.z) - Mathf.Abs(cp.Z)) < 0.5f;
                T.Check($"standing at ({cp.X:0}, {cp.Z:0}), the server records ({e?.Pos.x:0.00}, {e?.Pos.z:0.00})", ok);
            }
        }
    }
}
