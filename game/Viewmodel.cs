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
        // Live-tunable viewmodel FOV + a SINGLE uniform offset for ALL guns (master: remove the per-gun offsets + uniform
        // them). Driven by the ESC pause-menu sliders; applied every frame in _Process so tweaks are instant.
        public static float TuneFov = SourceFov;
        public static Vector3 TuneOffset = Vector3.Zero;

        SubViewport _vp;
        DirectionalLight3D _vpLight;                 // the viewmodel's own sun -- synced to the world's each frame
        DirectionalLight3D _vpFill1, _vpFill2;       // readability fill lights -- scaled with world brightness so the gun darkens at night
        Godot.Environment _vpEnv;                    // the viewmodel's ambient -- synced to the world's
        public DirectionalLight3D WorldSun;          // set by the game so the FP gun takes the world's day/night light
        public Godot.Environment WorldEnv;
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
        double _t;
        // Source-accurate viewmodel-camera motion (PlayerAnimator): the walk BOB (viewmodelMovementOffset,
        // Rk4Spring2) + the per-shot recoil SHAKE (recoilViewmodelCameraOffset, Rk4Spring3), both applied to
        // the viewmodel camera's local position. Stiffness/damping are Inspector-serialized on the Player
        // prefab in the original (not in the scripts) -> tuned here; the motion + amplitudes are source-exact.
        Rk4Spring2 _bobSpring = new Rk4Spring2(900f, 60f);   // tracks the Sin(speed*t) target cleanly + eases stop
        Rk4Spring3 _shakeSpring = new Rk4Spring3(550f, 40f); // positional kick, settles ~0.2s (slight overshoot)
        Rk4Spring3 _recoilRotSpring = new Rk4Spring3(550f, 40f); // per-shot gun tilt (pitch/yaw/roll deg), springs back
        bool _moving;                       // player has movement input this frame (drives bob on/off)
        EPlayerStance _stance = EPlayerStance.STAND;   // STAND/SPRINT/CROUCH/PRONE -> bob speed + amplitude
        float _blendedSway = 1f;            // blendedViewmodelSwayMultiplier: 1 hip -> 0.1 aim, eased at 16/s
        bool _reloading;      // true while the reload clip plays (blocks ADS)
        string _reloadClip = "Gun_Reload";   // per-gun reload clip ({Gun}_Reload), set in _Ready; falls back to Gun_Reload
        string _inspectClip = null;          // per-gun inspect clip ({Gun}_Inspect); null if the gun ships no Inspect anim
        bool _inspecting; float _inspectTimer; Basis _inspectBoneStart; bool _inspectCapture;   // inspect: layer the hand-bone rotation delta onto the camera-locked gun so it tilts with the gesture
        string _attachStartClip = null, _attachStopClip = null;   // per-gun attach-view pose clips ({Gun}_AttachStart/Stop)
        bool _attachView, _attachCapture; Basis _attachBoneStart;   // T attachment view: hold the presented pose (gun follows the bone like inspect)
        Node3D _muzzleFlash;  // brief flash light + spark at the muzzle on fire
        float _flash;
        ShaderMaterial _flashMat;   // muzzle flash billboard material (roll uniform set per shot)
        float _flashRoll;           // ACCUMULATED flash roll -- each shot rolls it L/R by an amount, remembering the last (master)
        AudioStreamPlayer _shootSnd, _reloadSnd, _drySnd;   // real Eaglefire Shoot/Reload/Hammer(dry-fire) sounds
        // Case ejection (master-requested feel add 2026-07-08 — the vanilla Eaglefire has no Shell effect, so this
        // is non-vanilla): a generic 5.56 casing (yellow rectangle cube) tossed from the gun's Eject hook each shot,
        // arcing out to the right + tumbling under gravity, then despawning. Lives in the viewmodel viewport world.
        Node3D _ejectHook;
        BoxMesh _casingMesh;
        bool _ejects = true;   // GunVisual.Ejects -- false for shotguns (masterkey): no per-shot shell eject
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
        public string MeleeMesh, MeleeAlbedo;   // set (instead of GunName) to show a MELEE weapon in-hand: mesh + albedo only, no sight/mag/muzzle/fire
        // Sight/Mag are null when the gun's sights + magazine are baked into Model_0 (the Masterkey shotgun — no
        // separate sight/mag prefab). MuzzleHook = the model's Effect hook (bore, port frame). Shoot/Reload = the
        // gun's own AudioClips (the assault rifles share the Eaglefire's).
        // ViewOffset = a per-gun hip-pose nudge (camera/arms-local metres) so each gun sits right in first person —
        // guns mount at their Model_0 origin, and the maple/shotgun models sit higher than the (reference) eaglefire.
        // AlbedoTint multiplies the albedo (Godot AlbedoColor*AlbedoTexture): the masterkey's base albedo is a mostly
        // WHITE paint-base that the game tints dark, so we tint it to a dark gunmetal (the eaglefire's is already dark).
        struct GunVisual { public string Gun, Sight, Mag, Albedo, Shoot, Reload; public Vector3 AimHook, MuzzleHook, ViewOffset, SightPos; public Color AlbedoTint, SightColor; public bool Ejects; }
        static GunVisual Visual(string name) => name switch
        {
            "masterkey"   => new GunVisual { Gun = "masterkey_gun.txt",   Sight = null,                          Mag = null,                Albedo = "masterkey_albedo.png",  Shoot = "masterkey_shoot.ogg", Reload = "masterkey_reload.ogg", AimHook = new Vector3(0f, -0.40f, -0.19f),    MuzzleHook = new Vector3(0f, 0.615f, -0.042f), ViewOffset = Vector3.Zero, AlbedoTint = new Color(0.46f, 0.28f, 0.13f), Ejects = false },   // masterkey = shotgun: no per-shot shell eject
            "maplestrike" => new GunVisual { Gun = "maplestrike_gun.txt", Sight = "maplestrike_iron_sights.txt", Mag = "eaglefire_mag.txt", Albedo = "maplestrike_albedo.png", Shoot = "eaglefire_shoot.ogg", Reload = "eaglefire_reload.ogg", AimHook = new Vector3(0f, -0.4388f, -0.2291f), MuzzleHook = new Vector3(0f, 0.78f, -0.079f),  ViewOffset = Vector3.Zero, AlbedoTint = new Color(0.44f, 0.40f, 0.28f), Ejects = true },
            "eaglefire"   => new GunVisual { Gun = "eaglefire_gun.txt",   Sight = "eaglefire_iron_sights.txt",   Mag = "eaglefire_mag.txt", Albedo = "eaglefire_albedo.png",  Shoot = "eaglefire_shoot.ogg", Reload = "eaglefire_reload.ogg", AimHook = new Vector3(0f, -0.4688f, -0.2098f), MuzzleHook = new Vector3(0f, 0.78f, -0.079f),  ViewOffset = Vector3.Zero, AlbedoTint = new Color(0.40f, 0.36f, 0.32f), Ejects = true },
            _             => ExtraVisual(name),   // the bulk PEI arsenal: extracted content + content/guns_visual.tsv
        };

        // GunVisuals for the bulk PEI arsenal, loaded from content/guns_visual.tsv (emitted by tools/extract_gun.py).
        // Line: name \t muzzle(x,y,z) \t aim(x,y,z) \t ejects(1|0). Sight/Mag null + real _MainTex albedo (white tint)
        // as the first pass -- per-gun ADS/mag/sight tuning is polish.
        static System.Collections.Generic.Dictionary<string, GunVisual> _extraVisuals;
        static GunVisual ExtraVisual(string name)
        {
            _extraVisuals ??= LoadExtraVisuals();
            return _extraVisuals.TryGetValue(name, out var gv) ? gv : Visual("eaglefire");
        }
        static System.Collections.Generic.Dictionary<string, GunVisual> LoadExtraVisuals()
        {
            var d = new System.Collections.Generic.Dictionary<string, GunVisual>();
            string path = ProjectSettings.GlobalizePath("res://content/guns_visual.tsv");
            if (!System.IO.File.Exists(path)) return d;
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                var c = line.Split('\t');
                if (c.Length < 4) continue;
                d[c[0]] = new GunVisual
                {
                    Gun = c[0] + "_gun.txt", Albedo = c[0] + "_albedo.png", Sight = null, Mag = null,
                    Shoot = Snd(c[0] + "_shoot.ogg", "eaglefire_shoot.ogg"), Reload = Snd(c[0] + "_reload.ogg", "eaglefire_reload.ogg"),   // real per-gun sounds; fall back to eaglefire's if a clip is missing
                    MuzzleHook = V3(c[1]), AimHook = V3(c[2]), ViewOffset = Vector3.Zero,
                    AlbedoTint = new Color(1f, 1f, 1f), Ejects = c[3].Trim() == "1",
                };
            }
            // per-gun DEFAULT iron sights (content/sights.tsv: name \t sight_model \t mount(x,y,z)) extracted from each
            // gun's default Sight attachment (tools/extract_gun_sights.py) -- merge onto the loaded GunVisuals.
            string sp = ProjectSettings.GlobalizePath("res://content/sights.tsv");
            if (System.IO.File.Exists(sp))
                foreach (var line in System.IO.File.ReadAllLines(sp))
                {
                    var c = line.Split('\t');
                    if (c.Length < 3 || !d.TryGetValue(c[0], out var gv)) continue;
                    gv.Sight = c[1]; gv.SightPos = V3(c[2]);
                    if (c.Length >= 4) { var rgb = V3(c[3]); gv.SightColor = new Color(rgb.X, rgb.Y, rgb.Z); }   // real per-gun sight _Color
                    d[c[0]] = gv;
                }
            return d;
        }
        static Vector3 V3(string s)
        {
            var p = s.Split(',');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return new Vector3(float.Parse(p[0], ci), float.Parse(p[1], ci), float.Parse(p[2], ci));
        }
        static string Snd(string name, string fallback) => System.IO.File.Exists(ProjectSettings.GlobalizePath("res://content/" + name)) ? name : fallback;
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
            _vpLight = new DirectionalLight3D { RotationDegrees = new Vector3(-40f, -25f, 10f), LightEnergy = 1.2f };
            _vp.AddChild(_vpLight);
            // Fill lights from complementary angles -- the SubViewport's ambient wasn't reaching the guns, so faces
            // missing the key light rendered black. These cover the other sides so the whole gun stays readable.
            _vpFill1 = new DirectionalLight3D { RotationDegrees = new Vector3(25f, 165f, 0f), LightEnergy = 0.45f };
            _vpFill2 = new DirectionalLight3D { RotationDegrees = new Vector3(-65f, 55f, 0f), LightEnergy = 0.35f };
            _vp.AddChild(_vpFill1);
            _vp.AddChild(_vpFill2);
            _vpEnv = new Godot.Environment
            {
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.72f, 0.72f, 0.74f),
                AmbientLightEnergy = 1.0f,
            };
            // Glow on the VIEWMODEL viewport too -- the world env's glow doesn't reach this isolated SubViewport, so the
            // FP muzzle flash never bloomed. High HDR threshold (1.25) so ONLY the HDR flash billboard blooms; the lit gun
            // surfaces (<=1.0) stay crisp -- NOT the old "energy 5 washed the frame". ACES matches the world tonemap.
            if (System.Environment.GetEnvironmentVariable("UG_NOGLOW") != "1")
            {
                _vpEnv.GlowEnabled = true;
                _vpEnv.GlowIntensity = 0.9f;
                _vpEnv.GlowStrength = 1.0f;
                _vpEnv.GlowBloom = 0.15f;
                _vpEnv.GlowHdrThreshold = 1.25f;
                _vpEnv.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Screen;
            }
            _vpEnv.TonemapMode = System.Environment.GetEnvironmentVariable("UG_LINEAR") == "1"
                ? Godot.Environment.ToneMapper.Linear : Godot.Environment.ToneMapper.Aces;
            _vp.AddChild(new WorldEnvironment { Environment = _vpEnv });

            _arms = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f), armsOnly: true);
            if (_arms != null)
            {
                _cam.AddChild(_arms);
                _arms.Position = _armsPos;
                _arms.SetClipLoop("Gun_Equip", false);   // equip plays ONCE and holds the ready pose
                _arms.SetClipLoop("Gun_Reload", false);  // reload plays ONCE (the clip returns the hands to ready)
                // per-gun reload clip ({Gun}_Reload, extracted from that gun's animations.prefab); fall back to Gun_Reload
                string capGun = char.ToUpper(GunName[0]) + GunName.Substring(1);
                _reloadClip = _arms.ClipLength(capGun + "_Reload") > 0f ? capGun + "_Reload" : "Gun_Reload";
                _arms.SetClipLoop(_reloadClip, false);
                // per-gun inspect clip ({Gun}_Inspect, from that gun's animations.prefab). null = gun has no Inspect anim.
                _inspectClip = _arms.ClipLength(capGun + "_Inspect") > 0f ? capGun + "_Inspect" : null;
                if (_inspectClip != null) _arms.SetClipLoop(_inspectClip, false);
                _attachStartClip = _arms.ClipLength(capGun + "_AttachStart") > 0f ? capGun + "_AttachStart" : null;
                _attachStopClip = _arms.ClipLength(capGun + "_AttachStop") > 0f ? capGun + "_AttachStop" : null;
                if (_attachStartClip != null) _arms.SetClipLoop(_attachStartClip, false);
                _arms.SetClipLoop("Melee_Equip", false); _arms.SetClipLoop("Melee_Weak", false); _arms.SetClipLoop("Melee_Strong", false);   // melee equip/swing clips play once
                string equipClip = MeleeMesh != null ? "Melee_Equip" : (_arms.ClipLength(capGun + "_Equip") > 0f ? capGun + "_Equip" : "Gun_Equip");   // melee: raise the weapon; gun: its OWN per-weapon hold (pistol grip / rifle stance / etc.)
                _arms.SetClipLoop(equipClip, false);
                _arms.Play(equipClip);
                _equipLen = _arms.ClipLength(equipClip);
                GD.Print($"[vm] equip (pull-out) length = {_equipLen:F3}s — aiming gated until then");

                var skel = _arms.Skeleton;
                int hb = skel.FindBone("Right_Hook");
                if (hb < 0) hb = skel.FindBone("Right_Hand");
                if (hb >= 0)
                {
                    var att = new BoneAttachment3D { Name = "GunAttach" };
                    skel.AddChild(att);
                    att.BoneName = skel.GetBoneName(hb);
                    var gv = MeleeMesh != null
                        ? new GunVisual { Gun = MeleeMesh, Albedo = MeleeAlbedo, Ejects = false, AlbedoTint = new Color(1, 1, 1) }   // melee: mesh + albedo only (no sight/mag/muzzle/shoot)
                        : Visual(GunName);
                    _ejects = gv.Ejects;
                    _armsPos += gv.ViewOffset;   // per-gun hip-pose nudge (ADS re-aligns via the aim hook regardless)
                    var mi = new MeshInstance3D { Mesh = ContentProvider.ParseObj($"res://content/{gv.Gun}") };
                    // TextureFilter = Nearest: runtime ImageTexture (Image.LoadFromFile) has NO mipmaps, so the default
                    // Linear-mipmap filter samples BLACK once the gun texture minifies -> the "guns render totally black"
                    // bug (same root as the icon-render black-gun). Nearest samples mip 0 always, so the texture shows.
                    // The gun's paint colours are BAKED into the albedo (tools/bake_gun_albedo.py: pure-black metal ->
                    // visible gunmetal, white paintable -> the gun's paint colour) because the raw metal is pure black
                    // and can't be shown by light/metallic/tint. So the material just shows the baked texture, matte.
                    // Fully matte: Unturned guns are non-reflective. MetallicSpecular=0 kills the dielectric specular
                    // highlight (the 3 viewmodel lights were kicking a "shiny" sheen off the body at Roughness 0.85).
                    var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, Metallic = 0f, MetallicSpecular = 0f, Roughness = 1f, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
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
                    // real per-gun sight _Color from content/sights.tsv (the sights have NO texture, just a flat _Color --
                    // greys 0.12-0.64, honeybadger tan); the old hardcoded 0.06 near-black was wrong. Grey default for the
                    // hardcoded guns (SightColor unset -> A==0).
                    var sightCol = gv.SightColor.A > 0f ? gv.SightColor : new Color(0.3f, 0.3f, 0.3f);
                    var sightMat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = sightCol, Metallic = 0f, MetallicSpecular = 0f, Roughness = 1f };
                    var ironMesh = gv.Sight != null ? ContentProvider.ParseObj($"res://content/{gv.Sight}") : null;
                    if (ironMesh != null)
                        mi.AddChild(new MeshInstance3D { Name = "IronSights", Mesh = ironMesh, MaterialOverride = sightMat, Position = gv.SightPos != Vector3.Zero ? gv.SightPos : new Vector3(0f, 0.1312f, -0.118f) });   // per-gun sight mount (extracted); eaglefire/maplestrike keep the tuned hardcoded pos

                    // Real default Magazine (item 6 = Military_30, GUID dbfb1d0d) — item.prefab Model_0 from
                    // core.masterbundle, converted (x,y,z)->(-x,y,-z). Mounted as Attachments.cs does
                    // (Instantiate(magazineAsset.magazine) at the Magazine hook, localPos 0 / identity); the mesh sits
                    // on the item root so its origin = MagazineHook(0,0.0166,-0.0238) -> port (0,0.0166,0.0238).
                    var magMat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(0.07f, 0.07f, 0.08f), Metallic = 0f, MetallicSpecular = 0f, Roughness = 1f };
                    var magMesh = gv.Mag != null ? ContentProvider.ParseObj($"res://content/{gv.Mag}") : null;
                    if (magMesh != null)
                        mi.AddChild(new MeshInstance3D { Name = "Magazine", Mesh = magMesh, MaterialOverride = magMat, Position = new Vector3(0f, 0.0166f, 0.0238f) });

                    // Real Military Suppressor (Barrel attachment) — barrel.prefab Model_0 from core.masterbundle, converted
                    // (x,y,z)->(-x,y,-z). HIDDEN by default (guns ship with no barrel); the T menu toggles it, and when on it
                    // SILENCES the shot (source: a silenced barrel skips the zombie AlertTool.alert entirely, UseableGun ~936).
                    // Mounted at the eaglefire Barrel hook (per-gun barrel hooks are still hardcoded, like the other slots).
                    var barrelMat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(0.05f, 0.05f, 0.055f), Metallic = 0f, MetallicSpecular = 0f, Roughness = 0.85f };   // dark matte, like the gun body
                    mi.AddChild(new MeshInstance3D { Name = "Barrel", Mesh = ContentProvider.ParseObj("res://content/suppressor.txt"), MaterialOverride = barrelMat, Position = new Vector3(0f, 0.7307f, -0.0818f), Visible = false });

                    // ADS anchor marker at the sight's real Aim hook (gv.AimHook, per-gun) — ADS slides the arms so this
                    // lands on the camera axis, i.e. you look straight through the aperture.
                    _sight = new Node3D { Name = "AimHook" };
                    mi.AddChild(_sight);
                    _sight.Position = gv.AimHook;

                    // muzzle flash = the REAL Muzzle_0 effect (ID 3; the Eaglefire.dat has Muzzle 3), extracted from
                    // core.masterbundle: a warm point light (Unity color (0.94,0.76,0.15), intensity 1.37 — NOT the old
                    // energy 5 that washed the frame) + a brief BILLBOARD star-flash sprite (the real 32x32 Muzzle_0
                    // texture, size ~0.5 per startSize, additive), flashed ~0.05s on fire.
                    // sits on the barrel BORE axis just past the muzzle tip: gun model muzzle is at Y=0.731, bore
                    // centre at (X=0, Z=-0.079) — the old Z=-0.04 was 0.039 off-axis, which read as the flash sitting low.
                    _muzzleFlash = new Node3D { Name = "MuzzleFlash", Position = gv.MuzzleHook, Visible = false };
                    _muzzleFlash.AddChild(new OmniLight3D { OmniRange = 4.0f, LightColor = new Color(0.941f, 0.756f, 0.152f), LightEnergy = 1.4f });
                    // shader billboard so the star can ROLL per shot (master); a StandardMaterial billboard cancels rotation
                    _flashMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/muzzleflash.gdshader") };
                    var flashTex = LoadTex("res://content/muzzleflash.png");
                    if (flashTex != null) _flashMat.SetShaderParameter("tex", flashTex);
                    _flashMat.SetShaderParameter("roll", 0f);
                    _muzzleFlash.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(0.6f, 0.6f) }, MaterialOverride = _flashMat });
                    // (the old muzzle-local tracer quad was removed — the Military_30's Trail_0 tracer is now drawn in
                    //  the main world from muzzle->impact in PlayerController.SpawnTracer, so a viewmodel streak is redundant.)

                    // real gun sounds — the Eaglefire's Shoot/Reload AudioClips from the bundle (-> ogg). Non-3D
                    // AudioStreamPlayers output to the Master bus, so they're audible even though the gun lives in
                    // the viewmodel SubViewport (the player's own gun sound is non-positional anyway).
                    _shootSnd = new AudioStreamPlayer { Stream = LoadOgg($"res://content/{gv.Shoot}"), VolumeDb = -3f };
                    mi.AddChild(_shootSnd);
                    _reloadSnd = new AudioStreamPlayer { Stream = LoadOgg($"res://content/{gv.Reload}"), VolumeDb = -3f };
                    mi.AddChild(_reloadSnd);
                    _drySnd = new AudioStreamPlayer { Stream = LoadOgg("res://content/eaglefire_hammer.ogg"), VolumeDb = -3f };
                    mi.AddChild(_drySnd);
                    mi.AddChild(_muzzleFlash);

                    // Eject hook marker (gun Eject hook (0,0.0275,0.0814) -> port (0,0.0275,-0.0814)) + the casing mesh/
                    // material. The source Casing effect's Model_0 IS a plain box (24 verts, square section, ~3.3:1) with a
                    // flat brass _Color (0.904,0.768,0.007) -- so the box replicates the real asset; sized to master's +50%.
                    // (Shotguns' red Shell casing _Color (0.588,0.190,0.190) is extracted too, pending per-gun action wiring.)
                    _ejectHook = new Node3D { Name = "EjectHook", Position = new Vector3(0f, 0.0275f, -0.0814f) };
                    mi.AddChild(_ejectHook);
                    _casingMesh = new BoxMesh { Size = new Vector3(0.0135f, 0.0135f, 0.042f) };   // source square section @ master's +50% length
                    _casingMat = new StandardMaterial3D { AlbedoColor = new Color(0.904f, 0.768f, 0.007f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };   // exact source brass _Color
                }
            }

            // Composite the viewmodel viewport on top of the main view.
            _layer = new CanvasLayer { Layer = 5 };
            var tr = new TextureRect { Texture = _vp.GetTexture(), StretchMode = TextureRect.StretchModeEnum.Scale };
            tr.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _layer.AddChild(tr);
            AddChild(_layer);
        }

        // Fire: muzzle flash + casing + sound, plus BOTH source per-shot recoils on the viewmodel camera —
        // the positional SHAKE (random [shakeMin,shakeMax] per axis -> _shakeSpring; UseableGun.cs:921/1036) and
        // the rotational tilt (recoilPitch/recoilYaw degrees -> _recoilRotSpring; UseableGun.cs:1037, PlayerAnimator
        // maps x=pitch, y=z=yaw). Both spring back to rest. STAND stance = 1x (crouch/prone scale handled at fire).
        public void Kick(Vector3 shakeMin, Vector3 shakeMax, float recoilPitch, float recoilYaw)
        {
            _flash = 0.05f; EjectCasing(); _shootSnd?.Play();
            // roll the muzzle flash L/R by a random amount each shot, accumulating from the last (master)
            _flashRoll += (_rng.Randf() < 0.5f ? -1f : 1f) * _rng.RandfRange(0.35f, 1.0f);
            _flashMat?.SetShaderParameter("roll", _flashRoll);
            _shakeSpring.CurrentPosition += new Vector3(
                _rng.RandfRange(Mathf.Min(shakeMin.X, shakeMax.X), Mathf.Max(shakeMin.X, shakeMax.X)),
                _rng.RandfRange(Mathf.Min(shakeMin.Y, shakeMax.Y), Mathf.Max(shakeMin.Y, shakeMax.Y)),
                _rng.RandfRange(Mathf.Min(shakeMin.Z, shakeMax.Z), Mathf.Max(shakeMin.Z, shakeMax.Z)));
            // rotational recoil: gun tilts up (pitch) + yaws/rolls (PlayerAnimator maps x=pitch, y=z=yaw), springs back
            _recoilRotSpring.CurrentPosition += new Vector3(recoilPitch, recoilYaw, recoilYaw);
        }

        // Driven each physics frame by PlayerController: whether the player is moving + their stance, so the
        // walk bob uses the right frequency (SPEED_*) + amplitude (BOB_*) and switches off when standing still.
        public void SetLocomotion(bool moving, EPlayerStance stance) { _moving = moving; _stance = stance; }

        public void PlayDryFire() { _drySnd?.Play(); }   // hammer click when the trigger's pulled on empty

        public void SwingMelee(bool strong = false) { _arms?.Play(strong ? "Melee_Strong" : "Melee_Weak"); }   // play the source melee swing anim (Weak / Strong)

        // Toss a casing from the Eject hook: initial velocity = gun-right + up + slightly back (+ jitter), then it
        // arcs under gravity + tumbles (integrated in _Process). Parented to the viewport world so it flies free of
        // the gun. Non-vanilla for the Eaglefire (it has no Shell effect) — a visual feel add per master.
        void EjectCasing()
        {
            if (!_ejects || _ejectHook == null || _casingMesh == null || _vp == null || _gun == null) return;
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
        public void SetAiming(bool on) { if (on && (!EquipDone || _attachView)) return; if (on && _inspecting) CancelInspect(); _aiming = on; }   // no ADS while the attachment menu is up; ADS mid-inspect cancels the inspect (snap to ready) then aims

        // Driven by PlayerController while reloading — the gun dips down as a simple reload gesture (the full
        // Gun_Reload clip is a TODO; it needs additive-layer integration like the aim pose). Can't ADS mid-reload.
        public void SetReloading(bool on, float speed = 1f)
        {
            _reloading = on;
            if (on) { _aiming = false; _arms?.Play(_reloadClip, speed); if (_reloadSnd != null) { _reloadSnd.PitchScale = speed; _reloadSnd.Play(); } }   // per-gun reload arm anim + sound, sped up by DEXTERITY
        }

        // F to inspect: play the gun's OWN Inspect clip (per-gun, from its animations.prefab; ends back on the ready
        // hold). Guns without an Inspect clip (_inspectClip == null) just don't inspect, matching the source's
        // PlayerEquipment.canInspect gating on animator.checkExists("Inspect"). Blocked mid-reload.
        public void PlayInspect()
        {
            if (_inspectClip == null || _reloading || _inspecting) return;
            _aiming = false; _arms?.Play(_inspectClip);
            _inspecting = true; _inspectCapture = true;
            _inspectTimer = _arms != null && _arms.ClipLength(_inspectClip) > 0f ? _arms.ClipLength(_inspectClip) : 3.3f;
        }

        public bool IsInspecting => _inspecting;

        // Firing mid-inspect cancels it: drop _inspecting (the gun basis reverts to the camera-lock = shoot pose
        // instantly) and snap the arms to the ready hold so the hands match the gun again.
        public void CancelInspect()
        {
            if (!_inspecting) return;
            _inspecting = false;
            _arms?.SnapToEnd("Gun_Equip");   // snap the arms to the equip-END (the ready hold), no pull-out replay
        }

        // T attachment view: present the gun in its source Attach_Start pose so the slot icons can sit on it; holds
        // the pose while the menu is open (like inspect, but not timed). Exit snaps back to the ready hold.
        public void EnterAttachView()
        {
            if (_attachView || _reloading || _inspecting) return;
            _aiming = false;
            if (_attachStartClip != null) _arms?.Play(_attachStartClip);
            _attachView = true; _attachCapture = true;
        }
        public void ExitAttachView()
        {
            if (!_attachView) return;
            _attachView = false;
            _arms?.SnapToEnd("Gun_Equip");
        }
        public bool InAttachView => _attachView;

        // ---- weapon attachments (T menu). The gun's attachment models are children of _gun named per slot; right now
        // only Sight (iron sights) + Magazine ship a model, so detach/attach = toggling that model's visibility. The
        // default iron sights ARE the Sight attachment -- removable (and later replaceable), matching the source.
        static readonly System.Collections.Generic.Dictionary<string, string> _attachMesh =
            new() { { "Sight", "IronSights" }, { "Magazine", "Magazine" }, { "Barrel", "Barrel" } };
        public bool SlotHasModel(string slot) => _attachMesh.TryGetValue(slot, out var n) && _gun?.GetNodeOrNull<MeshInstance3D>(n) != null;
        public bool SlotAttached(string slot) => _attachMesh.TryGetValue(slot, out var n) && (_gun?.GetNodeOrNull<MeshInstance3D>(n)?.Visible ?? false);
        public bool IsSuppressed => SlotAttached("Barrel");   // the only Barrel attachment is the silenced suppressor, so attached = suppressed (source: silenced barrel fires no zombie alert)
        public void SetSlotAttached(string slot, bool on)
        {
            if (_attachMesh.TryGetValue(slot, out var n)) { var m = _gun?.GetNodeOrNull<MeshInstance3D>(n); if (m != null) m.Visible = on; }
        }
        // swap the slot's model to a named attachment (null/empty = detach). Alternate attachments are calibrated to
        // the same child-node position as the default, so swapping just the mesh mounts the new part on the same hook.
        public void SetSlotMesh(string slot, string txtName)
        {
            if (!_attachMesh.TryGetValue(slot, out var n)) return;
            var m = _gun?.GetNodeOrNull<MeshInstance3D>(n);
            if (m == null) return;
            if (string.IsNullOrEmpty(txtName)) { m.Visible = false; return; }
            m.Mesh = ContentProvider.ParseObj($"res://content/{txtName}");
            m.Visible = true;
        }

        // Attachment hook positions on the gun (port frame, from the source prefab's Sight/Tactical/Barrel/Grip/Magazine
        // hooks: (x,y,z)->(-x,y,-z)). The T menu projects these through the viewmodel cam so the slot icons sit on the gun.
        static readonly System.Collections.Generic.Dictionary<string, Vector3> _hookLocal = new()
        {
            { "Sight",    new Vector3( 0f,      -0.2398f, -0.1386f) },
            { "Tactical", new Vector3(-0.0601f,  0.3815f, -0.0851f) },
            { "Barrel",   new Vector3( 0f,       0.7307f, -0.0818f) },
            { "Grip",     new Vector3( 0f,       0.2595f, -0.0226f) },
            { "Magazine", new Vector3( 0f,       0.0166f,  0.0238f) },
        };
        public bool TryGetSlotScreen(string slot, out Vector2 screen)
        {
            screen = Vector2.Zero;
            if (_gun == null || _cam == null || !_hookLocal.TryGetValue(slot, out var local)) return false;
            Vector3 world = _gun.GlobalTransform * local;
            if (_cam.IsPositionBehind(world)) return false;
            screen = _cam.UnprojectPosition(world);
            return true;
        }

        // Length (s) of the equipped gun's reload clip, so PlayerController times the ammo refill to the real anim
        // (rifles 1.633s, the masterkey's break-action 2.467s). Falls back to the eaglefire length.
        public float ReloadLength => _arms != null && _arms.ClipLength(_reloadClip) > 0f ? _arms.ClipLength(_reloadClip) : 1.633f;

        public float AimAlpha => _aimAlpha;   // 0 hip .. 1 ADS, for spread/accuracy

        public void SetShown(bool shown) { if (_layer != null) _layer.Visible = shown; }

        public override void _Process(double delta)
        {
            if (_arms == null || _cam == null) return;
            // take in the world's lighting: sync the FP viewport's sun + ambient to the day/night cycle each frame
            if (WorldSun != null && _vpLight != null)
            {
                // scale the whole viewmodel brightness with the world so the gun DARKENS at night to match (master: "pure
                // lighting"). A readability floor (UG_VMFLOOR, default 0.3) keeps it from going pitch-black in the hands.
                float vmFloor = float.TryParse(System.Environment.GetEnvironmentVariable("UG_VMFLOOR"), out var _vf) ? _vf : 0.3f;
                float bright = Mathf.Clamp(WorldSun.LightEnergy, vmFloor, 1f);
                _vpLight.RotationDegrees = WorldSun.RotationDegrees;
                _vpLight.LightEnergy = Mathf.Max(vmFloor * 0.6f, WorldSun.LightEnergy);   // key follows the sun; low floor so it dims hard at night
                _vpLight.LightColor = WorldSun.LightColor;
                if (_vpFill1 != null) _vpFill1.LightEnergy = 0.45f * bright;   // fills fade with the world -> gun no longer stays evenly lit 24/7
                if (_vpFill2 != null) _vpFill2.LightEnergy = 0.35f * bright;
            }
            if (WorldEnv != null && _vpEnv != null)
            {
                _vpEnv.AmbientLightColor = WorldEnv.AmbientLightColor;
                _vpEnv.AmbientLightEnergy = WorldEnv.AmbientLightEnergy;
            }
            _t += delta;
            _equipElapsed += (float)delta;
            _flash = Mathf.Max(0f, _flash - (float)delta);
            if (System.Environment.GetEnvironmentVariable("UG_FLASHHOLD") == "1") _flash = 0.05f;   // render-harness: hold the flash so a single-frame --shot captures its bloom
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
            _recoilRotSpring.TargetPosition = Vector3.Zero;   // recoil rotation springs back too
            _recoilRotSpring.Update((float)delta);
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
            _arms.Position = hipPos + vmOffset + TuneOffset;   // + the live uniform tune offset (ESC sliders); per-gun offsets removed
            if (_cam != null) _cam.Fov = TuneFov;              // live-tunable viewmodel FOV (ESC sliders); ADS doesn't change VM FOV

            // reload plays the real Gun_Reload clip (see SetReloading) — the base pose IS the reload motion, no dip.

            if (_inspecting) { _inspectTimer -= (float)delta; if (_inspectTimer <= 0f) _inspecting = false; }
            if (_gun != null && _gun.GetParent() is Node3D att)
            {
                Vector3 aim = -_cam.GlobalTransform.Basis.Z;   // viewmodel-forward
                Vector3 x = Vector3.Up.Cross(aim);
                if (x.LengthSquared() < 1e-5f) x = Vector3.Right;
                x = x.Normalized();
                var basis = new Basis(x, aim, x.Cross(aim).Normalized());   // barrel (+Y) -> aim
                basis = basis.Rotated(aim, Mathf.DegToRad(_gunRoll));
                // per-shot recoil tilt (source recoilViewmodelCameraRotation, spring-decayed): pitch up about the
                // camera-right axis (same climb sign as the old muzzle-rise), yaw about camera-up, roll about the barrel.
                Vector3 rr = _recoilRotSpring.CurrentPosition;   // (pitch, yaw, roll) degrees
                Basis cb = _cam.GlobalTransform.Basis;
                basis = basis.Rotated(cb.X, Mathf.DegToRad(rr.X))    // pitch -> muzzle climb
                             .Rotated(cb.Y, Mathf.DegToRad(rr.Y))    // yaw
                             .Rotated(aim,  Mathf.DegToRad(rr.Z));   // roll
                // inspect (source-accurate): the source animates the gun via the Inspect clip, so follow the hand
                // bone's rotation delta since inspect start -- the gun tilts/turns with the gesture instead of the
                // camera-lock pinning the barrel forward. bone.now * (bone.start.inv) == the animation's rotation.
                if (_inspecting)
                {
                    if (_inspectCapture) { _inspectBoneStart = att.GlobalTransform.Basis; _inspectCapture = false; }
                    basis = (att.GlobalTransform.Basis * _inspectBoneStart.Inverse()) * basis;
                }
                if (_attachView)   // hold the attach-view presented pose (same bone-delta layering as inspect)
                {
                    if (_attachCapture) { _attachBoneStart = att.GlobalTransform.Basis; _attachCapture = false; }
                    basis = (att.GlobalTransform.Basis * _attachBoneStart.Inverse()) * basis;
                }
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
