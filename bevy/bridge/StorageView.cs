using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Accepted owner-only container labels. Contents remain the original
/// inventory replica; a request never fabricates pages or opens this view.</summary>
public sealed class StorageView
{
    public sealed record Compartment(byte Page, byte Width, byte Height, string Label);
    public sealed record View(ulong Revision, uint NetId, string Label, Compartment[] Compartments);
    public View Current { get; private set; } = new(0, 0, "", []);
    public void Open(StorageOpenedEvent e) => Current = new(Current.Revision + 1, e.NetId,
        string.IsNullOrEmpty(e.StorageLabel) ? "Storage" : e.StorageLabel,
        (e.Compartments ?? []).Select((c, i) => new Compartment((byte)(PlayerInventory.COMPARTMENT0 + i), c.W, c.H, c.Label ?? "")).ToArray());
    public void Close() => Current = new(Current.Revision + 1, 0, "", []);
}

public sealed partial class BridgeHost
{
    StorageView _storageView = new();
}
