using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedSim
{
    /// <summary>What a whole building is FOR. One per building, and the volume it applies to is DERIVED from
    /// the walls rather than drawn -- strawberry_cow: "volume(s) that perfectly encompass the building, not
    /// stretching past". Deriving it is not a shortcut: a hand-drawn box goes stale the moment a wall moves,
    /// and "perfectly encompasses" is a property nothing but the walls can answer.
    ///
    /// Apartment: a block of flats (strawberry: "apartments should function differently from houses, no staircase
    /// inside the apartment, shared corridor and staircases"). On the END, as RoomKind's new members are: kinds persist
    /// by NAME, but nothing should renumber.
    ///
    /// Strip: a strip mall, a one-storey row of shops each its own tenant (strawberry, 2026-10-19, on the strip research:
    /// "and yes keep working on this") -- BuildingGenStrip.cs.
    ///
    /// Tower: a tall office building on one of a few researched plans -- a main-street office block, a setback skyscraper, an
    /// International Style slab -- at its full height (strawberry, 2026-10-19: "go for the full height. go for main street office,
    /// setback, and international for now") -- BuildingGenTower.cs.</summary>
    public enum BuildingKind { Residential, Commercial, Industrial, Misc, Apartment, Strip, Tower }

    /// <summary>What one ROOM is for. Extending this is a one-line change and safe: designations persist by
    /// NAME, never by ordinal, so adding or reordering members cannot silently re-label existing saves.
    ///
    /// An enum rather than a free string, deliberately. A free string rots -- "Bedroom", "bedroom" and
    /// "bed room" become three kinds nobody notices until something keys off them -- and this list is going
    /// to be read by the furniture rules later, which is exactly the code a typo breaks silently.</summary>
    public enum RoomKind
    {
        Unassigned,
        Bedroom, Bathroom, Kitchen, LivingRoom, DiningRoom,
        Hall, Stairwell, Closet,
        Garage, Workshop, Storage,
        Office, Shop, Warehouse,
        // (new kinds go on the END: saves hold names, but nothing should renumber)
        BreakRoom,
        // a vertical void through the storeys it stands in (GenRoom.Span): a lift's, open onto its lift hall on each storey it
        // serves (strawberry, 2026-10-19: "leave elevator shafts as empty shafts for now"), or a pipe chase's, sealed. Nothing
        // is furnished in one and nothing floors it but its pit; you can fall down it.
        Shaft,
    }

    /// <summary>A room designation as it is STORED: a kind plus a point inside the room it labels.
    ///
    /// THE ANCHOR IS THE WHOLE DESIGN. Rooms are DERIVED -- RoomEnclosure recomputes them from the walls
    /// every time -- so there is no stable room identity to hang an id on. Move one wall and the room a
    /// designation was attached to stops existing. Storing a point INSIDE the room instead lets the label
    /// re-find its room on load by asking which enclosure now contains that point, which survives nudging a
    /// wall, adding a partition elsewhere, or renumbering.
    ///
    /// It also fails LEGIBLY: delete the room and its anchor matches nothing, so the designation is visibly
    /// orphaned rather than silently reassigned to whatever room inherited the index.</summary>
    public struct RoomDesignation
    {
        public RoomKind Kind;
        /// <summary>A point inside the room, in the same plan space RoomEnclosure works in.</summary>
        public float X, Z;
        /// <summary>Which storey: 0 the ground. The plan point alone cannot say, and a generated building has a
        /// bedroom right over its living room -- two anchors at the same X, Z.</summary>
        public int Floor;
        /// <summary>The room's heating and ventilation (round 12, strawberry: "rooms that get vents must be marked internally
        /// as HVAC rooms, so room temperature and then player temperature can hook into it later"): null for a room with none.
        /// Saved with the label, on its line.</summary>
        public RoomHvac Hvac;
        /// <summary>The room's light switch, outlets and ceiling lights (round 12, strawberry: "light switches should function
        /// to switch on/off the ceiling lights within a room"; "the outlets should act as power io outlets 1500w each"):
        /// null for a room with neither fixture. Saved with the label, on its line -- the switch's state with it.</summary>
        public RoomElectrical Electrical;

        public RoomDesignation(RoomKind kind, float x, float z, int floor = 0, RoomHvac hvac = null, RoomElectrical electrical = null)
        { Kind = kind; X = x; Z = z; Floor = floor; Hvac = hvac; Electrical = electrical; }
    }

    /// <summary>Which electrical fixture: a light switch by the door, an outlet on the wall.</summary>
    public enum ElectricalKind { Switch, Outlet }

    /// <summary>One switch or outlet: its kind; its Id -- its place among the building's switches and outlets, which is their
    /// order among its pieces (BuildingGenResult.Electrical, and the Light_Switch_0/Power_Outlet_0 props of a saved layout in
    /// the order they were saved); where its prop's origin stands and which way it is turned; and, for an outlet, what it
    /// supplies (BuildingGen.OutletWatts).</summary>
    public readonly record struct ElectricalFixture(ElectricalKind Kind, int Id, float X, float Y, float Z, float Yaw, float Watts = 0f);

    /// <summary>A room's electrical record: its switches and outlets apart, the ceiling lights its switches work (each by its
    /// place among the building's ceiling-light props, Ceiling_Lamp_*, in prop order -- this room's only, never a lamp on a
    /// table or the next room's), and each switch's rocker. What the stage's switches toggle and a power net reads.
    ///
    /// Two-way switching (strawberry, round 12: "add support for two switches controlling one rooms light"): each switch's
    /// rocker is down (its bottom pressed in) or up, a press flips that switch's own, and the lights are on while an ODD
    /// number of the room's rockers are down -- so one switch alone reads down = on, and either of two works the light
    /// from wherever it was left. A room with no switch has its lights on.</summary>
    public sealed class RoomElectrical
    {
        public readonly List<ElectricalFixture> Switches = new List<ElectricalFixture>();
        /// <summary>Each switch's rocker, by its place in Switches: down (bottom pressed in) or up.</summary>
        public readonly List<bool> Down = new List<bool>();
        public readonly List<ElectricalFixture> Outlets = new List<ElectricalFixture>();
        public readonly List<int> Lights = new List<int>();
        /// <summary>The room's ceiling lights are on: an odd number of its rockers down (or no switch at all).</summary>
        public bool LightsOn => Switches.Count == 0 || Down.Count(d => d) % 2 == 1;
        public bool HasElectrical => Switches.Count + Outlets.Count > 0;
        public IEnumerable<ElectricalFixture> Fixtures { get { foreach (var v in Switches) yield return v; foreach (var v in Outlets) yield return v; } }
        /// <summary>Add a fixture; a switch with its rocker `down`.</summary>
        public void Add(ElectricalFixture v, bool down = false)
        {
            if (v.Kind == ElectricalKind.Switch) { Switches.Add(v); Down.Add(down); }
            else Outlets.Add(v);
        }
        /// <summary>Press switch `k` (its place in Switches): its rocker flips, and with it the lights.</summary>
        public void Flip(int k) { if (k >= 0 && k < Down.Count) Down[k] = !Down[k]; }
        /// <summary>The same record with every fixture moved, or turned (x, z) -> (W - x, D - z) and half round.</summary>
        public RoomElectrical Map(Func<ElectricalFixture, ElectricalFixture> f)
        {
            var r = new RoomElectrical();
            for (int k = 0; k < Switches.Count; k++) r.Add(f(Switches[k]), Down[k]);
            foreach (var v in Outlets) r.Add(f(v));
            r.Lights.AddRange(Lights);
            return r;
        }
        public RoomElectrical Moved(float dx, float dy, float dz) => Map(v => v with { X = v.X + dx, Y = v.Y + dy, Z = v.Z + dz });
        public override string ToString() => $"electrical: {Switches.Count} switch(es) [{string.Join(" ", Down.Select(d => d ? "down" : "up"))}] for {Lights.Count} ceiling light(s) {(LightsOn ? "on" : "off")}, {Outlets.Count} outlet(s)";
    }

    /// <summary>Which kind of HVAC vent a room has: a register on the floor, a grille high on a wall.</summary>
    public enum HvacVentKind { FloorRegister, WallGrille }

    /// <summary>One vent: its kind; its Id -- its place among the building's vents, which is their order among its pieces
    /// (BuildingGenResult.HvacVents, and the vent props of a saved layout in the order they were saved); and where its prop's
    /// origin stands (building-local, or the stage's once the editor has placed the building) and which way it is turned.</summary>
    public readonly record struct HvacVent(HvacVentKind Kind, int Id, float X, float Y, float Z, float Yaw);

    /// <summary>A room's HVAC record: whether it is an HVAC room, and its vents, floor registers and wall grilles apart. What
    /// a temperature system reads -- BuildingGenResult.HvacAt, RoomDesignations.HvacAt -- rather than scanning the décor.
    /// No temperature logic here: the record is the hook.</summary>
    public sealed class RoomHvac
    {
        public readonly List<HvacVent> FloorRegisters = new List<HvacVent>();
        public readonly List<HvacVent> WallGrilles = new List<HvacVent>();
        /// <summary>An HVAC room: one with a vent of either kind.</summary>
        public bool HasHvac => FloorRegisters.Count + WallGrilles.Count > 0;
        public IEnumerable<HvacVent> Vents { get { foreach (var v in FloorRegisters) yield return v; foreach (var v in WallGrilles) yield return v; } }
        public void Add(HvacVent v) => (v.Kind == HvacVentKind.FloorRegister ? FloorRegisters : WallGrilles).Add(v);
        /// <summary>The same record with every vent moved, or turned (x, z) -> (W - x, D - z) and half round.</summary>
        public RoomHvac Map(Func<HvacVent, HvacVent> f)
        {
            var r = new RoomHvac();
            foreach (var v in Vents) r.Add(f(v));
            return r;
        }
        public RoomHvac Moved(float dx, float dy, float dz) => Map(v => v with { X = v.X + dx, Y = v.Y + dy, Z = v.Z + dz });
        public override string ToString() => $"HVAC: {FloorRegisters.Count} floor register(s), {WallGrilles.Count} wall grille(s)";
    }

    /// <summary>What a door to outside is for: the way in, or the way out the back. Inside doors are not marked.</summary>
    public enum DoorRole { Inside, Front, Back }

    /// <summary>A marked door as it is STORED: its role and the middle of its opening in plan (the same space a room
    /// designation's anchor is in), on a storey. strawberry: "mark a front and back door of the house".</summary>
    public struct DoorDesignation
    {
        public DoorRole Role;
        public float X, Z;
        public int Floor;
        public DoorDesignation(DoorRole role, float x, float z, int floor = 0) { Role = role; X = x; Z = z; Floor = floor; }
    }

    /// <summary>A piece of furniture in a building: a prop by name (game/content/objects), its OBJ origin at (X, Y, Z),
    /// stood up and turned as EditorObjects.Upright(Yaw) does it (BuildingGen's FurniturePlan, saved) -- or, with Gen, a
    /// piece built at load from those inputs (a curtain, a rug), which has no OBJ to read.</summary>
    public struct PropDesignation
    {
        public string Name;
        public float X, Y, Z, Yaw;
        public RuntimeDecor Gen;
        public PropDesignation(string name, float x, float y, float z, float yaw, RuntimeDecor gen = null) { Name = name; X = x; Y = y; Z = z; Yaw = yaw; Gen = gen; }
    }

    /// <summary>What builds a runtime piece: game/RuntimeDecorGenerators' factory of that name. Saved BY NAME, never
    /// ordinal, as every other kind in a save is.</summary>
    public enum RuntimeDecorKind { Curtain, Rug }

    /// <summary>The inputs a runtime-built piece is made from (astraclaw's decor_expansion_handoff: "FurniturePlan should save
    /// generator type/version, the two dimensions, state and any selected style/tint. Rebuild on loading"): there is no mesh
    /// on disk, so these ARE the piece, and a save that dropped one would load a piece of nothing.
    /// A curtain: Width its fabric's coverage (the rail runs CurtainRailOver past it each side), Height its cloth's drop, Open
    /// whether it is drawn back to the sides, Style its cloth's tint (RuntimeDecor.CurtainTints). Its piece's origin is the
    /// middle of its back, where the rail's supports meet the wall, at the rail's height; its front faces the piece's Front.
    /// A rug: Width along its piece's Right, Height along its Front -- the floor's two ways, not a thickness -- and Style its
    /// pattern (RugStyles); its piece's origin is its middle, on the floor. Version is the generator's, for a factory that
    /// one day builds the same inputs differently.</summary>
    public sealed record RuntimeDecor(RuntimeDecorKind Kind, int Version, float Width, float Height, bool Open = false, string Style = "")
    {
        /// <summary>The factory version this generator writes.</summary>
        public const int CurrentVersion = 1;
        /// <summary>The curtain's rail and its fittings past each end of the fabric (RuntimeDecorGenerators: "rail adds 0.28 m"),
        /// how far the rail's middle stands over the cloth's top, its end caps' top over that, and how far the curtain stands
        /// out of the wall at most (an open one's tieback). The factory's own geometry, at its full scale -- it scales
        /// fittings down only for a curtain under 1 m either way, which the generator never asks for (CurtainMin) -- read
        /// off RuntimeDecorGenerators.CreateCurtainMesh and held to the built mesh by the in-engine test
        /// (decor.runtime_curtains_and_rugs).</summary>
        public const float CurtainRailOver = 0.14f, CurtainRailOverCloth = 0.09f, CurtainCapOverRail = 0.04f, CurtainDepth = 0.25f;
        /// <summary>No curtain narrower or shorter than this: the factory's fittings stay full size from 1 m up.</summary>
        public const float CurtainMin = 1f;
        /// <summary>A rug's quad lies this far over its floor (RuntimeDecorGenerators.CreateRugMesh).</summary>
        public const float RugLift = 0.015f;
        /// <summary>The cloth tints a room's curtains are drawn from (the game maps each name to a colour; the first is the
        /// factory's own default) and the rug patterns (RuntimeDecorGenerators.RugStyle, by name).</summary>
        public static readonly string[] CurtainTints = { "Linen", "Sage", "Slate", "Rust" };
        public static readonly string[] RugStyles = { "Bordered", "Diamond", "Striped", "Checker" };

        /// <summary>Its extents about its piece's origin, as FurnitureCatalog's (x across, y out of the front, H the top): a
        /// curtain from the wall to its tieback, its rail's ends across, its drop and fittings up; a rug flat, centred.</summary>
        public FurnitureCatalog.Size Size => Kind == RuntimeDecorKind.Curtain
            ? new(-Width * 0.5f - CurtainRailOver, Width * 0.5f + CurtainRailOver, 0f, CurtainDepth, CurtainCapOverRail)
            : new(-Width * 0.5f, Width * 0.5f, -Height * 0.5f, Height * 0.5f, RugLift);
    }

    /// <summary>Framework for building/room designation. NO placement logic -- strawberry_cow: "we dont have
    /// to do the stories system yet. just the framework". What this owns is the part that is expensive to
    /// change later: how a designation is anchored, how it re-finds its room, and how it persists.</summary>
    public static class RoomDesignations
    {
        /// <summary>Is this plan point inside the room? Even-odd ray cast against the outline.
        ///
        /// Counts crossings of a ray going in +X. A vertex exactly on the ray is the classic double-count,
        /// so the test is asymmetric on Z (one endpoint inclusive, the other exclusive) -- which makes a
        /// vertex belong to exactly one of its two edges.</summary>
        public static bool Contains(IReadOnlyList<RoomEnclosure.PlanPoint> outline, float x, float z)
        {
            if (outline == null || outline.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
            {
                var a = outline[i];
                var b = outline[j];
                if ((a.Z > z) == (b.Z > z)) continue;                 // the edge does not straddle the ray
                float t = (z - a.Z) / (b.Z - a.Z);
                if (x < a.X + t * (b.X - a.X)) inside = !inside;
            }
            return inside;
        }

        /// <summary>Re-attach stored designations to the rooms that currently exist.
        ///
        /// Returns one entry per ROOM (index into <paramref name="rooms"/>), and the designations that
        /// matched no room are handed back separately rather than dropped -- an orphan is information (a
        /// room was deleted or opened up) and silently discarding it is how a save quietly loses work.
        ///
        /// First match wins. Rooms do not overlap by construction (they are faces of one planar graph), so
        /// a point is inside at most one -- but if two designations land in the SAME room the later one is
        /// an orphan too, rather than overwriting: two labels for one room is a conflict the caller should
        /// see, not something to resolve by arrival order.</summary>
        public static void Resolve(
            IReadOnlyList<RoomEnclosure.Room> rooms,
            IReadOnlyList<RoomDesignation> stored,
            out RoomKind[] byRoom,
            out List<RoomDesignation> orphans)
        {
            byRoom = new RoomKind[rooms?.Count ?? 0];
            orphans = new List<RoomDesignation>();
            if (rooms == null || stored == null) return;

            for (int i = 0; i < byRoom.Length; i++) byRoom[i] = RoomKind.Unassigned;

            foreach (var d in stored)
            {
                int hit = -1;
                for (int r = 0; r < rooms.Count; r++)
                    if (Contains(rooms[r].Outline, d.X, d.Z)) { hit = r; break; }

                if (hit < 0 || byRoom[hit] != RoomKind.Unassigned) { orphans.Add(d); continue; }
                byRoom[hit] = d.Kind;
            }
        }

        /// <summary>The HVAC record of the room a plan point is in, or null (no room there, no label for it on that storey, or
        /// a room with no vents): the room the point is in among `rooms` (RoomEnclosure.Find of that storey's walls), and the
        /// label on that storey anchored in the same room. A temperature system asks this, not the décor.</summary>
        public static RoomHvac HvacAt(IReadOnlyList<RoomEnclosure.Room> rooms, IReadOnlyList<RoomDesignation> stored, float x, float z, int floor = 0)
        {
            if (rooms == null || stored == null) return null;
            foreach (var r in rooms)
            {
                if (!Contains(r.Outline, x, z)) continue;
                foreach (var d in stored)
                    if (d.Floor == floor && Contains(r.Outline, d.X, d.Z)) return d.Hvac != null && d.Hvac.HasHvac ? d.Hvac : null;
                return null;
            }
            return null;
        }

        /// <summary>The electrical record of the room a plan point is in, or null (no room there, no label for it, or a room
        /// with no switch or outlet), as HvacAt.</summary>
        public static RoomElectrical ElectricalAt(IReadOnlyList<RoomEnclosure.Room> rooms, IReadOnlyList<RoomDesignation> stored, float x, float z, int floor = 0)
        {
            if (rooms == null || stored == null) return null;
            foreach (var r in rooms)
            {
                if (!Contains(r.Outline, x, z)) continue;
                foreach (var d in stored)
                    if (d.Floor == floor && Contains(r.Outline, d.X, d.Z)) return d.Electrical != null && d.Electrical.HasElectrical ? d.Electrical : null;
                return null;
            }
            return null;
        }

        /// <summary>Does the room a plan point is in have HVAC (HvacAt)?</summary>
        public static bool HasHvacAt(IReadOnlyList<RoomEnclosure.Room> rooms, IReadOnlyList<RoomDesignation> stored, float x, float z, int floor = 0)
            => HvacAt(rooms, stored, x, z, floor) != null;

        /// <summary>Where to anchor a NEW designation for a room: its centroid, nudged onto the room if the
        /// centroid falls outside.
        ///
        /// A centroid is outside its own polygon for any concave shape -- an L-shaped room is the common
        /// case here, not a curiosity -- and an anchor outside the room it labels matches nothing on the
        /// very next load. When that happens, fall back to the centre of the room's largest slab, which is
        /// inside by construction because the slabs are a cover OF the room.</summary>
        public static bool AnchorFor(RoomEnclosure.Room room, out float x, out float z)
        {
            x = z = 0f;
            if (room == null || room.Outline.Count < 3) return false;

            float sx = 0f, sz = 0f;
            foreach (var p in room.Outline) { sx += p.X; sz += p.Z; }
            x = sx / room.Outline.Count;
            z = sz / room.Outline.Count;
            if (Contains(room.Outline, x, z)) return true;

            // Concave: take the biggest axis-aligned piece the room was decomposed into.
            float best = 0f;
            bool found = false;
            foreach (var s in room.Slabs)
                if (s.Area > best) { best = s.Area; x = (s.MinX + s.MaxX) * 0.5f; z = (s.MinZ + s.MaxZ) * 0.5f; found = true; }
            return found;
        }

        /// <summary>Parse a kind by NAME, case-insensitively. Unknown names become Unassigned rather than
        /// throwing: a save written by a newer editor with a kind this build has never heard of should lose
        /// that one label, not fail to load the building.</summary>
        public static RoomKind ParseRoom(string s)
            => IsName(s) && Enum.TryParse<RoomKind>(s, true, out var k) ? k : RoomKind.Unassigned;

        public static BuildingKind ParseBuilding(string s)
            => IsName(s) && Enum.TryParse<BuildingKind>(s, true, out var k) ? k : BuildingKind.Misc;

        /// <summary>Reject anything numeric BEFORE handing it to Enum.TryParse.
        ///
        /// TryParse accepts an ordinal string -- "3" happily becomes whatever member sits at 3 -- which
        /// quietly destroys the by-name guarantee this whole scheme rests on: a save written by ordinal
        /// would round-trip perfectly today and silently RE-LABEL every room the day someone reorders or
        /// inserts an enum member. Found by mutating the writer to emit ordinals and watching the
        /// round-trip test stay green, which is the only way this shows up.
        ///
        /// Rejecting it means an ordinal save degrades to Unassigned -- visibly wrong at the point of
        /// loading, instead of subtly wrong later.</summary>
        static bool IsName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            foreach (char c in s)
                if (char.IsLetter(c)) return true;      // a real name has at least one letter
            return false;
        }
    }
}
