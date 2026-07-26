using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 tests for the zombie sim core (rewrite plan phase 0).
    //
    // These exist because the state lives in arrays instead of nodes. In the old system none of this
    // could be tested without booting Godot; here the sim has no engine dependency at all, which was a
    // design goal rather than a by-product (plan section 9).
    [TestFixture]
    public class ZombieSimTests
    {
        // Big regions so "far away but still in the player's region" is expressible -- with retail's
        // 128 m regions, 200 m away is a different region and therefore Ambient, not Far.
        static ZombieSim NewSim() =>
            new ZombieSim(ZombieRegions.UniformGrid(cellsPerAxis: 8, regionSize: 512f, originOffset: 2048f));

        static ZombieSim WithPlayerAtOrigin(out Vector3[] players)
        {
            var sim = NewSim();
            players = new[] { Vector3.zero };
            sim.SetPlayers(players, 1);
            return sim;
        }

        // --- handles ---------------------------------------------------------------------------------

        [Test]
        public void Spawned_Zombie_Resolves_To_A_Row_With_Its_Kind_Defaults()
        {
            var sim = NewSim();
            var id = sim.Spawn(0, new Vector3(10f, 0f, 20f));

            Assert.That(sim.Count, Is.EqualTo(1));
            Assert.That(sim.TryGetRow(id, out int row), Is.True);
            Assert.That(sim.PositionOf(row), Is.EqualTo(new Vector3(10f, 0f, 20f)));
            Assert.That(sim.HealthOf(row), Is.EqualTo(sim.Kinds[0].Health));
            Assert.That(sim.IdOf(row), Is.EqualTo(id));
        }

        [Test]
        public void Default_Handle_Is_Never_Valid()
        {
            var sim = NewSim();
            sim.Spawn(0, Vector3.zero);
            Assert.That(sim.IsAlive(ZombieId.None), Is.False);
            Assert.That(sim.IsAlive(default), Is.False);
        }

        [Test]
        public void Unknown_Kind_Fails_Loudly_Instead_Of_Spawning_A_Blank()
        {
            var sim = NewSim();
            Assert.Throws<ArgumentOutOfRangeException>(() => sim.Spawn(7, Vector3.zero));
            Assert.That(sim.Count, Is.EqualTo(0));
        }

        [Test]
        public void Despawn_Invalidates_The_Handle()
        {
            var sim = NewSim();
            var id = sim.Spawn(0, Vector3.zero);
            Assert.That(sim.Despawn(id), Is.True);
            Assert.That(sim.IsAlive(id), Is.False);
            Assert.That(sim.Despawn(id), Is.False, "double despawn must be a no-op, not a corruption");
            Assert.That(sim.Count, Is.EqualTo(0));
        }

        [Test]
        public void A_Recycled_Slot_Does_Not_Resurrect_The_Old_Handle()
        {
            // The bug this prevents: something holds a reference to a dead zombie, a new one reuses the
            // slot, and the stale reference silently starts driving the new zombie.
            var sim = NewSim();
            var dead = sim.Spawn(0, Vector3.zero);
            sim.Despawn(dead);
            var fresh = sim.Spawn(0, new Vector3(100f, 0f, 0f));

            Assert.That(fresh.Slot, Is.EqualTo(dead.Slot), "slot should be reused -- that is the point");
            Assert.That(fresh.Generation, Is.Not.EqualTo(dead.Generation));
            Assert.That(sim.IsAlive(dead), Is.False);
            Assert.That(sim.IsAlive(fresh), Is.True);
        }

        [Test]
        public void Swap_Remove_Keeps_Rows_Dense_And_Every_Survivor_Addressable()
        {
            var sim = NewSim();
            var ids = new List<ZombieId>();
            for (int i = 0; i < 50; i++) ids.Add(sim.Spawn(0, new Vector3(i, 0f, 0f)));

            for (int i = 0; i < 50; i += 2) sim.Despawn(ids[i]);

            Assert.That(sim.Count, Is.EqualTo(25), "rows must be dense -- no holes to skip in the hot loop");
            var seen = new HashSet<int>();
            for (int i = 1; i < 50; i += 2)
            {
                Assert.That(sim.TryGetRow(ids[i], out int row), Is.True, $"survivor {i} lost its row");
                Assert.That(sim.PositionOf(row).x, Is.EqualTo((float)i), $"survivor {i} got someone else's data");
                Assert.That(seen.Add(row), Is.True, $"row {row} claimed twice");
            }
        }

        // --- tiers -----------------------------------------------------------------------------------

        [Test]
        public void Tier_Follows_Distance_To_The_Nearest_Player()
        {
            var sim = WithPlayerAtOrigin(out _);
            var close = sim.Spawn(0, new Vector3(3f, 0f, 0f));
            var near = sim.Spawn(0, new Vector3(50f, 0f, 0f));
            var far = sim.Spawn(0, new Vector3(200f, 0f, 0f));
            sim.SimStep(0, 0.02);

            Assert.That(Tier(sim, close), Is.EqualTo(ZombieTier.Close));
            Assert.That(Tier(sim, near), Is.EqualTo(ZombieTier.Near));
            Assert.That(Tier(sim, far), Is.EqualTo(ZombieTier.Far));
            Assert.That(sim.Stats.Alive, Is.EqualTo(3));
            Assert.That(sim.Stats.Orphan, Is.EqualTo(0));
        }

        [Test]
        public void A_Zombie_In_A_Cold_Region_Is_Ambient_However_Close_The_Number_Looks()
        {
            var sim = WithPlayerAtOrigin(out _);
            var id = sim.Spawn(0, new Vector3(1800f, 0f, 1800f));
            sim.SimStep(0, 0.02);
            Assert.That(Tier(sim, id), Is.EqualTo(ZombieTier.Ambient));
            Assert.That(sim.Stats.Ambient, Is.EqualTo(1));
        }

        [Test]
        public void No_Players_Means_The_Whole_Level_Is_Ambient()
        {
            var sim = NewSim();
            for (int i = 0; i < 20; i++) sim.Spawn(0, new Vector3(i, 0f, 0f));
            sim.SimStep(0, 0.02);
            Assert.That(sim.Stats.Ambient, Is.EqualTo(20));
        }

        [Test]
        public void Hysteresis_Stops_A_Boundary_Loiterer_Thrashing_Its_Tier()
        {
            // NearRange 96, hysteresis 1.15 -> demote at 110.4. A zombie shuffling across 96 m would
            // otherwise flip Near/Far every tick, granting and releasing a rig each time.
            var sim = WithPlayerAtOrigin(out _);
            var id = sim.Spawn(0, new Vector3(95f, 0f, 0f));
            sim.SimStep(0, 0.02);
            Assert.That(Tier(sim, id), Is.EqualTo(ZombieTier.Near));

            var flips = new List<ZombieTier>();
            for (int t = 1; t <= 20; t++)
            {
                sim.TryGetRow(id, out int row);
                sim.SetPosition(row, new Vector3(t % 2 == 0 ? 95f : 100f, 0f, 0f));
                sim.SimStep(t, 0.02);
                flips.Add(Tier(sim, id));
            }

            CollectionAssert.DoesNotContain(flips, ZombieTier.Far, "tier thrashed across the boundary");

            // ...and the band is a band, not a wall: leave it properly and the demotion still happens.
            sim.TryGetRow(id, out int r2);
            sim.SetPosition(r2, new Vector3(140f, 0f, 0f));
            sim.SimStep(99, 0.02);
            Assert.That(Tier(sim, id), Is.EqualTo(ZombieTier.Far));
        }

        [Test]
        public void A_Fresh_Spawn_Starts_Pessimistic_Not_Close()
        {
            // Spawning must not hand out a Close tier (and later a body) before the first classification.
            var sim = WithPlayerAtOrigin(out _);
            var id = sim.Spawn(0, new Vector3(500f, 0f, 500f));
            sim.TryGetRow(id, out int row);
            Assert.That(sim.TierOf(row), Is.EqualTo(ZombieTier.Ambient));
        }

        // --- the schedule ------------------------------------------------------------------------------

        [Test]
        public void Close_And_Near_Zombies_Think_Every_Tick()
        {
            var sim = WithPlayerAtOrigin(out _);
            for (int i = 0; i < 10; i++) sim.Spawn(0, new Vector3(3f + i * 5f, 0f, 0f));
            for (int t = 0; t < 5; t++)
            {
                sim.SimStep(t, 0.02);
                Assert.That(sim.Stats.Due, Is.EqualTo(10), $"tick {t}");
            }
        }

        [Test]
        public void Far_Zombies_Think_Exactly_Once_Per_Stride_And_The_Work_Is_Spread()
        {
            var sim = WithPlayerAtOrigin(out _);
            const int n = 100;
            var ids = new List<ZombieId>();
            for (int i = 0; i < n; i++) ids.Add(sim.Spawn(0, new Vector3(150f + i * 0.5f, 0f, 0f)));
            sim.SimStep(0, 0.02);
            Assert.That(sim.Stats.Far, Is.EqualTo(n), "test setup: all of them should be Far");

            var thoughts = new Dictionary<ZombieId, int>();
            for (int t = 0; t < sim.FarStride; t++)
            {
                sim.SimStep(t, 0.02);
                Assert.That(sim.Stats.Due, Is.EqualTo(n / sim.FarStride),
                    $"tick {t}: the 10 Hz tier bunched up instead of spreading across its stride");
                foreach (int row in sim.DueRows.ToArray())
                {
                    var id = sim.IdOf(row);
                    thoughts[id] = thoughts.TryGetValue(id, out int c) ? c + 1 : 1;
                }
            }

            Assert.That(thoughts.Count, Is.EqualTo(n), "some zombie never got a turn inside its stride");
            Assert.That(thoughts.Values.All(v => v == 1), Is.True, "some zombie got two turns inside its stride");
        }

        [Test]
        public void With_The_Player_Elsewhere_A_Big_Fleet_Costs_One_Fiftieth_Of_Itself_Per_Tick()
        {
            // Requirement 8, as a number instead of a screenshot. Note what this does and does not claim:
            // per-tick work is N/AmbientStride scalar rows -- NOT zero and not literally flat in N. The
            // part that is flat is the engine cost: no bodies, no rigs, no path queries, no swept moves.
            var sim = NewSim();
            var players = new[] { new Vector3(-1900f, 0f, -1900f) };
            sim.SetPlayers(players, 1);

            const int n = 2000;
            for (int i = 0; i < n; i++) sim.Spawn(0, new Vector3(1500f + (i % 40), 0f, 1500f + (i / 40)));

            var thoughts = new HashSet<int>();
            for (int t = 0; t < sim.AmbientStride; t++)
            {
                sim.SimStep(t, 0.02);
                Assert.That(sim.Stats.Ambient, Is.EqualTo(n));
                Assert.That(sim.Stats.Due, Is.LessThanOrEqualTo(n / sim.AmbientStride + 1), $"tick {t}");
                foreach (int row in sim.DueRows.ToArray()) thoughts.Add(sim.IdOf(row).Slot);
            }

            Assert.That(thoughts.Count, Is.EqualTo(n), "an ambient zombie starved -- it would never advance");
        }

        [Test]
        public void The_Spatial_Grid_Tracks_The_Live_Rows_Each_Step()
        {
            var sim = WithPlayerAtOrigin(out _);
            var ids = new List<ZombieId>();
            for (int i = 0; i < 30; i++) ids.Add(sim.Spawn(0, new Vector3(i * 2f, 0f, 0f)));
            sim.SimStep(0, 0.02);

            var buf = new int[64];
            Assert.That(sim.Spatial.QuerySphere(Vector3.zero, 10f, buf), Is.EqualTo(6), "0,2,4,6,8,10 m");

            for (int i = 0; i < 30; i += 2) sim.Despawn(ids[i]);
            sim.SimStep(1, 0.02);
            Assert.That(sim.Spatial.Count, Is.EqualTo(15));
            Assert.That(sim.Spatial.QuerySphere(Vector3.zero, 10f, buf), Is.EqualTo(3), "2,6,10 m survive");
        }

        static ZombieTier Tier(ZombieSim sim, ZombieId id)
        {
            Assert.That(sim.TryGetRow(id, out int row), Is.True, "handle went stale mid-test");
            return sim.TierOf(row);
        }
    }
}
