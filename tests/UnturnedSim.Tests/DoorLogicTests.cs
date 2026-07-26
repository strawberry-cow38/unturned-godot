using NUnit.Framework;
using SDG.Unturned;

namespace UnturnedSim.Tests
{
    // L0 tests for door access rules. The whole point of splitting the decision out of the Godot node is
    // that "who may open this and when" is where the bugs live -- hinges either turn or they don't.
    [TestFixture]
    public class DoorLogicTests
    {
        const ulong Me = 111, Someone = 222, MyGroup = 900, OtherGroup = 901;

        static DoorLogic.DoorState Door(ulong owner = Me, ulong group = 0, bool locked = true, bool open = false) =>
            new DoorLogic.DoorState { Owner = owner, Group = group, Locked = locked, IsOpen = open, LastToggled = -100 };

        [Test]
        public void An_Unlocked_Door_Opens_For_Anyone()
        {
            var d = Door(locked: false);
            Assert.That(DoorLogic.CanToggle(d, Someone, 0, 0, false, out var why), Is.True);
            Assert.That(why, Is.EqualTo(DoorRefusal.None));
        }

        [Test]
        public void A_Locked_Door_Refuses_A_Stranger_And_Says_Why()
        {
            var d = Door();
            Assert.That(DoorLogic.CanToggle(d, Someone, 0, 0, false, out var why), Is.False);
            Assert.That(why, Is.EqualTo(DoorRefusal.Locked), "the prompt needs a reason, not just a no");
        }

        [Test]
        public void The_Owner_Opens_Their_Own_Locked_Door()
        {
            var d = Door();
            Assert.That(DoorLogic.CanToggle(d, Me, 0, 0, false, out _), Is.True);
        }

        [Test]
        public void A_Groupmate_Gets_In_And_A_Rival_Group_Does_Not()
        {
            var d = Door(group: MyGroup);
            Assert.That(DoorLogic.CanToggle(d, Someone, MyGroup, 0, false, out _), Is.True);
            Assert.That(DoorLogic.CanToggle(d, Someone, OtherGroup, 0, false, out _), Is.False);
        }

        [Test]
        public void Group_Zero_Is_Not_A_Group_Everyone_Shares()
        {
            // The trap: treating "no group" as a value that matches, so every ungrouped player would
            // open every ungrouped-but-locked door.
            var d = Door(group: 0);
            Assert.That(DoorLogic.CanToggle(d, Someone, 0, 0, false, out var why), Is.False);
            Assert.That(why, Is.EqualTo(DoorRefusal.Locked));
        }

        [Test]
        public void An_Unowned_Door_Cannot_Be_Locked_Shut_Forever()
        {
            var d = Door(owner: 0);
            Assert.That(DoorLogic.CanToggle(d, Someone, 0, 0, false, out _), Is.True,
                "a locked door with no owner would be openable by nobody");

            Assert.That(DoorLogic.TrySetLocked(ref d, Someone, true), Is.False, "you cannot lock what you do not own");
        }

        [Test]
        public void Only_The_Owner_Sets_The_Lock()
        {
            var d = Door(locked: false);
            Assert.That(DoorLogic.TrySetLocked(ref d, Someone, true), Is.False);
            Assert.That(d.Locked, Is.False);
            Assert.That(DoorLogic.TrySetLocked(ref d, Me, true), Is.True);
            Assert.That(d.Locked, Is.True);
        }

        [Test]
        public void A_Door_Will_Not_Strobe_Under_A_Held_Key()
        {
            var d = Door(locked: false);
            Assert.That(DoorLogic.CanToggle(d, Me, 0, 10.0, false, out _), Is.True);
            d = DoorLogic.Toggle(d, 10.0);
            Assert.That(d.IsOpen, Is.True);

            Assert.That(DoorLogic.CanToggle(d, Me, 0, 10.1, false, out var why), Is.False);
            Assert.That(why, Is.EqualTo(DoorRefusal.Cooldown));

            Assert.That(DoorLogic.CanToggle(d, Me, 0, 10.0 + DoorLogic.ToggleCooldown + 0.01, false, out _), Is.True);
        }

        [Test]
        public void A_Blocked_Arc_Refuses_So_The_Leaf_Cannot_Sweep_Through_Someone()
        {
            var d = Door(locked: false, open: true);
            Assert.That(DoorLogic.CanToggle(d, Me, 0, 0, true, out var why), Is.False);
            Assert.That(why, Is.EqualTo(DoorRefusal.Obstructed));
        }

        [Test]
        public void Toggling_Flips_The_Leaf_And_Stamps_The_Clock()
        {
            var d = Door(locked: false);
            d = DoorLogic.Toggle(d, 5.0);
            Assert.That(d.IsOpen, Is.True);
            Assert.That(d.LastToggled, Is.EqualTo(5.0));
            d = DoorLogic.Toggle(d, 6.0);
            Assert.That(d.IsOpen, Is.False);
        }

        [Test]
        public void Access_And_Toggle_Agree_So_A_Server_Cannot_Contradict_Its_Own_Validation()
        {
            var d = Door(group: MyGroup);
            foreach (var (p, g) in new[] { (Me, 0UL), (Someone, MyGroup), (Someone, OtherGroup), (Someone, 0UL) })
            {
                bool access = DoorLogic.HasAccess(d, p, g);
                bool toggle = DoorLogic.CanToggle(d, p, g, 0, false, out var why);
                Assert.That(toggle, Is.EqualTo(access), $"player {p}/group {g} disagreed (why={why})");
            }
        }
    }
}
