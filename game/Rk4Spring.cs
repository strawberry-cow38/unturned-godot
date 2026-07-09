using Godot;

namespace UnturnedGodot
{
    // Exact ports of SDG.Unturned.Rk4Spring2 / Rk4Spring3 (RK4-integrated damped springs with a 0.05s
    // substep clamp). Unturned drives the first-person viewmodel motion with these: the walk BOB rides a
    // Rk4Spring2 (viewmodelMovementOffset) and the per-shot recoil SHAKE rides a Rk4Spring3
    // (recoilViewmodelCameraOffset). The integrator math here is line-for-line the source; only the
    // stiffness/damping are Inspector-serialized on the Player prefab in the original (not in the scripts),
    // so those two numbers are tuned on our side — everything else (the motion, the amplitudes, the gun's
    // shake values) is source-exact.
    public struct Rk4Spring2
    {
        public Vector2 CurrentPosition;
        public Vector2 TargetPosition;
        public float Stiffness;
        public float Damping;
        Vector2 _vel;

        public Rk4Spring2(float stiffness, float damping)
        {
            CurrentPosition = Vector2.Zero; TargetPosition = Vector2.Zero;
            Stiffness = stiffness; Damping = damping; _vel = Vector2.Zero;
        }

        public void Update(float dt)
        {
            while (dt > 0.05f) { Step(0.05f); dt -= 0.05f; }
            if (dt > 0f) Step(dt);
        }

        void Step(float dt)
        {
            var d0 = Eval(0f, default);
            var d1 = Eval(dt * 0.5f, d0);
            var d2 = Eval(dt * 0.5f, d1);
            var d3 = Eval(dt, d2);
            Vector2 dv = (1f / 6f) * (d0.Vel + 2f * (d1.Vel + d2.Vel) + d3.Vel);
            Vector2 da = (1f / 6f) * (d0.Acc + 2f * (d1.Acc + d2.Acc) + d3.Acc);
            CurrentPosition += dv * dt;
            _vel += da * dt;
        }

        Deriv Eval(float dt, Deriv d)
        {
            Vector2 pos = CurrentPosition + d.Vel * dt;
            Vector2 vel = _vel + d.Acc * dt;
            return new Deriv { Vel = vel, Acc = Stiffness * (TargetPosition - pos) - Damping * vel };
        }

        struct Deriv { public Vector2 Vel, Acc; }
    }

    public struct Rk4Spring3
    {
        public Vector3 CurrentPosition;
        public Vector3 TargetPosition;
        public float Stiffness;
        public float Damping;
        Vector3 _vel;

        public Rk4Spring3(float stiffness, float damping)
        {
            CurrentPosition = Vector3.Zero; TargetPosition = Vector3.Zero;
            Stiffness = stiffness; Damping = damping; _vel = Vector3.Zero;
        }

        public void Update(float dt)
        {
            while (dt > 0.05f) { Step(0.05f); dt -= 0.05f; }
            if (dt > 0f) Step(dt);
        }

        void Step(float dt)
        {
            var d0 = Eval(0f, default);
            var d1 = Eval(dt * 0.5f, d0);
            var d2 = Eval(dt * 0.5f, d1);
            var d3 = Eval(dt, d2);
            Vector3 dv = (1f / 6f) * (d0.Vel + 2f * (d1.Vel + d2.Vel) + d3.Vel);
            Vector3 da = (1f / 6f) * (d0.Acc + 2f * (d1.Acc + d2.Acc) + d3.Acc);
            CurrentPosition += dv * dt;
            _vel += da * dt;
        }

        Deriv Eval(float dt, Deriv d)
        {
            Vector3 pos = CurrentPosition + d.Vel * dt;
            Vector3 vel = _vel + d.Acc * dt;
            return new Deriv { Vel = vel, Acc = Stiffness * (TargetPosition - pos) - Damping * vel };
        }

        struct Deriv { public Vector3 Vel, Acc; }
    }
}
