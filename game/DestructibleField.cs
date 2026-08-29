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
            // BATCHED props (PropBatcher) have no MeshInstance3D of their own -- their visual is a handful of
            // slots inside shared MultiMeshes. Breaking one hides its alive slots and REVEALS its dead ones, so
            // the debris costs no extra draw: it is another instance in a batch that is already being drawn.
            // Meshes and Slots are mutually exclusive; whichever is non-null is how this prop renders.
            public PropBatcher.Slot[] Slots;
            public PropBatcher.Slot[] DeadSlots;
            // ...and PlayBreakEffect sizes its burst off the prop's mesh node, which a batched prop does not
            // have, so the geometry it needs is carried here instead of being read back off a node.
            public Aabb BatchAabb;
            public Transform3D BatchXf;
            public StandardMaterial3D BatchMat;
            public uint BodyLayer;
            public float MaxHealth;           // 0 = unregistered slot (indestructible)
            public long ResetTicks;
            public int EffectId;              // Rubble_Effect id -> the retail break VFX
            public Mesh[] RagdollMeshes;      // retail Ragdoll pieces (content/objects/<name>_Ragdoll_N.obj) -- each becomes its own physics body on break
            public bool Alive = true;
            public System.Action<bool> OnAliveChanged;   // extra teardown/restore a prop needs beyond hiding meshes:
                                                        // a Street_Light_0 owns a SpotLight3D + glow cone that are NOT
                                                        // in Meshes (separate world-space node), so hiding meshes alone
                                                        // leaves a lit cone floating over the rubble.
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
        public void Register(int index, StaticBody3D body, MeshInstance3D[] meshes, float maxHealth, long resetTicks, int effectId = 0, System.Action<bool> onAliveChanged = null, Mesh[] ragdollMeshes = null)
        {
            if (index < 0) return;
            EnsureSize(index + 1);
            if (_recs[index] == null) BuiltCount++;
            _recs[index] = new Rec { Meshes = meshes, Body = body, BodyLayer = body?.CollisionLayer ?? 0u,
                                     MaxHealth = maxHealth, ResetTicks = resetTicks, EffectId = effectId,
                                     OnAliveChanged = onAliveChanged, RagdollMeshes = ragdollMeshes };
        }

        /// <summary>Bind a BATCHED destructible (PropBatcher) to its index. Same contract as Register -- the
        /// index space, health, reset clock and effect id are identical -- but the visual is MultiMesh slots
        /// rather than nodes, and an optional set of dead slots carries the debris that appears in its place.
        /// `localAabb`/`worldXf`/`mat` stand in for the mesh node PlayBreakEffect would otherwise measure.</summary>
        public void RegisterBatched(int index, StaticBody3D body, PropBatcher.Slot[] slots, PropBatcher.Slot[] deadSlots,
                                    Aabb localAabb, Transform3D worldXf, StandardMaterial3D mat,
                                    float maxHealth, long resetTicks, int effectId = 0, System.Action<bool> onAliveChanged = null, Mesh[] ragdollMeshes = null)
        {
            if (index < 0) return;
            EnsureSize(index + 1);
            if (_recs[index] == null) BuiltCount++;
            _recs[index] = new Rec { Slots = slots, DeadSlots = deadSlots, Body = body, BodyLayer = body?.CollisionLayer ?? 0u,
                                     BatchAabb = localAabb, BatchXf = worldXf, BatchMat = mat,
                                     MaxHealth = maxHealth, ResetTicks = resetTicks, EffectId = effectId,
                                     OnAliveChanged = onAliveChanged, RagdollMeshes = ragdollMeshes };
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
            // Batched: the intact instances leave their MultiMesh and the debris takes their place. Both sides
            // are driven off the SAME alive bit, so a rubble reset restores the prop and retires the debris.
            if (r.Slots != null) foreach (var s in r.Slots) s?.SetVisible(alive);
            if (r.DeadSlots != null) foreach (var s in r.DeadSlots) s?.SetVisible(!alive);
            if (r.Body != null && GodotObject.IsInstanceValid(r.Body)) r.Body.CollisionLayer = alive ? r.BodyLayer : 0u;
            r.OnAliveChanged?.Invoke(alive);   // lights/effects the prop owns outside Meshes (see the field's comment)
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
            if (r == null) return;
            // The burst is sized off the prop's geometry, which lives in one of two places: its own mesh node,
            // or -- for a BATCHED prop, which has no node -- the bounds/transform/material recorded at
            // registration, with its collider standing in as the tree anchor. Everything downstream works off
            // the four locals below, so the two representations diverge here and nowhere else.
            Aabb aabb; Transform3D xf; StandardMaterial3D propMat; Node anchor; Mesh dropMesh;
            if (r.Meshes != null && r.Meshes.Length > 0 && r.Meshes[0] != null && GodotObject.IsInstanceValid(r.Meshes[0]))
            {
                var mesh = r.Meshes[0];
                aabb = mesh.Mesh?.GetAabb() ?? new Aabb(Vector3.Zero, Vector3.One);
                xf = mesh.GlobalTransform;
                propMat = mesh.MaterialOverride as StandardMaterial3D;
                anchor = mesh;
                dropMesh = mesh.Mesh;   // the actual prop model, cloned as the falling physics body
            }
            else if (r.Slots != null && r.Body != null && GodotObject.IsInstanceValid(r.Body))
            {
                aabb = r.BatchAabb; xf = r.BatchXf; propMat = r.BatchMat; anchor = r.Body;
                dropMesh = (r.Slots.Length > 0 && r.Slots[0] != null) ? r.Slots[0].Mm?.Mesh : null;   // batched: the MultiMesh's shared prop mesh
            }
            else return;
            var tree = anchor.GetTree();
            var scene = tree?.CurrentScene;
            if (scene == null) return;

            // prop bounds -> burst centre + radius (drives count/scale); clamp so a huge billboard or a tiny sign both read
            Vector3 gscale = xf.Basis.Scale;
            Vector3 worldSize = (aabb.Size * gscale).Abs();
            float radius = Mathf.Clamp(worldSize.Length() * 0.35f, 0.5f, 6f);
            // emit debris/dust ACROSS the prop's whole volume (a box matching its footprint), so a big barn crumbles
            // over its 15 m footprint instead of puffing at one point, while a small sign stays tight.
            Vector3 halfExt = new Vector3(Mathf.Clamp(worldSize.X * 0.5f, 0.2f, 8f), Mathf.Clamp(worldSize.Y * 0.5f, 0.2f, 8f), Mathf.Clamp(worldSize.Z * 0.5f, 0.2f, 8f));
            Vector3 centre = xf * (aabb.Position + aabb.Size * 0.5f);

            // NO dust/smoke poof on break (master 2026-08-09: "remove the smoke particle which appears on every
            // destruction"). The retail Rubble_Effect chips (or fallback debris) below carry the break read on their own.

            // The prop's retail break SOUND (material-keyed, extracted alongside the VFX by tools/extract_rubble_sfx.py).
            // Keyed on the SAME effect id as the chips and fired here -- BEFORE the VFX branch -- so it plays on both the
            // chip path and the fallback-debris path, i.e. for any prop whose effect has a sound. 3D-positional at the
            // break point, one-shot, self-freeing (Finished signal, plus a 5 s backstop timer in case it never fires).
            if (RubbleSnd.TryGet(r.EffectId, out var brkSnd))
            {
                var ap = new AudioStreamPlayer3D
                {
                    Stream = brkSnd,
                    UnitSize = Mathf.Clamp(radius * 3f, 3f, 22f),   // full volume out to ~this range, so a barn carries further than a sign
                    MaxDb = 3f,
                };
                scene.AddChild(ap);
                ap.GlobalPosition = centre;
                ap.Finished += () => { if (GodotObject.IsInstanceValid(ap)) ap.QueueFree(); };
                var sndTimer = tree.CreateTimer(5.0);
                sndTimer.Timeout += () => { if (GodotObject.IsInstanceValid(ap)) ap.QueueFree(); };
                ap.Play();
            }

            // Retail-faithful "dropped physics prop": the broken prop drops a physics CLONE OF ITS OWN MODEL that
            // topples + falls + settles (master: "single clones of the models that fall", 2026-08-11). Retail
            // (InteractableObjectRubble.updateRubble) instantiates each section's authored Ragdoll mesh as a Rigidbody
            // with force + drag, despawned after 8s; we have no authored ragdoll meshes, so we clone the prop's own
            // model (the mesh we already hold at break time). Fired here -- like the SFX, BEFORE the chip/fallback
            // branch -- so it drops on every break regardless of which VFX path runs.
            // PHYSICS DEBRIS: the prop's real authored Ragdoll pieces, else a whole-model clone -- UNLESS the prop is
            // EXCLUDED (glass etc. shatter, no chunks). The retail sprite chips below STILL fire (master: the particles
            // are wanted). The "two 3d models" bug was NOT the sprites -- it was the extractor dumping the ragdoll's
            // LOD0 AND LOD1 as two overlapping bodies; fixed in tools/extract_debris_meshes.py (LOD0 only).
            if (!RagdollExcluded(r.EffectId))
            {
                if (r.RagdollMeshes != null && r.RagdollMeshes.Length > 0)
                    SpawnRagdollDrop(scene, tree, xf, r.RagdollMeshes, propMat);   // REAL authored pieces, each scattering
                else if (dropMesh != null)
                    SpawnModelDrop(scene, tree, xf, aabb, dropMesh, propMat);      // fallback: clone the whole model
            }

            // the prop's ACTUAL retail Rubble_Effect debris chips on TOP of the dust, if we extracted it
            if (RubbleFx.TryGet(r.EffectId, out var fx) && fx.Tex != null)
            {
                var fmat = new StandardMaterial3D
                {
                    // LIT (PerPixel) + matte, NOT Unshaded: debris chips should darken at night with the sun like everything
                    // else (VoX: they rendered identical day + night). Roughness 1 / no specular so the billboards don't glint.
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    Roughness = 1f, Metallic = 0f, SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
                    // NOT VertexColorUseAsAlbedo: CpuParticles3D leaves the per-particle color buffer at default, so tinting
                    // albedo by it is fragile -- take the sprite straight (AlbedoColor stays white).
                    AlbedoColor = Colors.White, AlbedoTexture = fx.Tex,
                };
                // Visibility tuning over the raw retail params: retail's 8-16 chips fly up at 5-10 m/s + vanish in ~1s, so
                // they zip out of view before you notice (retail leans on the mesh-debris + finale we don't model). Keep the
                // real sprite/shape but ~3x the count, slower, a gentle arc + a longer life so the chips linger near the break.
                var ps = new CpuParticles3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, 
                    Emitting = false, OneShot = true, Amount = ParticleFx.Amount(Mathf.Clamp(Mathf.RoundToInt(fx.Count * 1.5f), 8, 28)), Lifetime = Mathf.Max(1.1f, fx.LifeMax * 1.2f),   // Emitting fired AFTER positioning (below) -- true-in-ctor fires empty when a BULLET break routes here via NetDamageObject (a physics tick); same fix as ImpactFx
                    Explosiveness = 0.9f, Randomness = 0.5f, Direction = Vector3.Up,
                    Spread = fx.Shape == "cone" ? Mathf.Clamp(fx.ConeAngle * 1.4f, 35f, 90f) : (fx.Shape == "sphere" ? 180f : 60f),
                    InitialVelocityMin = fx.SpeedMin * 0.5f, InitialVelocityMax = fx.SpeedMax * 0.6f,
                    Gravity = new Vector3(0f, -7f * fx.Gravity, 0f),
                    // retail startSize reads oversized on our quads -> scale down so chips aren't big blocks; per-chip random
                    // ROLL + tumble (AngleMin/Max + AngularVelocity) so the billboards scatter instead of a uniform grid (VoX).
                    ScaleAmountMin = fx.SizeMin * 0.45f * ParticleFx.SizeScale, ScaleAmountMax = fx.SizeMax * 0.55f * ParticleFx.SizeScale,
                    AngleMin = -180f, AngleMax = 180f, AngularVelocityMin = -400f, AngularVelocityMax = 400f,
                    EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = halfExt,
                    Mesh = new QuadMesh { Size = Vector2.One, Material = fmat },
                };
                scene.AddChild(ps);
                ps.GlobalPosition = centre;
                // THE FIX: CpuParticles3D's auto-computed visibility AABB does NOT track the launched particles, so the
                // fast chips leave it within a frame and the whole system gets frustum-culled -> invisible (the "chips don't
                // render" bug). The slow dust never tripped it. Force a bound covering the emission box + the chips' arc.
                ps.VisibilityAabb = new Aabb(-halfExt - Vector3.One * 6f, (halfExt + Vector3.One * 6f) * 2f);
                ps.Emitting = true;   // fire the one-shot AFTER AddChild+position -> a clean cycle even when spawned inside a physics tick (a bullet break)
                var tr = tree.CreateTimer(ps.Lifetime + 0.6f);
                tr.Timeout += () => { if (GodotObject.IsInstanceValid(ps)) ps.QueueFree(); };
                return;
            }

            // FALLBACK (effect id 0 / no extracted sprite): generic tumbling debris cubes wearing the prop's own material
            // (no dust anymore -- master removed the break smoke). Falls under gravity, ~1.6 s.
            Material debrisMat = propMat ?? new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.5f, 0.44f) };
            int n = Mathf.Clamp(Mathf.RoundToInt(radius * 14f), 12, 48);
            var debris = new CpuParticles3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, 
                Emitting = false, OneShot = true, Amount = ParticleFx.Amount(n), Lifetime = 1.6f, Explosiveness = 1f, Randomness = 0.4f,   // fired AFTER positioning (below) -- physics-tick-safe (see the chip burst above)
                Direction = Vector3.Up, Spread = 90f, InitialVelocityMin = 1.5f, InitialVelocityMax = 4.5f,
                Gravity = new Vector3(0f, -9.8f, 0f),
                ScaleAmountMin = radius * 0.07f * ParticleFx.SizeScale, ScaleAmountMax = radius * 0.18f * ParticleFx.SizeScale,
                AngleMin = -180f, AngleMax = 180f, AngularVelocityMin = -420f, AngularVelocityMax = 420f,
                EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = halfExt,
                Mesh = new BoxMesh { Size = Vector3.One, Material = debrisMat },
            };
            scene.AddChild(debris);
            debris.GlobalPosition = centre;
            debris.VisibilityAabb = new Aabb(-halfExt - Vector3.One * 6f, (halfExt + Vector3.One * 6f) * 2f);   // same fast-particle cull guard as the chip path (review)
            debris.Emitting = true;   // fire the one-shot AFTER positioning (physics-tick-safe; see the chip burst)
            var t = tree.CreateTimer(2.4);
            t.Timeout += () => { if (GodotObject.IsInstanceValid(debris)) debris.QueueFree(); };
        }

        // ---- retail-faithful "dropped physics prop": clone the destroyed prop's own MODEL as a falling Rigidbody ----
        // Retail InteractableObjectRubble.updateRubble instantiates each dead section's authored Ragdoll mesh, adds a
        // Rigidbody (AddForce, drag 0.5 / angularDrag 0.1, gravity), globally caps it (RegisterDebris) + Destroy after
        // 8s. We never extracted per-prop ragdoll meshes -- but the prop's OWN model mesh is in hand at break time, and
        // master wants "single clones of the models that fall", so we clone THAT: one Rigidbody wearing the real mesh
        // (shared resource, no duplication) + a box collider round its AABB, dislodged with a gentle topple so it falls
        // + settles. Capped so break-spam can't pile up bodies; 8s despawn (persistent settle = drop the timer).
        static readonly List<RigidBody3D> _debris = new List<RigidBody3D>();
        const int DebrisCap = 48;

        // Props whose Rubble_Effect names a SHATTER/soft material don't leave physics chunks -- they break with their
        // VFX instead (glass shatters via GlassPane; ice/web/paper puff). EXCLUDED from the ragdoll/clone mesh drop
        // (master 2026-08-11: "a lot of props should be excluded from the ragdoll thing, ie glass"). Seeded with Glass;
        // extend once master gives the full material/prop set.
        static readonly HashSet<int> _noRagdollEffects = new HashSet<int> { 64 };   // 64 = Glass
        static bool RagdollExcluded(int effectId) => _noRagdollEffects.Contains(effectId);

        static void SpawnModelDrop(Node scene, SceneTree tree, Transform3D xf, Aabb localAabb, Mesh dropMesh, StandardMaterial3D propMat)
        {
            if (scene == null || tree == null || dropMesh == null) return;   // no readable mesh (rare batched case) -> skip the drop; VFX + SFX still play
            _debris.RemoveAll(d => !GodotObject.IsInstanceValid(d));
            while (_debris.Count >= DebrisCap)                               // cap: cull the oldest live drop when over budget
            {
                var oldest = _debris[0]; _debris.RemoveAt(0);
                if (GodotObject.IsInstanceValid(oldest)) oldest.QueueFree();
            }
            Vector3 scale = xf.Basis.Scale;
            Basis rot = xf.Basis.Orthonormalized();   // rotation WITHOUT scale -> an unscaled body (Godot dislikes scaled rigidbodies); scale is baked into the children instead
            var body = new RigidBody3D
            {
                Mass = 1f, GravityScale = 1f, LinearDamp = 0.5f, AngularDamp = 0.1f,   // retail drag 0.5 / angularDrag 0.1
                // layer 0 so nothing QUERIES the drop (bullet/interaction raycasts pass through it); mask world + props
                // so it still lands + settles on terrain/buildings, without being shot or shoving the player.
                CollisionLayer = 0u, CollisionMask = (1u << 0) | (1u << 6),
            };
            // the ACTUAL prop model, scaled to how it stood (shared Mesh resource -> no per-drop duplication)
            body.AddChild(new MeshInstance3D { Mesh = dropMesh, MaterialOverride = propMat, Scale = scale });
            // box collider around the mesh's own AABB (cheap + robust for "falls + settles"), offset to the AABB centre
            body.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = localAabb.Size * scale },
                Position = (localAabb.Position + localAabb.Size * 0.5f) * scale,
            });
            scene.AddChild(body);
            body.GlobalTransform = new Transform3D(rot, xf.Origin);   // start exactly where the prop stood
            body.ResetPhysicsInterpolation();                        // brand-new body: don't interpolate from world origin on frame 1 (streak guard, cf. WorldItem)
            // a gentle dislodge -> it topples, falls + settles ("fall to the ground", not a launch). tune here.
            body.LinearVelocity = new Vector3((float)GD.RandRange(-1.5, 1.5), 0.6f + (float)GD.RandRange(0.0, 1.2), (float)GD.RandRange(-1.5, 1.5));
            body.AngularVelocity = new Vector3((float)GD.RandRange(-2.5, 2.5), (float)GD.RandRange(-2.0, 2.0), (float)GD.RandRange(-2.5, 2.5));
            _debris.Add(body);
            var life = tree.CreateTimer(8.0);   // retail Destroy(obj, 8f). master OK'd 8s (2026-08-11); persistent settle = drop this timer
            life.Timeout += () => { if (GodotObject.IsInstanceValid(body)) body.QueueFree(); };
        }

        // Retail-faithful RAGDOLL: spawn each of the prop's authored Ragdoll pieces as its OWN physics body, so the
        // debris SCATTERS -- retail InteractableObjectRubble.updateRubble Instantiates each Ragdoll/Model_x separately +
        // adds a Rigidbody. The piece meshes are extracted in the prop's LOCAL space (tools/extract_debris_meshes.py),
        // so each renders at its authored spot within the prop; Godot derives each body's COM from its (offset) box
        // collider, so a piece tumbles about its own centre. Same drag / gravity / 8s / cap as the whole-model drop.
        static void SpawnRagdollDrop(Node scene, SceneTree tree, Transform3D xf, Mesh[] pieces, StandardMaterial3D propMat)
        {
            if (scene == null || tree == null || pieces == null) return;
            Vector3 scale = xf.Basis.Scale;
            Basis rot = xf.Basis.Orthonormalized();   // unscaled body; scale baked into the mesh child + collider (Godot dislikes scaled rigidbodies)
            foreach (var pm in pieces)
            {
                if (pm == null) continue;
                _debris.RemoveAll(d => !GodotObject.IsInstanceValid(d));
                while (_debris.Count >= DebrisCap)
                {
                    var oldest = _debris[0]; _debris.RemoveAt(0);
                    if (GodotObject.IsInstanceValid(oldest)) oldest.QueueFree();
                }
                Aabb la = pm.GetAabb();
                var body = new RigidBody3D
                {
                    Mass = 1f, GravityScale = 1f, LinearDamp = 0.5f, AngularDamp = 0.1f,
                    CollisionLayer = 0u, CollisionMask = (1u << 0) | (1u << 6),
                };
                body.AddChild(new MeshInstance3D { Mesh = pm, MaterialOverride = propMat, Scale = scale });
                body.AddChild(new CollisionShape3D
                {
                    Shape = new BoxShape3D { Size = la.Size * scale },
                    Position = (la.Position + la.Size * 0.5f) * scale,   // collider at the piece's own centre -> Godot puts the COM there
                });
                scene.AddChild(body);
                body.GlobalTransform = new Transform3D(rot, xf.Origin);   // prop-local piece meshes -> place the body at the prop's transform
                body.ResetPhysicsInterpolation();
                body.LinearVelocity = new Vector3((float)GD.RandRange(-1.5, 1.5), 1.0f + (float)GD.RandRange(0.0, 2.0), (float)GD.RandRange(-1.5, 1.5)) * 2f;
                body.AngularVelocity = new Vector3((float)GD.RandRange(-4.0, 4.0), (float)GD.RandRange(-4.0, 4.0), (float)GD.RandRange(-4.0, 4.0));
                _debris.Add(body);
                var life = tree.CreateTimer(8.0);
                life.Timeout += () => { if (GodotObject.IsInstanceValid(body)) body.QueueFree(); };
            }
        }

        // SpawnDust REMOVED 2026-08-09 (master: "remove the smoke particle which appears on every destruction").
        // The break now reads off the Rubble_Effect chips / fallback debris alone. History: a veh_smoke poof tinted to
        // the prop albedo, fired on every break for an "it came apart" puff. git has it if the read ever needs bulking up.

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
            ApplyOverrides(map);
            GD.Print($"[rubble] catalog {map.Count} destructible object types");
            return map;
        }

        /// <summary>Deliberate departures from the retail scalars, applied AFTER the file is parsed.
        ///
        /// They live in code rather than in rubble.txt because that file is generated wholesale from the retail
        /// bundles (tools/extract_rubble.py) -- a hand-edit there is correct right up until someone re-extracts,
        /// and then it is gone with nothing to show it ever existed.</summary>
        static void ApplyOverrides(Dictionary<string, Rubble> map)
        {
            foreach (var (guid, health) in HealthOverrides)
                if (map.TryGetValue(guid, out var r)) map[guid] = new Rubble(health, r.ResetTicks, r.EffectId);
        }

        // TELEVISIONS take a FEW shots (master: "a few to destroy the actual prop"). Retail ships both sets at 25 hp,
        // which is exactly one Eaglefire Object_Damage -- so a TV exploded into rubble on the first bullet and the
        // shot-out-screen state added alongside this had nowhere to live: you could never see it, because the shot
        // that broke the glass also finished the cabinet. 100 hp is four more rounds after the screen goes.
        static readonly (string Guid, float Health)[] HealthOverrides =
        {
            ("b489a8a63fca4d179fd315cbae8b6ed7", 100f),   // Television_0 (flatscreen)
            ("becc624cee1a4845808af80472ee9310", 100f),   // Television_1 (CRT)
            // The TOASTER takes TWO (strawberry). Retail ships it at 25 -- exactly one Eaglefire Object_Damage -- so it
            // burst on the first bullet and there was no surviving first shot for the bread to pop on. 50 is the second
            // round and not a third.
            ("2d1daa0412b94503aa57a5b422187d48", 50f),    // Toaster_0
        };
    }
}
