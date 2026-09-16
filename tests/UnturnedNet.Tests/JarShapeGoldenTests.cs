using System.Reflection;
using NUnit.Framework;
using SDG.NetPak;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// A BYTE-SHAPE pin for the inventory jar schema (InventoryReplication.WriteJar/ReadJar).
    ///
    /// ⭐ WHY THIS IS NOT A DUPLICATE OF ItemWireCompletenessTests. Completeness and shape are DISJOINT
    /// guarantees, and the jar had only the first:
    ///   - COMPLETENESS pins WHAT IS CARRIED. `every_item_field_survives_a_real_round_trip` reflects over
    ///     Item's fields and requires each to survive a real server->client trip. Drop a field and it fails.
    ///     Strictly stronger than anything here, for that question.
    ///   - SHAPE pins WHERE IT SITS. Reorder two same-typed fields and the writer and reader move TOGETHER,
    ///     so the round trip still passes -- while a peer on the old build reads them swapped. Self-consistency
    ///     proves nothing about cross-version compatibility, which is the only thing a wire schema is for.
    ///
    /// The live instance of that in this very schema: `gunChambered` and `gunAttachSeeded` are two consecutive
    /// WriteBit calls (InventoryReplication.cs:1179-1180). Swap them and every round-trip test in the suite
    /// stays green while a v52 peer reads a chambered round as the attachment-seeded flag. An identical bool
    /// blindness was found the same night in the new WireShapeGoldenTests fixture builder, by an opus review
    /// that proved it with 469 tests green over a deliberately broken GunStateCommand.
    ///
    /// ⚠ THE FIXTURE RULE IS THE WHOLE TRICK, and it is why two bools here are set OPPOSITE. Equal values
    /// are exactly what makes a swap invisible: if both bits are true, exchanging them changes no byte and
    /// the pin is decorative. Every field below therefore carries a DISTINCT non-default value.
    ///
    /// Design by cow tools (2026-09-16), who worked out what the pin has to reject, found that the only part
    /// which is not already-covered duplicate work is the part requiring a run, and declined to write it blind
    /// rather than ship an instrument that cannot move. This is that test, executed.
    ///
    /// The goldens below are a CAPTURE, not a derivation: they pin behaviour ("this is what it emitted, tell
    /// me when that stops being true"), which is the guarantee that catches an unbumped schema change. The
    /// LENGTH assertions beside them are derived -- the bit accounting is in the comments, and it is what
    /// stops a re-blessed capture from silently absorbing a real shape change.
    /// </summary>
    [TestFixture]
    public class JarShapeGoldenTests
    {
        // WriteJar is private static; a golden needs the raw bytes, not a round trip through a live harness.
        static readonly MethodInfo WriteJarM = typeof(InventoryReplication)
            .GetMethod("WriteJar", BindingFlags.NonPublic | BindingFlags.Static);

        static string Hex(ItemJar j)
        {
            Assert.That(WriteJarM, Is.Not.Null, "WriteJar moved or was renamed -- this golden cannot see the schema any more");
            // Through NetMessagePak.Pack, the same path every other golden in this directory uses -- it owns
            // the buffer and the flush. Id 0 is a placeholder and is stripped below, so the pin is the JAR's
            // bytes rather than any message's framing.
            byte[] packed = NetMessagePak.Pack(0, w => WriteJarM.Invoke(null, new object[] { w, j }));
            var sb = new System.Text.StringBuilder((packed.Length - 1) * 2);
            for (int i = 1; i < packed.Length; i++) sb.Append(packed[i].ToString("X2"));
            return sb.ToString();
        }

        /// <summary>Every gate ON and every field distinct: the whole schema on the wire at once.</summary>
        static ItemJar FullJar()
        {
            var it = new Item(1234)
            {
                amount = 0x1122, quality = 0x33,
                gunAmmo = 0x0455, gunFiremode = 0x06, gunMagId = 0x07777777, gunAttach = 0x08888888,
                gunSightId = 0x09999999, gunBarrelId = 0x0AAAAAAA, gunGripId = 0x0BBBBBBB, gunTacticalId = 0x0CCCCCCC,
                // ⚠ DELIBERATELY OPPOSITE. Equal bools are what made the swap invisible in the other golden.
                gunChambered = true, gunAttachSeeded = false,
                fuelLevel = 12.25f,
                fluidType = 0x0D, fluidAmount = 345.5f, fluidQuality = 0x0E,
                autoDrink = false,                      // non-default: the field's default is true
                cooked = 0x0F, cookStyle = 0x10,        // opens the cook gate
                frozen = 0x11,                          // opens the froz gate
            };
            return new ItemJar(0x01, 0x02, 0x03, it);
        }

        /// <summary>Every gate OFF: the short frame a bandage actually costs.</summary>
        static ItemJar PlainJar() =>
            new ItemJar(0x04, 0x05, 0x06, new Item(4321) { amount = 1, quality = 0x77 });

        [Test]
        public void the_full_jar_schema_has_not_moved()
        {
            // MUTATION RUN, NOT ASSERTED (2026-09-16). Swapped the two WriteBit calls at
            // InventoryReplication.cs:1179-1180 and re-ran: this test went RED on one byte, 62C0 -> 64C0,
            // which is exactly those two bits and nothing else. ItemWireCompletenessTests stayed GREEN
            // across the same swap -- so the disjointness this fixture's header argues for is measured
            // here, not merely reasoned. Same applies to reordering any other same-typed pair (x/y/rot,
            // the four attachment ids, fluidType/fluidQuality); all of them survive a round trip.
            Assert.That(Hex(FullJar()), Is.EqualTo(GoldenFull),
                "the jar's byte layout changed. If intended: this schema is READ BY PEERS ON OLDER BUILDS, so it "
                + "needs a NetProtocol.Version bump and a version-history line, not just a re-blessed golden.");
        }

        [Test]
        public void the_gateless_jar_schema_has_not_moved()
        {
            Assert.That(Hex(PlainJar()), Is.EqualTo(GoldenPlain),
                "the jar's SHORT form changed -- this is what every bandage and bullet in the game costs.");
        }

        // ---- the derived half. A capture can absorb a shape change when someone re-blesses it; these cannot,
        // because they are computed from the schema rather than from the output.

        [Test]
        public void the_gates_are_what_separates_the_two_forms()
        {
            int full = Hex(FullJar()).Length / 2, plain = Hex(PlainJar()).Length / 2;
            // att opens 4 int32 + 2 bits, cook opens 2 bytes, froz opens 1 byte -> 19 bytes and 2 bits more,
            // which is 21 or 22 bytes once the shared bit budget rounds. Asserting the DIFFERENCE, not the
            // totals, so this survives an unrelated field being added to the common part.
            Assert.That(full - plain, Is.InRange(19, 22),
                $"the gated blocks should cost ~19-22 bytes (full {full}, plain {plain}). If this moved, a gate "
                + "changed what it guards -- check the att/cook/froz blocks against ReadJar's.");
        }

        [Test]
        public void a_bandage_pays_only_the_bits_for_gates_it_does_not_use()
        {
            // The gating argument in WriteJar's comments is a COST claim ("one bit for every bandage and
            // bullet, 17 for a steak"). This is that claim as arithmetic, ADDED UP FROM THE SCHEMA rather
            // than copied off a run -- so re-blessing the captures above cannot quietly move it.
            //
            //   whole bytes, unconditional:  x1 y1 rot1 id2 amount2 quality1
            //                                gunAmmo2 gunFiremode1 gunMagId4 gunAttach4        = 19 bytes
            //   bits, unconditional:         att gate 1, fuelLevel (12,2) 14, fluidType 8,
            //                                fluidAmount (20,1) 21, fluidQuality 8, autoDrink 1,
            //                                magLoadedRound 8, cook gate 1, froz gate 1        = 63 bits
            //   63 bits flushes to 8 bytes                                        -> 19 + 8   = 27 bytes
            //
            // So the three gates really do cost a bandage 3 BITS and no byte: 63 bits is 1 short of the
            // 64 that would need a ninth byte, and the gates are the last three of them.
            //
            // Un-gating the cook block (`bool cook = j.item != null`) was run as the mutation for this
            // one: it read 29, the +2 the arithmetic predicts, and `the_gates_are_what_separates_the_two_forms`
            // independently fell to 18. Two instruments, two frames, one cause.
            //
            // ⚠ 27 IS DERIVED, NOT MEASURED. It was 24 when this test was written -- a number that felt
            // right and had no accounting behind it -- and the run said 27. The fix is to redo the sum,
            // never to relax the bound until it passes, because a bound fitted to the output asserts
            // nothing about the schema. If this fails, add up WriteJar again and change BOTH the
            // arithmetic above and the number below; if the sum still says 27, the schema grew.
            Assert.That(Hex(PlainJar()).Length / 2, Is.EqualTo(27),
                "the un-gated jar's size left the accounting above. Un-gating the att block costs a bandage "
                + "16 bytes, cooking 2, freezing 1 -- the schema's own comments justify all three gates on "
                + "the grounds that the common case is a bandage, so if that case is paying for guns, "
                + "cooking or freezing, the trade the comments argue for no longer holds.");
        }

        [Test]
        public void the_two_gun_bits_are_distinguishable_in_the_fixture()
        {
            // The guard on the guard. If a future edit makes these equal "for tidiness", the full-jar golden
            // silently stops being able to see a swap, and nothing else would tell us.
            var j = FullJar();
            Assert.That(j.item.gunChambered, Is.Not.EqualTo(j.item.gunAttachSeeded),
                "FullJar's two gun bools must stay OPPOSITE -- equal values make a swapped pair invisible, "
                + "which is the exact hole this golden exists to close.");
        }

        // Captured 2026-09-16 at NetProtocol.Version 52 from the fixtures above.
        const string GoldenFull = "010203D20422113355040677777707888888083333331354555515767777179899991962C01AB202B003800F102300";
        const string GoldenPlain = "040506E110010077FFFFFFFFFFFFFFFFFFFFFF001080FFFF031000";
    }
}
