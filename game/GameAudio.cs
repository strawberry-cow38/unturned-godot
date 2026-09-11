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
            foreach (var (f, pfx) in want) if (Bank(f, pfx).Length == 0) { empty++; Log.Err($"[audio] EMPTY BANK {f}/{pfx}"); }
            Log.Print($"[audio] bank audit: {want.Count - empty}/{want.Count} present");
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
            if (orphan.Count > 0) Log.Print($"[audio] {orphan.Count} banks on disk nothing asks for: {string.Join(", ", orphan)}");
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
            if (_dbg) Log.Print($"[audio] bank {key}: {list.Count} clips");
            return _banks[key] = list.ToArray();
        }

        /// <summary>One clip: content/audio/<folder>/<name>.wav|ogg (null if missing). Reach for this over
        /// Pick whenever the exact file matters: Pick's glob is `prefix_*`, so in a folder holding both
        /// `throwables_smoke_red_use` and `throwables_smoke_red_smoke` there is no prefix that selects one of
        /// them -- asking for the pin-pull would play the canister half the time.</summary>
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
            catch (System.Exception e) { Log.Err($"[audio] {path}: {e.Message}"); return null; }
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
            if (_dbg) Log.Print($"[audio3d] {a.ResourcePath}{(a.ResourcePath == "" ? a.GetType().Name : "")} at {pos} vol={volumeDb:0}");
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
        /// <summary>AUDIO ONLY: hard ground under standing water splashes instead of clacking. Applied to a
        /// surface that has ALREADY been resolved by PlayerController.TryFootSurfaceAt, rather than re-raycasting
        /// or being folded into that probe -- Vehicle reads the very same probe for GRIP
        /// (_surfaceGrip = GripFor(surf)), and a car must not get water traction because the road is wet.
        ///
        /// This replaces a SurfaceUnder() that duplicated the whole probe, applied the puddle rule at the end of
        /// its own copy, and was called by NOTHING (strawberry 2026-09-09: "i dont hear the puddle step" -- they
        /// were right, the feature never ran). Writing a parallel implementation of a thing that already exists is
        /// how you ship a green build of dead code; the rule now lives ON the path the footsteps actually take.</summary>
        public static PlayerController.Surf PuddleAudio(Node3D n, Vector3 gp, PlayerController.Surf surf)
            => n != null && Puddled(n, gp, surf) ? PlayerController.Surf.Water : surf;

        /// <summary>How much standing water before footsteps splash. At this level the puddle shader is already
        /// reading as wet, so the sound arrives WITH the look rather than ahead of it.</summary>
        public const float PuddleAudioLevel = 0.35f;

        /// <summary>Is there standing water underfoot? (strawberry 2026-09-09: "change the walk sound on puddles to
        /// the beach splash footstep sound".) Surf.Water already maps to the shore bank, so a puddle just reports
        /// as water and every consumer -- the local shell and the remote puppets both -- follows for free.
        ///
        /// Three conditions, and the third is the one that keeps it honest: hard ground (puddles do not stand on
        /// grass), enough accumulated water to see, and OPEN SKY. The puddle level is a GLOBAL, so without the sky
        /// test the moment it rained you would splash your way across a warehouse floor.
        ///
        /// ⚠ KNOWN OVER-REACH, flagged rather than buried: the puddle SHADER paints road props only, while this
        /// says yes on any unsheltered hard surface -- so a bare concrete yard can splash with no visible puddle
        /// on it. Closing that needs the road props' colliders tagged at build time, which is a bigger change than
        /// the ask; this is the audible half, and it errs toward "it rained and the ground is wet".</summary>
        static bool Puddled(Node3D n, Vector3 gp, PlayerController.Surf surf)
        {
            if (WeatherManager.PuddleLevel < PuddleAudioLevel) return false;
            if (surf != PlayerController.Surf.Concrete && surf != PlayerController.Surf.Metal) return false;   // hard ground only
            var w = n.GetWorld3D();
            return w != null && !ShelterProbe.IsSheltered(w, gp + Vector3.Up * 0.2f);
        }
        // null = no melee bank for this surface, and the caller falls back. Only the four retail actually
        // ships as melee targets are named (grass, metal, ice, snow); the rest have no clip and saying so is
        // how the caller knows to fall back rather than play grass at a brick wall.
        public static string MeleeSurface(PlayerController.Surf s) => s switch
        {
            PlayerController.Surf.Metal => "metallight", PlayerController.Surf.Grass => "grass",
            PlayerController.Surf.Ice => "ice", PlayerController.Surf.Snow => "snow", _ => null,
        };

        // ---- surface names shared by the footstep / landing / casing / bullet-impact banks ----
        public static string FootSurface(PlayerController.Surf s) => s switch
        {
            PlayerController.Surf.Concrete => "concrete", PlayerController.Surf.Grass => "grass", PlayerController.Surf.Dirt => "dirt",
            PlayerController.Surf.Metal => "metallow", PlayerController.Surf.Wood => "wood", PlayerController.Surf.Sand => "sand",
            PlayerController.Surf.Water => "water",
            PlayerController.Surf.Gravel => "gravel", PlayerController.Surf.Snow => "snow", PlayerController.Surf.Ice => "ice",
            // Stone cliffs walk like the hard surface they are; there is no `rock` footstep bank, only a rock
            // BULLET bank -- so this is a real mapping, not a fallback, and is spelled out rather than left to
            // the default so that adding a rock bank later has one place to change.
            PlayerController.Surf.Rock => "concrete",
            _ => "concrete",
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
        // ---- FOLEY (content/audio/foley, 107 clips that nothing played until now) ------------------------------
        // Retail drives these off OneShotAudioDefinitions hung on the animations and the equipment; we have neither
        // the definitions nor those animation events, so the TRIGGER is ours and the clips are theirs. Where retail
        // has an equivalent moment the trigger matches it; where it does not, that is said at the call site rather
        // than dressed up as source-accurate.

        /// <summary>Picking something up off the ground or a shelf.</summary>
        public static AudioStream GrabItem() => Pick("foley", "foley_object_grab_pickup_rough")
                                             ?? Pick("foley", "foley_soldier_gear_equipment_movement_grab_item");

        /// <summary>One round going into a magazine. Retail has no inventory mag-loading -- these clips live on the
        /// round-by-round RELOAD animations -- so the pistol/rifle split has no source rule to copy here. Chosen on
        /// the magazine's own capacity, which is a proxy and is labelled as one: sidearm magazines are small.</summary>
        public static AudioStream MagRound(int magCapacity) =>
            Pick("foley", magCapacity <= 15 ? "gun_pistol_load_bullet" : "gun_semi_auto_rifle_load_bullet");

        /// <summary>What a harvestable RESOURCE makes when it comes down -- a felled tree, a depleted ore node.
        ///
        /// Retail reads this off the resource itself: `ResourceAsset.explosion` (ResourceAsset.cs:308) resolves via
        /// FindExplosionEffectAsset (:117) to an EffectAsset, and that prefab's single AudioSource carries the clip.
        /// Swept across all 69 retail resources with tools/extract_resource_sfx.py: they name 28 distinct effects,
        /// and 23 of those carry the BYTE-IDENTICAL `Timber` clip. Every birch, maple, pine and dead tree in the
        /// game falls with one sound.
        ///
        /// So there is no per-species tree crash to match, and the species matching that used to be here was worse
        /// than a coincidence: `birch_2_wood`/`maple_4_wood`/`pine_2_wood` are OBJECT rubble effects that all carry
        /// the generic Wood crate-break, so a falling maple or pine played a smashing crate 100% of the time and a
        /// birch did three times in four. `birch_0_timber.wav` is the real Timber clip (byte-identical to effect
        /// 21's), and `metal_2_metal.wav` the Metal one every ore and clay node shares (effect 52). Both prefixes
        /// name the retail effect they were ripped from, which is what makes them addressable at all.
        ///
        /// Bushes and mushrooms take effect 43's `Foliage` rustle, which got its caller the day forageable
        /// bushes did. ⚠ `foliage_0_foliage.wav` was MISLABELLED until then: 175584 bytes against effect
        /// Foliage_0's actual 120416-byte clip, and that prefab has exactly one AudioSource, so it was not a
        /// second source -- it was the wrong file under the right name. Replaced with the real one.
        ///
        /// Bush_0 and Bush_1 return null on purpose and are not an oversight: those two have no Explosion
        /// field at all in retail and no Forage key either, so they are scenery that neither breaks nor picks.
        /// The two Christmas resources resolve to a `Reset` chime that nothing destroys yet.</summary>
        public static AudioStream ResourceBreak(string resourceName)
        {
            string n = resourceName ?? "";
            if (n.StartsWith("Birch") || n.StartsWith("Maple") || n.StartsWith("Pine") || n.StartsWith("Dead"))
                return Pick("explosions", "birch_0");   // the shared Timber clip
            if (n.StartsWith("Metal") || n.StartsWith("Clay"))
                return Pick("explosions", "metal_2");   // the shared Metal clip
            if (n.StartsWith("Bush") || n.StartsWith("Mushroom"))
                return Pick("explosions", "foliage_0"); // effect 43, shared by every forageable bush and mushroom
            return null;
        }

        /// <summary>A keyring, for locking and unlocking a door you own.</summary>
        public static AudioStream Keys() => Pick("foley", "foley_keys_belt_metal_jingle");

        // GEAR-MOVEMENT FOLEY REMOVED (master 2026-09-11: "remove the 'walking with gear' sound"). The three
        // foley_soldier_gear_* / foley_cloth_light clips stay on disk -- they are ripped content, not ours to
        // delete -- but nothing asks for them, which is the state they were in before this was ever wired.

        // ---- THROWABLES (content/audio/items, 30 clips) ---------------------------------------------------------
        // ⚠ TWO PLACES IN Grenade.cs SAID THESE CLIPS DID NOT EXIST -- "the canister popping (no dedicated
        // retail clip in the rip)" and "no dedicated bounce clip in the rip" -- and both were playing a brass
        // CASING pitched down instead. The clips were in content/audio/items the whole time, in a folder no
        // Bank/Pick call had ever named. Retail hangs them off ItemThrowableAsset's own bundle, which is why
        // they landed under `items` rather than beside the explosion banks.
        //
        // The stem is per ITEM, not per kind: retail ships a separate pin-pull for each smoke and flare
        // COLOUR, so the id is what picks the clip. Ids are the port's own throwable table (ThrowableDef).
        public static string ThrowableStem(ushort id) => id switch
        {
            254 => "grenade", 1242 => "grenade_makeshift",
            255 => "flare_blue", 256 => "flare_green", 257 => "flare_orange",
            258 => "flare_purple", 259 => "flare_red", 260 => "flare_yellow",
            261 => "smoke_black", 262 => "smoke_blue", 263 => "smoke_green", 264 => "smoke_orange",
            265 => "smoke_purple", 266 => "smoke_red", 267 => "smoke_white", 268 => "smoke_yellow",
            _ => null,
        };

        /// <summary>Pulling the pin / lighting it -- played as it leaves the hand.</summary>
        public static AudioStream ThrowableUse(ushort id)
            => ThrowableStem(id) is string st ? Clip("items", $"throwables_{st}_use") : null;

        /// <summary>A smoke canister venting, per colour. Retail ships one per smoke; the port used to play a
        /// pitched-down bullet casing here.</summary>
        public static AudioStream SmokeVent(ushort id)
            => ThrowableStem(id) is string st && st.StartsWith("smoke") ? Clip("items", $"throwables_{st}_smoke") : null;

        /// <summary>A thrown thing hitting something. One clip for every throwable, as retail has it -- the
        /// file is named for the grenade because that is the bundle it lives in, not because it is frag-only.</summary>
        public static AudioStream ThrowableBounce() => Clip("items", "throwables_grenade_bounce_use");

        /// <summary>Fuel moving between a can and a tank -- pouring in, siphoning out, filling at a pump.
        /// Retail ships one clip per CONTAINER (UseableFuel's own bundle), and the port has all five of the
        /// items they belong to, so this is a straight id map rather than a family guess:
        /// 28 Portable Gas Can, 1440 Industrial Gas Can, 1114/1115/1116 the Maple/Birch/Pine Jerrycans.</summary>
        public static AudioStream FuelPour(ushort itemId) => Clip("items", itemId switch
        {
            1440 => "fuels_gas_large_use",
            1114 => "fuels_jerrycan_maple_use",
            1115 => "fuels_jerrycan_birch_use",
            1116 => "fuels_jerrycan_pine_use",
            _ => "fuels_gas_use",          // 28, and any fuel container a later rip adds
        });

        // ---- PHYSICS IMPACTS (content/audio/impacts, 19 clips, also referenced nowhere) -------------------------
        // ✅ THE STATIC/DYNAMIC SPLIT IS CONFIRMED RETAIL (2026-09-10), and the guess it replaces was right.
        // It was flagged here as "my reading, not a confirmed rule" because no PhysicMaterialCustomData caller
        // names a static/dynamic key. The answer was never in the callers: `Concrete_Static` and
        // `Concrete_Dynamic` are two SEPARATE PHYSIC MATERIALS -- the bundle carries 21 of them, Concrete /
        // Metal / Wood / Gravel / Foliage / Tile / Cloth each in a _Static and a _Dynamic flavour -- and
        // GetAudioDef is keyed by the material NAME, so the suffix picks the clip without any caller ever
        // spelling it. Static is world geometry, dynamic is a thing that moves, which is exactly "what was
        // struck". Found while extracting per-prop surfaces (tools/extract_prop_surfaces.py).
        public static AudioStream Impact(PlayerController.Surf s) => Pick("impacts", (s switch
        {
            PlayerController.Surf.Metal => "metal",
            PlayerController.Surf.Wood => "wood",
            PlayerController.Surf.Water => "water",
            PlayerController.Surf.Grass => "foliage",
            PlayerController.Surf.Dirt or PlayerController.Surf.Sand or PlayerController.Surf.Gravel => "gravel",
            PlayerController.Surf.Snow => "snow",   // the one impacts bank that exists only as a _static
            _ => "concrete",
        }) + "_static");

        public static string BulletSurface(PlayerController.Surf s) => s switch
        {
            PlayerController.Surf.Metal => "metallight", PlayerController.Surf.Wood => "woodlight",
            PlayerController.Surf.Sand => "gravel",   // retail ships no sand bullet bank (audit 2026-09-03: a missing bank returns null and the old single wav takes over -- gravel is the retail choice)
            PlayerController.Surf.Rock => "rock",     // the one bank `rock` exists for -- there is no rock footstep or landing clip
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
        /// <summary>A one-shot over the loop (the map outro / death sting); the loop keeps going underneath.
        ///
        /// The player is HELD (it used to be a fire-and-forget local that only the Finished signal could ever reach), because
        /// something has to be able to cut it: respawning inside the sting's own length left the death music playing over a
        /// live player until it ran out (master 2026-09-06 "if you respawn too quickly, the death music keeps playing").
        /// Starting a second sting also stops the first now -- dying twice inside one outro used to stack them.</summary>
        public void Sting(string name)
        {
            var s = GameAudio.Clip("music", name); if (s == null) return;
            StopSting(0f);
            _sting = new AudioStreamPlayer { Stream = s, VolumeDb = Mathf.LinearToDb(Mathf.Clamp(Volume, 0.0001f, 1f)) };
            var p = _sting;
            AddChild(p); p.Play(); p.Finished += () => { if (_sting == p) _sting = null; if (GodotObject.IsInstanceValid(p)) p.QueueFree(); };
        }
        AudioStreamPlayer _sting; float _stingFadeT, _stingFadeLen;
        /// <summary>Cut the death/outro sting -- on respawn, because a death sting over a living player is a bug you can hear.
        /// A short fade rather than a hard Stop(): killing a sustained orchestral tail mid-sample clicks.</summary>
        public void StopSting(float fade = 0.25f)
        {
            if (_sting == null || !GodotObject.IsInstanceValid(_sting)) { _sting = null; return; }
            if (fade <= 0f) { var p0 = _sting; _sting = null; p0.Stop(); p0.QueueFree(); return; }
            _stingFadeT = 0f; _stingFadeLen = fade;
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
            if (_stingFadeLen > 0f && _sting != null && GodotObject.IsInstanceValid(_sting))
            {
                _stingFadeT += (float)delta;
                float k = Mathf.Clamp(1f - _stingFadeT / _stingFadeLen, 0f, 1f);
                _sting.VolumeDb = Mathf.LinearToDb(Mathf.Max(0.0001f, Volume * k));
                if (k <= 0f) { var p = _sting; _sting = null; _stingFadeLen = 0f; p.Stop(); p.QueueFree(); }
            }
            else if (_stingFadeLen > 0f) { _stingFadeLen = 0f; _sting = null; }
        }
    }
}
