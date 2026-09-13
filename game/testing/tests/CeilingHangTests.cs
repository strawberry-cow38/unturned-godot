using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // A ceiling fixture must HANG. Shipped 2026-09-13 with all three pendants inverted -- plate on the slab, bulb
    // buried in it (strawberry: "they deploy upside down, point up into the ceiling instead of hanging down") --
    // because the defs carried no MeshEuler and the comment justifying that had the sign backwards. The meshes are
    // authored from z=0 at the plate into NEGATIVE z, and in this frame negative z is UP: StandBasis is a +90 about
    // X, which sends mesh -Z to world +Y.
    //
    // ⚠ WHY NO EXISTING CHECK CAUGHT IT, which is the more useful half. The pendants were verified through
    // `tools/shot.py lamp` (--lamptest), and that harness builds the MeshInstance at RotationDegrees(pitch,0,0) with
    // pitch defaulting to 0 -- raw authored coordinates, no StandBasis and no MeshBasis. It is structurally incapable
    // of showing an error in the composition, so four rounds of renders agreed the models hung correctly and the game
    // disagreed. Same blindness as the `prop` scene drawing raw mesh coords. This test exists because the render
    // instrument cannot see the transform under test: it composes the REAL one instead.
    //
    // Placed through Barricade.PlaceOnSurface, not Deployable.Spawn -- Spawn seats everything the FLOOR way, so a
    // test calling it would exercise a path a ceiling barricade never takes. That distinction is not academic: the
    // re-seat PlaceOnSurface does after Spawn is what left the omni 0.09 m inside the slab (LampLight.Reanchor).
    public class CeilingHangs : GameTest
    {
        public override string Name => "deploy.ceiling_hangs";

        // The fixture's whole visible extent in WORLD space. Not `mi.Mesh.GetAabb()` on its own: LampLight carves the
        // emitter onto a CHILD MeshInstance and leaves the housing behind (LampLight.cs, `_fixture.Mesh = body`), so
        // the parent mesh alone stops at the socket and reports the bulb-less 0.316 m where the model is 0.398 m.
        // Measuring the housing and calling it the fixture is the same class of error as measuring the .obj and
        // calling it the placed object.
        internal static Aabb VisualBounds(Node3D root)
        {
            Aabb acc = default; bool any = false;
            void Walk(Node n)
            {
                // VISIBLE meshes only. A Deployable carries hidden geometry -- the wire-tool arrows on every
                // ConnectionPort are MeshInstance3Ds parked until the tool asks for them -- and counting those
                // measures the scene graph rather than the thing a player looks at.
                if (n is MeshInstance3D m && m.Mesh != null && m.IsVisibleInTree())
                {
                    Aabb w = m.GlobalTransform * m.Mesh.GetAabb();
                    acc = any ? acc.Merge(w) : w; any = true;
                }
                foreach (var c in n.GetChildren()) Walk(c);
            }
            Walk(root);
            return acc;
        }

        public override IEnumerable<Step> Run()
        {
            const float CeilY = 3f;
            var placed = new List<(DeployableDef def, Deployable d)>();
            float x = 0f;
            foreach (var def in DeployableDef.All)
            {
                if (def.Mount != BarricadeMount.Ceiling) continue;
                placed.Add((def, Barricade.PlaceOnSurface(World, def, new Vector3(x, CeilY, 0f), Vector3.Down, 0f)));
                x += 3f;
            }

            // Not "> 0": the loop IS the coverage, so a def silently dropping out of All would leave a green suite
            // testing less than it did. 3 is the count at the time of writing -- raise it when a fourth lands.
            T.Check($"found the ceiling-mount defs to test (got {placed.Count}, want >= 3)", placed.Count >= 3);
            yield return Ticks(2);

            foreach (var (def, d) in placed)
            {
                var mi = d.DebugMesh;
                if (mi == null || mi.Mesh == null) { T.Check($"{def.Name}: has a body mesh", false); continue; }

                Aabb w = VisualBounds(mi);
                float oy = d.GlobalPosition.Y;   // the mount origin: hit point + normal * standoff

                // Both halves stated, because together they pin the fixture to [origin - span, origin] and the
                // inverted build lands on exactly [origin, origin + span] -- each check fails on its own.
                T.Check($"{def.Name}: plate sits AT the mount plane (top is {w.End.Y - oy:0.000} m off the origin, want ~0)",
                        Mathf.Abs(w.End.Y - oy) <= 0.01f);
                T.Check($"{def.Name}: body hangs its full {w.Size.Y:0.000} m BELOW it (bottom is {w.Position.Y - oy:0.000} m off the origin)",
                        Mathf.Abs((w.Position.Y - oy) + w.Size.Y) <= 0.01f);

                // ...and the plate is against the SLAB, not merely against its own origin. The two checks above are
                // both relative to the body, so a standoff that floats the whole fixture passes them untouched --
                // which it did: Offset's clamped 0.05 hung every canopy 5 cm clear of the ceiling.
                T.Check($"{def.Name}: plate is flush with the ceiling ({CeilY - w.End.Y:0.000} m clear of it)",
                        CeilY - w.End.Y >= 0f && CeilY - w.End.Y <= 0.01f);

                // The bulb is at the BOTTOM of a pendant. Independent of the two above -- they place the bounding
                // box, this places the part inside it -- and it is the half a player actually looks at. It also
                // still fails if a future model is authored with the socket and bulb swapped, which a bounds check
                // on its own would happily accept.
                MeshInstance3D em = null;
                foreach (var c in mi.GetChildren()) if (c is MeshInstance3D e && e.Name == "Emissive") { em = e; break; }
                if (em == null || em.Mesh == null) { T.Check($"{def.Name}: has a split-out emissive bulb", false); continue; }
                Aabb ew = em.GlobalTransform * em.Mesh.GetAabb();
                float frac = w.Size.Y > 0.001f ? (w.End.Y - ew.GetCenter().Y) / w.Size.Y : 0f;   // 0 = at the plate, 1 = at the very bottom
                T.Check($"{def.Name}: the bulb is in the lower half of the fixture ({frac:0.00} of the way down)", frac > 0.5f);

                // And the light goes with it. Kind.CeilingBulb anchors the omni on that emissive sub-mesh, so this
                // is true by construction once the mesh is right AND the lamp has been re-anchored after the mount
                // re-seat. It was 0.09 m ABOVE the plate before Reanchor existed, which no bounds check would see.
                LampLight lamp = null;
                foreach (var c in d.GetChildren()) if (c is LampLight l) { lamp = l; break; }
                if (lamp == null) { T.Check($"{def.Name}: has a LampLight", false); continue; }
                T.Check($"{def.Name}: the omni sits below the plate ({lamp.DebugLightWorld.Y - oy:0.000} m off the origin)",
                        lamp.DebugLightWorld.Y < oy - 0.05f);
            }
        }
    }

    // ...AND THE SAME THING OVER THE WIRE, which is where it actually broke for the player.
    //
    // CeilingHangs above places through Barricade.PlaceOnSurface and passed while the game was still wrong,
    // because that is the SINGLEPLAYER path. `--peidrive`, the mode the game is played in, is a listen server: the
    // placement goes out as PlaceDeployableCommand(DefId, Pos, Yaw), the server stores Pos as the raw surface point,
    // and DeployableReplicaView builds the node. It built every def through Deployable.Spawn -- which lifts a body
    // UP by GroundLift so its base rests on the point -- so a pendant was shoved up by its own height into the slab
    // (strawberry: "the light gets placed in the ceiling, but orientation is correct"). Orientation survived because
    // MountBasis returns plain StandBasis(yaw) for Ceiling: only the SEAT was wrong, which is precisely the half a
    // local test and a look-at-the-render both pass.
    //
    // So this one asserts on the node DeployableReplicaView produced, reached the way the client reaches it.
    public class NetCeilingPendantSeats : GameTest
    {
        public override string Name => "net.ceiling_pendant_seats";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20260913);
            world.Sim.Sim.Add(new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump"));
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "hanger" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Until(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            bool sHave = ded.Server.Inventories.TryGet(sess.Client.PlayerId, out var sInv);
            yield return Ticks(10);

            const ushort PendantId = 9210;   // Ceiling Bulb -- the plainest of the three
            T.Check("server granted the pendant", sHave && sInv.Inventory.tryAddItem(new Item(PendantId)));
            yield return Until(() => sess.Shell.Inventory.getItemCount(PendantId) == 1, 5);
            T.Check("the grant echoed into the bag", sess.Shell.Inventory.getItemCount(PendantId) == 1);

            // The point the wire carries is the raw CEILING HIT, well above the player -- exactly what
            // BarricadePlacer freezes and RequestPlaceDeployable sends.
            Vector3 hit = sess.Shell.GlobalPosition + new Vector3(0f, 3f, 0f);
            T.Check("the place request fired", sess.Shell.RequestPlaceDeployable(PendantId, hit, 0f));
            yield return Until(() => ded.Server.Deployables.Count == 1, 5);
            T.Check("the SERVER planted it", ded.Server.Deployables.Count == 1);

            Deployable node = null;
            yield return Until(() =>
            {
                foreach (var e in sess.Client.Deployables.All)
                    if (sess.Deploys.TryGetNode(e.NetIdValue, out node)) return true;
                return false;
            }, 5);
            T.Check("the replica view rendered the node", node != null);
            if (node == null) yield break;
            yield return Ticks(3);

            Aabb w = CeilingHangs.VisualBounds(node);   // the WHOLE placed object, port cubes included -- nothing it shows should be inside the slab
            T.Check($"the fixture is BELOW the ceiling hit, not driven up into it (top {w.End.Y - hit.Y:0.000} m, bottom {w.Position.Y - hit.Y:0.000} m relative to the hit)",
                    w.End.Y <= hit.Y + 0.01f && w.Position.Y < hit.Y - 0.05f);

            // The failure this replaces put the WHOLE fixture above the hit, so a top-only check would have caught
            // it -- but the bottom is stated too, because a half-sunk fixture is the shape a partial fix leaves.
            T.Check($"and it hangs its full height ({w.Size.Y:0.000} m below the slab)",
                    Mathf.Abs((hit.Y - w.Position.Y) - w.Size.Y) <= 0.02f);

            LampLight lamp = null;
            foreach (var c in node.GetChildren()) if (c is LampLight l) { lamp = l; break; }
            T.Check("the replicated pendant built a LampLight", lamp != null);
            if (lamp != null)
                T.Check($"and its omni is under the ceiling ({lamp.DebugLightWorld.Y - hit.Y:0.000} m relative to the hit)",
                        lamp.DebugLightWorld.Y < hit.Y - 0.05f);
        }
    }
}
