using Godot;

namespace UnturnedGodot
{
    // Real picture-in-picture scope (master: "real PiP scopes that show on the VM, not the cheap overlay").
    // A second camera renders the MAIN world zoomed, following the player's aim, into a SubViewport; when a scoped
    // gun is ADS'd that render fills a circular lens at screen centre with the periphery darkened -- the retail
    // SrScope look (they do it single-render for perf; we dual-render one gun for a crisp independent magnification).
    // Aug-only for now: PlayerController.ScopeMag returns >1 only for the augewehr. Reads all state from the
    // controller each frame so no per-equip wiring is needed; the scope viewport only renders while actually scoped.
    public partial class ScopeOverlay : Node
    {
        public PlayerController Pc;   // source of the look camera, the ADS blend, and the per-gun magnification

        SubViewport _svp;
        Camera3D _scopeCam;
        CanvasLayer _layer;
        ColorRect _lens;
        ShaderMaterial _mat;
        bool _built, _on;

        void Build()
        {
            _svp = new SubViewport
            {
                Size = new Vector2I(768, 768),                       // square -> round lens with no stretch
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,   // only render while scoped (perf)
                World3D = Pc.Camera.GetWorld3D(),                    // render the REAL world, not an isolated one
            };
            AddChild(_svp);
            _scopeCam = new Camera3D { Current = true, Fov = 20f };
            _svp.AddChild(_scopeCam);

            _layer = new CanvasLayer { Layer = 6 };                  // above the viewmodel composite (layer 5)
            _lens = new ColorRect();
            _lens.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _mat = new ShaderMaterial { Shader = LensShader() };
            _mat.SetShaderParameter("scope_tex", _svp.GetTexture());
            _lens.Material = _mat;
            _layer.AddChild(_lens);
            AddChild(_layer);
            _layer.Visible = false;
            _built = true;
        }

        static Shader LensShader()
        {
            return new Shader
            {
                Code = @"
shader_type canvas_item;
uniform sampler2D scope_tex : filter_linear;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform float strength = 0.0;   // 0 hip .. 1 full ADS (fades the whole effect in)
uniform float aspect = 1.777;   // screen width / height (keeps the lens a circle)
uniform float radius = 0.40;    // lens radius in half-height units (0.4 -> lens ~80% of screen height)

void fragment() {
    vec4 world = texture(screen_tex, SCREEN_UV);
    vec2 d = SCREEN_UV - vec2(0.5);
    d.x *= aspect;
    float lensR = length(d) / radius;          // 0 centre .. 1 lens edge
    if (lensR < 1.0) {
        vec2 suv = (d / radius) * 0.5 + vec2(0.5);   // map the lens disc onto the scope render
        vec4 sc = texture(scope_tex, suv);
        sc.rgb *= 1.6;                               // the isolated SubViewport renders darker -- lift it toward the main view
        float rx = smoothstep(0.0045, 0.0015, abs(d.x));   // thin crosshair reticle
        float ry = smoothstep(0.0045, 0.0015, abs(d.y));
        sc.rgb = mix(sc.rgb, vec3(0.03), max(rx, ry) * step(lensR, 0.86));
        float rim = smoothstep(1.0, 0.90, lensR);    // soft black rim at the glass edge
        vec4 lens = mix(vec4(0.0, 0.0, 0.0, 1.0), sc, rim);
        COLOR = mix(world, lens, strength);
    } else {
        COLOR = mix(world, world * 0.18, strength); // darkened peripheral (SrScope dark-scope mode)
    }
}"
            };
        }

        public override void _Process(double delta)
        {
            if (Pc == null || Pc.Camera == null) return;
            if (!_built) { if (Pc.Camera.GetWorld3D() == null) return; Build(); }   // wait until the look camera is in the tree (has a world) before sharing it

            float mag = Pc.ScopeMag;
            float aim = Pc.CurrentAimAlpha;
            bool scoped = mag > 1.01f && aim > 0.5f;

            if (scoped)
            {
                _scopeCam.GlobalTransform = Pc.Camera.GlobalTransform;   // look exactly where the player aims
                _scopeCam.Fov = Pc.Camera.Fov / mag;                     // zoomed
                _svp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                Vector2 vp = GetViewport().GetVisibleRect().Size;
                _mat.SetShaderParameter("aspect", vp.Y > 0f ? vp.X / vp.Y : 1.777f);
                _mat.SetShaderParameter("strength", Mathf.Clamp((aim - 0.5f) / 0.35f, 0f, 1f));   // fully on by ADS ~0.85
                _layer.Visible = true;
                _on = true;
            }
            else if (_on)
            {
                _layer.Visible = false;
                _svp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;   // stop rendering the world twice
                _on = false;
            }
        }
    }
}
