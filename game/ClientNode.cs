using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Rendering CLIENT: a NetClient to a SEPARATE dedicated-server process over UDP. Sends the local
    // player's (scripted) state, renders a capsule per player the server reports back. The remote player's
    // motion is what actually crossed the network between two OS processes. Runs under `godot -- --client`.
    public partial class ClientNode : Node3D
    {
        public NetClient Client;
        float _t;
        readonly Dictionary<byte, MeshInstance3D> _avatars = new();

        public override void _PhysicsProcess(double delta)
        {
            _t += (float)delta;
            var p = new Vector3(Mathf.Cos(_t * 0.9f + 1.0f) * 4f, 1f, Mathf.Sin(_t * 0.9f + 1.0f) * 4f);
            Client.SendState(new PlayerState { X = p.X, Y = p.Y, Z = p.Z, Yaw = _t });
            Client.Poll();

            foreach (var kv in Client.Remote)
            {
                if (!_avatars.TryGetValue(kv.Key, out var av))
                {
                    var color = kv.Key == 1 ? new Color(0.95f, 0.55f, 0.20f)   // id 1 = the server's bot (remote)
                                            : new Color(0.30f, 0.55f, 0.95f);  // id 2 = this client (local)
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
