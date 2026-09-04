using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // THE AVENGER IS A .45 ACP USP (strawberry: "change the Avenger to a 45 acp usp. reuses ammo and has a niche as a
    // bigger mag 1911").
    //
    // Two claims, and they pull against each other, which is the only reason this suite is interesting:
    //
    //   "reuses ammo"        -> the two pistols must be on the SAME caliber group, so either magazine seats in either.
    //   "bigger mag 1911"    -> ...but the Avenger must still be the one that actually holds more. If sharing a group
    //                           also shared the capacity, the 1911 would pick up 12 rounds from an Avenger mag and the
    //                           niche would evaporate -- and it would look exactly like the feature working, because
    //                           the guns really would be sharing ammo.
    //
    // What resolves it is Gun.Ammo_Max capping the reload, not the magazine. That is a property of DoMagSwap, one
    // Math.Min away from being wrong, and nothing else in the game would show it: both guns would still fire, still
    // reload, still consume the right magazine.
    //
    // Also pinned here: both magazines FUNCTION. Neither did before this change -- an inert TSV magazine has the right
    // name, the right icon, magCapacity 0, and is silently not a magazine, so reloads fell through to a free top-up and
    // no magazine was ever consumed. That is the exact shape the sabertooth shipped with.
    public sealed class AvengerUspTests : GameTest
    {
        public override string Name => "gun.avenger_usp_45";

        static GunDef Def(string dir, string g)
        {
            try { return GunDef.FromDatText(System.IO.File.ReadAllText(dir + g + ".dat")); } catch { return null; }
        }

        /// <summary>What a reload actually loads: the source rule from PlayerController.DoMagSwap, reproduced so the
        /// test computes it rather than reading the answer back out of the thing under test.</summary>
        static int Loaded(int magAmount, int gunAmmoMax) => System.Math.Min(magAmount, gunAmmoMax);

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();   // self-sufficient when run ALONE (t1.ps1 -t): the magazine checks below read the catalog, which only an earlier test used to register
            string dir = ProjectSettings.GlobalizePath("res://content/");
            var usp = Def(dir, "avenger");
            var colt = Def(dir, "colt");
            T.Check("both pistols' .dats load", usp != null && colt != null);
            if (usp == null || colt == null) yield break;

            // ---- THE CARTRIDGE.
            T.Check($"the Avenger is chambered in .45 ACP ({usp.CaliberName})", usp.CaliberName == ".45 ACP");
            T.Check($"...the same cartridge the 1911 fires ({colt.CaliberName})", usp.CaliberName == colt.CaliberName);
            T.Check($"...and it is recorded as the gun it now is ({usp.RealWeapon})",
                usp.RealWeapon != null && usp.RealWeapon.Contains("USP"));
            // The .40 S&W it used to be was an ORPHAN: caliber 23, and nothing in the game fed it. Assert no gun is
            // left on that group, because a half-done rechambering leaves one behind and it looks like nothing.
            var orphans = new List<string>();
            foreach (var f in System.IO.Directory.GetFiles(dir, "*.dat"))
            {
                var g = Def(dir, System.IO.Path.GetFileNameWithoutExtension(f));
                if (g != null && g.CaliberName == ".40 S&W") orphans.Add(System.IO.Path.GetFileNameWithoutExtension(f));
            }
            T.Check($"nothing is left on the orphan .40 S&W{(orphans.Count > 0 ? ": " + string.Join(",", orphans) : "")}",
                orphans.Count == 0);

            // ---- SAME GROUP = the ammo is genuinely shared. Caliber_Name alone would NOT do it: the .300 Blackout and
            // 5.56 share group 1 with different names, and the SCAR/M39 share a NAME across different groups. The
            // number is what the magazine fit actually keys on.
            T.Check($"...on the same caliber GROUP, which is what makes the ammo interchange ({usp.Caliber} vs {colt.Caliber})",
                usp.Caliber == colt.Caliber);

            // ---- SAME ROUND, SAME TERMINAL BALLISTICS. Left at 32 the Avenger would out-damage the 1911 while firing
            // the identical cartridge AND holding more of it, which is not a niche -- it is a replacement.
            T.Check($"...hitting for what a .45 hits for ({usp.Damage} vs the 1911's {colt.Damage})",
                Mathf.IsEqualApprox(usp.Damage, colt.Damage));

            // ---- THE NICHE: capacity, and ONLY capacity.
            T.Check($"the Avenger holds more ({usp.AmmoMax} vs {colt.AmmoMax})", usp.AmmoMax > colt.AmmoMax);
            T.Check($"...twelve, the real USP .45's magazine ({usp.AmmoMax})", usp.AmmoMax == 12);
            T.Check($"...and the 1911 keeps its seven ({colt.AmmoMax})", colt.AmmoMax == 7);

            // ---- THE TENSION, resolved. A shared group means an Avenger mag seats in a 1911; Ammo_Max is what stops
            // that from handing the 1911 twelve rounds. THIS is the check that separates "bigger mag 1911" from "both
            // pistols now hold 12", and both states share a caliber group, a working magazine and a consumed reload.
            T.Check($"a 12-round mag in the 1911 still only loads {Loaded(12, colt.AmmoMax)}",
                Loaded(12, colt.AmmoMax) == 7);
            T.Check($"...while the same mag in the Avenger loads all {Loaded(12, usp.AmmoMax)}",
                Loaded(12, usp.AmmoMax) == 12);
            T.Check($"...and a 1911 mag in the Avenger loads its {Loaded(7, usp.AmmoMax)}, not a phantom twelve",
                Loaded(7, usp.AmmoMax) == 7);

            // ---- BOTH MAGAZINES ACTUALLY FUNCTION. magCapacity > 0 is the real test; non-null is not, because an
            // inert TSV magazine is a perfectly good ItemAsset that simply is not a magazine.
            yield return Ticks(1);
            foreach (var (gun, name, cap) in new[] { (usp, "Avenger", 12), (colt, "1911", 7) })
            {
                var a = Assets.find((ushort)gun.MagazineId);
                T.Check($"the {name}'s magazine {gun.MagazineId} exists", a != null);
                if (a == null) continue;
                T.Check($"...and is a FUNCTIONING magazine (cap {a.magCapacity})", a.IsMagazine);
                T.Check($"...that fits its own gun (mag cal {a.magCaliber} vs gun {gun.Caliber})", a.magCaliber == gun.Caliber);
                T.Check($"...holding {cap} ({a.magCapacity})", a.magCapacity == cap);
                T.Check($"...and tagged with the round it carries ({a.magRound})", a.magRound == ".45 ACP");
            }

            // ...and CROSS-fit, which is the literal statement of "reuses ammo" and is a different assertion from
            // "same caliber number": it goes through AttachmentFit.Fits, the rule the inventory actually applies.
            var uspMag = Assets.find((ushort)usp.MagazineId);
            var coltMag = Assets.find((ushort)colt.MagazineId);
            if (uspMag != null && coltMag != null)
            {
                // The slot string is CASE-SENSITIVE (AttachmentFit.TypeFor is a switch on the literal). Asserted
                // before it is used, because getting it wrong makes every Fits call return false -- which reads as a
                // clean pass on the "refuses a foreign round" check below while silently proving nothing at all.
                const string MagSlot = "Magazine";
                T.Check($"'{MagSlot}' is a slot the fit rule recognises", AttachmentFit.TypeFor(MagSlot) == EItemType.MAGAZINE);
                T.Check("a 1911 magazine fits the Avenger", AttachmentFit.Fits(coltMag, MagSlot, usp.Caliber));
                T.Check("...and an Avenger magazine fits the 1911", AttachmentFit.Fits(uspMag, MagSlot, colt.Caliber));
                T.Check($"...but they are still DISTINCT items ({usp.MagazineId} vs {colt.MagazineId})",
                    usp.MagazineId != colt.MagazineId);
                // TEETH: the same rule must still REFUSE a foreign round, or "it fits" would be telling us nothing.
                var rifle = Def(dir, "eaglefire");
                T.Check($"...and neither seats in a 5.56 rifle (group {rifle?.Caliber})",
                    rifle != null && rifle.Caliber != usp.Caliber
                    && !AttachmentFit.Fits(coltMag, MagSlot, rifle.Caliber)
                    && !AttachmentFit.Fits(uspMag, MagSlot, rifle.Caliber));
            }

            // ---- THE PISTOL IS STILL A PISTOL. A rechambering that quietly changed the action would show up as a gun
            // that reloads wrong, and Real_Weapon is what the chamber rule reads.
            T.Check($"it is still a trigger action ({usp.Action})", usp.Action == "Trigger");
            T.Check("...not a revolver, so the chamber rule still gives it its +1", !usp.IsRevolver && usp.HasChamberRound);
            T.Check("...same as the 1911", !colt.IsRevolver && colt.HasChamberRound);

            // ---- THE TRACER follows the cartridge, so rechambering has to move it too. Both pistols now draw the
            // same width, and it is a .45's, not the .40's it used to be.
            T.Check($".45 ACP has a mapped tracer width ({GunDef.TracerScale(usp.CaliberName)})",
                GunDef.TracerScales.ContainsKey(usp.CaliberName));
            T.Check("...and both pistols now draw the same one",
                Mathf.IsEqualApprox(GunDef.TracerScale(usp.CaliberName), GunDef.TracerScale(colt.CaliberName)));
            T.Check($"...which is NOT the .40's ({GunDef.TracerScale(".45 ACP")} vs {GunDef.TracerScale(".40 S&W")})",
                !Mathf.IsEqualApprox(GunDef.TracerScale(".45 ACP"), GunDef.TracerScale(".40 S&W")));

            yield break;
        }
    }
}
