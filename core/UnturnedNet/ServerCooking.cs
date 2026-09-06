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
            public float Fuel;       // seconds of burn left in the piece currently alight (fuelled appliances only)
            public float FuelTotal;  // ...and how long that piece started with, so a progress bar has a denominator
            public byte SentFuel;    // the last fraction pushed to the opener, so Step only speaks when the bar moves
            public bool SentOn;      // ...and likewise the last on-bit, which the server can flip by itself

            /// <summary>The bar's height, 0..255. Unfuelled appliances have no bar: an oven is on or it is not.</summary>
            public byte FuelFrac => FuelTotal > 0f && Fuel > 0f
                ? (byte)Mathf.Clamp(Mathf.RoundToInt(Fuel / FuelTotal * 255f), 0, 255) : (byte)0;
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

        /// <summary>Can this appliance draw the watts it needs right now -- a wired power input, or the mains
        /// being up? Injected because PowerNet is a GAME-layer thing and this is core: the same reason Detonate
        /// is. Null (a bare test harness, or a host with no power sim) reads as powered, so cooking does not
        /// silently stop in a fixture that has no grid to be on.</summary>
        public System.Func<uint, float, bool> HasPower;

        /// <summary>(netId, on, fuelFrac) whenever the OPENER's view would visibly change. Injected for the same
        /// reason Detonate is -- this class owns when the bar moves, the host owns who to tell.
        ///
        /// It fires only on a real change, and "real" is measured in the units the bar is drawn in: a 255-step
        /// fraction, so a 40-minute maple log speaks about 255 times over its whole burn rather than 50 times a
        /// second. Nothing is sent for an appliance nobody has open -- checked by the caller, which is the side
        /// that knows about openers.</summary>
        public System.Action<uint, bool, byte> StateChanged;

        /// <summary>Push the current state to the opener whether or not it changed -- for the moment a player
        /// opens the panel, where "no change since the last tick" is the wrong test because this viewer has never
        /// been told anything.</summary>
        public void ForceStateSync(uint netId)
        {
            if (!_cookers.TryGetValue(netId, out var c)) return;
            c.SentOn = c.On; c.SentFuel = c.FuelFrac;
            StateChanged?.Invoke(netId, c.On, c.SentFuel);
        }

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
            NoteState(c);   // the switch is a visible change; do not make the opener wait for the next Step
            return true;
        }

        /// <summary>One step. Returns the NetIds of any microwaves that just detonated, for the caller to
        /// turn into a real explosion -- this class owns the RULE, not the fireball.</summary>
        public List<uint> Step(float dt)
        {
            List<uint> blew = null;
            foreach (var c in _cookers.Values)
            {
                // Every path out of this body is a `continue`, so the fuel-bar push cannot live at the bottom of
                // it -- the two most interesting transitions (ran out of fuel and switched itself off; lost mains
                // power) are exactly the ones that take an early exit. It runs in its own pass below instead.
                if (!c.On) continue;
                if (!_inventories.TryGetCrate(c.NetId, out var crate) || crate.Storage == null) continue;

                // FUELLED APPLIANCES burn what they burn and nothing else -- a barbecue takes charcoal, a
                // campfire takes wood. Out of fuel it is a cold grill, so it switches ITSELF off rather than
                // sitting on pretending to cook. (An oven, toaster and microwave run on the mains: FuelFor
                // returns null and this whole block is skipped.)
                // A MAINS APPLIANCE needs its watts. Unlike running out of fuel this does NOT flip the switch
                // off: a blackout should leave the oven still switched on and cooking again when the power comes
                // back, the way a real one does -- and the way TVDevice/RadioDevice already treat the mains.
                if (Cooking.NeedsPower(c.Kind) && HasPower != null && !HasPower(c.NetId, Cooking.PowerWatts(c.Kind)))
                    continue;

                if (Cooking.NeedsFuel(c.Kind))
                {
                    if (c.Fuel <= 0f && !TryLightNext(c, crate)) { c.On = false; continue; }
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

                    // THAW FIRST, THEN COOK (strawberry 2026-09-06: "cooking from 0-100% starts after the food
                    // is thawed", and frozen "drops faster when being cooked"). The appliance spends this whole
                    // tick on the ice and none of it on the cooking, so a frozen steak visibly does something in
                    // the oven while its cooked % is still pinned at zero -- rather than looking broken.
                    if (Freezing.IsFrozen(item))
                    {
                        Freezing.AdvanceCarried(item, -Freezing.CookingThawPerSecond, dt);
                        continue;
                    }
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
            // THE BAR MOVES HERE, for every cooker and every exit path above. NoteState is a no-op unless the
            // opener's view actually changed, so a shelf of idle appliances costs one comparison each.
            foreach (var c in _cookers.Values) NoteState(c);
            return blew ?? EmptyList;
        }

        /// <summary>Tell the host if -- and only if -- this appliance now looks different to whoever has it open.
        /// The comparison is against what was last SENT, not against the previous tick, so a fraction that drifts
        /// slowly still produces exactly one message per visible step of the bar.</summary>
        void NoteState(Cooker c)
        {
            byte frac = c.FuelFrac;
            if (c.On == c.SentOn && frac == c.SentFuel) return;
            c.SentOn = c.On; c.SentFuel = frac;
            StateChanged?.Invoke(c.NetId, c.On, frac);
        }

        static readonly List<uint> EmptyList = new List<uint>();

        /// <summary>Light the next piece of fuel out of the appliance's own grid, and remember how long that
        /// PARTICULAR piece burns -- a maple plate is not a pine stick. Returns false when there is nothing
        /// burnable left, which is what turns a fuelless grill or fire off.</summary>
        static bool TryLightNext(Cooker c, InventoryReplication.CrateEntry crate)
        {
            for (byte i = 0; i < crate.Storage.getItemCount(); i++)
            {
                var jar = crate.Storage.getItem(i);
                if (jar?.item == null) continue;
                var asset = Assets.find(jar.item.id);
                if (!Cooking.IsFuelFor(c.Kind, asset)) continue;
                float secs = Cooking.BurnSecondsFor(asset);
                if (secs <= 0f) continue;
                if (jar.item.amount > 1) jar.item.amount--; else crate.Storage.removeItem(i);
                c.Fuel = secs;
                c.FuelTotal = secs;   // the denominator the progress bar divides by
                return true;
            }
            return false;
        }
    }
}
