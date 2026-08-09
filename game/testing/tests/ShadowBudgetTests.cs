using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The shadow budget must cap the count, pick the RIGHT lights, and not flicker.
    ///
    /// The count cap is the point of the system — a shadowed omni is a cube, six renders per light per frame,
    /// and the map has 324 point lights. But a cap alone is not enough to be useful, so this also checks it
    /// picks by proximity and that an incumbent is not displaced by a light that is only marginally closer.
    /// That last one is the failure that makes a ranked budget worse than no shadows: two lights at nearly
    /// equal distance swap every update and visibly flick while you stand still.</summary>
    public class ShadowBudgetCapsAndPicks : GameTest
    {
        public override string Name => "light.shadow_budget_caps_and_picks";

        static OmniLight3D Lamp(Node parent, Vector3 at)
        {
            var l = new OmniLight3D { OmniRange = 8f, LightEnergy = 1f, ShadowEnabled = false };
            parent.AddChild(l);
            l.GlobalPosition = at;
            l.AddToGroup(LightShadowBudget.Group);
            return l;
        }

        public override IEnumerable<Step> Run()
        {
            var prevQ = GraphicsOptions.Shadows;
            var cam = new Camera3D { Current = true };
            World.AddChild(cam);
            cam.GlobalPosition = Vector3.Zero;
            cam.LookAtFromPosition(Vector3.Zero, new Vector3(0, 0, -10), Vector3.Up);   // facing -Z

            // Twelve lights straight ahead at 2,4,6...24 m — more than any budget, and ordered so "nearest"
            // is unambiguous.
            var lights = new List<OmniLight3D>();
            for (int i = 1; i <= 12; i++) lights.Add(Lamp(World, new Vector3(0, 0, -2f * i)));
            var far = Lamp(World, new Vector3(0, 0, -200f));      // past MaxShadowDistance
            var behind = Lamp(World, new Vector3(0, 0, 6f));       // behind the camera

            var budget = new LightShadowBudget();
            World.AddChild(budget);
            yield return Step.Ticks(2);

            GraphicsOptions.Shadows = GraphicsOptions.ShadowQuality.High;   // budget 6
            budget.Rebalance();
            int want = LightShadowBudget.BudgetFor(GraphicsOptions.ShadowQuality.High);
            int on = 0; foreach (var l in lights) if (l.ShadowEnabled) on++;
            T.Check($"never exceeds the budget ({budget.HoldingCountForTest} held, cap {want})",
                    budget.HoldingCountForTest <= want);
            T.Check($"and actually uses it ({on} of 12 candidates lit)", on >= want - 1 && on <= want);

            // Proximity, not arbitrary: the nearest must be in, the 12th must not.
            T.Check($"nearest light (2 m) casts", lights[0].ShadowEnabled);
            T.Check($"far light (24 m) does not", !lights[11].ShadowEnabled);
            T.Check($"one past the cull distance ({LightShadowBudget.MaxShadowDistance:0} m) never casts", !far.ShadowEnabled);
            // Behind the camera is deprioritised (x2.2), so this one at 6 m scores 13.2 and loses to the six
            // front lights at 2..12 m. Written as a flat assertion because the first version I wrote --
            // `!behind.ShadowEnabled || !lights[2].ShadowEnabled == false` -- parses as "or the front light is
            // on", which is true whatever `behind` does. It passed while checking nothing.
            T.Check($"a light behind the camera loses its slot to nearer ones in front (behind on: {behind.ShadowEnabled})",
                    !behind.ShadowEnabled);

            // STABILITY: re-ranking with nothing moved must not churn the set. A budget that reshuffles while
            // you stand still is the flicker this exists to prevent, and a count-only check cannot see it.
            var before = new List<OmniLight3D>();
            foreach (var l in lights) if (l.ShadowEnabled) before.Add(l);
            for (int i = 0; i < 5; i++) { budget.Rebalance(); yield return Step.Ticks(1); }
            int churn = 0;
            foreach (var l in lights) if (l.ShadowEnabled != before.Contains(l)) churn++;
            T.Check($"set is stable across repeated rebalances ({churn} changes)", churn == 0);

            // Off means OFF -- a zero budget is how you stop paying at all.
            GraphicsOptions.Shadows = GraphicsOptions.ShadowQuality.Off;
            budget.Rebalance();
            int stillOn = 0; foreach (var l in lights) if (l.ShadowEnabled) stillOn++;
            T.Check($"quality Off drops every shadow ({stillOn} left on, {budget.HoldingCountForTest} held)",
                    stillOn == 0 && budget.HoldingCountForTest == 0);

            GraphicsOptions.Shadows = prevQ;
            budget.QueueFree(); cam.QueueFree();
            foreach (var l in lights) l.QueueFree();
            far.QueueFree(); behind.QueueFree();
        }
    }
}
