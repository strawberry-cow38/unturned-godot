using System.Collections.Generic;
using NUnit.Framework;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // THE CACHE THAT IS ALSO THE WIRE.
    //
    // Nine replication call sites asked for an ascending id list several times per tick, and each call built a
    // fresh List<uint> and sorted it -- ~50% of the whole game's allocations in an ETW capture, for a set that
    // usually has not changed since the previous tick. The cache now lives in NetEntityRegistry because Add,
    // Remove and Clear are the only ways the key set can move, so invalidation cannot be forgotten at a call
    // site the way it could if each Replication class owned its own flag.
    //
    // That placement matters more than performance: this order IS THE WIRE. WriteFull writes the count then the
    // entities in this order, and WriteDelta walks the same order to build changed/removed. A stale list is not
    // a slow frame, it is a desync -- an entity that silently never makes the packet, or deltas keyed against an
    // order the other end does not share. So every test here fails if the cache is WRONG, not merely absent.
    [TestFixture]
    public class SortedIdCacheTests
    {
        static NetEntityRegistry<string> Reg(params uint[] ids)
        {
            var r = new NetEntityRegistry<string>();
            foreach (uint id in ids) r.Add(new NetId(id), "e" + id);
            return r;
        }

        [Test]
        public void IdsComeBackAscendingRegardlessOfInsertionOrder()
        {
            Assert.That(Reg(9, 2, 7, 1).SortedIdValues(), Is.EqualTo(new List<uint> { 1, 2, 7, 9 }));
        }

        // Invalidation on ADD. Without it a spawned entity never reaches the packet at all.
        [Test]
        public void AddingAnEntityShowsUpImmediately()
        {
            var r = Reg(1, 2);
            Assert.That(r.SortedIdValues(), Is.EqualTo(new List<uint> { 1, 2 }));   // prime the cache
            r.Add(new NetId(3), "e3");
            Assert.That(r.SortedIdValues(), Is.EqualTo(new List<uint> { 1, 2, 3 }), "a spawn must invalidate the cache");
        }

        // Invalidation on REMOVE -- the despawn/disconnect path, the one easiest to forget.
        [Test]
        public void RemovingAnEntityShowsUpImmediately()
        {
            var r = Reg(1, 2, 3);
            Assert.That(r.SortedIdValues().Count, Is.EqualTo(3));   // prime
            r.Remove(new NetId(2));
            Assert.That(r.SortedIdValues(), Is.EqualTo(new List<uint> { 1, 3 }), "a despawn must invalidate the cache");
        }

        [Test]
        public void ClearShowsUpImmediately()
        {
            var r = Reg(4, 5);
            Assert.That(r.SortedIdValues().Count, Is.EqualTo(2));   // prime
            r.Clear();
            Assert.That(r.SortedIdValues(), Is.Empty, "Clear must invalidate the cache");
        }

        // A Remove that removed nothing must not be treated as a change -- and must not corrupt the answer.
        [Test]
        public void RemovingAnAbsentIdLeavesTheListCorrect()
        {
            var r = Reg(1, 2);
            Assert.That(r.SortedIdValues(), Is.EqualTo(new List<uint> { 1, 2 }));
            Assert.That(r.Remove(new NetId(99)), Is.False);
            Assert.That(r.SortedIdValues(), Is.EqualTo(new List<uint> { 1, 2 }));
        }

        // Overwriting an existing id changes the entity but NOT the key set, so the order must stay correct.
        [Test]
        public void OverwritingAnExistingIdKeepsTheOrderCorrect()
        {
            var r = Reg(1, 2, 3);
            Assert.That(r.SortedIdValues(), Is.EqualTo(new List<uint> { 1, 2, 3 }));
            r.Add(new NetId(2), "replaced");
            Assert.That(r.SortedIdValues(), Is.EqualTo(new List<uint> { 1, 2, 3 }));
            Assert.That(r.TryGet(new NetId(2), out string e), Is.True);
            Assert.That(e, Is.EqualTo("replaced"));
        }

        // COPY-ON-WRITE, and this is the one that makes the cache safe rather than merely fast.
        // ContainerReplication.All is an iterator that yields to its consumer mid-walk, so a caller can still be
        // enumerating the previous list when an entity spawns. If a rebuild recycled one list -- Clear() and
        // refill -- that walk would be corrupted under it. A held reference must therefore be immune to a later
        // mutation. Change SortedIdValues to reuse its list and this test fails; nothing else here would.
        [Test]
        public void AListHandedOutEarlierIsNotMutatedByALaterChange()
        {
            var r = Reg(1, 2, 3);
            var held = r.SortedIdValues();
            Assert.That(held, Is.EqualTo(new List<uint> { 1, 2, 3 }));

            r.Add(new NetId(4), "e4");
            r.Remove(new NetId(1));
            var fresh = r.SortedIdValues();

            Assert.That(held, Is.EqualTo(new List<uint> { 1, 2, 3 }),
                "a consumer mid-enumeration must keep the list it started with");
            Assert.That(fresh, Is.EqualTo(new List<uint> { 2, 3, 4 }));
            Assert.That(ReferenceEquals(held, fresh), Is.False, "a rebuild must allocate a new list, not recycle");
        }

        // The cache must actually cache, or the allocation win is imaginary. Same instance back when nothing moved.
        [Test]
        public void AnUnchangedRegistryReturnsTheSameInstance()
        {
            var r = Reg(1, 2, 3);
            Assert.That(ReferenceEquals(r.SortedIdValues(), r.SortedIdValues()), Is.True,
                "an unchanged key set must not rebuild -- that rebuild was ~50% of the game's allocations");
        }
    }
}
