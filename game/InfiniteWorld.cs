using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>
    /// --infinite[=SEED] / Play -> Infinite World: an endless procedurally generated world (strawberry 2026-10-09).
    /// The pieces: core InfiniteTerrain (the generator), RegionStreamer (regions, LOD, colliders, trees, floating
    /// origin), and this -- the same lighting, player and shell as Drive PEI, minus everything that assumes a fixed
    /// map (the M map, zombies off PEI's spawn tables, the shore bake).
    ///
    /// Render/test knobs: UG_INF_TIME=0.5 sets the time of day (0..1); UG_INF_AT=x,z[,yaw] spawns at an ABSOLUTE position (e.g. 1000000,-3000000) instead of near
    /// the origin; UG_INF_CAM=x,y,z,yaw,pitch adds a fixed camera at an absolute position and streams around IT
    /// (an aerial overview for a --shot). UG_INF_NOWATER=1 / UG_INF_NOWARM=1 leave out the sea / the shader warm pass
    /// (the warm pass is what kept a movie-mode render from EXITING after its shot on lavapipe -- bisected 2026-10-09:
    /// 105 s hung with it, 6 s clean exit without; see the commit for the control runs).
    /// </summary>
    public static class InfiniteWorld
    {
        public const int DefaultSeed = 1337;

        public static WorldBuildResult Build(Node root, int seed)
        {
            var result = new WorldBuildResult();
            var sim = new SimDriver();
            root.AddChild(sim);
            result.Sim = sim;

            // the statics every water/swim/terrain reader consults survive from whatever world loaded before: set them
            Terrain.Active = null;
            Terrain.MapDir = "terrain";
            // grey rock and dirt from Yukon, PEI's wheat and sand: a temperate world, not PEI's red-soil one
            Terrain.RegionLayerDirs = new[] { "terrain_yukon", "terrain", "terrain", "terrain_yukon", "terrain", "terrain", "terrain_yukon", "terrain_yukon" };
            Terrain.HasWater = true;
            Terrain.SeaLevelY = InfiniteTerrain.SeaLevel;

            var env = new Godot.Environment { AmbientLightSource = Godot.Environment.AmbientSource.Color };
            { var we = new WorldEnvironment { Environment = env }; we.AddToGroup("world_env"); GraphicsOptions.ApplyEnvironment(env); root.AddChild(we); }
            var sun = new DirectionalLight3D { LightEnergy = 1.2f, ShadowEnabled = !WorldBuilder.SkipPhase("Shadows") };
            sun.AddToGroup("sun"); sun.DirectionalShadowMaxDistance = GraphicsOptions.ShadowDistance;
            root.AddChild(sun);
            var dayNight = new DayNightCycle { Sun = sun, Env = env, DayLength = DayNightCycle.DefaultDayLength };
            root.AddChild(dayNight);
            result.DayNight = dayNight;
            if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_INF_TIME"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tod))
                dayNight.Time = tod;   // render knob: 0.5 = noon (the default morning haze hides the distance a shot is for)

            var gen = new InfiniteTerrain(seed);
            var streamer = new RegionStreamer { Name = "RegionStreamer", Gen = gen, WorldRoot = root };

            // where to start, in ABSOLUTE metres
            double ax = 0, az = 0; float yaw = 0f;
            if (ParseCsv("UG_INF_AT") is double[] at && at.Length >= 2) { ax = at[0]; az = at[1]; if (at.Length >= 3) yaw = (float)at[2]; }
            gen.FindSpawn(ax, az, out double sx, out double sz, out float sy);
            var startRegion = RegionCoord.Containing(sx, sz);
            streamer.OriginX = startRegion.MinX; streamer.OriginZ = startRegion.MinZ;
            root.AddChild(streamer);

            CharacterModel.LoadBundled();
            var player = new PlayerController();
            root.AddChild(player);
            player.LinkWorldLighting(sun, env);
            streamer.Player = player;
            streamer.Focus = player;
            streamer.SyncGround(startRegion);   // the ground under the spawn exists BEFORE the player can fall
            player.GlobalPosition = streamer.ToLocal(sx, sy + 1.5, sz);
            player.RotationDegrees = new Vector3(0f, yaw, 0f);
            player.Spawn = player.GlobalPosition;
            result.Player = player;
            if (System.Environment.GetEnvironmentVariable("UG_INF_NOWATER") != "1") streamer.AddWater();

            // a jeep beside you: the quickest way to watch regions arrive (and to cross a rebase at speed)
            var jeep = Vehicle.BuildByName("jeep");
            root.AddChild(jeep);
            jeep.GlobalPosition = streamer.ToLocal(sx + 5.0, gen.HeightAt(sx + 5.0, sz + 5.0) + 1.5, sz + 5.0);   // beside and behind, not parked across the first view

            if (ParseCsv("UG_INF_CAM") is double[] cam && cam.Length >= 3)
            {
                var c = new Camera3D { Name = "InfiniteShotCam", Current = true, Far = 6000f, Fov = 70f };
                root.AddChild(c);
                c.GlobalPosition = streamer.ToLocal(cam[0], cam[1], cam[2]);
                c.RotationDegrees = new Vector3(cam.Length >= 5 ? (float)cam[4] : -20f, cam.Length >= 4 ? (float)cam[3] : 0f, 0f);
                streamer.Focus = c;   // stream around what the camera sees, not the player behind it
                streamer.SyncGround(RegionCoord.Containing(cam[0], cam[2]));
            }

            // the shell, minus MapUI: the M map is PEI's chart and would draw you on an island you are not on
            root.AddChild(new DevConsole { Player = player });
            root.AddChild(new BugReporter());
            { var hud = new HUD { Player = player }; root.AddChild(hud); player.Hud = hud; }
            root.AddChild(new FpsCounter());
            { var hmL = new CanvasLayer { Layer = 98 }; hmL.AddChild(new HitmarkerHUD()); root.AddChild(hmL); }
            { var pause = new PauseMenu(); root.AddChild(pause); player.PauseMenu = pause; }
            { var attach = new AttachmentMenu(); root.AddChild(attach); player.AttachMenu = attach; }
            { var ammo = new AmmoRadial(); root.AddChild(ammo); player.AmmoRadial = ammo; }
            { var l = new CanvasLayer { Layer = 90 }; l.AddChild(new InfiniteOverlay { Streamer = streamer }); root.AddChild(l); }

            Log.Print($"[infinite] seed {seed}: spawn ({sx:0}, {sz:0}) ground {sy:0.0} m, region {startRegion}, origin ({streamer.OriginX:0}, {streamer.OriginZ:0})");
            if (System.Environment.GetEnvironmentVariable("UG_INF_NOWARM") != "1") ShaderWarm.Begin(root);
            result.Ready = true;
            return result;
        }

        static double[] ParseCsv(string env)
        {
            var s = System.Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split(',');
            var v = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[i])) return null;
            return v;
        }
    }

    /// <summary>Bottom-left readout: where you are in the infinite world and what the streamer is doing. The
    /// absolute position is the one that grows without bound; the local one is what the engine sees, and staying
    /// small is the whole point of the floating origin.</summary>
    public partial class InfiniteOverlay : Label
    {
        public RegionStreamer Streamer;
        double _t;

        public override void _Ready()
        {
            AddThemeFontSizeOverride("font_size", 14);
            AddThemeColorOverride("font_color", new Color(0.92f, 0.96f, 0.86f));
            AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.8f));
            AddThemeConstantOverride("shadow_offset_x", 1); AddThemeConstantOverride("shadow_offset_y", 1);
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Process(double delta)
        {
            if ((_t -= delta) > 0) return;
            _t = 0.25;
            var s = Streamer;
            if (s == null || !IsInstanceValid(s) || s.Focus == null || !IsInstanceValid(s.Focus)) { Text = ""; return; }
            var p = s.Focus.GlobalPosition;
            var rc = s.FocusRegion();
            var l = s.LoadedByLod;
            Text = $"INFINITE  seed {s.Gen.Seed}   abs {s.AbsX(p.X):N0}, {s.AbsZ(p.Z):N0} m   y {p.Y:0}   region {rc}\n" +
                   $"local {p.X:0}, {p.Z:0}   origin {s.OriginX:N0}, {s.OriginZ:N0}   rebases {s.Rebases}   rescues {s.Rescues}\n" +
                   $"regions L0 {l[0]} · L1 {l[1]} · L2 {l[2]} · L3 {l[3]}   queued {s.Queued}+{s.InFlight}   colliders {s.Colliders}   trees {s.TreeCount:N0}   foliage {s.FoliageCount:N0}   gen {(s.GenCount > 0 ? s.GenMsTotal / s.GenCount : 0):0.0} ms";
            // top-left: the vitals bars own the bottom-left corner and the FPS counter the top-right
            Position = new Vector2(12f, 10f);
        }
    }
}
