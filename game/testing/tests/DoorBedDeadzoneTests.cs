using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // L1 tests for the three ported features. The RULES for all three are covered exhaustively at L0
    // (DoorLogicTests / BedClaimsTests / DeadzoneSimTests) because they are engine-free by construction.
    // What only the engine can answer is whether they are actually wired into a running game -- a leaf
    // that really swings, a node lifecycle that really keeps the claim table honest, a field that really
    // finds the player and really hurts them. That is what these cover.

    public class DoorSwingsAndBlocks : GameTest
    {
        public override string Name => "door.swings_and_blocks";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var door = Door.Spawn(World, new Vector3(0f, 0f, 0f), 0f, owner: 111);
            yield return Ticks(2);

            T.Check("a fresh door is shut", !door.IsOpen);
            T.Check("a shut door blocks the gap", !door.DebugBarrierDisabled);

            T.Check("the owner opens it", door.TryToggle(111, 0, 100.0));
            yield return Ticks(1);
            T.Check("it reports open", door.IsOpen);

            // The leaf takes time to swing; the barrier must not vanish until it is actually out of the way.
            yield return Until(() => door.DebugSwing > 0.99f, maxSimSeconds: 3);
            T.Check($"the leaf swung fully (got {door.DebugSwing:0.##})", door.DebugSwing > 0.99f);
            T.Check("an open door stops blocking", door.DebugBarrierDisabled);

            T.Check("it closes again", door.TryToggle(111, 0, 200.0));
            yield return Until(() => door.DebugSwing < 0.01f, maxSimSeconds: 3);
            T.Check("a shut door blocks once more", !door.DebugBarrierDisabled);
        }
    }

    public class DoorRefusesStrangersAndSaysWhy : GameTest
    {
        public override string Name => "door.locked_refuses";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var door = Door.Spawn(World, Vector3.Zero, 0f, owner: 111);
            yield return Ticks(2);

            T.Check("owner may lock it", door.TrySetLocked(111, true));
            T.Check("a stranger may not lock it", !door.TrySetLocked(222, false));

            T.Check("a stranger cannot open a locked door", !door.TryToggle(222, 0, 100.0));
            T.Check("and is told it is locked", door.LastRefusal == DoorRefusal.Locked);
            T.Check("the door did not move", !door.IsOpen);

            T.Check("the owner still can", door.TryToggle(111, 0, 100.0));
            T.Check("with no refusal", door.LastRefusal == DoorRefusal.None);
        }
    }

    public class DoorToggleIsHeard : GameTest
    {
        public override string Name => "door.toggle_alerts";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var z = new ZombieController();
            World.AddChild(z);
            z.GlobalPosition = new Vector3(4f, 0f, 0f);   // well inside the door's alert radius
            var door = Door.Spawn(World, Vector3.Zero, 0f, owner: 111);
            yield return Ticks(3);

            var (_, before) = z.DebugHeard();
            T.Check("the zombie has heard nothing yet", before <= 0f);

            door.TryToggle(111, 0, 100.0);
            yield return Ticks(1);

            var (pos, salience) = z.DebugHeard();
            T.Check($"opening a door is heard (salience {salience:0.##})", salience > 0f);
            T.Check("and it heard the DOOR's position", pos.DistanceTo(door.GlobalPosition) < 0.01f);
        }
    }

    public class BedClaimSurvivesTheNodeLifecycle : GameTest
    {
        public override string Name => "bed.claim_lifecycle";
        public override IEnumerable<Step> Run()
        {
            Bed.DebugResetAll();
            Rigs.Ground(World);
            var a = Bed.Spawn(World, new Vector3(10f, 0f, 0f), 0f);
            var b = Bed.Spawn(World, new Vector3(-10f, 0f, 0f), 90f);
            yield return Ticks(2);

            T.Check("beds register themselves on entering the tree", Bed.Claims.Count >= 2);
            T.Check("nobody has a spawn yet", !Bed.TryGetSpawn(111, out _, out _));

            T.Check("claiming works", a.TryClaim(111, 100.0));
            T.Check("and sets the spawn", Bed.TryGetSpawn(111, out var spawn, out _) && Mathf.IsEqualApprox(spawn.X, 10f));

            T.Check("claiming a second bed moves the spawn", b.TryClaim(111, 200.0));
            T.Check("the old bed is released", !a.IsClaimed);
            T.Check("the spawn followed", Bed.TryGetSpawn(111, out var moved, out _) && Mathf.IsEqualApprox(moved.X, -10f));

            // Blowing up a bed has to take the spawn with it -- otherwise raiding a base achieves nothing.
            b.QueueFree();
            yield return Ticks(3);
            T.Check("destroying the bed removes the spawn", !Bed.TryGetSpawn(111, out _, out _));

            Bed.DebugResetAll();
        }
    }

    public class BedCannotBeStolen : GameTest
    {
        public override string Name => "bed.not_stealable";
        public override IEnumerable<Step> Run()
        {
            Bed.DebugResetAll();
            Rigs.Ground(World);
            var bed = Bed.Spawn(World, new Vector3(5f, 0f, 0f), 0f);
            yield return Ticks(2);

            T.Check("first claimant gets it", bed.TryClaim(111, 100.0));
            T.Check("a rival cannot take it", !bed.TryClaim(222, 200.0));
            T.Check("it still belongs to the first", bed.Owner == 111);
            T.Check("and the rival has no spawn", !Bed.TryGetSpawn(222, out _, out _));

            Bed.DebugResetAll();
        }
    }

    public class DeadzoneHurtsAnUnprotectedPlayer : GameTest
    {
        public override string Name => "deadzone.hurts_unprotected";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            var field = new DeadzoneField();
            World.AddChild(field);
            field.AddVolume(new Vector3(0f, 0f, 0f), new Vector3(30f, 20f, 30f));
            yield return Ticks(4);

            T.Check("the field knows the player is inside", field.IsInside(p.GlobalPosition));

            float start = p.Health;
            field.Apply(p, 0.2f);   // inside the entry grace
            T.Check($"the grace window costs nothing (health {p.Health:0.##})", Mathf.IsEqualApprox(p.Health, start));

            field.Apply(p, 1.0f);   // past the grace: this one bites
            T.Check($"standing in it hurts ({start:0.#} -> {p.Health:0.#})", p.Health < start);

            // Walking out has to stop it. Assert the move actually landed first -- otherwise a failure
            // here is ambiguous between "the field kept tracking" and "the player never left".
            // TeleportTo, not a bare GlobalPosition write: the movement sim owns the transform and
            // overwrites a direct assignment on the next tick (measured -- the player never moved).
            p.TeleportTo(new Vector3(500f, 1f, 500f));
            yield return Ticks(2);
            T.Check($"the player left the volume (at {p.GlobalPosition})", !field.IsInside(p.GlobalPosition));

            field.Apply(p, 1.0f);
            T.Check("outside the volume it is not tracked", !field.DebugTracking(p));

            float outside = p.Health;
            field.Apply(p, 1.0f);
            T.Check($"and takes no further damage (health {p.Health:0.##})", Mathf.IsEqualApprox(p.Health, outside));
        }
    }

    public class DeadzoneLeavesCleanGroundAlone : GameTest
    {
        public override string Name => "deadzone.clean_ground_safe";
        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            var field = new DeadzoneField();
            World.AddChild(field);
            field.AddVolume(new Vector3(400f, 0f, 400f), new Vector3(20f, 20f, 20f));   // somewhere else entirely
            yield return Ticks(4);

            T.Check("the player is not in the zone", !field.IsInside(p.GlobalPosition));
            float start = p.Health;
            for (int i = 0; i < 10; i++) field.Apply(p, 1.0f);
            T.Check($"clean ground is free ({p.Health:0.##})", Mathf.IsEqualApprox(p.Health, start));
        }
    }
}
