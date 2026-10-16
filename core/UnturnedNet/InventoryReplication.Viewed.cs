using SDG.Unturned;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public sealed partial class InventoryReplication
    {
        /// <summary>Resolve the exact current backing instance, not a stale
        /// projected page. Container access is rechecked for every operation.</summary>
        internal Item ServerResolveScoped(ushort owner, ScopedInventoryAddress address, Vector3? position)
        {
            if (!address.IsValid || !_byOwner.TryGetValue(owner, out var entry) || ItemIdentityProvider == null) return null;
            var visible = entry.Inventory.items[address.Page];
            Item At(Items grid)
            {
                byte index = grid.getIndex(address.X, address.Y);
                var jar = index == byte.MaxValue ? null : grid.getItem(index);
                return jar?.x == address.X && jar.y == address.Y && jar.item?.id == address.Id
                    && ItemIdentityProvider(jar.item) == address.Token ? jar.item : null;
            }
            var item = At(visible);
            if (item == null || address.CrateId == 0) return item;
            if (!position.HasValue || entry.OpenCrateId != address.CrateId
                || !_crates.TryGetValue(address.CrateId, out var crate) || !crate.Viewers.Contains(owner)
                || !crate.CanAccess(owner, position.Value)
                || !TryBacking(crate, address.Page, out var backing, out _, out _)) return null;
            return ReferenceEquals(item, At(backing)) ? item : null;
        }
    }
}
