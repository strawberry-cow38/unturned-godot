using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // A hard cap on how many point lights cast shadows at once.
    //
    // WHY THIS EXISTS. The map places 324 point lights (148 streetlights, 63 traffic, 34 ceiling strips, 79
    // lamps) and every one ships with ShadowEnabled = false, which is why light passes through walls. You
    // cannot simply turn them on: a shadowed OmniLight3D is not one shadow map, it is a CUBE -- six renders of
    // everything in range. OmniShadowMode is never set anywhere in this project, so every one of them is on
    // the engine default, and the default is Cube: verified 2026-08-10 by constructing an OmniLight3D under
    // 4.6 headless (omni_shadow_mode == 1 == SHADOW_CUBE). DualParaboloid would be 2 faces instead of 6.
    //
    // WHAT THAT COSTS is NOT six renders per light per frame -- an earlier version of this comment said that
    // and it was wrong. Godot caches positional shadow maps. Per the docs, each frame per light it (1) checks
    // the light is on an atlas slot of the right size, re-rendering if it must move, (2) re-renders if any
    // object affecting the map changed, and (3) otherwise LEAVES THE SHADOW UNTOUCHED. A static light over
    // static geometry is close to free after its first frame.
    //
    // The real bill is the GRANULARITY of that invalidation: it is all-or-nothing, so one zombie walking
    // through re-renders every caster in the light's radius across all six faces, static props included.
    // Splitting static from dynamic casters is godot-proposals#4635 and is not implemented; the lever that
    // does exist is Light3D.ShadowCasterMask, which shrinks what a re-render has to draw.
    //
    // So shadows are still a BUDGET, for two reasons that survive the correction: the sun is already capped to
    // a 40 m cascade because a pulled-back third-person car camera tanked the GPU at 100 m, and a town full of
    // moving zombies invalidates constantly, which is exactly the case where caching stops saving anything.
    // Note the budget is itself an invalidation source -- toggling ShadowEnabled on, and any atlas slot resize
    // it causes, both force a re-render. That is what SwapMargin and Interval are really paying for.
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

        // BUDGETING FACES, NOT LIGHTS.
        //
        // A slot used to cost the same whatever took it, and that is not what the renderer charges. A shadowed spot
        // renders ONE depth map; a shadowed omni renders a CUBE, six. So "8 shadowed lights" meant anywhere between 8
        // and 48 shadow renders depending purely on which fixtures you happened to be standing near -- and the
        // expensive case is indoors, among the lamps, which is exactly where the frame is already tight.
        //
        // So charge each light what it actually costs and cap the TOTAL. Two limits apply together:
        //   * FaceBudgetFor -- half of what the old light budget could cost at each tier (8 lights x 6 = 48 -> 24).
        //     In an all-omni room that is 4 shadowed lamps instead of 8, a straight halving where it hurt most.
        //   * MaxLightsFor -- the old per-tier light count, unchanged, so a street of 1-face spots cannot balloon to
        //     24 shadowed lights just because they are individually cheap. Fixed per-light overhead is real even
        //     when the face count is not.
        // Both are ceilings, so this can only ever hand out fewer shadows than before, never more.
        public static int FaceBudgetFor(GraphicsOptions.ShadowQuality q) => BudgetFor(q) * 3;
        public static int MaxLightsFor(GraphicsOptions.ShadowQuality q) => BudgetFor(q);

        /// <summary>What one shadowed light costs in shadow-map renders. Omni is a cube unless it has been put in
        /// dual-paraboloid mode; Godot's default is Cube (verified under 4.6 headless), and this reads the real
        /// property rather than assuming, so flipping a fixture to DualParaboloid immediately buys more slots.</summary>
        public static int FacesOf(Node3D n) => n switch
        {
            SpotLight3D => 1,
            OmniLight3D o => o.OmniShadowMode == OmniLight3D.ShadowMode.DualParaboloid ? 2 : 6,
            _ => 6,   // unknown light type: assume the expensive one, never the cheap one
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
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
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

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            _clock += (float)delta;
            if (_clock < Interval) return;
            _clock = 0f;
            Rebalance();
        }

        /// <summary>Pick the winners and apply. Public so a test can drive it without waiting real seconds.</summary>
        public void Rebalance()
        {
            int faceBudget = FaceBudgetFor(GraphicsOptions.Shadows);
            int maxLights = MaxLightsFor(GraphicsOptions.Shadows);
            _cam = GetViewport()?.GetCamera3D();

            if (faceBudget <= 0 || maxLights <= 0 || _cam == null)
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

            // Take lights in rank order while BOTH ceilings hold. A light that does not fit is skipped rather than
            // ending the loop: a cheap spot further down the list can still fit in the faces a nearer cube left over,
            // and dropping it would waste budget for no reason.
            var winners = new List<Node3D>();
            int faces = 0;
            foreach (var (node, score) in scored)
            {
                if (winners.Count >= maxLights) break;
                int cost = FacesOf(node);
                if (faces + cost > faceBudget) continue;
                winners.Add(node); faces += cost;
            }

            // Hysteresis: an incumbent keeps its slot unless a challenger is clearly better.
            if (_holding.Count > 0)
            {
                for (int i = 0; i < winners.Count; i++)
                {
                    if (_holding.Contains(winners[i])) continue;
                    // winners[i] is a challenger -- find the incumbent it would displace
                    Node3D victim = null;
                    foreach (var h in _holding)
                        if (!winners.Contains(h)) { victim = h; break; }
                    if (victim == null || !GodotObject.IsInstanceValid(victim)) continue;
                    float cs = Score(winners[i], from, fwd), vs = Score(victim, from, fwd);
                    if (cs > vs * SwapMargin) winners[i] = victim;   // not decisively better -> incumbent stays
                }

                // A swap can trade a 1-face spot for a 6-face cube, so the ceiling has to be re-checked afterwards
                // -- otherwise hysteresis quietly reintroduces exactly the overspend the face budget exists to stop.
                faces = 0;
                for (int i = 0; i < winners.Count; i++)
                {
                    int cost = FacesOf(winners[i]);
                    if (faces + cost > faceBudget) { winners.RemoveAt(i); i--; continue; }
                    faces += cost;
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
        public int FacesHeldForTest { get { int f = 0; foreach (var h in _holding) if (GodotObject.IsInstanceValid(h)) f += FacesOf(h); return f; } }
        public bool IsHoldingForTest(Node3D n) => _holding.Contains(n);
    }
}
