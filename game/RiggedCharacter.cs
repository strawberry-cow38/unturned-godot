using Godot;
using System;
using System.Text.Json;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A real skeletal-animated Unturned character, built from content/rig.json:
    //   Skeleton3D (17 bones) + a hand-built skinned ArrayMesh + Skin (bind poses)
    //   + AnimationPlayer fed from Unturned's own legacy clips (Move_Walk/Idle_Stand/Move_Run).
    // The mesh is built from raw arrays (NOT an .obj import) so per-vertex skin indices
    // stay aligned to the bind-pose bone order.
    public partial class RiggedCharacter : Node3D
    {
        AnimationPlayer _ap;
        StandardMaterial3D _bodyMat;   // body surface material (baked-atlas path: zombies/animals), for the FLANKER_STALK ghost toggle
        ShaderMaterial _clothesMat;    // ported StandardClothes shader (player body / arms path); skin + SetShirt/SetPants painting
        Color _bodyTint;               // solid-state albedo/skin, restored when un-ghosting
        public Skeleton3D Skeleton { get; private set; }
        public string[] ClipNames { get; private set; } = Array.Empty<string>();

        // --- skeletons-cut instrument (strawberry POI-fps hunt): freeze ALL skeletal AnimationPlayers so the
        //     physics-frame cost of posing 17 bones x N zombies (UsePhysicsAnimRate -> the AnimationMixer runs in the
        //     PHYSICS callback) can be read straight off F3's physics line. z.rig only times the near-no-op Tick(); the
        //     real per-zombie posing is engine-side (AnimationMixer, physics callback) and INVISIBLE to it. Active=false
        //     stops the mixer dead -> the skeleton holds its last pose and the engine skips it. F6 (ZombieAnimCut)
        //     toggles it live. Not a fix -- an instrument: freeze it, watch F3 physics ms drop (or not).
        static readonly HashSet<RiggedCharacter> _live = new();
        public static bool AnimFrozen { get; private set; }
        public static int LiveRigCount => _live.Count;
        public static void SetAnimFrozen(bool f)
        {
            AnimFrozen = f;
            foreach (var rc in _live) if (rc._ap != null) rc._ap.Active = !f;
        }
        public override void _EnterTree() { _live.Add(this); if (AnimFrozen && _ap != null) _ap.Active = false; }
        public override void _ExitTree() => _live.Remove(this);

        // FLANKER_STALK: swap the body to a faint translucent shimmer (Unturned's ZombieClothing.ghostMaterial) --
        // NOT fully gone; a keen eye can still pick out the stalker. Restores the solid tint when off. This is the
        // ATLAS body path (zombies) -- the only ghost users. The clothes-shader body (player/corpse/1P arms) is now
        // OPAQUE (so it depth-sorts correctly against the translucent ocean) and can't shimmer; it never ghosts
        // anyway (only ZombieController calls SetGhost, and zombies build the atlas path so _clothesMat is null there).
        public void SetGhost(bool ghost)
        {
            if (_clothesMat != null) return;   // opaque clothes body -> no ghost shimmer (not a ghost user)
            if (_bodyMat == null) return;
            _bodyMat.Transparency = ghost ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled;
            _bodyMat.AlbedoColor = new Color(_bodyTint.R, _bodyTint.G, _bodyTint.B, ghost ? 0.2f : 1f);
        }

        // ---- clothing (P3a): the ported StandardClothes composite paints shirt+pants textures onto the body's
        //      UV0 atlas over the skin base. No-ops on the atlas (zombie/animal) path where _clothesMat is null.
        //      An unassigned texture reads as transparent (has_* = false) -> skin shows through.
        public void SetSkinColor(Color c) { _bodyTint = c; _clothesMat?.SetShaderParameter("skin_color", c); }

        public void SetFlipShirt(bool flip) => _clothesMat?.SetShaderParameter("flip_shirt", flip);   // _FlipShirt (left-hand mirror); SP body leaves false

        public void SetShirt(Texture2D albedo, Texture2D emission = null, Texture2D metallic = null)
        {
            if (_clothesMat == null) return;
            _clothesMat.SetShaderParameter("has_shirt_albedo", albedo != null);
            if (albedo != null) _clothesMat.SetShaderParameter("shirt_albedo", albedo);
            _clothesMat.SetShaderParameter("has_shirt_emission", emission != null);
            if (emission != null) _clothesMat.SetShaderParameter("shirt_emission", emission);
            _clothesMat.SetShaderParameter("has_shirt_metallic", metallic != null);
            if (metallic != null) _clothesMat.SetShaderParameter("shirt_metallic", metallic);
        }

        public void SetPants(Texture2D albedo, Texture2D emission = null, Texture2D metallic = null)
        {
            if (_clothesMat == null) return;
            _clothesMat.SetShaderParameter("has_pants_albedo", albedo != null);
            if (albedo != null) _clothesMat.SetShaderParameter("pants_albedo", albedo);
            _clothesMat.SetShaderParameter("has_pants_emission", emission != null);
            if (emission != null) _clothesMat.SetShaderParameter("pants_emission", emission);
            _clothesMat.SetShaderParameter("has_pants_metallic", metallic != null);
            if (metallic != null) _clothesMat.SetShaderParameter("pants_metallic", metallic);
        }

        public void ClearShirt()
        {
            if (_clothesMat == null) return;
            _clothesMat.SetShaderParameter("has_shirt_albedo", false);
            _clothesMat.SetShaderParameter("has_shirt_emission", false);
            _clothesMat.SetShaderParameter("has_shirt_metallic", false);
        }

        public void ClearPants()
        {
            if (_clothesMat == null) return;
            _clothesMat.SetShaderParameter("has_pants_albedo", false);
            _clothesMat.SetShaderParameter("has_pants_emission", false);
            _clothesMat.SetShaderParameter("has_pants_metallic", false);
        }

        // ---- gear attach (P3b): hat/mask/glasses ride the Skull bone, vest/backpack ride the Spine bone -- the port of
        //      HumanClothes.apply()'s Instantiate(prefab, parent=skull|spine) + name it + destroy colliders/rigidbody
        //      (a runtime ArrayMesh has neither). Each slot is a BoneAttachment3D (tracks the bone through animation +
        //      ragdoll -- the exact pattern the Skull face-quad decal uses in BuildFrom) holding a MeshInstance3D of the
        //      ripped gear .obj + a StandardMaterial3D albedo, placed at the captured bone-local offset. Static/opaque:
        //      no skinning, it just rides the bone. Re-attach destroys-and-rebuilds the slot; Detach clears it. Left-hand
        //      scale.y mirror is skipped (SP -- source only mirrors for the 1P left-handed viewmodel).
        BoneAttachment3D _hatAtt, _maskAtt, _glassesAtt, _vestAtt, _backpackAtt;

        // ---- FACES (strawberry 2026-09-04): the 32 retail faces (core.masterbundle Items/Faces/<n>/Texture.png, 16x16,
        //      transparent bg; face 14 also has an Emission.png) ripped to content/faces/face_<n>.png. The face is the
        //      Skull-attached decal quad built in BuildFrom; SetFace swaps its texture (+ emission when that face has one).
        MeshInstance3D _faceQuad;
        public int Face { get; private set; } = -1;
        public static string FacePath(int face) => $"res://content/faces/face_{Mathf.Clamp(face, 0, 31)}.png";
        public void SetFace(int face)
        {
            face = Mathf.Clamp(face, 0, 31);
            if (_faceQuad == null || !GodotObject.IsInstanceValid(_faceQuad)) return;
            if (_faceQuad.MaterialOverride is not StandardMaterial3D m) return;
            var tex = LoadTexCached(FacePath(face));
            if (tex == null) return;
            m.AlbedoTexture = tex;
            string em = $"res://content/faces/face_{face}_emission.png";
            var etex = System.IO.File.Exists(ProjectSettings.GlobalizePath(em)) ? LoadTexCached(em) : null;
            m.EmissionEnabled = etex != null;
            m.EmissionTexture = etex;
            if (etex != null) { m.Emission = Colors.White; m.EmissionEnergyMultiplier = 1.5f; }
            Face = face;
        }

        void AttachGear(ref BoneAttachment3D slot, string boneName, Mesh mesh, Texture2D albedo, Vector3 offset, string name)
        {
            DetachGear(ref slot);                        // source Destroy(model.gameObject) before re-instantiate
            if (Skeleton == null || mesh == null) return;
            var att = new BoneAttachment3D { BoneName = boneName, Name = name + "Attach" };
            Skeleton.AddChild(att);
            var mat = new StandardMaterial3D
            {
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,   // blocky Unturned pixels
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,            // gear .obj is Z-flipped like every ripped static mesh -> double-sided (repo convention: guns/vehicles/character), never inside-out
            };
            if (albedo != null) mat.AlbedoTexture = albedo;
            var mi = new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = mat, VisibilityRangeEnd = 95f };
            att.AddChild(mi);
            mi.Position = offset;                        // captured Model_0 bone-local offset (clothing_content.tsv attach_off)
            slot = att;
        }

        void DetachGear(ref BoneAttachment3D slot)
        {
            if (slot != null && GodotObject.IsInstanceValid(slot)) slot.QueueFree();
            slot = null;
        }

        public void AttachHat(Mesh mesh, Texture2D albedo, Vector3 offset = default)      => AttachGear(ref _hatAtt, "Skull", mesh, albedo, offset, "Hat");
        public void AttachMask(Mesh mesh, Texture2D albedo, Vector3 offset = default)     => AttachGear(ref _maskAtt, "Skull", mesh, albedo, offset, "Mask");
        public void AttachGlasses(Mesh mesh, Texture2D albedo, Vector3 offset = default)  => AttachGear(ref _glassesAtt, "Skull", mesh, albedo, offset, "Glasses");
        public void AttachVest(Mesh mesh, Texture2D albedo, Vector3 offset = default)     => AttachGear(ref _vestAtt, "Spine", mesh, albedo, offset, "Vest");
        public void AttachBackpack(Mesh mesh, Texture2D albedo, Vector3 offset = default) => AttachGear(ref _backpackAtt, "Spine", mesh, albedo, offset, "Backpack");

        public void DetachHat()      => DetachGear(ref _hatAtt);
        public void DetachMask()     => DetachGear(ref _maskAtt);
        public void DetachGlasses()  => DetachGear(ref _glassesAtt);
        public void DetachVest()     => DetachGear(ref _vestAtt);
        public void DetachBackpack() => DetachGear(ref _backpackAtt);

        string _loco;
        double _oneShot;   // remaining time a one-shot (attack/startle) clip holds before locomotion resumes

        // Additive ADS layer (viewmodel arms only): Gun_Aim (Aim_Start) is an additive clip — its motion is a
        // delta relative to its own frame 0. We bake that delta per bone and apply it on top of the base hold
        // pose, scaled by AimBlend, after manually advancing the base anim (so the order is base-then-additive).
        public float AimBlend;
        System.Collections.Generic.Dictionary<int, Quaternion> _aimDR;
        System.Collections.Generic.Dictionary<int, Vector3> _aimDP;

        // 3P GUN LAYER: the equipped gun's clips play on the UPPER body (spine/skull/arms/hands) via a 2nd
        // AnimationPlayer (_gunAp), while _ap keeps driving the legs' locomotion. Each Tick: advance loco (all bones)
        // -> snapshot the lower bones -> advance the gun overlay (all bones, overwrites) -> restore the lower bones,
        // so the legs walk while the arms hold/aim/reload. Full-body player body only (armsOnly viewmodel skips it).
        AnimationPlayer _gunAp;
        AnimationLibrary _lib;                     // shared clip library, kept so _gunAp can reuse it
        bool _gunLayer;
        int[] _lowerBones;                          // Skeleton(root)+hips+legs+feet -> restored from locomotion each frame
        int[] _lowerBonesTorso;                     // the above + Spine + Skull -> also preserved while CROUCHED/PRONE, so the stance's torso posture survives the gun overlay instead of the arms' standing pose overwriting it (master: "crouch and crawl states aren't being set correctly")
        bool _stancePreserveTorso;                  // current stance lays the torso down/low (crouch/prone) -> preserve the torso from locomotion too
        int _spineBone = -1;                        // Spine index -> the crouch/prone gun-aim counter reads its stance-vs-gun pitch delta
        int[] _armRootBones;                        // the two shoulders -> re-aimed forward after the torso restore so the barrel doesn't tilt down with the pitched stance spine (master: crouch pointed 45deg down, prone into the ground)
        Quaternion[] _lbRot; Vector3[] _lbPos;      // per-frame preserved-bone snapshot (sized to the larger torso set)
        Node3D _muzzle, _flash;                     // 3P: muzzle marker (at the gun's MuzzleHook) + the flash on the held gun
        float _flashT;                              // muzzle-flash visible timer
        ShaderMaterial _flashMat;                   // the real 1P muzzleflash shader (roll uniform set per shot)
        float _flashRoll;                           // accumulated flash roll (each shot rolls the star L/R)

        public void Play(string name, float speed = 1f)
        {
            if (_ap != null && !string.IsNullOrEmpty(name) && _ap.HasAnimation(name))
            { _ap.Play(name, -1, speed); }
        }

        // ---- PERF: never leave a manually-advanced player parked on a FINISHED clip ----
        // Measured 2026-09-02 (ETW + stopwatch + the caches_cleared signal): the 1P arms cost ~0.7 ms per frame with
        // NOTHING playing. Godot's AnimationPlayer, once a non-looping clip has reached its end, re-enters the end on
        // every manual advance() (_process_playback_data: prev_pos <= end && next_pos == end) and _blend_post_process
        // then calls _clear_caches() -- so the next advance rebuilds the track cache over the WHOLE library (this rig
        // shares one library of ~640 clips / 23k tracks). A 1-clip player on the same skeleton: 0.4 us. The cure is to
        // hold the end POSE with a looping 1-key clip instead of sitting on the finished one: pixel-identical, the
        // cache stays valid, and a plain advance is cheap. Two ways a player ends up parked: a clip finishing
        // (animation_finished -> ParkOnHold) and the Snap* helpers (Play + Seek-to-end never signals -> play the hold
        // directly). Same trap, same fix for the 3P gun overlay (_gunAp) on every player body.
        string HoldOf(AnimationPlayer ap, string clip)
        {
            if (ap == null || string.IsNullOrEmpty(clip) || _lib == null) return null;
            string hold = clip + "__hold";
            if (ap.HasAnimation(hold)) return hold;
            if (!ap.HasAnimation(clip)) return null;
            var src = ap.GetAnimation(clip);
            double end = src.Length;
            var a = new Animation { Length = 0.1f, LoopMode = Animation.LoopModeEnum.Linear };
            for (int t = 0; t < src.GetTrackCount(); t++)
            {
                var type = src.TrackGetType(t);
                switch (type)
                {
                    case Animation.TrackType.Position3D: { int k = a.AddTrack(type); a.TrackSetPath(k, src.TrackGetPath(t)); a.PositionTrackInsertKey(k, 0.0, src.PositionTrackInterpolate(t, end)); break; }
                    case Animation.TrackType.Rotation3D: { int k = a.AddTrack(type); a.TrackSetPath(k, src.TrackGetPath(t)); a.RotationTrackInsertKey(k, 0.0, src.RotationTrackInterpolate(t, end)); break; }
                    case Animation.TrackType.Scale3D:    { int k = a.AddTrack(type); a.TrackSetPath(k, src.TrackGetPath(t)); a.ScaleTrackInsertKey(k, 0.0, src.ScaleTrackInterpolate(t, end)); break; }
                    case Animation.TrackType.Value:      { int k = a.AddTrack(type); a.TrackSetPath(k, src.TrackGetPath(t)); a.TrackInsertKey(k, 0.0, src.ValueTrackInterpolate(t, end)); a.ValueTrackSetUpdateMode(k, src.ValueTrackGetUpdateMode(t)); break; }
                    default: break;   // method/audio/bezier/blend-shape tracks carry nothing to hold
                }
            }
            _lib.AddAnimation(hold, a);   // shared library -> every player of this rig sees it from now on
            return hold;
        }
        void ParkOnHold(AnimationPlayer ap, StringName finished)
        {
            if (ap == null || ap.CallbackModeProcess != AnimationMixer.AnimationCallbackModeProcess.Manual) return;   // engine-driven players stop processing on finish by themselves
            string f = finished.ToString();
            if (f.EndsWith("__hold")) return;
            string h = HoldOf(ap, f);
            if (h != null) ap.Play(h);
        }
        void OnApFinished(StringName anim) => ParkOnHold(_ap, anim);
        void OnGunApFinished(StringName anim) => ParkOnHold(_gunAp, anim);
        static string BaseClip(string s) => s != null && s.EndsWith("__hold") ? s.Substring(0, s.Length - 6) : s;

        // Scale locomotion playback rate (1 = the clip's authored speed). ZombieBody matches the shamble cycle to the
        // actual travel speed with this so the feet don't skate backward (foot-slide / moonwalk) when the body moves
        // faster than the clip's natural stride. Set per-frame; cheap (no re-Play, so it never restarts the cycle).
        public void SetLocoSpeedScale(float s) { if (_ap != null) _ap.SpeedScale = Mathf.Max(0.05f, s); }

        // Snap straight to a clip's END pose (Seek with update:true applies it this frame). Used to return to the
        // ready hold instantly when an inspect is cancelled -- without replaying the equip pull-out from frame 0.
        public void SnapToEnd(string name)
        {
            if (_ap != null && !string.IsNullOrEmpty(name) && _ap.HasAnimation(name))
            {
                string h = HoldOf(_ap, name);   // PERF: a looping 1-key end pose instead of parking on the finished clip (see HoldOf)
                if (h != null) { _ap.Play(h); return; }
                _ap.Play(name);
                _ap.Seek(_ap.GetAnimation(name).Length, true);
            }
        }

        // Length (seconds) of a clip, or 0 if absent. Used to gate ADS on the equip animation finishing.
        public float ClipLength(string name)
            => (_ap != null && _ap.HasAnimation(name)) ? (float)_ap.GetAnimation(name).Length : 0f;

        // Force a clip's loop mode (the extractor marks non-Attack/Startle/Jump clips as looping; the Equip
        // pull-out must play ONCE and hold its end pose = the two-handed ready hold).
        public void SetClipLoop(string name, bool loop)
        {
            if (_ap != null && _ap.HasAnimation(name))
                _ap.GetAnimation(name).LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        }

        // Locomotion clip names (players use the human set; zombies swap in their Move_N/Idle_N shamble).
        public string IdleClip = "Idle_Stand", WalkClip = "Move_Walk", RunClip = "Move_Run";

        // Drive locomotion by horizontal speed (m/s): idle / walk / run. Won't interrupt a one-shot.
        public void SetLocomotion(float speed)
        {
            if (_ap == null || _oneShot > 0) return;
            string want = speed < 0.2f ? IdleClip : (speed < 4.5f ? WalkClip : RunClip);
            if (!_ap.HasAnimation(want)) return;
            if (want != _loco || _ap.CurrentAnimation != want) { _loco = want; _ap.Play(want); }
        }

        // Stance-aware locomotion for the player body (master: crouch/crawl states): CROUCH swaps in Idle_Crouch/Move_Crouch,
        // PRONE swaps in Idle_Prone/Move_Prone (the crawl), everything else uses the standing Idle/Walk/Run clips.
        public void SetLocomotion(float speed, SDG.Unturned.EPlayerStance stance)
        {
            string idle = IdleClip, walk = WalkClip, run = RunClip;
            // Crouch/prone lay the TORSO down (their clips animate Spine + Skull, not just the legs), so while a gun is
            // out the overlay must preserve the torso from this stance clip too -- otherwise the arms' standing gun pose
            // pops the spine upright and the player looks like they're standing from the waist up (master).
            _stancePreserveTorso = stance == SDG.Unturned.EPlayerStance.CROUCH || stance == SDG.Unturned.EPlayerStance.PRONE;
            if (stance == SDG.Unturned.EPlayerStance.CROUCH) { idle = "Idle_Crouch"; walk = run = "Move_Crouch"; }
            else if (stance == SDG.Unturned.EPlayerStance.PRONE) { idle = "Idle_Prone"; walk = run = "Move_Prone"; }
            else if (stance == SDG.Unturned.EPlayerStance.SWIM) { idle = "Idle_Swim"; walk = run = "Move_Swim"; }   // moving=Move_Swim / still=Idle_Swim (PlayerAnimator.cs:940/998)
            if (_ap == null || _oneShot > 0) return;
            string want = speed < 0.2f ? idle : (speed < 4.5f ? walk : run);
            if (!_ap.HasAnimation(want)) return;
            if (want != _loco || _ap.CurrentAnimation != want) { _loco = want; _ap.Play(want); }
        }

        // Play a looping clip (e.g. Idle_Drive while seated in a vehicle) and HOLD it -- no-op if it's already the current
        // clip, so it can be called every frame without restarting. Uses the same _loco slot as locomotion (master).
        public void PlayLoop(string name)
        {
            if (_ap == null || _oneShot > 0 || !_ap.HasAnimation(name)) return;
            _ap.GetAnimation(name).LoopMode = Animation.LoopModeEnum.Linear;
            if (name != _loco || _ap.CurrentAnimation != name) { _loco = name; _ap.Play(name); }
        }

        // Play a one-shot (Attack_0 / Startle_0); locomotion resumes after it finishes.
        public void PlayOnce(string name)
        {
            if (_ap == null || !_ap.HasAnimation(name)) return;
            _ap.Play(name);
            _oneShot = _ap.CurrentAnimationLength;
            _loco = null;
        }

        // 3P GUN (source: PlayerAnimator adds the equipped gun's clip to the third-person animator; the body holds the
        // gun on the SAME Right_Hook hand bone the 1P viewmodel uses). Attach the gun mesh to the hand; play the gun's
        // clip ({Gun}_Equip / Gun_Equip) to pose the arms around it. Replaces any prior attached gun.
        public void AttachGun(string gunName)
        {
            if (Skeleton == null || string.IsNullOrEmpty(gunName)) return;
            Skeleton.GetNodeOrNull("GunAttach")?.QueueFree();
            int hb = Skeleton.FindBone("Right_Hook");
            if (hb < 0) hb = Skeleton.FindBone("Right_Hand");
            if (hb < 0) return;
            var att = new BoneAttachment3D { Name = "GunAttach" };
            Skeleton.AddChild(att);
            att.BoneName = Skeleton.GetBoneName(hb);
            var info = Viewmodel.VisualForTest(gunName);
            if (info.Gun == null) return;
            var mesh = ContentProvider.ParseObj($"res://content/{info.Gun}");
            if (mesh == null) return;
            var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };   // repo gear convention (:114): ripped meshes are winding-reversed -> Disabled or they render inside-out
            if (info.Albedo != null)
            {
                string ap = ProjectSettings.GlobalizePath($"res://content/{info.Albedo}");
                if (System.IO.File.Exists(ap)) { var img = ContentProvider.LoadImage(ap); if (img != null) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }
            }
            var mi = new MeshInstance3D { Name = "GunMesh", Mesh = mesh, MaterialOverride = mat, RotationDegrees = new Vector3(0f, 0f, 90f) };   // barrel is gun-local +Y; roll about local +Z (world-vertical here) swings it to the char forward (-Z)
            att.AddChild(mi);
            // 3P muzzle marker (at the gun's own MuzzleHook) + a flash quad, so firing effects spawn off the 3P gun itself.
            _muzzle = new Node3D { Name = "Muzzle", Position = info.MuzzleHook };
            mi.AddChild(_muzzle);
            _flash = new Node3D { Name = "Flash", Visible = false };
            _flash.AddChild(new OmniLight3D { OmniRange = 4.0f, LightColor = new Color(0.941f, 0.756f, 0.152f), LightEnergy = 1.4f, ShadowEnabled = false });
            // the REAL 1P muzzle flash: the Muzzle_0 star sprite on content/muzzleflash.gdshader (rolls per shot), same as the viewmodel (master)
            _flashMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/muzzleflash.gdshader") };
            string ffp = ProjectSettings.GlobalizePath("res://content/muzzleflash.png");
            if (System.IO.File.Exists(ffp)) { var fimg = ContentProvider.LoadImage(ffp); if (fimg != null) _flashMat.SetShaderParameter("tex", ImageTexture.CreateFromImage(fimg)); }
            _flashMat.SetShaderParameter("roll", 0f);
            _flash.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(0.55f, 0.55f) }, MaterialOverride = _flashMat });
            _muzzle.AddChild(_flash);
        }

        // Remove the held gun (weapon holstered / swapped away). Safe if nothing's attached.
        public void DetachGun() { Skeleton?.GetNodeOrNull("GunAttach")?.QueueFree(); _muzzle = null; _flash = null; }

        /// <summary>A melee weapon/tool in the right hand (content/{name}.txt + {name}_albedo.png, the viewmodel's own melee
        /// files) on the same Right_Hook the gun uses. The 3P body -- yours and other players' puppets -- never showed one.</summary>
        public void AttachMelee(string meleeName)
        {
            if (Skeleton == null || string.IsNullOrEmpty(meleeName)) return;
            Skeleton.GetNodeOrNull("MeleeAttach")?.QueueFree();
            int hb = Skeleton.FindBone("Right_Hook"); if (hb < 0) hb = Skeleton.FindBone("Right_Hand"); if (hb < 0) return;
            var mesh = ContentProvider.ParseObj($"res://content/{meleeName}.txt");
            if (mesh == null) return;
            var att = new BoneAttachment3D { Name = "MeleeAttach" };
            Skeleton.AddChild(att);
            att.BoneName = Skeleton.GetBoneName(hb);
            var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
            string ap = ProjectSettings.GlobalizePath($"res://content/{meleeName}_albedo.png");
            if (System.IO.File.Exists(ap)) { var img = ContentProvider.LoadImage(ap); if (img != null) mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }
            att.AddChild(new MeshInstance3D { Name = "MeleeMesh", Mesh = mesh, MaterialOverride = mat, RotationDegrees = new Vector3(0f, 0f, 90f) });   // held-model localRotation = Euler(0,0,90), same as the viewmodel's melee
        }
        public void DetachMelee() => Skeleton?.GetNodeOrNull("MeleeAttach")?.QueueFree();

        // The attached gun mesh (for mounting 3P attachments + a muzzle marker on it). Null when unarmed.
        public MeshInstance3D HeldGunMesh => Skeleton?.GetNodeOrNull("GunAttach")?.GetNodeOrNull<MeshInstance3D>("GunMesh");

        // World position of the 3P gun's muzzle (its own MuzzleHook), for spawning the flash + tracer there. Null when unarmed.
        public Vector3? MuzzleWorld => (_muzzle != null && IsInstanceValid(_muzzle)) ? _muzzle.GlobalPosition : (Vector3?)null;

        // Fire: flash the 3P muzzle for a couple of frames (Tick hides it), rolling the star L/R per shot like the 1P.
        public void FlashMuzzle()
        {
            if (_flash == null || !IsInstanceValid(_flash)) return;
            _flash.Visible = true; _flashT = 0.05f;
            _flashRoll += (GD.Randf() < 0.5f ? -1f : 1f) * (0.35f + GD.Randf() * 0.65f);
            _flashMat?.SetShaderParameter("roll", _flashRoll);
        }

        // Mount an attachment mesh (sight/scope/magazine/barrel) as a child of the 3P gun mesh at its gun-local hook.
        // Rides the gun's Z=90 roll like the body + muzzle marker. Called by the fire wiring right after AttachGun.
        public void MountGunAttachment(string name, Mesh mesh, Vector3 pos, Color color)
        {
            var gm = HeldGunMesh;
            if (gm == null || mesh == null) return;
            var mat = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = color, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Metallic = 0f, MetallicSpecular = 0f, Roughness = 1f };
            gm.AddChild(new MeshInstance3D { Name = "A_" + name, Mesh = mesh, MaterialOverride = mat, Position = pos });
        }

        public void Tick(double delta)
        {
            if (_oneShot > 0) _oneShot -= delta;
            if (_flashT > 0f) { _flashT -= (float)delta; if (_flashT <= 0f && _flash != null && IsInstanceValid(_flash)) _flash.Visible = false; }
            if (_ap != null && _ap.CallbackModeProcess == AnimationMixer.AnimationCallbackModeProcess.Manual)
            {
                // PERF: a player that has never played anything (or was explicitly stopped) has no pose to refresh and no
                // aim blend to layer -- skip the advance. (Finished clips park on a looping hold instead, see HoldOf, so
                // in steady state the players are always "playing" and a plain advance stays cheap.)
                if (!_ap.IsPlaying() && AimBlend <= 0.0001f && !(_gunLayer && _gunAp != null && _gunAp.IsPlaying())) return;
                _ap.Advance(delta);   // base pose: locomotion (full-body 3P) or equip/hold (1P arms), manually driven
                if (_gunLayer && _gunAp != null && Skeleton != null)
                {
                    // Standing: preserve the legs (the arms hold/aim over them). Crouch/prone: preserve the torso + head
                    // too, so the stance's laid-down posture isn't overwritten by the arms' standing gun pose (master).
                    var preserve = (_stancePreserveTorso && _lowerBonesTorso != null) ? _lowerBonesTorso : _lowerBones;
                    for (int i = 0; i < preserve.Length; i++)      // keep locomotion on the preserved bones...
                    { _lbRot[i] = Skeleton.GetBonePoseRotation(preserve[i]); _lbPos[i] = Skeleton.GetBonePosePosition(preserve[i]); }
                    _gunAp.Advance(delta);                          // ...while the gun clip poses the rest (the arms)
                    // Grab the gun clip's UPRIGHT spine before the restore overwrites it -- the counter below needs it.
                    Quaternion spineUp = (_stancePreserveTorso && _spineBone >= 0) ? Skeleton.GetBonePoseRotation(_spineBone) : Quaternion.Identity;
                    for (int i = 0; i < preserve.Length; i++)
                    { Skeleton.SetBonePoseRotation(preserve[i], _lbRot[i]); Skeleton.SetBonePosePosition(preserve[i], _lbPos[i]); }
                    // Crouch/prone gun-aim counter: the torso is now pitched down (right), but the arms still carry the
                    // gun clip's pose UNDER that pitched spine, so the barrel points at the ground (master: crouch ~45deg
                    // down, prone straight into the dirt). The whole tilt is the spine's own pitch delta, so re-aim each
                    // shoulder by (S_stance^-1 * S_gun): the arm chain -- and the gun -- returns to the forward direction
                    // it holds over an upright spine, while the torso keeps its lowered stance posture. ADS layers after.
                    if (_stancePreserveTorso && _spineBone >= 0 && _armRootBones != null && _armRootBones.Length > 0)
                    {
                        Quaternion cc = Skeleton.GetBonePoseRotation(_spineBone).Inverse() * spineUp;
                        foreach (int sh in _armRootBones)
                            Skeleton.SetBonePoseRotation(sh, cc * Skeleton.GetBonePoseRotation(sh));
                    }
                }
                ApplyAimAdditive();
            }
        }

        /// <summary>Third-person LEAN, applied to the SPINE (master: "the projectile launch point doesnt follow the
        /// player eyes when leaning, stays in an upright position").
        ///
        /// The bullet was never the bug: PlayerController fires from EyesWorld, which hangs off the lean pivot and
        /// does swing out. The BODY never leaned -- _body.Rotation is yaw only -- so the character stood bolt upright
        /// while the camera tilted, and every visual sourced from the model (muzzle flash, tracer anchor, the gun)
        /// stayed upright with it. Retail leans the spine and lets the arms inherit it: HumanAnimator.cs:45,
        /// `spine.Rotate(0, _pitch * 0.5f, _lean * LEAN)`. On the spine rather than the whole body is what keeps the
        /// legs planted.
        ///
        /// A SkeletonModifier3D, and that is the whole reason this took a second pass. Applying it at the end of
        /// Tick() worked ONLY while a gun was out: EnableGunLayer puts the base AnimationPlayer in Manual and Tick
        /// advances it, so the rotation landed after the pose -- but DisableGunLayer returns it to Idle and the engine
        /// then poses the skeleton OUTSIDE Tick, wiping the lean. Verified, not suspected: the first --rig render
        /// showed no lean at all and only UG_GUNLAYER=1 made it appear. A modifier runs after the mixer whatever the
        /// callback mode is, which is the property actually needed.
        ///
        /// (The tempting smaller fix -- flip DisableGunLayer to Manual -- is wrong: the base pose would then only
        /// advance when something calls Tick, freezing any consumer that does not.)
        ///
        /// NOT retail's `_pitch * 0.5f` term. Our 3P spine does not pitch with the look angle at all; separate
        /// missing behaviour, deliberately not bundled.
        public partial class TorsoPoseModifier : SkeletonModifier3D
        {
            public float LeanDeg;
            public float PitchDeg;        // the FULL look pitch; spine and skull take half each (see below)
            public int SpineBone = -1;
            public int SkullBone = -1;
            // The resulting Spine->Skull direction in skeleton space, recorded EVERY pass (leaning or not). This is
            // the only place the modified pose is observable: Godot restores the stored bone pose once the
            // modification pass ends, so a caller doing GetBoneGlobalPose from outside reads the UNMODIFIED skeleton
            // and sees a perfectly upright spine no matter what the modifier did. The lean is real -- it renders --
            // but it lives in the pass, so anything asserting on it has to sample here. Per-instance, never static:
            // a shared slot would let one rig's reading stand in for another's and turn "never ran" into a pass.
            public Vector3 SkullDir = Vector3.Up;

            // Global (skeleton-space) orientation of each bone as it ended up. Recorded so a test can measure how far
            // a bone TURNED between two runs -- Basis-to-Basis angle is convention-free, so the half-to-the-spine /
            // half-to-the-skull split can be checked without re-deriving which local axis means "pitch" on this rig,
            // which is the derivation the assertion is supposed to be independent of.
            public Basis SpineBasis = Basis.Identity, SkullBasis = Basis.Identity;

            public override void _ProcessModification()
            {
                var sk = GetSkeleton();
                if (sk == null || SpineBone < 0) return;
                // A lean is a roll about the character's fore-aft axis, which rig.json puts along Z: Spine's rest is
                // -90 about Z off the Skeleton root, so Spine-local -X runs up the body, and the Left_Shoulder /
                // Left_Arm chain extends toward parent -X -- left = -X, up = +Y, hence forward = -Z. Retail's
                // magnitude is HumanAnimator.LEAN = 20.
                //
                // THE AXIS IS Vector3.Back (+Z), NOT Forward, and the sign is the whole point: PlayerController leans
                // the CAMERA with `_leanPivot.Rotation.Z = +_leanAngle`, a rotation about +Z that carries the eye
                // toward -X. Rolling the spine about -Z instead swings the head toward +X -- a model leaning out from
                // the opposite side of the wall to the camera peeking past it, correct by 20 degrees and backwards to
                // every other player. A rig render cannot catch that (there is no camera in the shot to disagree
                // with); only tying the two conventions together can, which is what rig.spine_lean asserts.
                //
                // The axis is re-expressed in the parent's frame each tick rather than hardcoded, because EVERY clip
                // in rig.json animates the Skeleton root bone (432 of them). At rest the two are identical -- a render
                // measured 22.9 deg of tilt for a 20 deg input -- and they diverge only once the animated root carries
                // the parent frame away from the character's, which is exactly when "roll about the character's own
                // fore-aft axis" is the definition that still means something.
                int parent = sk.GetBoneParent(SpineBone);
                Basis parentBasis = parent >= 0 ? sk.GetBoneGlobalPose(parent).Basis : Basis.Identity;
                Vector3 axis = (parentBasis.Inverse() * Vector3.Back).Normalized();
                if (!axis.IsFinite() || axis.LengthSquared() < 1e-6f) return;
                // Retail composes both onto the one bone: spine.Rotate(0, _pitch * 0.5f, _lean * LEAN). Unity's
                // Rotate applies Z then X then Y, so the lean lands first and the pitch on top of it -- hence
                // pitch * lean here and not the other way round.
                var d = Quaternion.Identity;
                if (!Mathf.IsZeroApprox(LeanDeg))
                    d = new Quaternion(axis, Mathf.DegToRad(LeanDeg));
                if (!Mathf.IsZeroApprox(PitchDeg))
                {
                    // Pitch is a rotation about the character's RIGHT axis, +X by the same derivation that put
                    // forward at -Z. Looking up (our _pitchDeg > 0, a Godot +X camera rotation) has to tilt the torso
                    // BACK: the arms hang off the spine, so the shoulders can only raise the gun toward the sky by
                    // rotating the chest up and the head back. A rotation about +X carries +Y toward +Z, which is
                    // backwards -- so the angle keeps the pitch's sign rather than inverting it.
                    var pa = (parentBasis.Inverse() * Vector3.Right).Normalized();
                    if (pa.IsFinite() && pa.LengthSquared() > 1e-6f)
                        d = new Quaternion(pa, Mathf.DegToRad(PitchDeg * SpinePitchShare)) * d;
                }
                if (!d.IsEqualApprox(Quaternion.Identity))
                    sk.SetBonePoseRotation(SpineBone, d * sk.GetBonePoseRotation(SpineBone));

                // The SKULL takes the other half -- retail's very next line, skull.Rotate(0, _pitch * 0.5f, 0). Without
                // it the head only turns half as far as you are actually looking, and since the spine's half is all
                // the arms get, a 3P character aiming at the sky would be staring at the horizon.
                //
                // Read the skull's parent basis AFTER the spine write above: the globals recompute inside the pass, so
                // this half composes onto the pitched spine instead of fighting it.
                if (SkullBone >= 0 && !Mathf.IsZeroApprox(PitchDeg))
                {
                    int sp = sk.GetBoneParent(SkullBone);
                    Basis sb = sp >= 0 ? sk.GetBoneGlobalPose(sp).Basis : Basis.Identity;
                    var sa = (sb.Inverse() * Vector3.Right).Normalized();
                    if (sa.IsFinite() && sa.LengthSquared() > 1e-6f)
                        sk.SetBonePoseRotation(SkullBone,
                            new Quaternion(sa, Mathf.DegToRad(PitchDeg * SkullPitchShare)) * sk.GetBonePoseRotation(SkullBone));
                }

                SpineBasis = sk.GetBoneGlobalPose(SpineBone).Basis.Orthonormalized();
                if (SkullBone >= 0)
                {
                    SkullBasis = sk.GetBoneGlobalPose(SkullBone).Basis.Orthonormalized();
                    SkullDir = (sk.GetBoneGlobalPose(SkullBone).Origin - sk.GetBoneGlobalPose(SpineBone).Origin).Normalized();
                }
            }
        }

        TorsoPoseModifier _leanMod;
        float _leanDeg, _pitchDeg;

        /// <summary>Retail splits the look pitch across two bones -- spine.Rotate(0, _pitch * 0.5f, ...) then
        /// skull.Rotate(0, _pitch * 0.5f, 0) -- so the head ends up covering the full angle while the shoulders (and
        /// therefore the gun) only get half of it. That asymmetry is deliberate and load-bearing, not a rounding of
        /// "the torso pitches": a 3P character aiming at the sky raises the gun halfway and looks the rest.</summary>
        internal const float SpinePitchShare = 0.5f, SkullPitchShare = 0.5f;
        /// <summary>Signed lean in degrees; + leans left, matching PlayerController's _leanAngle.
        ///
        /// Backed by a FIELD rather than forwarded straight at the modifier. The first version was
        /// `set { if (_leanMod != null) _leanMod.LeanDeg = value; }` -- which silently DROPPED every assignment made
        /// before the modifier existed, and the modifier was being created inside EnableGunLayer, so an unarmed rig
        /// swallowed the lean and rendered pixel-identical to no lean at all. A no-op setter is the same failure as a
        /// null-returning loader: it reports success by saying nothing.</summary>
        public float LeanDeg
        {
            get => _leanDeg;
            set { _leanDeg = value; if (_leanMod != null) _leanMod.LeanDeg = value; }
        }

        /// <summary>Look pitch in degrees, + looking up, matching PlayerController._pitchDeg. Backed by a field for
        /// the same reason LeanDeg is: an assignment before the modifier exists must not vanish.</summary>
        public float PitchDeg
        {
            get => _pitchDeg;
            set { _pitchDeg = value; if (_leanMod != null) _leanMod.PitchDeg = value; }
        }

        /// <summary>Attach the lean modifier to a freshly built skeleton. Called from BuildFrom for EVERY rig --
        /// player, zombie, corpse -- because the lean must not depend on whether a gun layer was ever enabled.</summary>
        /// <summary>Where the torso actually ended up: the Spine-&gt;Skull direction in skeleton space, sampled inside
        /// the modification pass (the only place the leaned pose exists -- see LeanModifier.SkullDir).</summary>
        public Vector3 LeanSkullDir => _leanMod?.SkullDir ?? Vector3.Up;

        /// <summary>Spine/skull orientation as posed, sampled inside the modification pass (see TorsoPoseModifier).</summary>
        public Basis TorsoSpineBasis => _leanMod?.SpineBasis ?? Basis.Identity;
        public Basis TorsoSkullBasis => _leanMod?.SkullBasis ?? Basis.Identity;

        void AttachLeanModifier()
        {
            if (Skeleton == null || _leanMod != null) return;
            int spine = Skeleton.FindBone("Spine");
            if (spine < 0) return;
            _leanMod = new TorsoPoseModifier
            {
                SpineBone = spine, SkullBone = Skeleton.FindBone("Skull"),
                LeanDeg = _leanDeg, PitchDeg = _pitchDeg, Name = "LeanModifier",
            };
            Skeleton.AddChild(_leanMod);
        }

        // Perf (strawberry: POI fps): pose the skeletal AnimationPlayer at the 50 Hz PHYSICS rate instead of
        // the render rate (default Idle = _process = up to 280 fps). A shambling zombie/puppet looks identical
        // at 50 Hz, but posing 17 bones per zombie at a high-refresh render rate is pure waste -- this is the
        // biggest single zombie-CPU cut. Never touches a Manual-mode rig (the viewmodel drives Advance itself).
        public void UsePhysicsAnimRate()
        {
            if (_ap != null && _ap.CallbackModeProcess != AnimationMixer.AnimationCallbackModeProcess.Manual)
                _ap.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics;
        }

        // Bake the Gun_Aim additive delta (per bone, end relative to frame 0) and switch the arms' player to
        // manual advance so we can apply that delta on top of the base pose each frame. Viewmodel arms only.
        // clip = the additive ADS aim source. The Viewmodel re-bakes this per equipped gun ({Gun}_Aim, ripped from
        // that gun's own "Aim_Start"), falling back to the generic rifle-tuned "Gun_Aim". One generic delta pitched
        // pistols UP in ADS; the gun's own aim pose levels it flat, exactly as retail plays the equipped gun's Aim_Start.
        public void SetupAimAdditive(string clip = "Gun_Aim")
        {
            if (_ap == null || Skeleton == null || !_ap.HasAnimation(clip)) return;
            var anim = _ap.GetAnimation(clip);
            double end = anim.Length;
            _aimDR = new(); _aimDP = new();
            for (int t = 0; t < anim.GetTrackCount(); t++)
            {
                string path = anim.TrackGetPath(t).ToString();
                int c = path.LastIndexOf(':'); if (c < 0) continue;
                int bi = Skeleton.FindBone(path.Substring(c + 1));
                if (bi < 0) continue;
                switch (anim.TrackGetType(t))
                {
                    case Animation.TrackType.Rotation3D:
                        _aimDR[bi] = anim.RotationTrackInterpolate(t, end) * anim.RotationTrackInterpolate(t, 0.0).Inverse();
                        break;
                    case Animation.TrackType.Position3D:
                        _aimDP[bi] = anim.PositionTrackInterpolate(t, end) - anim.PositionTrackInterpolate(t, 0.0);
                        break;
                }
            }
            _ap.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
        }

        void ApplyAimAdditive()
        {
            if (_aimDR == null || AimBlend <= 0.0001f || Skeleton == null) return;
            // PRE-multiply: the baked delta is R_end * R_frame0^-1 (a parent-frame delta), so it must LEFT-multiply the
            // base pose (delta * current) to reach R_end. Post-multiplying (current * delta) CONJUGATES the delta by the
            // base pose instead -- ~fine for a near-single-axis aim (rifle) but it corrupts a large multi-axis one
            // (a pistol's two-handed raise rotates 7 bones incl. the gun bone) -> the barrel drooped in ADS.
            foreach (var kv in _aimDR)
                Skeleton.SetBonePoseRotation(kv.Key, Quaternion.Identity.Slerp(kv.Value, AimBlend) * Skeleton.GetBonePoseRotation(kv.Key));
            foreach (var kv in _aimDP)
                Skeleton.SetBonePosePosition(kv.Key, Skeleton.GetBonePosePosition(kv.Key) + kv.Value * AimBlend);
        }

        // ---- 3P gun layer control (player body) ----
        // Turn the upper-body gun overlay on: spin up the 2nd AnimationPlayer, resolve the lower bones to preserve
        // from locomotion, and bake the ADS aim delta (aimClip = {gun}_Aim, generic "Gun_Aim" fallback). SetupAimAdditive
        // also switches _ap to Manual advance, which Tick's overlay pass relies on. Idempotent.
        public void EnableGunLayer(string aimClip = "Gun_Aim")
        {
            if (_gunLayer || _ap == null || Skeleton == null || _lib == null) return;
            _gunAp = new AnimationPlayer { Name = "GunAnim" };
            AddChild(_gunAp);
            _gunAp.AddAnimationLibrary("", _lib);
            _gunAp.AnimationFinished += OnGunApFinished;   // PERF: see HoldOf
            _gunAp.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            string[] lower = { "Skeleton", "Left_Hip", "Left_Leg", "Left_Foot", "Right_Hip", "Right_Leg", "Right_Foot" };
            var idx = new List<int>();
            foreach (var n in lower) { int b = Skeleton.FindBone(n); if (b >= 0) idx.Add(b); }
            _lowerBones = idx.ToArray();
            // The crouch/prone set: the legs PLUS the torso (Spine) and head (Skull), so a low stance keeps its whole
            // posture under the overlay; the arms stay overlay-driven (they still aim/hold, now relative to the leaned
            // torso), and the ADS additive only nudges the spine when actually aiming (AimBlend-gated).
            var idxT = new List<int>(idx);
            foreach (var n in new[] { "Spine", "Skull" }) { int b = Skeleton.FindBone(n); if (b >= 0) idxT.Add(b); }
            _lowerBonesTorso = idxT.ToArray();
            _spineBone = Skeleton.FindBone("Spine");   // the crouch/prone gun-aim counter reads this
            var arms = new List<int>();
            foreach (var n in new[] { "Left_Shoulder", "Right_Shoulder" }) { int b = Skeleton.FindBone(n); if (b >= 0) arms.Add(b); }
            _armRootBones = arms.ToArray();   // shoulders = the top of each arm chain -> re-aim these to point the gun forward under a pitched spine
            _lbRot = new Quaternion[_lowerBonesTorso.Length];   // sized to the larger set so either can reuse the buffer
            _lbPos = new Vector3[_lowerBonesTorso.Length];
            SetupAimAdditive(aimClip);   // bakes the ADS delta + switches _ap to Manual
            _gunLayer = true;
        }

        // Re-bake the ADS aim delta for a different gun (each gun ships its own {Gun}_Aim). No-op unless the layer's up.
        public void RebakeAim(string aimClip) { if (_gunLayer) SetupAimAdditive(aimClip); }

        // Set the upper-body overlay clip: the ready hold (loop=true) or a one-shot reload/equip (loop=false). No-op if
        // the clip's already current (safe every frame). speed scales a reload to the gun's real reload time.
        public void SetGunOverlay(string clip, float speed = 1f, bool loop = true)
        {
            if (_gunAp == null || string.IsNullOrEmpty(clip) || !_gunAp.HasAnimation(clip)) return;
            _gunAp.GetAnimation(clip).LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            if (_gunAp.CurrentAnimation != clip) _gunAp.Play(clip, -1, speed);
        }

        // Snap the overlay straight to a clip's END pose (the ready hold) without replaying it -- used to return from a
        // reload/equip to the hold without re-running the pull-out.
        public void SnapGunOverlay(string clip)
        {
            if (_gunAp == null || string.IsNullOrEmpty(clip) || !_gunAp.HasAnimation(clip)) return;
            _gunAp.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.None;
            string h = HoldOf(_gunAp, clip);   // PERF: see HoldOf -- a Seek-to-end player re-clears its caches every advance
            if (h != null) { _gunAp.Play(h); return; }
            _gunAp.Play(clip); _gunAp.Seek(_gunAp.GetAnimation(clip).Length, true);
        }

        // ---- MELEE on the same upper-body overlay the gun uses (strawberry 2026-09-03: "third person cam doesnt show melee
        // animation? and they dont hold the weapon in the 'ready to swing' sorta state. just glued to their hand") ----
        // The rig ships per-weapon {Cap}_Equip/_Weak/_Strong clips (Katana_*, Axe_fire_*, Knife_butcher_* ...) plus generic
        // Melee_* fallbacks and Punch_Left/Right for fists. The END of _Equip is the ready hold, exactly as Gun_Equip is for guns.
        public (string equip, string weak, string strong) MeleeClipsFor(string meleeName)
        {
            if (string.IsNullOrEmpty(meleeName) || meleeName == "fists") return ("", "Punch_Left", "Punch_Right");
            string cap = char.ToUpper(meleeName[0]) + meleeName.Substring(1);
            string Pick(string a, string b) => ClipLength(a) > 0f ? a : (ClipLength(b) > 0f ? b : "");
            return (Pick(cap + "_Equip", "Melee_Equip"), Pick(cap + "_Weak", "Melee_Weak"), Pick(cap + "_Strong", "Melee_Strong"));
        }
        /// <summary>Ready-to-swing hold for a drawn melee weapon: the upper-body layer parked on the end of its Equip clip.</summary>
        public void ShowMeleeHold(string meleeName)
        {
            var (equip, _, _) = MeleeClipsFor(meleeName);
            if (equip == "") { if (_gunLayer && BaseClip(_gunAp?.CurrentAnimation ?? "").StartsWith("Punch")) DisableGunLayer(); return; }   // fists: no hold pose
            if (!_gunLayer) EnableGunLayer("Gun_Aim");   // the additive aim bake is inert at AimBlend 0; the layer is what we want
            SnapGunOverlay(equip);
        }
        /// <summary>Play a weak/strong swing on the upper body. Returns the clip length so the caller can return to the hold.</summary>
        public float PlayMeleeSwing(string meleeName, bool strong)
        {
            var (_, weak, strongClip) = MeleeClipsFor(meleeName);
            string clip = strong ? strongClip : weak;
            if (clip == "" || ClipLength(clip) <= 0f) return 0f;
            if (!_gunLayer) EnableGunLayer("Gun_Aim");
            if (_gunAp != null && _gunAp.CurrentAnimation == clip) _gunAp.Stop();   // a second swing of the same clip must restart, not be ignored
            SetGunOverlay(clip, 1f, loop: false);
            return ClipLength(clip);
        }
        public string GunOverlayClip => BaseClip(_gunAp?.CurrentAnimation ?? "");
        /// <summary>The looping locomotion/seated clip currently held (test seam).</summary>
        public string CurrentLoopClip => _loco ?? "";
        public bool GunLayerOn => _gunLayer;

        // Tear the gun layer down (weapon holstered): stop + free the overlay player, drop the aim delta, and hand
        // _ap back to automatic advance so plain locomotion drives the whole body again.
        public void DisableGunLayer()
        {
            if (!_gunLayer) return;
            _gunLayer = false;
            _gunAp?.QueueFree(); _gunAp = null;
            _aimDR = null; _aimDP = null; AimBlend = 0f;
            if (_ap != null) _ap.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Idle;
        }

        // ---- ragdoll (built from Unturned's Ragdoll_Player prefab: 11 bodies, box colliders,
        //      per-bone mass + CharacterJoint swing/twist limits, all extracted to rig.json) ----
        Dictionary<string, RagBone> _rag;
        bool _ragdollBuilt, _ragdolling;

        public void BuildRagdoll()
        {
            if (_ragdollBuilt || Skeleton == null || _rag == null) return;
            _ragdollBuilt = true;
            var pbs = new Dictionary<string, PhysicalBone3D>();
            foreach (var kv in _rag)
            {
                string bone = kv.Key; RagBone r = kv.Value;
                int bi = Skeleton.FindBone(bone);
                if (bi < 0) continue;
                var pb = new PhysicalBone3D
                {
                    Name = "PB_" + bone,
                    Mass = r.rb != null ? Mathf.Max((float)r.rb.mass, 0.05f) : 1f,
                    LinearDamp = r.rb != null ? (float)r.rb.drag : 0.01f,
                    AngularDamp = r.rb != null ? (float)r.rb.adrag : 0.05f,
                    CollisionLayer = 1u << 4,             // ragdoll bit
                    CollisionMask = (1u << 0) | (1u << 4), // ground + other ragdoll bones (self-collide -> natural sprawl)
                    JointType = r.joint != null ? PhysicalBone3D.JointTypeEnum.Cone : PhysicalBone3D.JointTypeEnum.None,
                };
                pb.Set("bone_name", bone);
                Skeleton.AddChild(pb);
                pbs[bone] = pb;

                if (r.joint != null)
                {
                    // CharacterJoint -> Godot cone: swing_span = max swing, twist_span = half the twist range.
                    float swing = Mathf.DegToRad((float)Math.Max(r.joint.swing1, r.joint.swing2));
                    float twist = Mathf.DegToRad((float)((r.joint.highTwist - r.joint.lowTwist) * 0.5));
                    pb.Set("joint_constraints/swing_span", Mathf.Max(swing, 0.02f));
                    pb.Set("joint_constraints/twist_span", Mathf.Max(twist, 0.02f));
                }

                var size = r.box?.size; var center = r.box?.center;
                var shape = new CollisionShape3D
                {
                    Shape = new BoxShape3D { Size = size != null ? new Vector3((float)size[0], (float)size[1], (float)size[2]) : new Vector3(0.3f, 0.3f, 0.3f) },
                    Position = center != null ? new Vector3((float)center[0], (float)center[1], (float)center[2]) : Vector3.Zero,
                };
                pb.AddChild(shape);
            }

            // Unity CharacterJoint enableCollision=0: a bone doesn't collide with its jointed parent
            // (nearest physical ancestor). Non-adjacent bones DO collide -> the body sprawls instead of folding through itself.
            foreach (var kv in pbs)
            {
                int p = Skeleton.GetBoneParent(Skeleton.FindBone(kv.Key));
                while (p >= 0)
                {
                    if (pbs.TryGetValue(Skeleton.GetBoneName(p), out var parent)) { kv.Value.AddCollisionExceptionWith(parent); break; }
                    p = Skeleton.GetBoneParent(p);
                }
            }
        }

        // Kill the animation and hand the skeleton to physics; knock the torso with an impulse.
        public void RagdollStart(Vector3 impulse)
        {
            if (_ragdolling) return;
            if (!GraphicsOptions.Ragdolls) { _ragdolling = true; _ap?.Stop(); return; }   // retail IsRagdollsEnabled off: the body just stops where it is
            BuildRagdoll();
            _ragdolling = true;
            _ap?.Stop();
            Skeleton.PhysicalBonesStartSimulation();
            var torso = Skeleton.GetNodeOrNull<PhysicalBone3D>("PB_Spine");
            torso?.ApplyCentralImpulse(impulse);
            var pelvis = Skeleton.GetNodeOrNull<PhysicalBone3D>("PB_Skeleton");
            pelvis?.ApplyCentralImpulse(impulse * 0.5f);
        }

        // Bullet impact: shove the ragdoll at the exact bone the shot hit (headshot snaps the head,
        // shooting a corpse tumbles it). Only affects an already-simulating ragdoll.
        public void ApplyImpact(Vector3 worldPoint, Vector3 impulse)
        {
            if (!_ragdolling) return;
            PhysicalBone3D best = null; float bd = float.MaxValue;
            foreach (var c in Skeleton.GetChildren())
                if (c is PhysicalBone3D pb)
                {
                    float d = pb.GlobalPosition.DistanceSquaredTo(worldPoint);
                    if (d < bd) { bd = d; best = pb; }
                }
            best?.ApplyImpulse(impulse, worldPoint - best.GlobalPosition);
        }

        public bool IsRagdolling => _ragdolling;

        static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        static readonly bool LoadProf = System.Environment.GetEnvironmentVariable("UG_PERF") == "1";

        /// <summary>Binary cache of a parsed RigData (strawberry 2026-09-03 loading optimizations; disk over compute). rig.json is
        /// 22 MB of JSON doubles -- ~400 ms to deserialize on every launch. The first launch parses it and writes
        /// user://rig_cache/<name>_<json size>.rig; later launches read that in a few tens of ms. Keyed by the JSON's
        /// size, so an edited rig.json re-parses. Format is versioned; anything unreadable falls back to JSON.</summary>
        static class RigBin
        {
            const int Version = 1;
            static string CachePath(string resPath)
            {
                long len = 0; try { len = new System.IO.FileInfo(ProjectSettings.GlobalizePath(resPath)).Length; } catch { }
                return ProjectSettings.GlobalizePath($"user://rig_cache/{System.IO.Path.GetFileNameWithoutExtension(resPath)}_{len}.rig");
            }
            public static RigData TryLoad(string resPath)
            {
                string path = CachePath(resPath);
                if (!System.IO.File.Exists(path)) return null;
                try
                {
                    using var br = new System.IO.BinaryReader(new System.IO.BufferedStream(System.IO.File.OpenRead(path), 1 << 20));
                    if (br.ReadInt32() != 0x47495231 || br.ReadInt32() != Version) return null;   // "RIG1"
                    var r = new RigData { vcount = br.ReadInt32() };
                    r.positions = D2(br); r.normals = D2(br); r.uvs = D2(br); r.skin_index = I2(br); r.skin_weight = D2(br); r.faces = I1(br);
                    int nb = br.ReadInt32(); r.bones = nb < 0 ? null : new BoneData[nb];
                    for (int i = 0; i < nb; i++) r.bones[i] = new BoneData { name = Str(br), parent = br.ReadInt32(), pos = D1(br), rot = D1(br), scale = D1(br) };
                    int ns = br.ReadInt32(); r.skin = ns < 0 ? null : new SkinBind[ns];
                    for (int i = 0; i < ns; i++) r.skin[i] = new SkinBind { bone = br.ReadInt32(), pos = D1(br), rot = D1(br), scale = D1(br) };
                    int na = br.ReadInt32(); r.anims = na < 0 ? null : new Dictionary<string, ClipData>(na);
                    for (int i = 0; i < na; i++)
                    {
                        string key = Str(br);
                        var c = new ClipData { fps = br.ReadDouble(), length = br.ReadDouble(), loop = br.ReadBoolean() };
                        int nt = br.ReadInt32(); c.tracks = nt < 0 ? null : new Dictionary<string, TrackData>(nt);
                        for (int k = 0; k < nt; k++) { string tk = Str(br); c.tracks[tk] = new TrackData { rot = D2(br), pos = D2(br), scale = D2(br) }; }
                        r.anims[key] = c;
                    }
                    int nr = br.ReadInt32(); r.ragdoll = nr < 0 ? null : new Dictionary<string, RagBone>(nr);
                    for (int i = 0; i < nr; i++)
                    {
                        string key = Str(br); var rb = new RagBone();
                        if (br.ReadBoolean()) rb.rb = new RagRb { mass = br.ReadDouble(), drag = br.ReadDouble(), adrag = br.ReadDouble() };
                        if (br.ReadBoolean()) rb.box = new RagBox { center = D1(br), size = D1(br) };
                        if (br.ReadBoolean()) rb.joint = new RagJoint { swing1 = br.ReadDouble(), swing2 = br.ReadDouble(), lowTwist = br.ReadDouble(), highTwist = br.ReadDouble() };
                        r.ragdoll[key] = rb;
                    }
                    if (br.ReadBoolean())
                        r.arms = new MeshData { vcount = br.ReadInt32(), positions = D2(br), normals = D2(br), uvs = D2(br), skin_index = I2(br), skin_weight = D2(br), faces = I1(br) };
                    if (br.ReadInt32() != 0x444E4521) return null;   // "!END" trailer: a truncated file is not a rig
                    return r;
                }
                catch (System.Exception e) { GD.PushWarning($"[rig] bad cache {path}: {e.Message}"); return null; }
            }
            public static void TrySave(string resPath, RigData r)
            {
                try
                {
                    string path = CachePath(resPath);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    using var bw = new System.IO.BinaryWriter(new System.IO.BufferedStream(System.IO.File.Create(path), 1 << 20));
                    bw.Write(0x47495231); bw.Write(Version); bw.Write(r.vcount);
                    W(bw, r.positions); W(bw, r.normals); W(bw, r.uvs); W(bw, r.skin_index); W(bw, r.skin_weight); W(bw, r.faces);
                    bw.Write(r.bones?.Length ?? -1); foreach (var b in r.bones ?? System.Array.Empty<BoneData>()) { Str(bw, b.name); bw.Write(b.parent); W(bw, b.pos); W(bw, b.rot); W(bw, b.scale); }
                    bw.Write(r.skin?.Length ?? -1); foreach (var b in r.skin ?? System.Array.Empty<SkinBind>()) { bw.Write(b.bone); W(bw, b.pos); W(bw, b.rot); W(bw, b.scale); }
                    bw.Write(r.anims?.Count ?? -1);
                    if (r.anims != null) foreach (var kv in r.anims)
                    {
                        Str(bw, kv.Key); bw.Write(kv.Value.fps); bw.Write(kv.Value.length); bw.Write(kv.Value.loop);
                        bw.Write(kv.Value.tracks?.Count ?? -1);
                        if (kv.Value.tracks != null) foreach (var t in kv.Value.tracks) { Str(bw, t.Key); W(bw, t.Value.rot); W(bw, t.Value.pos); W(bw, t.Value.scale); }
                    }
                    bw.Write(r.ragdoll?.Count ?? -1);
                    if (r.ragdoll != null) foreach (var kv in r.ragdoll)
                    {
                        Str(bw, kv.Key);
                        bw.Write(kv.Value.rb != null); if (kv.Value.rb != null) { bw.Write(kv.Value.rb.mass); bw.Write(kv.Value.rb.drag); bw.Write(kv.Value.rb.adrag); }
                        bw.Write(kv.Value.box != null); if (kv.Value.box != null) { W(bw, kv.Value.box.center); W(bw, kv.Value.box.size); }
                        bw.Write(kv.Value.joint != null); if (kv.Value.joint != null) { bw.Write(kv.Value.joint.swing1); bw.Write(kv.Value.joint.swing2); bw.Write(kv.Value.joint.lowTwist); bw.Write(kv.Value.joint.highTwist); }
                    }
                    bw.Write(r.arms != null);
                    if (r.arms != null) { bw.Write(r.arms.vcount); W(bw, r.arms.positions); W(bw, r.arms.normals); W(bw, r.arms.uvs); W(bw, r.arms.skin_index); W(bw, r.arms.skin_weight); W(bw, r.arms.faces); }
                    bw.Write(0x444E4521);
                    GD.Print($"[rig] cached {resPath} -> {path}");
                }
                catch (System.Exception e) { GD.PushWarning($"[rig] could not write cache: {e.Message}"); }
            }
            public static Dictionary<string, ClipData> TryLoadClips(string resPath)
            {
                string path = CachePath(resPath);
                if (!System.IO.File.Exists(path)) return null;
                try
                {
                    using var br = new System.IO.BinaryReader(new System.IO.BufferedStream(System.IO.File.OpenRead(path), 1 << 20));
                    if (br.ReadInt32() != 0x434C5031 || br.ReadInt32() != Version) return null;   // "CLP1"
                    int na = br.ReadInt32(); if (na < 0) return null;
                    var d = new Dictionary<string, ClipData>(na);
                    for (int i = 0; i < na; i++)
                    {
                        string key = Str(br);
                        var c = new ClipData { fps = br.ReadDouble(), length = br.ReadDouble(), loop = br.ReadBoolean() };
                        int nt = br.ReadInt32(); c.tracks = nt < 0 ? null : new Dictionary<string, TrackData>(nt);
                        for (int k = 0; k < nt; k++) { string tk = Str(br); c.tracks[tk] = new TrackData { rot = D2(br), pos = D2(br), scale = D2(br) }; }
                        d[key] = c;
                    }
                    if (br.ReadInt32() != 0x444E4521) return null;
                    return d;
                }
                catch (System.Exception e) { GD.PushWarning($"[rig] bad clip cache {path}: {e.Message}"); return null; }
            }
            public static void TrySaveClips(string resPath, Dictionary<string, ClipData> d)
            {
                try
                {
                    string path = CachePath(resPath);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    using var bw = new System.IO.BinaryWriter(new System.IO.BufferedStream(System.IO.File.Create(path), 1 << 20));
                    bw.Write(0x434C5031); bw.Write(Version); bw.Write(d.Count);
                    foreach (var kv in d)
                    {
                        Str(bw, kv.Key); bw.Write(kv.Value.fps); bw.Write(kv.Value.length); bw.Write(kv.Value.loop);
                        bw.Write(kv.Value.tracks?.Count ?? -1);
                        if (kv.Value.tracks != null) foreach (var t in kv.Value.tracks) { Str(bw, t.Key); W(bw, t.Value.rot); W(bw, t.Value.pos); W(bw, t.Value.scale); }
                    }
                    bw.Write(0x444E4521);
                }
                catch (System.Exception e) { GD.PushWarning($"[rig] could not write clip cache: {e.Message}"); }
            }
            // --- primitives: null-aware arrays (length -1 = null) ---
            static void Str(System.IO.BinaryWriter bw, string s) { bw.Write(s != null); if (s != null) bw.Write(s); }
            static string Str(System.IO.BinaryReader br) => br.ReadBoolean() ? br.ReadString() : null;
            static void W(System.IO.BinaryWriter bw, double[] a) { bw.Write(a?.Length ?? -1); if (a != null) foreach (var d in a) bw.Write(d); }
            static void W(System.IO.BinaryWriter bw, int[] a) { bw.Write(a?.Length ?? -1); if (a != null) foreach (var d in a) bw.Write(d); }
            static void W(System.IO.BinaryWriter bw, double[][] a) { bw.Write(a?.Length ?? -1); if (a != null) foreach (var row in a) W(bw, row); }
            static void W(System.IO.BinaryWriter bw, int[][] a) { bw.Write(a?.Length ?? -1); if (a != null) foreach (var row in a) W(bw, row); }
            static double[] D1(System.IO.BinaryReader br) { int n = br.ReadInt32(); if (n < 0) return null; var a = new double[n]; for (int i = 0; i < n; i++) a[i] = br.ReadDouble(); return a; }
            static int[] I1(System.IO.BinaryReader br) { int n = br.ReadInt32(); if (n < 0) return null; var a = new int[n]; for (int i = 0; i < n; i++) a[i] = br.ReadInt32(); return a; }
            static double[][] D2(System.IO.BinaryReader br) { int n = br.ReadInt32(); if (n < 0) return null; var a = new double[n][]; for (int i = 0; i < n; i++) a[i] = D1(br); return a; }
            static int[][] I2(System.IO.BinaryReader br) { int n = br.ReadInt32(); if (n < 0) return null; var a = new int[n][]; for (int i = 0; i < n; i++) a[i] = I1(br); return a; }
        }

        static readonly System.Collections.Generic.Dictionary<string, RigData> _rigCache = new();   // per-path (player/deer/pig/cow rigs coexist)

        // Built-once, shared across every character of the same rig+variant. The 316-clip AnimationLibrary is the
        // dominant per-build cost (each clip inserts per-bone rot/pos/scale keyframes) -- rebuilding it on every
        // `new Viewmodel` was the big equip frame-hitch. The skinned geometry (ArrayMesh + Skin) is identical too.
        // Keyed by (RigData ref, armsOnly): RigData is cached per-path so the player/viewmodel/zombies share one ref;
        // armsOnly separates the arms library (has consumable clips + one-shot equip overrides) from the full-body one
        // (has _body's Idle_Drive PlayLoop override) so their loop-mode mutations never collide. Sharing is safe:
        // AnimationPlayer playback state is per-instance; the only clip mutations are consistent within each variant;
        // MeshInstance3D material/tint and the face decal stay per-instance (built fresh below).
        static readonly System.Collections.Generic.Dictionary<(RigData, bool), (AnimationLibrary lib, string[] names)> _animCache = new();
        static readonly System.Collections.Generic.Dictionary<(RigData, bool), (ArrayMesh mesh, Skin skin)> _skinCache = new();

        // The mesh + skin were cached per rig, but the TEXTURE never was: every character built re-read its atlas
        // off disk and made a fresh ImageTexture, so a POI of 20 zombies uploaded 20 copies of the same image to
        // the GPU (there are only 6 zombie atlases in total, and one shared face). That is per-instance VRAM and a
        // disk read per spawn for bytes we already had. Texture only -- NOT the material: SetGhost/SetShirt mutate
        // the material per instance, so a shared one would ghost every zombie at once.
        static readonly System.Collections.Generic.Dictionary<string, ImageTexture> _texCache = new();

        static ImageTexture LoadTexCached(string resPath)
        {
            if (resPath == null) return null;
            if (_texCache.TryGetValue(resPath, out var cached) && cached != null) return cached;
            var img = ContentProvider.LoadImage(ProjectSettings.GlobalizePath(resPath));
            if (img == null) return null;
            var tex = ImageTexture.CreateFromImage(img);
            _texCache[resPath] = tex;
            return tex;
        }

        // The 36+36 distinct consumable Equip/Use clips (CE_n/CU_n) live in their OWN file so rig.json stays lean.
        // Only the 1P arms viewmodel needs them, so they're merged in for armsOnly builds (not the 3P body/zombies).
        static System.Collections.Generic.Dictionary<string, ClipData> _consumableAnims;
        static System.Collections.Generic.Dictionary<string, ClipData> ConsumableAnims()
        {
            if (_consumableAnims == null)
            {
                _consumableAnims = new();
                using var f = FileAccess.Open("res://content/consumable_anims.json", FileAccess.ModeFlags.Read);
                if (f != null)
                {
                    long tc = System.Diagnostics.Stopwatch.GetTimestamp();
                    _consumableAnims = RigBin.TryLoadClips("res://content/consumable_anims.json");   // same binary cache as the rig (user://rig_cache)
                    string src = "bin";
                    if (_consumableAnims == null)
                    {
                        _consumableAnims = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, ClipData>>(f.GetBuffer((long)f.GetLength()), JsonOpts) ?? new();
                        RigBin.TrySaveClips("res://content/consumable_anims.json", _consumableAnims);
                        src = "json";
                    }
                    if (LoadProf) GD.Print($"[rigprof] consumable_anims parsed from {src} in {(System.Diagnostics.Stopwatch.GetTimestamp() - tc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms");
                }
            }
            return _consumableAnims;
        }
        // Parse rig.json once, reuse the data for every character built (20 zombies shouldn't reparse 600KB).
        public static RiggedCharacter Build(string resPath, Color tint, bool armsOnly = false, string albedoTexPath = null, string faceTexPath = null)
        {
            if (!_rigCache.TryGetValue(resPath, out var rigData))
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                string src = "bin";
                rigData = RigBin.TryLoad(resPath);   // user://rig_cache/<name>_<size>.rig -- the JSON parsed ONCE per machine (rig.json is 22 MB)
                if (rigData == null)
                {
                    using var f = FileAccess.Open(resPath, FileAccess.ModeFlags.Read);
                    if (f == null) { GD.PrintErr($"[rig] cannot open {resPath}"); return null; }
                    var bytes = f.GetBuffer((long)f.GetLength());   // parse the UTF-8 bytes directly: GetAsText() built a 44 MB UTF-16 copy first
                    rigData = JsonSerializer.Deserialize<RigData>(bytes, JsonOpts);
                    RigBin.TrySave(resPath, rigData);
                    src = "json";
                }
                _rigCache[resPath] = rigData;
                if (LoadProf) GD.Print($"[rigprof] {resPath} parsed from {src} in {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms");
            }
            return BuildFrom(rigData, tint, armsOnly, albedoTexPath, faceTexPath);
        }

        public MeshInstance3D Body { get; private set; }

        public static RiggedCharacter BuildFrom(RigData rig, Color tint, bool armsOnly = false, string albedoTexPath = null, string faceTexPath = null)
        {
            var root = new RiggedCharacter();

            // ---- skeleton ----
            var skel = new Skeleton3D { Name = "Skeleton3D" };
            root.AddChild(skel);
            foreach (var b in rig.bones) skel.AddBone(b.name);
            for (int i = 0; i < rig.bones.Length; i++)
            {
                var b = rig.bones[i];
                if (b.parent >= 0) skel.SetBoneParent(i, b.parent);
                skel.SetBoneRest(i, Xf(b.pos, b.rot, b.scale));
            }
            skel.ResetBonePoses();
            root.Skeleton = skel;
            root.AttachLeanModifier();   // every rig, not only one that later gets a gun layer

            // ---- skinned mesh (raw arrays; arms-only variant for the 1P viewmodel) ----
            // Geometry (ArrayMesh) + Skin are identical for every character of this rig+variant, so build once and
            // share the resources. Material/tint is set per-instance below via mi.MaterialOverride (never on the mesh).
            if (!_skinCache.TryGetValue((rig, armsOnly), out var geom))
            {
                var m = (armsOnly && rig.arms != null) ? rig.arms
                    : new MeshData { vcount = rig.vcount, positions = rig.positions, normals = rig.normals, uvs = rig.uvs, skin_index = rig.skin_index, skin_weight = rig.skin_weight, faces = rig.faces };
                int vc = m.vcount;
                var verts = new Vector3[vc]; var norms = new Vector3[vc]; var uvs = new Vector2[vc];
                var bones = new int[vc * 4]; var weights = new float[vc * 4];
                for (int v = 0; v < vc; v++)
                {
                    verts[v] = new Vector3((float)m.positions[v][0], (float)m.positions[v][1], (float)m.positions[v][2]);
                    norms[v] = new Vector3((float)m.normals[v][0], (float)m.normals[v][1], (float)m.normals[v][2]);
                    uvs[v] = new Vector2((float)m.uvs[v][0], (float)m.uvs[v][1]);
                    bones[v * 4 + 0] = m.skin_index[v][0];
                    bones[v * 4 + 1] = m.skin_index[v][1];
                    float w0 = (float)m.skin_weight[v][0], w1 = (float)m.skin_weight[v][1];
                    float sum = w0 + w1; if (sum < 1e-6f) { w0 = 1f; w1 = 0f; sum = 1f; }
                    weights[v * 4 + 0] = w0 / sum; weights[v * 4 + 1] = w1 / sum;
                }
                var idx = new int[m.faces.Length];
                Array.Copy(m.faces, idx, m.faces.Length);

                var arr = new Godot.Collections.Array();
                arr.Resize((int)Mesh.ArrayType.Max);
                arr[(int)Mesh.ArrayType.Vertex] = verts;
                arr[(int)Mesh.ArrayType.Normal] = norms;
                arr[(int)Mesh.ArrayType.TexUV] = uvs;
                arr[(int)Mesh.ArrayType.Bones] = bones;
                arr[(int)Mesh.ArrayType.Weights] = weights;
                arr[(int)Mesh.ArrayType.Index] = idx;
                var builtMesh = new ArrayMesh();
                builtMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);

                // ---- skin: mesh blend index j -> skeleton bone + bind pose ----
                var builtSkin = new Skin();
                builtSkin.SetBindCount(rig.skin.Length);
                for (int j = 0; j < rig.skin.Length; j++)
                {
                    builtSkin.SetBindBone(j, rig.skin[j].bone);
                    builtSkin.SetBindPose(j, Xf(rig.skin[j].pos, rig.skin[j].rot, rig.skin[j].scale));
                }
                geom = (builtMesh, builtSkin);
                _skinCache[(rig, armsOnly)] = geom;
            }
            var mesh = geom.mesh;
            var skin = geom.skin;

            var mi = new MeshInstance3D { Name = "Body", Mesh = mesh, VisibilityRangeEnd = 95f };   // horde perf: don't draw a skinned body past ~95m (player/arms always near, never culled)
            root.Body = mi;
            skel.AddChild(mi);
            mi.Skin = skin;
            mi.Skeleton = mi.GetPathTo(skel);
            // Two body-material paths:
            //  - albedoTexPath != null (zombies/animals): a pre-baked skin+shirt+pants atlas (ZombieClothing
            //    composite -- NO face; the face-in-atlas bake landed on the LEFT ARM's texels, see
            //    tools/bake_zombie_variants.py + the Skull quad below) on a flat StandardMaterial3D. Kept as-is:
            //    it's opaque + cheap (horde perf) and already contains the clothing, so it must NOT go through
            //    the clothes shader (which would paint plain skin over it).
            //  - albedoTexPath == null (player 3P body, corpse, 1P arms): the ported StandardClothes shader --
            //    a skin base that SetShirt/SetPants paint real clothing textures onto (P3a). A bare body reads
            //    as plain skin_color (no shirt/pants bound). skin.png turned out to be a cosmetic item-skin
            //    atlas, not the body; the skin is the flat tint per team.
            root._bodyTint = tint;
            if (albedoTexPath != null)
            {
                var bodyMat = new StandardMaterial3D
                {
                    AlbedoColor = tint,
                    CullMode = BaseMaterial3D.CullModeEnum.Front, // Z-flip reverses winding -> cull the (reversed) BACK faces = single-sided = HALF the fragment cost (was Disabled/double-sided, the horde's per-pixel killer)
                };
                var tex = LoadTexCached(albedoTexPath);   // shared across every zombie using this atlas
                if (tex != null)
                {
                    bodyMat.AlbedoTexture = tex;
                    bodyMat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;   // blocky Unturned pixels
                }
                mi.MaterialOverride = bodyMat;
                root._bodyMat = bodyMat;
            }
            else
            {
                // clothes.gdshader ports StandardClothes: cull_front replaces CullMode.Front; skin_color = the
                // team/skin tint. No shirt/pants bound -> renders as plain skin (identical to the old flat tint).
                var cm = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/clothes.gdshader") };
                cm.SetShaderParameter("skin_color", tint);
                mi.MaterialOverride = cm;
                root._clothesMat = cm;
            }

            // Unturned's face is a shader-painted decal, NOT in the mesh UV (the head-front UV0 is a skin-only
            // sliver + there's no UV1). Reproduce it as a small quad on the head-front, textured with the real
            // Faces/19 (transparent bg -> only the eyes+mouth show over the skin). Double-sided; symmetric so the
            // mirror is invisible. Parented to the character root (follows position/turn; head-bob float is tiny).
            if (faceTexPath != null && !armsOnly)
            {
                var ftex = LoadTexCached(faceTexPath);   // one shared face texture, not one per character
                if (ftex != null)
                {
                    // Bone-attach to the Skull so the face TRACKS the head through animation + ragdoll (not a fixed
                    // root child, which floats at rest-pose height). Skull rest = pos(0,1.32,0), basis maps
                    // world=(localY,-localX,localZ); the head-front world (0,1.75,-0.25) -> bone-local (-0.43,0,-0.25).
                    var att = new BoneAttachment3D { BoneName = "Skull" };
                    skel.AddChild(att);
                    var fq = new MeshInstance3D { Name = "Face", Mesh = new QuadMesh { Size = new Vector2(0.38f, 0.38f) }, VisibilityRangeEnd = 45f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };   // tiny transparent decal: cull its overdraw past ~45m + it never needs a shadow
                    fq.MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoTexture = ftex,
                        Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,   // hard-edged pixel decal -> CUTOUT (early-z, no blend overdraw) beats alpha-blend
                        AlphaScissorThreshold = 0.5f,
                        TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    };
                    att.AddChild(fq);
                    root._faceQuad = fq;
                    { var fm = System.Text.RegularExpressions.Regex.Match(faceTexPath, @"face_(\d+)"); if (fm.Success && int.TryParse(fm.Groups[1].Value, out int fi)) root.SetFace(fi); }   // picks up the emission map for faces that have one
                    fq.Position = new Vector3(-0.43f, 0f, -0.25f);
                    fq.Basis = new Basis(new Vector3(0f, -1f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, -1f));
                }
            }

            // ---- animations ----
            // The library (316 clips, each with per-bone keyframe tracks) is the dominant per-build cost and is
            // identical for every character of this rig+variant -- build it once and share the resource across every
            // AnimationPlayer. Playback position is per-player; loop-mode overrides (SetClipLoop/PlayLoop) are
            // consistent within a variant, so the shared clips converge correctly. This is what kills the equip hitch.
            var ap = new AnimationPlayer { Name = "Anim" };
            root.AddChild(ap);
            if (!_animCache.TryGetValue((rig, armsOnly), out var built))
            {
                long ta = System.Diagnostics.Stopwatch.GetTimestamp();
                var lib = new AnimationLibrary();
                var names = new List<string>();
                if (_animCache.TryGetValue((rig, !armsOnly), out var sibling))
                {
                    // the body and the viewmodel arms share one rig -> the SAME Animation resources; only the arms add the
                    // consumable clips below. Building the 316 clips twice was ~a third of the Player load phase.
                    foreach (var nm in sibling.names) { lib.AddAnimation(nm, sibling.lib.GetAnimation(nm)); names.Add(nm); }
                }
                else if (rig.anims != null)
                    foreach (var kv in rig.anims)
                    {
                        lib.AddAnimation(kv.Key, BuildAnim(kv.Value));
                        names.Add(kv.Key);
                    }
                if (armsOnly)   // viewmodel: also load the per-item consumable eat/drink clips (CE_n/CU_n)
                    foreach (var kv in ConsumableAnims())
                        if (!names.Contains(kv.Key)) { lib.AddAnimation(kv.Key, BuildAnim(kv.Value)); names.Add(kv.Key); }
                built = (lib, names.ToArray());
                _animCache[(rig, armsOnly)] = built;
                if (LoadProf) GD.Print($"[rigprof] anim library (armsOnly={armsOnly}) {names.Count} clips in {(System.Diagnostics.Stopwatch.GetTimestamp() - ta) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms");
            }
            ap.AddAnimationLibrary("", built.lib);
            root._ap = ap;
            ap.AnimationFinished += root.OnApFinished;   // PERF: park on a looping hold instead of the finished clip (see HoldOf)
            root._lib = built.lib;   // kept so a lazily-created gun-overlay AnimationPlayer (3P) can share the same clips
            root.ClipNames = built.names;
            root._rag = rig.ragdoll;
            if (armsOnly) root.SetupAimAdditive();   // viewmodel: bake the Gun_Aim additive ADS layer
            return root;
        }

        static Animation BuildAnim(ClipData c)
        {
            var a = new Animation { Length = (float)Math.Max(c.length, 1.0 / 30.0) };
            a.LoopMode = c.loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            if (c.tracks == null) return a;
            foreach (var kv in c.tracks)
            {
                string path = "Skeleton3D:" + kv.Key;
                var tr = kv.Value;
                if (tr.rot != null && tr.rot.Length > 0)
                {
                    int t = a.AddTrack(Animation.TrackType.Rotation3D);
                    a.TrackSetPath(t, (NodePath)path);
                    foreach (var k in tr.rot)
                        a.RotationTrackInsertKey(t, k[0], new Quaternion((float)k[1], (float)k[2], (float)k[3], (float)k[4]).Normalized());
                }
                if (tr.pos != null && tr.pos.Length > 0)
                {
                    int t = a.AddTrack(Animation.TrackType.Position3D);
                    a.TrackSetPath(t, (NodePath)path);
                    foreach (var k in tr.pos)
                        a.PositionTrackInsertKey(t, k[0], new Vector3((float)k[1], (float)k[2], (float)k[3]));
                }
                if (tr.scale != null && tr.scale.Length > 0)
                {
                    int t = a.AddTrack(Animation.TrackType.Scale3D);
                    a.TrackSetPath(t, (NodePath)path);
                    foreach (var k in tr.scale)
                        a.ScaleTrackInsertKey(t, k[0], new Vector3((float)k[1], (float)k[2], (float)k[3]));
                }
            }
            return a;
        }

        static Transform3D Xf(double[] pos, double[] rot, double[] scale)
        {
            var q = new Quaternion((float)rot[0], (float)rot[1], (float)rot[2], (float)rot[3]).Normalized();
            var basis = new Basis(q).Scaled(new Vector3((float)scale[0], (float)scale[1], (float)scale[2]));
            return new Transform3D(basis, new Vector3((float)pos[0], (float)pos[1], (float)pos[2]));
        }

        public class RigData
        {
            public int vcount { get; set; }
            public double[][] positions { get; set; }
            public double[][] normals { get; set; }
            public double[][] uvs { get; set; }
            public int[][] skin_index { get; set; }
            public double[][] skin_weight { get; set; }
            public int[] faces { get; set; }
            public BoneData[] bones { get; set; }
            public SkinBind[] skin { get; set; }
            public Dictionary<string, ClipData> anims { get; set; }
            public Dictionary<string, RagBone> ragdoll { get; set; }
            public MeshData arms { get; set; }
        }
        public class MeshData
        {
            public int vcount { get; set; }
            public double[][] positions { get; set; }
            public double[][] normals { get; set; }
            public double[][] uvs { get; set; }
            public int[][] skin_index { get; set; }
            public double[][] skin_weight { get; set; }
            public int[] faces { get; set; }
        }
        public class RagBone { public RagRb rb { get; set; } public RagBox box { get; set; } public RagJoint joint { get; set; } }
        public class RagRb { public double mass { get; set; } = 1; public double drag { get; set; } = 0.01; public double adrag { get; set; } = 0.05; }
        public class RagBox { public double[] center { get; set; } public double[] size { get; set; } }
        public class RagJoint { public double swing1 { get; set; } public double swing2 { get; set; } public double lowTwist { get; set; } public double highTwist { get; set; } }
        public class BoneData { public string name { get; set; } public int parent { get; set; } public double[] pos { get; set; } public double[] rot { get; set; } public double[] scale { get; set; } }
        public class SkinBind { public int bone { get; set; } public double[] pos { get; set; } public double[] rot { get; set; } public double[] scale { get; set; } }
        public class ClipData { public double fps { get; set; } public double length { get; set; } public bool loop { get; set; } = true; public Dictionary<string, TrackData> tracks { get; set; } }
        public class TrackData { public double[][] rot { get; set; } public double[][] pos { get; set; } public double[][] scale { get; set; } }
    }
}
