using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    [TestFixture]
    public class SeatedCombatCarrierTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void OptionalSeatedCarrierDispatchesOnceWithoutAdoptingAnyMotion(bool enabled)
        {
            var h = new TransactionalHarness(7351).Connected("passenger");
            var c = h.Clients[0];
            h.Server.PlayerHost.AcceptSeatedActions = enabled;
            var car = h.Server.Vehicles.ServerSpawn(h.Server.Ids.Mint(), 0, 0, new Vector3(1, 0, 0), h.Server.Session.CurrentTick);
            h.Server.Vehicles.ServerSetSeatCount(new NetId(car.NetIdValue), 2);
            c.SendEnterVehicle(car.NetIdValue, 1);
            Assert.That(h.StepUntil(() => h.Server.VehicleHost.IsSeated(c.PlayerId)), Is.True);
            h.Step(10);
            h.Server.Players.TryGetByOwner(c.PlayerId, out var player);
            Vector3 position = player.Pos;
            int received = 0;
            h.Server.PlayerHost.CombatDispatch = (owner, action, tick) => {
                Assert.That(owner, Is.EqualTo(c.PlayerId));
                Assert.That(action.Kind, Is.EqualTo(CombatEventKind.Fire));
                Assert.That(h.Server.Players.TryGetHeldInput(owner, out var input), Is.True);
                Assert.That(input.HeldItemId, Is.EqualTo(4), "held identity precedes its action");
                Assert.That(input.Buttons, Is.Zero, "seated sender cannot claim sprint or steady");
                received++;
            };
            int recoveries = 0; c.PlayerRecov += _ => recoveries++;
            c.SendFire(position + Vector3.up, new Vector3(0, 0, -1));
            // Repeated packets carry the same queued shot until the ACK returns.
            for (int n = 0; n < 20; n++) {
                c.SendPlayerState(new Vector3(500, -200, 500), 190, 89,
                    new Vector3(30, -30, 30), 255, false, 99, heldItemId: 4);
                h.Step();
            }
            Assert.That(received, Is.EqualTo(enabled ? 1 : 0));
            Assert.That(player.Pos, Is.EqualTo(position), "seat owns pose despite hostile action-carrier position");
            Assert.That(recoveries, Is.Zero, "ignored seated motion cannot generate an on-foot rollback");
            Assert.That(h.Server.PlayerHost.IsClientDriven(c.PlayerId), Is.False, "carrier never becomes a walking solve");
            if (enabled) { Assert.That(player.LastProcessedCombatSeq, Is.Not.Zero); Assert.That(c.PendingCombatCount, Is.Zero, "replicated ACK retires the queued shot"); }
        }
    }
}
