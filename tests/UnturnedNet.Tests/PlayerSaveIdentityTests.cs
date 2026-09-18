using System.Text.Json;
using NUnit.Framework;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// Re-keying player persistence from profile NAME onto a verified SteamID (strawberry: "rekey server
    /// inventories/logout coords etc onto userids too").
    ///
    /// The two failures worth testing are opposite in shape. One is LOUD and enormous: bump the save format
    /// and every existing world stops loading. The other is SILENT and small: keep matching on name first,
    /// and anyone can collect a signed-in player's inventory by typing their name.
    /// </summary>
    public class PlayerSaveIdentityTests
    {
        static WorldSave Parse(string json)
        {
            Assert.That(WorldSave.TryParse(json, null, out var save, out var err), Is.True, $"did not parse: {err}");
            return save;
        }

        /// A v1 file, exactly as the live server has been writing them: no SteamId anywhere.
        const string V1Json = @"{""Version"":1,""MapId"":""PEI"",""Players"":[
            {""Name"":""strawberry"",""X"":10,""Y"":20,""Z"":30,""Experience"":4242},
            {""Name"":""someone_else"",""X"":1,""Y"":2,""Z"":3}]}";

        [Test]
        public void a_v1_save_still_loads_because_refusing_it_would_delete_the_world()
        {
            var save = Parse(V1Json);
            Assert.That(save.Players, Has.Count.EqualTo(2));
            Assert.That(save.FindPlayer("", "strawberry"), Is.Not.Null,
                        "a pre-token save must still find its players by name -- they have no other key");
            Assert.That(save.FindPlayer("", "strawberry").Experience, Is.EqualTo(4242u),
                        "found the block but not the contents");
        }

        [Test]
        public void loading_a_v1_save_migrates_it_forward_rather_than_leaving_it_behind()
        {
            Assert.That(Parse(V1Json).Version, Is.EqualTo(WorldSave.CurrentVersion),
                        "a v1 read back at v1 would be rewritten at v1 forever, so the field never starts being used");
        }

        [Test]
        public void a_genuinely_unknown_format_is_still_refused()
        {
            // The read set is not a licence to read anything. A v99 file means a newer build wrote it, and
            // half-reading it is the failure the version gate exists to prevent.
            Assert.That(WorldSave.TryParse(@"{""Version"":99,""Players"":[]}", null, out _, out var err), Is.False);
            Assert.That(err, Does.Contain("99"));
        }

        [Test]
        public void a_verified_steamid_finds_the_block_and_survives_a_rename()
        {
            var save = Parse(@"{""Version"":2,""Players"":[
                {""Name"":""old_name"",""SteamId"":""76561198012345678"",""Experience"":99}]}");
            var byId = save.FindPlayer("76561198012345678", "a_completely_different_name");
            Assert.That(byId, Is.Not.Null, "a rename lost the save -- which is the entire reason for re-keying");
            Assert.That(byId.Experience, Is.EqualTo(99u));
        }

        /// ⭐ The security-critical one. This is what a name-first lookup gets wrong while still passing every
        /// "the right save comes back" test, because in the happy case both keys point at the same block.
        [Test]
        public void a_claimed_block_cannot_be_taken_by_typing_its_owners_name()
        {
            var save = Parse(@"{""Version"":2,""Players"":[
                {""Name"":""strawberry"",""SteamId"":""76561198012345678"",""Experience"":99}]}");

            Assert.That(save.FindPlayer("", "strawberry"), Is.Null,
                        "an unauthenticated peer typing the name got a signed-in player's inventory");
            Assert.That(save.FindPlayer("76561190000000000", "strawberry"), Is.Null,
                        "a DIFFERENT verified account got it by name -- the fallback is firing when it must not");
            Assert.That(save.FindPlayer("76561198012345678", "strawberry"), Is.Not.Null,
                        "and the real owner must still get it");
        }

        [Test]
        public void an_unsigned_player_and_a_signed_one_with_the_same_name_do_not_collide()
        {
            var save = Parse(@"{""Version"":2,""Players"":[
                {""Name"":""dave"",""SteamId"":""76561198012345678"",""Experience"":100},
                {""Name"":""dave"",""SteamId"":"""",""Experience"":7}]}");
            Assert.That(save.FindPlayer("76561198012345678", "dave").Experience, Is.EqualTo(100u));
            Assert.That(save.FindPlayer("", "dave").Experience, Is.EqualTo(7u),
                        "the unsigned dave must land on the unsigned block, not the claimed one");
        }

        [Test]
        public void an_absent_player_is_absent_rather_than_matching_the_first_row()
        {
            var save = Parse(V1Json);
            Assert.That(save.FindPlayer("", "nobody_by_that_name"), Is.Null);
            Assert.That(save.FindPlayer("76561198099999999", ""), Is.Null);
            Assert.That(save.FindPlayer("", ""), Is.Null);
        }

        [Test]
        public void the_steamid_round_trips_through_a_save_and_reload()
        {
            var save = Parse(@"{""Version"":2,""Players"":[{""Name"":""x"",""SteamId"":""76561198012345678""}]}");
            var again = Parse(JsonSerializer.Serialize(save));
            Assert.That(again.FindPlayer("76561198012345678", null), Is.Not.Null,
                        "the field serialises out but does not come back -- it would silently reset every save cycle");
        }
    }
}
