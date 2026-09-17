using Godot;

namespace UnturnedGodot
{
    /// <summary>The M-map image for a generated island (strawberry 2026-09-17: "the map shows PEI's map", and
    /// earlier "actually generate the map graphic").
    ///
    /// ⭐ DRAWN FROM THE DATA, NOT PHOTOGRAPHED. The retail path bakes its map with --bakemap, which flies an
    /// orthographic camera over the world and screenshots it -- that needs a render pass, a settled frame and a
    /// camera the generator does not otherwise have, and it captures whatever the culler happened to admit.
    /// Everything a map wants to show is already in memory here: the heightmap says where the coast and the
    /// hills are, the splat says what the ground IS, and the route list says where the roads go. Reading those
    /// is deterministic, costs a few milliseconds, and cannot disagree with the world the way a photograph of a
    /// half-streamed scene can.
    ///
    /// ⚠ The image is written to content/ beside the shipped maps, because that is where MapUI.LoadMap looks.
    /// Keyed by seed, and OVERWRITTEN each generation -- the foliage bake taught that lesson today: a cache
    /// keyed on the seed alone goes stale the moment anything else about the generator changes, and a map of an
    /// island that no longer exists is worse than no map.</summary>
    public static class ProcIslandMap
    {
        public const int Res = 1024;

        public static string Bake(Terrain terr, int seed)
        {
            if (terr == null) return null;
            var b = terr.WorldBoundsXZ();
            // SQUARE, and framed on the island's own centre. MapUI.WorldToNorm divides by ONE size for both
            // axes, so a non-square frame would stretch every dot on it.
            float spanX = b.MaxX - b.MinX, spanZ = b.MaxZ - b.MinZ;
            float size = Mathf.Max(spanX, spanZ);
            float cx = (b.MinX + b.MaxX) * 0.5f, cz = (b.MinZ + b.MaxZ) * 0.5f;

            var img = Image.CreateEmpty(Res, Res, false, Image.Format.Rgb8);
            float sea = Terrain.SeaLevelY;
            for (int py = 0; py < Res; py++)
            {
                // ⚠ THE SAME MAPPING MapUI PROJECTS WITH, inverted. WorldToNorm is
                // ((x - cx)/size + 0.5, 0.5 + (z - cz)/size), so pixel -> world must be the exact inverse or
                // every marker lands somewhere the picture does not show. The bake reading the projection
                // rather than carrying its own copy is the same rule the retail baker follows.
                float wz = cz + (py / (float)(Res - 1) - 0.5f) * size;
                for (int px = 0; px < Res; px++)
                {
                    float wx = cx + (px / (float)(Res - 1) - 0.5f) * size;
                    float h = terr.SampleHeight(wx, wz);
                    Color c;
                    if (Terrain.HasWater && h < sea)
                    {
                        // Deeper water reads darker, which is what makes a coastline legible at a glance.
                        float d = Mathf.Clamp((sea - h) / 24f, 0f, 1f);
                        c = new Color(0.16f, 0.30f, 0.46f).Lerp(new Color(0.05f, 0.11f, 0.22f), d);
                    }
                    else
                    {
                        c = Terrain.LayerColor(terr.SampleDominantLayer(wx, wz));
                        // Relief shading off the real slope, lit from the north-west like every map ever drawn.
                        // Without it a flat-coloured splat map has no hills in it at all.
                        var n = terr.NormalAt(wx, wz);
                        float lit = Mathf.Clamp(0.55f + 0.45f * n.Dot(new Vector3(-0.55f, 0.72f, -0.42f).Normalized()), 0.35f, 1.25f);
                        c = new Color(c.R * lit, c.G * lit, c.B * lit);
                    }
                    img.SetPixel(px, py, c);
                }
            }

            // ---- the roads, drawn on top -----------------------------------------------------------------
            // Thicker than one pixel: at 1024 px over 3 km a road is a third of a pixel wide, which is a dotted
            // line at best. A map draws roads at a legible width rather than a true one.
            var road = new Color(0.93f, 0.88f, 0.72f);
            int drawn = 0;
            if (terr.IslandRoutes != null)
                foreach (var r in terr.IslandRoutes)
                {
                    if (r.Points == null) continue;
                    foreach (var pt in r.Points)
                    {
                        var w = ProcIslandSpawn.PosFor(terr, pt.X, pt.Y);
                        int ix = Mathf.RoundToInt(((w.X - cx) / size + 0.5f) * (Res - 1));
                        int iy = Mathf.RoundToInt((0.5f + (w.Z - cz) / size) * (Res - 1));
                        for (int oy = -1; oy <= 1; oy++)
                            for (int ox = -1; ox <= 1; ox++)
                            {
                                int qx = ix + ox, qy = iy + oy;
                                if (qx < 0 || qy < 0 || qx >= Res || qy >= Res) continue;
                                img.SetPixel(qx, qy, road);
                            }
                        drawn++;
                    }
                }

            // ---- and the towns, as filled pads -------------------------------------------------------------
            var townCol = new Color(0.78f, 0.74f, 0.66f);
            if (terr.IslandTiles != null)
                foreach (var t in terr.IslandTiles)
                {
                    var w = ProcIslandSpawn.PosFor(terr, t.X, t.Z);
                    int ix = Mathf.RoundToInt(((w.X - cx) / size + 0.5f) * (Res - 1));
                    int iy = Mathf.RoundToInt((0.5f + (w.Z - cz) / size) * (Res - 1));
                    int rad = Mathf.Max(1, Mathf.RoundToInt(ProcIsland.TileSize * 0.5f / size * Res));
                    for (int oy = -rad; oy <= rad; oy++)
                        for (int ox = -rad; ox <= rad; ox++)
                        {
                            int qx = ix + ox, qy = iy + oy;
                            if (qx < 0 || qy < 0 || qx >= Res || qy >= Res) continue;
                            img.SetPixel(qx, qy, townCol);
                        }
                }

            string name = $"island_{seed}_map.png";
            string path = ProjectSettings.GlobalizePath("res://content/" + name);
            var err = img.SavePng(path);
            if (err != Error.Ok) { Log.Err($"[island-map] save failed ({err}) -> {path}"); return null; }
            Log.Print($"[island-map] {Res}x{Res} map baked from data -> content/{name} "
                      + $"({size:0} m across, centre {cx:0},{cz:0}, {drawn} road point(s))");

            // Hand MapUI the image AND the frame. ⚠ Both, not just the image: a generated island lives in one
            // quadrant rather than centred on the origin, so without the centre every dot on the map is offset
            // by half the island.
            MapUI.IslandImage = name;
            MapUI.IslandSize = size;
            MapUI.MapCentre = new Vector2(cx, cz);
            return name;
        }
    }
}
