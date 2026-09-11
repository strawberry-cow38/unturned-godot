using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // Handcuffs. Every check here is a way the feature turns into a griefing tool or an exploit if it is wrong,
    // which is why the rules live in core with tests rather than inline in the command handler.
    [TestFixture]
    public class ArrestSimTests
    {
        const ushort Captor = 1, Target = 2, Other = 3;

        [Test]
        public void TheTwoRestraintsDifferExactlyAsTheirDatDoes()
        {
            Assert.That(ArrestDef.StrengthOf(ArrestDef.Handcuffs), Is.EqualTo(128));
            Assert.That(ArrestDef.StrengthOf(ArrestDef.CableTie), Is.EqualTo(64), "a tie is exactly half the work");
            Assert.That(ArrestDef.StrengthOf(4), Is.EqualTo(0), "a rifle is not a restraint");
            // The key recovers the cuffs; a cable tie has no key at all, which is the other half of the trade.
            Assert.That(ArrestDef.IsKey(ArrestDef.HandcuffsKey), Is.True);
            Assert.That(ArrestDef.IsKey(ArrestDef.CableTie), Is.False);
            Assert.That(ArrestDef.RecoversItem(ArrestDef.HandcuffsKey), Is.EqualTo(ArrestDef.Handcuffs));
        }

        // ⭐ THE ONE THAT KEEPS THIS FROM BEING A GRIEFING TOOL. Retail gates the arrest on the target's gesture
        // (UseableArrestStart: the raycast hit must be a player in SURRENDER_START). Without it, "aim at anyone,
        // click, they are cuffed" -- which is not the mechanic and would be the first thing abused.
        [Test]
        public void YouCannotCuffSomeoneWhoIsNotSurrendering()
        {
            var a = new ArrestSim();
            Assert.That(a.TryArrest(Captor, Target, ArrestDef.Handcuffs, targetIsSurrendering: false), Is.False);
            Assert.That(a.IsArrested(Target), Is.False);
            // ...and the control: the SAME call with the hands up works, so the refusal above is the gesture
            // and not something else quietly failing.
            Assert.That(a.TryArrest(Captor, Target, ArrestDef.Handcuffs, targetIsSurrendering: true), Is.True);
            Assert.That(a.IsArrested(Target), Is.True);
        }

        // Being arrested makes you un-arrestable, so arresting YOURSELF would be a way to become permanently
        // immune to anyone else's cuffs. Cheap to close, ugly to discover later.
        [Test]
        public void YouCannotCuffYourself()
        {
            var a = new ArrestSim();
            Assert.That(a.TryArrest(Captor, Captor, ArrestDef.Handcuffs, true), Is.False);
            Assert.That(a.IsArrested(Captor), Is.False);
        }

        [Test]
        public void ASecondArrestDoesNotRestartTheTimer()
        {
            var a = new ArrestSim();
            Assert.That(a.TryArrest(Captor, Target, ArrestDef.CableTie, true), Is.True);
            for (int i = 0; i < 60; i++) a.Struggle(Target);          // 60 of the 64 done
            Assert.That(a.TryArrest(Other, Target, ArrestDef.Handcuffs, true), Is.False,
                        "a second restraint must not re-cuff someone at 128 when they are four struggles from free");
            a.TryGet(Target, out var rec);
            Assert.That(rec.Strength, Is.EqualTo(4));
        }

        // The escape, and the reason the .dat numbers are not flavour: it takes EXACTLY Strength struggles.
        [TestCase(ArrestDef.Handcuffs, 128)]
        [TestCase(ArrestDef.CableTie, 64)]
        public void StruggleTakesExactlyTheRestraintsStrength(ushort restraint, int expected)
        {
            var a = new ArrestSim();
            a.TryArrest(Captor, Target, restraint, true);
            for (int i = 1; i < expected; i++)
                Assert.That(a.Struggle(Target), Is.False, $"struggle {i} of {expected} must not free them yet");
            Assert.That(a.Struggle(Target), Is.True, $"struggle {expected} is the one that breaks it");
            Assert.That(a.IsArrested(Target), Is.False);
            // ...and struggling when you are not cuffed is a no-op, not an exception and not a free pass.
            Assert.That(a.Struggle(Target), Is.False);
        }

        [Test]
        public void TheKeyFreesThemAndHandsTheCuffsBack()
        {
            var a = new ArrestSim();
            a.TryArrest(Captor, Target, ArrestDef.Handcuffs, true);
            Assert.That(a.TryUnlock(Target, ArrestDef.HandcuffsKey), Is.EqualTo(ArrestDef.Handcuffs));
            Assert.That(a.IsArrested(Target), Is.False);
        }

        // ⭐ A key must not CONJURE handcuffs off a cable tie. What comes back is what they were cuffed with.
        [Test]
        public void AKeyOnACableTieRecoversNothing()
        {
            var a = new ArrestSim();
            a.TryArrest(Captor, Target, ArrestDef.CableTie, true);
            Assert.That(a.TryUnlock(Target, ArrestDef.HandcuffsKey), Is.EqualTo(0),
                        "a tie is cut, not unlocked -- and it certainly does not turn into a pair of handcuffs");
        }

        [Test]
        public void SomethingThatIsNotAKeyUnlocksNothing()
        {
            var a = new ArrestSim();
            a.TryArrest(Captor, Target, ArrestDef.Handcuffs, true);
            Assert.That(a.TryUnlock(Target, 4), Is.EqualTo(0));
            Assert.That(a.IsArrested(Target), Is.True, "...and they are still cuffed");
        }

        // ⭐ DISCONNECTING MUST NOT BE AN ESCAPE. Forget() drops the leaver's OWN record; a captor logging off
        // leaves their prisoner cuffed, or quitting becomes the fastest way to undo your own arrest.
        [Test]
        public void ACaptorLeavingDoesNotFreeTheirPrisoner()
        {
            var a = new ArrestSim();
            a.TryArrest(Captor, Target, ArrestDef.Handcuffs, true);
            a.Forget(Captor);
            Assert.That(a.IsArrested(Target), Is.True);
            a.TryGet(Target, out var rec);
            Assert.That(rec.CaptorId, Is.EqualTo(Captor), "the record still names who did it");
        }
    }
}
