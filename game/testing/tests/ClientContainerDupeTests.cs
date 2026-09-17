using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Testing
{
    // strawberry, 2026-09-17: "theres a lot of stuff that exists on singleplayer loopback but not on the
    // vox server. 'smart' containers, vehicle glass, etc etc." This is the containers half.
    //
    // A container placement takes ONE of two forms. In Playable/Dedicated the placement loop FLAGS it --
    // the decoration mesh is skipped and a real StoreShelf goes in at that transform. On a joined CLIENT
    // the flag returns false, so the object falls through to PlaceObject and is drawn as ordinary scenery,
    // collider and all -- while StorageReplicaView ALSO materialises a StoreShelf there from the server's
    // fixture. Two meshes, two colliders, one spot: z-fighting, and a decoration body sitting in front of
    // the shelf for the F-interact ray to hit instead. WorldBuilder already logged this as a suspicion
    // ("each is drawn TWICE unless one side is suppressed"); this test is the part that was missing, which
    // is a NUMBER rather than a suspicion.
    //
    // ⚠ WHY SUPPRESSING THE DECORATION IS SAFE, AND THE CONDITION IT RIDES ON. StorageReplicaView's own
    // comment refused this fix pending proof: "Suppress while the replica is short (interest culling, a
    // dropped fixture) and those containers do not merely double, they VANISH: the original bug back, and
    // indistinguishable from it." That is a real hazard and it is NOT hypothetical -- ContainerReplication
    // has an InterestPolicy field and honours it (ids filtered by IsRelevant, removals collected). It is
    // safe only because nothing ever ASSIGNS it: DedicatedServer sets Interest on WorldItems and on nothing
    // else, so every client receives the full container set. So the invariant is pinned HERE, loudly, in
    // the test rather than left as a null someone tidies up later: set Containers.Interest and this test
    // fails and tells you the client will start losing fridges.
    public class ClientContainerDupes : GameTest
    {
        public override string Name => "world.client_container_dupes";
        public override double TimeoutSimSeconds => 180;

        static IEnumerable<Node> AllNodes(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                yield return c;
                foreach (var d in AllNodes(c)) yield return d;
            }
        }

        // A decoration drawn at a container's transform. PlaceObject builds its node at
        // gpos = (px, py, -pz), and the container record stores (F(q[1]), F(q[2]), -F(q[3])) from the same
        // placement row, so the two are the SAME float triple -- an exact-ish probe, not a fuzzy one.
        static int DecorationsAt(Node world, List<Vector3> spots)
        {
            var hits = new HashSet<int>();
            foreach (var n in AllNodes(world))
            {
                if (n is not VisualInstance3D && n is not StaticBody3D) continue;
                var o = ((Node3D)n).GlobalPosition;
                for (int i = 0; i < spots.Count; i++)
                    if (o.DistanceSquaredTo(spots[i]) < 0.0025f) { hits.Add(i); break; }
            }
            return hits.Count;
        }

        // ⚠ ANTI-VACUITY. The treatment asserts "no decoration at these spots", and the cheapest way for
        // that to be true is for the client to have built NOTHING -- a pass that looks exactly like the
        // pass we want. So count what the client DOES draw and require it to be a real world, not an empty
        // one. A floor rather than parity with the control: the two modes legitimately differ (the client
        // defers holiday content to the join handshake, builds no local player), so a tight band would be a
        // flake generator. Both totals go to the LOG (see the Log.Print below, not the check label), so the
        // real distribution is on the record for whoever next wants to tighten this.
        static int VisualCount(Node world)
        {
            int n = 0;
            foreach (var x in AllNodes(world)) if (x is VisualInstance3D) n++;
            return n;
        }

        public override IEnumerable<Step> Run()
        {
            string mapRoot = (System.Environment.GetEnvironmentVariable("UG_UNTURNED_DIR")?.TrimEnd('\\', '/')
                               ?? "/home/ec2-user/unturned") + "/Maps/PEI";
            if (!System.IO.Directory.Exists(mapRoot + "/Landscape/Heightmaps"))
            {
                T.Check("SKIPPED -- no real Unturned install found (set UG_UNTURNED_DIR)", true);
                yield break;
            }

            // A real map build writes Terrain's statics globally (the ladder.real_world_repro lesson).
            bool hadWater = Terrain.HasWater; float oldSea = Terrain.SeaLevelY;
            var oldActive = Terrain.Active; string oldMapDir = Terrain.MapDir;
            var spots = new List<Vector3>();
            try
            {
                // CONTROL. Singleplayer: the placements really do carry containers, and the decoration mesh
                // for each is SUPPRESSED -- nothing is drawn at those transforms during the build (the real
                // StoreShelf is spawned by the caller afterwards, once the asset DB is up). Without this leg
                // the treatment below is unreadable: "no decoration at position P" would also be what you
                // get if P were simply not a container.
                var t1 = WorldBuilder.BuildFullWorld(World, WorldMode.Playable, mapRoot, "placements.txt",
                                                     syncLoad: true, activeHoliday: "NONE");
                yield return Until(() => t1.IsCompleted, 90);
                T.Check("control world built", t1.Result.Ready);
                foreach (var c in t1.Result.Containers) spots.Add(c.pos);
                T.Check($"CONTROL: PEI's placements carry containers ({spots.Count})", spots.Count > 0);
                int spDeco = DecorationsAt(World, spots);
                T.Check($"CONTROL: singleplayer draws NO decoration at a container's transform ({spDeco} of {spots.Count})", spDeco == 0);
                int spVisuals = VisualCount(World);

                foreach (var c in World.GetChildren()) c.QueueFree();
                yield return Ticks(3);

                // TREATMENT. The same map as a joined client. The client records no containers of its own --
                // correct, the server owns them -- but it must not draw the scenery copy either, because the
                // server's fixture is about to put a StoreShelf on top of it.
                var t2 = WorldBuilder.BuildFullWorld(World, WorldMode.Client, mapRoot, "placements.txt",
                                                     syncLoad: true, activeHoliday: "NONE");
                yield return Until(() => t2.IsCompleted, 90);
                T.Check("client world built", t2.Result.Ready);
                T.Check($"a client records no containers of its own ({t2.Result.Containers.Count})", t2.Result.Containers.Count == 0);
                int clVisuals = VisualCount(World);
                // Logged, not merely put in a check label: a PASSING check's text is recorded nowhere, so a
                // label is only "on the record" for a run that fails. Whoever wants to tighten the floor
                // below into a band needs these two numbers from a GREEN run.
                Log.Print($"[containers] client world {clVisuals} visuals vs the singleplayer control's {spVisuals} ({spots.Count} containers)");
                T.Check($"the client built a REAL world, so the count below means something ({clVisuals} visuals vs the control's {spVisuals})", clVisuals > spots.Count * 2);
                int clDeco = DecorationsAt(World, spots);
                T.Check($"a client draws NO second copy at a container's transform ({clDeco} of {spots.Count} doubled)", clDeco == 0);
            }
            finally
            {
                Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea;
                Terrain.Active = oldActive; Terrain.MapDir = oldMapDir;
            }
        }
    }
}
