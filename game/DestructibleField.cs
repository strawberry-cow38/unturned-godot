using Godot;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot
{
    // Destructible props (retail rubble). The registry side of the DestructibleReplication(16) alive-bitmap:
    // a deterministic index space over PEI's placed destructible objects (WorldBuilder assigns the index in
    // placements.txt scan order, holiday-stable), each slot holding the object's live nodes (mesh(es) +
    // collider) plus its retail rubble scalars (Rubble_Health, Rubble_Reset). SetAlive(index,false) breaks a
    // prop -- hides its mesh(es) and drops its collider -- exactly the ResourceField.SetAlive contract, but on
    // individually-placed MeshInstance3D/StaticBody3D nodes instead of a MultiMesh. Server + client both build
    // it (identical index space, content-hash-matched); the server owns health/respawn (ServerDestructibles),
    // the client just mirrors the replicated alive bit.
    //
    // Not a Node: the meshes/colliders it references are already parented to the world root by
    // WorldBuilder.PlaceObject; this is a pure registry the net syncs drive.
    public sealed class DestructibleField
    {
        sealed class Rec
        {
            public MeshInstance3D[] Meshes;   // main mesh + optional foliage mesh (null slot = never built: out-of-season holiday / no colliders)
            public StaticBody3D Body;
            public uint BodyLayer;
            public float MaxHealth;           // 0 = unregistered slot (indestructible)
            public long ResetTicks;
            public int EffectId;              // Rubble_Effect id -> the retail break VFX
            public bool Alive = true;
        }

        /// <summary>Collider meta key carrying a destructible's index -- the server hit resolution
        /// (GodotWorldRay) reads it to route bullet/melee damage to the right prop.</summary>
        public static readonly StringName MetaKey = "destructible_index";

        Rec[] _recs = System.Array.Empty<Rec>();

        /// <summary>How many slots actually bound a built prop with health (Register calls) -- vs InstanceCount,
        /// which includes reserved-but-unbuilt holiday tail slots. A boot-time sanity number: if this is 0 the
        /// index space never got wired (the register-before-setcount bug), so nothing is destructible.</summary>
        public int BuiltCount { get; private set; }

        /// <summary>Total destructible-placement slots in the deterministic index space (includes reserved
        /// slots for out-of-season holiday placements that never build a node -- they keep the index aligned
        /// across peers). The bitmap sizes to this.</summary>
        public int InstanceCount => _recs.Length;

        // A reserved-but-unbuilt slot (out-of-season holiday) reads as ALIVE/intact -- there's no prop to break,
        // and the server bitmap keeps it alive too, so the net-sync mirror skips it instead of churning.
        public bool IsAlive(int index) => index < 0 || index >= _recs.Length || _recs[index] == null || _recs[index].Alive;
        public float MaxHealth(int index) => index >= 0 && index < _recs.Length && _recs[index] != null ? _recs[index].MaxHealth : 0f;
        public long ResetTicks(int index) => index >= 0 && index < _recs.Length && _recs[index] != null ? _recs[index].ResetTicks : 0L;

        void EnsureSize(int n) { if (n > _recs.Length) System.Array.Resize(ref _recs, n); }

        /// <summary>Reserve the whole index space to `total` slots. GROW-ONLY: WorldBuilder calls Register
        /// DURING the placement scan (before it knows the final count) and SetCount AFTER, so this must never
        /// shrink away already-registered recs -- it only extends to cover reserved-but-unbuilt tail slots
        /// (out-of-season holiday) so the bitmap size matches across peers.</summary>
        public void SetCount(int total) => EnsureSize(total);

        /// <summary>Bind a built destructible's live nodes + rubble scalars to its deterministic index. Grows
        /// the backing array to fit (Register runs before SetCount in the WorldBuilder scan order).</summary>
        public void Register(int index, StaticBody3D body, MeshInstance3D[] meshes, float maxHealth, long resetTicks, int effectId = 0)
        {
            if (index < 0) return;
            EnsureSize(index + 1);
            if (_recs[index] == null) BuiltCount++;
            _recs[index] = new Rec { Meshes = meshes, Body = body, BodyLayer = body?.CollisionLayer ?? 0u,
                                     MaxHealth = maxHealth, ResetTicks = resetTicks, EffectId = effectId };
        }

        /// <summary>Break (false) or respawn (true) one prop by index: hide/show its mesh(es) and toggle its
        /// collision. Idempotent. A reserved-but-unbuilt slot (out-of-season holiday) is a no-op.</summary>
        public void SetAlive(int index, bool alive)
        {
            if (index < 0 || index >= _recs.Length) return;
            var r = _recs[index];
            if (r == null || r.Alive == alive) return;
            r.Alive = alive;
            if (r.Meshes != null)
                foreach (var m in r.Meshes)
                    if (m != null && GodotObject.IsInstanceValid(m)) m.Visible = alive;
            if (r.Body != null && GodotObject.IsInstanceValid(r.Body)) r.Body.CollisionLayer = alive ? r.BodyLayer : 0u;
        }

        /// <summary>Play the one-shot break VFX for a prop that just shattered. Plays the prop's ACTUAL retail
        /// Rubble_Effect (Metal_5, Glass_0, Wheat_0, ...) -- the real sprite + burst count + cone/box shape + start
        /// speed/size/lifetime + gravity/tumble, extracted from core.masterbundle (see RubbleFx / tools/
        /// extract_rubble_effects.py) and reproduced on a CpuParticles3D. A prop with no effect id (0) or a missing
        /// sprite falls back to a generic material-tint debris puff. Wired to Client.ObjectDestroyed (a LIVE break
        /// broadcast) so it fires only when a prop breaks in front of you -- NOT on the join-sync of props broken
        /// before you arrived. The field isn't a Node, so it spawns via the (tree-parented) mesh's scene.</summary>
        public void PlayBreakEffect(int index)
        {
            if (index < 0 || index >= _recs.Length) return;
            var r = _recs[index];
            if (r == null || r.Meshes == null || r.Meshes.Length == 0) return;
            var mesh = r.Meshes[0];
            if (mesh == null || !GodotObject.IsInstanceValid(mesh)) return;
            var tree = mesh.GetTree();
            var scene = tree?.CurrentScene;
            if (scene == null) return;

            // prop bounds -> burst centre + radius (drives count/scale); clamp so a huge billboard or a tiny sign both read
            var aabb = mesh.Mesh?.GetAabb() ?? new Aabb(Vector3.Zero, Vector3.One);
            Vector3 gscale = mesh.GlobalTransform.Basis.Scale;
            Vector3 worldSize = (aabb.Size * gscale).Abs();
            float radius = Mathf.Clamp(worldSize.Length() * 0.35f, 0.5f, 6f);
            // emit debris/dust ACROSS the prop's whole volume (a box matching its footprint), so a big barn crumbles
            // over its 15 m footprint instead of puffing at one point, while a small sign stays tight.
            Vector3 halfExt = new Vector3(Mathf.Clamp(worldSize.X * 0.5f, 0.2f, 8f), Mathf.Clamp(worldSize.Y * 0.5f, 0.2f, 8f), Mathf.Clamp(worldSize.Z * 0.5f, 0.2f, 8f));
            Vector3 centre = mesh.GlobalTransform * (aabb.Position + aabb.Size * 0.5f);

            var propMat = mesh.MaterialOverride as StandardMaterial3D;

            // Every break kicks up a visible DUST poof. Retail rubble ALSO drops the section mesh as physics debris +
            // plays a bigger FINALE effect (InteractableObjectRubble.updateRubble) -- the raw Rubble_Effect is a sparse
            // 8-16 chip burst, too easy to miss on its own; the dust gives the "it came apart" read on every break.
            SpawnDust(scene, tree, centre, halfExt, radius, propMat != null ? propMat.AlbedoColor : new Color(0.62f, 0.58f, 0.52f));

            // the prop's ACTUAL retail Rubble_Effect debris chips on TOP of the dust, if we extracted it
            if (RubbleFx.TryGet(r.EffectId, out var fx) && fx.Tex != null)
            {
                var fmat = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                    AlbedoColor = Colors.White, AlbedoTexture = fx.Tex,
                };
                if (fx.HFrames > 1) { fmat.ParticlesAnimHFrames = fx.HFrames; fmat.ParticlesAnimVFrames = 1; fmat.ParticlesAnimLoop = false; }
                var ps = new CpuParticles3D
                {
                    Emitting = true, OneShot = true, Amount = Mathf.Max(1, fx.Count), Lifetime = Mathf.Max(0.3f, fx.LifeMax),
                    Explosiveness = 1f, LifetimeRandomness = fx.LifeMax > fx.LifeMin ? 0.3f : 0f, Direction = Vector3.Up,
                    Spread = fx.Shape == "cone" ? Mathf.Clamp(fx.ConeAngle, 5f, 90f) : (fx.Shape == "sphere" ? 180f : 45f),
                    InitialVelocityMin = fx.SpeedMin, InitialVelocityMax = fx.SpeedMax,
                    Gravity = new Vector3(0f, -9.8f * fx.Gravity, 0f),
                    ScaleAmountMin = fx.SizeMin, ScaleAmountMax = fx.SizeMax,
                    Mesh = new QuadMesh { Size = Vector2.One, Material = fmat },
                };
                if (fx.Shape == "box") { ps.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box; ps.EmissionBoxExtents = halfExt; }
                else if (fx.Shape == "sphere") { ps.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere; ps.EmissionSphereRadius = Mathf.Max(0.1f, fx.Radius); }
                // cone -> the default Point emission + Direction/Spread above (Godot has no cone shape)
                if (fx.Tumble) { ps.AngleMin = -180f; ps.AngleMax = 180f; ps.AngularVelocityMin = -300f; ps.AngularVelocityMax = 300f; }
                if (fx.HFrames > 1) { ps.AnimOffsetMin = 0f; ps.AnimOffsetMax = 1f; }   // random flipbook chip per particle
                if (fx.Shrink) { var c = new Curve(); c.AddPoint(new Vector2(0f, 1f)); c.AddPoint(new Vector2(1f, 0f)); ps.ScaleAmountCurve = c; }
                scene.AddChild(ps);
                ps.GlobalPosition = centre;
                var tr = tree.CreateTimer(fx.LifeMax + 0.6f);
                tr.Timeout += () => { if (GodotObject.IsInstanceValid(ps)) ps.QueueFree(); };
                return;
            }

            // FALLBACK (effect id 0 / no extracted sprite): generic tumbling debris cubes wearing the prop's own material
            // (the dust above already fired). Falls under gravity, ~1.6 s.
            Material debrisMat = propMat ?? new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.5f, 0.44f) };
            int n = Mathf.Clamp(Mathf.RoundToInt(radius * 14f), 12, 48);
            var debris = new CpuParticles3D
            {
                Emitting = true, OneShot = true, Amount = n, Lifetime = 1.6f, Explosiveness = 1f, Randomness = 0.4f,
                Direction = Vector3.Up, Spread = 90f, InitialVelocityMin = 1.5f, InitialVelocityMax = 4.5f,
                Gravity = new Vector3(0f, -9.8f, 0f),
                ScaleAmountMin = radius * 0.07f, ScaleAmountMax = radius * 0.18f,
                AngleMin = -180f, AngleMax = 180f, AngularVelocityMin = -420f, AngularVelocityMax = 420f,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = halfExt,
                Mesh = new BoxMesh { Size = Vector3.One, Material = debrisMat },
            };
            scene.AddChild(debris);
            debris.GlobalPosition = centre;
            var t = tree.CreateTimer(2.4);
            t.Timeout += () => { if (GodotObject.IsInstanceValid(debris)) debris.QueueFree(); };
        }

        /// <summary>A soft dust poof (veh_smoke sprite -- mipmapped so dense particles don't sample black, the vehicle-
        /// smoke bug), sized to the prop + tinted toward its albedo. Fired on EVERY break so a break always reads, with
        /// the real Rubble_Effect chips (or generic debris) layered on top.</summary>
        static void SpawnDust(Node scene, SceneTree tree, Vector3 centre, Vector3 halfExt, float radius, Color tint)
        {
            var dustMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, VertexColorUseAsAlbedo = true,
                AlbedoColor = new Color(Mathf.Lerp(tint.R, 0.85f, 0.65f), Mathf.Lerp(tint.G, 0.82f, 0.65f), Mathf.Lerp(tint.B, 0.76f, 0.65f), 0.55f),
            };
            string sp = ProjectSettings.GlobalizePath("res://content/veh_smoke_1.png");   // the LIGHTER smoke sprite -> reads as dust
            if (File.Exists(sp)) { var simg = Image.LoadFromFile(sp); if (simg != null) { simg.GenerateMipmaps(); dustMat.AlbedoTexture = ImageTexture.CreateFromImage(simg); } }
            var dust = new CpuParticles3D
            {
                Emitting = true, OneShot = true, Amount = Mathf.Clamp(Mathf.RoundToInt(radius * 8f), 8, 24), Lifetime = 1.15f, Explosiveness = 0.85f, Randomness = 0.5f,
                Direction = Vector3.Up, Spread = 70f, InitialVelocityMin = 0.4f, InitialVelocityMax = 1.4f,
                Gravity = new Vector3(0f, 0.3f, 0f), ScaleAmountMin = radius * 0.5f, ScaleAmountMax = radius * 1.1f,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = halfExt,
                Mesh = new QuadMesh { Size = Vector2.One, Material = dustMat },
            };
            scene.AddChild(dust);
            dust.GlobalPosition = centre + Vector3.Up * radius * 0.3f;
            var t = tree.CreateTimer(2.4);
            t.Timeout += () => { if (GodotObject.IsInstanceValid(dust)) dust.QueueFree(); };
        }

        // ---- catalog: guid -> rubble scalars, parsed from content/objects/rubble.txt ----
        // one line: "<guid> <health> <reset> <mode> <effectId> <ndrops> <dropId>..." (tools/extract_rubble.py)

        public readonly struct Rubble
        {
            public readonly float Health;
            public readonly long ResetTicks;   // Rubble_Reset seconds x 50 Hz
            public readonly int EffectId;      // Rubble_Effect id -> the retail break VFX (RubbleFx catalog)
            public Rubble(float health, long resetTicks, int effectId) { Health = health; ResetTicks = resetTicks; EffectId = effectId; }
        }

        public static Dictionary<string, Rubble> LoadCatalog()
        {
            var map = new Dictionary<string, Rubble>();
            string path = ProjectSettings.GlobalizePath("res://content/objects/rubble.txt");
            if (!File.Exists(path)) { GD.Print("[rubble] no rubble.txt -- destructibles disabled"); return map; }
            foreach (var line in File.ReadAllLines(path))
            {
                var sp = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (sp.Length < 3) continue;
                if (!float.TryParse(sp[1], System.Globalization.CultureInfo.InvariantCulture, out float health)) continue;
                if (!float.TryParse(sp[2], System.Globalization.CultureInfo.InvariantCulture, out float resetSecs)) continue;
                int effectId = sp.Length > 4 && int.TryParse(sp[4], out int e) ? e : 0;   // col 4 = Rubble_Effect id (mode is col 3)
                map[sp[0].ToLowerInvariant()] = new Rubble(health, (long)System.Math.Round(resetSecs * 50f), effectId);
            }
            GD.Print($"[rubble] catalog {map.Count} destructible object types");
            return map;
        }
    }
}
