using Godot;

namespace UnturnedGodot
{
    /// <summary>Turn a generated island's road/building lists into REAL props in the world.
    ///
    /// ProcIsland is deliberately pure: it produces heights and lists of (prop, x, z, yaw) and instantiates
    /// nothing, so every check in the suite can run headless. This is the one place that crosses over, and it
    /// exists because "the generator works" and "the generator produces a world you can walk around in" are
    /// different claims -- the suite could only ever have proved the first.
    ///
    /// THE FRAME. ProcIsland works in grid-index * 4 m, both axes positive; Terrain renders grid (gx,gy) at
    /// world (gx*4, h, -gy*4) -- "Z is negated relative to the grid" (Terrain.WorldBoundsXZ). That is the same
    /// relationship a retail map's coordinates have to the world, which is why WorldBuilder's own transform is
    /// the right one to reuse rather than invent a second: position Z negates, and yaw becomes 180 - yaw
    /// (EditorObjects.FromEuler / WorldBuilder's `Basis(Y, 180 - ey)`). Getting this wrong is quiet -- a
    /// symmetric Line or Quad looks identical either way, and only an asymmetric building shows the 180.</summary>
    public static class ProcIslandSpawn
    {
        /// <summary>The rotation a prop authored in ProcIsland's frame needs in the world.
        ///
        /// This is the retail placement euler with the yaw filled in, and it MUST go through the same
        /// FromEuler the map loader uses. ex=270 is the standing correction for the Z-up prop meshes (see
        /// EditorObjects.FromEuler); the first cut here passed a bare `Basis(Up, 180 - yaw)` and every road tile
        /// came out as a 24 m WALL on its edge -- which from a 3/4 view read as "the roads are a bit thick" and
        /// from directly above as a hairline. A boxy house lying on its side still reads as a house, so the
        /// buildings gave nothing away at all.</summary>
        public static Basis RotFor(float procYawDeg) => EditorObjects.FromEuler(270f, procYawDeg, 0f);

        /// <summary>Where a prop-LOCAL direction (mesh X/Y, the frame road_connectors.txt is written in) ends
        /// up in ProcIsland's frame once the prop is placed at this yaw.
        ///
        /// Runs the real placement basis rather than an open-coded matrix, because an open-coded one is where
        /// this went wrong: the suite's copy read
        ///     wx = lx*cos - ly*sin ;  wz = -lx*sin - ly*cos
        /// whose determinant is -1 -- a REFLECTION, not a rotation. It agreed on the +/-Y column, so Line,
        /// LineCap, Tee, TeeCap, Quad and QuadCap all passed: their X arms come in symmetric +/- pairs, and
        /// negating the X image maps that set onto itself. The Turn is the only piece with a lone +X arm, and
        /// its placement carried the matching flip, so check and code were wrong together and stayed green.
        /// strawberry found it by looking at a render.</summary>
        public static (float x, float z) ArmDir(float procYawDeg, float meshX, float meshY)
        {
            var v = RotFor(procYawDeg) * new Vector3(meshX, meshY, 0f);
            return (v.X, -v.Z);   // world -> ProcIsland's frame, which negates Z
        }

        /// <summary>The world position of a ProcIsland (x, z) pair, dropped onto the terrain.</summary>
        public static Vector3 PosFor(Terrain terr, float px, float pz)
        {
            float wx = px, wz = -pz;
            return new Vector3(wx, terr != null ? terr.SampleHeight(wx, wz) : 0f, wz);
        }

        /// <summary>Place every road tile and building of the last GenerateIsland through the editor's own
        /// object placer, so the result is selectable, movable and saves with the map like anything a human
        /// dragged in. Returns what actually landed -- a name the catalogue does not know returns null from
        /// Place and would otherwise vanish silently.</summary>
        public static (int roads, int buildings, int missing) Spawn(Terrain terr, EditorObjects objs)
        {
            if (terr == null || objs == null) return (0, 0, 0);
            int roads = 0, buildings = 0, missing = 0;

            foreach (var t in terr.IslandTiles)
            {
                string prop = ProcIsland.PropFor(t.Piece);
                if (prop == null) { missing++; continue; }
                if (objs.Place(prop, PosFor(terr, t.X, t.Z), RotFor(t.YawDeg)) != null) roads++; else missing++;
            }
            foreach (var b in terr.IslandBuildings)
            {
                if (objs.Place(b.Prop, PosFor(terr, b.X, b.Z), RotFor(b.YawDeg)) != null) buildings++; else missing++;
            }
            GD.Print($"[island] spawned {roads} road props + {buildings} buildings" + (missing > 0 ? $" ({missing} MISSING from the object catalogue)" : ""));
            return (roads, buildings, missing);
        }
    }
}
