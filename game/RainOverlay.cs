using Godot;

namespace UnturnedGodot
{
    // Bounded weather: a screen-space rain overlay (layered scrolling streaks in a CanvasItem shader) + it flips the
    // day/night cycle to Overcast (denser fog, greyer) while raining. Unturned drives weather from LevelLighting;
    // this is a simple, reliable stand-in (a 2D overlay always renders, unlike headless 3D particles).
    public partial class RainOverlay : CanvasLayer
    {
        public DayNightCycle Cycle;
        public bool Raining = true;
        public float Intensity = 1f;
        public bool RampDemo;   // demo: oscillate Intensity light<->heavy to show off varying intensity

        ColorRect _rect;
        ShaderMaterial _mat;
        float _t;

        // Layered parallax rain: 4 sheets of falling streaks at increasing size/speed (far thin+dense+slow ->
        // near fat+sparse+fast) skewed by `wind`, with per-column phase stagger + per-drop jitter/brightness so it
        // never tiles. `intensity` (0 drizzle .. 1 downpour) scales density + opacity. Splashes/wetness are separate
        // world-space passes; this is just the airborne rain (master 2026-08-29: nice, swag, pretty, performant).
        const string RainShader = @"
shader_type canvas_item;
uniform float time;
uniform float intensity = 1.0;
uniform float wind = 0.14;                                   // horizontal slant of the streaks
uniform vec3 rain_tint : source_color = vec3(0.80, 0.86, 0.98);
float h21(vec2 p){ p = fract(p * vec2(127.32, 311.7)); p += dot(p, p + 34.53); return fract(p.x * p.y); }
// one parallax sheet of falling streaks
float sheet(vec2 uv, float t, float dens, float speed, float len, float thick, float fill){
    uv.x += uv.y * wind;                                     // wind slant
    vec2 g = vec2(uv.x * dens, uv.y * dens * 0.14);          // long cells -> streaky
    float phase = h21(vec2(floor(g.x), 5.2));
    g.y += phase * 31.0 - t * speed;                         // -t so it falls DOWN (SCREEN_UV y=0 is top) + per-column phase stagger
    vec2 id = floor(g);
    float r = h21(id);
    float present = step(1.0 - fill * clamp(0.35 + intensity, 0.0, 1.0), r);   // more drops at higher intensity
    float fx = fract(g.x) - 0.5 + (r - 0.5) * 0.55;          // x jitter within the column
    float lx = smoothstep(thick, 0.0, abs(fx));             // thin bright core
    float fy = fract(g.y);
    float head = smoothstep(0.0, 0.04, fy);
    float tail = 1.0 - smoothstep(len * (0.6 + r * 0.4), 1.0, fy);
    return present * lx * head * tail * (0.4 + r * 0.6);
}
void fragment(){
    vec2 uv = SCREEN_UV;
    float a = 0.0;
    a += sheet(uv + 3.0,  time, 66.0, 1.2, 0.80, 0.040, 0.35) * 0.14;   // far, thin, faint
    a += sheet(uv + 9.0,  time, 44.0, 1.9, 0.66, 0.055, 0.35) * 0.18;
    a += sheet(uv + 17.0, time, 28.0, 2.8, 0.52, 0.075, 0.30) * 0.22;   // near
    a += sheet(uv + 27.0, time, 17.0, 4.0, 0.42, 0.100, 0.28) * 0.24;   // nearest
    a = clamp(a, 0.0, 1.0);
    float alpha = a * (0.30 + intensity * 0.40);
    COLOR = vec4(rain_tint, clamp(alpha, 0.0, 1.0) * clamp(intensity, 0.0, 1.0));
}";

        public override void _Ready()
        {
            Layer = 9;   // under the HUD (10) / inventory (11), over the 3D
            var shader = new Shader { Code = RainShader };
            _mat = new ShaderMaterial { Shader = shader };
            _mat.SetShaderParameter("intensity", 1f);
            _rect = new ColorRect { Material = _mat, Color = Colors.White };
            _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _rect.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(_rect);
        }

        public override void _Process(double delta)
        {
            _t += (float)delta;
            if (RampDemo) Intensity = 0.15f + 0.85f * (0.5f + 0.5f * Mathf.Sin(_t * 0.8f));   // light <-> heavy sweep
            _mat.SetShaderParameter("time", _t);
            _mat.SetShaderParameter("intensity", Raining ? Intensity : 0f);
            _rect.Visible = Raining;
            if (Cycle != null) Cycle.Overcast = Raining;
        }
    }
}
