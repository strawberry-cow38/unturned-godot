using Godot;

namespace UnturnedGodot
{
    /// <summary>Which way the shore is, per patch of sea -- so swell can bend toward it instead of running in one
    /// fixed compass direction forever (master 2026-10-04: "bringing the waves to move towards the shore").
    ///
    /// ⚠⚠ NOT FROM THE SHORE FOAM, which was the suggested source. That foam is computed from `depth_tex`, the
    /// SCREEN depth buffer -- it is view-dependent, so wave direction derived from it would SWING AS THE CAMERA
    /// TURNED. That is worse than a fixed direction: it reads as the sea reacting to you, which no amount of
    /// tuning fixes because it is not a tuning problem.
    ///
    /// ⭐ The seabed is the honest source. Waves refract toward shallower water, so the direction is the gradient
    /// of DEPTH, which is terrain data: stable, world-space, already loaded, and identical for every viewer.
    ///
    /// ⭐⭐ BAKED COARSE, ON PURPOSE. Refraction is a hundreds-of-metres effect, so a 32 m cell resolves it
    /// completely while keeping the whole field a few hundred KB -- small enough to hand to the GPU as a texture
    /// AND keep on the CPU, which is the point: WaveField floats boats on this sea and must bend the same way the
    /// shader draws it. One baked array, two readers, no hand-kept twin to drift.</summary>
    public sealed class ShoreField
    {
        /// <summary>Metres per cell. ⚠ Coarse DELIBERATELY: a fine grid would resolve every rock and hand the
        /// swell a direction that changes faster than a wave could physically turn, which looks like noise rather
        /// than refraction.</summary>
        public const float CellSize = 32f;

        /// <summary>Depth (m) below which the shore direction fully takes over. Deeper than this the swell keeps
        /// its open-ocean heading, which is also what real swell does -- refraction is a SHALLOW-water effect.</summary>
        public const float ShoalDepth = 40f;

        public float OriginX, OriginZ;
        public int Width, Height;
        Vector2[] _dir;          // unit vector toward SHALLOWER water
        float[] _shore;          // 0 = open ocean, keep the global heading .. 1 = fully shore-aligned
        public ImageTexture Texture { get; private set; }

        public static ShoreField Active;

        /// <summary>Sample the baked direction + weight at a world position. The CPU twin of the shader's texture
        /// read, and deliberately the SAME array rather than a parallel copy.</summary>
        public void Sample(float wx, float wz, out Vector2 dir, out float shoreness)
        {
            dir = Vector2.Right; shoreness = 0f;
            if (_dir == null) return;
            float fx = (wx - OriginX) / CellSize, fz = (wz - OriginZ) / CellSize;
            int x = Mathf.Clamp(Mathf.RoundToInt(fx), 0, Width - 1);
            int z = Mathf.Clamp(Mathf.RoundToInt(fz), 0, Height - 1);
            int i = z * Width + x;
            dir = _dir[i]; shoreness = _shore[i];
        }

        /// <summary>Build the field from a terrain's heightmap. Called once, after the terrain exists.</summary>
        public static ShoreField Bake(Terrain t, float seaLevel, float worldMinX, float worldMinZ,
                                      float worldSizeX, float worldSizeZ)
        {
            var f = new ShoreField
            {
                OriginX = worldMinX, OriginZ = worldMinZ,
                Width = Mathf.Max(2, Mathf.CeilToInt(worldSizeX / CellSize)),
                Height = Mathf.Max(2, Mathf.CeilToInt(worldSizeZ / CellSize)),
            };
            int w = f.Width, h = f.Height;
            var depth = new float[w * h];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    float wx = worldMinX + x * CellSize, wz = worldMinZ + z * CellSize;
                    // Positive under water, negative on land. Land is kept as negative rather than clamped: the
                    // GRADIENT across the waterline is what points shoreward, and clamping it to zero would
                    // flatten exactly the cells that carry the signal.
                    depth[z * w + x] = seaLevel - t.SampleHeight(wx, wz);
                }

            f._dir = new Vector2[w * h];
            f._shore = new float[w * h];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    int xm = Mathf.Max(x - 1, 0), xp = Mathf.Min(x + 1, w - 1);
                    int zm = Mathf.Max(z - 1, 0), zp = Mathf.Min(z + 1, h - 1);
                    // Central difference of DEPTH. Waves travel toward shallower water, i.e. DOWN the depth
                    // gradient -- hence the negation.
                    float gx = (depth[z * w + xp] - depth[z * w + xm]) / (2f * CellSize);
                    float gz = (depth[zp * w + x] - depth[zm * w + x]) / (2f * CellSize);
                    var g = new Vector2(-gx, -gz);
                    float slope = g.Length();
                    int i = z * w + x;
                    f._dir[i] = slope > 1e-5f ? g / slope : Vector2.Right;

                    // ⚠⚠ TWO CONDITIONS, NOT ONE. A direction is only trustworthy where the seabed is BOTH
                    // shallow (refraction actually happens) and SLOPING (there is a direction to speak of). Flat
                    // deep ocean has a gradient of numerical noise, and steering the swell by that would make the
                    // open sea wander. Either test alone lets that through.
                    float d = depth[i];
                    float shallow = 1f - Mathf.Clamp(d / ShoalDepth, 0f, 1f);   // 1 at the waterline -> 0 by 40 m down
                    float sloped = Mathf.Clamp(slope / 0.05f, 0f, 1f);          // 5 cm per metre is a real beach
                    f._shore[i] = shallow * sloped * Mathf.Clamp(d / 2f, 0f, 1f);   // ...and nothing on dry land
                }

            Smooth(f._dir, f._shore, w, h);
            f.Texture = MakeTexture(f._dir, f._shore, w, h);
            Active = f;
            f.Publish();
            Log.Print($"[shore] baked {w}x{h} cells ({CellSize:0} m) over {worldSizeX:0}x{worldSizeZ:0} m; " +
                      $"{Coverage(f._shore):P0} of cells are shore-steered");
            // ⭐ Name a few real coastal spots. Judging wave direction needs a camera AT a shore, and hunting one
            // by guessing world coordinates is a render per guess -- the field already knows where they are.
            int shown = 0;
            for (int z = 0; z < h && shown < 4; z += Mathf.Max(1, h / 7))
                for (int x = 0; x < w && shown < 4; x += Mathf.Max(1, w / 7))
                {
                    int i = z * w + x;
                    if (f._shore[i] < 0.55f) continue;
                    Log.Print($"[shore]   coast at world ({worldMinX + x * CellSize:0}, {worldMinZ + z * CellSize:0}) " +
                              $"-> waves run ({f._dir[i].X:0.00}, {f._dir[i].Y:0.00}), weight {f._shore[i]:0.00}");
                    shown++;
                }
            return f;
        }

        static float Coverage(float[] s)
        {
            int n = 0; foreach (var v in s) if (v > 0.05f) n++;
            return s.Length == 0 ? 0f : (float)n / s.Length;
        }

        /// <summary>⚠ A FEW PASSES, because a wave cannot turn a corner. The raw gradient changes direction
        /// abruptly at a headland, and swell following it exactly would kink -- which reads as a seam in the
        /// water. Blurring the FIELD is how the bend becomes gradual, and it costs nothing at this resolution.</summary>
        static void Smooth(Vector2[] dir, float[] shore, int w, int h, int passes = 3)
        {
            var d2 = new Vector2[dir.Length]; var s2 = new float[shore.Length];
            for (int p = 0; p < passes; p++)
            {
                for (int z = 0; z < h; z++)
                    for (int x = 0; x < w; x++)
                    {
                        Vector2 acc = Vector2.Zero; float sacc = 0f; int n = 0;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = Mathf.Clamp(x + dx, 0, w - 1), zz = Mathf.Clamp(z + dz, 0, h - 1);
                                acc += dir[zz * w + xx]; sacc += shore[zz * w + xx]; n++;
                            }
                        // ⚠ Averaged as VECTORS and renormalised, never as angles: averaging 350 deg and 10 deg
                        // as numbers gives 180 -- the exact opposite heading. Same circular trap that has bitten
                        // this project in three unrelated places.
                        var a = acc / n;
                        d2[z * w + x] = a.LengthSquared() > 1e-8f ? a.Normalized() : dir[z * w + x];
                        s2[z * w + x] = sacc / n;
                    }
                System.Array.Copy(d2, dir, dir.Length);
                System.Array.Copy(s2, shore, shore.Length);
            }
        }

        /// <summary>Hand the field to the GPU. ⚠ The RECT goes with the texture, always: a UV mapping and the
        /// data it indexes are one fact, and setting them apart is how a field ends up sampled at the wrong
        /// coordinates and looking like a bad bake rather than a bad transform.</summary>
        public void Publish()
        {
            RenderingServer.GlobalShaderParameterSet("shore_dir_tex", Texture);
            RenderingServer.GlobalShaderParameterSet("shore_rect",
                new Vector4(OriginX, OriginZ, 1f / (Width * CellSize), 1f / (Height * CellSize)));
        }

        /// <summary>A 1x1 "no coastline here" field: direction +X, weight 0. ⭐ The weight is what matters -- the
        /// shader blends toward the shore direction BY this value, so zero means the swell keeps its open-ocean
        /// heading and the sea looks exactly as it did before this feature existed. That is the right default for
        /// a global that may be read before any bake has run.</summary>
        public static ImageTexture NeutralTexture()
        {
            var img = Image.CreateEmpty(1, 1, false, Image.Format.Rgbf);
            img.SetPixel(0, 0, new Color(1f, 0f, 0f));
            return ImageTexture.CreateFromImage(img);
        }

        static ImageTexture MakeTexture(Vector2[] dir, float[] shore, int w, int h)
        {
            var img = Image.CreateEmpty(w, h, false, Image.Format.Rgbf);
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    var d = dir[z * w + x];
                    // Float format, so the direction is stored as-is: an 8-bit encode quantises the heading to
                    // ~1.4 degrees, and a swell that steps between headings is the artifact being removed.
                    img.SetPixel(x, z, new Color(d.X, d.Y, shore[z * w + x]));
                }
            return ImageTexture.CreateFromImage(img);
        }
    }
}
