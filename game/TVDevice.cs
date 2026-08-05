using Godot;

namespace UnturnedGodot
{
    // An in-game SCREEN -- Television_0/1 (flatscreen / CRT television) and Computer_0/3 (CRT / flatscreen computer
    // monitor). Look at it and press F to toggle it ON/OFF (per-set state); every set starts ON (master). When ON
    // *and* it has power -- the town grid, OR its own wired plug once the mains are down -- the screen shows the
    // SMPTE test pattern UNSHADED (so no light can wash its colours out), a SpotLight spills forward down the
    // screen normal with a visible cone shaft, and tv_tone.wav loops quietly with a fast 3D falloff. Toggling on plays tv_on.wav once; toggling
    // off plays tv_off.wav once. When OFF or the grid is dead: screen dark, no light, no tone -- and if the grid
    // dies while it's on, it goes dark (the DayNightCycle power sweep calls Refresh, like the glow containers).
    //
    // A TUBE (Television_1, Computer_0) WARMS UP: toggling it on does NOT snap the picture -- after a short dead delay
    // the picture FADES IN out of the tube's own glass colour over ~1.5s, and once lit it flickers and collapses to a
    // line on the way out. A PANEL (Television_0, Computer_3) snaps on instantly and holds steady, being an LCD.
    // Independently of that, a TELEVISION shows the SMPTE card and hums; a MONITOR shows a flat colour that changes
    // every few seconds and tints its own spill and shaft, and is silent. See DeviceKind.
    //
    // The SCREEN is the prop's darkest palette texel, carved off the body mesh by UV (ObjMesh.SplitByUv) and then
    // its one-texel UVs are REPLACED with a planar projection so the whole pattern fills the screen face. The
    // wiring mirrors proven props: the screen material + light + "tvdevices" group swept on a grid change
    // come from StoreShelf's cooler/fridge interior glow; the bit-6 look collider + SetMeta("tvdevice", ...)
    // F-interact routing come from ObjectDoor/GasPump. Ripped meshes need CullMode.Disabled.
    public partial class TVDevice : Node3D, IPowerDevice
    {
        // ---- palette-derived screen texel predicates (GODOT uv space -- ObjMesh V-flips on load) ----------------
        // Both TV screens are the prop's darkest grey texel and both are authored FACE-UP (local +Y normal); the
        // level placement basis stands the prop upright so the screen faces the room. Verified against the .obj:
        //   CRT (Television_1): screen at u>0.5, v<0.5   (rgb 53,53,53; a 0.85x0.79 recessed face)
        //   Flatscreen (Television_0): the INSET front face at u<0.25, v>0.5 (rgb 39,39,39; 3.55x1.8, recessed in
        //   the bezel). The u<0.25, v<0.5 face is the FLAT BACK panel (rgb 56,56,56) -- not the screen.
        // The two COMPUTER MONITOR props reuse the CRT television's screen texel exactly -- Computer_0's screen is the
        // same 0.85 x 0.79 quad at the same (53,53,53) texel, and Computer_3's is 1.15 x 0.79 at the same one. So they
        // take CrtScreen verbatim rather than getting a predicate of their own. Written down because that is a fact
        // about the ART, not about this code, and re-extracted UVs could break it silently: the split falls back to a
        // printed error, but a predicate that matched the WRONG face would just render a picture on the wrong plane.
        static bool CrtScreen(Vector2 a, Vector2 b, Vector2 c)
            => a.X > 0.5f && b.X > 0.5f && c.X > 0.5f && a.Y < 0.5f && b.Y < 0.5f && c.Y < 0.5f;
        static bool FlatScreen(Vector2 a, Vector2 b, Vector2 c)
            => a.X < 0.25f && b.X < 0.25f && c.X < 0.25f && a.Y > 0.5f && b.Y > 0.5f && c.Y > 0.5f;

        // ---- what KIND of set this is ------------------------------------------------------------------------------
        // Four props, two independent axes (master: "dupe the CRT thing onto the computer crt, minus the test pattern,
        // and vertical hold desync, and test tone. computer monitors cycle through a few random colors (which tints the
        // 'cone'), same for the flatscreen computer monitor, minus the crt exclusive things"):
        //
        //            | TUBE (warms up, flickers, collapses) | PANEL (snaps on)
        //   TV       | Television_1                          | Television_0        <- SMPTE card, blanking bar, tone
        //   MONITOR  | Computer_0                            | Computer_3          <- flat colour on a cycle, silent
        //   LAPTOP    |                                       | Computer_2          <- a panel too; own label + draw
        //
        // ...plus ONE thing that belongs to a single cell rather than an axis: the vertical-hold slip is the television
        // tube's alone. A monitor's picture is generated locally and does not roll (and master asked for it gone).
        //
        // One enum with derived predicates, not four booleans threaded through Build/Refresh/_Process. The failure that
        // shape invites is a fifth prop picking up three of the four flags -- and the one it misses is always a QUIET
        // feature (no tone, no roll), so nothing looks wrong, it just isn't there.
        //
        // Every predicate below is an ALLOWLIST (`k is A or B`), never a denylist, and that is what makes adding a kind
        // safe: a new member is false everywhere by default, which for a screen means no card, no roll, no tone and a
        // colour cycle -- the quiet, correct answer. A denylist would hand it the television's whole feature set.
        public enum DeviceKind { CrtTv, FlatTv, CrtMonitor, FlatMonitor, Laptop }

        /// <summary>Which props this device drives at all. WorldBuilder gates on THIS, so <see cref="KindFor"/> is
        /// only ever asked about a name that appears here -- the two are a pair and a test pins that they agree.</summary>
        public static bool IsDeviceProp(string propName)
            => propName is "Television_0" or "Television_1" or "Computer_0" or "Computer_2" or "Computer_3";

        // Computer_2 is the LAPTOP, and it is worth saying how that was settled because guessing it from the bounding
        // box got it wrong once: at 0.76 x 0.61 x 0.67 it reads as a small case. What identifies it is the SHAPE by
        // height band -- a flat 0.76 x 0.56 deck from z 0.01-0.12, then a panel only ~0.05-0.09 thick in Y rising to
        // z 0.68. A deck plus a hinged lid. Computer_1 and Computer_4 are the towers (0.52 x 0.97 footprint, constant
        // top to bottom, with a 0.40 x 0.10 drive slot two thirds up), and the CRT predicate matches that slot -- ten
        // triangles across a 0.04 m slab, which is why the screen test asserts a single planar quad rather than a hit.
        public static DeviceKind KindFor(string propName) => propName switch
        {
            "Television_0" => DeviceKind.FlatTv,
            "Television_1" => DeviceKind.CrtTv,
            "Computer_0"   => DeviceKind.CrtMonitor,
            "Computer_2"   => DeviceKind.Laptop,
            "Computer_3"   => DeviceKind.FlatMonitor,
            _              => DeviceKind.CrtTv,
        };

        /// <summary>A tube: warms up rather than snapping, flickers while lit, and collapses to a line on the way out.
        /// Both televisions and monitors have a tube variant, which is the whole point of splitting this apart from the
        /// old single is-this-a-CRT flag: "CRT" used to mean tube AND television AND rolls AND hums, all at once.</summary>
        internal static bool IsTube(DeviceKind k) => k is DeviceKind.CrtTv or DeviceKind.CrtMonitor;
        /// <summary>Shows the SMPTE test card (and therefore the blanking bar baked under it). Televisions only.</summary>
        internal static bool HasPattern(DeviceKind k) => k is DeviceKind.CrtTv or DeviceKind.FlatTv;
        /// <summary>Loses vertical hold. The television TUBE only -- not the monitor tube.</summary>
        internal static bool HasDesync(DeviceKind k) => k == DeviceKind.CrtTv;
        /// <summary>Hums the 1 kHz test tone. Tied to the test card because they are the same fiction: a set showing
        /// bars is showing a test broadcast, and a test broadcast is what the tone belongs to.</summary>
        internal static bool HasTone(DeviceKind k) => HasPattern(k);
        /// <summary>Shows a flat colour that changes every few seconds instead of a picture. Monitors.</summary>
        internal static bool CyclesColour(DeviceKind k) => !HasPattern(k);

        // ---- WHAT THE SCREEN IS SHOWING ------------------------------------------------------------------------------
        // Master: terminals with a blinking cursor, scrolling block text, bar graphs with a scientific animation, the
        // random colour, a bouncing DVD blob on the flatscreens, static fuzz on any television -- "and anything without
        // the test pattern does not have the 1khz test tone".
        //
        // That last clause is why a PROGRAM is a thing and not just a picture. The tone used to hang off the device
        // KIND (HasTone == HasPattern), which was fine while a television could only ever show the test card. Now that
        // it might be showing snow or a screensaver, sound and picture have to be chosen together or they drift: the
        // failure is a television humming a 1 kHz test tone over a DVD screensaver, which sounds like a bug in the
        // audio system rather than in the thing that picked the picture.
        //
        // So one enum decides both, and Sound() is derived from it rather than stored alongside it.
        public enum ScreenProgram { TestCard, Static, Dvd, Colour, TerminalCursor, TerminalScroll, BarGraph }

        public enum ScreenSound { None, Tone, Noise }

        /// <summary>The tone belongs to the TEST CARD, because they are the same fiction: bars on screen are a test
        /// broadcast, and 1 kHz is what a test broadcast carries. Snow gets hiss. Everything else is silent.</summary>
        internal static ScreenSound SoundFor(ScreenProgram p) => p switch
        {
            ScreenProgram.TestCard => ScreenSound.Tone,
            ScreenProgram.Static   => ScreenSound.Noise,
            _                      => ScreenSound.None,
        };

        /// <summary>What each kind of set can be showing. A television is broadcast equipment: a test card, snow, or --
        /// if it is a flatscreen, i.e. modern enough to be plugged into something -- a DVD screensaver. A computer
        /// screen is a computer screen.</summary>
        internal static ScreenProgram[] ProgramsFor(DeviceKind k) => k switch
        {
            DeviceKind.CrtTv  => new[] { ScreenProgram.TestCard, ScreenProgram.Static },
            DeviceKind.FlatTv => new[] { ScreenProgram.TestCard, ScreenProgram.Static, ScreenProgram.Dvd },
            _                 => new[] { ScreenProgram.Colour, ScreenProgram.TerminalCursor,
                                         ScreenProgram.TerminalScroll, ScreenProgram.BarGraph },
        };


        /// <summary>The vertical-hold slip is a property of a broadcast picture on a tube, so it needs BOTH: a CRT
        /// television (HasDesync) AND a program that is actually a received broadcast. A DVD blob does not roll.</summary>
        internal static bool CanRoll(DeviceKind k, ScreenProgram p)
            => HasDesync(k) && (p == ScreenProgram.TestCard || p == ScreenProgram.Static);

        // ---- CONE INTENSITY FROM WHAT IS ON SCREEN (master: "change the intensity of the cone to represent the
        // average brightness of the colors on screen"). The picture is generated on the GPU, so the CPU cannot read
        // it back -- these are MEASURED means, taken by evaluating each program over six seconds and averaging its
        // Rec.709 luma, the same way the prop dimensions were measured rather than guessed:
        //
        //   TestCard 0.406 (straight off smpte_pattern.png)   Static 0.460   BarGraph 0.282
        //   TerminalScroll 0.138   Dvd 0.070   TerminalCursor 0.0005 (a black terminal really is black)
        //
        // Normalised against the TEST CARD, because the cone's existing brightness was dialled in on it -- so a set
        // showing the card looks exactly as it did, and everything else is relative to that.
        internal const float RefLuma = 0.4057f;
        /// <summary>Mean screen luma per program. `tint` matters only for the flat-colour program, whose brightness is
        /// literally the colour it is showing -- so its cone dims and brightens as it cycles, for free.</summary>
        internal static float MeanLuma(ScreenProgram p, Color tint) => p switch
        {
            ScreenProgram.TestCard       => 0.4057f,
            ScreenProgram.Static         => 0.4597f,   // re-measured after the snow was coarsened
            ScreenProgram.Dvd            => 0.0704f,
            ScreenProgram.BarGraph       => 0.2821f,
            // 0.1384 measured, then adjusted for the deeper red: red lines are 9% of lines, their ink luma fell
            // 0.451 -> 0.228, and lit coverage on this program is ~0.165 -- so 0.09*0.165*0.223 comes off the mean.
            // Small enough that the ordering test's margins are untouched; carried anyway so the number stays honest.
            ScreenProgram.TerminalScroll => 0.1351f,
            ScreenProgram.TerminalCursor => 0.0005f,
            _                            => Mono(tint).R,   // Colour: the tint IS the picture
        };

        /// <summary>How hard this set throws light, relative to a test card. Floored rather than allowed to reach zero:
        /// a black terminal emits almost nothing and SHOULD barely light the room, but a spill that goes exactly to
        /// zero reads as the cone being broken rather than as the screen being dark.</summary>
        const float ConeFloor = 0.06f;
        internal static float ConeScale(ScreenProgram p, Color tint)
            => Mathf.Max(ConeFloor, MeanLuma(p, tint) / RefLuma);

        // ---- WHERE THE LIGHT COMES FROM (master: "track the scale/motion on screen? ie the light beam comes from the
        // dvd logo as it moves across the dark screen, the blinking cursor is the source of that cone").
        //
        // Mean brightness alone was the crude version of this: it made a screen showing one small bright thing into a
        // DIM light at the centre, when physically it is a SMALL light where that thing is. So the spill is placed at
        // the picture's luminance centroid and the shaft narrowed to the size of the lit region.
        //
        // Centre is in screen-plane units, (right, up), each in [-0.5, 0.5] of the screen's own width and height.
        // Extent is the lit region as a fraction of the screen, which is what narrows the beam.
        //
        // The DVD blob's position is NOT recomputed here from time -- it is the same value the shader is drawing with,
        // handed to both. Two independent copies of a bounce agree exactly until one is edited, and the symptom then is
        // a light tracking a blob that is not there, which looks like a lighting bug rather than a duplication one.
        internal static (Vector2 Centre, Vector2 Extent) Emitter(ScreenProgram p, Vector2 blobUv, Vector2 blobHalf) => p switch
        {
            // A blob at UV b sits at (b.x - 0.5) right and (0.5 - b.y) up: UV v runs DOWN the screen (Reproject puts
            // image top at screen top), so the vertical term is flipped, and getting that backwards would send the
            // light to the blob's mirror image -- which still tracks, still moves, and is wrong.
            ScreenProgram.Dvd            => (new Vector2(blobUv.X - 0.5f, 0.5f - blobUv.Y), blobHalf * 2f),
            // The cursor lives two cells in and two lines down of a 40x20 grid: top-left, and tiny.
            ScreenProgram.TerminalCursor => (new Vector2(0.05f - 0.5f, 0.5f - 0.10f), new Vector2(0.05f, 0.08f)),
            // Text runs from the left edge to a ragged right, full height.
            ScreenProgram.TerminalScroll => (new Vector2(-0.15f, 0f), new Vector2(0.70f, 1f)),
            // Bars rise from the bottom across the full width, so the lit mass sits low.
            ScreenProgram.BarGraph       => (new Vector2(0f, -0.22f), new Vector2(1f, 0.62f)),
            // A test card, snow and a flat colour genuinely do fill the screen.
            _                            => (Vector2.Zero, Vector2.One),
        };

        /// <summary>The bouncing logo's half-size in UV, aspect-corrected so it stays SQUARE in the world. Screen UV
        /// is 0..1 across a face that is 3.55 x 1.80 m on the big flatscreen, so equal UV steps are nothing like equal
        /// distances -- an uncorrected square logo comes out as a wide smear on exactly the set it is most visible on.
        /// Shared with the shader as a uniform rather than written twice.</summary>
        internal static Vector2 BlobHalf(float aspect) => new(0.18f / Mathf.Max(0.05f, aspect), 0.18f);

        /// <summary>The DVD blob's centre at time t. Triangle waves ARE a perfect reflection off the walls, so the
        /// corners fall out of the maths rather than needing a collision test. Pure so the emitter position and the
        /// drawn blob can be checked against each other.</summary>
        internal static Vector2 BlobPos(float t, float seed, Vector2 half)
        {
            var span = Vector2.One - half * 2f;
            float tx = Mathf.Abs(Mathf.PosMod(t * 0.13f + seed, 1f) * 2f - 1f);
            float ty = Mathf.Abs(Mathf.PosMod(t * 0.097f + seed * 1.7f, 1f) * 2f - 1f);
            return half + new Vector2(tx, ty) * span;
        }

        const float BeamMinScale = 0.18f;   // a cursor's beam is a pencil, but not a zero-width one
        float _screenHalfW = 0.5f, _screenHalfH = 0.5f;   // the screen's own in-plane half-extents, set by Reproject
        Vector2 _blob = new(0.5f, 0.5f);
        Vector2 _blobHalf = new(0.12f, 0.18f);
        Vector3 _coneBaseScale = Vector3.One;
        Basis _beamBasis = Basis.Identity;
        float _lightBaseAngle = 55f;

        // ---- TERMINAL SCROLL BURSTS (master: "make it scroll in bursts of random durations and time between. with a
        // blinking cursor sometimes"). A burst has memory, so the CPU owns the scroll position and the shader just
        // draws it -- a pure function of time could fake this but not readably.
        const float BurstMin = 0.35f, BurstMax = 2.6f;    // seconds of typing
        const float IdleMin = 0.5f, IdleMax = 3.4f;       // seconds parked, cursor blinking
        const float TypeSpeed = 19f;                      // cells per second while typing
        const float ScrollCols = 34f;                     // must match the shader's grid, and is the ONLY thing that
                                                          //  does -- C# deliberately does not know line LENGTHS. The
                                                          //  shader clips each line at its own hashed length and
                                                          //  clamps the cursor to it, so there is no second copy of
                                                          //  the layout to drift out of step with the first.
        float _headLine, _headCol, _burstLeft, _idleLeft;

        ScreenProgram _program = ScreenProgram.TestCard;
        float _seed;          // per-set, so a room of televisions is not in lockstep
        float _clock;         // seconds since build, driven into the shader
        bool _monoTube;       // a CRT television that happens to be a monochrome set (master: "either monochrome or color")

        internal static string LabelFor(DeviceKind k) => k switch
        {
            DeviceKind.Laptop => "Laptop",
            _ => HasPattern(k) ? "Television" : "Computer Monitor",
        };

        public string PropName = "Television_1";
        DeviceKind _kind = DeviceKind.CrtTv;
        bool _on;                 // player toggle state (independent of the grid)
        bool _broken;             // prop smashed -> screen + light + tone stay dead through any grid sweep
        bool _screenShot;         // GLASS shot out, cabinet still standing -- dead until the prop itself resets
        bool _lit;                // last EFFECTIVE state (_on && grid power) actually applied to the visuals

        MeshInstance3D _screen;   // the emissive SMPTE screen sub-mesh (hidden when dark)
        ShaderMaterial _screenMat;   // one shader, one program per set -- see ScreenProgram
        SpotLight3D _light;       // forward spill, aimed down the screen normal (energy 0 / hidden when dark)
        MeshInstance3D _cone;     // the visible light shaft, StreetLight's beam reused
        StandardMaterial3D _coneMat;

        // Shaft reach / how much wider the far end is / overall softness. endScale KEEPS THE SCREEN'S ASPECT --
        // the beam is the screen's rectangle scaled up, not a rectangle rounding into a circle the way the
        // streetlight's does (master: "maintain a square shape"). The CRT face is 0.85 x 0.79, so its shaft is
        // very nearly square the whole way down, which is the point.
        const float ConeLen = 3.2f, ConeEndScale = 2.6f, ConeAlpha = 0.05f;

        /// <summary>The visible light SHAFT (strawberry 2026-08-05: "the light cones from tvs are completely messed up.
        /// remove em for now"). One flag rather than deleting the code, because "for now" is what was asked -- the
        /// beam frame, the emitter tracking and the brightness curve all still work and are still exercised by the
        /// suite; only the mesh is withheld. Flip this to restore it.
        ///
        /// The SPILL LIGHT is deliberately NOT gated by this: it is what actually lights the room, and a television
        /// that throws no light at all is a different change from one that has no visible shaft.</summary>
        const bool ShowShaft = false;

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
        // Doubled from 0.3 / 1.5 (strawberry: "double the time it takes for the tub to warm up when turning on crts of
        // any type"). BOTH terms, not just the ramp: the dead delay is part of what you wait through, so doubling only
        // WarmDur would have made the total 3.3 s against 1.8 -- not double, just longer.
        const float WarmDelay = 0.6f, WarmDur = 3.0f;
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
        // crt model itself"), sampled from the prop's PALETTE TEXTURE at the screen face's own UV.
        //
        // It used to read the screen sub-mesh's vertex colour, which was wrong in the way that looks right: the
        // Television .obj files carry no baked vertex colours at all (384 `v` lines, none with rgb), and ObjMesh fills
        // that case with Colors.WHITE. So the "colour read off the model" was 1.0, and the blanking bar rendered as a
        // white band across the picture -- which is what master reported. The guard was on an EMPTY colour array, a
        // case that never happens, rather than on a white one, which always does.
        //
        // The texture is where the colour actually lives, and it is 2x2 / 4x2, so sampling it is free. Verified
        // against the real files: Television_1 screen texel rgb 53,53,53, Television_0 rgb 39,39,39.
        static readonly Color CrtGlass = new(53f / 255f, 53f / 255f, 53f / 255f);
        static readonly Color FlatGlass = new(39f / 255f, 39f / 255f, 39f / 255f);
        Color _glassColor = CrtGlass;
        const float PictureOffset = 0.004f;   // metres the picture floats off the cabinet's own screen face
        Vector3 _screenOffset;                // the above along the screen normal, folded into every _screen transform
        ImageTexture _patternTex, _monoTex;   // composite (picture + blanking bar), in colour and desaturated

        // BLANKING BAR (master: "with the vertical scroll, should have a small blank screen color horizontal zone
        // between the 'two' test pattern displays"). Baked into the texture rather than drawn as a second quad: the
        // roll is a UV offset, so a bar living in UV space scrolls with the picture for free and can never drift out
        // of step with it. Uv1Scale windows the material onto the picture, so at rest the bar is off-screen entirely.
        const float BlankFrac = 0.13f;   // master: "a little wider"

        // CRT VERTICAL HOLD slipping (master: "add a small chance every x seconds to do a vertical de-sync scroll, for
        // x ticks, before correcting itself"). The picture rolls through the frame and the hold then CATCHES, snapping
        // it back -- so the end of the effect is a jump, not a glide. Direction is signed because a slipped hold rolls
        // whichever way the field rate is off, and always rolling the same way looks scripted after the second time.
        const float DesyncMeanGap = 45f;                     // average seconds between slips, per set
        const float DesyncMin = 0.45f, DesyncMax = 1.30f;    // ~22-65 ticks at 50 Hz
        const float DesyncSpeedMin = 0.8f, DesyncSpeedMax = 2.4f;   // screens per second
        float _desyncLeft, _desyncSpeed, _desyncOffset;
        Vector3 _screenRightLocal = Vector3.Right, _screenUpLocal = Vector3.Up;   // the screen's OWN in-plane axes (Reproject's ax/ay)

        Vector3 _screenCenterLocal, _screenNormalLocal;   // stashed for the light placement + the render harness
        Vector3 _localUp = Vector3.Up;                    // world up expressed in the prop's local frame (Reproject computes it)
        Aabb _screenAabbLocal;    // the screen sub-mesh's bounds in PROP-LOCAL space, captured before anything animates
                                  //  the node -- so a hit test never has to invert a collapsing (possibly degenerate) scale

        // ---- MONITOR COLOUR CYCLE ----------------------------------------------------------------------------------
        // Master: "computer monitors cycle through a few random colors (which tints the 'cone')". A short PALETTE
        // rather than a random hue each time, because a computer screen is showing something -- a desktop, a terminal,
        // a crash -- and uniformly random hues read as a disco light rather than as a machine left running.
        //
        // The colour is not a texture. A monitor's screen material has no AlbedoTexture at all, so the colour rides
        // AlbedoColor directly, which is also what makes the warmup crossfade and the collapse work on a monitor for
        // free: both already drive that same colour's brightness and alpha.
        static readonly Color[] MonitorColours =
        {
            new(0.13f, 0.30f, 0.75f),   // desktop blue
            new(0.22f, 0.72f, 0.28f),   // phosphor green terminal
            new(0.85f, 0.62f, 0.15f),   // amber terminal
            new(0.62f, 0.66f, 0.70f),   // grey UI
            new(0.10f, 0.55f, 0.62f),   // teal
        };
        const float ColourHoldMin = 2.5f, ColourHoldMax = 7f;
        static readonly Color SpillWhite = new(0.85f, 0.9f, 1.0f);   // the televisions' fixed blue-white spill
        int _colourIdx;
        float _colourLeft;
        bool _mono;               // desaturate the picture (the power-off collapse); a texture swap on a TV, a luma on a monitor
        Color _tint = Colors.White;   // what the screen -- and therefore the spill and the shaft -- is coloured by

        /// <summary>The colour the screen, the spill and the shaft all take. White on a television (the SMPTE texture
        /// carries its own colours and albedo MULTIPLIES, so anything else would tint the bars); the current cycle
        /// colour on a monitor.</summary>
        // The flat-colour program carries its colour in the tint; every other program draws its own colours in the
        // shader and must be multiplied by WHITE or it would be recoloured. Desaturation is the shader's `mono`
        // uniform now, so Picture no longer applies it -- doing both would desaturate twice.
        Color Picture => _program == ScreenProgram.Colour ? _tint : Colors.White;
        Color Spill => _program == ScreenProgram.Colour ? ((_mono || _monoTube) ? Mono(_tint) : _tint) : SpillWhite;

        // ---- STATUS LEDS ---------------------------------------------------------------------------------------------
        // Master: "make the little green LED emissive" on the monitors, and on the flatscreen "the red little LED
        // emissive when the TV has power, but is off, and the green one lights up when its on".
        //
        // These are REAL GEOMETRY, already in the props as their own palette texels -- not something drawn on. Measured
        // off the .objs, with the palette colour each texel actually carries:
        //
        //   Television_0 (4x2 palette)  THREE 5 cm cubes at the bezel's bottom-left:
        //                                 red   (174,46,46)  texel(1,0) -> standby
        //                                 green (63,128,61)  texel(2,0) -> on
        //                                 blue  (50,112,147) texel(3,1) -> unused, left dark
        //   Computer_3   (2x2)          one 8 cm green cube (63,119,52) at texel(1,1) -> on
        //   Computer_0   (2x2)          same size, same position, but the texel is bluish-grey (114,124,133). Lit
        //                                 green anyway: it is LED-shaped and in the LED spot, and an indicator that
        //                                 stays grey while the tube is warm reads as a dead prop, not as a design.
        //   Computer_2   (2x1)          NO indicator geometry at all -- body and screen, nothing else. Gets none.
        //   Television_1 (2x2)          one grey (104,104,104) cube, not a red/green pair -- so no standby light,
        //                                 which is why the request only asked for one on the flatscreen.
        //
        // The LED texels sit at u>0.5 on the monitors, the SAME half as CrtScreen -- they are separated by V, so a
        // predicate that only tested U would carve the screen and the LED into one mesh and light the picture.
        static bool TvOnLed(Vector2 a, Vector2 b, Vector2 c)
            => InCell(a, b, c, 0.50f, 0.75f, 0f, 0.5f);
        static bool TvStandbyLed(Vector2 a, Vector2 b, Vector2 c)
            => InCell(a, b, c, 0.25f, 0.50f, 0f, 0.5f);
        static bool MonitorLed(Vector2 a, Vector2 b, Vector2 c)
            => InCell(a, b, c, 0.50f, 1.01f, 0.5f, 1.01f);
        static bool InCell(Vector2 a, Vector2 b, Vector2 c, float u0, float u1, float v0, float v1)
            => In1(a, u0, u1, v0, v1) && In1(b, u0, u1, v0, v1) && In1(c, u0, u1, v0, v1);
        static bool In1(Vector2 t, float u0, float u1, float v0, float v1)
            => t.X >= u0 && t.X < u1 && t.Y >= v0 && t.Y < v1;

        static readonly Color LedGreen = new(0.30f, 0.95f, 0.35f);
        static readonly Color LedRed = new(0.95f, 0.20f, 0.16f);
        const float LedEmission = 3.0f;   // same scale StreetLight/TrafficLight use, so an LED blooms like a lens

        /// <summary>Which indicator cubes this kind has, as UV predicates. Null = the prop has no such cube.</summary>
        internal static (System.Func<Vector2, Vector2, Vector2, bool> On, System.Func<Vector2, Vector2, Vector2, bool> Standby) LedsFor(DeviceKind k)
            => k switch
            {
                DeviceKind.FlatTv      => (TvOnLed, TvStandbyLed),   // the only prop with two SEPARATE cubes
                // A monitor has ONE cube, so it is BOTH lamps -- green lit when on, red when powered and off
                // (strawberry: "make the LEDs on CRT and flatscreen computer monitors light up red if they have power
                // but are off"). Two emissive copies of the same geometry, shown exclusively: LedState never returns
                // both, so they cannot z-fight, and this needs no new geometry or a colour-swapping material.
                DeviceKind.CrtMonitor  => (MonitorLed, MonitorLed),
                DeviceKind.FlatMonitor => (MonitorLed, MonitorLed),
                _                      => (null, null),              // CRT television: grey cube. Laptop: nothing at all.
            };

        MeshInstance3D _ledOn, _ledStandby;
        StandardMaterial3D _ledOnMat, _ledStandbyMat;

        // ---- POWER IO ----------------------------------------------------------------------------------------------
        // Master: "add power io for both TVs, computer crt and computer flat screen monitor". ONE wire-able CONSUMER
        // port per set -- the plug.
        //
        // Deliberately NOT the input/output pair LightTap gives a streetlight. That one exposes an output because a
        // wrecked lamp is a tap into a municipal main that is still live behind it; a television is an appliance, there
        // is nothing behind it to tap, and an Output port here would mean wiring a TV into a generator and getting
        // power back out of the television.
        //
        // The plug is an ALTERNATIVE feed, not a replacement: a set still runs off the town mains exactly as before,
        // and the port is what keeps it running once the mains are down. That is the entire reason to have one.
        public const float CrtTvWatts = 90f, FlatTvWatts = 60f, CrtMonitorWatts = 70f, FlatMonitorWatts = 25f;
        /// <summary>A laptop is a whole computer, not just a panel, so it draws more than the bare monitor it otherwise
        /// behaves identically to -- and less than anything with a tube in it.</summary>
        public const float LaptopWatts = 45f;
        internal static float WattsFor(DeviceKind k) => k switch
        {
            DeviceKind.FlatTv      => FlatTvWatts,
            DeviceKind.CrtMonitor  => CrtMonitorWatts,
            DeviceKind.FlatMonitor => FlatMonitorWatts,
            DeviceKind.Laptop      => LaptopWatts,
            _                      => CrtTvWatts,
        };

        readonly System.Collections.Generic.List<ConnectionPort> _ports = new();
        ConnectionPort _plug;
        Aabb _bodyAabbLocal;      // the cabinet's own bounds, kept so a rubble reset can rebuild the plug where it was
        bool _plugWasPowered;     // last polled state, so a wire going live re-derives the set without a per-frame Refresh

        // IPowerDevice: a pure consumer -- it never produces, and a map fixture never burns.
        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => 0;   // SP-local map fixture (an MP replica id would go here, like GasPump / LightTap)
        public System.Collections.Generic.IReadOnlyList<ConnectionPort> PowerPorts => _ports;

        /// <summary>Is the plug actually receiving its wattage? Separate from the mains: a set is live on EITHER.</summary>
        public bool PlugPowered => _plug != null && GodotObject.IsInstanceValid(_plug) && _plug.Powered;

        static float EnvF(string k, float d) => float.TryParse(System.Environment.GetEnvironmentVariable(k), out var v) ? v : d;

        /// <summary>Build a TV device for a placed prop. <paramref name="bodyMi"/> is the prop's body
        /// MeshInstance3D (its Mesh is split for the screen, its Transform is copied so the screen sub-mesh --
        /// carved in the body's own local space -- lines up exactly). Add the returned node to the SAME parent
        /// the body was added to.</summary>
        public static TVDevice Make(MeshInstance3D bodyMi, string propName)
        {
            // ON AT START (master: "making all tvs/monitors on at start"). Set BEFORE Build, because Build ends with
            // the first Refresh -- so a tube plays its warmup as the map comes up rather than snapping to a lit
            // picture, which is what a room full of sets left running should look like.
            var kind = KindFor(propName);
            var pool = ProgramsFor(kind);
            var tv = new TVDevice
            {
                PropName = propName, _kind = kind, _on = true, Transform = bodyMi.Transform,
                _program = pool[Mathf.Abs((int)GD.Randi()) % pool.Length],
                _seed = GD.Randf() * 100f,
                // A CRT television is randomly a monochrome set (master: "the CRT tvs can be either monochrome or
                // color"). Only the TUBE televisions -- a colour LCD is not a period-plausible black-and-white set,
                // and a monitor's programs are chosen for their colour.
                _monoTube = kind == DeviceKind.CrtTv && GD.Randf() < 0.35f,
            };
            tv.Build(bodyMi.Mesh as ArrayMesh);
            return tv;
        }

        /// <summary>Join the power graph. Deferred out of <see cref="Make"/> because Make runs BEFORE the device is in
        /// the tree (the world builder parents it afterwards), and the lazy PowerManager spawn needs a SceneTree.</summary>
        public override void _Ready()
        {
            AddToGroup("deployables");   // PowerNet gathers this group by IPowerDevice, not by the concrete Deployable
            if (GetTree() is SceneTree tr && tr.GetNodesInGroup("powermgr").Count == 0)
            {
                // A child of THIS device, added synchronously. It used to be a DEFERRED add to the parent, copying
                // LightTap/GasPump -- but those spawn it from Attach(), where the add happens immediately. Deferring it
                // breaks the "does one already exist" check above: the group is not populated until the deferred call
                // runs, so EVERY device on the map sees zero and spawns its own. One per television is a leak that only
                // shows up as accumulated nodes at the end of a long run.
                //
                // Parenting it to the device also gives it a lifetime: it dies with the set instead of outliving a
                // freed parent, which is what a deferred add into a torn-down sandbox does.
                var pm = new PowerManager();
                AddChild(pm);
                pm.AddToGroup("powermgr");   // AFTER entering the tree, so the next device's check sees it at once
            }
            PowerNet.MarkDirty();
        }

        void Build(ArrayMesh body)
        {
            if (body == null) { GD.PrintErr($"[tv] {PropName}: no body mesh"); return; }
            // Split the screen texel off the body. Bucket[0] = matched (screen) tris; Build() returns null for an
            // empty bucket, so a null bucket means the predicate matched nothing (wrong prop / re-extracted UVs).
            var screenMesh = SplitScreen(body, _kind);
            if (screenMesh == null) { GD.PrintErr($"[tv] {PropName}: screen split matched no triangles"); return; }

            // ALWAYS loaded, whatever program this set draws. The SMPTE image and its composite are shared statics --
            // one texture and one composite per glass colour for the whole map -- so wiring them costs nothing, and the
            // alternative is a device built showing snow that can never be switched to the test card because its
            // texture slot is empty. The shader samples this only for program 0.
            var pattern = LoadPattern();
            // BEFORE Reproject, which overwrites the UVs: the screen's original UV is a single palette texel, and that
            // texel IS the tube's glass colour. Read it now or it is gone.
            _glassColor = SampleScreenTexel(PropName, screenMesh, _kind == DeviceKind.FlatTv ? FlatGlass : CrtGlass);
            var projected = Reproject(screenMesh);   // one-texel UVs -> planar 0..1 fill

            // UNSHADED (master: "make the light they emit not reflect on the screen, bc its washing out the colors").
            // The screen used to be a normal lit material carrying BOTH albedo and emission, with the TV's own
            // OmniLight sitting 0.3m in front of it -- so every TV was lighting its own screen, and the diffuse term
            // pushed the SMPTE bars toward white. A screen is a light SOURCE, not a lit surface, so it should take no
            // lighting at all. Unshaded also makes the fix total rather than a tuning exercise: no light in the world
            // can wash it out, not the TV's own spill, not the sun through a window, not a torch in your hand.
            //
            // Unshaded outputs ALBEDO directly and ignores the emission slot, so brightness rides AlbedoColor instead
            // of EmissionEnergyMultiplier. Values above 1 still bloom, same trick the tracer and muzzle-flash
            // materials use.
            //
            // The warmup is an ALPHA CROSSFADE onto the cabinet's own screen face, not a brightness ramp (master:
            // "should fade from the tv model screen color into the image"). Brightness cannot do this: albedo
            // MULTIPLIES the texture, so scaling it down gives a dim SMPTE pattern, never a flat colour -- the whole
            // picture is simply there from the first frame, at 20% brightness, which is what "the fade got nuked"
            // looked like. Fading the picture's alpha in over the model's own screen texel is the real thing, and it
            // fades from the actual model colour rather than from a number copied out of a comment.
            //
            // Which is also why the picture is nudged 4mm off the cabinet face: it has to be strictly IN FRONT of what
            // it dissolves out of. SplitByUv copies triangles rather than removing them, so the body still draws that
            // face and the two were exactly coincident -- fine while the overlay was opaque, z-fighting the moment it
            // is not. And the body face carries the destructible's hide-on-break for free, so there is no second
            // screen-shaped node to leave hanging over the rubble.
            //
            // A MONITOR takes none of that texture machinery: no card, so no composite, no blanking bar and no
            // Uv1Scale window. Its picture IS the albedo colour, which is why the crossfade and the collapse work on it
            // unchanged -- both already drive that colour's brightness and alpha.
            var (patternTex, monoTex, patternFrac) = ScreenTextures(pattern, _glassColor);
            _patternTex = patternTex; _monoTex = monoTex;
            float aspect = _screenHalfH > 1e-5f ? _screenHalfW / _screenHalfH : 1f;
            _blobHalf = BlobHalf(aspect);
            _screenMat = MakeScreenMaterial(_patternTex, _program, patternFrac, _seed);
            // Transparent for the blob (a missing logo should draw NOTHING, not a white brick) and black for
            // the panel backdrop (a missing map should add no light, not a haze).
            _screenMat.SetShaderParameter("blob_tex", LoadPngOr(BlobAsset, new Color(0f, 0f, 0f, 0f)));
            _screenMat.SetShaderParameter("bg_tex", LoadPngOr(PanelAsset, Colors.Black));
            _screenMat.SetShaderParameter("blob_half", _blobHalf);
            _blob = BlobPos(0f, _seed, _blobHalf);
            _screenMat.SetShaderParameter("blob_pos", _blob);
            if (_program == ScreenProgram.Colour)
                _tint = MonitorColours[_colourIdx = Mathf.Abs((int)GD.Randi()) % MonitorColours.Length];
            _screenOffset = _screenNormalLocal * PictureOffset;
            SetMono(false);   // establishes the uniform; _monoTube keeps it lit for a black-and-white set
            _screen = new MeshInstance3D { Mesh = projected, MaterialOverride = _screenMat, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = _screenOffset };
            AddChild(_screen);

            // DIRECTIONAL spill (master: "the light should also be directional"). An OmniLight threw light backwards
            // through the cabinet and sideways into the wall the set is against; a TV only lights what is in front of
            // it. SpotLight3D aims down its own local -Z, so the node is basised to put -Z on the screen normal.
            var screenAabb = screenMesh.GetAabb();
            _screenAabbLocal = screenAabb;
            _light = new SpotLight3D
            {
                LightColor = Spill,
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
            _beamBasis = beam.Basis;
            if (ShowShaft)
            {
                _cone = new MeshInstance3D
                {
                    Mesh = StreetLight.BeamMesh(ConeLen, beam.HalfA, beam.HalfB, 0f, keepRect: true, endScale: ConeEndScale),
                    Transform = new Transform3D(beam.Basis, _screenCenterLocal),
                    Visible = false,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride = new StandardMaterial3D
                    {
                        // Master: the monitor's cycling colour "tints the 'cone'". So the shaft is not a fixed blue-white
                        // here -- it takes the screen's colour, and so does the SpotLight above, because a screen showing
                        // green spilling blue-white light onto the wall is the tell that the two are unrelated systems.
                        AlbedoColor = new Color(Spill, ConeAlpha),
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
            }

            BuildLeds(body);

            _bodyAabbLocal = body.GetAabb();
            BuildPlug(_bodyAabbLocal);   // the wire-able power input (see POWER IO above)

            // Whole-prop look-focus outline (F affordance -- tells the player F does something): the FULL body
            // silhouette on the outline overlay, hidden until looked at. Same recipe as StoreShelf._shelfGlow /
            // ObjectDoor._leafOutline.
            _outline = OutlineOverlay.MakeOutline(body);
            AddChild(_outline);

            BuildAudio();
            AddToGroup("tvdevices");   // DayNightCycle.DriveStreetlights sweeps this group on a grid change -> Refresh
            Refresh();
        }

        /// <summary>Carve the screen face off a body mesh for a given kind. Shared with the test rather than the test
        /// re-deriving one -- the point of checking a UV predicate is to check THE one production runs, and a copy that
        /// agrees with itself would pass with the real predicate broken.
        ///
        /// Keyed on the KIND, not on a two-value flag: the split cache is per (mesh, key) and three of the four props
        /// share one predicate, so a key that only distinguished CRT from flat would be fine today and collide the
        /// moment two kinds on one mesh want different splits.</summary>
        internal static ArrayMesh SplitScreen(ArrayMesh body, DeviceKind kind)
        {
            var parts = ObjMesh.SplitByUv(body, 70 + (int)kind, kind == DeviceKind.FlatTv ? FlatScreen : CrtScreen);
            return parts != null && parts.Length >= 1 ? parts[0] : null;
        }

        // ---- status LEDs -------------------------------------------------------------------------------------------
        void BuildLeds(ArrayMesh body)
        {
            var (on, standby) = LedsFor(_kind);
            _ledOn = MakeLed(body, on, LedGreen, 90, out _ledOnMat);
            _ledStandby = MakeLed(body, standby, LedRed, 91, out _ledStandbyMat);
        }

        /// <summary>Carve one indicator cube off the body and hang an emissive copy over it.
        ///
        /// A COPY, slightly inflated, rather than a recolour of the body face: SplitByUv copies triangles instead of
        /// removing them, so the prop still draws its own dark cube underneath. That is what an unlit LED should look
        /// like -- and it means the lit state is purely additive, so nothing has to be undone when the set goes dark.
        /// The 3% inflation about the cube's own centre is what keeps the two from z-fighting; the screen solves the
        /// same problem with a 4 mm offset, but an offset on a cube would slide it out of the bezel.</summary>
        MeshInstance3D MakeLed(ArrayMesh body, System.Func<Vector2, Vector2, Vector2, bool> pred, Color col, int key,
                               out StandardMaterial3D mat)
        {
            mat = null;
            if (pred == null) return null;
            var parts = ObjMesh.SplitByUv(body, key, pred);
            var mesh = parts != null && parts.Length >= 1 ? parts[0] : null;
            if (mesh == null) { GD.PrintErr($"[tv] {PropName}: LED split matched no triangles"); return null; }

            mat = new StandardMaterial3D
            {
                AlbedoColor = col,
                EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 0f,   // Refresh lights it
                Metallic = 0f, Roughness = 0.4f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // ripped mesh: winding may face either way
            };
            var c = mesh.GetAabb().GetCenter();
            var mi = new MeshInstance3D
            {
                Mesh = mesh, MaterialOverride = mat, Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Transform = new Transform3D(Basis.FromScale(Vector3.One * 1.03f), c - Basis.FromScale(Vector3.One * 1.03f) * c),
            };
            AddChild(mi);
            return mi;
        }

        /// <summary>Which indicator is lit. Deliberately a three-state read rather than "on/off": a set with no mains
        /// AND no wire shows NOTHING, because an unpowered television does not glow red -- standby is a thing a
        /// powered set does. That distinction is the entire point of the red lamp, and it is invisible unless the
        /// blackout case is handled separately from the switched-off case.</summary>
        internal static (bool On, bool Standby) LedState(bool lit, bool hasFeed, bool broken, bool screenShot)
        {
            if (broken) return (false, false);                       // rubble does not indicate anything
            if (lit) return (true, false);
            return (false, hasFeed && !screenShot);                  // powered, not showing a picture -> standby
        }

        void ApplyLeds()
        {
            var (on, standby) = LedState(_lit, HasFeed, _broken, _screenShot);
            if (_ledOn != null) { _ledOn.Visible = on; if (_ledOnMat != null) _ledOnMat.EmissionEnergyMultiplier = on ? LedEmission : 0f; }
            if (_ledStandby != null) { _ledStandby.Visible = standby; if (_ledStandbyMat != null) _ledStandbyMat.EmissionEnergyMultiplier = standby ? LedEmission : 0f; }
        }

        // ---- power plug --------------------------------------------------------------------------------------------
        void BuildPlug(Aabb bodyLocal)
        {
            if (_plug != null) return;
            _plug = ConnectionPort.Create(this, new DeployableDef.Port
            {
                Kind = DeployableDef.PortKind.Consumer,
                Pos = PlugLocal(bodyLocal, _screenNormalLocal, _localUp),
                Watts = WattsFor(_kind),
            }, LabelFor(_kind));
            AddChild(_plug);
            _ports.Add(_plug);
            PowerNet.MarkDirty();
        }

        void FreePlug()
        {
            if (_plug == null) return;
            _ports.Remove(_plug); _plug.QueueFree(); _plug = null;
            _plugWasPowered = false;
            PowerNet.MarkDirty();
        }

        /// <summary>Where the plug cube hangs, in the prop's own local frame: off the BACK of the cabinet (the far side
        /// from the screen), a quarter of the way up it.
        ///
        /// DERIVED from the body's bounds rather than four hand-measured constants, because the four props are not the
        /// same shape -- a 1.15 m desk monitor and a 3.55 m wall television -- and one shared vector would sit inside
        /// the big one and out in mid-air past the small one. Deriving it also means it cannot silently go stale if a
        /// prop is re-extracted, which matters here more than usual: a badly placed port is invisible until someone
        /// walks up with the wire tool and finds nothing to aim at, and nothing about the set looks wrong before that.
        ///
        /// Pure and internal so the placement can be checked against the real prop bounds without an Unturned install
        /// -- see <see cref="PointOnScreen"/> for the same argument.</summary>
        internal static Vector3 PlugLocal(Aabb bodyLocal, Vector3 screenNormalLocal, Vector3 localUp)
        {
            var n = screenNormalLocal.LengthSquared() > 1e-9f ? screenNormalLocal.Normalized() : Vector3.Forward;
            var c = bodyLocal.GetCenter();
            // Step from the centre out to the back face along the screen normal, then 6 cm clear of it so the cube sits
            // ON the cabinet rather than buried in it (the same failure GridPowerSource.PortLocal records at Y=0.60).
            float reach = (bodyLocal.Size * 0.5f).Dot(n.Abs()) + 0.06f;
            var p = c - n * reach;

            // ...then drop it to a quarter height, measured along whatever local axis the placement basis stands up.
            // Not simply bodyLocal's Y: these props are authored Z-up and the basis rotates them, so the local "up"
            // axis differs per prop and hardcoding one puts the plug out of the side of a television.
            //
            // ORTHOGONALIZED against the screen normal first, and that is the load-bearing line. The height slide runs
            // along `u`; if `u` has any component along `n`, the slide undoes part of the step out of the back face and
            // the port ends up INSIDE the cabinet -- invisible, unclickable, and with nothing about the set looking
            // wrong. A tilted wall set (screen angled down, so its local up is not perpendicular to its own normal) is
            // enough to trigger it. If the two are parallel outright -- a screen facing straight up, i.e. a set lying
            // on its back -- there is no height axis at all and the plug just stays at mid-height rather than being
            // slid along a degenerate direction.
            var u = localUp - n * localUp.Dot(n);
            if (u.LengthSquared() < 1e-6f) return p;
            u = u.Normalized();
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                float d = bodyLocal.GetEndpoint(i).Dot(u);
                lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d);
            }
            return p + u * (Mathf.Lerp(lo, hi, 0.25f) - p.Dot(u));
        }

        // Reproject the screen sub-mesh's one-texel UVs into a planar 0..1 fill, so the pattern covers the whole
        // face oriented upright (image top -> screen top = world up) and un-mirrored when viewed from the front.
        ArrayMesh Reproject(ArrayMesh screen)
        {
            var a0 = screen.SurfaceGetArrays(0);
            var V = a0[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var N = a0[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var C = a0[(int)Mesh.ArrayType.Color].AsColorArray();

            Vector3 c = Vector3.Zero; foreach (var v in V) c += v; c /= Mathf.Max(1, V.Length);
            // WHICH WAY THE SCREEN FACES -- and this is NEGATED winding, which needs saying because it looks like a
            // sign error.
            //
            // ObjMesh.Load reverses every face's vertex order on import ("Unity(LH) verts in Godot(RH) face inward with
            // the orig order -> reverse so faces point OUT"). After that reversal the summed cross product points INTO
            // the cabinet, uniformly: measured on all five screen props it is -Y local, every one. So the outward
            // facing is its negation, and that is a property of the LOADER rather than of any one prop.
            //
            // This USED to be signed outward a different way -- flip the winding whenever it pointed toward the body's
            // AABB centre. That rule is right for a deep cabinet and quietly wrong otherwise, because it assumes the
            // screen is the part of the prop furthest from its own centre of mass:
            //
            //   Television_0/1, Computer_0   screen 62-92% of the way to the front face -> flipped, correct, and the
            //                                negation here reproduces that answer exactly. NO-OP for all three, which
            //                                is what makes this change safe for the props that were eyeballed in game.
            //   Computer_3 (flat monitor)    screen 1 cm BEHIND the AABB centre, because the stand runs back past the
            //                                panel -> not flipped -> the picture, spill and cone faced out of its back
            //   Computer_2 (laptop)          screen at the far END of the prop, with the whole keyboard deck on the
            //                                side it looks at -> not flipped -> the lid rendered onto its own back
            //
            // The laptop is the case that shows the old rule was never sound rather than merely mistuned: a screen that
            // faces back ACROSS its own body cannot be found by pointing away from the centre of mass, and no
            // refinement of that idea can find it.
            Vector3 nrm = Vector3.Zero;
            for (int i = 0; i + 2 < V.Length; i += 3) nrm += (V[i + 1] - V[i]).Cross(V[i + 2] - V[i]);
            if (nrm.LengthSquared() < 1e-9f) nrm = Vector3.Down;   // negated below -> Up, the old degenerate fallback
            nrm = -nrm.Normalized();
            _screenCenterLocal = c; _screenNormalLocal = nrm;

            // screen "up" = WORLD up projected into the screen plane, expressed in the prop's LOCAL frame (the UVs
            // are baked here in local space, then the placement basis rotates them) -- so after placement the
            // pattern's up lands on world up for ANY prop orientation. Orthonormalize drops any placement scale.
            Vector3 localUp = Transform.Basis.Orthonormalized().Inverse() * Vector3.Up;
            _localUp = localUp;   // kept: the plug's height is measured along it too, and re-deriving it there would
                                  //  be a second copy of the same reasoning that could drift from this one
            Vector3 ay = localUp - nrm * localUp.Dot(nrm);
            if (ay.LengthSquared() < 1e-5f)   // screen faces straight up/down in WORLD (never for a placed TV) -> stable fallback
                ay = Mathf.Abs(nrm.Z) < 0.9f ? new Vector3(0, 0, 1) - nrm * nrm.Z : new Vector3(1, 0, 0) - nrm * nrm.X;
            ay = ay.Normalized();
            Vector3 ax = ay.Cross(nrm).Normalized();   // viewer-right from the +normal side -> increasing U reads left-to-right, un-mirrored
            // The screen's real in-plane axes, kept: the power-off collapse squeezes along ay (the picture falls to a
            // horizontal line) and then along ax. Deriving them a second time from the AABB would be guessing at what
            // is already known exactly here.
            _screenRightLocal = ax; _screenUpLocal = ay;

            float umin = 1e9f, umax = -1e9f, vmin = 1e9f, vmax = -1e9f;
            var pu = new float[V.Length]; var pv = new float[V.Length];
            for (int i = 0; i < V.Length; i++)
            {
                Vector3 d = V[i] - c; pu[i] = d.Dot(ax); pv[i] = d.Dot(ay);
                umin = Mathf.Min(umin, pu[i]); umax = Mathf.Max(umax, pu[i]);
                vmin = Mathf.Min(vmin, pv[i]); vmax = Mathf.Max(vmax, pv[i]);
            }
            float uw = Mathf.Max(1e-5f, umax - umin), vw = Mathf.Max(1e-5f, vmax - vmin);
            _screenHalfW = uw * 0.5f; _screenHalfH = vw * 0.5f;   // the emitter offset is measured in these
            var newU = new Vector2[V.Length];
            for (int i = 0; i < V.Length; i++)
                newU[i] = new Vector2((pu[i] - umin) / uw, (vmax - pv[i]) / vw);   // V flipped: image top (v=0) at the screen top

            var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = V; arr[(int)Mesh.ArrayType.Normal] = N; arr[(int)Mesh.ArrayType.TexUV] = newU; arr[(int)Mesh.ArrayType.Color] = C;
            var m = new ArrayMesh(); m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
            return m;
        }

        /// <summary>The picture as the material actually samples it: the SMPTE pattern with a strip of the tube's own
        /// glass colour appended below it, in colour and desaturated, plus the fraction of the composite the picture
        /// occupies (which is what Uv1Scale windows onto).
        ///
        /// The strip is the vertical blanking interval. Baking it into the texture is what makes the roll correct for
        /// free: the slip is a UV offset, so the bar scrolls with the picture by construction and cannot drift out of
        /// step with it the way a separately-animated quad would. At rest the window excludes it entirely, so a
        /// television that never slips looks exactly as it did.
        ///
        /// Cached per (glass colour, mono) rather than per device -- every CRT in the map shares one pair, and the map
        /// has a lot of televisions.</summary>
        static readonly System.Collections.Generic.Dictionary<(ulong, int), (ImageTexture Colour, ImageTexture Mono, float Frac)> _composites = new();
        internal static (ImageTexture Colour, ImageTexture Mono, float Frac) ScreenTextures(Texture2D pattern, Color glass)
        {
            if (pattern == null) return (null, null, 1f);
            // Keyed on the PATTERN as well as the glass. Production only ever has one pattern, so keying on colour
            // alone was harmless there and wrong anyway -- a cache whose key omits an input it is derived from. The
            // composite suite caught it immediately by asking for a synthetic pattern after the real one had been
            // built at the same glass colour, and getting the real one back. Order-dependent test failures are the
            // cheap version of this; the expensive version is a second pattern shipping one day and never rendering.
            var key = (pattern.GetInstanceId(),
                       (Mathf.RoundToInt(Mathf.Clamp(glass.R, 0f, 1f) * 255f) << 16)
                     | (Mathf.RoundToInt(Mathf.Clamp(glass.G, 0f, 1f) * 255f) << 8)
                     | Mathf.RoundToInt(Mathf.Clamp(glass.B, 0f, 1f) * 255f));
            if (_composites.TryGetValue(key, out var hit)) return hit;

            var src = pattern.GetImage();
            int w = src.GetWidth(), h = src.GetHeight();
            int bar = Mathf.Max(1, Mathf.RoundToInt(h * BlankFrac / (1f - BlankFrac)));
            int H = h + bar;

            var col = Image.CreateEmpty(w, H, false, Image.Format.Rgba8);
            var mono = Image.CreateEmpty(w, H, false, Image.Format.Rgba8);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var p = src.GetPixel(x, y);
                    col.SetPixel(x, y, p);
                    mono.SetPixel(x, y, Mono(p));
                }
            for (int y = h; y < H; y++)
                for (int x = 0; x < w; x++) { col.SetPixel(x, y, glass); mono.SetPixel(x, y, glass); }

            var made = (ImageTexture.CreateFromImage(col), ImageTexture.CreateFromImage(mono), (float)h / H);
            _composites[key] = made;
            return made;
        }

        /// <summary>Desaturate to Rec. 709 luma, NOT a flat channel average: the SMPTE bars are chosen as
        /// equal-LUMINANCE-ish steps, and averaging RGB collapses several of them onto the same grey -- the whole point
        /// of going monochrome on the way out is that you can still read the bars. The monitor palette needs it just as
        /// much: a flat average sends pure blue and pure green to the same value, so a collapsing monitor would lose
        /// which colour it had been showing a frame earlier.
        ///
        /// One definition, used by both paths, because a television that desaturates by one rule and a monitor by
        /// another is a difference nobody would ever see reported and nobody could ever explain.</summary>
        internal static Color Mono(Color c)
        {
            float l = c.R * 0.2126f + c.G * 0.7152f + c.B * 0.0722f;
            return new Color(l, l, l, c.A);
        }

        /// <summary>The prop's palette colour under the SCREEN triangles, sampled from its own texture at the screen
        /// face's UV centroid. Falls back to <paramref name="fallback"/> if the texture is missing or unreadable.
        ///
        /// The centroid is safe precisely because the screen is ONE palette texel -- that is the premise SplitByUv
        /// matches on -- so every screen UV lands in the same cell and averaging them cannot stray into a neighbour.
        /// ObjMesh V-flips on load, so the stored UVs are already Godot-space and index the image directly.</summary>
        internal static Color SampleScreenTexel(string propName, ArrayMesh screen, Color fallback)
        {
            try
            {
                var uv = screen?.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                if (uv == null || uv.Length == 0) return fallback;
                Vector2 c = Vector2.Zero;
                foreach (var t in uv) c += t;
                c /= uv.Length;

                string p = ProjectSettings.GlobalizePath($"res://content/objects/{propName}_tex.png");
                if (!System.IO.File.Exists(p)) return fallback;
                var img = new Image();
                if (img.Load(p) != Error.Ok) return fallback;
                int w = img.GetWidth(), h = img.GetHeight();
                if (w <= 0 || h <= 0) return fallback;
                int x = Mathf.Clamp(Mathf.FloorToInt(c.X * w), 0, w - 1);
                int y = Mathf.Clamp(Mathf.FloorToInt(c.Y * h), 0, h - 1);
                var px = img.GetPixel(x, y);
                return new Color(px.R, px.G, px.B);
            }
            catch { return fallback; }
        }

        // Repurposed game assets (master: "see if u can repurpose existing assets in the game"). Loaded raw via
        // Image.Load rather than GD.Load, the same as the SMPTE card -- these are loose pngs, not imported resources.
        static readonly System.Collections.Generic.Dictionary<string, ImageTexture> _pngCache = new();
        internal static ImageTexture LoadPng(string resPath)
        {
            if (_pngCache.TryGetValue(resPath, out var hit)) return hit;
            var img = new Image();
            string p = ProjectSettings.GlobalizePath(resPath);
            if (!System.IO.File.Exists(p) || img.Load(p) != Error.Ok) { GD.PrintErr($"[tv] {resPath} missing/failed"); _pngCache[resPath] = null; return null; }
            var t = ImageTexture.CreateFromImage(img);
            _pngCache[resPath] = t;
            return t;
        }

        /// <summary>A 1x1 texture of one colour, cached. Exists because of how Godot treats an UNSET sampler2D: it
        /// samples as OPAQUE WHITE, not as nothing. So handing a null texture to a shader parameter does not leave that
        /// program un-drawn -- it floods it with white.
        ///
        /// The failure that produces is different in each program and none of them looks like a missing file: the DVD
        /// blob becomes a plain white rectangle (which is exactly what it used to be, so it reads as correct), the bar
        /// panel gains a white haze that reads as a design choice, and the TEST CARD renders as a solid white screen
        /// instead of SMPTE bars. Every sampler therefore gets a real texture, always.
        ///
        /// Found because cow tools hit the same trap in the scope lens shader -- an unripped reticle left that sampler
        /// unset and painted a white lens over a live render, which is what made a working scope look blank.</summary>
        static readonly System.Collections.Generic.Dictionary<uint, ImageTexture> _solidCache = new();
        internal static ImageTexture Solid1x1(Color c)
        {
            uint key = ((uint)Mathf.RoundToInt(c.R * 255) << 24) | ((uint)Mathf.RoundToInt(c.G * 255) << 16)
                     | ((uint)Mathf.RoundToInt(c.B * 255) << 8) | (uint)Mathf.RoundToInt(c.A * 255);
            if (_solidCache.TryGetValue(key, out var hit)) return hit;
            var img = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            img.SetPixel(0, 0, c);
            var t = ImageTexture.CreateFromImage(img);
            _solidCache[key] = t;
            return t;
        }

        /// <summary>Load a png, or a 1x1 of <paramref name="fallback"/> if it is missing. NEVER null -- see Solid1x1.</summary>
        internal static ImageTexture LoadPngOr(string resPath, Color fallback)
            => LoadPng(resPath) ?? Solid1x1(fallback);
        internal const string BlobAsset = "res://content/menu/icon_sdglogo.png";
        internal const string PanelAsset = "res://content/menu/mappreview_pei.png";

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
            BuildLoopSound();
            var on = PlayerController.LoadWavOneShot("res://content/sounds/tv_on.wav");
            if (on != null) { _onClick = new AudioStreamPlayer3D { Stream = on, VolumeDb = Mathf.LinearToDb(0.7f), UnitSize = 3f, MaxDistance = 16f, Position = _screenCenterLocal }; AddChild(_onClick); }
            var off = PlayerController.LoadWavOneShot("res://content/sounds/tv_off.wav");
            if (off != null) { _offClick = new AudioStreamPlayer3D { Stream = off, VolumeDb = Mathf.LinearToDb(0.7f), UnitSize = 3f, MaxDistance = 16f, Position = _screenCenterLocal }; AddChild(_offClick); }
        }

        /// <summary>The looping sound belongs to the PROGRAM, not the device (master: "anything without the test pattern
        /// does not have the 1khz test tone"). A test card hums 1 kHz, snow hisses, everything else is silent -- and the
        /// silent case builds NO player at all rather than a muted one, so there is nothing for a later Play() to wake
        /// up and hand a DVD screensaver a broadcast tone.</summary>
        string _loopPath = "";   // which wav this set actually loaded -- see DebugLoopSound

        void BuildLoopSound()
        {
            _loopPath = "";
            string loop = SoundFor(_program) switch
            {
                ScreenSound.Tone  => "res://content/sounds/tv_tone.wav",
                ScreenSound.Noise => "res://content/sounds/tv_static.wav",
                _                 => null,
            };
            var tone = loop == null ? null : PlayerController.LoadWavOneShot(loop, loop: true);
            if (tone == null) return;
            _loopPath = loop;
            bool noise = SoundFor(_program) == ScreenSound.Noise;
            _tone = new AudioStreamPlayer3D
            {
                Stream = tone,
                VolumeDb = Mathf.LinearToDb(noise ? 0.30f : 0.45f),
                // Snow carries HALF the distance of the tone. At a shared 12 m a hiss reached two rooms away, so it
                // seemed to come from whichever set you happened to be facing -- which is how a monitor that owns no
                // audio node at all gets reported as "playing static sfx". A broadband hiss should also fall off
                // faster than a pure tone, so this is the physical answer as well as the legible one.
                UnitSize = noise ? 1.2f : 2f,
                MaxDistance = noise ? 6f : 12f,
                Position = _screenCenterLocal,
            };
            AddChild(_tone);
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
            if (broken)
            {
                _on = false;
                FreePlug();               // smashed: nothing to plug into, and a live wire hanging off rubble that is
                                          //  still drawing its 90 W is worse than merely wrong -- it is a load you
                                          //  cannot see, on a generator you can.
            }
            else
            {
                _screenShot = false;      // a reset prop is a NEW set, not the old smashed one wearing a fresh
                                          //  cabinet, so the glass comes back with it...
                _on = true;               // ...and so does the switch: master's "all tvs/monitors on at start" is
                                          //  about the state of the world, and rubble resetting into a dead set would
                                          //  make the map quietly go dark one prop at a time as things got shot.
                if (_screen != null) BuildPlug(_bodyAabbLocal);   // ...and its plug. Guarded on a built device: a bare
                                                                  //  TVDevice (no Unturned install) has no bounds to
                                                                  //  place a port against.
            }
            Refresh();
        }

        /// <summary>Does this set have power at all? The town mains OR its own wired plug -- either alone is enough,
        /// which is the whole point of the plug: a blackout kills every set on the grid and leaves the one you wired to
        /// your generator running.</summary>
        public bool HasFeed => PowerNet.GlobalPower || PlugPowered;

        public void Refresh()
        {
            bool eff = _on && HasFeed && !_broken && !_screenShot;
            _plugWasPowered = PlugPowered;   // the poll in _Process compares against this; stamping it here stops a
                                             //  Refresh from any other cause looking like a plug edge on the next frame
            // BEFORE the early-out, and that is not a detail: the standby lamp tracks HasFeed, which moves in cases
            // where the picture does not. A set switched off in a blackout and then re-powered goes dark-red-dark with
            // `eff` false the whole way, so an ApplyLeds below the early-out would simply never run for it.
            ApplyLeds();
            if (eff == _lit) return;
            _lit = eff;
            ApplyLeds();   // again, now that _lit has moved: the first call above ran on the OLD state
            if (eff)
            {
                EndCollapse();   // switched back on mid-collapse: the screen node is still squeezed, so undo it first
                if (IsTube(_kind)) { _warming = true; _warmDelay = WarmDelay; _warm = 0f; }   // tube warms in
                else { _warming = false; _warm = 1f; }                                        // panel snaps
                if (_program == ScreenProgram.Colour) _colourLeft = (float)GD.RandRange(ColourHoldMin, ColourHoldMax);
                if (_screen != null) _screen.Visible = true;
                if (_light != null) _light.Visible = true;
                if (_cone != null) _cone.Visible = true;
                _tone?.Play();
                ApplyTint();
            }
            else
            {
                _warming = false; _warm = 0f;
                _tone?.Stop();
                ResetDesync();
                if (ShouldCollapse(_kind, _broken, _screenShot) && _screen != null) { StartCollapse(); return; }
                EndCollapse();
            }
        }

        /// <summary>Who gets the graceful exit. A TUBE collapses -- and that now means the computer CRT as well as the
        /// television one (master: "dupe the CRT thing onto the computer crt"); an LCD just stops, and a set whose glass
        /// is already gone -- smashed prop or shot-out screen -- does not get to play a power-off animation on the way
        /// out. Pure, because on a box with no Unturned install a bare TVDevice has no screen mesh, so the branch in
        /// Refresh that consults this can never be reached by a test and the POLICY would go unpinned.</summary>
        internal static bool ShouldCollapse(DeviceKind kind, bool broken, bool screenShot)
            => IsTube(kind) && !broken && !screenShot;

        // ---- vertical hold ----------------------------------------------------------------------------------------
        /// <summary>Chance of a slip THIS FRAME, given the frame time and the average gap between slips. Expressed as
        /// a rate rather than a flat per-frame roll on purpose: a flat chance makes the effect happen twice as often
        /// on a 120 fps machine as on a 60 fps one, and the only symptom is "the TVs seem worse on my PC".</summary>
        internal static float DesyncChance(float dt, float meanGap)
            => meanGap <= 0f ? 0f : Mathf.Clamp(dt / meanGap, 0f, 1f);

        /// <summary>Only a lit set that HAS a vertical hold loses it, and only when it is not already slipping. The
        /// parameter is "does this kind roll", not "is this a tube" -- the computer CRT is a tube and does not roll
        /// (master), so tying this to IsTube would have quietly given it the effect back.</summary>
        internal static bool DesyncCanFire(bool hasDesync, bool lit, float running) => hasDesync && lit && running <= 0f;

        /// <summary>Advance a slip: returns the seconds left and the new V offset. When the clock runs out the hold
        /// CATCHES -- offset snaps to exactly 0 (master: "before correcting itself"), which is what a vertical hold
        /// locking actually does; easing it back would read as the picture drifting home.
        ///
        /// The offset is wrapped into [0,1) rather than left to accumulate. An unbounded offset works fine for a while
        /// and then quietly loses precision after a long enough session, which is the sort of thing that shows up as
        /// "the roll gets choppy on servers that have been up for days" and is never traced back to here.</summary>
        internal static (float Left, float Offset) DesyncStep(float left, float offset, float speed, float dt)
        {
            float next = left - dt;
            if (next <= 0f) return (0f, 0f);
            return (next, Mathf.PosMod(offset + speed * dt, 1f));
        }

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
            _screen.Transform = new Transform3D(squeeze, _screenOffset + _screenCenterLocal - squeeze * _screenCenterLocal);
            _screen.Visible = level > 0f;
            // FULLY OPAQUE on the way out, unlike the warmup. The collapse is the picture being crushed into a line,
            // not dissolving back into the tube -- a semi-transparent line would read as a fade playing at the same
            // time and blunt the whole effect.
            if (_screenMat != null) _screenMat.SetShaderParameter("tint", ScreenColor(Picture, _emitEnergy * level, 1f));
            // The spill and the shaft die WITH the picture rather than snapping off at the switch -- but they are not
            // squeezed. The collapse is the raster's, and a light cone narrowing to a blade would read as a bug.
            if (_light != null) { _light.LightEnergy = _lightEnergy * level; _light.LightColor = Spill; _light.Visible = level > 0f; }
            if (_coneMat != null) _coneMat.AlbedoColor = new Color(Spill, ConeAlpha * level);
            if (_cone != null) _cone.Visible = level > 0f;
        }

        void TickDesync(float dt)
        {
            if (_desyncLeft > 0f)
            {
                (_desyncLeft, _desyncOffset) = DesyncStep(_desyncLeft, _desyncOffset, _desyncSpeed, dt);
                ApplyDesync();
                return;
            }
            if (!DesyncCanFire(CanRoll(_kind, _program), _lit, _desyncLeft)) return;
            if (GD.Randf() >= DesyncChance(dt, DesyncMeanGap)) return;
            _desyncLeft = (float)GD.RandRange(DesyncMin, DesyncMax);
            _desyncSpeed = (float)GD.RandRange(DesyncSpeedMin, DesyncSpeedMax) * (GD.Randf() < 0.5f ? -1f : 1f);
        }

        void ApplyDesync()
        {
            _screenMat?.SetShaderParameter("roll_offset", _desyncOffset);
        }

        /// <summary>Drop any slip in progress and put the picture back in frame. A set going dark mid-roll must not
        /// come back on still rolling -- it would look like the effect had latched rather than fired.</summary>
        void ResetDesync()
        {
            _desyncLeft = 0f; _desyncOffset = 0f;
            ApplyDesync();
        }

        /// <summary>Stop the collapse and put the screen node back the way it was -- called both when it finishes and
        /// when the set is switched back on mid-effect. Leaving a squeezed transform behind would show up as a TV that
        /// turns on as a horizontal line and stays that way.</summary>
        void EndCollapse()
        {
            _collapse = -1f;
            SetMono(false);
            if (_screen != null) { _screen.Transform = new Transform3D(Basis.Identity, _screenOffset); _screen.Visible = _lit; }
            if (_light != null) _light.Visible = _lit;
            if (_cone != null) _cone.Visible = _lit;
        }

        /// <summary>Start the power-off collapse, in MONOCHROME (master: "when we do the beam collapse for turning off,
        /// change the picture to monochrome"). Swapping the texture rather than tinting it, because an unshaded albedo
        /// MULTIPLIES -- the same reason the warmup could not be a brightness ramp -- so there is no colour you can
        /// multiply an image by to desaturate it. The mono copy is built alongside the colour one and shared by every
        /// television with the same glass, so the swap costs nothing at the moment it happens.</summary>
        void StartCollapse()
        {
            _collapse = 0f;
            SetMono(true);
            ApplyCollapse();
        }

        /// <summary>Desaturate the picture. A uniform now, so it works for EVERY program rather than only the two that
        /// had a texture to swap -- which is what lets a monochrome CRT television be a thing at all (master: "the CRT
        /// tvs can be either monochrome or color"). The power-off collapse uses the same switch.
        ///
        /// The mono TEXTURE the composite still builds is no longer what does this; it is kept because ScreenTextures
        /// is the one place the blanking bar is baked, and pulling the mono copy out of it would only save building an
        /// image that is shared across every television on the map anyway.</summary>
        void SetMono(bool mono)
        {
            _mono = mono;
            _screenMat?.SetShaderParameter("mono", (mono || _monoTube) ? 1f : 0f);
        }

        // ---- monitor colour cycle -----------------------------------------------------------------------------------
        /// <summary>Pick the next colour index from a 0..1 roll, guaranteeing it is NOT the current one. A plain
        /// `Randi() % n` repeats a fifth of the time, and a repeat does not read as chance -- it reads as the cycle
        /// having stopped, which is exactly the bug this effect would be reported as.</summary>
        internal static int NextColourIndex(int cur, float roll, int n)
        {
            if (n <= 1) return 0;
            cur = ((cur % n) + n) % n;
            int step = 1 + Mathf.Clamp((int)(Mathf.Clamp(roll, 0f, 0.9999f) * (n - 1)), 0, n - 2);
            return (cur + step) % n;
        }

        /// <summary>Advance the terminal. Either it is mid-burst (text flowing, no cursor) or parked (cursor blinking)
        /// -- never both, because a real terminal does not scroll and blink at once, and doing both reads as two
        /// effects fighting rather than one machine working.</summary>
        void TickScroll(float dt)
        {
            // The cursor blinks CONTINUOUSLY now, typing or not. It used to be suppressed mid-burst, because it shared
            // a line with the text and the two read as effects fighting -- but master moved it onto its own blank line
            // below whatever is being printed, and on its own line there is nothing to fight with. A real terminal's
            // cursor sits at the insertion point and blinks the whole time.
            _screenMat?.SetShaderParameter("cursor_on", Mathf.PosMod(_clock, 1f) < 0.5f ? 1f : 0f);

            if (_burstLeft > 0f)
            {
                _burstLeft -= dt;
                _headCol += dt * TypeSpeed;
                if (_headCol >= ScrollCols) { _headCol = 0f; _headLine += 1f; }   // wrap to the next line
                if (_burstLeft <= 0f) _idleLeft = (float)GD.RandRange(IdleMin, IdleMax);
                _screenMat?.SetShaderParameter("head_line", _headLine);
                _screenMat?.SetShaderParameter("head_col", _headCol);
                return;
            }
            _idleLeft -= dt;
            if (_idleLeft <= 0f) _burstLeft = (float)GD.RandRange(BurstMin, BurstMax);
        }

        /// <summary>Move the spill to where the picture is actually bright, and narrow it to how much of the screen is
        /// lit. A DVD logo drags its pool of light across the wall as it bounces; a cursor throws a pencil from the
        /// top-left; a test card floods the room from the middle.
        ///
        /// The offset is measured along the screen's OWN axes -- the same ax/ay Reproject built the UVs from -- so
        /// "where the logo is in UV" and "where the light is in the world" are the same statement rather than two that
        /// happen to agree. The _cone branch is a no-op while the visible shaft is withheld (see ShowShaft); the spill
        /// is unaffected, because that is what actually lights the room.</summary>
        void PlaceEmitter()
        {
            var (c, ext) = Emitter(_program, _blob, _blobHalf);
            var offset = _screenRightLocal * (c.X * _screenHalfW * 2f)
                       + _screenUpLocal * (c.Y * _screenHalfH * 2f);
            if (_light != null)
            {
                _light.Transform = new Transform3D(AimBasis(_screenNormalLocal),
                                                   _screenCenterLocal + offset + _screenNormalLocal * 0.05f);
                _light.SpotAngle = Mathf.Lerp(18f, _lightBaseAngle, Mathf.Clamp((ext.X + ext.Y) * 0.5f, 0f, 1f));
            }
            if (_cone != null)
            {
                // Cross-section only: X and Z are the beam's width, Y is its length and must not move, or the shaft
                // would grow and shrink toward the viewer instead of getting narrower.
                float k = Mathf.Max(BeamMinScale, (ext.X + ext.Y) * 0.5f);
                _cone.Transform = new Transform3D(_beamBasis * Basis.FromScale(new Vector3(k, 1f, k)),
                                                  _screenCenterLocal + offset);
            }
        }

        void TickColour(float dt)
        {
            _colourLeft -= dt;
            if (_colourLeft > 0f) return;
            _colourIdx = NextColourIndex(_colourIdx, GD.Randf(), MonitorColours.Length);
            _colourLeft = (float)GD.RandRange(ColourHoldMin, ColourHoldMax);
            _tint = MonitorColours[_colourIdx];
            ApplyTint();
        }

        /// <summary>Push the current colour at everything that carries it. The SpotLight's colour is set once at build
        /// and then only here, so it is the one that would be left behind if this were folded into ApplyLevels -- and a
        /// blue-white spill under a green screen is the exact tell that the two stopped being the same light.</summary>
        void ApplyTint()
        {
            if (_light != null) _light.LightColor = Spill;
            ApplyLevels();
        }

        /// <summary>Brightness modulation, CRT only. A cosine BETWEEN TWO LIT LEVELS -- full and (1 - depth) -- never
        /// off (master: "when i say flicker i mean switch between a dimmer and brighter light state, not on/off").
        /// That is why it is 1 - depth*0.5*(1-cos) rather than anything that reaches zero: the floor is a real
        /// brightness, so the picture is continuously visible and only its level moves.</summary>
        internal static float Flicker(float phase01, float depth)
            => 1f - depth * 0.5f * (1f - Mathf.Cos(phase01 * Mathf.Tau));

        float FlickerFactor() => IsTube(_kind) ? Flicker(_flickerPhase, FlickerDepth) : 1f;

        void ApplyLevels()
        {
            // k is the CROSSFADE, not a dimmer: 0 is the bare tube face showing through, 1 is the full picture, and
            // the tube dissolves into the image in between. Brightness is held at full the whole way, so what comes up
            // is a picture RESOLVING rather than a picture brightening -- the difference between a tube warming and a
            // lamp on a dial. On the flatscreen k is pinned at 1, so an LCD still snaps and takes none of this.
            float k = IsTube(_kind) ? _warm : 1f;
            float f = FlickerFactor();   // 1.0 on a panel; a shallow breath on a tube
            if (_screenMat != null) _screenMat.SetShaderParameter("tint", ScreenColor(Picture, _emitEnergy * f, k));
            // ...and the SPILL is scaled by how bright the picture actually is (master). A terminal showing a black
            // screen barely lights the room; a test card floods it. Without this every set threw the same light no
            // matter what it was showing, which is most obvious on the DVD screensaver -- a white blob on black,
            // lighting the wall as hard as a full test card.
            float lum = ConeScale(_program, _tint);
            if (_light != null) _light.LightEnergy = _lightEnergy * k * f * lum;
            // the shaft rides it too, so the picture, the spill and the beam pulse together instead of drifting apart
            if (_coneMat != null) _coneMat.AlbedoColor = new Color(Spill, ConeAlpha * k * f * lum);
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
            // A wire going live (or dying) is the plug's half of the power gate. The mains half arrives as a push --
            // DayNightCycle sweeps the "tvdevices" group on a grid change -- but PowerNet has no such sweep, so this
            // edge has to be polled. Cheap: one bool compare per set per frame, and Refresh early-outs when nothing
            // actually changed.
            if (PlugPowered != _plugWasPowered) Refresh();

            // A TUBE breathes for as long as it is lit -- so this no longer early-outs on !_warming, which it did back
            // when warmup was the only thing that animated.
            if (_lit)
            {
                // The programs animate off this rather than off a global clock, so every set is offset by its own seed
                // and a room of televisions does not blink in unison. Only advanced while LIT: a set switched back on
                // should not resume a DVD blob from wherever it would have drifted to in the dark.
                _clock += (float)delta;
                _screenMat?.SetShaderParameter("time_s", _clock);
                if (_program == ScreenProgram.Colour) TickColour((float)delta);   // re-applies only on an actual change
                if (_program == ScreenProgram.TerminalScroll) TickScroll((float)delta);
                if (_program == ScreenProgram.Dvd) { _blob = BlobPos(_clock, _seed, _blobHalf); _screenMat?.SetShaderParameter("blob_pos", _blob); }
                PlaceEmitter();
                if (IsTube(_kind))
                {
                    _flickerPhase = Mathf.Wrap(_flickerPhase + (float)delta * FlickerHz, 0f, 1f);
                    if (CanRoll(_kind, _program)) TickDesync((float)delta);
                    ApplyLevels();
                }
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
        static Shader _screenShader;
        static Shader ScreenShader()
        {
            if (_screenShader != null) return _screenShader;
            _screenShader = GD.Load<Shader>("res://content/screen.gdshader");
            if (_screenShader == null) GD.PrintErr("[tv] screen.gdshader failed to load -- every screen will be blank");
            return _screenShader;
        }

        /// <summary>The screen material. Was a StandardMaterial3D; it is a ShaderMaterial now because six of the seven
        /// programs are drawn procedurally and the alternative is rewriting an ImageTexture per set per frame on the
        /// CPU, with dozens of sets on the map.
        ///
        /// The three properties the old material was carrying for a REASON are preserved as render_mode in the shader
        /// and must stay that way -- they are documented there, and asserted here by the suite rather than trusted:
        /// unshaded (a screen is a light source, so no light in the world can wash out its colours), cull_disabled
        /// (ripped meshes), and alpha blending (the warmup is a crossfade onto the cabinet's own screen face, which
        /// brightness cannot do because albedo MULTIPLIES).</summary>
        internal static ShaderMaterial MakeScreenMaterial(Texture2D pattern) => MakeScreenMaterial(pattern, ScreenProgram.TestCard, 1f, 0f);

        internal static ShaderMaterial MakeScreenMaterial(Texture2D pattern, ScreenProgram program, float patternFrac, float seed)
        {
            var m = new ShaderMaterial { Shader = ScreenShader() };
            m.SetShaderParameter("program", (int)program);
            // Black rather than null: an unset sampler is WHITE, so a missing test card would render every
            // television as a blank white screen -- the brightest possible failure for a missing file.
            m.SetShaderParameter("pattern_tex", pattern ?? (Texture2D)Solid1x1(Colors.Black));
            m.SetShaderParameter("pattern_frac", patternFrac);
            m.SetShaderParameter("roll_offset", 0f);
            m.SetShaderParameter("tint", new Color(1f, 1f, 1f, 0f));   // fully dissolved into the tube face; ApplyLevels fades it up
            m.SetShaderParameter("mono", 0f);
            m.SetShaderParameter("time_s", 0f);
            m.SetShaderParameter("seed", seed);
            m.SetShaderParameter("head_line", 0f);
            m.SetShaderParameter("head_col", 0f);
            m.SetShaderParameter("cursor_on", 0f);
            m.SetShaderParameter("blob_pos", new Vector2(0.5f, 0.5f));
            m.SetShaderParameter("blob_half", new Vector2(0.12f, 0.18f));
            return m;
        }


        /// <summary>Screen brightness -> AlbedoColor. Grey, so the SMPTE bars keep their own hues and only their
        /// level moves; black at 0 is what makes the CRT warmup a fade of the picture itself.</summary>
        internal static Color ScreenColor(float brightness) => new Color(brightness, brightness, brightness);

        /// <summary>...and how much of it is showing. <paramref name="fade"/> 0 is the bare tube face, 1 is the full
        /// picture; a tube's warmup drives it, a panel pins it at 1.</summary>
        internal static Color ScreenColor(float brightness, float fade)
            => ScreenColor(Colors.White, brightness, fade);

        /// <summary>...and what colour it is. WHITE for a television, because the SMPTE texture already carries its own
        /// colours and an unshaded albedo MULTIPLIES the texture -- any other tint here would recolour the bars. A
        /// monitor has no texture at all, so this IS its picture, which is the whole reason the monitors needed no new
        /// warmup, collapse or flicker path: all three drive exactly this.</summary>
        internal static Color ScreenColor(Color tint, float brightness, float fade)
            => new Color(tint.R * brightness, tint.G * brightness, tint.B * brightness, Mathf.Clamp(fade, 0f, 1f));

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
        /// <summary>The tube's glass colour as actually resolved from the prop. White here means the sample fell
        /// through to a vertex colour or a fallback that is not the screen texel -- which is what put a white band
        /// across the picture once already.</summary>
        public Color DebugGlassColor => _glassColor;
        public float DebugDesyncOffset => _desyncOffset;
        public bool DebugDesyncRolling => _desyncLeft > 0f;
        /// <summary>Force a vertical-hold slip, so the tick loop can be driven without waiting on a random roll.</summary>
        public void DebugForceDesync(float seconds, float speed) { _desyncLeft = seconds; _desyncSpeed = speed; }
        public bool DebugScreenOk => _screen != null;
        /// <summary>Is the screen taking NO lighting? The washout fix depends on this being true, and it is the kind
        /// of property that a screenshot cannot distinguish from "the light happens to be dim right now".</summary>
        /// <summary>Unshaded is a render_mode in the shader now, so this reports whether the screen is drawing
        /// through THAT shader at all -- which is the property the washout fix actually depends on.</summary>
        public bool DebugScreenUnshaded => _screenMat?.Shader != null;
        /// <summary>Screen brightness as actually applied (AlbedoColor). 0 = black tube, _emitEnergy = full picture.</summary>
        public float DebugScreenBrightness => _screenMat == null ? -1f : ((Color)_screenMat.GetShaderParameter("tint")).R;
        public bool DebugIsCrt => IsTube(_kind);
        public DeviceKind DebugKind => _kind;
        /// <summary>The colour the screen is currently showing (white on a television -- the card carries its own).</summary>
        public Color DebugTint => Picture;
        /// <summary>The spill/shaft colour actually applied to the SpotLight, so a render-free check can prove the cone
        /// follows the screen rather than merely that the screen changed.</summary>
        public Color DebugSpill => _light?.LightColor ?? Colors.Transparent;
        /// <summary>The texture the screen is actually sampling. NULL on a monitor -- its picture is the albedo
        /// colour and there is no card to show -- and non-null on a television. Exposed because "the monitor has no
        /// test pattern" is otherwise only checkable by looking at it, and a dim SMPTE card at a distance reads as
        /// a flat colour.</summary>
        public Texture2D DebugScreenTexture => _screenMat?.GetShaderParameter("pattern_tex").As<Texture2D>();
        public ScreenProgram DebugProgram => _program;
        public float DebugConeScale => ConeScale(_program, _tint);
        /// <summary>The spill light's actual energy. The shaft used to be the probe for "the brightness reached
        /// something real"; with the shaft withheld this is what remains, and it is the one that lights the room.</summary>
        public float DebugLightEnergy => _light?.LightEnergy ?? -1f;
        public float DebugConeAlpha => _coneMat?.AlbedoColor.A ?? -1f;
        /// <summary>The write head as one monotonic number: whole part = lines typed, fraction = progress along the
        /// current line. One value rather than two so a test can say "it advanced" without caring which of the two
        /// moved -- a line wrap moves both at once.</summary>
        public float DebugTypeHead => _headLine + _headCol / ScrollCols;
        /// <summary>Is a burst running right now? Paired with DebugCursorOn so the "never both at once" rule can be
        /// sampled at ONE INSTANT. Comparing scroll positions across a frame interval cannot express it: a burst that
        /// ends mid-interval leaves the position advanced and the cursor lit, which looks like a violation and is not.</summary>
        public bool DebugScrolling => _burstLeft > 0f;
        public bool DebugCursorOn => _screenMat != null && (float)_screenMat.GetShaderParameter("cursor_on") > 0.5f;
        /// <summary>Force this set onto a given program, rebuilding the loop sound to match.
        ///
        /// Needed because the program is chosen RANDOMLY at build -- that is the point, a street of televisions should
        /// not all show the same thing -- which makes "did the DVD blob work" untestable by construction. Without this
        /// a suite can only assert whatever the RNG happened to pick, so six of the seven programs would go uncovered
        /// on any given run and the suite would still be green.</summary>
        public void DebugSetProgram(ScreenProgram p)
        {
            _program = p;
            _screenMat?.SetShaderParameter("program", (int)p);
            if (p == ScreenProgram.Colour) _tint = MonitorColours[_colourIdx];
            _tone?.QueueFree(); _tone = null;
            BuildLoopSound();
            SetMono(_mono);
            ApplyLevels();
            if (_lit) _tone?.Play();
        }
        public ScreenSound DebugSound => SoundFor(_program);
        public bool DebugMonoTube => _monoTube;
        public string DebugScreenShaderCode => _screenMat?.Shader?.Code ?? "";
        public float DebugMonoUniform => _screenMat == null ? -1f : (float)_screenMat.GetShaderParameter("mono");
        /// <summary>Which indicator cube is currently emitting, for a check that does not need a render.</summary>
        public (bool On, bool Standby) DebugLeds =>
            (_ledOn != null && _ledOn.Visible && (_ledOnMat?.EmissionEnergyMultiplier ?? 0f) > 0f,
             _ledStandby != null && _ledStandby.Visible && (_ledStandbyMat?.EmissionEnergyMultiplier ?? 0f) > 0f);
        public bool DebugHasOnLed => _ledOn != null;
        public bool DebugHasStandbyLed => _ledStandby != null;
        public bool DebugHasPlug => _plug != null && GodotObject.IsInstanceValid(_plug);
        public Vector3 DebugPlugLocal => _plug?.Position ?? Vector3.Zero;
        public float DebugPlugWatts => _plug?.Watts ?? 0f;
        public bool DebugHasTone => _tone != null;
        /// <summary>WHICH wav is loaded, not merely whether one is. Asserting only that a sound exists cannot catch a
        /// set playing the wrong one -- a television humming hiss over a test card and one humming the tone are both
        /// "has a sound", and that is the whole of the claim being made about gating.</summary>
        public string DebugLoopSound => _loopPath;
        public float DebugToneRange => _tone?.MaxDistance ?? -1f;
        public bool DebugTonePlaying => _tone != null && _tone.Playing;
        /// <summary>Force a colour change now, so the cycle can be driven without waiting on the hold timer.</summary>
        public void DebugCycleColour() { _colourLeft = 0f; TickColour(0f); }
        public Vector3 DebugScreenCenterWorld => ToGlobal(_screenCenterLocal);
        /// <summary>The screen's facing in the PROP's own frame -- the winding normal. Exposed because which
        /// way a screen points is invisible in a still from the front and catastrophic from anywhere else.</summary>
        public Vector3 DebugScreenNormalLocal => _screenNormalLocal;
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
