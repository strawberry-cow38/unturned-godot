using Godot;
using SDG.Unturned;   // FluidPortKind lives with the engine-free solver

namespace UnturnedGodot
{
    // Fluid IO on the map's KITCHEN SINKS (strawberry: "sinks supply clean water", then "add hose io port to sinks,
    // connects at the spout of the faucet. add a water input, for using sinks after water shutoff").
    //
    // WHICH PROPS ARE SINKS was read off the meshes, not guessed. PEI has four counter variants and no prop named
    // "sink": Counter_0/Counter_2 stop at the counter top (mesh Z 1.35), while Counter_1/Counter_3 carry an extra
    // group reaching Z 1.834 -- a basin recessed into the top plus a tap standing above it. 22 Counter_1 + 6
    // Counter_3 = 28 sinks, against 126 plain counters that stay ordinary props. The pairing is wood/steel FINISH
    // (0 vs 2, 1 vs 3), so the palette says nothing and the geometry says everything -- see fluid.sink_prop_identity.
    //
    // A SINK IS A BASIN, NOT A TAP. That is the whole shape of this class, and it is what makes the water input work:
    // the sink HOLDS a little water and the mains REFILL it, rather than the sink being an infinite source that the
    // mains switch turns off. So after a shutoff a sink isn't dead, it's just no longer being topped up -- and
    // anything you hose into its inlet comes back out of its spout. That falls out of the existing solver with no
    // changes to it: FluidNet already drains a Storage's output by the load it feeds and fills it from its input, so
    // a sink is just a Storage whose top-up happens to be municipal.
    //
    // Deliberately NOT `SupplyEnabled => GlobalWater` -- which is what the hydrant and the tower use, and what this
    // class used before. That gate makes a source inert in the SOLVER, which is right for a pressurised main with
    // nothing in it; but it would mean the new inlet fed a container that could never give the water back, i.e. the
    // exact request defeated by the old design. The hydrant and tower keep the gate. Only the sink has a basin.
    public partial class SinkSource : FluidContainer
    {
        /// <summary>A tap, not a main: slow enough that filling a big tank off a kitchen sink is a chore rather than
        /// the obvious play, which is what keeps the hydrants and towers worth hosing.</summary>
        public const float TapRate = 30f;

        /// <summary>The supply line fills FASTER than the tap empties -- a tap is a restriction, which is the whole
        /// reason a basin fills at all while it's running.
        ///
        /// The margin is also headroom against a knife edge. A near-empty basin's output rate is clamped to
        /// Amount/dt, and the solver only counts a consumer as Flowing when it gets its FULL demand -- so at inlet ==
        /// tap the basin sits pinned near zero with the stored amount having to land exactly on the tap's per-tick
        /// draw for the tap to read as flowing at all. It does balance there at a fixed timestep (verified: the
        /// water-input test passes with the rates equal), so this is not fixing an observed bug -- it means the basin
        /// banks water instead of depending on the frame time being steady.</summary>
        public const float InletRate = 60f;

        /// <summary>What the basin and trap hold: 5 L. Small on purpose. While the mains are up it is refilled every
        /// tick, so the size never shows; the moment they are cut it is the difference between "the tap still runs
        /// for a moment" and "the tap is instantly dead", and 5 L is about a canteen's worth of grace.</summary>
        public const float BasinCapacity = 5000f;

        // ---- port anchors, in the PROP MESH's own coordinates (Counter_1.obj / Counter_3.obj, Z-up as authored) ----

        /// <summary>The FAUCET SPOUT MOUTH -- the downward opening at the end of the gooseneck, where water actually
        /// leaves the tap. Read off the mesh rather than eyeballed: isolate the tap's material group, take the arm's
        /// FORWARD half (the part overhanging the basin, mesh Y > -0.28) and its lowest ring is a clean 4-vertex
        /// square at Z 1.631 centred (0, -0.1835). Counter_1 and Counter_3 are one model in two finishes and return
        /// the identical ring, which is why a single constant serves both.
        ///
        /// The previous value -- a hand-guessed (0, 1.42, 0.22) in node space -- sat ~21 cm BELOW the spout, floating
        /// in the basin attached to nothing. That is precisely why this is derived and not typed.</summary>
        public static readonly Vector3 SpoutMeshLocal = new Vector3(0f, -0.1835f, 1.631f);

        /// <summary>The SUPPLY INLET: the stub-out under the counter lip on the cabinet's front face. Mesh +Y is the
        /// front (the top slab overhangs to 0.625 while the cabinet stops at 0.5, and an overhang is on the side you
        /// stand at), so 0.56 sits just proud of the door and still beneath the lip. Front rather than back because
        /// the player has to see and aim at it; a real supply enters at the back, where it would be unreachable
        /// against a wall.</summary>
        public static readonly Vector3 InletMeshLocal = new Vector3(0f, 0.56f, 0.62f);

        /// <summary>The placement rotation of an UPRIGHT counter, in the prop mesh's own terms: euler X=270 is what
        /// stands the Z-up mesh on its feet. Named so the parameterless Make() and the world path share one transform
        /// instead of one being a hand-copied version of the other's arithmetic.</summary>
        public static readonly Basis UprightPlacement = new Basis(Vector3.Right, Mathf.DegToRad(270f));

        // ---- resolved into THIS NODE's frame at Make() time ----
        public Vector3 SpoutLocal = Vector3.Zero;
        public Vector3 InletLocal = Vector3.Zero;

        /// <summary>Mesh-local -> this node's local, for a prop placed with `placement` under a sink node carrying
        /// `yawDeg` of yaw. WorldBuilder draws the prop at `gpos + basis * meshLocal` but gives the fluid node YAW
        /// ONLY, so a port has to be un-yawed back out of the full basis to land on the mesh feature it names.
        ///
        /// Per-instance rather than a fixed swizzle, and that matters for exactly one prop -- which is the point. 27
        /// of PEI's 28 counters are euler (270, *, 0); ONE Counter_3 is placed at (277.289, *, 237.977), pitched and
        /// rolled. A hardcoded (x, z, -y) would hang that one's spout out in the air beside the counter: invisible to
        /// every count-based test, and the sort of single-instance wrongness nobody finds by walking around.
        /// (Uniform scale only -- a non-uniformly scaled prop would need the node to carry the scale too. Every
        /// counter on this map is scale 1.)</summary>
        public static Vector3 MeshToNode(Basis placement, float yawDeg, Vector3 meshLocal)
            => new Basis(Vector3.Up, Mathf.DegToRad(yawDeg)).Inverse() * (placement * meshLocal);

        /// <summary>A sink on an upright, unrotated counter -- the shape tests and any non-world caller.</summary>
        public static SinkSource Make() => Make(UprightPlacement, 0f);

        public static SinkSource Make(Basis placement, float yawDeg) => new SinkSource
        {
            // STORAGE, not Source: a basin the mains top up (see the class comment). The role change is what gives it
            // an inlet at all, and what lets the spout keep supplying hosed-in water after a shutoff.
            Role = FluidRole.Storage,
            Tank = new FluidTank(FluidType.Water, BasinCapacity, BasinCapacity, WaterQuality.Clean),
            FlowRate = TapRate,
            DisplayName = "Sink",
            SpoutLocal = MeshToNode(placement, yawDeg, SpoutMeshLocal),
            InletLocal = MeshToNode(placement, yawDeg, InletMeshLocal),
        };

        // Ports[0] = the INLET (a supply line landing on the stub under the counter), Ports[1] = the SPOUT (the tap).
        // Consumer-then-Source is the same order as the base Storage case, so anything indexing a storage's ports
        // positionally still works. The base case is REPLACED rather than extended because its two ports sit on the
        // flat faces of a stand-alone tank body, and this device has no body of its own -- it rides the counter.
        protected override void BuildPorts()
        {
            AddPort(FluidPortKind.Consumer, InletRate, InletLocal);
            AddPort(FluidPortKind.Source, TapRate, SpoutLocal);
        }

        /// <summary>THE MAINS. Every tick the water is on, the basin is topped straight back up -- which is what makes
        /// the tap effectively infinite while the town has water, WITHOUT the sink being flagged Infinite (that flag
        /// also suppresses the drain, and a sink genuinely does run dry once nothing is refilling it).
        ///
        /// In OnPostTick rather than as a solver special case on purpose: it runs AFTER the tick's fluid has moved, so
        /// the drain is real and the refill is a separate, visible act. Cut the mains and the very next tick simply
        /// doesn't refill -- no flag to invalidate, no state to unwind.
        ///
        /// Pressure also FLUSHES the basin: while the mains are up the water is Clean whatever was standing in it. In
        /// practice you can't dirty a live sink anyway (a full basin has no space, so its inlet accepts nothing), but
        /// this makes restoring the water clean the sink out instead of leaving it permanently soured by something
        /// poured in during the outage.</summary>
        public override void OnPostTick(float dt)
        {
            if (Tank == null || !FluidNet.GlobalWater) return;
            Tank.Fill(Tank.Space);
            Tank.Quality = WaterQuality.Clean;
        }

        protected override void BuildVisuals() { }   // rides the Counter_1 / Counter_3 prop mesh

        public override (string text, Color color) StatusLine()
            => FluidNet.GlobalWater ? ("MAINS", new Color(0.5f, 1f, 0.6f))
             : Tank != null && !Tank.IsEmpty ? ($"{FluidDef.Litres(Tank.Amount)} standing", StatusIdle)
             : ("NO WATER", StatusWarn);
    }
}
