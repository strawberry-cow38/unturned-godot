using Godot;

namespace UnturnedGodot
{
    // How a world is being assembled (MP_PLAN §4 Phase 3 / §5 item 8: ONE world assembly for SP, client
    // and dedicated server, or the three modes drift forever).
    //   Aerial    = --objects: the placed world viewed from a survey camera (no player, no colliders).
    //   Playable  = --peidrive / menu "Drive PEI": full world + local player + streamers + HUD.
    //   Dedicated = headless server world: terrain/objects/roads/trees WITH colliders + nav, plus the
    //               authoritative population (loot + zombies + vehicles, streamed/published to clients --
    //               C4), but no camera, no HUD, no viewmodel, no local player, no loading UI (§2.1 fork A(b)).
    //   Client    = joined MP client (PEI_CLIENT_PLAN §2.1): the world as scenery + physics -- terrain +
    //               objects + colliders + roads/foliage/trees + day-night, but NO local player, NO camera,
    //               NO local-authority spawns (zombies/loot/animals/vehicles/jeep/crops) and NO navmesh
    //               (puppets don't path); authoritative state arrives as replicas rendered by client views.
    public enum WorldMode { Aerial, Playable, Dedicated, Client, Editor }   // Editor = colliders (mode != Aerial) for object picking, but NO player (mode != Playable) -- the map editor's free-fly world

    // A3 (SP/MP-unify): a world FIXTURE recorded by the object placement loop (a Circuit_0 grid-power source;
    // A2 will record Gas_Pump_0 here too). NOT spawned inline -- recording keeps worldgen byte-identical; the
    // caller decides how to realize it: the dedicated/consuming-loopback SERVER ServerPlaces it into the
    // deployable graph (replicated + client-materialized), while pure-direct SP Attaches it as a local node.
    public struct FixtureRecord
    {
        public ushort DefId;        // the DeployableDef.Id (GridSource = 9200, GasPump = 9201)
        public Vector3 Pos;         // world position of the drawn mesh (the ServerPlace / Attach point)
        public float YawDegrees;    // object yaw (ServerPlace/Materialize rebuild a plain-yaw basis from this)
        public Basis Basis;         // the FULL placement basis (yaw*pitch*roll*scale) -- the direct Attach path uses it for a byte-identical node transform
        public int StationId;       // A2 (GasPump only): the shared fuel-station id (StationFuel.StationIdFor(pos)); 0/unused for other fixtures
    }

    public sealed class WorldBuildResult
    {
        public SimDriver Sim;              // the 50 Hz sim spine (SimRoot host), present in every world
        public Terrain Terr;               // null if the map couldn't load (no local Unturned install)
        public PlayerController Player;    // Playable/PeiPlay only
        public ZombieField Zombies;        // Playable/Dedicated (and only when zombies are enabled; C4 populated the server)
        public DayNightCycle DayNight;     // the world clock -- MP Phase 8 syncs read/drive it (§3.7)
        public ResourceField Resources;    // trees/rocks -- MP Phase 8's alive-bitmap indexes into it (§3.7)
        public DestructibleField Destructibles;   // destructible props (rubble) -- the DestructibleReplication(16) alive-bitmap indexes into it
        public DirectionalLight3D Sun;     // world sun + env (C3: the client session LinkWorldLightings its late-spawned shell)
        public Godot.Environment Env;
        public Vector3 VehicleAim;         // first static service vehicle, for the legacy demo cam
        public bool HasVehicleAim;
        public Vector3 PlayerSpawn;        // Client only: a real Spawns/Players.dat point (terrain-height Y) -- pre-join reference (the C3 shell spawns at the SERVER-adopted entity pos, not here)
        public bool HasPlayerSpawn;
        public bool Ready;                 // true once the whole build finished (the old _worldReady)
        // convert-on-load containers: the placement loop FLAGS them here (skipping the decoration mesh); the caller
        // spawns the real StoreShelves AFTER the build, once the asset DB is loaded -- else the roll's tryAddItem
        // can't size the items + the containers come out EMPTY (looked stocked, weren't). (mesh,table,display,label,pos,yaw)
        public System.Collections.Generic.List<(string mesh, int table, bool display, string label, Vector3 pos, float yaw)> Containers = new();
        // A3: world power fixtures (Circuit_0 grid sources) recorded in EVERY mode, spawned by the caller (see
        // FixtureRecord). Recorded, never inline-spawned, so the mesh/collider draw stays byte-identical to worldgen.
        public System.Collections.Generic.List<FixtureRecord> Fixtures = new();
        /// <summary>P3 holiday parity (Client mode only, else null): the holiday-gated world content --
        /// the ~285 tagged props WITH their colliders, and the whole resource field -- is DEFERRED at
        /// build time and placed by this callback with the SERVER's activeHoliday (from the wire-v6
        /// Accept), so a joining client can never build a different static collision set than the
        /// server because its wall clock sits across a holiday boundary. One-shot. Resources defer
        /// WHOLE (not tagged-only): the §3.7 alive-bitmap index space is manifest-ordered, and a late
        /// tagged append would misalign every index against the server's interleaved order.</summary>
        public System.Action<string> ApplyHoliday;
    }

    // The one real-world assembly path, extracted verbatim from Main.BuildObjectsTest/BuildPeiPlay so
    // server / client / SP build the SAME world (MP_PLAN §4 Phase 3). Main still owns flag parsing and
    // the capture/demo scripting; this owns the nodes.
    public static class WorldBuilder
    {
        // Map-load loot distribution (master's "loot distribution on shelves"): certain PLACED props become lootable
        // CONTAINERS in singleplayer instead of plain decoration -- a StoreShelf spawns at the placement transform and
        // the decoration mesh is skipped. Registry = object guid -> PEI item table. First pass: the Shelf_1 store shelf
        // (24 placed in the map's shops). Extend the map as more prop->container mappings are dialed in.
        static readonly System.Collections.Generic.Dictionary<string, (string mesh, int table, bool display, string label)> ContainerShelf = new()
        {
            // OPEN-tier shelves -> loot SHOWN on the tiers
            ["3d37d6da42a34f19b6b6a25e3ab8eaab"] = ("Shelf_1", 6, true, "Store Shelf"),   // store gondola x24 -> Food
            ["c2ea9b50d4d640438800b9ff553ec627"] = ("Shelf_0", 21, true, "Shelf"),        // wood/metal shelf x46 -> Civilian Canada
            // SOLID-front props -> plain F-open containers (loot inside, not shown)
            ["f463c0c6285544ac86845d98a07d73a9"] = ("Shelf_2", 21, false, "Bookcase"),    // bookcase x62 -> Civilian Canada
            ["91dbbf923c8c401bb6b2d56084783f73"] = ("Fridge_0", 6, false, "Fridge"),      // fridge x17 -> Food
            ["8388edfa33b84f78ad7f5d277412433b"] = ("Wardrobe_0", 19, false, "Wardrobe"), // wardrobe x24 -> Cloth
            ["050dbe869b1c4fd5b215c552d145effd"] = ("Counter_0", 17, false, "Counter"),   // counter x103 -> Kitchen
            ["0aeeefaf364f46f9906aff76c40c6d2b"] = ("Counter_1", 17, false, "Counter"),   // counter x22 -> Kitchen
            ["02923364713c4385a2bdaa7221d717ae"] = ("Counter_2", 17, false, "Counter"),   // counter x23 -> Kitchen
            // business/industrial containers (crates + shipping containers) -> prime in-genre loot
            ["cb0d8bf87fca47e3b73f634959a9f523"] = ("Crate_0", 8, false, "Crate"),         // business crate x31 -> Construction
            ["054a9392fed9484e950ff92d13631f06"] = ("Crate_3", 8, false, "Crate"),         // business crate x20 -> Construction
            ["aa7fd5bc5e634a0c846770a28ff6cd65"] = ("Container_0", 2, false, "Container"), // shipping container x15 -> Military Canada
            ["d010fb4c4feb4f8fab81f1a5695617ba"] = ("Container_1", 2, false, "Container"), // shipping container x20 -> Military Canada
            ["4f2b60ecae204b899eec282891a79e05"] = ("Container_2", 2, false, "Container"), // shipping container x19 -> Military Canada
        };

        // MP (A1): the DISTINCT container kinds (mesh/display/label), sorted deterministically, so ContainerSchema can
        // assign each a stable KindId that the server + client agree on WITHOUT re-running the world build (the client
        // never spawns the SP StoreShelf nodes -- it materializes fixtures from the replica by KindId).
        public static System.Collections.Generic.List<(string mesh, bool display, string label)> ContainerKinds()
        {
            var seen = new System.Collections.Generic.List<(string mesh, bool display, string label)>();
            foreach (var kv in ContainerShelf)
            {
                var k = (kv.Value.mesh, kv.Value.display, kv.Value.label);
                if (!seen.Contains(k)) seen.Add(k);
            }
            seen.Sort((a, b) => string.CompareOrdinal(a.mesh, b.mesh));
            return seen;
        }

        // The full placed world (terrain + Objects.dat + spawns). syncLoad skips every frame-yield so the
        // whole build runs synchronously inside one _Ready (the --bakenav/--navpathtest/--zombietest tools
        // and the dedicated server use this); bakeNav additionally re-bakes + saves the canonical navmesh.
        public static async System.Threading.Tasks.Task<WorldBuildResult> BuildFullWorld(
            Node root, WorldMode mode, string mapRoot, string mapPlace,
            bool noZombies, bool syncLoad, bool bakeNav, string activeHoliday)
        {
            var result = new WorldBuildResult();
            // The sim spine (SimRoot/SimDriver) now exists in every world: gameplay systems migrate onto it
            // per-phase as their authority split lands (MP_PLAN §2.5); replication registers LAST.
            var sim = new SimDriver();
            root.AddChild(sim);
            result.Sim = sim;

            float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            // Phased async load with a progress screen + per-category timing (master). Phase(name) records the PREVIOUS
            // phase's elapsed ms, advances the bar, sets the label, and yields a frame so the overlay actually paints
            // before the next (blocking) chunk of work runs. (Dedicated: no loading UI -- there is nobody watching.)
            LoadingScreen loading = null;
            if (mode != WorldMode.Dedicated) { loading = new LoadingScreen(); root.AddChild(loading); loading.SetTotal(mode == WorldMode.Client ? 5 : 11); }   // Client: Terrain/Objects/Roads/Foliage/Trees
            // Guarantee the overlay is actually PRESENTED before any blocking asset load. A single
            // process_frame resumes mid-frame (before the draw), so the first + heaviest phase (Terrain)
            // would otherwise block on the previous frame and the loading screen would never show. Wait
            // for a real drawn frame (FramePostDraw fires after present) so the map-choice hands straight
            // to a visible loading screen. (--bakenav loads fully synchronously -> no yields.)
            if (!syncLoad)
            {
                loading?.SetStatus("Loading…");
                await root.ToSignal(root.GetTree(), SceneTree.SignalName.ProcessFrame);
                await root.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
            var timings = new System.Collections.Generic.Dictionary<string, double>();
            string curPhase = null; var phaseSw = System.Diagnostics.Stopwatch.StartNew();
            async System.Threading.Tasks.Task Phase(string name)
            {
                if (curPhase != null) { timings[curPhase] = phaseSw.Elapsed.TotalMilliseconds; loading?.Advance(); }
                curPhase = name; loading?.SetStatus(name + "…"); phaseSw.Restart();
                // Wait for a real DRAWN frame (not just process_frame, which resumes before the present) so
                // each bar/status update is actually visible before this phase's blocking work runs.
                // --bakenav: skip the frame-yield so the WHOLE world loads synchronously -> we can bake offline.
                if (!syncLoad)
                {
                    await root.ToSignal(root.GetTree(), SceneTree.SignalName.ProcessFrame);
                    await root.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                }
            }
            // REAL PEI lighting via DayNightCycle (src Lighting.dat: ported sky shader + warm ambient + sun per time-of-day)
            // -- replaces the ProceduralSky + sky-tinted ambient that didn't match the source palette. "Drive PEI"
            // (--peidrive) is the mode master actually plays, so THIS is the one that has to carry the src-accurate lighting.
            var env = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color };
            root.AddChild(new WorldEnvironment { Environment = env });
            // dedicated fx hygiene (§2.1/§5): the headless server keeps the CLOCK (day-night time is
            // authoritative state now, §3.7) but skips shadow maps + the per-frame sky/fog/glow work
            var sun = new DirectionalLight3D { LightEnergy = 1.2f, ShadowEnabled = mode != WorldMode.Dedicated };
            root.AddChild(sun);
            var dayNight = new DayNightCycle { Sun = sun, Env = env, DayLength = 300f, VisualsEnabled = mode != WorldMode.Dedicated };
            root.AddChild(dayNight);
            result.DayNight = dayNight;
            result.Sun = sun;
            result.Env = env;
            await Phase("Terrain");
            var terr = Terrain.LoadMapMerged(mapRoot + "/Landscape/Heightmaps", withCollider: true);
            if (terr == null)
            {
                // no local Unturned install -> LoadMapMerged logged the UG_UNTURNED_DIR hint; bail the world-build cleanly.
                // A dedicated server still boots: flat fallback ground (dev boxes/CI without retail map data), no objects/nav.
                if (mode != WorldMode.Dedicated) return result;
                var ground = new StaticBody3D { CollisionLayer = 1u << 0 };
                ground.AddChild(new CollisionShape3D { Shape = new WorldBoundaryShape3D() });
                root.AddChild(ground);
                GD.Print("[WORLD] dedicated: no map data (set UG_UNTURNED_DIR) -> flat fallback ground, no objects/nav");
                result.Ready = true;
                return result;
            }
            root.AddChild(terr);
            result.Terr = terr;
            await Phase("Objects");

            bool colliders = mode != WorldMode.Aerial;   // walkable collision for the player (Playable) and the server sim (Dedicated)
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var g2m = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var line in System.IO.File.ReadLines(dir + "guid_mesh.txt"))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2) g2m[p[0]] = p[1];
            }
            var cache = new System.Collections.Generic.Dictionary<string, ArrayMesh>();
            var folCache = new System.Collections.Generic.Dictionary<string, ArrayMesh>();   // separate tree-leaf meshes (own leaf material)
            var shapeCache = new System.Collections.Generic.Dictionary<string, ConcavePolygonShape3D>();   // one trimesh collider per unique prop mesh, shared across instances
            var matCache = new System.Collections.Generic.Dictionary<string, StandardMaterial3D>();
            StandardMaterial3D MatFor(string nm)
            {
                if (matCache.TryGetValue(nm, out var mm)) return mm;
                if (nm.StartsWith("Glass"))   // Glass_0/Glass_1 have NO albedo texture (src uses a shader-based transparent material) -> the brown fallback made them opaque. Give glass a proper see-through look.
                {
                    mm = new StandardMaterial3D
                    {
                        AlbedoColor = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // light blue-grey, mostly see-through
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        Metallic = 0f, Roughness = 0.06f,                       // smooth + glossy like glass
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    };
                    matCache[nm] = mm;
                    return mm;
                }
                // VertexColorUseAsAlbedo: objects are white by default (texture unchanged), but billboards bake their ad-geometry
                // palette into per-vertex colour so multi-material signs keep their real colours over the merged mesh (master).
                mm = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true };
                string tp = dir + nm + "_tex.png";
                if (System.IO.File.Exists(tp))
                {
                    var img = new Image();
                    if (img.Load(tp) == Error.Ok)
                    {
                        // leaf/foliage cutout: if the albedo carries real transparency (>1% of texels), alpha-scissor it
                        if (img.GetFormat() == Image.Format.Rgba8)
                        {
                            var data = img.GetData(); int tr = 0;
                            for (int i = 3; i < data.Length; i += 4) if (data[i] < 200) tr++;
                            if (tr > data.Length / 400) { mm.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor; mm.AlphaScissorThreshold = 0.5f; }
                        }
                        // Tiny PALETTE textures (billboards etc. use a 2x2/4x2 colour-key sampled by the mesh UVs): Linear
                        // filtering + mipmaps average the palette cells together -> the thin logo/text geometry minifies to
                        // black at distance (same class as the gun black-texture bug). Nearest + no mipmaps keeps each cell crisp.
                        bool palette = img.GetWidth() <= 16 && img.GetHeight() <= 16;
                        if (!palette) img.GenerateMipmaps();
                        mm.AlbedoTexture = ImageTexture.CreateFromImage(img);
                        // master: the whole world is Nearest (crisp Unturned look); only grass+flowers are bilinear (FoliageField).
                        // palette textures skip mipmaps too (else the 2x2 cells average to black at distance).
                        mm.TextureFilter = palette ? BaseMaterial3D.TextureFilterEnum.Nearest : BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
                    }
                    else mm.AlbedoColor = new Color(0.60f, 0.55f, 0.47f);
                }
                else mm.AlbedoColor = new Color(0.60f, 0.55f, 0.47f);
                matCache[nm] = mm;
                return mm;
            }
            var cellCount = new System.Collections.Generic.Dictionary<Vector2I, int>();
            var cellSum = new System.Collections.Generic.Dictionary<Vector2I, Vector3>();
            Vector2I bestCell = Vector2I.Zero; int bestN = 0; int placed = 0;
            // holiday gate: PEI's Objects.dat has ~285 Christmas/Halloween props placed that only show in-season
            // (src: ObjectAsset.holidayRestriction + HolidayUtil schedule). Skip any whose holiday != the active one.
            var holidayOf = new System.Collections.Generic.Dictionary<string, string>();
            string hpath = dir + "holiday_props.txt";
            if (System.IO.File.Exists(hpath))
                foreach (var hl in System.IO.File.ReadLines(hpath)) { var q = hl.Split(' ', System.StringSplitOptions.RemoveEmptyEntries); if (q.Length >= 2) holidayOf[q[0]] = q[1]; }
            int holidaySkipped = 0;
            // P3 (Client mode): holiday-tagged placements are NOT decided by this machine's clock -- they
            // defer, and result.ApplyHoliday places the ones matching the SERVER's holiday at join time
            // (the same PlaceObject body, so parity with an inline build is by construction).
            var deferredHoliday = mode == WorldMode.Client ? new System.Collections.Generic.List<(string[] p, string name, string holiday, int destIndex)>() : null;
            // DESTRUCTIBLE PROPS (rubble): a placed object whose GUID is in the rubble catalog gets a
            // deterministic index in placements.txt SCAN order (assigned below, before the holiday/container
            // branch, so server + client agree even though the client defers holiday props). The DestructibleField
            // binds each built one's nodes; the reserved index space (destN) is content-hash-matched across peers.
            var rubbleCat = DestructibleField.LoadCatalog();
            var destField = new DestructibleField();
            int destN = 0;
            void PlaceObject(string[] p, string name, int destIndex)
            {
                if (!cache.TryGetValue(name, out var mesh)) { mesh = ObjMesh.Load(dir + name + ".obj"); cache[name] = mesh; }
                if (mesh == null) return;
                float px = F(p[1]), py = F(p[2]), pz = F(p[3]), ex = F(p[4]), ey = F(p[5]), ez = F(p[6]), sx = F(p[7]), sy = F(p[8]), sz = F(p[9]);
                var gpos = new Vector3(px, py, -pz);
                // negate-Z LAYOUT (keeps the map orientation) with a RAW mesh (un-mirrored geometry): rotation = old C_z-conjugated euler
                // raw (un-mirrored) mesh convention: pitch is +ex, NOT -ex. The -ex was left over from the old negate-Z-verts
                // convention -> it inverted the pitch, flipping tilted props (e.g. the lighthouse, ex=270) upside-down into the
                // ground. +ex matches Unity's rotation for the raw mesh; yaw = 180-ey: the raw mesh in the negate-Z layout faces 180 off (only visible on asymmetric props like town buildings, hidden on the symmetric lighthouse), so +180 corrects every prop's facing.
                // ROLL = -ez: the raw-mesh frame flips the roll sign (same as it flips pitch), so ez!=0 props (e.g. the
                // Alberton bank clocks) came out ~180 off. Negating ez faces them right. ez=0 props are UNCHANGED (roll
                // term is identity), so the whole map except the handful of rolled props is byte-identical -- no regression.
                var rot = new Basis(new Vector3(0, 1, 0), Mathf.DegToRad(180f - ey)) * new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(ex)) * new Basis(new Vector3(0, 0, 1), Mathf.DegToRad(-ez));
                var basis = rot.Scaled(new Vector3(sx, sy, sz));
                var mainMi = new MeshInstance3D { Mesh = mesh, MaterialOverride = MatFor(name), Transform = new Transform3D(basis, gpos),
                    VisibilityRangeEnd = 320f, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled };   // individual props already frustum-cull behind the player; add a distance cutoff (master)
                root.AddChild(mainMi);
                // tree foliage: a SEPARATE leaf mesh with its own leaf material (so the trunk keeps its bark texture)
                if (!folCache.TryGetValue(name, out var fmesh))
                {
                    string fp = dir + name + "_foliage.obj";
                    fmesh = System.IO.File.Exists(fp) ? ObjMesh.Load(fp) : null;
                    folCache[name] = fmesh;
                }
                MeshInstance3D folMi = null;
                if (fmesh != null) { folMi = new MeshInstance3D { Mesh = fmesh, MaterialOverride = MatFor(name + "_foliage"), Transform = new Transform3D(basis, gpos),
                    VisibilityRangeEnd = 240f, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled };   // leaves cull closer
                    root.AddChild(folMi); }
                // gas pumps (A2): every Gas_Pump_0 is a 750W-consumer fuel PUMP over a shared station tank. RECORD
                // it in EVERY mode (the mesh + collider below stay byte-identical); the caller realizes it -- the
                // dedicated / consuming-loopback server ServerPlaces it into the deployable graph (so it rides
                // SystemDeployables and the client's DeployableReplicaView materializes it, with a gaspump-meta
                // interaction collider of its own), while pure-direct SP Attaches a local node + adds that collider
                // (WorldBuilder.SpawnFixturesDirect). stationId derived identically via StationFuel.StationIdFor.
                if (name == "Gas_Pump_0")
                    result.Fixtures.Add(new FixtureRecord { DefId = DeployableDef.GasPump.Id, Pos = gpos, YawDegrees = 180f - ey, Basis = basis, StationId = StationFuel.StationIdFor(gpos) });
                // grid power (A3): every Circuit_0 breaker box is a 10kW mains SOURCE. RECORD it in EVERY mode
                // (the mesh + collider above stay byte-identical); the caller realizes it -- the dedicated /
                // consuming-loopback server ServerPlaces it into the deployable graph so it rides SystemDeployables
                // replication and the client's DeployableReplicaView materializes it, while pure-direct SP Attaches
                // a local node (WorldBuilder.SpawnFixturesDirect). yaw = 180-ey, the object's placement yaw.
                if (name == "Circuit_0")
                    result.Fixtures.Add(new FixtureRecord { DefId = DeployableDef.GridSource.Id, Pos = gpos, YawDegrees = 180f - ey, Basis = basis });
                // WATER TOWER fluid IO (strawberry): each Tower_Water_0 becomes an infinite (tainted) water SOURCE you can
                // hose from -- attached SP-local in Playable (MP replication of map fluid fixtures = fast-follow, like the
                // rest of the fluid system). It rides this prop's mesh; just adds an output spigot at the base. One shared
                // FluidManager ticks them (created lazily, deduped by the group check).
                if (name == "Tower_Water_0" && mode == WorldMode.Playable)
                {
                    var tower = WaterTowerSource.Make();
                    tower.Position = gpos;
                    tower.RotationDegrees = new Vector3(0f, 180f - ey, 0f);
                    root.AddChild(tower);
                    if (tower.GetTree() != null && tower.GetTree().GetNodesInGroup("fluid_managers").Count == 0) root.AddChild(new FluidManager());
                }
                StaticBody3D destBody = null;
                if (colliders)   // walkable collision: trimesh of the VISUAL mesh (trees collide on the trunk only; the separate leaf mesh has no collider, so you walk through foliage)
                {
                    if (!shapeCache.TryGetValue(name, out var shp)) { shp = mesh.CreateTrimeshShape(); shapeCache[name] = shp; }
                    if (shp != null)
                    {
                        // Only LARGE opaque structures (buildings, gated by scaled mesh size) block the item LOS raycast; every small prop
                        // + all glass/alpha-cutout goes on the see-through layer 6 so the raycast passes through (master). Player collides with both.
                        var ab = mesh.GetAabb();
                        float maxDim = Mathf.Max(ab.Size.X * sx, Mathf.Max(ab.Size.Y * sy, ab.Size.Z * sz));
                        bool losBlocker = maxDim >= 5f && MatFor(name).Transparency == BaseMaterial3D.TransparencyEnum.Disabled;
                        var body = new StaticBody3D { Transform = new Transform3D(basis, gpos), CollisionLayer = losBlocker ? 1u << 0 : 1u << 6 };
                        body.SetMeta(PlayerController.SurfMeta, (int)(fmesh != null ? PlayerController.Surf.Wood : PlayerController.Surf.Concrete));   // trees (have foliage) = wood impacts; buildings/props = concrete
                        // A2: the gas pump's interaction collider is now the fixture node's OWN gaspump-meta box
                        // (GasPump.AddInteractionCollider), not this world-mesh collider -- so no tag here.
                        body.AddChild(new CollisionShape3D { Shape = shp });
                        root.AddChild(body);
                        destBody = body;   // the collider a server bullet/melee ray tags for destructible damage
                    }
                }
                // destructible prop: bind this placement's live nodes to its deterministic index + tag the
                // collider so the server hit resolution (GodotWorldRay) can route damage to it. Needs a collider
                // (nothing to shoot otherwise); a no-collider mode just leaves the slot reserved+indestructible.
                if (destIndex >= 0 && destBody != null && rubbleCat.TryGetValue(p[0].ToLowerInvariant(), out var rub))
                {
                    destBody.SetMeta(DestructibleField.MetaKey, destIndex);
                    var mis = folMi != null ? new[] { mainMi, folMi } : new[] { mainMi };
                    destField.Register(destIndex, destBody, mis, rub.Health, rub.ResetTicks, rub.EffectId);
                }
                placed++;
                var cell = new Vector2I(Mathf.FloorToInt(px / 96f), Mathf.FloorToInt(pz / 96f));
                cellCount.TryGetValue(cell, out int cc); cellCount[cell] = cc + 1;
                cellSum.TryGetValue(cell, out Vector3 cs); cellSum[cell] = cs + gpos;
                if (cc + 1 > bestN) { bestN = cc + 1; bestCell = cell; }
            }
            // SP loot distribution: a registered prop spawns as a lootable StoreShelf here (at the placement transform)
            // instead of the decoration mesh. Only in Playable -- the editor shows decoration; the client is server-driven.
            int converted = 0;
            bool TryContainer(string[] q)
            {
                if (mode != WorldMode.Playable || !ContainerShelf.TryGetValue(q[0], out var cfg)) return false;
                // FLAG it (skip the decoration mesh) -> the caller spawns the real container post-build (asset DB ready).
                result.Containers.Add((cfg.mesh, cfg.table, cfg.display, cfg.label, new Vector3(F(q[1]), F(q[2]), -F(q[3])), 180f - F(q[5])));   // ex=270/ez=0 upright -> yaw only
                converted++;
                return true;
            }
            StationFuel.Reset();   // fresh shared station tanks for this world build (before any gas pumps attach)
            foreach (var line in System.IO.File.ReadLines(dir + mapPlace))
            {
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 10 || !g2m.TryGetValue(p[0], out var name)) continue;
                // reserve the destructible index HERE (before the holiday/container branch) so every peer assigns
                // the same index to the same placement regardless of holiday deferral -- the wire id must agree.
                int destIdx = rubbleCat.ContainsKey(p[0].ToLowerInvariant()) ? destN++ : -1;
                if (holidayOf.TryGetValue(p[0], out var ph))
                {
                    if (deferredHoliday != null) { deferredHoliday.Add((p, name, ph, destIdx)); continue; }   // P3: the SERVER's holiday decides, at join (ApplyHoliday)
                    if (ph != activeHoliday) { holidaySkipped++; continue; }                          // out-of-season holiday prop (index stays reserved+unbuilt)
                }
                if (TryContainer(p)) continue;   // registered map prop -> lootable container (SP), skip the decoration mesh (no destructible overlap)
                PlaceObject(p, name, destIdx);
            }
            destField.SetCount(destN);   // reserve the whole deterministic index space (built + unbuilt holiday slots)
            result.Destructibles = destField;
            if (destN > 0) GD.Print($"[rubble] {destField.BuiltCount} destructible props wired ({destN} reserved, {destField.InstanceCount} slots)");
            if (converted > 0) GD.Print($"[containers] flagged {converted} map props for post-build container spawn");
            var focus = placed > 0 ? cellSum[bestCell] / bestN : Vector3.Zero;
            GD.Print($"[OBJECTS] placed {placed} objects ({cache.Count} meshes); densest cluster {bestN} near {focus}; holiday-gated {holidaySkipped}{(deferredHoliday != null ? $", deferred {deferredHoliday.Count} to the join handshake" : "")} (active={activeHoliday})");

            // Player spawn points: LevelSpawns.PlayerSpawns (C2 promoted the C1 local parse to a shared static
            // so the dedicated server's SpawnProvider reads the SAME points -- behavior-identical here).
            // ROADS + FOLIAGE + TREES -- ONE extraction shared verbatim by Playable and Client (PEI_CLIENT_PLAN §3
            // C1: call-site-identical for Playable -- same order, same params, same RNG -- so SP stays byte-identical).
            async System.Threading.Tasks.Task BuildRoadsFoliageTrees()
            {
                // ROAD SPLINES: Environment/Paths.dat bezier road network (separate from the road props) -> extruded strips.
                {
                    await Phase("Roads");
                    var rf = new RoadField { Terr = terr };
                    rf.LoadFromEnvironment(mapRoot + "/Environment");
                    root.AddChild(rf);
                }
                // FOLIAGE: PEI's baked Foliage.blob grass (asset 1, 612K instances) as one MultiMesh
                {
                    await Phase("Foliage");
                    var ff = new FoliageField();
                    root.AddChild(ff);
                    ff.LoadGrass();
                }
                // RESOURCES: Terrain/Trees.dat -> trees/bushes/ore-rocks/mushrooms (1694 spawns, 26 types) as MultiMeshes
                {
                    await Phase("Trees");
                    var rsf = new ResourceField();
                    root.AddChild(rsf);
                    // P3 (Client): the WHOLE load waits for the server's holiday -- the §3.7 alive-bitmap
                    // index space is manifest-ordered, so it must be built in ONE pass with the same
                    // holiday the server used, or every replicated fell/respawn index lands on the wrong
                    // tree. Pre-join there is no camera, so nobody sees the empty field.
                    if (mode != WorldMode.Client)
                        rsf.LoadResources(activeHoliday);   // gate CHRISTMAS resources (candy canes/snow piles) like the objects
                    result.Resources = rsf;
                }
            }

            // VEHICLE SPAWNS -- ONE extraction shared verbatim by Playable and Dedicated (PEI_CLIENT_PLAN §3
            // C4: call-site-identical for Playable -- same order, same params, same variant math -- so SP
            // stays byte-identical; the dedicated server calls it too and VehicleNetSync publishes the nodes).
            // Spawns/Vehicles.dat (source LevelVehicles River: u8 ver, [SteamID if 1<v<3], u8 tableCount,
            // per table [color 3B, name str, tableID u16 if v>3, u8 tierCount, per tier: name str, chance f32, u8 spawnCount, per spawn u16],
            // u16 pointCount, per point: u8 type, Vector3, u8 angle*2). type = table index: 0 Civilian, 1 Police, 2 Fire, 3 Military,
            // 4 Medic, 5 Farm, 6-11 air/water/tank. LAND (0-5): Civilian=car pool, Police/Fire/Medic=static mesh, Military=humvee, Farm=jeep stand-in.
            async System.Threading.Tasks.Task SpawnPeiVehicles()
            {
                await Phase("Vehicles");
                string vpath = mapRoot + "/Spawns/Vehicles.dat";
                int nv = 0;
                if (System.IO.File.Exists(vpath))
                {
                    var vd = System.IO.File.ReadAllBytes(vpath); int vp = 0;
                    byte U8() => vd[vp++];
                    ushort U16() { var v = System.BitConverter.ToUInt16(vd, vp); vp += 2; return v; }
                    float F32() { var v = System.BitConverter.ToSingle(vd, vp); vp += 4; return v; }
                    void RStr() { int n = U8(); vp += n; }
                    byte ver = U8();
                    if (ver > 1 && ver < 3) vp += 8;   // SteamID
                    byte tcount = U8();
                    for (int t = 0; t < tcount; t++)
                    {
                        vp += 3; RStr();                 // color + table name
                        if (ver > 3) vp += 2;            // tableID
                        byte tiers = U8();
                        for (int ti = 0; ti < tiers; ti++) { RStr(); vp += 4; byte sc = U8(); vp += sc * 2; }
                    }
                    ushort pcount = U16();
                    for (int i = 0; i < pcount; i++)
                    {
                        byte type = U8();
                        float px = F32(); vp += 4; float pz = F32();   // point.x, skip point.y, point.z
                        float ang = U8() * 2f;
                        if (type > 5) continue;          // skip air/boat/tank (6-11) until those models exist
                        float gz = -pz;                  // Unity Z -> port negate-Z
                        var vpos = new Vector3(px, terr.SampleHeight(px, gz) + 1.2f, gz);
                        var vyaw = new Basis(Vector3.Up, Mathf.DegToRad(-ang));   // spawn yaw (negate for negate-Z)
                        string vn = null;   // all PEI service vehicles (Police / Fire / Medic) are drivable now, no static meshes
                        if (vn != null)   // real static vehicle mesh (Police / Firetruck / Ambulance) + collider
                        {
                            if (!cache.TryGetValue(vn, out var vm)) { vm = ObjMesh.Load(dir + vn + ".obj"); cache[vn] = vm; }
                            if (vm != null)
                            {
                                var rpos = new Vector3(px, terr.SampleHeight(px, gz) - vm.GetAabb().Position.Y, gz);   // sit the mesh's bottom on the terrain
                                root.AddChild(new MeshInstance3D { Mesh = vm, MaterialOverride = MatFor(vn), Transform = new Transform3D(vyaw, rpos) });
                                if (!shapeCache.TryGetValue(vn, out var vs)) { vs = vm.CreateTrimeshShape(); shapeCache[vn] = vs; }
                                if (vs != null) { var vb = new StaticBody3D { Transform = new Transform3D(vyaw, rpos) }; vb.AddChild(new CollisionShape3D { Shape = vs }); root.AddChild(vb); }
                                if (!result.HasVehicleAim) { result.VehicleAim = rpos; result.HasVehicleAim = true; }
                                nv++;
                            }
                        }
                        else   // drivable: Civilian -> real civilian-car pool, Military -> humvee, Farm -> jeep stand-in (no tractor mesh yet)
                        {
                            vn = type switch   // reuse the outer vn (null here); the static-mesh branch above handled Police/Fire/Medic
                            {
                                0 => (i % 6) switch { 0 => "sedan", 1 => "hatchback", 2 => "roadster", 3 => "offroader", 4 => "truck", _ => "van" },   // Civilian rolls the civilian car pool (golf is command-only, excluded)
                                1 => "police",                                                              // Police
                                2 => "firetruck",                                                           // Fire
                                3 => (i % 3) switch { 0 => "humvee", 1 => "jeep", _ => "ural" },            // Military_Canada: humvee + jeep + ural truck, all forest
                                4 => "ambulance",                                                           // Medic -> drivable ambulance
                                5 => "tractor",                                                             // Farm -> drivable tractor
                                _ => "quad",                                                                // fallback
                            };
                            var veh = Vehicle.BuildByName(vn, i);   // variant=i -> deterministic paint variety per spawn point
                            root.AddChild(veh);
                            veh.GlobalPosition = vpos;
                            veh.RotationDegrees = new Vector3(0f, -ang, 0f);
                            nv++;
                        }
                    }
                }
                GD.Print($"[vehicles] spawned {nv} PEI vehicles (Civilian=sedan/hatchback/roadster/offroader/truck/van, Military=humvee/jeep/ural, Farm=tractor; golf command-only; air/water/tank skipped)");
            }

            if (mode == WorldMode.Playable)
            {
                await Phase("Player");
                // menu "Drive PEI": drop the player + jeep on open grass with REAL controls (WASD + mouse look, F to enter/drive the jeep)
                float sx = 0f, sz = -350f, spawnYaw = 0f;
                // player spawn: a random regular Spawns/Players.dat point (shared parse, source LevelPlayers.getSpawn).
                // Falls back to the inland-grass scan if the file's missing/empty.
                bool gotSpawn = false;
                {
                    var regs = LevelSpawns.PlayerSpawns(mapRoot);
                    if (regs.Count > 0) { var pick = regs[new RandomNumberGenerator { Seed = 7 }.RandiRange(0, regs.Count - 1)]; sx = pick.x; sz = pick.z; spawnYaw = pick.yaw; gotSpawn = true; }
                }
                if (!gotSpawn)   // fallback: most-inland grass
                {
                    int bestMargin = -1; float bestDist = float.MaxValue;
                    for (float cz = -1800f; cz <= 1800f; cz += 50f)
                        for (float cx = -1800f; cx <= 1800f; cx += 50f)
                        {
                            if (terr.SampleDominantLayer(cx, cz) != 2) continue;
                            int margin = 0;
                            for (int r = 32; r <= 160; r += 32)
                            {
                                bool ring = true;
                                for (int a = 0; a < 8 && ring; a++)
                                    if (Terrain.IsWater(terr.SampleDominantLayer(cx + r * Mathf.Cos(a * Mathf.Pi / 4f), cz + r * Mathf.Sin(a * Mathf.Pi / 4f)))) ring = false;
                                if (!ring) break;
                                margin = r;
                            }
                            float dist = Mathf.Sqrt(cx * cx + cz * cz);
                            if (margin > bestMargin || (margin == bestMargin && dist < bestDist)) { bestMargin = margin; bestDist = dist; sx = cx; sz = cz; }
                        }
                }
                if (System.Environment.GetEnvironmentVariable("UG_TOWNSPAWN") == "1") { sx = focus.X; sz = focus.Z; }   // demo: spawn in the busiest town (near zombies) instead of open grass
                if (System.Environment.GetEnvironmentVariable("UG_LHSPAWN") == "1") { sx = 247.452f; sz = -792.643f; }   // demo: spawn at the PEI lighthouse (prop-orientation check)
                { var _ox = System.Environment.GetEnvironmentVariable("UG_SPAWNX"); var _oz = System.Environment.GetEnvironmentVariable("UG_SPAWNZ");   // spawn at arbitrary godot XZ (e.g. a named location node) for town orbits
                  if (_ox != null && float.TryParse(_ox, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _px)) sx = _px;
                  if (_oz != null && float.TryParse(_oz, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var _pz)) sz = _pz; }
                CharacterModel.LoadBundled();
                var player = new PlayerController { CaptureMouse = true };
                root.AddChild(player);
                player.EquipUnarmed();   // spawn UNARMED (bare fists) -- pick items up to equip them (strawberry)
                result.Player = player;   // UG_AUTOFIRE terrain-impact verification
                player.LinkWorldLighting(sun, env);   // FP gun takes the world day/night sun + ambient -- was NEVER called in Drive PEI, so the gun ignored time-of-day (master saw "not applying at all")
                player.GlobalPosition = new Vector3(sx, terr.SampleHeight(sx, sz) + 3f, sz);
                player.RotationDegrees = new Vector3(0f, spawnYaw, 0f);   // face the spawn point's angle
                player.Spawn = player.GlobalPosition;   // respawn on this above-ground point, NOT the default (0,1,0) which is underground on PEI
                if (System.Environment.GetEnvironmentVariable("UG_OOBTEST") == "1") player.GlobalPosition = new Vector3(sx, -2000f, sz);   // test hook: drop below the map -> should trip the OOB kill
                AttachPlayerShell(root, player, withCropManager: true);   // console/map/HUD/hitmarkers/pause/profiler/attachments -- the C3 shared shell block (same nodes, same order)
                var jeep = Vehicle.BuildByName("jeep");
                root.AddChild(jeep);
                jeep.GlobalPosition = new Vector3(sx + 2.2f, terr.SampleHeight(sx + 2.2f, sz) + 1.5f, sz);

                // ZOMBIE SPAWNS: PEI's REAL zombie spawn points (Spawns/Animals.dat = 1456 points; legacy filename that
                // LevelZombies reads), region-streamed around the player like Unturned's region loader -- see ZombieField.
                // Replaces the old Environment/Bounds.dat navmesh approximation (52 zombies) with the map's actual horde design.
                if (!noZombies)   // "Drive PEI — No Zombies" menu button / --nozombies flag
                {
                    await Phase("Zombies");
                    var zf = new ZombieField { Player = player, Terr = terr };
                    zf.LoadFromPei(mapRoot);
                    root.AddChild(zf);
                    result.Zombies = zf;   // --zombietest reads this at frame 25 to verify spawns land on the navmesh
                }

                // VEHICLE SPAWNS: Spawns/Vehicles.dat -- the shared extraction above (identical order/params/variants)
                await SpawnPeiVehicles();
                // LOOT: PEI's 2470 item spawn points (Spawns/Jars.dat), region/distance-streamed around the player (LootField).
                {
                    await Phase("Loot");
                    var loot = new LootField { Player = player, Terr = terr };
                    loot.LoadFromPei(mapRoot);
                    root.AddChild(loot);
                }
                // WILDLIFE: Spawns/Fauna.dat animal points (deer/pig/cow), streamed as rigged RiggedCharacters (AnimalField).
                {
                    await Phase("Animals");
                    var animals = new AnimalField { Player = player, Terr = terr };
                    animals.LoadFromPei(mapRoot);
                    root.AddChild(animals);
                }
                await BuildRoadsFoliageTrees();   // roads/foliage/trees -- the shared extraction above (identical order/params)
                if (System.Environment.GetEnvironmentVariable("UG_ZAERIAL") == "1")   // demo cam: look down on the spawn town so the zombies are visible
                {
                    var acam = new Camera3D { Current = true, Fov = 62f, Far = 20000f };
                    root.AddChild(acam);
                    var ctr = result.HasVehicleAim ? result.VehicleAim : player.GlobalPosition;   // prefer a real vehicle; else the spawn town
                    acam.Position = ctr + (result.HasVehicleAim ? new Vector3(0f, 9f, 11f) : new Vector3(0f, 50f, 44f));
                    acam.LookAt(ctr, Vector3.Up);
                }
                if (System.Environment.GetEnvironmentVariable("UG_LHSPAWN") == "1")   // demo cam: frame the lighthouse (prop-orientation check)
                {
                    var lcam = new Camera3D { Current = true, Fov = 55f, Far = 20000f };
                    root.AddChild(lcam);
                    var lb = player.GlobalPosition;   // spawned at the lighthouse base
                    if (System.Environment.GetEnvironmentVariable("UG_ORBIT") == "1")   // orbit the prop (showcase video) instead of a static frame
                        root.AddChild(new OrbitCam { Cam = lcam, Center = lb });   // radius/height/center-lift via UG_ORBITR/UG_ORBITH/UG_ORBITCY
                    else
                    {
                        lcam.Position = lb + new Vector3(46f, 30f, 46f);
                        lcam.LookAt(lb + new Vector3(0f, 22f, 0f), Vector3.Up);
                    }
                }
                root.GetWindow().Mode = Window.ModeEnum.Maximized;
                GD.Print($"[PEI] playable: spawned on grass ({sx:0},{sz:0}); WASD move, E enter jeep, drive PEI");
            }
            else if (mode == WorldMode.Dedicated)
            {
                // Server world content (MP_PLAN §2.1 fork A(b)): everything with collision the sim needs --
                // roads + tree trunks -- but no player/camera/HUD/viewmodel. AnimalField still keys spawning
                // on the LOCAL PlayerController and stays out until its streamer generalizes (C4 generalized
                // ZombieField + LootField to PlayerRegistry -- both live here now).
                // FoliageField (visual-only grass, 612K instances) is skipped as dedicated fx hygiene (§5).
                {
                    await Phase("Roads");
                    var rf = new RoadField { Terr = terr };
                    rf.LoadFromEnvironment(mapRoot + "/Environment");
                    root.AddChild(rf);
                }
                {
                    await Phase("Trees");
                    // fx hygiene (§5): the server needs trunk colliders + the deterministic instance
                    // index space for the §3.7 alive-bitmap, not 1694 rendered MultiMesh instances
                    var rsf = new ResourceField { VisualInstances = false };
                    root.AddChild(rsf);
                    rsf.LoadResources(activeHoliday);
                    result.Resources = rsf;
                }
                // LOOT (Phase 6, §3.3): the rolls run server-side now that LootField keys spawn/despawn on
                // ANY player's proximity via PlayerRegistry (no local player exists here). The catalog must
                // load first -- nothing else registers items on a headless boot. WorldItemNetSync
                // (DedicatedServer) publishes the streamed nodes as replicated world-item entities.
                {
                    await Phase("Loot");
                    SDG.Unturned.ItemCatalog.RegisterAll();
                    var loot = new LootField { Terr = terr };
                    loot.LoadFromPei(mapRoot);
                    root.AddChild(loot);
                }
                // ZOMBIES (C4, §3): the SAME pocket-streamed ZombieField as Playable, with NO Player wired --
                // streaming keys on every registered player (the C2 avatar bodies) via PlayerRegistry, and
                // each spawned brain's null Target falls back to PlayerRegistry.Nearest (ZombieController's
                // §3.5 generalization). Same noZombies gate as Playable (--nozombies / UG_DEDICATED_NOZOMBIES).
                if (!noZombies)
                {
                    await Phase("Zombies");
                    var zf = new ZombieField { Terr = terr };
                    zf.LoadFromPei(mapRoot);
                    root.AddChild(zf);
                    result.Zombies = zf;
                }
                // WILDLIFE (A5 follow-up 2026-07-20): the same Fauna-streamed AnimalField as Playable, NO Player wired --
                // streams on every registered player via PlayerRegistry (the C4 generalization now extended to
                // AnimalField, like ZombieField/LootField). Rig-less server-side (the agent wanders + AnimalNetSync
                // publishes it; remote clients render the puppets), so a joined client gets deer/pig/cow around it.
                {
                    await Phase("Animals");
                    var animals = new AnimalField { Terr = terr };
                    animals.LoadFromPei(mapRoot);
                    root.AddChild(animals);
                }
                // VEHICLES (C4, §3): the shared Spawns/Vehicles.dat pass, after ItemCatalog.RegisterAll (the
                // Loot block above). VehicleNetSync publishes every "vehicles"-group node this spawns -- the
                // net layer needs nothing else.
                await SpawnPeiVehicles();
            }
            else if (mode == WorldMode.Client)
            {
                // Joined-client world (PEI_CLIENT_PLAN §3 Phase C1): everything deterministic-from-files loads
                // locally (terrain/objects/colliders above + roads/foliage/trees + the day-night visuals);
                // everything authoritative (players/zombies/vehicles/loot/items) arrives as replicas rendered
                // by the client views. No local player, no camera (Main.BuildClient owns the C1 overhead cam),
                // no local-authority spawns -- and the aerial else-branch below must NOT fire here.
                await BuildRoadsFoliageTrees();
                var regs = LevelSpawns.PlayerSpawns(mapRoot);
                if (regs.Count > 0)
                {
                    var pick = regs[new RandomNumberGenerator { Seed = 7 }.RandiRange(0, regs.Count - 1)];
                    result.PlayerSpawn = new Vector3(pick.x, terr.SampleHeight(pick.x, pick.z), pick.z);
                    result.HasPlayerSpawn = true;
                }
                // P3: the deferred holiday content lands here, with the SERVER's holiday from the Accept
                // (ClientWorldSession.ApplyServerHoliday -> this). One-shot; the placement body is the
                // same PlaceObject the inline build runs, so prop/collider parity is by construction.
                bool holidayApplied = false;
                var rsfDeferred = result.Resources;
                result.ApplyHoliday = holiday =>
                {
                    if (holidayApplied) return;
                    holidayApplied = true;
                    int before = placed;
                    if (deferredHoliday != null)
                        foreach (var (p, name, ph, destIdx) in deferredHoliday)
                            if (ph == holiday) PlaceObject(p, name, destIdx);
                    if (rsfDeferred != null && GodotObject.IsInstanceValid(rsfDeferred)) rsfDeferred.LoadResources(holiday);
                    GD.Print($"[WORLD] client holiday content applied: {holiday} ({placed - before} props of {deferredHoliday?.Count ?? 0} deferred, + resources)");
                };
            }
            else
            {
                // aerial over the busiest cluster so the full populated PEI (all ~360 types) reads at once, no gaps
                Vector3 sumAll = Vector3.Zero; foreach (var v in cellSum.Values) sumAll += v;
                var ctr = placed > 0 ? sumAll / placed : Vector3.Zero;
                var cam = new Camera3D { Current = true, Fov = 55f, Far = 20000f };
                root.AddChild(cam);
                cam.Position = new Vector3(ctr.X, 2200f, ctr.Z + 1f);
                cam.LookAt(new Vector3(ctr.X, 0f, ctr.Z), new Vector3(0f, 0f, -1f));   // straight down, screen-up = world -Z (north), to match the game chart
            }
            NearestFilter.Apply(root);   // Unturned point-filters level/object textures (FilterMode.Point) -- match it scene-wide (crisp pixel look)
            if (curPhase != null) { timings[curPhase] = phaseSw.Elapsed.TotalMilliseconds; loading?.Advance(); }   // record the final phase
            loading?.Finish(timings);   // hide the overlay + show the per-category timing breakdown top-left for a few seconds (master)
            // Zombie navmesh POCKETS -- bake NOW, in the FULL world, so the BUILDINGS (layer 1<<0) carve the mesh and
            // zombies route around them. This full-world bake is the CANONICAL one (save:true -> pei_pocket_N.res);
            // the terrain-only peiplay/navshot verify modes pass save:false so they never overwrite it.
            if (mode != WorldMode.Client && (!noZombies || bakeNav))   // client puppets don't path; noZombies -> nothing consumes the navmesh (only zombie AI does) so skip the pocket load+sync entirely; bakeNav still forces it (offline bake tool) -- pure load-time savings
                try { var _navPk = ZombieNav.LoadPockets(mapRoot); ZombieNav.BuildOrLoad(root, _navPk, overlay: false, save: bakeNav, bakeIfMissing: bakeNav); } catch (System.Exception _ne) { GD.PrintErr($"[zombienav] full-world nav failed: {_ne.Message}"); }   // --bakenav BAKES+SAVES here; the game just LOADS the committed .res
            result.Ready = true;   // async world fully built (terrain..trees) -> the --shot harness can now capture a loaded frame
            return result;
        }

        // The player-shell block (PEI_CLIENT_PLAN §3 C3): everything a HUMAN-driven PlayerController needs
        // around it -- dev console, map, HUD, FPS counter, hitmarkers, pause menu, profiler, attachment
        // menu -- extracted verbatim from the Playable branch (extract-and-call: same nodes, same order,
        // same wiring) so the joined-client session attaches the SAME shell around its predicted player.
        // withCropManager: Playable owns local-authority crop growth; on a joined client the SERVER owns
        // growth (crops arrive as replicas), so the session passes false.
        public static void AttachPlayerShell(Node root, PlayerController player, bool withCropManager)
        {
            root.AddChild(new DevConsole { Player = player });   // F1 dev console: give <item> / vehicle <name> / plant <crop> spawns at the look-orb (master)
            if (withCropManager) root.AddChild(new CropManager());   // farm crop growth ticking + plant/harvest (console `plant`, F to harvest)
            root.AddChild(new MapUI { Player = player });         // M: full-screen PEI map (town nodes + player pos/facing)
            { var hud = new HUD { Player = player }; root.AddChild(hud); player.Hud = hud; }
            root.AddChild(new FpsCounter());   // top-right yellow FPS counter (master 2026-07-11)
            { var hmL = new CanvasLayer { Layer = 98 }; hmL.AddChild(new HitmarkerHUD()); root.AddChild(hmL); }   // hit / headshot markers (master)
            { var pause = new PauseMenu(); root.AddChild(pause); player.PauseMenu = pause; }               // ESC menu (parity with BuildPlayable)
            root.AddChild(new Profiler());   // F3 perf overlay (parity)
            { var attach = new AttachmentMenu(); root.AddChild(attach); player.AttachMenu = attach; }       // T weapon-attachment menu -- was never wired in PEI drive, so T did nothing (broken since PEI map)
        }

        // A3 (SP/MP-unify): realize the recorded world fixtures as DIRECT local nodes -- the pure-direct SP path
        // (Main.AttachMpLoopback when no loopback attaches, and MpLoopback when NOT consuming). Byte-identical to
        // the old inline Circuit_0 Attach: same pos + full basis + PortLocal, NetId 0 (SP-local wiring). Under the
        // consuming loopback / dedicated server the fixtures are ServerPlaced instead, so this is NEVER called there
        // (the DeployableReplicaView is the sole node source -- no double-spawn).
        public static void SpawnFixturesDirect(Node root, System.Collections.Generic.IEnumerable<FixtureRecord> fixtures)
        {
            if (fixtures == null) return;
            foreach (var f in fixtures)
            {
                var def = DeployableDef.ById(f.DefId);
                if (def == null) continue;
                if (def.Fixture == Net.FixtureKind.GridSource)
                {
                    // A3: pure-direct SP -- a local grid source (NetId 0), plus its own gridpower-meta interaction
                    // collider so the look-ray can focus + wire it (the world mesh's collider is never tagged).
                    var g = GridPowerSource.Attach(root, f.Pos, f.Basis, GridPowerSource.PortLocal);
                    g.AddInteractionCollider();
                }
                else if (def.Fixture == Net.FixtureKind.GasPump)
                {
                    // A2: pure-direct SP -- a local pump over its shared StationFuel tank (NetId 0), plus its own
                    // gaspump-meta interaction collider (the world mesh's collider is no longer tagged).
                    var gp = GasPump.Attach(root, f.Pos, f.Basis, GasPump.PortLocal, null, f.StationId);
                    gp.AddInteractionCollider();
                }
            }
        }

        // --peiplay: drop the player onto REAL PEI terrain (colliders on), spawned on land via SampleHeight, scripted to walk.
        public static WorldBuildResult BuildPeiPlayWorld(Node root, string mapRoot, bool horde)
        {
            var result = new WorldBuildResult();
            var sim = new SimDriver();
            root.AddChild(sim);
            result.Sim = sim;

            // REAL PEI lighting via DayNightCycle (src Lighting.dat: warm ambient + sky/sun per time-of-day). The old
            // hardcoded flat GREY env (0.6 grey @ 0.75) is what made everything dark + washed -- it never used the
            // lighting rework at all. The DayNightCycle drives Env (sky + warm ambient) + the sun each frame.
            var env = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color };
            root.AddChild(new WorldEnvironment { Environment = env });
            var sun = new DirectionalLight3D { LightEnergy = 1.2f, ShadowEnabled = true };
            root.AddChild(sun);
            var dayNight = new DayNightCycle { Sun = sun, Env = env, DayLength = 300f };
            root.AddChild(dayNight);
            result.DayNight = dayNight;

            var terr = Terrain.LoadMapMerged(mapRoot + "/Landscape/Heightmaps", withCollider: true);
            if (terr == null) return result;
            root.AddChild(terr);
            result.Terr = terr;

            // Zombie navmesh POCKETS (source LevelNavigation Flags): bake a Godot navmesh in each of PEI's 19 POI
            // pockets from the world collision (agent-radius wall buffer), saved + reused. (Phase 1 -- pathing wired next.)
            { var _pk = ZombieNav.LoadPockets(mapRoot); ZombieNav.BuildOrLoad(root, _pk, overlay: false, save: false); }   // peiplay is terrain-only -> don't save (loads the canonical full-world mesh if --peidrive baked it)

            CharacterModel.LoadBundled();
            var player = new PlayerController();
            player.LoadGun("res://content/eaglefire.dat");
            root.AddChild(player);
            player.LinkWorldLighting(sun, env);   // FP gun takes the world day/night sun + ambient (same missing hookup as Drive PEI)
            if (System.Environment.GetEnvironmentVariable("UG_HOLD") is string _hc && _hc.Length > 0)
            {
                SDG.Unturned.ItemCatalog.RegisterAll();
                var _ha = SDG.Unturned.Assets.find(ConsumableRegistry.IdForMesh(_hc));   // resolve a REAL asset so StartConsume works (UG_EAT)
                player.EquipHeldConsumable(_ha, _hc);   // render harness: hold a consumable (UG_HOLD=canned_beans [UG_EAT=1 to play the eat])
            }
            if (System.Environment.GetEnvironmentVariable("UG_TESTLIGHT") == "1")   // render harness: a bright red dynlight just in front-left of the player -> should spill onto the FP gun
            {
                var tl = new OmniLight3D { OmniRange = 6f, LightColor = new Color(1f, 0.1f, 0.1f), LightEnergy = 8f, ShadowEnabled = false };
                tl.AddToGroup("dynlight");
                player.AddChild(tl);
                tl.Position = new Vector3(1.1f, 0.1f, 0f);   // relative to the player: hard RIGHT -> gun should light on its right (outer) side if the transform is correct
            }
            root.AddChild(new CropManager());   // farm crop growth ticking + plant/harvest (console `plant`, F to harvest)
            // auto-pick a grassy, well-inland spawn so the jeep drives on real green PEI land, not the coastal water-splat
            float sx = 0f, sz = -350f; int bestMargin = -1; float bestDist = float.MaxValue;
            for (float cz = -1800f; cz <= 1800f; cz += 50f)
                for (float cx = -1800f; cx <= 1800f; cx += 50f)
                {
                    if (terr.SampleDominantLayer(cx, cz) != 2) continue;   // grass underfoot
                    int margin = 0;
                    for (int r = 32; r <= 160; r += 32)   // how far land extends in all 8 directions (driving room)
                    {
                        bool ring = true;
                        for (int a = 0; a < 8 && ring; a++)
                            if (Terrain.IsWater(terr.SampleDominantLayer(cx + r * Mathf.Cos(a * Mathf.Pi / 4f), cz + r * Mathf.Sin(a * Mathf.Pi / 4f)))) ring = false;
                        if (!ring) break;
                        margin = r;
                    }
                    float dist = Mathf.Sqrt(cx * cx + cz * cz);
                    if (margin > bestMargin || (margin == bestMargin && dist < bestDist)) { bestMargin = margin; bestDist = dist; sx = cx; sz = cz; }
                }
            float gy = terr.SampleHeight(sx, sz);
            player.GlobalPosition = new Vector3(sx, gy + 3f, sz);   // drop 3 m onto the real ground
            player.Spawn = player.GlobalPosition;   // respawn above ground, NOT the default (0,1,0) which is underground on PEI
            { var hud = new HUD { Player = player }; root.AddChild(hud); player.Hud = hud; }
            result.Player = player;
            root.AddChild(new DevConsole { Player = player });   // F1 dev console: give <item> / vehicle <name> spawns at the look-orb (master)
            root.AddChild(new MapUI { Player = player });         // M: full-screen PEI map (town nodes + player pos/facing)

            // a jeep right beside the player, dropped onto the terrain -> hop in + drive PEI
            var jeep = Vehicle.BuildByName("jeep");   // auto-joins the "vehicles" group in Build
            root.AddChild(jeep);                       // in the tree FIRST, else GlobalPosition no-ops (!is_inside_tree)
            float jjx = sx + 2.2f;
            jeep.GlobalPosition = new Vector3(jjx, terr.SampleHeight(jjx, sz) + 1.5f, sz);

            GD.Print($"[PEIPLAY] grass spawn ({sx:0},{sz:0}) groundY {gy:0}, inland-margin {bestMargin}m, layer {terr.SampleDominantLayer(sx, sz)} + jeep beside");

            if (horde)   // a zombie field the jeep drives into -> the loud engine aggros them (source 48*speed), roadkill + swarm on real PEI
            {
                const int N = 18;
                for (int i = 0; i < N; i++)
                {
                    float ang = i * 2.39996f;                          // golden-angle scatter -> even disc fill
                    float r = 9f + 26f * (i / (float)N);               // 9..35 m filled disc around the spawn (jeep plows through the forward slice)
                    float zx = sx + r * Mathf.Cos(ang), zz = sz + r * Mathf.Sin(ang);
                    var z = new ZombieController { Target = player, Speciality = ZombieController.ESpeciality.NORMAL };
                    root.AddChild(z);                                  // in the tree first, else GlobalPosition no-ops
                    z.GlobalPosition = new Vector3(zx, terr.SampleHeight(zx, zz) + 1.5f, zz);
                }
                GD.Print($"[PEIPLAY] +{N} zombies scattered around the jeep (loud drive aggros -> roadkill + swarm)");
            }
            result.Ready = true;
            return result;
        }
    }
}
