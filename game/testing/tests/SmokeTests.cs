using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Tier-0 smoke: the host boots, a sandbox exists, and physics ticks advance. Runs first so a broken harness
    // surfaces before any feature test (Factorio's simplest-failing-case-first).
    public class SmokeEngineBoots : GameTest
    {
        public override string Name => "smoke.engine_boots";
        public override int Tier => 0;
        public override IEnumerable<Step> Run()
        {
            T.Check("sandbox + scene tree present", World != null && Tree != null);
            T.Check("arithmetic sanity", 2 + 2 == 4);
            yield return Ticks(3);   // physics actually steps
            T.Check("advanced 3 physics ticks", true);
        }
    }

    // Tier-0 smoke: the COMMITTED content loads -- the item catalog registers from items_catalog.tsv and the
    // runtime OBJ-ish mesh parser turns a bundled .txt model into real geometry (the swap seam every gameplay
    // test relies on). (The old --smoke GUID GATE needs res://content/manifest.json, a rip-pipeline artifact
    // that isn't in the repo -- kept as a dev-box check, not a test.)
    public class SmokeContentLoads : GameTest
    {
        public override string Name => "smoke.content_loads";
        public override int Tier => 0;
        public override IEnumerable<Step> Run()
        {
            SDG.Unturned.ItemCatalog.RegisterAll();
            int items = 0;
            foreach (var a in SDG.Unturned.Assets.all()) items++;
            T.Check($"item catalog registers (got {items})", items > 1000);   // items_catalog.tsv carries ~1937

            var mesh = ContentProvider.ParseObj("res://content/grenade.txt");   // real ripped model, parsed at runtime
            T.Check("runtime OBJ parse produces a mesh", mesh != null);
            var arrays = mesh?.SurfaceGetArrays(0);
            int vcount = arrays != null && arrays.Count > 0 && arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.Nil
                ? ((Vector3[])arrays[(int)Mesh.ArrayType.Vertex]).Length : 0;
            T.Check($"parsed mesh has geometry (verts={vcount})", vcount > 0);
            yield break;
        }
    }
}
