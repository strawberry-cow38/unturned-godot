using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedSim
{
    /// <summary>A window a room's layout changed after the walls were cut (BuildingGen.Furnish): the room, the wall, the
    /// window as BuildWalls cut it and as it is now (null: taken out), what was done and for which piece. The change
    /// itself is in the wall's Openings; this is the record of it.</summary>
    public sealed class WindowEdit
    {
        public int Floor;
        public GenRoom Room;
        public WallPlan Wall;
        public WallOpening Before;
        public WallOpening? After;
        /// <summary>"sill" (raised over a counter run), "slide", "narrow" or "remove".</summary>
        public string What;
        /// <summary>The piece it was done for.</summary>
        public string For;
        public override string ToString() => System.FormattableString.Invariant(
            $"{Room} {What} for {For}: u {Before.U:0.00}..{Before.U1:0.00} v {Before.V:0.00}..{Before.V1:0.00} -> {(After is WallOpening a ? $"u {a.U:0.00}..{a.U1:0.00} v {a.V:0.00}..{a.V1:0.00}" : "gone")}");
    }

    /// <summary>Where a window was free to be: its share of the stretch of wall BuildWalls laid it in (wall U).</summary>
    internal sealed class WindowSlot
    {
        public readonly WallPlan W; public readonly GenRoom Room; public readonly float U0, U1;
        public WindowSlot(WallPlan w, GenRoom room, float u0, float u1) { W = w; Room = room; U0 = u0; U1 = u1; }
    }

    public static partial class BuildingGen
    {
        /// <summary>How deep the floor in front of a sink is kept to stand at (strawberry: "make sure the sink has adequate
        /// standing space in front of it"), across the sink's whole width: nothing else stands there, and no door's leaf
        /// swings through it. 0.9 m: the player's capsule is 0.7 across (PlayerController: radius 0.35) and stands about
        /// real size before a sink built about twice it (Counter_1 is 1.5 m wide), so the real clear floor in front of a
        /// basin -- 30 in, 0.76 m (NKBA, ADA's 30 x 48 in) -- is the least, and the capsule with 0.1 m either side of it is
        /// 0.9, as BedAccess is the capsule and 0.1 to walk past a bed.</summary>
        public const float SinkStand = 0.9f;
        /// <summary>How far in front of a piece the floor is kept to stand at, over and above any doors it opens
        /// (<see cref="Swing"/>): a sink's.</summary>
        public static float Stand(string name) => name == "Counter_1" ? SinkStand : 0f;

        /// <summary>A window raised over a counter run keeps its sill this far above the tallest piece in front of it.</summary>
        public const float SillOverCounter = 0.1f;
        /// <summary>A window raised over a counter keeps at least this much glass; less and it is not raised (a room with
        /// another window loses it instead).</summary>
        public const float LeastGlass = 0.6f;
        /// <summary>What a kitchen's run is made of: what stands with its back to the wall at worktop height.</summary>
        static bool CounterRun(string name) => name is "Counter_0" or "Counter_1" or "Counter_2" or "Oven_0" or "Disher_0" or "Corner_Cabinet_0_Solid" or "Corner_Cabinet_1_Solid";

        /// <summary>The floor in front of an opening, both sides of its wall: `deep` past either face, `pad` past either end.
        /// A window's is where nothing tall stands (FloorPlan.KeepTall), a doorway's where nothing stands.</summary>
        static (float X0, float Z0, float X1, float Z1) OpeningZone(WallPlan w, WallOpening o, float pad, float deep)
        {
            var (wc, ws) = PlanTrig.CosSin(w.Yaw);
            float dx = wc, dz = -ws, nx = ws, nz = wc;
            float u0 = o.U - pad, u1 = o.U + o.Width + pad;
            float ax = w.X + dx * u0, az = w.Z + dz * u0, bx = w.X + dx * u1, bz = w.Z + dz * u1;
            float t = w.Thickness * 0.5f + deep;
            return (MathF.Min(MathF.Min(ax, bx) - nx * t, MathF.Min(ax, bx) + nx * t), MathF.Min(MathF.Min(az, bz) - nz * t, MathF.Min(az, bz) + nz * t),
                    MathF.Max(MathF.Max(ax, bx) - nx * t, MathF.Max(ax, bx) + nx * t), MathF.Max(MathF.Max(az, bz) - nz * t, MathF.Max(az, bz) + nz * t));
        }

        /// <summary>The floor a hung door's leaf sweeps, as a box: a quarter circle its own width round the hinge, on the
        /// side of the wall it opens to (WallOpening.DoorOpensFront), across the whole opening whichever jamb it hangs on
        /// -- from `from` off the wall's mid-plane to the leaf's reach. Where the hinge is decides where the open leaf rests,
        /// not what it sweeps.</summary>
        /// (`pad`: how far past either jamb; a wall standing across at a jamb is where the open leaf rests, not in its way.
        /// `reach`: how far off the mid-plane the leaf sweeps -- by default its opening's width past the wall's face, the
        /// room the furniture keeps; the leaf itself reaches LeafReach.)
        public static (float X0, float Z0, float X1, float Z1) LeafSweep(WallPlan w, WallOpening o, float from = 0f, float pad = 0.05f, float reach = float.NaN)
        {
            var (wc, ws) = PlanTrig.CosSin(w.Yaw);
            float dx = wc, dz = -ws, side = o.DoorOpensFront ? 1f : -1f, nx = ws * side, nz = wc * side;
            float s0 = o.U - pad, s1 = o.U + o.Width + pad, near = from, far = float.IsNaN(reach) ? w.Thickness * 0.5f + o.Width : reach;
            float px = w.X + dx * s0, pz = w.Z + dz * s0, qx = w.X + dx * s1, qz = w.Z + dz * s1;
            float ax = MathF.Min(px, qx), az = MathF.Min(pz, qz), bx = MathF.Max(px, qx), bz = MathF.Max(pz, qz);
            return (MathF.Min(ax + nx * near, ax + nx * far), MathF.Min(az + nz * near, az + nz * far),
                    MathF.Max(bx + nx * near, bx + nx * far), MathF.Max(bz + nz * near, bz + nz * far));
        }

        /// <summary>How far a hung door's leaf reaches off its wall's mid-plane, open: the leaf is fitted to the clear span
        /// inside the frame (WallSurface.ClearOpeningSize: the opening less a TrimProfile at each jamb) and swings about its
        /// thickness's middle, which stands on the mid-plane (WallSurface.PlaceDoor) -- so a 2.5 m doorway's leaf reaches
        /// 2.1 m, not the 2.85 the furniture keeps clear past the wall's face. What a door can open into is what the leaf
        /// meets, not the room kept for the furniture.</summary>
        public static float LeafReach(WallOpening o) => o.Width - 2f * WallOpenings.TrimProfile;

        sealed partial class FloorPlan
        {
            /// <summary>How many windows' zones each cell is in: KeepTall is that, or a living room's sight line (Sight), so a
            /// window moved or taken out frees exactly the floor that only it was keeping.</summary>
            public int[,] WinZones;
            public bool[,] Sight;
            /// <summary>Each window's zone on this floor, and the room and slot it has: what a room's layout may change.</summary>
            public readonly List<Pane> Panes = new();
            /// <summary>Every hung door's leaf sweep, exactly, on the side it opens to: no one stands at a sink in one.</summary>
            public readonly List<(float X0, float Z0, float X1, float Z1)> LeafRects = new();

            public sealed class Pane
            {
                public WallPlan W; public int I; public GenRoom Room;
                /// <summary>Its slot, in wall U: where it may slide.</summary>
                public float A, B;
                public (float X0, float Z0, float X1, float Z1) Zone;
                public bool Gone;
                public WallOpening O => W.Openings[I];
            }

            /// <summary>A window's zone into the grid (n = +1) or out of it (-1), KeepTall kept in step.</summary>
            public void Zone((float X0, float Z0, float X1, float Z1) a, int n)
            {
                foreach (var (i, j) in Cells(a))
                {
                    WinZones[i, j] = Math.Max(0, WinZones[i, j] + n);
                    KeepTall[i, j] = WinZones[i, j] > 0 || Sight[i, j];
                }
            }

            /// <summary>A window of this floor: its zone kept clear of anything tall, and where its room may move it.</summary>
            void AddPane(WallPlan w, int k, (float X0, float Z0, float X1, float Z1) zone)
            {
                Zone(zone, +1);
                var o = w.Openings[k];
                var slot = _res.WindowSlots.FirstOrDefault(s => s.W == w && s.U0 - 0.01f <= o.U && o.U1 <= s.U1 + 0.01f);
                GenRoom room = slot?.Room;
                if (room == null)
                {
                    // (one BuildWalls did not lay out: the room a metre in from its middle, either side)
                    var (wc, ws) = PlanTrig.CosSin(w.Yaw);
                    float u = o.U + o.Width * 0.5f, cx = w.X + wc * u, cz = w.Z - ws * u;
                    room = _res.Layout.FirstOrDefault(g => g.Floor == F && (g.Holds(cx - ws, cz - wc) || g.Holds(cx + ws, cz + wc)));
                }
                Panes.Add(new Pane { W = w, I = k, Room = room, A = slot?.U0 ?? o.U, B = slot?.U1 ?? o.U1, Zone = zone });
            }

            /// <summary>Hang this floor's doors (strawberry: "allow room layouts to decide door swing direction"): for each
            /// hung door, the face its leaf opens out of and the jamb it hangs on -- so that it opens into the room it serves
            /// (a bedroom's or a bathroom's into the bedroom or bathroom, an en-suite's into the bathroom, a room off a hall or
            /// a stairwell into the room, a door to outside into the house), never over the stairs or the tiles at a flight's
            /// ends, never into a wall (a corridor too narrow for the leaf), clear of another door's leaf and doorway where it
            /// can be; and hinged on the jamb nearer a wall standing across its way, so the open leaf folds flat against that
            /// wall rather than standing out into the room. Before anything is furnished: the furniture keeps out of the
            /// sweep it chose (LeafRects, KeepDoor). No draws: a door is hung the same way every time.</summary>
            void HangDoors(List<WallPlan> walls)
            {
                var swept = new List<(float, float, float, float)>();
                var ways = new List<(WallPlan W, WallOpening O, (float, float, float, float) Box)>();
                foreach (var w in walls) foreach (var o in w.Openings) if (o.V < 0.05f) ways.Add((w, o, OpeningZone(w, o, 0f, 1.0f)));
                static bool Over((float X0, float Z0, float X1, float Z1) a, (float X0, float Z0, float X1, float Z1) b)
                    => a.X0 < b.X1 - 0.01f && a.X1 > b.X0 + 0.01f && a.Z0 < b.Z1 - 0.01f && a.Z1 > b.Z0 + 0.01f;
                static bool Private(RoomKind k) => k is RoomKind.Bedroom or RoomKind.Bathroom or RoomKind.Closet or RoomKind.Office;
                foreach (var w in walls)
                {
                    var own = Solid(w).ToList();
                    var (wc, ws) = PlanTrig.CosSin(w.Yaw);
                    float dx = wc, dz = -ws, t = w.Thickness * 0.5f;
                    for (int k = 0; k < w.Openings.Count; k++)
                    {
                        var o = w.Openings[k];
                        if (o.V >= 0.05f || !o.HasDoor || o.Archetype == ArchGarage) continue;
                        float um = o.U + o.Width * 0.5f, cx = w.X + dx * um, cz = w.Z + dz * um;
                        GenRoom Side(float s) => _res.Layout.FirstOrDefault(g => g.Floor == F && g.Holds(cx + ws * s * (t + 0.3f), cz + wc * s * (t + 0.3f)));
                        // how far the wall runs on past a jamb (dir -1: past U, +1: past U1) before a wall across it on side s
                        // -- one standing out from this wall, met within a metre of its face: where the open leaf would rest
                        // against it (3: nothing near). (Deeper, it found a narrow hall's far wall, which is in front of the
                        // door, not beside it.)
                        float Fold(float s, int dir)
                        {
                            float j = dir < 0 ? o.U : o.U1;
                            for (float g = 0f; g <= 3f; g += 0.05f)
                            {
                                float u = j + dir * g;
                                foreach (float deep in new[] { 0.5f, 1.0f })
                                {
                                    float px = w.X + dx * u + ws * s * (t + deep), pz = w.Z + dz * u + wc * s * (t + deep);
                                    if (WallRects.Any(r => !own.Contains(r) && px >= r.X0 && px <= r.X1 && pz >= r.Z0 && pz <= r.Z1)) return g;
                                }
                            }
                            return 3f;
                        }
                        (double Score, bool Front, bool Start) best = (double.NegativeInfinity, false, false);
                        foreach (bool front in new[] { false, true })
                        {
                            float s = front ? 1f : -1f;
                            GenRoom into = Side(s), from = Side(-s);
                            var probe = o; probe.DoorOpensFront = front;
                            var sweep = LeafSweep(w, probe, t + 0.02f);
                            var inner = LeafSweep(w, probe, t + 0.02f, -0.05f, LeafReach(o));
                            double side = 0;
                            if (into == null) side -= 1000;                                                   // out of the building
                            var leaf = LeafSweep(w, probe, t + 0.02f, 0.05f, LeafReach(o));
                            if (StairRects.Any(r => Over(leaf, r)) || TileRects.Any(r => Over(leaf, r))) side -= 500;     // over the stairs
                            if (WallRects.Any(r => !own.Contains(r) && Over(inner, r))) side -= 300;                    // into a wall: no room for it
                            if (into != null && from != null && into.Circulation && !from.Circulation) side -= 50;      // into the hall, not the room
                            if (from != null && Private(from.Kind) && (into == null || !Private(into.Kind))) side -= 40; // out of a bedroom or bathroom
                            if (into != null && from != null && Private(into.Kind) && Private(from.Kind) && into.Kind is RoomKind.Bathroom or RoomKind.Closet) side += 5;
                            if (swept.Any(r => Over(sweep, r))) side -= 30;                                             // into another door's leaf
                            if (ways.Any(x => (x.W != w || MathF.Abs(x.O.U - o.U) > 0.01f) && Over(sweep, x.Box))) side -= 10;   // across another doorway
                            foreach (bool start in new[] { false, true })
                            {
                                // fold flat against the nearer wall across its way; where neither is near, as it always hung
                                double score = side - 2 * Fold(s, start ? -1 : 1) + (front ? 0 : 0.3) + (start ? 0 : 0.1);
                                if (score > best.Score) best = (score, front, start);
                            }
                        }
                        o.DoorOpensFront = best.Front; o.DoorHingeAtStart = best.Start;
                        w.Openings[k] = o;
                        swept.Add(LeafSweep(w, o, t));
                    }
                }
            }
        }

        sealed partial class Furnisher
        {
            /// <summary>Draws for the window changes' trials, apart from the shared stream: a room that tries a window and
            /// keeps it as it was leaves every later room's furniture where it was.</summary>
            Rng _paneRng;
            Rng PaneRng => _paneRng ??= new Rng(unchecked(((Seed * 486187739 + _room.Floor) * 486187739 + _room.X0) * 486187739 + _room.Z0) ^ 0x3A11);

            /// <summary>This room's windows, as they now are.</summary>
            List<FloorPlan.Pane> MyPanes() => _p.Panes.Where(q => !q.Gone && q.Room == _room).ToList();

            /// <summary>A pane's wall's direction and where a plan point is along it (wall U).</summary>
            static float UOf(WallPlan w, float x, float z) { var (c, s) = PlanTrig.CosSin(w.Yaw); return (x - w.X) * c - (z - w.Z) * s; }

            /// <summary>Change a window, and its zone with it; returns how to put it back. `to` null takes it out.</summary>
            Action Reshape(FloorPlan.Pane q, WallOpening? to)
            {
                var before = q.O; var zone = q.Zone; int at = q.I;
                _p.Zone(zone, -1);
                if (to is WallOpening n)
                {
                    q.W.Openings[at] = n;
                    q.Zone = OpeningZone(q.W, n, 0.1f, 0.7f);
                    _p.Zone(q.Zone, +1);
                }
                else
                {
                    q.W.Openings.RemoveAt(at);
                    q.Gone = true;
                    foreach (var r in _p.Panes) if (r != q && r.W == q.W && r.I > at) r.I--;
                }
                Recount(zone); Recount(q.Zone);
                return () =>
                {
                    if (q.Gone)
                    {
                        foreach (var r in _p.Panes) if (r != q && r.W == q.W && r.I >= at) r.I++;
                        q.W.Openings.Insert(at, before);
                        q.Gone = false;
                    }
                    else { _p.Zone(q.Zone, -1); q.W.Openings[at] = before; }
                    Recount(q.Zone);
                    q.Zone = zone;
                    _p.Zone(zone, +1);
                    Recount(zone);
                };
            }

            /// <summary>Would a window with this zone keep the rules for what already stands in its room: nothing tall in
            /// front of it, nothing hung where it would be glass, no bed's head under it?</summary>
            bool PaneFits((float X0, float Z0, float X1, float Z1) zone)
            {
                foreach (var m in _mine)
                {
                    if (m.On != null || m.Ceiling) continue;
                    if (m.Mounted) { if (Overlaps(Grow(zone, 0.6f), m.Footprint)) return false; continue; }
                    if (FurnitureCatalog.Sizes[m.Name].H > TallFurniture && Overlaps(zone, m.Footprint)) return false;
                    if (m.Name.StartsWith("Bed") && Overlaps(zone, HeadStrip(m.Name, (m.X, m.Z, m.Yaw)))) return false;
                }
                return !(_res.Chimney.X1 > _res.Chimney.X0 && Overlaps(zone, _res.Chimney));
            }

            /// <summary>The strip behind a piece's back that UnderWindow reads: where a bed's head meets the wall.</summary>
            static (float X0, float Z0, float X1, float Z1) HeadStrip(string name, (float X, float Z, float Yaw) c)
            {
                var fp = FurnitureCatalog.Footprint(name, c.X, c.Z, c.Yaw);
                var (fx, fz) = FrontOf(c.Yaw);
                return fx != 0 ? (fx > 0 ? (fp.X0 - 0.8f, fp.Z0, fp.X0, fp.Z1) : (fp.X1, fp.Z0, fp.X1 + 0.8f, fp.Z1))
                               : (fz > 0 ? (fp.X0, fp.Z0 - 0.8f, fp.X1, fp.Z0) : (fp.X0, fp.Z1, fp.X1, fp.Z1 + 0.8f));
            }

            /// <summary>Move a window out of the way of `need` (a box of floor in front of its wall): slid along its slot
            /// where it fits whole, else narrowed (to MinWindow at the least) on the side of it away from the piece -- each
            /// the nearer to where it was the better -- else taken out, where the room has another window to keep it lit.
            /// Never past its slot, so never onto another room's wall, a doorway, the stairs' wall or a roof's. Adds how
            /// to put it back to `undo`; false (and nothing changed) when it cannot.</summary>
            bool MakeWay(FloorPlan.Pane q, (float X0, float Z0, float X1, float Z1) need, string why, List<Action> undo, List<WindowEdit> edits)
            {
                var o = q.O;
                // the stretch of its wall the piece needs, the window's zone kept off it (its 0.1 pad and a little)
                float a = MathF.Min(UOf(q.W, need.X0, need.Z0), UOf(q.W, need.X1, need.Z1)), b = MathF.Max(UOf(q.W, need.X0, need.Z0), UOf(q.W, need.X1, need.Z1));
                a = MathF.Min(a, MathF.Min(UOf(q.W, need.X0, need.Z1), UOf(q.W, need.X1, need.Z0)));
                b = MathF.Max(b, MathF.Max(UOf(q.W, need.X0, need.Z1), UOf(q.W, need.X1, need.Z0)));
                float f0 = a - 0.15f, f1 = b + 0.15f;
                var tries = new List<(float U, float W, double Cost)>();
                foreach (var (p0, p1) in new[] { (q.A, MathF.Min(q.B, f0)), (MathF.Max(q.A, f1), q.B) })
                {
                    float room = p1 - p0, wd = MathF.Min(o.Width, room);
                    if (wd < MinWindow - 1e-3f) continue;
                    float u = Math.Clamp(o.U, p0, p1 - wd);
                    // whole beats narrowed, then the least moved
                    tries.Add((u, wd, (o.Width - wd) * 10 + MathF.Abs(u - o.U)));
                }
                foreach (var (u, wd, _) in tries.OrderBy(t => t.Cost))
                {
                    var n = o.MovedTo(u, o.V, wd, o.Height);
                    var zone = OpeningZone(q.W, n, 0.1f, 0.7f);
                    if (!PaneFits(zone)) continue;
                    undo.Add(Reshape(q, n));
                    edits.Add(new WindowEdit { Floor = _room.Floor, Room = _room, Wall = q.W, Before = o, After = n, What = wd < o.Width - 1e-3f ? "narrow" : "slide", For = why });
                    return true;
                }
                // out, where it is not the room's last
                if (MyPanes().Count(p => p != q && p.O.Glazed) == 0) return false;
                undo.Add(Reshape(q, null));
                edits.Add(new WindowEdit { Floor = _room.Floor, Room = _room, Wall = q.W, Before = o, After = null, What = "remove", For = why });
                return true;
            }

            /// <summary>Put a piece where the room's own windows are all that keep it out (strawberry: "allow room layout to
            /// alter windows after the fact if necessary"): at each candidate, best first, the windows whose zone takes the
            /// floor it needs -- under it (`head` false: a tall piece may not stand in front of glass), or behind its head
            /// (`head`: a bed's head goes under no window) -- make way (MakeWay), and it goes in if Put then takes it there;
            /// otherwise every window goes back as it was and the next candidate is tried. Null when none will do.</summary>
            FurniturePlan PutMakingWay(string name, IEnumerable<(float X, float Z, float Yaw)> cands, Func<float, float, float, double> score, bool head)
            {
                foreach (var c in Rank(cands, score, PaneRng).Take(80))
                {
                    var need = head ? HeadStrip(name, c) : FurnitureCatalog.Footprint(name, c.X, c.Z, c.Yaw);
                    var inWay = MyPanes().Where(q => Overlaps(q.Zone, need)).ToList();
                    if (inWay.Count == 0) continue;   // (not a window that keeps it out)
                    var undo = new List<Action>(); var edits = new List<WindowEdit>();
                    if (inWay.All(q => MakeWay(q, need, name, undo, edits)) && (!head || !UnderWindow(name, c)))
                    {
                        var m = Put(name, new[] { c }, null, 0f, PaneRng);
                        if (m != null)
                        {
                            _res.WindowEdits.AddRange(edits);
                            foreach (var e in edits) FurnishTrace?.Invoke($"window {e} in {_room}");
                            return m;
                        }
                    }
                    for (int i = undo.Count - 1; i >= 0; i--) undo[i]();
                }
                return null;
            }

            /// <summary>A window over the counter (strawberry: "allow room layout to alter windows after the fact if
            /// necessary"; a kitchen run wants the wall a window comes down to): where the run -- a counter, the sink, the
            /// stove, the dishwasher, a corner cabinet -- stands with its back to one of the room's windows and rises over its
            /// sill, the sill goes up to SillOverCounter above the tallest of them, its head where it was. Where that would
            /// leave less than LeastGlass, it is taken out instead if the room has another window, else left.</summary>
            void WindowsOverCounters()
            {
                foreach (var q in MyPanes())
                {
                    var o = q.O;
                    if (!o.Glazed) continue;
                    // the run in front of the glass itself (not just its pad), backed to this wall
                    float top = 0f; string by = null;
                    foreach (var m in _mine)
                    {
                        if (m.On != null || m.Mounted || m.Ceiling || !CounterRun(m.Name)) continue;
                        var fp = m.Footprint;
                        if (!Overlaps(q.Zone, fp)) continue;
                        float a = MathF.Min(UOf(q.W, fp.X0, fp.Z0), UOf(q.W, fp.X1, fp.Z1)), b = MathF.Max(UOf(q.W, fp.X0, fp.Z0), UOf(q.W, fp.X1, fp.Z1));
                        if (b <= o.U + 0.01f || a >= o.U1 - 0.01f) continue;
                        float h = FurnitureCatalog.Sizes[m.Name].H;
                        if (h > top) { top = h; by = m.Name; }
                    }
                    if (by == null || q.W.Y + o.V >= _room.Floor * Storey + top + SillOverCounter - 1e-3f) continue;
                    float v = _room.Floor * Storey + top + SillOverCounter - q.W.Y, v1 = o.V1;
                    if (v1 - v >= LeastGlass)
                    {
                        var n = o.MovedTo(o.U, v, o.Width, v1 - v);
                        Reshape(q, n);
                        var e = new WindowEdit { Floor = _room.Floor, Room = _room, Wall = q.W, Before = o, After = n, What = "sill", For = by };
                        _res.WindowEdits.Add(e); FurnishTrace?.Invoke($"window {e}");
                    }
                    else if (MyPanes().Count(p => p != q && p.O.Glazed) > 0)
                    {
                        Reshape(q, null);
                        var e = new WindowEdit { Floor = _room.Floor, Room = _room, Wall = q.W, Before = o, After = null, What = "remove", For = by };
                        _res.WindowEdits.Add(e); FurnishTrace?.Invoke($"window {e}");
                    }
                }
            }
        }
    }
}
