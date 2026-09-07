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

        // ---- prop doors (v37): a shipping container, a crossing gate arm ----
        //
        // These were purely LOCAL until v37: RequestToggleObjectDoor called ObjectDoor.Toggle and nothing
        // left the machine. So the checks that matter are that the intent now reaches the server at all, and
        // that what comes back is the SERVER's answer rather than the sender's optimism.

        const uint ObjDoorId = 801;

        static TransactionalHarness ObjDoorHarness(out NetWorldClient alice, out NetWorldClient bob)
        {
            var h = new TransactionalHarness(seed: 91).Connected("alice", "bob");
            alice = h.Clients[0];
            bob = h.Clients[1];
            h.Server.Interactables.RegisterObjectDoor(ObjDoorId, Vector3.zero);
            PutPlayerAt(h, alice, Vector3.zero);
            PutPlayerAt(h, bob, Vector3.zero);
            return h;
        }

        [Test]
        public void Opening_A_Container_Is_Seen_By_Everyone_Else()
        {
            var h = ObjDoorHarness(out var alice, out var bob);
            ObjectDoorStateEvent? atBob = null;
            bob.ObjectDoorState += e => atBob = e;

            alice.SendToggleObjectDoor(ObjDoorId);
            Assert.That(h.StepUntil(() => atBob.HasValue), Is.True,
                        "the swing reaches the OTHER player -- the whole point, since the leaf is a solid collider");
            Assert.That(atBob.Value.NetId, Is.EqualTo(ObjDoorId));
            Assert.That(atBob.Value.Open, Is.True);
            Assert.That(h.Server.Interactables.IsObjectDoorOpen(ObjDoorId), Is.True);
        }

        [Test]
        public void A_Second_Toggle_Shuts_It_Again()
        {
            var h = ObjDoorHarness(out var alice, out _);
            alice.SendToggleObjectDoor(ObjDoorId);
            Assert.That(h.StepUntil(() => h.Server.Interactables.IsObjectDoorOpen(ObjDoorId)), Is.True);
            alice.SendToggleObjectDoor(ObjDoorId);
            Assert.That(h.StepUntil(() => !h.Server.Interactables.IsObjectDoorOpen(ObjDoorId)), Is.True,
                        "it is a TOGGLE, not a latch -- a door that could only open is half a feature");
        }

        [Test]
        public void A_Container_Across_The_Map_Is_Refused()
        {
            var h = ObjDoorHarness(out var alice, out _);
            PutPlayerAt(h, alice, new Vector3(200f, 0f, 0f));
            alice.SendToggleObjectDoor(ObjDoorId);
            h.Step(40);
            Assert.That(h.Server.Interactables.IsObjectDoorOpen(ObjDoorId), Is.False,
                        "reach is the server's business, whatever the client believes it is standing next to");
        }

        [Test]
        public void The_Snapshot_Carries_The_Door_So_A_Late_Joiner_Is_Not_Wrong()
        {
            // Without the table in the block, a client joining after someone opened a container renders it
            // SHUT -- and then collides with a leaf every other player can see is out of the way.
            var h = ObjDoorHarness(out var alice, out var bob);
            alice.SendToggleObjectDoor(ObjDoorId);
            Assert.That(h.StepUntil(() => bob.InteractableState.ObjectDoorOpen(ObjDoorId)), Is.True,
                        "the snapshot names it open, not just the event");
        }

        // ---- seats (v35): sitting on furniture ----
        //
        // The rules are the vehicle's, one chair at a time, and the reason is the same: two clients each
        // deciding they took the same seat is the "multiple people can't get in a car, and so when they
        // tried it actually just like disappeared" report. Everything below goes out as a real datagram --
        // nothing calls the server's Sit/Stand directly, or these would prove only that the methods work.

        const uint SeatId = 701, OtherSeatId = 702;

        static TransactionalHarness SeatHarness(out NetWorldClient alice, out NetWorldClient bob)
        {
            var h = new TransactionalHarness(seed: 77).Connected("alice", "bob");
            alice = h.Clients[0];
            bob = h.Clients[1];
            h.Server.Interactables.RegisterSeat(SeatId, Vector3.zero);
            h.Server.Interactables.RegisterSeat(OtherSeatId, new Vector3(1f, 0f, 0f));
            PutPlayerAt(h, alice, Vector3.zero);
            PutPlayerAt(h, bob, Vector3.zero);
            return h;
        }

        [Test]
        public void Sitting_Down_Tells_Everyone_Else_The_Chair_Is_Taken()
        {
            var h = SeatHarness(out var alice, out var bob);
            SeatOccupiedEvent? atBob = null;
            bob.SeatOccupied += e => atBob = e;

            alice.SendSitSeat(SeatId);
            Assert.That(h.StepUntil(() => atBob.HasValue), Is.True, "the fact reaches the OTHER player, not just the sitter");
            Assert.That(atBob.Value.NetId, Is.EqualTo(SeatId));
            Assert.That(atBob.Value.Occupant, Is.EqualTo(alice.PlayerId));
            Assert.That(h.Server.Interactables.SeatOccupant(SeatId), Is.EqualTo(alice.PlayerId));
        }

        [Test]
        public void Two_People_Cannot_Sit_In_The_Same_Chair()
        {
            var h = SeatHarness(out var alice, out var bob);
            alice.SendSitSeat(SeatId);
            Assert.That(h.StepUntil(() => h.Server.Interactables.SeatOccupant(SeatId) == alice.PlayerId), Is.True);

            bob.SendSitSeat(SeatId);
            h.Step(40);
            Assert.That(h.Server.Interactables.SeatOccupant(SeatId), Is.EqualTo(alice.PlayerId),
                        "the second sitter is REFUSED, not seated on top -- and alice is still the one in it");
            Assert.That(h.Server.Interactables.IsSeated(bob.PlayerId), Is.False, "...and bob is not silently seated somewhere else");
        }

        [Test]
        public void Standing_Up_Frees_The_Chair_For_The_Next_Person()
        {
            var h = SeatHarness(out var alice, out var bob);
            alice.SendSitSeat(SeatId);
            Assert.That(h.StepUntil(() => h.Server.Interactables.SeatOccupant(SeatId) == alice.PlayerId), Is.True);

            alice.SendSitSeat(0);   // 0 = stand
            Assert.That(h.StepUntil(() => h.Server.Interactables.SeatOccupant(SeatId) == 0), Is.True);

            bob.SendSitSeat(SeatId);
            Assert.That(h.StepUntil(() => h.Server.Interactables.SeatOccupant(SeatId) == bob.PlayerId), Is.True,
                        "a vacated chair is really free, not merely marked so");
        }

        [Test]
        public void Moving_To_Another_Chair_Frees_The_First_One_And_Says_So_First()
        {
            // Same contract as the bed re-claim, and for the same reason: nobody may ever observe one player
            // in two seats, so the release is named BEFORE the claim on an ordered channel.
            var h = SeatHarness(out var alice, out _);
            var seen = new List<SeatOccupiedEvent>();
            alice.SeatOccupied += e => seen.Add(e);

            alice.SendSitSeat(SeatId);
            Assert.That(h.StepUntil(() => seen.Count >= 1), Is.True);
            alice.SendSitSeat(OtherSeatId);
            Assert.That(h.StepUntil(() => seen.Count >= 3), Is.True, "release + claim");

            Assert.That(seen[1].NetId, Is.EqualTo(SeatId), "the vacated seat is named first");
            Assert.That(seen[1].Occupant, Is.Zero);
            Assert.That(seen[2].NetId, Is.EqualTo(OtherSeatId));
            Assert.That(seen[2].Occupant, Is.EqualTo(alice.PlayerId));
            Assert.That(h.Server.Interactables.SeatOccupant(SeatId), Is.Zero);
        }

        [Test]
        public void A_Chair_Across_The_Map_Is_Refused()
        {
            var h = SeatHarness(out var alice, out _);
            PutPlayerAt(h, alice, new Vector3(200f, 0f, 0f));
            alice.SendSitSeat(SeatId);
            h.Step(40);
            Assert.That(h.Server.Interactables.SeatOccupant(SeatId), Is.Zero,
                        "reach is the server's business -- a client naming a distant chair does not get it");
        }

        [Test]
        public void You_Can_Always_Get_Out_Of_A_Chair_Even_From_Out_Of_Reach()
        {
            // Standing carries NO reach check on purpose. If it did, a player whose seat was removed or who
            // was teleported would be stuck sitting forever with no way to send a stand the server accepts --
            // and being stuck is worse than any exploit "standing up from far away" buys.
            var h = SeatHarness(out var alice, out _);
            alice.SendSitSeat(SeatId);
            Assert.That(h.StepUntil(() => h.Server.Interactables.SeatOccupant(SeatId) == alice.PlayerId), Is.True);

            PutPlayerAt(h, alice, new Vector3(500f, 0f, 0f));
            alice.SendSitSeat(0);
            Assert.That(h.StepUntil(() => h.Server.Interactables.SeatOccupant(SeatId) == 0), Is.True,
                        "the stand is accepted from anywhere");
        }

        [Test]
        public void A_Seated_Player_Reads_As_SITTING_On_The_Wire_So_Puppets_Can_Pose()
        {
            // THE VISUAL HALF. It needs no field of its own: the entity's stance byte has been a full byte
            // since v18 while only four codes were ever spelled, so the seat table writes a fifth. Checked on
            // the CLIENT's replica rather than on the server's entity -- the question is what another
            // player's machine renders, and only the replica can answer that.
            var h = SeatHarness(out var alice, out var bob);
            alice.SendSitSeat(SeatId);
            Assert.That(h.StepUntil(() =>
                bob.Players.TryGetByOwner(alice.PlayerId, out var e) && e.Stance == MoveInput.WireStanceSitting),
                Is.True, "bob's copy of alice carries the SITTING stance");

            alice.SendSitSeat(0);
            Assert.That(h.StepUntil(() =>
                bob.Players.TryGetByOwner(alice.PlayerId, out var e) && e.Stance != MoveInput.WireStanceSitting),
                Is.True, "...and stops carrying it the moment she stands, or she sits in mid-air forever");
        }

        [Test]
        public void The_Snapshot_Carries_Occupancy_So_A_Late_Joiner_Is_Not_Wrong()
        {
            // The events are the low-latency path and a joiner has missed all of them. Without the table in
            // the block, a client connecting after someone sat down would draw them standing in a chair AND
            // offer that chair as free -- the exact bug the door/bed table exists to prevent.
            var h = SeatHarness(out var alice, out var bob);
            alice.SendSitSeat(SeatId);
            Assert.That(h.StepUntil(() => bob.InteractableState.SeatOccupant(SeatId) == alice.PlayerId), Is.True,
                        "the seat table on the snapshot plane names the occupant, not just the event");
        }

        [Test]
        public void Disconnecting_Gives_The_Chair_Back()
        {
            // Unlike a bed claim, which survives a logout deliberately: a chair held by someone no longer
            // connected is a chair nobody can ever use again, and there is nothing in it worth keeping.
            var h = SeatHarness(out var alice, out var bob);
            SeatOccupiedEvent? freed = null;
            alice.SendSitSeat(SeatId);
            Assert.That(h.StepUntil(() => h.Server.Interactables.SeatOccupant(SeatId) == alice.PlayerId), Is.True);
            bob.SeatOccupied += e => { if (e.NetId == SeatId && e.Occupant == 0) freed = e; };

            alice.Disconnect();
            Assert.That(h.StepUntil(() => h.Server.Interactables.SeatOccupant(SeatId) == 0, 600), Is.True,
                        "the server lets go of the seat");
            Assert.That(h.StepUntil(() => freed.HasValue, 600), Is.True,
                        "...and SAYS so -- a silent release leaves every other client drawing an empty chair as taken");
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
