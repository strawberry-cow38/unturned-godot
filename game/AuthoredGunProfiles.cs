using Godot;
using System;
using System.IO;
using System.Text.Json;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    /// <summary>Scoped content bridge for the new authored gun. Existing retail guns keep their old
    /// profile behavior; this does not silently rebalance the rest of multiplayer.</summary>
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
                    FirerateTicks = gun.Firerate, MuzzleVelocity = gun.MuzzleVelocity,
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
            var previous = server.Combat.ResolveHeldGunProfile;
            server.Combat.ResolveHeldGunProfile = owner =>
            {
                // Held id comes from the existing movement intent, but ownership is server inventory data.
                if (server.Players.TryGetHeldInput(owner, out var input) && input.HeldItemId == _itemId
                    && server.Inventories.TryGet(owner, out var entry) && entry.Inventory.getItemCount(_itemId) > 0)
                    return profile;
                return previous?.Invoke(owner);
            };
        }
    }
}
