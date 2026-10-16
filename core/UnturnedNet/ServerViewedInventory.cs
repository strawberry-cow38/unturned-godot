using SDG.Unturned;

namespace UnturnedGodot.Net
{
    public sealed partial class ServerTransactions
    {
        void OnEmptyViewedFuel(ushort sender, EmptyViewedFuelCommand cmd)
        {
            if (!_combat.IsAlive(sender) || !TryGetSenderPos(sender, out var position)) return;
            var item = _inventories.ServerResolveScoped(sender, cmd.Address, position);
            if (item?.GetAsset()?.IsFuelContainer != true) return;
            item.fuelLevel = 0;
            // Bare Item writes do not raise a grid event. Every live viewer
            // must receive the same result without reopening the container.
            _inventories.ServerRepaintCrateViewers(cmd.Address.CrateId);
        }

        void OnViewedMagazineTransfer(ushort sender, ViewedMagazineTransferCommand cmd) =>
            ProcessMagazineTransfer(sender, new MagazineRequest(cmd.RequestId, cmd.Magazine, cmd.Round, cmd.Unloading, cmd.UnloadRoundId));
    }
}
