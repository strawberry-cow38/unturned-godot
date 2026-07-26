using System;
using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 tests for zombie perception and combat (rewrite plan phase 2).
    //
    // The headline claim under test: a zombie is shootable WITHOUT a collider, at any range, in any
    // tier. In the old system that was a physics raycast, so it could only be tested by booting Godot
    // and only worked where a body existed. Here it is maths against the spatial grid, so it is tested
    // here, in milliseconds, including the cases that were previously impossible to stage.
    [TestFixture]
    public class ZombieCombatTests
    {
        static ZombieSim NewSim(out Vector3[] players, IZombieNavQuery nav = null)
        {
            var sim = new ZombieSim(ZombieRegions.UniformGrid(cellsPerAxis: 8, regionSize: 512f, originOffset: 2048f));
            sim.Nav = nav ?? new FlatGroundNav();
            players = new[] { Vector3.zero };
            sim.SetPlayers(players, 1);
            return sim;
        }

        static void Run(ZombieSim sim, int ticks, long from = 0)
        {
            for (long t = from; t < from + ticks; t++) sim.SimStep(t, 0.02);
        }

        // --- the capsule maths --------------------------------------------------------------------

        [Test]
        public void A_Ray_Down_The_Middle_Hits_At_The_Near_Face()
        {
            // Zombie standing at x=10, radius 0.4. A ray along +x at chest height should enter at 9.6.
            float t = ZombieCombat.RayCapsule(new Vector3(0f, 1f, 0f), new Vector3(1f, 0f, 0f),
                                              new Vector3(10f, 0f, 0f), 0.4f, 1.9f);
            Assert.That(t, Is.EqualTo(9.6f).Within(0.01f));
        }

        [Test]
        public void A_Ray_Past_The_Shoulder_Misses()
        {
            float t = ZombieCombat.RayCapsule(new Vector3(0f, 1f, 0.5f), new Vector3(1f, 0f, 0f),
                                              new Vector3(10f, 0f, 0f), 0.4f, 1.9f);
            Assert.That(t, Is.LessThan(0f), "0.5 m off-axis is outside a 0.4 m capsule");
        }

        [Test]
        public void A_Ray_Over_The_Head_Misses()
        {
            float t = ZombieCombat.RayCapsule(new Vector3(0f, 2.5f, 0f), new Vector3(1f, 0f, 0f),
                                              new Vector3(10f, 0f, 0f), 0.4f, 1.9f);
            Assert.That(t, Is.LessThan(0f));
        }

        [Test]
        public void A_Ray_Under_The_Feet_Misses()
        {
            float t = ZombieCombat.RayCapsule(new Vector3(0f, -0.5f, 0f), new Vector3(1f, 0f, 0f),
                                              new Vector3(10f, 0f, 0f), 0.4f, 1.9f);
            Assert.That(t, Is.LessThan(0f));
        }

        [Test]
        public void The_Rounded_Cap_Is_Hit_Not_A_Flat_Top()
        {
            // Straight down onto the crown: entry is the top of the sphere cap, i.e. the full height.
            float t = ZombieCombat.RayCapsule(new Vector3(10f, 5f, 0f), new Vector3(0f, -1f, 0f),
                                              new Vector3(10f, 0f, 0f), 0.4f, 1.9f);
            Assert.That(t, Is.EqualTo(5f - 1.9f).Within(0.02f));
        }

        [Test]
        public void Shots_Behind_The_Shooter_Are_Not_Hits()
        {
            float t = ZombieCombat.RayCapsule(new Vector3(0f, 1f, 0f), new Vector3(-1f, 0f, 0f),
                                              new Vector3(10f, 0f, 0f), 0.4f, 1.9f);
            Assert.That(t, Is.LessThan(0f), "the capsule is behind the ray, not in front of it");
        }

        [Test]
        public void Limbs_Come_From_Hit_Height()
        {
            var foot = new Vector3(0f, 0f, 0f);
            Assert.That(ZombieCombat.LimbAt(new Vector3(0f, 1.8f, 0f), foot, 1.9f), Is.EqualTo(ZombieLimb.Skull));
            Assert.That(ZombieCombat.LimbAt(new Vector3(0f, 1.2f, 0f), foot, 1.9f), Is.EqualTo(ZombieLimb.Spine));
            Assert.That(ZombieCombat.LimbAt(new Vector3(0f, 0.3f, 0f), foot, 1.9f), Is.EqualTo(ZombieLimb.Leg));
        }

        // --- shooting through the sim ---------------------------------------------------------------

        [Test]
        public void You_Can_Shoot_A_Zombie_That_Has_No_Body_And_No_Rig()
        {
            var sim = NewSim(out _);
            var id = sim.Spawn(0, new Vector3(0f, 0f, 25f));
            Run(sim, 2);

            Assert.That(sim.Raycast(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), 100f, out var hit), Is.True);
            Assert.That(hit.Id, Is.EqualTo(id));
            Assert.That(hit.Distance, Is.EqualTo(24.6f).Within(0.05f));
        }

        [Test]
        public void Hit_Detection_Works_Identically_At_Five_Metres_And_Five_Hundred()
        {
            // Impossible to arrange in the old system: at 500 m there was no collider to hit.
            foreach (float range in new[] { 5f, 50f, 200f, 500f })
            {
                var sim = NewSim(out _);
                var id = sim.Spawn(0, new Vector3(0f, 0f, range));
                Run(sim, 2);

                Assert.That(sim.Raycast(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), 1000f, out var hit), Is.True,
                    $"missed a zombie at {range} m");
                Assert.That(hit.Id, Is.EqualTo(id));
                Assert.That(hit.Distance, Is.EqualTo(range - 0.4f).Within(0.05f));
            }
        }

        [Test]
        public void An_Ambient_Zombie_Is_Just_As_Shootable_As_A_Close_One()
        {
            // The old invariant "renderable implies shootable" existed because dropping a tier could drop
            // your collider. Here tier and shootability are unrelated, and this is what says so.
            var sim = NewSim(out _);
            var id = sim.Spawn(0, new Vector3(1500f, 0f, 1500f));   // nowhere near the player -> AMBIENT
            Run(sim, 5);
            Assert.That(sim.TryGetRow(id, out int row), Is.True);
            Assert.That(sim.TierOf(row), Is.EqualTo(ZombieTier.Ambient), "test setup");

            var from = new Vector3(1500f, 1f, 1400f);
            Assert.That(sim.Raycast(from, new Vector3(0f, 0f, 1f), 200f, out var hit), Is.True,
                "an AMBIENT zombie was bulletproof");
            Assert.That(hit.Id, Is.EqualTo(id));
        }

        [Test]
        public void The_Nearest_Zombie_On_The_Line_Is_The_One_You_Hit()
        {
            var sim = NewSim(out _);
            var near = sim.Spawn(0, new Vector3(0f, 0f, 10f));
            sim.Spawn(0, new Vector3(0f, 0f, 20f));
            sim.Spawn(0, new Vector3(0f, 0f, 30f));
            Run(sim, 2);

            Assert.That(sim.Raycast(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), 100f, out var hit), Is.True);
            Assert.That(hit.Id, Is.EqualTo(near), "the bullet passed through the front one");
        }

        [Test]
        public void A_Shot_That_Falls_Short_Does_Not_Reach()
        {
            var sim = NewSim(out _);
            sim.Spawn(0, new Vector3(0f, 0f, 50f));
            Run(sim, 2);
            Assert.That(sim.Raycast(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), 25f, out _), Is.False);
        }

        [Test]
        public void A_Headshot_Reports_The_Skull()
        {
            var sim = NewSim(out _);
            sim.Spawn(0, new Vector3(0f, 0f, 20f));
            Run(sim, 2);
            Assert.That(sim.Raycast(new Vector3(0f, 1.75f, 0f), new Vector3(0f, 0f, 1f), 100f, out var hit), Is.True);
            Assert.That(hit.Limb, Is.EqualTo(ZombieLimb.Skull));
        }

        [Test]
        public void Corpses_Do_Not_Stop_Bullets()
        {
            var sim = NewSim(out _);
            var front = sim.Spawn(0, new Vector3(0f, 0f, 10f));
            var behind = sim.Spawn(0, new Vector3(0f, 0f, 20f));
            Run(sim, 2);
            sim.Damage(front, 1000f);

            Assert.That(sim.Raycast(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), 100f, out var hit), Is.True);
            Assert.That(hit.Id, Is.EqualTo(behind), "the corpse ate the bullet");
        }

        // --- damage and death -------------------------------------------------------------------------

        [Test]
        public void Damage_Kills_And_Reports_It_Once()
        {
            var sim = NewSim(out _);
            var id = sim.Spawn(0, new Vector3(0f, 0f, 10f));
            Run(sim, 1);

            Assert.That(sim.Damage(id, 40f), Is.False, "not dead yet");
            Assert.That(sim.Damage(id, 40f), Is.False);
            Assert.That(sim.Damage(id, 40f), Is.True, "100 health, 120 dealt");
            Assert.That(sim.Damage(id, 40f), Is.False, "already dead -- must not die twice");

            sim.TryGetRow(id, out int row);
            Assert.That(sim.StateOf(row), Is.EqualTo(ZombieState.Dead));
        }

        [Test]
        public void A_Death_Is_Reported_For_The_Step_It_Happened_In()
        {
            var sim = NewSim(out _);
            var id = sim.Spawn(0, new Vector3(0f, 0f, 10f));
            Run(sim, 1);
            sim.Damage(id, 500f, ZombieLimb.Skull);

            Assert.That(sim.Deaths.Length, Is.EqualTo(1));
            Assert.That(sim.Deaths[0].Id, Is.EqualTo(id));
            Assert.That(sim.Deaths[0].KillingLimb, Is.EqualTo(ZombieLimb.Skull));

            Run(sim, 1, from: 1);
            Assert.That(sim.Deaths.Length, Is.EqualTo(0), "the event must not repeat every step");
        }

        [Test]
        public void A_Corpse_Holds_Its_Row_And_Then_Gives_It_Back()
        {
            var sim = NewSim(out _);
            sim.CorpseSeconds = 2f;
            var id = sim.Spawn(0, new Vector3(0f, 0f, 10f));
            Run(sim, 1);
            sim.Damage(id, 500f);

            Run(sim, 50, from: 1);                       // 1 s
            Assert.That(sim.IsAlive(id), Is.True, "recycled before the presentation layer could ragdoll it");

            Run(sim, 100, from: 51);                     // past CorpseSeconds
            Assert.That(sim.IsAlive(id), Is.False, "the corpse never gave its row back");
            Assert.That(sim.Count, Is.EqualTo(0));
        }

        [Test]
        public void A_Dead_Zombie_Stops_Moving_And_Stops_Swinging()
        {
            var sim = NewSim(out var players);
            sim.CorpseSeconds = 1000f;
            var id = sim.Spawn(0, new Vector3(0f, 0f, 6f));
            Run(sim, 60);
            sim.TryGetRow(id, out int row);
            var where = sim.PositionOf(row);

            sim.Damage(id, 500f);
            Run(sim, 200, from: 60);

            sim.TryGetRow(id, out row);
            Assert.That(sim.PositionOf(row), Is.EqualTo(where), "a corpse walked");
            Assert.That(sim.Attacks.Length, Is.EqualTo(0), "a corpse swung");
        }
    }
}
