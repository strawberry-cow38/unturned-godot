using Godot;
using System.Collections.Generic;
using System.Text.Json;

namespace UnturnedGodot
{
    public partial class Viewmodel
    {
        // Visual action channels authored with the Sks_* skeletal clips. This
        // rifle alone has a fixed magazine and a complete reload including closure.
        MeshInstance3D _sksBolt;
        Node3D _sksClip;
        readonly Node3D[] _sksRounds = new Node3D[10];
        Dictionary<string, Dictionary<string, float[][]>> _sksActions;
        string _sksAction;
        float _sksTime, _sksSpeed = 1f;
        bool _capturePoseFrozen;
        public bool ReloadIncludesChambering => GunName == "sks" && _reloadClip == "Sks_Reload";

        // Authoring-only --vm seam: sample the real AnimationPlayer and action
        // channels at the same time, then render that pose without advancing it.
        public void CaptureAnimationPose(string action, float time)
        {
            if (_arms == null) return;
            _equipElapsed = _equipLen;
            _arms.SnapToEnd(_holdClip); _arms.Tick(0);
            switch (action)
            {
                case "reload": SetReloading(true); break;
                case "hammer": PlayHammer(); break;
                case "equip": _arms.Play(_holdClip); break;
                case "sprint": _safe = _sprinting = true; _arms.Play(_sprintStartClip); break;
                case "ads": _aiming = true; _aimT = 1f; _arms.AimBlend = 1f; break;
                default: throw new System.ArgumentException("No snapshot for " + action);
            }
            _arms.Tick(time);
            TickSksAction(time);
            _capturePoseFrozen = true;
        }

        void BuildSksAction(MeshInstance3D body, StandardMaterial3D material)
        {
            if (!IsGunViewmodel || GunName != "sks" || _reloadClip != "Sks_Reload") return;
            using var file = FileAccess.Open("res://content/sks_action_tracks.json", FileAccess.ModeFlags.Read);
            if (file == null) return;
            using var document = JsonDocument.Parse(file.GetAsText());
            _sksActions = new();
            foreach (var action in document.RootElement.EnumerateObject())
            {
                var channels = new Dictionary<string, float[][]>();
                foreach (var channel in action.Value.EnumerateObject())
                    if (channel.Value.ValueKind == JsonValueKind.Array)
                        channels[channel.Name] = channel.Value.Deserialize<float[][]>();
                _sksActions[action.Name] = channels;
            }
            var staticMesh = ContentProvider.ParseObj("res://content/sks_action_body.txt");
            var boltMesh = ContentProvider.ParseObj("res://content/sks_action_bolt.txt");
            if (staticMesh == null || boltMesh == null) return;
            body.Mesh = staticMesh;
            _sksBolt = new MeshInstance3D { Name = "SksCarrierAndRightHandle", Mesh = boltMesh, MaterialOverride = material };
            body.AddChild(_sksBolt);
            _sksClip = new Node3D { Name = "SksStripperClip", Visible = false };
            body.AddChild(_sksClip);
            var steel = new StandardMaterial3D { AlbedoColor = new Color(0.24f, 0.25f, 0.27f), Roughness = 0.65f };
            var brass = new StandardMaterial3D { AlbedoColor = new Color(0.64f, 0.43f, 0.16f), Roughness = 0.48f };
            var copper = new StandardMaterial3D { AlbedoColor = new Color(0.47f, 0.25f, 0.13f), Roughness = 0.55f };
            // Clip rail retains the cartridge rims; it never enters from below.
            _sksClip.AddChild(new MeshInstance3D {
                Name = "EmptyClipRail", Mesh = new BoxMesh { Size = new Vector3(0.015f, 0.003f, 0.127f) },
                Position = new Vector3(0f, -0.01f, -0.06f), MaterialOverride = steel });
            for (int side = -1; side <= 1; side += 2)
                _sksClip.AddChild(new MeshInstance3D {
                    Mesh = new BoxMesh { Size = new Vector3(0.002f, 0.007f, 0.127f) },
                    Position = new Vector3(side * 0.0065f, -0.0065f, -0.06f), MaterialOverride = steel });
            for (int i = 0; i < _sksRounds.Length; i++)
            {
                var round = new Node3D { Name = $"Round{i + 1:D2}" };
                _sksClip.AddChild(round);
                round.AddChild(new MeshInstance3D {
                    Mesh = new CylinderMesh { BottomRadius = 0.0057f, TopRadius = 0.0047f, Height = 0.033f, RadialSegments = 8 },
                    Position = new Vector3(0f, 0.009f, 0f), MaterialOverride = brass });
                round.AddChild(new MeshInstance3D {
                    Mesh = new CylinderMesh { BottomRadius = 0.0039f, TopRadius = 0f, Height = 0.018f, RadialSegments = 8 },
                    Position = new Vector3(0f, 0.0345f, 0f), MaterialOverride = copper });
                _sksRounds[i] = round;
            }
            GD.Print("[vm] SKS action: original carrier/right handle split; ten-round top-loading clip");
        }

        void StartSksAction(string action, float speed)
        {
            if (_sksBolt == null) return;
            _sksAction = action; _sksTime = 0f; _sksSpeed = speed;
        }

        void CancelSksAction()
        {
            if (_sksBolt == null) return;
            _sksAction = null; _sksBolt.Position = Vector3.Zero; _sksClip.Visible = false;
        }

        static float[] SampleSks(float[][] keys, float time, bool step = false, bool rotation = false)
        {
            int i = 0;
            while (i + 1 < keys.Length && keys[i + 1][0] <= time) i++;
            var a = keys[i]; var b = keys[Mathf.Min(i + 1, keys.Length - 1)];
            float u = step || a == b ? 0f : Mathf.Clamp((time - a[0]) / (b[0] - a[0]), 0f, 1f);
            if (rotation)
            {
                var q = new Quaternion(a[1], a[2], a[3], a[4]).Slerp(new Quaternion(b[1], b[2], b[3], b[4]), u);
                return new[] { q.X, q.Y, q.Z, q.W };
            }
            var result = new float[a.Length - 1];
            for (int j = 0; j < result.Length; j++) result[j] = Mathf.Lerp(a[j + 1], b[j + 1], u);
            return result;
        }

        void TickSksAction(double delta)
        {
            if (_sksAction == null || _sksBolt == null || !_sksActions.TryGetValue(_sksAction, out var tracks)) return;
            _sksTime += (float)delta * _sksSpeed;
            float t = _sksTime;
            _sksBolt.Position = new Vector3(0f, -SampleSks(tracks["bolt"], t)[0], 0f);
            if (_sksAction == "reload")
            {
                _sksClip.Visible = SampleSks(tracks["visible"], t, step: true)[0] > 0.5f;
                var p = SampleSks(tracks["clip_pos"], t);
                var q = SampleSks(tracks["clip_rot"], t, rotation: true);
                _sksClip.Position = new Vector3(p[0], p[1], p[2]);
                _sksClip.Quaternion = new Quaternion(q[0], q[1], q[2], q[3]);
                float strip = SampleSks(tracks["strip"], t)[0];
                for (int i = 0; i < _sksRounds.Length; i++)
                {
                    float z = -0.006f - i * 0.012f + strip * 0.12f;
                    _sksRounds[i].Position = new Vector3(0f, 0f, z);
                    _sksRounds[i].Visible = z < 0f;
                }
            }
            else _sksClip.Visible = false;
            float length = _sksAction == "reload" ? ReloadLength : HammerLength;
            if (t >= length) CancelSksAction();
        }

        public void DumpSksCapture(string path)
        {
            if (_sksBolt == null) return;
            int rounds = 0;
            foreach (var round in _sksRounds) if (_sksClip.Visible && round.Visible) rounds++;
            var state = new {
                action = _sksAction, clip_time = _sksTime,
                bolt_rearward_m = -_sksBolt.Position.Y,
                clip_visible = _sksClip.Visible, visible_rounds = rounds,
                clip_position = new[] { _sksClip.Position.X, _sksClip.Position.Y, _sksClip.Position.Z }
            };
            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
    }
}
