using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // Floating damage numbers for the gun playground (strawberry: "shows floating damage numbers with each hit").
    //
    // Lives under a CanvasLayer like HitmarkerHUD so _Draw runs in screen space, but each number keeps its WORLD
    // anchor and re-projects every frame -- so it stays stuck to the spot you hit while you strafe, instead of
    // being frozen at the screen position the hit happened to have. A number behind the camera is culled rather
    // than drawn mirrored at the wrong edge, which is what IsPositionBehind guards.
    public partial class DamageNumbers : Node2D
    {
        public static DamageNumbers Instance;
        const float Life = 1.1f;
        const float RiseMetres = 0.55f;   // world-space climb, so the drift reads the same at 10 m and 200 m

        struct Num { public Vector3 World; public float Damage; public float Age; public TargetDummy.HitZone Zone; }
        readonly List<Num> _nums = new();
        Font _font;

        public override void _Ready()
        {
            Instance = this;
            _font = ThemeDB.FallbackFont;
        }
        public override void _ExitTree() { if (Instance == this) Instance = null; }

        public void Show(Vector3 world, float damage, TargetDummy.HitZone zone)
            => _nums.Add(new Num { World = world, Damage = damage, Age = 0f, Zone = zone });

        public override void _Process(double delta)
        {
            for (int i = _nums.Count - 1; i >= 0; i--)
            {
                var n = _nums[i];
                n.Age += (float)delta;
                if (n.Age >= Life) { _nums.RemoveAt(i); continue; }
                _nums[i] = n;
            }
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (_font == null || _nums.Count == 0) return;
            var cam = GetViewport().GetCamera3D();
            if (cam == null) return;
            foreach (var n in _nums)
            {
                float t = n.Age / Life;
                Vector3 world = n.World + Vector3.Up * (RiseMetres * t);
                if (cam.IsPositionBehind(world)) continue;   // behind the eye -- UnprojectPosition would mirror it on screen
                Vector2 p = cam.UnprojectPosition(world);
                // head red / torso white / legs dim, matching the hitmarker's body-vs-crit language
                Color c = n.Zone == TargetDummy.HitZone.Head ? new Color(1f, 0.25f, 0.2f)
                        : n.Zone == TargetDummy.HitZone.Torso ? new Color(1f, 1f, 1f)
                        : new Color(0.72f, 0.72f, 0.72f);
                c.A = 1f - t * t;   // hold legible, then fade off quickly at the end
                int size = n.Zone == TargetDummy.HitZone.Head ? 30 : 24;
                string s = n.Damage >= 10f ? Mathf.RoundToInt(n.Damage).ToString() : n.Damage.ToString("0.#");
                Vector2 half = _font.GetStringSize(s, HorizontalAlignment.Left, -1, size) * 0.5f;
                DrawString(_font, p - half + new Vector2(1f, 1f), s, HorizontalAlignment.Left, -1, size, new Color(0f, 0f, 0f, c.A * 0.7f));   // drop shadow: numbers sit against sky and terrain both
                DrawString(_font, p - half, s, HorizontalAlignment.Left, -1, size, c);
            }
        }

        public int DebugCount => _nums.Count;   // test hook: a hit should put exactly one number on screen
    }
}
