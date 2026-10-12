using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The abandoned well is a PLACEABLE PROP, not just art on disk (strawberry 2026-10-11, on
    /// astraclaw's damaged duplicate: "awesome, approved as a prop").
    ///
    /// ⚠ SAME REASON THE MAILBOX HAS ONE. astraclaw ships models and cannot push; registration is a separate
    /// job, and when it is skipped NOTHING ERRORS -- the .obj sits in content/objects/, every art check passes,
    /// and the prop simply never appears in the palette. So these checks are aimed at the REGISTRATION, which
    /// is the half that silently does not happen.
    ///
    /// ⭐ AND IT IS A DUPLICATE, which gives this one a control the mailbox never had: the stock Well_0 is
    /// right there, so "same prop, just broken" (master's words) is measurable rather than a claim in a
    /// handoff note.</summary>
    public sealed class AbandonedWellPropTests : GameTest
    {
        public override string Name => "prop.abandoned_well";
        public override double TimeoutSimSeconds => 30;
        const string Prop = "Well_0_Abandoned", Stock = "Well_0";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            yield return Ticks(2);

            var mesh = ContentProvider.ParseObj($"res://content/objects/{Prop}.obj");
            var stock = ContentProvider.ParseObj($"res://content/objects/{Stock}.obj");
            T.Check($"{Prop}.obj loads", mesh != null);
            T.Check($"{Stock}.obj loads, as the control", stock != null);
            if (mesh != null && stock != null)
            {
                var a = mesh.GetAabb(); var b = stock.GetAabb();
                // ⚠ PROPS ARE Z-UP (retail), while the SAME author's resource handoffs are +Y-up -- their
                // redwoods needed renaming into a part-indexed layout and this one must not. A prop on the
                // wrong axis lies on its side and nothing complains.
                T.Check($"it stands on Z, the prop axis ({a.Size.X:0.00} x {a.Size.Y:0.00} x {a.Size.Z:0.00} m)",
                        a.Size.Z > a.Size.X && a.Size.Z > a.Size.Y);
                T.Check($"...sitting on its base rather than sunk or floating ({a.Position.Z:0.00})",
                        Mathf.Abs(a.Position.Z) < 0.05f);
                // "the same prop, just broken": the damage must not have moved the well.
                T.Check($"...with the STOCK well's footprint and height ({a.Size.X:0.000}x{a.Size.Y:0.000}x{a.Size.Z:0.000} vs {b.Size.X:0.000}x{b.Size.Y:0.000}x{b.Size.Z:0.000})",
                        (a.Size - b.Size).Length() < 0.01f);
                T.Check($"...and the stock well's origin ({a.Position} vs {b.Position})",
                        (a.Position - b.Position).Length() < 0.01f);
                // ...and it must actually BE damaged, or this is the stock well under a new name. The broken
                // roof and the capped post ends are extra geometry: 224 triangles against the stock 124.
                T.Check($"...but it IS the damaged copy, not a rename ({mesh.GetFaces().Length / 3} tris vs the stock {stock.GetFaces().Length / 3})",
                        mesh.GetFaces().Length > stock.GetFaces().Length);
            }

            // THE REGISTRATION: a guid_mesh.txt row is the whole difference between "the file exists" and "a
            // mapper can place one".
            bool listed = false;
            foreach (var n in objs.Catalog) if (n == Prop) { listed = true; break; }
            T.Check($"{Prop} is in the editor's prop catalog", listed);

            var node = objs.Place(Prop, new Vector3(6f, 0f, 6f), Basis.Identity);
            yield return Ticks(2);
            T.Check("placing one returns a node", node != null);
            if (node != null)
            {
                int meshes = 0; StaticBody3D body = null;
                void Walk(Node n)
                {
                    if (n is MeshInstance3D mi && mi.Mesh != null) meshes++;
                    if (n is StaticBody3D sb) body ??= sb;
                    foreach (var c in n.GetChildren()) Walk(c);
                }
                Walk(node);
                T.Check($"...with something to look at ({meshes} mesh instance(s))", meshes > 0);
                T.Check("...and something to walk into", body != null);
                if (body != null)
                    T.Check($"...tagged with a surface ({(body.HasMeta(PlayerController.SurfMeta) ? ((PlayerController.Surf)(int)body.GetMeta(PlayerController.SurfMeta)).ToString() : "NONE")})",
                            body.HasMeta(PlayerController.SurfMeta));
            }
        }
    }
}
