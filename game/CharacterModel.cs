using Godot;

namespace UnturnedGodot
{
    // The REAL ripped Unturned character mesh (Model_0_84), bundled at res://content/character.txt as raw
    // .obj text so it packs into the export (Godot would import a .obj and drop the raw file). Loaded once +
    // shared; bind-pose (T-pose) skinned geometry, scaled to ~1.8 m, feet at y=0. Build(tint) tints per team
    // (players blue/orange, zombies green). Double-sided since the skinned mesh winds opposite the props.
    // (Skin texture + skeleton animation are the next layers.)
    public static class CharacterModel
    {
        static Mesh _mesh;
        static float _scale = 1f, _footOffset = 0f;
        public static bool Loaded => _mesh != null;

        public static void LoadBundled(string resPath = "res://content/character.txt")
        {
            if (_mesh != null) return;
            _mesh = ContentProvider.ParseObj(resPath);
            if (_mesh == null) { GD.PushError($"[CharacterModel] {resPath} not found"); return; }
            var aabb = _mesh.GetAabb();
            _scale = 1.8f / Mathf.Max(0.05f, aabb.Size.Y);
            _footOffset = -aabb.Position.Y * _scale;
            GD.Print($"[CharacterModel] bundled character loaded (scale {_scale:F3})");
        }

        public static Node3D Build(Color tint)
        {
            var root = new Node3D();
            if (_mesh == null) return root;
            var mat = new StandardMaterial3D { AlbedoColor = tint, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            var mi = new MeshInstance3D
            {
                Mesh = _mesh,
                MaterialOverride = mat,
                Scale = new Vector3(_scale, _scale, _scale),
                Position = new Vector3(0f, _footOffset, 0f),
            };
            root.AddChild(mi);
            return root;
        }
    }
}
