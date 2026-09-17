using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // The launcher is a Windows Avalonia app; its window cannot be driven on this box. These two pieces of
    // logic were extracted precisely because both shipped broken and neither could have been caught by any
    // test that existed -- there were none for the launcher at all.
    [TestFixture]
    public class LauncherRulesTests
    {
        // A fake disk. Godot mono ships the pair side by side, which is the whole premise of the toggle.
        static System.Func<string, bool> Disk(params string[] files)
        {
            var set = new System.Collections.Generic.HashSet<string>(files, System.StringComparer.OrdinalIgnoreCase);
            return p => set.Contains(p);
        }

        const string Win = @"C:\g\Godot_v4.6-stable_mono_win64.exe";
        const string Con = @"C:\g\Godot_v4.6-stable_mono_win64_console.exe";

        [Test]
        public void TickedPicksTheConsoleBuildAndUntickedPicksTheWindowedOne()
        {
            var disk = Disk(Win, Con);
            Assert.That(LauncherRules.GodotExeFor(Win, wantConsole: true, disk).Exe, Is.EqualTo(Con));
            Assert.That(LauncherRules.GodotExeFor(Win, wantConsole: false, disk).Exe, Is.EqualTo(Win));
        }

        // ⭐ THE ACTUAL BUG. ResolveGodot honours UNTURNED_GODOT_EXE and a bare "godot" on PATH, either of
        // which can already BE the console build. The old code only ever APPENDED "_console", so on such a
        // machine unticking the box changed nothing -- and it logged "launching the windowed build" while
        // launching the console one. Starting FROM the console exe is the case that must come back windowed.
        [Test]
        public void UntickingTurnsItOffEvenWhenTheResolvedGodotIsAlreadyTheConsoleBuild()
        {
            var disk = Disk(Win, Con);
            var off = LauncherRules.GodotExeFor(Con, wantConsole: false, disk);
            Assert.That(off.Exe, Is.EqualTo(Win), "unticking must strip _console, not no-op");
            Assert.That(off.Satisfied, Is.True);
            // ...and the other direction from the same start is a no-op that still reports success.
            Assert.That(LauncherRules.GodotExeFor(Con, wantConsole: true, disk).Exe, Is.EqualTo(Con));
        }

        // The log line is part of the contract: the old one asserted an action it had not taken. When the
        // wanted build is absent we fall back AND say the preference was not honoured.
        [Test]
        public void AnAbsentBuildFallsBackAndAdmitsIt()
        {
            var onlyWindowed = Disk(Win);
            var r = LauncherRules.GodotExeFor(Win, wantConsole: true, onlyWindowed);
            Assert.That(r.Exe, Is.EqualTo(Win), "fall back to what we have");
            Assert.That(r.Satisfied, Is.False, "and the caller must be able to say so");

            var onlyConsole = Disk(Con);
            var r2 = LauncherRules.GodotExeFor(Con, wantConsole: false, onlyConsole);
            Assert.That(r2.Exe, Is.EqualTo(Con));
            Assert.That(r2.Satisfied, Is.False);
        }

        [Test]
        public void ExtensionlessAndNullInputsDoNotThrow()
        {
            var unix = Disk("/opt/godot/Godot_v4.6_console", "/opt/godot/Godot_v4.6");
            Assert.That(LauncherRules.GodotExeFor("/opt/godot/Godot_v4.6", true, unix).Exe,
                        Is.EqualTo("/opt/godot/Godot_v4.6_console"));
            Assert.That(LauncherRules.GodotExeFor(null, true, unix).Exe, Is.Null);
            Assert.That(LauncherRules.GodotExeFor(Win, true, null).Exe, Is.EqualTo(Win));
        }

        // ---- the branch-dropdown race -------------------------------------------------------------------

        [Test]
        public void AFirstRequestRunsImmediately()
        {
            var c = new RefreshCoalescer();
            Assert.That(c.Request(), Is.True);
            Assert.That(c.Busy, Is.True);
            Assert.That(c.Done(), Is.False, "nothing queued -> idle");
            Assert.That(c.Busy, Is.False);
        }

        // ⭐ THE ACTUAL BUG: the old handler returned early while busy, BEFORE recording the branch, so the
        // selection was lost and the dropdown disagreed with what Update would fetch.
        [Test]
        public void ASelectionMadeWhileBusyIsQueuedNotDropped()
        {
            var c = new RefreshCoalescer();
            Assert.That(c.Request(), Is.True);            // first refresh starts
            Assert.That(c.Request(), Is.False, "queued, not run");
            Assert.That(c.Pending, Is.True, "and NOT discarded -- this is the whole bug");
            Assert.That(c.Done(), Is.True, "the queued one runs when the first lands");
            Assert.That(c.Busy, Is.True, "still busy: we went straight into the queued refresh");
            Assert.That(c.Done(), Is.False);
            Assert.That(c.Busy, Is.False);
        }

        // Flicking through six branches costs ONE refresh of the sixth, not six refreshes. Coalescing is the
        // reason this is a flag and not a queue.
        [Test]
        public void ManyRequestsWhileBusyCoalesceToExactlyOne()
        {
            var c = new RefreshCoalescer();
            c.Request();
            for (int i = 0; i < 6; i++) Assert.That(c.Request(), Is.False);
            Assert.That(c.Done(), Is.True, "one queued refresh");
            Assert.That(c.Done(), Is.False, "and only one");
        }
    }
}
