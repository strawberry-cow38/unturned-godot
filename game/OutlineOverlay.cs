using Godot;

namespace UnturnedGodot
{
    // Screen-space selection outline (our own -- the src uses a licensed third-party asset). Master wants a CRISP
    // rarity outline on the looked-at item; the flat, double-sided ripped item meshes defeat every inverted-hull trick
    // (they fill the item or vanish face-on). So: each item's _glow silhouette mesh lives on visual layer OutlineLayer
    // (which the MAIN cameras cull). This overlay's SubViewport shares the main World3D + copies the active camera each
    // frame, rendering ONLY that layer -> a white mask of just the focused item. A fullscreen dilate pass
    // (item_outline.gdshader) then draws a rarity RIM around the mask's silhouette. Topology-independent + crisp.
    public partial class OutlineOverlay : Node
    {
        public const uint OutlineLayer = 1u << 19;   // items' _glow silhouettes render here; main cams cull it, the mask cam renders only it

        // Perf (strawberry: 3p vehicle cam tanks fps in POIs). The subviewport's mask cam does a SECOND full
        // scene-cull every frame (UpdateMode.Always) + a fullscreen dilate pass -- cheap in a narrow 1p view,
        // but the wide 3p vehicle view over a dense POI culls far more objects, x2 (main + mask cam), 280x/s.
        // And while driving NOTHING is ever focused (PlayerController gates the look-at off in a vehicle), so
        // the whole pass is pure waste there. PlayerController sets this on vehicle enter/exit.
        public static bool DrivingSuppress;

        // ---- shared look-at outline helpers ------------------------------------------------------------------
        // Every focusable prop/item/container used to hand-roll its own silhouette + colour claim (9 copies), and a
        // standalone doored prop once forgot the claim and wore the last item's rarity. These are the ONE place:
        // MakeOutline builds the (always-white) mask mesh; ShowOutline toggles it AND claims the rim colour in a
        // single call, so a caller can't light a silhouette without saying what colour its rim should be.
        public static MeshInstance3D MakeOutline(Mesh mesh, Transform3D? transform = null)
        {
            var mi = new MeshInstance3D
            {
                Mesh = mesh, Visible = false, Layers = OutlineLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Colors.White, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
            };
            if (transform.HasValue) mi.Transform = transform.Value;   // bake a door pivot / shelf upright / standalone placement; omit for a child already placed by its owner
            return mi;
        }

        // Show/hide a look-at silhouette AND claim its rim colour together, so a caller can never light one without
        // saying its colour. On GAIN claims WorldItem.FocusColor; NEVER clears on loss (that would smear this colour
        // over an item that owns its rarity). color = rarity (items) / white (containers, doors, props) / etc.
        public static void ShowOutline(bool on, Color color, MeshInstance3D outline)
        {
            if (on) WorldItem.FocusColor = color;
            if (outline != null && GodotObject.IsInstanceValid(outline)) outline.Visible = on;
        }

        // Two-silhouette variant (a doored prop: body + swinging leaf) -- one colour claim, both toggled.
        public static void ShowOutline(bool on, Color color, MeshInstance3D a, MeshInstance3D b)
        {
            if (on) WorldItem.FocusColor = color;
            if (a != null && GodotObject.IsInstanceValid(a)) a.Visible = on;
            if (b != null && GodotObject.IsInstanceValid(b)) b.Visible = on;
        }

        SubViewport _vp;
        Camera3D _vpCam;
        ShaderMaterial _mat;
        TextureRect _tr;

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            _vp = new SubViewport
            {
                World3D = GetViewport().World3D,           // share the main scene so the mask cam sees the real items
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                Size = (Vector2I)GetViewport().GetVisibleRect().Size,
            };
            AddChild(_vp);
            _vpCam = new Camera3D { CullMask = OutlineLayer, Current = true, PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off };   // mirrors the main camera every frame
            _vp.AddChild(_vpCam);

            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/item_outline.gdshader") };
            _mat.SetShaderParameter(Sn.thickness, 3.5f);   // master: a teeny bit thicker
            _mat.SetShaderParameter(Sn.outline_color, new Vector3(1f, 1f, 1f));

            var canvas = new CanvasLayer { Layer = 50 };   // over the 3D view, under the HUD? 50 keeps it above the game, below any 100+ overlays
            AddChild(canvas);
            _tr = new TextureRect { Texture = _vp.GetTexture(), Material = _mat, MouseFilter = Control.MouseFilterEnum.Ignore };
            _tr.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            canvas.AddChild(_tr);
        }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            var cam = GetViewport().GetCamera3D();
            if (cam == null) return;
            // While driving, disable the whole pass (no second cull, no dilate, no stale mask on screen) --
            // nothing is focusable from a vehicle, so it's pure cost (esp. the wide 3p view over a POI).
            bool on = !DrivingSuppress && GraphicsOptions.Outline;   // retail OutlineQuality Off
            var mode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
            if (_vp.RenderTargetUpdateMode != mode) _vp.RenderTargetUpdateMode = mode;
            if (_tr != null && _tr.Visible != on) _tr.Visible = on;
            if (!on) return;
            var sz = (Vector2I)GetViewport().GetVisibleRect().Size;
            if (_vp.Size != sz) _vp.Size = sz;
            // match the active camera exactly so the mask aligns pixel-for-pixel with the main render
            _vpCam.GlobalTransform = cam.GlobalTransform;
            _vpCam.Fov = cam.Fov;
            _vpCam.Near = cam.Near;
            _vpCam.Far = cam.Far;
            _vpCam.KeepAspect = cam.KeepAspect;
            _mat.SetShaderParameter(Sn.outline_color, new Vector3(WorldItem.FocusColor.R, WorldItem.FocusColor.G, WorldItem.FocusColor.B));
        }
    }
}
