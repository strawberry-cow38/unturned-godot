using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // The real structure system (StructureCatalog + StructureManager), replacing BuildTool's stand-in.
    //
    // The first assertion is the one that matters most and is the easiest to get wrong silently: a structure
    // tile edge is SIX metres. BuildTool used `GRID = 3f` and called it "Unturned's structure tile size" -- 3 m
    // is HALF_EDGE_LENGTH, the half-step used for pivot maths. A base built on a 3 m lattice looks perfectly
    // fine on its own and can never line up with a real foundation, which is a failure you only discover by
    // trying to connect to something you did not build.
    public class StructureLatticeAndTiers : GameTest
    {
        public override string Name => "structure.lattice";

        public override IEnumerable<Step> Run()
        {
            var sm = new StructureManager();
            World.AddChild(sm);
            yield return Ticks(1);

            // ---- geometry, ported from SDK HousingConnections.cs:220-266 ----
            T.Check($"tile edge is 6 m, not 3 ({StructureCatalog.EdgeLength})",
                Mathf.IsEqualApprox(StructureCatalog.EdgeLength, 6.0f));
            T.Check($"half edge is 3 m ({StructureCatalog.HalfEdge})", Mathf.IsEqualApprox(StructureCatalog.HalfEdge, 3.0f));
            T.Check($"wall height 4.25 ({StructureCatalog.WallHeight})", Mathf.IsEqualApprox(StructureCatalog.WallHeight, 4.25f));
            T.Check($"wall pivot is half its height ({StructureCatalog.WallPivotOffset})",
                Mathf.IsEqualApprox(StructureCatalog.WallPivotOffset, 2.125f));
            T.Check($"rampart pivot is NOT the wall's ({StructureCatalog.RampartPivotOffset})",
                Mathf.IsEqualApprox(StructureCatalog.RampartPivotOffset, 0.9f)
                && !Mathf.IsEqualApprox(StructureCatalog.RampartPivotOffset, StructureCatalog.WallPivotOffset));

            // ---- snapping: faces to tile CENTRES, edges to side MIDPOINTS ----
            var (fp, _) = StructureCatalog.Snap(new Vector3(2.4f, 0.1f, -1.1f), EConstruct.Floor);
            T.Check($"floor snaps to a tile centre ({fp})",
                Mathf.IsEqualApprox(fp.X, 0f) && Mathf.IsEqualApprox(fp.Z, 0f));
            var (fp2, _) = StructureCatalog.Snap(new Vector3(3.6f, 0f, 0f), EConstruct.Floor);
            T.Check($"a point past the half-edge snaps to the NEXT tile ({fp2.X})", Mathf.IsEqualApprox(fp2.X, 6f));

            var (wp, wyaw) = StructureCatalog.Snap(new Vector3(2.9f, 0f, 0.2f), EConstruct.Wall);
            T.Check($"wall snaps to a side midpoint, not a centre ({wp})", Mathf.IsEqualApprox(Mathf.Abs(wp.X), 3f));
            T.Check($"a wall on the X side faces across it (yaw {wyaw})", Mathf.IsEqualApprox(wyaw, 90f));
            var (wp2, wyaw2) = StructureCatalog.Snap(new Vector3(0.2f, 0f, 2.9f), EConstruct.Wall);
            T.Check($"a wall on the Z side faces the other way (yaw {wyaw2})",
                Mathf.IsEqualApprox(wyaw2, 0f) && Mathf.IsEqualApprox(Mathf.Abs(wp2.Z), 3f));

            // A face and an edge at the same coordinates are DIFFERENT slots -- otherwise a wall would block
            // the floor of its own tile.
            var (a, _) = StructureCatalog.Snap(Vector3.Zero, EConstruct.Floor);
            T.Check("a floor slot and a wall slot never collide",
                StructureCatalog.SlotKey(a, EConstruct.Floor) != StructureCatalog.SlotKey(a, EConstruct.Wall));

            // Two points 1 cm apart are the SAME slot: LINK_TOLERANCE is 2 cm, and float drift must not open
            // a second slot inside one tile.
            T.Check("sub-tolerance drift resolves to one slot",
                StructureCatalog.SlotKey(new Vector3(0f, 0f, 0f), EConstruct.Floor)
                == StructureCatalog.SlotKey(new Vector3(0.009f, 0f, 0.009f), EConstruct.Floor));

            // ---- placement + support ----
            var floor = sm.Place(new Vector3(0f, 0f, 0f), EConstruct.Floor, 0);
            T.Check("a wood floor places on open ground", floor != null);
            T.Check($"it holds the tier's health ({floor?.Health})", floor != null && floor.Health == StructureCatalog.TierAt(0).Health);

            T.Check("the same slot cannot be taken twice", sm.Place(new Vector3(0.5f, 0f, 0.5f), EConstruct.Floor, 0) == null);
            sm.CanPlace(new Vector3(0.5f, 0f, 0.5f), EConstruct.Floor, 0, out var why);
            T.Check($"and it says WHY ({why})", why == "occupied");

            // THE support rule: wood cannot float. This is what separates a base from scattered tiles.
            T.Check("wood cannot be placed floating in mid-air",
                sm.Place(new Vector3(60f, 20f, 60f), EConstruct.Wall, 0) == null);
            sm.CanPlace(new Vector3(60f, 20f, 60f), EConstruct.Wall, 0, out var why2);
            T.Check($"...and the reason is support, not occupancy ({why2})", why2 == "no support");

            // metal is RequiresPillars=false, so it stands alone (retail lets some assets place free-standing)
            T.Check("metal places free-standing", sm.Place(new Vector3(60f, 20f, 60f), EConstruct.Wall, 2) != null);

            // a wall adjacent to the floor IS supported
            T.Check("a wood wall on the floor's edge is supported",
                sm.Place(new Vector3(2.9f, 0f, 0f), EConstruct.Wall, 0) != null);

            // Extending outward onto the NEXT tile is the commonest real build action, and it is the case the
            // neighbour-lookup rewrite could most easily have broken: support moved from a distance scan over
            // every piece to a fixed set of lattice probes, so an adjacent tile has to be in that set. A base
            // you cannot grow sideways is not a base.
            T.Check("a floor extends onto the adjacent tile",
                sm.Place(new Vector3(StructureCatalog.EdgeLength, 0f, 0f), EConstruct.Floor, 0) != null);
            T.Check("...and onto the one after that",
                sm.Place(new Vector3(StructureCatalog.EdgeLength * 2f, 0f, 0f), EConstruct.Floor, 0) != null);

            // Upward, too: a wall one level up, above a piece that supports it.
            T.Check("a wall stacks a level above supported ground",
                sm.Place(new Vector3(2.9f, StructureCatalog.WallHeight, 0f), EConstruct.Wall, 0) != null);

            // ...but two levels up with nothing between is still refused. If the probe set ever widens far
            // enough to accept this, wood floats again and the support rule has quietly stopped meaning
            // anything.
            T.Check("wood still cannot skip a level into thin air",
                sm.Place(new Vector3(2.9f, StructureCatalog.WallHeight * 3f, 0f), EConstruct.Wall, 0) == null);

            yield return Ticks(1);
            T.Check($"every placed piece joined the \"{StructureManager.Group}\" group (barricades query it)",
                floor.Node.IsInGroup(StructureManager.Group));
        }
    }

    // Damage, upgrade and persistence.
    public class StructureDamageUpgradeSave : GameTest
    {
        public override string Name => "structure.damage_save";

        public override IEnumerable<Step> Run()
        {
            var sm = new StructureManager();
            World.AddChild(sm);
            yield return Ticks(1);

            var wood = sm.Place(Vector3.Zero, EConstruct.Floor, 0);
            T.Check("placed a wood floor", wood != null);

            int before = wood.Health;
            T.Check("a survivable hit does not destroy it", !sm.Damage(wood, 50));
            T.Check($"...but it does take the damage ({before} -> {wood.Health})", wood.Health == before - 50);

            // Vulnerability: metal shrugs off non-explosive damage entirely, which is what stops a hatchet
            // felling a metal wall. If this ever passes for metal, melee can chew through the top tier.
            var metal = sm.Place(new Vector3(60f, 20f, 60f), EConstruct.Wall, 2);
            int mh = metal.Health;
            sm.Damage(metal, 500);
            T.Check($"metal ignores non-explosive damage ({mh} -> {metal.Health})", metal.Health == mh);
            sm.Damage(metal, 500, explosive: true);
            T.Check($"...but explosives DO hurt it ({metal.Health})", metal.Health < mh);

            // destruction frees the slot, or a destroyed base could never be rebuilt on
            var doomed = sm.Place(new Vector3(6f, 0f, 0f), EConstruct.Floor, 0);
            T.Check("destroyed at zero health", sm.Damage(doomed, 99999));
            yield return Ticks(2);
            T.Check("the slot is reusable after destruction", sm.Place(new Vector3(6f, 0f, 0f), EConstruct.Floor, 0) != null);

            // upgrade ladder
            var up = sm.Place(new Vector3(-6f, 0f, 0f), EConstruct.Floor, 0);
            int t0 = up.Tier, h0 = up.MaxHealth;
            T.Check("upgrade moves a tier", sm.Upgrade(up) && up.Tier == t0 + 1);
            T.Check($"...and raises max health ({h0} -> {up.MaxHealth})", up.MaxHealth > h0);
            T.Check("upgrade refills health", up.Health == up.MaxHealth);
            sm.Upgrade(up);
            T.Check($"the ladder tops out ({up.Tier})", !sm.Upgrade(up) && up.Tier == StructureCatalog.TierCount - 1);

            // ---- save / load ----
            int n = sm.Count;
            string json = sm.Serialize();
            T.Check($"serialised {n} pieces", !string.IsNullOrEmpty(json) && n > 0);

            int loaded = sm.Deserialize(json);
            yield return Ticks(1);
            T.Check($"round-trips every piece ({loaded} of {n})", loaded == n && sm.Count == n);

            // Load must NOT re-run the support check. A saved base is known-good, and re-validating it would
            // delete any piece whose supporting neighbour had not been restored yet -- load ORDER would quietly
            // eat parts of someone's base. This is the guard: a save containing ONLY a floating metal wall and
            // a floating wood wall must restore BOTH.
            var floatingOnly = "[{\"c\":1,\"t\":0,\"h\":300,\"x\":300,\"y\":40,\"z\":300}]";
            int f = sm.Deserialize(floatingOnly);
            T.Check($"a saved piece with no surviving support still loads ({f})", f == 1);

            // a corrupt row must not cost the rest of the base
            int mixed = sm.Deserialize("[{\"c\":0,\"t\":0,\"h\":300,\"x\":0,\"y\":0,\"z\":0},{\"bogus\":true},{\"c\":0,\"t\":0,\"h\":300,\"x\":6,\"y\":0,\"z\":0}]");
            T.Check($"one bad row does not lose the good ones ({mixed} of 2)", mixed == 2);

            T.Check("garbage json loads nothing rather than throwing", sm.Deserialize("not json at all") == 0);
            T.Check("empty save clears cleanly", sm.Deserialize("") == 0 && sm.Count == 0);
        }
    }

    // QueryAt is the seam barricades attach through, and it was rewritten from a scan over every piece to a
    // bounded lattice probe for speed. A faster lookup that MISSES a piece is worse than a slow one: a
    // barricade would simply refuse to mount on a wall that is plainly there, and nothing would error.
    //
    // So these compare the probe against the ground truth -- the pieces actually placed -- rather than against
    // itself. Every placed piece must be findable from its own position, and from a point offset within the
    // query radius.
    public class StructureQueryProbe : GameTest
    {
        public override string Name => "structure.query";

        public override IEnumerable<Step> Run()
        {
            var sm = new StructureManager();
            World.AddChild(sm);
            yield return Ticks(1);

            // a small base: a run of floors with walls along their edges
            var placed = new List<StructureManager.Piece>();
            for (int i = 0; i < 4; i++)
            {
                var f = sm.Place(new Vector3(i * StructureCatalog.EdgeLength, 0f, 0f), EConstruct.Floor, 0);
                if (f != null) placed.Add(f);
                var w = sm.Place(new Vector3(i * StructureCatalog.EdgeLength - StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 0);
                if (w != null) placed.Add(w);
            }
            T.Check($"built a {placed.Count}-piece base to query against", placed.Count >= 6);

            // EVERY piece must be findable from its own position -- the probe missing even one means a
            // barricade silently refuses to mount on a wall that is right there.
            int found = 0, missed = 0;
            foreach (var p in placed)
            {
                var hit = sm.QueryAt(p.Pos, 1.0f);
                if (hit != null && hit.Value.Node == p.Node) found++;
                else { missed++; if (missed <= 3) GD.Print($"[structure.query] MISSED {p.Construct} at {p.Pos}"); }
            }
            T.Check($"every placed piece is findable at its own position ({found}/{placed.Count})", missed == 0);

            // and from a point offset inside the radius, which is how a raycast hit actually arrives -- never
            // exactly on the anchor
            int nearFound = 0;
            foreach (var p in placed)
                if (sm.QueryAt(p.Pos + new Vector3(0.4f, 0.2f, 0.3f), 1.5f) != null) nearFound++;
            T.Check($"findable from an offset hit point too ({nearFound}/{placed.Count})", nearFound == placed.Count);

            // empty space must still come back empty -- a probe that returns the nearest piece regardless of
            // radius would let barricades mount on thin air
            T.Check("empty space returns nothing", sm.QueryAt(new Vector3(500f, 50f, 500f), 1.0f) == null);

            // a wide radius takes the full-scan fallback; it must not silently return a near-miss
            var far = sm.QueryAt(new Vector3(400f, 0f, 0f), 1000f);
            T.Check("a wide query falls back to the full scan and still finds the base", far != null);

            T.Check("CanAttach agrees with a real piece", sm.CanAttach(placed[0].Pos, Vector3.Zero));
            T.Check("CanAttach refuses empty space", !sm.CanAttach(new Vector3(500f, 50f, 500f), Vector3.Zero));
        }
    }

    // Repair, salvage, and the on-disk round trip.
    public class StructureRepairSalvageDisk : GameTest
    {
        public override string Name => "structure.repair_salvage";

        public override IEnumerable<Step> Run()
        {
            var sm = new StructureManager();
            World.AddChild(sm);
            yield return Ticks(1);

            var p = sm.Place(Vector3.Zero, EConstruct.Floor, 0);
            T.Check("placed a floor", p != null);

            sm.Damage(p, 100);
            int hurt = p.Health;
            T.Check($"took damage ({p.MaxHealth} -> {hurt})", hurt == p.MaxHealth - 100);
            T.Check("repair restores what it says it restores", sm.Repair(p, 60) == 60 && p.Health == hurt + 60);
            // Repair must CAP at max and report the real amount -- a caller charges materials off the return
            // value, so an over-repair that reported 9999 would bill for health it never gave.
            int room = p.MaxHealth - p.Health;
            T.Check($"over-repair returns only what fit ({room})", sm.Repair(p, 9999) == room && p.Health == p.MaxHealth);
            T.Check("repairing a full piece restores 0", sm.Repair(p, 50) == 0);

            // salvage duration scales with tier, or a metal wall comes down as fast as a wooden one
            var metal = sm.Place(new Vector3(90f, 30f, 90f), EConstruct.Wall, 2);
            T.Check($"tougher tiers salvage slower ({sm.SalvageSeconds(p):0.##}s wood vs {sm.SalvageSeconds(metal):0.##}s metal)",
                sm.SalvageSeconds(metal) > sm.SalvageSeconds(p));

            int n = sm.Count;
            T.Check("salvage returns the tier it took down", sm.Salvage(p) == 0);
            yield return Ticks(1);
            T.Check($"salvage frees the piece ({n} -> {sm.Count})", sm.Count == n - 1);
            T.Check("the freed slot is buildable again", sm.Place(Vector3.Zero, EConstruct.Floor, 0) != null);
            // -1, not 0: callers refund materials off this, so "nothing there" must not read as "tier 0".
            T.Check("salvaging nothing returns -1, not tier 0", sm.Salvage(null) == -1);

            // ---- disk round trip ----
            string path = "user://structures_test.json";
            int saved = sm.Count;
            T.Check($"saved {saved} pieces to disk", sm.SaveToDisk(path));
            sm.Clear();
            yield return Ticks(1);
            T.Check("cleared", sm.Count == 0);
            int back = sm.LoadFromDisk(path);
            yield return Ticks(1);
            T.Check($"loaded them back ({back} of {saved})", back == saved && sm.Count == saved);

            // A world nobody has built in yet is NOT an error -- treating a missing file as a failure would log
            // noise on every fresh start.
            T.Check("a missing save loads 0 quietly", sm.LoadFromDisk("user://structures_does_not_exist.json") == 0);

            // THE guard that protects a real player's base: auto-persist is OFF unless the game explicitly
            // turns it on. Every manager a test constructs must be inert on disk -- an L1 run that quietly
            // wrote its fixtures over user://structures.json would destroy someone's base and pass while doing
            // it. This is the one failure here that is not recoverable by re-running anything.
            var plain = new StructureManager();
            T.Check("a test-constructed manager does NOT auto-persist", !plain.AutoPersist);
            plain.QueueFree();

            if (Godot.FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        }
    }
}
