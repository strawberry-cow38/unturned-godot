using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Readable lore NOTES (source: an object with Interactability=Note, whose English.dat carries
    // Interactability_Text_Line_0..N). The note objects are shared across maps, so their text is keyed by GUID
    // and loaded ONCE from content/note_texts.tsv (tools/extract_notes.py) -- both PEI and Washington place a
    // subset of the same 52 notes. A note placement becomes a NoteBody: it renders the note mesh, carries the
    // text, and the player's look-ray focuses it (white outline) so F reads it in the NoteReader panel.

    // guid -> (name, text lines). Loaded lazily from the baked tsv.
    public static class NoteTexts
    {
        static Dictionary<string, (string Name, string[] Lines)> _cache;
        static Dictionary<string, (string, string[])> Map => _cache ??= Load();
        static Dictionary<string, (string, string[])> Load()
        {
            var d = new Dictionary<string, (string, string[])>();
            string path = ProjectSettings.GlobalizePath("res://content/note_texts.tsv");
            if (!System.IO.File.Exists(path)) return d;
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                var c = line.Split('\t');
                if (c.Length < 2) continue;
                var lines = c.Length > 2 ? c[2..] : System.Array.Empty<string>();
                d[c[0].ToLowerInvariant()] = (c[1], lines);
            }
            return d;
        }
        public static bool TryGet(string guid, out string name, out string[] lines)
        {
            if (guid != null && Map.TryGetValue(guid.ToLowerInvariant(), out var v)) { name = v.Item1; lines = v.Item2; return true; }
            name = null; lines = null; return false;
        }
    }

    // A placed, readable note. StaticBody3D on the SEE-THROUGH look layer (bit 6, like a stump): the look-ray hits
    // it to focus, but it never blocks movement or bullet LOS -- a scrap of paper shouldn't wall you off.
    public partial class NoteBody : StaticBody3D
    {
        public string NoteName { get; private set; }
        public string[] Lines { get; private set; }
        MeshInstance3D _glow;   // OutlineOverlay silhouette, shown while focused

        public static NoteBody Spawn(Node parent, Mesh mesh, Transform3D xf, string name, string[] lines, float cull, Material mat)
        {
            var n = new NoteBody { NoteName = name, Lines = lines, CollisionLayer = 1u << 6, CollisionMask = 0 };
            n.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, VisibilityRangeEnd = cull, VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled });
            var aabb = mesh.GetAabb();
            var size = aabb.Size;
            // a paper note is nearly flat -- pad the thin axis so the look-ray has something to hit
            size = new Vector3(Mathf.Max(size.X, 0.05f), Mathf.Max(size.Y, 0.05f), Mathf.Max(size.Z, 0.05f));
            n.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = aabb.GetCenter() });
            parent.AddChild(n);
            n.GlobalTransform = xf;
            return n;
        }

        // Whole-note white outline while looked at, same affordance doors/props use (OutlineOverlay layer 19).
        public void SetLookFocused(bool on)
        {
            if (on && _glow == null)
            {
                var mi = this.GetChildOrNull<MeshInstance3D>(0);
                if (mi?.Mesh != null) AddChild(_glow = OutlineOverlay.MakeOutline(mi.Mesh));
            }
            if (_glow != null) OutlineOverlay.ShowOutline(on, Colors.White, _glow);
        }
    }

    // Full-screen reading panel: the note's title + its lines, on a dimmed backdrop. F or Esc closes it. Owned by
    // PlayerController (one instance), shown via Show(note).
    public partial class NoteReader : CanvasLayer
    {
        Control _root;
        Label _title, _body;

        public override void _Ready()
        {
            Layer = 91;   // above the map (90), under the F1 console (100)
            _root = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_root);
            var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.78f), MouseFilter = Control.MouseFilterEnum.Stop };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(dim);

            var paper = new PanelContainer { Position = new Vector2(0, 0) };
            var pv = new StyleBoxFlat { BgColor = new Color(0.93f, 0.90f, 0.82f) };
            pv.SetContentMarginAll(26); pv.SetBorderWidthAll(2); pv.BorderColor = new Color(0.2f, 0.18f, 0.13f);
            paper.AddThemeStyleboxOverride("panel", pv);
            _root.AddChild(paper);
            _paper = paper;

            var col = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
            col.AddThemeConstantOverride("separation", 12);
            paper.AddChild(col);
            _title = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _title.AddThemeFontSizeOverride("font_size", 24);
            _title.AddThemeColorOverride("font_color", new Color(0.12f, 0.10f, 0.07f));
            col.AddChild(_title);
            col.AddChild(new HSeparator());
            _body = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(560, 0) };
            _body.AddThemeFontSizeOverride("font_size", 17);
            _body.AddThemeColorOverride("font_color", new Color(0.15f, 0.13f, 0.10f));
            col.AddChild(_body);
            var hint = new Label { Text = "F / Esc to close", HorizontalAlignment = HorizontalAlignment.Center };
            hint.AddThemeFontSizeOverride("font_size", 12);
            hint.AddThemeColorOverride("font_color", new Color(0.4f, 0.37f, 0.3f));
            col.AddChild(hint);

            GetViewport().SizeChanged += Layout;
        }
        PanelContainer _paper;

        void Layout()
        {
            if (_paper == null) return;
            var vp = GetViewport().GetVisibleRect().Size;
            var sz = _paper.Size;
            _paper.Position = new Vector2((vp.X - sz.X) * 0.5f, Mathf.Max(40f, (vp.Y - sz.Y) * 0.5f));
        }

        public bool IsOpen => _root != null && _root.Visible;

        public override void _Input(InputEvent e)   // Esc closes an open note (F-close is driven by PlayerController's F chain)
        {
            if (IsOpen && e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { Close(); GetViewport().SetInputAsHandled(); }
        }

        public void Show(NoteBody note)
        {
            if (note == null) return;
            _title.Text = (note.NoteName ?? "Note").Replace("Note - ", "");
            _body.Text = note.Lines != null ? string.Join("\n", note.Lines) : "";
            _root.Visible = true;
            CallDeferred(nameof(Layout));
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        public void Close()
        {
            if (_root != null) _root.Visible = false;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }
}
