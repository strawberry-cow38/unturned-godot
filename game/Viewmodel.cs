using Godot;

namespace UnturnedGodot
{
    // First-person viewmodel: the character rig (arms) in front of the camera, playing the equipped weapon's
    // hold animation (Eaglefire Equip_92 -> settles into the two-handed rifle hold), with the gun parented to
    // the right-hand bone. Idle sway + fire recoil. This is Unturned's approach: a character model driven by
    // the weapon's own clips, not a bespoke arms rig.
    public partial class Viewmodel : Node3D
    {
        RiggedCharacter _arms;
        Node3D _gun;
        // arms-only mesh: drop the rig so the shoulders sit well below the camera and the arms + gun read
        // as a proper lower viewmodel (not filling the screen).
        Vector3 _armsPos = new Vector3(-0.15f, -1.75f, 0.15f);
        Vector3 _armsRotDeg = new Vector3(0f, 0f, 0f);
        // gun seated on the Right_Hook (item attach point) -- Unturned mounts items there with identity local.
        // Source mount: Eaglefire Item.prefab root has m_LocalRotation -90deg X (Model_0 identity under it),
        // i.e. guns mount on the hook rotated -90 X in Unity -> +90 X in Godot (z-flip).
        Vector3 _gunPos = new Vector3(0f, 0f, 0f);
        Vector3 _gunRotDeg = new Vector3(-90f, 0f, 90f);   // source Euler(0,0,90) + the -90 root-convention correction
        float _recoil;
        double _t;
        float _gunRoll = 0f;   // roll about the barrel for magazine-down (tuned by render)

        public override void _Ready()
        {
            _arms = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f), armsOnly: true);
            if (_arms == null) { GD.PrintErr("[vm] arms rig failed"); return; }
            AddChild(_arms);
            _arms.Position = _armsPos;
            _arms.RotationDegrees = _armsRotDeg;
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
                mi.Position = _gunPos;
                mi.RotationDegrees = _gunRotDeg;
                att.AddChild(mi);
                _gun = mi;
            }
        }

        public void Kick() { _recoil = Mathf.Min(1f, _recoil + 0.7f); }

        public override void _Process(double delta)
        {
            if (_arms == null) return;
            _t += delta;
            _recoil = Mathf.Max(0f, _recoil - (float)delta * 5f);
            var sway = new Vector3(Mathf.Sin((float)_t * 1.4f) * 0.004f, Mathf.Sin((float)_t * 2.2f) * 0.003f, 0f);
            var kick = new Vector3(0f, 0.01f, 0.05f) * _recoil;
            Position = sway + kick;
            // Gun: source-exact POSITION (the hand hook it's parented to), oriented barrel-forward in view-space.
            // (The hook-relative source mount Euler(0,0,90) can't transfer directly -- my rig bridges the masterbundle
            //  mesh with the resources clips via a -90 root fix, offsetting the hook frame from Unity's.)
            if (_gun != null && _gun.GetParent() is Node3D att)
            {
                var cam = GetViewport().GetCamera3D();
                Vector3 aim = cam != null ? -cam.GlobalTransform.Basis.Z : Vector3.Forward; // where the player looks
                aim = aim.Rotated(cam != null ? cam.GlobalTransform.Basis.X : Vector3.Right, Mathf.DegToRad(_recoil * 6f)).Normalized(); // muzzle rise
                Vector3 x = Vector3.Up.Cross(aim);
                if (x.LengthSquared() < 1e-5f) x = Vector3.Right;
                x = x.Normalized();
                var basis = new Basis(x, aim, x.Cross(aim).Normalized());   // gun mesh barrel (+Y) -> aim
                basis = basis.Rotated(aim, Mathf.DegToRad(_gunRoll));        // roll for magazine-down
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
