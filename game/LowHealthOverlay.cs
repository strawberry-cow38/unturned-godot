using Godot;

namespace UnturnedGodot
{
    /// <summary>
    /// Red vignette + desaturation as health falls (master 2026-09-03: "add a red vignette and lose color
    /// saturation when low hp"). No retail source -- searched PlayerUI/PlayerLifeUI, neither has anything like
    /// it -- so this is a game-feel addition, not a port, and stays exactly as literal as the ask: driven off
    /// the health FRACTION alone, nothing else folded in.
    ///
    /// A screen-space CanvasItem shader (RainOverlay's shape: CanvasLayer + ColorRect + ShaderMaterial), not a
    /// write to the shared Godot.Environment.AdjustmentSaturation DayNightCycle already animates. That
    /// Environment is ONE resource for the whole world, so a second writer fighting it every frame would
    /// flicker lighting for every player sharing it -- and in MP it means one player's low HP would desaturate
    /// EVERYONE's screen. Screen-space is the only naturally per-viewer option.
    /// </summary>
    public partial class LowHealthOverlay : Control
    {
        // Starts appearing once health drops below this fraction, reaches full strength at Critical. Retail's
        // own bleeding/broken thresholds live around 25-50%, so this band sits in the same neighbourhood rather
        // than inventing an unrelated number.
        const float StartFraction = 0.5f;
        const float CriticalFraction = 0.15f;

        const string LowHpShader = @"
shader_type canvas_item;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform float strength = 0.0;                          // 0 = full health, no effect. 1 = critical.
uniform vec3 vignette_tint : source_color = vec3(0.55, 0.02, 0.02);
void fragment() {
    vec3 src = textureLod(screen_tex, SCREEN_UV, 0.0).rgb;
    // Desaturate toward luma -- proportional to strength, so healthy play is untouched and near-death is
    // close to monochrome without ever quite reaching it (full grey would read as a rendering fault, not HP).
    float luma = dot(src, vec3(0.299, 0.587, 0.114));
    vec3 desat = mix(src, vec3(luma), strength * 0.85);
    // Vignette: distance from centre in an ASPECT-CORRECTED space, so it reads as a circle rather than an
    // ellipse on a widescreen output -- SCREEN_UV alone is 0..1 per axis regardless of the real aspect ratio.
    vec2 uv = SCREEN_UV - 0.5;
    uv.x *= SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;   // width/height, without a second uniform to keep in sync
    float d = length(uv);
    float vig = smoothstep(0.25, 0.85, d) * strength;    // inner 0.25 stays clean (never eats the crosshair); ramps to the corners
    vec3 result = mix(desat, vignette_tint, vig * 0.8);
    COLOR = vec4(result, 1.0);
}";

        ColorRect _rect;
        ShaderMaterial _mat;
        float _strength;   // smoothed; see _Process

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            var shader = new Shader { Code = LowHpShader };
            _mat = new ShaderMaterial { Shader = shader };
            _rect = new ColorRect { Material = _mat, Color = Colors.White };
            _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _rect.MouseFilter = Control.MouseFilterEnum.Ignore;
            AddChild(_rect);
        }

        float _targetStrength;

        /// <summary>`fraction` is Health / MaxHealth, called every HUD frame regardless of mode -- Player.Health
        /// is already correctly populated for SP, the loopback, and a real MP client (AdoptReplicatedFineVitals'
        /// coarse pin), so this needs no wire event of its own, unlike the hurt indicator.</summary>
        public void SetFraction(float fraction)
        {
            fraction = Mathf.Clamp(fraction, 0f, 1f);
            _targetStrength = fraction >= StartFraction ? 0f
                : Mathf.Clamp((StartFraction - fraction) / (StartFraction - CriticalFraction), 0f, 1f);
        }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            // Smoothed rather than snapped straight to the target: healing 1 HP at a time (regen) would
            // otherwise flicker the vignette in and out around the threshold every tick.
            _strength = Mathf.Lerp(_strength, _targetStrength, 1f - Mathf.Exp(-6f * (float)delta));
            _mat.SetShaderParameter("strength", _strength);
        }
    }
}
