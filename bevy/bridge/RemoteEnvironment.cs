using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>v80 (row 15A): a joined client's read-only view of the host's weather and calendar
/// (System21 EnvironmentReplication). It never steps a WeatherSim of its own -- the Godot source let every
/// process roll its own weather and day, which is exactly the divergence this replaces. The outlook and
/// day number are pure functions of the replicated seed/base day and the replicated world clock, and the
/// State() row is the host's own projection (WorldEnvironment.StateOf) over the replicated type/blend.</summary>
public sealed class RemoteEnvironment
{
    readonly EnvironmentReplication _replica;
    readonly WorldEnvironment.Tables _tables = WorldEnvironment.Tables.Build();
    (int seed, int day, WeatherOutlook.Day today, WeatherOutlook.Day tomorrow)? _outlook;

    public RemoteEnvironment(EnvironmentReplication replica) => _replica = replica;

    public bool Ready => _replica.HasEnvironment;
    public WeatherType? Active => Ready ? _tables.Type(_replica.WeatherType) : null;
    /// <summary>WeatherManager.RainIntensity is the blend alpha.</summary>
    public float BlendAlpha => Ready ? _replica.BlendAlpha : 0f;
    public bool IsRaining => BlendAlpha > .001f;

    public int? DayAt(WorldClockReplication clock, long tick)
        => Ready && clock.HasClock && clock.DayLengthSeconds > 0 ? WorldEnvironment.DayAt(_replica.BaseDay, clock, tick) : null;

    public object State(WorldClockReplication clock, long tick)
    {
        if (DayAt(clock, tick) is not int day) return null;
        if (_outlook is not { } o || o.seed != _replica.Seed || o.day != day)
            _outlook = o = (_replica.Seed, day, _tables.Outlook(_replica.Seed, day), _tables.Outlook(_replica.Seed, day + 1));
        return WorldEnvironment.StateOf(Active, _replica.WeatherType == _tables.SnowType, _replica.Stage,
            _replica.BlendAlpha, _replica.PuddleLevel, _replica.SnowCover, o.today, o.tomorrow, day);
    }
}
