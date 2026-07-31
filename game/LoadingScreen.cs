using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Full-screen load overlay, matched to retail Loading/LoadingUI.cs + SleekLoadingScreenProgressBar (with
    // ROUNDED bars per master). Two loading-screen TYPES:
    //   * "map"    (per-level load): ONE bar -- the current PHASE (left) + progress (right).
    //   * "launch" (asset WARMUP): TWO bars -- top names the current STAGE (Vehicles / Zombies / Terrain / …) with
    //             its within-stage count, bottom shows the current asset NAME + overall %. Driven by the real
    //             Warmup (SetTotal/SetStage/SetStatus/Advance) as it preloads the vanilla core meshes.
    // Both: a full-screen screenshot BACKGROUND + a TIP line. No title. Mode from LoadingScreen.NextMode (code) →
    // UG_LOADMODE (env) → "map". Bars = a rounded dark track + a rounded light fill growing from the left.
    public partial class LoadingScreen : CanvasLayer
    {
        public static string NextMode;   // set in code before `new LoadingScreen()` to pick the type; consumed on _Ready.

        static readonly string[] LaunchPool = { "pei", "washington", "russia", "germany", "yukon" };

        static readonly string[] Tips =
        {
            "Zombies are drawn to gunfire — a silencer keeps you hidden.",
            "Cook raw meat at a campfire before eating it, or risk food poisoning.",
            "Bandages and dressings stop bleeding; a splint sets a broken leg.",
            "Vehicles need both fuel and a working battery to run.",
            "A claim flag keeps your base from decaying while you're away.",
            "Aim for the head — headshots deal far more damage.",
            "Cold and heat both drain your health. Dress for the biome.",
            "Airdrops fall in periodically — grab the loot before someone else does.",
        };

        Label _timings;
        Panel _mapFill; Label _mapStatus, _mapPct;                       // map: single bar
        Panel _laFill1, _laFill2; Label _laStage, _laCount, _laName, _laPct;   // launch: top=stage, bottom=overall
        Control _root;
        int _total = 1, _done;
        double _timingsHold = -1.0;
        bool _loading = true, _launch;
        float _barL, _barW, _barH, _yMap, _y1, _y2;

        static Texture2D LoadBg(string file)
        {
            string p = ProjectSettings.GlobalizePath($"res://content/menu/{file}");
            if (!System.IO.File.Exists(p)) return null;
            var img = new Image();
            if (img.Load(p) != Error.Ok) return null;
            return ImageTexture.CreateFromImage(img);
        }

        static Panel RoundedBar(float x, float y, float w, float h, Color color)
        {
            var p = new Panel { Position = new Vector2(x, y), Size = new Vector2(w, h) };
            var sb = new StyleBoxFlat { BgColor = color };
            sb.SetCornerRadiusAll(9);
            p.AddThemeStyleboxOverride("panel", sb);
            return p;
        }

        // one rounded bar at Y: dark rounded track + a light rounded fill (grows from the left) + left desc + right value.
        (Panel fill, Label desc, Label val) BuildBar(float y, string descText)
        {
            _root.AddChild(RoundedBar(_barL, y, _barW, _barH, new Color(0.13f, 0.14f, 0.16f, 0.9f)));   // track
            var fill = RoundedBar(_barL, y, 0f, _barH, new Color(0.80f, 0.82f, 0.85f, 0.95f));           // fill (width set by SetFill)
            _root.AddChild(fill);
            var desc = new Label { Text = descText, VerticalAlignment = VerticalAlignment.Center };
            desc.Position = new Vector2(_barL + 18f, y); desc.Size = new Vector2(_barW - 320f, _barH);
            desc.AddThemeFontSizeOverride("font_size", 22); desc.AddThemeColorOverride("font_color", new Color(0.97f, 0.97f, 0.97f));
            desc.AddThemeColorOverride("font_outline_color", Colors.Black); desc.AddThemeConstantOverride("outline_size", 6);
            _root.AddChild(desc);
            var val = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            val.Position = new Vector2(_barL + _barW - 298f, y); val.Size = new Vector2(280f, _barH);
            val.AddThemeFontSizeOverride("font_size", 22); val.AddThemeColorOverride("font_color", new Color(0.97f, 0.97f, 0.97f));
            val.AddThemeColorOverride("font_outline_color", Colors.Black); val.AddThemeConstantOverride("outline_size", 6);
            _root.AddChild(val);
            return (fill, desc, val);
        }

        static void SetFill(Panel fill, float fullW, float h, float f)
        {
            if (fill != null) fill.Size = new Vector2(fullW * Mathf.Clamp(f, 0f, 1f), h);
        }

        public override void _Ready()
        {
            Layer = 128;
            _root = new Control(); _root.SetAnchorsPreset(Control.LayoutPreset.FullRect); AddChild(_root);

            string mode = NextMode ?? System.Environment.GetEnvironmentVariable("UG_LOADMODE") ?? "map";
            NextMode = null;
            _launch = mode == "launch";
            string mapKey = System.Environment.GetEnvironmentVariable("UG_LOADMAP") ?? "pei";
            string bgKey = _launch ? LaunchPool[(int)(GD.Randi() % (uint)LaunchPool.Length)] : mapKey;
            var shot = LoadBg($"mappreview_{bgKey}.png");
            if (shot != null)
            {
                var shotRect = new TextureRect { Texture = shot, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize };
                shotRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                _root.AddChild(shotRect);
                var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.35f) };
                dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                _root.AddChild(dim);
            }
            else
            {
                var bg = new ColorRect { Color = new Color(0.04f, 0.05f, 0.07f) };
                bg.SetAnchorsPreset(Control.LayoutPreset.FullRect); _root.AddChild(bg);
            }

            _barL = 48f; _barW = 2560f - 96f; _barH = 36f;
            float tipY;
            if (_launch)
            {
                _y1 = 1440f - 156f; _y2 = 1440f - 110f;
                Panel f1, f2; Label d1, d2, v1, v2;
                (f1, d1, v1) = BuildBar(_y1, "Loading"); _laFill1 = f1; _laStage = d1; _laCount = v1;
                (f2, d2, v2) = BuildBar(_y2, "…"); _laFill2 = f2; _laName = d2; _laPct = v2;
                _laCount.Text = "(0 / 0)"; _laPct.Text = "0%";
                tipY = _y2 + _barH + 10f;
            }
            else
            {
                _yMap = 1440f - 116f;
                Panel f; Label d, v;
                (f, d, v) = BuildBar(_yMap, "Loading level"); _mapFill = f; _mapStatus = d; _mapPct = v; _mapPct.Text = "0%";
                tipY = _yMap + _barH + 12f;
            }

            var tip = new Label { Text = "TIP:   " + Tips[(int)(GD.Randi() % (uint)Tips.Length)], HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tip.Position = new Vector2(_barL, tipY); tip.Size = new Vector2(_barW, 40f);
            tip.AddThemeFontSizeOverride("font_size", 20); tip.AddThemeColorOverride("font_color", new Color(0.86f, 0.88f, 0.92f));
            tip.AddThemeColorOverride("font_outline_color", Colors.Black); tip.AddThemeConstantOverride("outline_size", 4);
            _root.AddChild(tip);

            _timings = new Label { Text = "", Visible = false, Position = new Vector2(16, 12) };
            _timings.AddThemeColorOverride("font_color", new Color(0.85f, 0.88f, 0.6f));
            _timings.AddThemeColorOverride("font_outline_color", Colors.Black);
            _timings.AddThemeConstantOverride("outline_size", 6);
            AddChild(_timings);

            SetProcess(true);
        }

        public void SetTotal(int n) => _total = Mathf.Max(1, n);

        public void SetStatus(string s)
        {
            if (_launch) { if (_laName != null) _laName.Text = "Loading  " + s.Replace('_', ' '); }
            else { if (_mapStatus != null) _mapStatus.Text = s; }
        }

        public void Advance()
        {
            _done++;
            float f = Mathf.Clamp((float)_done / _total, 0f, 1f);
            if (_launch) { SetFill(_laFill2, _barW, _barH, f); if (_laPct != null) _laPct.Text = $"{Mathf.RoundToInt(f * 100f)}%"; }
            else { SetFill(_mapFill, _barW, _barH, f); if (_mapPct != null) _mapPct.Text = $"{Mathf.RoundToInt(f * 100f)}%"; }
        }

        // launch: name the current STAGE + its within-stage count on the TOP bar.
        public void SetStage(string name, int x, int n)
        {
            if (!_launch) return;
            if (_laStage != null) _laStage.Text = "Loading  " + name;
            if (_laCount != null) _laCount.Text = $"({x} / {n})";
            SetFill(_laFill1, _barW, _barH, n > 0 ? (float)x / n : 0f);
        }

        public void Finish(Dictionary<string, double> timings)
        {
            _loading = false;
            double total = 0; foreach (var kv in timings) total += kv.Value;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"LOAD {total:0} ms");
            foreach (var kv in timings) sb.AppendLine($"  {kv.Key,-10} {kv.Value,6:0} ms  ({(total > 0 ? kv.Value / total * 100 : 0):0}%)");
            GD.Print("[load] " + sb.ToString().Replace("\n", " | "));
            if (_root != null) _root.Visible = false;
            if (_timings != null) { _timings.Text = sb.ToString(); _timings.Visible = true; }
            _timingsHold = 8.0;
        }

        public override void _Process(double delta)
        {
            if (_loading) return;
            if (_timingsHold > 0.0)
            {
                _timingsHold -= delta;
                if (_timingsHold <= 0.0) QueueFree();
            }
        }
    }
}
