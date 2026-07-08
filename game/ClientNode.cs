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
        float _t, _fireCd;
        readonly Dictionary<byte, MeshInstance3D> _avatars = new();
        readonly List<MeshInstance3D> _zombieAvatars = new();

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

            // server-authoritative zombies (green), pooled
            var zs = Client.Zombies;
            while (_zombieAvatars.Count < zs.Count)
            {
                var zav = new MeshInstance3D
                {
                    Mesh = new CapsuleMesh { Height = 1.8f, Radius = 0.4f },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.55f, 0.30f) },
                };
                AddChild(zav);
                _zombieAvatars.Add(zav);
            }
            for (int i = 0; i < _zombieAvatars.Count; i++)
            {
                _zombieAvatars[i].Visible = i < zs.Count;
                if (i < zs.Count) _zombieAvatars[i].Position = new Vector3(zs[i].X, zs[i].Y, zs[i].Z);
            }

            // auto-fire at the nearest zombie; the SERVER does the authoritative hit-reg + removes it.
            _fireCd -= (float)delta;
            if (_fireCd <= 0f && zs.Count > 0)
            {
                var eye = p + new Vector3(0f, 1.4f, 0f);
                int best = -1; float bd = float.MaxValue;
                for (int i = 0; i < zs.Count; i++)
                {
                    float d = new Vector3(zs[i].X, zs[i].Y, zs[i].Z).DistanceTo(eye);
                    if (d < bd) { bd = d; best = i; }
                }
                var target = new Vector3(zs[best].X, zs[best].Y + 0.6f, zs[best].Z);
                var dir = (target - eye).Normalized();
                Client.SendFire(eye.X, eye.Y, eye.Z, dir.X, dir.Y, dir.Z);
                _fireCd = 0.16f;
            }
        }
    }
}
