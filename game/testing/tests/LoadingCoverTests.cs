using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE LOADING COVER MUST OUTLAST THE SHADER WARM, AND MUST COME DOWN ANYWAY (strawberry 2026-09-10: "hide the
    // shader pre-load that happens when loading into a map").
    //
    // The warm draws real quads 0.6 m in front of the camera, because a shader that is not drawn is not compiled --
    // so it can only be COVERED, never hidden. That makes the cover load-bearing in a way it was not before, and
    // the failure mode of holding it too long (a player stuck staring at a loading screen) is far worse than the
    // flash it replaces. So both directions are checked: it waits, and it gives up waiting.
    public sealed class LoadingCoverTests : GameTest
    {
        public override string Name => "load.cover_outlasts_shader_warm";

        static bool RootVisible(LoadingScreen ls) => ls.DebugCoverVisible;

        public override IEnumerable<Step> Run()
        {
            // ---- 1. nothing warming -> the cover drops immediately, as it always did.
            var a = new LoadingScreen();
            World.AddChild(a);
            yield return Ticks(2);
            T.Check("the cover is up while loading", RootVisible(a));
            a.Finish(new Dictionary<string, double> { ["world"] = 12.0 });
            T.Check("with no shader warm running, Finish drops it at once", !RootVisible(a));
            a.QueueFree();
            yield return Ticks(2);

            // ---- 2. warm in progress -> the cover STAYS until it clears.
            var b = new LoadingScreen();
            World.AddChild(b);
            yield return Ticks(2);
            ShaderWarm.SetBusyForTest(true);
            b.Finish(new Dictionary<string, double> { ["world"] = 12.0 });
            T.Check("a running shader warm holds the cover up", RootVisible(b));
            yield return Ticks(3);
            T.Check("...and it is still up several frames later", RootVisible(b));
            ShaderWarm.SetBusyForTest(false);
            yield return Ticks(2);
            T.Check("...then drops once the warm finishes", !RootVisible(b));
            b.QueueFree();
            yield return Ticks(2);

            // ---- 3. ⭐ THE ONE THAT MATTERS MORE. A warm that never clears must NOT strand the player behind the
            // cover. Without the timeout, a ShaderWarm freed in some path that misses its own cleanup would leave
            // the loading screen up forever, and that is a hang, not a graphical glitch.
            var c = new LoadingScreen();
            World.AddChild(c);
            yield return Ticks(2);
            ShaderWarm.SetBusyForTest(true);
            c.Finish(new Dictionary<string, double> { ["world"] = 12.0 });
            T.Check("a stuck warm still holds it at first", RootVisible(c));
            yield return Until(() => !RootVisible(c), 4.0);   // the 2 s patience, with room to spare
            T.Check("...but the cover gives up waiting and drops anyway", !RootVisible(c));
            ShaderWarm.SetBusyForTest(false);
            c.QueueFree();
            yield break;
        }
    }
}
