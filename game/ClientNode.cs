using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Udp;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Multiplayer demo CLIENT, re-founded on the Phase 1-3 stack: a NetWorldClient that joins a --server /
    // --dedicated host, sends MoveInput commands each 50 Hz tick, and renders EVERY player -- including
    // itself -- from the server's snapshots (server-authoritative round trip made visible; blue = self,
    // orange = remote). The local avatar is a puppet of the replica on purpose: prediction is Phase 4.
    public partial class ClientNode : Node3D
    {
        public string Host = "127.0.0.1";
        public ushort Port = 47872;

        NetWorldClient _client;
        Label _hud;
        readonly Dictionary<ushort, Node3D> _avatars = new();

        static readonly Color Skin = new Color(0.85f, 0.70f, 0.55f);

        public override void _Ready()
        {
            _client = new NetWorldClient(new UdpClientTransport(Host, Port), "player");
            _client.Connect();

            var layer = new CanvasLayer();
            _hud = new Label { Position = new Vector2(24, 22) };
            _hud.AddThemeFontSizeOverride("font_size", 22);
            layer.AddChild(_hud);
            AddChild(layer);

            var sim = new SimDriver();
            AddChild(sim);
            sim.Sim.Add(new DelegateSimStep((tick, dt) => _client.SendMoveInput(0f, 1f, (float)(tick * dt) * -30f), "client.input"));   // walk a circle, opposite the server bot
            sim.Sim.Add(new DelegateSimStep((t, dt) => _client.Tick(), "client.session"));
        }

        public override void _Process(double delta)
        {
            foreach (var e in _client.Players.All)
            {
                if (!_avatars.TryGetValue(e.OwnerPlayerId, out var av))
                {
                    var tint = e.OwnerPlayerId == _client.PlayerId ? new Color(0.60f, 0.72f, 1.00f) : new Color(1.00f, 0.72f, 0.45f);
                    av = CharacterModel.Loaded
                        ? CharacterModel.Build(tint)
                        : Humanoid.Build(Skin, tint, tint * 0.6f);
                    AddChild(av);
                    _avatars[e.OwnerPlayerId] = av;
                }
                av.Position = new Vector3(e.Pos.x, e.Pos.y, e.Pos.z);   // feet-based, like the humanoid
                av.Rotation = new Vector3(0f, Mathf.DegToRad(e.YawDegrees), 0f);
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
        }

        public override void _ExitTree() => _client?.Disconnect();
    }
}
