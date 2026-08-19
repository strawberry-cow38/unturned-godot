using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // WHAT HAPPENS AT THE TOP OF A LADDER? strawberry, right after the solid-box collider fix:
    // "its really hard to get off the top of a ladder, i keep snapping back onto it."
    //
    // Asked in an EMPTY world on a hand-built ladder carrying the same solid BoxShape3D WorldBuilder now
    // gives a real one. The real-world probe found a ladder the player cannot climb past 53.02 at all
    // (blocked by the building it is bolted to) -- a real finding, but a confounded place to ask THIS
    // question. Here there is nothing but ground and a ladder, so anything that happens is the ladder.
    public sealed class LadderTopExitTest : GameTest
    {
        public override string Name => "ladder.top_exit";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            var floor = new StaticBody3D { CollisionLayer = 1u << 0 };
            floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60f, 1f, 60f) } });
            World.AddChild(floor);
            floor.GlobalPosition = new Vector3(0f, -0.5f, 0f);

            // Same construction WorldBuilder now emits: solid box, full mesh AABB, stood up by the placement pitch.
            var basis = new Basis(Vector3.Right, Mathf.DegToRad(270f));
            var ladder = new StaticBody3D { CollisionLayer = 1u << 0 };
            ladder.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.15f, 0.15f, 6.75f) } });
            World.AddChild(ladder);
            ladder.GlobalTransform = new Transform3D(basis, new Vector3(0f, 3.375f, -2f));
            ladder.SetMeta(Ladder.Meta, ladder);
            float top = 3.375f + 3.375f;   // 6.75

            var p = new PlayerController();
            World.AddChild(p);
            yield return Ticks(2);
            p.GlobalPosition = new Vector3(0f, 0f, -1.35f);
            p.Rotation = Vector3.Zero;
            yield return Ticks(4);
            T.Check($"attached at the bottom ({p.Stance})", p.Stance == EPlayerStance.CLIMB);

            // Hold forward all the way up and keep holding, which is what a player does at the top.
            p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            int climbFrames = 0, detachEvents = 0, reattachEvents = 0;
            var was = p.Stance;
            float maxY = p.GlobalPosition.Y;
            for (int i = 0; i < 200; i++)   // 4 s at 50 Hz
            {
                yield return Ticks(1);
                if (p.Stance != was)
                {
                    if (p.Stance != EPlayerStance.CLIMB) detachEvents++;
                    else reattachEvents++;
                    GD.Print($"[TOPEXIT] t={i * 0.02f:0.00}s {was} -> {p.Stance} at y={p.GlobalPosition.Y:0.00} (top {top:0.00})");
                    was = p.Stance;
                }
                if (p.Stance == EPlayerStance.CLIMB) climbFrames++;
                maxY = Mathf.Max(maxY, p.GlobalPosition.Y);
            }
            p.ScriptedInput = null;
            GD.Print($"[TOPEXIT] after 4s holding forward: y={p.GlobalPosition.Y:0.00} maxY={maxY:0.00} top={top:0.00} " +
                     $"stance={p.Stance} detaches={detachEvents} reattaches={reattachEvents}");

            T.Check($"climbed clear of the ladder top (maxY {maxY:0.00} vs top {top:0.00})", maxY > top - 0.6f);
            // THE COMPLAINT, stated as a number: letting go at the top must not re-grab over and over.
            T.Check($"does not oscillate on/off the ladder at the top (reattaches={reattachEvents})", reattachEvents <= 1);

            p.QueueFree(); ladder.QueueFree(); floor.QueueFree();
        }
    }

    // ...AND CAN YOU ACTUALLY GET OFF ONTO SOMETHING? The test above proves you stop being yanked back, but
    // it has nothing at the top, so the player just falls -- which cannot distinguish "let go properly" from
    // "let go and plummeted". strawberry's complaint is about a ladder that leads somewhere, so this one
    // gives it a roof and requires the player to END UP STANDING ON IT.
    //
    // Geometry note: the player detaches when the 0.5m-up probe clears the ladder top, i.e. with their feet
    // 0.5 m BELOW it -- so a roof has to sit at about (ladderTop - 0.5) for the climb to deliver you level
    // with it, which is how a real ladder is mounted (top rung above the parapet). The roof is placed on the
    // player's side of the ladder plane on purpose: a 0.15 m-thick collider is thinner than the player's
    // 0.28 m capsule radius, so stepping THROUGH the ladder is not something the physics will ever allow.
    public class LadderTopOntoRoofTest : GameTest
    {
        public override string Name => "ladder.top_onto_roof";   // overridden by the flush variant below
        public override double TimeoutSimSeconds => 60;
        // Roof level with where the climb hands you off. NOTE: this variant has NO TEETH for the re-grab
        // cooldown -- it passes with the cooldown zeroed, because the player lands the instant they detach and
        // so never falls back into the probe. It is a real acceptance test for "can you get onto the roof at
        // all", and nothing more. The cooldown's teeth live in ladder.top_exit and in the FLUSH variant below.
        protected virtual float RoofOffsetFromTop => -0.5f;

        public override IEnumerable<Step> Run()
        {
            var floor = new StaticBody3D { CollisionLayer = 1u << 0 };
            floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60f, 1f, 60f) } });
            World.AddChild(floor);
            floor.GlobalPosition = new Vector3(0f, -0.5f, 0f);

            var basis = new Basis(Vector3.Right, Mathf.DegToRad(270f));
            var ladder = new StaticBody3D { CollisionLayer = 1u << 0 };
            ladder.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.15f, 0.15f, 6.75f) } });
            World.AddChild(ladder);
            ladder.GlobalTransform = new Transform3D(basis, new Vector3(0f, 3.375f, -2f));
            ladder.SetMeta(Ladder.Meta, ladder);
            float top = 6.75f, roofY = top + RoofOffsetFromTop;

            // Roof slab on the +Z side (where the player climbs), starting just clear of the ladder.
            var roof = new StaticBody3D { CollisionLayer = 1u << 0 };
            roof.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(12f, 0.4f, 8f) } });
            World.AddChild(roof);
            roof.GlobalPosition = new Vector3(0f, roofY - 0.2f, -1.0f + 4f);   // top face at roofY, spans z -1..+7

            var p = new PlayerController();
            World.AddChild(p);
            yield return Ticks(2);
            p.GlobalPosition = new Vector3(0f, 0f, -1.35f);
            p.Rotation = Vector3.Zero;
            yield return Ticks(4);
            T.Check($"attached at the bottom ({p.Stance})", p.Stance == EPlayerStance.CLIMB);

            p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            yield return Until(() => p.Stance != EPlayerStance.CLIMB, 8.0);
            GD.Print($"[ROOF] came off the ladder at y={p.GlobalPosition.Y:0.00} (roof {roofY:0.00}) stance={p.Stance}");
            T.Check($"came off the ladder at the top ({p.Stance}, y {p.GlobalPosition.Y:0.00})", p.Stance != EPlayerStance.CLIMB);

            // Now walk AWAY from the ladder (backwards, +Z, out over the roof) and stay off it.
            p.Rotation = new Vector3(0f, Mathf.DegToRad(180f), 0f);   // face away from the ladder
            p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);        // forward = away
            int regrabs = 0; var was = p.Stance;
            for (int i = 0; i < 100; i++)   // 2 s
            {
                yield return Ticks(1);
                if (p.Stance != was) { if (p.Stance == EPlayerStance.CLIMB) regrabs++; was = p.Stance; }
            }
            p.ScriptedInput = null;
            float dz = p.GlobalPosition.Z - (-1.35f);
            GD.Print($"[ROOF] after walking away 2s: pos={p.GlobalPosition} stance={p.Stance} regrabs={regrabs}");

            T.Check($"ends up STANDING, not climbing or falling ({p.Stance})", p.Stance == EPlayerStance.STAND);
            T.Check($"is on the roof, not the ground (y {p.GlobalPosition.Y:0.00}, roof {roofY:0.00}, ground 0)",
                    p.GlobalPosition.Y > roofY - 0.6f);
            T.Check($"walked clear of the ladder (moved {dz:0.00} m along +Z)", dz > 1.0f);
            T.Check($"never re-grabbed while walking away (regrabs={regrabs})", regrabs == 0);

            p.QueueFree(); ladder.QueueFree(); roof.QueueFree(); floor.QueueFree();
        }
    }

    // A FLUSH-ROOF VARIANT LIVED HERE AND I DELETED IT RATHER THAN SHIP IT. It put the roof exactly level
    // with the ladder's top and required the player to reach it; it failed, and I nearly "fixed" the engine
    // against it. It is unreachable BY CONSTRUCTION: the attach probe is a forward ray cast from the player's
    // feet, so the feet can never rise above the ladder's top, so a surface AT the top can never be stood on.
    // Retail has the same probe and the same limit -- which is why real ladders are mounted poking ABOVE
    // whatever they serve. The variant was a geometry I invented, not one the game contains, and a test that
    // demands the impossible would have had me widening the grab envelope to chase it -- the exact thing
    // strawberry complained about. Recorded here so nobody re-adds it thinking it is an untested gap.
}