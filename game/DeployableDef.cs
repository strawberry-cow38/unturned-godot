using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // A deployable (Unturned "Useable Barricade") -- an item you HOLD, aim to show a placement ghost
    // (blue valid / red invalid), then LMB to plant a real object in the world. Ported from the release
    // src: UseableBarricade drives the hold loop off ItemBarricadeAsset fields (range/radius/offset), and
    // BarricadeManager.getRotation stands the (flat-authored) model up with a -90 X pre-rotation.
    // First pass = the placement MECHANIC only; the generator/spotlight electrical behaviour is a later pass.
    public class DeployableDef
    {
        public ushort Id;
        public string Name;
        public string Model;       // content/objects/<Model>.obj (+ _tex.png palette)
        public Vector3 Size;       // Size_X/Y/Z footprint (.dat) -> the placed collision box
        public float Offset;       // surface standoff: point = hit + normal*offset (ItemBarricadeAsset Offset)
        public float Radius;       // clearance sphere for the overlap check (ItemBarricadeAsset Radius)
        public float Range;        // aim reach from the eye (ItemBarricadeAsset Range)
        public float Health;
        public float Fuel;         // src .dat Capacity: fuel tank size (InteractableGenerator.capacity). 0 = no fuel gauge (e.g. spotlight, which draws from a wired generator)
        public bool IsBattery;     // a battery: its IN terminal charges the stored Energy, its OUT terminal discharges it (produces while Energy > 0)
        public bool IsSwitch;      // a power switch: an F-toggle gates its Passthrough (PowerConducting). Remembers state; a light shows on/off
        public float EnergyMax, ChargeWatts;   // battery: stored-energy capacity (watt-SECONDS) + the IN charge rate (W)
        public bool IsWindTurbine;    // a wind turbine: output ramps with WindField wind x a height-above-sea multiplier; blades spin ~ wind
        public bool IsStorage;        // a placeable storage container with its own IPowerDevice consumer port (the fridge): spawns via FridgeDeploy, not a plain Deployable body
        public bool Upright;          // build the mesh already-vertical (skip the flat->stand-up rotation) -- for procedural models like the turbine
        public BarricadeMount Mount = BarricadeMount.Floor;   // which surface family this places on: Floor (ground, default) / Wall / Sticky. BarricadePlacer + Barricade read this so a barricade def carries its own mount rule.
        public string PlaceSound;  // src .dat PlacementAudioClip stem (content/sounds/<stem>.wav) played when planted; null = silent
        public string HoldMesh, HoldAlbedo;   // content/<mesh>.obj + palette for the 1st-person carry model (item.prefab); null -> EmptyHands fallback (ghost only)
        public bool ShatterOnDeath;   // true -> explodes into flying debris + vanishes (no salvageable husk, drops nothing); false -> charred blowtorch-salvageable wreck
        /// <summary>A placeable DOOR: the prop name to look up in content/objects/doors.txt (the SAME catalog
        /// the container doors use). Non-null routes placement through DoorDeploy.SpawnFor instead of spawning a
        /// plain Deployable body, mirroring IsStorage -> FridgeDeploy and Fluid -> FluidDeploy.</summary>
        public string DoorProp;

        // CRAFTING STATION (strawberry 2026-08-22): a placed deployable GRANTS these crafting tag GUIDs to any player
        // within CraftingRange + line-of-sight -> recipes needing those tags unlock. From the src barricade's
        // PlaceableProvidesCraftingTags + Range (e.g. Workbench provides the Workbench tag at 4 m).
        public string[] CraftingTags;
        public float CraftingRange = 4f;

        public bool ProcBox;          // true -> a plain gray BoxMesh of Size (no .obj/palette); the custom splitters use it
        public bool ExplosionProof;   // src Proof_Explosion: immune to OTHER explosions' damage (the Charge) so a stack doesn't chain-detonate -- you blow them on the Detonator's command, not from one stray blast

        // --- TRAP: IsTrap makes the placed Deployable a hazard. THREE families: EXPLOSIVE proximity (landmine) fires a
        //     TrapBlast AoE (DamageTool.explode) + self-consumes; CONTACT (spike, TrapExplosive=false) shreds whoever ENTERS
        //     + wears down; MANUAL (charge, TrapManual=true) is an INERT explosive -- no auto-trigger, blows only when a
        //     Detonator fires it (Deployable.DetonateManual) or it's shot. Split by TrapExplosive (src isExplosive) + TrapManual. ---
        public bool IsTrap;
        public bool TrapExplosive = true;   // src isExplosive: true = BLAST (landmine/charge); false = CONTACT hazard (spike)
        public bool TrapManual = false;     // src Charge/InteractableCharge: INERT until a Detonator triggers it (no proximity/contact poll)
        public float TrapTrigger = 1.4f;   // proximity radius that fires it (m)
        public float TrapBlast = 6f;       // AoE explosion radius on trigger (m; 0 = contact-only)
        // TODO(zombie-removal): TrapZombieDamage NOT removed despite the name -- Deployable.cs's DetonateTrap
        // forwards it straight into PlayerController.Explode (off-limits, handled elsewhere), whose
        // zombieDamage parameter is ALSO spent on the "animals" group there. Deleting the field breaks that
        // off-limits compile AND silently zeroes animal blast damage from mines/charges, which is non-zombie
        // behaviour -- ambiguous per the task's own "don't guess" rule. Its one purely-zombie use (the
        // contact-trap "zombies" group loop in Deployable.cs.TrapContactTick) WAS removed since that loop also
        // referenced the now-deleted ZombieController type. See the matching note in GunDef.cs/MeleeDef.cs.
        public float TrapZombieDamage = 200f, TrapPlayerDamage = 101f, TrapVehicleDamage = 100f;
        public float TrapAnimalDamage = 0f;   // src Animal_Damage (contact traps); 0 = leaves animals alone (no animal-trap target wired yet)
        public float TrapArmDelay = 1.5f;   // placer grace / src Trap_Setup_Delay: inert this long after planting (landmine QoL 1.5; src spike 0.25)
        public float TrapStructureDamage = 75f;   // src Barricade/Structure_Damage (Landmine.dat 75): blast damage to nearby placed deployables (base-raiding)
        public float TrapWearPerHit = 5f;   // src InteractableTrap BarricadeManager.damage(transform, 5f): a CONTACT trap loses this HP per victim it shreds -> wears out (Health/5 hits) then breaks
        public float TrapCooldown = 0f;     // src Trap_Cooldown: min seconds between two damage events on a contact trap (Spikes.dat 0 -> every distinct ENTER hits)
        public FixtureKind Fixture = FixtureKind.None;   // A3/A2: a server-placed WORLD fixture (GridSource mains / GasPump) vs a normal player-placeable deployable. Bridged to DeployableNetDef.FixtureKind in DeployableNetSchema.

        // FLUID device marker (strawberry 2026-07-22): a non-null Fluid means this "deployable" places a FluidContainer
        // (the fluid IO system), NOT a power Deployable. The placement ghost still uses Size/Offset/Radius/Range; the real
        // fluid mesh + HosePorts are built by FluidContainer on spawn (see FluidDeploy.SpawnFor). Rides catboy's item/
        // placement rail with EXPLICIT ById cases (item ids 9110+, below the asset-factory's 60000+ block).
        public FluidRole? Fluid = null;
        public FluidType FluidType = FluidType.None, FluidOut = FluidType.None;   // source/transformer input + transformer output fluid
        public int FluidWays = 2;                    // splitter outputs / combiner inputs
        public float FluidCapacity = 20000f, FluidRate = 125f;   // tank capacity (mL) + base flow/intake (mL/s, garden-hose gravity)
        public bool FluidInfinite, FluidNoHead;      // submersible INLET: an infinite source with no head pressure (pump-only draw)
        public WaterQuality FluidQuality = WaterQuality.Clean;   // water this source spawns with (natural = tainted; a filled reservoir = tainted; bottled = clean)
        public bool FluidDirties;                    // a transformer that DIRTIES water (the sluice) -> its output resolves to dirty
        public bool FluidPurifies;                   // a POWERED transformer that CLEANS water (the purifier) -> FluidDeploy spawns a FluidPurifier (needs power to run)
        public float WaterDepthMin = -1f, WaterDepthMax = -1f;   // placement must be SUBMERGED in this water-depth band (-1 = no water requirement)
        public static float SeaLevel => Terrain.SeaLevelY;   // per-map water plane world-Y (Terrain reads each map's Lighting.dat seaLevel x 256; = Deployable.WindSeaLevel)
        // barricades are authored lying flat -> a +90 X stands them up. (The src uses -90 in Unity's left-handed
        // space; our rip negates Z into Godot's right-handed space, which flips the sense to +90.)
        public static float StandRotX = float.TryParse(System.Environment.GetEnvironmentVariable("UG_DEPLOYROT"), out var r) ? r : 90f;
        // generator fuel drained per SECOND at FULL load while running (master: realistic, not PZ's "days on 20L"). Scaled
        // by load (idle ~20%). 150 L tank: ~25min real at full load, ~2h idle. Tunable via UG_GENBURN. (metric 1u=1mL: was 0.04 units/s x2500)
        public static float GenFuelBurnPerSec = float.TryParse(System.Environment.GetEnvironmentVariable("UG_GENBURN"), out var gb) ? gb : 100f;

        // --- power connection points (nodes). A wire runs OUTPUT -> ... -> CONSUMER; a CONSUMER may also have a
        //     PASSTHROUGH that re-exports (input - usage). Pos is in the flat authored mesh frame (stands up with the model). ---
        public enum PortKind { Output, Consumer, Passthrough }
        public enum SwitchRole { None, TurnOn, TurnOff }   // a SWITCH's side trigger inputs: fed >=1w -> set the switch state on / off (they draw 0w)
        public struct Port { public PortKind Kind; public Vector3 Pos; public float Watts; public SwitchRole Role; }   // Output.Watts = produced (when source on); Consumer.Watts = drawn; Passthrough.Watts unused (= input - consumers)
        public Port[] Ports = System.Array.Empty<Port>();

        // --- lamps a CONSUMER lights up when powered (src InteractableSpot: the "Spots" node of Light children,
        //     toggled on when isWired && isPowered). Pos/Dir are in the flat authored frame (stand up with the model);
        //     Godot SpotAngle is the HALF-angle so it's src m_SpotAngle/2. ---
        public struct DeployLight { public bool Spot; public Vector3 Pos; public Vector3 Dir; public float Range; public float AngleDeg; public float Energy; public Color Color; }
        public DeployLight[] Lights = System.Array.Empty<DeployLight>();
        static readonly Color LampWarm = new Color(0.9706f, 0.9612f, 0.835f);   // src Lamp m_Color (warm white)

        // src Generator_Small.dat: id 458, Useable Barricade, Build Generator, footprint 2x2x0.5, Offset 0.75
        public static readonly DeployableDef Generator = new()
        {
            Id = 458, Name = "Generator", Model = "Generator_0",
            HoldMesh = "generator_hold.obj", HoldAlbedo = "generator_hold_tex.png", PlaceSound = "metalplacement",   // src Generator_Small.dat PlacementAudioClip Sounds/MetalPlacement.mp3
            Size = new Vector3(2f, 2f, 0.5f), Offset = 0.75f, Radius = 0.5f, Range = 4f, Health = 450f, Fuel = 150_000f,   // 150 L tank (metric 1u=1mL): ~7 jerrycans; burned by LOAD while running (GenFuelBurnPerSec). was PZ 60 units x2500
            Ports = new[] {
                new Port { Kind = PortKind.Output, Pos = new Vector3(0.4f, 0.6f, 0.05f), Watts = 4000f },   // output on the gray-face mid-right (flat frame; tuned visually)
                new Port { Kind = PortKind.Consumer, Role = SwitchRole.TurnOn, Pos = new Vector3(-0.5f, 0.4f, -0.2f), Watts = 0f },   // remote START (green): a >=1w sense (0w draw) spins the engine UP. UG_GTON tunes.
                new Port { Kind = PortKind.Consumer, Role = SwitchRole.TurnOff, Pos = new Vector3(-0.5f, 0.4f, 0.3f), Watts = 0f },  // remote STOP (red): a >=1w sense (0w draw) spins it DOWN. UG_GTOFF tunes.
            },
        };
        // src Spotlight.dat: id 459, Useable Barricade, Build Spot, footprint 2x2x0.55, Offset 1.12
        public static readonly DeployableDef Spotlight = new()
        {
            Id = 459, Name = "Spotlight", Model = "Spotlight_deploy", PlaceSound = "metalplacement",   // src Spotlight.dat PlacementAudioClip Sounds/MetalPlacement.mp3
            Size = new Vector3(2f, 2f, 0.55f), Offset = 1.12f, Radius = 0.5f, Range = 4f, Health = 300f, ShatterOnDeath = true,   // shatters into pieces, no husk/salvage (strawberry)
            Ports = new[] {   // I/O on the left/right of the central pillar, dropped to the feet-X (flat frame: authored X = the
                              // horizontal sides after stand-up, +Z = down toward the base). Master-tuned; UG_SPC/UG_SPP override.
                new Port { Kind = PortKind.Consumer, Pos = new Vector3(-0.13f, 0f, 0.65f), Watts = 250f },
                new Port { Kind = PortKind.Passthrough, Pos = new Vector3(0.13f, 0f, 0.65f), Watts = 0f },
            },
            // src barricade.prefab "Spots": two point lamps (bulb glow) + a spot beam. Positions/dir from the prefab,
            // z-negated into our rip frame; the spot's src full angle 60 -> Godot half-angle 30.
            Lights = new[] {
                new DeployLight { Spot = false, Pos = new Vector3(-0.48f, -0.416f, -1.351f), Range = 4f, Energy = 2.4f, Color = LampWarm },
                new DeployLight { Spot = false, Pos = new Vector3( 0.48f, -0.416f, -1.351f), Range = 4f, Energy = 2.4f, Color = LampWarm },
                new DeployLight { Spot = true, Pos = new Vector3(0f, -0.427f, -1.472f), Dir = new Vector3(0f, -0.966f, 0.259f), Range = 30f, AngleDeg = 30f, Energy = 4f, Color = LampWarm },
            },
        };

        // --- Splitters (custom -- our own system, not from src): a gray junction box that fans ONE power input out to
        //     N outputs. The input is a 0-watt CONSUMER (a relay -- draws nothing for itself); each output is a
        //     PASSTHROUGH that re-exports the FULL input, so the wattage isn't divided -- downstream devices each pull
        //     what they need. Ports sit on opposite faces: the orange input on the back, cyan outputs fanned across the
        //     front. ProcBox -> a plain gray BoxMesh (no .obj), per master's "a basic gray box will do". ---
        static DeployableDef MakeSplitter(ushort id, string name, float width, float[] outX)
        {
            var ports = new Port[outX.Length + 1];
            ports[0] = new Port { Kind = PortKind.Consumer, Pos = new Vector3(0f, -0.18f, 0f), Watts = 0f };   // input relay (back face)
            for (int i = 0; i < outX.Length; i++)
                ports[i + 1] = new Port { Kind = PortKind.Passthrough, Pos = new Vector3(outX[i], 0.18f, 0f), Watts = 0f };   // outputs, fanned across the front face
            return new DeployableDef
            {
                Id = id, Name = name, ProcBox = true, PlaceSound = "metalplacement",
                Size = new Vector3(width, 0.36f, 0.5f),   // flat frame: X = width, Y = depth (front/back port faces), Z = height (stands up)
                Offset = 0.7f, Radius = 0.35f, Range = 4f, Health = 200f, Fuel = 0f,   // passive: no fuel/engine. Offset > Radius so the clearance sphere clears flat ground (else it dips in -> always "blocked"/red)
                Ports = ports,
            };
        }
        public static readonly DeployableDef Splitter2 = MakeSplitter(9101, "2-Way Splitter", 0.55f, new[] { -0.14f, 0.14f });
        public static readonly DeployableDef Splitter3 = MakeSplitter(9102, "3-Way Splitter", 0.80f, new[] { -0.26f, 0f, 0.26f });
        public static readonly DeployableDef Splitter4 = MakeSplitter(9103, "4-Way Splitter", 1.05f, new[] { -0.36f, -0.12f, 0.12f, 0.36f });

        // --- Combiner (custom): the splitter's mirror -- N inputs (one per source, orange, on the back) feed ONE output
        //     (cyan, front) that re-exports their SUMMED wattage, and the downstream load splits back across the sources
        //     proportionally (see PowerSolver). Each input is a 0-watt relay Consumer; the output is a Passthrough. ---
        static DeployableDef MakeCombiner(ushort id, string name, float width, float[] inX)
        {
            var ports = new Port[inX.Length + 1];
            for (int i = 0; i < inX.Length; i++)
                ports[i] = new Port { Kind = PortKind.Consumer, Pos = new Vector3(inX[i], -0.18f, 0f), Watts = 0f };   // inputs, one per source, across the back face
            ports[inX.Length] = new Port { Kind = PortKind.Passthrough, Pos = new Vector3(0f, 0.18f, 0f), Watts = 0f };   // the single combined output, front face
            return new DeployableDef
            {
                Id = id, Name = name, ProcBox = true, PlaceSound = "metalplacement",
                Size = new Vector3(width, 0.36f, 0.5f),
                Offset = 0.7f, Radius = 0.35f, Range = 4f, Health = 200f, Fuel = 0f,   // same placement/clearance as the splitters
                Ports = ports,
            };
        }
        public static readonly DeployableDef Combiner2 = MakeCombiner(9104, "2-Way Combiner", 0.55f, new[] { -0.14f, 0.14f });

        // A procedural stand-in for a WALL barricade -- a thin metal plate that mounts flush + upright on a structure
        // wall, facing out (BarricadeMount.Wall). Real Unturned ships this as an ItemBarricadeAsset with a ripped mesh;
        // this is a placeholder box until that asset is extracted (id in the port's custom 91xx range, not a retail id).
        // NOT yet in All/ById -- a showcase + test def referenced directly; it joins the item-placement rail when
        // in-game barricade placement is wired (so it doesn't enter the net schema / catalog as an unobtainable item).
        public static readonly DeployableDef MetalBarricade = new()
        {
            Id = 9120, Name = "Metal Barricade", ProcBox = true, Mount = BarricadeMount.Wall, PlaceSound = "metalplacement",
            Size = new Vector3(1.6f, 0.08f, 1.6f),   // flat frame: X width, Y thickness (the facing axis), Z height -> a thin panel that stands up + faces out of the wall
            Offset = 0.05f, Radius = 0.6f, Range = 5f, Health = 300f, Fuel = 0f,
        };

        // The window barricade -- a planked panel that snaps INTO a building-editor window opening, one on each face
        // (inside + outside), sized to the opening at placement. Placeable ONLY when the reticle is on a window opening
        // (BarricadeMount.Window; BarricadePlacer.AimWindow UV-projects onto the wall plane since a hole has no collider).
        // Base Size is a nominal window; the placer scales it per-opening. id 9122 (9120/9121 taken). (master 2026-08-31)
        public static readonly DeployableDef WindowBarricade = new()
        {
            Id = 9122, Name = "Window Barricade", ProcBox = true, Mount = BarricadeMount.Window, PlaceSound = "metalplacement",
            Size = new Vector3(1.0f, 0.08f, 1.2f),   // flat frame: X width, Y thickness (facing axis), Z height -- scaled to fit each opening
            Offset = 0.06f, Radius = 0.5f, Range = 5f, Health = 200f, Fuel = 0f,
        };

        // --- Switch (custom): power in one side, out the other, gated by an F-toggle. A 0-watt relay Consumer (IN) + a
        //     Passthrough (OUT); PowerConducting = the toggle state, so OFF kills the passthrough = no downstream power.
        //     Remembers its state; a state light on top reads green (on) / red (off). ---
        public static readonly DeployableDef Switch = new()
        {
            Id = 9105, Name = "Power Switch", ProcBox = true, IsSwitch = true, PlaceSound = "metalplacement",
            Size = new Vector3(0.5f, 0.36f, 0.5f),   // same flat frame as the splitters (X width, Y depth port faces, Z stands up)
            Offset = 0.7f, Radius = 0.35f, Range = 4f, Health = 200f, Fuel = 0f,
            Ports = new[] {
                new Port { Kind = PortKind.Consumer,    Pos = new Vector3(0f, -0.18f, 0f), Watts = 0f },   // IN relay (back face)
                new Port { Kind = PortKind.Passthrough, Pos = new Vector3(0f,  0.18f, 0f), Watts = 0f },   // OUT (front) -- gated OFF by the switch
                new Port { Kind = PortKind.Consumer, Pos = new Vector3(-0.26f, 0f, 0f), Watts = 0f, Role = SwitchRole.TurnOn },   // LEFT side trigger: fed >=1w -> switch ON
                new Port { Kind = PortKind.Consumer, Pos = new Vector3( 0.26f, 0f, 0f), Watts = 0f, Role = SwitchRole.TurnOff },  // RIGHT side trigger: fed >=1w -> switch OFF
            },
        };

        // A3 (SP/MP-unify): the grid-power mains SOURCE bolted onto every Circuit_0 breaker box, promoted from
        // an SP-local IPowerDevice into a server-placed DEPLOYABLE-GRAPH fixture so it rides the existing
        // SystemDeployables replication (the mesh + collider are still drawn by WorldBuilder). A single 10kW
        // Output port, no HP/pickup/fuel, NOT player-placeable (no item id 9200 is ever grantable). The mains
        // toggle rides entity.ToggledOn (produce-while-on); the console toggleGlobalPower routes over the wire.
        public static readonly DeployableDef GridSource = new()
        {
            Id = 9200, Name = "Grid Power", Fixture = FixtureKind.GridSource,
            Size = new Vector3(1f, 0.58f, 1.87f),   // Circuit_0 AABB (cosmetic here -- the fixture node is a GridPowerSource, never a Deployable body)
            Offset = 0f, Radius = 0f, Range = 4f, Health = 0f, Fuel = 0f,   // a world fixture: no HP bar, no salvage/pickup, no fuel gauge
            Ports = new[] { new Port { Kind = PortKind.Output, Pos = GridPowerSource.PortLocal, Watts = GridPowerSource.DefaultWatts } },
        };

        // A2 (SP/MP-unify): the gas-station PUMP (the Gas_Pump_0 map object), promoted from an SP-local
        // IPowerDevice into a server-placed DEPLOYABLE-GRAPH fixture so it rides the existing SystemDeployables
        // replication (the mesh + collider are still drawn by WorldBuilder). A single 750 W Consumer port, no
        // HP/pickup/salvage, NOT player-placeable. FuelCapacity=0: the pump's entity.Fuel does NOT hold litres,
        // it carries a replicated 0..100 PERCENT of the shared 8000 L station tank (the absolute tank stays
        // server-side in GasStationServer -- entity.Fuel is 12int/2frac, can't hold 8000). Extract is the only
        // mutation, server-routed over CommandExtractFuel.
        public static readonly DeployableDef GasPump = new()
        {
            Id = 9201, Name = "Gas Pump", Fixture = FixtureKind.GasPump,
            Size = new Vector3(0.8f, 2.4f, 0.8f),   // standing Gas_Pump_0 AABB (cosmetic here -- the fixture node is a GasPump, never a Deployable body)
            Offset = 0f, Radius = 0f, Range = 4f, Health = 0f, Fuel = 0f,   // a world fixture: no HP bar, no salvage/pickup; Fuel scalar reused as the 0..100 station-fill percent
            // NB: fully-qualified so the CLASS UnturnedGodot.GasPump wins over this DeployableDef.GasPump field.
            Ports = new[] { new Port { Kind = PortKind.Consumer, Pos = UnturnedGodot.GasPump.PortLocal, Watts = UnturnedGodot.GasPump.Watts } },
        };

        // --- Battery (custom): a car battery you place + wire. The IN terminal (one end) CHARGES the stored Energy while
        //     powered; the OUT terminal (opposite end) DISCHARGES to whatever's wired to it while it has charge (produces
        //     up to its rating). Daisy-chain OUT->IN to pool capacity into a bigger reserve (master). Real Battery_0 model. ---
        public static readonly DeployableDef Battery = new()
        {
            Id = 1450, Name = "Vehicle Battery", Model = "Battery_0", MeshEuler = new Vector3(180f, 0f, 180f), PlaceSound = "metalplacement",   // item 1450 world mesh (extract_battery.py); MeshEuler flips it upright + 180 yaw (master)
            Size = new Vector3(0.5f, 0.3f, 0.28f), Offset = 0.5f, Radius = 0.24f, Range = 4f, Health = 200f, Fuel = 0f,
            IsBattery = true, EnergyMax = 600f * 3600f, ChargeWatts = 600f,   // 600 Wh (12V*50Ah); realistic ~600W (1C) sustained in/out (master) -> ~1h at full draw. Scale up via gen->splitter->batteries->combiners
            Ports = new[] {
                new Port { Kind = PortKind.Consumer, Pos = new Vector3(-0.2f, 0f, 0.05f), Watts = 600f },   // IN terminal (charge), one end (Pos is stood-up local: X=along, Y=height, Z=depth)
                new Port { Kind = PortKind.Output,   Pos = new Vector3( 0.2f, 0f, 0.05f), Watts = 600f },   // OUT terminal (discharge), opposite end — realistic 600W (master)
            },
        };

        // A wind turbine (custom): a procedural tower + nacelle + 3-blade hub. A SOURCE whose output ramps with the local
        // WIND (WindField noise sampled at its X/Z) x a height-above-sea multiplier; the blades spin ~ the wind. No fuel
        // or toggle -- always harvesting whatever wind is present.
        public static readonly DeployableDef WindTurbine = new()
        {
            Id = 9106, Name = "Wind Turbine", IsWindTurbine = true, Upright = true, PlaceSound = "metalplacement",
            Size = new Vector3(0.6f, 3.8f, 0.6f), Offset = 0.5f, Radius = 0.5f, Range = 5f, Health = 300f,
            Ports = new[] { new Port { Kind = PortKind.Output, Pos = new Vector3(0.16f, 0.12f, 0f), Watts = 2500f } },   // rated 2.5kW at full wind; the output CAP ramps 0..2x with wind x height (PowerScale)
        };

        // --- FLUID devices (strawberry 2026-07-22): placeable items that spawn FluidContainers, not power Deployables
        //     (Fluid marker set). The ghost is a gray box of Size; the real fluid mesh + HosePorts build on spawn. ---
        static DeployableDef MakeFluid(ushort id, string name, FluidRole role, System.Action<DeployableDef> tweak = null)
        {
            var d = new DeployableDef
            {
                Id = id, Name = name, ProcBox = true, PlaceSound = "metalplacement", Fluid = role,
                Size = new Vector3(1f, 1.4f, 1f), Offset = 0.7f, Radius = 0.5f, Range = 4.5f, Health = 200f, Fuel = 0f,
            };
            tweak?.Invoke(d);
            return d;
        }
        public static readonly DeployableDef FluidTank    = MakeFluid(9110, "Fluid Tank",    FluidRole.Storage);   // empty; adopts what's piped in
        public static readonly DeployableDef WaterSource  = MakeFluid(9111, "Fluid Water Source",  FluidRole.Source, d => { d.FluidType = FluidType.Water; d.FluidCapacity = 200000f; d.FluidQuality = WaterQuality.Tainted; });   // a filled water reservoir (200 L) -- bulk natural water = TAINTED (purify or bottle for clean)
        public static readonly DeployableDef FluidSplitter = MakeFluid(9112, "Fluid Splitter", FluidRole.Splitter);
        public static readonly DeployableDef FluidCombiner = MakeFluid(9113, "Fluid Combiner", FluidRole.Combiner);
        public static readonly DeployableDef FluidPumpDef  = MakeFluid(9114, "Fluid Pump",     FluidRole.Pump);      // draws power; head lift
        public static readonly DeployableDef FluidValve    = MakeFluid(9115, "Fluid Valve",    FluidRole.Valve);
        public static readonly DeployableDef Refinery      = MakeFluid(9116, "Fluid Refinery",       FluidRole.Transformer, d => { d.FluidType = FluidType.Oil;   d.FluidOut = FluidType.Gas; });        // oil -> gas
        public static readonly DeployableDef Sluice        = MakeFluid(9117, "Fluid Sluice",         FluidRole.Transformer, d => { d.FluidType = FluidType.Water; d.FluidOut = FluidType.Water; d.FluidDirties = true; });   // runs water through -> DIRTY-flagged water (not its own type anymore)
        public static readonly DeployableDef Purifier      = MakeFluid(9121, "Fluid Purifier",       FluidRole.Transformer, d => { d.FluidType = FluidType.Water; d.FluidOut = FluidType.Water; d.FluidPurifies = true; });   // tainted/dirty water + POWER -> clean water (dead without power)
        // Submersible INLET (9119): infinite Water source with NO head -> must be PUMPED. Placeable ONLY submerged in a
        // 0.6-5 m water-depth band. OUTLET (9120): a drain (Consumer) that deletes whatever's piped in; placeable anywhere.
        public static readonly DeployableDef WaterInlet    = MakeFluid(9119, "Fluid Inlet", FluidRole.Source, d => { d.FluidType = FluidType.Water; d.FluidInfinite = true; d.FluidNoHead = true; d.FluidCapacity = 1000f; d.FluidQuality = WaterQuality.Tainted; d.WaterDepthMin = 0.6f; d.WaterDepthMax = 5f; });   // river/ocean water = TAINTED
        public static readonly DeployableDef WaterOutlet   = MakeFluid(9120, "Fluid Drain",      FluidRole.Consumer);

        // A powered storage container (strawberry): places like any power deployable (an IPowerDevice, NOT a Fluid
        // marker), but IsStorage routes it through FridgeDeploy into a Refrigerator (StorageCrate subclass) instead of
        // a plain Deployable body. Its single Consumer port must be wired + powered for Refrigerator.Preserves to hold
        // (its Storage contents skip the daily food-spoilage sweep); unpowered/unwired it spoils like any crate.
        public static readonly DeployableDef Refrigerator = new()
        {
            Id = 9130, Name = "Refrigerator", IsStorage = true, ProcBox = true, PlaceSound = "metalplacement",
            Size = new Vector3(0.75f, 0.75f, 1.75f), Offset = 0.9f, Radius = 0.5f, Range = 4.5f, Health = 300f,
            // NB: fully-qualified so the CLASS UnturnedGodot.Refrigerator wins over this DeployableDef.Refrigerator field.
            Ports = new[] { new Port { Kind = PortKind.Consumer, Pos = new Vector3(0f, 0.25f, -0.36f), Watts = UnturnedGodot.Refrigerator.Watts } },
        };

        // IDS ARE 9160+, NOT 9140+. 9140-9143 are ALREADY the Augewehr / Nightraider / .300 Blackout /
        // Heartbreaker magazines (see AttachmentFit, which carries the same warning for the same reason:
        // "the later Add() calls silently overwrote the magazines registered under them"). Deployable defs
        // and inventory items share one id space, so a door on 9140 shadows a magazine and neither errors.
        // In use when this block was written: 9101-9106, 9110-9121, 9130, 9140-9143, 9200-9201.
        // ---- WOODEN BARRICADE DOORS -------------------------------------------------------------------
        // strawberry_cow 2026-08-09: "im gonna have u working on functional doors ... use the prop doors we
        // have and give them functionality ... i want doors to open 90 degrees."
        //
        // One def per ripped prop, built from a table rather than twelve hand-written blocks -- the only thing
        // that differs between a Birch and a Pine door is the mesh name, and three near-identical literals is
        // how one of them ends up with a stale Size nobody notices.
        //
        // DoorProp routes placement to DoorDeploy (hinge from the catalog, swing from ObjectDoor). Doubledoor
        // is deliberately ABSENT: its rip is two hinges against a single mesh and the panel split is not
        // written yet, so a def for it would place a door that swings two copies of itself.
        static DeployableDef WoodDoor(ushort id, string form, string wood, Vector3 size, float health) => new()
        {
            Id = id, Name = $"{wood} {form}", DoorProp = $"{form}_{wood}", Model = $"{form}_{wood}",
            Size = size, Offset = 0f, Radius = 0.5f, Range = 4.5f, Health = health,
            PlaceSound = "woodplacement",
        };

        static readonly Vector3 DoorSize = new(1.2f, 0.15f, 2.4f);    // leaf footprint, flat-frame (Z stands up)
        static readonly Vector3 GateSize = new(4.0f, 0.15f, 3.0f);    // garage door: wide, tilts up about X
        static readonly Vector3 HatchSize = new(1.6f, 0.15f, 1.6f);   // floor hatch

        public static readonly DeployableDef DoorBirch = WoodDoor(9160, "Door", "Birch", DoorSize, 250f);
        public static readonly DeployableDef DoorMaple = WoodDoor(9161, "Door", "Maple", DoorSize, 300f);
        public static readonly DeployableDef DoorPine  = WoodDoor(9162, "Door", "Pine",  DoorSize, 275f);
        public static readonly DeployableDef GateBirch = WoodDoor(9163, "Gate", "Birch", GateSize, 350f);
        public static readonly DeployableDef GateMaple = WoodDoor(9164, "Gate", "Maple", GateSize, 400f);
        public static readonly DeployableDef GatePine  = WoodDoor(9165, "Gate", "Pine",  GateSize, 375f);
        public static readonly DeployableDef HatchBirch = WoodDoor(9166, "Hatch", "Birch", HatchSize, 250f);
        public static readonly DeployableDef HatchMaple = WoodDoor(9167, "Hatch", "Maple", HatchSize, 300f);
        public static readonly DeployableDef HatchPine  = WoodDoor(9168, "Hatch", "Pine",  HatchSize, 275f);

        // METAL, and it cost four lines because the hinge lookup keys on the FORM rather than the material:
        // Door_Metal resolves the same "Door" row Door_Pine does. cow tools diffed the rigs before extracting
        // rather than assuming the twins matched -- they came back byte-identical in both geometry and hinge,
        // differing only in palette -- so there is no anim row and no code here, just the defs.
        public static readonly DeployableDef DoorMetal  = WoodDoor(9169, "Door", "Metal", DoorSize, 500f);
        public static readonly DeployableDef GateMetal  = WoodDoor(9170, "Gate", "Metal", GateSize, 700f);
        public static readonly DeployableDef HatchMetal = WoodDoor(9171, "Hatch", "Metal", HatchSize, 500f);

        public static readonly DeployableDef[] WoodDoors =
            { DoorBirch, DoorMaple, DoorPine, GateBirch, GateMaple, GatePine, HatchBirch, HatchMaple, HatchPine,
              DoorMetal, GateMetal, HatchMetal };

        // Merge (SP/MP-unify -> main): union of both sides' devices. main's Battery/Switch/WindTurbine +
        // the unification's GridSource/GasPump fixtures. Switch is defined above (auto-merged from main).
        // SOURCE-EXACT from retail Landmine.dat (Bundles/Items/Barricades/Landmine) -- read directly; .dat is text, the
        // AssetRipper (down) only gates the MESH. id 1101, Trap, Explosive. Range2=8 blast; Player/Zombie/Vehicle dmg
        // 91/175/175; Health 1 = a shot/blast destroys it -> it DETONATES (Vulnerable Explosive), handled in TakeDamage.
        // Fuller src damages (Animal 175, Barricade/Structure 75, Resource 625, Object 100) await extending Explode past
        // zombie/player/vehicle. TrapTrigger 1.4 approximates the src barricade-contact trigger; arm grace is a QoL (the
        // .dat has no setup delay). ProcBox placeholder until the real mesh (AssetRipper down).
        public static readonly DeployableDef Landmine = new()
        {
            Id = 1101, Name = "Landmine", Model = "Landmine_0",   // real world mesh ripped from core.masterbundle (tools/extract_trap_meshes.py)
            Size = new Vector3(1f, 1f, 0.35f), Offset = 0.075f, Radius = 0.05f, Range = 4f, Health = 1f,
            IsTrap = true, TrapTrigger = 1.4f, TrapBlast = 8f, TrapZombieDamage = 175f, TrapPlayerDamage = 91f, TrapVehicleDamage = 175f,
            ShatterOnDeath = true, PlaceSound = "metalplacement",
        };

        // src Spikes_Pine.dat: id 385, Type Trap, Build Spike, Rarity Rare. A CONTACT hazard (NOT explosive): whatever
        // ENTERS its footprint gets shredded (zombie 60 / player 30 / animal 60) and the spike WEARS 5 HP/hit (Health 40
        // -> ~8 hits) then breaks apart -- Vulnerable + Unrepairable. Retail PvP-gates player damage & ignores riders in a
        // vehicle; the port (like the landmine) hurts whoever steps on it. Animal damage deferred (no animal-trap target
        // wired yet, src 60). ProcBox placeholder until the real Spikes mesh (AssetRipper down). Wood variant = Pine.
        public static readonly DeployableDef Spike = new()
        {
            Id = 385, Name = "Wooden Spikes", Model = "Spikes_0",   // real spikes_pine mesh ripped from core.masterbundle (tools/extract_trap_meshes.py)
            Size = new Vector3(1f, 2f, 0.35f), Offset = 0.25f, Radius = 0.2f, Range = 4f, Health = 40f,
            IsTrap = true, TrapExplosive = false, TrapTrigger = 1.1f, TrapArmDelay = 0.25f,
            TrapZombieDamage = 60f, TrapPlayerDamage = 30f, TrapAnimalDamage = 60f, TrapWearPerHit = 5f, TrapCooldown = 0f,
            PlaceSound = "woodplacement",
        };

        // src Charge.dat: id 1241, Type Charge, Build Charge, Rarity Epic. A REMOTE explosive (base-raiding): placed INERT
        // -- NO proximity/contact trigger (TrapManual). It blows only when a Detonator fires it (Deployable.DetonateManual)
        // or it's shot (Health 1 Vulnerable -> TakeDamage detonates it, like the landmine). HUGE blast -- Range2 8, Player/
        // Zombie 200, Vehicle 500, Structure 1000 (TrapStructureDamage), Animal 200. Src Proof_Explosion (a charge resists
        // other blasts so a stack doesn't chain early) is already honoured: DetonateTrap's deployable-damage loop SKIPS
        // traps. ProcBox placeholder until the real Charge mesh (AssetRipper down). NOTE: the DETONATOR item (equip + plunge
        // to fire your charges) is the paired next increment; today a charge is triggered by DetonateAllCharges / a shot.
        public static readonly DeployableDef Charge = new()
        {
            Id = 1241, Name = "Remote Explosive", Model = "Charge_0",   // real charge Model_0 mesh ripped from core.masterbundle (tools/extract_trap_meshes.py)
            Size = new Vector3(1f, 1f, 0.325f), Offset = 0.05f, Radius = 0.05f, Range = 4f, Health = 1f,
            IsTrap = true, TrapManual = true, TrapBlast = 8f, TrapZombieDamage = 200f, TrapPlayerDamage = 200f, TrapVehicleDamage = 500f,
            TrapAnimalDamage = 200f, TrapStructureDamage = 1000f,
            ShatterOnDeath = true, ExplosionProof = true, PlaceSound = "metalplacement",
        };

        // src Barbedwire.dat: id 386, Type Trap, Build Wire, Rarity Uncommon. A CONTACT hazard like the spike (non-explosive)
        // -- shreds whoever ENTERS (zombie 80 / player 40 / animal 80) and WEARS 5 HP/hit (Health 70 -> ~14 hits) then breaks.
        // Bigger + tougher than wooden spikes; the iconic base-defense wire. Real ripped mesh (tools/extract_trap_meshes.py).
        public static readonly DeployableDef Barbedwire = new()
        {
            Id = 386, Name = "Barbed Wire", Model = "Barbedwire_0",
            Size = new Vector3(2f, 1f, 0.2f), Offset = 0.2f, Radius = 0.15f, Range = 4f, Health = 70f,
            IsTrap = true, TrapExplosive = false, TrapTrigger = 1.1f, TrapArmDelay = 0.25f,
            TrapZombieDamage = 80f, TrapPlayerDamage = 40f, TrapAnimalDamage = 80f, TrapWearPerHit = 5f, TrapCooldown = 0f,
            PlaceSound = "metalplacement",
        };

        // CRAFTING STATIONS (strawberry): placed barricades that grant crafting tags within CraftingRange + LOS.
        // Real world meshes ripped by tools/extract_station_meshes.py (LOD0, like Generator_0); the tag GUIDs +
        // ranges are from the src barricade .dat (PlaceableProvidesCraftingTags + Range). Campfire has no explicit
        // tag field (src "Build Campfire" is hardcoded) -> mapped to the Heat tag the ovens/kiln also grant.
        static DeployableDef Station(ushort id, string name, string model, float craftRange, params string[] tags) => new()
        {
            Id = id, Name = name, Model = model, PlaceSound = "metalplacement",
            Offset = 0f, Radius = 1.0f, Range = 6f, Health = 400f,
            MeshEuler = new Vector3(180f, 0f, 180f),   // ripped like the Battery: stands up upside-down + 180 off -> same fixup
            CraftingTags = tags, CraftingRange = craftRange,
        };
        public static readonly DeployableDef Workbench     = Station(1916, "Workbench",      "Workbench_0",     4f, "7b82c125a5a54984b8bb26576b59e977");   // Workbench (269 recipes)
        // Campfire is a ground FIRE PIT, not a stand-up barricade: skip StandRotX (Upright) + lay the mesh flat.
        public static readonly DeployableDef Campfire = new()
        {
            Id = 362, Name = "Campfire", Model = "Campfire_0", PlaceSound = "metalplacement",
            Offset = 0f, Radius = 1.0f, Range = 6f, Health = 400f, Upright = true,
            MeshEuler = new Vector3(-90f, 0f, 0f),   // the barricade.prefab mesh stands vertical the OTHER way -> tip flat (was +90 for the old item mesh; that pointed it DOWN)
            CraftingTags = new[] { "20f30322bbcc4b01a4f116d22b24c21a" }, CraftingRange = 4f,   // Heat (src has no explicit tag)
        };
        public static readonly DeployableDef ChemistryLab  = Station(1920, "Chemistry Lab",  "ChemistryLab_0",  4f, "99896da563a748148460c67b9962874f");   // ChemicalMixing (13)
        public static readonly DeployableDef Kiln          = Station(1927, "Kiln",           "Kiln_0",          5f, "20f30322bbcc4b01a4f116d22b24c21a", "192e071c94d1419b991a430d42fe2be3");
        public static readonly DeployableDef Loom          = Station(1923, "Loom",           "Loom_0",          4f, "2ac5ddc545a848008c0308d21f5d2e6b");   // Sewing (270)
        public static readonly DeployableDef OvenBrick     = Station(1919, "Brick Oven",     "Oven_Brick_0",    4f, "20f30322bbcc4b01a4f116d22b24c21a", "d2cc65b749e5477f95103601df89cdbc");
        public static readonly DeployableDef OvenElectric  = Station(1250, "Electric Oven",  "Oven_Electric_0", 4f, "20f30322bbcc4b01a4f116d22b24c21a", "d2cc65b749e5477f95103601df89cdbc");
        public static readonly DeployableDef SewingTable   = Station(1924, "Sewing Table",   "SewingTable_0",   4f, "2ac5ddc545a848008c0308d21f5d2e6b");   // Sewing
        public static readonly DeployableDef SpinningWheel = Station(1922, "Spinning Wheel", "SpinningWheel_0", 4f, "2ac5ddc545a848008c0308d21f5d2e6b");   // Sewing

        public static readonly DeployableDef[] All = { Generator, Spotlight, Splitter2, Splitter3, Splitter4, Combiner2, Battery, Switch, WindTurbine, GridSource, GasPump,
            FluidTank, WaterSource, FluidSplitter, FluidCombiner, FluidPumpDef, FluidValve, Refinery, Sluice, WaterInlet, WaterOutlet, Purifier, Refrigerator, Landmine, Spike, Charge, Barbedwire,
            DoorBirch, DoorMaple, DoorPine, GateBirch, GateMaple, GatePine, HatchBirch, HatchMaple, HatchPine,
            DoorMetal, GateMetal, HatchMetal, Workbench, Campfire, ChemistryLab, Kiln, Loom, OvenBrick, OvenElectric, SewingTable, SpinningWheel, WindowBarricade };
        public static DeployableDef ById(ushort id) => id switch
        {
            1101 => Landmine,
            385 => Spike,
            1241 => Charge,
            386 => Barbedwire,
            458 => Generator,
            459 => Spotlight,
            1916 => Workbench,
            362 => Campfire,
            1250 => OvenElectric,
            1919 => OvenBrick,
            1920 => ChemistryLab,
            1922 => SpinningWheel,
            1923 => Loom,
            1924 => SewingTable,
            1927 => Kiln,
            9169 => DoorMetal,
            9170 => GateMetal,
            9171 => HatchMetal,
            9160 => DoorBirch,
            9161 => DoorMaple,
            9162 => DoorPine,
            9163 => GateBirch,
            9164 => GateMaple,
            9165 => GatePine,
            9166 => HatchBirch,
            9167 => HatchMaple,
            9168 => HatchPine,
            9101 => Splitter2,
            9102 => Splitter3,
            9103 => Splitter4,
            9104 => Combiner2,
            9105 => Switch,
            1450 => Battery,
            9106 => WindTurbine,
            9110 => FluidTank,
            9111 => WaterSource,
            9112 => FluidSplitter,
            9113 => FluidCombiner,
            9114 => FluidPumpDef,
            9115 => FluidValve,
            9116 => Refinery,
            9117 => Sluice,
            9119 => WaterInlet,
            9120 => WaterOutlet,
            9121 => Purifier,
            9122 => WindowBarricade,
            9130 => Refrigerator,
            9200 => GridSource,
            9201 => GasPump,
            _ => null,
        };

        // The mesh + a nearest-filtered palette material (the src uses tiny 2x2 palette textures sampled by UV,
        // like the vehicles/barn). Shared by the ghost, the held viewmodel, and the placed object.
        public Mesh LoadMesh()
        {
            string dir = ProjectSettings.GlobalizePath("res://content/objects/");
            return ObjMesh.Load(dir + Model + ".obj");
        }

        public StandardMaterial3D MakeMaterial()
        {
            var mat = new StandardMaterial3D { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            if (ProcBox) { mat.AlbedoColor = new Color(0.42f, 0.43f, 0.45f); mat.Metallic = 0.15f; mat.Roughness = 0.7f; return mat; }   // plain gray junction box
            string tp = ProjectSettings.GlobalizePath($"res://content/objects/{Model}_tex.png");
            if (System.IO.File.Exists(tp))
            {
                var img = new Image();
                if (img.Load(tp) == Error.Ok)
                {
                    mat.AlbedoTexture = ImageTexture.CreateFromImage(img);
                    mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;   // crisp 2x2 palette cells
                }
            }
            return mat;
        }

        // The world rotation the src applies: yaw about world-up, then the -90 X stand-up
        // (BarricadeManager.getRotation: Quaternion.Euler(0,yaw,0) * Quaternion.Euler(-90,0,0)).
        public static Basis StandBasis(float yawDeg) =>
            new Basis(Vector3.Up, Mathf.DegToRad(yawDeg)) * new Basis(Vector3.Right, Mathf.DegToRad(StandRotX));

        // Per-def model orientation fixup applied to the MESH itself (Vector3.Zero = none, the common case). The
        // battery's ripped world mesh stands up UPSIDE-DOWN + 180 off (master), so it carries a correction here.
        // UG_BATROT="x,y,z" (deg) overrides at runtime for tuning the battery; otherwise the def's MeshEuler.
        public Vector3 MeshEuler;
        public Basis MeshBasis()
        {
            Vector3 e = MeshEuler;
            string env = System.Environment.GetEnvironmentVariable("UG_BATROT");
            if (Id == 1450 && env != null)
            {
                var p = env.Split(',');
                if (p.Length == 3 && float.TryParse(p[0], out float x) && float.TryParse(p[1], out float y) && float.TryParse(p[2], out float z))
                    e = new Vector3(x, y, z);
            }
            return e == Vector3.Zero ? Basis.Identity
                : Basis.FromEuler(new Vector3(Mathf.DegToRad(e.X), Mathf.DegToRad(e.Y), Mathf.DegToRad(e.Z)));
        }

        // How far to lift the model origin so the STANDING mesh's base sits exactly on the surface point.
        // (Yaw about world-up doesn't change the vertical extent, so only the fixed X stand-up matters.) This
        // decouples ground contact from the src's authored Offset, which assumed Unity's orientation.
        public static float GroundLift(Aabb localAabb)
        {
            var b = new Basis(Vector3.Right, Mathf.DegToRad(StandRotX));
            float minY = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = localAabb.Position + localAabb.Size * new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
                minY = Mathf.Min(minY, (b * corner).Y);
            }
            return -minY;
        }
    }
}
