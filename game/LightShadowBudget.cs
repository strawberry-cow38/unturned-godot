using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A hard cap on how many point lights cast shadows at once.
    //
    // WHY THIS EXISTS. The map places 324 point lights (148 streetlights, 63 traffic, 34 ceiling strips, 79
    // lamps) and every one ships with ShadowEnabled = false, which is why light passes through walls. You
    // cannot simply turn them on: a shadowed OmniLight3D is not one shadow map, it is a CUBE -- six renders of
    // everything in range, per light, per frame. The ten or so visible at once in a lit town would be sixty
    // extra passes. The sun is the only shadowed light in the game and it is already capped to a 40 m cascade
    // because a pulled-back third-person car camera tanked the GPU at 100 m.
    //
    // So shadows become a BUDGET: each update, the few lights that matter most to this camera get shadows and
    // everyone else does not. A light 60 m away casts a shadow nobody can resolve and costs exactly as much as
    // one at arm's length -- distance is invisible to the renderer's bill, which is the whole reason the naive
    // "just enable them" approach falls over.
    //
    // Lights opt in by joining the `shadowbudget` group; nothing central has to know they exist.
    public partial class LightShadowBudget : Node
    {
        public const string Group = "shadowbudget";

        /// <summary>How many lights may cast at once, per quality tier. Off means off -- a zero budget is how
        /// you actually stop paying, the same reasoning GraphicsOptions.ApplyShadows uses for its atlas.</summary>
        public static int BudgetFor(GraphicsOptions.ShadowQuality q) => q switch
        {
            GraphicsOptions.ShadowQuality.Off => 0,
            GraphicsOptions.ShadowQuality.Low => 2,
            GraphicsOptions.ShadowQuality.Medium => 4,
            GraphicsOptions.ShadowQuality.High => 6,
            _ => 8,
        };

        /// <summary>Beyond this a shadow is not resolvable, so it is never worth a cube. Cheap pre-filter that
        /// keeps the sort small in a dense town.</summary>
        public const float MaxShadowDistance = 28f;

        /// <summary>A challenger must beat an incumbent by this factor to take its slot. Without it, two lights
        /// at nearly equal distance trade places every update and their shadows visibly flick on and off as you
        /// stand still -- the classic ranked-budget failure, and worse than having no shadows at all.</summary>
        public const float SwapMargin = 0.85f;

        /// <summary>Re-rank a few times a second, not every frame. The set changes at walking pace and toggling
        /// a light's shadow forces the renderer to find atlas space for it.</summary>
        public const float Interval = 0.2f;

        float _clock;
        readonly List<Node3D> _holding = new();          // who currently owns a slot
        Camera3D _cam;

        public override void _Ready()
        {
            // Positional (omni/spot) shadows land in their OWN atlas, which nothing in this project had ever
            // configured -- GraphicsOptions only sizes the DIRECTIONAL one. Without this the budget would hand
            // out shadows that quietly fight for default atlas space and look broken at the edges.
            var vp = GetViewport();
            if (vp != null)
            {
                vp.PositionalShadowAtlasSize = 2048;
                vp.PositionalShadowAtlasQuad0 = Viewport.PositionalShadowAtlasQuadrantSubdiv.Subdiv4;
                vp.PositionalShadowAtlasQuad1 = Viewport.PositionalShadowAtlasQuadrantSubdiv.Subdiv4;
            }
        }

        public override void _Process(double delta)
        {
            using var _prof = Prof.Scope("LightShadowBudget");
            _clock += (float)delta;
            if (_clock < Interval) return;
            _clock = 0f;
            Rebalance();
        }

        /// <summary>Pick the winners and apply. Public so a test can drive it without waiting real seconds.</summary>
        public void Rebalance()
        {
            int budget = BudgetFor(GraphicsOptions.Shadows);
            _cam = GetViewport()?.GetCamera3D();

            if (budget <= 0 || _cam == null)
            {
                foreach (var l in _holding) SetShadow(l, false);
                _holding.Clear();
                return;
            }

            var from = _cam.GlobalPosition;
            var fwd = -_cam.GlobalTransform.Basis.Z;

            var scored = new List<(Node3D Node, float Score)>();
            foreach (var n in GetTree().GetNodesInGroup(Group))
            {
                if (n is not Node3D l || !GodotObject.IsInstanceValid(l) || !l.IsInsideTree()) continue;
                if (l is Light3D lit && lit.LightEnergy <= 0.001f) continue;   // an unlit lamp casting a shadow is pure waste
                var d = l.GlobalPosition.DistanceTo(from);
                if (d > MaxShadowDistance) continue;
                // Behind the camera is deprioritised, not banned: a light at your back still throws your own
                // shadow forwards, which is exactly the shadow you notice. Ranked worse, not excluded.
                bool ahead = (l.GlobalPosition - from).Normalized().Dot(fwd) > -0.2f;
                scored.Add((l, ahead ? d : d * 2.2f));
            }
            scored.Sort((a, b) => a.Score.CompareTo(b.Score));

            // Hysteresis: an incumbent keeps its slot unless a challenger is clearly better.
            var winners = new List<Node3D>();
            foreach (var (node, score) in scored)
            {
                if (winners.Count >= budget) break;
                winners.Add(node);
            }
            if (_holding.Count > 0)
            {
                for (int i = 0; i < winners.Count; i++)
                {
                    if (_holding.Contains(winners[i])) continue;
                    // winners[i] is a challenger -- find the incumbent it would displace
                    Node3D victim = null;
                    foreach (var h in _holding)
                        if (!winners.Contains(h) || winners.IndexOf(h) >= budget) { victim = h; break; }
                    if (victim == null || !GodotObject.IsInstanceValid(victim)) continue;
                    float cs = Score(winners[i], from, fwd), vs = Score(victim, from, fwd);
                    if (cs > vs * SwapMargin) winners[i] = victim;   // not decisively better -> incumbent stays
                }
            }

            foreach (var h in _holding) if (!winners.Contains(h)) SetShadow(h, false);
            foreach (var w in winners) SetShadow(w, true);
            _holding.Clear(); _holding.AddRange(winners);
        }

        static float Score(Node3D l, Vector3 from, Vector3 fwd)
        {
            if (!GodotObject.IsInstanceValid(l)) return float.MaxValue;
            float d = l.GlobalPosition.DistanceTo(from);
            return (l.GlobalPosition - from).Normalized().Dot(fwd) > -0.2f ? d : d * 2.2f;
        }

        static void SetShadow(Node3D n, bool on)
        {
            if (!GodotObject.IsInstanceValid(n)) return;
            if (n is Light3D l && l.ShadowEnabled != on) l.ShadowEnabled = on;
        }

        public int HoldingCountForTest => _holding.Count;
        public bool IsHoldingForTest(Node3D n) => _holding.Contains(n);
    }
}
