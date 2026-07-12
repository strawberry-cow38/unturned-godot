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

        SubViewport _vp;
        Camera3D _vpCam;
        ShaderMaterial _mat;

        public override void _Ready()
        {
            _vp = new SubViewport
            {
                World3D = GetViewport().World3D,           // share the main scene so the mask cam sees the real items
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                Size = (Vector2I)GetViewport().GetVisibleRect().Size,
            };
            AddChild(_vp);
            _vpCam = new Camera3D { CullMask = OutlineLayer, Current = true };
            _vp.AddChild(_vpCam);

            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/item_outline.gdshader") };
            _mat.SetShaderParameter("thickness", 3.5f);   // master: a teeny bit thicker
            _mat.SetShaderParameter("outline_color", new Vector3(1f, 1f, 1f));

            var canvas = new CanvasLayer { Layer = 50 };   // over the 3D view, under the HUD? 50 keeps it above the game, below any 100+ overlays
            AddChild(canvas);
            var tr = new TextureRect { Texture = _vp.GetTexture(), Material = _mat, MouseFilter = Control.MouseFilterEnum.Ignore };
            tr.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            canvas.AddChild(tr);
        }

        public override void _Process(double delta)
        {
            var cam = GetViewport().GetCamera3D();
            if (cam == null) return;
            var sz = (Vector2I)GetViewport().GetVisibleRect().Size;
            if (_vp.Size != sz) _vp.Size = sz;
            // match the active camera exactly so the mask aligns pixel-for-pixel with the main render
            _vpCam.GlobalTransform = cam.GlobalTransform;
            _vpCam.Fov = cam.Fov;
            _vpCam.Near = cam.Near;
            _vpCam.Far = cam.Far;
            _vpCam.KeepAspect = cam.KeepAspect;
            _mat.SetShaderParameter("outline_color", new Vector3(WorldItem.FocusColor.R, WorldItem.FocusColor.G, WorldItem.FocusColor.B));
        }
    }
}
