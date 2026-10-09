using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>
    /// THE INFINITE WORLD'S STREAMER (strawberry 2026-10-09: "throw together a prototype of very very large / infinite
    /// procgen maps, completely with the chunk streaming etc it needs").
    ///
    /// Three jobs, all of which have to hold at 10^7 m from where you started:
    ///
    /// 1. STREAM 256 m regions in square rings around the focus (the player), coarser with distance:
    ///      ring 0-2  LOD0  4 m grid  (the same spacing as retail PEI), ground collider within ring 1, trees to ring 3
    ///      ring 3-4  LOD1  8 m
    ///      ring 5-7  LOD2 16 m
    ///      ring 8-11 LOD3 32 m        -> ~2.9 km of terrain each way, 529 regions resident, regardless of world size
    ///    Generation (core InfiniteTerrain, a pure function of seed + absolute position) and mesh building run on
    ///    worker threads, nearest region first; the main thread only uploads, a few regions per frame. A coarse
    ///    region next to a fine one shares every vertex it has (InfiniteTerrainTests proves that), so the only crack
    ///    is between the fine region's extra edge vertices -- each region hangs a SKIRT off its edges to cover it.
    ///
    /// 2. FLOATING ORIGIN. Godot runs in floats; this keeps the player within RebaseDistance of (0,0,0) by moving the
    ///    WORLD instead of the player: when the focus strays past it, every Node3D under the world root shifts by a
    ///    whole number of regions and OriginX/Z absorbs the difference (in doubles). Whole regions, so world-space
    ///    shader patterns that tile at 16 m (the terrain albedo) do not jump.
    ///
    /// 3. NEVER LOSE THE GROUND. The 3x3 around the focus is generated synchronously on spawn and on a long teleport,
    ///    and the ground collider is a HeightMapShape3D straight from the LOD0 heights (the same order Godot wants:
    ///    row-major in Z). If the focus ever ends up below the generator's own height, it is lifted and counted --
    ///    `Rescues` -- so a test can require that number to stay 0 rather than have the safety net hide a hole.
    ///
    /// MP is NOT wired: this is a singleplayer, pure-direct-path world (no loopback), because the server half --
    /// a frame per cluster of players, region+offset positions on the wire -- is the next stage, not this one. The
    /// generator is already engine-free in core/ so a server can build the identical colliders.
    /// </summary>
    public partial class RegionStreamer : Node3D
    {
        public static RegionStreamer Active;

        public InfiniteTerrain Gen;
        /// <summary>Where regions come from; the generator unless something else is plugged in (a hand-made map
        /// streamed off disk would be an IRegionSource too -- nothing below cares which).</summary>
        public IRegionSource Source;
        IRegionSource Src => Source ?? Gen;
        /// <summary>What the rings centre on. Usually the player; a fixed render camera for an aerial shot.</summary>
        public Node3D Focus;
        public PlayerController Player;
        /// <summary>Whose Node3D children move on a rebase (the scene root everything is built under).</summary>
        public Node WorldRoot;

        /// <summary>Absolute position of local (0,0,0), in metres. Only ever a whole number of regions.</summary>
        public double OriginX, OriginZ;
        /// <summary>1024 m; UG_INF_REBASE overrides it so a render can cross a rebase without driving a kilometre
        /// (the shift is still a whole number of regions, so a small value means "every region boundary").</summary>
        public static readonly float RebaseDistance = float.TryParse(System.Environment.GetEnvironmentVariable("UG_INF_REBASE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rd) && rd > 0f ? rd : 1024f;
        /// <summary>UG_INF_ORIGINFIX=0 leaves the shaders' world origin at zero: the A/B control for the rebase twitch.</summary>
        static readonly bool OriginFix = System.Environment.GetEnvironmentVariable("UG_INF_ORIGINFIX") != "0";
        void PublishOrigin() { if (OriginFix) WorldOrigin.Set(OriginX, OriginZ); }
        /// <summary>Render probe: shift the world by whole regions NOW, wherever the focus is.</summary>
        public void DebugShift(int rx, int rz) => ShiftWorld(rx * InfiniteTerrain.RegionSize, rz * InfiniteTerrain.RegionSize);
        /// <summary>Wall-clock cost of the last ShiftWorld, ms (the rebase hitch).</summary>
        public double LastShiftMs;
        public static bool DebugShiftLog;

        public static readonly int[] LodRing = { 2, 4, 7, 11 };
        public const int ColliderRing = 1, TreeRing = 3, FoliageRing = 1;
        /// <summary>Foliage is batched in 64 m cells (4x4 a region) so each MultiMesh can be distance-culled on its own.</summary>
        public const float FoliageCellSize = 64f;
        public static int MaxRing => LodRing[LodRing.Length - 1];

        // ---- diagnostics (the overlay and the tests read these) ----
        public int Rebases, Rescues, Committed, TreeCount, FoliageCount, ImpostorCount;
        public double GenMsTotal; public int GenCount;
        public readonly int[] LoadedByLod = new int[4];
        public int Colliders => _colliders;
        /// <summary>Nothing queued, cooking or waiting to upload, and the outermost ring has arrived -- what a --shot
        /// waits for, since a movie-mode frame takes seconds and the first frames would show a world half-built.</summary>
        public bool Settled => Queued == 0 && InFlight == 0 && _done.IsEmpty && LoadedByLod[LodRing.Length - 1] > 0 && _impMissing == 0;
        public int Queued { get { lock (_lock) return _jobs.Count; } }
        public int InFlight => _inFlight;

        sealed class Region
        {
            public RegionCoord C;
            public Node3D Node;
            public MeshInstance3D Mesh;
            public MeshInstance3D[] Road = new MeshInstance3D[RoadSlots];   // one surface per road class (RoadKind), + the raised-highway debug slot
            public int Lod = -1, PendingLod = -1;
            public float[] Lod0Heights;
            public List<TreeSpawn> TreeList;
            public Node3D Trees;            // the MultiMeshes
            public Node3D TreeBodies;       // trunk colliders, ring <= ColliderRing only
            public StaticBody3D Ground;
            public Dictionary<(int kind, int cell), List<Transform3D>> FoliageXf;   // from the LOD0 build, kept across LOD swaps
            public List<(Transform3D Pole, bool HasNext, Transform3D Next)> PoleXf;   // from the LOD0/1 build
            public Node3D Power;            // pole meshes + wires, ring <= TreeRing
            public Node3D PoleBodies;       // pole colliders, ring <= ColliderRing
            public Node3D Foliage;          // grass / flowers / pebbles / bushes, ring <= FoliageRing only
            public int FoliageCount;
            public (Vector3 P, float S, int Cell)[] ImpTrees;   // billboard placements on the DISPLAYED LOD's ground
            public MultiMeshInstance3D Impostors;   // ring >= ImpostorRing
        }

        sealed class Job { public RegionCoord C; public int Lod; }
        sealed class RoadMesh { public Vector3[] V, N; public Vector2[] UV; public int[] I; }

        sealed class Built
        {
            public RegionData D;
            public Vector3[] V, N; public Vector2[] UV; public int[] I;
            public byte[] S0, S1; public int SplatSize;
            public Dictionary<string, List<Transform3D>> TreeXf;
            public Dictionary<(int kind, int cell), List<Transform3D>> FoliageXf;
            public RoadMesh[] Road;   // per RoadKind, null where the region has none of that class
            public List<(Transform3D Pole, bool HasNext, Transform3D Next)> Poles;
            public (Vector3 P, float S, int Cell)[] ImpTrees;
        }

        readonly Dictionary<RegionCoord, Region> _regions = new();
        readonly object _lock = new();
        readonly List<Job> _jobs = new();
        readonly ConcurrentQueue<Built> _done = new();
        // jobs a worker threw away because their region had left range: the main thread must clear the region's
        // PendingLod, or a region that comes BACK into range believes its request is still cooking and never asks again
        readonly ConcurrentQueue<Job> _dropped = new();
        Thread[] _workers;
        volatile bool _quit;
        volatile int _cx, _cz;   // the focus region, for the workers' nearest-first pick
        int _inFlight, _colliders;
        RegionCoord _lastCenter = new RegionCoord(int.MinValue, int.MinValue);
        double _resweep;
        MeshInstance3D _water;

        public override void _Ready()
        {
            Active = this;
            int n = Mathf.Clamp(System.Environment.ProcessorCount - 2, 1, 3);
            _workers = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"region-gen-{i}" };
                _workers[i].Start();
            }
            Log.Print($"[infinite] streamer up: {(Source != null ? Source.GetType().Name : $"seed {Gen.Seed}")}, {n} worker threads, rings {string.Join("/", LodRing)}, colliders <= {ColliderRing}, trees <= {TreeRing}, billboards {(TreeImpostors ? $"ring {ImpostorRing}+" : "off")}");
            if (TreeImpostors) BakeImpostorAtlas();
            PublishOrigin();
        }

        public override void _ExitTree()
        {
            WorldOrigin.Set(0, 0);   // the next world (a fixed map) draws its patterns from its own coordinates
            _quit = true;
            lock (_lock) Monitor.PulseAll(_lock);
            // wait for the workers to actually leave (a region takes milliseconds), so no managed thread of ours is
            // still running while the engine tears the runtime down
            if (_workers != null) foreach (var t in _workers) t.Join(250);
            if (Active == this) Active = null;
        }

        // ---------------------------------------------------------------------------------------------------
        // Coordinates.

        public double AbsX(float localX) => OriginX + localX;
        public double AbsZ(float localZ) => OriginZ + localZ;
        public Vector3 ToLocal(double ax, double y, double az) => new Vector3((float)(ax - OriginX), (float)y, (float)(az - OriginZ));
        public RegionCoord FocusRegion()
        {
            var p = Focus != null && IsInstanceValid(Focus) ? Focus.GlobalPosition : Vector3.Zero;
            return RegionCoord.Containing(AbsX(p.X), AbsZ(p.Z));
        }

        public static int LodForRing(int ring)
        {
            for (int l = 0; l < LodRing.Length; l++) if (ring <= LodRing[l]) return l;
            return -1;
        }

        Vector3 RegionLocalOrigin(RegionCoord c) => new Vector3((float)(c.MinX - OriginX), 0f, (float)(c.MinZ - OriginZ));

        // ---------------------------------------------------------------------------------------------------
        // Main thread.

        public override void _PhysicsProcess(double delta)
        {
            if (Focus == null || !IsInstanceValid(Focus)) return;
            var p = Focus.GlobalPosition;
            if (Mathf.Abs(p.X) > RebaseDistance || Mathf.Abs(p.Z) > RebaseDistance) Rebase(p);
            GuardGround();
        }

        public override void _Process(double delta)
        {
            if (Focus == null || !IsInstanceValid(Focus) || Src == null) return;
            var center = FocusRegion();
            _cx = center.X; _cz = center.Z;
            _resweep -= delta;
            if (!center.Equals(_lastCenter) || _resweep <= 0)
            {
                Sweep(center);
                _lastCenter = center;
                _resweep = 0.25;
            }
            CommitSome(center);
            FollowWater();
        }

        /// <summary>Decide what should exist: request every region in range at its ring's LOD, drop what has
        /// left, and add or remove colliders and trees as regions cross their rings.</summary>
        void Sweep(RegionCoord center)
        {
            int max = MaxRing;
            _impBudget = ImpostorBuildsPerSweep; _impMissing = 0;
            var drop = new List<RegionCoord>();
            foreach (var kv in _regions)
                if (kv.Key.RingTo(center) > max + 1) drop.Add(kv.Key);
            foreach (var c in drop) Unload(c);

            for (int dz = -max; dz <= max; dz++)
                for (int dx = -max; dx <= max; dx++)
                {
                    var c = new RegionCoord(center.X + dx, center.Z + dz);
                    int ring = c.RingTo(center), want = LodForRing(ring);
                    if (!_regions.TryGetValue(c, out var r))
                    {
                        r = new Region { C = c, Node = new Node3D { Name = $"Region_{c.X}_{c.Z}" } };
                        AddChild(r.Node);
                        r.Node.Position = RegionLocalOrigin(c);
                        _regions[c] = r;
                    }
                    if (r.Lod != want && r.PendingLod != want) Request(r, want);
                    UpdateExtras(r, ring);
                }
        }

        void Request(Region r, int lod)
        {
            r.PendingLod = lod;
            lock (_lock)
            {
                var c = r.C;
                _jobs.RemoveAll(j => j.C.Equals(c));   // a newer LOD supersedes the queued one
                _jobs.Add(new Job { C = r.C, Lod = lod });
                Monitor.Pulse(_lock);
            }
        }

        void Unload(RegionCoord c)
        {
            if (!_regions.TryGetValue(c, out var r)) return;
            if (r.Lod >= 0) LoadedByLod[r.Lod]--;
            if (r.Ground != null) _colliders--;
            if (r.Trees != null) TreeCount -= r.TreeList?.Count ?? 0;
            if (r.Foliage != null) FoliageCount -= r.FoliageCount;
            if (r.Impostors != null) ImpostorCount -= r.Impostors.Multimesh.InstanceCount;
            r.Node.QueueFree();
            _regions.Remove(c);
            _pendingTrees.Remove(c);
        }

        /// <summary>Ground collider and trees follow the ring, independently of the mesh's LOD.</summary>
        void UpdateExtras(Region r, int ring)
        {
            // ground: needs the full-resolution heights, which arrive with the LOD0 mesh (ring <= 2 covers ring <= 1)
            if (ring <= ColliderRing && r.Ground == null && r.Lod0Heights != null) AddGround(r);
            else if (ring > ColliderRing + 1 && r.Ground != null) { r.Ground.QueueFree(); r.Ground = null; _colliders--; }

            if (ring <= TreeRing && r.Trees == null && r.TreeList != null && _pendingTrees.TryGetValue(r.C, out var xf))
            {
                r.Trees = BuildTrees(xf);
                r.Node.AddChild(r.Trees);
                TreeCount += r.TreeList.Count;
            }
            else if (ring > TreeRing + 1 && r.Trees != null)
            {
                r.Trees.QueueFree(); r.Trees = null;
                TreeCount -= r.TreeList?.Count ?? 0;
            }

            if (ring <= ColliderRing && r.TreeBodies == null && r.TreeList != null) { r.TreeBodies = BuildTrunks(r.TreeList); r.Node.AddChild(r.TreeBodies); }
            else if (ring > ColliderRing + 1 && r.TreeBodies != null) { r.TreeBodies.QueueFree(); r.TreeBodies = null; }

            if (ring >= ImpostorRing && r.Impostors == null && r.ImpTrees != null && r.ImpTrees.Length > 0 && TreeImpostors)
            {
                if (_impMat != null && _impBudget > 0)
                {
                    _impBudget--;
                    r.Impostors = BuildImpostors(r.ImpTrees);
                    r.Node.AddChild(r.Impostors);
                    ImpostorCount += r.ImpTrees.Length;
                }
                else _impMissing++;   // the atlas is still baking, or this sweep's budget is spent: not settled yet
            }
            else if (ring < ImpostorRing - 1 && r.Impostors != null)
            {
                ImpostorCount -= r.Impostors.Multimesh.InstanceCount;
                r.Impostors.QueueFree(); r.Impostors = null;
            }

            if (ring <= TreeRing && r.Power == null && r.PoleXf != null && r.PoleXf.Count > 0) { r.Power = BuildPower(r, r.PoleXf); }
            else if (ring > TreeRing + 1 && r.Power != null) { r.Power.QueueFree(); r.Power = null; }
            if (ring <= ColliderRing && r.PoleBodies == null && r.PoleXf != null && r.PoleXf.Count > 0) { r.PoleBodies = BuildPoleBodies(r.PoleXf); r.Node.AddChild(r.PoleBodies); }
            else if (ring > ColliderRing + 1 && r.PoleBodies != null) { r.PoleBodies.QueueFree(); r.PoleBodies = null; }

            if (ring <= FoliageRing && r.Foliage == null && r.FoliageXf != null)
            {
                r.Foliage = BuildFoliage(r.FoliageXf, out r.FoliageCount);
                r.Node.AddChild(r.Foliage);
                FoliageCount += r.FoliageCount;
            }
            else if (ring > FoliageRing + 1 && r.Foliage != null)
            {
                r.Foliage.QueueFree(); r.Foliage = null;
                FoliageCount -= r.FoliageCount;
            }
        }

        // tree transforms are kept per region once built, so a region crossing the tree ring twice does not regenerate
        readonly Dictionary<RegionCoord, Dictionary<string, List<Transform3D>>> _pendingTrees = new();

        /// <summary>Regions uploaded per frame at most (and 4 ms). UG_INF_COMMIT raises it for a movie-mode render, where
        /// a frame takes seconds of real time and the workers finish everything long before 8-a-frame would show it.</summary>
        static readonly int CommitCap = int.TryParse(System.Environment.GetEnvironmentVariable("UG_INF_COMMIT"), out int cc) && cc > 0 ? cc : 8;

        /// <summary>Upload finished regions, nearest first, within a per-frame budget.</summary>
        void CommitSome(RegionCoord center)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            while (_dropped.TryDequeue(out var dj))
                if (_regions.TryGetValue(dj.C, out var dr) && dr.PendingLod == dj.Lod) dr.PendingLod = -1;
            int n = 0;
            while (n < CommitCap && _done.TryDequeue(out var b))
            {
                Interlocked.Decrement(ref _inFlight);
                GenMsTotal += b.D.GenMs; GenCount++;
                if (!_regions.TryGetValue(b.D.Coord, out var r)) continue;   // left range while it was cooking
                int ring = r.C.RingTo(center), want = LodForRing(ring);
                bool useful = b.D.Lod == want || r.Lod < 0;                   // a stale LOD is still better than a hole
                if (b.D.Lod == r.PendingLod) r.PendingLod = -1;
                if (b.D.Lod == 0) r.Lod0Heights = b.D.Heights;
                if (b.D.Trees != null && r.TreeList == null) { r.TreeList = b.D.Trees; _pendingTrees[r.C] = b.TreeXf; }
                if (b.FoliageXf != null && r.FoliageXf == null) r.FoliageXf = b.FoliageXf;
                if (b.Poles != null && r.PoleXf == null) r.PoleXf = b.Poles;
                if (useful)
                {
                    Apply(r, b);
                    // billboards stand on THIS LOD's ground: a new mesh means re-planting them on it
                    r.ImpTrees = b.ImpTrees;
                    if (r.Impostors != null) { ImpostorCount -= r.Impostors.Multimesh.InstanceCount; r.Impostors.QueueFree(); r.Impostors = null; }
                }
                UpdateExtras(r, ring);
                n++;
                if (CommitCap <= 8 && (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency > 4.0) break;
            }
        }

        void Apply(Region r, Built b)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = b.V;
            arrays[(int)Mesh.ArrayType.Normal] = b.N;
            arrays[(int)Mesh.ArrayType.TexUV] = b.UV;
            arrays[(int)Mesh.ArrayType.Index] = b.I;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var s0 = ImageTexture.CreateFromImage(Image.CreateFromData(b.SplatSize, b.SplatSize, false, Image.Format.Rgba8, b.S0));
            var s1 = ImageTexture.CreateFromImage(Image.CreateFromData(b.SplatSize, b.SplatSize, false, Image.Format.Rgba8, b.S1));
            var mat = (Material)Terrain.RegionMaterial(s0, s1)
                      ?? new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.45f, 0.28f), Roughness = 1f };
            if (r.Mesh == null)
            {
                r.Mesh = new MeshInstance3D { Name = "Ground" };
                r.Node.AddChild(r.Mesh);
            }
            r.Mesh.Mesh = mesh;
            r.Mesh.MaterialOverride = mat;
            // LOD2+ starts ~1.1 km out, past any shadow cascade worth having; the shadow pass would redraw it for nothing
            r.Mesh.CastShadow = b.D.Lod >= 2 ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On;
            if (r.Lod >= 0) LoadedByLod[r.Lod]--;
            r.Lod = b.D.Lod;
            LoadedByLod[r.Lod]++;
            Committed++;
            // the road surfaces, one mesh per class: rebuilt with every LOD, because they sit on THAT mesh's triangles
            for (int k = 0; k < RoadSlots; k++)
            {
                var rd = b.Road?[k];
                if (rd != null && rd.I.Length > 0)
                {
                    var ra = new Godot.Collections.Array();
                    ra.Resize((int)Mesh.ArrayType.Max);
                    ra[(int)Mesh.ArrayType.Vertex] = rd.V;
                    ra[(int)Mesh.ArrayType.Normal] = rd.N;
                    ra[(int)Mesh.ArrayType.TexUV] = rd.UV;
                    ra[(int)Mesh.ArrayType.Index] = rd.I;
                    var rm = new ArrayMesh();
                    rm.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, ra);
                    if (r.Road[k] == null)
                    {
                        r.Road[k] = new MeshInstance3D { Name = k == RaisedSlot ? "Road_Raised" : k == CutSlot ? "Road_Cut" : "Road_" + (RoadKind)k, MaterialOverride = RoadMat(k), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                        r.Node.AddChild(r.Road[k]);
                    }
                    r.Road[k].Mesh = rm;
                }
                else if (r.Road[k] != null) { r.Road[k].QueueFree(); r.Road[k] = null; }
            }
        }

        void AddGround(Region r)
        {
            int v = InfiniteTerrain.FullCells + 1;
            float sp = InfiniteTerrain.RegionSize / InfiniteTerrain.FullCells;
            var body = new StaticBody3D { Name = "GroundBody", CollisionLayer = 1u << 0 };
            body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Grass);
            // HeightMapShape3D: row-major in Z, 1 unit apart, CENTRED -- so scale X/Z by the spacing (Jolt wants them
            // equal, and they are) and sit it at the region's centre. Heights are absolute world Y; the body stays at 0.
            var cs = new CollisionShape3D
            {
                Shape = new HeightMapShape3D { MapWidth = v, MapDepth = v, MapData = r.Lod0Heights },
                Scale = new Vector3(sp, 1f, sp),
                Position = new Vector3(InfiniteTerrain.RegionSize * 0.5f, 0f, InfiniteTerrain.RegionSize * 0.5f),
            };
            body.AddChild(cs);
            r.Node.AddChild(body);
            r.Ground = body;
            _colliders++;
        }

        static readonly Dictionary<string, Material> _treeMats = new();
        static Material TreeMat(string name, int part)
        {
            string key = name + "_" + part;
            if (_treeMats.TryGetValue(key, out var m)) return m;
            string dir = ProjectSettings.GlobalizePath("res://content/resources/");
            m = part == 0 ? ResourceField.MakeSwayMat($"{dir}{name}_{part}_tex.png") : ResourceField.MakeMat($"{dir}{name}_{part}_tex.png", false);
            _treeMats[key] = m;
            return m;
        }

        // ---- tree impostors (strawberry 2026-10-09: "can we render more tree imposters further?"). Real trees stop at
        // TreeRing; past it every region keeps its trees as camera-facing cards out to the last LOD ring (~2.9 km).
        // The pictures are ResourceField's bake (from the same .obj the real trees draw, so they cannot disagree),
        // packed side by side into ONE atlas so a region's whole forest is one MultiMesh -- one draw call -- with the
        // species picked per instance through INSTANCE_CUSTOM. The handover overlaps the way ResourceField's does:
        // cards switch on at 88% of the real trees' cull, so no camera jitter can open a gap between the two.
        public static bool TreeImpostors = System.Environment.GetEnvironmentVariable("UG_INF_TREEIMP") != "0";
        public const int ImpostorRing = 2;              // built from here out (cards only DRAW past the handover distance)
        const int ImpostorBuildsPerSweep = 48;          // MultiMeshes built per sweep, so the first fill is not one frame
        static readonly string[] ImpNames = { "Pine_0", "Pine_1", "Birch_0", "Birch_1", "Maple_0", "Maple_1" };   // cell = kind * 2 + variant
        readonly Vector2[] _impSize = new Vector2[6];   // world size each card was framed at (W from the bake's aspect)
        ShaderMaterial _impMat;
        static QuadMesh _impQuad;
        int _impBudget, _impMissing;

        const string ImpostorShader = @"
shader_type spatial;
render_mode cull_disabled, specular_disabled;
uniform sampler2D atlas : source_color, filter_linear_mipmap;
uniform float cells = 6.0;
void vertex() {
	// Y-billboard keeping the instance's scale: Godot's own BILLBOARD_FIXED_Y + keep_scale, so a tree turns to face
	// you but never tips toward the camera
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
			vec4(normalize(cross(vec3(0.0, 1.0, 0.0), MAIN_CAM_INV_VIEW_MATRIX[2].xyz)), 0.0),
			vec4(0.0, 1.0, 0.0, 0.0),
			vec4(normalize(cross(MAIN_CAM_INV_VIEW_MATRIX[0].xyz, vec3(0.0, 1.0, 0.0))), 0.0),
			MODEL_MATRIX[3]);
	MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0), vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0), vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0), vec4(0.0, 0.0, 0.0, 1.0));
	MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
	UV = vec2((UV.x + INSTANCE_CUSTOM.r) / cells, UV.y);   // this tree's species, out of the atlas
}
void fragment() {
	vec4 c = texture(atlas, UV);
	ALBEDO = c.rgb;
	ALPHA = c.a;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
	ROUGHNESS = 1.0;
}";

        async void BakeImpostorAtlas()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/resources/");
            int w = ResourceField.ImpostorTexW, h = ResourceField.ImpostorTexH;
            var atlas = Image.CreateEmpty(w * ImpNames.Length, h, false, Image.Format.Rgba8);
            int ok = 0;
            for (int i = 0; i < ImpNames.Length; i++)
            {
                var (img, _, bh) = await ResourceField.BakeImpostorImageAsync(this, dir, ImpNames[i], 2);
                if (!IsInstanceValid(this)) return;
                if (img == null) { Log.Err($"[infinite] billboard bake failed for {ImpNames[i]}: that species gets no far trees"); continue; }
                if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
                atlas.BlitRect(img, new Rect2I(0, 0, w, h), new Vector2I(i * w, 0));
                // the ortho camera framed bh TALL and bh * w/h WIDE: that box is the card, or the picture stretches
                _impSize[i] = new Vector2(bh * w / h, bh);
                ok++;
            }
            atlas.GenerateMipmaps();
            _impQuad = new QuadMesh { Size = Vector2.One, Orientation = PlaneMesh.OrientationEnum.Z };
            var mat = new ShaderMaterial { Shader = new Shader { Code = ImpostorShader } };
            mat.SetShaderParameter("atlas", ImageTexture.CreateFromImage(atlas));
            mat.SetShaderParameter("cells", (float)ImpNames.Length);
            _impMat = mat;
            _resweep = 0;   // regions already out there get their cards on the next sweep
            Log.Print($"[infinite] billboard atlas baked: {ok}/{ImpNames.Length} species, on at {ImpostorBegin:0} m");
        }

        static float ImpostorBegin => (TreeRing + 0.5f) * InfiniteTerrain.RegionSize * ResourceField.ImpostorOverlap;

        MultiMeshInstance3D BuildImpostors((Vector3 P, float S, int Cell)[] trees)
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = _impQuad };
            mm.InstanceCount = trees.Length;   // format BEFORE count
            for (int k = 0; k < trees.Length; k++)
            {
                var (p, s, cell) = trees[k];
                var size = _impSize[cell] * s;   // zero for a species whose bake failed: an invisible card, never a black one
                // a QuadMesh is centred on its origin: lift it half a card so the picture's foot is on the ground
                mm.SetInstanceTransform(k, new Transform3D(Basis.Identity.Scaled(new Vector3(size.X, size.Y, 1f)), p + new Vector3(0f, size.Y * 0.5f, 0f)));
                mm.SetInstanceCustomData(k, new Color(cell, 0f, 0f, 0f));
            }
            var mmi = new MultiMeshInstance3D
            {
                Name = "Impostors", Multimesh = mm, MaterialOverride = _impMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,   // a flat card casts a flat wrong shadow
                VisibilityRangeBegin = ImpostorBegin,
            };
            mmi.AddToGroup(NearestFilter.KeepFilterGroup);
            return mmi;
        }

        Node3D BuildTrees(Dictionary<string, List<Transform3D>> byName)
        {
            var holder = new Node3D { Name = "Trees" };
            string dir = ProjectSettings.GlobalizePath("res://content/resources/");
            foreach (var kv in byName)
                for (int part = 0; part < 2; part++)
                {
                    var mesh = ObjMesh.Load($"{dir}{kv.Key}_{part}.obj");
                    if (mesh == null) continue;
                    var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = part == 0, Mesh = mesh };
                    mm.InstanceCount = kv.Value.Count;   // format BEFORE count
                    for (int k = 0; k < kv.Value.Count; k++) mm.SetInstanceTransform(k, kv.Value[k]);
                    var mmi = new MultiMeshInstance3D
                    {
                        Multimesh = mm, MaterialOverride = TreeMat(kv.Key, part),
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
                        VisibilityRangeEnd = (TreeRing + 0.5f) * InfiniteTerrain.RegionSize,
                    };
                    mmi.AddToGroup(NearestFilter.KeepFilterGroup);
                    holder.AddChild(mmi);
                }
            return holder;
        }

        // ---- roads: PEI's own road materials, the way RoadField draws its splines. Paved classes wear wet_surface
        // (rain sheen, rings, puddles); the dirt trail a plain material, as RoadField does for trails.
        //   Highway (per carriageway) = Highway_1, road_1 | Main = Highway_0, road_0 | Small = road_8 (two lanes, dashed
        //   yellow) | Trail = Trail, road_5.  UV repeats every texture.height / Roads.dat height, RoadField's rule.
        static readonly string[] RoadTex = { "road_1", "road_0", "road_8", "road_5" };
        static readonly float[] RoadTexMetres = { 128f / 4f, 128f / 4f, 256f / 8f, 64f / 8f };
        /// <summary>Half-width of one drawn surface: a highway is TWO of these, one per carriageway.</summary>
        static float RibbonHalf(RoadKind k) => k == RoadKind.Highway ? InfiniteRoads.HighwayLaneHalf : InfiniteRoads.PavedHalf(k);
        /// <summary>Lift over the profile: main roads and highways above the small roads and trails that start under
        /// their edges, highways above the mains they cross, so no two surfaces z-fight.</summary>
        static readonly float[] RoadLift = { 0.045f, 0.03f, 0.015f, 0.01f };
        /// <summary>UG_INF_MARKS=1: highway pieces on a raised stretch (bridge candidate) draw magenta and on a deep cut
        /// (tunnel candidate) cyan, each from its own slot, so the marking can be checked by eye. Off, they are
        /// ordinary highway.</summary>
        public static bool ShowMarks = System.Environment.GetEnvironmentVariable("UG_INF_MARKS") == "1";
        const int RoadSlots = 6, RaisedSlot = 4, CutSlot = 5;
        static readonly Material[] _roadMats = new Material[RoadSlots];
        static Material RoadMat(int slot)
        {
            if (_roadMats[slot] != null) return _roadMats[slot];
            var k = slot >= RaisedSlot ? RoadKind.Highway : (RoadKind)slot;
            var img = new Image();
            string p = ProjectSettings.GlobalizePath($"res://content/roads/{RoadTex[(int)k]}.png");
            bool ok = System.IO.File.Exists(p) && ContentProvider.LoadOk(img, p);
            if (ok) img.GenerateMipmaps();
            Material mat;
            if (slot >= RaisedSlot)
                mat = new StandardMaterial3D
                {
                    AlbedoTexture = ok ? ImageTexture.CreateFromImage(img) : null, AlbedoColor = slot == RaisedSlot ? new Color(1f, 0.25f, 1f) : new Color(0.1f, 0.9f, 1f),
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic, Roughness = 1f,
                };
            else if (k == RoadKind.Trail)
                mat = new StandardMaterial3D
                {
                    AlbedoTexture = ok ? ImageTexture.CreateFromImage(img) : null, AlbedoColor = ok ? Colors.White : new Color(0.45f, 0.37f, 0.28f),
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmapsAnisotropic, Roughness = 1f,
                };
            else
            {
                RainSystem3D.EnsureGlobals();
                var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/wet_surface.gdshader") };
                m.SetShaderParameter("dry_roughness", 1.0f);
                m.SetShaderParameter("impact_amount", 1.0f);
                m.SetShaderParameter("splash_scale", 1.0f);
                m.SetShaderParameter("puddle_amount", 1.0f);
                if (ok) { m.SetShaderParameter("albedo_tex", ImageTexture.CreateFromImage(img)); m.SetShaderParameter("use_tex", true); }
                else m.SetShaderParameter("dry_albedo", new Vector3(0.34f, 0.34f, 0.35f));
                mat = m;
            }
            return _roadMats[slot] = mat;
        }

        // ---- ground cover: the same meshes + shaders FoliageField draws PEI's baked foliage with
        sealed class FoliageType { public Mesh Mesh; public Material Mat; public float Range; }
        static FoliageType[] _foliageTypes;
        static FoliageType[] FoliageTypes()
        {
            if (_foliageTypes != null) return _foliageTypes;
            string fol = ProjectSettings.GlobalizePath("res://content/foliage/"), res = ProjectSettings.GlobalizePath("res://content/resources/");
            GrassDisplacers.EnsureGlobals();   // grass/flower shaders read wind + displacer globals: they must exist BEFORE the material links
            GrassDisplacers.SetFadeRange(160f);
            Texture2D Tex(string path)
            {
                var img = new Image();
                if (!System.IO.File.Exists(path) || !ContentProvider.LoadOk(img, path)) return null;
                img.GenerateMipmaps();
                return ImageTexture.CreateFromImage(img);
            }
            Material Grass()
            {
                var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/grass_displace.gdshader") };
                m.SetShaderParameter("albedo_tex", Tex(fol + "grass_00_tex.png"));
                return m;
            }
            Material Up(string tex, Color solid, bool sway)
            {
                var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/foliage_up.gdshader") };
                var t = tex != null ? Tex(fol + tex) : null;
                if (t != null) { m.SetShaderParameter("albedo_tex", t); m.SetShaderParameter("use_texture", true); }
                else { m.SetShaderParameter("albedo_color", solid); m.SetShaderParameter("use_texture", false); }
                m.SetShaderParameter("do_sway", sway);
                return m;
            }
            var grey = new Color(0.456f, 0.456f, 0.456f);
            _foliageTypes = new[]
            {
                new FoliageType { Mesh = ObjMesh.Load(fol + "grass_00.obj"), Mat = Grass(), Range = 160f },
                new FoliageType { Mesh = ObjMesh.Load(fol + "flowers_00.obj"), Mat = Up("flowers_00_tex.png", grey, true), Range = 160f },
                new FoliageType { Mesh = ObjMesh.Load(fol + "flowers_01.obj"), Mat = Up("flowers_01_tex.png", grey, true), Range = 160f },
                new FoliageType { Mesh = ObjMesh.Load(fol + "flowers_02.obj"), Mat = Up("flowers_02_tex.png", grey, true), Range = 160f },
                new FoliageType { Mesh = ObjMesh.Load(fol + "flowers_03.obj"), Mat = Up("flowers_03_tex.png", grey, true), Range = 160f },
                new FoliageType { Mesh = ObjMesh.Load(fol + "pebble_00.obj"), Mat = Up(null, grey, false), Range = 100f },
                new FoliageType { Mesh = ObjMesh.Load(fol + "pebble_sand_00.obj"), Mat = Up(null, new Color(0.506f, 0.506f, 0.506f), false), Range = 100f },
                new FoliageType { Mesh = ObjMesh.Load(res + "Bush_0_0.obj"), Mat = ResourceField.MakeSwayMat(res + "Bush_0_0_tex.png"), Range = 320f },
                new FoliageType { Mesh = ObjMesh.Load(res + "Bush_1_0.obj"), Mat = ResourceField.MakeSwayMat(res + "Bush_1_0_tex.png"), Range = 320f },
            };
            return _foliageTypes;
        }

        Node3D BuildFoliage(Dictionary<(int kind, int cell), List<Transform3D>> byCell, out int count)
        {
            var holder = new Node3D { Name = "Foliage" };
            var types = FoliageTypes();
            count = 0;
            foreach (var kv in byCell)
            {
                var ft = types[kv.Key.kind];
                if (ft.Mesh == null) continue;
                bool bush = kv.Key.kind >= (int)InfiniteTerrain.FoliageKind.Bush0;
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = bush, Mesh = ft.Mesh };
                mm.InstanceCount = kv.Value.Count;
                for (int k = 0; k < kv.Value.Count; k++) mm.SetInstanceTransform(k, kv.Value[k]);
                var mmi = new MultiMeshInstance3D
                {
                    Multimesh = mm, MaterialOverride = ft.Mat, VisibilityRangeEnd = ft.Range,
                    CastShadow = bush ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
                };
                mmi.AddToGroup(NearestFilter.KeepFilterGroup);
                holder.AddChild(mmi);
                count += kv.Value.Count;
            }
            return holder;
        }

        // ---- power lines: PEI's Power_Line_0 pole, wired by the editor's own PowerLineField (four wires, sag, sway)
        static Mesh _poleMesh; static Material _poleMat;
        static void PoleAssets()
        {
            if (_poleMesh != null) return;
            string odir = ProjectSettings.GlobalizePath("res://content/objects/");
            _poleMesh = ObjMesh.Load(odir + PowerLineField.PoleMesh + ".obj");
            var mat = new StandardMaterial3D { Roughness = 0.95f };
            var img = new Image();
            if (ContentProvider.LoadOk(img, odir + PowerLineField.PoleMesh + "_tex.png"))
            {
                mat.AlbedoTexture = ImageTexture.CreateFromImage(img);
                mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;   // a 2x2 palette (see BuildPowerLineTest)
            }
            _poleMat = mat;
        }

        Node3D BuildPower(Region r, List<(Transform3D Pole, bool HasNext, Transform3D Next)> poles)
        {
            PoleAssets();
            var holder = new Node3D { Name = "Power" };
            r.Node.AddChild(holder);
            if (_poleMesh != null)
            {
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _poleMesh };
                mm.InstanceCount = poles.Count;
                for (int k = 0; k < poles.Count; k++) mm.SetInstanceTransform(k, poles[k].Pole);
                var mmi = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = _poleMat, VisibilityRangeEnd = (TreeRing + 0.5f) * InfiniteTerrain.RegionSize };
                mmi.AddToGroup(NearestFilter.KeepFilterGroup);
                holder.AddChild(mmi);
            }
            // PowerLineField builds its wires in WORLD space and pins itself to the world origin -- so hand it world
            // transforms now, and parent it under the region: a later floating-origin shift moves the region node and
            // the wires go with it, since the field's local transform under it is fixed at build time.
            // ⭐ THE WIRES CULL WHERE THESE POLES DO. Master: "make sure the wires are actually culled when
            // both the parent poles are culled." The poles above take (TreeRing + 0.5) * RegionSize, so the
            // wires take the same expression rather than PowerLineField's LodTable default -- on the
            // infinite map those are not the same number.
            var field = new PowerLineField
            {
                Name = "Wires",
                PoleCullDistance = (TreeRing + 0.5f) * InfiniteTerrain.RegionSize,
            };
            holder.AddChild(field);
            var toWorld = r.Node.GlobalTransform;
            foreach (var p in poles)
            {
                if (!p.HasNext) continue;
                int a = field.AddPole(toWorld * p.Pole), b = field.AddPole(toWorld * p.Next);
                field.Connect(a, b, out _);
            }
            field.Rebuild();
            return holder;
        }

        static Node3D BuildPoleBodies(List<(Transform3D Pole, bool HasNext, Transform3D Next)> poles)
        {
            var holder = new Node3D { Name = "PoleBodies" };
            foreach (var p in poles)
            {
                var body = new StaticBody3D { CollisionLayer = 1u << 0, Position = p.Pole.Origin };
                body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Wood);
                body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.18f, Height = 8f }, Position = new Vector3(0f, 4f, 0f) });
                holder.AddChild(body);
            }
            return holder;
        }

        /// <summary>A pole's placement: stood up (the mesh is Z-up raw Unity geometry; PEI places it at ex=270) and
        /// turned so the crossarm (local X) lies ACROSS the road and the wires run along it.</summary>
        static Transform3D PoleXform(float lx, float y, float lz, float dirX, float dirZ)
        {
            float theta = Mathf.Atan2(-dirX, -dirZ);   // rotates local X onto the road's perpendicular (-dz, dx)
            var basis = new Basis(Vector3.Up, theta) * new Basis(Vector3.Right, Mathf.DegToRad(270f));
            return new Transform3D(basis, new Vector3(lx, y - 0.3f, lz));   // a little sunk, so a pole on a slope never floats
        }

        static Node3D BuildTrunks(List<TreeSpawn> trees)
        {
            var holder = new Node3D { Name = "TreeTrunks" };
            foreach (var t in trees)
            {
                float r = t.Kind == (byte)InfiniteTerrain.TreeKind.Maple ? 0.83f : t.Kind == (byte)InfiniteTerrain.TreeKind.Pine ? 0.80f : 0.5f;
                var body = new StaticBody3D { CollisionLayer = 1u << 0, Position = new Vector3(t.X, t.Y - ResourceField.TreeSink * t.Scale, t.Z) };
                body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Wood);
                body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = r * t.Scale, Height = 8f * t.Scale }, Position = new Vector3(0f, 2.5f * t.Scale, 0f) });
                holder.AddChild(body);
            }
            return holder;
        }

        // ---------------------------------------------------------------------------------------------------
        // Floating origin.

        /// <summary>Move the world so the focus is back near (0,0,0). A whole number of regions, in X and Z only --
        /// heights never shift, so fall distances and the sea level mean the same thing before and after.</summary>
        void Rebase(Vector3 p)
        {
            float sx = Mathf.Floor(p.X / InfiniteTerrain.RegionSize) * InfiniteTerrain.RegionSize;
            float sz = Mathf.Floor(p.Z / InfiniteTerrain.RegionSize) * InfiniteTerrain.RegionSize;
            ShiftWorld(sx, sz);
        }

        void ShiftWorld(float sx, float sz)
        {
            if (sx == 0f && sz == 0f) return;
            long shiftT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var d = new Vector3(-sx, 0f, -sz);
            OriginX += sx; OriginZ += sz;
            var root = WorldRoot ?? GetParent();
            foreach (var child in root.GetChildren())
            {
                if (child == this || child is not Node3D n3) continue;
                if (n3 == Player)
                {
                    var b0 = Player.GlobalPosition; var t0 = Player.TruePhysicsPosition;
                    Player.ShiftOrigin(d);
                    if (DebugShiftLog) Log.Print($"[infinite] shift player: render {b0.X:0.00} -> {Player.GlobalPosition.X:0.00}, phys {t0.X:0.00} -> {Player.TruePhysicsPosition.X:0.00}");
                    continue;
                }
                if (n3 is DirectionalLight3D) continue;   // direction only; its position means nothing
                n3.GlobalPosition += d;
                n3.ResetPhysicsInterpolation();
            }
            foreach (var r in _regions.Values) r.Node.Position = RegionLocalOrigin(r.C);
            // (Tried and measured: ResetPhysicsInterpolation() on the region subtree here changed NOTHING -- pixel-
            // identical frames across a forced shift with and without it. The visible jump was the shaders, below.)
            PublishOrigin();   // world-space shader patterns (swell, wind, ripples) follow the world, not the shift
            Rebases++;
            LastShiftMs = (System.Diagnostics.Stopwatch.GetTimestamp() - shiftT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Log.Print($"[infinite] rebase #{Rebases} at frame {Engine.GetFramesDrawn()}: world shifted ({-sx:0}, {-sz:0}) m, origin now ({OriginX:0}, {OriginZ:0}), took {LastShiftMs:0.0} ms");
        }

        /// <summary>The focus below the generator's own ground means a hole in the collider, a late region, or a
        /// shift that missed something. Lift it and COUNT it -- the count is what the stream test asserts is 0.</summary>
        void GuardGround()
        {
            if (Player == null || !IsInstanceValid(Player)) return;
            var p = Player.GlobalPosition;
            float g = Src.HeightAt(AbsX(p.X), AbsZ(p.Z));
            if (p.Y < g - 2f)
            {
                Rescues++;
                Log.Print($"[infinite] RESCUE #{Rescues}: player at y {p.Y:0.0} under the ground ({g:0.0}) at ({AbsX(p.X):0}, {AbsZ(p.Z):0})");
                Player.TeleportTo(new Vector3(p.X, g + 1.5f, p.Z));
            }
        }

        /// <summary>Jump to an absolute position: re-centre the origin on it, throw away every region (they are all
        /// the wrong ones now), and build the ground under the target before anything can fall.</summary>
        public void TeleportAbsolute(double x, double z)
        {
            foreach (var c in new List<RegionCoord>(_regions.Keys)) Unload(c);
            lock (_lock) _jobs.Clear();
            _pendingTrees.Clear();
            var rc = RegionCoord.Containing(x, z);
            // the generic shift moves everything else (vehicles, items) along by the same amount -- they stay put
            // relative to the OLD place, which is now far away; that is the honest outcome of a teleport
            double newOx = rc.MinX, newOz = rc.MinZ;
            ShiftWorld((float)(newOx - OriginX), (float)(newOz - OriginZ));
            OriginX = newOx; OriginZ = newOz;   // exact, even when the shift itself was too large for a float
            PublishOrigin();
            foreach (var r in _regions.Values) r.Node.Position = RegionLocalOrigin(r.C);
            SyncGround(rc);
            float y = Src.HeightAt(x, z);
            Player?.TeleportTo(ToLocal(x, System.Math.Max(y, InfiniteTerrain.SeaLevel) + 1.5, z));
            _lastCenter = new RegionCoord(int.MinValue, int.MinValue);
        }

        /// <summary>Generate the 3x3 around a region on THIS thread, collider and all. Spawn and teleport only.</summary>
        public void SyncGround(RegionCoord center)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var c = new RegionCoord(center.X + dx, center.Z + dz);
                    if (!_regions.TryGetValue(c, out var r))
                    {
                        r = new Region { C = c, Node = new Node3D { Name = $"Region_{c.X}_{c.Z}" } };
                        AddChild(r.Node);
                        r.Node.Position = RegionLocalOrigin(c);
                        _regions[c] = r;
                    }
                    if (r.Lod == 0) continue;
                    var b = Build(Src.Generate(c, 0));
                    GenMsTotal += b.D.GenMs; GenCount++;
                    r.Lod0Heights = b.D.Heights;
                    r.TreeList = b.D.Trees; _pendingTrees[c] = b.TreeXf;
                    r.FoliageXf ??= b.FoliageXf;
                    r.PoleXf ??= b.Poles;
                    Apply(r, b);
                    UpdateExtras(r, c.RingTo(center));
                }
        }

        // ---------------------------------------------------------------------------------------------------
        // Water: one plane that follows the focus in whole-region steps (its shader patterns are world-space).

        public void AddWater()
        {
            const float size = 2 * 11.5f * InfiniteTerrain.RegionSize;
            _water = new MeshInstance3D
            {
                Name = "Sea",
                Mesh = new PlaneMesh { Size = new Vector2(size, size), SubdivideWidth = 200, SubdivideDepth = 200 },
                MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/water.gdshader") },
                Layers = WaterReflection.WaterLayer,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_water);
            WaterReflection.Attach(this, (ShaderMaterial)_water.MaterialOverride, InfiniteTerrain.SeaLevel);
            FollowWater();
        }

        void FollowWater()
        {
            if (_water == null || Focus == null || !IsInstanceValid(Focus)) return;
            var p = Focus.GlobalPosition;
            float s = InfiniteTerrain.RegionSize;
            _water.Position = new Vector3(Mathf.Floor(p.X / s) * s + s * 0.5f, InfiniteTerrain.SeaLevel, Mathf.Floor(p.Z / s) * s + s * 0.5f);
        }

        // ---------------------------------------------------------------------------------------------------
        // Workers.

        void WorkerLoop()
        {
            while (!_quit)
            {
                Job job = null;
                lock (_lock)
                {
                    if (_jobs.Count == 0) { Monitor.Wait(_lock, 100); continue; }
                    // nearest to the CURRENT focus, not to wherever it was when the job was queued
                    var center = new RegionCoord(_cx, _cz);
                    int best = -1, bestRing = int.MaxValue;
                    for (int i = 0; i < _jobs.Count; i++)
                    {
                        int ring = _jobs[i].C.RingTo(center);
                        if (ring < bestRing) { bestRing = ring; best = i; }
                    }
                    job = _jobs[best];
                    _jobs.RemoveAt(best);
                    if (bestRing > MaxRing + 1) { _dropped.Enqueue(job); continue; }   // left range while it waited
                    Interlocked.Increment(ref _inFlight);
                }
                try { _done.Enqueue(Build(Src.Generate(job.C, job.Lod))); }
                catch (System.Exception e) { Interlocked.Decrement(ref _inFlight); Log.Err($"[infinite] region {job.C} lod {job.Lod} failed: {e.Message}"); }
            }
        }

        static readonly string[] TreeNames = { "Pine", "Birch", "Maple" };

        /// <summary>Turn generated data into upload-ready arrays. Runs on a worker: plain C# and Godot value types only.</summary>
        static Built Build(RegionData d)
        {
            int n = d.Cells, v = n + 1;
            float sp = d.Spacing, skirt = Mathf.Max(6f, sp * 2f);
            var V = new List<Vector3>(v * v + 4 * v);
            var N = new List<Vector3>(v * v + 4 * v);
            var UV = new List<Vector2>(v * v + 4 * v);
            var I = new List<int>(n * n * 6 + 4 * n * 6);
            for (int j = 0; j < v; j++)
                for (int i = 0; i < v; i++)
                {
                    int k = j * v + i;
                    V.Add(new Vector3(i * sp, d.Heights[k], j * sp));
                    N.Add(new Vector3(d.Normals[k * 3], d.Normals[k * 3 + 1], d.Normals[k * 3 + 2]));
                    UV.Add(new Vector2((i + 0.5f) / v, (j + 0.5f) / v));   // texel CENTRES: neighbours sample identical edge texels
                }
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * v + i, b = a + 1, c = a + v, e = c + 1;
                    Tri(V, I, a, b, c, Vector3.Up);
                    Tri(V, I, b, e, c, Vector3.Up);
                }
            // skirts: each edge's vertices again, hung `skirt` metres lower, same normal + UV so they read as ground
            void Skirt(System.Func<int, int> edgeVert, Vector3 outward)
            {
                int start = V.Count;
                for (int t = 0; t < v; t++)
                {
                    int k = edgeVert(t);
                    V.Add(V[k] + Vector3.Down * skirt); N.Add(N[k]); UV.Add(UV[k]);
                }
                for (int t = 0; t < n; t++)
                {
                    int a = edgeVert(t), b = edgeVert(t + 1), al = start + t, bl = start + t + 1;
                    Tri(V, I, a, b, al, outward);
                    Tri(V, I, b, bl, al, outward);
                }
            }
            Skirt(t => t, Vector3.Forward);                   // z = 0 edge, facing -Z
            Skirt(t => n * v + t, Vector3.Back);              // z = max, facing +Z
            Skirt(t => t * v, Vector3.Left);                  // x = 0, facing -X
            Skirt(t => t * v + n, Vector3.Right);             // x = max, facing +X

            var s0 = new byte[v * v * 4]; var s1 = new byte[v * v * 4];
            for (int k = 0; k < v * v; k++)
            {
                int l = d.Layers[k];
                if (l < 4) s0[k * 4 + l] = 255; else s1[k * 4 + l - 4] = 255;
            }

            Dictionary<string, List<Transform3D>> trees = null;
            (Vector3 P, float S, int Cell)[] imp = null;
            if (d.Trees != null)
            {
                trees = new Dictionary<string, List<Transform3D>>();
                imp = new (Vector3, float, int)[d.Trees.Count];
                for (int ti = 0; ti < d.Trees.Count; ti++)
                {
                    var t = d.Trees[ti];
                    int v2 = ((int)(t.X * 7f) + (int)(t.Z * 13f)) & 1;
                    // on the mesh this LOD draws, not the smooth function: a coarse far LOD sits metres off it
                    float gy = InfiniteTerrain.MeshHeightAt(d, t.X, t.Z);
                    imp[ti] = (new Vector3(t.X, gy - ResourceField.TreeSink * t.Scale, t.Z), t.Scale, t.Kind * 2 + v2);
                }
                foreach (var t in d.Trees)
                {
                    int variant = ((int)(t.X * 7f) + (int)(t.Z * 13f)) & 1;
                    string name = TreeNames[t.Kind] + "_" + variant;
                    if (!trees.TryGetValue(name, out var list)) trees[name] = list = new List<Transform3D>();
                    var basis = new Basis(Vector3.Up, Mathf.DegToRad(t.Yaw)).Scaled(new Vector3(t.Scale, t.Scale, t.Scale));
                    list.Add(new Transform3D(basis, new Vector3(t.X, t.Y - ResourceField.TreeSink * t.Scale, t.Z)));
                }
            }
            // road surfaces: a ribbon per centreline piece, one mesh per class, sat on the profile (the ground under it
            // is the bed, lower) and lifted only where this LOD's coarser mesh still rises above it
            RoadMesh[] roadMeshes = null;
            if (d.Roads != null && d.Roads.Count > 0)
            {
                roadMeshes = new RoadMesh[RoadSlots];
                var lists = new (List<Vector3> V, List<Vector3> N, List<Vector2> UV, List<int> I)[RoadSlots];
                double ox = d.Coord.MinX, oz = d.Coord.MinZ;
                foreach (var rp in d.Roads)
                {
                    int kind = rp.Kind, slot = !ShowMarks ? kind : rp.Raised ? RaisedSlot : rp.Cut ? CutSlot : kind;
                    lists[slot].V ??= new List<Vector3>(); lists[slot].N ??= new List<Vector3>(); lists[slot].UV ??= new List<Vector2>(); lists[slot].I ??= new List<int>();
                    var RV = lists[slot].V; var RN = lists[slot].N; var RUV = lists[slot].UV; var RI = lists[slot].I;
                    float hw = RibbonHalf((RoadKind)kind), lift = RoadLift[kind], texM = RoadTexMetres[kind];
                    float Y(float lx, float lz, float h) =>
                        Mathf.Max(h + lift, InfiniteTerrain.MeshHeightAt(d, Mathf.Clamp(lx, 0f, InfiniteTerrain.RegionSize), Mathf.Clamp(lz, 0f, InfiniteTerrain.RegionSize)) + 0.02f + lift);
                    float ax = (float)(rp.X0 - ox), az = (float)(rp.Z0 - oz), bx = (float)(rp.X1 - ox), bz = (float)(rp.Z1 - oz);
                    // perpendicular from each END's own tangent, so the next piece builds the identical edge
                    var pa = new Vector2(-rp.T0Z, rp.T0X) * hw;
                    var pb = new Vector2(-rp.T1Z, rp.T1X) * hw;
                    int b0 = RV.Count;
                    RV.Add(new Vector3(ax + pa.X, Y(ax + pa.X, az + pa.Y, rp.H0), az + pa.Y));
                    RV.Add(new Vector3(ax - pa.X, Y(ax - pa.X, az - pa.Y, rp.H0), az - pa.Y));
                    RV.Add(new Vector3(bx + pb.X, Y(bx + pb.X, bz + pb.Y, rp.H1), bz + pb.Y));
                    RV.Add(new Vector3(bx - pb.X, Y(bx - pb.X, bz - pb.Y, rp.H1), bz - pb.Y));
                    for (int q = 0; q < 4; q++) RN.Add(Vector3.Up);
                    RUV.Add(new Vector2(0f, rp.S0 / texM)); RUV.Add(new Vector2(1f, rp.S0 / texM));
                    RUV.Add(new Vector2(0f, rp.S1 / texM)); RUV.Add(new Vector2(1f, rp.S1 / texM));
                    Tri(RV, RI, b0, b0 + 1, b0 + 2, Vector3.Up);
                    Tri(RV, RI, b0 + 1, b0 + 3, b0 + 2, Vector3.Up);
                }
                for (int k = 0; k < RoadSlots; k++)
                    if (lists[k].V != null)
                        roadMeshes[k] = new RoadMesh { V = lists[k].V.ToArray(), N = lists[k].N.ToArray(), UV = lists[k].UV.ToArray(), I = lists[k].I.ToArray() };
            }

            List<(Transform3D, bool, Transform3D)> poles = null;
            if (d.Poles != null && d.Poles.Count > 0)
            {
                poles = new List<(Transform3D, bool, Transform3D)>();
                double ox = d.Coord.MinX, oz = d.Coord.MinZ;
                foreach (var pp in d.Poles)
                {
                    var a = PoleXform((float)(pp.X - ox), pp.H, (float)(pp.Z - oz), pp.DirX, pp.DirZ);
                    var nb = pp.HasNext ? PoleXform((float)(pp.NX - ox), pp.NH, (float)(pp.NZ - oz), pp.NDirX, pp.NDirZ) : Transform3D.Identity;
                    poles.Add((a, pp.HasNext, nb));
                }
            }

            Dictionary<(int, int), List<Transform3D>> foliage = null;
            if (d.Foliage != null)
            {
                foliage = new Dictionary<(int, int), List<Transform3D>>();
                int per = (int)(InfiniteTerrain.RegionSize / FoliageCellSize);
                foreach (var f in d.Foliage)
                {
                    int cell = Mathf.Clamp((int)(f.Z / FoliageCellSize), 0, per - 1) * per + Mathf.Clamp((int)(f.X / FoliageCellSize), 0, per - 1);
                    var key = ((int)f.Kind, cell);
                    if (!foliage.TryGetValue(key, out var list)) foliage[key] = list = new List<Transform3D>();
                    float s = f.Kind == (byte)InfiniteTerrain.FoliageKind.Pebble || f.Kind == (byte)InfiniteTerrain.FoliageKind.PebbleSand ? f.Scale * 0.75f : f.Scale;
                    var basis = new Basis(Vector3.Up, Mathf.DegToRad(f.Yaw)).Scaled(new Vector3(s, s, s));
                    list.Add(new Transform3D(basis, new Vector3(f.X, f.Y, f.Z)));
                }
            }
            return new Built { D = d, V = V.ToArray(), N = N.ToArray(), UV = UV.ToArray(), I = I.ToArray(), S0 = s0, S1 = s1, SplatSize = v, TreeXf = trees, ImpTrees = imp, FoliageXf = foliage,
                               Road = roadMeshes, Poles = poles };
        }

        /// <summary>Add a triangle FRONT-FACING along `front` whichever way round it was written: Godot's front face
        /// is clockwise as seen from the front, so the front normal is (p2-p0)x(p1-p0). Winding is the one thing
        /// in a generated mesh that renders perfectly from one side and vanishes from the other.</summary>
        static void Tri(List<Vector3> V, List<int> I, int a, int b, int c, Vector3 front)
        {
            var nf = (V[c] - V[a]).Cross(V[b] - V[a]);
            if (nf.Dot(front) >= 0f) { I.Add(a); I.Add(b); I.Add(c); }
            else { I.Add(a); I.Add(c); I.Add(b); }
        }
    }
}
