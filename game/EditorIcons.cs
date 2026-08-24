using Godot;

namespace UnturnedGodot
{
    // Icons for the building palette, RASTERISED IN CODE rather than shipped as PNGs.
    //
    // strawberry_cow asked for "icons / pictures instead of words on buttons of various shapes and sizes",
    // Sims-like. Drawing them here rather than adding art means no asset pipeline, no .import sidecars to
    // forget (a .tsv taught this repo that lesson once already), and they redraw at whatever size a button
    // asks for. The trade is honest: these are geometric glyphs, not illustrations. Swapping in real art
    // later is a one-line change per icon.
    //
    // Everything is drawn into a square Image at the requested size and handed back as an ImageTexture, so
    // the caller can just set Button.Icon.
    public static partial class EditorIcons
    {
        public enum Glyph
        {
            Wall, Room, Floor, Roof, Foundation, Delete, Move,
            Door, Window, TallWindow, Garage, Porch, Vent,
            Paint, Import, Bake,
        }

        static readonly System.Collections.Generic.Dictionary<(Glyph, int), ImageTexture> Cache = new();

        public static ImageTexture Get(Glyph g, int size = 40)
        {
            if (Cache.TryGetValue((g, size), out var had)) return had;
            var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
            img.Fill(new Color(0, 0, 0, 0));
            Draw(img, g, size);
            var tex = ImageTexture.CreateFromImage(img);
            Cache[(g, size)] = tex;
            return tex;
        }

        // the palette the glyphs are drawn in -- one ink, one accent, so the row reads as a set
        static readonly Color Ink = new(0.90f, 0.90f, 0.88f);
        static readonly Color Accent = new(1f, 0.78f, 0.25f);
        static readonly Color Dim = new(0.55f, 0.55f, 0.55f);

        static void Draw(Image im, Glyph g, int s)
        {
            float u = s / 40f;                     // everything below is authored against a 40px box
            int P(float v) => Mathf.RoundToInt(v * u);
            switch (g)
            {
                case Glyph.Wall:                   // a standing wall, seen face on, with courses
                    Rect(im, P(11), P(6), P(18), P(28), Ink);
                    for (int i = 1; i < 4; i++) Rect(im, P(11), P(6) + i * P(7), P(18), Mathf.Max(1, P(1)), Dim);
                    break;
                case Glyph.Room:                   // four walls in plan
                    Frame(im, P(7), P(9), P(26), P(22), Mathf.Max(2, P(3)), Ink);
                    break;
                case Glyph.Floor:                  // a slab seen nearly edge on, with its thickness showing
                    // A rhombus alone reads as a DIAMOND, not as a floor -- that is what the first version
                    // drew. Giving it a visible near edge underneath is what makes it a slab.
                    Quad(im, P(7), P(21), P(20), P(15), P(33), P(21), P(20), P(27), Ink);
                    Quad(im, P(7), P(21), P(20), P(27), P(20), P(31), P(7), P(25), Dim);
                    Quad(im, P(33), P(21), P(20), P(27), P(20), P(31), P(33), P(25), Dim);
                    break;
                case Glyph.Roof:                   // a gable
                    Tri(im, P(20), P(8), P(6), P(24), P(34), P(24), Ink);
                    Rect(im, P(6), P(24), P(28), Mathf.Max(2, P(3)), Dim);
                    break;
                case Glyph.Foundation:             // a slab with the buried skirt under it
                    Rect(im, P(8), P(14), P(24), P(6), Ink);
                    Rect(im, P(8), P(20), P(24), P(12), Dim);
                    for (int i = 0; i < 4; i++) Rect(im, P(10) + i * P(6), P(20), Mathf.Max(1, P(1)), P(12), Ink);
                    break;
                case Glyph.Delete:                 // an X
                    Line(im, P(10), P(10), P(30), P(30), Mathf.Max(2, P(3)), Accent);
                    Line(im, P(30), P(10), P(10), P(30), Mathf.Max(2, P(3)), Accent);
                    break;
                case Glyph.Move:                   // a four-way arrow
                    Rect(im, P(19), P(8), Mathf.Max(2, P(2)), P(24), Ink);
                    Rect(im, P(8), P(19), P(24), Mathf.Max(2, P(2)), Ink);
                    break;

                // ---- openings: the same wall square, with the hole that preset cuts ----------------
                case Glyph.Door:       Opening(im, s, 0.30f, 0.42f, 0.58f, bottom: true); break;
                case Glyph.Window:     Opening(im, s, 0.50f, 0.34f, 0.30f, bottom: false); break;
                case Glyph.TallWindow: Opening(im, s, 0.30f, 0.46f, 0.24f, bottom: false); break;
                case Glyph.Garage:     Opening(im, s, 0.66f, 0.40f, 0.55f, bottom: true); break;
                case Glyph.Porch:      Opening(im, s, 0.52f, 0.44f, 0.58f, bottom: true); break;
                case Glyph.Vent:       Opening(im, s, 0.20f, 0.20f, 0.42f, bottom: false); break;

                case Glyph.Paint:                  // a roller
                    Rect(im, P(8), P(9), P(20), P(8), Accent);
                    Rect(im, P(17), P(17), Mathf.Max(2, P(2)), P(8), Ink);
                    Rect(im, P(13), P(25), P(10), P(7), Ink);
                    break;
                case Glyph.Import:                 // an arrow into a box
                    Frame(im, P(8), P(18), P(24), P(14), Mathf.Max(2, P(2)), Ink);
                    Rect(im, P(19), P(6), Mathf.Max(2, P(2)), P(12), Accent);
                    Tri(im, P(20), P(20), P(14), P(12), P(26), P(12), Accent);
                    break;
                case Glyph.Bake:                   // a box with a tick
                    Frame(im, P(7), P(10), P(26), P(22), Mathf.Max(2, P(2)), Ink);
                    Line(im, P(13), P(21), P(18), P(26), Mathf.Max(2, P(3)), Accent);
                    Line(im, P(18), P(26), P(28), P(15), Mathf.Max(2, P(3)), Accent);
                    break;
            }
        }

        /// <summary>A wall square with a hole in it -- the openings all share this so they read as one family
        /// and differ only in the shape of the hole, which is the only thing that actually differs.</summary>
        static void Opening(Image im, int s, float w, float h, float top, bool bottom)
        {
            int P(float v) => Mathf.RoundToInt(v * s / 40f);
            Rect(im, P(7), P(7), P(26), P(26), Dim);
            int hw = Mathf.RoundToInt(P(26) * w), hh = Mathf.RoundToInt(P(26) * h);
            int hx = P(7) + (P(26) - hw) / 2;
            int hy = bottom ? P(7) + P(26) - hh : P(7) + Mathf.RoundToInt(P(26) * top);
            Rect(im, hx, hy, hw, hh, new Color(0, 0, 0, 0));
            Frame(im, hx, hy, hw, hh, 1, Accent);
        }

        // ---- the smallest rasteriser that draws all of the above ------------------------------------

        static void Px(Image im, int x, int y, Color c)
        {
            if (x < 0 || y < 0 || x >= im.GetWidth() || y >= im.GetHeight()) return;
            im.SetPixel(x, y, c);
        }

        static void Rect(Image im, int x, int y, int w, int h, Color c)
        {
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                    Px(im, x + i, y + j, c);
        }

        static void Frame(Image im, int x, int y, int w, int h, int t, Color c)
        {
            Rect(im, x, y, w, t, c);
            Rect(im, x, y + h - t, w, t, c);
            Rect(im, x, y, t, h, c);
            Rect(im, x + w - t, y, t, h, c);
        }

        static void Line(Image im, int x0, int y0, int x1, int y1, int t, Color c)
        {
            int dx = Mathf.Abs(x1 - x0), dy = -Mathf.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
            while (true)
            {
                Rect(im, x0 - t / 2, y0 - t / 2, t, t, c);
                if (x0 == x1 && y0 == y1) break;
                int e2 = err * 2;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        /// <summary>Filled triangle, by scanline. Half-space test per pixel inside the bounding box -- slow
        /// and completely fine for a 40px glyph drawn once.</summary>
        static void Tri(Image im, int ax, int ay, int bx, int by, int cx, int cy, Color col)
        {
            int x0 = Mathf.Min(ax, Mathf.Min(bx, cx)), x1 = Mathf.Max(ax, Mathf.Max(bx, cx));
            int y0 = Mathf.Min(ay, Mathf.Min(by, cy)), y1 = Mathf.Max(ay, Mathf.Max(by, cy));
            float Edge(int px, int py, int qx, int qy, int rx, int ry)
                => (rx - px) * (qy - py) - (ry - py) * (qx - px);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float w0 = Edge(ax, ay, bx, by, x, y);
                    float w1 = Edge(bx, by, cx, cy, x, y);
                    float w2 = Edge(cx, cy, ax, ay, x, y);
                    bool neg = w0 < 0 || w1 < 0 || w2 < 0, pos = w0 > 0 || w1 > 0 || w2 > 0;
                    if (!(neg && pos)) Px(im, x, y, col);
                }
        }

        static void Quad(Image im, int ax, int ay, int bx, int by, int cx, int cy, int dx, int dy, Color col)
        {
            Tri(im, ax, ay, bx, by, cx, cy, col);
            Tri(im, ax, ay, cx, cy, dx, dy, col);
        }
    }
}
