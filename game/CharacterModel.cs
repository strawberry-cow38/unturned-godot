using Godot;

namespace UnturnedGodot
{
    // The REAL ripped Unturned character mesh (Model_0_84, the Character body), loaded once via the
    // ContentProvider and shared. It's the bind-pose (T-pose) skinned geometry -- unlocked by the
    // multi-stream decoder. Scaled to ~1.8 m, feet at y=0. Build(tint) makes one instance with a tint over
    // the skin texture so players (blue/orange) and zombies (green) read apart. (Skeleton animation = next.)
    public static class CharacterModel
    {
        static Mesh _mesh;
        static Texture2D _tex;
        static float _scale = 1f, _footOffset = 0f;
        public static bool Loaded => _mesh != null;

        public static void Load(ContentProvider cp, string name = "Model_0_84")
        {
            var g = cp.FindGuidByName(name);
            if (g == null) { GD.PushError($"[CharacterModel] mesh {name} not found"); return; }
            _mesh = cp.LoadMesh(g);
            if (_mesh == null) return;
            var tp = cp.GetTexturePath(g);
            if (tp != null) { var img = Image.LoadFromFile(tp); if (img != null) _tex = ImageTexture.CreateFromImage(img); }
            var aabb = _mesh.GetAabb();
            _scale = 1.8f / Mathf.Max(0.05f, aabb.Size.Y); // ~1.8 m tall
            _footOffset = -aabb.Position.Y * _scale;        // feet on the ground
            GD.Print($"[CharacterModel] real character loaded: size=({aabb.Size.X:F2},{aabb.Size.Y:F2},{aabb.Size.Z:F2}) scale={_scale:F3} textured={_tex != null}");
        }

        public static Node3D Build(Color tint)
        {
            var root = new Node3D();
            if (_mesh == null) return root;
            // double-sided: the ripped skinned mesh winds opposite the static-mesh convention, so single-side
            // culling shows the interior ("inside-out"). Drawing both faces makes it read solid.
            var mat = new StandardMaterial3D { AlbedoColor = tint, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            if (_tex != null) mat.AlbedoTexture = _tex;
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
