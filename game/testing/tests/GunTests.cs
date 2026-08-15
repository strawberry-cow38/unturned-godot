using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // Port of --magtest: working magazines (reload swaps the fullest spare in, ammo conserved), loose shotgun
    // shells by caliber, bolt/pump per-shot rechamber, loot spawn amounts, melee .dat values, and gun-state
    // persistence on the backing item. Pure logic on a detached PlayerController.
    public class GunMagReload : GameTest
    {
        public override string Name => "gun.mag_reload";
        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            var p = new PlayerController { Inventory = new PlayerInventory() };
            p.Inventory.wearBackpack(new Item(253));   // Alicepack -> the BACKPACK page has room
            var bag = p.Inventory.items[PlayerInventory.BACKPACK];
            bag.tryAddItem(new Item(6, 30));   // 2 full + 1 partial Military mags in the bag
            bag.tryAddItem(new Item(6, 30));
            bag.tryAddItem(new Item(6, 12));

            p.LoadGun("res://content/eaglefire.dat");
            T.Check("eaglefire uses magazine items (caliber match)", p.DebugUsesMag());
            T.Check("gun starts loaded (Ammo=30)", p.Ammo == 30);
            T.Check("3 spare mags = 72 rounds in the bag", p.Inventory.getItemCount(6) == 72);
            T.Check("has a spare mag to reload from", p.DebugHasSpareMag());

            p.Ammo = 5;              // fired down to 5 (a round still chambered)
            p.DebugMagSwap();        // TACTICAL reload -> keep the chambered round
            T.Check("tactical reload keeps the chambered round (Ammo=31)", p.Ammo == 31);
            T.Check("ammo conserved: spares now 46 (72 - 30 taken + 4 back)", p.Inventory.getItemCount(6) == 46);

            p.Ammo = 0;              // fired dry -> chamber empty
            p.DebugMagSwap();        // EMPTY reload -> no +1
            T.Check("empty reload has no chambered bonus (Ammo=30, not 31)", p.Ammo == 30);

            // empty the bag of mags -> reload must be blocked
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++) { var pg = p.Inventory.items[b]; for (int i = pg.getItemCount() - 1; i >= 0; i--) if (pg.getItem((byte)i)?.item?.id == 6) pg.removeItem((byte)i); }
            T.Check("no spare mag -> cannot reload", !p.DebugHasSpareMag());

            // masterkey: a BREAK-action double-barrel feeding from loose 20 Gauge Buckshot (item 381, stack 32), no +1
            int Jars(ushort id) { int n = 0; var pg = p.Inventory.items[PlayerInventory.BACKPACK]; for (byte i = 0; i < pg.getItemCount(); i++) if (pg.getItem(i)?.item?.id == id) n++; return n; }
            p.LoadGun("res://content/masterkey.dat");
            T.Check("masterkey is a shotgun", p.DebugIsShotgun());
            T.Check("masterkey break-action is NOT shell-by-shell", !p.DebugShellReload());
            T.Check("masterkey has no +1 chamber", !p.DebugHasChamber());
            T.Check("masterkey feeds from loose shells", p.DebugUsesShells());
            T.Check("masterkey fires 8 pellets (20ga shell)", p.DebugPellets() == 8);
            bag.tryAddItem(new Item(381, 20));
            bag.tryAddItem(new Item(381, 20));   // 20 + 20 -> merges to 32, overflows 8
            T.Check("20 gauge shells stack (40 carried)", p.DebugCountShells() == 40);
            T.Check("shells cap at 32/slot (40 -> 2 stacks)", Jars(381) == 2);
            p.Ammo = 0;                 // both barrels empty
            p.DebugCompleteReload();    // one reload
            T.Check("masterkey loads BOTH barrels from the stack (Ammo=2)", p.Ammo == 2);
            T.Check("reload consumed 2 shells (40 -> 38)", p.DebugCountShells() == 38);
            bag.tryAddItem(new Item(113, 32));   // 12 Gauge (caliber 8) is a DIFFERENT ammo type
            T.Check("12 gauge shells don't count for the 20ga masterkey", p.DebugCountShells() == 38);
            p.LoadGun("res://content/bluntforce.dat");
            T.Check("bluntforce (pump) feeds from loose shells", p.DebugUsesShells());
            T.Check("bluntforce is shell-by-shell (pump)", p.DebugShellReload());
            T.Check("bluntforce sees the 12 gauge shells (32)", p.DebugCountShells() == 32);
            T.Check("bluntforce fires 6 pellets (12ga shell)", p.DebugPellets() == 6);

            // ammo-TYPE picker (R-hold radial / attachment-menu mag slot, master): choose slug vs buckshot for the 12ga
            // gun. The choice drives ShellAsset -> the fire loop's pellet count -- slug = 1 solid projectile, buckshot
            // keeps its 6. Both types must be carried to be pickable (the bag already holds 32 buckshot from above).
            bag.tryAddItem(new Item(5000, 10));   // 12 Gauge Slug (caliber 8, 1 pellet)
            T.Check("bluntforce (loose shells) can choose an ammo type", p.CanChooseShellType);
            T.Check("two 12ga shell types offered (buckshot + slug)", p.ShellTypeChoices().Count == 2);
            T.Check("default loads buckshot -> 6 pellets", p.DebugPellets() == 6);
            p.ChooseShellType(5000);   // pick slug (as the radial / mag-slot click does)
            T.Check("slug selected -> fires 1 solid projectile", p.DebugPellets() == 1);
            T.Check("HUD reflects the loaded shell type (slug)", p.LoadedShellName == "12 Gauge Slug");
            p.ChooseShellType(113);    // back to buckshot
            T.Check("buckshot re-selected -> 6 pellets again", p.DebugPellets() == 6);
            // beanbag: a THIRD 12ga type (separate id, functionally like the slug -- 1 pellet -- but tunable apart later)
            bag.tryAddItem(new Item(5002, 5));   // 12 Gauge Beanbag
            T.Check("three 12ga shell types offered (buckshot + slug + beanbag)", p.ShellTypeChoices().Count == 3);
            p.ChooseShellType(5002);
            T.Check("beanbag selected -> fires 1 projectile", p.DebugPellets() == 1);
            T.Check("HUD reflects the loaded shell type (beanbag)", p.LoadedShellName == "12 Gauge Beanbag");
            p.ChooseShellType(113);    // reset to buckshot for the checks below
            T.Check("HUD reflects the loaded shell type (buckshot)", p.LoadedShellName == "12 Gauge Buckshot");
            T.Check("ammo name pluralises for >1 (master)", PlayerController.PluralAmmo("12 Gauge Slug", 6) == "12 Gauge Slugs");
            T.Check("ammo name stays singular for 1", PlayerController.PluralAmmo("12 Gauge Slug", 1) == "12 Gauge Slug");

            // dupe guard (master): switching shell TYPES must not conjure rounds -- a loaded buckshot tube + slugs carried
            // must NOT turn the whole tube into slugs. the loaded type is one global (ShellAsset), so the fix ejects the
            // old-type rounds to the bag FIRST, then loads only slugs we truly carry.
            p.LoadGun("res://content/bluntforce.dat");     // fresh: a full buckshot tube (Ammo = AmmoMax)
            int tubeBuck = p.Ammo;
            int buckBag0 = p.Inventory.getItemCount(113);
            int slugBag0 = p.Inventory.getItemCount(5000);
            p.ChooseShellType(5000);        // switch to slug
            p.DebugCompleteReload();        // finish the fill
            T.Check("shell switch ejects the old buckshot to the bag (not converted to slugs)", p.Inventory.getItemCount(113) == buckBag0 + tubeBuck);
            T.Check("shell switch never loads more slugs than carried (no phantom mag)", p.Ammo <= slugBag0);
            T.Check("no slug dupe: loaded + still-carried == the slugs we had", p.Ammo + p.Inventory.getItemCount(5000) == slugBag0);
            p.ChooseShellType(113);         // reset to buckshot for anything downstream
            // same-type reload on a FULL shotgun is a no-op (master): don't replay the reload if the tube's already full of it
            p.LoadGun("res://content/bluntforce.dat");   // fresh: a full buckshot tube (Ammo = AmmoMax)
            int fullTube = p.Ammo;
            p.ChooseShellType(113);         // same type, tube already full -> should NOT reload
            T.Check("same-type reload on a full shotgun is a no-op (Ammo unchanged)", p.Ammo == fullTube && fullTube > 0);

            // bolt/pump per-shot rechamber (source RechamberAfterShotCount)
            p.LoadGun("res://content/timberwolf.dat");
            T.Check("timberwolf (bolt) rechambers after each shot", p.DebugRechamberCount() == 1);
            p.DebugFireRechamber();
            T.Check("after a shot the bolt gun must cycle (blocked)", p.DebugNeedsRechamber());
            p.DebugRechamberTick(0.30);   // past RechamberAfterShotDelay -> the bolt-cycle starts
            p.DebugRechamberTick(0.60);   // past the cycle -> ready again
            T.Check("after cycling the bolt gun can fire again", !p.DebugNeedsRechamber());
            p.LoadGun("res://content/eaglefire.dat");
            T.Check("eaglefire (semi) does NOT rechamber per shot", p.DebugRechamberCount() == 0);
            p.DebugFireRechamber();
            T.Check("semi-auto never needs a per-shot cycle", !p.DebugNeedsRechamber());

            // world loot: magazines spawn FULL (master)
            T.Check("military mag spawns full as loot (30 rounds)", Assets.makeLoot(6).amount == 30);
            T.Check("non-mag loot spawns as a single (1)", Assets.makeLoot(13).amount == 1);

            // melee: the strong-swing multiplier + stamina come from the real .dat (source UseableMelee)
            string knifePath = ProjectSettings.GlobalizePath("res://content/knife_military.dat");
            T.Check("knife_military.dat bundled", System.IO.File.Exists(knifePath));
            var mk = MeleeDef.FromDatText("knife_military", System.IO.File.ReadAllText(knifePath));
            T.Check("melee: knife strong-swing x1.5 (Strength)", Mathf.Abs(mk.Strength - 1.5f) < 0.01f);
            T.Check("melee: knife swing costs 15 stamina", Mathf.Abs(mk.Stamina - 15f) < 0.01f);
            T.Check("melee: knife is NOT Repeated", !mk.Repeated);
            string btPath = ProjectSettings.GlobalizePath("res://content/blowtorch.dat");
            T.Check("blowtorch.dat bundled", System.IO.File.Exists(btPath));
            var bt = MeleeDef.FromDatText("blowtorch", System.IO.File.ReadAllText(btPath));
            T.Check("melee: blowtorch parses Repeated (continuous hold tool)", bt.Repeated);
            T.Check("melee: blowtorch parses Repair (heals, not damages)", bt.Repair);

            // gun-state persistence: a gun remembers ammo/firemode on its backing item through hands<->inventory<->drop
            var gunItem = new Item(4);   // an Eaglefire item
            p.LoadGun("res://content/eaglefire.dat");
            p.DebugSetHeldItem(gunItem);
            p.Ammo = 17; p.DebugSetFiremode(2);       // fire some down + flick to Auto
            p.DebugSaveGunState();
            T.Check("item remembers the gun's ammo (17)", gunItem.gunAmmo == 17);
            T.Check("item remembers the fire mode (Auto=2)", gunItem.gunFiremode == 2);
            p.Ammo = 30; p.DebugSetFiremode(1);       // wipe live state (as if a fresh equip)
            p.DebugRestoreGunState(gunItem);
            T.Check("re-equip restores ammo (17)", p.Ammo == 17);
            T.Check("re-equip restores fire mode (Auto)", p.DebugFiremodeIdx() == 2);
            p.Ammo = 25; p.DebugRestoreGunState(new Item(4));   // a fresh item has no saved state
            T.Check("fresh gun item keeps live defaults (no clobber)", p.Ammo == 25);
            // mag pie mechanics (master): remove-magazine / rack on the eaglefire (STANAG, chambered)
            p.LoadGun("res://content/eaglefire.dat");
            p.DebugSetHeldItem(new Item(4));
            p.Ammo = 31;   // a full 30-round mag + 1 chambered
            int mags6 = p.Inventory.getItemCount(6);
            p.RemoveMagazine();
            T.Check("remove-mag: the chambered round stays (Ammo=1)", p.Ammo == 1);
            T.Check("remove-mag: the 30-round mag returns to the bag", p.Inventory.getItemCount(6) == mags6 + 30);
            p.RackGun();
            T.Check("rack: the chamber empties (Ammo=0)", p.Ammo == 0);
            T.Check("rack: a 5.56 FMJ round is ejected to the bag", p.Inventory.getItemCount(5004) == 1);
            // chamber tracks its OWN bullet TYPE, independent of the seated mag (master): an FMJ round in the pipe stays
            // FMJ even with an AP mag seated; only a fresh cycle (rack/fire) takes the mag's type.
            T.Check("Military Magazine is tagged FMJ", Assets.find(6)?.ammoType == "FMJ");
            T.Check("5.56 FMJ round is tagged FMJ", Assets.find(5004)?.ammoType == "FMJ");
            p.LoadGun("res://content/eaglefire.dat");
            p.DebugSetHeldItem(new Item(4));
            T.Check("fresh gun: chamber type follows its mag (FMJ)", p.LoadedAmmoType == "FMJ");
            if (Assets.find(9998) == null)   // throwaway AP mag for the separation test (STANAG group 1, 5.56 cartridge)
                Assets.add(new ItemAsset { id = 9998, itemName = "Test AP Magazine", type = EItemType.MAGAZINE, size_x = 2, size_y = 1, magCapacity = 30, magCaliber = 1, magRound = "5.56x45mm NATO", ammoType = "AP" });
            p.Inventory.tryAddItem(new Item(9998, 30));
            var apMag = p.SpareMags().Find(m => m.item.id == 9998).item;
            T.Check("AP mag shows up as a caliber-matched spare", apMag != null);
            p.Ammo = 31;   // 30 FMJ in the mag + 1 FMJ chambered
            p.LoadMagInstance(apMag);   // TACTICAL swap: a round is already chambered
            T.Check("tactical swap KEEPS the chambered round's type (FMJ, though the seated mag is AP)", p.LoadedAmmoType == "FMJ");
            p.RackGun();   // eject the FMJ chambered round, cycle an AP round from the mag
            T.Check("rack cycles the seated mag's type into the chamber (now AP)", p.LoadedAmmoType == "AP");
            p.QueueFree();
            yield break;
        }
    }

    // Port of --shelltest: gun ACTION detection off the real .dats -- Pump = shell-by-shell reload, Break/Bolt/
    // Trigger are not -- plus the catalog gun-wiring (items with gunName are pick-up-equippable).
    public class GunActionTypes : GameTest
    {
        public override string Name => "gun.action_types";
        public override IEnumerable<Step> Run()
        {
            GunDef Load(string g) => GunDef.FromDatText(System.IO.File.ReadAllText(ProjectSettings.GlobalizePath($"res://content/{g}.dat")));
            var masterkey = Load("masterkey"); var eaglefire = Load("eaglefire"); var timberwolf = Load("timberwolf"); var bluntforce = Load("bluntforce");
            T.Check($"masterkey action Break (got {masterkey.Action})", masterkey.Action == "Break");
            T.Check("masterkey (break) is not shell-by-shell", !masterkey.ShellReload);
            T.Check($"timberwolf action Bolt (got {timberwolf.Action})", timberwolf.Action == "Bolt");
            T.Check("eaglefire (trigger) is not shell-by-shell", !eaglefire.ShellReload);
            T.Check($"bluntforce action Pump (got {bluntforce.Action})", bluntforce.Action == "Pump");
            T.Check("bluntforce (pump) IS shell-by-shell", bluntforce.ShellReload);
            var seq = new List<int>();
            for (int a = 0; a < bluntforce.AmmoMax;) { a = System.Math.Min(a + 1, bluntforce.AmmoMax); seq.Add(a); }
            T.Check($"pump loads one shell per step up to {bluntforce.AmmoMax}", seq.Count == bluntforce.AmmoMax && seq[0] == 1);

            ItemCatalog.RegisterAll();   // catalog gun-wiring: gunName set = pick-up-equippable (EquipHeldGun -> viewmodel)
            int wired = 0;
            foreach (var a in Assets.all()) if (!string.IsNullOrEmpty(a.gunName)) wired++;
            T.Check($"equippable guns wired in the catalog (got {wired})", wired >= 8);
            T.Check("eaglefire item (id 4) wired to its gun", Assets.find(4)?.gunName == "eaglefire");
            yield break;
        }
    }
}
