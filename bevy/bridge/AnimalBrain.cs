using Godot;
using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Engine boundary for the generated original AnimalAgent behavior.
/// No independent native wander or collision solve runs for these terrain-followed bodies.</summary>
public sealed partial class AnimalBrain
{
    public uint NetId, Seed;
    public ushort AnimalId;
    public byte Species;
    public Vector3 Home, GlobalPosition, DeathImpulse;
    public float Foot, BodyH, Health, Yaw;
    public bool Dead { get; private set; }
    public bool Freed;
    public byte NetAnim { get; private set; }
    public string Clip = "Idle";
    public long RetireTick = -1;
    public Func<float, float, byte> SampleLayer;
    public Func<float, float, float> SampleGround;
    public Func<Vector3, Vector3, bool> Occluded;
    public Func<IEnumerable<Vector3>> Anchors;
    public Action Panic;
    public Action<Vector3> Died;
    public Vector3 Forward => new(-MathF.Sin(Yaw * MathF.PI / 180), 0, -MathF.Cos(Yaw * MathF.PI / 180));
    public float Radius => Mathf.Clamp(Mathf.Max(BodyH, .6f) * .38f, .25f, .7f);
    public void Begin() => StartIdle();
    void Play(string clip) => Clip = clip;
    float GroundY(float x, float z) => SampleGround(x, z);
    void LookAt(Vector3 point, Vector3 up)
    {
        var d = point - GlobalPosition;
        if (d.X * d.X + d.Z * d.Z > 0) Yaw = MathF.Atan2(-d.X, -d.Z) * 180 / MathF.PI;
    }
    Vector3? NearestPlayerEye()
    {
        Vector3? best = null; float distance = float.MaxValue;
        foreach (var p in Anchors())
        {
            var eye = p + Vector3.Up; float d = (eye - GlobalPosition).LengthSquared();
            if (d < distance) { best = eye; distance = d; }
        }
        return best;
    }
    public sealed record Saved(uint Seed, ushort AnimalId, byte Species, float[] Home, float[] Position,
        float Health, float Yaw, bool Dead, byte Anim, string Clip, float[] Target, bool Walking,
        double IdleTimer, double FleeTimer, double SeeCheck, float[] FaceDir, long CorpseRemaining, float[] DeathImpulse = null);
    static float[] V(Vector3 v) => [v.X,v.Y,v.Z];
    static Vector3 V(float[] v) => new(v[0],v[1],v[2]);
    public Saved Capture(long tick) => new(Seed,AnimalId,Species,V(Home),V(GlobalPosition),Health,Yaw,Dead,NetAnim,Clip,
        V(_target),_walking,_idleTimer,_fleeTimer,_seeCheck,V(_faceDir),RetireTick<0?-1:Math.Max(0,RetireTick-tick),V(DeathImpulse));
    public void Restore(Saved s, long tick)
    {
        Seed=s.Seed; Home=V(s.Home); GlobalPosition=V(s.Position); Health=s.Health; Yaw=s.Yaw; Dead=s.Dead; NetAnim=s.Anim; Clip=s.Clip;
        _target=V(s.Target); _walking=s.Walking; _idleTimer=s.IdleTimer; _fleeTimer=s.FleeTimer; _seeCheck=s.SeeCheck;
        _faceDir=V(s.FaceDir); RetireTick=s.CorpseRemaining<0?-1:tick+s.CorpseRemaining;
        DeathImpulse=s.DeathImpulse==null?Vector3.Zero:V(s.DeathImpulse);
    }
    public void TranslateWorld(Vector3 d) { Home+=d; GlobalPosition+=d; _target+=d; }
}
