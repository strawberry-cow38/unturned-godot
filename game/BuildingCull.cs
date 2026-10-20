using System.Collections.Generic;
using Godot;
using UnturnedSim;

namespace UnturnedGodot
{
    /// <summary>⭐ A GENERATED BUILDING'S BASEMENT, NOT DRAWN WHILE NOTHING CAN SEE INTO IT (strawberry 2026-10-16: "basements
    /// should get fully culled when not in view").
    ///
    /// A basement is a storey of rooms in a hole: its furniture, its partitions, its flight down, all under the ground floor's slab
    /// and behind outside walls standing in the dirt. From the street none of it can be seen, yet every piece was drawn -- and cast
    /// into every shadow pass in range -- wherever the camera stood. BuildingRuntime.Build puts all of that under one node, and
    /// this hides the node unless the camera is somewhere it could be seen from:
    ///   - over the building's plan, grown by PlanMargin -- inside it (the way down is a stair room's flight, or you are in the
    ///     basement itself), or close enough outside to look in at a window -- or over a hatch's well, grown the same: at its
    ///     lip, on its flight;
    ///   - looking DOWN at the door at the foot of a hatch's well -- within HatchRadius of it and at least HatchElevation over
    ///     it. The door is in a pit, behind a retaining wall that stands over the ground: from the street, however near, no
    ///     sight line reaches it, and a gate on distance alone drew the basement for anyone walking past the house.
    /// The SHELL stays drawn: the outside walls (the strip of them over the ground is the building's base, seen from anywhere),
    /// the floor slab, and the hatch's well (open to the sky). So a glance in from past the threshold -- down a well, through a
    /// door -- meets a floor and walls, never the dug-out hole, and what pops at the threshold is only what stands inside.
    ///
    /// VISUAL ONLY. Hiding a Node3D hides what it draws and nothing else: every collider in the basement stays live (Godot does not
    /// tie physics to visibility).
    ///
    /// ⚠ BUT NOT ITS LAMPS. A town's lamps cast no shadow (but the few LightShadowBudget picks), so a basement's ceiling lights
    /// shine up through the ground floor's slab into the rooms above -- and with them gated, the street view of a shop with a
    /// basement changed as the gate did: 2,032 pixels by more than 24/255 round its door and windows, that region's mean
    /// brightness 91.3 -> 87.2, the only stop where the gate changed the picture. With the lamps left out of the gate the same
    /// comparison is 0 pixels. So a lamp is NOT the basement's to hide: it stays drawn, and like every lamp in town it goes out
    /// past BuildingRuntime.LampFadeBegin + LampFadeLength (BuildingRuntime.Furnish keeps lamps out of the gated node). The
    /// leak itself is older than this and is not fixed here.
    ///
    /// UG_NOBASEMENTGATE=1 is the control: Build makes no basement node and nothing is ever hidden. ForceShow is the in-process
    /// one (CullProbe): every gated basement drawn while it is set.
    ///
    /// ⭐ AND A FAR BUILDING'S FURNITURE CASTS NO SHADOW. The shadow passes are what culling cannot reach: the sun's cascades
    /// draw every caster within GraphicsOptions.ShadowDistance (120 m) whether a wall hides it from the camera or not --
    /// occlusion culling is for the camera's own pass -- and in a town most of those casters are furniture, indoors, under a
    /// roof that already puts the whole room in shade (measured at street level in a 21-building town: furniture was 1,200 of
    /// the frame's 2,160 shadow-pass objects). So furniture in a building whose plan is more than ShadowRadius from the camera
    /// stops casting; it still draws. What that can change on screen is a piece's shadow inside a patch of sun through a
    /// window of a building 40 m off -- and no lamp's: a town's lamps are out past 40 m (BuildingRuntime.LampFadeBegin +
    /// LampFadeLength), and a lamp lights only its own room. UG_NOSHADOWGATE=1 is the control (every piece casts as it always
    /// did); ForceShadows the in-process one.
    ///
    /// ⭐ AND A ROOM'S FURNITURE IS DRAWN ONLY NEAR THE ROOM (strawberry 2026-10-20: "have furniture in rooms cull past a certain
    /// distance from the room"): the room gate, below.</summary>
    public partial class BuildingCull : Node
    {
        public static readonly bool Disabled = System.Environment.GetEnvironmentVariable("UG_NOBASEMENTGATE") == "1";
        /// <summary>Draw every gated basement regardless (the in-process control).</summary>
        public static bool ForceShow;

        /// <summary>How far past the basement's outer faces the camera still counts as over the building, metres.</summary>
        public const float PlanMargin = 4f;
        /// <summary>How near the door at the foot of a hatch's well the camera has to be to draw the basement, metres. From the ground
        /// that door can only be seen standing at the well's lip (its retaining wall stands over the ground, and the sight line has
        /// to clear it at 58 degrees or steeper); this reaches a camera high over the well too.</summary>
        public const float HatchRadius = 20f;
        /// <summary>How steeply the camera has to look down at the hatch's door, degrees over level (a sight line past the well's
        /// retaining wall needs 58; this is generous).</summary>
        public const float HatchElevation = 35f;

        sealed class Gate
        {
            public Node3D Building, Node;
            /// <summary>The basement's plan rectangles in the building's frame (Dig: to its outer faces, and the hatch's well), grown
            /// by PlanMargin.</summary>
            public (float X0, float Z0, float X1, float Z1)[] Plan;
            /// <summary>The hatch's door in the building's frame, if it has one.</summary>
            public Vector3? Hatch;
            public bool Shown = true;
        }

        static readonly List<Gate> _gates = new();
        static BuildingCull _hub;

        public static readonly bool NoShadowGate = System.Environment.GetEnvironmentVariable("UG_NOSHADOWGATE") == "1";
        /// <summary>Every piece casts its shadow regardless (the in-process control).</summary>
        public static bool ForceShadows;
        /// <summary>Furniture of a building whose plan is further than this from the camera casts no shadow, metres -- the
        /// distance a town's lamps are out by.</summary>
        public const float ShadowRadius = BuildingRuntime.LampFadeBegin + BuildingRuntime.LampFadeLength;

        sealed class Casters
        {
            public Node3D Building;
            public float Wx, Dz;                        // its plan, (0, 0)..(Wx, Dz) in its frame
            public GeometryInstance3D[] Pieces;
            public GeometryInstance3D.ShadowCastingSetting[] Cast;   // what each cast as built
            public bool On = true;
        }
        static readonly List<Casters> _casters = new();

        /// <summary>Buildings whose furniture is gated, and how many of them are casting right now.</summary>
        public static int ShadowCount => _casters.Count;
        public static int CastingCount { get { int n = 0; foreach (var c in _casters) if (c.On) n++; return n; } }

        /// <summary>Gate the shadows of everything `building` draws but its surfaces (and what hangs off them: doors, glass) --
        /// its furniture, in its basement too -- on how far the camera is from its plan.</summary>
        public static void AddFurnitureShadows(Node3D building, BuildingGenResult res)
        {
            if (NoShadowGate || building == null || res == null || !building.IsInsideTree()) return;
            var pieces = new List<GeometryInstance3D>();
            var cast = new List<GeometryInstance3D.ShadowCastingSetting>();
            void Walk(Node n)
            {
                if (n is WallSurface) return;
                if (n is GeometryInstance3D g && g.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off) { pieces.Add(g); cast.Add(g.CastShadow); }
                foreach (var c in n.GetChildren()) Walk(c);
            }
            Walk(building);
            if (pieces.Count == 0) return;
            _casters.Add(new Casters { Building = building, Wx = res.CellsX * BuildingGen.Cell, Dz = res.CellsZ * BuildingGen.Cell,
                                       Pieces = pieces.ToArray(), Cast = cast.ToArray() });
            Hub(building).Evaluate();
        }

        static BuildingCull Hub(Node3D building)
        {
            if (_hub == null || !IsInstanceValid(_hub))
            {
                _hub = new BuildingCull { Name = "BuildingCull" };
                building.GetParent().AddChild(_hub);
            }
            return _hub;
        }

        /// <summary>Gated basements, and how many are drawn right now.</summary>
        public static int Count => _gates.Count;
        public static int ShownCount { get { int n = 0; foreach (var g in _gates) if (g.Shown) n++; return n; } }

        /// <summary>Gate `basement` (a node under `building`, in its frame) on where the camera stands. Call once the building
        /// stands where it will stay; the gate reads the building's transform every frame, so a moved building is followed.</summary>
        public static void AddBasement(Node3D building, Node3D basement, BuildingGenResult res)
        {
            if (Disabled || building == null || basement == null || res == null || !building.IsInsideTree()) return;
            var plan = new List<(float, float, float, float)>();
            foreach (var (x0, z0, x1, z1) in res.Dig)     // (the hatch's well is one of them)
                plan.Add((x0 - PlanMargin, z0 - PlanMargin, x1 + PlanMargin, z1 + PlanMargin));
            if (plan.Count == 0)
                plan.Add((-PlanMargin, -PlanMargin, res.CellsX * BuildingGen.Cell + PlanMargin, res.CellsZ * BuildingGen.Cell + PlanMargin));
            Vector3? hatch = null;
            if (res.HatchWell.X1 > res.HatchWell.X0)
            {
                foreach (var d in res.Doors)
                    if (d.Floor == -1 && d.B == null)
                    {
                        var (mx, mz) = d.Middle;
                        hatch = new Vector3(mx, -BuildingGen.Storey + 1.2f, mz);
                        break;
                    }
            }
            _gates.Add(new Gate { Building = building, Node = basement, Plan = plan.ToArray(), Hatch = hatch });
            Hub(building).Evaluate();
        }

        /// <summary>Is the camera somewhere this building's basement could be seen from?</summary>
        static bool Wants(Gate g, Vector3 cam)
        {
            var l = g.Building.GlobalTransform.AffineInverse() * cam;
            foreach (var (x0, z0, x1, z1) in g.Plan)
                if (l.X >= x0 && l.X <= x1 && l.Z >= z0 && l.Z <= z1) return true;
            if (g.Hatch is not Vector3 h) return false;
            float d = l.DistanceTo(h);
            return d <= HatchRadius && l.Y - h.Y >= d * Mathf.Sin(Mathf.DegToRad(HatchElevation));
        }

        /// <summary>Is this building's gated basement drawn right now (true for one with no gate)?</summary>
        public static bool IsShown(Node3D building)
        {
            foreach (var g in _gates) if (g.Building == building) return g.Shown;
            return true;
        }

        public override void _Process(double delta) => Evaluate();

        /// <summary>Show or hide every gated basement, and switch every gated building's furniture shadows, for where the camera
        /// is now. Runs every frame (a few hundred point-in-rect tests), and at once when a gate is added or a Force flag changes,
        /// so nothing is ever a frame late at a threshold.</summary>
        public void Evaluate()
        {
            var cam = GetViewport()?.GetCamera3D();
            for (int i = _gates.Count - 1; i >= 0; i--)
            {
                var g = _gates[i];
                if (!IsInstanceValid(g.Building) || !IsInstanceValid(g.Node)) { _gates.RemoveAt(i); continue; }
                bool show = ForceShow || cam == null || Wants(g, cam.GlobalPosition);
                if (show == g.Shown) continue;
                g.Shown = show;
                g.Node.Visible = show;
            }
            for (int i = _casters.Count - 1; i >= 0; i--)
            {
                var c = _casters[i];
                if (!IsInstanceValid(c.Building)) { _casters.RemoveAt(i); continue; }
                bool on = ForceShadows || cam == null || PlanDistance(c, cam.GlobalPosition) <= ShadowRadius;
                if (on == c.On) continue;
                c.On = on;
                for (int k = 0; k < c.Pieces.Length; k++)
                    if (IsInstanceValid(c.Pieces[k])) c.Pieces[k].CastShadow = on ? c.Cast[k] : GeometryInstance3D.ShadowCastingSetting.Off;
            }
            if (_rooms.Count > 0) EvaluateRooms(cam);
        }

        /// <summary>How far the camera is from a building's plan, in plan (0 over it).</summary>
        static float PlanDistance(Casters c, Vector3 cam)
        {
            var l = c.Building.GlobalTransform.AffineInverse() * cam;
            float dx = Mathf.Max(0f, Mathf.Max(-l.X, l.X - c.Wx)), dz = Mathf.Max(0f, Mathf.Max(-l.Z, l.Z - c.Dz));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ---- the room gate ----------------------------------------------------------------------------------------------------

        /// <summary>UG_NOROOMGATE=1: the control -- Build groups nothing by room (every piece flat under its building, as before)
        /// and nothing is hidden by it.</summary>
        public static readonly bool RoomGateDisabled = System.Environment.GetEnvironmentVariable("UG_NOROOMGATE") == "1";
        /// <summary>Draw every gated room regardless (the in-process control, CullProbe's "roomgate").</summary>
        public static bool ForceRooms;
        /// <summary>A room's furniture is drawn while the camera is within this of the room's box, metres: the lamp fade's end
        /// (BuildingRuntime.LampFadeBegin + LampFadeLength). Past it the room's own lamp is out, so what is left of its inside
        /// through a window is lit by the sky alone -- and a lamp, inside the box, is never hidden while its light is on.
        /// (A field, not a constant, only so the culling probe can measure another radius in the same process.)</summary>
        public const float DefaultRoomRadius = BuildingRuntime.LampFadeBegin + BuildingRuntime.LampFadeLength;
        public static float RoomRadius = DefaultRoomRadius;
        /// <summary>...and hidden again only past RoomRadius plus this, so a camera standing at the threshold -- a head bob, a car
        /// idling -- cannot flicker a room in and out.</summary>
        public const float RoomHysteresis = 2f;

        /// <summary>A box in a building's frame.</summary>
        struct Box
        {
            public float X0, Y0, Z0, X1, Y1, Z1;
            public float DistanceSq(Vector3 p)
            {
                float dx = Mathf.Max(0f, Mathf.Max(X0 - p.X, p.X - X1)), dy = Mathf.Max(0f, Mathf.Max(Y0 - p.Y, p.Y - Y1));
                float dz = Mathf.Max(0f, Mathf.Max(Z0 - p.Z, p.Z - Z1));
                return dx * dx + dy * dy + dz * dz;
            }
            public static Box Union(Box a, Box b) => new Box { X0 = Mathf.Min(a.X0, b.X0), Y0 = Mathf.Min(a.Y0, b.Y0), Z0 = Mathf.Min(a.Z0, b.Z0),
                                                               X1 = Mathf.Max(a.X1, b.X1), Y1 = Mathf.Max(a.Y1, b.Y1), Z1 = Mathf.Max(a.Z1, b.Z1) };
        }

        sealed class GatedRoom { public Node3D Node; public Box[] Boxes; public bool Shown = true; }
        sealed class GatedStorey { public Node3D Node; public Box Box; public GatedRoom[] Rooms; public bool Shown = true; }
        sealed class RoomGate
        {
            public Node3D Building;
            public Box Box;                  // every gated room's, together: the broad phase
            public GatedStorey[] Storeys;
            public bool AllHidden;           // every storey hidden by the broad phase (so a far building costs one test a frame)
        }
        static readonly List<RoomGate> _rooms = new();

        /// <summary>Buildings with gated rooms; their rooms; how many are drawn right now.</summary>
        public static int RoomBuildings => _rooms.Count;
        /// <summary>...and of those, how many the broad phase let through on the last evaluation (the rest cost one test each).</summary>
        public static int RoomBuildingsNear { get { int n = 0; foreach (var b in _rooms) if (!b.AllHidden) n++; return n; } }
        public static int RoomCount { get { int n = 0; foreach (var b in _rooms) foreach (var s in b.Storeys) n += s.Rooms.Length; return n; } }
        public static int RoomsShown
        {
            get
            {
                int n = 0;
                foreach (var b in _rooms) foreach (var s in b.Storeys) foreach (var r in s.Rooms) if (IsInstanceValid(r.Node) && r.Node.IsVisibleInTree()) n++;
                return n;
            }
        }
        /// <summary>The room gate's own cost, for the probe: stopwatch ticks spent evaluating it, over how many evaluations, and
        /// how many room or storey nodes it has switched.</summary>
        public static long RoomTicks;
        public static int RoomEvals, RoomToggles;

        /// <summary>Gate each room's furniture (BuildingRuntime.GroupRooms' nodes) on the camera's distance to the room's box: its plan
        /// cells by the storeys it stands through, in the building's frame. A storey is tested first (the box round its rooms), and
        /// the building first of all, so a far building costs one test a frame and a far storey one more.
        ///
        /// MEASURED on the setback (CullProbe, UG_CULLGEN; gate on against ForceRooms, one process, same camera): at the street 30 m
        /// off its front the frame changed by 1 pixel while the visible pass went 1,936 -> 1,547 objects and the shadow pass
        /// 11,455 -> 8,659 -- the rooms lit by their own lamps (inside RoomRadius) are the ones a passer-by sees into, and those stay;
        /// in its rooms, 0 pixels; at 150 m, 0 pixels and the visible pass 7,581 -> 3,197. What it DOES change: 80 m up and 120 m
        /// off, 1,274 pixels (0.25%) -- desks and shelves seen from above through the glass of rooms 120 m away, which their own
        /// range (PropCull: the LOD table's, longer than the size rule's 64 m) still drew; the visible pass there 9,804 -> 4,008.
        /// At 64 m (PropCull's SMALL) instead of 40 the gate kept almost everything it now takes (street: 1,951 visible, 11,097
        /// shadow) for no fewer changed pixels.</summary>
        public static void AddRooms(Node3D building, BuildingRuntime.RoomGroups groups)
        {
            if (RoomGateDisabled || building == null || groups == null || groups.Storeys.Count == 0 || !building.IsInsideTree()) return;
            float c = BuildingGen.Cell, h = BuildingGen.Storey;
            var gate = new RoomGate { Building = building, Storeys = new GatedStorey[groups.Storeys.Count] };
            bool first = true;
            for (int i = 0; i < groups.Storeys.Count; i++)
            {
                var st = groups.Storeys[i];
                var rooms = new GatedRoom[st.Rooms.Count];
                Box sb = default; bool sFirst = true;
                for (int k = 0; k < st.Rooms.Count; k++)
                {
                    var (room, node) = st.Rooms[k];
                    var boxes = new List<Box>();
                    foreach (var (x0, z0, x1, z1) in room.Rects)
                    {
                        var b = new Box { X0 = x0 * c, Z0 = z0 * c, X1 = x1 * c, Z1 = z1 * c, Y0 = room.Floor * h, Y1 = (room.Floor + room.Span) * h };
                        boxes.Add(b);
                        sb = sFirst ? b : Box.Union(sb, b); sFirst = false;
                    }
                    rooms[k] = new GatedRoom { Node = node, Boxes = boxes.ToArray() };
                }
                gate.Storeys[i] = new GatedStorey { Node = st.Node, Box = sb, Rooms = rooms };
                gate.Box = first ? sb : Box.Union(gate.Box, sb); first = false;
            }
            _rooms.Add(gate);
            Hub(building).Evaluate();
        }

        /// <summary>Show or hide one gated node on its distance (squared) to the camera, with the hysteresis; true if it is shown.</summary>
        static bool GateNode(Node3D node, ref bool shown, float d2, bool force)
        {
            float r = shown ? RoomRadius + RoomHysteresis : RoomRadius;
            bool want = force || d2 <= r * r;
            if (want != shown)
            {
                shown = want;
                node.Visible = want;
                RoomToggles++;
            }
            return want;
        }

        void EvaluateRooms(Camera3D cam)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool force = ForceRooms || cam == null;
            float far = RoomRadius + RoomHysteresis;
            for (int i = _rooms.Count - 1; i >= 0; i--)
            {
                var g = _rooms[i];
                if (!IsInstanceValid(g.Building)) { _rooms.RemoveAt(i); continue; }
                var l = force ? Vector3.Zero : g.Building.GlobalTransform.AffineInverse() * cam.GlobalPosition;
                if (!force && g.Box.DistanceSq(l) > far * far)
                {
                    // the broad phase: nothing of it is near, every storey off -- once
                    if (g.AllHidden) continue;
                    foreach (var s in g.Storeys) if (s.Shown && IsInstanceValid(s.Node)) { s.Shown = false; s.Node.Visible = false; RoomToggles++; }
                    g.AllHidden = true;
                    continue;
                }
                g.AllHidden = false;
                foreach (var s in g.Storeys)
                {
                    if (!IsInstanceValid(s.Node)) continue;
                    if (!GateNode(s.Node, ref s.Shown, force ? 0f : s.Box.DistanceSq(l), force)) continue;
                    foreach (var r in s.Rooms)
                    {
                        if (!IsInstanceValid(r.Node)) continue;
                        float d2 = float.MaxValue;
                        if (!force) foreach (var b in r.Boxes) d2 = Mathf.Min(d2, b.DistanceSq(l));
                        GateNode(r.Node, ref r.Shown, d2, force);
                    }
                }
            }
            RoomTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            RoomEvals++;
        }

        /// <summary>Re-evaluate now (after ForceShow changes, or a camera jump that must not be drawn a frame late).</summary>
        public static void Refresh()
        {
            if (_hub != null && IsInstanceValid(_hub)) _hub.Evaluate();
        }

        public override void _ExitTree()
        {
            if (_hub == this) _hub = null;
        }
    }
}
