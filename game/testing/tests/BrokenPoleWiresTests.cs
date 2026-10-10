using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>A smashed power pole has to drop its wires (strawberry 2026-10-10: "work on destruction for
    /// the smaller power lines, wires should disconnect from broken poles"). The pole was already a
    /// destructible and already left logs; the wires simply never heard about it, so a broken pole left two
    /// spans hanging in the air off a patch of rubble.
    ///
    /// ⭐ THE RESTORE DIRECTION IS HALF THE TEST, as it is for the streetlight next door: Rubble_Reset
    /// respawns the prop, so breaking must be REVERSIBLE. The spans stay in the list and only their drawing
    /// is skipped -- which is also why SpanCount is asserted UNCHANGED across a break. A version that deleted
    /// the spans would pass every "the wires are gone" check here and quietly lose the line forever.
    ///
    /// ⚠ WHAT THIS DOES NOT COVER: that WorldBuilder passes this lambda for a Power_Line_0. It wires the
    /// destructible exactly the way WorldBuilder does -- same NotifyPoleBroken call -- but the `isPole`
    /// predicate and the captured position live in the prop loop, which has no headless entry point. Same
    /// boundary the streetlight test has.</summary>
    public sealed class BrokenPoleDropsWires : GameTest
    {
        public override string Name => "props.broken_pole_drops_wires";

        /// <summary>PEI's placement basis for a pole, standing it up. Yaw 0 runs the line along Z.</summary>
        static Transform3D PoleAt(Vector3 p) =>
            new Transform3D(new Basis(Vector3.Up, 0f) * new Basis(Vector3.Right, Mathf.DegToRad(270f)), p);

        public override IEnumerable<Step> Run()
        {
            var field = new PowerLineField();
            World.AddChild(field);
            yield return Ticks(1);

            var midAt = Vector3.Zero;
            int a = field.AddPole(PoleAt(new Vector3(0f, 0f, -32f)), PowerLineField.PoleMesh);
            int b = field.AddPole(PoleAt(midAt), PowerLineField.PoleMesh);
            int c = field.AddPole(PoleAt(new Vector3(0f, 0f,  32f)), PowerLineField.PoleMesh);
            T.Check("a three-pole run strings two spans",
                    field.Connect(a, b, out _) && field.Connect(b, c, out _));
            field.Rebuild();
            yield return Ticks(1);
            int spansBefore = field.SpanCount;
            T.Check($"both are drawn ({field.WireNodeCount} wire node(s))", field.WireNodeCount == 2);

            // Wired exactly the way WorldBuilder.PlaceObject now does for a Power_Line_0.
            var dest = new DestructibleField();
            var body = new StaticBody3D { CollisionLayer = 1u << 0 };
            World.AddChild(body);
            var mesh = new MeshInstance3D { Mesh = new BoxMesh() };
            World.AddChild(mesh);
            field.Rebuild();
            dest.Register(0, body, new[] { mesh }, 275f, 300L, 0,
                          alive => PowerLineField.NotifyPoleBroken(midAt, !alive));

            // ---- BREAK THE MIDDLE POLE: both spans touch it, so both go -----------------------------------
            dest.SetAlive(0, false);
            yield return Ticks(1);
            T.Check($"breaking the middle pole drops BOTH its wires ({field.WireNodeCount})",
                    field.WireNodeCount == 0);
            T.Check($"...and the pole reads broken", field.IsPoleBroken(b));
            // ⭐⭐ THE REVERSIBILITY CONTROL. "No wires" is also what deleting the spans looks like.
            T.Check($"...but the spans are KEPT, not deleted ({field.SpanCount} of {spansBefore})",
                    field.SpanCount == spansBefore);

            // ---- AND THE RESPAWN PUTS THEM BACK -----------------------------------------------------------
            dest.SetAlive(0, true);
            yield return Ticks(1);
            T.Check($"the rubble reset restrings both ({field.WireNodeCount})", field.WireNodeCount == 2);
            T.Check("...and the pole reads whole again", !field.IsPoleBroken(b));

            // ---- AN END POLE TAKES ONLY ITS OWN SPAN ------------------------------------------------------
            // ⚠ The span count alone cannot tell "the right one went" from "a random one went", so the
            // SURVIVING span is identified by the two poles still unbroken.
            T.Check("breaking an end pole matches exactly one field",
                    PowerLineField.NotifyPoleBroken(new Vector3(0f, 0f, -32f), true) == 1);
            yield return Ticks(1);
            T.Check($"...and only ITS span goes ({field.WireNodeCount} left)", field.WireNodeCount == 1);
            T.Check("...the broken end is the one that broke", field.IsPoleBroken(a) && !field.IsPoleBroken(c));
            PowerLineField.NotifyPoleBroken(new Vector3(0f, 0f, -32f), false);
            yield return Ticks(1);
            T.Check($"...and it comes back ({field.WireNodeCount})", field.WireNodeCount == 2);

            // ---- CONTROL: a break where no pole stands must do NOTHING -------------------------------------
            // Without this, a NotifyPoleBroken that matched everything would pass every check above.
            int none = PowerLineField.NotifyPoleBroken(new Vector3(500f, 0f, 500f), true);
            yield return Ticks(1);
            T.Check($"CONTROL: breaking empty ground matches no pole ({none})", none == 0);
            T.Check($"CONTROL: ...and leaves every wire up ({field.WireNodeCount})", field.WireNodeCount == 2);

            // ⭐ CONTROL: a field that has left the tree must not be notified -- RegionStreamer frees one per
            // region as you walk away, and a stale entry in the registry is a use-after-free waiting to happen.
            field.QueueFree();
            yield return Ticks(2);
            T.Check("CONTROL: a freed field is dropped from the live registry",
                    PowerLineField.NotifyPoleBroken(midAt, true) == 0);
        }
    }
}
