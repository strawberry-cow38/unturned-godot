using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace UnturnedGodot
{
    /// <summary>The approved vanilla-style letter meshes, not a Label3D or a substitute OS font.</summary>
    public static class ModeledTextFont
    {
        public const string Asset = "res://content/text/vanilla_sign_font.json";
        sealed class Glyph { public Vector3[] V; public int[][] T; public float Advance; }
        static Dictionary<char, Glyph> _glyphs;
        static float _space;
        static void Load()
        {
            if (_glyphs != null) return;
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(Asset));
            var root = doc.RootElement; var loaded = new Dictionary<char, Glyph>();
            float depth = root.GetProperty("depth").GetSingle();
            foreach (var entry in root.GetProperty("glyphs").EnumerateObject())
            {
                var g = entry.Value;
                loaded.Add(entry.Name[0], new Glyph {
                    // Authoring is Z-up/front -Y. Runtime is Y-up/front +Z. Depth normalized to 1.
                    V = g.GetProperty("vertices").EnumerateArray().Select(v => new Vector3(v[0].GetSingle(), v[2].GetSingle(), -v[1].GetSingle() / depth)).ToArray(),
                    T = g.GetProperty("triangles").EnumerateArray().Select(t => t.EnumerateArray().Select(i => i.GetInt32()).ToArray()).ToArray(),
                    Advance = g.GetProperty("advance").GetSingle()
                });
            }
            _space = root.GetProperty("space_advance").GetSingle(); _glyphs = loaded;
        }
        public sealed class Result { public ArrayMesh Mesh; public Aabb Bounds; public int Triangles, Replaced; public float Width, Height; }
        public static Result Build(TextPropStyle input)
        {
            Load(); var s = input.Clean(); var result = new Result();
            string text = s.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");
            var lines = text.Split('\n'); var verts = new List<Vector3>(); var widths = new List<float>();
            float scaleX = s.Height * s.WidthScale;
            foreach (string line in lines)
            {
                float w = 0;
                for (int i = 0; i < line.Length; i++)
                {
                    char ch = line[i];
                    w += (ch == ' ' ? _space * s.WordSpacing : (_glyphs.TryGetValue(ch, out var gl) ? gl : _glyphs['?']).Advance) * scaleX;
                    if (i + 1 < line.Length) w += s.Tracking * s.Height;
                }
                widths.Add(w);
            }
            for (int row = 0; row < lines.Length; row++)
            {
                float x = s.Align == 1 ? -widths[row] / 2 : s.Align == 2 ? -widths[row] : 0;
                float y = -row * s.Height * s.LineSpacing;
                foreach (char ch in lines[row])
                {
                    if (ch == ' ') { x += _space * s.WordSpacing * scaleX + s.Tracking * s.Height; continue; }
                    if (!_glyphs.TryGetValue(ch, out var g)) { g = _glyphs['?']; result.Replaced++; }
                    foreach (var t in g.T)
                        foreach (int index in t)
                        { var v = g.V[index]; verts.Add(new Vector3(x + v.X * scaleX, y + v.Y * s.Height, v.Z * s.Depth)); }
                    x += g.Advance * scaleX + s.Tracking * s.Height;
                }
            }
            if (verts.Count == 0) return result;
            float minY = verts.Min(v => v.Y), maxY = verts.Max(v => v.Y);
            float offset = s.Anchor == 1 ? -maxY : s.Anchor == 2 ? -(maxY + minY) / 2 : s.Anchor == 3 ? -minY : 0;
            var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); var hi = -lo;
            for (int i = 0; i < verts.Count; i += 3)
            {
                Vector3 a = verts[i] + Vector3.Up * offset, b = verts[i + 1] + Vector3.Up * offset, c = verts[i + 2] + Vector3.Up * offset;
                var normal = (b - a).Cross(c - a).Normalized();
                foreach (var v in new[] { a, c, b }) { st.SetNormal(normal); st.AddVertex(v); lo = lo.Min(v); hi = hi.Max(v); }
            }
            st.Index(); result.Mesh = st.Commit(); st.Dispose();
            result.Bounds = new Aabb(lo, hi - lo); result.Triangles = verts.Count / 3;
            result.Width = widths.Max(); result.Height = hi.Y - lo.Y; return result;
        }
    }

    public sealed class TextPropStyle
    {
        public string Text { get; set; } = "Hardware";
        public float Height { get; set; } = 0.6f;
        public float WidthScale { get; set; } = 1;
        public float Depth { get; set; } = 0.12f;
        public float Tracking { get; set; } = 0;
        public float WordSpacing { get; set; } = 1;
        public float LineSpacing { get; set; } = 1.45f;
        public int Align { get; set; } = 0; // left, center, right
        public int Anchor { get; set; } = 0; // baseline, top, middle, bottom
        public string Color { get; set; } = "646464";
        public bool Solid { get; set; }
        public bool Shadows { get; set; } = true;
        public TextPropStyle Copy() => (TextPropStyle)MemberwiseClone();
        static float F(float v, float min, float max, float fallback) => float.IsFinite(v) ? Mathf.Clamp(v, min, max) : fallback;
        public TextPropStyle Clean()
        {
            var s = Copy(); s.Text = (s.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            s.Text = string.Join("\n", s.Text.Split('\n').Take(16)); if (s.Text.Length > 512) s.Text = s.Text[..512];
            s.Height = F(s.Height, .02f, 20, .6f); s.WidthScale = F(s.WidthScale, .1f, 5, 1);
            s.Depth = F(s.Depth, .001f, 3, .12f); s.Tracking = F(s.Tracking, -.5f, 2, 0);
            s.WordSpacing = F(s.WordSpacing, .1f, 5, 1); s.LineSpacing = F(s.LineSpacing, .5f, 5, 1.45f);
            s.Align = Math.Clamp(s.Align, 0, 2); s.Anchor = Math.Clamp(s.Anchor, 0, 3);
            s.Color = Godot.Color.HtmlIsValid(s.Color ?? "") ? new Color(s.Color).ToHtml(false) : "646464";
            return s;
        }
    }
}