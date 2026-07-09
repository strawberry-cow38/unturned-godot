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

        ColorRect _rect;
        ShaderMaterial _mat;
        float _t;

        const string RainShader = @"
shader_type canvas_item;
uniform float time;
uniform float intensity = 1.0;
float hash(vec2 p){ return fract(sin(dot(p, vec2(41.3, 289.1))) * 43758.5453); }
void fragment(){
    vec2 uv = SCREEN_UV;
    float a = 0.0;
    for(int i = 0; i < 3; i++){
        float fi = float(i);
        float cols = 90.0 + fi * 55.0;          // more, finer streaks in front layers
        float x = uv.x * cols;
        float col = floor(x);
        float fx = fract(x);
        float lineMask = smoothstep(0.40, 0.5, fx) * (1.0 - smoothstep(0.5, 0.60, fx));
        float rnd = hash(vec2(col, fi * 7.0));
        if(hash(vec2(col + 31.0, fi)) < 0.55) continue;    // gaps between drops
        float speed = 1.4 + fi * 0.9 + rnd * 0.6;
        float y = fract(uv.y * (2.0 + fi) + time * speed + rnd);
        float drop = smoothstep(0.0, 0.02, y) * (1.0 - smoothstep(0.02, 0.22, y));   // a falling dash
        a += lineMask * drop * (0.30 - fi * 0.06);
    }
    COLOR = vec4(0.80, 0.85, 0.95, clamp(a, 0.0, 1.0) * intensity);
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
            _mat.SetShaderParameter("time", _t);
            _mat.SetShaderParameter("intensity", Raining ? Intensity : 0f);
            _rect.Visible = Raining;
            if (Cycle != null) Cycle.Overcast = Raining;
        }
    }
}
