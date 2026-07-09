using Godot;
using SDG.Unturned;   // EPlayerStance (bob speed/amplitude are stance-driven)

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
        // Source-accurate viewmodel-camera motion (PlayerAnimator): the walk BOB (viewmodelMovementOffset,
        // Rk4Spring2) + the per-shot recoil SHAKE (recoilViewmodelCameraOffset, Rk4Spring3), both applied to
        // the viewmodel camera's local position. Stiffness/damping are Inspector-serialized on the Player
        // prefab in the original (not in the scripts) -> tuned here; the motion + amplitudes are source-exact.
        Rk4Spring2 _bobSpring = new Rk4Spring2(900f, 60f);   // tracks the Sin(speed*t) target cleanly + eases stop
        Rk4Spring3 _shakeSpring = new Rk4Spring3(550f, 40f); // snappy kick, settles ~0.2s (slight overshoot)
        bool _moving;                       // player has movement input this frame (drives bob on/off)
        EPlayerStance _stance = EPlayerStance.STAND;   // STAND/SPRINT/CROUCH/PRONE -> bob speed + amplitude
        float _blendedSway = 1f;            // blendedViewmodelSwayMultiplier: 1 hip -> 0.1 aim, eased at 16/s
        bool _reloading;      // true while the Gun_Reload clip plays (blocks ADS)
        Node3D _muzzleFlash;  // brief flash light + spark at the muzzle on fire
        float _flash;
        AudioStreamPlayer _shootSnd, _reloadSnd, _drySnd;   // real Eaglefire Shoot/Reload/Hammer(dry-fire) sounds
        // Case ejection (master-requested feel add 2026-07-08 — the vanilla Eaglefire has no Shell effect, so this
        // is non-vanilla): a generic 5.56 casing (yellow rectangle cube) tossed from the gun's Eject hook each shot,
        // arcing out to the right + tumbling under gravity, then despawning. Lives in the viewmodel viewport world.
        Node3D _ejectHook;
        BoxMesh _casingMesh;
        StandardMaterial3D _casingMat;
        readonly System.Collections.Generic.List<Casing> _casings = new();
        readonly RandomNumberGenerator _rng = new();
        sealed class Casing { public MeshInstance3D Node; public Vector3 Vel; public Vector3 Spin; public float Life; }

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
        // Per-gun viewmodel visuals: body + sight meshes, albedo, and the sight's ADS "Aim" hook (extracted from
        // each gun's sight.prefab; source: GetAimingViewmodelAlignment). Unturned assault rifles share the Sight
        // hook + Military_30 mag + FX hooks, so only these differ. Set GunName before the node enters the tree
        // (_Ready builds the gun). Aim hooks: Eaglefire SightHook(0,-0.2398,0.1386)+Model_0(0,0.371,-0.0206)+
        // Aim(0,-0.6,0.0918) -> port (0,-0.4688,-0.2098); Maplestrike Aim(0,-0.57,0.1111) -> port (0,-0.4388,-0.2291).
        public string GunName = "eaglefire";
        struct GunVisual { public string Gun, Sight, Albedo; public Vector3 AimHook; }
        static GunVisual Visual(string name) => name switch
        {
            "maplestrike" => new GunVisual { Gun = "maplestrike_gun.txt", Sight = "maplestrike_iron_sights.txt", Albedo = "maplestrike_albedo.png", AimHook = new Vector3(0f, -0.4388f, -0.2291f) },
            _             => new GunVisual { Gun = "eaglefire_gun.txt",   Sight = "eaglefire_iron_sights.txt",   Albedo = "eaglefire_albedo.png",   AimHook = new Vector3(0f, -0.4688f, -0.2098f) },
        };
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
                _arms.SetClipLoop("Gun_Reload", false);  // reload plays ONCE (the clip returns the hands to ready)
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
                    var gv = Visual(GunName);
                    var mi = new MeshInstance3D { Mesh = ContentProvider.ParseObj($"res://content/{gv.Gun}") };
                    var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, Metallic = 0f, Roughness = 0.6f };
                    var tex = LoadTex($"res://content/{gv.Albedo}");
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
                    var ironMesh = ContentProvider.ParseObj($"res://content/{gv.Sight}");
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

                    // ADS anchor marker at the sight's real Aim hook (gv.AimHook, per-gun) — ADS slides the arms so this
                    // lands on the camera axis, i.e. you look straight through the aperture.
                    _sight = new Node3D { Name = "AimHook" };
                    mi.AddChild(_sight);
                    _sight.Position = gv.AimHook;

                    // muzzle flash = the REAL Muzzle_0 effect (ID 3; the Eaglefire.dat has Muzzle 3), extracted from
                    // core.masterbundle: a warm point light (Unity color (0.94,0.76,0.15), intensity 1.37 — NOT the old
                    // energy 5 that washed the frame) + a brief BILLBOARD star-flash sprite (the real 32x32 Muzzle_0
                    // texture, size ~0.5 per startSize, additive), flashed ~0.05s on fire.
                    _muzzleFlash = new Node3D { Name = "MuzzleFlash", Position = new Vector3(0f, 0.75f, -0.04f), Visible = false };
                    _muzzleFlash.AddChild(new OmniLight3D { OmniRange = 4.0f, LightColor = new Color(0.941f, 0.756f, 0.152f), LightEnergy = 1.4f });
                    var flashMat = new StandardMaterial3D
                    {
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    };
                    var flashTex = LoadTex("res://content/muzzleflash.png");
                    if (flashTex != null) flashMat.AlbedoTexture = flashTex; else flashMat.AlbedoColor = new Color(1f, 0.85f, 0.4f);
                    _muzzleFlash.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(0.6f, 0.6f) }, MaterialOverride = flashMat });
                    // tracer: the Military_30 mag fires tracers (Tracer 48 = the Trail_0 effect). A brief bright streak
                    // down the barrel (+Y = aim), shown with the flash. Thin box from the muzzle extending downrange.
                    // tracer = the real Trail_0 effect (Military_30's Tracer 48): renderMode=Stretch, LengthScale 128,
                    // NO rotation module — a stretched billboard using the "Bullet" sprite. Ported as a long thin quad
                    // down the barrel (+Y) textured with the real 32x32 Bullet sprite (additive), shown with the flash.
                    var tracerMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                    var bulletTex = LoadTex("res://content/bullet.png");
                    if (bulletTex != null) tracerMat.AlbedoTexture = bulletTex; else tracerMat.AlbedoColor = new Color(1f, 0.9f, 0.5f);
                    _muzzleFlash.AddChild(new MeshInstance3D { Name = "Tracer", Mesh = new QuadMesh { Size = new Vector2(0.12f, 12f) }, MaterialOverride = tracerMat, Position = new Vector3(0f, 6f, 0f) });

                    // real gun sounds — the Eaglefire's Shoot/Reload AudioClips from the bundle (-> ogg). Non-3D
                    // AudioStreamPlayers output to the Master bus, so they're audible even though the gun lives in
                    // the viewmodel SubViewport (the player's own gun sound is non-positional anyway).
                    _shootSnd = new AudioStreamPlayer { Stream = LoadOgg("res://content/eaglefire_shoot.ogg"), VolumeDb = -3f };
                    mi.AddChild(_shootSnd);
                    _reloadSnd = new AudioStreamPlayer { Stream = LoadOgg("res://content/eaglefire_reload.ogg"), VolumeDb = -3f };
                    mi.AddChild(_reloadSnd);
                    _drySnd = new AudioStreamPlayer { Stream = LoadOgg("res://content/eaglefire_hammer.ogg"), VolumeDb = -3f };
                    mi.AddChild(_drySnd);
                    mi.AddChild(_muzzleFlash);

                    // Eject hook marker (gun Eject hook (0,0.0275,0.0814) -> port (0,0.0275,-0.0814)) + the shared
                    // casing mesh/material: a small yellow rectangle cube standing in for the 5.56 brass.
                    _ejectHook = new Node3D { Name = "EjectHook", Position = new Vector3(0f, 0.0275f, -0.0814f) };
                    mi.AddChild(_ejectHook);
                    _casingMesh = new BoxMesh { Size = new Vector3(0.009f, 0.009f, 0.028f) };
                    _casingMat = new StandardMaterial3D { AlbedoColor = new Color(0.96f, 0.79f, 0.15f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
                }
            }

            // Composite the viewmodel viewport on top of the main view.
            _layer = new CanvasLayer { Layer = 5 };
            var tr = new TextureRect { Texture = _vp.GetTexture(), StretchMode = TextureRect.StretchModeEnum.Scale };
            tr.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _layer.AddChild(tr);
            AddChild(_layer);
        }

        // Fire: muzzle flash + casing + sound, plus the source per-shot recoil SHAKE — add a random offset in
        // [shakeMin, shakeMax] per axis to the recoil viewmodel-camera spring (UseableGun.cs:921 -> AddRecoil
        // ViewmodelCameraOffset), which springs back to rest. STAND stance = 1x (crouch 0.85 / prone 0.7 handled
        // where fired). _recoil (kept) still drives the small muzzle-rise rotation flourish.
        public void Kick(Vector3 shakeMin, Vector3 shakeMax)
        {
            _recoil = Mathf.Min(1f, _recoil + 0.7f); _flash = 0.05f; EjectCasing(); _shootSnd?.Play();
            _shakeSpring.CurrentPosition += new Vector3(
                _rng.RandfRange(Mathf.Min(shakeMin.X, shakeMax.X), Mathf.Max(shakeMin.X, shakeMax.X)),
                _rng.RandfRange(Mathf.Min(shakeMin.Y, shakeMax.Y), Mathf.Max(shakeMin.Y, shakeMax.Y)),
                _rng.RandfRange(Mathf.Min(shakeMin.Z, shakeMax.Z), Mathf.Max(shakeMin.Z, shakeMax.Z)));
        }

        // Driven each physics frame by PlayerController: whether the player is moving + their stance, so the
        // walk bob uses the right frequency (SPEED_*) + amplitude (BOB_*) and switches off when standing still.
        public void SetLocomotion(bool moving, EPlayerStance stance) { _moving = moving; _stance = stance; }

        public void PlayDryFire() { _drySnd?.Play(); }   // hammer click when the trigger's pulled on empty

        // Toss a casing from the Eject hook: initial velocity = gun-right + up + slightly back (+ jitter), then it
        // arcs under gravity + tumbles (integrated in _Process). Parented to the viewport world so it flies free of
        // the gun. Non-vanilla for the Eaglefire (it has no Shell effect) — a visual feel add per master.
        void EjectCasing()
        {
            if (_ejectHook == null || _casingMesh == null || _vp == null || _gun == null) return;
            var node = new MeshInstance3D { Mesh = _casingMesh, MaterialOverride = _casingMat };
            _vp.AddChild(node);
            node.GlobalPosition = _ejectHook.GlobalPosition;
            node.Basis = _gun.GlobalTransform.Basis;                       // casing starts in the gun's orientation
            Basis cb = _cam.GlobalTransform.Basis;                         // camera: X=right, Y=up, -Z=forward
            Vector3 vel = cb.X * (2.1f + _rng.RandfRange(-0.3f, 0.3f))      // eject to the shooter's right
                        + cb.Y * (1.2f + _rng.RandfRange(-0.2f, 0.2f))      // up
                        - cb.Z * (0.5f + _rng.RandfRange(-0.2f, 0.2f));     // slightly forward, so it stays in view
            Vector3 spin = new Vector3(_rng.RandfRange(-18f, 18f), _rng.RandfRange(-18f, 18f), _rng.RandfRange(-18f, 18f));
            _casings.Add(new Casing { Node = node, Vel = vel, Spin = spin, Life = 0f });
        }

        // Hold RMB to aim (Unturned's default aiming mode). PlayerController drives this on RMB down/up.
        // Source gate: can't begin aiming until the equip pull-out is finished (IsEquipAnimationFinished).
        public void SetAiming(bool on) { if (on && !EquipDone) return; _aiming = on; }

        // Driven by PlayerController while reloading — the gun dips down as a simple reload gesture (the full
        // Gun_Reload clip is a TODO; it needs additive-layer integration like the aim pose). Can't ADS mid-reload.
        public void SetReloading(bool on)
        {
            _reloading = on;
            if (on) { _aiming = false; _arms?.Play("Gun_Reload"); _reloadSnd?.Play(); }   // real reload arm anim + sound
        }

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
            // ---- source viewmodel-camera motion (PlayerAnimator): walk bob + recoil shake ----
            // blendedViewmodelSwayMultiplier eases toward the sway target (1 hip -> 0.1 aiming) at 16/s.
            _blendedSway = Mathf.Lerp(_blendedSway, Mathf.Lerp(1f, 0.1f, _aimAlpha), 16f * (float)delta);
            // stance-driven bob frequency (SPEED_*) + amplitude (BOB_*), scaled by the sway multiplier.
            float bobSpeed = _stance switch { EPlayerStance.SPRINT => 10f, EPlayerStance.CROUCH => 6f, EPlayerStance.PRONE => 4f, _ => 8f };
            float bobAmp = (_stance switch { EPlayerStance.SPRINT => 0.075f, EPlayerStance.CROUCH => 0.025f, EPlayerStance.PRONE => 0.0125f, _ => 0.05f }) * _blendedSway;
            if (_moving)
            {
                float s = Mathf.Sin(bobSpeed * (float)_t) * bobAmp;   // horizontal sine; vertical = |horizontal| (double-freq dip)
                _bobSpring.TargetPosition = new Vector2(s, Mathf.Abs(s));
            }
            else _bobSpring.TargetPosition = Vector2.Zero;
            _bobSpring.Update((float)delta);
            _shakeSpring.TargetPosition = Vector3.Zero;   // recoil shake always springs back to rest
            _shakeSpring.Update((float)delta);
            // Bob + recoil shake as an ARMS offset. The source moves the viewmodel CAMERA; our arms are children
            // of that camera (rigid), so instead we move the arms by the NEGATIVE offset — the same on-screen sway
            // (camera fixed, arms move opposite). Godot arms-local == camera-local (scale 1). Source maps bob to
            // (horizontal, -vertical dip); the Eaglefire's negative shake Z becomes a +Z arms push = a back-punch
            // toward the viewer on each shot.
            Vector3 vmOffset = new Vector3(
                -(_bobSpring.CurrentPosition.X - _shakeSpring.CurrentPosition.X),
                 (_bobSpring.CurrentPosition.Y - _shakeSpring.CurrentPosition.Y),
                -_shakeSpring.CurrentPosition.Z);

            Vector3 hipPos = _armsPos;   // hip anchor; the ADS slide + bob/shake are added below
            _arms.Position = hipPos;     // set the hip pose first so the ADS sight measurement reads its hip position
            // SOURCE-EXACT ADS (GetAimingViewmodelAlignment): bring the sight's real Aim hook onto the camera
            // ORIGIN — the source parks the viewmodel camera AT the aim hook (InverseTransformPoint into the cam's
            // space, scaled by aim progress). No forced depth: the sight sits at its natural eye relief, so its
            // apparent size is exactly what the real model geometry gives.
            if (_aimAlpha > 0.0001f && _sight != null)
            {
                Vector3 mCam = _cam.ToLocal(_sight.GlobalPosition);   // aim hook, camera-local
                hipPos -= mCam * _aimAlpha;                           // slide arms so the aim hook -> camera origin
            }
            _arms.Position = hipPos + vmOffset;   // hip/ADS pose + bob + recoil shake (arms move opposite the source vm camera)
            // reload plays the real Gun_Reload clip (see SetReloading) — the base pose IS the reload motion, no dip.

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

            // integrate ejected casings: gravity + tumble in the viewport world, despawn after ~1.3s
            for (int i = _casings.Count - 1; i >= 0; i--)
            {
                var c = _casings[i];
                c.Life += (float)delta;
                c.Vel += Vector3.Down * 9.8f * (float)delta;
                c.Node.GlobalPosition += c.Vel * (float)delta;
                c.Node.RotateX(c.Spin.X * (float)delta);
                c.Node.RotateY(c.Spin.Y * (float)delta);
                c.Node.RotateZ(c.Spin.Z * (float)delta);
                if (c.Life > 1.3f) { c.Node.QueueFree(); _casings.RemoveAt(i); }
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

        static AudioStream LoadOgg(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            return System.IO.File.Exists(p) ? AudioStreamOggVorbis.LoadFromFile(p) : null;
        }
    }
}
