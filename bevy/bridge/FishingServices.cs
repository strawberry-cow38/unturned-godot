using SDG.Unturned;
using UnityEngine;
using UnturnedGodot;
using UnturnedGodot.Net;
namespace UnturnedBevy;

/// <summary>Host choke point for v81 commands 103/104: the server-side UseableFisher the source never had.
/// PlayerController ran FishingSim on the client and GrantFish added to its own copy of the bag; here the
/// authority owns the bite timer, the catch window (with retail's SERVER_LENIENCY_WINDOW), the catch-challenge
/// minigame and the reward, configured exactly as FishingContent.ConfigureForPei configures the source's sim
/// (Rod_Fishing values, the PEI ocean table and the caster's FISHING skill). The caster's client keeps the gauge,
/// the bobber's flight and the presentation, and learns where the server's line is from Event78.</summary>
public sealed class FishingServices
{
    /// <summary>The farthest a bobber can land from the caster on the source's flight (Lerp(12, 26) m/s
    /// forward plus 3 up, 20 m/s² down, from the eye): about 41 m over flat water from a standing eye.
    /// The bound is generous on purpose, as the arrest and paint reaches are: it refuses a forged point,
    /// not a cast off a cliff.</summary>
    public const float CastReach = 64f;
    /// <summary>Terrain.MinFishDepth: retail UseableFisher minimumDepth.</summary>
    public const float MinFishDepth = 4f;
    readonly NetWorldServer _server;
    readonly WorldGeometry _geometry;
    readonly Dictionary<ushort, Line> _lines = new();
    readonly System.Random _seeds = new();
    sealed class Line
    {
        public FishingSim Sim;
        public ushort Rod;
        public bool Bite;
        public EFishingState State;
    }
    public FishingServices(NetWorldServer server, WorldGeometry geometry)
    {
        _server = server; _geometry = geometry;
        server.Commands.Register<FishingCastCommand>(ReplicationIds.CommandFishingCast, FishingCastCommand.TryRead, OnCast);
        server.Commands.Register<FishingInputCommand>(ReplicationIds.CommandFishingInput, FishingInputCommand.TryRead, OnInput);
    }
    public int LineCount => _lines.Count;
    /// <summary>The server's sim behind a caster's line (diagnostics and tests).</summary>
    public FishingSim SimFor(ushort owner) => _lines.TryGetValue(owner, out var line) ? line.Sim : null;

    /// <summary>The rod really in the caster's living hand and bag, and the caster on foot.</summary>
    bool Holding(ushort sender, out ushort rod)
    {
        rod = 0;
        if (!_server.CombatState.TryGet(sender, out var life) || !life.Alive) return false;
        if (_server.VehicleHost.IsSeated(sender)) return false;
        rod = life.HeldId;
        return Assets.find(rod) is { type: EItemType.FISHER }
            && _server.Inventories.TryGet(sender, out var bag) && bag.Inventory.getItemCount(rod) > 0;
    }
    /// <summary>BobberOverFishableWater: on the sea plane, and the terrain under it at least MinFishDepth below
    /// the sea (`SampleHeight <= SeaLevelY - MinFishDepth`). As in the source only the terrain counts (a pier or a
    /// boat over the water does not), and a point with no registered terrain trusts the plane.</summary>
    public bool Fishable(Vector3 bobber)
    {
        if (!_server.HasWater || Mathf.Abs(bobber.y - _server.SeaLevelY) > 1f) return false;
        return !_geometry.TryGroundHeight(bobber.x, bobber.z, out float floor) || floor <= _server.SeaLevelY - MinFishDepth;
    }
    void OnCast(ushort sender, FishingCastCommand cmd)
    {
        if (!Holding(sender, out ushort rod) || !_server.Players.TryGetByOwner(sender, out var player)) { Send(sender, FishingStateEvent.Idle, 0); return; }
        var flat = cmd.Bobber - player.Pos; flat.y = 0;
        if (flat.magnitude > CastReach || cmd.Bobber.y > player.Pos.y + 2f || !Fishable(cmd.Bobber)) { Send(sender, FishingStateEvent.Idle, 0); return; }
        var sim = new FishingSim(_seeds.Next());
        byte level = _server.Skills.TryGet(sender, out var skills) ? skills.Skills.Level(EPlayerSupport.FISHING) : (byte)0;
        FishingContent.ConfigureForPei(sim, level);
        sim.BeginLine(cmd.Strength);
        _lines[sender] = new Line { Sim = sim, Rod = rod, State = sim.State };
        Send(sender, FishingStateEvent.Line, 0);
    }
    void OnInput(ushort sender, FishingInputCommand cmd)
    {
        if (!_lines.TryGetValue(sender, out var line)) return;
        switch (cmd.Kind)
        {
            case FishingInputCommand.Press:
                var caught = line.Sim.Press(FishingSim.SERVER_LENIENCY_WINDOW);
                if (caught.Success) Grant(sender, caught);
                Publish(sender, line, caught.Success ? caught.ItemId : (ushort)0);
                break;
            case FishingInputCommand.Release:
                line.Sim.Release();
                break;
            default:
                _lines.Remove(sender);
                Send(sender, FishingStateEvent.Idle, 0);
                break;
        }
    }
    /// <summary>UseableFisher.tock + simulate at the 50 Hz server tick. A line ends when the rod leaves the
    /// living hand (ClearFisher), and every edge the caster cannot time on its own is told.</summary>
    public void Step()
    {
        foreach (var (owner, line) in _lines.ToArray())
        {
            if (!Holding(owner, out ushort rod) || rod != line.Rod)
            {
                _lines.Remove(owner); Send(owner, FishingStateEvent.Idle, 0); continue;
            }
            line.Sim.Tock();
            line.Sim.Simulate((float)SimClock.FixedDelta);
            ushort item = 0;
            if (line.Sim.TryTakePendingCatch(out var won)) { Grant(owner, won); item = won.ItemId; }
            Publish(owner, line, item);
        }
    }
    void Publish(ushort owner, Line line, ushort caught)
    {
        var state = line.Sim.State; bool bite = line.Sim.HasBite;
        if (state == line.State && bite == line.Bite && caught == 0) return;
        line.State = state; line.Bite = bite;
        if (state == EFishingState.Idle) _lines.Remove(owner);
        Send(owner, state switch
        {
            EFishingState.CatchChallenge => FishingStateEvent.Challenge,
            EFishingState.LineDeployed => bite ? FishingStateEvent.Bite : FishingStateEvent.Line,
            _ => FishingStateEvent.Idle,
        }, caught);
    }
    /// <summary>UseableFisher.GrantRewards as GrantFish ported it: tryAddItem (a full bag keeps nothing) and the
    /// fishing XP, both on the authoritative copies.</summary>
    void Grant(ushort owner, FishingCatch caught)
    {
        if (Assets.find(caught.ItemId) != null && _server.Inventories.TryGet(owner, out var bag)
            && bag.Inventory.tryAddItem(new Item(caught.ItemId)))
            _server.Inventories.ServerMarkDirty(owner);
        if (caught.Experience > 0) _server.Skills.ServerAward(owner, (uint)caught.Experience, _server.Session.CurrentTick);
    }
    void Send(ushort owner, byte state, ushort item)
        => _server.SendEventTo(owner, NetMessagePak.Pack(ReplicationIds.EventFishingState, new FishingStateEvent { State = state, CaughtItemId = item }.Write));
}
