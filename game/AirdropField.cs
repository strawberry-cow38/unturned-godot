using Godot;
using SDG.Unturned;
using UVector3 = UnityEngine.Vector3;

namespace UnturnedGodot
{
    /// <summary>
    /// Draws the supply drop the server announced.
    ///
    /// The client keeps its OWN AirdropSim rather than being fed a position every tick. It is told
    /// only the landing point and the server's start clock, and derives the descent from those --
    /// which is possible precisely because the trajectory is closed-form. That costs no bandwidth per
    /// tick and, more usefully, a client that joins halfway down still puts the crate in the right
    /// place from the same two facts.
    ///
    /// Singleplayer uses the same node with a locally-driven sim, so there is exactly one code path
    /// for "where is the crate" rather than a networked one and an offline one that can disagree.
    /// </summary>
    public partial class AirdropField : Node3D
    {
        public static AirdropField Instance;

        /// <summary>The sim this field renders. In SP the world owns and steps it; in MP it mirrors
        /// what the server announced.</summary>
        public AirdropSim Sim { get; } = new AirdropSim();

        public Terrain Terr;

        /// <summary>What a crate carries. A field rather than a constant so a map or a server can set
        /// its own table without touching this class.</summary>
        public static readonly ushort[] DropLoot = { 67, 67, 4, 5 };
        AirdropCrate _crate;
        bool _driveLocally;

        public override void _Ready() => Instance = this;
        public override void _ExitTree() { if (Instance == this) Instance = null; }

        /// <summary>Singleplayer: this machine decides when and where, and steps the schedule itself.</summary>
        public void DriveLocally(bool on) => _driveLocally = on;

        /// <summary>A plane the server announced. Clocks are adopted wholesale rather than re-based to
        /// local time -- the whole point of a closed-form trajectory is that both machines integrate
        /// from the SAME origin.
        ///
        /// No crate is spawned here. The client is not told where the drop lands; it flies the plane it
        /// was given and the crate appears when the plane reaches its mark, same as it does on the
        /// server. That is the mechanic, not a limitation.</summary>
        public void BeginRemote(uint netId, Vector3 planeStart, Vector3 planeVelocity,
                                float launchedAt, float releaseAt, float groundY)
        {
            Sim.AdoptPlane(new UVector3(planeStart.X, planeStart.Y, planeStart.Z),
                           new UVector3(planeVelocity.X, planeVelocity.Y, planeVelocity.Z),
                           launchedAt, releaseAt, groundY);
            _pendingNetId = netId;
            if (IsInstanceValid(_crate)) { _crate.QueueFree(); _crate = null; }
        }

        uint _pendingNetId;

        public void LandRemote(uint netId)
        {
            if (AirdropCrate.TryGetByNetId(netId, out var c)) c.MarkLanded();
            else _crate?.MarkLanded();
        }

        void SpawnCrate(uint netId)
        {
            if (IsInstanceValid(_crate)) _crate.QueueFree();
            var p = Sim.CurrentPosition;
            _crate = AirdropCrate.Spawn(this, new Vector3(p.x, p.y, p.z), netId);
            // Contents are decided where the crate is built so both machines fill it the same way from
            // the same drop. An empty crate is not a supply drop, it is an orange box.
            _crate.Contents.AddRange(DropLoot);
            GD.Print($"[airdrop] crate {netId} inbound at ({p.x:0}, {p.z:0})");
        }

        public override void _PhysicsProcess(double delta)
        {
            // SP drives the schedule; MP only advances the clock, because the closed-form position is
            // a function of time and a stalled clock freezes the plane mid-air.
            if (_driveLocally) Sim.Step(delta, PickTarget, Roll);
            else Sim.Step(delta, null);

            // The crate is created by the plane reaching its mark, on BOTH machines, from the same two
            // facts. Nobody is told a landing point.
            if (Sim.JustReleased) SpawnCrate(_driveLocally ? 0 : _pendingNetId);
            if (Sim.JustLanded) _crate?.MarkLanded();

            // PlaneVisible, not Phase: the aircraft keeps flying after it lets go, and gating on the
            // crate's phase made it vanish at the instant of release -- in front of whoever had just
            // tracked it across the map to work out where the drop was going.
            if (Sim.PlaneVisible) ShowPlane(Sim.PlanePositionAt(Sim.Clock));
            else HidePlane();

            if (IsInstanceValid(_crate) && !_crate.Landed)
            {
                var p = Sim.CurrentPosition;
                _crate.ApplyPosition(new Vector3(p.x, p.y, p.z));
            }
        }

        readonly RandomNumberGenerator _rng = new();

        /// <summary>Lets a shot scene fix the approach. Without it the heading is drawn fresh every run,
        /// so a render can't be compared with the last one and "the nav lights are on the correct
        /// wingtips" isn't a claim anyone can check twice.</summary>
        public System.Func<double> RollOverride;

        double Roll() => RollOverride != null ? RollOverride() : _rng.Randf();

        Dropship _plane;

        /// <summary>The plane, once it exists. Null before the first drop -- a shot scene checks
        /// <c>HasModel</c> on it rather than assuming the content was there.</summary>
        public Dropship Plane => _plane;

        /// <summary>The telegraph. A drop that simply appears at altitude is a loot spawn; the plane is
        /// what makes it an event you can see coming from across the map and move on.</summary>
        void ShowPlane(UVector3 at)
        {
            if (!IsInstanceValid(_plane))
            {
                _plane = Dropship.Build();
                AddChild(_plane);
            }
            _plane.Visible = true;
            _plane.GlobalPosition = new Vector3(at.x, at.y, at.z);
            var v = Sim.PlaneVelocity;
            _plane.FaceVelocity(new Vector3(v.x, v.y, v.z));
        }

        void HidePlane() { if (IsInstanceValid(_plane)) _plane.Visible = false; }

        /// <summary>Somewhere on the terrain near the origin. Deliberately simple: the interesting part
        /// of an airdrop is the event, and a smarter site picker can replace this without touching
        /// anything else.</summary>
        /// <summary>Lets a test or shot scene pin the landing spot instead of scattering it.</summary>
        public System.Func<UVector3> TargetOverride;

        /// <summary>A ONE-SHOT landing spot, consumed by the next drop. The console's summon verbs use
        /// this rather than TargetOverride so that calling `airdrop` at your feet doesn't permanently
        /// nail every future scheduled drop to that spot.</summary>
        public UVector3? TargetOnce;

        /// <summary>Where the next crate lands.
        ///
        /// Retail picks a map-authored airdrop node uniformly at random -- NOT the player's position,
        /// and not the nearest one (LevelManager: airdropNodes[Random.Range(0, count)]). PEI has 14 of
        /// them spread across the island.
        ///
        /// The old behaviour was a random point within 120 m of the WORLD ORIGIN, which is why every
        /// drop landed in roughly the same corner of the map. That was a placeholder from when the
        /// plane went in; the nodes were always there, just in Level.hierarchy rather than the
        /// Nodes.dat the port already parsed.</summary>
        public UVector3 PickTarget()
        {
            if (TargetOverride != null) return TargetOverride();
            if (TargetOnce.HasValue) { var t = TargetOnce.Value; TargetOnce = null; return Grounded(t); }

            var nodes = MapNodes.AirdropNodes;
            var rng = new RandomNumberGenerator();
            rng.Randomize();
            if (nodes.Count > 0)
            {
                var n = nodes[rng.RandiRange(0, nodes.Count - 1)];
                return Grounded(new UVector3(n.X, n.Y, n.Z));
            }
            // No node data (a map we haven't extracted, or a bare test scene) -- fall back to the old
            // scatter rather than refusing to drop at all.
            float x = rng.RandfRange(-120f, 120f), z = rng.RandfRange(-120f, 120f);
            return new UVector3(x, Terr != null ? Terr.SampleHeight(x, z) : 0f, z);
        }

        /// <summary>Put a target on the actual ground. The authored node height is the map author's, and
        /// our terrain is rebuilt from the retail heightmap rather than being the same mesh -- sampling
        /// keeps the crate flush instead of buried or hovering. Falls back to the authored Y when there
        /// is no terrain to ask.</summary>
        UVector3 Grounded(UVector3 p) =>
            Terr != null ? new UVector3(p.x, Terr.SampleHeight(p.x, p.z), p.z) : p;
    }
}
