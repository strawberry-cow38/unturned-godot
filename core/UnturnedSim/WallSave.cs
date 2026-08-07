using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnturnedSim
{
    /// <summary>One wall as saved: where it is, how big, and its holes. Deliberately POCO and engine-free --
    /// a wall is only data, which is the same reason undo can rebuild a deleted one from a snapshot.</summary>
    public sealed class WallPlan
    {
        public float X, Y, Z;         // origin: the wall's start corner at its base
        public float Yaw;             // degrees; the wall runs along its local +X
        public float Length = WallOpenings.LatticeStep;
        public float Height = WallOpenings.DoorHeight;
        public float Thickness = WallOpenings.DefaultThickness;
        public int Material;
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
            sb.Append("# wall <x> <y> <z> <yawDeg> <length> <thickness> <materialId> [height]\n");
            sb.Append("#   open <u> <v> <width> <height> <depth> <archetype>\n");
            if (walls != null)
                foreach (var w in walls)
                {
                    if (w == null) continue;
                    sb.Append("wall ").Append(F(w.X)).Append(' ').Append(F(w.Y)).Append(' ').Append(F(w.Z))
                      .Append(' ').Append(F(w.Yaw)).Append(' ').Append(F(w.Length)).Append(' ')
                      .Append(F(w.Thickness)).Append(' ').Append(w.Material.ToString(CultureInfo.InvariantCulture))
                      .Append(' ').Append(F(w.Height))
                      .Append('\n');
                    foreach (var o in w.Openings)
                        sb.Append("  open ").Append(F(o.U)).Append(' ').Append(F(o.V)).Append(' ')
                          .Append(F(o.Width)).Append(' ').Append(F(o.Height)).Append(' ')
                          .Append(F(o.Depth)).Append(' ').Append(o.Archetype.ToString(CultureInfo.InvariantCulture))
                          .Append('\n');
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
                    outp.Add(w);
                    cur = w;
                }
                else if (p[0] == "open" && p.Length >= 7 && cur != null)
                {
                    if (!N(p[1], out float u) || !N(p[2], out float v) || !N(p[3], out float ow)
                        || !N(p[4], out float oh) || !N(p[5], out float d) || !int.TryParse(p[6], out int arch)) continue;
                    // Clamped on load, not trusted: a hand-edited file with an opening bigger than its wall
                    // must produce a wall with a silly hole, never a crash or a wall that fails to appear.
                    cur.Openings.Add(WallOpenings.Clamp(new WallOpening(u, v, ow, oh, d, arch),
                                                        cur.Length, cur.Height, cur.Openings));
                }
                // anything else: a newer editor's field. Skip it and keep the building.
            }
            return outp;
        }

        static bool N(string s, out float v) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
