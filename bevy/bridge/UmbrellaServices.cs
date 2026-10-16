using SDG.Unturned;
using UnturnedGodot;
using UnturnedGodot.Net;
namespace UnturnedBevy;

/// <summary>The server half of the umbrella (Umbrellas.cs, UseableCloud). PlayerController sets
/// `_move.GravityMultiplier` to the held umbrella's Gravity once the viewmodel's equip clip has
/// finished, and CheckFallDamage skips the landing at or below 0.67 (PlayerLife:2399). Fall damage is
/// server-derived here, so the host reads the same rule off the replicated hand: an umbrella held
/// for at least its EmptyHands equip clip (Melee_Equip) is up. Covers the listen host's own player
/// and every joined client alike.</summary>
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
    /// <summary>Once per tick: when each player's replicated hand last changed.</summary>
    public void Step()
    {
        long now = _server.Session.CurrentTick;
        foreach (var player in _server.CombatState.All)
        {
            ushort held = player.Alive ? player.HeldId : (ushort)0;
            if (!_held.TryGetValue(player.OwnerPlayerId, out var h) || h.Held != held) _held[player.OwnerPlayerId] = (held, now);
        }
    }
    public float GravityOf(ushort owner)
        => _held.TryGetValue(owner, out var h) && Umbrellas.For(h.Held) is float gravity
           && (_server.Session.CurrentTick - h.Since) * SimClock.FixedDelta >= _equipSeconds ? gravity : 1f;
}
