using Godot;

namespace UnturnedGodot
{
    // SWOOP (strawberry 2026-09-08: "make the inv, craft, skills, map uis all 'swoop' in/out instead of
    // appearing/disappearing"). One animation shared by all four screens rather than four hand-rolled ones --
    // they are the same gesture, and four copies would drift apart the first time one of them was tweaked.
    //
    // Two things move, and they are deliberately separate:
    //   * the whole screen FADES (root.Modulate.a), which carries the frosted backdrop with it, and
    //   * the PANEL slides up into place, which is the bit that reads as a swoop.
    // The slide is applied to a wrapper Control that sits between the root and the panel and does nothing else.
    // Sliding the panel directly would fight each screen's own Layout(), which sets that panel's Position from
    // the viewport size; sliding the CanvasLayer instead would drag the full-screen backdrop off its edge and
    // leave an unblurred strip. A wrapper owns one property that no layout code touches.
    //
    // It processes ONLY while animating -- a sixth of a second at a time -- and switches itself off again, so it
    // is not worth a permanent TickHub entry the way the always-on world systems are.
    public partial class MenuSwoop : Node
    {
        public static float Seconds = 0.085f;  // master 2026-09-09: "make the opening/close animation of the ui faster". Still two or three frames of travel at 30fps, so it reads as motion rather than a cut -- but a menu you open constantly should never be something you wait for.
        public static float Rise = 54f;        // pixels the panel travels on its way in

        CanvasLayer _layer;
        Control _root;      // faded
        Control _slider;    // slid
        // The frosted backdrop, hidden the instant a screen starts CLOSING (strawberry 2026-09-09: "the ui goes
        // dark when switching tabs"). Switching tabs closes one screen and opens another in the same frame, and
        // for the length of the swoop BOTH were drawing a full-screen blur -- the second one samples the first
        // one's already-tinted output, so the tint lands twice and the whole screen dips. Only the screen that is
        // arriving should own the backdrop; the one leaving has nothing left to blur behind.
        Control _backdrop;
        float _t;           // 0 = fully out, 1 = fully in
        int _dir;           // +1 opening, -1 closing, 0 idle

        /// <summary>Wire a screen up. <paramref name="slider"/> is a full-rect Control that owns nothing but the
        /// offset -- put the screen's panel inside it.</summary>
        public static MenuSwoop Attach(CanvasLayer layer, Control root, Control slider, Control backdrop = null)
        {
            var s = new MenuSwoop { _layer = layer, _root = root, _slider = slider, _backdrop = backdrop, Name = "Swoop" };
            layer.AddChild(s);
            return s;
        }

        public override void _Ready() => SetProcess(false);

        public bool Animating => _dir != 0;

        /// <summary>Start (or reverse into) the opening animation. The caller has already made the screen visible.</summary>
        public void In()
        {
            _dir = 1;
            if (_backdrop != null && GodotObject.IsInstanceValid(_backdrop)) _backdrop.Visible = true;
            SetProcess(true);
            Apply();
        }

        /// <summary>Start the closing animation. The screen stays VISIBLE until it finishes -- the caller's own
        /// IsOpen flag should already be false by then, so input routing stops immediately while the pixels catch
        /// up. Returns false if there is nothing to animate, so the caller can hide immediately instead.</summary>
        public bool Out()
        {
            if (_root == null || !GodotObject.IsInstanceValid(_root)) return false;
            // Straight away, not faded out with the rest: a blur that is half-faded is still a second blur pass.
            if (_backdrop != null && GodotObject.IsInstanceValid(_backdrop)) _backdrop.Visible = false;
            _dir = -1;
            SetProcess(true);
            return true;
        }

        /// <summary>Jump straight to shut, no animation -- for teardown and for the harness paths that want a
        /// screen simply gone.</summary>
        public void Snap(bool open)
        {
            if (_backdrop != null && GodotObject.IsInstanceValid(_backdrop)) _backdrop.Visible = open;
            _t = open ? 1f : 0f;
            _dir = 0;
            SetProcess(false);
            Apply();
        }

        public override void _Process(double delta)
        {
            if (_dir == 0) { SetProcess(false); return; }
            _t = Mathf.Clamp(_t + _dir * (float)delta / Mathf.Max(0.01f, Seconds), 0f, 1f);
            Apply();
            if (_t >= 1f && _dir > 0) { _dir = 0; SetProcess(false); }
            else if (_t <= 0f && _dir < 0)
            {
                _dir = 0; SetProcess(false);
                if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.Visible = false;
                if (_root != null && GodotObject.IsInstanceValid(_root)) _root.Visible = false;
                Closed?.Invoke();
            }
        }

        /// <summary>Fired once the closing animation has actually finished and the screen is hidden. The map uses
        /// it to put the mouse back where it belongs, which must not happen while the panel is still on screen.</summary>
        public event System.Action Closed;

        void Apply()
        {
            if (_root == null || !GodotObject.IsInstanceValid(_root)) return;
            // Ease OUT on the way in and IN on the way out, so it arrives softly and leaves decisively -- the same
            // asymmetry every menu that feels right uses.
            float k = _dir >= 0 ? 1f - Mathf.Pow(1f - _t, 3f) : _t * _t;
            _root.Modulate = new Color(1f, 1f, 1f, k);
            if (_slider != null && GodotObject.IsInstanceValid(_slider))
                _slider.Position = new Vector2(0f, (1f - k) * Rise);
        }
    }
}
