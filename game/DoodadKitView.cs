using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>strawberry's doodad kit's STOCK CHILDREN, composed at load: the ceramic bowl under the fruit, the three
    /// knives in the block, the cups of the mug pair -- the game's own item rips (content/items/&lt;id&gt;.txt and .png,
    /// byte-identical to the ones the kit composed against, tools/import_doodads.py checks), each set into the doodad by
    /// the transform its placement row gives (content/objects/doodad_kit_children.json).
    ///
    /// ⚠ THE KIT'S OWN COMPOSITION, NOT A GUESS AT IT. stock_children.schema.json: an item rip is first turned (x, y, z) ->
    /// (x, -z, y) into the doodad's native frame (Z-up, front +Y), then scaled, then turned by its XYZ Euler angles as
    /// Rz Ry Rx, then moved. The kit's checker (scripts/check_round5b.py) composes exactly that, and the importer
    /// reproduces every row's composed bounds to 1e-9 m with it. Hung under the doodad's mesh node -- whose basis is
    /// EditorObjects.Upright, native frame to world -- a child's local transform IS that native-frame transform.
    ///
    /// Each child is drawn as a dropped item of its id is (WorldItem.BuildReplicaVisual: its mesh, its albedo, its
    /// flags), so a cup in a doodad is the cup you pick up. A doodad with no art of its own (Mug_Pair_0, Plate_Stack_0,
    /// Leaning_Garden_Tools_0) is nothing but these.</summary>
    public static class DoodadKitView
    {
        const string Sidecar = "res://content/objects/doodad_kit_children.json";
        static Dictionary<string, List<(ushort Item, Transform3D Local)>> _kids;

        static Dictionary<string, List<(ushort Item, Transform3D Local)>> Kids()
        {
            if (_kids != null) return _kids;
            _kids = new Dictionary<string, List<(ushort, Transform3D)>>();
            using var f = FileAccess.Open(Sidecar, FileAccess.ModeFlags.Read);
            if (f == null) { Log.Err($"[doodads] {Sidecar} unreadable: the kit's doodads draw without their stock children"); return _kids; }
            var parsed = Json.ParseString(f.GetAsText());
            if (parsed.VariantType != Variant.Type.Dictionary) { Log.Err($"[doodads] {Sidecar} is not a table"); return _kids; }
            // the item rip's turn into the native frame: x stays, y -> z, z -> -y (the columns are where each axis goes)
            var native = new Basis(new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, -1f, 0f));
            foreach (var kv in parsed.AsGodotDictionary())
            {
                string name = kv.Key.AsString();
                if (name.StartsWith("_")) continue;
                var list = new List<(ushort, Transform3D)>();
                foreach (var cv in kv.Value.AsGodotArray())
                {
                    var c = cv.AsGodotDictionary();
                    var p = c["position"].AsGodotArray(); var r = c["rotation_deg"].AsGodotArray();
                    float s = c["scale"].AsSingle();
                    var rx = new Basis(Vector3.Right, Mathf.DegToRad(r[0].AsSingle()));
                    var ry = new Basis(Vector3.Up, Mathf.DegToRad(r[1].AsSingle()));
                    var rz = new Basis(Vector3.Back, Mathf.DegToRad(r[2].AsSingle()));
                    var basis = rz * ry * rx * native * Basis.FromScale(new Vector3(s, s, s));
                    list.Add(((ushort)c["item"].AsInt32(), new Transform3D(basis, new Vector3(p[0].AsSingle(), p[1].AsSingle(), p[2].AsSingle()))));
                }
                _kids[name] = list;
            }
            Log.Print($"[doodads] {_kids.Count} kit doodads carry stock children");
            return _kids;
        }

        /// <summary>Does this prop carry stock children?</summary>
        public static bool Has(string name) => name != null && Kids().ContainsKey(name);

        /// <summary>Hang a doodad's stock children under its node (one placed upright, as EditorObjects.Upright stands a prop),
        /// each a MeshInstance3D in the doodad's native frame. Returns how many; 0 for a prop with none.</summary>
        public static int Attach(Node3D node, string name)
        {
            if (node == null || !Kids().TryGetValue(name ?? "", out var kids)) return 0;
            int k = 0;
            foreach (var (item, local) in kids)
            {
                var mi = WorldItem.BuildReplicaVisual(item, Colors.White);
                mi.Name = $"Stock_{item}_{k++}";
                mi.Transform = local;
                node.AddChild(mi);
            }
            return k;
        }
    }
}
