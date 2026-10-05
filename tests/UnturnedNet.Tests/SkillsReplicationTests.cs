using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // MP_PLAN §3.2 -- skills, the owner-only pilot: the first consumer of the §2.6 interest hook. XP and
    // levels replicate to their owner and ONLY their owner; UpgradeSkill validates server-side through the
    // same PlayerSkills.TryUpgrade math single-player runs; XpAwarded is the owner's HUD fact.
    [TestFixture]
    public class SkillsReplicationTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        [Test]
        public void xp_award_replicates_to_owner_only()
        {
            var h = new TransactionalHarness(9061).Connected("a", "b");
            var a = h.Clients[0];
            var b = h.Clients[1];
            uint aXpEvents = 0;
            a.XpAwarded += e => aXpEvents += e.Amount;
            uint bXpEvents = 0;
            b.XpAwarded += e => bXpEvents += e.Amount;

            h.Server.Transactions.AwardXp(a.PlayerId, 30);
            Assert.That(h.StepUntil(() => a.Skills.TryGet(a.PlayerId, out var mine) && mine.Skills.experience == 30),
                        Is.True, $"owner replica received the award (seed={h.Net.Seed})");

            Assert.That(aXpEvents, Is.EqualTo(30), "XpAwarded event reached the owner");
            Assert.That(bXpEvents, Is.EqualTo(0), "XpAwarded never went to another player");
            // owner-only proof: B replicates its OWN entry, never A's (§2.6 interest hook)
            Assert.That(b.Skills.TryGet(a.PlayerId, out _), Is.False, "A's skills never entered B's replica");
            Assert.That(b.Skills.TryGet(b.PlayerId, out var bOwn) && bOwn.Skills.experience == 0, Is.True,
                        "B still sees its own (empty) skills");
            Assert.That(a.Skills.StateHash(), Is.EqualTo(h.Server.Skills.StateHashFor(a.PlayerId)), "owner parity");
        }

        [Test]
        public void upgrade_spends_xp_via_server_validation()
        {
            var h = new TransactionalHarness(9062).Connected("a");
            var a = h.Clients[0];
            h.Server.Transactions.AwardXp(a.PlayerId, 30);
            h.Step(10);

            // OFFENSE/OVERKILL costs 10 at level 0 -> level 1, 20 XP left
            a.SendUpgradeSkill((byte)EPlayerSpeciality.OFFENSE, (byte)EPlayerOffense.OVERKILL);
            Assert.That(h.StepUntil(() => a.Skills.TryGet(a.PlayerId, out var mine) && mine.Skills.Level(EPlayerOffense.OVERKILL) == 1),
                        Is.True, $"upgrade applied + replicated (seed={h.Net.Seed})");
            a.Skills.TryGet(a.PlayerId, out var replica);
            Assert.That(replica.Skills.experience, Is.EqualTo(20), "cost deducted server-side");
            Assert.That(a.Skills.StateHash(), Is.EqualTo(h.Server.Skills.StateHashFor(a.PlayerId)), "owner parity");
        }

        [Test]
        public void upgrade_without_xp_changes_nothing()
        {
            var h = new TransactionalHarness(9063).Connected("a");
            var a = h.Clients[0];
            h.Step(5);
            ulong before = h.Server.Skills.StateHashFor(a.PlayerId);

            a.SendUpgradeSkill((byte)EPlayerSpeciality.OFFENSE, (byte)EPlayerOffense.OVERKILL);
            h.Step(20);

            Assert.That(h.Server.Skills.StateHashFor(a.PlayerId), Is.EqualTo(before), "TryUpgrade refused: no XP");
            h.Server.Skills.TryGet(a.PlayerId, out var server);
            Assert.That(server.Skills.Level(EPlayerOffense.OVERKILL), Is.EqualTo((byte)0));
        }

        [Test]
        public void out_of_range_speciality_rejected_at_choke_point()
        {
            var h = new TransactionalHarness(9064).Connected("a");
            var a = h.Clients[0];
            h.Step(5);
            long rejectedBefore = h.Server.Commands.Diag.ValidationRejected;

            a.SendUpgradeSkill(7, 0);   // speciality 7 does not exist
            h.Step(20);

            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejectedBefore),
                        "the validator refused the speciality before any handler ran");
        }

        [Test]
        public void kill_credit_feeds_the_xp_hook()
        {
            var h = new TransactionalHarness(9065);
            h.Server.KillExperience = 3;   // the §3.2 kill-award hook; default is 0 until SP awards kill XP
            h.PumpCombatState = true;      // v10: SendFire now rides the PlayerStateCommand stream -- flush it each tick
            h.Connected("a");
            var a = h.Clients[0];
            uint awarded = 0;
            a.XpAwarded += e => awarded += e.Amount;

            // a zombie the server combat step can kill: one bullet of 40 dmg on a 35 hp target
            var host = new KillableZombies();
            host.Hp[RegisterZombie(h)] = 35f;
            h.Server.Combat.ZombieHost = host;
            h.Step(5);
            h.Server.Players.TryGetByOwner(a.PlayerId, out var pa);
            a.SendFire(pa.Pos + new UnityEngine.Vector3(0f, 1.5f, 0f), new UnityEngine.Vector3(0f, 0f, 1f));

            Assert.That(h.StepUntil(() => awarded == 3), Is.True,
                        $"kill credit awarded KillExperience through ServerCombat.KillCredited (seed={h.Net.Seed})");
            h.Server.Skills.TryGet(a.PlayerId, out var server);
            Assert.That(server.Skills.experience, Is.EqualTo(3u));
        }

        sealed class KillableZombies : IZombieHost
        {
            public readonly System.Collections.Generic.Dictionary<uint, float> Hp = new System.Collections.Generic.Dictionary<uint, float>();

            public bool DamageZombie(uint zombieNetId, float damage, UnityEngine.Vector3 point, UnityEngine.Vector3 dir, ushort attackerPlayerId, bool headshot)
            {
                if (!Hp.TryGetValue(zombieNetId, out float hp) || hp <= 0f) return false;
                hp -= damage;
                Hp[zombieNetId] = hp;
                return hp <= 0f;
            }
        }

        static uint RegisterZombie(TransactionalHarness h)
        {
            // straight down the +Z aim line from player 1's spawn (0,0,0), inside gun range
            var id = h.Server.Ids.Mint();
            h.Server.Zombies.ServerSpawn(id, 0, new UnityEngine.Vector3(0f, 0f, 6f), h.Server.Session.CurrentTick);
            return id.Value;
        }
    }
}
