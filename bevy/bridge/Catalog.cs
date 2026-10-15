using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

public sealed class Catalog
{
    public readonly List<BlueprintDef> Recipes = new();
    public readonly Dictionary<ushort, ServerGunProfile> Guns = new();
    public readonly Dictionary<ushort, GunDef> GunDefs = new();
    public readonly Dictionary<ushort, MeleeDef> MeleeDefs = new();
    readonly Dictionary<ushort, double> _reloadSeconds = new();
    readonly Dictionary<string, double> _clips = new();
    public double ReloadSeconds(ushort id) => _reloadSeconds.GetValueOrDefault(id, 1.633);
    public double Clip(string name, string suffix, string fallback = null)
    {
        string cap = name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : "";
        if (_clips.TryGetValue(cap + suffix, out double own)) return own;
        string donor = WeaponClips.AnimationDonorFor(name);
        if (donor != null && _clips.TryGetValue(donor + suffix, out double borrowed)) return borrowed;
        return fallback != null ? _clips.GetValueOrDefault(fallback) : 0;
    }
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
        ItemCatalog.RegisterAll(); ConsumableRegistry.Load(); VehiclePaints.Clear();
        foreach (var a in Assets.all()) a.iconPath = Existing($"items/icons/{a.id}.png");
        BlueprintRegistry.ResetForTests(); BlueprintRegistry.EnsureLoaded();
        Recipes.Clear(); Recipes.AddRange(BlueprintRegistry.All); Guns.Clear(); GunDefs.Clear(); MeleeDefs.Clear(); _reloadSeconds.Clear(); _visuals.Clear();
        using var rig = File.Exists(Path.Combine(Root, "rig.json")) ? System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "rig.json"))) : null;
        _clips.Clear();
        if (rig != null && rig.RootElement.TryGetProperty("anims", out var animations))
            foreach (var clip in animations.EnumerateObject()) _clips[clip.Name] = clip.Value.GetProperty("length").GetDouble();
        string throwableClips = Path.Combine(Root, "throwable_anims.json");
        if (File.Exists(throwableClips))
        {
            using var clips = System.Text.Json.JsonDocument.Parse(File.ReadAllText(throwableClips));
            foreach (var clip in clips.RootElement.EnumerateObject()) _clips[clip.Name] = clip.Value.GetProperty("length").GetDouble();
        }
        foreach (var path in Directory.EnumerateFiles(Root, "*.dat"))
        {
            var text = File.ReadAllText(path); var d = new DatParser().Parse(text);
            if (!ushort.TryParse(d.GetString("ID"), out var id) || Assets.find(id) is not { } a) continue;
            string name = Path.GetFileNameWithoutExtension(path);
            if (a.type == EItemType.MELEE) MeleeDefs[id] = MeleeDef.FromDatText(name, text);
            if (a.type != EItemType.GUN) continue;
            var gun = GunDef.FromDatText(text); GunDefs[id] = gun;
            double reloadSeconds = 1.633;
            if (rig != null && rig.RootElement.TryGetProperty("anims", out var clips))
            {
                string cap = char.ToUpper(name[0]) + name[1..], donor = WeaponClips.AnimationDonorFor(name);
                System.Text.Json.JsonElement reload;
                if (clips.TryGetProperty(cap + "_Reload", out reload)
                    || (donor != null && clips.TryGetProperty(donor + "_Reload", out reload))
                    || clips.TryGetProperty("Gun_Reload", out reload)) reloadSeconds = reload.GetProperty("length").GetDouble();
            }
            _reloadSeconds[id] = reloadSeconds;
            Guns[id] = new ServerGunProfile { AssetName = Path.GetFileNameWithoutExtension(path),
                PlayerDamage = gun.PlayerDamage, ZombieDamage = gun.ZombieDamage, ResourceDamage = gun.Damage, AnimalDamage = gun.Damage,
                FalloffStart = gun.FalloffStart, FalloffEnd = gun.FalloffEnd, FalloffMin = gun.FalloffMin,
                ObjectDamage = gun.ObjectDamage, VehicleDamage = gun.VehicleDamage,
                FirerateTicks = gun.Firerate, CyclicRateRPM = gun.CyclicRateRPM,
                MuzzleVelocity = gun.MuzzleVelocity, BallisticSteps = gun.BallisticSteps,
                GravityMultiplier = gun.GravityMultiplier, MagCapacity = a.gunAmmoMax, Pellets = gun.Pellets,
                ReloadTicks = Math.Max(1, (int)Math.Round(reloadSeconds * 50)) };
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
        var consumeAnims = ConsumableRegistry.Anims(asset);
        string mesh = asset == null ? null : Existing(asset + (a.gunName != null ? "_gun.txt" : ".txt"));
        if (mesh == null && def?.Model != null) mesh = Existing("objects/" + def.Model + ".obj");
        string texture = asset == null ? null : Existing(asset + "_albedo.png");
        if (texture == null && def?.Model != null) texture = Existing("objects/" + def.Model + "_tex.png");
        return (object)new { id = a.id, name = a.itemName, type = a.type.ToString().ToLowerInvariant(), asset, mesh, texture,
            attachmentMesh = AttachmentNoise.MeshFor(a.id), attachmentTexture = AttachmentNoise.TexPathFor(a.id),
            icon = a.iconPath, width = a.size_x, height = a.size_y, stackSize = a.stackSize, slot = a.slot.ToString(),
            handKind = HandRules.KindOf(a).ToString().ToLowerInvariant(), canEquipFromBag = a.slot.CanEquipFromBag(),
            preferredSlot = a.slot.PreferredSlot(), tool = ToolDef.ById(a.id), gravityMultiplier = Umbrellas.For(a.id),
            throwable = Throwables.Find(a.id),
            consumeAnimation = a.IsConsumable ? new { equip = consumeAnims.Equip, use = consumeAnims.Use, seconds = consumeAnims.UseLen,
                leftHook = consumeAnims.LeftHook, sound = ConsumableRegistry.Sound(a.id), color = ConsumableRegistry.FlatColor(asset) } : null,
            gunAmmoMax = a.gunAmmoMax, gun = GunDefs.GetValueOrDefault(a.id), melee = MeleeDefs.GetValueOrDefault(a.id),
            repeatedHitSeconds = MeleeDefs.GetValueOrDefault(a.id)?.Repeated == true ? HandRules.RepeatedHitInterval : 0,
            reloadSeconds = _reloadSeconds.GetValueOrDefault(a.id), movementMultiplier = a.equipableMovementSpeedMultiplier,
            consume = a.IsConsumable ? new { health = a.useHealth, food = a.useFood, water = a.useWater,
                virus = a.useVirus, disinfectant = a.useDisinfectant, energy = a.useEnergy,
                stopsBleeding = a.useStopsBleeding, healsBroken = a.useHealBroken } : null,
            fluid = a.IsFluidContainer ? new { capacity = a.fluidCapacity, defaultType = a.fluidDefaultType,
                defaultQuality = a.fluidDefaultQuality } : null,
            fuelCapacity = a.fuelCapacity,
            isConsumable = a.IsConsumable, isFluidContainer = a.IsFluidContainer, isDeployable = def != null };
    }).ToArray();
    string Existing(string path) => File.Exists(Path.Combine(Root, path)) ? path : null;
}
