using Godot;
using System.Collections.Generic;
using System.Linq;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // THROWABLES (strawberry 2026-09-05: "grenades, smoke grenades and flares. grenades are equippable, thrown
    // from the hand. 3s fuse before detonation. smoke grenades are the same, they emit smoke in a radius,
    // coloured depending on the colour of the one you threw. flares glow and flicker brightly, giving off sparks
    // for a period before dissipating. smoke also dissipates after a while").
    //
    // Three of these checks have real teeth and the rest are pins:
    //
    //  * THE GROUND. The old Grenade bounced off a hard-coded plane at y=0.11 with a comment saying it "assumes
    //    ground near y=0". Every previous check of a grenade was run on flat test ground at y=0, where the bug
    //    and the fix produce identical results -- the default configuration hid it. So this drops one onto a
    //    platform at y=6 and asserts it rests THERE. Under the old code it sinks to 0.11 and the check fails.
    //
    //  * THE COLOUR. The claim being made is not "smoke is coloured", it is "the colour comes off the item's own
    //    asset". A hand-typed palette would pass a check that only asked whether red smoke is reddish, so this
    //    asserts the two ENDS of the range -- White Smoke and Black Smoke, the same mesh with different paint --
    //    which no plausible hardcoded tint table gets right by accident.
    //
    //  * THE STACK. Throwing spends one and re-arms from the bag; the last one reverts the hand. That tail is
    //    shared with the consumable path and it is where "the item left your hand" goes wrong.
    public sealed class ThrowableTests : GameTest
    {
        public override string Name => "throwable.equip_throw_effect";
        public override double TimeoutSimSeconds => 40;

        static byte PageOf(PlayerInventory inv, ushort id, out byte gx, out byte gy)
        {
            gx = gy = 0;
            for (byte b = 0; b < (byte)(PlayerInventory.PAGES - 2); b++)
            {
                var pg = inv.items[b];
                if (pg == null) continue;
                for (byte i = 0; i < pg.getItemCount(); i++)
                {
                    var j = pg.getItem(i);
                    if (j?.item?.id == id) { gx = j.x; gy = j.y; return b; }
                }
            }
            return 255;
        }

        static IEnumerable<T> Descend<T>(Node n) where T : Node
        {
            foreach (var c in n.GetChildren())
            {
                if (c is T t) yield return t;
                foreach (var d in Descend<T>(c)) yield return d;
            }
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            yield return Ticks(1);

            // ---- 1. the table, against the retail .dats ----
            var frag = Throwables.Find(254);
            T.Check($"the frag is in the table ({frag?.Name})", frag != null);
            T.Check($"...with Grenade.dat's numbers (dmg {frag?.PlayerDamage}, range {frag?.Radius}, vehicle {frag?.VehicleDamage})",
                    frag != null && frag.PlayerDamage == 175f && frag.Radius == 8f && frag.VehicleDamage == 100f);
            var mk = Throwables.Find(1242);
            // The makeshift's .dat has Player_Damage 150 and Range 6 and NO Vehicle_Damage key at all. The zero is
            // the point: it is the one thing about this item a reasonable guess gets wrong.
            T.Check($"the makeshift grenade is weaker and does NOTHING to a vehicle (dmg {mk?.PlayerDamage}, range {mk?.Radius}, vehicle {mk?.VehicleDamage})",
                    mk != null && mk.PlayerDamage == 150f && mk.Radius == 6f && mk.VehicleDamage == 0f);
            var smoke = Throwables.Find(266);
            T.Check($"red smoke carries no damage at all ({smoke?.PlayerDamage}/{smoke?.ZombieDamage}/{smoke?.VehicleDamage})",
                    smoke != null && smoke.Kind == EThrowableKind.Smoke && smoke.PlayerDamage == 0f && smoke.ZombieDamage == 0f && smoke.VehicleDamage == 0f);
            T.Check("a flare is a flare", Throwables.Find(259)?.Kind == EThrowableKind.Flare);
            // Unimplemented mechanics must stay OUT. A flashbang that equips and throws as a frag is a lie the
            // player can only find out by dying to it.
            T.Check("the flashbang is NOT throwable yet (its mechanic does not exist)", !Throwables.Is(1346));
            T.Check("...nor the sticky grenade", !Throwables.Is(1100));
            T.Check($"the table holds the 16 items that ARE implemented ({Throwables.Count})", Throwables.Count == 16);
            T.Check($"the fuse is 3 s, not retail's 2.5 (strawberry) -- {Throwables.FuseSeconds}s / {Throwables.FuseTicks} ticks",
                    Mathf.IsEqualApprox(Throwables.FuseSeconds, 3f) && Throwables.FuseTicks == 150);

            // ---- 2. the colour comes off the ASSET, not a table I typed ----
            var white = WorldItem.PaletteColor(267);
            var black = WorldItem.PaletteColor(261);
            var red = WorldItem.PaletteColor(266);
            T.Check($"White Smoke reads white off its own palette ({white})",
                    white.HasValue && white.Value.R > 0.75f && white.Value.G > 0.75f && white.Value.B > 0.75f);
            T.Check($"Black Smoke reads dark off the SAME mesh's palette ({black})",
                    black.HasValue && black.Value.R < 0.30f && black.Value.G < 0.30f && black.Value.B < 0.30f);
            T.Check($"Red Smoke reads red ({red})",
                    red.HasValue && red.Value.R > 0.45f && red.Value.G < 0.25f && red.Value.B < 0.25f);

            // ---- 3. equip -> throw -> spend -> re-arm -> revert ----
            var p = new PlayerController { CaptureMouse = false, Inventory = new PlayerInventory() };
            World.AddChild(p);
            p.GlobalPosition = new Vector3(0f, 1f, 0f);
            yield return Ticks(2);
            p.Inventory.wearBackpack(new Item(253));
            p.Inventory.tryAddItem(new Item(266));
            p.Inventory.tryAddItem(new Item(266));
            yield return Ticks(1);

            byte pg266 = PageOf(p.Inventory, 266, out byte sx, out byte sy);
            T.Check($"two red smokes are in the bag (page {pg266})", pg266 != 255 && p.Inventory.getItemCount(266) == 2);

            p.NoteHeldFrom(pg266, sx, sy);
            p.EquipItemAsset(Assets.find(266), p.Inventory.items[pg266].getItem(p.Inventory.items[pg266].getIndex(sx, sy))?.item);
            yield return Ticks(2);
            T.Check($"equipping one puts it in the hand (held={p.HoldingThrowable}, def={p.HeldThrowableDef?.Name})",
                    p.HoldingThrowable && p.HeldThrowableDef?.Id == 266);
            T.Check($"...carrying the canister's own colour ({p.HeldThrowableTint})",
                    p.HeldThrowableTint.R > 0.45f && p.HeldThrowableTint.G < 0.25f);

            p.ThrowHeld();
            // RETAIL TIMING (UseableThrowable): the click starts the swing; the canister leaves the hand -- and is spent --
            // at 60 % of the "Use" clip (0.98 s of TU_0, or 0.6 s of the 1 s stand-in when no arms rig is loaded), and the
            // hand is busy until the clip ends (1.63 s). So: 55 ticks to see the spend, 90 between throws.
            yield return Ticks(55);
            T.Check($"throwing spends one ({p.Inventory.getItemCount(266)} left)", p.Inventory.getItemCount(266) == 1);
            T.Check("...and re-arms with the next of the same kind", p.HoldingThrowable && p.HeldThrowableDef?.Id == 266);

            // Busy for the whole swing, so the second throw has to wait it out; and the hand falls back only once the
            // follow-through has finished (retail dequips after useTime), hence the long wait before the fallback check.
            yield return Ticks(90);
            p.ThrowHeld();
            yield return Ticks(90);
            T.Check($"the last one empties the stack ({p.Inventory.getItemCount(266)} left)", p.Inventory.getItemCount(266) == 0);
            T.Check("...and the hand falls back (no throwable held)", !p.HoldingThrowable);

            // ---- 3b. THE REVERT TARGET. Throwing your last grenade should hand back what you were HOLDING,
            // not fists and not whatever an unrelated earlier item stashed. _revertEquip is shared across
            // consumables, deployables and tools, so a path that reads it without ever writing it silently
            // inherits somebody else's answer -- which is exactly what this did until it was caught here.
            p.EquipHeldGun("eaglefire", null);
            yield return Ticks(2);
            T.Check("a rifle is in hand before the grenade", p.HasGunOut);
            p.Inventory.tryAddItem(new Item(254));
            yield return Ticks(1);
            byte pgG = PageOf(p.Inventory, 254, out byte ggx, out byte ggy);
            p.NoteHeldFrom(pgG, ggx, ggy);
            p.EquipItemAsset(Assets.find(254), p.Inventory.items[pgG].getItem(p.Inventory.items[pgG].getIndex(ggx, ggy))?.item);
            yield return Ticks(2);
            T.Check("...the frag replaces it in the hand", p.HoldingThrowable && !p.HasGunOut);
            yield return Ticks(90);   // clear the swing
            p.ThrowHeld();
            yield return Ticks(90);   // release at 60 %, then the follow-through, then the hand falls back
            T.Check($"throwing the LAST one hands the rifle back, not fists (gun out={p.HasGunOut})",
                    !p.HoldingThrowable && p.HasGunOut);

            // ---- 4. the smoke actually becomes a cloud, in the right colour, and then goes ----
            // Two were thrown above; the fuse is 3 s, so wait it out and look for the cloud.
            yield return Ticks(170);
            var clouds = Descend<SmokeCloud>(World).ToList();
            T.Check($"a smoke canister leaves a cloud behind ({clouds.Count} in the world)", clouds.Count > 0);
            if (clouds.Count > 0)
            {
                var c = clouds[0];
                T.Check($"...in the thrown canister's colour ({c.Tint})", c.Tint.R > 0.45f && c.Tint.G < 0.25f);
                T.Check($"...at the item's own radius ({c.Radius} vs {smoke.Radius})", Mathf.IsEqualApprox(c.Radius, smoke.Radius));
                T.Check($"...and it is still producing smoke well before its {c.Duration}s is up", c.Emitting);
            }

            // ---- 5. THE GROUND IS NOT AT y=0. The teeth check. ----
            // A platform up at y=6. The old code bounced off a hard plane at y=0.11 and would sink straight
            // through this; every earlier grenade check ran on flat ground at y=0 where both behave the same.
            var plat = new StaticBody3D { CollisionLayer = 1u << 0 };
            plat.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(12f, 1f, 12f) } });
            World.AddChild(plat);
            plat.GlobalPosition = new Vector3(30f, 6f, 0f);
            yield return Ticks(2);

            var dropped = new Grenade { Thrower = p, Vel = Vector3.Zero, Def = frag, ItemId = 254, Fuse = 30f };
            World.AddChild(dropped);
            dropped.GlobalPosition = new Vector3(30f, 10f, 0f);   // straight down onto the platform top at y=6.5
            yield return Until(() => !GodotObject.IsInstanceValid(dropped) || dropped.AtRest, 8);
            T.Check($"a grenade dropped onto raised ground comes to REST (y={(GodotObject.IsInstanceValid(dropped) ? dropped.GlobalPosition.Y : float.NaN):0.00})",
                    GodotObject.IsInstanceValid(dropped) && dropped.AtRest);
            T.Check($"...on the platform at ~6.5, NOT sunk to the old hardcoded y=0.11 (y={(GodotObject.IsInstanceValid(dropped) ? dropped.GlobalPosition.Y : float.NaN):0.00})",
                    GodotObject.IsInstanceValid(dropped) && dropped.GlobalPosition.Y > 6.2f && dropped.GlobalPosition.Y < 7.2f);

            // ---- 6. a flare is ALREADY BURNING when it leaves the hand ----
            p.Inventory.tryAddItem(new Item(259));
            yield return Ticks(1);
            byte pgF = PageOf(p.Inventory, 259, out byte fx, out byte fy);
            p.NoteHeldFrom(pgF, fx, fy);
            p.EquipItemAsset(Assets.find(259), p.Inventory.items[pgF].getItem(p.Inventory.items[pgF].getIndex(fx, fy))?.item);
            yield return Ticks(2);
            T.Check($"a flare equips ({p.HeldThrowableDef?.Name})", p.HeldThrowableDef?.Kind == EThrowableKind.Flare);
            yield return Ticks(90);   // clear the previous swing
            p.ThrowHeld();
            // 55 ticks: PAST the release (60 % of the 1.63 s TU_0 = 0.98 s = 49 ticks) and FAR short of the fuse,
            // which does not even start until the release and then runs 150 more. So finding a burning flare here
            // still proves it lit on the THROW rather than on the fuse -- a fuse-lit flare shows nothing until
            // tick ~199. This wait was Ticks(3), correct when the throw left the hand on the click; the retail
            // 60 % release moved the event and the assertion had to move with it. Four sibling waits were updated
            // for the new timing and this one was missed, which is what made main red rather than any feature bug.
            yield return Ticks(55);
            var burns = Descend<FlareBurn>(World).ToList();
            T.Check($"the flare is lit as it leaves the hand, not 3 s later on a fuse ({burns.Count} burning)", burns.Count > 0);

            if (GodotObject.IsInstanceValid(dropped)) dropped.QueueFree();
            yield return Ticks(1);
        }
    }
}
