using Godot;
using System.Collections.Generic;

// A world-space foam wake for boats/ships: a triangle whose APEX sits on the hull's bow tip and which
// fans out behind her at the Kelvin half-angle (19.5 deg), filled with the same ocean-foam noise as
// content/wake.gdshader so it matches the sea. Built from a short history of the bow-tip position --
// each render frame the hull calls Push() with its INTERPOLATED transform (so the apex stays glued to
// the visually-rendered ship, not the raw 50Hz physics pose); the triangle is rebuilt as an
// ImmediateMesh strip that widens with distance behind the bow and fades with age. Added as a TopLevel
// child of the hull, so it lives in world space yet is freed with the vehicle.
public partial class WakeTrail : MeshInstance3D
{
    struct Sample { public Vector3 P; public float Age; public float Dist; }   // world pos on the sea surface, age (s), distance behind the bow apex (m)

    readonly List<Sample> _pts = new();
    ImmediateMesh _im;
    Vector3 _lastApex;
    bool _hasLastApex;

    const float Life = 7.5f;          // seconds a foam sample lives
    const float MinStep = 0.6f;       // min metres between samples (fine -> the curved wake reads smooth, not faceted)
    const float SpreadRate = 0.3541f; // half-width per metre behind the apex = tan(19.5 deg) = the Kelvin wake half-angle
    const float MaxSpread = 45f;      // cap the fan-out on a very long trail
    const float MaxApexStep = 0.8f;   // clamp per-frame apex travel -> absorb the rare 2-3 m physics lurch (normal ~0.3 m/frame)

    public override void _Ready()
    {
        _im = new ImmediateMesh();
        Mesh = _im;
        TopLevel = true;   // world-space: ignore the hull's transform; the verts are already world coords
        CastShadow = ShadowCastingSetting.Off;
        MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://content/wake.gdshader") };
    }

    // Hull calls this on the RENDER frame with its INTERPOLATED transform + the MEASURED bow-tip Z in
    // local space, so the triangle apex sits exactly on her bow. seaY = sea surface Y, speed = horizontal
    // speed (m/s), dt = render delta. speed at or below the threshold -> age out only (no new foam).
    public void Push(Transform3D shipXf, float bowLocalZ, float seaY, float speed, float dt)
    {
        if (_im == null) return;
        // the triangle apex = the hull's bow tip (centreline, measured bow-Z) on the sea surface
        Vector3 bowW = shipXf * new Vector3(0f, 0f, bowLocalZ);
        Vector3 apex = new Vector3(bowW.X, seaY + 0.05f, bowW.Z);
        // clamp per-frame apex travel -> absorb the rare physics lurch that would shoot the wake tip forward
        if (_hasLastApex) { Vector3 d = apex - _lastApex; if (d.Length() > MaxApexStep) apex = _lastApex + d.Normalized() * MaxApexStep; }
        _lastApex = apex; _hasLastApex = true;

        if (speed > 1.2f)
        {
            if (_pts.Count == 0)
                _pts.Add(new Sample { P = apex, Age = 0f, Dist = 0f });
            else
            {
                // the newest sample is a LIVE HEAD: pin it to the current bow every frame (age 0) so the
                // triangle tip glides with her instead of snapping forward each time a point is dropped
                var head = _pts[_pts.Count - 1];
                head.P = apex; head.Age = 0f;
                _pts[_pts.Count - 1] = head;
                // freeze the head + start a fresh one only once we have travelled MinStep from the last committed point
                int prev = _pts.Count - 2;
                if (prev < 0 || apex.DistanceTo(_pts[prev].P) >= MinStep)
                    _pts.Add(new Sample { P = apex, Age = 0f, Dist = 0f });
            }
        }

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

        // distance from the apex (newest = last index, dist 0) back toward the tail
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
            var a = _pts[i + 1];   // nearer the bow (smaller Dist, younger)
            var b = _pts[i];       // farther back (larger Dist, older)
            Vector3 rightA = Vector3.Up.Cross(SegDir(i + 1)).Normalized();
            Vector3 rightB = Vector3.Up.Cross(SegDir(i)).Normalized();
            float ha = Mathf.Min(a.Dist * SpreadRate, MaxSpread);   // apex width 0 -> a triangle tip at the bow, fanning out behind
            float hb = Mathf.Min(b.Dist * SpreadRate, MaxSpread);
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
