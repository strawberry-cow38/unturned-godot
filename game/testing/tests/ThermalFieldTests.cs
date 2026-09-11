using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Layer 3 of the temperature work, the half that needs an engine: which sources a point can actually FEEL.
    // The falloff curve is unit-tested in ThermalFalloffTests; everything here is about range and line of
    // sight, which is where a silent failure lives -- a source that is quietly always blocked reads exactly
    // like a world with no fires in it.
    public class ThermalFieldLineOfSight : GameTest
    {
        public override string Name => "thermal.field_line_of_sight";
        public override double TimeoutSimSeconds => 20;

        static StaticBody3D Wall(Node parent, Vector3 at, Vector3 size)
        {
            var body = new StaticBody3D { Position = at, CollisionLayer = 1u << 0, CollisionMask = 0 };
            var cs = new CollisionShape3D { Shape = new BoxShape3D { Size = size } };
            body.AddChild(cs);
            parent.AddChild(body);
            return body;
        }

        public override IEnumerable<Step> Run()
        {
            var root = new Node3D();
            World.AddChild(root);
            yield return Ticks(2);

            var fire = new Node3D { Position = new Vector3(0f, 1f, 0f) };
            root.AddChild(fire);
            var src = ThermalSource.AttachTo(fire, deltaC: 30f, radius: 6f);
            yield return Ticks(2);

            float near = ThermalField.NetC(root, new Vector3(1f, 1f, 0f));
            T.Check($"a fire 1 m away warms you ({near:0.0} C)", near > 5f);

            float far = ThermalField.NetC(root, new Vector3(20f, 1f, 0f));
            T.Check($"out of range is exactly nothing ({far:0.000})", Mathf.IsZeroApprox(far));

            // The edge is OFF, not faint -- the falloff reaches 0 at the radius and the range check is >=.
            float rim = ThermalField.NetC(root, new Vector3(6f, 1f, 0f));
            T.Check($"at the rim it is off ({rim:0.000})", Mathf.IsZeroApprox(rim));

            // A COOLER is the same node with the opposite sign.
            src.DeltaC = -30f;
            yield return Ticks(2);
            float chilled = ThermalField.NetC(root, new Vector3(1f, 1f, 0f));
            T.Check($"a cooling source cools ({chilled:0.0} C)", chilled < -5f);
            src.DeltaC = 30f;

            // INACTIVE contributes nothing: an unlit campfire is scenery.
            src.Active = false;
            yield return Ticks(2);
            T.Check("an inactive source is silent", Mathf.IsZeroApprox(ThermalField.NetC(root, new Vector3(1f, 1f, 0f))));
            src.Active = true;
            yield return Ticks(2);

            // A WALL between you and the fire blocks it.
            var wall = Wall(root, new Vector3(1.5f, 1f, 0f), new Vector3(0.3f, 4f, 8f));
            yield return Ticks(4);
            float shaded = ThermalField.NetC(root, new Vector3(3f, 1f, 0f));
            T.Check($"a wall between you and the fire blocks it ({shaded:0.000})", Mathf.IsZeroApprox(shaded));

            // ...and standing on the SAME side as the fire still works, so the wall test above is not just
            // "the ray always hits something".
            float sameSide = ThermalField.NetC(root, new Vector3(0.5f, 1f, 0f));
            T.Check($"the same wall does not block a fire you are beside ({sameSide:0.0} C)", sameSide > 5f);
            wall.QueueFree();
            yield return Ticks(4);

            // THE SELF-OCCLUSION CASE. A fire pit sunk into its own collider, or a stove inside a cabinet:
            // the ray from the player ends INSIDE geometry belonging to the source. Without the tolerance at
            // the far end this reads as blocked and the source silently switches off -- a bug that looks
            // exactly like "heat sources do not work" and has no error to grep for.
            Wall(root, new Vector3(0f, 1f, 0f), new Vector3(1.2f, 1.2f, 1.2f));
            yield return Ticks(4);
            float buried = ThermalField.NetC(root, new Vector3(2f, 1f, 0f));
            T.Check($"a source inside its own body still warms you ({buried:0.0} C)", buried > 1f);
        }
    }
}
