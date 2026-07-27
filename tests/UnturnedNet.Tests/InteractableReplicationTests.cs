using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // SP/MP unify, server side: the authoritative door + bed state. The point of these is that the SERVER
    // decides, using the same DoorLogic/BedClaims singleplayer runs -- a client can ask, and be refused.
    // Reach is the server's own addition, because a client that names a door across the map is not a
    // client with a long arm, it is a client lying about where it is standing.
    [TestFixture]
    public class InteractableReplicationTests
    {
        const ushort Me = 7, Rival = 9;
        static readonly Vector3 AtDoor = new Vector3(0f, 0f, 0f);
        static readonly Vector3 FarAway = new Vector3(500f, 0f, 500f);

        static ServerInteractables NewServer()
        {
            var s = new ServerInteractables { Now = 1000.0 };
            s.RegisterDoor(1, Vector3.zero, owner: Me);
            s.RegisterBed(2, Vector3.zero);
            return s;
        }

        // --- doors ---

        [Test]
        public void An_Unlocked_Door_Opens_For_Anyone_In_Reach()
        {
            var s = NewServer();
            Assert.That(s.CanToggleDoor(1, AtDoor, Rival, 0UL), Is.True);
            Assert.That(s.ToggleDoor(1, out bool open), Is.True);
            Assert.That(open, Is.True);
            Assert.That(s.IsDoorOpen(1), Is.True);
        }

        [Test]
        public void A_Locked_Door_Refuses_A_Stranger_Server_Side()
        {
            var s = NewServer();
            Assert.That(s.SetDoorLocked(1, Me, true), Is.True);
            Assert.That(s.CanToggleDoor(1, AtDoor, Rival, 0UL), Is.False, "the server must not take the client's word for it");
            Assert.That(s.CanToggleDoor(1, AtDoor, Me, 0UL), Is.True, "...but the owner still gets in");
        }

        [Test]
        public void Only_The_Owner_Can_Lock_It()
        {
            var s = NewServer();
            Assert.That(s.SetDoorLocked(1, Rival, true), Is.False);
            Assert.That(s.IsDoorLocked(1), Is.False);
            Assert.That(s.SetDoorLocked(1, Me, true), Is.True);
            Assert.That(s.IsDoorLocked(1), Is.True);
        }

        [Test]
        public void A_Door_Across_The_Map_Is_Out_Of_Reach()
        {
            // The whole reason reach lives server-side: a modified client can send any NetId it likes.
            var s = NewServer();
            Assert.That(s.CanToggleDoor(1, FarAway, Me, 0UL), Is.False);
        }

        [Test]
        public void The_Toggle_Cooldown_Is_Enforced_By_The_Server_Too()
        {
            var s = NewServer();
            Assert.That(s.CanToggleDoor(1, AtDoor, Me, 0UL), Is.True);
            s.ToggleDoor(1, out _);
            Assert.That(s.CanToggleDoor(1, AtDoor, Me, 0UL), Is.False, "spamming the command must not strobe the door");
            s.Now += DoorLogic.ToggleCooldown + 0.01;
            Assert.That(s.CanToggleDoor(1, AtDoor, Me, 0UL), Is.True);
        }

        [Test]
        public void Commands_Naming_A_Door_That_Does_Not_Exist_Are_Refused()
        {
            var s = NewServer();
            Assert.That(s.CanToggleDoor(999, AtDoor, Me, 0UL), Is.False);
            Assert.That(s.ToggleDoor(999, out _), Is.False);
            Assert.That(s.SetDoorLocked(999, Me, true), Is.False);
        }

        // --- beds ---

        [Test]
        public void Claiming_A_Bed_Server_Side_Sets_The_Spawn()
        {
            var s = NewServer();
            Assert.That(s.CanClaimBed(2, AtDoor, Me), Is.True);
            Assert.That(s.ClaimBed(2, Me, out uint released), Is.True);
            Assert.That(released, Is.EqualTo(0u), "they held nothing before");
            Assert.That(s.BedOwner(2), Is.EqualTo((ulong)Me));
            Assert.That(s.TryGetSpawn(Me, out _, out _), Is.True);
        }

        [Test]
        public void One_Bed_Per_Player_Is_Enforced_And_The_Old_One_Is_Named()
        {
            // The caller has to tell everyone the old bed came free, so the server reports which it was.
            var s = NewServer();
            s.RegisterBed(3, new Vector3(5f, 0f, 0f));
            Assert.That(s.ClaimBed(2, Me, out _), Is.True);
            s.Now += 1.0;
            Assert.That(s.ClaimBed(3, Me, out uint released), Is.True);
            Assert.That(released, Is.EqualTo(2u), "the server must name the bed that was released");
            Assert.That(s.BedOwner(2), Is.EqualTo(0UL));
            Assert.That(s.BedOwner(3), Is.EqualTo((ulong)Me));
        }

        [Test]
        public void You_Cannot_Claim_Someone_Elses_Bed_However_Nicely_You_Ask()
        {
            var s = NewServer();
            Assert.That(s.ClaimBed(2, Rival, out _), Is.True);
            s.Now += 1.0;
            Assert.That(s.CanClaimBed(2, AtDoor, Me), Is.False);
            Assert.That(s.ClaimBed(2, Me, out _), Is.False);
            Assert.That(s.BedOwner(2), Is.EqualTo((ulong)Rival), "a refused claim must not half-apply");
        }

        [Test]
        public void A_Bed_Across_The_Map_Is_Out_Of_Reach()
        {
            var s = NewServer();
            Assert.That(s.CanClaimBed(2, FarAway, Me), Is.False);
        }

        [Test]
        public void Destroying_A_Bed_Server_Side_Takes_The_Spawn()
        {
            var s = NewServer();
            s.ClaimBed(2, Me, out _);
            Assert.That(s.TryGetSpawn(Me, out _, out _), Is.True);
            s.RemoveBed(2);
            Assert.That(s.TryGetSpawn(Me, out _, out _), Is.False);
            Assert.That(s.BedCount, Is.EqualTo(0));
        }

        // --- the wire ---

        delegate bool TryReader<T>(SDG.NetPak.NetPakReader r, out T value);

        // Write one message into a virgin writer, read it back through a virgin reader. Everything the
        // message does not itself put on the wire is therefore not part of the result.
        static bool RoundTrip<T>(System.Action<SDG.NetPak.NetPakWriter> write, TryReader<T> read, out T value)
        {
            var w = new SDG.NetPak.NetPakWriter { buffer = new byte[64] };
            w.Reset();
            write(w);
            w.Flush();
            var r = new SDG.NetPak.NetPakReader();
            r.SetBufferSegment(w.buffer, w.writeByteIndex);
            return read(r, out value);
        }

        [Test]
        public void Every_New_Message_Round_Trips_Its_Bytes()
        {
            // A fresh writer/reader pair per message: sharing one reader across cases carries its cursor
            // state between them, which fails for reasons that have nothing to do with the payloads.
            Assert.That(RoundTrip<ToggleDoorCommand>(new ToggleDoorCommand { NetId = 4242 }.Write,
                        ToggleDoorCommand.TryRead, out var toggle), Is.True);
            Assert.That(toggle.NetId, Is.EqualTo(4242u));

            Assert.That(RoundTrip<SetDoorLockedCommand>(new SetDoorLockedCommand { NetId = 7, Locked = true }.Write,
                        SetDoorLockedCommand.TryRead, out var lockCmd), Is.True);
            Assert.That(lockCmd.NetId, Is.EqualTo(7u));
            Assert.That(lockCmd.Locked, Is.True);

            Assert.That(RoundTrip<ClaimBedCommand>(new ClaimBedCommand { NetId = 33 }.Write,
                        ClaimBedCommand.TryRead, out var claim), Is.True);
            Assert.That(claim.NetId, Is.EqualTo(33u));

            Assert.That(RoundTrip<BedClaimedEvent>(new BedClaimedEvent { NetId = 11, Owner = 300 }.Write,
                        BedClaimedEvent.TryRead, out var claimed), Is.True);
            Assert.That(claimed.NetId, Is.EqualTo(11u));
            Assert.That(claimed.Owner, Is.EqualTo((ushort)300));

            // Both bits, and set DIFFERENTLY: two bools written back to back is exactly the shape where a
            // read that silently returns the same bit twice still passes if you only ever test true/true.
            Assert.That(RoundTrip<DoorStateEvent>(new DoorStateEvent { NetId = 5, Open = true, Locked = false }.Write,
                        DoorStateEvent.TryRead, out var state), Is.True);
            Assert.That(state.NetId, Is.EqualTo(5u));
            Assert.That(state.Open, Is.True);
            Assert.That(state.Locked, Is.False);

            Assert.That(RoundTrip<DoorStateEvent>(new DoorStateEvent { NetId = 6, Open = false, Locked = true }.Write,
                        DoorStateEvent.TryRead, out var locked), Is.True);
            Assert.That(locked.Open, Is.False);
            Assert.That(locked.Locked, Is.True);
        }

        [Test]
        public void A_Truncated_Message_Is_Refused_Rather_Than_Half_Read()
        {
            var r = new SDG.NetPak.NetPakReader();
            r.SetBufferSegment(new byte[1], 1);   // far too short for a uint32
            Assert.That(ToggleDoorCommand.TryRead(r, out _), Is.False);
        }
    }
}
