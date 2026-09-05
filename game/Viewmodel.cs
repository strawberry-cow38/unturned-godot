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
        string _meleeCap;   // Cap(melee content name), e.g. "Blowtorch"/"Sledgehammer" -> per-melee clip labels (Blowtorch_Start_Swing, Sledgehammer_Weak, ...); null for guns/consumables
        Node3D _gun;
        CanvasLayer _layer;
        TextureRect _vpRect;   // the composited viewmodel image; rolled 2D about screen-centre for the 1P lean tilt
        public const float VpOverX = 1.15f, VpOverY = 1.45f;   // render-target oversize factors (see _Ready) -- enough for the 20 deg lean roll on 16:9
        Vector2 _scr, _vpMargin;   // screen size at build; (viewport - screen)/2
        /// <summary>The camera fov that keeps the on-screen framing identical to a screen-sized viewport at vertical `vfov`:
        /// convert to horizontal at the screen aspect, then widen by the horizontal oversize (width-locked camera; the
        /// vertical follows the viewport's aspect, which lands on vfov * the vertical oversize exactly).</summary>
        float OversizeFov(float vfov)
        {
            float aspect = _scr.Y > 0f ? _scr.X / _scr.Y : 16f / 9f;
            float hHalf = Mathf.Atan(Mathf.Tan(Mathf.DegToRad(vfov) * 0.5f) * aspect);
            return Mathf.RadToDeg(2f * Mathf.Atan(Mathf.Tan(hHalf) * VpOverX));
        }
        Vector2 VpToScreen(Vector2 px) => px - _vpMargin;   // UnprojectPosition gives viewport px; 2D overlays live in screen px
        // Source-accurate: horizontal offset is ZERO (PlayerAnimator.cs:1653 base = Vector3.zero,
        // PreferenceData Offset_Horizontal defaults 0). The gun reads right-handed because the RIG holds
        // it in the right hand (lefties get localScale.x=-1, PlayerAnimator:1613 — a mirror, not a shift).
        // Y is the eye-alignment + the source -0.45 vertical drop (PlayerAnimator:1431, gun sits low).
        Vector3 _armsPos = new Vector3(0f, -1.75f, 0.12f);
        // DRIVING ARMS (strawberry 2026-09-03 "get the 1p viewmodel for steering wheels"): behind the wheel in 1P the arms play the
        // body's Idle_Drive and the rig is slid so its SKULL sits on the viewmodel camera -- the world camera sits on the seated
        // body's skull (PlayerController.SeatedEyeLocal), so the hands land where the seated body's hands are: on the real wheel.
        public bool Driving { get; private set; }
        bool _poseDrive;   // UG_POSE=drive (render harness): enter driving arms once the rig exists
        int _vmSkull = -1;
        public void SetDriving(bool on)
        {
            if (on == Driving || _arms == null) return;
            Driving = on;
            if (on)
            {
                if (_arms.ClipLength("Idle_Drive") > 0f) { _arms.SetClipLoop("Idle_Drive", true); _arms.PlayLoop("Idle_Drive"); }
                if (_gun != null && Godot.GodotObject.IsInstanceValid(_gun)) _gun.Visible = false;   // no rifle floating over the dashboard
            }
            else
            {
                if (_gun != null && Godot.GodotObject.IsInstanceValid(_gun)) _gun.Visible = true;
                _arms.Position = _armsPos;
                _arms.Play(_holdClip);   // back to the item's ready hold
            }
        }
        void SetDrivingDeferred() => SetDriving(true);
        // WHEEL PIN (strawberry 2026-09-04: "the 1p vehicle driving hands should attach to the steering wheel no matter what").
        // PlayerController hands us the real steering-wheel pivot each frame as MAIN-camera screen px + depth (+ the wheel's axis in
        // camera space and the steer angle). We re-project that through the viewmodel camera at the same depth -- exact regardless
        // of the two cameras' FOV difference -- and slide the arms so the MIDPOINT of the two hand bones sits on it, then turn the
        // whole arm pair about that point with the wheel. No pose can miss the wheel this way; a vehicle without a wheel model
        // falls back to the skull-on-camera slide.
        Vector2 _wheelScreen; float _wheelDepth, _wheelSteerDeg; Vector3 _wheelAxisCam; bool _wheelKnown;
        int _vmLHand = -1, _vmRHand = -1;
        public void SetDrivingWheel(Vector2 screenPx, float depth, Vector3 axisCamLocal, float steerDeg) { _wheelScreen = screenPx; _wheelDepth = depth; _wheelAxisCam = axisCamLocal; _wheelSteerDeg = steerDeg; _wheelKnown = depth > 0.05f; }
        public void ClearDrivingWheel() => _wheelKnown = false;
        Vector3 _wheelTargetCam;   // the wheel pivot in viewmodel-camera space, this frame
        bool WheelHandsCentre(out Vector3 handsLocal)
        {
            handsLocal = Vector3.Zero;
            var skel = _arms?.Skeleton; if (skel == null) return false;
            if (_vmLHand < 0) { _vmLHand = skel.FindBone("Left_Hand"); _vmRHand = skel.FindBone("Right_Hand"); }
            if (_vmLHand < 0 || _vmRHand < 0) return false;
            handsLocal = (skel.GetBoneGlobalPose(_vmLHand).Origin + skel.GetBoneGlobalPose(_vmRHand).Origin) * 0.5f;
            return handsLocal.LengthSquared() > 1e-4f;
        }
        Vector3 DrivingArmsPos()
        {
            var skel = _arms?.Skeleton; if (skel == null) return _armsPos;
            if (_vmSkull < 0) { _vmSkull = skel.FindBone("Skull"); if (_vmSkull < 0) return _armsPos; }
            var head = skel.GetBoneGlobalPose(_vmSkull).Origin;
            if (head.LengthSquared() < 0.01f) return _armsPos;
            if (_wheelKnown && _cam != null && WheelHandsCentre(out var hands))
            {
                _wheelTargetCam = _cam.ProjectPosition(_wheelScreen + _vpMargin, _wheelDepth);   // same screen spot + depth as the real wheel
                return _wheelTargetCam - hands;   // hands' midpoint ON the wheel pivot (rotation about it is applied after the sway pass)
            }
            return -(head + new Vector3(0f, 0.16f, -0.10f));   // same head-base -> eyes offset as PlayerController.SeatedEyeFromSkull
        }
        // NOTE: guns are oriented by riding the animated hand-bone HOLD pose (see the gun branch in _Process), which is
        // source-accurate -- each gun's own <Gun>_Equip anim poses a pistol vs a rifle. An earlier per-gun "hold pitch"
        // hack (magic +12deg on pistols) was removed once the bone-hold path handled it for real.
        double _t;
        // Source-accurate viewmodel-camera motion (PlayerAnimator): the walk BOB (viewmodelMovementOffset,
        // Rk4Spring2) + the per-shot recoil SHAKE (recoilViewmodelCameraOffset, Rk4Spring3), both applied to
        // the viewmodel camera's local position. Stiffness/damping are Inspector-serialized on the Player
        // prefab in the original (not in the scripts) -> tuned here; the motion + amplitudes are source-exact.
        Rk4Spring2 _bobSpring = new Rk4Spring2(900f, 60f);   // tracks the Sin(speed*t) target cleanly + eases stop
        Rk4Spring2 _swayTilt = new Rk4Spring2(120f, 22f);    // movement-driven viewmodel TILT (PlayerAnimator sway): softer/slower than the bob so the tilt trails the walk
        Godot.Vector2 _moveInput;                            // last move axes from the player (x=strafe, y=forward) -> the sway tilt direction
        Rk4Spring3 _shakeSpring = new Rk4Spring3(550f, 40f); // positional kick, settles ~0.2s (slight overshoot)
        Rk4Spring3 _recoilRotSpring = new Rk4Spring3(550f, 40f); // per-shot gun tilt (pitch/yaw/roll deg), springs back
        bool _moving;                       // player has movement input this frame (drives bob on/off)
        EPlayerStance _stance = EPlayerStance.STAND;   // STAND/SPRINT/CROUCH/PRONE/SWIM -> bob speed + amplitude
        bool _safe;                         // gun on SAFETY firemode -> un-shouldered "safe" carry (same pose as sprint, source UseableGun.cs:3509)
        bool _sprinting;                    // playing the Sprint_Start hold (un-shouldered); drives the Sprint_Stop return
        float _shootHold;                   // >0 briefly after each shot: firing breaks + suppresses sprint (source Sprint_Start needs !isShooting)
        string _sprintStartClip, _sprintStopClip;   // per-gun {Cap}_Sprint_Start/Stop, ripped from the gun's own animations.prefab
        float _blendedSway = 1f;            // blendedViewmodelSwayMultiplier: 1 hip -> 0.1 aim, eased at 16/s
        bool _reloading;      // true while the reload clip plays (blocks ADS)
        string _reloadClip = "Gun_Reload";   // per-gun reload clip ({Gun}_Reload), set in _Ready; falls back to Gun_Reload
        string _hammerClip = null;           // {Gun}_Hammer: the rechamber/rack played AFTER Reload when the mag was empty (source UseableGun); null = gun has none
        string _inspectClip = null;          // per-gun inspect clip ({Gun}_Inspect); null if the gun ships no Inspect anim
        bool _inspecting; float _inspectTimer; Basis _inspectBoneStart; bool _inspectCapture;   // inspect: layer the hand-bone rotation delta onto the camera-locked gun so it tilts with the gesture
        string _attachStartClip = null, _attachStopClip = null;   // per-gun attach-view pose clips ({Gun}_AttachStart/Stop)
        bool _attachView, _attachCapture; Basis _attachBoneStart;   // T attachment view: hold the presented pose (gun follows the bone like inspect)
        bool _hammering, _hammerCapture; float _hammerViewTimer; Basis _hammerBoneStart;   // rack (Hammer clip): follow the hand bone so the gun ROTATES as it's charged (else the rack was translation-only)
        Node3D _muzzleFlash;  // brief flash light + spark at the muzzle on fire
        float _flash;
        ShaderMaterial _flashMat;   // muzzle flash billboard material (roll uniform set per shot)
        float _flashRoll;           // ACCUMULATED flash roll -- each shot rolls it L/R by an amount, remembering the last (master)
        AudioStreamPlayer _shootSnd, _reloadSnd, _hammerSnd, _drySnd;   // real per-gun Shoot / Reload / Hammer(rack) sounds; dry-fire = its own click (none shipped yet)
        AudioStream _shootStream; AudioStreamPlaybackPolyphonic _shootPoly;   // shoot = overlapping polyphonic voices so full-auto shots ring out fully (no restart-cut)
        // Case ejection (master-requested feel add 2026-07-08 — the vanilla Eaglefire has no Shell effect, so this
        // is non-vanilla): a generic 5.56 casing (yellow rectangle cube) tossed from the gun's Eject hook each shot,
        // arcing out to the right + tumbling under gravity, then despawning. Lives in the viewmodel viewport world.
        Node3D _ejectHook;
        BoxMesh _casingMesh;
        bool _ejects = true;   // GunVisual.Ejects -- false for shotguns (masterkey): no per-shot shell eject
        StandardMaterial3D _casingMat;
        // Current gun's iron-sight body _Color + its default sight mesh name, remembered at build so a DETACHED-then-
        // REFITTED iron sight restores its real per-gun colour instead of the near-black scope/red-dot default.
        Color _sightColor = new(0.3f, 0.3f, 0.3f);
        string _defaultSightTxt;
        string _gunTxt;   // current gun's mesh name (gv.Gun) -- gates gun-specific attachment tuning (the red-dot ADS aim is eaglefire-tuned for now)
        public bool IntegralSight => _gunTxt != null && _gunTxt.Contains("augewehr");   // aug: built-in 4x scope is part of the gun -- no detachable/replaceable Sight slot (master)
        Vector3 _defaultSightPos = new(0f, 0.1312f, -0.118f);   // the gun's sight mount (SightPos = hook + iron Model_0); iron/scope/red-dot all mount here
        Vector3 _defaultAimHook = new(0f, -0.4688f, -0.2098f);   // the gun's ADS aim (gv.AimHook) -- the eye point iron/scope/red-dot all aim down
        readonly System.Collections.Generic.List<Casing> _casings = new();
        readonly RandomNumberGenerator _rng = new();
        sealed class Casing { public MeshInstance3D Node; public Vector3 Vel; public Vector3 Spin; public float Life; public bool Bounced; }

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
        public bool LeftHook;   // GunDef.LeftHook: the model hangs off Left_Hook (bows) instead of Right_Hook -- retail EquipableModelParent
        public string MeleeMesh, MeleeAlbedo;   // set (instead of GunName) to show a MELEE weapon in-hand: mesh + albedo only, no sight/mag/muzzle/fire
        public bool EmptyHands;   // holding-something-with-no-arm-model (e.g. a deployable) -> arms in a static rest hold, no weapon mesh
        public bool Fists;        // UNARMED combat state -> bare arms in the melee ready hold + weak/strong punch swings, no mesh (src: empty hands = hardcoded fists)
        public string ConsumableMesh, ConsumableAlbedo;   // set (instead of GunName) to HOLD a consumable (food/drink/medical): mesh + albedo, Equip hold + Use eat/drink anim, no gun FX
        public string ConsumableEquipClip, ConsumableUseClip;   // this item's OWN archetype clips (CE_n/CU_n from consumable_anims), e.g. drink vs eat vs syringe; empty -> generic fallback
        public Color? ConsumableColor;   // flat _Color for a no-texture consumable (cheese=yellow, potato=brown) -> used instead of the gray default
        public string DeployableMesh, DeployableAlbedo;   // set (instead of GunName) to HOLD a deployable (generator/spotlight): item.prefab carry mesh + palette, Deploy_Equip hold + Deploy_Use place anim, no gun FX
        public string ToolMesh; public Color? ToolColor;   // set (instead of GunName) to HOLD a tool in-hand (the Wire wiring tool): static mesh + generic ready hold, flat colour, no gun/deploy FX
        public Vector3? HoldPos, HoldRoll; public float HoldScale = 0f;   // per-item carry pose for a DeployableMesh (view-space nudge / Euler degrees / scale); unset = the gas-can defaults below
        public bool NaturalHold;   // for a DeployableMesh that is HELD not PLACED (the portable gas can): use the Melee_Equip ready-hold + tool offset instead of the low carry-to-place stance
        // Sight/Mag are null when the gun's sights + magazine are baked into Model_0 (the Masterkey shotgun — no
        // separate sight/mag prefab). MuzzleHook = the model's Effect hook (bore, port frame). Shoot/Reload = the
        // gun's own AudioClips (the assault rifles share the Eaglefire's).
        // ViewOffset = a per-gun hip-pose nudge (camera/arms-local metres) so each gun sits right in first person —
        // guns mount at their Model_0 origin, and the maple/shotgun models sit higher than the (reference) eaglefire.
        // AlbedoTint multiplies the albedo (Godot AlbedoColor*AlbedoTexture): the masterkey's base albedo is a mostly
        // WHITE paint-base that the game tints dark, so we tint it to a dark gunmetal (the eaglefire's is already dark).
        struct GunVisual { public string Gun, Sight, Mag, Albedo, Shoot, Reload, Hammer; public Vector3 AimHook, MuzzleHook, ViewOffset, SightPos; public Color AlbedoTint, SightColor; public bool Ejects; }
        // EVERY gun now comes from content/guns_visual.tsv (strawberry: "could we un-hardcode eaglefire n maplestrike
        // to be in line with the rest of the weapons"). The three that used to live here as switch arms -- eaglefire,
        // maplestrike, masterkey -- are ordinary rows in the table, using the optional mag/tint columns added for
        // them. Their exact former values are pinned in gun.visual_table_lossless so the move can't have quietly
        // changed how they look.
        static GunVisual Visual(string name) => ExtraVisual(name);

        /// <summary>A read-only view of a gun's RESOLVED visual, for the test that pins the three formerly-hardcoded
        /// guns to their exact former values. GunVisual itself stays private (it's an internal build recipe with a
        /// dozen fields nothing outside should depend on); this exposes only what a lossless-move check has to
        /// compare, and exposes it as data rather than opening the table up.</summary>
        public readonly struct GunVisualInfo
        {
            public readonly string Gun, Sight, Mag, Albedo, Shoot, Reload, Hammer;
            public readonly Vector3 AimHook, MuzzleHook, SightPos;
            public readonly Color Tint, SightColor;
            public readonly bool Ejects;
            public GunVisualInfo(string gun, string sight, string mag, string albedo, string shoot, string reload,
                                 string hammer, Vector3 aim, Vector3 muzzle, Color tint, bool ejects, Vector3 sightPos, Color sightColor)
            {
                Gun = gun; Sight = sight; Mag = mag; Albedo = albedo;
                Shoot = shoot; Reload = reload; Hammer = hammer;
                AimHook = aim; MuzzleHook = muzzle; Tint = tint; Ejects = ejects;
                SightPos = sightPos; SightColor = sightColor;
            }
        }
        public static GunVisualInfo VisualForTest(string name)
        {
            var g = Visual(name);
            return new GunVisualInfo(g.Gun, g.Sight, g.Mag, g.Albedo, g.Shoot, g.Reload, g.Hammer,
                                     g.AimHook, g.MuzzleHook, g.AlbedoTint, g.Ejects, g.SightPos, g.SightColor);
        }

        /// <summary>Is this a gun the visual table knows about? The equip path asks BEFORE putting it in your hands,
        /// because the honest answer to an unknown gun is to refuse, not to hand you something else wearing its name
        /// (strawberry: "unknown gun should spit an error center screen and fallback to unarmed").</summary>
        public static bool IsKnownGun(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            _extraVisuals ??= LoadExtraVisuals();
            return _extraVisuals.ContainsKey(name);
        }

        // GunVisuals for the bulk PEI arsenal, loaded from content/guns_visual.tsv (emitted by tools/extract_gun.py).
        // Line: name \t muzzle(x,y,z) \t aim(x,y,z) \t ejects(1|0). Sight/Mag null + real _MainTex albedo (white tint)
        // as the first pass -- per-gun ADS/mag/sight tuning is polish.
        static System.Collections.Generic.Dictionary<string, GunVisual> _extraVisuals;
        // An UNKNOWN gun used to silently become an eaglefire here. That is the worst possible answer: the gun fires,
        // reloads and looks like a working weapon, so the missing table row never gets noticed and the bug is reported
        // as "the dragonfang looks wrong" months later. The equip path now refuses unknown guns outright (see
        // IsKnownGun / PlayerController.EquipHeldGun), so reaching this fallback means something bypassed that check;
        // it's kept as a last resort so a stray call renders SOMETHING rather than throwing, and it says so loudly.
        static GunVisual ExtraVisual(string name)
        {
            _extraVisuals ??= LoadExtraVisuals();
            if (_extraVisuals.TryGetValue(name, out var gv)) return gv;
            GD.PushWarning($"[gun] no guns_visual.tsv row for '{name}' -- falling back to the eaglefire model. " +
                           "The equip path should have refused this; something built a Viewmodel directly.");
            return _extraVisuals.TryGetValue("eaglefire", out var ef) ? ef : default;
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
                // Columns 5 and 6 are OPTIONAL and exist for the three guns that used to be hardcoded in C#: a
                // magazine mesh, and an albedo tint for a texture the game darkens rather than using at face value.
                // The 28 extracted rows have neither and are untouched by their addition -- no mag, white tint, which
                // is what they had before. Blank is treated the same as absent so a row can carry a tint without a mag.
                string mag = c.Length >= 5 && c[4].Trim().Length > 0 ? c[4].Trim() : null;
                var tint = c.Length >= 6 && c[5].Trim().Length > 0 ? Col(c[5]) : new Color(1f, 1f, 1f);
                d[c[0]] = new GunVisual
                {
                    Gun = c[0] + "_gun.txt", Albedo = c[0] + "_albedo.png", Sight = null, Mag = mag,
                    Shoot = Snd(c[0] + "_shoot.ogg", "eaglefire_shoot.ogg"), Reload = Snd(c[0] + "_reload.ogg", "eaglefire_reload.ogg"),   // real per-gun sounds; fall back to eaglefire's if a clip is missing
                    Hammer = Snd(c[0] + "_hammer.ogg", "eaglefire_hammer.ogg"),   // rack / bolt-cycle sound (per-gun once ripped; eaglefire's for now)
                    MuzzleHook = V3(c[1]), AimHook = V3(c[2]), ViewOffset = Vector3.Zero,
                    AlbedoTint = tint, Ejects = c[3].Trim() == "1",
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
            // NOTE: no per-gun Sight-hook gap-fill for sightless guns. The raw Sight-child hook (guns_sighthook.tsv) is
            // the mount BEFORE the iron Model_0 is composed in -- measured (tinyclaw): paintballgun's hook == the eaglefire's
            // visibly-too-far-back spot, nailgun (0,-0.671,-0.159), fury (0,0.103,-0.342). Sightless guns have no iron
            // Model_0 to compose, so they mount optics at the eaglefire fallback (_defaultSightPos), which reads correctly.
            // guns_sighthook.tsv stays as extracted reference data; the eaglefire/maplestrike 0,0,0 sentinel -> fallback path is untouched.
            return d;
        }
        static Color Col(string s) { var v = V3(s); return new Color(v.X, v.Y, v.Z); }
        static Vector3 V3(string s)
        {
            var p = s.Split(',');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return new Vector3(float.Parse(p[0], ci), float.Parse(p[1], ci), float.Parse(p[2], ci));
        }
        static string Snd(string name, string fallback) => System.IO.File.Exists(ProjectSettings.GlobalizePath("res://content/" + name)) ? name : fallback;
        Node3D _sight;
        SubViewport _scopeVp; Camera3D _scopeCam; MeshInstance3D _scopeLens; Node3D _scopeCamAnchor; Godot.Environment _scopeEnv; DayNightCycle _dnc; bool _isScope, _scopeWasOn;   // PiP scope: lens ON the gun model (rides recoil); 2nd cam renders the world zoomed from the scope's OBJECTIVE end (LINEAR env so the lens isn't double-tonemapped by _vp)
        MeshInstance3D _scopeHost; Vector3 _ironAimPos;   // ADS aim hook: irons use _ironAimPos; a scope moves it to the scope's own `Aim` node (Attachments.cs:590 -- retail aligns the SIGHT model's Aim, so ADS looks THROUGH the scope, not the irons)
        CanvasLayer _ladderLayer; ScopeLadder2D _ladder; bool _scopeHasLadder;   // range ladder (100/200/300m) shown ADS'd with a numbered-ladder scope (8x/7x/16x); text = the global Units setting
        const float ScopeZeroDist = 100f;   // (b) zeroing range (m): scope cam converges onto the bullet ray here, so the reticle = point of impact at 100m + drifts slightly past. Miss at range R = |objective-eye| * |1 - R/Z| ~ 0.1m@50m, 0@100, 0.2m@200 (torso-tight at 4x; tinyclaw)

        // Equip gate — source: you can't start OR stop aiming until the Equip (pull-out) animation finishes
        // (UseableGun.ReceivePlayAimStart/Stop both guard on player.equipment.IsEquipAnimationFinished, which is
        // Time >= equipStart + GetAnimationLength("Equip"), PlayerEquipment.cs:269/1633). So SetAiming is ignored
        // while the gun is still raising.
        float _equipLen;       // Gun_Equip clip length (seconds)
        string _holdClip = "Gun_Equip";   // the CURRENT item's ready-hold clip (gun/melee/consumable/deployable each differ) -- restored on sprint-exit AND on leaving the water, instead of forcing the gun pose onto everything
        float _equipElapsed;   // time since the viewmodel spawned / equip started
        bool EquipDone => _equipLen <= 0f || _equipElapsed >= _equipLen;
        public bool IsEquipComplete => EquipDone;

        // The gun+arms live in this isolated SubViewport (composited over the main view by a CanvasLayer), so the
        // main GetViewport().GetImage() misses them -> a still-frame --vm capture came out background-only. Expose the
        // viewport image (RGBA, transparent bg) so the render harness can BlendRect it over the background frame.
        public Image CaptureViewport()
        {
            if (_vp == null) return null;
            RenderingServer.ForceDraw();   // flush the SubViewport render so GetImage reads the CURRENT gun -- an in-_Process GetImage otherwise reads an empty/stale render target (-> a background-only still)
            return _vp.GetTexture()?.GetImage();
        }

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;   // the whole viewmodel subtree (SubViewport cam, arms, scope cams) is placed per FRAME from the main camera -- interpolating it again only earns the engine's "Interpolated Camera3D triggered from outside physics process" warning
            _vp = new SubViewport
            {
                OwnWorld3D = true,
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                HandleInputLocally = false,
            };
            // OVERSIZED render target (master 2026-09-03: "render MORE longer vertically, when we lean we can see the bottom
            // of the viewport"). The 1P lean is a 2D roll of the composited image about screen centre, so a screen-sized
            // image shows its own edge in the corners at 20 deg. Render a taller+wider image and composite it CENTRED at
            // 1:1 -- the framing is pixel-identical (the camera's FOV is widened by exactly the same factors, see
            // OversizeFov), only the margin that used to be transparent is now painted. (The earlier attempt that read as
            // a zoom-in oversized the image but still stretched it into the screen rect.)
            _scr = GetViewport().GetVisibleRect().Size;
            _vp.Size = new Vector2I(Mathf.RoundToInt(_scr.X * VpOverX), Mathf.RoundToInt(_scr.Y * VpOverY));
            _vpMargin = ((Vector2)_vp.Size - _scr) * 0.5f;   // viewport px -> screen px = subtract this; the lean-roll corner margin will be re-done as a wider vm-cam FOV ("render more") instead.
            AddChild(_vp);

            _cam = new Camera3D { KeepAspect = Camera3D.KeepAspectEnum.Width, Fov = OversizeFov(SourceFov), Current = true };   // width-locked: the fov is horizontal, the extra height follows the taller viewport at the same px/deg
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

            // Scope range-ladder overlay (2D, screen-centered like retail's distance markers). Toggled ADS'd with a numbered-ladder scope.
            _ladderLayer = new CanvasLayer { Layer = 60 };
            _ladder = new ScopeLadder2D();
            _ladderLayer.AddChild(_ladder);
            AddChild(_ladderLayer);

            if (System.Environment.GetEnvironmentVariable("UG_POSE") is string _pz)   // render-harness: force a stance/firemode pose (no PlayerController in the --vm harness to drive it)
            {
                if (_pz == "sprint") { _stance = EPlayerStance.SPRINT; _moving = true; }
                else if (_pz == "safe") _safe = true;
                else if (_pz == "drive") _poseDrive = true;   // driving arms (Idle_Drive, skull on the camera) for the --vm render
            }

            _arms = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f), armsOnly: true);
            if (_arms != null)
            {
                _cam.AddChild(_arms);
                _arms.Position = _armsPos;
                if (_poseDrive) CallDeferred(nameof(SetDrivingDeferred));
                _arms.SetClipLoop("Gun_Equip", false);   // equip plays ONCE and holds the ready pose
                _arms.SetClipLoop("Gun_Reload", false);  // reload plays ONCE (the clip returns the hands to ready)
                // per-gun reload clip ({Gun}_Reload, extracted from that gun's animations.prefab); fall back to Gun_Reload
                string capGun = char.ToUpper(GunName[0]) + GunName.Substring(1);
                if (MeleeMesh != null) { string mn = MeleeMesh.Replace(".txt", ""); if (mn.Length > 0) _meleeCap = char.ToUpper(mn[0]) + mn.Substring(1); }   // per-melee clip prefix: "blowtorch.txt" -> "Blowtorch", "knife_military.txt" -> "Knife_military"
                _reloadClip = _arms.ClipLength(capGun + "_Reload") > 0f ? capGun + "_Reload" : "Gun_Reload";
                _hammerClip = _arms.ClipLength(capGun + "_Hammer") > 0f ? capGun + "_Hammer" : null;   // rechamber rack (empty-reload second half)
                if (_hammerClip != null) _arms.SetClipLoop(_hammerClip, false);
                _arms.SetClipLoop(_reloadClip, false);
                // per-gun inspect clip ({Gun}_Inspect, from that gun's animations.prefab). null = play nothing.
                //
                // GATED ON IsGunViewmodel (strawberry: "make sure it doesnt use the eaglefire inspect anim if its not
                // an eaglefire"). GunName defaults to "eaglefire" and every NON-gun viewmodel is built without setting
                // it -- `new Viewmodel { ConsumableMesh = ... }`, `{ DeployableMesh = ... }`, `{ ToolMesh = ... }`,
                // `{ Fists = true }` -- so capGun was "Eaglefire" for all of them, Eaglefire_Inspect exists, and F with
                // nothing focused fell through to PlayInspect(). Holding a can of beans and pressing F played a rifle
                // inspect. Guns and melee were always fine (melee has its own PlayMeleeInspect).
                //
                // Non-weapon holdables have no Inspect clip of their own and shouldn't: null here means PlayInspect
                // early-returns and nothing plays, which is the asked-for behaviour, not a fallback.
                _inspectClip = IsGunViewmodel && _arms.ClipLength(capGun + "_Inspect") > 0f ? capGun + "_Inspect" : null;
                if (_inspectClip != null) _arms.SetClipLoop(_inspectClip, false);
                _attachStartClip = _arms.ClipLength(capGun + "_AttachStart") > 0f ? capGun + "_AttachStart" : null;
                _attachStopClip = _arms.ClipLength(capGun + "_AttachStop") > 0f ? capGun + "_AttachStop" : null;
                if (_attachStartClip != null) _arms.SetClipLoop(_attachStartClip, false);
                // per-gun un-shoulder pose ({Gun}_Sprint_Start/Stop, from its animations.prefab). Gun-only (IsGunViewmodel)
                // like Inspect, so a non-gun holdable never matches the default-"Eaglefire" cap. Both play ONCE and hold.
                _sprintStartClip = IsGunViewmodel && _arms.ClipLength(capGun + "_Sprint_Start") > 0f ? capGun + "_Sprint_Start" : null;
                _sprintStopClip  = IsGunViewmodel && _arms.ClipLength(capGun + "_Sprint_Stop")  > 0f ? capGun + "_Sprint_Stop"  : null;
                if (_sprintStartClip != null) _arms.SetClipLoop(_sprintStartClip, false);
                if (_sprintStopClip  != null) _arms.SetClipLoop(_sprintStopClip,  false);
                // ADS aim POSE: re-bake the additive from THIS gun's own aim clip ({Gun}_Aim, ripped from its "Aim_Start"),
                // else the generic rifle-tuned Gun_Aim. Source: UseableGun aims by playing the equipped gun's own Aim_Start,
                // so a pistol levels FLAT; the single generic delta pitched every pistol UP in ADS. Re-bake each equip so a
                // gun-switch never inherits the previous weapon's aim delta.
                _arms.SetupAimAdditive(_arms.ClipLength(capGun + "_Aim") > 0f ? capGun + "_Aim" : "Gun_Aim");
                _arms.SetClipLoop("Melee_Equip", false); _arms.SetClipLoop("Melee_Weak", false); _arms.SetClipLoop("Melee_Strong", false);   // generic (knife) melee fallback clips play once
                _arms.SetClipLoop("Punch_Left", false); _arms.SetClipLoop("Punch_Right", false);   // bare-fists jabs play once (ported from Punch.fbx)
                if (_meleeCap != null)   // this melee's OWN ripped clips ALL play once and hold (source animator.play plays non-looping); a Repeated tool's continuous "blowtorching" is the spark EMISSION while held, NOT a looping Start_Swing
                    foreach (var c in new[] { "_Equip", "_Weak", "_Strong", "_Start_Swing", "_Stop_Swing", "_Inspect" }) _arms.SetClipLoop(_meleeCap + c, false);
                string equipClip = Fists ? "Punch_Left"   // bare fists: the GUARD the punch jabs start/return to (snapped below), NOT the knife-grip Melee_Equip (master: empty fist "held out like a knife")
                                 : EmptyHands ? "Melee_Equip"   // carry (invisible deployable): the generic melee READY hold (one-shot, no loop) -- NOT the 3P Idle_Hands_0 that was looping ("grab off back")
                                 : ToolMesh != null ? "Melee_Equip"   // held tool (wire): the generic one-hand ready hold
                                 : DeployableMesh != null ? (NaturalHold ? (_arms.ClipLength("Fuel_Equip") > 0f ? "Fuel_Equip" : "Deploy_Equip") : (_arms.ClipLength("Deploy_Equip") > 0f ? "Deploy_Equip" : "Melee_Equip"))   // deployable: the src barricade "Equip" raise-to-hold; NaturalHold (gas can) = its OWN TWO-HANDED Fuel_Equip carry (both hands on the can, source animations.prefab)
                                 : ConsumableMesh != null ? (_arms.ClipLength(ConsumableEquipClip) > 0f ? ConsumableEquipClip : _arms.ClipLength("Consume_Equip") > 0f ? "Consume_Equip" : "Melee_Equip")   // consumable: this item's OWN raise-to-hold archetype (CE_n), else generic Consume_Equip, else the melee raise
                                 : MeleeMesh != null ? (_arms.ClipLength(_meleeCap + "_Equip") > 0f ? _meleeCap + "_Equip" : "Melee_Equip") : (_arms.ClipLength(capGun + "_Equip") > 0f ? capGun + "_Equip" : "Gun_Equip");   // melee: its OWN raise anim (fallback generic knife); gun: its OWN per-weapon hold (pistol grip / rifle stance / etc.)
                GD.Print($"[vm] hold clip {equipClip} (capGun {capGun}, len {_arms.ClipLength(equipClip):0.###}s)");   // which per-item hold posed the hands (bow frame audit)
                _arms.SetClipLoop(equipClip, false);   // equip/ready-hold ALWAYS plays once and holds (src: one-shot wrapMode) -- the looping empty-hand pose was the bug
                _holdClip = equipClip;   // remember THIS item's hold so sprint-exit (etc.) restores it, not the gun pose
                if (Fists) _arms.SnapToEnd(equipClip);   // fists: snap straight to the guard pose -- don't play a jab-on-equip when you put an item away
                else _arms.Play(equipClip);
                _equipLen = Fists ? 0f : _arms.ClipLength(equipClip);
                GD.Print($"[vm] equip (pull-out) length = {_equipLen:F3}s — aiming gated until then");

                var skel = _arms.Skeleton;
                int hb = skel.FindBone(LeftHook ? "Left_Hook" : "Right_Hook");   // retail EquipableModelParent: bows parent to the LEFT hook
                if (hb < 0) hb = skel.FindBone(LeftHook ? "Left_Hand" : "Right_Hand");
                if (hb >= 0 && !EmptyHands && !Fists)   // EmptyHands/Fists -> no weapon mesh; just the bare arms in the ready hold
                {
                    var att = new BoneAttachment3D { Name = "GunAttach" };
                    skel.AddChild(att);
                    att.BoneName = skel.GetBoneName(hb);
                    var gv = ToolMesh != null
                        ? new GunVisual { Gun = ToolMesh, Albedo = null, Ejects = false, AlbedoTint = ToolColor ?? new Color(0.647f, 0.647f, 0.647f) }   // held tool (wire): flat-colour mesh, no texture
                        : DeployableMesh != null
                        ? new GunVisual { Gun = DeployableMesh, Albedo = DeployableAlbedo, Ejects = false, AlbedoTint = new Color(1, 1, 1) }   // deployable: item.prefab carry mesh + palette texture, no gun FX
                        : ConsumableMesh != null
                        ? new GunVisual { Gun = ConsumableMesh, Albedo = ConsumableAlbedo, Ejects = false, AlbedoTint = ConsumableColor ?? new Color(1, 1, 1) }   // consumable: mesh + albedo; AlbedoTint carries the flat _Color for no-texture items (cheese/potato)
                        : MeleeMesh != null
                        ? new GunVisual { Gun = MeleeMesh, Albedo = MeleeAlbedo, Ejects = false, AlbedoTint = new Color(1, 1, 1) }   // melee: mesh + albedo only
                        : Visual(GunName);
                    _ejects = gv.Ejects;
                    _armsPos += gv.ViewOffset;   // per-gun hip-pose nudge (ADS re-aligns via the aim hook regardless)
                    // gun body -- peel painted sight-dot markers onto their own emissive surface(s) (rendered below). Some
                    // pistols model tritium 3-dot sights as saturated-colour tris in the albedo; retail draws them flat, we glow
                    // them. ONLY real guns are split (not melee/consumable/deployable/tool), so a red apple never lights up.
                    // albedoImg is loaded once here + reused for the body texture.
                    bool isGunBody = MeleeMesh == null && ConsumableMesh == null && DeployableMesh == null && ToolMesh == null;
                    Image albedoImg = null;
                    if (gv.Albedo != null) { string _ap = ProjectSettings.GlobalizePath($"res://content/{gv.Albedo}"); if (System.IO.File.Exists(_ap)) albedoImg = ContentProvider.LoadImage(_ap); }
                    System.Collections.Generic.List<(Color color, ArrayMesh mesh)> sightDots = null;
                    ArrayMesh bodyMesh;
                    if (isGunBody) { var _sp = ContentProvider.ParseObjSplitByAlbedoMarker($"res://content/{gv.Gun}", albedoImg); bodyMesh = _sp.body; sightDots = _sp.markers; }
                    else bodyMesh = ContentProvider.ParseObj($"res://content/{gv.Gun}");
                    if (bodyMesh != null) GD.Print($"[vm] gun mesh {gv.Gun} aabb pos={bodyMesh.GetAabb().Position} size={bodyMesh.GetAabb().Size}");   // which axis is the long one -- the bow frame audit (2026-09-04)
                    var mi = new MeshInstance3D { Mesh = bodyMesh };
                    // TextureFilter = Nearest: runtime ImageTexture (Image.LoadFromFile) has NO mipmaps, so the default
                    // Linear-mipmap filter samples BLACK once the gun texture minifies -> the "guns render totally black"
                    // bug (same root as the icon-render black-gun). Nearest samples mip 0 always, so the texture shows.
                    // The gun's paint colours are BAKED into the albedo (tools/bake_gun_albedo.py: pure-black metal ->
                    // visible gunmetal, white paintable -> the gun's paint colour) because the raw metal is pure black
                    // and can't be shown by light/metallic/tint. So the material just shows the baked texture, matte.
                    // Fully matte: Unturned guns are non-reflective. MetallicSpecular=0 kills the dielectric specular
                    // highlight (the 3 viewmodel lights were kicking a "shiny" sheen off the body at Roughness 0.85).
                    var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, Metallic = 0f, MetallicSpecular = 0f, Roughness = 1f, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
                    var tex = albedoImg != null ? ImageTexture.CreateFromImage(albedoImg) : null;   // texture from the image loaded above (== LoadTex)
                    // no albedo texture: a consumable uses its real flat _Color (cheese/potato); anything else falls back to the neutral gray.
                    if (tex != null) mat.AlbedoTexture = tex; else mat.AlbedoColor = (ConsumableMesh != null || ToolMesh != null) ? gv.AlbedoTint : new Color(0.24f, 0.24f, 0.26f);
                    mi.MaterialOverride = mat;
                    att.AddChild(mi);
                    _gun = mi;
                    // glowing sight dots: each peeled marker surface rendered emissive in its OWN source colour (ace red,
                    // avenger/desert_falcon green, cobra white). Children of the body so they ride its transform. Energy is
                    // tunable -- it pushes the dot into HDR so the viewport glow blooms it.
                    if (sightDots != null)
                        foreach (var (_dc, _dm) in sightDots)
                        {
                            if (_dm == null) continue;
                            var dotMat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = _dc, EmissionEnabled = true, Emission = _dc, EmissionEnergyMultiplier = 1.5f, Metallic = 0f, MetallicSpecular = 0f, Roughness = 1f };
                            mi.AddChild(new MeshInstance3D { Name = "SightGlow", Mesh = _dm, MaterialOverride = dotMat });
                        }
                    // Real Eaglefire_Iron_Sights model (item 5) — sight.prefab from core.masterbundle, extracted via
                    // UnityPy and converted to the port gun frame (x,y,z)->(-x,y,-z), same pipeline as the gun body.
                    // Mounted exactly as Attachments.cs does: Instantiate(sightAsset.sight) parented to the Sight hook
                    // at localPos 0 / localRot identity / localScale 1. The sight's Model_0 origin therefore sits at
                    // SightHook(0,-0.2398,0.1386)+Model_0(0,0.371,-0.0206) = (0,0.1312,0.118) -> port (0,0.1312,-0.118).
                    // real per-gun sight _Color from content/sights.tsv (the sights have NO texture, just a flat _Color --
                    // greys 0.12-0.64, honeybadger tan); the old hardcoded 0.06 near-black was wrong. Grey default for the
                    // hardcoded guns (SightColor unset -> A==0).
                    var sightCol = gv.SightColor.A > 0f ? gv.SightColor : new Color(0.3f, 0.3f, 0.3f);
                    _sightColor = sightCol; _defaultSightTxt = gv.Sight; _gunTxt = gv.Gun;   // remembered so a re-fitted iron sight (SetSlotMesh) restores this colour, not the red-dot default; _gunTxt gates gun-specific tuning
                    _defaultSightPos = gv.SightPos != Vector3.Zero ? gv.SightPos : new Vector3(0f, 0.1312f, -0.118f);   // the sight mount (all optics mount here)
                    _defaultAimHook = gv.AimHook;   // the ADS aim (all optics aim down this eye point)
                    var sightMat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = sightCol, Metallic = 0f, MetallicSpecular = 0f, Roughness = 1f };
                    var ironMesh = gv.Sight != null ? ContentProvider.ParseObj($"res://content/{gv.Sight}") : null;
                    if (ironMesh != null)
                        mi.AddChild(new MeshInstance3D { Name = "IronSights", Mesh = ironMesh, MaterialOverride = sightMat, Position = gv.SightPos != Vector3.Zero ? gv.SightPos : new Vector3(0f, 0.1312f, -0.118f) });
                    // Real PiP scope (master): the aug scope model is an empty 12-sided tube -- drop a LENS at its rear
                    // opening showing a live render of the world (2nd cam), so you see THROUGH the optic. The lens is a
                    // child of the scope so it rides recoil/sway with the gun (master). No vignette; zoom = a low FOV.
                    _isScope = gv.Gun != null && gv.Gun.Contains("augewehr");
                    if (_isScope && ironMesh != null)
                    {
                        _scopeVp = new SubViewport { Size = new Vector2I(GraphicsOptions.ScopeSize, GraphicsOptions.ScopeSize), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, OwnWorld3D = false };   // NOT OwnWorld3D: that DUPLICATES the world (copies the sky env but a FRESH EMPTY scenario = no geometry -> lens shows only sky). Leave it false + bind World3D to the REAL main world below so the optic renders actual geometry. (This is a SEPARATE viewport from the arms _vp -- that one stays OwnWorld3D-isolated.)
                        AddChild(_scopeVp);
                        _scopeCam = new Camera3D { Current = true, Fov = 22.5f };   // retail scope fov = 90/Zoom; the aug scope is 4x (items_catalog: "Rail mounted 4x zoom scope") -> 90/4 = 22.5deg, not the 3.5x/25.7 I'd guessed
                        _scopeCam.CullMask &= ~OutlineOverlay.OutlineLayer;   // don't render the look-focus outline SILHOUETTE meshes (layer 19) into the scope -- like the main cams cull them; else they draw as a SOLID tint over the whole object (master: "outlines turn the entire object that color in scope space")
                        _scopeVp.AddChild(_scopeCam);
                        // Match the PiP colors to the main cam (master: "a little blown-out"): the lens re-renders inside the arms
                        // viewport (_vp), which re-applies ACES -> the scope's already-tonemapped image gets tonemapped TWICE and
                        // reads bright + over-saturated. Fix: render the SCOPE viewport LINEAR (a copy of the world env with tonemap
                        // Linear, Sky shared so it stays in sync), so _vp's single ACES is the ONLY tonemap -> the lens matches the
                        // periphery. (Diagnostic: forcing _vp Linear made lens==periphery, confirming the double-tonemap.)
                        _dnc = GetTree().GetFirstNodeInGroup("daynight") as DayNightCycle;
                        Godot.Environment _mainEnv = _dnc?.Env;
                        if (_mainEnv == null)   // no day/night (e.g. the firetest harness): find the main WorldEnvironment directly (skipping the arms _vpEnv)
                            foreach (var _n in GetTree().Root.FindChildren("*", "WorldEnvironment", true, false))
                                if (_n is WorldEnvironment _we && _we.Environment != null && _we.Environment != _vpEnv) { _mainEnv = _we.Environment; break; }
                        if (_mainEnv != null)
                        {
                            _scopeEnv = (Godot.Environment)_mainEnv.Duplicate();   // Duplicate() shares sub-resources (the Sky auto-updates); scalars (ambient/fog) synced each frame below (game only; the firetest env is static)
                            _scopeEnv.TonemapMode = Godot.Environment.ToneMapper.Linear;
                            _scopeEnv.GlowEnabled = _mainEnv.GlowEnabled;   // MATCH the main env's glow so emissive (streetlights/TV/signs) blooms in the scope too (master: "emissive textures do not render in scope space"); the lens is unshaded so _vp barely re-blooms it
                            _scopeCam.Environment = _scopeEnv;
                        }
                        // Round lens: the quad was only ever cropped round by the scope RING's aperture (far end). Inset in
                        // front of the ring, nothing masks it -> raw SQUARE (master: "why is the PiP square"). Mask it in the
                        // MATERIAL (tinyclaw) with a UV-radius discard so it's a true circle at ANY depth/size -- the scope
                        // viewport is square (720x720) so no 16:9 ellipse. Billboard in the vertex so the lens faces the eye.
                        var lensShader = new Shader { Code =
                            "shader_type spatial;\n" +
                            "render_mode unshaded, cull_disabled, shadows_disabled;\n" +
                            "uniform sampler2D scope_tex : source_color, filter_linear;\n" +
                            "void fragment() { vec2 p = (UV - vec2(0.5)) * 2.0; float a = atan(p.y, p.x) + 0.2618; float seg = 0.5235988; float dd = cos(floor(0.5 + a/seg) * seg - a) * length(p); if (dd > 0.95) discard; vec3 col = texture(scope_tex, UV).rgb; float r = length(p); bool cx = (abs(p.x) < 0.005 || abs(p.y) < 0.005) && r > 0.067; bool dn = abs(r - 0.05) < 0.017; if (cx || dn) col = vec3(0.0); ALBEDO = col; }\n" };   // NO billboard (removed) -> RIGID lens follows the scope model's rotation/placement when inspecting (master); perpendicular to the barrel via the node basis below. 12-gon mask ALIGNED to the ring (+0.2618rad=15deg onto k*30 verts). RETICLE: thin crosshair (half-width 0.012) STOPPING at the donut (r>0.115, not through the center) + a donut ring at r=0.10 (half-thick 0.015)
                        var lensMat = new ShaderMaterial { Shader = lensShader };
                        lensMat.SetShaderParameter("scope_tex", _scopeVp.GetTexture());
                        _scopeLens = new MeshInstance3D { Name = "ScopeLens", Mesh = new QuadMesh { Size = new Vector2(0.105f, 0.105f) }, MaterialOverride = lensMat, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };   // sized to sit inside the bore (0.105: shrunk a touch from 0.11 so it stops peeking past the ring); master-tunable
                        var _iron = mi.GetNodeOrNull<MeshInstance3D>("IronSights");
                        (_iron ?? mi).AddChild(_scopeLens);
                        _scopeLens.Position = new Vector3(0f, -0.17f, -0.0695f);   // CENTERED on the scope's ocular ring: augewehr_sight.txt ring = Xc=0, Zc=-0.0695, r=0.0522 (measured; sight-local -- the mount's SightPos offset handles height-over-bore vs the gun bore). Was Z=-0.06 = ~0.01 too low; a head-on view can't show a Z error. Y=-0.17 = master's depth.
                        var _lb = Basis.Identity; _lb.X = new Vector3(-1f, 0f, 0f); _lb.Y = new Vector3(0f, 0f, -1f); _lb.Z = new Vector3(0f, -1f, 0f); _scopeLens.Basis = _lb;   // RIGID (replaces the billboard): quad perpendicular to the barrel, normal -Y toward the eye, up -Z, UV un-mirrored. Reads face-on down the barrel AND follows the scope's rotation/placement when inspecting (master). Same axis as the scope cam anchor.
                        // Scope CAMERA anchor at the OBJECTIVE (front) end looking downrange (+Y) -- the PiP view must come from
                        // the FRONT lens, NOT the player's eye (master + tinyclaw): an eye-centered cam renders naked-eye
                        // parallax (a zoomed hole); the objective renders what the SCOPE sees (near cover/edges behave like a
                        // real optic). Child of the scope so it rides sway/recoil; mapped VM->main world each frame. Objective
                        // ring center = sight-local (0, +0.1932, -0.0695) -- optical axis, same Z as the lens (tinyclaw's numbers).
                        _scopeCamAnchor = new Node3D { Name = "ScopeCamAnchor" };
                        (_iron ?? mi).AddChild(_scopeCamAnchor);
                        var _cb = Basis.Identity; _cb.X = new Vector3(-1f, 0f, 0f); _cb.Y = new Vector3(0f, 0f, -1f); _cb.Z = new Vector3(0f, -1f, 0f);   // cam -Z=+Y (looks downrange), cam up +Y=-Z (gun up); right-handed (det +1, non-mirrored)
                        _scopeCamAnchor.Transform = new Transform3D(_cb, new Vector3(0f, 0.1932f, -0.0695f));
                    }   // per-gun sight mount (extracted); eaglefire/maplestrike keep the tuned hardcoded pos
                    else if (ironMesh != null)   // non-aug scopable gun: pre-build the PiP rig NOW (at _Ready) so an attachment scope can Configure it on mount -- a runtime-CREATED viewport renders black
                        EnsureScopeRig(mi.GetNodeOrNull<MeshInstance3D>("IronSights") ?? mi);

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
                    if (System.Environment.GetEnvironmentVariable("UG_AIMHOOK") is string _ah && _ah.Split(',').Length == 3)   // tuning: override the per-gun ADS aim hook (find the value that centers iron sights, then bake it)
                    { var _p = _ah.Split(','); _sight.Position = new Vector3(float.Parse(_p[0]), float.Parse(_p[1]), float.Parse(_p[2])); }
                    _ironAimPos = _sight.Position;   // remember the iron ADS hook so removing a scope restores it

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
                    // Full-auto: each shot must ring out FULLY, not restart-cut the previous (master). A lone
                    // AudioStreamPlayer restarts on Play(); an AudioStreamPolyphonic mixes each shot as its OWN
                    // voice so they overlap like real gunfire. Play() arms it once; PlayShoot() adds a voice per shot. Polyphony 32 = headroom past the worst full-auto (zubeknakov ~18 voices = 1.78s x 600rpm); 16 exhausted + PlayStream silently dropped the shot -> cut out on sustained fire (master).
                    _shootStream = LoadOgg($"res://content/{gv.Shoot}");
                    _shootSnd = new AudioStreamPlayer { Stream = new AudioStreamPolyphonic { Polyphony = 32 }, VolumeDb = -3f };
                    mi.AddChild(_shootSnd);
                    _shootSnd.Play();
                    _shootPoly = _shootSnd.GetStreamPlayback() as AudioStreamPlaybackPolyphonic;
                    _reloadSnd = new AudioStreamPlayer { Stream = LoadOgg($"res://content/{gv.Reload}"), VolumeDb = -3f };
                    mi.AddChild(_reloadSnd);
                    _hammerSnd = new AudioStreamPlayer { Stream = LoadOgg($"res://content/{gv.Hammer}"), VolumeDb = -3f };   // the rack / bolt-cycle sound (source ItemGunAsset.hammer) -> plays with the Hammer animation
                    mi.AddChild(_hammerSnd);
                    // dry-fire is its OWN sound, NOT the hammer (master). Vanilla Unturned plays no dry-fire sound (just a RELOAD hint),
                    // so this is null until a real {gun}_dryfire.ogg is ripped -> a null stream just clicks silently for now.
                    _drySnd = new AudioStreamPlayer { Stream = LoadOgg($"res://content/{gv.Gun.Replace("_gun.txt", "")}_dryfire.ogg"), VolumeDb = -3f };
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
            _vpRect = new TextureRect { Texture = _vp.GetTexture(), StretchMode = TextureRect.StretchModeEnum.Scale };
            _vpRect.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            _vpRect.Position = -_vpMargin;              // the oversized image, centred on the screen at 1:1 (see _Ready)
            _vpRect.Size = (Vector2)_vp.Size;   // REVERTED (master): screen-sized composite. The lean-roll self-sets its centre pivot at :1285, so the roll still turns about screen-centre.
            _layer.AddChild(_vpRect);
            AddChild(_layer);
        }

        // Fire: muzzle flash + casing + sound, plus BOTH source per-shot recoils on the viewmodel camera —
        // the positional SHAKE (random [shakeMin,shakeMax] per axis -> _shakeSpring; UseableGun.cs:921/1036) and
        // the rotational tilt (recoilPitch/recoilYaw degrees -> _recoilRotSpring; UseableGun.cs:1037, PlayerAnimator
        // maps x=pitch, y=z=yaw). Both spring back to rest. STAND stance = 1x (crouch/prone scale handled at fire).
        public void Kick(Vector3 shakeMin, Vector3 shakeMax, float recoilPitch, float recoilYaw)
        {
            _flash = 0.05f; EjectCasing(); PlayShoot();   // overlapping voice per shot -- full-auto rings out fully
            _shootHold = 0.25f;   // a shot jumps the gun OUT of the sprint pose (master) + suppresses re-entry for the burst
            // roll the muzzle flash L/R by a random amount each shot, accumulating from the last (master)
            _flashRoll += (_rng.Randf() < 0.5f ? -1f : 1f) * _rng.RandfRange(0.35f, 1.0f);
            _flashMat?.SetShaderParameter("roll", _flashRoll);
            _shakeSpring.CurrentPosition += new Vector3(
                _rng.RandfRange(Mathf.Min(shakeMin.X, shakeMax.X), Mathf.Max(shakeMin.X, shakeMax.X)),
                _rng.RandfRange(Mathf.Min(shakeMin.Y, shakeMax.Y), Mathf.Max(shakeMin.Y, shakeMax.Y)),
                _rng.RandfRange(Mathf.Min(shakeMin.Z, shakeMax.Z), Mathf.Max(shakeMin.Z, shakeMax.Z)));
            // rotational recoil: gun tilts up (pitch) + yaws/rolls (PlayerAnimator maps x=pitch, y=z=yaw), springs back.
            // horizontal (yaw+roll) recoil was inverted -> negate recoilYaw so the gun kicks the correct way (master, noticed in play).
            _recoilRotSpring.CurrentPosition += new Vector3(recoilPitch, -recoilYaw, -recoilYaw);
        }

        // The muzzle has NO world position -- the viewmodel is an isolated SubViewport (OwnWorld3D) at a DIFFERENT
        // FOV, so _muzzleFlash.GlobalPosition is a point in the sub-world, not where the barrel is drawn on the main
        // screen. Screen space is the bridge (the SubViewport is sized to the main viewport's rect, so a pixel here =
        // a pixel there): unproject the muzzle through the viewmodel cam; the caller re-projects it through the WORLD
        // camera. Guard behind-camera first (unprojecting a point behind the cam mirrors it across the screen). Used
        // to anchor the bent bullet tracer's near end at the barrel.
        public bool TryMuzzleScreenPos(out Vector2 px)
        {
            px = default;
            if (_cam == null || _muzzleFlash == null || _cam.IsPositionBehind(_muzzleFlash.GlobalPosition)) return false;
            px = VpToScreen(_cam.UnprojectPosition(_muzzleFlash.GlobalPosition));
            return true;
        }

        // Driven each physics frame by PlayerController: whether the player is moving + their stance, so the
        // walk bob uses the right frequency (SPEED_*) + amplitude (BOB_*) and switches off when standing still.
        public void SetLocomotion(bool moving, EPlayerStance stance, bool safe = false, float moveX = 0f, float moveZ = 0f)
        {
            static bool HandsOff(EPlayerStance st) => st == EPlayerStance.SWIM || st == EPlayerStance.CLIMB;
            bool wasOff = HandsOff(_stance), nowOff = HandsOff(stance);
            _moving = moving; _stance = stance; _safe = safe;
            _moveInput = new Vector2(moveX, moveZ);   // x=strafe, y=forward -> the movement sway tilt
            if (_arms == null) return;
            // 1p arms swim / climb: retail's PlayerAnimator.updateState plays Idle_Swim/Move_Swim and Idle_Climb/Move_Climb on
            // the FIRST-PERSON animator too (not just 3p), and PlayerEquipment.simulate_MustDequip puts the item AWAY for both
            // (a ladder, or water with canUseUnderwater=false). So: the stance clip on the arms, the held model hidden, no ADS
            // (master 2026-09-05: "prevent holding a weapon ... on a ladder ... make sure the gun in your hands is hidden while
            // swimming too"). Back to the held pose + model on leaving.
            if (nowOff)
            {
                string want = stance == EPlayerStance.SWIM ? (moving ? "Move_Swim" : "Idle_Swim") : (moving ? "Move_Climb" : "Idle_Climb");
                if (_arms.ClipLength(want) > 0f) { _arms.SetClipLoop(want, true); _arms.PlayLoop(want); }
                if (!wasOff)
                {
                    if (_aiming) SetAiming(false);
                    if (_gun != null && Godot.GodotObject.IsInstanceValid(_gun)) _gun.Visible = false;   // the item is put away, retail-style
                }
            }
            else if (wasOff)
            {
                if (_gun != null && Godot.GodotObject.IsInstanceValid(_gun)) _gun.Visible = true;
                _arms.Play(_holdClip);
            }
        }

        // ---- INPUT INERTIA (PlayerAnimator.rotationInputViewmodelRoll, source lines 1480-1485) ----------------
        // The gun lags and leans when you swing the view. Source drives it off the per-frame LOOK DELTA rather than
        // the camera's angle, so the impulse is accumulated here by PlayerController's mouse handler and integrated
        // by an RK4 spring below -- that spring is what makes it read as weight rather than as a lerp catching up.
        //
        // Source coefficients, verbatim:
        //     currentPosition.x += deltaPitch * -0.03  * swayMult * bobScale * misalign
        //     currentPosition.y += deltaYaw   * -0.015 * swayMult * bobScale * misalign
        //     currentPosition.z += deltaYaw   * -0.05  *            bobScale * misalign
        // Note the Z term carries NO sway multiplier while X and Y do. That asymmetry is in the source and is
        // reproduced rather than tidied: it means ADS damps the pitch/yaw lag but leaves the ROLL at full strength,
        // so a scoped gun still banks into a turn while its up/down lag is suppressed.
        Rk4Spring3 _inputRoll = new Rk4Spring3(140f, 18f);   // springs back to zero; stiffness/damping are Inspector-side in the source
        Vector3 _inputRollImpulse;                           // accumulated between frames, applied once per tick

        /// <summary>Accumulate a look delta (degrees this frame, already sensitivity-scaled). Called from the input
        /// handler because a mouse delta does not survive to _Process -- by then only the resulting angle remains,
        /// and the angle cannot distinguish a fast flick from a slow pan.</summary>
        public void AddLookDelta(float deltaPitch, float deltaYaw)
        {
            _inputRollImpulse.X += deltaPitch * -0.03f;
            _inputRollImpulse.Y += deltaYaw * -0.015f;
            _inputRollImpulse.Z += deltaYaw * -0.05f;
        }

        // ---- SCOPE SWAY (UseableGun.cs:5983-6021) -----------------------------------------------------------
        // A LISSAJOUS, not a circle: x rides sin(0.75*t) and y rides sin(1.0*t). The mismatched frequencies are the
        // whole trick -- the figure never quite repeats, so the drift reads as a hand rather than as a loop. Getting
        // both axes onto one frequency gives a clean diagonal oscillation that looks mechanical immediately.
        float _swayTime;
        Vector3 _scopeSway;
        /// <summary>The scope's current sway, DEGREES (x=pitch, y=yaw). Read by PlayerController and folded into
        /// the aim so the camera moves and the optic stays put. Source-derived amplitude (1 - 1/zoom), stance
        /// scaling and the SteadyAccuracy breath term all live in the one place that computes it.</summary>
        /// <summary>The gun's own rotational recoil, degrees. Recoil now lands on the AIM, so this must stay at
        /// rest through a burst -- a non-zero reading means a second impulse path grew back on the viewmodel.</summary>
        public Vector3 DebugRecoilRot => _recoilRotSpring.CurrentPosition;
        /// <summary>Set from the equipped gun's Scope_Sway_Scale. 1 = the shared default.</summary>
        public float ScopeSwayScale = 1f;
        public Vector2 ScopeSwayDegrees => new Vector2(_scopeSway.X, _scopeSway.Y);
        /// <summary>Steadiness 0..1 (breath-hold). Source advances swayTime at (1 - steadyAccuracy/4), so steadying
        /// SLOWS the drift rather than shrinking it -- the sight still wanders, just lazily.</summary>
        public float SteadyAccuracy;

        public void PlayDryFire() { _drySnd?.Play(); }   // hammer click when the trigger's pulled on empty

        void PlayShoot()   // one OVERLAPPING polyphonic voice per shot so full-auto shots don't restart-cut each other (master)
        {
            if (_shootStream == null) return;
            _shootPoly ??= _shootSnd?.GetStreamPlayback() as AudioStreamPlaybackPolyphonic;   // (re)fetch lazily in case Play() armed it a frame late
            _shootPoly?.PlayStream(_shootStream);
        }

        public void SwingMelee(bool strong = false)   // play this melee's OWN Weak/Strong swing (source UseableMelee), falling back to the generic knife clip if it wasn't ripped
        {
            PlaySwing();   // src swing WHOOSH (sounds/meleeattack_0{1,2}, random of 2) -- fires for weapons AND bare fists
            if (Fists) { _arms?.Play(strong ? "Punch_Right" : "Punch_Left"); return; }   // bare fists: the real src jab (LMB=left / RMB=right, ported from Punch.fbx)
            string own = _meleeCap + (strong ? "_Strong" : "_Weak");
            _arms?.Play(_meleeCap != null && _arms.ClipLength(own) > 0f ? own : (strong ? "Melee_Strong" : "Melee_Weak"));
        }
        AudioStreamPlayer _swingSnd; AudioStream[] _swingWavs;   // melee/fists SWING whoosh: src sounds/meleeattack_0{1,2} -> content/melee_swing_{0,1}.wav (a random one per swing)
        void PlaySwing()
        {
            _swingWavs ??= new AudioStream[] { PlayerController.LoadWavOneShot("res://content/melee_swing_0.wav"), PlayerController.LoadWavOneShot("res://content/melee_swing_1.wav") };
            if (_swingSnd == null) { _swingSnd = new AudioStreamPlayer { VolumeDb = -5f }; AddChild(_swingSnd); }
            AudioStream w = _swingWavs[(int)(GD.Randi() % 2)];
            if (w != null) { _swingSnd.Stream = w; _swingSnd.Play(); }   // one player, restarts per swing (swings are gated by the cooldown, so no choppy overlap)
        }

        // This weapon's swing-anim length (per-weapon), used as the attack cooldown so click-spam can't beat the cadence.
        public float MeleeSwingLength(bool strong)
        {
            if (_arms == null) return 0f;
            if (Fists) return _arms.ClipLength(strong ? "Punch_Right" : "Punch_Left");
            if (_meleeCap != null) { float l = _arms.ClipLength(_meleeCap + (strong ? "_Strong" : "_Weak")); if (l > 0f) return l; }
            return _arms.ClipLength(strong ? "Melee_Strong" : "Melee_Weak");
        }
        // Repeated tool (blowtorch/chainsaw/jackhammer): the continuous "using" motion. Start_Swing LOOPS while the trigger's
        // held; Stop_Swing plays once on release (source UseableMelee.startSwing/stopSwing). HasStartSwing == "this is a Repeated tool".
        public bool HasStartSwing => _meleeCap != null && _arms != null && _arms.ClipLength(_meleeCap + "_Start_Swing") > 0f;

        /// <summary>Positional shake with NONE of Kick()'s gun furniture -- no muzzle flash, no casing, no shot
        /// sound. A running chainsaw shakes the view continuously; routing that through Kick would eject a brass
        /// case out of it every frame.</summary>
        public void ShakeOnly(Vector3 shakeMin, Vector3 shakeMax)
        {
            _shakeSpring.CurrentPosition += new Vector3(
                _rng.RandfRange(Mathf.Min(shakeMin.X, shakeMax.X), Mathf.Max(shakeMin.X, shakeMax.X)),
                _rng.RandfRange(Mathf.Min(shakeMin.Y, shakeMax.Y), Mathf.Max(shakeMin.Y, shakeMax.Y)),
                _rng.RandfRange(Mathf.Min(shakeMin.Z, shakeMax.Z), Mathf.Max(shakeMin.Z, shakeMax.Z)));
        }
        AudioStreamPlayer _torchSnd;   // the blowtorch "Use" loop (ripped use.wav, NATIVELY looped -> gapless) -- plays while the torch runs
        public void StartTorch(bool sound = true)   // sound: the BLOWTORCH loop -- a chainsaw brings its own (PlayerController.UpdateChainsaw)
        {
            if (!HasStartSwing) return;
            _arms.Play(_meleeCap + "_Start_Swing");
            if (!sound) return;
            if (_torchSnd == null)
            {
                _torchSnd = new AudioStreamPlayer { Stream = LoadWavLooped("res://content/blowtorch_use.wav"), VolumeDb = -5f };
                AddChild(_torchSnd);
            }
            if (!_torchSnd.Playing) _torchSnd.Play();   // native LoopMode.Forward -> seamless, no per-loop gap
        }
        public void StopTorch()
        {
            _torchSnd?.Stop();
            if (_meleeCap == null || _arms == null) return;
            if (_arms.ClipLength(_meleeCap + "_Stop_Swing") > 0f) _arms.Play(_meleeCap + "_Stop_Swing");
            else _arms.Play(_arms.ClipLength(_meleeCap + "_Equip") > 0f ? _meleeCap + "_Equip" : "Melee_Equip");   // no stop clip: settle back to the ready hold
        }
        public void PlayMeleeInspect() { if (_meleeCap != null && _arms != null && _arms.ClipLength(_meleeCap + "_Inspect") > 0f) _arms.Play(_meleeCap + "_Inspect"); }

        CpuParticles3D _torchSparks;   // blowtorch: the REAL "Hit" ParticleSystem from item.prefab -- the game's own blue spark sprite, emitted from the nozzle while the torch is used (source UseableMelee.firstEmitter)
        public void SetTorchSparks(bool on)
        {
            if (_torchSparks == null)
            {
                if (_gun == null) return;
                // the real 16x16 spark sprite ripped from the blowtorch "Hit" ParticleSystem material (_MainTex + _EmissionMap).
                // source startColor is WHITE, so the blue lives in the sprite itself -- albedo tint stays white.
                // glow-emissive shader (torch_spark.gdshader): billboards + rolls each spark a random amount (muzzle-flash style)
                // + outputs HDR so the viewport glow blooms them. Alpha-blended so overlaps stay blue, not white.
                var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/torch_spark.gdshader") };
                var spark = LoadTex("res://content/torch_spark.png");
                if (spark != null) mat.SetShaderParameter("tex", spark);
                var quad = new QuadMesh { Size = new Vector2(0.06f * ParticleFx.SizeScale, 0.06f * ParticleFx.SizeScale), Material = mat };   // spark size baked into the mesh (CpuParticles ScaleAmount doesn't scale the mesh here); ~source startSize 0.05-0.10
                // "Hit" node local pos in item.prefab = (-0.1359, 0.4719, 0) -> port frame (x,y,z)->(-x,y,-z) = (0.1359, 0.4719, 0) (the nozzle tip)
                // Source ParticleSystem params (startSize 0.05-0.10, startSpeed 1-2, sphere r=0.25, gravity x1, lifetime 1s)
                // are WORLD-scale; the viewmodel renders the torch at native model scale (the gun ~0.5 units in view), so the
                // raw values fill the screen. Scaled ~0.2x here so it reads as the game's small blue nozzle spark spray.
                _torchSparks = new CpuParticles3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, 
                    Emitting = false, Amount = ParticleFx.Amount(16), Lifetime = 0.6f, Mesh = quad,
                    Position = TorchNozzlePos(),                                // the NOZZLE head (top of the mesh); UG_TORCHPOS to tune
                    EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.008f,   // tight point at the nozzle so sparks clearly originate there
                    Direction = new Vector3(0f, 1f, 0f), Spread = 45f,           // stream OUT the nozzle (up the torch axis) in a cone, not an omni cloud
                    InitialVelocityMin = 0.3f, InitialVelocityMax = 0.7f,        // source startSpeed 1-2 is world-scale; scaled for the close viewmodel
                    Gravity = new Vector3(0f, -2.2f, 0f),                        // fall off
                };
                _gun.AddChild(_torchSparks);
            }
            _torchSparks.Emitting = on;
        }

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
        /// <summary>Is the gun shouldered? Read by the stance FSM, which must not enter SPRINT while it is
        /// (PlayerStance.cs:701 `doesEquipmentAllowSprinting`).</summary>
        public bool IsAiming => _aiming;

        // ...and the other half of the same rule: you cannot START an aim while sprinting (UseableGun.cs:3221,
        // `canStartAim &= isSprinting == false || equippedGunAsset.canAimDuringSprint`). Both directions are gated in
        // source and they are NOT the same statement -- one withholds the stance, the other withholds the aim -- so
        // implementing either alone still leaves a way into sprint-ADS. `_stance == SPRINT && _moving` mirrors retail's
        // own isSprinting (UseableGun.cs:3947). Can_Aim_During_Sprint is a per-gun .dat exception, default false, and
        // nothing in our content sets it -- so this is unconditional here until something does.
        public void SetAiming(bool on) { if (on && (!EquipDone || _attachView || _reloading || _hammering || (_stance == EPlayerStance.SPRINT && _moving) || _stance == EPlayerStance.SWIM || _stance == EPlayerStance.CLIMB)) return; /* no ADS with the item put away (swim / ladder) */ if (on && _inspecting) CancelInspect(); _aiming = on; }   // no ADS while the attach menu is up, or during ANY active reload / rack / bolt-cycle (source canStartAim: !isReloading && !isHammering) (master); ADS mid-inspect cancels the inspect then aims
        // Consumable eat/drink motion on click -- this item's OWN archetype (CU_n: eat/drink/pills/syringe/bandage),
        // else the generic Consume_Use, else re-raise (Melee_Equip placeholder).
        public void PlayConsumeUse()
        {
            _arms?.Play(ConsumeUseClipName(), 1f);
        }

        // Deployable place motion on LMB -- the src barricade "Use" clip (UseableBarricade.build plays "Use"). One-shot.
        public void PlayDeployUse()
        {
            if (_arms != null && _arms.ClipLength("Deploy_Use") > 0f) { _arms.SetClipLoop("Deploy_Use", false); _arms.Play("Deploy_Use", 1f); }
        }
        public float DeployUseLength() => _arms != null ? _arms.ClipLength("Deploy_Use") : 0f;
        // Return to the ready carry hold (Deploy_Equip end pose) after a place when there's still one in the stack.
        public void PlayDeployHold()
        {
            if (_arms != null && _arms.ClipLength("Deploy_Equip") > 0f) { _arms.SetClipLoop("Deploy_Equip", false); _arms.SnapToEnd("Deploy_Equip"); }
        }
        string ConsumeUseClipName()
            => (_arms != null && _arms.ClipLength(ConsumableUseClip) > 0f) ? ConsumableUseClip
             : (_arms != null && _arms.ClipLength("Consume_Use") > 0f) ? "Consume_Use" : "Melee_Equip";
        // source: useTime = length of the "Use" clip. Per-item; 0 -> caller uses its own default.
        public float ConsumeUseLength() => _arms != null ? _arms.ClipLength(ConsumeUseClipName()) : 0f;

        // Dynamic world lights (muzzle flash / headlights / flares) mirrored into the subviewport so they spill onto the
        // gun -- ADDITIVE on top of the sun-mirror + ambient rig. Each entry = the light's position in the player CAMERA's
        // local space (so it hits the gun from the same direction as it hits the player) + color/energy/range. Pooled + capped.
        readonly System.Collections.Generic.List<OmniLight3D> _worldMirrors = new();
        public void SetWorldLights(System.Collections.Generic.IReadOnlyList<(Vector3 camLocalPos, Color color, float energy, float range)> lights)
        {
            if (_cam == null) return;
            int n = lights.Count;
            while (_worldMirrors.Count < n)
            {
                var l = new OmniLight3D { ShadowEnabled = false, OmniAttenuation = 1.0f };
                _cam.AddChild(l);   // child of the subviewport camera -> its local position IS the view-space offset
                _worldMirrors.Add(l);
            }
            for (int i = 0; i < _worldMirrors.Count; i++)
            {
                var m = _worldMirrors[i];
                if (i < n) { var (p, c, e, r) = lights[i]; m.Position = p; m.LightColor = c; m.LightEnergy = e; m.OmniRange = r; m.Visible = true; }
                else m.Visible = false;
            }
        }

        public void SetReloading(bool on, float speed = 1f)
        {
            _reloading = on;
            if (on) { _aiming = false; _arms?.Play(_reloadClip, speed); if (_reloadSnd != null) { _reloadSnd.PitchScale = speed; _reloadSnd.Play(); } }   // per-gun reload arm anim + sound, sped up by DEXTERITY
        }
        // The rechamber RACK (source Hammer clip) -- the 2nd half of an empty reload. Stays in the reloading state so ADS/fire stay blocked.
        public bool HasHammer => _hammerClip != null;
        public float HammerLength => (_arms != null && _hammerClip != null) ? _arms.ClipLength(_hammerClip) : 0f;
        public void PlayHammer(float speed = 1f)
        {
            if (_hammerClip == null) return;
            _arms?.Play(_hammerClip, speed);
            if (_hammerSnd != null) { _hammerSnd.PitchScale = speed; _hammerSnd.Play(); }   // the real rack / bolt-cycle sound (was missing) -- master
            _aiming = false;                                                // master: working the bolt/pump DROPS you out of ADS (SetAiming already blocks re-aim while _hammering; source canStartAim = !isHammering)
            _hammering = true; _hammerCapture = true;                       // follow the hand bone through the rack so the gun rotates with it
            _hammerViewTimer = HammerLength / Mathf.Max(0.01f, speed);      // for the clip's (dexterity-sped) duration
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
            _arms?.SnapToEnd(_holdClip);   // snap the arms to the equip-END (the ready hold), no pull-out replay
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
            _arms?.SnapToEnd(_holdClip);
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
            if (slot == "Sight" && !on) HideScopePiP();   // a hidden Sight slot must not leave a live scope picture / scope aim hook behind
            if (_attachMesh.TryGetValue(slot, out var n)) { var m = _gun?.GetNodeOrNull<MeshInstance3D>(n); if (m != null) m.Visible = on; }
        }
        public string DefaultSightTxt => _defaultSightTxt;   // the gun's own iron-sight mesh (null/empty = the gun has no irons of its own)
        // Attachment state as a bitmask over AttachSlots (bit set = that slot's model is attached) -- persisted on the gun's Item so
        // a detached suppressor/sight etc. survives hands<->inventory<->drop (master). Only slots the gun HAS a model for count.
        static readonly string[] AttachSlots = { "Sight", "Tactical", "Grip", "Barrel", "Magazine" };
        // True only when this viewmodel is actually showing a GUN (not fists / a melee / a consumable / empty hands).
        // GetAttachMask is meaningless on a non-gun viewmodel -> callers must gate on this before saving a gun's mask.
        public bool IsGunViewmodel => !EmptyHands && !Fists && MeleeMesh == null && ConsumableMesh == null && DeployableMesh == null && ToolMesh == null;
        public bool IsRopeTool;   // this tool viewmodel is the tow ROPE (item 64) -- all tools set ToolMesh; the kind bits disambiguate
        public bool IsHoseTool;   // this tool viewmodel is the fluid HOSE (item 66)
        public bool IsDetonatorTool;   // this tool viewmodel is the remote-charge DETONATOR (item 1240)
        public bool IsWireViewmodel => ToolMesh != null && !IsRopeTool && !IsHoseTool && !IsDetonatorTool;
        public bool IsRopeViewmodel => ToolMesh != null && IsRopeTool;
        public bool IsHoseViewmodel => ToolMesh != null && IsHoseTool;
        public bool IsDetonatorViewmodel => ToolMesh != null && IsDetonatorTool;
        public int GetAttachMask() { int m = 0; for (int i = 0; i < AttachSlots.Length; i++) if (SlotHasModel(AttachSlots[i]) && SlotAttached(AttachSlots[i])) m |= 1 << i; return m; }
        public void ApplyAttachMask(int mask) { for (int i = 0; i < AttachSlots.Length; i++) if (SlotHasModel(AttachSlots[i])) SetSlotAttached(AttachSlots[i], (mask & (1 << i)) != 0); }
        // swap the slot's model to a named attachment (null/empty = detach). Alternate attachments are calibrated to
        // the same child-node position as the default, so swapping just the mesh mounts the new part on the same hook.
        public void SetSlotMesh(string slot, string txtName)
        {
            if (!_attachMesh.TryGetValue(slot, out var n)) return;
            var m = _gun?.GetNodeOrNull<MeshInstance3D>(n);
            if (m == null) return;
            if (slot == "Sight") { HideScopePiP(); var _oldRet = m.GetNodeOrNull("Reticle"); if (_oldRet != null) { m.RemoveChild(_oldRet); _oldRet.QueueFree(); } }   // deactivate any prior scope's PiP + drop any prior red-dot reticle before the swap
            if (string.IsNullOrEmpty(txtName)) { m.Visible = false; return; }
            m.Mesh = ContentProvider.ParseObj($"res://content/{txtName}");
            m.Visible = true;   // the node may have been hidden by a detach -- a freshly mounted mesh must show (was: new scope stayed invisible after detaching the old one)
            if (slot == "Sight")   // scopes/optics: each scope's REAL body colour from source (7x gray, most near-black); satin metal
            {
                bool _isSc = ScopeCal.TryGetValue(txtName, out var _sc);
                // a re-fitted IRON SIGHT (this gun's own default sight mesh, or an *_iron_sights mesh) restores its real
                // per-gun _Color; only a ported red-dot (non-ScopeCal, non-iron) keeps the near-black default. Without
                // this, detach+re-attach of iron sights went jet-black -- they carry no texture, just a flat _Color, and
                // fell through to the 0.06 body colour meant for red-dots.
                bool _isIron = txtName == _defaultSightTxt || txtName.Contains("iron_sights");
                Color _bodyCol = _isSc ? _sc.Col : (_isIron ? _sightColor : new Color(0.06f, 0.065f, 0.075f));
                m.MaterialOverride = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = _bodyCol, Metallic = 0f, MetallicSpecular = 0f, Roughness = 1f };   // FULLY MATTE like the gun body/irons/mags -- Unturned guns are non-reflective (master: "why are the scope bodies so shiny"); the old satin 0.35/0.5 broke that convention
                m.Position = _defaultSightPos;   // mount at the gun's SightPos (iron/scope/red-dot all share this)
                if (_sight != null) _sight.Position = _defaultAimHook;   // ADS aim at the gun's eye point (iron/scope/red-dot all share this; a red dot just adds a reticle billboard, no aim override)
                if (_isSc)
                {
                    // Real ripped reticle (source): each scope's Reticule submesh texture, saved as <base>_reticle.png. The
                    // white +/^ (cross/chevron) are tinted RED at runtime (source: criticalHitmarkerColor); the black crosshairs
                    // + baked-red X use white tint. Scale = the reticle's fraction of the glass (measured): dots ~0.056, rest 1.0.
                    string _retName = txtName.Replace("_sight.txt", "_reticle.png");
                    Texture2D _retTex = null;
                    string _rp = ProjectSettings.GlobalizePath($"res://content/{_retName}");
                    if (System.IO.File.Exists(_rp)) { var _ri = ContentProvider.LoadImage(_rp); if (_ri != null) _retTex = ImageTexture.CreateFromImage(_ri); }
                    bool _dot = txtName.Contains("cross") || txtName.Contains("chevron");
                    ConfigureScopePiP(_sc.Lens, _sc.Obj, _sc.Aim, _sc.Fov, _sc.Size, _sc.Sides, _retTex, _dot ? 0.056f : 1.0f, _dot ? new Color(1f, 0f, 0f) : new Color(1f, 1f, 1f));   // real PiP zoom + ADS aim + ripped reticle
                    _scopeHasLadder = txtName.StartsWith("scope_");   // the tube zoom scopes (8x/7x/16x) carry the numbered 100/200/300m range ladder
                }
                else if (RedDotCal.TryGetValue(txtName, out var _rd))
                {
                    // RED DOT / HOLO / KOBRA: the optic mesh renders as the housing (opaque near-black), mounted + ADS-aimed
                    // exactly like the iron sight. On top of it we add a billboard reticle glowing red, snapped to the source
                    // OPTICAL AXIS (RedDotCal.Pos, X=0 Z=-0.072 from the Aim/Reticule node) so it centers in the ring like the
                    // scopes do. Reticle textures (<name>_reticle.png) are white shape-masks tinted by Glow. NoDepthTest keeps
                    // it visible over the halo's opaque combiner glass. (The source Reticule is a runtime emissive billboard --
                    // this reproduces that, vs. the old merged extraction that baked it into the mesh as a static black disc.)
                    string _retName = txtName.Replace("_sight.txt", "_reticle.png");
                    var _retMat = new StandardMaterial3D
                    {
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        AlbedoColor = _rd.Glow,
                        EmissionEnabled = true, Emission = _rd.Glow, EmissionEnergyMultiplier = 2.5f,
                        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                        TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                        NoDepthTest = true,   // a reflex reticle is a projected overlay -> always draws on top. The HALO's
                        // Model_0 bakes an opaque combiner-glass plate at the optical center; without this it sits between the
                        // eye and the reticle and occludes it (renders as a BLACK disc). red_dot/kobra are open rings (no-op there).
                    };
                    string _rp = ProjectSettings.GlobalizePath($"res://content/{_retName}");
                    if (System.IO.File.Exists(_rp)) { var _ri = ContentProvider.LoadImage(_rp); if (_ri != null) { var _rt = ImageTexture.CreateFromImage(_ri); _retMat.AlbedoTexture = _rt; _retMat.EmissionTexture = _rt; } }
                    m.AddChild(new MeshInstance3D { Name = "Reticle", Mesh = new QuadMesh { Size = new Vector2(_rd.Size, _rd.Size) }, MaterialOverride = _retMat, Position = _rd.Pos, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                    // ADS through the optic's OWN aim (gun-local composed aim), so the eye tucks behind the sight instead of
                    // aligning to the gun's iron eye-point -- which parked the gun at the wrong height/angle in the sight
                    // picture (master: gun pose wrong on the holos). Restored to iron on swap by HideScopePiP.
                    // ⚠ _rd.Aim is EAGLEFIRE-TUNED (composed from its Sight hook) -- gate to the eaglefire so red-dots on OTHER
                    // guns keep their working per-gun iron aim (the default set above) instead of regressing. TODO generalise:
                    // compose port(SightHook + sightModel0 + sightAim) per gun (needs the per-gun Sight hook, e.g. guns_sighthook.tsv).
                    if (_sight != null && _gunTxt != null && _gunTxt.Contains("eaglefire")) _sight.Position = _rd.Aim;
                }
            }
            m.Visible = true;
        }

        // Red-dot / holo / kobra calibration. Pos = the glowing reticle on the sight's OPTICAL AXIS (X=0, Z=-0.072) so it
        // centers in the ring. Aim = the sight's own `Aim` NODE (source, port) = the ADS eye-point behind the optic; ADS
        // sets _sight.Position = mount + Aim so you look THROUGH the sight like the scopes do (ConfigureScopePiP line ~1098),
        // NOT at iron-sight height/angle (master: "gun angle/position wrong on the holos"). Both come from the source node
        // at Model_0-local (0,*,+0.07203) for Pos and (0, Y,+0.072) for Aim (measured, tools/reddot_axis_probe.py; export_mesh
        // negates Z). In this sight-local frame Z is VERTICAL, Y is fore-aft/depth: the reticle billboard's Y only sets
        // apparent size; the Aim's Y (-0.30/-0.32) is the eye relief behind the glass. Size = dot size. Glow = RED.
        static readonly System.Collections.Generic.Dictionary<string, (Vector3 Pos, Vector3 Aim, float Size, Color Glow)> RedDotCal = new()
        {   // Pos = reticle on the optical axis (rel the mount node). Aim = the ADS eye-point, GUN-LOCAL port =
            // port(SightHook + sight Model_0 + sight Aim) -- the composed aim so ADS looks THROUGH the optic (source-
            // accurate, same as extract_gun_sights.py real_aims / the scopes). NOTE: eaglefire-tuned (uses the eaglefire
            // Sight hook 0,-0.2398,-0.1386); iron aim is 0,-0.4688,-0.2098, these tuck the eye ~0.05 higher + 0.03 forward.
            { "red_dot_sight.txt",   (new Vector3(0f, -0.1884f, -0.0655f), new Vector3(0f, -0.4183f, -0.1831f), 0.014f, new Color(1f, 0f, 0f)) },
            { "red_halo_sight.txt",  (new Vector3(0f, -0.2970f, -0.0813f), new Vector3(0f, -0.4576f, -0.1990f), 0.016f, new Color(1f, 0f, 0f)) },
            { "red_kobra_sight.txt", (new Vector3(0f, -0.1884f, -0.0655f), new Vector3(0f, -0.4183f, -0.1831f), 0.02f,  new Color(1f, 0f, 0f)) },
        };

        // ---- Generalized PiP scope (master: real zoom-THROUGH the attachment scopes, not the cheap fov-drop) ----
        // The aug's INTEGRATED scope builds this inline (gun-construction); attachment scopes (8x/7x/16x/makeshift/cross/
        // chevron/shadowstalker) mount later via SetSlotMesh and call BuildScopePiP. Same two-render PiP as the aug: a 2nd
        // cam at the scope's OBJECTIVE renders the world at a narrow fov (90/zoom) into a RIGID lens quad at the OCULAR ring;
        // the generic block in _Process drives it (world-bind + linear env + objective zeroing). See reference_unturned_scope_pip.
        struct ScopeC { public Vector3 Lens, Obj, Aim; public float Fov, Size; public int Sides; public Color Col; public ScopeC(Vector3 l, Vector3 o, Vector3 aim, float f, float s, int sides, Color col) { Lens = l; Obj = o; Aim = aim; Fov = f; Size = s; Sides = sides; Col = col; } }
        static readonly System.Collections.Generic.Dictionary<string, ScopeC> ScopeCal = new()
        {
            // mesh -> (lens@ocular-ring, cam-anchor@objective, fov=90/zoom, lens-size=2*ocular-radius) -- MEASURED from each scope's .txt verts; zoom from the retail .dat.
            // lens at the NATURAL ocular ring (ymin+~0.008, muzzle-ward per master) -- the occluding Reticule face was removed
            // from the meshes so the lens no longer needs to sit eye-side of it; size ~= 2*ocular-radius to fill the ring.
            // Sides = the scope's actual internal-ring shape, MEASURED per scope (master): tube scopes 12-gon,
            // makeshift = HEXAGON (6), cross/chevron = 12, shadowstalker = SQUARE (4).
            // Aim = the scope's own `Aim` node (sight-local, MEASURED) -- ADS moves the aim hook here so you look THROUGH the glass (Attachments.cs:590). ~0.15 behind the ocular = real eye relief.
            // Lens + Obj X,Z now SHARE the Aim node's X,Z (= the true optical axis, the source's own eye alignment)
            // so the lens/reticle sit dead-center on the axis the eye aligns to (fixes cross/chevron mis-placement +
            // off-center reticle). Only Y (depth along the tube) differs per element. Col = the scope's real body colour
            // (Model_0 _MainTex median, MEASURED from source): 7x is GRAY, most are near-black, shadowstalker blue-grey.
            { "scope_8x_sight.txt",            new ScopeC(new Vector3( 0f,      -0.364f, -0.1086f), new Vector3( 0f,       0.149f, -0.1086f), new Vector3( 0f,      -0.5122f, -0.1086f), 11.25f, 0.124f, 12, new Color(0.118f,0.118f,0.118f)) },   // 8x  (30,30,30)
            { "scope_7x_sight.txt",            new ScopeC(new Vector3( 0f,      -0.364f, -0.0868f), new Vector3( 0f,       0.149f, -0.0868f), new Vector3( 0f,      -0.4893f, -0.0868f), 12.86f, 0.115f, 12, new Color(0.588f,0.588f,0.588f)) },   // 7x  GRAY (150,150,150); lens bumped 0.087->0.112
            { "scope_16x_sight.txt",           new ScopeC(new Vector3( 0f,      -0.364f, -0.1086f), new Vector3( 0f,       0.149f, -0.1086f), new Vector3( 0f,      -0.5122f, -0.1086f),  5.63f, 0.124f, 12, new Color(0.118f,0.118f,0.118f)) },   // 16x (30,30,30)
            { "makeshift_scope_sight.txt",     new ScopeC(new Vector3(-0.0021f, -0.374f, -0.1149f), new Vector3(-0.0021f,  0.120f, -0.1149f), new Vector3(-0.0021f, -0.4705f, -0.1149f), 15.0f,  0.080f,  6, new Color(0.235f,0.235f,0.235f)) },   // Makeshift HEX (60,60,60); a hair wider 0.072->0.078
            { "cross_scope_sight.txt",         new ScopeC(new Vector3( 0f,      -0.347f, -0.0724f), new Vector3( 0f,      -0.148f, -0.0724f), new Vector3( 0f,      -0.4667f, -0.0724f), 15.0f,  0.090f, 12, new Color(0.118f,0.118f,0.118f)) },   // Cross 12-gon (30,30,30); Z ->optical axis (was -0.056)
            { "chevron_scope_sight.txt",       new ScopeC(new Vector3( 0f,      -0.355f, -0.0760f), new Vector3( 0f,      -0.110f, -0.0760f), new Vector3( 0f,      -0.4541f, -0.0760f), 22.5f,  0.076f, 12, new Color(0.118f,0.118f,0.118f)) },   // Chevron 12-gon (30,30,30); Z ->optical axis
            { "shadowstalker_scope_sight.txt", new ScopeC(new Vector3( 0f,      -0.364f, -0.0927f), new Vector3( 0f,       0.149f, -0.0927f), new Vector3( 0f,      -0.4893f, -0.0927f), 15.0f,  0.124f,  4, new Color(0.192f,0.18f,0.2f)) },   // Shadowstalker SQUARE; _Color (0.192,0.18,0.2)
        };

        // Build the PiP rig ONCE at gun-construction (_Ready) -- a SubViewport CREATED AT RUNTIME renders BLACK (its render
        // target never inits like it does during the initial tree render), so the aug's inline PiP works but a runtime build
        // does not. We pre-build the rig here (lens hidden, _isScope=false) and only RECONFIGURE it when a scope mounts.
        void EnsureScopeRig(MeshInstance3D host)
        {
            if (_scopeVp != null && Godot.GodotObject.IsInstanceValid(_scopeVp)) return;   // once per gun
            _scopeVp = new SubViewport { Size = new Vector2I(GraphicsOptions.ScopeSize, GraphicsOptions.ScopeSize), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, OwnWorld3D = false };   // OwnWorld3D=false -> renders the parent (main) world; built at _Ready so the render target initialises
            AddChild(_scopeVp);
            _scopeCam = new Camera3D { Current = true, Fov = 20f };
            _scopeCam.CullMask &= ~OutlineOverlay.OutlineLayer;   // exclude the outline silhouette layer (19) -- else focus outlines tint the whole object in the scope (master)
            _scopeVp.AddChild(_scopeCam);
            _dnc = GetTree().GetFirstNodeInGroup("daynight") as DayNightCycle;
            Godot.Environment _mainEnv = _dnc?.Env;
            if (_mainEnv == null)   // no day/night (firetest/vm harness): find the main WorldEnvironment (skip the arms _vpEnv)
                foreach (var _n in GetTree().Root.FindChildren("*", "WorldEnvironment", true, false))
                    if (_n is WorldEnvironment _we && _we.Environment != null && _we.Environment != _vpEnv) { _mainEnv = _we.Environment; break; }
            if (_mainEnv != null)
            {
                _scopeEnv = (Godot.Environment)_mainEnv.Duplicate();   // LINEAR copy so the lens isn't double-tonemapped by _vp's ACES (Sky is a shared sub-resource -> auto-syncs)
                _scopeEnv.TonemapMode = Godot.Environment.ToneMapper.Linear;
                _scopeEnv.GlowEnabled = false;
                _scopeCam.Environment = _scopeEnv;
            }
            var lensShader = new Shader { Code =   // mask SHAPE per scope via u_seg/u_rot; the RETICLE is the scope's REAL ripped texture (reticle_tex) composited on the glass, sized by ret_scale + tinted by ret_tint (white texture -> red for cross/chevron; black/red reticles use white tint). Set in ConfigureScopePiP.
                "shader_type spatial;\n" +
                "render_mode unshaded, cull_disabled, shadows_disabled;\n" +
                "uniform sampler2D scope_tex : source_color, filter_linear;\n" +
                "uniform sampler2D reticle_tex : source_color, filter_linear;\n" +
                "uniform float u_seg = 0.5235988;\n" +
                "uniform float u_rot = 0.2618;\n" +
                "uniform float ret_scale = 1.0;\n" +
                "uniform vec3 ret_tint = vec3(1.0);\n" +
                "void fragment() { vec2 p = (UV - vec2(0.5)) * 2.0; float a = atan(p.y, p.x) + u_rot; float dd = cos(floor(0.5 + a/u_seg) * u_seg - a) * length(p); if (dd > 0.95) discard; vec3 col = texture(scope_tex, UV).rgb; vec2 ruv = (UV - vec2(0.5)) / ret_scale + vec2(0.5); if (ruv.x >= 0.0 && ruv.x <= 1.0 && ruv.y >= 0.0 && ruv.y <= 1.0) { vec4 ret = texture(reticle_tex, ruv); col = mix(col, ret.rgb * ret_tint, ret.a); } ALBEDO = col; }\n" };
            var lensMat = new ShaderMaterial { Shader = lensShader };
            lensMat.SetShaderParameter("scope_tex", _scopeVp.GetTexture());
            var _lb = Basis.Identity; _lb.X = new Vector3(-1f, 0f, 0f); _lb.Y = new Vector3(0f, 0f, -1f); _lb.Z = new Vector3(0f, -1f, 0f);   // RIGID, perpendicular to the barrel
            _scopeLens = new MeshInstance3D { Name = "ScopeLens", Mesh = new QuadMesh { Size = new Vector2(0.1f, 0.1f) }, MaterialOverride = lensMat, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            host.AddChild(_scopeLens);
            _scopeLens.Basis = _lb;
            _scopeCamAnchor = new Node3D { Name = "ScopeCamAnchor" };
            host.AddChild(_scopeCamAnchor);
            var _cb = Basis.Identity; _cb.X = new Vector3(-1f, 0f, 0f); _cb.Y = new Vector3(0f, 0f, -1f); _cb.Z = new Vector3(0f, -1f, 0f);
            _scopeCamAnchor.Basis = _cb;
            _scopeHost = host;   // the scope mesh sits on this node (at the gun's SightPos); the scope's Aim node is host-local
            // _isScope stays FALSE -> the _Process PiP block is inactive + the lens hidden until a scope Configures it on.
        }

        // 1x1 fully-transparent texture for scopes with no ripped reticle -- so the lens composite is a no-op and the
        // glass shows, instead of an unset sampler reading opaque white and whiting out the whole lens.
        static ImageTexture _blankReticle;
        static ImageTexture BlankReticle()
        {
            if (_blankReticle == null || !Godot.GodotObject.IsInstanceValid(_blankReticle))
            {
                var img = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
                img.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
                _blankReticle = ImageTexture.CreateFromImage(img);
            }
            return _blankReticle;
        }

        // Point the pre-built rig at a specific scope (mesh's measured ocular/objective + fov=90/zoom + lens size + ADS aim). Called on mount.
        void ConfigureScopePiP(Vector3 lensLocal, Vector3 objLocal, Vector3 aimLocal, float fov, float lensSize, int sides, Texture2D reticleTex, float retScale, Color retTint)
        {
            if (_scopeVp == null || !Godot.GodotObject.IsInstanceValid(_scopeLens)) return;   // gun has no rig
            _scopeLens.Mesh = new QuadMesh { Size = new Vector2(lensSize, lensSize) };
            _scopeLens.Position = lensLocal;
            _scopeCam.Fov = fov;
            _scopeCamAnchor.Position = objLocal;
            if (_scopeLens.MaterialOverride is ShaderMaterial _sm)   // mask shape to match the scope's ocular: 4=square (verts on the diagonals), else N-gon (rot puts a vertex up)
            {
                _sm.SetShaderParameter("u_seg", 2f * Mathf.Pi / sides); _sm.SetShaderParameter("u_rot", sides == 4 ? 0f : Mathf.Pi / sides);
                _sm.SetShaderParameter("reticle_tex", reticleTex ?? BlankReticle());   // a MISSING reticle must be TRANSPARENT: an unset sampler2D samples OPAQUE WHITE, and the shader mix(col,white,1) then whites out the whole lens even with a live cam (that's the "blank scope" -- it was really a reticleless one)
                _sm.SetShaderParameter("ret_scale", retScale);
                _sm.SetShaderParameter("ret_tint", new Vector3(retTint.R, retTint.G, retTint.B));
            }
            // ADS aim through the SCOPE: move the aim hook to the scope's `Aim` node (host-local -> gun-local via the host's SightPos), so ADS lines the eye up with the ocular instead of the gun's irons (Attachments.cs:590).
            if (_sight != null && _scopeHost != null) _sight.Position = _scopeHost.Position + aimLocal;
            _isScope = true;
        }

        void HideScopePiP()   // scope removed/swapped: deactivate + hide the lens + restore the iron ADS hook; the rig stays built (rebuilding it at runtime renders black)
        {
            _isScope = false; _scopeWasOn = false; _scopeHasLadder = false;
            if (_scopeLens != null && Godot.GodotObject.IsInstanceValid(_scopeLens)) _scopeLens.Visible = false;
            if (_ladder != null) _ladder.Active = false;
            if (_sight != null) _sight.Position = _ironAimPos;   // back to iron-sight ADS alignment
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
            screen = VpToScreen(_cam.UnprojectPosition(world));
            return true;
        }

        // Length (s) of the equipped gun's reload clip, so PlayerController times the ammo refill to the real anim
        // (rifles 1.633s, the masterkey's break-action 2.467s). Falls back to the eaglefire length.
        public float ReloadLength => _arms != null && _arms.ClipLength(_reloadClip) > 0f ? _arms.ClipLength(_reloadClip) : 1.633f;

        public float AimAlpha => _aimAlpha;   // 0 hip .. 1 ADS, for spread/accuracy
        // 1P lean tilt (master: "shooting while leaning in 1p feels off"). The viewmodel lives in an isolated
        // SubViewport that can't inherit the lean pivot's roll, so the gun stayed upright while the world tilted.
        // The player pushes its already-lerped, already-obstruct-snapped _leanAngle here each frame (LeanLerp=4/s =
        // retail's rate -- DON'T re-lerp/re-snap, that double-lerps and feels mushy, tinyclaw); we roll the arms root
        // by it. Source: PlayerAnimator.cs:1537 rolls player.first by lean*HumanAnimator.LEAN (=20deg). Stylistic tilt.
        public float LeanRoll;   // degrees of Z-roll to apply to the viewmodel this frame (0 upright)
        public string CasingSurface = "general";   // PlayerController feeds the surface under the feet (metal/wood/sand/water/general) for the casing bounce bank
        public float ScopeZoom => _isScope && _scopeCam != null && Godot.GodotObject.IsInstanceValid(_scopeCam) ? 90f / _scopeCam.Fov : 0f;   // mounted scope's zoom (90/fov): aug=4, 8x=8, 16x=16... -> drives ADS-sens reduction

        public void SetShown(bool shown) { if (_layer != null) _layer.Visible = shown; }
        public string ShownDebug => $"layer={( _layer == null ? "null" : _layer.Visible.ToString())} layerN={_layer?.Layer} rectVis={_vpRect?.IsVisibleInTree()} rectParent={_vpRect?.GetParent()?.Name}";
        /// <summary>A lens disc on the CARRIED model (binoculars: one per eyepiece, mesh-local placement) drawing `mat` -- the
        /// magnified-world disc of content/binoculars.gdshader. False until the held model exists (call again next tick).</summary>
        public bool AddHeldLens(Material mat, Vector3 localPos, Vector3 localRotDeg, float radius)
        {
            if (_gun == null || !Godot.GodotObject.IsInstanceValid(_gun)) return false;
            bool dbg = System.Environment.GetEnvironmentVariable("UG_LENSDBG") == "1";
            var disc = new MeshInstance3D { Name = "HeldLens", Mesh = new QuadMesh { Size = Vector2.One * (radius * 2f * (dbg ? 3f : 1f)), Material = mat },
                                            Position = localPos, RotationDegrees = localRotDeg, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _gun.AddChild(disc);
            if (dbg) _lensDbg.Add(disc);
            return true;
        }
        readonly System.Collections.Generic.List<MeshInstance3D> _lensDbg = new(); int _lensDbgT;
        void LensDebugTick()
        {
            if (_lensDbg.Count == 0 || ++_lensDbgT % 60 != 0) return;
            var d = _lensDbg[0];
            GD.Print($"[lensdbg] disc {d.GlobalPosition} vis={d.IsVisibleInTree()} gun {_gun?.GlobalPosition} gunVis={_gun?.IsVisibleInTree()} gunChildren={_gun?.GetChildCount()} cam {_cam?.GlobalPosition} camFwd {-_cam?.GlobalTransform.Basis.Z} gunAabb={(_gun as MeshInstance3D)?.GetAabb()} gunType={_gun?.GetType().Name}");
        }
        public static readonly Vector2 ViewportOversize = new Vector2(VpOverX, VpOverY);   // for a SCREEN_UV-sampling material drawn in the arms viewport

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
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
            if (_isScope && _scopeVp != null)   // real two-render PiP: world at a NARROW fov into the lens, periphery stays 1x (NOT the cheap FOV-drop)
            {
                var _main = GetViewport().GetCamera3D();
                bool _on = _main != null && _cam != null && _scopeCamAnchor != null;   // render whenever the scope gun is OUT -- even at the hip, not just ADS (master)
                if (_on)
                {
                    if (_scopeVp.World3D != _main.GetWorld3D()) _scopeVp.World3D = _main.GetWorld3D();   // render the REAL world, not the VM's isolated arms-world (tinyclaw)
                    if (_scopeEnv != null && _dnc?.Env is Godot.Environment _me)   // keep the scope's LINEAR env in sync with the day/night (ambient + fog drift with time-of-day; the Sky is a shared sub-resource so it auto-updates)
                    {
                        _scopeEnv.AmbientLightColor = _me.AmbientLightColor;
                        _scopeEnv.FogEnabled = _me.FogEnabled; _scopeEnv.FogDensity = _me.FogDensity;
                        _scopeEnv.FogLightColor = _me.FogLightColor; _scopeEnv.FogSkyAffect = _me.FogSkyAffect;
                    }
                    // PiP view from the scope's OBJECTIVE end, not the eye: map the anchor's pose (relative to the arms cam _cam) into the main world (relative to _main). Rides the gun's sway/recoil + looks down the barrel -> works at the hip too, not just ADS.
                    // The scope cam RIGIDLY rides the scope's optical axis (master: "parent the camera to the scope itself").
                    // _objPose is the objective anchor mapped VM->main world; its -Z already points downrange along the optic.
                    // Using it AS the cam pose -- rather than LookingAt a fixed forward zero point -- means the PiP shows the
                    // TRUE angle the scope points at all times (inspect, hip, sway, lean) for free, instead of snapping back to
                    // the player's forward (the old bug: the glass showed a level forward view while the gun tilted away).
                    // ADS still zeroes: aiming aligns the optic with the eye-line so the reticle (lens center) sits on the aim
                    // point; the small height-over-bore drop is a reticle-offset follow-up, not a look-direction issue.
                    var _objPose = _main.GlobalTransform * (_cam.GlobalTransform.AffineInverse() * _scopeCamAnchor.GlobalTransform);
                    _scopeCam.GlobalTransform = _objPose;
                    _scopeVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                    _scopeLens.Visible = true; _scopeWasOn = true;
                }
                else if (_scopeWasOn)
                {
                    _scopeLens.Visible = false; _scopeVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled; _scopeWasOn = false;
                }
                if (_ladder != null)
                {
                    _ladder.Active = false;   // range ladder REMOVED from all scopes (master). was: _scopeHasLadder && _aimAlpha > 0.6f
                    if (_ladder.Active && _cam != null && Godot.GodotObject.IsInstanceValid(_scopeLens))
                        _ladder.Center = VpToScreen(_cam.UnprojectPosition(_scopeLens.GlobalPosition));   // follow the lens's screen position so the ladder sways WITH the glass + crosshair
                        // ...and its ROLL, or the ladder slides with the glass while staying upright. Measured as
                        // the scope's up-vector projected into the view plane, against the camera's own up -- not
                        // the raw node rotation, which includes pitch/yaw the 2D overlay must ignore.
                        {
                            Basis cb = _cam.GlobalBasis, sb = _scopeLens.GlobalBasis;
                            Vector3 fwd = -cb.Z;
                            Vector3 up = sb.Y - fwd * sb.Y.Dot(fwd);
                            _ladder.Roll = up.LengthSquared() > 1e-6f
                                ? Mathf.Atan2(up.Dot(cb.X), up.Dot(cb.Y)) : 0f;
                        }
                }
            }
            _arms.Tick(delta);   // manual-advance the base anim, then layer the additive Aim_Start pose on top
            // ---- source viewmodel-camera motion (PlayerAnimator): walk bob + recoil shake ----
            // blendedViewmodelSwayMultiplier eases toward the sway target (1 hip -> 0.1 aiming) at 16/s.
            _blendedSway = Mathf.Lerp(_blendedSway, Mathf.Lerp(1f, 0.1f, _aimAlpha), 16f * (float)delta);
            // stance-driven bob frequency (SPEED_*) + amplitude (BOB_*), scaled by the sway multiplier.
            float bobSpeed = _stance switch { EPlayerStance.SPRINT => 10f, EPlayerStance.CROUCH => 6f, EPlayerStance.PRONE => 4f, EPlayerStance.SWIM => 6f, _ => 8f };   // SWIM = viewmodel SPEED_SWIM (PlayerAnimator.cs:34)
            float bobAmp = (_stance switch { EPlayerStance.SPRINT => 0.075f, EPlayerStance.CROUCH => 0.025f, EPlayerStance.PRONE => 0.0125f, EPlayerStance.SWIM => 0.025f, _ => 0.05f }) * _blendedSway;   // SWIM = BOB_SWIM (PlayerAnimator.cs:22)
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

            // ---- INPUT INERTIA. Apply the frame's accumulated look impulse, then let the spring pull back to rest.
            // Source scales x/y by the sway multiplier and leaves z unscaled (see AddLookDelta) and clamps the whole
            // vector to +-10 degrees, which is what stops a fast flick from whipping the gun off screen.
            if (_inputRollImpulse != Vector3.Zero)
            {
                _inputRoll.CurrentPosition += new Vector3(
                    _inputRollImpulse.X * _blendedSway,
                    _inputRollImpulse.Y * _blendedSway,
                    _inputRollImpulse.Z);
                _inputRoll.CurrentPosition = _inputRoll.CurrentPosition.Clamp(Vector3.One * -10f, Vector3.One * 10f);
                _inputRollImpulse = Vector3.Zero;
            }
            _inputRoll.TargetPosition = Vector3.Zero;
            _inputRoll.Update((float)delta);

            // ---- SCOPE SWAY. Only while actually aiming through a magnifying optic: the source gates on
            // `isAiming && sightAsset != null` and its amplitude is (1 - 1/zoom), which is exactly 0 at 1x. So iron
            // sights and red dots get none of this for free, from the formula rather than from a special case.
            float scopeZoom = ScopeZoom;
            if (_aiming && scopeZoom > 1f)
            {
                float sway = (1f - 1f / scopeZoom) * 1.25f * ScopeSwayScale;   // per-gun: a steadier platform holds its optic better
                sway *= _stance switch { EPlayerStance.CROUCH => 0.85f, EPlayerStance.PRONE => 0.7f, _ => 1f };
                _swayTime += (float)delta * (1f - Mathf.Clamp(SteadyAccuracy, 0f, 1f) / 4f);
                var target = new Vector3(Mathf.Sin(0.75f * _swayTime) * sway, Mathf.Sin(1.0f * _swayTime) * sway, 0f);
                _scopeSway = _scopeSway.Lerp(target, Mathf.Clamp((float)delta * 4f, 0f, 1f));
            }
            else _scopeSway = _scopeSway.Lerp(Vector3.Zero, Mathf.Clamp((float)delta * 4f, 0f, 1f));
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
            _arms.Position = Driving ? DrivingArmsPos() : hipPos + vmOffset + TuneOffset;   // driving: skull on the camera; else + the live uniform tune offset (ESC sliders)
            // ARMS ROTATION = input inertia + scope sway. Applied to the arms ROOT rather than to the gun model,
            // because the source rotates the viewmodel CAMERA and our arms hang rigidly off that camera -- rotating
            // the gun alone would swing the barrel out of the hands. Recoil rotation stays on the gun (it is a
            // muzzle-climb of the weapon, not of the view) so the two do not fight.
            //
            // Scope sway lands on pitch/yaw only; its Z is always 0 in the source, so there is nothing to roll.
            // Scope sway is NOT added here any more. It rotates the CAMERA now (PlayerController folds
            // ScopeSwayDegrees into the aim), because rotating the arms moves the optic around the frame --
            // strawberry: "the scope stays centered in the frame, always ... the thing that moves when the scope
            // sways is the CAMERA". Adding it in both places would double the amplitude and put the reticle back
            // in motion, which is the behaviour being removed.
            var armRot = _inputRoll.CurrentPosition;
            // MOVEMENT SWAY TILT (PlayerAnimator.cs:1449-1458, tinyclaw). While moving, the gun tilts by the move
            // direction x a per-stance TILT, plus a slow "roll" oscillation -- the walk wiggle -- eased through a spring.
            // swayMul = the same viewmodelSwayMultiplier the bob uses (1 hip -> 0.1 aim), so ADS keeps a small residual
            // sway (misalignmentScale defaults 1.0 -- vanilla ADS does NOT zero the sway, it's a server knob; a magnifying
            // SCOPE zeroes it, handled by the ScopeZoom gate below). tilt = TILT x (1 - swayMul/2). 1:1 SOURCE QUIRK: the
            // y (strafe) term omits swayMul while the x (fwd/back) term keeps it -- reads like a slip but it's retail's,
            // so it's copied verbatim rather than "fixed". Scopes kill sway entirely (UseableOptic sets the mult to 0).
            float TILT = _stance switch { EPlayerStance.SPRINT => 5f, EPlayerStance.CROUCH => 2f, EPlayerStance.PRONE => 1f, EPlayerStance.SWIM => 10f, _ => 3f };
            float swayMul = (_aiming && ScopeZoom > 1f) ? 0f : _blendedSway;   // a magnifying optic zeroes the sway outright
            float tiltAmt = TILT * (1f - swayMul * 0.5f);
            float wiggle = Mathf.Sin(TILT * (float)_t * 0.25f) * TILT;   // the slow movement oscillation
            Vector2 swayTarget = _moving
                ? new Vector2(_moveInput.Y * tiltAmt * swayMul + wiggle * swayMul,   // x <- fwd/back, WITH swayMul
                              _moveInput.X * tiltAmt          + wiggle * swayMul)     // y <- strafe, NO swayMul on the move term (source quirk)
                : Vector2.Zero;
            _swayTilt.TargetPosition = swayTarget;
            _swayTilt.Update((float)delta);
            // AXIS per source (PlayerAnimator.cs:1466-1468, tinyclaw): spring.x -> PITCH (x), spring.y -> ROLL (z),
            // both POSITIVE, yaw HARD-ZEROED. So fwd/back PITCHES the gun and strafe ROLLS it (NOT yaws -- that was the
            // "inverted" feel master caught). The negative signs I nearly copied belong to rotationInputViewmodelRoll,
            // a SEPARATE +='d system -- don't inherit them.
            // SIGN: the source values are Unity Euler degrees (+X = nose DOWN, +Z = clockwise on screen); Godot's are the
            // opposite on both axes (+X = nose UP, +Z = counter-clockwise). Applied raw they pitched the muzzle UP toward the
            // face while walking forward and rolled the ADS picture against the strafe (master 2026-09-03).
            armRot.X -= _swayTilt.CurrentPosition.X;   // fwd/back sway -> PITCH
            armRot.Z -= _swayTilt.CurrentPosition.Y;   // strafe sway -> ROLL (source :1468, NOT yaw)
            if (Driving && _wheelKnown && _wheelAxisCam.LengthSquared() > 0.5f)
            {
                // hands turn WITH the wheel: rotate the arm pair about the wheel pivot, around the wheel's own axis, by the steer angle
                var pivot = new Transform3D(Basis.Identity, _wheelTargetCam);
                _arms.Transform = pivot * new Transform3D(new Basis(_wheelAxisCam.Normalized(), Mathf.DegToRad(_wheelSteerDeg)), Vector3.Zero) * pivot.AffineInverse() * new Transform3D(Basis.Identity, _arms.Position);
            }
            else _arms.RotationDegrees = armRot;
            // 1P lean tilt: a 2D roll of the composited viewmodel IMAGE about screen-centre -- NOT a 3D roll of the arms.
            // Stylistic only (the bullet origin already leans via the eye pivot, tinyclaw). Doing it in 2D keeps the ADS
            // sight pinned dead-centre (it sits at the roll pivot) while the gun tilts around it, and -- crucially -- it
            // never touches _sight.GlobalPosition, which the ADS slide measures: a 3D arms-roll fed the roll back into
            // that measurement and the aim drifted off-centre. Source rolls player.first by lean*20 (PlayerAnimator:1537);
            // for a screen-space viewmodel a centre-pivot image roll reads the same. Negated: Godot Control rotation is
            // CW-positive (screen Y-down), the head-lean tilts the view CCW.
            if (_vpRect != null)
            {
                _vpRect.PivotOffset = _vpRect.Size * 0.5f;
                _vpRect.Rotation = Mathf.DegToRad(-LeanRoll);
            }
            // ---- SPRINT + SAFETY pose: play the REAL Sprint_Start clip. Source UseableGun.cs:3509 plays ONE clip for
            //      BOTH (stance==SPRINT && moving) OR firemode==SAFETY. The un-shoulder (incl. the ~90deg yaw) is baked
            //      into the clip -- no hand-authored angles, arms ROOT untouched (the skeleton clip does the posing).
            //      Sprint is the LOWEST-tier pose (master): aim, fire, reload, rack, inspect, attach ALL override it,
            //      and it must ALWAYS hand the base back or the un-shouldered clip lingers.
            if (_shootHold > 0f) _shootHold -= (float)delta;   // a shot suppresses sprint for its burst (source: Sprint_Start needs !isShooting)
            bool _wantSprint = IsGunViewmodel && EquipDone && !_reloading && !_hammering && !_inspecting && !_attachView && !_aiming
                               && _shootHold <= 0f && ((_stance == EPlayerStance.SPRINT && _moving) || _safe);   // GUNS ONLY: the un-shoulder/safety pose is a gun thing. melee/consumable/deployable/fists never enter it -> they just keep their hold + bob (master: melee sprint-END animated buggily because it flipped _sprinting with null clips then hit the exit snap)
            if (_wantSprint && !_sprinting)
            {
                _sprinting = true;
                if (_sprintStartClip != null) _arms.Play(_sprintStartClip);
            }
            else if (!_wantSprint && _sprinting)
            {
                _sprinting = false;
                // reload/rack/inspect/attach already replaced the base with their own clip -> leave it. Otherwise
                // restore the ready hold: SNAP instantly when aiming OR firing (ADS is an ADDITIVE with no base clip
                // and a shot fires from the hip -- both must come off the ready pose, not the un-shoulder; leaving it
                // was the "weird ADS" + "won't un-set" bug), else play the gentle Sprint_Stop transition.
                if (!_reloading && !_hammering && !_inspecting && !_attachView)
                {
                    if (_aiming || _shootHold > 0f || _sprintStopClip == null) _arms.SnapToEnd(_holdClip);
                    else _arms.Play(_sprintStopClip);
                }
            }
            if (_cam != null) _cam.Fov = OversizeFov(TuneFov);              // live-tunable viewmodel FOV (ESC sliders); ADS doesn't change VM FOV

            // reload plays the real Gun_Reload clip (see SetReloading) — the base pose IS the reload motion, no dip.

            if (_inspecting) { _inspectTimer -= (float)delta; if (_inspectTimer <= 0f) _inspecting = false; }
            if (_hammering) { _hammerViewTimer -= (float)delta; if (_hammerViewTimer <= 0f) { _hammering = false; _hammerCapture = false; } }   // rack over -> stop following the bone, gun snaps back to barrel-lock
            if (_gun != null && (DeployableMesh != null || ToolMesh != null) && _gun.GetParent() is Node3D datt)
            {
                // Deployable: FOLLOW THE HAND BONE so the Deploy_Equip raise + Deploy_Use place anims move the carry model
                // (same as consumable/melee). Held-model localRotation = Euler(0,0,90) (source PlayerEquipment.firstModel), tunable via UG_DROLL.
                Vector3 droll = HoldRoll ?? (NaturalHold ? new Vector3(180f, 0f, 90f) : new Vector3(0f, 0f, 90f));   // gas can (NaturalHold): +180 pitch so the yellow CAP sits UP (the baked mesh + Fuel_Equip pose left it cap-down / upside-down)
                if (System.Environment.GetEnvironmentVariable("UG_DROLL") is string _dr && _dr.Split(',').Length == 3)
                { var pp = _dr.Split(','); droll = new Vector3(float.Parse(pp[0]), float.Parse(pp[1]), float.Parse(pp[2])); }
                var drollB = Basis.FromEuler(new Vector3(Mathf.DegToRad(droll.X), Mathf.DegToRad(droll.Y), Mathf.DegToRad(droll.Z)));
                float natScale = HoldScale > 0f ? HoldScale : (NaturalHold ? 1.6f : 1f);   // gas can scaled up so the two-handed Fuel_Equip carry reads BIG + in-your-face in the port's (wider) FP camera (master's ask); env UG_VMSCALE overrides for tuning
                if (System.Environment.GetEnvironmentVariable("UG_VMSCALE") is string _sc2 && float.TryParse(_sc2, out var _s2)) natScale = _s2;
                if (natScale != 1f) drollB = drollB.Scaled(new Vector3(natScale, natScale, natScale));
                // The generator is a big object centered on the hand -> it hangs mostly below the view. Lift it into
                // frame in VIEW space (the arms live in the SubViewport, whose world axes are the camera axes: +Y up,
                // -Z forward). Tunable via UG_DPOS="x,y,z".
                Vector3 dpos = HoldPos ?? (NaturalHold ? new Vector3(0f, 0.04f, -0.06f) : (ToolMesh != null ? new Vector3(0f, 0.02f, 0.04f) : new Vector3(0f, 0.30f, -0.10f)));   // gas can: nudge the two-handed carry up + toward the camera (in-your-face); Fuel_Equip poses the hook; tool sits in the hand; the big generator gets lifted into frame
                if (System.Environment.GetEnvironmentVariable("UG_DPOS") is string _dp && _dp.Split(',').Length == 3)
                { var pp = _dp.Split(','); dpos = new Vector3(float.Parse(pp[0]), float.Parse(pp[1]), float.Parse(pp[2])); }
                _gun.GlobalTransform = new Transform3D(datt.GlobalTransform.Basis * drollB, datt.GlobalPosition + dpos);
                LensDebugTick();
            }
            else if (_gun != null && ConsumableMesh != null && _gun.GetParent() is Node3D catt)
            {
                // Consumable: FOLLOW THE HAND BONE (the eat/drink anim tilts the wrist to sip -- source), instead of the
                // gun's barrel->aim pin which would freeze it upright + kill the tilt (master). + the source's held-model
                // localRotation = Euler(0,0,90) (PlayerEquipment: firstModel.localRotation), render-tunable via UG_ROLL.
                Vector3 roll = new Vector3(0f, 0f, 90f);   // source PlayerEquipment held-model localRotation = Euler(0,0,90). (my earlier -90 was a bad derivation: it flipped asymmetric items -- carrot showed green-up instead of the root, master caught it. the SOURCE value is right.)
                if (System.Environment.GetEnvironmentVariable("UG_ROLL") is string _r && _r.Split(',').Length == 3)
                { var pp = _r.Split(','); roll = new Vector3(float.Parse(pp[0]), float.Parse(pp[1]), float.Parse(pp[2])); }
                var rollB = Basis.FromEuler(new Vector3(Mathf.DegToRad(roll.X), Mathf.DegToRad(roll.Y), Mathf.DegToRad(roll.Z)));
                if (System.Environment.GetEnvironmentVariable("UG_VMSCALE") is string _sc && float.TryParse(_sc, out var _s)) rollB = rollB.Scaled(new Vector3(_s, _s, _s));   // debug: enlarge held item to inspect orientation
                _gun.GlobalTransform = new Transform3D(catt.GlobalTransform.Basis * rollB, catt.GlobalPosition);
            }
            else if (_gun != null && MeleeMesh != null && _gun.GetParent() is Node3D matt)
            {
                // MELEE: FOLLOW THE HAND BONE so the Equip / Weak / Strong swing anims actually move + rotate the weapon,
                // instead of the gun's barrel->aim camera-lock that pinned it facing forward (master). Held-model
                // localRotation = Euler(0,0,90) like a consumable (source PlayerEquipment.firstModel), tunable via UG_MROLL.
                Vector3 mroll = new Vector3(0f, 0f, 90f);
                if (System.Environment.GetEnvironmentVariable("UG_MROLL") is string _mr && _mr.Split(',').Length == 3)
                { var pp = _mr.Split(','); mroll = new Vector3(float.Parse(pp[0]), float.Parse(pp[1]), float.Parse(pp[2])); }
                var mrollB = Basis.FromEuler(new Vector3(Mathf.DegToRad(mroll.X), Mathf.DegToRad(mroll.Y), Mathf.DegToRad(mroll.Z)));
                _gun.GlobalTransform = new Transform3D(matt.GlobalTransform.Basis * mrollB, matt.GlobalPosition);
            }
            else if (_gun != null && _gun.GetParent() is Node3D att)
            {
                // PROPER (source-accurate): the gun rides the ANIMATED hand-bone HOLD pose (per-gun <Gun>_Equip / _Inspect /
                // _Reload clips pose the Right_Hook bone) x the source held-model localRotation Euler(0,0,90)
                // (PlayerEquipment.firstModel), exactly like the melee/consumable path. So each gun sits + animates however
                // ITS OWN anim holds it -- pistols get their grip, rifles their stance, and inspect / reload / rack move it
                // FOR FREE (the gun follows the bone, so no bone-delta compensation, no barrel-forward shortcut, no magic
                // per-gun pitch). Only the per-shot recoil spring is layered on top, in camera space.
                Basis basis = att.GlobalTransform.Basis * Basis.FromEuler(new Vector3(0f, 0f, Mathf.DegToRad(90f)));
                // gun-model muzzle-climb ANIM, faded OUT under ADS (master: kill the recoil ANIM while aiming, keep it
                // at the hip). Fade (not a branch) so bringing the sights up mid-shot settles the tilt out instead of
                // snapping it while the spring still rings. The aim-PUNCH recoil (the mouse-upwards push) is in
                // PlayerController (_recoilPending) and is deliberately untouched.
                Vector3 rr = _recoilRotSpring.CurrentPosition * (1f - _aimAlpha);
                Basis cb = _cam.GlobalTransform.Basis;
                basis = basis.Rotated(cb.X, Mathf.DegToRad(rr.X))     // pitch -> muzzle climb
                             .Rotated(cb.Y, Mathf.DegToRad(rr.Y))     // yaw
                             .Rotated(-cb.Z, Mathf.DegToRad(rr.Z));   // roll about the view axis
                if (_aimAlpha > 0.001f && float.TryParse(System.Environment.GetEnvironmentVariable("UG_ADSPITCH"), out var _adsp))
                    basis = basis.Rotated(cb.X, Mathf.DegToRad(_adsp) * _aimAlpha);   // tuning: extra ADS muzzle pitch to level a drooping iron-sight pistol barrel
                _gun.GlobalTransform = new Transform3D(basis, att.GlobalPosition);
            }

            // integrate ejected casings: gravity + tumble in the viewport world, despawn after ~1.3s
            for (int i = _casings.Count - 1; i >= 0; i--)
            {
                var c = _casings[i];
                c.Life += (float)delta;
                c.Vel += Vector3.Down * 9.8f * (float)delta;
                c.Node.GlobalPosition += c.Vel * (float)delta;
                if (!c.Bounced && c.Life > 0.5f)   // the brass lands half a second after ejection (0.35 -> 0.5, strawberry 2026-09-03 "delay the bullet brass sound by a little more"); was a third of a second: retail bulletcasingbounce/<surface> (2D -- the local player's own casing)
                {
                    c.Bounced = true;
                    GameAudio.Play2D(this, GameAudio.Pick("casings", CasingSurface), -12f, _rng.RandfRange(0.92f, 1.08f));
                }
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
            if (System.IO.File.Exists(p)) { var img = ContentProvider.LoadImage(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }

        static AudioStream LoadOgg(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            return System.IO.File.Exists(p) ? AudioStreamOggVorbis.LoadFromFile(p) : null;
        }

        // Load a .wav as a NATIVELY-LOOPING AudioStreamWav (LoopMode.Forward) so a held loop (blowtorch) has NO gap
        // between repeats (the Finished->replay trick left an audible seam). Minimal RIFF parse: fmt + data chunks.
        public static AudioStream LoadWavLooped(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (!System.IO.File.Exists(p)) return null;
            var b = System.IO.File.ReadAllBytes(p);
            int channels = 2, rate = 48000, bits = 16, dataOff = -1, dataLen = 0;
            for (int i = 12; i + 8 <= b.Length;)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, i, 4);
                int sz = System.BitConverter.ToInt32(b, i + 4);
                if (id == "fmt ") { channels = System.BitConverter.ToInt16(b, i + 10); rate = System.BitConverter.ToInt32(b, i + 12); bits = System.BitConverter.ToInt16(b, i + 22); }
                else if (id == "data") { dataOff = i + 8; dataLen = sz; break; }
                i += 8 + sz + (sz & 1);
            }
            if (dataOff < 0 || dataOff + dataLen > b.Length) return null;
            int bpf = (bits / 8) * channels;
            int frames = dataLen / bpf;
            // TRIM leading/trailing SILENCE, then loop the sounding portion -> no gap. The ripped clip carries ~59ms of
            // lead + ~31ms of trailing silence; looping the whole thing played that silence every cycle = the audible seam.
            int lead = 0, trail = frames - 1;
            if (bits == 16)
            {
                const int THR = 400;
                System.Func<int, int> amp = fr => { int m = 0; for (int c = 0; c < channels; c++) { int s = System.BitConverter.ToInt16(b, dataOff + (fr * channels + c) * 2); int a = System.Math.Abs(s); if (a > m) m = a; } return m; };
                while (lead < trail && amp(lead) < THR) lead++;
                while (trail > lead && amp(trail) < THR) trail--;
            }
            int trimFrames = trail - lead + 1;
            var pcm = new byte[trimFrames * bpf];
            System.Array.Copy(b, dataOff + lead * bpf, pcm, 0, trimFrames * bpf);
            return new AudioStreamWav
            {
                Data = pcm,
                Format = bits == 16 ? AudioStreamWav.FormatEnum.Format16Bits : AudioStreamWav.FormatEnum.Format8Bits,
                MixRate = rate, Stereo = channels == 2,
                LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = trimFrames,
            };
        }

        // Blowtorch spark origin = the NOZZLE head. The prefab "Hit" node converted wrong (landed outside the mesh);
        // the real nozzle is the top of the blowtorch mesh (AABB Y max ~0.406, top-Y vertex cluster X~-0.021 Z~-0.017).
        static Vector3 TorchNozzlePos()
        {
            var p = new Vector3(-0.039f, 0.406f, -0.026f);   // the mesh's very tip = the nozzle opening
            if (System.Environment.GetEnvironmentVariable("UG_TORCHPOS") is string s && s.Split(',').Length == 3)
            { var q = s.Split(','); p = new Vector3(float.Parse(q[0]), float.Parse(q[1]), float.Parse(q[2])); }
            return p;
        }
    }
}
