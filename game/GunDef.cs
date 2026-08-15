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
        public bool IsShotgun => Action == "Pump" || Action == "Break";   // shell-calibre guns

        /// <summary>Can this gun carry a round in the chamber on top of a full magazine (the "30 + 1")?
        ///
        /// NO SOURCE ANSWER EXISTS. Retail has rechambering (bolt/pump cycling) but its reload just fills to
        /// Ammo_Max -- there is no +1 concept in UseableGun or ItemGunAsset at all. So this is the port's own design
        /// call, settled by master:
        ///   bolt   -> YES. A real bolt action tops the magazine up with one already chambered.
        ///   pump   -> YES, ghost loading: cycle a shell into the chamber, then refill the tube behind it.
        ///   break  -> no. Both barrels ARE the chambers; there is nowhere else to put a shell.
        ///   string -> no (bows, crossbow). Master: "thats just dumb."
        ///   rocket -> no. One tube, one rocket.
        ///   trigger-> YES for magazine-fed, NO for revolvers -- the cylinder IS the chambers.
        ///
        /// THE REVOLVER CASE IS NOT DERIVABLE FROM Action. Every revolver in this port reads `Action Trigger`,
        /// identical to the Colt and the Eaglefire; the .dat carries no feed or magazine field to separate them. So
        /// it keys on Real_Weapon, which names the actual firearm -- "Colt Python" is a revolver as a matter of fact
        /// rather than of taste, so this list is checkable by a human instead of being a table you have to trust.
        ///
        /// (Note schofield reads `Action Bolt` with Real_Weapon "Mosin-Nagant" -- despite the name it is modelled as
        /// a bolt rifle here, not a revolver, so it correctly keeps its +1 with no exception needed.)</summary>
        public bool HasChamberRound => Action switch
        {
            // Minigun: a rotary/belt-fed gun has no single chamber to top up (Fury). Same "nowhere to put a +1" as
            // break-action, so it joins the no-chamber list rather than falling through to the magazine-fed default.
            "Break" or "String" or "Rocket" or "Minigun" => false,
            _ => !IsRevolver,
        };

        /// <summary>Revolvers, identified by the real firearm they model. Extend this when one is added -- the
        /// alternative is inventing a feed field the extracted .dat files do not have.</summary>
        static readonly string[] RevolverModels = { "Python", "Revolver", "Smith & Wesson", "Peacemaker", "Anaconda", "Redhawk", "Magnum" };
        public bool IsRevolver
        {
            get
            {
                if (string.IsNullOrEmpty(RealWeapon)) return false;
                foreach (var m in RevolverModels)
                    if (RealWeapon.Contains(m, System.StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }
        // Shell-by-shell reload for the PUMP tube (bluntforce): load ONE shell per interval, so firing mid-reload keeps what's
        // loaded. master WANTS this (a pump-shotgun feel) -- the earlier "feels completely wrong" was the reload SOUND replaying
        // once per shell, now fixed (the sound plays once at the start). A BREAK-action (masterkey) is NOT shell-by-shell: it
        // cracks open and loads all barrels at once.
        public bool ShellReload => Action == "Pump";
        // THE canonical per-shot damage (strawberry 2026-08-15: "standardize player and zombie damage as one
        // DAMAGE field. and apply a multiplier later"). One number per cartridge; what a target actually takes is
        // this times a per-target multiplier -- humanoid zones (Humanoid.HeadMult/Torso/Leg) or zombie limbs
        // (ZombieCombat.MultFor). Falls back to Player_Damage so a .dat that predates the merge still loads.
        public float Damage;
        // DISTANCE FALLOFF (strawberry 2026-08-15: "i also dont want the range to be just a bullet nope wall
        // that deletes past it. the killer is the damage dropoff over distance"). Full Damage out to Start,
        // decaying linearly to Damage*Min at End, floored at Min beyond. Start<=0 disables it entirely, so a
        // gun that declares nothing behaves exactly as before.
        // ---- LEARNABLE RECOIL PATTERN (strawberry 2026-08-15) ----------------------------------------
        // A fixed per-gun path the crosshair walks while you hold the trigger, with a small per-node
        // randomness on top. Deterministic where it matters: the first rounds land essentially ON the
        // pattern, so tapping and short bursts are exact and learning the path pays every time.
        //
        // Design rules, all from strawberry: vertical NEVER decreases (recoil accumulates while held);
        // drift is one smooth direction with no snaps; node SPACING grows down the burst so early rounds
        // cluster and late ones jump; everything leans RIGHT because the support arm resists a leftward
        // push. Shape differs by WHEN the drift arrives (PatternDriftExp), not by exotic curves.
        public float PatternClimb;        // total vertical travel over the pattern (deg)
        public int PatternNodes = 30;     // pattern length; a belt-fed gun outlives it, see PatternPlateau
        public float PatternDrift;        // lateral travel by the last node (deg, + = right)
        public float PatternDriftExp = 1.9f;   // >1 holds straight then eases out; ~1 drifts from shot one
        public int PatternDriftStart;     // node the drift begins (M249 stays dead straight for 5)
        public float PatternWiggle;       // overcorrection amplitude -- you fighting the drift and overshooting
        public float PatternSway;         // late oscillation amplitude (M249 only; 0 = none)
        public int PatternSwayStart;      // node the sway begins
        public float PatternPlateau;      // extra climb added asymptotically PAST PatternNodes, so a 100-round
                                          // belt does not walk the crosshair off the screen by round 60
        /// <summary>Per-gun scope-sway scale (1 = the shared default). A gun with a bipod, a heavy barrel or
        /// simply a steadier platform should hold its scope better; the sway oscillator is otherwise identical
        /// for every optic, which makes a police DMR wobble exactly like a hunting rifle.</summary>
        public float ScopeSwayScale = 1f;
        public float ScatterH = 1f, ScatterV = 1f;   // per-axis randomness scalars (the AUG's grip kills H, not V)
        public bool ScatterTightEarly;    // M249: near-zero for the first rounds, opening hard once the sway starts

        /// <summary>Cumulative (horizontal, vertical) pattern offset in degrees after <paramref name="shot"/>
        /// rounds of a held burst. Shot 1 is the first round. The per-shot impulse is the DELTA between
        /// consecutive calls, which is what the fire path actually applies.</summary>
        public (float H, float V) PatternAt(int shot)
        {
            if (PatternNodes <= 0 || PatternClimb <= 0f) return (0f, 0f);
            int n = PatternNodes;
            float t = (shot < n ? shot : n) / (float)n;
            float v = PatternClimb * Pow(t, 1.55f);
            if (shot > n && PatternPlateau > 0f)
                v += PatternPlateau * (1f - Exp(-(shot - n) / (n * 0.93f)));
            float h = 0f;
            if (PatternDrift != 0f)
            {
                int ds = PatternDriftStart;
                if (shot > ds)
                {
                    float dt = (float)((shot < n ? shot : n) - ds) / (n - ds);
                    h = PatternDrift * Pow(dt, PatternDriftExp);
                }
                if (shot > n && PatternPlateau > 0f)
                    h += PatternDrift * 0.45f * (1f - Exp(-(shot - n) / (n * 1.33f)));
            }
            if (PatternWiggle != 0f) h += PatternWiggle * Sin(shot / 2.15f) * Pow(t, 1.15f);
            if (PatternSway != 0f && shot > PatternSwayStart)
            {
                float g = (float)(shot - PatternSwayStart) / System.Math.Max(1, n - PatternSwayStart);
                h += PatternSway * Sin((shot - PatternSwayStart) / 2.6f) * (g < 1f ? g : 1f);
            }
            return (h, v);
        }

        /// <summary>Randomness half-angle (deg) for a given round of the burst, per axis.</summary>
        public float ScatterAt(int shot, bool horizontal)
        {
            float s = horizontal ? ScatterH : ScatterV;
            float t = (shot < PatternNodes ? shot : PatternNodes) / (float)(PatternNodes > 0 ? PatternNodes : 30);
            if (ScatterTightEarly)
            {
                float e = shot - 4 > 0 ? (shot - 4) : 0;
                float en = e / (PatternNodes > 0 ? PatternNodes : 30);
                return s * (0.005f + 0.30f * en * en);
            }
            return s * (0.04f + 0.55f * Pow(t, 1.6f));
        }
        static float Pow(float a, float b) => (float)System.Math.Pow(a, b);
        static float Exp(float a) => (float)System.Math.Exp(a);
        static float Sin(float a) => (float)System.Math.Sin(a);

        public float FalloffStart;      // Damage_Falloff_Start (m)
        public float FalloffEnd;        // Damage_Falloff_End (m)
        public float FalloffMin = 1f;   // Damage_Falloff_Min (fraction of Damage at End and beyond)

        /// <summary>Damage multiplier at a given travel distance. One implementation, shared by the SP fire
        /// path and the server, so a shot cannot fall off differently depending on who resolved it.</summary>
        public float FalloffAt(float metres)
        {
            if (FalloffStart <= 0f || FalloffEnd <= FalloffStart) return 1f;
            if (metres <= FalloffStart) return 1f;
            if (metres >= FalloffEnd) return FalloffMin;
            float t = (metres - FalloffStart) / (FalloffEnd - FalloffStart);
            return 1f + (FalloffMin - 1f) * t;
        }
        public float PlayerDamage;
        public float ZombieDamage;
        public float VehicleDamage;   // Vehicle_Damage: bullets hurt vehicles LESS than zombies (eaglefire 35 vs 99) -- was wrongly using ZombieDamage
        public float ObjectDamage;    // Object_Damage: bullets vs destructible props (rubble); mirrors ServerGunProfile.ObjectDamage (eaglefire 25)

        // IMPACT BLAST. A round that detonates where it lands, rather than only damaging what it hit.
        //
        // These are PORT fields, not retail ones. Retail puts a blast on the MAGAZINE (ItemMagazineAsset carries the
        // explosion + its damages), which is the better model long-term because it is the ROUND that explodes, not the
        // gun -- but the port has no magazine-asset layer to hang it on yet, and the launcher's own `Explosion 45` key
        // is an EFFECT id, not a radius, so it cannot supply these numbers either.
        //
        // What this replaces is worth naming: the rocket launcher's blast was four literals inline in the fire path,
        // reached by `if (Gun?.Action == "Rocket")`. That is fine for exactly one gun and stops being fine at two --
        // the second one either copies the branch or silently gets the first one's numbers. BlastRadius 0 = no blast,
        // which is every gun that does not ask for one.
        public float BlastRadius;           // Explosion_Radius, metres. 0 = this round does not detonate
        public float BlastZombieDamage;     // Explosion_Zombie_Damage  -- linear falloff (DamageTool.explode)
        public float BlastPlayerDamage;     // Explosion_Player_Damage  -- SQUARED falloff, and cut by explosion armour
        public float BlastVehicleDamage;    // Explosion_Vehicle_Damage -- linear falloff
        public float Range;
        public float SpreadAngleDegrees;
        public float SpreadAim = 1f;   // spread multiplier while aiming (Eaglefire 0.05 = 5% of hip spread)
        public int Firerate;   // sim ticks between shots (lower = faster); cooldown = Firerate / 50 s
        public int AmmoMax;
        public int MagazineId;   // .dat Magazine: the default magazine item id
        public int Caliber;      // .dat Caliber: mags with a matching caliber can be loaded
        // The REAL-WORLD cartridge, and the real firearm the gun is modelled on. Separate from Caliber above, which is
        // Unturned's abstract magazine-compatibility GROUP -- the two are different axes and must not be conflated: the
        // Mosin (schofield, caliber 5), the SVD (snayperskya, 11) and the PKM (nykorev, 10) all fire 7.62x54mmR but sit
        // in three different groups, because a stripper clip, a box mag and a belt are not interchangeable. Conversely
        // group 1 holds both 5.56 rifles and the .300 BLK honeybadger -- which is correct, not a bug: .300 BLK is 5.56
        // necked up and feeds from the same STANAG magazines.
        // Sourced per-gun from the wiki's Trivia section (master's instruction), recorded here rather than inferred so
        // the next person does not have to re-derive which real weapon each is based on.
        public string CaliberName;   // .dat Caliber_Name: "5.56x45mm NATO", "12 Gauge", ...
        public string RealWeapon;    // .dat Real_Weapon:  "Colt AR-15", "Mossberg 500", ...
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
        // A suppressor built into the gun rather than screwed onto it. The Barrel attachment slot answers "is a
        // can fitted", which is the right question for every gun that takes one and the wrong question for the two
        // that ARE one -- the Honey Badger and the VSS Vintorez are both integrally suppressed in real life and
        // have no removable barrel. Bare valueless key, same shape as the melee `Light` flag.
        public bool IntegrallySuppressed;
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

        // TRACER WIDTH per cartridge (master: "scale tracers based on ammo caliber. ie .22 smol 50bmg big").
        // 1.0 = 5.56x45mm, the most common round in the game, so everything reads relative to the eaglefire.
        // Each entry is anchored to the real BULLET diameter (in the comment), not invented -- but diameter alone is
        // NOT the whole answer and two cases say so out loud:
        //   - .22 LR is a 5.70mm bullet, the SAME bore as 5.56x45. On diameter it would tie with the eaglefire, which
        //     is the exact opposite of "smol". It carries roughly a tenth the energy, so it is scaled on that instead.
        //   - the shotguns are scaled per PELLET (00 buck ~8.4mm), not per bore. A 12ga bore is 18.5mm, and 8 pellets
        //     each drawn three times eaglefire-width would be a walking wall of orange rather than a shot.
        // Unknown / non-ballistic (arrows, bolts, rockets, the railgun) fall through to 1.0; the rocket draws its own
        // visible projectile anyway and the bows have no tracer worth speaking of.
        // A DICTIONARY rather than a switch so the test can ask what is mapped. With a switch, renaming a cartridge in
        // a .dat drops it into the default arm and every tracer silently reverts to 1.0 -- identical to a gun that is
        // genuinely 5.56, and invisible in play.
        public static float TracerScale(string caliberName)
            => caliberName != null && TracerScales.TryGetValue(caliberName, out var s) ? s : 1.00f;

        public static readonly System.Collections.Generic.Dictionary<string, float> TracerScales = new()
        {
            { ".22 LR", 0.60f },                  // 5.70mm bore, ~1/10 the energy -- scaled on energy, see above
            { "5.56x45mm NATO", 1.00f },          // 5.70mm  (baseline)
            { ".300 AAC Blackout", 1.15f },       // 7.82mm
            { "7.62x39mm", 1.25f },               // 7.92mm
            { "7.62x51mm NATO", 1.35f },          // 7.82mm, notably more case than the x39
            { "7.62x54mmR", 1.35f },              // 7.92mm
            { ".338 Lapua Magnum", 1.60f },       // 8.60mm
            { "9x18mm Makarov", 1.00f },          // 9.27mm but low pressure
            { "9x19mm Parabellum", 1.05f },       // 9.01mm
            { "9x39mm", 1.20f },                  // 9.25mm, heavy subsonic
            { ".357 Magnum", 1.25f },             // 9.07mm
            { ".40 S&W", 1.20f },                 // 10.17mm
            { ".44 Magnum", 1.45f },              // 10.90mm
            { ".45 ACP", 1.30f },                 // 11.48mm
            { ".45 Long Colt", 1.35f },           // 11.48mm on a bigger case than the ACP (Peacemaker)
            { ".50 AE", 1.70f },                  // 12.70mm
            { ".50 BMG", 2.30f },                 // 12.95mm on an enormous case -- the top of the range on purpose
            { "12 Gauge", 0.45f },                // EXTRA small per master: this is ONE PELLET, and a shot spawns 8 of
            { "20 Gauge", 0.40f },                // them. Scaling off the 18.5mm bore would draw eight walls of orange.
            // Added with the Washington port. Placed by BORE against the neighbours above, not invented:
            { "7.62x25mm Tokarev", 1.10f },       // 7.85mm, a fast PISTOL round -- bigger bore than the
                                                  // 9x19 but nowhere near the x39's case (PPSh-41)
            { "5.7x28mm", 0.70f },                // 5.70mm, same bore as the 5.56 baseline on a much smaller
                                                  // case -- so between the .22 LR and the baseline (P90)
            { ".408 CheyTac", 2.00f },            // 10.36mm; heavier than .338 Lapua, lighter than .50 BMG,
                                                  // and sits between them here for the same reason (M200)
            { "Railgun Slug", 1.80f },            // not a real cartridge; sized to read as heavy
            // Not cartridges either -- master gave the nailgun and paintball gun their own calibers rather than
            // leaving them blank. They still need a width: every bullet draws a tracer sized by this table, and an
            // unmapped name silently falls back to 1.00 and draws an EAGLEFIRE-sized streak, which is exactly the
            // failure the coverage check in gun.caliber_field exists to catch. It caught these.
            { "Nail", 0.30f },                    // ~3.1mm shank -- thinner than a buckshot pellet, the smallest
                                                  // thing here, because it is the smallest thing fired
            { "Paintball", 1.20f },               // 17.3mm is wider than a .50 BMG, but bore loses to ENERGY here
                                                  // the same way it does for the .22 and for buckshot: a paintball
                                                  // must not draw the fattest streak in the game
        };

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
                BlastRadius = d.ParseFloat("Explosion_Radius", 0f),               // 0 = no impact blast, which is all but the launcher
                BlastZombieDamage = d.ParseFloat("Explosion_Zombie_Damage", 0f),
                BlastPlayerDamage = d.ParseFloat("Explosion_Player_Damage", 0f),
                BlastVehicleDamage = d.ParseFloat("Explosion_Vehicle_Damage", 0f),
                Range = d.ParseFloat("Range", 100f),
                Firerate = d.ParseInt32("Firerate", 8),
                AmmoMax = d.ParseInt32("Ammo_Max", 30),
                MagazineId = d.ParseInt32("Magazine", 0),   // default magazine item id (eaglefire/maplestrike = 6, the Military STANAG)
                Damage = d.ParseFloat("Damage", d.ParseFloat("Player_Damage", 0f)),   // canonical; legacy dats fall back
                PatternClimb = d.ParseFloat("Recoil_Pattern_Climb", 0f),
                PatternNodes = d.ParseInt32("Recoil_Pattern_Nodes", 30),
                PatternDrift = d.ParseFloat("Recoil_Pattern_Drift", 0f),
                PatternDriftExp = d.ParseFloat("Recoil_Pattern_Drift_Exp", 1.9f),
                PatternDriftStart = d.ParseInt32("Recoil_Pattern_Drift_Start", 0),
                PatternWiggle = d.ParseFloat("Recoil_Pattern_Wiggle", 0f),
                PatternSway = d.ParseFloat("Recoil_Pattern_Sway", 0f),
                PatternSwayStart = d.ParseInt32("Recoil_Pattern_Sway_Start", 0),
                PatternPlateau = d.ParseFloat("Recoil_Pattern_Plateau", 0f),
                ScopeSwayScale = d.ParseFloat("Scope_Sway_Scale", 1f),
                ScatterH = d.ParseFloat("Recoil_Scatter_H", 1f),
                ScatterV = d.ParseFloat("Recoil_Scatter_V", 1f),
                ScatterTightEarly = d.ContainsKey("Recoil_Scatter_Tight_Early"),
                FalloffStart = d.ParseFloat("Damage_Falloff_Start", 0f),
                FalloffEnd = d.ParseFloat("Damage_Falloff_End", 0f),
                FalloffMin = d.ParseFloat("Damage_Falloff_Min", 1f),
                Caliber = d.ParseInt32("Caliber", 0),       // which magazines fit: a mag's caliber must match (eaglefire caliber 1)
                CaliberName = d.GetString("Caliber_Name"),  // real cartridge; null for anything not yet tagged
                RealWeapon = d.GetString("Real_Weapon"),
                Pellets = d.ParseInt32("Pellets", 1),   // staged into the shotgun's .dat from its Shells_2 mag (8)
                SpreadAngleDegrees = d.ParseFloat("Spread_Angle_Degrees"),
                SpreadAim = d.ParseFloat("Spread_Aim", 1f),
                RecoilMinX = d.ParseFloat("Recoil_Min_X"),
                RecoilMaxX = d.ParseFloat("Recoil_Max_X"),
                RecoilMinY = d.ParseFloat("Recoil_Min_Y"),
                RecoilMaxY = d.ParseFloat("Recoil_Max_Y"),
                IntegrallySuppressed = d.ContainsKey("Integrally_Suppressed"),
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
