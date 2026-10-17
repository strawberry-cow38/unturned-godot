using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
namespace UnturnedBevy;

/// <summary>The server half of the umbrella (Umbrellas.cs, UseableCloud). PlayerController sets
/// `_move.GravityMultiplier` to the held umbrella's Gravity once the viewmodel's equip clip has
/// finished, and CheckFallDamage skips the landing at or below 0.67 (PlayerLife:2399). Fall damage is
/// server-derived here, so the host reads the same rule off the replicated hand: an umbrella held for
/// at least its EmptyHands equip clip (Melee_Equip) is up. The hand is the client's MoveInput, so the
/// umbrella must also be in the authoritative bag, and core still refuses a landing faster than the
/// umbrella's own descent cap (ServerPlayerAuthority.GlideLandingSlack). Covers the listen host's own
/// player and every joined client alike.</summary>
public sealed class UmbrellaServices
{
    readonly NetWorldServer _server;
    readonly double _equipSeconds;
    readonly Dictionary<ushort, (ushort Held, long Since)> _held = new();
    public UmbrellaServices(NetWorldServer server, Catalog catalog)
    {
        _server = server;
        _equipSeconds = catalog.ClipSeconds("Melee_Equip");
        server.PlayerHost.FallGravityMultiplier = GravityOf;
    }
    public int Tracked => _held.Count;
    ushort HeldNow(ushort owner)
        => _server.CombatState.TryGet(owner, out var player) && player.Alive ? player.HeldId : (ushort)0;
    /// <summary>Once per tick: when each player's replicated hand last changed. Players who left are
    /// pruned; the dead read as empty-handed.</summary>
    public void Step()
    {
        long now = _server.Session.CurrentTick;
        foreach (var owner in _held.Keys.ToArray())
            if (!_server.CombatState.TryGet(owner, out _)) _held.Remove(owner);
        foreach (var player in _server.CombatState.All) Observe(player.OwnerPlayerId, now);
    }
    void Observe(ushort owner, long now)
    {
        ushort held = HeldNow(owner);
        if (!_held.TryGetValue(owner, out var h) || h.Held != held) _held[owner] = (held, now);
    }
    public float GravityOf(ushort owner)
    {
        // Read the hand as it is now, not as of the last Step (which runs after the landing is judged).
        Observe(owner, _server.Session.CurrentTick);
        var h = _held[owner];
        return Umbrellas.For(h.Held) is float gravity
            && _server.Inventories.TryGet(owner, out var bag) && bag.Inventory.getItemCount(h.Held) > 0
            && (_server.Session.CurrentTick - h.Since) * SimClock.FixedDelta >= _equipSeconds ? gravity : 1f;
    }
}
