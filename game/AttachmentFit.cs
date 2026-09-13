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

        /// <summary>Per-item CARTRIDGE restrictions, keyed by item id. Empty = universal, same rule as Calibers.
        ///
        /// ⚠ A SEPARATE AXIS FROM Calibers, and it has to be. Calibers holds Unturned's abstract magazine GROUP,
        /// and "only fits 5.56 guns" cannot be written in that space: the ten guns chambered in 5.56x45mm NATO are
        /// spread across FIVE groups (eaglefire/maplestrike/swissgewehr/the three wood rifles = 1, dragonfang = 12,
        /// augewehr = 201, nightraider = 202, fusilaut = 204), and group 1 ALSO holds the honeybadger, which is
        /// .300 BLK. Restricting by group would simultaneously miss four fifths of the 5.56 rifles and wrongly fit
        /// the one gun in the list that is not 5.56 -- because a group answers "what magazine feeds it", and this
        /// question is "what comes out of the barrel". GunDef keeps the two apart for the same reason.</summary>
        public static readonly System.Collections.Generic.Dictionary<ushort, string[]> CaliberNames = new()
        {
            // strawberry 2026-09-13: the Military Suppressor "only fits 5.56 guns, rename it to 5.56 silencer".
            { 7, new[] { "5.56x45mm NATO" } },
        };

        /// <summary>Does `a` fit `slot` on a gun of `gunCaliber`? Pure, engine-free, and the single place the rule
        /// lives -- the menu asks this rather than re-deriving it per button.</summary>
        public static bool Fits(ItemAsset a, string slot, int gunCaliber) => Fits(a, slot, gunCaliber, null);

        /// <summary>As above, with the gun's real CARTRIDGE (GunDef.CaliberName) so an attachment can be
        /// restricted to what the barrel actually fires rather than to what feeds it. Null = unknown, which
        /// leaves a cartridge-restricted attachment refusing rather than fitting: a caller that does not know
        /// what the gun chambers has not earned a yes.</summary>
        public static bool Fits(ItemAsset a, string slot, int gunCaliber, string gunCaliberName)
        {
            if (a == null) return false;
            if (a.type != TypeFor(slot)) return false;
            if (CaliberNames.TryGetValue(a.id, out var names) && names != null && names.Length > 0)
            {
                if (string.IsNullOrEmpty(gunCaliberName)) return false;
                bool ok = false;
                foreach (var n in names) if (n == gunCaliberName) { ok = true; break; }
                if (!ok) return false;
            }
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
            PlayerInventory inv, string slot, int gunCaliber, string gunCaliberName = null)
        {
            var outp = new System.Collections.Generic.List<(ItemAsset, Item, byte, byte)>();
            if (inv == null) return outp;
            for (byte b = 0; b < PlayerInventory.OWNPAGES; b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var jar = pg.getItem(i);
                    if (jar?.item == null) continue;
                    var a = Assets.find(jar.item.id);
                    if (!Fits(a, slot, gunCaliber, gunCaliberName)) continue;
                    outp.Add((a, jar.item, b, i));
                }
            }
            return outp;
        }

        /// <summary>Every distinct attachment in the player's bag that fits `slot`, as (asset, count). Distinct by
        /// item id: carrying six identical magazines is one button that says x6, not six buttons.
        ///
        /// Scans the same page range FindBestMag does -- OWNPAGES excludes the external container pages, so a sight
        /// sewn into a shirt is not offered.</summary>
        public static System.Collections.Generic.List<(ItemAsset Asset, int Count)> InBag(
            PlayerInventory inv, string slot, int gunCaliber, string gunCaliberName = null)
        {
            var outp = new System.Collections.Generic.List<(ItemAsset, int)>();
            if (inv == null) return outp;
            var seen = new System.Collections.Generic.Dictionary<ushort, int>();
            var order = new System.Collections.Generic.List<ushort>();
            for (byte b = 0; b < PlayerInventory.OWNPAGES; b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var jar = pg.getItem(i);
                    if (jar?.item == null) continue;
                    var a = Assets.find(jar.item.id);
                    if (!Fits(a, slot, gunCaliber, gunCaliberName)) continue;
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
        /// <summary>Is the sight on this gun its own factory irons? Irons are NOT an item (strawberry 2026-09-04: "irons
        /// should be non-removable, only removed visually when a new scope is attached. they shouldnt be their own
        /// item"): they cannot be detached, fitting a scope over them displaces nothing, and taking the scope off
        /// puts them back. The sight slot still stores their id so the viewmodel knows what to draw.</summary>
        public static bool IsDefaultIrons(Item gunItem)
        {
            if (gunItem == null || gunItem.gunSightId < 0) return false;
            string name = Assets.find(gunItem.id)?.itemName;
            int irons = DefaultIronsId(name);
            return irons >= 0 && gunItem.gunSightId == irons;
        }
        public static int DefaultIronsIdOf(Item gunItem) => gunItem == null ? -1 : DefaultIronsId(Assets.find(gunItem.id)?.itemName);

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
        /// <summary>Hang a gun's sight / magazine / barrel on a rig, from ids rather than from an Item.
        ///
        /// ⚠ ONE implementation for all four viewers: the live 3P body, the inventory paperdoll, and other players'
        /// puppets. The first two read the ids off the held Item; a puppet cannot -- the item never crosses the
        /// wire, only its id does -- so the ids are the parameter and the mounting is shared. A second copy for
        /// remote players would have drifted from this one the first time a hook position moved, and the symptom
        /// would be "other people's scopes sit slightly wrong", which nobody reports precisely.
        ///
        /// Hook positions and colours are the ones the viewmodel attach loop uses. Falls back to the gun's factory
        /// iron sight and default magazine when nothing is fitted; a barrel only appears when one actually is,
        /// because guns ship bare.</summary>
        /// <summary>The parts a gun should be WEARING -- (slot, mesh, position in the gun's own frame, tint) --
        /// given what its item says is installed. Falls back to the gun's FACTORY sight and magazine when a slot
        /// has no installed id, so a gun that has never been touched still wears its irons.
        ///
        /// ⚠ THE ONE PLACE THE HOOKS LIVE. MountOn already existed to stop the 3P body and the inventory
        /// paperdoll drifting apart ("a second copy of this in the UI would drift the moment one of the two hook
        /// positions or fallbacks changed, which is how the paperdoll ended up bare in the first place"). A
        /// DROPPED gun is the third consumer and it is not a RiggedCharacter, so rather than write those
        /// positions out a second time the decision moved here and MountOn became one of its callers.</summary>
        public static System.Collections.Generic.List<(string Slot, Godot.Mesh Mesh, Godot.Vector3 Pos, Godot.Color Tint, Godot.Texture2D Tex)>
            PartsFor(string gunName, int sightId, int magId, int barrelId) => PartsFor(gunName, sightId, magId, barrelId, 0);

        public static System.Collections.Generic.List<(string Slot, Godot.Mesh Mesh, Godot.Vector3 Pos, Godot.Color Tint, Godot.Texture2D Tex)>
            PartsFor(string gunName, int sightId, int magId, int barrelId, int tacticalId)
        {
            var parts = new System.Collections.Generic.List<(string, Godot.Mesh, Godot.Vector3, Godot.Color, Godot.Texture2D)>();
            if (string.IsNullOrEmpty(gunName)) return parts;
            var gv = Viewmodel.VisualForTest(gunName);
            string sightTxt = sightId > 0 ? MeshFor((ushort)sightId) : gv.Sight;
            if (!string.IsNullOrEmpty(sightTxt) && ContentProvider.ParseObj($"res://content/{sightTxt}") is Godot.Mesh sm)
                parts.Add(("Sight", sm, gv.SightPos != Godot.Vector3.Zero ? gv.SightPos : new Godot.Vector3(0f, 0.1312f, -0.118f),
                           gv.SightColor.A > 0f ? gv.SightColor : new Godot.Color(0.3f, 0.3f, 0.3f), null));
            string magTxt = magId > 0 ? MeshFor((ushort)magId) : gv.Mag;
            if (!string.IsNullOrEmpty(magTxt) && ContentProvider.ParseObj($"res://content/{magTxt}") is Godot.Mesh mm)
                parts.Add(("Magazine", mm, new Godot.Vector3(0f, 0.0166f, 0.0238f), new Godot.Color(0.07f, 0.07f, 0.08f), null));
            if (barrelId > 0 && MeshFor((ushort)barrelId) is string bt && ContentProvider.ParseObj($"res://content/{bt}") is Godot.Mesh bm)
                parts.Add(("Barrel", bm, new Godot.Vector3(0f, 0.7307f, -0.0818f), new Godot.Color(0.05f, 0.05f, 0.055f), null));
            // TACTICAL (strawberry 2026-09-13: "wire the tactical laser, flashlight attachments"). The hook is the
            // one Viewmodel._hookLocal already publishes for the slot -- the same table the T menu projects to place
            // its slot icons, so the part lands exactly where the menu says the slot is. There is no factory
            // fallback: no gun ships with a laser or a light, so nothing mounts here unless one was installed.
            if (tacticalId > 0 && MeshFor((ushort)tacticalId) is string tt && ContentProvider.ParseObj($"res://content/{tt}") is Godot.Mesh tm)
                // WHITE tint with the real albedo bound: the texture IS the colour here, and modulating it by a
                // grey would mute the one cell that distinguishes a laser from a light.
                parts.Add(("Tactical", tm, new Godot.Vector3(-0.0601f, 0.3815f, -0.0851f),
                           Godot.Colors.White, TexFor((ushort)tacticalId)));
            return parts;
        }

        public static void MountOn(RiggedCharacter body, string gunName, int sightId, int magId, int barrelId)
            => MountOn(body, gunName, sightId, magId, barrelId, 0);

        public static void MountOn(RiggedCharacter body, string gunName, int sightId, int magId, int barrelId, int tacticalId)
        {
            if (body == null || string.IsNullOrEmpty(gunName)) return;
            body.ClearGunAttachments();
            foreach (var (slot, mesh, pos, tint, tex) in PartsFor(gunName, sightId, magId, barrelId, tacticalId))
                body.MountGunAttachment(slot, mesh, pos, tint, tex);
        }

        /// <summary>What a TACTICAL attachment DOES, as the retail .dat states it.
        ///
        /// ItemTacticalAsset.PopulateAsset parses four BARE PRESENCE KEYS -- `Laser`, `Light`, `Rangefinder`,
        /// `Melee` -- plus `Laser_Color` (LegacyParseColor, default Color.red, clamped and forced opaque) and,
        /// when Light is present, a PlayerSpotLightConfig built from the same SpotLight_* keys the handheld
        /// torch reads. Presence, not a bool parse, which is why this is a table of flags and not of values.
        ///
        /// READ OFF THE FIVE SHIPPED .dats on this box (Bundles/Items/Tacticals/), and reading them settled the
        /// two things a guess would have got wrong: Tactical_Laser.dat is `Laser` ALONE -- no Laser_Color -- so
        /// the laser is retail-default RED rather than anything authored; and Tactical_Light.dat is `Light`
        /// ALONE, no SpotLight_* overrides at all, which is the source's own statement that the rail light IS
        /// the flashlight (strawberry 2026-09-13: "identical to the flashlight, just on N and attached to the
        /// gun") rather than a lookalike with its own numbers.
        ///
        /// The other three are here as DATA with no behaviour, deliberately: the Rangefinder and the Bayonet are
        /// different mechanics the port does not have, and Adaptive Chambering is a pure stat part. Listing them
        /// as known-and-inert is what stops a future `IsLaser` fallback quietly making a rangefinder emit a
        /// beam -- the table answers "not a laser" for them instead of not knowing.</summary>
        public readonly struct TacticalDef
        {
            public readonly bool Laser, Light, Rangefinder, Melee;
            public readonly Godot.Color LaserColor;
            public TacticalDef(bool laser = false, bool light = false, bool rangefinder = false, bool melee = false,
                               Godot.Color? laserColor = null)
            { Laser = laser; Light = light; Rangefinder = rangefinder; Melee = melee; LaserColor = laserColor ?? Godot.Colors.Red; }
            /// <summary>Does N do anything with this fitted? Only the two that have an ON state.</summary>
            public bool Toggleable => Laser || Light;
        }

        static readonly System.Collections.Generic.Dictionary<ushort, TacticalDef> Tacticals = new()
        {
            { 151,  new TacticalDef(laser: true) },          // Tactical_Laser.dat: `Laser`, no Laser_Color -> retail red
            { 152,  new TacticalDef(light: true) },          // Tactical_Light.dat: `Light`, no SpotLight_* -> the flashlight's own defaults
            { 1008, new TacticalDef(rangefinder: true) },    // Rangefinder.dat -- mechanic not ported; here so it is not mistaken for a laser
            { 1438, new TacticalDef(melee: true) },          // Bayonet.dat -- a jab on the tactical key; not ported
            { 1007, new TacticalDef() },                     // Adaptive_Chambering.dat -- stats only, nothing to switch on
        };

        public static TacticalDef TacticalFor(int id)
            => id > 0 && Tacticals.TryGetValue((ushort)id, out var t) ? t : default;
        /// <summary>Is the fitted tactical something the tactical key can switch on or off?</summary>
        public static bool TacticalToggleable(int id) => TacticalFor(id).Toggleable;
        public static bool IsLaser(int id) => TacticalFor(id).Laser;
        public static bool IsTacticalLight(int id) => TacticalFor(id).Light;
        /// <summary>Retail's `laserColor`: the .dat's Laser_Color, default red. Both halves of the beam and the
        /// dot take it, and the emission is twice it (UseableGun ~4737).</summary>
        public static Godot.Color LaserColor(int id) => TacticalFor(id).LaserColor;

        /// <summary>Attachments whose colour lives in a TEXTURE rather than a flat tint. Almost nothing here needs
        /// one -- a suppressor and a magazine really are one shade of near-black, and the sights carry a per-item
        /// _Color in sights.tsv -- but the tactical pair's entire identity is a palette cell: the laser's 32x32
        /// albedo is dark grey with a pure RED (255,0,0) emitter, the light's the same grey with a warm
        /// (239,223,156) bulb. Tinted flat they render as two identical dark boxes, which is the shape of "wired"
        /// that is really "present but indistinguishable".</summary>
        static readonly System.Collections.Generic.Dictionary<ushort, string> Textures = new()
        {
            { 151, "tactical_laser_tex.png" },
            { 152, "tactical_light_tex.png" },
        };

        /// <summary>The attachment's albedo, or null when it is a flat-tint part. Goes through
        /// ContentProvider.TextureCached rather than a cache of its own: the same laser is mounted on the
        /// viewmodel, the 3P body, the paperdoll and every dropped copy of the gun, and the project already has
        /// one place that answers "this path, as a texture, once".</summary>
        /// <summary>The attachment albedo's path RELATIVE to res://content, for callers that need the image
        /// rather than the texture -- the lens-mask derivation reads pixels off disk, not off a Texture2D.</summary>
        public static string TexPathFor(int id)
            => id > 0 && Textures.TryGetValue((ushort)id, out var rel) ? rel : null;

        public static Godot.Texture2D TexFor(ushort id)
            => Textures.TryGetValue(id, out var rel) && !string.IsNullOrEmpty(rel)
                ? ContentProvider.TextureCached(Godot.ProjectSettings.GlobalizePath($"res://content/{rel}"))
                : null;

        /// <summary>What a BARREL does to the shot, read off the retail
        /// Bundles/Items/Barrels/&lt;Name&gt;/&lt;Name&gt;.dat: its own Shoot clip, its Volume multiplier, and the bare
        /// `Silenced` key.
        ///
        /// ⚠ "A BARREL IS ATTACHED" IS NOT "THE GUN IS SILENCED", and the port assumed it was -- IsSuppressed read
        /// `SlotAttached("Barrel")` under the comment "the only Barrel attachment is the silenced suppressor".
        /// That was true of the content then and is not true of the game: Military Barrel (149) and Ranger Barrel
        /// (1191) are accuracy parts, and the two Muzzles (150, 1190) are BRAKES. Fitting a muzzle brake would
        /// have made you inaudible to zombies, tracerless and flashless.
        ///
        /// The ten that silence each ship their own Shoot.ogg and the four that do not ship none, so the clip and
        /// the flag agree -- both are recorded here rather than deriving one from the other, because they answer
        /// different questions and a future barrel could easily break the coincidence.</summary>
        public readonly struct BarrelDef
        {
            public readonly string ShootClip; public readonly float Volume; public readonly bool Silenced;
            public BarrelDef(string clip, float volume, bool silenced) { ShootClip = clip; Volume = volume; Silenced = silenced; }
        }

        static readonly System.Collections.Generic.Dictionary<ushort, BarrelDef> Barrels = new()
        {
            { 7,    new BarrelDef("military_suppressor_shoot.ogg",  0.30f, true) },   // 5.56 Silencer
            { 144,  new BarrelDef("ranger_suppressor_shoot.ogg",    0.30f, true) },
            { 477,  new BarrelDef("makeshift_muffler_shoot.ogg",    0.80f, true) },
            { 1444, new BarrelDef("bluntforce_muffler_shoot.ogg",   0.80f, true) },
            { 117,  new BarrelDef("honeybadger_barrel_shoot.ogg",   0.90f, true) },   // integrally suppressed gun's own barrel
            { 1002, new BarrelDef("matamorez_barrel_shoot.ogg",     0.80f, true) },
            { 1167, new BarrelDef("nailgun_barrel_shoot.ogg",       0.50f, true) },
            { 1338, new BarrelDef("paintballgun_barrel_shoot.ogg",  0.90f, true) },
            { 350,  new BarrelDef("crossbow_barrel_shoot.ogg",      0.60f, true) },
            { 354,  new BarrelDef("bow_barrel_shoot.ogg",           0.60f, true) },
            { 149,  new BarrelDef(null, 1f, false) },   // Military Barrel -- accuracy
            { 150,  new BarrelDef(null, 1f, false) },   // Military Muzzle -- BRAKED, not silenced
            { 1191, new BarrelDef(null, 1f, false) },   // Ranger Barrel
            { 1190, new BarrelDef(null, 1f, false) },   // Ranger Muzzle -- braked
        };

        /// <summary>The barrel's shot profile; an unknown or absent barrel reads as "the gun's own, unsilenced".</summary>
        public static BarrelDef BarrelFor(int id)
            => id > 0 && Barrels.TryGetValue((ushort)id, out var b) ? b : new BarrelDef(null, 1f, false);

        /// <summary>Does this barrel actually silence? The ONE place that question is answered.</summary>
        public static bool IsSilencer(int id) => BarrelFor(id).Silenced;

        /// <summary>Muzzle-velocity multiplier on a silenced shot (strawberry 2026-09-13: "lower velocity").
        ///
        /// ⚠ NOT A PORT FIDELITY VALUE -- a deliberate deviation, flagged as one. Retail's barrels change damage
        /// (ballisticDamageMultiplier) and bullet GRAVITY (BallisticGravityMultiplier); nothing in ItemBarrelAsset
        /// or ItemCaliberAsset touches velocity, so there is no number to be faithful to. It is realistic --
        /// a suppressed weapon runs subsonic ammunition -- and it gives the silencer a cost to match its benefit:
        /// more drop and more lead at range in exchange for being unheard. One constant, easy to retune.</summary>
        public const float SilencedVelocityMultiplier = 0.80f;

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
            { 17,  "military_100_mag.txt" },        // Military Drum -- real ripped drum model (96v; was the STANAG military_30 stand-in)
            { 21,  "scope_8x_sight.txt" },          // 8x Scope (was a red_kobra stand-in)
            { 22,  "cross_scope_sight.txt" },       // Cross Scope (was a red_halo stand-in)
            { 151, "tactical_laser.txt" },          // Tactical Laser  -- items/tacticals/tactical_laser (136v)
            { 152, "tactical_light.txt" },          // Tactical Light  -- items/tacticals/tactical_light (136v)
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
