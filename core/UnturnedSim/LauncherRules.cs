namespace SDG.Unturned
{
    /// <summary>The launcher's two decisions that are logic rather than layout, pulled out of MainWindow so
    /// they can be tested. The launcher is a Windows Avalonia app and its window cannot be driven headlessly
    /// on the build box, so anything left inside it is verified by clicking -- which is how both of the bugs
    /// below shipped. Linked into the launcher csproj exactly as ProfileRules is; depends on nothing but
    /// System.</summary>
    public static class LauncherRules
    {
        /// <summary>Which Godot binary to launch for a given "debug console" preference.
        ///
        /// ⚠ THE BUG THIS EXISTS TO KILL (strawberry 2026-09-17: "make the toggle for debug console actually
        /// have an effect when the game launches, bc its broken rn"). The old code was ONE-DIRECTIONAL: it
        /// appended "_console" when the box was ticked and did nothing at all when it was not. That is only
        /// correct if the resolved Godot is always the windowed build -- and it is not, because ResolveGodot
        /// honours UNTURNED_GODOT_EXE and a bare "godot" on PATH, either of which can already BE a console
        /// build. On such a machine unticking the box changed nothing whatsoever, and the launcher logged
        /// "launching the windowed build" while launching the console one. A toggle that cannot turn a thing
        /// OFF is not a toggle, and a log line that asserts an action it did not take is worse than silence.
        ///
        /// So the choice is symmetric: strip any existing "_console" to get the base name, then add it back
        /// only if the caller wants it. <paramref name="exists"/> is injected so this is testable without a
        /// filesystem (and so the caller can probe however it likes).</summary>
        public static GodotChoice GodotExeFor(string resolved, bool wantConsole, System.Func<string, bool> exists)
        {
            if (string.IsNullOrEmpty(resolved)) return new GodotChoice(resolved, satisfied: false);
            if (exists == null) return new GodotChoice(resolved, satisfied: false);

            // ⚠ NOT Path.GetExtension. A Godot binary is called "Godot_v4.6-stable_mono_win64.exe", and an
            // extension-less unix build is "Godot_v4.6" -- on which GetExtension returns ".6", because the
            // VERSION NUMBER contains a dot. Splitting on that yields "Godot_v4_console.6", a file that
            // cannot exist, so the toggle would silently fall back on exactly the platform where nothing
            // else would notice. The original inline code had this right by testing for ".exe" explicitly;
            // this is that, kept. (Caught by ExtensionlessAndNullInputsDoNotThrow, which is the entire
            // argument for extracting this function rather than tidying it in place.)
            const string Exe = ".exe";
            const string Suffix = "_console";
            string stem = resolved, ext = "";
            if (stem.EndsWith(Exe, System.StringComparison.OrdinalIgnoreCase))
            { ext = stem.Substring(stem.Length - Exe.Length); stem = stem.Substring(0, stem.Length - Exe.Length); }

            // The base is the windowed build's: Godot ships Foo.exe AND Foo_console.exe side by side.
            if (stem.EndsWith(Suffix, System.StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - Suffix.Length);

            string want = (wantConsole ? stem + Suffix : stem) + ext;
            if (exists(want)) return new GodotChoice(want, satisfied: true);

            // The wanted build is not on disk. Fall back to what we resolved, and SAY SO rather than claiming
            // the preference was honoured -- the caller logs Satisfied, not its own assumption.
            return new GodotChoice(resolved, satisfied: false);
        }

        /// <summary>The result of <see cref="GodotExeFor"/>: the binary to run, and whether the requested
        /// console preference was actually honoured. Satisfied exists so the caller can log the truth.</summary>
        public readonly struct GodotChoice
        {
            public readonly string Exe;
            public readonly bool Satisfied;
            public GodotChoice(string exe, bool satisfied) { Exe = exe; Satisfied = satisfied; }
        }
    }

    /// <summary>Serialises "refresh the branch view" so a request that arrives mid-refresh is QUEUED rather
    /// than dropped.
    ///
    /// ⚠ THE BUG THIS EXISTS TO KILL (strawberry 2026-09-17: "sometimes when i switch branches from the
    /// dropdown too quickly, it doesnt register the new branch i selected, so i download the other branch
    /// instead"). The old handler opened with `if (_mode == Mode.Busy) return;` -- so a selection made while
    /// a refresh was in flight was thrown away BEFORE the branch was recorded. The dropdown kept showing the
    /// new branch while _branch and branch.txt still held the old one, and the next Update fetched the old
    /// one. The UI and the thing it controls disagreed, silently, and the visible half was the wrong half.
    ///
    /// The rule is that a user's selection is never discarded: record it first, then either refresh now or
    /// queue exactly one refresh to run when the current one lands. Coalescing to ONE pending refresh is the
    /// point -- flicking through six branches should cost one refresh of the sixth, not six refreshes.</summary>
    public sealed class RefreshCoalescer
    {
        bool _busy, _pending;

        public bool Busy => _busy;
        public bool Pending => _pending;

        /// <summary>Ask to refresh. True: run it now. False: a refresh is already running and this one has
        /// been queued -- <see cref="Done"/> will hand it back.</summary>
        public bool Request()
        {
            if (_busy) { _pending = true; return false; }
            _busy = true;
            return true;
        }

        /// <summary>A refresh finished. True: one was queued while it ran, so run again NOW (and stay busy).
        /// False: nothing queued, we are idle.</summary>
        public bool Done()
        {
            if (_pending) { _pending = false; return true; }   // stays busy: the caller is about to run again
            _busy = false;
            return false;
        }
    }
}
