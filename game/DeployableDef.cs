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
        public string HoldMesh, HoldAlbedo;   // content/<mesh>.obj + palette for the 1st-person carry model (item.prefab); null -> EmptyHands fallback (ghost only)
        // barricades are authored lying flat -> a +90 X stands them up. (The src uses -90 in Unity's left-handed
        // space; our rip negates Z into Godot's right-handed space, which flips the sense to +90.)
        public static float StandRotX = float.TryParse(System.Environment.GetEnvironmentVariable("UG_DEPLOYROT"), out var r) ? r : 90f;

        // src Generator_Small.dat: id 458, Useable Barricade, Build Generator, footprint 2x2x0.5, Offset 0.75
        public static readonly DeployableDef Generator = new()
        {
            Id = 458, Name = "Generator", Model = "Generator_0",
            HoldMesh = "generator_hold.obj", HoldAlbedo = "generator_hold_tex.png",
            Size = new Vector3(2f, 2f, 0.5f), Offset = 0.75f, Radius = 0.5f, Range = 4f, Health = 450f,
        };
        // src Spotlight.dat: id 459, Useable Barricade, Build Spot, footprint 2x2x0.55, Offset 1.12
        public static readonly DeployableDef Spotlight = new()
        {
            Id = 459, Name = "Spotlight", Model = "Spotlight_deploy",
            Size = new Vector3(2f, 2f, 0.55f), Offset = 1.12f, Radius = 0.5f, Range = 4f, Health = 300f,
        };

        public static readonly DeployableDef[] All = { Generator, Spotlight };
        public static DeployableDef ById(ushort id) => id == 458 ? Generator : id == 459 ? Spotlight : null;

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
