using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    /// <summary>
    /// A ring of wedges around the screen centre pointing toward recent damage sources (master 2026-09-03:
    /// "add directional visual hit feedback when you get hurt by something"). One mark per hit, drawn at the
    /// hit's WORLD-space horizontal direction re-projected against the camera EVERY FRAME -- not the screen
    /// angle at the moment of the hit -- so a mark tracks correctly while the player keeps turning during its
    /// fade, the same way retail's arrow does. Getting this wrong is the difference between "that came from
    /// behind me" staying true and it silently rotating with your view.
    ///
    /// See PlayerController.ShowHurtCosmetics for both callers: TakeDamage (SP/loopback, where `dir` is exact
    /// because the local sim computed it) and the PlayerHurt wire handler (a real MP client, where `dir` comes
    /// from the server's PlayerHurtEvent).
    /// </summary>
    public partial class HurtDirectionIndicator : Control
    {
        public const float MarkTime = 3.0f;   // how long a wedge stays visible before it's gone
        const float FadeStart = 1.5f;   // begins fading at half life, full opacity before that -- a fresh hit should read as sharp, not already dissolving
        const float Radius = 90f;       // px from screen centre
        const float WedgeHalfAngle = 22f;   // degrees either side of the exact bearing -- wide enough to read as a direction, narrow enough that two hits from different sides don't merge into a ring

        public Camera3D Cam;   // set by HUD to the owning player's camera; direction is projected against ITS yaw

        struct Mark { public Vector3 Dir; public float Age; public bool Crit; }   // Dir: world-space, horizontal, normalized (victim -> attacker: "which way to face")
        readonly List<Mark> _marks = new();

        /// <summary>Register a hit. `dir` must already be world-space, horizontal and normalized, pointing FROM
        /// the victim TOWARD the attacker -- the opposite of the vector PlayerController's flinch math uses
        /// (attacker -> victim, the incoming direction it kicks away from). See ShowHurtCosmetics.</summary>
        public void Show(Vector3 dir, float damage)
        {
            if (!dir.IsFinite() || dir.LengthSquared() < 0.0001f) return;   // no real direction to show (fromPos ~= our own position)
            _marks.Add(new Mark { Dir = dir.Normalized(), Age = 0f, Crit = damage >= 25f });
            QueueRedraw();
        }

        public override void _Process(double delta)
        {
            if (_marks.Count == 0) return;
            bool anyAlive = false;
            for (int i = _marks.Count - 1; i >= 0; i--)
            {
                var m = _marks[i];
                m.Age += (float)delta;
                if (m.Age >= MarkTime) { _marks.RemoveAt(i); continue; }
                _marks[i] = m;
                anyAlive = true;
            }
            if (anyAlive) QueueRedraw();   // re-projects every frame even for a mark that isn't fading -- the CAMERA moves, the mark's world direction doesn't
        }

        public override void _Draw()
        {
            if (Cam == null || !IsInstanceValid(Cam) || _marks.Count == 0) return;
            Vector2 centre = Size * 0.5f;
            // Camera FORWARD/RIGHT flattened to the horizontal plane, matching how `dir` was built (Y zeroed
            // before normalizing) -- projecting against the full 3D basis would tilt the ring's zero-point
            // every time the player merely looks up or down, which is not a direction change worth showing.
            Vector3 camFwd = -Cam.GlobalTransform.Basis.Z; camFwd.Y = 0f;
            if (camFwd.LengthSquared() < 0.0001f) return;   // looking straight up/down: no stable "forward" to measure the bearing against
            camFwd = camFwd.Normalized();
            Vector3 camRight = Cam.GlobalTransform.Basis.X; camRight.Y = 0f;
            if (camRight.LengthSquared() < 0.0001f) return;
            camRight = camRight.Normalized();

            foreach (var m in _marks)
            {
                float fwd = m.Dir.Dot(camFwd), right = m.Dir.Dot(camRight);
                float bearingDeg = Mathf.RadToDeg(Mathf.Atan2(right, fwd));   // 0 = dead ahead, +90 = hit came from the right, 180 = behind
                float alpha = m.Age < FadeStart ? 1f : 1f - (m.Age - FadeStart) / (MarkTime - FadeStart);
                DrawWedge(centre, bearingDeg, alpha, m.Crit);
            }
        }

        void DrawWedge(Vector2 centre, float bearingDeg, float alpha, bool crit)
        {
            // Godot's 2D angle convention: 0 = +X (screen right), clockwise positive. Bearing 0 (dead ahead) has
            // to draw at the TOP of the ring (screen -Y, i.e. -90deg), and a hit from the right (bearing +90)
            // has to draw further clockwise, i.e. at 0deg screen-angle -- hence the -90 offset with no sign flip.
            float screenDeg = bearingDeg - 90f;
            float a0 = Mathf.DegToRad(screenDeg - WedgeHalfAngle), a1 = Mathf.DegToRad(screenDeg + WedgeHalfAngle);
            var col = crit ? new Color(1f, 0.35f, 0.2f, alpha * 0.9f) : new Color(0.95f, 0.15f, 0.1f, alpha * 0.85f);
            // A filled wedge with the tip AT centre reads as an arrowhead pointing outward once the whole shape
            // is inspected, but from a glance it is closer to a solid pie slice -- so the ring is drawn as a
            // band (inner radius > 0) instead: outward-facing, unmistakably a "there" marker, not a blob.
            const int segs = 10;
            const float innerR = Radius * 0.62f;
            var band = new Vector2[(segs + 1) * 2];
            for (int i = 0; i <= segs; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)segs);
                var dir2 = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                band[i] = centre + dir2 * innerR;
                band[segs * 2 + 1 - i] = centre + dir2 * Radius;
            }
            DrawColoredPolygon(band, col);
        }
    }
}
