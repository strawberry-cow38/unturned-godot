using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Multiplayer CLIENT: local player = a real PlayerController (ported movement), synced to the server.
    // Renders every entity as a blocky HUMANOID (players + zombies) instead of capsules, and fires at the
    // nearest zombie's HEAD so the server's per-zone hit-reg (headshot 3x) shows. Feet-based positions.
    public partial class ClientNode : Node3D
    {
        public NetClient Client;

        PlayerController _player;
        Label _hud;
        float _fireCd;
        readonly Dictionary<byte, Node3D> _avatars = new();
        readonly List<Node3D> _zombieAvatars = new();

        static readonly Color Skin = new Color(0.85f, 0.70f, 0.55f);

        public override void _Ready()
        {
            _player = new PlayerController { CaptureMouse = false };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(0f, 1f, 0f);
            _player.Camera.Current = false; // overview camera (BuildClient) is the demo view

            var layer = new CanvasLayer();
            _hud = new Label { Position = new Vector2(24, 22) };
            _hud.AddThemeFontSizeOverride("font_size", 22);
            layer.AddChild(_hud);
            AddChild(layer);
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

            if (nearest >= 0)
            {
                var flat = new Vector3(zs[nearest].X, me.Y, zs[nearest].Z);
                if (flat.DistanceTo(me) > 0.3f) _player.LookAt(flat, Vector3.Up);
            }
            _player.ScriptedInput = new UnityEngine.Vector2(0.5f, bd < 7f ? -0.7f : 0.15f);

            // send FEET position (the humanoid + the server hitboxes are feet-based)
            Client.SendState(new PlayerState { X = me.X, Y = me.Y, Z = me.Z, Yaw = _player.Rotation.Y });
            Client.Poll();

            // fire at the nearest zombie's HEAD -> server per-zone hit-reg (headshot). damage ~Eaglefire.
            _fireCd -= (float)delta;
            if (_fireCd <= 0f && nearest >= 0)
            {
                var cam = _player.Camera;
                Vector3 o = cam.GlobalPosition;
                Vector3 head = new Vector3(zs[nearest].X, zs[nearest].Y + 1.6f, zs[nearest].Z);
                Vector3 dir = (head - o).Normalized();
                Client.SendFire(o.X, o.Y, o.Z, dir.X, dir.Y, dir.Z, 40f);
                _fireCd = 0.22f;
            }

            // players as humanoids (blue = self via SelfId, orange = remote)
            foreach (var kv in Client.Remote)
            {
                if (!_avatars.TryGetValue(kv.Key, out var av))
                {
                    av = kv.Key == Client.SelfId
                        ? Humanoid.Build(Skin, new Color(0.25f, 0.45f, 0.85f), new Color(0.15f, 0.20f, 0.35f))
                        : Humanoid.Build(Skin, new Color(0.90f, 0.50f, 0.15f), new Color(0.40f, 0.28f, 0.15f));
                    AddChild(av);
                    _avatars[kv.Key] = av;
                }
                av.Position = new Vector3(kv.Value.X, kv.Value.Y, kv.Value.Z);
                av.Rotation = new Vector3(0f, kv.Value.Yaw, 0f);
            }

            // zombies as green humanoids, pooled
            while (_zombieAvatars.Count < zs.Count)
            {
                var zh = Humanoid.Build(new Color(0.50f, 0.60f, 0.45f), new Color(0.30f, 0.45f, 0.28f), new Color(0.25f, 0.32f, 0.22f));
                AddChild(zh);
                _zombieAvatars.Add(zh);
            }
            for (int i = 0; i < _zombieAvatars.Count; i++)
            {
                _zombieAvatars[i].Visible = i < zs.Count;
                if (i < zs.Count) _zombieAvatars[i].Position = new Vector3(zs[i].X, zs[i].Y, zs[i].Z);
            }

            _hud.Text = $"MULTIPLAYER   ·   players {Client.Remote.Count}   ·   horde {zs.Count}   ·   zombies killed {Client.Kills}";
        }
    }
}
