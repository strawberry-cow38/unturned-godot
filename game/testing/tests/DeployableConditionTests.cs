using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // A PICKED-UP DEVICE REMEMBERS ITS CONDITION (strawberry 2026-09-10: "fix the item goldfish brain").
    //
    // Pickup already stamped a deployable's HP and fuel onto the item it gives back, on both paths. Placing threw
    // them away: ServerPlace stamped def.Health and def.Fuel unconditionally, so a generator picked up on
    // its last drop of fuel came back down FULL. Not merely forgetful -- pick up and re-place was free fuel.
    //
    // It hid because the singleplayer path has restored both from the backing item since it was written, and
    // reading that code says the feature works. Every session runs through the loopback, so the server path is the
    // one that actually executes.
    public sealed class DeployableConditionTests : GameTest
    {
        public override string Name => "mp.deployable_condition";

        /// <summary>Where a specific Item instance currently sits, by REFERENCE -- the same rule the place command
        /// uses, and the reason a same-id twin cannot be picked by mistake.</summary>
        static void FindAddress(PlayerInventory inv, Item want, ref byte page, ref byte x, ref byte y)
        {
            for (byte pg = 0; pg < PlayerInventory.PAGES; pg++)
            {
                var p = inv.items[pg];
                if (p == null) continue;
                for (byte i = 0; i < p.getItemCount(); i++)
                {
                    var jar = p.getItem(i);
                    if (jar != null && ReferenceEquals(jar.item, want)) { page = pg; x = jar.x; y = jar.y; return; }
                }
            }
        }

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);

            var loop = new MpLoopback { Player = player, Driver = driver };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            ushort pid = loop.Client.PlayerId;
            var def = DeployableDef.Generator;
            T.Check("the generator def is in the server schema", loop.Server.Deployables.Schema.TryGet(def.Id, out _));

            loop.Server.Inventories.TryGet(pid, out var sinv);

            // A WORN generator: 40% condition, a quarter of a tank.
            var worn = new Item(def.Id);
            worn.quality = 40;
            worn.fuelLevel = def.Fuel * 0.25f;
            T.Check("the worn generator is in the holster page", sinv.Inventory.items[0].tryAddItem(worn));
            yield return Ticks(2);
            byte wp = 255, wx = 0, wy = 0;
            FindAddress(sinv.Inventory, worn, ref wp, ref wx, ref wy);
            T.Check($"...and its address was found (page {wp})", wp != 255);

            int before = 0; foreach (var _ in loop.Server.Deployables.All) before++;
            loop.Client.SendPlaceDeployable(def.Id, new UnityEngine.Vector3(4f, 0f, 4f), 0f, wp, wx, wy);
            yield return Until(() => { int n = 0; foreach (var _ in loop.Server.Deployables.All) n++; return n > before; }, 15);

            DeployableReplication.DeployableEntity placed = null;
            foreach (var e in loop.Server.Deployables.All) if (e.DefId == def.Id) placed = e;
            T.Check("the worn generator was placed", placed != null);
            if (placed == null) yield break;
            GD.Print($"[cond] placed health={placed.Health:0.0}/{def.Health:0.0} fuel={placed.Fuel:0.0}/{def.Fuel:0.0}");

            // ⭐ THE EXPLOIT. Full fuel here is "pick it up and put it down to refill it".
            T.Check($"...carrying its quarter tank, not a full one ({placed.Fuel:0.0} of {def.Fuel:0.0})",
                placed.Fuel < def.Fuel * 0.6f);
            T.Check($"...and its 40% condition, not full HP ({placed.Health:0.0} of {def.Health:0.0})",
                placed.Health < def.Health * 0.7f);

            // ---- CONTROL: a FRESH one still starts full. Without this, "carries the item's state" would pass on
            // a change that simply placed everything empty, which is the same bug facing the other way.
            var fresh = Assets.makeLoot(def.Id);
            T.Check("a fresh generator goes into the bag", sinv.Inventory.tryAddItem(fresh));
            yield return Ticks(2);
            byte fp = 255, fx = 0, fy = 0;
            FindAddress(sinv.Inventory, fresh, ref fp, ref fx, ref fy);
            T.Check("...and its address was found", fp != 255);
            int mid = 0; foreach (var _ in loop.Server.Deployables.All) mid++;
            loop.Client.SendPlaceDeployable(def.Id, new UnityEngine.Vector3(-4f, 0f, -4f), 0f, fp, fx, fy);
            yield return Until(() => { int n = 0; foreach (var _ in loop.Server.Deployables.All) n++; return n > mid; }, 15);
            DeployableReplication.DeployableEntity second = null;
            foreach (var e in loop.Server.Deployables.All)
                if (e.DefId == def.Id && !ReferenceEquals(e, placed)) second = e;
            T.Check($"a FRESH generator still places full ({second?.Fuel:0.0} of {def.Fuel:0.0})",
                second != null && second.Fuel > def.Fuel * 0.9f);
            yield break;
        }
    }
}
