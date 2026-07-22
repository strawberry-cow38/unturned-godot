using Godot;

namespace UnturnedGodot
{
    // The standalone Asset Factory editor (a main-menu entry, sibling of the map editor).
    // Compose meshes into one asset + hand-place colliders / volumes / named points, then
    // save a self-contained .assetbundle the game auto-loads. Reuses EditorCamera (fly cam)
    // and, from Phase 2b, EditorGizmo for part manipulation.
    //
    // Phase 2a (here): the scene + fly cam + live preview of a working bundle + shell UI.
    // Phase 2b: add-part (mesh browser) + select/gizmo/delete + Save.
    public partial class AssetFactoryEditor : Node3D
    {
        public System.Action OnExit;

        AssetBundle _bundle = new() { Name = "new_asset", Type = "prop" };
        Node3D _assetRoot;          // the live-rebuilt composition (AssetBundleLoader output)
        EditorCamera _cam;
        Label _title;

        public void Setup(string loadPath = null)
        {
            var env = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.53f, 0.67f, 0.86f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.92f, 0.92f, 0.94f), AmbientLightEnergy = 1.1f,
            };
            AddChild(new WorldEnvironment { Environment = env });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52f, -38f, 0f), LightEnergy = 1.25f, ShadowEnabled = true });
            AddChild(BuildGroundGrid());

            _cam = new EditorCamera { Position = new Vector3(3.5f, 2.6f, 4.5f), RotationDegrees = new Vector3(-22f, 38f, 0f), Current = true };
            AddChild(_cam);

            if (loadPath != null)
            {
                var b = AssetBundle.Load(loadPath);
                if (b != null) _bundle = b;
                else GD.Print($"[assetfactory] could not load {loadPath} — starting empty");
            }
            Rebuild();
            BuildUI();
            GD.Print($"[assetfactory] editor up: {_bundle.Name} [{_bundle.Type}] — {_bundle.Parts.Count} parts");
        }

        // Rebuild the live composition from _bundle (called after every edit in Phase 2b).
        void Rebuild()
        {
            if (_assetRoot != null && IsInstanceValid(_assetRoot)) _assetRoot.QueueFree();
            _assetRoot = AssetBundleLoader.Build(_bundle);
            AddChild(_assetRoot);
        }

        static Node3D BuildGroundGrid()
        {
            var mat = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.45f, 0.47f, 0.52f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = false,
            };
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines, mat);
            const int N = 12;
            for (int i = -N; i <= N; i++)
            {
                im.SurfaceAddVertex(new Vector3(i, 0, -N)); im.SurfaceAddVertex(new Vector3(i, 0, N));
                im.SurfaceAddVertex(new Vector3(-N, 0, i)); im.SurfaceAddVertex(new Vector3(N, 0, i));
            }
            im.SurfaceEnd();
            var holder = new Node3D { Name = "Grid" };
            holder.AddChild(new MeshInstance3D { Mesh = im });
            return holder;
        }

        void BuildUI()
        {
            var layer = new CanvasLayer();
            AddChild(layer);
            _title = new Label { Text = $"Asset Factory — {_bundle.Name}  [{_bundle.Type}]", Position = new Vector2(18, 12) };
            _title.AddThemeFontSizeOverride("font_size", 22);
            layer.AddChild(_title);
            layer.AddChild(new Label
            {
                Text = "Phase 2a — fly-cam preview.  add-part / gizmo / save land next.",
                Position = new Vector2(18, 44),
            });
            var exit = new Button { Text = "Exit", Position = new Vector2(18, 76), CustomMinimumSize = new Vector2(96, 34) };
            exit.Pressed += () => OnExit?.Invoke();
            layer.AddChild(exit);
        }
    }
}
