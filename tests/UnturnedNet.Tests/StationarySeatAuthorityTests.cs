using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    [TestFixture]
    public sealed class StationarySeatAuthorityTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void HostAnchorIsOptInAndRetiresOnlyOneWalkingWindow(bool enabled)
        {
            var h = new TransactionalHarness(7362).Connected("occupant");
            var c = h.Clients[0];
            var anchor = new Vector3(2, .4f, -2);
            bool occupied = true;
            h.Server.PlayerHost.AcceptSeatedActions = enabled;
            h.Server.PlayerHost.StationarySeatOf = owner => occupied && owner == c.PlayerId
                ? new StationarySeatPose(394, anchor, 90) : null;
            int shots = 0, recoveries = 0;
            byte recovery = 0;
            h.Server.PlayerHost.CombatDispatch = (_, _, _) => shots++;
            c.PlayerRecov += e => { recoveries++; recovery = e.RecovCounter; };
            c.SendFire(Vector3.up, new Vector3(0, 0, -1));
            for (int n = 0; n < 40; n++) {
                c.SendPlayerState(new Vector3(400, -100, 400), 250, 80,
                    new Vector3(30, -32, 30), 255, false, recovery);
                h.Step();
            }
            h.Server.Players.TryGetByOwner(c.PlayerId, out var player);
            Assert.That(shots, Is.EqualTo(1), "Motion never duplicates the carried action");
            if (!enabled) {
                Assert.That(player.Pos, Is.Not.EqualTo(anchor), "A null-default legacy host does not adopt furniture metadata");
                return;
            }
            Assert.That(player.Pos.x, Is.EqualTo(anchor.x).Within(.01));
            Assert.That(player.Pos.y, Is.EqualTo(anchor.y).Within(.01));
            Assert.That(player.YawDegrees, Is.EqualTo(90).Within(.1));
            Assert.That(recoveries, Is.EqualTo(1), "Accepted entry invalidates old walking once, never every fixed step");
            Assert.That(h.Server.PlayerHost.DebugRecovering(c.PlayerId), Is.True);
            occupied = false;
            c.SendPlayerState(anchor, 90, 0, Vector3.zero, 0, true, recovery);
            h.Step(10);
            Assert.That(h.Server.PlayerHost.DebugRecovering(c.PlayerId), Is.False);
            Assert.That(h.Server.PlayerHost.IsClientDriven(c.PlayerId), Is.True);
            c.SendPlayerState(anchor + new Vector3(100, 0, 0), 0, 0, Vector3.zero, 0, true, recovery);
            h.Step(10);
            Assert.That(recoveries, Is.EqualTo(2), "Standing restores the ordinary envelope; no broader teleport allowance");
            Assert.That(player.Pos.x, Is.EqualTo(anchor.x).Within(.01));
        }
    }
}
