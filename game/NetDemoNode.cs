using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // In-process 2-player NETWORK demo: a real NetServer + two real NetClients over loopback UDP. Player A
    // (local, id 1) + player B (a bot, id 2) each send their position every physics tick; the authoritative
    // server broadcasts the world; we render a capsule per synced player id, positioned from what the LOCAL
    // client received back THROUGH THE SERVER. The orange capsule's position literally travelled
    // bot -> UDP -> server -> UDP -> local client -> render. Co-located in one process only so a single
    // frame captures both peers; the networking (sockets, NetPak, server authority) is genuine.
    public partial class NetDemoNode : Node3D
    {
        public NetServer Server;
        public NetClient Local;   // becomes server id 1 (connects first)
        public NetClient Bot;     // becomes server id 2

        float _t;
        readonly Dictionary<byte, MeshInstance3D> _avatars = new();

        public override void _PhysicsProcess(double delta)
        {
            _t += (float)delta;

            var localPos = new Vector3(Mathf.Cos(_t * 0.9f) * 4f, 1f, Mathf.Sin(_t * 0.9f) * 4f);
            var botPos = new Vector3(Mathf.Cos(_t * 0.7f + 3.14159f) * 6.5f, 1f, Mathf.Sin(_t * 0.7f + 3.14159f) * 6.5f);

            Local.SendState(new PlayerState { X = localPos.X, Y = localPos.Y, Z = localPos.Z, Yaw = _t });
            Bot.SendState(new PlayerState { X = botPos.X, Y = botPos.Y, Z = botPos.Z, Yaw = -_t });

            Server.Poll();
            Server.Broadcast();
            Local.Poll();
            Bot.Poll();

            foreach (var kv in Local.Remote)
            {
                if (!_avatars.TryGetValue(kv.Key, out var av))
                {
                    var color = kv.Key == 1 ? new Color(0.30f, 0.55f, 0.95f)   // "me" (blue)
                                            : new Color(0.95f, 0.55f, 0.20f);  // remote player (orange)
                    av = new MeshInstance3D
                    {
                        Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.4f },
                        MaterialOverride = new StandardMaterial3D { AlbedoColor = color },
                    };
                    AddChild(av);
                    _avatars[kv.Key] = av;
                }
                av.Position = new Vector3(kv.Value.X, kv.Value.Y, kv.Value.Z);
            }
        }
    }
}
