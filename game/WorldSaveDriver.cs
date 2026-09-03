using System;
using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    /// <summary>
    /// The game-side half of persistence: where the save file lives, when it is written, and when it is read
    /// back. The FORMAT and the state walk live in core (WorldSave) -- this only owns the path and the timing,
    /// because `user://` is a Godot concept and core is engine-free.
    ///
    /// ONE PATH FOR BOTH MODES. Singleplayer is the MP loopback server, so it saves through exactly this driver
    /// on exactly the same code as a dedicated server; the only difference is which file it writes. That is
    /// deliberate -- a separate SP save path is a second implementation to keep in step, and the one that gets
    /// less traffic is the one that silently rots.
    /// </summary>
    public sealed class WorldSaveDriver
    {
        /// <summary>How often a running world writes itself out. Frequent enough that a crash costs a minute,
        /// rare enough that it is not a per-frame cost on a server with a big base on it.</summary>
        public const double AutosaveSeconds = 60.0;

        readonly NetWorldServer _server;
        readonly string _mapId;
        readonly DayNightCycle _clock;
        readonly string _path;

        double _sinceSave;
        WorldSave _lastLoaded;   // carry-over source: keeps offline players' blocks alive across a save

        public string Path => _path;
        public bool HasSave => Godot.FileAccess.FileExists(_path);

        public WorldSaveDriver(NetWorldServer server, string mapId, DayNightCycle clock)
        {
            _server = server;
            _mapId = string.IsNullOrEmpty(mapId) ? "world" : mapId;
            _clock = clock;
            _path = PathFor(_mapId);
        }

        /// <summary>`UG_SAVE_DIR` moves the whole save directory, which is what a dedicated server wants: its
        /// world should live beside the service, not in the user profile of whoever launched it.</summary>
        public static string PathFor(string mapId)
        {
            string dir = System.Environment.GetEnvironmentVariable("UG_SAVE_DIR");
            if (string.IsNullOrEmpty(dir)) dir = "user://saves";
            return dir.TrimEnd('/') + "/" + Sanitize(mapId) + ".json";
        }

        static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? char.ToLowerInvariant(c) : '_');
            return sb.Length == 0 ? "world" : sb.ToString();
        }

        // ---------------------------------------------------------------- load

        /// <summary>Read the save and put the world back. Call AFTER the map has built -- the overlaid state
        /// (vehicles, containers, doors, the resource bitmaps) needs its targets to exist -- and BEFORE the
        /// socket accepts anyone, so no client ever sees the pre-restore world. Returns a line for the log.</summary>
        public string LoadIntoWorld()
        {
            if (!HasSave) return "no save at " + _path + " -- fresh world";

            string json = ReadAll(_path);
            if (json == null) return "save at " + _path + " could not be read -- fresh world";

            if (!WorldSave.TryParse(json, _mapId, out var save, out string error))
            {
                // A save we cannot read is REPORTED, never silently skipped. The failure mode this avoids is a
                // player losing a base to a format bump and seeing only an ordinary empty world, with nothing
                // anywhere saying why. The file is left on disk so it can still be recovered by hand.
                GD.PrintErr($"[save] refusing {_path}: {error}. Starting a FRESH world; the file is untouched.");
                return "save refused (" + error + ") -- fresh world, file kept";
            }

            _lastLoaded = save;
            _server.PendingSave = save;                       // players restore out of this as they connect
            save.ApplyWorld(_server, _server.Session.CurrentTick);

            if (_clock != null)
            {
                _clock.Day = save.Day;
                _clock.Time = save.TimeOfDay01;
                if (save.DayLengthSeconds > 0f) _clock.DayLength = save.DayLengthSeconds;
                _clock.Apply();
            }

            return $"loaded {_path}: day {save.Day}, {save.Players.Count} player(s), "
                 + $"{save.Deployables.Count} deployable(s), {save.WorldItems.Count} dropped item(s)";
        }

        // ---------------------------------------------------------------- save

        public void Tick(double delta)
        {
            _sinceSave += delta;
            if (_sinceSave < AutosaveSeconds) return;
            _sinceSave = 0.0;
            SaveNow();
        }

        /// <summary>Write the world out. Safe to call at any time; returns false and logs on failure rather
        /// than throwing into a game tick.</summary>
        public bool SaveNow()
        {
            try
            {
                var save = WorldSave.Capture(_server, _mapId,
                                             _clock?.Day ?? 0,
                                             _clock?.Time ?? 0f,
                                             _clock?.DayLength ?? DayNightCycle.DefaultDayLength,
                                             carryOver: _lastLoaded);
                if (!WriteAll(_path, save.ToJson())) return false;
                // The freshly written state becomes the carry-over source, so a player who logs off does not
                // get dropped by the NEXT autosave -- the block that outlived them stays in the chain.
                _lastLoaded = save;
                return true;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[save] write failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // ---------------------------------------------------------------- wipe

        /// <summary>Delete the save and forget it. The LIVE world is left standing on purpose -- see the note on
        /// the `wipe` verb in ServerTransactions.RunConsole -- so the returned line says that plainly instead of
        /// letting an admin believe the running server has already been cleared.</summary>
        public string Wipe()
        {
            _server.PendingSave = null;
            _lastLoaded = null;
            _sinceSave = 0.0;

            if (!HasSave) return "no save file to delete; the running world is unchanged";

            var dir = DirAccess.Open(_path.GetBaseDir());
            if (dir == null || dir.Remove(_path.GetFile()) != Error.Ok)
                return "could not delete " + _path;

            return "save deleted (" + _path + "). The running world is UNCHANGED -- restart the server for a fresh one.";
        }

        // ---------------------------------------------------------------- file io

        static string ReadAll(string path)
        {
            using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            return f?.GetAsText();
        }

        static bool WriteAll(string path, string text)
        {
            string dir = path.GetBaseDir();
            if (!DirAccess.DirExistsAbsolute(dir))
            {
                var err = DirAccess.MakeDirRecursiveAbsolute(dir);
                if (err != Error.Ok) { GD.PrintErr($"[save] cannot create {dir}: {err}"); return false; }
            }
            // Write to a temp file and swap. A crash midway through a direct write leaves a TRUNCATED save --
            // which parses as far as it goes and then fails, costing the whole world. The swap makes the file
            // either the old one or the new one, never half of either.
            string tmp = path + ".tmp";
            using (var f = Godot.FileAccess.Open(tmp, Godot.FileAccess.ModeFlags.Write))
            {
                if (f == null) { GD.PrintErr($"[save] cannot open {tmp}: {Godot.FileAccess.GetOpenError()}"); return false; }
                f.StoreString(text);
            }
            var d = DirAccess.Open(path.GetBaseDir());
            if (d == null) return false;
            if (d.FileExists(path.GetFile())) d.Remove(path.GetFile());
            return d.Rename(tmp.GetFile(), path.GetFile()) == Error.Ok;
        }
    }
}
