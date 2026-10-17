using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Engine adapter for WeatherManager's core weather, outlook and snow-cover
/// rules. The original types, schedule, seasonal model and clock replication remain
/// authoritative; Bevy consumes the conditions and supplies visual/audio effects.</summary>
public sealed partial class WorldEnvironment
{
    /// <summary>The assembled weather table (PEI + variants + snow) and its schedule. Host and remote build
    /// the identical table from the same source lists, so a replicated type index names the same weather
    /// on both sides (v80 System21).</summary>
    public sealed record Tables(WeatherType[] Types, WeatherSchedule[] Schedule, int VariantBase, int SnowType, int SnowSchedule)
    {
        public static Tables Build()
        {
            var types = new List<WeatherType>(WeatherSim.PeiTypes());
            int variants = types.Count; types.AddRange(WeatherSim.VariantTypes());
            int snowType = types.Count; types.AddRange(WeatherSim.SnowTypes());
            var schedule = new List<WeatherSchedule>(WeatherSim.PeiSchedule());
            schedule.AddRange(WeatherSim.VariantSchedule(variants));
            int snowSchedule = schedule.Count; schedule.AddRange(WeatherSim.SnowSchedule(snowType));
            return new(types.ToArray(), schedule.ToArray(), variants, snowType, snowSchedule);
        }
        public WeatherType? Type(int index) => index >= 0 && index < Types.Length ? Types[index] : null;
        public WeatherOutlook.Day Outlook(int seed, int day) => WeatherOutlook.ForDay(seed, day, Types, Schedule,
            WorldTemperature.SeasonPhase(WorldTemperature.DayOfYear(WorldTemperature.StartDayOfYear, day)), MeanTemperature(day), SnowSchedule);
    }
    readonly WeatherType[] _types;
    readonly WeatherSchedule[] _schedule;
    readonly int _seed, _snowSchedule, _snowType, _baseDay, VariantBase;
    int _day;
    public WeatherSim Sim { get; }
    public WeatherOutlook.Day Today { get; private set; }
    public WeatherOutlook.Day Tomorrow { get; private set; }
    public float SnowCover { get; private set; }
    public float PuddleLevel { get; private set; }
    public bool IsSnowing => Sim.ActiveTypeIndex == _snowType && Sim.BlendAlpha > .001f;
    public float Severity => Sim.Active is { } t ? Math.Clamp(t.FogDensity, .1f, 1f) : 1f;

    public WorldEnvironment(NetWorldServer server, int seed, WorldSave save = null)
    {
        if (float.TryParse(Environment.GetEnvironmentVariable("UG_PUDDLE"), out float puddle)) PuddleLevel = Math.Clamp(puddle, 0, 1);
        // RainSystem3D.EnsureGlobals seeds this diagnostic on every source world path.
        if (float.TryParse(Environment.GetEnvironmentVariable("UG_SNOWCOVER"), out float snow) && float.IsFinite(snow)) SnowCover = Math.Clamp(snow, 0, 1);
        _seed = seed; _baseDay = save?.Day ?? 0;
        server.Clock.ServerConfigure(save?.TimeOfDay01 ?? WorldDefaults.StartTime,
            save?.DayLengthSeconds > 0 ? save.DayLengthSeconds : WorldDefaults.DefaultDayLength, 0);
        var tables = _tables = Tables.Build();
        int variants = VariantBase = tables.VariantBase;
        _snowType = tables.SnowType; _snowSchedule = tables.SnowSchedule;
        _types = tables.Types; _schedule = tables.Schedule;
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
        Publish(server);
    }
    readonly Tables _tables;

    /// <summary>v80 System21: the accepted weather/calendar every remote rebuilds State() from. Quantized and
    /// dirty-on-change inside EnvironmentReplication, so calling it every tick costs nothing at steady state.</summary>
    void Publish(NetWorldServer server) => server.Environment.ServerSet(_seed, _baseDay, Sim.ActiveTypeIndex, Sim.Stage,
        Sim.BlendAlpha, PuddleLevel, SnowCover, server.Session.CurrentTick);

    public int DayAt(WorldClockReplication clock, long tick) => DayAt(_baseDay, clock, tick);
    public static int DayAt(int baseDay, WorldClockReplication clock, long tick) => baseDay +
        (int)Math.Floor(clock.BaseTime01 + tick * SimClock.FixedDelta / clock.DayLengthSeconds);

    void Refresh(int day)
    {
        _day = day; Today = Outlook(day); Tomorrow = Outlook(day + 1);
    }
    internal static float MeanTemperature(int day) => WorldTemperature.BaseMeanC + WorldTemperature.SeasonAmplitude *
        WorldTemperature.SeasonPhase(WorldTemperature.DayOfYear(WorldTemperature.StartDayOfYear, day));
    WeatherOutlook.Day Outlook(int day) => _tables.Outlook(_seed, day);

    public void Step(NetWorldServer server)
    {
        int day = DayAt(server.Clock, server.Session.CurrentTick);
        if (_day != day) Refresh(day);
        float dt = (float)SimClock.FixedDelta, dayLength = server.Clock.DayLengthSeconds;
        Sim.Step(dt);
        PuddleLevel = WorldDefaults.StepPuddle(PuddleLevel, Sim.BlendAlpha * Severity, dt);
        if (IsSnowing)
            SnowCover = MathF.Min(1, SnowCover + dt / (WorldDefaults.SnowLayDays * dayLength) * Sim.BlendAlpha);
        else
        {
            float warmth = MeanTemperature(day) - WeatherOutlook.FreezingC;
            if (warmth > 0) SnowCover = MathF.Max(0, SnowCover - dt / (WorldDefaults.SnowMeltDays * dayLength) * (warmth / WorldDefaults.MeltRefC));
        }
        Publish(server);
    }
    public object State() => StateOf(Sim.Active, Sim.ActiveTypeIndex == _snowType, Sim.Stage, Sim.BlendAlpha, PuddleLevel, SnowCover, Today, Tomorrow, _day);

    /// <summary>The one State() projection, shared by the host sim and the remote replica (RemoteEnvironment).
    /// Severity/wind/fog/rain flags are the WeatherSim/WorldEnvironment derivations of (type, blend).</summary>
    internal static object StateOf(WeatherType? active, bool snowType, WeatherStage stage, float blend, float puddle, float snow,
        WeatherOutlook.Day today, WeatherOutlook.Day tomorrow, int day)
    {
        float severity = active is { } t ? Math.Clamp(t.FogDensity, .1f, 1f) : 1f;
        float committed = Math.Clamp(blend, 0f, 1f);
        bool snowing = snowType && blend > .001f;
        return new { name = active?.Name ?? "Clear", stage = stage.ToString(),
            rainWetness = blend * severity, rainPuddle = puddle,
            blendAlpha = blend, severity, rainIntensity = blend * severity,
            isRaining = blend > .001f && !snowing, isSnowing = snowing, snowCover = snow,
            hasLightning = active?.HasLightning ?? false, minLightningInterval = active?.MinLightningInterval ?? 0,
            maxLightningInterval = active?.MaxLightningInterval ?? 0,
            windMain = active is { } w ? w.WindMain * committed : 0f, fogDensity = active is { } f ? f.FogDensity * committed : 0f,
            today, tomorrow, dayMeanC = MeanTemperature(day) };
    }
}
