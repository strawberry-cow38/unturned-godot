using Godot;

namespace UnturnedGodot
{
    // Source-accurate first-person viewmodel: Unturned renders the viewmodel in a SEPARATE camera at
    // FOV 60 (PreferenceData Field_Of_View_Hip = 60). We reproduce that with an isolated SubViewport +
    // its own 60deg camera, composited on top of the main view (also fixes wall-clipping). The arms are
    // the arms-only character mesh playing the weapon's Equip hold clip; the gun is parented to the
    // Right_Hook (source-exact hand position) with the barrel aimed down the viewmodel-forward.
    public partial class Viewmodel : Node3D
    {
        public const float SourceFov = 60f;   // PreferenceData.cs:93 Field_Of_View_Hip

        SubViewport _vp;
        Camera3D _cam;
        RiggedCharacter _arms;
        Node3D _gun;
        CanvasLayer _layer;
        Vector3 _armsPos = new Vector3(0.22f, -1.75f, 0.12f);  // right-handed lower viewmodel
        float _gunRoll = 0f;
        float _recoil;
        double _t;

        public override void _Ready()
        {
            _vp = new SubViewport
            {
                OwnWorld3D = true,
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                HandleInputLocally = false,
            };
            _vp.Size = (Vector2I)GetViewport().GetVisibleRect().Size;
            AddChild(_vp);

            _cam = new Camera3D { Fov = SourceFov, Current = true };
            _vp.AddChild(_cam);
            _vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-40f, -25f, 10f), LightEnergy = 1.2f });
            _vp.AddChild(new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = new Color(0.62f, 0.62f, 0.64f),
                    AmbientLightEnergy = 0.9f,
                }
            });

            _arms = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f), armsOnly: true);
            if (_arms != null)
            {
                _cam.AddChild(_arms);
                _arms.Position = _armsPos;
                _arms.Play("Gun_Equip");   // raise -> holds the two-handed rifle stance

                var skel = _arms.Skeleton;
                int hb = skel.FindBone("Right_Hook");
                if (hb < 0) hb = skel.FindBone("Right_Hand");
                if (hb >= 0)
                {
                    var att = new BoneAttachment3D { Name = "GunAttach" };
                    skel.AddChild(att);
                    att.BoneName = skel.GetBoneName(hb);
                    var mi = new MeshInstance3D { Mesh = ContentProvider.ParseObj("res://content/eaglefire_gun.txt") };
                    var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, Metallic = 0f, Roughness = 0.6f };
                    var tex = LoadTex("res://content/eaglefire_albedo.png");
                    if (tex != null) mat.AlbedoTexture = tex; else mat.AlbedoColor = new Color(0.24f, 0.24f, 0.26f);
                    mi.MaterialOverride = mat;
                    att.AddChild(mi);
                    _gun = mi;
                }
            }

            // Composite the viewmodel viewport on top of the main view.
            _layer = new CanvasLayer { Layer = 5 };
            var tr = new TextureRect { Texture = _vp.GetTexture(), StretchMode = TextureRect.StretchModeEnum.Scale };
            tr.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _layer.AddChild(tr);
            AddChild(_layer);
        }

        public void Kick() { _recoil = Mathf.Min(1f, _recoil + 0.7f); }

        public void SetShown(bool shown) { if (_layer != null) _layer.Visible = shown; }

        public override void _Process(double delta)
        {
            if (_arms == null || _cam == null) return;
            _t += delta;
            _recoil = Mathf.Max(0f, _recoil - (float)delta * 5f);
            var sway = new Vector3(Mathf.Sin((float)_t * 1.4f) * 0.004f, Mathf.Sin((float)_t * 2.2f) * 0.003f, 0f);
            _arms.Position = _armsPos + sway + new Vector3(0f, 0.01f, 0.05f) * _recoil;

            if (_gun != null && _gun.GetParent() is Node3D att)
            {
                Vector3 aim = -_cam.GlobalTransform.Basis.Z;   // viewmodel-forward
                aim = aim.Rotated(_cam.GlobalTransform.Basis.X, Mathf.DegToRad(_recoil * 6f)).Normalized(); // muzzle rise
                Vector3 x = Vector3.Up.Cross(aim);
                if (x.LengthSquared() < 1e-5f) x = Vector3.Right;
                x = x.Normalized();
                var basis = new Basis(x, aim, x.Cross(aim).Normalized());   // barrel (+Y) -> aim
                basis = basis.Rotated(aim, Mathf.DegToRad(_gunRoll));
                _gun.GlobalTransform = new Transform3D(basis, att.GlobalPosition);
            }
        }

        static Texture2D LoadTex(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }
    }
}
