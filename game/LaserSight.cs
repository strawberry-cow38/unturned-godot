using Godot;

namespace UnturnedGodot
{
    /// <summary>The tactical laser's beam and dot (strawberry 2026-09-13: "give the tactical laser an actual
    /// laser beam. toggles on/off with N").
    ///
    /// WHAT RETAIL ACTUALLY HAS, because it is half of this. UseableGun instantiates `Guns/Laser.prefab` while
    /// `firstAttachments.tacticalAsset.isLaser &amp;&amp; interact`, colours it
    /// `laserMaterial.SetColor("_Color", tacticalAsset.laserColor)` with `_EmissionColor` at twice that, and
    /// every frame raycasts `player.look.aim` out to 2048 m on RayMasks.BLOCK_LASER and parks the prefab at
    /// `contact.point + laserDirection * -0.05f`, hiding it when the ray hits nothing. The prefab carries
    /// TacticalLaserScale, whose summary calls it "the tactical laser attachment's red dot" and which resizes
    /// per-camera "to maintain a constant on-screen size", tapered by a curve "so that far away laser is not
    /// quite as comically large".
    ///
    /// So retail's laser is a DOT ON THE WALL and nothing else -- there is no beam in the source at all. The
    /// beam here is strawberry's ask, and it is drawn as what a real one is: a thin additive line that is
    /// mostly air, not a solid rod. The dot is the retail half and is the part that actually aims the gun.
    ///
    /// The dot's scale is retail's formula -- `distance * tan(fovY/2) * k` is exactly constant on-screen size --
    /// with the taper as an explicit exponent rather than the AnimationCurve, which lives in the prefab's
    /// serialised data and not in any source file I can read. UG_LASERDOT / UG_LASERBEAM override the two
    /// constants at runtime so they can be tuned against a picture instead of guessed at twice.</summary>
    public partial class LaserSight : Node3D
    {
        /// <summary>Retail raycasts 2048 m (UseableGun ~5849). Kept, because a laser that stops at 100 m would
        /// be a different sight on a long shot, which is the shot you would use it for.</summary>
        public const float MaxRange = 2048f;
        /// <summary>Retail pulls the dot 5 cm back along the ray so it never z-fights what it landed on.</summary>
        public const float SurfaceLift = 0.05f;
        /// <summary>Beam radius in metres. A real sight's beam is invisible except in dust; this is the
        /// thinnest line that still reads at arm's length.</summary>
        public const float BeamRadius = 0.004f;
        /// <summary>Retail's `scaleMultiplier`, 0.1 on the prefab -- but its quad's own size is serialised data
        /// I cannot read, so this is the one number here that is a choice rather than a reading. Flagged as
        /// such: it is the dot's apparent size and nothing downstream depends on it.</summary>
        public const float DotScale = 0.035f;
        /// <summary>Stands in for TacticalLaserScale's AnimationCurve. 1.0 would be exactly constant on-screen
        /// size; below 1 the dot shrinks with distance, which is the curve's stated purpose.</summary>
        public const float DotTaper = 0.85f;

        MeshInstance3D _beam, _dot;

        // ---- test seams. The dot IS the feature (it is the half retail ships), and "a laser exists" is not the
        // claim -- "the dot is on the thing you are pointing at" is, so a test has to be able to read where it
        // landed rather than that a node was created.
        public bool Lit => _beam != null && _beam.Visible;
        public Vector3 DotWorld => _dot != null ? _dot.GlobalPosition : Vector3.Zero;
        public Vector3 BeamFrom { get; private set; }
        public float BeamLength { get; private set; }

        StandardMaterial3D _beamMat, _dotMat;
        Color _color = Colors.Red;

        public override void _Ready()
        {
            TopLevel = true;   // the beam is placed in WORLD space every frame; the player's own transform must not move it
            SetProcess(false);
            _beamMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DisableReceiveShadows = true,
                NoDepthTest = false,
            };
            _dotMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DisableReceiveShadows = true,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                // ⚠ A BILLBOARD DISCARDS THE NODE'S SCALE unless this is set, and the dot's whole job is to be
                // scaled by distance -- without it every dot renders at the quad's authored size and the
                // constant-on-screen-size formula above computes a number nothing reads.
                BillboardKeepScale = true,
            };
            _beam = new MeshInstance3D
            {
                Name = "Beam",
                Mesh = new CylinderMesh { TopRadius = BeamRadius, BottomRadius = BeamRadius, Height = 1f, RadialSegments = 6, Rings = 0 },
                MaterialOverride = _beamMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            _dot = new MeshInstance3D
            {
                Name = "Dot",
                Mesh = new QuadMesh { Size = new Vector2(1f, 1f) },
                MaterialOverride = _dotMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            // BOTH children are TopLevel: each is placed by a single GlobalTransform write per frame, and a
            // node that also inherits a parent transform would have that write silently composed with it.
            _beam.TopLevel = true;
            _dot.TopLevel = true;
            AddChild(_beam);
            AddChild(_dot);
            SetColor(_color);
        }

        /// <summary>Retail: `_Color` = the asset's laserColor and `_EmissionColor` = the same AT TWICE IT
        /// (UseableGun ~4737-4738).
        ///
        /// ⚠ THE 2x RIDES IN THE ALBEDO, NOT IN AN EMISSION SLOT, and that is a Godot fact rather than a
        /// shortcut: an UNSHADED material writes its albedo straight out and ignores Emission entirely, so
        /// `EmissionEnabled = true; EmissionEnergyMultiplier = 2f` on one of these is dead code that reads
        /// exactly like a glowing laser in the source and produces a flat one on screen. An unshaded colour
        /// ABOVE 1.0 is what reaches HDR and blooms, so retail's x2 lands there instead -- same number, same
        /// result, on the slot this shading mode actually reads.</summary>
        public void SetColor(Color c)
        {
            _color = c;
            if (_beamMat == null) return;
            _beamMat.AlbedoColor = new Color(c.R * EmissionGain, c.G * EmissionGain, c.B * EmissionGain, BeamAlpha);
            _dotMat.AlbedoColor = new Color(c.R * EmissionGain, c.G * EmissionGain, c.B * EmissionGain, DotAlpha);
        }

        /// <summary>Retail's `laserColor * 2.0f` for _EmissionColor.</summary>
        public const float EmissionGain = 2f;
        /// <summary>The beam is MOSTLY AIR. A real sight's beam is invisible except where dust catches it, so a
        /// solid rod is the wrong answer even though it is the easy one -- this is a faint additive line.</summary>
        public const float BeamAlpha = 0.16f;
        /// <summary>The dot is the part you aim with, so it is nearly solid.</summary>
        public const float DotAlpha = 0.95f;

        public void Hide3D()
        {
            BeamLength = 0f;
            if (_beam != null) _beam.Visible = false;
            if (_dot != null) _dot.Visible = false;
        }

        /// <summary>Place the beam and the dot for this frame. <paramref name="emitter"/> is where the beam is
        /// DRAWN from (the gun), <paramref name="aimFrom"/>/<paramref name="aimDir"/> is the ray that decides
        /// where it LANDS (the eye, exactly as retail does it) -- the two are deliberately different, the same
        /// split the tracer already documents: a beam drawn from the eye would have no gun under it, and a ray
        /// cast from the muzzle would paint a dot the bullet does not go to.</summary>
        public void Aim(Vector3 emitter, Vector3 aimFrom, Vector3 aimDir, float camFovDegrees, Vector3 camPos)
        {
            if (_beam == null) return;
            var space = GetWorld3D()?.DirectSpaceState;
            if (space == null) { Hide3D(); return; }

            aimDir = aimDir.Normalized();
            var q = PhysicsRayQueryParameters3D.Create(aimFrom, aimFrom + aimDir * MaxRange, HitMask);
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) { Hide3D(); return; }   // retail: SetActive(false) when the ray finds nothing

            Vector3 point = (Vector3)hit["position"] - aimDir * SurfaceLift;
            Vector3 span = point - emitter;
            float len = span.Length();
            if (len < 0.05f) { Hide3D(); return; }

            // Y-along-the-ray basis, built by hand rather than LookingAt: a CylinderMesh's axis is +Y and
            // LookingAt aims -Z, so the convenience call would need a second rotation and a degenerate-up case
            // anyway. The fallback axis covers a perfectly vertical beam.
            Vector3 y = span / len;
            Vector3 x = y.Cross(Vector3.Up);
            if (x.LengthSquared() < 1e-6f) x = y.Cross(Vector3.Right);
            x = x.Normalized();
            Vector3 z = x.Cross(y).Normalized();
            // The length rides in the BASIS (column Y scaled by len) rather than in a separate Scale write:
            // CylinderMesh Height is 1, so Y is the length in metres, and one transform assignment cannot be
            // composed with a stale local scale the way a GlobalTransform-then-Scale pair can.
            _beam.GlobalTransform = new Transform3D(new Basis(x, y * len, z), emitter + y * (len * 0.5f));
            _beam.Visible = true;
            BeamFrom = emitter; BeamLength = len;

            // CONSTANT ON-SCREEN SIZE, tapered. distance * tan(halfFov) is retail's `distanceFromCamera *
            // screenRatio`; the exponent stands in for its curve.
            float dist = Mathf.Max(0.05f, (point - camPos).Length());
            float screenRatio = Mathf.Tan(Mathf.DegToRad(Mathf.Clamp(camFovDegrees, 1f, 179f) * 0.5f));
            float s = DotScaleK * Mathf.Pow(dist * screenRatio, DotTaperK);
            _dot.GlobalTransform = new Transform3D(Basis.Identity.Scaled(new Vector3(s, s, s)), point);
            _dot.Visible = true;
        }

        /// <summary>World + vehicles + props -- what a laser can paint. Players and debris are out, which is
        /// also what retail's RayMasks.BLOCK_LASER excludes.</summary>
        public const uint HitMask = (1u << 0) | (1u << 5) | (1u << 6);

        // UG_LASERDOT / UG_LASERBEAM: tune the two numbers that are choices rather than readings, against a
        // picture, without a rebuild. Read once (statics), same idiom as UG_SWELL/UG_DEPTHROW.
        static readonly float DotScaleK = EnvF("UG_LASERDOT", DotScale);
        static readonly float DotTaperK = EnvF("UG_LASERTAPER", DotTaper);
        static float EnvF(string name, float dflt)
            => System.Environment.GetEnvironmentVariable(name) is string s && float.TryParse(s, out float v) ? v : dflt;
    }
}
