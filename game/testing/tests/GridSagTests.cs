using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE GRID REACHING THINGS THAT WERE IGNORING IT (strawberry: "make tvs flicker for brownouts/turn off for
    // blackouts if they arent powered via their input", and "make gas pumps take global power").
    //
    // Both are the same shape: a fixture that only ever looked at its own wired input, taught to also see the mains.
    // And both carry the same qualifier, which is the part worth testing -- **either** source keeps it alive. Gating
    // on the mains ALONE is the tempting one-liner and it silently guts the reason to wire a generator to anything:
    // the blackout is exactly when you want your own supply to matter.
    //
    // Note the harness starts with the grid DOWN, which is why the pre-existing pump tests still pass unchanged. That
    // also means those tests say nothing about this change, so the mains path needs its own coverage or the feature is
    // shipping untested behind a green suite.
    public sealed class GridSagTests : GameTest
    {
        public override string Name => "power.grid_reaches_pumps_and_tvs";
        public override double TimeoutSimSeconds => 30;

        static readonly Transform3D StandUp = new(Basis.FromEuler(new Vector3(-Mathf.Pi * 0.5f, 0f, 0f)), Vector3.Zero);

        TVDevice BuildTv(string prop)
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
            bool gridWas = PowerNet.GlobalPower;

            // ---- THE PUMP TAKES THE MAINS.
            PowerNet.SetGlobalPower(false);
            var pump = GasPump.Attach(World, new Vector3(3f, 0f, 0f), Basis.Identity, GasPump.PortLocal);
            yield return Ticks(2);
            T.Check("an unwired pump is dead with the grid down", !pump.IsPowered);
            PowerNet.SetGlobalPower(true);
            PowerNet.Recompute(Tree);
            yield return Ticks(2);
            T.Check("...and lives on the mains alone, no wire needed", pump.IsPowered);

            // ...and the mains is not the ONLY thing it will accept. A generator wired straight in has to keep it
            // running through a blackout -- otherwise wiring one to a pump buys you nothing at the one moment it
            // should matter, which is what gating purely on GlobalPower would have done.
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(-3f, 0f, 0f), 0f);
            var genOut = gen.Ports.Find(p => p.Kind == DeployableDef.PortKind.Output);
            PowerRig.Connect(World, genOut, pump.PowerPorts[0]);
            gen.TogglePower();
            PowerNet.SetGlobalPower(false);
            PowerNet.Recompute(Tree);
            yield return Ticks(2);
            T.Check("a generator-wired pump survives the blackout", pump.IsPowered);

            // ---- TELEVISIONS: sag and blackout, but only on the mains.
            PowerNet.SetGlobalPower(true);
            PowerNet.Recompute(Tree);
            var tv = BuildTv("Television_1");
            if (tv == null) { PowerNet.SetGlobalPower(gridWas); yield break; }
            yield return Ticks(240);   // let the tube finish warming, so a sag is distinguishable from a warm-up
            T.Check($"a mains-fed set is lit ({tv.DebugLit})", tv.DebugLit);
            T.Check("...and is not already sagging", !tv.DebugBrownout);

            // A TUBE BREATHES. FlickerFactor swings the picture over FlickerDepth (0.18) at 24 Hz for as long as it is
            // lit, so a single sample of DebugScreenBrightness is a sample of a moving value -- and comparing two of
            // them with a 0.05 tolerance is a coin flip on which phase each landed in. Measure the BAND instead: what
            // the level ranges over while it is behaving normally, and then assert the sag leaves that band and the
            // recovery returns to it. (This test passed on one machine and flaked on another until it did.)
            float steadyLo = float.MaxValue, steadyHi = float.MinValue;
            for (int i = 0; i < 30; i++)   // 0.6 s ~= 14 flicker cycles: comfortably the whole breath
            {
                yield return Ticks(1);
                steadyLo = Mathf.Min(steadyLo, tv.DebugScreenBrightness);
                steadyHi = Mathf.Max(steadyHi, tv.DebugScreenBrightness);
            }
            T.Check($"the lit tube's level is a BAND, not a number ({steadyLo:0.###}..{steadyHi:0.###})",
                steadyHi > steadyLo);

            tv.FlickerPulse(0.6f);
            T.Check("a brownout pulse starts a sag", tv.DebugBrownout);
            // The sag must take the picture BELOW the whole steady band -- not merely below some sample of it, which
            // the breath alone would manage.
            float lowest = float.MaxValue;
            for (int i = 0; i < 40; i++)
            {
                yield return Ticks(1);
                lowest = Mathf.Min(lowest, tv.DebugScreenBrightness);
            }
            T.Check($"...and the picture drops clear of the steady band ({lowest:0.###} vs floor {steadyLo:0.###})",
                lowest < steadyLo * 0.75f);
            yield return Ticks(60);
            T.Check("...then settles back", !tv.DebugBrownout);
            float backLo = float.MaxValue, backHi = float.MinValue;
            for (int i = 0; i < 30; i++)
            {
                yield return Ticks(1);
                backLo = Mathf.Min(backLo, tv.DebugScreenBrightness);
                backHi = Mathf.Max(backHi, tv.DebugScreenBrightness);
            }
            T.Check($"...to the same BAND it was in ({backLo:0.###}..{backHi:0.###} vs {steadyLo:0.###}..{steadyHi:0.###})",
                backLo > steadyLo - 0.05f && backHi < steadyHi + 0.05f);
            T.Check("...still lit -- a sag is a dip, not a power cut", tv.DebugLit);

            // ---- BLACKOUT kills a mains-fed set.
            PowerNet.SetGlobalPower(false);
            PowerNet.Recompute(Tree);
            yield return Ticks(20);
            T.Check("the grid dying takes the mains-fed set with it", !tv.DebugLit);
            // ...and a dark set must not flicker: a pulse arriving after the lights went out would light it back up
            // for half a second, which is the failure a naive "flicker everything" sweep produces.
            tv.FlickerPulse(0.6f);
            T.Check("a dark set ignores the pulse entirely", !tv.DebugBrownout && !tv.DebugLit);

            // ---- THE CONSOLE PATH: a bare flag flip, with NO explicit Recompute.
            //
            // Every check above calls PowerNet.Recompute by hand. The game does not -- `toggleGlobalPower` calls
            // SetGlobalPower, which only MarkDirty()s, and something else recomputes on its own schedule. So the
            // assertions above can all pass while the thing a player actually does has no effect, which is exactly the
            // report ("globalpower has no effect on tvs still"). A set with no wire in it must not need the solver at
            // all: HasFeed reads the mains flag directly.
            PowerNet.SetGlobalPower(true);
            PowerNet.Recompute(Tree);
            yield return Ticks(240);
            T.Check($"a set is lit before the console flip ({tv.DebugLit})", tv.DebugLit);
            PowerNet.SetGlobalPower(false);        // NO Recompute -- deliberately
            yield return Ticks(30);
            T.Check($"...and a bare flag flip darkens it too ({tv.DebugLit})", !tv.DebugLit);
            PowerNet.SetGlobalPower(true);         // ...and back, same way
            yield return Ticks(30);
            T.Check($"...and relights it ({tv.DebugLit})", tv.DebugLit);

            PowerNet.SetGlobalPower(gridWas);
            PowerNet.Recompute(Tree);
            yield break;
        }
    }
}
