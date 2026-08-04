using Godot;

namespace UnturnedGodot
{
    // An in-game TELEVISION -- Television_0 (flatscreen) / Television_1 (CRT). Look at it and press F to toggle
    // it ON/OFF (per-TV state). When ON *and* the town grid is live (PowerNet.GlobalPower), the screen shows the
    // SMPTE test pattern UNSHADED (so no light can wash its colours out), a SpotLight spills forward down the
    // screen normal with a visible cone shaft, and tv_tone.wav loops quietly with a fast 3D falloff. Toggling on plays tv_on.wav once; toggling
    // off plays tv_off.wav once. When OFF or the grid is dead: screen dark, no light, no tone -- and if the grid
    // dies while it's on, it goes dark (the DayNightCycle power sweep calls Refresh, like the glow containers).
    //
    // The CRT (Television_1) WARMS UP: toggling it on does NOT snap the picture -- after a short dead delay the
    // picture FADES IN from black over ~1.5s (a tube heating up), and once lit it flickers. The flatscreen
    // (Television_0) snaps on instantly and holds steady, being an LCD.
    //
    // The SCREEN is the prop's darkest palette texel, carved off the body mesh by UV (ObjMesh.SplitByUv) and then
    // its one-texel UVs are REPLACED with a planar projection so the whole pattern fills the screen face. The
    // wiring mirrors proven props: the screen material + light + "tvdevices" group swept on a grid change
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
        bool _screenShot;         // GLASS shot out, cabinet still standing -- dead until the prop itself resets
        bool _lit;                // last EFFECTIVE state (_on && grid power) actually applied to the visuals

        MeshInstance3D _screen;   // the emissive SMPTE screen sub-mesh (hidden when dark)
        StandardMaterial3D _screenMat;
        SpotLight3D _light;       // forward spill, aimed down the screen normal (energy 0 / hidden when dark)
        MeshInstance3D _cone;     // the visible light shaft, StreetLight's beam reused
        StandardMaterial3D _coneMat;

        // Shaft reach / how much wider the far end is / overall softness. endScale KEEPS THE SCREEN'S ASPECT --
        // the beam is the screen's rectangle scaled up, not a rectangle rounding into a circle the way the
        // streetlight's does (master: "maintain a square shape"). The CRT face is 0.85 x 0.79, so its shaft is
        // very nearly square the whole way down, which is the point.
        const float ConeLen = 3.2f, ConeEndScale = 2.6f, ConeAlpha = 0.05f;

        // CRT flicker, 24 Hz (master's call, overriding the physically-real rate).
        // This is NOT the NTSC field rate and the constant is named so it cannot be mistaken for it. 59.94 Hz is the
        // true one, and it was the first attempt -- but sampled by a 60 Hz render it beats down to ~0.06 Hz, one slow
        // swell every ~17 seconds, which is what a camera filming a CRT records and is very nearly invisible in play.
        // 24 Hz sits below the 30 Hz Nyquist limit of a 60 fps render, so it is sampled honestly and actually reads as
        // flicker. Physically it is a film rate rather than a television one; the point here is the look, not the spec.
        // Only the CRT gets it; the flatscreen is an LCD and holds its pixels steady.
        const float FlickerHz = 24f, FlickerDepth = 0.18f;
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

        // CRT POWER-OFF COLLAPSE (master: "when turning it off, do the beam collapse on the center, the classic crt
        // turn off"). The raster loses its vertical deflection first, so the picture squeezes into a bright horizontal
        // line -- bright because the same beam energy is now painting a fraction of the area -- and then the horizontal
        // deflection goes and the line pulls into a dot that fades. Only the CRT does this; an LCD just stops.
        const float CollapseLine = 0.13f;    // picture -> line
        const float CollapseDot = 0.11f;     // line -> dot -> out
        const float CollapseFlash = 2.2f;    // peak level as the beam concentrates (blooms, which is the look)
        const float CollapseThin = 0.02f;    // the line's thickness as a fraction of screen height: one scanline
        float _collapse = -1f;               // seconds elapsed into the effect; negative = not running

        // The tube's OWN GLASS COLOUR (master: "instead of fading from 0,0,0 fade from the color of the screen on the
        // crt model itself"). The screen sub-mesh carries the prop's palette texel as a vertex colour, so this is read
        // off the model rather than hardcoded from a comment. The fallback is Television_1's texel, rgb 53,53,53.
        const float DefaultGlassLevel = 53f / 255f;
        float _glassLevel = DefaultGlassLevel;
        Vector3 _screenRightLocal = Vector3.Right, _screenUpLocal = Vector3.Up;   // the screen's OWN in-plane axes (Reproject's ax/ay)

        Vector3 _screenCenterLocal, _screenNormalLocal;   // stashed for the light placement + the render harness
        Aabb _screenAabbLocal;    // the screen sub-mesh's bounds in PROP-LOCAL space, captured before anything animates
                                  //  the node -- so a hit test never has to invert a collapsing (possibly degenerate) scale

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
            _screenAabbLocal = screenAabb;
            _light = new SpotLight3D
            {
                LightColor = new Color(0.85f, 0.9f, 1.0f),
                SpotRange = 4.0f, SpotAngle = 55f, SpotAngleAttenuation = 1.2f,
                LightEnergy = 0f, ShadowEnabled = false, Visible = false,
                Transform = new Transform3D(AimBasis(_screenNormalLocal), _screenCenterLocal + _screenNormalLocal * 0.05f),
            };
            AddChild(_light);

            // The visible SHAFT, reusing the streetlight's cone rather than a second implementation (master: "we
            // might wanna do the 'cone' effect we have on the streetlights for the tv effect"). BeamMesh is a better
            // fit here than it is on a lamp: its cross-section STARTS AS A RECTANGLE and rounds toward a circle with
            // depth, which is exactly what light leaving a rectangular screen does.
            // BeamMesh runs along -Y with the section in X/Z, so the node's -Y goes on the screen normal.
            var beam = BeamFrame(screenAabb, _screenNormalLocal);
            _cone = new MeshInstance3D
            {
                Mesh = StreetLight.BeamMesh(ConeLen, beam.HalfA, beam.HalfB, 0f, keepRect: true, endScale: ConeEndScale),
                Transform = new Transform3D(beam.Basis, _screenCenterLocal),
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.85f, 0.9f, 1.0f, ConeAlpha),
                    AlbedoTexture = ConeGradient(),
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
            // The screen's real in-plane axes, kept: the power-off collapse squeezes along ay (the picture falls to a
            // horizontal line) and then along ax. Deriving them a second time from the AABB would be guessing at what
            // is already known exactly here.
            _screenRightLocal = ax; _screenUpLocal = ay;
            // ...and the tube's own glass colour, straight off the model's palette texel rather than out of a comment.
            _glassLevel = C != null && C.Length > 0 ? Mathf.Clamp(C[0].R, 0f, 1f) : DefaultGlassLevel;

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
            // A shot-out set still has a working SWITCH -- you get the click, nothing comes on. Deliberately does not
            // flip _on: Refresh would keep it dark either way, but an armed _on switches the TV on by itself the moment
            // the prop resets and the screen comes back. That is the same silent-arming bug _broken hit above,
            // arriving through the other door, and it is just as invisible while the set is dark.
            if (_screenShot) { _offClick?.Play(); return; }
            _on = !_on;
            (_on ? _onClick : _offClick)?.Play();
            Refresh();
        }

        /// <summary>Collider meta carrying the device, so a look-ray or a bullet landing on a Television_0/1 body can
        /// find it. One key, two consumers: PlayerController's F-interact focus and its bullet routing.</summary>
        public static readonly StringName HitMeta = "tvdevice";

        /// <summary>Is this world point on the SCREEN rather than the cabinet? Same shape as StreetLight.IsBulbHit and
        /// for the same reason: the prop's collider is one trimesh over the whole set, so a shot at the glass arrives
        /// indistinguishable from a shot at the plastic, and the screen sub-mesh's own bounds are what separate them.
        ///
        /// Tested against the bounds CAPTURED AT BUILD in prop-local space, not against the live _screen node -- the
        /// CRT power-off collapses that node's scale toward zero, and inverting a degenerate transform mid-animation
        /// would make the hit test go haywire for exactly as long as the effect is playing.</summary>
        public bool IsScreenHit(Vector3 worldPoint)
            => _screen != null && PointOnScreen(_screenAabbLocal, GlobalTransform, worldPoint);

        /// <summary>The geometry half of <see cref="IsScreenHit"/>, pulled out as a pure function because the
        /// Television meshes ship with Unturned and there is no install on the build box -- so a test can never get a
        /// real TVDevice with a real screen, and this predicate would otherwise be unreachable by anything but a human
        /// with the game running.</summary>
        internal static bool PointOnScreen(Aabb screenAabbLocal, Transform3D propGlobal, Vector3 worldPoint)
        {
            if (screenAabbLocal.Size == Vector3.Zero) return false;
            var local = propGlobal.AffineInverse() * worldPoint;
            return screenAabbLocal.Grow(0.04f).HasPoint(local);   // bullets land ON the surface, i.e. exactly on the boundary
        }

        /// <summary>Shoot the screen out: one hit kills the glass, the spill and the shaft for good and leaves the
        /// cabinet standing (master: "make the tvs take 1 shot to destroy the visual screen +cone and a few to destroy
        /// the actual prop").
        ///
        /// Returns FALSE if the screen was already dead, so the caller lets that shot fall through to the cabinet's
        /// health rather than swallowing it -- otherwise the first bullet would make the set bulletproof, which is the
        /// opposite of what was asked for. Same contract as StreetLight.ShootOutBulb / TrafficLight.ShootOutLens.</summary>
        public bool ShootOutScreen()
        {
            if (_screenShot || _broken) return false;
            _screenShot = true;
            Refresh();
            return true;
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
            if (broken) _on = false;      // a rubble reset rebuilds the set switched OFF, not mid-programme
            else _screenShot = false;     // ...and rebuilds it WHOLE. A reset prop is a new television, not the old
                                          //  smashed one wearing a fresh cabinet, so the glass comes back with it.
            Refresh();
        }

        public void Refresh()
        {
            bool eff = _on && PowerNet.GlobalPower && !_broken && !_screenShot;
            if (eff == _lit) return;
            _lit = eff;
            if (eff)
            {
                EndCollapse();   // switched back on mid-collapse: the screen node is still squeezed, so undo it first
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
                _tone?.Stop();
                if (ShouldCollapse(_isCrt, _broken, _screenShot) && _screen != null) { _collapse = 0f; ApplyCollapse(); return; }
                EndCollapse();
            }
        }

        /// <summary>Who gets the graceful exit. A TUBE collapses; an LCD just stops, and a set whose glass is already
        /// gone -- smashed prop or shot-out screen -- does not get to play a power-off animation on the way out. Pure,
        /// because on a box with no Unturned install a bare TVDevice has no screen mesh, so the branch in Refresh that
        /// consults this can never be reached by a test and the POLICY would go unpinned.</summary>
        internal static bool ShouldCollapse(bool isCrt, bool broken, bool screenShot) => isCrt && !broken && !screenShot;

        /// <summary>Where the collapse is at <paramref name="t"/> seconds in: how much of the screen's height and width
        /// are left, and how bright it is. Pure, because everything interesting about this effect is the SHAPE of those
        /// three curves over time and none of it is observable on a box with no Unturned install.
        ///
        /// Vertical deflection fails first (picture -> line), then horizontal (line -> dot). Level RISES through the
        /// first phase: the beam is painting a fraction of the area with the same energy, which is why a dying CRT
        /// flashes rather than dimming. Returns Level 0 once it is over.</summary>
        internal static (float Vert, float Horiz, float Level) Collapse(float t)
        {
            if (t < 0f) return (1f, 1f, 1f);
            // Ease OUT, not in. Deflection does not decay gently -- it goes, and what you see is a fast squeeze that
            // settles into the line. The obvious ease-in (u*u) instead holds a nearly full-size picture for most of the
            // phase and then snaps, which reads as a lag followed by a glitch. Caught by asserting the shape at 42% of
            // the effect, where ease-in still left the picture 41% tall.
            static float Ease(float x) { float k = 1f - x; return 1f - k * k; }
            if (t < CollapseLine)
            {
                float u = t / CollapseLine;
                return (Mathf.Lerp(1f, CollapseThin, Ease(u)), 1f, Mathf.Lerp(1f, CollapseFlash, u));
            }
            float d = (t - CollapseLine) / CollapseDot;
            if (d >= 1f) return (0f, 0f, 0f);
            // The width goes fast and the BRIGHTNESS trails it linearly -- so what is left is a dot that lingers and
            // fades, which is the phosphor. Horizontal floors at CollapseThin rather than 0 so there is still a point
            // to see; the level is what actually puts it out.
            return (CollapseThin, Mathf.Lerp(1f, CollapseThin, Ease(d)), Mathf.Lerp(CollapseFlash, 0f, d));
        }

        internal static float CollapseDur => CollapseLine + CollapseDot;

        /// <summary>Squeeze the screen node about the screen's centre, along the screen's OWN axes. Not a plain
        /// Scale on the node: the mesh lives in prop-local space with its centre nowhere near the origin, so scaling
        /// the node directly would drag the picture across the cabinet instead of collapsing it in place.</summary>
        void ApplyCollapse()
        {
            if (_screen == null) return;
            var (vert, horiz, level) = Collapse(_collapse);
            var frame = new Basis(_screenRightLocal, _screenUpLocal, _screenNormalLocal);
            var squeeze = frame * Basis.FromScale(new Vector3(horiz, vert, 1f)) * frame.Transposed();
            _screen.Transform = new Transform3D(squeeze, _screenCenterLocal - squeeze * _screenCenterLocal);
            _screen.Visible = level > 0f;
            if (_screenMat != null) _screenMat.AlbedoColor = ScreenColor(_emitEnergy * level);
            // The spill and the shaft die WITH the picture rather than snapping off at the switch -- but they are not
            // squeezed. The collapse is the raster's, and a light cone narrowing to a blade would read as a bug.
            if (_light != null) { _light.LightEnergy = _lightEnergy * level; _light.Visible = level > 0f; }
            if (_coneMat != null) _coneMat.AlbedoColor = new Color(0.85f, 0.9f, 1.0f, ConeAlpha * level);
            if (_cone != null) _cone.Visible = level > 0f;
        }

        /// <summary>Stop the collapse and put the screen node back the way it was -- called both when it finishes and
        /// when the set is switched back on mid-effect. Leaving a squeezed transform behind would show up as a TV that
        /// turns on as a horizontal line and stays that way.</summary>
        void EndCollapse()
        {
            _collapse = -1f;
            if (_screen != null) { _screen.Transform = Transform3D.Identity; _screen.Visible = _lit; }
            if (_light != null) _light.Visible = _lit;
            if (_cone != null) _cone.Visible = _lit;
        }

        /// <summary>Brightness modulation, CRT only. A cosine BETWEEN TWO LIT LEVELS -- full and (1 - depth) -- never
        /// off (master: "when i say flicker i mean switch between a dimmer and brighter light state, not on/off").
        /// That is why it is 1 - depth*0.5*(1-cos) rather than anything that reaches zero: the floor is a real
        /// brightness, so the picture is continuously visible and only its level moves.</summary>
        internal static float Flicker(float phase01, float depth)
            => 1f - depth * 0.5f * (1f - Mathf.Cos(phase01 * Mathf.Tau));

        float FlickerFactor() => _isCrt ? Flicker(_flickerPhase, FlickerDepth) : 1f;

        /// <summary>CRT warmup level: the picture rises out of the tube's OWN GLASS, not out of a black hole (master:
        /// "instead of fading from 0,0,0 fade from the color of the screen on the crt model itself"). The screen
        /// sub-mesh is an overlay sitting on the cabinet's own screen face, so a fade that starts at 0 puts a rectangle
        /// DARKER THAN THE SET on the front of it for the first moment of every power-on -- the picture visibly dips
        /// below the surrounding plastic before it comes up.</summary>
        internal static float WarmLevel(float warm, float glass, float full) => Mathf.Lerp(glass, full, warm);

        void ApplyLevels()
        {
            float k = _isCrt ? _warm : 1f;
            // The CRT's fade is now IN THE TEXTURE: albedo lerps up from the glass colour, so the picture itself
            // resolves out of the tube instead of a fully-drawn image being dimmed. On the flatscreen k is 1 and it
            // snaps, so WarmLevel returns `full` and the LCD is untouched by any of this.
            float f = FlickerFactor();   // 1.0 on the flatscreen; a shallow breath on the tube
            float lvl = WarmLevel(k, _glassLevel, _emitEnergy);
            if (_screenMat != null) _screenMat.AlbedoColor = ScreenColor(lvl * f);
            if (_light != null) _light.LightEnergy = _lightEnergy * k * f;
            // the shaft rides it too, so the picture, the spill and the beam pulse together instead of drifting apart
            if (_coneMat != null) _coneMat.AlbedoColor = new Color(0.85f, 0.9f, 1.0f, ConeAlpha * k * f);
        }

        public override void _Process(double delta)
        {
            // Power-off collapse runs while the set is already NOT lit, so it has to come before everything else here
            // -- and it ends by calling EndCollapse, which is what actually hides the screen/light/cone.
            if (_collapse >= 0f)
            {
                _collapse += (float)delta;
                if (_collapse >= CollapseDur) EndCollapse();
                else ApplyCollapse();
                return;
            }
            // The CRT breathes at the NTSC field rate for as long as it is lit -- so this no longer early-outs on
            // !_warming, which it did back when warmup was the only thing that animated.
            if (_lit && _isCrt)
            {
                _flickerPhase = Mathf.Wrap(_flickerPhase + (float)delta * FlickerHz, 0f, 1f);
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

        /// <summary>The shaft's fade, BRIGHT AT THE SCREEN and gone by the far end (master: "gradient fade towards
        /// to bigger end too, brighter toward the source").
        ///
        /// This is the inverse of StreetLight.ConeGradient and has to be, for a reason that is not obvious from
        /// either file alone: BeamMesh maps `v = t * 0.5`, so v=0 sits at the SOURCE and v=0.5 at the far end, and
        /// the lamp's gradient rises with v -- faint at the lamp, dense at the ground. StreetLight documents that as
        /// a deliberate tuning it was built against, so it is not mine to flip; the TV just needs the opposite
        /// picture and gets its own texture rather than a shared one with a mode flag.
        ///
        /// Only v in [0, 0.5] is ever sampled (CylinderMesh reserves the top half of UV space for caps), so the
        /// falloff is packed into the FIRST half and the rest is left at zero. Writing the ramp across the full
        /// [0,1] would put the shaft's midpoint at the far end and half the fade off the end of the mesh.</summary>
        internal static ImageTexture ConeGradient()
        {
            const int n = 64;
            var img = Image.CreateEmpty(1, n, false, Image.Format.Rgba8);
            for (int y = 0; y < n; y++)
            {
                float v = (float)y / (n - 1);
                float k = Mathf.Clamp(1f - v / 0.5f, 0f, 1f);       // 1 at the screen -> 0 by the far end
                img.SetPixel(0, y, new Color(1f, 1f, 1f, Mathf.Pow(k, 1.7f)));
            }
            return ImageTexture.CreateFromImage(img);
        }

        /// <summary>A basis aiming a SpotLight3D (which points down its local -Z) along <paramref name="normal"/>.
        ///
        /// Roll is arbitrary here and that is FINE, because a spot cone is radially symmetric -- there is nothing on it
        /// for a roll to misalign. The beam MESH is the opposite case and must not use this; it gets its frame from the
        /// screen's own axes via <see cref="BeamFrame"/>. This used to serve both, with a flag, and that is precisely
        /// how the flatscreen shaft ended up rolled 90 degrees.</summary>
        internal static Basis AimBasis(Vector3 normal)
        {
            Vector3 axis = -normal.Normalized();                                   // local +Z sits opposite the aim
            Vector3 seed = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.95f ? Vector3.Right : Vector3.Up;
            Vector3 x = seed.Cross(axis).Normalized();
            Vector3 y = axis.Cross(x).Normalized();
            return new Basis(x, y, axis);
        }

        /// <summary>The beam's frame AND its near-ring size, derived together from the screen's OWN axes.
        ///
        /// These have to come from one place. AimBasis picked an arbitrary perpendicular for the beam's roll, so the
        /// rectangle landed at whatever rotation fell out of a cross product -- while the half-extents were chosen by
        /// a separate rule. Nothing tied the two together, so the beam's "width" axis and the screen's "width" axis
        /// agreed only by luck. On the CRT (0.85 x 0.79) that is invisible; on the flatscreen (3.55 x 1.8) it reads
        /// as the shaft rolled 90 degrees, which is exactly what master saw.
        ///
        /// Returns a basis whose -Y is the screen normal (BeamMesh runs down -Y) and whose X / Z are the screen's own
        /// in-plane axes, plus the half-extents IN THAT ORDER -- so halfA belongs to basis X and halfB to basis Z by
        /// construction rather than by coincidence. A 180-degree roll is not corrected because a rectangle is
        /// symmetric under it; only the 90 matters.</summary>
        internal static (Basis Basis, float HalfA, float HalfB) BeamFrame(Aabb screenAabb, Vector3 normal)
        {
            Vector3 h = screenAabb.Size * 0.5f, n = normal.Normalized(), an = n.Abs();
            Vector3 seed; float a, b;
            if (an.X >= an.Y && an.X >= an.Z) { seed = Vector3.Up;    a = h.Y; b = h.Z; }   // normal along X -> plane is YZ
            else if (an.Y >= an.Z)            { seed = Vector3.Right; a = h.X; b = h.Z; }   // normal along Y -> plane is XZ
            else                              { seed = Vector3.Right; a = h.X; b = h.Y; }   // normal along Z -> plane is XY
            Vector3 y = -n;                                        // beam runs down local -Y, so +Y is anti-normal
            Vector3 x = (seed - y * seed.Dot(y)).Normalized();     // the screen axis that owns `a`, made perpendicular
            Vector3 z = x.Cross(y).Normalized();                   // right-handed: x cross y = z
            return (new Basis(x, y, z), Mathf.Max(a, 0.05f), Mathf.Max(b, 0.05f));
        }

        public bool DebugLit => _lit;      // last EFFECTIVE state actually applied -- survives a prop with no meshes
        public bool DebugBroken => _broken;
        public bool DebugScreenShot => _screenShot;
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
