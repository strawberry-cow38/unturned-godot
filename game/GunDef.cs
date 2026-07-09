using SDG.Unturned; // DatParser + DatDictionaryEx (the game's own .dat accessors), from the ported UnturnedDat

namespace UnturnedGodot
{
    // A gun's stats read from a real Unturned ItemGunAsset .dat through the ported UnturnedDat layer,
    // using the SAME accessors (ParseFloat/ParseInt32/GetString) the game's ItemGunAsset uses -- so the
    // numbers are the real ones (Eaglefire: dmg 40/99, range 200, firerate 4, mag 30), not placeholders.
    public sealed class GunDef
    {
        public string Id;
        public float PlayerDamage;
        public float ZombieDamage;
        public float Range;
        public float SpreadAngleDegrees;
        public float SpreadAim = 1f;   // spread multiplier while aiming (Eaglefire 0.05 = 5% of hip spread)
        public int Firerate;   // sim ticks between shots (lower = faster); cooldown = Firerate / 50 s
        public int AmmoMax;
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

        public static GunDef FromDatText(string datText)
        {
            IDatDictionary d = new DatParser().Parse(datText);
            return new GunDef
            {
                Id = d.GetString("ID"),
                PlayerDamage = d.ParseFloat("Player_Damage"),
                ZombieDamage = d.ParseFloat("Zombie_Damage"),
                Range = d.ParseFloat("Range", 100f),
                Firerate = d.ParseInt32("Firerate", 8),
                AmmoMax = d.ParseInt32("Ammo_Max", 30),
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
        }
    }
}
