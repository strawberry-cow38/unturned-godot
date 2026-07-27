using System.Collections.Generic;
using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>
    /// Doors, beds and deadzones over the REAL wire: a NetWorldServer and NetWorldClient pair on
    /// MemTransport, with every intent going out as an actual datagram and every answer coming back as one.
    ///
    /// These exist because the ServerInteractables battery next door proves the RULES and nothing else.
    /// A rule that is never reached by a command is a rule nobody can use -- which is the exact failure
    /// this feature already shipped once (doors whose logic was tested and whose collider belonged to no
    /// physics body, methods with no gameplay caller). So nothing here calls a server method directly:
    /// each test sends the command a player's keypress would send, and reads the result off the client.
    /// </summary>
    [TestFixture]
    public class InteractableMpTests
    {
        const uint DoorId = 501, BedId = 601, OtherBedId = 602;
        const ushort MaskId = 9500, SuitTopId = 9501, SuitLegsId = 9502;

        [SetUp]
        public void SetUp()
        {
            // Assets is a process-wide static, so every fixture re-seeds it rather than inheriting whatever
            // ran last. The radiation-proof outfit is the point: proofRadiation has been parsed off item
            // data all along with no hazard to protect against.
            TransactionalFixtures.RegisterAssets();
            Assets.add(new ItemAsset { id = MaskId, itemName = "Gas Mask", size_x = 2, size_y = 2, proofRadiation = true });
            Assets.add(new ItemAsset { id = SuitTopId, itemName = "Hazmat Top", size_x = 2, size_y = 2, proofRadiation = true });
            Assets.add(new ItemAsset { id = SuitLegsId, itemName = "Hazmat Legs", size_x = 2, size_y = 2, proofRadiation = true });
        }

        static TransactionalHarness Harness(out NetWorldClient client, Vector3 doorAt = default)
        {
            var h = new TransactionalHarness(seed: 4242).Connected("alice");
            client = h.Clients[0];
            h.Server.Interactables.RegisterDoor(DoorId, doorAt, owner: client.PlayerId);
            h.Server.Interactables.RegisterBed(BedId, doorAt);
            return h;
        }

        /// <summary>Park the player's authoritative entity somewhere. Reach is measured against the SERVER's
        /// idea of where they are, which is the whole point of checking it there.</summary>
        static void PutPlayerAt(TransactionalHarness h, NetWorldClient c, Vector3 pos)
        {
            h.Server.Players.ServerTeleport(c.PlayerId, pos, h.Server.Session.CurrentTick);
        }

        // ---- doors ----

        [Test]
        public void A_Client_Opens_A_Door_By_Sending_Intent_And_Learns_The_Result()
        {
            var h = Harness(out var c);
            PutPlayerAt(h, c, Vector3.zero);

            var seen = new List<DoorStateEvent>();
            c.DoorStateChanged += e => seen.Add(e);

            Assert.That(c.SendToggleDoor(DoorId), Is.True, "the command went out");
            Assert.That(h.StepUntil(() => seen.Count > 0), Is.True, "the client heard back");

            Assert.That(seen[0].NetId, Is.EqualTo(DoorId));
            Assert.That(seen[0].Open, Is.True);
            Assert.That(h.Server.Interactables.IsDoorOpen(DoorId), Is.True, "and the SERVER is the one that opened it");
        }

        [Test]
        public void A_Door_The_Player_Is_Nowhere_Near_Does_Not_Open()
        {
            // The client is free to name any NetId it likes; standing 500 m away is what makes it a lie.
            var h = Harness(out var c);
            PutPlayerAt(h, c, new Vector3(500f, 0f, 500f));

            int events = 0;
            c.DoorStateChanged += _ => events++;
            c.SendToggleDoor(DoorId);
            h.Step(40);

            Assert.That(h.Server.Interactables.IsDoorOpen(DoorId), Is.False);
            Assert.That(events, Is.Zero, "a refusal is silence, not a correction");
        }

        [Test]
        public void Locking_A_Door_Reaches_Every_Client_Not_Just_The_Owner()
        {
            // The bug this pins: the lock used to change only server-side state, so a second player went on
            // seeing an unlocked door forever. Both clients must hear it.
            var h = new TransactionalHarness(seed: 7).Connected("owner", "bystander");
            var owner = h.Clients[0];
            var bystander = h.Clients[1];
            h.Server.Interactables.RegisterDoor(DoorId, Vector3.zero, owner: owner.PlayerId);
            PutPlayerAt(h, owner, Vector3.zero);
            PutPlayerAt(h, bystander, Vector3.zero);

            DoorStateEvent? atOwner = null, atBystander = null;
            owner.DoorStateChanged += e => atOwner = e;
            bystander.DoorStateChanged += e => atBystander = e;

            owner.SendSetDoorLocked(DoorId, true);
            Assert.That(h.StepUntil(() => atOwner.HasValue && atBystander.HasValue), Is.True);

            Assert.That(atOwner.Value.Locked, Is.True);
            Assert.That(atBystander.Value.Locked, Is.True, "a lock everyone else cannot see is not a lock");
        }

        [Test]
        public void A_Stranger_Cannot_Lock_Someone_Elses_Door()
        {
            var h = new TransactionalHarness(seed: 8).Connected("owner", "stranger");
            var owner = h.Clients[0];
            var stranger = h.Clients[1];
            h.Server.Interactables.RegisterDoor(DoorId, Vector3.zero, owner: owner.PlayerId);
            PutPlayerAt(h, stranger, Vector3.zero);

            stranger.SendSetDoorLocked(DoorId, true);
            h.Step(40);

            Assert.That(h.Server.Interactables.IsDoorLocked(DoorId), Is.False);
        }

        [Test]
        public void A_Locked_Door_Refuses_The_Stranger_Over_The_Wire()
        {
            var h = new TransactionalHarness(seed: 9).Connected("owner", "stranger");
            var owner = h.Clients[0];
            var stranger = h.Clients[1];
            h.Server.Interactables.RegisterDoor(DoorId, Vector3.zero, owner: owner.PlayerId);
            PutPlayerAt(h, owner, Vector3.zero);
            PutPlayerAt(h, stranger, Vector3.zero);

            owner.SendSetDoorLocked(DoorId, true);
            h.Step(20);

            stranger.SendToggleDoor(DoorId);
            h.Step(40);
            Assert.That(h.Server.Interactables.IsDoorOpen(DoorId), Is.False, "locked means locked to the stranger");

            owner.SendToggleDoor(DoorId);
            Assert.That(h.StepUntil(() => h.Server.Interactables.IsDoorOpen(DoorId)), Is.True,
                        "...and still open to the owner");
        }

        // ---- beds ----

        [Test]
        public void Claiming_A_Bed_Over_The_Wire_Tells_Everyone_Who_Owns_It()
        {
            var h = new TransactionalHarness(seed: 11).Connected("alice", "bob");
            var alice = h.Clients[0];
            var bob = h.Clients[1];
            h.Server.Interactables.RegisterBed(BedId, Vector3.zero);
            PutPlayerAt(h, alice, Vector3.zero);

            BedClaimedEvent? atBob = null;
            bob.BedClaimed += e => atBob = e;

            alice.SendClaimBed(BedId);
            Assert.That(h.StepUntil(() => atBob.HasValue), Is.True);
            Assert.That(atBob.Value.NetId, Is.EqualTo(BedId));
            Assert.That(atBob.Value.Owner, Is.EqualTo(alice.PlayerId));
        }

        [Test]
        public void Re_Claiming_Frees_The_Old_Bed_And_Says_So()
        {
            // One bed per player. The client cannot work out WHICH bed came free without an index of its
            // own, so the server has to say -- as a release event ahead of the claim, on an ordered channel.
            var h = Harness(out var alice);
            h.Server.Interactables.RegisterBed(OtherBedId, Vector3.zero);
            PutPlayerAt(h, alice, Vector3.zero);

            var seen = new List<BedClaimedEvent>();
            alice.BedClaimed += e => seen.Add(e);

            alice.SendClaimBed(BedId);
            Assert.That(h.StepUntil(() => seen.Count >= 1), Is.True);
            h.Server.Interactables.Now += 1.0;   // clear the settle window
            alice.SendClaimBed(OtherBedId);
            Assert.That(h.StepUntil(() => seen.Count >= 3), Is.True, "release + claim");

            Assert.That(seen[1].NetId, Is.EqualTo(BedId), "the released bed is named first");
            Assert.That(seen[1].Owner, Is.Zero);
            Assert.That(seen[2].NetId, Is.EqualTo(OtherBedId));
            Assert.That(seen[2].Owner, Is.EqualTo(alice.PlayerId));
            Assert.That(h.Server.Interactables.BedOwner(BedId), Is.Zero);
        }

        [Test]
        public void You_Cannot_Take_A_Bed_Someone_Else_Claimed()
        {
            var h = new TransactionalHarness(seed: 12).Connected("alice", "bob");
            var alice = h.Clients[0];
            var bob = h.Clients[1];
            h.Server.Interactables.RegisterBed(BedId, Vector3.zero);
            PutPlayerAt(h, alice, Vector3.zero);
            PutPlayerAt(h, bob, Vector3.zero);

            alice.SendClaimBed(BedId);
            Assert.That(h.StepUntil(() => h.Server.Interactables.BedOwner(BedId) == alice.PlayerId), Is.True);
            h.Server.Interactables.Now += 1.0;

            bob.SendClaimBed(BedId);
            h.Step(40);
            Assert.That(h.Server.Interactables.BedOwner(BedId), Is.EqualTo((ulong)alice.PlayerId),
                        "a claimed bed is taken by destroying it, not by asking");
        }

        // ---- respawn ----

        [Test]
        public void A_Dead_Player_With_A_Bed_Comes_Back_At_The_Bed()
        {
            // The point of the whole bed feature. Without this it is a piece of furniture you can highlight.
            var bedAt = new Vector3(60f, 0f, -25f);
            var h = new TransactionalHarness(seed: 13).Connected("alice");
            var alice = h.Clients[0];
            h.Server.Interactables.RegisterBed(BedId, bedAt);
            PutPlayerAt(h, alice, bedAt);

            alice.SendClaimBed(BedId);
            Assert.That(h.StepUntil(() => h.Server.Interactables.BedOwner(BedId) == alice.PlayerId), Is.True);

            // Die far from the bed, so landing there cannot be confused with never having moved.
            PutPlayerAt(h, alice, new Vector3(-200f, 0f, 300f));
            h.Server.Combat.DamagePlayerExternal(alice.PlayerId, 1000f);
            Assert.That(h.StepUntil(() => !h.Server.CombatState.IsAlive(alice.PlayerId)), Is.True, "they died");

            Assert.That(h.StepUntil(() => h.Server.CombatState.IsAlive(alice.PlayerId), maxTicks: 400), Is.True,
                        "and respawned");
            Assert.That(h.Server.Players.TryGetByOwner(alice.PlayerId, out var e), Is.True);
            Assert.That((e.Pos - bedAt).magnitude, Is.LessThan(1.5f),
                        $"respawned at {e.Pos}, expected the claimed bed at {bedAt}");
        }

        [Test]
        public void With_No_Bed_The_Map_Spawn_Still_Wins()
        {
            // The seam must be invisible when nobody has claimed anything -- otherwise every existing
            // respawn in every other test starts landing somewhere new.
            var h = new TransactionalHarness(seed: 14).Connected("alice");
            var alice = h.Clients[0];
            Assert.That(h.Server.CombatState.TryGet(alice.PlayerId, out var cs), Is.True);
            var mapSpawn = cs.SpawnPos;

            PutPlayerAt(h, alice, new Vector3(-200f, 0f, 300f));
            h.Server.Combat.DamagePlayerExternal(alice.PlayerId, 1000f);
            Assert.That(h.StepUntil(() => !h.Server.CombatState.IsAlive(alice.PlayerId)), Is.True);
            Assert.That(h.StepUntil(() => h.Server.CombatState.IsAlive(alice.PlayerId), maxTicks: 400), Is.True);

            Assert.That(h.Server.Players.TryGetByOwner(alice.PlayerId, out var e), Is.True);
            Assert.That((e.Pos - mapSpawn).magnitude, Is.LessThan(0.01f));
        }

        // ---- join state ----

        [Test]
        public void A_Late_Joiner_Learns_Which_Doors_Are_Open_And_Who_Owns_Which_Bed()
        {
            // Events only carry CHANGES. Someone who joins after the change has missed it, and would render
            // an open base sealed -- so the snapshot block has to carry the state itself.
            var h = Harness(out var alice);
            PutPlayerAt(h, alice, Vector3.zero);

            alice.SendToggleDoor(DoorId);
            alice.SendSetDoorLocked(DoorId, true);
            alice.SendClaimBed(BedId);
            Assert.That(h.StepUntil(() => h.Server.Interactables.IsDoorOpen(DoorId)
                                       && h.Server.Interactables.IsDoorLocked(DoorId)
                                       && h.Server.Interactables.BedOwner(BedId) == alice.PlayerId), Is.True);

            var bob = h.AddClient("latecomer");
            Assert.That(h.StepUntil(() => bob.State == NetSessionState.Connected
                                       && bob.InteractableState.DoorCount > 0, maxTicks: 600), Is.True);

            Assert.That(bob.InteractableState.TryGetDoor(DoorId, out var view), Is.True);
            Assert.That(view.Open, Is.True, "the latecomer must see the door as it actually stands");
            Assert.That(view.Locked, Is.True);
            Assert.That(bob.InteractableState.BedOwner(BedId), Is.EqualTo(alice.PlayerId));
        }

        [Test]
        public void The_Join_Table_Keeps_Up_With_Later_Changes()
        {
            // A block that only ever populated on join would pass the test above and still leave a client
            // permanently stale from its second tick onward.
            var h = Harness(out var alice);
            PutPlayerAt(h, alice, Vector3.zero);
            Assert.That(h.StepUntil(() => alice.InteractableState.DoorCount > 0, maxTicks: 600), Is.True);
            Assert.That(alice.InteractableState.TryGetDoor(DoorId, out var before), Is.True);
            Assert.That(before.Open, Is.False);

            alice.SendToggleDoor(DoorId);
            Assert.That(h.StepUntil(() => alice.InteractableState.TryGetDoor(DoorId, out var v) && v.Open,
                                    maxTicks: 200), Is.True, "the snapshot block tracks the change too");
        }

        [Test]
        public void A_Door_Removed_Server_Side_Leaves_The_Replica_Table()
        {
            // The table REPLACES rather than merges, so a broken-down door has to vanish from the replica.
            // A merge would keep it -- and keep it looking interactable -- forever.
            var h = Harness(out var alice);
            Assert.That(h.StepUntil(() => alice.InteractableState.DoorCount > 0, maxTicks: 600), Is.True);

            h.Server.Interactables.RemoveDoor(DoorId);
            Assert.That(h.StepUntil(() => !alice.InteractableState.TryGetDoor(DoorId, out _), maxTicks: 200), Is.True);
        }

        // ---- deadzones ----

        [Test]
        public void Standing_In_A_Deadzone_Hurts_A_Networked_Player()
        {
            var h = new TransactionalHarness(seed: 15).Connected("alice");
            var alice = h.Clients[0];
            var zoneAt = new Vector3(0f, 0f, 0f);
            h.Server.Deadzones.AddVolume(zoneAt, new Vector3(30f, 25f, 30f));
            PutPlayerAt(h, alice, zoneAt);

            Assert.That(h.Server.CombatState.TryGet(alice.PlayerId, out var cs), Is.True);
            float startHp = cs.HealthExact;

            // Long enough to clear the entry grace and take real damage from an unprotected body.
            h.Step(200);   // 4 s at 50 Hz

            Assert.That(cs.HealthExact, Is.LessThan(startHp),
                        "contaminated ground has to hurt the server's player, not only a PlayerController");
        }

        [Test]
        public void Walking_Out_Of_A_Deadzone_Stops_The_Damage()
        {
            var h = new TransactionalHarness(seed: 16).Connected("alice");
            var alice = h.Clients[0];
            h.Server.Deadzones.AddVolume(Vector3.zero, new Vector3(30f, 25f, 30f));
            PutPlayerAt(h, alice, Vector3.zero);
            h.Step(200);

            Assert.That(h.Server.CombatState.TryGet(alice.PlayerId, out var cs), Is.True);
            PutPlayerAt(h, alice, new Vector3(400f, 0f, 400f));
            h.Step(20);   // let the poll notice they left
            float afterLeaving = cs.HealthExact;

            h.Step(200);
            Assert.That(cs.HealthExact, Is.EqualTo(afterLeaving).Within(0.001f),
                        "out of the zone is out of the zone");
        }

        [Test]
        public void A_Deadzone_Can_Kill_And_The_Death_Runs_The_Normal_Path()
        {
            // Routing damage through the external-damage queue rather than poking health directly is what
            // makes this a real death (respawn timer, broadcast) instead of a player stuck at 0 HP.
            var h = new TransactionalHarness(seed: 17).Connected("alice");
            var alice = h.Clients[0];
            h.Server.Deadzones.AddVolume(Vector3.zero, new Vector3(30f, 25f, 30f));
            PutPlayerAt(h, alice, Vector3.zero);

            bool died = false;
            alice.PlayerDied += e => { if (e.Victim == alice.PlayerId) died = true; };

            Assert.That(h.StepUntil(() => died, maxTicks: 2000), Is.True,
                        "an unprotected player standing in contaminated ground eventually dies of it");
        }

        [Test]
        public void A_Sealed_Suit_Buys_Time_In_The_Same_Zone()
        {
            // Proves the gear read reaches the server's authoritative inventory. Without it, radiation
            // proofing would be a stat that does nothing in MP -- which is the state the whole deadzone
            // feature was written to get OUT of.
            var bare = TimeToDie(protectedSuit: false);
            var suited = TimeToDie(protectedSuit: true);
            Assert.That(suited, Is.GreaterThan(bare * 2),
                        $"a suit lasted {suited} ticks vs {bare} bare -- protection must actually be read server-side");
        }

        static int TimeToDie(bool protectedSuit)
        {
            var h = new TransactionalHarness(seed: 18).Connected("alice");
            var alice = h.Clients[0];
            h.Server.Deadzones.AddVolume(Vector3.zero, new Vector3(30f, 25f, 30f));
            h.Server.Players.ServerTeleport(alice.PlayerId, Vector3.zero, h.Server.Session.CurrentTick);
            if (protectedSuit) DressForRadiation(h, alice.PlayerId);

            for (int t = 0; t < 4000; t++)
            {
                h.Step();
                if (!h.Server.CombatState.IsAlive(alice.PlayerId)) return t;
            }
            return 4000;
        }

        /// <summary>Put a full radiation-proof outfit on the SERVER's copy of this player.</summary>
        static void DressForRadiation(TransactionalHarness h, ushort playerId)
        {
            Assert.That(h.Server.Inventories.TryGet(playerId, out var entry), Is.True);
            var inv = entry.Inventory;
            inv.wornMask = new Item(MaskId, 1, 100);
            inv.wornShirt = new Item(SuitTopId, 1, 100);
            inv.wornPants = new Item(SuitLegsId, 1, 100);
        }
    }
}
