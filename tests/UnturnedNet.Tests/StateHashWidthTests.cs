using System.Reflection;
using NUnit.Framework;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// The StateHash mixers carry NO golden and NO pinned literal -- a grep of the whole suite for a hash
    /// constant finds nothing. Every StateHash assertion in the repo is same-build PARITY
    /// (`client.X.StateHash() == server.X.StateHash()`), which is the self-agreeing shape: change a mixer's
    /// field order, field set, or mix WIDTH and both sides rebuild together, parity holds, suite green.
    ///
    /// ⚠ THIS IS NOT HYPOTHETICAL. The v50 version-history entry records exactly this going wrong -- "three
    /// StateHash mixers went MixByte -> MixUInt32 ... it disagrees about its own input". That change was
    /// survivable only because someone noticed by hand; nothing in the suite could have.
    ///
    /// WHY THERE IS NO HASH LITERAL HERE EITHER. A pinned hash value would be re-blessed on every legitimate
    /// field addition and would say nothing about WHY it moved. This pins the PROPERTY that a truncating
    /// mixer would destroy, using a value pair chosen so the arithmetic does the work:
    ///
    ///     amount 44 and amount 300 are the SAME BYTE (300 &amp; 0xFF == 44) and different uint32s.
    ///
    /// So under MixByte the two inventories hash IDENTICALLY and under MixUInt32 they do not. No constant to
    /// re-bless, no dependence on the rest of the fold, and it fails with a message naming the cause.
    /// (Found by a peer session's read-only wire sweep, 2026-09-16, which verified that no test would go red
    /// if `InventoryReplication.cs:1328` were reverted to MixByte. I re-verified that before writing this.)
    /// </summary>
    [TestFixture]
    public class StateHashWidthTests
    {
        // The pair the whole instrument rests on: same low byte, different uint32. Declared ONCE and used
        // by both tests, so the guard below genuinely guards the values in use rather than a copy of them.
        const ushort Narrow = 44;
        const ushort Wide = 300;      // 300 & 0xFF == 44

        static readonly MethodInfo MixEntryM = typeof(InventoryReplication)
            .GetMethod("MixEntry", BindingFlags.NonPublic | BindingFlags.Static);

        static object EntryWithAmount(ushort amount)
        {
            var entryType = typeof(InventoryReplication).GetNestedType("PlayerEntry");
            var e = System.Activator.CreateInstance(entryType);
            entryType.GetField("OwnerPlayerId").SetValue(e, (ushort)7);
            var inv = (PlayerInventory)entryType.GetField("Inventory").GetValue(e);
            inv.items[0].addItem(0, 0, 0, new Item(1234, (byte)1) { amount = amount, quality = 50 });
            return e;
        }

        static ulong Mix(ushort amount) =>
            (ulong)MixEntryM.Invoke(null, new object[] { NetHash.FnvOffset, EntryWithAmount(amount) });

        [Test]
        public void the_inventory_mixer_folds_amount_at_full_width_not_one_byte()
        {
            Assert.That(MixEntryM, Is.Not.Null, "MixEntry moved or was renamed -- this pin cannot see the mixer any more");
            // MUTATION that must make this fail: InventoryReplication.cs:1328,
            //   h = NetHash.MixUInt32(h, j.item?.amount ?? (ushort)0);  ->  NetHash.MixByte(...)
            // Every StateHash parity assertion in the suite stays green through that edit, because both
            // sides compute the narrowed hash and agree with each other about it.
            Assert.That(Mix(Wide), Is.Not.EqualTo(Mix(Narrow)),
                "a stack of 300 and a stack of 44 hash the SAME, so the inventory mixer is folding `amount` "
                + "through a byte-wide mix and silently discarding the high bits. Two inventories that differ "
                + "by 256 in a stack size are then indistinguishable to the desync detector, which is the v50 "
                + "failure (see the version history) repeated. Fix the mixer, not this test.");
        }

        [Test]
        public void the_fixture_pair_is_actually_a_byte_collision()
        {
            // The guard on the guard: if someone "tidies" 300 to another value, the test above keeps passing
            // for the wrong reason -- almost any two amounts differ under BOTH mixers -- and silently stops
            // testing width at all. The whole instrument rests on these two sharing a low byte.
            Assert.That(unchecked((byte)Wide), Is.EqualTo(unchecked((byte)Narrow)),
                "the 44/300 pair must stay a low-byte collision. Without that, this fixture cannot tell a "
                + "byte-wide mixer from a full-width one and the test above becomes decorative.");
        }
    }
}
