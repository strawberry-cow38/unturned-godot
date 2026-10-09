using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>The map-tool showcase: one STATION per mapmaker tool, laid out on a grid with a sign over each.
    ///
    /// Master 2026-10-06: "i want you to make a map thats a map tool showcase. when we add new mapmaker tools we
    /// show them off/experiment there."
    ///
    /// ⭐ SO THE POINT OF THIS FILE IS THAT ADDING A STATION IS ONE ENTRY. The thing master asked for is not a map,
    /// it is a HABIT -- somewhere the next tool gets demonstrated the day it lands. A hand-placed demo map would rot
    /// the first time someone had to re-author it; a table of stations laid out automatically means the next tool
    /// is a `new Station(...)` and nothing else moves. If you are here to add one, go to Stations() and copy the
    /// nearest neighbour.
    ///
    /// ⭐ AND IT IS A REAL, EDITABLE MAP, not a diorama. It authors into the live Terrain / RoadField /
    /// EditorObjects / PowerLineField, so everything here can be picked up and changed with the very tools it is
    /// demonstrating -- which is the "experiment there" half of the ask. It saves under its own map name like any
    /// other custom map, so experiments do not clobber PEI.</summary>
    public static class MapShowcase
    {
        public const string MapName = "Showcase";

        /// <summary>Metres between station centres. Big enough that a tool's demo cannot be mistaken for its
        /// neighbour's, small enough to fly between them in a few seconds.</summary>
        public const float StationSpacing = 140f;
        public const int StationsPerRow = 3;

        public sealed class Station
        {
            public string Title;
            public string Blurb;      // the one line on the sign: what the tool does, and the key that opens it
            public System.Action<Context> Build;
            public Station(string title, string blurb, System.Action<Context> build) { Title = title; Blurb = blurb; Build = build; }
        }

        /// <summary>What a station is allowed to author into, plus where it sits. `Origin` is the station's centre
        /// on the ground -- everything a station builds should be placed RELATIVE to it, so stations stay
        /// independent of the layout and re-ordering the table cannot break one.</summary>
        public sealed class Context
        {
            public Vector3 Origin;
            public Terrain Terr;
            public RoadField Roads;
            public EditorObjects Objects;
            public PowerLineField PowerLines;
            public Node3D Root;
        }

        /// <summary>⭐ THE TABLE. One entry per mapmaker tool.
        ///
        /// ⭐ NEWEST TOOL FIRST. Station 1 is where both the opening camera and the player spawn land, so whatever
        /// just shipped is the thing you are looking at without flying anywhere -- which is the entire point of the
        /// map. Master, after asking for it: "i just wanted to see the wired up power lines in game". Put the new
        /// one at the top of this list and that is handled.</summary>
        public static List<Station> Stations() => new()
        {
            new Station("FENCE ROAD", "Click a path — straight, or curved through 3+ points · Shift+F", c =>
            {
                // A CURVE, because that is the thing the tool does that placing the prop by hand cannot. A
                // straight demo would be indistinguishable from dragging the same prop along a line.
                if (c.Objects == null) return;
                EditorFenceRoad.LayPath(c.Objects, c.Terr, new List<Vector3>
                {
                    // ⚠ A GENTLE arc: a rigid 16 m segment cannot follow a bend under MinBendRadius, and the
                    // first version of this station used one tight enough (38 m) to trip the tool's own
                    // warning -- a showcase that demonstrates the thing being warned about.
                    c.Origin + new Vector3(-82f, 0f, -6f),
                    c.Origin + new Vector3(  0f, 0f, 14f),
                    c.Origin + new Vector3( 82f, 0f, -6f),
                }, false, false, null, null);
                // ...and a BROKEN run set back behind it, so the wrecked variant is visible beside the intact
                // one rather than being a mode you have to know about.
                EditorFenceRoad.LayPath(c.Objects, c.Terr, new List<Vector3>
                {
                    c.Origin + new Vector3(-48f, 0f, 40f),
                    c.Origin + new Vector3( 48f, 0f, 40f),
                }, true, false, null, null);
            }),

            new Station("POWER LINES", "Pick a pole, pick the next — four wires string themselves · Shift+P", c =>
            {
                // A RUN of poles, not a pair: the tool's whole point is chaining, and a single span would not show
                // that the wires stay pinned across several of them. The slight Z stagger keeps it from reading as
                // a technical drawing.
                if (c.PowerLines == null) return;
                var made = new List<int>();
                for (int i = 0; i < 5; i++)
                {
                    var pos = c.Origin + new Vector3(-56f + i * 28f, 0f, (i % 2 == 0) ? -3f : 3f);
                    if (c.Terr != null) pos.Y = c.Terr.SampleHeight(pos.X, pos.Z);
                    float ey = 90f + i * 4f;   // not parallel, so crossed wires would be obvious
                    var rot = new Basis(new Vector3(0, 1, 0), Mathf.DegToRad(180f - ey))
                            * new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(270f));
                    var xf = new Transform3D(rot, pos);
                    c.Objects?.Place(PowerLineField.PoleMesh, pos, rot);
                    made.Add(c.PowerLines.AddPole(xf));
                }
                for (int i = 0; i + 1 < made.Count; i++) c.PowerLines.Connect(made[i], made[i + 1], out _);
                c.PowerLines.Rebuild();
            }),

            new Station("TERRAIN — SCULPT", "Raise / Lower / Smooth brushes · Terrain tab, M cycles the brush", c =>
            {
                // A hill and a crater side by side, because the interesting thing about the brush is that it does
                // both and the falloff is the same curve either way.
                for (int i = 0; i < 10; i++) c.Terr?.EditHeight(c.Origin.X - 22f, c.Origin.Z, 20f, 1.4f);
                for (int i = 0; i < 8; i++) c.Terr?.EditHeight(c.Origin.X + 22f, c.Origin.Z, 16f, -1.2f);
            }),

            new Station("TERRAIN — RAMP", "Two-click graded corridor · Terrain tab, Ramp", c =>
            {
                // A ramp needs something to climb, so it gets its own mound first -- otherwise it grades flat
                // ground into flat ground and demonstrates nothing.
                for (int i = 0; i < 10; i++) c.Terr?.EditHeight(c.Origin.X + 26f, c.Origin.Z, 18f, 1.5f);
                c.Terr?.EditRamp(c.Origin + new Vector3(-26f, 0f, 0f), c.Origin + new Vector3(26f, 0f, 0f), 7f);
            }),

            new Station("ROADS", "Draw a road or rail along a spline · R, Shift+R for the node tool", c =>
            {
                // A deliberate S-bend: a straight road would look the same whether the spline worked or not.
                var pts = new List<Vector3>
                {
                    c.Origin + new Vector3(-50f, 0f, -18f),
                    c.Origin + new Vector3(-16f, 0f,  16f),
                    c.Origin + new Vector3( 16f, 0f, -16f),
                    c.Origin + new Vector3( 50f, 0f,  18f),
                };
                c.Roads?.AddRoadFromPolyline(pts);
            }),

            new Station("OBJECTS", "Place, gizmo-move, copy and delete props · Level tab", c =>
            {
                // A handful of ordinary map props, arranged so the gizmo has something to be used ON.
                string[] props = { "Fire_Hydrant_0", "Street_Light_0", "Bench_Wood_0", "Barrel_0" };
                for (int i = 0; i < props.Length; i++)
                {
                    var pos = c.Origin + new Vector3(-24f + i * 16f, 0f, 0f);
                    if (c.Terr != null) pos.Y = c.Terr.SampleHeight(pos.X, pos.Z);
                    var rot = new Basis(new Vector3(0, 1, 0), Mathf.DegToRad(i * 35f))
                            * new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(270f));
                    c.Objects?.Place(props[i], pos, rot);
                }
            }),
        };

        /// <summary>Author every station into a freshly created map. Returns how many were built.
        ///
        /// ⚠ Terrain colliders are flushed ONCE at the end rather than per station: every sculpt marks chunks dirty
        /// and rebuilding a collider per brush stroke is the slow path the editor itself avoids.</summary>
        public static int Author(Terrain terr, RoadField roads, EditorObjects objects, PowerLineField powerLines, Node3D root, EditorSpawns spawns = null)
        {
            var stations = Stations();
            for (int i = 0; i < stations.Count; i++)
            {
                int col = i % StationsPerRow, row = i / StationsPerRow;
                var origin = new Vector3(col * StationSpacing, 0f, row * StationSpacing);
                if (terr != null) origin.Y = terr.SampleHeight(origin.X, origin.Z);

                var ctx = new Context { Origin = origin, Terr = terr, Roads = roads, Objects = objects, PowerLines = powerLines, Root = root };
                try { stations[i].Build(ctx); }
                catch (System.Exception e) { Log.Err($"[showcase] station '{stations[i].Title}' failed: {e.Message}"); }

                AddSign(root, origin, stations[i]);
            }
            terr?.FlushColliders();

            // ⭐ A PLAYER SPAWN, because the showcase is meant to be PLAYED as well as edited and a map with no
            // spawn drops you at whatever the fallback is -- which, the first time this was tested through the
            // Workshop's Play button, was a view of nothing but sky. Put it in front of the first station, facing
            // it, so playing the showcase starts where opening it does.
            if (spawns != null)
            {
                var at = new Vector3(0f, 0f, 60f);
                if (terr != null) at.Y = terr.SampleHeight(at.X, at.Z);
                spawns.SetCategoryTo(0);   // ECategory.Player
                spawns.AddSpawn(at, 180f); // facing -Z, toward station 1
            }

            Log.Print($"[showcase] authored {stations.Count} station(s)");
            return stations.Count;
        }

        /// <summary>Where to stand when the showcase opens: back from the grid and above it, looking down the
        /// rows so every station's sign is in frame at once. A map whose purpose is "look at what the tools do"
        /// should not open pointing at empty ground.</summary>
        public static void OpenView(EditorCamera cam, int stationCount)
        {
            // ⭐ IN FRONT OF THE FIRST STATION, not a wide shot of the whole grid. The first version pulled back
            // far enough to frame every station at once, which put the camera ~300 m out -- and at that range the
            // world's distance fog washes the whole view into an orange haze, so the "overview" showed LESS than
            // standing at one station does. Open on something legible and let the mapper fly to the rest.
            //
            // ⚠ YAW 0, NOT 180: a Godot camera at yaw 0 looks down -Z, and this stands on the +Z side of the
            // station, so 180 points it at the empty horizon behind. That is exactly the blank frame it gave the
            // first time; the sign of a look direction is not worth guessing at twice.
            if (cam == null) return;
            // ⭐ UG_CAMPOS / UG_CAMLOOK OVERRIDE IT, the same pair WorldBuilder's aerial camera already takes.
            // The showcase exists to be LOOKED AT, so "open on a different station, close enough to see it" is
            // the one thing a screenshot of it always needs -- and the default pose below frames station 1 from
            // 88 m, which is fine for a person who can fly and useless for a single captured frame.
            var cp = System.Environment.GetEnvironmentVariable("UG_CAMPOS");
            if (!string.IsNullOrEmpty(cp))
            {
                var t = cp.Split(',');
                var look = (System.Environment.GetEnvironmentVariable("UG_CAMLOOK") ?? "0,0,0").Split(',');
                if (t.Length == 3 && look.Length == 3)
                {
                    float F(string[] a, int i) => float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
                    var at = new Vector3(F(t, 0), F(t, 1), F(t, 2));
                    var to = new Vector3(F(look, 0), F(look, 1), F(look, 2));
                    var d = (to - at);
                    float yaw = Mathf.RadToDeg(Mathf.Atan2(-d.X, -d.Z));
                    float pitch = Mathf.RadToDeg(Mathf.Atan2(d.Y, new Vector2(d.X, d.Z).Length()));
                    cam.SetPose(at, yaw, pitch);
                    Log.Print($"[showcase] UG_CAMPOS {at} -> {to} (yaw {yaw:0.#}, pitch {pitch:0.#})");
                    return;
                }
            }
            cam.SetPose(new Vector3(0f, 26f, 88f), 0f, -11f);
        }

        /// <summary>A readable sign over each station. Label3D rather than a prop with a texture: the text is the
        /// documentation, it has to be editable in one place, and it must stay legible from the air where you will
        /// actually be flying.</summary>
        static void AddSign(Node3D root, Vector3 origin, Station st)
        {
            if (root == null) return;
            var title = new Label3D
            {
                Text = st.Title,
                FontSize = 128,
                PixelSize = 0.012f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = new Color(1f, 0.92f, 0.45f),
                OutlineSize = 24,
                NoDepthTest = false,
                Position = origin + new Vector3(0f, 18f, 0f),
            };
            root.AddChild(title);
            var blurb = new Label3D
            {
                Text = st.Blurb,
                FontSize = 64,
                PixelSize = 0.012f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = new Color(0.85f, 0.92f, 1f),
                OutlineSize = 16,
                Position = origin + new Vector3(0f, 15.6f, 0f),
            };
            root.AddChild(blurb);
        }
    }
}
