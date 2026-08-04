using Godot;

namespace UnturnedGodot
{
    // Unturned filters its level/object/item textures with FilterMode.Point (LevelBatching.cs:693/1007,
    // ItemTool.cs:445/695) for the crisp pixel look; only foliage stays Trilinear. The port builds most of
    // its materials at runtime (props, characters, vehicles, zombies), so this walks the built scene and sets
    // every BaseMaterial3D to nearest-neighbor to match the source. Call once after the scene is assembled.
    public static class NearestFilter
    {
        /// <summary>Node group whose materials this sweep leaves alone. Retail point-filters level/object/item
        /// textures but keeps FOLIAGE on a smooth filter, and master extended that to trees and bushes -- all of
        /// which are alpha-scissored billboards where nearest-neighbour stair-steps every leaf edge.
        ///
        /// This exists because the sweep runs AFTER the scene is built, so a material that sets LinearWithMipmaps at
        /// construction gets silently overwritten here. Anything opting out has to say so at the node.</summary>
        public const string KeepFilterGroup = "keep_texture_filter";

        public static void Apply(Node n)
        {
            if (n.IsInGroup(KeepFilterGroup)) return;   // ...and skip its children too: a MultiMeshInstance's material is its own
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
