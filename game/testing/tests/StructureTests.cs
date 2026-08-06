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
}
