using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // Water/swim, in-engine: a player whose feet+1.25 body probe is under the ocean surface enters the SWIM
    // stance, has NO gravity (doesn't sink to the seabed), swims UP when jump is held, and takes no fall/drown
    // damage from being in the water. Retail model (PlayerMovement.cs:1134-1164, PlayerStance.cs:636-673).
    // The port's ocean is a single global plane at Terrain.SeaLevelY -- set here (STATIC, so reset at the end or
    // it would flip every later test's low-spawned player into swimming).
    public class PlayerSwimInWater : GameTest
    {
        public override string Name => "player.swim_in_water";
        public override IEnumerable<Step> Run()
        {
            bool hadWater = Terrain.HasWater;
            float oldSea = Terrain.SeaLevelY;
            Rigs.Ground(World);                       // floor at ~Y0 -- the "seabed" the player must NOT sink to
            Terrain.HasWater = true;
            Terrain.SeaLevelY = 8f;                   // ocean surface at Y8
            var p = Rigs.Player(World, new Vector3(0f, 2f, 0f));   // feet Y2: body(3.25)+eyes(3.75) both < 8 -> submerged
            yield return Ticks(6);

            T.Check($"submerged player entered SWIM (stance={p.Stance})", p.Stance == EPlayerStance.SWIM);

            // no gravity: a submerged idle player holds depth (neutral), does not sink to the seabed at Y0
            float ySub = p.GlobalPosition.Y;
            yield return Ticks(25);
            T.Check($"no gravity in water -- doesn't sink to the floor (Y {ySub:0.0} -> {p.GlobalPosition.Y:0.0})",
                    p.GlobalPosition.Y > ySub - 0.5f);

            // jump swims UP (free-swim vertical = 3 m/s)
            float yUp0 = p.GlobalPosition.Y;
            p.ScriptedJump = true;
            yield return Ticks(20);
            p.ScriptedJump = false;
            T.Check($"jump swims UP (Y {yUp0:0.0} -> {p.GlobalPosition.Y:0.0})", p.GlobalPosition.Y > yUp0 + 0.5f);

            T.Check($"no fall/drown damage from swimming (health {p.Health:0})", p.Health >= 99f);

            Terrain.HasWater = hadWater;   // MUST restore -- static leaks into every later test
            Terrain.SeaLevelY = oldSea;
        }
    }

    // SWIMMING FOLLOWS YOUR AIM, NOT THE CAMERA (merge seam, boats-integration into main).
    //
    // SwimStep took its 3D aim from `_cam.GlobalTransform.Basis`, which was correct on the branch: third person was a
    // fixed chase cam sitting straight behind you, so the camera basis and the look basis were the same thing to within
    // a few degrees. Main's third-person camera is now a real over-the-shoulder one -- 2 m back, a metre to the side,
    // toed in 5 degrees -- so the same line would swim you off at an angle to where you are aiming, in third person
    // only. Neither side's tests could see it: the branch had no OTS camera and main had no water.
    //
    // That is the shape of merge damage worth testing for -- both halves correct, the seam between them wrong -- so
    // this checks the property directly: swim forward, and you go where you LOOK.
    public class PlayerSwimFollowsAim : GameTest
    {
        public override string Name => "player.swim_follows_aim";
        public override IEnumerable<Step> Run()
        {
            bool hadWater = Terrain.HasWater;
            float oldSea = Terrain.SeaLevelY;
            Rigs.Ground(World);
            Terrain.HasWater = true;
            Terrain.SeaLevelY = 8f;
            var p = Rigs.Player(World, new Vector3(0f, 2f, 0f));
            p.DriveFP = false;   // THIRD person -- where the camera and the look part company
            yield return Ticks(20);
            T.Check($"the swimmer is in SWIM (stance={p.Stance})", p.Stance == EPlayerStance.SWIM);

            // The camera really is somewhere else, or the rest of this proves nothing.
            var camFwd = -p.Camera.GlobalBasis.Z;
            float camVsLook = Mathf.RadToDeg(camFwd.AngleTo(p.LookAxis));
            T.Check($"the 3P camera is NOT aligned with the look axis ({camVsLook:0.##} deg apart)", camVsLook > 1f);

            var from = p.GlobalPosition;
            p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);   // swim straight forward
            yield return Ticks(30);
            p.ScriptedInput = null;
            var moved = p.GlobalPosition - from;
            moved.Y = 0f;
            T.Check($"...and the swimmer actually moved ({moved.Length():0.##} m)", moved.Length() > 0.5f);
            if (moved.Length() > 0.5f)
            {
                float offLook = Mathf.RadToDeg(moved.Normalized().AngleTo(new Vector3(p.LookAxis.X, 0f, p.LookAxis.Z).Normalized()));
                float offCam = Mathf.RadToDeg(moved.Normalized().AngleTo(new Vector3(camFwd.X, 0f, camFwd.Z).Normalized()));
                T.Check($"swimming forward follows the LOOK axis ({offLook:0.##} deg off)", offLook < 2f);
                T.Check($"...rather than the camera axis ({offCam:0.##} deg off it)", offCam > offLook);
            }

            Terrain.HasWater = hadWater;
            Terrain.SeaLevelY = oldSea;
        }
    }

    // Wading: feet wet but not deep enough to swim (body probe dry) forces STAND/SPRINT and BLOCKS crouch/prone
    // (PlayerStance.cs:340-346, 865-869 -- _inShallows early-returns crouch/prone intent).
    public class PlayerWadingBlocksCrouch : GameTest
    {
        public override string Name => "player.wading_blocks_crouch";
        public override IEnumerable<Step> Run()
        {
            bool hadWater = Terrain.HasWater; float oldSea = Terrain.SeaLevelY;
            Rigs.Ground(World);                 // floor at ~Y0; the player settles to feetY 0
            Terrain.HasWater = true;
            Terrain.SeaLevelY = 1f;             // shin-deep: feet(0)<1 wet, body(0+1.25=1.25)>1 dry -> shallows, not swim
            var p = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(6);              // settle onto the floor

            T.Check($"shin-deep water is NOT swimming (stance={p.Stance})", p.Stance != EPlayerStance.SWIM);
            p.ScriptedStance = EPlayerStance.CROUCH;   // try to crouch while wading
            yield return Ticks(6);
            T.Check($"wading blocks crouch -> forced upright (stance={p.Stance})", p.Stance != EPlayerStance.CROUCH);
            p.ScriptedStance = EPlayerStance.PRONE;    // try to crawl while wading
            yield return Ticks(6);
            T.Check($"wading blocks prone/crawl (stance={p.Stance})", p.Stance != EPlayerStance.PRONE);

            p.ScriptedStance = null;
            Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea;
        }
    }

    // Can't use a weapon while swimming (source PlayerEquipment: submerged/SWIM + !canUseUnderwater blocks the use;
    // "No punching while swimming"). Guns are canUseUnderwater=false, so firing is blocked in the SWIM stance and
    // resumes once out of the water.
    public class PlayerSwimBlocksFire : GameTest
    {
        public override string Name => "player.swim_blocks_fire";
        public override IEnumerable<Step> Run()
        {
            bool hadWater = Terrain.HasWater; float oldSea = Terrain.SeaLevelY;
            Rigs.Ground(World);
            Terrain.HasWater = true;
            Terrain.SeaLevelY = 8f;                                   // ocean surface at Y8
            var p = Rigs.Player(World, new Vector3(0f, 2f, 0f));      // feet Y2 -> body/eyes submerged -> SWIM (eaglefire in hand)
            p.Ammo = 30;
            yield return Ticks(6);
            T.Check($"submerged player is swimming (stance={p.Stance})", p.Stance == EPlayerStance.SWIM);

            // fire attempts over many ticks (equip anim completes + cooldown clears) -> STILL blocked while swimming
            for (int i = 0; i < 120 && p.Stance == EPlayerStance.SWIM; i++) { p.Fire(); yield return Ticks(1); }
            T.Check($"firing is BLOCKED the whole time swimming (Ammo {p.Ammo} == 30)", p.Ammo == 30);

            // teeth/control: lower the sea far below -> not submerged -> the SAME gun now fires
            Terrain.SeaLevelY = -100f;
            yield return Ticks(6);
            T.Check($"out of the water the player is no longer swimming (stance={p.Stance})", p.Stance != EPlayerStance.SWIM);
            int shots = 0;
            for (int i = 0; i < 200 && p.Ammo == 30; i++) { if (p.Fire()) shots++; yield return Ticks(1); }
            T.Check($"out of the water the gun FIRES (Ammo {p.Ammo} < 30, {shots} shots)", p.Ammo < 30);

            Terrain.HasWater = hadWater; Terrain.SeaLevelY = oldSea;
        }
    }
}
