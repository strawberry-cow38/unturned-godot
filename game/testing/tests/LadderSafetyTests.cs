using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>THE TOP OF A LADDER (strawberry 2026-10-05: "make ladders a lot safer lol. you tend to fucking plummet when
    /// reaching the top sometimes").
    ///
    /// The ladder here is mounted the way the map mounts them: against a building, with the roof on the FAR side of the
    /// ladder from the climber. That is the case the old tests could not reach -- ladder.top_onto_roof put its roof on
    /// the climber's own side, because the ladder's solid box stood between a body and anything past it, and the
    /// flush-roof variant was deleted as "unreachable by construction". It was unreachable; it was also the common
    /// case, which is why people fell. Each variant drives the real StepLadder from the bottom with forward held.
    ///
    /// The one ladder with nothing at its top is the other half: the answer there is not a step but a HOLD.</summary>
    public abstract class LadderTopCase : GameTest
    {
        public override double TimeoutSimSeconds => 40;
        /// <summary>Roof height relative to the ladder's top, or null for a free-standing ladder (nothing to step onto).</summary>
        protected abstract float? RoofFromTop { get; }

        protected const float Top = 6.75f, LadderZ = -2f, StartZ = -1.35f;

        protected (StaticBody3D ladder, StaticBody3D building) BuildScene()
        {
            Rigs.Ground(World);
            var basis = new Basis(Vector3.Right, Mathf.DegToRad(270f));   // WorldBuilder's stand-up: thin axis -> world Z
            var ladder = new StaticBody3D { CollisionLayer = 1u << 0 };
            ladder.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.15f, 0.15f, Top) } });
            World.AddChild(ladder);
            ladder.GlobalTransform = new Transform3D(basis, new Vector3(0f, Top * 0.5f, LadderZ));
            ladder.SetMeta(Ladder.Meta, ladder);
            StaticBody3D building = null;
            if (RoofFromTop is float off)
            {
                float roofY = Top + off;
                building = new StaticBody3D { CollisionLayer = 1u << 0 };
                building.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(12f, roofY, 8f) } });
                World.AddChild(building);
                building.GlobalPosition = new Vector3(0f, roofY * 0.5f, LadderZ - 0.075f - 4f);   // its wall against the ladder's back
            }
            return (ladder, building);
        }

        protected PlayerController Climber()
        {
            var p = new PlayerController();
            World.AddChild(p);
            return p;
        }
    }

    /// <summary>A roof level with the ladder's top, past the ladder: you end up STANDING ON IT.</summary>
    public class LadderTopOverFlushRoof : LadderTopCase
    {
        public override string Name => "ladder.top_over_flush_roof";
        protected override float? RoofFromTop => 0f;

        public override IEnumerable<Step> Run()
        {
            var (ladder, building) = BuildScene();
            float roofY = Top + RoofFromTop.Value;
            var p = Climber();
            yield return Ticks(2);
            p.GlobalPosition = new Vector3(0f, 0f, StartZ); p.Rotation = Vector3.Zero;
            yield return Ticks(4);
            T.Check($"attached at the bottom ({p.Stance})", p.Stance == EPlayerStance.CLIMB);

            p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            float maxY = 0f, lowAfterHigh = float.MaxValue; bool stepped = false;
            for (int i = 0; i < 300 && !(stepped && p.Stance == EPlayerStance.STAND && p.IsOnFloor()); i++)   // 6 s
            {
                yield return Ticks(1);
                maxY = Mathf.Max(maxY, p.GlobalPosition.Y);
                if (maxY > Top * 0.5f) lowAfterHigh = Mathf.Min(lowAfterHigh, p.GlobalPosition.Y);
                stepped |= p.DebugMantling;
            }
            p.ScriptedInput = null;
            yield return Ticks(20);
            var at = p.GlobalPosition;
            GD.Print($"[ladder-safe] {Name}: at {at} stance={p.Stance} stepped={stepped} lowest-after-halfway={lowAfterHigh:0.00}");
            T.Check("it stepped off the top rather than letting go", stepped);
            T.Check($"STANDING on the roof ({p.Stance}, y {at.Y:0.00} vs roof {roofY:0.00})", p.Stance == EPlayerStance.STAND && Mathf.Abs(at.Y - roofY) < 0.15f);
            T.Check($"...on the FAR side of the ladder (z {at.Z:0.00} past the ladder's back at {LadderZ - 0.075f:0.00})", at.Z < LadderZ - 0.075f);
            T.Check($"never dropped on the way (lowest after halfway {lowAfterHigh:0.00})", lowAfterHigh > Top * 0.5f - 0.5f);
            p.QueueFree(); ladder.QueueFree(); building?.QueueFree();
        }
    }

    /// <summary>The ladder poking half a metre above the roof (how a ladder is properly mounted): the step clears the
    /// ladder's top and sets you down on the roof below it.</summary>
    public sealed class LadderTopOverParapet : LadderTopOverFlushRoof
    {
        public override string Name => "ladder.top_over_parapet";
        protected override float? RoofFromTop => -0.5f;
    }

    /// <summary>Nothing at the top. Holding forward must NOT carry you off into the air: you stay on the ladder, still,
    /// and can climb back down.</summary>
    public sealed class LadderTopHoldsFreeStanding : LadderTopCase
    {
        public override string Name => "ladder.top_holds_freestanding";
        protected override float? RoofFromTop => null;

        public override IEnumerable<Step> Run()
        {
            var (ladder, _) = BuildScene();
            var p = Climber();
            yield return Ticks(2);
            p.GlobalPosition = new Vector3(0f, 0f, StartZ); p.Rotation = Vector3.Zero;
            yield return Ticks(4);
            T.Check($"attached at the bottom ({p.Stance})", p.Stance == EPlayerStance.CLIMB);

            p.ScriptedInput = new UnityEngine.Vector2(0f, 1f);
            yield return Until(() => p.DebugLadderAtTop, 6.0);
            T.Check($"reached the top and is HOLDING there (y {p.GlobalPosition.Y:0.00}, top {Top:0.00})", p.DebugLadderAtTop);
            float minY = float.MaxValue, maxY = float.MinValue; bool left = false;
            for (int i = 0; i < 100; i++)   // 2 s more of forward at the top
            {
                yield return Ticks(1);
                minY = Mathf.Min(minY, p.GlobalPosition.Y); maxY = Mathf.Max(maxY, p.GlobalPosition.Y);
                left |= p.Stance != EPlayerStance.CLIMB;
            }
            GD.Print($"[ladder-safe] {Name}: held 2 s at the top, y {minY:0.00}..{maxY:0.00}, left the ladder: {left}");
            T.Check("still on the ladder after 2 s of pushing up at the top", !left && p.Stance == EPlayerStance.CLIMB);
            T.Check($"...and not drifting or falling (y range {maxY - minY:0.000} m)", maxY - minY < 0.05f && minY > Top - 1.0f);

            p.ScriptedInput = new UnityEngine.Vector2(0f, -1f);
            float y0 = p.GlobalPosition.Y;
            yield return Ticks(100);
            p.ScriptedInput = null;
            T.Check($"and it climbs back down from there ({y0:0.00} -> {p.GlobalPosition.Y:0.00})", p.GlobalPosition.Y < y0 - 2f);
            p.QueueFree(); ladder.QueueFree();
        }
    }

    /// <summary>Leaving a ladder (or water, or a car) unarmed must not throw a punch (strawberry 2026-10-05: "when getting
    /// off a ladder unarmed, it plays the punch animation for some reaosn"). The bare-hands guard IS the last frame of the
    /// left jab; restoring it has to SNAP there, the way equipping fists does, not play the jab from the top.</summary>
    public sealed class LadderNoJabOnExit : GameTest
    {
        public override string Name => "ladder.no_jab_on_exit";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            var vm = new Viewmodel { Fists = true };
            World.AddChild(vm);
            yield return Ticks(3);
            var arms = vm.ArmsRig;
            T.Check("the fists viewmodel has its arms", arms != null);
            if (arms == null) yield break;
            T.Check($"the jab clip is there to be (mis)played ({arms.ClipLength("Punch_Left"):0.00}s)", arms.ClipLength("Punch_Left") > 0.05f);

            foreach (var off in new[] { EPlayerStance.CLIMB, EPlayerStance.SWIM })
            {
                vm.SetLocomotion(false, off);
                yield return Ticks(1);
                string during = arms.CurrentClip;
                vm.SetLocomotion(false, EPlayerStance.STAND);
                string after = arms.CurrentClip;
                T.Check($"off a {off}: {during} -> {after}, the guard SNAPPED (Punch_Left__hold), not the jab replayed", after == "Punch_Left__hold");
            }
            vm.SetDriving(true);
            yield return Ticks(1);
            vm.SetDriving(false);
            T.Check($"out of a car: {arms.CurrentClip}", arms.CurrentClip == "Punch_Left__hold");

            // CONTROL: an item with a real raise clip still PLAYS it -- the fix is for fists, not a blanket snap.
            var knife = new Viewmodel { MeleeMesh = "knife" };
            World.AddChild(knife);
            yield return Ticks(3);
            if (knife.ArmsRig != null)
            {
                knife.SetLocomotion(false, EPlayerStance.CLIMB);
                yield return Ticks(1);
                knife.SetLocomotion(false, EPlayerStance.STAND);
                string k = knife.ArmsRig.CurrentClip ?? "";
                T.Check($"control: a melee item replays its raise ({k}), it is not snapped", !k.EndsWith("__hold") && k.Length > 0);
            }
            vm.QueueFree(); knife.QueueFree();
        }
    }
}
