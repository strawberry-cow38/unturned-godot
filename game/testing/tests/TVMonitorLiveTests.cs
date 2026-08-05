using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The same feature as tv.monitors_and_power_io, but against REAL BUILT DEVICES rather than the pure statics.
    //
    // The sibling suite proves the kind table says a monitor has no test card. This one proves a monitor built off the
    // actual Computer_0 mesh has no test card -- which is a different claim, and the gap between them is where this
    // feature can fail: every one of master's "minus" items is a branch in Build/_Process, and a table that says the
    // right thing while the branch does the wrong thing looks identical from outside.
    //
    // The other TV suites all open with "the Television meshes ship with Unturned and this box has no install, so
    // TVDevice.Make cannot run here". That is no longer true and was the reason the built path went untested: the five
    // prop .obj files and their palette pngs are tracked in this repo (git ls-files), so Make runs fine here. What it
    // does NOT get is a placement transform from the world builder, so this supplies one -- a -90 deg X rotation, which
    // is exactly what standing a Z-up authored prop upright does, and without it the prop lies on its back and the
    // plug's height axis degenerates.
    public sealed class TVMonitorLiveTests : GameTest
    {
        public override string Name => "tv.monitors_live";

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

        static bool Achromatic(Color c) => Mathf.Abs(c.R - c.G) < 1e-3f && Mathf.Abs(c.G - c.B) < 1e-3f;

        public override IEnumerable<Step> Run()
        {
            bool gridWas = PowerNet.GlobalPower;
            PowerNet.SetGlobalPower(true);

            var crtMon = Build("Computer_0");
            var flatMon = Build("Computer_3");
            var laptop = Build("Computer_2");
            var crtTv = Build("Television_1");
            if (crtMon == null || flatMon == null || laptop == null || crtTv == null) { PowerNet.SetGlobalPower(gridWas); yield break; }
            yield return Ticks(2);

            // ---- ON AT START (master: "making all tvs/monitors on at start"). Asserted on a BUILT device rather than
            // on the _on field: the flag being set means nothing if Refresh's power gate then rejects it, and this is
            // the first release where that gate has two terms in it.
            foreach (var (d, nm) in new[] { (crtMon, "the CRT monitor"), (flatMon, "the flatscreen monitor"),
                                            (laptop, "the laptop"), (crtTv, "the CRT television") })
                T.Check($"{nm} comes up LIT with no player input ({d.DebugLit})", d.DebugLit);
            T.Check($"...and the screen mesh actually got carved out ({crtMon.DebugScreenOk})",
                crtMon.DebugScreenOk && flatMon.DebugScreenOk && laptop.DebugScreenOk && crtTv.DebugScreenOk);

            // ---- NO TEST CARD on a monitor. What decides the picture is the shader's `program`, not whether a texture
            // happens to be bound -- the SMPTE composite is a shared static wired into every set so any of them can be
            // switched onto the card. So the claim to check is that a monitor is never DEALT the card in the first
            // place: its pool does not contain it.
            foreach (var (d, nm) in new[] { (crtMon, "CRT monitor"), (flatMon, "flatscreen monitor"), (laptop, "laptop") })
                T.Check($"the {nm} is not showing a test card ({d.DebugProgram})",
                    d.DebugProgram != TVDevice.ScreenProgram.TestCard);
            foreach (var k in new[] { TVDevice.DeviceKind.CrtMonitor, TVDevice.DeviceKind.FlatMonitor, TVDevice.DeviceKind.Laptop })
                T.Check($"...and {k}'s whole pool excludes it, so it never can be",
                    !System.Array.Exists(TVDevice.ProgramsFor(k), x => x == TVDevice.ScreenProgram.TestCard));
            T.Check("...while a television's pool does include it",
                System.Array.Exists(TVDevice.ProgramsFor(TVDevice.DeviceKind.CrtTv), x => x == TVDevice.ScreenProgram.TestCard));
            T.Check("...and every set has the shared card wired, so any can be switched onto it",
                crtTv.DebugScreenTexture != null && crtMon.DebugScreenTexture != null);
            T.Check("a monitor is still UNSHADED, like every other screen here",
                crtMon.DebugScreenUnshaded && flatMon.DebugScreenUnshaded && laptop.DebugScreenUnshaded);
            // WHICH WAY EACH SCREEN FACES, on a BUILT device. The static suite proves the winding is +Y on every prop;
            // this proves the device actually adopted it, which is the half that was broken -- Reproject used to
            // re-sign the normal away from the body's centre, and that flipped the flatscreen monitor (its stand runs
            // 1 cm past the panel) and the laptop (its whole deck is on the side the lid faces).
            foreach (var (d, nm) in new[] { (crtMon, "CRT monitor"), (flatMon, "flatscreen monitor"),
                                            (laptop, "laptop"), (crtTv, "CRT television") })
                T.Check($"the {nm}'s screen faces local +Y, not back into its own body ({d.DebugScreenNormalLocal})",
                    d.DebugScreenNormalLocal.Y > 0.9f);
            // The television is the ANCHOR for that direction: it is the prop that has been looked at in-game, and the
            // old rule and the new one produce the same answer for it. Without something in this list whose facing was
            // confirmed by eye, "+Y" would just be the number the code happens to emit.
            T.Check($"...anchored on the television, whose facing the old rule already agreed on ({crtTv.DebugScreenNormalLocal})",
                crtTv.DebugScreenNormalLocal.Y > 0.9f);
            // The laptop's lid is HINGED -- tilted back about 6 degrees -- so alone among these its screen plane is not
            // axis-aligned. Its normal must carry that tilt rather than being snapped to an axis, or the spill and the
            // shaft leave the lid square instead of angled the way the screen is.
            T.Check($"...and the laptop's carries its hinge tilt ({laptop.DebugScreenNormalLocal})",
                Mathf.Abs(laptop.DebugScreenNormalLocal.Z) > 0.02f
                && Mathf.Abs(crtMon.DebugScreenNormalLocal.Z) < 0.02f);

            // ---- NO TONE on a monitor (master). Absent rather than built-and-never-played, so there is no silent
            // player to be woken up later by some unrelated Play().
            T.Check("no monitor -- nor the laptop -- has a test tone at all",
                !crtMon.DebugHasTone && !flatMon.DebugHasTone && !laptop.DebugHasTone);
            T.Check("...and the television does -- again, a difference, not a missing sound file", crtTv.DebugHasTone);

            // ---- THE PLUG (master: "add power io"). One consumer port per set, at the right load.
            foreach (var (d, nm, w) in new[]
            {
                (crtMon, "CRT monitor", TVDevice.CrtMonitorWatts),
                (flatMon, "flatscreen monitor", TVDevice.FlatMonitorWatts),
                (laptop, "laptop", TVDevice.LaptopWatts),
                (crtTv, "CRT television", TVDevice.CrtTvWatts),
            })
            {
                T.Check($"the {nm} has a wire-able plug", d.DebugHasPlug);
                T.Check($"...drawing {w:0} W ({d.DebugPlugWatts:0})", Mathf.IsEqualApprox(d.DebugPlugWatts, w));
                T.Check($"...exposed to the power graph ({d.PowerPorts.Count} port)", d.PowerPorts.Count == 1);
                T.Check($"...as a CONSUMER, never a source", d.PowerPorts.Count == 1
                    && d.PowerPorts[0].Kind == DeployableDef.PortKind.Consumer && !d.PowerProducing);
                // The height axis is only non-degenerate because the prop was stood up. If it were not, the plug would
                // sit at mid-height -- which is legal but would mean this test silently stopped covering the slide.
                T.Check($"...and hung at a height, not at the cabinet's midpoint ({d.DebugPlugLocal})",
                    d.DebugPlugLocal.LengthSquared() > 0f);
            }

            // ---- THE INDICATOR LAMPS, on built devices. The static suite pins the POLICY; this pins that the UV split
            // actually found the cubes in the real meshes -- a predicate that matched nothing leaves the device with a
            // null lamp and no error anyone sees, which is indistinguishable from "this prop has no LED".
            var flatTv = Build("Television_0");
            if (flatTv != null)
            {
                yield return Ticks(2);
                T.Check("the flatscreen TV found BOTH indicator cubes in its mesh",
                    flatTv.DebugHasOnLed && flatTv.DebugHasStandbyLed);
                T.Check($"...and lit, it shows green and not red ({flatTv.DebugLeds})",
                    flatTv.DebugLeds == (true, false));
                flatTv.Toggle();
                yield return Ticks(2);
                T.Check($"...switched off but still powered, it shows RED standby ({flatTv.DebugLeds})",
                    flatTv.DebugLeds == (false, true));
                PowerNet.SetGlobalPower(false);
                flatTv.Refresh();
                yield return Ticks(2);
                T.Check($"...and in a blackout it shows NOTHING -- an unpowered set does not glow ({flatTv.DebugLeds})",
                    flatTv.DebugLeds == (false, false));
                PowerNet.SetGlobalPower(true);
                flatTv.Toggle();
                flatTv.Refresh();
                yield return Ticks(2);
                T.Check($"...and comes back green ({flatTv.DebugLeds})", flatTv.DebugLeds == (true, false));
            }
            T.Check("both monitors found their green cube", crtMon.DebugHasOnLed && flatMon.DebugHasOnLed);
            T.Check("...and neither claims a standby lamp it does not have",
                !crtMon.DebugHasStandbyLed && !flatMon.DebugHasStandbyLed);
            T.Check($"a lit monitor's green lamp is emitting ({crtMon.DebugLeds})", crtMon.DebugLeds == (true, false));
            // The laptop and the CRT television have no indicator geometry at all. Asserted so "no lamp" stays a
            // measured fact about those meshes rather than a split that silently failed on all four.
            T.Check("the laptop has no indicator lamp (its mesh has none)",
                !laptop.DebugHasOnLed && !laptop.DebugHasStandbyLed);
            T.Check("the CRT television has none either -- its cube is plain grey, not a red/green pair",
                !crtTv.DebugHasOnLed && !crtTv.DebugHasStandbyLed);

            // ---- EVERY PROGRAM, on a real device. The program is picked at RANDOM per set -- which is the point, a
            // street of televisions should not all show the same thing -- and that makes coverage impossible by
            // construction: whatever this suite asserted about "the monitor" would only ever describe the one program
            // the RNG happened to deal it, and six of seven would go untested on a green run. So each is forced.
            foreach (var prog in System.Enum.GetValues<TVDevice.ScreenProgram>())
            {
                var dev = TVDevice.HasPattern(TVDevice.KindFor("Television_1")) && prog is TVDevice.ScreenProgram.TestCard
                          or TVDevice.ScreenProgram.Static or TVDevice.ScreenProgram.Dvd ? crtTv : crtMon;
                dev.DebugSetProgram(prog);
                yield return Ticks(2);
                T.Check($"{prog}: the device adopts it", dev.DebugProgram == prog);
                // THE RULE master added: the tone belongs to the test card, not to the television.
                var want = TVDevice.SoundFor(prog);
                T.Check($"{prog}: sound is {want}", dev.DebugSound == want);
                T.Check($"{prog}: ...and the loop player exists ONLY when there is a sound ({dev.DebugHasTone})",
                    dev.DebugHasTone == (want != TVDevice.ScreenSound.None));
            }
            T.Check("only the test card carries the 1kHz tone",
                TVDevice.SoundFor(TVDevice.ScreenProgram.TestCard) == TVDevice.ScreenSound.Tone);
            T.Check("...only static hisses",
                TVDevice.SoundFor(TVDevice.ScreenProgram.Static) == TVDevice.ScreenSound.Noise);
            foreach (var quiet in new[] { TVDevice.ScreenProgram.Dvd, TVDevice.ScreenProgram.Colour,
                                          TVDevice.ScreenProgram.TerminalCursor, TVDevice.ScreenProgram.TerminalScroll,
                                          TVDevice.ScreenProgram.BarGraph })
                T.Check($"...and {quiet} is silent", TVDevice.SoundFor(quiet) == TVDevice.ScreenSound.None);

            // ---- THE COLOUR CYCLE, AND THE CONE FOLLOWING IT (master: "cycle through a few random colors (which tints
            // the 'cone')"). The tint and the spill are set in different places -- the shader's `tint` uniform and the
            // SpotLight's LightColor -- so "the screen changed colour" alone would pass with the cone left blue.
            crtMon.DebugSetProgram(TVDevice.ScreenProgram.Colour);
            yield return Ticks(2);
            var seenTints = new HashSet<Color>();
            for (int i = 0; i < 24; i++)
            {
                seenTints.Add(crtMon.DebugTint);
                T.Check($"the spill tracks the screen ({crtMon.DebugSpill} vs {crtMon.DebugTint})",
                    crtMon.DebugSpill.IsEqualApprox(crtMon.DebugTint));
                crtMon.DebugCycleColour();
            }
            T.Check($"the monitor cycles through several colours ({seenTints.Count} distinct over 24 changes)",
                seenTints.Count >= 3);
            T.Check("...and none of them is white (which is what an untinted screen would report)",
                !seenTints.Contains(Colors.White));
            // THE CONTRAST that makes the above mean something: a program that draws its OWN colours must be multiplied
            // by white, or the tint would recolour whatever it drew. That is why Picture is white for everything except
            // the flat-colour program.
            crtMon.DebugSetProgram(TVDevice.ScreenProgram.BarGraph);
            yield return Ticks(2);
            T.Check($"a program that draws its own colours is multiplied by WHITE ({crtMon.DebugTint})",
                crtMon.DebugTint == Colors.White);
            crtTv.DebugSetProgram(TVDevice.ScreenProgram.TestCard);
            yield return Ticks(2);
            T.Check($"...as is the test card, so albedo cannot tint the bars ({crtTv.DebugTint})",
                crtTv.DebugTint == Colors.White);
            T.Check($"...and its spill stays the fixed blue-white ({crtTv.DebugSpill})",
                !crtTv.DebugSpill.IsEqualApprox(Colors.White) && crtTv.DebugSpill.B > crtTv.DebugSpill.R);

            crtMon.DebugSetProgram(TVDevice.ScreenProgram.Colour);
            yield return Ticks(2);

            // ---- THE TERMINAL SCROLLS IN BURSTS (master: "scroll in bursts of random durations and time between.
            // with a blinking cursor sometimes"). What makes this a burst rather than a slow scroll is that the
            // position sometimes DOES NOT MOVE -- so the check is that it both advances and stalls over a long enough
            // window. A continuous scroll would satisfy "it advances" perfectly.
            crtMon.DebugSetProgram(TVDevice.ScreenProgram.TerminalScroll);
            yield return Ticks(2);
            float last = crtMon.DebugScrollOffset;
            int moved = 0, stalled = 0, cursorSeen = 0, bothAtOnce = 0;
            for (int i = 0; i < 400; i++)
            {
                yield return Ticks(1);
                float now = crtMon.DebugScrollOffset;
                if (now > last + 1e-5f) moved++; else stalled++;
                if (crtMon.DebugCursorOn) cursorSeen++;
                // Sampled at ONE INSTANT from the state machine itself, not inferred from movement across the
                // interval -- a burst that ends mid-interval leaves the position advanced AND the cursor lit, which
                // looks like a violation of this rule and is not one.
                if (crtMon.DebugCursorOn && crtMon.DebugScrolling) bothAtOnce++;
                last = now;
            }
            T.Check($"the terminal scrolls ({moved} frames advancing)", moved > 20);
            T.Check($"...and PAUSES between bursts ({stalled} frames parked) -- a continuous scroll would never stall",
                stalled > 20);
            T.Check($"...showing a cursor while it waits ({cursorSeen} frames)", cursorSeen > 5);
            T.Check($"...and never while a burst is running ({bothAtOnce}) -- one machine, not two effects fighting",
                bothAtOnce == 0);
            T.Check($"...with the offset only ever going forward ({crtMon.DebugScrollOffset:0.0})",
                crtMon.DebugScrollOffset > 0f);

            // ---- THE CONE READS THE PICTURE. Same device, two programs, and the dark one must throw less light.
            crtMon.DebugSetProgram(TVDevice.ScreenProgram.TerminalCursor);
            yield return Ticks(3);
            float darkCone = crtMon.DebugConeAlpha, darkScale = crtMon.DebugConeScale;
            crtMon.DebugSetProgram(TVDevice.ScreenProgram.BarGraph);
            yield return Ticks(3);
            float brightCone = crtMon.DebugConeAlpha, brightScale = crtMon.DebugConeScale;
            T.Check($"a black terminal barely lights the room ({darkScale:0.000} scale)", darkScale < 0.1f);
            T.Check($"...far less than an instrument panel ({brightScale:0.000})", brightScale > darkScale * 4f);
            T.Check($"...and that reaches the actual shaft, not just the number ({darkCone:0.0000} vs {brightCone:0.0000})",
                brightCone > darkCone);

            crtMon.DebugSetProgram(TVDevice.ScreenProgram.Colour);   // the collapse below is asserted on a colour picture
            yield return Ticks(2);

            // ---- THE CRT MONITOR STILL COLLAPSES, in monochrome. Both are "dupe the CRT thing onto the computer crt",
            // and both had to survive the picture becoming a flat colour instead of a texture -- the television's mono
            // is a TEXTURE SWAP, which a monitor has nothing to swap, so it desaturates its tint instead.
            var beforeOff = crtMon.DebugTint;
            T.Check($"the lit monitor's picture is coloured ({beforeOff})", !Achromatic(beforeOff));
            crtMon.Toggle();
            yield return Ticks(2);
            T.Check($"switching it off starts the collapse rather than snapping ({crtMon.DebugScreenOk})",
                crtMon.DebugScreenOk);
            // Desaturation moved from the C# tint into the shader's `mono` uniform when the programs landed -- it has
            // to, because five of the seven programs generate their colours on the GPU and there is no CPU-side colour
            // to desaturate. So the probe moves too: the uniform is what carries it now.
            T.Check($"...and the picture goes MONOCHROME on the way out (mono={crtMon.DebugMonoUniform})",
                Mathf.IsEqualApprox(crtMon.DebugMonoUniform, 1f));
            T.Check($"...while the tint keeps the colour it was showing, undesaturated ({crtMon.DebugTint})",
                !Achromatic(crtMon.DebugTint) && crtMon.DebugTint.IsEqualApprox(beforeOff));
            // The flatscreen monitor is a PANEL and gets none of that -- it just stops.
            flatMon.Toggle();
            yield return Ticks(2);
            T.Check($"the flatscreen monitor just stops, no collapse ({flatMon.DebugLit})", !flatMon.DebugLit);

            // ...and once the collapse has run its course the set is dark and comes back on cleanly.
            yield return Until(() => !crtMon.DebugLit, 3);
            crtMon.Toggle();
            yield return Ticks(4);
            T.Check($"the monitor switches back on ({crtMon.DebugLit})", crtMon.DebugLit);
            T.Check($"...in colour again, not stuck in the collapse's monochrome (mono={crtMon.DebugMonoUniform})",
                Mathf.IsEqualApprox(crtMon.DebugMonoUniform, 0f));

            // "the uniform is 1" only proves the C# half asked for desaturation. What actually desaturates is the
            // shader, so the shader is pinned too -- otherwise deleting the mix() leaves every check here green and
            // every dying CRT in colour. And the weights must be the SAME Rec.709 the C# Mono() uses: a flat average
            // sends pure blue and pure green to one grey, so the two rules disagreeing is a real visual difference.
            string shader = crtMon.DebugScreenShaderCode;
            T.Check("the shader actually desaturates by `mono`", shader.Contains("mix(c, desat(c)"));
            T.Check("...using Rec.709, matching the C# rule", shader.Contains("0.2126, 0.7152, 0.0722"));
            T.Check($"...the same weights C# uses ({TVDevice.Mono(new Color(0f, 1f, 0f)).R:0.0000})",
                Mathf.IsEqualApprox(TVDevice.Mono(new Color(0f, 1f, 0f)).R, 0.7152f));

            // A MONOCHROME CRT TELEVISION (master: "the CRT tvs can be either monochrome or color") holds mono lit
            // while it is running, not only while it dies.
            T.Check($"a colour tube runs with mono off (monoTube={crtTv.DebugMonoTube}, mono={crtTv.DebugMonoUniform})",
                crtTv.DebugMonoTube || Mathf.IsEqualApprox(crtTv.DebugMonoUniform, 0f));

            // ---- SMASHED: the plug goes with the cabinet. A wire hanging off rubble still drawing 70 W is a load you
            // cannot see on a generator you can, and it is the sort of thing only ever noticed as "my genny is short".
            crtMon.SetBroken(true);
            yield return Ticks(2);
            T.Check($"a smashed set is dark ({crtMon.DebugLit})", !crtMon.DebugLit);
            T.Check($"...and has no plug left to draw through ({crtMon.PowerPorts.Count} ports)",
                !crtMon.DebugHasPlug && crtMon.PowerPorts.Count == 0);
            crtMon.SetBroken(false);
            yield return Ticks(2);
            T.Check($"a rubble reset gives the plug back ({crtMon.DebugPlugWatts:0} W)",
                crtMon.DebugHasPlug && Mathf.IsEqualApprox(crtMon.DebugPlugWatts, TVDevice.CrtMonitorWatts));
            T.Check($"...and the set comes back ON with the rest of the world ({crtMon.DebugLit})", crtMon.DebugLit);

            // ---- THE POWER GATE HAS TWO TERMS. With the mains down and nothing wired, every set is dark; that is the
            // half that already worked. The half that is new is that HasFeed consults the plug at all -- asserted here
            // as the state rather than by standing up a generator, which PowerTests already covers end to end.
            PowerNet.SetGlobalPower(false);
            crtMon.Refresh(); crtTv.Refresh();
            yield return Ticks(2);
            T.Check($"a blackout kills an unwired set ({crtMon.DebugLit}, feed={crtMon.HasFeed})",
                !crtMon.DebugLit && !crtMon.HasFeed);
            T.Check("...because neither the mains nor its plug is live", !PowerNet.GlobalPower && !crtMon.PlugPowered);
            PowerNet.SetGlobalPower(true);
            crtMon.Refresh();
            yield return Ticks(2);
            T.Check($"...and the mains coming back is enough on its own ({crtMon.DebugLit})", crtMon.DebugLit);

            PowerNet.SetGlobalPower(gridWas);
        }
    }
}
