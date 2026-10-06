using Godot;
using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    /// <summary>Scoped content bridge for the authored MAC-10 and Mossberg single-shell reload.
    /// Other guns keep their profile behavior; Mossberg clones preserve host damage/cadence tuning.</summary>
    internal static class AuthoredGunProfiles
    {
        const string Name = "mac10";
        static ServerGunProfile _profile;
        static ushort _itemId;


        internal static ServerGunProfile Profile
        {
            get
            {
                if (_profile != null) return _profile;
                var gun = GunDef.FromDatText(File.ReadAllText(ProjectSettings.GlobalizePath($"res://content/{Name}.dat")));
                _itemId = ushort.Parse(gun.Id, System.Globalization.CultureInfo.InvariantCulture);
                string cap = char.ToUpper(Name[0]) + Name[1..];
                string donor = Viewmodel.AnimationDonorFor(Name);
                using var rig = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("res://content/rig.json")));
                var clips = rig.RootElement.GetProperty("anims");
                JsonElement reload;
                bool own = clips.TryGetProperty(cap + "_Reload", out reload);
                if (!own && (donor == null || !clips.TryGetProperty(donor + "_Reload", out reload)))
                    reload = clips.GetProperty("Gun_Reload");
                _profile = new ServerGunProfile
                {
                    AssetName = Name, PlayerDamage = gun.Damage, ZombieDamage = gun.Damage,
                    ObjectDamage = gun.ObjectDamage, VehicleDamage = gun.VehicleDamage,
                    FirerateTicks = gun.Firerate, CyclicRateRPM = gun.CyclicRateRPM, MuzzleVelocity = gun.MuzzleVelocity,
                    BallisticSteps = gun.BallisticSteps, GravityMultiplier = gun.GravityMultiplier,
                    MagCapacity = gun.AmmoMax, Pellets = 1,
                    ReloadTicks = Math.Max(1, (int)Math.Round(reload.GetProperty("length").GetDouble() * 50)),
                };
                return _profile;
            }
        }

        internal static void Install(NetWorldServer server)
        {
            var profile = Profile;
            var mossbergProfiles = new Dictionary<ushort, ServerGunProfile>();
            using var mossbergClips = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("res://content/mossberg_anims.json")));
            double insertSeconds = mossbergClips.RootElement.GetProperty("Bluntforce_Reload_OneShell").GetProperty("length").GetDouble();
            var previous = server.Combat.ResolveHeldGunProfile;
            server.Combat.ResolveHeldGunProfile = owner =>
            {
                // Held id comes from the existing movement intent, but ownership is server inventory data.
                if (server.Players.TryGetHeldInput(owner, out var input) && input.HeldItemId == _itemId
                    && server.Inventories.TryGet(owner, out var entry) && entry.Inventory.getItemCount(_itemId) > 0)
                    return profile;
                if (server.Players.TryGetHeldInput(owner, out var held) && (held.HeldItemId == 112 || held.HeldItemId == 9148)
                    && server.Inventories.TryGet(owner, out var inventory) && inventory.Inventory.getItemCount(held.HeldItemId) > 0)
                {
                    if (!mossbergProfiles.TryGetValue(owner, out var mossbergProfile))
                        mossbergProfiles[owner] = mossbergProfile = (previous?.Invoke(owner) ?? server.Combat.DefaultGun).SingleShellReload("bluntforce", 8, 1);
                    mossbergProfile.AssetName = held.HeldItemId == 9148 ? "bluntforce_wood" : "bluntforce";
                    float speed = server.Skills.TryGet(owner, out var skills) ? skills.Skills.DexterityReloadSpeed() : 1f;
                    // Avoid an extra tick from binary floating-point 1.1 * 50 = 55.00000000000001.
                    mossbergProfile.ReloadTicks = Math.Max(1, (int)Math.Ceiling(insertSeconds * 50 / speed - 1e-6));
                    return mossbergProfile;
                }
                return previous?.Invoke(owner);
            };
        }
    }
}
