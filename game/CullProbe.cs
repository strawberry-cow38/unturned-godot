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
    /// Pictures land beside the --shot path as cull_&lt;stop&gt;_&lt;config&gt;.png; the --shot path itself gets the first stop's.</summary>
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
        bool _shotSaved;
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
            public int VisPrims, ShadDraw, ShadPrims;
            public double CpuMs, SetupMs;           // the viewport's measured CPU render time, and the frame setup time
            public double ProcMs;                   // Performance.TimeProcess: every _Process of the last frame (the gates' own cost is in it)
            public override string ToString() => $"objects {Objects} draws {Draws} prims {Prims} | visible {VisObj} obj / {VisDraw} draws / {VisPrims} prims, "
                                               + $"shadow {ShadObj} obj / {ShadDraw} draws / {ShadPrims} prims | cpu {CpuMs:0.00} ms setup {SetupMs:0.00} ms process {ProcMs:0.00} ms";
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
                VisPrims = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.PrimitivesInFrame),
                ShadObj = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.ObjectsInFrame),
                ShadDraw = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.DrawCallsInFrame),
                ShadPrims = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.PrimitivesInFrame),
                CpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp.GetViewportRid()),
                SetupMs = RenderingServer.GetFrameSetupTimeCpu(),
                ProcMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0,
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
            if (_gen != null) return _gen;
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

        // ---- UG_CULLGEN: one generated building, built through the town's own path -----------------------------------
        //
        // A tower is what costs the frame now (strawberry 2026-10-20: "big buildings like this dont KILL our frames"), and the
        // town does not put one up yet (cow tools is wiring them onto downtown plots). So the probe builds ONE building from a
        // UG_EDITGEN-style spec through BuildingRuntime.Build -- the town's builder, with every gate the town's buildings get
        // (the lamp fade, the basement gate, the furniture shadow gate, PropCull, the occluders) -- just outside the town it was
        // given, its front toward the town's middle, and takes it as THE building. The town stays round it: its buildings, its
        // streets and its props are in every frame and every shadow pass they would be in, so what the counters read is a tower
        // standing at the edge of a town, not a tower alone on a plain.

        TownRoadGrid.TownBuilding _gen;
        BuildingRuntime.RoomGroups _genRooms;
        /// <summary>The generated building's Build + Furnish time, ms (BuildingGen.Generate's apart).</summary>
        double _genMs, _buildMs;

        /// <summary>UG_CULLGEN's spec: UG_EDITGEN's fields kind,seed,minRooms,maxFloors,perFloor,width,depth (0-6), the plan
        /// field (18: a tower's MainStreet/Setback/Slab or MS/SB/IS) and wealth (19), read as Main.cs reads them. The fields
        /// between (sides, shape, porch, chimney, fit, shop, basement) are not read here: say so rather than drop them.</summary>
        static BuildingGenSpec GenSpec(string gen)
        {
            var g = gen.Split(',');
            int Arg(int i, int dflt) => g.Length > i && int.TryParse(g[i], out var v) ? v : dflt;
            for (int i = 7; i < 18 && i < g.Length; i++)
                if (!string.IsNullOrWhiteSpace(g[i])) Log.Print($"[cullprobe] UG_CULLGEN field {i} ('{g[i]}') is not read by the probe");
            string plan = g.Length > 18 ? g[18].ToUpperInvariant() : "";
            return new BuildingGenSpec
            {
                Kind = RoomDesignations.ParseBuilding(g[0]), Seed = Arg(1, 0), MinRooms = Arg(2, 0),
                MaxFloors = Arg(3, 2), MaxRoomsPerFloor = Arg(4, 6), Width = Arg(5, 0), Depth = Arg(6, 0),
                Tower = plan switch
                {
                    "MS" or "MAIN" => TowerStyle.MainStreet, "SB" => TowerStyle.Setback, "IS" or "INTL" => TowerStyle.Slab,
                    _ => System.Enum.TryParse<TowerStyle>(plan, true, out var t) ? t : TowerStyle.Auto,
                },
                Wealth = g.Length > 19 ? g[19].ToUpperInvariant() switch { "P" => TownWealth.Poor, "A" => TownWealth.Affluent, _ => TownWealth.Working }
                                       : TownWealth.Working,
            };
        }

        /// <summary>Generate UG_CULLGEN's building and build it through BuildingRuntime.Build, outside the town's extent on the
        /// side away from its bearing, its front (the plan's z = 0 side) facing the town's middle across a 35 m gap.</summary>
        IEnumerable<int> BuildGenerated(string gen)
        {
            var spec = GenSpec(gen);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            BuildingGenResult res = null;
            try { res = BuildingGen.Generate(spec); }
            catch (System.Exception e) { Log.Err($"[cullprobe] UG_CULLGEN '{gen}' failed to generate: {e.Message}"); yield break; }
            _genMs = clock.Elapsed.TotalMilliseconds;
            if (res == null) { Log.Err($"[cullprobe] UG_CULLGEN '{gen}' generated nothing"); yield break; }
            // the parent and the placer the town's own buildings were built with
            Node3D parent = null;
            foreach (var b in _town.Buildings) if (b.Node != null && GodotObject.IsInstanceValid(b.Node)) { parent = b.Node.GetParent() as Node3D; break; }
            parent ??= GetParent() as Node3D;
            var objs = (parent as Editor)?.Objects;
            if (objs == null) Log.Err("[cullprobe] no EditorObjects beside the town: the generated building gets no furniture");
            float wx = res.CellsX * BuildingGen.Cell, dz = res.CellsZ * BuildingGen.Cell;
            // +Z away from the town (its front toward it), +X = up x +Z: a proper turn, as TownRoadGrid.FrameFor makes one
            var zw = new Vector3(Mathf.Sin(_town.Bearing), 0f, Mathf.Cos(_town.Bearing)).Normalized();
            var xw = Vector3.Up.Cross(zw).Normalized();
            var origin = _town.Centre + zw * (_town.Extent + 35f) - xw * (wx * 0.5f);
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var (u, v) in new[] { (0f, 0f), (wx, 0f), (wx, dz), (0f, dz), (wx * 0.5f, dz * 0.5f) })
            {
                var q = origin + xw * u + zw * v;
                float h = Ground(q); lo = Mathf.Min(lo, h); hi = Mathf.Max(hi, h);
            }
            origin.Y = hi + TownRoadGrid.BuildingClearance;
            var frame = new Transform3D(new Basis(xw, Vector3.Up, zw), origin);
            clock.Restart();
            var node = BuildingRuntime.Build(res, frame, parent, objs, "Building_Generated");
            _buildMs = clock.Elapsed.TotalMilliseconds;
            if (node == null) { Log.Err("[cullprobe] UG_CULLGEN: Build returned nothing"); yield break; }
            _genRooms = BuildingRuntime.LastRooms;
            _gen = new TownRoadGrid.TownBuilding { Spec = spec, Result = res, Frame = frame, Node = node, PadY = hi, Ms = _genMs };
            _town.Buildings.Add(_gen);     // (the census and the experiments walk the town's buildings: it is one of them now)
            var f = BuildingRuntime.LastFurnished;
            Log.Print($"[cullprobe] UG_CULLGEN '{gen}': a {res.Kind} ({res.Plan}), {res.CellsX}x{res.CellsZ} cells ({wx:0} x {dz:0} m), {res.Floors} storeys "
                    + $"({res.Floors * BuildingGen.Storey:0.0} m), {res.Layout.Count} rooms, {res.Doors.Count} doors, {res.Walls.Count} planned surfaces; "
                    + $"generated in {_genMs:0} ms, built in {_buildMs:0} ms -- {BuildingRuntime.LastWalls} surfaces, {BuildingRuntime.LastProps} pieces "
                    + $"({f?.Colliders} with a collider, {f?.Containers} containers, {f?.Lamps.Count} lamps); furnishing: {f?.Timings}; ground under it {lo:0.00}..{hi:0.00} m");
            Log.Print($"[cullprobe]   props by list: {res.Furniture.Count} furniture, {res.RuntimeDecor.Count} rugs and curtains, {res.HvacVents.Count} vents, "
                    + $"{res.Electrical.Count} electrical, {res.Doodads.Count} doodads; grouped by room "
                    + (_genRooms == null ? "NOT (the room gate is off)" : $"{_genRooms.Grouped} in {System.Linq.Enumerable.Sum(_genRooms.Storeys, st => st.Rooms.Count)} rooms on "
                       + $"{_genRooms.Storeys.Count} storeys, {_genRooms.NoRoom} with no room, {_genRooms.BasementLamps} basement lamps left out"));
            yield return 4;   // its last nodes ready, a frame drawn with it
        }

        /// <summary>What a drawable of the target is, by the nearest thing over it that says: a surface by its kind (an outside
        /// wall, a partition, a slab, a floor cover, a roof, a railing, a stair), what hangs in a surface (glazing, a door), and a
        /// piece of furniture by the list the generator put it in (furniture, rugs and curtains, vents, electrical, doodads) or
        /// as a lamp.</summary>
        Dictionary<string, string> _propList;
        string PieceLabel(string name)
        {
            if (_propList == null)
            {
                var res = _b.Result;
                _propList = new Dictionary<string, string>();
                foreach (var m in res.Doodads) _propList[m.Name] = "doodads";
                foreach (var m in res.HvacVents) _propList[m.Name] = "vents";
                foreach (var m in res.Electrical) _propList[m.Name] = "electrical";
                foreach (var m in res.RuntimeDecor) _propList[m.Name] = "rugs+curtains";
            }
            if (LampLight.KindFor(name) != LampLight.Kind.Generic) return "lamps";
            return _propList.TryGetValue(name, out var l) ? l : "furniture";
        }

        static string SurfaceLabel(WallSurface w) => w.Kind switch
        {
            SurfaceKind.Railing => "railings",
            SurfaceKind.Stairs => "stairs",
            SurfaceKind.Roof => "roofs",
            SurfaceKind.Floor => w.Thickness < 0.1f ? "floor covers" : "slabs",
            SurfaceKind.Foundation => "foundations",
            _ => w.Thickness < (BuildingGen.Ext + BuildingGen.Inner) * 0.5f ? "partitions" : "outside walls",
        };

        /// <summary>The target's census by kind: drawables (GeometryInstance3D), and of the whole: nodes, collision shapes,
        /// occluders, lights.</summary>
        void TargetCensus(string when)
        {
            var gis = new SortedDictionary<string, int>();
            var shown = new SortedDictionary<string, int>();
            int nodes = 0, shapes = 0, occ = 0, lights = 0, lightsShown = 0;
            void Walk(Node n, string label)
            {
                nodes++;
                if (n is WallSurface w) label = SurfaceLabel(w);
                else if (n is GlassPane || n.Name.ToString().StartsWith("GlassShards")) label = "glazing";
                else if (n.GetParent() is WallSurface && n is Node3D && n is not GeometryInstance3D && n is not StaticBody3D && n is not OccluderInstance3D) label = "doors";
                else if (n.HasMeta(BuildingRuntime.PieceMeta)) label = PieceLabel(n.GetMeta(BuildingRuntime.PieceMeta).AsString());
                if (n is GeometryInstance3D)
                {
                    gis[label] = (gis.TryGetValue(label, out int c) ? c : 0) + 1;
                    if (((GeometryInstance3D)n).IsVisibleInTree()) shown[label] = (shown.TryGetValue(label, out int s) ? s : 0) + 1;
                }
                if (n is CollisionShape3D) shapes++;
                if (n is OccluderInstance3D o && o.Occluder != null) occ++;
                if (n is Light3D l) { lights++; if (l.IsVisibleInTree()) lightsShown++; }
                foreach (var ch in n.GetChildren()) Walk(ch, label);
            }
            Walk(_b.Node, "other");
            int total = 0; foreach (var v in gis.Values) total += v;
            int totalShown = 0; foreach (var v in shown.Values) totalShown += v;
            Log.Print($"[cullprobe] target census ({when}): {nodes} nodes, {total} drawables ({totalShown} visible in tree), {shapes} collision shapes, "
                    + $"{occ} occluders, {lights} lights ({lightsShown} visible in tree)");
            Log.Print($"[cullprobe]   drawables by kind: {string.Join(", ", System.Linq.Enumerable.Select(gis, kv => $"{kv.Key} {kv.Value} ({(shown.TryGetValue(kv.Key, out int s) ? s : 0)} shown)"))}");
        }

        /// <summary>The stops round a generated TOWER, from its real size: the street 30 m and 150 m off its front, its biggest
        /// ground-floor room, its biggest room on a middle storey (the 15th, or half way up a lower one), and a high view, 80 m
        /// up and 120 m off its front, that holds the whole of it.</summary>
        void PlanTowerStops()
        {
            var res = _b.Result;
            float c = BuildingGen.Cell, wx = res.CellsX * c, dz = res.CellsZ * c, top = res.Floors * Storey;
            Vector3 doorL = new(wx * 0.5f, 0f, 0f), outL = new(0f, 0f, -1f);
            foreach (var d in res.Doors)
            {
                if (d.Role != DoorRole.Front || d.Floor != 0) continue;
                var (mx, mz) = d.Middle;
                doorL = new Vector3(mx, 0f, mz);
                outL = d.AlongX ? new Vector3(0f, 0f, d.Line == 0 ? -1f : 1f) : new Vector3(d.Line == 0 ? -1f : 1f, 0f, 0f);
                break;
            }
            Vector3 Street(float off) { var e = W(doorL + outL * off); e.Y = Ground(e) + 1.7f; return e; }
            var mid = new Vector3(wx * 0.5f, top * 0.5f, dz * 0.5f);
            _stops.Add(("s30_street", Street(30f), W(doorL + new Vector3(0f, 8f, 0f))));
            _stops.Add(("s150_street", Street(150f), W(new Vector3(doorL.X, top * 0.5f, doorL.Z))));
            if (RoomView(0, 1.7f) is { } g0) _stops.Add(("r0_room", W(g0.Eye), W(g0.Look)));
            int midFloor = res.Floors > 16 ? 15 : res.Floors / 2;
            if (midFloor > 0 && RoomView(midFloor, midFloor * Storey + 1.7f) is { } gm) _stops.Add(($"r{midFloor}_room", W(gm.Eye), W(gm.Look)));
            var hEye = W(doorL + outL * 120f); hEye.Y = Ground(hEye) + 80f;
            _stops.Add(("h_high", hEye, W(mid)));
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
                if (n.HasMeta(BuildingRuntime.PieceMeta)) furniture++;
                if (n is LampLight l) { lamps++; if (l.IsVisibleInTree()) lampsShown++; }
                if (n is OccluderInstance3D o && o.Occluder is ArrayOccluder3D ao) { occ++; occTris += ao.Indices.Length / 3; }
                foreach (var ch in n.GetChildren()) Walk(ch);
            }
            int bn = 0;
            foreach (var b in _town.Buildings) if (b.Node != null && GodotObject.IsInstanceValid(b.Node)) { bn++; Walk(b.Node); }
            Log.Print($"[cullprobe] census ({when}): {bn} buildings -- {gi} drawable instances ({ranged} with a distance cull, {hidden} hidden), "
                    + $"{furniture} furnished pieces (nodes Furnish made, an outlet's source among them), {lamps} lamps ({lampsShown} shown), {occ} occluders ({occTris} triangles)");
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

        /// <summary>The target's lamps: fixtures drawn (visible in the tree) of all it has, and of those how many are within their
        /// light's fade-out distance of the camera (lit).</summary>
        string TargetLamps()
        {
            int n = 0, drawn = 0, lit = 0;
            var at = _cam.GlobalPosition;
            float reach = BuildingRuntime.LampFadeBegin + BuildingRuntime.LampFadeLength;
            void Walk(Node x)
            {
                if (x is LampLight l) { n++; if (l.IsVisibleInTree()) { drawn++; if (l.GlobalPosition.DistanceTo(at) <= reach) lit++; } }
                foreach (var ch in x.GetChildren()) Walk(ch);
            }
            Walk(_b.Node);
            return $"target lamps drawn {drawn}/{n} ({lit} within their light's reach)";
        }

        long _roomTicks; int _roomEvals;
        /// <summary>What the room gate's evaluation cost since the last call: microseconds an evaluation, and how many.</summary>
        string RoomGateCost()
        {
            long dt = BuildingCull.RoomTicks - _roomTicks; int de = BuildingCull.RoomEvals - _roomEvals;
            _roomTicks = BuildingCull.RoomTicks; _roomEvals = BuildingCull.RoomEvals;
            double us = de > 0 ? dt * 1e6 / System.Diagnostics.Stopwatch.Frequency / de : 0;
            return $"room gate {us:0.0} us an evaluation over {de}, {BuildingCull.RoomToggles} nodes switched so far";
        }

        /// <summary>The culling features to switch off one at a time: only those this build and this run actually have on.</summary>
        void MakeConfigs()
        {
            var vp = GetViewport();
            Log.Print($"[cullprobe] viewport occlusion culling: {(vp.UseOcclusionCulling ? "ON" : "off")}; gated basements: {BuildingCull.Count}; "
                    + $"buildings with gated furniture shadows: {BuildingCull.ShadowCount}; buildings with gated rooms: {BuildingCull.RoomBuildings} "
                    + $"({BuildingCull.RoomCount} rooms); storeys with gated shadows: {BuildingCull.ShadowStoreys}");
            if (vp.UseOcclusionCulling)
                Configs.Add(new Config { Tag = "occlusion", Off = () => vp.UseOcclusionCulling = false, On = () => vp.UseOcclusionCulling = true });
            if (BuildingCull.Count > 0)
                Configs.Add(new Config
                {
                    Tag = "basementgate",
                    Off = () => { BuildingCull.ForceShow = true; BuildingCull.Refresh(); },
                    On = () => { BuildingCull.ForceShow = false; BuildingCull.Refresh(); },
                });
            if (BuildingCull.RoomBuildings > 0)
                Configs.Add(new Config
                {
                    Tag = "roomgate",
                    Off = () => { BuildingCull.ForceRooms = true; BuildingCull.Refresh(); },
                    On = () => { BuildingCull.ForceRooms = false; BuildingCull.Refresh(); },
                });
            if (BuildingCull.ShadowStoreys > 0)
                Configs.Add(new Config
                {
                    Tag = "storeyshadowgate",
                    Off = () => { BuildingCull.ForceStoreyShadows = true; BuildingCull.Refresh(); },
                    On = () => { BuildingCull.ForceStoreyShadows = false; BuildingCull.Refresh(); },
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
        /// <summary>UG_CULLEXP=tag,tag: only those experiments (all of them if unset).</summary>
        readonly HashSet<string> _expOnly = System.Environment.GetEnvironmentVariable("UG_CULLEXP") is { Length: > 0 } ex ? new HashSet<string>(ex.Split(',')) : null;

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
            // the room gate at PropCull's SMALL distance instead of the lamp fade's end: nearly every piece of furniture is SMALL
            // (64 m, LodBaker.LayerFor), so at that radius the gate hides only what the pieces' own ranges have already culled
            // -- the picture the gate leaves at that radius is the one without it, but for the pieces with a longer range
            if (BuildingCull.RoomBuildings > 0)
                Experiments.Add(new Config
                {
                    Tag = "roomradius-64",
                    On = () => { BuildingCull.RoomRadius = LodTable.LayerCull("SMALL"); BuildingCull.Refresh(); },
                    Off = () => { BuildingCull.RoomRadius = BuildingCull.DefaultRoomRadius; BuildingCull.Refresh(); },
                });
            if (_gen == null) return;
            // and for a generated target (UG_CULLGEN), what each part of IT costs -- the upper bound of a gate on that part:
            // every piece of its furniture hidden (all a room gate could ever take away); the insides of its storeys hidden
            // (partitions and floor covers, with the doors and glazing they hold: a storey-insides gate's most); nothing of it
            // casting a shadow (its share of the shadow passes)
            var hidden = new List<Node3D>();
            void Hide(System.Func<Node, bool> want)
            {
                hidden.Clear();
                void Walk(Node n)
                {
                    if (n is Node3D n3 && n3.Visible && want(n)) { n3.Visible = false; hidden.Add(n3); return; }
                    foreach (var c in n.GetChildren()) Walk(c);
                }
                Walk(_gen.Node);
            }
            void Unhide() { foreach (var n in hidden) if (GodotObject.IsInstanceValid(n)) n.Visible = true; }
            Experiments.Add(new Config
            {
                Tag = "target-furniture-hidden",
                On = () => Hide(n => n.HasMeta(BuildingRuntime.PieceMeta)),
                Off = Unhide,
            });
            Experiments.Add(new Config
            {
                Tag = "target-insides-hidden",
                On = () => Hide(n => n is WallSurface w && (SurfaceLabel(w) == "partitions" || SurfaceLabel(w) == "floor covers")),
                Off = Unhide,
            });
            var tcast = new List<(GeometryInstance3D G, GeometryInstance3D.ShadowCastingSetting S)>();
            Experiments.Add(new Config
            {
                Tag = "target-casts-no-shadow",
                On = () =>
                {
                    tcast.Clear();
                    void Walk(Node n)
                    {
                        if (n is GeometryInstance3D g && g.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) tcast.Add((g, g.CastShadow));
                        foreach (var c in n.GetChildren()) Walk(c);
                    }
                    Walk(_gen.Node);
                    foreach (var (g, _) in tcast) g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                },
                Off = () => { foreach (var (g, s) in tcast) if (GodotObject.IsInstanceValid(g)) g.CastShadow = s; },
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

        /// <summary>What a config COSTS the CPU at this stop: its feature on and off, interleaved, three rounds of five frames
        /// each way after the lag -- the viewport's measured render time (the render thread's cull and draw recording), the
        /// frame's process time (every _Process: a gate's own evaluation is in it), the frame setup time, and the wall time
        /// between frames (on this box that includes the software rasteriser, so it moves with what is DRAWN, not only with
        /// the CPU's share). Mean, min and max each way.</summary>
        IEnumerable<int> TimeConfig(string tag, Config cfg)
        {
            var on = new List<(double Cpu, double Proc, double Setup, double Wall)>();
            var off = new List<(double Cpu, double Proc, double Setup, double Wall)>();
            for (int round = 0; round < 3; round++)
                foreach (bool want in new[] { true, false })
                {
                    if (want) cfg.On(); else cfg.Off();
                    yield return _lag;
                    ulong t0 = Godot.Time.GetTicksUsec();
                    for (int k = 0; k < 5; k++)
                    {
                        yield return 1;
                        ulong t1 = Godot.Time.GetTicksUsec();
                        var r = Read();
                        (want ? on : off).Add((r.CpuMs, r.ProcMs, r.SetupMs, (t1 - t0) / 1000.0));
                        t0 = t1;
                    }
                }
            cfg.On();
            string Stat(List<(double Cpu, double Proc, double Setup, double Wall)> l, System.Func<(double Cpu, double Proc, double Setup, double Wall), double> f)
            {
                double sum = 0, lo = double.MaxValue, hi = double.MinValue;
                foreach (var v in l) { double x = f(v); sum += x; lo = System.Math.Min(lo, x); hi = System.Math.Max(hi, x); }
                return l.Count == 0 ? "-" : $"{sum / l.Count:0.00} [{lo:0.00}..{hi:0.00}]";
            }
            Log.Print($"[cullprobe] {cfg.Tag} cost at {tag} ({on.Count} frames each way, interleaved; mean [min..max] ms) -- "
                    + $"render cpu ON {Stat(on, v => v.Cpu)} vs off {Stat(off, v => v.Cpu)}; process ON {Stat(on, v => v.Proc)} vs off {Stat(off, v => v.Proc)}; "
                    + $"setup ON {Stat(on, v => v.Setup)} vs off {Stat(off, v => v.Setup)}; frame wall ON {Stat(on, v => v.Wall)} vs off {Stat(off, v => v.Wall)}");
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
            // UG_CULLGEN=<UG_EDITGEN's string>: one generated building, built through the town's path beside the town, is the target
            var genEnv = System.Environment.GetEnvironmentVariable("UG_CULLGEN");
            if (!string.IsNullOrEmpty(genEnv)) foreach (var e in BuildGenerated(genEnv)) yield return e;
            _b = Pick();
            if (_b == null) { Log.Err("[cullprobe] no built building in the town"); yield break; }
            MakeConfigs();
            MakeExperiments();
            var res = _b.Result;
            Log.Print($"[cullprobe] building {_b.Node.Name} at {_b.Node.GlobalPosition}: {res.Kind}, {res.CellsX}x{res.CellsZ} cells, {res.Floors} floor(s), "
                    + $"basement {(res.HasBasement ? $"{res.Basement} by {res.BasementAccess}" : "none")}; configs: {string.Join(", ", Configs.ConvertAll(c => c.Tag))}");
            Census("as built");
            if (_gen != null) { TargetCensus("as built"); PlanTowerStops(); }
            else PlanStops();
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
                        + $"({BuildingCull.ShownCount}/{BuildingCull.Count} basements drawn, {BuildingCull.CastingCount}/{BuildingCull.ShadowCount} buildings' furniture casting) "
                        + $"| rooms drawn {BuildingCull.RoomsShown}/{BuildingCull.RoomCount} | storeys casting {BuildingCull.ShadowStoreysCasting}/{BuildingCull.ShadowStoreys} "
                        + $"| {TargetLamps()} | {RoomGateCost()}");
                Save($"cull_{tag}_all");
                // the same frame again, nothing changed: the noise floor any picture-to-picture difference is read against (the
                // sky, the water, a lamp's flicker all move between two frames on their own)
                foreach (var e in Settle($"{tag} again")) yield return e;
                Log.Print($"[cullprobe] {tag} all again: {_last}");
                Save($"cull_{tag}_again");
                if (!_shotSaved && !string.IsNullOrEmpty(_shot))
                {
                    // the --shot path gets the first stop's frame (a missing file is how tools/shot.py reads a failed run)
                    GetViewport().GetTexture()?.GetImage()?.SavePng(_shot);
                    _shotSaved = true;
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
                    if (tag != "a_street" && tag != "b_room" && _gen == null) continue;   // street-level questions (every stop of a generated target's)
                    if (_expOnly != null && !_expOnly.Contains(x.Tag)) continue;
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
            // (A generated target's: at every stop, and for every config named in UG_CULLTIME -- occlusion if unset.)
            if (System.Environment.GetEnvironmentVariable("UG_CULLTIMING") != "0")
            {
                var timed = new List<Config>();
                // (a+b: those configs off and on together)
                foreach (var t in (System.Environment.GetEnvironmentVariable("UG_CULLTIME") ?? "occlusion").Split(','))
                {
                    var parts = Configs.FindAll(c => System.Array.IndexOf(t.Split('+'), c.Tag) >= 0);
                    if (parts.Count == 0) continue;
                    timed.Add(new Config { Tag = t, Off = () => { foreach (var c in parts) c.Off(); }, On = () => { foreach (var c in parts) c.On(); } });
                }
                foreach (var (tag, eye, look) in _stops)
                {
                    if (tag != "a_street" && tag != "b_room" && _gen == null) continue;
                    Aim(eye, look);
                    yield return 6;
                    foreach (var cfg in timed)
                        foreach (var e in TimeConfig(tag, cfg)) yield return e;
                }
            }
            // what the room gate's evaluation costs a frame at each stop, alone: every gate evaluated 500 times with the camera there
            // (the basement and shadow gates' loops run too, but only the room gate's own time is counted)
            if (BuildingCull.RoomBuildings > 0)
                foreach (var (tag, eye, look) in _stops)
                {
                    Aim(eye, look);
                    yield return 2;
                    long t0 = BuildingCull.RoomTicks; int e0 = BuildingCull.RoomEvals;
                    for (int k = 0; k < 500; k++) BuildingCull.Refresh();
                    double us = (BuildingCull.RoomTicks - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency / Mathf.Max(1, BuildingCull.RoomEvals - e0);
                    Log.Print($"[cullprobe] room gate at {tag}: {us:0.0} us an evaluation (500 in a row), {BuildingCull.RoomBuildings} buildings gated, "
                            + $"{BuildingCull.RoomBuildingsNear} past the broad phase, rooms drawn {BuildingCull.RoomsShown}/{BuildingCull.RoomCount}");
                }
            // what SWITCHING costs, which the evaluation above does not include: every room node of the target hidden and shown
            // again (Node3D.Visible, propagated to every piece under it), and every drawable under them told to cast no shadow and
            // to cast again (what the storey shadow gate does to a storey), three times each, timed on the main thread
            if (_genRooms != null && _genRooms.Storeys.Count > 0)
            {
                BuildingCull.ForceRooms = true; BuildingCull.Refresh();
                var nodes = new List<Node3D>(); var gis = new List<(GeometryInstance3D G, GeometryInstance3D.ShadowCastingSetting S)>();
                void Walk(Node n) { if (n is GeometryInstance3D g && g.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) gis.Add((g, g.CastShadow)); foreach (var c in n.GetChildren()) Walk(c); }
                foreach (var st in _genRooms.Storeys) foreach (var (_, rn) in st.Rooms) if (GodotObject.IsInstanceValid(rn)) { nodes.Add(rn); Walk(rn); }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int k = 0; k < 3; k++) { foreach (var rn in nodes) rn.Visible = false; foreach (var rn in nodes) rn.Visible = true; }
                double visMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                for (int k = 0; k < 3; k++)
                {
                    foreach (var (g, _) in gis) g.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                    foreach (var (g, c) in gis) g.CastShadow = c;
                }
                double castMs = sw.Elapsed.TotalMilliseconds;
                BuildingCull.ForceRooms = false; BuildingCull.Refresh();
                Log.Print($"[cullprobe] switching cost: {nodes.Count} room nodes holding {gis.Count} casting drawables -- a room hidden or shown "
                        + $"{visMs * 1000.0 / (6 * nodes.Count):0.0} us ({visMs * 1000.0 / (6 * gis.Count):0.00} us a drawable under it); a drawable's shadow switched "
                        + $"{castMs * 1000.0 / (6 * gis.Count):0.00} us");
                yield return 2;
            }
            Log.Print("[cullprobe] done");
        }
    }
}
