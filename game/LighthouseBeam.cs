using Godot;

namespace UnturnedGodot
{
    // A lighthouse's sweeping light BEAM (master 2026-08-09): a big additive cone shaft at the lamp room that slowly
    // spins, seen from far, NIGHT-GATED, with NO actual lighting -- purely the visible cone (the same trick as the
    // streetlight's fake beam shaft, but horizontal + huge + rotating). Reuses StreetLight.BeamMesh + ConeGradient so
    // the look matches the rest of the game's light cones.
    //
    // Its OWN TopLevel node (not parented into the tower's mesh) so it spins in world space and isn't dragged by the
    // tower's LODGroup. It shares the tower's ~447m region cull (master: "don't touch anything") -- past that the beam
    // culls with the tower, which is retail-accurate (tinyclaw: retail region-culls the lighthouse at 447 too).
    public partial class LighthouseBeam : Node3D
    {
        public static float SpinRate  = 0.08f;                        // rad/s -- a slow lighthouse sweep, ~78s per revolution (master: much slower)
        public static float BeamLen   = 200f;                         // how far the shaft reaches
        public static float SrcRadius = 2.0f;                         // half-width at the lamp room
        public static float FarRadius = 22f;                          // half-width at the far end (a widening beam)
        public static Color BeamColor = new Color(1f, 0.95f, 0.82f);  // warm white

        MeshInstance3D _cone;
        float _spin;

        public static LighthouseBeam Make(Vector3 lampRoomWorld)
            => new LighthouseBeam { Position = lampRoomWorld, TopLevel = true };

        // Fade toward the WIDE (far) end (master): dense at the apex near the lamp, transparent by the tip. The beam
        // mesh samples v = t*0.5 (apex t=0 -> v=0, far t=1 -> v=0.5), so this fades fully across rows 0..n/2. (The
        // streetlight's ConeGradient does the opposite -- dense at the far/ground end -- which is wrong for a beam.)
        static ImageTexture BeamGradient()
        {
            int n = 64;
            var img = Image.CreateEmpty(1, n, false, Image.Format.Rgba8);
            for (int y = 0; y < n; y++)
            {
                float t = Mathf.Clamp(y / (n * 0.5f), 0f, 1f);   // 0 at the apex row, 1 by row n/2 (the far end the beam reaches)
                float a = Mathf.Pow(1f - t, 1.5f);               // bright at the apex, faded to nothing at the wide end
                img.SetPixel(0, y, new Color(1f, 1f, 1f, a));
            }
            return ImageTexture.CreateFromImage(img);
        }

        public override void _Ready()
        {
            // BeamMesh runs along -Y; rotate it -90 about X so the shaft points HORIZONTALLY (+Z), then the NODE spins
            // around world-Y so the beam sweeps the horizon. Narrow at the lamp, widening to FarRadius far out.
            var mesh = StreetLight.BeamMesh(BeamLen, SrcRadius, SrcRadius, FarRadius, morphEnd: 0.12f, seg: 20, rings: 22);
            _cone = new MeshInstance3D
            {
                Mesh = mesh,
                RotationDegrees = new Vector3(-90f, 0f, 0f),          // -Y shaft -> +Z, horizontal
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEnd = LodTable.RegionMaxDistance,      // cull with the tower (~447m); don't outlive an invisible base
                MaterialOverride = new StandardMaterial3D
                {
                    // additive + unshaded + gradient-faded, same recipe as StreetLight's cone (soft, dissolves at the far end)
                    AlbedoColor = new Color(BeamColor.R, BeamColor.G, BeamColor.B, 0.06f),   // master: LESS opaque -- more see-through
                    AlbedoTexture = BeamGradient(),                    // master: fade out toward the WIDE (far) end, not the streetlight's fade-at-the-ground
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,  // render the inside of the cone too (additive, so the far wall just adds)
                    TextureRepeat = false,                            // CLAMP the gradient (no wrap)
                },
            };
            AddChild(_cone);
        }

        public override void _Process(double delta)
        {
            // NIGHT-GATE: only sweep at night. Self-gated off DayNightCycle.IsNightTime (default night if no cycle),
            // so this touches nothing shared -- no group registration, no edit to the day/night sweep.
            var dn = GetTree().GetFirstNodeInGroup("daynight") as DayNightCycle;
            bool night = dn == null || DayNightCycle.IsNightTime(dn.Time);
            if (_cone != null && IsInstanceValid(_cone) && _cone.Visible != night) _cone.Visible = night;
            if (!night) return;
            _spin = Mathf.PosMod(_spin + (float)delta * SpinRate, Mathf.Tau);
            Rotation = new Vector3(0f, _spin, 0f);                    // spin around world-Y -> the horizontal beam sweeps
        }
    }
}
