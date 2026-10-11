using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // RAIN ON GLASS IS ONE SHADER AGAIN (strawberry 2026-10-10: "shrink the size of the raindrop impacts on car
    // glass, having them appear more sharply, and then turn into runners instead of a separate shader").
    //
    // The drops and the long rivulets were two shaders, the second hung on the material's NextPass. Folding them
    // back together is not a tidy-up -- a drop now RUNS at the end of its life instead of fading, and two passes
    // that do not know about each other cannot hand a drop over to become a rivulet.
    //
    // What these check is the part a render cannot: a still frame of a parked car shows beads and streaks whether
    // they come from one shader or two, and it shows them whether or not the globals were registered in time. Each
    // check below is aimed at a failure that LOOKS FINE in a screenshot.
    public sealed class RainGlassOnePassTests : GameTest
    {
        public override string Name => "render.rain_glass_one_pass";
        public override double TimeoutSimSeconds => 30;

        static string ReadSrc(string rel)
        {
            foreach (var c in new[] { "res://" + rel, "res://game/" + rel })
            {
                string p = ProjectSettings.GlobalizePath(c);
                if (System.IO.File.Exists(p)) return System.IO.File.ReadAllText(p);
            }
            return "";
        }

        /// <summary>The source with every `//` comment stripped. The header of rain_glass.gdshader TALKS about the
        /// world-position dot it must not do, and about the next_pass it no longer has -- so a check that greps the
        /// raw file passes or fails on prose. Only code counts.</summary>
        static string CodeOnly(string src)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var line in src.Split('\n')) sb.Append(line.Split("//")[0]).Append('\n');
            return sb.ToString();
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            yield return Ticks(2);

            // Sampled BEFORE anything builds a pane, so the check at the end can say whether this run could
            // discriminate at all: if something upstream already ran the funnel, a pass proves nothing about
            // RainGlassMat and the label says so instead of quietly claiming a green.
            bool globalsBefore = RainSystem3D.GlobalsRegistered;

            string src = ReadSrc("content/rain_glass.gdshader");
            string code = CodeOnly(src);
            T.Check($"rain_glass.gdshader found ({src.Length} chars)", src.Length > 1000);

            // ---- 1. THERE IS NO SECOND SHADER LEFT TO RUN. Deleting the file is the half that is easy to
            // forget to check: a stale rain_glass_runners.gdshader on disk still loads, and the next person to
            // "restore" the pass finds it sitting there looking current.
            bool runnersGone = ReadSrc("content/rain_glass_runners.gdshader").Length == 0;
            T.Check("the separate runners shader is gone from content/", runnersGone);

            // ---- 2. ONE SHADER CARRIES BOTH EFFECTS. Not "the file is big" -- the drop field and the rivulet
            // field each have uniforms only they use, so requiring both in one source is the actual contract.
            T.Check("...and this one shader declares the DROP field (drop_scale, drop_run)",
                    code.Contains("drop_scale") && code.Contains("drop_run"));
            T.Check("...and the RIVULET field (streak_amount, streak_alpha)",
                    code.Contains("streak_amount") && code.Contains("streak_alpha"));

            // ---- 3. THE BOIL GUARD. The old runners pass built its across-axis as cross(world_up, world_normal)
            // and dotted it with a WORLD position. That axis is re-derived from the vehicle's orientation every
            // frame, so the error scales with distance from the map origin -- half a degree of pitch at 800 m
            // moves the coordinate ~7 m, which at 14 columns/m reshuffles ~100 columns. It does not swim, it
            // BOILS, and only far from origin, so it renders perfectly in any showcase near (0,0).
            // Gravity may enter only as a unit DIRECTION and a scalar (fall_dir / fall_tilt, built in vertex()).
            // ⚠ wpos.y alone is fine and is load-bearing -- it is the rivulets' PHASE, which must be world
            // height or a raked windscreen runs slower than an upright window. What must never happen is an
            // orientation-derived AXIS meeting a world POSITION.
            bool dotsWorldPos = code.Contains("dot(wpos") || code.Contains("dot(world_pos");
            T.Check("no shader axis is dotted with a world position (the across-axis boil)", !dotsWorldPos);
            T.Check("...gravity reaches the drops as a direction+scalar from vertex()",
                    code.Contains("fall_dir") && code.Contains("fall_tilt"));
            T.Check("...and the rivulets still take their PHASE from world height",
                    code.Contains("wpos.y"));

            // ---- 4. THE MATERIAL A REAL CAR GETS HAS NO SECOND PASS. This is the one that would quietly come
            // back: NextPass is set in one line and nothing visible changes if it is re-added, because the
            // folded-in rivulets would simply be drawn twice.
            var car = Vehicle.BuildByName("sedan");
            World.AddChild(car);
            car.GlobalPosition = new Vector3(40f, 1.2f, 0f);
            yield return Ticks(20);

            T.Check($"the sedan builds glass panes ({car.GlassCount})", car.GlassCount > 0);
            int shaderPanes = 0, withNextPass = 0;
            MeshInstance3D firstPane = null;
            foreach (var ch in car.GetChildren())
            {
                if (ch is not MeshInstance3D mi || !mi.Name.ToString().StartsWith("Glass_")) continue;
                if (mi.MaterialOverride is not ShaderMaterial sm) continue;
                shaderPanes++; firstPane ??= mi;
                if (sm.NextPass != null) withNextPass++;
            }
            T.Check($"...every pane wears a ShaderMaterial ({shaderPanes}/{car.GlassCount})", shaderPanes == car.GlassCount && shaderPanes > 0);
            T.Check($"...and NONE of them has a NextPass ({withNextPass} do)", withNextPass == 0);

            // ---- 5. `covered` ROUND-TRIPS THROUGH ONE SLOT TABLE. Instance parameters are addressed by SLOT per
            // shader. With two passes, "covered" set on the pane reached the drop shader and landed on some other
            // slot in the runners shader, where it read as permanent shelter and switched every rivulet off --
            // which looks exactly like the runners not working, and did, for a day. One shader, one table.
            if (firstPane != null)
            {
                firstPane.SetInstanceShaderParameter("covered", 1f);
                var back = firstPane.GetInstanceShaderParameter("covered");
                T.Check($"the pane's `covered` instance parameter reads back what was set ({back})",
                        back.VariantType != Variant.Type.Nil && Mathf.IsEqualApprox((float)back, 1f));
                firstPane.SetInstanceShaderParameter("covered", 0f);
            }
            else T.Check("a pane was found to probe `covered` on", false);

            // ---- 6. THE GLOBALS FUNNEL RAN BEFORE THE PANE MATERIAL WAS BUILT. A material that compiles before
            // its globals are registered links them invalid and renders with NONE of them -- silently, and only
            // on the harnesses that skip a map load, which is why RainGlassMat calls EnsureGlobals itself.
            //
            // ⚠ The obvious check -- GlobalShaderParameterGet("rain_wetness") != Nil -- CANNOT WORK HERE, and it
            // was written that way first and failed. Headless Godot registers no global shader parameters at all:
            // every one of them reads Nil whether or not it was added, so that check asks about the RENDERER and
            // would fail identically on a perfectly wired build. Reaching for a GPU-shaped assertion the harness
            // cannot answer is how a test ends up either vacuous or permanently red.
            // RainSystem3D.GlobalsRegistered is the part that is actually ours, and it is the part that broke.
            T.Check($"the rain-globals funnel ran (registered before the car: {globalsBefore})",
                    RainSystem3D.GlobalsRegistered);
        }
    }
}
