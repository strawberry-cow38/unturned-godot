using System.Diagnostics;
using Godot;

namespace UnturnedGodot
{
    /// <summary>A BAKED PHASE FIELD for the swell, so waves run toward the shore instead of in one fixed compass
    /// direction forever (master 2026-10-04: "bringing the waves to move towards the shore").
    ///
    /// ⚠⚠ NOT FROM THE SHORE FOAM, which was the suggested source. That foam is computed from `depth_tex`, the
    /// SCREEN depth buffer -- it is view-dependent, so a wave direction derived from it would SWING AS THE CAMERA
    /// TURNED. That is worse than a fixed direction: it reads as the sea reacting to you, which no amount of
    /// tuning fixes because it is not a tuning problem.
    ///
    /// ⭐ The seabed is the honest source. Waves refract toward shallower water, so the heading is the gradient of
    /// DEPTH, which is terrain data: stable, world-space, already loaded, and identical for every viewer.
    ///
    /// ⚠⚠⚠ WHAT THIS CLASS HANDS OUT IS A SCALAR PHASE, NOT A DIRECTION, AND THAT IS THE WHOLE FIX. The first
    /// version baked unit DIRECTIONS and let the shader build its sample basis from them. It visibly wrecked the
    /// sea -- master, looking at it: "whys it all scrunchy":
    ///
    ///     the swell samples noise along `u = dot(wp, d)`. With one global `d`, u is a clean linear ramp and the
    ///     wave's spatial frequency is exactly SWELL_FU. Let `d` vary with position and u stops being linear: its
    ///     gradient gains a `(grad d) . wp` term -- and `wp` is WORLD coordinates, thousands of metres. A
    ///     negligible change in heading gets multiplied by a ~2000 m lever arm and swamps SWELL_FU entirely, so
    ///     the wavelength collapses wherever the field turns.
    ///
    /// ⭐⭐ A SCALAR PHASE FIELD HAS NO BASIS TO ROTATE, so that failure is not available to it. Crests are the
    /// iso-contours of the phase, which come out parallel to the coast by construction. And where the field is
    /// imperfect the waves STRETCH rather than pinch -- the benign direction -- because an under-converged or
    /// non-integrable solve gives |grad phase| &lt; 1, never a 50x spike. The failure mode is chosen, not hoped for.
    ///
    /// ⭐ BAKED AS THE DIFFERENCE from the open-ocean ramp (`dot(wp, open)`), not as the phase itself. Offshore the
    /// correction is exactly 0, so the open sea is BIT-IDENTICAL to the sea before this feature existed, and the
    /// huge linear part is computed analytically in the shader instead of being interpolated out of a texture.
    ///
    /// ⭐⭐ COARSE ON PURPOSE. Refraction is a hundreds-of-metres effect, so a 32 m cell resolves it completely
    /// while keeping the whole field a few hundred KB -- small enough to hand the GPU as a texture AND keep on the
    /// CPU, which is the point: WaveField floats boats on this sea and must bend the same way the shader draws it.
    /// One baked array, two readers, no hand-kept twin to drift.</summary>
    public sealed class ShoreField
    {
        /// <summary>Metres per cell. ⚠ Coarse DELIBERATELY: a fine grid would resolve every rock and hand the
        /// swell a direction that changes faster than a wave could physically turn, which looks like noise rather
        /// than refraction.</summary>
        public const float CellSize = 32f;

        /// <summary>Depth (m) below which the shore direction fully takes over. Deeper than this the swell keeps
        /// its open-ocean heading, which is also what real swell does -- refraction is a SHALLOW-water effect.</summary>
        public const float ShoalDepth = 40f;

        /// <summary>SOR relaxation factor and sweep cap for the phase solve. ⚠ The cap is a cap, not a plan: the
        /// loop exits on the residual (see Converge) and reports which happened, because "it ran 4000 sweeps" and
        /// "it converged" are different facts and only one of them is good news.</summary>
        const float Omega = 1.9f;
        const int MaxSweeps = 4000;
        /// <summary>Metres of phase change per sweep below which the solve is done. 1 mm against a ~12 m
        /// wavelength -- four orders of magnitude under anything that could be seen.</summary>
        const float ConvergeM = 1e-3f;

        /// <summary>⚠ HOW FAR THE SHORE MAY TURN THE SWELL, degrees. Refraction bends a wave train; it does not
        /// reverse it. Without a cap, a lee shore whose "toward shallower" points back INTO the oncoming swell
        /// asks for a 180-degree reversal, and the least-squares field answers by putting a stagnation line
        /// through the middle of it -- |grad phase| near zero, which draws as a flat dead patch of sea. Capping
        /// the turn is the honest simplification: real swell wrapping right around an island is diffraction, and
        /// this is not a diffraction solver.</summary>
        const float MaxBendDeg = 70f;

        public float OriginX, OriginZ;
        public int Width, Height;
        Vector2[] _dir;          // unit vector toward SHALLOWER water (an INTERMEDIATE: see the class note)
        float[] _shore;          // 0 = open ocean, keep the global heading .. 1 = fully shore-aligned
        float[] _phase;          // METRES of phase correction: full phase = dot(wp, open) + _phase
        public ImageTexture Texture { get; private set; }

        public static ShoreField Active;

        /// <summary>The open-ocean heading, taken from the CPU twin so the bake, the shader and the buoyancy
        /// cannot hold three different ideas of which way the swell arrives from.</summary>
        static Vector2 OpenDir()
        {
            float a = Mathf.DegToRad(WaveField.SwellDirDeg);
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }

        /// <summary>Metres of phase correction at a world position: add to <c>dot(wp, open)</c> for the full swell
        /// phase. The CPU twin of the shader's texture read.
        ///
        /// ⚠⚠ BILINEAR, AND THAT IS NOT A QUALITY CHOICE -- it is what `texture()` with filter_linear does on the
        /// GPU. A nearest-neighbour read here would put the boats on a phase up to half a cell (16 m, more than a
        /// wavelength) away from the one being drawn, which is a hull bobbing out of step with the crest under
        /// it. Clamp-to-edge and the outside-the-rect zero mirror the sampler's `repeat_disable` and the shader's
        /// own bounds check, in that order.</summary>
        public float PhaseAt(float wx, float wz)
        {
            if (_phase == null) return 0f;
            float fx = (wx - OriginX) / CellSize, fz = (wz - OriginZ) / CellSize;
            if (fx < -0.5f || fz < -0.5f || fx > Width - 0.5f || fz > Height - 0.5f) return 0f;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0, tz = fz - z0;
            int xa = Mathf.Clamp(x0, 0, Width - 1), xb = Mathf.Clamp(x0 + 1, 0, Width - 1);
            int za = Mathf.Clamp(z0, 0, Height - 1), zb = Mathf.Clamp(z0 + 1, 0, Height - 1);
            float p00 = _phase[za * Width + xa], p10 = _phase[za * Width + xb];
            float p01 = _phase[zb * Width + xa], p11 = _phase[zb * Width + xb];
            return Mathf.Lerp(Mathf.Lerp(p00, p10, tx), Mathf.Lerp(p01, p11, tx), tz);
        }

        /// <summary>How shore-like this patch of sea is, 0..1 -- nearest-cell, because nothing steers on it; it is
        /// reported and carried in the texture's second channel for anything that wants to know it is inshore.</summary>
        public float ShorenessAt(float wx, float wz)
        {
            if (_shore == null) return 0f;
            int x = Mathf.Clamp(Mathf.RoundToInt((wx - OriginX) / CellSize), 0, Width - 1);
            int z = Mathf.Clamp(Mathf.RoundToInt((wz - OriginZ) / CellSize), 0, Height - 1);
            return _shore[z * Width + x];
        }

        /// <summary>Build the field from a terrain's heightmap. Called once, after the terrain exists.</summary>
        public static ShoreField Bake(Terrain t, float seaLevel, float worldMinX, float worldMinZ,
                                      float worldSizeX, float worldSizeZ)
        {
            var sw = Stopwatch.StartNew();
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
            f._phase = SolvePhase(f._dir, f._shore, w, h);
            f.Texture = MakeTexture(f._phase, f._shore, w, h);
            Active = f;
            f.Publish();
            Log.Print($"[shore] baked {w}x{h} cells ({CellSize:0} m) over {worldSizeX:0}x{worldSizeZ:0} m " +
                      $"in {sw.ElapsedMilliseconds} ms; {Coverage(f._shore):P0} of cells are shore-steered");
            f.Audit(depth);
            // ⭐ Name a few real coastal spots. Judging wave direction needs a camera AT a shore, and hunting one
            // by guessing world coordinates costs a render per guess -- the field already knows where they are.
            // ⚠ These are a NAVIGATION aid and nothing else. An earlier version of this bake reported the
            // shore-steered percentage as if it were good news; back when the steering was wrong, that figure was
            // measuring how much of the map had been WRECKED. The audit lines above are the quality measure.
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

        /// <summary>⭐⭐ THE PHASE SOLVE. Find R (metres) such that grad(dot(wp, open) + R) is the blended shore
        /// heading -- i.e. grad R = q, where q = steered_heading - open. A gradient field is generally NOT
        /// integrable, so this takes the least-squares R: minimise the integral of |grad R - q|^2, whose
        /// Euler-Lagrange equation is the Poisson equation lap(R) = div(q). Solved by SOR.
        ///
        /// ⭐ R = 0 EVERYWHERE IS BOTH THE INITIAL GUESS AND THE BOUNDARY CONDITION, and that is not laziness:
        /// q is exactly zero wherever shoreness is (all of the open ocean), so zero is already the exact solution
        /// there and the residual only lives near coasts. High-frequency, local residuals are what SOR eats
        /// fastest, and pinning the rect edge to 0 is what guarantees the far horizon keeps the open-ocean swell.
        ///
        /// ⭐ WHY THE FAILURE IS SAFE: least squares cannot make |grad R| large where q is small, so an
        /// under-converged or non-integrable patch gives |grad phase| a little under 1 -- waves slightly longer
        /// than nominal. The old formulation's failure was unbounded in the other direction. Audit() measures
        /// which of those actually happened rather than trusting this paragraph.</summary>
        static float[] SolvePhase(Vector2[] dir, float[] shore, int w, int h)
        {
            var sw = Stopwatch.StartNew();
            var open = OpenDir();
            var q = new Vector2[w * h];
            for (int i = 0; i < q.Length; i++)
                q[i] = Steer(open, dir[i], shore[i]) - open;

            var div = new float[w * h];
            for (int z = 1; z < h - 1; z++)
                for (int x = 1; x < w - 1; x++)
                    div[z * w + x] = (q[z * w + x + 1].X - q[z * w + x - 1].X) / (2f * CellSize)
                                   + (q[(z + 1) * w + x].Y - q[(z - 1) * w + x].Y) / (2f * CellSize);

            var r = new float[w * h];
            float hh = CellSize * CellSize;
            int sweeps = 0;
            float moved = 0f;
            for (; sweeps < MaxSweeps; sweeps++)
            {
                moved = 0f;
                for (int z = 1; z < h - 1; z++)
                    for (int x = 1; x < w - 1; x++)
                    {
                        int i = z * w + x;
                        float target = 0.25f * (r[i - 1] + r[i + 1] + r[i - w] + r[i + w] - hh * div[i]);
                        float delta = Omega * (target - r[i]);
                        r[i] += delta;
                        float ad = Mathf.Abs(delta);
                        if (ad > moved) moved = ad;
                    }
                if (moved < ConvergeM) { sweeps++; break; }
            }
            Log.Print($"[shore] phase solve: {sweeps} SOR sweeps in {sw.ElapsedMilliseconds} ms, " +
                      $"last sweep moved {moved:0.####} m " +
                      (moved < ConvergeM ? "(converged)" : $"⚠ (HIT THE {MaxSweeps}-SWEEP CAP, not converged)"));
            return r;
        }

        /// <summary>The heading the swell takes at one cell: the open heading turned toward the shore by the
        /// shoreness weight, capped at MaxBendDeg.
        ///
        /// ⭐ ROTATED BY A CAPPED ANGLE, not mixed and renormalised. The mix is only approximately a rotation, and
        /// it has a singularity: two near-opposite headings cancel to a zero vector that normalises to NaN and
        /// then propagates through the whole solve. A rotation is exact, always unit, and has no bad case.</summary>
        static Vector2 Steer(Vector2 open, Vector2 dir, float shoreness)
        {
            float ang = Mathf.Atan2(open.X * dir.Y - open.Y * dir.X, open.Dot(dir));   // signed, (-pi, pi]
            float cap = Mathf.DegToRad(MaxBendDeg);
            return open.Rotated(Mathf.Clamp(ang, -cap, cap) * Mathf.Clamp(shoreness, 0f, 1f));
        }

        /// <summary>⭐⭐ MEASURE THE WAVELENGTH THE FIELD ACTUALLY PRODUCES, because the thing that went wrong
        /// last time was invisible in every number this bake printed and obvious in a screenshot. |grad phase| is
        /// exactly the ratio of drawn wavelength to nominal: 1.0 is correct, 2.0 is waves half as long as they
        /// should be, 50 is the scrunch.
        ///
        /// ⭐ UG_SHORECHECK=1 ALSO MEASURES THE OLD, BROKEN FORMULA ON THIS SAME FIELD, as the control. The
        /// control is the point: if rotating the basis per position does NOT come out enormous here, then the
        /// diagnosis written at the top of this file is wrong and this measurement is vacuous. A test that cannot
        /// fail proves nothing, so it is given the one input it must fail on.</summary>
        void Audit(float[] depth)
        {
            var open = OpenDir();
            int n = 0; float mn = float.MaxValue, mx = 0f, sum = 0f;
            var all = new System.Collections.Generic.List<float>();
            for (int z = 0; z < Height; z++)
                for (int x = 0; x < Width; x++)
                {
                    if (depth[z * Width + x] <= 0.5f) continue;    // only where there is sea to draw
                    float wx = OriginX + x * CellSize, wz = OriginZ + z * CellSize;
                    float g = GradMag(wx, wz, (px, pz) => px * open.X + pz * open.Y + PhaseAt(px, pz));
                    n++; sum += g; all.Add(g);
                    if (g < mn) mn = g;
                    if (g > mx) mx = g;
                }
            if (n == 0) { Log.Print("[shore] audit: no water cells to measure"); return; }
            all.Sort();
            float P(float f) => all[Mathf.Clamp((int)(all.Count * f), 0, all.Count - 1)];
            int flat = 0, pinched = 0;
            foreach (var g in all) { if (g < 0.6f) flat++; else if (g > 1.6f) pinched++; }
            float rmn = float.MaxValue, rmx = -float.MaxValue;
            foreach (var v in _phase) { if (v < rmn) rmn = v; if (v > rmx) rmx = v; }
            Log.Print($"[shore] |grad phase| over {n} water cells: min {mn:0.00} p01 {P(0.01f):0.00} " +
                      $"mean {sum / n:0.00} p99 {P(0.99f):0.00} max {mx:0.00}   (1.00 = nominal wavelength)");
            Log.Print($"[shore]   outside a sane band: {(float)flat / n:P1} stretched past 0.6x, " +
                      $"{(float)pinched / n:P1} pinched past 1.6x; phase correction spans {rmn:0.0}..{rmx:0.0} m");

            if (System.Environment.GetEnvironmentVariable("UG_SHORECHECK") != "1") return;
            // ⭐⭐ THE CONTROL: the ABANDONED formulation -- u = dot(wp, steered_dir(wp)) -- measured with the same
            // instrument. It exists for no other purpose and nothing else calls it.
            //
            // ⚠⚠ IT SAMPLES THE DIRECTION FIELD **BILINEARLY**, AND THE FIRST VERSION OF THIS CONTROL DID NOT.
            // With nearest-cell sampling `d` is constant across the 2 m finite difference, so grad(d) is zero at
            // every cell centre and the control measured a flawless 1.00 everywhere -- it reported the diagnosis
            // as WRONG. What it had actually done was measure a formulation that never shipped: the shader read
            // this field through `texture()` with filter_linear. The shear lives entirely in the interpolation
            // the control had left out.
            float cmx = 0f, csum = 0f;
            for (int z = 0; z < Height; z++)
                for (int x = 0; x < Width; x++)
                {
                    if (depth[z * Width + x] <= 0.5f) continue;
                    float wx = OriginX + x * CellSize, wz = OriginZ + z * CellSize;
                    float g = GradMag(wx, wz, (px, pz) => { var d = DirBilinear(px, pz); return px * d.X + pz * d.Y; });
                    csum += g; if (g > cmx) cmx = g;
                }
            Log.Print($"[shore] CONTROL (the abandoned rotate-the-basis formula, same field, same instrument, " +
                      $"bilinear as the shader read it): mean {csum / n:0.00} max {cmx:0.00} -- waves up to " +
                      $"{cmx:0}x too short. " +
                      (cmx > 5f ? "⭐ the control fails as it must, so the figures above are not vacuous."
                                : "⚠⚠ CONTROL DID NOT FAIL -- either the diagnosis is wrong or this instrument is "
                                + "blind to the defect. DO NOT trust the figures above until it does."));
        }

        /// <summary>The steered heading, bilinearly interpolated -- i.e. exactly what the old shader's
        /// `texture(shore_dir_tex, uv).rg` returned. Only the control uses it.</summary>
        Vector2 DirBilinear(float wx, float wz)
        {
            var open = OpenDir();
            float fx = (wx - OriginX) / CellSize, fz = (wz - OriginZ) / CellSize;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0, tz = fz - z0;
            Vector2 At(int cx, int cz)
            {
                cx = Mathf.Clamp(cx, 0, Width - 1); cz = Mathf.Clamp(cz, 0, Height - 1);
                return Steer(open, _dir[cz * Width + cx], _shore[cz * Width + cx]);
            }
            var a = At(x0, z0).Lerp(At(x0 + 1, z0), tx);
            var b = At(x0, z0 + 1).Lerp(At(x0 + 1, z0 + 1), tx);
            var d = a.Lerp(b, tz);
            return d.LengthSquared() > 1e-6f ? d.Normalized() : open;
        }

        /// <summary>|grad f| by central difference over 2 m, one shared instrument so the new field and the
        /// control are not measured two different ways (agreement between two methods is not corroboration when
        /// the methods differ; here there is only one).</summary>
        static float GradMag(float wx, float wz, System.Func<float, float, float> f)
        {
            const float e = 1f;
            float gx = (f(wx + e, wz) - f(wx - e, wz)) / (2f * e);
            float gz = (f(wx, wz + e) - f(wx, wz - e)) / (2f * e);
            return Mathf.Sqrt(gx * gx + gz * gz);
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
        /// coordinates and looking like a bad bake rather than a bad transform.
        ///
        /// ⚠⚠ THE HALF-CELL SHIFT IS LOAD-BEARING AND WAS MISSING. `texture()` reads texel i at uv (i+0.5)/size,
        /// but cell i was BAKED at world origin + i*CellSize. Mapping the world origin to uv 0 therefore sampled
        /// the field half a cell -- 16 m, more than a swell wavelength -- from where it was measured. Pushing the
        /// rect origin back by half a cell makes uv (i+0.5)/size land exactly on cell i's world position.</summary>
        public void Publish()
        {
            RenderingServer.GlobalShaderParameterSet("shore_phase_tex", Texture);
            RenderingServer.GlobalShaderParameterSet("shore_rect",
                new Vector4(OriginX - CellSize * 0.5f, OriginZ - CellSize * 0.5f,
                            1f / (Width * CellSize), 1f / (Height * CellSize)));
        }

        /// <summary>A 1x1 "no coastline here" field: zero phase correction, zero shoreness. ⭐ Zero is the right
        /// default precisely because the baked value is a DIFFERENCE from the open-ocean ramp -- a global read
        /// before any bake has run gives the sea exactly as it was before this feature existed, rather than a
        /// hole or a flat plane.</summary>
        public static ImageTexture NeutralTexture()
        {
            var img = Image.CreateEmpty(1, 1, false, Image.Format.Rgf);
            img.SetPixel(0, 0, new Color(0f, 0f, 0f));
            return ImageTexture.CreateFromImage(img);
        }

        /// <summary>r = phase correction in METRES, g = shoreness. Float format, not 8-bit: the phase is metres
        /// of world distance, so quantising it would step the crests.</summary>
        static ImageTexture MakeTexture(float[] phase, float[] shore, int w, int h)
        {
            var img = Image.CreateEmpty(w, h, false, Image.Format.Rgf);
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    img.SetPixel(x, z, new Color(phase[z * w + x], shore[z * w + x], 0f));
            return ImageTexture.CreateFromImage(img);
        }
    }
}
