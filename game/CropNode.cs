using Godot;
using System.IO;
using SDG.Unturned;

namespace UnturnedGodot
{
    // A planted farm crop in the WORLD: a dirt base (Model_0) + a growth-stage billboard
    // (Foliage_0 young / Foliage_1 grown) swapped by IsFullyGrown -- matching InteractableFarm.SetModelGrown,
    // which just toggles the Foliage_0/Foliage_1 child GameObjects. Meshes/textures from tools/extract_crop.py
    // + extract_crop_tex.py (content/crop_<name>_*). Growth logic = SDG.Unturned.PlantedCrop (increment 1).
    public partial class CropNode : Node3D
    {
        MeshInstance3D _young, _grown;
        bool _lastGrown;
        public PlantedCrop Crop;        // the growth-logic instance (Def + PlantedAt); null in the render test
        public string CropName;

        // The Model_0 dirt base lies FLAT on the ground as-extracted (no rotation), but the Foliage_0/Foliage_1
        // billboards are authored lying down and need standing UP = -90 deg about X (verified by the croptest A/B:
        // dirt is correct at rot 0, foliage needs +/-90). UG_FOLIROT tunes the foliage stand-up angle.
        static float FoliRot => float.TryParse(System.Environment.GetEnvironmentVariable("UG_FOLIROT"), out var r) ? r : -90f;

        public static CropNode Spawn(string cropName, Color dirtColor)
        {
            var n = new CropNode { CropName = cropName };
            string dir = ProjectSettings.GlobalizePath("res://content/");
            var baseMesh = ObjMesh.Load(dir + $"crop_{cropName}_Model_0.txt");
            if (baseMesh != null)
                n.AddChild(new MeshInstance3D { Mesh = baseMesh, MaterialOverride = new StandardMaterial3D {
                    AlbedoColor = dirtColor, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled } });
            n._young = MakeFoliage(dir, cropName, "Foliage_0");
            n._grown = MakeFoliage(dir, cropName, "Foliage_1");
            var foliRot = new Vector3(FoliRot, 0f, 0f);   // stand the plant billboards up; the dirt base stays flat
            if (n._young != null) { n._young.RotationDegrees = foliRot; n.AddChild(n._young); }
            if (n._grown != null) { n._grown.RotationDegrees = foliRot; n.AddChild(n._grown); }
            n.SetGrown(false);
            return n;
        }

        // A foliage billboard: real 16x16 plant sprite, AlphaScissor (alpha holds the plant shape), double-sided.
        static MeshInstance3D MakeFoliage(string dir, string cropName, string stage)
        {
            var mesh = ObjMesh.Load(dir + $"crop_{cropName}_{stage}.txt");
            if (mesh == null) return null;
            var mat = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                AlphaScissorThreshold = 0.4f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Roughness = 1f,
            };
            string tex = dir + $"crop_{cropName}_{stage}.png";
            if (File.Exists(tex))
            {
                var img = new Image();
                if (img.Load(tex) == Error.Ok)
                {
                    img.GenerateMipmaps();
                    mat.AlbedoTexture = ImageTexture.CreateFromImage(img);
                    mat.TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
                }
            }
            return new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
        }

        public void SetGrown(bool grown)
        {
            _lastGrown = grown;
            if (_young != null) _young.Visible = !grown;
            if (_grown != null) _grown.Visible = grown;
        }

        // Flip the model the moment the crop matures (source InteractableFarm.SetModelGrown). Call each frame.
        public void UpdateGrowth(double now)
        {
            if (Crop == null) return;
            bool g = Crop.IsFullyGrown(now);
            if (g != _lastGrown) SetGrown(g);
        }
    }
}
