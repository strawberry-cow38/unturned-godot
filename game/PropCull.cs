using Godot;

namespace UnturnedGodot
{
    /// <summary>⭐ THE EDITOR'S PROPS CULL BY DISTANCE, AS THE WORLD LOADER'S DO (strawberry 2026-10-16: "make sure actual culling
    /// of props happens").
    ///
    /// WorldBuilder has always baked each prop's retail cull distance (LodTable: its render layer's distance, tightened by its
    /// LODGroup's last screen height) into VisibilityRangeEnd as it places it. EditorObjects.BuildProp -- the editor's placer, and
    /// since round 14 every generated building's furniture and every town prop -- never did, so all of it drew at any distance:
    /// a town's wardrobes, mugs and light switches were in the frame from the far side of the island, and so was everything
    /// on a custom map, which loads its props through the same placer. This gives that path the world loader's own rule, from
    /// the same table. A prop with NO row -- the port's own decor, 96 of the furniture catalog's 207 (a toothbrush cup, a
    /// painting, a vent, a wall cabinet) -- is not given the world loader's flat 320 m for a missing row: it gets the layer
    /// its size puts it in, LodBaker.LayerFor's rule, the one the generated table was baked with (a 0.2 m cup is SMALL,
    /// 64 m). Measured in a 21-building town, 898 of 1,612 editor-path props have no row.
    ///
    /// The range is stamped on EVERY drawable piece of the prop that has none of its own -- a lamp's glow, a streetlight's lens,
    /// a signal's heads, a cupboard's leaves, a kit's stock -- because VisibilityRange is per instance and is not inherited: a
    /// housing that culled while its lit lens did not would leave the lens glowing in mid-air across the map (LampLight learned
    /// that once). Devices build some pieces in their _Ready, so it is stamped again once the prop's root is ready.
    ///
    /// Collision is untouched: an editor-path prop's collider is resident whatever the distance (it is not in ColliderBudget's
    /// group), so "see it = shoot it" holds trivially -- the collider outlives the mesh.
    ///
    /// UG_NOPROPCULL=1 is the control: nothing is stamped, every editor-path prop draws at any distance as before.</summary>
    public static class PropCull
    {
        public static readonly bool Disabled = System.Environment.GetEnvironmentVariable("UG_NOPROPCULL") == "1";
        /// <summary>Marks a range PropCull stamped (so the culling probe can lift exactly those and put them back).</summary>
        public static readonly StringName StampMeta = "prop_cull";
        /// <summary>WorldBuilder's distance for a prop with no LOD row -- used here only when the prop's size is not known.</summary>
        public const float Fallback = 320f;

        static bool _tableTried;
        /// <summary>Props given a distance from the table (by GUID or generated name), and by their size for want of a row.</summary>
        public static int FromTable, BySize;

        /// <summary>Where this prop stops drawing: its retail cull (by GUID), else a generated table's band for its name, else the
        /// layer its size (`size`: the mesh's largest extent, m) puts it in -- or the world loader's 320 m if that is not known.
        /// 0 when culling is off.</summary>
        public static float For(string name, string guid, float size = 0f)
        {
            if (Disabled) return 0f;
            EnsureTable();
            float cull = string.IsNullOrEmpty(guid) ? 0f : LodTable.CullDistance(guid, LodTable.SourceFov);
            if (cull <= 0f)
            {
                var r = LodTable.LevelRanges(guid ?? "", name, LodTable.SourceFov);
                if (r != null && r.Length > 0) cull = r[r.Length - 1].End;
            }
            if (cull > 0f) { FromTable++; return cull; }
            if (size <= 0f) return Fallback;
            BySize++;
            return LodTable.LayerCull(LodBaker.LayerFor(size));
        }

        /// <summary>A retail render layer's distance ("SMALL", "MEDIUM", "LARGE"), for a piece with no prop of its own -- a
        /// generated rug, a light switch. 0 when culling is off.</summary>
        public static float Layer(string layer) => Disabled ? 0f : LodTable.LayerCull(layer);

        /// <summary>The LOD table, read once if nothing has (the world loader reads it for PEI; a custom map never builds a
        /// world, so without this every editor-path prop fell to the fallback). UG_NOLOD=1 keeps it unread, as it does there.</summary>
        static void EnsureTable()
        {
            if (_tableTried || LodTable.Loaded) return;
            _tableTried = true;
            if (System.Environment.GetEnvironmentVariable("UG_NOLOD") == "1") return;
            var dir = ProjectSettings.GlobalizePath("res://content/objects/");
            LodTable.Load(dir + "lods.txt");
            LodTable.LoadGenerated(dir + LodBaker.GeneratedTable);
        }

        /// <summary>Stamp `cull` on every drawable under `root` with no range of its own -- now, and again once the root is
        /// ready if it is not yet (the pieces its devices build in their _Ready).</summary>
        public static void Apply(Node3D root, float cull)
        {
            if (root == null || cull <= 0f) return;
            Stamp(root, cull);
            if (!root.IsNodeReady())
                root.Connect(Node.SignalName.Ready, Callable.From(() => Stamp(root, cull)), (uint)GodotObject.ConnectFlags.OneShot);
        }

        static void Stamp(Node n, float cull)
        {
            if (n is WellShaftWarm) return;   // its disc is a renderer warm-up draw that must never be range-culled
            // (a piece that copied the housing's range already -- LampLight's glow -- is marked as the housing is)
            if (n is GeometryInstance3D g && (g.VisibilityRangeEnd <= 0f || g.VisibilityRangeEnd == cull))
            {
                g.VisibilityRangeEnd = cull;
                g.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
                g.SetMeta(StampMeta, true);
            }
            foreach (var c in n.GetChildren()) Stamp(c, cull);
        }
    }
}
