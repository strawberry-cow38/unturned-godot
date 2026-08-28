using System;
using System.Collections.Generic;
using System.Globalization;
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

        public static string Write(IEnumerable<WallPlan> walls)
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            sb.Append("# wall <x> <y> <z> <yawDeg> <length> <thickness> <materialId> [height] [pitchDeg] [kind] [gableRise] [texel] [insetL0 insetL1 insetR0 insetR1] [matBack texelBack]\n");
            sb.Append("#   open <u> <v> <width> <height> <depth> <archetype> [glazed tint hp indestructible broken] [doorProp doorOpen]\n");
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
                        sb.Append('\n');
                    }
                }
            return sb.ToString();
        }

        public static List<WallPlan> Read(IEnumerable<string> lines)
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
