using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Multiplayer demo CLIENT, re-founded on the Phase 1-3 stack: a NetWorldClient that joins a --server /
    // --dedicated host, sends MoveInput commands each 50 Hz tick, and renders every player (blue = self,
    // orange = remote). Phase 4: the LOCAL avatar is PREDICTED -- each input integrates immediately
    // through the same sim-core the server runs (ClientPrediction), and snapshot acks smooth-correct the
    // residual (§2.5b). Remote avatars stay replica puppets.
    public partial class ClientNode : Node3D
    {
        public string Host = "127.0.0.1";
        public ushort Port = 47872;

        NetWorldClient _client;
        Label _hud;
        Label _desyncLabel;
        string _desyncAlert = "";
        readonly Dictionary<ushort, Node3D> _avatars = new();

        static readonly Color Skin = new Color(0.85f, 0.70f, 0.55f);

        public override void _Ready()
        {
            // net diagnostics (hardening Part B) -- same toggle as the server: UG_NETLOG=1 or --netlog
            NetLog.Sink = s => GD.Print(s);
            NetLog.ErrorSink = s => GD.PrintErr(s);
            if (System.Environment.GetEnvironmentVariable("UG_NETLOG") == "1") NetLog.Enabled = true;

            _client = new NetWorldClient(new UdpClientTransport(Host, Port), "player", contentHash: NetContent.Hash);
            // desync detection (hardening Part C): the server hashes the mirrored systems into the
            // snapshot; a confirmed replica mismatch lands here -- log loudly + banner the player
            _client.DesyncDetected += report =>
            {
                GD.PrintErr($"[CLIENT] DESYNC DETECTED -- {report}");
                _desyncAlert = $"!! DESYNC detected (system {report.SystemId} @ tick {report.ServerTick}) -- state may be out of sync";
            };
            _client.Connect();
            // Phase 6: mirror the replicated deployable graph as real nodes (the local PowerSolver pass
            // lights the lamps, §3.1), and gate console cheats through the server (§2.3).
            DeployableNetSchema.RegisterAll(_client.Deployables.Schema);
            CropNetSchema.RegisterAll(_client.Crops.Schema);   // Phase 8 (§3.7): growth stages derive from the synced defs + snapshot tick
            AddChild(new DeployableReplicaView { Client = _client });
            AddChild(new VehicleReplicaView { Client = _client });   // Phase 7: server vehicles render as dead-reckoned puppets (§3.6)
            DevConsole.RemoteClient = _client;

            var layer = new CanvasLayer();
            _hud = new Label { Position = new Vector2(24, 22) };
            _hud.AddThemeFontSizeOverride("font_size", 22);
            layer.AddChild(_hud);
            _desyncLabel = new Label { Position = new Vector2(24, 54), Modulate = new Color(1f, 0.35f, 0.30f) };
            _desyncLabel.AddThemeFontSizeOverride("font_size", 20);
            layer.AddChild(_desyncLabel);
            AddChild(layer);

            var sim = new SimDriver();
            AddChild(sim);
            sim.Sim.Add(new DelegateSimStep((tick, dt) =>
            {
                // walk a circle, opposite the server bot; predict the result immediately under the sent seq
                float yaw = (float)(tick * dt) * -30f;
                ushort seq = _client.SendMoveInput(0f, 1f, yaw);
                _client.Prediction.PredictAndRecord(seq, 0f, 1f, yaw, (float)dt);
            }, "client.input"));
            sim.Sim.Add(new DelegateSimStep((t, dt) => _client.Tick(), "client.session"));
        }

        public override void _Process(double delta)
        {
            foreach (var e in _client.Players.All)
            {
                bool self = e.OwnerPlayerId == _client.PlayerId;
                if (!_avatars.TryGetValue(e.OwnerPlayerId, out var av))
                {
                    var tint = self ? new Color(0.60f, 0.72f, 1.00f) : new Color(1.00f, 0.72f, 0.45f);
                    av = CharacterModel.Loaded
                        ? CharacterModel.Build(tint)
                        : Humanoid.Build(Skin, tint, tint * 0.6f);
                    AddChild(av);
                    _avatars[e.OwnerPlayerId] = av;
                }
                // self renders from the PREDICTION (immediate, smooth-corrected); remotes from the replica
                var pos = self && _client.Prediction.Spawned ? _client.Prediction.Pos : e.Pos;
                float yawDeg = self && _client.Prediction.Spawned ? _client.Prediction.YawDegrees : e.YawDegrees;
                av.Position = new Vector3(pos.x, pos.y, pos.z);   // feet-based, like the humanoid
                av.Rotation = new Vector3(0f, Mathf.DegToRad(yawDeg), 0f);
            }

            if (_avatars.Count > _client.Players.Count)   // a player left -> free the stale avatar
            {
                var live = new HashSet<ushort>();
                foreach (var e in _client.Players.All) live.Add(e.OwnerPlayerId);
                var stale = new List<ushort>();
                foreach (var kv in _avatars) if (!live.Contains(kv.Key)) stale.Add(kv.Key);
                foreach (var id in stale) { _avatars[id].QueueFree(); _avatars.Remove(id); }
            }

            _hud.Text = $"MULTIPLAYER   ·   {_client.State}   ·   players {_client.Players.Count}   ·   applied tick {_client.Applier.LastAppliedServerTick}";
            _desyncLabel.Text = _desyncAlert;
        }

        public override void _ExitTree()
        {
            if (DevConsole.RemoteClient == _client) DevConsole.RemoteClient = null;
            _client?.Disconnect();
        }
    }
}
