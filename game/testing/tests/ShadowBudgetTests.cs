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

            GraphicsOptions.Shadows = GraphicsOptions.ShadowQuality.High;
            budget.Rebalance();
            int maxLights = LightShadowBudget.MaxLightsFor(GraphicsOptions.ShadowQuality.High);
            int faceCap = LightShadowBudget.FaceBudgetFor(GraphicsOptions.ShadowQuality.High);
            int perOmni = LightShadowBudget.FacesOf(lights[0]);
            int want = faceCap / perOmni;   // these are all cubes, so FACES is the binding limit, not the light count
            int on = 0; foreach (var l in lights) if (l.ShadowEnabled) on++;
            T.Check($"never exceeds the light cap ({budget.HoldingCountForTest} held, cap {maxLights})",
                    budget.HoldingCountForTest <= maxLights);
            T.Check($"never exceeds the FACE cap ({budget.FacesHeldForTest} faces, cap {faceCap})",
                    budget.FacesHeldForTest <= faceCap);
            T.Check($"and actually uses it ({on} of 12 candidates lit, {perOmni} faces each -> room for {want})",
                    on == want);

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

    /// <summary>The budget is denominated in shadow-map FACES, not lights, because that is what the renderer bills.
    ///
    /// A shadowed spot renders one depth map and a shadowed omni renders a cube of six. Counting lights therefore
    /// let the true cost of a full budget swing 6x depending on nothing but which fixtures you were standing near.
    /// The check that matters is the one a light-count budget CANNOT make: given identical geometry, a field of
    /// spots must be allowed strictly more shadow-casters than a field of cubes, and neither may exceed the face
    /// ceiling. Both scenes below are the same twelve positions -- only the light TYPE differs.</summary>
    public class ShadowBudgetChargesByFace : GameTest
    {
        public override string Name => "light.shadow_budget_charges_by_face";

        static TLight Lamp<TLight>(Node parent, Vector3 at) where TLight : Light3D, new()
        {
            var l = new TLight { LightEnergy = 1f, ShadowEnabled = false };
            parent.AddChild(l);
            l.GlobalPosition = at;
            l.AddToGroup(LightShadowBudget.Group);
            return l;
        }

        public override IEnumerable<Step> Run()
        {
            var prevQ = GraphicsOptions.Shadows;
            GraphicsOptions.Shadows = GraphicsOptions.ShadowQuality.Ultra;
            int faceCap = LightShadowBudget.FaceBudgetFor(GraphicsOptions.ShadowQuality.Ultra);
            int maxLights = LightShadowBudget.MaxLightsFor(GraphicsOptions.ShadowQuality.Ultra);

            T.Check($"a spot costs 1 face", LightShadowBudget.FacesOf(new SpotLight3D()) == 1);
            T.Check($"a cube omni costs 6", LightShadowBudget.FacesOf(new OmniLight3D()) == 6);
            T.Check($"a dual-paraboloid omni costs 2",
                    LightShadowBudget.FacesOf(new OmniLight3D { OmniShadowMode = OmniLight3D.ShadowMode.DualParaboloid }) == 2);
            T.Check($"an unrecognised light is assumed EXPENSIVE, never cheap ({LightShadowBudget.FacesOf(new DirectionalLight3D())})",
                    LightShadowBudget.FacesOf(new DirectionalLight3D()) == 6);

            var cam = new Camera3D { Current = true };
            World.AddChild(cam);
            cam.LookAtFromPosition(Vector3.Zero, new Vector3(0, 0, -10), Vector3.Up);

            // Scene A: twelve cubes.
            var omnis = new List<OmniLight3D>();
            for (int i = 1; i <= 12; i++) omnis.Add(Lamp<OmniLight3D>(World, new Vector3(0, 0, -2f * i)));
            var b1 = new LightShadowBudget();
            World.AddChild(b1);
            yield return Step.Ticks(2);
            b1.Rebalance();
            int omniHeld = b1.HoldingCountForTest, omniFaces = b1.FacesHeldForTest;
            foreach (var l in omnis) l.QueueFree();
            b1.QueueFree();
            yield return Step.Ticks(2);

            // Scene B: the same twelve positions as spots.
            var spots = new List<SpotLight3D>();
            for (int i = 1; i <= 12; i++) spots.Add(Lamp<SpotLight3D>(World, new Vector3(0, 0, -2f * i)));
            var b2 = new LightShadowBudget();
            World.AddChild(b2);
            yield return Step.Ticks(2);
            b2.Rebalance();
            int spotHeld = b2.HoldingCountForTest, spotFaces = b2.FacesHeldForTest;

            T.Check($"cubes are throttled by faces, not by the light cap ({omniHeld} held, light cap {maxLights})",
                    omniHeld == faceCap / 6 && omniHeld < maxLights);
            T.Check($"neither scene exceeds the face ceiling (cubes {omniFaces}, spots {spotFaces}, cap {faceCap})",
                    omniFaces <= faceCap && spotFaces <= faceCap);
            T.Check($"cheap lights earn more casters than dear ones ({spotHeld} spots vs {omniHeld} cubes)",
                    spotHeld > omniHeld);
            // The teeth: a light-COUNT budget would hand both scenes the same number and this would fail. It is
            // also the assertion that fails if FacesOf ever returns a constant, which is the tempting simplification.
            T.Check($"spots are capped by the LIGHT limit once faces stop binding ({spotHeld} held, cap {maxLights})",
                    spotHeld == maxLights);

            GraphicsOptions.Shadows = prevQ;
            b2.QueueFree(); cam.QueueFree();
            foreach (var l in spots) l.QueueFree();
        }
    }
}
