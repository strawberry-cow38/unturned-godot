namespace Godot;

public partial struct Vector2
{
    public static Vector2 Zero => new();
    public readonly float LengthSquared() => X*X+Y*Y;
    public readonly float Length() => MathF.Sqrt(LengthSquared());
    public readonly float Dot(Vector2 other) => X*other.X+Y*other.Y;
    public readonly Vector2 LimitLength(float length=1)
    {float current=Length();return current>length?this*(length/current):this;}
    public static Vector2 operator +(Vector2 a,Vector2 b)=>new(a.X+b.X,a.Y+b.Y);
    public static Vector2 operator -(Vector2 a,Vector2 b)=>new(a.X-b.X,a.Y-b.Y);
    public static Vector2 operator -(Vector2 a)=>new(-a.X,-a.Y);
    public static Vector2 operator *(Vector2 a,float f)=>new(a.X*f,a.Y*f);
    public static bool operator ==(Vector2 a,Vector2 b)=>a.X==b.X&&a.Y==b.Y;
    public static bool operator !=(Vector2 a,Vector2 b)=>!(a==b);
    public override readonly bool Equals(object obj)=>obj is Vector2 v&&this==v;
    public override readonly int GetHashCode()=>HashCode.Combine(X,Y);
}
