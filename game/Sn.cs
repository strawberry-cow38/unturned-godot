using Godot;

namespace UnturnedGodot
{
    // Cached StringNames for per-frame engine calls. Passing a C# string literal to SetShaderParameter/HasMeta/GetMeta
    // allocates a managed StringName wrapper (+ a native StringName, a DisposablesTracker entry and a finalizer) on
    // EVERY call; ETW allocation ticks (2026-09-02) showed that churn feeding the gen1 GC pauses. A static readonly
    // StringName is allocated once for the process lifetime. Add names here as hot call sites are converted.
    public static class Sn
    {
        public static readonly StringName alive = "alive";
        public static readonly StringName ambient_equator = "ambient_equator";
        public static readonly StringName ambient_ground = "ambient_ground";
        public static readonly StringName cloud_intensity = "cloud_intensity";
        public static readonly StringName cloud_params = "cloud_params";
        public static readonly StringName cloud_rim_color = "cloud_rim_color";
        public static readonly StringName clouds_tex = "clouds_tex";
        public static readonly StringName equator_color = "equator_color";
        public static readonly StringName flag_len = "flag_len";
        public static readonly StringName flag_tex = "flag_tex";
        public static readonly StringName gaspump = "gaspump";
        public static readonly StringName gridpower = "gridpower";
        public static readonly StringName ground_color = "ground_color";
        public static readonly StringName lightning_dir = "lightning_dir";
        public static readonly StringName lightning_flash = "lightning_flash";
        public static readonly StringName lightning_tint = "lightning_tint";
        public static readonly StringName lit = "lit";
        public static readonly StringName moon_color = "moon_color";
        public static readonly StringName moon_direction = "moon_direction";
        public static readonly StringName moon_light_direction = "moon_light_direction";
        public static readonly StringName objectdoor = "objectdoor";
        public static readonly StringName outline_color = "outline_color";
        public static readonly StringName period = "period";
        public static readonly StringName sag = "sag";
        public static readonly StringName sky_color = "sky_color";
        public static readonly StringName sqr_moon_radius = "sqr_moon_radius";
        public static readonly StringName stars_cutoff = "stars_cutoff";
        public static readonly StringName stars_tex = "stars_tex";
        public static readonly StringName sun_color = "sun_color";
        public static readonly StringName sun_direction = "sun_direction";
        public static readonly StringName sun_inner = "sun_inner";
        public static readonly StringName sun_outer = "sun_outer";
        public static readonly StringName thickness = "thickness";
        public static readonly StringName time_s = "time_s";
        public static readonly StringName wind = "wind";
    }
}
