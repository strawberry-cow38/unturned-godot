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

        /// <summary>A second monitor, standing somewhere else. Placed apart from the first because two of these at the
        /// origin is a collision, not a test rig.</summary>
        HeartMonitor Spawn(Vector3 at, bool alive)
        {
            var m = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Science_3.obj"));
            var mi = new MeshInstance3D { Mesh = m, Transform = new Transform3D(StandUp.Basis, at) };
            World.AddChild(mi);
            var hm = HeartMonitor.Make(mi, alive);
            World.AddChild(hm);
            return hm;
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
            T.Check("...and its screen is showing the live trace",
                hm.DebugScreen != null && hm.DebugScreen.Visible && hm.DebugScreen.MaterialOverride == hm.DebugMaterial);
            T.Check($"...beating, not flatlined ({hm.Alive})", hm.Alive);
            T.Check($"...at a hibernating rate ({60f / hm.DebugPeriod:0} bpm)", Mathf.IsEqualApprox(60f / hm.DebugPeriod, 60f));

            // The sweep advances: time_s must actually be driven, or the trace is a still picture that happens to be
            // the right shape -- which is exactly what "shows a pattern" would look like if _Process never ran.
            //
            // Compared MODULO the period, because time_s is now a sawtooth rather than an ever-growing accumulator:
            // it is handed to the shader pre-wrapped so the beat cannot decay as the session lengthens. A plain
            // t1 > t0 test passes or fails on where in the cycle the sample happened to land.
            float per0 = hm.DebugPeriod;
            float t0 = (float)hm.DebugMaterial.GetShaderParameter("time_s");
            yield return Ticks(25);
            float t1 = (float)hm.DebugMaterial.GetShaderParameter("time_s");
            float adv = Mathf.PosMod(t1 - t0, per0);
            T.Check($"the trace is animated, not a still ({t0:0.###} -> {t1:0.###}, advanced {adv:0.###}s of {per0:0.#})",
                adv > 0.05f && adv < per0 * 0.95f);
            T.Check($"...and it never leaves its own period ({t1:0.###} in 0..{per0:0.#})", t1 >= 0f && t1 < per0);

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
            T.Check("switching it off darkens the screen", !hm.DebugLit && hm.DebugOffMaterial != null
                && hm.DebugScreen.MaterialOverride == hm.DebugOffMaterial);
            // ...and it must still be DRAWN. Hiding it uncovers the prop's own modelled green ECG, which is what a
            // dead monitor was showing before -- the failure looked exactly like "the screen went off" until you
            // noticed the trace was still there.
            T.Check("...while still COVERING the vanilla trace", hm.DebugScreen.Visible);

            // THE OFF COLOUR IS THE PROP'S OWN, read back out of the palette rather than trusted. Science_3's texture
            // is 2x2, and the screen face's UVs land on texel (1,0) -- so that texel IS the answer, and a hardcoded
            // constant that drifted from the asset would otherwise look perfectly reasonable.
            var pal = Image.LoadFromFile(ProjectSettings.GlobalizePath("res://content/objects/Science_3_tex.png"));
            T.Check($"the prop's palette loaded ({pal?.GetWidth()}x{pal?.GetHeight()})", pal != null && pal.GetWidth() == 2);
            if (pal != null && pal.GetWidth() == 2)
            {
                var screenTexel = pal.GetPixel(1, 0);
                var off = hm.DebugOffMaterial.AlbedoColor;
                T.Check($"...and the dark screen IS its screen texel (off {off.R:0.###},{off.G:0.###},{off.B:0.###} vs texel {screenTexel.R:0.###},{screenTexel.G:0.###},{screenTexel.B:0.###})",
                    Mathf.Abs(off.R - screenTexel.R) < 0.01f && Mathf.Abs(off.G - screenTexel.G) < 0.01f && Mathf.Abs(off.B - screenTexel.B) < 0.01f);
                // TEETH: and it is NOT black, which is the thing that was explicitly rejected for every screen type.
                T.Check($"...and is not perfect black ({off.R:0.###})", off.R > 0.05f);

                // ...and the LIT shader's background is the SAME texel. Two hand-typed copies of one colour is how the
                // screen ends up changing shade as it powers on, which is the thing this replaced.
                string esrc = ReadText("res://content/ecg.gdshader");
                float bg = 0f;
                foreach (var l in esrc.Split('\n'))
                    if (l.Trim().StartsWith("const vec3 SCREEN_BG"))
                    {
                        int a = l.IndexOf('('), b = l.IndexOf(',');
                        if (a >= 0 && b > a) float.TryParse(l.Substring(a + 1, b - a - 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out bg);
                    }
                T.Check($"the shader's SCREEN_BG parsed ({bg:0.####})", bg > 0f);
                // COMPARED IN LINEAR. This check used to put the shader constant next to the raw texel and pass when
                // they were the same number -- which is precisely the bug: AlbedoColor is linearised by Godot and a
                // shader's ALBEDO is not, so the SAME colour must be written as two different numbers. Comparing the
                // numbers agreed that a washed-out mid grey was the model's dark screen. Converting first is what
                // makes the check mean "same colour" rather than "same digits".
                float wantLinear = screenTexel.SrgbToLinear().R;
                T.Check($"...and the lit background IS the model's screen texel, in linear ({bg:0.####} vs {wantLinear:0.####}; the sRGB digits are {screenTexel.R:0.###})",
                    Mathf.Abs(bg - wantLinear) < 0.005f);
                // TEETH: and it must NOT be the raw sRGB number, which is the mistake that shipped.
                T.Check($"...not the sRGB digits written straight in ({bg:0.####} vs {screenTexel.R:0.###})",
                    Mathf.Abs(bg - screenTexel.R) > 0.05f);
            }
            hm.Toggle();
            yield return Ticks(2);
            T.Check("...and back on relights it", hm.DebugLit);

            PowerNet.SetGlobalPower(false);
            yield return Ticks(4);
            T.Check("a blackout takes it out", !hm.DebugLit);
            PowerNet.SetGlobalPower(true);
            yield return Ticks(4);
            T.Check("...and it returns with the grid", hm.DebugLit);

            // ---- THE AUDIO IS A REAL RECORDING, cut by measurement (strawberry: "the beep tones are very soft and
            // generic"; VoX: source a royalty-free effect and pull the pieces out with amplitude analysis).
            //
            // What is checkable here is that the assets EXIST, LOAD, and carry the right loop flag -- and the last of
            // those is the one that matters, because a flatline that does not loop plays once and leaves a silent
            // monitor showing a flat trace, which reads as the sound being broken rather than absent. Whether it
            // sounds right is a listening judgement and was made by ear on a posted preview.
            //
            // Loaded through the DEVICE's own loader, not through GD.Load. These files were dropped into content/ and
            // never went through the editor, so they have no .import sidecar and the res:// route returns null for
            // them -- which is precisely the bug this pass fixed, and testing the route the game does not use would
            // have re-passed while the monitor stayed silent.
            string beepFile = ProjectSettings.GlobalizePath(HeartMonitor.BeepPath);
            string flatFile = ProjectSettings.GlobalizePath(HeartMonitor.FlatPath);
            T.Check($"the beep asset is present ({beepFile})", System.IO.File.Exists(beepFile));
            T.Check($"the flatline asset is present ({flatFile})", System.IO.File.Exists(flatFile));
            var beepRes = HeartMonitor.LoadClip(HeartMonitor.BeepPath, false);
            var flatRes = HeartMonitor.LoadClip(HeartMonitor.FlatPath, true);
            T.Check($"the beep loads as audio ({beepRes?.GetType().Name})", beepRes != null);
            T.Check($"the flatline loads as audio ({flatRes?.GetType().Name})", flatRes != null);
            if (beepRes != null) T.Check($"the beep is short, like a blip ({beepRes.GetLength():0.###}s)",
                beepRes.GetLength() > 0.05 && beepRes.GetLength() < 0.6);
            if (flatRes != null) T.Check($"the flatline is a loopable body ({flatRes.GetLength():0.###}s)",
                flatRes.GetLength() > 0.2);
            // THE LOOP FLAG, which is the one that decides whether a flatline holds or stops after 600 ms. Asserted on
            // both, because a loader that ignored the argument and always looped would pass a one-sided check and turn
            // every heartbeat into a drone.
            var flatWav = flatRes as AudioStreamWav;
            T.Check($"the flatline is a WAV with real loop points ({flatRes?.GetType().Name})", flatWav != null);
            T.Check($"...set to loop forward ({flatWav?.LoopMode})",
                flatWav != null && flatWav.LoopMode == AudioStreamWav.LoopModeEnum.Forward);
            // The loop starts AFTER a lead-in, not at sample 0: the clip opens with a ramp up from silence so that
            // switching a flatlined monitor on is not a click, and that ramp must play once rather than every wrap.
            // Read from the file's own `smpl` chunk -- a LoopBegin of 0 here means the chunk was not parsed and the
            // fade-in is being replayed 1.6 times a second.
            T.Check($"...from a loop point the ASSET carries ({flatWav?.LoopBegin}..{flatWav?.LoopEnd} of {flatWav?.Data.Length / 2})",
                flatWav != null && flatWav.LoopBegin > 0 && flatWav.LoopEnd == flatWav.Data.Length / 2);
            T.Check($"...and the clip opens at SILENCE, so power-on does not click ({(flatWav != null ? (short)(flatWav.Data[0] | (flatWav.Data[1] << 8)) : -1)})",
                flatWav != null && Mathf.Abs((short)(flatWav.Data[0] | (flatWav.Data[1] << 8))) < 200);
            T.Check($"...and the blip does NOT loop ({(beepRes as AudioStreamOggVorbis)?.Loop})",
                beepRes is AudioStreamOggVorbis bo && !bo.Loop);

            // ---- THE LOOP SEAM, measured on the SHIPPED BYTES.
            //
            // This is the check whose absence let an audible tick ship. The flatline was a crossfaded .ogg, and the
            // crossfade was right in PCM and then wrecked by the encoder: Vorbis is a lapped transform and gave back
            // 13312 frames for the 13230 fed in, so the real wrap sat 82 samples from the faded join and stepped 35%
            // of full scale. The check that "verified" it read frame 13230 -- an ordinary interior sample -- and
            // reported a clean number, which is how a wrong offset agrees with the bug.
            //
            // So: measure the ACTUAL last->first step, and judge it against the waveform's OWN step distribution
            // rather than against a tolerance somebody picked. A loop is inaudible when its join is indistinguishable
            // from ordinary motion of the wave, and that is a property the wave itself defines.
            if (flatWav != null && flatWav.Data.Length >= 4)
            {
                int n = flatWav.Data.Length / 2;
                short S(int i) => (short)(flatWav.Data[i * 2] | (flatWav.Data[i * 2 + 1] << 8));
                var steps = new List<int>(n);
                for (int i = 0; i + 1 < n; i++) steps.Add(Mathf.Abs(S(i + 1) - S(i)));
                steps.Sort();
                int p95 = steps[(int)(steps.Count * 0.95f)];
                // Measured at the ACTUAL wrap -- last sample back to LoopBegin, not to sample 0. Reading the wrong
                // offset is the entire original bug, so this deliberately uses the same LoopBegin the engine will.
                int seam = Mathf.Abs(S(flatWav.LoopBegin) - S(flatWav.LoopEnd - 1));
                T.Check($"the loop seam is inside the wave's own motion (seam {seam} vs p95 {p95}, {seam / (float)p95:0.00}x)",
                    seam <= p95);
                // ...and the lead-in must hand over to the body continuously too, or the fade-in ends in a step of
                // its own the first time it plays.
                int handover = Mathf.Abs(S(flatWav.LoopBegin) - S(flatWav.LoopBegin - 1));
                T.Check($"...and the lead-in joins the body cleanly ({handover} vs p95 {p95})", handover <= p95);
                // TEETH: p95 must be a real number off a real waveform. A silent or constant buffer would make the
                // check above pass trivially -- 0 <= 0 -- and prove nothing at all.
                T.Check($"...and that distribution came from an actual signal (p95 {p95}, peak {S(n / 2)})", p95 > 100);

                // ---- STEADINESS, which no seam check can see.
                //
                // The second bug in this asset had a PERFECT seam and still pulsed: an equal-power crossfade of two
                // in-phase copies of an 880 Hz tone sums to 1.414, so the level bulged +3.01 dB over 50 ms, once per
                // loop. Continuity at the join and constancy across the body are different properties, and a tone
                // that wobbles is the one a listener actually complains about.
                int blk = flatWav.MixRate / 100;   // 10 ms
                int lo = int.MaxValue, hi = 0;
                for (int b0 = flatWav.LoopBegin; b0 + blk <= flatWav.LoopEnd; b0 += blk)
                {
                    int pk = 0;
                    for (int i = b0; i < b0 + blk; i++) pk = Mathf.Max(pk, Mathf.Abs(S(i)));
                    lo = Mathf.Min(lo, pk); hi = Mathf.Max(hi, pk);
                }
                T.Check($"the flatline tone holds a steady level ({lo}..{hi}, spread {100f * (hi - lo) / Mathf.Max(hi, 1):0.00}%)",
                    hi > 0 && (hi - lo) <= hi * 0.05f);

                // ...and it loops on a WHOLE number of cycles at the measured fundamental, which is WHY the wrap is
                // exact rather than merely small. A body that is a fraction of a cycle out still measures a tolerable
                // seam and then drifts in phase, which is audible as a chirp across repeats.
                float cycles = HeartMonitor.SourceHz * (flatWav.LoopEnd - flatWav.LoopBegin) / flatWav.MixRate;
                T.Check($"...over a whole number of {HeartMonitor.SourceHz:0} Hz cycles ({cycles:0.####})",
                    Mathf.Abs(cycles - Mathf.Round(cycles)) < 0.001f);
            }

            // ...AND THE MONITOR ACTUALLY PLAYS ONE. The two claims above -- the file is there, the file loads -- were
            // both true of the version that sat on the ward in total silence, because the device loaded via res://
            // and got null. So run a live lit monitor past its own R spike and check something reached the player.
            var sound = Spawn(new Vector3(9f, 0f, 0f), alive: true);
            var flatUnit = Spawn(new Vector3(11f, 0f, 0f), alive: false);
            // Checked with no yield in between, so this is a fact about a fresh device rather than about how many
            // frames fit in a tick: Beep only ever runs from _Process, so nothing can have reached the player yet.
            T.Check("a fresh monitor has handed its player nothing", sound.DebugStream == null && flatUnit.DebugStream == null);
            yield return Ticks(180);   // ~3 s of sim: several beats even if _Process and _PhysicsProcess disagree
            T.Check($"the sound rig's monitor is lit ({sound.DebugLit})", sound.DebugLit);
            T.Check($"a beat hands the player a stream ({sound.DebugStream?.GetType().Name})", sound.DebugStream != null);
            T.Check($"...and it is the BLIP, not the sustained tone ({sound.DebugStream?.GetLength():0.###}s)",
                sound.DebugStream != null && sound.DebugStream.GetLength() < 0.6);

            // A FLATLINED unit takes the other branch, and it must take the sustained clip -- the two are chosen by one
            // bool in _Process, so a swapped branch is a morgue that beeps merrily and a patient that drones.
            T.Check($"a flatlined unit gets the sustained tone instead ({flatUnit.DebugStream?.GetLength():0.###}s)",
                flatUnit.DebugStream != null && flatUnit.DebugStream.GetLength() > 0.2);
            T.Check("...and the two units were not handed the same clip",
                sound.DebugStream != null && flatUnit.DebugStream != null && sound.DebugStream != flatUnit.DebugStream);
            // ---- ONE BEEP SOURCE FOR THE WHOLE WARD (strawberry: "give ecgs a global 'beep source' to sync to so we
            // dont get a bunch of them beeping out of phase").
            //
            // The unit spawned SECOND is the whole test. Both run at the same rate either way, so a per-unit clock
            // fails this and nothing else: identical rate at scattered offsets is what sounds wrong, and it is
            // invisible to any check that only looks at one monitor.
            var late = Spawn(new Vector3(13f, 0f, 0f), alive: true);
            yield return Ticks(37);   // deliberately not a whole number of beats
            var later = Spawn(new Vector3(15f, 0f, 0f), alive: true);
            yield return Ticks(3);
            T.Check($"two monitors born {37f / 60f:0.##}s apart share a phase ({sound.DebugPhase:0.####} / {late.DebugPhase:0.####} / {later.DebugPhase:0.####})",
                Mathf.Abs(sound.DebugPhase - late.DebugPhase) < 0.001f && Mathf.Abs(late.DebugPhase - later.DebugPhase) < 0.001f);
            // TEETH: a phase pinned at 0 (or at any constant) would satisfy "they agree" while the display sat still.
            float p0 = late.DebugPhase;
            yield return Ticks(12);
            T.Check($"...and that shared phase actually ADVANCES ({p0:0.####} -> {late.DebugPhase:0.####})",
                !Mathf.IsEqualApprox(p0, late.DebugPhase));
            // A flatlined unit runs its own period, so it is NOT expected to match -- asserted so that "everything
            // agrees with everything" can't quietly become the rule.
            T.Check($"...while a flatline keeps its own slower sweep ({HeartMonitor.FlatlinePeriod:0.#}s vs {HeartMonitor.BeatPeriod:0.#}s)",
                !Mathf.IsEqualApprox(flatUnit.DebugPeriod, late.DebugPeriod));

            sound.QueueFree(); flatUnit.QueueFree(); late.QueueFree(); later.QueueFree();
            yield return Ticks(2);
            // ...and the blip must be shorter than a beat, or beats would overlap into a drone.
            if (beepRes != null) T.Check($"a blip fits inside one beat ({beepRes.GetLength():0.###}s vs {HeartMonitor.BeatPeriod:0.###}s)",
                beepRes.GetLength() < HeartMonitor.BeatPeriod);

            // ---- BROWNOUT: a sag, not a power cut. Measured as a BAND, because the shader's level is driven per
            // frame and a single sample of a moving value is a coin flip -- the lesson the TV brownout test learned
            // the hard way an hour before this was written.
            float sagLo = 1f;
            hm.FlickerPulse(0.6f);
            T.Check("a brownout pulse sags the monitor", hm.DebugBrownout);
            for (int i = 0; i < 40; i++)
            {
                yield return Ticks(1);
                sagLo = Mathf.Min(sagLo, (float)hm.DebugMaterial.GetShaderParameter("sag"));
            }
            T.Check($"...and the picture level really drops ({sagLo:0.###})", sagLo < 0.6f);
            yield return Ticks(60);
            T.Check("...then settles", !hm.DebugBrownout);
            T.Check($"...back to full ({(float)hm.DebugMaterial.GetShaderParameter("sag"):0.###})",
                Mathf.IsEqualApprox((float)hm.DebugMaterial.GetShaderParameter("sag"), 1f));
            T.Check("...still lit -- a sag is a dip", hm.DebugLit);

            // ---- SHOOTING IT OUT. The contract that matters is the RETURN: false once already dead, so the shot
            // falls through to the prop's health instead of being swallowed. Swallow it and the stand is bulletproof
            // after one hit, which is the opposite of what shooting it should do.
            T.Check("the first shot kills the display", hm.ShootOutScreen());
            yield return Ticks(4);
            T.Check("...the screen goes dark and stays dark",
                !hm.DebugLit && hm.DebugScreen.Visible && hm.DebugScreen.MaterialOverride == hm.DebugOffMaterial);
            T.Check("...a second shot is NOT swallowed", !hm.ShootOutScreen());
            // ...and a dead display cannot be switched or powered back on. A shot-out monitor that relit on the next
            // grid sweep would read as the shot not having registered.
            hm.Toggle(); yield return Ticks(2);
            T.Check("...toggling does not resurrect it", !hm.DebugLit);
            hm.Toggle(); yield return Ticks(2);
            T.Check("...nor does toggling back", !hm.DebugLit);
            hm.FlickerPulse(0.6f);
            T.Check("...and it ignores a brownout", !hm.DebugBrownout);

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
