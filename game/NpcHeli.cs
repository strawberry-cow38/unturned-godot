using Godot;

namespace UnturnedGodot
{
    // An AI-flown helicopter (strawberry 2026-08-17): "spawn at a map edge, fly towards the nearest map name node,
    // maintaining steady heights (terrain height + accounting for treetops), circling the monument".
    //
    // It flies the REAL machine through DriveHeli -- the same four stick axes a player has -- rather than animating a
    // transform along a path. So it inherits the airframe's own thrust, drag, turn authority and envelope, which is
    // what "consider the heli's stats" means: a Hind and a minicopter fly this route differently because they are
    // different aircraft, not because anything here special-cases them.
    //
    // HEIGHT COMES FROM THE TERRAIN SAMPLER, NOT A RAYCAST. Trees carry only a TRUNK collider (0.5 m radius, 8 m tall
    // -- ResourceField deliberately replaced the full-tree AABB, which floated a canopy-height cylinder above the
    // ground), and those colliders are streamed out past ~320 m by ColliderBudget. So a downward ray would sense a
    // trunk at best and bare ground most of the time, and would go blind entirely once the NPC left the player's
    // neighbourhood. Terrain.SampleHeight is exact at any range, so the canopy is handled as a CLEARANCE above it.
    public partial class NpcHeli : Node
    {
        public Vehicle Heli;
        public Terrain Terr;
        public Vector3 Target;          // the map node being flown to
        public string TargetName = "";

        // Canopy clearance. ResourceField's trunk collider is 8 m and the real canopies stand well above it, so this
        // is the margin between the terrain underneath and the aircraft -- deliberately generous, because the cost of
        // being too high is cosmetic and the cost of being too low is flying through a forest.
        public const float CanopyClearance = 34f;
        public const float OrbitRadius = 90f;      // how wide it circles the monument
        const float ArriveDist = 140f;             // switch from transit to orbit inside this

        float _hoverColl = 0.6f;

        public override void _PhysicsProcess(double delta)
        {
            if (Heli == null || !IsInstanceValid(Heli) || Heli.Exploded) return;
            float dt = (float)delta;
            Vector3 pos = Heli.GlobalPosition;

            // ---- HEIGHT: terrain under the aircraft, plus canopy clearance.
            float ground = Terr != null ? Terr.SampleHeight(pos.X, pos.Z) : 0f;
            float wantY = ground + CanopyClearance;
            float vy = Heli.LinearVelocity.Y;
            // Hover collective is thrust-dependent, so ask the airframe rather than assuming: a machine with more
            // thrust needs less stick to hold height, which is half of "consider the heli's stats".
            float hover = Heli.DebugThrust > 0.01f ? Mathf.Clamp(9.8f / Heli.DebugThrust, 0f, 1f) : 0.6f;
            _hoverColl = hover;
            float collective = Mathf.Clamp(hover + 0.055f * (wantY - pos.Y) - 0.16f * vy, 0f, 1f);

            // ---- WHERE TO GO: run the target down, then circle it.
            Vector2 here = new Vector2(pos.X, pos.Z), goal = new Vector2(Target.X, Target.Z);
            float range = here.DistanceTo(goal);
            Vector2 aim;
            if (range > ArriveDist)
            {
                aim = goal;   // transit
            }
            else
            {
                // ORBIT: steer at a point on the circle a quarter-turn ahead, so the aircraft is always chasing a
                // tangent rather than the centre. Aiming AT the monument just makes it spiral in and sit on top.
                Vector2 radial = (here - goal);
                if (radial.LengthSquared() < 1f) radial = Vector2.Right;
                radial = radial.Normalized();
                Vector2 tangent = new Vector2(-radial.Y, radial.X);
                aim = goal + (radial * OrbitRadius) + tangent * (OrbitRadius * 0.9f);
            }

            // ---- HEADING: yaw the nose onto the bearing. Turning on the PEDALS rather than banking keeps this to
            // axes whose sign convention is established (pitch positive = nose up, per HeliSpeedTests' HoldDive);
            // a rolled turn would fly better but I am not guessing at a sign on an aircraft with no self-levelling.
            Vector3 fwd = -Heli.GlobalTransform.Basis.Z;
            float headNow = Mathf.Atan2(-fwd.X, -fwd.Z);
            Vector2 toAim = aim - here;
            float headWant = Mathf.Atan2(-toAim.X, -toAim.Y);
            // SIGNS ARE MEASURED, NOT DERIVED (vehicle.heli_axis_probe). Yaw +1 drives AngularVelocity.Y NEGATIVE,
            // which DECREASES this heading convention -- so closing a positive error needs a negative stick. I
            // derived pitch off the torque expression correctly and then guessed these two; both were inverted and
            // put the aircraft in the ground on the first flight.
            float err = Mathf.Wrap(headWant - headNow, -Mathf.Pi, Mathf.Pi);
            float yaw = Mathf.Clamp(-1.2f * err + 0.45f * Heli.AngularVelocity.Y, -1f, 1f);

            // ---- SPEED, VIA A TARGET ATTITUDE. The cyclic is a RATE command, so feeding it a constant derived from
            // the speed error just rotates the machine forever: from a standing start it held -0.50 and pitched
            // straight over the top -- upY 1.00 -> 0.87 -> 0.52 -> 0.04 -> -0.70, inverted in four seconds, thrust
            // pointing at the ground. Speed error now sets a BOUNDED nose-down ANGLE, and a PD loop flies the
            // aircraft onto that angle. The bound is what makes it impossible to command past the vertical.
            float cruise = Heli.SpeedMaxMps * 0.62f;
            Vector3 flat = new Vector3(Heli.LinearVelocity.X, 0f, Heli.LinearVelocity.Z);
            float speed = flat.Length();
            float wantSpeed = range > ArriveDist ? cruise : cruise * 0.55f;   // ease off to hold the circle
            float wantNoseUpDeg = -Mathf.Clamp(0.9f * (wantSpeed - speed), -8f, 20f);   // nose-down to accelerate, capped
            float noseUpDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(fwd.Y, -1f, 1f)));
            float pitchRateDeg = Mathf.RadToDeg(Heli.AngularVelocity.Dot(Heli.GlobalTransform.Basis.X));
            float pitch = Mathf.Clamp(0.05f * (wantNoseUpDeg - noseUpDeg) - 0.015f * pitchRateDeg, -1f, 1f);

            // ---- ROLL: hold the wings level. Roll +1 puts the right wing DOWN, i.e. drives Basis.X.Y negative, so
            // levelling a right bank (bank < 0) needs a negative stick -- the proportional term is +k*bank, not -k.
            // bank_rate is -(w . forward), hence the damping term's sign.
            float bank = Heli.GlobalTransform.Basis.X.Y;
            float roll = Mathf.Clamp(2.2f * bank + 0.40f * Heli.AngularVelocity.Dot(fwd), -1f, 1f);

            LastColl = collective; LastYaw = yaw; LastPitch = pitch; LastRoll = roll; LastErr = err;
            Heli.DriveHeli(collective, yaw, pitch, roll, delta);
        }

        public float DebugWantY => Terr != null && Heli != null
            ? Terr.SampleHeight(Heli.GlobalPosition.X, Heli.GlobalPosition.Z) + CanopyClearance : 0f;
        public float DebugRange => Heli != null ? new Vector2(Heli.GlobalPosition.X - Target.X, Heli.GlobalPosition.Z - Target.Z).Length() : -1f;
        public float DebugHoverCollective => _hoverColl;
        public float LastColl, LastYaw, LastPitch, LastRoll, LastErr;   // telemetry: which axis is diverging

        /// <summary>Spawn `name` at the nearest MAP EDGE and send it to the closest named node. Returns null if the
        /// map has no nodes to fly to, which is the one case where there is nothing sensible to do.</summary>
        public static NpcHeli Spawn(Node world, string name, Terrain terr, Vector3 near)
        {
            var nodes = MapNodes.Locations;
            if (nodes == null || nodes.Count == 0) return null;

            // Nearest named node to the reference point (the player, normally).
            var best = nodes[0];
            float bestD = float.MaxValue;
            foreach (var n in nodes)
            {
                float d = new Vector2(n.Pos.X - near.X, n.Pos.Z - near.Z).LengthSquared();
                if (d < bestD) { bestD = d; best = n; }
            }

            // Map edge: push out from the node along the direction of whichever boundary is closest, so the
            // approach is a real run in across the map rather than a spawn just off-camera.
            Vector3 at;
            if (terr != null)
            {
                var (minX, maxX, minZ, maxZ) = terr.WorldBoundsXZ();
                float inset = 60f;
                float dW = Mathf.Abs(best.Pos.X - minX), dE = Mathf.Abs(maxX - best.Pos.X);
                float dS = Mathf.Abs(best.Pos.Z - minZ), dN = Mathf.Abs(maxZ - best.Pos.Z);
                float m = Mathf.Min(Mathf.Min(dW, dE), Mathf.Min(dS, dN));
                at = m == dW ? new Vector3(minX + inset, 0f, best.Pos.Z)
                   : m == dE ? new Vector3(maxX - inset, 0f, best.Pos.Z)
                   : m == dS ? new Vector3(best.Pos.X, 0f, minZ + inset)
                             : new Vector3(best.Pos.X, 0f, maxZ - inset);
                at.Y = terr.SampleHeight(at.X, at.Z) + CanopyClearance;
            }
            else at = best.Pos + new Vector3(600f, CanopyClearance, 0f);

            var v = Vehicle.BuildByName(name);
            world.AddChild(v);
            v.GlobalPosition = at;
            v.EngineOn = true;
            v.DebugInstantStart = true;
            v.SpawnRotorRunning();   // already on station: a cold 3.2 s spool-up from cruise height is a crash, not a start
            // Point it at the target and give it cruise speed, so it arrives flying rather than falling.
            Vector2 toT = new Vector2(best.Pos.X - at.X, best.Pos.Z - at.Z).Normalized();
            v.LookAt(new Vector3(best.Pos.X, at.Y, best.Pos.Z), Vector3.Up);
            v.LinearVelocity = new Vector3(toT.X, 0f, toT.Y) * (v.SpeedMaxMps * 0.5f);

            var ai = new NpcHeli { Heli = v, Terr = terr, Target = best.Pos, TargetName = best.Name };
            world.AddChild(ai);
            return ai;
        }
    }
}
