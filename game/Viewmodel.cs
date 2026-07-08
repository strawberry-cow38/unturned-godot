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
        // Source-accurate: horizontal offset is ZERO (PlayerAnimator.cs:1653 base = Vector3.zero,
        // PreferenceData Offset_Horizontal defaults 0). The gun reads right-handed because the RIG holds
        // it in the right hand (lefties get localScale.x=-1, PlayerAnimator:1613 — a mirror, not a shift).
        // Y is the eye-alignment + the source -0.45 vertical drop (PlayerAnimator:1431, gun sits low).
        Vector3 _armsPos = new Vector3(0f, -1.75f, 0.12f);
        float _gunRoll = 0f;
        float _recoil;
        double _t;
        bool _reloading;      // reload gesture: dip the gun while reloading
        float _reloadBlend;   // eased 0..1
        Node3D _muzzleFlash;  // brief flash light + spark at the muzzle on fire
        float _flash;

        // ADS (aim down sights) — source: hold RMB to aim; blend over Aim_In_Duration with a
        // smootherstep-squared ease (UseableGun.GetInterpolatedAimAlpha). Eaglefire Aim_In_Duration = 0.25s.
        // Iron sights do NOT zoom the FOV (startAim -> enableZoom(1.0) for a scopeless gun in first person);
        // ADS just raises the gun's sight onto the view axis (GetAimingViewmodelAlignment centers the aimHook
        // + a +0.45 eye-raise that cancels the hip drop) and cuts sway to 0.1x (viewmodelSwayMultiplier).
        public const float AimInDuration = 0.25f;   // Eaglefire.dat Aim_In_Duration
        // ADS uses the real Aim-hook alignment (below), NO depth constant: the source (GetAimingViewmodelAlignment)
        // parks the viewmodel camera AT the sight's Aim hook, so eye relief + apparent sight size fall straight out
        // of the real model geometry — nothing tunable.
        bool _aiming;
        float _aimT;       // 0..1 aim-accuracy ramp over AimInDuration seconds
        float _aimAlpha;   // eased blend (hip 0 -> ADS 1)
        // ADS aligns the mounted iron sight's real "Aim" hook onto the camera's aim axis (source:
        // GetAimingViewmodelAlignment + Attachments.defaultAimHook = sightModelLOD0.Find("Aim")). Pulled from
        // core.masterbundle (UnityPy): eaglefire_iron_sights/sight.prefab mounted at the gun's Sight hook gives
        // Aim in gun space = SightHook(0,-0.2398,0.1386)+Model_0(0,0.371,-0.0206)+Aim(0,-0.6,0.0918) = (0,-0.4688,
        // 0.2098); converted to the port gun frame (x,y,z)->(-x,y,-z) = (0,-0.4688,-0.2098). Marker lands on-axis.
        static readonly Vector3 AimHookLocal = new Vector3(0f, -0.4688f, -0.2098f);
        Node3D _sight;

        // Equip gate — source: you can't start OR stop aiming until the Equip (pull-out) animation finishes
        // (UseableGun.ReceivePlayAimStart/Stop both guard on player.equipment.IsEquipAnimationFinished, which is
        // Time >= equipStart + GetAnimationLength("Equip"), PlayerEquipment.cs:269/1633). So SetAiming is ignored
        // while the gun is still raising.
        float _equipLen;       // Gun_Equip clip length (seconds)
        float _equipElapsed;   // time since the viewmodel spawned / equip started
        bool EquipDone => _equipLen <= 0f || _equipElapsed >= _equipLen;
        public bool IsEquipComplete => EquipDone;

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
                _arms.SetClipLoop("Gun_Equip", false);   // equip plays ONCE and holds the ready pose
                _arms.Play("Gun_Equip");                 // raise -> holds the two-handed rifle stance
                _equipLen = _arms.ClipLength("Gun_Equip");
                GD.Print($"[vm] equip (pull-out) length = {_equipLen:F3}s — aiming gated until then");

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
                    // Real Eaglefire_Iron_Sights model (item 5) — sight.prefab from core.masterbundle, extracted via
                    // UnityPy and converted to the port gun frame (x,y,z)->(-x,y,-z), same pipeline as the gun body.
                    // Mounted exactly as Attachments.cs does: Instantiate(sightAsset.sight) parented to the Sight hook
                    // at localPos 0 / localRot identity / localScale 1. The sight's Model_0 origin therefore sits at
                    // SightHook(0,-0.2398,0.1386)+Model_0(0,0.371,-0.0206) = (0,0.1312,0.118) -> port (0,0.1312,-0.118).
                    var sightMat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(0.06f, 0.06f, 0.07f), Metallic = 0.75f, Roughness = 0.35f };
                    var ironMesh = ContentProvider.ParseObj("res://content/eaglefire_iron_sights.txt");
                    if (ironMesh != null)
                        mi.AddChild(new MeshInstance3D { Name = "IronSights", Mesh = ironMesh, MaterialOverride = sightMat, Position = new Vector3(0f, 0.1312f, -0.118f) });

                    // Real default Magazine (item 6 = Military_30, GUID dbfb1d0d) — item.prefab Model_0 from
                    // core.masterbundle, converted (x,y,z)->(-x,y,-z). Mounted as Attachments.cs does
                    // (Instantiate(magazineAsset.magazine) at the Magazine hook, localPos 0 / identity); the mesh sits
                    // on the item root so its origin = MagazineHook(0,0.0166,-0.0238) -> port (0,0.0166,0.0238).
                    var magMat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(0.07f, 0.07f, 0.08f), Metallic = 0.3f, Roughness = 0.6f };
                    var magMesh = ContentProvider.ParseObj("res://content/eaglefire_mag.txt");
                    if (magMesh != null)
                        mi.AddChild(new MeshInstance3D { Name = "Magazine", Mesh = magMesh, MaterialOverride = magMat, Position = new Vector3(0f, 0.0166f, 0.0238f) });

                    // ADS anchor marker at the sight's real Aim hook (see AimHookLocal) — ADS slides the arms so this
                    // lands on the camera axis, i.e. you look straight through the aperture.
                    _sight = new Node3D { Name = "AimHook" };
                    mi.AddChild(_sight);
                    _sight.Position = AimHookLocal;

                    // muzzle flash: a warm light + an unshaded spark at the barrel end (+y), flashed briefly on fire
                    _muzzleFlash = new Node3D { Name = "MuzzleFlash", Position = new Vector3(0f, 0.75f, -0.04f), Visible = false };
                    _muzzleFlash.AddChild(new OmniLight3D { OmniRange = 1.6f, LightColor = new Color(1f, 0.82f, 0.45f), LightEnergy = 5f });
                    _muzzleFlash.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.03f, Height = 0.06f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.9f, 0.5f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } });
                    mi.AddChild(_muzzleFlash);
                }
            }

            // Composite the viewmodel viewport on top of the main view.
            _layer = new CanvasLayer { Layer = 5 };
            var tr = new TextureRect { Texture = _vp.GetTexture(), StretchMode = TextureRect.StretchModeEnum.Scale };
            tr.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _layer.AddChild(tr);
            AddChild(_layer);
        }

        public void Kick() { _recoil = Mathf.Min(1f, _recoil + 0.7f); _flash = 0.05f; }

        // Hold RMB to aim (Unturned's default aiming mode). PlayerController drives this on RMB down/up.
        // Source gate: can't begin aiming until the equip pull-out is finished (IsEquipAnimationFinished).
        public void SetAiming(bool on) { if (on && !EquipDone) return; _aiming = on; }

        // Driven by PlayerController while reloading — the gun dips down as a simple reload gesture (the full
        // Gun_Reload clip is a TODO; it needs additive-layer integration like the aim pose). Can't ADS mid-reload.
        public void SetReloading(bool on) { _reloading = on; if (on) _aiming = false; }

        public float AimAlpha => _aimAlpha;   // 0 hip .. 1 ADS, for spread/accuracy

        public void SetShown(bool shown) { if (_layer != null) _layer.Visible = shown; }

        public override void _Process(double delta)
        {
            if (_arms == null || _cam == null) return;
            _t += delta;
            _equipElapsed += (float)delta;
            _recoil = Mathf.Max(0f, _recoil - (float)delta * 5f);
            _flash = Mathf.Max(0f, _flash - (float)delta);
            if (_muzzleFlash != null) _muzzleFlash.Visible = _flash > 0f;
            // aim-in/out ramp (AimInDuration seconds) + the source smootherstep-squared ease
            _aimT = Mathf.Clamp(_aimT + (_aiming ? 1f : -1f) * (float)delta / AimInDuration, 0f, 1f);
            _aimAlpha = AimEase(_aimT);
            _arms.AimBlend = _aimAlpha;
            _arms.Tick(delta);   // manual-advance the base anim, then layer the additive Aim_Start pose on top
            float swayMult = Mathf.Lerp(1f, 0.1f, _aimAlpha);   // startAim: viewmodelSwayMultiplier 1 -> 0.1
            var sway = new Vector3(Mathf.Sin((float)_t * 1.4f) * 0.004f, Mathf.Sin((float)_t * 2.2f) * 0.003f, 0f) * swayMult;
            Vector3 hipPos = _armsPos + sway + new Vector3(0f, 0.01f, 0.05f) * _recoil;
            _arms.Position = hipPos;
            // SOURCE-EXACT ADS (GetAimingViewmodelAlignment): bring the sight's real Aim hook onto the camera
            // ORIGIN — the source parks the viewmodel camera AT the aim hook (InverseTransformPoint into the cam's
            // space, scaled by aim progress). No forced depth: the sight sits at its natural eye relief, so its
            // apparent size is exactly what the real model geometry gives.
            if (_aimAlpha > 0.0001f && _sight != null)
            {
                Vector3 mCam = _cam.ToLocal(_sight.GlobalPosition);   // aim hook, camera-local
                _arms.Position = hipPos - mCam * _aimAlpha;           // slide arms so the aim hook -> camera origin
            }
            // reload gesture: dip the gun down/in while reloading (simple visual; real Gun_Reload anim is a TODO)
            _reloadBlend = Mathf.MoveToward(_reloadBlend, _reloading ? 1f : 0f, (float)delta * 4f);
            if (_reloadBlend > 0.001f)
                _arms.Position += new Vector3(0.04f, -0.32f, 0.04f) * _reloadBlend;

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

        // UseableGun.GetInterpolatedAimAlpha ease: 1 - (1 - smootherStep01(t))^2
        static float AimEase(float t)
        {
            float s = Mathf.Clamp(t, 0f, 1f);
            s = s * s * s * (s * (s * 6f - 15f) + 10f);   // smootherStep01
            float inv = 1f - s;
            return 1f - inv * inv;
        }

        static Texture2D LoadTex(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }
    }
}
