using Godot;
using System.Collections.Generic;
using System.Linq;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    /// <summary>A generated building's curtains and rugs BUILD, at the sizes it saved them at, where it put them -- and
    /// still do after a save and a load (round 12c: astraclaw's runtime factories, RuntimeDecorGenerators, placed by
    /// BuildingGen's FurnishTextiles and saved as their inputs on WallSave's prop lines).
    ///
    /// ⚠ THE CORE PLACES WHAT THE GAME BUILDS, ACROSS A BOUNDARY NEITHER SIDE CHECKS. The generator knows the rail's height
    /// and the curtain's reach only as RuntimeDecor's copies of the factory's numbers (CurtainRailOver, CurtainRailOverCloth,
    /// CurtainCapOverRail), and the game turns a plan into a transform its own way (RuntimeDecorTransform). Both are green
    /// in their own suites while the rail floats in the window or a rug stands on its edge, so this reads the MESHES the
    /// editor actually built: their extents against the saved inputs, and the rail's fittings -- found in the geometry, not
    /// in the constants -- against the window it hangs at.</summary>
    public sealed class RuntimeDecorTests : GameTest
    {
        public override string Name => "decor.runtime_curtains_and_rugs";
        public override double TimeoutSimSeconds => 120;

        static string SavePath => ProjectSettings.GlobalizePath("res://content/buildings/") + "editor_none_Walls.dat";

        public override IEnumerable<Step> Run()
        {
            if (System.IO.File.Exists(SavePath)) System.IO.File.Delete(SavePath);
            var ed = new Editor();
            World.AddChild(ed);
            var tool = new EditorBuildings();
            World.AddChild(tool);
            tool.Setup(ed, null, null);
            yield return Ticks(1);

            // a single storey (a rail over every window: the roof leaves room) and two (the ground floor's windows reach the
            // floor above's underside: a rail at the ceiling)
            int above = 0, ceilinged = 0;
            foreach (int seed in new[] { 16, 23 })
            {
                var res = tool.Generate(new BuildingGenSpec { Kind = BuildingKind.Residential, Seed = seed });
                yield return Ticks(1);
                var at = EditorBuildings.StageOrigin + new Vector3(-Mathf.Floor(res.CellsX * 0.5f) * BuildingGen.Cell, EditorBuildings.GroundClearance,
                                                                   -Mathf.Floor(res.CellsZ * 0.5f) * BuildingGen.Cell);
                int curtains = res.RuntimeDecor.Count(m => m.Gen.Kind == RuntimeDecorKind.Curtain), rugs = res.RuntimeDecor.Count - curtains;
                T.Check($"Residential {seed}: the generator hung curtains ({curtains}) and laid a rug ({rugs})", curtains > 0 && rugs > 0);
                var gen = tool.Furniture.Where(p => p.Gen != null).ToList();
                T.Check($"...and the stage carries all {res.RuntimeDecor.Count} as runtime pieces ({gen.Count})", gen.Count == res.RuntimeDecor.Count);
                foreach (var c in res.RuntimeDecor)
                {
                    var p = gen.FirstOrDefault(q => q.Name == c.Name && Mathf.Abs(q.X - (at.X + c.X)) < 1e-3f && Mathf.Abs(q.Z - (at.Z + c.Z)) < 1e-3f);
                    var mi = Node(tool, p);
                    if (!T.CheckNode($"Residential {seed}: {c.Name} at ({c.X:0.00},{c.Z:0.00}) built a mesh", mi)) continue;
                    if (c.Gen.Kind == RuntimeDecorKind.Curtain) CheckCurtain(res, at, c, p, mi, ref above, ref ceilinged);
                    else CheckRug(at, c, p, mi);
                }
            }
            T.Check($"curtains with their rail over the window ({above}) and at the ceiling of a storey under another ({ceilinged})", above > 0 && ceilinged > 0);

            // the HVAC record a room temperature system reads (round 12): asked of the stage as the editor holds it -- the rooms
            // found from the walls standing there, the labels anchored in them -- from the middle of every room, before the save
            // and after it
            var hvacRes = tool.Generate(new BuildingGenSpec { Kind = BuildingKind.Residential, Seed = 16 });
            yield return Ticks(1);
            var hat = EditorBuildings.StageOrigin + new Vector3(-Mathf.Floor(hvacRes.CellsX * 0.5f) * BuildingGen.Cell, EditorBuildings.GroundClearance,
                                                                -Mathf.Floor(hvacRes.CellsZ * 0.5f) * BuildingGen.Cell);
            string Ids(RoomHvac h) => h == null ? "none" : string.Join(",", h.Vents.Select(v => $"{v.Kind}#{v.Id}"));
            var asked = hvacRes.Layout.Select(g => (G: g, X: hat.X + (g.X0 + g.X1) * 0.5f * BuildingGen.Cell, Z: hat.Z + (g.Z0 + g.Z1) * 0.5f * BuildingGen.Cell)).ToList();
            int rightBefore = asked.Count(q => Ids(tool.HvacAt(q.X, q.Z, q.G.Floor)) == Ids(q.G.Hvac));
            T.Check($"the stage answers HvacAt with each room's own record ({rightBefore} of {asked.Count} rooms, {asked.Count(q => q.G.Hvac != null)} with HVAC)",
                    rightBefore == asked.Count && asked.Any(q => q.G.Hvac != null));
            var vent = hvacRes.Layout.First(g => g.Hvac != null).Hvac.Vents.First();
            var got = tool.HvacAt(asked.First(q => q.G.Hvac != null).X, asked.First(q => q.G.Hvac != null).Z, 0)?.Vents.First() ?? default;
            T.Check($"...its vents where the stage stands them ({got.X - hat.X:0.00},{got.Z - hat.Z:0.00} for {vent.X:0.00},{vent.Z:0.00})",
                    Mathf.Abs(got.X - (hat.X + vent.X)) < 1e-3f && Mathf.Abs(got.Z - (hat.Z + vent.Z)) < 1e-3f);
            T.Check("saved with the HVAC records", tool.Save() > 0);
            var tool3 = new EditorBuildings();
            World.AddChild(tool3);
            tool3.Setup(ed, null, null);
            yield return Ticks(1);
            int rightAfter = asked.Count(q => Ids(tool3.HvacAt(q.X, q.Z, q.G.Floor)) == Ids(q.G.Hvac));
            T.Check($"...and a load answers it the same ({rightAfter} of {asked.Count})", rightAfter == asked.Count);
            tool3.QueueFree();
            // (back to the two-storey building the save and load below are of)
            tool.Generate(new BuildingGenSpec { Kind = BuildingKind.Residential, Seed = 23 });
            yield return Ticks(1);

            // saved, and loaded into a second tool as opening the map again would: the same pieces, the same meshes
            var before = tool.Furniture.Where(p => p.Gen != null).ToList();
            var sizes = before.Select(p => Node(tool, p)?.GetAabb().Size ?? Vector3.Zero).ToList();
            T.Check("saved", tool.Save() > 0);
            var tool2 = new EditorBuildings();
            World.AddChild(tool2);
            tool2.Setup(ed, null, null);
            yield return Ticks(1);
            var after = tool2.Furniture.Where(p => p.Gen != null).ToList();
            T.Check($"the load has every curtain and rug back ({after.Count} of {before.Count})", after.Count == before.Count);
            int same = 0;
            for (int i = 0; i < System.Math.Min(before.Count, after.Count); i++)
            {
                var a = before[i]; var b = after[i];
                var mb = Node(tool2, b);
                if (a.Gen.Kind == b.Gen.Kind && a.Gen.Open == b.Gen.Open && a.Gen.Style == b.Gen.Style && Mathf.Abs(a.Gen.Width - b.Gen.Width) < 1e-3f
                    && Mathf.Abs(a.Gen.Height - b.Gen.Height) < 1e-3f && mb != null && (mb.GetAabb().Size - sizes[i]).Length() < 2e-3f
                    && (mb.GlobalPosition - EditorBuildings.RuntimeDecorTransform(a).Origin).Length() < 2e-3f) same++;
            }
            T.Check($"...each built again from what it saved, the same size in the same place ({same} of {before.Count})", same == before.Count && same > 0);
            if (System.IO.File.Exists(SavePath)) System.IO.File.Delete(SavePath);
        }

        /// <summary>The node the editor built for a runtime piece: the one standing where RuntimeDecorTransform puts it. (By
        /// place, not by name: siblings of one name -- every curtain is "Furniture_Curtain" -- are renamed by the tree.)</summary>
        static MeshInstance3D Node(EditorBuildings tool, PropDesignation p)
        {
            if (p.Gen == null) return null;
            var want = EditorBuildings.RuntimeDecorTransform(p).Origin;
            return tool.FurnitureNodes.OfType<MeshInstance3D>().FirstOrDefault(n => IsInstanceValid(n) && (n.GlobalPosition - want).Length() < 1e-3f);
        }
        static bool IsInstanceValid(GodotObject o) => GodotObject.IsInstanceValid(o);

        void CheckCurtain(BuildingGenResult res, Vector3 at, FurniturePlan c, PropDesignation p, MeshInstance3D mi, ref int above, ref int ceilinged)
        {
            string nm = $"curtain at ({c.X:0.00},{c.Z:0.00}) floor {c.Floor}, {c.Gen.Width:0.00} x {c.Gen.Height:0.00}";
            var local = mi.GetAabb();
            // the saved sizes: the fabric's width and the rail's 0.14 past it each side; the drop and the fittings' 0.13 over it
            T.Check($"{nm}: {local.Size.X:0.000} m across, rail to rail (width + 0.28)", Mathf.Abs(local.Size.X - (c.Gen.Width + 2f * RuntimeDecor.CurtainRailOver)) < 0.01f);
            T.Check($"...{local.Size.Y:0.000} m tall (drop + 0.13)", Mathf.Abs(local.Size.Y - (c.Gen.Height + RuntimeDecor.CurtainRailOverCloth + RuntimeDecor.CurtainCapOverRail)) < 0.01f);
            T.Check($"...{local.Size.Z:0.000} m out of the wall at most (CurtainDepth {RuntimeDecor.CurtainDepth})", local.Size.Z <= RuntimeDecor.CurtainDepth + 0.005f);
            // the rail, found in the GEOMETRY: the end caps are the vertices out past the fabric, and their middle is the rail's
            var verts = mi.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            // (past the rail's own end, half + 0.10: the caps' outer faces, half + 0.14, symmetric about the rail's middle)
            var caps = verts.Where(v => Mathf.Abs(v.X) > c.Gen.Width * 0.5f + 0.11f).ToList();
            if (!T.CheckNode($"...its rail's end caps are in the mesh ({caps.Count} vertices)", caps.Count > 0 ? mi : null)) return;
            var xf = mi.GlobalTransform;
            float railY = caps.Select(v => (xf * v).Y).Average(), capTop = caps.Max(v => (xf * v).Y), clothBottom = verts.Min(v => (xf * v).Y);
            T.Check($"...its rail at {railY - at.Y:0.000} m, where the generator put it ({c.Y:0.000})", Mathf.Abs(railY - (at.Y + c.Y)) < 0.01f);
            T.Check($"...its hem at {clothBottom - at.Y:0.000} m, the drop under the cloth's top ({c.Y - RuntimeDecor.CurtainRailOverCloth - c.Gen.Height:0.000})",
                    Mathf.Abs(clothBottom - (at.Y + c.Y - RuntimeDecor.CurtainRailOverCloth - c.Gen.Height)) < 0.01f);
            // the curtain's back on the wall, its front into the room: every vertex on the room side of its back plane
            var front = new Vector3(-Mathf.Sin(Mathf.DegToRad(c.Yaw)), 0f, -Mathf.Cos(Mathf.DegToRad(c.Yaw)));
            var back = at + new Vector3(c.X, 0f, c.Z);
            T.Check("...standing out of the wall into the room, none of it behind its back", verts.All(v => (xf * v - back).Dot(front) > -0.005f));
            // the window it hangs at, from the walls as generated
            WallPlan win = null; WallOpening wo = default;
            foreach (var w in res.Walls.Where(w => w.Kind == SurfaceKind.Wall && Mathf.RoundToInt(w.Y / BuildingGen.Storey) == c.Floor))
            {
                var (cs, sn) = PlanTrig.CosSin(w.Yaw); float dx = cs, dz = -sn;
                foreach (var o in w.Openings.Where(o => o.V >= 0.05f))
                {
                    float mx = w.X + dx * (o.U + o.Width * 0.5f), mz = w.Z + dz * (o.U + o.Width * 0.5f);
                    float along = Mathf.Abs((mx - c.X) * front.Z * -1f + (mz - c.Z) * front.X), into = (mx - c.X) * front.X + (mz - c.Z) * front.Z;
                    if (along < c.Gen.Width * 0.5f && into < 0f && into > -w.Thickness) { win = w; wo = o; }
                }
            }
            if (!T.CheckNode($"...at a window", win != null ? mi : null)) return;
            float head = at.Y + win.Y + wo.V + wo.Height;
            // across the whole opening: the cloth's run along the wall covers it
            var (wc, ws) = PlanTrig.CosSin(win.Yaw); var dir = new Vector3(wc, 0f, -ws);
            float u0 = float.MaxValue, u1 = float.MinValue;
            foreach (var v in verts.Where(v => Mathf.Abs(v.X) <= c.Gen.Width * 0.5f + 1e-3f))
            {
                float u = (xf * v - (at + new Vector3(win.X, 0f, win.Z))).Dot(dir);
                u0 = Mathf.Min(u0, u); u1 = Mathf.Max(u1, u);
            }
            T.Check($"...its cloth runs {u0:0.00}..{u1:0.00} along the wall, over the window's {wo.U:0.00}..{wo.U1:0.00}", u0 <= wo.U + 0.01f && u1 >= wo.U1 - 0.01f);
            bool under = c.Floor < res.Floors - 1;
            if (!under)
            {
                // a storey with nothing over it: the whole rail, caps and all, over the window's head
                above++;
                T.Check($"...its rail sits over the window: its fittings from {capTop - 2f * RuntimeDecor.CurtainCapOverRail - at.Y:0.000} m, the head at {head - at.Y:0.000}",
                        capTop - 2f * RuntimeDecor.CurtainCapOverRail > head + 0.005f);
            }
            else
            {
                // under the floor above: the window's head IS its underside, so the rail is at the ceiling, its caps under it
                ceilinged++;
                float ceiling = at.Y + (c.Floor + 1) * BuildingGen.Storey - BuildingGen.Slab;
                T.Check($"...under the floor above (head {head - at.Y:0.00}, ceiling {ceiling - at.Y:0.00}): its caps at {capTop - at.Y:0.000}, at the ceiling and under it",
                        capTop <= ceiling + 1e-3f && capTop >= ceiling - 0.05f);
            }
        }

        void CheckRug(Vector3 at, FurniturePlan c, PropDesignation p, MeshInstance3D mi)
        {
            string nm = $"rug at ({c.X:0.00},{c.Z:0.00}), {c.Gen.Width:0.00} x {c.Gen.Height:0.00} {c.Gen.Style}";
            var world = mi.GlobalTransform * mi.GetAabb();
            var fp = c.Footprint;
            T.Check($"{nm}: {world.Size.X:0.000} x {world.Size.Z:0.000} m on the floor, as its plan ({fp.X1 - fp.X0:0.000} x {fp.Z1 - fp.Z0:0.000})",
                    Mathf.Abs(world.Size.X - (fp.X1 - fp.X0)) < 0.01f && Mathf.Abs(world.Size.Z - (fp.Z1 - fp.Z0)) < 0.01f);
            T.Check($"...flat ({world.Size.Y:0.0000} m thick) and just over its floor ({world.Position.Y - at.Y - c.Y:0.000} m)",
                    world.Size.Y < 0.001f && Mathf.Abs(world.Position.Y - (at.Y + c.Y + RuntimeDecor.RugLift)) < 0.003f);
            T.Check($"...where it was planned", Mathf.Abs(world.Position.X - (at.X + fp.X0)) < 0.01f && Mathf.Abs(world.Position.Z - (at.Z + fp.Z0)) < 0.01f);
            T.Check($"...in its pattern", p.Gen.Style == c.Gen.Style && mi.Mesh.SurfaceGetMaterial(0) is BaseMaterial3D m && m.AlbedoTexture != null);
        }
    }

    static class TestContextNodeExt
    {
        /// <summary>Check that a node was found, and say so: false when it was not, so the caller can stop reading it.</summary>
        public static bool CheckNode(this TestContext t, string desc, Node n) { t.Check(desc, n != null); return n != null; }
    }
}
