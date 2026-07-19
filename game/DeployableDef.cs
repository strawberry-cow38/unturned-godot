using Godot;

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
        public string PlaceSound;  // src .dat PlacementAudioClip stem (content/sounds/<stem>.wav) played when planted; null = silent
        public string HoldMesh, HoldAlbedo;   // content/<mesh>.obj + palette for the 1st-person carry model (item.prefab); null -> EmptyHands fallback (ghost only)
        public bool ShatterOnDeath;   // true -> explodes into flying debris + vanishes (no salvageable husk, drops nothing); false -> charred blowtorch-salvageable wreck
        public bool ProcBox;          // true -> a plain gray BoxMesh of Size (no .obj/palette); the custom splitters use it
        // barricades are authored lying flat -> a +90 X stands them up. (The src uses -90 in Unity's left-handed
        // space; our rip negates Z into Godot's right-handed space, which flips the sense to +90.)
        public static float StandRotX = float.TryParse(System.Environment.GetEnvironmentVariable("UG_DEPLOYROT"), out var r) ? r : 90f;

        // --- power connection points (nodes). A wire runs OUTPUT -> ... -> CONSUMER; a CONSUMER may also have a
        //     PASSTHROUGH that re-exports (input - usage). Pos is in the flat authored mesh frame (stands up with the model). ---
        public enum PortKind { Output, Consumer, Passthrough }
        public struct Port { public PortKind Kind; public Vector3 Pos; public float Watts; }   // Output.Watts = produced (when source on); Consumer.Watts = drawn; Passthrough.Watts unused (= input - consumers)
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
            Size = new Vector3(2f, 2f, 0.5f), Offset = 0.75f, Radius = 0.5f, Range = 4f, Health = 450f, Fuel = 2000f,   // src Generator_Small.dat Capacity 2000
            Ports = new[] { new Port { Kind = PortKind.Output, Pos = new Vector3(0.4f, 0.6f, 0.05f), Watts = 4000f } },   // output on the gray-face mid-right (flat frame; tuned visually)
        };
        // src Spotlight.dat: id 459, Useable Barricade, Build Spot, footprint 2x2x0.55, Offset 1.12
        public static readonly DeployableDef Spotlight = new()
        {
            Id = 459, Name = "Spotlight", Model = "Spotlight_deploy", PlaceSound = "metalplacement",   // src Spotlight.dat PlacementAudioClip Sounds/MetalPlacement.mp3
            Size = new Vector3(2f, 2f, 0.55f), Offset = 1.12f, Radius = 0.5f, Range = 4f, Health = 300f, ShatterOnDeath = true,   // shatters into pieces, no husk/salvage (strawberry)
            Ports = new[] {   // consumer on the back of the central pillar, passthrough on the front (flat frame; tuned visually)
                new Port { Kind = PortKind.Consumer, Pos = new Vector3(0f, -0.35f, 0f), Watts = 250f },
                new Port { Kind = PortKind.Passthrough, Pos = new Vector3(0f, 0.35f, 0f), Watts = 0f },
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

        public static readonly DeployableDef[] All = { Generator, Spotlight, Splitter2, Splitter3, Splitter4, Combiner2 };
        public static DeployableDef ById(ushort id) => id switch
        {
            458 => Generator,
            459 => Spotlight,
            9101 => Splitter2,
            9102 => Splitter3,
            9103 => Splitter4,
            9104 => Combiner2,
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
