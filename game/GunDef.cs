using SDG.Unturned; // DatParser + DatDictionaryEx (the game's own .dat accessors), from the ported UnturnedDat

namespace UnturnedGodot
{
    // A gun's stats read from a real Unturned ItemGunAsset .dat through the ported UnturnedDat layer,
    // using the SAME accessors (ParseFloat/ParseInt32/GetString) the game's ItemGunAsset uses -- so the
    // numbers are the real ones (Eaglefire: dmg 40/99, range 200, firerate 4, mag 30), not placeholders.
    public sealed class GunDef
    {
        public string Id;
        public string Action;   // .dat Action: Trigger/Bolt/Pump/Break/Rail...
        public bool IsShotgun => Action == "Pump" || Action == "Break";   // shell-calibre guns (no detachable-mag, so no +1 chamber)
        // Shell-by-shell reload for the PUMP tube (bluntforce): load ONE shell per interval, so firing mid-reload keeps what's
        // loaded. master WANTS this (a pump-shotgun feel) -- the earlier "feels completely wrong" was the reload SOUND replaying
        // once per shell, now fixed (the sound plays once at the start). A BREAK-action (masterkey) is NOT shell-by-shell: it
        // cracks open and loads all barrels at once.
        public bool ShellReload => Action == "Pump";
        public float PlayerDamage;
        public float ZombieDamage;
        public float VehicleDamage;   // Vehicle_Damage: bullets hurt vehicles LESS than zombies (eaglefire 35 vs 99) -- was wrongly using ZombieDamage
        public float ObjectDamage;    // Object_Damage: bullets vs destructible props (rubble); mirrors ServerGunProfile.ObjectDamage (eaglefire 25)
        public float Range;
        public float SpreadAngleDegrees;
        public float SpreadAim = 1f;   // spread multiplier while aiming (Eaglefire 0.05 = 5% of hip spread)
        public int Firerate;   // sim ticks between shots (lower = faster); cooldown = Firerate / 50 s
        public int AmmoMax;
        public int MagazineId;   // .dat Magazine: the default magazine item id
        public int Caliber;      // .dat Caliber: mags with a matching caliber can be loaded
        // Per-shot rechamber (source ItemGunAsset): Bolt & Pump guns must CYCLE the action (bolt-cycle / pump) after each shot
        // before they can fire, aim or reload again. RechamberAfterShotCount 0 = self-loading (semi/auto). Delay = seconds after
        // the shot before the "Hammer" (bolt-cycle) animation plays.
        public int RechamberAfterShotCount;
        public float RechamberAfterShotDelay;
        public bool NeedsRechamber => RechamberAfterShotCount > 0;
        public int Pellets = 1;   // rays fired per shot (source: the magazine's Pellets; shotgun shells = 8). Each
                                  // pellet is deviated within the spread cone -> the shotgun spread pattern.
        // Per-shot camera recoil (degrees): X = horizontal (yaw, random sign), Y = vertical (pitch up). The aim
        // gets kick*Recover then recovers; the gun viewmodel gets the full kick (UseableGun.cs:1049/1188/1191).
        public float RecoilMinX, RecoilMaxX, RecoilMinY, RecoilMaxY;
        public float RecoverX, RecoverY;
        // Per-shot viewmodel-camera SHAKE (metres): each shot adds a random offset in [min,max] per axis to
        // the recoil viewmodel-camera spring, which springs back to rest (UseableGun.cs:921 reads these into
        // PlayerAnimator.AddRecoilViewmodelCameraOffset -> recoilViewmodelCameraOffset). Eaglefire's is Z-heavy
        // (-0.01..-0.02): the gun punches back toward the camera each shot. Distinct from the aim recoil above.
        public float ShakeMinX, ShakeMinY, ShakeMinZ, ShakeMaxX, ShakeMaxY, ShakeMaxZ;
        // Firemodes (flags in the .dat): the Eaglefire has Safety + Semi + Bursts 3.
        public bool HasSafety, HasSemi, HasAuto;
        public int BurstCount;   // Bursts value; 0 = no burst mode
        // Ballistics (ItemGunAsset.cs:679-733): bullets are simulated projectiles, not hitscan. MuzzleVelocity =
        // Ballistic_Travel * 50 (TOCK_PER_SECOND); the bullet steps every 0.02s and drops under Physics.gravity
        // (-9.81) * GravityMultiplier. No ballistic keys -> travel 10m/step, steps = ceil(range/10), gravMult 4.
        public float MuzzleVelocity;      // m/s (Eaglefire 500, Masterkey 1500)
        public int BallisticSteps;        // 0.02s steps before the bullet despawns (Eaglefire 20 = 0.4s to 200m)
        public float GravityMultiplier;   // bullet gravity = -9.81 * this (Eaglefire 4 -> ~3m drop over 200m)

        public static GunDef FromDatText(string datText)
        {
            IDatDictionary d = new DatParser().Parse(datText);
            var g = new GunDef
            {
                Id = d.GetString("ID"),
                Action = d.GetString("Action"),
                PlayerDamage = d.ParseFloat("Player_Damage"),
                ZombieDamage = d.ParseFloat("Zombie_Damage"),
                VehicleDamage = d.ParseFloat("Vehicle_Damage", 40f),   // .dat Vehicle_Damage (all stock guns specify it; 40 = fallback)
                ObjectDamage = d.ParseFloat("Object_Damage", 25f),     // .dat Object_Damage vs destructible props (eaglefire 25); matches the server profile
                Range = d.ParseFloat("Range", 100f),
                Firerate = d.ParseInt32("Firerate", 8),
                AmmoMax = d.ParseInt32("Ammo_Max", 30),
                MagazineId = d.ParseInt32("Magazine", 0),   // default magazine item id (eaglefire/maplestrike = 6, the Military STANAG)
                Caliber = d.ParseInt32("Caliber", 0),       // which magazines fit: a mag's caliber must match (eaglefire caliber 1)
                Pellets = d.ParseInt32("Pellets", 1),   // staged into the shotgun's .dat from its Shells_2 mag (8)
                SpreadAngleDegrees = d.ParseFloat("Spread_Angle_Degrees"),
                SpreadAim = d.ParseFloat("Spread_Aim", 1f),
                RecoilMinX = d.ParseFloat("Recoil_Min_X"),
                RecoilMaxX = d.ParseFloat("Recoil_Max_X"),
                RecoilMinY = d.ParseFloat("Recoil_Min_Y"),
                RecoilMaxY = d.ParseFloat("Recoil_Max_Y"),
                RecoverX = d.ParseFloat("Recover_X", 0.4f),
                RecoverY = d.ParseFloat("Recover_Y", 0.4f),
                ShakeMinX = d.ParseFloat("Shake_Min_X"),
                ShakeMinY = d.ParseFloat("Shake_Min_Y"),
                ShakeMinZ = d.ParseFloat("Shake_Min_Z"),
                ShakeMaxX = d.ParseFloat("Shake_Max_X"),
                ShakeMaxY = d.ParseFloat("Shake_Max_Y"),
                ShakeMaxZ = d.ParseFloat("Shake_Max_Z"),
                HasSafety = d.ContainsKey("Safety"),
                HasSemi = d.ContainsKey("Semi"),
                HasAuto = d.ContainsKey("Auto"),
                BurstCount = d.ParseInt32("Bursts", 0),
            };
            int defaultRechamber = (g.Action == "Bolt" || g.Action == "Pump") ? 1 : 0;   // source ItemGunAsset: Bolt & Pump default to rechambering after each shot
            g.RechamberAfterShotCount = d.ParseInt32("RechamberAfterShotCount", defaultRechamber);
            g.RechamberAfterShotDelay = d.ParseFloat("RechamberAfterShotDelay", 0.25f);   // source default 0.25s
            ComputeBallistics(g, d);
            return g;
        }

        // Port of ItemGunAsset's ballistic setup (679-733): resolve steps/travel (from the .dat or the range), then
        // muzzle velocity + the gravity multiplier (Ballistic_Drop -> a computed value, else Bullet_Gravity_Multiplier
        // default 4). Bullets then simulate at 0.02s: pos += vel*0.02, vel.y += (-9.81*GravityMultiplier)*0.02.
        static void ComputeBallistics(GunDef g, IDatDictionary d)
        {
            int steps = d.ParseInt32("Ballistic_Steps", 0);
            float travel = d.ParseFloat("Ballistic_Travel", 0f);
            bool hasSteps = d.ContainsKey("Ballistic_Steps") && steps > 0;
            bool hasTravel = d.ContainsKey("Ballistic_Travel") && travel > 0.1f;
            if (hasSteps && hasTravel) { /* both specified -> use as given */ }
            else if (hasSteps) travel = g.Range / steps;
            else if (hasTravel) steps = Godot.Mathf.CeilToInt(g.Range / travel);
            else { travel = 10f; steps = Godot.Mathf.CeilToInt(g.Range / 10f); }
            g.BallisticSteps = System.Math.Max(1, steps);
            g.MuzzleVelocity = travel * 50f;   // TOCK_PER_SECOND = 50
            if (d.ContainsKey("Ballistic_Drop"))
            {
                float drop = d.ParseFloat("Ballistic_Drop", 0f);
                if (drop < 1e-6f) g.GravityMultiplier = 0f;
                else
                {
                    float sum = 0f; Godot.Vector2 r = Godot.Vector2.Right;
                    for (int l = 0; l < steps; l++) { sum += r.Y * travel; r.Y -= drop; r = r.Normalized(); }
                    float t = steps * 0.02f;
                    g.GravityMultiplier = (2f * sum / (t * t)) / -9.81f;
                }
            }
            else g.GravityMultiplier = d.ParseFloat("Bullet_Gravity_Multiplier", 4f);
        }
    }
}
