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
    // TVDevice.Make cannot run here". That is no longer true and was the reason the built path went untested: the four
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
            var crtTv = Build("Television_1");
            if (crtMon == null || flatMon == null || crtTv == null) { PowerNet.SetGlobalPower(gridWas); yield break; }
            yield return Ticks(2);

            // ---- ON AT START (master: "making all tvs/monitors on at start"). Asserted on a BUILT device rather than
            // on the _on field: the flag being set means nothing if Refresh's power gate then rejects it, and this is
            // the first release where that gate has two terms in it.
            foreach (var (d, nm) in new[] { (crtMon, "the CRT monitor"), (flatMon, "the flatscreen monitor"), (crtTv, "the CRT television") })
                T.Check($"{nm} comes up LIT with no player input ({d.DebugLit})", d.DebugLit);
            T.Check($"...and the screen mesh actually got carved out ({crtMon.DebugScreenOk})",
                crtMon.DebugScreenOk && flatMon.DebugScreenOk && crtTv.DebugScreenOk);

            // ---- NO TEST CARD on a monitor. This is the check the pure table cannot make: HasPattern being false is
            // one thing, Build actually skipping LoadPattern and leaving the material textureless is another.
            T.Check("a monitor's screen carries NO texture -- its picture IS the albedo colour",
                crtMon.DebugScreenTexture == null && flatMon.DebugScreenTexture == null);
            T.Check("...where the television still has the SMPTE card, so this is a difference and not a broken load",
                crtTv.DebugScreenTexture != null);
            T.Check("a monitor is still UNSHADED, like every other screen here",
                crtMon.DebugScreenUnshaded && flatMon.DebugScreenUnshaded);

            // ---- NO TONE on a monitor (master). Absent rather than built-and-never-played, so there is no silent
            // player to be woken up later by some unrelated Play().
            T.Check("neither monitor has a test tone at all", !crtMon.DebugHasTone && !flatMon.DebugHasTone);
            T.Check("...and the television does -- again, a difference, not a missing sound file", crtTv.DebugHasTone);

            // ---- THE PLUG (master: "add power io"). One consumer port per set, at the right load.
            foreach (var (d, nm, w) in new[]
            {
                (crtMon, "CRT monitor", TVDevice.CrtMonitorWatts),
                (flatMon, "flatscreen monitor", TVDevice.FlatMonitorWatts),
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

            // ---- THE COLOUR CYCLE, AND THE CONE FOLLOWING IT (master: "cycle through a few random colors (which tints
            // the 'cone')"). The tint and the spill are set in different places -- AlbedoColor on the screen material,
            // LightColor on the SpotLight -- so "the screen changed colour" alone would pass with the cone left blue.
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
            // THE CONTRAST that makes the above mean something: a television's spill is the fixed blue-white and its
            // "tint" is white, because albedo MULTIPLIES the SMPTE texture and any other value would recolour the bars.
            T.Check($"a television's picture stays untinted ({crtTv.DebugTint})", crtTv.DebugTint == Colors.White);
            T.Check($"...and its spill stays the fixed blue-white ({crtTv.DebugSpill})",
                !crtTv.DebugSpill.IsEqualApprox(Colors.White) && crtTv.DebugSpill.B > crtTv.DebugSpill.R);

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
            T.Check($"...and the picture goes MONOCHROME on the way out ({crtMon.DebugTint})",
                Achromatic(crtMon.DebugTint));
            T.Check($"...to the luma of the colour it was showing, not to black ({crtMon.DebugTint.R:0.000})",
                crtMon.DebugTint.R > 0.01f && Mathf.IsEqualApprox(crtMon.DebugTint.R, TVDevice.Mono(beforeOff).R));
            // The flatscreen monitor is a PANEL and gets none of that -- it just stops.
            flatMon.Toggle();
            yield return Ticks(2);
            T.Check($"the flatscreen monitor just stops, no collapse ({flatMon.DebugLit})", !flatMon.DebugLit);

            // ...and once the collapse has run its course the set is dark and comes back on cleanly.
            yield return Until(() => !crtMon.DebugLit, 3);
            crtMon.Toggle();
            yield return Ticks(4);
            T.Check($"the monitor switches back on ({crtMon.DebugLit})", crtMon.DebugLit);
            T.Check($"...in colour again, not stuck in the collapse's monochrome ({crtMon.DebugTint})",
                !Achromatic(crtMon.DebugTint));

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
