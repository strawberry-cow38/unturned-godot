using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace UnturnedGodot
{
    // Authored, palette-sampled Y-up assets. The catalog is generated alongside the
    // meshes, so placement bounds and part pivots have one source. See the measured
    // barrel / generator / hydrant tables in notes/FLUID_ART_MEASUREMENTS.md.
    public static class FluidArt
    {
        public sealed class Spec
        {
            public float[] BoundsMin { get; set; }
            public float[] BoundsSize { get; set; }
            public float[] Part { get; set; }
            public float Offset { get; set; }
            public float Radius { get; set; }
        }

        static readonly Lazy<Dictionary<ushort, Spec>> Catalog = new(() =>
            JsonSerializer.Deserialize<Dictionary<ushort, Spec>>(
                Godot.FileAccess.GetFileAsString("res://content/fluid/catalog.json"),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
        static readonly Dictionary<ushort, StandardMaterial3D> Materials = new();
        static Vector3 Vec(float[] v) => new(v[0], v[1], v[2]);
        public static bool HasArt(DeployableDef def) => def?.Fluid != null && !def.ProcBox;
        public static Aabb Bounds(DeployableDef def)
        {
            var spec = Catalog.Value[def.Id];
            return new Aabb(Vec(spec.BoundsMin), Vec(spec.BoundsSize));
        }

        public static void Configure(DeployableDef def)
        {
            var spec = Catalog.Value[def.Id];
            def.Size = Vec(spec.BoundsSize);
            def.Offset = spec.Offset;
            def.Radius = spec.Radius;
        }

        static ArrayMesh Load(ushort id, string part) =>
            ContentProvider.ParseObj($"res://content/fluid/{id}_{part}.txt");

        public static Mesh Preview(DeployableDef def) =>
            Load(def.Id, Catalog.Value[def.Id].Part == null ? "body" : "preview");

        public static StandardMaterial3D Material(DeployableDef def)
        {
            if (Materials.TryGetValue(def.Id, out var mat)) return mat;
            mat = new StandardMaterial3D
            {
                AlbedoTexture = ContentProvider.TextureCached(ProjectSettings.GlobalizePath($"res://content/fluid/{def.Id}_palette.png")),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                Roughness = 1f,
            };
            Materials[def.Id] = mat;
            return mat;
        }

        // Both levels of an animated part live under the same MeshInstance3D.
        // Moving the pump drum or valve handle therefore transforms either LOD.
        static MeshInstance3D AddLevels(Node3D parent, ushort id, string file, string name,
            StandardMaterial3D material, float distance)
        {
            var mesh = new MeshInstance3D
            {
                Name = name, Mesh = Load(id, file), MaterialOverride = material,
                VisibilityRangeEnd = distance,
            };
            parent.AddChild(mesh);
            mesh.AddChild(new MeshInstance3D
            {
                Name = "Lod1", Mesh = Load(id, file + "_lod1"), MaterialOverride = material,
                VisibilityRangeBegin = distance,
            });
            return mesh;
        }

        public static MeshInstance3D BuildPlaced(FluidContainer owner)
        {
            var def = owner.Def;
            var bounds = Bounds(def);
            // Barrel_0's measured first screen-height threshold. Use the same
            // distance conversion / source FOV as existing prop LODs. No far cull:
            // deployed devices remain visible and functional beyond the LOD split.
            float distance = LodTable.DistanceForHeight(bounds.Size[(int)bounds.Size.MaxAxisIndex()], 0.20415f, LodTable.SourceFov);
            var material = Material(def);
            AddLevels(owner, def.Id, "body", "FluidBody", material, distance);
            owner.AddChild(new CollisionShape3D
            {
                Name = "FluidCollider", Shape = new BoxShape3D { Size = bounds.Size },
                Position = bounds.GetCenter(),
            });
            var spec = Catalog.Value[def.Id];
            if (spec.Part == null) return null;
            // Valve colour state shifts only the handle's UVs between two measured
            // palette texels; materials on all other placed valves remain shared.
            if (def.Fluid == FluidRole.Valve) material = (StandardMaterial3D)material.Duplicate();
            var part = AddLevels(owner, def.Id, "part",
                def.Fluid == FluidRole.Pump ? "PumpDrum" : "ValveHandle", material, distance);
            part.Position = Vec(spec.Part);
            return part;
        }
    }
}
