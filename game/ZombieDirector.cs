using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UVector3 = UnityEngine.Vector3;

namespace UnturnedGodot
{
    // The Godot-facing half of the zombie rewrite (docs/ZOMBIE_REWRITE_PLAN.md). ONE node for the whole
    // level: it owns a ZombieSim, drives it, and lends rigs to the few zombies close enough to be seen.
    //
    // The inversion against the old system: there is no zombie node. A zombie is a row in the sim, and a
    // RiggedCharacter is a VIEW it borrows while it is worth drawing. No CharacterBody3D, no per-zombie
    // NavigationAgent3D, no _PhysicsProcess per zombie -- those are exactly the 20.3 ms of engine-side
    // cost the old system spent simulating 63 kinematic capsules.
    //
    // Enabled with --newzombies; without the flag ZombieField and ZombieController run untouched, so the
    // two systems can be compared on the same map in the same session.
    public partial class ZombieDirector : Node3D
    {
        /// <summary>--newzombies. Set once during argument parsing; read where the world is built, so the
        /// choice of system is one branch in one place rather than a parameter threaded through everything.</summary>
        public static bool Enabled;

        public PlayerController Player;
        public Terrain Terr;

        /// <summary>How many rigs exist at most. Views are lent to the nearest CLOSE/NEAR rows; everyone
        /// else is simulated without one. Scarcity is the design, not a limitation.</summary>
        [Export] public int MaxViews = 48;

        ZombieSim _sim;
        GodotNavQuery _nav;
        readonly List<View> _views = new();
        readonly Dictionary<ZombieId, int> _assigned = new();
        readonly List<(float DistSq, int Row)> _candidates = new();
        Vector3[] _playerScratch = System.Array.Empty<Vector3>();
        UVector3[] _players = new UVector3[8];
        readonly RandomNumberGenerator _rng = new();

        class View
        {
            public RiggedCharacter Rig;
            public ZombieId Owner;      // ZombieId.None when free
            public bool InUse;
        }

        public ZombieSim Sim => _sim;
        public int LiveCount => _sim?.Count ?? 0;
        public int ViewsInUse { get; private set; }

        public override void _Ready()
        {
            _rng.Randomize();
        }

        /// <summary>Build the sim from PEI's navmesh pockets and spawn points. Regions come from the
        /// pocket bounds, which IS the plan's "regions come from the navmesh bounds" -- and is what
        /// retail does, sizing its region array from LevelNavigation.bounds.</summary>
        public void LoadFromPei(string peiRoot)
        {
            var pockets = ZombieNav.LoadPockets(peiRoot);
            if (pockets.Count == 0) { GD.Print("[zdirector] no pockets -- no zombies"); return; }

            var bounds = new ZombieRegionBounds[pockets.Count];
            for (int i = 0; i < pockets.Count; i++)
            {
                var c = pockets[i].Center; var h = pockets[i].HalfExtent;
                bounds[i] = new ZombieRegionBounds(c.X - h.X, c.Z - h.Z, c.X + h.X, c.Z + h.Z);
            }

            var kinds = new ZombieKindTable();
            kinds.Register(new ZombieKind { Name = "normal", MoveSpeed = 5.5f, Health = 100f });
            _sim = new ZombieSim(new ZombieRegions(bounds), kinds);

            // Plan the same pocket-capped spawn set the old field uses, then hand the POSITIONS to the
            // sim instead of instantiating a node per zombie.
            var planner = new ZombieField { Terr = Terr };
            planner.LoadFromPei(peiRoot);
            var planned = planner.DebugPlanSpawns();
            planner.Free();

            foreach (var (_, pos) in planned) _sim.Spawn(0, new UVector3(pos.X, pos.Y, pos.Z));

            GD.Print($"[zdirector] {_sim.Count} zombies as sim rows across {bounds.Length} pockets, " +
                     $"{MaxViews} rigs in the pool, 0 physics bodies");
        }

        // --- test seams (L1) --------------------------------------------------------------------------
        // Build the sim without map data, and stand in for a player avatar, so the in-engine tests can
        // exercise the REAL navigation server and the REAL view pool on a synthetic navmesh.

        public Vector3? DebugPlayer;

        public void DebugBuild(ZombieRegionBounds[] regions, Vector3[] spawns, float moveSpeed = 5.5f)
        {
            var kinds = new ZombieKindTable();
            kinds.Register(new ZombieKind { Name = "normal", MoveSpeed = moveSpeed, Health = 100f });
            _sim = new ZombieSim(new ZombieRegions(regions), kinds);
            foreach (var s in spawns) _sim.Spawn(0, new UVector3(s.X, s.Y, s.Z));
        }

        public override void _PhysicsProcess(double delta)
        {
            if (_sim == null) return;
            // Terrain is optional: it only supplies ground height. Gating the whole nav wiring on it left
            // the sim with no navmesh at all in any world without a Terrain, and a sim with no navmesh
            // does not move -- silently, because standing still is also what "no route" looks like.
            if (_nav == null)
            {
                _nav = new GodotNavQuery(GetWorld3D().NavigationMap, Terr);
                _sim.Nav = _nav;
            }

            int n = GatherPlayers();
            _sim.SetPlayers(_players, n);
            _sim.SimStep(_tick++, delta);
            SyncViews(delta);
        }

        long _tick;

        int GatherPlayers()
        {
            int n = 0;
            if (DebugPlayer.HasValue) { Push(DebugPlayer.Value, ref n); return n; }
            if (Player != null && IsInstanceValid(Player)) { Push(Player.GlobalPosition, ref n); return n; }
            foreach (var p in PlayerRegistry.All)
                if (IsInstanceValid(p)) Push(p.GlobalPosition, ref n);
            return n;
        }

        void Push(Vector3 p, ref int n)
        {
            if (n >= _players.Length) System.Array.Resize(ref _players, _players.Length * 2);
            _players[n++] = new UVector3(p.X, p.Y, p.Z);
        }

        // --- views ------------------------------------------------------------------------------------
        //
        // Rigs go to the nearest CLOSE/NEAR rows, re-evaluated each tick. A row that keeps its view keeps
        // its animation state; one that loses it is simply not drawn, and the sim carries on regardless --
        // which is the whole point of decoupling the two.

        void SyncViews(double delta)
        {
            var cam = GetViewport()?.GetCamera3D();
            Vector3 eye = cam?.GlobalPosition ?? (Player != null && IsInstanceValid(Player) ? Player.GlobalPosition : Vector3.Zero);

            _candidates.Clear();
            for (int row = 0; row < _sim.Count; row++)
            {
                var tier = _sim.TierOf(row);
                if (tier != ZombieTier.Close && tier != ZombieTier.Near) continue;
                Vector3 p = G(_sim.PositionOf(row));
                _candidates.Add((eye.DistanceSquaredTo(p), row));
            }
            _candidates.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
            if (_candidates.Count > MaxViews) _candidates.RemoveRange(MaxViews, _candidates.Count - MaxViews);

            // Release views whose owner is no longer a candidate (moved away, died, dropped a tier).
            var keep = new HashSet<ZombieId>();
            foreach (var c in _candidates) keep.Add(_sim.IdOf(c.Row));
            for (int i = 0; i < _views.Count; i++)
            {
                if (!_views[i].InUse) continue;
                if (keep.Contains(_views[i].Owner)) continue;
                Release(i);
            }

            ViewsInUse = 0;
            foreach (var c in _candidates)
            {
                var id = _sim.IdOf(c.Row);
                if (!_assigned.TryGetValue(id, out int vi)) { vi = Acquire(id); if (vi < 0) continue; }
                Drive(_views[vi], c.Row, delta);
                ViewsInUse++;
            }
        }

        int Acquire(ZombieId id)
        {
            for (int i = 0; i < _views.Count; i++)
                if (!_views[i].InUse) { Bind(i, id); return i; }

            if (_views.Count >= MaxViews) return -1;
            var rig = BuildRig();
            if (rig == null) return -1;
            AddChild(rig);
            _views.Add(new View { Rig = rig });
            Bind(_views.Count - 1, id);
            return _views.Count - 1;
        }

        void Bind(int i, ZombieId id)
        {
            _views[i].InUse = true;
            _views[i].Owner = id;
            _views[i].Rig.Visible = true;
            _assigned[id] = i;
        }

        void Release(int i)
        {
            _assigned.Remove(_views[i].Owner);
            _views[i].InUse = false;
            _views[i].Owner = ZombieId.None;
            _views[i].Rig.Visible = false;
        }

        void Drive(View v, int row, double delta)
        {
            Vector3 p = G(_sim.PositionOf(row));
            v.Rig.GlobalPosition = p;
            UVector3 f = _sim.FacingOf(row);
            if (f.sqrMagnitude > 1e-4f) v.Rig.LookAt(p + new Vector3(f.x, 0f, f.z), Vector3.Up);
            v.Rig.Tick(delta);
            v.Rig.SetLocomotion(_sim.StateOf(row) == ZombieState.Pursue ? _sim.Kinds[_sim.KindOf(row)].MoveSpeed : 0f);
        }

        RiggedCharacter BuildRig()
        {
            string atlas = $"res://content/zombie_atlas_{_rng.RandiRange(0, 5)}.png";
            var rig = RiggedCharacter.Build("res://content/rig.json", Colors.White, false, atlas, "res://content/face_19.png");
            if (rig == null) return null;
            rig.UsePhysicsAnimRate();
            rig.WalkClip = "Move_" + _rng.RandiRange(0, 3);
            rig.RunClip = rig.WalkClip;
            rig.IdleClip = "Idle_" + _rng.RandiRange(0, 3);
            rig.Play(rig.IdleClip);
            if (rig.Body != null) rig.Body.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            return rig;
        }

        static Vector3 G(UVector3 v) => new Vector3(v.x, v.y, v.z);

        /// <summary>One line of live state for the F3 overlay: what the sim is actually doing.</summary>
        public string DebugLine()
        {
            if (_sim == null) return "zdirector: no sim";
            var s = _sim.Stats;
            return $"zsim {s.Alive} alive  C/N/F/A {s.Close}/{s.Near}/{s.Far}/{s.Ambient}  " +
                   $"due {s.Due}  moving {s.Moving}  paths {s.PathQueries}/tick (+{s.PathQueued} queued, " +
                   $"{_sim.TotalPathQueries} total)  views {ViewsInUse}";
        }
    }

    // The navmesh, behind the sim's interface. Paths come from the baked pocket navmesh; ground height
    // comes from the terrain, because asking the navigation server to snap every zombie every tick would
    // reintroduce a per-zombie engine call -- the exact shape of cost this rewrite exists to remove.
    public sealed class GodotNavQuery : IZombieNavQuery
    {
        readonly Rid _map;
        readonly Terrain _terr;

        public GodotNavQuery(Rid map, Terrain terr) { _map = map; _terr = terr; }

        public int QueryPath(UVector3 from, UVector3 to, UVector3[] corridor)
        {
            if (!NavigationServer3D.MapIsActive(_map)) return 0;
            var a = NavigationServer3D.MapGetClosestPoint(_map, new Vector3(from.x, from.y, from.z));
            var b = NavigationServer3D.MapGetClosestPoint(_map, new Vector3(to.x, to.y, to.z));
            var path = NavigationServer3D.MapGetPath(_map, a, b, true);
            if (path == null || path.Length == 0) return 0;

            int n = 0;
            for (int i = 0; i < path.Length && n < corridor.Length; i++)
            {
                // Drop a leading waypoint we are already standing on -- it would just burn a step.
                if (i == 0 && new Vector3(from.x, from.y, from.z).DistanceTo(path[i]) < 0.5f) continue;
                corridor[n++] = new UVector3(path[i].X, path[i].Y, path[i].Z);
            }
            return n;
        }

        public UVector3 SnapToSurface(UVector3 p) =>
            _terr != null ? new UVector3(p.x, _terr.SampleHeight(p.x, p.z), p.z) : p;
    }
}
