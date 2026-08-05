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

            float steady = tv.DebugScreenBrightness;
            tv.FlickerPulse(0.6f);
            T.Check("a brownout pulse starts a sag", tv.DebugBrownout);
            // Sample across the pulse: the picture must actually drop somewhere in it, not merely set a flag. A sag
            // that never reaches the screen is indistinguishable from no sag at all.
            float lowest = steady;
            for (int i = 0; i < 40; i++)
            {
                yield return Ticks(1);
                lowest = Mathf.Min(lowest, tv.DebugScreenBrightness);
            }
            T.Check($"...and the picture really dips ({lowest:0.###} vs {steady:0.###} steady)", lowest < steady * 0.75f);
            yield return Ticks(60);
            T.Check("...then settles back", !tv.DebugBrownout);
            T.Check($"...to the same level it was ({tv.DebugScreenBrightness:0.###} vs {steady:0.###})",
                Mathf.Abs(tv.DebugScreenBrightness - steady) < 0.05f);
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

            PowerNet.SetGlobalPower(gridWas);
            PowerNet.Recompute(Tree);
            yield break;
        }
    }
}
