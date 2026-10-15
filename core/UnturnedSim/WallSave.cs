using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace UnturnedSim
{
    /// <summary>One wall as saved: where it is, how big, and its holes. Deliberately POCO and engine-free --
    /// a wall is only data, which is the same reason undo can rebuild a deleted one from a snapshot.</summary>
    /// <summary>What a surface is FOR. Presentation and defaults only -- the geometry never reads it, the
    /// same rule the opening archetypes follow. A floor is a wall lying down: the identical rectangle-minus-
    /// openings problem, so it goes through the identical partition, collider, palette and bake path, and a
    /// stairwell is an opening. Splitting them into separate types would buy a second copy of every bug.</summary>
    // Stairs is a LABEL, like every other kind -- Rebuild() never reads Kind. A flight is emitted as one
    // flat tread surface per step by AddStairs; the kind is what lets the panel, the bake and a future
    // "delete the whole flight" tell those treads from an ordinary floor.
    public enum SurfaceKind { Wall, Floor, Roof, Foundation, Stairs }

    public sealed class WallPlan
    {
        public float X, Y, Z;         // origin: the wall's start corner at its base
        public float Yaw;             // degrees; the wall runs along its local +X
        public float Pitch;           // degrees about local X: 0 upright, -90 lying flat (a floor or flat roof)
        public SurfaceKind Kind = SurfaceKind.Wall;
        public float GableRise;       // 0 = flat top; >0 raises this wall to a central peak (a gable end)
        public float Length = WallOpenings.LatticeStep;
        public float Height = WallOpenings.DoorHeight;
        public float Thickness = WallOpenings.DefaultThickness;
        public int Material;
        /// <summary>Which of the palette's eight texels this surface is painted in, or -1 for "whatever the
        /// palette says a WALL is". A retail building is one palette but not one colour -- its roof is a
        /// different texel from its walls -- so a roof imported without this comes back cream.</summary>
        public int Texel = -1;
        /// <summary>Trapezoid edges: how far the surface is set in from each side at its base and at its top.
        /// All zero is a plain rectangle. See WallImport.Recovered.</summary>
        public float InsetL0, InsetL1, InsetR0, InsetR1;
        /// <summary>Palette + texel for the BACK face, -1 for "same as the front".</summary>
        public int MaterialBack = -1, TexelBack = -1;
        public readonly List<WallOpening> Openings = new();
    }

    /// <summary>Text format for drawn walls. Line-oriented and human-readable on purpose: these files land in
    /// a git repo next to the maps, and a format you can read in a diff is a format you can fix by hand when a
    /// tool writes something stupid.
    ///
    /// Unknown lines are SKIPPED rather than fatal, so a newer editor's extra fields degrade to "loses the new
    /// thing" instead of "loses the building".</summary>
    public static class WallSave
    {
        public const string Header = "# unturned-godot walls v1";

        static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>Serialise walls, and OPTIONALLY the building's designation data.
        ///
        /// The designation lines are top-level and additive, so an older reader skips them (see the class
        /// doc: unknown lines are skipped, not fatal) and loses only the labels rather than the building.
        /// They are optional PARAMETERS rather than a new type so the three existing callers keep working
        /// unchanged -- a save format is the expensive thing to change twice.</summary>
        public static string Write(IEnumerable<WallPlan> walls,
                                   BuildingKind? building = null,
                                   IEnumerable<RoomDesignation> rooms = null,
                                   IEnumerable<DoorDesignation> doors = null,
                                   IEnumerable<PropDesignation> props = null)
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            sb.Append("# wall <x> <y> <z> <yawDeg> <length> <thickness> <materialId> [height] [pitchDeg] [kind] [gableRise] [texel] [insetL0 insetL1 insetR0 insetR1] [matBack texelBack]\n");
            sb.Append("#   open <u> <v> <width> <height> <depth> <archetype> [glazed tint hp indestructible broken] [doorProp doorOpen] [barr <style>] [swing <start|end> <front|back>] [action <double|pocket>]\n");
            sb.Append("# building <kind>            -- Residential|Commercial|Industrial|Misc|Apartment (the volume is DERIVED from the walls)\n");
            sb.Append("# room <kind> <x> <z> [floor] [hvac <n> (<floor|wall> <id> <x> <y> <z> <yawDeg>) x n] -- a designation ANCHORED at a point inside the room it labels; hvac: its vents\n");
            sb.Append("# door <front|back> <x> <z> [floor] -- a marked door to outside, at the middle of its opening\n");
            sb.Append("# prop <name> <x> <y> <z> <yawDeg> [gen <curtain|rug> <version> <width> <height> <open> <style>] -- a piece of furniture, a prop by name stood up and turned as the editor does; gen: built at load from these inputs\n");
            // Kinds by NAME, never ordinal: the enums can gain or reorder members without re-labelling
            // every existing save.
            if (building.HasValue) sb.Append("building ").Append(building.Value.ToString()).Append('\n');
            if (rooms != null)
                foreach (var r in rooms)
                    sb.Append("room ").Append(r.Kind.ToString()).Append(' ')
                      .Append(F(r.X)).Append(' ').Append(F(r.Z))
                      // only when it is not the ground, so a single-storey save reads exactly as it always did
                      .Append(r.Floor != 0 ? " " + r.Floor.ToString(CultureInfo.InvariantCulture) : "")
                      // an HVAC room's vents: a KEYED trailing block, written only for one -- a room with none writes the line it
                      // always did, and an older reader skips the tokens (the room's label survives; its vents' record does not)
                      .Append(r.Hvac != null && r.Hvac.HasHvac ? Hvac(r.Hvac) : "")
                      // and its switch, outlets and ceiling lights, the same way (after the vents: a reader of the vents alone
                      // stops at their count)
                      .Append(r.Electrical != null && r.Electrical.HasElectrical ? Elec(r.Electrical) : "").Append('\n');
            if (doors != null)
                foreach (var d in doors)
                    sb.Append("door ").Append(d.Role.ToString().ToLowerInvariant()).Append(' ').Append(F(d.X)).Append(' ').Append(F(d.Z))
                      .Append(d.Floor != 0 ? " " + d.Floor.ToString(CultureInfo.InvariantCulture) : "").Append('\n');
            if (props != null)
                foreach (var m in props)
                    if (!string.IsNullOrEmpty(m.Name) && m.Name.IndexOf(' ') < 0)
                    {
                        sb.Append("prop ").Append(m.Name).Append(' ').Append(F(m.X)).Append(' ').Append(F(m.Y)).Append(' ').Append(F(m.Z))
                          .Append(' ').Append(F(m.Yaw));
                        // a piece built at load (a curtain, a rug): a KEYED trailing block, as "barr" and "swing" are, written only
                        // when there is one -- so every prop saves the line it always did, and an older reader takes the first six
                        // tokens and skips the rest (it loads a name with no mesh: the curtain is lost, the building is not)
                        if (m.Gen != null)
                            sb.Append(" gen ").Append(m.Gen.Kind.ToString().ToLowerInvariant()).Append(' ').Append(m.Gen.Version.ToString(CultureInfo.InvariantCulture))
                              .Append(' ').Append(F(m.Gen.Width)).Append(' ').Append(F(m.Gen.Height)).Append(' ').Append(m.Gen.Open ? '1' : '0')
                              .Append(' ').Append(string.IsNullOrEmpty(m.Gen.Style) ? "-" : m.Gen.Style);
                        sb.Append('\n');
                    }
            if (walls != null)
                foreach (var w in walls)
                {
                    if (w == null) continue;
                    sb.Append("wall ").Append(F(w.X)).Append(' ').Append(F(w.Y)).Append(' ').Append(F(w.Z))
                      .Append(' ').Append(F(w.Yaw)).Append(' ').Append(F(w.Length)).Append(' ')
                      .Append(F(w.Thickness)).Append(' ').Append(w.Material.ToString(CultureInfo.InvariantCulture))
                      .Append(' ').Append(F(w.Height))
                      .Append(' ').Append(F(w.Pitch))
                      .Append(' ').Append(w.Kind.ToString())
                      .Append(' ').Append(F(w.GableRise))
                      .Append(' ').Append(w.Texel.ToString(CultureInfo.InvariantCulture))
                      .Append(' ').Append(F(w.InsetL0)).Append(' ').Append(F(w.InsetL1))
                      .Append(' ').Append(F(w.InsetR0)).Append(' ').Append(F(w.InsetR1))
                      .Append(' ').Append(w.MaterialBack.ToString(CultureInfo.InvariantCulture))
                      .Append(' ').Append(w.TexelBack.ToString(CultureInfo.InvariantCulture))
                      .Append('\n');
                    foreach (var o in w.Openings)
                    {
                        sb.Append("  open ").Append(F(o.U)).Append(' ').Append(F(o.V)).Append(' ')
                          .Append(F(o.Width)).Append(' ').Append(F(o.Height)).Append(' ')
                          .Append(F(o.Depth)).Append(' ').Append(o.Archetype.ToString(CultureInfo.InvariantCulture));
                        // Glazing is written only when there IS some, so a building with no windows saves
                        // byte-for-byte as it did before glass existed. Trailing and optional, like every
                        // field added to this format after the fact.
                        // A DOOR needs the glazing block written first even when there is none, because these
                        // are POSITIONAL trailing tokens -- skipping five of them would slide the door name
                        // into the "glazed" slot. Cheap to be explicit; silent to get wrong.
                        if (o.Glazed || o.GlassBroken || o.HasDoor)
                            sb.Append(' ').Append(o.Glazed ? '1' : '0')
                              .Append(' ').Append(o.GlassTint.ToString(CultureInfo.InvariantCulture))
                              .Append(' ').Append(F(o.GlassHp))
                              .Append(' ').Append(o.GlassIndestructible ? '1' : '0')
                              .Append(' ').Append(o.GlassBroken ? '1' : '0');
                        if (o.HasDoor)
                            sb.Append(' ').Append(o.DoorProp)
                              .Append(' ').Append(o.DoorOpen ? '1' : '0');
                        // pre-barricade: a KEYED token ("barr <style>") rather than another positional trailing block,
                        // so it never depends on the glass/door blocks being written first (the slide-hazard the code
                        // above warns about). An old reader just skips the two unknown trailing tokens.
                        if (o.HasBarricade)
                            sb.Append(" barr ").Append(o.Barricade.ToString());
                        // which way its door swings: keyed for the same reason, and written only when it is not the
                        // default (hinged at U1, opening to the back), so every save from before the choice existed
                        // reads back as the door it always was, and a default door writes the line it always did
                        if (o.HasDoor && (o.DoorHingeAtStart || o.DoorOpensFront))
                            sb.Append(" swing ").Append(o.DoorHingeAtStart ? "start" : "end").Append(' ').Append(o.DoorOpensFront ? "front" : "back");
                        // how it moves (round 12b): keyed, and only for a door that does not simply swing, so a swing
                        // door -- every door before this -- writes the line it always did, and an older reader, which
                        // skips what it does not know, loads a double-acting or pocket door as the swing door it was hung as
                        if (o.HasDoor && o.DoorAction != DoorAction.Swing)
                            sb.Append(" action ").Append(o.DoorAction == DoorAction.Pocket ? "pocket" : "double");
                        sb.Append('\n');
                    }
                }
            return sb.ToString();
        }

        static string Hvac(RoomHvac h)
        {
            var sb = new StringBuilder(" hvac ").Append(h.Vents.Count().ToString(CultureInfo.InvariantCulture));
            foreach (var v in h.Vents)
                sb.Append(' ').Append(v.Kind == HvacVentKind.FloorRegister ? "floor" : "wall").Append(' ').Append(v.Id.ToString(CultureInfo.InvariantCulture))
                  .Append(' ').Append(F(v.X)).Append(' ').Append(F(v.Y)).Append(' ').Append(F(v.Z)).Append(' ').Append(F(v.Yaw));
            return sb.ToString();
        }

        /// <summary>A room's electrical block: "elec", its switches (counted, then id x y z yaw and its rocker, 1 down or 0
        /// up -- the lights' state is theirs: RoomElectrical.LightsOn), its outlets (counted, id x y z yaw watts), its ceiling
        /// lights' ids (counted).</summary>
        static string Elec(RoomElectrical e)
        {
            var sb = new StringBuilder(" elec ").Append(e.Switches.Count.ToString(CultureInfo.InvariantCulture));
            for (int k = 0; k < e.Switches.Count; k++)
            {
                var v = e.Switches[k];
                sb.Append(' ').Append(v.Id.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(F(v.X)).Append(' ').Append(F(v.Y)).Append(' ').Append(F(v.Z))
                  .Append(' ').Append(F(v.Yaw)).Append(' ').Append(e.Down[k] ? '1' : '0');
            }
            sb.Append(' ').Append(e.Outlets.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var v in e.Outlets)
                sb.Append(' ').Append(v.Id.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(F(v.X)).Append(' ').Append(F(v.Y)).Append(' ').Append(F(v.Z))
                  .Append(' ').Append(F(v.Yaw)).Append(' ').Append(F(v.Watts));
            sb.Append(' ').Append(e.Lights.Count.ToString(CultureInfo.InvariantCulture));
            foreach (int k in e.Lights) sb.Append(' ').Append(k.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>A room line's electrical block (Elec), or null: none, or one that will not read -- which loses the record,
        /// not the room's label.</summary>
        static RoomElectrical ReadElec(string[] p)
        {
            int i = Array.IndexOf(p, "elec", 4);
            if (i < 0) return null;
            var e = new RoomElectrical();
            int at = i + 1;
            bool Int(out int v) { v = 0; return at < p.Length && int.TryParse(p[at++], NumberStyles.Integer, CultureInfo.InvariantCulture, out v); }
            bool Num(out float v) { v = 0f; return at < p.Length && N(p[at++], out v); }
            foreach (var kind in new[] { ElectricalKind.Switch, ElectricalKind.Outlet })
            {
                if (!Int(out int n) || n < 0 || n > 64) return null;
                for (int k = 0; k < n; k++)
                {
                    if (!Int(out int id) || !Num(out float x) || !Num(out float y) || !Num(out float z) || !Num(out float yaw)) return null;
                    if (kind == ElectricalKind.Switch)
                    {
                        if (!Int(out int down) || down is not (0 or 1)) return null;
                        e.Add(new ElectricalFixture(kind, id, x, y, z, yaw), down == 1);
                    }
                    else
                    {
                        if (!Num(out float watts)) return null;
                        e.Add(new ElectricalFixture(kind, id, x, y, z, yaw, watts));
                    }
                }
            }
            if (!Int(out int lights) || lights < 0 || lights > 256) return null;
            for (int k = 0; k < lights; k++) { if (!Int(out int id)) return null; e.Lights.Add(id); }
            return e;
        }

        /// <summary>A room line's HVAC block (Hvac), or null: none, or one that will not read -- which loses the record, not
        /// the room's label.</summary>
        static RoomHvac ReadHvac(string[] p)
        {
            int hi = Array.IndexOf(p, "hvac", 4);
            if (hi < 0 || hi + 1 >= p.Length || !int.TryParse(p[hi + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1
                || n > (p.Length - hi - 2) / 6) return null;
            var h = new RoomHvac();
            for (int k = 0; k < n; k++)
            {
                int b = hi + 2 + k * 6;
                if (b + 5 >= p.Length || (p[b] != "floor" && p[b] != "wall") || !int.TryParse(p[b + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                    || !N(p[b + 2], out float x) || !N(p[b + 3], out float y) || !N(p[b + 4], out float z) || !N(p[b + 5], out float yaw)) return null;
                h.Add(new HvacVent(p[b] == "floor" ? HvacVentKind.FloorRegister : HvacVentKind.WallGrille, id, x, y, z, yaw));
            }
            return h;
        }

        public static List<WallPlan> Read(IEnumerable<string> lines)
            => Read(lines, out _, out _);

        /// <summary>Read walls AND designation data. A file with no designation lines yields Misc and an
        /// empty list, which is exactly what every save written before this existed contains -- so the
        /// old-file path is the default path, not a special case.</summary>
        public static List<WallPlan> Read(IEnumerable<string> lines,
                                          out BuildingKind building,
                                          out List<RoomDesignation> rooms)
        {
            building = BuildingKind.Misc;
            rooms = new List<RoomDesignation>();
            return ReadInner(lines, ref building, rooms, null, null);
        }

        /// <summary>Read walls, designations and the marked doors.</summary>
        public static List<WallPlan> Read(IEnumerable<string> lines, out BuildingKind building,
                                          out List<RoomDesignation> rooms, out List<DoorDesignation> doors)
        {
            building = BuildingKind.Misc;
            rooms = new List<RoomDesignation>();
            doors = new List<DoorDesignation>();
            return ReadInner(lines, ref building, rooms, doors, null);
        }

        /// <summary>Read walls, designations, the marked doors and the furniture.</summary>
        public static List<WallPlan> Read(IEnumerable<string> lines, out BuildingKind building, out List<RoomDesignation> rooms,
                                          out List<DoorDesignation> doors, out List<PropDesignation> props)
        {
            building = BuildingKind.Misc;
            rooms = new List<RoomDesignation>();
            doors = new List<DoorDesignation>();
            props = new List<PropDesignation>();
            return ReadInner(lines, ref building, rooms, doors, props);
        }

        static List<WallPlan> ReadInner(IEnumerable<string> lines, ref BuildingKind building, List<RoomDesignation> rooms,
                                        List<DoorDesignation> doors, List<PropDesignation> props)
        {
            var outp = new List<WallPlan>();
            if (lines == null) return outp;
            WallPlan cur = null;
            foreach (var raw in lines)
            {
                if (raw == null) continue;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

                if (p[0] == "building" && p.Length >= 2)
                {
                    building = RoomDesignations.ParseBuilding(p[1]);
                    continue;
                }
                if (p[0] == "door" && p.Length >= 4)
                {
                    // an unknown role or a malformed coordinate drops THAT mark, not the building
                    if (doors != null && Enum.TryParse<DoorRole>(p[1], true, out var role) && role != DoorRole.Inside
                        && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float dx)
                        && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float dz))
                        doors.Add(new DoorDesignation(role, dx, dz,
                            p.Length >= 5 && int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int df) ? df : 0));
                    continue;
                }
                if (p[0] == "prop" && p.Length >= 6)
                {
                    // a malformed number drops THAT piece, not the building
                    if (props != null && N(p[2], out float px) && N(p[3], out float py) && N(p[4], out float pz) && N(p[5], out float pyaw))
                    {
                        // built at load: "gen <kind> <version> <width> <height> <open> <style>", found by its key. A block that
                        // will not parse -- a kind this build has no factory for, a size that is not a size -- drops the piece:
                        // there is no mesh to fall back on, and a name with nothing to build is not a piece
                        int gi = System.Array.IndexOf(p, "gen", 6);
                        RuntimeDecor gen = null;
                        if (gi >= 0)
                        {
                            if (gi + 6 >= p.Length || !System.Enum.TryParse<RuntimeDecorKind>(p[gi + 1], true, out var gk) || int.TryParse(p[gi + 1], out _)
                                || !int.TryParse(p[gi + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gv)
                                || !N(p[gi + 3], out float gw) || !N(p[gi + 4], out float gh) || !(gw > 0f && gh > 0f && float.IsFinite(gw) && float.IsFinite(gh))
                                || (p[gi + 5] != "0" && p[gi + 5] != "1")) continue;
                            gen = new RuntimeDecor(gk, gv, gw, gh, p[gi + 5] == "1", p[gi + 6] == "-" ? "" : p[gi + 6]);
                        }
                        props.Add(new PropDesignation(p[1], px, py, pz, pyaw, gen));
                    }
                    continue;
                }
                if (p[0] == "room" && p.Length >= 4)
                {
                    // A malformed coordinate drops THAT label, not the building -- same principle as an
                    // unknown kind degrading to Unassigned.
                    if (float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float rx)
                        && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float rz))
                        rooms.Add(new RoomDesignation(RoomDesignations.ParseRoom(p[1]), rx, rz,
                            p.Length >= 5 && int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rf) ? rf : 0, ReadHvac(p), ReadElec(p)));
                    continue;
                }
                if (p[0] == "wall" && p.Length >= 8)
                {
                    var w = new WallPlan();
                    if (!N(p[1], out w.X) || !N(p[2], out w.Y) || !N(p[3], out w.Z) || !N(p[4], out w.Yaw)
                        || !N(p[5], out w.Length) || !N(p[6], out w.Thickness) || !int.TryParse(p[7], out w.Material))
                    { cur = null; continue; }          // a malformed wall drops itself, not the rest of the file
                    // height is trailing and optional: it arrived after the format did, and a file written
                    // before it existed describes walls that were all one storey tall
                    if (p.Length < 9 || !N(p[8], out w.Height)) w.Height = WallOpenings.DoorHeight;
                    if (p.Length < 10 || !N(p[9], out w.Pitch)) w.Pitch = 0f;
                    if (p.Length < 11 || !System.Enum.TryParse(p[10], out w.Kind)) w.Kind = SurfaceKind.Wall;
                    if (p.Length < 12 || !N(p[11], out w.GableRise)) w.GableRise = 0f;
                    if (p.Length < 13 || !int.TryParse(p[12], out w.Texel)) w.Texel = -1;
                    if (p.Length < 17 || !N(p[13], out w.InsetL0) || !N(p[14], out w.InsetL1)
                        || !N(p[15], out w.InsetR0) || !N(p[16], out w.InsetR1))
                    { w.InsetL0 = w.InsetL1 = w.InsetR0 = w.InsetR1 = 0f; }
                    if (p.Length < 19 || !int.TryParse(p[17], out w.MaterialBack)
                        || !int.TryParse(p[18], out w.TexelBack))
                    { w.MaterialBack = -1; w.TexelBack = -1; }
                    outp.Add(w);
                    cur = w;
                }
                else if (p[0] == "open" && p.Length >= 7 && cur != null)
                {
                    if (!N(p[1], out float u) || !N(p[2], out float v) || !N(p[3], out float ow)
                        || !N(p[4], out float oh) || !N(p[5], out float d) || !int.TryParse(p[6], out int arch)) continue;
                    // Clamped into the WALL, but deliberately NOT against its siblings.
                    //
                    // Overlapping openings are legal -- the partition removes their union once, and that is
                    // L0-tested as load-bearing -- and the editor can produce them, because Clamp's two-sided
                    // fallback parks an opening overlapping a neighbour and DragEdge never constrains size
                    // against siblings at all. Passing the already-loaded openings in as blockers meant load
                    // RELOCATED the second of any overlapping pair: openings at U=1 and U=2 read back with the
                    // second at U=4. Saving and reopening then gave a different building than the one baked,
                    // silently. A loader's job is to return what was written.
                    float cu = Math.Clamp(u, 0f, Math.Max(0f, cur.Length - Math.Min(ow, cur.Length)));
                    float cv = Math.Clamp(v, 0f, Math.Max(0f, cur.Height - Math.Min(oh, cur.Height)));
                    var op = new WallOpening(cu, cv, Math.Min(ow, cur.Length), Math.Min(oh, cur.Height), d, arch);
                    // Glazing: trailing and optional, so a file written before glass existed loads as
                    // unglazed rather than failing. Parsed as a block -- a half-written set is not honoured
                    // in part, because a window that came back with its tint but not its "broken" flag would
                    // repair itself on load and look like the save had worked.
                    if (p.Length >= 12 && int.TryParse(p[7], out int gz) && int.TryParse(p[8], out int tint)
                        && N(p[9], out float ghp) && int.TryParse(p[10], out int gind) && int.TryParse(p[11], out int gbr))
                    {
                        op.Glazed = gz != 0; op.GlassTint = tint; op.GlassHp = ghp;
                        op.GlassIndestructible = gind != 0; op.GlassBroken = gbr != 0;
                    }
                    // door: trailing after the glazing block, same optional-block rule
                    if (p.Length >= 14 && int.TryParse(p[13], out int dop))
                    { op.DoorProp = p[12]; op.DoorOpen = dop != 0; }
                    // pre-barricade: a KEYED "barr <style>" token, found by scanning (position-independent, so it
                    // reads back whether or not the glass/door blocks are present); an unknown style -> None.
                    int bi = System.Array.IndexOf(p, "barr");
                    if (bi >= 0 && bi + 1 < p.Length) System.Enum.TryParse(p[bi + 1], true, out op.Barricade);
                    // door swing: a KEYED "swing <start|end> <front|back>", found the same way; a word it does not know
                    // leaves that half at its default rather than guessing
                    int si = System.Array.IndexOf(p, "swing");
                    if (si >= 0 && si + 2 < p.Length && op.HasDoor)
                    {
                        op.DoorHingeAtStart = p[si + 1] == "start";
                        op.DoorOpensFront = p[si + 2] == "front";
                    }
                    // how it moves: a KEYED "action <double|pocket>"; no token, or a word it does not know, is a swing door
                    int ai = System.Array.IndexOf(p, "action");
                    if (ai >= 0 && ai + 1 < p.Length && op.HasDoor)
                        op.DoorAction = p[ai + 1] switch { "double" => DoorAction.DoubleSwing, "pocket" => DoorAction.Pocket, _ => DoorAction.Swing };
                    cur.Openings.Add(op);
                }
                // anything else: a newer editor's field. Skip it and keep the building.
            }
            return outp;
        }

        static bool N(string s, out float v) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
