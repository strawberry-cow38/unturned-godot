using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Retail audio ripped 2026-09-03 (master: "grab whatever sfx, music etc we're missing") into game/content/audio/:
    //   footsteps/<surface>_<walk|run>_NN.wav   landing/<surface>_NN.wav   casings/<surface>_NN.wav + shell_<surface>_NN.wav
    //   bulletimpacts/<surface>_NN.wav   meleeimpacts/  swim/<gait>wading_NN.wav   music/<map>_loop|outro.ogg + death.ogg
    //   ambience/defaultrain|defaultsnowambience.ogg + thunder_*.ogg + cave/waterfall   impacts/ explosions/ foley/ items/ animals/ vehicles/ misc/
    // This is the tiny runtime side: variation banks that pick a random clip (never the same twice in a row), one-shot
    // positional players that free themselves, and the music player. Loads lazily and caches; a missing bank is silent.
    public static class GameAudio
    {
        static readonly Dictionary<string, AudioStream[]> _banks = new();
        static readonly Dictionary<string, int> _last = new();
        static readonly RandomNumberGenerator _rng = new();
        static readonly bool _dbg = System.Environment.GetEnvironmentVariable("UG_AUDIODBG") == "1";   // print every bank pick / one-shot (verification)
        /// <summary>UG_AUDIODBG=1: resolve every bank the code can emit and print the ones that are EMPTY. A missing
        /// bank and a present one both hand the caller something playable, so a live run sounds fine and just wrong;
        /// this is the check that catches it (tinyclaw, 2026-09-03: retail has no dirt_run, sprinting on dirt played pavement).</summary>
        public static void AuditBanks()
        {
            if (!_dbg) return;
            var want = new List<(string, string)>();
            foreach (PlayerController.Surf sf in System.Enum.GetValues(typeof(PlayerController.Surf)))
            {
                want.Add(("footsteps", FootSurface(sf) + "_walk")); want.Add(("footsteps", FootSurface(sf) + "_run"));
                want.Add(("landing", LandSurface(sf))); want.Add(("bulletimpacts", BulletSurface(sf)));
                var ms = MeleeSurface(sf); if (ms != null) want.Add(("meleeimpacts", ms));
            }
            foreach (var c in new[] { "general", "metal", "wood", "sand", "water" }) want.Add(("casings", c));
            foreach (var g in new[] { "lightwading", "mediumwading", "heavywading" }) want.Add(("swim", g));
            want.Add(("explosions", "bomb_fire")); want.Add(("misc", "popup_ui_menu_popup")); want.Add(("animals", "cow_panic")); want.Add(("animals", "pig_panic")); want.Add(("ambience", "thunder_lightning_strike_rumble"));
            int empty = 0;
            foreach (var (f, pfx) in want) if (Bank(f, pfx).Length == 0) { empty++; GD.PrintErr($"[audio] EMPTY BANK {f}/{pfx}"); }
            GD.Print($"[audio] bank audit: {want.Count - empty}/{want.Count} present");
            // The other half (tinyclaw): banks ON DISK that no code path can ask for. Different query; a Surf value
            // the enum lacks (gravel, ice, mud, snow, dirtloose, metalhigh...) shows up here, not above.
            var asked = new HashSet<string>(); foreach (var (f, pfx) in want) asked.Add(f + "/" + pfx);
            var orphan = new List<string>();
            foreach (var folder in new[] { "footsteps", "landing", "bulletimpacts", "casings", "meleeimpacts", "swim" })
            {
                string dir = ProjectSettings.GlobalizePath($"res://content/audio/{folder}");
                if (!System.IO.Directory.Exists(dir)) continue;
                var seen = new HashSet<string>();
                foreach (var file in System.IO.Directory.GetFiles(dir))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(System.IO.Path.GetFileName(file), @"^(.+?)_\d+\.(wav|ogg)$");
                    if (m.Success && seen.Add(m.Groups[1].Value) && !asked.Contains(folder + "/" + m.Groups[1].Value)) orphan.Add(folder + "/" + m.Groups[1].Value);
                }
            }
            if (orphan.Count > 0) GD.Print($"[audio] {orphan.Count} banks on disk nothing asks for: {string.Join(", ", orphan)}");
        }

        /// <summary>All clips named `<prefix>_NN.wav|ogg` under content/audio/<folder>, in order. Empty if none.</summary>
        public static AudioStream[] Bank(string folder, string prefix)
        {
            string key = folder + "/" + prefix;
            if (_banks.TryGetValue(key, out var b)) return b;
            var list = new List<AudioStream>();
            string dir = ProjectSettings.GlobalizePath($"res://content/audio/{folder}");
            if (System.IO.Directory.Exists(dir))
            {
                var files = new List<string>();
                foreach (var ext in new[] { ".wav", ".ogg" })
                    foreach (var f in System.IO.Directory.GetFiles(dir, prefix + "_*" + ext)) files.Add(f);
                files.Sort(System.StringComparer.OrdinalIgnoreCase);
                foreach (var f in files) { var s = Load(f); if (s != null) list.Add(s); }
            }
            if (_dbg) GD.Print($"[audio] bank {key}: {list.Count} clips");
            return _banks[key] = list.ToArray();
        }

        /// <summary>One clip: content/audio/<folder>/<name>.wav|ogg (null if missing).</summary>
        public static AudioStream Clip(string folder, string name)
        {
            string key = folder + "/=" + name;
            if (_banks.TryGetValue(key, out var b)) return b.Length > 0 ? b[0] : null;
            string dir = ProjectSettings.GlobalizePath($"res://content/audio/{folder}");
            AudioStream s = null;
            foreach (var ext in new[] { ".wav", ".ogg" }) { string p = System.IO.Path.Combine(dir, name + ext); if (System.IO.File.Exists(p)) { s = Load(p); if (s != null) break; } }
            _banks[key] = s != null ? new[] { s } : System.Array.Empty<AudioStream>();
            return s;
        }

        static AudioStream Load(string path)
        {
            try
            {
                if (path.EndsWith(".ogg", System.StringComparison.OrdinalIgnoreCase)) return AudioStreamOggVorbis.LoadFromFile(path);
                return PlayerController.LoadWavOneShot("res://content/audio/" + path.Substring(ProjectSettings.GlobalizePath("res://content/audio/").Length).Replace('\\', '/'));
            }
            catch (System.Exception e) { GD.PrintErr($"[audio] {path}: {e.Message}"); return null; }
        }

        /// <summary>Random member of a bank, never the same index twice in a row (retail's variation rule).</summary>
        public static AudioStream Pick(string folder, string prefix)
        {
            var b = Bank(folder, prefix);
            if (b.Length == 0) return null;
            if (b.Length == 1) return b[0];
            string key = folder + "/" + prefix;
            _last.TryGetValue(key, out int last);
            int i = _rng.RandiRange(0, b.Length - 2); if (i >= last) i++;
            _last[key] = i;
            return b[i];
        }

        /// <summary>Positional one-shot at `pos`, self-freeing. Returns the player (for a pitch tweak) or null.</summary>
        public static AudioStreamPlayer3D PlayAt(Node scene, AudioStream a, Vector3 pos, float volumeDb = 0f, float unitSize = 6f, float maxDistance = 60f, float pitch = 1f)
        {
            if (a == null || scene == null || !scene.IsInsideTree()) return null;
            var pl = new AudioStreamPlayer3D { Stream = a, UnitSize = unitSize, MaxDistance = maxDistance, VolumeDb = volumeDb, PitchScale = pitch };
            if (_dbg) GD.Print($"[audio3d] {a.ResourcePath}{(a.ResourcePath == "" ? a.GetType().Name : "")} at {pos} vol={volumeDb:0}");
            scene.GetTree().Root.AddChild(pl);
            pl.GlobalPosition = pos;
            pl.Play();
            pl.Finished += () => { if (GodotObject.IsInstanceValid(pl)) pl.QueueFree(); };
            return pl;
        }

        /// <summary>Non-positional one-shot (UI / the local player's own body), self-freeing.</summary>
        public static AudioStreamPlayer Play2D(Node scene, AudioStream a, float volumeDb = 0f, float pitch = 1f)
        {
            if (a == null || scene == null || !scene.IsInsideTree()) return null;
            var pl = new AudioStreamPlayer { Stream = a, VolumeDb = volumeDb, PitchScale = pitch };
            scene.GetTree().Root.AddChild(pl);
            pl.Play();
            pl.Finished += () => { if (GodotObject.IsInstanceValid(pl)) pl.QueueFree(); };
            return pl;
        }

        /// <summary>Retail Bomb explosion (effects/explosions/bomb_N/fire) at a point, radius-scaled loudness. One-shot.</summary>
        public static void Explosion(Node scene, Vector3 at, float radius)
        {
            var clip = Pick("explosions", "bomb_fire");   // bomb_fire_00..06.wav (retail effects/explosions/bomb_N/fire; renamed so the bank regex sees one prefix)
            PlayAt(scene, clip, at, Mathf.Clamp(-2f + radius * 0.6f, -2f, 6f), 12f, 260f, _rng.RandfRange(0.95f, 1.05f));
        }
        /// <summary>UI click: retail sounds/popup/ui_menu_popup_N (2D).</summary>
        public static void UiPopup(Node scene, float db = -6f) => Play2D(scene, Pick("misc", "popup_ui_menu_popup"), db);
        /// <summary>Material under a node's feet: water when below sea level, else a short down-ray to the terrain splatmap
        /// or a prop's SurfMeta. Shared by the local player and the remote puppets.</summary>
        public static PlayerController.Surf SurfaceUnder(Node3D n, Rid exclude)
        {
            var gp = n.GlobalPosition;
            if (Terrain.HasWater && gp.Y < Terrain.SeaLevelY + 0.1f) return PlayerController.Surf.Water;
            var space = n.GetWorld3D()?.DirectSpaceState; if (space == null) return PlayerController.Surf.Concrete;
            var q = PhysicsRayQueryParameters3D.Create(gp + Vector3.Up * 0.3f, gp + Vector3.Down * 0.6f, 1u << 0, exclude.IsValid ? new Godot.Collections.Array<Rid> { exclude } : null);
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) return PlayerController.Surf.Concrete;
            if (hit["collider"].As<GodotObject>() is Node c)
            {
                if (Terrain.Active != null && c.IsInGroup("terrain")) return Terrain.Active.SurfAt(gp.X, gp.Z);
                if (c.HasMeta(PlayerController.SurfMeta)) return (PlayerController.Surf)(int)c.GetMeta(PlayerController.SurfMeta);
            }
            return PlayerController.Surf.Concrete;
        }
        public static string MeleeSurface(PlayerController.Surf s) => s switch { PlayerController.Surf.Metal => "metallight", PlayerController.Surf.Grass => "grass", _ => null };

        // ---- surface names shared by the footstep / landing / casing / bullet-impact banks ----
        public static string FootSurface(PlayerController.Surf s) => s switch
        {
            PlayerController.Surf.Concrete => "concrete", PlayerController.Surf.Grass => "grass", PlayerController.Surf.Dirt => "dirt",
            PlayerController.Surf.Metal => "metallow", PlayerController.Surf.Wood => "wood", PlayerController.Surf.Sand => "sand",
            PlayerController.Surf.Water => "water", _ => "concrete",
        };
        public static string LandSurface(PlayerController.Surf s) => s == PlayerController.Surf.Metal ? "metal" : FootSurface(s);

        /// <summary>A footstep clip for this surface and gait, with the fallback that matters: retail ships NO
        /// dirt_run bank (12 walk clips, 0 run), so asking for one used to miss and drop straight to CONCRETE --
        /// sprinting across a field sounded like sprinting down a pavement. Degrade to the SAME material's walk
        /// bank first and only then to concrete, so a missing gait costs you the gait, never the ground you are
        /// standing on. Shared by the local shell and the remote puppets so both hear the same thing.</summary>
        public static AudioStream PickFootstep(PlayerController.Surf surf, bool run)
        {
            string mat = FootSurface(surf);
            return (run ? Pick("footsteps", mat + "_run") : null)
                ?? Pick("footsteps", mat + "_walk")
                ?? Pick("footsteps", "concrete" + (run ? "_run" : "_walk"));
        }
        public static string BulletSurface(PlayerController.Surf s) => s switch
        {
            PlayerController.Surf.Metal => "metallight", PlayerController.Surf.Wood => "woodlight",
            PlayerController.Surf.Sand => "gravel",   // retail ships no sand bullet bank (audit 2026-09-03: a missing bank returns null and the old single wav takes over -- gravel is the retail choice)
            _ => FootSurface(s),
        };
    }

    /// <summary>Retail's per-map music: a loop while you play, the map's outro sting on death (death.ogg for maps
    /// without one). One node under the scene root; survives menu->world since it re-targets rather than rebuilds.
    /// Volume from GraphicsOptions... no -- from AudioOptions.Music (0..1), applied live.</summary>
    public partial class MusicPlayer : Node
    {
        static MusicPlayer _inst;
        AudioStreamPlayer _a, _b; string _current; float _fadeT; bool _fading;
        public static float Volume = 0.35f;   // linear; retail's default music slider sits low under the SFX
        public static MusicPlayer Get(Node any)
        {
            if (_inst != null && GodotObject.IsInstanceValid(_inst)) return _inst;
            var tree = any?.GetTree(); if (tree == null) return null;
            _inst = new MusicPlayer { Name = "Music", ProcessMode = ProcessModeEnum.Always };
            tree.Root.CallDeferred(Node.MethodName.AddChild, _inst);
            return _inst;
        }
        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            _a = new AudioStreamPlayer { Bus = "Master" }; _b = new AudioStreamPlayer { Bus = "Master" };
            AddChild(_a); AddChild(_b);
        }
        /// <summary>Crossfade to content/audio/music/<name>.ogg (looping). Same name = no-op. null = fade out.</summary>
        public void PlayLoop(string name, float fade = 2.5f)
        {
            if (name == _current) return;
            _current = name;
            var s = name != null ? GameAudio.Clip("music", name) : null;
            if (s is AudioStreamOggVorbis ogg) ogg.Loop = true;
            (_a, _b) = (_b, _a);   // the new track always plays on _a; _b fades out
            if (_a == null) return;
            _a.Stream = s; _a.VolumeDb = Mathf.LinearToDb(0.0001f);
            if (s != null) _a.Play(); else _a.Stop();
            _fadeT = 0f; _fading = true; _fadeLen = Mathf.Max(0.05f, fade);
        }
        float _fadeLen = 2.5f;
        /// <summary>A one-shot over the loop (the map outro / death sting); the loop keeps going underneath.</summary>
        public void Sting(string name)
        {
            var s = GameAudio.Clip("music", name); if (s == null) return;
            var p = new AudioStreamPlayer { Stream = s, VolumeDb = Mathf.LinearToDb(Mathf.Clamp(Volume, 0.0001f, 1f)) };
            AddChild(p); p.Play(); p.Finished += () => { if (GodotObject.IsInstanceValid(p)) p.QueueFree(); };
        }
        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            if (_a == null || _b == null) return;
            float target = Mathf.LinearToDb(Mathf.Clamp(Volume, 0.0001f, 1f));
            if (_fading)
            {
                _fadeT += (float)delta; float k = Mathf.Clamp(_fadeT / _fadeLen, 0f, 1f);
                if (_a.Stream != null) _a.VolumeDb = Mathf.LinearToDb(Mathf.Max(0.0001f, Volume * k));
                _b.VolumeDb = Mathf.LinearToDb(Mathf.Max(0.0001f, Volume * (1f - k)));
                if (k >= 1f) { _fading = false; _b.Stop(); _b.Stream = null; }
            }
            else if (_a.Stream != null) _a.VolumeDb = target;   // live slider
        }
    }
}
