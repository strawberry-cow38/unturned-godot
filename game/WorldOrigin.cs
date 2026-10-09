using Godot;

namespace UnturnedGodot
{
    /// <summary>
    /// The floating origin, as the WORLD-SPACE PATTERNS see it (strawberry 2026-10-09: "any way to make the rebases a
    /// lil softer? its very visible that something happens").
    ///
    /// Every shader that draws a pattern from its world position -- the sea's swell, the wind gusting through trees,
    /// grass and wires, rain rings on roads, caustics, the terrain's paint-edge jitter -- computes it from the
    /// engine's LOCAL coordinates. In the infinite world a rebase moves every one of those local positions a
    /// kilometre at once, so every pattern jumped to a different phase on the same frame: the whole scene twitched.
    ///
    /// The fix is to feed the patterns the absolute position instead: local + origin. Not the raw origin -- a float
    /// at 10^7 m has a metre of precision -- but the origin MODULO <see cref="Period"/>, so what reaches the GPU is
    /// at most ~33 km and good to millimetres. Patterns are continuous across every rebase except the one that
    /// wraps the period, i.e. once per 32 km of travel instead of once per kilometre. Every fixed map leaves this at
    /// zero, which changes nothing it draws.
    ///
    /// CPU code that must agree with a shader (WaveField floats boats on the GPU's swell) adds <see cref="Offset"/>
    /// the same way.
    /// </summary>
    public static class WorldOrigin
    {
        public const double Period = 32768.0;   // a multiple of every tile size and of the region size
        public static readonly StringName Param = "ug_origin";
        static bool _registered;

        /// <summary>The origin modulo Period, xz -- what the shaders add to a local world position.</summary>
        public static Vector2 Offset { get; private set; }

        /// <summary>Register the global ONCE, before any material that reads it compiles (the GrassDisplacers lesson:
        /// a material compiled against a missing global dies). Both shader-global funnels call this.</summary>
        public static void EnsureGlobal()
        {
            if (_registered) return;
            _registered = true;
            RenderingServer.GlobalShaderParameterAdd(Param, RenderingServer.GlobalShaderParameterType.Vec2, Vector2.Zero);
        }

        public static void Set(double originX, double originZ)
        {
            EnsureGlobal();
            Offset = new Vector2((float)Wrap(originX), (float)Wrap(originZ));
            RenderingServer.GlobalShaderParameterSet(Param, Offset);
        }

        static double Wrap(double v) { double m = v % Period; return m < 0 ? m + Period : m; }
    }
}
