using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // strawberry, right after the solid-box collider fix landed: "its really hard to get off the top of a
    // ladder, i keep snapping back onto it. and the snap-on has a very big radius".
    //
    // Both are plausibly MY fix biting: the rung trimesh only presented grab surface at 9 discrete heights,
    // so the attach was patchy in a way that accidentally made letting go easy. A solid box grabs at every
    // height, which is the point -- and also means any downward drift at the top re-acquires instantly.
    //
    // This measures rather than assumes: what IS the attach envelope, and what actually happens at the top.
    public sealed class LadderTopAndRadiusProbe : GameTest
    {
        public override string Name => "ladder.top_and_radius_probe";
        public override double TimeoutSimSeconds => 90;

        static IEnumerable<Node> AllNodes(Node n)
        {
            yield return n;
            foreach (var c in n.GetChildren()) foreach (var d in AllNodes(c)) yield return d;
        }

        public override IEnumerable<Step> Run()
        {
            // EXPLORATION, not a gate: builds a whole PEI world for ~20 s to print a climb trajectory. Gated
            // because ladder.real_world_repro already builds the same world and actually ASSERTS things --
            // paying for it twice on every L1 run buys nothing, and the L1 cap has been overrun three times.
            // Kept because this is what found the ladder that cannot be climbed past 53.02 (a bunker shaft
            // sealed by the unimplemented terrain holes), which no assertion would have surfaced.
            if (System.Environment.GetEnvironmentVariable("UG_LADDERPROBE") != "1")
            { T.Check("SKIPPED (set UG_LADDERPROBE=1 to probe)", true); yield break; }

            string mapRoot = (System.Environment.GetEnvironmentVariable("UG_UNTURNED_DIR")?.TrimEnd('\\', '/')
                               ?? "/home/ec2-user/unturned") + "/Maps/PEI";
            if (!System.IO.Directory.Exists(mapRoot + "/Landscape/Heightmaps"))
            { T.Check("SKIPPED -- no real Unturned install", true); yield break; }

            bool hadWater = Terrain.HasWater; float oldSea = Terrain.SeaLevelY;
            var oldActive = Terrain.Active; string oldMapDir = Terrain.MapDir;
            try
            {
                var task = WorldBuilder.BuildFullWorld(World, WorldMode.Playable,
                    mapRoot: mapRoot, mapPlace: "placements.txt",
                    syncLoad: true, activeHoliday: "NONE");
                yield return Until(() => task.IsCompleted, 30);
                if (!task.Result.Ready) { T.Check("world ready", false); yield break; }

                Node3D lad = null;
                foreach (var n in AllNodes(World))
                    if (n is StaticBody3D sb && sb.HasMeta(Ladder.Meta)) { lad = sb; break; }
                if (lad == null) { T.Check("found a ladder", false); yield break; }
                PlayerController p = null;
                foreach (var n in AllNodes(World)) if (n is PlayerController pc) { p = pc; break; }
                if (p == null) { T.Check("found a player", false); yield break; }
                yield return Ticks(2);

                var face = Ladder.FaceAxis(lad);
                var side = face.Cross(Vector3.Up).Normalized();
                float top = lad.GlobalPosition.Y + 3.375f;   // mesh AABB is +-3.375 on its long axis
                float foot = lad.GlobalPosition.Y - 3.375f;
                GD.Print($"[LADPROBE] ladder origin {lad.GlobalPosition} top={top:0.00} foot={foot:0.00}");

                // ENVELOPE ALREADY MEASURED (and then dropped from this probe): grabs out to ~0.75 m along
                // the face normal and ~0.5 m laterally, which IS retail's spec (0.75 m ray, ladder half-width
                // 0.575). The sweep also sampled non-monotonically -- teleporting the player 40 m away between
                // samples left it mid-fall -- so it was measuring its own reset, not the envelope. Removed
                // rather than left in printing numbers I would not trust.

                // ---- 2. THE TOP. Climb to the very top holding forward, then RELEASE and watch.
                var startAt = lad.GlobalPosition + face * 0.65f;
                p.TeleportTo(new Vector3(startAt.X, lad.GlobalPosition.Y - 1.5f, startAt.Z));   // foot is buried in real terrain; this height is the one ladder.real_world_repro proved attaches
                p.LookAt(p.GlobalPosition - face, Vector3.Up);
                yield return Until(() => p.Stance == EPlayerStance.CLIMB, 3.0);
                T.Check($"attached at the foot to climb ({p.Stance})", p.Stance == EPlayerStance.CLIMB);

                // NO `Until` HERE. A condition that never holds reports "timed out" and throws away the
                // trajectory, which is exactly the data needed -- first run burned 12 s and told me only that
                // neither branch fired. Log unconditionally instead.
                p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
                for (int i = 0; i < 24; i++)
                {
                    yield return Ticks(25);   // 0.5 s
                    GD.Print($"[LADPROBE] climb t+{(i + 1) * 0.5f:0.0}s y={p.GlobalPosition.Y:0.00} (top {top:0.00}) " +
                             $"stance={p.Stance} vy={p.Velocity.Y:+0.00;-0.00} out={(p.GlobalPosition - lad.GlobalPosition).Dot(face):0.00}");
                    if (p.Stance != EPlayerStance.CLIMB) break;
                }

                // hold forward at the top for a while -- this is what a player does trying to get off
                for (int i = 0; i < 5; i++)
                {
                    yield return Ticks(20);
                    GD.Print($"[LADPROBE]   t+{(i + 1) * 0.4f:0.0}s  y={p.GlobalPosition.Y:0.00}  stance={p.Stance}  " +
                             $"distFromFace={(p.GlobalPosition - lad.GlobalPosition).Dot(face):0.00}");
                }
                p.ScriptedInput = null;
                T.Check("probe ran (see [LADPROBE])", true);
            }
            finally
            {
                Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea;
                Terrain.Active = oldActive; Terrain.MapDir = oldMapDir;
            }
        }
    }
}
