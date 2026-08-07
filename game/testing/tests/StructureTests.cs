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
            Rigs.Ground(World);   // a floor needs GROUND under it now, not merely a Y near sea level
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

            // PIN the values that are OURS rather than retail's, with LITERALS. The check above re-derives from
            // the same constant it is checking, so it agrees with any value including a typo -- and the docs
            // claimed these were "pinned by a test" on the strength of it. A pin has to state the number.
            T.Check($"wood/brick/metal health is 300/600/1000 ({StructureCatalog.TierAt(0).Health}/{StructureCatalog.TierAt(1).Health}/{StructureCatalog.TierAt(2).Health})",
                StructureCatalog.TierAt(0).Health == 300 && StructureCatalog.TierAt(1).Health == 600 && StructureCatalog.TierAt(2).Health == 1000);
            T.Check($"the door opening is 2.0 x 3.0 ({StructureCatalog.DoorOpeningWidth} x {StructureCatalog.DoorOpeningHeight})",
                Mathf.IsEqualApprox(StructureCatalog.DoorOpeningWidth, 2.0f) && Mathf.IsEqualApprox(StructureCatalog.DoorOpeningHeight, 3.0f));
            // and a RETAIL one the reviewer caught us getting wrong: floors/roofs were built 0.25 thick, which
            // is HALF_ROOF_THICKNESS used as the full extent. Retail's ROOF_THICKNESS is 0.5
            // (HousingConnections.cs:295-296).
            T.Check($"floor/roof are ROOF_THICKNESS 0.5 thick, not the half ({StructureCatalog.Extents(EConstruct.Floor).Y})",
                Mathf.IsEqualApprox(StructureCatalog.Extents(EConstruct.Floor).Y, 0.5f)
                && Mathf.IsEqualApprox(StructureCatalog.Extents(EConstruct.Roof).Y, 0.5f));

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

            // PILLARS stand at tile CORNERS -- where four tiles meet -- not on faces or edges. Before the
            // corner slot class they snapped like walls, to a side midpoint, so a pillar meant to hold up the
            // junction of four tiles landed halfway along one wall: wrong, and impossible to frame with.
            var (pp, _) = StructureCatalog.Snap(new Vector3(2.6f, 0f, 2.6f), EConstruct.Pillar);
            T.Check($"a pillar snaps to a tile CORNER ({pp})",
                Mathf.IsEqualApprox(Mathf.Abs(pp.X), StructureCatalog.HalfEdge)
                && Mathf.IsEqualApprox(Mathf.Abs(pp.Z), StructureCatalog.HalfEdge));
            // ...and not to the centre a floor would take
            var (fc, _) = StructureCatalog.Snap(new Vector3(2.6f, 0f, 2.6f), EConstruct.Floor);
            T.Check($"a pillar and a floor at the same aim take DIFFERENT spots ({pp} vs {fc})", pp != fc);
            T.Check("corner, face and edge are three distinct slot classes",
                StructureCatalog.SlotKey(pp, EConstruct.Pillar) != StructureCatalog.SlotKey(pp, EConstruct.Wall)
                && StructureCatalog.SlotKey(pp, EConstruct.Pillar) != StructureCatalog.SlotKey(pp, EConstruct.Floor));
            // The real invariant, rather than the one I first wrote: EVERY pillar lands on the corner lattice,
            // i.e. an ODD multiple of the half edge on both axes. My first attempt asserted that two points
            // either side of x=6 snap together -- but x=6 is exactly halfway between corners 3 and 9, so
            // landing on different ones is correct, and the test was wrong rather than the code. Sweeping the
            // whole range catches a genuine double-round (which would produce an EVEN multiple) without
            // encoding a false expectation about tie points.
            bool allOnLattice = true;
            for (float x = -13f; x <= 13f; x += 0.7f)
                for (float z = -13f; z <= 13f; z += 0.7f)
                {
                    var (q, _) = StructureCatalog.Snap(new Vector3(x, 0f, z), EConstruct.Pillar);
                    int kx = Mathf.RoundToInt(q.X / StructureCatalog.HalfEdge);
                    int kz = Mathf.RoundToInt(q.Z / StructureCatalog.HalfEdge);
                    if (kx % 2 == 0 || kz % 2 == 0) { allOnLattice = false; GD.Print($"[structure] pillar off-lattice at ({x},{z}) -> {q}"); }
                }
            T.Check("every pillar snaps onto the corner lattice (odd multiples of the half edge)", allOnLattice);

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
            Rigs.Ground(World);   // a floor needs GROUND under it now, not merely a Y near sea level
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
            Rigs.Ground(World);   // a floor needs GROUND under it now, not merely a Y near sea level
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
            Rigs.Ground(World);   // a floor needs GROUND under it now, not merely a Y near sea level
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

            // THE guard that actually matters, and the one the check above only pretended to be. It asserted
            // `!new StructureManager().AutoPersist` -- i.e. that C# zero-initialises a bool. Unfalsifiable, and
            // aimed at the wrong manager entirely: the one that reached the disk was created by the GAME, via
            // Rigs.Player -> PlayerController._Ready -> BuildTool.EnsureManager -> AutoPersist = true. Around 25
            // suites call Rigs.Player, so every L1 run was silently overwriting the real save with "[]".
            T.Check("the L1 harness has disk persistence switched OFF globally", !StructureManager.PersistenceEnabled);
            var gameLike = new StructureManager { AutoPersist = true };   // exactly what EnsureManager builds
            World.AddChild(gameLike);
            gameLike.Place(Vector3.Zero, EConstruct.Floor, 2);
            string real = StructureManager.SavePath;
            bool existedBefore = Godot.FileAccess.FileExists(real);
            string beforeText = existedBefore ? Godot.FileAccess.GetFileAsString(real) : null;
            gameLike.QueueFree();          // _ExitTree would SaveToDisk() if the switch were on
            yield return Ticks(3);
            string afterText = Godot.FileAccess.FileExists(real) ? Godot.FileAccess.GetFileAsString(real) : null;
            T.Check("an AutoPersist manager tearing down does NOT write the real save file",
                existedBefore == Godot.FileAccess.FileExists(real) && beforeText == afterText);

            if (Godot.FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        }
    }

    // The CROSSHAIR path into the structure system -- melee, salvage, upgrade.
    //
    // The manager's Damage/Repair/Salvage were covered; nothing checked whether aiming at a wall reaches THAT
    // wall. It did not: the resolver took the nearest piece within 3 m of the hit point, measured to each
    // piece's ORIGIN, and an origin sits at the piece's base. Aim high on a wall and the floor tile beside it
    // is nearer to the hit than the wall you are looking at -- and past 3 m up, nothing is in range at all, so
    // melee, salvage and upgrade silently no-op. Every manager-level test stayed green throughout, because
    // none of them went through the aim.
    public class StructureAimedActions : GameTest
    {
        public override string Name => "structure.aimed_actions";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var sm = new StructureManager();
            World.AddChild(sm);
            yield return Ticks(1);

            var floor = sm.Place(Vector3.Zero, EConstruct.Floor, 0);
            var wall = sm.Place(new Vector3(StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 0);
            T.Check("fixture: a floor and a wall on its edge", floor != null && wall != null);

            var p = Rigs.Player(World, new Vector3(wall.Pos.X + 4f, 0f, 0f));
            // Let the player's own deferred setup flush. Run alone, Rigs.Player blocks ~3 s loading gun assets
            // and everything has settled by the first check; run inside the full suite those assets are cached,
            // the call returns instantly, and the deferred work lands AFTER the test has already aimed -- which
            // is why this suite passed alone and failed in the suite. Waiting on ticks makes it independent of
            // whether the assets happened to be warm.
            yield return Ticks(30);

            // The aim is a PRECONDITION, not a check: re-aim immediately before each action and confirm the eye
            // really points at the wall, so an action failing means the ACTION is broken rather than that the
            // camera quietly reset.
            var high = new Vector3(wall.Pos.X, StructureCatalog.WallHeight * 0.85f, 0f);
            var eye = p.DebugLookAt(high);
            GD.Print($"[aimtest] eye={eye.Origin} fwd={-eye.Basis.Z} target={high}");
            T.Check($"the eye is above the wall's mid-height ({eye.Origin.Y:0.00})", eye.Origin.Y > 0.5f);
            T.Check("the eye points at the upper wall (downward-ish is wrong)", (-eye.Basis.Z).Dot((high - eye.Origin).Normalized()) > 0.99f);

            var aimed = p.DebugAimedStructure();
            GD.Print($"[aimtest] aimed={(aimed == null ? "null" : aimed.Construct.ToString())}");
            T.Check("aiming high on a wall resolves to the WALL", aimed == wall);
            T.Check("...and specifically not the floor sharing its tile", aimed != floor);

            // ---- melee damages the piece under the crosshair ----
            int before = wall.Health;
            p.DebugLookAt(high);
            bool hitIt = p.DebugMeleeStructure(50f, 4f);
            T.Check("the swing connects", hitIt);
            T.Check($"the wall lost exactly the swing ({before - wall.Health})", wall.Health == before - 50);
            T.Check("the floor was untouched", floor.Health == floor.MaxHealth);

            // ---- upgrade the aimed piece ----
            int tierBefore = wall.Tier;
            p.DebugLookAt(high);
            p.DebugUpgradeAimed();
            T.Check($"upgrade raised the aimed piece a tier ({tierBefore} -> {wall.Tier})", wall.Tier == tierBefore + 1);
            T.Check("and refilled health to the new tier", wall.Health == StructureCatalog.TierAt(wall.Tier).Health);

            // ---- salvage takes the aimed piece, not its neighbour ----
            p.DebugLookAt(high);
            p.DebugSalvageAimed();
            yield return Ticks(2);
            bool wallGone = true, floorGone = true;
            foreach (var pc in sm.All) { if (pc == wall) wallGone = false; if (pc == floor) floorGone = false; }
            T.Check("salvage removed the wall", wallGone);
            T.Check("and left the floor standing", !floorGone);

            // ---- the negative: aiming at nothing resolves to nothing ----
            p.DebugLookAt(new Vector3(wall.Pos.X + 400f, 300f, 0f));
            T.Check("aiming at empty sky resolves to no piece", p.DebugAimedStructure() == null);
            T.Check("and a swing at nothing reports no hit", !p.DebugMeleeStructure(50f, 4f));
        }
    }

    // Is the explosion code actually REACHED by a real charge?
    //
    // structure.explosion tests StructureManager.Explode directly and passes whether or not anything in the
    // game ever calls it -- and for a while nothing did. DetonateTrap damaged nearby DEPLOYABLES and left every
    // wall untouched, so a charge would blow up the generator next to a base and not scratch the base. Fully
    // tested, completely unreachable, and invisible to a suite that only ever calls the manager itself.
    //
    // So this drives the real path: plant a real Charge, fire it the way a detonator does, and check the wall.
    public class StructureChargeRaid : GameTest
    {
        public override string Name => "structure.charge_raid";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var sm = new StructureManager();
            World.AddChild(sm);
            yield return Ticks(1);

            sm.Place(Vector3.Zero, EConstruct.Floor, 0);
            var wall = sm.Place(new Vector3(StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 1);   // brick
            T.Check("fixture: a brick wall", wall != null);
            yield return Ticks(2);

            int before = wall.Health;
            // a charge planted against the wall's outer face, at its mid-height
            var at = new Vector3(wall.Pos.X + 0.4f, StructureCatalog.WallPivotOffset, 0f);
            var charge = Deployable.Spawn(World, DeployableDef.Charge, at, 0f);
            T.Check("fixture: a charge is planted", charge != null);
            yield return Ticks(3);

            int fired = Deployable.DetonateAllCharges(World.GetTree());
            T.Check($"the detonator fires it ({fired})", fired == 1);
            yield return Ticks(3);

            T.Check($"the WALL took the blast ({before - wall.Health} of {before})", wall.Health < before);
            // and it went through the shared rules rather than a second hand-rolled falloff: 1000 structure
            // damage at point-blank on a 600 hp brick wall destroys it outright.
            bool gone = true;
            foreach (var pc in sm.All) if (pc == wall) gone = false;
            T.Check("a 1000-damage charge point-blank destroys a brick wall", gone);

            // a wall well outside the 8 m blast is untouched -- proves the radius is respected and that the
            // charge is not simply damaging every piece in the world.
            var far = sm.Place(new Vector3(90f, 0f, 90f), EConstruct.Wall, 2);
            yield return Ticks(2);
            int farHp = far.Health;
            var c2 = Deployable.Spawn(World, DeployableDef.Charge, new Vector3(0.4f, 1f, 0f), 0f);
            T.Check("fixture: a second charge planted", c2 != null);
            yield return Ticks(3);
            int fired2 = Deployable.DetonateAllCharges(World.GetTree());
            // assert the STIMULUS, or "the far wall is undamaged" passes just as well when nothing detonated
            T.Check($"the second charge actually fired ({fired2})", fired2 == 1);
            yield return Ticks(3);
            T.Check($"a wall 120 m away is untouched ({far.Health}/{farHp})", far.Health == farHp);
        }
    }

    // Doorways: a wall-class piece with a hole, and the socket a door leaf hangs in.
    //
    // The mutual exclusion is the interesting part and it is deliberately NOT written as a rule anywhere. A
    // doorway and a wall both resolve to the same "E" slot namespace, so an edge holds one or the other and
    // nothing has to remember to check. Asserted here because it is the kind of property that survives only as
    // long as someone knows it is load-bearing.
    public class StructureDoorway : GameTest
    {
        public override string Name => "structure.doorway";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var sm = new StructureManager();
            World.AddChild(sm);
            yield return Ticks(1);

            var floor = sm.Place(Vector3.Zero, EConstruct.Floor, 0);
            var door = sm.Place(new Vector3(StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Doorway, 0);
            T.Check("fixture: a floor and a doorway", floor != null && door != null);

            // ---- it snaps and sits exactly like a wall ----
            var (dp, dyaw) = StructureCatalog.Snap(new Vector3(2.9f, 0f, 0.2f), EConstruct.Doorway);
            var (wp, wyaw) = StructureCatalog.Snap(new Vector3(2.9f, 0f, 0.2f), EConstruct.Wall);
            T.Check($"a doorway snaps to the same edge slot a wall would ({dp} vs {wp})", dp == wp && Mathf.IsEqualApprox(dyaw, wyaw));
            T.Check("and shares the wall's pivot",
                Mathf.IsEqualApprox(StructureCatalog.PivotOffset(EConstruct.Doorway), StructureCatalog.WallPivotOffset));
            T.Check("it is wall-class, but not a face or a corner",
                StructureCatalog.IsWallClass(EConstruct.Doorway)
                && !StructureCatalog.IsFace(EConstruct.Doorway) && !StructureCatalog.IsCorner(EConstruct.Doorway));

            // ---- one edge, one piece: a wall cannot also occupy the doorway's slot ----
            T.Check("the same edge cannot hold a wall as well",
                sm.Place(new Vector3(StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 0) == null);
            sm.CanPlace(new Vector3(StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 0, out var why);
            T.Check($"...and the reason is occupancy ({why})", why == "occupied");
            T.Check("a doorway and a wall share one slot key",
                StructureCatalog.SlotKey(dp, EConstruct.Doorway) == StructureCatalog.SlotKey(dp, EConstruct.Wall));

            // ---- a doorway supports what a wall supports ----
            T.Check("a wood floor can rest against the doorway's edge",
                sm.Place(new Vector3(StructureCatalog.EdgeLength, 0f, 0f), EConstruct.Floor, 0) != null);

            // ---- the door socket ----
            var sock = StructureManager.DoorSocket(door);
            T.Check("a doorway exposes a door socket", sock.HasValue);
            T.Check($"the socket is centred in the OPENING, not on the piece origin ({sock.Value.Origin.Y:0.00})",
                Mathf.IsEqualApprox(sock.Value.Origin.Y, door.Pos.Y + StructureCatalog.DoorOpeningHeight * 0.5f));
            T.Check("the socket sits at the doorway's edge in plan",
                Mathf.IsEqualApprox(sock.Value.Origin.X, door.Pos.X) && Mathf.IsEqualApprox(sock.Value.Origin.Z, door.Pos.Z));
            // Mathf.Abs matters: the signed form passed for ANY basis with euler.Y <= the doorway's yaw,
            // including Basis.Identity -- i.e. "DoorSocket forgot the yaw entirely", which is a door leaf
            // mounted 90 degrees into the wall it hangs in.
            T.Check($"the socket faces the way the doorway does ({Mathf.RadToDeg(sock.Value.Basis.GetEuler().Y):0.0} vs {door.YawDeg:0.0})",
                Mathf.Abs(sock.Value.Basis.GetEuler().Y - Mathf.DegToRad(door.YawDeg)) < 0.001f);
            // a wall is NOT a doorway: returning a plausible transform here would mount a door inside a solid wall
            var solid = sm.Place(new Vector3(0f, 0f, StructureCatalog.HalfEdge), EConstruct.Wall, 0);
            T.Check("a plain wall has NO socket rather than a plausible one",
                solid != null && !StructureManager.DoorSocket(solid).HasValue);

            // ---- the hole is real: the collider has it too ----
            // three solids (two jambs + a lintel), not one box with a painted-on gap. A doorway you can see
            // through and cannot walk through reads as a stuck door, not as missing geometry.
            yield return Ticks(2);
            int bodies = 0;
            foreach (var ch in door.Node.GetChildren()) if (ch is StaticBody3D) bodies++;
            T.Check($"the doorway's collider is built around the opening ({bodies} solids)", bodies == 3);
            var space = World.GetWorld3D().DirectSpaceState;
            var q = PhysicsRayQueryParameters3D.Create(
                new Vector3(door.Pos.X - 2f, 1.0f, 0f), new Vector3(door.Pos.X + 2f, 1.0f, 0f));
            q.CollisionMask = 1u << 0;
            T.Check("you can see (and walk) straight through the opening", space.IntersectRay(q).Count == 0);
            var qh = PhysicsRayQueryParameters3D.Create(
                new Vector3(door.Pos.X - 2f, 1.0f, 2.4f), new Vector3(door.Pos.X + 2f, 1.0f, 2.4f));
            qh.CollisionMask = 1u << 0;
            T.Check("but the jamb beside it is solid", space.IntersectRay(qh).Count > 0);
        }
    }

    // Building somewhere that is NOT sea level.
    //
    // Every fixture in this file stood on Rigs.Ground -- a WorldBoundaryShape3D at exactly y=0 -- which is why
    // the whole suite stayed green while you could not found a wood or brick base anywhere above ~2.1 m. The
    // storey lattice was `Round(world.Y / WallHeight) * WallHeight`, anchored at world zero, so a floor aimed
    // at terrain y=12 snapped to 12.75, failed its own "am I on the ground" test (which really asked "am I
    // near sea level"), and was refused. A flat test rig cannot see a bug about height.
    public class StructureOnHighGround : GameTest
    {
        public override string Name => "structure.high_ground";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);                     // sea level, y = 0
            const float HillY = 40f;                // a plateau well beyond the old +-2.125 m window
            var hill = new StaticBody3D { CollisionLayer = 1 << 0 };
            hill.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60f, 4f, 60f) } });
            World.AddChild(hill);
            hill.GlobalPosition = new Vector3(0f, HillY - 2f, 0f);   // top face at exactly HillY
            var sm = new StructureManager();
            World.AddChild(sm);
            yield return Ticks(3);

            // ---- the bug, stated as the user experiences it ----
            var f = sm.Place(new Vector3(0f, HillY, 0f), EConstruct.Floor, 0);
            T.Check("a WOOD floor founds on a hilltop 40 m up", f != null);
            if (f == null) { sm.CanPlace(new Vector3(0f, HillY, 0f), EConstruct.Floor, 0, out var why); T.Check($"(refusal reason was '{why}')", false); yield break; }

            // ...and it sits ON the hill rather than half a storey through it. The old lattice would have put
            // this at Round(40/4.25)*4.25 = 38.25, nearly 2 m underground.
            T.Check($"and it sits on the surface, not on the sea-level lattice ({f.Pos.Y:0.00})",
                Mathf.IsEqualApprox(f.Pos.Y, HillY));

            // ---- a second piece takes ITS levels from the base, not from the sea ----
            var w = sm.Place(new Vector3(StructureCatalog.HalfEdge, HillY, 0f), EConstruct.Wall, 0);
            T.Check("a wall attaches to it", w != null);
            T.Check($"at the same storey as the floor ({w.Pos.Y:0.00})", Mathf.IsEqualApprox(w.Pos.Y, f.Pos.Y));

            var up = sm.Place(new Vector3(0f, HillY + StructureCatalog.WallHeight, 0f), EConstruct.Floor, 0);
            T.Check("a second storey stacks", up != null);
            T.Check($"exactly one wall-height above the first ({up.Pos.Y - f.Pos.Y:0.00})",
                Mathf.IsEqualApprox(up.Pos.Y - f.Pos.Y, StructureCatalog.WallHeight));

            // ---- QueryAt has to find pieces up here too, or the barricade seam is dead on a hill ----
            var q = sm.QueryAt(f.Pos, 1.0f);
            T.Check("QueryAt finds a hilltop piece", q.HasValue && q.Value.Node == f.Node);

            // ---- and the rule it replaced still holds: you cannot found in mid-air ----
            T.Check("wood still cannot be founded floating in the sky",
                sm.Place(new Vector3(300f, 80f, 300f), EConstruct.Floor, 0) == null);
            sm.CanPlace(new Vector3(300f, 80f, 300f), EConstruct.Floor, 0, out var why2);
            T.Check($"...for lack of support ({why2})", why2 == "no support");
            T.Check("and sea level still works", sm.Place(new Vector3(300f, 0f, 300f), EConstruct.Floor, 0) != null);
        }
    }

    // Explosions against a base, reimplemented from SDK StructureDrop.cs:52-70.
    //
    // The check that matters is the LINE-OF-SIGHT one. Distance falloff alone looks perfectly reasonable and
    // quietly removes the point of building: one charge at the front door damages every wall in the building
    // at once, layering buys you nothing, and the whole upgrade ladder becomes decoration. It is also the rule
    // most easily lost, because a version without it passes every "does the explosion hurt things nearby" test
    // anyone would naturally write.
    public class StructureExplosion : GameTest
    {
        public override string Name => "structure.explosion";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var sm = new StructureManager();
            World.AddChild(sm);
            yield return Ticks(1);

            // ---- geometry helper: range is to the CLOSEST POINT, not the origin ----
            // a floor first: wood cannot float, so a bare wall would be refused (which is the support rule
            // working, not a fixture that happens to fail)
            var pad = sm.Place(Vector3.Zero, EConstruct.Floor, 0);
            var w = sm.Place(new Vector3(StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 0);
            T.Check("fixture: a floor and a wall", pad != null && w != null);
            // a point level with the wall's middle, 1 m out from its face
            var outside = new Vector3(w.Pos.X + 1f, StructureCatalog.WallPivotOffset, 0f);
            float toClosest = StructureManager.ClosestPointOn(w, outside).DistanceTo(outside);
            float toOrigin = w.Pos.DistanceTo(outside);
            T.Check($"closest point is nearer than the origin ({toClosest:0.00} vs {toOrigin:0.00})",
                toClosest < toOrigin - 1f);
            // TWO-SIDED. `< 1.2` alone passes for an implementation that returns the query point verbatim
            // (distance 0), which is the actual way this helper would break.
            T.Check($"and it is about the face standoff, ~1 m ({toClosest:0.00})", toClosest > 0.8f && toClosest < 1.2f);
            yield return Ticks(2);

            // ---- falloff is linear in range/radius ----
            int hpFull = w.Health;
            sm.Explode(StructureManager.ClosestPointOn(w, outside), 10f, 100, out int dmgd);
            T.Check($"a point-blank blast deals ~full damage ({hpFull - w.Health} of 100)",
                dmgd >= 1 && hpFull - w.Health >= 95);

            var w2 = sm.Place(new Vector3(-StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 0);
            yield return Ticks(2);
            int hp2 = w2.Health;
            // half a radius away from its face -> half damage
            var half = StructureManager.ClosestPointOn(w2, new Vector3(-20f, StructureCatalog.WallPivotOffset, 0f))
                       + new Vector3(-5f, 0f, 0f);
            sm.Explode(half, 10f, 100, out _);
            int dealt2 = hp2 - w2.Health;
            T.Check($"at half the radius it deals about half ({dealt2} of 100)", dealt2 >= 40 && dealt2 <= 60);

            // ---- out of range is untouched ----
            int hp3 = w2.Health;
            sm.Explode(new Vector3(-200f, 0f, 0f), 10f, 100, out int dmgd3);
            T.Check("nothing outside the radius is touched", dmgd3 == 0 && w2.Health == hp3);

            // ---- metal is NOT immune to explosives the way it is to melee ----
            var metal = sm.Place(new Vector3(60f, 0f, 60f), EConstruct.Wall, 2);
            yield return Ticks(2);
            int mhp = metal.Health;
            T.Check("metal shrugs off a melee hit", !sm.Damage(metal, 50) && metal.Health == mhp);
            sm.Explode(StructureManager.ClosestPointOn(metal, new Vector3(61f, 2f, 60f)), 10f, 100, out _);
            T.Check($"but an explosion hurts it ({mhp - metal.Health})", metal.Health < mhp);

            // ---- THE rule: a piece behind another piece is SHIELDED ----
            // two parallel walls on the same tile row; blast outside the near one, aimed through both.
            sm.Place(new Vector3(120f, 0f, 120f), EConstruct.Floor, 0);   // support for both
            var near = sm.Place(new Vector3(120f + StructureCatalog.HalfEdge, 0f, 120f), EConstruct.Wall, 0);
            var far = sm.Place(new Vector3(120f - StructureCatalog.HalfEdge, 0f, 120f), EConstruct.Wall, 0);
            T.Check("fixture: two stacked walls", near != null && far != null);
            yield return Ticks(3);
            int nearHp = near.Health, farHp = far.Health;
            // outside the near wall, far enough back that BOTH are inside a generous radius
            var blast = new Vector3(near.Pos.X + 2f, StructureCatalog.WallPivotOffset, 120f);
            sm.Explode(blast, 30f, 200, out _);
            T.Check($"the near wall takes it ({nearHp - near.Health})", near.Health < nearHp);
            T.Check($"the far wall is SHIELDED by it ({farHp - far.Health} damage)", far.Health == farHp);
            // and once the shield is gone, the far wall is exposed -- proving the block was line of sight and
            // not simply that the far wall was out of range all along.
            sm.Damage(near, 100000, explosive: true);   // remove the shield directly -- another huge blast would
                                                        // also flatten the far wall and prove nothing
            yield return Ticks(3);
            bool nearGone = true;
            foreach (var pc in sm.All) if (pc == near) nearGone = false;
            T.Check("the near wall is destroyed", nearGone);
            int farHp2 = far.Health;
            sm.Explode(blast, 30f, 200, out _);
            T.Check($"now the far wall takes damage ({farHp2 - far.Health})", far.Health < farHp2);
        }
    }

    // The defects the 4-way review found that a green suite was hiding. Each check here is written so that
    // removing the corresponding fix turns it RED -- several of the originals passed either way, which is how
    // this set survived in the first place.
    public class StructureReviewRegressions : GameTest
    {
        public override string Name => "structure.review_regressions";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var sm = new StructureManager();
            World.AddChild(sm);
            yield return Ticks(2);

            // ---- Mathf.Sign(0) put an EDGE piece at the tile CENTRE ----
            var (wp, _) = StructureCatalog.Snap(Vector3.Zero, EConstruct.Wall);
            T.Check($"a wall aimed at a tile centre still lands on an EDGE, not the centre ({wp})",
                Mathf.IsEqualApprox(Mathf.Abs(wp.X) + Mathf.Abs(wp.Z), StructureCatalog.HalfEdge));
            // every point in a tile must produce a real edge slot -- sweep rather than trust the one tie point
            bool allEdges = true;
            for (float x = -3f; x <= 3f; x += 0.5f)
                for (float z = -3f; z <= 3f; z += 0.5f)
                {
                    var (q, _) = StructureCatalog.Snap(new Vector3(x, 0f, z), EConstruct.Wall);
                    float ax = Mathf.Abs(q.X), az = Mathf.Abs(q.Z);
                    bool onEdge = (Mathf.IsEqualApprox(ax, StructureCatalog.HalfEdge) && Mathf.IsZeroApprox(az))
                               || (Mathf.IsEqualApprox(az, StructureCatalog.HalfEdge) && Mathf.IsZeroApprox(ax));
                    if (!onEdge) { allEdges = false; GD.Print($"[structure] edge snap off-lattice at ({x},{z}) -> {q}"); }
                }
            T.Check("every point in a tile snaps a wall to a real side midpoint", allEdges);

            // ---- a ROOF and the storey above's FLOOR are different slots ----
            T.Check("roof and floor do not share a slot key",
                StructureCatalog.SlotKey(Vector3.Zero, EConstruct.Roof) != StructureCatalog.SlotKey(Vector3.Zero, EConstruct.Floor));
            var groundFloor = sm.Place(Vector3.Zero, EConstruct.Floor, 2);
            var roof = sm.Place(new Vector3(0f, StructureCatalog.WallHeight, 0f), EConstruct.Roof, 2);
            T.Check("a roof caps the storey", roof != null);
            var above = sm.Place(new Vector3(0f, StructureCatalog.WallHeight, 0f), EConstruct.Floor, 2);
            T.Check("and you can STILL build a floor on top of it", above != null);

            // ---- QueryAt's bounded probe has to see pillars (their own "C" namespace) ----
            var pil = sm.Place(new Vector3(StructureCatalog.HalfEdge, 0f, StructureCatalog.HalfEdge), EConstruct.Pillar, 2);
            T.Check("fixture: a pillar", pil != null);
            yield return Ticks(2);
            var narrow = sm.QueryAt(pil.Pos, 1.0f);
            T.Check("a NARROW query finds a pillar (it used to only show up in the wide fallback)",
                narrow.HasValue && narrow.Value.Node == pil.Node);

            // ---- Remove must not evict a slot that has been rebuilt ----
            var first = sm.Place(new Vector3(60f, 0f, 60f), EConstruct.Floor, 2);
            string key = first.Key;
            sm.Remove(first);
            var second = sm.Place(new Vector3(60f, 0f, 60f), EConstruct.Floor, 2);
            T.Check("the slot can be rebuilt", second != null && second.Key == key);
            sm.Remove(first);                       // stale handle: must be a no-op on the new occupant
            yield return Ticks(2);
            sm.CanPlace(new Vector3(60f, 0f, 60f), EConstruct.Floor, 2, out var why);
            T.Check($"removing a STALE piece handle does not orphan the new one ({why})", why == "occupied");

            // ---- support must not reach DIAGONALLY across an empty tile ----
            // a lone floor, then a wall on the far edge of the EMPTY tile next to it: braced only on the
            // diagonal, which used to be accepted and lets a base walk out over a void one piece at a time.
            // Anchor on a real tile CENTRE (a multiple of the 6 m edge). My first attempt used 200, which is
            // not on the lattice -- it snapped to 198 and the "own edge" wall landed on a neighbouring tile
            // entirely, so the test failed while the code was right. Same mistake as the first pillar test.
            const float T0 = 204f;   // 34 * EdgeLength
            var lone = sm.Place(new Vector3(T0, 0f, T0), EConstruct.Floor, 0);
            T.Check($"fixture: a lone wood floor on a tile centre ({lone?.Pos})",
                lone != null && Mathf.IsEqualApprox(lone.Pos.X, T0) && Mathf.IsEqualApprox(lone.Pos.Z, T0));
            T.Check("a wood wall on the far edge of the NEXT tile is refused",
                sm.Place(new Vector3(T0 + StructureCatalog.HalfEdge, 0f, T0 + StructureCatalog.EdgeLength), EConstruct.Wall, 0) == null);
            // ...while the ones that genuinely touch it still work
            T.Check("a wall on the floor's OWN edge is still supported",
                sm.Place(new Vector3(T0 + StructureCatalog.HalfEdge, 0f, T0), EConstruct.Wall, 0) != null);
            T.Check("a floor on the ADJACENT tile is still supported",
                sm.Place(new Vector3(T0 + StructureCatalog.EdgeLength, 0f, T0), EConstruct.Floor, 0) != null);

            // ---- deserialise clamps a drifted save instead of trusting it ----
            string path = "user://structures_review_test.json";
            var bad = new StructureManager();
            World.AddChild(bad);
            yield return Ticks(1);
            using (var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
                f.StoreString("[{\"c\":0,\"t\":7,\"h\":999999,\"x\":0,\"y\":0,\"z\":0}]");
            int n = bad.LoadFromDisk(path);
            T.Check($"a drifted save still loads ({n})", n == 1);
            var lp = bad.All[0];
            T.Check($"tier is clamped into range ({lp.Tier})", lp.Tier >= 0 && lp.Tier < StructureCatalog.TierCount);
            T.Check($"health is clamped to the tier max ({lp.Health}/{lp.MaxHealth})", lp.Health <= lp.MaxHealth && lp.Health > 0);
            bad.QueueFree();
            if (Godot.FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        }
    }

    // Explosion damage must not depend on the order the base was BUILT in.
    public class StructureExplosionOrder : GameTest
    {
        public override string Name => "structure.explosion_order";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var sm = new StructureManager();
            World.AddChild(sm);
            yield return Ticks(2);

            // Two bases, identical geometry, opposite build order. Outer wall weak enough that the blast
            // destroys it; inner wall behind it must survive untouched in BOTH.
            // base A: outer placed first (the natural order)
            sm.Place(new Vector3(0f, 0f, 0f), EConstruct.Floor, 0);
            var aOuter = sm.Place(new Vector3(StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 0);
            var aInner = sm.Place(new Vector3(-StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 0);
            // base B: inner placed first
            sm.Place(new Vector3(120f, 0f, 0f), EConstruct.Floor, 0);
            var bInner = sm.Place(new Vector3(120f - StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 0);
            var bOuter = sm.Place(new Vector3(120f + StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 0);
            T.Check("fixture: both bases built", aOuter != null && aInner != null && bInner != null && bOuter != null);
            yield return Ticks(3);

            int aInnerHp = aInner.Health, bInnerHp = bInner.Health;
            float y = StructureCatalog.WallPivotOffset;
            // blast outside each outer wall, strong enough to destroy it and reach the inner one
            sm.Explode(new Vector3(aOuter.Pos.X + 1.5f, y, 0f), 30f, 400, out _);
            sm.Explode(new Vector3(bOuter.Pos.X + 1.5f, y, 0f), 30f, 400, out _);
            yield return Ticks(2);

            bool aOuterGone = true, bOuterGone = true;
            foreach (var pc in sm.All) { if (pc == aOuter) aOuterGone = false; if (pc == bOuter) bOuterGone = false; }
            T.Check("both outer walls were destroyed", aOuterGone && bOuterGone);
            T.Check($"inner wall shielded when the outer was placed FIRST ({aInnerHp - aInner.Health} damage)",
                aInner.Health == aInnerHp);
            T.Check($"inner wall shielded when the outer was placed LAST ({bInnerHp - bInner.Health} damage)",
                bInner.Health == bInnerHp);
            T.Check("...and the two identical bases took identical damage",
                aInner.Health == bInner.Health);
        }
    }

    // The SEAM between structures and barricades, exercised through the exact hook PlayerController installs.
    //
    // Both subsystems are green in isolation and the merge compiles clean, which is precisely the situation
    // where the integration bug lives. BarricadePlacer's own header proposes wiring
    // `placer.CanAttach = StructureManager.Instance.CanAttach` -- and that is wrong in a way no test on either
    // branch can see. CanAttach answers "is there a structure face here"; on open terrain it answers NO, and the
    // placer reads a false as "you may not build here". Wire it verbatim and every generator, crate and charge
    // becomes unplaceable on the ground, in a build where every existing barricade and structure test still
    // passes: the barricade tests construct a placer with NO hook, so the hook is the one thing they cannot see.
    //
    // So the first check below is the one with the teeth, and it only has teeth because a StructureManager is
    // alive and holding pieces while we aim at bare ground far away from them. With no manager the hook is
    // trivially true and the check would pass while proving nothing.
    public class StructureBarricadeAttachSeam : GameTest
    {
        public override string Name => "structure.barricade_seam";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var sm = new StructureManager();
            World.AddChild(sm);
            var cam = new Camera3D { Current = false };
            World.AddChild(cam);
            yield return Ticks(1);

            // a real base: floor at the origin tile, wall on its +x edge (the manager snaps both).
            var floor = sm.Place(Vector3.Zero, EConstruct.Floor, 0);
            var wall = sm.Place(new Vector3(StructureCatalog.HalfEdge, 0f, 0f), EConstruct.Wall, 0);
            T.Check("fixture: floor and wall both placed", floor != null && wall != null);
            yield return Ticks(2);   // let the new colliders register with the physics space

            var placer = new BarricadePlacer();
            World.AddChild(placer);
            placer.CanAttach = StructureManager.BarricadeAttachHook;   // EXACTLY what PlayerController installs

            // ---- 1. the regression guard: ground placement must survive the hook ----
            placer.SetDef(DeployableDef.Generator);   // Mount = Floor, like every deployable that already existed
            cam.Position = new Vector3(40f, 2.2f, 40f);
            cam.LookAt(new Vector3(41.5f, 0f, 41.5f), Vector3.Up);
            bool groundOk = placer.Aim(cam);
            T.Check("a FLOOR deployable still places on open ground with the structure hook wired", groundOk);
            T.Check("the manager is genuinely live, so that check was not vacuous",
                StructureManager.Instance == sm && sm.Count == 2);
            T.Check("and the hook itself abstains on open ground rather than refusing it",
                StructureManager.BarricadeAttachHook(new Vector3(40f, 0f, 40f), Vector3.Up, null));
            // the distinction the seam turns on: the raw CanAttach says NO out here, which is the correct answer
            // to its own question and the wrong answer to the placer's.
            T.Check("raw CanAttach still reports no structure face on open ground (the trap)",
                !sm.CanAttach(new Vector3(40f, 0f, 40f), Vector3.Up));
            // The normal-agreement branch had NO coverage at all -- every existing call passed Vector3.Zero or
            // queried empty space, so both early-outs fired first and you could invert the comparison, or
            // `return true`, with the suite still green.
            T.Check("CanAttach AGREES with a floor's up-normal", sm.CanAttach(floor.Pos, Vector3.Up));
            T.Check("CanAttach REFUSES a horizontal normal on a floor", !sm.CanAttach(floor.Pos, new Vector3(1f, 0f, 0f)));

            // ---- 2. a WALL barricade mounts on a real structure wall ----
            placer.SetDef(DeployableDef.MetalBarricade);   // Mount = Wall
            T.Check("the def carries its own mount family", placer.Mount == BarricadeMount.Wall);
            float wx = wall.Pos.X, wy = StructureCatalog.WallPivotOffset;
            cam.Position = new Vector3(wx + 3f, wy, 0f);
            cam.LookAt(new Vector3(wx, wy, 0f), Vector3.Up);   // straight at the wall's outward face
            bool wallOk = placer.Aim(cam);
            Vector3 mountProbe = placer.Point;
            T.Check($"a WALL barricade mounts on a structure wall (normal {placer.Normal})", wallOk);
            T.Check($"the mount surface is vertical, not the floor ({placer.Normal.Y:0.00})",
                Mathf.Abs(placer.Normal.Y) < 0.1f);
            T.Check("it faces out of the wall",
                BarricadeAxes.Facing(BarricadePlacer.MountBasis(BarricadeMount.Wall, placer.Normal, placer.Yaw))
                    .Dot(placer.Normal) > 0.99f);

            // ...and the structure gate ENGAGED rather than abstained. Worth asserting separately, because the
            // first version of this passed for the wrong reason: the gate resolved the piece by nearest-origin
            // within 1.5 m, a wall's origin is its base, and the hit is 2.1 m up the face -- so it found nothing
            // and returned "not my department". Valid either way, and the wall rule totally untested. Resolving
            // by COLLIDER is what makes the two outcomes distinguishable.
            // Resolve from the COLLIDER the aim actually hit, not from wall.Node. Feeding in the piece root --
            // the node that carries the slot meta -- makes PieceForCollider's parent walk run zero iterations,
            // so it proved the lookup worked and nothing at all about the WIRING. The real path hands it the
            // StaticBody3D grandchild built by AddSlab.
            var space2 = World.GetWorld3D().DirectSpaceState;
            var probe = PhysicsRayQueryParameters3D.Create(new Vector3(wx + 3f, wy, 0f), new Vector3(wx - 1f, wy, 0f));
            probe.CollisionMask = 1u << 0;
            var probeHit = space2.IntersectRay(probe);
            var realCollider = probeHit.Count > 0 ? probeHit["collider"].As<Node>() : null;
            T.Check("the aim hits a collider that is NOT the piece root (so the walk is exercised)",
                realCollider != null && realCollider != wall.Node);
            T.Check("the gate resolves that real collider to the wall piece",
                sm.PieceForCollider(realCollider) == wall);
            T.Check("a hit on the wall AGREES with a horizontal normal",
                sm.AllowsBarricadeAt(mountProbe, placer.Normal, wall.Node));
            T.Check("...and the same wall REFUSES an up-normal, so the gate is doing work",
                !sm.AllowsBarricadeAt(mountProbe, Vector3.Up, wall.Node));
            T.Check("a floor piece is the other way round: agrees with up, refuses horizontal",
                sm.AllowsBarricadeAt(floor.Pos, Vector3.Up, floor.Node)
                && !sm.AllowsBarricadeAt(floor.Pos, new Vector3(1f, 0f, 0f), floor.Node));

            // ---- 3. supplying the hook must not drop the no-stacking rule ----
            // This is the other half of the seam: the placer used to pick CanAttach *instead of* its own
            // attachability rule, so wiring structures in would have quietly made barricades stackable.
            Vector3 mountPoint = placer.Point, mountNormal = placer.Normal;
            float mountYaw = placer.Yaw;
            var planted = Barricade.PlaceOnSurface(World, DeployableDef.MetalBarricade, mountPoint, mountNormal, mountYaw);
            T.Check("fixture: the barricade planted and is tagged", planted != null && planted.IsInGroup("barricades"));
            yield return Ticks(2);
            cam.Position = new Vector3(wx + 3f, wy, 0f);
            cam.LookAt(new Vector3(wx, wy, 0f), Vector3.Up);   // same aim -- now the barricade is in the way
            T.Check("cannot stack a barricade on another barricade, hook or no hook", !placer.Aim(cam));

            // ---- 4. the frozen normal survives the place gesture ----
            // PlayerController freezes point+normal+yaw at the click. Freezing point+yaw alone (the old
            // two-arg overload) defaults the normal to UP, so a wall barricade snaps flat for the length of
            // the placement animation and then lands correctly -- a visible pop nobody would call a bug report.
            placer.Freeze(mountPoint, mountNormal, mountYaw);
            T.Check("freeze keeps the wall normal", placer.Normal.Dot(mountNormal) > 0.99f);
            placer.Freeze(mountPoint, mountYaw);
            T.Check("the two-arg freeze assumes UP -- which is why the normal is carried", placer.Normal.Dot(Vector3.Up) > 0.99f);
        }
    }
}
