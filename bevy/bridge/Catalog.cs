using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
using System.Globalization;

namespace UnturnedBevy;

public sealed class Catalog
{
    public readonly List<BlueprintDef> Recipes = new();
    public readonly Dictionary<ushort, ServerGunProfile> Guns = new();
    public int Count => Assets.all().Count();
    public string Root { get; private set; }

    // Files are the original shipping metadata, not a second invented item table.
    public void Load(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        Assets.clear(); Recipes.Clear(); Guns.Clear();
        foreach (var c in Rows("items_catalog.tsv"))
        {
            if (c.Length < 6 || !ushort.TryParse(c[0], out var id)) continue;
            Enum.TryParse<EItemType>(c[2], true, out var type);
            Enum.TryParse<EItemRarity>(c[3], true, out var rarity);
            Assets.add(new ItemAsset { id = id, itemName = c[1], type = type, rarity = rarity,
                size_x = Byte(c[4], 1), size_y = Byte(c[5], 1),
                slot = type == EItemType.MELEE ? ESlotType.SECONDARY : ESlotType.NONE,
                description = c.Length > 6 ? c[6] : "", guid = c.Length > 7 ? c[7] : "",
                iconPath = $"item_icons/{id}.png" });
        }
        foreach (var path in Directory.EnumerateFiles(Root, "*.dat"))
        {
            var text = File.ReadAllText(path);
            var d = new DatParser().Parse(text);
            if (!ushort.TryParse(d.GetString("ID"), out var id)) continue;
            var a = Assets.find(id);
            if (a == null) continue;
            a.amount = (byte)Math.Clamp(d.ParseInt32("Amount", 1), 1, 255);
            a.durability = d.ParseFloat("Durability", 0);
            a.wear = d.ParseUInt8("Wear", 1);
            a.equipableMovementSpeedMultiplier = d.ParseFloat("Equipable_Movement_Speed_Multiplier", 1);
            if (a.type == EItemType.GUN)
            {
                var gun = GunDef.FromDatText(text);
                a.gunName = Path.GetFileNameWithoutExtension(path);
                a.gunAmmoMax = d.ParseInt32("Ammo_Max", 30);
                a.gunCaliber = d.ParseInt32("Caliber", 0);
                a.slot = SlotTypeExtension.Parse(d.GetString("Slot"));
                Guns[id] = new ServerGunProfile { AssetName = a.gunName,
                    PlayerDamage = gun.PlayerDamage, ZombieDamage = gun.ZombieDamage,
                    ObjectDamage = gun.ObjectDamage, VehicleDamage = gun.VehicleDamage,
                    FirerateTicks = gun.Firerate, CyclicRateRPM = gun.CyclicRateRPM,
                    MuzzleVelocity = gun.MuzzleVelocity, BallisticSteps = gun.BallisticSteps,
                    GravityMultiplier = gun.GravityMultiplier, MagCapacity = a.gunAmmoMax };
            }
        }
        foreach (var c in Rows("consumable_stats.tsv"))
        {
            if (c.Length < 9 || !ushort.TryParse(c[0], out var id) || Assets.find(id) is not { } a) continue;
            a.useHealth = Int(c[1]); a.useFood = Int(c[2]); a.useWater = Int(c[3]);
            a.useVirus = Int(c[4]); a.useDisinfectant = Int(c[5]); a.useEnergy = Int(c[6]);
            a.useStopsBleeding = Int(c[7]) == 1; a.useHealBroken = Int(c[8]) == 1;
        }
        var bpFile = Path.Combine(Root, "blueprints.tsv");
        if (File.Exists(bpFile)) foreach (var line in File.ReadLines(bpFile))
            if (BlueprintDef.FromTsv(line) is { } bp) Recipes.Add(bp);
        Durability.RegisterTools(Recipes);
    }

    IEnumerable<string[]> Rows(string name)
    {
        var file = Path.Combine(Root, name);
        return File.Exists(file) ? File.ReadLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Split('\t')) : [];
    }
    static byte Byte(string v, byte fallback) => byte.TryParse(v, out var n) ? n : fallback;
    static int Int(string v) => int.TryParse(v, out var n) ? n : 0;
}
