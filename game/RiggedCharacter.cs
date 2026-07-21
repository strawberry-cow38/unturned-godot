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

        // FLANKER_STALK: swap the body to a faint translucent shimmer (Unturned's ZombieClothing.ghostMaterial) --
        // NOT fully gone; a keen eye can still pick out the stalker. Restores the solid tint when off.
        // Clothes-shader body: drive the ghost_alpha uniform (1.0 solid / 0.2 shimmer). Atlas body: the old
        // StandardMaterial3D transparency path (unchanged -- FLANKER zombies use the atlas path).
        public void SetGhost(bool ghost)
        {
            if (_clothesMat != null) { _clothesMat.SetShaderParameter("ghost_alpha", ghost ? 0.2f : 1f); return; }
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

        public void Play(string name, float speed = 1f)
        {
            if (_ap != null && !string.IsNullOrEmpty(name) && _ap.HasAnimation(name))
            { _ap.Play(name, -1, speed); }
        }

        // Snap straight to a clip's END pose (Seek with update:true applies it this frame). Used to return to the
        // ready hold instantly when an inspect is cancelled -- without replaying the equip pull-out from frame 0.
        public void SnapToEnd(string name)
        {
            if (_ap != null && !string.IsNullOrEmpty(name) && _ap.HasAnimation(name))
            {
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
            if (stance == SDG.Unturned.EPlayerStance.CROUCH) { idle = "Idle_Crouch"; walk = run = "Move_Crouch"; }
            else if (stance == SDG.Unturned.EPlayerStance.PRONE) { idle = "Idle_Prone"; walk = run = "Move_Prone"; }
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

        public void Tick(double delta)
        {
            if (_oneShot > 0) _oneShot -= delta;
            if (_ap != null && _ap.CallbackModeProcess == AnimationMixer.AnimationCallbackModeProcess.Manual)
            {
                _ap.Advance(delta);   // base pose (equip/hold), manually driven so we can layer the aim delta on
                ApplyAimAdditive();
            }
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
            foreach (var kv in _aimDR)
                Skeleton.SetBonePoseRotation(kv.Key, Skeleton.GetBonePoseRotation(kv.Key) * Quaternion.Identity.Slerp(kv.Value, AimBlend));
            foreach (var kv in _aimDP)
                Skeleton.SetBonePosePosition(kv.Key, Skeleton.GetBonePosePosition(kv.Key) + kv.Value * AimBlend);
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

        // The 36+36 distinct consumable Equip/Use clips (CE_n/CU_n) live in their OWN file so rig.json stays lean.
        // Only the 1P arms viewmodel needs them, so they're merged in for armsOnly builds (not the 3P body/zombies).
        static System.Collections.Generic.Dictionary<string, ClipData> _consumableAnims;
        static System.Collections.Generic.Dictionary<string, ClipData> ConsumableAnims()
        {
            if (_consumableAnims == null)
            {
                _consumableAnims = new();
                using var f = FileAccess.Open("res://content/consumable_anims.json", FileAccess.ModeFlags.Read);
                if (f != null) _consumableAnims = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, ClipData>>(f.GetAsText(), JsonOpts) ?? new();
            }
            return _consumableAnims;
        }
        // Parse rig.json once, reuse the data for every character built (20 zombies shouldn't reparse 600KB).
        public static RiggedCharacter Build(string resPath, Color tint, bool armsOnly = false, string albedoTexPath = null, string faceTexPath = null)
        {
            if (!_rigCache.TryGetValue(resPath, out var rigData))
            {
                using var f = FileAccess.Open(resPath, FileAccess.ModeFlags.Read);
                if (f == null) { GD.PrintErr($"[rig] cannot open {resPath}"); return null; }
                rigData = JsonSerializer.Deserialize<RigData>(f.GetAsText(), JsonOpts);
                _rigCache[resPath] = rigData;
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
                var img = Image.LoadFromFile(ProjectSettings.GlobalizePath(albedoTexPath));
                if (img != null)
                {
                    bodyMat.AlbedoTexture = ImageTexture.CreateFromImage(img);
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
                var fimg = Image.LoadFromFile(ProjectSettings.GlobalizePath(faceTexPath));
                if (fimg != null)
                {
                    // Bone-attach to the Skull so the face TRACKS the head through animation + ragdoll (not a fixed
                    // root child, which floats at rest-pose height). Skull rest = pos(0,1.32,0), basis maps
                    // world=(localY,-localX,localZ); the head-front world (0,1.75,-0.25) -> bone-local (-0.43,0,-0.25).
                    var att = new BoneAttachment3D { BoneName = "Skull" };
                    skel.AddChild(att);
                    var fq = new MeshInstance3D { Name = "Face", Mesh = new QuadMesh { Size = new Vector2(0.38f, 0.38f) }, VisibilityRangeEnd = 45f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };   // tiny transparent decal: cull its overdraw past ~45m + it never needs a shadow
                    fq.MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoTexture = ImageTexture.CreateFromImage(fimg),
                        Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,   // hard-edged pixel decal -> CUTOUT (early-z, no blend overdraw) beats alpha-blend
                        AlphaScissorThreshold = 0.5f,
                        TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    };
                    att.AddChild(fq);
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
                var lib = new AnimationLibrary();
                var names = new List<string>();
                if (rig.anims != null)
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
            }
            ap.AddAnimationLibrary("", built.lib);
            root._ap = ap;
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
