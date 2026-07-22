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
            if (b.Type == "vehicle") return Vehicle.BuildFromBundle(b);   // vehicles get the full drivable Vehicle rig, not the generic tree
            // everything else -> a StaticBody3D that renders + collides (or a FactoryPowerDevice if it declares power).
            string powerKind = b.ParamString("power_kind");
            bool isPowered = !string.IsNullOrEmpty(powerKind);
            if (!isPowered) foreach (var pt in b.Points) if (PortKindFromName(pt.Name) != null) { isPowered = true; break; }   // multi-port: named Power* points make it a power device too
            Node3D root = isPowered
                ? new FactoryPowerDevice { Name = b.Name, CollisionLayer = 1 << 0, IsSource = powerKind?.ToLowerInvariant() == "output" }   // WireBehaviors re-sets IsSource from the actual ports
                : new StaticBody3D { Name = b.Name, CollisionLayer = 1 << 0 };

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

            WireBehaviors(root, b);
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

        // Build a bundle's whole composed model into ONE container node (all parts as textured, transformed children)
        // and return its combined local AABB. Used by the deployable binder (Deployable.BuildMesh) + icon bake.
        public static MeshInstance3D BuildComposite(AssetBundle b, out Aabb aabb)
        {
            var root = new MeshInstance3D();   // container (no mesh of its own); parts are children
            aabb = default; bool first = true;
            if (b != null)
                foreach (var p in b.Parts)
                {
                    var part = BuildPart(p);
                    if (part?.Mesh == null) continue;
                    root.AddChild(part);
                    var mb = CompositeAabb(part.Transform, part.Mesh.GetAabb());
                    aabb = first ? mb : aabb.Merge(mb); first = false;
                }
            if (first) aabb = new Aabb(Vector3.Zero, Vector3.One);   // empty bundle -> unit box (never a zero collider)
            return root;
        }

        // World-space AABB of a local AABB under a transform (8-corner transform; no Transform3D*Aabb operator in use).
        static Aabb CompositeAabb(Transform3D t, Aabb a)
        {
            Vector3 mn = new(float.MaxValue, float.MaxValue, float.MaxValue), mx = new(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                Vector3 w = t * (a.Position + new Vector3((i & 1) != 0 ? a.Size.X : 0f, (i & 2) != 0 ? a.Size.Y : 0f, (i & 4) != 0 ? a.Size.Z : 0f));
                mn.X = Mathf.Min(mn.X, w.X); mn.Y = Mathf.Min(mn.Y, w.Y); mn.Z = Mathf.Min(mn.Z, w.Z);
                mx.X = Mathf.Max(mx.X, w.X); mx.Y = Mathf.Max(mx.Y, w.Y); mx.Z = Mathf.Max(mx.Z, w.Z);
            }
            return new Aabb(mn, mx - mn);
        }

        // Behaviours layer (master's vision — declare a behaviour, the loader wires it to the existing system).
        // impact-fx here; power ports (ConnectionPort/IPowerDevice) + destructible (DestructibleField) + powered-gate
        // roll on the same rails. (Fluid containers = tinyclaw's fluid-IO system, wired in when it lands.)
        static void WireBehaviors(Node3D root, AssetBundle b)
        {
            // impact-fx: tag the surface so bullet hits play the right impact effect/sound (PlayerController.Surf)
            string surf = b.ParamString("surface");
            if (!string.IsNullOrEmpty(surf) && root is StaticBody3D sb
                && System.Enum.TryParse<PlayerController.Surf>(surf, true, out var s))
            {
                sb.SetMeta(PlayerController.SurfMeta, (int)s);
                GD.Print($"[assetbundle] {b.Name}: impact surface = {s}");
            }

            // power in/OUTS: bolt ConnectionPort(s) onto the grid (root is a FactoryPowerDevice, see Build). MULTI-PORT:
            // each point named PowerOut*/PowerIn*/PowerThru* becomes a port at its position -> a factory device can be a
            // relay/battery/splitter (in AND out). Falls back to the single power_kind + "Power" point if no such points.
            if (root is FactoryPowerDevice fpd)
            {
                float watts = b.ParamFloat("power_watts", 0f);
                string plabel = b.ParamString("power_label") ?? b.Name;
                bool hasOutput = false; int made = 0;
                foreach (var pt in b.Points)
                {
                    var k = PortKindFromName(pt.Name);
                    if (k == null) continue;
                    fpd.AddPort(ConnectionPort.Create(fpd, new DeployableDef.Port { Kind = k.Value, Pos = AssetBundle.V3(pt.Pos), Watts = watts }, $"{plabel}:{pt.Name}"));
                    if (k.Value == DeployableDef.PortKind.Output) hasOutput = true;
                    made++;
                }
                if (made == 0)   // no named power points -> single power_kind (backward compat)
                {
                    var kind = (b.ParamString("power_kind") ?? "").ToLowerInvariant() switch
                    {
                        "output" => DeployableDef.PortKind.Output,
                        "consumer" => DeployableDef.PortKind.Consumer,
                        "passthrough" => DeployableDef.PortKind.Passthrough,
                        _ => (DeployableDef.PortKind?)null,
                    };
                    if (kind.HasValue)
                    {
                        var pt = b.FindPoint("Power");   // author-placed port position, else a default just above the base
                        fpd.AddPort(ConnectionPort.Create(fpd, new DeployableDef.Port { Kind = kind.Value, Pos = pt != null ? AssetBundle.V3(pt.Pos) : new Vector3(0f, 0.5f, 0f), Watts = watts }, plabel));
                        if (kind.Value == DeployableDef.PortKind.Output) hasOutput = true;
                        made = 1;
                    }
                }
                if (made > 0)
                {
                    fpd.IsSource = hasOutput;        // a device with any Output port produces
                    fpd.AddToGroup("deployables");   // PowerNet scans this group
                    GD.Print($"[assetbundle] {b.Name}: {made} power port(s) {watts}w, source={fpd.IsSource}");
                    if (b.ParamBool("powered_light"))   // powered-flag behaviour: a light gated by this device's power
                    {
                        fpd.AddPoweredLight(b.ParamFloat("light_energy", 4f), new Color(1f, 0.95f, 0.8f), b.ParamFloat("light_range", 6f));
                        GD.Print($"[assetbundle] {b.Name}: powered light (on when powered)");
                    }
                }
            }
        }

        // A power point's name -> its port kind (multi-port authoring): PowerOut*/PowerIn*(or PowerConsumer)/PowerThru*(or PowerPass).
        static DeployableDef.PortKind? PortKindFromName(string n)
        {
            n = (n ?? "").ToLowerInvariant();
            if (n.StartsWith("powerout")) return DeployableDef.PortKind.Output;
            if (n.StartsWith("powerin") || n.StartsWith("powercon")) return DeployableDef.PortKind.Consumer;
            if (n.StartsWith("powerthru") || n.StartsWith("powerpass")) return DeployableDef.PortKind.Passthrough;
            return null;
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
            if (tex == null) { var res = AssetBundle.ResolveAlbedo(p.Mesh); if (res != null) tex = LoadTex(ContentDir + res); }   // auto-texture from the mesh name
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
