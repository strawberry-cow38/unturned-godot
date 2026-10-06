using Godot;
using System.Text.Json;
using System.Linq;

namespace UnturnedGodot
{
    public partial class Viewmodel
    {
        internal static bool IsMossbergGun(string name) => name == "bluntforce" || name == "bluntforce_wood";
        MeshInstance3D _mossbergPump;
        float[][] _mossbergPumpKeys;
        float _mossbergPumpTime = -1f, _mossbergPumpSpeed = 1f;
        Node3D _mossbergShell;
        float _mossbergInsertTime = -1f, _mossbergInsertSpeed = 1f;
        public bool ReloadIsSingleShell => IsMossbergGun(GunName) && _reloadClip == "Bluntforce_Reload_OneShell";
        void BuildMossbergPump(MeshInstance3D body, StandardMaterial3D material)
        {
            if (!IsGunViewmodel || !IsMossbergGun(GunName)) return;
            var mesh = ContentProvider.ParseObj("res://content/bluntforce_pump.txt");
            if (mesh == null) return;
            // Wood body uses a widened atlas; the grey pump must retain its original atlas/UV pairing.
            var pumpMaterial = material;
            if (GunName == "bluntforce_wood")
            {
                pumpMaterial = (StandardMaterial3D)material.Duplicate();
                pumpMaterial.AlbedoTexture = LoadTex("res://content/bluntforce_albedo.png");
                pumpMaterial.AlbedoColor = Colors.White;
            }
            _mossbergPump = new MeshInstance3D { Name = "MossbergPump", Mesh = mesh, MaterialOverride = pumpMaterial };
            body.AddChild(_mossbergPump);
            var hand = new BoneAttachment3D { Name = "MossbergReloadHand", BoneName = "Left_Hand" };
            _arms.Skeleton.AddChild(hand);
            _mossbergShell = new Node3D { Name = "SingleReloadShell", Position = new Vector3(-.32f, 0f, -.09f), Rotation = new Vector3(0f, 0f, Mathf.Pi/2f), Visible = false };
            hand.AddChild(_mossbergShell);
            _mossbergShell.AddChild(new MeshInstance3D { Name = "RedHull", Mesh = new CylinderMesh { Height=.055f, TopRadius=.01f, BottomRadius=.01f, RadialSegments=6, Rings=1 }, MaterialOverride=new StandardMaterial3D { AlbedoColor=new Color(.55f,.10f,.08f), Roughness=1f, MetallicSpecular=0f } });
            _mossbergShell.AddChild(new MeshInstance3D { Name = "BrassRim", Position=new Vector3(0,-.027f,0), Mesh = new CylinderMesh { Height=.007f, TopRadius=.0105f, BottomRadius=.0105f, RadialSegments=6, Rings=1 }, MaterialOverride=new StandardMaterial3D { AlbedoColor=new Color(.65f,.49f,.20f), Roughness=1f, MetallicSpecular=0f } });
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/mossberg_pump_action.json")));
            _mossbergPumpKeys = doc.RootElement.GetProperty("Bluntforce_Hammer").GetProperty("keys").EnumerateArray()
                .Select(k => new[] { k.GetProperty("time").GetSingle(), k.GetProperty("localGunYDelta").GetSingle() }).ToArray();
        }
        void BeginMossbergInsert(bool on, float speed)
        {
            if (_mossbergShell == null) return;
            _mossbergInsertTime = on ? 0f : -1f; _mossbergInsertSpeed = speed; _mossbergShell.Visible = false;
        }
        void StartMossbergPump(float speed)
        {
            if (_mossbergPump == null) return;
            _mossbergPumpTime = 0f; _mossbergPumpSpeed = speed;
        }
        void TickMossbergPump(double delta)
        {
            if (_mossbergShell != null && _mossbergInsertTime >= 0f)
            {
                _mossbergInsertTime += (float)delta * _mossbergInsertSpeed;
                _mossbergShell.Visible = _mossbergInsertTime >= .3f && _mossbergInsertTime < .818f;
            }
            if (_mossbergPump == null || _mossbergPumpTime < 0f) return;
            _mossbergPumpTime += (float)delta * _mossbergPumpSpeed;
            _mossbergPump.Position = new Vector3(0, SampleSks(_mossbergPumpKeys, _mossbergPumpTime)[0], 0);
            if (_mossbergPumpTime >= HammerLength) { _mossbergPumpTime = -1f; _mossbergPump.Position = Vector3.Zero; }
        }
        public bool MossbergShellVisibleForTest => _mossbergShell?.Visible == true;
        public float MossbergPumpOffsetForTest => _mossbergPump?.Position.Y ?? 0f;
    }
}
