using System.Collections.Generic;
using Godot;
using UnturnedSim;

namespace UnturnedGodot
{
    /// <summary>UG_TOWNCLOSE=cull: THE CULLING PROBE, for a generated town (strawberry 2026-10-16: "make sure actual culling of
    /// props happens ... and basements should get fully culled when not in view"). No effect unless asked for.
    ///
    /// It takes one building -- one with a hatch basement if the town has one (UG_TOWNBASEMENTS=1), nearest the middle -- and
    /// stands the camera in and round it: (a) on the street in front of it, (b) in its biggest ground-floor room, (c) on the
    /// ground 300 m from the town's middle (and (c2) 80 m over that spot), (d) past the top of its basement hatch's flight, (d2)
    /// over the well, (d3) on the well's landing looking in at the basement door, (e) in its basement, and either side of each
    /// basement-gate threshold (t_*: BuildingCull.PlanMargin in front of the door, HatchRadius over the well). UG_CULLSTOPS picks
    /// some. At each it reads what the renderer admitted into the frame -- objects, draw calls, primitives, split into the
    /// visible pass and the shadow pass -- under each culling CONFIG the build offers (occlusion, the basement gate, the
    /// far-furniture shadow gate, PropCull's distances: each switched off in turn, then all of them, in this one process, so
    /// every comparison is the same town and the same camera), and saves every frame, with two unchanged frames ("again",
    /// "after") as the noise floor a picture difference is read against. EXPERIMENTS (x-*) measure what the build does NOT
    /// do. At (a) and (b) it then times occlusion culling on and off, interleaved.
    ///
    /// ⚠ A COUNTER IS ONLY EVIDENCE ONCE IT HAS MOVED. Before anything else it hides the whole building at stop (a) and reads
    /// the counters every frame for six frames, then shows it again for six: a counter that reads the same either way is blind,
    /// and the series says how many frames late the counters run (this project renders on its own thread, so a value read in
    /// _Process belongs to a frame already drawn). Every later read waits past that lag and takes two consecutive frames.
    ///
    /// Pictures land beside the --shot path as cull_&lt;stop&gt;_&lt;config&gt;.png; the --shot path itself gets stop (a)'s.</summary>
    public partial class CullProbe : Node
    {
        /// <summary>A culling feature the probe can switch off and back on: On() must restore exactly what Off() took away.</summary>
        sealed class Config
        {
            public string Tag;
            public System.Action Off, On;
        }

        /// <summary>The culling features this build has, each switched off in turn at every stop (MakeConfigs).</summary>
        readonly List<Config> Configs = new();

        Camera3D _cam;
        TownRoadGrid _town;
        TownRoadGrid.TownBuilding _b;
        string _dir, _shot;
        IEnumerator<int> _script;
        int _wait, _frames;
        readonly List<(string Tag, Vector3 Eye, Vector3 Look)> _stops = new();
        const int MaxFrames = 3000;
        /// <summary>Frames to wait after a change before its counters are read (set from the teeth series, never less than 3).</summary>
        int _lag = 3;

        public static bool Begin(Node host, TownRoadGrid town, Camera3D cam, string shotPath)
        {
            if (host == null || town == null || cam == null) return false;
            var p = new CullProbe { Name = "CullProbe", _cam = cam, _town = town, _shot = shotPath };
            p._dir = string.IsNullOrEmpty(shotPath) ? "/tmp" : System.IO.Path.GetDirectoryName(shotPath);
            host.AddChild(p);
            return true;
        }

        public override void _Ready()
        {
            // UG_CULLRES=WxH (960x540 if unset): the probe's own window. Its counters do not depend on the pixel count; the
            // software rasteriser's time per frame does, and the probe draws hundreds of frames.
            var res = (System.Environment.GetEnvironmentVariable("UG_CULLRES") ?? "960x540").Split('x');
            if (res.Length == 2 && int.TryParse(res[0], out int rw) && int.TryParse(res[1], out int rh) && rw > 0 && rh > 0)
                GetWindow().Size = new Vector2I(rw, rh);
            _script = Run().GetEnumerator();
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        }

        public override void _Process(double delta)
        {
            if (++_frames > MaxFrames) { Log.Err($"[cullprobe] gave up after {MaxFrames} frames"); GetTree().Quit(1); return; }
            if (_wait > 0) { _wait--; return; }
            if (!_script.MoveNext()) { GetTree().Quit(); SetProcess(false); return; }
            _wait = Mathf.Max(0, _script.Current - 1);
        }

        // ---- reading the renderer -------------------------------------------------------------------------------

        public struct Reading
        {
            public long Objects, Draws, Prims;      // RenderingServer totals: every viewport, visible + shadow passes
            public int VisObj, VisDraw, ShadObj;    // the main viewport's visible pass, and its shadow pass
            public double CpuMs, SetupMs;           // the viewport's measured CPU render time, and the frame setup time
            public override string ToString() => $"objects {Objects} draws {Draws} prims {Prims} | visible {VisObj} obj / {VisDraw} draws, shadow {ShadObj} obj | cpu {CpuMs:0.00} ms setup {SetupMs:0.00} ms";
            public bool SameCounts(Reading o) => Objects == o.Objects && Draws == o.Draws && Prims == o.Prims;
        }

        Reading Read()
        {
            var vp = GetViewport();
            return new Reading
            {
                Objects = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame),
                Draws = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame),
                Prims = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame),
                VisObj = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.ObjectsInFrame),
                VisDraw = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.DrawCallsInFrame),
                ShadObj = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.ObjectsInFrame),
                CpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp.GetViewportRid()),
                SetupMs = RenderingServer.GetFrameSetupTimeCpu(),
            };
        }

        Reading _last;
        /// <summary>Wait out the lag, then read until two consecutive frames agree (at most four reads); _last holds it.</summary>
        IEnumerable<int> Settle(string what)
        {
            yield return _lag;
            var a = Read();
            for (int k = 0; k < 4; k++)
            {
                yield return 1;
                var b = Read();
                if (b.SameCounts(a)) { _last = b; yield break; }
                a = b;
            }
            Log.Print($"[cullprobe]   ({what}: counters still moving after 4 more frames -- last read kept)");
            _last = a;
        }

        void Save(string name)
        {
            var img = GetViewport().GetTexture()?.GetImage();
            if (img == null) { Log.Err("[cullprobe] no image -- needs a rendering driver"); return; }
            var path = System.IO.Path.Combine(_dir, name + ".png");
            var err = img.SavePng(path);
            Log.Print(err == Error.Ok ? $"[cullprobe]   saved {path}" : $"[cullprobe]   FAILED to save {path}: {err}");
        }

        // ---- the building and the stops --------------------------------------------------------------------------

        static float Storey => BuildingGen.Storey;

        Vector3 W(Vector3 local) => _b.Node.GlobalTransform * local;
        static float Ground(Vector3 at) => Terrain.Active != null ? Terrain.Active.SampleHeight(at.X, at.Z) : at.Y;

        TownRoadGrid.TownBuilding Pick()
        {
            TownRoadGrid.TownBuilding best = null;
            int bestRank = -1; float bestD = float.MaxValue;
            foreach (var b in _town.Buildings)
            {
                if (b.Result == null || b.Node == null || !GodotObject.IsInstanceValid(b.Node)) continue;
                int rank = b.Result.HasBasement ? (b.Result.BasementAccess == BasementAccess.Hatch ? 2 : 1) : 0;
                bool hasGround = false, hasBasementRoom = false;
                foreach (var g in b.Result.Layout) { if (g.Floor == 0) hasGround = true; if (g.Floor == -1) hasBasementRoom = true; }
                if (!hasGround || (rank > 0 && !hasBasementRoom)) rank = 0;
                float d = (b.Node.GlobalPosition - _town.Centre).Length();
                if (rank > bestRank || (rank == bestRank && d < bestD)) { best = b; bestRank = rank; bestD = d; }
            }
            return best;
        }

        /// <summary>The biggest room on a floor (not the stair room), and a standing spot at one end of its longest rectangle
        /// looking down it: (eye, look) in the lattice's frame, at `eyeY`.</summary>
        (Vector3 Eye, Vector3 Look)? RoomView(int floor, float eyeY)
        {
            var res = _b.Result;
            GenRoom best = null;
            foreach (var g in res.Layout)
                if (g.Floor == floor && g != res.BasementStairs && (best == null || g.Cells > best.Cells)) best = g;
            if (best == null) return null;
            (int X0, int Z0, int X1, int Z1) r = default; int area = -1;
            foreach (var q in best.Rects) { int a = (q.X1 - q.X0) * (q.Z1 - q.Z0); if (a > area) { area = a; r = q; } }
            float c = BuildingGen.Cell;
            var mid = new Vector3((r.X0 + r.X1) * 0.5f * c, eyeY, (r.Z0 + r.Z1) * 0.5f * c);
            bool alongX = r.X1 - r.X0 >= r.Z1 - r.Z0;
            float half = (alongX ? r.X1 - r.X0 : r.Z1 - r.Z0) * c * 0.5f;
            // look toward the building's middle along the long axis
            var centre = new Vector3(res.CellsX * c * 0.5f, eyeY, res.CellsZ * c * 0.5f);
            float toward = alongX ? centre.X - mid.X : centre.Z - mid.Z;
            float sgn = toward < 0f ? -1f : 1f;
            var dir = alongX ? new Vector3(sgn, 0f, 0f) : new Vector3(0f, 0f, sgn);
            var eye = mid - dir * Mathf.Max(0f, half - 0.8f);
            return (eye, eye + dir * 6f);
        }

        void PlanStops()
        {
            var res = _b.Result;
            float c = BuildingGen.Cell;
            // (a) the street: 14 m out from the front door, at eye height on the ground there, looking at the door's head
            Vector3 doorL = new(res.CellsX * c * 0.5f, 0f, 0f), outL = new(0f, 0f, -1f);
            foreach (var d in res.Doors)
            {
                if (d.Role != DoorRole.Front || d.Floor != 0) continue;
                var (mx, mz) = d.Middle;
                doorL = new Vector3(mx, 0f, mz);
                outL = d.AlongX ? new Vector3(0f, 0f, d.Line == 0 ? -1f : 1f) : new Vector3(d.Line == 0 ? -1f : 1f, 0f, 0f);
                break;
            }
            var aEye = W(doorL + outL * 14f); aEye.Y = Ground(aEye) + 1.7f;
            _stops.Add(("a_street", aEye, W(doorL + new Vector3(0f, 2.5f, 0f))));
            // (b) the biggest ground-floor room
            if (RoomView(0, 1.7f) is { } bv) _stops.Add(("b_room", W(bv.Eye), W(bv.Look)));
            // (c) on the ground 300 m out from the town's middle, and (c2) 80 m over that spot
            var outDir = new Vector3(Mathf.Sin(_town.Bearing + 0.6f), 0f, Mathf.Cos(_town.Bearing + 0.6f));
            var cEye = _town.Centre + outDir * 300f; cEye.Y = Ground(cEye) + 1.7f;
            var cLook = _town.Centre + new Vector3(0f, 6f, 0f);
            _stops.Add(("c_far", cEye, cLook));
            _stops.Add(("c2_far_high", cEye + new Vector3(0f, 80f, 0f), cLook));
            // (d) the hatch: standing 2.5 m past the top of its flight, looking down it to the landing and the basement door
            if (res.HatchWell.X1 > res.HatchWell.X0)
            {
                var (hx0, hz0, hx1, hz1) = res.HatchWell;
                foreach (var s in res.Stairs)
                {
                    if (s.Floor != -1 || !s.Concrete || s.X < hx0 - 0.5f || s.X > hx1 + 0.5f || s.Z < hz0 - 0.5f || s.Z > hz1 + 0.5f) continue;
                    var climb = new Vector3(s.Back.X, 0f, s.Back.Z);
                    var foot = new Vector3(s.X, 0f, s.Z) + new Vector3(s.Right.X, 0f, s.Right.Z) * (s.Width * 0.5f);
                    var top = foot + climb * s.Run;
                    var dEye = W(top + climb * 2.5f); dEye.Y = Ground(dEye) + 1.7f;
                    _stops.Add(("d_hatch", dEye, W(foot + new Vector3(0f, -Storey + 1.2f, 0f))));
                    break;
                }
            }
            // (e) the basement's biggest room
            if (RoomView(-1, -Storey + 1.7f) is { } ev) _stops.Add(("e_basement", W(ev.Eye), W(ev.Look)));
            // (d2) over the hatch's well, 7 m up, looking down its length; (d3) on its landing, looking in at the basement door --
            // the one view from outside that reaches the basement's rooms
            if (res.HatchWell.X1 > res.HatchWell.X0)
                foreach (var d in res.Doors)
                {
                    if (d.Floor != -1 || d.B != null) continue;
                    var (mx, mz) = d.Middle;
                    var (hx0, hz0, hx1, hz1) = res.HatchWell;
                    var wellMid = new Vector3((hx0 + hx1) * 0.5f, 0f, (hz0 + hz1) * 0.5f);
                    var toWell = new Vector3(wellMid.X - mx, 0f, wellMid.Z - mz);
                    var n = d.AlongX ? new Vector3(0f, 0f, Mathf.Sign(toWell.Z)) : new Vector3(Mathf.Sign(toWell.X), 0f, 0f);
                    var floorY = -Storey;
                    var door = new Vector3(mx, floorY + 1.2f, mz);
                    _stops.Add(("d2_well_above", W(new Vector3(wellMid.X, 7f, wellMid.Z) + n * 3f), W(new Vector3(mx, floorY, mz) + n * 0.8f)));
                    // a metre and a half out from the house wall's outer face, at eye height on the landing, looking through the door
                    _stops.Add(("d3_hatch_door", W(new Vector3(mx, floorY + 1.6f, mz) + n * (BuildingGen.Ext * 0.5f + 1.5f)), W(door - n * 4f)));
                    break;
                }
            // the plan gate's threshold: in front of the front door, half a metre past BuildingCull.PlanMargin from the basement's
            // outer face and half a metre inside it, at eye height, looking at the door -- windows either side of it
            if (res.HasBasement)
            {
                var pEye = W(doorL + outL * (BuildingGen.Ext * 0.5f + BuildingCull.PlanMargin + 0.5f)); pEye.Y = Ground(pEye) + 1.7f;
                var pIn = W(doorL + outL * (BuildingGen.Ext * 0.5f + BuildingCull.PlanMargin - 0.5f)); pIn.Y = Ground(pIn) + 1.7f;
                _stops.Add(("t_plan_out", pEye, W(doorL + new Vector3(0f, 1.2f, 0f))));
                _stops.Add(("t_plan_in", pIn, W(doorL + new Vector3(0f, 1.2f, 0f))));
            }
            // the hatch gate's threshold: half a metre past BuildingCull.HatchRadius from the door at the well's foot, and half a
            // metre inside it, at 65 degrees over the well -- steep enough to see the door past the retaining wall -- looking at
            // the door. Outside, a pop would show here if it shows anywhere.
            if (res.HatchWell.X1 > res.HatchWell.X0)
                foreach (var d in res.Doors)
                {
                    if (d.Floor != -1 || d.B != null) continue;
                    var (mx, mz) = d.Middle;
                    var door = new Vector3(mx, -Storey + 1.2f, mz);
                    var (hx0, hz0, hx1, hz1) = res.HatchWell;
                    var toWell = new Vector3((hx0 + hx1) * 0.5f - mx, 0f, (hz0 + hz1) * 0.5f - mz);
                    var n = d.AlongX ? new Vector3(0f, 0f, Mathf.Sign(toWell.Z)) : new Vector3(Mathf.Sign(toWell.X), 0f, 0f);
                    var up = (n * Mathf.Cos(Mathf.DegToRad(65f)) + Vector3.Up * Mathf.Sin(Mathf.DegToRad(65f))).Normalized();
                    _stops.Add(("t_hatch_out", W(door + up * (BuildingCull.HatchRadius + 0.5f)), W(door)));
                    _stops.Add(("t_hatch_in", W(door + up * (BuildingCull.HatchRadius - 0.5f)), W(door)));
                    break;
                }
        }

        void Aim(Vector3 eye, Vector3 look)
        {
            _cam.Current = true;
            _cam.Fov = 60f;
            _cam.GlobalPosition = eye;
            _cam.LookAt(look, Vector3.Up);
            BuildingCull.Refresh();   // the gates follow the jump before the next frame is drawn
        }

        /// <summary>What the town's buildings hold, for the record: drawable instances (and how many carry a distance cull),
        /// furniture pieces, lamps (and how many are drawn at all), occluders.</summary>
        void Census(string when)
        {
            int gi = 0, ranged = 0, furniture = 0, lamps = 0, lampsShown = 0, occ = 0, occTris = 0, hidden = 0;
            void Walk(Node n)
            {
                if (n is GeometryInstance3D g) { gi++; if (g.VisibilityRangeEnd > 0f) ranged++; if (!g.IsVisibleInTree()) hidden++; }
                if (n is Node3D n3 && n3.Name.ToString().StartsWith("Furniture_")) furniture++;
                if (n is LampLight l) { lamps++; if (l.IsVisibleInTree()) lampsShown++; }
                if (n is OccluderInstance3D o && o.Occluder is ArrayOccluder3D ao) { occ++; occTris += ao.Indices.Length / 3; }
                foreach (var ch in n.GetChildren()) Walk(ch);
            }
            int bn = 0;
            foreach (var b in _town.Buildings) if (b.Node != null && GodotObject.IsInstanceValid(b.Node)) { bn++; Walk(b.Node); }
            Log.Print($"[cullprobe] census ({when}): {bn} buildings -- {gi} drawable instances ({ranged} with a distance cull, {hidden} hidden), "
                    + $"{furniture} furniture pieces, {lamps} lamps ({lampsShown} shown), {occ} occluders ({occTris} triangles)");
        }

        /// <summary>Lamps whose light could reach the frame from here: shown, and within their fade-out distance of the camera
        /// (LampLight's DistanceFade -- past it the engine skips the light).</summary>
        int LiveLamps()
        {
            int n = 0;
            var at = _cam.GlobalPosition;
            float reach = BuildingRuntime.LampFadeBegin + BuildingRuntime.LampFadeLength;
            void Walk(Node x)
            {
                if (x is LampLight l && l.IsVisibleInTree() && l.GlobalPosition.DistanceTo(at) <= reach) n++;
                foreach (var ch in x.GetChildren()) Walk(ch);
            }
            foreach (var b in _town.Buildings) if (b.Node != null && GodotObject.IsInstanceValid(b.Node)) Walk(b.Node);
            return n;
        }

        /// <summary>The culling features to switch off one at a time: only those this build and this run actually have on.</summary>
        void MakeConfigs()
        {
            var vp = GetViewport();
            Log.Print($"[cullprobe] viewport occlusion culling: {(vp.UseOcclusionCulling ? "ON" : "off")}; gated basements: {BuildingCull.Count}; "
                    + $"buildings with gated furniture shadows: {BuildingCull.ShadowCount}");
            if (vp.UseOcclusionCulling)
                Configs.Add(new Config { Tag = "occlusion", Off = () => vp.UseOcclusionCulling = false, On = () => vp.UseOcclusionCulling = true });
            if (BuildingCull.Count > 0)
                Configs.Add(new Config
                {
                    Tag = "basementgate",
                    Off = () => { BuildingCull.ForceShow = true; BuildingCull.Refresh(); },
                    On = () => { BuildingCull.ForceShow = false; BuildingCull.Refresh(); },
                });
            if (BuildingCull.ShadowCount > 0)
                Configs.Add(new Config
                {
                    Tag = "shadowgate",
                    Off = () => { BuildingCull.ForceShadows = true; BuildingCull.Refresh(); },
                    On = () => { BuildingCull.ForceShadows = false; BuildingCull.Refresh(); },
                });
            // every range PropCull stamped, anywhere in the scene (the town's street props come through the same placer)
            int stamped = 0;
            void Walk(Node n, System.Action<Node> f) { f(n); foreach (var c in n.GetChildren()) Walk(c, f); }
            Walk(GetTree().Root, n => { if (n is GeometryInstance3D g && g.HasMeta(PropCull.StampMeta)) stamped++; });
            Log.Print($"[cullprobe] drawables carrying PropCull's distance: {stamped}; props given one from the LOD table {PropCull.FromTable}, "
                    + $"by their size {PropCull.BySize}");
            if (stamped > 0)
            {
                var lifted = new List<(GeometryInstance3D G, float R)>();
                Configs.Add(new Config
                {
                    Tag = "propcull",
                    Off = () =>
                    {
                        lifted.Clear();
                        Walk(GetTree().Root, n =>
                        {
                            if (n is GeometryInstance3D g && g.HasMeta(PropCull.StampMeta) && g.VisibilityRangeEnd > 0f)
                            { lifted.Add((g, g.VisibilityRangeEnd)); g.VisibilityRangeEnd = 0f; }
                        });
                    },
                    On = () => { foreach (var (g, r) in lifted) if (GodotObject.IsInstanceValid(g)) g.VisibilityRangeEnd = r; },
                });
            }
        }

        /// <summary>How far the camera is from a building's plan rectangle, in plan.</summary>
        float PlanDistance(TownRoadGrid.TownBuilding b)
        {
            var l = b.Node.GlobalTransform.AffineInverse() * _cam.GlobalPosition;
            float wx = b.Result.CellsX * BuildingGen.Cell, dz = b.Result.CellsZ * BuildingGen.Cell;
            float dx = Mathf.Max(0f, Mathf.Max(-l.X, l.X - wx)), dzz = Mathf.Max(0f, Mathf.Max(-l.Z, l.Z - dz));
            return Mathf.Sqrt(dx * dx + dzz * dzz);
        }

        /// <summary>EXPERIMENTS, not features: something the build does NOT do, switched ON at a stop to measure what it would
        /// save. Read as "x-&lt;tag&gt;" rows; nothing here ships.</summary>
        readonly List<Config> Experiments = new();

        void MakeExperiments()
        {
            // what the town's FURNITURE costs the shadow passes: every drawable under a building but its surfaces stops casting.
            // (The sun's cascades reach GraphicsOptions.ShadowDistance and draw everything in it, walls or no walls -- occlusion
            // culling is for the camera's pass only.)
            var cast = new List<(GeometryInstance3D G, GeometryInstance3D.ShadowCastingSetting S)>();
            void Walk(Node n, bool inSurface)
            {
                inSurface |= n is WallSurface;
                if (!inSurface && n is GeometryInstance3D g && g.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) cast.Add((g, g.CastShadow));
                foreach (var c in n.GetChildren()) Walk(c, inSurface);
            }
            Experiments.Add(new Config
            {
                Tag = "furniture-casts-no-shadow",
                On = () =>
                {
                    cast.Clear();
                    foreach (var b in _town.Buildings) if (b.Node != null && GodotObject.IsInstanceValid(b.Node)) Walk(b.Node, false);
                    foreach (var (g, _) in cast) g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                },
                Off = () => { foreach (var (g, sc) in cast) if (GodotObject.IsInstanceValid(g)) g.CastShadow = sc; },
            });
            // what an INTERIOR gate would save: every building whose plan is more than 50 m from the camera has its furniture
            // (everything under its root but its surfaces and their gated basement) hidden
            var hiddenFurniture = new List<Node3D>();
            Experiments.Add(new Config
            {
                Tag = "interiors-hidden-past-50m",
                On = () =>
                {
                    hiddenFurniture.Clear();
                    foreach (var b in _town.Buildings)
                    {
                        if (b.Node == null || !GodotObject.IsInstanceValid(b.Node) || b.Result == null || PlanDistance(b) <= 50f) continue;
                        foreach (var c in b.Node.GetChildren())
                            if (c is Node3D n && n is not WallSurface && n.Name != "Basement" && n.Visible) { n.Visible = false; hiddenFurniture.Add(n); }
                    }
                },
                Off = () => { foreach (var n in hiddenFurniture) if (GodotObject.IsInstanceValid(n)) n.Visible = true; },
            });
        }

        /// <summary>Every door hung at the building's basement level stands open (ObjectDoor.SetInitialState, as UG_EDITDOORSOPEN
        /// opens a stage building's).</summary>
        void OpenBasementDoors()
        {
            int n = 0;
            float below = _b.Node.GlobalPosition.Y - 2f;
            void Walk(Node x)
            {
                if (x is ObjectDoor d && d.GlobalPosition.Y < below) { d.SetInitialState(true); n++; }
                foreach (var c in x.GetChildren()) Walk(c);
            }
            Walk(_b.Node);
            Log.Print($"[cullprobe] opened {n} basement door(s)");
        }

        /// <summary>The lights holding a shadow right now (LightShadowBudget's choice), and how many of them hang in a basement.</summary>
        string ShadowedLamps()
        {
            int n = 0, below = 0;
            void Walk(Node x, bool inBasement)
            {
                inBasement |= x.Name == "Basement";
                if (x is Light3D l && l.ShadowEnabled && l.IsVisibleInTree()) { n++; if (inBasement) below++; }
                foreach (var c in x.GetChildren()) Walk(c, inBasement);
            }
            foreach (var b in _town.Buildings) if (b.Node != null && GodotObject.IsInstanceValid(b.Node)) Walk(b.Node, false);
            return $"shadowed lamps {n} ({below} in basements)";
        }

        // ---- the run -------------------------------------------------------------------------------------------

        IEnumerable<int> Run()
        {
            yield return 10;   // the town's last nodes into the tree, a few frames drawn
            // the editor's panels off the pictures (2D: not in any 3D count)
            void HideUi(Node n)
            {
                if (n is CanvasLayer cl) { cl.Visible = false; return; }
                if (n is Control ct) { ct.Visible = false; return; }   // a top-level control: everything under it goes with it
                foreach (var c in n.GetChildren()) HideUi(c);
            }
            HideUi(GetTree().Root);
            // the sun held still: a shadow edge creeping across the frame between two captures reads as a difference between them
            if (DayNightCycle.Current != null) DayNightCycle.Current.Speed = 0f;
            _b = Pick();
            if (_b == null) { Log.Err("[cullprobe] no built building in the town"); yield break; }
            MakeConfigs();
            MakeExperiments();
            var res = _b.Result;
            Log.Print($"[cullprobe] building {_b.Node.Name} at {_b.Node.GlobalPosition}: {res.Kind}, {res.CellsX}x{res.CellsZ} cells, {res.Floors} floor(s), "
                    + $"basement {(res.HasBasement ? $"{res.Basement} by {res.BasementAccess}" : "none")}; configs: {string.Join(", ", Configs.ConvertAll(c => c.Tag))}");
            Census("as built");
            PlanStops();
            foreach (var s in _stops) Log.Print($"[cullprobe] stop {s.Tag}: eye {s.Eye} looking at {s.Look}");
            // UG_CULLSTOPS=a_street,d_hatch,...: only those stops (the teeth check always runs at the first one planned)
            var only = System.Environment.GetEnvironmentVariable("UG_CULLSTOPS");
            var first = _stops[0];
            if (!string.IsNullOrEmpty(only))
            {
                var keep = new HashSet<string>(only.Split(','));
                _stops.RemoveAll(st => !keep.Contains(st.Tag));
            }

            // ⚠ TEETH FIRST: hide the whole building at stop (a); every counter must move, and the series says how late they run
            var st0 = first;
            Aim(st0.Eye, st0.Look);
            yield return 8;
            var shown = Read();
            _b.Node.Visible = false;
            int lagSeen = -1;
            for (int k = 1; k <= 6; k++)
            {
                yield return 1;
                var r = Read();
                Log.Print($"[cullprobe] teeth: building HIDDEN, frame +{k}: {r}");
                if (lagSeen < 0 && r.Objects != shown.Objects) lagSeen = k;
            }
            var hiddenR = Read();
            _b.Node.Visible = true;
            for (int k = 1; k <= 6; k++)
            {
                yield return 1;
                Log.Print($"[cullprobe] teeth: building SHOWN, frame +{k}: {Read()}");
            }
            if (lagSeen < 0)
                Log.Err($"[cullprobe] teeth: the counters did NOT move when the building was hidden ({shown}) -- they are blind here");
            else
            {
                _lag = Mathf.Max(3, lagSeen + 1);
                Log.Print($"[cullprobe] teeth: hiding the building moved objects {shown.Objects} -> {hiddenR.Objects}, draws {shown.Draws} -> {hiddenR.Draws}, "
                        + $"prims {shown.Prims} -> {hiddenR.Prims}; first moved {lagSeen} frame(s) after the change, reads wait {_lag}");
            }

            // each stop, under each config
            foreach (var (tag, eye, look) in _stops)
            {
                // (d3) is a look in at the basement: its doors stand open for it
                if (tag == "d3_hatch_door") OpenBasementDoors();
                Aim(eye, look);
                yield return 6;
                foreach (var e in Settle(tag)) yield return e;
                var all = _last;
                Log.Print($"[cullprobe] {tag} all: {all} | live lamps {LiveLamps()} | {ShadowedLamps()} | basement {(BuildingCull.IsShown(_b.Node) ? "DRAWN" : "hidden")} "
                        + $"({BuildingCull.ShownCount}/{BuildingCull.Count} basements drawn, {BuildingCull.CastingCount}/{BuildingCull.ShadowCount} buildings' furniture casting)");
                Save($"cull_{tag}_all");
                // the same frame again, nothing changed: the noise floor any picture-to-picture difference is read against (the
                // sky, the water, a lamp's flicker all move between two frames on their own)
                foreach (var e in Settle($"{tag} again")) yield return e;
                Log.Print($"[cullprobe] {tag} all again: {_last}");
                Save($"cull_{tag}_again");
                if (tag == "a_street" && !string.IsNullOrEmpty(_shot))
                {
                    var img = GetViewport().GetTexture()?.GetImage();
                    img?.SavePng(_shot);
                }
                // (a threshold stop is only about the basement gate: the frame with it, and without it)
                bool threshold = tag.StartsWith("t_");
                foreach (var cfg in Configs)
                {
                    if (threshold && cfg.Tag != "basementgate") continue;
                    cfg.Off();
                    foreach (var e in Settle($"{tag} no-{cfg.Tag}")) yield return e;
                    var r = _last;
                    Log.Print($"[cullprobe] {tag} no-{cfg.Tag}: {r} | live lamps {LiveLamps()} | vs all: objects {r.Objects - all.Objects:+0;-0;0} draws {r.Draws - all.Draws:+0;-0;0} prims {r.Prims - all.Prims:+0;-0;0}"
                            + $" | visible {r.VisObj - all.VisObj:+0;-0;0} shadow {r.ShadObj - all.ShadObj:+0;-0;0} | {ShadowedLamps()}");
                    Save($"cull_{tag}_no-{cfg.Tag}");
                    cfg.On();
                }
                foreach (var x in Experiments)
                {
                    if (tag != "a_street" && tag != "b_room") continue;   // street-level questions
                    x.On();
                    foreach (var e in Settle($"{tag} x-{x.Tag}")) yield return e;
                    var r = _last;
                    Log.Print($"[cullprobe] {tag} x-{x.Tag}: {r} | vs all: objects {r.Objects - all.Objects:+0;-0;0} draws {r.Draws - all.Draws:+0;-0;0} prims {r.Prims - all.Prims:+0;-0;0}"
                            + $" | visible {r.VisObj - all.VisObj:+0;-0;0} shadow {r.ShadObj - all.ShadObj:+0;-0;0} | {ShadowedLamps()}");
                    Save($"cull_{tag}_x-{x.Tag}");
                    x.Off();
                }
                // and everything back on at the end: what the frame drifted by over the whole stop on its own
                foreach (var e in Settle($"{tag} after")) yield return e;
                Log.Print($"[cullprobe] {tag} all after: {_last}");
                Save($"cull_{tag}_after");
                if (Configs.Count > 1 && !threshold)
                {
                    foreach (var cfg in Configs) cfg.Off();
                    foreach (var e in Settle($"{tag} none")) yield return e;
                    var r = _last;
                    Log.Print($"[cullprobe] {tag} none: {r} | live lamps {LiveLamps()} | vs all: objects {r.Objects - all.Objects:+0;-0;0} draws {r.Draws - all.Draws:+0;-0;0} prims {r.Prims - all.Prims:+0;-0;0}"
                            + $" | visible {r.VisObj - all.VisObj:+0;-0;0} shadow {r.ShadObj - all.ShadObj:+0;-0;0}");
                    Save($"cull_{tag}_none");
                    foreach (var cfg in Configs) cfg.On();
                }
            }
            // what occlusion culling COSTS: its rays are CPU work on every frame. Interleaved on/off at two stops so drift on
            // this shared box falls on both sides; the viewport's measured CPU render time includes the occlusion buffer's
            // update (its worker threads are waited on inside it), but also the cull and the draw recording it saves.
            var occ = Configs.Find(c => c.Tag == "occlusion");
            if (occ != null && System.Environment.GetEnvironmentVariable("UG_CULLTIMING") != "0")
                foreach (var (tag, eye, look) in _stops)
                {
                    if (tag != "a_street" && tag != "b_room") continue;
                    Aim(eye, look);
                    yield return 6;
                    var on = new List<double>(); var off = new List<double>();
                    for (int round = 0; round < 3; round++)
                        foreach (bool want in new[] { true, false })
                        {
                            if (want) occ.On(); else occ.Off();
                            yield return _lag;
                            for (int k = 0; k < 5; k++) { yield return 1; (want ? on : off).Add(Read().CpuMs); }
                        }
                    occ.On();
                    on.Sort(); off.Sort();
                    double Med(List<double> l) => l.Count == 0 ? 0 : l[l.Count / 2];
                    double Mean(List<double> l) { double t = 0; foreach (var v in l) t += v; return l.Count == 0 ? 0 : t / l.Count; }
                    Log.Print($"[cullprobe] occlusion cost at {tag}: viewport CPU render time median {Med(on):0.00} ms ON vs {Med(off):0.00} ms off "
                            + $"(means {Mean(on):0.00} / {Mean(off):0.00}, {on.Count} frames each, interleaved)");
                }
            Log.Print("[cullprobe] done");
        }
    }
}
