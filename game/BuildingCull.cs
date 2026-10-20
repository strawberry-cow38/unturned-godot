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
    /// did); ForceShadows the in-process one.</summary>
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
        }

        /// <summary>How far the camera is from a building's plan, in plan (0 over it).</summary>
        static float PlanDistance(Casters c, Vector3 cam)
        {
            var l = c.Building.GlobalTransform.AffineInverse() * cam;
            float dx = Mathf.Max(0f, Mathf.Max(-l.X, l.X - c.Wx)), dz = Mathf.Max(0f, Mathf.Max(-l.Z, l.Z - c.Dz));
            return Mathf.Sqrt(dx * dx + dz * dz);
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
