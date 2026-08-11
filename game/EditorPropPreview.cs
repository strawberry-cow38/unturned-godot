using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Props rendered to textures for the Objects palette: a thumbnail beside every catalog row, and a larger
    // turntable of the one that is selected. strawberry_cow asked for "visual preview icons" and "maybe a 3d
    // spinning preview of the selected prop" -- both are the same job (put a prop in front of a camera), so
    // they are one class rather than two that drift.
    //
    // TWO SubViewports total, not one per row. The catalog is 505 entries; a viewport each would be 505 render
    // targets for a list that shows about twenty at a time. Instead one small viewport renders thumbnails ON
    // DEMAND, one per couple of frames, and caches them by name -- and one larger viewport runs the turntable.
    //
    // The isolation gotcha, learned in InventoryUI's paperdoll and repeated here because it is silent: with
    // OwnWorld3D the world's environment does NOT reach the viewport. No lights and no WorldEnvironment of its
    // own means the prop renders BLACK, which reads as "the thumbnail failed" rather than "the scene is unlit".
    public partial class EditorPropPreview : Node
    {
        public const int ThumbPx = 64;
        public const int StagePx = 200;

        readonly System.Func<string, ArrayMesh> _meshFor;
        readonly System.Func<string, StandardMaterial3D> _matFor;

        // ---- thumbnails ------------------------------------------------------------------------------
        SubViewport _thumbVp;
        Node3D _thumbPivot;
        MeshInstance3D _thumbMi;
        Camera3D _thumbCam;
        readonly Dictionary<string, ImageTexture> _thumbs = new();
        readonly List<string> _queue = new();
        readonly HashSet<string> _queued = new();
        string _inFlight;
        int _settle;

        /// <summary>Fired when a thumbnail finishes, so the list can attach it to the row it belongs to. The
        /// list cannot just poll every row every frame -- that is 505 dictionary hits a frame for a result that
        /// changes twice a second.</summary>
        public event System.Action<string, ImageTexture> ThumbReady;

        // ---- turntable -------------------------------------------------------------------------------
        SubViewport _stageVp;
        Node3D _stagePivot;
        MeshInstance3D _stageMi;
        Camera3D _stageCam;
        string _stageName;
        float _spin;

        public SubViewport Stage => _stageVp;

        public EditorPropPreview(System.Func<string, ArrayMesh> meshFor, System.Func<string, StandardMaterial3D> matFor)
        { _meshFor = meshFor; _matFor = matFor; }

        public override void _Ready()
        {
            (_thumbVp, _thumbPivot, _thumbMi, _thumbCam) = MakeStage(ThumbPx, SubViewport.UpdateMode.Disabled);
            (_stageVp, _stagePivot, _stageMi, _stageCam) = MakeStage(StagePx, SubViewport.UpdateMode.Always);
        }

        /// <summary>One isolated little studio: viewport, camera, key + fill, ambient, and a PIVOT holding an
        /// empty mesh instance to swap props through.
        ///
        /// The pivot is not ceremony. Prop geometry is not centred on its own origin -- Door_Pine's sits at
        /// x -2.35..+0.10 -- so rotating the mesh node makes an off-origin prop ORBIT the origin and swing out
        /// of frame instead of spinning in place. Dress() offsets the mesh inside the pivot so the geometry's
        /// centre lands ON the pivot, and the turntable turns the pivot.</summary>
        (SubViewport, Node3D, MeshInstance3D, Camera3D) MakeStage(int px, SubViewport.UpdateMode mode)
        {
            var vp = new SubViewport
            {
                Size = new Vector2I(px, px),
                OwnWorld3D = true,               // isolated: the editor's own world must not leak into a thumbnail
                TransparentBg = true,
                Msaa3D = Viewport.Msaa.Msaa4X,
                RenderTargetUpdateMode = mode,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
            };
            AddChild(vp);

            var cam = new Camera3D { Fov = 35f, Current = true };
            vp.AddChild(cam);

            // Lit from the camera's side so a prop reads as solid rather than a silhouette. Two lights, because
            // one leaves the shadowed side pure black and props are mostly flat-shaded boxes.
            vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-32f, 145f, 0f), LightEnergy = 1.15f });
            vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-10f, -40f, 0f), LightEnergy = 0.5f });
            vp.AddChild(new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color,
                    BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = new Color(0.55f, 0.55f, 0.55f),
                    AmbientLightEnergy = 1.0f,
                    TonemapMode = Godot.Environment.ToneMapper.Aces,
                },
            });

            var pivot = new Node3D();
            vp.AddChild(pivot);
            var mi = new MeshInstance3D();
            pivot.AddChild(mi);
            return (vp, pivot, mi, cam);
        }

        /// <summary>UG_ICONDUMP=1: report every thumbnail's size and opaque-pixel count as it resolves. The
        /// palette cannot tell a MISSING icon from a BLANK one -- both render as empty space -- and they have
        /// different causes, so the count is the only thing that separates them.</summary>
        public static bool DebugDump = System.Environment.GetEnvironmentVariable("UG_ICONDUMP") == "1";

        const int SettleFrames = 2;    // frames between requesting the single render and reading it back
        const int RetryLimit = 3;      // blank readbacks re-rendered before the emptiness is believed
        int _tries;

        /// <summary>Does this readback contain anything at all? Strided -- a thumbnail with ink has thousands of
        /// opaque pixels, so sampling every 3rd finds it, and the cost lands on every thumbnail.</summary>
        static bool HasInk(Image img)
        {
            for (int y = 0; y < img.GetHeight(); y += 3)
                for (int x = 0; x < img.GetWidth(); x += 3)
                    if (img.GetPixel(x, y).A > 0.15f) return true;
            return false;
        }

        public int RetriesForTest { get; private set; }

        static int OpaqueCount(Image img)
        {
            int n = 0;
            for (int y = 0; y < img.GetHeight(); y++)
                for (int x = 0; x < img.GetWidth(); x++)
                    if (img.GetPixel(x, y).A > 0.15f) n++;
            return n;
        }

        /// <summary>UG_ICONDUMP: queue every catalog name so the dump covers the whole palette, not just the
        /// dozen rows that happen to be scrolled into view.</summary>
        public void DebugQueueAll(System.Collections.Generic.IEnumerable<string> names)
        {
            foreach (var n in names) if (n != null && !_thumbs.ContainsKey(n) && _queued.Add(n)) _queue.Add(n);
            GD.Print($"[icon] queued {_queue.Count} thumbnails for dump");
        }

        /// <summary>Ask for a prop's thumbnail. Returns it immediately if it is already drawn; otherwise queues
        /// it and raises ThumbReady later. Safe to call every time a row scrolls into view -- a name already
        /// drawn or already queued costs one hash lookup.</summary>
        public ImageTexture Thumb(string name)
        {
            if (name == null) return null;
            if (_thumbs.TryGetValue(name, out var had)) return had;   // may be null: a prop that failed to draw is remembered, not retried forever
            if (_queued.Add(name)) _queue.Add(name);
            return null;
        }

        /// <summary>Drop every cached picture. A re-bake under the same name must not serve the previous
        /// prop's thumbnail -- the same reason EditorObjects clears its mesh cache on ReloadCatalog.</summary>
        public void Clear()
        {
            _thumbs.Clear(); _queue.Clear(); _queued.Clear();
            _inFlight = null; _settle = 0;
            if (_thumbVp != null) _thumbVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        }

        /// <summary>Show a prop on the turntable. Null parks it.</summary>
        public void ShowOnStage(string name)
        {
            if (name == _stageName) return;
            _stageName = name;
            _spin = 0f;
            _stagePivot.Basis = Basis.Identity;
            if (name == null || !Dress(_stagePivot, _stageMi, _stageCam, name)) _stageMi.Mesh = null;
        }

        /// <summary>Put a prop on a pivot and frame the camera on it. The distance comes from the mesh's own
        /// AABB rather than a constant, because the catalog runs from a doorknob to a whole house -- one fixed
        /// distance makes half the palette a dot and the other half a wall of texture.</summary>
        bool Dress(Node3D pivot, MeshInstance3D mi, Camera3D cam, string name)
        {
            var mesh = _meshFor(name);
            if (mesh == null) { mi.Mesh = null; return false; }
            mi.Mesh = mesh;
            mi.MaterialOverride = _matFor(name);

            // Props are authored with height on +Z and stood up by the placement's 270 pitch. A preview has no
            // placement, so stand it up here or every prop lies on its face.
            var stand = new Basis(Vector3.Right, Mathf.DegToRad(270f));
            var ab = mesh.GetAabb();
            // Offset so the geometry's centre sits ON the pivot -- see MakeStage. Without this the turntable
            // orbits and the thumbnail framing is off by however far the prop's origin is from its bulk.
            mi.Transform = new Transform3D(stand, -(stand * ab.GetCenter()));

            float radius = ab.Size.Length() * 0.5f;
            if (radius < 1e-3f) radius = 1f;
            float dist = radius / Mathf.Tan(Mathf.DegToRad(cam.Fov * 0.5f)) * 1.15f;   // 15% margin so nothing clips the frame

            // A 3/4 view: straight-on turns every box prop into a rectangle and the palette becomes a wall of
            // indistinguishable grey squares.
            var dir = new Vector3(0.62f, 0.44f, 0.65f).Normalized();
            cam.Position = pivot.Position + dir * dist;
            cam.LookAtFromPosition(cam.Position, pivot.Position, Vector3.Up);
            cam.Near = Mathf.Max(0.05f, dist - radius * 2f);
            cam.Far = dist + radius * 4f;
            return true;
        }

        public override void _Process(double delta)
        {
            if (_stageMi?.Mesh != null)
            {
                _spin += (float)delta * 0.6f;   // slow: a fast turntable is harder to read, not more alive
                _stagePivot.Basis = new Basis(Vector3.Up, _spin);
            }
            PumpThumbs();
        }

        /// <summary>Where the staged prop's geometric centre actually sits, in the viewport's space. The
        /// turntable aims the camera at the pivot, so this is how far off-target the prop is drawn.
        ///
        /// Exposed because the failure it guards is invisible to any check on the prop itself: a prop whose
        /// geometry is off its own origin (Door_Pine sits at x -2.35..+0.10) still has the right mesh, the
        /// right material and the right size while ORBITING out of frame. The distance from the pivot is the
        /// only place it shows. Measured after a rotation as well as at rest -- at rest catches a missing
        /// recentre, a rotation catches the subtler version where the prop IS recentred but the turntable
        /// turns about some other point.</summary>
        public Vector3 StageCentreForTest()
        {
            if (_stageMi?.Mesh == null) return Vector3.Zero;
            return _stagePivot.Transform * (_stageMi.Transform * _stageMi.Mesh.GetAabb().GetCenter());
        }

        /// <summary>Turn the turntable by hand, so a test can check the prop spins rather than orbits without
        /// waiting real seconds for _Process to get there.</summary>
        public void SpinToForTest(float radians)
        {
            _spin = radians;
            if (_stagePivot != null) _stagePivot.Basis = new Basis(Vector3.Up, radians);
        }

        /// <summary>One thumbnail at a time: dress the stage, render a single frame, then read it back.
        ///
        /// The readback cannot happen on the frame the render is requested -- UpdateMode.Once draws during the
        /// FOLLOWING draw pass, so grabbing the texture immediately returns the previous prop's pixels. That
        /// failure is nasty because every thumbnail still appears, just shifted by one prop, which reads as a
        /// mislabelled catalog rather than a timing bug. Hence the settle counter.</summary>
        void PumpThumbs()
        {
            if (_inFlight != null)
            {
                if (--_settle > 0) return;
                var img = _thumbVp.GetTexture()?.GetImage();
                // AN EMPTY READBACK IS A TIMING MISS, NOT A VERDICT. Measured: Demo_House came back with 1905
                // opaque pixels on one boot and 0 on the next, same mesh, same catalog -- so a blank thumbnail is
                // the render not having landed yet, not a prop that has nothing to draw. Because the result was
                // cached either way, one unlucky frame blanked that prop for the whole session, which is what
                // strawberry was looking at: "a lot of prop's small icons dont load ... their BIG spinning 3d
                // preview shows fine". The stage never hits it because it runs UpdateMode.Always.
                //
                // So: re-render instead of believing it, up to RetryLimit. Bounded because a prop CAN legitimately
                // be invisible, and an unbounded retry would re-queue it forever and starve every icon behind it.
                if (img != null && img.GetWidth() > 0 && !HasInk(img) && _tries < RetryLimit)
                {
                    _tries++; RetriesForTest++;
                    if (DebugDump) GD.Print($"[icon] {_inFlight,-24} blank, retry {_tries}/{RetryLimit}");
                    _thumbVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
                    _settle = SettleFrames + _tries;   // give each retry a longer runway than the attempt that failed
                    return;
                }
                if (img != null && img.GetWidth() > 0)
                {
                    var tex = ImageTexture.CreateFromImage(img);
                    _thumbs[_inFlight] = tex;
                    // A thumbnail can succeed at every step and still be EMPTY -- the readback returns a valid
                    // image of nothing. In the palette that is indistinguishable from no icon at all, and the two
                    // have completely different causes, so count the ink rather than trusting the texture.
                    if (DebugDump) GD.Print($"[icon] {_inFlight,-24} {img.GetWidth()}x{img.GetHeight()} opaque={OpaqueCount(img)}");
                    ThumbReady?.Invoke(_inFlight, tex);
                }
                else { _thumbs[_inFlight] = null; if (DebugDump) GD.Print($"[icon] {_inFlight,-24} READBACK-NULL"); }   // remember the failure too, or it re-queues forever
                _inFlight = null; _tries = 0;
                return;
            }

            if (_queue.Count == 0) return;
            var name = _queue[0];
            _queue.RemoveAt(0);
            _queued.Remove(name);
            _thumbPivot.Basis = Basis.Identity;
            if (!Dress(_thumbPivot, _thumbMi, _thumbCam, name)) { _thumbs[name] = null; if (DebugDump) GD.Print($"[icon] {name,-24} NO-MESH"); return; }
            _thumbVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            _inFlight = name;
            _tries = 0;
            _settle = SettleFrames;
        }
    }
}
