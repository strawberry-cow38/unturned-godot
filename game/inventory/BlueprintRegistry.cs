using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // Loads the pre-extracted blueprint catalog (content/blueprints.tsv, made by the --extractblueprints harness)
    // into memory and answers "what can I craft right now". The port bundles only a handful of item .dats, so the
    // full recipe set lives in this catalog rather than being parsed from .dats at runtime.
    public static class BlueprintRegistry
    {
        static readonly List<BlueprintDef> _all = new();
        public static IReadOnlyList<BlueprintDef> All => _all;

        public static int Load(string resPath = "res://content/blueprints.tsv")
        {
            _all.Clear();
            string path = ProjectSettings.GlobalizePath(resPath);
            if (!System.IO.File.Exists(path)) { GD.PrintErr($"[bp] catalog missing: {path}"); return 0; }
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var bp = BlueprintDef.FromTsv(line);
                if (bp != null) _all.Add(bp);
            }
            GD.Print($"[bp] loaded {_all.Count} blueprints from {resPath}");
            return _all.Count;
        }

        // blueprints craftable right now from `inv` (item-satisfiability only; skill/station are the caller's gate)
        public static List<BlueprintDef> Applicable(Crafting.IInv inv)
        {
            var r = new List<BlueprintDef>();
            foreach (var bp in _all)
            {
                if (bp.Inputs.Count == 0) continue;   // input-less (Salvage/target-ops) consume the OWNED item itself, not supplies -> not a supply-based craft
                if (Crafting.CanCraft(bp, inv, out _)) r.Add(bp);
            }
            return r;
        }
    }
}
