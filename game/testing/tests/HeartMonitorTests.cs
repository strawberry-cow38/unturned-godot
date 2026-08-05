using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE PATIENT MONITOR (strawberry: "change the science 3 prop to show an actual pattern, with the same amplitude
    // as the vanilla one, turns on/off like a tv. it draws the same ecg every x seconds... also add a flag for
    // flatline vs on").
    //
    // "Same amplitude as the vanilla one" is the claim with a right answer, and it is not a texture: the prop's
    // palette is 2x2, so the vanilla zigzag is twelve real VERTICES lying 2.1 mm proud of the screen face. Those
    // twelve are the specification, so this suite reads them back out of the shipped .obj and checks the shader's
    // constants against them. A tolerance on a number nobody measured would just be a second guess agreeing with the
    // first.
    public sealed class HeartMonitorTests : GameTest
    {
        public override string Name => "props.heart_monitor";
        public override double TimeoutSimSeconds => 30;

        static readonly Transform3D StandUp = new(Basis.FromEuler(new Vector3(-Mathf.Pi * 0.5f, 0f, 0f)), Vector3.Zero);

        static string ReadText(string resPath)
        {
            try { string p = ProjectSettings.GlobalizePath(resPath); return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : ""; }
            catch { return ""; }
        }

        /// <summary>The vanilla drawn trace, straight out of the prop: every vertex sitting on the raised trace plane.
        /// Read from the .obj rather than hardcoded, so the day someone re-rips the prop this suite notices.</summary>
        static List<Vector3> VanillaTrace()
        {
            var outp = new List<Vector3>();
            string path = ProjectSettings.GlobalizePath("res://content/objects/Science_3.obj");
            if (!System.IO.File.Exists(path)) return outp;
            foreach (var line in System.IO.File.ReadLines(path))
            {
                if (!line.StartsWith("v ")) continue;
                var p = line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4) continue;
                var v = new Vector3(float.Parse(p[1]), float.Parse(p[2]), float.Parse(p[3]));
                if (Mathf.Abs(v.Y - HeartMonitor.TraceY) < 0.0005f) outp.Add(v);
            }
            return outp;
        }

        public override IEnumerable<Step> Run()
        {
            // ---- THE VANILLA TRACE IS WHERE WE THINK IT IS.
            var trace = VanillaTrace();
            T.Check($"found the prop's drawn ECG in the mesh ({trace.Count} verts on the trace plane)", trace.Count >= 12);
            if (trace.Count >= 12)
            {
                // CENTRELINES on both sides. The vanilla trace is a RIBBON: every point is a pair of vertices about
                // 45 mm apart, and at the spike the mitre pushes them further apart still. Reading min/max Z gives the
                // ribbon's outer edges, which overstates the amplitude by ~12% -- close enough to look like a
                // judgement call and simply wrong. The peak's centreline is the midpoint of the two vertices that
                // bound it, so that is what gets compared to the shader's centreline constants.
                var zs = new List<float>();
                foreach (var v in trace)
                {
                    // DISTINCT heights. The .obj lists a position once per face corner that uses it, so the peak
                    // vertex appears several times -- and averaging the "top two" of a list with duplicates in it just
                    // returns the peak again, which reads as the ribbon edge and silently reintroduces the 12% error
                    // this whole block exists to avoid.
                    bool seen = false;
                    foreach (var z in zs) if (Mathf.Abs(z - v.Z) < 1e-4f) { seen = true; break; }
                    if (!seen) zs.Add(v.Z);
                }
                zs.Sort();
                T.Check($"the trace has distinct heights to measure ({zs.Count})", zs.Count >= 6);
                float hi = (zs[zs.Count - 1] + zs[zs.Count - 2]) * 0.5f;   // R peak, centreline
                float lo = (zs[0] + zs[1]) * 0.5f;                          // Q trough, centreline
                float h = HeartMonitor.ScreenZ1 - HeartMonitor.ScreenZ0;
                string src = ReadText("res://content/ecg.gdshader");
                T.Check($"the ECG shader was readable ({src.Length} chars)", src.Length > 400);

                float baseline = 0f, peak = 0f, dip = 0f, lw = 0f, scale = 1f;
                foreach (var l in src.Split('\n'))
                {
                    var t = l.Trim();
                    if (t.StartsWith("const float BASELINE")) baseline = ParseConst(t);
                    else if (t.StartsWith("const float VANILLA_SCALE")) scale = ParseConst(t);
                    else if (t.StartsWith("const float R_PEAK")) peak = ParseFactor(t);
                    else if (t.StartsWith("const float Q_DIP")) dip = ParseFactor(t);
                    else if (t.StartsWith("const float LINE_W")) lw = ParseConst(t);
                }
                peak *= scale; dip *= scale;
                T.Check($"...and its four proportions parsed (base {baseline:0.###}, R {peak:0.###}, Q {dip:0.###}, w {lw:0.###})",
                    baseline > 0f && peak > 0f && dip > 0f && lw > 0f);

                // Measured, in screen-height fractions, from the mesh:
                float meshTop = (hi - HeartMonitor.ScreenZ0) / h;
                float meshBot = (lo - HeartMonitor.ScreenZ0) / h;
                float shaderTop = baseline + peak;
                float shaderBot = baseline - dip;
                // Deliberately 80% of the measured amplitude, not 100% (strawberry: "the amplitude looked like its
                // more. lower it by like 20%"). The measurement is still the anchor -- the check is that the shader is
                // that fraction OF the mesh, so the vanilla numbers remain the thing everything is expressed against
                // and a drift in either the mesh or the constants still fails. Deleting the comparison and hardcoding
                // 0.286 would have passed just as well and pinned nothing.
                float meshPeak = meshTop - (baseline);      // vanilla amplitude above the baseline
                float meshDipA = baseline - meshBot;
                T.Check($"the shader's scale factor was found ({scale:0.##})", scale > 0.5f && scale <= 1f);
                T.Check($"the R spike is {scale:0.##} of the vanilla amplitude ({peak:0.###} vs {meshPeak * scale:0.###})",
                    Mathf.Abs(peak - meshPeak * scale) < 0.02f);
                T.Check($"...and the Q dip likewise ({dip:0.###} vs {meshDipA * scale:0.###})",
                    Mathf.Abs(dip - meshDipA * scale) < 0.02f);
                T.Check($"...so the trace is visibly shorter than vanilla, on purpose ({shaderTop - shaderBot:0.###} vs mesh {meshTop - meshBot:0.###})",
                    (shaderTop - shaderBot) < (meshTop - meshBot));
                // TEETH: the screen is much taller than the trace, so "fills the screen" would ALSO sit inside a loose
                // tolerance. Pin that the trace does not fill it -- the vanilla one occupies about 53% centreline.
                T.Check($"...and it is NOT simply the whole screen ({meshTop - meshBot:0.###} of it)",
                    (meshTop - meshBot) < 0.7f);
                // The LINE WIDTH is deliberately NOT the vanilla ribbon's. Amplitude was the ask; the ribbon is 0.076
                // of the screen and drawn that thick the spike blurs into a blob at real on-screen size. Stated as its
                // own check so the divergence is a decision on the record rather than a number that drifted.
                T.Check($"the drawn line is thinner than the vanilla ribbon, on purpose ({lw:0.###} vs 0.076)",
                    lw > 0.02f && lw < 0.076f);
            }

            // ---- THE OVERLAY CLEARS THE DRAWN TRACE. It has to cover geometry that is itself proud of the screen; an
            // offset measured from the SCREEN would sit behind the zigzag and z-fight with it.
            T.Check($"the overlay sits in front of the vanilla trace, not just the screen ({(HeartMonitor.TraceY + HeartMonitor.OverlayGap) - HeartMonitor.TraceY:0.####} m clear)",
                HeartMonitor.OverlayGap > 0f && HeartMonitor.TraceY > HeartMonitor.ScreenY);

            // ---- LIVE.
            bool gridWas = PowerNet.GlobalPower;
            PowerNet.SetGlobalPower(true);
            var mesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Science_3.obj"));
            T.Check("Science_3.obj loads", mesh != null);
            if (mesh == null) { PowerNet.SetGlobalPower(gridWas); yield break; }
            var mi = new MeshInstance3D { Mesh = mesh, Transform = StandUp };
            World.AddChild(mi);
            var hm = HeartMonitor.Make(mi, alive: true);
            World.AddChild(hm);
            yield return Ticks(4);

            T.Check("a powered monitor is lit", hm.DebugLit);
            T.Check("...and its screen is showing", hm.DebugScreen != null && hm.DebugScreen.Visible);
            T.Check($"...beating, not flatlined ({hm.Alive})", hm.Alive);
            T.Check($"...at a resting rate ({60f / hm.DebugPeriod:0} bpm)", hm.DebugPeriod > 0.5f && hm.DebugPeriod < 1.5f);

            // The sweep advances: time_s must actually be driven, or the trace is a still picture that happens to be
            // the right shape -- which is exactly what "shows a pattern" would look like if _Process never ran.
            float t0 = (float)hm.DebugMaterial.GetShaderParameter("time_s");
            yield return Ticks(25);
            float t1 = (float)hm.DebugMaterial.GetShaderParameter("time_s");
            T.Check($"the trace is animated, not a still ({t0:0.###} -> {t1:0.###})", t1 > t0 + 0.2f);

            // ---- THE FLATLINE FLAG. Both halves: the shader is told, and the period changes with it.
            hm.SetAlive(false);
            yield return Ticks(2);
            T.Check("flatline flag reaches the shader",
                Mathf.IsZeroApprox((float)hm.DebugMaterial.GetShaderParameter("alive")));
            T.Check($"...and a flatline sweeps at its own rate ({hm.DebugPeriod:0.##}s vs {HeartMonitor.BeatPeriod:0.##}s alive)",
                !Mathf.IsEqualApprox(hm.DebugPeriod, HeartMonitor.BeatPeriod));
            hm.SetAlive(true);
            yield return Ticks(2);
            T.Check("...and it comes back", Mathf.IsEqualApprox((float)hm.DebugMaterial.GetShaderParameter("alive"), 1f));

            // ---- ON/OFF LIKE A TV, and the same two-source power gate.
            hm.Toggle();
            yield return Ticks(2);
            T.Check("switching it off darkens the screen", !hm.DebugLit && !hm.DebugScreen.Visible);
            hm.Toggle();
            yield return Ticks(2);
            T.Check("...and back on relights it", hm.DebugLit);

            PowerNet.SetGlobalPower(false);
            yield return Ticks(4);
            T.Check("a blackout takes it out", !hm.DebugLit);
            PowerNet.SetGlobalPower(true);
            yield return Ticks(4);
            T.Check("...and it returns with the grid", hm.DebugLit);

            PowerNet.SetGlobalPower(gridWas);
            yield break;
        }

        /// <summary>Parse `const float X = 0.358 * SCALE;` -- the leading factor only. The scale is read separately and
        /// applied by the caller, so the vanilla measurement stays visible in the source as its own number rather than
        /// being pre-multiplied into something nobody can trace back to the mesh.</summary>
        static float ParseFactor(string line)
        {
            int eq = line.IndexOf('=');
            int semi = line.IndexOf(';', eq + 1);
            if (eq < 0 || semi < 0) return 0f;
            var body = line[(eq + 1)..semi];
            int star = body.IndexOf('*');
            if (star >= 0) body = body[..star];
            return float.TryParse(body.Trim(), out var v) ? v : 0f;
        }

        static float ParseConst(string line)
        {
            int eq = line.IndexOf('=');
            int semi = line.IndexOf(';', eq + 1);
            if (eq < 0 || semi < 0) return 0f;
            return float.TryParse(line[(eq + 1)..semi].Trim(), out var v) ? v : 0f;
        }
    }
}
