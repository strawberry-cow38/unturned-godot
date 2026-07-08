using Godot;

namespace UnturnedGodot
{
    // First-person viewmodel: the equipped gun rendered in front of the camera, drawn on top so it never
    // clips into walls, with idle sway + fire recoil kick. Parented to the player camera.
    // (Next layers: the 1P arms mesh + Unturned's real per-weapon viewmodel clips (Aim/Reload/Idle_Hands).)
    public partial class Viewmodel : Node3D
    {
        MeshInstance3D _gun;
        Vector3 _basePos = new Vector3(0.20f, -0.25f, -0.48f);
        Vector3 _baseRotDeg = new Vector3(-90f, 13f, 3f);   // barrel forward (-Z), yawed so the side profile shows
        float _recoil;
        double _t;

        public override void _Ready()
        {
            var mesh = ContentProvider.ParseObj("res://content/eaglefire_gun.txt");
            _gun = new MeshInstance3D { Mesh = mesh, Scale = Vector3.One };
            var mat = new StandardMaterial3D
            {
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,  // gun mesh winding varies
                Metallic = 0.0f, Roughness = 0.6f,                // no env map -> metallic renders black
            };
            var tex = LoadTex("res://content/eaglefire_albedo.png");
            if (tex != null) mat.AlbedoTexture = tex; else mat.AlbedoColor = new Color(0.24f, 0.24f, 0.26f);
            GD.Print($"[vm] albedo loaded={tex != null} size={tex?.GetSize()}");
            _gun.MaterialOverride = mat;
            _gun.Position = _basePos;
            _gun.RotationDegrees = _baseRotDeg;
            AddChild(_gun);
        }

        public void Kick() { _recoil = Mathf.Min(1f, _recoil + 0.7f); }

        public override void _Process(double delta)
        {
            if (_gun == null) return;
            _t += delta;
            _recoil = Mathf.Max(0f, _recoil - (float)delta * 5f);
            var sway = new Vector3(Mathf.Sin((float)_t * 1.4f) * 0.004f, Mathf.Sin((float)_t * 2.2f) * 0.003f, 0f);
            var kick = new Vector3(0f, 0.012f, 0.055f) * _recoil;      // back + slightly up
            _gun.Position = _basePos + sway + kick;
            _gun.RotationDegrees = _baseRotDeg + new Vector3(-_recoil * 7f, 0f, 0f); // muzzle rise
        }

        static Texture2D LoadTex(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }
    }
}
