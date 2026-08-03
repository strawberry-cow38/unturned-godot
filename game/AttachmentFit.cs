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

        /// <summary>The in-hand MESH for an attachment item, or null if this port never ripped one. Separate from
        /// Fits() because fitting is a rules question and having a model is an asset question -- an attachment with
        /// no mesh still attaches and still applies its stats, it just renders nothing, and conflating the two would
        /// hide every un-ripped attachment from a menu that is supposed to show what you own.</summary>
        public static string MeshFor(ushort id) => Meshes.TryGetValue(id, out var m) ? m : null;

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
            { 21,  "red_kobra_sight.txt" },         // 8x Scope
            { 22,  "red_halo_sight.txt" },          // Cross Scope
        };
    }
}
