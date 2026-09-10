using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>What each placed prop is actually MADE OF, from the retail physic material on its collider.
    ///
    /// Retail picks footstep, bullet-impact and melee audio off the physic material NAME of the thing you hit
    /// (PhysicMaterialCustomData.GetAudioDef(materialName, "BulletImpact" / "FootstepWalk" / ...)). The port
    /// carries a Surf per collider instead, and WorldBuilder set it from a single ternary --
    /// `fmesh != null ? Wood : Concrete`, i.e. "trees have a foliage mesh so they are wood, everything else in
    /// the world is concrete". Half the map is metal: of the 1028 object prefabs that name a material, 506 are
    /// Metal, 149 Wood, 67 Tile, 87 Cloth, 47 Gravel, and only 134 are Concrete. So a chain-link fence, a
    /// shipping container and a car wreck all rang like pavement when you shot them, and the impact debris and
    /// decal came off the same wrong answer.
    ///
    /// Table from tools/extract_prop_surfaces.py -> content/objects/surfaces.tsv. Props with no row keep the
    /// old ternary: the resources (trees, bushes) live in a different bundle and never had a row here, and
    /// they are the exact case the ternary was written for.</summary>
    public static class PropSurfaces
    {
        static Dictionary<string, PlayerController.Surf> _byName;

        public static int Count => _byName?.Count ?? 0;

        /// <summary>A retail physic material name -> the port's Surf. `_Static`/`_Dynamic` is whether the
        /// collider moves, not what it sounds like, so both collapse onto one surface -- our banks are keyed
        /// by material alone. `_Silent` and `Metal_Slip` are behaviour variants of the same material.</summary>
        public static PlayerController.Surf SurfForMaterial(string physicMaterial)
        {
            string m = (physicMaterial ?? "").ToLowerInvariant();
            if (m.StartsWith("metal")) return PlayerController.Surf.Metal;
            if (m.StartsWith("wood")) return PlayerController.Surf.Wood;
            if (m.StartsWith("gravel")) return PlayerController.Surf.Gravel;
            if (m.StartsWith("foliage")) return PlayerController.Surf.Grass;
            if (m.StartsWith("snow")) return PlayerController.Surf.Snow;
            if (m.StartsWith("ice")) return PlayerController.Surf.Ice;
            if (m.StartsWith("water")) return PlayerController.Surf.Water;
            // TILE has no bank of its own anywhere in the rip -- no tile footstep, landing, bullet or melee
            // clip exists -- so 67 tiled props take the hard-floor answer. Not a fallback catching them by
            // accident: it is the closest thing retail ships, and it is here so a tile bank has one line to
            // land in.
            if (m.StartsWith("tile")) return PlayerController.Surf.Concrete;
            // CLOTH likewise has no bank, and this one is MY choice rather than retail's: a tarp or an awning
            // is soft and dead, so it takes dirt's dull thud instead of concrete's ring. 87 props. Flagged
            // rather than presented as ported -- retail resolves it through the physic material's own audio
            // fallback chain, which we do not extract.
            if (m.StartsWith("cloth")) return PlayerController.Surf.Dirt;
            return PlayerController.Surf.Concrete;
        }

        static void Load()
        {
            _byName = new Dictionary<string, PlayerController.Surf>();
            string p = ProjectSettings.GlobalizePath("res://content/objects/surfaces.tsv");
            if (!System.IO.File.Exists(p)) { Log.Print("[surf] no objects/surfaces.tsv -- props fall back to the mesh guess"); return; }
            foreach (var ln in System.IO.File.ReadAllLines(p))
            {
                var c = ln.Split('\t');
                if (c.Length >= 2 && c[0].Length > 0) _byName[c[0].Trim().ToLowerInvariant()] = SurfForMaterial(c[1].Trim());
            }
            Log.Print($"[surf] {_byName.Count} prop surfaces from the retail physic materials");
        }

        /// <summary>The prop's surface, or null when the table has nothing for it. Falls back through the
        /// name's PARENT before giving up: our rip splits some props into their own meshes (`biodome_0_glass`,
        /// `barbecue_1_lid`) which never had a prefab of their own, and a piece of a prop is made of what the
        /// prop is made of.</summary>
        public static PlayerController.Surf? For(string propName)
        {
            if (_byName == null) Load();
            string n = (propName ?? "").ToLowerInvariant();
            while (n.Length > 0)
            {
                if (_byName.TryGetValue(n, out var s)) return s;
                int i = n.LastIndexOf('_');
                if (i <= 0) break;
                n = n.Substring(0, i);
            }
            return null;
        }

        /// <summary>Drop the cache so a map change or a re-extract is picked up (ResourceCaches.ClearAll).</summary>
        public static void Clear() => _byName = null;
    }
}
