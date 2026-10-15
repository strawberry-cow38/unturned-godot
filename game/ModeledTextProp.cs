using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnturnedGodot
{
    public partial class ModeledTextProp : Node3D
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public TextPropStyle Style { get; private set; } = new();
        public ModeledTextFont.Result Geometry { get; private set; }
        public MeshInstance3D Visual { get; private set; }
        public StaticBody3D PickBody { get; private set; }
        public StaticBody3D SolidBody { get; private set; }
        public void Rebuild(TextPropStyle style, bool pickable = false, bool visuals = true, bool collision = true)
        {
            Style = style.Clean(); Geometry = ModeledTextFont.Build(Style);
            foreach (var c in GetChildren()) { RemoveChild(c); c.QueueFree(); }
            Visual = null; PickBody = null; SolidBody = null;
            Name = "TextProp_" + Id; SetMeta("obj_name", "Text: " + Style.Text.Replace('\n', ' '));
            if (Geometry.Mesh == null) return;
            if (visuals)
            {
                Visual = new MeshInstance3D { Mesh = Geometry.Mesh, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(Style.Color), Roughness = 1 },
                    CastShadow = Style.Shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off };
                AddChild(Visual); // child zero: ordinary object selection marker reads this mesh's AABB
            }
            if (pickable)
            {
                PickBody = new StaticBody3D { CollisionLayer = EditorObjects.PickLayer, CollisionMask = 0 };
                PickBody.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Geometry.Bounds.Size.Max(Vector3.One * .02f) }, Position = Geometry.Bounds.GetCenter() }); AddChild(PickBody);
            }
            if (Style.Solid && collision)
            {
                SolidBody = new StaticBody3D { CollisionLayer = 1u | (1u << 6), CollisionMask = 0 };
                SolidBody.AddChild(new CollisionShape3D { Shape = Geometry.Mesh.CreateTrimeshShape() }); AddChild(SolidBody);
            }
        }
        public TextPropRecord Snapshot() => new() { Id = Id, Style = Style.Copy(), Transform = TextPropRecord.Pack(GlobalTransform) };
    }
    public sealed class TextPropRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public TextPropStyle Style { get; set; } = new();
        public float[] Transform { get; set; }
        public static float[] Pack(Transform3D t) => new[] { t.Basis.X.X,t.Basis.X.Y,t.Basis.X.Z,t.Basis.Y.X,t.Basis.Y.Y,t.Basis.Y.Z,t.Basis.Z.X,t.Basis.Z.Y,t.Basis.Z.Z,t.Origin.X,t.Origin.Y,t.Origin.Z };
        public Transform3D Unpack()
        {
            var a = Transform;
            if (a == null || a.Length != 12 || a.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid text-prop transform");
            var b = new Basis(new Vector3(a[0],a[1],a[2]),new Vector3(a[3],a[4],a[5]),new Vector3(a[6],a[7],a[8]));
            if (Mathf.Abs(b.Determinant()) < 1e-9f) throw new InvalidDataException("Singular text-prop transform");
            return new Transform3D(b, new Vector3(a[9],a[10],a[11]));
        }
        public TextPropRecord Copy(bool freshId = false) => new() { Id = freshId ? Guid.NewGuid().ToString("N") : Id, Style = Style.Copy(), Transform = (float[])Transform.Clone() };
    }
    public static class TextPropStore
    {
        public sealed class Document { public int Version { get; set; } = 1; public List<TextPropRecord> Props { get; set; } = new(); }
        public static string PathFor(string map) => ProjectSettings.GlobalizePath("res://content/objects/") + "editor_" + Path.GetFileName(map) + "_textprops.json";
        static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
        public static List<TextPropRecord> Read(string map)
        {
            string path = PathFor(map); if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Text-prop sidecar exceeds 16 MB");
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options);
            if (doc == null || doc.Version != 1 || doc.Props == null) throw new InvalidDataException("Unknown text-prop sidecar version");
            var ids = new HashSet<string>();
            foreach (var r in doc.Props)
            {
                if (r == null || r.Style == null || !Guid.TryParse(r.Id, out _) || !ids.Add(r.Id)) throw new InvalidDataException("Invalid or duplicate text-prop ID");
                r.Unpack(); r.Style = r.Style.Clean();
            }
            return doc.Props;
        }
        public static void Write(string map, IEnumerable<TextPropRecord> records)
        {
            string path = PathFor(map); var doc = new Document { Props = records.ToList() };
            if (doc.Props.Count == 0 && !File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Options)); File.Move(tmp, path, true);
        }
        public static int LoadRuntime(Node parent, string map, bool visuals = true, bool collision = true)
        {
            try
            {
                var records = Read(map);
                foreach (var r in records)
                {
                    var p = new ModeledTextProp { Id = r.Id }; p.Rebuild(r.Style, false, visuals, collision); parent.AddChild(p); p.GlobalTransform = r.Unpack();
                }
                return records.Count;
            }
            catch (Exception e) { Log.Err("[text-props] cannot load '" + map + "': " + e.Message); return 0; }
        }
    }
}