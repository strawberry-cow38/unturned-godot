using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // A RED GHOST NOW SAYS WHY (strawberry 2026-09-10: "polish and ui/ux improvements ... deployables in general").
    //
    // There are five distinct ways a placement fails and the player was shown one undifferentiated red ghost for
    // all of them, so "why can't I put it here" had no answer and the technique was to wave the cursor about until
    // it turned blue.
    //
    // Drives BarricadePlacer, which is what the player actually holds -- deploy.placer_aim covers the older
    // DeployablePlacer, and testing the class nobody uses would have proved nothing about the hint on screen.
    public sealed class PlacementReasonTests : GameTest
    {
        public override string Name => "deploy.placement_reason";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var cam = new Camera3D { Current = false };
            World.AddChild(cam);
            cam.Position = new Vector3(8f, 3f, -6f);
            cam.LookAt(new Vector3(8f, 0f, -6f), Vector3.Back);   // straight down at open ground
            var placer = new BarricadePlacer();
            World.AddChild(placer);
            placer.SetDef(DeployableDef.Generator);
            yield return Ticks(2);

            T.Check("open ground is valid", placer.Aim(cam));
            T.Check($"...and a VALID spot carries no reason (got '{placer.Reason}')", string.IsNullOrEmpty(placer.Reason));

            cam.LookAt(cam.Position + Vector3.Up * 5f, Vector3.Forward);
            placer.Aim(cam);
            string sky = placer.Reason;
            T.Check($"aiming at the sky explains itself ('{sky}')", !string.IsNullOrEmpty(sky));

            // a tall box beside the aim point: ground is still hit, the clearance sphere is not free
            var box = new StaticBody3D { CollisionLayer = 1 << 0 };
            box.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1f, 3f, 1f) } });
            World.AddChild(box);
            box.GlobalPosition = new Vector3(8.6f, 1.5f, -6f);
            cam.Position = new Vector3(8f, 3f, -6f);
            cam.LookAt(new Vector3(8f, 0f, -6f), Vector3.Back);
            yield return Ticks(2);
            placer.Aim(cam);
            string blocked = placer.Reason;
            T.Check($"an obstructed spot explains itself ('{blocked}')", !string.IsNullOrEmpty(blocked));

            // the box's vertical FACE: a Floor-mount def cannot sit on it
            cam.Position = new Vector3(6.5f, 1.5f, -6f);
            cam.LookAt(new Vector3(8.6f, 1.5f, -6f), Vector3.Up);
            placer.Aim(cam);
            string wall = placer.Reason;
            T.Check($"a wall explains itself ('{wall}')", !string.IsNullOrEmpty(wall));

            // ⭐ THE ONE THAT MATTERS. Each check above passes on a single hardcoded "Can't place here" -- which is
            // no better than the red ghost it replaced, because the whole point is telling the player WHICH rule
            // they are hitting. Three different failures must read as three different things.
            T.Check($"...and the three reasons are DISTINCT (sky='{sky}' blocked='{blocked}' wall='{wall}')",
                sky != blocked && blocked != wall && sky != wall);

            // ...and it clears again rather than sticking to the next good spot.
            cam.Position = new Vector3(8f, 3f, -2f);
            cam.LookAt(new Vector3(8f, 0f, -2f), Vector3.Back);
            yield return Ticks(2);
            T.Check("returning to open ground is valid again", placer.Aim(cam));
            T.Check($"...and the reason is cleared, not left over (got '{placer.Reason}')", string.IsNullOrEmpty(placer.Reason));
            yield break;
        }
    }
}
