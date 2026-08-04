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
        bool _broken;             // prop smashed -> screen + light + tone stay dead through any grid sweep
        bool _lit;                // last EFFECTIVE state (_on && grid power) actually applied to the visuals

        MeshInstance3D _screen;   // the emissive SMPTE screen sub-mesh (hidden when dark)
        StandardMaterial3D _screenMat;
        SpotLight3D _light;       // forward spill, aimed down the screen normal (energy 0 / hidden when dark)
        MeshInstance3D _cone;     // the visible light shaft, StreetLight's beam reused
        StandardMaterial3D _coneMat;

        const float ConeLen = 3.2f, ConeBaseR = 1.1f, ConeAlpha = 0.05f;   // shaft reach / spread at the far end / overall softness

        // NTSC flicker (master: "make the crt SMPTE bars flicker at ntsc refresh rate as well as the light effect").
        // 59.94 Hz is the real NTSC FIELD rate. Sampling it at a 60 Hz render gives a ~0.06 Hz beat -- one slow pulse
        // roughly every 17s -- which is not a bug in the choice, it is precisely what a 60fps camera pointed at a CRT
        // records, and it is the reason a flicker at the true rate reads as a gentle breathing rather than a strobe.
        // Only the CRT gets it; the flatscreen is an LCD and holds its pixels steady.
        const float NtscFieldHz = 59.94f, FlickerDepth = 0.06f;
        float _flickerPhase;
        MeshInstance3D _outline;  // whole-prop white rim silhouette on the outline overlay -- shown while looked at (F affordance)
        AudioStreamPlayer3D _tone;               // looping 1kHz tone -- plays only while lit
        AudioStreamPlayer3D _onClick, _offClick; // one-shot turn-on / turn-off clicks

        // tunable brightness (env overrides so the visual can be dialed in from a render without a rebuild)
        float _emitEnergy = EnvF("UG_TV_EMIT", 1.0f);    // screen brightness -> AlbedoColor on an UNSHADED material. 1.0 = the SMPTE texture at face value, which is the point: no lighting term can shift the bars any more. >1 pushes it into bloom. (Was 0.4 as an emission multiplier back when the screen was lit and needed holding down to stop it clipping white.)
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

            // UNSHADED (master: "make the light they emit not reflect on the screen, bc its washing out the colors").
            // The screen used to be a normal lit material carrying BOTH albedo and emission, with the TV's own
            // OmniLight sitting 0.3m in front of it -- so every TV was lighting its own screen, and the diffuse term
            // pushed the SMPTE bars toward white. A screen is a light SOURCE, not a lit surface, so it should take no
            // lighting at all. Unshaded also makes the fix total rather than a tuning exercise: no light in the world
            // can wash it out, not the TV's own spill, not the sun through a window, not a torch in your hand.
            //
            // Unshaded outputs ALBEDO directly and ignores the emission slot, so brightness now rides AlbedoColor
            // instead of EmissionEnergyMultiplier. That is also what gives the CRT its fade: lerping this from black
            // fades THE TEXTURE ITSELF up, rather than dimming a light over an already-visible picture (master: "on
            // crts the texture itself should fade in from black"). Values above 1 still bloom, same trick the tracer
            // and muzzle-flash materials use.
            _screenMat = MakeScreenMaterial(pattern);
            _screen = new MeshInstance3D { Mesh = projected, MaterialOverride = _screenMat, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_screen);

            // DIRECTIONAL spill (master: "the light should also be directional"). An OmniLight threw light backwards
            // through the cabinet and sideways into the wall the set is against; a TV only lights what is in front of
            // it. SpotLight3D aims down its own local -Z, so the node is basised to put -Z on the screen normal.
            var screenAabb = screenMesh.GetAabb();
            _light = new SpotLight3D
            {
                LightColor = new Color(0.85f, 0.9f, 1.0f),
                SpotRange = 4.0f, SpotAngle = 55f, SpotAngleAttenuation = 1.2f,
                LightEnergy = 0f, ShadowEnabled = false, Visible = false,
                Transform = new Transform3D(AimBasis(_screenNormalLocal, aimNegZ: true), _screenCenterLocal + _screenNormalLocal * 0.05f),
            };
            AddChild(_light);

            // The visible SHAFT, reusing the streetlight's cone rather than a second implementation (master: "we
            // might wanna do the 'cone' effect we have on the streetlights for the tv effect"). BeamMesh is a better
            // fit here than it is on a lamp: its cross-section STARTS AS A RECTANGLE and rounds toward a circle with
            // depth, which is exactly what light leaving a rectangular screen does.
            // BeamMesh runs along -Y with the section in X/Z, so the node's -Y goes on the screen normal.
            Vector3 halfExt = ScreenHalfExtents(screenAabb, _screenNormalLocal);
            _cone = new MeshInstance3D
            {
                Mesh = StreetLight.BeamMesh(ConeLen, halfExt.X, halfExt.Y, ConeBaseR),
                Transform = new Transform3D(AimBasis(_screenNormalLocal, aimNegZ: false), _screenCenterLocal),
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.85f, 0.9f, 1.0f, ConeAlpha),
                    AlbedoTexture = StreetLight.ConeGradient(),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // visible from inside the shaft too
                    DisableReceiveShadows = true,
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                    TextureRepeat = false,                             // CLAMP: the gradient is 1x64 and repeat wrap
                                                                       // blends the two ends into a bright band (the
                                                                       // bug StreetLight documents at its own cone)
                },
            };
            _coneMat = (StandardMaterial3D)_cone.MaterialOverride;
            AddChild(_cone);

            // Whole-prop look-focus outline (F affordance -- tells the player F does something): the FULL body
            // silhouette on the outline overlay, hidden until looked at. Same recipe as StoreShelf._shelfGlow /
            // ObjectDoor._leafOutline.
            _outline = OutlineOverlay.MakeOutline(body);
            AddChild(_outline);

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
            // A smashed set does not take input at all. Without this the press LOOKS ignored -- Refresh keeps it dark
            // because _broken gates the effective state -- but it still flips _on, so the TV silently arms itself and
            // switches on by itself when the rubble resets. Found by the reset assertion in tv.broken_kills_screen,
            // not by looking: while it is rubble there is nothing on screen to show you it happened.
            if (_broken) return;
            _on = !_on;
            (_on ? _onClick : _offClick)?.Play();
            Refresh();
        }

        /// <summary>Bring the screen/light/tone in line with the effective state (_on AND grid power). Called by
        /// Toggle() and by the DayNightCycle grid sweep, so losing the grid drops a lit TV to dark and restoring
        /// it warms it back up. A fresh power-up kicks off the CRT warmup; the flatscreen snaps.</summary>
        // Smashed prop -> screen dead until the rubble resets (master: "when tvs get destroyed make sure to kill the
        // screen"). STATE, not a one-shot off, for the same reason GridLight.SetBroken is: Refresh re-derives `eff`
        // on every PowerNet sweep, so a TV merely switched off would light itself back up at the next grid change --
        // a glowing screen hanging in the air over its own rubble.
        //
        // This is needed at all because the screen sub-mesh and the spill light are TVDevice's OWN children, not part
        // of the prop body handed to DestructibleField, so hiding the prop's meshes does not touch them. Exactly the
        // trap the street lamp hit ("hiding the meshes left a lit cone hanging over the rubble").
        public void SetBroken(bool broken)
        {
            if (_broken == broken) return;
            _broken = broken;
            if (broken) _on = false;   // a rubble reset rebuilds the set switched OFF, not mid-programme
            Refresh();
        }

        public void Refresh()
        {
            bool eff = _on && PowerNet.GlobalPower && !_broken;
            if (eff == _lit) return;
            _lit = eff;
            if (eff)
            {
                if (_isCrt) { _warming = true; _warmDelay = WarmDelay; _warm = 0f; }   // tube warms in
                else { _warming = false; _warm = 1f; }                                 // flatscreen snaps
                if (_screen != null) _screen.Visible = true;
                if (_light != null) _light.Visible = true;
                if (_cone != null) _cone.Visible = true;
                _tone?.Play();
                ApplyLevels();
            }
            else
            {
                _warming = false; _warm = 0f;
                if (_screen != null) _screen.Visible = false;
                if (_light != null) _light.Visible = false;
                if (_cone != null) _cone.Visible = false;
                _tone?.Stop();
            }
        }

        /// <summary>NTSC field-rate brightness modulation, CRT only. Depth is deliberately shallow -- a CRT does not
        /// visibly strobe to the naked eye; the flicker is something you notice at the edge of vision, so a deep
        /// modulation would read as a fault rather than as a tube.</summary>
        internal static float Flicker(float phase01, float depth)
            => 1f - depth * 0.5f * (1f - Mathf.Cos(phase01 * Mathf.Tau));

        float FlickerFactor() => _isCrt ? Flicker(_flickerPhase, FlickerDepth) : 1f;

        void ApplyLevels()
        {
            float k = _isCrt ? _warm : 1f;
            // The CRT's fade is now IN THE TEXTURE: albedo lerps up from black, so the picture itself resolves out of
            // a dark tube instead of a fully-drawn image being dimmed. On the flatscreen k is 1 and it snaps.
            float f = FlickerFactor();   // 1.0 on the flatscreen; a shallow NTSC breath on the tube
            if (_screenMat != null) _screenMat.AlbedoColor = ScreenColor(_emitEnergy * k * f);
            if (_light != null) _light.LightEnergy = _lightEnergy * k * f;
            // the shaft rides it too, so the picture, the spill and the beam pulse together instead of drifting apart
            if (_coneMat != null) _coneMat.AlbedoColor = new Color(0.85f, 0.9f, 1.0f, ConeAlpha * k * f);
        }

        public override void _Process(double delta)
        {
            // The CRT breathes at the NTSC field rate for as long as it is lit -- so this no longer early-outs on
            // !_warming, which it did back when warmup was the only thing that animated.
            if (_lit && _isCrt)
            {
                _flickerPhase = Mathf.Wrap(_flickerPhase + (float)delta * NtscFieldHz, 0f, 1f);
                ApplyLevels();
            }
            if (!_warming) return;
            if (_warmDelay > 0f) { _warmDelay -= (float)delta; if (_warmDelay > 0f) return; }   // dead time before the tube lights
            _warm = Mathf.Min(1f, _warm + (float)delta / WarmDur);
            if (_warm >= 1f) _warming = false;
            ApplyLevels();
        }

        /// <summary>Look-focus highlight (F affordance): the whole-prop white rim silhouette. Same shared-static
        /// trap as StoreShelf/ObjectDoor -- the outline shader tints from WorldItem.FocusColor, so CLAIM it white
        /// on GAIN; do NOT reset it on loss (that would smear white over an item that legitimately owns its rarity).</summary>
        public void SetLookFocused(bool on)
        {
            OutlineOverlay.ShowOutline(on, Colors.White, _outline);
        }

        // ---- render-harness / test seams ------------------------------------------------------------------------
        /// <summary>The screen material, built here rather than inline so the two properties the washout fix depends
        /// on are reachable without an Unturned install (the Television meshes ship with the game, so TVDevice.Make
        /// cannot run on a box without one -- and neither can a render).</summary>
        internal static StandardMaterial3D MakeScreenMaterial(Texture2D pattern) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = pattern,
            AlbedoColor = Colors.Black,                                       // starts dark; ApplyLevels raises it
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,          // SMPTE is a real image, not a palette texel
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,                  // ripped mesh: winding may face either way
        };

        /// <summary>Screen brightness -> AlbedoColor. Grey, so the SMPTE bars keep their own hues and only their
        /// level moves; black at 0 is what makes the CRT warmup a fade of the picture itself.</summary>
        internal static Color ScreenColor(float brightness) => new Color(brightness, brightness, brightness);

        /// <summary>A basis putting the beam's forward axis on <paramref name="normal"/>. SpotLight3D aims down local
        /// -Z (aimNegZ), BeamMesh runs down local -Y -- two different conventions for the same "point that way", so
        /// the caller says which it needs rather than one of them silently getting a sideways cone.</summary>
        internal static Basis AimBasis(Vector3 normal, bool aimNegZ)
        {
            Vector3 f = normal.Normalized();
            Vector3 axis = -f;                                                     // local +Z or +Y sits opposite the aim
            Vector3 seed = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
            Vector3 x = seed.Cross(axis).Normalized();
            Vector3 y = axis.Cross(x).Normalized();
            return aimNegZ ? new Basis(x, y, axis) : new Basis(x, axis, y);
        }

        /// <summary>The screen's two IN-PLANE half-extents, i.e. its size with the axis along the normal dropped.
        /// Feeds the beam's rectangular near end so the shaft starts the shape of the actual screen.</summary>
        internal static Vector3 ScreenHalfExtents(Aabb screenAabb, Vector3 normal)
        {
            Vector3 h = screenAabb.Size * 0.5f;
            Vector3 n = normal.Normalized().Abs();
            // drop the thinnest axis -- the one the normal points along -- and keep the other two
            if (n.X >= n.Y && n.X >= n.Z) return new Vector3(Mathf.Max(h.Z, 0.05f), Mathf.Max(h.Y, 0.05f), 0f);
            if (n.Y >= n.X && n.Y >= n.Z) return new Vector3(Mathf.Max(h.X, 0.05f), Mathf.Max(h.Z, 0.05f), 0f);
            return new Vector3(Mathf.Max(h.X, 0.05f), Mathf.Max(h.Y, 0.05f), 0f);
        }

        public bool DebugLit => _lit;      // last EFFECTIVE state actually applied -- survives a prop with no meshes
        public bool DebugBroken => _broken;
        public bool DebugScreenOk => _screen != null;
        /// <summary>Is the screen taking NO lighting? The washout fix depends on this being true, and it is the kind
        /// of property that a screenshot cannot distinguish from "the light happens to be dim right now".</summary>
        public bool DebugScreenUnshaded => _screenMat != null && _screenMat.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded;
        /// <summary>Screen brightness as actually applied (AlbedoColor). 0 = black tube, _emitEnergy = full picture.</summary>
        public float DebugScreenBrightness => _screenMat?.AlbedoColor.R ?? -1f;
        public bool DebugIsCrt => _isCrt;
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
