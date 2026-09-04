using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>
    /// Who this player says they are: the display name and profile picture the launcher saved, read once
    /// from the child process's environment (the same route UG_UNTURNED_DIR and UG_BUGREPORT_KEY take).
    ///
    /// The launcher owns the settings files; the game owns none of that. It reads two variables and nothing
    /// else, which is what keeps a game build runnable without a launcher at all -- no launcher, no name, and
    /// ProfileRules supplies the fallback.
    ///
    /// EVERYTHING HERE IS BEST-EFFORT AND NEVER THROWS. A missing file, an unreadable one, a path pointing at
    /// something enormous: all of them mean "no picture", never a crash on the way into a server. And the
    /// picture is validated HERE too, before it is ever sent, so the player finds out their PNG was refused
    /// at launch rather than silently having no avatar in game.
    /// </summary>
    public static class PlayerProfile
    {
        public const string NameEnv = "UG_USERNAME";
        public const string AvatarEnv = "UG_PROFILE_PNG";

        static bool _loaded;
        static string _name;
        static byte[] _avatar;
        // FACE (strawberry 2026-09-04 "port player faces from the source and original game files"): retail
        // Customization -- a new character rolls a random FREE face (0..9) and keeps it; the Appearance menu changes
        // it. Persisted in user://profile.cfg [character] face; UG_FACE=<n> overrides for renders/tests.
        public const string FaceEnv = "UG_FACE";
        const string FaceCfg = "user://profile.cfg";
        static byte _face;
        public static byte Face { get { Load(); return _face; } }
        public static void SetFace(byte face)
        {
            Load();
            _face = UnturnedGodot.Net.PlayerProfileReplication.ClampFace(face);
            try { var cfg = new ConfigFile(); cfg.Load(FaceCfg); cfg.SetValue("character", "face", (int)_face); cfg.Save(FaceCfg); }
            catch (System.Exception e) { GD.PrintErr($"[profile] could not save {FaceCfg}: {e.Message}"); }
        }
        static void LoadFace()
        {
            if (int.TryParse(System.Environment.GetEnvironmentVariable(FaceEnv), out int envFace)) { _face = UnturnedGodot.Net.PlayerProfileReplication.ClampFace((byte)Mathf.Clamp(envFace, 0, 255)); return; }
            try
            {
                var cfg = new ConfigFile();
                if (cfg.Load(FaceCfg) == Error.Ok && cfg.HasSectionKey("character", "face")) { _face = UnturnedGodot.Net.PlayerProfileReplication.ClampFace((byte)Mathf.Clamp((int)cfg.GetValue("character", "face", 0), 0, 255)); return; }
                _face = (byte)(GD.Randi() % 10);   // retail: Random.Range(0, FACES_FREE) for a fresh character
                cfg.SetValue("character", "face", (int)_face); cfg.Save(FaceCfg);
            }
            catch { _face = 0; }
        }

        public static string Name { get { Load(); return _name; } }
        public static byte[] AvatarPng { get { Load(); return _avatar; } }
        public static bool HasAvatar { get { Load(); return _avatar != null; } }

        /// <summary>Test seam: drop the cache so a test can set the environment and re-read it.</summary>
        public static void ResetForTest() { _loaded = false; _name = null; _avatar = null; _face = 0; }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            LoadFace();

            // Sanitised on the way IN, not just on the way out, so what the player is told they are called is
            // the same string the server will end up publishing. The server re-runs this on arrival anyway --
            // it does not trust us -- but agreeing here means no surprise rename on the join screen.
            string raw = System.Environment.GetEnvironmentVariable(NameEnv);
            _name = ProfileRules.SanitizeName(raw, out bool changed);
            if (!string.IsNullOrEmpty(raw) && changed)
                GD.Print($"[profile] name '{raw}' is not usable as-is -> '{_name}'");

            string path = System.Environment.GetEnvironmentVariable(AvatarEnv);
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var info = new System.IO.FileInfo(path);
                if (!info.Exists) { GD.Print($"[profile] no picture at {path}"); return; }
                // Check the SIZE before reading, not after: the point of a cap is to not have the bytes in
                // memory, and File.ReadAllBytes on a pathological path would defeat it.
                if (info.Length > ProfileRules.MaxAvatarBytes)
                {
                    GD.Print($"[profile] picture is {info.Length / 1024} KB, over the {ProfileRules.MaxAvatarBytes / 1024} KB limit -- ignored");
                    return;
                }
                var bytes = System.IO.File.ReadAllBytes(path);
                var verdict = ProfileRules.CheckAvatarPng(bytes);
                if (verdict != ProfileRules.AvatarVerdict.Ok)
                {
                    GD.Print($"[profile] picture refused: {ProfileRules.Explain(verdict)}");
                    return;
                }
                _avatar = bytes;
                GD.Print($"[profile] {_name} + a {bytes.Length / 1024f:0.0} KB picture");
            }
            catch (System.Exception ex)
            {
                GD.Print($"[profile] could not read the picture ({ex.GetType().Name}) -- continuing without one");
            }
        }

        /// <summary>Decode avatar bytes into a texture. Returns null rather than throwing on anything Godot
        /// refuses, because these bytes came off the wire from another player: a decoder failure here is an
        /// expected outcome, not an exceptional one. ProfileRules already bounded the dimensions, so this
        /// cannot be handed a gigapixel image.</summary>
        public static ImageTexture DecodeAvatar(byte[] png)
        {
            if (png == null || png.Length == 0) return null;
            var img = new Image();
            if (img.LoadPngFromBuffer(png) != Error.Ok) return null;
            if (img.GetFormat() == Image.Format.Rgb8) img.Convert(Image.Format.Rgba8);   // alpha-less avatar PNG: convert here, not with a warning at upload
            if (img.GetWidth() != ProfileRules.AvatarPixels || img.GetHeight() != ProfileRules.AvatarPixels) return null;
            return ImageTexture.CreateFromImage(img);
        }
    }
}
