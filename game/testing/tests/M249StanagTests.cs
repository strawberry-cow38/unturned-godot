using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>The M249 really does take a STANAG magazine, through the real catalog and the real fit rule.
    ///
    /// master 2026-10-05: "make the m249 take stanag magazines as well as its own box mags", then on the model:
    /// "a flag on the mag and a flag on the gun too".
    ///
    /// ⭐ THE L0 TESTS PROVE THE RULE; THIS PROVES THE WIRING, and they fail differently. AcceptsMagazine can be
    /// perfectly correct while ItemCatalog never sets magStandardPattern on item 6 or gunTakesStandardMags on
    /// item 132 -- and that looks identical to the feature not existing. Same shape as the carjack clip whose
    /// fallback length happened to match the real one.
    ///
    /// So it asks the REAL assets rather than numbers typed into the test, and ends on the attachment ring's own
    /// predicate, because a magazine the reload accepts and the ring refuses to show cannot be loaded.</summary>
    public class M249TakesStanag : GameTest
    {
        public override string Name => "items.m249_takes_stanag";

        const ushort SawId = 132, StanagId = 6, Blk300Id = 9142, EaglefireId = 4, AugId = 201;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            yield return Ticks(1);

            var saw = Assets.find(SawId);
            var stanag = Assets.find(StanagId);
            var blk = Assets.find(Blk300Id);
            var eagle = Assets.find(EaglefireId);
            T.Check("the M249 (132) is in the catalog", saw != null);
            T.Check("the Military Magazine (6) is in the catalog", stanag != null);
            T.Check("the .300 Blackout Magazine (9142) is in the catalog", blk != null);
            T.Check("the Eaglefire (4) is in the catalog", eagle != null);
            if (saw == null || stanag == null || blk == null || eagle == null) yield break;

            UnturnedGodot.Log.Print($"[m249] {saw.itemName}: group {saw.gunCaliber}, cartridge '{saw.gunCaliberName}', takesStandard={saw.gunTakesStandardMags}");
            UnturnedGodot.Log.Print($"[m249] {stanag.itemName}: group {stanag.magCaliber}, round '{stanag.magRound}', standard={stanag.magStandardPattern}");

            // THE FLAGS ARE ACTUALLY SET -- the half that can silently not happen.
            T.Check("the Military Magazine is flagged STANDARD pattern", stanag.magStandardPattern);
            T.Check("the M249 is flagged as taking standard-pattern magazines", saw.gunTakesStandardMags);
            T.Check("the M249 knows its own cartridge", !string.IsNullOrEmpty(saw.gunCaliberName));

            T.Check("the M249 still takes its own 200-round box group", saw.AcceptsMagazineCaliber(saw.gunCaliber));
            T.Check("...and now takes the STANAG magazine", saw.AcceptsMagazine(stanag));

            // ⭐ THE DISCRIMINATOR, on real data: 9142 is a STANAG-pattern BODY in a different cartridge. If the
            // rule had dropped the cartridge test this would pass and the belt-fed would eat subsonic .300.
            UnturnedGodot.Log.Print($"[m249] {blk.itemName}: group {blk.magCaliber}, round '{blk.magRound}', standard={blk.magStandardPattern}");
            T.Check("control: the .300 BLK magazine is the same STANDARD body...", blk.magStandardPattern && blk.magCaliber == stanag.magCaliber);
            T.Check("...and the M249 REFUSES it, because the cartridge is wrong", !saw.AcceptsMagazine(blk));

            // CONTROLS: the exception must not become a general amnesty in either direction.
            T.Check("control: the Eaglefire takes STANAG", eagle.AcceptsMagazine(stanag));
            T.Check("control: ...but NOT the M249's proprietary box", !eagle.AcceptsMagazineCaliber(saw.gunCaliber));

            // The proprietary-fed guns must still refuse STANAG -- "Does not interchange with STANAG" is authored
            // into their catalog rows and a cartridge-only rule would have quietly undone all three.
            var aug = Assets.find(AugId);
            if (aug != null && aug.type == EItemType.GUN)
            {
                UnturnedGodot.Log.Print($"[m249] {aug.itemName}: group {aug.gunCaliber}, takesStandard={aug.gunTakesStandardMags}");
                T.Check($"control: {aug.itemName} (proprietary magwell) still refuses STANAG", !aug.AcceptsMagazine(stanag));
            }

            // ...and the ring agrees with the reload. The second is the teeth: with no gun asset the call falls
            // back to plain group equality and refuses, so the pass above came from the flags.
            T.Check("the attachment ring offers STANAG for the M249",
                    AttachmentFit.Fits(stanag, "Magazine", saw.gunCaliber, saw.gunCaliberName, saw));
            T.Check("control: with no gun asset the same call refuses it",
                    !AttachmentFit.Fits(stanag, "Magazine", saw.gunCaliber, saw.gunCaliberName, null));
        }
    }
}
