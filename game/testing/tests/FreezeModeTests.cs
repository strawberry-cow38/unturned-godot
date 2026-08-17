using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // FREEZE MODE: the world must actually STOP, and the right arrow must advance it by exactly ONE physics tick.
    //
    // Both halves need proving and neither proves the other. "Nothing moved" passes on a build that froze the world
    // permanently and cannot step; "it moved after a step" passes on a build that never froze at all. So the subject
    // is a body in free fall, measured three ways: still while frozen, moved after one step, and moved by the SAME
    // amount a single unfrozen tick produces.
    //
    // The per-tick reference is measured from this same falling body rather than computed from g: the number that
    // matters is what the sim does in one tick, not what the arithmetic says it ought to.
    public sealed class FreezeModeTests : GameTest
    {
        public override string Name => "sim.freeze_mode";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            var fz = new FreezeMode();
            World.AddChild(fz);

            var ball = new RigidBody3D { Name = "Faller", GravityScale = 1f };
            ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.5f } });
            World.AddChild(ball);
            ball.GlobalPosition = new Vector3(0f, 400f, 0f);
            for (int i = 0; i < 40; i++) yield return Ticks(1);   // let it reach a steady fall

            // Reference: how far does this body move in ONE ordinary tick, right now?
            float before = ball.GlobalPosition.Y;
            yield return Ticks(1);
            float perTick = Mathf.Abs(ball.GlobalPosition.Y - before);
            T.Check($"the reference body is actually falling, so a tick is measurable ({perTick:0.####} m/tick)", perTick > 0.001f);

            // ---- FROZEN: nothing moves, however long we wait.
            fz.Enter(World);
            T.Check("entering freeze mode pauses the tree", GetTreePaused(fz));
            float frozenAt = ball.GlobalPosition.Y;
            for (int i = 0; i < 60; i++) yield return Ticks(1);
            float drift = Mathf.Abs(ball.GlobalPosition.Y - frozenAt);
            T.Check($"the world is STOPPED: 60 ticks of wall time move the faller {drift:0.#####} m (one live tick is {perTick:0.####})",
                drift < perTick * 0.05f);

            // ---- ONE STEP = ONE TICK.
            float stepFrom = ball.GlobalPosition.Y;
            fz.RequestStep();
            for (int i = 0; i < 12; i++) yield return Ticks(1);   // the step needs an idle frame then a physics frame
            float moved = Mathf.Abs(ball.GlobalPosition.Y - stepFrom);
            T.Check($"one step advances the sim ({moved:0.####} m against a {perTick:0.####} m tick)", moved > perTick * 0.4f);
            T.Check($"...by ONE tick, not several ({moved:0.####} m; two ticks would be about {perTick * 2f:0.####})",
                moved < perTick * 1.6f);
            T.Check($"...and it re-froze afterwards, so a step is not a resume (tree paused = {GetTreePaused(fz)}, {fz.TicksStepped} stepped)",
                GetTreePaused(fz) && fz.TicksStepped == 1);

            // ---- REPEATED STEPS accumulate one tick each, rather than the first one working and the rest not.
            float multiFrom = ball.GlobalPosition.Y;
            for (int s = 0; s < 4; s++)
            {
                fz.RequestStep();
                for (int i = 0; i < 12; i++) yield return Ticks(1);
            }
            float multi = Mathf.Abs(ball.GlobalPosition.Y - multiFrom);
            T.Check($"four more steps advance four more ticks ({multi:0.####} m, {fz.TicksStepped} total stepped)",
                fz.TicksStepped == 5 && multi > perTick * 2f && multi < perTick * 8f);

            // ---- EXIT restores a running world.
            fz.Exit();
            T.Check("leaving freeze mode unpauses the tree", !GetTreePaused(fz));
            float runFrom = ball.GlobalPosition.Y;
            for (int i = 0; i < 10; i++) yield return Ticks(1);
            T.Check($"...and the sim runs again ({Mathf.Abs(ball.GlobalPosition.Y - runFrom):0.###} m in 10 ticks)",
                Mathf.Abs(ball.GlobalPosition.Y - runFrom) > perTick);
        }

        static bool GetTreePaused(Node n) => n.GetTree().Paused;
    }
}
