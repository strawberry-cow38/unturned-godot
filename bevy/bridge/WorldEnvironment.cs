using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Engine adapter for WeatherManager's core weather, outlook and snow-cover
/// rules. The original types, schedule, seasonal model and clock replication remain
/// authoritative; Bevy consumes the conditions and supplies visual/audio effects.</summary>
public sealed class WorldEnvironment
{
    readonly WeatherType[] _types;
    readonly WeatherSchedule[] _schedule;
    readonly int _seed, _snowSchedule, _snowType, _baseDay;
    int _day;
    public WeatherSim Sim { get; }
    public WeatherOutlook.Day Today { get; private set; }
    public WeatherOutlook.Day Tomorrow { get; private set; }
    public float SnowCover { get; private set; }
    public bool IsSnowing => Sim.ActiveTypeIndex == _snowType && Sim.BlendAlpha > .001f;
    public float Severity => Sim.Active is { } t ? Math.Clamp(t.FogDensity, .1f, 1f) : 1f;

    public WorldEnvironment(NetWorldServer server, int seed, WorldSave save = null)
    {
        _seed = seed; _baseDay = save?.Day ?? 0;
        server.Clock.ServerConfigure(save?.TimeOfDay01 ?? WorldDefaults.StartTime,
            save?.DayLengthSeconds > 0 ? save.DayLengthSeconds : WorldDefaults.DefaultDayLength, 0);
        var types = new List<WeatherType>(WeatherSim.PeiTypes());
        int variants = types.Count; types.AddRange(WeatherSim.VariantTypes());
        _snowType = types.Count; types.AddRange(WeatherSim.SnowTypes());
        var schedule = new List<WeatherSchedule>(WeatherSim.PeiSchedule());
        schedule.AddRange(WeatherSim.VariantSchedule(variants));
        _snowSchedule = schedule.Count; schedule.AddRange(WeatherSim.SnowSchedule(_snowType));
        _types = types.ToArray(); _schedule = schedule.ToArray();
        Sim = new(_types, _schedule, seed, server.Clock.DayLengthSeconds,
            WorldDefaults.FrequencyMultiplier, WorldDefaults.DurationMultiplier);
        Refresh(_baseDay);
        Sim.ScheduleChooser = () => Today.ScheduleIndex;
        var forced = Environment.GetEnvironmentVariable("UG_WEATHER");
        int index = forced switch {
            "rain" => 0, "heavy" or "lightning" => 1, "drizzle" => variants,
            "squall" or "windy" => variants + 1, "downpour" or "torrential" => variants + 2,
            "tempest" or "gale" => variants + 3, "snow" => _snowType, _ => -1 };
        if (index >= 0) Sim.SetPerpetual(index);
    }

    public int DayAt(WorldClockReplication clock, long tick) => _baseDay +
        (int)Math.Floor(clock.BaseTime01 + tick * SimClock.FixedDelta / clock.DayLengthSeconds);

    void Refresh(int day)
    {
        _day = day; Today = Outlook(day); Tomorrow = Outlook(day + 1);
    }
    static float MeanTemperature(int day) => WorldTemperature.BaseMeanC + WorldTemperature.SeasonAmplitude *
        WorldTemperature.SeasonPhase(WorldTemperature.DayOfYear(WorldTemperature.StartDayOfYear, day));
    WeatherOutlook.Day Outlook(int day) => WeatherOutlook.ForDay(_seed, day, _types, _schedule,
        WorldTemperature.SeasonPhase(WorldTemperature.DayOfYear(WorldTemperature.StartDayOfYear, day)), MeanTemperature(day), _snowSchedule);

    public void Step(NetWorldServer server)
    {
        int day = DayAt(server.Clock, server.Session.CurrentTick);
        if (_day != day) Refresh(day);
        float dt = (float)SimClock.FixedDelta, dayLength = server.Clock.DayLengthSeconds;
        Sim.Step(dt);
        if (IsSnowing)
            SnowCover = MathF.Min(1, SnowCover + dt / (WorldDefaults.SnowLayDays * dayLength) * Sim.BlendAlpha);
        else
        {
            float warmth = MeanTemperature(day) - WeatherOutlook.FreezingC;
            if (warmth > 0) SnowCover = MathF.Max(0, SnowCover - dt / (WorldDefaults.SnowMeltDays * dayLength) * (warmth / WorldDefaults.MeltRefC));
        }
    }
    public object State() => new { name = Sim.Active?.Name ?? "Clear", stage = Sim.Stage.ToString(),
        blendAlpha = Sim.BlendAlpha, severity = Severity, rainIntensity = Sim.BlendAlpha * Severity,
        isRaining = Sim.IsRaining && !IsSnowing, isSnowing = IsSnowing, snowCover = SnowCover,
        windMain = Sim.WindMain, fogDensity = Sim.FogDensity, today = Today, tomorrow = Tomorrow,
        dayMeanC = MeanTemperature(_day) };
}
