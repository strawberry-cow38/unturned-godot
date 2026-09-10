using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Respraying a vehicle across the wire (v41). master 2026-09-10: "wire it for MP", on the spraypaint
    // feature that shipped SP-first and was flagged as not crossing the wire.
    //
    // The security property is the one worth pinning: the client names the CAR and the CAN, never the
    // colour, so the whole feature rests on the server refusing a can the sender does not own. A version of
    // this that trusted the payload would look identical in singleplayer and let anyone repaint the map.
    [TestFixture]
    public class VehiclePaintMpTests
    {
        const ushort CanId = TransactionalFixtures.ScrapId;   // stands in for a spraypaint: the fixtures register it
        const uint Hotrod = 0xEC2A20;

        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        static TransactionalHarness Painted(out uint netId)
        {
            var h = new TransactionalHarness(7710);
            h.Server.Transactions.PaintColorFor = id => id == CanId ? Hotrod : (uint?)null;
            netId = h.Server.Vehicles.ServerSpawn(h.Server.Ids.Mint(), 0, 0, Vector3.zero,
                                                  h.Server.Session.CurrentTick).NetIdValue;
            return h;
        }

        [Test]
        public void Respray_SpendsTheCan_AndReachesEveryClient()
        {
            var h = Painted(out uint netId);
            h.Connected("a", "b");
            var a = h.Clients[0]; var b = h.Clients[1];
            h.Grant(a.PlayerId, new Item(CanId, 1));
            h.StepUntil(() => a.Vehicles.Count == 1 && b.Vehicles.Count == 1);

            var inv = h.Server.Transactions.InventoryForTest(a.PlayerId);
            Assert.That(inv.getItemCount(CanId), Is.EqualTo(1), "the sprayer starts with one can");

            a.SendPaintVehicle(netId, CanId);
            h.StepUntil(() => h.Server.Vehicles.TryGet(new NetId(netId), out var e) && e.PaintRgb.HasValue);

            Assert.That(h.Server.Vehicles.TryGet(new NetId(netId), out var se) && se.PaintRgb == Hotrod,
                        Is.True, "the server took the colour off the CAN, not off the wire");
            Assert.That(inv.getItemCount(CanId), Is.EqualTo(0), "and spent it");

            Assert.That(h.StepUntil(() => b.Vehicles.TryGet(new NetId(netId), out var be) && be.PaintRgb == Hotrod),
                        Is.True, "the OTHER player sees the respray -- this is the whole point of the wire");
            Assert.That(a.Vehicles.StateHash(), Is.EqualTo(h.Server.Vehicles.StateHash()), "vehicle block parity");
        }

        [Test]
        public void Respray_IsRefused_WithoutTheCan()
        {
            var h = Painted(out uint netId);
            h.Connected("a");
            var a = h.Clients[0];
            h.StepUntil(() => a.Vehicles.Count == 1);
            // No Grant: an empty-handed client asking for a colour it named itself.
            a.SendPaintVehicle(netId, CanId);
            h.Step(30);
            Assert.That(h.Server.Vehicles.TryGet(new NetId(netId), out var e) && !e.PaintRgb.HasValue,
                        Is.True, "a client that does not own the can paints nothing");
        }

        [Test]
        public void Respray_IsRefused_ForAnItemThatIsNotAPaint()
        {
            var h = Painted(out uint netId);
            h.Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(TransactionalFixtures.BeansId, 1));
            h.StepUntil(() => a.Vehicles.Count == 1);
            a.SendPaintVehicle(netId, TransactionalFixtures.BeansId);
            h.Step(30);
            Assert.That(h.Server.Vehicles.TryGet(new NetId(netId), out var e) && !e.PaintRgb.HasValue,
                        Is.True, "a tin of beans is not a spraypaint however hard the client insists");
        }

        [Test]
        public void Respray_IsRefused_FromAcrossTheMap()
        {
            var h = new TransactionalHarness(7711);
            h.Server.Transactions.PaintColorFor = id => id == CanId ? Hotrod : (uint?)null;
            // Well past the 8 m reach; the player spawns at the origin.
            uint netId = h.Server.Vehicles.ServerSpawn(h.Server.Ids.Mint(), 0, 0, new Vector3(0f, 0f, 60f),
                                                       h.Server.Session.CurrentTick).NetIdValue;
            h.Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(CanId, 1));
            h.StepUntil(() => a.Vehicles.Count == 1);

            a.SendPaintVehicle(netId, CanId);
            h.Step(30);
            Assert.That(h.Server.Vehicles.TryGet(new NetId(netId), out var e) && !e.PaintRgb.HasValue,
                        Is.True, "out of reach paints nothing");
            Assert.That(h.Server.Transactions.InventoryForTest(a.PlayerId).getItemCount(CanId), Is.EqualTo(1),
                        "...and the can is NOT spent -- the spend happens after every check, not before one");
        }

        [Test]
        public void UnpaintedAndBlack_DoNotHashAlike()
        {
            // PaintRgb is nullable rather than 0-means-unpainted because #000000 is a legal colour. If the
            // hash collapsed them, a car sprayed black would be indistinguishable from one never sprayed and
            // the desync check would pass through a real divergence.
            var h = new TransactionalHarness(7712);
            var id = h.Server.Ids.Mint();
            h.Server.Vehicles.ServerSpawn(id, 0, 0, Vector3.zero, h.Server.Session.CurrentTick);
            ulong unpainted = h.Server.Vehicles.StateHash();
            h.Server.Vehicles.ServerSetPaint(id, 0x000000, h.Server.Session.CurrentTick);
            ulong black = h.Server.Vehicles.StateHash();
            Assert.That(black, Is.Not.EqualTo(unpainted), "painted black differs from never painted");
        }
    }
}
