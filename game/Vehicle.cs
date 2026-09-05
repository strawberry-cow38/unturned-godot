using Godot;
using SDG.Unturned;   // ItemTool.RarityColorUI + EItemRarity (vehicle look-at outline colour)

namespace UnturnedGodot
{
    // Drivable vehicle. Source: InteractableVehicle + VehicleAsset (WheelCollider rig -> Godot VehicleBody3D +
    // VehicleWheel3D 1:1). Meshes ripped by tools/extract_vehicle_mesh.py; params + real _PaintColor from the .dat.
    public partial class Vehicle : VehicleBody3D, ITowNode
    {
        float _engineForce = 600f;                  // acceleration feel (calibrated: Unity WheelCollider torque doesn't map 1:1)
        float _steerMax = 28f, _steerMin = 14f;      // Steer_Max (at rest) .. Steer_Min (at full speed), degrees -- source .dat
        float _speedMax = 12.5f, _speedMin = -7f;    // Speed_Max fwd / Speed_Min reverse, m/s -- source .dat (directly usable)
        float _brakeForce = 32f;                     // Brake -- source .dat value
        float _steerTarget, _steerAngle, _steerTurnSpeed = 70f;   // steering smoothing: MoveTowards target at deg/s. LOWERED for a weighty/laggy feel -- the wheels float behind the input, slow to turn AND slow to re-center (master)
        WaterMode _water; Vector3[] _buoys; float _inThrottle, _inSteer; int _waterFrame;   // BOAT/AMPHIBIOUS: water mode + hull buoyancy VOXELS + the last drive input (water propulsion runs in _PhysicsProcess)
        // SWAMPED -- a LAND vehicle driven into water. The source has no such behaviour (an Unturned car simply
        // drives along the seabed, and so did this port: the ocean's only collider is bullets-only on bit 9, which
        // player/vehicle masks deliberately exclude). strawberry's design: the engine drowns, the body rides on the
        // air trapped in it for a few seconds, then that air escapes and it goes down.
        Vector3[] _swampBuoys; bool _swamped; float _swampTime;   // hull voxels (WHEELED vehicles only), in-water latch, seconds since it went in
        public bool Swamped => _swamped;                          // read by vehicle.water_swamp
        public float SwampTime => _swampTime;
        WakeTrail _wake;   // foam wake ribbon (lazy: created when the hull is first afloat)
        float _bowLocalZ;   // MEASURED hull bow-tip Z in local space -> the wake triangle's apex
        float _waveAmp = 0.1f;                                  // sea-surface ripple amplitude for THIS hull
        bool _steadyHull;                                       // hold her still: extra heave damping (Spec.SteadyHull)
        Vector3 _deckVolume, _deckCenter;                       // MOVING DECK: the carry box (local space); Zero = not a carrier
        Transform3D _deckPrevXf; bool _deckHasPrev;
        PhysicsShapeQueryParameters3D _deckQ;
        struct DeckRider { public float Grace, Settle; public Vector3 LastVel; public float LastYawRate; }
        readonly System.Collections.Generic.Dictionary<Node3D, DeckRider> _deckRiders = new();
        readonly System.Collections.Generic.Dictionary<Node3D, float> _deckLoadVy = new();   // load -> its vertical velocity last tick
        public int DebugDeckRiders;                             // test seam: bodies carried on the last tick
        public int DebugDeckLoads;                              // test seam: bodies whose weight we cancelled last tick
        public static bool DeckLoadCancelEnabled = true;        // test seam: turn the load cancellation off, so a test
                                                                // can ask whether the cancellation is itself what is
                                                                // exciting the hull rather than assuming it is not
        float _voxelHalfHeight, _waterTime, _gravityMag = 9.8f, _buoyDamp = 1f, _turnScale = 1f, _buoyReserve = 1f;   // source Buoyancy.cs port: voxel half-height (submersion test), wave-ripple clock, gravity magnitude (Archimedes balance); _buoyDamp = per-vehicle damping multiplier
        bool _afloat;   // currently floating (any buoy submerged) -- HUD/anim can read it
        public bool Afloat => _afloat;
        // ---- ROTARY WING (VoX 2026-08-15: "a rust style minicopeter"). A helicopter is a Vehicle rather than a
        // new node type because every downstream system -- NetId minting, the hold/adopt authority split,
        // enter/exit occupancy, damage, fuel, despawn -- is written against Vehicle, and VehicleReplication
        // already carries pitch AND roll (not just yaw) for both the entity and the client-auth state command.
        // A sibling RigidBody3D would have to rebuild all of it. VehicleBody3D with no VehicleWheel3D children
        // is just a RigidBody3D, so the base class does not fight flight.
        bool _heli; float _heliThrust, _heliPitchTq, _heliRollTq, _heliYawTq, _heliLevel, _heliDragFwd;
        bool _plane; float _planeThrust, _planeLift, _planeTargetSpeed, _planePitchTq, _planeRollTq, _planeYawTq, _planeSteerFade = 1f;   // PLANE (EEngine.PLANE): forward thrust + airspeed lift, bank-to-turn
        Node3D _propNode; MeshInstance3D _propBlades, _propDisc;   // propeller pivot + its 2 draw states (blades / spin-blur), spun about body forward
        float _propSpin;   // prop visual phase (about local Z)
        Node3D[] _jetFlames; OmniLight3D[] _jetFlameLights; ShaderMaterial[] _jetFlameMats; float _jetFlameT;   // afterburner flame cones (content/afterburner.gdshader, per-burner mat) + glow (jet); throttle-scaled
        Contrail[] _contrails; float _contrailFade;   // world-space wingtip/winglet vapour trails (Contrail class below); _contrailFade = LAGGED airspeed fade so they ease in, not pop (jet)
        int _planeDbgFrame;   // UG_PLANEDBG print throttle
        bool _planeGroundMode;   // master: hold Ctrl -> drop onto the ground/water + taxi (no lift), for floatplanes now + wheeled aircraft later
        public bool PlaneGroundMode { get => _planeGroundMode; set => _planeGroundMode = value; }
        bool _slingHook; float _slingLen; Vector3 _slingAnchor, _slingVisualAnchor;   // winch + electromagnet (sky-crane): see UpdateSling. Anchor = FORCE point (must stay on the CoM axis). VisualAnchor = where the cable is DRAWN from; may differ.
        SlingMagnet _magnet; TowRope _slingCable; TowRope[] _slingLegs; MeshInstance3D _slingLink; bool _magnetWanted; float _slingOut;
        RigidBody3D _slingHeldPrev;   // what the coil held last tick, so the carrier's collision exception tracks it   // _slingOut = cable CURRENTLY paid out, ramping to _slingLen
        public SlingMagnet Sling => _magnet;
        public bool SlingDeployed => _magnet != null && IsInstanceValid(_magnet);
        public bool DebugNoSling;   // suppress winch deployment, so a rig can fly the SAME airframe with and without its magnet
        public Vector3 DebugTailHub => _tailHubCentre;
        public float DebugCollective => _inCollective;
        public float DebugSlingLen => _slingLen;
        public Vector3 DebugSlingAnchorLocal => _slingAnchor;           // FORCE point (CoM axis)
        public Vector3 DebugSlingVisualAnchorLocal => _slingVisualAnchor;   // DRAW point (cable geometry uses this)
        public bool DebugSlingHook => _slingHook;
        float _rotorRadius, _heliLiftCap = 1f, _groundEffect = 1f, _geApplied = 1f;   // cached once per StepHeli; _geApplied is the share the CAP let through
        MeshInstance3D _beaconMesh; OmniLight3D _beaconLight; StandardMaterial3D _beaconMat; float _beaconTimer;   // belly anti-collision flasher
        float _ignitionLeft, _ignitionLen;   // start-up gate: the rotor winds up THROUGH the clip, thrust waits for it
        bool _tracked;   // TANK: tracked/differential drive -- Drive() branches on this to set per-TRACK torque instead of a steered-wheel angle
        const float TankWheelSlip = 1.0f;   // TANK: lateral wheel friction. Too LOW (0.5) and the yaw torque spins it in place instead of arcing forward (low grip = no forward bite either); too HIGH and turning drags to a crawl. Paired with the speed-faded yaw below. Tunable.
        const float TankComY = 0.1f;   // TANK: low centre of mass (anti-flip -- master "easily flipped"). Tunable.
        const float TankTrackDiff = 0.3f;   // TANK: how much steer biases the two tracks' SPEED (both still drive -- fully stopping a track halves the power = crawl, master). The yaw torque does the turning; this is just feel. Tunable.
        static float MaxLatAccel => float.TryParse(System.Environment.GetEnvironmentVariable("UG_LATG"), out var _g) ? _g * 9.8f : 8.3f;   // ~0.85 g of cornering a car may ASK for; the steer angle is capped to it
        const float MinSteerDeg = 3.5f;   // never fade lock away entirely, however fast it is going
        const float SuspensionHeadroom = 3f;   // suspension max force, as a multiple of the static load ONE wheel carries
        const float TankMaxYawRate = 0.6f, TankYawGain = 60000f, TankYawSpeedFade = 0.7f;   // TANK skid-steer: a REAL torque (ApplyTorque -- integrated into owned momentum, MP-safe + survives slopes/walls, per VoX) GOVERNED toward TankMaxYawRate*input. A plain constant torque is bang-bang here (the wheels' yaw resistance is ~constant -> stalls or runs away), so this feedback torque holds a stable rate. TankYawSpeedFade FADES the target as forward speed rises: a tight pivot at rest, a WIDE arc at speed, so a turn doesn't drag to a crawl (master). Tunable.
        float _tankYawInput;   // TANK: yaw request [-1,1] from the track difference (set in Drive, applied as a real torque in _PhysicsProcess)
        float _inCollective, _inYaw, _inPitch, _inRoll, _rawThrottle;   // the pilot's held axes (W/S, A/D, mouse Y, mouse X); _rawThrottle = last raw W/S axis (for ground reverse)
        float _rotorSpin, _tailSpin, _rotorRpm;            // visual blade phases (main/tail) + spool state (0..1)
        Node3D _rotorNode, _tailRotorNode;
        // ---- TRACKED ARMOUR (tank). The turret + gun ride their OWN pivots so the vehicle-weapon system
        // (tinyclaw) aims them independently of the hull: TurretPivot yaws about local Y, GunPivot (its child)
        // pitches about local X, and MuzzleLocal is the cannon tip for shell spawns. Null/Zero on non-tanks.
        public Node3D TurretPivot, GunPivot; public Vector3 MuzzleLocal;
        MeshInstance3D _bladesMesh, _discMesh, _tailBladesMesh, _tailDiscMesh;   // the two drawn states per rotor
        const float DiscSwapSpool = 0.35f;   // RETIRED: the blur-plate swap threshold. Kept as the record of what the
                                             // retail prefab did; the real blades are drawn and spun at every rpm now.
        public const float TailRotorRollDegrees = 90f;   // stands the tail disc on edge; composed with the spin each tick
        // ---- playtest tuning, strawberry 2026-08-16 ----------------------------------------------------
        /// <summary>Fraction of lift lost to tilting off vertical, ON TOP of the cosine you get for free from
        /// thrusting along the body axis. strawberry: "reduce the upward thrust more when tilting forward" --
        /// so a nose-down dash costs you altitude rather than being a free way to go fast level.</summary>
        const float TiltThrustLoss = 0.55f;
        /// <summary>HEAVE DAMPING, s^-1. The vertical axis's whole resisting force, and deliberately LINEAR --
        /// which is not the same law as the horizontal below, on purpose. The shaft axis is not dominated by
        /// fuselage drag: a rotor climbing sees reduced inflow through the disc, so blade angle of attack rises
        /// and thrust rises with it, a restoring force linear in axial velocity to first order (the Z_w
        /// stability derivative of rotorcraft flight dynamics). The horizontal axis IS dominated by parasite
        /// drag, which is quadratic. Two axes, two mechanisms, two laws; modelling both with one law is what
        /// would actually be inconsistent.
        ///
        /// THE VALUE IS LOAD-BEARING AND MUST NOT BE RETUNED CASUALLY: it is what the fleet's terminal fall
        /// (g / 0.45 = 21.8 m/s) and HeliCrashExplodeSpeed were settled against. It
        /// was Godot's RigidBody3D.LinearDamp until the drag rework; it is applied by hand now because that
        /// property is a SCALAR and damps the whole velocity vector, so leaving it set would have applied this
        /// linear law to the horizontal too -- where, once the old thrust boosts were gone, it became the
        /// binding constraint and capped six of seven airframes BELOW their own spec top speed (the
        /// scoutcopter at 18.8 m/s against a spec of 26).
        ///
        /// 0.45 AND NOT THE 0.35 THE REST OF THIS FILE SAYS, because 0.45 is what actually shipped. The
        /// property was set to 0.35, but LinearDampMode defaults to COMBINE, which ADDS the body's value to
        /// ProjectSettings physics/3d/default_linear_damp -- and this project never overrides that, so it is
        /// Godot's default 0.1. Measured, not inferred: with the body value at 0 the fleet still showed
        /// exactly 0.100 s^-1 of horizontal damping, agreeing to three digits across three airframes.
        ///
        /// This matters beyond bookkeeping and is left alone DELIBERATELY. The HeliThrust derivation table at
        /// the Huey spec computes each airframe's thrust as g + 0.35 * (that aircraft's real climb rate), so
        /// against the 0.45 actually in force the whole fleet climbs about 22 % slower than the real machines
        /// it was derived from. Fixing that is a change to the VERTICAL axis and to numbers strawberry signed
        /// off by feel; this rework is about the horizontal law, and quietly retuning every climb rate inside
        /// it is exactly the kind of change that compiles and ships wrong. Raised separately instead.</summary>
        const float HeliHeaveDamp = 0.45f;
        /// <summary>How much of HeliFallMax a shaft-aligned descent is allowed to reach. Mirrors the 0.9 the CLIMB
        /// side already applies via _heliLiftCap, and for the same stated reason -- "keeps a margin so the cap
        /// binds before the envelope does". Targeting the cap exactly, which is what the first cut did, leaves
        /// nothing to absorb position quantization (1/256 m, truncating) or a suspended sling load, both of which
        /// add to the fall rate the server actually measures.</summary>
        const float FallEnvelopeMargin = 0.9f;
        /// <summary>LIVE FLIGHT-MODEL KNOBS, driven by the `heliphys` console command (VoX 2026-08-17: "so we are
        /// essentially applying max speeds to the helis? Can we test removing those please"). Static because the
        /// question is about the FLEET's feel, not one airframe's, and because they exist to be A/B'd in the air.
        ///
        /// NONE OF THESE ARE SHIPPING DEFAULTS. They default to exactly the calibrated behaviour, so a build with
        /// nobody typing at the console flies identically to one without them. What they are for is answering
        /// "what would it feel like without the limiter" by flying it rather than arguing about it.
        ///
        /// THE MP CAVEAT, because it is not visible from the cockpit: VehicleReplication validates horizontal
        /// motion against SpeedMaxMps * 1.25 and vertical against HeliClimbMax with ZERO slack. Turning these up
        /// is free in singleplayer and gets a pilot ROLLED BACK on a server. Tune to taste here, then decide
        /// whether the spec numbers move to match -- do not ship a feel the envelope will reject.</summary>
        public static float HeaveDampScale = 1f;      // vertical resistance, 1 = calibrated (terminal fall 21.8 m/s)
        public static float DragScale = 1f;           // horizontal parasite drag, 1 = level terminal equals Speed_Max
        public static bool BackstopEnabled = true;    // the 1.25x hard wall that keeps the client inside the envelope
        /// <summary>Vertical resistance follows the DISC rather than the world vertical: tilt the shaft and the
        /// disc goes edge-on to the airflow, which is cos^2 of the tilt (one cosine resolving velocity onto the
        /// shaft, one resolving the force back to vertical). VoX got here from the feel -- "a horizontal heli has
        /// more vertical drag than a vertical heli right?" -- and the physics review recommended it originally.
        ///
        /// DESCENT ONLY -- but NOT for the reason originally given here, which was wrong and is worth recording
        /// rather than quietly deleting. The claim was that the same factor on the CLIMB side raises terminal
        /// climb ~22 % into HeliClimbMax's zero-slack check. The 22 % is real but it is a SAME-ATTITUDE
        /// comparison, and the server does not check climb at 25 deg -- it checks whatever the rate actually is.
        /// Terminal climb under the shaft form is strictly increasing in cos(tilt) for every airframe in the
        /// fleet, so its maximum is at LEVEL, where cos^2 = 1 and the shaft form IS the world form. Verified per
        /// airframe: max 8.32 m/s at 0 deg on a Huey, 11.36 at 0 deg on a Hind, both identical to the world-
        /// aligned figure. A symmetric version would LOWER climb at every tilted attitude and raise nothing the
        /// server looks at. It stays descent-only because the descent is the axis VoX was complaining about and
        /// a one-sided change is the smaller one -- not because the climb side was ever unsafe. ON BY DEFAULT since VoX 2026-08-18 ("can you make sure my preference is the defaut for
        /// testing") -- he reached this from the feel before seeing the code, so the fleet now flies his version
        /// without anyone typing at the console, and `heliphys shaft off` is how you get the old behaviour back.
        ///
        /// STILL UNRESOLVED AND DELIBERATELY NOT PAPERED OVER: HeliCrashExplodeSpeed is 15 m/s and was settled
        /// against the old 21.8 terminal fall. A floored dive now reaches the airframe's HeliFallMax (39.9 on a
        /// Huey), so a misjudged pullout is a fireball where it used to be a hard landing. That is a difficulty
        /// decision, not a physics one, so it is flagged rather than silently retuned alongside this.</summary>
        public static bool ShaftAlignedDescent = true;
        /// <summary>HOW MUCH OF THE SINK IS REDIRECTED FORWARD instead of destroyed, 0 = shipped behaviour.
        ///
        /// VoX's second complaint -- "when I level out it doesnt translate my falling speed into forward speed
        /// enough" -- is not a tuning problem. The horizontal equation of motion contains NO term that depends on
        /// vertical velocity: lift reads attitude and flatSpeed, parasite drag and the backstop read the flat
        /// vector only, and the heave damper is the single vel.Y-dependent force in the model but is applied
        /// along Vector3.Down, whose horizontal component is zero at every attitude. Conversion is therefore
        /// exactly 0 %, not merely low. Flying the identical pullout with entry sink rates of 0 and 60 m/s gives
        /// bit-identical exit speed, and ShaftAlignedDescent does not change that -- it only makes the fall
        /// faster, so it answers the first half of his sentence and none of the second.
        ///
        /// The fix falls out of the cos^2 derivation already documented above: "one cosine resolving velocity onto
        /// the shaft, one resolving the force back to vertical". The implementation kept only the vertical
        /// projection and silently discarded the IN-PLANE component -- which is precisely the forward push. This
        /// restores it, and because the vertical axis is untouched, terminal fall, the FallMax floor, ClimbMax,
        /// _heliLiftCap and the k derivation all stay exactly as calibrated.
        ///
        /// Signs come out right without special-casing: nose-down tilts b.Y forward, so a descent pushes FORWARD;
        /// a nose-up flare tilts it aft, so flaring brakes; a descending bank pushes into the turn.
        ///
        /// DEFAULT 0 -- OFF. This is a real feel change nobody has flown yet, so it ships inert and is opted into
        /// with `heliphys redirect 1`. At 1.0 a Huey exits a 200 m dive at ~22 m/s instead of ~6, peaking near the
        /// 1.25 MP envelope -- so anything above 1.0 needs a per-airframe envelope check before it goes further.</summary>
        public static float HeaveRedirect = 0f;
        /// <summary>How much draggier sideways than forwards. This is what replaces the old ForeAftBoost /
        /// LateralBoost pair, which multiplied THRUST to make leaning into a run build momentum ("increase the
        /// forward/back momentum when tilting forward/back") and lateral slip feel less eager than a drone.
        /// Resisting the sideways axis harder produces the same asymmetry from the side of the equation where
        /// it belongs: a fuselage genuinely does present far more area sideways than forwards, so the fore/aft
        /// axis keeps its speed and a sideways slide does not.</summary>
        const float HeliLateralDragRatio = 2.5f;
        /// <summary>Where the hard horizontal wall sits, as a multiple of Speed_Max. Above level flight's own
        /// terminal (1.0 by construction, since the drag coefficient is derived to put it there) and below the
        /// MP envelope's EnvelopeSlack of 1.25, so a dive can outrun cruise without ever producing a state the
        /// server would reject.</summary>
        const float HeliEnvelopeBackstop = 1.15f;
        /// <summary>Anti-collision beacon timing. 1.4 s between flashes is ~43 per minute, inside the real
        /// 40-45 civil range, and the flash itself is SHORT -- a pulse reads as a strobe, an even blink reads as
        /// a warning lamp on a dashboard.</summary>
        const float BeaconPeriod = 1.4f, BeaconFlash = 0.12f;
        /// <summary>How much of the start-up clip the rotor has to get through before it makes thrust, and
        /// how long the spin-up itself takes, as a FRACTION of that clip.
        ///
        /// 1.0 is strawberry's literal ask -- "only start generating thrust after the sound finishes" -- and
        /// heli_ignition.ogg is 8.10 s, so that is an eight-second startup during which the machine makes no
        /// lift whatsoever. That is realistic (a real turbine is slower still) but it is a large gameplay
        /// change rather than a detail: anything already airborne when it starts simply falls, and a helicopter
        /// spawned at 60 m reaches the ground before its rotor is legal. It is one number precisely because
        /// which value is RIGHT is a feel call, not an engineering one.
        ///
        /// 0.74 = 6.0 s of the 8.10 s clip, and it is MEASURED rather than picked. strawberry: "i think the
        /// heli ignition has a big fadeout tail. should overlap engine starting as it fades" -- and the
        /// clip's RMS envelope says exactly that: it holds 60-75 % of peak from 2.7 s to 5.7 s, then falls
        /// away monotonically (48 % at 6.0, 32 % at 6.9, 21 % at 7.2, 7 % at 7.8). So the START-UP ends and
        /// the fade begins at ~6.0 s, and thrust arriving there leaves the last 2.1 s of tail playing over
        /// a running engine. Gating on the full 8.10 s instead meant eight seconds of no lift, which drops
        /// anything already airborne.</summary>
        const float IgnitionThrustFraction = 0.74f;
        /// <summary>EFFECTIVE TRANSLATIONAL LIFT. In a hover a rotor is flying in its own downwash; as the
        /// machine translates it moves into undisturbed air, induced drag falls, and the same collective makes
        /// more thrust. Pilots feel it as a distinct surge and a lightening of the airframe as they come out
        /// of a hover, and it is why a machine too heavy to hover can often still fly away.
        ///
        /// THE GAIN IS BOUNDED BY A SIGNED-OFF BEHAVIOUR, not by taste. Hands off, the collective springs to
        /// IdleHoverFraction (0.92) of hover, so lift is 9.8 * 0.92 = 9.016 and the machine sinks gently --
        /// which is what VoX asked for. ETL multiplies that, so any gain at or above 9.8/9.016 - 1 = 0.087
        /// turns the hands-off sink into a hands-off CLIMB and silently deletes the behaviour.
        ///
        /// 0.05, NOT THE 0.087 THE ALGEBRA ALLOWS, because the bound is where the sink INVERTS and the
        /// behaviour dies well before that. At 0.08 the steady-state hands-off sink is 0.139 m/s -- 1.4 m of
        /// descent over ten seconds, which is a hover with a rounding error, not the "gentle sink" that was
        /// asked for. At 0.05 it is 0.74 m/s: still gentle, still unmistakably down. Sizing a gain to the
        /// point where a behaviour reverses leaves nothing of the behaviour.
        ///
        /// CORRECTION, recorded because the wrong version shipped in this file and in a commit message: the
        /// original justification here claimed a measurement of a +0.14 m/s CLIMB at 0.08, blamed on the
        /// collective settling above its spring target. That reading was a rig artefact -- the test zeroed
        /// vertical velocity while the collective was still at full, so the window opened with ~1.4 m/s of
        /// climb which decays on a 2.2 s time constant, and 4 s was not long enough for it to settle. The
        /// stated mechanism was wrong too: DriveHeli converges with MoveToward, which cannot overshoot its
        /// target. The number was real and the question it answered was "what is this machine doing one time
        /// constant in", not "where does it settle". Found by review, not by me.</summary>
        const float EtlGain = 0.05f;
        const float EtlOnset = 4f, EtlFull = 11f;   // m/s: starting to outrun the downwash, and fully clear of it
        /// <summary>Closest approach used in the ground-effect term, as a fraction of rotor radius.
        /// Cheeseman-Bennett diverges as z -> R/4, so the disc height is clamped here; at R/2 the factor is
        /// 1/(1 - 0.25) = 1.333, which is about what a real machine sees sitting on its skids.</summary>
        const float GroundEffectMinZ = 0.5f;

        /// <summary>The horizontal acceleration an airframe can sustain WITHOUT LOSING ALTITUDE: the steepest
        /// attitude whose remaining vertical thrust still holds the machine up, times the sine of it.
        ///
        /// This exists so the drag coefficient can be DERIVED rather than typed. Nothing about a real
        /// helicopter would tell us the right number anyway -- every vehicle in this game has GlobalMass 900
        /// regardless of what it is, and rho and mass are both folded into the coefficient -- so a hand-tuned
        /// table would be seven magic numbers whose provenance dies with the commit, and whose rank is
        /// INVERTED against real aircraft (the scrap minicopter ends up the draggiest, the Hind the least).
        /// Deriving it from HeliThrust and Speed_Max instead means the two authorities that already exist stay
        /// the only authorities, and retuning either one carries the drag along with it.
        ///
        /// Not a closed form because TiltThrustLoss sits inside the tilt term: leaning costs lift twice (the
        /// free cosine, then that extra bite), so the tilt that maximises horizontal thrust subject to holding
        /// altitude has no tidy solution. A tenth-of-a-degree sweep is exact enough and runs once per spec at
        /// build time. The loop breaks rather than continues because lift(theta)*cos(theta) decreases
        /// monotonically -- once an attitude sinks, every steeper one does too.</summary>
        static float LevelFlightAccel(float thrust)
        {
            float best = 0f;
            for (int i = 1; i <= 900; i++)
            {
                float th = Mathf.DegToRad(i * 0.1f);
                float lift = thrust * (1f - TiltThrustLoss * (1f - Mathf.Cos(th)));
                if (lift * Mathf.Cos(th) < 9.8f) break;   // any steeper and it descends -- not level flight any more
                // MAX, not last. The break is provably safe -- with c = cos(theta), vertical thrust is
                // T(0.45c + 0.55c^2), whose derivative T(0.45 + 1.1c) is positive for every c >= 0, so it
                // decreases monotonically in theta and no feasible attitude hides past an infeasible one.
                // The OBJECTIVE is not monotone though: lift*sin(theta) peaks at 57.9 deg. The altitude
                // constraint currently binds at 27.6-38.4 deg, well short of that, so taking the last feasible
                // angle happens to be the maximum today -- and stops being so above thrust 24.8, where it
                // would under-derive the coefficient and put top speed over spec. Cheap to just be correct.
                best = Mathf.Max(best, lift * Mathf.Sin(th));
            }
            // A rotor that cannot lift its own weight breaks on the first step and leaves best at 0, which
            // downstream becomes a drag coefficient of 0 -- an airframe with NO horizontal drag whatsoever,
            // limited only by the backstop. Nothing in the fleet is close (minimum thrust 11.8), but the
            // failure is silent and the guard is one line.
            return best > 0.01f ? best : 0.01f;
        }

        // ---- INERTIA + TURBULENCE (strawberry 2026-08-16: "adding inertia. joystick changes should feel
        // slower, heavier and more sluggish. like the heli actually has weight. as well as minor turbulence at
        // random intervals") ------------------------------------------------------------------------------
        //
        // Weight is TWO lags, not one. The stick first drives a smoothed COMMAND (this is the pilot's control
        // linkage and the rotor taking time to change its disc), and the airframe's angular velocity then
        // chases that command (this is the mass actually resisting). One lag alone still feels like a mouse
        // cursor with a delay -- it starts late but stops instantly. Two gives the overshoot-and-settle of
        // something with momentum, and crucially it is heavy coming OUT of an input as well as going in.
        Vector3 _cmdRate;      // the smoothed command the airframe is chasing
        Vector3 _turbKick;     // the current gust, decaying
        float _turbTimer;
        const float CommandSlew = 2.4f;        // stick -> commanded rate (the linkage)
        const float TurbMinGap = 1.6f, TurbMaxGap = 5.5f;   // seconds between gusts, at ALTITUDE (see TurbLowGapScale)
        const float TurbStrength = 0.42f;      // rad/s^2 of angular kick at full strength -- it is added to `cmd`, which is an angular ACCELERATION (see the ApplyTorque at the end of StepHeli), not a rate
        const float TurbDecay = 1.5f;          // how fast a gust bleeds away
        // TURBULENCE SCALES WITH HEIGHT ABOVE GROUND (strawberry: "make turbulence scale with vertical height, in
        // terms of frequency and severity. low to the ground should be relatively calm"). Measured AGL, not absolute
        // Y -- otherwise hugging a hilltop at 300 m would be as rough as open sky at 300 m, and the whole point is
        // that low-level flying is the calm regime.
        const float TurbCalmAgl = 12f;         // at or below this, as calm as it gets
        const float TurbFullAgl = 140f;        // at or above this, the full gusts the fleet was tuned with
        const float TurbLowSeverity = 0.15f;   // fraction of full strength down on the deck -- calm, not dead
        const float TurbLowGapScale = 3.0f;    // gusts this many times further apart down low
        const float TurbAglReach = 260f;       // probe length; no hit = open air, treat as full
        float _turbAgl = TurbFullAgl;          // cached, refreshed on the gust timer rather than every tick
        static readonly RandomNumberGenerator HeliRng = MakeHeliRng();
        static RandomNumberGenerator MakeHeliRng() { var r = new RandomNumberGenerator(); r.Randomize(); return r; }
        /// <summary>Test seam: the live gust, so a test can prove turbulence is real without waiting on a die roll.</summary>
        public Vector3 DebugTurbulence => _turbKick;
        public float DebugTurbAgl => _turbAgl;
        /// <summary>Test seam: turbulence OFF, so a control-response test measures the pilot and not the weather.</summary>
        public bool DebugNoTurbulence;
        /// <summary>Skip the start-up gate: full rotor immediately, thrust from the first tick.
        ///
        /// For rigs whose subject is something ELSE. A check about roll authority should not have to fly
        /// six seconds of ignition first -- padding every window to clear the gate makes each test slower,
        /// makes them all depend on a gameplay constant they do not care about, and (the reason this exists)
        /// silently turns them into tests of the START-UP whenever that number moves. The gate has its own
        /// dedicated check instead, which is where it belongs.</summary>
        public bool DebugInstantStart;

        // ---- ROTOR DAMAGE (VoX 2026-08-16) --------------------------------------------------------------
        // "give the main rotor and tail rotor independent HP values. main rotor hp low -> reduced thrust. tail
        // rotor hp low -> reduced turning. main rotor dead -> no more gaining vertical thrust, quickly lose
        // height. tail rotor dead, go into a spin."
        //
        // Two separate hitboxes per rotor, because the two ways a rotor dies are not the same event:
        //   the DISC (a thin cylinder swept by the blades) catches anything the blades strike -- trees,
        //     buildings, the ground -- and grinds the rotor down on a cooldown while contact persists;
        //   the HUB (a small box at the mast) is what a BULLET has to find. Shooting a rotor down should mean
        //     hitting the machinery, not clipping the tip of a 5 m disc.
        public enum HeliPart { Body, MainRotor, TailRotor }
        float _mainRotorHp, _mainRotorHpMax = 1f, _tailRotorHp, _tailRotorHpMax = 1f;
        float _mainStrikeCd, _tailStrikeCd;
        Area3D _mainDiscArea, _tailDiscArea;
        CpuParticles3D _mainRotorSmoke, _mainRotorFire, _tailRotorSmoke, _tailRotorFire;
        CpuParticles3D _mainStrikeFx, _tailStrikeFx;   // one-shot sparks per blade strike
        AudioStreamPlayer3D _strikeAudio;
        bool _rotorFxExtinguished;   // the cold wreck has had its rotor fires put out; do not relight them
        CpuParticles3D _bonkFx; float _crashCd, _recentTopSpeed;
        /// <summary>Impact speed that writes the machine off outright, ~54 km/h.
        ///
        /// Set against what the airframe can actually REACH, not picked as a round number. Horizontal top speed
        /// is 26 m/s, so flying into a cliff at cruise is always fatal. Vertically, damping caps terminal fall
        /// at 21.8 m/s (g / HeliHeaveDamp) but a 45 m drop only arrives at ~18 -- so a threshold of 19, which
        /// is what this was, meant falling out of the sky from any survivable height could NOT write the
        /// machine off, while a horizontal crash could. 15 puts a genuine plummet and a fast collision on the
        /// same side of the line, and leaves the 10-12 m/s arrivals of a botched landing survivable.
        ///
        /// The terminal figure here read "~28 m/s" until 2026-08-17, which is g / 0.35 -- the damping the file
        /// SAID it had. The value actually in force was 0.45 (see HeliHeaveDamp), giving 21.8. The companion
        /// 45 m number beside it was measured and is right, which is the tell: one figure was observed and the
        /// one next to it was reasoned from a constant that was never true. The threshold of 15 was chosen
        /// against the measured arrival speeds, so it does not move -- but the "18.5 m fall is fatal" figure
        /// quoted elsewhere IS a 0.35 number: at 0.45 you need about 23 m to reach 15 m/s.</summary>
        const float HeliCrashExplodeSpeed = 15f;
        /// <summary>Below this an impact is not a crash at all -- setting down firmly, brushing a wall while
        /// hovering. Without a floor, every landing would chip the airframe.</summary>
        const float HeliBonkSpeed = 5.5f;
        /// <summary>Test seam: how many survivable impacts this machine has taken.</summary>
        public int DebugBonkCount { get; private set; }
        public float DebugPrevSpeed => _recentTopSpeed;
        /// <summary>Speed at the moment of the last detected impact. Captured AT the crash: reading the live
        /// peak afterwards reports 0, because it decays away while the wreck sits there.</summary>
        public float DebugLastImpactSpeed { get; private set; }
        public float DebugSpawnGrace => _spawnGrace;
        /// <summary>Health fraction at which a rotor starts smoking. Above it the rotor is scuffed, not
        /// failing, and a machine that smokes from the first bullet tells the pilot nothing.</summary>
        const float RotorSmokeAt = 0.7f;
        /// <summary>Test seams for the rotor damage FX. Asserted on the EMITTER state rather than on health,
        /// because "hurt rotors smoke" is a claim about what the player can see -- reading the health back
        /// would just re-assert the number the test itself set.</summary>
        public bool DebugMainRotorSmoking => _mainRotorSmoke != null && _mainRotorSmoke.Emitting;
        public bool DebugMainRotorBurning => _mainRotorFire != null && _mainRotorFire.Emitting;
        public bool DebugTailRotorSmoking => _tailRotorSmoke != null && _tailRotorSmoke.Emitting;
        public bool DebugTailRotorBurning => _tailRotorFire != null && _tailRotorFire.Emitting;
        public int DebugMainRotorSmokeAmount => _mainRotorSmoke?.Amount ?? 0;
        /// <summary>Test seam: the main rotor's visual blade phase, so a test can prove the disc has actually
        /// STOPPED rather than merely that the spool number is zero -- the old constant spin term made those
        /// two different facts.</summary>
        public float DebugRotorPhase => _rotorSpin;
        /// <summary>Balance seams: the tuned handling numbers, so a test can pin the fleet's ORDERING without
        /// flying every airframe. The absolute values are taste; the order is the specification.</summary>
        public float DebugRollAuthority => _heliRollTq * SlingAgility;   // LIVE: what the machine actually rolls with right now
        public float DebugThrust => _heliThrust;
        public float DebugHeliDragK => _heliDragFwd;   // 1/m, derived at build time -- see LevelFlightAccel
        public float DebugHeliLiftCap => _heliLiftCap;
        public float DebugEnginePitch => _engineAudio?.PitchScale ?? 0f;
        public float DebugIgnitionPitch => _ignitionAudio?.PitchScale ?? 0f;
        /// <summary>The value the flight model ACTUALLY used this tick, not a fresh recompute: a test that
        /// re-probes could agree with the code while the code disagreed with itself, which is the failure a
        /// debug accessor exists to catch. Reads 1.0 until StepHeli has run once on this machine.</summary>
        public float DebugGroundEffect => _groundEffect;
        Vector3 _mainHubCentre, _mainHubHalf, _tailHubCentre, _tailHubHalf;
        // A blade strike GRINDS the rotor down over time rather than killing it outright (strawberry
        // 2026-08-16: "rotors' blade damage to be ticked over time instead of instantly killing the rotor").
        // Was 34 a tick against ~112 max, i.e. four ticks -- under two seconds of contact, which reads as
        // instant death rather than as damage. At 7 it takes ~16 ticks, so clipping something is a mistake you
        // can hear happening and fly out of, and sitting in it still finishes you.
        const float BladeStrikeInterval = 0.22f;
        const float BladeStrikeDamage = 7f, TailStrikeDamage = 6f;
        /// <summary>What the blades do TO what they hit, per strike tick. Higher than what the rotor takes:
        /// a spinning rotor beats a fence convincingly and comes off worse than the fence only over time.</summary>
        const float MainBladePropDamage = 34f, TailBladePropDamage = 22f;
        /// <summary>Spool below which the blades are not moving fast enough to hurt anything, including
        /// themselves. Well under the disc-swap point, so a rotor that still LOOKS like blades can still cut.</summary>
        const float BladeStrikeMinSpool = 0.12f;
        /// <summary>Unopposed main-rotor torque on the fuselage once the tail rotor is dead (rad/s^2 at full
        /// power). Sized to be clearly unrecoverable by pedal input -- the pedals are gone anyway -- but slow
        /// enough that a pilot who cuts collective immediately can still put it down.</summary>
        const float TailLossTorque = 1.35f;

        public float MainRotorHealth => _mainRotorHp;
        public float TailRotorHealth => _tailRotorHp;
        public float MainRotorNorm => _mainRotorHpMax > 0f ? Mathf.Clamp(_mainRotorHp / _mainRotorHpMax, 0f, 1f) : 0f;
        public float TailRotorNorm => _tailRotorHpMax > 0f ? Mathf.Clamp(_tailRotorHp / _tailRotorHpMax, 0f, 1f) : 0f;
        public bool MainRotorDead => _heli && _mainRotorHp <= 0f;
        public bool TailRotorDead => _heli && _tailRotorHp <= 0f;

        public void DamageMainRotor(float amount)
        {
            if (!_heli || amount <= 0f) return;
            _mainRotorHp = Mathf.Max(0f, _mainRotorHp - amount);
        }
        public void DamageTailRotor(float amount)
        {
            if (!_heli || amount <= 0f) return;
            _tailRotorHp = Mathf.Max(0f, _tailRotorHp - amount);
        }
        /// <summary>Rotor bar colour: healthy green through amber to the hull's red as it fails, so a glance
        /// at the billboard says WHICH rotor is going without reading the bar length.</summary>
        static Color RotorBarColor(float norm)
            => norm > 0.6f ? new Color(0.35f, 0.80f, 0.35f)
             : norm > 0.25f ? new Color(0.90f, 0.72f, 0.20f)
             : new Color(0.85f, 0.22f, 0.18f);

        public void KillMainRotor() { if (_heli) _mainRotorHp = 0f; }
        public void KillTailRotor() { if (_heli) _tailRotorHp = 0f; }

        /// <summary>Which part a world-space impact landed on. The HUB boxes are tested in the vehicle's own
        /// local space, so they follow the airframe through any attitude -- a world-axis test would drift off
        /// the mast the moment the machine banked, which is most of the time it is being shot at.</summary>
        /// <summary>Is anything OTHER THAN THIS VEHICLE inside the disc?
        ///
        /// The exclusion is the whole method. The disc has to watch the vehicle layer (bit 5) so it notices
        /// other vehicles, but this vehicle is ON bit 5, so a bare HasOverlappingBodies() sees its own hull
        /// forever -- which ground both rotors to zero within seconds of every spawn. The symptom was total:
        /// no lift, no yaw, no rotation, every helicopter in the suite free-falling identically, healthy and
        /// damaged alike. A self-overlap reads exactly like a physics failure.</summary>
        /// <summary>Rotor damage FX: smoke that thickens as a rotor is worn down, fire once it is dead
        /// (strawberry 2026-08-16: "the rotors should smoke more when hurt and set fire when broken").
        ///
        /// "More" is done with emission RATE rather than by switching between a light and a heavy emitter the
        /// way the hull does, because a rotor degrades continuously under blade strikes -- a two-step plume
        /// would read as a state change at an arbitrary threshold instead of as something progressively
        /// failing. A dead rotor keeps smoking underneath the fire; fire alone looks like a decoration sitting
        /// on an otherwise healthy machine.</summary>
        /// <summary>One burst of sparks + a metal clang for a single blade strike. Restarts the emitter each
        /// time (OneShot + Explosiveness 1) so repeated strikes read as repeated hits rather than merging into
        /// one continuous shower.</summary>
        /// <summary>A survivable impact: debris, and the same metal hit the blades use. Counted so a test can
        /// assert a bonk happened at all -- "it lost health" would also pass on a machine that was quietly
        /// bleeding out for some entirely different reason.</summary>
        void BonkFx()
        {
            DebugBonkCount++;
            if (_bonkFx != null) { _bonkFx.Emitting = false; _bonkFx.Restart(); _bonkFx.Emitting = true; }
            if (_strikeAudio != null) _strikeAudio.Play();
        }

        void BladeStrikeFx(CpuParticles3D sparks)
        {
            if (sparks != null) { sparks.Emitting = false; sparks.Restart(); sparks.Emitting = true; }
            if (_strikeAudio != null) _strikeAudio.Play();
        }

        /// <summary>Sparks fly only when a bare rim is actually SCRUBBING: popped, touching ground, and moving.
        /// Gated on the wheel's own contact rather than the body's speed so a car airborne over a jump stops
        /// throwing sparks from a wheel touching nothing, and so a popped wheel that happens to be off the
        /// ground on a camber stays quiet while the others spark.</summary>
        void UpdateTireSparks()
        {
            if (_tireSparks == null) return;
            float v2 = LinearVelocity.LengthSquared();
            for (int i = 0; i < _tireSparks.Length; i++)
            {
                var fx = _tireSparks[i];
                if (fx == null || !GodotObject.IsInstanceValid(fx)) continue;
                bool scrub = i < _tirePopped.Length && _tirePopped[i]
                             && v2 > 4f                                   // ~2 m/s: rolling, not creeping
                             && _wNodes != null && i < _wNodes.Length
                             && _wNodes[i] != null && _wNodes[i].IsInContact();
                if (fx.Emitting != scrub) fx.Emitting = scrub;
            }
        }

        void UpdateRotorFx()
        {
            if (_rotorFxExtinguished) return;   // the wreck has cooled; leave it cold
            Fx(_mainRotorSmoke, _mainRotorFire, MainRotorNorm, MainRotorDead, 16);
            Fx(_tailRotorSmoke, _tailRotorFire, TailRotorNorm, TailRotorDead, 12);

            static void Fx(CpuParticles3D smoke, CpuParticles3D fire, float norm, bool dead, int maxAmount)
            {
                if (smoke != null)
                {
                    bool hurt = norm < RotorSmokeAt;
                    if (smoke.Emitting != hurt) smoke.Emitting = hurt;
                    if (hurt)
                    {
                        // 0 at the smoke threshold -> 1 at destroyed, so the plume grows as it is worn down.
                        float t = Mathf.Clamp(1f - norm / RotorSmokeAt, 0f, 1f);
                        smoke.Amount = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(maxAmount * 0.25f, maxAmount, t) * ParticleFx.AmountScale));
                    }
                }
                if (fire != null && fire.Emitting != dead) fire.Emitting = dead;
            }
        }

        /// <summary>Is there ground within a short reach below the airframe? Asked with a raycast because it
        /// has to stay truthful while the body is FROZEN, which rules out contact counts.</summary>
        bool GroundedByRay()
        {
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return false;
            Vector3 from = GlobalPosition;
            var q = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * (_groundClearance + 0.45f));
            // Exclude our OWN slung equipment. The magnet is aircraft kit on the vehicle layer hanging directly
            // under the hull, so an un-excluded ray reads it as ground: the crane "landed" every tick, stowed the
            // winch, un-landed the next tick with the magnet gone, and redeployed -- a 1-tick deploy/stow oscillation
            // that presented as a magnet that would not pay out past ~1.2 m. Nothing in the cable maths was wrong.
            q.Exclude = SlingExclude();
            q.CollisionMask = (1u << 0) | (1u << 5);
            return space.IntersectRay(q).Count > 0;
        }

        /// <summary>Height above the ground directly below, for turbulence. Long probe, WORLD layer only -- a gust
        /// regime should not change because another aircraft or a slung container happened to pass underneath. No
        /// hit means open air, which reads as full altitude.</summary>
        float ProbeAgl()
        {
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return TurbFullAgl;
            Vector3 from = GlobalPosition;
            var q = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * TurbAglReach);
            q.Exclude = SlingExclude();
            q.CollisionMask = 1u << 0;
            var hit = space.IntersectRay(q);
            return hit.Count > 0 ? Mathf.Max(0f, from.Y - ((Vector3)hit["position"]).Y) : TurbFullAgl;
        }

        /// <summary>Build the belly beacon's mesh from a nav-light lens: ONE lamp, re-centred on the origin, and
        /// turned to face DOWN.
        ///
        /// Two things make the raw lens wrong for a belly fitting. It can contain more than one lamp -- the orca's
        /// spans 0.96 m in X where every other airframe's is 0.16-0.26, i.e. two lenses in one mesh, which is why its
        /// belly light appeared as two. And the lens is a thin slab authored facing SIDEWAYS off the hull (thin in X,
        /// ~0.37 in Y and Z), so dropped on the belly unrotated it points out the side instead of at the ground.
        ///
        /// So: split the triangles into X clusters, keep the biggest single cluster, centre it, and rotate its thin
        /// axis from +/-X onto -Y.</summary>
        static ArrayMesh BeaconLensMesh(Mesh src)
        {
            if (src == null || src.GetSurfaceCount() == 0) return null;
            var arr = src.SurfaceGetArrays(0);
            if (arr.Count == 0 || arr[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.Nil) return null;
            var verts = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var idx = arr[(int)Mesh.ArrayType.Index].VariantType != Variant.Type.Nil
                ? arr[(int)Mesh.ArrayType.Index].AsInt32Array() : null;
            int triCount = (idx != null ? idx.Length : verts.Length) / 3;
            if (triCount == 0) return null;
            Vector3 V(int t, int k) => idx != null ? verts[idx[t * 3 + k]] : verts[t * 3 + k];

            // Cluster triangle centroids along X, splitting wherever there is a gap wider than a lamp.
            var cx = new float[triCount];
            for (int t = 0; t < triCount; t++) cx[t] = (V(t, 0).X + V(t, 1).X + V(t, 2).X) / 3f;
            var sorted = (float[])cx.Clone(); System.Array.Sort(sorted);
            float span = sorted[^1] - sorted[0];
            float gap = Mathf.Max(0.08f, span * 0.25f);
            float bestLo = sorted[0], bestHi = sorted[^1];
            {
                float lo = sorted[0], prev = sorted[0]; int count = 1, best = -1;
                for (int i = 1; i <= sorted.Length; i++)
                {
                    bool end = i == sorted.Length;
                    if (!end && sorted[i] - prev <= gap) { count++; prev = sorted[i]; continue; }
                    if (count > best) { best = count; bestLo = lo; bestHi = prev; }
                    if (end) break;
                    lo = prev = sorted[i]; count = 1;
                }
            }
            // Face the thin axis downward: a lamp on the starboard side points +X, one on port points -X.
            float mid = (bestLo + bestHi) * 0.5f;
            var turn = new Basis(Vector3.Back, Mathf.DegToRad(mid >= 0f ? -90f : 90f));

            var keep = new System.Collections.Generic.List<Vector3>();
            for (int t = 0; t < triCount; t++)
            {
                if (cx[t] < bestLo - 1e-3f || cx[t] > bestHi + 1e-3f) continue;
                keep.Add(V(t, 0)); keep.Add(V(t, 1)); keep.Add(V(t, 2));
            }
            if (keep.Count < 3) return null;
            var bounds = new Aabb(keep[0], Vector3.Zero);
            foreach (var p in keep) bounds = bounds.Expand(p);
            Vector3 c = bounds.GetCenter();
            var outArr = new Godot.Collections.Array();
            outArr.Resize((int)Mesh.ArrayType.Max);
            var final = new Vector3[keep.Count];
            for (int i = 0; i < keep.Count; i++) final[i] = turn * (keep[i] - c);   // centred on the origin, then turned to face down
            outArr[(int)Mesh.ArrayType.Vertex] = final;
            var am = new ArrayMesh();
            am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, outArr);
            return am;
        }

        // Self + anything we are carrying: a downward probe must never mistake our own load for the ground.
        Godot.Collections.Array<Rid> SlingExclude()
        {
            var ex = new Godot.Collections.Array<Rid> { GetRid() };
            if (_magnet != null && IsInstanceValid(_magnet))
            {
                ex.Add(_magnet.GetRid());
                if (_magnet.Held != null && IsInstanceValid(_magnet.Held)) ex.Add(_magnet.Held.GetRid());
            }
            return ex;
        }

        /// <summary>Rotor thrust multiplier from GROUND EFFECT, 1.0 with nothing underneath.
        ///
        /// Within about a rotor diameter of the ground the downwash cannot escape sideways fast enough, the
        /// rotor's induced velocity falls, and the same collective makes more thrust -- the "cushion" you feel
        /// settling onto a pad, and what a heavy machine uses to stagger into the air. Cheeseman-Bennett:
        /// T_ige / T_oge = 1 / (1 - (R / 4z)^2), for disc height z and rotor radius R. It decays fast and
        /// honestly: 1.33 at half a radius, 1.07 at one radius, 1.02 at two.
        ///
        /// ITS OWN RAYCAST, deliberately, rather than reusing GroundedByRay. That one is a landing-gear
        /// contact test and reaches _groundClearance + 0.45, about 1.4 m; a Huey's rotor is 11 m across, so a
        /// probe that short cannot see this effect at all. This one reaches two radii, which is where the term
        /// has decayed to nothing anyway.
        ///
        /// The mask is bit 0, which is world geometry AND vehicle bodies -- vehicles sit on bit0|bit5 (see
        /// _baseCollisionLayer), so bit 0 alone does NOT exclude them and it would be wrong to claim it does.
        /// That is left as it is on purpose: a surface under the disc is a surface, and a helicopter hovering
        /// low over a truck really is in ground effect. It is only for a LANDING test that "is that thing
        /// under me the ground?" needs the distinction. Props (bit 6) are excluded, since a bush is not a
        /// surface the downwash builds a cushion against.</summary>
        float GroundEffect()
        {
            if (!_heli || _rotorRadius < 0.01f) return 1f;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return 1f;
            // FROM THE ROTOR HUB, not the fuselage origin. Cheeseman-Bennett's z is the height of the DISC, and
            // the hub sits 1.12 m (scoutcopter) to 4.18 m (Hind) above the origin. Measuring from the origin
            // overstated the cushion by 11-22 % on the deck and, worse, shifted the whole decay curve upward by
            // that offset -- a Hind kept a meaningful boost until its fuselage was at 2R, with the disc nearly
            // three rotor radii up. It also made every airframe pin to the R/2 clamp while parked, so the clamp
            // was silently standing in for the geometry instead of guarding the pole.
            Vector3 from = ToGlobal(_mainHubCentre);
            var q = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * (_rotorRadius * 2f));
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            q.CollisionMask = 1u << 0;   // WORLD geometry only -- not vehicles (1<<5), not props (1<<6)
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) return 1f;
            float z = Mathf.Max(from.Y - ((Vector3)hit["position"]).Y, _rotorRadius * GroundEffectMinZ);
            float r = _rotorRadius / (4f * z);
            return 1f / Mathf.Max(1f - r * r, 0.1f);
        }

        /// <summary>Seam to the authoritative destructibles, mirroring PlayerController.NetDamageObject.
        /// STATIC because Vehicles are built by a factory with no per-instance wiring point; MpLoopback sets it
        /// alongside the player's. Null in pure --direct SP, where props are inert anyway.</summary>
        public static System.Action<int, float> NetDamageObject;

        bool DiscStruck(Area3D disc, float propDamage)
        {
            bool hit = false;
            foreach (var body in disc.GetOverlappingBodies())
            {
                if (body == this || !GodotObject.IsInstanceValid(body)) continue;
                hit = true;
                // THE BLADES CUT BACK (strawberry 2026-08-16: "make the rotors apply damage to props that
                // collide with them each tick where the rotor would take damage"). Same cadence as the rotor's
                // own damage by construction -- this only runs on a strike tick.
                //
                // "once the props are destroyed make sure they stop applying damage" needs no guard here:
                // DestructibleField.SetAlive drops a broken prop's CollisionLayer to 0, so it leaves the disc
                // and stops being found. That holds in both directions -- a dead prop neither takes further
                // blade damage nor keeps grinding the rotor. Asserted in the tests rather than assumed.
                if (propDamage > 0f && body.HasMeta(DestructibleField.MetaKey))
                    NetDamageObject?.Invoke((int)body.GetMeta(DestructibleField.MetaKey), propDamage);
            }
            return hit;
        }

        public HeliPart ResolveHitPart(Vector3 worldPoint)
        {
            if (!_heli) return HeliPart.Body;
            Vector3 local = ToLocal(worldPoint);
            if (InBox(local, _mainHubCentre, _mainHubHalf)) return HeliPart.MainRotor;
            if (InBox(local, _tailHubCentre, _tailHubHalf)) return HeliPart.TailRotor;
            return HeliPart.Body;
            static bool InBox(Vector3 p, Vector3 c, Vector3 h)
                => h.X > 0f && Mathf.Abs(p.X - c.X) <= h.X && Mathf.Abs(p.Y - c.Y) <= h.Y && Mathf.Abs(p.Z - c.Z) <= h.Z;
        }
        public bool IsHeli => _heli;
        public bool IsPlane => _plane;
        public bool HasRetractGear => _gearPivots != null;   // driven vehicle has retractable gear (jet) -> G toggles it
        public void ToggleGear()
        {
            GD.Print($"[GEAR] ToggleGear deploy={_gearDeploy:0.###} grounded={GroundedByRay()} wantDown={_gearWantDown} afloat={_afloat} pgm={_planeGroundMode} pivots={_gearPivots!=null}");
            if (_gearPivots == null) { GD.Print("[GEAR] blocked: no pivots"); return; }
            if (_gearDeploy > 0.001f && _gearDeploy < 0.999f) { GD.Print("[GEAR] blocked: mid-fold"); return; }
            if (_gearWantDown && (GroundedByRay() || _planeGroundMode) && !_afloat) { GD.Print("[GEAR] blocked: ground-lock (retract only when airborne)"); return; }
            _gearWantDown = !_gearWantDown;
            GD.Print($"[GEAR] TOGGLED -> wantDown={_gearWantDown}");
        }
        public bool HasWheels => _wNodes != null && _wNodes.Length > 0;   // a WHEELED plane seats on spawn; a floatplane (no wheels) drops onto the water
        float _groundClearance;
        /// <summary>Distance from the body origin down to its lowest collision point (skids, hull floor).</summary>
        public float GroundClearance => _groundClearance;
        /// <summary>Seat this vehicle ON a ground point rather than dropping it from a guessed height -- the
        /// lowest collision point lands on <paramref name="ground"/>, plus a hair so it is not born intersecting.
        /// A 2 mm interpenetration at spawn is a solver impulse, and on a skidded airframe with no suspension
        /// that reads as the helicopter flinging itself sideways the moment it appears.</summary>
        public void PlaceOnGround(Vector3 ground)
        {
            GlobalPosition = ground + Vector3.Up * (_groundClearance + 0.02f);
            // A WHEELED plane on UNEVEN terrain: single-point seating buries whatever wheel sits over a rise
            // -- classically the far-forward NOSE wheel ("front wheel stuck under the ground"), which then
            // spawns the airframe interpenetrating and the solver flings it (the "freaks out + slides"). Probe
            // straight down under each wheel and RAISE the body until the highest-ground wheel clears, so nothing
            // is born inside the terrain; the suspension then extends the low wheels down onto it. (master 2026-08-18)
            if (_plane && _wNodes != null && _wNodes.Length > 0) SeatWheelsClear(0.06f);
        }
        void SeatWheelsClear(float margin)
        {
            var space = GetWorld3D()?.DirectSpaceState; if (space == null) return;
            float maxRaise = 0f;
            foreach (var w in _wNodes)
            {
                if (w == null) continue;
                Vector3 wp = w.GlobalPosition;
                var q = PhysicsRayQueryParameters3D.Create(wp + Vector3.Up * 2f, wp + Vector3.Down * 4f);
                q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
                q.CollisionMask = (1u << 0) | (1u << 5);
                var hit = space.IntersectRay(q);
                if (hit.Count == 0) continue;
                float groundY = ((Vector3)hit["position"]).Y;
                float wheelBottomY = w.GlobalPosition.Y - w.WheelRadius;
                float needed = (groundY + margin) - wheelBottomY;
                if (needed > maxRaise) maxRaise = needed;
            }
            if (maxRaise > 0f) GlobalPosition += Vector3.Up * maxRaise;
        }

        /// <summary>Rotor spool 0..1. Thrust scales with its SQUARE (a rotor at half speed makes a quarter of
        /// the lift), so a cold start has to spin up before it will leave the ground.</summary>
        public float RotorSpool => _rotorRpm;
        /// <summary>Put the rotor straight to full for a machine that is spawned ALREADY FLYING (NPC traffic, and
        /// any rig that starts a helicopter in mid-air). Even with DebugInstantStart the disc still winds up over
        /// SpoolUpSeconds = 3.2 s, and an aircraft spawned at cruise height simply falls out of the sky during that
        /// -- it hit the ground and detonated before the AI had any thrust to fly with.</summary>
        public void SpawnRotorRunning() { EngineOn = true; _rotorRpm = 1f; }
        /// <summary>Test seam: the collective/yaw/pitch/roll the flight model is currently flying on.</summary>
        public Vector4 DebugHeliInput => new Vector4(_inCollective, _inYaw, _inPitch, _inRoll);
        float _heliClimbMax, _heliFallMax;
        /// <summary>This spec's VERTICAL envelope caps (m/s); 0 = inherit the retail car defaults. Published at
        /// spawn alongside Speed_Max so the MP plausibility check bounds a helicopter by what a helicopter does
        /// instead of by what a car falling off a hill does.</summary>
        public float ClimbMaxMps => _heliClimbMax;
        public float FallMaxMps => _heliFallMax;
        bool _parked, _handbraking; float _spawnGrace = 2.5f; Vector3 _velAvg, _angAvg;   // -> SLEEPS once majority-grounded + the LOW-PASSED velocity/spin are low (jitter-immune, d9588d3); _spawnGrace lets a fresh car DROP to terrain first
        bool _asleep; float _wakeGrace;   // _asleep: WE put it to sleep (vs the engine, or never). _wakeGrace: seconds of guaranteed live physics after something woke it -- see the settle block
        Node3D _doorPivotA, _doorPivotB; float _doorT, _doorHold; bool _doorOpenWanted; float _doorFoldDeg = 90f;   // bi-fold door (bus): fold 0..1, hold-open timer
        float _driveIdle = 999f;   // seconds since anything last called Drive() on this car. UNATTENDED is a fact about the present, not about how the car was spawned -- see the settle block
        float _tankYawGain;   // TankYawGain scaled to THIS hull's mass (0 = unset -> fall back to the constant); see BuildByName
        float _prevSpeed;   // last frame's speed, to detect a sudden drop = a crash (collision/ram damage)
        float _deadTimer = -1f; bool _exploded, _husk; CpuParticles3D _smoke, _smoke0, _fire; OmniLight3D _fireLight;
        float _burnTime = -1f;   // seconds since the wreck caught fire (master lifecycle): <40 full, 40-60 dying down, 60 out+light killed, 360 despawn
        CpuParticles3D[] _wheelDust;   // per-WHEEL dust from the ground contact point (src Wheel.cs TireMotionEffectInstance is per-wheel); tinted by the Surf under each wheel
        PlayerController.Surf[] _wheelSurf; float _dustCheckT, _dustLogT;   // cached ground material per wheel (raycast, throttled); _dustLogT throttles UG_DUSTDEBUG
        MeshInstance3D _bodyMesh; AudioStreamPlayer3D _explosionAudio; Vector3 _firePos;   // damage/explosion (source askDamage/explode); _husk = settled wreck, sim killed; _firePos = engine-bay local offset
        const float ExplodeDelay = 4f, SmokeHealth = 200f, HeavySmokeHealth = 100f;   // source EXPLODE=4s, SMOKE_1<200, SMOKE_0<100
        // FOOT BRAKE. 1.5, not the 6 that shipped, because 6 stands the car on its nose. strawberry, after
        // driving it: "on braking/direction changes the car kicks up the front/back wheels." Measured on the
        // jeep braking from 12 m/s -- peak deceleration, peak pitch, fewest wheels touching, and the stop:
        //
        //   6.0   4.43 g   52.1 deg   0/4 wheels   3.3 m   <- pole-vaults, fully airborne for ~1 s
        //   3.0   2.24 g    3.8 deg   2/4          5.5 m
        //   2.0   1.50 g    1.8 deg   2/4          5.6 m
        //   1.5   1.13 g    1.4 deg   4/4          6.7 m   <- all four planted, and a real car's 1 g
        //   1.0   0.77 g    0.9 deg   4/4          9.9 m
        //
        // 4.4 g is more retardation than an F1 car and the jeep was leaving the ground under it, which is the
        // whole of the reported symptom. The line this replaces already knew: "raw .dat Brake too weak, but
        // 15/35 flipped the car onto its nose". 15 was reduced to 6 to stop that -- and 6 still flipped it,
        // just less often than a full lap of the probe would show. This is PRE-EXISTING, not from the mass or
        // suspension work: f9ef9b0c measures 4.49 g and 48.2 deg with the same wheels off the ground.
        const float FootBrakeScale = 1.5f, HandbrakeScale = 13f;   // Godot Brake calibration (raw .dat Brake too weak, but 15/35 flipped the car onto its nose -- master); S foot-brake vs Space handbrake bite
        public bool Exploded => _exploded;
        public bool OnFire => _deadTimer >= 0f || _exploded;   // caught fire at 0 HP (burning toward explosion) or a wreck -> engine is DEAD + unfixable (master)
        VehicleWheel3D[] _wNodes; MeshInstance3D[] _wMeshes;   // wheels: VehicleWheel3D auto-rolls its node (mesh child inherits it), so no manual spin. _wMeshes kept for debris/hide.
        Node3D[] _gearPivots; Vector3[] _gearAxis; float[] _gearAng; float _gearDeploy = 1f;   // retractable gear (jet): per-wheel hinge pivots carry the visual; _gearDeploy lerps 1=down/deployed -> 0=up/retracted
        bool _gearWantDown = true; bool _gearPhysOn = true; float[] _wheelSuspF, _wheelFricF;   // manual G-toggle target (starts DOWN), wheel-physics-active flag, + original suspension/friction to restore on deploy (master 2026-08-18)
        Mesh _wheelMeshRef; Material _wheelMatRef; float _wheelR;   // kept so the wheels can fly off as debris on explode
        public static float GlobalMass = 900f;   // all vehicles share one mass (the source does: Rigidbody mass = 2.0 for every vehicle)
        float[] _gears; float _reverseGear, _shiftUpRpm; float _engineRpm = 1000f; int _gear = 1;   // engine RPM + gear sim
        float _wheelbase;   // front-to-rear axle span from the spec wheels -- the steering cap needs the real geometry
        float _specSpeedMax;   // spec SpeedMax before TopSpeedBuff -- the reference the STEERING fade uses (and an L1 baseline)
        float _clutchT;   // >0 while drive is disconnected mid-shift
        public bool Declutched => _clutchT > 0f;   // test/HUD seam
        float _peakTorque, _dragK, _rollK, _driveR, _shiftCd; int _nTraction;   // drivetrain: peak engine torque (Nm), aero drag coeff (N per (m/s)^2), rolling resistance (N), driven wheel radius, shift lockout, traction wheel count
        AudioStreamPlayer3D _engineAudio, _ignitionAudio; bool _ignitionFired; float _idlePitch = 1f, _maxPitch = 2f, _idleVol = 0.75f, _maxVol = 1f;   // EngineRPMSimple sound
        float _engineWind = -1f, _windPitch0, _windVol0;   // engine-off WIND-DOWN (master 2026-09-04): -1 = not winding; else 0..1 progress
        const float EngineWindDownSec = 1.4f;              // the loop sags in pitch and dies over this, then STOPS (it used to cut to -80 dB)
        const float EngineVolumeBoost = 2.8f;   // 1.5 -> 2.8 (strawberry 2026-09-04 "make all vehicle engines much louder and travel much further"); distances raised with it below   // every engine loop +50% louder (strawberry 2026-07-15) -- amplitude x1.5 = +3.5 dB
        const float IdleRpm = 1000f, MaxRpm = 6000f;   // source EngineIdleRPM / EngineMaxRPM
        // ---- DRIVETRAIN. A real one: an engine that makes TORQUE as a function of its own RPM, a gearbox
        // that MULTIPLIES that torque, and a top speed that falls out of DRAG instead of out of an
        // if-statement. strawberry: "engine rpm = speed rn, there arent mechanics for torque. it feels like
        // a video game car."
        const float RedlineFrac  = 0.90f;    // upshift + rev-limit point, as a fraction of MaxRpm
        const float TorquePeakN  = 0.60f;    // where peak torque sits in the usable rpm band
        const float TorqueFallLo = 1.25f;    // quadratic falloff BELOW the peak (gentle)
        const float TorqueFallHi = 1.875f;   // ...and ABOVE it (steeper -- holding a gear past peak costs you)
        const float TorqueFloor  = 0.35f;    // an engine still pulls off-peak; it does not stop
        // UG_BUFF overrides this so the scale-invariance ACCEPTANCE TEST can drive the rescale from
        // outside the build. A constant nobody can vary is a constant nobody can prove scale-invariant --
        // and measured 2026-08-28, the drivetrain suite only passes in a narrow band around 2.0: the tank's
        // coastdown climbs 3.84 / 4.39 / 4.64 across buff 1.3 / 2.0 / 2.5 against a fixed 4.0 limit, while
        // at 1.3 the jeep instead fails "top speed clears the old hard cap". Two absolute thresholds
        // failing in OPPOSITE directions. Vary this to see it.
        static readonly float TopSpeedBuff = ParseBuff();
        static float ParseBuff()
        {
            var e = System.Environment.GetEnvironmentVariable("UG_BUFF");
            return (e != null && float.TryParse(e, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var f) && f > 0f) ? f : 2.0f;
        }     // strawberry: "a big thing is buffing top speeds", then 2026-08-24 "increase the cap for vehicle top speeds across the board" -- 1.6 -> 2.0
        const float GearStep     = 1.35f;    // rpm drop per shift -> the gear COUNT falls out of the spread
        // FIRST-GEAR PEAK FORCE vs the old flat force. Raised 1.5 -> 4.0 alongside the top-speed cap
        // (strawberry 2026-08-24: "rebalance gears and engine power to fit"), and it is the ACCELERATION knob
        // specifically: raising TopSpeedBuff alone moves the ceiling but not the pull, because _dragK is solved
        // so tractive force meets drag exactly AT _speedMax -- the car still gets there, just as slowly.
        //
        // The two knobs stay independent BY CONSTRUCTION: peakTorque scales with this, fTop scales with
        // peakTorque, and _dragK is solved from fTop, so top speed lands on _speedMax whatever this is.
        //
        // MEASURED against grip, because this is also what makes wheelspin reachable at all. Launch force is
        // engineForce * wheels * LaunchBoost * TorqueFrac(idle) = 0.55, against a limit of mu * m * g. At 1.5
        // the whole fleet sat at 3-58% of a mu=1.0 limit -- the jeep would not have broken traction ON ICE, so
        // any traction model layered on top was inert. At 4.0 the quad reaches ~156% and the golf ~134% (they
        // spin), the jeep 32% and the semi 10% (they do not). That split falls out of mass, not taste.
        const float LaunchBoost  = 4.0f;
        const float StallRpm     = 2600f;    // torque-converter stall: what the engine revs to against a stopped car
        const float RollingCrr   = 0.015f;   // rolling resistance, as a fraction of weight
        // RETAIL SPLITS THESE and we had collapsed them into one number. Jeep.dat carries BOTH
        // GearShift_Duration 0.2 (how long the shift itself takes) and GearShift_Interval 0.5 (the minimum gap
        // between shifts). One constant cannot be both, and conflating them is why the shift read as a flat
        // lockout rather than a gearchange.
        const float ShiftClutchTime = 0.20f;   // GearShift_Duration: drive is DISCONNECTED for this long
        const float ShiftTime       = 0.50f;   // GearShift_Interval: earliest the box will shift again
        const float EngineBrakeScale = 0.12f; // lift-off engine braking, as a fraction of the FOOT brake, AT REDLINE. Raised 0.03 -> 0.12 when FootBrakeScale went 6 -> 1.5, because engine braking is derived from it: the PRODUCT is what sets the coastdown, and it was signed off at 1.83 m/s2
        /// <summary>Steering RATE at the reference speed, as a fraction of the at-rest rate. This is the
        /// weight in "real simulated weight/inertia on steering" -- see the steer integration in
        /// _PhysicsProcess.</summary>
        const float SteerRateAtSpeed = 0.35f;
        const float SpeedBackstop = 1.15f;   // hard cut this far past the drag equilibrium (runaway guard only)
        public float EngineRpm => _engineRpm;
        public string GearLabel => LinearVelocity.LengthSquared() < 0.25f ? "N" : (LinearVelocity.Dot(-GlobalTransform.Basis.Z) < -0.5f ? "R" : $"G{_gear}");   // N stopped / R reversing / G<n>
        public float EngineRpmNorm => Mathf.Clamp((_engineRpm - IdleRpm) / (MaxRpm - IdleRpm), 0f, 1f);
        public int Gear => _gear;
        public int GearCount => _gears != null ? _gears.Length : 0;
        public float PeakTorque => _peakTorque;
        /// <summary>How much of the commanded drive force the tyres could NOT put down, 0..1. 0 = full grip;
        /// climbing toward 1 = the wheels are spinning. Read by the HUD/audio/effects and by L1.</summary>
        public float WheelSlip => _wheelSlip;
        float _wheelSlip;
        /// <summary>Tyre-to-ground friction coefficient. Physical values: ~1.0 dry tarmac, ~0.6-0.7 loose dirt,
        /// tracks bite harder than tyres. Per-vehicle so a truck on knobblies is not a roadster, which is the
        /// "per-vehicle traction" half of the ask; the per-GEAR half needs no data at all because the force
        /// through the current ratio already differs.</summary>
        float _tyreMu = 1.0f;
        bool _water0Boat;   // pure boat -> no wheels worth limiting
        public float WheelbaseForTest => _wheelbase;   // L1: needed to compute the Ackermann yaw a steer angle COMMANDS, so a probe can see oversteer
        // L1: how many wheels are actually touching the ground, and how many there are. A heavy multi-axle
        // hull that falls short of its drag equilibrium is usually not short of POWER -- it is airborne and
        // cannot put the newtons down. Without this the shortfall reads as a torque problem and sends you
        // to tune the wrong constant.
        public int WheelsOnGroundForTest { get { int n = 0; if (_wNodes != null) foreach (var w in _wNodes) if (w.IsInContact()) n++; return n; } }
        public int WheelCountForTest => _wNodes?.Length ?? 0;
        public bool Tracked;   // set from the spec at build; the drivetrain probe's fleet-wide guards skip tracked hulls (they hop legitimately on their short stiff suspension)
        public float SpecSpeedMaxForTest => _specSpeedMax;   // L1: the un-buffed spec top speed the OLD model capped at
        public float RedlineRpmForTest => RedlineFrac * MaxRpm;                                   // L1: the shift/limit point, so a probe doesn't re-derive it
        public float TorqueAtRpmForTest(float rpm) =>                                             // L1: sample the torque CURVE directly -- a flat-force model returns the same number at every rpm
            _peakTorque * TorqueFrac(Mathf.Clamp((rpm - IdleRpm) / (MaxRpm - IdleRpm), 0f, 1f)) * RevLimit(rpm);
        float CurrentGearRatio => (_gears != null && _gear >= 1 && _gear <= _gears.Length) ? _gears[_gear - 1] : 20f;

        /// <summary>Normalised engine torque at a normalised RPM. A real engine does NOT make a constant
        /// force: torque climbs off idle, peaks around 60% of the usable band and falls away toward the
        /// redline -- which is the entire reason a gearbox exists. Asymmetric on purpose, because the drop
        /// past peak is steeper than the climb to it, so short-shifting costs you a little and hanging on
        /// past peak costs you more. The old model had no curve at all: drive force was a flat constant at
        /// every speed in every gear, and the gear ratio never multiplied anything.</summary>
        static float TorqueFrac(float n)
        {
            n = Mathf.Clamp(n, 0f, 1f);
            float d = n - TorquePeakN;
            return Mathf.Max(TorqueFloor, 1f - (d < 0f ? TorqueFallLo : TorqueFallHi) * d * d);
        }

        /// <summary>Rev limiter, faded rather than cut. A hard cut to zero at the redline makes the engine
        /// hunt -- force dies, the car slows, rpm drops, force returns -- so it is ramped out over the last
        /// slice of the band instead.</summary>
        static float RevLimit(float rpm)
        {
            float red = RedlineFrac * MaxRpm;
            return rpm <= red ? 1f : Mathf.Max(0f, 1f - (rpm - red) / (MaxRpm - red));
        }

        /// <summary>The RPM at which gear g should drop to g-1. This is HYSTERESIS and it has to be computed
        /// from the ratios rather than picked by hand: a downshift multiplies rpm by ratio[g-2]/ratio[g-1],
        /// so a downshift point set too high lands the engine straight back above the UPSHIFT point and the
        /// box hunts between two gears forever. 0.85 is the margin that survives the round trip.</summary>
        float DownshiftRpm(int g)
        {
            if (_gears == null || g < 2 || g > _gears.Length) return 0f;
            return RedlineFrac * MaxRpm * (_gears[g - 1] / _gears[g - 2]) * 0.85f;
        }

        /// <summary>Tractive force PER TRACTION WHEEL, from the torque curve through the current gear.
        /// Godot's VehicleBody3D.EngineForce setter writes its value to EVERY traction wheel, so what this
        /// returns is one wheel's share of the drivetrain's total output. That distinction is not cosmetic:
        /// it is a factor of four on a car, and I had it backwards in the first model of this change, which
        /// made the whole drivetrain feel like there was no engine in it.</summary>
        float ThrottleForcePerWheel(float throttle)
        {
            if (_peakTorque <= 0f || _driveR <= 0f || _nTraction <= 0) return throttle * _engineForce;   // no drivetrain (trailer, unset spec): the old flat force stands
            float ratio = throttle < 0f ? _reverseGear : CurrentGearRatio;
            float total = _peakTorque * TorqueFrac(EngineRpmNorm) * RevLimit(_engineRpm) * ratio / _driveR;
            return throttle * total / _nTraction;
        }

        /// <summary>Derive this hull's drivetrain from its own spec, so 22 vehicles self-calibrate instead of
        /// being hand-tuned. Everything here is solved for, not chosen:
        ///
        ///   top gear   - the ratio that puts the engine at the redline exactly at the target top speed
        ///   spread     - how much ratio the vehicle needs, from its mass (a heavy hull needs more)
        ///   gear COUNT - falls out of the spread at a fixed rpm-drop per shift. The jeep gets 5 where it had
        ///                2, the semi 6 where it had 3. strawberry asked to change the number of gears; this
        ///                derives it rather than picking it.
        ///   ratios     - geometric between first and top, which is what a real gearbox is
        ///   peak torque- calibrated so first-gear peak force is LaunchBoost x the old flat force
        ///   drag       - solved so tractive force meets drag exactly AT the target top speed
        ///
        /// THE BUFF GOES INTO _speedMax ITSELF rather than being layered on top of it, and that is a
        /// correctness requirement, not a style choice: VehicleReplication caps a driving client's motion at
        /// SpeedMaxMps * EnvelopeSlack(1.25), so a car that really does 1.6x its reported SpeedMax would be
        /// rejected by the server's anti-cheat envelope on every tick. Steering fade, the tank's yaw fade and
        /// ForwardSpeedPct all read the same field and stay consistent for free.</summary>
        static void SetupDrivetrain(Vehicle v, Spec s)
        {
            v._nTraction = s.Kingpin == Vector3.Zero ? s.Wheels.Length : 0;   // a trailer's wheels are passive rollers
            if (s.Heli || s.Plane || v._nTraction <= 0 || s.Engine <= 0f || s.SpeedMax <= 0f || s.WheelRadius <= 0f) return;
            v._speedMax = s.SpeedMax * TopSpeedBuff;
            // NEGATIVE ALWAYS. Reverse speed is stored as a negative number and BOTH readers compare against
            // it directly -- the tracked path as `fwd <= _speedMin`, the wheeled path as `speed >= -_speedMin`.
            // A spec that wrote it POSITIVE therefore did not get a smaller reverse, it got NO reverse: the
            // wheeled test degenerates to `speed >= negative`, true at every speed including standing still, so
            // reverse force was zeroed on every tick. Three specs had the sign wrong (apc, ship, runabout) and
            // the APC is the one somebody drove -- strawberry, 2026-08-24, "apc has no reverse gear".
            // Normalising here rather than only fixing the data, because the failure is silent, total, and
            // looks like a missing feature rather than a typo. Zero stays zero: a trailer and a heli have no
            // reverse and must keep having none.
            v._speedMin = -Mathf.Abs(s.SpeedMin) * TopSpeedBuff;
            // EVERY CAR HAS BEEN DRIVING THROUGH A HIDDEN VELOCITY DAMP, and it is the single biggest force in
            // the old model after the engine. LinearDampMode defaults to COMBINE, which ADDS the body's value
            // to ProjectSettings physics/3d/default_linear_damp -- Godot's default 0.1, never overridden here.
            // So setting LinearDamp = 0 (which _PhysicsProcess does every tick) did NOT mean zero: it meant
            // 0.1 s^-1 on the whole body, forever. MEASURED off a full-throttle trace, resistance minus the
            // modelled drag came to 170, 168 and 171 N per m/s at three speeds -- dead flat, i.e. viscous, not
            // aerodynamic -- and m*0.1*v predicts 770/1346/1926 N against 769/1332/1940 measured. Within 1%.
            //
            // That is 1940 N on the jeep at 11 m/s, three quarters of the total resistance, and it is why the
            // first cut of this drivetrain LOST top speed: a real torque curve puts less force at the top of
            // the rev range than a flat constant did, and the flat constant had been quietly paying this tax.
            // The heli hit the identical trap and the note at the top of this file spells it out; nobody had
            // ever checked the car. Drag is now the explicit v^2 force below, so the implicit one goes.
            //
            // NOT A TRUE BOAT. A pure Boat keeps the combined damping -- its hull drag was tuned against it and
            // boat_hull / boat_turn_sweep hold it to tight tolerances. An AMPHIBIAN is a land vehicle that can
            // also swim, and leaving the hidden damp on it cost the APC a third of its top speed (12.7 against
            // a 19.2 target) while nothing in the suite asserts its behaviour in water.
            if (s.Water != WaterMode.Boat) { v.LinearDampMode = DampMode.Replace; v.LinearDamp = 0f; }
            // THE RADIUS THAT TURNS, not the one the spec happens to declare. Three specs give a
            // WheelRadius that their own WheelRadii then override -- the semi says 0.55 and fits six 0.65 s,
            // the tractor says 0.90 and fits two 0.90 s and two 1.05 s. Gearing is rpm-per-metre, so an 18%
            // radius error is an 18% gearing error: the semi hit its redline well short of top gear and
            // topped out at 9.5 m/s against a 22.4 target. Averaging the real radii fixes it at the source.
            float r = s.WheelRadius;
            if (s.WheelRadii != null && s.WheelRadii.Length > 0)
            {
                float rs = 0f; foreach (var rr in s.WheelRadii) rs += rr;
                r = rs / s.WheelRadii.Length;
            }
            v._driveR = r;
            float wheelRpmTop = v._speedMax / (2f * Mathf.Pi * r) * 60f;        // wheel rev/min at the target top speed
            float ratioTop = RedlineFrac * MaxRpm / wheelRpmTop;                 // ...geared to sit at the redline there
            // GEAR SPREAD, and it is the lever that keeps coastdown sane after a power raise.
            //
            // Launch force is engineForce * nTraction * LaunchBoost -- ratio1 CANCELS out of it, because
            // peakTorque is defined relative to ratio1 two lines below. Force at the TOP of the box is
            // proportional to LaunchBoost / spread, and _dragK is solved from that force. So raising
            // LaunchBoost to 4.0 quadrupled the drag needed to cap top speed, and vehicle.drivetrain caught
            // the consequence immediately: lift-off deceleration hit 5.36 m/s2, which is 0.55 g of "coasting"
            // -- a brake pedal, exactly what that check exists to refuse.
            //
            // Widening the spread cuts top-end force WITHOUT touching launch force, which is precisely what a
            // wide-ratio gearbox is for: launch torque without a bigger engine. Solved, not guessed --
            // dragK is proportional to LaunchBoost / (spread * speedMax^3), so holding it at its old value
            // through LaunchBoost 1.5 -> 4.0 and TopSpeedBuff 1.6 -> 2.0 needs spread * (4.0/1.5) * (1.6/2.0)^3
            // = 1.366x. The ceiling goes to 12 so the heaviest hulls are not clamped back into the problem.
            const float SpreadForPower = 1.366f;
            // CEILING RAISED 12 -> 18, because the clamp itself was the bug. Spread is what keeps top-end force
            // (and therefore the solved drag, and therefore coastdown) in proportion to launch force. The tank
            // wants 17.76 by the mass formula and was being clamped to 12, so it alone carried ~1.5x the drag
            // the formula intended -- and it was the only hull failing the coasting-is-not-a-brake check, at
            // 4.93 m/s2 against jeep 2.55, semi 2.02, apc 3.74. Nothing else is near the ceiling, so this moves
            // exactly one vehicle, which is the one that was wrong.
            // REVERTED from /3600 + ceiling 20. That was aimed at the tank's coastdown and derived from
            // fTop ~ LaunchBoost/spread -- predicted 4.39 -> 4.05 m/s2, measured 4.29, and it cost the SEMI its
            // top speed (21.2 against a 28.0 target). Three times tonight this drivetrain has moved less than
            // my algebra said it would, which is the signal to stop turning the knob rather than turn it again:
            // the model I am predicting with does not match the one that runs.
            //
            // Left where only the TANK misses, at 4.29-4.39 against a 4.0 limit, and reported as a known
            // deviation instead of chased at 1am. See the comment on the ceiling below.
            float spread = Mathf.Clamp((3f + v.Mass / 4000f) * SpreadForPower, 3f * SpreadForPower, 18f);
            // CEIL, and a cap of 10. GearStep is the largest rpm drop a shift may take, so the gear count has
            // to be the SMALLEST n whose step fits inside it -- rounding can land on an n whose actual step
            // (spread^(1/(n-1))) is WIDER than GearStep, and a cap of 8 forced exactly that on the heaviest
            // hulls once the spread was widened for the power raise.
            //
            // Measured: the tank came out at spread 12 over 8 gears = step 1.426 against a 1.35 design step,
            // which drops rpm far enough on each upshift that DownshiftRpm catches it, and vehicle.drivetrain
            // reported it as 2 downshifts on a steady pull. It was the ONLY vehicle over the step, and the only
            // one hunting. Ceiling makes the constant an actual bound rather than a target it can overshoot.
            int n = Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(spread) / Mathf.Log(GearStep)) + 1, 2, 11);
            float ratio1 = ratioTop * spread;
            var g = new float[n];
            for (int i = 0; i < n; i++) g[i] = ratio1 * Mathf.Pow(ratioTop / ratio1, i / (float)(n - 1));
            v._gears = g;
            v._reverseGear = ratio1 * 0.9f;
            v._peakTorque = v._engineForce * v._nTraction * LaunchBoost * r / ratio1;
            v._water0Boat = s.Water == WaterMode.Boat;
            // Tracks lay down a far larger contact patch than tyres, so they hook up where a wheel would spin.
            // Everything else takes the tyre default; a per-spec override is the obvious next dial if one
            // vehicle needs to feel different, but inventing 20 hand-picked numbers now would be taste, not data.
            v._tyreMu = s.Tracked ? 1.6f : 1.0f;
            float rpmTop = wheelRpmTop * ratioTop;
            float fTop = v._peakTorque * TorqueFrac((rpmTop - IdleRpm) / (MaxRpm - IdleRpm)) * RevLimit(rpmTop) * ratioTop / r;
            v._rollK = RollingCrr * v.Mass * 9.8f;
            v._dragK = Mathf.Max(0f, fTop - v._rollK) / (v._speedMax * v._speedMax);
        }
        // vehicle status for the HUD (source InteractableVehicle): fuel drains while the engine's on; health = damage; battery = accessories
        public float Fuel, FuelMax, Health, HealthMax, Battery;
        public float FuelBurn;   // fuel drained per second while driving (PZ-scale, per vehicle CLASS -- master); set from FuelClassOf at build
        public static bool InfiniteFuel = true;   // master 2026-07-20: cars DON'T burn fuel by DEFAULT (playtesting); the infFuel console command toggles it. SP-local static.
        public bool EngineOn; public string DisplayName; public Vector3 SeatOffset;   // per-vehicle driver-seat spot for the 3rd-person body
        /// <summary>A traversing weapon mount. Retail's own model (VehicleAsset.TurretInfo): a SEAT with a gun
        /// bolted to it, plus traverse limits -- not a separate system. Yaw and pitch are separate meshes baked
        /// at their own pivots (tools/extract_turret.py), because a single merged turret mesh cannot articulate:
        /// rotating it swings geometry about the vehicle's origin instead of the mount's.</summary>
        public sealed class TurretDef
        {
            public int Seat;                       // which seat operates it; Turret_1 -> seat 1 (the Hind's nose gunner)
            public string YawMesh, PitchMesh;      // ring + gun, each at its own pivot origin
            public Vector3 Pivot;                  // mount position, vehicle-local
            public Vector3 Muzzle;                 // where a shot leaves, relative to the PITCH frame
            public float YawMin = -180f, YawMax = 180f;
            public float PitchMin = -20f, PitchMax = 60f;
            // The mount carries its OWN gun and its OWN belt, which is retail's model (TurretInfo.itemID): a
            // turret is a gun item bolted to a seat, so it does not eat the gunner's rifle rounds.
            /// <summary>Where the CREW MEMBER stands, vehicle-local. For a door gun this is the DOORWAY, level
            /// with the mount, not tucked inboard: a gunner at 0.62 of the cabin half-width has a 0.42 m hit
            /// radius and therefore sits entirely inside the hull box, so every shot from abeam struck the
            /// fuselage first and the gunner could not be killed at all -- "the hitbox of the helis overlap the
            /// hitboxes of the gunners. so its impossible to hit them". Standing them in the door puts 0.27 m of
            /// body outside the skin, which is both the fix and what a door gunner actually does.</summary>
            public Vector3 GunnerAt = Vector3.Zero;
            /// <summary>Euler degrees applied to the gun MESH inside its pitch frame. A held-weapon model is not
            /// authored pointing down -Z: dragonfang_gun.txt measures 0.22 x 1.14 x 0.37, i.e. its length runs
            /// along Y, so dropped straight onto a mount it stands upright like a fence post. Measured, not
            /// assumed -- the AABB says which axis is the barrel.</summary>
            public Vector3 MeshRotationDeg = Vector3.Zero;
            public string GunId = "nykorev";
            public int Belt = 200;
            public Color Colour = new Color(0.16f, 0.17f, 0.14f);
        }
        public TurretDef[] Turrets = System.Array.Empty<TurretDef>();
        Node3D[] _turretYaw, _turretPitch;
        int[] _turretAmmo; float[] _turretCd;
        TargetDummy[] _turretCrew;
        readonly System.Collections.Generic.List<StandardMaterial3D> _navMats = new();
        readonly System.Collections.Generic.List<OmniLight3D> _navOmnis = new();
        /// <summary>Put crew in the door guns. NOT done at build time (strawberry: "helis spawned with the
        /// vehicle command shouldnt have gunners") -- a helicopter you spawn to fly is an empty airframe, and the
        /// gunners are something the AI brings with it.
        ///
        /// COLLISION EXCEPTION IS LOAD-BEARING, not tidiness. TargetDummy is a StaticBody3D on collision layer 1,
        /// which is the WORLD layer, and a vehicle's mask includes the world -- so parenting two of them inside
        /// the fuselage gave the aircraft two immovable world obstacles embedded in its own hull. It collided
        /// with its own crew every tick and departed on the yaw axis, which is exactly what strawberry saw:
        /// "orca and huey spin violently out of control yaw axis". The exception keeps them raycast-visible to
        /// bullets while making them invisible to the vehicle's own collision.</summary>
        public void EquipDoorGunners()
        {
            if (_turretCrew == null) return;
            for (int i = 0; i < Turrets.Length; i++)
            {
                if (Turrets[i].GunnerAt == Vector3.Zero || _turretCrew[i] != null) continue;
                var crew = new TargetDummy
                {
                    Name = $"Gunner{Turrets[i].Seat}",
                    Position = Turrets[i].GunnerAt,
                    MaxHealth = new PlayerVitalsSim().MaxHealth,   // "same hp as a player", taken FROM the player's sim
                    NeverRespawn = true,
                };
                AddChild(crew);
                AddCollisionExceptionWith(crew);
                _turretCrew[i] = crew;
                if (_turretPitch?[i] != null)
                    foreach (var g in _turretPitch[i].GetChildren())
                        if (g is MeshInstance3D gm) gm.Visible = true;   // the gun comes with the gunner
            }
        }
        /// <summary>Can this mount still shoot? A mount with no crew (a remote chin turret) is always manned; a
        /// door gun is manned only while its gunner is alive.</summary>
        public bool TurretCrewAlive(int seat)
        {
            if (_turretCrew == null) return true;
            for (int i = 0; i < Turrets.Length; i++)
            {
                if (Turrets[i].Seat != seat) continue;
                // A mount that declares NO gunner position is remote-operated and always manned (the Hind's chin
                // turret). A mount that DOES declare one needs a live body in it -- including the case where no
                // crew was ever installed, which is now every player-spawned airframe.
                if (Turrets[i].GunnerAt == Vector3.Zero) return true;
                return _turretCrew[i] != null && !_turretCrew[i].Down;
            }
            return false;
        }
        /// <summary>Test seam: drop this mount's gunner without shooting them five times.</summary>
        public bool DebugKillCrew(int seat)
        {
            if (_turretCrew == null) return false;
            for (int i = 0; i < Turrets.Length; i++)
                if (Turrets[i].Seat == seat && _turretCrew[i] != null) { _turretCrew[i].DebugKill(); return true; }
            return false;
        }

        public int TurretAmmo(int seat)
        {
            for (int i = 0; i < Turrets.Length; i++) if (Turrets[i].Seat == seat) return _turretAmmo?[i] ?? 0;
            return 0;
        }
        public bool HasTurret(int seat)
        {
            for (int i = 0; i < Turrets.Length; i++) if (Turrets[i].Seat == seat) return true;
            return false;
        }

        /// <summary>Try to fire the turret on `seat`. Returns false -- without consuming anything -- if that seat
        /// has no turret, the belt is empty, or it is still cycling. On success yields the world muzzle and the
        /// direction the BARREL points, which is not the direction the gunner is looking: the mount clamps, the
        /// view does not, and a shot that came out of the camera instead of the gun would quietly ignore the
        /// traverse limits that are the whole point of a chin turret.</summary>
        public bool TryTurretFire(int seat, out Vector3 origin, out Vector3 dir, out string gunId)
        {
            origin = Vector3.Zero; dir = Vector3.Forward; gunId = null;
            for (int i = 0; i < Turrets.Length; i++)
            {
                if (Turrets[i].Seat != seat) continue;
                if (_turretPitch?[i] == null || _turretAmmo == null) return false;
                if (_turretCd[i] > 0f || _turretAmmo[i] <= 0) return false;
                var t = Turrets[i];
                origin = _turretPitch[i].ToGlobal(t.Muzzle);
                dir = -_turretPitch[i].GlobalTransform.Basis.Z;   // barrel axis, not the look ray
                gunId = t.GunId;
                if (!InfiniteTurretBelt) _turretAmmo[i]--;   // an AI gunship is not a looting problem; see the field
                _turretCd[i] = TurretCycle;
                return true;
            }
            return false;
        }
        const float TurretCycle = 0.12f;   // belt-fed cadence; the gun's own Firerate governs the held-weapon path
        /// <summary>Never run this mount dry (strawberry: "does it have infinite ammo? it should"). Set by the AI
        /// on the aircraft it flies, NOT on the spec -- a player who takes the Hind's gunner seat still gets the
        /// finite 200-round belt, because the reason to give an NPC an endless one is that nobody can reload it,
        /// and that reason does not apply to a person sitting in the chair.</summary>
        public bool InfiniteTurretBelt;
        /// <summary>Test seam: are the navigation lights lit right now?</summary>
        public bool DebugNavLightsOn => _navMats.Count > 0 && _navMats[0].EmissionEnergyMultiplier > 0.01f;

        /// <summary>Aim the turret operated by `seat`, in degrees, clamped to its traverse limits. Returns false
        /// if that seat has no turret -- callers must not assume every seat is a gun position.</summary>
        public bool AimTurret(int seat, float yawDeg, float pitchDeg)
        {
            if (_turretYaw == null) return false;
            for (int i = 0; i < Turrets.Length; i++)
            {
                if (Turrets[i].Seat != seat) continue;
                var t = Turrets[i];
                float y = Mathf.Clamp(yawDeg, t.YawMin, t.YawMax);
                float p = Mathf.Clamp(pitchDeg, t.PitchMin, t.PitchMax);
                if (_turretYaw[i] != null) _turretYaw[i].Rotation = new Vector3(0f, Mathf.DegToRad(y), 0f);
                if (_turretPitch[i] != null) _turretPitch[i].Rotation = new Vector3(Mathf.DegToRad(p), 0f, 0f);
                return true;
            }
            return false;
        }

        /// <summary>World direction the BARREL points for `seat`, or the hull's forward if there is no such
        /// mount. Exposed so a test can measure where the gun ended up instead of trusting the maths that aimed
        /// it -- the aim derivation is a sign question and this file has a history with those.</summary>
        public Vector3 TurretBarrelDir(int seat)
        {
            if (_turretPitch != null)
                for (int i = 0; i < Turrets.Length; i++)
                    if (Turrets[i].Seat == seat && _turretPitch[i] != null)
                        return -_turretPitch[i].GlobalTransform.Basis.Z;
            return -GlobalTransform.Basis.Z;
        }

        /// <summary>World-space muzzle of the turret on `seat`, for spawning a shot where the barrel actually
        /// points rather than where the operator's head is.</summary>
        public Vector3? TurretMuzzle(int seat)
        {
            if (_turretPitch == null) return null;
            for (int i = 0; i < Turrets.Length; i++)
                if (Turrets[i].Seat == seat && _turretPitch[i] != null)
                    return _turretPitch[i].ToGlobal(Turrets[i].Muzzle);
            return null;
        }

        public int TurretCountBuilt => _turretYaw?.Length ?? 0;

        /// <summary>Every seat, local, index 0 = DRIVER. Never null and never empty: a vehicle with no extracted
        /// seat data still has one, at SeatOffset, so callers can index seat 0 unconditionally.</summary>
        public Vector3[] SeatLocals = { Vector3.Zero };
        public int SeatCount => SeatLocals.Length;
        /// <summary>Which seats are taken. Index-aligned with SeatLocals; entry 0 is the driver.</summary>
        public readonly System.Collections.Generic.HashSet<int> OccupiedSeats = new();
        public bool SeatFree(int i) => i >= 0 && i < SeatCount && !OccupiedSeats.Contains(i);
        /// <summary>Local seat position, clamped -- an out-of-range index returns the driver's rather than throwing
        /// mid-frame on a vehicle whose seat count shrank under a stale index.</summary>
        public Vector3 SeatLocal(int i) => SeatLocals[Mathf.Clamp(i, 0, SeatCount - 1)];

        // ACCESS ZONES: per-door, hood and trunk volumes you aim at, instead of one lookat for the whole car.
        //
        // strawberry: "kill the lookat for the whole car, change it for a collider on each 'door'... pressing f
        // gets you in at that seat. add volumes for the hood and trunk too". The car's overall hull is kept --
        // it still owns collision and damage; this is only about what the LOOK RAY resolves to.
        public enum AccessKind { Door, Hood, Trunk }
        public readonly record struct AccessZone(AccessKind Kind, int Seat, Vector3 Center, Vector3 Size);
        public AccessZone[] AccessZones = System.Array.Empty<AccessZone>();
        // What the focused player's look ray is currently pointing at on this hull, as a ready-made prompt line.
        // PlayerController owns the ray, so it writes this; the billboard below just draws it. Empty = no prompt.
        public string AccessHint = "";
        public Vector3 AccessBoxCenter;   // hull box centre the AccessZones above were derived from (vehicle-local)

        /// <summary>The trunk's contents, created on FIRST OPEN rather than at spawn -- a map holds hundreds of
        /// vehicles and an inventory grid each, allocated up front, is a cost paid for cars nobody ever opens.
        /// Parented to the vehicle so it rides along and dies with it.</summary>
        public StorageCrate Trunk;
        public bool HasTrunk { get { foreach (var z in AccessZones) if (z.Kind == AccessKind.Trunk) return true; return false; } }
        public bool HasHood { get { foreach (var z in AccessZones) if (z.Kind == AccessKind.Hood) return true; return false; } }

        public StorageCrate EnsureTrunk()
        {
            if (!HasTrunk) return null;
            if (Trunk != null && IsInstanceValid(Trunk)) return Trunk;
            Trunk = new VehicleTrunk { Width = TrunkWidth, Height = TrunkHeight };
            AddChild(Trunk);
            return Trunk;
        }
        const byte TrunkWidth = 6, TrunkHeight = 4;

        /// <summary>A StorageCrate with no crate. The base class builds a visible box mesh and culls it by
        /// distance; a car boot is already drawn by the car, so the visual is suppressed rather than parked
        /// inside the bodywork where it would z-fight the panel it is behind.</summary>
        public partial class VehicleTrunk : StorageCrate
        {
            protected override void BuildVisual() { }
        }

        /// <summary>Build the zones from the seat table and the hull box.
        ///
        /// DERIVED, not hand-listed. A door is its seat pushed outboard to the hull side, so the doors follow
        /// the prefab seats and a vehicle whose seats are right cannot have doors that are wrong.
        ///
        /// HOOD AND TRUNK ARE GEOMETRIC TESTS, which is how "some vehicles may not have trunks" (strawberry)
        /// answers itself instead of becoming a list I would have to keep in step: a vehicle has a hood if the
        /// hull extends far enough IN FRONT of its frontmost seat, and a trunk if it extends far enough BEHIND
        /// the rearmost one. That gives the sedan both, the bus a hood and no boot (its seats run to the back
        /// panel), and the quad neither -- without anyone deciding it per vehicle.
        ///
        /// Only wheeled land vehicles get either. A boat, a heli, a plane and a tank have neither in any sense
        /// worth aiming at.</summary>
        static AccessZone[] BuildAccessZones(Spec s, Vector3[] seats, Vector3 boxCenter, Vector3 boxSize)
        {
            var zones = new System.Collections.Generic.List<AccessZone>();
            float halfW = boxSize.X * 0.5f, halfL = boxSize.Z * 0.5f;

            for (int i = 0; i < seats.Length; i++)
            {
                var st = seats[i];
                // Outboard along the side the seat sits on; a centreline seat (quad, tractor) gets a zone
                // straddling it rather than being pushed to an arbitrary side.
                float side = Mathf.Abs(st.X) < 0.15f ? 0f : Mathf.Sign(st.X);
                float x = side == 0f ? st.X : Mathf.Sign(st.X) * Mathf.Max(Mathf.Abs(st.X), halfW * 0.82f);
                zones.Add(new AccessZone(AccessKind.Door, i,
                    new Vector3(x, st.Y + DoorZoneRise, st.Z),
                    new Vector3(side == 0f ? boxSize.X * 0.9f : DoorZoneWidth, DoorZoneHeight, DoorZoneLength)));
            }

            bool wheeled = !s.Heli && !s.Plane && !s.Tracked && s.Water != WaterMode.Boat && s.Kingpin == Vector3.Zero;
            if (wheeled && seats.Length > 0)
            {
                float front = boxCenter.Z - halfL, rear = boxCenter.Z + halfL;   // front is -Z in this port
                float frontSeat = float.MaxValue, rearSeat = float.MinValue;
                foreach (var st in seats) { frontSeat = Mathf.Min(frontSeat, st.Z); rearSeat = Mathf.Max(rearSeat, st.Z); }

                float hoodRun = frontSeat - front;
                if (hoodRun > MinCompartmentRun && !s.RearEngine)   // a rear-engined bus has no bonnet to open at the front
                    zones.Add(new AccessZone(AccessKind.Hood, -1,
                        new Vector3(boxCenter.X, boxCenter.Y + boxSize.Y * 0.25f, front + hoodRun * 0.5f),
                        new Vector3(boxSize.X * 0.85f, CompartmentHeight, hoodRun * 0.8f)));

                // The rear compartment is the BOOT -- or, rear-engined, the engine bay (the bus: master
                // "move the bus' engine to the back"). NoTrunk drops the boot outright (bus, tractor).
                float trunkRun = rear - rearSeat;
                if (trunkRun > MinCompartmentRun && (s.RearEngine || !s.NoTrunk))
                    zones.Add(new AccessZone(s.RearEngine ? AccessKind.Hood : AccessKind.Trunk, -1,
                        new Vector3(boxCenter.X, boxCenter.Y + boxSize.Y * 0.25f, rear - trunkRun * 0.5f),
                        new Vector3(boxSize.X * 0.85f, CompartmentHeight, trunkRun * 0.8f)));
            }
            return zones.ToArray();
        }
        const float DoorZoneWidth = 0.55f, DoorZoneHeight = 1.5f, DoorZoneLength = 1.15f, DoorZoneRise = 0.45f;
        /// <summary>How much hull has to stick out past the end seat before there is a compartment worth
        /// aiming at. Below this it is a bumper, not a boot.</summary>
        const float MinCompartmentRun = 0.85f;
        const float CompartmentHeight = 0.9f;

        /// <summary>Which access zone the player is aiming at, in the SAME oriented-box style as LookRayHitsHull.
        ///
        /// Two rules, in order. A zone the look SEGMENT actually passes through wins outright. Otherwise the
        /// zone nearest the AIM POINT wins, if it is within AccessNearReach of it -- and that second rule is
        /// load-bearing, not a nicety: focus is won by a fat sphere probe at the ray terminus, so the crosshair
        /// routinely sits on hull that no zone box contains (a wheel arch, the roof, the gap between two doors).
        /// A strict segment test alone answers "no zone" for most of the car and every one of those frames
        /// silently degrades to the driver's seat, which is the whole behaviour this replaced.
        ///
        /// Beyond that reach it genuinely returns false and the caller falls back to the hull, so a vehicle with
        /// no zones at all (boat, heli, tank, trailer) focuses exactly the way it always did.</summary>
        public bool ResolveAccess(Vector3 from, Vector3 to, out AccessZone hit)
        {
            hit = default;
            if (AccessZones.Length == 0) return false;
            var inv = GlobalTransform.AffineInverse();
            Vector3 lf = inv * from, lt = inv * to;
            float bestHit = float.MaxValue, bestNear = float.MaxValue;
            AccessZone near = default;
            bool found = false, anyNear = false;
            foreach (var z in AccessZones)
            {
                var aabb = new Aabb(z.Center - z.Size * 0.5f, z.Size);
                if (aabb.IntersectsSegment(lf, lt))
                {
                    float d = lf.DistanceSquaredTo(z.Center);   // ray through two zones -> the one nearer the eye
                    if (d < bestHit) { bestHit = d; hit = z; found = true; }
                    continue;
                }
                float nd = PointBoxDistSq(lt, aabb);
                if (nd < bestNear) { bestNear = nd; near = z; anyNear = true; }
            }
            if (found) return true;
            if (anyNear && bestNear <= AccessNearReach * AccessNearReach) { hit = near; return true; }
            return false;
        }
        /// <summary>How far off a zone the crosshair may sit and still count as aiming at it. Wide enough to
        /// cover the unclaimed hull between zones, short enough that the roof of a bus still means "no zone".</summary>
        const float AccessNearReach = 1.5f;
        static float PointBoxDistSq(Vector3 p, Aabb b)
        {
            Vector3 mn = b.Position, mx = b.End;
            float dx = Mathf.Max(Mathf.Max(mn.X - p.X, 0f), p.X - mx.X);
            float dy = Mathf.Max(Mathf.Max(mn.Y - p.Y, 0f), p.Y - mx.Y);
            float dz = Mathf.Max(Mathf.Max(mn.Z - p.Z, 0f), p.Z - mx.Z);
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>Where the 3rd-person BODY sits for a given seat.
        ///
        /// Seat 0 keeps SeatOffset, which is the prefab's Seat_0 plus a hand-tuned rise that puts the driver in
        /// the seat rather than through the floor. Passengers get their own extracted seat plus that SAME delta,
        /// so the tuning carries across instead of being re-guessed per seat -- and so a vehicle whose driver
        /// pose is already right cannot have its passengers sitting at a different height to him.</summary>
        public Vector3 SeatBodyLocal(int i) => i == 0 ? SeatOffset : SeatLocal(i) + (SeatOffset - SeatLocal(0));
        public string SpecKey = "jeep"; public int SpawnVariant;   // MP §3.6: which Spec built this + its paint variant -- VehicleNetSync replicates them so client puppets rebuild the same look
        public ushort NetDriverId;   // MP §3.6: remote player holding the driver seat (set by VehicleNetSync); 0 = none. Gates the local direct-path enter; never set in pure SP.
        public Vector3 DriverEyeLocal = new Vector3(-0.4f, 1.85f, 0.4f);   // FP driving eye (local); tall cabs override higher so the view clears the hood

        // --- MP Part A (CLIENT_PREDICTION_PLAN §5.2): the predicted-driver authority split. Both flags are
        // MP-only null-seam state -- never set in pure SP, so every gate below is inert there. ---
        // NetClientPredicted: THIS node is the driver's client-local vehicle (ClientWorldSession built it).
        // The server owns health/explosion (they arrive via the replica's Exploded flag), so local damage
        // must not eject/blow the driver on a divergence the server never saw.
        public bool NetClientPredicted;
        // NetHeld: THIS node is the server's body for a vehicle whose physics a driver's client owns
        // (retail updatePhysics kinematic, U3 InteractableVehicle.cs:1490-1519). VehicleNetSync freezes it
        // and teleports it to the adopted state every tick; _PhysicsProcess collapses to fuel burn + the
        // explosion timer (retail simulateBurnFuel runs server-side for driven cars too).
        public bool NetHeld { get; private set; }

        /// <summary>Server hold begin (VehicleNetSync, first adopted state): freeze STATIC -- the parked-car
        /// combo below; FreezeMode.Kinematic is known-bad on this Godot/Jolt build ("kinematic vanished the
        /// car", the settle freeze) -- zero velocities, flag the hold. Layer ghosting is NetGhost's job.</summary>
        public void NetBeginHold()
        {
            NetHeld = true;
            LinearVelocity = Vector3.Zero; AngularVelocity = Vector3.Zero;
            FreezeMode = FreezeModeEnum.Static;
            Freeze = true;
        }

        /// <summary>Per-tick teleport of the held body to the driver-adopted state. A frozen static body
        /// takes the transform verbatim (no solver fight -- wheels don't raycast while frozen), and space
        /// queries (server ballistics/occlusion/interaction) see it at the new pose immediately.</summary>
        public void NetHoldTeleport(Transform3D t) => GlobalTransform = t;

        /// <summary>Server hold end (exit/disconnect): physics authority returns to the server exactly as
        /// retail removePlayer -> updatePhysics. Seed the body from the last adopted velocity so it coasts
        /// on instead of stopping dead, and seed the settle low-pass (_velAvg) + crash detector (_prevSpeed)
        /// from the same -- stale zeros would insta-refreeze a rolling car / fake a crash on tick one.</summary>
        public void NetEndHold(Vector3 lin, Vector3 ang)
        {
            NetHeld = false;
            Freeze = false; Sleeping = false; _asleep = false; _parked = false;
            LinearVelocity = lin; AngularVelocity = ang;
            _velAvg = lin; _angAvg = ang;
            _prevSpeed = lin.Length();
        }

        /// <summary>Hold teardown when the vehicle EXPLODED while held: Explode() already unfroze + flung
        /// the body -- just drop the flag and keep whatever velocity the blast set.</summary>
        public void NetAbortHold() => NetHeld = false;

        /// <summary>Driven-vehicle layer ghost (VehicleNetSync, remote-enter side effect): swap body layer
        /// bit0 -> bit6 -- the SetTowGhost trick -- so the driver-client's own physics body (mask bit0) and
        /// its wheel raycasts never ride the server duplicate when both live in one tree (the L1 shared-tree
        /// harness), while players (mask bit0|bit6) still collide and server bullets (GodotWorldRay mask
        /// bit0|bit6) still occlude. Cost, accepted for v1: other VEHICLES (mask bit0) pass through a driven
        /// car -- retail's kinematic driven body does collide there; revisit with vehicle-vs-vehicle play.</summary>
        public void NetGhost(bool on)
        {
            uint wantLayer = on ? (_baseCollisionLayer & ~SolidBit) | (1u << 6) : _baseCollisionLayer;
            if (CollisionLayer != wantLayer) CollisionLayer = wantLayer;
        }

        // --- trailer hitch (master steer: back the cab under the trailer, hop out, walk to the hitch, F to couple; then
        // the trailer swings behind on the pin like a real rig). A PinJoint3D pins the cab's fifth-wheel to the trailer
        // kingpin -> a ball joint that lets the trailer articulate (yaw through turns) around the coupling point. ---
        public Vector3 FifthWheelLocal, KingpinLocal;   // local coupling points (cab plate / trailer kingpin); Zero = none
        public bool CanTow => FifthWheelLocal != Vector3.Zero;
        public bool IsTrailer => KingpinLocal != Vector3.Zero;
        public Vehicle CoupledTrailer, CoupledCab;       // partner when hitched (cab -> trailer, trailer -> cab)
        CollisionShape3D _landingGear;                   // trailer's front landing-leg support: enabled (down) when parked, disabled (retracted) while towed
        MeshInstance3D _landingLegMesh;                  // trailer's landing-leg VISUAL (split out of the body mesh) -> hidden while coupled so the legs vanish, shown when parked (mirrors _landingGear)
        PinJoint3D _hitch;                               // the coupling constraint (owned by the cab; freed on uncouple)
        readonly System.Collections.Generic.List<CollisionShape3D> _extraShapes = new();  // the Spec.ExtraBoxes hulls (cab: the low rear frame; trailer: headboard + gooseneck) -- kept SOLID; a towed trailer ghosts vs the cab by a layer swap, not by disabling shapes (would hole the player)
        uint _baseCollisionLayer;                        // the un-ghosted body layer (bit0|bit5); a towed/backing-under trailer swaps bit0->bit6 so the cab (mask bit0) phases it while the player (mask bit6) still collides
        uint _baseCollisionMask;                         // the un-ghosted body mask; a ghosted trailer also adds bit6 so a towing cab's separate sleeper hull (layer bit6) still blocks it
        StaticBody3D _sleeperHull;                        // tow-cab only: a copy of the roof hull on a SEPARATE body (layer bit6), so the sleeper blocks the coupled trailer even though the whole cab body is excepted from it (anti-clip)
        public const float CoupleReach = 1.6f;           // max fifth-wheel<->kingpin world gap to allow a couple (back it under)
        public const float ApproachReach = 6f;           // start phasing the cab through a trailer once its fifth wheel is this close to the kingpin (so you can back all the way under to CoupleReach)
        public const float HitchReach = 3.5f;            // on-foot: how close the PLAYER must stand to the kingpin to connect/disconnect (also gates the billboard prompt)
        const float JackknifeLimit = 90f;                // trailer yaw is clamped to +-this many degrees of the cab heading (no folding into the cab)
        const float RollDisconnectDeg = 50f;             // cab OR trailer tipped past this from upright -> drop the trailer
        float _ripTimer;                                 // cab: how long the trailer's velocity has diverged hard from ours (clipped something -> yank it off)

        // --- rope tow (strawberry 2026-07-19): a generic hemp rope from ANY vehicle's REAR tow node to ANY other
        // vehicle's FRONT tow node -- tied like a wire, held by a spring-tension pull (the rope only PULLS, never pushes),
        // and the tower drives a bit sluggish. Distinct from the semi fifth-wheel PinJoint hitch above: a SOFT link
        // between two independent cars, not a rigid articulated coupling. SP/integrated-server only (needs both bodies
        // in one physics space -- MP replication is a fast-follow). ---
        public Vector3 FrontTowLocal, RearTowLocal;   // bumper-height attach points (front / rear face centre), derived from the box in Build
        public Vehicle Towing;      // I am the tower -> the car roped behind me (my rear -> their front); null = not towing
        public Vehicle TowedBy;     // I am towed -> the car towing me; null = not towed
        TowRope _rope;              // the visual rope, owned by the TOWER, re-pointed each physics tick, freed on detach
        MeshInstance3D _towFrontNub, _towRearNub;   // small marker cubes shown while a rope tool is out (mirrors the wire-tool port arrows)
        float _towRestLen;          // this rope's natural length (set at attach = clamped current gap) -> slack below it, tension above
        public float TowRestLenValue => _towRestLen;   // A6: the live rope rest length (set at AttachTow) -> published by VehicleNetSync via ServerPublishTow so the client rope sags to the same length
        public const float TowRestMin = 2.0f;       // floor on the rope's rest length (a bumper-to-bumper tie still gets a 2m rope, slack)
        public const float TowAttachReach = 4.5f;   // max rear<->front world gap allowed when tying (walk the cars close first) -> also the rest-length CEILING, so the rope always forms at exactly the current gap and never yanks on attach
        public const float TowBreakLen = 7.5f;     // stretched past this -> the rope snaps (overload / one car driving off)
        const float TowStiffness = 20000f;         // spring: newtons per metre of stretch beyond rest (7000->20000: cars were WAYYY too weak to tow -- master 2026-07-20)
        const float TowDamping = 3200f;            // spring damper along the rope axis (kills bounce/oscillation; scaled up with the stiffer spring to stay stable)
        const float TowMaxForce = 30000f;          // clamp so a hard yank can't explode the ~900kg bodies at the physics rate (13000->30000: let a real haul actually pull)
        float _engineNoiseT;   // Phase 3 hearing: throttle the moving-car engine-noise emit
        const float BatteryMax = 10000f;   // battery full = 10000 (fuel burn is now per-class -> Vehicle.FuelBurn, set by FuelClassOf)
        public float FuelNorm => FuelMax > 0f ? Fuel / FuelMax : 0f;
        public float HealthNorm => HealthMax > 0f ? Health / HealthMax : 0f;
        public float BatteryNorm => Battery / BatteryMax;
        Node3D _headlights; bool _headlightsOn; StandardMaterial3D _headlightMat; Node3D _headlightFill;
        readonly System.Collections.Generic.List<Vector3> _autoSpot = new(), _autoTail = new();   // lamp emitter spots DERIVED from the lens meshes when the spec authors none (quad, bus)
        MeshInstance3D _headlightBeam;
        // Lamp tint, decided by the lens SHAPE. Round lamps read as older/halogen and go considerably warmer than
        // rectangular ones (strawberry). Derived from the hull the beam already computes -- a hexagonal outline IS
        // the round one on these low-poly meshes -- and fed to the emitter, the lens emission, the shaft and the
        // dust together, so the whole fixture agrees rather than three places each picking a cream.
        Color _lampTint = new(0.97f, 0.96f, 0.83f); bool _lampRound;
        public static float LampKelvinRound = 3000f;   // warm halogen
        public static float LampKelvinRect  = 4300f;   // cooler, whiter
        CpuParticles3D _headlightMotes; Color _hlMoteBase; float _hlMoteFade = 0f;   // dust in the beam -- night only, on the STREETLIGHT clock   // the visible shaft in front of the lamps (HeadlightBeam) -- ONE mesh for both, shown with the lights   // headlights ('L'): source "Headlights" node (2 spot + 1 omni) + emission + battery burn
        Node3D _taillights; bool _taillightsOn; StandardMaterial3D _taillightMat;   // running taillights: red glow while driven (source synchronizeTaillights = isDriven && canTurnOnLights)
        bool _braking;   // cab: is the brake being applied this frame (hand/foot) -> passed through to the trailer's brake lights while towing
        CpuParticles3D _exhaust; float _exhaustPuff;   // tailpipe smoke while running; a fat puff for a moment after the engine catches
        StandardMaterial3D _sirenMat0, _sirenMat1; OmniLight3D _sirenLight0, _sirenLight1; bool _sirenOn; float _sirenFlash;
        MeshInstance3D _sirenMi0, _sirenMi1, _sirenCentre;   // the two lenses + a hidden CENTRE hit-box between them (all three are shoot-out lamps: lightbar_l / lightbar_r / lightbar_c)
        public int LightbarPattern { get; private set; }   // 0 alternate L/R (retail wee-oo), 1 double-strobe both, 2 fast wig-wag -- ctrl-hold radial (strawberry 2026-09-04)
        public static readonly string[] LightbarPatternNames = { "wail", "double strobe", "wig-wag" };
        static readonly float[] LightbarSirenPitch = { 1.0f, 1.12f, 0.88f };   // the same siren.wav per pattern until master sources the three real ones
        /// <summary>ENGINE hp, separate from body hp (strawberry 2026-09-04 "split vehicle HP into body hp, and engine hp"). The engine only
        /// starts while this is above zero; a DROWNED engine (swamped in water) is zero for good -- no repair brings it back.</summary>
        public float EngineHealth, EngineHealthMax;
        public bool EngineDrowned { get; private set; }
        public bool EngineDead => EngineHealth <= 0f;
        float _carIgnitionLeft;   // seconds of ignition left before the drivetrain answers the throttle (strawberry 2026-09-04 "delay between starting the engine and the ability to start moving")
        public bool EngineStarting => _carIgnitionLeft > 0f;   // emergency lightbar (police/fire/ambulance): ctrl toggles; red + blue lenses alternate every 0.33s (source UpdateSirenVisuals) + cast real colored light from each side
        AudioStreamPlayer3D _hornAudio; float _hornCd;   // horn (LMB): one-shot the .dat HornAudioClip, 0.5s cooldown (source canUseHorn)
        /// <summary>Is anyone close enough to set an alarm off? Set by the side that owns the cars and knows
        /// where every player is (VehicleNetSync on a server); null falls back to the local camera, which is
        /// what singleplayer has always used.</summary>
        public static System.Func<Vector3, bool> AlarmProximityTest;
        public const float AlarmRadiusSq = 49f;   // ~7 m
        bool _alarmed; float _alarmTimer, _alarmBlip, _alarmCheckT = 0.3f; bool _alarmLit;   // "alarmed" car (5% of spawns): proximity (player) or damage sets off a ~30s honk+lights blip loop (master)
        AudioStreamPlayer3D _sirenAudio;   // looping siren clip while the emergency lightbar's on (master)
        Node3D _steerPivot; Vector3 _steerAxis;   // steering wheel model (source Objects/Steer): rotates by the steer angle around the disc normal
        const float BatteryBurnRate = 20f;   // source batteryBurnRate default (headlights drain while on, EBatteryMode.Burn)
        const float SirenBurnRate = 35f;     // the lightbar is a heavier draw than the headlamps: two flashing omnis + the siren loop
        const float BatteryChargeRate = 40f; // alternator. 40/s = a flat battery back to full in ~250s of running -- "somewhat slowly" (strawberry_cow 2026-08-24)
        const float BatteryStartMin = 400f;  // 4% -- below this the starter only clicks. Non-zero on purpose: a battery that dies at exactly 0 lets you crank it forever on the last drop
        // Bumper roadkill (source Bumper.OnTriggerEnter + VehicleAsset ParseFloat defaults): a moving vehicle damages a
        // character its front bumper touches. dmg = floor(baseDamage * speed); speed = clamp(fwdVel * mult, -10, 10),
        // ignored below the threshold. None of the stock vehicles override these in their .dat, so the defaults hold.
        // (enemy targeting removed with the zombie system: OnBumperHit's only wired branch was zombies -- see there.)
        const float BumperMult = 1f, BumperThreshold = 3f, BumperPlayerDmg = 10f, BumperSelfMult = 1f;
        // The zombie bumper constant (15f) went with main's removal of the zombie game layer, but the
        // ANIMAL roadkill path still needs a number. Same measured value, under a name that is now true.
        const float BumperAnimalDmg = 15f;
        const float CrashPropThreshold = 4f, CrashPropDmgPerSpeed = 18f, CrashPropMaxDmg = 500f;   // vehicle -> destructible prop: min impact speed to break, dmg per m/s, cap
        const float HornAlertRadius = 32f;   // source InteractableVehicle.tellHorn: AlertTool.alert(pos, 32) -> earshot-radius, unused (no listener wired up currently)
        public bool HeadlightsOn => _headlightsOn;
        public bool TaillightsOn => _taillightsOn;          // MP §3.6: replicated light/brake flags (read-only views of the SP state)
        public bool SirenOn => _sirenOn;
        /// <summary>L1 only: whether this car is an "alarmed" one. Spawn rolls it at 5%, so a test that needs
        /// the alarm has to set it rather than hope. Paired with AlarmActiveForTest to observe the loop.</summary>
        public bool AlarmedForTest { get => _alarmed; set => _alarmed = value; }
        public bool AlarmActiveForTest => _alarmTimer > 0f;
        public bool BrakingNow => _braking;
        public float SteerAngleDegrees => _steerAngle;
        public bool HasSteerWheel => _steerPivot != null;                           // a real steering-wheel model exists (its pivot = where 1P driving hands go)
        public Vector3 SteerPivotLocal => _steerPivot != null ? _steerPivot.Position : Vector3.Zero;
        public Vector3 SteerAxisLocal => _steerAxis;                                 // wheel disc normal, vehicle-local      // MP §3.6: the wheel-steer summary the snapshot carries
        public float SpeedMaxMps => _speedMax;              // MP Part A: the spec Speed_Max -- the server envelope's horizontal cap derives from it (spec-derived, never hardcoded)

        // look-at focus (master): same system as items -- a screen-space outline + an info billboard (name/HP/fuel/battery)
        bool _lookFocused; System.Collections.Generic.List<MeshInstance3D> _outlineMeshes; InfoBillboard _info;
        Color _outlineColor = new Color(0.82f, 0.83f, 0.90f);   // vehicle outline/label tint (no per-vehicle rarity in the port yet)
        const float InfoH = 1.1f;   // billboard sits INSIDE the car (cabin height), not floating above the roof (strawberry)

        // source's 2nd body BoxCollider (a slab at roof height, Godot space -- Z already negated) = the roof/frame
        // collision the port was missing (master). Jeep/Quad/Tractor are open-top -> null.
        static (Vector3 size, Vector3 center)? RoofBox(string name) => name switch
        {
            "Sedan" or "Police" => (new Vector3(2.5f, 0.254f, 2.320f), new Vector3(0f, 2.0f, 0.195f)),
            "Hatchback"         => (new Vector3(2.5f, 0.254f, 2.675f), new Vector3(0f, 2.0f, 0.723f)),
            "Humvee"            => (new Vector3(2.5f, 0.254f, 2.815f), new Vector3(0f, 2.0f, 0.050f)),
            "Roadster"          => (new Vector3(2.5f, 0.254f, 1.367f), new Vector3(0f, 2.0f, 0.672f)),
            "Bus"               => (new Vector3(3.0f, 0.512f, 7.834f), new Vector3(0f, 2.130f, 0.346f)),
            "Ambulance"         => (new Vector3(2.5f, 0.254f, 4.815f), new Vector3(0f, 2.0f, 0.087f)),
            "Firetruck"         => (new Vector3(2.5f, 0.262f, 6.803f), new Vector3(0f, 2.256f, 0.104f)),
            "Ural"              => (new Vector3(2.5f, 0.255f, 3.169f), new Vector3(0f, 2.257f, 1.570f)),
            "Semi Truck"        => (new Vector3(2.5f, 2.34f, 3.95f), new Vector3(0f, 2.67f, -0.605f)),   // tall cab+sleeper, tightened to the mesh (X±1.25, Y 1.5..3.84, Z -2.58..1.37): was ±1.59 wide + poking forward over the hood + stopping short of the sleeper back; the rest of the length stays the low frame box so a trailer interlocks over the rear
            _ => null,
        };

        public enum WaterMode { Car, Boat, Amphibious }   // source VehicleAsset.engine: CAR = land; BOAT = floats + water-drive; amphibious (e.g. APC) = CAR wheels + buoyancy so it swims too

        /// <summary>Which procedural helicopter airframe to build. Two exist because VoX wanted the Rust-accurate
        /// machine and strawberry liked the first one ("its like the gta vice city RC heli"), and keeping both
        /// costs one Spec -- the flight model, controls and net path are identical either way.
        ///   Ultralight = the real Rust minicopter: an open tube frame, exposed seats, wheels, a bare mast.
        ///   Pod        = the enclosed little scout with skids and a canopy.</summary>
        public enum HeliFrame { Ultralight, Pod }

        struct Spec
        {
            public string Body, Wheel, WheelTex, Palette, GlassMesh, MissileMesh, SteerMesh;   // Palette = paintable palette; WheelTex = wheel albedo; GlassMesh = translucent canopy overlay (jet)
            public Color? GlassTint;   // GlassMesh albedo+alpha; null = the jet's golden canopy. Cars use GlassPane.DefaultHue so vehicle glass matches the building editor's windows.
            public bool RetractGear;   // JET: wheels tuck up into the fuselage when airborne (retract pivots + struts)
            public WaterMode Water;   // Car (default) = land only; Boat = floats+water-drives (no useful wheels); Amphibious = land wheels + float/water-drive when its hull is in the sea
            public Vector3[] Buoys;   // hull buoyancy points (local space, Godot); null = auto 4 bottom corners of BoxSize. Boats/amphibious float via a spring at each toward SeaLevelY
            public float BuoyLift;    // added to the auto buoyancy-voxel Y. NEGATIVE = float HIGHER (voxels sit lower -> the hull rides up -> more of the coloured bottom shows above the waterline). 0 = default
            public float BuoyDamp;    // multiplier on the buoyancy VELOCITY damping (source Buoyancy.cs 0.1). >1 = settles faster / bobs less (a big hull is underdamped otherwise). 0 = default (1x)
            public float TurnScale;   // multiplier on the rudder torque. 0 = default (1x). NOT cosmetic: the rudder torque is MASS-scaled (BoatTurn * Mass) but a hull's yaw INERTIA scales with its LENGTH SQUARED, so the same constant that spins a 9 m runabout at 58 deg/s moves a 66 m ship at 0.74 -- 360 degrees in eight minutes (strawberry: "almost impossible to turn"). A long hull has to buy the difference back explicitly.
            public float BuoyReserve; // multiplier on the hull's DISPLACEMENT. 0 = default (1x). Reserve buoyancy: the
                                      // volume a real hull carries ABOVE its waterline, which is what lets it take cargo
                                      // without foundering. Ours had none -- displacement is derived from Mass/HullDensity
                                      // and every vehicle in this game masses the same GlobalMass 900, so full submersion
                                      // generated exactly 2x the ship's own weight and ONE 900 kg vehicle on deck matched
                                      // it. Measured: the hull sank 10.2 m in 10 s and was still going, never finding a new
                                      // equilibrium (vehicle.ship_deck_probe). Raising this and re-tuning BuoyLift keeps the
                                      // draft where it was while giving the hull headroom to carry things.
            public (Vector3 center, float height, float yawDeg)[] Ladders;   // climbable ladders bolted to this
                                      // vessel. `center` is local, `height` the vertical span, `yawDeg` which way the
                                      // climbable FACE points (0 = aft, +Z). Built as a solid box like every other
                                      // ladder in the world -- the open-rung trimesh is what made the map's ladders
                                      // unclimbable, see WorldBuilder.PlaceObject.
            public bool SteadyHull;   // hold this hull STILL: heavy extra heave damping, for a vessel meant to be
                                      // stood and built on. strawberry 2026-08-19: "the idea is that the ship is
                                      // eventually a spot where you can build a base, if its constantly wobbling,
                                      // its hard to build on."
                                      //
                                      // Implemented as DAMPING, and not as the obvious thing. The obvious thing is
                                      // to switch off the per-voxel wave ripple that is visibly driving the bob --
                                      // I tried it and the hull got WORSE, 0.259 m/s to 0.763. The ripple is doing
                                      // a second job nobody wrote down: it gives each voxel a slightly different
                                      // sea surface, so the submerged/not-submerged threshold is crossed by a few
                                      // voxels at a time instead of all of them at once. It is DITHER, and without
                                      // it the quantised buoyancy is a staircase the hull chatters up and down.
                                      // So the ripple stays and the residual motion is damped instead.
            public (Vector3 min, Vector3 max)? HullTrimesh;   // region given the model's ACTUAL geometry, on a STATIC
                                      // child body that rides along. Godot forbids a concave trimesh on a body that
                                      // moves -- it is static-only, and cow tools watched one drop a crane through
                                      // the floor the moment it moved. But a deckhouse does not need to be part of
                                      // the hull's rigid body at all: it is scenery you walk on and into, it never
                                      // has to push the ship. As a static child it may carry the exact mesh,
                                      // interior walls and all, which no convex decomposition can reproduce.
                                      // strawberry: "its pretty complex collision, with interior walls. i dont
                                      // think casting rays is the solution here. why cant we just use the model's
                                      // colliders?"
            public (Vector3 min, Vector3 max)? HullDecompose;   // region handed to Godot's own convex DECOMPOSITION
                                      // (VHACD) instead of to hand-cut bands. Bands are fine for a shape that is
                                      // convex slice by slice -- a hull is. A deckhouse is not: it is stepped,
                                      // overhanging and hollow, and four bands over it measured 630 sample points
                                      // SOLID WHERE THE MODEL IS AIR, which is 99.5% of the whole ship's invisible
                                      // wall (vehicle.ship_hull_1to1's volume pass). strawberry: "the entire
                                      // superstructure is messed up." Voxelising it instead would need 164-521
                                      // boxes; VHACD gets it in a handful of hulls.
            public (Vector3 min, Vector3 max)[] HullBands;   // 1:1 COLLISION. Each entry is an AABB filter in MESH
                                      // space; every body-mesh vertex inside it becomes one CONVEX hull shape. Set this
                                      // and the single BoxShape3D hull below is REPLACED. Godot cannot give a MOVING
                                      // body a concave trimesh collider at all (they are static-only), so "matches the
                                      // model" means a convex DECOMPOSITION -- bands are how the model gets cut into
                                      // pieces each of which genuinely is convex.
            public (Vector3 size, Vector3 center, float yawDeg)[] HullBoxes;   // extra box shapes alongside HullBands,
                                      // for the parts a convex hull cannot express -- a RING (a deck bulwark) is the
                                      // case: any single convex hull spanning it fills the deck in flush with the top
                                      // of the rail. yawDeg lets a rail follow a hull that TAPERS instead of running
                                      // straight past the end of the deck and out over open water.
            public Vector3 DeckVolume, DeckCenter;   // MOVING DECK. Non-zero = the local-space box inside which things
                                      // RIDE this vessel: anything resting in here is carried with the hull as it moves
                                      // and turns. Without it a deck is scenery -- measured, a helicopter parked on the
                                      // ship was 106 m astern ten seconds after it got under way (vehicle.ship_deck_probe),
                                      // because contact friction between two rigid bodies transfers essentially nothing
                                      // at this mass ratio. 0 = not a carrier (every other vehicle).
            public int BuoySlices;    // voxels PER AXIS for the buoyancy grid. 0 = source default (2 -> 2x2x2 = 8). A big hull needs more: at 2 slices a 20x11x66 ship gets ONE voxel per 10x5.5x33 m block, so its whole waterplane is 2 points across the beam and the vertical resolution is coarser than the draft -- measured as a 2.5 m dead band with zero heave AND zero roll stiffness (see vehicle.boat_hull_probe)
            public string[] DefaultPaints;   // source .dat DefaultPaintColors (random on spawn); null + !RandomHueGray = unpainted white
            public bool RandomHueGray;       // source RandomHueOrGrayscale mode (quad/sedan/hatchback)
            public float WheelRadius, Engine, SteerMax, SteerMin, SpeedMax, SpeedMin, Brake;
            /// <summary>Kerb mass in kg. 0 = fall back to GlobalMass, so a spec that has not been given a
            /// number behaves exactly as before. Retail ports one Rigidbody mass (2.0) onto every vehicle, which
            /// is why they all massed 900 -- faithful, but it means a loaded semi carries the momentum of a
            /// hatchback, and momentum is what ramming, braking distance and hill-climbing are made of
            /// (strawberry 2026-08-22: "yes do per vehicle weight").
            ///
            /// Buoyancy does NOT need re-deriving for this: the buoyant force is rho_water * g * (Mass /
            /// HullDensity) * reserve = 2 * Mass * g * reserve against a weight of Mass * g, so mass CANCELS and
            /// draft is set by HullDensity and BuoyReserve alone. Checked before changing anything, because the
            /// standing assumption -- mine and cow tools' both -- was that it would sink the ship.</summary>
            public float Mass;
            public float[] WheelRadii;   // optional per-wheel radius (tractor: small front, big rear); null = uniform WheelRadius
            public Vector3 BoxSize, BoxCenter;   // source BoxCollider (Godot space: center Z negated)
            public float[] ForwardGears;   // .dat ForwardGearRatios (engine RPM = wheelRPM * ratio)
            public float ReverseGear, ShiftUpRpm;   // .dat ReverseGearRatio + GearShift_UpThresholdRPM
            public string Sound;   // engine loop ogg basename (source: the prefab's AudioSource m_audioClip)
            public string IgnitionSound;   // one-shot start-up clip (helicopters: the rotor spin-up)
            public float IdlePitch, MaxPitch, IdleVolume, MaxVolume;   // .dat EngineSound (EngineRPMSimple)
            public float Fuel, Health;   // .dat Fuel / Health capacities (HUD gauges)
            public EItemRarity Rarity;   // .dat Rarity (default COMMON) -> look-at outline colour (master)
            public string Name;   // display name (English.dat) for the HUD title
            public Vector3[] SpotPos; public Vector3 OmniPos;   // headlight spot beams + omni fill (prefab "Headlights", Godot space); null = no lights yet
            public bool NoTrunk, RearEngine;
            public Vector3 DoorZoneMin, DoorZoneMax;   // BI-FOLD DOOR (bus): the body triangles inside this box become the door; split at DoorSplitZ into two leaves
            public float DoorHingeX, DoorHingeZ, DoorSplitZ, DoorFoldDeg;   // hinge A on the panel's INNER face at the front jamb; (fold 0 = 90 degrees)
            public float DoorHingeBX;   // hinge B (mullion) X: the panel's OUTER face (+ a hair), so leaf B folds back beside leaf A instead of through it   // hinge A = front jamb (X = panel mid-thickness), hinge B = the split; fold angle of leaf A (B folds back twice that)
            public string DoorGlassA, DoorGlassB;   // glass pane labels that ride leaf A / leaf B
            public Vector3 DoorFloorCutMin, DoorFloorCutMax; public float DoorPocketY;   // cabin floor box cut out where the leaves swing, replaced by a pocket floor at DoorPocketY (the first step's level) + risers
            public Vector3 DoorRiserCutMin, DoorRiserCutMax;   // the old 2nd-step riser between the first step and the cabin floor: gone, the pocket continues the step
            public float DoorTrimZ0, DoorTrimZ1, DoorTrimY;   // the panel is modelled wider than the doorway (its ends hide inside the jambs) and deeper than the step: trim it to the opening + the step top, cap the cuts   // NoTrunk: no boot access zone at all (bus, tractor); RearEngine: the engine bay is the REAR compartment (bus) -> rear hood zone, no front one (master)
            public Vector3[] TailPos;   // taillight spot positions (prefab "Taillights", rear, Godot space); null = emission-only
            public Vector3[] TaillightMesh;   // red taillight/brake LAMP boxes (rear) -> red running glow while driven, flare on brake; captured as _taillightMat. null = none
            public string Horn;   // .dat HornAudioClip ogg (one-shot on LMB)
            public Vector3 SteerPivot, SteerAxis;   // steering wheel model pivot (centroid) + rotation axis (disc normal); Zero = don't rotate
            public Vector3 DriverEye;   // FP driving eye offset (local); Zero = the shared default (-0.4,1.85,0.4). Tall cabs (semi) sit HIGHER so you see over the hood
            public Vector3[] Seats;     // every seat, local, index 0 = DRIVER. Null = single-seat (SeatOf's driver spot only).
            public TurretDef[] Turrets; // traversing weapon mounts, by seat. Null = none.
            public string SeatModelFile, SteerModel;   // REAL ripped interior models re-centred into the cab (props whose body mesh has no interior sub-objects, e.g. semi). SteerModel turns via SteerPivot/SteerAxis
            public Vector3 SeatModel;   // world-target for the seat model's AABB centre (the mesh is baked at its source vehicle -> translated here)
            public (float x, float y, float z, bool steer)[] Wheels;
            public (string txt, Color color)[] Parts;   // detail meshes (root-relative) with their real solid colours
            public Vector3 FifthWheel;   // tow vehicle: local fifth-wheel coupling point (behind the cab); Zero = can't tow
            public Vector3 Kingpin;      // trailer: local kingpin point (front); Zero = not a trailer
            public Vector3 LandingGearSize, LandingGearCenter;   // trailer: front landing-leg support box (holds the nose up when parked); toggled OFF while coupled. Zero size = none
            public Vector3 LandingLegZoneMin, LandingLegZoneMax;  // trailer: mesh-space AABB enclosing the landing-leg triangles -> split them into a toggleable MeshInstance so they VANISH when coupled. Min==Max = no split
            public Vector3 HeadlightZoneMin, HeadlightZoneMax;    // AABB enclosing the baked-in headlight LENS triangles -> split them into their own mesh with an emissive material so the REAL lenses glow on 'L' (semi). Min==Max = no split
            public Vector3 TaillightZoneMin, TaillightZoneMax;    // LEFT AABB (right = X-mirror) enclosing the baked-in RED taillight triangles -> split into an emissive _taillightMat mesh so the REAL baked lights glow (trailer). Min==Max = no split
            public float LandingLegScaleY, LandingLegPivotY;      // trailer: vertically STRETCH the split-out leg mesh (scale about PivotY) so the feet reach the ground at the nose-up parked height. ScaleY 0/1 = no stretch
            public (Vector3 size, Vector3 center)[] ExtraBoxes;   // extra fixed collision boxes beyond the main box + RoofBox (e.g. the trailer's kingpin/gooseneck, the cab's low rear fifth-wheel deck) -> match the model geometry
            // ---- rotary wing. Heli=true swaps the wheel/engine drive for rotor thrust along the body UP axis,
            // which is what makes tilting the airframe the way you translate -- the Rust minicopter feel.
            public bool Heli;
            public float HeliThrust;                             // peak rotor acceleration, m/s^2 (must exceed g to climb)
            public float HeliPitchTorque, HeliRollTorque, HeliYawTorque;   // control authority, rad/s^2
            public float HeliLevel;                              // self-levelling strength (0 = none, fully manual)
            public float HeliClimbMax, HeliFallMax;              // MP envelope caps, m/s (0 = inherit the car defaults)
            public float RotorRadius, TailRotorRadius;           // blade half-spans (the rotor mesh is scaled to these)
            public bool SlingHook;                               // carries a winch + electromagnet under the belly (sky-crane duty)
            public float SlingCable;                             // deployed cable length, metres (0 = use the default)
            public Vector3 SlingAnchor;                          // LOCAL point the winch hangs from (mesh frame, so it must be offset by nothing at use)
            public Vector3 RotorHub, TailRotorHub;               // local mount points for the two rotors
            /// <summary>Take the BELLY beacon's lens from ANOTHER airframe's taillight mesh. Null = use this
            /// airframe's own, which is the right answer for six of the seven. The Hind's own lens is the odd one
            /// out: measured across the fleet it is the only non-square lamp and the only fat one -- 0.338 x 0.288
            /// footprint against a uniform 0.368 square elsewhere, and 0.256 THICK against 0.161 -- so on the
            /// biggest airframe in the game it reads as a small lump stuck to the belly rather than a fitting set
            /// into it (strawberry: "the belly light of the hind is weird and small? doesnt fit the bottom").
            /// The nav lights are untouched; this is the belly fitting only.</summary>
            public string BeaconLensFrom;
            public float MainRotorHp, TailRotorHp;                // independent rotor health (0 = derive from Health)
            public Vector3 MainHubBox, TailHubBox;                // the BULLET hitbox at each mast (full size); Zero = a default off the rotor radius
            public string[] HeliBodyMeshes;                      // airframe .obj(s); null = build one of the procedural frames below
            public HeliFrame Frame;                              // which procedural airframe (ignored when HeliBodyMeshes is set)
            public string HeliRotorMeshPrefix;                   // content prefix for <p>_rotor_{main,tail}_{blades,disc}.txt; null = the Huey's
            // ---- FIXED WING (plane, EEngine.PLANE). Thrust along body FORWARD; lift RAMPS with forward airspeed and
            // pushes along body UP, so banking spills lift and CARVES the turn -- bank-to-turn / realistic (master).
            // Control authority only when AIRBORNE (speed-gate takeoff). Floats on its pontoons via the boat buoyancy
            // for a WATER takeoff. Reuses the heli rotor spool/spin/blur for the propeller (spun about body forward).
            public bool Plane;
            public float PlaneThrust;              // forward acceleration at full throttle, m/s^2
            public float PlaneLift;                // up-accel along body-UP at PlaneTargetSpeed (>= g to climb), m/s^2
            public float PlaneTargetSpeed;         // forward speed at which lift is full (~ Speed_Max)
            public float PlanePitchTorque, PlaneRollTorque, PlaneYawTorque;   // control rates, rad/s
            public float PlaneSteerFade;           // fraction of control authority KEPT at top speed (source: steer fades with speed); 1 = no fade
            public Vector3 PropHub;                // propeller pivot (local, Godot space)
            public string PropMeshPrefix;          // <prefix>_prop.txt (blades) + <prefix>_prop_disc.txt (spin-blur)
            public Vector3[] BurnerPos;            // JET afterburner exhaust points (rear engines) -> flame FX shooting aft, scaled by throttle
            public Vector3[] ContrailPos;          // JET wingtip trailing edges -> vapour contrails streaming aft, faded in by airspeed
            // ---- TRACKED ARMOUR (tank). Tracks + a rotating turret/elevating gun instead of steered wheels. The
            // road wheels still do the physics; the treads are a visual overlay and the turret is a vehicle weapon
            // aimed by tinyclaw's system via the exposed pivots. Differential steering (Drive branches on Tracked):
            // the steer input drives the two tracks at different speeds instead of a wheel angle.
            public bool Tracked;
            public string Treads;                    // palette-painted tread band, root-relative overlay on the hull
            public string[] TurretMeshes;            // palette-painted turret meshes, baked centred on the yaw pivot
            public Vector3 TurretYawPivot;           // turret rotates about local Y here (root space)
            public string GunMesh;                   // palette-painted cannon, baked centred on the pitch pivot
            public Vector3 GunPitchPivot;            // gun elevates about local X here (root space)
            public Vector3 Muzzle;                   // cannon muzzle (root space) -> shell spawn for the weapon system
        }

        static AudioStreamWav LoadWav(string resPath)   // load a PCM wav at runtime (no ffmpeg on the box) as a looping stream for the siren
        {
            byte[] b = System.IO.File.ReadAllBytes(ProjectSettings.GlobalizePath(resPath));
            int channels = System.BitConverter.ToInt16(b, 22), rate = System.BitConverter.ToInt32(b, 24), bits = System.BitConverter.ToInt16(b, 34);
            int dataSize = System.BitConverter.ToInt32(b, 40); byte[] pcm = new byte[dataSize]; System.Array.Copy(b, 44, pcm, 0, dataSize);
            return new AudioStreamWav { Data = pcm, Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = channels == 2,
                                        LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopEnd = dataSize / (channels * bits / 8) };
        }
        /// <summary>Per-airframe pitch multiplier: a big helicopter sounds LOW, a small one sounds high.
        /// Applied to the engine loop AND the start-up clip, since a Skycrane whose idle is a low thud but
        /// whose ignition is Huey-pitched just sounds like two different aircraft.
        ///
        /// Sized off the AIRFRAME's box volume, not the rotor. Rotor radius was the obvious choice and it is
        /// nearly useless here: the hind, orca and skycrane are all 5.90 m, so the three heaviest machines came
        /// out within 3 % of each other. The collision box actually separates them -- 60.9 m^3 for the
        /// Skycrane, 19.3 for the Hummingbird, 1.3 for the minicopter -- and its cube root is a real
        /// characteristic LENGTH, which is the quantity a resonating structure's frequency scales inversely
        /// with. Square-rooted to tame the extremes (the minicopter is 45x smaller than the Skycrane by volume,
        /// which raw would put it two octaves up) and referenced to the Huey, the aircraft the clips came from.
        ///
        /// Result across the fleet: minicopter/scoutcopter 1.50, hummingbird 1.05, huey 1.00, orca 0.93,
        /// hind 0.89, skycrane 0.87. (strawberry: "big hind low pitch, mini higher pitched")</summary>
        static float HeliSizePitch(Spec s)
        {
            if (!s.Heli) return 1f;
            float vol = s.BoxSize.X * s.BoxSize.Y * s.BoxSize.Z;
            if (vol < 0.01f) return 1f;
            const float HueyLength = 2.970f;   // cbrt(2.40 * 2.10 * 5.20)
            return Mathf.Clamp(Mathf.Sqrt(HueyLength / Mathf.Pow(vol, 1f / 3f)), 0.78f, 1.50f);
        }

        static StandardMaterial3D SolidMat(Color c) =>
            new() { AlbedoColor = c, Metallic = 0f, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

        /// <summary>A lens that actually emits, rather than a surface painted the colour of light.</summary>
        static StandardMaterial3D LensMat(Color c, float energy) => new()
        {
            AlbedoColor = c, Metallic = 0f, Roughness = 0.4f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = energy,
        };

        /// <summary>AIRCRAFT NAVIGATION LIGHTS: red to port, green to starboard, and both STEADY.
        ///
        /// The convention is not decoration -- it is how another pilot reads which way you are pointing in the
        /// dark, so the side lights never blink. The thing that flashes on a real helicopter is a separate red
        /// anti-collision beacon, which is built as its own node (see _beacon).
        ///
        /// EVERY airframe in this fleet ships only ONE lens, so the pair has to be made: huey, hind, skycrane
        /// and hummingbird carry a lens on the port side, and the ORCA's is on starboard -- yet all five were
        /// painted the same flat red, which means the orca's light was on the wrong side of the aircraft and
        /// its green was simply missing. Rather than trust the filename, the side is taken from the mesh's own
        /// X centroid and the opposite lens is mirrored from it: colour follows GEOMETRY, so an airframe whose
        /// lens sits to starboard gets green there and red on the mirrored side, automatically.</summary>
        void BuildNavLights(string txt)
        {
            var mesh = ContentProvider.ParseObj($"res://content/{txt}");
            if (mesh == null) return;
            float cx = mesh.GetAabb().GetCenter().X;
            if (Mathf.IsZeroApprox(cx)) cx = -1f;   // dead centre: treat the original as port so the pair is still built
            // The mirrored copy is a -1 X scale. Winding flips with it, which is why LensMat leaves culling off.
            for (int i = 0; i < 2; i++)
            {
                bool mirrored = i == 1;
                bool isPort = (cx < 0f) != mirrored;   // the ORIGINAL sits on the side its centroid says; the copy is the other one
                var col = isPort ? new Color(0.95f, 0.05f, 0.05f) : new Color(0.05f, 0.95f, 0.15f);
                var navMat = LensMat(col, 2.6f);
                _navMats.Add(navMat);
                AddChild(new MeshInstance3D
                {
                    Name = isPort ? "NavLightPort" : "NavLightStarboard",
                    Mesh = mesh, MaterialOverride = navMat,
                    Scale = mirrored ? new Vector3(-1f, 1f, 1f) : Vector3.One,
                });
                // A small omni so the lens tints the airframe around it at night instead of being a flat
                // bright dot. Short range on purpose: a nav light marks the aircraft, it does not light terrain.
                var navOmni = new OmniLight3D
                {
                    Position = new Vector3(mirrored ? -cx : cx, mesh.GetAabb().GetCenter().Y, mesh.GetAabb().GetCenter().Z),
                    OmniRange = 2.2f, LightColor = col, LightEnergy = 1.4f,
                };
                _navOmnis.Add(navOmni);
                AddChild(navOmni);
            }
        }

        // billboarded smoke/fire burst using the REAL source particle texture (veh_smoke_0/veh_smoke_1/veh_fire,
        // ripped from the vehicle prefab's ParticleSystemRenderer). smoke = grey rising; fire = additive orange.
        public static CpuParticles3D MakeSmoke(string texName, Color c, float life, float vel, int amount, bool fire, float sizeMin, float sizeMax)
        {
            var mat = new StandardMaterial3D
            {
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true,
                AlbedoColor = new Color(1f, 1f, 1f, fire ? 0.95f : 0.7f),
                BlendMode = fire ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
            };
            string tp = ProjectSettings.GlobalizePath($"res://content/{texName}");
            // GenerateMipmaps: a runtime Image.LoadFromFile texture has NO mipmaps, so the default Linear-mipmap filter
            // samples BLACK once the sprite MINIFIES (small/dense particles) -> the "stationary black smoke cluster" at
            // the engine (same root cause as the old guns-render-black bug). Mips make minified particles sample grey.
            if (System.IO.File.Exists(tp)) { var tex = ContentProvider.TextureCached(tp, mipmaps: true); if (tex != null) { mat.AlbedoTexture = tex; } }
            if (fire)   // veh_fire.png is a 4-frame flipbook (64x16 = 4x16^2) -> animate the frames, don't stretch all 4 onto one quad (master)
            {
                mat.EmissionEnabled = true; mat.Emission = new Color(1f, 0.4f, 0.05f); mat.EmissionEnergyMultiplier = 2.5f;
                mat.ParticlesAnimHFrames = 4; mat.ParticlesAnimVFrames = 1; mat.ParticlesAnimLoop = true;
            }
            var ps = new CpuParticles3D { 
                Emitting = false, Amount = ParticleFx.Amount(amount), Lifetime = life, Direction = Vector3.Up, Spread = 25f,
                InitialVelocityMin = vel * 0.6f, InitialVelocityMax = vel, Gravity = new Vector3(0f, 1.5f, 0f),
                ScaleAmountMin = sizeMin * ParticleFx.SizeScale, ScaleAmountMax = sizeMax * ParticleFx.SizeScale, Color = c, Mesh = new QuadMesh { Size = Vector2.One, Material = mat },   // Size 1 -> ScaleAmount = the particle diameter in metres (src startSize)
            };
            if (fire) { ps.AnimOffsetMax = 1f; ps.AnimSpeedMin = 5f; ps.AnimSpeedMax = 9f; }   // random start frame + flicker through the 4
            else { ps.AngleMin = -180f; ps.AngleMax = 180f; ps.AngularVelocityMin = -35f; ps.AngularVelocityMax = 35f; }   // SMOKE (not fire): random per-puff rotation + slow tumble (master)
            return ps;
        }

        // Ground material under a wheel (raycast down from the wheel to read the collider's "surf" tag). Drives the
        // per-wheel dust tint + gate. Untagged ground defaults to grass (PEI terrain).
        PlayerController.Surf WheelSurf(VehicleWheel3D w)
        {
            var from = w.GlobalPosition;
            var to = from + Vector3.Down * (w.WheelRadius + w.SuspensionTravel + 0.4f);
            var q = PhysicsRayQueryParameters3D.Create(from, to, 1u << 0);
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count > 0 && hit["collider"].AsGodotObject() is Node n && n.HasMeta(PlayerController.SurfMeta))
                return (PlayerController.Surf)(int)n.GetMeta(PlayerController.SurfMeta);
            return PlayerController.Surf.Grass;
        }

        // source Bumper.OnTriggerEnter: the front bumper roadkills a character it drives into. Damage scales with impact
        // speed (clamped at 10) x the base BumperZombieDamage; the vehicle takes a little self-damage per hit too.
        public bool Parked => _parked;   // exposed for the net tests: "exit parked the car" is now the assertion, since exit no longer touches the engine
        public void Wake() { Freeze = false; Sleeping = false; _asleep = false; _parked = false; }   // resume dynamic physics (rammed or re-driven)
        // vehicle crash -> authoritative destructible break, through the SAME seam the heli rotors already use
        // (Vehicle.NetDamageObject, declared once above -- main had added it for rotors while this branch was adding
        // it for crashes, and the merge brought in a second copy of the field). Null in --direct SP / on an MP
        // puppet -> a crash just bumps the prop, no break.
        void OnVehicleContact(Node body)
        {
            if (body is Vehicle v && v != this && !v._husk) { v.Wake(); return; }   // ram a frozen parked car -> wake it (a dead husk stays put)
            if (_exploded || _parked) return;
            if (body is Node dn && dn.HasMeta(DestructibleField.MetaKey))   // crashed into a destructible prop -> speed-scaled break (server-owned health, same seam as the bullet path)
            {
                float speed = LinearVelocity.Length();
                if (speed < CrashPropThreshold) return;                        // a gentle nudge doesn't break it
                NetDamageObject?.Invoke((int)dn.GetMeta(DestructibleField.MetaKey), Mathf.Clamp(speed * CrashPropDmgPerSpeed, 0f, CrashPropMaxDmg));
                TakeDamage(3f * BumperSelfMult);                               // the car takes a little crash damage too (source takeCrashDamage)
            }
        }
        public bool HasSiren => _sirenMat0 != null;   // only emergency vehicles (police/fire/ambulance) have a lightbar
        public void ToggleSiren() { if (HasSiren && (Battery > 0f || _sirenOn)) _sirenOn = !_sirenOn; }
        /// <summary>Ctrl-hold radial: pick a flash pattern (turns the lightbar on), or -1 = lightbar off.</summary>
        public void SetLightbar(int pattern)
        {
            if (!HasSiren) return;
            if (pattern < 0) { _sirenOn = false; return; }
            LightbarPattern = Mathf.Clamp(pattern, 0, LightbarPatternNames.Length - 1);
            if (Battery > 0f) _sirenOn = true;
        }
        public bool IsLightbarLensBroken(int side) { int i = _lampLabels.IndexOf(side == 0 ? "lightbar_l" : "lightbar_r"); return i >= 0 && _lampBroken[i]; }
        public bool IsLightbarCentreBroken { get { int i = _lampLabels.IndexOf("lightbar_c"); return i >= 0 && _lampBroken[i]; } }   // master: ctrl toggles the siren/lightbar while driving. A flat battery can't power it -- but you can always switch it OFF

        /// <summary>Can the starter turn it over? A flat battery clicks and nothing happens.
        ///
        /// The threshold is above zero deliberately: at exactly 0 the player can keep cranking on the last drop
        /// forever, which reads as the starter being broken rather than the battery being flat.</summary>
        public bool CanStartEngine => !OnFire && Battery >= BatteryStartMin && EngineHealth > 0f;   // + a live engine (drowned/shot-out engines never start)

        /// <summary>Start it if the battery can. Returns whether it caught.
        ///
        /// Here rather than at the call sites because there are already two of them (driver enters, passenger
        /// moves to the driver seat) and a third would silently skip the rule -- the gate belongs with the
        /// battery it reads, not with each person who asks.</summary>
        public bool TryStartEngine()
        {
            if (EngineOn || !CanStartEngine) return false;
            EngineOn = true;
            // Ground vehicles fire the ignition one-shot here. Aircraft do NOT: StepHeli/StepPlane drive
            // _ignitionAudio off the rotor spin-up, where the clip's LENGTH is the spin-up gate, and firing it
            // from here as well would play it twice and desync that gate from the sound it is derived from.
            if (!_heli && _ignitionAudio != null && !_ignitionAudio.Playing) _ignitionAudio.Play();
            if (!_heli) _carIgnitionLeft = _ignitionAudio?.Stream != null && _ignitionAudio.Stream.GetLength() > 0.3 ? Mathf.Min(2.5f, (float)_ignitionAudio.Stream.GetLength()) : 1.2f;   // the drivetrain answers only after the crank (strawberry 2026-09-04)
            _exhaustPuff = 1.0f;   // cold start: a thick puff (the exhaust block reads it)
            return true;
        }

        /// <summary>Kill the engine. Separate from TryStartEngine so the caller says which it means -- a single
        /// Toggle() at the call site turns a mis-read state into the opposite action.</summary>
        public void StopEngine() => EngineOn = false;

        /// <summary>Driver's ignition switch. Returns the state it ended in.
        ///
        /// The engine is NOT tied to occupancy any more (strawberry_cow 2026-08-24): a car you get into is off
        /// until you start it, and stays running when you get out. So this is the only thing that starts or
        /// stops one, and every caller has to be a driver -- the seat check lives at the call site because only
        /// the caller knows who is asking.</summary>
        public bool ToggleEngine()
        {
            if (EngineOn) StopEngine(); else TryStartEngine();
            return EngineOn;
        }

        // IMPACT DAMAGE MASS SCALE (strawberry_cow 2026-08-24: "speed + vehicle weight based").
        //
        // Normalised against the JEEP so the existing, tuned roadkill numbers stay exactly where they were: the
        // jeep scales by 1.0 and everything else is relative to it. Picking an absolute kg->damage constant
        // instead would have silently re-tuned every zombie hit that is already correct.
        //
        // sqrt, not linear. Momentum is linear in mass, but a 40 t semi is ~44x the jeep and a linear term makes
        // it one-shot a building from walking pace, which is not a difficulty knob so much as a different game.
        // sqrt(44) ~ 6.6x is heavy enough that a truck plainly outweighs a hatchback without the number running
        // away. Capped anyway, because the cap is what stops a future 200 t vehicle from being a bug report.
        const float ImpactRefMass = 900f;      // the jeep, the vehicle every existing bumper constant was tuned on
        const float ImpactMassCapX = 4f;       // heaviest thing hits 4x the jeep, not 6.6x
        const float ImpactVehicleDmg = 9f;     // per (m/s * massScale) into another vehicle
        const float ImpactPropDmg = 22f;       // into a deployable/prop -- higher, because props are meant to lose to a car
        const float ImpactSelfProp = 0.35f;    // share of the dealt damage the CAR takes back off a prop

        /// <summary>How much heavier-than-a-jeep this vehicle hits. See ImpactRefMass for why it is sqrt+capped.</summary>
        float ImpactMassScale => Mathf.Min(ImpactMassCapX, Mathf.Sqrt(Mathf.Max(1f, Mass) / ImpactRefMass));

        /// <summary>Closing speed against a thing we hit, in m/s, along our forward axis.
        ///
        /// RELATIVE, not absolute: two cars going the same way at 30 m/s that touch have barely collided, and
        /// using our own speed there would total both of them. A static prop has zero velocity, so this reduces
        /// to our own speed for everything that cannot move.</summary>
        float ClosingSpeed(Node3D other)
        {
            // Live velocities on BOTH sides, deliberately -- _recentTopSpeed is an absolute own-speed peak and
            // has no relative equivalent, so substituting it here would overstate two cars travelling together.
            // The honest cost: a very fast car-on-car hit that ContinuousCd resolves over several ticks reads
            // low, the same way aircraft impacts did before the peak existed. Under-reading is the safe
            // direction for a damage number; over-reading would total cars that merely brushed.
            var theirs = other is RigidBody3D rb ? rb.LinearVelocity : Vector3.Zero;
            return (LinearVelocity - theirs).Dot(-GlobalTransform.Basis.Z);
        }

        void OnBumperHit(Node3D body)
        {
            if (_exploded || _parked) return;

            // Peak, not instantaneous: BodyEntered can arrive a tick or two after ContinuousCd started
            // resolving the contact, by which point LinearVelocity has already been bled down and the hit reads
            // as a gentle nudge. Sign comes from the live velocity, magnitude from the peak.
            float fwdNow = LinearVelocity.Dot(-GlobalTransform.Basis.Z);
            float fwd = Mathf.Sign(fwdNow) * Mathf.Max(Mathf.Abs(fwdNow), _recentTopSpeed);
            float massScale = ImpactMassScale;

            // ZOMBIE branch dropped in the 2026-08-29 merge: main deleted game/ZombieController.cs
            // with the zombie game layer. The player / animal / vehicle-vs-vehicle roadkill below is
            // independent of it and is kept. Restore this branch with the class if zombies come back.

            // PLAYERS and ANIMALS. The old comment here said these were unwired because no such target shared a
            // scene with a vehicle yet -- that stopped being true, and source Bumper hits both.
            if (body is PlayerController p)
            {
                float speed = Mathf.Clamp(fwd * BumperMult, -10f, 10f);
                if (speed < BumperThreshold) return;
                p.TakeDamage(Mathf.Floor(BumperPlayerDmg * speed * massScale), GlobalPosition);
                TakeDamage(2f * BumperSelfMult);
                return;
            }
            if (body is AnimalAgent a)
            {
                float speed = Mathf.Clamp(fwd * BumperMult, -10f, 10f);
                if (speed < BumperThreshold) return;
                a.DamageHit(Mathf.Floor(BumperAnimalDmg * speed * massScale), a.GlobalPosition, -GlobalTransform.Basis.Z);
                TakeDamage(2f * BumperSelfMult);
                return;
            }

            // VEHICLE vs VEHICLE. Damage BOTH, each by the OTHER's mass -- a jeep hitting a parked semi should
            // come off worse than the semi does. Only the mover applies it (the closing-speed gate below is
            // signed), so a symmetric head-on still resolves once per car rather than four times.
            if (body is Vehicle other && !other._exploded)
            {
                float closing = ClosingSpeed(other);
                if (closing < BumperThreshold) return;
                float ours = ImpactVehicleDmg * closing * massScale;
                float theirs = ImpactVehicleDmg * closing * other.ImpactMassScale;
                other.TakeDamage(Mathf.Floor(ours));    // we hit them with OUR weight
                TakeDamage(Mathf.Floor(theirs));        // they resist with THEIRS
                GD.Print($"[RAM] {DisplayName} -> {other.DisplayName} closing={closing:0.0} dealt={ours:0} taken={theirs:0}");
                return;
            }

            // DESTRUCTIBLES. Deployables and glass both expose TakeDamage; walk up from the collider because the
            // body that reports the contact is usually a child StaticBody of the thing that owns the health.
            for (Node n = body; n != null; n = n.GetParent())
            {
                float closing = ClosingSpeed(body);
                if (closing < BumperThreshold) break;
                float dmg = Mathf.Floor(ImpactPropDmg * closing * massScale);
                if (n is Deployable dep) { dep.TakeDamage(dmg); TakeDamage(dmg * ImpactSelfProp); return; }
                if (n is GlassPane gp)   { gp.TakeDamage(dmg);  TakeDamage(dmg * ImpactSelfProp * 0.25f); return; }   // glass barely scratches the car
            }
        }

        /// <summary>Where this vehicle was last shot FROM, in world space, and when. Recorded for every vehicle
        /// because it costs two fields; acted on only by an NPC Hind (strawberry: "dont wire up the other helis
        /// for attack behavior"). It is the SHOOTER's position, not the impact point -- the point of it is "where
        /// were you standing", which is what an aircraft would turn toward, and the two differ by the whole length
        /// of the bullet's flight.</summary>
        public Vector3 LastAttackedFrom { get; private set; }
        public double LastAttackedAtMsec { get; private set; } = -1e9;
        public bool HasBeenAttacked => LastAttackedAtMsec > -1e8;
        public void NoteAttackedFrom(Vector3 shooterWorldPos)
        {
            LastAttackedFrom = shooterWorldPos;
            LastAttackedAtMsec = Godot.Time.GetTicksMsec();
        }

        /// <summary>Bullets hitting the front third of the hull (the engine bay) go to the ENGINE hp instead of the body.</summary>
        bool _rearEngine;   // Spec.RearEngine: the engine bay is the back third (bus), not the front
        public bool IsEngineBay(Vector3 world) => _hullSizeLocal.Z > 0f && (_rearEngine ? ToLocal(world).Z > AccessBoxCenter.Z + 0.15f * _hullSizeLocal.Z
                                                                                  : ToLocal(world).Z < AccessBoxCenter.Z - 0.15f * _hullSizeLocal.Z);   // forward = -Z
        Vector3 _hullSizeLocal;
        public void TakeEngineDamage(float amount)
        {
            if (NetClientPredicted || _exploded || amount <= 0f || EngineHealth <= 0f) return;
            EngineHealth = Mathf.Max(0f, EngineHealth - amount);
            TriggerAlarm();
            if (EngineHealth <= 0f)
            {
                EngineOn = false;   // dies where it stands; no restart until repaired (never, if drowned)
                if (_smoke0 != null) _smoke0.Emitting = true;
            }
        }
        public void TakeDamage(float amount, float engineShare = 0.5f)   // source askDamage; engineShare = the fraction ALSO taken off the engine (explosions/crashes 0.5; a body-panel bullet 0): reduce health; at 0 the EXPLODE timer starts
        {
            if (NetClientPredicted) return;   // MP Part A: the driver's client-local vehicle -- health/explosion are SERVER truth (replica Exploded flag); a local crash must not eject the driver on damage the server never applied
            if (_exploded || amount <= 0f) return;
            Health = Mathf.Max(0f, Health - amount);
            if (engineShare > 0f) TakeEngineDamage(amount * engineShare);
            TriggerAlarm();   // damaging an alarmed car sets off its alarm (master)
            if (Health <= 0f && _deadTimer < 0f)
            {
                _deadTimer = ExplodeDelay;
                EngineOn = false;   // engine dies AT 0 HP: cuts engine POWER (Drive gates on EngineOn) + the engine SOUND (audio goes silent when !EngineOn). Velocity is untouched -> the car keeps its momentum and coasts to a stop (master)
                // a SMALL fire starts the moment it hits 0 HP (master), before Explode() (4s later) ramps it to the full blaze
                if (_smoke != null) _smoke.Emitting = true;
                if (_smoke0 != null) _smoke0.Emitting = true;
                if (_fire != null) _fire.Emitting = true;
                if (_fireLight != null) { _fireLight.Visible = true; _fireLight.LightEnergy = 1.2f; }   // dim glow now; Explode() takes it to 3
            }
        }

        void Explode()   // source explode: launch up + spin, fire on, char the body, disable
        {
            if (CoupledTrailer != null || CoupledCab != null) Uncouple();   // a blown-up cab or trailer drops its partner so the wreck doesn't fling the whole rig (strawberry)
            if (Towing != null || TowedBy != null) DetachTow();   // a wrecked car also drops its rope tow (both ends) so the wreck doesn't drag or get dragged
            _exploded = true;
            Freeze = false; Sleeping = false; _asleep = false;   // un-hold the parked car (frozen or asleep) so the wreck flies + tumbles
            foreach (var w in _wNodes) { w.SuspensionStiffness = 0.5f; w.SuspensionMaxForce = 0f; }   // KILL the suspension -> the hulk collapses flush onto its body instead of perching on ghost-wheels (master "kill it completely")
            ApplyCentralImpulse(Vector3.Up * 18000f);         // source min/maxExplosionForce straight up; boosted for a dramatic chassis fling against the 3x gravity (master: much higher)
            ApplyTorqueImpulse(new Vector3(2800f, 0f, 0f));   // source AddTorque(16,0,0)
            EngineOn = false;
            SetHeadlights(false); SetTaillights(false);   // a corpse's lamps go dark -- kill the head + tail lights (master)
            // ...and SHOT OUT, not just switched off (strawberry 2026-09-03: "destroy headlights, tail lights, through the
            // shooting-them-out path when a car explodes") -- the same BreakLamp the bullet path uses, so the wreck's
            // lenses carry the smashed look and nothing can re-light them.
            for (int li = 0; li < _lampNodes.Count; li++) BreakLamp(li);
            // ...but killing the lamps here is NOT enough on its own, and that was the bug: an alarmed car's
            // blip loop below re-lights them every 0.5s and honks, on a burning wreck. Worse, once the hulk
            // settles it becomes a _husk and the per-frame sim early-returns for good -- so whatever the blip
            // left behind is frozen there permanently, lamps ON if it stopped in the lit half of the cycle.
            // A corpse is dead: end the alarm outright rather than relying on it to expire.
            _alarmed = false; _alarmTimer = 0f; _alarmBlip = 0f; _alarmLit = false;
            _sirenOn = false;   // the lightbar block already gates on !_exploded, but clear the state too so nothing can re-arm it
            if (_sirenAudio != null && _sirenAudio.Playing) _sirenAudio.Stop();
            if (_engineAudio != null) _engineAudio.VolumeDb = -80f;   // EngineOn=false silences it next tick; do it now in case the wreck husks first
            if (_fire != null) _fire.Emitting = true;
            if (_fireLight != null) { _fireLight.Visible = true; _fireLight.LightEnergy = 3f; }
            _burnTime = 0f;   // start the fire lifecycle (dies down at 40s, out at 60s, despawns 5 min later)
            _explosionAudio?.Play();
            if (_bodyMesh != null) _bodyMesh.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.05f, 0.05f), Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };   // charred wreck
            SpawnWheelDebris();
            ExplodeDamage();
        }

        // source InteractableVehicle explode: DamageTool.explode(pos, radius 8, playerDmg 200, zombieDmg 200, vehicleDmg 500).
        // The 500 vehicle damage easily blows a neighbouring car too -> a staggered chain reaction.
        void ExplodeDamage()
        {
            const float R = 8f;
            Vector3 p = GlobalPosition;
            PlayerRegistry.FlinchAllFromExplosion(p, 32f, 45f);   // big vehicle blast -> strong camera shake, every player, distance-gated (src Bomb_0-like: radius 32 / mag 45)
            // (enemy damage removed with the zombie system)
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && v != this && !v.Exploded)
                {
                    float d = v.GlobalPosition.DistanceTo(p);
                    if (d <= R) v.TakeDamage(SDG.Unturned.ExplosionMath.Linear(500f, d, R));   // chain: 500 easily blows the next car too
                }
            foreach (var n in GetTree().GetNodesInGroup("deployables"))
                if (n is Deployable dep && !dep.IsWreck)
                {
                    float d = dep.GlobalPosition.DistanceTo(p);
                    if (d <= R) dep.TakeDamage(SDG.Unturned.ExplosionMath.Linear(500f, d, R));   // a car blast wrecks a nearby generator too
                }
            foreach (var n in GetTree().GetNodesInGroup("players"))
                if (n is PlayerController pl)
                {
                    float d = pl.GlobalPosition.DistanceTo(p);
                    if (d <= R) pl.TakeDamage(SDG.Unturned.ExplosionMath.Linear(200f, d, R));
                }
        }

        void SpawnWheelDebris()   // source canExplode: the wheels fly off when the vehicle blows up
        {
            if (_wNodes == null || _wheelMeshRef == null) return;
            Node scene = GetTree()?.CurrentScene ?? GetParent();
            if (scene == null) return;
            var rng = new RandomNumberGenerator(); rng.Randomize();
            for (int i = 0; i < _wNodes.Length; i++)
            {
                var pos = _wMeshes[i].GlobalPosition;
                var mat = (StandardMaterial3D)_wheelMatRef.Duplicate();   // per-debris material so the 10s fade doesn't touch the car's own wheels
                var rb = new WheelDebris { Mass = 18f, Mat = mat, CollisionLayer = 1u << 2, CollisionMask = 1u << 0 };   // debris on its own bit, masks GROUND only -> lands + rolls but never collides with the player (bit3 masks 0/6, not 2) (strawberry)
                rb.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = _wheelR } });
                rb.AddChild(new MeshInstance3D { Mesh = _wheelMeshRef, MaterialOverride = mat, Scale = _wMeshes[i].Scale });
                scene.AddChild(rb);
                rb.GlobalPosition = pos;
                var outward = pos - GlobalPosition; outward.Y = 0f;
                outward = outward.LengthSquared() > 0.01f ? outward.Normalized() : Vector3.Right;
                rb.ApplyCentralImpulse(outward * 45f + Vector3.Up * 55f + new Vector3(rng.RandfRange(-15f, 15f), 0f, rng.RandfRange(-15f, 15f)));
                rb.AngularVelocity = new Vector3(rng.RandfRange(-12f, 12f), rng.RandfRange(-12f, 12f), rng.RandfRange(-12f, 12f));
                _wMeshes[i].Visible = false;   // hide the wheel still on the car
            }
        }

        // Unturned paintable shading: body samples the palette, paintable texels tinted by _PaintColor.
        static ShaderMaterial PaintMat(string palette, Color paint)
        {
            var sh = ContentProvider.ShaderCached(ProjectSettings.GlobalizePath("res://content/vehicle_paint.gdshader"));   // ONE compiled shader for the fleet (was a new Shader per vehicle)
            var m = new ShaderMaterial { Shader = sh };
            m.SetShaderParameter("palette", ContentProvider.TextureCached(ProjectSettings.GlobalizePath($"res://content/{palette}")));   // decoded once per palette
            var lin = paint.SrgbToLinear();   // ALBEDO is linear; the palette texels already come through source_color (sRGB->linear), but the raw paint Vector3 did not -> #437c44 rendered as a washed-out light green. Convert so it shows true deep forest (master: "our render is diff")
            m.SetShaderParameter("paint_color", new Vector3(lin.R, lin.G, lin.B));
            return m;
        }

        // Source paint on spawn (VehicleAsset.getDefaultPaintColor): unpainted -> white; LIST mode -> a random pick from
        // the .dat's DefaultPaintColors; RandomHueOrGrayscale -> the REAL HSV roll (10% grayscale, else random hue with
        // saturation 0.15-0.7 + value 0.15-0.9, from Sedan.dat's RandomPaintColorConfiguration). Seeded by the spawn's
        // deterministic variant so each instance keeps a stable colour. (Was a hand-picked "curated" set -- not src-accurate.)
        static Color SpawnPaint(Spec s, int variant)
        {
            if (s.RandomHueGray)
            {
                var rng = new System.Random(unchecked(variant * 486187739 + 1150833019));
                float R() => (float)rng.NextDouble();
                if (R() < 0.1f) { float v = 0.15f + R() * 0.75f; return new Color(v, v, v); }   // grayscaleChance 0.1
                return Color.FromHsv(R(), 0.15f + R() * 0.55f, 0.15f + R() * 0.75f);              // hue / sat .15-.7 / val .15-.9
            }
            if (s.DefaultPaints != null && s.DefaultPaints.Length > 0)
                return new Color(s.DefaultPaints[variant % s.DefaultPaints.Length]);
            return Colors.White;   // no paint slot -> unpainted white
        }

        // Driver seat position per vehicle (prefab Seats/Seat_0, Godot space Z-negated) + a small body rise so the 3rd-person
        // driver sits in the right spot -- cars sit LEFT, the quad is CENTRED, the bus is far-left + way back (master).
        // Per-vehicle-CLASS fuel BURN rate (units/sec while driving; PZ-scale, master -- tweakable). Keyword-matched so it
        // covers every build variant. A trailer is never driven -> 0 burn. TANK CAPACITY is now the per-vehicle Spec.Fuel
        // (metric mL, set on each spec), NOT here -- so a jerrycan (mL) and a vehicle tank share units. Burn stays PZ-scale
        // for now: consumption is masked by the infFuel default, so a metric consumption pass is deferred.
        /// <summary>strawberry 2026-09-03: "10x vehicle hp". ONE multiplier on every Spec.Health at build time (the specs keep
        /// their source-relative numbers; HeliBase-built specs pass health positionally, so a per-spec edit would miss them).
        /// Damage dealt (bullets, explosion chain 500, collisions) is deliberately untouched -- cars just last 10x longer.</summary>
        public const float VehicleHealthScale = 10f;
        static float FuelBurnClassOf(string name)
        {
            string n = name ?? "";
            if (n.Contains("Trailer")) return 0f;                                                                        // never driven
            if (n.Contains("Semi")) return 3.2f;                                                                         // semi: guzzles
            if (n.Contains("Truck") || n.Contains("Bus") || n.Contains("Firetruck") || n.Contains("Ural")) return 2.6f; // big trucks / bus
            if (n.Contains("Van") || n.Contains("Ambulance")) return 1.8f;                                               // vans
            if (n.Contains("Quad")) return 0.6f;                                                                         // small ATV: sips
            if (n.Contains("Roadster") || n.Contains("Golf") || n.Contains("Hatchback")) return 1.0f;                   // small cars
            return 1.4f;                                                                                                 // Sedan / Police / Jeep / Humvee / Tractor / Off-Roader / default
        }

        // EVERY SEAT, per vehicle, index 0 = driver (strawberry 2026-08-16: "first we need vehicle seats.
        // switch between them with the function keys. only F1 is the drivers seat").
        //
        // Extracted from each prefab's Seats/Seat_* empties by tools/dump_vehicle_seats.py rather than placed by
        // eye: a seat guessed from the outside of a body mesh puts the passenger's head through the roof on
        // exactly the vehicles with the least headroom. Validated against the hand-tuned driver spots already in
        // SeatOf -- X agrees to the millimetre on all five checked, with Y/Z differing only by the deliberate
        // body-rise noted there, which is what a correct coordinate convention looks like.
        //
        // SORTED BY SEAT INDEX at extraction. The prefab returns them in tree order, which is NOT index order
        // (the sedan hands back Seat_3 before Seat_2), and unsorted they would silently seat the driver in the
        // back of half the fleet.
        /// <summary>Seat positions, VERBATIM from the retail prefabs (tools/vehicle_seats.json, dumped by
        /// tools/dump_vehicle_seats.py). strawberry: "get all the CORRECT seating positions for all vehicles
        /// from the source and implement."
        ///
        /// Mostly they already were -- diffed against the extraction, 20 of the 25 entries matched to 5 mm and
        /// the other 5 differed only by 2-decimal rounding. What the diff DID find:
        ///   - the TANK had no entry at all, so it fell through to a single default seat and its gunner sat on
        ///     the driver;
        ///   - the OTTER's seats were Z-NEGATED against the prefab (+1.23/+0.41 against -1.318/-0.504), which
        ///     put the pilot behind the passenger and facing the tail.
        ///
        /// SORTED BY SEAT INDEX. The prefab returns them in TREE order -- the ambulance dumps Seat_3, Seat_2,
        /// Seat_4 -- so taking the file's order seats the driver in the back.</summary>
        static readonly System.Collections.Generic.Dictionary<string, Vector3[]> SeatTable = new()
        {
            ["jeep"] = new[] { new Vector3(-0.500f, 0.050f, -0.116f), new Vector3(0.500f, 0.050f, -0.116f), new Vector3(-0.500f, 0.050f, 1.404f), new Vector3(0.500f, 0.050f, 1.404f) },   // jeep: 4 seats, verbatim from the prefab
            ["quad"] = new[] { new Vector3(-0.000f, 0.163f, 0.557f), new Vector3(-0.000f, 0.439f, 1.645f) },   // quad: 2 seats, verbatim from the prefab
            ["bus"] = new[] { new Vector3(-0.800f, -0.081f, -2.651f), new Vector3(-0.800f, -0.081f, -1.056f), new Vector3(0.800f, -0.081f, -1.056f), new Vector3(-0.800f, -0.081f, 0.449f), new Vector3(0.800f, -0.081f, 0.449f), new Vector3(-0.800f, -0.081f, 1.866f), new Vector3(0.800f, -0.081f, 1.866f), new Vector3(-0.800f, -0.081f, 3.366f), new Vector3(0.800f, -0.081f, 3.366f), new Vector3(0.000f, -0.081f, 3.366f) },   // bus: 10 seats, verbatim from the prefab
            ["sedan"] = new[] { new Vector3(-0.500f, -0.079f, -0.625f), new Vector3(0.500f, -0.079f, -0.625f), new Vector3(-0.500f, -0.079f, 0.772f), new Vector3(0.500f, -0.079f, 0.772f) },   // sedan: 4 seats, verbatim from the prefab
            ["hatchback"] = new[] { new Vector3(-0.500f, -0.079f, -0.299f), new Vector3(0.500f, -0.079f, -0.299f), new Vector3(-0.500f, -0.079f, 1.240f), new Vector3(0.500f, -0.079f, 1.240f) },   // hatchback: 4 seats, verbatim from the prefab
            ["humvee"] = new[] { new Vector3(-0.500f, -0.033f, -0.480f), new Vector3(0.500f, -0.033f, -0.480f), new Vector3(-0.500f, -0.033f, 0.858f), new Vector3(0.500f, -0.033f, 0.858f) },   // humvee: 4 seats, verbatim from the prefab
            ["roadster"] = new[] { new Vector3(-0.500f, -0.079f, 0.331f), new Vector3(0.500f, -0.079f, 0.331f) },   // roadster: 2 seats, verbatim from the prefab
            ["ambulance"] = new[] { new Vector3(-0.500f, 0.020f, -1.400f), new Vector3(0.500f, 0.020f, -1.400f), new Vector3(-0.603f, 0.051f, 0.138f), new Vector3(0.603f, 0.051f, 0.138f), new Vector3(0.000f, 0.051f, 1.707f) },   // ambulance: 5 seats, verbatim from the prefab
            ["firetruck"] = new[] { new Vector3(-0.500f, 0.193f, -2.396f), new Vector3(0.500f, 0.193f, -2.396f) },   // firetruck: 2 seats, verbatim from the prefab
            ["tractor"] = new[] { new Vector3(0.000f, 0.587f, 1.100f) },   // tractor_0: 1 seats, verbatim from the prefab
            ["ural"] = new[] { new Vector3(-0.500f, 0.055f, -1.302f), new Vector3(0.500f, 0.055f, -1.302f), new Vector3(-0.617f, 0.055f, 0.444f), new Vector3(0.617f, 0.055f, 0.444f), new Vector3(-0.617f, 0.055f, 1.444f), new Vector3(0.617f, 0.055f, 1.444f), new Vector3(-0.617f, 0.055f, 2.444f), new Vector3(0.617f, 0.055f, 2.444f) },   // ural: 8 seats, verbatim from the prefab
            ["police"] = new[] { new Vector3(-0.500f, -0.079f, -0.625f), new Vector3(0.500f, -0.079f, -0.625f), new Vector3(-0.500f, -0.079f, 0.772f), new Vector3(0.500f, -0.079f, 0.772f) },   // police: 4 seats, verbatim from the prefab
            ["offroader"] = new[] { new Vector3(-0.500f, 0.050f, -0.116f), new Vector3(0.500f, 0.050f, -0.116f), new Vector3(-0.500f, 0.050f, 1.404f), new Vector3(0.500f, 0.050f, 1.404f) },   // off_roader: 4 seats, verbatim from the prefab
            ["truck"] = new[] { new Vector3(-0.500f, 0.051f, -0.593f), new Vector3(0.500f, 0.051f, -0.593f), new Vector3(-0.603f, 0.051f, 1.188f), new Vector3(0.603f, 0.051f, 1.188f), new Vector3(0.000f, 0.051f, 1.707f) },   // truck: 5 seats, verbatim from the prefab
            ["van"] = new[] { new Vector3(-0.500f, 0.051f, -0.731f), new Vector3(0.500f, 0.051f, -0.731f), new Vector3(-0.603f, 0.051f, 1.188f), new Vector3(0.603f, 0.051f, 1.188f), new Vector3(0.000f, 0.051f, 1.707f) },   // van: 5 seats, verbatim from the prefab
            ["golf"] = new[] { new Vector3(-0.500f, -0.079f, -0.350f), new Vector3(0.500f, -0.079f, -0.350f), new Vector3(-0.500f, -0.079f, 0.772f), new Vector3(0.500f, -0.079f, 0.772f) },   // vw_golf: 4 seats, verbatim from the prefab
            ["runabout"] = new[] { new Vector3(-0.500f, 0.062f, -0.758f), new Vector3(0.500f, 0.062f, -0.758f), new Vector3(-0.500f, 0.062f, 0.898f), new Vector3(0.500f, 0.062f, 0.898f) },   // runabout: 4 seats, verbatim from the prefab
            ["apc"] = new[] { new Vector3(-0.800f, -0.015f, -1.845f), new Vector3(0.800f, -0.015f, -1.845f), new Vector3(-1.002f, -0.015f, -0.028f), new Vector3(1.002f, -0.015f, -0.028f), new Vector3(-1.002f, -0.015f, 0.972f), new Vector3(1.002f, -0.015f, 0.972f), new Vector3(-1.002f, -0.015f, 1.972f), new Vector3(1.002f, -0.015f, 1.972f) },   // apc: 8 seats, verbatim from the prefab
            ["huey"] = new[] { new Vector3(-0.625f, 0.096f, -1.958f), new Vector3(0.625f, 0.096f, -1.958f), new Vector3(-1.261f, -0.120f, -0.423f), new Vector3(1.261f, -0.120f, -0.423f) },   // huey: 4 seats, verbatim from the prefab
            ["hind"] = new[] { new Vector3(-0.000f, 0.790f, -1.960f), new Vector3(0.000f, 0.095f, -3.677f), new Vector3(0.500f, 0.080f, 0.260f), new Vector3(-0.500f, 0.080f, 0.260f), new Vector3(-0.500f, 0.080f, 1.480f), new Vector3(0.500f, 0.080f, 1.480f) },   // hind: 6 seats, verbatim from the prefab
            ["orca"] = new[] { new Vector3(-0.610f, -0.079f, -0.299f), new Vector3(0.600f, -0.079f, -0.299f), new Vector3(-0.609f, -0.080f, 0.876f), new Vector3(0.601f, -0.080f, 0.876f), new Vector3(1.500f, -0.240f, 2.656f), new Vector3(-1.500f, -0.240f, 2.660f) },   // orca: 6 seats, verbatim from the prefab
            ["skycrane"] = new[] { new Vector3(0.000f, 0.096f, -2.844f) },   // skycrane: 1 seats, verbatim from the prefab
            ["hummingbird"] = new[] { new Vector3(-0.625f, 0.096f, -1.958f), new Vector3(0.625f, 0.096f, -1.958f), new Vector3(-1.261f, -0.120f, -0.423f), new Vector3(1.261f, -0.120f, -0.423f) },   // hummingbird_police: 4 seats, verbatim from the prefab

            ["minicopter"] = new[] { new Vector3(0f, 0.32f, 0.10f) },

            ["scoutcopter"] = new[] { new Vector3(-0.34f, 0.32f, 0.10f), new Vector3(0.34f, 0.32f, 0.10f) },
            ["tank"] = new[] { new Vector3(0.000f, 0.192f, -2.711f), new Vector3(0.000f, 0.000f, -0.000f) },   // tank: 2 seats, verbatim from the prefab
            // OTTER was never actually in this table (the comment above describing its Z-negation fix documented a
            // finding that never got wired in) -- it fell through to SeatOf's jeep-shaped default, a car seat height
            // inside a floatplane cockpit. Verbatim retail Seat_0/Seat_1 (tools/vehicle_seats.json): tandem, both X=0.
            ["otter"] = new[] { new Vector3(0.000f, 0.666f, -1.318f), new Vector3(0.000f, 0.666f, -0.504f) },
        };
        static Vector3 SeatOf(string name) => name switch
        {
            "Sedan" => new Vector3(-0.50f, -0.04f, -0.566f),
            "Hatchback" => new Vector3(-0.50f, -0.04f, -0.239f),
            "Humvee" => new Vector3(-0.50f, 0.07f, -0.480f),
            "Roadster" => new Vector3(-0.50f, -0.04f, 0.390f),
            "Bus" => new Vector3(-0.80f, -0.03f, -2.558f),
            "Quad" => new Vector3(0.00f, 0.26f, 0.557f),
            "Ambulance" => new Vector3(-0.50f, 0.12f, -1.40f),
            "Firetruck" => new Vector3(-0.50f, 0.29f, -2.40f),
            "Tractor" => new Vector3(0.00f, 0.69f, 1.10f),
            "Ural" => new Vector3(-0.50f, 0.10f, -1.21f),
            "Police" => new Vector3(-0.50f, 0.02f, -0.63f),
            _ => new Vector3(-0.50f, 0.10f, -0.024f),   // Jeep + fallback
        };
        /// <summary>The 11 classes above (+ Jeep, which legitimately IS the fallback) that ever got an eyeballed
        /// per-vehicle rise off their own extracted Seat_0. Everything else used to fall through to the SAME
        /// switch -> the Jeep's absolute spot, not "a small rise on THIS vehicle's own seat": a Huey/APC/Tank/
        /// boat/Otter/Semi driver sat at a car's seat coordinate regardless of that vehicle's own size or shape
        /// (strawberry 2026-09-03: "theres still a lot of vehicle seating positions that arent accurate. fix them
        /// all"). Mirrors the identical BuildByName/SpecFor gap fixed 2026-08-16 one level up (whole SPEC, not
        /// just the seat) -- same shape of bug, in the sibling table nobody re-checked when that one was found.</summary>
        static bool HandTunedSeatOf(string name) => name is "Sedan" or "Hatchback" or "Humvee" or "Roadster"
            or "Bus" or "Quad" or "Ambulance" or "Firetruck" or "Tractor" or "Ural" or "Police" or "Jeep";
        /// <summary>Average of the 11 hand-tuned deltas above against their own real Seat_0 (Y +0.04 to +0.10,
        /// Z from -0.005 to +0.09 with no consistent sign -- not touching Z avoids guessing the wrong direction).
        /// Applied to a vehicle's OWN real seat instead of borrowing Jeep's, this is a small forgiving nudge off
        /// a correct base rather than a substitute for one.</summary>
        static readonly Vector3 GenericSeatRise = new Vector3(0f, 0.08f, 0f);

        // Jeep.dat: Speed 12.5, steer 28, front-steered, torque 2.8. Godot space (front = -Z): X +-1.30, front Z -1.40.
        static readonly Spec _jeep = new()
        {
            Mass = 1700f,   // kerb mass, kg
            Body = "jeep_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "jeep_palette.png",
            GlassMesh = "jeep_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py -- open-bodied: a windscreen and nothing else (--skip=rear; the tub has no back window)
            DefaultPaints = new[] { "#475e83", "#a69884", "#437c44", "#495631" },   // src .dat DefaultPaintColors = the 4 faction paints (#475e83 Coalition / #a69884 Desert / #437c44 Forest / #495631 Russia), random pick per spawn
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 12.5f, SpeedMin = -7f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),   // source BoxCollider
            ForwardGears = new[] { 20f, 13.7f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,   // .dat EngineSound (prefab AudioSource = Engine_Medium)
            Fuel = 60_000f, Health = 600f, Name = "Jeep", Horn = "carhorn_04.ogg",   // 60 L tank (metric 1u=1mL; realistic, was the x2500 5,000,000)
            SpotPos = new[] { new Vector3(-0.979f, 0.746f, -2.49f), new Vector3(0.979f, 0.746f, -2.49f) }, OmniPos = new Vector3(0f, 0.878f, -2.47f),   // source prefab Headlights (Z negated)
            TailPos = new[] { new Vector3(-0.979f, 0.746f, 2.48f), new Vector3(0.979f, 0.746f, 2.48f) },   // source prefab Taillights (rear, Z negated)
            SteerPivot = new Vector3(-0.464f, 1.018f, -0.922f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),   // steering wheel centroid + disc normal (PCA)
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("jeep_seats.txt", new Color(0.25f, 0.25f, 0.25f)),        // seats: dark grey (real _Color)
                ("jeep_steer.txt", new Color(0.28f, 0.23f, 0.14f)),        // steering wheel: dark brown
                ("jeep_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // headlights: cream
                ("jeep_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // taillights: red
            },
        };

        // Semi truck cab (Semi_0 prop -> driveable, master). Model_0 = cab + chassis, 3.2w x 7.1L x 3.7h. Heavy: slow
        // steer, low top speed, big engine, tandem rear drive axles. Colours from the prop's own 4x2 palette (blue cab).
        static readonly Spec _semi = new()
        {
            Mass = 7800f,   // kerb mass, kg
            Body = "semi_0.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "semi_palette.png",   // semi_palette = semi_0_albedo with texel0 (the blue body) flagged PAINTABLE (alpha 0) so the cab recolours like every other vehicle -- only the blue panels; metal/red/cream/exhaust stay fixed (strawberry)
            RandomHueGray = true,   // paintable cab -> random civilian colours per spawn (like sedan/quad/hatchback)
            WheelRadius = 0.55f, Engine = 550f, SteerMax = 22f, SteerMin = 10f, SpeedMax = 14f, SpeedMin = -4f, Brake = 34f,   // Engine 950->550: a semi accelerates SLOW + heavy (nerfed further while towing, see Drive) (strawberry 2026-07-15)
            // Cab hull matches the mesh (semi_0.txt): the CAB half (front, mesh Z -2.58..2.0) stands Y 0..1.5 with
            // the tall cab+sleeper as RoofBox("Semi Truck") on top; the REAR chassis (mesh Z 2.0..4.5) is the low
            // BLACK frame, only Y 0..0.96 -- so the trailer's deck overhangs it and the fifth wheel is exposed. The
            // old single Y0..1.5 box ran the full 7.08 length, making the black rear frame 0.54 too tall. (strawberry 2026-07-15)
            BoxSize = new Vector3(3.18f, 1.35f, 4.08f), BoxCenter = new Vector3(0f, 0.825f, -0.54f),   // cab BODY only, Z -2.58..1.5 (front face stays Z -2.58); floor raised 0->0.15 to the mesh underside (was hanging 0.15 below the chassis); behind the cab is all the low frame so the trailer nose can nestle down
            ExtraBoxes = new (Vector3, Vector3)[] { (new Vector3(2.5f, 0.76f, 3.0f), new Vector3(0f, 0.58f, 3.0f)) },   // low black rear frame (Y 0.2..0.96, Z 1.5..4.5) -- floor raised 0->0.2 to the rear-chassis underside; carries the fifth wheel, kept LOW so the coupled trailer sits on it, not over a tall box
            ForwardGears = new[] { 22f, 15f, 10f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_large.ogg", IdlePitch = 0.8f, MaxPitch = 1.5f, IdleVolume = 0.85f, MaxVolume = 1.0f,   // engine_large = the SOURCE heavy/truck engine (bus uses it); low pitch = diesel rumble (strawberry 2026-07-15)
            Fuel = 300_000f, Health = 1000f, Name = "Semi Truck", Horn = "carhorn_03.ogg",   // 300 L tank (metric 1u=1mL; realistic big-rig). CarHorn_03 = the SOURCE heavy-truck horn (Ural/Firetruck/Ambulance use it in vanilla; deepest of the ripped horns) (strawberry 2026-07-15)
            SpotPos = new[] { new Vector3(-1.175f, 0.86f, -2.60f), new Vector3(1.175f, 0.86f, -2.60f) }, OmniPos = Vector3.Zero,   // beam sources CENTERED on the real headlight lenses (X±1.175, Y0.86, front face Z-2.58); no middle omni fill (strawberry)
            TailPos = new[] { new Vector3(-0.82f, 0.65f, 4.45f), new Vector3(0.82f, 0.65f, 4.45f) },   // red spot sources centered on the cab taillight blocks (strawberry)
            HeadlightZoneMin = new Vector3(-1.44f, 0.66f, -2.63f), HeadlightZoneMax = new Vector3(-0.92f, 1.05f, -2.20f),   // LEFT headlight = the CREAM-texel geometry X[-1.40,-0.95] Y[0.71,1.01] near the fender (NOT the grey trim by the grille I was wrongly lighting). Verified: zone catches exactly the 20 cream tris, nothing else. right = auto X-mirror (strawberry)
            TaillightMesh = new[] { new Vector3(-0.82f, 0.65f, 4.45f), new Vector3(0.82f, 0.65f, 4.45f) },   // red brake/tail blocks on the rear frame; moved closer together again (1.035->0.82) (strawberry). Cab has NO baked taillights so these blocks ARE the cab's
            SeatModelFile = "roadster_seats.txt", SeatModel = new Vector3(0f, 2.2f, 0.3f),   // REAL ripped seats (single 2-seat row) back near the cab rear wall (strawberry: use src, not proc-gen)
            SteerModel = "jeep_steer.txt", SteerPivot = new Vector3(-0.5f, 2.1f, -0.45f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),   // REAL ripped steering wheel in front of the driver (back a hair -0.55->-0.45); turns 1:1 with the wheels (strawberry)
            DriverEye = new Vector3(-0.5f, 2.5f, 0.05f),   // eye above the seat, looking forward over the hood (floor ~Y1.5, roof ~Y3.85)
            WheelRadii = new[] { 0.65f, 0.65f, 0.65f, 0.65f, 0.65f, 0.65f },   // big semi tyres (mesh scales 1.24x). Axle Y kept at 0.55 so the taller tyre LIFTS the truck (ride height = radius+restLen-axleY). tandem axles spaced >1.5 apart so the fat tyres don't overlap
            Wheels = new (float, float, float, bool)[]
            {
                (-1.46f, 0.55f, -1.62f, true),  (1.46f, 0.55f, -1.62f, true),    // front axle (steered): out 1.28->1.46 (under the fender) + back -1.95->-1.62, central in the wheel-well arch (strawberry: "back + wider just a touch" more)
                (-1.28f, 0.55f,  1.90f, false), (1.28f, 0.55f,  1.90f, false),   // rear axle 1 (drive)
                (-1.28f, 0.55f,  3.70f, false), (1.28f, 0.55f,  3.70f, false),   // rear axle 2 (tandem, drive) -- moved back 3.5->3.7 (strawberry)
            },
            Parts = new (string, Color)[] { },   // Model_0 is the whole cab; no separate seat/steer/light parts
            // No vehicle prefab to extract a Seats node from (Semi_0 is a repurposed static PROP, not one of the
            // Bundles/Vehicles/* entries tools/dump_vehicle_seats.py scans) -- it fell through to SeatOf's jeep
            // spot, seating the driver at a car's Y=0.10 inside a cab whose own floor sits at Y~1.5. Triangulated
            // instead from geometry this Spec ALREADY authors for the same driver: X=-0.5 matches SteerPivot.X and
            // DriverEye.X on every other vehicle's convention; Y/Z = SeatModel, the real ripped seat mesh's own
            // placement, so the body sits ON the visible seat rather than at an unrelated vehicle's coordinate.
            Seats = new[] { new Vector3(-0.5f, 2.2f, 0.3f) },
            FifthWheel = new Vector3(0f, 0.62f, 3.0f),   // over the rear tandem (moved back from 2.6 -> pivot sits further back on the cab, more trailer clearance). Y matched to the trailer kingpin's Y (0.62) so the coupled trailer rides LEVEL. (strawberry 2026-07-15)
        };

        // Semi trailer (semi_1 prop -> towable). TOWED, not driven: no engine/steer/drive (Engine=0 -> _engineForce=0
        // so its traction wheels apply no force); it's dragged behind the cab by a fifth-wheel PinJoint hitch (see
        // BuildTrailer). semi_0 (cab) + semi_1 (trailer) are one authoring set and SHARE the flat blue _MainTex
        // (verified via UnityPy: same texture path_id), so it reuses semi_0_albedo.png. Bbox 3.0w x 2.5h x 16.1L.
        // Wheels = a rear tandem bogie only; the front of the trailer rests on the cab's fifth wheel (no front axle).
        // NOTE: orientation from the rip is UNVERIFIED (catboy's flip-check rendered edge-on) -- render + eyeball
        // behind the cab; if inverted, roll the mesh 180 deg about Z (x->-x, y->(minY+maxY)-y) and re-ground.
        static readonly Spec _trailer = new()
        {
            Mass = 6000f,   // kerb mass, kg
            Body = "trailer_0.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "semi_0_albedo.png",
            DefaultPaints = new[] { "#3a5a78" },   // shares the cab's blue palette
            WheelRadius = 0.55f, Engine = 0f, SteerMax = 0f, SteerMin = 0f, SpeedMax = 0f, SpeedMin = 0f, Brake = 0f,   // towed: no drive/steer/brake of its own
            // Flatbed hull matches the mesh (trailer_0.txt): the DECK is Y 0.15..1.25 (top surface ~1.25, underside
            // ~0.15) running the main bed Z -4.25..8.1 -- NOT the old Y 1.5..2.5 slab, which floated ~1.3 above the
            // real deck. The front steps down to a narrow gooseneck (±0.95) + kingpin coupler, capped by the tall
            // front headboard wall (Y up to 2.5). ExtraBoxes carry those front features. (strawberry 2026-07-15)
            BoxSize = new Vector3(3.0f, 1.10f, 12.35f), BoxCenter = new Vector3(0f, 0.70f, 1.9f),   // main flatbed deck (Y 0.15..1.25, Z -4.25..8.1)
            ExtraBoxes = new (Vector3, Vector3)[]
            {
                (new Vector3(3.0f, 1.5f, 0.5f), new Vector3(0f, 1.75f, -7.75f)),     // front headboard wall -- tightened to the MODEL: X±1.5, Y 1.0..2.5, Z -8..-7.5 (was Y0.15..2.5 Z-8..-7, too tall+deep vs the mesh) (strawberry 2026-07-15)
                (new Vector3(1.9f, 1.10f, 3.6f), new Vector3(0f, 0.70f, -5.7f)),     // gooseneck + kingpin coupler in ONE box (narrow ±0.95, Z -7.5..-3.9) -> the coupling area is a single clean hull, not a pile of overlapping boxes
            },
            ForwardGears = new[] { 1f }, ReverseGear = 1f, ShiftUpRpm = 5000f,   // unused (no engine) but non-null for the drive logic
            Sound = null,   // no engine -> no engine loop
            Fuel = 1000f, Health = 600f, Name = "Semi Trailer",   // never driven; >0 avoids a fuel-fraction div-by-zero (metric 1u=1mL, nominal 1 L)
            TailPos = new[] { new Vector3(-1.13f, 1.0f, 8.0f), new Vector3(1.13f, 1.0f, 8.0f) },   // red spot sources centered on the trailer's baked taillights (X±1.13, Y1.0, Z8.0) (strawberry)
            TaillightZoneMin = new Vector3(-1.42f, 0.84f, 7.85f), TaillightZoneMax = new Vector3(-0.84f, 1.17f, 8.15f),   // split the REAL baked red taillights (X[0.88,1.38] Y[0.88,1.13] Z[7.90,8.10]) out -> emissive, driven by the cab pass-through. NO added blocks (was duping the baked ones) (strawberry)
            SteerPivot = Vector3.Zero, SteerAxis = Vector3.Zero,
            WheelRadii = new[] { 0.65f, 0.65f, 0.65f, 0.65f },   // big trailer tyres to match the cab. Axle Y kept at 0.55 so the taller tyre lifts the bed (matches the cab's lift, so the coupled deck rises level)
            Wheels = new (float, float, float, bool)[]
            {
                (-1.30f, 0.55f, 5.4f, false), (1.30f, 0.55f, 5.4f, false),   // rear tandem bogie -- both axles moved forward 0.3 (5.7->5.4, 7.3->7.0) (strawberry)
                (-1.30f, 0.55f, 7.0f, false), (1.30f, 0.55f, 7.0f, false),
            },
            Parts = new (string, Color)[] { },   // Model_0 is the whole trailer box; no separate parts
            Kingpin = new Vector3(0f, 0.62f, -6.6f),   // centered on the round coupler plate under the gooseneck (was a guessed 0.4,-7.5 which sat forward+low of it)
            // Front landing legs: a ground-to-deck support so the nose sits LEVEL when parked (rigid body on rear
            // wheels + this = level). Placed at Z -4.5, BEHIND where the cab's rear frame reaches under the front
            // (~Z -5.6 at couple), so the cab can still back all the way under. Toggled OFF the instant it couples.
            // Landing gear extended DOWN to Y-0.5 (box Y-0.5..1.5): parked, it props the nose ~0.5 above the body
            // origin so the connection side sits HIGHER than the coupled fifth-wheel height -> the trailer visibly
            // DROPS onto the cab when hitched (legs then retract). The leg VISUAL is stretched to match (see Build).
            LandingGearSize = new Vector3(2.24f, 1.63f, 0.5f), LandingGearCenter = new Vector3(0f, 0.315f, -4.13f),   // matches the STRETCHED leg mesh (X±1.12, Y-0.5..1.13, Z-4.38..-3.88); top capped at 1.13 so it no longer pokes above the flatbed top (1.25) (strawberry 2026-07-15)
            // the landing-leg triangles live in a clean mesh band at Z -4.5..-3.8 (feet Y0 up to the deck underside),
            // between the gooseneck (Z -5.7) and the deck front (Z -1) -> split them out so they hide when coupled
            LandingLegZoneMin = new Vector3(-1.25f, -0.05f, -4.55f), LandingLegZoneMax = new Vector3(1.25f, 1.16f, -3.75f),
            LandingLegScaleY = 1.44f, LandingLegPivotY = 1.13f,   // stretch the legs down ~0.5 (anchored at the deck ~Y1.13) so they reach the ground with the nose propped up 0.5
        };

        // Quad.dat: Speed 13.5, steer 32, front-steered, torque 4.8. X +-0.50, front Z -0.39 / rear 1.44, Y 0.20.
        static readonly Spec _quad = new()
        {
            Mass = 300f,   // kerb mass, kg
            Body = "quad_body.txt", Wheel = "quad_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "quad_palette.png",
            RandomHueGray = true,   // source RandomHueOrGrayscale -> our curated CarColors list
            WheelRadius = 0.45f, Engine = 520f, SteerMax = 32f, SteerMin = 16f, SpeedMax = 13.5f, SpeedMin = -5f, Brake = 24f,
            BoxSize = new Vector3(2.0f, 0.777f, 3.581f), BoxCenter = new Vector3(0f, 0.478f, 0.407f),   // source BoxCollider
            ForwardGears = new[] { 20f, 10f }, ReverseGear = 8f, ShiftUpRpm = 3000f,
            Sound = "engine_small.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,   // .dat EngineSound (prefab AudioSource = Engine_Small)
            Fuel = 15_000f, Health = 450f, Name = "Quad", Horn = "carhorn_01.ogg",   // 15 L tank (metric 1u=1mL; realistic ATV)
            SteerPivot = new Vector3(0f, 1.00f, -0.32f), SteerAxis = new Vector3(0f, 1f, 0f),   // handlebars: pivot at the prefab Steer node, yaw around vertical
            Wheels = new (float, float, float, bool)[]
            { (-0.50f, 0.20f, -0.39f, true), (0.50f, 0.20f, -0.39f, true), (-0.50f, 0.20f, 1.44f, false), (0.50f, 0.20f, 1.44f, false) },
            Parts = new (string, Color)[]
            {
                ("quad_steer.txt", new Color(0.15f, 0.15f, 0.15f)),        // handlebars: dark metal/rubber (turns with steering)
                ("quad_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("quad_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
            },
        };

        // Bus.dat: Speed 12, steer 24->12, front-steered, torque 2.5. Long 4-wheeler, 10 seats.
        static readonly Spec _bus = new()
        {
            Mass = 12000f,   // kerb mass, kg
            Body = "bus_body.txt", Wheel = "bus_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "bus_palette.png",
            GlassMesh = "bus_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // 12 side panes; band measured, see tools/gen_vehicle_glass.py --band
            DefaultPaints = new[] { "#d4d4d4" },   // source .dat: single near-white default
            WheelRadius = 0.6f, Engine = 780f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 12f, SpeedMin = -6f, Brake = 24f,
            BoxSize = new Vector3(3.0f, 1.018f, 7.964f), BoxCenter = new Vector3(0f, 0.361f, 0.281f),   // source BoxCollider
            ForwardGears = new[] { 20f, 14.6f }, ReverseGear = 12f, ShiftUpRpm = 4000f,
            Sound = "engine_large.ogg", IdlePitch = 1.0f, MaxPitch = 1.8f, IdleVolume = 0.75f, MaxVolume = 1.0f,   // .dat EngineSound (prefab AudioSource = Engine_Large; bus MaxPitch 1.8)
            Fuel = 200_000f, Health = 700f, Rarity = EItemRarity.UNCOMMON, Name = "Bus", Horn = "carhorn_04.ogg",
            NoTrunk = true, RearEngine = true,   // no boot; the engine bay is the back (master 2026-09-04)
            // FUNCTIONAL BI-FOLD DOOR (master 2026-09-04 "give the bus a functional bi-fold door"): the door panel is the 12.5 cm box
            // inside the front-right doorway (X 1.293..1.418, Z -3.35..-2.15, Y -0.23..1.97, measured off bus_body.txt), two windows
            // split by the mullion at Z -2.75. Leaf A hinges at the front jamb, leaf B at the mullion; both windows ride their leaf.
            DoorZoneMin = new Vector3(1.28f, -0.25f, -3.36f), DoorZoneMax = new Vector3(1.43f, 1.99f, -2.14f),
            // The panel is modelled 10 cm wider than the 1 m doorway (Z -3.25..-2.25; its ends hid inside the jambs) and 10 cm deeper than
            // the first step (-0.125): trimmed to both, capped, so hinge A sits ON the front jamb and the open stack (leaves 0.5 m each)
            // lies flush against the inner side of the step-well wall at Z -3.25 instead of through it.
            DoorHingeX = 1.293f, DoorHingeBX = 1.428f, DoorHingeZ = -3.25f, DoorSplitZ = -2.75f, DoorFoldDeg = 90f, DoorGlassA = "r_front", DoorGlassB = "r_mid1",
            DoorTrimZ0 = -3.25f, DoorTrimZ1 = -2.25f, DoorTrimY = -0.12f,
            // FLOOR (master 2026-09-05): the first step (outside, -0.125) extends seamlessly into the bus where the leaves swing --
            // the cabin floor (+0.125) is cut out over that patch (X 0.68..1.23 between the step-well walls) and the old 2nd-step riser
            // at X 1.231 goes; a floor at the step's level + risers on the three inner sides take their place.
            DoorFloorCutMin = new Vector3(0.68f, 0.05f, -3.25f), DoorFloorCutMax = new Vector3(1.229f, 0.1251f, -2.25f), DoorPocketY = -0.125f,
            DoorRiserCutMin = new Vector3(1.229f, -0.13f, -3.25f), DoorRiserCutMax = new Vector3(1.235f, 0.1251f, -2.25f),   // 200 L tank (metric 1u=1mL; realistic bus)
            Wheels = new (float, float, float, bool)[]
            { (-1.50f, 0.08f, -1.52f, true), (1.50f, 0.08f, -1.52f, true), (-1.50f, 0.08f, 2.69f, false), (1.50f, 0.08f, 2.69f, false) },
            Parts = new (string, Color)[]
            {
                ("bus_seats.txt", new Color(0.25f, 0.25f, 0.25f)),         // 10 grey seats
                ("bus_steer.txt", new Color(0.28f, 0.23f, 0.14f)),         // steering wheel brown
                ("bus_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),    // cream
                ("bus_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),    // red
            },
        };

        // Sedan.dat: Speed 16.5 (fastest so far), steer 28->14, front-steered, RandomHueOrGrayscale. 4-seat road car, ~6m long.
        static readonly Spec _sedan = new()
        {
            Mass = 1500f,   // kerb mass, kg
            Body = "sedan_body.txt", Wheel = "sedan_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "sedan_palette.png",
            GlassMesh = "sedan_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // window panes fitted to the greenhouse apertures; tint = GlassPane.DefaultHue @ its 0.26 alpha (strawberry: "the same glass as windows in the building editor")
            RandomHueGray = true,   // source RandomHueOrGrayscale -> our curated CarColors
            WheelRadius = 0.6f, Engine = 700f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 16.5f, SpeedMin = -6f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 0.916f, 5.656f), BoxCenter = new Vector3(0f, 0.548f, -0.063f),   // source BoxCollider (Z negated)
            ForwardGears = new[] { 14f, 8.75f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 50_000f, Health = 600f, Name = "Sedan", Horn = "carhorn_02.ogg",   // 50 L tank (metric 1u=1mL; realistic sedan)
            SpotPos = new[] { new Vector3(-0.765f, 0.708f, -2.969f), new Vector3(0.765f, 0.708f, -2.969f) }, OmniPos = new Vector3(0f, 0.841f, -2.945f),   // prefab Headlights (Z neg)
            TailPos = new[] { new Vector3(-0.979f, 0.688f, 2.841f), new Vector3(0.979f, 0.688f, 2.841f) },   // prefab Taillights (rear, Z neg)
            SteerPivot = new Vector3(-0.464f, 0.894f, -1.416f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),   // steer centroid + disc normal (PCA)
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.62f, true), (1.30f, 0.25f, -1.62f, true), (-1.30f, 0.25f, 1.38f, false), (1.30f, 0.25f, 1.38f, false) },   // X +-1.30, front Z -1.62, rear 1.38
            Parts = new (string, Color)[]
            {
                ("sedan_seats.txt", new Color(0.25f, 0.25f, 0.25f)),        // 4 grey seats
                ("sedan_steer.txt", new Color(0.28f, 0.23f, 0.14f)),        // steering wheel brown
                ("sedan_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("sedan_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
            },
        };

        // Hatchback.dat: Speed 15, steer 24->12, front-steered, RandomHueOrGrayscale. Compact 4-seat car (~5.5m).
        static readonly Spec _hatchback = new()
        {
            Mass = 1100f,   // kerb mass, kg
            Body = "hatchback_body.txt", Wheel = "hatchback_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "hatchback_palette.png",
            GlassMesh = "hatchback_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 680f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 15f, SpeedMin = -5f, Brake = 24f,
            BoxSize = new Vector3(2.5f, 0.916f, 5.261f), BoxCenter = new Vector3(0f, 0.548f, -0.003f),
            ForwardGears = new[] { 14f, 8.75f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 45_000f, Health = 650f, Name = "Hatchback", Horn = "carhorn_01.ogg",   // 45 L tank (metric 1u=1mL; realistic hatchback)
            SpotPos = new[] { new Vector3(-0.765f, 0.571f, -2.679f), new Vector3(0.765f, 0.571f, -2.679f) }, OmniPos = new Vector3(0f, 0.703f, -2.655f),
            TailPos = new[] { new Vector3(-0.979f, 0.738f, 2.677f), new Vector3(0.979f, 0.738f, 2.677f) },
            SteerPivot = new Vector3(-0.464f, 0.894f, -1.089f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.41f, true), (1.30f, 0.25f, -1.41f, true), (-1.30f, 0.25f, 1.39f, false), (1.30f, 0.25f, 1.39f, false) },
            Parts = new (string, Color)[]
            {
                ("hatchback_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("hatchback_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("hatchback_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("hatchback_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // Humvee.dat: Speed 14, steer 24->12, front-steered, faction DefaultPaints (military, like the jeep). Heavy 4x4, brake 40.
        static readonly Spec _humvee = new()
        {
            Mass = 2400f,   // kerb mass, kg
            Body = "humvee_body.txt", Wheel = "humvee_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "humvee_palette.png",
            GlassMesh = "humvee_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py
            DefaultPaints = new[] { "#475e83", "#a69884", "#437c44", "#495631" },   // src .dat DefaultPaintColors = the 4 faction paints (#475e83 Coalition / #a69884 Desert / #437c44 Forest / #495631 Russia), random pick per spawn
            WheelRadius = 0.6f, Engine = 680f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 14f, SpeedMin = -6f, Brake = 40f,
            BoxSize = new Vector3(2.5f, 1.032f, 5.029f), BoxCenter = new Vector3(0f, 0.605f, -0.018f),
            ForwardGears = new[] { 20f, 12.56f }, ReverseGear = 8f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 95_000f, Health = 550f, Name = "Humvee", Horn = "carhorn_03.ogg",   // 95 L tank (metric 1u=1mL; realistic military humvee)
            SpotPos = new[] { new Vector3(-0.979f, 0.741f, -2.511f), new Vector3(0.979f, 0.741f, -2.511f) }, OmniPos = new Vector3(0f, 0.873f, -2.487f),
            TailPos = new[] { new Vector3(-0.979f, 0.738f, 2.548f), new Vector3(0.979f, 0.738f, 2.548f) },
            SteerPivot = new Vector3(-0.464f, 0.94f, -1.27f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("humvee_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("humvee_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("humvee_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("humvee_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // Roadster.dat: Speed 19 (fastest!), steer 28->14, RandomHueOrGrayscale, its OWN horn. Fragile 2-seat sports car (Health 500).
        static readonly Spec _roadster = new()
        {
            Mass = 1400f,   // kerb mass, kg
            Body = "roadster_body.txt", Wheel = "roadster_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "roadster_palette.png",
            GlassMesh = "roadster_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 760f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 19f, SpeedMin = -5f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 0.916f, 5.656f), BoxCenter = new Vector3(0f, 0.548f, -0.063f),
            ForwardGears = new[] { 14f, 8f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 50_000f, Health = 500f, Rarity = EItemRarity.RARE, Name = "Roadster", Horn = "roadster_horn.ogg",   // 50 L tank (metric 1u=1mL; realistic sports car)
            SpotPos = new[] { new Vector3(-0.765f, 0.708f, -2.969f), new Vector3(0.765f, 0.708f, -2.969f) }, OmniPos = new Vector3(0f, 0.841f, -2.945f),
            TailPos = new[] { new Vector3(-0.979f, 0.688f, 2.841f), new Vector3(0.979f, 0.688f, 2.841f) },
            SteerPivot = new Vector3(-0.464f, 0.894f, -0.46f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.225f, -1.62f, true), (1.30f, 0.225f, -1.62f, true), (-1.30f, 0.225f, 1.38f, false), (1.30f, 0.225f, 1.38f, false) },
            Parts = new (string, Color)[]
            {
                ("roadster_seats.txt", new Color(0.25f, 0.25f, 0.25f)),        // 2 grey seats
                ("roadster_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("roadster_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("roadster_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // Ambulance.dat: Speed 15.5, steer 28->14, front-steered 4-wheel van, white DefaultPaint, Health 600, CarHorn_03.
        static readonly Spec _ambulance = new()
        {
            Mass = 3600f,   // kerb mass, kg
            Body = "ambulance_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "ambulance_palette.png",
            GlassMesh = "ambulance_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py
            DefaultPaints = new[] { "#e8e8e8" },   // white ambulance
            WheelRadius = 0.6f, Engine = 700f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 15.5f, SpeedMin = -6.5f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 2.0f, 5.0f), BoxCenter = new Vector3(0f, 1.0f, 0f),   // tall van (compound BoxCollider -> one encompassing box)
            ForwardGears = new[] { 14f, 8f }, ReverseGear = 8f, ShiftUpRpm = 4500f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 80_000f, Health = 600f, Rarity = EItemRarity.UNCOMMON, Name = "Ambulance", Horn = "carhorn_03.ogg",   // 80 L (metric 1u=1mL)
            SpotPos = new[] { new Vector3(-0.71f, 0.74f, -2.58f), new Vector3(0.71f, 0.74f, -2.58f) }, OmniPos = new Vector3(0f, 0.87f, -2.56f),
            TailPos = new[] { new Vector3(-0.95f, 0.71f, 2.59f), new Vector3(0.95f, 0.71f, 2.59f) },
            SteerPivot = new Vector3(-0.47f, 0.99f, -2.21f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("ambulance_seats.txt", new Color(0.25f, 0.25f, 0.25f)),   // seats (Seat_0/Seat_1 extracted -- were missing, master)
                ("ambulance_steer.txt", new Color(0.15f, 0.15f, 0.15f)),   // steering wheel: dark
                ("ambulance_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("ambulance_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
                ("ambulance_siren0.txt", new Color(0.5f, 0.08f, 0.08f)),   // roof lightbar (left) red lens -- flashes with the siren
                ("ambulance_siren1.txt", new Color(0.08f, 0.12f, 0.5f)),   // roof lightbar (right) blue lens
            },
        };

        // Firetruck.dat: Speed 14.5, steer 48->24 (big), 6-wheel, red DefaultPaint, Health 700, CarHorn_03.
        static readonly Spec _firetruck = new()
        {
            Mass = 12000f,   // kerb mass, kg
            Body = "firetruck_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "firetruck_palette.png",
            GlassMesh = "firetruck_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py
            DefaultPaints = new[] { "#b81c1c" },   // red firetruck
            WheelRadius = 0.6f, Engine = 800f, SteerMax = 48f, SteerMin = 24f, SpeedMax = 14.5f, SpeedMin = -6f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 2.0f, 7.0f), BoxCenter = new Vector3(0f, 1.0f, 0f),
            ForwardGears = new[] { 20f, 12f }, ReverseGear = 8f, ShiftUpRpm = 4000f,
            Sound = "engine_large.ogg", IdlePitch = 1.0f, MaxPitch = 1.8f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 200_000f, Health = 700f, Rarity = EItemRarity.UNCOMMON, Name = "Firetruck", Horn = "carhorn_03.ogg",   // 200 L (metric 1u=1mL)
            SpotPos = new[] { new Vector3(-0.69f, 0.89f, -3.59f), new Vector3(0.69f, 0.89f, -3.59f) }, OmniPos = new Vector3(0f, 1.02f, -3.57f),
            TailPos = new[] { new Vector3(-0.98f, 0.55f, 3.64f), new Vector3(0.98f, 0.55f, 3.64f) },
            SteerPivot = new Vector3(-0.47f, 1.16f, -3.20f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            {
                (-1.30f, 0.25f, -2.33f, true), (1.30f, 0.25f, -2.33f, true),    // front (steered)
                (-1.30f, 0.25f, 0.80f, false), (1.30f, 0.25f, 0.80f, false),    // mid
                (-1.30f, 0.25f, 2.24f, false), (1.30f, 0.25f, 2.24f, false),    // rear
            },
            Parts = new (string, Color)[]
            {
                ("firetruck_seats.txt", new Color(0.25f, 0.25f, 0.25f)),   // seats (Seat_0/Seat_1 extracted -- were missing, master)
                ("firetruck_steer.txt", new Color(0.15f, 0.15f, 0.15f)),
                ("firetruck_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("firetruck_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
                ("firetruck_siren0.txt", new Color(0.5f, 0.08f, 0.08f)),   // roof lightbar (left) red lens -- flashes with the siren
                ("firetruck_siren1.txt", new Color(0.08f, 0.12f, 0.5f)),   // roof lightbar (right) blue lens
            },
        };

        // Tractor_0.dat: Speed 10 (slow), steer 24->12, front-steered, big-rear/small-front wheels, green, Health 700, CarHorn_03.
        static readonly Spec _tractor = new()
        {
            Mass = 4000f,   // kerb mass, kg
            Body = "tractor_body.txt", Wheel = "tractor_wheel_front.txt", WheelTex = "tractor_wheel_albedo.png", Palette = "tractor_palette.png",
            GlassMesh = "tractor_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py
            DefaultPaints = new[] { "#3f7d2f" },   // green tractor
            WheelRadius = 0.90f, WheelRadii = new[] { 0.90f, 0.90f, 1.05f, 1.05f },   // src Tractor_0 Tire WheelCollider radii: 0.90 front / 1.05 rear (the real yellow tractor wheel model)
            Engine = 620f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 10f, SpeedMin = -5f, Brake = 24f,
            BoxSize = new Vector3(2.5f, 1.8f, 4.78f), BoxCenter = new Vector3(0f, 0.72f, -0.12f),
            ForwardGears = new[] { 20f, 12f }, ReverseGear = 8f, ShiftUpRpm = 3000f,
            Sound = "engine_large.ogg", IdlePitch = 1.0f, MaxPitch = 1.8f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 40_000f, Health = 700f, Name = "Tractor", Horn = "carhorn_03.ogg",
            NoTrunk = true,   // no boot on a tractor (master 2026-09-04)   // 40 L (metric 1u=1mL)
            SpotPos = new[] { new Vector3(-0.40f, 1.26f, -2.65f), new Vector3(0.40f, 1.26f, -2.65f) }, OmniPos = new Vector3(0f, 1.40f, -2.62f),
            TailPos = new[] { new Vector3(0.70f, 1.08f, 2.45f), new Vector3(-0.70f, 1.08f, 2.45f) },
            SteerPivot = new Vector3(0f, 1.56f, -0.29f), SteerAxis = new Vector3(0f, 0.5f, 0.866f),   // upright tractor column
            Wheels = new (float, float, float, bool)[]
            { (-0.903f, 0.450f, -1.545f, true), (0.903f, 0.450f, -1.545f, true), (-1.505f, 0.525f, 1.359f, false), (1.505f, 0.525f, 1.359f, false) },   // src Tire WheelCollider positions (Z-flipped): front y0.45 / rear y0.525
            Parts = new (string, Color)[]
            {
                ("tractor_steer.txt", new Color(0.15f, 0.15f, 0.15f)),
                ("tractor_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("tractor_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
            },
        };

        // Ural.dat: Speed 14.5, steer 48->24, 6-wheel military truck, forest DefaultPaint, Health 700, CarHorn_03.
        static readonly Spec _ural = new()
        {
            Mass = 8000f,   // kerb mass, kg
            Body = "ural_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "ural_palette.png",
            GlassMesh = "ural_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived by tools/gen_vehicle_glass.py --band=1.20,1.85 -- the band is MEASURED: casting across the cab skin, the side opening runs y 1.20..1.85, where the silhouette rule derived 1.89..2.32 (a 43 cm band with nothing in it) and produced no panes at all
            DefaultPaints = new[] { "#475e83", "#a69884", "#437c44", "#495631" },   // src 4 faction paints (Coalition/Desert/Forest/Russia)
            WheelRadius = 0.6f, Engine = 800f, SteerMax = 48f, SteerMin = 24f, SpeedMax = 14.5f, SpeedMin = -6f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 2.0f, 6.6f), BoxCenter = new Vector3(0f, 1.0f, 0f),
            ForwardGears = new[] { 20f, 12f }, ReverseGear = 8f, ShiftUpRpm = 4000f,
            Sound = "engine_large.ogg", IdlePitch = 1.0f, MaxPitch = 1.8f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 300_000f, Health = 700f, Rarity = EItemRarity.RARE, Name = "Ural", Horn = "carhorn_03.ogg",   // 300 L (metric 1u=1mL)
            SpotPos = new[] { new Vector3(-0.97f, 0.78f, -3.12f), new Vector3(0.97f, 0.78f, -3.12f) }, OmniPos = new Vector3(0f, 0.91f, -3.10f),
            TailPos = new[] { new Vector3(-0.98f, 0.73f, 3.30f), new Vector3(0.98f, 0.73f, 3.30f) },
            SteerPivot = new Vector3(-0.47f, 1.03f, -2.11f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            {
                (-1.30f, 0.25f, -2.32f, true), (1.30f, 0.25f, -2.32f, true),    // front (steered)
                (-1.30f, 0.25f, 0.80f, false), (1.30f, 0.25f, 0.80f, false),    // mid
                (-1.30f, 0.25f, 2.20f, false), (1.30f, 0.25f, 2.20f, false),    // rear
            },
            Parts = new (string, Color)[]
            {
                ("ural_steer.txt", new Color(0.15f, 0.15f, 0.15f)),
                ("ural_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("ural_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
            },
        };

        // Police.dat: Speed 17, steer 28->14, front-steered cruiser, paintable livery, Health 600, CarHorn_02.
        static readonly Spec _police = new()
        {
            Mass = 1800f,   // kerb mass, kg
            Body = "police_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "police_palette.png",
            GlassMesh = "police_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py
            DefaultPaints = new[] { "#d4d4d4" },   // source Police.dat DefaultPaintColors = #d4d4d4 (white body; the palette's black livery = a black/white cruiser)
            WheelRadius = 0.6f, Engine = 720f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 17f, SpeedMin = -6f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 0.916f, 5.656f), BoxCenter = new Vector3(0f, 0.548f, -0.063f),
            ForwardGears = new[] { 14f, 8f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 60_000f, Health = 600f, Rarity = EItemRarity.UNCOMMON, Name = "Police", Horn = "carhorn_02.ogg",   // 60 L (metric 1u=1mL)
            SpotPos = new[] { new Vector3(-0.77f, 0.71f, -2.97f), new Vector3(0.77f, 0.71f, -2.97f) }, OmniPos = new Vector3(0f, 0.84f, -2.95f),
            TailPos = new[] { new Vector3(-0.98f, 0.69f, 2.84f), new Vector3(0.98f, 0.69f, 2.84f) },
            SteerPivot = new Vector3(-0.47f, 0.90f, -1.42f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.60f, true), (1.30f, 0.25f, -1.60f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("sedan_seats.txt", new Color(0.25f, 0.25f, 0.25f)),   // police reuses the sedan seats (no separate Seat node; sedan-class body) -- were missing, master
                ("police_steer.txt", new Color(0.15f, 0.15f, 0.15f)),
                ("police_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // cream
                ("police_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // red
                ("police_siren0.txt", new Color(0.5f, 0.08f, 0.08f)),   // roof lightbar (left) red lens -- flashes with the siren
                ("police_siren1.txt", new Color(0.08f, 0.12f, 0.5f)),   // roof lightbar (right) blue lens
            },
        };

        // Off_Roader.dat: Speed -7..12.5, steer 12->24, AWD 4-wheel buggy, RandomHueOrGrayscale, Health 600, CarHorn_04.
        // Shares the jeep chassis: identical wheel/headlight/taillight/steer layout (source vehicle.prefab positions match).
        static readonly Spec _offroader = new()
        {
            Mass = 2000f,   // kerb mass, kg
            Body = "offroad_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "offroad_palette.png",
            GlassMesh = "offroader_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py
            RandomHueGray = true,   // source DefaultPaintColor_Mode RandomHueOrGrayscale -> random civilian colour per spawn
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 12.5f, SpeedMin = -7f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),   // jeep-chassis BoxCollider
            ForwardGears = new[] { 20f, 13.7f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 80_000f, Health = 600f, Name = "Off_Roader", Horn = "carhorn_04.ogg",   // 80 L (metric 1u=1mL)
            SpotPos = new[] { new Vector3(-0.979f, 0.746f, -2.49f), new Vector3(0.979f, 0.746f, -2.49f) }, OmniPos = new Vector3(0f, 0.878f, -2.47f),   // source Headlights (Z negated)
            TailPos = new[] { new Vector3(-0.979f, 0.746f, 2.48f), new Vector3(0.979f, 0.746f, 2.48f) },   // source Taillights (Z negated)
            SteerPivot = new Vector3(-0.465f, 1.022f, -0.923f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),   // source Steer node centroid + disc normal
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("offroad_seats.txt", new Color(0.25f, 0.25f, 0.25f)),        // seats: dark grey
                ("offroad_steer.txt", new Color(0.28f, 0.23f, 0.14f)),        // steering wheel: dark brown
                ("offroad_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // headlights: cream
                ("offroad_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // taillights: red
            },
        };

        // Truck.dat: Speed -6..13.5, steer 12->24, AWD 4-wheel pickup, RandomHueOrGrayscale, Health 550, CarHorn_01. Jeep chassis; round headlights.
        static readonly Spec _truck = new()
        {
            Mass = 2300f,   // kerb mass, kg
            Body = "truck_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "truck_palette.png",
            GlassMesh = "truck_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 13.5f, SpeedMin = -6f, Brake = 40f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),
            ForwardGears = new[] { 20f, 14.2f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 150_000f, Health = 550f, Name = "Truck", Horn = "carhorn_01.ogg",   // 150 L (metric 1u=1mL)
            SpotPos = new[] { new Vector3(-0.979f, 0.741f, -2.511f), new Vector3(0.979f, 0.741f, -2.511f) }, OmniPos = new Vector3(0f, 0.873f, -2.487f),
            TailPos = new[] { new Vector3(-0.979f, 0.738f, 2.548f), new Vector3(0.979f, 0.738f, 2.548f) },
            SteerPivot = new Vector3(-0.465f, 1.027f, -1.384f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("truck_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("truck_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("truck_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("truck_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // Van.dat: Speed -5..14.5, steer 12->24, AWD 4-wheel van, RandomHueOrGrayscale, Health 600, CarHorn_01. Jeep chassis; round headlights.
        static readonly Spec _van = new()
        {
            Mass = 2100f,   // kerb mass, kg
            Body = "van_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "van_palette.png",
            GlassMesh = "van_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 14.5f, SpeedMin = -5f, Brake = 35f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),
            ForwardGears = new[] { 20f, 14.4f }, ReverseGear = 10f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 70_000f, Health = 600f, Name = "Van", Horn = "carhorn_01.ogg",   // 70 L (metric 1u=1mL)
            SpotPos = new[] { new Vector3(-0.979f, 0.741f, -2.511f), new Vector3(0.979f, 0.741f, -2.511f) }, OmniPos = new Vector3(0f, 0.873f, -2.487f),
            TailPos = new[] { new Vector3(-0.979f, 0.815f, 2.548f), new Vector3(0.979f, 0.815f, 2.548f) },
            SteerPivot = new Vector3(-0.465f, 1.027f, -1.523f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.40f, true), (1.30f, 0.25f, -1.40f, true), (-1.30f, 0.25f, 1.40f, false), (1.30f, 0.25f, 1.40f, false) },
            Parts = new (string, Color)[]
            {
                ("van_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("van_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("van_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("van_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        // VW_Golf.dat: Speed -6..16.5 (fast), steer 14->28, FWD 4-wheel hatch, RandomHueOrGrayscale, Health 600, CarHorn_02. Rect headlights. Curated vehicle: 256x256 Albedo_Base (alpha-0 body regions paint via the shared shader). COMMAND-ONLY (no natural PEI spawn).
        static readonly Spec _golf = new()
        {
            Mass = 400f,   // kerb mass, kg
            Body = "golf_body.txt", Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", Palette = "golf_palette.png",
            GlassMesh = "golf_glass.txt", GlassTint = new Color(0.62f, 0.73f, 0.78f, 0.26f),   // panes derived from this body by tools/gen_vehicle_glass.py
            RandomHueGray = true,
            WheelRadius = 0.6f, Engine = 600f, SteerMax = 28f, SteerMin = 14f, SpeedMax = 16.5f, SpeedMin = -6f, Brake = 32f,
            BoxSize = new Vector3(2.5f, 1.046f, 4.522f), BoxCenter = new Vector3(0f, 0.612f, 0.029f),
            ForwardGears = new[] { 14f, 8.75f }, ReverseGear = 5f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.75f, MaxVolume = 1.0f,
            Fuel = 50_000f, Health = 600f, Name = "VW_Golf", Horn = "carhorn_02.ogg",   // 50 L (metric 1u=1mL)
            SpotPos = new[] { new Vector3(-0.765f, 0.708f, -2.588f), new Vector3(0.765f, 0.708f, -2.588f) }, OmniPos = new Vector3(0f, 0.841f, -2.564f),
            TailPos = new[] { new Vector3(-0.765f, 0.787f, 2.424f), new Vector3(0.765f, 0.787f, 2.424f) },
            SteerPivot = new Vector3(-0.465f, 0.897f, -1.180f), SteerAxis = new Vector3(0f, 0.259f, 0.966f),
            Wheels = new (float, float, float, bool)[]
            { (-1.30f, 0.25f, -1.62f, true), (1.30f, 0.25f, -1.62f, true), (-1.30f, 0.25f, 1.38f, false), (1.30f, 0.25f, 1.38f, false) },
            Parts = new (string, Color)[]
            {
                ("golf_seats.txt", new Color(0.25f, 0.25f, 0.25f)),
                ("golf_steer.txt", new Color(0.28f, 0.23f, 0.14f)),
                ("golf_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),
                ("golf_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),
            },
        };

        public static Vehicle BuildJeep(int variant = 0) => Build(_jeep, variant, "jeep");
        public static Vehicle BuildQuad(int variant = 0) => Build(_quad, variant, "quad");
        public static Vehicle BuildBus(int variant = 0) => Build(_bus, variant, "bus");
        public static Vehicle BuildSedan(int variant = 0) => Build(_sedan, variant, "sedan");
        public static Vehicle BuildSemi(int variant = 0) => Build(_semi, variant, "semi");
        public static Vehicle BuildTrailer(int variant = 0) => Build(_trailer, variant, "trailer");
        public static Vehicle BuildHatchback(int variant = 0) => Build(_hatchback, variant, "hatchback");
        public static Vehicle BuildHumvee(int variant = 0) => Build(_humvee, variant, "humvee");
        public static Vehicle BuildRoadster(int variant = 0) => Build(_roadster, variant, "roadster");
        public static Vehicle BuildAmbulance(int variant = 0) => Build(_ambulance, variant, "ambulance");
        public static Vehicle BuildFiretruck(int variant = 0) => Build(_firetruck, variant, "firetruck");
        public static Vehicle BuildTractor(int variant = 0) => Build(_tractor, variant, "tractor");
        public static Vehicle BuildUral(int variant = 0) => Build(_ural, variant, "ural");
        public static Vehicle BuildPolice(int variant = 0) => Build(_police, variant, "police");
        public static Vehicle BuildOffRoader(int variant = 0) => Build(_offroader, variant, "offroader");
        public static Vehicle BuildTruck(int variant = 0) => Build(_truck, variant, "truck");
        public static Vehicle BuildVan(int variant = 0) => Build(_van, variant, "van");
        public static Vehicle BuildGolf(int variant = 0) => Build(_golf, variant, "golf");
        // Runabout motorboat (source vehicles/runabout: Model_0 hull, NO wheels, a Buoyancy/Pontoon + Rotors). WaterMode.Boat
        // -> floats on the sea + drives via water thrust/rudder (the wheel drive is inert). First aquatic vehicle.
        static readonly Spec _runabout = new()
        {
            Mass = 900f,   // kerb mass, kg
            Body = "runabout_body.txt", Water = WaterMode.Boat,
            Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", WheelRadius = 0.3f,   // unused (no wheels) but non-null for safety
            Palette = "runabout_palette.png", DefaultPaints = new[] { "#e8e8ea" },   // paintable hull (Texture_Paintable) + its fixed detail texels via PaintMat
            Engine = 600f, SteerMax = 0f, SteerMin = 0f, SpeedMax = 16f, SpeedMin = -8f, Brake = 0f,   // boat: propulsion is BoatThrust, not wheel EngineForce
            BoxSize = new Vector3(2.8f, 1.6f, 9.0f), BoxCenter = new Vector3(0f, 0.1f, -0.3f),   // hull box (mesh x±1.5, y-0.85..2.1, z-4.94..4.37)
            ForwardGears = new[] { 1f }, ReverseGear = 1f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 1.0f, MaxPitch = 2.0f, IdleVolume = 0.7f, MaxVolume = 1.0f,   // outboard motor loop
            Fuel = 500f, Health = 300f, Name = "Runabout",
            Wheels = new (float, float, float, bool)[0],   // NO wheels -- a boat floats on buoyancy
            Parts = new (string, Color)[]
            {
                ("runabout_seats.txt", new Color(0.25f, 0.25f, 0.25f)),   // 4 real cockpit seats (Objects/Seat_0..3): real material _Color = dark grey (no texture, flat -- same as jeep seats)
                ("runabout_steer.txt", new Color(0.28f, 0.23f, 0.14f)),   // real steering console/wheel (Objects/Steer): real material _Color = dark brown (matches jeep steer)
            },
        };
        public static Vehicle BuildRunabout(int variant = 0) => Build(_runabout, variant, "runabout");

        // CONTAINER SHIP -- the big Objects/Large/Vehicles/Ship_2 cargo ship, made a drivable BOAT (master 2026-08-17).
        // One hull mesh (ship_body.txt, converted from Ship_2.obj by tools/convert_ship.py: length 67.5m along Z,
        // width 22 along X, keel at y=0; bow -Z, bridge/superstructure at the stern +Z). Floats + water-drives on the
        // WaveField sea like the runabout (buoyancy is mass-normalised -- GlobalMass -- so the ship's size is fine).
        // The BOTTOM HULL is the random-colorable part (paintable palette + random paint per spawn -- next pass).
        static readonly Spec _ship = new()
        {
            Mass = 60000f,   // kerb mass, kg
            Body = "ship_body.txt", Water = WaterMode.Boat,
            Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", WheelRadius = 0.3f,   // unused (no wheels), non-null for safety
            Palette = "ship_palette.png", RandomHueGray = true,   // orange hull-BOTTOM texel (3,1) flagged paintable (alpha 0) -> random colour per spawn (master); the other texels keep the ship's own albedo
            Engine = 600f, SteerMax = 0f, SteerMin = 0f, SpeedMax = 12f, SpeedMin = -6f, Brake = 0f,   // boat: BoatThrust propels + rudder-yaws; a touch slower than the runabout (it's a SHIP)
            BoxSize = new Vector3(20f, 11f, 66f), BoxCenter = new Vector3(0f, 5.5f, 0f),   // hull collision box (mesh x±11, z±33.75, keel y0); covers the lower hull -> 4 corner buoys at the keel, COM low
            // 1:1 HULL, cut into pieces that are each genuinely convex. Every bound below is MEASURED off
            // ship_body.txt's own vertex planes, not eyeballed: the hull reaches full beam (x +-12) at y=8 and
            // tapers to a x +-10 keel; the deck is a FULL PLATE at y=11 (x +-11.5, z +-33.25); the aft
            // superstructure stands x +-9.1, z 10.4..25.75, stepping up to y=22. The single BoxShape3D this
            // replaces was x +-10 by y 0..11 -- 4 m narrower than the real beam, 1 m short of the sheer, and it
            // gave the superstructure no collision whatsoever, so the bridge was scenery you walked through.
            HullBands = new (Vector3, Vector3)[]
            {
                (new Vector3(-13f, -0.1f, -35f), new Vector3(13f, 11.001f, 35f)),      // lower hull + weather deck
            },
            // THE DECKHOUSE GETS A REAL DECOMPOSITION, not bands. Bands work on the hull because a hull is convex
            // slice by slice; the deckhouse is stepped, overhanging and hollow, and four bands over it accounted
            // for 630 of the ship's 633 invisible-wall sample points -- the lower hull and rails contributed 3.
            // strawberry, on the collider that scored 0.23 m by the old ray test: "the entire superstructure is
            // messed up", and, asked whether it was invisible walls or walk-through, "both".
            HullTrimesh = (new Vector3(-10f, 11.4f, 9f), new Vector3(10f, 23f, 27f)),
            // NOT HullTrimesh, though the machinery for it is kept below and it is the obvious next thing to try.
            // Handing the deckhouse the model's real mesh on a static child DOES work -- Godot allows a concave
            // trimesh on a static body -- but it measured WORSE on every probe I have (deck surface 11.47 against
            // the model's 11.00, bridge roof 21.44 against 22.00), and the volumetric test cannot adjudicate it at
            // all: a trimesh is a SURFACE with no interior, so "is this point inside the collider" is not a
            // question it can answer, and the walk-through count leapt 148 -> 745 purely as an artefact of asking
            // it. Shipping a collider I cannot measure, in response to a report that my measurements were wrong,
            // is the one move clearly not available here. Wants a surface-distance instrument first.
            // The BULWARK, as four boxes. It is a RING, so no single convex hull can hold it -- one spanning
            // y 11..12 fills the deck in flush with the top of the rail, which both raises the walking surface a
            // metre above the visible deck and removes the only thing stopping a parked vehicle rolling over the
            // side. Measured: deck edge at x +-11.5 / z +-33.25, outer sheer at +-12 / +-33.75, rail top y=12.
            // The deck is HULL-SHAPED in plan, not rectangular, and the rails follow it. Measured half-widths
            // along the deck plate: 3.0 m at the bow (z -33.25), opening to 11.5 by z -18.25, held to z +28.25,
            // then closing to 9.0 at the stern. Straight full-length rails at x +-11.75 were the first attempt
            // and they hang up to 8 m out over open water across the whole bow -- an invisible wall you walk into
            // where there is no ship. The two taper sections are yawed to sit on the sheer line instead.
            HullBoxes = new (Vector3, Vector3, float)[]
            {
                (new Vector3(0.5f, 1f, 46.5f), new Vector3(-11.75f, 11.5f, 5f), 0f),        // port rail, parallel body
                (new Vector3(0.5f, 1f, 46.5f), new Vector3(11.75f, 11.5f, 5f), 0f),         // starboard rail, parallel body
                (new Vector3(0.5f, 1f, 17.3f), new Vector3(-7.25f, 11.5f, -25.75f), -29.5f),// port bow taper
                (new Vector3(0.5f, 1f, 17.3f), new Vector3(7.25f, 11.5f, -25.75f), 29.5f),  // starboard bow taper
                (new Vector3(0.5f, 1f, 5.6f), new Vector3(-10.25f, 11.5f, 30.75f), 26.6f),  // port stern taper
                (new Vector3(0.5f, 1f, 5.6f), new Vector3(10.25f, 11.5f, 30.75f), -26.6f),  // starboard stern taper
                (new Vector3(6f, 1f, 0.5f), new Vector3(0f, 11.5f, -33.5f), 0f),            // stem, 6 m across at the bow
                (new Vector3(18f, 1f, 0.5f), new Vector3(0f, 11.5f, 33.5f), 0f),            // transom, 18 m across
                // BRIDGE ROOF as an explicit slab. VHACD reproduces the deckhouse WALLS well but shaves its top:
                // it put the roof at 20.64 where the model has 22.00, i.e. you would stand a metre and a half
                // inside the visible bridge. A thin plate is the one thing a concavity-driven decomposition
                // consistently under-serves, and it is cheap to just state. Measured: the top runs y 21.75..22.5
                // over z 10.5..25.5 with |x| <= 9.0.
                (new Vector3(18f, 0.7f, 15f), new Vector3(0f, 21.9f, 18f), 0f),             // bridge roof
            },
            // The weather deck: x +-11.5, z +-33.25 at y=11 (the top of the hull collision box), given 6 m of
            // headroom. Measured off the ship mesh, not guessed. The aft superstructure stands inside this box
            // and is part of the hull, so it simply never registers as a rider.
            DeckVolume = new Vector3(23f, 6f, 66.5f), DeckCenter = new Vector3(0f, 14f, 0f),
            // STERN LADDER (strawberry: "add a ladder to the back of the container ship"). Spans y 0.5 to 14.0,
            // so the foot is well below the 4.79 m waterline -- you can reach it swimming -- and the head stands
            // 3 m proud of the deck. That overshoot is deliberate: Ladder.cs documents that a player's feet stop
            // short of the ladder top, so one ending flush with the deck strands you beside the edge with nothing
            // underfoot. It sits at z=34, just aft of the transom (hull ends at 33.75), so it is climbable from
            // open water rather than buried in the plating.
            Ladders = new (Vector3, float, float)[] { (new Vector3(0f, 7.25f, 34f), 13.5f, 0f) },
            SteadyHull = true,  // she is meant to be BUILT ON (strawberry), and a hull this size never settles on
                                // her own -- 0.259 m/s of vertical motion with an empty deck, measured 18 s after
                                // spawn (vehicle.ship_orca_landing's control). Reads as life on a runabout; reads
                                // as unbuildable on a 66 m ship.
            BuoyReserve = 4f,   // RESERVE BUOYANCY -- the volume a real hull carries above its waterline, and the
                                // thing this ship had none of. Displacement comes from Mass/HullDensity, and every
                                // vehicle here masses the same GlobalMass 900, so full submersion produced exactly 2x
                                // the ship's own weight: ONE 900 kg vehicle on deck matched it and the hull sank 10.2 m
                                // in 10 s without ever finding a new equilibrium (vehicle.ship_deck_probe). At 4x it
                                // supports itself plus ~7 vehicles before the voxels saturate. BuoyLift below is
                                // re-tuned against this -- the two trade 1:1 and must move together.
            BuoyLift = 2.96f,   // keel 4.80 m under, matching the retail static Alberton reference hull Main.cs parks at that draft. MEASURED, not eyeballed: was -0.7 at 1x reserve; 4x reserve floats the hull 3.66 m higher, so this comes up by the same amount to hold the SAME draft. (Originally -3.0, which settled the keel at 2.44 m, less than HALF the draft its own comment claimed to have verified -- the visual compare that "verified" it was made against a hull sitting at 27 deg of heel (see BuoySlices), which walks the waterline up the hull side until it looks right. Draft moves 1:1 with this value (vehicle.boat_slice_sweep).
            BuoyDamp = 4f,      // settle FAST + calm -- a 67.5m hull is heavily underdamped at the source 1x (master "settles really slowly, way too buoyant")
            BuoySlices = 3,     // 27 voxels, NOT the source's 8. At 2 slices the ship capsized ITSELF: upright was an UNSTABLE equilibrium (restoring POSITIVE out to 20 deg) and it settled at 26.7 deg of heel with no input at all, sitting on a 3 m band of exactly ZERO heave stiffness -- and a hull with no waterline has no roll stiffness either, because it is the same voxels doing both. Full submersion is 2x weight, so equilibrium needs half the voxel DECKS under; on an EVEN count that lands exactly on a deck boundary, where nothing varies with either depth or heel. 3, 5 and 7 all measure clean (0 m dead band, restoring at every angle 2-60 deg); 3 wins on both axes -- strongest small-heel restoring (-0.45 rad/s2 at 5 deg vs -0.30 and -0.23) and 27 force applications a tick instead of 343. Sweep: UG_BOATSWEEP=1 ./test.sh --l1 --only 'vehicle.boat_slice_sweep'.
            TurnScale = 26f,    // 360 deg in ~28 s. Was 20 before reserve buoyancy landed: the drag compensation
                                // is close but not exact (the sqrt depth curve means submerged count does not scale
                                // perfectly as 1/reserve), which took the circle to 36 s. 26 puts it back on the
                                // number strawberry actually drove and approved rather than leaving the feel moved
                                // by a buoyancy change he did not ask to affect handling. Original note: 360 deg in 28 s at 14 m/s, against the runabout's 6 s and the 593 s this hull gets at the fleet default. strawberry asked for "ship like but usable" and this is the measured knee: scale 15 = 38 s, 20 = 28 s, 30 = 19 s, and 20 is also the last rung where the turn is nearly free (13.9 m/s held, vs 12.6 at scale 50). Sweep: UG_BOATSWEEP=1 ./test.sh --l1 --only 'vehicle.boat_turn_sweep'.
            ForwardGears = new[] { 1f }, ReverseGear = 1f, ShiftUpRpm = 5000f,
            Sound = "engine_medium.ogg", IdlePitch = 0.5f, MaxPitch = 0.95f, IdleVolume = 0.9f, MaxVolume = 1.0f,   // low ship-engine rumble
            Fuel = 5000f, Health = 4000f, Name = "Container Ship",
            Wheels = new (float, float, float, bool)[0],   // NO wheels -- floats on buoyancy
            // THE HELM, actually in the bridge. Was (0,13,26): z=26 is aft of the superstructure's own back wall
            // (it ends at 25.75) and y=13 is two metres off the deck, so the "bridge" seat was a spot hanging off
            // the stern at deck height -- strawberry asked for it "up to in the superstructure, looking down onto
            // the deck". The bridge is the top enclosed band, floor y=19.6, roof y=22, forward bulkhead z=10.4;
            // this sits on that floor at its forward end.
            Seats = new[] { new Vector3(0f, 20f, 12f) },      // driver seat (index 0), on the bridge floor
            DriverEye = new Vector3(0f, 21.2f, 11f),          // eye 1.2 m up, just inside the forward windows: 10 m
                                                              // above the deck and looking down the full 45 m of it
        };
        public static Vehicle BuildContainerShip(int variant = 0) => Build(_ship, variant, "ship");
        // APC -- 8-wheeled AMPHIBIOUS armored car (source vehicles/apc). WaterMode.Amphibious: drives on land via the
        // wheels AND floats + water-drives when its hull is in the sea. Wheels approximated (4/side) from the hull box.
        static readonly Spec _apc = new()
        {
            Mass = 13000f,   // kerb mass, kg
            Body = "apc_body.txt", Water = WaterMode.Amphibious,
            Wheel = "apc_wheel.txt", WheelTex = "jeep_wheel_albedo.png", WheelRadius = 0.74f,   // REAL APC wheel (Wheel_LOD0 ripped): radius 0.74 (was a too-small 0.55 jeep wheel); tire albedo reused
            Palette = "apc_palette.png", DefaultPaints = new[] { "#5a6650" },   // Texture_MilitaryPaintable: olive paintable hull. NO grille (strawberry) -- headlights/taillights are the real separate Parts meshes below, not palette texels
            Engine = 700f, SteerMax = 24f, SteerMin = 12f, SpeedMax = 12f, SpeedMin = -6f, Brake = 35f,
            BoxSize = new Vector3(3.6f, 1.8f, 7.7f), BoxCenter = new Vector3(0f, 0.6f, 0f),   // hull (mesh x±1.83 y-0.27..2.31 z-4..3.72)
            ForwardGears = new[] { 18f, 10f }, ReverseGear = 8f, ShiftUpRpm = 4000f,
            Sound = "engine_medium.ogg", IdlePitch = 0.9f, MaxPitch = 1.8f, IdleVolume = 0.8f, MaxVolume = 1.0f,
            Fuel = 1500f, Health = 800f, Name = "APC",
            SpotPos = new[] { new Vector3(-1.03f, 0.78f, -4.0f), new Vector3(1.03f, 0.78f, -4.0f) }, OmniPos = Vector3.Zero,   // headlight beams at the 2 lens clusters (real Headlights mesh: front z-4.06, y0.78, x-groups ±1.03)
            TailPos = new[] { new Vector3(-1.4f, 0.72f, 3.5f), new Vector3(1.4f, 0.72f, 3.5f) },   // taillight red glow at the rear lenses (real Taillights mesh: z+3.5, x±1.4)
            Wheels = new (float, float, float, bool)[]   // REAL Wheel_0..7 positions from the prefab: X±2.0, Y0.1, 4 axles (was guessed X±1.7 Y0.25)
            {
                (-2.0f, 0.1f, -2.4f, true),   (2.0f, 0.1f, -2.4f, true),    // front axle (steers)
                (-2.0f, 0.1f, -0.75f, false), (2.0f, 0.1f, -0.75f, false),
                (-2.0f, 0.1f, 0.9f, false),   (2.0f, 0.1f, 0.9f, false),
                (-2.0f, 0.1f, 2.55f, false),  (2.0f, 0.1f, 2.55f, false),    // rear
            },
            Parts = new (string, Color)[]
            {
                ("apc_headlights.txt", new Color(0.94f, 0.89f, 0.73f)),   // the 4 real headlights (Headlights_Model mesh): cream lenses. APC has NO grille -- these ARE the front detail (strawberry)
                ("apc_taillights.txt", new Color(0.56f, 0.13f, 0.13f)),   // real taillights (Taillights_Model mesh): red
            },
        };
        public static Vehicle BuildAPC(int variant = 0) => Build(_apc, variant, "apc");

        // TANK -- tracked armour (source vehicles/tank), extracted FULLY by tools/extract_tank.py: hull + crawler
        // treads + rotating turret + elevating cannon + 8 road wheels + driver/gunner seats + steering. Olive
        // MilitaryPaintable like the APC. The 8 road wheels do the physics (VehicleWheel3D); the treads are a
        // palette-painted overlay; the turret is a VEHICLE WEAPON -- BuildTankExtras hangs it + the gun on aim
        // pivots (TurretPivot/GunPivot) that tinyclaw's weapon system rotates. Differential steering (Drive) is the
        // tracked-drive pass. Rig values are tools/tank_manifest.json (already Z-negated to Godot).
        static readonly Spec _tank = new()
        {
            Mass = 40000f,   // kerb mass, kg
            Body = "tank_hull.txt", Palette = "tank_palette.png", DefaultPaints = new[] { "#5a6650" },   // Texture_MilitaryPaintable olive (same texel as the APC)
            Tracked = true,
            Treads = "tank_treads.txt",
            TurretMeshes = new[] { "tank_turret.txt", "tank_turret_1.txt" }, TurretYawPivot = new Vector3(0f, 0f, 0.85f),
            GunMesh = "tank_gun.txt", GunPitchPivot = new Vector3(0f, 2.8f, -1.15f), Muzzle = new Vector3(0f, 2.8f, -6.306f),
            Wheel = "tank_wheel.txt", WheelRadius = 0.74f,   // REAL road-wheel radius (tank_wheel.txt bbox Y+-0.74); a too-small 0.5 sat the hull LOW so the collision box scraped the ground (master). no WheelTex -> solid dark, hidden inside the treads
            Engine = 2000f, SteerMax = 0f, SteerMin = 0f, SpeedMax = 9f, SpeedMin = -4f, Brake = 48f,   // strong engine so a turn (which drags via the skid) keeps decent speed rather than crawling (master); top speed still capped at SpeedMax 9. SteerMax 0 -> tracked differential steer
            BoxSize = new Vector3(5.4f, 1.8f, 8.5f), BoxCenter = new Vector3(0f, 1.45f, 0f),   // hull collision box, tightened to the model + BELLY CUT: bottom sits at local 0.55 (above the wheel AXLES 0.556, well clear of the ground on bumps/slopes) so the box can't drag (master: "cut the belly... otherwise we backtrack on the hitbox dragging"). The 8 wheels carry the ride; this box is the UPPER hull only
            ForwardGears = new[] { 16f, 9f }, ReverseGear = 8f, ShiftUpRpm = 3500f,
            Sound = "engine_large.ogg", IdlePitch = 0.65f, MaxPitch = 1.25f, IdleVolume = 0.9f, MaxVolume = 1.0f,   // heavy diesel rumble
            Fuel = 2000f, Health = 1600f, Name = "Tank", IgnitionSound = "audio/vehicles/tank_ignition.wav",   // retail Tank.dat: its own ignition clip (ripped)
            Wheels = new (float, float, float, bool)[]   // 8 road wheels (rig, Z-negated); none STEERED (tracked)
            {
                (-2.0f, 0.556f, -3.0f, false), (2.0f, 0.556f, -3.0f, false),
                (-2.0f, 0.556f, -1.0f, false), (2.0f, 0.556f, -1.0f, false),
                (-2.0f, 0.556f,  1.0f, false), (2.0f, 0.556f,  1.0f, false),
                (-2.0f, 0.556f,  3.0f, false), (2.0f, 0.556f,  3.0f, false),
            },
            // No exterior Parts: the driver/gunner seats + steering are INTERIOR on a buttoned-up tank and clip
            // through the closed hull if drawn from outside. The meshes are extracted (content/tank_seat_driver,
            // tank_seat_gunner, tank_steer) and the seat POSITIONS live in the rig -> wire them into the FP/interior
            // view (and tinyclaw's seat system) later; the exterior stays a clean closed hull.
            Parts = new (string, Color)[] { },
        };
        public static Vehicle BuildTank(int variant = 0) => Build(_tank, variant, "tank");

        /// <summary>Tank-only meshes on top of the shared Build (hull/wheels/seats/collision): the palette-painted
        /// crawler treads (a static overlay on the hull), the rotating turret on its yaw pivot, and the elevating
        /// cannon on its pitch pivot (a CHILD of the turret so it yaws with it). All three share the hull's
        /// MilitaryPaintable material. The pivots are exposed as TurretPivot/GunPivot for the vehicle-weapon system
        /// to aim; at rest they sit at the extracted rig positions and the gun points forward.</summary>
        static void BuildTankExtras(Vehicle v, Spec s, Material bodyMat)
        {
            if (s.Treads != null)   // treads: DARK track steel. The tread mesh shares the hull's MilitaryPaintable, but its
                // UVs land on the palette's fixed texels (which come out red/white in the hull palette), not the paintable
                // one -- and real tank tracks are dark steel regardless. Solid dark reads right; the real track texture +
                // a UV-scroll by track speed are a later pass. Baked at the hull origin -> a plain root-relative overlay.
                v.AddChild(new MeshInstance3D { Name = "Treads", Mesh = ContentProvider.ParseObj($"res://content/{s.Treads}"), MaterialOverride = SolidMat(new Color(0.14f, 0.14f, 0.15f)) });
            if (s.TurretMeshes != null)
            {
                v.TurretPivot = new Node3D { Name = "TurretYaw", Position = s.TurretYawPivot };   // yaws about local Y (weapon system)
                foreach (var t in s.TurretMeshes)   // baked centred on the yaw pivot -> local 0
                    v.TurretPivot.AddChild(new MeshInstance3D { Name = t.Replace(".txt", ""), Mesh = ContentProvider.ParseObj($"res://content/{t}"), MaterialOverride = bodyMat });
                v.AddChild(v.TurretPivot);
                if (s.GunMesh != null)
                {
                    // gun pivot is a CHILD of the turret so it yaws with it; offset by the pivot delta, then the gun
                    // mesh (baked centred on the pitch pivot) sits at local 0 and elevates about local X.
                    v.GunPivot = new Node3D { Name = "GunPitch", Position = s.GunPitchPivot - s.TurretYawPivot };
                    v.GunPivot.AddChild(new MeshInstance3D { Name = "tank_gun", Mesh = ContentProvider.ParseObj($"res://content/{s.GunMesh}"), MaterialOverride = bodyMat });
                    v.TurretPivot.AddChild(v.GunPivot);
                }
            }
        }

        // MINICOPTER -- Rust-style two-seat rotary wing (VoX 2026-08-15). No ripped mesh exists, so the
        // airframe is procedural (BuildHeliModel) and the numbers are chosen for feel rather than ported from
        // a .dat, since there is no source .dat to port.
        //
        // HeliThrust 17 against g=9.8 is a thrust-to-weight of ~1.7, so hover sits near 58 % collective and
        // there is real climb left above it -- enough to feel powered, short of the "hold W and leave the
        // map" that a 3:1 ratio gives. SpeedMax 26 m/s is what the MP envelope's HORIZONTAL cap is derived
        // from (VehicleReplication: SpeedMax x dt x 1.25), so it has to be an honest top speed or a legitimate
        // fast pass gets rolled back as a cheat. Climb/fall caps are declared for the same reason -- the
        // shared car defaults (12.5 up / 25 down) would recov a pilot out of any real dive.
        static readonly Spec _minicopter = new()
        {
            Heli = true, Frame = HeliFrame.Ultralight,
            // Thrust cut TWICE on playtest feedback: 17 -> 13.6 (strawberry, "reduce the upward thrust by like
            // 20%") -> 11.8 (VoX, "less thrust from W"). 11.8 against g leaves thrust-to-weight at 1.20 and
            // hover at ~83 % collective, so there is still real climb available but you have to commit to it.
            HeliThrust = 11.8f, HeliPitchTorque = 2.08f, HeliRollTorque = 2.40f, HeliYawTorque = 1.76f, HeliLevel = 0f,
            HeliClimbMax = 22f, HeliFallMax = 45f,
            RotorRadius = 2.85f, TailRotorRadius = 0.34f,
            RotorHub = new Vector3(0f, 1.22f, 0.55f), TailRotorHub = new Vector3(0.09f, 0.02f, 2.46f),
            Body = null, Palette = null, DefaultPaints = new[] { "#8a7f5c" },   // bare weathered tube frame
            Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", WheelRadius = 0.3f,   // unused (no wheels), non-null for safety like the runabout
            // 20, not 26: once the fleet was balanced against real aircraft, a scrap ultralight out-running a Huey
            // and a Skycrane read wrong. This is the one figure NOT derived from a real machine -- its analogue
            // (a Mosquito-class ultralight, ~100 km/h) would scale to 10 m/s and be miserable to fly. Gamified
            // instead, and placed below the whole fleet, which is the relationship that matters.
            Engine = 0f, SteerMax = 0f, SteerMin = 0f, SpeedMax = 20f, SpeedMin = 0f, Brake = 0f,
            BoxSize = new Vector3(1.05f, 0.80f, 1.60f), BoxCenter = new Vector3(0f, 0.05f, 0.20f),   // seats + engine bay
            ExtraBoxes = new (Vector3, Vector3)[]
            {
                (new Vector3(1.85f, 0.22f, 0.30f), new Vector3(0f, -0.46f, -1.05f)),   // front axle -- what it sits on
                (new Vector3(0.24f, 0.24f, 2.60f), new Vector3(0f, -0.30f, 1.30f)),    // keel aft section + tail
                (new Vector3(0.20f, 0.30f, 0.20f), new Vector3(0f, -0.50f, 2.35f)),    // tail wheel
            },
            ForwardGears = new[] { 1f }, ReverseGear = 1f, ShiftUpRpm = 5000f,
            Sound = "heli_engine.ogg", IgnitionSound = "heli_ignition.ogg", IdlePitch = 0.85f, MaxPitch = 1.35f, IdleVolume = 0.7f, MaxVolume = 1.0f,
            Fuel = 200f, Health = 250f, Name = "Minicopter", Rarity = EItemRarity.RARE,
            Wheels = new (float, float, float, bool)[0],   // the wheels are scenery -- it flies, it does not drive
        };
        public static Vehicle BuildMinicopter(int variant = 0) => Build(_minicopter, variant, "minicopter");

        // SCOUTCOPTER -- the enclosed pod-and-skids machine that was the first cut of the minicopter. Kept as its
        // own spec rather than edited away: VoX asked for the Rust-accurate ultralight ("basically a frame with a
        // steat") and strawberry liked this one ("noo i love the model. its like the gta vice city RC heli"), and
        // both fit -- the flight model, controls and net path are shared, so a second airframe costs one Spec.
        static readonly Spec _scoutcopter = new()
        {
            Heli = true, Frame = HeliFrame.Pod,
            HeliThrust = 11.8f, HeliPitchTorque = 2.08f, HeliRollTorque = 2.40f, HeliYawTorque = 1.76f, HeliLevel = 0f,
            HeliClimbMax = 22f, HeliFallMax = 45f,
            RotorRadius = 2.65f, TailRotorRadius = 0.42f,
            RotorHub = new Vector3(0f, 1.12f, 0.20f), TailRotorHub = new Vector3(0.10f, 0.62f, 3.02f),
            Body = null, Palette = null, DefaultPaints = new[] { "#d9d24b" },
            Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", WheelRadius = 0.3f,
            Engine = 0f, SteerMax = 0f, SteerMin = 0f, SpeedMax = 26f, SpeedMin = 0f, Brake = 0f,
            BoxSize = new Vector3(1.15f, 1.05f, 2.05f), BoxCenter = new Vector3(0f, 0.12f, 0.15f),
            ExtraBoxes = new (Vector3, Vector3)[]
            {
                (new Vector3(0.16f, 0.16f, 2.30f), new Vector3(-0.52f, -0.72f, 0.10f)),   // left skid
                (new Vector3(0.16f, 0.16f, 2.30f), new Vector3( 0.52f, -0.72f, 0.10f)),   // right skid
                (new Vector3(0.22f, 0.22f, 2.70f), new Vector3(0f, 0.34f, 1.85f)),        // tail boom
            },
            ForwardGears = new[] { 1f }, ReverseGear = 1f, ShiftUpRpm = 5000f,
            Sound = "heli_engine.ogg", IgnitionSound = "heli_ignition.ogg", IdlePitch = 0.9f, MaxPitch = 1.45f, IdleVolume = 0.7f, MaxVolume = 1.0f,
            Fuel = 200f, Health = 250f, Name = "Scoutcopter", Rarity = EItemRarity.RARE,
            Wheels = new (float, float, float, bool)[0],
        };
        public static Vehicle BuildScoutcopter(int variant = 0) => Build(_scoutcopter, variant, "scoutcopter");

        // HUEY -- the RETAIL helicopter (VoX 2026-08-15: "do both, make a huey varient and model a new
        // minicopter varient"). Unlike the minicopter this one has a real source .dat, so its numbers are
        // ported rather than invented: Bundles/Vehicles/Huey/Huey.dat gives Speed_Max 16, Speed_Min -2,
        // Fuel 2000, Health 1000, Rarity Epic, Engine Helicopter, and the four Coalition/Desert/Forest/Russia
        // DefaultPaintColors. Its Steer_Min/Max (16/8) describe wheel steering it does not have, so they stay 0.
        //
        // The airframe is the extracted retail mesh (11.20 m long, 3.50 wide, 4.78 tall -- measured, not
        // guessed) and is therefore SPEC'd, not procedural: only the rotor mounts differ from a normal vehicle.
        // It flies the same model as the minicopter but heavier: less thrust-to-weight, slower roll rate, and
        // a stronger levelling term, so it handles like a loaded transport instead of a lawn chair.
        static readonly Spec _huey = new()
        {
            Heli = true,
            // The Huey takes a SMALLER cut than the minicopter's 20 %. strawberry was flying the minicopter, and
            // 20 % off 13.5 leaves thrust-to-weight at 1.10 -- hover at 91 % collective, with almost nothing left
            // to climb on once the new tilt penalty takes its bite. 12.0 keeps it heavy (T/W 1.22, hover ~82 %)
            // without making a loaded transport unable to get out of its own way.
            HeliThrust = 12.9f, HeliPitchTorque = 1.12f, HeliRollTorque = 1.32f, HeliYawTorque = 1.03f, HeliLevel = 0f,
            HeliClimbMax = 18f, HeliFallMax = 40f,
            RotorRadius = 5.57f, TailRotorRadius = 1.28f,        // the mesh's own spans -- no scaling for this one
            RotorHub = new Vector3(0f, 3.01f, -0.25f), TailRotorHub = new Vector3(-0.55f, 3.57f, 6.68f),   // prefab local positions, Z negated. Tail hub X was -0.45: the post is on the STARBOARD side (measured +0.55), so the hub sat in empty air off the port boom -- see the note on _hind
            HeliBodyMeshes = new[] { "huey_body.txt", "huey_body_1.txt" },
            Turrets = HueyDoorGuns(),   // door gunners, port + starboard
            Parts = HeliParts("huey"),   // same three as the rest of the fleet, despite this spec predating HeliBase
            Body = null, Palette = "huey_palette.png",   // MilitaryPaintable; see the note in HeliBase
            DefaultPaints = new[] { "#475e83", "#a69884", "#437c44", "#495631" },   // .dat DefaultPaintColors
            Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", WheelRadius = 0.3f,   // unused (no wheels)
            Engine = 0f, SteerMax = 0f, SteerMin = 0f, SpeedMax = 23f, SpeedMin = 0f, Brake = 0f,   // .dat says 16, but the fleet is balanced on the real UH-1's 222 km/h -- see the table above
            BoxSize = new Vector3(2.40f, 2.10f, 5.20f), BoxCenter = new Vector3(0f, 0.75f, 0.30f),   // cabin; boom/skids are ExtraBoxes
            ExtraBoxes = new (Vector3, Vector3)[]
            {
                (new Vector3(0.30f, 0.30f, 3.60f), new Vector3(-1.15f, -0.42f, 0.30f)),   // left skid
                (new Vector3(0.30f, 0.30f, 3.60f), new Vector3( 1.15f, -0.42f, 0.30f)),   // right skid
                (new Vector3(0.45f, 0.60f, 4.60f), new Vector3(0f, 1.30f, 4.10f)),        // tail boom
            },
            ForwardGears = new[] { 1f }, ReverseGear = 1f, ShiftUpRpm = 5000f,
            Sound = "heli_engine.ogg", IgnitionSound = "heli_ignition.ogg", IdlePitch = 0.7f, MaxPitch = 1.15f, IdleVolume = 0.8f, MaxVolume = 1.0f,
            Fuel = 2000f, Health = 1000f, Name = "Huey", Rarity = EItemRarity.EPIC,   // .dat Fuel/Health/Rarity
            Wheels = new (float, float, float, bool)[0],
        };
        public static Vehicle BuildHuey(int variant = 0) => Build(_huey, variant, "huey");

        // ---- THE REST OF THE RETAIL HELICOPTER FLEET -------------------------------------------------
        // Meshes extracted by cow tools (tools/extract_heli.py, a generalisation of extract_huey.py); every
        // number below is PORTED from the vehicle's own .dat rather than invented, because unlike the
        // minicopter these all have a source entry: Speed_Max, Speed_Min, Fuel, Health, Rarity, Explosion.
        //
        // MANOEUVRABILITY CUT 20 % ACROSS THE WHOLE FLEET (strawberry 2026-08-16: "nerf the maneuverabilty of
        // all helis by like 20%"). Applied uniformly, so the ORDERING below is untouched -- the balance tests
        // pin relative agility, not absolute numbers, and a flat scalar is exactly the change they should
        // survive. The minicopter and scoutcopter took the same cut for consistency.
        //
        // BALANCED AGAINST THE REAL AIRCRAFT (strawberry 2026-08-16: "hind is an mi24. orca is a ka-60.
        // skycrane is an S-64 skycrane. hummingbird is a littlebird. huey is a huey. balance all around
        // these"). The .dat speeds are useless for this -- everything is 16, or 18 for a Hummingbird -- so the
        // fleet's character comes from the machines they actually are:
        //
        //   aircraft              top speed   MTOW      climb     -> game speed / thrust / roll
        //   Mi-24 Hind            330 km/h    11,500kg  12.5 m/s      34   14.2   1.01
        //   Ka-60 Kasatka (Orca)  300         6,500     10.4          31   13.4   1.34
        //   MD500 (Hummingbird)   282         1,610     10.5          29   13.5   2.70
        //   UH-1 Huey             222         4,300      8.9          23   12.9   1.65
        //   S-64 Skycrane         213         21,000     6.8          22   12.2   0.74
        //
        // Two of these inverted what I had guessed, which is the point of looking them up. THE HIND IS THE
        // FASTEST of the five, not the slowest -- it is heavy AND fast, which is the whole idea of a gunship;
        // I had tuned it as the lumbering one. And the SKYCRANE CLIMBS WORST despite being the lift machine,
        // because what it is lifting is mostly itself.
        //
        // Speeds keep the real RATIOS, scaled into a 22-34 m/s band that is flyable on this map -- the numbers
        // are gamified, the GAPS are real. Measured: every entry lands within 0.6 % of its real-world ratio to
        // the Hind (0.909 -> 0.912, 0.855 -> 0.853, 0.673 -> 0.676, 0.645 -> 0.647). Thrust is
        // derived, not chosen: terminal climb in this model is (thrust - g) / LinearDamp, so thrust = 9.8 +
        // 0.35 x the real climb rate. Roll authority goes as 1/sqrt(MTOW), normalised to the Little Bird.
        // MTOW throughout -- mixing empty and max weights would have flipped Skycrane and Hind against each
        // other, since the Skycrane is lighter than the Hind empty and twice it loaded.
        //
        // DETAIL PARTS (meshes by cow tools, 0f8719c1). Every airframe in the fleet carries the same three,
        // extracted under the same `<heli>_<part>.txt` convention, so they are DERIVED from the mesh name
        // rather than typed out five times -- five hand-written arrays is five chances to paste `orca_seats`
        // into the Skycrane and never notice, since a wrong-but-present seat mesh renders perfectly happily.
        //
        // The colours are the extractor's per-type DEFAULTS, not ported values, and that distinction matters:
        // the retail parts read a white `_Color` because they are palette-driven, so there is nothing to port
        // until the palette pass samples the real texels. Identical across the fleet on purpose -- a Hind's
        // seats being greyer than a Huey's would be invention, not fidelity.
        static (string, Color)[] HeliParts(string mesh, params (string, Color)[] extra)
        {
            var std = new (string, Color)[]
            {
                ($"{mesh}_seats.txt", new Color(0.12f, 0.12f, 0.13f)),        // dark cabin seats
                ($"{mesh}_steer.txt", new Color(0.08f, 0.08f, 0.09f)),        // collective/cyclic sticks, near-black
                ($"{mesh}_taillights.txt", new Color(0.80f, 0.10f, 0.10f)),   // red lenses
            };
            if (extra == null || extra.Length == 0) return std;
            var all = new (string, Color)[std.Length + extra.Length];
            std.CopyTo(all, 0);
            extra.CopyTo(all, std.Length);
            return all;
        }

        // LANDING GEAR (2026-08-16). Every airframe except the Huey shipped with its cabin box as its ONLY
        // collider, and a cabin box whose floor sits above the aircraft's own belly: the Hind's was 0.58 m up,
        // the Hummingbird's 0.83 m. So a parked helicopter sank until its underside was inside the terrain,
        // which is how the Hind's chin turret came to be invisible -- it was on the aircraft the whole time,
        // correctly placed, and entirely below the ground line.
        //
        // The footprints below are MEASURED rather than eyeballed. For the skid aircraft that means the body
        // mesh's own lowest vertices; for the Hind and Orca it means their WHEEL meshes, which is a correction:
        // the first pass measured their bodies and got the belly, because the wheels were not in the game at
        // all (never extracted -- see tools/extract_heli_wheels.py). Those two sat 0.3 m too high the moment the
        // wheels appeared under them. A number measured off the geometry you have is still the wrong number
        // when the geometry is incomplete. The Huey already had skid boxes and is left alone.
        static (Vector3, Vector3)[] Skids(float halfX, float width, float bottom, float zFrom, float zTo, float h = 0.30f)
        {
            float zc = (zFrom + zTo) * 0.5f, len = zTo - zFrom;
            var size = new Vector3(width, h, len);
            return new (Vector3, Vector3)[]
            {
                (size, new Vector3(-halfX, bottom + h * 0.5f, zc)),
                (size, new Vector3( halfX, bottom + h * 0.5f, zc)),
            };
        }

        // HeliBase carries everything the fleet shares, so each entry below is only what makes it itself.
        static Spec HeliBase(string mesh, float thrust, float pitchTq, float rollTq, float yawTq,
                             float rotorR, float tailR, Vector3 mainHub, Vector3 tailHub,
                             Vector3 box, Vector3 boxCentre, (Vector3, Vector3)[] gear,
                             float speedMax, float fuel, float health,
                             string name, EItemRarity rarity, params (string, Color)[] extraParts) => new()
        {
            Heli = true,
            Parts = HeliParts(mesh, extraParts),
            ExtraBoxes = gear,
            HeliThrust = thrust, HeliPitchTorque = pitchTq, HeliRollTorque = rollTq, HeliYawTorque = yawTq,
            HeliLevel = 0f,   // attitude is state on every airframe; nothing self-levels (VoX)
            HeliClimbMax = 20f, HeliFallMax = 42f,
            RotorRadius = rotorR, TailRotorRadius = tailR,
            RotorHub = mainHub, TailRotorHub = tailHub,
            HeliBodyMeshes = new[] { $"{mesh}_body.txt", $"{mesh}_body_1.txt" },
            HeliRotorMeshPrefix = mesh,
            // Body stays null on purpose: the airframe geometry comes from HeliBodyMeshes, and the shared
            // `Body` field would build a SECOND fuselage on top of it. Only the palette is shared with the
            // car path -- BuildHeliModel already paints through the same bodyMat, so naming the palette is
            // the whole of what "colour the bodies" needs (meshes re-ripped with UVs by cow tools 5cd4e772).
            Body = null, Palette = $"{mesh}_palette.png",
            DefaultPaints = new[] { "#475e83", "#a69884", "#437c44", "#495631" },   // the shared faction paints
            Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", WheelRadius = 0.3f,
            Engine = 0f, SteerMax = 0f, SteerMin = 0f, SpeedMax = speedMax, SpeedMin = 0f, Brake = 0f,
            BoxSize = box, BoxCenter = boxCentre,
            ForwardGears = new[] { 1f }, ReverseGear = 1f, ShiftUpRpm = 5000f,
            Sound = "heli_engine.ogg", IgnitionSound = "heli_ignition.ogg",
            IdlePitch = 0.7f, MaxPitch = 1.15f, IdleVolume = 0.8f, MaxVolume = 1.0f,
            Fuel = fuel, Health = health, Name = name, Rarity = rarity,
            Wheels = new (float, float, float, bool)[0],
        };

        // Declared BEFORE _hind on purpose. Static field initialisers run in DECLARATION order, so with this
        // below the spec it was still null when _hind's initialiser read it -- the turret silently became "no
        // turrets" and the Hind built without a mount, with nothing anywhere reporting a problem.
        /// <summary>A PAIR of door guns, port and starboard (strawberry: "one on each side, they have a 120 deg
        /// cone and will try to point at least one side at you when agro'd").
        ///
        /// The cone is expressed as ASYMMETRIC YAW LIMITS rather than a new concept: yaw +90 points the barrel at
        /// -X (port) and -90 at +X (starboard), measured in vehicle.npc_heli_turret, so a 90 deg beam cone is
        /// simply [45,135] and [-135,-45]. AimTurret already clamps to those, which means a door gunner
        /// physically cannot swing across its own cabin and shoot the crew on the other side.
        ///
        /// Each mount declares a GunnerAt, so a killable body is built behind it. That is the whole point: these
        /// are people leaning out of a doorway, not the Hind's remote chin turret.</summary>
        static TurretDef[] DoorGuns(string gunId, string gunMesh, float halfWidth, float gunY, float floorY, float z)
        {
            return new[]
            {
                new TurretDef
                {
                    Seat = 1,   // PORT
                    PitchMesh = gunMesh,
                    Pivot = new Vector3(-halfWidth, gunY, z),
                    GunnerAt = new Vector3(-halfWidth, floorY, z),
                    Muzzle = new Vector3(0f, 0f, -0.90f),
                    MeshRotationDeg = new Vector3(-90f, 0f, 0f),   // lay the Y-axis gun model down the barrel line
                    YawMin = 45f, YawMax = 135f,      // 90 deg centred on the port beam (strawberry tightened it from 120)
                    PitchMin = -70f, PitchMax = 20f,  // a door gun leans out and shoots well below itself
                    GunId = gunId,
                },
                new TurretDef
                {
                    Seat = 2,   // STARBOARD
                    PitchMesh = gunMesh,
                    Pivot = new Vector3(halfWidth, gunY, z),
                    GunnerAt = new Vector3(halfWidth, floorY, z),
                    Muzzle = new Vector3(0f, 0f, -0.90f),
                    MeshRotationDeg = new Vector3(-90f, 0f, 0f),
                    YawMin = -135f, YawMax = -45f,
                    PitchMin = -70f, PitchMax = 20f,
                    GunId = gunId,
                },
            };
        }

        // CALLED, NOT STORED IN A STATIC FIELD. These began as `static readonly TurretDef[] HueyDoorGuns = ...`
        // and the Huey silently had no turrets at all: C# runs static field initializers in TEXTUAL ORDER, _huey
        // is declared ABOVE them, so it read null -- and `s.Turrets ?? Array.Empty` downstream turned that null
        // into "this airframe has no mounts" without a word. The Orca worked purely because it happens to be
        // declared lower in the file. A static method has no such ordering, so the spec table cannot be broken by
        // where someone chooses to put a helper.
        //
        // GEOMETRY IS MEASURED OFF THE MESHES, not the collision box -- the box is the hull envelope and put the
        // crew 0.38 m under the floor on the first render. Cabin floor from *_seats.txt (huey +0.08, orca +0.03),
        // and the fore-aft station from the NAV LIGHT, which is where strawberry asked for them: "align
        // horizontally based off where the green light is". BuildNavLights places those at the taillight mesh's
        // own AABB centre, so the station is huey Z -0.258 and orca Z -0.221 rather than anything chosen here.
        static TurretDef[] HueyDoorGuns() => DoorGuns("dragonfang", "dragonfang_gun.txt", 1.05f, 1.15f, 0.08f, -0.26f);
        static TurretDef[] OrcaDoorGuns() => DoorGuns("nykorev", "nykorev_gun.txt", 1.15f, 1.10f, 0.03f, -0.22f);

        static readonly TurretDef[] HindTurret =
        {
            // tools/extract_turret.py --vehicle hind. Turret_1 -> seat 1, which is the nose gunner seat the seat
            // extraction found independently; muzzle is Aim+Barrel composed, in the pitch frame.
            new TurretDef
            {
                Seat = 1,
                YawMesh = "hind_turret_yaw.txt", PitchMesh = "hind_turret_pitch.txt",
                Pivot = new Vector3(0f, 0.064f, -4.378f),
                Muzzle = new Vector3(0.229f, -0.2f, -2.6f),   // Aim(-0.275,-0.2,-0.4) + Barrel(0.504,0,-2.2)
                YawMin = -120f, YawMax = 120f,   // a chin turret cannot shoot through its own airframe
                PitchMin = -60f, PitchMax = 15f, // mostly DOWNWARD: it is a ground-attack gun slung under the nose
                // THE HMG, not the Nykorev (strawberry: "uses the HMG weapon from the source"). Retail HMG.dat is
                // ID 1394 and carries the `Turret` flag, i.e. the game itself marks it as the mount gun: Range 250,
                // Firerate 7, Spread_Angle_Degrees 1.43. Its HMG_50 magazine is EXPLOSIVE .50 (Player_Damage 30,
                // Vehicle_Damage 40), which is why the AI's added inaccuracy below is load-bearing rather than
                // flavour -- an accurate one of these would delete a player instantly.
                GunId = "hmg",
            },
        };
        // HIND -- the gunship, and the FASTEST thing in the fleet as well as the second heaviest. Fast and
        // unwieldy: it will outrun anything and hates changing its mind.
        static readonly Spec _hind = WithBeaconLens(WithTurrets(HeliBase("hind", 14.2f, 0.69f, 0.81f, 0.63f, 5.90f, 1.25f,
            new Vector3(0f, 4.18f, 0.58f), new Vector3(0.57f, 4.46f, 9.60f),   // tail hub on the RIGHT: the boom carries a horizontal mounting post whose end face is 16 verts at X +0.57, Y 4.46, Z 9.60 -- the old -0.30 had the right height and station but the mirrored side, so the rotor hung in clear air with the post sticking out opposite it (strawberry)
            new Vector3(2.90f, 2.60f, 7.20f), new Vector3(0f, 1.40f, 0.20f),
            new (Vector3, Vector3)[]   // REAL gear, measured off hind_wheels.txt: twin nose wheels forward, mains aft
            {
                (new Vector3(0.24f, 0.20f, 0.62f), new Vector3(-0.21f, -0.68f, -3.15f)),
                (new Vector3(0.24f, 0.20f, 0.62f), new Vector3( 0.21f, -0.68f, -3.15f)),
                (new Vector3(0.24f, 0.20f, 0.62f), new Vector3(-1.50f, -0.52f,  1.82f)),
                (new Vector3(0.24f, 0.20f, 0.62f), new Vector3( 1.50f, -0.52f,  1.82f)),
            },
            34f, 1750f, 1250f, "Hind", EItemRarity.LEGENDARY,
            // NOT hind_turret.txt any more -- that merged lump is replaced by the articulated yaw/pitch pair
            // built from Spec.Turrets below. Leaving it here would draw a second, permanently-forward turret
            // clipping through the one that aims.
            ("hind_wheels.txt", new Color(0.09f, 0.09f, 0.10f))), HindTurret), "skycrane_taillights.txt");   // 4 landing wheels -- tyre black

        /// <summary>Attach turret mounts to a spec built by HeliBase, which has no parameter for them.</summary>
        static Spec WithTurrets(Spec s, TurretDef[] t) { s.Turrets = t; return s; }
        static Spec WithBeaconLens(Spec s, string file) { s.BeaconLensFrom = file; return s; }

        public static Vehicle BuildHind(int variant = 0) => Build(_hind, variant, "hind");

        // ORCA (Ka-60) -- the modern transport. Nearly Hind-fast and noticeably more agile; the all-rounder.
        // Tail radius 0.72, not the fleet's shared 1.25: the ORCA is the one airframe with a DUCTED tail
        // (a fenestron), and the duct is real geometry -- 256 verts ring its hub in a band at 0.75-1.25 m
        // with nothing beyond, while every other airframe just has scattered boom geometry there. A 1.25 m
        // rotor is the duct's own outer rim, so the blades were sweeping THROUGH the housing. 0.72 clears
        // the inner wall. (strawberry: "the orca tail needs to be shrunk to fit its enclosure")
        static readonly Spec _orca = WithTurrets(HeliBase("orca", 13.4f, 0.91f, 1.07f, 0.84f, 5.90f, 0.72f,
            new Vector3(0f, 3.28f, -0.25f), new Vector3(-0.30f, 1.48f, 7.55f),
            new Vector3(2.60f, 2.50f, 6.40f), new Vector3(0f, 1.20f, 0.10f),
            new (Vector3, Vector3)[]   // REAL gear, measured off orca_wheels.txt: mains forward, twin tail wheels aft
            {
                (new Vector3(0.24f, 0.20f, 0.76f), new Vector3(-1.71f, -0.75f, -1.89f)),
                (new Vector3(0.24f, 0.20f, 0.76f), new Vector3( 1.71f, -0.75f, -1.89f)),
                (new Vector3(0.24f, 0.20f, 0.76f), new Vector3(-0.21f, -0.86f,  3.11f)),
                (new Vector3(0.24f, 0.20f, 0.76f), new Vector3( 0.21f, -0.86f,  3.11f)),
            },
            31f, 2000f, 1000f, "Orca", EItemRarity.EPIC,
            ("orca_wheels.txt", new Color(0.09f, 0.09f, 0.10f))), OrcaDoorGuns());    // 4 landing wheels -- tyre black
        public static Vehicle BuildOrca(int variant = 0) => Build(_orca, variant, "orca");

        // SKYCRANE (S-64) -- the heavy lifter, and counter-intuitively the WORST climber and slowest of the
        // five, because at 21 t what it mostly lifts is itself. Least agile by a wide margin.
        // BUFFED (strawberry 2026-08-17: "buff the skycrane because it is really slow"). It was both the slowest
        // airframe in the fleet by a wide margin (22 vs 29-34) AND the least powerful (12.2 vs 13.4-14.2), which is
        // backwards for the one machine whose entire purpose is heavy lift. Thrust 12.2 -> 16.5 makes it the
        // strongest, taking spare capacity from 2160 N (~220 kg) to 6030 N (~615 kg) so it can actually carry
        // something; Speed_Max 22 -> 28 keeps it the slowest, which is right for a crane, without being painful.
        // Terminal climb becomes (16.5-9.8)/0.45 = 14.9 m/s, still under the 20 m/s HeliClimbMax cap.
        static readonly Spec _skycrane = HeliBase("skycrane", 16.5f, 0.50f, 0.59f, 0.46f, 5.90f, 1.25f,
            new Vector3(0f, 3.01f, -1.21f), new Vector3(0.55f, 3.55f, 7.71f),   // tail hub X -0.45 -> +0.55: the post is starboard
            new Vector3(3.20f, 2.80f, 6.80f), new Vector3(0f, 1.30f, 0.30f),
            Skids(2.065f, 0.60f, -0.63f, -4.15f, 2.73f, 0.20f),   // the S-64's tall splayed legs, measured
            28f, 2000f, 900f, "Skycrane", EItemRarity.EPIC);
        // The sky-crane is the ONLY airframe with the winch: the whole point of the real S-64 is that it has no cargo
        // hold, just a spine and a hook. 9 m of cable clears the 0.63 m gear with room for a tall load to swing.
        static readonly Spec _skycraneRigged = WithSling(_skycrane, 9.0f, new Vector3(0f, 1.88f, 0.00f));
        // MEASURED off the mesh, not inferred from the collision Skids(). The visible gear runs Z -3.05..2.73 and is
        // dominated by the MAIN posts at Z 0.5..2.5 (438 verts, centroid +1.70), with a small forward cluster at -3.0.
        // My first attempt used the collision skid span's centre (-0.71), which is on the OPPOSITE side of the origin
        // from where the posts actually are -- strawberry, immediately: "thats the opposite direction." The collision
        // boxes and the visible legs are simply not the same geometry, and only one of them is what a player sees.
        static readonly Vector3 _skycraneSlingVisualLocal = new Vector3(0f, 1.88f, 1.70f);
        public static Vehicle BuildSkycrane(int variant = 0) => Build(_skycraneRigged, variant, "skycrane");
        // Anchor is the MEASURED belly over the load footprint (local Y 1.88 -- the sky-crane's whole shape is a high
        // spine on tall legs, so the winch head sits 2.5 m above the skid bottoms and the cable drops between them).
        //
        // X AND Z MUST BE THE CENTRE OF MASS (both 0 here). The cable force is applied as a POSITIONED force, so an
        // anchor offset from the CoM turns a hanging load into a constant pitching moment: the airframe tips, its
        // thrust vector tilts off vertical, and it descends however much collective is in. At Z=+1.0 a 40 kg magnet
        // -- 392 N, under a fifth of the spare thrust -- took the sky-crane from +4.1 m/s of climb to -21 m/s of
        // descent. That is not a weight problem and no amount of trimming the magnet's mass would have fixed it.
        // Directly under the CoM the moment arm is parallel to a vertical cable, so the torque is zero when it hangs
        // straight and appears only as the load swings -- which is the behaviour we actually want. Real sling hooks
        // are rigged at the CoM for exactly this reason.
        static Spec WithSling(Spec b, float cable, Vector3 anchor) { b.SlingHook = true; b.SlingCable = cable; b.SlingAnchor = anchor; return b; }

        // The VISUAL start of the cable, distinct from the FORCE anchor above (strawberry: "move the heli side rope
        // anchor point to be in line with the leg posts"). Tried moving the real one there first: the leg posts sit
        // at local Z -0.71 (their span's centre, Skids(...,-4.15,2.73)), 0.71 m off the CoM axis, and that reintroduced
        // the exact pitching-moment bug from before at reduced scale -- measured -11.76 m/s of descent instead of a
        // climb. So the FORCE still applies at the CoM (SlingAnchor, Z=0, torque-free), and only where the cable is
        // DRAWN moves to line up with the gear. A small, deliberate lie in the render -- the pull doesn't really come
        // from where the rope appears to leave the hull -- and I said so rather than let the picture imply otherwise.

        // HUMMINGBIRD (MD500 Little Bird) -- the scout. A tenth of the Hind's weight, so far and away the
        // sharpest controls in the fleet, and the thinnest hull. The three retail variants share one geometry.
        static readonly Spec _hummingbird = HeliBase("hummingbird", 13.5f, 1.84f, 2.16f, 1.68f, 5.57f, 1.25f,
            new Vector3(0f, 3.01f, -0.25f), new Vector3(0.55f, 3.45f, 6.95f),   // tail hub X -0.45 -> +0.55: the post is starboard
            new Vector3(2.00f, 2.10f, 4.60f), new Vector3(0f, 1.00f, 0.10f),
            Skids(1.125f, 0.30f, -0.88f, -3.25f, 1.75f),   // classic skids, same shape as the Huey's, measured
            29f, 1750f, 750f, "Hummingbird", EItemRarity.EPIC);
        public static Vehicle BuildHummingbird(int variant = 0) => Build(_hummingbird, variant, "hummingbird");

        // OTTER -- retail's light FLOATPLANE (Bundles/Vehicles/Otter: Engine Plane, Lift 5, Speed 24). Fixed-wing,
        // bank-to-turn (master): forward prop thrust + airspeed lift along body-UP. Floats on its pontoons via the
        // boat buoyancy for a WATER takeoff (throttle up on the water, lift builds, she lifts off). Body meshes +
        // propeller extracted from the vehicle prefab (otter_body{,_1}.txt, otter_prop{,_disc}.txt).
        static readonly Spec _otter = new()
        {
            Plane = true, HeliBodyMeshes = new[] { "otter_body.txt", "otter_body_1.txt" },
            PropHub = new Vector3(0f, 1.29f, -3.95f), PropMeshPrefix = "otter",   // prop pivot at the nose (-Z); spins about body forward
            PlaneThrust = 9f, PlaneLift = 10f, PlaneTargetSpeed = 16f,            // T/W ~0.9 (a peppy bush plane; snappy takeoff run but NOT a rocket). Lift scales with ANGLE OF ATTACK (see StepPlane): PlaneLift is the lift authority -- tuned so it trims to level at a few deg nose-up around cruise, and rotates off the water ~14 m/s with a bit of back-stick. Floats plane easily thanks to the reduced plane water-drag
            PlanePitchTorque = 2.4f, PlaneRollTorque = 2.6f, PlaneYawTorque = 0.9f, PlaneSteerFade = 0.45f,   // roll snappiest (bank-to-turn), rudder gentlest; pitch firm enough to ROTATE the nose up against the pontoons' righting on takeoff
            Palette = "otter_body_tex.png", DefaultPaints = new[] { "#e0c42c" },  // the real 2x2 atlas is a PALETTE: texel (0,0) is alpha-0 PAINTABLE (the fuselage -> spawn paint), the other 3 greys are fixed (floats/struts/frames). Source .dat DefaultPaintColors = #e0c42c (the classic bush-plane yellow-gold); PaintableSections -> repaintable in-game
            Water = WaterMode.Boat, BuoyLift = -0.5f, BuoyDamp = 3f,              // float on the pontoons + settle; water takeoff
            BoxSize = new Vector3(2.6f, 1.2f, 7.6f), BoxCenter = new Vector3(0f, 0.1f, 0f),   // pontoon/hull footprint -> buoyancy voxels
            SpeedMax = 28f, Engine = 600f, SteerMax = 0f, SteerMin = 0f, Brake = 0f,   // cap ABOVE target so there's cruise room; pilot pitch-trims altitude
            Wheel = "jeep_wheel.txt", WheelTex = "jeep_wheel_albedo.png", WheelRadius = 0.3f, Wheels = new (float, float, float, bool)[0],
            ForwardGears = new[] { 1f }, ReverseGear = 1f, ShiftUpRpm = 5000f,
            Sound = "engine_plane.ogg", IgnitionSound = "otter_ignition.ogg", IdlePitch = 0.9f, MaxPitch = 1.9f, IdleVolume = 0.8f, MaxVolume = 1.0f,   // the REAL shared prop-plane engine loop + the Otter's own ignition
            Fuel = 1750f, Health = 800f, Name = "Otter",
            DriverEye = new Vector3(0f, 1.5f, 1.0f),
        };
        public static Vehicle BuildOtter(int variant = 0) => Build(_otter, variant, "otter");

        // FIGHTER JET -- retail's fast military jet (ID 140, Engine Plane, Speed_Max 36, Air_Steer 32-64). A WHEELED
        // LAND plane: it takes off from a runway on its tricycle gear. The VehicleWheel3D wheels (built from Wheels
        // below) give passive ground support + rolling while flying, and are car-driven in Ctrl ground mode. NO
        // propeller -> thrust is the (jet) engine; PropMeshPrefix null skips the prop. Fast + agile vs the Otter.
        static readonly Spec _fighterjet = new()
        {
            Plane = true, HeliBodyMeshes = new[] { "fighterjet_body.txt" },   // Model_0 (LOD0) ONLY -- Model_1 is the coincident LOD1 (a closed low-poly shell that CAPS the open cockpit); co-rendering both hid the cockpit interior
            PropMeshPrefix = null,                                                // JET: no propeller
            BurnerPos = new[] { new Vector3(-0.39f, 0.99f, 5.32f), new Vector3(0.39f, 0.99f, 5.32f) },   // the 2 rear engine exhausts (prefab Burner_0/1, Godot Z-neg) -> afterburner flames shoot aft (+Z)
            ContrailPos = new[] { new Vector3(-4.5f, 0.85f, 3.75f), new Vector3(4.5f, 0.85f, 3.75f), new Vector3(-1.25f, 3.05f, 4.5f), new Vector3(1.25f, 3.05f, 4.5f) },   // 4 emitters: 2 wingtip trailing edges + 2 vertical-winglet (tail-fin) tips
            PlaneThrust = 16f, PlaneLift = 11f, PlaneTargetSpeed = 28f,           // strong thrust; rotates ~24 m/s, cruises fast
            PlanePitchTorque = 2.8f, PlaneRollTorque = 3.8f, PlaneYawTorque = 1.1f, PlaneSteerFade = 0.55f,   // agile (Air_Steer 64) -- snappier roll/pitch than the otter
            Water = WaterMode.Car,                                               // LAND plane: no buoyancy; rests + rolls on its wheels
            BoxSize = new Vector3(2.4f, 1.0f, 8.0f), BoxCenter = new Vector3(0f, 1.25f, -0.3f),   // UPPER-fuselage collision box, RAISED well clear of the wheels/ground (bottom ~0.75 above the origin) + shortened so it never pokes the nose -- the low/long box was clipping the terrain + freaking out (master). The GEAR (VehicleWheel3D) carries the ground ride.
            SpeedMax = 36f, Engine = 800f, SteerMax = 32f, SteerMin = 8f, Brake = 30f,         // Steer_Max/Min for GROUND-mode taxi; Speed_Max 36
            Wheel = "fighterjet_wheel.txt", WheelTex = "fighterjet_wheel_albedo.png", WheelRadius = 0.34f,   // the jet's OWN wheel mesh (prefab Wheel_*/Model_0, 168v) not the jeep car wheel
            GlassMesh = "fighterjet_canopy.txt",   // the LOD's closed cockpit cap, re-laid TRANSLUCENT over the open cockpit
            MissileMesh = "fighterjet_missiles.txt",   // the 4 wing missiles carved into their own DARKER-GREY mesh (master 2026-08-18)
            SteerMesh = "fighterjet_joystick.txt",   // cockpit control stick (source Objects/Steer)
            RetractGear = true,                    // wheels retract up into the fuselage when flying
            Wheels = new (float, float, float, bool)[]   // tricycle gear (Godot Z = -Unity Z): nose steers, 2 wide mains
            {
                (0f, -0.27f, -2.83f, true),      // nose wheel (forward) -- steers on the ground
                (-0.85f, -0.27f, 2.00f, false),  // main gear L (F-15: on the FUSELAGE, not the wings -> clears the wing missiles on retract; master 2026-08-18)
                (0.85f, -0.27f, 2.00f, false),   // main gear R
            },
            ForwardGears = new[] { 24f }, ReverseGear = 8f, ShiftUpRpm = 5000f,
            Sound = "fighterjet_engine.ogg", IgnitionSound = "fighterjet_ignition.ogg", IdlePitch = 0.9f, MaxPitch = 1.7f, IdleVolume = 0.85f, MaxVolume = 1.0f,   // the REAL dedicated jet engine + ignition (from the prefab)
            Palette = "fighter_jet_body_tex.png", DefaultPaints = new[] { "#bcbcbc" },   // real .dat DefaultPaintColors = military grey; paintable panels + fixed tan/grey details
            Fuel = 1000f, Health = 800f, Name = "Fighter Jet",
            Seats = new[] { new Vector3(0f, 0.05f, -4.053f) },   // verbatim retail Seat_0 (fighter_jet, tools/vehicle_seats.json) -- the hand-placed guess (master 2026-08-18) sat the pilot 0.5m too high
            DriverEye = new Vector3(0f, 1.58f, -4.50f),   // FP eye in the cockpit, under the canopy, looking out the windscreen (master 2026-08-18)
        };
        public static Vehicle BuildFighterJet(int variant = 0) => Build(_fighterjet, variant, "fighterjet");
        public static Vehicle BuildByName(string name, int variant = 0) => name switch { "quad" => BuildQuad(variant), "bus" => BuildBus(variant), "sedan" => BuildSedan(variant), "hatchback" => BuildHatchback(variant), "humvee" => BuildHumvee(variant), "roadster" => BuildRoadster(variant), "ambulance" => BuildAmbulance(variant), "firetruck" => BuildFiretruck(variant), "tractor" => BuildTractor(variant), "ural" => BuildUral(variant), "police" => BuildPolice(variant), "semi" => BuildSemi(variant), "trailer" => BuildTrailer(variant), "offroader" => BuildOffRoader(variant), "off_roader" => BuildOffRoader(variant), "truck" => BuildTruck(variant), "van" => BuildVan(variant), "golf" => BuildGolf(variant), "vw_golf" => BuildGolf(variant), "runabout" => BuildRunabout(variant), "apc" => BuildAPC(variant), "minicopter" => BuildMinicopter(variant), "mini" => BuildMinicopter(variant), "heli" => BuildMinicopter(variant), "huey" => BuildHuey(variant), "scoutcopter" => BuildScoutcopter(variant), "scout" => BuildScoutcopter(variant), "hind" => BuildHind(variant), "orca" => BuildOrca(variant), "skycrane" => BuildSkycrane(variant), "hummingbird" => BuildHummingbird(variant), "bird" => BuildHummingbird(variant), "tank" => BuildTank(variant), "ship" => BuildContainerShip(variant), "containership" => BuildContainerShip(variant), "otter" => BuildOtter(variant), "plane" => BuildOtter(variant), "fighterjet" => BuildFighterJet(variant), "jet" => BuildFighterJet(variant), _ => BuildJeep(variant) };
        public static readonly string[] SpecNames = { "jeep", "quad", "bus", "sedan", "hatchback", "humvee", "roadster", "ambulance", "firetruck", "tractor", "ural", "police", "semi", "trailer", "offroader", "truck", "van", "golf", "runabout", "apc", "minicopter", "huey", "scoutcopter", "hind", "orca", "skycrane", "hummingbird", "tank", "ship", "otter", "fighterjet", "jet" };   // F1 dev-console autocomplete + validation ("golf" = VW_Golf, command-only, no natural spawn; runabout = boat + apc = amphibious, both command-spawnable -- drop over water to float)

        /// <summary>The spec's main body BoxCollider (the hull Build() adds as the primary CollisionShape3D)
        /// for a spec key -- the hitbox debug overlay reconstructs the server's vehicle collider from a
        /// replicated TypeId through this. Unknown names fall back to the jeep, same as SpecFor.</summary>
        public static void GetBodyBox(string name, out Vector3 size, out Vector3 center)
        {
            var s = SpecFor(name);
            size = s.BoxSize; center = s.BoxCenter;
        }

        // spec lookup by key (same table as BuildByName) -- the MP puppet builder resolves replicated
        // TypeIds through this so client replicas rebuild the exact meshes/palette the server spawned
        static Spec SpecFor(string name) => name switch
        {
            "quad" => _quad, "bus" => _bus, "sedan" => _sedan, "hatchback" => _hatchback, "humvee" => _humvee,
            "roadster" => _roadster, "ambulance" => _ambulance, "firetruck" => _firetruck, "tractor" => _tractor,
            "ural" => _ural, "police" => _police, "semi" => _semi, "trailer" => _trailer,
            "offroader" => _offroader, "off_roader" => _offroader, "truck" => _truck, "van" => _van,
            "golf" => _golf, "vw_golf" => _golf, "tank" => _tank,
            // The heli fleet, the APC and the runabout were missing here while being present in SpecNames and in
            // BuildByName, so the MP puppet path resolved all nine to _jeep and built a jeep-shaped replica --
            // silently, because _jeep builds perfectly. WorldBuilder spawns three real runabouts on the PEI coast
            // every map load, so this was live without anyone touching a console: jeeps floating on the sea for
            // every client not driving one, with jeep dimensions and a jeep-sized look-focus hull. The tank was
            // added when it merged; the helicopters never were. Keep this switch in step with BuildByName --
            // SpecNames is the list both must cover. Review 2026-08-16.
            "runabout" => _runabout, "apc" => _apc,
            "minicopter" => _minicopter, "mini" => _minicopter, "heli" => _minicopter,
            "huey" => _huey, "scoutcopter" => _scoutcopter, "scout" => _scoutcopter,
            "hind" => _hind, "orca" => _orca, "skycrane" => _skycrane,
            "hummingbird" => _hummingbird, "bird" => _hummingbird,
            _ => _jeep,
        };

        // MP §3.6 client replica: a mesh-only PUPPET -- the same ripped body/palette/parts/wheels as
        // Build(), but NO VehicleBody3D/VehicleWheel3D/collision/audio/particles. Puppets are interpolated
        // visuals (VehicleReplicaView dead-reckons them); the server owns the physics. Wheel pivots are
        // exposed on the returned node for steer/spin dressing.
        const float WheelRestDrop = 0.25f;   // = the real VehicleWheel3D WheelRestLength (~line 1044): suspension rest drop below the axle mount

        public static VehiclePuppet BuildPuppetByName(string name, int variant)
        {
            var s = SpecFor(name);
            var pSeatLocals = s.Seats ?? (SeatTable.TryGetValue(name, out var pst) ? pst : new[] { SeatOf(s.Name) });
            var p = new VehiclePuppet { SpecKey = name, SeatOffset = HandTunedSeatOf(s.Name) ? SeatOf(s.Name) : pSeatLocals[0] + GenericSeatRise };
            // Opt the puppet OUT of Godot's global physics interpolation (project.godot physics_interpolation=true), like
            // the PlayerController shell does (PlayerController.cs:1674). VehicleReplicaView repositions the puppet every
            // _Process frame with its OWN manual glide/dead-reckoning; leaving Godot interp ON renders the puppet at its
            // stale physics-frame transform instead -> a DRIVEN car's mesh freezes at its old spot while its data position
            // (and colliders) drive off, which no headless test can see (there's no render). This is the live drive freeze.
            p.PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off;
            if (s.DriverEye != Vector3.Zero) p.DriverEyeLocal = s.DriverEye;   // tall-cab override, same rule as Build()
            var paint = SpawnPaint(s, variant);   // deterministic from the replicated variant -> same look as the server spawn
            Material bodyMat = s.Palette != null
                ? PaintMat(s.Palette, paint)
                : new StandardMaterial3D { AlbedoColor = paint, Metallic = 0f, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            // SPLIT THE LENSES OUT, exactly as Build() does. The puppet used to load the body WHOLE, so its
            // headlights and taillights were baked into the paintwork and could never emit -- which is why a
            // remote car drove around dark no matter what its driver did. Same zones, same X-mirror, so the
            // lens geometry a puppet lights is the same geometry the real car lights.
            ArrayMesh pBody = null, pHl = null, pTl = null;
            var pTlZones = s.TaillightZoneMin != s.TaillightZoneMax
                ? new[] { (s.TaillightZoneMin, s.TaillightZoneMax),
                          (new Vector3(-s.TaillightZoneMax.X, s.TaillightZoneMin.Y, s.TaillightZoneMin.Z), new Vector3(-s.TaillightZoneMin.X, s.TaillightZoneMax.Y, s.TaillightZoneMax.Z)) }
                : null;
            if (s.HeadlightZoneMin != s.HeadlightZoneMax)
            {
                var lz = (s.HeadlightZoneMin, s.HeadlightZoneMax);
                var rz = (new Vector3(-s.HeadlightZoneMax.X, s.HeadlightZoneMin.Y, s.HeadlightZoneMin.Z), new Vector3(-s.HeadlightZoneMin.X, s.HeadlightZoneMax.Y, s.HeadlightZoneMax.Z));
                (pBody, pHl) = ContentProvider.ParseObjSplitByZone($"res://content/{s.Body}", new[] { lz, rz });
            }
            else if (pTlZones != null)
                (pBody, pTl) = ContentProvider.ParseObjSplitByZone($"res://content/{s.Body}", pTlZones);
            else
                pBody = ContentProvider.ParseObj($"res://content/{s.Body}");
            p.AddChild(new MeshInstance3D { Name = "Body", Mesh = pBody, MaterialOverride = bodyMat });
            if (pHl != null)
            {
                var m = new StandardMaterial3D { AlbedoColor = new Color(0.94f, 0.89f, 0.73f), Metallic = 0f, Roughness = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                p.AddChild(new MeshInstance3D { Name = "PuppetHeadlights", Mesh = pHl, MaterialOverride = m });
                p.HeadlightMat = m;   // tint matches the real vehicle default (Vehicle._lampTint)
            }
            if (pTl != null)
            {
                var m = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.06f, 0.06f), Metallic = 0f, Roughness = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                p.AddChild(new MeshInstance3D { Name = "PuppetTaillights", Mesh = pTl, MaterialOverride = m });
                p.TaillightMat = m;
            }
            if (s.Parts != null)
                foreach (var (txt, color) in s.Parts)
                {
                    var mi = new MeshInstance3D { Mesh = ContentProvider.ParseObj($"res://content/{txt}"), MaterialOverride = SolidMat(color) };
                    if (txt.Contains("steer") && s.SteerAxis != Vector3.Zero)   // wrap the steering wheel in a pivot so DressWheels can turn it (#38) -- mirrors Build()'s Parts loop
                    {
                        p.SteerPivot = new Node3D { Position = s.SteerPivot };
                        mi.Position = -s.SteerPivot;   // baked world verts render in place once the pivot sits at the centre
                        p.SteerPivot.AddChild(mi);
                        p.AddChild(p.SteerPivot);
                        p.SteerAxis = s.SteerAxis.Normalized();
                    }
                    else p.AddChild(mi);
                }
            if (s.SteerModel != null && s.SteerAxis != Vector3.Zero)   // dedicated ripped steering wheel (semi) -- mirrors Build()'s SteerModel block (#38)
            {
                var sMesh = ContentProvider.ParseObj($"res://content/{s.SteerModel}");
                p.SteerPivot = new Node3D { Position = s.SteerPivot };
                p.SteerAxis = s.SteerAxis.Normalized();
                p.SteerPivot.AddChild(new MeshInstance3D { Mesh = sMesh, MaterialOverride = SolidMat(new Color(0.13f, 0.11f, 0.08f)), Position = -sMesh.GetAabb().GetCenter() });
                p.AddChild(p.SteerPivot);
            }
            var wheelMesh = ContentProvider.ParseObj($"res://content/{s.Wheel}");
            Material wheelMat;
            if (s.WheelTex != null)
            {
                wheelMat = new StandardMaterial3D { AlbedoTexture = ContentProvider.TextureCached(ProjectSettings.GlobalizePath($"res://content/{s.WheelTex}")), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            }
            else
                wheelMat = new StandardMaterial3D { AlbedoColor = new Color(0.09f, 0.09f, 0.10f), Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            p.Wheels = new VehiclePuppet.WheelDress[s.Wheels.Length];
            for (int i = 0; i < s.Wheels.Length; i++)
            {
                var (x, y, z, steer) = s.Wheels[i];
                float wr = s.WheelRadii != null ? s.WheelRadii[i] : s.WheelRadius;
                float wscale = wr / s.WheelRadius;
                // drop the wheel by the suspension rest length: the real VehicleWheel3D (WheelRestLength=0.25, Vehicle.cs
                // ~1044) hangs the wheel this far below the axle mount at rest, so the body sits at ride height with wheels
                // ON the ground. The puppet has no suspension -> place the wheel where the real one rests, else it floats.
                var pivot = new Node3D { Position = new Vector3(x, y - WheelRestDrop, z) };
                pivot.AddChild(new MeshInstance3D { Mesh = wheelMesh, MaterialOverride = wheelMat, Scale = new Vector3((x < 0 ? -1f : 1f) * wscale, wscale, wscale) });
                p.AddChild(pivot);
                p.Wheels[i] = new VehiclePuppet.WheelDress { Pivot = pivot, Steer = steer, Radius = wr };
            }
            // A car alarm you cannot hear is not an alarm. Same clip, same 3D falloff as the real vehicle's.
            if (s.Horn != null)
            {
                var hogg = ContentProvider.OggCached(ProjectSettings.GlobalizePath($"res://content/{s.Horn}"), loop: false);   // shared decoded stream (was a decode per vehicle)
                if (hogg != null)
                {
                    p.HornAudio = new AudioStreamPlayer3D { Stream = hogg, UnitSize = 12f, MaxDistance = 90f, VolumeDb = 4f };
                    p.AddChild(p.HornAudio);
                }
            }
            // A6 rope-tow attach nodes: the IDENTICAL formula the real Build uses (lines ~1143-1147) so the
            // client's cosmetic rope hangs off the same bumper-height spots the host's physics rope does.
            float towFrontZ = s.BoxCenter.Z - s.BoxSize.Z * 0.5f - 0.15f;
            float towRearZ = s.BoxCenter.Z + s.BoxSize.Z * 0.5f + 0.15f;
            float towY = s.BoxCenter.Y - s.BoxSize.Y * 0.30f;
            p.FrontTowLocal = new Vector3(s.BoxCenter.X, towY, towFrontZ);
            p.RearTowLocal = new Vector3(s.BoxCenter.X, towY, towRearZ);

            p.OutlineColor = ItemTool.RarityColorUI(s.Rarity);   // match the real vehicle's look-at rim colour (line 931)
            p.SetNameLabel(s.Name, p.OutlineColor);              // look-at name tag (hidden until focused), like the real Vehicle's InfoBillboard title
            // The puppet's hull (client-only): a StaticBody3D box on the SAME layers the real Vehicle body carries --
            // HitMeshBit (bit 15 -- the layer NO vehicle masks, same as HitMesh/GlassHit below) + bit 5 (the look-ray,
            // bullets and the tow scan probe). CollisionMask 0: it is placed, never pushed.
            //
            // Until 2026-09-02 this was bit 5 ALONE, by design ("detection only, it never blocks movement") -- and that
            // design was the bug strawberry reported as "no collisions for vehicles on the server". On a --connect client
            // EVERY vehicle except the one you drive is this puppet (the Client world build never calls
            // SpawnPeiVehicles; only VehicleReplicaView materializes cars), the shell's CollisionMask is bit0|bit6
            // (PlayerController._Ready), and bit0|bit6 against bit5 is zero: players walked through every car, and the
            // locally driven Vehicle (mask bit0|bit8) drove through every parked one. Singleplayer never saw it because
            // its cars are real VehicleBody3Ds carrying bit 0. The server was innocent: under client-authoritative
            // position it adopts the client's claim inside a speed envelope and never re-solves geometry, so "can I
            // walk into a car" is decided entirely by what the CLIENT's world contains -- which was a ghost.
            // Bit 0 makes the puppet what the real car is to the world: solid to the shell, to the local car's wheels,
            // and to the LOS/bullet rays -- parity with SP. It is still repositioned per render frame (a StaticBody3D
            // teleport), so a moving remote car BLOCKS a standing player rather than shoving them; that is the
            // remaining gap, not this one.
            var focusBody = new StaticBody3D { CollisionLayer = HitMeshBit | (1u << 5), CollisionMask = 0 };
            focusBody.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = s.BoxSize }, Position = s.BoxCenter });
            p.AddChild(focusBody);
            return p;
        }

        /// <summary>The minicopter airframe, built from primitives. Sets <c>v._bodyMesh</c> to the fuselage pod
        /// (so the shared damage/hide paths still have a body to work on) and hangs the rest off the vehicle.
        ///
        /// The two rotors go on their OWN Node3D pivots -- `_rotorNode` spins about local Y, `_tailRotorNode`
        /// about local X -- because the blades have to turn without the airframe turning. Blade phase is
        /// visual only; the flight model never reads it.</summary>
        /// <summary>Parse a content .obj if it is present, else null. Generated content (the extracted Huey
        /// rotors) must not be a hard build dependency -- a checkout that has not run the extractor should
        /// still get a flyable machine, not a crash or an invisible rotor.</summary>
        /// <summary>Window panes, one node each so they break independently.
        ///
        /// Lives here rather than in BuildPlaneModel because a car never goes through the plane builder --
        /// setting Spec.GlassMesh on the sedan silently did nothing until this moved out (tinyclaw 2026-08-30:
        /// caught by rendering it opaque red as a positive control and seeing 94 changed pixels, all HUD).
        ///
        /// The mesh is split per pane by tools/gen_vehicle_glass.py into `<base>_<label>.txt`. A vehicle whose
        /// generator found no side apertures simply has fewer files; the loader skips what is absent, so a spec
        /// can name GlassMesh without every pane existing.</summary>
        // Mid panes APPENDED, not inserted in front-to-back order: the index into this array is the pane
        // id used by ResolveHitGlass, the break/repair calls and the mechanics-panel rows, so inserting
        // would silently renumber every existing vehicle's panes. A long body (bus, and any future coach or
        // articulated rig) derives more than one side aperture per flank; without these four labels the
        // generator's l_mid1/r_mid1/l_mid2/r_mid2 files were written to disk and then never loaded.
        public static readonly string[] GlassPaneLabels = { "windshield", "rear", "l_front", "r_front", "l_rear", "r_rear",
                                                            "l_mid1", "r_mid1", "l_mid2", "r_mid2",
                                                            "l_mid3", "r_mid3", "l_mid4", "r_mid4",
                                                            // ROOF GLASS (strawberry 2026-09-03, offroader: a skylight over the front
                                                            // seats and a glazed rear ceiling meeting the trunk glass). `roof` is the
                                                            // single-aperture case; front/rear when a cross-member splits it in two.
                                                            "roof", "roof_front", "roof_rear" };
        public static string GlassPaneDisplay(string label) => label switch
        {
            "windshield" => "windscreen", "rear" => "rear window",
            "l_front" => "left front", "r_front" => "right front",
            "l_rear" => "left rear",  "r_rear" => "right rear",
            "l_mid1" => "left mid 1", "r_mid1" => "right mid 1",
            "l_mid2" => "left mid 2", "r_mid2" => "right mid 2",
            "l_mid3" => "left mid 3", "r_mid3" => "right mid 3",
            "l_mid4" => "left mid 4", "r_mid4" => "right mid 4",
            "roof" => "roof glass", "roof_front" => "skylight", "roof_rear" => "rear ceiling", _ => label,
        };

        readonly System.Collections.Generic.List<MeshInstance3D> _glassNodes = new();
        readonly System.Collections.Generic.List<StaticBody3D> _glassBodies = new();   // parallel to _glassNodes when MeshHitbox is on, empty otherwise
        readonly System.Collections.Generic.List<string> _glassLabels = new();
        bool[] _glassBroken = System.Array.Empty<bool>();

        public int GlassCount => _glassNodes.Count;
        public string GlassLabel(int i) => (uint)i < (uint)_glassLabels.Count ? _glassLabels[i] : "";
        public bool IsGlassBroken(int i) => (uint)i < (uint)_glassBroken.Length && _glassBroken[i];
        public int GlassBrokenCount { get { int n = 0; foreach (var b in _glassBroken) if (b) n++; return n; } }

        /// <summary>Break a pane. Deliberately does NOT self-heal: the pane stays gone until RepairGlass, which
        /// is what strawberry asked for ("doesnt respawn unless 'fixed' in the vehicle mechanics ui").</summary>
        /// <summary>Put pane `i`'s collider in or out of the player/bullet layers. The LAYER, not the shape's
        /// Disabled flag, because this is called from inside a physics callback (a bullet resolving its own hit)
        /// where toggling a shape has to be deferred; writing a layer does not. A no-op when the panes have no
        /// colliders at all, which is every vehicle with the mesh hitbox off.</summary>
        void SetPaneSolid(int i, bool solid)
        {
            if ((uint)i >= (uint)_glassBodies.Count) return;
            var b = _glassBodies[i];
            if (GodotObject.IsInstanceValid(b)) b.CollisionLayer = solid ? HitMeshBit | (1u << 5) : 0u;
        }

        public bool BreakGlass(int i)
        {
            if ((uint)i >= (uint)_glassNodes.Count || _glassBroken[i]) return false;
            _glassBroken[i] = true;
            if (GodotObject.IsInstanceValid(_glassNodes[i])) _glassNodes[i].Visible = false;
            SetPaneSolid(i, false);   // a broken window is a hole you can shoot and climb through, not an invisible pane
            return true;
        }

        public bool RepairGlass(int i)
        {
            if ((uint)i >= (uint)_glassNodes.Count || !_glassBroken[i]) return false;
            _glassBroken[i] = false;
            if (GodotObject.IsInstanceValid(_glassNodes[i])) _glassNodes[i].Visible = true;
            SetPaneSolid(i, true);
            return true;
        }

        /// <summary>Which INTACT pane a world-space hit landed on, or -1. Same shape as ResolveHitPart: the
        /// bullet path already has the impact point, so no extra colliders are needed on the glass (a collider
        /// per pane would join every physics query the car takes part in).</summary>
        // ---- SHOOTABLE LAMPS (strawberry 2026-09-01: "shoot out headlights and tail lights. they simply
        // stay off when broken, can be repaired from the mechanics ui like the windows can.")
        // Modelled per SIDE, not per fixture, so one lucky round does not kill both headlights. The lens
        // mesh ships as ONE mesh covering both lamps, so it is split by vertex x at build time into a left
        // and a right MeshInstance3D with their own materials -- a shared material cannot glow on one side
        // and not the other.
        readonly System.Collections.Generic.List<MeshInstance3D> _lampNodes = new();   // the lens half
        readonly System.Collections.Generic.List<StandardMaterial3D> _lampMats = new();
        readonly System.Collections.Generic.List<Node3D> _lampLights = new();          // the emitter to hide
        readonly System.Collections.Generic.List<string> _lampLabels = new();
        bool[] _lampBroken = System.Array.Empty<bool>();

        public static readonly string[] LampLabels = { "headlight_l", "headlight_r", "taillight_l", "taillight_r", "lightbar_l", "lightbar_r", "lightbar_c" };
        public static string LampDisplay(string label) => label switch
        {
            "headlight_l" => "left headlight",  "headlight_r" => "right headlight",
            "taillight_l" => "left taillight",  "taillight_r" => "right taillight",
            "lightbar_l" => "lightbar red lens", "lightbar_r" => "lightbar blue lens", "lightbar_c" => "lightbar centre", _ => label,
        };
        public int LampCount => _lampNodes.Count;
        public string LampLabel(int i) => (uint)i < (uint)_lampLabels.Count ? _lampLabels[i] : "";
        public bool IsLampBroken(int i) => (uint)i < (uint)_lampBroken.Length && _lampBroken[i];
        public int LampBrokenCount { get { int n = 0; foreach (var b in _lampBroken) if (b) n++; return n; } }
        public bool IsHeadlightSideBroken(bool left)
        {
            for (int i = 0; i < _lampLabels.Count; i++)
                if (_lampLabels[i] == (left ? "headlight_l" : "headlight_r")) return _lampBroken[i];
            return false;
        }

        /// <summary>Shoot a lamp out. No self-heal -- it stays dead until RepairLamp, same contract as glass.</summary>
        public bool BreakLamp(int i)
        {
            if ((uint)i >= (uint)_lampNodes.Count || _lampBroken[i]) return false;
            _lampBroken[i] = true;
            ApplyLampState();
            return true;
        }

        public bool RepairLamp(int i)
        {
            if ((uint)i >= (uint)_lampNodes.Count || !_lampBroken[i]) return false;
            _lampBroken[i] = false;
            ApplyLampState();
            return true;
        }

        /// <summary>Re-apply every lamp's visual from its broken flag AND the current on/off state. Called on
        /// break, on repair, and from SetHeadlights/SetTaillights -- so a lamp shot out while the lights are
        /// OFF still stays dark when they are switched on, which is the whole point of the feature.</summary>
        /// <summary>Brake flare / running glow on EVERY tail lamp: the single _taillightMat (the first/left half of a split
        /// lens, or the shared box material) plus every _lampMats entry labelled taillight_*. A shot-out lamp stays dark.</summary>
        void SetTailFlare(bool braking)
        {
            float e = braking ? 6f : 2f;
            if (_taillightMat != null) _taillightMat.EmissionEnergyMultiplier = e;
            for (int i = 0; i < _lampMats.Count; i++)
                if (_lampMats[i] != null && _lampLabels[i].StartsWith("taillight") && !_lampBroken[i]) _lampMats[i].EmissionEnergyMultiplier = e;
        }
        void ApplyLampState()
        {
            for (int i = 0; i < _lampNodes.Count; i++)
            {
                bool dead = _lampBroken[i];
                if (_lampLabels[i].StartsWith("lightbar"))   // the flash block drives these; here only the shot-out look
                {
                    if (dead && _lampMats[i] != null) { _lampMats[i].EmissionEnabled = false; _lampMats[i].AlbedoColor = new Color(0.12f, 0.12f, 0.12f); }
                    if (dead && GodotObject.IsInstanceValid(_lampLights[i])) _lampLights[i].Visible = false;
                    continue;
                }
                bool head = _lampLabels[i].StartsWith("headlight");
                bool lit = !dead && (head ? _headlightsOn : _taillightsOn);
                if (GodotObject.IsInstanceValid(_lampLights[i])) _lampLights[i].Visible = lit;
                if (_lampMats[i] != null)
                {
                    _lampMats[i].EmissionEnabled = lit;
                    if (lit)
                    {
                        _lampMats[i].Emission = head ? _lampTint : new Color(0.56f, 0.13f, 0.13f);
                        _lampMats[i].EmissionEnergyMultiplier = 2f;
                    }
                    // A shot-out lens reads as broken even in daylight, when nothing is emitting anyway.
                    _lampMats[i].AlbedoColor = dead
                        ? new Color(0.12f, 0.12f, 0.12f)
                        : (head ? new Color(0.94f, 0.89f, 0.73f) : new Color(0.42f, 0.06f, 0.06f));
                }
            }
            // The beam is ONE volume merged from both lenses (BuildHeadlightBeam), so it cannot be half of
            // itself. With either headlight out, hide it and let the surviving SpotLight3D light the road --
            // a full-width shaft leaving a dead lamp is a worse lie than no shaft.
            if (_headlightBeam != null)
                _headlightBeam.Visible = _headlightsOn && !IsHeadlightSideBroken(true) && !IsHeadlightSideBroken(false);
            if (_headlightFill != null)
                _headlightFill.Visible = _headlightsOn && !(IsHeadlightSideBroken(true) && IsHeadlightSideBroken(false));
        }

        /// <summary>Nearest lamp lens to a world point, or -1. Same no-collider approach as ResolveHitGlass:
        /// lamps are tiny, so a generous tolerance is what makes them hittable at all.</summary>
        public int ResolveHitLamp(Vector3 world, float tol = 0.42f)
        {
            int best = -1; float bestD = tol * tol;
            for (int i = 0; i < _lampNodes.Count; i++)
            {
                if (_lampBroken[i] || !GodotObject.IsInstanceValid(_lampNodes[i])) continue;
                var mi = _lampNodes[i];
                var c = mi.GlobalTransform * mi.GetAabb().GetCenter();
                float d = c.DistanceSquaredTo(world);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // ---- SHOOTABLE TIRES (strawberry 2026-09-01: "shoot tires, pops the actual tire part of the wheel
        // model, leaving the rim, driving when missing tire(s) affects handling, causes sparks from the
        // damaged wheel when driving on it. can be replaced by the mechanics ui.")
        readonly System.Collections.Generic.List<MeshInstance3D> _tireNodes = new();   // outer ring only
        bool[] _tirePopped = System.Array.Empty<bool>();
        float[] _tireFricRef = System.Array.Empty<float>();   // stock grip, to restore on replace
        float[] _tireRadRef = System.Array.Empty<float>();
        CpuParticles3D[] _tireSparks;

        public int TireCount => _tireNodes.Count;
        public bool IsTirePopped(int i) => (uint)i < (uint)_tirePopped.Length && _tirePopped[i];
        public int TirePoppedCount { get { int n = 0; foreach (var b in _tirePopped) if (b) n++; return n; } }
        public static string TireDisplay(int i, int n)
        {
            if (n < 4) return $"wheel {i + 1}";
            bool front = i < 2;   // wheel order is authored front pair first on every road spec
            return (i % 2 == 0 ? "left " : "right ") + (front ? "front tire" : "rear tire");
        }

        /// <summary>Blow a tire off. The rim stays -- it is a separate MeshInstance3D -- and the wheel keeps
        /// rolling on it with far less grip and a smaller radius, which is what "affects handling" means here
        /// rather than an abstract penalty.</summary>
        public bool PopTire(int i)
        {
            if ((uint)i >= (uint)_tireNodes.Count || _tirePopped[i]) return false;
            _tirePopped[i] = true;
            if (GodotObject.IsInstanceValid(_tireNodes[i])) _tireNodes[i].Visible = false;
            ApplyTirePhysics(i);
            return true;
        }

        public bool RepairTire(int i)
        {
            if ((uint)i >= (uint)_tireNodes.Count || !_tirePopped[i]) return false;
            _tirePopped[i] = false;
            if (GodotObject.IsInstanceValid(_tireNodes[i])) _tireNodes[i].Visible = true;
            ApplyTirePhysics(i);
            return true;
        }

        /// <summary>Push one wheel's grip and radius from its popped flag. Both are restored from the values
        /// captured at build, never recomputed -- the stock numbers are tuned per vehicle (a trailer's wheels
        /// are deliberately low-grip), so a popped-then-fixed wheel must come back to ITS OWN figure and not to
        /// a shared constant.</summary>
        void ApplyTirePhysics(int i)
        {
            if (_wNodes == null || (uint)i >= (uint)_wNodes.Length) return;
            var w = _wNodes[i];
            if (w == null || !GodotObject.IsInstanceValid(w)) return;
            if (_tirePopped[i])
            {
                w.WheelFrictionSlip = _tireFricRef[i] * 0.35f;   // bare steel on tarmac: it still bites, it just slides
                w.WheelRadius = _tireRadRef[i] * 0.78f;          // riding on the rim -> that corner drops
            }
            else
            {
                w.WheelFrictionSlip = _tireFricRef[i];
                w.WheelRadius = _tireRadRef[i];
            }
        }

        /// <summary>Nearest wheel to a world point, or -1. Tolerance is the wheel's own radius rather than a
        /// constant, so a bus tire is not harder to hit than a hatchback's.</summary>
        public int ResolveHitTire(Vector3 world, float slack = 0.22f)
        {
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < _tireNodes.Count; i++)
            {
                if (_tirePopped[i] || !GodotObject.IsInstanceValid(_tireNodes[i])) continue;
                var c = _tireNodes[i].GlobalPosition;
                float tol = (_tireRadRef.Length > i ? _tireRadRef[i] : 0.35f) + slack;
                float d = c.DistanceSquaredTo(world);
                if (d < tol * tol && d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>Split a wheel into (tire, rim) at the widest EMPTY radial band in its outer half -- the gap
        /// the modeller left between rim edge and tread. Derived per mesh rather than a fixed fraction: measured
        /// across all 11 wheel meshes the tire is 17-24% of the verts on road wheels but 64% on the tank, so a
        /// hardcoded ratio would cut a road tire off its rim and saw the tank's road wheel in half. Confirmed
        /// against the albedo through the UVs: on the sedan r 0.05-0.46 samples grey metal and r 0.56-0.61
        /// samples black rubber, with the seam in the empty band at 0.51.</summary>
        static (ArrayMesh tire, ArrayMesh rim) SplitWheelRadial(Mesh src)
        {
            if (src == null || src.GetSurfaceCount() == 0) return (null, null);
            var verts = src.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (verts.Length < 6) return (null, null);

            Vector3 c = Vector3.Zero;
            foreach (var q in verts) c += q;
            c /= verts.Length;
            float Rad(Vector3 q) => Mathf.Sqrt((q.Y - c.Y) * (q.Y - c.Y) + (q.Z - c.Z) * (q.Z - c.Z));   // wheels spin about X
            float rMax = 0f;
            foreach (var q in verts) rMax = Mathf.Max(rMax, Rad(q));
            if (rMax <= 0f) return (null, null);

            var rs = new System.Collections.Generic.List<float>();
            foreach (var q in verts) rs.Add(Rad(q));
            rs.Sort();
            float gap = 0f, split = 0f;
            for (int i = 0; i + 1 < rs.Count; i++)
            {
                if (rs[i] < 0.35f * rMax) continue;   // ignore the hub's own spokes
                float g = rs[i + 1] - rs[i];
                if (g > gap) { gap = g; split = (rs[i] + rs[i + 1]) * 0.5f; }
            }
            if (gap < 0.02f) return (null, null);   // no seam worth cutting -- a solid wheel (train), leave it whole

            // ANY vertex past the seam puts the triangle in the TIRE. 52 of the sedan wheel's 370 triangles
            // genuinely span the gap (the sidewall panels bridging rim edge to tread), so no assignment leaves
            // both halves clean -- but a blown tire takes its sidewall with it, and this way the rim ends at
            // the seam instead of keeping spikes that reach into the tread.
            return SplitMeshBy(src, (p0, p1, p2) => Rad(p0) > split || Rad(p1) > split || Rad(p2) > split);
        }

        // Tire test hooks -- the nodes and the wheel physics are private, and the point of the tire checks is
        // to assert on the REAL wheel numbers rather than on the popped flag that is supposed to drive them.
        public MeshInstance3D TireNodeForTest(int i) => (uint)i < (uint)_tireNodes.Count ? _tireNodes[i] : null;
        public MeshInstance3D RimNodeForTest(int i) => _wMeshes != null && (uint)i < (uint)_wMeshes.Length ? _wMeshes[i] : null;
        public float WheelFrictionForTest(int i) => _wNodes != null && (uint)i < (uint)_wNodes.Length && _wNodes[i] != null ? _wNodes[i].WheelFrictionSlip : 0f;
        public float WheelRadiusForTest(int i) => _wNodes != null && (uint)i < (uint)_wNodes.Length && _wNodes[i] != null ? _wNodes[i].WheelRadius : 0f;
        public CpuParticles3D TireSparksForTest(int i) => _tireSparks != null && (uint)i < (uint)_tireSparks.Length ? _tireSparks[i] : null;
        public bool WheelInContactForTest(int i) => _wNodes != null && (uint)i < (uint)_wNodes.Length && _wNodes[i] != null && _wNodes[i].IsInContact();

        // Test hooks. SetHeadlights/SetTaillights are private and the public ToggleHeadlights is gated on the
        // alarm and on Battery, so a test driving the real toggle would be asserting on those gates rather than
        // on lamp state. These reach the same code path with the gates satisfied.
        public void SetLightsForTest(bool on)
        {
            if (on && Battery <= 0f) Battery = 50f;
            SetHeadlights(on);
            SetTaillights(on);
        }
        public Node3D LampLightForTest(int i) => (uint)i < (uint)_lampLights.Count ? _lampLights[i] : null;

        /// <summary>Split a mesh in two by a per-triangle predicate, carrying EVERY vertex attribute the
        /// source actually has rather than a hand-picked subset.
        ///
        /// Both earlier splits rebuilt with position+normal, then position+normal+UV, each time by listing the
        /// attributes someone remembered. Dropping UVs made a rim sample one texel and render as rubber;
        /// RECOMPUTING normals as flat face normals threw away the 398 authored vertex normals the .obj ships
        /// and ContentProvider is careful to preserve, which lit the bare rim inside out and moved a visual
        /// golden (jeep_vside 0.0017 -> 0.0023) that I had written off as a harmless side effect of splitting.
        /// A list of attributes is a list you can be one short of, so this copies whatever is present and
        /// computes nothing. Anything added to these meshes later comes along without a code change here.</summary>
        // ---- BI-FOLD DOOR (bus) ----------------------------------------------------------------------------------
        /// <summary>Hang the door panel peeled out of the body as two leaves on two vertical hinges: leaf A on the front
        /// jamb, leaf B on the mullion between the two windows (a child of leaf A, so it folds back onto it like a
        /// jackknife). The meshes stay in BODY space and are offset back by their pivot, so at fold 0 nothing moves.
        /// The two window panes ride their leaves. Opens on enter/exit (CycleDoor) and folds shut after a hold.</summary>
        static void BuildBiFoldDoor(Vehicle v, Spec s, ArrayMesh doorMesh, Material bodyMat)
        {
            // Two leaves out of one panel: cut at the mullion. CLOSE THE CUT (master: "close up the open ends on the mesh, that
            // cutting left"): the panel is a hollow box, so the cut opens both leaves -- cap each with the convex hull of the
            // cut loop, coloured like the panel's inner face (one palette cell).
            var door = MeshCut.Read(doorMesh);
            // TRIM to the doorway + the step top (the modelled panel overhangs both, hidden inside the jambs / the step slab).
            var cutFront = new System.Collections.Generic.List<MeshCut.V>(); var cutRear = new System.Collections.Generic.List<MeshCut.V>();
            bool trim = s.DoorTrimZ0 != s.DoorTrimZ1;
            if (trim)
            {
                door = MeshCut.Split(door, 2, s.DoorTrimZ0, cutFront).above;
                door = MeshCut.Split(door, 2, s.DoorTrimZ1, cutRear).below;
            }
            var cut = new System.Collections.Generic.List<MeshCut.V>();
            var (setA, setB) = MeshCut.Split(door, 2, s.DoorSplitZ, cut);
            bool two = setA.Tris.Count > 0 && setB.Tris.Count > 0;
            // CLOSE EVERY CUT (master: "close up the open ends on the mesh" / "both end faces are still open"): the panel is a
            // hollow box, so every cut -- the mullion, both trimmed ends, the trimmed bottom -- opens it. Cap each with the
            // convex hull of its cut loop, coloured like the panel's inner face (one palette cell).
            var inner = cut.Count > 0 ? cut[0] : (cutFront.Count > 0 ? cutFront[0] : default);
            foreach (var c in cut) if (c.P.X < inner.P.X) inner = c;
            foreach (var c in cutFront) if (c.P.X < inner.P.X) inner = c;
            if (two && cut.Count >= 3)
            {
                MeshCut.CapHull(setA, cut, 2, Vector3.Back, inner.T, inner.C);      // leaf A's cut face looks +Z (at B)
                MeshCut.CapHull(setB, cut, 2, Vector3.Forward, inner.T, inner.C);   // leaf B's looks -Z
            }
            if (trim)
            {
                MeshCut.CapHull(two ? setA : door, cutFront, 2, Vector3.Forward, inner.T, inner.C);   // front end (on the jamb)
                MeshCut.CapHull(two ? setB : door, cutRear, 2, Vector3.Back, inner.T, inner.C);       // rear end
                foreach (var leaf in two ? new[] { setA, setB } : new[] { door })   // bottom: to the step top, capped underneath
                {
                    var cutBot = new System.Collections.Generic.List<MeshCut.V>();
                    var kept = MeshCut.Split(leaf, 1, s.DoorTrimY, cutBot).above;
                    leaf.Tris.Clear(); leaf.Tris.AddRange(kept.Tris);
                    MeshCut.CapHull(leaf, cutBot, 1, Vector3.Down, inner.T, inner.C);
                }
            }
            ArrayMesh leafA = two ? MeshCut.Commit(setA) : (trim ? MeshCut.Commit(door) : doorMesh), leafB = two ? MeshCut.Commit(setB) : null;
            v._doorFoldDeg = s.DoorFoldDeg > 0f ? s.DoorFoldDeg : 90f;
            v._doorPivotA = new Node3D { Name = "DoorHingeA", Position = new Vector3(s.DoorHingeX, 0f, s.DoorHingeZ) };
            v._doorPivotA.AddChild(new MeshInstance3D { Name = "DoorLeafA", Mesh = leafA, MaterialOverride = bodyMat, Position = -v._doorPivotA.Position });
            if (leafB != null)
            {
                // hinge B sits on the OUTER face at the mullion: after A swings in, B folds 180 back along A's outer face
                // and the two leaves stack side by side (25 cm) at the front jamb -- the doorway itself is clear.
                float hbx = s.DoorHingeBX != 0f ? s.DoorHingeBX : s.DoorHingeX;
                v._doorPivotB = new Node3D { Name = "DoorHingeB", Position = new Vector3(hbx - s.DoorHingeX, 0f, s.DoorSplitZ - s.DoorHingeZ) };
                v._doorPivotB.AddChild(new MeshInstance3D { Name = "DoorLeafB", Mesh = leafB, MaterialOverride = bodyMat, Position = new Vector3(-hbx, 0f, -s.DoorSplitZ) });
                v._doorPivotA.AddChild(v._doorPivotB);
            }
            v.AddChild(v._doorPivotA);
            // the windows in the door ride their leaf (they were built in body space under the vehicle: keep that offset)
            for (int i = 0; i < v._glassNodes.Count; i++)
            {
                var g = v._glassNodes[i]; string label = v._glassLabels[i];
                Node3D pivot = label == s.DoorGlassA ? v._doorPivotA : (label == s.DoorGlassB ? v._doorPivotB : null);
                if (pivot == null || !IsInstanceValid(g)) continue;
                var bodyOffset = pivot == v._doorPivotA ? -v._doorPivotA.Position : new Vector3(-(s.DoorHingeBX != 0f ? s.DoorHingeBX : s.DoorHingeX), 0f, -s.DoorSplitZ);
                g.GetParent()?.RemoveChild(g);
                pivot.AddChild(g);
                g.Position = bodyOffset;
            }
            // FLOOR POCKET (master: "clip out a chunk of the floor in the bus, theres a step up that the door clips with"): the
            // leaves' bottom is well under the cabin floor, so cut the floor where they swing and put a lowered floor at the
            // step-well level there with risers on its three inner sides -- the step well just gets bigger.
            if (s.DoorFloorCutMin != s.DoorFloorCutMax && v._bodyMesh != null)
            {
                var body = MeshCut.Read(v._bodyMesh.Mesh);
                var dropped = new System.Collections.Generic.List<MeshCut.V>();
                var kept = MeshCut.SubtractBox(body, new Aabb(s.DoorFloorCutMin, s.DoorFloorCutMax - s.DoorFloorCutMin), dropped);
                if (s.DoorRiserCutMin != s.DoorRiserCutMax)   // the old 2nd-step riser: the pocket continues the first step right past it
                    kept = MeshCut.SubtractBox(kept, new Aabb(s.DoorRiserCutMin, s.DoorRiserCutMax - s.DoorRiserCutMin), null);
                if (dropped.Count > 0)
                {
                    var f = dropped[0];   // the floor face's UV/colour (its palette cell) for the pocket
                    float x0 = s.DoorFloorCutMin.X, x1 = s.DoorRiserCutMin != s.DoorRiserCutMax ? s.DoorRiserCutMax.X : s.DoorFloorCutMax.X;
                    float z0 = s.DoorFloorCutMin.Z, z1 = s.DoorFloorCutMax.Z, yF = f.P.Y, yP = s.DoorPocketY;
                    MeshCut.Quad(kept, new Vector3(x0, yP, z0), new Vector3(x1, yP, z0), new Vector3(x1, yP, z1), new Vector3(x0, yP, z1), Vector3.Up, f.T, f.C);        // pocket floor, seamless with the first step
                    MeshCut.Quad(kept, new Vector3(x0, yP, z0), new Vector3(x0, yP, z1), new Vector3(x0, yF, z1), new Vector3(x0, yF, z0), Vector3.Right, f.T, f.C);     // inner riser (the new 2nd step)
                    MeshCut.Quad(kept, new Vector3(x0, yP, z1), new Vector3(x1, yP, z1), new Vector3(x1, yF, z1), new Vector3(x0, yF, z1), Vector3.Forward, f.T, f.C);   // rear side, under the step-well wall
                    MeshCut.Quad(kept, new Vector3(x0, yP, z0), new Vector3(x1, yP, z0), new Vector3(x1, yF, z0), new Vector3(x0, yF, z0), Vector3.Back, f.T, f.C);      // front side
                    var nb = MeshCut.Commit(kept);
                    if (nb != null) v._bodyMesh.Mesh = nb;
                }
            }
            if (System.Environment.GetEnvironmentVariable("UG_DOOROPEN") == "1") { v._doorOpenWanted = true; v._doorHold = 1e9f; }   // harness: hold it open for a render
        }

        /// <summary>Somebody got in or out: swing the door open, hold, fold shut.</summary>
        public void CycleDoor()
        {
            if (_doorPivotA == null) return;
            _doorOpenWanted = true; _doorHold = 1.6f;
        }
        public float DoorFold => _doorT;

        void UpdateDoor(float dt)
        {
            if (_doorOpenWanted) { _doorHold -= dt; if (_doorHold <= 0f) _doorOpenWanted = false; }
            float target = _doorOpenWanted ? 1f : 0f;
            if (Mathf.Abs(_doorT - target) < 1e-4f) return;
            _doorT = Mathf.MoveToward(_doorT, target, dt / 0.8f);   // 0.8 s swing either way
            float e = _doorT < 0.5f ? 2f * _doorT * _doorT : 1f - Mathf.Pow(-2f * _doorT + 2f, 2f) / 2f;   // ease in-out
            _doorPivotA.RotationDegrees = new Vector3(0f, -_doorFoldDeg * e, 0f);            // leaf A swings INTO the bus (its free edge goes -X)
            if (_doorPivotB != null) _doorPivotB.RotationDegrees = new Vector3(0f, 2f * _doorFoldDeg * e, 0f);   // leaf B folds fully back beside A (hinge B is a hair outside A's face: no z-fight)
        }

        static (ArrayMesh a, ArrayMesh b) SplitMeshBy(Mesh src, System.Func<Vector3, Vector3, Vector3, bool> intoA)
        {
            if (src == null || src.GetSurfaceCount() == 0) return (null, null);
            var arrays = src.SurfaceGetArrays(0);
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (verts.Length < 3) return (null, null);

            Vector3[] Norms()  { var v = arrays[(int)Mesh.ArrayType.Normal];  return v.VariantType != Variant.Type.Nil ? v.AsVector3Array() : null; }
            Vector2[] UVs()    { var v = arrays[(int)Mesh.ArrayType.TexUV];   return v.VariantType != Variant.Type.Nil ? v.AsVector2Array() : null; }
            Vector2[] UV2s()   { var v = arrays[(int)Mesh.ArrayType.TexUV2];  return v.VariantType != Variant.Type.Nil ? v.AsVector2Array() : null; }
            Color[]   Cols()   { var v = arrays[(int)Mesh.ArrayType.Color];   return v.VariantType != Variant.Type.Nil ? v.AsColorArray() : null; }
            float[]   Tans()   { var v = arrays[(int)Mesh.ArrayType.Tangent]; return v.VariantType != Variant.Type.Nil ? v.AsFloat32Array() : null; }
            var nrm = Norms(); var uv = UVs(); var uv2 = UV2s(); var col = Cols(); var tan = Tans();
            if (nrm != null && nrm.Length != verts.Length) nrm = null;
            if (uv  != null && uv.Length  != verts.Length) uv  = null;
            if (uv2 != null && uv2.Length != verts.Length) uv2 = null;
            if (col != null && col.Length != verts.Length) col = null;
            if (tan != null && tan.Length != verts.Length * 4) tan = null;

            var idxVar = arrays[(int)Mesh.ArrayType.Index];
            int[] idx;
            if (idxVar.VariantType != Variant.Type.Nil) idx = idxVar.AsInt32Array();
            else { idx = new int[verts.Length]; for (int i = 0; i < idx.Length; i++) idx[i] = i; }

            var stA = new SurfaceTool(); stA.Begin(Mesh.PrimitiveType.Triangles);
            var stB = new SurfaceTool(); stB.Begin(Mesh.PrimitiveType.Triangles);
            int nA = 0, nB = 0;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                var p0 = verts[idx[t]]; var p1 = verts[idx[t + 1]]; var p2 = verts[idx[t + 2]];
                bool a = intoA(p0, p1, p2);
                var st = a ? stA : stB;
                for (int k = 0; k < 3; k++)
                {
                    int vi = idx[t + k];
                    // Order matters to SurfaceTool: every attribute must be set BEFORE AddVertex.
                    if (nrm != null) st.SetNormal(nrm[vi]);
                    if (uv  != null) st.SetUV(uv[vi]);
                    if (uv2 != null) st.SetUV2(uv2[vi]);
                    if (col != null) st.SetColor(col[vi]);
                    if (tan != null) st.SetTangent(new Plane(tan[vi * 4], tan[vi * 4 + 1], tan[vi * 4 + 2], tan[vi * 4 + 3]));
                    st.AddVertex(verts[vi]);
                }
                if (a) nA++; else nB++;
            }
            // Only generate normals if the source genuinely had none -- never override authored ones.
            if (nrm == null) { if (nA > 0) stA.GenerateNormals(); if (nB > 0) stB.GenerateNormals(); }
            return (nA > 0 ? stA.Commit() : null, nB > 0 ? stB.Commit() : null);
        }

        /// <summary>Left/right halves of a lens mesh, by triangle centroid x. Delegates the actual rebuild to
        /// SplitMeshBy so the halves keep whatever the source carried.</summary>
        static (ArrayMesh, ArrayMesh) SplitMeshByX(Mesh src)
            => SplitMeshBy(src, (p0, p1, p2) => (p0.X + p1.X + p2.X) / 3f < 0f);

        /// <summary>The world-space centre of pane `i`, or Vector3.Zero if there is no such pane. Exposed so a
        /// test can aim AT a window without hard-coding a number that every vehicle would need its own copy of.</summary>
        public Vector3 GlassPaneCenter(int i)
            => (uint)i < (uint)_glassNodes.Count && GodotObject.IsInstanceValid(_glassNodes[i])
               ? _glassNodes[i].GlobalTransform * _glassNodes[i].GetAabb().GetCenter()
               : Vector3.Zero;

        /// <summary>Outward world-space normal of pane `i` -- its THINNEST local axis, flipped to point away
        /// from the vehicle's own origin. A pane is a flat slab, so its thin axis is its face direction; taking
        /// it off the mesh rather than assuming local Z is what makes it right for a RAKED windscreen and for
        /// the roof panes, whose thin axis is Y.</summary>
        public Vector3 GlassPaneNormal(int i)
        {
            if ((uint)i >= (uint)_glassNodes.Count || !GodotObject.IsInstanceValid(_glassNodes[i])) return Vector3.Zero;
            var mi = _glassNodes[i];
            var sz = mi.GetAabb().Size;
            var axis = sz.X <= sz.Y && sz.X <= sz.Z ? Vector3.Right
                     : sz.Y <= sz.Z ? Vector3.Up : Vector3.Back;
            var n = (mi.GlobalTransform.Basis * axis).Normalized();
            var outward = GlassPaneCenter(i) - GlobalPosition;
            return n.Dot(outward) < 0f ? -n : n;
        }

        /// <summary>The Vehicle a physics collider belongs to, or null. THE reason this exists: once the mesh
        /// hitbox is on, what a bullet ray or a look ray actually hits is the HitMesh StaticBody3D CHILD, and
        /// every `collider is Vehicle` test in the game silently stops matching -- shooting a car would do
        /// nothing at all, quietly, with no error anywhere. Walking up from the collider is the resolution that
        /// holds in both modes, and it also covers the child bodies that already existed (the ship's ladder, the
        /// helicopter's rotor hub boxes), which previously each needed their own GetParent() dance.
        ///
        /// Bounded to a few levels rather than walking to the scene root: an unbounded walk would report the
        /// world's terrain as belonging to whatever vehicle happened to be an ancestor of it in some future
        /// re-parenting, and a wrong Vehicle is worse than no Vehicle.</summary>
        static readonly System.Collections.Generic.Dictionary<string, ConcavePolygonShape3D> _triCache = new();
        StaticBody3D _hitMesh;   // the model-as-hitbox child, when UG_MESHHITBOX is on; null otherwise

        const int OwningMaxDepth = 4;   // collider -> body -> vehicle, with headroom; see the note above
        public static Vehicle Owning(GodotObject o)
        {
            if (o is Vehicle direct) return IsInstanceValid(direct) ? direct : null;
            var n = o as Node;
            for (int depth = 0; n != null && depth < OwningMaxDepth; depth++, n = n.GetParent())
                if (n is Vehicle v) return IsInstanceValid(v) ? v : null;
            return null;
        }

        public int ResolveHitGlass(Vector3 world, float tol = 0.28f)
        {
            int best = -1; float bestD = tol;
            for (int i = 0; i < _glassNodes.Count; i++)
            {
                if (_glassBroken[i] || !GodotObject.IsInstanceValid(_glassNodes[i])) continue;
                var mi = _glassNodes[i];
                var local = mi.GlobalTransform.AffineInverse() * world;
                var box = mi.GetAabb().Grow(tol);
                if (!box.HasPoint(local)) continue;
                float d = local.DistanceTo(mi.GetAabb().GetCenter());
                if (d < bestD || best < 0) { best = i; bestD = d; }
            }
            return best;
        }

        /// <summary>UG_GLASSDEBUG=1 -- one flat opaque colour per pane. strawberry 2026-08-31 asked for this
        /// to BE the check ("bright colors per pane, from multiple angles so you are sure you are covering with
        /// no overlap"), so it lives here rather than as a patch someone re-applies each time. Unshaded, so the
        /// colour is the same at every angle and a gap round the frame reads as body colour rather than shading.</summary>
        // One per entry in GlassPaneLabels. The list must not be SHORTER than that array: the index wraps
        // with %, so a 10-pane bus drawn from 6 colours repeats two of them, and a repeated colour is
        // exactly what this check exists to rule out -- you cannot tell an overlap from a neighbour.
        static readonly Color[] GlassDebugColors = {
            new Color(1f, 0.2f, 1f),    new Color(0.1f, 1f, 1f),   new Color(0.25f, 0.45f, 1f),
            new Color(1f, 0.15f, 0.15f), new Color(1f, 0.95f, 0.1f), new Color(0.15f, 1f, 0.25f),
            new Color(1f, 0.55f, 0f),   new Color(0.6f, 0.2f, 1f), new Color(1f, 1f, 1f),
            new Color(0.1f, 0.35f, 0.2f), new Color(0.5f, 0.9f, 0.1f), new Color(0.9f, 0.4f, 0.6f),
            new Color(0.2f, 0.2f, 0.6f), new Color(0.7f, 0.7f, 0.2f),
            // three more for the roof labels -- this array must never be SHORTER than GlassPaneLabels or the
            // index wraps and two panes share a colour, which is the one thing the debug view exists to rule out
            new Color(0.0f, 0.8f, 0.6f), new Color(0.85f, 0.3f, 0.0f), new Color(0.45f, 0.65f, 1f),
        };

        /// <summary>Settle which body carries the player/bullet layers, once it is known whether this vehicle
        /// got a HitMesh at all, and record the result as the un-ghosted base.
        ///
        /// WHY IT IS CONDITIONAL, and it is the bug this method exists for. The strip used to run early and
        /// UNCONDITIONALLY, on every vehicle. But the HitMesh is only built on the car-body branch: a ship goes
        /// down the HullBands/HullDecompose path and an aircraft may take the plain box, and neither gets one.
        /// So those vehicles handed bit0|bit5 away and received nothing back -- no collider on any layer a
        /// player, a bullet, a look ray or the crane scans. The container ship measured 792 probe points with
        /// model but NO collider (against the old box hull's 972) and a player could not stand on her deck.
        /// A vehicle with no hit mesh keeps the layers it always had.</summary>
        static void FinaliseHitboxLayers(Vehicle v)
        {
            if (MeshHitbox && v._hitMesh != null)
            {
                // The convex hulls stop being what the player walks into and what bullets stop on; they keep
                // driving the car and colliding with terrain, props and each other.
                // The chassis gives up BOTH bit 0 and bit 5 -- those are the layers the player, bullets and
                // every vehicle-scanner look at, and the mesh takes that job. Note bit 0 here is the layer the
                // chassis SITS ON, which is a different thing from the bit 0 in its MASK: the car still masks
                // bit 0 and so still collides with the terrain exactly as before. Conflating the two is what
                // made the first attempt at fixing the self-collision put bit 0 back on the layer, which
                // quietly returned the hulls to the bullet and walk masks and undid the entire change (the
                // roof went back to stopping a footstep 7 cm proud of the model).
                v.CollisionLayer = (v.CollisionLayer & ~((1u << 0) | (1u << 5))) | ChassisBit;
                v.CollisionMask |= ChassisBit;   // ...so car-on-car still resolves, with real impulses
            }
            v._baseCollisionLayer = v.CollisionLayer;   // the un-ghosted layer, so a towed trailer can swap the solid bit for bit6 and restore it
            v._baseCollisionMask = v.CollisionMask;      // and the un-ghosted mask (incl. bit8), so a ghosted trailer can add bit6 (to hit the cab's sleeper hull) and restore it
        }

        static void AddGlassOverlay(Vehicle v, Spec s)
        {
            if (s.GlassMesh == null) return;
            bool dbg = System.Environment.GetEnvironmentVariable("UG_GLASSDEBUG") == "1";
            var glassMat = new StandardMaterial3D
            {
                AlbedoColor = s.GlassTint ?? new Color(0.78f, 0.62f, 0.30f, 0.40f),   // default = the jet's golden canopy, ~40% opaque
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                Metallic = 0.35f, Roughness = 0.10f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            string base_ = s.GlassMesh.EndsWith(".txt") ? s.GlassMesh[..^4] : s.GlassMesh;
            foreach (var label in GlassPaneLabels)
            {
                var m = LoadOptionalObjQuiet($"{base_}_{label}.txt");
                if (m == null) continue;
                var mat = glassMat;
                if (dbg) mat = new StandardMaterial3D {
                    AlbedoColor = GlassDebugColors[v._glassNodes.Count % GlassDebugColors.Length],
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                var mi = new MeshInstance3D { Name = $"Glass_{label}", Mesh = m, MaterialOverride = mat,
                                              CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                v.AddChild(mi);
                v._glassNodes.Add(mi); v._glassLabels.Add(label);
                AddPaneCollider(v, mi, $"{base_}_{label}.txt", m);
            }
            if (v._glassNodes.Count == 0)   // no per-pane files -- fall back to a single mesh (the jet's canopy)
            {
                var gm = LoadOptionalObj(s.GlassMesh);
                if (gm == null) return;
                var mi = new MeshInstance3D { Name = "Glass", Mesh = gm, MaterialOverride = glassMat,
                                              CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                v.AddChild(mi);
                v._glassNodes.Add(mi); v._glassLabels.Add("canopy");
            }
            v._glassBroken = new bool[v._glassNodes.Count];
        }

        static readonly System.Collections.Generic.Dictionary<string, ConcavePolygonShape3D> _paneTriCache = new();

        /// <summary>Give one glass pane a collider of its own, but ONLY with the mesh hitbox on.
        ///
        /// WHY IT EXISTS. With the hulls out of the player and bullet layers, the windscreen APERTURE is a hole:
        /// the hull used to be what stopped a round at the glass, and the panes have never carried a collider.
        /// Measured on a sedan, a ray fired along the windscreen's own normal stopped 0.26 m short of the pane
        /// on the hull -- and passed clean through the car, hitting NOTHING AT ALL, with the mesh hitbox on.
        ///
        /// WHY NOT ALWAYS. With the hulls still on those layers the pane sits INSIDE a collider that already
        /// stops everything, so these bodies would add six more shapes to every physics query the car takes
        /// part in and change nothing observable. That was the stated reason the panes had no collider, and it
        /// is still the right call in that mode.
        ///
        /// The body hangs off the PANE, so it inherits the pane's transform and Vehicle.Owning resolves a hit
        /// through it (body -> pane -> vehicle, inside OwningMaxDepth). Excepted from the car for the same
        /// reason HitMesh is: bit 0 is a layer the chassis masks, and a car must not collide with its own glass.</summary>
        static void AddPaneCollider(Vehicle v, MeshInstance3D pane, string key, Mesh m)
        {
            if (!MeshHitbox) return;
            if (!_paneTriCache.TryGetValue(key, out var tri))
            {
                tri = m.CreateTrimeshShape();
                tri.BackfaceCollision = true;   // a pane is a single-sided quad; a round from outside must stop on it
                _paneTriCache[key] = tri;
            }
            var body = new StaticBody3D { Name = "GlassHit", CollisionLayer = HitMeshBit | (1u << 5), CollisionMask = 0 };
            body.AddChild(new CollisionShape3D { Shape = tri });
            pane.AddChild(body);
            v.AddCollisionExceptionWith(body);
            v._glassBodies.Add(body);
        }

        /// <summary>LoadOptionalObj without the "missing" print -- probing per-pane files, absence is normal.</summary>
        static Mesh LoadOptionalObjQuiet(string file)
        {
            string abs = ProjectSettings.GlobalizePath($"res://content/{file}");
            return System.IO.File.Exists(abs) ? ContentProvider.ParseObj($"res://content/{file}") : null;
        }

        static Mesh LoadOptionalObj(string file)
        {
            string abs = ProjectSettings.GlobalizePath($"res://content/{file}");
            if (!System.IO.File.Exists(abs)) { GD.Print($"[heli] {file} missing -- falling back to primitive blades (run tools/extract_huey.py)"); return null; }
            return ContentProvider.ParseObj($"res://content/{file}");
        }

        /// <summary>The Rust minicopter: an ULTRALIGHT, which is to say almost nothing. VoX 2026-08-16, with a
        /// reference shot: "It should be a rust style ultralight minicopter, basically a frame with a steat".
        ///
        /// The design point is that it is mostly holes. A long tapered keel with two side rails, an open pair
        /// of seats bolted straight to it, a bare mast, a fuel can, and three wheels -- no panels, no canopy,
        /// nothing enclosing the pilot. Anything that reads as bodywork is wrong for this machine, which is why
        /// the enclosed version lives on as its own spec rather than being edited into this shape.</summary>
        static void BuildUltralightFrame(Vehicle v, Spec s, Material bodyMat, Material frameMat)
        {
            void Part(string name, Mesh m, Vector3 pos, Material mat, Vector3 rotDeg = default, Node3D parent = null)
            {
                var mi = new MeshInstance3D { Name = name, Mesh = m, MaterialOverride = mat, Position = pos };
                if (rotDeg != Vector3.Zero) mi.RotationDegrees = rotDeg;
                (parent ?? (Node3D)v).AddChild(mi);
            }
            void Tube(string name, float radius, float len, Vector3 pos, Material mat, Vector3 rotDeg, Node3D parent = null)
                => Part(name, new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = len, RadialSegments = 10, Rings = 1 }, pos, mat, rotDeg, parent);

            var seatMat = SolidMat(new Color(0.42f, 0.28f, 0.16f));   // the worn wooden seat pans
            var tankMat = SolidMat(new Color(0.62f, 0.16f, 0.12f));   // the red FUEL can

            // ---- KEEL. The spine of the machine, running nose to tail and carrying everything. It is also
            // _bodyMesh (the paintable member), because on this airframe the frame IS the body.
            var keel = new MeshInstance3D
            {
                Name = "Body",
                Mesh = new BoxMesh { Size = new Vector3(0.13f, 0.13f, 4.30f) },
                MaterialOverride = bodyMat,
                Position = new Vector3(0f, -0.28f, 0.55f),
                RotationDegrees = new Vector3(-3.5f, 0f, 0f),   // tail rides slightly high, as in the reference
            };
            v.AddChild(keel);
            v._bodyMesh = keel;

            // Side rails: front-wide, converging back onto the keel -- the A-frame that gives it its silhouette.
            foreach (float sx in new[] { -1f, 1f })
            {
                Tube($"Rail{(sx < 0 ? "L" : "R")}", 0.045f, 2.30f, new Vector3(sx * 0.45f, -0.30f, -0.35f), frameMat, new Vector3(84f, sx * 11f, 0f), null);
                Tube($"RailCross{(sx < 0 ? "L" : "R")}", 0.04f, 0.55f, new Vector3(sx * 0.30f, -0.05f, -0.25f), frameMat, new Vector3(0f, 0f, sx * 62f), null);
            }
            Tube("Axle", 0.05f, 1.70f, new Vector3(0f, -0.46f, -1.05f), frameMat, new Vector3(0f, 0f, 90f), null);

            // ---- WHEELS, not skids. Two up front on the axle, one small one under the tail.
            foreach (float sx in new[] { -1f, 1f })
                Part($"Wheel{(sx < 0 ? "L" : "R")}", new CylinderMesh { TopRadius = 0.30f, BottomRadius = 0.30f, Height = 0.16f, RadialSegments = 14, Rings = 1 },
                     new Vector3(sx * 0.86f, -0.46f, -1.05f), frameMat, new Vector3(0f, 0f, 90f), null);
            Part("TailWheel", new CylinderMesh { TopRadius = 0.13f, BottomRadius = 0.13f, Height = 0.09f, RadialSegments = 10, Rings = 1 },
                 new Vector3(0f, -0.50f, 2.35f), frameMat, new Vector3(0f, 0f, 90f), null);

            // ---- SEAT. ONE, on the centreline (VoX: "only 1 seat on the minicopter, you put 2 side by side").
            // It is also the only place to sit: the vehicle carries a single occupant anyway, so a second seat
            // was furniture that promised a passenger the netcode cannot deliver.
            Part("SeatPan", new BoxMesh { Size = new Vector3(0.46f, 0.06f, 0.44f) }, new Vector3(0f, 0.02f, 0.18f), seatMat, Vector3.Zero, null);
            Part("SeatBack", new BoxMesh { Size = new Vector3(0.46f, 0.50f, 0.06f) }, new Vector3(0f, 0.25f, 0.43f), seatMat, new Vector3(-9f, 0f, 0f), null);
            foreach (float sx in new[] { -1f, 1f })
                Tube($"SeatLeg{(sx < 0 ? "L" : "R")}", 0.03f, 0.40f, new Vector3(sx * 0.16f, -0.18f, 0.20f), frameMat, Vector3.Zero, null);
            Tube("Handlebar", 0.035f, 0.70f, new Vector3(0f, 0.16f, -0.30f), frameMat, new Vector3(0f, 0f, 90f), null);

            // ---- POWERPLANT: an engine block, a fuel can and a bare mast. No cowling over any of it.
            Part("Engine", new BoxMesh { Size = new Vector3(0.52f, 0.40f, 0.46f) }, new Vector3(0f, 0.22f, 0.86f), frameMat, Vector3.Zero, null);
            Part("FuelCan", new BoxMesh { Size = new Vector3(0.34f, 0.34f, 0.26f) }, new Vector3(0f, 0.62f, 0.72f), tankMat, Vector3.Zero, null);
            Tube("Mast", 0.055f, 1.15f, new Vector3(0f, 0.62f, 0.55f), frameMat, Vector3.Zero, null);   // behind the seat back (0.43), ahead of the fuel can (0.72)
            foreach (float sx in new[] { -1f, 1f })   // mast bracing down to the keel
                Tube($"MastStay{(sx < 0 ? "L" : "R")}", 0.028f, 0.95f, new Vector3(sx * 0.20f, 0.38f, 0.72f), frameMat, new Vector3(26f, 0f, sx * 22f), null);

            // ---- TAIL: a bare boom with a small fin. The keel already carries it, so this is just the fin.
            Part("TailFin", new BoxMesh { Size = new Vector3(0.04f, 0.46f, 0.34f) }, new Vector3(0f, 0.02f, 2.42f), frameMat, Vector3.Zero, null);
            Part("Stabiliser", new BoxMesh { Size = new Vector3(0.70f, 0.035f, 0.20f) }, new Vector3(0f, -0.16f, 2.30f), frameMat, Vector3.Zero, null);
        }

        static void BuildHeliModel(Vehicle v, Spec s, Material bodyMat)
        {
            var frameMat = SolidMat(new Color(0.16f, 0.16f, 0.18f));   // black tube frame, skids, boom
            var bladeMat = SolidMat(new Color(0.10f, 0.10f, 0.11f));
            var glassMat = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.70f, 0.78f, 0.35f), Metallic = 0.3f, Roughness = 0.1f,
                                                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

            void Part(string name, Mesh m, Vector3 pos, Material mat, Vector3 rotDeg = default, Node3D parent = null)
            {
                var mi = new MeshInstance3D { Name = name, Mesh = m, MaterialOverride = mat, Position = pos };
                if (rotDeg != Vector3.Zero) mi.RotationDegrees = rotDeg;
                (parent ?? (Node3D)v).AddChild(mi);
            }
            // a tube along Z (the boom/skid primitive): CylinderMesh is Y-up, so it is laid down 90 deg about X
            void Tube(string name, float radius, float len, Vector3 pos, Material mat, Vector3 rotDeg, Node3D parent = null)
                => Part(name, new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = len, RadialSegments = 10, Rings = 1 }, pos, mat, rotDeg, parent);

            // ---- AIRFRAME. A spec that names real meshes (the Huey) uses them; one that does not (the
            // minicopter, which has no retail counterpart) gets the primitive frame built below.
            if (s.HeliBodyMeshes != null)
            {
                for (int i = 0; i < s.HeliBodyMeshes.Length; i++)
                {
                    var m = LoadOptionalObj(s.HeliBodyMeshes[i]);
                    if (m == null) continue;
                    var mi = new MeshInstance3D { Name = i == 0 ? "Body" : $"Body{i}", Mesh = m, MaterialOverride = bodyMat };
                    v.AddChild(mi);
                    if (v._bodyMesh == null) v._bodyMesh = mi;
                }
                BuildHeliRotors(v, s, bladeMat, frameMat);
                return;
            }

            if (s.Frame == HeliFrame.Ultralight)
            {
                BuildUltralightFrame(v, s, bodyMat, frameMat);
                BuildHeliRotors(v, s, bladeMat, frameMat);
                return;
            }

            // ---- fuselage pod. This one is _bodyMesh: the paintable panel, and what the shared code hides/dresses.
            var pod = new MeshInstance3D
            {
                Name = "Body",
                Mesh = new BoxMesh { Size = new Vector3(1.05f, 0.80f, 1.35f) },
                MaterialOverride = bodyMat,
                Position = new Vector3(0f, 0.10f, 0.05f),
            };
            v.AddChild(pod);
            v._bodyMesh = pod;

            Part("Nose", new SphereMesh { Radius = 0.52f, Height = 1.04f, RadialSegments = 14, Rings = 8 }, new Vector3(0f, 0.10f, -0.60f), bodyMat);
            Part("Canopy", new SphereMesh { Radius = 0.46f, Height = 0.92f, RadialSegments = 14, Rings = 8 }, new Vector3(0f, 0.30f, -0.42f), glassMat);
            Part("Engine", new BoxMesh { Size = new Vector3(0.72f, 0.52f, 0.62f) }, new Vector3(0f, 0.34f, 0.62f), frameMat);
            Part("FuelTank", new SphereMesh { Radius = 0.30f, Height = 0.60f, RadialSegments = 12, Rings = 7 }, new Vector3(0f, 0.30f, 1.02f), frameMat);
            // two exposed bench seats, the minicopter's whole cabin
            Part("SeatL", new BoxMesh { Size = new Vector3(0.42f, 0.10f, 0.44f) }, new Vector3(-0.26f, 0.18f, 0.02f), frameMat);
            Part("SeatR", new BoxMesh { Size = new Vector3(0.42f, 0.10f, 0.44f) }, new Vector3(0.26f, 0.18f, 0.02f), frameMat);
            Part("SeatBack", new BoxMesh { Size = new Vector3(1.00f, 0.46f, 0.09f) }, new Vector3(0f, 0.42f, 0.27f), frameMat);

            // ---- skids. Two tubes plus four struts; these are what it rests on, and ExtraBoxes gives them collision.
            foreach (float sx in new[] { -1f, 1f })
            {
                Tube($"Skid{(sx < 0 ? "L" : "R")}", 0.055f, 2.30f, new Vector3(sx * 0.52f, -0.72f, 0.10f), frameMat, new Vector3(90f, 0f, 0f));
                Part($"SkidTip{(sx < 0 ? "L" : "R")}", new BoxMesh { Size = new Vector3(0.09f, 0.09f, 0.22f) }, new Vector3(sx * 0.52f, -0.66f, -1.10f), frameMat, new Vector3(-22f, 0f, 0f));
                foreach (float sz in new[] { -0.45f, 0.62f })
                    Tube($"Strut", 0.045f, 0.66f, new Vector3(sx * 0.38f, -0.40f, sz), frameMat, new Vector3(0f, 0f, sx * 22f));
            }

            // ---- tail boom + fin
            Tube("TailBoom", 0.075f, 2.70f, new Vector3(0f, 0.34f, 1.85f), frameMat, new Vector3(90f, 0f, 0f));
            Part("TailFin", new BoxMesh { Size = new Vector3(0.05f, 0.62f, 0.42f) }, new Vector3(0f, 0.62f, 2.98f), frameMat);
            Part("Stabiliser", new BoxMesh { Size = new Vector3(0.86f, 0.04f, 0.26f) }, new Vector3(0f, 0.34f, 2.82f), frameMat);

            Tube("Mast", 0.06f, 0.60f, new Vector3(0f, 0.80f, 0.20f), frameMat, Vector3.Zero);
            BuildHeliRotors(v, s, bladeMat, frameMat);
        }

        /// <summary>Mount both rotors, shared by every rotary-wing spec.
        ///
        /// The blades are the RETAIL Huey's (strawberry 2026-08-15: "theres an existing huey helicopter model
        /// etc in the game already"), pulled out of core.masterbundle by tools/extract_huey.py. Both meshes are
        /// authored as a flat disc in XZ -- main 11.14 m across and 0.10 thick, tail 2.56 m, measured off the
        /// extracted files -- i.e. already spinning about local Y, so each just needs scaling to the spec's span.
        /// The Huey uses them at 1:1; the minicopter shrinks the same geometry to a 5.3 m disc.
        ///
        /// Falls back to box blades when the extraction has not been run, so a fresh checkout without the
        /// generated content still builds a flyable machine instead of an invisible rotor.</summary>
        static void BuildHeliRotors(Vehicle v, Spec s, Material bladeMat, Material frameMat)
        {
            // Per-spec rotor meshes. The fleet ships exactly TWO distinct rotors -- a 2-blade bar (Huey,
            // Hummingbird: 11.14 m span) and a 4-blade cross (Hind, Orca, Skycrane: 11.80 m) -- so the span used
            // for scaling is MEASURED off the mesh rather than being one constant. Scaling a cross by the bar's
            // number would size every heavy's rotor ~6 % wrong, which is exactly the sort of error that looks
            // fine in a screenshot.
            string rp = s.HeliRotorMeshPrefix ?? "huey";
            float mainSpan = MeshSpanX(LoadOptionalObj($"{rp}_rotor_main_blades.txt"), 11.14f);
            float tailSpan = MeshSpanX(LoadOptionalObj($"{rp}_rotor_tail_blades.txt"), 2.56f);

            var discMat = new StandardMaterial3D   // the blur plate reads as a translucent smear, not a lid
            {
                AlbedoColor = new Color(0.12f, 0.12f, 0.13f, 0.30f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };

            v._rotorNode = new Node3D { Name = "Rotor", Position = s.RotorHub };
            v.AddChild(v._rotorNode);
            v._rotorNode.AddChild(new MeshInstance3D
            {
                Name = "RotorHub",
                Mesh = new CylinderMesh { TopRadius = 0.11f, BottomRadius = 0.13f, Height = 0.16f, RadialSegments = 10, Rings = 1 },
                MaterialOverride = frameMat,
            });
            MountRotor(v._rotorNode, $"{rp}_rotor_main_blades.txt", $"{rp}_rotor_main_disc.txt",
                       s.RotorRadius * 2f / mainSpan, s.RotorRadius * 2f, 0.035f, 0.20f, bladeMat, discMat,
                       out v._bladesMesh, out v._discMesh);

            // Tail rotor. Its meshes lie flat about their own Y like the main rotor's, so the PIVOT is rolled
            // 90 deg to stand the disc on edge -- that way _tailRotorNode still just turns about local Y.
            v._tailRotorNode = new Node3D { Name = "TailRotor", Position = s.TailRotorHub, RotationDegrees = new Vector3(0f, 0f, 90f) };
            v.AddChild(v._tailRotorNode);
            MountRotor(v._tailRotorNode, $"{rp}_rotor_tail_blades.txt", $"{rp}_rotor_tail_disc.txt",
                       s.TailRotorRadius * 2f / tailSpan, s.TailRotorRadius * 2f, 0.03f, 0.10f, bladeMat, discMat,
                       out v._tailBladesMesh, out v._tailDiscMesh);
        }

        /// <summary>Hang one rotor's two states on a pivot: the physical BLADES and the spin-blur DISC.
        ///
        /// Both come from the retail Huey, where they are separate meshes precisely because the game swaps
        /// between them by rotor speed -- stationary blades when it is idle, a smeared plate when it is up.
        /// Drawing both at once (which the first cut did, by merging them in the extractor) puts an opaque
        /// 5 m plate over the airframe: structurally perfect, visually a table. Falls back to box blades and
        /// no disc when the extraction has not been run.</summary>
        /// <summary>Widest X extent of a rotor mesh, used to scale it to a spec's declared span. Measured
        /// rather than assumed: the fleet has both a 2-blade bar (11.14 m) and a 4-blade cross (11.80 m), and
        /// scaling one by the other's constant would size every heavy's rotor ~6 % wrong.</summary>
        static float MeshSpanX(Mesh m, float fallback)
        {
            if (m == null) return fallback;
            var aabb = m.GetAabb();
            return aabb.Size.X > 0.01f ? aabb.Size.X : fallback;
        }

        static void MountRotor(Node3D pivot, string bladeFile, string discFile, float scale, float span,
                               float boxThick, float boxChord, Material bladeMat, Material discMat,
                               out MeshInstance3D blades, out MeshInstance3D disc)
        {
            Mesh bm = LoadOptionalObj(bladeFile), dm = LoadOptionalObj(discFile);
            disc = null;
            if (bm != null)
            {
                blades = new MeshInstance3D { Name = "Blades", Mesh = bm, MaterialOverride = bladeMat, Scale = new Vector3(scale, 1f, scale) };
                pivot.AddChild(blades);
                if (dm != null)
                {
                    disc = new MeshInstance3D
                    {
                        Name = "Disc", Mesh = dm, MaterialOverride = discMat,
                        Scale = new Vector3(scale, 1f, scale), Visible = false,
                        // A blur plate must NOT cast a shadow. It is a rendering trick standing in for two
                        // thin blades, and a solid 5 m disc of shade under a hovering minicopter is the most
                        // conspicuous thing in the frame -- the translucent plate itself is nearly invisible
                        // from above while its shadow stays fully opaque, so the artefact reads as a bug in
                        // the lighting rather than in the rotor.
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    };
                    pivot.AddChild(disc);
                }
                return;
            }
            blades = new MeshInstance3D { Name = "Blades", Mesh = new BoxMesh { Size = new Vector3(span, boxThick, boxChord) }, MaterialOverride = bladeMat };
            pivot.AddChild(blades);
        }

        /// <summary>Assemble a fixed-wing PLANE: the paintable airframe meshes + a spinning propeller.
        ///
        /// The airframe reuses the heli's named-mesh convention -- <see cref="Spec.HeliBodyMeshes"/> lists the
        /// extracted fuselage pieces (otter_body{,_1}.txt), the first is the paintable <c>_bodyMesh</c>. The
        /// PROPELLER hangs on its own pivot (<c>_propNode</c>) that spins about the body FORWARD axis (local Z),
        /// not a rotor's vertical Y, and carries the extracted prop mesh + a spin-blur disc swapped in at speed --
        /// exactly the two-state trick MountRotor uses for a rotor. The pivot sits at the prop's own geometric
        /// centre (its hub) so it spins true no matter whether the mesh was authored in vehicle space or centred;
        /// <see cref="Spec.PropHub"/> is only the fallback when the extraction has not been run.</summary>
        static void BuildPlaneModel(Vehicle v, Spec s, Material bodyMat)
        {
            var bladeMat = SolidMat(new Color(0.10f, 0.10f, 0.11f));

            // ---- AIRFRAME (same path as the heli's named-mesh branch)
            if (s.HeliBodyMeshes != null)
                for (int i = 0; i < s.HeliBodyMeshes.Length; i++)
                {
                    var m = LoadOptionalObj(s.HeliBodyMeshes[i]);
                    if (m == null) continue;
                    var mi = new MeshInstance3D { Name = i == 0 ? "Body" : $"Body{i}", Mesh = m, MaterialOverride = bodyMat };
                    v.AddChild(mi);
                    if (v._bodyMesh == null) v._bodyMesh = mi;
                }

            // ---- CANOPY GLASS (jet): the LOD's closed cockpit cap (fighterjet_canopy.txt) re-laid over the open
            // cockpit as TRANSLUCENT golden glass (master: "take the golden opaque one from the LOD, give it transparency").
            AddGlassOverlay(v, s);   // jet canopy; cars use the same path (see the helper)

            if (s.MissileMesh != null)
            {
                var mmesh = LoadOptionalObj(s.MissileMesh);
                if (mmesh != null) v.AddChild(new MeshInstance3D { Name = "Missiles", Mesh = mmesh, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.34f, 0.36f), Metallic = 0.15f, Roughness = 0.65f, CullMode = BaseMaterial3D.CullModeEnum.Disabled } });   // wing missiles separated -> darker grey (master 2026-08-18)
            }
            if (s.SteerMesh != null)
            {
                var jmesh = LoadOptionalObj(s.SteerMesh);
                if (jmesh != null) v.AddChild(new MeshInstance3D { Name = "Joystick", Mesh = jmesh, Position = new Vector3(0f, 0.30f, 0f), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.14f, 0.14f, 0.16f), Metallic = 0.2f, Roughness = 0.7f, CullMode = BaseMaterial3D.CullModeEnum.Disabled } });   // cockpit control stick (source Objects/Steer), baked vehicle-local (master 2026-08-18)
            }

            // ---- PROPELLER (piston planes only). A JET has no prop -> a null PropMeshPrefix skips this whole block
            // (StepPlane already guards _propNode == null). Pivot at the mesh's own centre (the hub); blades +
            // blur-disc hang off it offset back to that centre so their verts land where authored.
            if (s.PropMeshPrefix != null)
            {
            string pp = s.PropMeshPrefix;
            Mesh propMesh = LoadOptionalObj($"{pp}_prop.txt");
            Mesh discMesh = LoadOptionalObj($"{pp}_prop_disc.txt");
            Vector3 hub = propMesh != null ? propMesh.GetAabb().GetCenter() : s.PropHub;
            v._propNode = new Node3D { Name = "Prop", Position = hub };
            v.AddChild(v._propNode);

            if (propMesh != null)
            {
                v._propBlades = new MeshInstance3D { Name = "PropBlades", Mesh = propMesh, MaterialOverride = bladeMat, Position = -hub };
                v._propNode.AddChild(v._propBlades);
                if (discMesh != null)
                {
                    var discMat = new StandardMaterial3D
                    {
                        AlbedoColor = new Color(0.12f, 0.12f, 0.13f, 0.28f),
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    };
                    v._propDisc = new MeshInstance3D
                    {
                        Name = "PropDisc", Mesh = discMesh, MaterialOverride = discMat, Position = -hub,
                        Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    };
                    v._propNode.AddChild(v._propDisc);
                }
            }
            else
            {
                // fallback so a fresh checkout without the extraction still shows something turning
                v._propBlades = new MeshInstance3D { Name = "PropBlades", Mesh = new BoxMesh { Size = new Vector3(2.0f, 0.14f, 0.05f) }, MaterialOverride = bladeMat };
                v._propNode.AddChild(v._propBlades);
            }
            }   // end: has a propeller

            // ---- AFTERBURNER FLAMES (jet): a procedural-shader flame on a hollow cone shell out each rear engine
            // (content/afterburner.gdshader -- turbulent gas, hot core -> orange -> smoky tip, shock diamonds), plus
            // an orange point light. Each flame is a pivot NODE at the nozzle; StepPlane scales its Y for length +
            // width and feeds u_throttle to the shader. Per-burner u_seed de-syncs the two engines.
            if (s.BurnerPos != null && s.BurnerPos.Length > 0)
            {
                var flameShader = GetAfterburnerShader();
                v._jetFlames = new Node3D[s.BurnerPos.Length];
                v._jetFlameLights = new OmniLight3D[s.BurnerPos.Length];
                v._jetFlameMats = new ShaderMaterial[s.BurnerPos.Length];
                for (int i = 0; i < s.BurnerPos.Length; i++)
                {
                    var bp = s.BurnerPos[i];
                    var mat = new ShaderMaterial { Shader = flameShader };
                    // flame colours (purple->blue->orange) live in afterburner.gdshader defaults -> re-grade with no C# rebuild
                    mat.SetShaderParameter("u_seed", i * 3.7f);
                    mat.SetShaderParameter("u_height", 2.4f);
                    mat.SetShaderParameter("u_throttle", 0f);
                    var pivot = new Node3D { Name = $"Afterburner{i}", Position = bp, RotationDegrees = new Vector3(90f, 0f, 0f) };   // +Y -> +Z (aft)
                    var cone = new MeshInstance3D
                    {
                        Mesh = new CylinderMesh { TopRadius = 0.03f, BottomRadius = 0.28f, Height = 2.4f, RadialSegments = 16, Rings = 1, CapTop = false, CapBottom = false },
                        MaterialOverride = mat,
                        Position = new Vector3(0f, 1.2f, 0f),   // wide bottom (-Y) at the nozzle; runs out +Y (aft)
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    };
                    pivot.AddChild(cone);
                    v.AddChild(pivot);
                    v._jetFlames[i] = pivot;
                    v._jetFlameMats[i] = mat;
                    var light = new OmniLight3D { Position = bp + new Vector3(0f, 0f, 0.7f), LightColor = new Color(1f, 0.5f, 0.18f), LightEnergy = 0f, OmniRange = 4.5f };
                    light.AddToGroup("dynlight");
                    v.AddChild(light);
                    v._jetFlameLights[i] = light;
                }
            }

            // ---- CONTRAILS (jet): a WORLD-SPACE vapour trail off each wingtip + winglet tip. Each Contrail keeps a
            // ring buffer of recent emitter world-positions + rebuilds a camera-facing ribbon every frame, so the
            // trail CURVES with the flight path + hangs in the air (not a stiff attached line). StepPlane feeds the
            // airspeed fade + the emitter world positions.
            if (s.ContrailPos != null && s.ContrailPos.Length > 0)
            {
                var trailMat = new ShaderMaterial { Shader = GetContrailShader() };
                v._contrails = new Contrail[s.ContrailPos.Length];
                for (int i = 0; i < s.ContrailPos.Length; i++)
                    v._contrails[i] = new Contrail(v, s.ContrailPos[i], trailMat);
            }
        }

        static Shader _afterburnerShader;
        // Loaded straight from the .gdshader text (not GD.Load) so a freshly-added file needs no Godot reimport
        // -- same idiom as the vehicle_paint.gdshader load above.
        static Shader GetAfterburnerShader()
            => _afterburnerShader ??= new Shader { Code = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/afterburner.gdshader")) };

        static Shader _contrailShader;
        static Shader GetContrailShader()
            => _contrailShader ??= new Shader { Code = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://content/contrail_trail.gdshader")) };

        // A world-space vapour contrail: a ring buffer of recent emitter world-positions, rebuilt each frame as a
        // camera-facing ribbon (ImmediateMesh, TopLevel = its verts ARE world coords). Points fade by age + by the
        // airspeed at emission, so the trail curves with the flight path + hangs in the air, thinning as it dissipates.
        sealed class Contrail
        {
            public readonly Vector3 Local;
            const int Max = 80;
            const float MaxAge = 4.0f, MinSeg = 0.7f;
            readonly Vector3[] _p = new Vector3[Max];
            readonly float[] _a = new float[Max], _t = new float[Max];
            int _n;
            readonly ImmediateMesh _im = new();
            readonly MeshInstance3D _mi;
            public Contrail(Node parent, Vector3 local, Material mat)
            {
                Local = local;
                _mi = new MeshInstance3D { Mesh = _im, MaterialOverride = mat, TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
                parent.AddChild(_mi);
            }
            public void Update(Vector3 world, float speedFac, Vector3 cam, float dt)
            {
                for (int i = 0; i < _n; i++) _t[i] += dt;
                int drop = 0; while (drop < _n && _t[drop] > MaxAge) drop++;
                if (drop > 0) { for (int i = drop; i < _n; i++) { _p[i - drop] = _p[i]; _a[i - drop] = _a[i]; _t[i - drop] = _t[i]; } _n -= drop; }
                if (_n == 0 || world.DistanceTo(_p[_n - 1]) >= MinSeg)
                {
                    if (_n >= Max) { for (int i = 1; i < Max; i++) { _p[i - 1] = _p[i]; _a[i - 1] = _a[i]; _t[i - 1] = _t[i]; } _n = Max - 1; }
                    _p[_n] = world; _a[_n] = speedFac; _t[_n] = 0f; _n++;
                }
                else { _p[_n - 1] = world; if (speedFac > _a[_n - 1]) _a[_n - 1] = speedFac; }
                Rebuild(cam);
            }
            void Rebuild(Vector3 cam)
            {
                _im.ClearSurfaces();
                if (_n < 2) { _mi.Visible = false; return; }
                _mi.Visible = true;
                _im.SurfaceBegin(Mesh.PrimitiveType.Triangles);
                for (int i = 0; i < _n - 1; i++)
                {
                    Vector3 a = _p[i], b = _p[i + 1];
                    Vector3 dir = b - a; if (dir.LengthSquared() < 1e-6f) continue; dir = dir.Normalized();
                    float ha = (float)i / (_n - 1), hb = (float)(i + 1) / (_n - 1);   // 0 = tail (old), 1 = head (fresh)
                    Vector3 sa = dir.Cross(cam - a); sa = sa.LengthSquared() > 1e-6f ? sa.Normalized() * Mathf.Lerp(0.40f, 0.06f, ha) : Vector3.Zero;
                    Vector3 sb = dir.Cross(cam - b); sb = sb.LengthSquared() > 1e-6f ? sb.Normalized() * Mathf.Lerp(0.40f, 0.06f, hb) : Vector3.Zero;
                    float aa = _a[i] * Mathf.Clamp(1f - _t[i] / MaxAge, 0f, 1f);
                    float ab = _a[i + 1] * Mathf.Clamp(1f - _t[i + 1] / MaxAge, 0f, 1f);
                    Q(a - sa, 0f, aa); Q(a + sa, 1f, aa); Q(b + sb, 1f, ab);
                    Q(a - sa, 0f, aa); Q(b + sb, 1f, ab); Q(b - sb, 0f, ab);
                }
                _im.SurfaceEnd();
            }
            void Q(Vector3 p, float u, float alpha) { _im.SurfaceSetColor(new Color(1f, 1f, 1f, alpha)); _im.SurfaceSetUV(new Vector2(u, 0f)); _im.SurfaceAddVertex(p); }
        }

        /// <summary>A rotor disc as a monitoring Area3D: the thin cylinder the blades sweep. Masks the world +
        /// vehicle layers (bit0 | bit5) so it notices terrain, buildings and other vehicles, and sits on no
        /// layer of its own so nothing can collide WITH it -- it reports, it does not push.</summary>
        static Area3D MakeDiscArea(string name, float radius, float height, Vector3 pos, Vector3 rotDeg)
        {
            var area = new Area3D { Name = name, Position = pos, CollisionLayer = 0, CollisionMask = (1u << 0) | (1u << 5), Monitoring = true };
            if (rotDeg != Vector3.Zero) area.RotationDegrees = rotDeg;
            area.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = radius, Height = height } });
            return area;
        }

        static Vehicle Build(Spec s, int variant, string specKey)
        {
            var v = new Vehicle { Mass = s.Mass > 0f ? s.Mass : GlobalMass };   // per-spec kerb mass; GlobalMass is the fallback for specs with no number yet
            v.SpecKey = specKey; v.SpawnVariant = variant;   // MP §3.6: replicated so puppets rebuild the same spec + paint
            v.CollisionLayer |= 1u << 5;   // bit 5 = "vehicle" so player bullets can raycast-hit it (see PlayerController.StepBullets)
            v.CollisionMask |= 1u << 8;    // bit 8 = "solid small prop" -> a car collides with fences/hydrants/barrels instead of phasing through (NOT bit6, so trailer ghosting is unaffected) (strawberry)
            // The chassis layers are finalised at the END of Build (FinaliseHitboxLayers), because whether this
            // vehicle gives bit0|bit5 up depends on whether it actually GETS a HitMesh, and that is not known
            // until the body has been built.
            v.AddToGroup("vehicles");      // so NearestVehicle + explosion damage (grenades) find every vehicle, not just harness-grouped ones
            v.ContactMonitor = true; v.MaxContactsReported = 6; v.BodyEntered += v.OnVehicleContact;   // wake a frozen parked car when another vehicle rams it (master)
            // ENGINE AND BRAKE SCALE WITH MASS, for now. Both are FORCES applied through the wheels, and both
            // were authored against the one-size-fits-all 900 kg. Give a semi its real 7800 kg and leave the
            // force alone and it simply does not move: a = F/m = 600/7800 = 0.077 m/s^2, less than rolling
            // resistance. Measured -- the 7800 kg semi could not reach 0.5 m/s, and its turn probe collected
            // zero samples.
            //
            // So the ratio is held: a heavier vehicle gets proportionally more engine and more brake, which
            // leaves acceleration and braking-g exactly where strawberry last signed off on them while making
            // mass REAL everywhere it should be -- momentum in a collision, what shunts what, tow loads, and the
            // inertia tensor. This is scaffolding, not the destination: once the drivetrain has a real torque
            // curve, power stops being a mass-proportional constant and starts being an engine.
            float massScale = v.Mass / GlobalMass;
            v._engineForce = s.Engine * massScale; v._steerMax = s.SteerMax; v._steerMin = s.SteerMin;
            v._speedMax = s.SpeedMax; v._speedMin = -Mathf.Abs(s.SpeedMin); v._brakeForce = s.Brake * massScale;   // negative always -- see SetupDrivetrain
if (s.Wheels != null && s.Wheels.Length > 1)
            {
                float zmin = float.MaxValue, zmax = float.MinValue;
                foreach (var wl in s.Wheels) { zmin = Mathf.Min(zmin, wl.Item3); zmax = Mathf.Max(zmax, wl.Item3); }
                v._wheelbase = zmax - zmin;
            }
                        v._specSpeedMax = s.SpeedMax;   // the PRE-BUFF spec value: the steering fade keys off this, so the buff cannot stretch the steering curve
            // The THIRD constant that has to ride the mass, and the one the per-vehicle-mass commit missed.
            // TankYawGain is a TORQUE, so what it buys is torque/inertia -- and the pinned hull inertia is
            // m/12*(a^2+b^2), exactly proportional to mass at a fixed box. The tank went 900 kg -> 40000 kg,
            // so its yaw authority fell by the same 44x and skid-steer simply stopped: tank.differential_steer
            // measured a pivot of 0.0 deg and a turn of 1.1 deg. Scaling by massScale is exact rather than
            // approximate here, because inertia and mass move together.
            v._tankYawGain = TankYawGain * massScale;
            v._heli = s.Heli; v._tracked = s.Tracked;
            v._plane = s.Plane; v._planeThrust = s.PlaneThrust; v._planeLift = s.PlaneLift; v._planeTargetSpeed = s.PlaneTargetSpeed;
            v._planePitchTq = s.PlanePitchTorque; v._planeRollTq = s.PlaneRollTorque; v._planeYawTq = s.PlaneYawTorque;
            if (s.PlaneSteerFade > 0f) v._planeSteerFade = s.PlaneSteerFade;
            if (s.Plane && s.Wheels.Length > 0) v._spawnGrace = 0.4f;   // a WHEELED plane is SEATED on spawn (no drop) -> park-freeze it quickly, don't let it slide/spin through a long settle grace
            v._heliThrust = s.HeliThrust; v._heliPitchTq = s.HeliPitchTorque; v._heliRollTq = s.HeliRollTorque;
            v._heliYawTq = s.HeliYawTorque; v._heliLevel = s.HeliLevel;
            v._heliClimbMax = s.HeliClimbMax; v._heliFallMax = s.HeliFallMax;
            // DRAG, 1/m, derived so that LEVEL-FLIGHT TERMINAL SPEED IS EXACTLY Speed_Max: at equilibrium the
            // sustainable horizontal thrust equals drag, a = k*v^2, so k = a / Speed_Max^2. Speed_Max is the
            // right target because it is already the number the MP envelope validates against
            // (VehicleReplication caps horizontal motion at SpeedMaxMps * EnvelopeSlack), so calibrating the
            // sim's own top speed to anything else would guarantee either an unreachable spec or a pilot the
            // server rolls back. A dive still exceeds it -- that is what the backstop in StepHeli is for.
            // DERIVED AGAINST ETL-BOOSTED THRUST, because cruise is always inside ETL. EtlFull is 11 m/s and
            // the slowest airframe's Speed_Max is 20, so translational lift is pinned at its maximum at every
            // speed this calibration is about, and no lift cap binds it (every cap is >= 1.26 against 1.05).
            // Deriving from bare thrust made the stated invariant false by 4.2 % (Hind) to 7.0 % (minicopter)
            // -- not an envelope break, but it quietly spent a third of the backstop's margin.
            v._heliDragFwd = s.Heli && s.SpeedMax > 0.01f
                ? LevelFlightAccel(s.HeliThrust * (1f + EtlGain)) / (s.SpeedMax * s.SpeedMax) : 0f;
            v._rotorRadius = s.RotorRadius;
            v._slingHook = s.SlingHook; v._slingLen = s.SlingCable > 0.01f ? s.SlingCable : 9f; v._slingAnchor = s.SlingAnchor;
            // Only the sky-crane has a hook today, so this can be a flat table lookup rather than needing the
            // per-airframe name threaded through here. Grow this into a per-Spec field if a second hook airframe
            // ever needs its own leg geometry.
            v._slingVisualAnchor = s.SlingHook ? _skycraneSlingVisualLocal : s.SlingAnchor;
            // CEILING ON THE COMBINED LIFT MULTIPLIERS, derived from this airframe's OWN climb envelope rather
            // than picked. Terminal climb is (thrust * multipliers - g) / HeliHeaveDamp, and the server checks
            // vertical motion against HeliClimbMax with ZERO slack -- so a multiplier large enough to out-climb
            // that envelope does not read as a fast helicopter, it reads as a rollback of a legitimate pilot
            // doing the single most fun thing in the game. ETL 1.05 x ground effect 1.333 = 1.40 busts the
            // Hind (cap 1.26) and clears the minicopter (1.59), which is why this is per-airframe. The 0.9
            // keeps a margin so the cap binds before the envelope does.
            v._heliLiftCap = s.Heli && s.HeliThrust > 0.01f
                ? Mathf.Max(1f, (9.8f + HeliHeaveDamp * s.HeliClimbMax * 0.9f) / s.HeliThrust) : 1f;
            // TUNNELLING (strawberry_cow 2026-08-24: "cars arent colliding with some smaller props").
            // Only helis and planes had ContinuousCd; a car did not. At 30 m/s on the 50 Hz tick a car advances
            // 0.6 m PER STEP, so any prop thinner than that -- a fence post, a sign, a bollard -- can sit
            // entirely between two positions and never generate a contact. That matches the symptom exactly:
            // it is the SMALL props that go through, because small props are the thin ones.
            v.ContinuousCd = true;
            if (s.Heli)
            {
                // A helicopter is flown, not suspended. Damping here is AERODYNAMIC, not friction, and the
                // angular figure is deliberately TINY: the airframe is supposed to keep rotating after you let
                // go (VoX: "the vehical itself has rotational inertia which will keep it rotating for a bit
                // unless you counteract it with opposite stick input", then "a tiny amount of drag that does
                // eventually bring rotations and such to a stop but it should be very very slight"). 0.25 is a
                // ~4 s decay -- present, so nothing spins forever, but far too slow to fly for you. Stopping a
                // rotation is the pilot's job; this only cleans up afterwards.
                // LINEAR DAMP IS ZERO ON PURPOSE, and StepHeli hand-rolls both axes instead. Godot's
                // RigidBody3D.LinearDamp is a SCALAR -- Jolt's SetLinearDamping takes one float -- so it damps
                // the whole velocity vector and there is no axis-selective form. Leaving it at the old 0.35
                // would apply the vertical's linear heave-damping law to the horizontal as well, and once the
                // thrust boosts were replaced by real drag that linear term became the BINDING horizontal
                // constraint: terminal = sqrt(thrust^2 - g^2) / 0.35 puts six of the seven airframes below
                // their own spec top speed, the scoutcopter at 18.8 m/s against a spec of 26. Angular damping
                // is untouched and stays on the engine.
                // REPLACE, not the default COMBINE: setting LinearDamp to 0 under Combine does NOT mean zero,
                // it means the project's default_linear_damp (Godot's 0.1, never overridden here) still
                // applies. That residual is a LINEAR horizontal drag, which is precisely what this rework
                // exists to remove -- it left the three fastest airframes short of their own spec top speed,
                // and it is measurable: 0.100 s^-1, identical across hind, orca and hummingbird.
                v.LinearDampMode = DampMode.Replace; v.LinearDamp = 0f;
                // MEASURED 0.351 s^-1, NOT 0.25 (vehicle.heli_angular_damp, 2026-08-18). AngularDampMode is left
                // at Combine, so the project's default_angular_damp (Godot's 0.1, never overridden) is ADDED to
                // this. That is the identical trap the LINEAR axis hit and fixed twenty lines above -- measured at
                // 0.100 s^-1, switched to Replace -- and nobody came back for the angular one. It matters more
                // than it looks: cmd is an angular ACCELERATION integrated by ApplyTorque, so total attitude change
                // per stick input is alpha/zeta and the damping is a DIVISOR on how far the machine ends up
                // rotating, not merely on how fast. The real decay constant is 1/0.351 = 2.85 s, not the ~4 s
                // claimed below. Left as Combine deliberately for now: correcting it to Replace would make every
                // airframe 40 % looser, which is a feel change and VoX's call, not a silent cleanup.
                v.AngularDamp = 0.25f;   // -> 0.35 effective under Combine; see the measurement above
                // ANTI-COLLISION BEACON: the red flasher on the belly, and the ONLY light on the aircraft that
                // blinks. Slung just under the hull on the centreline so it reads from below and from the side.
                // Rate is the real one -- civil beacons run 40-45 flashes per minute, hence BeaconPeriod -- and
                // it is a short bright pulse rather than a 50/50 blink, which is what makes it read as a strobe
                // instead of a warning lamp.
                v._beaconMat = LensMat(new Color(1f, 0.06f, 0.06f), 0f);
                // SAME LENS MODEL AS THE NAV LIGHTS (strawberry) rather than a procedural sphere, so the three
                // lights on an airframe are visibly the same fitting in three places. The taillights mesh is
                // authored at ABSOLUTE positions in the airframe's own frame -- it is the port/starboard lens where
                // it sits on the hull -- so it cannot simply be dropped at the belly: re-centre it on its own
                // centroid first (a pivot at the belly, the mesh offset by -centroid inside it), which is the same
                // trick the door leaves use for their hinges.
                Vector3 bellyAt = new Vector3(0f, s.BoxCenter.Y - s.BoxSize.Y * 0.5f - 0.08f, s.BoxCenter.Z);
                // Parts is NULL on the specs that predate HeliParts (minicopter, scoutcopter) -- they carry no detail
                // meshes at all, which is also why they have no nav lights. Those fall through to the bead below.
                Mesh lens = null;
                if (s.BeaconLensFrom != null)
                    lens = ContentProvider.ParseObj($"res://content/{s.BeaconLensFrom}");   // borrowed lamp; see Spec.BeaconLensFrom
                else if (s.Parts != null)
                    foreach (var (ptxt, _) in s.Parts)
                        if (ptxt.Contains("taillights")) { lens = ContentProvider.ParseObj($"res://content/{ptxt}"); break; }
                // Bake the re-centring into the node's own Position rather than wrapping it in a pivot: the beacon
                // stays a DIRECT MeshInstance3D child named BeaconBelly, which is how the rest of the code and
                // HeliPartsTests address it. A pivot would have been tidier to read and would have quietly broken
                // every non-recursive FindChild("BeaconBelly") that expects a mesh.
                // BeaconLensMesh returns ONE lamp, already centred on the origin and already turned to face down,
                // so the node just sits at the belly point -- no centroid offset, no basis to get wrong.
                var beaconLens = BeaconLensMesh(lens);
                v._beaconMesh = new MeshInstance3D
                {
                    Name = "BeaconBelly",
                    Mesh = (Mesh)beaconLens ?? new SphereMesh { Radius = 0.10f, Height = 0.20f, RadialSegments = 8, Rings = 4 },
                    MaterialOverride = v._beaconMat,
                    Position = bellyAt,
                };
                v.AddChild(v._beaconMesh);
                v._beaconLight = new OmniLight3D { Position = bellyAt, OmniRange = 6f, LightColor = new Color(1f, 0.1f, 0.1f), LightEnergy = 0f };   // the BELLY point, not the mesh node, whose position is now a -centroid offset
                v.AddChild(v._beaconLight);
                v.ContinuousCd = true;   // a fast dive must not tunnel through terrain between ticks
                // ISOTROPIC inertia, set explicitly rather than left to Godot's derivation from the collision
                // boxes. Two reasons: those boxes are a crude stand-in for an open tube frame and would hand us
                // an essentially arbitrary tensor, and an isotropic tensor is rotation-invariant -- so
                // torque = alpha * I holds exactly in world space at any attitude, with no basis juggling.
                // The per-axis feel differences live in the spec's pitch/roll/yaw numbers instead, where they
                // are readable.
                v.Inertia = Vector3.One * (v.Mass * HeliInertiaPerKg);   // the BODY's mass, not the shared constant -- an airframe given its own weight must take its inertia with it

                // ---- ROTOR HEALTH + HITBOXES
                v._mainRotorHpMax = v._mainRotorHp = s.MainRotorHp > 0f ? s.MainRotorHp : s.Health * 0.45f;
                v._tailRotorHpMax = v._tailRotorHp = s.TailRotorHp > 0f ? s.TailRotorHp : s.Health * 0.28f;

                // The HUB boxes: small, at the mast, and the only thing a bullet can hit to kill a rotor.
                // Defaults scale off the rotor radius so a spec that declares nothing still gets a sane target.
                Vector3 mainBox = s.MainHubBox != Vector3.Zero ? s.MainHubBox : Vector3.One * Mathf.Max(0.22f, s.RotorRadius * 0.14f);
                Vector3 tailBox = s.TailHubBox != Vector3.Zero ? s.TailHubBox : Vector3.One * Mathf.Max(0.16f, s.TailRotorRadius * 0.42f);
                v._mainHubCentre = s.RotorHub; v._mainHubHalf = mainBox * 0.5f;
                v._tailHubCentre = s.TailRotorHub; v._tailHubHalf = tailBox * 0.5f;
                // Real collision shapes so a bullet raycast can actually reach them -- without these the hub
                // sits in empty space above the hull and no shot would ever resolve to it.
                v.AddChild(new CollisionShape3D { Name = "MainHubHit", Shape = new BoxShape3D { Size = mainBox }, Position = s.RotorHub });
                v.AddChild(new CollisionShape3D { Name = "TailHubHit", Shape = new BoxShape3D { Size = tailBox }, Position = s.TailRotorHub });

                // The DISCS: thin cylinders swept by the blades, as monitoring Areas rather than solid bodies.
                // Solid would make the rotor a battering ram that shoves the world; an Area only reports that
                // the blades are in something, which is what grinds them down.
                v._mainDiscArea = MakeDiscArea("MainDisc", s.RotorRadius, 0.14f, s.RotorHub, Vector3.Zero);
                v.AddChild(v._mainDiscArea);
                v._tailDiscArea = MakeDiscArea("TailDisc", s.TailRotorRadius, 0.10f, s.TailRotorHub, new Vector3(0f, 0f, 90f));
                v.AddChild(v._tailDiscArea);

                // PER-ROTOR SMOKE + FIRE (strawberry 2026-08-16: "the rotors should smoke more when hurt and
                // set fire when broken"). Separate emitters from the hull's damage smoke, at each hub, so a
                // dying rotor is legible as a ROTOR failure -- the whole point of splitting their health is
                // that you can tell which one is going, and one shared plume out of the engine bay would erase
                // exactly that. Sized well below the hull plumes: these mark a component, not a wreck.
                v._mainRotorSmoke = MakeSmoke("veh_smoke_1.png", new Color(0.42f, 0.42f, 0.42f), 1.5f, 2.0f, 16, false, 0.5f, 1.1f);
                v._mainRotorFire = MakeSmoke("veh_fire.png", new Color(1f, 0.72f, 0.32f), 0.6f, 2.6f, 22, true, 0.4f, 0.9f);
                v._tailRotorSmoke = MakeSmoke("veh_smoke_1.png", new Color(0.42f, 0.42f, 0.42f), 1.2f, 1.6f, 12, false, 0.3f, 0.7f);
                v._tailRotorFire = MakeSmoke("veh_fire.png", new Color(1f, 0.72f, 0.32f), 0.5f, 2.1f, 16, true, 0.25f, 0.6f);
                v._mainRotorSmoke.Position = v._mainRotorFire.Position = s.RotorHub;
                v._tailRotorSmoke.Position = v._tailRotorFire.Position = s.TailRotorHub;
                foreach (var fx in new[] { v._mainRotorSmoke, v._mainRotorFire, v._tailRotorSmoke, v._tailRotorFire })
                { fx.Emitting = false; v.AddChild(fx); }

                // BLADE-STRIKE SPARKS + a metal hit, one burst per damage tick (strawberry: "add a particle
                // effect and sound (metal)"). One-shot rather than continuous, so the feedback is per-strike --
                // grinding a rotor down is a rhythm of hits you can hear counting off, not a steady hiss.
                v._mainStrikeFx = MakeSmoke("veh_fire.png", new Color(1f, 0.85f, 0.45f), 0.30f, 5.5f, 14, true, 0.10f, 0.28f);
                v._tailStrikeFx = MakeSmoke("veh_fire.png", new Color(1f, 0.85f, 0.45f), 0.26f, 4.5f, 10, true, 0.08f, 0.22f);
                v._mainStrikeFx.Position = s.RotorHub; v._tailStrikeFx.Position = s.TailRotorHub;
                foreach (var fx in new[] { v._mainStrikeFx, v._tailStrikeFx })
                { fx.Emitting = false; fx.OneShot = true; fx.Explosiveness = 1f; v.AddChild(fx); }
                v._bonkFx = MakeSmoke("veh_smoke_1.png", new Color(0.62f, 0.60f, 0.55f), 0.55f, 3.2f, 18, false, 0.20f, 0.60f);
                v._bonkFx.Position = s.BoxCenter;
                v._bonkFx.Emitting = false; v._bonkFx.OneShot = true; v._bonkFx.Explosiveness = 1f;
                v.AddChild(v._bonkFx);
                var hitWav = LoadWav("res://content/impact_metal.wav");
                if (hitWav != null)
                {
                    hitWav.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;   // a strike is a hit, not a loop
                    v._strikeAudio = new AudioStreamPlayer3D { Stream = hitWav, UnitSize = 12f, MaxDistance = 90f, VolumeDb = 2f };
                    v.AddChild(v._strikeAudio);
                }
            }
            if (s.Plane)
            {
                // A plane is FLOWN like the heli: aerodynamic damping (angular a bit FIRMER so it self-settles
                // its rotation rate and doesn't tumble), continuous collision so a fast dive can't tunnel, and
                // an explicit isotropic inertia so torque = alpha*I holds at any attitude (the per-axis feel
                // lives in the spec's pitch/roll/yaw numbers). Linear damp stays low -- PlaneDrag does the real
                // airflow drag in StepPlane, and a plane should carry its speed.
                v.LinearDamp = 0.05f; v.AngularDamp = 1.1f;   // firmer angular damp than the heli: it damps the pitch short-period so a step of elevator SETTLES to the trimmed climb angle instead of over-rotating past it (the tail's pitch-rate damping). Roll needs held aileron in a turn as a result -- which is how a real plane flies
                v.ContinuousCd = true;
                v.Inertia = Vector3.One * (v.Mass * HeliInertiaPerKg);   // the BODY's mass, not the shared constant -- an airframe given its own weight must take its inertia with it
            }
            if (s.Ladders != null)
                foreach (var (centre, height, yawDeg) in s.Ladders)
                {
                    // The body's LOCAL Y is the climbable face normal (Ladder.FaceAxis reads GlobalBasis.Y), and
                    // the mesh's own thin axis is mesh Y too -- so one rotation serves both. Y -> +Z puts the face
                    // aft; yawDeg turns it elsewhere.
                    var faceAft = new Basis(Vector3.Right, Mathf.Pi * 0.5f);          // local Y -> +Z, local Z -> -Y (down)
                    var basis = new Basis(Vector3.Up, Mathf.DegToRad(yawDeg)) * faceAft;
                    var lad = new StaticBody3D
                    {
                        Name = "Ladder",
                        Transform = new Transform3D(basis, centre),
                        CollisionLayer = 1u << 0,   // the layer the player's body and the climb probe both see
                        CollisionMask = 0,
                    };
                    lad.SetMeta(Ladder.Meta, lad);   // what PlayerController.StepLadder resolves a probe hit through
                    // SOLID box, not the rung geometry. Local Z is the long axis after the rotation above.
                    lad.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.15f, 0.2f, height) } });
                    var rungs = ContentProvider.ParseObj("res://content/objects/Ladder_Metal_0.obj");
                    if (rungs != null)
                    {
                        var lmat = SolidMat(new Color(0.44f, 0.45f, 0.47f));
                        for (float z = -height * 0.5f + 3.375f; z < height * 0.5f; z += 6.75f)
                            lad.AddChild(new MeshInstance3D { Mesh = rungs, MaterialOverride = lmat, Position = new Vector3(0f, 0f, z) });
                    }
                    v.AddChild(lad);
                }
            v._deckVolume = s.DeckVolume; v._deckCenter = s.DeckCenter;
            if (v._deckVolume != Vector3.Zero)
            {
                // Contact reporting is the honest "is it actually standing on me" test, so it is switched on ONLY
                // for a vessel that carries -- it is not free, and no other vehicle needs it.
                v.ContactMonitor = true;
                // 32, not 16. The load cancellation below reads the contact list, so anything that falls off the
                // end of it keeps its full weight on the hull -- and a busy deck (several vehicles, a player, a
                // barricade) plus whatever the bow is nudging can reach 16 without trying. A cap that silently
                // drops loads is exactly the kind of limit that shows up as "it still sinks sometimes".
                v.MaxContactsReported = 32;
            }
            v._water = s.Water;   // BOAT/AMPHIBIOUS: voxelize the hull box for the source Buoyancy.cs voxel-Archimedes model
            v._bowLocalZ = s.BoxCenter.Z - s.BoxSize.Z * 0.5f;   // bow tip = front of the measured hull box along local -Z (Godot forward)
            if (s.Water != WaterMode.Car)
            {
                int slices = s.BuoySlices > 0 ? s.BuoySlices : 2;   // source Buoyancy.slicesPerAxis default -> 2x2x2 = 8 voxels; per-spec for hulls the source's 2 cannot resolve
                if (int.TryParse(System.Environment.GetEnvironmentVariable("UG_BUOYSLICES"), out var _bslc) && _bslc >= 2) slices = _bslc;   // live sweep knob (probe)
                Vector3 vsz = s.BoxSize / slices, minExt = s.BoxCenter - s.BoxSize * 0.5f;
                v._voxelHalfHeight = Mathf.Min(vsz.X, Mathf.Min(vsz.Y, vsz.Z)) * 0.5f;   // a voxel is "submerged enough" when its centre is within this of the surface
                var vox = new Vector3[slices * slices * slices];
                int vi = 0;
                float buoyDy = s.BuoyLift + (float.TryParse(System.Environment.GetEnvironmentVariable("UG_BUOYDY"), out var _bdy) ? _bdy : 0f);   // BuoyLift per-vehicle shifts float height (neg=higher); UG_BUOYDY tunes it live
                for (int sx = 0; sx < slices; sx++)
                    for (int sy = 0; sy < slices; sy++)
                        for (int sz = 0; sz < slices; sz++)
                            vox[vi++] = new Vector3(minExt.X + vsz.X * (0.5f + sx), minExt.Y + vsz.Y * (0.5f + sy) + buoyDy, minExt.Z + vsz.Z * (0.5f + sz));
                v._buoys = vox;
                v._steadyHull = s.SteadyHull;
                v._buoyReserve = s.BuoyReserve > 0f ? s.BuoyReserve : 1f;
                if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_BUOYRESERVE"), out var _br) && _br > 0f) v._buoyReserve = _br;   // live sweep knob
                v._buoyDamp = s.BuoyDamp > 0f ? s.BuoyDamp : 1f;   // per-vehicle buoyancy damping (big hulls settle slowly at 1x)
                v._turnScale = s.TurnScale > 0f ? s.TurnScale : 1f;   // per-vehicle rudder authority (a long hull's yaw inertia is not paid for by the mass-scaled torque)
                if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_BOATTURN"), out var _bt) && _bt > 0f) v._turnScale = _bt;   // live sweep knob (probe)
                v._gravityMag = Mathf.Abs(ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f).AsSingle());   // the g the body actually falls under -> Archimedes must balance it
            }
            else if (!s.Heli && !s.Plane && s.BoxSize != Vector3.Zero)
            {
                // A WHEELED land vehicle gets the same 2x2x2 voxel grid, but it is inert until the hull is actually
                // in water -- see ApplySwampedPhysics. Aircraft are excluded deliberately: they never reach the
                // wheeled path in _PhysicsProcess (StepPlane/StepHeli return before it), so buoys built for them
                // would be dead weight, and "driven into water" is not what a helicopter does.
                Vector3 vsz = s.BoxSize / 2f, minExt = s.BoxCenter - s.BoxSize * 0.5f;
                v._voxelHalfHeight = Mathf.Min(vsz.X, Mathf.Min(vsz.Y, vsz.Z)) * 0.5f;
                var vox = new Vector3[8];
                int vi = 0;
                for (int sx = 0; sx < 2; sx++)
                    for (int sy = 0; sy < 2; sy++)
                        for (int sz = 0; sz < 2; sz++)
                            vox[vi++] = new Vector3(minExt.X + vsz.X * (0.5f + sx), minExt.Y + vsz.Y * (0.5f + sy), minExt.Z + vsz.Z * (0.5f + sz));
                v._swampBuoys = vox;
                v._gravityMag = Mathf.Abs(ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f).AsSingle());
            }
            v.FifthWheelLocal = s.FifthWheel; v.KingpinLocal = s.Kingpin;   // trailer-hitch coupling points (Zero = neither)
            v._steerTurnSpeed = s.SteerMax * 2f;   // master: ramp to full lock a LOT longer than source (source default = SteerMax*5 deg/s) -> slower turn-in
            v._gears = s.ForwardGears; v._reverseGear = s.ReverseGear; v._shiftUpRpm = s.ShiftUpRpm;
            SetupDrivetrain(v, s);   // MUST run after the line above: it REPLACES _gears/_speedMax/_speedMin for a driven hull, and a trailer keeps the spec's
            v._idlePitch = s.IdlePitch; v._maxPitch = s.MaxPitch; v._idleVol = s.IdleVolume; v._maxVol = s.MaxVolume;
            v.FuelMax = v.Fuel = s.Fuel; v.FuelBurn = FuelBurnClassOf(s.Name);   // TANK = per-vehicle metric Spec.Fuel (1u=1mL) so cans<->vehicles share units; burn = per-class (PZ-scale, infFuel-masked)
            v.HealthMax = v.Health = s.Health * VehicleHealthScale; v.Battery = BatteryMax; v.DisplayName = s.Name;
            v.EngineHealthMax = v.EngineHealth = s.Health * VehicleHealthScale * 0.4f; v._hullSizeLocal = s.BoxSize;   // engine hp = 40% of body hp (a separate pool, not a slice of it)   // 10x hp (strawberry 2026-09-03); damage numbers untouched
            // Seats: the spec's own array if it has one, else the extracted table by spec key, else the single
            // hand-tuned driver spot. The fallback matters -- trailer has no bundle prefab to extract from, and a
            // null here would crash every seat lookup rather than degrading to one seat.
            v.SeatLocals = s.Seats ?? (SeatTable.TryGetValue(specKey, out var st) ? st : new[] { SeatOf(s.Name) });
            // SeatOffset (the visible 3rd-person BODY spot -- SeatBodyLocal, PlayerController.cs) uses the eyeballed
            // rise ONLY for the 11 classes it was actually tuned against; anyone else gets THEIR OWN real seat
            // plus a small generic rise, not the Jeep's absolute coordinate wearing this vehicle's name.
            v.SeatOffset = HandTunedSeatOf(s.Name) ? SeatOf(s.Name) : v.SeatLocals[0] + GenericSeatRise;
            v.AccessZones = BuildAccessZones(s, v.SeatLocals, s.BoxCenter, s.BoxSize);
            v._rearEngine = s.RearEngine;
            v.AccessBoxCenter = s.BoxCenter;   // the frame the zones were laid out in; a test needs it to know which way is 'outboard' of a given zone

            // TURRETS. Two nested pivots per mount -- yaw about the vehicle's up, pitch inside it -- with each
            // mesh baked at its own origin, so rotating a node swings only its own geometry. Built even when no
            // gun is wired yet: the mount is the thing seats and weapons both hang off.
            v.Turrets = s.Turrets ?? System.Array.Empty<TurretDef>();
            v._turretYaw = new Node3D[v.Turrets.Length];
            v._turretPitch = new Node3D[v.Turrets.Length];
            for (int i = 0; i < v.Turrets.Length; i++)
            {
                var t = v.Turrets[i];
                var yaw = new Node3D { Name = $"TurretYaw{t.Seat}", Position = t.Pivot };
                var pitch = new Node3D { Name = $"TurretPitch{t.Seat}" };
                var mat = SolidMat(t.Colour);
                if (t.YawMesh != null)
                    yaw.AddChild(new MeshInstance3D { Name = t.YawMesh.Replace(".txt", ""), Mesh = ContentProvider.ParseObj($"res://content/{t.YawMesh}"), MaterialOverride = mat });
                if (t.PitchMesh != null)
                    pitch.AddChild(new MeshInstance3D
                    {
                        Name = t.PitchMesh.Replace(".txt", ""),
                        Mesh = ContentProvider.ParseObj($"res://content/{t.PitchMesh}"),
                        MaterialOverride = mat,
                        RotationDegrees = t.MeshRotationDeg,
                        // A CREWED mount's gun is the gunner's, so it arrives with them: "hide the
                        // dragonfangs/nyks when spawned via vehicle command". A remote mount (the Hind's chin
                        // turret) is part of the airframe and is always there.
                        Visible = t.GunnerAt == Vector3.Zero,
                    });
                yaw.AddChild(pitch);
                v.AddChild(yaw);
                v._turretYaw[i] = yaw; v._turretPitch[i] = pitch;
            }
            v._turretCrew = new TargetDummy[v.Turrets.Length];
            v._turretAmmo = new int[v.Turrets.Length];
            v._turretCd = new float[v.Turrets.Length];
            for (int i = 0; i < v.Turrets.Length; i++) v._turretAmmo[i] = v.Turrets[i].Belt;
            if (s.DriverEye != Vector3.Zero) v.DriverEyeLocal = s.DriverEye;   // tall-cab override (semi); else keep the shared default
            v._outlineColor = ItemTool.RarityColorUI(s.Rarity);   // real vehicle rarity -> look-at outline/label colour (master)
            v._info = new InfoBillboard { TopLevel = true };   // look-at info billboard: name + HP/fuel/battery BARS, world-space at the cabin
            v.AddChild(v._info);

            var paint = SpawnPaint(s, variant);   // the source spawn paint by variant: default-list / curated car colour / white
            Material bodyMat = s.Palette != null
                ? PaintMat(s.Palette, paint)
                : new StandardMaterial3D { AlbedoColor = paint, Metallic = 0f, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            ArrayMesh bodyMesh = null, doorMesh = null; ArrayMesh legMesh = null, hlMesh = null, tlMesh = null;
            // baked taillight zone pair (LEFT + its X-mirror), when the body has REAL red taillights to split out (trailer)
            (Vector3, Vector3)[] tlZones = s.TaillightZoneMin != s.TaillightZoneMax
                ? new[] { (s.TaillightZoneMin, s.TaillightZoneMax),
                          (new Vector3(-s.TaillightZoneMax.X, s.TaillightZoneMin.Y, s.TaillightZoneMin.Z), new Vector3(-s.TaillightZoneMin.X, s.TaillightZoneMax.Y, s.TaillightZoneMax.Z)) }
                : null;
            // A HELICOPTER HAS NO RIPPED MESH. Unturned ships no minicopter, and nothing under content/ or the
            // U3-SDK is close enough to re-skin, so the airframe is built here out of primitives instead of
            // parsed from an .obj. Everything below the model -- collision, seats, fuel, damage -- is the
            // ordinary Vehicle path; only the geometry source differs.
            // ONE chain. `if (s.Heli) ...` used to be a DETACHED statement, so a helicopter ran the heli
            // builder and then fell straight through the plane/trailer/headlight tests into the final
            // `else`, asking ContentProvider for `res://content/` + a null Body. It returned null, which is
            // exactly what the code below expects for a heli, so nothing broke and nobody noticed -- it just
            // pushed an error per heli build, 131 of them across one suite run.
            if (s.Heli) BuildHeliModel(v, s, bodyMat);
            else if (s.Plane) BuildPlaneModel(v, s, bodyMat);
            else if (s.LandingLegZoneMin != s.LandingLegZoneMax && tlZones != null)   // trailer: peel BOTH the landing legs AND the baked taillights in one pass
                (bodyMesh, legMesh, tlMesh) = ContentProvider.ParseObjSplit2($"res://content/{s.Body}", new[] { (s.LandingLegZoneMin, s.LandingLegZoneMax) }, tlZones);
            else if (s.LandingLegZoneMin != s.LandingLegZoneMax)   // split the baked-in landing legs into their own mesh so they can vanish on couple
                (bodyMesh, legMesh) = ContentProvider.ParseObjSplitByZone($"res://content/{s.Body}", s.LandingLegZoneMin, s.LandingLegZoneMax);
            else if (s.HeadlightZoneMin != s.HeadlightZoneMax)   // split the baked-in headlight LENSES out (LEFT zone + its X-mirror) so the REAL geometry emits on 'L'; two zones keep the grille strip BETWEEN the lights out of the split (strawberry)
            {
                var lz = (s.HeadlightZoneMin, s.HeadlightZoneMax);
                var rz = (new Vector3(-s.HeadlightZoneMax.X, s.HeadlightZoneMin.Y, s.HeadlightZoneMin.Z), new Vector3(-s.HeadlightZoneMin.X, s.HeadlightZoneMax.Y, s.HeadlightZoneMax.Z));
                (bodyMesh, hlMesh) = ContentProvider.ParseObjSplitByZone($"res://content/{s.Body}", new[] { lz, rz });
            }
            else if (s.DoorZoneMin != s.DoorZoneMax)   // bi-fold door: peel the door panel out of the body so it can swing
                (bodyMesh, doorMesh) = ContentProvider.ParseObjSplitByZone($"res://content/{s.Body}", s.DoorZoneMin, s.DoorZoneMax);
            else
                bodyMesh = ContentProvider.ParseObj($"res://content/{s.Body}");
            if (bodyMesh != null)   // null only for the procedural heli, whose BuildHeliModel already set _bodyMesh
            {
                v._bodyMesh = new MeshInstance3D { Name = "Body", Mesh = bodyMesh, MaterialOverride = bodyMat };
                v.AddChild(v._bodyMesh);
                if (!s.Plane) AddGlassOverlay(v, s);   // road-car windows (the plane builder adds its own canopy)
                if (doorMesh != null) BuildBiFoldDoor(v, s, doorMesh, bodyMat);
            }
            if (s.Tracked) { v.MuzzleLocal = s.Muzzle; BuildTankExtras(v, s, bodyMat); }   // tank: treads + turret/gun aim pivots on top of the shared hull/wheel/collision path
            if (legMesh != null)   // the landing legs as a sibling MeshInstance sharing the body material -> toggled with the coupling (visible when parked, hidden when towed)
            {
                v._landingLegMesh = new MeshInstance3D { Name = "LandingLegs", Mesh = legMesh, MaterialOverride = bodyMat };
                if (s.LandingLegScaleY > 0f && s.LandingLegScaleY != 1f)   // vertically stretch the legs (about the deck pivot) so the feet reach the ground at the nose-up parked stance
                {
                    v._landingLegMesh.Scale = new Vector3(1f, s.LandingLegScaleY, 1f);
                    v._landingLegMesh.Position = new Vector3(0f, s.LandingLegPivotY * (1f - s.LandingLegScaleY), 0f);
                }
                v.AddChild(v._landingLegMesh);
            }
            if (hlMesh != null)   // the REAL baked headlight lenses as their own mesh -> cream, and emit on 'L' like a car (SetHeadlights drives _headlightMat)
            {
                // Split per side so each lamp can be shot out on its own. _headlightMat still points at one
                // of the halves so existing callers that poke it keep working; ApplyLampState drives both.
                var (hlL, hlR) = SplitMeshByX(hlMesh);
                if (hlL != null || hlR != null)
                {
                    foreach (var (half, label) in new[] { (hlL, "headlight_l"), (hlR, "headlight_r") })
                    {
                        if (half == null) continue;
                        var m = new StandardMaterial3D { AlbedoColor = new Color(0.94f, 0.89f, 0.73f), Metallic = 0f, Roughness = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                        var mi = new MeshInstance3D { Name = $"Lamp_{label}", Mesh = half, MaterialOverride = m };
                        v.AddChild(mi);
                        v._lampNodes.Add(mi); v._lampMats.Add(m); v._lampLights.Add(null); v._lampLabels.Add(label);
                        v._headlightMat ??= m;
                    }
                }
                else   // un-splittable (all one side) -- keep the old single-mesh behaviour rather than lose the lenses
                {
                    var hlMat = new StandardMaterial3D { AlbedoColor = new Color(0.94f, 0.89f, 0.73f), Metallic = 0f, Roughness = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                    v.AddChild(new MeshInstance3D { Name = "Headlights", Mesh = hlMesh, MaterialOverride = hlMat });
                    v._headlightMat = hlMat;
                }
            }
            if (tlMesh != null)   // the REAL baked RED taillights as their own mesh -> _taillightMat, so they glow while driven / on brake (trailer: driven by the cab pass-through). No added blocks -> no dupe (strawberry)
            {
                var (tlL, tlR) = SplitMeshByX(tlMesh);
                if (tlL != null || tlR != null)
                {
                    foreach (var (half, label) in new[] { (tlL, "taillight_l"), (tlR, "taillight_r") })
                    {
                        if (half == null) continue;
                        var m = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.06f, 0.06f), Metallic = 0f, Roughness = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                        var mi = new MeshInstance3D { Name = $"Lamp_{label}", Mesh = half, MaterialOverride = m };
                        v.AddChild(mi);
                        v._lampNodes.Add(mi); v._lampMats.Add(m); v._lampLights.Add(null); v._lampLabels.Add(label);
                        v._taillightMat ??= m;
                    }
                }
                else
                {
                    var tlMat = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.06f, 0.06f), Metallic = 0f, Roughness = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                    v.AddChild(new MeshInstance3D { Name = "Taillights", Mesh = tlMesh, MaterialOverride = tlMat });
                    v._taillightMat = tlMat;
                }
            }

            // source BoxCollider hull (Godot space), not the mesh AABB (which wrongly included the roll bar) --
            // UNLESS the spec asks for a real convex decomposition of its own mesh (Spec.HullBands), which is what
            // "make the hitbox match the model 1:1" has to mean for a body that MOVES.
            if ((s.HullBands != null || s.HullBoxes != null) && !ForceBoxHull
                && System.Environment.GetEnvironmentVariable("UG_SHIPBOX") != "1")   // live A/B knob, same seam as ForceBoxHull
            {
                int made = 0;
                if (s.HullTrimesh.HasValue && bodyMesh != null)
                {
                    var (tlo, thi) = s.HullTrimesh.Value;
                    var region = MeshRegion(bodyMesh, tlo, thi);
                    if (region != null)
                    {
                        // Its own body, not a shape on the vehicle: the vehicle is a VehicleBody3D and a trimesh
                        // on it is exactly the unsupported case. A StaticBody3D child inherits the parent's
                        // transform, so it rides along for free.
                        var tri = new StaticBody3D { Name = "HullMesh", CollisionLayer = 1u << 0, CollisionMask = 0 };
                        tri.AddChild(new CollisionShape3D { Shape = region.CreateTrimeshShape() });
                        v.AddChild(tri);
                        // AND EXEMPT THE HULL FROM ITS OWN DECKHOUSE. A static child is a SEPARATE body, not a
                        // shape on this one, and the vessel's mask scans the layer it sits on -- so she collides
                        // with her own superstructure, cannot resolve the overlap she is permanently inside, and
                        // is thrown out of the sea. Measured, without this: sank 80.83 m and finished at 179.99
                        // degrees of tilt, i.e. upside down. Every geometry check still PASSED while that
                        // happened; only the physics tests caught it.
                        v.AddCollisionExceptionWith(tri);
                        made++;
                    }
                }
                if (s.HullDecompose.HasValue && bodyMesh != null)
                {
                    var (dlo, dhi) = s.HullDecompose.Value;
                    v._decomposeMesh = MeshRegion(bodyMesh, dlo, dhi);
                    v._decomposeKey = $"{s.Body}|{dlo}|{dhi}|{BodyStamp(s.Body)}";   // BodyStamp: a changed body mesh invalidates its baked hulls
                    if (v._decomposeMesh != null) made++;
                }
                if (s.HullBands != null && bodyMesh != null)
                    foreach (var band in s.HullBands)
                    {
                        var cv = ConvexBand(bodyMesh, band.min, band.max);
                        if (cv != null) { v.AddChild(new CollisionShape3D { Shape = cv }); made++; }
                    }
                if (s.HullBoxes != null)
                    foreach (var b in s.HullBoxes)
                    {
                        v.AddChild(new CollisionShape3D
                        {
                            Shape = new BoxShape3D { Size = b.size },
                            Transform = new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(b.yawDeg)), b.center),
                        });
                        made++;
                    }
                // FALL BACK rather than ship a vehicle with NO collision: an empty band list would otherwise be a
                // silent hole you drive through the world in.
                if (made == 0) v.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = s.BoxSize }, Position = s.BoxCenter });
            }
            else if (bodyMesh != null && !ForceBoxHull
                     && System.Environment.GetEnvironmentVariable("UG_BOXHULL") != "1")
            {
                // 1:1 HULL FOR EVERY VEHICLE (strawberry_cow 2026-08-24: "give all vehicles the 1:1 hitbox vs
                // model visual treatment that the ship got"). Same machinery the ship uses -- decompose the body
                // mesh into convex hulls -- just no longer opt-in per spec. It runs on _Ready and is cached by
                // key, so VHACD runs once per vehicle TYPE, not per spawn.
                //
                // LOOSER SETTINGS THAN THE SHIP, deliberately. Hers are 48 hulls at 0.02 concavity because the
                // thing being captured is a deckhouse full of steps and voids that a box filled in. A car body
                // is nearly convex; asking for the same fidelity would spend a lot of VHACD time and a lot of
                // shapes to describe a wedge. Those numbers live on the spec key below, so the cache cannot
                // serve a ship's hulls to a hatchback or the reverse.
                //
                // The WHEELS are safe: Spec.Body and Spec.Wheel are separate meshes, so the chassis mesh being
                // decomposed contains no wheel geometry to collide with the ground.
                //
                // UG_BOXHULL=1 reverts every vehicle to the old single box, matching the UG_SHIPBOX seam. This
                // is a handling change, not a visual one -- a 1:1 hull follows the real underside where a box
                // was clamped clear of it -- so there needs to be one switch that puts it back.
                if (MeshHitbox)
                {
                    // The model itself, on its own static body so Jolt will accept a mesh shape at all, and
                    // exempted from the car so the car is not permanently inside its own hitbox. It carries
                    // the layers the player and bullets scan; the chassis has just given them up.
                    var hit = new StaticBody3D { Name = "HitMesh", CollisionLayer = HitMeshBit | (1u << 5), CollisionMask = 0 };
                    v._hitMesh = hit;
                    // Measured without BackfaceCollision, a ray straight down on the player's own layers passed
                    // THROUGH the bonnet and stopped on the floorpan at y -0.11 -- a hole you could shoot and
                    // walk through, over the whole front of the car. With it the same ray stops on the bonnet.
                    // CACHED PER BODY MESH, like the hulls above and for the same reason. Build() runs once per
                    // vehicle INSTANCE, not once per spec: a real PEI load spawns 88 vehicles from ~15 specs, so
                    // an uncached CreateTrimeshShape is 88 trimesh builds to describe 15 distinct shapes. The
                    // shape is immutable and refcounted, so every car of a spec can share one.
                    if (!_triCache.TryGetValue(s.Body, out var tri))
                    {
                        tri = bodyMesh.CreateTrimeshShape();
                        // BackfaceCollision, because these bodies are NOT watertight and their winding is not
                        // consistent: 492 of the sedan's 795 edges are not shared by exactly two faces.
                        tri.BackfaceCollision = true;
                        _triCache[s.Body] = tri;
                    }
                    hit.AddChild(new CollisionShape3D { Shape = tri });
                    v.AddChild(hit);
                    v.AddCollisionExceptionWith(hit);
                    v.DebugHitMeshTris = bodyMesh.GetFaces().Length / 3;
                }
                v._decomposeMesh = bodyMesh;
                v._decomposeKey = $"body|{s.Body}|{s.Name}|{System.Environment.GetEnvironmentVariable("UG_CARHULLS")}|{System.Environment.GetEnvironmentVariable("UG_CARCONCAVITY")}|{BodyStamp(s.Body)}";
                v._decomposeCars = true;
                // The box is BUILT but taken out of physics once the hulls land (see DecomposeHulls). It was
                // kept as a belly-pan on the reasoning that a hollow chassis decomposes to hulls that hug the
                // floorpan and leave the gap between the axles open. Measured, that premise does not hold for
                // these models: casting down over a 5x10 grid of the sedan's and the van's footprint finds a
                // CLOSED floor at y -0.13 at every single point. There is no hollow for the box to fill, and
                // the box's own underside sits at y 0.09 -- 22 cm ABOVE the floor the hulls already follow,
                // so it was never the ground contact either.
                // It stays in the tree, disabled, because two things read box children and neither is physics:
                // LookHulls (look-focus, which deliberately tracks the VISUAL footprint and already ignores
                // Disabled) and the _groundClearance measurement above. Deleting it would silently kill
                // look-focus on every car.
                v.AddChild(new CollisionShape3D { Name = "BellyBox", Shape = new BoxShape3D { Size = s.BoxSize }, Position = s.BoxCenter });
            }
            else v.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = s.BoxSize }, Position = s.BoxCenter });
            var roof = RoofBox(s.Name);   // source 2nd body box (roof slab): the port only had the main box, so the roof had no collision (master); jeep/quad/tractor are open, no roof
            if (roof.HasValue)
            {
                v.AddChild(new CollisionShape3D { Name = "RoofBox", Shape = new BoxShape3D { Size = roof.Value.size }, Position = roof.Value.center });
                if (v.CanTow)   // a tow-cab excepts its WHOLE body from the coupled trailer (CoupleTo) so the low coupling area doesn't fight the pin joint -- which also lets the trailer phase through the sleeper. Put a COPY of the roof hull on a SEPARATE static body (layer bit6) so the sleeper still blocks the trailer deck/headboard (anti-clip). The coupled trailer scans bit6 (SetTowGhost), so it hits this; the cab (mask bit0) never scans bit6, so it can't fight its own child hull. (strawberry 2026-07-16)
                {
                    var sleeper = new StaticBody3D { Name = "SleeperHull", CollisionLayer = 1u << 6, CollisionMask = 0 };
                    sleeper.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = roof.Value.size }, Position = roof.Value.center });
                    v.AddChild(sleeper);
                    v._sleeperHull = sleeper;
                }
            }
            if (s.ExtraBoxes != null) foreach (var (size, center) in s.ExtraBoxes)   // fixed extra hull boxes matching model geometry (trailer flatbed deck/headboard/gooseneck+kingpin, cab's low black rear frame)
            {
                var cs = new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = center };
                v.AddChild(cs); v._extraShapes.Add(cs);   // tracked so a towing cab can drop its rear frame to back under a trailer
            }
            if (s.LandingGearSize != Vector3.Zero)   // trailer front landing legs -> holds the nose level when parked; CoupleTo disables it (retracts) while towed
            {
                v._landingGear = new CollisionShape3D { Name = "LandingGear", Shape = new BoxShape3D { Size = s.LandingGearSize }, Position = s.LandingGearCenter };
                v.AddChild(v._landingGear);
            }
            // GROUND CLEARANCE: how far the lowest collision point sits BELOW the origin, measured off the
            // shapes actually attached rather than assumed from the spec. A wheeled vehicle can be dropped from
            // any sensible height and its suspension sorts it out; a helicopter has no suspension, so spawning
            // it "1.5 m up" either drops it onto its skids with a bang or, on a spec whose skids hang lower than
            // that, buries them in the terrain. PlaceOnGround uses this to seat it exactly.
            float lowest = 0f;
            foreach (var child in v.GetChildren())
                if (child is CollisionShape3D cs3 && cs3.Shape is BoxShape3D bs3)
                    lowest = Mathf.Min(lowest, cs3.Position.Y - bs3.Size.Y * 0.5f);
            if (s.Plane)   // a WHEELED plane rests on its GEAR (below the fuselage collision box) -> the wheels are the real lowest point, so GroundedByRay reaches the runway (else it reads airborne + the takeoff rotation never fires)
                foreach (var (wx, wy, wz, _) in s.Wheels)
                    lowest = Mathf.Min(lowest, wy - s.WheelRadius);
            v._groundClearance = -lowest;

            // rope-tow attach nodes (generic -- every vehicle gets them): bumper-height centre of the front / rear faces,
            // nudged just outside the hull so the rope clears the body. front = -Z (forward), rear = +Z. (strawberry rope tow)
            float towFrontZ = s.BoxCenter.Z - s.BoxSize.Z * 0.5f - 0.15f;
            float towRearZ  = s.BoxCenter.Z + s.BoxSize.Z * 0.5f + 0.15f;
            float towY = s.BoxCenter.Y - s.BoxSize.Y * 0.30f;   // low bumper height
            v.FrontTowLocal = new Vector3(s.BoxCenter.X, towY, towFrontZ);
            v.RearTowLocal  = new Vector3(s.BoxCenter.X, towY, towRearZ);
            v._towFrontNub = MakeTowNub(new Color(0.30f, 0.62f, 1f), v.FrontTowLocal);   // blue nub = front (the TOWED end)
            v._towRearNub  = MakeTowNub(new Color(0.25f, 0.85f, 0.30f), v.RearTowLocal);  // green nub = rear (the TOWER end)
            v.AddChild(v._towFrontNub); v.AddChild(v._towRearNub);

            // front bumper trigger (source Bumper): a forward volume that roadkills characters (enemy layer bit 1) the
            // vehicle drives into. Trigger only -- the body's own mask ignores the enemy layer, so it plows through.
            var bumper = new Area3D { CollisionLayer = 0, CollisionMask = 1u << 1 };
            float frontZ = s.BoxCenter.Z - s.BoxSize.Z * 0.5f;   // front face (forward = -Z)
            bumper.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(s.BoxSize.X, s.BoxSize.Y, 0.8f) }, Position = new Vector3(s.BoxCenter.X, s.BoxCenter.Y, frontZ - 0.2f) });
            v.AddChild(bumper);
            bumper.BodyEntered += v.OnBumperHit;

            var wheelMesh = ContentProvider.ParseObj($"res://content/{s.Wheel}");
            Material wheelMat;
            if (s.WheelTex != null)   // real wheel albedo (tyre + rim), nearest-sampled like the game
            {
                wheelMat = new StandardMaterial3D { AlbedoTexture = ContentProvider.TextureCached(ProjectSettings.GlobalizePath($"res://content/{s.WheelTex}")), TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            }
            else
                wheelMat = new StandardMaterial3D { AlbedoColor = new Color(0.09f, 0.09f, 0.10f), Metallic = 0f, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            int nw = s.Wheels.Length;
            v._wheelMeshRef = wheelMesh; v._wheelMatRef = wheelMat; v._wheelR = s.WheelRadius;   // for explosion debris
            v._wNodes = new VehicleWheel3D[nw]; v._wMeshes = new MeshInstance3D[nw];
            if (s.RetractGear) { v._gearPivots = new Node3D[nw]; v._gearAxis = new Vector3[nw]; v._gearAng = new float[nw]; v._wheelSuspF = new float[nw]; v._wheelFricF = new float[nw]; }
            // SUSPENSION IS SIZED BY WHAT EACH WHEEL ACTUALLY CARRIES, not by the hull's mass.
            //
            // I scaled both of these by massScale in dbb873ae and it was the wrong law, because massScale is
            // blind to how many wheels share the load. Bisected on the semi: 0% airborne at ce7e5f4a, 75% at
            // dbb873ae. A 7800 kg six-wheeler got 8.67x the spring of a 900 kg four-wheeler while each of its
            // wheels carries only 5.8x the load, so it bounced -- majority of wheels OFF THE GROUND for 77% of
            // a full-throttle run, which is why it could not put its power down and topped out at 11 m/s.
            //
            //   stiffness -> per-WHEEL static load against the 900kg-on-4-wheels point the constants were tuned at
            //   max force -> the wheel's share of the vehicle's weight, x3 headroom for bumps and landings
            //
            // The headroom factor reproduces the original 12000 for a jeep (3 * 1700 * 9.8 / 4 = 12495), which
            // is the check that this is a generalisation of the tuned value rather than a replacement for it.
            v.Tracked = s.Tracked;   // surface for the probe's fleet-wide headroom guards
            float loadScale = (v.Mass / Mathf.Max(1, nw)) / (GlobalMass / 4f);
            // Heavy multi-axle hulls (the semi) LAUNCH off a full 3x-headroom spring under throttle: the rebound
            // out-pushes gravity and the whole truck hops airborne ~21% of a run -- the machine-dependent flake in
            // vehicle.drivetrain (tinyclaw 2026-08-26: red every run on their box, green+deterministic on the 4080).
            // Trim the headroom on heavy hulls so the spring still holds static + dynamic load but can't FLING the
            // hull; the light jeep keeps the full 3x for bumps/landings. (Scaling damping up instead made it WORSE:
            // an over-damped stiff spring goes numerically unstable at 50 Hz and hops harder.)
            float headroom = (loadScale > 3.0f && !s.Tracked) ? 1.8f : SuspensionHeadroom;   // trim ONLY the very-heavy launchers: semi (loadScale 5.78, 7800kg) + apc (~7, 13000kg). 3.0 EXCLUDES the jeep (1.89, 1700kg) -- it never launches, and 1.8x makes it BOTTOM OUT (chassis-drag, ~36g one-tick stop that broke net.vehicle_freeze_hold). Tracked hulls (tank) keep full force too, for their short stiff suspension.
            float suspMaxF = headroom * v.Mass * 9.8f / Mathf.Max(1, nw);
            for (int i = 0; i < nw; i++)
            {
                var (x, y, z, steer) = s.Wheels[i];
                float wr = s.WheelRadii != null ? s.WheelRadii[i] : s.WheelRadius;   // per-wheel radius (tractor dual sizes)
                float wscale = wr / s.WheelRadius;                                   // scale the shared wheel mesh to match
                var w = new VehicleWheel3D
                {
                    Position = new Vector3(x, y, z), UseAsSteering = steer, UseAsTraction = s.Kingpin == Vector3.Zero,   // a TRAILER's wheels are passive rollers, NOT traction -- traction wheels on a towed body resist the pull
                    WheelRadius = wr, WheelRestLength = s.Tracked ? 0.15f : 0.25f, SuspensionTravel = s.Tracked ? 0.20f : 0.25f,   // tank: shorter suspension so the tracks sit ON the ground + a stiffer, less-bouncy ride
                    // stiffer + higher max force so 900kg doesn't compress the suspension into a permanent SQUAT; more
                    // damping to settle without bounce; higher friction slip = more TRACTION (was sliding/understeering).
                    // Trailer = low friction so the wheels free-roll behind the cab instead of gripping/dragging.
                    SuspensionStiffness = (s.Plane ? 30f : 55f) * loadScale, SuspensionMaxForce = suspMaxF, DampingCompression = s.Plane ? 7f : 3.5f, DampingRelaxation = s.Plane ? 8f : 4.2f, WheelFrictionSlip = s.Tracked ? TankWheelSlip : (s.Kingpin != Vector3.Zero ? 1.5f : s.Plane ? 2.0f : 6.0f),   // PLANE: softer + heavily-damped gear + lower friction slip so the narrow fuselage wheels do not CHATTER into a yaw wobble on rough terrain (master 2026-08-18)
                };
                // left wheels: flip the mesh so the tread faces outward
                // SHOOTABLE TIRES: hang the tread as its OWN MeshInstance3D, a child of the rim mesh, so popping
                // it hides the tread and leaves the rim turning. Child-of-rim rather than a sibling so the
                // explosion-debris path (which hides _wMeshes[i] and reads its position/scale) keeps working
                // untouched -- hiding the parent takes the tread with it.
                var (tireMesh, rimMesh) = s.Tracked || s.Plane ? (null, null) : SplitWheelRadial(wheelMesh);
                var _rimDbg = System.Environment.GetEnvironmentVariable("UG_TIREDEBUG") == "1" && tireMesh != null;
                var mi = new MeshInstance3D { Mesh = rimMesh ?? wheelMesh,
                    MaterialOverride = _rimDbg
                        ? new StandardMaterial3D { AlbedoColor = new Color(0.1f, 1f, 1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded }
                        : wheelMat,
                    Scale = new Vector3((x < 0 ? -1f : 1f) * wscale, wscale, wscale) };
                w.AddChild(mi);
                if (tireMesh != null)
                {
                    var _tireDbg = System.Environment.GetEnvironmentVariable("UG_TIREDEBUG") == "1";
                    var tn = new MeshInstance3D { Name = $"Tire{i}", Mesh = tireMesh,
                        MaterialOverride = _tireDbg
                            ? new StandardMaterial3D { AlbedoColor = new Color(1f, 0.15f, 0.9f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded }
                            : wheelMat };
                    mi.AddChild(tn);   // inherits the rim's flip+scale, so it lines up with no second transform
                    v._tireNodes.Add(tn);
                }
                v.AddChild(w);
                v._wNodes[i] = w; v._wMeshes[i] = mi;
                if (s.RetractGear)   // RETRACTABLE GEAR: hide the suspension-driven wheel; put the visual (strut + wheel) on a hinge PIVOT at the belly that folds up when airborne. VehicleWheel3D stays for physics.
                {
                    mi.Visible = false;
                    v._wheelSuspF[i] = w.SuspensionMaxForce; v._wheelFricF[i] = w.WheelFrictionSlip;   // remember the wheel's physics to restore when the gear deploys
                    var pivot = new Node3D { Name = $"Gear{i}", Position = new Vector3(x, 0.55f, z) };   // hinge at the TOP of the leg (matches the carve's re-centre) so the whole leg tucks up cleanly
                    var gm = LoadOptionalObj(Mathf.Abs(x) < 1f ? "fighterjet_gear_nose.txt" : (x < 0 ? "fighterjet_gear_mainL.txt" : "fighterjet_gear_mainR.txt"));   // the ACTUAL strut geometry, carved out of the body + re-centred to this pivot so it folds WITH the wheel
                    if (gm != null) pivot.AddChild(new MeshInstance3D { Name = "Strut", Mesh = gm, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.72f, 0.72f, 0.74f), Metallic = 0.1f, Roughness = 0.6f, CullMode = BaseMaterial3D.CullModeEnum.Disabled }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                    var gwheel = new MeshInstance3D { Mesh = wheelMesh, MaterialOverride = wheelMat, Position = new Vector3(0f, y - 0.55f, 0f), Scale = new Vector3((x < 0 ? -1f : 1f) * wscale, wscale, wscale) };   // wheel hangs below the top hinge (world y stays at the spec axle)
                    pivot.AddChild(gwheel);
                    v.AddChild(pivot);
                    v._gearPivots[i] = pivot;
                    if (z < 0f) { v._gearAxis[i] = Vector3.Right; v._gearAng[i] = -85f; }          // nose gear (forward, z<0): folds AFT about X
                    else { v._gearAxis[i] = Vector3.Right; v._gearAng[i] = 95f; }                             // main gear (fuselage, F-15): folds FORWARD + up into the belly about X -> X stays 0.85 so it clears the wing missiles (master 2026-08-18)
                }
            }

            // Tire state, sized once the wheel loop has run. The reference grip/radius are captured HERE rather
            // than recomputed on repair: they are tuned per vehicle (a trailer's wheels are deliberately
            // low-friction), so a fixed tire must return to its own figure, not a shared constant.
            v._tirePopped = new bool[v._tireNodes.Count];
            v._tireFricRef = new float[v._tireNodes.Count];
            v._tireRadRef = new float[v._tireNodes.Count];
            for (int ti = 0; ti < v._tireNodes.Count && ti < v._wNodes.Length; ti++)
            {
                if (v._wNodes[ti] == null) continue;
                v._tireFricRef[ti] = v._wNodes[ti].WheelFrictionSlip;
                v._tireRadRef[ti] = v._wNodes[ti].WheelRadius;
            }
            // Sparks off a bare rim, one emitter per wheel, parked at the CONTACT PATCH rather than the hub --
            // steel grinding tarmac throws from where it touches, and a plume at the axle reads as an engine
            // fire. Continuous while rolling, unlike the one-shot blade strikes: this is a state you are driving
            // in, not an event.
            if (v._tireNodes.Count > 0)
            {
                v._tireSparks = new CpuParticles3D[v._tireNodes.Count];
                for (int ti = 0; ti < v._tireNodes.Count && ti < v._wNodes.Length; ti++)
                {
                    if (v._wNodes[ti] == null) continue;
                    var fx = MakeSmoke("veh_fire.png", new Color(1f, 0.82f, 0.38f), 0.22f, 4.2f, 12, true, 0.05f, 0.16f);
                    fx.Position = v._wNodes[ti].Position - new Vector3(0f, v._tireRadRef[ti] * 0.78f, 0f);
                    fx.Emitting = false;
                    v.AddChild(fx);
                    v._tireSparks[ti] = fx;
                }
            }

            // Drop the centre of mass to just below the axle line so the car stops rolling on turns and pitching onto its
            // nose under braking (master). Godot's auto COM sat at the body-box centre (~0.6m up) -> top-heavy + tippy.
            float comY = 0f;   // (always overwritten below; init keeps the compiler happy now the plane branch is conditional)
            if (s.Wheels.Length > 0) { comY = 0f; foreach (var wl in s.Wheels) comY += wl.y; comY = comY / s.Wheels.Length - 0.2f; }
            if (s.Tracked) comY = TankComY;   // tank: force the COM LOW -- a tall hull on high (0.556) wheels is tippy otherwise (master "easily flipped")
            else if (s.Plane) { if (s.Wheels.Length == 0) comY = s.BoxCenter.Y - s.BoxSize.Y * 0.60f; }   // FLOATPLANE (no wheels): CoM DOWN at the pontoons so buoyancy holds it upright like a pendulum (a high-wing plane is CoM-below-wing anyway). A WHEELED land plane KEEPS its wheel-based CoM set above -- low + between the gear.
            else comY = s.BoxCenter.Y - s.BoxSize.Y * 0.25f;   // BOAT (no wheels): low COM below the hull centre so buoyancy keeps it upright (was a div-by-zero)
            v.CenterOfMassMode = RigidBody3D.CenterOfMassModeEnum.Custom;
            v.CenterOfMass = new Vector3(0f, comY, 0f);

            // INERTIA IS PINNED TO THE HULL BOX for anything with a convex decomposition, because otherwise
            // HANDLING IS A FUNCTION OF THE COLLISION MESH. Godot derives a body's inertia tensor from its
            // collision shapes whenever Inertia is left at zero, and the centre of mass is already Custom here --
            // so the two halves of the mass model disagreed: CoM was authored, inertia was whatever the collider
            // happened to be. Measured directly on the ship, same build, only the collider swapped
            // (UG_SHIPBOX=1): box hull 12.6 m/s and a 28 s circle, convex decomposition 12.0 m/s and 26 s. That
            // is a 1:1 HITBOX change quietly re-tuning a boat whose feel strawberry drove and signed off on.
            // BoxSize is the authored hull volume and is what TurnScale was calibrated against, so it stays the
            // authority for the mass distribution and the collider is free to describe the SHAPE instead.
            // EVERY vehicle that has not already authored one, not just the hull-decomposed ones (strawberry
            // 2026-08-22: "rebalance vehicles to use the same real physics inertia principal helis use").
            // The heli and the plane set an isotropic tensor of their own a few hundred lines up, and the gate
            // used to be `HullBands != null || HullBoxes != null` -- which exactly ONE spec satisfies, the ship.
            // So cars, tanks and the runabout were the case this whole comment warns about: authored centre of
            // mass, collider-derived inertia, half the mass model chosen and half of it whatever the collision
            // shape happened to be.
            // UG_CARINERTIA=0 restores the old collider-derived tensor, so the change can be measured A/B in
            // ONE build rather than argued from two. Same seam as the ship's UG_SHIPBOX knob above.
            if (!s.Heli && !s.Plane && System.Environment.GetEnvironmentVariable("UG_CARINERTIA") != "0")
            {
                var e = s.BoxSize;
                float dy = s.BoxCenter.Y - comY;                     // parallel-axis shift onto the real CoM
                v.Inertia = new Vector3(
                    v.Mass / 12f * (e.Y * e.Y + e.Z * e.Z) + v.Mass * dy * dy,
                    v.Mass / 12f * (e.X * e.X + e.Z * e.Z),          // yaw axis is along the offset, so unshifted
                    v.Mass / 12f * (e.X * e.X + e.Y * e.Y) + v.Mass * dy * dy);
            }

            if (s.Parts != null)   // detail meshes with their real solid colours (seats grey, lights, steering brown)
                foreach (var (txt, color) in s.Parts)
                {
                    // A helicopter's "taillights" are its NAVIGATION lights, and they are a red/green PAIR that
                    // has to be built from the single lens the mesh ships. Handled apart from the flat-coloured
                    // car parts because the colour depends on which SIDE each copy lands on.
                    if (s.Heli && txt.Contains("taillights")) { v.BuildNavLights(txt); continue; }

                    // SHOOTABLE LAMPS: a car's headlight/taillight part ships as ONE mesh covering both lamps,
                    // so split it per side and hang each half as its own MeshInstance3D with its own material.
                    // A shared material cannot glow on one side and not the other, which is what "shoot out the
                    // left headlight" requires. The BEAM is still built from the WHOLE mesh -- it is one merged
                    // volume spanning both lenses, and building it from a half would narrow the shaft.
                    if (!s.Heli && (txt.Contains("headlight") || txt.Contains("taillight")))
                    {
                        bool isHead = txt.Contains("headlight");
                        var full = ContentProvider.ParseObj($"res://content/{txt}");
                        if (isHead && full != null) v.BuildHeadlightBeam(full);
                        var (lhalf, rhalf) = SplitMeshByX(full);
                        if (lhalf != null && rhalf != null)   // BOTH halves, or it is not a per-side pair
                        {
                            // NO AUTHORED EMITTERS (quad, bus: master "no actual light sources") -> derive one per
                            // lens half from the lens geometry itself: its centre, pushed just outside the lens
                            // face (front for a headlight, rear for a taillight) so the beam is not born inside
                            // the mesh. Same vehicle-local frame as the lens (both hang straight off the body).
                            if (isHead ? s.SpotPos == null : s.TailPos == null)
                            {
                                foreach (var half in new[] { lhalf, rhalf })
                                {
                                    var ab = half.GetAabb(); var c = ab.GetCenter();
                                    float z = isHead ? ab.Position.Z - 0.06f : ab.End.Z + 0.06f;
                                    (isHead ? v._autoSpot : v._autoTail).Add(new Vector3(c.X, c.Y, z));
                                    if (System.Environment.GetEnvironmentVariable("UG_LAMPDBG") == "1") GD.Print($"[lamp] {s.Name} auto {(isHead ? "head" : "tail")} emitter at ({c.X:F2}, {c.Y:F2}, {z:F2}) from {txt}");
                                }
                            }
                            foreach (var (half, side) in new[] { (lhalf, "l"), (rhalf, "r") })
                            {
                                string label = (isHead ? "headlight_" : "taillight_") + side;
                                var lm = SolidMat(color);
                                var lmi = new MeshInstance3D { Name = $"Lamp_{label}", Mesh = half, MaterialOverride = lm };
                                v.AddChild(lmi);
                                v._lampNodes.Add(lmi); v._lampMats.Add(lm); v._lampLights.Add(null); v._lampLabels.Add(label);
                            }
                            if (isHead) v._headlightMat ??= v._lampMats[^1]; else v._taillightMat ??= v._lampMats[^1];
                            continue;
                        }
                        // Un-splittable (a single centred lamp, e.g. a bike) -- fall through to the old one-mesh path.
                    }

                    var pm = SolidMat(color);
                    // Named after its source file so the scene tree is readable and, more usefully, so a test can
                    // ASK for a specific part instead of guessing which unnamed MeshInstance3D is the turret.
                    var mi = new MeshInstance3D { Name = txt.Replace(".txt", ""), Mesh = ContentProvider.ParseObj($"res://content/{txt}"), MaterialOverride = pm };
                    if (txt.Contains("seat") || txt.Contains("steer")) mi.SetMeta("no_outline", true);   // interior parts -> keep OUT of the look-at outline so it's ONE silhouette, not the seats/wheel showing through the windows (master)
                    if (txt.Contains("steer") && s.SteerAxis != Vector3.Zero)   // wrap the steering wheel in a pivot at its centre so it can turn
                    {
                        v._steerPivot = new Node3D { Position = s.SteerPivot };
                        mi.Position = -s.SteerPivot;   // baked world verts render in place once the pivot sits at the centre
                        v._steerPivot.AddChild(mi);
                        v.AddChild(v._steerPivot);
                        v._steerAxis = s.SteerAxis.Normalized();
                    }
                    else v.AddChild(mi);
                    if (txt.Contains("headlight")) v._headlightMat = pm;   // capture so the lamp glows when the headlights are on
                    if (txt.Contains("headlight") && mi.Mesh != null) v.BuildHeadlightBeam(mi.Mesh);   // the visible shaft, shaped from these very lenses
                    if (txt.Contains("taillight")) v._taillightMat = pm;   // capture so the taillight glows red while driving
                    if (txt.Contains("siren0")) { v._sirenMat0 = pm; v._sirenLight0 = AddSirenLight(mi, new Color(1f, 0.05f, 0.05f)); v._sirenMi0 = mi; v._lampNodes.Add(mi); v._lampMats.Add(pm); v._lampLights.Add(v._sirenLight0); v._lampLabels.Add("lightbar_l"); }   // + a shoot-out lamp   // red lens: glow the material + cast a real red light from that side (master)
                    if (txt.Contains("siren1")) { v._sirenMat1 = pm; v._sirenLight1 = AddSirenLight(mi, new Color(0.2f, 0.3f, 1f)); v._sirenMi1 = mi; v._lampNodes.Add(mi); v._lampMats.Add(pm); v._lampLights.Add(v._sirenLight1); v._lampLabels.Add("lightbar_r"); }      // blue lens: material glow + real blue light from the other side
                }
            if (v._sirenMi0 != null && v._sirenMi1 != null)   // LIGHTBAR CENTRE: a hidden hit-box between the lenses; shot out -> the siren mangles/mutes (strawberry 2026-09-04)
            {
                var c0 = v._sirenMi0.Position + v._sirenMi0.Mesh.GetAabb().GetCenter(); var c1 = v._sirenMi1.Position + v._sirenMi1.Mesh.GetAabb().GetCenter();
                v._sirenCentre = new MeshInstance3D { Name = "Lamp_lightbar_c", Mesh = new BoxMesh { Size = new Vector3(0.36f, 0.16f, 0.24f) }, Position = (c0 + c1) * 0.5f, Visible = false };
                v.AddChild(v._sirenCentre);
                v._lampNodes.Add(v._sirenCentre); v._lampMats.Add(null); v._lampLights.Add(null); v._lampLabels.Add("lightbar_c");
            }
            System.Array.Resize(ref v._lampBroken, v._lampNodes.Count);   // the lamp arrays were sized before the lightbar joined them
            if (s.TaillightMesh != null)   // red lamp boxes at the rear -> red running glow while driven + brake flare; captured for the brake-light logic
            {
                var tlMat = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.06f, 0.06f), Metallic = 0f, Roughness = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
                var tlBox = new BoxMesh { Size = new Vector3(0.34f, 0.28f, 0.14f) };
                foreach (var p in s.TaillightMesh)
                {
                    var mi = new MeshInstance3D { Mesh = tlBox, Position = p, MaterialOverride = tlMat };
                    mi.SetMeta("no_outline", true);
                    v.AddChild(mi);
                }
                v._taillightMat = tlMat;
            }
            if (s.SeatModelFile != null)   // driver seat: the REAL ripped seat mesh, translated so its AABB centre lands at SeatModel (baked at its source vehicle) (strawberry: use src, not proc-gen)
            {
                var seatMesh = ContentProvider.ParseObj($"res://content/{s.SeatModelFile}");
                var mi = new MeshInstance3D { Mesh = seatMesh, MaterialOverride = SolidMat(new Color(0.22f, 0.22f, 0.24f)), Position = s.SeatModel - seatMesh.GetAabb().GetCenter() };
                mi.SetMeta("no_outline", true); v.AddChild(mi);
            }
            if (s.SteerModel != null && s.SteerAxis != Vector3.Zero)   // steering wheel: the REAL ripped wheel mesh, re-centred on the steer pivot so it turns 1:1 with the wheels about SteerAxis
            {
                var wMesh = ContentProvider.ParseObj($"res://content/{s.SteerModel}");
                v._steerPivot = new Node3D { Position = s.SteerPivot };
                v._steerAxis = s.SteerAxis.Normalized();
                var mi = new MeshInstance3D { Mesh = wMesh, MaterialOverride = SolidMat(new Color(0.13f, 0.11f, 0.08f)), Position = -wMesh.GetAabb().GetCenter() };
                mi.SetMeta("no_outline", true);
                v._steerPivot.AddChild(mi); v.AddChild(v._steerPivot);
            }
            if (v._sirenMat0 != null)   // emergency vehicle -> looping siren audio (master), silent until the lightbar's toggled on
            {
                v._sirenAudio = new AudioStreamPlayer3D { Stream = LoadWav("res://content/siren.wav"), UnitSize = 14f, MaxDistance = 120f, VolumeDb = 2f };
                v.AddChild(v._sirenAudio);
            }

            var spotPos = s.SpotPos ?? (v._autoSpot.Count > 0 ? v._autoSpot.ToArray() : null);   // authored, else derived from the lens meshes
            var tailPos = s.TailPos ?? (v._autoTail.Count > 0 ? v._autoTail.ToArray() : null);
            if (spotPos != null)   // headlights: source "Headlights" node -- 2 warm spot beams + 1 omni fill at the front, off until 'L'
            {
                var warm = v._lampTint;   // the EMITTER matches the lens it sits behind, per lamp shape
                v._headlights = new Node3D { Visible = false };
                foreach (var p in spotPos)
                {
                    var hs = new SpotLight3D { Position = p, SpotRange = 45f, SpotAngle = 25f, SpotAngleAttenuation = 1.3f, LightColor = warm, LightEnergy = 9f };
                    hs.AddToGroup("dynlight");   // spills onto the FP gun (light-scan)
                    v._headlights.AddChild(hs);
                    // Bind the emitter to the lens half on the same side, so shooting that lens kills THIS beam.
                    // Matched by the spot's own x sign rather than array order: SpotPos is authored per vehicle
                    // and nothing guarantees element 0 is the left one.
                    string want = p.X < 0f ? "headlight_l" : "headlight_r";
                    int li = v._lampLabels.IndexOf(want);
                    if (li >= 0 && v._lampLights[li] == null) v._lampLights[li] = hs;
                }
                if (s.OmniPos != Vector3.Zero)   // omni fill is OPTIONAL (OmniPos Zero = spots only) -- the semi drops it, its center glow read as a weird third headlight (strawberry)
                {
                    var hfill = new OmniLight3D { Position = s.OmniPos + Vector3.Up * 0.5f, OmniRange = 28f, LightColor = warm, LightEnergy = 0.8f };   // dim soft fill (raised above the seats so it doesn't glare)
                    hfill.AddToGroup("dynlight");
                    v._headlights.AddChild(hfill);
                    v._headlightFill = hfill;   // centre fill belongs to NEITHER side; killed only when both lamps are out
                }
                v.AddChild(v._headlights);
            }

            if (tailPos != null)   // running taillights: dim red spots at the rear (aim +Z, backward), on while driving
            {
                var red = new Color(0.996f, 0f, 0f);
                v._taillights = new Node3D { Visible = false };
                foreach (var p in tailPos)
                {
                    var ts = new SpotLight3D { Position = p, RotationDegrees = new Vector3(0f, 180f, 0f), SpotRange = 3f, SpotAngle = 72f, SpotAngleAttenuation = 0.6f, LightColor = red, LightEnergy = 2.2f };
                    v._taillights.AddChild(ts);
                    string wantT = p.X < 0f ? "taillight_l" : "taillight_r";
                    int ti = v._lampLabels.IndexOf(wantT);
                    if (ti >= 0 && v._lampLights[ti] == null) v._lampLights[ti] = ts;
                }   // WIDE + SHORT diffuse red glow, not a focused red-headlight beam (SpotRange 6->3, SpotAngle 35->72, soft edge) (strawberry)
                v.AddChild(v._taillights);
            }

            // Lamps are registered across two passes (lens meshes, then the emitters), so the broken array is
            // sized here, once both are in. Sizing it at the lens pass would leave a shorter array than the
            // label list and every IsLampBroken would silently read false.
            v._lampBroken = new bool[v._lampNodes.Count];
            if (v._lampNodes.Count > 0) v.ApplyLampState();

            if (s.Horn != null)   // horn: one-shot the .dat HornAudioClip (a shared CarHorn) on LMB
            {
                var hogg = ContentProvider.OggCached(ProjectSettings.GlobalizePath($"res://content/{s.Horn}"), loop: false);   // shared decoded stream (was a decode per vehicle)
                v._hornAudio = new AudioStreamPlayer3D { Stream = hogg, UnitSize = 12f, MaxDistance = 90f, VolumeDb = 4f };
                v.AddChild(v._hornAudio);
            }

            if (s.Sound != null)   // EngineRPMSimple: a looping engine clip (the prefab AudioSource) whose pitch + volume ride the RPM
            {
                AudioStream ogg = s.Sound.EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase)
                    ? PlayerController.LoadWavOneShot($"res://content/{s.Sound}", loop: true)   // retail rips (content/audio/vehicles) are wav
                    : ContentProvider.OggCached(ProjectSettings.GlobalizePath($"res://content/{s.Sound}"), loop: true);   // shared decoded stream, Loop baked into the cache key
                // HELICOPTERS CARRY. A car at 80 m is a car you have driven past; a helicopter is the thing you
                // hear long before you see it, and that is most of what makes one feel big. UnitSize is the
                // distance at which the attenuation curve starts, so raising BOTH is what actually extends the
                // audible range rather than just making it loud up close. (strawberry: "a lot louder and heard
                // from far away")
                //
                // PITCH FALLS WITH ROTOR SIZE, and the rule is physical rather than picked: blade TIP speed is
                // roughly constant across helicopters, so rotational frequency -- and with it the blade-passing
                // thud you actually hear -- goes as 1/R. Square-rooted to tame the extremes and clamped, because
                // the fleet's radii span 2.65 m to 5.90 m and the raw ratio would put the minicopter an octave
                // up. Referenced to the Huey, which is the aircraft the clip was recorded from. Without this the
                // four HeliBase airframes shared one IdlePitch, so the tiny Hummingbird sounded exactly like the
                // 21-tonne Skycrane. (strawberry: "heavier helis should alter the sound too")
                float sizePitch = HeliSizePitch(s);
                v._engineAudio = new AudioStreamPlayer3D { Stream = ogg, UnitSize = s.Heli ? 52f : 26f, MaxDistance = s.Heli ? 800f : 300f, PitchScale = s.IdlePitch * sizePitch, VolumeDb = Mathf.LinearToDb(s.IdleVolume * EngineVolumeBoost * (s.Heli ? 2.0f : 1f)), Autoplay = true };
                if (s.Heli) { v._idlePitch = s.IdlePitch * sizePitch; v._maxPitch = s.MaxPitch * sizePitch; }
                v.AddChild(v._engineAudio);   // Autoplay starts the loop when the vehicle enters the scene tree
            }
            // RETAIL IGNITION (strawberry 2026-09-04 "source the ignition sound from the official game files"): every ground vehicle .dat in the
            // retail Bundles points at Sounds/CarIgnition.mp3 (49 of them; the tank has its own) -> ripped from core.masterbundle as car_ignition.wav (2.04 s).
            string ignSound = s.IgnitionSound ?? ((!s.Heli && !s.Plane) ? "car_ignition.wav" : null);
            if (ignSound != null)   // one-shot spin-up; NOT autoplayed -- StepHeli fires it on a start
            {
                AudioStream ig = ignSound.EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase)
                    ? PlayerController.LoadWavOneShot($"res://content/{ignSound}")
                    : ContentProvider.OggCached(ProjectSettings.GlobalizePath($"res://content/{ignSound}"), loop: false);
                if (ig != null)
                {
                    // (Loop=false is part of the cached stream)
                    // The clip's own length becomes the spin-up gate, so "the rotor is ready" and "the start-up
                    // sound has finished" are the same instant by construction rather than two numbers someone
                    // has to keep in step.
                    v._ignitionAudio = new AudioStreamPlayer3D { Stream = ig, UnitSize = s.Heli ? 34f : 10f, MaxDistance = s.Heli ? 520f : 80f, PitchScale = HeliSizePitch(s), VolumeDb = Mathf.LinearToDb(EngineVolumeBoost * (s.Heli ? 2.0f : 1f)) };
                    // The GATE follows the pitch. PitchScale resamples the clip, so a Skycrane's start-up at
                    // 0.87 actually runs 8.10 / 0.87 = 9.3 s of wall time -- gating on the unpitched length
                    // would cut a heavy machine's thrust in before its own start-up had finished.
                    v._ignitionLen = (float)ig.GetLength() / Mathf.Max(HeliSizePitch(s), 0.01f);
                    v.AddChild(v._ignitionAudio);
                }
            }

            // damage smoke + explosion fire from the engine bay (source: smoke_0/1 at health thresholds, fire + Fire light on explode)
            var firePos = new Vector3(0f, 1.24f, -1.70f);   // source Fire node (0,1.238,1.703), Z negated
            v._firePos = firePos;   // remembered so the explosion plume can emit from the engine bay in world-space
            v._smoke  = MakeSmoke("veh_smoke_1.png", new Color(0.55f, 0.55f, 0.55f), 2.2f, 2.2f, 20, false, 2.0f, 4.0f);   // light damage smoke (hp<200); src startSize 2-4m
            v._smoke0 = MakeSmoke("veh_smoke_0.png", new Color(0.30f, 0.29f, 0.27f), 2.9f, 2.9f, 28, false, 2.0f, 4.0f);   // heavy smoke (hp<100); src startSize 2-4m
            v._fire   = MakeSmoke("veh_fire.png",   new Color(1f, 0.72f, 0.32f),    0.7f, 4.5f, 30, true,  1.0f, 2.0f);   // explosion fire; src startSize 1-2m
            v._smoke.Position = firePos; v._smoke0.Position = firePos; v._fire.Position = firePos;
            v.AddChild(v._smoke); v.AddChild(v._smoke0); v.AddChild(v._fire);
            if (!s.Heli && !s.Plane && s.BoxSize != Vector3.Zero)   // EXHAUST: a small grey puff-stream off the tailpipe while the engine runs (strawberry 2026-09-04 "so you can see visually when its running")
            {
                v._exhaust = MakeSmoke("veh_smoke_1.png", new Color(0.66f, 0.66f, 0.64f, 0.8f), 1.3f, 1.3f, 14, false, 0.14f, 0.34f);
                v._exhaust.Direction = new Vector3(0f, 0.35f, 1f); v._exhaust.Spread = 16f; v._exhaust.Gravity = new Vector3(0f, 0.7f, 0f);   // out the back, drifting up
                v._exhaust.Position = new Vector3(+(s.BoxSize.X * 0.5f - 0.3f), Mathf.Max(0.22f, s.BoxCenter.Y - s.BoxSize.Y * 0.5f + 0.18f), s.BoxCenter.Z + s.BoxSize.Z * 0.5f - 0.05f);   // rear-left, low: no per-vehicle pipe data, this is where a tailpipe sits on the ripped bodies   // RIGHT rear: the tailpipes are on the right now that the bodies are un-mirrored (master 2026-09-05 "exhaust emitters are on the opposite side")
                v.AddChild(v._exhaust);
            }
            // Per-WHEEL tire dust (source Wheel.cs TireMotionEffectInstance): one emitter per wheel, spawned at that wheel's
            // ground CONTACT point, aimed UP at low speed -> tilting ~45deg backward at speed, only while grounded + moving.
            // NOTE: vanilla assigns NO TireMotionEffect to any physics material (the whole system is WIP "WipDoNotUse"), so
            // vanilla actually kicks up NOTHING -- this is an ENHANCEMENT driven by our Surf tag: soft ground (grass/dirt/sand)
            // puffs a tinted cloud; road/metal/wood stay clean.
            v._wheelDust = new CpuParticles3D[nw];
            v._wheelSurf = new PlayerController.Surf[nw];
            for (int i = 0; i < nw; i++)
            {
                var d = MakeSmoke("veh_smoke_1.png", new Color(0.55f, 0.50f, 0.40f), 0.55f, 1.4f, 8, false, 0.2f, 0.55f);
                d.Spread = 22f; d.Gravity = new Vector3(0f, -3f, 0f);   // fall back to the ground quickly
                v.AddChild(d);
                v._wheelDust[i] = d;
                v._wheelSurf[i] = PlayerController.Surf.Grass;
            }
            v._fireLight = new OmniLight3D { Position = firePos, OmniRange = 8f, LightColor = new Color(1f, 0.55f, 0.2f), LightEnergy = 0f, Visible = false };
            v._fireLight.AddToGroup("dynlight");   // a burning wreck spills onto the FP gun (light-scan)
            v.AddChild(v._fireLight);
            v._explosionAudio = new AudioStreamPlayer3D { Stream = ContentProvider.OggCached(ProjectSettings.GlobalizePath("res://content/explosion.ogg"), loop: false), UnitSize = 20f, MaxDistance = 200f, VolumeDb = 6f };   // boom on explode
            v.AddChild(v._explosionAudio);
            v.Brake = s.Brake * HandbrakeScale; v._parked = true;   // spawns parked: brake on + freezes once settled so it holds ride height without jitter (released once driven)
            v._alarmed = GD.Randf() < 0.05f;   // 5% of spawned cars are "alarmed" -- proximity/damage sets off the alarm loop (master). Only real Vehicles roll; a client's PUPPET is told via FlagAlarmed, so the two sides cannot disagree about which cars have alarms.
            ApplyVehicleCull(v);   // driveable vehicles had NO distance cull -> rendered full-detail at every range (master); cap them like props
            FinaliseHitboxLayers(v);
            return v;
        }

        // Driveable vehicles build full-detail MeshInstance3Ds with NO VisibilityRange, so a parked truck on the far
        // side of PEI drew every triangle at full quality (master: "all vehicles render full quality"). Props cull via
        // LodTable; a vehicle has no LodTable entry, so apply retail's RegularObjectMaxDistance (min(defaultCull, 447))
        // as a flat distance cull to every mesh under it. The DRIVEN vehicle sits at ~0 distance so it always renders;
        // parked/other vehicles stop drawing past the cap. (FOLLOW-UP: a low-poly LOD SWAP via ImporterMesh.GenerateLods
        // -- decimate + keep visible instead of hard-culling -- and a size-scaled cap so the 67m ship isn't capped like
        // a hatchback.)
        static void ApplyVehicleCull(Node root)
        {
            float cull = Mathf.Min(LodTable.DefaultCullDistance, 447f);
            foreach (var n in root.FindChildren("*", "MeshInstance3D", true, false))
                if (n is MeshInstance3D m)
                {
                    m.VisibilityRangeEnd = cull;
                    m.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
                }
        }

        // throttle/brake/steer in [-1,1]; applies the source .dat handling: hard Speed_Max/Min caps + speed-dependent
        // steering (Steer_Max at rest -> Steer_Min at full speed), so the observable handling matches the game.
        // ---- ROTARY WING ------------------------------------------------------------------------------
        const float SpoolUpSeconds = 3.2f, SpoolDownSeconds = 5.5f;   // cold start has to wind up before it will fly
        const float CollectiveRate = 0.55f;         // how fast W/S drive the throttle, per second of held key
        const float CollectiveReturnRate = 0.40f;   // how fast it springs back to idle once you let go
        /// <summary>Hands-off collective, as a fraction of the power that exactly cancels gravity. Below 1 on
        /// purpose: "a bit below the amount of thrust required to counteract gravity" (VoX), so letting go sinks
        /// you slowly rather than parking you in a perfect hover.</summary>
        const float IdleHoverFraction = 0.92f;
        /// <summary>Collective that would exactly hold a hover at full rotor spool, from THIS spec's thrust:
        /// thrust * c = g. Derived, not hardcoded, so retuning HeliThrust moves the idle point with it.</summary>
        /// <summary>Collective needed to hold a hover RIGHT HERE -- including ground effect, which is the whole
        /// reason this reads a cached field instead of just dividing by thrust.
        ///
        /// The hands-off spring targets IdleHoverFraction of this, and VoX's rule is that hands off gives a
        /// gentle sink: "a bit below the amount of thrust required to counteract gravity". Near the ground,
        /// the thrust required to counteract gravity is LESS -- so a fixed 0.92 * (g / thrust) stops being a
        /// bit below hover and becomes comfortably above it. Measured: a parked minicopter with the engine
        /// idling generated 9.016 * 1.333 = 12.0 against a g of 9.8 and floated off the ground, which broke
        /// the turbulence test's grounded subject and would have had parked helicopters drifting into the sky.
        ///
        /// Making the trim ground-effect-aware fixes that WITHOUT capping the effect: hands-off lift works out
        /// to 0.92 * g exactly, at any height, while collective you actually pull still gets the full cushion.
        /// The alternative was clamping ground effect to about 1.06 -- the largest value that leaves the 8.7 %
        /// idle margin intact -- which preserves the same behaviour by deleting the feature.
        ///
        /// The value can be one physics frame stale, since DriveHeli and StepHeli are not ordered relative to
        /// each other. Ground effect changes over metres of altitude, so a frame of lag is not observable.</summary>
        float HoverCollective => _heliThrust > 0.01f
            ? Mathf.Clamp(9.8f / (_heliThrust * Mathf.Max(_geApplied, 0.01f)), 0f, 1f) : 0f;
        float IdleCollective => HoverCollective * IdleHoverFraction;
        // Angular ACCELERATION at full deflection (rad/s^2), not a target rate -- these become torque against
        // the body's inertia, so the airframe builds up to a rotation and keeps it. Higher than the old rate
        // numbers because reaching a given spin now takes time instead of being assigned.
        const float HeliPitchRate = 2.30f, HeliRollRate = 2.90f, HeliYawRate = 2.10f;
        /// <summary>Inertia per kg of vehicle mass (m^2). Sets how hard the airframe is to spin up and, once
        /// spinning, how long it carries. Tuned by feel rather than derived, because the collision boxes that
        /// would otherwise define it are a crude stand-in for an open tube frame.</summary>
        const float HeliInertiaPerKg = 0.9f;

        // ---- FIXED WING (EEngine.PLANE) ---------------------------------------------------------------
        const float PlaneThrottleUp = 0.6f, PlaneThrottleDown = 0.35f;   // W ramps the throttle SETTING up fast, S bleeds it down slower; hands-off holds (sticky throttle)
        const float PlaneDrag = 0.03f;             // parasitic airflow drag -> throttle sets a real top speed, and a dead-engine plane glides down instead of bricking
        const float PlaneCtrlSpeedFrac = 0.35f;    // fraction of target airspeed at which elevator/aileron reach FULL authority (below it they're mushy -> a real rotate-at-speed on takeoff)
        const float PlaneWaterRollDamp = 3.2f;     // extra ROLL-rate damping while afloat: settles a wave-induced lean fast (the low float drag that lets it accelerate for takeoff left roll underdamped -> it lingered leaning ~5deg, "wants to tip over")
        const float PlaneBankComp = 0.75f;         // bank-lift compensation (master "if i bank sharply i lose height sooo fast"): a real coordinated turn needs back-pressure to keep the vertical lift up; auto-apply this FRACTION of it (1/cos(bank)) so a hard bank doesn't drop like a stone. 0 = fully realistic (drops), 1 = altitude-holding arcade turn.
        const float PlaneGroundRotate = 2.6f;      // WHEELED plane takeoff: the gear holds the airframe rigidly level + the Inertia-based elevator is too weak to lift the nose against the weight on the wheels. At takeoff speed, back-stick adds a DIRECT nose-up torque (a real elevator makes a big tail-download at speed) so it ROTATES off the runway. Fades in with airspeed, on the ground only.
        const float PlaneStability = 1.6f;         // aerodynamic (weathervane) stability: how hard the tail pulls the NOSE back onto the airflow. This is what makes a plane statically stable + stops a held elevator over-rotating; it aligns pitch+yaw only, never roll, so bank-to-turn survives
        // AEROFOIL: lift comes from ANGLE OF ATTACK (nose above the airflow), not raw speed -- so the plane trims
        // to level at the AoA where lift == weight, climbs when you pull, and STALLS (lift collapses -> mush/drop)
        // if you pull past the stall angle. cl = Cl0 + slope*AoAdeg up to the stall, then falls away.
        const float PlaneStallDeg = 15f;           // stall angle of attack (deg)
        const float PlaneCl0 = 0.34f;              // lift coefficient at zero AoA (wing camber) -> needs a few deg nose-up to hold level, like a real trim
        const float PlaneClSlope = 0.09f;          // lift-curve slope per degree of AoA (up to the stall)
        const float PlaneClMax = 1.7f;             // clamp (== Cl0 + stall*slope): the peak just before the stall

        /// <summary>The pilot's held flight controls. <paramref name="collective"/> is +1 while W is held, -1
        /// while S is held, 0 with neither.
        ///
        /// THE THROTTLE IS SPRING-LOADED, NOT STICKY, and it does not return to zero -- it returns to just under
        /// the power needed to hold a hover. VoX 2026-08-16: "if the player doesnt have either pressed then the
        /// copter idles at a bit below the amount of thrust required to counteract gravity ... s should reduce
        /// the thrust to 0 but only when actively pressed, and w should increase the trust to maximum but again
        /// only when pressed."
        ///
        /// That is a deliberate replacement for the sticky Rust throttle this shipped with, and it is a nicer
        /// resting state than either extreme: hands off, the machine sinks gently instead of either climbing
        /// away on a throttle you forgot about or dropping out of the sky. The idle point is derived from the
        /// spec's own thrust rather than hardcoded, so retuning HeliThrust cannot silently make hands-off mean
        /// "climb" on one airframe and "plummet" on another.
        ///
        /// This is the single flight-input seam, the same way <see cref="Drive"/> is for cars: SP calls it from
        /// the input path, and the MP fallback maps its 3-axis DriveInput onto it (throttle -> collective,
        /// steer -> yaw). Pitch/roll never need to ride the input wire, because once a client is predicting it
        /// reports the resulting TRANSFORM and the server adopts that whole basis.</summary>
        public void DriveHeli(float collective, float yaw, float pitch, float roll, double delta)
        {
            if (_exploded) { _inCollective = 0f; _inYaw = _inPitch = _inRoll = 0f; return; }
            _parked = false;
            if (Freeze) { Freeze = false; }   // any control input wakes a settled machine
            float target = collective > 0.05f ? 1f : collective < -0.05f ? 0f : IdleCollective;
            float rate = Mathf.Abs(collective) > 0.05f ? CollectiveRate : CollectiveReturnRate;
            _inCollective = Mathf.MoveToward(_inCollective, target, rate * (float)delta);
            _inYaw = Mathf.Clamp(yaw, -1f, 1f);
            _inPitch = Mathf.Clamp(pitch, -1f, 1f);
            _inRoll = Mathf.Clamp(roll, -1f, 1f);
        }

        /// <summary>The plane's held controls (master: W/S throttle, A/D tail rudder, mouse L/R roll, mouse
        /// up/down pitch). <paramref name="throttle"/> is a STICKY setting -- W ramps it up, S bleeds it down,
        /// hands-off holds it (a plane does not spring its throttle back to idle like a helicopter's collective).
        /// Yaw is the rudder (A/D), pitch/roll come off the mouse; invert-Y is applied by the caller before this,
        /// so a scripted flight harness can inject the axes raw.</summary>
        public void DrivePlane(float throttle, float yaw, float pitch, float roll, double delta)
        {
            if (_exploded) { _inCollective = 0f; _inYaw = _inPitch = _inRoll = 0f; return; }
            _parked = false;
            if (Freeze) Freeze = false;   // any input wakes a settled plane
            // sticky throttle: hold the current setting when hands-off, ramp toward 1 on W, toward 0 on S
            _rawThrottle = throttle;   // remember the raw W/S axis so the ground code can tell 'S held' (reverse) from 'throttle spooled to 0'
            float target = throttle > 0.05f ? 1f : throttle < -0.05f ? 0f : _inCollective;
            float rate = throttle > 0.05f ? PlaneThrottleUp : throttle < -0.05f ? PlaneThrottleDown : 0f;
            _inCollective = Mathf.MoveToward(_inCollective, target, rate * (float)delta);
            _inYaw = Mathf.Clamp(yaw, -1f, 1f);
            _inPitch = Mathf.Clamp(pitch, -1f, 1f);
            _inRoll = Mathf.Clamp(roll, -1f, 1f);
        }

        /// <summary>Cut to idle and let it settle -- the heli equivalent of <see cref="Park"/>. The collective
        /// does NOT snap to zero: an unmanned helicopter in the air keeps whatever power it had and descends as
        /// the rotor winds down, rather than being deleted out of the sky the instant the pilot steps out.</summary>
        public void ParkHeli()
        {
            _parked = true;
            _inYaw = _inPitch = _inRoll = 0f;
        }

        void StepHeli(float dt)
        {
            // fuel + explosion lifecycle, same rules the wheeled path runs
            if (EngineOn && Fuel > 0f && !InfiniteFuel) Fuel = Mathf.Max(0f, Fuel - FuelBurn * dt);
            if (EngineOn && FuelMax > 0f && Fuel <= 0f) EngineOn = false;
            if (_deadTimer > 0f) { _deadTimer -= dt; if (_deadTimer <= 0f) Explode(); }

            // ROTOR SPOOL. Thrust scales with the SQUARE of it, so a cold start genuinely cannot lift until the
            // disc is up -- and cutting the engine in the air leaves you autorotating down, not dropping like a
            // brick. Spool-down is slower than spool-up for the same reason.
            float want = (EngineOn && !_exploded && (Fuel > 0f || InfiniteFuel)) ? 1f : 0f;
            // WIND UP THROUGH THE START-UP CLIP. The spin-up used to run on its own fixed SpoolUpSeconds while
            // the ignition sound played to a completely independent clock, so the rotor could be at full song
            // with the starter still audible, or ready long before it. Driving the ramp off the clip's own
            // length makes the two the same event. (strawberry: "rotors should ramp up during the ignition
            // sound, and we should only start generating thrust after the sound finishes")
            float spoolUp = !DebugInstantStart && _ignitionLen > 0.1f ? _ignitionLen * IgnitionThrustFraction : SpoolUpSeconds;
            _rotorRpm = Mathf.MoveToward(_rotorRpm, want, dt / (want > _rotorRpm ? spoolUp : SpoolDownSeconds));
            if (_ignitionLeft > 0f) _ignitionLeft = Mathf.Max(0f, _ignitionLeft - dt);

            // NAV LIGHTS RUN OFF THE ENGINE, not the rotor -- they are electrical, and a parked machine with the
            // switches off is dark (strawberry: "make the heading lights only on when the heli's engine is on.
            // make sure the heading lights turn off when the heli is destroyed"). A wreck is dark for the more
            // obvious reason. This is the opposite rule from the beacon below, which follows the DISC.
            bool navOn = EngineOn && !_exploded && Health > 0f;
            for (int i = 0; i < _navMats.Count; i++) _navMats[i].EmissionEnergyMultiplier = navOn ? 2.6f : 0f;
            for (int i = 0; i < _navOmnis.Count; i++) _navOmnis[i].LightEnergy = navOn ? 1.4f : 0f;

            // The beacon runs off the ROTOR, not the ignition switch: its job is to say "this disc is live",
            // so it keeps flashing through a spool-down and stops only once the blades actually have.
            if (_beaconMat != null)
            {
                bool armed = _rotorRpm > 0.02f && !_exploded;
                _beaconTimer = armed ? (_beaconTimer + dt) % BeaconPeriod : 0f;
                bool lit = armed && _beaconTimer < BeaconFlash;
                _beaconMat.EmissionEnergyMultiplier = lit ? 6f : 0f;
                _beaconMat.AlbedoColor = lit ? new Color(1f, 0.35f, 0.35f) : new Color(0.28f, 0.05f, 0.05f);
                if (_beaconLight != null) _beaconLight.LightEnergy = lit ? 3.2f : 0f;
            }
            if (_rotorNode != null)   // visual only -- the flight model never reads blade phase
            {
                // STOPS when idle, on death, and slows as the rotor is damaged (strawberry: "rotor should stop
                // when idle ... stop rotor on death ... lower each rotor's rpm when they are hurt"). The old
                // constant +2.5 term meant the disc never actually stopped -- a parked, dead-engine machine
                // sat there slowly turning its blades forever.
                float spinScale = _exploded ? 0f : _rotorRpm * (0.30f + 0.70f * MainRotorNorm);
                _rotorSpin += dt * spinScale * 46f;
                _rotorNode.Rotation = new Vector3(0f, _rotorSpin, 0f);
            }
            if (_tailRotorNode != null)
                // COMPOSE the roll with the spin. Assigning `Rotation = (0, spin, 0)` here -- which is what this
                // line used to do -- overwrote the whole basis every tick and wiped the 90 deg roll set at build
                // time, so the tail rotor lay flat like a second main rotor. The comment on that line asserted
                // "pivot is rolled 90 deg, so local Y is still the spin axis" while the assignment beside it
                // destroyed exactly that; strawberry spotted it in the first minute of flying
                // ("the tail rotor needs to be rotated + 90 deg roll").
            {
                // The tail turns on its OWN health, so a shredded tail rotor visibly lags a healthy main --
                // which is the tell that says which one you lost.
                _tailSpin += dt * (_exploded ? 0f : _rotorRpm * (0.30f + 0.70f * TailRotorNorm)) * 120f;
                _tailRotorNode.Basis = new Basis(Vector3.Back, Mathf.DegToRad(TailRotorRollDegrees))
                                     * new Basis(Vector3.Up, _tailSpin);
            }
            // Swap blades <-> blur disc by rotor speed, which is why the retail prefab ships both meshes. Below
            // the threshold you see two blades sitting still; above it, the smear plate.
            // THE REAL BLADES SPIN, ALWAYS (strawberry: "instead of a billboard could we actually spin the real
            // rotor mesh(es)"). The retail prefab ships a separate blur PLATE and swapped to it above
            // DiscSwapSpool, which is the cheap trick -- it hides blade geometry behind a translucent disc the
            // moment the rotor is up, so at any real rotor speed you were looking at a smear, not an aircraft.
            // The mesh is already being turned every tick; it just was not being drawn. The plates stay in the
            // scene but never show, so the extractor and the meshes do not have to change.
            if (_bladesMesh != null) _bladesMesh.Visible = true;
            if (_discMesh != null)
            {
                _discMesh.Visible = false;
            }

            // ENGINE AUDIO rides the ROTOR, not an RPM the machine does not have. The shared car path drives
            // pitch from gear/wheel RPM, which on a helicopter reads as an engine revving while the disc is
            // still winding up. Sounds are the retail Unturned clips (HelicopterIgnition + Engine_Heli),
            // extracted by cow tools.
            if (_engineAudio != null)
            {
                if (_rotorRpm > 0.01f)
                {
                    _engineAudio.PitchScale = Mathf.Lerp(_idlePitch, _maxPitch, _rotorRpm);
                    _engineAudio.VolumeDb = Mathf.LinearToDb(Mathf.Lerp(_idleVol * 0.35f, _maxVol, _rotorRpm) * EngineVolumeBoost);
                    if (!_engineAudio.Playing) _engineAudio.Play();
                }
                else if (_engineAudio.Playing) { _engineAudio.VolumeDb = -80f; _engineAudio.Stop(); }
            }
            // IGNITION is a one-shot on the START of a spin-up, latched so it fires once per start rather than
            // every tick the rotor happens to be below speed.
            if (_ignitionAudio != null)
            {
                bool starting = want > 0f && _rotorRpm < 0.05f;
                if (starting && !_ignitionFired) { _ignitionFired = true; _ignitionAudio.Play(); _ignitionLeft = DebugInstantStart ? 0f : _ignitionLen * IgnitionThrustFraction; }
                else if (want <= 0f && _rotorRpm < 0.01f) _ignitionFired = false;   // fully stopped -> armed again
            }

            // A wreck is just a falling body -- but it still falls through air, and with LinearDamp now 0 it
            // would free-fall unbounded instead. Applied ISOTROPICALLY, and above this return rather than
            // below it, which reproduces the old engine damping to within 1 %: terminal fall stays at
            // g / HeliHeaveDamp = 9.8 / 0.45 = 21.8 m/s, which is what a wreck has ALWAYS fallen at here --
            // 0.35 on the body plus Godot's 0.1 project default under Combine. A tumbling airframe has no
            // meaningful shaft axis to hang a heave term on, so this one is isotropic.
            if (_exploded) { ApplyCentralForce(-LinearVelocity * (HeliHeaveDamp * Mass)); return; }

            Basis b = GlobalTransform.Basis;
            float spool = _rotorRpm * _rotorRpm;

            // LIFT along the BODY up axis. This one line is the whole Rust feel: you do not steer a helicopter,
            // you tilt it and the lift vector takes you with it.
            // BLADE STRIKES. While anything is inside a disc, that rotor grinds down on a fixed interval --
            // an interval rather than per-frame damage so the cost of clipping a tree does not depend on the
            // framerate, and so brushing something is survivable while sitting in it is not.
            // A STOPPED ROTOR NEITHER CUTS NOR IS CUT (strawberry 2026-08-16: "stop rotor damage
            // recieve/give from happening with the rotor off"). Blades have to be TURNING to do this -- a
            // parked machine resting in a bush was grinding its own rotor away, and shredding the bush, purely
            // by being parked there. Gated on spool rather than on EngineOn so a rotor still coasting down
            // after a shutdown stays dangerous, which is true of the real thing.
            _mainStrikeCd -= dt; _tailStrikeCd -= dt;
            bool bladesLive = _rotorRpm > BladeStrikeMinSpool;
            if (bladesLive && _mainDiscArea != null && _mainStrikeCd <= 0f && DiscStruck(_mainDiscArea, MainBladePropDamage))
            { _mainStrikeCd = BladeStrikeInterval; DamageMainRotor(BladeStrikeDamage); BladeStrikeFx(_mainStrikeFx); }
            if (bladesLive && _tailDiscArea != null && _tailStrikeCd <= 0f && DiscStruck(_tailDiscArea, TailBladePropDamage))
            { _tailStrikeCd = BladeStrikeInterval; DamageTailRotor(TailStrikeDamage); BladeStrikeFx(_tailStrikeFx); }

            // ---- CRASH (strawberry 2026-08-16: "make the vehicle EXPLODE if it hits anything at a
            // considerable speed. bonking with particles and taking damage if its below the explosion
            // threshold").
            //
            // FULL 3-D SPEED, unlike the wheeled detector, which measures horizontal only and says so: "so the
            // spawn drop doesn't count". That is right for a car, whose crashes are all lateral, and exactly
            // wrong for a helicopter, whose defining crash is straight down. Taking the vertical component back
            // means the spawn drop DOES count, so the guard is _spawnGrace instead -- a real condition (the
            // machine has only just been placed) rather than an axis that happened to exclude it.
            float curSpeed = LinearVelocity.Length();
            float decel = _prevSpeed - curSpeed;
            // HOW FAST WERE YOU GOING WHEN YOU HIT -- a short decaying peak, not last tick's speed. With
            // ContinuousCd the solver bleeds a fast impact off across several ticks, so by the tick the
            // deceleration is large enough to notice, _prevSpeed has ALREADY dropped well below the real
            // approach speed. A 25 m/s dive was being detected at ~11 and written off as a survivable bonk
            // (111 damage instead of a fireball). The peak decays at 12 m/s per second, so it reflects the
            // last moment of flight rather than the whole trip.
            _recentTopSpeed = Mathf.Max(curSpeed, _recentTopSpeed - dt * 6f);   // 6, not 12: a faster decay shaved real speed off impacts the solver resolved over several ticks
            if (_crashCd > 0f) _crashCd -= dt;
            if (!_exploded && _spawnGrace <= 0f && _crashCd <= 0f && _recentTopSpeed > HeliBonkSpeed && decel > 200f * dt)
            {
                _crashCd = 0.25f;   // one impact per contact, not one per tick of a scrape
                DebugLastImpactSpeed = _recentTopSpeed;
                if (_recentTopSpeed >= HeliCrashExplodeSpeed) Explode();   // hard hit: straight to the fireball, no 4 s fuse
                else { TakeDamage(decel * 18f); BonkFx(); }
            }
            _prevSpeed = curSpeed;

            // A DEAD ROTOR CUTS THE ENGINE ONCE YOU ARE DOWN (strawberry 2026-08-16: "kill the engine if main
            // rotor dead once you touch the ground. same with tail"). Deliberately gated on being GROUNDED
            // rather than firing the moment the rotor dies: cutting the engine mid-air would take away the
            // autorotation you need to survive the landing, turning a recoverable failure into a guaranteed
            // kill. Once you are down it stays down -- you are not flying that machine again without repairs.
            if (EngineOn && (MainRotorDead || TailRotorDead) && GroundedByRay() && LinearVelocity.LengthSquared() < 4f)
                EngineOn = false;

            UpdateRotorFx();

            // A DAMAGED MAIN ROTOR MAKES LESS LIFT, and a dead one makes none. "main rotor hp low -> reduced
            // thrust ... main rotor dead -> no more gaining vertical thrust, quickly lose height" -- with zero
            // lift the machine simply falls, which is the quick loss of height without needing a special case
            // to shove it downward.
            float mainEff = MainRotorNorm;
            float lift = _heliThrust * spool * _inCollective * (0.20f + 0.80f * mainEff);
            // NO THRUST UNTIL THE STARTER HAS FINISHED. Zeroed at the SOURCE rather than at the ApplyForce so
            // that everything downstream -- the tilt loss, the dead-tail clamp, ETL, ground effect -- sees a
            // machine making no lift, instead of each having to know about the gate. The disc still turns and
            // still makes noise while this holds; it just is not flying yet.
            if (_ignitionLeft > 0f) lift = 0f;
            // ---- EFFECTIVE TRANSLATIONAL LIFT + GROUND EFFECT, both multipliers on rotor thrust.
            //
            // APPLIED HERE, ABOVE THE DEAD-TAIL CLAMP, and the order is the whole point. That clamp is an
            // absolute ceiling encoding a signed-off rule -- a dead tail must prevent gaining height. A
            // multiplier applied AFTER it lifts the machine straight back through the ceiling, and ground
            // effect would do it exactly when the pilot is nearest the ground and closest to surviving. So
            // these go in first and the clamp still has the last word.
            //
            // Capped as a PRODUCT, not individually: it is the combination that out-climbs the MP envelope
            // (1.05 x 1.333 = 1.40 against the Hind's 1.26), and capping each factor separately would let the
            // product through.
            Vector3 hvel = LinearVelocity;
            var hflat = new Vector3(hvel.X, 0f, hvel.Z);
            float flatSpeed = hflat.Length();
            float etl = 1f + EtlGain * Mathf.Clamp((flatSpeed - EtlOnset) / (EtlFull - EtlOnset), 0f, 1f);
            _groundEffect = GroundEffect();   // ONE raycast per tick, two readers
            float liftMul = Mathf.Min(etl * _groundEffect, _heliLiftCap);
            // THE GROUND-EFFECT SHARE ACTUALLY DELIVERED, which is what the hands-off trim has to cancel --
            // and NOT the raw factor. When the cap binds they are different numbers, and dividing the trim by
            // the raw one over-trims: on a Hind parked in ground effect (raw 1.333 against a cap of 1.261) the
            // hands-off sink came out 63 % HARDER near the deck than at altitude, which is ground effect
            // running backwards, in the flare, on the airframe least able to absorb it. ETL is deliberately
            // left OUT of the trim -- it should still lighten a hands-off machine at speed, and the sink at
            // 0.92 g * etl stays a sink for any gain under 0.087.
            _geApplied = etl > 0.01f ? liftMul / etl : _groundEffect;
            lift *= liftMul;
            // A DEAD TAIL ALSO GROUNDS YOU (strawberry: "dead tail should also have the same effect as
            // killmain of preventing gaining height"). Capped just under g rather than zeroed like a dead main:
            // the tail is not what lifts you, so losing it should leave you able to sink under some control
            // while spinning, not drop like a machine with no rotor at all. Either way you cannot climb out.
            if (TailRotorDead && !MainRotorDead) lift = Mathf.Min(lift, 9.8f * 0.95f);
            // BOTH ROTORS GONE -> the airframe is finished ("if both rotors are destroyed, kill the main body
            // too"). Routed through TakeDamage so it uses the ordinary death path -- burn timer, explosion,
            // driver ejection -- instead of inventing a second way for a vehicle to die.
            if (MainRotorDead && TailRotorDead && !_exploded && Health > 0f) TakeDamage(Health);
            // TILT COSTS LIFT, twice over. Thrusting along the body axis already gives the free cosine (a
            // 30 deg nose-down keeps only 87 % of its thrust pointing up); this takes a further bite on top,
            // so committing to a fast nose-down run actually costs you height instead of being free speed.
            lift *= 1f - TiltThrustLoss * (1f - Mathf.Clamp(b.Y.Y, 0f, 1f));
            // THRUST ALONG THE SHAFT, unmodified. This one line is the whole Rust feel: you do not steer a
            // helicopter, you tilt it and the lift vector takes you with it. The horizontal half of this vector
            // used to be split out and multiplied by ForeAftBoost / LateralBoost so that leaning into a run
            // built real momentum; that asymmetry now lives in the DRAG below, where a fuselage's own geometry
            // puts it, so the thrust can go back to being a vector.
            if (lift > 0f) ApplyCentralForce(b.Y * (lift * Mass));

            // ---- RESISTANCE. Two axes, two mechanisms, two laws -- the reasoning is at HeliHeaveDamp.
            Vector3 vel = hvel;   // read once, above, so ETL and drag cannot disagree about how fast we are going

            // VERTICAL: linear heave damping, in the WORLD frame. Deliberately NOT the body shaft axis, which
            // is the more obviously physical choice and was what the physics review recommended -- heave
            // damping is a rotor property and really does follow the disc. The reason it is world-aligned here
            // is that the body-frame form scales vertical damping by cos^2(tilt), which silently retunes every
            // number derived from this constant: terminal climb would rise ~22 % at an ordinary 25 deg cruise,
            // straight into HeliClimbMax and the server's ZERO-slack vertical check. World-aligned keeps "the
            // vertical axis is unchanged, only who applies the force changed" literally true, which is the
            // stronger requirement of the two. The cost is a coordinate artefact at extreme bank; the benefit
            // is that six calibrated numbers stay valid.
            float heave = HeliHeaveDamp * HeaveDampScale;
            if (ShaftAlignedDescent && vel.Y < 0f)
            {
                // cos^2 of the tilt. SQUARED, not clamped to [0,1]: an INVERTED disc is still a flat disc facing
                // the airflow, so b.Y.Y = -1 has to read as 1, not 0. Clamping first gave an upside-down
                // helicopter zero vertical resistance.
                float shaftUp = b.Y.Y;
                heave *= shaftUp * shaftUp;

                // ---- ENVELOPE FLOOR. Everything below exists because VehicleReplication validates the fall rate
                // with ZERO slack (the horizontal check gets 1.25; the vertical gets none), and a failure is not a
                // soft correction -- it teleports the pilot to the last good pose and resumes them FROM REST.
                //
                // THREE THINGS THE FIRST VERSION GOT WRONG, all of which let this feature INTRODUCE violations
                // that did not exist before it:
                //
                // 1. IT USED g AS THE WHOLE DOWNWARD ACCELERATION. Inverted, the tilt loss above clamps at zero
                //    so the rotor keeps 45 % of its thrust, and :3615 applies it along b.Y -- pointing at the
                //    ground, ADDING to gravity -- while cos^2 is near its minimum. Measured on the shipped
                //    constants: 58 m/s on a Huey against a 40 cap (+46 %), 66 vs 42 on a Skycrane. Both sit at
                //    32-34 with this feature OFF. Terminal is ABOVE the cap, so it is a recov loop every ~5 s,
                //    not a single blip. Using the real downward accel makes the guarantee hold at EVERY attitude.
                // 2. IT TARGETED THE CAP EXACTLY, so the designed margin was zero -- against a check that is
                //    strict, quantized (1/256 m, truncating, worth +0.098 m/s), and clamps dt. The climb side has
                //    mirrored this problem for ages and solves it by targeting 0.9 * ClimbMax; do the same.
                // 3. IT DERIVED THE FLOOR FROM THE RAW CONSTANT while the damping it floored was scaled by
                //    HeaveDampScale, so the guarantee silently evaporated off scale 1 -- `heliphys heave 0.5`
                //    gave 80 m/s against a 40 cap. The floor is applied to the PRODUCT now, so the envelope holds
                //    whatever the debug knob is set to.
                float downAccel = 9.8f + Mathf.Max(0f, -lift * b.Y.Y);
                if (_heliFallMax > 0.01f)
                    heave = Mathf.Max(heave, downAccel / (_heliFallMax * FallEnvelopeMargin));
            }
            ApplyCentralForce(Vector3.Down * (heave * vel.Y * Mass));
            // THE HORIZONTAL PARTNER OF THAT SAME FORCE, which the vertical-only projection throws away. Descent
            // only, matching the shaft factor above, and zero by default. See HeaveRedirect.
            if (HeaveRedirect > 0f && vel.Y < 0f)
            {
                Vector3 shaftFlat = new Vector3(b.Y.X, 0f, b.Y.Z);
                if (shaftFlat.LengthSquared() > 1e-6f)
                    ApplyCentralForce(shaftFlat * (heave * -vel.Y * HeaveRedirect * Mass));
            }

            // HORIZONTAL: quadratic parasite drag, anisotropic. Taken from the FLAT vector only -- both its
            // direction AND its magnitude -- never from LinearVelocity. Using the full 3-D speed would scale
            // horizontal drag by the vertical component, so a 40 m/s dive would produce a large horizontal
            // braking force at near-zero horizontal speed.
            var flat = hflat;     // same vector ETL was sized from, for the same reason
            if (flatSpeed > 0.01f && _heliDragFwd * DragScale > 0f)
            {
                var fwd = new Vector3(-b.Z.X, 0f, -b.Z.Z);
                Vector3 alongFwd = Vector3.Zero;
                if (fwd.LengthSquared() > 1e-6f) { fwd = fwd.Normalized(); alongFwd = fwd * flat.Dot(fwd); }
                Vector3 lateral = flat - alongFwd;
                // F_i = -k_i * |v| * v_i -- the standard anisotropic quadratic form, magnitude set by the total
                // flat speed and direction resolved per axis, so a diagonal slip is dragged on both.
                ApplyCentralForce(-(alongFwd + lateral * HeliLateralDragRatio) * (_heliDragFwd * DragScale * flatSpeed * Mass));
            }

            // BACKSTOP, NOT THE SPEED LIMIT. Drag sets top speed now; this exists only so the sim cannot hand
            // the server a state it would reject -- VehicleReplication validates horizontal motion against
            // Speed_Max * EnvelopeSlack (1.25), and the limit has to bind on the CLIENT that is flying or the
            // server rolls back a legitimate pilot. A committed dive genuinely does exceed Speed_Max, because
            // gravity is helping, so the wall sits above level flight's reach and inside the envelope. It used
            // to sit exactly AT Speed_Max as the only limiter, engaging on any committed run at around 20 deg
            // of tilt: undiminished acceleration right up to the cap and then a wall, which is the opposite of
            // how an aircraft approaches its top speed.
            if (BackstopEnabled && _speedMax > 0f && flatSpeed > _speedMax * HeliEnvelopeBackstop)
            {
                Vector3 excess = flat.Normalized() * (flatSpeed - _speedMax * HeliEnvelopeBackstop);
                ApplyCentralForce(-excess * Mass * 3.0f);
            }

            // CONTROL. Angular VELOCITY is driven toward the commanded rate rather than integrating torques,
            // because torque->spin runs through an inertia tensor nobody has tuned; converging on a rate is
            // stable, framerate-independent and gives the same response on every machine.
            // SIGN CONVENTION, asserted in vehicle.heli_flight so it cannot drift: with Godot's forward = -Z,
            //   pitch +1 = nose UP     -> omega along +X  (about +X, -Z rotates toward +Y)
            //   roll  +1 = bank RIGHT  -> omega along -Z  (about +Z, up would tilt to -X = left)
            //   yaw   +1 = nose RIGHT  -> omega along -Y  (about +Y, -Z rotates toward -X = left)
            // Two of the three need a negation and one does not, which is exactly the situation where
            // reasoning it out once and never checking gets you an inverted axis nobody notices until a
            // playtest. The tests pin all three against the body basis.
            // A DAMAGED TAIL ROTOR TURNS WORSE ("tail rotor hp low -> reduced turning"). Pitch and roll are the
            // MAIN rotor's job and are untouched by tail damage -- losing the tail costs you the pedals, not
            // the cyclic, which is what makes it a distinct failure rather than a general "controls worse".
            // strawberry: "make the tail nerf all horizontal yaw/roll movement, including mouse, turn up the
            // amount of nerf too." SQUARED, so half-health is a quarter of the authority rather than half --
            // a damaged tail should be alarming well before it is dead. Applies to ROLL as well as yaw (both
            // are horizontal control, and roll is where the mouse lives); PITCH is left alone because it is
            // the main rotor's axis and the vertical one.
            float tn = TailRotorNorm;
            float tailEff = 0.04f + 0.96f * tn * tn;
            float agi = SlingAgility;   // empty hook -> crisper; heavy load -> the spec figures
            Vector3 cmd = b.X * (_inPitch * HeliPitchRate * _heliPitchTq * agi / 2.6f)
                        + b.Z * (-_inRoll * HeliRollRate * _heliRollTq * agi / 3.0f * tailEff)
                        + b.Y * (-_inYaw * HeliYawRate * _heliYawTq * agi / 2.2f * tailEff);

            // TORQUE REACTION. A tail rotor's whole job is cancelling the main rotor's torque on the fuselage;
            // with it dead, that torque is unopposed and the airframe spins ("tail rotor dead, go into a
            // spin"). Scaled by the power actually going through the main rotor, so a dead tail on a spun-down
            // machine sitting on the ground does nothing -- it is the LIFT you are pulling that spins you, and
            // that is also the cruel part: the collective you need to stay up is what makes the spin worse.
            if (TailRotorDead)
                cmd += b.Y * (TailLossTorque * spool * Mathf.Max(_inCollective, 0.15f));

            // SELF-LEVELLING IS OFF BY DEFAULT, and that is the correction VoX made after flying it
            // (2026-08-16): "the vehicals pitch and yaw are tracked as a current value and ... thrust applies in
            // relation to that value. The mouse movements should impart changes on that value. Right now your
            // model keeps reverting the copter to upright even if no mouse is applied which is wrong."
            //
            // He is right, and it was a real design error rather than a tuning one. ATTITUDE IS STATE. The
            // airframe holds whatever bank and pitch you put it in, the mouse edits that state, and thrust
            // follows wherever it currently points -- so holding a 20 deg nose-down cruise is something you set
            // once, not something you fight a spring to maintain. A restoring term makes the machine
            // un-commandable in exactly the way that reads as "the controls don't do anything": you lean it,
            // let go, and it undoes your input.
            //
            // The term is KEPT, at 0 on every current spec, because a heavy stabilised airframe is a plausible
            // future variant and the knob costs nothing. Angular damping alone is what stops a held input from
            // spinning forever -- damping bleeds the RATE to zero and leaves the attitude where you left it,
            // which is the behaviour being asked for.
            float manual = Mathf.Max(Mathf.Abs(_inPitch), Mathf.Abs(_inRoll));
            if (_heliLevel > 0f && manual < 0.95f)
            {
                Vector3 up = b.Y;
                Vector3 axis = up.Cross(Vector3.Up);
                if (axis.LengthSquared() > 1e-6f)
                    cmd += axis.Normalized() * up.AngleTo(Vector3.Up) * _heliLevel * spool * (1f - manual);
            }
            // TURBULENCE. Gusts on a random timer, only in the air and only under a live rotor -- a machine
            // sitting on its wheels does not get shoved around, and neither does one whose disc has stopped.
            // Added to the COMMAND rather than applied as a torque, so it rides the same inertia the pilot's
            // input does: a gust builds and washes out with the airframe's own weight instead of teleporting
            // the attitude.
            if (!DebugNoTurbulence && GetContactCount() == 0 && _rotorRpm > 0.4f)
            {
                _turbTimer -= dt;
                if (_turbTimer <= 0f)
                {
                    // Re-probe AGL once per gust, not per tick: gusts are seconds apart and a raycast per frame per
                    // helicopter buys nothing at this timescale.
                    _turbAgl = ProbeAgl();
                    float rough = Mathf.Lerp(TurbLowSeverity, 1f,
                        Mathf.SmoothStep(TurbCalmAgl, TurbFullAgl, _turbAgl));
                    float gapScale = Mathf.Lerp(TurbLowGapScale, 1f,
                        Mathf.SmoothStep(TurbCalmAgl, TurbFullAgl, _turbAgl));
                    _turbTimer = HeliRng.RandfRange(TurbMinGap, TurbMaxGap) * gapScale;
                    var dir = new Vector3(HeliRng.RandfRange(-1f, 1f), HeliRng.RandfRange(-0.5f, 0.5f), HeliRng.RandfRange(-1f, 1f));
                    if (dir.LengthSquared() > 1e-4f)
                        _turbKick = dir.Normalized() * HeliRng.RandfRange(0.35f, 1f) * TurbStrength * rough;
                }
                _turbKick = _turbKick.Lerp(Vector3.Zero, 1f - Mathf.Exp(-TurbDecay * dt));
                cmd += _turbKick * spool;
            }
            else { _turbKick = Vector3.Zero; _turbTimer = 0f; }

            // REAL TORQUE, not an assigned angular velocity. VoX 2026-08-16: "its not inertia of the control
            // its inertia of the vehical which needs to be modeled ... not fake input inertia, real physics
            // simulated inertia."
            //
            // He is right, and the previous version was the wrong thing dressed as the right one. Assigning
            // AngularVelocity each tick means the airframe has NO angular momentum: it rotates exactly as fast
            // as I say and stops the instant I stop saying it. The lag I added on top only made the number I
            // was assigning change more slowly -- releasing the stick still stopped the machine because I
            // stopped it, not because anything was ever spinning.
            //
            // Now `cmd` is an angular ACCELERATION (rad/s^2) and becomes a torque against the body's inertia.
            // Godot integrates it, so momentum is real: let go and it keeps turning, arrested only by
            // aerodynamic damping or by opposite stick. That also makes counter-input a genuine skill -- you
            // stop a rotation by flying against it, which is the thing a helicopter actually asks of a pilot.
            _cmdRate = cmd;   // kept purely as a debug read of what is being commanded this tick
            if (cmd.LengthSquared() > 1e-8f) ApplyTorque(cmd * Inertia.X);

            // SETTLE. No wheels means the shared wheel-contact settle test can never fire, so a parked heli
            // would idle its physics forever.
            //
            // TOUCHING THE GROUND IS PART OF THE TEST, and leaving it out is not a small omission: a helicopter
            // that cuts its collective at altitude coasts upward, decelerates, and passes through zero vertical
            // velocity at the apex. A settle rule that only asks "is it slow and unpowered" fires exactly
            // there and FREEZES IT IN THE SKY -- which is what the first cut of this did, and the flight test
            // caught it as a descent of exactly 0.00 m at exactly 0 m/s. Being stationary in the air is the
            // normal top of every climb, not a machine at rest.
            // GROUNDED BY RAYCAST, not by contact count. A body frozen with FreezeMode.Static keeps reporting
            // the contacts it had when it froze, so "am I still on the ground" answers YES forever once it has
            // settled -- and the settle rule then re-freezes it every tick, from which it can never wake. The
            // only thing that broke the deadlock was DriveHeli explicitly clearing Freeze on pilot input, so
            // flying hid it completely: a settled machine teleported into the air simply HOVERED there, frozen,
            // and a 45 m drop test never fell a single metre.
            bool grounded = GroundedByRay();
            bool idle = grounded && _inCollective < 0.02f && vel.LengthSquared() < 0.05f && AngularVelocity.LengthSquared() < 0.05f;
            if (idle && _spawnGrace <= 0f && !Freeze)
            {
                LinearVelocity = Vector3.Zero; AngularVelocity = Vector3.Zero;
                FreezeMode = RigidBody3D.FreezeModeEnum.Static; Freeze = true;
            }
            else if (!idle && Freeze) Freeze = false;
            if (_spawnGrace > 0f) _spawnGrace -= dt;
        }

        /// <summary>The fixed-wing flight model (EEngine.PLANE). The heli's rotor thrust is replaced by two
        /// separate forces: a FORWARD prop thrust, and LIFT along the body-up axis that scales with AIRSPEED --
        /// so the plane must build speed before the wings carry it, and turns by BANKING (the lift vector tilts
        /// with the roll), which is the "realistic" model master chose. Control surfaces need airflow, so their
        /// authority fades in with speed; the rudder keeps a taxi floor. Buoyancy still runs, so a floatplane
        /// takes off from the water. Prop spin, audio, crash and settle mirror the heli path.</summary>
        void StepPlane(float dt)
        {
            // fuel + explosion lifecycle, same rules every other engine runs
            if (EngineOn && Fuel > 0f && !InfiniteFuel) Fuel = Mathf.Max(0f, Fuel - FuelBurn * dt);
            if (EngineOn && FuelMax > 0f && Fuel <= 0f) EngineOn = false;
            if (_deadTimer > 0f) { _deadTimer -= dt; if (_deadTimer <= 0f) Explode(); }

            // PROP SPOOL 0..1 (shared engine-spool field). Thrust + lift fade in with it, so a cold start can't
            // yank the plane off the ground and cutting the engine leaves the prop windmilling down.
            float want = (EngineOn && !_exploded && (Fuel > 0f || InfiniteFuel)) ? 1f : 0f;
            _rotorRpm = Mathf.MoveToward(_rotorRpm, want, dt / (want > _rotorRpm ? SpoolUpSeconds : SpoolDownSeconds));

            // PROP ANIMATION: spin about the body FORWARD axis (local Z); swap physical blades <-> blur disc by speed.
            if (_propNode != null)
            {
                _propSpin += dt * (_exploded ? 0f : _rotorRpm) * 150f;
                _propNode.Rotation = new Vector3(0f, 0f, _propSpin);
            }
            bool spun = _rotorRpm > DiscSwapSpool;
            if (_propDisc != null) { if (_propBlades != null) _propBlades.Visible = !spun; _propDisc.Visible = spun; }

            // AFTERBURNER flames (jet): length + glow scale with throttle x spool, with a fast flicker; off at idle.
            if (_jetFlames != null)
            {
                _jetFlameT += dt * 32f;
                float burn = _exploded ? 0f : _inCollective * _rotorRpm;   // 0..1 spool
                float flick = 0.85f + 0.15f * Mathf.Sin(_jetFlameT) * Mathf.Sin(_jetFlameT * 0.37f);
                bool burning = burn > 0.04f;
                for (int i = 0; i < _jetFlames.Length; i++)
                {
                    _jetFlames[i].Visible = burning;
                    if (burning)
                    {
                        _jetFlames[i].Scale = new Vector3(0.7f + 0.4f * burn, 0.45f + 1.25f * burn, 0.7f + 0.4f * burn);   // Y = length (aft), X/Z = width; shader flickers
                        _jetFlameMats[i]?.SetShaderParameter("u_throttle", burn);
                    }
                    if (_jetFlameLights[i] != null) _jetFlameLights[i].LightEnergy = burning ? (1.5f + 3.5f * burn) * flick : 0f;
                }
            }

            // CONTRAILS (jet): push each wingtip/winglet's WORLD position into its trail, faded in by airspeed.
            if (_contrails != null)
            {
                float cspd = _exploded ? 0f : LinearVelocity.Length();
                float t01 = Mathf.Clamp((cspd - 24f) / 12f, 0f, 1f);   // gated HIGHER: nothing below 24 m/s, full by ~36 (near top speed)
                float target = t01 * t01 * (3f - 2f * t01);            // smoothstep over the speed gate
                _contrailFade = Mathf.MoveToward(_contrailFade, target, dt / 1.6f);   // LAG it ~1.6s so the trails EASE in from nothing instead of popping on the instant you cross the threshold (you accelerate through the gate too fast to see the raw ramp)
                var camN = GetViewport()?.GetCamera3D();
                Vector3 camPos = camN != null ? camN.GlobalPosition : GlobalPosition + Vector3.Up * 12f;
                var xf = GlobalTransform;
                foreach (var c in _contrails) c.Update(xf * c.Local, _contrailFade, camPos, dt);
            }

            // ENGINE + IGNITION AUDIO ride the prop spool (same wiring as the heli)
            if (_engineAudio != null)
            {
                if (_rotorRpm > 0.01f)
                {
                    _engineAudio.PitchScale = Mathf.Lerp(_idlePitch, _maxPitch, _rotorRpm);
                    _engineAudio.VolumeDb = Mathf.LinearToDb(Mathf.Lerp(_idleVol * 0.35f, _maxVol, _rotorRpm) * EngineVolumeBoost);
                    if (!_engineAudio.Playing) _engineAudio.Play();
                }
                else if (_engineAudio.Playing) { _engineAudio.VolumeDb = -80f; _engineAudio.Stop(); }
            }
            if (_ignitionAudio != null)
            {
                bool starting = want > 0f && _rotorRpm < 0.05f;
                if (starting && !_ignitionFired) { _ignitionFired = true; _ignitionAudio.Play(); }
                else if (want <= 0f && _rotorRpm < 0.01f) _ignitionFired = false;
            }

            // BUOYANCY: a floatplane sits on its pontoons and takes off from the water. The boat's OWN
            // propulsion is gated off for a plane inside ApplyWaterPhysics -- the prop is what moves it. Self-
            // guards on _buoys/HasWater, so a land plane (no buoys) simply does nothing here.
            ApplyWaterPhysics(dt);

            if (_exploded) return;   // a wreck is just a falling body

            Basis b = GlobalTransform.Basis;
            float spool = _rotorRpm;

            // live tuning without a rebuild (the flight model is fiddly) -- env overrides for the three big numbers
            float pThrust = float.TryParse(System.Environment.GetEnvironmentVariable("UG_PLANETHRUST"), out var _pt) ? _pt : _planeThrust;
            float pLift = float.TryParse(System.Environment.GetEnvironmentVariable("UG_PLANELIFT"), out var _pl) ? _pl : _planeLift;
            float pTarget = float.TryParse(System.Environment.GetEnvironmentVariable("UG_PLANETARGET"), out var _pta) ? _pta : _planeTargetSpeed;
            if (pTarget < 1f) pTarget = 1f;

            bool grounded = GroundedByRay();
            bool onSurface = grounded || _afloat;

            // FORWARD THRUST from the prop -- pulls along body forward (-Z), scaled by the throttle setting + spool.
            float throttle = _inCollective;
            ApplyCentralForce(-b.Z * (pThrust * throttle * spool) * Mass);

            // AIRSPEED = forward component of velocity.
            float airspeed = LinearVelocity.Dot(-b.Z);

            // ANGLE OF ATTACK: how far the nose sits ABOVE the oncoming airflow, in the pitch plane. A wing makes
            // lift from THIS, not from raw speed -- so level flight is the AoA where lift == weight (the plane is
            // TRIMMABLE: hold a few degrees nose-up to stay level), pulling adds lift (climb), and past the stall
            // angle the lift COLLAPSES and it mushes / drops. This is what stops a wings-level plane at cruise
            // speed climbing forever (the airspeed-only model did -- lift was always > weight).
            float upV = LinearVelocity.Dot(b.Y);
            float aoaDeg = airspeed > 0.5f ? Mathf.RadToDeg(Mathf.Atan2(-upV, airspeed)) : 0f;
            float cl = aoaDeg <= PlaneStallDeg
                     ? PlaneCl0 + aoaDeg * PlaneClSlope
                     : Mathf.Max(0f, (PlaneCl0 + PlaneStallDeg * PlaneClSlope) - (aoaDeg - PlaneStallDeg) * PlaneClSlope * 2.2f);   // stall: lift falls away past the critical angle
            cl = Mathf.Clamp(cl, 0f, PlaneClMax);
            // Dynamic pressure ~ v^2 (a wing at twice the speed makes ~4x the lift); normalised so airspeed ==
            // target gives factor 1. No airspeed -> no lift -> sink, which forces the takeoff run.
            float liftFrac = Mathf.Min(airspeed / pTarget, 1.3f); liftFrac *= liftFrac;

            // LIFT along BODY-UP: rolling tilts this vector so a banked wing carves the turn (bank-to-turn).
            // GROUND MODE (Ctrl) kills lift so the plane stays down to taxi.
            // BANK COMPENSATION: boost lift toward 1/cos(bank) so a hard bank keeps its VERTICAL component up
            // (master "if i bank sharply i lose height sooo fast") -- the automated back-pressure of a coordinated turn.
            if (!_planeGroundMode)
            {
                float upDot = Mathf.Clamp(b.Y.Y, 0.2f, 1f);   // cos of the tilt from vertical (floored so an inverted attitude doesn't blow up)
                float bankComp = Mathf.Lerp(1f, 1f / upDot, PlaneBankComp);
                ApplyCentralForce(b.Y * (liftFrac * cl * pLift * bankComp) * Mass);
            }

            // DRAG: parasitic airflow drag (throttle -> a real top speed; dead engine -> glide) + the horizontal
            // speed cap the MP envelope binds on (mirrors heli/boat).
            Vector3 vel = LinearVelocity;
            ApplyCentralForce(-vel * (PlaneDrag * Mass));
            var flat = new Vector3(vel.X, 0f, vel.Z);
            if (_speedMax > 0f && flat.Length() > _speedMax)
            {
                Vector3 excess = flat.Normalized() * (flat.Length() - _speedMax);
                ApplyCentralForce(-excess * Mass * 2.0f);
            }

            // CONTROL. On HARD GROUND a wheeled plane behaves like a CAR: the nose wheel steers off the rudder,
            // and the flight-control torques are SUPPRESSED -- applying pitch/roll/yaw torque against the gear is
            // exactly what makes it FREAK OUT on contact (master). In the AIR it's full 3-axis control +
            // weathervane; the ground-rotation assist below still lifts the nose for takeoff.
            // SIGN CONVENTION matches DriveHeli: pitch +1 = nose up -> +X; roll +1 = bank right -> -Z; yaw +1 = nose right -> -Y.
            bool onGround = (grounded || _planeGroundMode) && !_afloat;   // wheeled land plane sitting/rolling on hard ground (or forced ground/taxi mode)
            AngularDamp = onGround ? 12f : 0f;   // GROUND: bleed the wheel-chatter yaw/roll wobble on rough terrain; airborne = free rotation for flight (master 2026-08-18)

            // RETRACTABLE GEAR (jet): deploy (down) on the ground or when slow; retract (fold up into the belly) once
            // airborne + fast. Lerped ~1.5s so it swings up/down smoothly instead of snapping.
            if (_gearPivots != null)
            {
                float gTarget = _gearWantDown ? 1f : 0f;   // MANUAL: G toggles _gearWantDown (debounced in ToggleGear) -- no more auto speed-based retract (master 2026-08-18)
                _gearDeploy = Mathf.MoveToward(_gearDeploy, gTarget, dt / 1.5f);
                bool gearVis = _gearDeploy > 0.01f;   // fully retracted -> hide the gear + wheels entirely, not just tucked (master 2026-08-18)
                for (int i = 0; i < _gearPivots.Length; i++)
                    if (_gearPivots[i] != null) { _gearPivots[i].Visible = gearVis; _gearPivots[i].Basis = new Basis(_gearAxis[i], Mathf.DegToRad(_gearAng[i] * (1f - _gearDeploy))); }
                bool physOn = _gearDeploy > 0.5f;   // wheel PHYSICS off once the gear is >half UP -> retracted plane has no phantom invisible-wheel ground contact (master)
                if (physOn != _gearPhysOn)
                {
                    _gearPhysOn = physOn;
                    for (int wi = 0; wi < _wNodes.Length; wi++)
                        if (_wNodes[wi] != null && _wheelSuspF != null) { _wNodes[wi].SuspensionMaxForce = physOn ? _wheelSuspF[wi] : 0f; _wNodes[wi].WheelFrictionSlip = physOn ? _wheelFricF[wi] : 0f; }
                }
            }
            if (onGround)
            {
                Steering = _steerMax > 0f ? Mathf.DegToRad(-_inYaw * _steerMax) : 0f;   // rudder -> nose-wheel steer, so it actually turns while taxiing
                // BRAKE the gear when not driving forward, so a parked plane HOLDS instead of free-rolling down any
                // slope forever ("slides along the floor" -- the wheels never brake + StepPlane's settle needs
                // vel~0 to freeze, which a rolling plane never reaches). Release the brake once you throttle up.
                // REVERSE (ground taxi): hold S at idle throttle to back up slowly on the wheels. A jet has no
                // reverse thrust, but Unturned vehicles reverse -- a gentle backward push capped at a slow taxi-back
                // speed; the nose wheel still steers, and the parking brake releases so it can roll. (master 2026-08-18)
                bool reversing = _rawThrottle < -0.05f && _inCollective < 0.08f;
                if (reversing)
                {
                    Brake = 0f;
                    if (LinearVelocity.Dot(b.Z) < 6f) ApplyCentralForce(b.Z * (pThrust * 0.35f) * Mass);   // b.Z = body BACKWARD; cap ~6 m/s reverse
                }
                else
                    Brake = _inCollective < 0.08f ? _brakeForce * HandbrakeScale : 0f;   // proper PARKING brake (was Max(_,30) -- 13x too weak)
                // NO flight ApplyTorque while grounded -- torque against the gear is the freak-out
            }
            else
            {
                float ctrlAuth = Mathf.Clamp(airspeed / (pTarget * PlaneCtrlSpeedFrac), 0f, 1f);
                Vector3 cmd = b.X * (_inPitch * _planePitchTq * ctrlAuth)
                            + b.Z * (-_inRoll * _planeRollTq * ctrlAuth)
                            + b.Y * (-_inYaw * _planeYawTq * ctrlAuth * _planeSteerFade);
                // AERODYNAMIC STABILITY (weathervaning): a restoring torque rotating the nose (-Z) onto the
                // airflow, firmer with airspeed. Axis = nose x velDir -> pitch+yaw only, never roll, so the BANK
                // survives (a banked wing still carves the turn) while the nose tracks the curving flight path.
                // Also stops a held elevator over-rotating (elevator raises the nose, stability pulls it back).
                if (LinearVelocity.LengthSquared() > 4f)
                {
                    float stab = float.TryParse(System.Environment.GetEnvironmentVariable("UG_PLANESTAB"), out var _ps) ? _ps : PlaneStability;
                    Vector3 velDir = LinearVelocity.Normalized();
                    Vector3 restore = (-b.Z).Cross(velDir);
                    cmd += restore * (Mathf.Clamp(airspeed / pTarget, 0f, 1.5f) * stab);
                }
                if (cmd.LengthSquared() > 1e-8f) ApplyTorque(cmd * Inertia.X);
            }

            // GROUND ROTATION assist (wheeled land plane): the gear holds the airframe rigidly level + the
            // Inertia-based elevator can't lift the nose against the weight on the wheels, so a runway takeoff
            // needs a hand. At takeoff speed, back-stick adds a DIRECT nose-up torque (a real elevator makes a big
            // tail-download at speed) -> the plane ROTATES off the runway; once airborne (grounded false) it stops
            // and the normal elevator flies it. On the ground only, fades in with airspeed. +X = nose up.
            if (grounded && !_afloat && _inPitch > 0.02f && airspeed > pTarget * 0.45f && _inCollective > 0.4f)   // throttle UP -> only on a takeoff run, not while landing (a flare wouldn't want the slam)
                ApplyTorque(b.X * (_inPitch * PlaneGroundRotate * airspeed * Mass));

            // FLIGHT DEBUG (UG_PLANEDBG=1): airspeed / altitude / lift / pitch so I can read the takeoff envelope
            if (System.Environment.GetEnvironmentVariable("UG_PLANEDBG") == "1" && ++_planeDbgFrame % 20 == 0)
                GD.Print($"[plane] t={_planeDbgFrame} spd={LinearVelocity.Length():F1} air={airspeed:F1} alt={GlobalPosition.Y:F1} aoa={aoaDeg:F0} cl={cl:F2} spool={spool:F2} thr={throttle:F2} noseDeg={Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-b.Z.Y, -1f, 1f))):F0} roll={Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(b.X.Y, -1f, 1f))):F0} angv={AngularVelocity.Length():F1} hdg={Mathf.RadToDeg(Mathf.Atan2(-b.Z.X, -b.Z.Z)):F0} grnd={grounded} afloat={_afloat}");

            // CRASH: full-3D speed like the heli (a plane's defining crash is a nose-in dive, not a lateral
            // bonk). Guarded by the spawn grace so the placement drop doesn't count.
            float curSpeed = LinearVelocity.Length();
            float decel = _prevSpeed - curSpeed;
            _recentTopSpeed = Mathf.Max(curSpeed, _recentTopSpeed - dt * 6f);
            if (_crashCd > 0f) _crashCd -= dt;
            if (!_exploded && _spawnGrace <= 0f && _crashCd <= 0f && _recentTopSpeed > HeliBonkSpeed && decel > 200f * dt)
            {
                _crashCd = 0.25f;
                DebugLastImpactSpeed = _recentTopSpeed;
                if (_recentTopSpeed >= HeliCrashExplodeSpeed) Explode();
                else { TakeDamage(decel * 18f); BonkFx(); }
            }
            _prevSpeed = curSpeed;

            // PARK-FREEZE. A passive-wheeled plane is NOT the full car sim, so on a real-terrain SLOPE it
            // slides + slowly spins (master's "freaks out + slides") -- the gear brake can't fully hold it and the
            // strict settle never fires while it's rotating. So the moment it's on the ground/water with the
            // throttle low and moving slowly, FREEZE it solid: the freeze zeroes the slide AND the spin. It wakes
            // instantly on throttle. The velocity gate (< 2 m/s) means a fast landing ROLLOUT isn't frozen mid-roll
            // -- only once it's slowed to park. No angular gate on purpose: killing the spin is the whole point.
            bool idle = onSurface && throttle < 0.1f && _rawThrottle >= -0.05f && vel.LengthSquared() < 4.0f;   // holding S (reverse) keeps it awake so it doesn't park-freeze mid-back-up
            if (idle && _spawnGrace <= 0f && !Freeze)
            {
                LinearVelocity = Vector3.Zero; AngularVelocity = Vector3.Zero;
                FreezeMode = RigidBody3D.FreezeModeEnum.Static; Freeze = true;
            }
            else if (!idle && Freeze) Freeze = false;
            if (_spawnGrace > 0f) _spawnGrace -= dt;
        }

        /// <summary>The driver got out: drop every held axis so the car stops pulling the last throttle/steer it
        /// was given and the rpm falls back to idle (master: "vehicles hold the last player input when you exit
        /// them, and also the last rpm"). The brake is left as the driver left it and nothing parks here --
        /// momentum is still theirs to leave behind (see PlayerController.ExitVehicle).</summary>
        public void ReleaseControls()
        {
            _inThrottle = 0f; _inSteer = 0f; _rawThrottle = 0f;
            _inCollective = 0f; _inYaw = 0f; _inPitch = 0f; _inRoll = 0f;
            if (!_heli) { EngineForce = 0f; Steering = 0f; _steerTarget = 0f; }   // _steerTarget too, or the smoothing re-applies the last steer next tick
        }

        public void Drive(float throttle, float steer, bool handbrake)
        {
            if (_carIgnitionLeft > 0f) { _carIgnitionLeft -= (float)GetPhysicsProcessDeltaTime(); throttle = 0f; }   // still cranking: no drive yet (steering + brakes work)
            // A helicopter has no wheels to turn or brake. The MP fallback and any generic caller still reach it
            // through this one seam, mapped onto the flight axes it does have -- throttle is the collective,
            // steer is the pedals. Pitch/roll are absent here on purpose: they arrive as the reported TRANSFORM
            // from a predicting client, not as input (see DriveHeli).
            if (_heli) { DriveHeli(throttle, steer, 0f, 0f, GetPhysicsProcessDeltaTime()); return; }
            _inThrottle = throttle; _inSteer = steer;   // remembered for boat/amphibious water propulsion (applied as forces in _PhysicsProcess)
            _driveIdle = 0f;   // somebody is at the wheel THIS tick
            if (_exploded) { EngineForce = 0f; Steering = 0f; Brake = 0f; return; }   // a wrecked vehicle can't be driven
            _parked = false;
            if (Sleeping) { Sleeping = false; _asleep = false; }   // a settled car SLEEPS now, and a sleeping body integrates nothing -- EngineForce on it would do exactly nothing until something else nudged it
            if (!EngineOn) throttle = 0f;   // dead/off engine (e.g. 0 HP): no drive power, but the car keeps its momentum and can still steer + brake -> coasts to a stop instead of freezing (master)
            float speed = LinearVelocity.Length();   // m/s (horizontal-ish while driving)
            float fwd = LinearVelocity.Dot(-GlobalTransform.Basis.Z);   // signed forward speed (front = -Z)
            // S while rolling FORWARD (or W while rolling backward) = a foot BRAKE, not an instant reverse -- real pedal feel
            bool footBrake = (throttle < 0f && fwd > 0.6f) || (throttle > 0f && fwd < -0.6f);
            bool neutral = handbrake && speed < 0.5f;   // near-stop + handbrake -> NEUTRAL: cut engine force so a slow reverse doesn't fight the brake + jitter (master)
            if (_tracked)
            {
                // DIFFERENTIAL / SKID STEER (master "actual tank controls"). BOTH tracks keep driving off the
                // throttle with a speed DIFFERENCE from steer (a fraction, TankTrackDiff), so turning never STOPS a
                // track and halves the power -- master drove the stop-a-track version and it CRAWLED ("braking one
                // set of wheels... braking the whole tank"). The actual TURN is the real yaw torque below (driven by
                // -steer, throttle-independent so the rate is the same pivoting or arcing); the track difference is
                // just the which-track-drives feel. Per-WHEEL EngineForce by side; no steered wheels.
                float leftT = Mathf.Clamp(throttle + steer * TankTrackDiff, -1f, 1f);
                float rightT = Mathf.Clamp(throttle - steer * TankTrackDiff, -1f, 1f);
                if (footBrake || neutral) { leftT = 0f; rightT = 0f; }                                   // pedal brake / handbrake-at-rest cuts drive, same as the car
                if (fwd >= _speedMax) { leftT = Mathf.Min(leftT, 0f); rightT = Mathf.Min(rightT, 0f); }  // at the forward speed cap: no more forward torque (a turn/reverse is still allowed)
                if (fwd <= _speedMin) { leftT = Mathf.Max(leftT, 0f); rightT = Mathf.Max(rightT, 0f); }  // at the reverse cap: no more reverse torque
                EngineForce = 0f; Steering = 0f; _steerTarget = 0f;                                      // clear the GLOBAL traction (its setter overwrites every traction wheel) + the wheel-angle steer, THEN set per-wheel below
                float tTrack = Mathf.Abs(ThrottleForcePerWheel(1f));                                     // a tank has a gearbox too: per-wheel force from the torque curve at the current gear
                for (int i = 0; i < _wNodes.Length; i++)                                                 // negate like the car path: this rig drives +Z for +force
                    _wNodes[i].EngineForce = -(_wNodes[i].Position.X < 0f ? leftT : rightT) * tTrack;
                _tankYawInput = -steer;   // [-1,1] turn request, throttle-INDEPENDENT -> the REAL yaw torque in _PhysicsProcess. A on its own pivots; W+A arcs at the same rate + forward speed (both tracks still driving)
                _handbraking = handbrake;
                bool tCoast = Mathf.Abs(throttle) < 0.05f && Mathf.Abs(steer) < 0.05f && !footBrake;     // no throttle AND no steer input -> engine-brake it down (a steer-only pivot must NOT coast-brake, or it can't spin)
                Brake = handbrake ? _brakeForce * HandbrakeScale : (footBrake ? _brakeForce * FootBrakeScale : (tCoast ? _brakeForce * FootBrakeScale * EngineBrakeScale * EngineRpmNorm : 0f));   // engine braking scales with revs, same as the car
                _braking = handbrake || footBrake;
                if (_taillightsOn) SetTailFlare(_braking);   // BOTH tail lamps (the baked lens is split L/R with a material each; _taillightMat alone was the left one -- strawberry 2026-09-04 "only the left brake light works")
                return;
            }
            float eng = (footBrake || neutral) ? 0f : ThrottleForcePerWheel(throttle);
            if (CoupledTrailer != null) eng *= 0.5f;   // towing a loaded trailer halves the pull -> even slower accel while hooked up (strawberry 2026-07-15)
            if (Towing != null) eng *= 0.9f;   // towing a car on a rope: only a touch sluggish now -> the tower keeps most of its power to actually haul the load (0.7->0.9, master "WAYYY too weak" 2026-07-20)
            // NO HARD TOP-SPEED CUTOFF ANY MORE. `speed >= _speedMax -> eng = 0` is what made this feel like a
            // video game car: full power right up to an invisible wall, then nothing. Top speed is now where
            // tractive force meets drag, so the car eases up to it the way a real one does and a heavy load,
            // a hill or a headwind change the answer. What is left here is a RUNAWAY GUARD, not a speed model:
            // it sits above the drag equilibrium and below the MP envelope, and in normal driving never fires.
            if (throttle > 0f && speed >= _speedMax * SpeedBackstop) eng = 0f;
            if (throttle < 0f && speed >= -_speedMin) eng = 0f;   // cap reverse at -Speed_Min (7)
            // THE CLUTCH (strawberry_cow 2026-08-24: "a somewhat real clutch engine power disconnect when
            // shifting up/down"). While the shift is in progress the drive is DISCONNECTED, so the car coasts
            // through the gearchange and picks up again in the new ratio.
            //
            // This is what makes a shift felt rather than merely logged: previously _shiftCd was purely a
            // lockout on shifting AGAIN and power was never interrupted, so the only cue was a cosmetic jolt.
            // Momentum carries the car, drag still acts, and on an upshift at redline the loss of thrust is
            // exactly the pause a real box has.
            //
            // Eased in and out rather than a hard gate: a step to zero force and back is an impulse the
            // suspension answers with a nod, which reads as a bug rather than a gearchange.
            // Read only -- Drive() has no delta; the timer ticks in _PhysicsProcess beside _shiftCd, which is
            // the one place that already owns the shift clock.
            if (_clutchT > 0f)
            {
                float k = Mathf.Clamp(_clutchT / ShiftClutchTime, 0f, 1f);
                eng *= 1f - Mathf.Sin(k * Mathf.Pi);   // 1 -> 0 -> 1 across the shift, zero drive at its midpoint
            }
            // TRACTION LIMIT -- the tyre can only pass so much force to the ground, and past that it spins.
            //
            // strawberry asked for grip/wheelspin and per-vehicle-per-gear traction. It could not be built
            // until now: measured against mu * m * g the whole fleet was using 3-58% of available grip at
            // launch, so a limit would have been a no-op on every vehicle (the jeep would not have spun ON
            // ICE). Raising LaunchBoost to 4.0 is what put the light vehicles over the line.
            //
            // CLAMPS THE FORCE, does not touch WheelFrictionSlip. That parameter is Godot's COMBINED lateral +
            // longitudinal grip, so using it to limit wheelspin would tax cornering by exactly as much -- which
            // is the trap that made this look impossible the first time I costed it. Clamping the drive force
            // leaves the cornering model completely alone.
            //
            // PER-GEAR FALLS OUT, no per-gear table needed: the force arriving here is already the torque curve
            // through the current ratio, so first gear asks for several times what fifth does against the same
            // limit. Spins in 1st, grips in 5th, because that is what the arithmetic says.
            //
            // Load is weight / wheels ON THE GROUND. No longitudinal-transfer term, deliberately: every wheel
            // on these vehicles is a drive wheel (UseAsTraction is set for all of them unless it is a trailer),
            // so transfer moves load BETWEEN driven wheels and the total available traction does not change.
            // Adding a transfer term here would be arithmetic that changes nothing.
            _wheelSlip = 0f;
            if (_peakTorque > 0f && _nTraction > 0 && !_water0Boat)
            {
                int onGround = 0;
                if (_wNodes != null) foreach (var w in _wNodes) if (w != null && w.IsInContact()) onGround++;
                if (onGround > 0)
                {
                    // Both sides are NEWTONS. Writing this against Mass alone (kg) is the unit slip I already
                    // made once today on this same model, and it read as a plausible 3.5x grip figure.
                    float gripPerWheel = _tyreMu * (Mass * _gravityMag / onGround);
                    float demanded = Mathf.Abs(eng);
                    if (demanded > gripPerWheel && demanded > 0.01f)
                    {
                        _wheelSlip = (demanded - gripPerWheel) / demanded;   // 0 = gripping, ->1 = all slip
                        eng = Mathf.Sign(eng) * gripPerWheel;
                    }
                }
            }
            EngineForce = -eng;   // NEGATE: Godot drives this rig +Z for positive force, so W(throttle+1) was going backward
            // STEERING FADES AGAINST THE SPEC TOP SPEED, NOT THE BUFFED ONE. This fade is a function of ROAD
            // SPEED -- how much lock you get at 12 m/s should not depend on how fast the car can eventually
            // go -- but it was written as a fraction of _speedMax, and the drivetrain raised _speedMax from
            // 12.5 to 20. So the same 12 m/s went from t=0.96 (14.6 deg of lock) to t=0.60 (19.6 deg): 34%
            // more steering at every real speed in the usable range. strawberry, after driving it: "turning
            // is wayy too sensitive". Keying it to the pre-buff spec value restores the exact original curve
            // at every road speed and pins full fade at _steerMin from the old top speed upward.
            float steerRef = _specSpeedMax > 0f ? _specSpeedMax : _speedMax;
            float t = steerRef > 0f ? Mathf.Clamp(speed / steerRef, 0f, 1f) : 0f;   // guard div-by-0 for a towed body (=0) -> NaN steer target
            // target steer angle (deg); NEGATE because Godot VehicleBody3D steers LEFT for positive (D(+1)=right). 28deg at rest -> 14 at full speed.
            // ...AND CAPPED BY LATERAL ACCELERATION, which is what makes it feel like it has mass.
            // strawberry: "turning is wayy too sensitive, this is the area i wanted real simulated intertia."
            //
            // MEASURED first, because the obvious suspect was wrong. Dropping WheelFrictionSlip from 6.0 to
            // 1.5 moved cornering only from 2.08 g to 1.39 g -- the car is not sliding at all. At 14 deg of
            // lock it carved an 11.6 m radius, which is exactly the Ackermann radius wheelbase/tan(delta) for
            // its own 2.89 m wheelbase, so it TRACKS the steering angle and grip was never the limit. The
            // steering angle is the whole story: 14 deg at 15 m/s asks for a 2 g corner and the tyres took it.
            //
            // THE CAP IS GONE (strawberry_cow 2026-08-24: "the turning circle for cars right now is massive",
            // and before that, twice: "i want real simulated weight/inertia on steering, not a hard clamp").
            //
            // It capped the steer angle to whatever would ask for MaxLatAccel -- tan(delta) = wheelbase*a/v^2 --
            // which is cheap and self-limiting and was an invisible hand on the player's steering wheel. It is
            // also, measured, the entire cause of the wide circle: removing it and changing NOTHING else takes
            // the jeep's full-lock radius from 27.9 m to 11.7 m. 11.7 is the Ackermann radius for its 2.89 m
            // wheelbase at the 14 deg it has faded to by then, i.e. the car now tracks its own steering angle
            // instead of being held off it.
            //
            // What the cap bought was stopping a 2 g corner. That is a real thing to want and this gives it
            // back -- but the way to buy it is tyres that run out of grip, not a limiter on the input, and that
            // is what was asked for twice. Steer_Max/Steer_Min stay exactly retail's (Jeep.dat: 28 and 14).
            float angle = Mathf.Lerp(_steerMax, _steerMin, t);
            _steerTarget = -steer * angle;   // smoothed toward in _PhysicsProcess (not snapped) via the AnimatedSteeringAngle-style ramp
            // SPACE = handbrake (locks hard); S-into-forward-motion = foot brake. Both far stronger than the old raw .dat Brake.
            _handbraking = handbrake;   // remembered so the car freezes (no jitter) when stopped with the handbrake held
            bool coasting = Mathf.Abs(throttle) < 0.05f && !footBrake;   // no throttle + no brake input -> engine braking drags it down FASTER than pure friction (master: slow faster on its own)
            // THE HANDBRAKE LOCKS THE REAR AXLE ONLY. That is the entire point of one: killing rear grip lets
            // the back step out, which is what a handbrake turn IS. It used to be the footbrake with a bigger
            // number applied to all four wheels evenly, and the probe measured the consequence exactly --
            // peak yaw 0.00 rad/s through a full handbrake stop from 12 m/s. No rotation at all, ever.
            // strawberry: "the handbrake SUCKS".
            //
            // Rear is +Z: the car faces -Z, so a wheel behind the centre has a positive local Z.
            // ENGINE BRAKING IS NOT A BRAKE PEDAL. Lifting off used to apply 35% of full braking force --
            // measured, that stopped the jeep from 72 km/h in about two seconds, roughly 1 g, purely for
            // taking your finger off the key. It is now proportional to ENGINE SPEED, which is what actually
            // produces engine braking: strong when you lift off at high revs in a low gear, nothing at all at
            // idle. A coasting car should roll.
            float footB = footBrake ? _brakeForce * FootBrakeScale
                        : (coasting ? _brakeForce * FootBrakeScale * EngineBrakeScale * EngineRpmNorm : 0f);
            if (handbrake && _wNodes != null && _wNodes.Length > 0)
            {
                Brake = 0f;   // nothing body-wide: the rear wheels carry it
                foreach (var w in _wNodes) w.Brake = w.Position.Z > 0f ? _brakeForce * HandbrakeScale : footB;
            }
            else
            {
                if (_wNodes != null) foreach (var w in _wNodes) w.Brake = 0f;   // hand back to the body-wide value
                Brake = footB;
            }
            _braking = handbrake || footBrake;   // remembered for the trailer brake-light pass-through (UpdateCoupled)
            if (_taillightsOn) SetTailFlare(_braking);   // BOTH tail lamps (the baked lens is split L/R with a material each; _taillightMat alone was the left one -- strawberry 2026-09-04 "only the left brake light works")
        }

        public void Park()   // driver left: smoothly damp to a stop + straighten (no hard-brake judder), then hold
        {
            if (_heli) { ParkHeli(); return; }   // nothing to brake or straighten; an airborne heli must keep flying, not stop dead
            _parked = true;
            EngineForce = 0f;
            _steerTarget = 0f;
            AngularVelocity = Vector3.Zero;
        }

        // --- Trailer hitch: couple/uncouple. Called from the on-foot E interaction (PlayerController). ---
        public Vector3 FifthWheelWorld => ToGlobal(FifthWheelLocal);
        public Vector3 KingpinWorld => ToGlobal(KingpinLocal);

        // an uncoupled cab whose fifth wheel is within CoupleReach of THIS trailer's kingpin -> it's backed under, ready to hitch (drives the $"[{Keybinds.Get(GameAction.Interact).Label}] connect trailer" billboard prompt)
        bool CabBackedUnder()
        {
            var kp = KingpinWorld;
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && v != this && v.CanTow && v.CoupledTrailer == null && v.FifthWheelWorld.DistanceSquaredTo(kp) <= CoupleReach * CoupleReach) return true;
            return false;
        }

        // Couple THIS cab to a trailer: pin the fifth-wheel to the kingpin so the trailer swings behind on the joint.
        public bool CoupleTo(Vehicle trailer)
        {
            if (!CanTow || trailer == null || !trailer.IsTrailer || CoupledTrailer != null || trailer.CoupledCab != null) return false;
            if (FifthWheelWorld.DistanceTo(trailer.KingpinWorld) > CoupleReach) return false;   // must be backed under the kingpin
            // MAGNETIZE: snap the trailer so its kingpin sits exactly under the fifth wheel -> pivot perfectly centered.
            // A pin joint can't PULL two offset anchors together (it just holds the offset), so the only real way to
            // center is to align them here. Do it on a WOKEN, zero-velocity body so the teleport adds no jolt -- the
            // jolt (not the alignment) is what locked driving when this ran on a frozen/moving body before. (strawberry)
            trailer.Wake(); trailer.LinearVelocity = Vector3.Zero; trailer.AngularVelocity = Vector3.Zero;
            trailer.GlobalPosition += FifthWheelWorld - trailer.KingpinWorld;
            var joint = new PinJoint3D { Name = "Hitch" };
            GetParent().AddChild(joint);                       // sibling of the two bodies in the world
            joint.GlobalPosition = FifthWheelWorld;            // the coupling point (kingpin now coincident with it)
            joint.NodeA = joint.GetPathTo(this);
            joint.NodeB = joint.GetPathTo(trailer);
            joint.SetParam(PinJoint3D.Param.Bias, 0.4f);       // holds the centered pivot; the pin's free rotation gives the vertical flex over bumps
            _hitch = joint; CoupledTrailer = trailer; trailer.CoupledCab = this;
            AddCollisionExceptionWith(trailer); trailer.SetTowGhost(true);   // ghost the cab<->trailer pair ONLY: the exception makes the two BODIES ignore each other (both directions -> no coupling fight, no clip), the layer swap keeps the cab's rear WHEELS off the trailer front hulls (no ride-up). Every hull stays SOLID vs the player/world -- no shape disabling, no holes (strawberry 2026-07-15)
            _approachGhost = trailer;   // remember it so Uncouple tears down the same pair
            if (trailer._landingGear != null) trailer._landingGear.Disabled = true;   // RETRACT the landing legs -> the cab's fifth wheel now carries the nose, legs would just drag
            if (trailer._landingLegMesh != null) trailer._landingLegMesh.Visible = false;   // and hide their VISUAL -> legs vanish on hookup
            Sleeping = false; trailer.Wake();                  // wake both; trailer.Wake() also clears its spawn `_parked` so it won't damp/freeze-static and anchor the tow (was the 2mph stall)
            return true;
        }

        // Uncouple: works called on either the cab or the trailer.
        public void Uncouple()
        {
            var cab = CanTow ? this : CoupledCab;
            if (cab == null || cab.CoupledTrailer == null) return;
            var trailer = cab.CoupledTrailer;
            if (cab._hitch != null && IsInstanceValid(cab._hitch)) cab._hitch.QueueFree();
            cab._hitch = null; cab.CoupledTrailer = null;
            if (trailer != null)
            {
                trailer.CoupledCab = null;
                cab.RemoveCollisionExceptionWith(trailer); trailer.SetTowGhost(false);   // restore cab<->trailer collision + the trailer's solid layer (UpdateTrailerApproach re-ghosts if the cab is still lined up under it, so it can drive out clean)
                cab._approachGhost = null;
                if (trailer._landingGear != null) trailer._landingGear.Disabled = false;   // DEPLOY the landing legs -> hold the nose level now that the cab's gone (fixes the "front sinks into the ground")
                if (trailer._landingLegMesh != null) trailer._landingLegMesh.Visible = true;   // and show their VISUAL again
                trailer.DriveTrailerLights(false, false);   // cab no longer drives them -> kill the trailer's brake/tail lights (its own logic resumes now CoupledCab is null)
                trailer.Park();   // re-park so a dropped trailer settles + freezes in place instead of free-rolling off on its low-friction wheels
            }
        }

        // --- Rope tow (strawberry 2026-07-19). SP/integrated-server only (both bodies must live in one physics space). ---
        public Vector3 FrontTowWorld => ToGlobal(FrontTowLocal);
        public Vector3 RearTowWorld  => ToGlobal(RearTowLocal);

        // small nub cube marking a tow node; hidden until a rope tool is out (PlayerController toggles them, like port arrows)
        static MeshInstance3D MakeTowNub(Color c, Vector3 pos)
        {
            var m = new StandardMaterial3D { AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel, Metallic = 0f, Roughness = 0.6f };
            return new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * 0.16f }, MaterialOverride = m, Position = pos, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        }
        // Show the tow nubs while a rope tool is out, but HIDE the DISALLOWED node (master 2026-07-20): a car that is
        // TOWING has its FRONT node hidden (it can't also be towed), one being TOWED has its REAR node hidden (it can't
        // also tow) -- one rope per car end (AttachTow). The OCCUPIED node stays shown so you can RMB-disconnect it.
        public void SetTowNodesVisible(bool on)
        {
            if (_towFrontNub != null) _towFrontNub.Visible = on && Towing == null;
            if (_towRearNub  != null) _towRearNub.Visible  = on && TowedBy == null;
        }
        public bool TowFrontNubVisible => _towFrontNub != null && _towFrontNub.Visible;   // test accessors for the node-hiding regression
        public bool TowRearNubVisible  => _towRearNub  != null && _towRearNub.Visible;
        // B11 ITowNode: a real Vehicle is the SP/loopback-host tie target -> NetId 0 (the host attaches directly,
        // no wire). Roped = either end already tied; Scannable = not a wreck (mirrors the UpdateRopeLook skip).
        public uint TowNetId => 0;
        public bool TowRoped => Towing != null || TowedBy != null;
        public bool TowScannable => !Exploded;
        public void SetTowNubHighlighted(bool rear, bool on)   // brighten the looked-at node while roping
        {
            var nub = rear ? _towRearNub : _towFrontNub;
            if (nub != null && nub.MaterialOverride is StandardMaterial3D m) { m.EmissionEnabled = on; m.Emission = m.AlbedoColor; m.EmissionEnergyMultiplier = on ? 1.0f : 0f; }
        }

        float[] _towSavedSlip;   // towed: original per-wheel friction slip, saved while towed, restored on release
        const float TowedFreeRollSlip = 0.5f;   // a towed car's wheel friction slip while roped: low enough the rope can drag it, high enough it still tracks laterally (grippy default is ~6)
        // While roped behind another car, a towed car FREE-ROLLS (like the semi trailer's low-friction wheels): drop the
        // grippy traction friction (slip 6) to ~1.5 so the rope can actually drag it, instead of the wheels gripping the
        // ground and resisting the pull. Idempotent; restores the saved per-wheel slip on release.
        public void SetTowedFreeRoll(bool on)
        {
            if (_wNodes == null) return;
            if (on)
            {
                if (_towSavedSlip != null) return;
                _towSavedSlip = new float[_wNodes.Length];
                for (int i = 0; i < _wNodes.Length; i++) { _towSavedSlip[i] = _wNodes[i].WheelFrictionSlip; _wNodes[i].WheelFrictionSlip = TowedFreeRollSlip; }
            }
            else if (_towSavedSlip != null)
            {
                for (int i = 0; i < _wNodes.Length && i < _towSavedSlip.Length; i++) _wNodes[i].WheelFrictionSlip = _towSavedSlip[i];
                _towSavedSlip = null;
            }
        }

        // Tie a rope: THIS car (the tower) hooks its REAR node to `towed`'s FRONT node. Rejects if either end is already
        // roped, they're the same car, either is wrecked, or they're too far apart. The rest length is the current gap
        // clamped to [TowRestMin, TowAttachReach] -> always short, and (since dist <= reach at tie) never yanks on attach.
        public bool AttachTow(Vehicle towed)
        {
            if (towed == null || towed == this || _exploded || towed._exploded) return false;
            if (Towing != null || TowedBy != null || towed.Towing != null || towed.TowedBy != null) return false;   // one rope per car end
            if (towed == CoupledTrailer || towed == CoupledCab || this == towed.CoupledTrailer || this == towed.CoupledCab) return false;   // already joined by the semi hitch -> don't double-link (they share the collision-exception set)
            float gap = RearTowWorld.DistanceTo(towed.FrontTowWorld);
            if (gap > TowAttachReach) return false;   // walk the cars closer first
            _towRestLen = Mathf.Clamp(gap, TowRestMin, TowAttachReach);   // ceiling == attach reach -> restLen is exactly the current gap, never < it (no snap-on-attach yank)
            Towing = towed; towed.TowedBy = this;
            towed.SetTowedFreeRoll(true);   // the towed car free-rolls so the rope can drag it (else its grippy wheels resist the pull)
            AddCollisionExceptionWith(towed); towed.AddCollisionExceptionWith(this);   // a short rope keeps them close -> don't let the bumpers bash
            _rope = new TowRope();
            GetParent().AddChild(_rope);   // sibling in the world (like the hitch joint)
            _rope.SetEndpoints(RearTowWorld, towed.FrontTowWorld, _towRestLen);
            Wake(); towed.Wake(); Sleeping = false; towed.Sleeping = false;   // both dynamic + awake so the pull force takes effect
            return true;
        }

        // Untie the rope. Callable on the tower OR the towed car (resolves to the tower). Frees the visual + restores collision.
        public void DetachTow()
        {
            var tower = Towing != null ? this : (TowedBy != null ? TowedBy : null);
            if (tower == null || tower.Towing == null) return;
            var towed = tower.Towing;
            if (tower._rope != null && IsInstanceValid(tower._rope)) tower._rope.QueueFree();
            tower._rope = null; tower.Towing = null;
            if (towed != null)
            {
                towed.TowedBy = null;
                towed.SetTowedFreeRoll(false);   // restore the towed car's grippy wheels now it drives/parks on its own again
                tower.RemoveCollisionExceptionWith(towed); towed.RemoveCollisionExceptionWith(tower);
                towed.Wake();
            }
            tower.Wake();
        }

        // Per-tick spring-tension pull (runs on the TOWER's _PhysicsProcess). The rope only PULLS: past its rest length it
        // applies a damped spring force dragging the two tow nodes together (towed forward, tower back = the load); slack
        // does nothing. Snaps on overstretch (TowBreakLen) or a wrecked car. Redraws the rope every tick.
        void UpdateTow(float delta)
        {
            var towed = Towing;
            if (towed == null || !IsInstanceValid(towed) || _exploded || towed._exploded) { DetachTow(); return; }
            Vector3 a = RearTowWorld, b = towed.FrontTowWorld, d = b - a;
            float dist = d.Length();
            if (dist > TowBreakLen) { DetachTow(); return; }   // yanked apart -> the rope snaps
            if (_rope != null) _rope.SetEndpoints(a, b, _towRestLen);
            if (dist < 1e-3f) return;
            Vector3 dir = d / dist;
            if (dist > _towRestLen)   // in tension: pull the nodes together (a rope can't push)
            {
                float stretch = dist - _towRestLen;
                float sepVel = (towed.LinearVelocity - LinearVelocity).Dot(dir);   // >0 = separating -> damping ADDS tension
                float f = Mathf.Clamp(TowStiffness * stretch + TowDamping * sepVel, 0f, TowMaxForce);
                // keep both bodies awake + dynamic: a settled car is Sleeping (and Godot ignores continuous ApplyForce on
                // a sleeping body) and may be Freeze-Static from the park settle -- Wake clears Freeze, Sleeping=false clears sleep.
                towed.Wake(); Wake(); towed.Sleeping = false; Sleeping = false;
                // positioned forces (offset from each body's CoM, world frame): towed pulled toward the tower (forward +
                // yaws its nose to follow), tower pulled toward the towed (the drag/load).
                towed.ApplyForce(-dir * f, towed.FrontTowWorld - towed.ToGlobal(towed.CenterOfMass));
                ApplyForce(dir * f, RearTowWorld - ToGlobal(CenterOfMass));
            }
        }

        // --- Sky-crane winch + electromagnet -------------------------------------------------------------
        // The cable is the tow rope's model turned on its side: a PULL-ONLY damped spring, so slack does nothing and
        // tension drags both ends together. That is what makes the load a real pendulum -- yaw hard and it lags behind,
        // stop and it keeps swinging -- instead of an animation glued under the hull.
        // SPRING CONSTANTS ARE DERIVED FROM THE SUSPENDED MASS, not copied. The first cut reused the tow rope's
        // numbers (k=26000, c=4200), which are tuned to drag ~900 kg cars; hung on a 40 kg magnet the damping term
        // alone reached 54,600 N as the cable went taut, i.e. 1365 m/s^2 on the magnet -- a single tick overshot by
        // more than the approach speed and FLUNG the magnet back up at the aircraft, which then rang. A fixed k is
        // wrong on both ends anyway: the same cable has to hold an empty coil and a car.
        //
        // So pick the RESPONSE and solve for the constants: k = m*w^2, c = 2*zeta*m*w. Static stretch under load is
        // then m*g/k = g/w^2 = 0.20 m whatever is on the hook, and stability is structural rather than tuned --
        // c <= m/dt and k <= m/dt^2 hold by a wide margin at any mass, so it cannot explode at the physics rate.
        const float SlingOmega = 7.0f;         // rad/s: cable response. Soft enough to be stable, stiff enough not to read as elastic
        const float SlingZeta = 0.9f;          // near-critical: a winch snatch arrests, it does not bounce
        // Clamp in g-ish terms, scaled by the load, so the guard means the same thing empty or full. 60 was a pure
        // anti-explosion backstop and far too permissive as a WINCH limit: hauling full collective off the ground with
        // a container on the hook snapped the cable taut and dealt the load 6g, which threw it up PAST the aircraft
        // and left the cable slack with the freight above the rotor. 25 still gives 2.5x the 9.8 needed to lift
        // anything, while making the cable behave like a winch rather than a catapult.
        const float SlingMaxAccel = 25f;
        // A WINCH PAYS OUT; IT DOES NOT DROP. Deploying to full length instantly let the magnet free-fall the whole
        // 9 m and hit 13 m/s before the cable caught it, and arresting that snatch costs far more than the sky-crane's
        // entire 2160 N spare thrust -- so every deployment yanked the aircraft down, it rebounded, and the machine
        // sank and crashed at FULL collective. Paying the cable out at a controlled rate keeps tension near the load's
        // static weight, which is the only regime the airframe can actually afford.
        const float SlingPayoutRate = 2.5f;    // m/s of cable out (and back in when stowing)
        // ANTI-SWAY. Damps the load's velocity RELATIVE TO THE AIRCRAFT, perpendicular to the cable -- not its
        // absolute velocity, which is what LinearDamp did and what towed the aircraft backwards. Hanging plumb at
        // cruise the load moves exactly with the airframe, so the relative velocity is zero and this costs nothing;
        // it only bites on an actual swing. That is what a crane's anti-sway system does, and it is why "stop the
        // swinging" and "no drag" are not in conflict after all.
        const float SwayDamp = 2.6f;           // 1/s on the cross-cable relative velocity (~half-critical on a 9 m pendulum)
        // BRIDLE. Two spread attachments instead of one hook, so the magnet cannot pivot freely at the cable end.
        // Modelled as an alignment torque rather than a literal second rope constraint: two stiff positional
        // constraints on one rigid body is over-constrained and buzzes in the solver, whereas a torque toward the
        // cable axis is exactly the couple a real bridle applies and is unconditionally stable.
        const float BridleStiff = 9f, BridleDamp = 3.2f;
        const float BridleForkGap = 1.6f;      // how far above the coil the cable ends at the master link
        const int BridleLegs = 4;              // legs from the link down to the coil, spaced around its rim
        // HANDLING SCALES WITH WHAT IS ON THE HOOK (strawberry: "the current handling of the skycrane should be
        // when we are hauling a heavy object, with nothing we should handle a lot better"). The SPEC figures stay
        // the LOADED case, and an empty hook multiplies them up.
        //
        // The bonus is CAPPED at 1.30 for a reason worth keeping: the fleet's agility ordering is deliberate and
        // inverse to weight (Hummingbird 2.16 > Huey 1.32 > Orca 1.07 > Hind 0.81 > Skycrane 0.59), and 0.59 * 1.30
        // = 0.767 keeps the empty crane just under the Hind. Any more and a 21 t crane out-handles a gunship, which
        // is a fleet-identity decision rather than a tuning one -- see HeliFlightTests.
        const float SlingAgilityBonus = 1.30f;
        const float SlingAgilityLoadRef = 600f;   // kg on the hook at which handling is back to the spec figure

        // 1.0 for every airframe without a hook -- this must never quietly buff the rest of the fleet.
        float SlingAgility
        {
            get
            {
                if (!_slingHook) return 1f;
                float carried = _magnet != null && IsInstanceValid(_magnet)
                    ? _magnet.Mass + (_magnet.Held != null && IsInstanceValid(_magnet.Held) ? _magnet.Held.Mass : 0f)
                    : 0f;
                return Mathf.Lerp(SlingAgilityBonus, 1f, Mathf.Clamp(carried / SlingAgilityLoadRef, 0f, 1f));
            }
        }
        const float SlingStowSpeed = 1.5f;     // m/s ground speed under which a landed crane reels the magnet back in

        // Shift, from the cockpit. De-energising is also how the load is PUT DOWN, so this is the whole control.
        public void ToggleSlingMagnet()
        {
            _magnetWanted = !_magnetWanted;
            if (_magnet != null && IsInstanceValid(_magnet)) _magnet.SetMagnetized(_magnetWanted);
        }

        void DeploySling()
        {
            if (_magnet != null && IsInstanceValid(_magnet)) return;
            var m = new SlingMagnet { Name = "SlingMagnet" };
            GetParent().AddChild(m);   // a sibling in the world, like the tow rope's joint -- NOT a child, or it would ride the hull rigidly
            // Born just under the belly and allowed to FALL to cable length, so deploying reads as paying out a winch
            // rather than teleporting a magnet to the end of a taut wire.
            m.GlobalPosition = ToGlobal(_slingVisualAnchor) + Vector3.Down * 1.2f;
            _slingOut = 1.2f;   // and the winch starts there, paying out from the hull rather than dropping to full length
            m.LinearVelocity = LinearVelocity;   // match the aircraft or it gets left behind the instant it spawns
            m.AddCollisionExceptionWith(this); AddCollisionExceptionWith(m);
            m.SetMagnetized(_magnetWanted);
            _magnet = m;
            _slingCable = new TowRope();
            GetParent().AddChild(_slingCable);
            // ONE cable down to a MASTER LINK, then four legs fanning onto the coil (strawberry: "one rope to a
            // link, then 3-4 from link onto the magnet"). That is how a real lifting magnet is slung.
            _slingLegs = new TowRope[BridleLegs];
            for (int i = 0; i < BridleLegs; i++) { _slingLegs[i] = new TowRope(); GetParent().AddChild(_slingLegs[i]); }
            _slingLink = new MeshInstance3D
            {
                Mesh = new TorusMesh { InnerRadius = 0.13f, OuterRadius = 0.24f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.23f, 0.26f), Metallic = 0.9f, Roughness = 0.35f },
            };
            GetParent().AddChild(_slingLink);
        }

        void StowSling()
        {
            if (_slingHeldPrev != null && IsInstanceValid(_slingHeldPrev)) RemoveCollisionExceptionWith(_slingHeldPrev);
            _slingHeldPrev = null;
            if (_magnet != null && IsInstanceValid(_magnet)) { _magnet.Release(); RemoveCollisionExceptionWith(_magnet); _magnet.QueueFree(); }
            if (_slingCable != null && IsInstanceValid(_slingCable)) _slingCable.QueueFree();
            if (_slingLegs != null) foreach (var l in _slingLegs) if (l != null && IsInstanceValid(l)) l.QueueFree();
            if (_slingLink != null && IsInstanceValid(_slingLink)) _slingLink.QueueFree();
            _magnet = null; _slingCable = null; _slingLegs = null; _slingLink = null;
        }

        void UpdateSling(float delta)
        {
            // "Dangles below the heli when in flight" -- so it deploys on leaving the ground and reels in on landing,
            // but NEVER while it is holding something: reeling in with a car on the magnet would delete the car.
            bool airborne = !GroundedByRay() && !_exploded;
            if (airborne) { if (_magnet == null && !DebugNoSling) DeploySling(); }
            else if (_magnet != null && _magnet.Held == null && LinearVelocity.Length() < SlingStowSpeed) { StowSling(); return; }
            if (_magnet == null || !IsInstanceValid(_magnet)) return;

            _slingOut = Mathf.MoveToward(_slingOut, _slingLen, SlingPayoutRate * delta);
            // The two anchors: FORCE at the CoM (fa) so a hanging load applies no pitching moment, DRAW at the leg
            // line (a) so the rope reads as coming out of the gear. The cable's own droop/tension geometry (a, dist,
            // _slingOut) is all computed from the visual point; only the actual push/pull uses the force point.
            Vector3 fa = ToGlobal(_slingAnchor), a = ToGlobal(_slingVisualAnchor), b = _magnet.GlobalPosition, d = b - a;
            float dist = d.Length();
            // The single cable runs to a JUNCTION just above the coil; the bridle legs carry on from there.
            Vector3 fork = dist > 1e-3f ? b + (a - b) / dist * BridleForkGap : b + Vector3.Up * BridleForkGap;
            if (_slingCable != null && IsInstanceValid(_slingCable)) _slingCable.SetEndpoints(a, fork, Mathf.Max(0.1f, _slingOut - BridleForkGap));

            if (dist > 1e-3f && dist > _slingOut)   // in tension: a cable pulls, it never pushes
            {
                Vector3 dir = d / dist;
                float sepVel = (_magnet.LinearVelocity - LinearVelocity).Dot(dir);   // >0 = separating -> damping ADDS tension
                // The mass the cable is actually carrying: the coil, plus whatever is welded to it.
                float susp = _magnet.Mass + (_magnet.Held != null && IsInstanceValid(_magnet.Held) ? _magnet.Held.Mass : 0f);
                float k = susp * SlingOmega * SlingOmega, c = 2f * SlingZeta * susp * SlingOmega;
                float f = Mathf.Clamp(k * (dist - _slingOut) + c * sepVel, 0f, susp * SlingMaxAccel);
                _magnet.Sleeping = false; Wake(); Sleeping = false;
                _magnet.ApplyForce(-dir * f, Vector3.Zero);                       // load hauled up toward the aircraft
                ApplyForce(dir * f, fa - ToGlobal(CenterOfMass));                 // FORCE point, not the draw point -- this is what stays torque-free
            }

            if (dist > 1e-3f)
            {
                Vector3 dir2 = d / dist;
                float susp2 = _magnet.Mass + (_magnet.Held != null && IsInstanceValid(_magnet.Held) ? _magnet.Held.Mass : 0f);
                // Cross-cable RELATIVE velocity: the swing, with the along-cable part (the spring's business) removed.
                Vector3 rel = _magnet.LinearVelocity - LinearVelocity;
                Vector3 perp = rel - dir2 * rel.Dot(dir2);
                // SCALE BY THE MAGNET'S OWN MASS, not the suspended total. These forces are applied to the MAGNET
                // body, whose inertia is its own 12 kg -- sizing them for a welded 800 kg load meant a 68x overshoot
                // on the body actually receiving them, and the solver diverged to NaN the moment anything was
                // picked up. (The cable SPRING legitimately uses the suspended mass: it acts along the weld, where
                // magnet and load genuinely move as one.) A heavy load now damps and aligns more slowly, which is
                // also the physically honest answer.
                Vector3 fSway = -perp * (SwayDamp * _magnet.Mass);
                _magnet.ApplyForce(fSway, Vector3.Zero);
                ApplyForce(-fSway, fa - ToGlobal(CenterOfMass));   // equal and opposite, at the FORCE point
                // Bridle: hold the coil's axis along the cable so it hangs face-down instead of tumbling.
                Vector3 up = _magnet.GlobalBasis.Y, want = -dir2;
                Vector3 bridle = up.Cross(want) * (BridleStiff * _magnet.Mass) - _magnet.AngularVelocity * (BridleDamp * _magnet.Mass);
                if (bridle.IsFinite()) _magnet.ApplyTorque(bridle);
                if (_slingLink != null && IsInstanceValid(_slingLink)) _slingLink.GlobalPosition = fork;
                if (_slingLegs != null)
                    for (int li = 0; li < _slingLegs.Length; li++)
                    {
                        if (_slingLegs[li] == null || !IsInstanceValid(_slingLegs[li])) continue;
                        Vector3 foot = _magnet.RimWorldAt(Mathf.Tau * li / _slingLegs.Length);
                        _slingLegs[li].SetEndpoints(fork, foot, fork.DistanceTo(foot));   // taut: rest == actual, so no droop on a short leg
                    }
            }

            // THE AIRCRAFT MUST NOT COLLIDE WITH WHAT IT IS CARRYING. The magnet already has an exception with the
            // hull, but the LOAD did not -- so a crane that descended onto its own container simply sat on it, at
            // full collective, going nowhere, with the load pinned to the ground underneath. Nothing about the lift
            // maths was wrong; the machine was standing on its own cargo.
            if (_magnet.Held != _slingHeldPrev)
            {
                if (_slingHeldPrev != null && IsInstanceValid(_slingHeldPrev)) RemoveCollisionExceptionWith(_slingHeldPrev);
                if (_magnet.Held != null && IsInstanceValid(_magnet.Held)) AddCollisionExceptionWith(_magnet.Held);
                _slingHeldPrev = _magnet.Held;
            }

            if (_magnetWanted && _magnet.Held == null)   // energised + empty -> bite the first thing the coil touches
            {
                var skip = new Godot.Collections.Array<Rid> { GetRid(), _magnet.GetRid() };
                var t = _magnet.FindGrabTarget(skip);
                if (t != null) _magnet.Grab(t);
            }
        }

        public override void _ExitTree()   // a despawned/unloaded car drops its rope (either end) so no dangling TowedBy/Towing ref survives
        {
            _live.Remove(this);
            GrassDisplacers.Unregister(this);
            if (Towing != null || TowedBy != null) DetachTow();
            if (_magnet != null) StowSling();   // a despawned/wrecked crane must not leave an orphan magnet hanging in the sky
        }

        // Swap this trailer's body layer bit0->bit6 while a cab is coupled/backing under. This is ONLY for the cab's
        // rear-WHEEL raycasts (which ignore collision exceptions but DO respect the cab's collision_mask=bit0): off bit0,
        // the wheels stop riding up the trailer's front hulls. Body-vs-body ghosting is the exception's job (below). The
        // player (mask bit6) still collides, so no hole. Idempotent. (strawberry 2026-07-15)
        public void SetTowGhost(bool ghost)
        {
            // SolidBit, not a literal bit 0. The sibling ghost path at SetGhosted was updated for the mesh
            // hitbox and this one was not, so with the hitbox on `& ~(1u << 0)` cleared a bit the base layer no
            // longer has: the trailer stayed solid and was never actually ghosted.
            uint wantLayer = ghost ? (_baseCollisionLayer & ~SolidBit) | (1u << 6) : _baseCollisionLayer;
            if (CollisionLayer != wantLayer) CollisionLayer = wantLayer;
            // Also SCAN bit6 while ghosted so the towing cab's separate sleeper hull (layer bit6) still blocks this
            // trailer -> the deck/headboard can't phase through the sleeper (anti-clip). The cab body never scans bit6,
            // so ghosting the two bodies from each other is untouched. (strawberry 2026-07-16)
            uint wantMask = ghost ? _baseCollisionMask | (1u << 6) : _baseCollisionMask;
            if (CollisionMask != wantMask) CollisionMask = wantMask;
        }

        Vehicle _approachGhost;   // cab-side: the uncoupled trailer this cab is currently ghosting itself against to back under

        // Cab-side, every physics frame while uncoupled: find a trailer we're backing under (fifth wheel within
        // ApproachReach of its kingpin) and GHOST ourselves against it -- a symmetric collision exception (cab body <->
        // trailer body ignore each other, BOTH directions, so the low deck+legs don't wall the cab off) PLUS the trailer
        // layer swap (kills the rear-wheel ride-up). Both are cab<->trailer ONLY: the player/world still hit both, no
        // holes. Dropped when we leave range. (strawberry 2026-07-15)
        void UpdateTrailerApproach()
        {
            if (!CanTow || CoupledTrailer != null) return;   // coupled -> CoupleTo owns the exception+ghost; leave it
            Vehicle near = null; float best = ApproachReach * ApproachReach;
            var fw = FifthWheelWorld;
            foreach (var n in GetTree().GetNodesInGroup("vehicles"))
                if (n is Vehicle v && v != this && v.IsTrailer && v.CoupledCab == null && fw.DistanceSquaredTo(v.KingpinWorld) < best) { near = v; break; }
            if (near == _approachGhost) return;
            if (_approachGhost != null && IsInstanceValid(_approachGhost) && _approachGhost.CoupledCab != this)
            { RemoveCollisionExceptionWith(_approachGhost); _approachGhost.SetTowGhost(false); }   // left the one we were lining up under
            _approachGhost = near;
            if (near != null) { AddCollisionExceptionWith(near); near.SetTowGhost(true); }
        }

        // Cab-side, every physics frame while COUPLED: keep the rig sane -- drop the trailer on a rollover or a hard
        // clip, and clamp the jackknife so the trailer can't fold into the cab. (strawberry 2026-07-15)
        void UpdateCoupled(Vehicle trailer, float delta)
        {
            // rollover: cab or trailer tipped past RollDisconnectDeg from upright -> drop the trailer
            if (TiltDegrees() > RollDisconnectDeg || trailer.TiltDegrees() > RollDisconnectDeg) { Uncouple(); return; }
            // clipped something: the trailer's SPEED drops hard vs ours while we're moving -> the coupling can't hold it,
            // so yank it off. Use speed MAGNITUDE difference (not the velocity vector) so hard turns -- where cab+trailer
            // move at the same speed in different directions -- don't false-rip. Persist ~0.15s so a bump doesn't rip it.
            float mismatch = Mathf.Abs(LinearVelocity.Length() - trailer.LinearVelocity.Length());
            if (LinearVelocity.Length() > 3f && mismatch > 7f) _ripTimer += delta; else _ripTimer = 0f;
            if (_ripTimer > 0.15f) { _ripTimer = 0f; Uncouple(); return; }
            ClampJackknife(trailer);
            trailer.DriveTrailerLights(EngineOn && Battery > 0f, _braking);   // pass the cab's running + brake state through to the trailer's brake lights
        }

        // total tilt (roll+pitch) of this body from upright: angle between its up axis and world up, in degrees.
        float TiltDegrees() => Mathf.RadToDeg(GlobalTransform.Basis.Y.AngleTo(Vector3.Up));

        // Clamp the trailer's yaw to +-JackknifeLimit of the cab heading. The PinJoint allows free rotation, so when the
        // relative yaw exceeds the limit we rotate the trailer back to it about the kingpin (keeps the pin satisfied) and
        // kill the angular velocity that pushed past -- a wall the trailer can't fold through into the cab.
        void ClampJackknife(Vehicle trailer)
        {
            Vector3 cabF = -GlobalTransform.Basis.Z; cabF.Y = 0f;
            Vector3 trlF = -trailer.GlobalTransform.Basis.Z; trlF.Y = 0f;
            if (cabF.LengthSquared() < 1e-4f || trlF.LengthSquared() < 1e-4f) return;
            cabF = cabF.Normalized(); trlF = trlF.Normalized();
            float yaw = cabF.SignedAngleTo(trlF, Vector3.Up);
            float lim = Mathf.DegToRad(JackknifeLimit);
            if (Mathf.Abs(yaw) <= lim) return;
            float excess = yaw - Mathf.Sign(yaw) * lim;
            Vector3 pivot = trailer.KingpinWorld;
            var rot = new Basis(Vector3.Up, -excess);
            var xf = trailer.GlobalTransform;
            xf.Origin = pivot + rot * (xf.Origin - pivot);
            xf.Basis = (rot * xf.Basis).Orthonormalized();
            trailer.GlobalTransform = xf;
            var av = trailer.AngularVelocity;
            if (Mathf.Sign(av.Y) == Mathf.Sign(yaw)) { av.Y = 0f; trailer.AngularVelocity = av; }   // stop pushing further past the limit
        }

        public float ForwardSpeedPct()   // source GetReplicatedForwardSpeedPercentageOfTargetSpeed: forward speed / top speed (0..1) for the DRIVING stealth radius
        {
            if (_speedMax <= 0f) return 0f;
            float fwd = LinearVelocity.Dot(-GlobalTransform.Basis.Z);   // signed: reversing clamps to 0 (quiet)
            return Mathf.Clamp(fwd / _speedMax, 0f, 1f);
        }

        public void Honk()   // source tellHorn: one-shot the horn; 0.5s cooldown (canUseHorn) + needs battery charge
        {
            if (_hornCd > 0f || Battery <= 0f || _hornAudio == null || _alarmTimer > 0f) return;   // can't manually honk while the alarm's blaring (master)
            DoHorn();
            _hornCd = 0.5f;
        }
        void DoHorn()   // the actual honk: a pitch-varied one-shot (master: slight variation per honk) + a sound-bus hearing alert
        {
            if (_hornAudio == null) return;
            _hornAudio.Play();
            SoundBus.Emit(GetTree(), GlobalPosition, SoundBus.Horn);   // Phase 3 sound bus: horn loudness (source tellHorn AlertTool.alert(pos,32))
        }
        void TriggerAlarm() { if (_alarmed && !_exploded && _alarmTimer <= 0f) { _alarmTimer = 30f; _alarmBlip = 0f; } }   // start the ~30s honk+lights alarm loop (master); a wreck never alarms -- damage still lands on corpses

        public void ToggleHeadlights() { if (_alarmTimer > 0f) return; SetHeadlights(!_headlightsOn); }   // source tellHeadlights; blocked while the alarm owns the lights (master)
        void SetHeadlights(bool on)
        {
            _headlightsOn = on && Battery > 0f;   // a dead battery can't power the lights
            if (_headlights != null) _headlights.Visible = _headlightsOn;
            if (_headlightBeam != null) _headlightBeam.Visible = _headlightsOn;
            ApplyHeadlightMotes();
            if (_lampNodes.Count > 0) { ApplyLampState(); return; }   // per-side lamps own the emission
            if (_headlightMat != null)   // source: lamp emission = colour*2 when lit, off otherwise
            {
                _headlightMat.EmissionEnabled = _headlightsOn;
                if (_headlightsOn) { _headlightMat.Emission = _lampTint; _headlightMat.EmissionEnergyMultiplier = 2f; }
            }
        }

        // THE HEADLIGHT SHAFT (strawberry). Built from the vehicle's OWN headlight lens mesh, so the beam leaves
        // the car as the shape of the lamps emitting it -- a jeep's hexagons, a sedan's rectangles -- and merges
        // into one solid volume rather than two cones that cross and double-brighten. See HeadlightBeam.
        void BuildHeadlightBeam(Mesh lensMesh)
        {
            if (_headlightBeam != null) return;
            var verts = lensMesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (verts.Length < 3) return;
            float mnX = 9e9f, mxX = -9e9f, frontZ = 9e9f;
            foreach (var q in verts) { mnX = Mathf.Min(mnX, q.X); mxX = Mathf.Max(mxX, q.X); frontZ = Mathf.Min(frontZ, q.Z); }
            float midX = (mnX + mxX) * 0.5f;
            var left = new System.Collections.Generic.List<Vector2>();
            Vector2 c = Vector2.Zero; int n = 0;
            foreach (var q in verts) if (q.X < midX) { left.Add(new Vector2(q.X, q.Y)); c += new Vector2(q.X, q.Y); n++; }
            if (n < 3) return;
            c /= n;
            var hull = HeadlightBeam.Hull(left);
            // 4 corners = rectangle; more = a polygon standing in for a round lamp (a jeep hulls to 6).
            _lampRound = hull.Length >= 5;
            _lampTint = StreetLight.KelvinToColor(_lampRound ? LampKelvinRound : LampKelvinRect);
            var mesh = HeadlightBeam.Build(hull, c, new Vector2(-c.X, c.Y), BeamLength, BeamSpread, 0.30f, BeamVertical);
            if (mesh == null) return;

            // Warmer than the lens itself (strawberry) and additive, so it reads as light in the air rather than a
            // surface. The gradient runs bright at the lamp -> transparent by the far end, which is the fade being
            // asked for; unlike the streetlight cone this samples the FULL v range because the mesh is hand-built
            // (CylinderMesh reserves the top half of v for its caps -- see StreetLight.BeamMesh).
            var mat = new StandardMaterial3D
            {
                AlbedoColor = new Color(_lampTint.R, _lampTint.G, _lampTint.B, BeamAlpha),
                AlbedoTexture = BeamGradient(),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DisableReceiveShadows = true,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                TextureRepeat = false,   // linear sampling wraps v=0 into the bright end otherwise -- the phantom
                                          // band that read as a second cone on the streetlight (StreetLight)
            };
            _headlightBeam = new MeshInstance3D
            {
                Name = "HeadlightBeam", Mesh = mesh, MaterialOverride = mat,
                Position = new Vector3(0f, 0f, frontZ), Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEnd = BeamCull, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
            };
            AddChild(_headlightBeam);

            // DUST IN THE BEAM (strawberry) -- night only, on the SAME fade curve as the streetlight motes, and
            // culled the same way. The timing is not re-derived here: StreetLight.MoteFadeFor IS the curve, so
            // when it is retuned both follow. Copying it is how the "one definition of lit" bug happened earlier.
            if (StreetLight.MoteCount > 0)
            {
                var mm = new StandardMaterial3D
                {
                    AlbedoColor = new Color(_lampTint.R, _lampTint.G, _lampTint.B, StreetLight.MoteOpacity),
                    EmissionEnabled = true, Emission = _lampTint, EmissionEnergyMultiplier = 2.2f,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                    DisableReceiveShadows = true,
                };
                var ab = mesh.GetAabb();
                _headlightMotes = new CpuParticles3D { 
                    Position = new Vector3(0f, 0f, frontZ),
                    Amount = ParticleFx.Amount(StreetLight.MoteCount), Lifetime = 7f, Preprocess = 7f,   // start at steady state
                    Randomness = 1f, Emitting = false, Visible = false,
                    Mesh = new QuadMesh { Size = new Vector2(0.0495f, 0.0495f) },
                    EmissionShape = CpuParticles3D.EmissionShapeEnum.Points,
                    EmissionPoints = BeamPoints(hull, c, 56),
                    Direction = Vector3.Up, Spread = 180f,
                    InitialVelocityMin = 0.02f, InitialVelocityMax = 0.14f,
                    Gravity = new Vector3(0f, -0.03f, 0f),
                    ScaleAmountMin = 0.6f * ParticleFx.SizeScale, ScaleAmountMax = 1.5f * ParticleFx.SizeScale,
                    AngleMin = -180f, AngleMax = 180f,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    VisibilityRangeEnd = StreetLight.MoteCullRange,
                    VisibilityRangeEndMargin = StreetLight.MoteFadeMargin,
                    VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
                    CustomAabb = ab.Grow(0.5f),   // explicit: a slow drifter's auto bounds collapse toward the
                                                   // emitter and the whole system pops out at glancing angles
                    MaterialOverride = mm,
                };
                _hlMoteBase = mm.AlbedoColor;
                AddChild(_headlightMotes);
            }
        }

        /// <summary>Points sampled inside the LOBES themselves, never the space between them (strawberry: "the
        /// motes should be killed when they are outside of the cones"). The previous version sampled one box
        /// spanning both beams and tapered it with depth, which scattered dust down the dark gap between the two
        /// cones -- visible as motes hanging in unlit air. Each point now picks a side and lands within that
        /// lobe's own cross-section at its depth, so every mote is inside light by construction.</summary>
        static Vector3[] BeamPoints(Vector2[] hull, Vector2 lc, int n)
        {
            var pts = new Vector3[n];
            uint seed = 0x9E3779B9;
            float Rnd() { seed = seed * 1664525u + 1013904223u; return (seed >> 8) * (1f / 16777216f); }
            for (int i = 0; i < n; i++)
            {
                float t = Mathf.Pow(Rnd(), 0.65f);            // bias toward the car, where the beam is brightest
                var half = HeadlightBeam.LobeHalf(hull, lc, BeamSpread, BeamVertical, t);
                float side = Rnd() < 0.5f ? 1f : -1f;          // one lamp or the other
                float cxs = side * Mathf.Abs(lc.X);
                pts[i] = new Vector3(cxs + (Rnd() * 2f - 1f) * half.X,
                                     lc.Y + (Rnd() * 2f - 1f) * half.Y,
                                     -t * BeamLength);
            }
            return pts;
        }

        /// <summary>Drive the beam dust from the world clock. Night-only falls out of the curve itself -- it is
        /// zero through the day -- and the lamps still have to be ON.</summary>
        public void SetHeadlightMoteFade(float a)
        {
            _hlMoteFade = Mathf.Clamp(a, 0f, 1f);
            ApplyHeadlightMotes();
        }

        void ApplyHeadlightMotes()
        {
            if (_headlightMotes == null) return;
            float a = (_headlightsOn && !_exploded) ? _hlMoteFade : 0f;
            _headlightMotes.Emitting = a > 0.001f;
            _headlightMotes.Visible = a > 0.001f;
            if (_headlightMotes.MaterialOverride is StandardMaterial3D m)
                m.AlbedoColor = new Color(_hlMoteBase.R, _hlMoteBase.G, _hlMoteBase.B, _hlMoteBase.A * a);
        }

        // Effective density = BeamAlpha * the gradient. The streetlight shaft lands at ~0.022 (0.07 albedo x a
        // gradient that only reaches 0.31 because CylinderMesh gives its side half the v range). This mesh samples
        // the full gradient, so matching that look means the albedo alpha IS the density -- 0.055 rendered as a
        // solid tan slab, 2.5x the tuned streetlight.
        public static float BeamAlpha  = 0.020f;
        public static float BeamVertical = 0.40f;   // vertical spread as a fraction of horizontal
        public static float BeamSpread = 22f;   // wider (strawberry)   // how much each lobe grows over the throw (strawberry: much wider)
        public static float BeamLength = 14f;   // shorter throw (strawberry)
        public static float BeamCull   = 90f;   // it is a close-range detail; retire it well before the car does

        // bright at the lamp, gone by the far end -- v runs 0 at the lens to 1 at the tip of the throw
        static ImageTexture BeamGradient()
        {
            int n = 64;
            var img = Image.CreateEmpty(1, n, false, Image.Format.Rgba8);
            for (int y = 0; y < n; y++)
            {
                float t = (float)y / (n - 1);
                img.SetPixel(0, y, new Color(1f, 1f, 1f, Mathf.Pow(1f - t, 1.9f)));
            }
            return ImageTexture.CreateFromImage(img);
        }

        void SetTaillights(bool on)   // running taillights: red glow while driven (source: emission = colour*2)
        {
            _taillightsOn = on;
            if (_taillights != null) _taillights.Visible = on;
            if (_lampNodes.Count > 0) { ApplyLampState(); return; }   // per-side lamps own the emission
            if (_taillightMat != null)
            {
                _taillightMat.EmissionEnabled = on;
                if (on) { _taillightMat.Emission = new Color(0.56f, 0.13f, 0.13f); _taillightMat.EmissionEnergyMultiplier = 2f; }
            }
        }

        // Cab drives the TRAILER's tail/brake lights while coupled (the trailer has no engine of its own, so its own
        // synchronizeTaillights never fires). running = cab powered; braking = cab on the brake -> flare. The trailer skips
        // its own taillight logic while CoupledCab != null so the two don't fight. (strawberry: brake-light pass-through)
        public void DriveTrailerLights(bool running, bool braking)
        {
            if (_taillightsOn != running) SetTaillights(running);
            if (running) SetTailFlare(braking);   // BOTH tail lamps (the baked lens is split L/R with a material each; _taillightMat alone was the left one -- strawberry 2026-09-04 "only the left brake light works")
        }

        // A real colored light cast from a lightbar lens (source Siren_0/Siren_1 are GameObjects with Unity Lights).
        // Placed at the lens mesh's centre so red emits from one side + blue from the other; off until it flashes.
        static OmniLight3D AddSirenLight(MeshInstance3D mi, Color c)
        {
            var center = mi.Mesh != null ? mi.Mesh.GetAabb().GetCenter() : Vector3.Zero;
            var light = new OmniLight3D { Position = center, OmniRange = 12f, LightColor = c, LightEnergy = 0f, ShadowEnabled = false, OmniAttenuation = 1.5f };
            light.AddToGroup("dynlight");   // spills onto the FP gun via the viewmodel light-scan (master)
            mi.AddChild(light);
            return light;
        }

        // look-at focus (master): the eye-sphere is on this vehicle -> screen-space outline (add the outline layer to every
        // vehicle mesh so OutlineOverlay's mask cam picks them up) + the info billboard. E enters (PlayerController).
        public void SetLookFocused(bool on)
        {
            if (_lookFocused == on) return;
            _lookFocused = on;
            if (on || _outlineMeshes == null)   // (re)collect on FOCUS -- a settled wreck dropped its wheels, so a stale cached list would hold FREED refs
            {
                _outlineMeshes = new System.Collections.Generic.List<MeshInstance3D>();
                CollectMeshes(this, _outlineMeshes);
            }
            foreach (var mi in _outlineMeshes)
                if (IsInstanceValid(mi))   // guard freed husk meshes -- else the loop threw + aborted, leaving later meshes stuck ON the layer (outline "never reset", master)
                    mi.Layers = on ? (mi.Layers | OutlineOverlay.OutlineLayer) : (mi.Layers & ~OutlineOverlay.OutlineLayer);
            if (on) WorldItem.FocusColor = _outlineColor;   // OutlineOverlay tints the rim with this
            _info?.SetActive(on);
        }

        static void CollectMeshes(Node n, System.Collections.Generic.List<MeshInstance3D> list)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is MeshInstance3D mi) list.Add(mi);   // ALL meshes incl. seats + steering wheel -> they're part of the one combined silhouette outline now (master)
                CollectMeshes(c, list);
            }
        }

        // Union of every mesh's AABB (incl. seats/steering) in WORLD space -> the look-at can focus the whole visual
        // bounds, so looking at a seat/wheel through a window still selects the car even though they have no collider (master).
        Aabb _localMeshAabb; bool _localAabbCached;
        public Aabb WorldMeshAabb()
        {
            if (!_localAabbCached)   // the mesh set is fixed after build -> compute the VEHICLE-LOCAL union ONCE (walking the tree every frame was the look-at perf regression)
            {
                var list = new System.Collections.Generic.List<MeshInstance3D>();
                CollectMeshes(this, list);
                var inv = GlobalTransform.AffineInverse();
                Aabb acc = default; bool any = false;
                foreach (var mi in list)
                {
                    if (!IsInstanceValid(mi) || mi.Mesh == null) continue;
                    var lb = mi.Mesh.GetAabb(); var rel = inv * mi.GlobalTransform;
                    for (int i = 0; i < 8; i++)
                    {
                        var c = rel * (lb.Position + lb.Size * new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1));
                        if (!any) { acc = new Aabb(c, Vector3.Zero); any = true; } else acc = acc.Expand(c);
                    }
                }
                _localMeshAabb = any ? acc.Grow(0.1f) : new Aabb(-Vector3.One, Vector3.One * 2f);
                _localAabbCached = true;
            }
            var xf = GlobalTransform; Aabb w2 = default; bool a2 = false;   // transform the cached local box into world (8 corners -- cheap)
            for (int i = 0; i < 8; i++)
            {
                var c = xf * (_localMeshAabb.Position + _localMeshAabb.Size * new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1));
                if (!a2) { w2 = new Aabb(c, Vector3.Zero); a2 = true; } else w2 = w2.Expand(c);
            }
            return w2;
        }

        // --- Look-focus HULLS (strawberry 2026-07-15): the loose WorldMeshAabb union ballooned for long/rotated vehicles
        // -- a diagonal 16 m trailer's WORLD-AXIS box engulfs the airspace over the flatbed AND overlaps the cab's box,
        // so you'd focus empty air / the wrong half. These helpers use the vehicle's REAL box collision hulls, tested
        // ORIENTED (in each box's own frame), so the focus volume hugs the silhouette at any heading. ---
        System.Collections.Generic.List<CollisionShape3D> _lookHulls;
        System.Collections.Generic.List<CollisionShape3D> LookHulls()
        {
            if (_lookHulls == null)
            {
                _lookHulls = new();
                foreach (var ch in GetChildren())   // DIRECT box CollisionShape3D children = the body hulls (main/roof/ExtraBoxes/landing gear); the bumper's shape is an Area3D grandchild + wheels are VehicleWheel3D, so both are excluded
                    if (ch is CollisionShape3D cs && cs.Shape is BoxShape3D) _lookHulls.Add(cs);
            }
            return _lookHulls;
        }

        // Does the look segment from..to cross any box hull? Each box is tested in its OWN local frame (segment pushed
        // through the shape's inverse world xf), so the AABB test is exact for an oriented box -- no world-axis bloat.
        // NOTE: does NOT skip .Disabled hulls -- look-focus tracks the VISUAL footprint, not physics. A coupled trailer
        // physics-disables its front hulls (the cab-rear-wheel fix), but the nose is still visibly there + is exactly
        // where you stand to disconnect, so it must stay look-focusable. (strawberry 2026-07-15)
        public bool LookRayHitsHull(Vector3 from, Vector3 to)
        {
            foreach (var cs in LookHulls())
            {
                if (!IsInstanceValid(cs) || cs.Shape is not BoxShape3D box) continue;
                var inv = cs.GlobalTransform.AffineInverse();
                var half = box.Size * 0.5f;
                if (new Aabb(-half, box.Size).IntersectsSegment(inv * from, inv * to)) return true;
            }
            return false;
        }

        // Look-hull boxes as (world transform, size) -- feeds the debug wireframe overlay (PlayerController "I" toggle).
        // Includes physics-disabled hulls, matching LookRayHitsHull (the look region == the visual footprint).
        public System.Collections.Generic.IEnumerable<(Transform3D xf, Vector3 size)> LookHullBoxes()
        {
            foreach (var cs in LookHulls())
                if (IsInstanceValid(cs) && cs.Shape is BoxShape3D box)
                    yield return (cs.GlobalTransform, box.Size);
        }

        // --- Wreck salvage (master): a burnt-out car can be broken down with a blowtorch into scrap metal ---
        public bool IsWreck => _exploded;
        public bool WreckOnFire => _exploded && _burnTime >= 0f && _burnTime < 60f;   // still burning -> too hot to salvage
        public bool WreckSalvageable => _exploded && _burnTime >= 60f;                // fire's out -> can be salvaged (with a blowtorch)
        // Set the look-at prompt for a focused wreck (name + salvage line) with a state colour; PlayerController drives it (it knows the blowtorch).
        public void SetSalvagePrompt(string line2, Color color)
        {
            if (_info != null) { _info.SetName(DisplayName, color); _info.SetBar(0, 0f, InfoBillboard.HealthColor, false); _info.SetBar(1, 0f, InfoBillboard.FuelColor, false); _info.SetBar(2, 0f, InfoBillboard.FuelColor, false); _info.SetPrompt(line2, color); }
            if (_lookFocused) WorldItem.FocusColor = color;   // recolour the screen-space outline (red = can't, white = salvageable)
        }
        public bool Hurt => !_exploded && Health < HealthMax;   // alive-but-damaged -> a blowtorch can repair it (source isRepair, master)
        // Blowtorch repair: heal HP up to max (source: isRepair heals instead of damaging). On a HELICOPTER it
        // also brings the rotors back (strawberry 2026-08-16: "blowtorching the hull will repair the rotors") --
        // otherwise a machine could be welded to full health and still be unflyable with no way to fix it, which
        // is a dead end rather than a difficulty. Rotors heal at the same rate as the hull, scaled to their own
        // smaller maxima so a full hull repair is also a full rotor repair.
        public void Repair(float amount)
        {
            if (_exploded) return;
            Health = Mathf.Min(HealthMax, Health + amount);
            if (!EngineDrowned && HealthMax > 0f) EngineHealth = Mathf.Min(EngineHealthMax, EngineHealth + EngineHealthMax * (amount / HealthMax));   // same fraction to the engine; a drowned one is past repair
            if (!_heli || HealthMax <= 0f) return;
            float frac = amount / HealthMax;
            _mainRotorHp = Mathf.Min(_mainRotorHpMax, _mainRotorHp + _mainRotorHpMax * frac);
            _tailRotorHp = Mathf.Min(_tailRotorHpMax, _tailRotorHp + _tailRotorHpMax * frac);
            if (_mainRotorHp > 0f || _tailRotorHp > 0f) _rotorFxExtinguished = false;   // repaired: let the FX speak again
        }
        public void Salvage()   // blowtorch teardown: the cold wreck breaks apart into scrap metal on the ground, then despawns
        {
            var parent = GetParent();
            if (parent != null)
                for (int i = 0; i < 3; i++)   // a wreck yields a few Metal Scrap (item 67)
                    WorldItem.Spawn(parent, new SDG.Unturned.Item(67), GlobalPosition + new Vector3((i - 1) * 0.6f, 0.5f, 0f));
            QueueFree();
        }

        // PERF (ETW 2026-09-02): 88 parked cars x (_Process + _PhysicsProcess) = ~25% of the main thread, nearly all of it
        // GodotSharp dispatch (StringName walk of Vehicle->VehicleBody3D->...->GodotObject per call). The physics tick is
        // now driven from ONE node (TickHub._PhysicsProcess -> PhysicsTickAll), same phase, one dispatch instead of 88.
        static readonly System.Collections.Generic.List<Vehicle> _live = new();
        public static System.Collections.Generic.IReadOnlyList<Vehicle> Live => _live;   // every vehicle in the tree (PlayerController look scan reads it instead of GetNodesInGroup)
        public override void _EnterTree() { _live.Add(this); TickHub.Ensure(this); }
        public static long PhysicsTickAllCalls;   // wiring probe for tests: proves the hub reaches this every physics tick
        static double _awakeLogT;
        public static void PhysicsTickAll(double delta)
        {
            PhysicsTickAllCalls++;
            if (System.Environment.GetEnvironmentVariable("UG_PERF") == "1" && (_awakeLogT += delta) >= 3.0)   // [vehawake]: how many bodies the physics + the C# bridge still see every step
            {
                _awakeLogT = 0; int asleep = 0, frozen = 0, awake = 0, proc = 0;
                foreach (var v in _live) { if (!GodotObject.IsInstanceValid(v)) continue; if (v.Freeze) frozen++; else if (v.Sleeping) asleep++; else awake++; if (v.IsProcessing()) proc++; }
                GD.Print($"[vehawake] live={_live.Count} awake={awake} asleep={asleep} frozen={frozen} processing={proc}");
            }
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var v = _live[i];
                if (!GodotObject.IsInstanceValid(v)) { _live.RemoveAt(i); continue; }
                if (!v.CanProcess()) continue;   // honour pause / ProcessMode exactly like the old per-node callback
                v.PhysicsTick(delta);
            }
        }
        bool _interpOff, _interpNear = true; float _interpNearT, _creepT;   // PERF: physics interpolation opted out while parked / far-and-bobbing (see PhysicsTick); creep-sleep timer
        public void PhysicsTick(double delta)   // was _PhysicsProcess; body unchanged below the interpolation gate
        {
            // PERF (ETW 2026-09-02, measured with a notification histogram): with physics_interpolation on,
            // VehicleBody3D::_update_process_mode enables INTERNAL_PROCESS on ITSELF just to interpolate the wheel
            // visuals, so all 88 PEI cars took NOTIFICATION_INTERNAL_PROCESS every rendered frame -- and every one of
            // those walked the whole C# dispatch chain (~10% of the main thread, parked). Interpolation only matters
            // while the body moves, so a car at rest with the engine off and nothing roped to it opts out; it opts
            // back in the physics step it starts moving, is started, towed, or net-held (before any visible motion).
            {
                bool wantInterp = EngineOn || NetHeld || Towing != null || TowedBy != null
                    || LinearVelocity.LengthSquared() > 0.0025f || AngularVelocity.LengthSquared() > 0.0025f;
                // A floating boat bobs forever, so the velocity gate never releases it; past ~150 m nobody can see a
                // 50 Hz bob step, so only keep interpolating while someone is close enough to look at it.
                if (wantInterp && !EngineOn && !NetHeld && _water != WaterMode.Car && (_interpNearT -= (float)delta) <= 0f)
                {
                    _interpNearT = 0.5f;
                    var np = PlayerRegistry.Nearest(GlobalPosition);
                    _interpNear = np != null && np.GlobalPosition.DistanceSquaredTo(GlobalPosition) < 150f * 150f;
                }
                if (!EngineOn && !NetHeld && _water != WaterMode.Car && !_interpNear) wantInterp = false;
                if (wantInterp == _interpOff)
                {
                    _interpOff = !wantInterp;
                    PhysicsInterpolationMode = wantInterp ? PhysicsInterpolationModeEnum.Inherit : PhysicsInterpolationModeEnum.Off;
                    if (wantInterp) ResetPhysicsInterpolation();   // the stored "previous" xform is from opt-out time; without this the first interpolated frame smears from there (tinyclaw; same lesson as Main.cs's teleport reset)
                }
            }
            // Turret cycle timers. Ticked BEFORE the perf early-returns below, so a turret does not jam because
            // its vehicle happened to be far enough away to skip a frame of simulation.
            if (_turretCd != null)
                for (int i = 0; i < _turretCd.Length; i++)
                    if (_turretCd[i] > 0f) _turretCd[i] = Mathf.Max(0f, _turretCd[i] - (float)delta);
            UpdateTireSparks();
            if (_lookFocused && _info != null)   // keep the info billboard at the cabin + live (before any perf early-return)
            {
                _info.GlobalPosition = GlobalPosition + Vector3.Up * InfoH;
                if (!_exploded)   // alive car: HP/fuel/battery bars. A WRECK's salvage prompt is set by PlayerController (it knows the blowtorch).
                {
                    _info.SetName(DisplayName, _outlineColor);
                    _info.SetBar(0, HealthMax > 0f ? Health / HealthMax : 0f, InfoBillboard.HealthColor);   // HP bar (red)
                    if (IsTrailer)   // a trailer has no engine -> no fuel/battery; show HP + a clear hitch state (connected / can connect / can't connect) instead
                    {
                        _info.SetBar(1, 0f, InfoBillboard.FuelColor, false); _info.SetBar(2, 0f, InfoBillboard.FuelColor, false);
                        // only surface the connect/disconnect prompt when a player is actually standing in the hitch region (strawberry)
                        var hitchPlayer = PlayerRegistry.Nearest(KingpinWorld);   // nearest-player query (the old Local static is gone)
                        bool inHitchRange = hitchPlayer != null
                            && hitchPlayer.GlobalPosition.DistanceTo(KingpinWorld) <= HitchReach;
                        string hint = !inHitchRange ? ""
                            : CoupledCab != null ? $"[{Keybinds.Get(GameAction.Interact).Label}] disconnect trailer"
                            : (CabBackedUnder() ? $"[{Keybinds.Get(GameAction.Interact).Label}] connect trailer" : "can't connect - back a cab under");   // explicit can/can't feedback
                        _info.SetPrompt(hint, _outlineColor);
                    }
                    else
                    {
                        _info.SetBar(1, FuelNorm, InfoBillboard.FuelColor);                 // fuel bar (yellow)
                        _info.SetBar(2, Battery / BatteryMax, InfoBillboard.FuelColor);     // battery bar (yellow)
                        // ROTOR BARS, helicopters only (strawberry 2026-08-16: "the rotors each get a health
                        // bar too"). Hidden outright on anything else rather than drawn empty -- the split
                        // health only means something on a machine that HAS rotors, and two dead rows on every
                        // car would read as a car with broken parts it does not own.
                        _info.SetBar(3, MainRotorNorm, RotorBarColor(MainRotorNorm), _heli);
                        _info.SetBar(4, TailRotorNorm, RotorBarColor(TailRotorNorm), _heli);
                        _info.SetPrompt(AccessHint, _outlineColor);
                    }
                }
            }
            if (_burnTime >= 0f)   // wreck fire lifecycle (master): 0-40s full burn, 40-60s dying down, out at 60s (+ light killed), sits 5 min, then despawns
            {
                _burnTime += (float)delta;
                if (_burnTime < 40f) { if (_fireLight != null) _fireLight.LightEnergy = 3f; }
                else if (_burnTime < 60f)   // die down over 20s: flames + smoke fade out, fire light dims to nothing
                {
                    float f = 1f - (_burnTime - 40f) / 20f;   // 1 -> 0
                    if (_fireLight != null) _fireLight.LightEnergy = 3f * f;
                    if (_fire != null) _fire.Transparency = 1f - f;
                    if (_smoke != null) _smoke.Transparency = 1f - f;
                    if (_smoke0 != null) _smoke0.Transparency = 1f - f;
                }
                else if (_burnTime < 360f)   // EXTINGUISHED at 60s: flames+smoke off, fire light killed; stays a cold wreck for 5 min
                {
                    if (_fire != null && _fire.Emitting) _fire.Emitting = false;
                    // Rotor fires die with the hull's ("extinguish rotor fires along with the body flames
                    // after corpse cools down") -- otherwise a cold wreck sits there with two burning hubs.
                    if (_mainRotorFire != null) _mainRotorFire.Emitting = false;
                    if (_tailRotorFire != null) _tailRotorFire.Emitting = false;
                    if (_mainRotorSmoke != null) _mainRotorSmoke.Emitting = false;
                    if (_tailRotorSmoke != null) _tailRotorSmoke.Emitting = false;
                    _rotorFxExtinguished = true;
                    if (_smoke != null && _smoke.Emitting) _smoke.Emitting = false;
                    if (_smoke0 != null && _smoke0.Emitting) _smoke0.Emitting = false;
                    if (_fireLight != null && _fireLight.Visible) { _fireLight.Visible = false; _fireLight.LightEnergy = 0f; }
                }
                else { QueueFree(); return; }   // 5 min after extinguishing -> despawn the wreck
            }
            if (_wNodes == null || _husk) return;   // a settled wreck is a dead husk -- no per-frame sim at all (master, perf)
            if (NetHeld)   // MP Part A: a driver's client owns this body's physics -- the frozen node only burns fuel + counts down its explosion (retail simulateBurnFuel / explode run server-side for driven cars too); settle/damage/gear sim all skip
            {
                if (EngineOn && Fuel > 0f && !InfiniteFuel) Fuel = Mathf.Max(0f, Fuel - FuelBurn * (float)delta);
                if (EngineOn && FuelMax > 0f && Fuel <= 0f) EngineOn = false;   // ran dry -> cut the engine (master)
                if (_deadTimer > 0f) { _deadTimer -= (float)delta; if (_deadTimer <= 0f) Explode(); }   // Explode unfreezes + flings; VehicleNetSync then aborts the hold + force-exits the driver
                return;
            }
            if (_plane) { StepPlane((float)delta); return; }   // fixed wing: prop thrust + airspeed lift replace the wheel/tow/settle sim (buoyancy still runs for a floatplane)
            if (_heli) { if (_slingHook) UpdateSling((float)delta); StepHeli((float)delta); return; }   // rotary wing: rotor thrust replaces the wheel/tow/settle sim entirely
            if (_tracked && !_exploded && !_parked && EngineOn && !Freeze && !Sleeping)   // TANK skid-steer turn authority: a REAL yaw torque (integrated -> owned momentum, survives slopes/walls + the MP transform-adopt path). FADED by forward speed -- a tight pivot at rest but a WIDE arc at speed, so a full-rate yaw while driving doesn't fight the wheels' grip and crawl (master). The per-track EngineForces carry the fwd/back drive.
            {
                float tfwd = LinearVelocity.Dot(-GlobalTransform.Basis.Z);
                float tTarget = _tankYawInput * TankMaxYawRate * (1f - TankYawSpeedFade * Mathf.Clamp(Mathf.Abs(tfwd) / _speedMax, 0f, 1f));
                // STABILITY CEILING, measured. This is a proportional velocity governor: with a = k*dt/I the
                // yaw error updates as (1-a), so it is stable only for 0 < a < 2 and diverges above it. That is
                // not theory here -- while hunting this bug a gain of a=2.5 threw the tank 41 km across the map
                // in 1.8 s and a=10 sent it 4.6e10 m. At the shipped gain a is 0.16, exactly where the 900 kg
                // tank sat, but the clamp means no future mass or hull edit can walk off that cliff silently.
                float yawK = _tankYawGain > 0f ? _tankYawGain : TankYawGain;
                yawK = Mathf.Min(yawK, 1.2f * Inertia.Y / Mathf.Max((float)delta, 0.0001f));
                ApplyTorque(new Vector3(0f, (tTarget - AngularVelocity.Y) * yawK, 0f));
            }
            if (CanTow && CoupledTrailer != null) UpdateCoupled(CoupledTrailer, (float)delta);   // coupled: rollover/clip disconnect + jackknife clamp
            else if (CanTow) UpdateTrailerApproach();     // ghost this cab vs a trailer it's backing under (exception + layer swap) so it phases the low deck+legs; solid vs the player throughout
            if (Towing != null) UpdateTow((float)delta);   // rope tower: spring-tension pull on both bodies + redraw the rope (SP)
            if ((Freeze || Sleeping) && _deadTimer < 0f && !_alarmed)   // a held (frozen wreck / sleeping car) off-screen -> skip the settle sim (but NOT an alarmed one -- its alarm keeps watching/looping); particles render on their own (master, perf)
            {
                var cam = GetViewport().GetCamera3D();
                if (cam != null && (cam.IsPositionBehind(GlobalPosition) || cam.GlobalPosition.DistanceSquaredTo(GlobalPosition) > 90000f)) return;
            }
            if (_spawnGrace > 0f) _spawnGrace -= (float)delta;   // spawn/world-init: stay DYNAMIC ~2.5s so a fresh car drops to fit terrain first
            _driveIdle += (float)delta;   // Drive() zeroes this; a car nobody is steering climbs away from zero
            if (_doorPivotA != null) UpdateDoor((float)delta);
            // Freeze a settled car (source isKinematic) -- but ONLY once it's GROUNDED + fully stopped. No fixed exit-timer (that kept the
            // car dynamic ~1s -> braking jitter) and full velocity incl. vertical (so a falling/braking car never freezes mid-air). (master)
            int groundedCount = 0; foreach (var w in _wNodes) if (w.IsInContact()) groundedCount++;
            bool mostlyGrounded = groundedCount * 2 > _wNodes.Length;   // MAJORITY of wheels down = sitting level (not teetering on 1 wheel, not airborne) -- master
            bool anyGrounded = groundedCount > 0;                        // at least touching -- a wreck must be grounded to freeze so it can't stick at its own fling-apex (master "stuck in the air")
            _velAvg = _velAvg.Lerp(LinearVelocity, 0.12f);    // LOW-PASS velocity + spin (master's "check above the jitter freq"): the jitter's rapid back-and-forth
            _angAvg = _angAvg.Lerp(AngularVelocity, 0.12f);   // cancels to ~0 in the running average, but a real roll / handbrake nose-dive REBOUND (sustained,
            // directional) survives the filter -- so we wait for the suspension to normalize yet never deadlock on the jitter. Reverted to the CLEAN
            // d9588d3 low-pass (no dwell, no raised thresholds) per master. The wreck branch keeps the no-wheel-contact check (killed suspension).
            bool towed = CoupledCab != null || TowedBy != null || Towing != null;   // a trailer PULLED by a cab, OR either end of a rope tow: never let the settle/park logic hold or damp it -- that would anchor the link (the 2mph stall)
            // WOKEN BY SOMETHING ELSE, and the whole fix turns on catching it. A car we put to sleep integrates
            // nothing, so its velocity stays exactly where we left it -- zero. Any velocity at all therefore
            // means the physics engine ACTIVATED it (a collision, a shove, ground giving way) and has already
            // stepped it. Without this the settle test below is STILL satisfied on that tick (the low-passed
            // velocity has barely moved) and would put it straight back to sleep, which is a wall wearing a
            // different hat. The grace window buys the impulse enough live ticks to register in _velAvg.
            //
            // Deliberately NOT `if (_asleep && !Sleeping)`: that reads the flag back out of the physics server
            // to detect the wake, and if the readback ever lags or the wheels re-activate the body on their own,
            // the false positive re-arms the grace every time we sleep it -- a car that can never settle,
            // jittering forever, which is the exact failure the static freeze was papering over. Velocity is
            // the physical signal and it cannot loop.
            if (_asleep && (LinearVelocity.LengthSquared() > 0.02f || AngularVelocity.LengthSquared() > 0.02f)) { _wakeGrace = 1.5f; _asleep = false; }
            if (_wakeGrace > 0f) _wakeGrace -= (float)delta;
            // UNATTENDED, not "spawned parked". The hold used to be gated on _parked, and the bumper handler
            // ("ram a frozen parked car -> wake it") calls Wake(), which CLEARS _parked -- so a car that had
            // ever been nudged fell through to the `_handbraking` branch, which is false when nobody is aboard,
            // and could never settle again for the rest of its life, running the full wheel sim forever.
            // That bug predates this change and the static freeze HID it: you cannot shove a piece of terrain,
            // so the case never arose. Making parked cars real is what made it arise, and the new probe caught
            // it on the first run -- the rammed jeep was still awake 10 s later with the rammer 130 m away.
            // Time since anyone last touched the controls is the honest test of "nobody is driving this".
            bool unattended = _parked || _driveIdle > 1.0f;
            bool wantHold = !towed && _wakeGrace <= 0f && _angAvg.LengthSquared() < 0.03f && (_exploded ? (anyGrounded && _velAvg.LengthSquared() < 1.0f)
                                                                          : mostlyGrounded && (unattended ? (_spawnGrace <= 0f && _velAvg.LengthSquared() < 1.0f)
                                                                                                          : (_handbraking && _velAvg.LengthSquared() < 0.06f)));
            if (wantHold && !Freeze && !Sleeping)
            {
                LinearVelocity = Vector3.Zero; AngularVelocity = Vector3.Zero;
                // A WRECK still freezes STATIC (kinematic vanished the car): its wheels get deleted below, so
                // it is scenery, and PEI leaves a lot of it lying about. A LIVE car SLEEPS instead. Sleep costs
                // the same as a freeze while nothing touches it -- no solver, no wheel raycasts -- but the body
                // is still DYNAMIC and still in the world, so it can be rammed, shunted and rolled downhill.
                // Freeze-Static made a parked car a piece of the terrain, which is what strawberry meant by
                // "no physics unless driving SUCKS": you could drive into a jeep at 45 km/h and bounce off it.
                if (_exploded) { FreezeMode = RigidBody3D.FreezeModeEnum.Static; Freeze = true; _husk = true; foreach (var w in _wNodes) w.QueueFree(); }
                else { Sleeping = true; _asleep = true; }
            }
            else if (!wantHold && (Freeze || Sleeping)) { Freeze = false; Sleeping = false; _asleep = false; }
            // NO LINEAR DAMPING. `LinearDamp = 6` while slowing was not braking, it was a velocity DELETE: an
            // exponential decay on the whole body that owes nothing to the wheels, the tyres or the road, worth
            // ~2.4 g on the jeep and applied whether or not anything was gripping. It is the other half of
            // "no physics unless driving". Brake torque already stops the car -- the footbrake alone does it in
            // 3.2 m -- so all the damping ever added was hiding the last of the suspension wobble, and a modest
            // ANGULAR term does that honestly without touching where the car ends up.
            bool settling = !towed && (unattended || _handbraking) && !Freeze && !Sleeping && LinearVelocity.LengthSquared() < 2.0f;
            LinearDamp = 0f; AngularDamp = settling ? 3f : 0f;
            // AERODYNAMIC DRAG + ROLLING RESISTANCE. This is what sets top speed now, and it is the honest
            // version of the LinearDamp that was deleted above: drag is a real force that grows with v^2 and
            // opposes motion, where the damping was an exponential decay applied to the body regardless of
            // whether anything was touching the road. _dragK is SOLVED per hull so equilibrium lands exactly
            // at _speedMax (see SetupDrivetrain) rather than being a number somebody guessed.
            // Horizontal only: gravity and the suspension own the vertical axis.
            if (!Freeze && !Sleeping && !_husk && _dragK > 0f)
            {
                var hvel = new Vector3(LinearVelocity.X, 0f, LinearVelocity.Z);
                float hsp = hvel.Length();
                if (hsp > 0.15f) ApplyCentralForce(-hvel / hsp * (_dragK * hsp * hsp + _rollK));
            }
            if (unattended && !Freeze && !Sleeping && !towed) Brake = _brakeForce * HandbrakeScale;   // parking brake: hold a rolling unattended car down until it settles (never brake a towed trailer). Also on `unattended` rather than `_parked`, so a car that has been rammed keeps its brake instead of free-rolling away forever
            // CREEP-SLEEP (census 2026-09-03: both firetrucks, a sedan and a hatchback crept at 0.4-1.1 m/s "parked"
            // forever -- the parking brake can't hold a heavy hull on a slope, so Jolt never sleeps them and they pay
            // the full wheel sim + interpolation every frame). An unattended car that has been slow (< 1.5 m/s) for a
            // second with nobody within 40 m is put to sleep where it stands; a touch (collision) or a driver wakes it
            // exactly like the 84 that Jolt parked on its own.
            if (unattended && !towed && !Freeze && !Sleeping && LinearVelocity.LengthSquared() < 2.25f)
            {
                if ((_creepT += (float)delta) >= 1f)
                {
                    _creepT = 0f;
                    var np = PlayerRegistry.Nearest(GlobalPosition);
                    if (np == null || np.GlobalPosition.DistanceSquaredTo(GlobalPosition) > 40f * 40f) { LinearVelocity = Vector3.Zero; AngularVelocity = Vector3.Zero; Sleeping = true; }
                }
            }
            else _creepT = 0f;
            // NO manual wheel spin: Godot's VehicleWheel3D already bakes the ROLL (+ suspension + steering) into its own
            // node transform every physics tick, and the wheel MESH is a child that inherits it. An old manual
            // _wMeshes[i].Rotation added an equal+opposite roll that CANCELLED the node's auto-roll in world space -> the
            // wheels looked frozen (the local rotation changed, but the world basis was pinned). Verified: node world-Y
            // rolls full circle, and once the manual spin is gone the mesh world-Y rolls with it. (fable diagnosis)
            // engine RPM + gears (source InteractableVehicle): rpm = |avg wheel rpm| * gear ratio, idle-floored, then auto-shift
            // Engine rpm must track ROAD SPEED, not a free-spinning airborne wheel. A driven wheel off the ground
            // spins up under engine force; a plain mean over ALL wheels lets that lift the engine to the rev limiter,
            // which cuts force and stalls the hull BELOW speedMax -- the airborne-wheels-lying-to-the-tach ceiling
            // behind the semi topping at 0.827 (tinyclaw 2026-08-28). Average GROUNDED DRIVEN wheels only, so one
            // spinning wheel can't carry the engine to redline. Fallbacks so it never /0: all driven (a full launch),
            // then all wheels (no traction wheels at all -- shouldn't happen).
            float sum = 0f; int rpmN = 0;
            foreach (var w in _wNodes) if (w.UseAsTraction && w.IsInContact()) { sum += Mathf.Abs(w.GetRpm()); rpmN++; }
            if (rpmN == 0) foreach (var w in _wNodes) if (w.UseAsTraction) { sum += Mathf.Abs(w.GetRpm()); rpmN++; }
            if (rpmN == 0) { foreach (var w in _wNodes) sum += Mathf.Abs(w.GetRpm()); rpmN = _wNodes.Length; }
            float avgWheelRpm = rpmN > 0 ? sum / rpmN : 0f;
            float ratio = CurrentGearRatio;
            // CLUTCH / TORQUE CONVERTER. Without one, engine rpm is a pure function of road speed -- which is
            // exactly what strawberry meant by "engine rpm = speed rn": a stopped car sits at idle making idle
            // torque and cannot launch, and the rev counter is a speedometer with a different dial on it.
            // Below the stall speed the engine is partly decoupled and revs toward StallRpm on throttle, which
            // is what actually gets a standing car moving; as the wheels catch up it locks to the road.
            float drivenRpm = avgWheelRpm * ratio;
            float engage = Mathf.Clamp(drivenRpm / StallRpm, 0f, 1f);
            float launch = IdleRpm + Mathf.Abs(_inThrottle) * (StallRpm - IdleRpm);
            float target = Mathf.Clamp(Mathf.Lerp(launch, drivenRpm, engage), IdleRpm, MaxRpm);
            _engineRpm = Mathf.Lerp(_engineRpm, target, Mathf.Min(1f, 8f * (float)delta));
            // SHIFT ON RPM, which is what a gearbox does. The old selector picked the gear from a SPEED BAND
            // -- gear = f(speed) -- so the ratio could never influence anything and the rev counter sawtoothed
            // decoratively over the top. It was written that way because the RPM model it replaced genuinely
            // could not shift: at the spec ratios the engine reached only ~2700 rpm at top speed against a 6000
            // redline, so it never hit a shift point in ANY gear. The gearing was the bug; SetupDrivetrain now
            // derives ratios that actually reach the redline, so shifting on rpm works.
            if (_gears != null && _gears.Length > 0)
            {
                float fwd = Mathf.Abs(LinearVelocity.Dot(-GlobalTransform.Basis.Z));
                if (_shiftCd > 0f) _shiftCd -= (float)delta;
                if (_clutchT > 0f) _clutchT = Mathf.Max(0f, _clutchT - (float)delta);   // the clutch re-engages on its own clock, shorter than the shift interval
                if (fwd < 1.0f) _gear = 1;   // rolled to a stop -> back into first, ready to launch
                else if (_shiftCd <= 0f && !_exploded && !_husk)
                {
                    if (_engineRpm >= RedlineFrac * MaxRpm && _gear < _gears.Length)
                    {
                        _gear++; _shiftCd = ShiftTime; _clutchT = ShiftClutchTime;
                        // The jolt is a VERTICAL hitch + pitch nod you FEEL but that does NOT touch the fore-aft
                        // speed: a fore-aft impulse used to dip the speed back under the old band's shift point
                        // and re-downshift instantly, sticking the box in a shift loop (master caught it). That
                        // trap is gone with rpm hysteresis, but the vertical jolt is the better effect anyway.
                        ApplyCentralImpulse(Vector3.Up * Mass * 0.22f);
                        ApplyTorqueImpulse(GlobalTransform.Basis.X * Mass * 0.5f);
                    }
                    else if (_gear > 1 && _engineRpm <= DownshiftRpm(_gear)) { _gear--; _shiftCd = ShiftTime; _clutchT = ShiftClutchTime; }   // downshifts declutch too: the box does not care which way it went
                }
            }
            if (_engineAudio != null)   // EngineRPMSimple: pitch + volume by RPM while running; silent when off (exited)
            {
                if (EngineOn)
                {
                    float n = EngineRpmNorm;
                    _engineAudio.PitchScale = Mathf.Lerp(_idlePitch, _maxPitch, n);
                    _engineAudio.VolumeDb = Mathf.LinearToDb(Mathf.Lerp(_idleVol, _maxVol, n) * EngineVolumeBoost);
                    if (!_engineAudio.Playing) _engineAudio.Play();   // resume the loop STOPPED below
                    _engineWind = -1f;
                }
                // STOP it, don't just silence it. Autoplay=true starts this loop the moment the vehicle enters
                // the tree, and -80 dB is still a playing stream: the mixer keeps decoding the ogg every frame
                // for something nobody can hear. PEI places ~89 vehicles, so the map booted with ~89 permanently
                // inaudible loops running. Volume alone was never going to stop that; Playing is the switch.
                // ...but not INSTANTLY: the engine WINDS DOWN first (master 2026-09-04 "the sound should sorta wind
                // down as the engine turns off, not just a fadeout") -- the pitch sags toward a stalled idle, fast at
                // first then trailing, while the volume falls; the loop stops when it is gone. A wreck's husk path
                // has already slammed the volume to -80 dB, so an explosion still cuts dead.
                else if (_engineAudio.Playing)
                {
                    if (_engineWind < 0f) { _engineWind = 0f; _windPitch0 = _engineAudio.PitchScale; _windVol0 = Mathf.DbToLinear(_engineAudio.VolumeDb); }
                    _engineWind += (float)GetPhysicsProcessDeltaTime() / EngineWindDownSec;
                    float k = Mathf.Clamp(_engineWind, 0f, 1f), ease = 1f - (1f - k) * (1f - k);
                    _engineAudio.PitchScale = Mathf.Lerp(_windPitch0, _idlePitch * 0.45f, ease);
                    _engineAudio.VolumeDb = Mathf.LinearToDb(Mathf.Max(0.0001f, _windVol0 * (1f - ease)));
                    if (k >= 1f) { _engineAudio.VolumeDb = -80f; _engineAudio.Stop(); _engineWind = -1f; }
                }
            }
            // Phase 3 hearing: a running, MOVING car makes engine/tire noise a listener would hear -- source DRIVING
            // stealth radius DETECT_FORWARD(48) x forward-speed% (parked/idling ~silent since speed~0). Throttled like
            // footsteps. (SoundBus currently has no listener wired up -- it lost its zombie audience with the zombie
            // system -- but the emit stays so a future one needs no changes here.)
            _engineNoiseT -= (float)delta;
            if (EngineOn && _engineNoiseT <= 0f)
            {
                _engineNoiseT = 0.4f;
                float loud = 48f * ForwardSpeedPct();
                if (loud > 2f) SoundBus.Emit(GetTree(), GlobalPosition, loud);
            }
            if (OnFire) EngineOn = false;   // caught fire -> engine force-killed EVERY frame: can't drive, can't restart, unfixable (master)
            if (EngineOn && Fuel > 0f && !InfiniteFuel)   // source simulateBurnFuel: burn fuelBurnRate/sec while the engine runs (dev infFuel skips the drain)
                Fuel = Mathf.Max(0f, Fuel - FuelBurn * (float)delta);
            if (EngineOn && FuelMax > 0f && Fuel <= 0f) EngineOn = false;   // ran DRY (or entered an empty car) -> cut the engine; Drive gates on EngineOn so it coasts to a stop. Refuel (gas can / pump) + re-enter to restart (master)
            // BATTERY (strawberry_cow 2026-08-24). Running engine = alternator: the drain stops and the battery
            // recharges slowly. Engine off = the electrics eat it, and at flat everything electrical dies.
            if (EngineOn)
            {
                // Charge even with the lights on. A real alternator outruns the lamps, and the alternative --
                // netting the two -- means a car idling with its headlights on never recovers, which is the
                // opposite of what a running engine should do for you.
                Battery = Mathf.Min(BatteryMax, Battery + BatteryChargeRate * (float)delta);
            }
            else
            {
                if (_headlightsOn) Battery = Mathf.Max(0f, Battery - BatteryBurnRate * (float)delta);   // source: headlights burn the battery (EBatteryMode.Burn)
                if (_sirenOn) Battery = Mathf.Max(0f, Battery - SirenBurnRate * (float)delta);
            }
            if (Battery <= 0f)
            {
                // Everything electrical goes with it, the same way the headlights already did. SetHeadlights is
                // guarded because it re-touches materials and the mote emitter, and this runs every tick once a
                // battery is flat -- which for a parked wreck is the rest of the session.
                if (_headlightsOn) SetHeadlights(false);
                _sirenOn = false;
            }
            if (_alarmed && !_exploded)   // "alarmed" car (master): proximity (player) or damage sets off a ~30s honk+lights blip loop. NOT on a wreck -- Explode clears the state, and this guard means even a re-arm can't relight a corpse
            {
                if (_alarmTimer <= 0f)   // idle -> watch for a proximity trigger (throttled)
                {
                    _alarmCheckT -= (float)delta;
                    if (_alarmCheckT <= 0f)
                    {
                        _alarmCheckT = 0.3f;
                        // WHO COUNTS AS "somebody walked past". The local camera is the right answer in
                        // singleplayer and on a listen-server host, and the WRONG one on a dedicated server,
                        // which has no camera at all -- GetCamera3D returns null there, so an alarm could
                        // never fire on the machine that owns the car. AlarmProximityTest lets whoever knows
                        // where the players actually are answer it; nothing sets it in SP, which keeps the
                        // camera behaviour byte-identical.
                        bool near;
                        if (AlarmProximityTest != null) near = AlarmProximityTest(GlobalPosition);
                        else
                        {
                            var acam = GetViewport().GetCamera3D();
                            near = acam != null && acam.GlobalPosition.DistanceSquaredTo(GlobalPosition) < AlarmRadiusSq;   // player within ~7m
                        }
                        // (enemy proximity check removed with the zombie system)
                        if (near) TriggerAlarm();
                    }
                }
                else   // ALARMING: blip 0.5s on / 0.5s off for ~30s
                {
                    _alarmTimer -= (float)delta; _alarmBlip += (float)delta;
                    bool on = (_alarmBlip % 1.0f) < 0.5f;
                    if (on && !_alarmLit) { DoHorn(); SetHeadlights(true); SetTaillights(true); }   // rising edge -> honk + head+tail lights ON in sync (master), like a real horn
                    else if (!on && _alarmLit) { SetHeadlights(false); SetTaillights(false); }     // falling edge -> all lights off, NO honk
                    _alarmLit = on;
                    if (_alarmTimer <= 0f) { SetHeadlights(false); SetTaillights(false); _alarmLit = false; _alarmed = false; }   // alarm done -> killed for good, never alarms again (master)
                }
            }
            if (_exhaust != null)   // tailpipe: runs with the engine, thickens with revs, a fat puff for the first moment after it catches
            {
                bool run = EngineOn && !_exploded;
                if (_exhaust.Emitting != run) _exhaust.Emitting = run;
                if (run)
                {
                    if (_exhaustPuff > 0f) _exhaustPuff -= (float)delta;
                    float revs = Mathf.Clamp(EngineRpm / 6000f, 0f, 1f);
                    int want = _exhaustPuff > 0f ? 24 : (revs > 0.6f ? 16 : revs > 0.25f ? 10 : 6);   // CpuParticles3D has no AmountRatio: step the pool size instead (idle wisp -> revving stream -> cold-start puff)
                    if (_exhaust.Amount != want) _exhaust.Amount = want;
                }
            }
            if (_sirenMat0 != null)   // emergency lightbar: alternate the red + blue lenses while the siren's on (master: ctrl toggles). Dead on a wreck.
            {
                if (_sirenOn && !_exploded)
                {
                    bool lBroken = IsLightbarLensBroken(0), rBroken = IsLightbarLensBroken(1), cBroken = IsLightbarCentreBroken;
                    if (_sirenAudio != null)
                    {
                        if (lBroken && rBroken && cBroken) { if (_sirenAudio.Playing) _sirenAudio.Stop(); }   // the whole bar is gone: silence
                        else
                        {
                            if (!_sirenAudio.Playing) _sirenAudio.Play();
                            float basePitch = LightbarSirenPitch[LightbarPattern];
                            if (cBroken)   // a shot centre = the amp: warbles, drops out, muffled (strawberry: "mess up / mute the siren sound when damaged/broken")
                            {
                                float wob = Mathf.Sin(_sirenFlash * 23f) * 0.18f + Mathf.Sin(_sirenFlash * 5.3f) * 0.12f;
                                bool dropout = (_sirenFlash % 1.7f) > 1.35f;
                                _sirenAudio.PitchScale = Mathf.Max(0.3f, basePitch + wob);
                                _sirenAudio.VolumeDb = dropout ? -40f : -12f;
                            }
                            else { _sirenAudio.PitchScale = basePitch; _sirenAudio.VolumeDb = 2f; }
                        }
                    }
                    _sirenFlash += (float)delta;
                    // PATTERNS (ctrl-hold radial): 0 wail = alternate 0.33s each (retail sirenState/lastWeeoo); 1 double strobe = both lenses
                    // pop twice then rest; 2 wig-wag = fast 0.12s alternation.
                    bool lLit, rLit;
                    switch (LightbarPattern)
                    {
                        case 1:   // double strobe (strawberry 2026-09-04): LEFT pops twice, then RIGHT pops twice, and so on
                        {
                            float t = _sirenFlash % 0.8f; bool leftHalf = t < 0.4f; float u = leftHalf ? t : t - 0.4f;
                            bool pop = u < 0.07f || (u >= 0.15f && u < 0.22f);
                            lLit = leftHalf && pop; rLit = !leftHalf && pop; break;
                        }
                        case 2: { bool a = (_sirenFlash % 0.24f) < 0.12f; lLit = a; rLit = !a; break; }
                        default: { bool a = (_sirenFlash % 0.66f) < 0.33f; lLit = a; rLit = !a; break; }
                    }
                    if (lBroken) lLit = false; if (rBroken) rLit = false;
                    _sirenMat0.EmissionEnabled = !lBroken; _sirenMat0.Emission = new Color(1f, 0.05f, 0.05f); _sirenMat0.EmissionEnergyMultiplier = lLit ? 4f : 0f;
                    _sirenMat1.EmissionEnabled = !rBroken; _sirenMat1.Emission = new Color(0.1f, 0.15f, 1f); _sirenMat1.EmissionEnergyMultiplier = rLit ? 4f : 0f;
                    if (_sirenLight0 != null) _sirenLight0.LightEnergy = lLit ? 5f : 0f;
                    if (_sirenLight1 != null) _sirenLight1.LightEnergy = rLit ? 5f : 0f;
                }
                else { _sirenMat0.EmissionEnabled = false; _sirenMat1.EmissionEnabled = false; if (_sirenLight0 != null) _sirenLight0.LightEnergy = 0f; if (_sirenLight1 != null) _sirenLight1.LightEnergy = 0f; if (_sirenAudio != null && _sirenAudio.Playing) _sirenAudio.Stop(); }
            }
            if (_alarmTimer <= 0f && CoupledCab == null)   // the alarm owns the taillights while blaring (master); a COUPLED trailer's lights are driven by the cab (DriveTrailerLights) so it skips its own logic
            {
                bool tailWant = ((EngineOn && Battery > 0f) || _headlightsOn) && CoupledTrailer == null;   // source synchronizeTaillights = isDriven && canTurnOnLights; master ADDS headlights->tail. While TOWING the cab's own tail is off -> the trailer carries the lights (pass-through)
                if (tailWant != _taillightsOn) SetTaillights(tailWant);
            }
            if (_hornCd > 0f) _hornCd -= (float)delta;
            // collision/ram damage (source isVulnerableToBumper): a sudden horizontal deceleration = a crash. Horizontal only, so the spawn drop doesn't count.
            float curSpeed = new Vector2(LinearVelocity.X, LinearVelocity.Z).Length();
            float decel = _prevSpeed - curSpeed;
            // The DECAYING PEAK, not last tick's speed -- carried over from the aircraft path, which learned it
            // the hard way (see StepHeli): with ContinuousCd the solver bleeds a fast impact off across several
            // ticks, so by the tick the deceleration is big enough to trip the gate, _prevSpeed has already
            // fallen well below the real approach speed. Cars were exempt from that only because they had no
            // ContinuousCd. Turning it on above without this would have quietly HALVED crash damage -- a fix
            // for one bug paying for itself by breaking a neighbour.
            _recentTopSpeed = Mathf.Max(curSpeed, _recentTopSpeed - (float)delta * 6f);
            if (!_parked && !_exploded && _recentTopSpeed > 5f && decel > 200f * (float)delta)
                TakeDamage(_recentTopSpeed * 20f);   // >200 m/s^2 = a crash (braking is ~8); full-speed hit ~250 dmg
            _prevSpeed = curSpeed;
            if (_smoke != null) _smoke.Emitting = _burnTime < 60f && (_exploded || Health < SmokeHealth);   // source updateFires: smoke_1 at health < 200 (or exploded); OFF once the wreck fire is out at 60s (master)
            if (_smoke0 != null) _smoke0.Emitting = _exploded || Health < HeavySmokeHealth;   // source updateFires: smoke_0 (heavy) at health < 100 (or exploded)
            if (_wheelDust != null)   // per-wheel dust at each wheel's ground contact (source structure; vanilla ships none -> our Surf-driven enhancement)
            {
                float spd = new Vector2(LinearVelocity.X, LinearVelocity.Z).Length();
                bool moving = spd > 3f && !_exploded;
                // aim UP at low speed, tilt ~45deg toward backward (+Z local) approaching top speed (src blendWeight = speed% * 0.5)
                float blend = Mathf.Clamp(spd / Mathf.Max(1f, _speedMax), 0f, 1f) * 0.5f;
                var dir = new Vector3(0f, 1f, 0f).Lerp(new Vector3(0f, 0f, 1f), blend).Normalized();
                bool recheck = moving && (_dustCheckT -= (float)delta) <= 0f;   // throttle the per-wheel surface raycast
                if (recheck) _dustCheckT = 0.12f;
                for (int i = 0; i < _wNodes.Length; i++)
                {
                    var w = _wNodes[i]; var d = _wheelDust[i];
                    if (w == null || d == null) continue;
                    bool contact = moving && w.IsInContact();
                    if (contact)
                    {
                        d.Position = ToLocal(w.GetContactPoint());   // spawn at the ground hit like the source
                        if (recheck) _wheelSurf[i] = WheelSurf(w);
                    }
                    var sf = _wheelSurf[i];
                    bool soft = sf == PlayerController.Surf.Grass || sf == PlayerController.Surf.Dirt || sf == PlayerController.Surf.Sand;   // only loose ground kicks up
                    d.Direction = dir;
                    if (soft) d.Color = PlayerController.SurfDust(sf);
                    d.Emitting = contact && soft;
                }
                if (System.Environment.GetEnvironmentVariable("UG_DUSTDEBUG") == "1" && moving && (_dustLogT -= (float)delta) <= 0f)
                {
                    _dustLogT = 1f;
                    bool anyEmit = false; foreach (var d in _wheelDust) if (d != null && d.Emitting) { anyEmit = true; break; }
                    GD.Print($"[wheeldust] spd={spd:0.0} surf0={_wheelSurf[0]} anyEmit={anyEmit}");
                }
            }
            if (_exploded)   // master: explosion smoke/fire emits from the ENGINE bay (like the hurt smoke) but rises STRAIGHT UP -- world-space so the plume doesn't tilt with the tumbling wreck
            {
                var enginePos = ToGlobal(_firePos);   // engine-bay world position (rides the wreck); plume forced world-up via Rotation=0
                if (_smoke  != null) { _smoke.TopLevel  = true; _smoke.GlobalPosition  = enginePos; _smoke.Rotation  = Vector3.Zero; }
                if (_smoke0 != null) { _smoke0.TopLevel = true; _smoke0.GlobalPosition = enginePos; _smoke0.Rotation = Vector3.Zero; }
                if (_fire   != null) { _fire.TopLevel   = true; _fire.GlobalPosition   = enginePos; _fire.Rotation   = Vector3.Zero; }
            }
            if (_deadTimer > 0f) { _deadTimer -= (float)delta; if (_deadTimer <= 0f) Explode(); }   // source EXPLODE: 4s after health 0

            // steering smoothing (source: AnimatedSteeringAngle = MoveTowards(target, SteeringAngleTurnSpeed*dt)) -- no instant snap
            //
            // ...AND THE RATE FALLS WITH ROAD SPEED. strawberry, 2026-08-24: "steering is way too sensitive
            // now", after the lateral-acceleration cap came out. The cap is NOT going back in -- they rejected
            // it twice ("real simulated weight/intertia on steering, not a hard clamp"), and removing it is the
            // measured reason the jeep's full-lock circle went 27.9 m -> 11.7 m, which was the other complaint.
            //
            // So the lever is the RATE, not the limit: quick at rest (parking), heavy at speed. That is inertia
            // rather than a clamp -- full lock stays reachable at any speed, it just costs time to wind on,
            // which is what a loaded steering system actually feels like. Max angle, turning circle and the
            // existing ANGLE fade are all untouched.
            //
            // Keyed to _specSpeedMax, the PRE-buff top speed, for the same reason the angle fade is: how heavy
            // the wheel feels at 12 m/s must not depend on how fast the car can eventually go -- and
            // TopSpeedBuff has now moved twice.
            float steerRef2 = _specSpeedMax > 0f ? _specSpeedMax : _speedMax;
            float steerLoad = steerRef2 > 0f ? Mathf.Clamp(LinearVelocity.Length() / steerRef2, 0f, 1f) : 0f;
            float steerRate = _steerTurnSpeed * Mathf.Lerp(1f, SteerRateAtSpeed, steerLoad);
            _steerAngle = Mathf.MoveToward(_steerAngle, _steerTarget, steerRate * (float)delta);
            Steering = Mathf.DegToRad(_steerAngle);
            if (_steerPivot != null) _steerPivot.Basis = new Basis(_steerAxis, Mathf.DegToRad(_steerAngle));   // steering wheel model turns 1:1 with the steer angle (source line 4020, AnimatedSteeringAngle)
            SyncHitMeshVelocity();           // MESH HITBOX: hand the static child our motion so a rider is carried
            CarryDeckRiders((float)delta);   // MOVING DECK: carry anything standing on us. Outside ApplyWaterPhysics
                                             // deliberately -- that returns early when the hull is not afloat, and a
                                             // grounded or beached vessel still has a deck.
            if (_water != WaterMode.Car) ApplyWaterPhysics((float)delta);   // BOAT/AMPHIBIOUS: buoyancy float + water propulsion (overrides wheel drive while afloat)
            else ApplySwampedPhysics((float)delta);                          // CAR: drowned engine + a few seconds of trapped-air float, then it goes down
        }

        /// <summary>One convex collision hull built from the body mesh's OWN vertices inside an AABB slice of it.
        ///
        /// The point cloud is REDUCED before it becomes a shape, and the reduction is exact rather than a
        /// tolerance: any vertex of a 3D convex hull is also a vertex of the 2D hull of its own horizontal plane,
        /// so taking the 2D hull per distinct y-plane and unioning the results yields precisely the same 3D hull
        /// from far fewer points. The ship's lower hull drops from 360 vertices to a few dozen that way, which
        /// matters because a convex shape's support function is linear in its point count and this one is
        /// queried every tick.</summary>
        static ConvexPolygonShape3D ConvexBand(Mesh mesh, Vector3 lo, Vector3 hi)
        {
            var planes = new System.Collections.Generic.SortedDictionary<int, System.Collections.Generic.List<Vector2>>();
            foreach (var v in mesh.GetFaces())
            {
                if (v.X < lo.X || v.X > hi.X || v.Y < lo.Y || v.Y > hi.Y || v.Z < lo.Z || v.Z > hi.Z) continue;
                int key = Mathf.RoundToInt(v.Y * 1000f);
                if (!planes.TryGetValue(key, out var list)) planes[key] = list = new System.Collections.Generic.List<Vector2>();
                list.Add(new Vector2(v.X, v.Z));
            }
            var pts = new System.Collections.Generic.List<Vector3>();
            foreach (var kv in planes)
            {
                float y = kv.Key / 1000f;
                foreach (var p in Hull2D(kv.Value)) pts.Add(new Vector3(p.X, y, p.Y));
            }
            // Fewer than two planes is a flat sheet, which is not a volume -- refuse it rather than hand the
            // physics server a degenerate shape.
            return planes.Count >= 2 && pts.Count >= 4 ? new ConvexPolygonShape3D { Points = pts.ToArray() } : null;
        }

        /// <summary>Andrew's monotone chain, counter-clockwise, colinear points dropped.</summary>
        static System.Collections.Generic.List<Vector2> Hull2D(System.Collections.Generic.List<Vector2> src)
        {
            var p = new System.Collections.Generic.List<Vector2>(new System.Collections.Generic.HashSet<Vector2>(src));
            p.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            if (p.Count < 3) return p;
            static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var h = new System.Collections.Generic.List<Vector2>();
            for (int i = 0; i < p.Count; i++)
            {
                while (h.Count >= 2 && Cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 0f) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }
            int lower = h.Count + 1;
            for (int i = p.Count - 2; i >= 0; i--)
            {
                while (h.Count >= lower && Cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 0f) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }
            h.RemoveAt(h.Count - 1);   // the closing point repeats the first
            return h;
        }

        /// <summary>Test seam: build the OLD single-box hull even for a spec that has a convex decomposition, so a
        /// fidelity test can measure both in one run and its pass means something.</summary>
        public static bool ForceBoxHull;

        // THE MODEL AS THE HITBOX (strawberry 2026-09-02: "use the model as the collision mesh. not boxes
        // of best fit"). Measured on a 6 cm voxel grid, vehicle-local, hulls against the body mesh:
        //   sedan   car 13.4 m^3   convex hitbox 24.4 m^3   solid-where-there-is-no-car 11.3 m^3 (+84%)
        //   van     car 10.9 m^3   convex hitbox 23.0 m^3                              12.3 m^3 (+113%)
        // The outline matches -- that is why a silhouette pass reported only an antialiasing rim -- but the
        // VOLUME is nearly double, because a convex hull cannot hold a concavity: the notch over the bonnet,
        // the wheel arches, the step down to the boot are all filled in. Raising the decomposition does not
        // help; it saturates at 10 hulls on a 366-triangle body and 48/0.01 measures very slightly WORSE
        // than 12/0.08.
        //
        // Jolt will not collide a MOVING mesh shape with the terrain, so the driving body keeps its convex
        // hulls and the real mesh rides along on a StaticBody3D child -- the same shape the ship's deckhouse
        // already uses. For the player and bullets to actually meet the MESH and not the hull, the hulls have
        // to leave the layers those scan: the chassis moves to a private bit that only other vehicles mask,
        // and bit0|bit5 go to the mesh.
        //
        // ON BY DEFAULT since 2026-09-03 (strawberry: "do it on real collision. we will fix as we go").
        // UG_MESHHITBOX=0 puts every car back on its convex hulls, so the A/B is one flag on one binary.
        //
        // What it cost to turn on, all of it measured on a sedan in vehicle.mesh_hitbox rather than reasoned
        // about, and none of it visible to a test that only asked whether a ray hit something:
        //   * SHOOTING A CAR DID NOTHING AT ALL. 6000 -> 6000 hp on a round through the door, no glass, no
        //     lamp, nothing logged. A ray now returns the HitMesh CHILD, so every `collider is Vehicle` in the
        //     game stopped matching. Fixed by resolving through Vehicle.Owning at the four sites that took a
        //     collider and cast it -- bullets, look focus, the deck ray, the ladder carry.
        //   * THE WINDSCREEN WAS A HOLE. The hull was what stopped a round at the glass; a ray along the
        //     windscreen's own normal passed clean through the car and hit NOTHING. The panes now carry their
        //     own colliders (mesh-hitbox mode only), and a pane that BREAKS gives its collider up.
        //   * A RIDER WAS LEFT BEHIND. A player on the roof tracked the car 1.00 for 1.00 on the hulls and
        //     0.21 with the hitbox on -- a StaticBody3D reports no velocity to stand on. SyncHitMeshVelocity
        //     publishes it; back to 0.97.
        // The gain it was turned on for: a down-ray over the cabin stops at the model's real roof, 2.160,
        // instead of 7 cm proud of it at 2.237.
        public static bool MeshHitbox => System.Environment.GetEnvironmentVariable("UG_MESHHITBOX") != "0";
        const uint ChassisBit = 1u << 13;   // free; bits 0-12 are all spoken for

        /// <summary>The layer the model-as-hitbox sits on, alongside bit 5.
        ///
        /// NOT BIT 0, and this is the whole point of having a bit of its own. The hitbox first borrowed bit 0
        /// because that is the layer the player's capsule walks on -- but EVERY VEHICLE MASKS BIT 0 too (it is
        /// how a car finds the terrain), so each vehicle's own wheels, tracks and hulls collided with its own
        /// hitbox. The vehicle then sat permanently inside a collider it could not resolve, exactly the failure
        /// the ship's own deckhouse exception was written for. Measured, with the hitbox on bit 0:
        ///   tank    flipped upside down (up.y -1.00) and slid 20 m instead of pivoting on the spot
        ///   sedan   top speed 8.42 m/s against a 14.7 floor, and no coast-down at all off the throttle
        ///   jeep    a released hold skidded to rest in 0.39 m instead of the verified 0.74 m
        /// All three passed with the flag off, on the same binary, in the same isolation.
        ///
        /// Bit 15 is otherwise unused, so nothing else in the game masks it: a hitbox can never again be
        /// something its own vehicle -- or any other vehicle -- runs into. The player's capsule masks it
        /// explicitly (PlayerController), which is a more honest statement of "the player collides with
        /// vehicles" than borrowing the world layer was. Bit 5 stays for the things that SCAN for vehicles --
        /// bullets, look focus, the deck ray, the crane and the sling.</summary>
        public const uint HitMeshBit = 1u << 15;
        /// <summary>The layer bits that make THIS vehicle solid to other vehicles -- what ghosting has to
        /// clear so a towing cab can phase through the trailer it is backing under.
        ///
        /// The private chassis bit for a vehicle with a mesh hitbox, bit 0 for one without.
        ///
        /// PER INSTANCE, not per build. Keyed on the global flag it would claim a ship -- which never gets a
        /// hitbox, so never gives bit 0 up -- sits on a layer it does not have.</summary>
        uint SolidBit => _hitMesh != null ? ChassisBit : 1u << 0;

        /// <summary>Test seam: the bit that means "solid" for THIS vehicle. Exposed so a test can assert the
        /// un-ghosted layer was restored without hard-coding a layer scheme that has now changed twice.</summary>
        public uint DebugSolidBit => SolidBit;

        public int DebugBoxHullsDisabled;   // fitted boxes taken out of physics once the hulls landed
        public int DebugHitMeshTris;        // triangles in the mesh hitbox, 0 when it is off
        Mesh _decomposeMesh;   // set at build; turned into collision shapes on _Ready (VHACD needs a scene tree)
        bool _decomposeCars;   // this is an ordinary vehicle body, not the ship's deckhouse -> cheaper VHACD settings
        static readonly System.Collections.Generic.Dictionary<string, Godot.Collections.Array<Shape3D>> _decomposeCache = new();
        string _decomposeKey;
        public int DebugDecomposedHulls;   // test seam: how many convex hulls the decomposition produced

        /// <summary>Run Godot's convex decomposition on the region a spec asked for, and hang the resulting hulls
        /// on this body. Deferred to _Ready because CreateMultipleConvexCollisions works by adding a StaticBody3D
        /// SIBLING, so it needs to be in a tree -- and cached per mesh region, because VHACD is far too slow to
        /// repeat for every ship that spawns.</summary>
        public override void _Ready()
        {
            SetProcess(false); if (_water != WaterMode.Car) TickHub.AddProcess(this, HubProcess);   // PERF: boat-only wake tick, through the hub (3 boats x 500 fps of chain-walk was 15% of the bridge)
            base._Ready();
            GrassDisplacers.Register(this, GrassDisplacers.VehicleRadius);   // master: a driven vehicle flattens grass in a wide swath under + around it
            if (_decomposeMesh == null || ForceBoxHull) return;
            if (!_decomposeCache.TryGetValue(_decomposeKey, out var shapes) && (shapes = LoadBakedHulls(_decomposeKey)) != null)
                _decomposeCache[_decomposeKey] = shapes;   // BAKED (shipped in content/vehicle_hulls or the machine's user:// cache): no VHACD on load
            if (shapes == null)
            {
                var mi = new MeshInstance3D { Mesh = _decomposeMesh };
                AddChild(mi);
                // Tight, because the whole point is not to fill in the deckhouse's steps and voids. At the
                // defaults (24 hulls / 0.15 concavity) VHACD merged it down to 8 hulls and only got the
                // invisible-wall count from 633 to 527 -- barely better than the hand-cut bands it replaced.
                // UG_CARHULLS / UG_CARCONCAVITY override the car numbers so the fidelity of the hitbox
                // against the model can be SWEPT and measured, rather than argued about. They change
                // the shapes, so the cache key below carries them too.
                int carHulls = int.TryParse(System.Environment.GetEnvironmentVariable("UG_CARHULLS"), out var _ch) ? _ch : 12;
                float carConc = float.TryParse(System.Environment.GetEnvironmentVariable("UG_CARCONCAVITY"),
                                               System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.InvariantCulture, out var _cc) ? _cc : 0.08f;
                var settings = _decomposeCars
                    ? new MeshConvexDecompositionSettings   // ordinary vehicle: a near-convex shell, cheap to describe
                    {
                        MaxConvexHulls = (uint)carHulls,
                        MaxConcavity = carConc,
                        MaxNumVerticesPerConvexHull = 16,
                        Resolution = 10000,
                    }
                    : new MeshConvexDecompositionSettings
                    {
                        MaxConvexHulls = 48,
                        MaxConcavity = 0.02f,
                        MaxNumVerticesPerConvexHull = 24,
                        Resolution = 50000,
                    };
                mi.CreateMultipleConvexCollisions(settings);
                // The generated StaticBody3D is a child of the MESH INSTANCE, not a sibling of it -- harvesting
                // from the vehicle's own children found nothing and reported "hulls harvested: 0" while the
                // vehicle's child count sat unchanged at 22, which is what pointed at this.
                shapes = new Godot.Collections.Array<Shape3D>();
                foreach (var gen in mi.GetChildren())
                    if (gen is StaticBody3D sb)
                        foreach (var cs in sb.GetChildren())
                            if (cs is CollisionShape3D csh && csh.Shape is ConvexPolygonShape3D) shapes.Add(csh.Shape);
                GD.Print($"[DECOMP] region tris={_decomposeMesh.GetFaces().Length / 3} -> {shapes.Count} convex hulls");
                _decomposeCache[_decomposeKey] = shapes;
                SaveBakedHulls(_decomposeKey, shapes);   // next load on this machine reads the bake instead of decomposing
                mi.QueueFree();   // takes the generated body with it; the shapes themselves are refcounted and survive
            }
            foreach (var sh in shapes) AddChild(new CollisionShape3D { Shape = sh });
            DebugDecomposedHulls = shapes.Count;
            // THE MODEL IS THE HITBOX (strawberry 2026-09-02: "use the model as the collision mesh. not boxes
            // of best fit. no half measure."). With hulls attached, the two fitted boxes come out of physics.
            // Measured on the silhouette from five angles, body-pass against hull-pass at a frozen camera:
            //   sedan  boxes+hulls 15.1% of the car's outline OUTSIDE the model -> hulls alone 8.1%
            //   van                11.8%                                        -> 6.7%
            // and coverage barely moves (sedan 6.5% -> 6.8% uncovered, van 5.8% -> 6.4%), because the hulls
            // already cover what the boxes covered. Only when hulls actually landed: shapes.Count of 0 would
            // otherwise leave the car with no collision at all, which is a hole you drive the world through.
            // Guarded by name so a spec's ExtraBoxes, landing gear and the bumper Area are untouched.
            // UG_KEEPBOXHULLS=1 leaves them in, so the before/after is ONE FLAG on ONE BINARY rather than
            // two builds -- the A/B this change is justified by has to compare like with like.
            if (_decomposeCars && shapes.Count > 0
                && System.Environment.GetEnvironmentVariable("UG_KEEPBOXHULLS") != "1")
            {
                int off = 0;
                foreach (var ch in GetChildren())
                    if (ch is CollisionShape3D cs && !cs.Disabled
                        && (cs.Name == "BellyBox" || cs.Name == "RoofBox")) { cs.Disabled = true; off++; }
                DebugBoxHullsDisabled = off;
                GD.Print($"[DECOMP] fitted boxes taken out of physics: {off} (kept in the tree for look-focus)");
            }
            GD.Print($"[DECOMP] hulls harvested: {shapes.Count}");
            _decomposeMesh = null;
        }

        /// <summary>The triangles of `mesh` inside an AABB, as their own mesh -- what gets decomposed.</summary>
        // ---- BAKED CONVEX HULLS (strawberry 2026-09-03: "loading optimizations ... vehicles is 50% of the loading!") ----
        // VHACD (CreateMultipleConvexCollisions) ran on the FIRST vehicle of every spec at scene entry: ~100-150 ms x ~13
        // specs on a PEI load, most of the "AddChild" half of the vehicle phase. The result is a pure function of the
        // body mesh + the settings baked into _decomposeKey, so it is stored: res://content/vehicle_hulls/<key-hash>.hulls
        // ships the fleet's bakes (generated by a normal load on the 4080 and committed), user://vehicle_hulls/ caches
        // anything new on first sight. Format: i32 hulls, per hull i32 points, f32 xyz. Disk over compute (master).
        /// <summary>Size of the body mesh file, folded into the decomposition key so a re-ripped/edited mesh never reuses a stale bake.</summary>
        static string BodyStamp(string body)
        {
            try { return new System.IO.FileInfo(ProjectSettings.GlobalizePath($"res://content/{body}")).Length.ToString(); } catch { return "0"; }
        }
        static string HullFileName(string key)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in key) { h ^= c; h *= 1099511628211UL; }
            return h.ToString("x16") + ".hulls";
        }
        static Godot.Collections.Array<Shape3D> LoadBakedHulls(string key)
        {
            string fn = HullFileName(key);
            foreach (var dir in new[] { "res://content/vehicle_hulls/", "user://vehicle_hulls/" })
            {
                string path = ProjectSettings.GlobalizePath(dir + fn);
                if (!System.IO.File.Exists(path)) continue;
                try
                {
                    using var br = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
                    int hulls = br.ReadInt32();
                    if (hulls <= 0 || hulls > 256) continue;
                    var shapes = new Godot.Collections.Array<Shape3D>();
                    for (int i = 0; i < hulls; i++)
                    {
                        int n = br.ReadInt32();
                        if (n <= 0 || n > 4096) { shapes = null; break; }
                        var pts = new Vector3[n];
                        for (int k = 0; k < n; k++) pts[k] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        shapes.Add(new ConvexPolygonShape3D { Points = pts });
                    }
                    if (shapes != null && shapes.Count > 0) { GD.Print($"[DECOMP] baked hulls: {shapes.Count} from {dir}{fn}"); return shapes; }
                }
                catch (System.Exception e) { GD.PushWarning($"[DECOMP] bad hull bake {path}: {e.Message}"); }
            }
            return null;
        }
        static void SaveBakedHulls(string key, Godot.Collections.Array<Shape3D> shapes)
        {
            try
            {
                string dir = ProjectSettings.GlobalizePath("user://vehicle_hulls/");
                System.IO.Directory.CreateDirectory(dir);
                using var bw = new System.IO.BinaryWriter(System.IO.File.Create(dir + HullFileName(key)));
                bw.Write(shapes.Count);
                foreach (var sh in shapes)
                {
                    var pts = (sh as ConvexPolygonShape3D)?.Points ?? System.Array.Empty<Vector3>();
                    bw.Write(pts.Length);
                    foreach (var pt in pts) { bw.Write(pt.X); bw.Write(pt.Y); bw.Write(pt.Z); }
                }
                GD.Print($"[DECOMP] baked {shapes.Count} hulls -> user://vehicle_hulls/{HullFileName(key)}  (key: {key})");
            }
            catch (System.Exception e) { GD.PushWarning($"[DECOMP] could not bake hulls: {e.Message}"); }
        }

        static bool In(Vector3 p, Vector3 lo, Vector3 hi) =>
            p.X >= lo.X && p.X <= hi.X && p.Y >= lo.Y && p.Y <= hi.Y && p.Z >= lo.Z && p.Z <= hi.Z;

        static Mesh MeshRegion(Mesh mesh, Vector3 lo, Vector3 hi)
        {
            var src = mesh.GetFaces();
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            int kept = 0;
            for (int i = 0; i + 2 < src.Length; i += 3)
            {
                // EVERY vertex must be inside, not the centroid. A centroid test keeps the WHOLE triangle, and
                // the deck plate is triangulated into enormous triangles running most of the ship's length -- one
                // with a corner up on the 12 m rim has its centroid above y=12 and gets kept, dragging collision
                // geometry out to z=-10 with it. That is why the trimesh deckhouse made the deck read 11.47
                // instead of 11.00: not the approach, my extraction.
                if (!In(src[i], lo, hi) || !In(src[i + 1], lo, hi) || !In(src[i + 2], lo, hi)) continue;
                st.AddVertex(src[i]); st.AddVertex(src[i + 1]); st.AddVertex(src[i + 2]);
                kept += 3;
            }
            return kept >= 3 ? st.Commit() : null;
        }

        /// <summary>True when this vessel has a deck things can ride on (see Spec.DeckVolume).</summary>
        public bool CarriesRiders => _deckVolume != Vector3.Zero;

        /// <summary>How fast a point on this hull is actually travelling, INCLUDING the tangential term from its
        /// own rotation -- which is the whole difference on a turning ship, where the bow and the stern are going
        /// in visibly different directions. Horizontal only: the vertical axis belongs to gravity and contact.</summary>
        public Vector3 DeckPointVelocity(Vector3 worldPoint)
        {
            var v = LinearVelocity + AngularVelocity.Cross(worldPoint - ToGlobal(CenterOfMass));
            return new Vector3(v.X, 0f, v.Z);
        }

        /// <summary>Yaw rate, rad/s -- what a rider has to turn at to keep facing the same way along the deck.</summary>
        public float DeckYawRate => AngularVelocity.Y;

        // Spec.SteadyHull: hold her flat and level. 10, not the 4 this started at -- 4 already took the EMPTY
        // hull from 0.259 m/s to a clean 0.000, but a machine parked on the deck still rang her 22.7 mm
        // peak-to-peak, because what a load actually presses with oscillates around its weight and only the mean
        // is cancelled. Damping the leftover is cheaper and steadier than trying to track the AC part of a
        // contact force through a finite difference, which was measurably worse (see DeckImpactMinSpeed).
        // Both are per-second decay RATES, applied directly to velocity (see the use site) -- no mass or inertia
        // term, and unconditionally stable.
        // The trim number is a COMPROMISE and both ends of it are measured. It has to be stiff because the
        // residual is a continuous excitation, not a decaying ring: a load's real contact force wanders around
        // its weight, only the mean is cancelled, and the leftover acts 10 m off centre where a 66 m hull turns
        // a fraction of a degree into a long swing at the deck corner. But it also stops her HEELING, and a boat
        // that cannot heel into its own turn loses drive: at 25 (half the roll rate removed every tick) the deck
        // was beautifully steady at 11.5 mm and she fell to 11.5 m/s with a 33 s circle, against 12.6 and 28 s.
        // Measured at a single coefficient for both axes: 25 gave a lovely 11.5 mm deck and 11.5 m/s / 33 s;
        // 8 gave 12.5 m/s / 28 s (exactly the tuned baseline) and 100.7 mm of deck. Splitting by AXIS did not
        // help (11.8 m/s / 30 s and 113.8 mm) because the residual lives in roll either way. Splitting by STATE
        // does: vehicle.boat_hull drives her, so it reads SteadyDampUnderWay; vehicle.ship_orca_landing parks
        // her, so it reads the stiff pair. Neither gate is allowed to win alone.
        const float SteadyHeaveDamp = 10f, SteadyPitchDamp = 25f, SteadyRollDamp = 25f, SteadyDampUnderWay = 3f;
        const float DeckRiderSettle = 12f;     // per-second rate at which a carried rider's bounce is damped out
        const float DeckImpactMinSpeed = 1f;   // m/s of descent below which a load is parked, not landing
        const float DeckImpactCap = 200f;   // m/s^2, ~20g: the most deceleration we will believe as contact force
        const float DeckGrace = 0.35f;    // how long a rider stays a rider after its last contact frame
        const float DeckSettle = 0.10f;   // contact time required BEFORE carrying starts. A machine that has landed
                                          // spends many ticks settling onto the plating and clears this immediately;
                                          // a hovering one that grazes the deck for a tick or two never does. Without
                                          // it a single glancing frame hands over the hull's full velocity for good --
                                          // measured, a helicopter hovering 5 m over the deck was towed 46 m.

        /// <summary>Carry whatever is standing on this vessel's deck -- the moving-platform problem.
        ///
        /// Applied as the hull's DELTA TRANSFORM since the previous tick (translation, plus yaw about the hull's
        /// own origin), not as a velocity match. A rider has to hold STATION on a deck that is also TURNING, and
        /// matching linear velocity keeps up with the translation while sliding steadily aft through every turn.
        /// At 50 Hz the delta is under 25 cm even at full speed, so writing the rider's position reads as being
        /// carried rather than as a teleport, and it preserves the contact geometry exactly (rider and deck move
        /// together), so the solver has no new penetration to resolve.
        ///
        /// WHO COUNTS AS A RIDER is the part worth being careful about. Box overlap alone would drag a helicopter
        /// that is merely HOVERING over the deck, which is wrong and would feel awful to fly. Contact alone drops
        /// riders constantly -- a machine that has just touched down settles over several ticks and is not in
        /// contact on every one of them. So: in contact AND inside the deck box, with a short grace window that
        /// covers the bounce frames.
        ///
        /// LEAVING is where the "landing on a moving ship" half lives. A rider is released with the deck's own
        /// velocity added once, because that is what it was actually doing -- stepping off a moving deck, not
        /// coming to a dead stop in mid-air. That is also what makes taking off from a moving ship work: the
        /// aircraft is already doing 12 m/s when it breaks contact, exactly like a real deck launch.</summary>
        /// <summary>Publish this vehicle's motion on the HitMesh child, so a player standing on the roof is
        /// carried along.
        ///
        /// THE PROBLEM. CharacterBody3D reads a moving floor's velocity off the body it is standing on. A
        /// RigidBody3D reports its own, which is why a rider on the hulls was carried perfectly -- measured, the
        /// rider tracked the car 1.00 for 1.00 over 12 m. A StaticBody3D reports ConstantLinearVelocity, which
        /// is zero unless something sets it, and with the hitbox on the body under the rider's feet IS a
        /// StaticBody3D: the same 12 m drive carried the rider 2.7 m, a ratio of 0.21. The car drove out from
        /// under them.
        ///
        /// ConstantLinearVelocity is exactly the knob for this -- it means "I am static, but treat me as moving
        /// at this rate for the purposes of things resting on me", and it costs one vector write per tick. The
        /// ANGULAR half matters as much for a car as the linear: a rider on the roof of a car going round a
        /// corner should turn with it rather than slide off the outside.</summary>
        void SyncHitMeshVelocity()
        {
            if (_hitMesh == null || !IsInstanceValid(_hitMesh)) return;
            _hitMesh.ConstantLinearVelocity = LinearVelocity;
            _hitMesh.ConstantAngularVelocity = AngularVelocity;
        }

        void CarryDeckRiders(float delta)
        {
            DebugDeckRiders = 0;
            if (_deckVolume == Vector3.Zero) return;
            var xf = GlobalTransform;
            if (!_deckHasPrev) { _deckPrevXf = xf; _deckHasPrev = true; return; }
            var prev = _deckPrevXf; _deckPrevXf = xf;

            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) return;

            // 1. WHO IS TOUCHING US AT ALL. Cheap, and it is the difference between carrying and dragging.
            var touching = new System.Collections.Generic.HashSet<Node3D>();
            foreach (var b in GetCollidingBodies())
                if (b is Node3D n3 && n3 != this) touching.Add(n3);

            // 2. ...AND IS INSIDE THE DECK BOX. Filters out a boat nudging the hull SIDE, which is touching but
            //    is not aboard.
            _deckQ ??= new PhysicsShapeQueryParameters3D
            {
                Shape = new BoxShape3D(),
                CollisionMask = (1u << 3) | (1u << 5),   // players (bit3) + vehicles (bit5); NOT bit0, or every tick
                                                         // scoops up the terrain the hull is floating over
                CollideWithBodies = true,
            };
            ((BoxShape3D)_deckQ.Shape).Size = _deckVolume;
            _deckQ.Transform = new Transform3D(xf.Basis, xf * _deckCenter);
            _deckQ.Exclude = new Godot.Collections.Array<Rid> { GetRid() };

            var aboard = new System.Collections.Generic.HashSet<Node3D>();
            foreach (var h in space.IntersectShape(_deckQ, 24))
            {
                if (h["collider"].As<GodotObject>() is not Node3D body) continue;
                if (body == this || IsAncestorOf(body) || body.IsAncestorOf(this)) continue;
                if (body is StaticBody3D) continue;                              // the world does not ride
                if (body is Vehicle bv && bv._water != WaterMode.Car && bv._afloat) continue;   // another hull FLOATING alongside is not cargo
                aboard.Add(body);
            }
            // 2a. CARGO DOES NOT WEIGH ON THE HULL (strawberry 2026-08-19: "we should have the ship have an
            // effect on other vehicles, but other vehicles have no effect on the ship"). Every vehicle in this
            // game masses the same GlobalMass 900, so a ship and the helicopter parked on it weigh exactly the
            // same, and any honest displacement model puts the deck under in short order.
            //
            // Scoped to RESTING ON US, which is not the same question as the carry's "is it on the deck" and so
            // is answered separately. The first version reused the deck box and the rider settle timer, and left
            // three holes strawberry could still see the hull move through: something on the BRIDGE ROOF (above
            // the box), something half over the RAIL (outside it), and the moment of TOUCHDOWN itself (before
            // the timer). No upper or lateral bound here, and no settle wait.
            //
            // The floor of it matters though: cancelling the weight of anything merely TOUCHING would shove the
            // hull UPWARD every time a boat came alongside, since that boat's weight was never on us to begin
            // with. So the test is contact plus "at or above deck height", which separates a machine on the
            // plating (local y 11+) from one bobbing against the side at the waterline (local y ~4.8).
            float deckFloorLocal = _deckCenter.Y - _deckVolume.Y * 0.5f;
            var invXf = xf.AffineInverse();
            DebugDeckLoads = 0;
            // Velocity history is seeded from ABOARD (box overlap), not from contact, and that detail is the
            // difference between this working and not. The cancellation needs the load's velocity from the
            // PREVIOUS tick to know how hard it is being arrested; seeded on first contact there is no previous
            // sample, so the very first tick of the impact -- by far the largest -- was cancelled by exactly
            // zero. An incoming machine is in the deck box for many ticks before it touches anything, so track
            // it from there and the history is already warm when it arrives.
            foreach (var b in aboard) if (b is RigidBody3D pre && !touching.Contains(b)) _deckLoadVy[b] = pre.LinearVelocity.Y;
            if (DeckLoadCancelEnabled)
            foreach (var b in touching)
            {
                if (b is not RigidBody3D load) continue;
                if (b is Vehicle lv && lv._water != WaterMode.Car && lv._afloat) continue;   // a hull FLOATING alongside carries itself
                if ((invXf * b.GlobalPosition).Y < deckFloorLocal - 2f) continue;            // alongside or beneath us, not resting on us
                // WEIGHT, at the LOAD'S OWN POSITION so the trim moment goes with it -- a heli parked on the bow
                // would otherwise still pitch her down by the head. Scaled by GravityScale, because a body that is
                // not falling is not pressing on anything and "cancelling" it would push the hull up.
                float cancel = load.Mass * _gravityMag * load.GravityScale;

                // ...PLUS THE IMPACT, which is the half that was missing. What a load actually presses on the deck
                // with is weight PLUS whatever is decelerating it (F = m*a), and only the weight term was being
                // removed. So the settled draft came out perfect while the moment of TOUCHDOWN still punched the
                // hull down -- 0.08 to 0.12 m for an orca arriving at 8.8 m/s, which reads exactly as strawberry
                // put it: "it sinks when i first land". The deceleration IS the contact force, so cancel it too.
                //
                // Capped, because a single frame of deep interpenetration can report an enormous deceleration and
                // an uncapped cancellation would fire the ship out of the sea. Only upward deceleration counts: a
                // load ACCELERATING downward is in free fall and pressing on nothing.
                float vy = load.LinearVelocity.Y;
                float prevVy = _deckLoadVy.TryGetValue(b, out var pv) ? pv : vy;
                _deckLoadVy[b] = vy;
                // ONLY WHILE IT IS ACTUALLY ARRIVING. The impact term is a finite difference of the load's
                // velocity, and a machine sitting on the deck jitters on its own suspension every tick, so run
                // continuously it is mostly noise -- and because only the upward half is cancelled, biased noise:
                // an impulse train that pushed the hull into a 0.247 m/s ring it does not have when empty (it
                // measures 0.000 with nothing aboard). Gated on a real descent rate it fires for the landing and
                // is silent for the parking, which is the only thing it was ever meant to cover.
                if (prevVy < -DeckImpactMinSpeed)
                {
                    float decel = (vy - prevVy) / Mathf.Max(delta, 1e-5f);
                    if (decel > 0f) cancel += load.Mass * Mathf.Min(decel, DeckImpactCap);
                }

                ApplyForce(Vector3.Up * cancel, b.GlobalPosition - GlobalPosition);
                DebugDeckLoads++;
            }
            if (_deckLoadVy.Count > touching.Count)   // drop anything that has left, so the table cannot grow forever
                foreach (var stale in new System.Collections.Generic.List<Node3D>(_deckLoadVy.Keys))
                    if (!touching.Contains(stale)) _deckLoadVy.Remove(stale);


            // A rider is one that is BOTH aboard and in contact. Touching alone is not enough: a machine that
            // slides off the deck edge and scrapes down the hull side is still touching, and refreshing on
            // contact alone would glue it there and carry it around forever.
            foreach (var b in aboard)
                if (touching.Contains(b))
                {
                    if (!_deckRiders.ContainsKey(b)) _deckRiders[b] = new DeckRider();   // brand new: it has none of our motion yet
                    var st0 = _deckRiders[b]; st0.Grace = DeckGrace; _deckRiders[b] = st0;
                }

            // 3. CARRY, and expire anyone whose grace ran out.
            var comG = ToGlobal(CenterOfMass);
            var delta3 = xf * prev.AffineInverse();                              // how the hull moved this tick
            float dyaw = Mathf.Wrap(xf.Basis.GetEuler().Y - prev.Basis.GetEuler().Y, -Mathf.Pi, Mathf.Pi);
            System.Collections.Generic.List<Node3D> drop = null;
            foreach (var key in new System.Collections.Generic.List<Node3D>(_deckRiders.Keys))
            {
                if (!GodotObject.IsInstanceValid(key)) { (drop ??= new()).Add(key); continue; }
                var st = _deckRiders[key];
                bool onDeck = aboard.Contains(key) && touching.Contains(key);
                if (onDeck) st.Settle += delta; else st.Grace -= delta;
                if (st.Grace <= 0f) { (drop ??= new()).Add(key); continue; }
                if (st.Settle < DeckSettle) { _deckRiders[key] = st; continue; }   // touched, but not yet aboard for real

                if (key is RigidBody3D rb)
                {
                    // A RIGID rider is moved by shifting the FRAME its velocity is measured in, not by writing its
                    // position. Pinning the position looks perfect and is a trap: the rider becomes immovable, the
                    // deck slides against it every tick, and the friction brakes the ship -- measured, one 900 kg
                    // heli aboard cut the hull from 12.5 m/s to 1.4. Handing it the deck's own velocity instead
                    // means there is no relative sliding to generate that friction at all.
                    //
                    // Only the DIFFERENCE since last tick is applied, so the rider keeps whatever velocity is its
                    // own: a car can still be driven around on the deck, and a helicopter can still lift off it.
                    // A rider that has just come aboard has LastVel zero, so it receives the deck's full velocity
                    // on its first tick -- which is exactly "it landed, and now it is going where the ship goes".
                    // Nothing is subtracted on release: something leaving a moving deck keeps the deck's motion.
                    var deckVel = LinearVelocity + AngularVelocity.Cross(key.GlobalPosition - comG);
                    deckVel.Y = 0f;                                              // vertical stays with gravity + contact
                    rb.LinearVelocity += deckVel - st.LastVel;
                    st.LastVel = deckVel;
                    rb.AngularVelocity += Vector3.Up * (AngularVelocity.Y - st.LastYawRate);   // turn with the hull
                    st.LastYawRate = AngularVelocity.Y;

                    // SETTLE THE RIDER ONTO THE DECK, because the rider's bounce is what keeps re-exciting the
                    // hull. What a parked machine actually presses with oscillates around its weight, only the
                    // mean is cancelled, and the leftover lands at the rider's position as a PITCH moment -- a
                    // 66 m hull rocking 0.73 deg swings its deck corner through 149 mm, which is what is left of
                    // "it wobbles" after the damping was fixed. Damping the hull harder only fights that
                    // continuously; damping the RIDER removes it at the source. Allowed by the same rule that
                    // set all this up: the ship may affect other vehicles, they may not affect her.
                    float relVy = rb.LinearVelocity.Y - LinearVelocity.Y;
                    rb.LinearVelocity -= new Vector3(0f, relVy * Mathf.Min(DeckRiderSettle * delta, 1f), 0f);

                }
                else if (key is not CharacterBody3D)
                {
                    // Anything else that is not a character: carry it positionally, horizontally only. The vertical
                    // axis is left to gravity and contact because overwriting it also overwrites the solver's
                    // penetration correction -- with the full 3D write a rider sank into the deck a little further
                    // every tick and the growing contact impulse drove the hull 47 m under water.
                    var carried = delta3 * key.GlobalPosition;
                    key.GlobalPosition = new Vector3(carried.X, key.GlobalPosition.Y, carried.Z);
                    if (Mathf.Abs(dyaw) > 1e-6f)
                        key.GlobalRotation = new Vector3(key.GlobalRotation.X, key.GlobalRotation.Y + dyaw, key.GlobalRotation.Z);
                }
                // A PLAYER (CharacterBody3D) is deliberately NOT carried yet. Its controller rewrites Velocity from
                // input every tick so the frame shift above is erased, and a bare GlobalPosition write on it is
                // undone one tick later by the render-interpolation snapshot (see PlayerController.TeleportTo).
                // Walking the deck of a moving ship needs its own path in that controller; claiming it here would
                // be a carry that silently does nothing.
                _deckRiders[key] = st;
                DebugDeckRiders++;
            }
            if (drop != null) foreach (var d in drop) _deckRiders.Remove(d);
        }

        const float BoatThrust = 15f, BoatTurn = 2.2f, BoatDrag = 0.5f;   // water propulsion / rudder yaw / extra horizontal drag. Thrust 6->15 + drag 0.9->0.5: the source voxel damping now adds its own per-voxel water drag, so the old values left the boat sluggish (~4 m/s); these hit a proper speedboat pace (strawberry)
        const float WaterDensity = 1000f, HullDensity = 500f;            // source Buoyancy.cs: rho_water, and density=500 (a vehicle floats at ~half-submersion)
        // SWAMP tuning. Density 800 (not the boat's 500) because a car is not a hull: at 500 it would ride at
        // half-submersion like a runabout, which reads as a boat rather than as a car that has just gone in. 800
        // balances weight at ~80 % submerged -- waterline around the windows, roof out -- which is what floats.
        const float SwampHullDensity = 800f;
        const float SwampFloatSeconds = 5f;   // how long the trapped air holds it at the surface
        const float SwampSinkSeconds  = 4f;   // and how long that air then takes to bleed away
        const float SwampSubmergeFrac = 0.25f;   // fraction of hull voxels under the surface before it counts as IN the water rather than fording

        // BOAT / AMPHIBIOUS water physics. Buoyancy is a faithful port of the source Buoyancy.cs voxel-Archimedes model:
        // the hull box is sliced 2x2x2; each SUBMERGED voxel gets an Archimedes up-force (rho_water*g*V, depth-scaled by a
        // sqrt curve) + point-velocity damping, applied AT the voxel -> the hull floats level, self-rights, and damps sway.
        // While afloat the drive input becomes forward thrust + rudder yaw (source propels boats via the engine; same feel).
        /// <summary>A LAND vehicle in water: the engine drowns, trapped air floats it briefly, then it sinks.
        /// Self-guards on _swampBuoys, so a boat, an aircraft or a trailer with no hull box never enters here.</summary>
        void ApplySwampedPhysics(float delta)
        {
            if (!Terrain.HasWater || _swampBuoys == null || _exploded) return;
            float seaY = Terrain.SeaLevelY;
            var xf = GlobalTransform;

            // FORDING IS NOT SWAMPING. A quarter of the hull has to be under before the engine drowns, so a car
            // crossing a shallow ford or clipping a puddle keeps running and keeps its wheels on the bottom --
            // which is exactly the behaviour that was here before this method existed.
            int submerged = 0;
            foreach (var lp in _swampBuoys) if ((xf * lp).Y < seaY) submerged++;
            if (submerged < Mathf.CeilToInt(_swampBuoys.Length * SwampSubmergeFrac))
            {
                _swamped = false; _swampTime = 0f;   // drove back out -> the timer resets, but the engine stays off until restarted
                return;
            }
            if (!_swamped) { _swamped = true; EngineHealth = 0f; EngineDrowned = true; }   // DROWNED: engine hp gone and stays gone (strawberry 2026-09-04 "shouldnt be able to restart their engine ever")
            _swampTime += delta;

            // The engine is cut EVERY tick it is under, not once on entry: otherwise the driver simply restarts it
            // and drives on along the seabed, which is the behaviour this exists to remove.
            EngineOn = false;

            // The ELECTRICS drown with it (strawberry_cow 2026-08-24). Same every-tick reasoning as the engine:
            // clearing these once on entry lets the driver flick the headlights back on while submerged, and the
            // alarm re-arms itself on its own timer regardless of anyone touching it.
            //
            // The alarm matters most of the three. Its blip loop re-lights the lamps and honks every 0.5s on its
            // own clock, so a car alarm going off underwater keeps flashing and sounding forever -- exactly the
            // failure the explode path documents at the _alarmed reset above, arrived at from a different
            // direction. Kill the state, not just the output, or the loop simply re-creates it.
            // SetHeadlights, not `_headlightsOn = false`. The flag is not what lights the lamps -- SetHeadlights
            // is the only thing that touches _headlights.Visible, the beam mesh and the lens emission, so
            // clearing the field alone leaves a submerged car glowing with its headlight shafts still drawn and
            // only the boolean saying otherwise.
            if (_headlightsOn) SetHeadlights(false);   // guarded: this runs EVERY physics tick while under, and SetHeadlights re-touches materials + the mote emitter
            _sirenOn = false;                     // polled per-frame by the lightbar block (:6499), so the flag IS the switch here
            if (_sirenAudio != null && _sirenAudio.Playing) _sirenAudio.Stop();
            _alarmed = false; _alarmTimer = 0f; _alarmBlip = 0f; _alarmLit = false;
            // Taillights are deliberately NOT set here: :6513 recomputes them every frame from EngineOn and
            // _headlightsOn, both of which are now false, so they follow on their own. Forcing the field would be
            // overwritten next frame anyway, and writing code that the next tick undoes is how a fix gets
            // credited for something it is not doing.

            // Trapped air holds it up, then escapes. Linear bleed rather than a curve -- the interesting moment is
            // WHEN it starts going down, and a curve only makes that harder to predict without looking different.
            float lift = _swampTime <= SwampFloatSeconds
                ? 1f
                : 1f - Mathf.Clamp((_swampTime - SwampFloatSeconds) / SwampSinkSeconds, 0f, 1f);
            // NOTE the lift term goes to zero but the DAMPING below does not: once the air is gone the hull still
            // has to push water out of the way on the way down. Returning early here instead (which is what this
            // did first) drops it at a clean 9.8 m/s^2 -- a car falling through air that happens to be drawn blue.
            var comGlobal = ToGlobal(CenterOfMass);
            float volume = Mass / SwampHullDensity;
            var archPerVoxel = new Vector3(0f, WaterDensity * _gravityMag * volume * lift, 0f) / _swampBuoys.Length;
            foreach (var localPoint in _swampBuoys)
            {
                var worldPoint = xf * localPoint;
                if (worldPoint.Y >= seaY) continue;
                var pv = LinearVelocity + AngularVelocity.Cross(worldPoint - comGlobal);
                var damping = -pv * 0.1f * Mass;   // same coefficient as the hull model at 8 voxels
                float subFactor = Mathf.Sqrt(Mathf.Clamp((seaY - worldPoint.Y) / (2f * _voxelHalfHeight) + 0.5f, 0f, 1f));
                ApplyForce(damping + subFactor * archPerVoxel, worldPoint - GlobalPosition);
            }
        }

        // foam wake on the RENDER frame: drive it from the INTERPOLATED hull pose so the leading
        // foam stays glued to the visually-rendered ship. Building it off the raw 50Hz physics pose
        // lags/jitters a step behind her -- the same interp trap as the flatbed container rider.
        public override void _Process(double delta) => HubProcess(delta);   // forwarder; boats register HubProcess with TickHub (_Ready), the engine callback stays off
        public void HubProcess(double delta)
        {
            if (_water == WaterMode.Car) return;
            bool active = _afloat && _buoys != null;
            if (active && _wake == null)
            {
                _wake = new WakeTrail();
                AddChild(_wake);   // TopLevel child -> world-space, but freed with the vehicle
            }
            if (_wake != null)
            {
                float wspd = active ? new Vector3(LinearVelocity.X, 0f, LinearVelocity.Z).Length() : 0f;
                _wake.Push(GetGlobalTransformInterpolated(), _bowLocalZ, Terrain.SeaLevelY, wspd, (float)delta);   // apex at the MEASURED bow tip; speed 0 -> the trail just ages out
            }
        }

        void ApplyWaterPhysics(float delta)
        {
            _afloat = false;
            if (!Terrain.HasWater || _buoys == null) return;
            _waterTime += delta;
            float seaY = Terrain.SeaLevelY;
            var xf = GlobalTransform;
            var comGlobal = ToGlobal(CenterOfMass);
            float volume = Mass / HullDensity;                                            // source: volume = mass / density
            var archPerVoxel = new Vector3(0f, WaterDensity * _gravityMag * volume * _buoyReserve, 0f) / _buoys.Length;   // rho_water * |g| * V * reserve, split per voxel
            // FLOATPLANE "on the step": as the floats plane forward, hydrodynamic planing keeps them UP but the
            // pontoons stop resisting PITCH (they skim the surface instead of displacing along their length). Model
            // that below by fading the fore-aft moment ARM of each buoy with speed -- full vertical float force (so
            // it never sinks), but a collapsing pitch-stiffness so the elevator can rotate the nose up to take off.
            float _planePitchFree = _plane
                ? Mathf.Lerp(1f, 0.04f, Mathf.Clamp((new Vector3(LinearVelocity.X, 0f, LinearVelocity.Z).Length() - 4f) / 9f, 0f, 1f))
                : 1f;
            int submerged = 0;
            foreach (var localPoint in _buoys)
            {
                var worldPoint = xf * localPoint;                                         // source: transform.TransformPoint(localPoint)
                if (worldPoint.Y >= seaY) continue;                                       // WaterUtility: above the flat sea surface -> not underwater
                float surface = seaY + Mathf.Sin((worldPoint.X + worldPoint.Z) * 8f + _waterTime) * _waveAmp;   // source client-side wave ripple (_waveAmp 0 on a SteadyHull -- see Spec.SteadyHull)
                if (worldPoint.Y - _voxelHalfHeight >= surface) continue;                 // voxel not yet within voxelHalfHeight of the surface -> no force
                submerged++;
                var pv = LinearVelocity + AngularVelocity.Cross(worldPoint - comGlobal);  // source: rootRigidbody.GetPointVelocity(worldPoint)
                float _bdMul = float.TryParse(System.Environment.GetEnvironmentVariable("UG_BUOYDAMP"), out var _bd) ? _bd : _buoyDamp;   // damping mult: env override else the per-vehicle spec value
                // Base coefficient combines TWO fixes: a FLOATPLANE's pontoons plane across the surface with far less
                // drag than a displacement hull (a third -- else the Otter's takeoff run needs rocket thrust); AND
                // divide by voxel COUNT so grid RESOLUTION is not also a drag knob (source hardcodes 0.1 at 2x2x2=8, so
                // raising the ship to 27 voxels would otherwise triple its water drag + halve its top speed). The 8f
                // keeps every 2-slice hull (runabout/APC/pontoons) bit-identical to the source calibration.
                float dampBase = _plane ? 0.035f : 0.1f;
                // ...and multiplied by the RESERVE, which is not a fudge: raising displacement means FEWER voxels
                // need to be under to balance the weight, and drag is applied per SUBMERGED voxel -- so reserve
                // silently removes water drag. Measured: 4x reserve took the ship from 14.3 to 20.1 m/s and cut
                // its 360 from 28 s to 13 s, i.e. a buoyancy knob quietly became a handling knob. Scaling the
                // coefficient by the same factor keeps total drag where it was. Reserve is 1 for every other
                // hull, so this term is exactly 1.0 there and the runabout/APC/pontoons stay bit-identical.
                float dampPerVox = dampBase * (8f / _buoys.Length) * _buoyReserve;
                var damping = -pv * dampPerVox * Mass;                                    // source: -velocity * 0.1 * mass (all-axis)
                damping.Y += -pv.Y * dampPerVox * Mass * (_bdMul - 1f);                   // EXTRA vertical-only damping -> settles FAST (floatplane calm at rest / big hull settles) without horizontal drag
                float subFactor = Mathf.Sqrt(Mathf.Clamp((surface - worldPoint.Y) / (2f * _voxelHalfHeight) + 0.5f, 0f, 1f));   // source sqrt depth curve
                Vector3 arm = worldPoint - GlobalPosition;
                if (_planePitchFree < 1f)   // floatplane on the step: shrink the fore-aft moment arm so buoyancy holds it up but stops pinning the pitch
                {
                    Vector3 fwdAxis = xf.Basis.Z;
                    arm -= fwdAxis * (arm.Dot(fwdAxis) * (1f - _planePitchFree));
                }
                ApplyForce(damping + subFactor * archPerVoxel, arm);   // source: AddForceAtPosition(force, worldPoint)
            }
            _afloat = submerged > 0;
            if (!_afloat) return;
            if (_steadyHull)
            {
                // A BUILD PLATFORM HOLDS STILL. Damps the hull's own heave and roll rate directly, rather than
                // going through the voxels -- the voxel forces are what is exciting the motion in the first
                // place, and the residual is a limit cycle around the quantisation, not something a stiffer
                // spring fixes. Vertical and rotational only, so it costs her nothing in forward speed.
                // Applied as a DIRECT EXPONENTIAL DECAY on our own velocity rather than as a damping force, and
                // that is a stability decision, not a style one. The force form's strength has to be scaled by
                // MASS for heave and by INERTIA for rotation -- get that wrong and rotation is ~400x too weak
                // (it was: `damp * Mass` on a hull whose pitch inertia is 342581 against a mass of 900 gave a
                // decay constant of ~32 SECONDS, and the deck corner swung 548 mm while the centre moved 10 mm).
                // Get it right and strong, and the explicit integration overshoots: at damp 25 the hull produced
                // 72381 non-finite transforms the moment she was turning. Removing a FRACTION of the rate, capped
                // at 100 %, cannot overshoot at any strength or timestep, and needs no mass or inertia term at all.
                float kH = Mathf.Min(SteadyHeaveDamp * delta, 1f);
                // HOLD STILL WHEN SHE IS HOLDING STATION; SAIL NORMALLY WHEN SHE IS SAILING. The two requirements
                // are separated by WHEN, not by axis -- my first guess was that cargo rocks her in PITCH and
                // turning uses ROLL, so the axes could be damped independently. Measured, that was simply wrong:
                // with pitch clamped the residual read pitch 0.008 deg against roll 0.560. Roll inertia is 45881
                // where pitch is 342581, so roll is the soft mode and anything left over goes there.
                //
                // So gate on the DRIVE INPUT instead. Parked -- which is when you are standing on her placing
                // foundations -- hold her rigid. Under helm, hand the roll back, because a boat turns on its heel
                // and clamping it cost 12.6 m/s and a 28 s circle down to 11.5 and 33 s.
                float sailing = Mathf.Clamp(Mathf.Abs(_inSteer) + Mathf.Abs(_inThrottle), 0f, 1f);
                float kP = Mathf.Min(Mathf.Lerp(SteadyPitchDamp, SteadyDampUnderWay, sailing) * delta, 1f);
                float kR = Mathf.Min(Mathf.Lerp(SteadyRollDamp, SteadyDampUnderWay, sailing) * delta, 1f);
                var lv = LinearVelocity;
                LinearVelocity = new Vector3(lv.X, lv.Y * (1f - kH), lv.Z);   // heave only; never touches her way through the water
                var av = AngularVelocity;
                // PITCH HARD, ROLL GENTLY, and the split is what resolves a conflict that looked unresolvable.
                // Cargo sits along the centreline, so what it rocks her in is PITCH (about X). What a boat TURNS
                // on is ROLL (about Z) -- damping that is what cost her 12.6 m/s and a 28 s circle down to 11.5
                // and 33 s at a single stiff coefficient. They are different axes, so they do not have to trade:
                // hold the pitch rigid for the deck, leave her the heel she steers with.
                AngularVelocity = new Vector3(av.X * (1f - kP), av.Y, av.Z * (1f - kR));   // never yaw -- that is steering
            }
            if (_plane)
            {
                // ROLL DAMPING on the water: damp the roll RATE about the body forward axis so a wave-induced
                // lean settles fast + level (the reduced float drag that lets it accelerate for takeoff left roll
                // underdamped, so a lean lingered ~4s and read as "wants to tip over"). Roll axis only -> adds no
                // fore/aft drag, so it doesn't slow the takeoff run.
                Vector3 fwdAx = xf.Basis.Z;
                float rollRate = AngularVelocity.Dot(fwdAx);
                ApplyTorque(-fwdAx * (rollRate * PlaneWaterRollDamp * Mass));
            }
            if (!_plane)   // a FLOATPLANE keeps only the buoyancy above -- its PROP (StepPlane) does the driving,
            {              // and the boat rudder/thrust would fight the flight model + cap its takeoff run
                var fwd = -xf.Basis.Z;                                                        // boat forward = -Z
                float thr = EngineOn ? _inThrottle : 0f;
                ApplyCentralForce(fwd * thr * BoatThrust * Mass);                             // propulsion
                float spd = LinearVelocity.Dot(fwd);
                float rudder = Mathf.Clamp(Mathf.Abs(spd) * 0.25f + 0.25f, 0.25f, 1f);        // speed-dependent rudder + a little idle authority
                ApplyTorque(Vector3.Up * -_inSteer * BoatTurn * Mass * rudder * _turnScale);  // rudder yaw (x _turnScale: see Spec.TurnScale -- mass-scaled torque vs length-scaled inertia)
                ApplyCentralForce(new Vector3(-LinearVelocity.X, 0f, -LinearVelocity.Z) * BoatDrag * Mass);   // extra horizontal water drag -> controllable top speed
                if (_water == WaterMode.Boat) { EngineForce = 0f; Brake = 0f; }               // a pure boat has no useful wheels
            }
            if (++_waterFrame % 30 == 0 && System.Environment.GetEnvironmentVariable("UG_BOATDBG") == "1") GD.Print($"[boat] afloat={_afloat} sub={submerged}/{_buoys.Length} y={GlobalPosition.Y:F2} spd={LinearVelocity.Length():F1} thr={_inThrottle:F1} str={_inSteer:F1}");   // gated behind UG_BOATDBG -- was spamming the console every 30 frames afloat (master); counter still ticks
        }
    }
}
