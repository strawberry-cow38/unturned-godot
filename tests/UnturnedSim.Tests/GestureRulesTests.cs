using System;
using System.IO;
using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // The admission rules for gestures, which are the whole safety story of the feature: SURRENDER is the
    // precondition for being handcuffed, so "who may enter which gesture, from what stance, and when" is not
    // decoration -- a hole here is a way to cuff someone who never put their hands up.
    [TestFixture]
    public class GestureRulesTests
    {
        // The table is INDEXED by the enum value. Inserting a gesture in the middle without moving the row
        // would shift every rule by one, and the symptom -- salutes making you surrender -- is not something
        // anyone would guess at from the diff.
        [Test]
        public void TableIsIndexedByTheEnum()
        {
            Assert.That(GestureRules.TableIsAligned(), Is.True,
                "GestureRules.Table must have one row per EPlayerGesture, in enum order");
        }

        [Test]
        public void ArrestedPlayerCannotGestureAtAll()
        {
            foreach (EPlayerGesture g in Enum.GetValues(typeof(EPlayerGesture)))
            {
                if (!GestureRules.Of(g).PlayerRequestable) continue;
                Assert.That(GestureRules.CanRequest(g, EPlayerGesture.ARREST_START, EPlayerStance.STAND, false),
                            Is.False, $"{g} must be refused while cuffed");
            }
            // ...and it says WHY it is refused, naming the thing the player cannot do anything about.
            Assert.That(GestureRules.RefusalFor(EPlayerGesture.WAVE, EPlayerGesture.ARREST_START, EPlayerStance.STAND, true),
                        Is.EqualTo("You're cuffed."), "cuffs outrank a full hand: source tests them first");
        }

        [Test]
        public void SomethingInYourHandsBlocksIt()
        {
            Assert.That(GestureRules.CanRequest(EPlayerGesture.WAVE, EPlayerGesture.NONE, EPlayerStance.STAND, true), Is.False);
            Assert.That(GestureRules.CanRequest(EPlayerGesture.WAVE, EPlayerGesture.NONE, EPlayerStance.STAND, false), Is.True);
        }

        [TestCase(EPlayerStance.PRONE)]
        [TestCase(EPlayerStance.DRIVING)]
        [TestCase(EPlayerStance.SITTING)]
        public void SomeStancesBlockEveryGesture(EPlayerStance stance)
        {
            Assert.That(GestureRules.CanRequest(EPlayerGesture.WAVE, EPlayerGesture.NONE, stance, false), Is.False);
            Assert.That(GestureRules.CanRequest(EPlayerGesture.SURRENDER_START, EPlayerGesture.NONE, stance, false), Is.False);
        }

        [Test]
        public void TheTwoStanceGatedGesturesAreGated()
        {
            Assert.That(GestureRules.CanRequest(EPlayerGesture.REST_START, EPlayerGesture.NONE, EPlayerStance.CROUCH, false), Is.True);
            Assert.That(GestureRules.CanRequest(EPlayerGesture.REST_START, EPlayerGesture.NONE, EPlayerStance.STAND, false), Is.False);
            Assert.That(GestureRules.CanRequest(EPlayerGesture.T_POSE_START, EPlayerGesture.NONE, EPlayerStance.STAND, false), Is.True);
            Assert.That(GestureRules.CanRequest(EPlayerGesture.T_POSE_START, EPlayerGesture.NONE, EPlayerStance.CROUCH, false), Is.False);
            // ...and standing is otherwise fine, so the two above are failing on the STANCE and not on something
            // else that would make this whole fixture pass for the wrong reason.
            Assert.That(GestureRules.CanRequest(EPlayerGesture.SALUTE, EPlayerGesture.NONE, EPlayerStance.STAND, false), Is.True);
            Assert.That(GestureRules.CanRequest(EPlayerGesture.SALUTE, EPlayerGesture.NONE, EPlayerStance.CROUCH, false), Is.True);
        }

        // A WHITELIST, not a blacklist. PICKUP is fired by the item manager, the PUNCHes by combat and the
        // ARRESTs by a captor -- a client asking for any of them is ignored, which is what stops "cuff yourself
        // so nobody can cuff you" and "arrest me, I'll do it myself".
        [TestCase(EPlayerGesture.PICKUP)]
        [TestCase(EPlayerGesture.PUNCH_LEFT)]
        [TestCase(EPlayerGesture.PUNCH_RIGHT)]
        [TestCase(EPlayerGesture.ARREST_START)]
        [TestCase(EPlayerGesture.ARREST_STOP)]
        public void SystemDrivenGesturesCannotBeRequested(EPlayerGesture g)
        {
            Assert.That(GestureRules.Of(g).PlayerRequestable, Is.False);
            Assert.That(GestureRules.CanRequest(g, EPlayerGesture.NONE, EPlayerStance.STAND, false), Is.False);
        }

        [Test]
        public void OnlyTheInventoryPairStaysPrivate()
        {
            foreach (EPlayerGesture g in Enum.GetValues(typeof(EPlayerGesture)))
            {
                if (g == EPlayerGesture.NONE) continue;
                bool inventory = g == EPlayerGesture.INVENTORY_START || g == EPlayerGesture.INVENTORY_STOP;
                Assert.That(GestureRules.Of(g).Broadcast, Is.EqualTo(!inventory), $"{g} broadcast");
            }
        }

        // ⭐ THE ONE THAT MATTERS. A STOP must only end its OWN start. Without that rule a cuffed player could
        // send SURRENDER_STOP -- a gesture they are allowed to send in general -- and walk out of the handcuffs.
        [Test]
        public void AStopOnlyEndsItsOwnStart()
        {
            Assert.That(GestureRules.Apply(EPlayerGesture.ARREST_START, EPlayerGesture.SURRENDER_STOP),
                        Is.EqualTo(EPlayerGesture.ARREST_START), "SURRENDER_STOP must not unlock handcuffs");
            Assert.That(GestureRules.Apply(EPlayerGesture.ARREST_START, EPlayerGesture.REST_STOP),
                        Is.EqualTo(EPlayerGesture.ARREST_START));
            Assert.That(GestureRules.Apply(EPlayerGesture.ARREST_START, EPlayerGesture.ARREST_STOP),
                        Is.EqualTo(EPlayerGesture.NONE), "...but its own STOP does");
            Assert.That(GestureRules.Apply(EPlayerGesture.SURRENDER_START, EPlayerGesture.SURRENDER_STOP),
                        Is.EqualTo(EPlayerGesture.NONE));
            // A one-shot leaves the state alone -- waving while surrendering keeps your hands up.
            Assert.That(GestureRules.Apply(EPlayerGesture.SURRENDER_START, EPlayerGesture.WAVE),
                        Is.EqualTo(EPlayerGesture.SURRENDER_START));
            // ...and entering a state replaces whatever state you were in.
            Assert.That(GestureRules.Apply(EPlayerGesture.SURRENDER_START, EPlayerGesture.ARREST_START),
                        Is.EqualTo(EPlayerGesture.ARREST_START), "cuffing someone with their hands up is the whole flow");
        }

        // A clip name that does not exist in rig.json plays NOTHING and throws NOTHING -- the gesture would
        // simply have no animation, which is exactly the silent-null failure a missing content table gave the
        // spraypaints. Checked as text: rig.json is 22 MB and a substring scan costs milliseconds where parsing
        // it costs seconds.
        [Test]
        public void EveryClipNamedHereExistsInTheRig()
        {
            string rig = FindContent("rig.json");
            Assert.That(rig, Is.Not.Null, "could not locate game/content/rig.json -- this test cannot verify anything without it");
            string text = File.ReadAllText(rig);
            int checkedClips = 0;
            foreach (EPlayerGesture g in Enum.GetValues(typeof(EPlayerGesture)))
            {
                string clip = GestureRules.ClipOf(g);
                if (clip == null) continue;
                checkedClips++;
                Assert.That(text.Contains("\"" + clip + "\""), Is.True, $"{g} names clip {clip}, which is not in rig.json");
            }
            Assert.That(checkedClips, Is.EqualTo(9), "nine gestures ship a clip; T_POSE and the punches deliberately do not");
        }

        static string FindContent(string file)
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, "game", "content", file);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }
}
