using System;
using System.Collections.Generic;

namespace UnturnedSim
{
    /// <summary>Which pre-barricade a building-editor opening spawns already-boarded: none, or one of the three
    /// window-barricade styles (master 2026-09-01). Engine-free enum so UnturnedSim stays Godot-free; the game maps
    /// it to the DeployableDef (Barricade.DefFor). Names match WindowBarricadeStyle so the save token is legible.</summary>
    public enum WallBarricade { None, Planks, Bars, Plate }

    /// <summary>A rectangular opening in a wall, in the wall's own 2D plane space.
    ///
    /// Everything is a rectangle plus a depth. There is no Door type and no Window type -- an archetype is a
    /// PRESET of these numbers, so the generator never has to know what a hole is "for". That is deliberate:
    /// scanning ~2400 retail openings established that a garage, a porch and an open interior span are
    /// geometrically identical, so any code that branches on intent is guessing.
    ///
    /// U runs along the wall from its start, V runs up from its base. Depth is centred on the wall's
    /// mid-plane: Depth >= wall thickness is a through-hole, less is a recess with material behind it.</summary>
    public struct WallOpening
    {
        public float U, V;          // lower-left corner in wall space
        public float Width, Height;
        /// <summary>NOT IMPLEMENTED YET: nothing reads this, so every opening is a through-hole and the 999
        /// written to each save line is ballast. Kept because the recess case is real (a blind alcove, a
        /// window seat) and the field costs a float; do not document it as working until Rebuild honours it.</summary>
        public float Depth;         // intended: centred on the wall mid-plane; >= thickness means it pierces
        public int Archetype;       // index into the preset table; presentation only, never read by the geometry

        // ---- glazing -------------------------------------------------------------------------------
        // A hole can be FILLED with glass. This lives on the opening rather than on a separate pane list
        // because the pane's existence, size and position are all consequences of the hole -- a second list
        // would have to be kept in step through move, resize, reparent, delete and undo, and would drift.
        // The pane itself (game/GlassPane.cs) persists nothing; these are the fields it is rebuilt from.

        /// <summary>Fill this opening with glass. OFF by default -- strawberry_cow: "toggleable on/off so not
        /// every opening is a window", and a door is an opening too.</summary>
        public bool Glazed;
        /// <summary>Packed 0xRRGGBB glass tint. An int rather than a colour type so UnturnedSim stays
        /// engine-free and the save line stays one token. 0 means "unset" -> GlassPane.DefaultHue.</summary>
        public int GlassTint;
        public float GlassHp;           // <= 0 means "unset" -> the pane's own default of 1 (retail glass is one shot)
        public bool GlassIndestructible;
        /// <summary>Already smashed. Kept so a broken window survives save/load and undo as broken, instead of
        /// silently repairing itself the next time the wall rebuilds.</summary>
        public bool GlassBroken;

        /// <summary>Is there glass to build right now? Glazed but broken is a hole, which is the point.</summary>
        public bool HasGlass => Glazed && !GlassBroken;

        // ---- a DOOR in the hole ----------------------------------------------------------------------
        // strawberry_cow 2026-08-09: "what i want is to have these doors as things i can enable on relevant
        // openings". Same shape as the glazing above and for the same reason: a door's existence, size and
        // position are all consequences of its hole, so the hole owns it and there is no second list to keep
        // in step through move / resize / reparent / delete / undo.
        //
        // The prop NAME rather than an index: the door catalog is keyed by name, ids have already collided
        // once tonight, and a name survives the table being reordered. Empty = no door.
        public string DoorProp;
        /// <summary>Persisted so a door you left open is still open after a save, a load or an undo -- the
        /// same reason GlassBroken exists rather than being recomputed.</summary>
        public bool DoorOpen;

        public bool HasDoor => !string.IsNullOrEmpty(DoorProp);

        // ---- a PRE-BARRICADE in the hole (master 2026-09-01) --------------------------------------------
        // Same optional-per-opening shape as glass + door (the opening owns it, no second list to keep in step):
        // which window-barricade style, if any, this opening spawns already-boarded. Placed on the OUTSIDE (+Z)
        // face by WallSurface.RebuildBarricades (and the baked marker). A pre-barricaded DOOR reads as barricaded.
        public WallBarricade Barricade;
        public bool HasBarricade => Barricade != WallBarricade.None;

        public float U1 => U + Width;
        public float V1 => V + Height;

        public WallOpening(float u, float v, float w, float h, float depth = 999f, int archetype = 0)
        { U = u; V = v; Width = w; Height = h; Depth = depth; Archetype = archetype;
          Glazed = false; GlassTint = 0; GlassHp = 0f; GlassIndestructible = false; GlassBroken = false;
          DoorProp = null; DoorOpen = false; Barricade = WallBarricade.None; }

        /// <summary>This opening moved/resized to (u,v,w,h), keeping EVERYTHING ELSE.
        ///
        /// Use this instead of `new WallOpening(...)` when deriving one opening from another. The constructor
        /// takes four of the eleven fields, so rebuilding through it silently drops the rest -- which is how a
        /// dragged window would lose its glazing, its tint and its broken flag, with no error and no failing
        /// test. A struct copy keeps fields that do not exist yet, so this stays correct as the type grows.</summary>
        public WallOpening MovedTo(float u, float v, float w, float h)
        {
            var r = this;
            r.U = u; r.V = v; r.Width = w; r.Height = h;
            return r;
        }
    }

    /// <summary>An axis-aligned solid region of wall left over after the openings are removed. Wall space,
    /// same axes as WallOpening. These become both the render boxes and the collision boxes -- which is the
    /// whole reason the wall is generated rather than boolean'd: a CSG hole needs collision rebuilt
    /// separately and can disagree with what you see.</summary>
    public struct WallSolid
    {
        public float U0, V0, U1, V1;
        public WallSolid(float u0, float v0, float u1, float v1) { U0 = u0; V0 = v0; U1 = u1; V1 = v1; }
        public float Width => U1 - U0;
        public float Height => V1 - V0;
        public float Area => Width * Height;
    }

    /// <summary>Wall geometry: turn (wall size, openings) into the solid regions around the holes.
    ///
    /// Pure and engine-free so the hard part is L0-testable without a running engine. The Godot side only
    /// materialises what these functions decide.</summary>
    public static class WallOpenings
    {
        public const float Eps = 1e-4f;

        /// <summary>Partition a wall of W x H minus its openings into disjoint axis-aligned boxes.
        ///
        /// Vertical-strip sweep: cut a strip at every opening edge, so within a strip an opening either spans
        /// its full width or is absent -- never partial. Take the union of the covered V-intervals in that
        /// strip, complement it against [0,H], and emit a box per surviving gap. Then fuse horizontally
        /// adjacent strips whose gap lists match, so a lintel stays ONE box instead of a row of slices.
        ///
        /// Union rather than per-opening subtraction is load-bearing: two overlapping openings must remove
        /// their union once, not twice.</summary>
        public static List<WallSolid> Solids(float wallWidth, float wallHeight, IReadOnlyList<WallOpening> openings)
        {
            var result = new List<WallSolid>();
            if (wallWidth <= Eps || wallHeight <= Eps) return result;

            // 1. clamp into the wall and drop anything degenerate. Hand-edited saves and openings larger than
            //    the wall get neutralised here rather than rejected -- the generator never throws.
            var rects = new List<WallOpening>();
            if (openings != null)
            {
                foreach (var o in openings)
                {
                    float u0 = Math.Max(0f, Math.Min(o.U, o.U1));
                    float u1 = Math.Min(wallWidth, Math.Max(o.U, o.U1));
                    float v0 = Math.Max(0f, Math.Min(o.V, o.V1));
                    float v1 = Math.Min(wallHeight, Math.Max(o.V, o.V1));
                    if (u1 - u0 > Eps && v1 - v0 > Eps) rects.Add(new WallOpening(u0, v0, u1 - u0, v1 - v0));
                }
            }

            // 2. strip boundaries at every opening edge
            var xs = new List<float> { 0f, wallWidth };
            foreach (var o in rects) { xs.Add(o.U); xs.Add(o.U1); }
            xs.Sort();
            var cuts = new List<float>();
            foreach (var x in xs)
                if (cuts.Count == 0 || x - cuts[cuts.Count - 1] > Eps) cuts.Add(x);

            // 3. per strip: complement the union of covered V-intervals
            var stripGaps = new List<List<(float A, float B)>>();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                float a = cuts[i], b = cuts[i + 1];
                if (b - a <= Eps) { stripGaps.Add(null); continue; }
                float mid = (a + b) * 0.5f;

                var covered = new List<(float A, float B)>();
                foreach (var o in rects)
                    if (o.U - Eps <= mid && mid <= o.U1 + Eps) covered.Add((o.V, o.V1));
                covered.Sort((p, q) => p.A.CompareTo(q.A));

                var gaps = new List<(float A, float B)>();
                float cursor = 0f;
                foreach (var (ca, cb) in covered)
                {
                    if (ca - cursor > Eps) gaps.Add((cursor, ca));
                    cursor = Math.Max(cursor, cb);
                }
                if (wallHeight - cursor > Eps) gaps.Add((cursor, wallHeight));
                stripGaps.Add(gaps);
            }

            // 4. fuse neighbouring strips with identical gap lists, then emit
            int s = 0;
            while (s < stripGaps.Count)
            {
                var gaps = stripGaps[s];
                if (gaps == null || gaps.Count == 0) { s++; continue; }
                int e = s;
                while (e + 1 < stripGaps.Count && SameGaps(gaps, stripGaps[e + 1])) e++;
                float u0 = cuts[s], u1 = cuts[e + 1];
                foreach (var (a, b) in gaps) result.Add(new WallSolid(u0, a, u1, b));
                s = e + 1;
            }
            return result;
        }

        static bool SameGaps(List<(float A, float B)> x, List<(float A, float B)> y)
        {
            if (y == null || x.Count != y.Count) return false;
            for (int i = 0; i < x.Count; i++)
                if (Math.Abs(x[i].A - y[i].A) > Eps || Math.Abs(x[i].B - y[i].B) > Eps) return false;
            return true;
        }

        /// <summary>Slide an opening to a target position, stopping at the wall edges and at its siblings.
        /// NEVER rejects -- an out-of-bounds drag lands flush against the limit and stays attached to the
        /// cursor. A refusing editor makes you undo your way out of a mistake; a clamping one has no mistake
        /// to make.
        ///
        /// Resolved per axis, U first using the opening's current V-extent, then V using the clamped U. Only
        /// siblings that actually overlap on the other axis can block, so a window at a different height
        /// never stops you sliding past it.</summary>
        /// <param name="fromU">Where the opening currently IS, for deciding which side of it a sibling is
        /// on. Pass it when clamping a DRAG. Deciding from the proposed position instead lets an opening
        /// teleport: drag it rightward far enough that its centre passes the blocker's centre and the
        /// blocker reclassifies as being on the LEFT, so the lower bound jumps to the blocker's far edge and
        /// the opening reappears on the other side of it. It never overlaps at any point, so an
        /// overlap check does not catch it -- measured, a 3 m opening dragged through a neighbour landed at
        /// u 9..12 instead of stopping at 6.</param>
        public static WallOpening Clamp(WallOpening o, float wallWidth, float wallHeight,
                                        IReadOnlyList<WallOpening> siblings, int skipIndex = -1,
                                        float fromU = float.NaN, float fromV = float.NaN)
        {
            float w = Math.Min(o.Width, wallWidth), h = Math.Min(o.Height, wallHeight);
            float sideU = float.IsNaN(fromU) ? o.U + w * 0.5f : fromU;
            float sideV = float.IsNaN(fromV) ? o.V + h * 0.5f : fromV;

            float lo = 0f, hi = wallWidth - w;
            if (siblings != null)
                for (int i = 0; i < siblings.Count; i++)
                {
                    if (i == skipIndex) continue;
                    var s2 = siblings[i];
                    if (s2.V1 <= o.V + Eps || s2.V >= o.V + h - Eps) continue;   // no V overlap -> cannot block in U
                    // Which SIDE a sibling blocks from is decided by comparing centres, not by testing whether
                    // the proposed rect already clears it. The proposed rect is usually mid-overlap -- that is
                    // the whole reason clamp is being called -- so an "is it left of me" test on the proposal
                    // matches neither branch and the opening slides straight through its neighbour.
                    if (s2.U + s2.Width * 0.5f < sideU) lo = Math.Max(lo, s2.U1);            // blocker on the left
                    else hi = Math.Min(hi, s2.U - w);                                        // blocker on the right
                }
            float u = hi < lo ? lo : Math.Max(lo, Math.Min(o.U, hi));

            float vlo = 0f, vhi = wallHeight - h;
            if (siblings != null)
                for (int i = 0; i < siblings.Count; i++)
                {
                    if (i == skipIndex) continue;
                    var s2 = siblings[i];
                    if (s2.U1 <= u + Eps || s2.U >= u + w - Eps) continue;       // no U overlap at the clamped U
                    if (s2.V + s2.Height * 0.5f < sideV) vlo = Math.Max(vlo, s2.V1);            // blocker below
                    else vhi = Math.Min(vhi, s2.V - h);                                        // blocker above
                }
            float v = vhi < vlo ? vlo : Math.Max(vlo, Math.Min(o.V, vhi));

            return o.MovedTo(u, v, w, h);   // NOT `new WallOpening(...)` -- that drops glazing. See MovedTo.
        }

        /// <summary>Snap one world coordinate onto the placement grid.</summary>
        public static float SnapGrid(float v) => (float)Math.Round(v / GridStep) * GridStep;

        /// <summary>Does this rect overlap any sibling? Used to REFUSE a move rather than relocate it.
        ///
        /// Clamp's job is to find the nearest legal spot, and when a drag leaves no legal spot at all its
        /// two-sided fallback parks the opening on top of a neighbour. That reads as the editor shoving
        /// things around. A caller that would rather nothing happened asks this first.</summary>
        public static bool Overlaps(WallOpening o, IReadOnlyList<WallOpening> siblings, int skipIndex = -1)
        {
            if (siblings == null) return false;
            for (int i = 0; i < siblings.Count; i++)
            {
                if (i == skipIndex) continue;
                var s = siblings[i];
                if (s.U1 <= o.U + Eps || s.U >= o.U1 - Eps) continue;
                if (s.V1 <= o.V + Eps || s.V >= o.V1 - Eps) continue;
                return true;
            }
            return false;
        }

        /// <summary>Snap a value to the nearest target within tolerance, else return it unchanged.
        /// The caller passes a tolerance derived from SCREEN pixels, not world units -- a fixed world
        /// tolerance feels sticky zoomed out and useless zoomed in.</summary>
        public static float Snap(float value, IReadOnlyList<float> targets, float tolerance)
        {
            if (targets == null || tolerance <= 0f) return value;
            float best = value, bestD = tolerance;
            foreach (var t in targets)
            {
                float d = Math.Abs(value - t);
                if (d < bestD) { bestD = d; best = t; }
            }
            return best;
        }

        // ---- measured retail defaults ---------------------------------------------------------------
        // From ~2400 openings across ~35 retail building meshes. These are SNAP TARGETS and starting
        // values, not law -- the editor exposes them and you find out what feels right by dragging.

        public static readonly float[] HeadHeights = { 4.25f, 3.75f, 3.85f };   // opening tops above the floor
        public static readonly float[] SillHeights = { 0f, 1.00f, 0.88f };      // opening bottoms
        public static readonly float[] Widths = { 2.5f, 3.0f, 4.0f, 5.5f, 7.0f, 8.0f, 11.0f };
        public const float StoreyPitch = 4.75f;      // 4.25 opening + 0.50 slab, confirmed off apartment sill series
        public const float DoorHeight = 4.25f;       // equals retail WALL_HEIGHT
        public const float WindowHeight = 2.75f;
        public const float WindowSill = 1.00f;
        public const float LatticeStep = 3.0f;       // wall lengths snap to multiples of this (retail half-edge)
        /// <summary>Wall ENDPOINTS snap to this in world X/Z. Snapping only the length is what leaves the
        /// "weird little gaps": two walls can each be an exact 3 m and still not meet, because nothing pins
        /// where they start. Same step as the length, so a run of walls always closes.</summary>
        public const float GridStep = LatticeStep;
        // Measured off the loose panels of House_00/03, Apartment_0, Office_0: a reveal strip spans the wall
        // thickness by definition, and 0.70 dominates every building (74/212/145/184 panels). 0.50 is the
        // second cluster -- interior partitions are a thinner wall. 0.20 is the trim profile.
        public const float DefaultThickness = 0.70f;   // exterior wall
        public const float InteriorThickness = 0.50f;  // partitions
        public const float TrimProfile = 0.20f;        // frame bar width, retail-measured
        public const float MinOpening = 0.5f;
        /// <summary>How far a foundation reaches below the floor. Every one of the 52 sampled retail buildings
        /// extends below ground, and it is a hollow SKIRT rather than a solid block -- hundreds of square
        /// metres of side face down there and essentially no bottom. Depth is bimodal, 5.00 or 6.00, median 6.
        /// So a foundation is a wall hanging under a wall, and needs no geometry of its own.</summary>
        public const float FoundationDepth = 6.0f;

        // ---- STAIRS -------------------------------------------------------------------------------------
        // A staircase is not a surface kind the mesh builder understands -- Rebuild() never reads Kind -- it is
        // a GENERATOR that emits one flat tread per step, the same way a room emits four walls and an opening
        // becomes four boxes. Everything here is derived so the run lands EXACTLY flush on the next storey:
        // change DoorHeight and the stairs follow instead of ending half a step short.
        //
        // StairRiseTarget is a COMFORT target, not a divisor: the step count is round(storey / target) and the
        // actual rise is storey / count, so the flight always closes on the floor above. 0.38 puts a step at
        // roughly 18 cm against this kit's scale (DoorHeight 4.25 is a door, so a unit is about half a metre).
        public const float StairRiseTarget = 0.38f;
        // Default run from a target PITCH rather than a picked number: tan(32 deg) = 0.62 is a domestic
        // staircase, so run = DoorHeight / 0.62 = 6.85, which snaps to two lattice steps. Snapping is what
        // keeps a flight interoperable with the walls around it.
        public const float StairPitchTangent = 0.62f;
        public const float StairTreadThickness = 0.20f;   // matches TrimProfile: the kit's thin-plate dimension
        public const float StairDefaultWidth = 3.0f;      // one lattice, and retail's commonest door width

        /// <summary>Steps in a flight climbing <paramref name="rise"/>. At least two -- one "step" is a kerb,
        /// not a staircase -- and derived so rise/count lands on the floor above exactly.</summary>
        public static int StairSteps(float rise)
            => System.Math.Max(2, (int)System.Math.Round(rise / StairRiseTarget));

        /// <summary>Rise of ONE step in a flight climbing <paramref name="rise"/>. This is the number the
        /// flush landing depends on, so it lives here where a test can reach it -- asserting it from a value
        /// the test computed itself would be a tautology that passes against any generator at all.</summary>
        public static float StairStepRise(float rise) => rise / StairSteps(rise);

        /// <summary>Default horizontal run for a flight climbing <paramref name="rise"/>, snapped to the
        /// lattice so it lines up with the walls it sits between.</summary>
        public static float StairDefaultRun(float rise)
            => System.Math.Max(LatticeStep,
                   (float)System.Math.Round(rise / StairPitchTangent / LatticeStep) * LatticeStep);
    }
}
