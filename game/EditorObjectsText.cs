using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot
{
    public partial class EditorObjects
    {
        public EditorTextProps TextTool { get; private set; }
        public ModeledTextProp SelectedText => Primary as ModeledTextProp;
        string _textLoadError;
        readonly List<TextPropRecord> _textCopies = new();
        public override void _Ready()
        {
            if (_editor == null || _cam == null) return;
            TextTool = new EditorTextProps(_editor, this, _cam); AddChild(TextTool);
        }
        public void OpenTextTool() => TextTool?.Open(SelectedText);
        public ModeledTextProp FindText(string id) => _placed.OfType<ModeledTextProp>().FirstOrDefault(p => IsInstanceValid(p) && p.Id == id);
        void RebuildText(ModeledTextProp node, TextPropStyle style)
        {
            foreach (var pair in _pickToObj.Where(k => k.Value == node).ToArray()) _pickToObj.Remove(pair.Key);
            node.Rebuild(style, pickable: true);
            if (node.PickBody != null) _pickToObj[node.PickBody.GetRid()] = node;
        }
        public ModeledTextProp PutText(TextPropRecord record)
        {
            var node = FindText(record.Id);
            if (node == null) { node = new ModeledTextProp { Id = record.Id }; _world.AddChild(node); _placed.Add(node); }
            RebuildText(node, record.Style); node.GlobalTransform = record.Unpack();
            AttachGizmo(); PositionMarkers(); return node;
        }
        public ModeledTextProp CreateText(TextPropStyle style, Transform3D transform)
        {
            var rec = new TextPropRecord { Style = style.Clean(), Transform = TextPropRecord.Pack(transform) };
            var n = PutText(rec); Select(n); PlaceName = null;
            _editor.PushUndo("place text", () => RemoveProp(FindText(rec.Id)), () => Select(PutText(rec)));
            return n;
        }
        public bool EditText(string id, TextPropStyle style)
        {
            var n = FindText(id); if (n == null) return false;
            var before = n.Snapshot(); RebuildText(n, style); var after = n.Snapshot();
            Select(n); _editor.PushUndo("edit text", () => Select(PutText(before)), () => Select(PutText(after))); return true;
        }
        int SaveTextProps()
        {
            // Never replace an unreadable/newer sidecar with the empty list left by a failed load.
            if (_textLoadError != null) throw new System.IO.IOException("Text-prop save blocked: " + _textLoadError);
            var records = _placed.OfType<ModeledTextProp>().Where(IsInstanceValid).Select(p => p.Snapshot()).ToList();
            TextPropStore.Write(_editor.MapName, records); return records.Count;
        }
        void LoadTextProps()
        {
            try { foreach (var rec in TextPropStore.Read(_editor.MapName)) PutText(rec); }
            catch (Exception e) { _textLoadError = e.Message; Log.Err("[editor-text] load failed; sidecar protected: " + e.Message); }
        }
        // Text records carry stable IDs so older style/move undo actions still find a prop after delete/undo.
        sealed class PlacementCopy
        {
            public string Guid; public Transform3D Transform; public TextPropRecord Text;
        }
        PlacementCopy CapturePlacement(Node3D node)
        {
            if (node is ModeledTextProp text) return new PlacementCopy { Text = text.Snapshot() };
            return node.HasMeta("guid") ? new PlacementCopy { Guid = (string)node.GetMeta("guid"), Transform = node.GlobalTransform } : null;
        }
        Node3D RestorePlacement(PlacementCopy p) => p.Text != null ? PutText(p.Text) : RePlace(p.Guid, p.Transform);
        void DeleteSelectionWithText()
        {
            var snapshots = _selection.Select(CapturePlacement).Where(s => s != null).ToList();
            var current = new List<Node3D>(_selection); int omitted = current.Count - snapshots.Count;
            foreach (var n in current) RemoveProp(n); Select(null);
            _editor.PushUndo("delete props/text", () => {
                current.Clear(); _selection.Clear();
                foreach (var s in snapshots) { var n = RestorePlacement(s); if (n != null) { current.Add(n); _selection.Add(n); } }
                AttachGizmo(); RefreshMarkers();
                if (omitted > 0) Log.Err($"[editor] {omitted} non-text custom props cannot be restored by legacy undo");
            }, () => { RemoveCaptured(current,snapshots); Select(null); });
        }
        void PasteCopiesWithText()
        {
            var snapshots = _textCopies.Select(r => new PlacementCopy { Text = r.Copy(freshId: true) }).ToList();
            snapshots.AddRange(_copies.Select(p => new PlacementCopy { Guid = p.guid, Transform = p.x }));
            var current = new List<Node3D>();
            void Paste()
            {
                current.Clear(); _selection.Clear();
                foreach (var s in snapshots) { var n = RestorePlacement(s); if (n != null) { current.Add(n); _selection.Add(n); } }
                AttachGizmo(); RefreshMarkers();
            }
            Paste(); _editor.PushUndo("paste props/text", () => RemoveCaptured(current,snapshots), Paste);
        }
        void RemoveCaptured(List<Node3D> current,List<PlacementCopy> snapshots)
        {
            foreach (var s in snapshots) if (s.Text != null) RemoveProp(FindText(s.Text.Id));
            foreach (var n in current.ToArray()) if (IsInstanceValid(n) && n is not ModeledTextProp) RemoveProp(n);
        }
        public void SelectText(ModeledTextProp node) => Select(node);
        public void SetTextEditingOverlay(bool open)
        {
            if (open) { _gizmo.Visible = false; foreach (var m in _markers) m.Visible = false; }
            else { AttachGizmo(); foreach (var m in _markers) m.Visible = true; }
        }
    }
}