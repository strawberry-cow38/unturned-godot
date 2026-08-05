using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // FLATSCREEN POWER-ON (strawberry: "make flatscreens of both tv and monitors (not laptops) have a short delay (half
    // a crts delay, no fade) followed by a white rectangle box with the black fake text (from terminalscroll), centered
    // on screen to simulate like an 'Input 1:' screen", then respec'd: "actually not white, but blue, thin 1px white
    // border with 1px blue padding outside the white border, between it and the rectangle edge with white text. stays
    // on for 0.8s. overlayed over the channel playing behind it").
    //
    // Plus: "change the black color for all screen types on all channels to not be perfect black, but the mesh's screen
    // color".
    //
    // The claim that needs real care is "NO FADE". A step and a very fast crossfade look identical in a screenshot and
    // nearly identical in motion, and the existing warm-up machinery is a crossfade -- so the natural way to implement
    // this is to reuse it with a short duration, which would pass any check that only looked at the endpoints. The test
    // for it therefore samples EVERY tick across the transition and asserts the picture level is only ever fully out or
    // fully in, never in between.
    public sealed class TVInputBannerTests : GameTest
    {
        public override string Name => "tv.input_banner";
        public override double TimeoutSimSeconds => 40;

        static readonly Transform3D StandUp = new(Basis.FromEuler(new Vector3(-Mathf.Pi * 0.5f, 0f, 0f)), Vector3.Zero);

        TVDevice Build(string prop)
        {
            var mesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/") + prop + ".obj");
            if (mesh == null) { T.Fail($"{prop}.obj loads"); return null; }
            var mi = new MeshInstance3D { Mesh = mesh, Transform = StandUp };
            World.AddChild(mi);
            var dev = TVDevice.Make(mi, prop);
            World.AddChild(dev);
            return dev;
        }

        public override IEnumerable<Step> Run()
        {
            // ---- WHO GETS ONE. "not laptops" was explicit, and a laptop is the kind most likely to be swept in by a
            // "flat panel" predicate written from the shape of the prop rather than from the ask.
            T.Check("a flat TV gets the input banner", TVDevice.HasInputBanner(TVDevice.DeviceKind.FlatTv));
            T.Check("a flat monitor gets one", TVDevice.HasInputBanner(TVDevice.DeviceKind.FlatMonitor));
            T.Check("a LAPTOP does not", !TVDevice.HasInputBanner(TVDevice.DeviceKind.Laptop));
            T.Check("a CRT television does not", !TVDevice.HasInputBanner(TVDevice.DeviceKind.CrtTv));
            T.Check("a CRT monitor does not", !TVDevice.HasInputBanner(TVDevice.DeviceKind.CrtMonitor));

            // ---- HALF A CRT'S DELAY, stated as a RATIO rather than as a copy of the number. Written as a literal it
            // would silently stop being half the moment the tube's delay was retuned -- which has already happened once
            // this week, when the CRT warm-up was doubled.
            float tube = TVDevice.PowerDelay(TVDevice.DeviceKind.CrtTv);
            float flat = TVDevice.PowerDelay(TVDevice.DeviceKind.FlatTv);
            T.Check($"a tube has a dead delay at all ({tube:0.###} s)", tube > 0f);
            T.Check($"a flat panel's is exactly half of it ({flat:0.###} vs {tube:0.###})",
                Mathf.IsEqualApprox(flat, tube * 0.5f));
            T.Check($"a flat MONITOR gets the same ({TVDevice.PowerDelay(TVDevice.DeviceKind.FlatMonitor):0.###} s)",
                Mathf.IsEqualApprox(TVDevice.PowerDelay(TVDevice.DeviceKind.FlatMonitor), flat));
            T.Check($"a laptop has none ({TVDevice.PowerDelay(TVDevice.DeviceKind.Laptop):0.###} s)",
                TVDevice.PowerDelay(TVDevice.DeviceKind.Laptop) == 0f);
            T.Check("only tubes FADE in", TVDevice.FadesIn(TVDevice.DeviceKind.CrtTv)
                && TVDevice.FadesIn(TVDevice.DeviceKind.CrtMonitor)
                && !TVDevice.FadesIn(TVDevice.DeviceKind.FlatTv)
                && !TVDevice.FadesIn(TVDevice.DeviceKind.FlatMonitor)
                && !TVDevice.FadesIn(TVDevice.DeviceKind.Laptop));
            T.Check($"the banner holds for 0.8 s ({TVDevice.BannerDur:0.##})", Mathf.IsEqualApprox(TVDevice.BannerDur, 0.8f));

            // ---- THE SHADER HAS THE UNIFORMS. SetShaderParameter on a name that does not exist is SILENTLY A NO-OP,
            // so a typo here produces a screen that renders perfectly and simply never shows a banner -- and
            // GetShaderUniformList only enumerates off a shader that actually PARSED, which makes this a compile check
            // for the new GLSL as well.
            var sh = GD.Load<Shader>("res://content/screen.gdshader");
            T.Check("the screen shader loads", sh != null);
            if (sh != null)
            {
                var names = new List<string>();
                foreach (var u in sh.GetShaderUniformList())
                    names.Add(((Godot.Collections.Dictionary)u)["name"].AsString());
                T.Check($"...and PARSED, so its uniforms are readable ({names.Count} of them)", names.Count > 5);
                T.Check("...including `banner`", names.Contains("banner"));
                T.Check("...and `screen_black`", names.Contains("screen_black"));
            }

            bool gridWas = PowerNet.GlobalPower;
            PowerNet.SetGlobalPower(true);
            var flatTv = Build("Television_0");
            var crtTv = Build("Television_1");
            var laptop = Build("Computer_2");
            if (flatTv == null || crtTv == null || laptop == null) { PowerNet.SetGlobalPower(gridWas); yield break; }
            yield return Ticks(200);   // settle: everything starts on, so let the spawn state finish

            // ---- THE BLACK IS THE GLASS. Pushed to the shader from the texel sampled off the prop, so a set with a
            // different screen grey gets ITS grey.
            var black = (Color)flatTv.DebugScreenMaterial.GetShaderParameter("screen_black");
            T.Check($"the shader's black is the mesh's screen colour ({black} vs glass {flatTv.DebugGlassColor})",
                Mathf.Abs(black.R - flatTv.DebugGlassColor.R) < 0.01f
                && Mathf.Abs(black.G - flatTv.DebugGlassColor.G) < 0.01f
                && Mathf.Abs(black.B - flatTv.DebugGlassColor.B) < 0.01f);
            T.Check($"...and is NOT perfect black ({black.R:0.###})", black.R > 0.02f);
            var crtBlack = (Color)crtTv.DebugScreenMaterial.GetShaderParameter("screen_black");
            T.Check($"a CRT gets its own glass too ({crtBlack.R:0.###})", crtBlack.R > 0.02f);

            // ---- POWER-CYCLE A FLAT TV and watch the whole transition, tick by tick.
            if (flatTv.DebugLit) flatTv.Toggle();
            yield return Ticks(40);
            T.Check("the set is off to start", !flatTv.DebugLit);
            T.Check("...with no banner up", !flatTv.DebugBannerUp);

            flatTv.Toggle();
            T.Check("switching on starts the dead delay", flatTv.DebugWarmDelayLeft > 0f);
            T.Check("...with the picture still out", flatTv.DebugScreenBrightness < 0.01f || flatTv.DebugWarm < 0.01f);
            T.Check("...and no banner yet -- it comes AFTER the delay", !flatTv.DebugBannerUp);

            // Sample every tick. A crossfade would be caught here and nowhere else.
            bool sawPartial = false, sawBanner = false;
            int ticksToPicture = 0;
            for (int i = 0; i < 120; i++)
            {
                yield return Ticks(1);
                float w = flatTv.DebugWarm;
                if (w > 0.01f && w < 0.99f) sawPartial = true;
                if (flatTv.DebugBannerUp) sawBanner = true;
                if (w >= 0.99f && ticksToPicture == 0) ticksToPicture = i + 1;
            }
            T.Check($"the picture came up ({ticksToPicture} ticks in)", ticksToPicture > 0);
            T.Check($"...only AFTER the delay ({ticksToPicture} ticks vs {Mathf.RoundToInt(flat * 50f)} of dead time)",
                ticksToPicture >= Mathf.RoundToInt(flat * 50f) - 1);
            T.Check("...and it STEPPED -- never once caught part-way in, so it is not a fast fade", !sawPartial);
            T.Check("...and the banner went up with it", sawBanner);
            T.Check($"...then cleared ({TVDevice.BannerDur:0.##} s later)", !flatTv.DebugBannerUp);

            // ---- A TUBE STILL FADES, and gets no banner. The same power-cycle, so the difference is the kind and not
            // the way it was driven.
            if (crtTv.DebugLit) crtTv.Toggle();
            yield return Ticks(40);
            crtTv.Toggle();
            bool tubePartial = false, tubeBanner = false;
            for (int i = 0; i < 220; i++)
            {
                yield return Ticks(1);
                float w = crtTv.DebugWarm;
                if (w > 0.01f && w < 0.99f) tubePartial = true;
                if (crtTv.DebugBannerUp) tubeBanner = true;
            }
            T.Check("a tube is caught part-way in -- it really does crossfade", tubePartial);
            T.Check("...and never shows an input banner", !tubeBanner);

            // ---- A LAPTOP JUST COMES ON. No delay, no banner: the exclusion strawberry asked for, checked live rather
            // than only through the predicate.
            if (laptop.DebugLit) laptop.Toggle();
            yield return Ticks(40);
            laptop.Toggle();
            yield return Ticks(1);
            T.Check($"a laptop is lit immediately ({laptop.DebugWarm:0.##})", laptop.DebugWarm >= 0.99f);
            bool lapBanner = false;
            for (int i = 0; i < 60; i++) { yield return Ticks(1); if (laptop.DebugBannerUp) lapBanner = true; }
            T.Check("...and never banners", !lapBanner);

            PowerNet.SetGlobalPower(gridWas);
            yield break;
        }
    }
}
