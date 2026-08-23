using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>EVERY catalog entry must be previewable, because the object browser previews one on open and
    /// the palette is not all meshes.
    ///
    /// `MeshFor` built a path and handed it straight to `ObjMesh.Load`, which goes to `File.ReadLines` and
    /// THROWS on a missing file. Most of the catalog is a real `<name>.obj` so that held -- but "★ Loot Crate"
    /// is a placeable CONTAINER whose geometry the placer builds procedurally, there is no crate .obj, and the
    /// catalog pins it at index 0. So the browser opened, armed the first entry, asked for "★ Loot Crate.obj"
    /// and threw on boot, every time.
    ///
    /// The crate was the one that got NOTICED, because it is the entry the browser opens on. Reverting the
    /// fix under this test shows FOUR throwing entries -- "★ Loot Crate", "🛒 Store Shelf", "⚡ Grid Power"
    /// and "⛽ Gas Pump" -- which is the argument for sweeping the catalog instead of asserting on the one
    /// name in the bug report.
    ///
    /// The check walks the WHOLE catalog rather than naming the crate, because the defect is "a catalog entry
    /// that is not a mesh file", and the next one added would land the same way. `MatFor`, two methods away
    /// from the culprit, already guarded its texture load with File.Exists -- so this was a divergence from
    /// the file's own convention, not an unknown hazard.</summary>
    public class EditorEveryCatalogEntryPreviews : GameTest
    {
        public override string Name => "editor.every_catalog_entry_previews";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            yield return Ticks(1);

            var names = new List<string>(objs.Catalog);
            T.Check($"the catalog loaded ({names.Count} entries)", names.Count > 0);

            int meshed = 0, meshless = 0;
            var threw = new List<string>(); var noMesh = new List<string>();
            foreach (var n in names)
            {
                try
                {
                    // Both halves of what the browser does on open. A preview that throws is the bug; a
                    // preview that returns null is fine and the stage renders an empty turntable.
                    if (objs.PreviewMesh(n) != null) meshed++; else { meshless++; noMesh.Add(n); }
                    objs.PreviewMaterial(n);
                }
                catch (System.Exception e) { threw.Add($"{n}: {e.GetType().Name}"); }
            }
            GD.Print($"[preview] {names.Count} catalog entries: {meshed} with a mesh, {meshless} without, {threw.Count} threw");
            foreach (var t in threw) GD.Print($"[preview]   THREW {t}");
            foreach (var t in noMesh) GD.Print($"[preview]   no mesh (previews blank): {t}");
            T.Check($"no catalog entry throws when previewed ({threw.Count} threw)", threw.Count == 0);

            // The store shelf is a REAL prop wearing a container's name -- PlaceStoreShelf builds it from
            // Shelf_1 -- so it should preview as that mesh rather than as nothing. Asserted separately
            // because "did not throw" is satisfied by returning null, which would silently be a blank tile.
            if (names.Contains(EditorObjects.StoreShelfName))
                T.Check("the store shelf previews its real Shelf_1 mesh, not a blank",
                        objs.PreviewMesh(EditorObjects.StoreShelfName) != null);

            // ...and most of the palette really is meshes, so a fix that returned null for everything (which
            // would also make "nothing throws" pass) is ruled out.
            T.Check($"the palette still resolves real meshes ({meshed} of {names.Count})", meshed > names.Count / 2);
        }
    }
}
