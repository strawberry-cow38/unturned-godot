using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    /// <summary>Overhead highway signs with editable legends.
    ///
    /// Master 2026-10-09: "add some highway overhead signs" then "mount the signs directly on the pole
    /// without hanging them. allow custom text to be added". astraclaw supplied the art with the legend
    /// DELIBERATELY ABSENT -- blank green boards, two exclusive atlas regions -- because the prop loader
    /// gives a prop exactly one MaterialOverride and baked words cannot be changed downstream.
    ///
    /// ⭐ THE ASSET CONTRACT IS ASSERTED, NOT ASSUMED. Every number this feature depends on lives in
    /// astraclaw's mesh, not in our code, so a re-export that quietly breaks one would otherwise surface as
    /// a sign with its legend in the wrong place -- or in mid-air.</summary>
    public sealed class HighwaySignTests : GameTest
    {
        public override string Name => "editor.highway_signs";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor();
            World.AddChild(ed);
            var objs = new EditorObjects(ed, World, null);
            World.AddChild(objs);
            yield return Ticks(2);

            // ---- 0. THE ASSET CONTRACT. astraclaw: 78 triangles, 54 pole + 24 board, with the two faces in
            // exclusive UV regions below v=0.469 while the body stays at or above 0.5.
            var mesh = ContentProvider.ParseObj("res://content/objects/Highway_Overhead_Signs.obj");
            T.Check("the sign mesh loads", mesh != null && mesh.GetSurfaceCount() > 0);
            if (mesh == null) yield break;

            var arr = mesh.SurfaceGetArrays(0);
            var V = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var U = arr[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            T.Check($"it carries UVs for every vertex ({U.Length} of {V.Length})", U.Length == V.Length);

            int face = 0, body = 0;
            float faceMaxV = 0f, bodyMinV = 1f;
            for (int i = 0; i + 2 < V.Length; i += 3)
            {
                bool isFace = U[i].Y < 0.5f && U[i + 1].Y < 0.5f && U[i + 2].Y < 0.5f;
                for (int k = 0; k < 3; k++)
                    if (isFace) faceMaxV = Mathf.Max(faceMaxV, U[i + k].Y);
                    else bodyMinV = Mathf.Min(bodyMinV, U[i + k].Y);
                if (isFace) face++; else body++;
            }
            T.Check($"exactly two blank faces of two triangles, and the rest is support "
                  + $"({face} face / {body} body, want 4 / 74)", face == 4 && body == 74);
            // ⭐ THE MARGIN, not just the split. A predicate that happens to land between two values 0.001
            // apart is one re-export away from being wrong, and would carve the pole into the legend.
            T.Check($"...separated with room to spare (faces reach {faceMaxV:0.000}, body starts {bodyMinV:0.000})",
                    bodyMinV - faceMaxV > 0.05f);

            // ---- 1. PLACED, WITH A LEGEND ON EACH BOARD.
            var sign = objs.Place(EditorObjects.HighwaySignName, new Vector3(10f, 0f, -5f), EditorObjects.Upright(0f));
            T.Check("a sign places from the catalog", sign != null);
            if (sign == null) yield break;
            yield return Ticks(1);

            var labels = new List<Label3D>();
            foreach (var c in sign.GetChildren()) if (c is Label3D l) labels.Add(l);
            T.Check($"one legend per board ({labels.Count} labels, prop reports "
                  + $"{(sign.HasMeta("sign_boards") ? (int)sign.GetMeta("sign_boards") : -1)} boards)",
                    labels.Count == 2 && sign.HasMeta("sign_boards") && (int)sign.GetMeta("sign_boards") == 2);
            if (labels.Count != 2) yield break;
            T.Check($"...each carrying its default text (\"{labels[0].Text.Replace("\n", "/")}\", "
                  + $"\"{labels[1].Text.Replace("\n", "/")}\")",
                    labels[0].Text == EditorObjects.DefaultSignText(0)
                 && labels[1].Text == EditorObjects.DefaultSignText(1));

            // ⭐ AND SITTING ON THE BOARDS, which is the check that catches a wrong stand-up or a bad axis
            // mapping -- the legend would otherwise float beside the pole and still "exist".
            var mi = sign.GetChild<MeshInstance3D>(0);
            var boardsWorld = new List<Aabb>();
            for (int i = 0; i + 2 < V.Length; i += 3)
            {
                if (!(U[i].Y < 0.5f && U[i + 1].Y < 0.5f && U[i + 2].Y < 0.5f)) continue;
                var b = new Aabb(mi.GlobalTransform * V[i], Vector3.Zero)
                            .Expand(mi.GlobalTransform * V[i + 1]).Expand(mi.GlobalTransform * V[i + 2]);
                int hit = -1;
                for (int k = 0; k < boardsWorld.Count; k++)
                    if (boardsWorld[k].Grow(0.5f).Intersects(b)) { hit = k; break; }
                if (hit < 0) boardsWorld.Add(b); else boardsWorld[hit] = boardsWorld[hit].Merge(b);
            }
            T.Check($"fixture: two board volumes in world space ({boardsWorld.Count})", boardsWorld.Count == 2);
            if (boardsWorld.Count == 2)
            {
                bool seated = true;
                float worst = 0f;
                foreach (var l in labels)
                {
                    float best = float.MaxValue;
                    foreach (var b in boardsWorld)
                        best = Mathf.Min(best, (l.GlobalPosition - b.GetCenter()).Length());
                    worst = Mathf.Max(worst, best);
                    if (best > 0.2f) seated = false;
                }
                T.Check($"each legend sits on its board's centre (worst {worst * 100f:0.#} cm off)", seated);

                // ⭐ AND FACES OUT OF THE BOARD, not into the mast behind it.
                var outward = -sign.GlobalTransform.Basis.X.Normalized();   // boards look down the prop's -X
                bool facing = true;
                foreach (var l in labels)
                    if (l.GlobalTransform.Basis.Z.Normalized().Dot(outward) < 0.9f) facing = false;
                T.Check("...and faces outward, away from the mast", facing);
            }

            // ---- 2. THE TEXT IS EDITABLE, which is the whole request.
            objs.DebugSelect(sign);
            yield return Ticks(1);
            T.Check($"selecting a sign exposes its legend fields ({objs.SelectedSignBoards} board(s))",
                    objs.SignSelected && objs.SelectedSignBoards == 2);
            // ⭐ CONTROL: a board index the asset does not have must be REFUSED, not silently stored --
            // the board count comes from the mesh, so a four-field UI on a two-board sign must write nothing.
            objs.SetSelectedSignText(5, "GHOST");
            T.Check("control: writing to a board this sign lacks is refused",
                    objs.SelectedSignText(5) == "");
            objs.SetSelectedSignText(0, "SOUTH\\nHarbour");
            objs.SetSelectedSignText(1, "EAST\\nFerry Terminal");
            T.Check($"board 1 takes new text (\"{objs.SelectedSignText(0)}\")",
                    objs.SelectedSignText(0) == "SOUTH\\nHarbour" && labels[0].Text == "SOUTH\\nHarbour");
            T.Check($"board 2 takes its own, independently (\"{objs.SelectedSignText(1)}\")",
                    objs.SelectedSignText(1) == "EAST\\nFerry Terminal" && labels[1].Text == "EAST\\nFerry Terminal");

            // ⚠ A TAB WOULD SPLIT THE SAVE ROW and corrupt every sign after it. Control for the sanitiser.
            objs.SetSelectedSignText(0, "A\tB");
            T.Check($"a tab in a legend is neutralised, not written through (\"{objs.SelectedSignText(0)}\")",
                    !objs.SelectedSignText(0).Contains('\t'));
            objs.SetSelectedSignText(0, "SOUTH\\nHarbour");

            // ---- 3. IT SURVIVES A SAVE. A legend contains SPACES, so the space-separated layout the other
            // editable props use would have split "Ferry Terminal" across two fields.
            objs.DebugSaveSigns();
            T.Check($"the sidecar is written ({System.IO.Path.GetFileName(objs.DebugSignPath)})",
                    System.IO.File.Exists(objs.DebugSignPath));
            // ⚠ COUNT BEFORE REMOVING TOO. The first version only asserted "0 left", which passed while the
            // lookup was returning nothing at all -- the sign had no obj_name meta, so it was never findable
            // and the reload check below was the only thing that noticed.
            int before = 0;
            foreach (var _ in objs.PlacedOfNodes(EditorObjects.HighwaySignName)) before++;
            T.Check($"fixture: the placed sign IS findable before removal ({before})", before == 1);
            objs.RemovePlaced(new List<Node3D> { sign });
            yield return Ticks(1);
            int after = 0;
            foreach (var _ in objs.PlacedOfNodes(EditorObjects.HighwaySignName)) after++;
            T.Check($"fixture: removed before reloading ({after} left)", after == 0);

            objs.DebugLoadSigns();
            yield return Ticks(1);
            var loaded = new List<Node3D>();
            foreach (var n in objs.PlacedOfNodes(EditorObjects.HighwaySignName)) loaded.Add(n);
            T.Check($"one sign comes back ({loaded.Count})", loaded.Count == 1);
            if (loaded.Count == 1)
            {
                string r0 = (string)loaded[0].GetMeta("sign_text_0");
                string r1 = (string)loaded[0].GetMeta("sign_text_1");
                T.Check($"...with BOTH legends intact, spaces and all (\"{r0}\" / \"{r1}\")",
                        r0 == "SOUTH\\nHarbour" && r1 == "EAST\\nFerry Terminal");
                var rl = new List<Label3D>();
                foreach (var c in loaded[0].GetChildren()) if (c is Label3D l) rl.Add(l);
                T.Check($"...and the boards show them, not the defaults",
                        rl.Count == 2 && rl[0].Text == "SOUTH\\nHarbour" && rl[1].Text == "EAST\\nFerry Terminal");
            }
            try { System.IO.File.Delete(objs.DebugSignPath); } catch { }
        }
    }
}
