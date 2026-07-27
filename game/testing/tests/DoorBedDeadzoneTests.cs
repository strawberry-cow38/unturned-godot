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

            // Assert BLOCKING, not a flag. The previous version checked the collider's Disabled property,
            // which stayed true-looking even though the collider was parented under an intermediate node
            // and therefore owned by no physics body at all -- the door was never solid and the test could
            // not tell. A shut door must stop something; an open one must not.
            var probe = new PhysicsRayQueryParameters3D
            {
                From = new Vector3(0f, 1f, 3f), To = new Vector3(0f, 1f, -3f), CollisionMask = 1u << 0,
            };
            var space = World.GetWorld3D().DirectSpaceState;
            T.Check("a shut door actually blocks a ray through the gap", space.IntersectRay(probe).Count > 0);

            T.Check("the owner opens it", door.TryToggle(111, 0, 100.0));
            yield return Until(() => door.DebugSwing > 0.99f, maxSimSeconds: 3);
            T.Check($"the leaf swung fully (got {door.DebugSwing:0.##})", door.DebugSwing > 0.99f);
            T.Check("an open door leaves the gap clear", space.IntersectRay(probe).Count == 0);

            T.Check("it closes again", door.TryToggle(111, 0, 200.0));
            yield return Until(() => door.DebugSwing < 0.01f, maxSimSeconds: 3);
            T.Check("a shut door blocks once more", space.IntersectRay(probe).Count > 0);
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
            // The hinge, not GlobalPosition: the body swings, so its origin moves between the emit and
            // this read. The doorway is the fixed thing a listener should localise.
            T.Check($"and it heard the DOORWAY (heard {pos}, hinge {door.DebugHinge})",
                    pos.DistanceTo(door.DebugHinge) < 0.01f);
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

    // The integration tests. Everything above proves the three systems WORK; these prove they are wired
    // into the game a player actually plays -- that the production look-raycast finds them, and that
    // dying really returns you to your claimed bed through the ordinary Die/Respawn path.

    // The player's interaction path, through the SAME seam the F key uses. (The look-focus raycast that
    // normally picks the target cannot run here: it is gated on a captured mouse, and headless Godot
    // reports MouseMode=Visible however you set it -- measured. So the F handler and this test both go
    // through RequestToggleDoor/RequestClaimBed, the codebase's existing convention for exactly this.)
    public class PlayerInteractsWithDoorsAndBeds : GameTest
    {
        public override string Name => "player.door_and_bed_interaction";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            Bed.DebugResetAll();
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            var door = Door.Spawn(World, new Vector3(0f, 0f, -3f), 0f, owner: p.PlayerId);
            var bed = Bed.Spawn(World, new Vector3(3f, 0f, -3f), 0f);
            yield return Ticks(4);

            T.Check("the player opens their own door", p.RequestToggleDoor(door));
            T.Check("it is open", door.IsOpen);

            // Someone else's locked door refuses this player.
            var theirs = Door.Spawn(World, new Vector3(-6f, 0f, -3f), 0f, owner: 999UL);
            yield return Ticks(2);
            theirs.TrySetLocked(999UL, true);
            T.Check("a stranger's locked door refuses", !p.RequestToggleDoor(theirs));
            T.Check("and stays shut", !theirs.IsOpen);

            T.Check("the player claims a bed", p.RequestClaimBed(bed));
            T.Check("the bed is theirs", bed.Owner == p.PlayerId);
            T.Check("which becomes their spawn", Bed.TryGetSpawn(p.PlayerId, out _, out _));

            Bed.DebugResetAll();
        }
    }

    public class DyingReturnsYouToYourBed : GameTest
    {
        public override string Name => "player.respawns_at_claimed_bed";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            Bed.DebugResetAll();
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            p.CaptureMouse = false;
            var bed = Bed.Spawn(World, new Vector3(40f, 0f, -25f), 0f);
            yield return Ticks(4);

            T.Check("the bed is claimed", bed.TryClaim(p.PlayerId, 100.0));
            T.Check("the claim resolves to a spawn point", Bed.TryGetSpawn(p.PlayerId, out _, out _));

            // Kill through the ordinary damage path and let the ordinary respawn clock run -- no test seam.
            // Staged, not one compound wait: a single Until hides WHICH half failed.
            p.TakeDamage(10000f);
            yield return Ticks(2);
            T.Check($"the damage killed them (health {p.Health:0})", p.Health <= 0f);

            yield return Until(() => p.Health > 0f, maxSimSeconds: 15);
            T.Check($"the respawn clock brought them back (health {p.Health:0})", p.Health > 0f);

            yield return Ticks(3);
            T.Check($"the claim still resolves AFTER respawn (owner={bed.Owner}, id={p.PlayerId})",
                    Bed.TryGetSpawn(p.PlayerId, out var stillThere, out _) && stillThere.DistanceTo(new Vector3(40f, 0f, -25f)) < 1f);
            float d = p.GlobalPosition.DistanceTo(new Vector3(40f, 0f, -25f));
            T.Check($"woke up at the claimed bed, not the map spawn (at {p.GlobalPosition}, {d:0.#} m away)", d < 4f);

            Bed.DebugResetAll();
        }
    }

    // Review found that doors and beds had no health, which made a rule BedClaims already implements --
    // destroying a bed takes its owner's respawn with it -- unreachable in an actual game. These cover
    // the behaviour now that it can happen.
    public class BreakingABedTakesTheSpawnWithIt : GameTest
    {
        public override string Name => "bed.destroyed_removes_spawn";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            Bed.DebugResetAll();
            Rigs.Ground(World);
            var bed = Bed.Spawn(World, new Vector3(12f, 0f, 0f), 0f);
            yield return Ticks(3);

            T.Check("claimed", bed.TryClaim(77UL, 100.0));
            T.Check("it is a spawn point", Bed.TryGetSpawn(77UL, out _, out _));

            T.Check("a partial hit does not destroy it", !bed.TakeDamage(bed.HealthMax - 1f));
            T.Check("still a spawn point", Bed.TryGetSpawn(77UL, out _, out _));

            T.Check("the finishing blow destroys it", bed.TakeDamage(50f));
            yield return Ticks(3);
            T.Check("and the owner loses their spawn", !Bed.TryGetSpawn(77UL, out _, out _));

            Bed.DebugResetAll();
        }
    }

    public class DoorsCanBeBrokenDown : GameTest
    {
        public override string Name => "door.breaks_down";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var door = Door.Spawn(World, new Vector3(0f, 0f, -4f), 0f, owner: 500UL);
            yield return Ticks(3);
            door.TrySetLocked(500UL, true);

            T.Check("a stranger cannot open it", !door.TryToggle(1UL, 0UL, 100.0));
            T.Check("chip damage does not fell it", !door.TakeDamage(door.HealthMax - 1f));
            T.Check("but it can be broken through", door.TakeDamage(10f));
            yield return Ticks(3);
            T.Check("the door is gone", !IsInstanceValid(door) || door.IsDestroyed);
        }

        static bool IsInstanceValid(GodotObject o) => GodotObject.IsInstanceValid(o);
    }

    // Review (cow tools) caught that Health/TakeDamage existed with NO gameplay caller: the only things
    // invoking them were the tests, which therefore proved the method worked and nothing else. This
    // fires a REAL bullet and lets StepBullets decide what it hit -- the test shoots, production code
    // does the damage.
    public class ShootingABedDestroysItAndTakesTheSpawn : GameTest
    {
        public override string Name => "bed.shot_destroys_and_clears_spawn";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            Bed.DebugResetAll();
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            var bed = Bed.Spawn(World, new Vector3(0f, 0f, -6f), 0f);
            yield return Ticks(4);

            T.Check("claimed", bed.TryClaim(p.PlayerId, 100.0));
            T.Check("it is the spawn", Bed.TryGetSpawn(p.PlayerId, out _, out _));

            float before = bed.Health;
            p.DebugFireBullet(new Vector3(0f, 0.2f, 0f), new Vector3(0f, 0f, -1f), 40f);
            yield return Ticks(6);
            T.Check($"a bullet hurt it ({before:0} -> {bed.Health:0})", bed.Health < before);

            // Keep shooting until it breaks -- through the same path every time.
            for (int i = 0; i < 12 && GodotObject.IsInstanceValid(bed) && !bed.IsDestroyed; i++)
            {
                p.DebugFireBullet(new Vector3(0f, 0.2f, 0f), new Vector3(0f, 0f, -1f), 40f);
                yield return Ticks(6);
            }

            T.Check("gunfire destroyed it", !GodotObject.IsInstanceValid(bed) || bed.IsDestroyed);
            yield return Ticks(3);
            T.Check("and the owner lost their spawn", !Bed.TryGetSpawn(p.PlayerId, out _, out _));

            Bed.DebugResetAll();
        }
    }

    public class ShootingADoorBreaksIt : GameTest
    {
        public override string Name => "door.shot_breaks_down";
        public override double TimeoutSimSeconds => 25;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            var door = Door.Spawn(World, new Vector3(0f, 0f, -6f), 0f, owner: 999UL);
            yield return Ticks(4);
            door.TrySetLocked(999UL, true);

            float before = door.Health;
            p.DebugFireBullet(new Vector3(0f, 1.0f, 0f), new Vector3(0f, 0f, -1f), 40f);
            yield return Ticks(6);
            T.Check($"a bullet hurt the door ({before:0} -> {door.Health:0})", door.Health < before);

            for (int i = 0; i < 12 && GodotObject.IsInstanceValid(door) && !door.IsDestroyed; i++)
            {
                p.DebugFireBullet(new Vector3(0f, 1.0f, 0f), new Vector3(0f, 0f, -1f), 40f);
                yield return Ticks(6);
            }
            T.Check("a locked door can be shot down", !GodotObject.IsInstanceValid(door) || door.IsDestroyed);
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

    // Locking used to be unreachable. DoorLogic.TrySetLocked existed and was L0-tested, Door.TrySetLocked
    // wrapped it -- and the only callers in the entire codebase were tests, so a lockable door could not
    // actually be locked or unlocked by a player in either mode. This drives the seam the hold-F input
    // drives (the input itself needs a captured mouse, which headless cannot have, hence the public seam).
    public class PlayerCanLockAndUnlockTheirOwnDoor : GameTest
    {
        public override string Name => "door.player_locks_own";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            var mine = Door.Spawn(World, new Vector3(0f, 0f, -3f), 0f, owner: p.PlayerId);
            var theirs = Door.Spawn(World, new Vector3(-6f, 0f, -3f), 0f, owner: 999UL);
            yield return Ticks(4);

            T.Check("a door starts unlocked", !mine.IsLocked);
            T.Check("the owner can lock it through the player seam", p.RequestSetDoorLocked(mine, true));
            T.Check("and it is locked", mine.IsLocked);

            T.Check("a locked door refuses a stranger", !mine.TryToggle(777UL, 0UL, 500.0));

            T.Check("the owner can unlock it again", p.RequestSetDoorLocked(mine, false));
            T.Check("and it is unlocked", !mine.IsLocked);

            T.Check("nobody locks someone else's door", !p.RequestSetDoorLocked(theirs, true));
            T.Check("which stays unlocked", !theirs.IsLocked);
        }
    }
}
