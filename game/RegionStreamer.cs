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
        public int Rebases, Rescues, Committed, TreeCount, FoliageCount, ImpostorCount, BridgeCount, TunnelCount, RailCount;
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
            public List<(Transform3D Tower, Transform3D[] Wired)> PylonXf;   // from the first build (LOD-independent)
            public Node3D Pylons;           // the towers and their wires, every ring
            public Node3D PylonBodies;      // their legs, ring <= ColliderRing
            public Node3D Foliage;          // grass / flowers / pebbles / bushes, ring <= FoliageRing only
            public int FoliageCount;
            public (Vector3 P, float S, int Cell)[] ImpTrees;   // billboard placements on the DISPLAYED LOD's ground
            public MultiMeshInstance3D Impostors;   // ring >= ImpostorRing
            public List<Transform3D>[] BridgeXf;    // [deck, pier, cap], region-local, from the first build (LOD-independent)
            public Node3D Bridges;                  // the bridge MultiMeshes, every ring
            public Node3D BridgeBodies;             // deck colliders, ring <= ColliderRing
            public Vector3[][] RoadCol;             // the road slabs' collision soup, from the latest LOD0 build
            public bool[] Lod0Holes;                // tunnel-mouth holes in the LOD0 ground (NaN in its collider)
            public List<InfiniteRoads.TunnelSpan> TunnelSpans;   // from the first build (LOD-independent)
            public Node3D Tunnels;                  // bores, portals, floors -- every ring
            public List<(Shape3D Shape, Transform3D Xf, int Surf)> TunnelShapes;
            public Node3D TunnelBodies;             // their colliders, ring <= ColliderRing
            public Node3D RoadBodies;               // road colliders, ring <= ColliderRing
            public List<Transform3D>[] RailXf;      // [unit, sleeper, crossbuck], region-local, from the first build (LOD-independent)
            public Node3D Rails;                    // the track MultiMeshes, every ring (culled at RailCull)
            public Node3D RailBodies;               // the ballast's collider, ring <= ColliderRing
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
            public Vector3[][] RoadCol;   // LOD0 only: the slabs as triangle soup, [paved, trail]
            public List<(Transform3D Pole, bool HasNext, Transform3D Next)> Poles;
            public List<(Transform3D Tower, Transform3D[] Wired)> Pylons;
            public (Vector3 P, float S, int Cell)[] ImpTrees;
            public List<Transform3D>[] BridgeXf;
            public List<InfiniteRoads.TunnelSpan> Tunnels;
            public List<Transform3D>[] RailXf;
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
            if (r.Bridges != null) BridgeCount -= r.BridgeXf[0].Count;
            if (r.Pylons != null && _pylonMesh != null) PylonCount -= r.PylonXf.Count;
            if (r.Tunnels != null) TunnelCount -= r.TunnelSpans.Count;
            if (r.Rails != null) RailCount -= r.RailXf[0].Count;
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

            if (ring <= ColliderRing && r.TunnelBodies == null && r.TunnelShapes != null) { r.TunnelBodies = BuildTunnelBodies(r.TunnelShapes); r.Node.AddChild(r.TunnelBodies); }
            else if (ring > ColliderRing + 1 && r.TunnelBodies != null) { r.TunnelBodies.QueueFree(); r.TunnelBodies = null; }
            if (ring <= ColliderRing && r.RoadBodies == null && r.RoadCol != null) { r.RoadBodies = BuildRoadBodies(r.RoadCol); r.Node.AddChild(r.RoadBodies); }
            else if (ring > ColliderRing + 1 && r.RoadBodies != null) { r.RoadBodies.QueueFree(); r.RoadBodies = null; }
            if (ring <= ColliderRing && r.BridgeBodies == null && r.BridgeXf != null && r.BridgeXf[0].Count > 0) { r.BridgeBodies = BuildDeckBodies(r.BridgeXf[0]); r.Node.AddChild(r.BridgeBodies); }
            else if (ring > ColliderRing + 1 && r.BridgeBodies != null) { r.BridgeBodies.QueueFree(); r.BridgeBodies = null; }
            if (ring <= ColliderRing && r.RailBodies == null && r.RailXf != null && r.RailXf[0].Count + r.RailXf[2].Count > 0) { r.RailBodies = BuildRailBodies(r.RailXf[0], r.RailXf[2]); r.Node.AddChild(r.RailBodies); }
            else if (ring > ColliderRing + 1 && r.RailBodies != null) { r.RailBodies.QueueFree(); r.RailBodies = null; }
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
            if (ring <= ColliderRing && r.PylonBodies == null && r.PylonXf != null && r.PylonXf.Count > 0) { r.PylonBodies = BuildPylonBodies(r.PylonXf); r.Node.AddChild(r.PylonBodies); }
            else if (ring > ColliderRing + 1 && r.PylonBodies != null) { r.PylonBodies.QueueFree(); r.PylonBodies = null; }

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
                if (b.D.Lod == 0) { r.Lod0Heights = b.D.Heights; r.Lod0Holes = b.D.Holes; }
                if (b.D.Trees != null && r.TreeList == null) { r.TreeList = b.D.Trees; _pendingTrees[r.C] = b.TreeXf; }
                if (b.FoliageXf != null && r.FoliageXf == null) r.FoliageXf = b.FoliageXf;
                if (b.Poles != null && r.PoleXf == null) r.PoleXf = b.Poles;
                AdoptBridges(r, b);
                AdoptTunnels(r, b);
                AdoptPylons(r, b);
                AdoptRails(r, b);
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
                        r.Road[k] = new MeshInstance3D { Name = k == RaisedSlot ? "Road_Raised" : k == CutSlot ? "Road_Cut" : k == RampSlot ? "Road_Ramp" : k == RailBedSlot ? "Rail_Bed" : "Road_" + (RoadKind)k, MaterialOverride = RoadMat(k), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                        r.Node.AddChild(r.Road[k]);
                    }
                    r.Road[k].Mesh = rm;
                }
                else if (r.Road[k] != null) { r.Road[k].QueueFree(); r.Road[k] = null; }
            }
            // the road colliders follow the LOD0 slabs (a coarser build keeps the last LOD0 soup: its bodies only exist
            // in the ring LOD0 covers); a new soup drops the old bodies for UpdateExtras to rebuild
            if (b.D.Lod == 0)
            {
                r.RoadCol = b.RoadCol;
                if (r.RoadBodies != null) { r.RoadBodies.QueueFree(); r.RoadBodies = null; }
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
            // a tunnel mouth's holes: NaN, which Jolt (and GodotPhysics) read as no collision at that vertex. (Verified: the
            // mouth ray is blocked by GroundBody without them and clear with them, and the rest of the region still
            // collides. A region that once lost its WHOLE collider here had -Infinity heights from a generator bug --
            // see InfiniteRoads.TwinShellTop -- not NaN holes.)
            var data = r.Lod0Heights;
            if (r.Lod0Holes != null)
            {
                data = (float[])data.Clone();
                for (int k = 0; k < data.Length; k++) if (r.Lod0Holes[k]) data[k] = float.NaN;
            }
            var cs = new CollisionShape3D
            {
                Shape = new HeightMapShape3D { MapWidth = v, MapDepth = v, MapData = data },
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
        /// <summary>On/off ramps: road_6 (two lanes, dashed white -- road_8's layout, 256 px over Roads.dat's 8).</summary>
        const string RampTex = "road_6";
        const float RampTexMetres = 256f / 8f;
        /// <summary>UG_INF_MARKS=1: highway pieces on a raised stretch (bridge candidate) draw magenta and on a deep cut
        /// (tunnel candidate) cyan, each from its own slot, so the marking can be checked by eye. Off, they are
        /// ordinary highway.</summary>
        public static bool ShowMarks = System.Environment.GetEnvironmentVariable("UG_INF_MARKS") == "1";
        const int RoadSlots = 8, RaisedSlot = 4, CutSlot = 5, RampSlot = 6, RailBedSlot = 7;
        /// <summary>A rail deck's bed is the ground's own gravel (terrain layer 3), tiled every RailBedMetres.</summary>
        const float RailBedMetres = 4f;
        static readonly Material[] _roadMats = new Material[RoadSlots];
        static Material RoadMat(int slot)
        {
            if (_roadMats[slot] != null) return _roadMats[slot];
            if (slot == RailBedSlot)
            {
                var gi = new Image();
                string gp = ProjectSettings.GlobalizePath("res://content/terrain/layer3.png");
                bool gok = System.IO.File.Exists(gp) && ContentProvider.LoadOk(gi, gp);
                if (gok) gi.GenerateMipmaps();
                return _roadMats[slot] = new StandardMaterial3D
                {
                    AlbedoTexture = gok ? ImageTexture.CreateFromImage(gi) : null, AlbedoColor = gok ? Colors.White : new Color(0.48f, 0.46f, 0.43f),
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic, Roughness = 1f,
                };
            }
            var k = slot == RampSlot ? RoadKind.Small : slot >= RaisedSlot ? RoadKind.Highway : (RoadKind)slot;
            var img = new Image();
            string p = ProjectSettings.GlobalizePath($"res://content/roads/{(slot == RampSlot ? RampTex : RoadTex[(int)k])}.png");
            bool ok = System.IO.File.Exists(p) && ContentProvider.LoadOk(img, p);
            if (ok) img.GenerateMipmaps();
            Material mat;
            if (slot == RaisedSlot || slot == CutSlot)
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

        // ---- high-voltage pylons (strawberry 2026-10-10: "implement the big pylon splines. cross country, their own
        // network"): cow tools' Power_Line_1 lattice tower, strung by the same PowerLineField as its pylon kind (six
        // conductors, insulator pairs, 400 m spans). Towers and wires show at EVERY ring -- a 48 m tower is a landmark.
        static Mesh _pylonMesh; static Material _pylonMat;
        public static int PylonSpansRefused;   // spans PowerLineField would not string (it says why): must stay 0
        public static int PylonCount;
        static void PylonAssets()
        {
            if (_pylonMesh != null) return;
            string odir = ProjectSettings.GlobalizePath("res://content/objects/");
            _pylonMesh = ObjMesh.Load(odir + PowerLineField.PylonMesh + ".obj");
            var mat = new StandardMaterial3D { Roughness = 0.9f };
            var img = new Image();
            if (ContentProvider.LoadOk(img, odir + PowerLineField.PylonMesh + "_tex.png"))
            {
                mat.AlbedoTexture = ImageTexture.CreateFromImage(img);
                mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;   // a palette, like the pole's
            }
            _pylonMat = mat;
        }

        static Transform3D PylonXform(float lx, float y, float lz, float dirX, float dirZ)
        {
            float theta = Mathf.Atan2(-dirX, -dirZ);
            var b = new Basis(Vector3.Up, theta) * new Basis(Vector3.Right, Mathf.DegToRad(270f));
            float k = PowerLineField.PylonScale;
            return new Transform3D(new Basis(b.X * k, b.Y * k, b.Z * k), new Vector3(lx, y, lz));
        }

        /// <summary>For tests: how many towers the loaded region at `c` draws, and its pylon wire field (null if it has none).</summary>
        public (int Towers, PowerLineField Wires) PylonsOf(RegionCoord c)
        {
            if (!_regions.TryGetValue(c, out var r) || r.Pylons == null) return (0, null);
            return (r.Pylons.GetNodeOrNull<MultiMeshInstance3D>("Towers")?.Multimesh?.InstanceCount ?? 0, r.Pylons.GetNodeOrNull<PowerLineField>("PylonWires"));
        }

        void AdoptPylons(Region r, Built b)
        {
            if (b.Pylons == null || r.PylonXf != null) return;
            r.PylonXf = b.Pylons;
            r.Pylons = BuildPylons(r, r.PylonXf);
        }

        Node3D BuildPylons(Region r, List<(Transform3D Tower, Transform3D[] Wired)> towers)
        {
            PylonAssets();
            var holder = new Node3D { Name = "Pylons" };
            r.Node.AddChild(holder);
            float cull = (MaxRing + 0.5f) * InfiniteTerrain.RegionSize;
            if (_pylonMesh != null)
            {
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _pylonMesh };
                mm.InstanceCount = towers.Count;
                for (int k = 0; k < towers.Count; k++) mm.SetInstanceTransform(k, towers[k].Tower);
                var mmi = new MultiMeshInstance3D { Name = "Towers", Multimesh = mm, MaterialOverride = _pylonMat, VisibilityRangeEnd = cull };
                mmi.AddToGroup(NearestFilter.KeepFilterGroup);
                holder.AddChild(mmi);
                PylonCount += towers.Count;
            }
            // the wires, in WORLD space as BuildPower does, culled where the towers are. A far tower in the next region
            // is added here too, as a wire end only (its own region draws it); one pole per spot, so a span between two
            // towers of this region does not end on a duplicate
            var field = new PowerLineField { Name = "PylonWires", PoleCullDistance = cull };
            holder.AddChild(field);
            var toWorld = r.Node.GlobalTransform;
            var at = new Dictionary<Vector3I, int>();
            int Pole(Transform3D x)
            {
                var w = toWorld * x;
                var key = new Vector3I(Mathf.RoundToInt(w.Origin.X * 10f), Mathf.RoundToInt(w.Origin.Y * 10f), Mathf.RoundToInt(w.Origin.Z * 10f));
                if (!at.TryGetValue(key, out int i)) at[key] = i = field.AddPole(w, PowerLineField.PylonMesh);
                return i;
            }
            foreach (var (tower, wired) in towers)
            {
                int a = Pole(tower);
                foreach (var far in wired)
                    if (!field.Connect(a, Pole(far), out string why)) { PylonSpansRefused++; GD.PushWarning($"[pylons] span refused: {why}"); }
            }
            field.Rebuild();
            return holder;
        }

        /// <summary>The tower's four legs where they meet the ground (the lattice base, the mesh's +-2.46 m square),
        /// each a post you walk into; you can walk between them, as under a real tower.</summary>
        static Node3D BuildPylonBodies(List<(Transform3D Tower, Transform3D[] Wired)> towers)
        {
            var holder = new Node3D { Name = "PylonBodies" };
            foreach (var (x, _) in towers)
                foreach (var (cx, cy) in new[] { (-2.46f, -2.46f), (2.46f, -2.46f), (2.46f, 2.46f), (-2.46f, 2.46f) })
                {
                    var foot = x * new Vector3(cx, cy, 0f);
                    var body = new StaticBody3D { CollisionLayer = 1u << 0, Position = foot + Vector3.Up * 5f };
                    body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Metal);
                    body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.45f, Height = 10f } });
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

        // ---- railways (strawberry 2026-10-10: "add railways."): cow tools' rail kit, the way EditorRailSpline lays it --
        // New_Rail_Unit every RailPitch (the generator's TrackOf walk) and a New_Rail_Sleeper closing an open end, one
        // MultiMesh each per region. The ballast is the unit's own, so the collider is ITS cross-section swept per unit.
        // ...and a crossbuck (Crossing_0, a Z-up rip like the roadside pole) on each approach to a level crossing
        static readonly string[] RailProps = { EditorRailSpline.Unit, EditorRailSpline.Sleeper, "Crossing_0" };
        static readonly Mesh[] _railMesh = new Mesh[3];
        static readonly Material[] _railMat = new Material[3];
        /// <summary>Track is drawn out to here (a 2 m tile is a speck past it; the worn formation in the ground's own
        /// splat carries the line on to the horizon).</summary>
        public static float RailCull => (TreeRing + 0.5f) * InfiniteTerrain.RegionSize;
        static void LoadRailKit(int i)
        {
            if (_railMesh[i] != null) return;
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            _railMesh[i] = ObjMesh.Load(dir + RailProps[i] + ".obj");
            var mat = new StandardMaterial3D { Roughness = 0.95f, TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps };
            var img = new Image();
            string tp = dir + RailProps[i] + "_tex.png";
            if (System.IO.File.Exists(tp) && ContentProvider.LoadOk(img, tp)) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }
            else mat.AlbedoColor = new Color(0.45f, 0.42f, 0.38f);
            _railMat[i] = mat;
        }

        /// <summary>A track piece's transform, region-local: EditorRailSpline's basis (Z along the track, grade
        /// included; X = up x Z, level across; Y = Z x X), Z stretched by the piece's K.</summary>
        static Transform3D RailXform(RailPiece p, double ox, double oz)
        {
            var z = new Vector3(p.DX, p.DY, p.DZ).Normalized();
            var x = Vector3.Up.Cross(z).Normalized();
            var y = z.Cross(x);
            return new Transform3D(new Basis(x, y, z * p.K), new Vector3((float)(p.X - ox), p.Y, (float)(p.Z - oz)));
        }

        void AdoptRails(Region r, Built b)
        {
            if (b.RailXf == null || r.RailXf != null) return;
            r.RailXf = b.RailXf;
            if (r.RailXf[0].Count + r.RailXf[1].Count + r.RailXf[2].Count == 0) return;
            r.Rails = new Node3D { Name = "Rails" };
            for (int i = 0; i < 3; i++)
            {
                if (r.RailXf[i].Count == 0) continue;
                LoadRailKit(i);
                if (_railMesh[i] == null) continue;
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _railMesh[i] };
                mm.InstanceCount = r.RailXf[i].Count;   // format BEFORE count
                for (int k = 0; k < r.RailXf[i].Count; k++) mm.SetInstanceTransform(k, r.RailXf[i][k]);
                var mmi = new MultiMeshInstance3D { Name = RailProps[i], Multimesh = mm, MaterialOverride = _railMat[i], VisibilityRangeEnd = RailCull };
                mmi.AddToGroup(NearestFilter.KeepFilterGroup);
                r.Rails.AddChild(mmi);
            }
            r.Node.AddChild(r.Rails);
            RailCount += r.RailXf[0].Count;
        }

        /// <summary>For tests: the tunnels a loaded region built (the holder of their tubes, headwalls, floors), or null.</summary>
        public Node3D TunnelsOf(RegionCoord c) => _regions.TryGetValue(c, out var r) ? r.Tunnels : null;

        /// <summary>For tests: the loaded region's track units (0 if none or not loaded) and its ballast body.</summary>
        public (int Units, int Sleepers, StaticBody3D Ballast) RailsOf(RegionCoord c)
        {
            if (!_regions.TryGetValue(c, out var r) || r.RailXf == null) return (0, 0, null);
            return (r.RailXf[0].Count, r.RailXf[1].Count, r.RailBodies?.GetNodeOrNull<StaticBody3D>("Ballast"));
        }

        /// <summary>The ballast as ONE double-sided trimesh for the region: each unit's top and both sides, from its
        /// root to RailPitch on (times K). The rails (13 cm) are not in it -- you walk on the ballast, and a car crossing
        /// the track bumps over the ballast's slope, which is the shape that matters.</summary>
        static Node3D BuildRailBodies(List<Transform3D> units, List<Transform3D> signs)
        {
            var holder = new Node3D { Name = "RailBodies" };
            // a crossbuck's post: 10 cm square in the mesh, 2.6 m to its top
            foreach (var t in signs)
            {
                var post = new StaticBody3D { Name = "Crossbuck", CollisionLayer = 1u << 0, Position = t.Origin + Vector3.Up * 1.3f };
                post.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Metal);
                post.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.07f, Height = 2.6f } });
                holder.AddChild(post);
            }
            if (units.Count == 0) return holder;
            float ft = InfiniteRoads.RailBallastTopHalf, fb = InfiniteRoads.RailHalfWidth, yt = InfiniteRoads.RailBallastTop, yb = InfiniteRoads.RailBallastFoot;
            var faces = new List<Vector3>(units.Count * 18);
            var sec = new[] { new Vector2(-fb, yb), new Vector2(-ft, yt), new Vector2(ft, yt), new Vector2(fb, yb) };
            foreach (var t in units)
                for (int q = 0; q < 3; q++)
                {
                    var a0 = t * new Vector3(sec[q].X, sec[q].Y, 0f); var b0 = t * new Vector3(sec[q + 1].X, sec[q + 1].Y, 0f);
                    var a1 = t * new Vector3(sec[q].X, sec[q].Y, InfiniteRoads.RailPitch); var b1 = t * new Vector3(sec[q + 1].X, sec[q + 1].Y, InfiniteRoads.RailPitch);
                    faces.Add(a0); faces.Add(b0); faces.Add(a1);
                    faces.Add(b0); faces.Add(b1); faces.Add(a1);
                }
            var body = new StaticBody3D { Name = "Ballast", CollisionLayer = 1u << 0 };
            body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Gravel);
            body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces.ToArray(), BackfaceCollision = true } });
            holder.AddChild(body);
            return holder;
        }

        // ---- bridges (strawberry 2026-10-09: "implementing the bridges"): cow tools' Bridge_Line_1 kit, one MultiMesh per
        // prop per region -- the deck unit repeats hundreds of times, which is exactly what a MultiMesh is for.
        static readonly string[] BridgeProps = { EditorBridgeSpline.DeckUnit, EditorBridgeSpline.PierUnit, EditorBridgeSpline.DeckCap };
        static readonly ArrayMesh[] _bridgeMesh = new ArrayMesh[3];
        static ArrayMesh _deckRender;   // the deck unit minus its baked roadway, which the region's highway mesh draws instead
        public static int DeckRoadwayTrisStripped;
        static readonly Material[] _bridgeMat = new Material[3];
        static Shape3D _deckShape;

        static void LoadBridgeKit(int i)
        {
            if (_bridgeMesh[i] != null) return;
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            _bridgeMesh[i] = ObjMesh.Load(dir + BridgeProps[i] + ".obj");
            var mat = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps };
            var img = new Image();
            string tp = dir + BridgeProps[i] + "_tex.png";
            if (System.IO.File.Exists(tp) && ContentProvider.LoadOk(img, tp)) { img.GenerateMipmaps(); mat.AlbedoTexture = ImageTexture.CreateFromImage(img); }
            else mat.AlbedoColor = new Color(0.60f, 0.58f, 0.55f);
            _bridgeMat[i] = mat;
        }

        // ---- tunnels (strawberry 2026-10-09: "wiring up tunnels to use the tool nyatools made"): cow tools' TunnelMesh
        // sweep, ONE TUBE PER CARRIAGEWAY ("do the separate carriageways as separate tunnels"), each widened across by
        // InfiniteRoads.TunnelLateral. Swept from the BORE chain alone: the carriageways are 17.8 m apart and a shell is
        // 12.6 m from its centre, so each tube's shell would cut through the other's inner lane -- the hill is their
        // outside. For the same reason the Tunnel_Line_Cap_0 portal (bore + shell + facade) cannot stand twice side by
        // side; each mouth gets ONE twin-arch headwall: the two shells' outline with the two bores cut out of it. Plus a
        // FLOOR at the road's bed wall to wall and an APRON over the hole cells at each mouth. Built on the main thread
        // when a region first arrives; colliders ring <= ColliderRing.
        static List<Vector2[]> _boreProfile;
        /// <summary>How far from the route the ground can be missing at a mouth: the hole vertices' reach plus the LOD0 cell
        /// every one of them takes with it, plus a margin.</summary>
        static float TunnelHoleExtent => TunnelHoleExtentOf(RoadKind.Highway);
        static float TunnelHoleExtentOf(RoadKind k) => InfiniteRoads.BoreReachOf(k) + InfiniteRoads.TunnelHoleBeside + InfiniteTerrain.RegionSize / InfiniteTerrain.FullCells + 0.5f;
        /// <summary>The bore chain scaled up by the class's TunnelVerticalOf (a rail's bore is lower than a highway's);
        /// across, the sweep's own lateral factor does it.</summary>
        static readonly Dictionary<RoadKind, List<Vector2[]>> _boreFor = new();
        static List<Vector2[]> BoreProfileFor(RoadKind k)
        {
            float v = InfiniteRoads.TunnelVerticalOf(k);
            if (v == 1f) return _boreProfile;
            if (_boreFor.TryGetValue(k, out var hit)) return hit;
            var scaled = new List<Vector2[]>();
            foreach (var ch in _boreProfile) { var c = new Vector2[ch.Length]; for (int i = 0; i < ch.Length; i++) c[i] = new Vector2(ch[i].X, ch[i].Y * v); scaled.Add(c); }
            return _boreFor[k] = scaled;
        }
        static Material _tunnelMat, _tunnelFloorMat;
        static void LoadTunnelKit()
        {
            if (_boreProfile != null) return;
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            var prof = TunnelMesh.ProfileFrom(ObjMesh.Load(dir + EditorTunnelSpline.BoreUnit + ".obj"));
            float bore = TunnelMesh.BoreHalfWidth(prof);
            _boreProfile = new List<Vector2[]>();
            foreach (var ch in prof)
            {
                float w = 0f; foreach (var q in ch) w = Mathf.Max(w, Mathf.Abs(q.X));
                if (Mathf.Abs(w - bore) < 0.01f) { _boreProfile.Add(ch); break; }
            }
            // EditorObjects.MatFor's recipe for a prop with no texture (the tunnel prop ships none): the mesh's white
            // vertex colours times tan, both faces drawn
            _tunnelMat = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, VertexColorUseAsAlbedo = true,
                                                  AlbedoColor = new Color(0.60f, 0.55f, 0.47f) };
            _tunnelFloorMat = new StandardMaterial3D { Roughness = 1f, AlbedoColor = new Color(0.40f, 0.39f, 0.37f) };
        }

        void AdoptTunnels(Region r, Built b)
        {
            if (b.Tunnels == null || r.TunnelSpans != null) return;
            r.TunnelSpans = b.Tunnels;
            if (b.Tunnels.Count == 0) return;
            LoadTunnelKit();
            r.Tunnels = new Node3D { Name = "Tunnels" };
            r.TunnelShapes = new List<(Shape3D, Transform3D, int)>();
            foreach (var t in b.Tunnels) BuildTunnel(t, b.D.Coord.MinX, b.D.Coord.MinZ, r.Tunnels, r.TunnelShapes);
            r.Node.AddChild(r.Tunnels);
            TunnelCount += b.Tunnels.Count;
        }

        /// <summary>One tunnel, region-local: two tubes, a headwall at each mouth, the floor. Stations are every
        /// TunnelStep of HORIZONTAL arc from the near facade, on the route and on each carriageway.</summary>
        static void BuildTunnel(InfiniteRoads.TunnelSpan t, double ox, double oz, Node3D holder, List<(Shape3D, Transform3D, int)> shapes)
        {
            int m = t.X.Length;
            var kind = t.Kind;   // a highway's two tubes, or a railway's one (InfiniteRoads.TunnelTubes)
            float lat = InfiniteRoads.TunnelLateralOf(kind), feet = InfiniteRoads.TunnelFloorDropOf(kind);
            var boreProfile = BoreProfileFor(kind);
            Vector3 Loc(double x, float y, double z) => new Vector3((float)(x - ox), y, (float)(z - oz));
            var mat = (int)PlayerController.Surf.Concrete;

            // THE TUBES, each on its own carriageway's (or the track's) centreline at the datum, facade to facade
            for (int s = 0; s < t.SX.Length; s++)
            {
                var run = new List<Vector3>(m);
                for (int i = 0; i < m; i++)
                {
                    var p = Loc(t.SX[s][i], t.Y[i], t.SZ[s][i]);
                    if (run.Count == 0 || p.DistanceSquaredTo(run[run.Count - 1]) > 1e-6f) run.Add(p);
                }
                var mesh = run.Count >= 2 ? TunnelMesh.Sweep(boreProfile, run, lat) : null;
                if (mesh == null) continue;
                holder.AddChild(new MeshInstance3D { Name = t.SX.Length == 1 ? "Tube" : s == 0 ? "TubeL" : "TubeR", Mesh = mesh, MaterialOverride = _tunnelMat,
                                                      CastShadow = GeometryInstance3D.ShadowCastingSetting.DoubleSided });
                if (mesh.CreateTrimeshShape() is ConcavePolygonShape3D shp) { shp.BackfaceCollision = true; shapes.Add((shp, Transform3D.Identity, mat)); }
            }

            // THE HEADWALLS: in each mouth's vertical plane, the outline of both shells (their upper envelope, with the
            // outer feet) and, cut up out of its bottom edge, the two bores -- a simple polygon, no holes, because each
            // bore opening reaches the ground
            float reach = InfiniteRoads.ShellReachOf(kind);
            // WINGS past the shells as far as the ground the mouth's holes take out: the hole vertices reach TunnelHoleBeside
            // past the bores, and every cell touching one goes, a further cell (4 m) out -- so the cut slope's missing cells
            // beside the portal are backed by a wall, not open to the sky
            float wing = TunnelHoleExtentOf(kind), wingTop = InfiniteRoads.ShellTopOf(kind, reach) + InfiniteRoads.HeadwallCover;
            var bore = boreProfile[0];
            bool boreRightToLeft = bore[0].X > bore[bore.Length - 1].X;
            var outline = new List<Vector2>();
            outline.Add(new Vector2(-wing, -feet));
            outline.Add(new Vector2(-wing, wingTop));
            for (float u = -reach; u <= reach + 1e-3f; u += 0.25f) outline.Add(new Vector2(u, InfiniteRoads.ShellTopOf(kind, Mathf.Clamp(u, -reach, reach))));
            outline.Add(new Vector2(wing, wingTop));
            outline.Add(new Vector2(wing, -feet));
            for (int s = t.SX.Length - 1; s >= 0; s--)   // right tube first: the path runs back along the bottom from right to left
            {
                float c = InfiniteRoads.TubeOffset(kind, s);
                for (int k = 0; k < bore.Length; k++)
                {
                    var q = bore[boreRightToLeft ? k : bore.Length - 1 - k];
                    outline.Add(new Vector2(c + q.X * lat, q.Y));
                }
            }
            var poly = outline.ToArray();
            var tri = Geometry2D.TriangulatePolygon(poly);
            if (tri.Length == 0) Log.Err($"[infinite] tunnel headwall did not triangulate ({poly.Length} points)");
            for (int end = 0; end < 2 && tri.Length > 0; end++)
            {
                int a = end == 0 ? 0 : m - 1, b = end == 0 ? 1 : m - 2;
                var pa = Loc(t.X[a], t.Y[a], t.Z[a]);
                var fwd = Loc(t.X[b], t.Y[b], t.Z[b]) - pa; fwd.Y = 0f; fwd = fwd.Normalized();
                // +u is the +offset carriageway's side whichever end: fwd points INTO the tunnel, so at the far end it runs
                // against the route and its left-hand normal is the other side
                var side = new Vector3(-fwd.Z, 0f, fwd.X);
                if (end == 1) side = -side;
                var WV = new Vector3[poly.Length]; var WN = new Vector3[poly.Length];
                var outward = end == 0 ? -fwd : fwd;
                for (int k = 0; k < poly.Length; k++) { WV[k] = pa + side * poly[k].X + Vector3.Up * poly[k].Y; WN[k] = outward; }
                var WI = new List<int>(tri.Length);
                var tmpV = new List<Vector3>(WV);
                for (int k = 0; k + 2 < tri.Length; k += 3) Tri(tmpV, WI, tri[k], tri[k + 1], tri[k + 2], outward);
                var wa = new Godot.Collections.Array();
                wa.Resize((int)Mesh.ArrayType.Max);
                var cols = new Color[WV.Length]; for (int k = 0; k < cols.Length; k++) cols[k] = Colors.White;
                wa[(int)Mesh.ArrayType.Vertex] = WV; wa[(int)Mesh.ArrayType.Normal] = WN; wa[(int)Mesh.ArrayType.Color] = cols;
                wa[(int)Mesh.ArrayType.Index] = WI.ToArray();
                var wm = new ArrayMesh();
                wm.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, wa);
                holder.AddChild(new MeshInstance3D { Name = end == 0 ? "HeadwallIn" : "HeadwallOut", Mesh = wm, MaterialOverride = _tunnelMat,
                                                      CastShadow = GeometryInstance3D.ShadowCastingSetting.DoubleSided });
                var wsoup = new Vector3[WI.Count];
                for (int k = 0; k < WI.Count; k++) wsoup[k] = WV[WI[k]];
                shapes.Add((new ConcavePolygonShape3D { Data = wsoup, BackfaceCollision = true }, Transform3D.Identity, mat));

                // THE COLLAR: a concrete lid over the hole band behind the headwall, at the height the hill's cap would have
                // (InfiniteRoads.TunnelGround: the shells + HeadwallCover, rising HeadwallSlope past the holes) -- the ground
                // there is gone so the mouth can be, and from above the gap reads as the portal's own top instead of a slot
                // of sky. It is over the tubes, so it closes nothing you drive through.
                var inward = fwd;
                int nu = Mathf.CeilToInt(2f * wing / 1f), na = Mathf.CeilToInt((InfiniteRoads.TunnelHoleIn + 4.5f) / 1f);
                var LV = new List<Vector3>(); var LN = new List<Vector3>(); var LC = new List<Color>(); var LI = new List<int>();
                for (int ia = 0; ia <= na; ia++)
                    for (int iu = 0; iu <= nu; iu++)
                    {
                        float aIn = (InfiniteRoads.TunnelHoleIn + 4.5f) * ia / na, u = -wing + 2f * wing * iu / nu;
                        float top = Mathf.Max(InfiniteRoads.ShellTopOf(kind, Mathf.Clamp(u, -reach, reach)), wingTop - InfiniteRoads.HeadwallCover)
                                    + InfiniteRoads.HeadwallCover + Mathf.Max(0f, aIn - InfiniteRoads.TunnelHoleIn) * InfiniteRoads.HeadwallSlope;
                        LV.Add(pa + inward * aIn + side * u + Vector3.Up * top); LN.Add(Vector3.Up); LC.Add(Colors.White);
                    }
                for (int ia = 0; ia < na; ia++)
                    for (int iu = 0; iu < nu; iu++)
                    {
                        int q0 = ia * (nu + 1) + iu, q1 = q0 + 1, q2 = q0 + nu + 1, q3 = q2 + 1;
                        Tri(LV, LI, q0, q1, q2, Vector3.Up);
                        Tri(LV, LI, q1, q3, q2, Vector3.Up);
                    }
                var la = new Godot.Collections.Array();
                la.Resize((int)Mesh.ArrayType.Max);
                la[(int)Mesh.ArrayType.Vertex] = LV.ToArray(); la[(int)Mesh.ArrayType.Normal] = LN.ToArray(); la[(int)Mesh.ArrayType.Color] = LC.ToArray();
                la[(int)Mesh.ArrayType.Index] = LI.ToArray();
                var lm = new ArrayMesh();
                lm.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, la);
                holder.AddChild(new MeshInstance3D { Name = end == 0 ? "CollarIn" : "CollarOut", Mesh = lm, MaterialOverride = _tunnelMat });
                var lsoup = new Vector3[LI.Count];
                for (int k = 0; k < LI.Count; k++) lsoup[k] = LV[LI[k]];
                shapes.Add((new ConcavePolygonShape3D { Data = lsoup, BackfaceCollision = true }, Transform3D.Identity, mat));
            }

            // THE FLOOR, at the road's bed (the approach ground's level beside the slab), wall to wall across both tubes;
            // and an APRON at each mouth over the hole cells -- they reach a cell out in front of the facade and
            // TunnelHoleBeside past the outer walls
            var c3 = new Vector3[m]; var h = new float[m];
            for (int i = 0; i < m; i++) { c3[i] = Loc(t.X[i], t.Y[i], t.Z[i]); if (i > 0) h[i] = h[i - 1] + new Vector2(c3[i].X - c3[i - 1].X, c3[i].Z - c3[i - 1].Z).Length(); }
            float len = h[m - 1];
            Vector3 At(float s)
            {
                s = Mathf.Clamp(s, 0f, len);
                int k = 0; while (k < m - 2 && h[k + 1] < s) k++;
                return c3[k].Lerp(c3[k + 1], Mathf.Clamp((s - h[k]) / Mathf.Max(1e-6f, h[k + 1] - h[k]), 0f, 1f));
            }
            var FV = new List<Vector3>(); var FN = new List<Vector3>(); var FI = new List<int>();
            float drop = InfiniteRoads.TunnelBedBelow(kind);
            float boreR = InfiniteRoads.BoreReachOf(kind), apronW = TunnelHoleExtentOf(kind), apronOut = 6.5f;
            void Strip(float sA, float sB, float half, float below, int steps)
            {
                for (int q = 0; q < steps; q++)
                {
                    float u0 = sA + (sB - sA) * q / steps, u1 = sA + (sB - sA) * (q + 1) / steps;
                    Vector3 P(float s)
                    {
                        // beyond either facade the line runs straight on at the end's grade
                        if (s < 0f) { var d0 = At(Mathf.Min(2f, len)) - At(0f); return At(0f) + d0 / Mathf.Max(1e-6f, new Vector2(d0.X, d0.Z).Length()) * s; }
                        if (s > len) { var d1 = At(len) - At(Mathf.Max(0f, len - 2f)); return At(len) + d1 / Mathf.Max(1e-6f, new Vector2(d1.X, d1.Z).Length()) * (s - len); }
                        return At(s);
                    }
                    Vector3 pa = P(u0), pb = P(u1);
                    var dir = new Vector3(pb.X - pa.X, 0f, pb.Z - pa.Z).Normalized();
                    var nrm = new Vector3(-dir.Z, 0f, dir.X) * half;
                    var down = Vector3.Down * (drop + below);
                    int i0 = FV.Count;
                    FV.Add(pa + nrm + down); FV.Add(pa - nrm + down); FV.Add(pb + nrm + down); FV.Add(pb - nrm + down);
                    for (int k = 0; k < 4; k++) FN.Add(Vector3.Up);
                    Tri(FV, FI, i0, i0 + 1, i0 + 2, Vector3.Up);
                    Tri(FV, FI, i0 + 1, i0 + 3, i0 + 2, Vector3.Up);
                }
            }
            Strip(-apronOut, len + apronOut, boreR, 0.03f, Mathf.Max(1, Mathf.CeilToInt((len + 2f * apronOut) / InfiniteRoads.TunnelStep)));
            Strip(-apronOut, 0.5f, apronW, 0.04f, 4);
            Strip(len - 0.5f, len + apronOut, apronW, 0.04f, 4);
            var fa = new Godot.Collections.Array();
            fa.Resize((int)Mesh.ArrayType.Max);
            fa[(int)Mesh.ArrayType.Vertex] = FV.ToArray(); fa[(int)Mesh.ArrayType.Normal] = FN.ToArray(); fa[(int)Mesh.ArrayType.Index] = FI.ToArray();
            var fm = new ArrayMesh();
            fm.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, fa);
            holder.AddChild(new MeshInstance3D { Name = "Floor", Mesh = fm, MaterialOverride = _tunnelFloorMat });
            var soup = new Vector3[FI.Count];
            for (int k = 0; k < FI.Count; k++) soup[k] = FV[FI[k]];
            shapes.Add((new ConcavePolygonShape3D { Data = soup, BackfaceCollision = true }, Transform3D.Identity, mat));
        }

        static Node3D BuildTunnelBodies(List<(Shape3D Shape, Transform3D Xf, int Surf)> shapes)
        {
            var holder = new Node3D { Name = "TunnelBodies" };
            foreach (var (shape, xf, surf) in shapes)
            {
                var body = new StaticBody3D { CollisionLayer = 1u << 0, Transform = xf };
                body.SetMeta(PlayerController.SurfMeta, surf);
                body.AddChild(new CollisionShape3D { Shape = shape });
                holder.AddChild(body);
            }
            return holder;
        }

        /// <summary>The deck unit without the faces of its roadway (every triangle lying in the roadway plane between the
        /// parapets' feet), for DRAWING only -- its collider keeps them. Axes read off the mesh's own bounds (the
        /// 17 m one is across, the 5.25 m one is up) rather than assumed, because ObjMesh's import convention flips one.</summary>
        static ArrayMesh DeckRenderMesh()
        {
            if (_deckRender != null) return _deckRender;
            var src = _bridgeMesh[0];
            var size = src.GetAabb().Size;
            int lat = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2, up = -1;
            for (int a = 0; a < 3; a++)
                if (a != lat && Mathf.Abs(size[a] - EditorBridgeSpline.DeckThickness) < 0.05f) up = a;
            if (up < 0) { Log.Print($"[infinite] deck unit bounds {size} have no {EditorBridgeSpline.DeckThickness} m axis: drawing its own roadway"); return _deckRender = src; }
            var dst = new ArrayMesh();
            for (int s = 0; s < src.GetSurfaceCount(); s++)
            {
                var arr = src.SurfaceGetArrays(s);
                var v = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var iv = arr[(int)Mesh.ArrayType.Index];
                int[] idx;
                if (iv.VariantType != Variant.Type.Nil) idx = iv.AsInt32Array();
                else { idx = new int[v.Length]; for (int k = 0; k < idx.Length; k++) idx[k] = k; }
                var keep = new List<int>(idx.Length);
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    bool roadway = true;
                    for (int q = 0; q < 3; q++)
                    {
                        var p = v[idx[t + q]];
                        if (Mathf.Abs(p[up]) > 0.01f || Mathf.Abs(p[lat]) > InfiniteRoads.DeckRoadwayHalf + 0.01f) roadway = false;
                    }
                    if (roadway) DeckRoadwayTrisStripped++;
                    else { keep.Add(idx[t]); keep.Add(idx[t + 1]); keep.Add(idx[t + 2]); }
                }
                arr[(int)Mesh.ArrayType.Index] = keep.ToArray();
                dst.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            }
            Log.Print($"[infinite] deck unit: {DeckRoadwayTrisStripped} roadway triangles stripped (the highway mesh draws the deck's roadway)");
            return _deckRender = dst;
        }

        /// <summary>Take a region's bridge pieces from its first build (they do not depend on LOD) and draw them. BOTH
        /// paths that commit a build call this -- the streaming commit and SyncGround -- because SyncGround is what a
        /// spawn or teleport uses for the 3x3 under the player, and it never re-commits those regions afterwards.</summary>
        void AdoptBridges(Region r, Built b)
        {
            if (b.BridgeXf == null || r.BridgeXf != null) return;
            r.BridgeXf = b.BridgeXf;
            if (r.BridgeXf[0].Count + r.BridgeXf[1].Count + r.BridgeXf[2].Count > 0) { r.Bridges = BuildBridges(r.BridgeXf); r.Node.AddChild(r.Bridges); }
        }

        Node3D BuildBridges(List<Transform3D>[] xf)
        {
            var holder = new Node3D { Name = "Bridges" };
            for (int i = 0; i < 3; i++)
            {
                if (xf[i].Count == 0) continue;
                LoadBridgeKit(i);
                if (_bridgeMesh[i] == null) continue;
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = i == 0 ? DeckRenderMesh() : _bridgeMesh[i] };
                mm.InstanceCount = xf[i].Count;   // format BEFORE count
                for (int k = 0; k < xf[i].Count; k++) mm.SetInstanceTransform(k, xf[i][k]);
                var mmi = new MultiMeshInstance3D { Name = BridgeProps[i], Multimesh = mm, MaterialOverride = _bridgeMat[i] };
                mmi.AddToGroup(NearestFilter.KeepFilterGroup);
                holder.AddChild(mmi);
            }
            BridgeCount += xf[0].Count;
            return holder;
        }

        /// <summary>The region's road slabs as colliders: one closed, double-sided trimesh per surface (RoadField's
        /// BackfaceCollision rule -- nothing slips under an edge), asphalt and the dirt trail tagged apart for footsteps
        /// and tyres. Before this a car drove on the ground 0.12 m under the drawn road.</summary>
        static Node3D BuildRoadBodies(Vector3[][] soup)
        {
            var holder = new Node3D { Name = "RoadBodies" };
            for (int i = 0; i < soup.Length; i++)
            {
                if (soup[i] == null || soup[i].Length < 3) continue;
                var body = new StaticBody3D { Name = i == 0 ? "Paved" : "Trail", CollisionLayer = 1u << 0 };
                body.SetMeta(PlayerController.SurfMeta, (int)(i == 0 ? PlayerController.Surf.Concrete : PlayerController.Surf.Dirt));
                body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = soup[i], BackfaceCollision = true } });
                holder.AddChild(body);
            }
            return holder;
        }

        /// <summary>The deck's collision shape widened across (local X) by `ws`, for a main's overpass decks.</summary>
        static readonly Dictionary<int, ConcavePolygonShape3D> _wideDeck = new();
        static ConcavePolygonShape3D WideDeckShape(float ws)
        {
            int key = Mathf.RoundToInt(ws * 1000f);
            if (_wideDeck.TryGetValue(key, out var hit)) return hit;
            var faces = ((ConcavePolygonShape3D)_deckShape).Data;
            for (int i = 0; i < faces.Length; i++) faces[i].X *= ws;
            var shape = new ConcavePolygonShape3D { Data = faces, BackfaceCollision = ((ConcavePolygonShape3D)_deckShape).BackfaceCollision };
            _wideDeck[key] = shape;
            return shape;
        }

        /// <summary>One trimesh body per deck unit, so a car drives across. Shared shape: every unit is the same prop.</summary>
        static Node3D BuildDeckBodies(List<Transform3D> decks)
        {
            LoadBridgeKit(0);
            var holder = new Node3D { Name = "BridgeBodies" };
            if (_bridgeMesh[0] == null) return holder;
            _deckShape ??= _bridgeMesh[0].CreateTrimeshShape();
            foreach (var t in decks)
            {
                // a widened (main) deck: a body takes no scale, so the SHAPE is widened and the body keeps a pure rotation
                float ws = t.Basis.X.Length();
                bool wide = Mathf.Abs(ws - 1f) >= 1e-3f;
                var shape = wide ? WideDeckShape(ws) : _deckShape;
                var body = new StaticBody3D { CollisionLayer = 1u << 0, Transform = wide ? new Transform3D(new Basis(t.Basis.X / ws, t.Basis.Y, t.Basis.Z), t.Origin) : t };
                body.SetMeta(PlayerController.SurfMeta, (int)PlayerController.Surf.Concrete);
                body.AddChild(new CollisionShape3D { Shape = shape });
                holder.AddChild(body);
            }
            return holder;
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
            RainRoofMap.Shift(d);   // its cache is filed by engine-space cell: re-file it, or the rain stops at roofs 2 km away
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
            // what they should be standing ON: inside a tunnel the hill is overhead and the floor is the tunnel's
            float g = Src.WalkableHeightAt(AbsX(p.X), AbsZ(p.Z));
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
                    r.Lod0Heights = b.D.Heights; r.Lod0Holes = b.D.Holes;
                    r.TreeList = b.D.Trees; _pendingTrees[c] = b.TreeXf;
                    r.FoliageXf ??= b.FoliageXf;
                    r.PoleXf ??= b.Poles;
                    AdoptBridges(r, b);   // ⚠ this path is how a teleport builds the 3x3 -- miss it and the bridges beside you never appear
                    AdoptTunnels(r, b);
                    AdoptPylons(r, b);
                    AdoptRails(r, b);
                    Apply(r, b);
                    r.ImpTrees = b.ImpTrees;
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
                    if (d.Holes != null && (d.Holes[a] || d.Holes[b] || d.Holes[c] || d.Holes[e])) continue;   // a tunnel mouth
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
                    // not over a tunnel: the hill's height hung skirt-deep is a wall straight across the bore (and from a
                    // mouth's hole, across the mouth)
                    if (d.OverTunnel != null && (d.OverTunnel[a] || d.OverTunnel[b])) continue;
                    if (d.Holes != null && (d.Holes[a] || d.Holes[b])) continue;
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
            // road surfaces: a SLAB per centreline piece, RoadField's own cross-section -- a flat top at
            // InfiniteRoads.SurfaceY and a bevel each side running down 2 x Thickness, so the edge stands PEI's height
            // and its foot is buried -- but out over BevelRun, a 1:3 slope (InfiniteRoads.BevelSlope) rather than RoadField's 45-degree kerb (strawberry 2026-10-09: "give the road splines actual collision and the
            // proper thickness (vertical height)"). Lifted only where this LOD's coarser mesh still rises above it. One
            // mesh per class; at LOD0 the same faces, closed underneath as RoadField closes its collider, are the
            // region's road collision.
            RoadMesh[] roadMeshes = null;
            Vector3[][] roadCol = null;
            bool decks = d.Bridges != null && d.Bridges.Exists(bp => bp.Kind == 0);
            if (d.Roads != null && d.Roads.Count > 0 || decks)
            {
                roadMeshes = new RoadMesh[RoadSlots];
                var lists = new (List<Vector3> V, List<Vector3> N, List<Vector2> UV, List<int> I)[RoadSlots];
                var soup = d.Lod == 0 ? new[] { new List<Vector3>(), new List<Vector3>() } : null;   // [paved, trail]
                double ox = d.Coord.MinX, oz = d.Coord.MinZ;
                // THE DECKS' ROADWAY is the carriageway's own surface (strawberry 2026-10-09: "the bridge road is the
                // highway 0 type. and the highway is highway 1"): the cut unit's baked two-way paint is stripped from
                // its mesh (DeckRenderMesh) and each unit gets a Highway_1 strip lying exactly on its roadway plane, in
                // this region's highway mesh -- no extra instances -- with v from the unit's S0..S1, the ribbon's own
                // measure, so the dashes run on from the approach instead of restarting at the bridge. Across the
                // 13.8 m carriageway u runs 0..1 as on the ribbon; the deck's extra 1.1 m each side wears the edge
                // column (plain asphalt), as a ribbon bevel does.
                // A MAIN's overpass deck is the same unit widened (DeckScale) to the main's full asphalt, and wears the
                // main's surface in the main's mesh the same way.
                // A RAIL's deck is the unit narrowed to RailDeckHalf, and its roadway is a gravel bed (RailBedSlot) the
                // track's own ballast stands on -- the same plane the track's root is at (InfiniteRoads.RailOriginY).
                void RailBed(BridgePiece bp)
                {
                    var L = lists[RailBedSlot];
                    L.V ??= new List<Vector3>(); L.N ??= new List<Vector3>(); L.UV ??= new List<Vector2>(); L.I ??= new List<int>();
                    lists[RailBedSlot] = L;
                    var dir = new Vector3(bp.DX, bp.DY, bp.DZ).Normalized();
                    var c = new Vector3((float)(bp.X - ox), (float)bp.Y, (float)(bp.Z - oz));
                    var nrm = new Vector3(-dir.Z, 0f, dir.X).Normalized();
                    var along = dir * (InfiniteRoads.BridgePitch * 0.5f);
                    var up = nrm.Cross(dir).Normalized(); if (up.Y < 0f) up = -up;
                    float w = InfiniteRoads.RailDeckHalf, v0 = bp.S0 / RailBedMetres, v1 = bp.S1 / RailBedMetres, u1 = 2f * w / RailBedMetres;
                    int i0 = L.V.Count;
                    L.V.Add(c - along + nrm * w); L.V.Add(c - along - nrm * w); L.V.Add(c + along + nrm * w); L.V.Add(c + along - nrm * w);
                    for (int q = 0; q < 4; q++) L.N.Add(up);
                    L.UV.Add(new Vector2(0f, v0)); L.UV.Add(new Vector2(u1, v0)); L.UV.Add(new Vector2(0f, v1)); L.UV.Add(new Vector2(u1, v1));
                    Tri(L.V, L.I, i0, i0 + 1, i0 + 2, up);
                    Tri(L.V, L.I, i0 + 1, i0 + 3, i0 + 2, up);
                    if (soup != null) for (int q = 0; q < 6; q++) soup[0].Add(L.V[L.I[L.I.Count - 6 + q]]);
                }
                if (decks)
                {
                    foreach (var bp in d.Bridges)
                    {
                        if (bp.Kind != 0) continue;
                        if (bp.Road == (byte)RoadKind.Rail) { RailBed(bp); continue; }
                        int hs = bp.Road;
                        lists[hs].V ??= new List<Vector3>(); lists[hs].N ??= new List<Vector3>(); lists[hs].UV ??= new List<Vector2>(); lists[hs].I ??= new List<int>();
                        var DV = lists[hs].V; var DN = lists[hs].N; var DUV = lists[hs].UV; var DI = lists[hs].I;
                        float texM = RoadTexMetres[hs], lane = RibbonHalf((RoadKind)hs), wide = InfiniteRoads.DeckRoadwayHalf * InfiniteRoads.DeckScale((RoadKind)hs);
                        var dir = new Vector3(bp.DX, bp.DY, bp.DZ).Normalized();
                        var c = new Vector3((float)(bp.X - ox), (float)bp.Y, (float)(bp.Z - oz));
                        var nrm = new Vector3(-dir.Z, 0f, dir.X).Normalized();       // the ribbon's u = 0 side
                        var along = dir * (InfiniteRoads.BridgePitch * 0.5f);
                        var up = nrm.Cross(dir).Normalized(); if (up.Y < 0f) up = -up;
                        float v0 = bp.S0 / texM, v1 = bp.S1 / texM;
                        // three bands across: shoulder (u 0), the carriageway (u 0..1), shoulder (u 1)
                        float[] o = { wide, lane, -lane, -wide }; float[] u = { 0f, 0f, 1f, 1f };
                        for (int band = 0; band < 3; band++)
                        {
                            if (o[band] - o[band + 1] < 1e-3f) continue;   // a main's roadway is all carriageway: no shoulder bands
                            int i0 = DV.Count;
                            DV.Add(c - along + nrm * o[band]); DV.Add(c - along + nrm * o[band + 1]);
                            DV.Add(c + along + nrm * o[band]); DV.Add(c + along + nrm * o[band + 1]);
                            for (int q = 0; q < 4; q++) DN.Add(up);
                            DUV.Add(new Vector2(u[band], v0)); DUV.Add(new Vector2(u[band + 1], v0));
                            DUV.Add(new Vector2(u[band], v1)); DUV.Add(new Vector2(u[band + 1], v1));
                            Tri(DV, DI, i0, i0 + 1, i0 + 2, up);
                            Tri(DV, DI, i0 + 1, i0 + 3, i0 + 2, up);
                            // and it is solid in its own right: the driven surface does not depend on the cut unit
                            // keeping a roadway face its owner may strip from the asset
                            if (soup != null) for (int q = 0; q < 6; q++) soup[0].Add(DV[DI[DI.Count - 6 + q]]);
                        }
                    }
                }
                foreach (var rp in d.Roads ?? new List<RoadPiece>())
                {
                    // a RAMP wears road_6, the two-lane road with a dashed WHITE divider (strawberry 2026-10-10: "its a 2
                    // lane white dotted one"); its slab is a small road's, which road_8 -- the same road, yellow -- dresses
                    int kind = rp.Kind, slot = rp.Ramp ? RampSlot : !ShowMarks ? kind : rp.Raised ? RaisedSlot : rp.Cut ? CutSlot : kind;
                    lists[slot].V ??= new List<Vector3>(); lists[slot].N ??= new List<Vector3>(); lists[slot].UV ??= new List<Vector2>(); lists[slot].I ??= new List<int>();
                    var RV = lists[slot].V; var RN = lists[slot].N; var RUV = lists[slot].UV; var RI = lists[slot].I;
                    var col = soup?[kind == (int)RoadKind.Trail ? 1 : 0];
                    var rk = (RoadKind)kind;
                    float hw = RibbonHalf(rk), lift = InfiniteRoads.Lift(rk), texM = rp.Ramp ? RampTexMetres : RoadTexMetres[kind], bev = 2f * InfiniteRoads.Thickness(rk), run = InfiniteRoads.BevelRun(rk);
                    // under a tunnel the ground mesh is the HILL: never lift the slab onto it
                    bool inTunnel = rp.InTunnel;
                    float Y(float lx, float lz, float h) => inTunnel ? InfiniteRoads.SurfaceY(rk, h) :
                        Mathf.Max(InfiniteRoads.SurfaceY(rk, h), InfiniteTerrain.MeshHeightAt(d, Mathf.Clamp(lx, 0f, InfiniteTerrain.RegionSize), Mathf.Clamp(lz, 0f, InfiniteTerrain.RegionSize)) + 0.02f + lift);
                    var A = new Vector3((float)(rp.X0 - ox), 0f, (float)(rp.Z0 - oz));
                    var B = new Vector3((float)(rp.X1 - ox), 0f, (float)(rp.Z1 - oz));
                    // perpendicular from each END's own tangent, so the next piece builds the identical edge
                    var na = new Vector3(-rp.T0Z, 0f, rp.T0X);
                    var nb = new Vector3(-rp.T1Z, 0f, rp.T1X);
                    Vector3 Edge(Vector3 c, Vector3 n, float w, float h) { var p = c + n * w; p.Y = Y(p.X, p.Z, h); return p; }
                    var down = new Vector3(0f, -bev, 0f);
                    // the lane edges, and the OUTER edges -- wider than the lanes only at a bridge's mouth, where the
                    // ribbon widens to its deck's roadway (RoadPiece.W0/W1) with a plain-asphalt shoulder
                    Vector3 la = Edge(A, na, hw, rp.H0), ra = Edge(A, -na, hw, rp.H0), lb = Edge(B, nb, hw, rp.H1), rb = Edge(B, -nb, hw, rp.H1);
                    bool widened = rp.W0 > 1e-3f || rp.W1 > 1e-3f;
                    Vector3 laW = widened ? Edge(A, na, hw + rp.W0, rp.H0) : la, raW = widened ? Edge(A, -na, hw + rp.W0, rp.H0) : ra;
                    Vector3 lbW = widened ? Edge(B, nb, hw + rp.W1, rp.H1) : lb, rbW = widened ? Edge(B, -nb, hw + rp.W1, rp.H1) : rb;
                    // the edge bevels: down the slab's depth over BevelRun outward (1:3; RoadField's 1:1 was a kerb)
                    Vector3 loa = laW + na * run + down, roa = raW - na * run + down, lob = lbW + nb * run + down, rob = rbW - nb * run + down;
                    float v0 = rp.S0 / texM, v1 = rp.S1 / texM;

                    // a quad between an A-row (a0, a1) and a B-row (b0, b1): to the visual mesh if `nA` is given, and
                    // always to the collision soup
                    void Quad(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1, Vector3? nA, Vector3 nB, float u0, float u1, float va, float vb)
                    {
                        if (nA is Vector3 n0)
                        {
                            int i0 = RV.Count;
                            RV.Add(a0); RV.Add(a1); RV.Add(b0); RV.Add(b1);
                            RN.Add(n0); RN.Add(n0); RN.Add(nB); RN.Add(nB);
                            RUV.Add(new Vector2(u0, va)); RUV.Add(new Vector2(u1, va)); RUV.Add(new Vector2(u0, vb)); RUV.Add(new Vector2(u1, vb));
                            var front = (n0 + nB).Normalized();
                            Tri(RV, RI, i0, i0 + 1, i0 + 2, front);
                            Tri(RV, RI, i0 + 1, i0 + 3, i0 + 2, front);
                        }
                        if (col == null) return;
                        col.Add(a0); col.Add(a1); col.Add(b0);
                        col.Add(a1); col.Add(b1); col.Add(b0);
                    }
                    Vector3 Lean(Vector3 n) => (n * (bev / run) + Vector3.Up).Normalized();   // the bevel's own slope
                    Quad(la, ra, lb, rb, Vector3.Up, Vector3.Up, 0f, 1f, v0, v1);                    // the driven top
                    if (widened)
                    {
                        // the mouth's shoulders wear the edge column, as the deck's own shoulder bands do
                        Quad(laW, la, lbW, lb, Vector3.Up, Vector3.Up, 0f, 0f, v0, v1);
                        Quad(ra, raW, rb, rbW, Vector3.Up, Vector3.Up, 1f, 1f, v0, v1);
                    }
                    // the bevels wear the texture's edge column, as RoadField's do (u 0 left, 1 right)
                    Quad(loa, laW, lob, lbW, Lean(na), Lean(nb), 0f, 0f, v0, v1);
                    Quad(raW, roa, rbW, rob, Lean(-na), Lean(-nb), 1f, 1f, v0, v1);
                    Quad(loa, roa, lob, rob, null, Vector3.Down, 0f, 0f, 0f, 0f);                    // collider only: sealed underneath
                    // the line's own ends ramp down, RoadField's end caps: the whole cross-section pushed out by the bevel
                    // and dropped to its foot, so a road that stops in the open is a slope, not a step
                    if (rp.OpenStart) EndRamp(A, laW, raW, loa, roa, new Vector3(-rp.T0X, 0f, -rp.T0Z), v0);
                    if (rp.OpenEnd) EndRamp(B, lbW, rbW, lob, rob, new Vector3(rp.T1X, 0f, rp.T1Z), v1);
                    void EndRamp(Vector3 c, Vector3 l, Vector3 r, Vector3 lo, Vector3 ro, Vector3 outDir, float v)
                    {
                        var o = outDir * run;
                        Vector3 l2 = l + o + down, r2 = r + o + down, lo2 = lo + o, ro2 = ro + o;
                        var nOut = Lean(outDir);
                        Quad(l, r, l2, r2, nOut, nOut, 0f, 1f, v, v);
                        Quad(lo, l, lo2, l2, nOut, nOut, 0f, 0f, v, v);
                        Quad(r, ro, r2, ro2, nOut, nOut, 1f, 1f, v, v);
                    }
                }
                for (int k = 0; k < RoadSlots; k++)
                    if (lists[k].V != null)
                        roadMeshes[k] = new RoadMesh { V = lists[k].V.ToArray(), N = lists[k].N.ToArray(), UV = lists[k].UV.ToArray(), I = lists[k].I.ToArray() };
                if (soup != null) roadCol = new[] { soup[0].ToArray(), soup[1].ToArray() };
            }

            // bridges: the bridge TOOL's own bases (EditorBridgeSpline.DeckBasis / StandBasis) on the core's walk, so the
            // infinite world lays the kit exactly the way the editor does -- ripped props, Z-up, length on local +Y
            List<Transform3D>[] bridgeXf = null;
            if (d.Bridges != null)
            {
                bridgeXf = new[] { new List<Transform3D>(), new List<Transform3D>(), new List<Transform3D>() };
                double bx0 = d.Coord.MinX, bz0 = d.Coord.MinZ;
                foreach (var bp in d.Bridges)
                {
                    var dir = new Vector3(bp.DX, bp.DY, bp.DZ);
                    var at = new Vector3((float)(bp.X - bx0), (float)bp.Y, (float)(bp.Z - bz0));
                    if (bp.Kind == 1)
                    {
                        var up = EditorBridgeSpline.StandBasis(dir);
                        float ps = InfiniteRoads.DeckScale((RoadKind)bp.Road);   // a rail viaduct's pier pair stands as narrow as its deck
                        bridgeXf[1].Add(new Transform3D(new Basis(up.X * ps, up.Y, up.Z * bp.K), at));   // stretched on its OWN long axis
                    }
                    else
                    {
                        // a main's deck and caps are widened across (local X) to its asphalt
                        var db = EditorBridgeSpline.DeckBasis(dir);
                        float ws = InfiniteRoads.DeckScale((RoadKind)bp.Road);
                        if (ws != 1f) db = new Basis(db.X * ws, db.Y, db.Z);
                        bridgeXf[bp.Kind == 0 ? 0 : 2].Add(new Transform3D(db, at));
                    }
                }
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

            // high-voltage pylons (InfinitePylons): cow tools' lattice tower, stood up and turned like the roadside
            // pole (arms -- its local X -- across the line) and scaled by PowerLineField.PylonScale through the basis,
            // so the field's anchors scale with it. Base at the generator's height, the lowest ground under it.
            List<(Transform3D, Transform3D[])> pylons = null;
            if (d.Pylons != null && d.Pylons.Count > 0)
            {
                pylons = new List<(Transform3D, Transform3D[])>();
                double ox = d.Coord.MinX, oz = d.Coord.MinZ;
                foreach (var pp in d.Pylons)
                {
                    var wired = new Transform3D[pp.Wired.Length];
                    for (int w = 0; w < wired.Length; w++)
                        wired[w] = PylonXform((float)(pp.Wired[w].X - ox), pp.Wired[w].H, (float)(pp.Wired[w].Z - oz), pp.Wired[w].DirX, pp.Wired[w].DirZ);
                    pylons.Add((PylonXform((float)(pp.X - ox), pp.H, (float)(pp.Z - oz), pp.DirX, pp.DirZ), wired));
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
            List<Transform3D>[] railXf = null;
            if (d.Rails != null)
            {
                railXf = new[] { new List<Transform3D>(), new List<Transform3D>(), new List<Transform3D>() };
                foreach (var rp in d.Rails) railXf[rp.Kind].Add(RailXform(rp, d.Coord.MinX, d.Coord.MinZ));
                // a crossbuck stands up like the roadside pole (Z-up rip, PEI's ex=270), its arms across the road
                if (d.CrossingSigns != null)
                    foreach (var cs in d.CrossingSigns)
                        railXf[2].Add(PoleXform((float)(cs.X - d.Coord.MinX), cs.Y + 0.2f, (float)(cs.Z - d.Coord.MinZ), cs.DX, cs.DZ));
            }
            return new Built { D = d, V = V.ToArray(), N = N.ToArray(), UV = UV.ToArray(), I = I.ToArray(), S0 = s0, S1 = s1, SplatSize = v, TreeXf = trees, ImpTrees = imp, BridgeXf = bridgeXf, Pylons = pylons, FoliageXf = foliage,
                               Road = roadMeshes, RoadCol = roadCol, Poles = poles, Tunnels = d.Tunnels, RailXf = railXf };
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
