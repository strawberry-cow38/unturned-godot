using System.Collections.Generic;
using Godot;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;
using UVector3 = UnityEngine.Vector3;

namespace UnturnedGodot.Testing
{
    // Doors, beds and deadzones in a REAL engine, over a REAL client/server pair.
    //
    // The L0 batteries prove the rules and the wire. What they cannot prove is that any of it reaches a
    // node: this feature has already shipped once with a door whose collider belonged to no physics body
    // and a TakeDamage nothing called. So each test here drives the seam a keypress drives, on a world the
    // WorldBuilder built, and then checks the WORLD -- the leaf that swung, the body that was blocked, the
    // place a corpse came back at.

    /// <summary>
    /// A dedicated server BUILDS doors, beds and a deadzone, without a test placing any of them.
    ///
    /// This is the check the other tests here could not make, because they spawn their own fixtures: the
    /// world-build placement sat inside `if (mode == WorldMode.Playable)`, so a real dedicated server and a
    /// real joined client had no doors or beds in the world at all. Every round-trip test still passed --
    /// they were exercising furniture the test itself had put there. So this one asserts on what the
    /// WorldBuilder produced, and nothing else.
    /// </summary>
    public class DedicatedServerBuildsInteractables : GameTest
    {
        public override string Name => "net.dedicated_builds_interactables";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);

            // The world itself must carry them -- nothing below spawns a door or a bed.
            int doorsInWorld = 0, bedsInWorld = 0;
            foreach (var n in AllNodes(World))
            {
                if (n is Door) doorsInWorld++;
                else if (n is Bed) bedsInWorld++;
            }
            T.Check($"the DEDICATED world build placed a door ({doorsInWorld} found)", doorsInWorld >= 1);
            T.Check($"...and a bed ({bedsInWorld} found)", bedsInWorld >= 1);
            T.Check("...and a deadzone volume", world.Deadzones != null && world.Deadzones.VolumeCount >= 1);

            var net = new MemNetwork(7073);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net),
                                            WorldRoot = World, Deadzones = world.Deadzones };
            World.AddChild(ded);
            yield return Ticks(5);

            T.Check($"the server registered the built doors ({ded.InteractableSync.DoorCount})",
                    ded.InteractableSync.DoorCount == doorsInWorld);
            T.Check($"...and the built beds ({ded.Server.Interactables.BedCount})",
                    ded.Server.Interactables.BedCount == bedsInWorld);
            T.Check($"...and copied the deadzone volumes ({ded.Server.Deadzones.VolumeCount})",
                    ded.Server.Deadzones.VolumeCount == world.Deadzones.VolumeCount);

            world.Sim.Sim.Remove(pump);
        }

        static IEnumerable<Node> AllNodes(Node n)
        {
            yield return n;
            foreach (var c in n.GetChildren())
                foreach (var d in AllNodes(c))
                    yield return d;
        }
    }

    /// <summary>
    /// A client opens a door by intent; the server decides; BOTH worlds swing. The second half is the part
    /// worth having: the server runs its own physics, so a door that moves only in the authoritative table
    /// is one that clients see open and the server's bullets still stop at.
    /// </summary>
    public class NetDoorRoundTrip : GameTest
    {
        public override string Name => "net.door_roundtrip";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);

            // One door, built into the world the same way WorldBuilder builds them.
            var serverDoor = Door.Spawn(World, new Vector3(0f, 0f, 0f), 0f, owner: 1UL);

            var net = new MemNetwork(7070);
            var a = new NetWorldClient(new MemClientTransport(net), "opener", contentHash: NetContent.Hash);
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); a.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), WorldRoot = World };
            World.AddChild(ded);
            a.Connect();

            yield return Until(() => a.State == NetSessionState.Connected, 5);
            T.Check("client joined", a.State == NetSessionState.Connected);
            T.Check("the built door was registered as server-authoritative",
                    ded.InteractableSync != null && ded.InteractableSync.DoorCount >= 1 && serverDoor.NetId != 0);

            // Stand at the door -- reach is judged against the SERVER's idea of where this player is.
            ded.Server.Players.ServerTeleport(a.PlayerId, UVector3.zero, ded.Server.Session.CurrentTick);

            DoorStateEvent? heard = null;
            a.DoorStateChanged += e => heard = e;

            T.Check("the intent went out", a.SendToggleDoor(serverDoor.NetId));
            yield return Until(() => heard.HasValue, 5);
            T.Check("the client heard the server's answer", heard.HasValue && heard.Value.Open);

            // The payoff: the SERVER's own node swung, not just its table.
            yield return Until(() => serverDoor.IsOpen, 5);
            T.Check("the server's own door node swung open (its physics agrees with what clients see)",
                    serverDoor.IsOpen);
            yield return Until(() => serverDoor.DebugSwing > 0.9f, 5);
            T.Check($"the leaf actually animated (swing {serverDoor.DebugSwing:0.00})", serverDoor.DebugSwing > 0.9f);

            // And a door out of reach stays put, whatever the client claims.
            ded.Server.Players.ServerTeleport(a.PlayerId, new UVector3(500f, 0f, 500f), ded.Server.Session.CurrentTick);
            heard = null;
            a.SendToggleDoor(serverDoor.NetId);
            yield return Ticks(50);    // 1 s at 50 Hz
            T.Check("a door 500 m away does not close on request", serverDoor.IsOpen && !heard.HasValue);

            world.Sim.Sim.Remove(pump);
            a.Disconnect();
        }
    }

    /// <summary>
    /// A client claims a bed by intent, dies far away, and comes back at the bed. This is the whole reason
    /// beds exist -- without the respawn they are furniture you can highlight.
    /// </summary>
    public class NetBedRespawnRoundTrip : GameTest
    {
        public override string Name => "net.bed_respawn_roundtrip";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);

            // NOT ResetForNewWorld: the world build just placed (and registered) its own bed, and resetting
            // here would unregister it while leaving the node in the tree, so two beds would share a BedId.
            var bedAt = new Vector3(40f, 0f, -25f);
            var bed = Bed.Spawn(World, bedAt, 0f);

            var net = new MemNetwork(7071);
            var a = new NetWorldClient(new MemClientTransport(net), "sleeper", contentHash: NetContent.Hash);
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); a.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), WorldRoot = World };
            World.AddChild(ded);
            a.Connect();

            yield return Until(() => a.State == NetSessionState.Connected, 5);
            T.Check("client joined", a.State == NetSessionState.Connected);
            T.Check("the built bed was registered server-side", bed.NetId != 0 && ded.Server.Interactables.BedCount >= 1);

            ded.Server.Players.ServerTeleport(a.PlayerId, new UVector3(bedAt.X, bedAt.Y, bedAt.Z), ded.Server.Session.CurrentTick);

            BedClaimedEvent? heard = null;
            a.BedClaimed += e => heard = e;
            T.Check("the claim went out", a.SendClaimBed(bed.NetId));

            yield return Until(() => heard.HasValue, 5);
            T.Check("the server says this player owns the bed",
                    heard.HasValue && heard.Value.Owner == a.PlayerId
                    && ded.Server.Interactables.BedOwner(bed.NetId) == a.PlayerId);

            // Die somewhere else entirely, so waking at the bed cannot be mistaken for never having moved.
            ded.Server.Players.ServerTeleport(a.PlayerId, new UVector3(-300f, 0f, 200f), ded.Server.Session.CurrentTick);
            ded.Server.Combat.DamagePlayerExternal(a.PlayerId, 1000f);
            yield return Until(() => !ded.Server.CombatState.IsAlive(a.PlayerId), 5);
            T.Check("the player died", !ded.Server.CombatState.IsAlive(a.PlayerId));

            yield return Until(() => ded.Server.CombatState.IsAlive(a.PlayerId), 10);
            T.Check("the player respawned", ded.Server.CombatState.IsAlive(a.PlayerId));

            ded.Server.Players.TryGetByOwner(a.PlayerId, out var e2);
            float d = (e2.Pos - new UVector3(bedAt.X, bedAt.Y, bedAt.Z)).magnitude;
            T.Check($"they woke up at their bed ({d:0.00} m away, was 500+ m when they died)", d < 1.5f);

            world.Sim.Sim.Remove(pump);
            a.Disconnect();
        }
    }

    /// <summary>
    /// A networked player standing in contaminated ground loses health, and stops losing it on leaving.
    /// Singleplayer drives deadzones off PlayerController nodes, which a dedicated server does not have --
    /// this is the proof the server-side hazard reaches a real replicated player.
    /// </summary>
    public class NetDeadzoneRoundTrip : GameTest
    {
        public override string Name => "net.deadzone_roundtrip";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);

            var zoneAt = new Vector3(80f, 0f, 80f);
            var field = new DeadzoneField();
            World.AddChild(field);
            field.AddVolume(zoneAt, new Vector3(30f, 25f, 30f));

            var net = new MemNetwork(7072);
            var a = new NetWorldClient(new MemClientTransport(net), "glowing", contentHash: NetContent.Hash);
            var pump = new DelegateSimStep((t, dt) => { net.Tick(); a.Tick(); }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net),
                                            WorldRoot = World, Deadzones = field };
            World.AddChild(ded);
            a.Connect();

            yield return Until(() => a.State == NetSessionState.Connected, 5);
            T.Check("client joined", a.State == NetSessionState.Connected);
            T.Check("the built volume was copied server-side", ded.Server.Deadzones.VolumeCount == 1);

            ded.Server.CombatState.TryGet(a.PlayerId, out var cs);
            float startHp = cs.HealthExact;

            // Well outside the zone first: the hazard must not be a thing that hurts everyone everywhere.
            ded.Server.Players.ServerTeleport(a.PlayerId, new UVector3(-300f, 0f, -300f), ded.Server.Session.CurrentTick);
            yield return Ticks(75);    // 1.5 s
            T.Check($"standing outside costs nothing (hp {cs.HealthExact:0.0})", cs.HealthExact == startHp);

            ded.Server.Players.ServerTeleport(a.PlayerId, new UVector3(zoneAt.X, zoneAt.Y, zoneAt.Z), ded.Server.Session.CurrentTick);
            yield return Until(() => cs.HealthExact < startHp, 8);
            T.Check($"contaminated ground hurts a networked player (hp {cs.HealthExact:0.0} from {startHp:0.0})",
                    cs.HealthExact < startHp);

            ded.Server.Players.ServerTeleport(a.PlayerId, new UVector3(-300f, 0f, -300f), ded.Server.Session.CurrentTick);
            yield return Ticks(30);    // 0.6 s -- long enough for the 0.25 s poll to notice they left
            float afterLeaving = cs.HealthExact;
            yield return Ticks(100);   // 2 s
            T.Check($"walking out stops it (hp {cs.HealthExact:0.0}, was {afterLeaving:0.0})",
                    Mathf.Abs(cs.HealthExact - afterLeaving) < 0.001f);

            world.Sim.Sim.Remove(pump);
            a.Disconnect();
        }
    }
}
