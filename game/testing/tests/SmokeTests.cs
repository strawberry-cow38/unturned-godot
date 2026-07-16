using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Tier-0 smoke: the host boots, a sandbox exists, and physics ticks advance. Runs first so a broken harness
    // surfaces before any feature test (Factorio's simplest-failing-case-first).
    public class SmokeEngineBoots : GameTest
    {
        public override string Name => "smoke.engine_boots";
        public override int Tier => 0;
        public override IEnumerable<Step> Run()
        {
            T.Check("sandbox + scene tree present", World != null && Tree != null);
            T.Check("arithmetic sanity", 2 + 2 == 4);
            yield return Ticks(3);   // physics actually steps
            T.Check("advanced 3 physics ticks", true);
        }
    }
}
