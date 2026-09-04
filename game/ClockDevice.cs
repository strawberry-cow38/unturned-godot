using Godot;

namespace UnturnedGodot
{
    // Clock_0's hands are baked INTO the mesh (master 2026-08-10). This carves them off by REACH from the dial centre --
    // minute tip 0.373, hour 0.273; the dial disc (0.500) + the twelve hour markers (0.405) stay put -- BAKES each hand to
    // point at 12 (rotating its verts so the angular offset is paid ONCE in geometry, not per-frame; tinyclaw), and spins
    // them to the in-game clock. TimezoneHours offsets the displayed time -- the Alberton clocks are a BANK of world clocks.
    //
    // Hands rotate around LOCAL Y as CHILDREN of the placed node, so the placement basis (including the rolled Alberton
    // instances) applies on top and the sweep stays in the dial plane whatever the world orientation (tinyclaw).
    public partial class ClockDevice : Node3D
    {
        public float TimezoneHours;      // +/- hours offset from base game-time (0 = local/in-game time)

        MeshInstance3D _minute, _hour, _body;   // _body = the clock's dial mesh (bodyMi): the hands track its visibility/validity so they vanish when the clock is broken/destroyed (master)

        static bool Front(Vector3 a, Vector3 b, Vector3 c) => Mathf.Min(a.Y, Mathf.Min(b.Y, c.Y)) >= 0.0875f;
        static float MaxReach(Vector3 a, Vector3 b, Vector3 c)
        {
            float ra = Mathf.Sqrt(a.X * a.X + a.Z * a.Z);
            float rb = Mathf.Sqrt(b.X * b.X + b.Z * b.Z);
            float rc = Mathf.Sqrt(c.X * c.X + c.Z * c.Z);
            return Mathf.Max(ra, Mathf.Max(rb, rc));
        }

        // Bake a hand to point at 12 (+Z): find its tip (the max-reach vert) and rotate the whole hand so that direction lands
        // on +Z. Afterwards the hand's rest pose IS 12 o'clock, so the runtime rotation below is the raw time angle for BOTH.
        static ArrayMesh PointAt12(ArrayMesh hand)
        {
            if (hand == null || hand.GetSurfaceCount() < 1) return hand;
            var V = hand.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            int tip = 0; float best = -1f;
            for (int i = 0; i < V.Length; i++) { float r = V[i].X * V[i].X + V[i].Z * V[i].Z; if (r > best) { best = r; tip = i; } }
            return ObjMesh.RotateAboutY(hand, -Mathf.Atan2(V[tip].X, V[tip].Z));   // undo the tip's angle-from-+Z
        }

        public static ClockDevice Make(MeshInstance3D bodyMi, Material mat, float tzHours)
        {
            var full = bodyMi.Mesh as ArrayMesh;
            if (full == null) return null;
            var (afterHour, hourMesh) = ObjMesh.SplitByFaceVerts(full, (a, b, c) =>
            {
                if (!Front(a, b, c)) return false; float r = MaxReach(a, b, c); return r >= 0.20f && r < 0.32f;   // hour: ~0.273
            });
            var (bodyMesh, minuteMesh) = ObjMesh.SplitByFaceVerts(afterHour, (a, b, c) =>
            {
                if (!Front(a, b, c)) return false; float r = MaxReach(a, b, c); return r >= 0.32f && r < 0.40f;   // minute: ~0.373 (markers 0.405 excluded)
            });
            bodyMi.Mesh = bodyMesh;   // the static clock keeps the dial + markers

            var dev = new ClockDevice { TimezoneHours = tzHours, Transform = bodyMi.Transform, _body = bodyMi };
            if (hourMesh != null)   { dev._hour   = new MeshInstance3D { Mesh = PointAt12(hourMesh),   MaterialOverride = mat }; dev.AddChild(dev._hour); }
            if (minuteMesh != null) { dev._minute = new MeshInstance3D { Mesh = PointAt12(minuteMesh), MaterialOverride = mat }; dev.AddChild(dev._minute); }
            return dev;
        }

        public override void _EnterTree() { TickHub.Add(this, HubTick, 2f); }
        public override void _ExitTree() { TickHub.Remove(this); }
        public void HubTick(double delta)   // PERF: hub-ticked at 2 Hz (was a per-frame engine callback; see TickHub)
        {
            if (!GodotObject.IsInstanceValid(_body)) { QueueFree(); return; }   // the clock mesh was destroyed -> destroy the hands with it (master)
            bool clockShown = _body.IsVisibleInTree();
            if (Visible != clockShown) Visible = clockShown;                     // hands appear/vanish WITH the dial -- the destructible break HIDES the mesh (doesn't free it), a reset re-shows it
            if (!clockShown) return;                                             // a broken/hidden clock: hands gone + don't spend the tick
            var dn = GetTree().GetFirstNodeInGroup("daynight") as DayNightCycle;
            float t = dn?.Time ?? 0.5f;                                  // 0..1, 0 = midnight, 0.5 = noon
            float local = Mathf.PosMod(t + TimezoneHours / 24f, 1f);
            // Hands are baked pointing at 12, so this is the raw time angle -- the SAME line for both, no per-hand constant.
            // -Y advances clockwise as read from the +Y face. minute 24 turns/day; hour 2 turns/day (0.5 deg/min creep rides the fraction).
            if (_minute != null) _minute.Rotation = new Vector3(0f, -local * 24f * Mathf.Tau, 0f);
            if (_hour != null)   _hour.Rotation   = new Vector3(0f, -local * 2f  * Mathf.Tau, 0f);
        }
    }
}
