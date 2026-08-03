using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // WHICH ATTACHMENTS THE T-MENU OFFERS (strawberry: "actually consider player inventory and which attachments they
    // have and which apply to each slot ... like source does").
    //
    // The menu used to cycle a hardcoded list of four sights on every gun regardless of what you carried. The failure
    // mode of the replacement is quieter and worse: a filter that returns nothing, on every slot, forever. It looks
    // identical to "you aren't carrying any attachments" and there is no error to notice. So these tests assert both
    // directions -- the right things appear AND the wrong things don't.
    public sealed class AttachmentFitTests : GameTest
    {
        public override string Name => "gun.attachment_fit";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();

            // ---- The type layer. Before SIGHT/BARREL/GRIP/TACTICAL existed in EItemType these all parsed to GENERIC,
            // so no filter could tell a scope from a rock. Asserted on the real catalog rows, not on hand-built assets.
            T.Check($"Eaglefire Iron Sights (5) is a SIGHT ({Assets.find(5)?.type})", Assets.find(5)?.type == EItemType.SIGHT);
            T.Check($"Military Suppressor (7) is a BARREL ({Assets.find(7)?.type})", Assets.find(7)?.type == EItemType.BARREL);
            T.Check($"Vertical Grip (8) is a GRIP ({Assets.find(8)?.type})", Assets.find(8)?.type == EItemType.GRIP);
            T.Check($"Military Magazine (6) is a MAGAZINE ({Assets.find(6)?.type})", Assets.find(6)?.type == EItemType.MAGAZINE);

            int sights = 0;
            foreach (var a in Assets.all()) if (a.type == EItemType.SIGHT) sights++;
            T.Check($"the catalog now exposes its sights instead of burying them in GENERIC ({sights})", sights > 20);

            // ---- The rule itself, off the real assets.
            var eagle = Assets.find(4);                      // Eaglefire, caliber 1
            var gun = GunDef.FromDatText(System.IO.File.ReadAllText(
                ProjectSettings.GlobalizePath("res://content/eaglefire.dat")));
            T.Check($"eaglefire is caliber {gun.Caliber}", gun.Caliber == 1);

            T.Check("a sight fits the Sight slot", AttachmentFit.Fits(Assets.find(5), "Sight", gun.Caliber));
            T.Check("...and NOT the Barrel slot", !AttachmentFit.Fits(Assets.find(5), "Barrel", gun.Caliber));
            T.Check("a suppressor fits Barrel", AttachmentFit.Fits(Assets.find(7), "Barrel", gun.Caliber));
            T.Check("...and not Sight", !AttachmentFit.Fits(Assets.find(7), "Sight", gun.Caliber));
            T.Check("a grip fits Grip", AttachmentFit.Fits(Assets.find(8), "Grip", gun.Caliber));
            T.Check("a gun is not an attachment", !AttachmentFit.Fits(eagle, "Sight", gun.Caliber));
            T.Check("neither is a can of beans", !AttachmentFit.Fits(Assets.find(13), "Sight", gun.Caliber));

            // MAGAZINES are the one type with real caliber data, and the rule must MATCH FindBestMag exactly -- if the
            // menu offers a magazine the reload path then refuses, you get a gun that won't load from the mag you
            // just attached, with nothing on screen explaining why.
            T.Check("a caliber-1 magazine fits the eaglefire", AttachmentFit.Fits(Assets.find(6), "Magazine", 1));
            T.Check("...and does NOT fit a caliber-2 gun", !AttachmentFit.Fits(Assets.find(6), "Magazine", 2));

            // A non-magazine with no caliber list is UNIVERSAL (retail IncludeUnspecifiedCaliber). Reading that branch
            // as "no calibers = fits nothing" leaves every sight slot permanently empty while looking like it works,
            // so it is pinned in both directions.
            AttachmentFit.Calibers.Remove(5);
            T.Check("an unspecified-caliber sight is universal -- fits a caliber-9 gun too",
                AttachmentFit.Fits(Assets.find(5), "Sight", 9));
            AttachmentFit.Calibers[5] = new ushort[] { 1 };
            T.Check("...but once it DECLARES caliber 1 it stops fitting caliber 9",
                !AttachmentFit.Fits(Assets.find(5), "Sight", 9));
            T.Check("...and still fits caliber 1", AttachmentFit.Fits(Assets.find(5), "Sight", 1));
            AttachmentFit.Calibers.Remove(5);

            // ---- The bag scan: what the menu actually shows.
            var inv = new PlayerInventory();
            inv.wearBackpack(new Item(253));
            var bag = inv.items[PlayerInventory.BACKPACK];
            bag.tryAddItem(new Item(5));    // iron sights
            bag.tryAddItem(new Item(7));    // suppressor
            bag.tryAddItem(new Item(6, 30));   // three identical magazines
            bag.tryAddItem(new Item(6, 30));
            bag.tryAddItem(new Item(6, 12));
            bag.tryAddItem(new Item(13));   // canned beans -- must never appear

            var forSight = AttachmentFit.InBag(inv, "Sight", 1);
            var forBarrel = AttachmentFit.InBag(inv, "Barrel", 1);
            var forMag = AttachmentFit.InBag(inv, "Magazine", 1);
            var forGrip = AttachmentFit.InBag(inv, "Grip", 1);

            T.Check($"the Sight slot offers exactly the one sight carried ({forSight.Count})", forSight.Count == 1 && forSight[0].Asset.id == 5);
            T.Check($"the Barrel slot offers the suppressor ({forBarrel.Count})", forBarrel.Count == 1 && forBarrel[0].Asset.id == 7);
            // Three identical magazines are ONE button saying x3, not three buttons -- a full ammo belt would
            // otherwise fan out a column taller than the screen.
            T.Check($"three identical magazines collapse to one option ({forMag.Count})", forMag.Count == 1);
            T.Check($"...counted as x3 ({(forMag.Count > 0 ? forMag[0].Count : 0)})", forMag.Count > 0 && forMag[0].Count == 3);
            T.Check($"a slot you carry nothing for offers nothing ({forGrip.Count})", forGrip.Count == 0);

            foreach (var (a, _) in forSight) T.Check($"...and no food leaked into the sight list ({a.itemName})", a.type == EItemType.SIGHT);

            // The magazines must vanish for a gun of another caliber, while the universal sight stays.
            T.Check($"a caliber-2 gun is offered none of these magazines ({AttachmentFit.InBag(inv, "Magazine", 2).Count})",
                AttachmentFit.InBag(inv, "Magazine", 2).Count == 0);
            T.Check($"...but still gets the sight ({AttachmentFit.InBag(inv, "Sight", 2).Count})",
                AttachmentFit.InBag(inv, "Sight", 2).Count == 1);

            // A null bag must degrade to "no options", not throw -- the menu opens in the --attach viewmodel harness
            // where there is no player at all.
            T.Check("a player-less menu asks an empty bag and survives", AttachmentFit.InBag(null, "Sight", 1).Count == 0);

            // ---- ATTACHMENTS ARE OBJECTS, not flags (strawberry: "irons are their own item and can be installed
            // across weapons"). Installed state lives on the GUN'S ITEM so it survives holstering and travels with
            // the weapon, and taking something off has to hand it back.
            var g1 = new Item(4);      // an Eaglefire
            T.Check($"a fresh gun has nothing recorded in its sight slot ({AttachmentFit.InstalledId(g1, "Sight")})",
                AttachmentFit.InstalledId(g1, "Sight") == -1);

            // Factory irons are DERIVED from the catalog by name, so a newly ported gun needs no extra wiring.
            T.Check($"Eaglefire's factory irons resolve to item 5 ({AttachmentFit.DefaultIronsId("Eaglefire")})",
                AttachmentFit.DefaultIronsId("Eaglefire") == 5);
            T.Check($"Timberwolf's resolve to 19 ({AttachmentFit.DefaultIronsId("Timberwolf")})",
                AttachmentFit.DefaultIronsId("Timberwolf") == 19);
            // Pistols carry their sights in the body mesh and have no separate irons item -- -1 is the right answer,
            // not a lookup failure.
            T.Check($"a pistol with no separate irons resolves to -1 ({AttachmentFit.DefaultIronsId("Cobra")})",
                AttachmentFit.DefaultIronsId("Cobra") == -1);
            T.Check("...and an unknown name doesn't throw", AttachmentFit.DefaultIronsId("Not A Gun") == -1);

            AttachmentFit.SeedDefaults(g1, "Eaglefire");
            T.Check($"seeding installs the factory irons ({AttachmentFit.InstalledId(g1, "Sight")})",
                AttachmentFit.InstalledId(g1, "Sight") == 5);
            // Seeding runs on every equip, so it must never stomp what the player fitted.
            AttachmentFit.SetInstalledId(g1, "Sight", 21);
            AttachmentFit.SeedDefaults(g1, "Eaglefire");
            T.Check($"...and re-seeding leaves a player-fitted scope alone ({AttachmentFit.InstalledId(g1, "Sight")})",
                AttachmentFit.InstalledId(g1, "Sight") == 21);

            // Each slot is its own field; writing one must not disturb the others. The Magazine slot in particular is
            // backed by the pre-existing gunMagId, so a careless mapping would silently overwrite a loaded magazine.
            var g2 = new Item(4);
            AttachmentFit.SetInstalledId(g2, "Magazine", 6);
            AttachmentFit.SetInstalledId(g2, "Barrel", 7);
            T.Check($"the Magazine slot writes through to gunMagId ({g2.gunMagId})", g2.gunMagId == 6);
            T.Check($"...and the Barrel slot didn't disturb it ({g2.gunMagId}, {g2.gunBarrelId})",
                g2.gunMagId == 6 && g2.gunBarrelId == 7);
            T.Check($"...and the sight slot is still empty ({AttachmentFit.InstalledId(g2, "Sight")})",
                AttachmentFit.InstalledId(g2, "Sight") == -1);

            // Installed state travels with the ITEM -- that is the whole point of putting it there. Two guns hold
            // their own attachments independently, which is what "installed across weapons" requires.
            var g3 = new Item(4);
            AttachmentFit.SetInstalledId(g3, "Sight", 5);
            T.Check($"a second gun keeps its own sight ({AttachmentFit.InstalledId(g3, "Sight")} vs {AttachmentFit.InstalledId(g1, "Sight")})",
                AttachmentFit.InstalledId(g3, "Sight") == 5 && AttachmentFit.InstalledId(g1, "Sight") == 21);
            yield break;
        }
    }
}
