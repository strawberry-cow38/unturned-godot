using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // WHERE THE INTERACTION TRACE STARTS (strawberry: "base the interaction lookatradius sphere off a straight line
    // based off the relevant lean shoulder (right shoulder is default if none held)", then "in 1p use the exiating
    // focus point").
    //
    // Two different origins for two different situations, and the split is the interesting part. In FIRST person the
    // camera IS the eye, so the existing origin was already right and moving it to the shoulder would buy nothing but
    // 0.2 m of parallax between the crosshair and whatever lights up. In THIRD person the camera is 2 m behind you and
    // a metre to one side -- not a place a person can see from -- so it would happily reach through the wall you are
    // stood against. Hence: shoulder in third, camera in first.
    //
    // Everything here asks the production selector (LookTrace) rather than restating the rule. A test that restates it
    // agrees with itself whichever of the two is wrong, which is precisely the mistake that let a bad screen normal and
    // a bad lean capsule both ship green.
    public sealed class InteractOriginTests : GameTest
    {
        public override string Name => "look.interact_origin";
        public override double TimeoutSimSeconds => 40;

        public override IEnumerable<Step> Run()
        {
            // ---- WHICH SHOULDER, engine-free. Note _lean is +1 for LEFT (source's convention) while the shoulder
            // sign is +1 for RIGHT, so this is a flip and not a passthrough -- easy to get backwards, cheap to pin.
            T.Check($"not leaning -> the RIGHT shoulder ({PlayerController.ShoulderSideFor(0)})",
                PlayerController.ShoulderSideFor(0) == 1);
            T.Check($"leaning left -> the LEFT shoulder ({PlayerController.ShoulderSideFor(1)})",
                PlayerController.ShoulderSideFor(1) == -1);
            T.Check($"leaning right -> the RIGHT shoulder ({PlayerController.ShoulderSideFor(-1)})",
                PlayerController.ShoulderSideFor(-1) == 1);

            var floor = new StaticBody3D { CollisionLayer = 1u << 0, CollisionMask = 0, Position = new Vector3(0f, -0.5f, 0f) };
            floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(200f, 1f, 200f) } });
            World.AddChild(floor);
            var p = new PlayerController { CaptureMouse = false, Position = new Vector3(0f, 0.2f, 0f) };
            World.AddChild(p);
            yield return Ticks(80);

            // ---- FIRST PERSON KEEPS THE EXISTING FOCUS POINT.
            p.DriveFP = true;
            yield return Ticks(40);
            var fp = p.LookTrace();
            T.Check($"in FIRST person the trace still starts at the camera ({fp.From.DistanceTo(p.Camera.GlobalPosition):0.###} m off)",
                fp.From.DistanceTo(p.Camera.GlobalPosition) < 0.01f);
            T.Check($"...i.e. NOT at the shoulder ({fp.From.DistanceTo(p.ShoulderWorld):0.##} m from it)",
                fp.From.DistanceTo(p.ShoulderWorld) > 0.15f);

            // ---- THIRD PERSON STARTS AT THE SHOULDER.
            p.DriveFP = false;
            yield return Ticks(60);
            var tp = p.LookTrace();
            T.Check($"in THIRD person it starts at the shoulder ({tp.From.DistanceTo(p.ShoulderWorld):0.###} m off)",
                tp.From.DistanceTo(p.ShoulderWorld) < 0.01f);
            // The claim that actually matters: it is NOT the camera. Everything else is bookkeeping.
            T.Check($"...metres away from the camera ({tp.From.DistanceTo(p.Camera.GlobalPosition):0.##} m)",
                tp.From.DistanceTo(p.Camera.GlobalPosition) > 1.5f);
            T.Check($"...and it is ON the player, not floating behind them ({p.GlobalPosition.DistanceTo(tp.From):0.##} m from the feet)",
                p.GlobalPosition.DistanceTo(tp.From) < 2f);

            var local = p.GlobalTransform.AffineInverse() * tp.From;
            T.Check($"...on the RIGHT by default ({local.X:0.##} m across)", local.X > 0.1f);
            T.Check($"...below the eyes, where a shoulder is ({p.EyesWorld.Y - tp.From.Y:0.##} m down)",
                p.EyesWorld.Y - tp.From.Y > 0.1f);
            T.Check($"...aimed straight down the look axis ({Mathf.RadToDeg(tp.Dir.AngleTo(p.LookAxis)):0.###} deg off)",
                tp.Dir.AngleTo(p.LookAxis) < 0.01f);

            // ---- AND IT FOLLOWS THE LEAN. This is what makes it worth doing at all: peek round the corner and the
            // trace goes with you, the same way the shot does.
            float rightX = local.X;
            p.ScriptedLean = 1;   // hold Q -- past the shoulder-tap window, so it really leans
            yield return Ticks(40);
            var leaned = p.LookTrace();
            var leanedLocal = p.GlobalTransform.AffineInverse() * leaned.From;
            T.Check($"leaning left moves the trace to the LEFT shoulder ({leanedLocal.X:0.##} m, was {rightX:0.##})",
                leanedLocal.X < -0.1f);
            // ...and the lean itself carries it further out than the shoulder swap alone would, because the origin
            // hangs off the lean pivot. Without that it would flip sides on the spot and never actually peek.
            T.Check($"...and the lean swings it clear of the body ({Mathf.Abs(leanedLocal.X):0.##} m vs {Mathf.Abs(rightX):0.##} upright)",
                Mathf.Abs(leanedLocal.X) > Mathf.Abs(rightX) + 0.1f);

            p.ScriptedLean = 0;
            yield return Ticks(40);
            var back = p.GlobalTransform.AffineInverse() * p.LookTrace().From;
            T.Check($"releasing puts it back on the right ({back.X:0.##} m)", back.X > 0.1f);

            yield break;
        }
    }
}
