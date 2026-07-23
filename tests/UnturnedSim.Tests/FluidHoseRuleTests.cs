using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // L0 tests for the hose-completion type-lock rule (fluid IO F3.5c). Booleans reduce the two live ports; the rule
    // decides Ok / None (illegal target) / Mismatch ("cannot mix fluids"). Gravity is NOT decided here.
    [TestFixture]
    public class FluidHoseRuleTests
    {
        // shorthand: complete a hose from `start` role onto `target` role with the given tank states
        static HoseVerdict V(FluidPortKind start, FluidPortKind target, bool startEmpty, bool targetEmpty, bool typesEqual,
                             bool sameOwner = false, bool targetHosed = false)
            => FluidHoseRule.Completion(start, target, startEmpty, targetEmpty, typesEqual, sameOwner, targetHosed);

        [Test]
        public void Source_To_Consumer_Same_Fluid_Ok()
        {
            // both tanks hold the same set fluid -> connects
            Assert.That(V(FluidPortKind.Source, FluidPortKind.Consumer, startEmpty: false, targetEmpty: false, typesEqual: true),
                        Is.EqualTo(HoseVerdict.Ok));
        }

        [Test]
        public void Consumer_Start_To_Source_Ok()
        {
            // you may start from the consumer end and complete on a source (opposite roles)
            Assert.That(V(FluidPortKind.Consumer, FluidPortKind.Source, startEmpty: false, targetEmpty: false, typesEqual: true),
                        Is.EqualTo(HoseVerdict.Ok));
        }

        [Test]
        public void Empty_Target_Adopts_Ok()
        {
            // an empty storage adopts the source's fluid -> not a mismatch
            Assert.That(V(FluidPortKind.Source, FluidPortKind.Consumer, startEmpty: false, targetEmpty: true, typesEqual: false),
                        Is.EqualTo(HoseVerdict.Ok));
        }

        [Test]
        public void Empty_Start_Adopts_Ok()
        {
            Assert.That(V(FluidPortKind.Consumer, FluidPortKind.Source, startEmpty: true, targetEmpty: false, typesEqual: false),
                        Is.EqualTo(HoseVerdict.Ok));
        }

        [Test]
        public void Different_Set_Fluids_Mismatch()
        {
            // both hold DIFFERENT non-empty fluids -> "cannot mix fluids"
            Assert.That(V(FluidPortKind.Source, FluidPortKind.Consumer, startEmpty: false, targetEmpty: false, typesEqual: false),
                        Is.EqualTo(HoseVerdict.Mismatch));
        }

        [Test]
        public void Same_Role_None()
        {
            // two sources (or two consumers) can't hose together
            Assert.That(V(FluidPortKind.Source, FluidPortKind.Source, startEmpty: false, targetEmpty: false, typesEqual: true),
                        Is.EqualTo(HoseVerdict.None));
        }

        [Test]
        public void Same_Container_None()
        {
            Assert.That(V(FluidPortKind.Source, FluidPortKind.Consumer, startEmpty: false, targetEmpty: false, typesEqual: true, sameOwner: true),
                        Is.EqualTo(HoseVerdict.None));
        }

        [Test]
        public void Already_Hosed_Target_None()
        {
            Assert.That(V(FluidPortKind.Source, FluidPortKind.Consumer, startEmpty: false, targetEmpty: false, typesEqual: true, targetHosed: true),
                        Is.EqualTo(HoseVerdict.None));
        }

        [Test]
        public void Mismatch_Outranks_Role_Ok_Only_When_Opposite()
        {
            // a mismatch is only reported for a legal opposite-side target; same-side short-circuits to None first
            Assert.That(V(FluidPortKind.Source, FluidPortKind.Source, startEmpty: false, targetEmpty: false, typesEqual: false),
                        Is.EqualTo(HoseVerdict.None));
        }

        [Test]
        public void Passthrough_To_Consumer_Ok()
        {
            // a fitting's Passthrough (source-side) -> a storage Consumer completes (splitter output feeds a tank)
            Assert.That(V(FluidPortKind.Passthrough, FluidPortKind.Consumer, startEmpty: true, targetEmpty: false, typesEqual: false),
                        Is.EqualTo(HoseVerdict.Ok));
        }

        [Test]
        public void Consumer_To_Passthrough_Ok()
        {
            Assert.That(V(FluidPortKind.Consumer, FluidPortKind.Passthrough, startEmpty: false, targetEmpty: true, typesEqual: false),
                        Is.EqualTo(HoseVerdict.Ok));
        }

        [Test]
        public void Passthrough_To_Source_None()
        {
            // both are source-side (push out) -> not a legal pair
            Assert.That(V(FluidPortKind.Passthrough, FluidPortKind.Source, startEmpty: true, targetEmpty: false, typesEqual: false),
                        Is.EqualTo(HoseVerdict.None));
        }

        [Test]
        public void Source_Side_Test_Helper()
        {
            Assert.That(FluidHoseRule.IsSourceSide(FluidPortKind.Source), Is.True);
            Assert.That(FluidHoseRule.IsSourceSide(FluidPortKind.Passthrough), Is.True);
            Assert.That(FluidHoseRule.IsSourceSide(FluidPortKind.Consumer), Is.False);
        }
    }
}
