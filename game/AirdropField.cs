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

        /// <summary>A drop the server announced. `startedAt` is the SERVER's clock for the drop, which
        /// this client adopts wholesale -- adjusting it to local time would reintroduce exactly the
        /// drift the closed-form trajectory exists to avoid.</summary>
        public void BeginRemote(uint netId, Vector3 target, float startedAt)
        {
            Sim.ForceDrop(new UVector3(target.X, target.Y, target.Z));
            Sim.AdoptStart(startedAt);
            SpawnCrate(netId);
        }

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
            if (_driveLocally)
            {
                bool began = Sim.Step(delta, PickTarget);
                if (began) SpawnCrate(0);
                if (Sim.JustLanded) _crate?.MarkLanded();
            }
            else
            {
                // Remote: the schedule is the server's, but the CLOCK still has to advance or the
                // closed-form position never moves. Stepping with a null picker advances time without
                // ever starting a drop of our own.
                Sim.Step(delta, null);
                if (Sim.JustLanded) _crate?.MarkLanded();
            }

            if (IsInstanceValid(_crate) && !_crate.Landed)
            {
                var p = Sim.CurrentPosition;
                _crate.ApplyPosition(new Vector3(p.x, p.y, p.z));
            }
        }

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
