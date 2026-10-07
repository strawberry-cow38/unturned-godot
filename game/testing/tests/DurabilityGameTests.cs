using System.Collections.Generic;
using Godot;
using SDG.NetTransport.Mem;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // v56 DURABILITY, the in-engine half (strawberry 2026-10-06: "weapons; melee and firearms have a chance to lower
    // durability when "shot" (incl melee)").
    //
    // The rules are L0 (DurabilityTests, DurabilityServerTests). What only a real client can show is the ROUTE: a shot
    // is decided client-side, so the wear has to be REPORTED -- and a report that never leaves the client, lands on the
    // wrong address, or is undone by the next owner echo looks, from the server's side, exactly like a gun nobody
    // fired. So this drives the real joined client (ClientWorldSession over MemNetwork, the same stack a player on
    // the dedicated server runs) and reads the SERVER's copy of the item, then the client's echo of it.
    public sealed class DurabilityWeaponWireTests : GameTest
    {
        // A wait that does NOT abort the test on timeout: the labelled T.Check after it then says WHICH condition never
        // held, with its values. A bare Until's timeout reads "condition never held" for every wait alike.
        Step Wait(System.Func<bool> c, double seconds) { int n = 0, max = (int)(seconds * 50); return Until(() => c() || ++n >= max, seconds + 1); }

        public override string Name => "durability.weapon_wear_over_the_wire";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var gunAsset = Assets.find(4);   // Eaglefire: Durability 0.15, no Wear line (= 1 point)
            T.Check($"the Eaglefire is a gun with a wear chance ({gunAsset?.itemName}, durability {gunAsset?.durability})",
                gunAsset?.gunName != null && gunAsset.durability > 0f && gunAsset.wear == 1);
            if (gunAsset?.gunName == null || gunAsset.durability <= 0f) yield break;

            var net = new MemNetwork(20261006);
            var pump = new DelegateSimStep((t, dt) => net.Tick(), "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "wearer" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);

            yield return Wait(() => sess.Shell != null, 5);
            T.Check("shell spawned", sess.Shell != null);
            if (sess.Shell == null) yield break;
            ushort pid = sess.Client.PlayerId;
            if (!ded.Server.Inventories.TryGet(pid, out var sInv)) { T.Check("server owns an inventory", false); yield break; }
            var tx = ded.Server.Transactions;
            var p = sess.Shell;

            var served = new Item(4) { quality = 100 };
            sInv.Inventory.items[0].addItem(0, 0, 0, served);
            yield return Wait(() => p.Inventory.items[0].getItemCount() == 1, 5);
            Item Client0() => p.Inventory.items[0].getItem(0)?.item;
            T.Check($"the client adopted the rifle at 100% ({Client0()?.quality})", Client0()?.id == 4 && Client0()?.quality == 100);

            p.EquipHotbar(1);
            yield return Ticks(100);   // the pull-out gates Fire() for ~1.6 s
            T.Check($"gun in hand ({p.HeldGunName})", p.HasGunOut);
            if (!p.HasGunOut) yield break;

            // The server has to KNOW what is in the hands -- for the broken refusal and the worn-gun damage below, and
            // for every other player's puppet drawing it. A joined client streams PlayerState, not MoveInput (client-
            // auth, 2026-07-18), and v22 put the held id on MoveInput only, so this used to read 0 for every joiner.
            PlayerCombatReplication.CombatEntity sce = null;
            yield return Wait(() => ded.Server.CombatState.TryGet(pid, out sce) && sce.HeldId == 4, 3);
            T.Check($"the SERVER knows the rifle is in my hands (appearance HeldId {sce?.HeldId})", sce?.HeldId == 4);
            // ...and the stance, off the same packet: puppets read it from the player entity, which for a joiner was
            // fed by the same MoveInput that stopped arriving -- so everyone else saw them standing whatever they did.
            p.ScriptedStance = EPlayerStance.CROUCH;
            PlayerReplication.PlayerEntity spe = null;
            yield return Wait(() => ded.Server.Players.TryGetByOwner(pid, out spe) && spe.Stance == 2, 3);
            T.Check($"...and that I am crouching (server stance {spe?.Stance}, 2 = crouch)", spe?.Stance == 2);
            p.ScriptedStance = null;
            yield return Wait(() => ded.Server.Players.TryGetByOwner(pid, out spe) && spe.Stance == 0, 3);

            // ---- 1. EVERY SHOT ROLLS, SERVER-SIDE. Rand 0 lands under every chance, so each shot costs exactly Wear.
            tx.Rand = () => 0f;
            p.Ammo = 30;
            long accepted0 = ded.Server.Combat.Diag.ShotsAccepted, applied0 = tx.Diag.WeaponUsesApplied;
            int shots = 0;
            for (int i = 0; i < 6; i++) { if (p.Fire()) shots++; yield return Ticks(12); }
            yield return Wait(() => served.quality == 100 - shots, 5);
            T.Check($"fired a burst ({shots} shots, server accepted {ded.Server.Combat.Diag.ShotsAccepted - accepted0})",
                shots >= 4 && ded.Server.Combat.Diag.ShotsAccepted - accepted0 == shots);
            T.Check($"the SERVER's rifle lost one point per shot (100 -> {served.quality}, {shots} shots)", served.quality == 100 - shots);
            T.Check($"...by REPORT over the wire ({tx.Diag.WeaponUsesApplied - applied0} reports for {shots} shots)",
                tx.Diag.WeaponUsesApplied - applied0 >= 1);
            yield return Wait(() => Client0()?.quality == served.quality, 5);
            T.Check($"...and the owner echo brought the condition back to the client ({Client0()?.quality})", Client0()?.quality == served.quality);
            T.Check($"nothing left unsent ({p.DebugPendingWeaponUses})", p.DebugPendingWeaponUses == 0);

            // ---- 2. CONTROL: the roll is a CHANCE. Above it, the reports still arrive and nothing is lost -- without
            // this, "loses a point per shot" is indistinguishable from a flat per-shot cost that ignores Durability.
            tx.Rand = () => 0.99f;
            byte before = served.quality;
            long appliedC = tx.Diag.WeaponUsesApplied;
            int shotsC = 0;
            for (int i = 0; i < 6; i++) { if (p.Fire()) shotsC++; yield return Ticks(12); }
            yield return Wait(() => tx.Diag.WeaponUsesApplied > appliedC, 5);
            yield return Ticks(20);
            T.Check($"a roll above the chance costs nothing ({shotsC} shots reported in {tx.Diag.WeaponUsesApplied - appliedC} reports, {before} -> {served.quality})",
                shotsC >= 4 && tx.Diag.WeaponUsesApplied > appliedC && served.quality == before);

            // ---- 3. BROKEN = WILL NOT FIRE. Set on the server (the authority), arrives by echo like any wear would.
            served.quality = 0;
            ded.Server.Inventories.ServerMarkDirty(pid);
            yield return Wait(() => Client0()?.quality == 0, 5);
            yield return Ticks(30);   // past the fire-rate gate and the hint cooldown, so a refusal is the durability's
            T.Check("the client sees it broken", p.HeldBroken);
            ded.Server.Players.TryGetHeldInput(pid, out var heldIn);
            byte? serverHeld = ded.Server.Combat.HeldCondition?.Invoke(pid);
            T.Check($"...and so does the SERVER, which refuses the shot on its own (held id {heldIn.HeldItemId}, condition {serverHeld?.ToString() ?? "unknown"})",
                serverHeld == 0);
            int ammo0 = p.Ammo, hints0 = p.DebugBrokenHints;
            long acceptedB = ded.Server.Combat.Diag.ShotsAccepted;
            bool firedBroken = p.Fire();
            yield return Ticks(20);
            T.Check($"a broken gun does not fire (fired {firedBroken}, ammo {ammo0} -> {p.Ammo})", !firedBroken && p.Ammo == ammo0);
            T.Check($"...the server saw no shot ({ded.Server.Combat.Diag.ShotsAccepted - acceptedB})", ded.Server.Combat.Diag.ShotsAccepted == acceptedB);
            T.Check($"...and the player is TOLD why ({p.DebugBrokenHints - hints0} hint)", p.DebugBrokenHints == hints0 + 1);

            // ...and the refusal is about the condition, not about the gun: give it some back and it fires again.
            served.quality = 30;
            ded.Server.Inventories.ServerMarkDirty(pid);
            yield return Wait(() => Client0()?.quality == 30, 5);
            yield return Ticks(12);
            T.Check($"at 30% it fires again ({p.HeldCondition}%)", !p.HeldBroken && p.Fire());

            // ---- 4. MELEE. A melee weapon has no backing item in the hand -- it is found at its grid address -- so this
            // is a separate route that can break on its own.
            ItemAsset melee = null;
            for (ushort id = 1; id < 2000 && melee == null; id++)
            {
                var a = Assets.find(id);
                if (a?.meleeName != null && a.slot == ESlotType.SECONDARY && a.durability > 0f
                    && a.meleeName != "chainsaw" && a.meleeName != "blowtorch") melee = a;
            }
            T.Check($"a (secondary-slot) melee weapon with a wear chance exists ({melee?.itemName}, durability {melee?.durability}, wear {melee?.wear})", melee != null);
            if (melee == null) { world.Sim.Sim.Remove(pump); yield break; }

            Item Client1() => p.Inventory.items[1].getItem(0)?.item;
            var blade = new Item(melee.id) { quality = 100 };
            sInv.Inventory.items[1].addItem(0, 0, 0, blade);
            ded.Server.Inventories.ServerMarkDirty(pid);
            yield return Wait(() => Client1()?.id == melee.id, 5);
            p.EquipHotbar(2);
            yield return Ticks(100);
            T.Check($"melee in hand ({p.HeldMeleeNameForDisplay})", p.HeldDurableItem()?.id == melee.id);

            tx.Rand = () => 0f;
            int swings = 0;
            var origMelee = p.NetMelee;
            p.NetMelee = (strong, yaw) => { swings++; origMelee?.Invoke(strong, yaw); };
            for (int i = 0; i < 3; i++) { p.MeleeAttack(false); yield return Ticks(100); }
            p.NetMelee = origMelee;
            int expect = 100 - swings * melee.wear;
            yield return Wait(() => blade.quality == expect, 5);
            T.Check($"each swing cost the SERVER's weapon its Wear ({swings} swings x {melee.wear}: 100 -> {blade.quality}, want {expect})",
                swings >= 2 && blade.quality == expect);

            blade.quality = 0;
            ded.Server.Inventories.ServerMarkDirty(pid);
            yield return Wait(() => Client1()?.quality == 0, 5);
            yield return Ticks(100);
            int swingsB = 0, hintsM = p.DebugBrokenHints;
            p.NetMelee = (strong, yaw) => { swingsB++; origMelee?.Invoke(strong, yaw); };
            p.MeleeAttack(false);
            p.NetMelee = origMelee;
            T.Check($"a broken melee weapon does not swing ({swingsB} swings, {p.DebugBrokenHints - hintsM} hint)",
                swingsB == 0 && p.DebugBrokenHints == hintsM + 1);

            tx.Rand = null;
            world.Sim.Sim.Remove(pump);
        }
    }

    // v56 DURABILITY: the worn look (strawberry: "a tattered holey shader for clothing thats worn that shows in 1p and
    // 3p for other players too"). Condition lives on the SERVER's copy of each garment; the look has to reach three
    // places off it -- your own 3P body, your own 1P arms, and every other player's puppet -- and the puppet is the
    // hard one, because only an id per slot used to cross the wire. WornCond carries a 4-bit step per slot.
    //
    // ⚠ The failure this is built for: a wear change does NOT change the outfit, so a puppet that only re-dresses on
    // an outfit change would draw the first condition it saw forever. Step 2 moves ONLY the condition.
    public sealed class DurabilityWornLookTests : GameTest
    {
        // A wait that does NOT abort the test on timeout: the labelled T.Check after it then says WHICH condition never
        // held, with its values. A bare Until's timeout reads "condition never held" for every wait alike.
        Step Wait(System.Func<bool> c, double seconds) { int n = 0, max = (int)(seconds * 50); return Until(() => c() || ++n >= max, seconds + 1); }

        public override string Name => "durability.worn_look_replicates";
        public override double TimeoutSimSeconds => 60;

        public override IEnumerable<Step> Run()
        {
            var task = WorldBuilder.BuildFullWorld(World, WorldMode.Dedicated,
                mapRoot: "res://__no_such_map__", mapPlace: "placements.txt",
                syncLoad: true, activeHoliday: "NONE");
            var world = task.Result;
            T.Check("world ready", world.Ready);
            ItemCatalog.RegisterAll();

            var net = new MemNetwork(20261007);
            var other = new NetWorldClient(new MemClientTransport(net), "other", contentHash: NetContent.Hash);
            var pump = new DelegateSimStep((t, dt) =>
            {
                net.Tick(); other.Tick();
                if (other.State == NetSessionState.Connected) other.SendMoveInput(0f, 0f, 0f);
            }, "l1.netpump");
            world.Sim.Sim.Add(pump);
            var sess = new ClientWorldSession { Driver = world.Sim, TransportOverride = new MemClientTransport(net), PlayerName = "viewer" };
            World.AddChild(sess);
            var ded = new DedicatedServer { Driver = world.Sim, TransportOverride = new MemServerTransport(net), RemoteAvatars = true };
            World.AddChild(ded);
            other.Connect();

            yield return Wait(() => sess.Shell != null && other.State == NetSessionState.Connected, 8);
            T.Check("both players joined", sess.Shell != null && other.State == NetSessionState.Connected);
            if (sess.Shell == null) yield break;
            ushort me = sess.Client.PlayerId, them = other.PlayerId;
            ded.Server.Inventories.TryGet(me, out var myInv);
            ded.Server.Inventories.TryGet(them, out var theirInv);
            T.Check("the server owns both inventories", myInv != null && theirInv != null);
            if (myInv == null || theirInv == null) yield break;

            // ---- 1. MY OWN BODY. A 20% shirt on the server -> the echo -> the clothing controller -> the shader.
            var myShirt = new Item(3) { quality = 20 };   // Orange Hoodie
            myInv.Inventory.wearShirt(myShirt);
            ded.Server.Inventories.ServerMarkDirty(me);
            var p = sess.Shell;
            yield return Wait(() => p.Inventory.wornShirt?.quality == 20, 5);
            yield return Ticks(10);   // ReconcileTick runs on the body tick
            float body = p.BodyRigForTest?.DebugShirtWear ?? -1f;
            T.Check($"my 3P body draws the shirt at 80% wear ({body:0.00})", Mathf.Abs(body - 0.8f) < 0.03f);
            var arms = p.ArmsRigForTest;
            if (arms != null)
                T.Check($"...and so do my 1P arms ({arms.DebugShirtWear:0.00})", Mathf.Abs(arms.DebugShirtWear - 0.8f) < 0.03f);
            else
                T.Check("my 1P arms rig exists to check", false);

            // ---- 2. THEIR PUPPET, first at full condition (the CONTROL: a puppet that drew everything worn would pass
            // step 3 on its own).
            var theirShirt = new Item(3) { quality = 100 };
            var theirHat = new Item(192) { quality = 100 };   // a hat with a mesh, for the gear shader
            theirInv.Inventory.wearShirt(theirShirt);
            theirInv.Inventory.wearHat(theirHat);
            ded.Server.Inventories.ServerMarkDirty(them);
            var remotes = sess.Remotes;
            RiggedCharacter Puppet() => remotes != null && remotes.TryGetPuppet(them, out var n) ? n as RiggedCharacter : null;
            yield return Wait(() => Puppet() != null && remotes.TryGetWorn(them, out var w) && w.wornShirt?.id == 3, 8);
            yield return Ticks(10);
            T.Check($"their puppet is dressed in the hoodie, clean ({Puppet()?.DebugShirtWear:0.00})",
                Puppet() != null && Puppet().DebugShirtWear >= 0f && Puppet().DebugShirtWear < 0.01f);
            T.Check($"...and the hat is on its plain material ({Puppet()?.DebugGearWear(EItemType.HAT):0.00}: -1 = not swapped)",
                Puppet()?.DebugGearWear(EItemType.HAT) == -1f);

            // ---- 3. ONLY THE CONDITION MOVES. Same ids, so the appearance signature is unchanged.
            theirShirt.quality = 20;
            theirHat.quality = 0;
            ded.Server.Inventories.ServerMarkDirty(them);
            uint want = Durability.PackWorn(theirInv.Inventory);
            yield return Wait(() => sess.Client.CombatState.TryGet(them, out var c) && c.WornCond == want, 5);
            T.Check("the packed condition replicated", sess.Client.CombatState.TryGet(them, out var ce) && ce.WornCond == want);
            yield return Ticks(10);
            float pShirt = Puppet()?.DebugShirtWear ?? -1f, pHat = Puppet()?.DebugGearWear(EItemType.HAT) ?? -1f;
            T.Check($"their puppet's shirt now draws 80% wear WITHOUT a re-dress ({pShirt:0.00})", Mathf.Abs(pShirt - 0.8f) < 0.03f);
            T.Check($"...and their broken hat is fully tattered ({pHat:0.00})", Mathf.Abs(pHat - 1f) < 0.01f);
            remotes.TryGetWorn(them, out var pw);
            T.Check($"...and the puppet's broken hat protects nothing on THIS side too (quality {pw?.wornHat?.quality})", pw?.wornHat?.quality == 0);

            world.Sim.Sim.Remove(pump);
            other.Disconnect();
        }
    }
}
