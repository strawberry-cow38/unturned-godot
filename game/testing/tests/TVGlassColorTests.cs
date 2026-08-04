using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE GLASS COLOUR, AGAINST THE REAL PROPS (master: "the v blanking bar should be the dark color of the tv screen,
    // instead of white").
    //
    // It was white, and the reason is worth keeping: the colour was read off the screen sub-mesh's VERTEX COLOURS.
    // That sounds like reading it off the model, and it is not. Television_0.obj and Television_1.obj carry no baked
    // vertex colours at all -- 384 `v` lines, not one with rgb -- and ObjMesh fills that case with Colors.White. So
    // "the model's own colour" evaluated to 1.0 on every television in the map.
    //
    // The guard I had wrapped the WRONG condition: it fell back when the colour array was EMPTY, which never happens,
    // rather than when it was white, which always does. A fallback on an impossible branch is not a fallback.
    //
    // The colour lives in the TEXTURE, at the screen face's own UV, so that is where it is read from now. And because
    // Television_0/1.obj and their _tex.png are checked into content/, this suite can run the real TVDevice.Make and
    // assert the real number -- no Unturned install needed, which the older TV suites assume and which is why none of
    // them could have caught this.
    public sealed class TVGlassColorTests : GameTest
    {
        public override string Name => "tv.glass_color";

        public override IEnumerable<Step> Run()
        {
            // Ground truth, read straight out of the shipped palettes:
            //   Television_1 (CRT)        screen texel rgb 53,53,53   (2x2 palette, godot u>0.5 v<0.5)
            //   Television_0 (flatscreen) screen texel rgb 39,39,39   (4x2 palette, godot u<0.25 v>0.5)
            foreach (var (prop, want, label) in new[]
            {
                ("Television_1", 53f / 255f, "CRT"),
                ("Television_0", 39f / 255f, "flatscreen"),
            })
            {
                var mesh = ObjMesh.Load(ProjectSettings.GlobalizePath($"res://content/objects/{prop}.obj"));
                if (mesh == null) { T.Check($"{prop}.obj loads from content/ (it is checked in)", false); continue; }

                var body = new MeshInstance3D { Mesh = mesh };
                World.AddChild(body);
                var tv = TVDevice.Make(body, prop);
                World.AddChild(tv);
                yield return Ticks(2);

                T.Check($"{label}: the screen split found triangles ({tv.DebugScreenOk})", tv.DebugScreenOk);

                var g = tv.DebugGlassColor;
                // THE REGRESSION. This is the single assertion that would have caught the white bar, and it can only
                // exist because the real prop is reachable here.
                T.Check($"{label}: glass is NOT white ({g})", g.R < 0.9f);
                T.Check($"{label}: glass is the real screen texel {want * 255f:0} ({g.R * 255f:0},{g.G * 255f:0},{g.B * 255f:0})",
                    Mathf.IsEqualApprox(g.R, want, 0.01f) && Mathf.IsEqualApprox(g.G, want, 0.01f) && Mathf.IsEqualApprox(g.B, want, 0.01f));
                // ...and it is DARK, which is the property master actually asked for. Pinned separately from the exact
                // value so a future palette change fails on the number without also silently going bright.
                T.Check($"{label}: ...and dark enough to read as a blanking bar ({g.R:0.000})", g.R < 0.35f);

                tv.QueueFree(); body.QueueFree();
                yield return Ticks(1);
            }

            // The two sets must NOT resolve to the same colour -- 53 vs 39. If a future refactor keys the cache or the
            // sample on something prop-independent, both televisions quietly get one tube colour and the wrong one is
            // invisible on whichever set it did not come from.
            var crt = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Television_1.obj"));
            var flat = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Television_0.obj"));
            if (crt != null && flat != null)
            {
                var cs = ObjMesh.SplitByUv(crt, 71, (a, b, c) => a.X > 0.5f && b.X > 0.5f && c.X > 0.5f && a.Y < 0.5f && b.Y < 0.5f && c.Y < 0.5f);
                var fs = ObjMesh.SplitByUv(flat, 70, (a, b, c) => a.X < 0.25f && b.X < 0.25f && c.X < 0.25f && a.Y > 0.5f && b.Y > 0.5f && c.Y > 0.5f);
                var c1 = TVDevice.SampleScreenTexel("Television_1", cs?[0], Colors.Magenta);
                var c0 = TVDevice.SampleScreenTexel("Television_0", fs?[0], Colors.Magenta);
                T.Check($"the two sets have DIFFERENT glass ({c1.R * 255f:0} vs {c0.R * 255f:0})", !Mathf.IsEqualApprox(c1.R, c0.R, 0.01f));
                // Magenta (1,0,1) is the fallback sentinel, and it is checked on R/B rather than G on purpose: a real
                // screen texel is a dark grey, so R below 0.9 means the sample genuinely read the texture. Checking
                // G would pass on magenta -- its green channel is 0 -- which is a sentinel that cannot fire.
                //
                // This matters more than it looks. The production fallback for a CRT is CrtGlass, the correct value,
                // so a totally broken texture read still yields 53,53,53 and every assertion above goes green on a
                // fallback. These two lines are the only thing separating "reads the model" from "guesses right".
                T.Check($"...and neither fell through to the fallback (crt {c1}, flat {c0})", c1.R < 0.9f && c0.R < 0.9f);
            }

            // A missing prop texture must degrade to the caller's fallback, not throw and not return white -- the
            // flatscreen's own default is darker than the CRT's, so the fallback has to be the one passed in.
            var fb = TVDevice.SampleScreenTexel("NoSuchProp_9999", null, new Color(0.1f, 0.2f, 0.3f));
            T.Check($"a missing prop/mesh returns the fallback untouched ({fb})",
                Mathf.IsEqualApprox(fb.R, 0.1f) && Mathf.IsEqualApprox(fb.B, 0.3f));
        }
    }
}
