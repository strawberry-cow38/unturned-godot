using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // strawberry: "fix ladders. they are very broken. i dont even know how to describe. they simply dont
    // work." ladder.attach_rules and ladder.climb_end_to_end both passed, and both were vacuous the same way
    // the door bug was: they hand-build their own ladder StaticBody3D with a solid BoxShape3D, so neither one
    // ever exercised the collider WorldBuilder actually gives a real ladder.
    //
    // THE REAL BUG: PlaceObject's default collider is `mesh.CreateTrimeshShape()` -- a trimesh of the raw
    // ripped geometry. A ladder's raw geometry is RUNGS, not a slab: Ladder_Metal_0.obj has vertices in 9
    // discrete Z-clusters exactly 0.75 m apart and ZERO vertices at any gap midpoint (verified against the
    // .obj directly). The player-attach probe (Ladder.cs / PlayerController.StepLadder) is a SINGLE ray at a
    // fixed height. As a climbing player's feet rise, that ray sweeps in and out of alignment with the rungs
    // -- attach, lose the probe in the very next gap, fall off, maybe re-attach at the next rung. That is
    // exactly "i dont even know how to describe. they simply dont work" from the player's side. Fixed in
    // WorldBuilder.PlaceObject: ladders get a solid BoxShape3D matching their own mesh AABB instead of the
    // open-rung trimesh -- climbable end to end, not rung to rung.
    //
    // This test drives a REAL ladder, placed by WorldBuilder from the REAL placements.txt via the REAL
    // guid_mesh lookup, against the REAL PEI install (mapRoot resolved from UG_UNTURNED_DIR, falling back to
    // the box's known install path) -- WorldMode.Playable, since the Objects phase (and therefore every
    // ladder collider) is only ever built when real terrain loads, regardless of WorldMode.
    public class LadderRealWorldRepro : GameTest
    {
        public override string Name => "ladder.real_world_repro";
        public override double TimeoutSimSeconds => 40;

        static IEnumerable<Node> AllNodes(Node n)
        {
            yield return n;
            foreach (var c in n.GetChildren())
                foreach (var d in AllNodes(c))
                    yield return d;
        }

        public override IEnumerable<Step> Run()
        {
            string mapRoot = (System.Environment.GetEnvironmentVariable("UG_UNTURNED_DIR")?.TrimEnd('\\', '/')
                               ?? "/home/ec2-user/unturned") + "/Maps/PEI";
            if (!System.IO.Directory.Exists(mapRoot + "/Landscape/Heightmaps"))
            {
                T.Check("SKIPPED -- no real Unturned install found (set UG_UNTURNED_DIR)", true);
                yield break;
            }

            // RESTORE THE WORLD-BUILD'S LEAKED STATICS. TestHost.ResetGlobals does not cover Terrain, and a
            // real map build writes Terrain.HasWater/SeaLevelY/Active/MapDir globally. First version of this
            // test left PEI's sea level (25.6) set for every LATER test in the run -- which sorts after
            // "ladder." alphabetically -- so their players spawned near y=0 UNDERWATER and flipped to SWIM.
            // That took the suite from 2 known failures to 12: every net.shell_* movement test, player.lean,
            // player.fall_damage, player.stance_stealth_radius, vehicle.seats. All of them passed alone. This
            // is the same hazard SwimTests already documents ("MUST restore -- static leaks into every later
            // test"); a full world build just leaks considerably more of it.
            bool hadWater = Terrain.HasWater;
            float oldSea = Terrain.SeaLevelY;
            var oldActive = Terrain.Active;
            string oldMapDir = Terrain.MapDir;
            try
            {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Playable,
                mapRoot: mapRoot, mapPlace: "placements.txt",
                noZombies: true, syncLoad: true, bakeNav: false, activeHoliday: "NONE");
            yield return Until(() => task.IsCompleted, 30);
            var world = task.Result;
            T.Check("world ready", world.Ready);
            if (!world.Ready) yield break;

            // Find every real ladder body WorldBuilder actually built -- not one this test constructed.
            Node3D found = null;
            int ladderCount = 0;
            foreach (var n in AllNodes(World))
                if (n is StaticBody3D sb && sb.HasMeta(Ladder.Meta))
                {
                    ladderCount++;
                    if (found == null) found = sb;
                }
            T.Check($"the real placements file produced at least one ladder body ({ladderCount} found, 83 expected across all maps)",
                    ladderCount > 0);
            if (found == null) yield break;

            var face = Ladder.FaceAxis(found);
            T.Check($"the real body's face axis is horizontal, as built (y {face.Y:+0.000;-0.000})",
                    Mathf.Abs(face.Y) <= Ladder.SlopeDot);

            // REUSE the world-build's OWN player rather than adding a second PlayerController -- a second one
            // never gets a legitimate spawn and its position/stance reads back meaningless in this rig.
            PlayerController p = null;
            foreach (var n in AllNodes(World)) if (n is PlayerController pc) { p = pc; break; }
            T.Check("the world build produced a player to test with", p != null);
            if (p == null) yield break;
            yield return Ticks(2);

            // Stand 0.65 m out along the face (within the 0.75 m probe), at the ladder's own foot height.
            // TeleportTo, not a bare GlobalPosition write: this player has already ticked (it auto-spawned),
            // so its render-interp snapshot is live, and PlayerController.TeleportTo's own comment documents
            // that a bare write here is silently undone one physics tick later by the interp restore --
            // exactly the bug that made the FIRST version of this test fail for a reason that had nothing to
            // do with ladders. Confirmed separately: ordinary WASD movement is unaffected (it lands inside
            // the same tick as the interp snapshot), so this was purely a teleport-vs-interpolation mismatch.
            var standAt = found.GlobalPosition + face * 0.65f;
            p.TeleportTo(new Vector3(standAt.X, found.GlobalPosition.Y - 1.5f, standAt.Z));
            p.LookAt(p.GlobalPosition - face, Vector3.Up);   // face the ladder square-on
            // ColliderBudget streams collision by proximity and scans its cells incrementally rather than all
            // at once (Cell=64m, Interval=0.25s) -- 10 ticks (0.2s) right after a teleport was not reliably
            // enough for it to reach THIS specific cell, and reported as an attach failure that had nothing to
            // do with ladders. Poll instead of a fixed short wait: a REAL player walking up over a couple of
            // seconds gives it the same room naturally.
            yield return Until(() => p.Stance == EPlayerStance.CLIMB, 3.0);

            T.Check($"attaches to the REAL, WorldBuilder-placed ladder ({p.Stance})", p.Stance == EPlayerStance.CLIMB);
            if (p.Stance != EPlayerStance.CLIMB) yield break;

            float y0 = p.GlobalPosition.Y;
            p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            yield return Ticks(30);
            T.Check($"and actually climbs it, rung to rung, not attach-and-fall ({p.GlobalPosition.Y - y0:0.00} m gained)",
                    p.GlobalPosition.Y > y0 + 0.5f);
            T.Check($"still attached partway up ({p.Stance})", p.Stance == EPlayerStance.CLIMB);
            p.ScriptedInput = null;
            }
            finally
            {
                Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea;
                Terrain.Active = oldActive; Terrain.MapDir = oldMapDir;
            }
        }
    }
}
