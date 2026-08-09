using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Ladder attach rules, ported from PlayerStance.simulate.
    ///
    /// The axis is the whole risk, and it caught me rather than the naive port. Retail tests
    /// `collider.transform.up`; I reasoned that our ex=270 stand-up sends mesh Y to world -Z and "corrected"
    /// it to GlobalBasis.Z -- which is VERTICAL and refuses every ladder in the map. A basis COLUMN already
    /// means "where this node's local axis points" under any rotation, so retail's axis needed no translation
    /// at all. That is why this runs against a real Ladder_Metal_0 under the bases WorldBuilder actually
    /// builds, at five of the yaws the map really uses, instead of a hand-built box that would have agreed
    /// with whichever answer I picked.</summary>
    public class LadderAttachRules : GameTest
    {
        public override string Name => "ladder.attach_rules";
        static string Dir => ProjectSettings.GlobalizePath("res://content/objects/");

        public override IEnumerable<Step> Run()
        {
            // --- the axis, off the real asset + a real placement ------------------------------------
            var mesh = ObjMesh.Load(Dir + "Ladder_Metal_0.obj");
            if (mesh == null) { T.Check("Ladder_Metal_0.obj loads", false); yield break; }
            var ab = mesh.GetAabb();
            T.Check($"the rip's thin axis is mesh Y ({ab.Size.X:0.00} x {ab.Size.Y:0.00} x {ab.Size.Z:0.00})",
                    ab.Size.Y < ab.Size.X && ab.Size.Y < ab.Size.Z);

            // WorldBuilder's basis for a placement at euler (270, ey, 0): Yaw(180-ey) * Pitch(270) * Roll(0).
            foreach (float ey in new[] { 0f, 90f, 104f, 270f, 315f })
            {
                var basis = new Basis(Vector3.Up, Mathf.DegToRad(180f - ey))
                          * new Basis(Vector3.Right, Mathf.DegToRad(270f));
                var body = new Node3D { Transform = new Transform3D(basis, Vector3.Zero) };
                World.AddChild(body);
                yield return Step.Ticks(1);

                var face = Ladder.FaceAxis(body);
                // The face normal of an upright ladder is HORIZONTAL whatever the yaw. Reading the wrong basis
                // column gives the ladder's LONG axis, which is vertical -> this fails, which is how it found my bug.
                T.Check($"yaw {ey:0}: face axis is horizontal (y {face.Y:+0.000;-0.000})", Mathf.Abs(face.Y) <= Ladder.SlopeDot);
                // And the mesh's LONG axis must come out vertical, confirming the stand-up went the right way.
                var longAxis = (basis * Vector3.Back).Normalized();   // mesh +Z after the pitch
                T.Check($"yaw {ey:0}: the ladder stands up (long axis y {Mathf.Abs(longAxis.Y):0.00})",
                        Mathf.Abs(longAxis.Y) > 0.99f);

                // Facing the ladder head-on = climbable; brushing the edge = not.
                T.Check($"yaw {ey:0}: head-on is climbable", Ladder.IsClimbable(-face, face));
                T.Check($"yaw {ey:0}: from behind is climbable too (both faces)", Ladder.IsClimbable(face, face));
                var edge = face.Cross(Vector3.Up).Normalized();
                T.Check($"yaw {ey:0}: the edge is NOT", !Ladder.IsClimbable(edge, face));
                body.QueueFree();
            }

            // --- sloped ladders are refused ----------------------------------------------------------
            var tilted = new Vector3(0.3f, 0.9f, 0f).Normalized();   // face axis tipped well off horizontal
            T.Check("a sloped ladder is refused even head-on", !Ladder.IsClimbable(-tilted, tilted));

            // --- the climb point --------------------------------------------------------------------
            // Snaps to the LADDER's x/z, not the hit's -- looking at the far side of the rungs must still
            // centre you. Asserting only the height would miss that entirely.
            var ladderOrigin = new Vector3(10f, 0f, -4f);
            var hit = new Vector3(10.45f, 6.2f, -4.05f);            // off-centre on the face
            var normal = new Vector3(0f, 0f, 1f);
            var cp = Ladder.ClimbPoint(ladderOrigin, hit, normal);
            T.Check($"centres on the ladder in X ({cp.X:0.00} vs ladder {ladderOrigin.X:0.00}, hit {hit.X:0.00})",
                    Mathf.Abs(cp.X - ladderOrigin.X) < 1e-3f);
            T.Check($"and stands off the face ({cp.Z:0.00} = ladder {ladderOrigin.Z:0.00} + {Ladder.OutOffset:0.00})",
                    Mathf.Abs(cp.Z - (ladderOrigin.Z + Ladder.OutOffset)) < 1e-3f);
            T.Check($"and sits below the hit ({cp.Y:0.00} = {hit.Y:0.00} - {Ladder.DropBelowHit:0.00})",
                    Mathf.Abs(cp.Y - (hit.Y - Ladder.DropBelowHit)) < 1e-3f);

            // --- speed ------------------------------------------------------------------------------
            T.Check($"full forward climbs at 2.25 m/s ({Ladder.ClimbVelocity(1f):0.00})",
                    Mathf.Abs(Ladder.ClimbVelocity(1f) - 2.25f) < 1e-3f);
            T.Check($"back-input descends ({Ladder.ClimbVelocity(-1f):0.00})", Ladder.ClimbVelocity(-1f) < -2f);
            T.Check("no input, no movement", Mathf.Abs(Ladder.ClimbVelocity(0f)) < 1e-6f);

            T.Check("both catalogue ladders are recognised",
                    Ladder.IsLadderProp("Ladder_Metal_0") && Ladder.IsLadderProp("Ladder_Wood_0"));
            T.Check("and a non-ladder is not", !Ladder.IsLadderProp("Lighthouse_0"));
        }
    }

    /// <summary>End to end: a player walked into a real ladder attaches, climbs, and gets off at the top.
    ///
    /// The unit test above checks the RULES; this checks they are actually wired to something. The door
    /// feature on this project shipped fully built and completely unreachable because every test exercised the
    /// model and nothing exercised the path a player takes, so a ladder that passes IsClimbable and never gets
    /// consulted is the exact failure worth spending a slow test on.</summary>
    public class LadderClimbEndToEnd : GameTest
    {
        public override string Name => "ladder.climb_end_to_end";

        public override IEnumerable<Step> Run()
        {
            // Ground.
            var floor = new StaticBody3D { CollisionLayer = 1u << 0 };
            floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60f, 1f, 60f) } });
            World.AddChild(floor);
            floor.GlobalPosition = new Vector3(0f, -0.5f, 0f);

            // A ladder, built exactly as WorldBuilder builds one: the raw rip's proportions, stood up by the
            // placement pitch, tagged with the meta, facing +Z.
            var basis = new Basis(Vector3.Right, Mathf.DegToRad(270f));   // stood up, yaw 0 -> face normal along Z
            var ladder = new StaticBody3D { CollisionLayer = 1u << 0 };
            ladder.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.16f, 0.15f, 6.76f) } });
            World.AddChild(ladder);
            ladder.GlobalTransform = new Transform3D(basis, new Vector3(0f, 3.38f, -2f));
            ladder.SetMeta(Ladder.Meta, ladder);

            var p = new PlayerController();
            World.AddChild(p);
            yield return Step.Ticks(2);
            p.GlobalPosition = new Vector3(0f, 0f, -1.35f);            // within the 0.75 m probe of the face
            p.Rotation = Vector3.Zero;                                  // forward is -Basis.Z, so yaw 0 looks at the ladder
            yield return Step.Ticks(4);

            T.Check($"attaches when facing it ({p.Stance})", p.Stance == EPlayerStance.CLIMB);
            float y0 = p.GlobalPosition.Y;

            // Hold forward and climb. 2.25 m/s, so ~0.5 s of ticks should gain well over half a metre.
            p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            yield return Step.Ticks(30);
            float gained = p.GlobalPosition.Y - y0;
            T.Check($"climbing gains height ({gained:0.00} m)", gained > 0.5f);
            T.Check($"still attached partway up ({p.Stance})", p.Stance == EPlayerStance.CLIMB);

            // And DOWN, which a sign error in ClimbVelocity would break while "gains height" still passed.
            float yUp = p.GlobalPosition.Y;
            p.ScriptedInput = new UnityEngine.Vector2(0f, -1f);
            yield return Step.Ticks(15);
            T.Check($"and descends ({p.GlobalPosition.Y - yUp:+0.00;-0.00} m)", p.GlobalPosition.Y < yUp - 0.2f);

            // Turn away: the probe misses and the ladder lets go. This is the dismount, and nothing else tests it.
            p.ScriptedInput = new UnityEngine.Vector2(0f, 0f);
            p.Rotation = new Vector3(0f, Mathf.DegToRad(180f), 0f);   // turn away from the ladder
            yield return Step.Ticks(4);
            T.Check($"turning away drops you off ({p.Stance})", p.Stance != EPlayerStance.CLIMB);

            // The clearance check must still REFUSE. I had to inset it off the floor (the climb point sits at
            // foot level, so a flush capsule collided with the ground the ladder stands on and blocked every
            // attach). An inset big enough to fix that could easily be big enough to stop blocking anything --
            // so wall off the climb point and require a refusal.
            var blocker = new StaticBody3D { CollisionLayer = 1u << 0 };
            blocker.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3f, 3f, 0.4f) } });
            World.AddChild(blocker);
            blocker.GlobalPosition = new Vector3(0f, 1.5f, -1.5f);   // exactly where ClimbPoint puts you
            p.GlobalPosition = new Vector3(0f, 0f, -1.35f);
            p.Rotation = Vector3.Zero;
            yield return Step.Ticks(6);
            T.Check($"a blocked climb point refuses the attach ({p.Stance})", p.Stance != EPlayerStance.CLIMB);

            p.QueueFree(); ladder.QueueFree(); floor.QueueFree(); blocker.QueueFree();
        }
    }
}
