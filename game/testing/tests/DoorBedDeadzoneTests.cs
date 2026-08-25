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

    // strawberry_cow 2026-08-09: "im gonna have u working on functional doors ... i want doors to open 90
    // degrees." The door this replaces had ELEVEN green tests and did not work -- nothing could place one and
    // its leaf was a BoxMesh. So the two claims worth checking are the two those tests never made: that a
    // PLACEMENT yields a door, and that the door is the real ripped asset rather than a stand-in.
    public class DoorPlacedWoodenSwings : GameTest
    {
        public override string Name => "door.placed_wooden_door_swings_90";

        static ObjectDoor LeafOf(Node host)
        {
            foreach (var c in host.GetChildren()) if (c is ObjectDoor d) return d;
            return null;
        }
        // the hinge pivot is ObjectDoor's first Node3D child (see ObjectDoor._Ready) -- read off the live tree
        // rather than adding a debug seam to someone else's file
        static Node3D PivotOf(ObjectDoor d)
        {
            foreach (var c in d.GetChildren()) if (c is Node3D n && c is not CollisionShape3D) return n;
            return null;
        }
        /// <summary>Highest point of the leaf mesh in WORLD space -- the discriminator between a door that
        /// swings and one that falls over, both of which sweep the same 90 degrees.</summary>
        static float LeafTopY(Node3D pivot)
        {
            float top = float.MinValue;
            foreach (var c in pivot.GetChildren())
            {
                if (c is not MeshInstance3D m || m.Mesh == null) continue;
                var ab = m.Mesh.GetAabb();
                for (int i = 0; i < 8; i++)
                    top = Mathf.Max(top, (m.GlobalTransform * ab.GetEndpoint(i)).Y);
            }
            return top;
        }

        static float LeafBottomY(Node3D pivot)
        {
            float bot = float.MaxValue;
            foreach (var c in pivot.GetChildren())
            {
                if (c is not MeshInstance3D m || m.Mesh == null) continue;
                var ab = m.Mesh.GetAabb();
                for (int i = 0; i < 8; i++)
                    bot = Mathf.Min(bot, (m.GlobalTransform * ab.GetEndpoint(i)).Y);
            }
            return bot;
        }

        static float SweptDeg(Node3D pivot)
        {
            var q = pivot.Basis.GetRotationQuaternion();
            return Mathf.RadToDeg(2f * Mathf.Acos(Mathf.Clamp(Mathf.Abs(q.W), -1f, 1f)));
        }

        public override IEnumerable<Step> Run()
        {
            DoorDeploy.ForgetCatalog();
            var host = DoorDeploy.SpawnFor(DeployableDef.DoorPine, World, Vector3.Zero, 0f);
            T.Check("placing a door prop produces a door", host != null);
            if (host == null) yield break;
            yield return Step.Ticks(2);

            var leaf = LeafOf(host);
            T.Check("with a swinging leaf", leaf != null);
            if (leaf == null) yield break;
            var pivot = PivotOf(leaf);
            T.Check("and a hinge pivot", pivot != null);
            if (pivot == null) yield break;

            // The RIPPED mesh, not a placeholder. "a door exists" is exactly what the old door passed while
            // being a brown box, so the discriminating check is that real geometry came through.
            // Count from the INDEX array when there is one and the VERTEX array when there is not: ObjMesh
            // hands back an unindexed mesh, so reading only the index array counts a perfectly good door as
            // zero triangles -- which is what this check did on its first run, and it looked exactly like a
            // missing mesh rather than like a wrong test.
            long tris = 0;
            foreach (var c in pivot.GetChildren())
            {
                if (c is not MeshInstance3D m || m.Mesh == null || m.Mesh.GetSurfaceCount() == 0) continue;
                var arr = m.Mesh.SurfaceGetArrays(0);
                var idx = arr[(int)Mesh.ArrayType.Index].As<int[]>();
                var vts = arr[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
                tris += (idx != null && idx.Length > 0 ? idx.Length : (vts?.Length ?? 0)) / 3;
            }
            T.Check($"carrying the ripped door mesh, not a box ({tris} tris)", tris > 12);

            T.Check($"starts closed ({SweptDeg(pivot):0.#} deg)", !leaf.IsOpen && SweptDeg(pivot) < 3f);
            // STANDS UP, checked against the ASSET rather than a floor value. "taller than 1 m" passes for a
            // door lying on its side, which is exactly what shipped: I stood these up with the deployable
            // table's +90 when these rips carry their height on +Z and need 270, and strawberry_cow caught it
            // in one look at a render while two of my checks sat green. So compare the placed height to the
            // mesh's OWN Z extent -- that fails for any wrong stand-up, in either direction.
            float meshH = 0f;
            foreach (var c in pivot.GetChildren())
                if (c is MeshInstance3D m0 && m0.Mesh != null) meshH = Mathf.Max(meshH, m0.Mesh.GetAabb().Size.Z);
            float closedTop = LeafTopY(pivot), closedBot = LeafBottomY(pivot);
            // UP from the placement point, not down from it. Height EXTENT is the wrong discriminator and I
            // tried it: rotating +-90 about X maps the mesh's +Z onto -+Y either way, so a door standing up
            // and a door hanging through the floor both measure 2.80 m tall. The bug is an INVERSION, so the
            // thing that differs is the SIGN -- placed at y=0 the leaf must occupy [0, +h], and the broken
            // stand-up puts it in [-h, 0]. Ask what the check prints when it is broken, before writing it.
            T.Check($"stands UP from where it was placed, not down through the floor "
                    + $"(y {closedBot:0.00}..{closedTop:0.00}, mesh {meshH:0.00} m)",
                    meshH > 0.5f && closedBot > -0.2f && closedTop > meshH * 0.8f);

            // ObjectDoor's re-toggle cooldown is WALL CLOCK (Time.GetTicksMsec), and a headless test steps
            // physics far faster than real time -- so a single Toggle() call is legitimately refused and the
            // door just sits there. Retry until it takes rather than pretending the cooldown is not real.
            for (int i = 0; i < 240 && !leaf.Toggle(); i++) yield return Step.Ticks(1);
            for (int i = 0; i < 240 && SweptDeg(pivot) < 89f; i++) yield return Step.Ticks(1);
            T.Check("toggling opens it", leaf.IsOpen);
            // Assert the POSE it reached, not the angle it was asked for. A sign error on the axis mirrors the
            // swing while every catalog number still reads correct, so the request is not the evidence.
            float open = SweptDeg(pivot);
            T.Check($"and it swings to 90 deg ({open:0.#})", Mathf.Abs(open - 90f) < 6f);

            // ...but 90 degrees ABOUT WHAT? The swept magnitude is identical whether the leaf yaws open like a
            // door or tips over like a drawbridge, so the angle alone cannot tell those apart -- and a
            // three-quarter render cannot either, which is what sent me looking for a real check. A vertical
            // hinge PRESERVES the leaf's height; a horizontal one collapses it. So compare the world-space top
            // of the leaf, closed vs open.
            float openTop = LeafTopY(pivot);
            T.Check($"about a VERTICAL hinge -- the door stays as tall open as shut ({closedTop:0.00} -> {openTop:0.00})",
                    Mathf.Abs(openTop - closedTop) < 0.15f);

            for (int i = 0; i < 240 && !leaf.Toggle(); i++) yield return Step.Ticks(1);
            for (int i = 0; i < 240 && SweptDeg(pivot) > 1f; i++) yield return Step.Ticks(1);
            T.Check("toggling again closes it", !leaf.IsOpen);
            T.Check($"back to the closed pose ({SweptDeg(pivot):0.#} deg)", SweptDeg(pivot) < 3f);

            // METAL for free. The hinge lookup keys on the FORM, so Door_Metal is supposed to resolve the same
            // "Door" row with no anim data of its own -- which is a claim, and claims of the form "that should
            // just work" are the ones worth executing. Places and stands, or the form-key is wrong.
            var mhost = DoorDeploy.SpawnFor(DeployableDef.DoorMetal, World, new Vector3(6f, 0f, 0f), 0f);
            T.Check("a METAL door places off the same form row, with no anims of its own", mhost != null);
            if (mhost == null) yield break;
            yield return Step.Ticks(2);
            var mleaf = LeafOf(mhost);
            var mpivot = mleaf == null ? null : PivotOf(mleaf);
            T.Check("and stands up the same way",
                    mpivot != null && LeafBottomY(mpivot) > 5.8f - 6f - 0.2f && LeafTopY(mpivot) > 2.0f);
        }
    }

    // Deployable defs and inventory items share ONE id space, and nothing enforces it. AttachmentFit already
    // carries a scar comment -- "IDs are 9140+, NOT 9110-9112: those are already the Fluid Tank / Water
    // Source / Splitter, and the later Add() calls silently overwrote the magazines registered under them" --
    // and I read that comment while looking for something else, AFTER having just assigned the doors
    // 9140-9151, straight on top of four magazines. A warning in a comment is only as good as whoever happens
    // to open that file. This makes the collision fail a run instead.
    public class DeployableIdsAreUnique : GameTest
    {
        public override string Name => "deploy.ids_do_not_collide";

        public override IEnumerable<Step> Run()
        {
            var seen = new Dictionary<ushort, string>();
            var clashes = new List<string>();
            foreach (var d in DeployableDef.All)
            {
                if (d == null) continue;
                if (seen.TryGetValue(d.Id, out var other)) clashes.Add($"{d.Id}: {other} vs {d.Name}");
                else seen[d.Id] = d.Name;
            }
            T.Check($"no two deployables share an id ({seen.Count} defs{(clashes.Count > 0 ? " -- " + string.Join("; ", clashes) : "")})",
                    clashes.Count == 0);

            // and none of them lands on a magazine id, the specific collision that already happened once
            var mags = new ushort[] { 9140, 9141, 9142, 9143 };
            var onMags = new List<string>();
            foreach (var d in DeployableDef.All)
                if (d != null && System.Array.IndexOf(mags, d.Id) >= 0) onMags.Add($"{d.Id} {d.Name}");
            T.Check($"none sits on a magazine id 9140-9143 ({(onMags.Count == 0 ? "clear" : string.Join("; ", onMags))})",
                    onMags.Count == 0);

            // every def reachable through ById -- an id in the table but missing from the switch places nothing,
            // which is the silent half of this failure rather than the loud half
            var unreachable = new List<string>();
            foreach (var d in DeployableDef.All)
                if (d != null && DeployableDef.ById(d.Id) == null) unreachable.Add($"{d.Id} {d.Name}");
            T.Check($"and every def resolves through ById ({(unreachable.Count == 0 ? "all" : string.Join("; ", unreachable))})",
                    unreachable.Count == 0);

            // OBTAINABLE. A def with no item is a thing that exists in code and cannot be got -- which is
            // precisely the state the old building door was in: complete, tested, and unreachable. Check the
            // whole chain the player actually walks: item asset exists -> its id resolves to a def -> that def
            // is the door it claims to be.
            SDG.Unturned.ItemCatalog.RegisterAll();
            var missing = new List<string>();
            var mismatched = new List<string>();
            foreach (var d in DeployableDef.WoodDoors)
            {
                var asset = SDG.Unturned.Assets.find(d.Id);
                if (asset == null) { missing.Add($"{d.Id} {d.Name}"); continue; }
                if (DeployableDef.ById(asset.id) != d) mismatched.Add($"{d.Id} {d.Name}");
            }
            T.Check($"every door is an obtainable item ({(missing.Count == 0 ? "all 12" : string.Join("; ", missing) + " have no item")})",
                    missing.Count == 0);
            T.Check($"and each item equips the door it names ({(mismatched.Count == 0 ? "all" : string.Join("; ", mismatched))})",
                    mismatched.Count == 0);
            yield break;
        }
    }
}
