using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnturnedGodot.Net;
using UnturnedNet.Tests.Mocks;

namespace UnturnedNet.Tests
{
    // MP_PLAN §2.3/§6: the command (client->server) and event (server->client) planes -- explicit
    // append-only ids, hand-written Write/Read, the one validation choke point (sender identity comes from
    // the connection, never the payload), and "TryDispatch must never throw" under fuzzed input.
    [TestFixture]
    public class CommandEventPlaneTests
    {
        [Test]
        public void Command_Dispatches_ToTheRightHandler_WithSenderIdentityFromCaller()
        {
            var registry = new CommandRegistry();
            var applied = new List<(ushort sender, MockMoveCommand cmd)>();
            registry.Register<MockMoveCommand>(MockCommandIds.Move, MockMoveCommand.TryRead,
                (sender, cmd) => applied.Add((sender, cmd)));

            var wire = new MockMoveCommand(entityId: 3, x: 1f, y: 2f, z: 3f).Pack();

            // The SAME bytes, dispatched on behalf of two different connections, must be attributed to
            // whichever sender the CALLER says delivered them -- never something read out of `wire` itself
            // (MockMoveCommand's wire format doesn't even carry a sender field).
            Assert.That(registry.TryDispatch(wire, senderPlayerId: 11), Is.True);
            Assert.That(registry.TryDispatch(wire, senderPlayerId: 22), Is.True);

            Assert.That(applied.Count, Is.EqualTo(2));
            Assert.That(applied[0].sender, Is.EqualTo(11));
            Assert.That(applied[1].sender, Is.EqualTo(22));
            Assert.That(applied[0].cmd.EntityId, Is.EqualTo(3));
            Assert.That(registry.Diag.Dispatched, Is.EqualTo(2));
        }

        [Test]
        public void Command_SecondClassDispatchesIndependently_InputVsMove()
        {
            var registry = new CommandRegistry();
            MockMoveCommand? move = null;
            MockInputCommand? input = null;
            registry.Register<MockMoveCommand>(MockCommandIds.Move, MockMoveCommand.TryRead, (s, c) => move = c);
            registry.Register<MockInputCommand>(MockCommandIds.Input, MockInputCommand.TryRead, (s, c) => input = c);

            registry.TryDispatch(new MockMoveCommand(1, 0f, 0f, 0f).Pack(), 1);
            registry.TryDispatch(new MockInputCommand(99, 1, -1).Pack(), 1);

            Assert.That(move.HasValue, Is.True);
            Assert.That(input.HasValue, Is.True);
            Assert.That(input.Value.Seq, Is.EqualTo(99));
            Assert.That(input.Value.AxisX, Is.EqualTo(1));
            Assert.That(input.Value.AxisZ, Is.EqualTo(-1));
        }

        [Test]
        public void Command_ValidationChokePoint_RejectsWhenSenderDoesNotOwnTheEntity()
        {
            var registry = new CommandRegistry();
            var owner = new Dictionary<uint, ushort> { [3] = 11 }; // entity 3 is owned by player 11
            bool applied = false;
            registry.Register<MockMoveCommand>(MockCommandIds.Move, MockMoveCommand.TryRead,
                apply: (sender, cmd) => applied = true,
                validate: (sender, cmd) => owner.TryGetValue(cmd.EntityId, out ushort ownerId) && ownerId == sender);

            var wire = new MockMoveCommand(entityId: 3, x: 1f, y: 1f, z: 1f).Pack();

            Assert.That(registry.TryDispatch(wire, senderPlayerId: 99), Is.True, "dispatch succeeds; the command was just refused");
            Assert.That(applied, Is.False, "sender 99 doesn't own entity 3 -- must not apply");
            Assert.That(registry.Diag.ValidationRejected, Is.EqualTo(1));

            Assert.That(registry.TryDispatch(wire, senderPlayerId: 11), Is.True);
            Assert.That(applied, Is.True, "the rightful owner's identical command must apply");
        }

        [Test]
        public void Command_UnknownId_And_Malformed_AreRejectedNotCrashed()
        {
            var registry = new CommandRegistry();
            bool applied = false;
            registry.Register<MockMoveCommand>(MockCommandIds.Move, MockMoveCommand.TryRead, (s, c) => applied = true);

            Assert.That(registry.TryDispatch(new byte[] { 250 }, 1), Is.False, "id 250 was never registered");
            Assert.That(registry.Diag.UnknownIdRejected, Is.EqualTo(1));

            // TryDispatch's return value means "a handler was found and ran without throwing" -- a
            // recognized id with a truncated/malformed payload is still "dispatched" in that sense; the
            // actual rejection shows up as MalformedRejected and the command never applying.
            Assert.That(registry.TryDispatch(new byte[] { MockCommandIds.Move }, 1), Is.True,
                "truncated payload -- TryRead must fail cleanly, not throw");
            Assert.That(applied, Is.False, "a payload with no fields at all must never apply");
            Assert.That(registry.Diag.MalformedRejected, Is.EqualTo(1));

            Assert.That(registry.TryDispatch(null, 1), Is.False);
            Assert.That(registry.TryDispatch(new byte[0], 1), Is.False);
        }

        [Test]
        public void Command_DuplicateIdRegistration_Throws()
        {
            var registry = new CommandRegistry();
            registry.Register<MockMoveCommand>(MockCommandIds.Move, MockMoveCommand.TryRead, (s, c) => { });
            Assert.Throws<InvalidOperationException>(() =>
                registry.Register<MockInputCommand>(MockCommandIds.Move, MockInputCommand.TryRead, (s, c) => { }));
        }

        [Test]
        public void Command_FuzzedRandomBytes_NeverThrows()
        {
            var registry = new CommandRegistry();
            registry.Register<MockMoveCommand>(MockCommandIds.Move, MockMoveCommand.TryRead, (s, c) => { });
            registry.Register<MockInputCommand>(MockCommandIds.Input, MockInputCommand.TryRead, (s, c) => { });

            var rng = new Random(20260716);
            for (int i = 0; i < 2000; i++)
            {
                var blob = new byte[rng.Next(0, 40)];
                rng.NextBytes(blob);
                Assert.DoesNotThrow(() => registry.TryDispatch(blob, (ushort)rng.Next(1, 100)),
                    $"fuzz iteration {i} threw for blob length {blob.Length}");
            }
            Assert.That(registry.Diag.HandlerExceptionsCaught, Is.Zero, "well-behaved handlers never actually throw here");
        }

        [Test]
        public void Event_DispatchesToTheRightHandler()
        {
            var registry = new EventRegistry();
            MockEntityDestroyedEvent? received = null;
            registry.Register<MockEntityDestroyedEvent>(MockEventIds.EntityDestroyed, MockEntityDestroyedEvent.TryRead,
                evt => received = evt);

            var wire = new MockEntityDestroyedEvent(entityId: 5, reason: 2).Pack();
            Assert.That(registry.TryDispatch(wire), Is.True);
            Assert.That(received.HasValue, Is.True);
            Assert.That(received.Value.EntityId, Is.EqualTo(5));
            Assert.That(received.Value.Reason, Is.EqualTo(2));
        }

        [Test]
        public void Event_UnknownId_And_Malformed_AreSkippedNotCrashed()
        {
            var registry = new EventRegistry();
            bool applied = false;
            registry.Register<MockEntityDestroyedEvent>(MockEventIds.EntityDestroyed, MockEntityDestroyedEvent.TryRead, evt => applied = true);

            Assert.That(registry.TryDispatch(new byte[] { 99 }), Is.False);
            Assert.That(registry.Diag.UnknownIdSkipped, Is.EqualTo(1));

            Assert.That(registry.TryDispatch(new byte[] { MockEventIds.EntityDestroyed }), Is.True,
                "the id was recognized and the handler ran safely -- rejection shows up in diagnostics, not the return value");
            Assert.That(applied, Is.False, "missing the reason byte -- must not apply");
            Assert.That(registry.Diag.MalformedSkipped, Is.EqualTo(1));
        }

        [Test]
        public void Event_FuzzedRandomBytes_NeverThrows()
        {
            var registry = new EventRegistry();
            registry.Register<MockEntityDestroyedEvent>(MockEventIds.EntityDestroyed, MockEntityDestroyedEvent.TryRead, evt => { });

            var rng = new Random(424242);
            for (int i = 0; i < 2000; i++)
            {
                var blob = new byte[rng.Next(0, 40)];
                rng.NextBytes(blob);
                Assert.DoesNotThrow(() => registry.TryDispatch(blob), $"fuzz iteration {i} threw for blob length {blob.Length}");
            }
        }
    }
}
