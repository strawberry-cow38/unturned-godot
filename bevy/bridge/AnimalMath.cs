namespace Godot;

public partial struct Vector3
{
    public static Vector3 Zero => new();
    public static Vector3 Up => new(0,1,0);
    public static Vector3 Forward => new(0,0,-1);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static Vector3 operator -(Vector3 a) => new(-a.X,-a.Y,-a.Z);
    public static Vector3 operator /(Vector3 a, float f) => new(a.X/f,a.Y/f,a.Z/f);
    public readonly float LengthSquared() => X*X+Y*Y+Z*Z;
    public readonly float Length() => MathF.Sqrt(LengthSquared());
    public readonly float Dot(Vector3 b) => X*b.X+Y*b.Y+Z*b.Z;
    public readonly Vector3 Cross(Vector3 b) => new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
    public readonly Vector3 Normalized() { float n=Length(); return n==0?Zero:this/n; }
    // Godot4.6 Vector3.Slerp behavior: zero/collinear vectors lerp, including
    // antiparallel vectors. Rodrigues rotation is the same Basis(axis,angle) transform.
    // https://github.com/godotengine/godot/blob/4.6-stable/modules/mono/glue/GodotSharp/GodotSharp/Core/Vector3.cs
    public readonly Vector3 Slerp(Vector3 to,float weight)
    {
        float a=LengthSquared(),b=to.LengthSquared(); var axis=Cross(to); float c=axis.LengthSquared();
        if(a==0||b==0||c==0) return this+(to-this)*weight;
        axis/=MathF.Sqrt(c); float length=MathF.Sqrt(a), result=length+(MathF.Sqrt(b)-length)*weight;
        float angle=MathF.Atan2(MathF.Sqrt(c),Dot(to))*weight, cosine=MathF.Cos(angle),sine=MathF.Sin(angle);
        return (this*cosine+axis.Cross(this)*sine+axis*(axis.Dot(this)*(1-cosine)))*(result/length);
    }
}
