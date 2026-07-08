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

        // ADS (aim down sights) — source: hold RMB to aim; blend over Aim_In_Duration with a
        // smootherstep-squared ease (UseableGun.GetInterpolatedAimAlpha). Eaglefire Aim_In_Duration = 0.25s.
        // Iron sights do NOT zoom the FOV (startAim -> enableZoom(1.0) for a scopeless gun in first person);
        // ADS just raises the gun's sight onto the view axis (GetAimingViewmodelAlignment centers the aimHook
        // + a +0.45 eye-raise that cancels the hip drop) and cuts sway to 0.1x (viewmodelSwayMultiplier).
        public const float AimInDuration = 0.25f;   // Eaglefire.dat Aim_In_Duration
        // The View hook is the REAR sight, so aligning it exactly to the eye (source: camera moves to the hook)
        // parks the breech in your face. Unturned's own model/viewmodel geometry makes that read fine; ours
        // needs a small forward readability offset so you look DOWN the sights instead of at the breech.
        const float AdsSightDepth = -0.30f;
        bool _aiming;
        float _aimT;       // 0..1 aim-accuracy ramp over AimInDuration seconds
        float _aimAlpha;   // eased blend (hip 0 -> ADS 1)
        // ADS aligns the gun's real "View" hook onto the camera's aim axis (source: GetAimingViewmodelAlignment
        // takes the aimHook world pos into the viewmodel-camera space). The View hook = the Eaglefire's actual
        // aim transform, pulled from the model (collection I:13): gun-local (0, -0.7706, 0.1337) in Unity, Z
        // negated to Godot. We attach a marker there and move the arms so that marker lands on-axis at eye level.
        static readonly Vector3 ViewHookLocal = new Vector3(0f, -0.7706487f, -0.1337f);
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
                    _sight = new Node3D { Name = "ViewHook" };   // the gun's aim point, from the real model
                    mi.AddChild(_sight);
                    _sight.Position = ViewHookLocal;

                    // Modeled iron sights on the gun mesh frame (up = -z, barrel = +y). Front post at the muzzle +
                    // a rear aperture ring on the receiver, both on the sight line so ADS lines them up. The eye
                    // viewpoint (ViewHookLocal, behind at the same height) looks through the ring at the post.
                    var ironMat = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.05f, 0.06f), Metallic = 0.5f, Roughness = 0.4f };
                    var front = new MeshInstance3D { Name = "FrontSight", Mesh = new BoxMesh { Size = new Vector3(0.013f, 0.013f, 0.13f) }, MaterialOverride = ironMat, Position = new Vector3(0f, 0.5f, -0.15f) };
                    mi.AddChild(front);
                    var rear = new MeshInstance3D { Name = "RearSight", Mesh = new TorusMesh { InnerRadius = 0.028f, OuterRadius = 0.045f, RingSegments = 16 }, MaterialOverride = ironMat, Position = new Vector3(0f, -0.1f, -0.15f) };
                    mi.AddChild(rear);
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

        // Hold RMB to aim (Unturned's default aiming mode). PlayerController drives this on RMB down/up.
        // Source gate: can't begin aiming until the equip pull-out is finished (IsEquipAnimationFinished).
        public void SetAiming(bool on) { if (on && !EquipDone) return; _aiming = on; }

        // Driven by PlayerController while reloading — the gun dips down as a simple reload gesture (the full
        // Gun_Reload clip is a TODO; it needs additive-layer integration like the aim pose). Can't ADS mid-reload.
        public void SetReloading(bool on) { _reloading = on; if (on) _aiming = false; }

        public void SetShown(bool shown) { if (_layer != null) _layer.Visible = shown; }

        public override void _Process(double delta)
        {
            if (_arms == null || _cam == null) return;
            _t += delta;
            _equipElapsed += (float)delta;
            _recoil = Mathf.Max(0f, _recoil - (float)delta * 5f);
            // aim-in/out ramp (AimInDuration seconds) + the source smootherstep-squared ease
            _aimT = Mathf.Clamp(_aimT + (_aiming ? 1f : -1f) * (float)delta / AimInDuration, 0f, 1f);
            _aimAlpha = AimEase(_aimT);
            _arms.AimBlend = _aimAlpha;
            _arms.Tick(delta);   // manual-advance the base anim, then layer the additive Aim_Start pose on top
            float swayMult = Mathf.Lerp(1f, 0.1f, _aimAlpha);   // startAim: viewmodelSwayMultiplier 1 -> 0.1
            var sway = new Vector3(Mathf.Sin((float)_t * 1.4f) * 0.004f, Mathf.Sin((float)_t * 2.2f) * 0.003f, 0f) * swayMult;
            Vector3 hipPos = _armsPos + sway + new Vector3(0f, 0.01f, 0.05f) * _recoil;
            _arms.Position = hipPos;
            // fine sight alignment: bring the gun's View hook onto the aim axis (x=0, y=0) at its NATURAL depth.
            // The additive Aim_Start pose (above) now sets the gross hand position; this just centers the sight
            // exactly (source: GetAimingViewmodelAlignment). No forced depth anymore.
            if (_aimAlpha > 0.0001f && _sight != null)
            {
                Vector3 mCam = _cam.ToLocal(_sight.GlobalPosition);
                Vector3 target = new Vector3(0f, 0f, AdsSightDepth);
                _arms.Position = hipPos + (target - mCam) * _aimAlpha;
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
