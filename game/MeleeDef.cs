using Godot;            // Color, for the spotlight tint
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
        // TODO(zombie-removal): ZombieDamage NOT removed despite the name -- PlayerController.ApplyMeleeHit
        // (off-limits, handled elsewhere) computes `dmg` from this field and applies it to BOTH the "zombies"
        // group AND the "animals" group (wildlife melee damage reuses the same number). Deleting the field
        // breaks that off-limits compile AND silently zeroes animal melee damage, which is non-zombie
        // behaviour -- ambiguous per the task's own "don't guess" rule. See the matching note in GunDef.cs.
        public float ZombieDamage = 45f, PlayerDamage = 45f, VehicleDamage = 10f, StructureDamage = 5f, ResourceDamage = 5f;
        public float Stamina;   // swing stamina cost (.dat Stamina, 0-100)
        public float Weak = 0.5f;     // weak-swing hit timing = this fraction of the swing clip (.dat Weak; ItemMeleeAsset.cs:91 default 0.5; 15/34 retail dats author it, 0.37-0.45)
        public float Strong = 0.33f;  // strong-swing hit timing fraction (.dat Strong; ItemMeleeAsset.cs:92 default 0.33 -- was wrongly 0.5 here; 11 dats take the default)
        public float Strength = 1.5f; // STRONG swing damage multiplier (.dat Strength; source: dmg *= strength on a strong swing)
        public float Alert;           // .dat Alert_Radius: a swing's noise radius (source AlertTool.alert); 0 = silent/stealthy
        public bool Repeated;   // .dat "Repeated": a continuous HOLD-to-use tool (blowtorch, chainsaw). Source ItemMeleeAsset: "'Repeated' melee weapons don't have strong attacks" -> LMB = continuous use (no weak click / no punch), RMB = nothing.
        public bool Repair;     // .dat "Repair": the continuous action REPAIRS the target (blowtorch) rather than damaging it.
        public bool SpotEnabled = true;    // .dat SpotLight_Enabled -- source lets a modder opt out of the player spotlight entirely (their example: a lightsaber glow that shouldn't cast a beam)
        public float SpotRange = 64f;      // .dat SpotLight_Range
        public float SpotAngleFull = 90f;  // .dat SpotLight_Angle. FULL cone angle, Unity's convention. Godot's SpotAngle is the HALF-angle, so this is halved at build time -- getting that wrong doubles the cone and looks like a bug in the beam rather than in the units.
        public float SpotIntensity = 1.3f; // .dat SpotLight_Intensity -> Godot LightEnergy. Source multiplies the COLOUR by intensity and pins light.intensity to 1.0; Godot separates them, so the colour stays normalised and this rides LightEnergy. Same result, and it dodges the >1 colour channel Nelson's own comment calls out as "very bright!".
        /// <summary>.dat SpotLight_Color, source default Color32(245, 223, 147) — a warm filament white, not pure white.</summary>
        public Color SpotColor = new Color(245f / 255f, 223f / 255f, 147f / 255f);

        public bool Light;      // .dat "Light": this melee item IS a flashlight. Source ItemMeleeAsset: `_isLight = p.data.ContainsKey("Light")` -- a bare key with no value, which is why this is a presence test and not a bool parse. The handheld torch is a MELEE asset in retail (flashlight.dat: Type Melee / Useable Melee / Slot Secondary), NOT the gun-rail tactical light, which is a separate ItemTacticalAsset flag on a separate code path.

        // Bare FISTS = the src's hardcoded empty-hand punch (PlayerEquipment.simulate_PunchInput): LMB left / RMB right,
        // 15 base dmg (x hit-zone), 1.75 m reach, ~every 0.1 s, no strong-swing multiplier (both fists equal).
        public static MeleeDef Fists => new()
        {
            Name = "fists", Range = 1.75f,
            ZombieDamage = 15f, PlayerDamage = 15f, VehicleDamage = 0f, StructureDamage = 2f, ResourceDamage = 20f,
            Weak = 0.5f, Strong = 0.33f, Strength = 1f, Stamina = 0f,
        };

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
                Weak = d.ParseFloat("Weak", 0.5f),
                Strong = d.ParseFloat("Strong", 0.33f),
                Strength = d.ParseFloat("Strength", 1.5f),
                Alert = d.ParseFloat("Alert_Radius", 0f),
                Repeated = d.ContainsKey("Repeated"),   // blowtorch/chainsaw: continuous hold, no weak/strong swings
                Repair = d.ContainsKey("Repair"),       // blowtorch: continuous action heals instead of damaging
                Light = d.ContainsKey("Light"),         // flashlight: held torch, toggled with the TACTICAL key
                // Beam shape is DATA, not a constant: source builds `new PlayerSpotLightConfig(p.data)` from the
                // same .dat, so a modded torch can carry its own. flashlight.dat declares none of these, so the
                // stock torch runs on every default below -- which is why they are written out rather than left
                // implicit. Defaults are source-exact (Player.cs PlayerSpotLightConfig(IDatDictionary)).
                SpotEnabled = d.ParseBool("SpotLight_Enabled", true),
                SpotRange = d.ParseFloat("SpotLight_Range", 64f),
                SpotAngleFull = d.ParseFloat("SpotLight_Angle", 90f),
                SpotIntensity = d.ParseFloat("SpotLight_Intensity", 1.3f),
            };
        }
    }
}
