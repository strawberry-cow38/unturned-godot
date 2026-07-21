using SDG.Unturned;
using System.Collections.Generic;
using UnityEngine;

namespace UnturnedGodot.Testing
{
    // L0 unit test for the shared SentryTargeting math (source InteractableSentry.ScanForTargets) -- the pure logic
    // BOTH the client replica's aim AND the server-side ServerSentries run, so pinning it here pins both. No world:
    // synthetic candidates aimed down -Z + a plug LOS function. Muzzle at origin; a candidate at (0,0,-d) is d metres
    // dead ahead (in-arc), at (0,0,+d) is directly behind (out of the 60-degree arc).
    public class SentryTargetingRules : GameTest
    {
        public override string Name => "sentry.targeting_rules";
        public override int Tier => 0;

        static SentryTargeting.Candidate Cand(ulong id, float z, byte spec = 0, bool hunting = true)
            => new SentryTargeting.Candidate(id, new Vector3(0f, 0f, z), spec, hunting);

        public override IEnumerable<Step> Run()
        {
            var muzzle = Vector3.zero;
            var fwd = new Vector3(0f, 0f, -1f);                           // the barrel's forward (-Z)
            System.Func<Vector3, Vector3, bool> clear = (a, b) => true;   // nothing blocks
            System.Func<Vector3, Vector3, bool> blocked = (a, b) => false;

            // nearest in-arc hunting zombie wins (id 2 at 5 m beats id 1 at 12 m, both dead ahead)
            var cands = new List<SentryTargeting.Candidate> { Cand(1, -12), Cand(2, -5) };
            T.Check("picks the nearest in-arc hunting target", SentryTargeting.ChooseTarget(cands, muzzle, fwd, 48, 57.6f, 100, clear, 0) == 2);

            // idle (!hunting) zombies are ignored -> the hunting one wins even though it's farther
            cands = new List<SentryTargeting.Candidate> { Cand(1, -5, hunting: false), Cand(2, -12, hunting: true) };
            T.Check("skips idle (!hunting) zombies", SentryTargeting.ChooseTarget(cands, muzzle, fwd, 48, 57.6f, 100, clear, 0) == 2);

            // 60-degree forward arc: a zombie BEHIND the aim (+Z) is not acquired even if it's nearest
            cands = new List<SentryTargeting.Candidate> { Cand(1, 5) };
            T.Check("won't acquire a target behind the aim (forward arc)", SentryTargeting.ChooseTarget(cands, muzzle, fwd, 48, 57.6f, 100, clear, 0) == 0);

            // range: nothing past detectionRadius is acquired
            cands = new List<SentryTargeting.Candidate> { Cand(1, -60) };
            T.Check("won't acquire past detection radius", SentryTargeting.ChooseTarget(cands, muzzle, fwd, 48, 57.6f, 100, clear, 0) == 0);

            // gunRange clamps detection: a 40 m zombie with a 30 m gun isn't reached (source Min(detection, weaponRange))
            cands = new List<SentryTargeting.Candidate> { Cand(1, -40) };
            T.Check("gun range clamps detection", SentryTargeting.ChooseTarget(cands, muzzle, fwd, 48, 57.6f, 30, clear, 0) == 0);

            // keep-target is arc-EXEMPT: the acquired target is kept when it walks BEHIND (inside lossRadius)
            cands = new List<SentryTargeting.Candidate> { Cand(7, 5) };
            T.Check("keeps the current target when it moves behind (loss bubble, arc-exempt)", SentryTargeting.ChooseTarget(cands, muzzle, fwd, 48, 57.6f, 100, clear, keptId: 7) == 7);

            // ...but drops it once past lossRadius
            cands = new List<SentryTargeting.Candidate> { Cand(7, -60) };
            T.Check("drops the kept target past loss radius", SentryTargeting.ChooseTarget(cands, muzzle, fwd, 48, 57.6f, 100, clear, keptId: 7) == 0);

            // LOS gate: a world-blocked candidate is skipped
            cands = new List<SentryTargeting.Candidate> { Cand(1, -5) };
            T.Check("skips an LOS-blocked target", SentryTargeting.ChooseTarget(cands, muzzle, fwd, 48, 57.6f, 100, blocked, 0) == 0);

            // AimHeight per speciality (source ScanForTargets switch: crawler .25, sprinter 1.0, upright 1.75)
            T.Check("aim height: crawler .25", System.MathF.Abs(SentryTargeting.AimHeight(2) - 0.25f) < 1e-4f);
            T.Check("aim height: sprinter 1.0", System.MathF.Abs(SentryTargeting.AimHeight(1) - 1.0f) < 1e-4f);
            T.Check("aim height: upright 1.75", System.MathF.Abs(SentryTargeting.AimHeight(0) - 1.75f) < 1e-4f);
            yield break;
        }
    }
}
