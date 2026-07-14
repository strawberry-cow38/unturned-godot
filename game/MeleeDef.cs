using SDG.Unturned;   // DatParser + IDatDictionary (the ported UnturnedDat accessors, same as GunDef)

namespace UnturnedGodot
{
    // A melee weapon's stats from a real Unturned ItemMeleeAsset .dat -- swing Range + per-target damage + swing
    // stamina, through the SAME DatParser as GunDef so the numbers are real: Military Knife = range 1.75, 50 dmg
    // (anti-personnel); Sledgehammer = range 2.25, 34 dmg but 40 vs vehicles / 20 structures (anti-structure).
    // A null MeleeDef = bare fists (the generic 45-dmg / 2.2 m punch fallback).
    public sealed class MeleeDef
    {
        public string Name = "fists";
        public float Range = 2.2f;
        public float ZombieDamage = 45f, PlayerDamage = 45f, VehicleDamage = 10f, StructureDamage = 5f, ResourceDamage = 5f;
        public float Stamina;   // swing stamina cost (.dat Stamina, 0-100)
        public float Strong = 0.5f;   // strong-swing timing fraction (.dat Strong)
        public float Strength = 1.5f; // STRONG swing damage multiplier (.dat Strength; source: dmg *= strength on a strong swing)
        public float Alert;           // .dat Alert_Radius: a swing's noise radius (source AlertTool.alert); 0 = silent/stealthy

        public static MeleeDef FromDatText(string name, string datText)
        {
            IDatDictionary d = new DatParser().Parse(datText);
            return new MeleeDef
            {
                Name = name,
                Range = d.ParseFloat("Range", 2.2f),
                ZombieDamage = d.ParseFloat("Zombie_Damage", 45f),
                PlayerDamage = d.ParseFloat("Player_Damage", 45f),
                VehicleDamage = d.ParseFloat("Vehicle_Damage", 10f),
                StructureDamage = d.ParseFloat("Structure_Damage", 5f),
                ResourceDamage = d.ParseFloat("Resource_Damage", 5f),
                Stamina = d.ParseFloat("Stamina", 0f),
                Strong = d.ParseFloat("Strong", 0.5f),
                Strength = d.ParseFloat("Strength", 1.5f),
                Alert = d.ParseFloat("Alert_Radius", 0f),
            };
        }
    }
}
