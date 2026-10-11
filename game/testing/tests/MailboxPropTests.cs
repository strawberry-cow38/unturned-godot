using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The blue street mailbox is a PLACEABLE PROP, not just art on disk (strawberry 2026-10-11:
    /// "ship the mailbox as a prop and scrap the newspaper stands").
    ///
    /// ⚠ WHY THIS EXISTS AT ALL. astraclaw ships art and cannot push; registration is a separate job, and when
    /// it is skipped NOTHING ERRORS -- the .obj sits in content/objects/, every art check passes, and the prop
    /// simply never appears in the palette. Three art branches were sitting in exactly that state when this was
    /// written. So the checks below are about the REGISTRATION, which is the part that silently does not happen.</summary>
    public sealed class MailboxPropTests : GameTest
    {
        public override string Name => "prop.street_mailbox";
        public override double TimeoutSimSeconds => 30;
        const string Prop = "Street_Mailbox_Blue_0";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            yield return Ticks(2);

            // ---- 1. THE MESH IS WHAT A MAILBOX SHOULD BE, measured not assumed. ⚠ Props are Z-UP (retail
            // convention) while the same author's RESOURCE handoffs are Y-up -- the redwoods needed renaming
            // into a part-indexed layout and this one must not. A prop shipped on the wrong axis lies on its
            // side and nothing complains.
            var mesh = ContentProvider.ParseObj($"res://content/objects/{Prop}.obj");
            T.Check($"{Prop}.obj loads", mesh != null);
            if (mesh != null)
            {
                var a = mesh.GetAabb();
                T.Check($"...it stands up on Z, the prop axis ({a.Size.X:0.00} x {a.Size.Y:0.00} x {a.Size.Z:0.00} m)",
                        a.Size.Z > a.Size.X && a.Size.Z > a.Size.Y);
                T.Check($"...and is mailbox-sized, about chest height ({a.Size.Z:0.00} m)",
                        a.Size.Z > 1.2f && a.Size.Z < 1.9f);
                T.Check($"...sitting on its base rather than sunk or floating ({a.Position.Z:0.00})",
                        Mathf.Abs(a.Position.Z) < 0.05f);
            }

            // ---- 2. IT IS IN THE PALETTE. This is the registration: a guid_mesh.txt row is the whole
            // difference between "the file exists" and "a mapper can place one".
            bool listed = false;
            foreach (var n in objs.Catalog) if (n == Prop) { listed = true; break; }
            T.Check($"{Prop} is in the editor's prop catalog", listed);

            // ---- 3. PLACING ONE BUILDS A REAL OBJECT: a mesh to see, a body to walk into, and a surface tag
            // so footsteps and vehicles know what it is made of (EditorObjects.Place gained that tagging today).
            var node = objs.Place(Prop, new Vector3(5f, 0f, 5f), Basis.Identity);
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
