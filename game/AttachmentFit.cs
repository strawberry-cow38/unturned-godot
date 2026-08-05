using SDG.Unturned;

namespace UnturnedGodot
{
    // WHICH ATTACHMENTS FIT WHICH SLOT, and which of them you are actually carrying (strawberry: "actually consider
    // player inventory and which attachments they have and which apply to each slot ... like source does").
    //
    // The rule is the retail one, from Items.SearchContents' filter (ItemType + CaliberId + IncludeUnspecifiedCaliber)
    // and ItemCaliberAsset.CalibersContainId:
    //
    //   an item fits a slot when its TYPE matches the slot, AND
    //     - it declares no calibers            -> universal, fits any gun   (retail: IncludeUnspecifiedCaliber)
    //     - it declares calibers               -> the gun's caliber must be in that list
    //
    // The empty-list-means-universal branch is the one worth stating out loud: it is why a rail sight goes on
    // everything while a magazine does not, and reading it as "no calibers = fits nothing" would leave every sight
    // slot permanently empty while looking like a working filter.
    //
    // DATA GAP, deliberate and visible: this port has no attachment .dat files at all, so no item carries a caliber
    // list. Magazines are the exception -- ItemAsset.magCaliber is already extracted, and PlayerController.FindBestMag
    // has been matching on it. So magazines filter by caliber for real, and sights/barrels/grips/tacticals currently
    // land in the "declares no calibers" branch and read as universal. That is the correct behaviour for the data we
    // have rather than a shortcut: when the attachment .dats are extracted, fill Calibers and this tightens with no
    // change to the callers.
    public static class AttachmentFit
    {
        /// <summary>The five hook slots a gun presents, in the source's own order.</summary>
        public static readonly string[] Slots = { "Sight", "Tactical", "Grip", "Barrel", "Magazine" };

        public static EItemType TypeFor(string slot) => slot switch
        {
            "Sight" => EItemType.SIGHT,
            "Tactical" => EItemType.TACTICAL,
            "Grip" => EItemType.GRIP,
            "Barrel" => EItemType.BARREL,
            "Magazine" => EItemType.MAGAZINE,
            _ => EItemType.GENERIC,
        };

        /// <summary>Per-item caliber lists, once someone extracts the attachment .dats. Empty = universal, which is
        /// what every non-magazine attachment resolves to today. Keyed by item id.</summary>
        public static readonly System.Collections.Generic.Dictionary<ushort, ushort[]> Calibers = new();

        /// <summary>Does `a` fit `slot` on a gun of `gunCaliber`? Pure, engine-free, and the single place the rule
        /// lives -- the menu asks this rather than re-deriving it per button.</summary>
        public static bool Fits(ItemAsset a, string slot, int gunCaliber)
        {
            if (a == null) return false;
            if (a.type != TypeFor(slot)) return false;
            // A MAGAZINE carries its caliber directly (magCaliber, already extracted) and must match exactly --
            // this is the same test FindBestMag uses, kept identical on purpose so the menu and the reload agree
            // about what fits. A mismatch here would let you attach a magazine the gun then refuses to reload from.
            if (a.type == EItemType.MAGAZINE) return a.IsMagazine && a.magCaliber == gunCaliber;
            if (!Calibers.TryGetValue(a.id, out var cals) || cals == null || cals.Length == 0) return true;   // universal
            foreach (var c in cals) if (c == gunCaliber) return true;
            return false;
        }

        /// <summary>Every fitting attachment in the bag as SEPARATE PHYSICAL OBJECTS, with the page/index they live
        /// at so the exact one clicked is the one consumed.
        ///
        /// Distinct from InBag, which collapses by item id, because two magazines of the same id are NOT
        /// interchangeable: Item.amount is the rounds left in that particular magazine, which is why FindBestMag
        /// picks the fullest rather than any. A ring that shows a 30/30 and a 12/30 as one icon is showing the player
        /// a choice they cannot make, and consuming "any one of that id" would let them click the full one and get
        /// the empty one.</summary>
        public static System.Collections.Generic.List<(ItemAsset Asset, Item Item, byte Page, byte Index)> InBagInstances(
            PlayerInventory inv, string slot, int gunCaliber)
        {
            var outp = new System.Collections.Generic.List<(ItemAsset, Item, byte, byte)>();
            if (inv == null) return outp;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var jar = pg.getItem(i);
                    if (jar?.item == null) continue;
                    var a = Assets.find(jar.item.id);
                    if (!Fits(a, slot, gunCaliber)) continue;
                    outp.Add((a, jar.item, b, i));
                }
            }
            return outp;
        }

        /// <summary>Every distinct attachment in the player's bag that fits `slot`, as (asset, count). Distinct by
        /// item id: carrying six identical magazines is one button that says x6, not six buttons.
        ///
        /// Scans the same page range FindBestMag does -- PAGES-2 excludes the two clothing/area pages, so a sight
        /// sewn into a shirt is not offered.</summary>
        public static System.Collections.Generic.List<(ItemAsset Asset, int Count)> InBag(
            PlayerInventory inv, string slot, int gunCaliber)
        {
            var outp = new System.Collections.Generic.List<(ItemAsset, int)>();
            if (inv == null) return outp;
            var seen = new System.Collections.Generic.Dictionary<ushort, int>();
            var order = new System.Collections.Generic.List<ushort>();
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var jar = pg.getItem(i);
                    if (jar?.item == null) continue;
                    var a = Assets.find(jar.item.id);
                    if (!Fits(a, slot, gunCaliber)) continue;
                    if (seen.TryGetValue(a.id, out var n)) seen[a.id] = n + 1;
                    else { seen[a.id] = 1; order.Add(a.id); }
                }
            }
            foreach (var id in order) outp.Add((Assets.find(id), seen[id]));
            return outp;
        }

        // ---- INSTALLED STATE: which attachment item sits in which slot, stored on the GUN'S ITEM ----
        // Read/written through here rather than by touching the fields directly, so "which field backs the Magazine
        // slot" (gunMagId, which predates the other four) is answered in exactly one place.

        public static int InstalledId(Item gun, string slot)
        {
            if (gun == null) return -1;
            return slot switch
            {
                "Sight" => gun.gunSightId, "Barrel" => gun.gunBarrelId,
                "Grip" => gun.gunGripId, "Tactical" => gun.gunTacticalId,
                "Magazine" => gun.gunMagId, _ => -1,
            };
        }

        public static void SetInstalledId(Item gun, string slot, int id)
        {
            if (gun == null) return;
            switch (slot)
            {
                case "Sight": gun.gunSightId = id; break;
                case "Barrel": gun.gunBarrelId = id; break;
                case "Grip": gun.gunGripId = id; break;
                case "Tactical": gun.gunTacticalId = id; break;
                case "Magazine": gun.gunMagId = id; break;
            }
        }

        /// <summary>The iron-sight ITEM a gun ships with, derived from the catalog by name -- retail names every one
        /// "&lt;Gun&gt; Iron Sights" (34 of them). Derived rather than tabulated so a newly ported gun's irons resolve
        /// with no extra wiring, the same reason the gun table reads the .dat files instead of a list.
        ///
        /// Returns -1 for a gun with no such item, which is not a failure: the pistols carry their sights baked into
        /// the body mesh (verified when the glowing-sight markers were traced) and have no separate irons to remove.</summary>
        public static int DefaultIronsId(string gunItemName)
        {
            if (string.IsNullOrEmpty(gunItemName)) return -1;
            string want = gunItemName + " Iron Sights";
            foreach (var a in Assets.all())
                if (a.type == EItemType.SIGHT && string.Equals(a.itemName, want, System.StringComparison.OrdinalIgnoreCase))
                    return a.id;
            return -1;
        }

        /// <summary>Give a gun item its factory sight the first time it's picked up, so the irons you can see on the
        /// weapon are the same object you get back when you take them off. Without this, detaching a stock gun's
        /// sights would return nothing and the sight would simply cease to exist -- the gun looks the same either
        /// way, which is what makes the loss invisible.
        ///
        /// Only fills an UNSET slot (-1), so it can run on every equip without overwriting what the player fitted.</summary>
        public static void SeedDefaults(Item gunItem, string gunItemName)
        {
            if (gunItem == null || gunItem.gunAttachSeeded) return;
            gunItem.gunAttachSeeded = true;   // set FIRST: a gun with no irons item must still count as seeded,
                                              // or it re-runs this lookup on every single equip forever.
            int irons = DefaultIronsId(gunItemName);
            if (irons >= 0) gunItem.gunSightId = irons;
        }

        /// <summary>The in-hand MESH for an attachment item, or null if this port never ripped one. Separate from
        /// Fits() because fitting is a rules question and having a model is an asset question -- an attachment with
        /// no mesh still attaches and still applies its stats, it just renders nothing, and conflating the two would
        /// hide every un-ripped attachment from a menu that is supposed to show what you own.</summary>
        public static string MeshFor(ushort id)
        {
            if (Meshes.TryGetValue(id, out var m)) return m;
            _irons ??= BuildIronsMeshes();
            return _irons.TryGetValue(id, out var im) ? im : null;
        }

        static System.Collections.Generic.Dictionary<ushort, string> _irons;
        public static void ResetIronsMeshCache() => _irons = null;   // L1: the catalog is re-registered between sandboxes

        /// <summary>Every gun's FACTORY IRONS mesh, keyed by the iron-sight item id, joined out of data already on
        /// disk instead of hand-listed: content/sights.tsv gives gun-content-name -> sight mesh, the catalog gives
        /// gun-content-name -> gun item -> "&lt;Gun&gt; Iron Sights" -> item id.
        ///
        /// Without this, taking a gun's irons off and putting them back renders NOTHING -- the item is real, the slot
        /// records it, and the sight is simply invisible. The hand-written table below only ever covered seven items,
        /// so every gun past the eaglefire had that hole. Deriving it means a newly ported gun's irons render the day
        /// its sights.tsv row lands, with no extra wiring -- the same reason the gun table reads the .dat files.</summary>
        static System.Collections.Generic.Dictionary<ushort, string> BuildIronsMeshes()
        {
            var byGunName = new System.Collections.Generic.Dictionary<string, string>();   // gun content name -> mesh
            string sp = Godot.ProjectSettings.GlobalizePath("res://content/sights.tsv");
            if (System.IO.File.Exists(sp))
                foreach (var line in System.IO.File.ReadAllLines(sp))
                {
                    var c = line.Split('\t');
                    if (c.Length >= 2 && c[0].Length > 0 && c[1].Length > 0) byGunName[c[0]] = c[1];
                }
            var outp = new System.Collections.Generic.Dictionary<ushort, string>();
            foreach (var a in Assets.all())
            {
                if (string.IsNullOrEmpty(a.gunName) || !byGunName.TryGetValue(a.gunName, out var mesh)) continue;
                int irons = DefaultIronsId(a.itemName);
                if (irons >= 0) outp[(ushort)irons] = mesh;
            }
            return outp;
        }

        // Item id -> ripped mesh. Only the handful that exist in content/ today; the rest attach model-less until
        // someone rips them. Deliberately a table rather than a name guess: item names ("8x Scope") do not map onto
        // mesh filenames ("red_kobra_sight.txt") by any rule.
        static readonly System.Collections.Generic.Dictionary<ushort, string> Meshes = new()
        {
            { 5,   "eaglefire_iron_sights.txt" },   // Eaglefire Iron Sights
            { 6,   "military_30_mag.txt" },         // Military Magazine
            { 7,   "suppressor.txt" },              // Military Suppressor
            { 8,   null },                          // Vertical Grip -- no rip yet
            { 17,  "military_30_mag.txt" },         // Military Drum (stand-in: same family, no drum rip)
            { 21,  "scope_8x_sight.txt" },          // 8x Scope (was a red_kobra stand-in)
            { 22,  "cross_scope_sight.txt" },       // Cross Scope (was a red_halo stand-in)
            { 146, "red_dot_sight.txt" },           // Dot Sight (electronic aiming point)
            { 147, "red_halo_sight.txt" },          // Halo Sight (electronic aiming halo)
            { 148, "chevron_scope_sight.txt" },     // Chevron Scope
            { 153, "scope_7x_sight.txt" },          // 7x Scope
            { 296, "scope_16x_sight.txt" },         // 16x Scope
            { 302, "shadowstalker_scope_sight.txt" }, // Shadowstalker Scope
            { 476, "makeshift_scope_sight.txt" },   // Makeshift Scope (6x)
            { 1004, "red_kobra_sight.txt" },        // Kobra Sight (1x red-dot) -- mesh already ripped, now wired to its id
            { 1201, "nightvision_scope_sight.txt" }, // Nightvision Military Scope (6x)
            { 1442, "shadowstalkermk2_scope_sight.txt" }, // Shadowstalker Mk2 Scope (20x)
            // The three mags added for the group-1 split. Master asked for them to be VISUALLY IDENTICAL to the
            // STANAG one, so they deliberately share its mesh -- the difference is which gun accepts them, not what
            // they look like. Reusing the mesh is the requirement here, not a missing-rip stand-in.
            // IDs are 9140+, NOT 9110-9112: those are already the Fluid Tank / Water Source / Splitter, and the
            // later Add() calls silently overwrote the magazines registered under them.
            { 9140, "military_30_mag.txt" },        // Augewehr Magazine (group 201)
            { 9141, "military_30_mag.txt" },        // Nightraider Magazine (group 202)
            { 9142, "military_30_mag.txt" },        // .300 Blackout Magazine (group 1, different round)
            { 9143, "military_30_mag.txt" },        // Heartbreaker Magazine (group 203, clone of the M39's)
        };
    }
}
