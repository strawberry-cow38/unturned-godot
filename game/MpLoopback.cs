using Godot;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // SP-as-loopback-listen-server, behind --mploopback (MP_PLAN §4 Phase 4: "SP-loopback lands BEHIND A
    // FLAG: SP still defaults to the direct path until parity is proven"). The single-player world gains
    // an in-process NetWorldServer + NetWorldClient over MemTransport; the local PlayerController keeps
    // playing exactly as in direct SP -- its node IS the listen-server's authoritative avatar
    // (PlayerReplication.ServerDrive writes the shell's sim-core + real-collision result into the
    // replication entity, per §2.1 "prediction becomes a pass-through"), while the full wire path runs
    // underneath: MoveInput commands -> server -> snapshots -> replica + lastProcessedInputSeq acks.
    // Remote players joining this session (listen-server proper) render through RemotePlayers puppets.
    // Steps ride the world's SimRoot in §2.5 order, replication LAST.
    public partial class MpLoopback : Node
    {
        public PlayerController Player;   // the SP shell (WorldBuildResult.Player)
        public SimDriver Driver;          // the world's sim spine

        public MemNetwork Net { get; private set; }
        public NetWorldServer Server { get; private set; }
        public NetWorldClient Client { get; private set; }
        public RemotePlayers Remotes { get; private set; }
        public ZombieNetSync ZombieSync { get; private set; }
        public WorldItemNetSync WorldItemSync { get; private set; }

        public override void _Ready()
        {
            Net = new MemNetwork(seed: 1);   // in-process wire; seed irrelevant without fault injection
            Server = new NetWorldServer(new MemServerTransport(Net), contentHash: NetContent.Hash);
            Client = new NetWorldClient(new MemClientTransport(Net), "local", contentHash: NetContent.Hash);
            Client.Connect();
            Server.Combat.WorldRay = GodotWorldRay;   // Phase 5: remote joiners' server bullets stop at real world geometry
            // Phase 6 def tables (see DedicatedServer): remote joiners' place/wire/craft commands validate
            // against these; the LOCAL player keeps the direct SP paths (the listen-server IS the authority).
            DeployableNetSchema.RegisterAll(Server.Deployables.Schema);
            DeployableNetSchema.RegisterAll(Client.Deployables.Schema);
            Server.Transactions.Blueprints = BlueprintRegistry.All;

            Remotes = new RemotePlayers { Client = Client };
            AddChild(Remotes);

            Driver.Sim.Add(new DelegateSimStep((t, dt) => TickLocal((float)dt), "mp.loopback.local"));
            Driver.Sim.Add(new DelegateSimStep((t, dt) => Server.TickSimulation(), "net.server.sim"));
            // Phase 5: the world's real zombie brains publish into ZombieReplication at 12.5 Hz (§3.5) --
            // every loopback session soaks the zombie wire; the local view renders the brains directly
            // (no ZombiePuppets here -- puppets are for worlds that don't own the brains).
            ZombieSync = new ZombieNetSync(Server, this);
            Driver.Sim.Add(new DelegateSimStep((t, dt) => ZombieSync.Tick(), "net.zombies.publish"));
            // Phase 6: the loopback world's dropped/loot items publish as entities too (§3.3) -- every SP
            // session soaks the world-item wire the same way it soaks the zombie wire.
            WorldItemSync = new WorldItemNetSync(Server, this);
            Driver.Sim.Add(new DelegateSimStep((t, dt) => WorldItemSync.Tick(), "net.worlditems.publish"));
            Driver.Sim.Add(new DelegateSimStep((t, dt) => Server.TickReplication(), "net.server.replicate"));   // LAST (§2.5)
            GD.Print($"[MPLOOPBACK] listen-server up over MemTransport (content {NetContent.Hash:X16})");
        }

        bool GodotWorldRay(UnityEngine.Vector3 from, UnityEngine.Vector3 to, out UnityEngine.Vector3 point)
        {
            point = default;
            var world = GetViewport()?.World3D;
            if (world == null) return false;
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(from.x, from.y, from.z), new Vector3(to.x, to.y, to.z), (1u << 0) | (1u << 6));
            var hit = world.DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) return false;
            var p = (Vector3)hit["position"];
            point = new UnityEngine.Vector3(p.X, p.Y, p.Z);
            return true;
        }

        void TickLocal(float dt)
        {
            Net.Tick();
            Client.Tick();
            if (Client.State != NetSessionState.Connected || Player == null || !IsInstanceValid(Player)) return;

            // 1) the shell's captured input goes over the wire as this tick's MoveInput (held-keys model)
            float yaw = Player.RotationDegrees.Y;
            ushort seq = Client.SendMoveInput(Player.LastMoveInput.x, Player.LastMoveInput.y, yaw);

            // 2) the local node IS the authority (listen-server): write its sim-core + real-collision
            //    result into the replication entity; ServerStep skips externally-driven entities
            var pos = Player.GlobalPosition;
            Server.Players.ServerDrive(Client.PlayerId,
                new UnityEngine.Vector3(pos.X, pos.Y, pos.Z), yaw, seq, Server.Session.CurrentTick);

            // 3) prediction bookkeeping (pass-through in loopback, §2.1): record the shell's position under
            //    the sent seq; NetWorldClient.Tick reconciles it against the snapshot's
            //    lastProcessedInputSeq, closing the predict->ack loop end to end. The residual is
            //    quantization-sized and is NOT applied back to the node (the node IS the authority here) --
            //    remote-client shells consume corrections for real (ClientPrediction).
            if (seq != 0)
                Client.Prediction.Reconciler.Record(seq, new UnityEngine.Vector3(pos.X, pos.Y, pos.Z));
        }

        public override void _ExitTree()
        {
            Client?.Disconnect();
            Server?.TearDown();
        }
    }
}
