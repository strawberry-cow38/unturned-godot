using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // DOOR SOLIDITY (strawberry: "make openable doors only solid when they are fully open or closed").
    //
    // cow tools built it, and then guarded it: snapping the leaf collider ON at an endpoint while a body stands in
    // its volume makes Jolt eject that body, sometimes through a wall -- a worse trap than the shoving the change was
    // meant to remove, and reachable by closing a door on yourself. So an endpoint now solidifies only once an
    // overlap check says the volume is clear, retrying each frame until it is.
    //
    // WHY THIS TEST EXISTS SEPARATELY FROM door.swings_and_blocks: that one asserts with IntersectRay, and a raycast
    // reads the collision layer regardless of whether a body could ever stand there. It passes identically against
    // the old always-solid door, the naive endpoint-snap, and the guarded version -- so every behaviour this feature
    // is about is invisible to it. It stayed green through both of cow tools' commits, which is exactly the problem.
    //
    // The leaf collider is on bit 6, and PlayerController's body mask is (1<<0)|(1<<6) -- so bit 6 is genuinely solid
    // to the player, not merely a look-ray target. That is what makes the eject case real rather than theoretical.
    public sealed class DoorSolidityTests : GameTest
    {
        public override string Name => "door.solid_only_at_endpoints";

        // The leaf collider is a direct child of the door body (Godot needs shapes parented straight to the body), so
        // the test can reach it without adding a debug accessor to someone else's file mid-review.
        static CollisionShape3D LeafCollider(ObjectDoor d)
        {
            foreach (var c in d.GetChildren()) if (c is CollisionShape3D cs) return cs;
            return null;
        }

        // The OBSERVABLE form of "solid": can a ray on the door's own layer hit the leaf where it stands? Asserted
        // alongside the Disabled flag rather than instead of it -- the flag is the mechanism, this is the consequence,
        // and a change that sets the flag without affecting the physics server would pass one and fail the other.
        bool SolidAt(Vector3 worldCentre)
        {
            var q = PhysicsRayQueryParameters3D.Create(
                worldCentre + new Vector3(0f, 1.6f, 0f), worldCentre - new Vector3(0f, 1.6f, 0f), 1u << 6);
            return World.GetWorld3D().DirectSpaceState.IntersectRay(q).Count > 0;
        }

        static CharacterBody3D Bystander(Vector3 pos)
        {
            var b = new CharacterBody3D
            {
                Position = pos,
                CollisionLayer = 1u << 3,   // the PLAYER bit -- what ObjectDoor.ColliderClear() looks for
                CollisionMask = 0,          // it only needs to BE detected; it isn't walking anywhere
            };
            b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.5f, 1.8f, 0.5f) } });
            return b;
        }

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);

            var leaf = new BoxMesh { Size = new Vector3(1.0f, 2.0f, 0.08f) };
            var door = ObjectDoor.Spawn(World, new Transform3D(Basis.Identity, new Vector3(0f, 1f, 0f)),
                pivotLocal: new Vector3(-0.5f, 0f, 0f), axisLocal: Vector3.Up,
                angleDeg: 90f, durationSec: 0.2f, leafMesh: leaf, leafMaterial: null);
            yield return Ticks(3);

            var col = LeafCollider(door);
            T.Check("the door built a leaf collider", col != null);
            if (col == null) yield break;

            // ---- CLOSED + EMPTY: solid. The resting state everything else is measured against.
            T.Check($"a shut door with nobody in it is solid (disabled={col.Disabled})", !col.Disabled);
            T.Check("...and a ray on its layer hits it", SolidAt(col.GlobalPosition));

            // ---- MID-SWING: not solid. This is master's actual request.
            door.Toggle();
            yield return Ticks(2);            // 0.2s swing at 60Hz -> ~12 ticks, so 2 is comfortably mid-flight
            T.Check($"a door part-way through its swing is NOT solid (disabled={col.Disabled})", col.Disabled);
            T.Check("...and a ray passes straight through where the leaf is", !SolidAt(col.GlobalPosition));

            // Stepped with Ticks, NOT Until: the swing runs in _PhysicsProcess, and Until spins on render frames --
            // it timed out here against a door that was working perfectly. The inverse of the fluid tests, where the
            // solver lives in _Process and Ticks is the wrong stepper. Match the step to where the logic runs.
            // ---- OPEN + EMPTY: solid again. THE TEETH. Without this, a door that simply never solidifies scores
            // perfectly on every other check in this file.
            yield return Ticks(40);
            T.Check($"a fully-open door with a clear volume goes solid (disabled={col.Disabled})", !col.Disabled);
            var openCentre = col.GlobalPosition;
            T.Check("...and a ray hits it at the open pose", SolidAt(openCentre));

            // ---- Now the eject case. Park a body exactly where the leaf comes to rest when open. Reading the pose
            // off the door rather than deriving it by hand: if my hinge arithmetic were wrong the body would sit
            // beside the door and the whole test would pass while proving nothing.
            door.Toggle();
            yield return Ticks(40);
            yield return Ticks(3);

            var body = Bystander(openCentre);
            World.AddChild(body);
            yield return Ticks(2);
            var stood = body.GlobalPosition;

            door.Toggle();                                   // swing it into the body
            yield return Ticks(40);
            yield return Ticks(8);                            // well past arrival, so a retry has had chances to fire

            // ---- THE ONE THAT MATTERS: at the endpoint, with someone standing in it, the door stays non-solid.
            T.Check($"an open door with a body in its volume stays NON-solid (disabled={col.Disabled})", col.Disabled);
            T.Check("...and does not become ray-solid on top of them", !SolidAt(col.GlobalPosition));

            // ...and the visible harm the guard exists to prevent: nobody gets shoved anywhere.
            float moved = body.GlobalPosition.DistanceTo(stood);
            T.Check($"...and the body standing there is NOT ejected ({moved:F3}m)", moved < 0.05f);

            // ---- CLEARS: the retry finally solidifies it. Proves the door is held, not permanently broken.
            body.GlobalPosition = stood + new Vector3(6f, 0f, 6f);
            yield return Ticks(30);
            yield return Ticks(40);
            T.Check($"once they step clear, the open door solidifies (disabled={col.Disabled})", !col.Disabled);
            T.Check("...and is ray-solid again", SolidAt(col.GlobalPosition));
        }
    }
}
