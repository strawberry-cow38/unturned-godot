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

            // ---- THIRD PERSON STARTS AT THE CAMERA CENTRE (strawberry 2026-09-04: "make the lookatradius ball come from the
            // camera center in 3p so it lines up w crosshair" -- this supersedes the earlier shoulder rule for 3P; the
            // body is excluded from the ray so the camera-to-body gap cannot self-focus).
            p.DriveFP = false;
            yield return Ticks(60);
            var tp = p.LookTrace();
            T.Check($"in THIRD person it starts at the camera centre ({tp.From.DistanceTo(p.Camera.GlobalPosition):0.###} m off)",
                tp.From.DistanceTo(p.Camera.GlobalPosition) < 0.01f);
            T.Check($"...and points down the camera's forward (dot {tp.Dir.Dot(-p.Camera.GlobalTransform.Basis.Z):0.###})",
                tp.Dir.Dot(-p.Camera.GlobalTransform.Basis.Z) > 0.999f);
            T.Check($"...which is metres behind the shoulder ({tp.From.DistanceTo(p.ShoulderWorld):0.##} m) -- the crosshair, not the body, is the reference in 3P",
                tp.From.DistanceTo(p.ShoulderWorld) > 1.5f);

            // ---- AND IT STAYS ON THE CAMERA THROUGH A LEAN. In 3P the crosshair is the reference, so peeking round a
            // corner moves the camera (and with it the trace), never the trace off the camera.
            p.ScriptedLean = 1;   // hold Q -- past the shoulder-tap window, so it really leans
            yield return Ticks(40);
            var leaned = p.LookTrace();
            T.Check($"leaning keeps the trace on the camera centre ({leaned.From.DistanceTo(p.Camera.GlobalPosition):0.###} m off)",
                leaned.From.DistanceTo(p.Camera.GlobalPosition) < 0.01f);
            p.ScriptedLean = 0;
            yield return Ticks(40);
            var back = p.LookTrace();
            T.Check($"...and so does releasing ({back.From.DistanceTo(p.Camera.GlobalPosition):0.###} m off)",
                back.From.DistanceTo(p.Camera.GlobalPosition) < 0.01f);

            yield break;
        }
    }
}
