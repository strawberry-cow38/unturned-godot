using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>The headlight cycle and its shadows (strawberry 2026-10-10: "make the headlights cycle throw
    /// off/low(current brightness)/high(new more powerful, longer reach) make the headlights cast shadows").
    ///
    /// ⭐ LOW IS ASSERTED TO BE EXACTLY WHAT SHIPPED. "(current brightness)" is a requirement, not a
    /// description -- the first click of the cycle has to be indistinguishable from the old single state, and
    /// a high beam that quietly also brightened low would satisfy every other check here.</summary>
    public sealed class HeadlightBeam : GameTest
    {
        public override string Name => "vehicle.headlight_beam";

        static List<SpotLight3D> Spots(Vehicle v)
        {
            var found = new List<SpotLight3D>();
            void Walk(Node n) { foreach (var c in n.GetChildren()) { if (c is SpotLight3D s) found.Add(s); Walk(c); } }
            Walk(v);
            return found;
        }

        public override IEnumerable<Step> Run()
        {
            var car = Vehicle.BuildByName("jeep");
            World.AddChild(car);
            yield return Ticks(3);

            var spots = Spots(car);
            T.Check($"the jeep has headlight spots ({spots.Count})", spots.Count >= 2);
            // The taillight spots live on the same car; the headlights are the long-range ones.
            var heads = new List<SpotLight3D>();
            foreach (var s in spots) if (s.SpotRange > 20f) heads.Add(s);
            T.Check($"...of which {heads.Count} are headlights (range > 20 m)", heads.Count >= 2);

            // ---- SHADOWS -----------------------------------------------------------------------------------
            int noShadow = 0;
            foreach (var s in heads) if (!s.ShadowEnabled) noShadow++;
            T.Check($"every headlight spot casts a shadow ({noShadow} do not)", noShadow == 0);
            // ⚠ CONTROL: the omni FILL must NOT, because an omni shadow is a six-face cube map for a light
            // nobody looks at. Without this, "shadows on" is satisfied by switching them on everywhere.
            int omniShadow = 0;
            void WalkOmni(Node n) { foreach (var c in n.GetChildren()) { if (c is OmniLight3D o && o.ShadowEnabled) omniShadow++; WalkOmni(c); } }
            WalkOmni(car);
            T.Check($"CONTROL: the omni fill does not cast one ({omniShadow} do)", omniShadow == 0);

            // ---- THE CYCLE ---------------------------------------------------------------------------------
            T.Check("starts off", !car.HeadlightsOn && !car.HighBeam);

            car.ToggleHeadlights();
            yield return Ticks(1);
            T.Check("click 1 -> LOW", car.HeadlightsOn && !car.HighBeam);
            // ⭐⭐ LOW IS UNCHANGED. These are the numbers that shipped before high beam existed.
            float r = heads[0].SpotRange, e = heads[0].LightEnergy, a = heads[0].SpotAngle;
            T.Check($"...and low is exactly what it was (range {r:0.#}, energy {e:0.#}, angle {a:0.#})",
                    Mathf.IsEqualApprox(r, Vehicle.LowRange) && Mathf.IsEqualApprox(e, Vehicle.LowEnergy)
                    && Mathf.IsEqualApprox(a, Vehicle.LowAngle));

            car.ToggleHeadlights();
            yield return Ticks(1);
            T.Check("click 2 -> HIGH", car.HeadlightsOn && car.HighBeam);
            float r2 = heads[0].SpotRange, e2 = heads[0].LightEnergy, a2 = heads[0].SpotAngle;
            T.Check($"...reaches further ({r:0.#} -> {r2:0.#} m)", r2 > r * 1.5f);
            T.Check($"...and is more powerful ({e:0.#} -> {e2:0.#})", e2 > e);
            // A main beam throws further by CONCENTRATING; a wider cone would be a flood lamp.
            T.Check($"...with a tighter cone, not a wider one ({a:0.#} -> {a2:0.#} deg)", a2 < a);

            car.ToggleHeadlights();
            yield return Ticks(1);
            T.Check("click 3 -> OFF", !car.HeadlightsOn && !car.HighBeam);

            // ---- COMING BACK ON STARTS AT LOW --------------------------------------------------------------
            // ⚠ Otherwise a driver who parked on main beam blinds the next road they join.
            car.ToggleHeadlights();
            yield return Ticks(1);
            T.Check($"switching on again starts at LOW, not main beam ({heads[0].SpotRange:0.#} m)",
                    car.HeadlightsOn && !car.HighBeam
                    && Mathf.IsEqualApprox(heads[0].SpotRange, Vehicle.LowRange));

            // ---- MAIN BEAM COSTS MORE BATTERY --------------------------------------------------------------
            car.Battery = 100f;
            for (int i = 0; i < 20; i++) car.PhysicsTick(0.05);
            float lowUsed = 100f - car.Battery;
            car.ToggleHeadlights();   // -> HIGH
            car.Battery = 100f;
            for (int i = 0; i < 20; i++) car.PhysicsTick(0.05);
            float highUsed = 100f - car.Battery;
            T.Check($"main beam draws more current ({lowUsed:0.00} -> {highUsed:0.00} per second)",
                    highUsed > lowUsed * 1.2f);

            // ---- A FLAT BATTERY STILL REFUSES --------------------------------------------------------------
            car.Battery = 0f;
            car.ToggleHeadlights(); car.ToggleHeadlights(); car.ToggleHeadlights();
            yield return Ticks(1);
            T.Check("a flat battery lights nothing, whatever the cycle says", !car.HeadlightsOn);

            car.QueueFree();
        }
    }
}
