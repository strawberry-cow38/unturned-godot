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
            float.TryParse(System.Environment.GetEnvironmentVariable("UG_INF_DRIVE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float driveSecs);
            if (driveSecs > 0f || System.Environment.GetEnvironmentVariable("UG_INF_SHIFTAT") != null)
            {
                HoldShot = true;
                float.TryParse(System.Environment.GetEnvironmentVariable("UG_INF_SHIFTAT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float shiftAt);
                root.AddChild(new InfiniteDriveProbe { S = streamer, P = player, Jeep = jeep, Secs = driveSecs, ShiftAt = shiftAt });
            }
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

            // WEATHER (strawberry 2026-10-10: "work on getting the weather engine in the inf world mode"): the same
            // WeatherManager as PEI -- the scheduled WeatherSim, 3D rain, wetness and puddles, thunder, the storm sky, wind.
            // What it needed from this world: RainRoofMap's tile cache re-keyed on every rebase (RegionStreamer.ShiftWorld)
            // and evicted far behind you, and PuddleField reading the world through WorldOrigin like the shaders do.
            if (WeatherManager.Current == null)
            {
                var wm = WeatherManager.Attach(root, null, dayNight);
                wm.ApplyEnvOverride();
            }

            Log.Print($"[infinite] seed {seed}: spawn ({sx:0}, {sz:0}) ground {sy:0.0} m, region {startRegion}, origin ({streamer.OriginX:0}, {streamer.OriginZ:0})");
            if (System.Environment.GetEnvironmentVariable("UG_INF_NOWARM") != "1") ShaderWarm.Begin(root);
            result.Ready = true;
            return result;
        }

        /// <summary>A render probe is still running: Main's --shot waits for this to clear.</summary>
        public static bool HoldShot;

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

    /// <summary>UG_INF_DRIVE=SECS (render probe): once the world has streamed in, put the jeep on the nearest main road
    /// heading along it, get in, and drive flat out for SECS while logging every frame -- the way to see what a rebase
    /// looks and sounds like at speed (pair with UG_INF_REBASE to make one happen within a region's drive).</summary>
    public partial class InfiniteDriveProbe : Node
    {
        public RegionStreamer S; public PlayerController P; public Vehicle Jeep; public float Secs;
        /// <summary>UG_INF_SHIFTAT: force a one-region rebase this far into the drive, so the frame it happens on is known.</summary>
        public float ShiftAt;
        int _phase, _wait; double _t; ulong _lastUs; bool _shifted;

        public override void _PhysicsProcess(double delta)
        {
            if (S == null || P == null || Jeep == null) return;
            switch (_phase)
            {
                case 0:
                    if (!S.Settled) return;
                    if (Secs <= 0f) { _phase = 3; _lastUs = Time.GetTicksUsec(); return; }   // shift only: no drive, the camera stays put
                    PlaceOnRoad();
                    _phase = 1; _wait = 20; return;
                case 1:
                    if (--_wait > 0) return;
                    P.EnterNearestVehicle();
                    _phase = 2; _wait = 10; return;
                case 2:
                    if (--_wait > 0) return;
                    Log.Print($"[drive] in the jeep: {P.Driving != null}");
                    _phase = 3; _lastUs = Time.GetTicksUsec(); return;
                case 3:
                    if (Secs > 0f) P.ScriptedDrive = new Vector2(0f, 1f);
                    _t += delta;
                    if (ShiftAt > 0f && !_shifted && _t >= ShiftAt) { _shifted = true; S.DebugShift(1, 0); }
                    ulong now = Time.GetTicksUsec();
                    var lp = Jeep.GlobalPosition;
                    Log.Print($"[drive] f{Engine.GetFramesDrawn()} t {_t:0.000} local ({lp.X:0.0}, {lp.Z:0.0}) abs ({S.AbsX(lp.X):0.0}, {S.AbsZ(lp.Z):0.0}) v {Jeep.LinearVelocity.Length():0.0} m/s rebases {S.Rebases} wall {(now - _lastUs) / 1000.0:0} ms");
                    _lastUs = now;
                    if (_t >= System.Math.Max(Secs, ShiftAt + 0.5f)) { P.ScriptedDrive = null; InfiniteWorld.HoldShot = false; _phase = 4; }
                    return;
            }
        }

        void PlaceOnRoad()
        {
            var lp = Jeep.GlobalPosition;
            double ax = S.AbsX(lp.X), az = S.AbsZ(lp.Z), best = double.MaxValue;
            (double x, double z, float h) bp = default; double hx = 0, hz = 1;
            long ci = (long)System.Math.Floor(ax / InfiniteRoads.MainCell), cj = (long)System.Math.Floor(az / InfiniteRoads.MainCell);
            for (long i = ci - 1; i <= ci + 1; i++)
                for (long j = cj - 1; j <= cj + 1; j++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        var line = S.Gen.Roads.MainCentreline(i, j, dir);
                        if (line == null) continue;
                        for (int k = 0; k + 1 < line.Length; k++)
                        {
                            double d = (line[k].x - ax) * (line[k].x - ax) + (line[k].z - az) * (line[k].z - az);
                            if (d < best) { best = d; bp = line[k]; hx = line[k + 1].x - line[k].x; hz = line[k + 1].z - line[k].z; }
                        }
                    }
            if (best == double.MaxValue) { Log.Print("[drive] no main road nearby; driving from where the jeep is"); return; }
            var pos = S.ToLocal(bp.x, bp.h + 1.2, bp.z);
            var fwd = new Vector3((float)hx, 0f, (float)hz).Normalized();
            Jeep.GlobalTransform = new Transform3D(Basis.LookingAt(fwd, Vector3.Up), pos);
            Jeep.LinearVelocity = Vector3.Zero; Jeep.AngularVelocity = Vector3.Zero;
            Jeep.ResetPhysicsInterpolation();
            P.TeleportTo(pos + new Vector3(fwd.Z, 0f, -fwd.X) * 3f + Vector3.Up * 0.5f);
            Log.Print($"[drive] jeep on the main road at ({bp.x:0}, {bp.z:0}), {System.Math.Sqrt(best):0} m from where it was");
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
                   $"regions L0 {l[0]} · L1 {l[1]} · L2 {l[2]} · L3 {l[3]}   queued {s.Queued}+{s.InFlight}   colliders {s.Colliders}   trees {s.TreeCount:N0} + {s.ImpostorCount:N0} far   foliage {s.FoliageCount:N0}   gen {(s.GenCount > 0 ? s.GenMsTotal / s.GenCount : 0):0.0} ms";
            // top-left: the vitals bars own the bottom-left corner and the FPS counter the top-right
            Position = new Vector2(12f, 10f);
        }
    }
}
