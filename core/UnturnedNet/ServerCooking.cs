using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    /// <summary>The appliances that cook, on the SERVER (strawberry 2026-09-05: "bbqs, ovens, toasters,
    /// microwaves have a new on/off button for cooking").
    ///
    /// SERVER-OWNED, and not because of a general preference for server authority. Two concrete reasons: an
    /// oven left on has to keep cooking while nobody is standing in front of it -- including while no client
    /// is anywhere near it -- and `cooked` is a value that multiplies what a meal is worth, so a client
    /// allowed to assert it is a client allowed to print food. The owner echo carries the result back
    /// (InventoryReplication.WriteJar), the client never writes it.
    ///
    /// The cookers are CRATES: these four props were made into containers earlier, so each already has a
    /// server-side grid (InventoryReplication.CrateEntry) and a NetId. This adds a kind + an on/off switch
    /// beside that grid rather than a second store of items.</summary>
    public sealed class ServerCooking
    {
        public sealed class Cooker
        {
            public uint NetId;
            public ECookerKind Kind;
            public bool On;
            public float Fuel;       // BARBECUE only: seconds of burn left in the charcoal currently loaded
        }

        // Burn time per unit of fuel now lives on Cooking.SecondsPerFuel, per appliance -- a log outlasts a
        // briquette. Kept as a name here only because tests and callers referred to it.
        public const float SecondsPerCharcoal = 45f;

        readonly Dictionary<uint, Cooker> _cookers = new Dictionary<uint, Cooker>();
        readonly InventoryReplication _inventories;
        readonly System.Func<long> _tick;

        /// <summary>How a detonating microwave actually explodes. Injected rather than called directly so this
        /// class keeps owning the RULE and not the fireball -- and so an L0 test can assert that a can of beans
        /// sets it off without needing a combat system at all.</summary>
        public System.Action<Vector3, float, float> Detonate;

        /// <summary>A microwave is a small box, not a grenade: it takes the kitchen, not the street.</summary>
        public const float MicrowaveBlastRadius = 3.5f;
        public const float MicrowaveBlastDamage = 60f;

        public ServerCooking(InventoryReplication inventories, System.Func<long> tick)
        { _inventories = inventories; _tick = tick; }

        public int Count => _cookers.Count;
        public bool TryGet(uint netId, out Cooker c) => _cookers.TryGetValue(netId, out c);

        /// <summary>The game layer names which crates are cookers -- it is the side that knows a prop's mesh
        /// is an Oven_0 rather than a Fridge_0.</summary>
        public void Register(uint netId, ECookerKind kind)
        {
            if (_cookers.TryGetValue(netId, out var c)) { c.Kind = kind; return; }
            _cookers[netId] = new Cooker { NetId = netId, Kind = kind };
        }

        public void Forget(uint netId) => _cookers.Remove(netId);

        /// <summary>The on/off button. Returns false if this NetId is not a cooker, so a forged toggle for an
        /// arbitrary crate does nothing rather than creating one.</summary>
        public bool SetOn(uint netId, bool on)
        {
            if (!_cookers.TryGetValue(netId, out var c)) return false;
            c.On = on;
            return true;
        }

        /// <summary>One step. Returns the NetIds of any microwaves that just detonated, for the caller to
        /// turn into a real explosion -- this class owns the RULE, not the fireball.</summary>
        public List<uint> Step(float dt)
        {
            List<uint> blew = null;
            foreach (var c in _cookers.Values)
            {
                if (!c.On) continue;
                if (!_inventories.TryGetCrate(c.NetId, out var crate) || crate.Storage == null) continue;

                // FUELLED APPLIANCES burn what they burn and nothing else -- a barbecue takes charcoal, a
                // campfire takes wood. Out of fuel it is a cold grill, so it switches ITSELF off rather than
                // sitting on pretending to cook. (An oven, toaster and microwave run on the mains: FuelFor
                // returns null and this whole block is skipped.)
                var fuel = Cooking.FuelFor(c.Kind);
                if (fuel != null)
                {
                    if (c.Fuel <= 0f && !TryConsumeFuel(crate, fuel)) { c.On = false; continue; }
                    if (c.Fuel <= 0f) c.Fuel = Cooking.SecondsPerFuel(c.Kind);
                    c.Fuel -= dt;
                }

                for (byte i = 0; i < crate.Storage.getItemCount(); i++)
                {
                    var jar = crate.Storage.getItem(i);
                    var item = jar?.item;
                    if (item == null) continue;
                    var asset = Assets.find(item.id);
                    if (asset == null) continue;

                    // METAL IN A MICROWAVE. Checked before the accept test on purpose: a can of beans is FOOD
                    // and would otherwise simply cook, and "will EXPLODE if you add metal" is the whole point.
                    if (Cooking.Detonates(c.Kind, asset))
                    {
                        c.On = false;
                        Detonate?.Invoke(crate.Pos, MicrowaveBlastRadius, MicrowaveBlastDamage);
                        (blew ??= new List<uint>()).Add(c.NetId);
                        break;
                    }

                    if (!Cooking.Accepts(c.Kind, asset)) continue;   // a toaster ignores everything that is not bread
                    if (item.cooked >= Cooking.MaxCooked) continue;

                    item.cooked = Cooking.Advance(item.cooked, c.Kind, dt);
                    // The style is stamped when it becomes COOKED, not while raw: a steak pulled out at 40 %
                    // is raw and unlabelled, and moving it from the microwave to the barbecue before it is
                    // done should let the barbecue claim it.
                    if (Cooking.IsCooked(item.cooked) || Cooking.IsBurnt(item.cooked))
                        item.cookStyle = (byte)Cooking.StyleOf(c.Kind);
                }
                // PUBLISH. Two things about this are load-bearing and neither is obvious:
                //
                // 1. Mutating item.cooked does NOT dirty anything by itself. Items.onStateUpdated fires on
                //    add / remove / resize -- a field changing on an item already in the grid raises nothing,
                //    so without this the food would cook perfectly on the server and the client would never
                //    be told until something else happened to touch the grid.
                // 2. It is the VIEWER that replicates, not the crate: the inventory system is owner-only, and
                //    a crate's contents reach a client by being copied into that player's STORAGE page.
                //
                // And the reason writing to crate.Storage is enough while somebody has the door open:
                // CopyPage re-seats the SAME Item references rather than cloning them, so the open page and
                // the crate alias one object per jar. If anyone ever "hardens" CopyPage into a deep copy,
                // cooking silently stops working for exactly as long as a player is watching it -- which is
                // the hardest possible time to notice.
                if (crate.OpenBy != 0) _inventories.ServerMarkDirty(crate.OpenBy);
            }
            return blew ?? EmptyList;
        }

        static readonly List<uint> EmptyList = new List<uint>();

        /// <summary>Spend one unit of fuel out of the appliance's own grid. Returns false when there is none,
        /// which is what turns a fuelless grill or fire off. Any of the accepted ids will do -- a campfire does
        /// not care whether the log is birch or pine.</summary>
        static bool TryConsumeFuel(InventoryReplication.CrateEntry crate, IReadOnlySet<ushort> fuel)
        {
            for (byte i = 0; i < crate.Storage.getItemCount(); i++)
            {
                var jar = crate.Storage.getItem(i);
                if (jar?.item == null || !fuel.Contains(jar.item.id)) continue;
                if (jar.item.amount > 1) { jar.item.amount--; return true; }
                crate.Storage.removeItem(i);
                return true;
            }
            return false;
        }
    }
}
