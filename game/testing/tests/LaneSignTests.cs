using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>Lane-selectable overhead signs: a board only for the lanes you tick.
    ///
    /// Master: "can u make it smart, where u can choose which lanes get signs above them". astraclaw split
    /// their gantry into a bare frame plus a reusable board and asked for one specific acceptance check,
    /// which section 3 is: select lanes 1 and 3, give them different text, save and reload, and only those
    /// two boards should return, over the same lanes, with their text intact.</summary>
    public sealed class LaneSignTests : GameTest
    {
        public override string Name => "editor.lane_signs";
        public override double TimeoutSimSeconds => 30;

        static int Boards(Node3D root)
        {
            int n = 0;
            foreach (var c in root.GetChildren()) if (c is MeshInstance3D m && m.Name.ToString().StartsWith("Board")) n++;
            return n;
        }
        static List<Label3D> Legends(Node3D root)
        {
            var o = new List<Label3D>();
            foreach (var c in root.GetChildren()) if (c is Label3D l && l.Name.ToString().StartsWith("LaneText")) o.Add(l);
            return o;
        }

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            try { System.IO.File.Delete(ProjectSettings.GlobalizePath("res://content/objects/editor__lanesigns.txt")); } catch { }
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            var field = new RoadField();
            World.AddChild(field);
            ed.Roads = field;
            var terr = Terrain.CreateFlat(1, 1, withCollider: false);
            World.AddChild(terr);
            field.Terr = terr;
            yield return Ticks(2);
            float g0 = terr.SampleHeight(0f, 0f);

            // ---- 0. ⭐⭐ THE LANES COME OFF THE PAINT, NOT FROM BuildLanePaths. astraclaw's warning: that
            // forces an EVEN two-way count at a global 4.6 m spacing, which is wrong for a one-way
            // carriageway. road_1 paints FOUR lanes across 13.8 m, and this reads the dashed dividers.
            var lanes = field.PaintedLaneCentres(1, 6.9f);
            T.Check($"road_1's painted markings give four lanes ({(lanes == null ? -1 : lanes.Length)})",
                    lanes != null && lanes.Length == 4);
            if (lanes == null || lanes.Length != 4) yield break;
            // ⭐ AGREEING WITH A SECOND, INDEPENDENT DERIVATION: astraclaw measured the same texture by hand
            // and published these centres. Two derivations that share no code is the only reason either is
            // worth quoting. [[feedback_validate_before_claiming]]
            float[] want = { 5.12109375f, 1.6171875f, -1.8328125f, -5.22890625f };
            float worst = 0f;
            for (int i = 0; i < 4; i++) worst = Mathf.Max(worst, Mathf.Abs(lanes[i] - want[i]));
            T.Check($"...matching astraclaw's independent measurement to {worst * 1000f:0.##} mm", worst < 0.01f);
            // ⭐ CONTROL: a material that paints no dashed divider must report no lanes, or "four lanes"
            // above would be a number this code produces for anything at all.
            T.Check($"control: the 2x2 palette road (material 3) paints no lanes",
                    field.PaintedLaneCentres(3, 6.9f) == null);

            // ---- 1. A SIGN OVER A REAL ROAD TAKES THAT ROAD'S LANES.
            var pts = new List<Vector3>();
            for (int k = 0; k <= 6; k++) pts.Add(new Vector3(k * 20f, g0, 0f));
            // ⚠ width BEFORE the road: the material has to exist when the road is built, or RoadHalfWidth
            // reports 0 and every lane figure below is derived from a road with no width.
            field.DebugSetMaterialWidth(1, 6.9f);
            field.DebugSetMaterialDepth(1, 0.3f);
            int road = field.AddRoadFromPolyline(pts, 1, false, true);
            yield return Ticks(1);
            T.Check($"fixture: a Highway_1 road {field.RoadHalfWidth(road) * 2f:0.#} m wide",
                    Mathf.Abs(field.RoadHalfWidth(road) - 6.9f) < 0.2f);

            var sign = objs.Place(EditorObjects.LaneSignName, new Vector3(60f, g0, 0f), EditorObjects.Upright(0f));
            T.Check("a lane sign places from the catalog", sign != null);
            if (sign == null) yield break;
            yield return Ticks(1);
            T.Check($"...reading 4 lanes off the road beneath it ({(int)sign.GetMeta("lane_count")})",
                    (int)sign.GetMeta("lane_count") == 4);
            T.Check($"...with every lane boarded by default ({Boards(sign)} boards, {Legends(sign).Count} legends)",
                    Boards(sign) == 4 && Legends(sign).Count == 4);

            // ⭐ AND THE BOARDS SIT OVER THEIR OWN LANES. A composition that put all four in one place would
            // pass a count check and be useless.
            var seen = new List<float>();
            foreach (var c in sign.GetChildren())
                if (c is MeshInstance3D m && m.Name.ToString().StartsWith("Board"))
                    seen.Add(Mathf.Round(m.Position.Z * 100f) / 100f);
            seen.Sort();
            float spread = seen.Count > 1 ? seen[seen.Count - 1] - seen[0] : 0f;
            T.Check($"...spread across the carriageway, not stacked ({spread:0.##} m between outer boards)",
                    seen.Count == 4 && spread > 9f);

            // ---- 2. TICKING A LANE OFF REMOVES ONLY THAT BOARD.
            objs.DebugSelect(sign);
            T.Check($"selecting it exposes {objs.SelectedLaneCount} lane rows", objs.LaneSignSelected && objs.SelectedLaneCount == 4);
            objs.SetSelectedLaneOn(1, false);
            objs.SetSelectedLaneOn(3, false);
            yield return Ticks(1);
            T.Check($"two lanes unticked leaves two boards ({Boards(sign)})", Boards(sign) == 2);
            // ⭐ astraclaw: "prefer preserving text for temporarily unchecked lanes."
            objs.SetSelectedLaneText(1, "KEEP ME");
            objs.SetSelectedLaneOn(1, true);
            yield return Ticks(1);
            objs.SetSelectedLaneOn(1, false);
            yield return Ticks(1);
            T.Check($"...and an unticked lane KEEPS its text (\"{objs.SelectedLaneText(1)}\")",
                    objs.SelectedLaneText(1) == "KEEP ME");
            T.Check($"zero lanes is allowed -- frame only, no ghost boards",
                    AllOff(objs, sign) && Boards(sign) == 0);

            // ---- 3. ⭐⭐ astraclaw's ACCEPTANCE CHECK, verbatim: lanes 1 and 3, different text, save and
            // reload, only those two boards return over the same lanes with their text intact.
            objs.SetSelectedLaneOn(0, true);
            objs.SetSelectedLaneOn(2, true);
            objs.SetSelectedLaneText(0, "LANE 1 City Centre");
            objs.SetSelectedLaneText(2, "LANE 3 Airport");
            yield return Ticks(1);
            var before = new List<float>();
            foreach (var c in sign.GetChildren())
                if (c is MeshInstance3D m && m.Name.ToString().StartsWith("Board")) before.Add(m.Position.Z);
            before.Sort();
            T.Check($"lanes 1 and 3 ticked -> two boards ({Boards(sign)})", Boards(sign) == 2);

            objs.DebugSaveLaneSigns();
            objs.RemovePlaced(new List<Node3D> { sign });
            yield return Ticks(1);
            int gone = 0;
            foreach (var _ in objs.PlacedOfNodes(EditorObjects.LaneSignName)) gone++;
            T.Check($"fixture: removed before reloading ({gone})", gone == 0);

            objs.DebugLoadLaneSigns();
            yield return Ticks(1);
            var back = new List<Node3D>();
            foreach (var n in objs.PlacedOfNodes(EditorObjects.LaneSignName)) back.Add(n);
            T.Check($"the sign reloads ({back.Count})", back.Count == 1);
            if (back.Count == 1)
            {
                T.Check($"...with ONLY the two chosen lanes boarded ({Boards(back[0])})", Boards(back[0]) == 2);
                var after = new List<float>();
                foreach (var c in back[0].GetChildren())
                    if (c is MeshInstance3D m && m.Name.ToString().StartsWith("Board")) after.Add(m.Position.Z);
                after.Sort();
                float off = 0f;
                for (int i = 0; i < Mathf.Min(before.Count, after.Count); i++) off = Mathf.Max(off, Mathf.Abs(before[i] - after[i]));
                T.Check($"...over the SAME lanes (worst {off * 1000f:0.#} mm drift)",
                        after.Count == before.Count && off < 0.01f);
                T.Check($"...each keeping its own text (\"{(string)back[0].GetMeta("lane_text_0")}\" / "
                      + $"\"{(string)back[0].GetMeta("lane_text_2")}\")",
                        (string)back[0].GetMeta("lane_text_0") == "LANE 1 City Centre"
                     && (string)back[0].GetMeta("lane_text_2") == "LANE 3 Airport");
            }
            try { System.IO.File.Delete(objs.DebugLaneSignPath); } catch { }
        }

        static bool AllOff(EditorObjects objs, Node3D sign)
        {
            for (int i = 0; i < objs.SelectedLaneCount; i++) objs.SetSelectedLaneOn(i, false);
            return objs.SelectedLaneMask == 0;
        }
    }
}
