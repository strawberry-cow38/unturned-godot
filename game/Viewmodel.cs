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
        // Source-accurate: horizontal offset is ZERO (PlayerAnimator.cs:1653 base = Vector3.zero,
        // PreferenceData Offset_Horizontal defaults 0). The gun reads right-handed because the RIG holds
        // it in the right hand (lefties get localScale.x=-1, PlayerAnimator:1613 — a mirror, not a shift).
        // Y is the eye-alignment + the source -0.45 vertical drop (PlayerAnimator:1431, gun sits low).
        Vector3 _armsPos = new Vector3(0f, -1.75f, 0.12f);
        // NOTE: guns are oriented by riding the animated hand-bone HOLD pose (see the gun branch in _Process), which is
        // source-accurate -- each gun's own <Gun>_Equip anim poses a pistol vs a rifle. An earlier per-gun "hold pitch"
        // hack (magic +12deg on pistols) was removed once the bone-hold path handled it for real.
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
        public bool EmptyHands;   // holding-something-with-no-arm-model (e.g. a deployable) -> arms in a static rest hold, no weapon mesh
        public bool Fists;        // UNARMED combat state -> bare arms in the melee ready hold + weak/strong punch swings, no mesh (src: empty hands = hardcoded fists)
        public string ConsumableMesh, ConsumableAlbedo;   // set (instead of GunName) to HOLD a consumable (food/drink/medical): mesh + albedo, Equip hold + Use eat/drink anim, no gun FX
        public string ConsumableEquipClip, ConsumableUseClip;   // this item's OWN archetype clips (CE_n/CU_n from consumable_anims), e.g. drink vs eat vs syringe; empty -> generic fallback
        public Color? ConsumableColor;   // flat _Color for a no-texture consumable (cheese=yellow, potato=brown) -> used instead of the gray default
        public string DeployableMesh, DeployableAlbedo;   // set (instead of GunName) to HOLD a deployable (generator/spotlight): item.prefab carry mesh + palette, Deploy_Equip hold + Deploy_Use place anim, no gun FX
        public string ToolMesh; public Color? ToolColor;   // set (instead of GunName) to HOLD a tool in-hand (the Wire wiring tool): static mesh + generic ready hold, flat colour, no gun/deploy FX
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
            public readonly Vector3 AimHook, MuzzleHook;
            public readonly Color Tint;
            public readonly bool Ejects;
            public GunVisualInfo(string gun, string sight, string mag, string albedo, string shoot, string reload,
                                 string hammer, Vector3 aim, Vector3 muzzle, Color tint, bool ejects)
            {
                Gun = gun; Sight = sight; Mag = mag; Albedo = albedo;
                Shoot = shoot; Reload = reload; Hammer = hammer;
                AimHook = aim; MuzzleHook = muzzle; Tint = tint; Ejects = ejects;
            }
        }
        public static GunVisualInfo VisualForTest(string name)
        {
            var g = Visual(name);
            return new GunVisualInfo(g.Gun, g.Sight, g.Mag, g.Albedo, g.Shoot, g.Reload, g.Hammer,
                                     g.AimHook, g.MuzzleHook, g.AlbedoTint, g.Ejects);
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

            // Scope range-ladder overlay (2D, screen-centered like retail's distance markers). Toggled ADS'd with a numbered-ladder scope.
            _ladderLayer = new CanvasLayer { Layer = 60 };
            _ladder = new ScopeLadder2D();
            _ladderLayer.AddChild(_ladder);
            AddChild(_ladderLayer);

            _arms = RiggedCharacter.Build("res://content/rig.json", new Color(0.82f, 0.66f, 0.52f), armsOnly: true);
            if (_arms != null)
            {
                _cam.AddChild(_arms);
                _arms.Position = _armsPos;
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
                // ADS aim POSE: re-bake the additive from THIS gun's own aim clip ({Gun}_Aim, ripped from its "Aim_Start"),
                // else the generic rifle-tuned Gun_Aim. Source: UseableGun aims by playing the equipped gun's own Aim_Start,
                // so a pistol levels FLAT; the single generic delta pitched every pistol UP in ADS. Re-bake each equip so a
                // gun-switch never inherits the previous weapon's aim delta.
                _arms.SetupAimAdditive(_arms.ClipLength(capGun + "_Aim") > 0f ? capGun + "_Aim" : "Gun_Aim");
                _arms.SetClipLoop("Melee_Equip", false); _arms.SetClipLoop("Melee_Weak", false); _arms.SetClipLoop("Melee_Strong", false);   // generic (knife) melee fallback clips play once
                _arms.SetClipLoop("Punch_Left", false); _arms.SetClipLoop("Punch_Right", false);   // bare-fists jabs play once (ported from Punch.fbx)
                if (_meleeCap != null)   // this melee's OWN ripped clips ALL play once and hold (source animator.play plays non-looping); a Repeated tool's continuous "blowtorching" is the spark EMISSION while held, NOT a looping Start_Swing
                    foreach (var c in new[] { "_Equip", "_Weak", "_Strong", "_Start_Swing", "_Stop_Swing", "_Inspect" }) _arms.SetClipLoop(_meleeCap + c, false);
                string equipClip = (EmptyHands || Fists) ? "Melee_Equip"   // unarmed / carry: the generic melee READY hold (one-shot, no loop) -- NOT the 3P Idle_Hands_0 that was looping ("grab off back")
                                 : ToolMesh != null ? "Melee_Equip"   // held tool (wire): the generic one-hand ready hold
                                 : DeployableMesh != null ? (NaturalHold ? (_arms.ClipLength("Fuel_Equip") > 0f ? "Fuel_Equip" : "Deploy_Equip") : (_arms.ClipLength("Deploy_Equip") > 0f ? "Deploy_Equip" : "Melee_Equip"))   // deployable: the src barricade "Equip" raise-to-hold; NaturalHold (gas can) = its OWN TWO-HANDED Fuel_Equip carry (both hands on the can, source animations.prefab)
                                 : ConsumableMesh != null ? (_arms.ClipLength(ConsumableEquipClip) > 0f ? ConsumableEquipClip : _arms.ClipLength("Consume_Equip") > 0f ? "Consume_Equip" : "Melee_Equip")   // consumable: this item's OWN raise-to-hold archetype (CE_n), else generic Consume_Equip, else the melee raise
                                 : MeleeMesh != null ? (_arms.ClipLength(_meleeCap + "_Equip") > 0f ? _meleeCap + "_Equip" : "Melee_Equip") : (_arms.ClipLength(capGun + "_Equip") > 0f ? capGun + "_Equip" : "Gun_Equip");   // melee: its OWN raise anim (fallback generic knife); gun: its OWN per-weapon hold (pistol grip / rifle stance / etc.)
                _arms.SetClipLoop(equipClip, false);   // equip/ready-hold ALWAYS plays once and holds (src: one-shot wrapMode) -- the looping empty-hand pose was the bug
                _arms.Play(equipClip);
                _equipLen = _arms.ClipLength(equipClip);
                GD.Print($"[vm] equip (pull-out) length = {_equipLen:F3}s — aiming gated until then");

                var skel = _arms.Skeleton;
                int hb = skel.FindBone("Right_Hook");
                if (hb < 0) hb = skel.FindBone("Right_Hand");
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
                    if (gv.Albedo != null) { string _ap = ProjectSettings.GlobalizePath($"res://content/{gv.Albedo}"); if (System.IO.File.Exists(_ap)) albedoImg = Image.LoadFromFile(_ap); }
                    System.Collections.Generic.List<(Color color, ArrayMesh mesh)> sightDots = null;
                    ArrayMesh bodyMesh;
                    if (isGunBody) { var _sp = ContentProvider.ParseObjSplitByAlbedoMarker($"res://content/{gv.Gun}", albedoImg); bodyMesh = _sp.body; sightDots = _sp.markers; }
                    else bodyMesh = ContentProvider.ParseObj($"res://content/{gv.Gun}");
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
                        _scopeVp = new SubViewport { Size = new Vector2I(720, 720), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, OwnWorld3D = false };   // NOT OwnWorld3D: that DUPLICATES the world (copies the sky env but a FRESH EMPTY scenario = no geometry -> lens shows only sky). Leave it false + bind World3D to the REAL main world below so the optic renders actual geometry. (This is a SEPARATE viewport from the arms _vp -- that one stays OwnWorld3D-isolated.)
                        AddChild(_scopeVp);
                        _scopeCam = new Camera3D { Current = true, Fov = 22.5f };   // retail scope fov = 90/Zoom; the aug scope is 4x (items_catalog: "Rail mounted 4x zoom scope") -> 90/4 = 22.5deg, not the 3.5x/25.7 I'd guessed
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
                            _scopeEnv.GlowEnabled = false;   // glow is the display viewport's post-pass; the scope render doesn't need it (and _vp would bloom the lens)
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
            _flash = 0.05f; EjectCasing(); PlayShoot();   // overlapping voice per shot -- full-auto rings out fully
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
            px = _cam.UnprojectPosition(_muzzleFlash.GlobalPosition);
            return true;
        }

        // Driven each physics frame by PlayerController: whether the player is moving + their stance, so the
        // walk bob uses the right frequency (SPEED_*) + amplitude (BOB_*) and switches off when standing still.
        public void SetLocomotion(bool moving, EPlayerStance stance) { _moving = moving; _stance = stance; }

        public void PlayDryFire() { _drySnd?.Play(); }   // hammer click when the trigger's pulled on empty

        void PlayShoot()   // one OVERLAPPING polyphonic voice per shot so full-auto shots don't restart-cut each other (master)
        {
            if (_shootStream == null) return;
            _shootPoly ??= _shootSnd?.GetStreamPlayback() as AudioStreamPlaybackPolyphonic;   // (re)fetch lazily in case Play() armed it a frame late
            _shootPoly?.PlayStream(_shootStream);
        }

        public void SwingMelee(bool strong = false)   // play this melee's OWN Weak/Strong swing (source UseableMelee), falling back to the generic knife clip if it wasn't ripped
        {
            if (Fists) { _arms?.Play(strong ? "Punch_Right" : "Punch_Left"); return; }   // bare fists: the real src jab (LMB=left / RMB=right, ported from Punch.fbx)
            string own = _meleeCap + (strong ? "_Strong" : "_Weak");
            _arms?.Play(_meleeCap != null && _arms.ClipLength(own) > 0f ? own : (strong ? "Melee_Strong" : "Melee_Weak"));
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
        AudioStreamPlayer _torchSnd;   // the blowtorch "Use" loop (ripped use.wav, NATIVELY looped -> gapless) -- plays while the torch runs
        public void StartTorch()
        {
            if (!HasStartSwing) return;
            _arms.Play(_meleeCap + "_Start_Swing");
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
                var quad = new QuadMesh { Size = new Vector2(0.06f, 0.06f), Material = mat };   // spark size baked into the mesh (CpuParticles ScaleAmount doesn't scale the mesh here); ~source startSize 0.05-0.10
                // "Hit" node local pos in item.prefab = (-0.1359, 0.4719, 0) -> port frame (x,y,z)->(-x,y,-z) = (0.1359, 0.4719, 0) (the nozzle tip)
                // Source ParticleSystem params (startSize 0.05-0.10, startSpeed 1-2, sphere r=0.25, gravity x1, lifetime 1s)
                // are WORLD-scale; the viewmodel renders the torch at native model scale (the gun ~0.5 units in view), so the
                // raw values fill the screen. Scaled ~0.2x here so it reads as the game's small blue nozzle spark spray.
                _torchSparks = new CpuParticles3D
                {
                    Emitting = false, Amount = 16, Lifetime = 0.6f, Mesh = quad,
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
        public void SetAiming(bool on) { if (on && (!EquipDone || _attachView || _reloading || _hammering)) return; if (on && _inspecting) CancelInspect(); _aiming = on; }   // no ADS while the attach menu is up, or during ANY active reload / rack / bolt-cycle (source canStartAim: !isReloading && !isHammering) (master); ADS mid-inspect cancels the inspect then aims
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
            if (slot == "Sight") HideScopePiP();   // deactivate any prior scope's PiP before the swap (change or detach)
            if (string.IsNullOrEmpty(txtName)) { m.Visible = false; return; }
            m.Mesh = ContentProvider.ParseObj($"res://content/{txtName}");
            if (slot == "Sight")   // scopes/optics: each scope's REAL body colour from source (7x gray, most near-black); satin metal
            {
                bool _isSc = ScopeCal.TryGetValue(txtName, out var _sc);
                Color _bodyCol = _isSc ? _sc.Col : new Color(0.06f, 0.065f, 0.075f);   // ported red-dots (no ScopeCal entry) keep the dark default
                m.MaterialOverride = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = _bodyCol, Metallic = 0.35f, MetallicSpecular = 0.5f, Roughness = 0.5f };
                if (_isSc)
                {
                    // Real ripped reticle (source): each scope's Reticule submesh texture, saved as <base>_reticle.png. The
                    // white +/^ (cross/chevron) are tinted RED at runtime (source: criticalHitmarkerColor); the black crosshairs
                    // + baked-red X use white tint. Scale = the reticle's fraction of the glass (measured): dots ~0.056, rest 1.0.
                    string _retName = txtName.Replace("_sight.txt", "_reticle.png");
                    Texture2D _retTex = null;
                    string _rp = ProjectSettings.GlobalizePath($"res://content/{_retName}");
                    if (System.IO.File.Exists(_rp)) { var _ri = Image.LoadFromFile(_rp); if (_ri != null) _retTex = ImageTexture.CreateFromImage(_ri); }
                    bool _dot = txtName.Contains("cross") || txtName.Contains("chevron");
                    ConfigureScopePiP(_sc.Lens, _sc.Obj, _sc.Aim, _sc.Fov, _sc.Size, _sc.Sides, _retTex, _dot ? 0.056f : 1.0f, _dot ? new Color(1f, 0f, 0f) : new Color(1f, 1f, 1f));   // real PiP zoom + ADS aim + ripped reticle
                    _scopeHasLadder = txtName.StartsWith("scope_");   // the tube zoom scopes (8x/7x/16x) carry the numbered 100/200/300m range ladder
                }
            }
            m.Visible = true;
        }

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
            _scopeVp = new SubViewport { Size = new Vector2I(720, 720), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, OwnWorld3D = false };   // OwnWorld3D=false -> renders the parent (main) world; built at _Ready so the render target initialises
            AddChild(_scopeVp);
            _scopeCam = new Camera3D { Current = true, Fov = 20f };
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
                if (reticleTex != null) _sm.SetShaderParameter("reticle_tex", reticleTex);
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
            screen = _cam.UnprojectPosition(world);
            return true;
        }

        // Length (s) of the equipped gun's reload clip, so PlayerController times the ammo refill to the real anim
        // (rifles 1.633s, the masterkey's break-action 2.467s). Falls back to the eaglefire length.
        public float ReloadLength => _arms != null && _arms.ClipLength(_reloadClip) > 0f ? _arms.ClipLength(_reloadClip) : 1.633f;

        public float AimAlpha => _aimAlpha;   // 0 hip .. 1 ADS, for spread/accuracy
        public float ScopeZoom => _isScope && _scopeCam != null && Godot.GodotObject.IsInstanceValid(_scopeCam) ? 90f / _scopeCam.Fov : 0f;   // mounted scope's zoom (90/fov): aug=4, 8x=8, 16x=16... -> drives ADS-sens reduction

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
                    // (b) ZEROING (master + tinyclaw): the scope cam stays at the objective (parallax) but AIMS at the point on
                    // the BULLET ray (main cam fires from _main along -Z) at ScopeZeroDist, so the reticle = point of impact at
                    // that range and drifts only slightly past it -- a real zeroed optic. (Merely pointing it PARALLEL to the aim
                    // would leave a CONSTANT eye->objective offset that never converges.)
                    var _objPose = _main.GlobalTransform * (_cam.GlobalTransform.AffineInverse() * _scopeCamAnchor.GlobalTransform);
                    var _zeroPt = _main.GlobalPosition + (-_main.GlobalTransform.Basis.Z) * ScopeZeroDist;
                    _scopeCam.GlobalTransform = _objPose.LookingAt(_zeroPt, _main.GlobalTransform.Basis.Y);   // objective position, look AT the zero point; up = main cam up (level horizon)
                    _scopeVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                    _scopeLens.Visible = true; _scopeWasOn = true;
                }
                else if (_scopeWasOn)
                {
                    _scopeLens.Visible = false; _scopeVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled; _scopeWasOn = false;
                }
                if (_ladder != null)
                {
                    _ladder.Active = _scopeHasLadder && _aimAlpha > 0.6f;   // range ladder only while actually ADS'd through a numbered-ladder scope
                    if (_ladder.Active && _cam != null && Godot.GodotObject.IsInstanceValid(_scopeLens))
                        _ladder.Center = _cam.UnprojectPosition(_scopeLens.GlobalPosition);   // follow the lens's screen position so the ladder sways WITH the glass + crosshair
                }
            }
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
            if (_hammering) { _hammerViewTimer -= (float)delta; if (_hammerViewTimer <= 0f) { _hammering = false; _hammerCapture = false; } }   // rack over -> stop following the bone, gun snaps back to barrel-lock
            if (_gun != null && (DeployableMesh != null || ToolMesh != null) && _gun.GetParent() is Node3D datt)
            {
                // Deployable: FOLLOW THE HAND BONE so the Deploy_Equip raise + Deploy_Use place anims move the carry model
                // (same as consumable/melee). Held-model localRotation = Euler(0,0,90) (source PlayerEquipment.firstModel), tunable via UG_DROLL.
                Vector3 droll = NaturalHold ? new Vector3(180f, 0f, 90f) : new Vector3(0f, 0f, 90f);   // gas can (NaturalHold): +180 pitch so the yellow CAP sits UP (the baked mesh + Fuel_Equip pose left it cap-down / upside-down)
                if (System.Environment.GetEnvironmentVariable("UG_DROLL") is string _dr && _dr.Split(',').Length == 3)
                { var pp = _dr.Split(','); droll = new Vector3(float.Parse(pp[0]), float.Parse(pp[1]), float.Parse(pp[2])); }
                var drollB = Basis.FromEuler(new Vector3(Mathf.DegToRad(droll.X), Mathf.DegToRad(droll.Y), Mathf.DegToRad(droll.Z)));
                float natScale = NaturalHold ? 1.6f : 1f;   // gas can scaled up so the two-handed Fuel_Equip carry reads BIG + in-your-face in the port's (wider) FP camera (master's ask); env UG_VMSCALE overrides for tuning
                if (System.Environment.GetEnvironmentVariable("UG_VMSCALE") is string _sc2 && float.TryParse(_sc2, out var _s2)) natScale = _s2;
                if (natScale != 1f) drollB = drollB.Scaled(new Vector3(natScale, natScale, natScale));
                // The generator is a big object centered on the hand -> it hangs mostly below the view. Lift it into
                // frame in VIEW space (the arms live in the SubViewport, whose world axes are the camera axes: +Y up,
                // -Z forward). Tunable via UG_DPOS="x,y,z".
                Vector3 dpos = NaturalHold ? new Vector3(0f, 0.04f, -0.06f) : (ToolMesh != null ? new Vector3(0f, 0.02f, 0.04f) : new Vector3(0f, 0.30f, -0.10f));   // gas can: nudge the two-handed carry up + toward the camera (in-your-face); Fuel_Equip poses the hook; tool sits in the hand; the big generator gets lifted into frame
                if (System.Environment.GetEnvironmentVariable("UG_DPOS") is string _dp && _dp.Split(',').Length == 3)
                { var pp = _dp.Split(','); dpos = new Vector3(float.Parse(pp[0]), float.Parse(pp[1]), float.Parse(pp[2])); }
                _gun.GlobalTransform = new Transform3D(datt.GlobalTransform.Basis * drollB, datt.GlobalPosition + dpos);
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

        // Load a .wav as a NATIVELY-LOOPING AudioStreamWav (LoopMode.Forward) so a held loop (blowtorch) has NO gap
        // between repeats (the Finished->replay trick left an audible seam). Minimal RIFF parse: fmt + data chunks.
        static AudioStream LoadWavLooped(string res)
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
