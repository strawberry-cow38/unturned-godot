using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Asset Factory gun presets (master 2026-07-23 "selectable presets for all existing weapons"): every real weapon
    // .dat in content/ is a selectable STARTING POINT for gun-making. Load a preset -> its full stats land in the
    // bundle's gun_* params (the inverse of PlayerController.ApplyFactoryGunStats) -> tweak / reskin from there.
    // Guns only (Useable Gun), not melee/throwables.
    public static class GunPresets
    {
        static List<string> _names;

        public static List<string> Names()
        {
            if (_names != null) return _names;
            _names = new List<string>();
            foreach (var f in DirAccess.GetFilesAt("res://content/"))
            {
                if (f == null || !f.EndsWith(".dat")) continue;
                var text = ReadText($"res://content/{f}");
                if (text == null || !text.Contains("Useable Gun")) continue;   // guns only (melee = Useable Melee)
                _names.Add(f.Substring(0, f.Length - 4));   // strip .dat
            }
            _names.Sort();
            return _names;
        }

        // Load a preset weapon's REAL stats into the bundle's gun_* params. Every knob ApplyFactoryGunStats reads is
        // written here, so "pick Maplestrike" gives a factory gun that plays exactly like a Maplestrike until you tweak it.
        public static void WriteToBundle(AssetBundle b, string preset)
        {
            if (b == null || string.IsNullOrEmpty(preset)) return;
            var text = ReadText($"res://content/{preset}.dat");
            if (text == null) { GD.PushWarning($"[gunpresets] preset '{preset}' not found"); return; }
            var g = GunDef.FromDatText(text);
            b.SetParam("gun_damage", g.PlayerDamage);
            b.SetParam("gun_vehicle_damage", g.VehicleDamage);
            b.SetParam("gun_object_damage", g.ObjectDamage);
            b.SetParam("gun_range", g.Range);
            b.SetParam("gun_rpm", g.Firerate > 0 ? Mathf.Round(3000f / g.Firerate) : 0f);   // Firerate(ticks) -> rpm for the editor field
            b.SetParam("gun_ammo", (float)g.AmmoMax);
            b.SetParam("gun_caliber", (float)g.Caliber);
            b.SetParam("gun_spread", g.SpreadAngleDegrees);
            b.SetParam("gun_spread_aim", g.SpreadAim);
            b.SetParam("gun_pellets", (float)g.Pellets);
            b.SetParam("gun_recoil_min_x", g.RecoilMinX); b.SetParam("gun_recoil_max_x", g.RecoilMaxX);
            b.SetParam("gun_recoil_min_y", g.RecoilMinY); b.SetParam("gun_recoil_max_y", g.RecoilMaxY);
            b.SetParam("gun_recover_x", g.RecoverX); b.SetParam("gun_recover_y", g.RecoverY);
            b.SetParam("gun_shake_min_x", g.ShakeMinX); b.SetParam("gun_shake_max_x", g.ShakeMaxX);
            b.SetParam("gun_shake_min_y", g.ShakeMinY); b.SetParam("gun_shake_max_y", g.ShakeMaxY);
            b.SetParam("gun_shake_min_z", g.ShakeMinZ); b.SetParam("gun_shake_max_z", g.ShakeMaxZ);
            b.SetParam("gun_muzzle_velocity", g.MuzzleVelocity);
            b.SetParam("gun_gravity", g.GravityMultiplier);
            b.SetParam("gun_ballistic_steps", (float)g.BallisticSteps);
            b.SetParam("gun_burst", (float)g.BurstCount);
            b.SetParam("gun_action", g.Action);
            b.SetParam("gun_auto", g.HasAuto); b.SetParam("gun_semi", g.HasSemi); b.SetParam("gun_safety", g.HasSafety);
            b.SetParam("gun_preset", preset);   // remember the source weapon
            GD.Print($"[gunpresets] '{preset}' -> params: dmg {g.PlayerDamage} spread {g.SpreadAngleDegrees} pellets {g.Pellets} recoilY {g.RecoilMinY}..{g.RecoilMaxY} vel {g.MuzzleVelocity} auto {g.HasAuto}");
        }

        static string ReadText(string resPath)
        {
            using var f = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
            return f?.GetAsText();
        }
    }
}
