using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Multiplayer CLIENT: the local player is a REAL PlayerController (the ported movement physics), whose
    // state syncs to the dedicated server every tick. It kites the nearest zombie and fires -> the server
    // does authoritative hit-reg. Renders every networked player (blue = self via SelfId, orange = remote)
    // + the server's zombies. For the recorded demo the player is driven by ScriptedInput; run interactively
    // (--client with a human) it's real WASD/mouse. Third-person overview camera comes from BuildClient.
    public partial class ClientNode : Node3D
    {
        public NetClient Client;

        PlayerController _player;
        float _fireCd;
        readonly Dictionary<byte, MeshInstance3D> _avatars = new();
        readonly List<MeshInstance3D> _zombieAvatars = new();

        public override void _Ready()
        {
            _player = new PlayerController { CaptureMouse = false };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(0f, 1f, 0f);
            _player.Camera.Current = false; // overview camera (BuildClient) is the demo view
        }

        public override void _PhysicsProcess(double delta)
        {
            var zs = Client.Zombies;
            Vector3 me = _player.GlobalPosition;

            int nearest = -1; float bd = float.MaxValue;
            for (int i = 0; i < zs.Count; i++)
            {
                float d = new Vector3(zs[i].X, zs[i].Y, zs[i].Z).DistanceTo(me);
                if (d < bd) { bd = d; nearest = i; }
            }

            // face + kite the nearest zombie with the REAL movement (strafe, back off when it's close)
            if (nearest >= 0)
            {
                var flat = new Vector3(zs[nearest].X, me.Y, zs[nearest].Z);
                if (flat.DistanceTo(me) > 0.3f) _player.LookAt(flat, Vector3.Up);
            }
            _player.ScriptedInput = new UnityEngine.Vector2(0.5f, bd < 7f ? -0.7f : 0.15f);

            // sync my state to the server, then apply the world it sends back
            Client.SendState(new PlayerState { X = me.X, Y = me.Y, Z = me.Z, Yaw = _player.Rotation.Y });
            Client.Poll();

            // fire at the nearest zombie -> server hit-reg
            _fireCd -= (float)delta;
            if (_fireCd <= 0f && nearest >= 0)
            {
                var cam = _player.Camera;
                Vector3 o = cam.GlobalPosition;
                Vector3 dir = -cam.GlobalTransform.Basis.Z;
                Client.SendFire(o.X, o.Y, o.Z, dir.X, dir.Y, dir.Z);
                _fireCd = 0.16f;
            }

            // render networked players (blue = me, orange = remote)
            foreach (var kv in Client.Remote)
            {
                if (!_avatars.TryGetValue(kv.Key, out var av))
                {
                    var color = kv.Key == Client.SelfId ? new Color(0.30f, 0.55f, 0.95f) : new Color(0.95f, 0.55f, 0.20f);
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

            // render the server's zombies (green), pooled
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
        }
    }
}
