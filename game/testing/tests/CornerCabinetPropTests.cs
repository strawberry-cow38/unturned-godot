using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The two fixed-panel corner cabinets are placeable props (strawberry 2026-10-13, of astraclaw's corner
    /// cabinets: "ship the non-doored one, but archive the doored one for later" -- "the solid one").
    ///
    /// ⚠⚠ THE GUID IS THE CONTRACT. The kitchen pool will write these GUIDs into every generated layout, and a layout
    /// loads each cabinet back through guid_mesh.txt -- so the exact GUIDs are asserted, through the same lookup a
    /// loading placement uses. A cabinet re-registered under a fresh GUID would sit in the palette and pass any
    /// "is it in the catalog" check while every saved kitchen lost its corners.
    ///
    /// ⚠ AND THEY HAVE TO JOIN THE COUNTERS THEY TURN. A corner piece standing a centimetre taller than the run it
    /// joins shows a lip along the whole worktop; one whose back corner is not where the room's corner is leaves a
    /// gap behind it. So its height is held to the stock counters' own, read off their meshes, not to a number, and
    /// its footprint to the square the art notes give, with the back corner at -X/-Y.</summary>
    public sealed class CornerCabinetPropTests : GameTest
    {
        public override string Name => "props.corner_cabinets";
        public override double TimeoutSimSeconds => 30;

        static readonly (string name, string guid, string palette)[] Cabinets =
        {
            ("Corner_Cabinet_0_Solid", "92b9ba0da51b47068b7533565925a340", "Counter_0"),
            ("Corner_Cabinet_1_Solid", "96a55549406e43eebc5dc641d1196b6e", "Counter_2"),
        };

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            yield return Ticks(2);

            var catalog = new HashSet<string>();
            foreach (var n in objs.Catalog) catalog.Add(n);

            foreach (var (name, guid, palette) in Cabinets)
            {
                var mesh = ContentProvider.ParseObj($"res://content/objects/{name}.obj");
                var counter = ContentProvider.ParseObj($"res://content/objects/{palette}.obj");
                T.Check($"{name}.obj loads, and the counter it joins ({palette}) too", mesh != null && counter != null);
                if (mesh != null && counter != null)
                {
                    var a = mesh.GetAabb(); var c = counter.GetAabb();
                    float top = a.Position.Z + a.Size.Z, counterTop = c.Position.Z + c.Size.Z;
                    T.Check($"...standing on its base (z {a.Position.Z:0.000}) to exactly the counters' height ({top:0.000} m vs {counterTop:0.000})",
                            Mathf.Abs(a.Position.Z) < 0.001f && Mathf.Abs(top - counterTop) < 0.001f);
                    T.Check($"...on a {a.Size.X:0.000} x {a.Size.Y:0.000} m square, its back corner at ({a.Position.X:0.000}, {a.Position.Y:0.000})",
                            Mathf.Abs(a.Size.X - 1.75f) < 0.001f && Mathf.Abs(a.Size.Y - 1.75f) < 0.001f
                            && Mathf.Abs(a.Position.X + 0.875f) < 0.001f && Mathf.Abs(a.Position.Y + 0.875f) < 0.001f);
                }
                // the palette is the counter's own, so the two read as one run of units
                var tex = FileAccess.GetFileAsBytes($"res://content/objects/{name}_tex.png");
                var stock = FileAccess.GetFileAsBytes($"res://content/objects/{palette}_tex.png");
                bool samePalette = tex != null && stock != null && tex.Length > 0 && tex.Length == stock.Length;
                for (int k = 0; samePalette && k < tex.Length; k++) samePalette = tex[k] == stock[k];
                T.Check($"...wearing {palette}'s own palette, byte for byte", samePalette);
                T.Check($"{name} is in the editor's prop catalog", catalog.Contains(name));
                var resolved = objs.NameForGuid(guid);
                T.Check($"...and the kitchen pool's GUID {guid} loads it (resolves to {resolved ?? "NOTHING"})", resolved == name);
            }

            // ⭐ THE CONTROL: a GUID nobody registered resolves to nothing, so the lookup above cannot pass by
            // handing back a cabinet for anything it is asked
            T.Check("control: an unregistered GUID resolves to nothing", objs.NameForGuid("00000000000000000000000000000000") == null);

            var node = objs.Place(Cabinets[0].name, new Vector3(6f, 0f, 6f), Basis.Identity);
            yield return Ticks(2);
            T.Check("placing one returns a node", node != null);
            if (node != null)
            {
                int meshes = 0;
                void Walk(Node n) { if (n is MeshInstance3D mi && mi.Mesh != null) meshes++; foreach (var c in n.GetChildren()) Walk(c); }
                Walk(node);
                T.Check($"...with something to look at ({meshes} mesh instance(s))", meshes > 0);
            }
        }
    }
}
