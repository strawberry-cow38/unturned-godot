using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // THE GUN-RAIL TACTICAL ATTACHMENTS (strawberry 2026-09-13: "give the tactical laser an actual laser beam.
    // toggles on/off with N. wire the tactical flashlight too. identical to the flashlight, just on N and
    // attached to the gun").
    //
    // Three things here can be wrong in a way that LOOKS right, and each one gets a check that fails on it:
    //
    //   1. A laser that draws a beam but does not RAYCAST -- a fixed-length line out of the gun looks exactly
    //      like a working sight until you point it at a wall. So the dot's position is asserted against the
    //      wall's real surface, not against "a node exists".
    //   2. A rail light that is a lookalike instead of THE flashlight. Both .dats declare a bare Light key and
    //      no SpotLight_* overrides, so the two beams must be the same numbers; a hand-typed 60/80/1.5 would
    //      render as a perfectly good torch and quietly not be the one the item says it is.
    //   3. N eating the key. The rail has four attachments and only two of them switch anything -- a fitted
    //      rangefinder or bayonet must fall THROUGH to the goggles rather than swallowing the press.
    public sealed class TacticalRailTests : GameTest
    {
        public override string Name => "attachments.tactical_rail";
        public override double TimeoutSimSeconds => 30;

        const ushort LaserId = 151, LightId = 152, RangefinderId = 1008, BayonetId = 1438, RifleId = 4;

        static Item Rifle(int tacticalId)
        {
            var it = new Item(RifleId);
            AttachmentFit.SetInstalledId(it, "Tactical", tacticalId);
            return it;
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            if (p.Camera == null) { T.Check("the rig built a camera to aim with", false); yield break; }

            // ---- 0. THE TABLE, read off the five shipped Tacticals/*.dat --------------------------------------
            T.Check("151 is a laser", AttachmentFit.IsLaser(LaserId) && !AttachmentFit.IsTacticalLight(LaserId));
            T.Check("152 is a light", AttachmentFit.IsTacticalLight(LightId) && !AttachmentFit.IsLaser(LightId));
            T.Check($"the laser is retail RED ({AttachmentFit.LaserColor(LaserId)}) -- Tactical_Laser.dat declares no Laser_Color",
                    AttachmentFit.LaserColor(LaserId).R > 0.9f && AttachmentFit.LaserColor(LaserId).G < 0.1f
                 && AttachmentFit.LaserColor(LaserId).B < 0.1f);
            T.Check("both of them are toggleable",
                    AttachmentFit.TacticalToggleable(LaserId) && AttachmentFit.TacticalToggleable(LightId));
            // THE CONTROL for the whole key-priority rule below. A rangefinder and a bayonet are real fitted
            // tactical attachments with no ON state; if they answered "toggleable" they would eat N for ever and
            // the goggles would stop working with one on the rail, which is not something you would look for.
            T.Check("a RANGEFINDER is not", !AttachmentFit.TacticalToggleable(RangefinderId));
            T.Check("a BAYONET is not", !AttachmentFit.TacticalToggleable(BayonetId));
            T.Check("and an empty rail is not", !AttachmentFit.TacticalToggleable(0));

            // ---- 1. THE LIGHT IS THE FLASHLIGHT, not a lookalike ----------------------------------------------
            p.EquipHeldGun("eaglefire", Rifle(LightId));
            yield return Until(() => p.HeldItemReady, 6);
            T.Check($"a rifle with a rail LIGHT is in hand (tactical={p.TacticalId})", p.TacticalId == LightId);
            T.Check("...and N has something to switch", p.HasTacticalToggle);
            T.Check("the lamp is OFF until it is switched on", p.DebugTacticalLight == null || !p.DebugTacticalLight.Visible);

            p.ToggleTactical();
            yield return Ticks(2);
            var lamp = p.DebugTacticalLight;
            T.Check("N lit the rail lamp", p.TacticalLightOn && lamp != null && lamp.Visible);
            if (lamp != null)
            {
                // ⚠ EQUALITY, not "roughly a torch". Tactical_Light.dat and flashlight.dat both declare a bare
                // `Light` and nothing else, so both fall through to PlayerSpotLightConfig's defaults -- the claim
                // is that they are the SAME numbers, and only equality can fail on a hand-typed near-miss.
                T.Check($"...at the flashlight's range ({lamp.SpotRange} == {MeleeDef.DefaultSpotRange})",
                        Mathf.IsEqualApprox(lamp.SpotRange, MeleeDef.DefaultSpotRange));
                T.Check($"...its energy ({lamp.LightEnergy} == {MeleeDef.DefaultSpotIntensity})",
                        Mathf.IsEqualApprox(lamp.LightEnergy, MeleeDef.DefaultSpotIntensity));
                T.Check($"...its warm colour ({lamp.LightColor})",
                        lamp.LightColor.IsEqualApprox(MeleeDef.DefaultSpotColor));
                // THE HALVING. Godot's SpotAngle is the HALF-angle and the .dat states the FULL cone; getting
                // this wrong doubles the beam and reads as a bug in the light rather than in the units, which is
                // exactly what it did to the handheld torch once already.
                T.Check($"...and its cone HALVED for Godot ({lamp.SpotAngle} == {MeleeDef.DefaultSpotAngleFull} / 2)",
                        Mathf.IsEqualApprox(lamp.SpotAngle, MeleeDef.DefaultSpotAngleFull * 0.5f));
            }
            T.Check("a LIGHT draws no beam", p.DebugLaser == null || !p.DebugLaser.Lit);

            p.ToggleTactical();
            yield return Ticks(2);
            T.Check("N again switches it off", !p.TacticalLightOn && (p.DebugTacticalLight == null || !p.DebugTacticalLight.Visible));

            // ---- 2. THE LASER RAYCASTS -------------------------------------------------------------------------
            // A wall placed ON the camera's own forward axis, at a distance nothing else in the scene shares, so
            // "the dot is 6 m away" can only have come from hitting THIS.
            Vector3 eye = p.Camera.GlobalPosition;
            Vector3 aim = -p.Camera.GlobalTransform.Basis.Z;
            const float WallDist = 6f;
            var wall = new StaticBody3D { CollisionLayer = 1u << 0 };
            wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(14f, 14f, 0.4f) } });
            World.AddChild(wall);
            wall.GlobalPosition = eye + aim * WallDist;
            wall.LookAt(eye, Vector3.Up);   // face the player, so the slab's 0.4 m is along the aim
            yield return Ticks(2);

            p.EquipHeldGun("eaglefire", Rifle(LaserId));
            yield return Until(() => p.HeldItemReady, 6);
            T.Check($"a rifle with a rail LASER is in hand (tactical={p.TacticalId})", p.TacticalId == LaserId);
            T.Check("the beam is dark until it is switched on", p.DebugLaser == null || !p.DebugLaser.Lit);

            p.ToggleTactical();
            yield return Ticks(3);
            var las = p.DebugLaser;
            T.Check("N lit the beam", p.TacticalLaserOn && las != null && las.Lit);
            if (las != null && las.Lit)
            {
                eye = p.Camera.GlobalPosition; aim = -p.Camera.GlobalTransform.Basis.Z;
                float along = (las.DotWorld - eye).Dot(aim);
                float lateral = (las.DotWorld - eye - aim * along).Length();
                // ⭐ THE CLAIM. The dot sits on the WALL's face, roughly WallDist out (less the 0.2 m half-slab
                // and retail's 5 cm surface lift), and dead on the aim axis. A beam of fixed length, or one that
                // never raycast at all, lands somewhere else on both counts.
                T.Check($"the dot landed ON the wall, not at a fixed range ({along:0.00} m out, wall at {WallDist})",
                        along > WallDist - 1f && along < WallDist);
                T.Check($"...and dead on the aim axis ({lateral:0.000} m off)", lateral < 0.05f);
                T.Check($"the beam spans gun -> dot ({las.BeamLength:0.00} m vs {(las.DotWorld - las.BeamFrom).Length():0.00})",
                        Mathf.IsEqualApprox(las.BeamLength, (las.DotWorld - las.BeamFrom).Length(), 0.02f));
                // The beam starts at the GUN and the ray at the EYE -- deliberately different points (the same
                // split the tracer documents). If they were collapsed into one, this distance would be zero and
                // the beam would have no gun under it.
                T.Check($"...and starts at the gun, not at the eye ({(las.BeamFrom - eye).Length():0.000} m apart)",
                        (las.BeamFrom - eye).Length() > 0.05f);
            }

            // ---- 3. PUTTING THE GUN AWAY KILLS IT --------------------------------------------------------------
            // TacticalOn is `switch AND attachment`, the same shape as HeadlampOn, so this needs no clear call at
            // any of the dozen equip paths -- which is the point, and is what this check is really testing.
            p.EquipHeldMelee("machete");
            yield return Ticks(3);
            T.Check("holstering the rifle drops the beam", !p.TacticalLaserOn && (p.DebugLaser == null || !p.DebugLaser.Lit));
            T.Check("...and N has nothing to switch with a machete out", !p.HasTacticalToggle);

            // ---- 4. RE-EQUIPPING RESETS THE SWITCH, which is retail: `interact` is a field on UseableGun and
            // UseableGun is rebuilt on every equip, so a laser you left on is off when you draw the gun again.
            p.EquipHeldGun("eaglefire", Rifle(LaserId));
            yield return Until(() => p.HeldItemReady, 6);
            yield return Ticks(2);
            T.Check("re-drawing the rifle brings it back OFF", !p.TacticalLaserOn && (p.DebugLaser == null || !p.DebugLaser.Lit));

            if (GodotObject.IsInstanceValid(wall)) wall.QueueFree();
            yield return Ticks(1);
        }
    }
}
