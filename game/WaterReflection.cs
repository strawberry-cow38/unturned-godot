using Godot;

namespace UnturnedGodot
{
    /// <summary>Planar water reflection. A SubViewport whose camera is the MAIN camera MIRRORED across the water
    /// plane (Y = _y) renders the reflection-worthy geometry into a texture the water shader samples + Fresnel-gates.
    /// Cheap by construction, not by lowering resolution (master: "the most evil hacks"):
    ///   - REFLECTION LAYER: the mirror camera's cull_mask is one visual layer, so it renders ONLY the trees/props/
    ///     buildings flagged for reflection and skips grass, particles, UI, the whole map, for free.
    ///   - TEMPORAL: re-renders every _every frames, not 60/s -- ripple-blur hides the low rate (it's not resolution).
    ///   - NO SHADOWS in the reflection buffer -- the chop hides them, big saving.
    /// Survives the SEE-THROUGH water + cut-down-able trees a bake can't: it's a live pass, so chopping a tree drops
    /// its reflection the same frame, and the shader Fresnel-gates it so it only shows where reflections read.
    /// UG_REFLALL=1 renders every layer (diagnostic: prove the pipe before layer-flagging trees/props).
    /// UG_REFLEVERY overrides the temporal stride. Setup() is called next to the ocean MeshInstance in Terrain.</summary>
    public partial class WaterReflection : Node3D
    {
        public const uint ReflLayer = 1u << 18;   // visual layer 19: reflection-worthy geometry ALSO renders here (NOT 1<<19 -- that's OutlineOverlay.OutlineLayer; sharing it would draw trees as solid outline silhouettes)
        public const uint WaterLayer = 1u << 17;   // visual layer 18: the water plane itself sits here so the mirror camera can SKIP it (else it looks up into the water's own underside + occludes the trees)
        SubViewport _vp; Camera3D _cam; ShaderMaterial _mat; float _y; int _f; int _every = 2;
        public static bool Enabled = true; public static int EveryFrames = 1;   // GraphicsOptions.PlanarReflection (retail PlanarReflectionQuality)

        public void Setup(ShaderMaterial waterMat, float waterY, Vector2I res)
        {
            _mat = waterMat; _y = waterY;
            if (int.TryParse(System.Environment.GetEnvironmentVariable("UG_REFLEVERY"), out var e) && e >= 1) _every = e;
            bool all = System.Environment.GetEnvironmentVariable("UG_REFLALL") == "1";
            _vp = new SubViewport
            {
                Size = res,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                TransparentBg = true,             // empty = alpha 0 so the shader composites reflected geometry over sky_tint
                Msaa3D = Viewport.Msaa.Disabled,
                PositionalShadowAtlasSize = 0,   // no shadows in the reflection -- ripple hides them
            };
            AddChild(_vp);
            // diagnostic "all" mask still drops the water plane (self-occlusion) AND the outline-silhouette layer (else focus glows reflect as solid tints).
            _cam = new Camera3D { PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off, Current = true, CullMask = all ? (0xFFFFFu & ~WaterLayer & ~OutlineOverlay.OutlineLayer) : ReflLayer };
            _vp.AddChild(_cam);
            _mat.SetShaderParameter("reflection_tex", _vp.GetTexture());
            _mat.SetShaderParameter("reflection_on", true);
            _mat.SetShaderParameter("reflection_debug", System.Environment.GetEnvironmentVariable("UG_REFLDEBUG") == "1");
            float str = 1f; if (float.TryParse(System.Environment.GetEnvironmentVariable("UG_REFLSTR"), out var s)) str = s;
            _mat.SetShaderParameter("reflection_strength", str);
        }

        public override void _Process(double delta)
        {
            if (_mat != null) { _mat.SetShaderParameter("reflection_on", Enabled); if (!Enabled) { if (_vp != null && _vp.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled) _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled; return; } }   // GraphicsOptions.PlanarReflection Off
            _every = System.Math.Max(1, EveryFrames);   // Low = every 4th frame, Medium = every 2nd, High/Ultra = every frame
            if (_vp == null) return;
            var main = GetViewport()?.GetCamera3D();
            if (main == null) return;
            _f++;
            // temporal: re-render only every _every frames; the ripple hides the low rate.
            _vp.RenderTargetUpdateMode = (_f % _every == 0) ? SubViewport.UpdateMode.Once : SubViewport.UpdateMode.Disabled;
            // MIRROR the main camera across the water plane Y=_y. A negative-Y-scale basis is the "correct" mirror but
            // its NEGATIVE DETERMINANT flips triangle winding, so single-sided geometry (tree leaf-cards, trunks) gets
            // back-face-culled out of the reflection -- tall trees vanish from the buffer. Instead build a PROPER
            // (positive-det) camera: reflect the position across the plane, then look along the mirrored forward with a
            // mirrored up. Winding stays correct so all geometry renders; the shader Y-flips SCREEN_UV to sample it.
            var mt = main.GlobalTransform;
            if (System.Environment.GetEnvironmentVariable("UG_REFLLOOKAT") == "1")
            {
                // POSITIVE-det look-at fallback: winding stays correct so single-sided geometry never culls, but the
                // framing is only approximate -> the shader must Y-flip SCREEN_UV and the reflection can be a bit soft.
                var mp = mt.Origin;
                var mirroredPos = new Vector3(mp.X, 2f * _y - mp.Y, mp.Z);
                var fwd = -mt.Basis.Z; var up = mt.Basis.Y;
                _cam.LookAtFromPosition(mirroredPos, mirroredPos + new Vector3(fwd.X, -fwd.Y, fwd.Z), new Vector3(up.X, -up.Y, up.Z));
            }
            else
            {
                // NEGATIVE-det EXACT mirror: reflection-camera view = R * main_transform, so a point above the water
                // projects to the SAME screen pixel as its reflection does in the main view -> the shader samples the
                // buffer straight by SCREEN_UV (no Y-flip), crisp + correctly framed. Cost: the mirror flips triangle
                // winding, so single-sided geometry back-face-culls; reflection-worthy meshes must be double-sided.
                var refl = new Transform3D(new Vector3(1f, 0f, 0f), new Vector3(0f, -1f, 0f), new Vector3(0f, 0f, 1f), new Vector3(0f, 2f * _y, 0f));
                _cam.GlobalTransform = refl * mt;
            }
            _cam.Fov = main.Fov; _cam.Near = main.Near; _cam.Far = main.Far;
        }
    }
}
