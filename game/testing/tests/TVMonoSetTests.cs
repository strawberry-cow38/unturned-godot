using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE BLACK-AND-WHITE SET, and the snow (strawberry: "change the black and white crt to not be a black and white
    // filter but it has its own channels", "redo both static types to look like real static, instead of blobs",
    // "monochrome the test pattern before putting it on the b&w crt").
    //
    // The distinction that matters is between a FILTER and a SOURCE. A desaturate pass over colour output and a set
    // that only ever had luma look similar in a still and are not the same thing -- most visibly on snow, where
    // filtering averages three independent channels toward mid-grey and the contrast collapses, so the filtered mono
    // set showed DULLER static than a real mono tube does. So the checks here are about where the grey comes from:
    // its own channel list, its own composite texture, and the `mono` uniform left switched off.
    public sealed class TVMonoSetTests : GameTest
    {
        public override string Name => "tv.mono_set";
        public override double TimeoutSimSeconds => 30;

        static readonly Transform3D StandUp = new(Basis.FromEuler(new Vector3(-Mathf.Pi * 0.5f, 0f, 0f)), Vector3.Zero);

        static bool Has(TVDevice.ScreenProgram[] a, TVDevice.ScreenProgram p) => System.Array.Exists(a, x => x == p);

        public override IEnumerable<Step> Run()
        {
            // ---- ITS OWN CHANNELS, not the colour list with a filter on top.
            var colour = TVDevice.ProgramsFor(TVDevice.DeviceKind.CrtTv, monoTube: false);
            var mono = TVDevice.ProgramsFor(TVDevice.DeviceKind.CrtTv, monoTube: true);
            T.Check($"a colour CRT carries the colour snow ({colour.Length} channels)",
                Has(colour, TVDevice.ScreenProgram.Static) && !Has(colour, TVDevice.ScreenProgram.StaticMono));
            T.Check($"a MONO CRT carries the mono snow instead ({mono.Length} channels)",
                Has(mono, TVDevice.ScreenProgram.StaticMono) && !Has(mono, TVDevice.ScreenProgram.Static));
            T.Check("...and still gets a test card", Has(mono, TVDevice.ScreenProgram.TestCard));
            // Only the tube TELEVISION is ever a mono set -- a flat panel asked for a mono list must not get one, or a
            // stray true would quietly turn an LCD black-and-white.
            T.Check("a flat TV is unaffected by the mono flag",
                Has(TVDevice.ProgramsFor(TVDevice.DeviceKind.FlatTv, monoTube: true), TVDevice.ScreenProgram.Static));

            // ---- THE MONO SNOW IS A REAL CHANNEL, so everything keyed off a program has to know about it. Each of
            // these is a switch that silently defaults, which is how a new enum member ships half-wired.
            T.Check("mono snow hisses like the colour snow",
                TVDevice.SoundFor(TVDevice.ScreenProgram.StaticMono) == TVDevice.SoundFor(TVDevice.ScreenProgram.Static));
            T.Check($"mono snow rolls on a desync like colour snow does",
                TVDevice.CanRoll(TVDevice.DeviceKind.CrtTv, TVDevice.ScreenProgram.StaticMono));
            float lc = TVDevice.MeanLuma(TVDevice.ScreenProgram.Static, Colors.White);
            float lm = TVDevice.MeanLuma(TVDevice.ScreenProgram.StaticMono, Colors.White);
            T.Check($"both snows throw about the same light ({lc:0.000} vs {lm:0.000})", Mathf.Abs(lc - lm) < 0.02f);
            T.Check($"...and neither is left at a default of zero ({lc:0.000}, {lm:0.000})", lc > 0.2f && lm > 0.2f);

            // ---- THE SHADER ACTUALLY DRAWS IT. A program int with no branch falls through to the `else`, which is the
            // flat-colour channel -- so a missing branch here is a mono TV showing a solid colour, not an error.
            string src = ReadText("res://content/screen.gdshader");
            T.Check($"the shader source was readable ({src.Length} chars)", src.Length > 500);
            T.Check("...and it has a branch for the mono snow",
                src.Contains($"program == {(int)TVDevice.ScreenProgram.StaticMono}") && src.Contains("prog_static_mono"));
            // ...and the blob band that made the old snow read as blobs is gone.
            T.Check("the coarse drifting band is gone (that was the 'blobs')", !src.Contains("vec2(5.0, 4.0)"));

            // ---- LIVE: a mono set is handed the PRE-MONOCHROMED composite and the filter stays off.
            bool gridWas = PowerNet.GlobalPower;
            PowerNet.SetGlobalPower(true);
            TVDevice monoSet = null, colourSet = null;
            for (int i = 0; i < 40 && (monoSet == null || colourSet == null); i++)
            {
                var mesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/") + "Television_1.obj");
                if (mesh == null) { T.Fail("Television_1.obj loads"); break; }
                var mi = new MeshInstance3D { Mesh = mesh, Transform = StandUp };
                World.AddChild(mi);
                var dev = TVDevice.Make(mi, "Television_1");
                World.AddChild(dev);
                if (dev.DebugMonoTube) monoSet ??= dev; else colourSet ??= dev;
            }
            yield return Ticks(4);
            T.Check("rolled up both a mono and a colour CRT to compare", monoSet != null && colourSet != null);

            if (monoSet != null && colourSet != null)
            {
                // The `mono` uniform is the power-off collapse's filter and nothing else now. A mono SET sitting at 1
                // here would mean the old filter path is still doing the work.
                T.Check($"the mono set is NOT running the desaturate filter ({(float)monoSet.DebugScreenMaterial.GetShaderParameter("mono"):0.##})",
                    Mathf.IsZeroApprox((float)monoSet.DebugScreenMaterial.GetShaderParameter("mono")));
                // ...it got a different composite instead. Same prop, same glass: if these textures are the same
                // object then the mono set is being handed the colour picture.
                var mtex = monoSet.DebugScreenTexture;
                var ctex = colourSet.DebugScreenTexture;
                T.Check("both sets have a pattern texture at all", mtex != null && ctex != null);
                T.Check("...and the mono set was handed a DIFFERENT one -- the pre-monochromed composite",
                    mtex != null && ctex != null && mtex != ctex);

                // ...and it is genuinely grey, which is the actual claim. Sampled off the image rather than trusted.
                var img = mtex?.GetImage();
                if (img != null)
                {
                    int achromatic = 0, sampled = 0;
                    for (int y = 0; y < img.GetHeight(); y += Mathf.Max(1, img.GetHeight() / 16))
                        for (int x = 0; x < img.GetWidth(); x += Mathf.Max(1, img.GetWidth() / 16))
                        {
                            var c = img.GetPixel(x, y); sampled++;
                            if (Mathf.Abs(c.R - c.G) < 0.02f && Mathf.Abs(c.G - c.B) < 0.02f) achromatic++;
                        }
                    T.Check($"the mono composite is grey through and through ({achromatic}/{sampled} samples)",
                        sampled > 0 && achromatic == sampled);
                }

                var cimg = ctex?.GetImage();
                if (cimg != null)
                {
                    // TEETH: the colour composite must NOT be grey, or the check above passes on a card that was
                    // never coloured in the first place and proves nothing about the monochroming.
                    int coloured = 0;
                    for (int y = 0; y < cimg.GetHeight(); y += Mathf.Max(1, cimg.GetHeight() / 16))
                        for (int x = 0; x < cimg.GetWidth(); x += Mathf.Max(1, cimg.GetWidth() / 16))
                        {
                            var c = cimg.GetPixel(x, y);
                            if (Mathf.Abs(c.R - c.G) > 0.05f || Mathf.Abs(c.G - c.B) > 0.05f) coloured++;
                        }
                    T.Check($"...while the colour composite has real colour in it ({coloured} samples) -- so grey means something",
                        coloured > 0);
                }

                T.Check($"the mono set's channel came from the mono list ({monoSet.DebugProgram})",
                    Has(mono, monoSet.DebugProgram));
            }

            PowerNet.SetGlobalPower(gridWas);
            yield break;
        }

        static string ReadText(string resPath)
        {
            try { string p = ProjectSettings.GlobalizePath(resPath); return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : ""; }
            catch { return ""; }
        }
    }
}
