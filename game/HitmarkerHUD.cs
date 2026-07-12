using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // Source hitmarker (SleekHitmarker + PlayerUI.hitmark): 4 diagonal ticks flash at the crosshair on a confirmed hit and
    // spread outward over HIT_TIME = 0.33s. White (alpha 0.5) for a body hit (EPlayerHit.ENTITIY), red (alpha 0.5) for a
    // headshot (EPlayerHit.CRITICAL = source limb == SKULL). Lives under a CanvasLayer so _Draw runs in screen space.
    public partial class HitmarkerHUD : Node2D
    {
        public static HitmarkerHUD Instance;
        const float HitTime = 0.33f;   // source PlayerUI.HIT_TIME

        struct Mark { public float Age; public bool Crit; }
        readonly List<Mark> _marks = new();

        public override void _Ready() => Instance = this;
        public override void _ExitTree() { if (Instance == this) Instance = null; }

        // crit = headshot -> red marker (+ the source plays Sounds/Hit.mp3, wired by the caller)
        public void Show(bool crit) => _marks.Add(new Mark { Age = 0f, Crit = crit });

        public override void _Process(double delta)
        {
            if (_marks.Count == 0) return;
            for (int i = _marks.Count - 1; i >= 0; i--)
            {
                var m = _marks[i]; m.Age += (float)delta;
                if (m.Age > HitTime) _marks.RemoveAt(i); else _marks[i] = m;
            }
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (_marks.Count == 0) return;
            Vector2 c = GetViewport().GetVisibleRect().Size * 0.5f;   // crosshair = screen centre
            foreach (var m in _marks)
            {
                float t = m.Age / HitTime;                            // 0..1 over the marker's life
                float spread = Mathf.Lerp(5f, 15f, t);               // ticks fan outward (source BASE_OFFSET -> TARGET_OFFSET)
                var col = m.Crit ? new Color(1f, 0f, 0f, 0.5f) : new Color(1f, 1f, 1f, 0.5f);   // red headshot / white body, a=0.5
                for (int k = 0; k < 4; k++)                          // 4 diagonal ticks (NE / SE / SW / NW)
                {
                    float ang = Mathf.Pi / 4f + k * (Mathf.Pi / 2f);
                    var dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                    Vector2 mid = c + dir * spread;
                    DrawLine(mid - dir * 4f, mid + dir * 4f, col, 2f, true);   // a short tick along the diagonal
                }
            }
        }
    }
}
