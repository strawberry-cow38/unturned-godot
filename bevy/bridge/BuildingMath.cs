// Additional Godot 4.6 value operations used by the original WallSurface.
// Same Godot MIT notice as IslandBasis.cs applies to these adapted methods.
// Source: https://github.com/godotengine/godot/tree/4.6-stable/modules/mono/glue/GodotSharp/GodotSharp/Core
namespace Godot;

public static partial class Mathf
{
    public static float Floor(float value) => MathF.Floor(value);
    // GodotSharp/Core/Mathf.cs, Godot 4.6: preserve the divisor's sign.
    public static int PosMod(int a, int b)
    {
        int c = a % b;
        if ((c < 0 && b > 0) || (c > 0 && b < 0)) c += b;
        return c;
    }
}

public partial struct Vector3
{
    public static Vector3 Back => new(0, 0, 1);
}

public partial struct Basis
{
    // Godot's default YXZ order, in radians. This is not EditorObjects.FromEuler,
    // which converts retail PEI angles and includes a 180-degree heading change.
    public static Basis FromEuler(Vector3 euler)
    {
        (float sin, float cos) = MathF.SinCos(euler.X);
        Basis xmat = new(new Vector3(1, 0, 0), new Vector3(0, cos, sin), new Vector3(0, -sin, cos));
        (sin, cos) = MathF.SinCos(euler.Y);
        Basis ymat = new(new Vector3(cos, 0, -sin), new Vector3(0, 1, 0), new Vector3(sin, 0, cos));
        (sin, cos) = MathF.SinCos(euler.Z);
        Basis zmat = new(new Vector3(cos, sin, 0), new Vector3(-sin, cos, 0), new Vector3(0, 0, 1));
        return ymat * xmat * zmat;
    }

    public readonly Basis Inverse()
    {
        float cofac00 = Row1.Y * Row2.Z - Row1.Z * Row2.Y;
        float cofac10 = Row1.Z * Row2.X - Row1.X * Row2.Z;
        float cofac20 = Row1.X * Row2.Y - Row1.Y * Row2.X;
        float det = Row0.X * cofac00 + Row0.Y * cofac10 + Row0.Z * cofac20;
        if (det == 0)
            throw new InvalidOperationException("Matrix determinant is zero and cannot be inverted.");
        float detInv = 1.0f / det;
        float cofac01 = Row0.Z * Row2.Y - Row0.Y * Row2.Z;
        float cofac02 = Row0.Y * Row1.Z - Row0.Z * Row1.Y;
        float cofac11 = Row0.X * Row2.Z - Row0.Z * Row2.X;
        float cofac12 = Row0.Z * Row1.X - Row0.X * Row1.Z;
        float cofac21 = Row0.Y * Row2.X - Row0.X * Row2.Y;
        float cofac22 = Row0.X * Row1.Y - Row0.Y * Row1.X;
        return new Basis(
            cofac00 * detInv, cofac01 * detInv, cofac02 * detInv,
            cofac10 * detInv, cofac11 * detInv, cofac12 * detInv,
            cofac20 * detInv, cofac21 * detInv, cofac22 * detInv);
    }
}
