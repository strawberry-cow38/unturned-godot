using Godot;
using System.Collections.Generic;

// A world-space foam ribbon that trails a moving boat/ship: the turbulent wake down the middle +
// the divergent bow-wave V at the edges, drawn with the same ocean-foam noise (content/wake.gdshader)
// so it matches the sea. Built from a short history of the hull's stern position -- each physics
// tick the hull calls Push(); the ribbon is rebuilt as an ImmediateMesh strip that widens toward the
// tail (the Kelvin spread) and fades with age. Added as a TopLevel child of the hull, so it lives in
// world space yet is freed with the vehicle.
public partial class WakeTrail : MeshInstance3D
{
    struct Sample { public Vector3 P; public float Age; public float Dist; }   // world pos on the sea surface, age (s), cumulative distance behind the hull (m)

    readonly List<Sample> _pts = new();
    ImmediateMesh _im;
    Vector3 _lastRaw;
    bool _hasRaw;

    public float HalfWidth = 6f;     // base half-width at the stern (~ the hull's half-beam)
    public float SternOffset = 0f;   // how far behind the hull origin the wake is born (~ half the hull length)

    const float Life = 7.5f;         // seconds a foam sample lives
    const float MinStep = 2.5f;      // min metres between samples
    const float SpreadRate = 0.16f;  // half-width gained per metre behind the hull (~ tan of the Kelvin half-angle)
    const float MaxSpread = 20f;     // cap the fan-out

    public override void _Ready()
    {
        _im = new ImmediateMesh();
        Mesh = _im;
        TopLevel = true;   // world-space: ignore the hull's transform; the verts are already world coords
        CastShadow = ShadowCastingSetting.Off;
        MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/wake.gdshader") };
    }

    // Hull calls this each physics tick while afloat. shipPos = hull origin (world), seaY = flat sea
    // surface Y, speed = horizontal speed (m/s), dt = physics delta.
    public void Push(Vector3 shipPos, float seaY, float speed, float dt)
    {
        if (_im == null) return;
        Vector3 vel = _hasRaw ? new Vector3(shipPos.X - _lastRaw.X, 0f, shipPos.Z - _lastRaw.Z) : Vector3.Zero;
        _lastRaw = shipPos; _hasRaw = true;
        Vector3 dir = vel.LengthSquared() > 1e-6f ? vel.Normalized() : Vector3.Zero;

        // the wake is born at the stern (behind the hull, opposite travel) on the flat sea surface
        Vector3 stern = new Vector3(shipPos.X, seaY + 0.05f, shipPos.Z) - dir * SternOffset;

        bool moving = speed > 1.2f && dir != Vector3.Zero;
        if (moving && (_pts.Count == 0 || stern.DistanceTo(_pts[_pts.Count - 1].P) >= MinStep))
            _pts.Add(new Sample { P = stern, Age = 0f, Dist = 0f });

        for (int i = _pts.Count - 1; i >= 0; i--)
        {
            var s = _pts[i]; s.Age += dt; _pts[i] = s;
            if (s.Age > Life) _pts.RemoveAt(i);
        }
        Rebuild();
    }

    void Rebuild()
    {
        _im.ClearSurfaces();
        int n = _pts.Count;
        if (n < 2) return;

        // cumulative distance from the hull end (newest = last index, dist 0) toward the tail
        float total = 0f;
        _pts[n - 1] = WithDist(_pts[n - 1], 0f);
        for (int i = n - 2; i >= 0; i--)
        {
            total += _pts[i].P.DistanceTo(_pts[i + 1].P);
            _pts[i] = WithDist(_pts[i], total);
        }
        float inv = 1f / (total + 0.001f);

        _im.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < n - 1; i++)
        {
            var a = _pts[i + 1];   // nearer the hull (smaller Dist, younger)
            var b = _pts[i];       // farther back (larger Dist, older)
            Vector3 rightA = Vector3.Up.Cross(SegDir(i + 1)).Normalized();
            Vector3 rightB = Vector3.Up.Cross(SegDir(i)).Normalized();
            float ha = HalfWidth + Mathf.Min(a.Dist * SpreadRate, MaxSpread);
            float hb = HalfWidth + Mathf.Min(b.Dist * SpreadRate, MaxSpread);
            float fa = 1f - a.Age / Life;
            float fb = 1f - b.Age / Life;
            float va = a.Dist * inv;
            float vb = b.Dist * inv;

            Vector3 aL = a.P - rightA * ha, aR = a.P + rightA * ha;
            Vector3 bL = b.P - rightB * hb, bR = b.P + rightB * hb;

            Emit(aL, new Vector2(0f, va), fa); Emit(aR, new Vector2(1f, va), fa); Emit(bR, new Vector2(1f, vb), fb);
            Emit(aL, new Vector2(0f, va), fa); Emit(bR, new Vector2(1f, vb), fb); Emit(bL, new Vector2(0f, vb), fb);
        }
        _im.SurfaceEnd();
    }

    Vector3 SegDir(int i)
    {
        int a = Mathf.Max(i - 1, 0), b = Mathf.Min(i + 1, _pts.Count - 1);
        Vector3 d = _pts[b].P - _pts[a].P;
        return d.LengthSquared() > 1e-6f ? d.Normalized() : Vector3.Forward;
    }

    static Sample WithDist(Sample s, float dist) { s.Dist = dist; return s; }

    void Emit(Vector3 p, Vector2 uv, float fade)
    {
        _im.SurfaceSetColor(new Color(1f, 1f, 1f, fade));
        _im.SurfaceSetUV(uv);
        _im.SurfaceSetNormal(Vector3.Up);
        _im.SurfaceAddVertex(p);
    }
}
