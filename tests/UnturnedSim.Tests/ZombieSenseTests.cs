using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 tests for zombie sensing and the state machine (rewrite plan phase 2).
    //
    // Sight and hearing both reach the sim through interfaces, so the awkward cases -- a wall appearing
    // mid-chase, two noises competing, a player standing behind a zombie's back -- are ordinary tests
    // rather than things you can only hope to reproduce by playing.
    [TestFixture]
    public class ZombieSenseTests
    {
        // A wall the test can raise and drop between shots.
        sealed class Blindfold : IZombieLineOfSight
        {
            public bool Blocked;
            public bool CanSee(Vector3 from, Vector3 to) => !Blocked;
        }

        static ZombieSim NewSim(out Vector3[] players, out Blindfold sight)
        {
            var sim = new ZombieSim(ZombieRegions.UniformGrid(cellsPerAxis: 8, regionSize: 512f, originOffset: 2048f));
            sim.Nav = new FlatGroundNav();
            sight = new Blindfold();
            sim.Sight = sight;
            players = new[] { Vector3.zero };
            sim.SetPlayers(players, 1);
            return sim;
        }

        static void Run(ZombieSim sim, int ticks, ref long t)
        {
            for (int i = 0; i < ticks; i++) sim.SimStep(t++, 0.02);
        }

        static ZombieState State(ZombieSim sim, ZombieId id)
        {
            Assert.That(sim.TryGetRow(id, out int row), Is.True);
            return sim.StateOf(row);
        }

        // Zombies spawn facing +z, so a player on +z is in the cone and one on -z is behind them.
        static ZombieId SpawnFacingThePlayer(ZombieSim sim, Vector3 at) => sim.Spawn(0, at);

        [Test]
        public void A_Player_In_The_Cone_Is_Seen_And_Chased()
        {
            long t = 0;
            var sim = NewSim(out var players, out _);
            players[0] = new Vector3(0f, 0f, 20f);
            var id = SpawnFacingThePlayer(sim, Vector3.zero);

            Run(sim, 5, ref t);
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Pursue));
        }

        [Test]
        public void A_Player_Behind_The_Zombie_Is_Not_Seen()
        {
            long t = 0;
            var sim = NewSim(out var players, out _);
            players[0] = new Vector3(0f, 0f, -20f);   // directly behind: outside a 60 degree half-cone
            var id = SpawnFacingThePlayer(sim, Vector3.zero);

            Run(sim, 5, ref t);
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Idle));
        }

        [Test]
        public void A_Player_Beyond_Sight_Range_Is_Not_Seen()
        {
            long t = 0;
            var sim = NewSim(out var players, out _);
            players[0] = new Vector3(0f, 0f, 300f);   // kind SightRange is 48
            var id = SpawnFacingThePlayer(sim, Vector3.zero);

            Run(sim, 5, ref t);
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Idle));
        }

        [Test]
        public void A_Wall_Blocks_Sight_Even_Dead_Ahead()
        {
            long t = 0;
            var sim = NewSim(out var players, out var sight);
            players[0] = new Vector3(0f, 0f, 20f);
            sight.Blocked = true;
            var id = SpawnFacingThePlayer(sim, Vector3.zero);

            Run(sim, 5, ref t);
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Idle), "it saw through a wall");

            sight.Blocked = false;
            Run(sim, 5, ref t);
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Pursue), "it did not notice the wall coming down");
        }

        [Test]
        public void Losing_Sight_Sends_It_To_Look_Rather_Than_Tracking_Through_Walls()
        {
            long t = 0;
            var sim = NewSim(out var players, out var sight);
            players[0] = new Vector3(0f, 0f, 20f);
            var id = SpawnFacingThePlayer(sim, Vector3.zero);
            Run(sim, 10, ref t);
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Pursue), "test setup");

            sight.Blocked = true;
            players[0] = new Vector3(0f, 0f, 45f);        // they ran off while out of sight

            Run(sim, 20, ref t);                          // inside the grace window
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Pursue), "brief occlusion should not break the chase");

            Run(sim, 150, ref t);                         // past LoseSightGrace
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Investigate),
                "it should go look where they were, not keep tracking them");

            sim.TryGetRow(id, out int row);
            Assert.That(sim.DestinationOf(row).z, Is.EqualTo(20f).Within(1.5f),
                "it should be heading for the LAST SEEN spot, not the current one");
        }

        [Test]
        public void A_Noise_Starts_An_Investigation()
        {
            long t = 0;
            var sim = NewSim(out var players, out var sight);
            players[0] = new Vector3(0f, 0f, -500f);      // nowhere near
            sight.Blocked = true;
            var id = SpawnFacingThePlayer(sim, Vector3.zero);
            Run(sim, 2, ref t);

            int heard = sim.Hear(new Vector3(0f, 0f, 15f), 30f);
            Assert.That(heard, Is.EqualTo(1));

            // A zombie with no player near it thinks at 10 Hz, so reacting can lag by up to one stride.
            // That latency is the tiering working, not a bug -- but it is bounded, so pin the bound.
            Run(sim, sim.FarStride, ref t);
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Investigate),
                "a gunshot 15 m away should wake it within one FAR stride");
        }

        [Test]
        public void A_Noise_Too_Quiet_To_Carry_Is_Not_Heard()
        {
            long t = 0;
            var sim = NewSim(out _, out var sight);
            sight.Blocked = true;
            SpawnFacingThePlayer(sim, Vector3.zero);
            Run(sim, 2, ref t);

            Assert.That(sim.Hear(new Vector3(0f, 0f, 20f), 5f), Is.EqualTo(0),
                "a 5 m sound 20 m away must not carry");
        }

        [Test]
        public void A_Noise_Outside_The_Ears_Is_Not_Heard()
        {
            long t = 0;
            var sim = NewSim(out _, out var sight);
            sight.Blocked = true;
            SpawnFacingThePlayer(sim, Vector3.zero);
            Run(sim, 2, ref t);

            // Loud enough to carry 200 m, but the kind's HearingRange is 32 m.
            Assert.That(sim.Hear(new Vector3(0f, 0f, 100f), 200f), Is.EqualTo(0));
        }

        [Test]
        public void The_Loudest_And_Closest_Noise_Wins()
        {
            long t = 0;
            var sim = NewSim(out _, out var sight);
            sight.Blocked = true;
            var id = SpawnFacingThePlayer(sim, Vector3.zero);
            Run(sim, 2, ref t);

            sim.Hear(new Vector3(5f, 0f, 0f), 6f);       // salience 1
            sim.Hear(new Vector3(0f, 0f, 25f), 30f);     // salience 5  <- should win
            sim.Hear(new Vector3(-8f, 0f, 0f), 10f);     // salience 2
            Run(sim, 2, ref t);

            sim.TryGetRow(id, out int row);
            Assert.That(sim.StateOf(row), Is.EqualTo(ZombieState.Investigate));
            Assert.That(sim.DestinationOf(row).z, Is.EqualTo(25f).Within(0.01f));
        }

        [Test]
        public void It_Stays_On_Task_Unless_A_More_Salient_Noise_Arrives()
        {
            long t = 0;
            var sim = NewSim(out _, out var sight);
            sight.Blocked = true;
            var id = SpawnFacingThePlayer(sim, Vector3.zero);
            Run(sim, 2, ref t);

            sim.Hear(new Vector3(0f, 0f, 25f), 30f);     // salience 5, commit to this
            Run(sim, 4, ref t);
            sim.TryGetRow(id, out int row);
            Assert.That(sim.DestinationOf(row).z, Is.EqualTo(25f).Within(0.01f));

            sim.Hear(new Vector3(6f, 0f, 0f), 8f);       // salience 2 -- quieter, must not steal attention
            Run(sim, 4, ref t);
            sim.TryGetRow(id, out row);
            Assert.That(sim.DestinationOf(row).z, Is.EqualTo(25f).Within(0.01f),
                "a quieter noise re-targeted a committed zombie");

            sim.Hear(new Vector3(3f, 0f, 0f), 30f);      // salience 27 -- louder AND closer, this wins
            Run(sim, 4, ref t);
            sim.TryGetRow(id, out row);
            Assert.That(sim.DestinationOf(row).x, Is.EqualTo(3f).Within(0.01f));
        }

        [Test]
        public void Sight_Outranks_Sound()
        {
            long t = 0;
            var sim = NewSim(out var players, out _);
            players[0] = new Vector3(0f, 0f, 20f);
            var id = SpawnFacingThePlayer(sim, Vector3.zero);
            Run(sim, 2, ref t);

            sim.Hear(new Vector3(20f, 0f, 0f), 30f);     // a noise off to the side
            Run(sim, 4, ref t);
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Pursue), "a noise pulled it off a visible player");
        }

        [Test]
        public void An_Unreachable_Noise_Does_Not_Hold_It_Forever()
        {
            long t = 0;
            var sim = NewSim(out var players, out var sight);
            players[0] = new Vector3(0f, 0f, -500f);
            sight.Blocked = true;
            var id = SpawnFacingThePlayer(sim, Vector3.zero);
            Run(sim, 2, ref t);

            sim.Hear(new Vector3(0f, 0f, 20f), 30f);
            Run(sim, 4, ref t);
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Investigate));

            Run(sim, 50 * 20, ref t);   // well past InvestigateTimeout
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Idle));
        }

        // --- attacking ------------------------------------------------------------------------------

        [Test]
        public void In_Reach_It_Attacks_On_A_Cadence_And_The_Blow_Lands_Mid_Swing()
        {
            long t = 0;
            var sim = NewSim(out var players, out _);
            players[0] = new Vector3(0f, 0f, 1f);        // inside the kind's 1.75 m reach
            var id = SpawnFacingThePlayer(sim, Vector3.zero);

            Run(sim, 2, ref t);
            Assert.That(State(sim, id), Is.EqualTo(ZombieState.Attack));

            int swings = 0;
            for (int i = 0; i < 50 * 5; i++)             // 5 s
            {
                sim.SimStep(t++, 0.02);
                swings += sim.Attacks.Length;
            }
            // AttackInterval 1 s over 5 s: about five, and definitely not one per tick.
            Assert.That(swings, Is.InRange(3, 7), $"got {swings} swings in 5 s");
        }

        [Test]
        public void An_Attack_Names_The_Player_It_Hit()
        {
            long t = 0;
            var sim = new ZombieSim(ZombieRegions.UniformGrid(cellsPerAxis: 8, regionSize: 512f, originOffset: 2048f));
            sim.Nav = new FlatGroundNav();
            var players = new[] { new Vector3(0f, 0f, 300f), new Vector3(0f, 0f, 1f) };
            sim.SetPlayers(players, 2);
            sim.Spawn(0, Vector3.zero);

            for (int i = 0; i < 200; i++)
            {
                sim.SimStep(t++, 0.02);
                if (sim.Attacks.Length > 0)
                {
                    Assert.That(sim.Attacks[0].PlayerIndex, Is.EqualTo(1), "it swung at the wrong player");
                    Assert.That(sim.Attacks[0].Damage, Is.GreaterThan(0f));
                    return;
                }
            }
            Assert.Fail("it never swung at the player standing next to it");
        }

        [Test]
        public void Stepping_Out_Of_Reach_Mid_Swing_Means_No_Hit()
        {
            long t = 0;
            var sim = NewSim(out var players, out _);
            players[0] = new Vector3(0f, 0f, 1f);
            sim.Spawn(0, Vector3.zero);
            Run(sim, 2, ref t);

            // Advance to just inside the wind-up, then run away before the blow lands.
            bool sawSwingStart = false;
            for (int i = 0; i < 60 && !sawSwingStart; i++)
            {
                sim.SimStep(t++, 0.02);
                sawSwingStart = true;   // the first Attack-state tick starts a swing
            }
            players[0] = new Vector3(0f, 0f, 40f);

            int hits = 0;
            for (int i = 0; i < 40; i++) { sim.SimStep(t++, 0.02); hits += sim.Attacks.Length; }
            Assert.That(hits, Is.EqualTo(0), "the blow followed the player out of reach");
        }
    }
}
