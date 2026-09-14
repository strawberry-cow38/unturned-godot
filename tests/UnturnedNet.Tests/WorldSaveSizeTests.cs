using System.Text.Json;
using NUnit.Framework;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // THE SAVE THAT STOPPED THE GAME.
    //
    // On a real PEI world the save reached 41.5 MB / 1.54M lines, of which Containers were 38.6 MB. The
    // autosave (WorldSaveDriver.AutosaveSeconds = 60) serialises the whole file on the MAIN THREAD, so once
    // the write took longer than 60 s the next autosave was already due and it never caught up: measured at a
    // permanent ~1 fps with the GPU idle at 0% and one core pegged on JSON. It survived a reboot, reproduced
    // on clean main and on a commit that had measured 95 fps hours earlier, and was identical at another map
    // location -- which is exactly why it read as a code regression for an hour. It was the save file.
    //
    // The bloat: every JarSave wrote its ten gun fields even on a tomato, ten lines of eighteen per item,
    // across every item in 696 containers. WorldSave now omits them when they hold the "not a gun" sentinel.
    //
    // These tests exist because that omission is only safe if a field that is ABSENT reads back as the value
    // that was omitted -- otherwise every pre-existing save silently loses its guns. Each one fails if the
    // omission is wrong, not merely if it is missing.
    [TestFixture]
    public class WorldSaveSizeTests
    {
        static WorldSave.JarSave Tomato() => new WorldSave.JarSave { X = 1, Y = 2, Id = 66, Amount = 1, Quality = 100 };

        static WorldSave.JarSave Gun() => new WorldSave.JarSave
        {
            X = 0, Y = 0, Id = 4, Amount = 1, Quality = 88,
            GunAmmo = 17, GunFiremode = 2, GunMagId = 9001, GunAttach = 3,
            GunSightId = 146, GunBarrelId = 7, GunGripId = 8, GunTacticalId = 11,
            GunChambered = true, GunAttachSeeded = true,
        };

        static string Ser(WorldSave.JarSave j)
        {
            var w = new WorldSave { MapId = "PEI" };
            w.Containers.Add(new WorldSave.ContainerSave
            {
                Contents = new WorldSave.PageSave { Width = 8, Height = 6, Items = { j } },
            });
            return w.ToJson();
        }

        // The bug itself: a non-gun must not carry ten gun fields. Revert the ShouldSerialize hook and this
        // fails, because DefaultIgnoreCondition.Never writes every one of them.
        [Test]
        public void ANonGunDoesNotWriteGunFields()
        {
            string json = Ser(Tomato());
            foreach (string field in new[] { "GunAmmo", "GunFiremode", "GunMagId", "GunAttach", "GunSightId",
                                             "GunBarrelId", "GunGripId", "GunTacticalId", "GunChambered", "GunAttachSeeded" })
                Assert.That(json, Does.Not.Contain(field), $"a tomato wrote {field}; that field is 93% of a 41 MB save");
            Assert.That(json, Does.Contain("\"Id\""), "the item itself still has to be written");
        }

        // The other half, and the one that would make the fix a data-loss bug: a REAL gun must still round-trip
        // every field. If ShouldSerialize is ever widened to drop them unconditionally, this is what says so.
        [Test]
        public void AGunRoundTripsEveryField()
        {
            var before = Gun();
            Assert.That(WorldSave.TryParse(Ser(before), "PEI", out var save, out string err), Is.True, err);
            var after = save.Containers[0].Contents.Items[0];
            Assert.Multiple(() =>
            {
                Assert.That(after.GunAmmo, Is.EqualTo(before.GunAmmo));
                Assert.That(after.GunFiremode, Is.EqualTo(before.GunFiremode));
                Assert.That(after.GunMagId, Is.EqualTo(before.GunMagId));
                Assert.That(after.GunAttach, Is.EqualTo(before.GunAttach));
                Assert.That(after.GunSightId, Is.EqualTo(before.GunSightId));
                Assert.That(after.GunBarrelId, Is.EqualTo(before.GunBarrelId));
                Assert.That(after.GunGripId, Is.EqualTo(before.GunGripId));
                Assert.That(after.GunTacticalId, Is.EqualTo(before.GunTacticalId));
                Assert.That(after.GunChambered, Is.EqualTo(before.GunChambered));
                Assert.That(after.GunAttachSeeded, Is.EqualTo(before.GunAttachSeeded));
            });
        }

        // An OMITTED field must read back as the sentinel that was omitted. This is the whole safety argument
        // for the change, and it rests on the `= -1` property initialisers rather than on JSON defaults --
        // default(short) is 0, so a reader that fell back to the type default would turn every non-gun into a
        // gun with ammo 0 and mag id 0. Strip the initialisers and this test fails.
        [Test]
        public void AnAbsentGunFieldReadsBackAsTheOmittedSentinel()
        {
            var round = JsonSerializer.Deserialize<WorldSave.JarSave>("{\"X\":1,\"Y\":2,\"Id\":66,\"Amount\":1,\"Quality\":100}");
            Assert.Multiple(() =>
            {
                Assert.That(round.GunAmmo, Is.EqualTo(-1), "absent must mean -1, not default(short)=0");
                Assert.That(round.GunFiremode, Is.EqualTo(-1));
                Assert.That(round.GunMagId, Is.EqualTo(-1));
                Assert.That(round.GunAttach, Is.EqualTo(-1));
                Assert.That(round.GunSightId, Is.EqualTo(-1));
                Assert.That(round.GunBarrelId, Is.EqualTo(-1));
                Assert.That(round.GunGripId, Is.EqualTo(-1));
                Assert.That(round.GunTacticalId, Is.EqualTo(-1));
                Assert.That(round.GunChambered, Is.False);
                Assert.That(round.GunAttachSeeded, Is.False);
            });
        }

        // Backward compatibility, stated as a test rather than as a hope: a save written by the OLD code, with
        // every gun field spelled out as -1, still loads and still means "not a gun".
        [Test]
        public void AnOldSaveWithExplicitSentinelsStillLoads()
        {
            const string old = "{\"Version\":1,\"MapId\":\"PEI\",\"Containers\":[{\"Qx\":1,\"Qy\":2,\"Qz\":3,"
                + "\"Contents\":{\"Width\":8,\"Height\":6,\"Items\":[{\"X\":0,\"Y\":0,\"Rot\":0,\"Id\":66,"
                + "\"Amount\":1,\"Quality\":100,\"GunAmmo\":-1,\"GunFiremode\":-1,\"GunMagId\":-1,\"GunAttach\":-1,"
                + "\"GunSightId\":-1,\"GunBarrelId\":-1,\"GunGripId\":-1,\"GunTacticalId\":-1,"
                + "\"GunChambered\":false,\"GunAttachSeeded\":false}]}}]}";
            Assert.That(WorldSave.TryParse(old, "PEI", out var save, out string err), Is.True, err);
            var it = save.Containers[0].Contents.Items[0];
            Assert.That(it.Id, Is.EqualTo(66));
            Assert.That(it.GunAmmo, Is.EqualTo(-1));
        }

        // The size claim, made falsifiable. Ten of eighteen lines per item is not a rounding error, and a
        // regression that re-adds them would otherwise only show up as a machine mysteriously running at 1 fps
        // several hours later -- which is how it showed up the first time.
        [Test]
        public void ContainerItemsStaySmall()
        {
            int perItem = Ser(Tomato()).Length;
            var w = new WorldSave { MapId = "PEI" };
            var grid = new WorldSave.PageSave { Width = 8, Height = 6 };
            for (int i = 0; i < 100; i++) grid.Items.Add(Tomato());
            w.Containers.Add(new WorldSave.ContainerSave { Contents = grid });
            int per100 = w.ToJson().Length;
            int each = (per100 - perItem) / 99;
            Assert.That(each, Is.LessThan(90),
                $"a stored non-gun costs {each} bytes; it was ~330 when 696 containers made a 41 MB save");
        }
    }
}
