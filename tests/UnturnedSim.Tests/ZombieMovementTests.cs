using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 tests for zombie movement and budgeted pathing (rewrite plan phase 1).
    //
    // None of this needs the engine. The navmesh arrives through IZombieNavQuery, so a test can hand the
    // sim a flat plane, a dog-leg corridor, or a navmesh that refuses to answer, and assert what the
    // zombies do about it. That is the payoff of keeping the sim engine-free rather than the point of it.
    [TestFixture]
    public class ZombieMovementTests
    {
        // Counts what the sim asks for, and hands back a fixed corridor.
        sealed class RecordingNav : IZombieNavQuery
        {
            public readonly List<(Vector3 From, Vector3 To)> Queries = new List<(Vector3, Vector3)>();
            public Vector3[] Corridor;          // null -> straight line to the target
            public bool Refuse;                 // answer "no route"

            public int QueryPath(Vector3 from, Vector3 to, Vector3[] corridor)
            {
                Queries.Add((from, to));
                if (Refuse) return 0;
                if (Corridor == null) { corridor[0] = new Vector3(to.x, 0f, to.z); return 1; }
                int n = Math.Min(Corridor.Length, corridor.Length);
                for (int i = 0; i < n; i++) corridor[i] = Corridor[i];
                return n;
            }

            public Vector3 SnapToSurface(Vector3 p) => new Vector3(p.x, 0f, p.z);
        }

        static ZombieSim NewSim(IZombieNavQuery nav, out Vector3[] players)
        {
            var sim = new ZombieSim(ZombieRegions.UniformGrid(cellsPerAxis: 8, regionSize: 512f, originOffset: 2048f));
            sim.Nav = nav;
            players = new[] { Vector3.zero };
            sim.SetPlayers(players, 1);
            return sim;
        }

        static void Run(ZombieSim sim, int ticks, long from = 0)
        {
            for (long t = from; t < from + ticks; t++) sim.SimStep(t, 0.02);
        }

        [Test]
        public void A_Zombie_Walks_To_The_Player_Without_Any_Physics_Body()
        {
            var sim = NewSim(new RecordingNav(), out _);
            var id = sim.Spawn(0, new Vector3(20f, 0f, 0f));
            Run(sim, 50 * 20);   // 20 seconds

            sim.TryGetRow(id, out int row);
            Assert.That(sim.PositionOf(row).magnitude, Is.LessThan(2f),
                $"ended at {sim.PositionOf(row)} -- it should have reached the player");
        }

        [Test]
        public void Speed_Is_The_Kind_Record_Not_The_Tick_Rate()
        {
            var sim = NewSim(new RecordingNav(), out _);
            sim.PursueRange = 1000f;
            var id = sim.Spawn(0, new Vector3(200f, 0f, 0f));
            float speed = sim.Kinds[0].MoveSpeed;

            Run(sim, 50 * 4);   // 4 s
            sim.TryGetRow(id, out int row);
            float travelled = 200f - sim.PositionOf(row).x;
            Assert.That(travelled, Is.EqualTo(speed * 4f).Within(speed * 0.25f),
                "distance covered should be speed x elapsed time, whatever tier it was in");
        }

        [Test]
        public void A_Far_Zombie_Moves_At_The_Same_Speed_As_A_Near_One()
        {
            // The trap this guards: Far updates at 10 Hz, so integrating a 50 Hz step would make it crawl
            // at a fifth speed and visibly change pace the moment it crossed a tier boundary.
            var near = NewSim(new RecordingNav(), out _);
            near.PursueRange = 1000f;
            var a = near.Spawn(0, new Vector3(40f, 0f, 0f));       // inside NearRange 96
            Run(near, 50 * 3);
            near.TryGetRow(a, out int ra);
            Assert.That(near.TierOf(ra), Is.EqualTo(ZombieTier.Near), "test setup");
            float nearTravel = 40f - near.PositionOf(ra).x;

            var far = NewSim(new RecordingNav(), out _);
            far.PursueRange = 1000f;
            var b = far.Spawn(0, new Vector3(200f, 0f, 0f));       // beyond NearRange -> Far
            Run(far, 50 * 3);
            far.TryGetRow(b, out int rb);
            Assert.That(far.TierOf(rb), Is.EqualTo(ZombieTier.Far), "test setup");
            float farTravel = 200f - far.PositionOf(rb).x;

            Assert.That(farTravel, Is.EqualTo(nearTravel).Within(nearTravel * 0.2f),
                $"near covered {nearTravel:F2} m, far covered {farTravel:F2} m -- the stride is not being integrated");
        }

        [Test]
        public void It_Follows_The_Corridor_Round_A_Corner_Instead_Of_Beelining()
        {
            // A dog-leg: the direct line from the spawn to the player cuts a corner the route goes around.
            var nav = new RecordingNav
            {
                Corridor = new[] { new Vector3(30f, 0f, 30f), new Vector3(0f, 0f, 30f), Vector3.zero },
            };
            var sim = NewSim(nav, out _);
            sim.PursueRange = 1000f;
            var id = sim.Spawn(0, new Vector3(30f, 0f, 0f));

            // 30 s at the kind's 1.6 m/s covers ~48 m, which is enough to walk the 30 m out-leg and turn.
            bool sawTheCorner = false;
            for (int t = 0; t < 50 * 30; t++)
            {
                sim.SimStep(t, 0.02);
                sim.TryGetRow(id, out int r);
                if (sim.PositionOf(r).z > 25f) sawTheCorner = true;
            }
            Assert.That(sawTheCorner, Is.True, "it cut the corner rather than following the route");
        }

        [Test]
        public void An_Idle_Zombie_Never_Asks_For_A_Path()
        {
            var nav = new RecordingNav();
            var sim = NewSim(nav, out _);
            sim.Spawn(0, new Vector3(400f, 0f, 400f));   // way outside PursueRange
            Run(sim, 200);
            Assert.That(nav.Queries, Is.Empty, "a zombie with nothing to chase queried the navmesh");
        }

        [Test]
        public void A_Horde_That_All_Wakes_At_Once_Cannot_Blow_The_Path_Budget()
        {
            // The exact failure the budget exists for: sixty zombies hear one gunshot on the same tick.
            var nav = new RecordingNav();
            var sim = NewSim(nav, out _);
            sim.PursueRange = 1000f;
            sim.PathQueriesPerTick = 8;
            for (int i = 0; i < 60; i++) sim.Spawn(0, new Vector3(30f + i * 0.1f, 0f, 20f));

            for (int t = 0; t < 40; t++)
            {
                sim.SimStep(t, 0.02);
                Assert.That(sim.Stats.PathQueries, Is.LessThanOrEqualTo(8), $"tick {t} issued {sim.Stats.PathQueries} queries");
            }
            Assert.That(nav.Queries.Count, Is.GreaterThan(0), "the backlog never drained at all");
        }

        [Test]
        public void The_Backlog_Drains_Rather_Than_Starving_Anyone()
        {
            var nav = new RecordingNav();
            var sim = NewSim(nav, out _);
            sim.PursueRange = 1000f;
            sim.PathQueriesPerTick = 4;
            var ids = new List<ZombieId>();
            for (int i = 0; i < 40; i++) ids.Add(sim.Spawn(0, new Vector3(30f, 0f, 20f + i * 0.5f)));

            Run(sim, 50 * 3);
            foreach (var id in ids)
            {
                sim.TryGetRow(id, out int row);
                Assert.That(sim.PositionOf(row), Is.Not.EqualTo(sim.DestinationOf(row)));
                Assert.That(sim.StateOf(row), Is.EqualTo(ZombieState.Pursue));
            }
            Assert.That(sim.Stats.PathQueued, Is.LessThan(40), "the queue never cleared -- someone is starving");
        }

        [Test]
        public void A_Zombie_With_No_Route_Stands_Still_Instead_Of_Walking_Through_The_Wall()
        {
            var nav = new RecordingNav { Refuse = true };
            var sim = NewSim(nav, out _);
            sim.PursueRange = 1000f;
            var id = sim.Spawn(0, new Vector3(20f, 0f, 0f));
            Run(sim, 50 * 3);

            sim.TryGetRow(id, out int row);
            Assert.That(sim.PositionOf(row), Is.EqualTo(new Vector3(20f, 0f, 0f)),
                "no navmesh route should mean no movement, not a beeline");
            Assert.That(nav.Queries.Count, Is.GreaterThan(0), "it should still be asking");
        }

        [Test]
        public void Nothing_Moves_When_No_Navmesh_Is_Attached()
        {
            var sim = new ZombieSim(ZombieRegions.UniformGrid(cellsPerAxis: 8, regionSize: 512f, originOffset: 2048f));
            sim.SetPlayers(new[] { Vector3.zero }, 1);
            var id = sim.Spawn(0, new Vector3(10f, 0f, 0f));
            Run(sim, 100);

            sim.TryGetRow(id, out int row);
            Assert.That(sim.PositionOf(row), Is.EqualTo(new Vector3(10f, 0f, 0f)));
            Assert.That(sim.Stats.Moving, Is.EqualTo(0));
        }

        [Test]
        public void The_Corridor_Is_Refetched_When_The_Player_Walks_Away()
        {
            var nav = new RecordingNav();
            var sim = NewSim(nav, out var players);
            sim.PursueRange = 1000f;
            sim.Spawn(0, new Vector3(30f, 0f, 0f));
            Run(sim, 20);
            int afterFirst = nav.Queries.Count;

            players[0] = new Vector3(0f, 0f, 60f);   // target moved well past DestMovedTolerance
            Run(sim, 20, from: 20);
            Assert.That(nav.Queries.Count, Is.GreaterThan(afterFirst), "the stale corridor was never refetched");
            Assert.That(nav.Queries[nav.Queries.Count - 1].To.z, Is.EqualTo(60f).Within(0.01f));
        }

        [Test]
        public void Teleporting_A_Zombie_Drops_The_Route_It_Was_Following()
        {
            var nav = new RecordingNav();
            var sim = NewSim(nav, out _);
            sim.PursueRange = 1000f;
            var id = sim.Spawn(0, new Vector3(30f, 0f, 0f));
            Run(sim, 30);
            sim.TryGetRow(id, out int row);
            Assert.That(sim.WaypointsRemaining(row), Is.GreaterThan(0), "test setup: it should have a route");

            sim.SetPosition(row, new Vector3(-400f, 0f, 400f));
            Assert.That(sim.WaypointsRemaining(row), Is.EqualTo(0),
                "a route computed from where it used to be is not a route from where it is");
        }

        [Test]
        public void Despawning_Mid_Chase_Does_Not_Hand_The_Route_To_Someone_Else()
        {
            // Rows swap-remove, so a queued path request naming row 3 could name a different zombie by
            // the time it drains. This asserts the survivors still behave, rather than one inheriting a
            // corridor computed for a corpse.
            var nav = new RecordingNav();
            var sim = NewSim(nav, out _);
            sim.PursueRange = 1000f;
            var ids = new List<ZombieId>();
            for (int i = 0; i < 20; i++) ids.Add(sim.Spawn(0, new Vector3(30f + i, 0f, 10f)));
            Run(sim, 25);

            for (int i = 0; i < 20; i += 2) sim.Despawn(ids[i]);
            Run(sim, 50 * 4, from: 25);

            for (int i = 1; i < 20; i += 2)
            {
                Assert.That(sim.TryGetRow(ids[i], out int row), Is.True);
                Assert.That(sim.PositionOf(row).magnitude, Is.LessThan(30f + i),
                    $"survivor {i} never made progress toward the player");
            }
        }
    }
}
