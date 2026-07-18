using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // #27 (branch mp-teleport) -- the F1 `teleport` verb goes server-authoritative: the client resolves its
    // game-side location table to coordinates (DevConsole.TryResolveTeleport) and sends
    // `teleport <x> <y> <z>` over the EXISTING console command (no protocol change); RunConsole validates +
    // applies it via ServerTeleport. The pre-fix MP path was a client-LOCAL TeleportTo -- the server entity
    // never moved and the reconciler snapped the player straight back. The L1 net.shell_console_teleport
    // proves the round trip on real bodies; this battery pins the choke-point parsing/gating.
    [TestFixture]
    public class ConsoleTeleportTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        [Test]
        public void numeric_teleport_moves_the_authoritative_entity_and_replicates()
        {
            var h = new TransactionalHarness(9270).Connected("a");
            var a = h.Clients[0];
            string verdict = null;
            a.ConsoleResult += e => verdict = e.Text;

            var target = PlayerReplication.Quantize(new Vector3(40.5f, 12f, -25.25f));
            a.SendConsole("teleport 40.5 12 -25.25");
            Assert.That(h.StepUntil(() => h.Server.Players.TryGetByOwner(a.PlayerId, out var e) && e.Pos == target), Is.True,
                        $"ServerTeleport moved the authoritative entity (seed={h.Net.Seed})");
            Assert.That(h.Server.Transactions.Diag.ConsoleApplied, Is.EqualTo(1), "counted as applied");
            Assert.That(h.StepUntil(() => a.Players.TryGetByOwner(a.PlayerId, out var e) && e.Pos == target), Is.True,
                        "the moved entity replicated back to the owner's replica -- the spot the shell reconciles onto");
            Assert.That(h.StepUntil(() => verdict != null), Is.True, "the verdict event rode back to the sender");
            Assert.That(verdict, Does.Contain("teleported to"), "the verdict names the applied coords");
        }

        [Test]
        public void malformed_teleport_rejected_entity_unmoved()
        {
            var h = new TransactionalHarness(9271).Connected("a");
            var a = h.Clients[0];
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var before), Is.True, "player spawned");
            var spawn = before.Pos;
            string verdict = null;
            a.ConsoleResult += e => verdict = e.Text;

            // a raw location NAME (what a stale client would send), too few args, and the NaN/Infinity
            // poison forms -- all die at the choke point with usage, never touching the entity
            foreach (string bad in new[] { "teleport Stratford", "tp 1 2", "teleport NaN 0 0", "teleport Infinity 0 0" })
            {
                verdict = null;
                a.SendConsole(bad);
                Assert.That(h.StepUntil(() => verdict != null), Is.True, $"verdict for '{bad}' (seed={h.Net.Seed})");
                Assert.That(verdict, Does.Contain("usage: teleport"), $"'{bad}' rejected with usage");
            }
            Assert.That(h.Server.Transactions.Diag.ConsoleApplied, Is.EqualTo(0), "nothing applied");
            Assert.That(h.Server.Transactions.Diag.ConsoleRejected, Is.EqualTo(4), "each malformed form rejected");
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var after) && after.Pos == spawn, Is.True,
                        "the entity never moved");
        }

        [Test]
        public void seated_sender_rejected_the_seat_owns_the_entity()
        {
            var h = new TransactionalHarness(9272).Connected("a");
            var a = h.Clients[0];
            h.Server.Transactions.IsSeated = _ => true;   // NetWorldServer wires this to VehicleHost.IsDriver
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var before), Is.True, "player spawned");
            var spawn = before.Pos;
            string verdict = null;
            a.ConsoleResult += e => verdict = e.Text;

            a.SendConsole("teleport 10 2 10");
            Assert.That(h.StepUntil(() => verdict != null), Is.True, $"verdict came back (seed={h.Net.Seed})");
            Assert.That(verdict, Does.Contain("exit the vehicle"),
                        "seated senders are told why -- ServerVehicles.Step re-asserts the seat every tick, a teleport would silently lose");
            Assert.That(h.Server.Transactions.Diag.ConsoleRejected, Is.EqualTo(1), "rejected at the choke point");
            Assert.That(h.Server.Players.TryGetByOwner(a.PlayerId, out var after) && after.Pos == spawn, Is.True,
                        "the entity never moved");
        }
    }
}
