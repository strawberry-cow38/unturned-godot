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
        public const float RebaseDistance = 1024f;
        public static bool DebugShiftLog;

        public static readonly int[] LodRing = { 2, 4, 7, 11 };
        public const int ColliderRing = 1, TreeRing = 3;
        public static int MaxRing => LodRing[LodRing.Length - 1];

        // ---- diagnostics (the overlay and the tests read these) ----
        public int Rebases, Rescues, Committed, TreeCount;
        public double GenMsTotal; public int GenCount;
        public readonly int[] LoadedByLod = new int[4];
        public int Colliders => _colliders;
        /// <summary>Nothing queued, cooking or waiting to upload, and the outermost ring has arrived -- what a --shot
        /// waits for, since a movie-mode frame takes seconds and the first frames would show a world half-built.</summary>
        public bool Settled => Queued == 0 && InFlight == 0 && _done.IsEmpty && LoadedByLod[LodRing.Length - 1] > 0;
        public int Queued { get { lock (_lock) return _jobs.Count; } }
        public int InFlight => _inFlight;

        sealed class Region
        {
            public RegionCoord C;
            public Node3D Node;
            public MeshInstance3D Mesh;
            public int Lod = -1, PendingLod = -1;
            public float[] Lod0Heights;
            public List<TreeSpawn> TreeList;
            public Node3D Trees;            // the MultiMeshes
            public Node3D TreeBodies;       // trunk colliders, ring <= ColliderRing only
            public StaticBody3D Ground;
        }

        sealed class Job { public RegionCoord C; public int Lod; }

        sealed class Built
        {
            public RegionData D;
            public Vector3[] V, N; public Vector2[] UV; public int[] I;
            public byte[] S0, S1; public int SplatSize;
            public Dictionary<string, List<Transform3D>> TreeXf;
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
            Log.Print($"[infinite] streamer up: {(Source != null ? Source.GetType().Name : $"seed {Gen.Seed}")}, {n} worker threads, rings {string.Join("/", LodRing)}, colliders <= {ColliderRing}, trees <= {TreeRing}");
        }

        public override void _ExitTree()
        {
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
                if (useful) Apply(r, b);
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
            Rebases++;
            Log.Print($"[infinite] rebase #{Rebases}: world shifted ({-sx:0}, {-sz:0}) m, origin now ({OriginX:0}, {OriginZ:0})");
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
            if (d.Trees != null)
            {
                trees = new Dictionary<string, List<Transform3D>>();
                foreach (var t in d.Trees)
                {
                    int variant = ((int)(t.X * 7f) + (int)(t.Z * 13f)) & 1;
                    string name = TreeNames[t.Kind] + "_" + variant;
                    if (!trees.TryGetValue(name, out var list)) trees[name] = list = new List<Transform3D>();
                    var basis = new Basis(Vector3.Up, Mathf.DegToRad(t.Yaw)).Scaled(new Vector3(t.Scale, t.Scale, t.Scale));
                    list.Add(new Transform3D(basis, new Vector3(t.X, t.Y - ResourceField.TreeSink * t.Scale, t.Z)));
                }
            }
            return new Built { D = d, V = V.ToArray(), N = N.ToArray(), UV = UV.ToArray(), I = I.ToArray(), S0 = s0, S1 = s1, SplatSize = v, TreeXf = trees };
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
