using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // DUPLICATE_AUDIT 2.2 + 2.3 -- the Power Switch was broken server-side in two ways that only show up
    // together with a downstream consumer:
    //
    //   2.2  DeployableReplication.Solve() built `new PowerDevice { Producing, OnFire }` and never set
    //        Conducting, so it defaulted to TRUE. SP passes Deployable.PowerConducting
    //        (`!Def.IsSwitch || _switchOn`). A switch you turned OFF therefore kept conducting on the
    //        server and everything downstream of it stayed lit in MP while being dark in SP.
    //
    //   2.3  CanToggle required `def.FuelCapacity > 0f` -- true of a generator, never of a switch
    //        (Fuel = 0). So the toggle command was rejected at the choke point and the switch could not
    //        be flipped through the server AT ALL. This one hides 2.2: with the toggle rejected, the
    //        entity's ToggledOn never changes, so a test that only flips the switch sees nothing move.
    //
    // Both sides run the same Solve() over the replicated inputs, so fixing it in DeployableReplication
    // fixes server and client together -- there is no second copy to keep in step.
    [TestFixture]
    public class PowerSwitchMpTests
    {
        const ushort GEN = TransactionalFixtures.GeneratorId;
        const ushort SPOT = TransactionalFixtures.SpotlightId;
        const ushort SW = TransactionalFixtures.SwitchId;

        [SetUp]
        public void SetUp() => TransactionalFixtures.RegisterAssets();

        /// <summary>gen --wire--> switch --wire--> spotlight, generator ON, switch ON.
        /// Returns the three net ids once every replica agrees the rig is built and lit.</summary>
        static (TransactionalHarness h, uint genId, uint swId, uint spotId) BuildSwitchedRig(int seed)
        {
            var h = new TransactionalHarness(seed).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(GEN));
            h.Grant(a.PlayerId, new Item(SW));
            h.Grant(a.PlayerId, new Item(SPOT));

            a.SendPlaceDeployable(GEN, new Vector3(-2f, 0f, 0f), 0f);
            a.SendPlaceDeployable(SW, new Vector3(0f, 0f, 0f), 0f);
            a.SendPlaceDeployable(SPOT, new Vector3(2f, 0f, 0f), 0f);
            Assert.That(h.StepUntil(() => a.Deployables.Count == 3), Is.True, $"three placements replicated (seed={seed})");

            uint genId = h.FindDeployable(a, GEN), swId = h.FindDeployable(a, SW), spotId = h.FindDeployable(a, SPOT);
            Assert.That(genId, Is.Not.EqualTo(0u)); Assert.That(swId, Is.Not.EqualTo(0u)); Assert.That(spotId, Is.Not.EqualTo(0u));

            a.SendConnectWire(genId, 0, swId, 0);    // generator Output -> switch Consumer (the relay IN)
            a.SendConnectWire(swId, 1, spotId, 0);   // switch Passthrough -> spotlight Consumer
            a.SendToggleDeployable(genId, true);
            a.SendToggleDeployable(swId, true);
            Assert.That(h.StepUntil(() => a.Deployables.WireCount == 2
                                       && a.Deployables.TryGet(genId, out var g) && g.ToggledOn
                                       && a.Deployables.TryGet(swId, out var s) && s.ToggledOn), Is.True,
                        $"two wires + both toggles replicated (seed={seed})");
            return (h, genId, swId, spotId);
        }

        /// <summary>Is the spotlight's Consumer port (index 0) receiving power in this system's own solve?</summary>
        static bool SpotlightLit(DeployableReplication d, uint spotId)
        {
            d.Solve();
            Assert.That(d.TryGet(spotId, out var e), Is.True, "spotlight entity exists");
            Assert.That(e.Solved, Is.Not.Null.And.Length.GreaterThan(0), "the solve wrote per-port state");
            return e.Solved[0].Powered;
        }

        [Test]
        public void a_switch_can_be_toggled_through_the_server_at_all()
        {
            // 2.3 on its own. A switch has no fuel tank, and CanToggle used to demand one.
            var (h, _, swId, _) = BuildSwitchedRig(7301);
            var a = h.Clients[0];

            Assert.That(h.Server.Deployables.TryGet(swId, out var before) && before.ToggledOn, Is.True,
                        "precondition: the rig left the switch ON");

            a.SendToggleDeployable(swId, false);
            Assert.That(h.StepUntil(() => h.Server.Deployables.TryGet(swId, out var s) && !s.ToggledOn), Is.True,
                        $"the server accepted a switch toggle and the OFF state replicated (seed={h.Net.Seed})");
        }

        [Test]
        public void a_switch_turned_off_stops_conducting_on_the_SERVER()
        {
            // 2.2, the one that mattered: downstream must go dark on the authority, not just on the client shell.
            var (h, _, swId, spotId) = BuildSwitchedRig(7302);
            var a = h.Clients[0];

            Assert.That(SpotlightLit(h.Server.Deployables, spotId), Is.True,
                        "precondition: with the switch ON the spotlight is lit");

            a.SendToggleDeployable(swId, false);
            Assert.That(h.StepUntil(() => h.Server.Deployables.TryGet(swId, out var s) && !s.ToggledOn), Is.True,
                        $"the switch is OFF server-side (seed={h.Net.Seed})");

            Assert.That(SpotlightLit(h.Server.Deployables, spotId), Is.False,
                        "a switch turned OFF must kill its passthrough -- everything downstream goes dark");
        }

        [Test]
        public void the_client_replica_agrees_with_the_server_about_the_dark_spotlight()
        {
            // Both sides run the SAME Solve() over the replicated inputs, so the fix must land on both with
            // no wire change. If this ever diverges from the test above, the two copies have drifted apart.
            var (h, _, swId, spotId) = BuildSwitchedRig(7303);
            var a = h.Clients[0];

            a.SendToggleDeployable(swId, false);
            Assert.That(h.StepUntil(() => a.Deployables.TryGet(swId, out var s) && !s.ToggledOn), Is.True,
                        $"the OFF state reached the replica (seed={h.Net.Seed})");

            Assert.That(SpotlightLit(a.Deployables, spotId), Is.False, "client replica: dark");
            Assert.That(SpotlightLit(h.Server.Deployables, spotId), Is.False, "server: dark");
        }

        [Test]
        public void turning_the_switch_back_on_relights_it()
        {
            // The gate must be a gate, not a one-way kill -- otherwise "fixed" could mean "always off".
            var (h, _, swId, spotId) = BuildSwitchedRig(7304);
            var a = h.Clients[0];

            a.SendToggleDeployable(swId, false);
            Assert.That(h.StepUntil(() => h.Server.Deployables.TryGet(swId, out var s) && !s.ToggledOn), Is.True);
            Assert.That(SpotlightLit(h.Server.Deployables, spotId), Is.False, "dark while off");

            a.SendToggleDeployable(swId, true);
            Assert.That(h.StepUntil(() => h.Server.Deployables.TryGet(swId, out var s) && s.ToggledOn), Is.True,
                        $"the switch went back ON (seed={h.Net.Seed})");
            Assert.That(SpotlightLit(h.Server.Deployables, spotId), Is.True, "lit again once the switch is back on");
        }

        [Test]
        public void a_plain_consumer_still_cannot_be_toggled()
        {
            // The control for 2.3. Widening CanToggle to admit switches must not admit everything fuel-less:
            // a spotlight has no tank AND is not a switch, so it must still be refused at the validator.
            //
            // (An earlier version of this control asserted a FUELLED-BUT-EMPTY generator is refused. That was
            // wrong about the contract, and running it before the fix is what caught it: CanToggle gates on
            // the def HAVING a tank, not on the entity's current fuel -- an empty generator toggles fine and
            // simply does not Produce, which is Producing()'s job, not the toggle gate's.)
            var h = new TransactionalHarness(7305).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(SPOT));
            a.SendPlaceDeployable(SPOT, new Vector3(2f, 0f, 0f), 0f);
            Assert.That(h.StepUntil(() => a.Deployables.Count == 1), Is.True, "the replica has it");
            uint spotId = h.FindDeployable(a, SPOT);
            Assert.That(spotId, Is.Not.EqualTo(0u));

            long rejected = h.Server.Commands.Diag.ValidationRejected;
            a.SendToggleDeployable(spotId, true);
            h.Step(20);

            Assert.That(h.Server.Deployables.TryGet(spotId, out var s) && !s.ToggledOn, Is.True,
                        "a spotlight is neither a generator nor a switch -- nothing to toggle");
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.GreaterThan(rejected),
                        "and it was refused at the validator, not silently ignored");
        }

        [Test]
        public void an_empty_generator_toggles_but_does_not_produce()
        {
            // The contract the wrong control above assumed away, pinned so nobody "fixes" CanToggle into
            // checking fuel level: the toggle is accepted, and Producing() is what a dry tank kills.
            var h = new TransactionalHarness(7306).Connected("a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(GEN));
            h.Grant(a.PlayerId, new Item(SPOT));
            a.SendPlaceDeployable(GEN, new Vector3(-2f, 0f, 0f), 0f);
            a.SendPlaceDeployable(SPOT, new Vector3(2f, 0f, 0f), 0f);
            Assert.That(h.StepUntil(() => a.Deployables.Count == 2), Is.True, "the replica has both");
            uint genId = h.FindDeployable(a, GEN), spotId = h.FindDeployable(a, SPOT);
            Assert.That(genId, Is.Not.EqualTo(0u)); Assert.That(spotId, Is.Not.EqualTo(0u));

            a.SendConnectWire(genId, 0, spotId, 0);
            h.Server.Deployables.ServerSetScalars(genId, 450f, 0f, onFire: false, h.Server.Session.CurrentTick);
            a.SendToggleDeployable(genId, true);
            Assert.That(h.StepUntil(() => h.Server.Deployables.TryGet(genId, out var g) && g.ToggledOn), Is.True,
                        $"the toggle IS accepted on an empty generator (seed={h.Net.Seed})");

            Assert.That(SpotlightLit(h.Server.Deployables, spotId), Is.False,
                        "but a dry tank means it produces nothing, so the spotlight stays dark");
        }
    }
}
