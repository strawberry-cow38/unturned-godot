using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>The M249 really does take a STANAG magazine, through the real catalog and the real fit rule.
    ///
    /// master 2026-10-05: "make the m249 take stanag magazines as well as its own box mags".
    ///
    /// ⭐ THE L0 TESTS PROVE THE RULE; THIS PROVES THE WIRING, and they are different failures. AcceptsMagazineCaliber
    /// can be perfectly correct while ItemCatalog never populates the set on item 132, and the symptom would be
    /// identical to the feature not existing. That is the same shape as the carjack clip whose fallback length
    /// happened to match the real one: a mechanism that works on data that never arrives.
    ///
    /// So this asks the REAL assets -- item 132 and the actual Military Magazine, item 6 -- rather than numbers
    /// typed into the test, and finishes on the attachment menu's own predicate, because a magazine the reload
    /// accepts and the ring refuses to show is a magazine the player cannot load.</summary>
    public class M249TakesStanag : GameTest
    {
        public override string Name => "items.m249_takes_stanag";

        const ushort SawId = 132, StanagId = 6, EaglefireId = 4;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            yield return Ticks(1);

            var saw = Assets.find(SawId);
            var stanag = Assets.find(StanagId);
            var eagle = Assets.find(EaglefireId);
            T.Check("the M249 (132) is in the catalog", saw != null);
            T.Check("the Military Magazine (6) is in the catalog", stanag != null);
            T.Check("the Eaglefire (4) is in the catalog", eagle != null);
            if (saw == null || stanag == null || eagle == null) yield break;

            UnturnedGodot.Log.Print($"[m249] {saw.itemName} caliber {saw.gunCaliber} groups [{string.Join(",", saw.gunMagazineCalibers ?? new int[0])}]; {stanag.itemName} magCaliber {stanag.magCaliber}");

            // Read off the ASSETS, not off constants: if the rip ever renumbers a caliber this still asks the
            // right question instead of asserting a stale integer.
            T.Check("the M249 still feeds from its own box magazine group", saw.AcceptsMagazineCaliber(saw.gunCaliber));
            T.Check("the Military Magazine really is a magazine", stanag.IsMagazine);
            T.Check("...and the M249 now feeds from the STANAG group too", saw.AcceptsMagazineCaliber(stanag.magCaliber));

            // ⭐ CONTROLS. Without these, "accepts STANAG" would also pass if the set had been flattened so that
            // every gun accepts everything -- which is the easy way to get this wrong.
            T.Check("control: the Eaglefire takes STANAG", eagle.AcceptsMagazineCaliber(stanag.magCaliber));
            T.Check("control: ...but NOT the M249's 200-round box group",
                    !eagle.AcceptsMagazineCaliber(saw.gunCaliber));

            // ...and the ATTACHMENT RING agrees with the reload. The last check is the teeth: strip the set and
            // the menu falls back to plain equality and refuses, which is exactly the old behaviour.
            T.Check("the attachment ring offers STANAG for the M249",
                    AttachmentFit.Fits(stanag, "Magazine", saw.gunCaliber, null, saw.gunMagazineCalibers));
            T.Check("control: with NO set the same call refuses it (so the pass above came from the set)",
                    !AttachmentFit.Fits(stanag, "Magazine", saw.gunCaliber, null, null));
        }
    }
}
