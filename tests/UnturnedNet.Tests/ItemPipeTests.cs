using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // INDUSTRIAL ITEM PIPES (strawberry 2026-10-06). Every rule in her request has a test here that has been
    // seen to FAIL with the rule broken -- the teeth table is in the commit that added them.
    //
    // The headline numbers are hers, verbatim: "32 stack quantity per second (so 32 stacks of 1, or a single
    // stack of 32 item, or a mix of 14 in a single stack and 18 stacks of 1)". Each is a test, and each
    // asserts the count at 49 ticks as well as at 50, because "32 after a second" is also what a mover that
    // moved everything on the first tick would show -- the 49-tick reading is the one that says it is a RATE.
    //
    // Most of this runs on a bare NetWorldServer (no clients): the mover is server-only, and its behaviour is
    // a property of the server's graph and crates. The wire half (commands, events, the snapshot a late joiner
    // gets) runs on the TransactionalHarness, through the real command choke point.

    static class PipeFixtures
    {
        // ids mirror the game's DeployableDef ids so a failure reads like the game
        public const ushort ADAPTER = 9216, SPLITTER = 9217, COMBINER = 9218, MOVER = 9219;
        public const ushort GEN = TransactionalFixtures.GeneratorId;
        public const ushort SINGLE = TransactionalFixtures.ScrapId;   // 1x1, stackSize 1 -- a "stack of 1"
        public const ushort NAILS = 930;                              // 1x1, stackSize 64 -- something that stacks
        public const ushort MAG = TransactionalFixtures.StanagId;     // 1x2 magazine: amount = ROUNDS, never split

        const byte In = (byte)ItemPortDir.In, Out = (byte)ItemPortDir.Out;

        public static void RegisterAssets()
        {
            TransactionalFixtures.RegisterAssets();
            Assets.add(new ItemAsset { id = NAILS, itemName = "Nails", size_x = 1, size_y = 1, stackSize = 64 });
            // the devices as ITEMS, so a test can grant one and place it through the real command path
            foreach (var (id, name) in new[] { (ADAPTER, "Storage Adapter"), (SPLITTER, "Item Splitter"), (COMBINER, "Item Combiner"), (MOVER, "Item Mover") })
                Assets.add(new ItemAsset { id = id, itemName = name, size_x = 1, size_y = 1 });
        }

        public static void Register(DeployableSchema s)
        {
            s.Register(new DeployableNetDef { DefId = ADAPTER, Health = 150f, Range = 4f, ItemDevice = ItemDeviceKind.Adapter, ItemPorts = new[] { In, Out } });
            s.Register(new DeployableNetDef { DefId = SPLITTER, Health = 200f, Range = 4f, ItemDevice = ItemDeviceKind.Splitter, ItemPorts = new[] { In, Out, Out, Out } });
            s.Register(new DeployableNetDef { DefId = COMBINER, Health = 200f, Range = 4f, ItemDevice = ItemDeviceKind.Combiner, ItemPorts = new[] { In, In, In, Out } });
            s.Register(new DeployableNetDef
            {
                DefId = MOVER, Health = 250f, Range = 4f, ItemDevice = ItemDeviceKind.Mover, ItemPorts = new[] { In, Out },
                // "power i/o input, passthrough" -- 100 W ("100w fine")
                Ports = new[]
                {
                    new DeployablePortSpec { Kind = (byte)PowerPortKind.Consumer, Watts = 100f },
                    new DeployablePortSpec { Kind = (byte)PowerPortKind.Passthrough, Watts = 0f },
                },
            });
        }
    }

    /// <summary>A server with no clients: place, pipe, power, step.</summary>
    sealed class PipeRig
    {
        public readonly NetWorldServer S;
        public PipeRig(int seed = 1)
        {
            S = new NetWorldServer(new MemServerTransport(new MemNetwork(seed)));
            TransactionalFixtures.RegisterSchema(S.Deployables.Schema);
            PipeFixtures.Register(S.Deployables.Schema);
        }

        public uint Place(ushort def, Vector3 pos) => S.Deployables.ServerPlace(S.Ids.Mint(), def, 0, pos, 0f, 0).NetIdValue;
        public DeployableReplication.DeployableEntity E(uint id) { S.Deployables.TryGet(id, out var e); return e; }

        public InventoryReplication.CrateEntry Crate(Vector3 pos, byte w = 5, byte h = 4)
            => S.Inventories.ServerRegisterCrate(S.Ids.Mint(), w, h, pos);

        /// <summary>An adapter bolted to this crate (placed beside it, bound the way OnPlaceDeployable binds).</summary>
        public uint Adapter(InventoryReplication.CrateEntry c)
        {
            uint id = Place(PipeFixtures.ADAPTER, c.Pos + new Vector3(0.6f, 0f, 0f));
            E(id).ItemCrateId = ServerItemMovers.FindCrateFor(S.Inventories, E(id).Pos, c.NetIdValue);
            Assert.That(E(id).ItemCrateId, Is.EqualTo(c.NetIdValue), "the adapter bound its crate");
            return id;
        }

        public uint Pipe(uint src, byte srcPort, uint dst, byte dstPort)
            => S.Deployables.ServerConnectPipe(S.Ids.Mint(), src, srcPort, dst, dstPort, System.Array.Empty<Vector3>(), 0).NetIdValue;

        /// <summary>A mover with a running generator wired into its power input.</summary>
        public uint PoweredMover(Vector3 pos)
        {
            uint mover = Place(PipeFixtures.MOVER, pos);
            uint gen = Place(PipeFixtures.GEN, pos + new Vector3(0f, 0f, 3f));
            S.Deployables.ServerToggle(gen, true, 0);
            S.Deployables.ServerConnectWire(S.Ids.Mint(), gen, 0, mover, 0, 0);
            return mover;
        }

        /// <summary>crate A -> adapter -> mover -> adapter -> crate B, the simplest line. Both 8x6 unless told.</summary>
        public (InventoryReplication.CrateEntry a, InventoryReplication.CrateEntry b, uint mover) Line(byte bw = 8, byte bh = 6, bool powered = true)
        {
            var a = Crate(new Vector3(0f, 0f, 0f), 8, 6);
            var b = Crate(new Vector3(10f, 0f, 0f), bw, bh);
            uint aa = Adapter(a), ab = Adapter(b);
            uint mover = powered ? PoweredMover(new Vector3(5f, 0f, 0f)) : Place(PipeFixtures.MOVER, new Vector3(5f, 0f, 0f));
            Pipe(aa, 1, mover, 0);   // adapter OUT -> mover IN
            Pipe(mover, 1, ab, 0);   // mover OUT -> adapter IN
            return (a, b, mover);
        }

        public void Run(int steps) { for (int i = 0; i < steps; i++) S.ItemMovers.Step(0.02f); }

        public static int Units(Items page, ushort id)
        {
            int n = 0;
            for (byte i = 0; i < page.getItemCount(); i++)
            {
                var j = page.getItem(i);
                if (j?.item != null && j.item.id == id) n += ItemTransfer.UnitsOf(j.item);
            }
            return n;
        }
        public static int Units(Items page) { int n = 0; for (byte i = 0; i < page.getItemCount(); i++) n += ItemTransfer.UnitsOf(page.getItem(i).item); return n; }

        public void Fill(InventoryReplication.CrateEntry c, ushort id, int jars, ushort amount = 1)
        {
            for (int i = 0; i < jars; i++) Assert.That(c.Storage.tryAddItem(new Item(id, amount)), Is.True, "fixture fill fits");
        }
    }

    [TestFixture]
    public class ItemPipeConnectRuleTests
    {
        [SetUp] public void SetUp() => PipeFixtures.RegisterAssets();

        // two adapters 6 m apart, the sender beside the destination
        static (PipeRig r, uint src, uint dst) Pair()
        {
            var r = new PipeRig();
            uint src = r.Place(PipeFixtures.ADAPTER, new Vector3(0f, 0f, 0f));
            uint dst = r.Place(PipeFixtures.ADAPTER, new Vector3(6f, 0f, 0f));
            return (r, src, dst);
        }

        static readonly Vector3[] NoNodes = System.Array.Empty<Vector3>();
        static readonly Vector3 AtDst = new Vector3(6f, 0f, 1f);

        [Test]
        public void out_to_in_is_the_only_direction()
        {
            var (r, src, dst) = Pair();
            var d = r.S.Deployables;
            Assert.That(d.CanConnectPipe(src, 1, dst, 0, NoNodes, AtDst), Is.True, "CONTROL: Out(1) -> In(0) connects");
            Assert.That(d.CanConnectPipe(src, 0, dst, 0, NoNodes, AtDst), Is.False, "In -> In refused");
            Assert.That(d.CanConnectPipe(src, 1, dst, 1, NoNodes, AtDst), Is.False, "Out -> Out refused");
            Assert.That(d.CanConnectPipe(src, 0, dst, 1, NoNodes, AtDst), Is.False, "In -> Out (backwards) refused");
            Assert.That(d.CanConnectPipe(src, 7, dst, 0, NoNodes, AtDst), Is.False, "a port the def does not have is refused");
        }

        [Test]
        public void a_device_cannot_be_piped_into_itself()
        {
            var (r, src, _) = Pair();
            Assert.That(r.S.Deployables.CanConnectPipe(src, 1, src, 0, NoNodes, new Vector3(0f, 0f, 1f)), Is.False);
        }

        [Test]
        public void one_pipe_per_port_at_both_ends()
        {
            var (r, src, dst) = Pair();
            uint third = r.Place(PipeFixtures.ADAPTER, new Vector3(6f, 0f, 3f));
            var d = r.S.Deployables;
            Assert.That(d.CanConnectPipe(src, 1, dst, 0, NoNodes, AtDst), Is.True);
            r.Pipe(src, 1, dst, 0);
            Assert.That(d.CanConnectPipe(src, 1, third, 0, NoNodes, AtDst), Is.False, "the Out port already carries a pipe");
            Assert.That(d.CanConnectPipe(third, 1, dst, 0, NoNodes, AtDst), Is.False, "the In port already carries a pipe");
            Assert.That(d.CanConnectPipe(third, 1, src, 0, NoNodes, AtDst), Is.True, "CONTROL: free ports on the same devices still connect");
        }

        [Test]
        public void reach_is_measured_to_the_nearer_end()
        {
            var (r, src, dst) = Pair();
            var d = r.S.Deployables;
            Assert.That(d.CanConnectPipe(src, 1, dst, 0, NoNodes, new Vector3(6f, 0f, 15f)), Is.True, "15 m from the destination: in reach");
            Assert.That(d.CanConnectPipe(src, 1, dst, 0, NoNodes, new Vector3(0f, 0f, -15f)), Is.True, "15 m from the source: in reach");
            Assert.That(d.CanConnectPipe(src, 1, dst, 0, NoNodes, new Vector3(3f, 0f, 30f)), Is.False, "30 m from both: refused");
        }

        [Test]
        public void at_most_twenty_route_nodes()
        {
            var (r, src, dst) = Pair();
            var twenty = Enumerable.Range(0, ItemPipeRules.MaxNodes).Select(i => new Vector3(i * 0.3f, 0.5f, 0f)).ToArray();
            var twentyOne = Enumerable.Range(0, ItemPipeRules.MaxNodes + 1).Select(i => new Vector3(i * 0.28f, 0.5f, 0f)).ToArray();
            Assert.That(r.S.Deployables.CanConnectPipe(src, 1, dst, 0, twenty, AtDst), Is.True, "20 nodes connect");
            Assert.That(r.S.Deployables.CanConnectPipe(src, 1, dst, 0, twentyOne, AtDst), Is.False, "21 nodes are refused");
        }

        [Test]
        public void at_most_forty_metres_of_pipe()
        {
            var (r, src, dst) = Pair();
            // a detour out and back: 0 -> (0,0,18) -> (6,0,18) -> 6 = 18 + 6 + 18 = 42 m (inside 40 + slack 2)
            var ok = new[] { new Vector3(0f, 0f, 17f), new Vector3(6f, 0f, 17f) };       // 17 + 6 + 17 = 40
            var tooLong = new[] { new Vector3(0f, 0f, 22f), new Vector3(6f, 0f, 22f) };  // 22 + 6 + 22 = 50
            Assert.That(r.S.Deployables.CanConnectPipe(src, 1, dst, 0, ok, AtDst), Is.True, "40 m connects");
            Assert.That(r.S.Deployables.CanConnectPipe(src, 1, dst, 0, tooLong, AtDst), Is.False, "50 m is refused");
            Assert.That(r.S.Deployables.CanConnectPipe(src, 1, dst, 0, new[] { new Vector3(float.NaN, 0f, 0f) }, AtDst), Is.False,
                        "a NaN node is refused (it would make the length check NaN, which compares false and passes)");
        }

        [Test]
        public void a_burning_device_takes_no_pipe()
        {
            var (r, src, dst) = Pair();
            r.S.Deployables.ServerSetScalars(dst, 10f, 0f, true, 0);
            Assert.That(r.S.Deployables.CanConnectPipe(src, 1, dst, 0, NoNodes, AtDst), Is.False);
        }

        [Test]
        public void adapter_binding_names_a_container_or_takes_the_nearest_in_reach()
        {
            var r = new PipeRig();
            var near = r.Crate(new Vector3(0f, 0f, 0f));
            var far = r.Crate(new Vector3(3f, 0f, 0f));
            var inv = r.S.Inventories;
            var at = new Vector3(1f, 0f, 0f);
            Assert.That(ServerItemMovers.FindCrateFor(inv, at), Is.EqualTo(near.NetIdValue), "unnamed: the nearest");
            Assert.That(ServerItemMovers.FindCrateFor(inv, at, far.NetIdValue), Is.EqualTo(far.NetIdValue),
                        "NAMED wins over nearer -- the box the ghost snapped to, not the box whose origin is closest");
            Assert.That(ServerItemMovers.FindCrateFor(inv, new Vector3(40f, 0f, 0f)), Is.EqualTo(0u), "nothing in reach: unbound");
            Assert.That(ServerItemMovers.FindCrateFor(inv, new Vector3(40f, 0f, 0f), near.NetIdValue), Is.EqualTo(0u),
                        "a named container out of reach is REFUSED, not swapped for a guess");
        }
    }

    [TestFixture]
    public class ItemMoverTests
    {
        [SetUp] public void SetUp() => PipeFixtures.RegisterAssets();

        // ---- strawberry's three examples, verbatim ----

        [Test]
        public void thirty_two_stacks_of_one_move_in_one_second()
        {
            var r = new PipeRig();
            var (a, b, _) = r.Line();
            r.Fill(a, PipeFixtures.SINGLE, 40);
            Assert.That(PipeRig.Units(a.Storage), Is.EqualTo(40), "fixture: 40 singles waiting");
            r.Run(49);
            Assert.That(PipeRig.Units(b.Storage), Is.EqualTo(31), "after 49 ticks: 31 -- it is a RATE, not a dump");
            r.Run(1);
            Assert.That(PipeRig.Units(b.Storage), Is.EqualTo(32), "after 50 ticks (1 s): exactly 32 stacks of 1");
            Assert.That(PipeRig.Units(a.Storage) + PipeRig.Units(b.Storage), Is.EqualTo(40), "nothing minted or lost");
        }

        [Test]
        public void a_single_stack_of_thirty_two_moves_in_one_second()
        {
            var r = new PipeRig();
            var (a, b, _) = r.Line();
            Assert.That(a.Storage.tryAddItem(new Item(PipeFixtures.NAILS, 40)), Is.True);
            r.Run(49);
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.NAILS), Is.EqualTo(31));
            r.Run(1);
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.NAILS), Is.EqualTo(32), "32 of the stack moved in one second");
            Assert.That(b.Storage.getItemCount(), Is.EqualTo(1), "...and it arrived as ONE stack (each unit merged into the last)");
            Assert.That(PipeRig.Units(a.Storage, PipeFixtures.NAILS), Is.EqualTo(8), "8 left behind in the source stack");
        }

        [Test]
        public void fourteen_in_a_stack_and_eighteen_singles_move_in_one_second()
        {
            var r = new PipeRig();
            var (a, b, _) = r.Line();
            Assert.That(a.Storage.tryAddItem(new Item(PipeFixtures.NAILS, 14)), Is.True);
            r.Fill(a, PipeFixtures.SINGLE, 18);
            r.Run(49);
            Assert.That(PipeRig.Units(b.Storage), Is.EqualTo(31), "after 49 ticks: one unit short");
            r.Run(1);
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.NAILS), Is.EqualTo(14), "the whole 14-stack");
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.SINGLE), Is.EqualTo(18), "and all 18 singles");
            Assert.That(PipeRig.Units(a.Storage), Is.EqualTo(0), "the source is empty: 14 + 18 = 32 = one second");
        }

        [Test]
        public void the_rate_is_configurable_five_per_second_moves_five()
        {
            var r = new PipeRig();
            var (a, b, mover) = r.Line();
            r.Fill(a, PipeFixtures.SINGLE, 12);
            Assert.That(r.S.Deployables.ServerConfigure(mover, ItemDeviceConfig.From(0, 1, 1, 1, 5), 0), Is.True);
            r.Run(50);
            Assert.That(PipeRig.Units(b.Storage), Is.EqualTo(5), "5/s moves 5 in one second");
            r.Run(50);
            Assert.That(PipeRig.Units(b.Storage), Is.EqualTo(10), "...and 10 in two");
        }

        [Test]
        public void a_stalled_mover_banks_at_most_one_second()
        {
            var r = new PipeRig();
            var (a, b, _) = r.Line(bw: 1, bh: 1);
            r.Fill(a, PipeFixtures.SINGLE, 45);
            b.Storage.tryAddItem(new Item(PipeFixtures.NAILS, 1));   // B's only cell holds something that will not merge
            r.Run(150);                                              // three seconds stalled
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.SINGLE), Is.EqualTo(0), "stalled: nothing moved");
            b.Storage.loadSize(8, 6);                                // room appears
            r.Run(1);
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.SINGLE), Is.EqualTo(32),
                        "the tick after unstalling moves the banked second -- 32, not 96 (three seconds) and not 0 or 1 (no bank)");
        }

        // ---- which item, and what "fits" means ----

        [Test]
        public void the_mover_takes_the_last_slot_first()
        {
            var r = new PipeRig();
            var (a, b, mover) = r.Line();
            // three different things, deliberately ADDED out of reading order: the list's last entry is the
            // magazine, and a list-order pick would take it first
            a.Storage.addItem(7, 5, 0, new Item(PipeFixtures.NAILS, 3));   // bottom-right: the LAST slot
            a.Storage.addItem(0, 0, 0, new Item(PipeFixtures.SINGLE));     // top-left
            a.Storage.addItem(2, 4, 0, new Item(PipeFixtures.MAG, 30));    // a 1x2 standing in the bottom two rows
            r.S.Deployables.ServerConfigure(mover, ItemDeviceConfig.From(0, 1, 1, 1, 1), 0);   // 1/s so we see the order
            r.Run(50);
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.NAILS), Is.EqualTo(1), "first unit out: the bottom-right stack");
            r.Run(100);
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.NAILS), Is.EqualTo(3), "...which drains before anything else moves");
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.MAG), Is.EqualTo(0));
            r.Run(50);
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.MAG), Is.EqualTo(1), "then the next slot back in reading order");
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.SINGLE), Is.EqualTo(0), "the top-left item is still last in line");
        }

        [Test]
        public void a_magazine_moves_whole_and_costs_one()
        {
            var r = new PipeRig();
            var (a, b, mover) = r.Line();
            a.Storage.tryAddItem(new Item(PipeFixtures.MAG, 30));
            r.S.Deployables.ServerConfigure(mover, ItemDeviceConfig.From(0, 1, 1, 1, 1), 0);
            r.Run(50);
            Assert.That(b.Storage.getItemCount(), Is.EqualTo(1), "one magazine arrived");
            Assert.That(b.Storage.getItem(0).item.amount, Is.EqualTo(30), "with all 30 rounds -- a magazine is not split");
            Assert.That(a.Storage.getItemCount(), Is.EqualTo(0));
        }

        [Test]
        public void a_full_destination_stalls_the_mover()
        {
            var r = new PipeRig();
            var (a, b, _) = r.Line(bw: 1, bh: 1);
            r.Fill(a, PipeFixtures.SINGLE, 5);
            b.Storage.tryAddItem(new Item(PipeFixtures.NAILS, 1));
            long stallsBefore = r.S.ItemMovers.Diag.Stalls;
            r.Run(100);
            Assert.That(PipeRig.Units(a.Storage), Is.EqualTo(5), "nothing left the source");
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.SINGLE), Is.EqualTo(0), "nothing arrived");
            Assert.That(r.S.ItemMovers.Diag.Stalls, Is.GreaterThan(stallsBefore), "and the mover says it stalled");
        }

        [Test]
        public void the_mover_does_not_hunt_past_a_stuck_last_slot()
        {
            // "Don't hunt other slots when it doesn't fit; the mover stalls." The nails COULD go (B's stack has room
            // for 4); the single in the last slot cannot. A mover that went looking for something that fits would
            // move nails here.
            var r = new PipeRig();
            var (a, b, _) = r.Line(bw: 1, bh: 1);
            a.Storage.addItem(0, 0, 0, new Item(PipeFixtures.NAILS, 5));
            a.Storage.addItem(7, 5, 0, new Item(PipeFixtures.SINGLE));
            b.Storage.tryAddItem(new Item(PipeFixtures.NAILS, 60));
            r.Run(100);
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.NAILS), Is.EqualTo(60), "the mover did not go hunting for the nails");
            Assert.That(PipeRig.Units(a.Storage), Is.EqualTo(6), "the source is untouched");
        }

        [Test]
        public void a_transfer_that_cannot_land_puts_everything_back()
        {
            // The second layer under the fit check: even handed a destination with no room, Transfer conserves.
            var r = new PipeRig();
            var src = r.Crate(new Vector3(0f, 0f, 0f), 8, 6);
            var full = r.Crate(new Vector3(5f, 0f, 0f), 1, 1);
            full.Storage.tryAddItem(new Item(PipeFixtures.NAILS, 60));
            src.Storage.addItem(3, 3, 0, new Item(PipeFixtures.SINGLE));
            Assert.That(ItemTransfer.Transfer(src.Storage, src.Storage.getItem(0), full.Storage, 1), Is.EqualTo(0), "nothing landed");
            Assert.That(src.Storage.getItemCount(), Is.EqualTo(1), "the single is back in the source...");
            Assert.That((src.Storage.getItem(0).x, src.Storage.getItem(0).y), Is.EqualTo(((byte)3, (byte)3)), "...in the cell it left");
            src.Storage.addItem(0, 0, 0, new Item(PipeFixtures.NAILS, 10));
            var nails = src.Storage.getItem(1);
            Assert.That(ItemTransfer.Transfer(src.Storage, nails, full.Storage, 10), Is.EqualTo(4), "a whole stack of 10 into room for 4 moves 4");
            Assert.That(PipeRig.Units(src.Storage, PipeFixtures.NAILS) + PipeRig.Units(full.Storage, PipeFixtures.NAILS), Is.EqualTo(70),
                        "10 + 60 = 70 before and after: nothing minted, nothing lost");
            Assert.That(PipeRig.Units(src.Storage, PipeFixtures.NAILS), Is.EqualTo(6));

            // ...and the SPLIT path (part of a stack): 5 of the remaining 6 offered to a destination that now has
            // no room at all. takeFrom has already reduced the source by the time the place fails, so this is the
            // put-back that keeps the 5 from vanishing.
            var partial = src.Storage.getItem(1);
            Assert.That(ItemTransfer.Transfer(src.Storage, partial, full.Storage, 5), Is.EqualTo(0), "nothing fits");
            Assert.That(PipeRig.Units(src.Storage, PipeFixtures.NAILS), Is.EqualTo(6), "the split-off 5 went back into the stack");
        }

        [Test]
        public void a_full_destination_still_takes_a_merge()
        {
            var r = new PipeRig();
            var (a, b, _) = r.Line(bw: 1, bh: 1);
            a.Storage.tryAddItem(new Item(PipeFixtures.NAILS, 10));
            b.Storage.tryAddItem(new Item(PipeFixtures.NAILS, 60));   // B has no free cell -- but its stack has room for 4
            r.Run(100);
            Assert.That(PipeRig.Units(b.Storage, PipeFixtures.NAILS), Is.EqualTo(64), "the 4 that merge, merged (\"unless it can merge stacks\")");
            Assert.That(PipeRig.Units(a.Storage, PipeFixtures.NAILS), Is.EqualTo(6), "the 6 that cannot stayed home -- not lost");
        }

        [Test]
        public void nothing_moves_without_power()
        {
            var r = new PipeRig();
            var (a, b, mover) = r.Line(powered: false);
            r.Fill(a, PipeFixtures.SINGLE, 10);
            r.Run(100);
            Assert.That(PipeRig.Units(b.Storage), Is.EqualTo(0), "an unpowered mover is a box");
            Assert.That(r.S.ItemMovers.Diag.UnpoweredSteps, Is.GreaterThan(0));
            // CONTROL: the same rig moves once it is fed, so the zero above is the power and not a broken line
            uint gen = r.Place(PipeFixtures.GEN, new Vector3(5f, 0f, 3f));
            r.S.Deployables.ServerToggle(gen, true, 0);
            r.S.Deployables.ServerConnectWire(r.S.Ids.Mint(), gen, 0, mover, 0, 0);
            r.Run(60);
            Assert.That(PipeRig.Units(b.Storage), Is.EqualTo(10), "...and moves everything once powered");
        }

        [Test]
        public void a_generator_switched_off_stops_the_mover()
        {
            var r = new PipeRig();
            var (a, b, _) = r.Line();
            r.Fill(a, PipeFixtures.SINGLE, 20);
            uint gen = r.S.Deployables.All.First(e => e.DefId == PipeFixtures.GEN).NetIdValue;
            r.S.Deployables.ServerToggle(gen, false, 0);
            r.Run(100);
            Assert.That(PipeRig.Units(b.Storage), Is.EqualTo(0), "a wired but STOPPED generator powers nothing");
        }
    }

    [TestFixture]
    public class ItemRoutingTests
    {
        [SetUp] public void SetUp() => PipeFixtures.RegisterAssets();

        // A -> adapter -> mover -> splitter -> up to three adapters -> crates. `connect` picks which outputs are piped.
        static (PipeRig r, InventoryReplication.CrateEntry src, InventoryReplication.CrateEntry[] outs, uint splitter, uint mover)
            SplitRig(bool[] connect, byte outW = 8, byte outH = 6)
        {
            var r = new PipeRig();
            var src = r.Crate(new Vector3(0f, 0f, 0f));
            src.Storage.loadSize(8, 6);
            uint as_ = r.Adapter(src);
            uint mover = r.PoweredMover(new Vector3(3f, 0f, 0f));
            uint split = r.Place(PipeFixtures.SPLITTER, new Vector3(6f, 0f, 0f));
            r.Pipe(as_, 1, mover, 0);
            r.Pipe(mover, 1, split, 0);
            var outs = new InventoryReplication.CrateEntry[3];
            for (int i = 0; i < 3; i++)
            {
                outs[i] = r.Crate(new Vector3(10f, 0f, i * 6f), outW, outH);
                uint ad = r.Adapter(outs[i]);
                if (connect[i]) r.Pipe(split, (byte)(1 + i), ad, 0);
            }
            return (r, src, outs, split, mover);
        }

        static int[] Counts(InventoryReplication.CrateEntry[] outs) => outs.Select(c => PipeRig.Units(c.Storage)).ToArray();

        [Test]
        public void round_robin_rotates_through_the_connected_outputs()
        {
            var (r, src, outs, _, _) = SplitRig(new[] { true, true, true });
            r.Fill(src, PipeFixtures.SINGLE, 30);
            r.Run(60);
            Assert.That(Counts(outs), Is.EqualTo(new[] { 10, 10, 10 }), "30 singles, three outputs, ten each");
        }

        [Test]
        public void round_robin_ignores_a_disconnected_output()
        {
            var (r, src, outs, _, _) = SplitRig(new[] { true, false, true });
            r.Fill(src, PipeFixtures.SINGLE, 20);
            r.Run(40);
            Assert.That(Counts(outs), Is.EqualTo(new[] { 10, 0, 10 }),
                        "the unpiped middle output gets no turn at all (\"completely ignore disconnected i/o\")");
        }

        [Test]
        public void round_robin_skips_a_full_branch_instead_of_stalling()
        {
            var (r, src, outs, _, _) = SplitRig(new[] { true, true, true });
            outs[1].Storage.loadSize(1, 1);
            outs[1].Storage.tryAddItem(new Item(PipeFixtures.NAILS, 1));   // branch 2 is full
            r.Fill(src, PipeFixtures.SINGLE, 20);
            r.Run(40);
            Assert.That(Counts(outs), Is.EqualTo(new[] { 10, 1, 10 }), "the full branch is skipped, the other two share everything");
        }

        [Test]
        public void overflow_fills_the_first_output_before_the_next()
        {
            var (r, src, outs, splitter, _) = SplitRig(new[] { true, true, true });
            outs[0].Storage.loadSize(2, 2);   // the first output holds four
            r.S.Deployables.ServerConfigure(splitter, ItemDeviceConfig.From((byte)SplitterMode.Overflow, 1, 1, 1, 32), 0);
            r.Fill(src, PipeFixtures.SINGLE, 10);
            r.Run(30);
            Assert.That(Counts(outs), Is.EqualTo(new[] { 4, 6, 0 }), "1 until full, then 2; 3 never needed");
        }

        [Test]
        public void weighted_splits_by_weight_and_interleaves()
        {
            var (r, src, outs, splitter, mover) = SplitRig(new[] { true, true, true });
            r.S.Deployables.ServerConfigure(splitter, ItemDeviceConfig.From((byte)SplitterMode.Weighted, 3, 1, 0, 32), 0);
            r.S.Deployables.ServerConfigure(mover, ItemDeviceConfig.From(0, 1, 1, 1, 1), 0);   // one per second, so the order is visible
            r.Fill(src, PipeFixtures.SINGLE, 24);
            var order = new List<int>();
            for (int s = 0; s < 8; s++)
            {
                var before = Counts(outs);
                r.Run(50);
                var after = Counts(outs);
                for (int i = 0; i < 3; i++) if (after[i] > before[i]) order.Add(i);
            }
            Assert.That(order, Is.EqualTo(new[] { 0, 0, 1, 0, 0, 0, 1, 0 }),
                        "3:1:0 interleaves A A B A | A A B A (smooth WRR), and weight 0 is OFF");
            r.S.Deployables.ServerConfigure(mover, ItemDeviceConfig.From(0, 1, 1, 1, 32), 0);
            r.Run(60);
            Assert.That(Counts(outs), Is.EqualTo(new[] { 18, 6, 0 }), "24 at 3:1:0 is 18:6:0");
        }

        [Test]
        public void combiner_round_robins_the_connected_inputs_and_skips_empty_ones()
        {
            var r = new PipeRig();
            var a = r.Crate(new Vector3(0f, 0f, 0f));
            var b = r.Crate(new Vector3(0f, 0f, 6f));    // input 1: an EMPTY source
            var d = r.Crate(new Vector3(0f, 0f, 12f));   // input 2
            var dst = r.Crate(new Vector3(20f, 0f, 0f), 8, 6);
            uint aa = r.Adapter(a), ab = r.Adapter(b), adD = r.Adapter(d), ad = r.Adapter(dst);
            uint comb = r.Place(PipeFixtures.COMBINER, new Vector3(6f, 0f, 6f));
            uint mover = r.PoweredMover(new Vector3(12f, 0f, 6f));
            r.Pipe(aa, 1, comb, 0);
            r.Pipe(ab, 1, comb, 1);
            r.Pipe(adD, 1, comb, 2);
            r.Pipe(comb, 3, mover, 0);
            r.Pipe(mover, 1, ad, 0);
            r.Fill(a, PipeFixtures.SINGLE, 5);
            r.Fill(d, PipeFixtures.NAILS, 5);   // merges into one stack of 5
            r.S.Deployables.ServerConfigure(mover, ItemDeviceConfig.From(0, 1, 1, 1, 1), 0);   // one per second
            var sources = new System.Text.StringBuilder();
            for (int s = 0; s < 6; s++)
            {
                int beforeA = PipeRig.Units(a.Storage), beforeD = PipeRig.Units(d.Storage);
                r.Run(50);
                if (PipeRig.Units(a.Storage) < beforeA) sources.Append('A');
                if (PipeRig.Units(d.Storage) < beforeD) sources.Append('D');
            }
            Assert.That(sources.ToString(), Is.EqualTo("ADADAD"),
                        "A and D take turns; the EMPTY input B is skipped rather than costing a turn");
        }

        [Test]
        public void a_combiner_input_left_unpiped_is_not_a_turn()
        {
            var r = new PipeRig();
            var a = r.Crate(new Vector3(0f, 0f, 0f));
            var d = r.Crate(new Vector3(0f, 0f, 12f));
            var dst = r.Crate(new Vector3(20f, 0f, 0f), 8, 6);
            uint aa = r.Adapter(a), adD = r.Adapter(d), ad = r.Adapter(dst);
            uint comb = r.Place(PipeFixtures.COMBINER, new Vector3(6f, 0f, 6f));
            uint mover = r.PoweredMover(new Vector3(12f, 0f, 6f));
            r.Pipe(aa, 1, comb, 0);
            r.Pipe(adD, 1, comb, 2);   // input 1 has NO pipe at all
            r.Pipe(comb, 3, mover, 0);
            r.Pipe(mover, 1, ad, 0);
            r.Fill(a, PipeFixtures.SINGLE, 10);
            r.Fill(d, PipeFixtures.SINGLE, 10);
            r.Run(20);   // 12.8 units of budget -> 12 transfers
            Assert.That(PipeRig.Units(dst.Storage), Is.EqualTo(12));
            Assert.That((10 - PipeRig.Units(a.Storage), 10 - PipeRig.Units(d.Storage)), Is.EqualTo((6, 6)),
                        "the two piped inputs split evenly; the empty socket between them is ignored");
        }

        [Test]
        public void a_mover_upstream_is_not_a_source()
        {
            var r = new PipeRig();
            var a = r.Crate(new Vector3(0f, 0f, 0f));
            var b = r.Crate(new Vector3(20f, 0f, 0f));
            uint aa = r.Adapter(a), ab = r.Adapter(b);
            uint m1 = r.PoweredMover(new Vector3(6f, 0f, 0f));
            uint m2 = r.PoweredMover(new Vector3(12f, 0f, 0f));
            r.Pipe(aa, 1, m1, 0);
            r.Pipe(m1, 1, m2, 0);   // mover -> mover: m1 has no destination, m2 has no source
            r.Pipe(m2, 1, ab, 0);
            r.Fill(a, PipeFixtures.SINGLE, 5);
            r.Run(100);
            Assert.That(PipeRig.Units(b.Storage), Is.EqualTo(0), "movers do not chain into each other");
            Assert.That(PipeRig.Units(a.Storage), Is.EqualTo(5), "and nothing was lost in between");
        }

        [Test]
        public void a_pipe_loop_downstream_does_not_hang_the_router()
        {
            // mover -> S1 -> C -> S2, and S2's first output loops straight back into C. S2's second output is the
            // only real destination. The router must walk the loop, refuse it, and still find B.
            var r = new PipeRig();
            var a = r.Crate(new Vector3(0f, 0f, 0f));
            var b = r.Crate(new Vector3(20f, 0f, 0f));
            uint aa = r.Adapter(a), ab = r.Adapter(b);
            uint mv = r.PoweredMover(new Vector3(3f, 0f, 0f));
            uint s1 = r.Place(PipeFixtures.SPLITTER, new Vector3(6f, 0f, 0f));
            uint c = r.Place(PipeFixtures.COMBINER, new Vector3(9f, 0f, 0f));
            uint s2 = r.Place(PipeFixtures.SPLITTER, new Vector3(12f, 0f, 0f));
            r.Pipe(aa, 1, mv, 0);
            r.Pipe(mv, 1, s1, 0);
            r.Pipe(s1, 1, c, 0);
            r.Pipe(c, 3, s2, 0);
            r.Pipe(s2, 1, c, 1);    // the loop
            r.Pipe(s2, 2, ab, 0);   // the way out
            r.Fill(a, PipeFixtures.SINGLE, 5);
            r.Run(30);
            Assert.That(PipeRig.Units(b.Storage), Is.EqualTo(5), "everything found the way out past the loop");
        }
    }

    [TestFixture]
    public class ItemPipePersistenceTests
    {
        [SetUp] public void SetUp() => PipeFixtures.RegisterAssets();

        [Test]
        public void pipes_configs_and_adapter_bindings_survive_a_save()
        {
            var r = new PipeRig();
            var (a, b, mover) = r.Line();
            // A DECOY whose origin is NEARER to B's adapter (at b + 0.6) than B's own origin is. Re-binding by
            // nearest on load would bolt that adapter to the decoy; the saved position has to win.
            var decoy = r.Crate(b.Pos + new Vector3(0.9f, 0f, 0f));
            uint splitter = r.Place(PipeFixtures.SPLITTER, new Vector3(5f, 0f, 8f));
            r.S.Deployables.ServerConfigure(mover, ItemDeviceConfig.From(0, 1, 1, 1, 7), 0);
            r.S.Deployables.ServerConfigure(splitter, ItemDeviceConfig.From((byte)SplitterMode.Weighted, 2, 9, 0, 32), 0);
            // a routed pipe with nodes, so the path is part of what has to come back
            uint spare = r.Place(PipeFixtures.ADAPTER, new Vector3(5f, 0f, 12f));
            r.S.Deployables.ServerConnectPipe(r.S.Ids.Mint(), splitter, 1, spare, 0, new[] { new Vector3(5.5f, 1f, 9f), new Vector3(5.5f, 1f, 11f) }, 0);
            int pipesBefore = r.S.Deployables.Pipes.Count;
            var json = WorldSave.Capture(r.S, "", 0, 0.5f, 1200f).ToJson();
            Assert.That(WorldSave.TryParse(json, "", out var save, out var err), Is.True, err);

            // a FRESH server: the map's containers exist (registered at the same positions, as ContainerNetSync
            // would) but with NEW NetIds -- the saved ids would name nothing, which is the point of saving positions
            var r2 = new PipeRig(2);
            for (int i = 0; i < 7; i++) r2.S.Ids.Mint();   // shift the id space so a stale id cannot pass by luck
            var decoy2 = r2.Crate(decoy.Pos);   // registered FIRST, so a nearest-wins tie-break by id would also favour it
            var a2 = r2.Crate(a.Pos); var b2 = r2.Crate(b.Pos);
            save.ApplyWorld(r2.S, 0);

            Assert.That(r2.S.Deployables.Pipes.Count, Is.EqualTo(pipesBefore), "every pipe came back");
            var m2 = r2.S.Deployables.All.First(e => e.DefId == PipeFixtures.MOVER);
            var s2 = r2.S.Deployables.All.First(e => e.DefId == PipeFixtures.SPLITTER);
            Assert.That(m2.ItemConfig.Rate, Is.EqualTo(7), "the mover's rate");
            Assert.That(s2.ItemConfig.Mode, Is.EqualTo(SplitterMode.Weighted), "the splitter's mode");
            Assert.That(s2.ItemConfig.Weights, Is.EqualTo(new byte[] { 2, 9, 0 }), "and its weights");
            var routed = r2.S.Deployables.Pipes.All.First(p => p.SrcId == s2.NetIdValue);
            Assert.That(routed.Path.Length, Is.EqualTo(2), "the route nodes");
            Assert.That(routed.Path[1].z, Is.EqualTo(11f).Within(0.01f));
            var adapters = r2.S.Deployables.All.Where(e => e.DefId == PipeFixtures.ADAPTER).Select(e => e.ItemCrateId).ToList();
            Assert.That(adapters, Does.Contain(a2.NetIdValue).And.Contain(b2.NetIdValue), "adapters re-bound to the NEW crate ids");
            Assert.That(adapters, Does.Not.Contain(decoy2.NetIdValue), "...and B's adapter to B, not to the nearer decoy");

            // and it WORKS: the restored line moves at the restored rate
            r2.Fill(a2, PipeFixtures.SINGLE, 10);
            r2.Run(50);
            Assert.That(PipeRig.Units(b2.Storage), Is.EqualTo(7), "the reloaded mover moves 7 in a second");
        }

        [Test]
        public void a_save_from_before_pipes_still_loads()
        {
            var r = new PipeRig();
            r.Place(PipeFixtures.GEN, new Vector3(1f, 0f, 1f));
            var json = WorldSave.Capture(r.S, "", 0, 0.5f, 1200f).ToJson();
            // strip every v56 property the way an older build's file simply never had them
            var legacy = System.Text.RegularExpressions.Regex.Replace(json, ",\"(Pipes|ItemConfig|ItemCrate)\":(\\[\\]|null)", "");
            Assert.That(legacy, Does.Not.Contain("\"Pipes\""), "fixture: the legacy text really lacks the field");
            Assert.That(WorldSave.TryParse(legacy, "", out var save, out var err), Is.True, err);
            var r2 = new PipeRig(2);
            save.ApplyWorld(r2.S, 0);
            Assert.That(r2.S.Deployables.Count, Is.EqualTo(1));
            Assert.That(r2.S.Deployables.Pipes.Count, Is.EqualTo(0));
        }
    }

    [TestFixture]
    public class ItemPipeReplicationTests
    {
        [SetUp] public void SetUp() => PipeFixtures.RegisterAssets();

        static TransactionalHarness Harness(int seed, params string[] names)
        {
            var h = new TransactionalHarness(seed);
            PipeFixtures.Register(h.Server.Deployables.Schema);
            foreach (var n in names) { var c = h.AddClient(n); PipeFixtures.Register(c.Deployables.Schema); }
            h.StepUntil(() => h.Clients.All(c => c.State == NetSessionState.Connected), 600);
            return h;
        }

        [Test]
        public void a_pipe_and_a_config_reach_every_client_and_a_late_joiner()
        {
            var h = Harness(5601, "a", "b");
            var a = h.Clients[0];
            var crate = h.Server.Inventories.ServerRegisterCrate(h.Server.Ids.Mint(), 5, 4, new Vector3(-2f, 0f, 3f));
            // a decoy whose origin is NEARER the adapter than the container the client snapped to
            h.Server.Inventories.ServerRegisterCrate(h.Server.Ids.Mint(), 5, 4, new Vector3(0.4f, 0f, 3f));
            // place through the REAL command path, adapter included -- its binding is the place command's job
            h.Grant(a.PlayerId, new Item(PipeFixtures.ADAPTER));
            h.Grant(a.PlayerId, new Item(PipeFixtures.SPLITTER));
            a.SendPlaceDeployable(PipeFixtures.ADAPTER, new Vector3(-0.4f, 0f, 3f), 0f, 255, 0, 0, crate.NetIdValue);
            a.SendPlaceDeployable(PipeFixtures.SPLITTER, new Vector3(2f, 0f, 3f), 0f);
            Assert.That(h.StepUntil(() => a.Deployables.Count == 2), Is.True, "both placed and replicated");
            uint ad = h.FindDeployable(a, PipeFixtures.ADAPTER), sp = h.FindDeployable(a, PipeFixtures.SPLITTER);
            h.Server.Deployables.TryGet(ad, out var adE);
            Assert.That(adE.ItemCrateId, Is.EqualTo(crate.NetIdValue), "the adapter bound the container the client NAMED");

            var path = new[] { new Vector3(0.5f, 1f, 3.5f) };
            a.SendConnectPipe(ad, 1, sp, 0, path);
            a.SendConfigureItemDevice(sp, ItemDeviceConfig.From((byte)SplitterMode.Overflow, 4, 5, 6, 32));
            Assert.That(h.StepUntil(() => h.Clients[1].Deployables.Pipes.Count == 1
                                       && h.Clients[1].Deployables.TryGet(sp, out var e) && e.ItemConfig.Mode == SplitterMode.Overflow), Is.True,
                        "the pipe and the config reached the OTHER client");
            h.Step(10);
            Assert.That(a.Deployables.StateHash(), Is.EqualTo(h.Server.Deployables.StateHash()), "A parity (pipes + config hashed)");

            // the late joiner gets it all from the join snapshot alone
            var c = h.AddClient("c");
            PipeFixtures.Register(c.Deployables.Schema);
            Assert.That(h.StepUntil(() => c.State == NetSessionState.Connected && c.Deployables.Pipes.Count == 1), Is.True, "late joiner sees the pipe");
            var lp = c.Deployables.Pipes.All.First();
            Assert.That((lp.SrcId, lp.SrcPort, lp.DstId, lp.DstPort), Is.EqualTo((ad, (byte)1, sp, (byte)0)));
            Assert.That(lp.Path.Length, Is.EqualTo(1));
            Assert.That(lp.Path[0].y, Is.EqualTo(1f).Within(0.01f), "with its route node");
            Assert.That(c.Deployables.TryGet(sp, out var cs) && cs.ItemConfig.Weights.SequenceEqual(new byte[] { 4, 5, 6 }), Is.True, "and the splitter's weights");
            h.Step(10);
            Assert.That(c.Deployables.StateHash(), Is.EqualTo(h.Server.Deployables.StateHash()), "late-joiner parity");
        }

        [Test]
        public void a_refused_pipe_never_reaches_anyone()
        {
            var h = Harness(5602, "a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(PipeFixtures.SPLITTER));
            h.Grant(a.PlayerId, new Item(PipeFixtures.COMBINER));
            a.SendPlaceDeployable(PipeFixtures.SPLITTER, new Vector3(1f, 0f, 2f), 0f);
            a.SendPlaceDeployable(PipeFixtures.COMBINER, new Vector3(3f, 0f, 2f), 0f);
            Assert.That(h.StepUntil(() => a.Deployables.Count == 2), Is.True);
            uint sp = h.FindDeployable(a, PipeFixtures.SPLITTER), cb = h.FindDeployable(a, PipeFixtures.COMBINER);
            long rej = h.Server.Commands.Diag.ValidationRejected;
            a.SendConnectPipe(sp, 0, cb, 0, null);   // In -> In
            a.SendConfigureItemDevice(sp, ItemDeviceConfig.From(0, 1, 1, 1, 200));   // a forged rate
            h.Step(20);
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.EqualTo(rej + 2), "both refused at the choke point");
            Assert.That(h.Server.Deployables.Pipes.Count, Is.EqualTo(0));
            Assert.That(h.Server.Deployables.TryGet(sp, out var e) && e.ItemConfig.Rate == ItemDeviceConfig.DefaultRate, Is.True);
            // CONTROL: the right way round goes through
            a.SendConnectPipe(sp, 1, cb, 0, null);
            Assert.That(h.StepUntil(() => a.Deployables.Pipes.Count == 1), Is.True, "Out -> In connects over the wire");
        }

        [Test]
        public void cutting_a_pipe_and_picking_up_a_device_both_take_pipes_off_every_client()
        {
            var h = Harness(5603, "a", "b");
            var a = h.Clients[0]; var b = h.Clients[1];
            foreach (var id in new[] { PipeFixtures.SPLITTER, PipeFixtures.COMBINER, PipeFixtures.SPLITTER }) h.Grant(a.PlayerId, new Item(id));
            a.SendPlaceDeployable(PipeFixtures.SPLITTER, new Vector3(1f, 0f, 2f), 0f);
            a.SendPlaceDeployable(PipeFixtures.COMBINER, new Vector3(3f, 0f, 2f), 0f);
            a.SendPlaceDeployable(PipeFixtures.SPLITTER, new Vector3(5f, 0f, 2f), 0f);
            Assert.That(h.StepUntil(() => a.Deployables.Count == 3), Is.True);
            var ids = a.Deployables.All.Select(e => e.NetIdValue).OrderBy(x => x).ToArray();
            a.SendConnectPipe(ids[0], 1, ids[1], 0, null);
            a.SendConnectPipe(ids[1], 3, ids[2], 0, null);
            Assert.That(h.StepUntil(() => b.Deployables.Pipes.Count == 2), Is.True, "two pipes on B");

            uint first = h.Server.Deployables.Pipes.All.First(p => p.SrcId == ids[0]).NetIdValue;
            a.SendRemovePipe(first);
            Assert.That(h.StepUntil(() => b.Deployables.Pipes.Count == 1), Is.True, "a cut pipe leaves B");

            a.SendPickupDeployable(ids[2]);   // the device at the far end of the remaining pipe
            Assert.That(h.StepUntil(() => b.Deployables.Pipes.Count == 0 && h.Server.Deployables.Pipes.Count == 0), Is.True,
                        "picking up a device cascades its pipes, server and client");
            h.Step(10);
            Assert.That(b.Deployables.StateHash(), Is.EqualTo(h.Server.Deployables.StateHash()), "parity after the cascade");
        }

        [Test]
        public void an_adapter_with_no_container_is_refused_and_not_spent()
        {
            var h = Harness(5604, "a");
            var a = h.Clients[0];
            h.Grant(a.PlayerId, new Item(PipeFixtures.ADAPTER));
            long rej = h.Server.Commands.Diag.ValidationRejected;
            a.SendPlaceDeployable(PipeFixtures.ADAPTER, new Vector3(1f, 0f, 2f), 0f);   // no crate anywhere
            h.Step(20);
            Assert.That(h.Server.Deployables.Count, Is.EqualTo(0), "no adapter in mid-air");
            Assert.That(h.Server.Commands.Diag.ValidationRejected, Is.EqualTo(rej + 1));
            h.Server.Inventories.TryGet(a.PlayerId, out var inv);
            Assert.That(inv.Inventory.getItemCount(PipeFixtures.ADAPTER), Is.EqualTo(1), "and the player keeps the adapter");
            // CONTROL: with a container in reach the same command places
            h.Server.Inventories.ServerRegisterCrate(h.Server.Ids.Mint(), 5, 4, new Vector3(1.5f, 0f, 2f));
            a.SendPlaceDeployable(PipeFixtures.ADAPTER, new Vector3(1f, 0f, 2f), 0f);
            Assert.That(h.StepUntil(() => h.Server.Deployables.Count == 1), Is.True, "beside a container it places");
        }
    }
}
