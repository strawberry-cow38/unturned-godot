using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // Hold-breath-to-steady over the REAL path: a client that sets the button bit on its normal per-tick
    // state command, a server that reads it off the held input and drains the authoritative oxygen bar.
    //
    // ScopeSteadySimTests already covers the rules in isolation. This exists because the rules being right
    // and the bit arriving are different claims, and today has produced three HIGH bugs that lived
    // entirely in the second one.
    [TestFixture]
    public class ScopeSteadyWireTests
    {
        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        static float OxygenOf(TransactionalHarness h, ushort pid)
            => h.Server.Vitals.TryGet(pid, out var e) ? e.Sim.Oxygen : float.NaN;

        /// <summary>Drive N ticks with the steady bit either set or clear, the way ClientWorldSession does.</summary>
        static void Hold(TransactionalHarness h, NetWorldClient c, bool steady, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                c.SendPlayerState(UnityEngine.Vector3.zero, 0f, 0f, UnityEngine.Vector3.zero,
                                  steady ? MoveInput.ButtonSteady : (byte)0, grounded: true, recovAck: 0);
                h.Step();
            }
        }

        [Test]
        public void Holding_The_Bit_Drains_The_Servers_Oxygen()
        {
            var h = new TransactionalHarness(40);
            var c = h.AddClient("Shooter");
            h.StepUntil(() => c.State == NetSessionState.Connected);

            float before = OxygenOf(h, c.PlayerId);
            Assert.That(before, Is.EqualTo(1f).Within(0.02f), "starts with a full bar");

            Hold(h, c, steady: true, ticks: 100);   // ~2 s
            float after = OxygenOf(h, c.PlayerId);

            Assert.That(after, Is.LessThan(before - 0.10f),
                        $"the server should have spent air ({before:0.###} -> {after:0.###})");
        }

        // THE ONE THAT WOULD HAVE CAUGHT THE FREE-ON-DRY-LAND BUG. The surface refill is 0.25/s and the
        // steady drain is 0.133/s, so without HoldingBreath suppressing the refill the bar goes UP while
        // you hold your breath and the whole mechanic costs nothing.
        [Test]
        public void The_Bar_Goes_DOWN_On_Dry_Land_Not_Up()
        {
            var h = new TransactionalHarness(41);
            var c = h.AddClient("Shooter");
            h.StepUntil(() => c.State == NetSessionState.Connected);

            // Spend a little first so the bar has room to refill into, or a full bar hides the bug by clamp.
            Hold(h, c, steady: true, ticks: 60);
            float mid = OxygenOf(h, c.PlayerId);
            Hold(h, c, steady: true, ticks: 60);
            float end = OxygenOf(h, c.PlayerId);

            Assert.That(end, Is.LessThan(mid), $"still draining, not refilling ({mid:0.###} -> {end:0.###})");
        }

        [Test]
        public void Not_Holding_Refills_As_Normal()
        {
            var h = new TransactionalHarness(42);
            var c = h.AddClient("Shooter");
            h.StepUntil(() => c.State == NetSessionState.Connected);

            Hold(h, c, steady: true, ticks: 120);
            float spent = OxygenOf(h, c.PlayerId);
            Assert.That(spent, Is.LessThan(0.95f), "we actually spent something to recover from");

            Hold(h, c, steady: false, ticks: 120);
            Assert.That(OxygenOf(h, c.PlayerId), Is.GreaterThan(spent), "releasing lets the bar come back");
        }

        // The guarantee, over the wire: hold it indefinitely and the server's bar stops at the reserve.
        [Test]
        public void The_Server_Never_Drains_Below_The_Reserve()
        {
            var h = new TransactionalHarness(43);
            var c = h.AddClient("Shooter");
            h.StepUntil(() => c.State == NetSessionState.Connected);

            for (int i = 0; i < 1200; i++)   // ~24 s, four times the full-bar budget
            {
                c.SendPlayerState(UnityEngine.Vector3.zero, 0f, 0f, UnityEngine.Vector3.zero,
                                  MoveInput.ButtonSteady, grounded: true, recovAck: 0);
                h.Step();
                Assert.That(OxygenOf(h, c.PlayerId), Is.GreaterThanOrEqualTo(ScopeSteadySim.SteadyFloor - 0.02f),
                            $"server let the bar past the reserve on tick {i}");
            }
        }

        [Test]
        public void A_Client_That_Never_Sets_The_Bit_Is_Untouched()
        {
            var h = new TransactionalHarness(44);
            var c = h.AddClient("Bystander");
            h.StepUntil(() => c.State == NetSessionState.Connected);

            Hold(h, c, steady: false, ticks: 200);
            Assert.That(OxygenOf(h, c.PlayerId), Is.EqualTo(1f).Within(0.02f),
                        "no bit, no cost -- steadying must not tax everyone");
        }

        [Test]
        public void Two_Players_Steady_Independently()
        {
            var h = new TransactionalHarness(45);
            var a = h.AddClient("A");
            var b = h.AddClient("B");
            h.StepUntil(() => a.State == NetSessionState.Connected && b.State == NetSessionState.Connected);

            for (int i = 0; i < 100; i++)
            {
                a.SendPlayerState(UnityEngine.Vector3.zero, 0f, 0f, UnityEngine.Vector3.zero,
                                  MoveInput.ButtonSteady, grounded: true, recovAck: 0);
                b.SendPlayerState(UnityEngine.Vector3.zero, 0f, 0f, UnityEngine.Vector3.zero,
                                  0, grounded: true, recovAck: 0);
                h.Step();
            }
            Assert.That(OxygenOf(h, a.PlayerId), Is.LessThan(0.95f), "A paid");
            Assert.That(OxygenOf(h, b.PlayerId), Is.EqualTo(1f).Within(0.02f), "B did not");
        }
    }
}
