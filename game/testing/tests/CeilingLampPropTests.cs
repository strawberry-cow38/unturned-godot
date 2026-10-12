using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The two ceiling lamps are placeable props, under the GUIDs tinyclaw's generated layouts already
    /// write (strawberry 2026-10-12, of astraclaw's pendant and flush mount: "nice ship"; tinyclaw: "reuse them
    /// when you register the props or saved layouts won't resolve").
    ///
    /// ⚠⚠ THE GUID IS THE CONTRACT, NOT THE NAME. A layout saved by the building generator stores each lamp as
    /// a GUID, and on load the editor turns that back into a mesh through guid_mesh.txt. Registering the same
    /// mesh under a freshly minted GUID would put the lamp in the palette, pass every "is it in the catalog"
    /// check -- and leave every saved house with no lights, silently. So the exact GUIDs are asserted, through
    /// the same lookup a loading placement uses.
    ///
    /// ⚠ AND THEY HANG FROM THE CEILING. The origin is the ceiling plane and all the geometry is below it (the
    /// art notes: "Origin is the CEILING PLANE at Z=0"). A lamp authored floor-up would be placed by the
    /// layouts with its whole body above the ceiling, inside the floor above -- invisible, and nothing errors.</summary>
    public sealed class CeilingLampPropTests : GameTest
    {
        public override string Name => "props.ceiling_lamps";
        public override double TimeoutSimSeconds => 30;

        static readonly (string name, string guid, float drop)[] Lamps =
        {
            ("Ceiling_Lamp_Pendant_0", "8140c08c106446938b4a219cfcf7a48a", 0.943f),
            ("Ceiling_Lamp_Flush_0",   "4812aa1e38084252b25f064c88a87b6e", 0.275f),
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

            foreach (var (name, guid, drop) in Lamps)
            {
                var mesh = ContentProvider.ParseObj($"res://content/objects/{name}.obj");
                T.Check($"{name}.obj loads", mesh != null);
                if (mesh != null)
                {
                    var a = mesh.GetAabb();
                    float top = a.Position.Z + a.Size.Z;
                    T.Check($"...{name} is mounted at its top: the origin is the ceiling (top at z {top:0.000})", Mathf.Abs(top) < 0.01f);
                    T.Check($"...and it hangs DOWN from it, by the drop the art notes give ({a.Size.Z:0.000} m vs {drop:0.000})",
                            a.Position.Z < -0.1f && Mathf.Abs(a.Size.Z - drop) < 0.01f);
                }
                T.Check($"{name} is in the editor's prop catalog", catalog.Contains(name));
                // the contract: the GUID a saved layout carries loads THIS mesh
                var resolved = objs.NameForGuid(guid);
                T.Check($"...and the layouts' GUID {guid} loads it (resolves to {resolved ?? "NOTHING"})", resolved == name);
            }

            // ⭐ THE CONTROL: a GUID nobody registered resolves to nothing. Without it, a lookup that returned the
            // first lamp for ANY guid would pass the check above.
            T.Check("control: an unregistered GUID resolves to nothing", objs.NameForGuid("00000000000000000000000000000000") == null);

            var node = objs.Place(Lamps[0].name, new Vector3(6f, 3f, 6f), Basis.Identity);
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
