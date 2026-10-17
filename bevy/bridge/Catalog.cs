using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

public sealed class Catalog
{
    MountedWeaponCatalog _mounted;
    public MountedWeaponCatalog Mounted => _mounted ??= new(Root);
    public readonly List<BlueprintDef> Recipes = new();
    public readonly Dictionary<ushort, ServerGunProfile> Guns = new();
    public readonly Dictionary<ushort, GunDef> GunDefs = new();
    public readonly Dictionary<ushort, MeleeDef> MeleeDefs = new();
    readonly Dictionary<ushort, double> _reloadSeconds = new();
    readonly Dictionary<string, double> _clips = new();
    /// <summary>A first-person clip's length in seconds (rig.json plus the per-item tables), 0 if unknown.</summary>
    public double ClipSeconds(string clip) => _clips.GetValueOrDefault(clip);
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
        _mounted = null;
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
        // Throwable and consumable/tool hand banks (the vehicle tools' Paint_/Tire_/Jack_ clips live in the latter).
        foreach (string bank in new[] { "throwable_anims.json", "consumable_anims.json" })
        {
            string bankPath = Path.Combine(Root, bank);
            if (!File.Exists(bankPath)) continue;
            using var clips = System.Text.Json.JsonDocument.Parse(File.ReadAllText(bankPath));
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
                // Source Fire/SpawnBullet uses canonical Damage for living targets;
                // current handheld .dat files no longer have Player/Zombie_Damage.
                PlayerDamage = gun.Damage, ZombieDamage = gun.Damage, ResourceDamage = gun.Damage, AnimalDamage = gun.Damage,
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
            hasCondition = Durability.HasCondition(a), rarity = a.rarity.ToString().ToLowerInvariant(),
            handKind = HandRules.KindOf(a).ToString().ToLowerInvariant(), canEquipFromBag = a.slot.CanEquipFromBag(),
            preferredSlot = a.slot.PreferredSlot(), tool = ToolDef.ById(a.id), gravityMultiplier = Umbrellas.For(a.id),
            throwable = Throwables.Find(a.id), handTool = HandTool(a),
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
    /// <summary>Source vehicle hand tools: EquipHeldSpraypaint / EquipHeldTire /
    /// EquipHeldCarjack viewmodels, and the use timelines of TrySprayVehicle,
    /// TryFitTire and TryCarjack (useTime = the Use clip, else the fallback).</summary>
    object HandTool(ItemAsset a)
    {
        double Use(string clip, float fallback) => _clips.TryGetValue(clip, out double length) && length > .05 ? length : fallback;
        return HandRules.KindOf(a) switch
        {
            HandRules.HandKind.Paint => new { kind = "paint", equip = "Paint_Equip", use = "Paint_Use",
                useSeconds = Use("Paint_Use", HandRules.PaintFallbackUseSeconds), applyFraction = HandRules.PaintApplyFraction,
                mesh = Existing("spraypaint.txt"), texture = (string)null, paintRgb = VehicleHandToolServices.Rgb(a.id) },
            // EmptyHands takes precedence over ConsumableEquipClip in Viewmodel's
            // equip selection, so the spare's hold is the generic Melee_Equip.
            HandRules.HandKind.Tire => new { kind = "tire", equip = "Melee_Equip", use = "Tire_Use",
                useSeconds = Use("Tire_Use", HandRules.TireFallbackUseSeconds), applyFraction = HandRules.TireFitFraction,
                mesh = (string)null, texture = (string)null, paintRgb = (uint?)null },
            HandRules.HandKind.Carjack => new { kind = "carjack", equip = "Jack_Equip", use = "Jack_Use",
                useSeconds = Use("Jack_Use", HandRules.CarjackFallbackUseSeconds), applyFraction = HandRules.CarjackApplyFraction,
                mesh = Existing("carjack.txt"), texture = Existing("carjack_albedo.png"), paintRgb = (uint?)null },
            // EquipHeldRestraint: EmptyHands (so Melee_Equip wins the equip precedence) with the
            // restraint's own Use clip; RestraintUseSeconds = that clip, else 1.300 / 1.633.
            // The cuffs land when the clip ends (applyFraction 1); the key's unlock is sent at the click.
            HandRules.HandKind.Restraint => new { kind = "restraint", equip = "Melee_Equip", use = "Cuff_Use",
                useSeconds = Use("Cuff_Use", 1.300f), applyFraction = 1f,
                mesh = (string)null, texture = (string)null, paintRgb = (uint?)null },
            HandRules.HandKind.RestraintKey => new { kind = "restraintKey", equip = "Melee_Equip", use = "Key_Use",
                useSeconds = Use("Key_Use", 1.633f), applyFraction = 0f,
                mesh = (string)null, texture = (string)null, paintRgb = (uint?)null },
            // EquipHeldOptic: the 333 carry model on the NaturalHold carry (Fuel_Equip is not in the
            // rig, so the source falls back to Deploy_Equip); no Use clip.
            HandRules.HandKind.Optic => new { kind = "optic", equip = _clips.ContainsKey("Fuel_Equip") ? "Fuel_Equip" : "Deploy_Equip", use = "",
                useSeconds = 0f, applyFraction = 0f,
                mesh = Existing("items/333.txt"), texture = Existing("items/333.png"), paintRgb = (uint?)null },
            _ => (object)null,
        };
    }
    string Existing(string path) => File.Exists(Path.Combine(Root, path)) ? path : null;
}
