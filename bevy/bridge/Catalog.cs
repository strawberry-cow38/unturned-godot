using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

public sealed class Catalog
{
    public readonly List<BlueprintDef> Recipes = new();
    public readonly Dictionary<ushort, ServerGunProfile> Guns = new();
    readonly Dictionary<ushort, string> _visuals = new();
    public int Count => Assets.all().Count();
    public string Root { get; private set; }

    public void Load(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        Godot.ProjectSettings.ContentRoot = Root;
        // These are linked ORIGINAL source files, including hand-authored overrides,
        // ammo / firewood / salvage recipe generation, bags and consumable effects.
        ItemCatalog.RegisterAll();
        foreach (var a in Assets.all()) a.iconPath = Existing($"items/icons/{a.id}.png");
        BlueprintRegistry.ResetForTests(); BlueprintRegistry.EnsureLoaded();
        Recipes.Clear(); Recipes.AddRange(BlueprintRegistry.All); Guns.Clear(); _visuals.Clear();
        foreach (var path in Directory.EnumerateFiles(Root, "*.dat"))
        {
            var text = File.ReadAllText(path); var d = new DatParser().Parse(text);
            if (!ushort.TryParse(d.GetString("ID"), out var id) || Assets.find(id) is not { } a || a.type != EItemType.GUN) continue;
            var gun = GunDef.FromDatText(text);
            Guns[id] = new ServerGunProfile { AssetName = Path.GetFileNameWithoutExtension(path),
                PlayerDamage = gun.PlayerDamage, ZombieDamage = gun.ZombieDamage,
                ObjectDamage = gun.ObjectDamage, VehicleDamage = gun.VehicleDamage,
                FirerateTicks = gun.Firerate, CyclicRateRPM = gun.CyclicRateRPM,
                MuzzleVelocity = gun.MuzzleVelocity, BallisticSteps = gun.BallisticSteps,
                GravityMultiplier = gun.GravityMultiplier, MagCapacity = a.gunAmmoMax, Pellets = gun.Pellets };
        }
        // Original extracted item-id to mesh stem tables, used by the Bevy host.
        foreach (string name in new[] { "consumables.tsv", "melee.tsv", "throwables.tsv", "attachments.tsv" })
        {
            var path = Path.Combine(Root, name);
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path))
            {
                var fields = line.Split('\t');
                if (fields.Length >= 2 && ushort.TryParse(fields[0], out var id)) _visuals[id] = fields[1];
            }
        }
    }

    public object[] Metadata() => Assets.all().Select(a =>
    {
        string asset = a.gunName ?? a.meleeName ?? _visuals.GetValueOrDefault(a.id);
        var def = DeployableDef.ById(a.id);
        string mesh = asset == null ? null : Existing(asset + (a.gunName != null ? "_gun.txt" : ".txt"));
        if (mesh == null && def?.Model != null) mesh = Existing("objects/" + def.Model + ".obj");
        string texture = asset == null ? null : Existing(asset + "_albedo.png");
        if (texture == null && def?.Model != null) texture = Existing("objects/" + def.Model + "_tex.png");
        return (object)new { id = a.id, name = a.itemName, type = a.type.ToString().ToLowerInvariant(), asset, mesh, texture,
            icon = a.iconPath, width = a.size_x, height = a.size_y, stackSize = a.stackSize, slot = a.slot.ToString(),
            gunAmmoMax = a.gunAmmoMax, movementMultiplier = a.equipableMovementSpeedMultiplier,
            isConsumable = a.IsConsumable, isFluidContainer = a.IsFluidContainer, isDeployable = def != null };
    }).ToArray();
    string Existing(string path) => File.Exists(Path.Combine(Root, path)) ? path : null;
}
