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
    // THREE DIFFERENT PICTURES, AND THEY ARE NOT INTERCHANGEABLE (strawberry 2026-09-15: "the loading into maps
    // should use the official map screenshots (not community screenshots). community screenshots are just the
    // loading into main menu shots. the small low res pics you have are the menu ICONS for the maps").
    //
    // Loading into a MAP shows that map's own official 4K screenshot and credits nobody, because the game's
    // authors made it. Loading into the MENU shows a random community screenshot and MUST name whoever took it.
    // The map picker shows a 320x180 icon and credits nobody either.
    //
    // ⚠ The art is read from the player's Unturned install, not from git, so these skip when there is no install
    // rather than failing -- a CI box with no Unturned is not a broken loading screen. The build box HAS one, so
    // the real paths do get exercised.
    //
    // Asserted rather than rendered: the loading cover is torn down before a --shot frame lands (tried three
    // capture times), and --menushot drives the 3D barn's CAMERA anchors, not its UI panels (tried two glide
    // budgets). The limitation is structural, not effort.
    public sealed class LoadingScreenArtTests : GameTest
    {
        public override string Name => "load.art_sources";

        public override IEnumerable<Step> Run()
        {
            bool haveInstall = MapShots.CommunityShots().Count > 0;
            T.Check($"an Unturned install to read art from ({MapShots.InstallRoot})", true);   // informational
            if (!haveInstall)
            {
                T.Check("no install on this box -> art tests skipped, fallback icon path is what runs", true);
                yield break;
            }

            string prevMap = System.Environment.GetEnvironmentVariable("UG_LOADMAP");
            string prevMode = System.Environment.GetEnvironmentVariable("UG_LOADMODE");
            // ⚠ try/FINALLY around the whole body, because these are PROCESS-WIDE env vars. Restoring them on
            // the happy path only means any abort in the middle -- an exception, a harness timeout -- leaves
            // UG_LOADMODE set for every test that runs after this one. That is precisely the order-dependent
            // leak shape tinyclaw is currently hunting two of on main, and it costs one keyword not to add a
            // third. (C# iterators permit try/finally around a yield; only try/CATCH is disallowed.)
            try
            {

            // ---- 1. INTO A MAP: the map's OWN official screenshot, no photographer.
            System.Environment.SetEnvironmentVariable("UG_LOADMODE", "map");
            foreach (string key in new[] { "pei", "washington", "russia", "yukon", "germany" })
            {
                System.Environment.SetEnvironmentVariable("UG_LOADMAP", key);
                var ls = new LoadingScreen();
                World.AddChild(ls);
                yield return Ticks(2);

                var sz = ls.DebugShotSize;
                T.Check($"{key}: the official map shot loaded ({sz.X}x{sz.Y})", sz.X > 1920);
                T.Check($"{key}: ...and credits nobody, being the game's own art ('{ls.DebugCredit}')",
                        string.IsNullOrEmpty(ls.DebugCredit));
                ls.QueueFree();
                yield return Ticks(1);
            }

            // ---- 2. INTO THE MENU: a community screenshot, and it MUST be credited.
            System.Environment.SetEnvironmentVariable("UG_LOADMODE", "launch");
            var seen = new HashSet<string>();
            for (int i = 0; i < 6; i++)
            {
                var ls = new LoadingScreen();
                World.AddChild(ls);
                yield return Ticks(2);
                var sz = ls.DebugShotSize;
                T.Check($"menu load {i}: a community shot loaded ({sz.X}x{sz.Y})", sz.X > 1920);
                T.Check($"menu load {i}: ...credited to its author ('{ls.DebugCredit}')",
                        !string.IsNullOrEmpty(ls.DebugCredit) && ls.DebugCredit.Contains(" by "));
                if (!string.IsNullOrEmpty(ls.DebugCredit)) seen.Add(ls.DebugCredit);
                ls.QueueFree();
                yield return Ticks(1);
            }
            // ⭐ It is a POOL, not one picture. Six draws from 39 landing on a single shot every time would mean
            // the randomness is dead -- which is exactly what "random pool" was asked for.
            T.Check($"the menu pool is actually random ({seen.Count} distinct across 6 loads)", seen.Count > 1);

            }
            finally
            {
                System.Environment.SetEnvironmentVariable("UG_LOADMAP", prevMap);
                System.Environment.SetEnvironmentVariable("UG_LOADMODE", prevMode);
            }
        }
    }

    // The map picker shows an ICON, and an icon has no photographer. Separate from the art test above because it
    // needs no install at all -- the icons are ours and always shipped.
    public sealed class MenuIconTests : GameTest
    {
        public override string Name => "menu.map_icon_uncredited";

        public override IEnumerable<Step> Run()
        {
            var menu = new MainMenu();
            World.AddChild(menu);
            yield return Ticks(2);
            var layer = new CanvasLayer();
            World.AddChild(layer);
            menu.DebugBuildMapSelectorForTest(layer);
            yield return Ticks(2);

            // ⚠ THE SIZE CHECK BELONGS INSIDE THE LOOP. It used to sit after it, so it only ever measured the
            // LAST key -- and that was "playground", the one map with no mappreview_playground.png shipped at
            // all. It resolved to nothing, read 0x0, and failed. The credit checks were fine because crediting
            // nobody is the right answer for a missing icon; it was the assertion that escaped the loop and
            // landed on the single key that has no asset. (tinyclaw found it.)
            foreach (string key in new[] { "pei", "washington" })
            {
                menu.DebugSelectMapForTest(key);
                yield return Ticks(1);
                T.Check($"{key}: the picker shows an icon, and names no photographer ('{menu.DebugPreviewCredit}')",
                        string.IsNullOrEmpty(menu.DebugPreviewCredit));
                var sz = menu.DebugPreviewSize;
                T.Check($"{key}: ...and it is the small icon, not a 4K screenshot ({sz.X}x{sz.Y})",
                        sz.X > 0 && sz.X <= 640);
            }

            // Playground ships no icon at all, so it gets its own case: no art, and therefore no credit. Asking
            // it for a size is what broke this test, and it is not a claim worth making about a map with no image.
            menu.DebugSelectMapForTest("playground");
            yield return Ticks(1);
            T.Check($"the gun range has no icon shipped, and so credits nobody ('{menu.DebugPreviewCredit}')",
                    string.IsNullOrEmpty(menu.DebugPreviewCredit));

            layer.QueueFree(); menu.QueueFree();
            yield return Ticks(2);
        }
    }

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
