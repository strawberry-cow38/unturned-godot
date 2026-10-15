using System;
using System.Linq;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    [TestFixture]
    public class SkillTreeTests
    {
        [SetUp] public void Setup() => TransactionalFixtures.RegisterAssets();
        static SkillsReplication.SkillsEntry Own(NetWorldClient client)
        { Assert.That(client.Skills.TryGet(client.PlayerId, out var entry), Is.True); return entry; }

        [Test]
        public void accepted_graph_purchase_is_atomic_owner_only_and_does_not_upgrade_legacy_skills()
        {
            var h = new TransactionalHarness(11001).Connected("a", "b");
            var a = h.Clients[0]; var b = h.Clients[1];
            h.Server.Transactions.AwardXp(a.PlayerId, 100); h.Step(12);
            Assert.That(a.SendLearnSkillNode("heavy_lifter"), Is.True);
            Assert.That(Own(a).Progress.Count, Is.Zero, "sending does not predict a purchase");
            for (int i = 0; i < 30 && !Own(a).Progress.Has("heavy_lifter"); i++)
            {
                h.Step(1);
                Assert.That(Own(a).Skills.experience, Is.EqualTo(Own(a).Progress.Has("heavy_lifter") ? 80u : 100u), "XP and graph must agree in every applied snapshot");
            }
            Assert.That(Own(a).Progress.Has("heavy_lifter"), Is.True);
            a.SendLearnSkillNode("heavy_lifter"); h.Step(20);
            Assert.That(Own(a).Skills.experience, Is.EqualTo(80));
            Assert.That(Own(a).Progress.Count, Is.EqualTo(1));
            Assert.That(Own(a).Skills.skills.SelectMany(row => row).All(s => s.level == 0), Is.True, "placeholder grants have no invented legacy mapping");
            Assert.That(b.Skills.TryGet(a.PlayerId, out _), Is.False);
            Assert.That(Own(b).Progress.Count, Is.Zero);
            Assert.That(a.Skills.StateHash(), Is.EqualTo(h.Server.Skills.StateHashFor(a.PlayerId)));
        }

        [Test]
        public void original_two_parent_and_advanced_pillar_gates_use_the_live_authority_pool()
        {
            var h = new TransactionalHarness(11002).Connected("a"); var a = h.Clients[0];
            a.SendLearnSkillNode("salvage"); h.Step(10);
            Assert.That(Own(a).Progress.Count, Is.Zero, "insufficient XP");
            h.Server.Transactions.AwardXp(a.PlayerId, 500); h.Step(10);
            foreach (string id in new[] { "unknown_node", "wiring", "improvised_armour", "field_surgery" }) a.SendLearnSkillNode(id);
            h.Step(12); Assert.That(Own(a).Progress.Count, Is.Zero);
            foreach (string id in new[] { "salvage", "sharpening" }) { a.SendLearnSkillNode(id); h.Step(10); }
            a.SendLearnSkillNode("improvised_armour"); h.Step(10);
            Assert.That(Own(a).Progress.Has("improvised_armour"), Is.False, "second cross-column prerequisite still missing");
            a.SendLearnSkillNode("heavy_lifter"); h.Step(10);
            a.SendLearnSkillNode("improvised_armour"); h.Step(10);
            a.SendLearnSkillNode("wiring"); h.Step(10);
            Assert.That(Own(a).Progress.Has("improvised_armour"), Is.True);
            Assert.That(Own(a).Progress.Has("wiring"), Is.True, "salvage opened Engineering");
            Assert.That(Own(a).Skills.experience, Is.EqualTo(280));
        }

        [Test]
        public void command_bounds_and_truncation_fail_closed()
        {
            foreach (string id in new[] { "", "UPPER", "bad id", "a\n", "é", new string('a', 64) })
            {
                Assert.That(LearnSkillNodeCommand.ValidId(id), Is.False);
                var bytes = NetMessagePak.Pack(77, w => w.WriteString(id, 6));
                var reader = new NetPakReader(); reader.SetBufferSegment(bytes, bytes.Length); reader.ReadUInt8(out _);
                Assert.That(LearnSkillNodeCommand.TryRead(reader, out _), Is.False);
            }
            var valid = NetMessagePak.Pack(77, new LearnSkillNodeCommand { NodeId = "field_surgery" }.Write);
            var cut = new NetPakReader(); cut.SetBufferSegment(valid, valid.Length - 2); cut.ReadUInt8(out _);
            Assert.That(LearnSkillNodeCommand.TryRead(cut, out _), Is.False);
        }

        [Test]
        public void malformed_owner_graph_does_not_partially_adopt_xp_or_levels()
        {
            var copy = new SkillsReplication(); var own = copy.ServerAdd(1, 0); own.Skills.NetSetExperience(80);
            copy.ServerRestoreLearned(1, new[] { "heavy_lifter" }, 0);
            ulong before = copy.StateHash();
            foreach (int mode in new[] { 0, 1, 2, 3 })
            {
                var bytes = NetMessagePak.Pack(0, w =>
                {
                    w.WriteUInt8(1); w.WriteUInt16(1); w.WriteUInt32(999);
                    foreach (var row in new PlayerSkills().skills) foreach (var skill in row) w.WriteUInt8(1);
                    w.WriteUInt8((byte)(mode == 0 ? 65 : mode == 1 ? 1 : 2));
                    if (mode == 2) { w.WriteString("salvage", 6); w.WriteString("heavy_lifter", 6); }
                    if (mode == 3) { w.WriteString("salvage", 6); w.WriteString("salvage", 6); }
                });
                var reader = new NetPakReader(); reader.SetBufferSegment(bytes, bytes.Length); reader.ReadUInt8(out _);
                copy.ReadSnapshot(reader, true);
                Assert.That(copy.StateHash(), Is.EqualTo(before), $"malformed owner tail {mode} applied nothing");
            }
        }

        [Test]
        public void protocol69_is_refused_before_the_wider_owner_skill_block_can_be_read()
        {
            var h = new NetSimHarness(11005);
            var old = h.AddClient("old skills", version: 69);
            old.Connect();
            Assert.That(h.StepUntil(() => old.State == NetSessionState.Disconnected, 100), Is.True);
            Assert.That(old.RejectReason, Is.EqualTo(NetRejectReason.VersionMismatch));
        }

        [Test]
        public void save_rejoin_preserves_graph_and_xp_while_old_saves_and_unknown_ids_are_safe()
        {
            var h = new TransactionalHarness(11003).Connected("keeper"); var a = h.Clients[0];
            h.Server.Transactions.AwardXp(a.PlayerId, 100); h.Step(10);
            a.SendLearnSkillNode("salvage"); h.Step(12);
            string json = WorldSave.Capture(h.Server, "", 0, 0.5f, 1200).ToJson();
            Assert.That(WorldSave.TryParse(json, "", out var save, out var error), Is.True, error);
            save.Players[0].LearnedSkillNodes.Add("removed_source_node");
            var h2 = new TransactionalHarness(11004); h2.Server.PendingSave = save; h2.Connected("keeper");
            var back = h2.Clients[0]; h2.Step(12);
            Assert.That(Own(back).Progress.Taken, Is.EquivalentTo(new[] { "salvage" }));
            Assert.That(Own(back).Skills.experience, Is.EqualTo(80), "restore does not purchase again");
            Assert.That(WorldSave.TryParse(json.Replace("\"LearnedSkillNodes\"", "\"oldMissingField\""), "", out var old, out error), Is.True, error);
            Assert.That(old.Players[0].LearnedSkillNodes, Is.Empty);
        }
    }
}
