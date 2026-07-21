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
        public bool Upright;          // build the mesh already-vertical (skip the flat->stand-up rotation) -- for procedural models like the turbine
        public string PlaceSound;  // src .dat PlacementAudioClip stem (content/sounds/<stem>.wav) played when planted; null = silent
        public string HoldMesh, HoldAlbedo;   // content/<mesh>.obj + palette for the 1st-person carry model (item.prefab); null -> EmptyHands fallback (ghost only)
        public bool ShatterOnDeath;   // true -> explodes into flying debris + vanishes (no salvageable husk, drops nothing); false -> charred blowtorch-salvageable wreck
        public bool ProcBox;          // true -> a plain gray BoxMesh of Size (no .obj/palette); the custom splitters use it
        public FixtureKind Fixture = FixtureKind.None;   // A3/A2: a server-placed WORLD fixture (GridSource mains / GasPump) vs a normal player-placeable deployable. Bridged to DeployableNetDef.FixtureKind in DeployableNetSchema.
        // barricades are authored lying flat -> a +90 X stands them up. (The src uses -90 in Unity's left-handed
        // space; our rip negates Z into Godot's right-handed space, which flips the sense to +90.)
        public static float StandRotX = float.TryParse(System.Environment.GetEnvironmentVariable("UG_DEPLOYROT"), out var r) ? r : 90f;
        // generator fuel drained per SECOND at FULL load while running (master: realistic, not PZ's "days on 20L"). Scaled
        // by load (idle ~20%). 60-unit tank: ~25min real at full load, ~2h idle. Tunable via UG_GENBURN.
        public static float GenFuelBurnPerSec = float.TryParse(System.Environment.GetEnvironmentVariable("UG_GENBURN"), out var gb) ? gb : 0.04f;

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
            Size = new Vector3(2f, 2f, 0.5f), Offset = 0.75f, Radius = 0.5f, Range = 4f, Health = 450f, Fuel = 60f,   // PZ-scale fuel (master): ~7 portable cans; burned by LOAD while running (GenFuelBurnPerSec). src Capacity was 2000
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
        // toggle rides entity.ToggledOn (produce-while-on); the F1 toggleGlobalPower routes over the wire.
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

        // Oil pump / derrick (source InteractableOil, id1219): a placed FIXTURE that regenerates a server-owned fuel
        // reservoir you siphon from. Fixture=OilPump -> the client materializes a VIEW-ONLY UnturnedGodot.OilPump node
        // that mirrors entity.Fuel (the server runs regen/siphon). Health 450 (shootable), Fuel = Fuel_Capacity 2500.
        public static readonly DeployableDef OilPump = new()
        {
            Id = 1219, Name = "Oil Pump", Fixture = FixtureKind.OilPump,
            // ProcBox: the placement GHOST is a Size-box (the real pump-jack has no .obj Model -- its visual is built
            // procedurally in OilPump.Materialize). Without this, BuildMesh -> LoadMesh() returns null -> an INVISIBLE
            // ghost, so equipping it from the bag looked like "not equippable" (you saw nothing to place). The PLACED
            // result is unaffected (it routes through RequestPlaceDeployable -> the fixture's Materialize, not BuildMesh).
            ProcBox = true,
            Size = new Vector3(1.1f, 1.7f, 1.1f), Offset = 0f, Radius = 0.5f, Range = 4f, Health = 450f, Fuel = 2500f,
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

        // Merge (SP/MP-unify -> main): union of both sides' devices. main's Battery/Switch/WindTurbine +
        // the unification's GridSource/GasPump fixtures. Switch is defined above (auto-merged from main).
        public static readonly DeployableDef[] All = { Generator, Spotlight, Splitter2, Splitter3, Splitter4, Combiner2, Battery, Switch, WindTurbine, GridSource, GasPump, OilPump };
        public static DeployableDef ById(ushort id) => id switch
        {
            1219 => OilPump,
            458 => Generator,
            459 => Spotlight,
            9101 => Splitter2,
            9102 => Splitter3,
            9103 => Splitter4,
            9104 => Combiner2,
            9105 => Switch,
            1450 => Battery,
            9106 => WindTurbine,
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
