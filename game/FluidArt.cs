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
            public float[] PartRot { get; set; }
            public float Offset { get; set; }
            public float Radius { get; set; }
            public float PortX { get; set; }
            public float PortY { get; set; }
            public float[] BranchZ { get; set; }
        }

        /// <summary>The ripped fluid-device art, by item id. NEVER THROWS: a missing or malformed catalog
        /// yields an EMPTY table and every caller falls back to the def's own dimensions.
        ///
        /// ⚠ This is loaded from DeployableDef's field initialisers -- a STATIC CONSTRUCTOR -- so anything
        /// thrown in here comes out as TypeInitializationException on DeployableDef and EVERY deployable in
        /// the game ceases to exist; the world build dies on the first prop that wants a fixture. A content
        /// file being absent is an ordinary condition (a clone that was only sent .cs files has no
        /// content/fluid at all) and must degrade, not detonate. Both halves of that were live on 2026-09-11:
        /// master hit the missing-KEY half, and the box hit the missing-FILE half an hour later.</summary>
        static readonly Lazy<Dictionary<ushort, Spec>> Catalog = new(() =>
        {
            try
            {
                var json = Godot.FileAccess.GetFileAsString("res://content/fluid/catalog.json");
                if (string.IsNullOrWhiteSpace(json)) { Log.Err("[fluidart] catalog.json missing/empty -- fluid devices keep their own dimensions"); return new Dictionary<ushort, Spec>(); }
                return JsonSerializer.Deserialize<Dictionary<ushort, Spec>>(
                           json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? new Dictionary<ushort, Spec>();
            }
            catch (System.Exception e)
            {
                Log.Err($"[fluidart] catalog.json unreadable ({e.Message}) -- fluid devices keep their own dimensions");
                return new Dictionary<ushort, Spec>();
            }
        });
        static readonly Dictionary<ushort, StandardMaterial3D> Materials = new();
        static Vector3 Vec(float[] v) => new(v[0], v[1], v[2]);
        public static bool HasArt(DeployableDef def) => def?.Fluid != null && !def.ProcBox;

        /// <summary>Where this device presents its hoses. The four machines strawberry enlarged carry
        /// their anchors further out (their fittings did NOT grow with them, so a spigot left at the old
        /// +-0.5 would sit buried inside the new hull); everything else still answers 0.5. The catalog
        /// publishes each height: barrels at .45, most machines at .60, purifier at its midpoint.</summary>
        public static Vector3 Port(DeployableDef def, float x, float y, float z)
        {
            if (!HasArt(def) || !Catalog.Value.TryGetValue(def.Id, out var spec)) return new Vector3(x, y, z);
            float px = spec.PortX > 0f ? spec.PortX : Mathf.Abs(x);
            // x == 0 is a FRONT-face port (the source's outlet), which keeps its own x and z and only
            // takes the device's hose height.
            return new Vector3(Mathf.IsZeroApprox(x) ? x : Mathf.Sign(x) * px,
                               spec.PortY > 0f ? spec.PortY : y, z);
        }
        /// <summary>Authored bounds, or the def's own box when it has no art row. Same reasoning as
        /// Configure: an unauthored device renders at its declared size instead of throwing.</summary>
        public static Aabb Bounds(DeployableDef def)
        {
            if (def == null || !Catalog.Value.TryGetValue(def.Id, out var spec))
                return new Aabb(-(def?.Size ?? Vector3.One) * 0.5f, def?.Size ?? Vector3.One);   // no art row -> the def's own box, centered (no live caller reads Position on this path, but GetCenter() should still answer Zero)
            return new Aabb(Vec(spec.BoundsMin), Vec(spec.BoundsSize));
        }

        public static float BranchZ(DeployableDef def, int i, float fallback) =>
            HasArt(def) && Catalog.Value.TryGetValue(def.Id, out var spec) && spec.BranchZ != null
                ? spec.BranchZ[i] : fallback;

        /// <summary>Stamp the ripped art's real bounds onto a fluid def. A device with NO row in the catalog
        /// keeps the dimensions its own definition set.
        ///
        /// ⚠ THIS USED TO THROW ON A MISSING ROW, AND IT TOOK THE WHOLE GAME DOWN WITH IT. Configure is called
        /// from DeployableDef's field initialisers, i.e. from a STATIC CONSTRUCTOR, so a KeyNotFoundException
        /// here surfaces as TypeInitializationException on DeployableDef and every deployable in the game
        /// stops existing -- the world build dies on the first prop that asks for a fixture. That is a
        /// catastrophic failure mode for "this id has no art row yet", which is a perfectly ordinary state for
        /// a device whose model has not been ripped (the pump jack, 1219). Fixed twice the same night on two
        /// branches (same cause, same guard): the lookup is now TryGetValue, as Anchor's already was.</summary>
        public static void Configure(DeployableDef def)
        {
            if (def == null || !Catalog.Value.TryGetValue(def.Id, out var spec)) return;
            def.Size = Vec(spec.BoundsSize);
            def.Offset = spec.Offset;
            def.Radius = spec.Radius;
        }

        static ArrayMesh Load(ushort id, string part) =>
            ContentProvider.ParseObj($"res://content/fluid/{id}_{part}.txt");

        public static Mesh Preview(DeployableDef def) =>
            def != null && Catalog.Value.TryGetValue(def.Id, out var spec)
                ? Load(def.Id, spec.Part == null ? "body" : "preview")
                : null;   // no art row -> no ripped model; the caller's own fallback draws it

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
            if (!Catalog.Value.TryGetValue(def.Id, out var spec) || spec.Part == null) return null;   // no art row -> body only
            // Valve colour state shifts only the handle's UVs between two measured
            // palette texels; materials on all other placed valves remain shared.
            if (def.Fluid == FluidRole.Valve) material = (StandardMaterial3D)material.Duplicate();
            var part = AddLevels(owner, def.Id, "part",
                def.Fluid == FluidRole.Pump ? "PumpDrum" : "ValveHandle", material, distance);
            part.Position = Vec(spec.Part);
            // The pump's coupling is authored lying flat and is stood upright onto the motor shaft here,
            // so both moving parts spin about their OWN local Y and the animation is one code path.
            if (spec.PartRot != null) part.RotationDegrees = Vec(spec.PartRot);
            return part;
        }
    }
}
