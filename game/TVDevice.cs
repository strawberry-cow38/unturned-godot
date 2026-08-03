using Godot;

namespace UnturnedGodot
{
    // An in-game TELEVISION -- Television_0 (flatscreen) / Television_1 (CRT). Look at it and press F to toggle
    // it ON/OFF (per-TV state). When ON *and* the town grid is live (PowerNet.GlobalPower), the screen shows the
    // SMPTE test pattern EMISSIVE (self-lit, so it glows into bloom), a soft OmniLight spills forward off the
    // screen, and tv_tone.wav loops quietly with a fast 3D falloff. Toggling on plays tv_on.wav once; toggling
    // off plays tv_off.wav once. When OFF or the grid is dead: screen dark, no light, no tone -- and if the grid
    // dies while it's on, it goes dark (the DayNightCycle power sweep calls Refresh, like the glow containers).
    //
    // The CRT (Television_1) WARMS UP: toggling it on does NOT snap the picture -- after a short dead delay the
    // emissive + light FADE IN over ~1.5s (a tube heating up). The flatscreen (Television_0) snaps on instantly.
    //
    // The SCREEN is the prop's darkest palette texel, carved off the body mesh by UV (ObjMesh.SplitByUv) and then
    // its one-texel UVs are REPLACED with a planar projection so the whole pattern fills the screen face. The
    // wiring mirrors proven props: the emissive material + OmniLight + "tvdevices" group swept on a grid change
    // come from StoreShelf's cooler/fridge interior glow; the bit-6 look collider + SetMeta("tvdevice", ...)
    // F-interact routing come from ObjectDoor/GasPump. Ripped meshes need CullMode.Disabled.
    public partial class TVDevice : Node3D
    {
        // ---- palette-derived screen texel predicates (GODOT uv space -- ObjMesh V-flips on load) ----------------
        // Both TV screens are the prop's darkest grey texel and both are authored FACE-UP (local +Y normal); the
        // level placement basis stands the prop upright so the screen faces the room. Verified against the .obj:
        //   CRT (Television_1): screen at u>0.5, v<0.5   (rgb 53,53,53; a 0.85x0.79 recessed face)
        //   Flatscreen (Television_0): the INSET front face at u<0.25, v>0.5 (rgb 39,39,39; 3.55x1.8, recessed in
        //   the bezel). The u<0.25, v<0.5 face is the FLAT BACK panel (rgb 56,56,56) -- not the screen.
        static bool CrtScreen(Vector2 a, Vector2 b, Vector2 c)
            => a.X > 0.5f && b.X > 0.5f && c.X > 0.5f && a.Y < 0.5f && b.Y < 0.5f && c.Y < 0.5f;
        static bool FlatScreen(Vector2 a, Vector2 b, Vector2 c)
            => a.X < 0.25f && b.X < 0.25f && c.X < 0.25f && a.Y > 0.5f && b.Y > 0.5f && c.Y > 0.5f;

        public string PropName = "Television_1";
        bool _isCrt;              // Television_1 warms up; Television_0 snaps on
        bool _on;                 // player toggle state (independent of the grid)
        bool _lit;                // last EFFECTIVE state (_on && grid power) actually applied to the visuals

        MeshInstance3D _screen;   // the emissive SMPTE screen sub-mesh (hidden when dark)
        StandardMaterial3D _screenMat;
        OmniLight3D _light;       // soft forward spill (energy 0 / hidden when dark)
        AudioStreamPlayer3D _tone;               // looping 1kHz tone -- plays only while lit
        AudioStreamPlayer3D _onClick, _offClick; // one-shot turn-on / turn-off clicks

        // tunable brightness (env overrides so the visual can be dialed in from a render without a rebuild)
        float _emitEnergy = EnvF("UG_TV_EMIT", 0.4f);    // screen emissive multiplier -- low so the SMPTE colours read instead of clipping to white under bloom (render-verified); env-tunable
        float _lightEnergy = EnvF("UG_TV_LIGHT", 0.6f);  // forward spill energy

        // CRT warmup: WarmDelay dead, then _warm ramps 0->1 over WarmDur, scaling emissive + light.
        const float WarmDelay = 0.3f, WarmDur = 1.5f;
        bool _warming; float _warmDelay, _warm;

        Vector3 _screenCenterLocal, _screenNormalLocal;   // stashed for the light placement + the render harness

        static float EnvF(string k, float d) => float.TryParse(System.Environment.GetEnvironmentVariable(k), out var v) ? v : d;

        /// <summary>Build a TV device for a placed prop. <paramref name="bodyMi"/> is the prop's body
        /// MeshInstance3D (its Mesh is split for the screen, its Transform is copied so the screen sub-mesh --
        /// carved in the body's own local space -- lines up exactly). Add the returned node to the SAME parent
        /// the body was added to.</summary>
        public static TVDevice Make(MeshInstance3D bodyMi, string propName)
        {
            var tv = new TVDevice { PropName = propName, _isCrt = propName == "Television_1", Transform = bodyMi.Transform };
            tv.Build(bodyMi.Mesh as ArrayMesh);
            return tv;
        }

        void Build(ArrayMesh body)
        {
            if (body == null) { GD.PrintErr($"[tv] {PropName}: no body mesh"); return; }
            // Split the screen texel off the body. Bucket[0] = matched (screen) tris; Build() returns null for an
            // empty bucket, so a null bucket means the predicate matched nothing (wrong prop / re-extracted UVs).
            var parts = ObjMesh.SplitByUv(body, _isCrt ? 71 : 70, _isCrt ? CrtScreen : FlatScreen);
            var screenMesh = parts != null && parts.Length >= 1 ? parts[0] : null;
            if (screenMesh == null) { GD.PrintErr($"[tv] {PropName}: screen split matched no triangles"); return; }

            var pattern = LoadPattern();
            var projected = Reproject(screenMesh, body.GetAabb().GetCenter());   // one-texel UVs -> planar 0..1 fill

            _screenMat = new StandardMaterial3D
            {
                AlbedoTexture = pattern,
                EmissionEnabled = true, EmissionTexture = pattern, Emission = Colors.White, EmissionEnergyMultiplier = _emitEnergy,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,          // SMPTE is a real image, not a palette texel
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,                  // ripped mesh: winding may face either way
            };
            _screen = new MeshInstance3D { Mesh = projected, MaterialOverride = _screenMat, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_screen);

            // Soft forward spill just in FRONT of the screen (the emissive screen alone doesn't light the room).
            _light = new OmniLight3D
            {
                LightColor = new Color(0.85f, 0.9f, 1.0f), OmniRange = 2.6f, LightEnergy = 0f, ShadowEnabled = false, Visible = false,
                Position = _screenCenterLocal + _screenNormalLocal * 0.3f,   // just in front of the screen face (soft forward spill; keeps the on-screen hotspot down)
            };
            AddChild(_light);

            BuildAudio();
            AddToGroup("tvdevices");   // DayNightCycle.DriveStreetlights sweeps this group on a grid change -> Refresh
            Refresh();
        }

        // Reproject the screen sub-mesh's one-texel UVs into a planar 0..1 fill, so the pattern covers the whole
        // face oriented upright (image top -> screen top = world up) and un-mirrored when viewed from the front.
        ArrayMesh Reproject(ArrayMesh screen, Vector3 bodyCenter)
        {
            var a0 = screen.SurfaceGetArrays(0);
            var V = a0[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var N = a0[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var C = a0[(int)Mesh.ArrayType.Color].AsColorArray();

            Vector3 c = Vector3.Zero; foreach (var v in V) c += v; c /= Mathf.Max(1, V.Length);
            // geometric normal (summed triangle cross products -- robust to a sliver tri), then SIGN it outward
            // (away from the body interior) so the pattern faces the room regardless of the ripped winding.
            Vector3 nrm = Vector3.Zero;
            for (int i = 0; i + 2 < V.Length; i += 3) nrm += (V[i + 1] - V[i]).Cross(V[i + 2] - V[i]);
            if (nrm.LengthSquared() < 1e-9f) nrm = Vector3.Up;
            nrm = nrm.Normalized();
            if (nrm.Dot(c - bodyCenter) < 0f) nrm = -nrm;
            _screenCenterLocal = c; _screenNormalLocal = nrm;

            // screen "up" = WORLD up projected into the screen plane, expressed in the prop's LOCAL frame (the UVs
            // are baked here in local space, then the placement basis rotates them) -- so after placement the
            // pattern's up lands on world up for ANY prop orientation. Orthonormalize drops any placement scale.
            Vector3 localUp = Transform.Basis.Orthonormalized().Inverse() * Vector3.Up;
            Vector3 ay = localUp - nrm * localUp.Dot(nrm);
            if (ay.LengthSquared() < 1e-5f)   // screen faces straight up/down in WORLD (never for a placed TV) -> stable fallback
                ay = Mathf.Abs(nrm.Z) < 0.9f ? new Vector3(0, 0, 1) - nrm * nrm.Z : new Vector3(1, 0, 0) - nrm * nrm.X;
            ay = ay.Normalized();
            Vector3 ax = ay.Cross(nrm).Normalized();   // viewer-right from the +normal side -> increasing U reads left-to-right, un-mirrored

            float umin = 1e9f, umax = -1e9f, vmin = 1e9f, vmax = -1e9f;
            var pu = new float[V.Length]; var pv = new float[V.Length];
            for (int i = 0; i < V.Length; i++)
            {
                Vector3 d = V[i] - c; pu[i] = d.Dot(ax); pv[i] = d.Dot(ay);
                umin = Mathf.Min(umin, pu[i]); umax = Mathf.Max(umax, pu[i]);
                vmin = Mathf.Min(vmin, pv[i]); vmax = Mathf.Max(vmax, pv[i]);
            }
            float uw = Mathf.Max(1e-5f, umax - umin), vw = Mathf.Max(1e-5f, vmax - vmin);
            var newU = new Vector2[V.Length];
            for (int i = 0; i < V.Length; i++)
                newU[i] = new Vector2((pu[i] - umin) / uw, (vmax - pv[i]) / vw);   // V flipped: image top (v=0) at the screen top

            var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = V; arr[(int)Mesh.ArrayType.Normal] = N; arr[(int)Mesh.ArrayType.TexUV] = newU; arr[(int)Mesh.ArrayType.Color] = C;
            var m = new ArrayMesh(); m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            return m;
        }

        static ImageTexture _pattern;
        static ImageTexture LoadPattern()
        {
            if (_pattern != null) return _pattern;
            var img = new Image();   // raw png at runtime: Image.Load, not GD.Load (game feedback)
            string p = ProjectSettings.GlobalizePath("res://content/objects/smpte_pattern.png");
            if (!System.IO.File.Exists(p) || img.Load(p) != Error.Ok) { GD.PrintErr("[tv] smpte_pattern.png missing/failed"); return null; }
            _pattern = ImageTexture.CreateFromImage(img);
            return _pattern;
        }

        void BuildAudio()
        {
            // Looping tone: quiet-but-noticeable, small UnitSize + a MaxDistance cap so it falls off fast.
            var tone = PlayerController.LoadWavOneShot("res://content/sounds/tv_tone.wav", loop: true);
            if (tone != null) { _tone = new AudioStreamPlayer3D { Stream = tone, VolumeDb = Mathf.LinearToDb(0.45f), UnitSize = 2f, MaxDistance = 12f, Position = _screenCenterLocal }; AddChild(_tone); }
            var on = PlayerController.LoadWavOneShot("res://content/sounds/tv_on.wav");
            if (on != null) { _onClick = new AudioStreamPlayer3D { Stream = on, VolumeDb = Mathf.LinearToDb(0.7f), UnitSize = 3f, MaxDistance = 16f, Position = _screenCenterLocal }; AddChild(_onClick); }
            var off = PlayerController.LoadWavOneShot("res://content/sounds/tv_off.wav");
            if (off != null) { _offClick = new AudioStreamPlayer3D { Stream = off, VolumeDb = Mathf.LinearToDb(0.7f), UnitSize = 3f, MaxDistance = 16f, Position = _screenCenterLocal }; AddChild(_offClick); }
        }

        /// <summary>Player F-interact: flip the toggle, click, and refresh. The click plays even with no grid
        /// power (you still hear the switch); the picture/tone only come up if the grid is live.</summary>
        public void Toggle()
        {
            _on = !_on;
            (_on ? _onClick : _offClick)?.Play();
            Refresh();
        }

        /// <summary>Bring the screen/light/tone in line with the effective state (_on AND grid power). Called by
        /// Toggle() and by the DayNightCycle grid sweep, so losing the grid drops a lit TV to dark and restoring
        /// it warms it back up. A fresh power-up kicks off the CRT warmup; the flatscreen snaps.</summary>
        public void Refresh()
        {
            bool eff = _on && PowerNet.GlobalPower;
            if (eff == _lit) return;
            _lit = eff;
            if (eff)
            {
                if (_isCrt) { _warming = true; _warmDelay = WarmDelay; _warm = 0f; }   // tube warms in
                else { _warming = false; _warm = 1f; }                                 // flatscreen snaps
                if (_screen != null) _screen.Visible = true;
                if (_light != null) _light.Visible = true;
                _tone?.Play();
                ApplyLevels();
            }
            else
            {
                _warming = false; _warm = 0f;
                if (_screen != null) _screen.Visible = false;
                if (_light != null) _light.Visible = false;
                _tone?.Stop();
            }
        }

        void ApplyLevels()
        {
            float k = _isCrt ? _warm : 1f;
            if (_screenMat != null) _screenMat.EmissionEnergyMultiplier = _emitEnergy * k;
            if (_light != null) _light.LightEnergy = _lightEnergy * k;
        }

        public override void _Process(double delta)
        {
            if (!_warming) return;
            if (_warmDelay > 0f) { _warmDelay -= (float)delta; if (_warmDelay > 0f) return; }   // dead time before the tube lights
            _warm = Mathf.Min(1f, _warm + (float)delta / WarmDur);
            if (_warm >= 1f) _warming = false;
            ApplyLevels();
        }

        // ---- render-harness / test seams ------------------------------------------------------------------------
        public bool DebugScreenOk => _screen != null;
        public Vector3 DebugScreenCenterWorld => ToGlobal(_screenCenterLocal);
        public Vector3 DebugScreenNormalWorld => (GlobalTransform.Basis.Orthonormalized() * _screenNormalLocal).Normalized();
        /// <summary>Force the TV on for a render. instant=true skips the CRT warmup so a single captured frame is
        /// at full brightness.</summary>
        public void DebugForceOn(bool instant = true)
        {
            _on = true;
            Refresh();
            if (instant) { _warming = false; _warm = 1f; ApplyLevels(); }
        }
    }
}
