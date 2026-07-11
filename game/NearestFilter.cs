using Godot;

namespace UnturnedGodot
{
    // Unturned filters its level/object/item textures with FilterMode.Point (LevelBatching.cs:693/1007,
    // ItemTool.cs:445/695) for the crisp pixel look; only foliage stays Trilinear. The port builds most of
    // its materials at runtime (props, characters, vehicles, zombies), so this walks the built scene and sets
    // every BaseMaterial3D to nearest-neighbor to match the source. Call once after the scene is assembled.
    public static class NearestFilter
    {
        public static void Apply(Node n)
        {
            if (n is MeshInstance3D mi)
            {
                Set(mi.MaterialOverride);
                int so = mi.GetSurfaceOverrideMaterialCount();
                for (int i = 0; i < so; i++) Set(mi.GetSurfaceOverrideMaterial(i));
                if (mi.Mesh != null)
                    for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++) Set(mi.Mesh.SurfaceGetMaterial(i));
            }
            foreach (var c in n.GetChildren()) Apply(c);
        }

        static void Set(Material m)
        {
            if (m is BaseMaterial3D b) b.TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
        }
    }
}
