using Godot;
using System.Collections.Generic;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // review #9: AnimalCatalog.SpeciesForAnimalId (PEI Fauna animal id -> species byte, used by AnimalField to
    // seed replicated wildlife) had no test -- UnifyTests set AnimalAgent.Species directly, bypassing it. Verify
    // the id->species mapping and the fail-safe-to-deer default (an unknown id must never yield an out-of-range
    // species -> a missing render, never a crash or desync).
    public class AnimalCatalogSpecies : GameTest
    {
        public override string Name => "review.animal_catalog_species";
        public override IEnumerable<Step> Run()
        {
            T.Check("deer id 1 -> species 0", AnimalCatalog.SpeciesForAnimalId(1) == 0);
            T.Check("pig id 4 -> species 1", AnimalCatalog.SpeciesForAnimalId(4) == 1);
            T.Check("cow id 6 -> species 2", AnimalCatalog.SpeciesForAnimalId(6) == 2);
            T.Check("unknown id -> fail-safe deer (species 0), never out of range", AnimalCatalog.SpeciesForAnimalId(9999) == 0);
            T.Check("Get(species) maps back to the right rig", AnimalCatalog.Get(1).Rig == "pig" && AnimalCatalog.Get(2).Rig == "cow");
            T.Check("Get is bounds-safe for an out-of-range species (fail-safe to entry 0)", AnimalCatalog.Get(200).Rig == "deer");
            yield return Ticks(1);
        }
    }

    // A minimal "zombies"-group listener that records raw Hear() calls -- isolates "did a moving MP player cause a
    // SERVER-side footstep emit" from the real ZombieController's sight/decay/hunt logic.
    public partial class HearSpy : Node3D
    {
        public int Heard;
        public float LastLoud;
        public override void _Ready() => AddToGroup("zombies");
        public void Hear(Vector3 pos, float loudness) { Heard++; LastLoud = loudness; }
    }

    // MP hearing (VoX 2026-07-20): a moving remote player must make FOOTSTEP noise the SERVER's zombie AI hears.
    // Pre-fix the footstep emit ran only on the client's local tree (PlayerController Phase-3 hearing), so a
    // dedicated server's zombies only ever aggro'd on SIGHT. The fix emits server-side from PlayerNetSync (the
    // NetHold avatar's adopted stance + moved flag). Drive a real walker over the wire into a DedicatedServer
    // (RemoteAvatars -> PlayerNetSync) and assert a "zombies"-group node hears the footsteps only while moving.
    public class MpZombieHearsFootsteps : GameTest
    {
        public override string Name => "review.mp_zombie_hears_footsteps";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready (flat fallback on CI)", world.Ready);

            var net = new MemNetwork(20260731);
            var walker = new NetWorldClient(new MemClientTransport(net), "walker", contentHash: NetContent.Hash);
            bool send = false; UnityEngine.Vector3 claim = default; byte recovAck = 0;
            byte sprint = MoveInput.PackStance(EPlayerStance.SPRINT);
            walker.PlayerRecov += e => { recovAck = e.RecovCounter; claim = e.Pos; };
            var pump = new DelegateSimStep((t, dt) =>
            {
                net.Tick(); walker.Tick();
                if (send) walker.SendPlayerState(claim, 0f, 0f, default, sprint, grounded: true, recovAck: recovAck);
            }, "l1.clientpump");
            world.Sim.Sim.Add(pump);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            walker.Connect();

            yield return Until(() => walker.State == NetSessionState.Connected && walker.JoinSnapshotsApplied >= 1, 5);
            T.Check("walker joined", walker.State == NetSessionState.Connected && walker.JoinSnapshotsApplied >= 1);
            walker.Players.TryGetByOwner(walker.PlayerId, out var spawnE);
            claim = spawnE.Pos;

            var spy = new HearSpy();
            World.AddChild(spy);
            spy.GlobalPosition = new Vector3(spawnE.Pos.x, spawnE.Pos.y, spawnE.Pos.z);

            // (teeth 1) STATIONARY: the walker claims the same spot -> the avatar never moves -> no footstep noise
            send = true;
            yield return Ticks(30);
            T.Check($"a motionless player makes NO footstep noise (spy heard {spy.Heard})", spy.Heard == 0);

            // (teeth 2) MOVING: sprint forward -> the SERVER emits footstep noise each ~0.4 s into the zombies group.
            // Pre-fix (no server-side emit) the spy would stay 0 no matter how far the player runs.
            var pos = spawnE.Pos;
            for (int i = 0; i < 60; i++) { pos.x += 7f * 0.02f; claim = pos; yield return Ticks(1); }
            T.Check($"the SERVER emitted footstep noise for the moving player -> the zombies group heard it (spy heard {spy.Heard}, loudness {spy.LastLoud:0})",
                    spy.Heard > 0);
        }
    }
}
