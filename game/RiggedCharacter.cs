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
        StandardMaterial3D _bodyMat;   // body surface material, for the FLANKER_STALK ghost toggle
        Color _bodyTint;               // solid-state albedo, restored when un-ghosting
        public Skeleton3D Skeleton { get; private set; }
        public string[] ClipNames { get; private set; } = Array.Empty<string>();

        // FLANKER_STALK: swap the body to a faint translucent shimmer (Unturned's ZombieClothing.ghostMaterial) --
        // NOT fully gone; a keen eye can still pick out the stalker. Restores the solid tint when off.
        public void SetGhost(bool ghost)
        {
            if (_bodyMat == null) return;
            _bodyMat.Transparency = ghost ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled;
            _bodyMat.AlbedoColor = new Color(_bodyTint.R, _bodyTint.G, _bodyTint.B, ghost ? 0.2f : 1f);
        }

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

        // Bake the Gun_Aim additive delta (per bone, end relative to frame 0) and switch the arms' player to
        // manual advance so we can apply that delta on top of the base pose each frame. Viewmodel arms only.
        void SetupAimAdditive()
        {
            if (_ap == null || Skeleton == null || !_ap.HasAnimation("Gun_Aim")) return;
            var anim = _ap.GetAnimation("Gun_Aim");
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

        static RigData _shared;
        // Parse rig.json once, reuse the data for every character built (20 zombies shouldn't reparse 600KB).
        public static RiggedCharacter Build(string resPath, Color tint, bool armsOnly = false, string albedoTexPath = null)
        {
            if (_shared == null)
            {
                using var f = FileAccess.Open(resPath, FileAccess.ModeFlags.Read);
                if (f == null) { GD.PrintErr($"[rig] cannot open {resPath}"); return null; }
                _shared = JsonSerializer.Deserialize<RigData>(f.GetAsText(), JsonOpts);
            }
            return BuildFrom(_shared, tint, armsOnly, albedoTexPath);
        }

        public MeshInstance3D Body { get; private set; }

        public static RiggedCharacter BuildFrom(RigData rig, Color tint, bool armsOnly = false, string albedoTexPath = null)
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
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);

            // ---- skin: mesh blend index j -> skeleton bone + bind pose ----
            var skin = new Skin();
            skin.SetBindCount(rig.skin.Length);
            for (int j = 0; j < rig.skin.Length; j++)
            {
                skin.SetBindBone(j, rig.skin[j].bone);
                skin.SetBindPose(j, Xf(rig.skin[j].pos, rig.skin[j].rot, rig.skin[j].scale));
            }

            var mi = new MeshInstance3D { Name = "Body", Mesh = mesh };
            root.Body = mi;
            skel.AddChild(mi);
            mi.Skin = skin;
            mi.Skeleton = mi.GetPathTo(skel);
            // Unturned's character body is a flat skin-tone colour (clothing is separate meshes); skin.png
            // turned out to be a cosmetic item-skin atlas, not the body. Flat tint per team.
            var bodyMat = new StandardMaterial3D
            {
                AlbedoColor = tint,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, // skinned winding is doubled
            };
            // optional baked skin atlas (ZombieClothing composite: skin + shirt + pants + face decal). The tint
            // multiplies it, so a NORMAL zombie passes white for the natural look, specials an accent colour.
            if (albedoTexPath != null)
            {
                var img = Image.LoadFromFile(ProjectSettings.GlobalizePath(albedoTexPath));
                if (img != null)
                {
                    bodyMat.AlbedoTexture = ImageTexture.CreateFromImage(img);
                    bodyMat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;   // blocky Unturned pixels
                }
            }
            mi.MaterialOverride = bodyMat;
            root._bodyMat = bodyMat;
            root._bodyTint = tint;

            // ---- animations ----
            var ap = new AnimationPlayer { Name = "Anim" };
            root.AddChild(ap);
            var lib = new AnimationLibrary();
            var names = new List<string>();
            if (rig.anims != null)
                foreach (var kv in rig.anims)
                {
                    lib.AddAnimation(kv.Key, BuildAnim(kv.Value));
                    names.Add(kv.Key);
                }
            ap.AddAnimationLibrary("", lib);
            root._ap = ap;
            root.ClipNames = names.ToArray();
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
