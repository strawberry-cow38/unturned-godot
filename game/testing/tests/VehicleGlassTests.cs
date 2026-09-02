using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // VEHICLE WINDOW GLASS (strawberry 2026-08-31: "make each glass pane destructable, doesnt respawn unless
    // 'fixed' in the vehicle mechanics ui").
    //
    // The checks are split by failure mode, because the first cut of vehicle glass shipped INVISIBLE for a day:
    // Spec.GlassMesh was read only inside BuildPlaneModel, so setting it on a car was a silent no-op that looked
    // exactly like "the tint is subtle". A test that only asserted "the sedan has a GlassMesh string" would have
    // passed against that build. So these assert on NODES that exist in the tree and on state that changed.
    public sealed class VehicleGlassTests : GameTest
    {
        public override string Name => "vehicle.glass";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            yield return Ticks(2);

            var car = Vehicle.BuildByName("sedan");
            World.AddChild(car);
            car.GlobalPosition = new Vector3(60f, 1.2f, 0f);
            yield return Ticks(20);

            // ---- 1. THE PANES EXIST AS NODES. Not "the spec names a mesh" -- the invisible-glass bug had a
            // correct spec and no node, and the two are indistinguishable from the spec alone.
            T.Check($"the sedan builds 6 glass panes ({car.GlassCount})", car.GlassCount == 6);
            var labels = new List<string>();
            for (int i = 0; i < car.GlassCount; i++) labels.Add(car.GlassLabel(i));
            T.Check($"...windscreen and rear window among them ({string.Join(",", labels)})",
                    labels.Contains("windshield") && labels.Contains("rear"));
            T.Check("...and a pane on each flank fore and aft",
                    labels.Contains("l_front") && labels.Contains("r_front") &&
                    labels.Contains("l_rear") && labels.Contains("r_rear"));
            int nodes = 0;
            foreach (var ch in car.GetChildren()) if (ch is MeshInstance3D mi && mi.Name.ToString().StartsWith("Glass_")) nodes++;
            T.Check($"...each pane is a real MeshInstance3D child ({nodes})", nodes == car.GlassCount);

            // ---- 1b. THE LABELS POINT THE RIGHT WAY ROUND. Front is -Z on every vehicle here: all 11 road
            // specs put SpotPos (headlights) at negative z and TailPos at positive. The generator took the
            // MAXIMUM z as the windscreen, so every car shipped with 'windshield' naming its REAR window and
            // 'l_front' naming the rear door glass. The three checks above cannot see that -- they only ask
            // whether the labels exist, and they exist either way. It is not cosmetic: ResolveHitGlass maps a
            // hit to a pane by index, so a round through the windscreen was breaking the rear window, and the
            // mechanics-panel "fix" button repaired the wrong pane.
            var wsN = car.GetNodeOrNull<MeshInstance3D>("Glass_windshield");
            var rrN = car.GetNodeOrNull<MeshInstance3D>("Glass_rear");
            if (wsN != null && rrN != null)
            {
                float wz = wsN.Position.Z + wsN.GetAabb().GetCenter().Z;
                float rz = rrN.Position.Z + rrN.GetAabb().GetCenter().Z;
                T.Check($"the windscreen sits FORWARD of the rear window (ws {wz:F2} < rear {rz:F2})", wz < rz);
                T.Check($"...and on the front half of the car (ws z {wz:F2} < 0)", wz < 0f);
            }
            var lfN = car.GetNodeOrNull<MeshInstance3D>("Glass_l_front");
            var lrN = car.GetNodeOrNull<MeshInstance3D>("Glass_l_rear");
            if (lfN != null && lrN != null)
            {
                float fz = lfN.Position.Z + lfN.GetAabb().GetCenter().Z;
                float rz2 = lrN.Position.Z + lrN.GetAabb().GetCenter().Z;
                T.Check($"the front door glass sits forward of the rear door glass ({fz:F2} < {rz2:F2})", fz < rz2);
            }

            // ---- 2. ALL INTACT AT SPAWN.
            T.Check($"a fresh car has no broken glass ({car.GlassBrokenCount})", car.GlassBrokenCount == 0);

            // ---- 3. HIT RESOLUTION. A point on the windscreen resolves to it; a point out in the field does not.
            // The negative half matters: ResolveHitGlass returning an index for everything would "work" in play
            // (shoot the car anywhere, a window breaks) and be completely wrong.
            int wsIdx = labels.IndexOf("windshield");
            var wsNode = car.GetNodeOrNull<MeshInstance3D>("Glass_windshield");
            T.Check("the windscreen node is findable by name", wsNode != null);
            if (wsNode == null) yield break;
            var wsCentre = wsNode.GlobalTransform * wsNode.GetAabb().GetCenter();
            T.Check($"a hit at the windscreen resolves to it ({car.ResolveHitGlass(wsCentre)} vs {wsIdx})",
                    car.ResolveHitGlass(wsCentre) == wsIdx);
            T.Check("a hit 8 m away resolves to no pane",
                    car.ResolveHitGlass(car.GlobalPosition + new Vector3(0f, 0f, 8f)) < 0);

            // ---- 4. BREAKING. State flips, the node hides, and a second break is a no-op rather than a
            // double-count.
            bool broke = car.BreakGlass(wsIdx);
            yield return Ticks(2);
            T.Check("breaking the windscreen reports success", broke);
            T.Check("...it reads as broken", car.IsGlassBroken(wsIdx));
            T.Check("...the mesh is hidden", !wsNode.Visible);
            T.Check($"...exactly one pane is broken ({car.GlassBrokenCount})", car.GlassBrokenCount == 1);
            T.Check("...breaking it again does nothing", !car.BreakGlass(wsIdx));
            T.Check($"...and still only one is broken ({car.GlassBrokenCount})", car.GlassBrokenCount == 1);

            // ---- 5. IT STAYS BROKEN. The whole point of the request: no self-heal over time.
            yield return Ticks(120);
            T.Check("a broken pane does NOT come back on its own", car.IsGlassBroken(wsIdx) && !wsNode.Visible);
            T.Check("...and a broken pane no longer answers hit resolution", car.ResolveHitGlass(wsCentre) != wsIdx);

            // ---- 6. REPAIR -- what the mechanics button calls.
            bool fixed_ = car.RepairGlass(wsIdx);
            yield return Ticks(2);
            T.Check("repairing reports success", fixed_);
            T.Check("...it reads intact again", !car.IsGlassBroken(wsIdx));
            T.Check("...the mesh is visible again", wsNode.Visible);
            T.Check($"...nothing is broken now ({car.GlassBrokenCount})", car.GlassBrokenCount == 0);
            T.Check("...repairing an intact pane does nothing", !car.RepairGlass(wsIdx));

            // ---- 7. A VEHICLE WITH NO GLASS FILES IS FINE. The loader must tolerate absence, or adding glass
            // to one car breaks every other one.
            var quad = Vehicle.BuildByName("quad");
            World.AddChild(quad);
            quad.GlobalPosition = new Vector3(80f, 1.2f, 0f);
            yield return Ticks(20);
            T.Check($"a quad has no glass and still builds ({quad.GlassCount})", quad.GlassCount == 0);
            T.Check("...and resolves no glass hit", quad.ResolveHitGlass(quad.GlobalPosition) < 0);

            // ---- 8. THE PANES ARE DERIVED PER BODY, not copied off the sedan. strawberry: "vehicles are each
            // fundamentally different". A 2-door roadster must not get the 4-door sedan's six panes -- if every
            // car came back with an identical set, the generator would be emitting a template and this whole
            // pass would be decoration.
            var road = Vehicle.BuildByName("roadster");
            World.AddChild(road); road.GlobalPosition = new Vector3(100f, 1.2f, 0f);
            var vanv = Vehicle.BuildByName("van");
            World.AddChild(vanv); vanv.GlobalPosition = new Vector3(120f, 1.2f, 0f);
            yield return Ticks(20);
            T.Check($"the roadster is glazed ({road.GlassCount} panes)", road.GlassCount > 0);
            T.Check($"the van is glazed ({vanv.GlassCount} panes)", vanv.GlassCount > 0);
            T.Check($"...and a 2-door roadster has FEWER panes than the 4-door sedan ({road.GlassCount} < {car.GlassCount})",
                    road.GlassCount < car.GlassCount);
            var roadLabels = new List<string>();
            for (int i = 0; i < road.GlassCount; i++) roadLabels.Add(road.GlassLabel(i));
            T.Check($"...and its set differs from the sedan's ({string.Join(",", roadLabels)})",
                    !roadLabels.SequenceEqual(labels));
        }
    }
}
