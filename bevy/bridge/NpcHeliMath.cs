namespace Godot;

public partial struct Vector3
{
    public static Vector3 One => new(1,1,1);
    public readonly float DistanceTo(Vector3 other)=>(other-this).Length();
    public readonly float DistanceSquaredTo(Vector3 other)=>(other-this).LengthSquared();
    public readonly float AngleTo(Vector3 other)=>MathF.Atan2(Cross(other).Length(),Dot(other));
    public static bool operator ==(Vector3 a,Vector3 b)=>a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;
    public static bool operator !=(Vector3 a,Vector3 b)=>!(a==b);
    public override readonly bool Equals(object other)=>other is Vector3 v&&this==v;
    public override readonly int GetHashCode()=>HashCode.Combine(X,Y,Z);
}
public partial struct Vector2
{
    public readonly float DistanceTo(Vector2 other)=>(other-this).Length();
}
