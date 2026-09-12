using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // v49 global chat and moderation, over the REAL transport. ChatRulesTests and ServerModerationTests
    // cover the rules in isolation; these exist because a rule that works and a wire that carries it are
    // different claims, and the second one is where an unbumped id or a missing registration hides.
    [TestFixture]
    public class ChatAndModerationTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        static List<ChatMessageEvent> Capture(NetWorldClient c)
        {
            var seen = new List<ChatMessageEvent>();
            c.ChatMessage += e => seen.Add(e);
            return seen;
        }

        [Test]
        public void A_Players_Line_Reaches_Every_Client()
        {
            var h = new TransactionalHarness(1);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Bob");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);
            var atSeen = Capture(a); var btSeen = Capture(b);

            Assert.That(a.SendChat("anyone at the airport"), Is.True);
            h.Step(20);

            Assert.That(btSeen.Count, Is.EqualTo(1), "the other player must hear it");
            Assert.That(btSeen[0].Text, Is.EqualTo("anyone at the airport"));
            Assert.That(btSeen[0].Channel, Is.EqualTo((byte)ChatChannel.Global));
            Assert.That(btSeen[0].Name, Is.EqualTo("Alice"), "attributed to the speaker");
            Assert.That(btSeen[0].SpeakerId, Is.EqualTo(a.PlayerId));
            Assert.That(atSeen.Count, Is.EqualTo(1), "and the speaker sees their own line");
        }

        [Test]
        public void The_Server_Can_Speak_And_Is_Marked_As_The_Server()
        {
            var h = new TransactionalHarness(2);
            var a = h.AddClient("Alice");
            h.StepUntil(() => a.State == NetSessionState.Connected);
            var seen = Capture(a);

            h.Server.Transactions.SayAsServer("restarting in 5 minutes");
            h.Step(20);

            Assert.That(seen.Count, Is.EqualTo(1));
            Assert.That(seen[0].Text, Is.EqualTo("restarting in 5 minutes"));
            Assert.That(seen[0].Channel, Is.EqualTo((byte)ChatChannel.Server));
            Assert.That(seen[0].SpeakerId, Is.EqualTo(0), "a server line has no speaker");
        }

        // THE IMPERSONATION TEST. A player named "SERVER" saying something must still arrive on the Global
        // channel, or the UI cannot tell them apart from the real thing.
        [Test]
        public void A_Player_Cannot_Send_On_The_Server_Channel()
        {
            var h = new TransactionalHarness(3);
            var a = h.AddClient("SERVER");
            var b = h.AddClient("Bob");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);
            var seen = Capture(b);

            a.SendChat("everyone has been given admin");
            h.Step(20);

            Assert.That(seen.Count, Is.EqualTo(1));
            Assert.That(seen[0].Channel, Is.EqualTo((byte)ChatChannel.Global), "a player line is ALWAYS Global");
            Assert.That(seen[0].SpeakerId, Is.EqualTo(a.PlayerId), "and carries their real id");
        }

        // The server re-sanitises rather than trusting the client's pass. Driven through the command
        // handler directly, because SendChat sanitises client-side and could not deliver the raw text.
        [Test]
        public void The_Server_Re_Sanitises_Text_A_Client_Did_Not_Clean()
        {
            var h = new TransactionalHarness(4);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Bob");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);
            var seen = Capture(b);

            a.SendCommandRaw(ReplicationIds.CommandChatSend,
                             new ChatSendCommand { Text = "[img]https://attacker.example/x.png[/img]" }.Write);
            h.Step(20);

            Assert.That(seen.Count, Is.EqualTo(1));
            Assert.That(seen[0].Text.IndexOf('['), Is.EqualTo(-1), "a modified client cannot smuggle a tag past the server");
            Assert.That(seen[0].Text, Is.EqualTo("(img)https://attacker.example/x.png(/img)"));
        }

        [Test]
        public void A_Rate_Limited_Message_Is_Refused_And_Only_The_Sender_Is_Told()
        {
            var h = new TransactionalHarness(5);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Bob");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);
            var aSeen = Capture(a); var bSeen = Capture(b);

            for (int i = 0; i < 12; i++) { a.SendChat($"spam {i}"); h.Step(); }
            h.Step(20);

            int globalToB = 0;
            foreach (var e in bSeen) if (e.Channel == (byte)ChatChannel.Global) globalToB++;
            Assert.That(globalToB, Is.LessThan(12), "the flood must not all get through");
            Assert.That(globalToB, Is.GreaterThan(0), "but the first ones do");

            int noticesToA = 0, noticesToB = 0;
            foreach (var e in aSeen) if (e.Channel == (byte)ChatChannel.Server) noticesToA++;
            foreach (var e in bSeen) if (e.Channel == (byte)ChatChannel.Server) noticesToB++;
            Assert.That(noticesToA, Is.GreaterThan(0), "the sender is told why");
            Assert.That(noticesToB, Is.EqualTo(0), "and nobody else is -- the notice is not itself spam");
        }

        [Test]
        public void An_Empty_Message_Never_Reaches_The_Wire()
        {
            var h = new TransactionalHarness(6);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Bob");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);
            var seen = Capture(b);

            Assert.That(a.SendChat("     "), Is.False, "whitespace-only is refused client-side");
            a.SendCommandRaw(ReplicationIds.CommandChatSend, new ChatSendCommand { Text = "​​​" }.Write);
            h.Step(20);
            Assert.That(seen.Count, Is.EqualTo(0), "and a client that sends it anyway is dropped server-side");
        }

        // ---- moderation ------------------------------------------------------------------------------

        [Test]
        public void Kick_Disconnects_The_Named_Player_And_Tells_Everyone()
        {
            var h = new TransactionalHarness(7);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Griefer");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);
            var seen = Capture(a);

            string reply = h.Server.Transactions.RunConsole(a.PlayerId, "kick Griefer 10m building spam");
            h.Step(30);

            Assert.That(reply, Does.Contain("Griefer"));
            Assert.That(reply, Does.Contain("10m"));
            Assert.That(reply, Does.Contain("building spam"));
            Assert.That(b.State, Is.Not.EqualTo(NetSessionState.Connected), "they are actually gone");

            bool announced = false;
            foreach (var e in seen) if (e.Channel == (byte)ChatChannel.Server && e.Text.Contains("Griefer")) announced = true;
            Assert.That(announced, Is.True, "a disappearance must never be a mystery");
        }

        [Test]
        public void An_Unknown_Or_Ambiguous_Name_Kicks_Nobody()
        {
            var h = new TransactionalHarness(8);
            var a = h.AddClient("Alice");
            h.StepUntil(() => a.State == NetSessionState.Connected);

            Assert.That(h.Server.Transactions.RunConsole(a.PlayerId, "kick Nobody"), Does.Contain("no connected player"));
            Assert.That(a.State, Is.EqualTo(NetSessionState.Connected), "and the admin is still connected");
        }

        [Test]
        public void A_Bare_Kick_Takes_No_Duration_And_Records_No_Ban()
        {
            var h = new TransactionalHarness(9);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Griefer");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);

            h.Server.Transactions.RunConsole(a.PlayerId, "kick Griefer");
            h.Step(30);
            Assert.That(b.State, Is.Not.EqualTo(NetSessionState.Connected));
            Assert.That(h.Server.Transactions.Moderation.Count, Is.EqualTo(0),
                        "a plain kick is a boot, not a ban -- they may come straight back");
        }

        [Test]
        public void An_Unparseable_Duration_Is_Read_As_The_Start_Of_The_Reason()
        {
            // "kick griefer stop building there" must not be a usage error.
            var h = new TransactionalHarness(10);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Griefer");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);

            string reply = h.Server.Transactions.RunConsole(a.PlayerId, "kick Griefer stop building there");
            Assert.That(reply, Does.Contain("stop building there"));
            Assert.That(h.Server.Transactions.Moderation.Count, Is.EqualTo(0), "still a plain kick");
        }

        [Test]
        public void Ban_Defaults_To_Permanent_And_Is_Refused_At_The_Handshake()
        {
            var h = new TransactionalHarness(11);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Cheater");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);

            string reply = h.Server.Transactions.RunConsole(a.PlayerId, "ban Cheater aimbot");
            h.Step(30);
            Assert.That(reply, Does.Contain("permanently"));
            Assert.That(h.Server.Transactions.Moderation.Count, Is.EqualTo(1));
            Assert.That(h.Server.Transactions.Moderation.Entries[0].IsPermanent, Is.True);

            // THE ONE THAT MATTERS: coming back must be refused at the door, not admitted and dropped.
            var again = h.AddClient("Cheater");
            h.Step(60);
            Assert.That(again.State, Is.Not.EqualTo(NetSessionState.Connected), "the ban survives the disconnect");
        }

        [Test]
        public void Unban_Lets_Them_Back_In()
        {
            var h = new TransactionalHarness(12);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Cheater");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);

            h.Server.Transactions.RunConsole(a.PlayerId, "ban Cheater");
            h.Step(30);
            Assert.That(h.Server.Transactions.RunConsole(a.PlayerId, "unban Cheater"), Does.Contain("lifted"));

            var again = h.AddClient("Cheater");
            h.StepUntil(() => again.State == NetSessionState.Connected, maxTicks: 200);
            Assert.That(again.State, Is.EqualTo(NetSessionState.Connected));
        }

        [Test]
        public void The_Ban_Is_Recorded_Before_The_Disconnect_So_The_Address_Survives()
        {
            // Order bug this pins: disconnect first and IdentityOf can no longer answer, so the entry is
            // stored with no address and only the name to match on.
            var h = new TransactionalHarness(13);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Cheater");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);

            h.Server.Transactions.RunConsole(a.PlayerId, "ban Cheater 2h");
            var e = h.Server.Transactions.Moderation.Entries[0];
            Assert.That(e.Name, Is.EqualTo("Cheater"), "the name was captured while they were still connected");
            Assert.That(e.IsPermanent, Is.False);
        }

        // ---- the two the fable review found, pinned before fixing ------------------------------------

        // Every moderation test above calls RunConsole DIRECTLY, so none of them go through the command
        // dispatch loop -- and that loop is `foreach (var peer in Session.Peers)` over the very List that
        // RemovePeer mutates. A kick sent the way a real admin sends it therefore throws
        // InvalidOperationException out of TickSimulation, skipping every remaining sim step that tick.
        [Test]
        public void A_Kick_Sent_Over_The_WIRE_Does_Not_Break_The_Tick()
        {
            var h = new TransactionalHarness(20);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Griefer");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);

            a.SendConsole("kick Griefer");
            Assert.DoesNotThrow(() => h.Step(30), "removing a peer mid-dispatch must not break the loop");
            Assert.That(b.State, Is.Not.EqualTo(NetSessionState.Connected), "and the kick still lands");

            // The tick must still be doing its job afterwards, not silently dead from the throw.
            var c = h.AddClient("Carol");
            h.StepUntil(() => c.State == NetSessionState.Connected, maxTicks: 200);
            Assert.That(c.State, Is.EqualTo(NetSessionState.Connected), "the server still accepts joins");
        }

        // TWO CLOCKS. NowSeconds is uptime (tick/50) and the handshake gate is Unix time, so a timed ban is
        // stored at ~4200 and judged against ~1.79e9 -- expired on arrival, deleted, and the player walks
        // in. Only permanent bans worked. The existing rejoin test used a PERMANENT ban and so was blind.
        [Test]
        public void A_TIMED_Ban_Is_Still_Enforced_On_Reconnect()
        {
            var h = new TransactionalHarness(21);
            var a = h.AddClient("Alice");
            var b = h.AddClient("Cheater");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);

            h.Server.Transactions.RunConsole(a.PlayerId, "ban Cheater 2h aimbot");
            h.Step(30);
            Assert.That(h.Server.Transactions.Moderation.Count, Is.EqualTo(1));
            Assert.That(h.Server.Transactions.Moderation.Entries[0].IsPermanent, Is.False, "a timed ban");

            var again = h.AddClient("Cheater");
            h.Step(80);
            Assert.That(again.State, Is.Not.EqualTo(NetSessionState.Connected),
                        "a 2h ban must still be in force one tick later");
            Assert.That(h.Server.Transactions.Moderation.Count, Is.EqualTo(1),
                        "and the entry must not have been pruned as already-expired");
        }

        [Test]
        public void Say_Posts_As_The_Server()
        {
            var h = new TransactionalHarness(14);
            var a = h.AddClient("Alice");
            h.StepUntil(() => a.State == NetSessionState.Connected);
            var seen = Capture(a);

            h.Server.Transactions.RunConsole(a.PlayerId, "say wipe at midnight");
            h.Step(20);

            Assert.That(seen.Count, Is.EqualTo(1));
            Assert.That(seen[0].Channel, Is.EqualTo((byte)ChatChannel.Server));
            Assert.That(seen[0].Text, Is.EqualTo("wipe at midnight"));
        }
    }
}
