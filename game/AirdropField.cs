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

            if (Sim.Phase == AirdropPhase.Inbound) ShowPlane(Sim.PlanePositionAt(Sim.Clock));
            else HidePlane();

            if (IsInstanceValid(_crate) && !_crate.Landed)
            {
                var p = Sim.CurrentPosition;
                _crate.ApplyPosition(new Vector3(p.x, p.y, p.z));
            }
        }

        readonly RandomNumberGenerator _rng = new();
        double Roll() { return _rng.Randf(); }

        MeshInstance3D _plane;

        /// <summary>The telegraph. A drop that simply appears at altitude is a loot spawn; the plane is
        /// what makes it an event you can see coming from across the map and move on.</summary>
        void ShowPlane(UVector3 at)
        {
            if (!IsInstanceValid(_plane))
            {
                _plane = new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(14f, 3f, 4f) },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.20f, 0.22f, 0.26f) },
                };
                AddChild(_plane);
            }
            _plane.Visible = true;
            _plane.GlobalPosition = new Vector3(at.x, at.y, at.z);
            var v = Sim.PlaneVelocity;
            if (v.x * v.x + v.z * v.z > 0.01f)
                _plane.LookAt(_plane.GlobalPosition + new Vector3(v.x, 0f, v.z), Vector3.Up);
        }

        void HidePlane() { if (IsInstanceValid(_plane)) _plane.Visible = false; }

        /// <summary>Somewhere on the terrain near the origin. Deliberately simple: the interesting part
        /// of an airdrop is the event, and a smarter site picker can replace this without touching
        /// anything else.</summary>
        public UVector3 PickTarget()
        {
            var rng = new RandomNumberGenerator();
            rng.Randomize();
            float x = rng.RandfRange(-120f, 120f), z = rng.RandfRange(-120f, 120f);
            float y = Terr != null ? Terr.SampleHeight(x, z) : 0f;
            return new UVector3(x, y, z);
        }
    }
}
