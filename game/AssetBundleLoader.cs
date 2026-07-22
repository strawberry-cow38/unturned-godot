using Godot;

namespace UnturnedGodot
{
    // Turns an AssetBundle (Asset Factory output) into an in-game node tree — the
    // "easy to implement into the game" half. Phase 1: a static tree that always at least
    // renders + collides: root body + Parts (MeshInstance3D) + Colliders (CollisionShape3D)
    // + Volumes (named Area3D) + Points (named Node3D markers the game queries by name).
    // Phase 4 swaps the root per Type (vehicle->VehicleBody3D, etc.) and wires the points.
    public static class AssetBundleLoader
    {
        public const string ContentDir = "res://content/";

        public static Node3D Load(string path)
        {
            var b = AssetBundle.Load(path);
            return b == null ? null : Build(b);
        }

        public static Node3D Build(AssetBundle b)
        {
            // Phase 1: every type builds a StaticBody3D root so a bundle always renders +
            // collides. Phase 4 binders specialize this (vehicle->VehicleBody3D reading Wheel_*/
            // Seat_*, deployable->placement + storage volume, gun->viewmodel hooks).
            Node3D root = new StaticBody3D { Name = b.Name, CollisionLayer = 1 << 0 };

            var partsHolder = new Node3D { Name = "Parts" };
            root.AddChild(partsHolder);
            foreach (var p in b.Parts)
            {
                var mi = BuildPart(p);
                if (mi != null) partsHolder.AddChild(mi);
            }

            foreach (var c in b.Colliders)
            {
                var shape = ColliderShape(c, b);
                if (shape == null) continue;
                root.AddChild(new CollisionShape3D
                {
                    Shape = shape,
                    Transform = new Transform3D(AssetBundle.EulerDegBasis(c.Rot), AssetBundle.V3(c.Pos)),
                });
            }

            foreach (var v in b.Volumes)
            {
                var area = new Area3D { Name = v.Name };
                area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = AssetBundle.V3(v.Size, Vector3.One) } });
                area.Transform = new Transform3D(AssetBundle.EulerDegBasis(v.Rot), AssetBundle.V3(v.Pos));
                root.AddChild(area);
            }

            var pointsHolder = new Node3D { Name = "Points" };
            root.AddChild(pointsHolder);
            foreach (var pt in b.Points)
                pointsHolder.AddChild(new Node3D
                {
                    Name = pt.Name,
                    Transform = new Transform3D(AssetBundle.EulerDegBasis(pt.Rot), AssetBundle.V3(pt.Pos)),
                });

            root.SetMeta("assetType", b.Type);
            root.SetMeta("assetName", b.Name);
            return root;
        }

        // Build one part's MeshInstance3D (mesh + material + transform). Shared by the runtime
        // loader (above) and the Asset Factory editor, so both compose parts identically.
        public static MeshInstance3D BuildPart(AssetBundle.Part p)
        {
            if (p == null || string.IsNullOrEmpty(p.Mesh)) return null;
            var mesh = ContentProvider.ParseObj(ContentDir + p.Mesh);
            if (mesh == null) { GD.PushWarning($"[AssetBundle] part mesh missing: {p.Mesh}"); return null; }
            return new MeshInstance3D
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(p.Mesh),
                Mesh = mesh,
                MaterialOverride = PartMaterial(p),
                Transform = new Transform3D(
                    AssetBundle.EulerDegBasis(p.Rot).Scaled(AssetBundle.V3(p.Scale, Vector3.One)),
                    AssetBundle.V3(p.Pos)),
            };
        }

        static StandardMaterial3D PartMaterial(AssetBundle.Part p)
        {
            var mat = new StandardMaterial3D
            {
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // ripped meshes are often single-sided
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                Metallic = 0f, MetallicSpecular = 0f, Roughness = 1f,
            };
            Texture2D tex = p.Albedo != null ? LoadTex(ContentDir + p.Albedo) : null;
            if (tex != null) mat.AlbedoTexture = tex;
            else if (p.Color != null && p.Color.Length >= 3)
                mat.AlbedoColor = new Color(p.Color[0], p.Color[1], p.Color[2], p.Color.Length >= 4 ? p.Color[3] : 1f);
            else mat.AlbedoColor = new Color(0.7f, 0.7f, 0.72f);
            return mat;
        }

        static Shape3D ColliderShape(AssetBundle.Collider c, AssetBundle b)
        {
            switch ((c.Shape ?? "box").ToLowerInvariant())
            {
                case "sphere":
                    return new SphereShape3D { Radius = c.Size != null && c.Size.Length >= 1 ? c.Size[0] : 0.5f };
                case "capsule":
                    return new CapsuleShape3D
                    {
                        Radius = c.Size != null && c.Size.Length >= 1 ? c.Size[0] : 0.5f,
                        Height = c.Size != null && c.Size.Length >= 2 ? c.Size[1] : 2f,
                    };
                case "convex":
                {
                    int idx = c.Size != null && c.Size.Length >= 1 ? (int)c.Size[0] : 0;
                    if (idx >= 0 && idx < b.Parts.Count && !string.IsNullOrEmpty(b.Parts[idx].Mesh))
                    {
                        var m = ContentProvider.ParseObj(ContentDir + b.Parts[idx].Mesh);
                        if (m != null) return m.CreateConvexShape();
                    }
                    return new BoxShape3D { Size = Vector3.One };
                }
                default:
                    return new BoxShape3D { Size = AssetBundle.V3(c.Size, Vector3.One) };
            }
        }

        static Texture2D LoadTex(string res)
        {
            string p = ProjectSettings.GlobalizePath(res);
            if (System.IO.File.Exists(p)) { var img = Image.LoadFromFile(p); if (img != null) return ImageTexture.CreateFromImage(img); }
            return null;
        }
    }
}
