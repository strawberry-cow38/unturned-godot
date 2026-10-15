// Basis arithmetic adapted from Godot Engine 4.6 Basis.cs.
// Copyright (c) 2014-present Godot Engine contributors (see AUTHORS.md).
// Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur.
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
namespace Godot;

// Only the source island consumer's value operations. Columns match Godot's
// public axes; arithmetic order is retained for deterministic placement output.
public partial struct Basis
{
    public Vector3 Row0, Row1, Row2;
    public readonly Vector3 X => new(Row0.X, Row1.X, Row2.X);
    public readonly Vector3 Y => new(Row0.Y, Row1.Y, Row2.Y);
    public readonly Vector3 Z => new(Row0.Z, Row1.Z, Row2.Z);
    public readonly float Tdotx(Vector3 v) => Row0.X*v.X + Row1.X*v.Y + Row2.X*v.Z;
    public readonly float Tdoty(Vector3 v) => Row0.Y*v.X + Row1.Y*v.Y + Row2.Y*v.Z;
    public readonly float Tdotz(Vector3 v) => Row0.Z*v.X + Row1.Z*v.Y + Row2.Z*v.Z;
    public Basis(Vector3 axis, float angle)
    {
        Vector3 axisSq = new Vector3(axis.X * axis.X, axis.Y * axis.Y, axis.Z * axis.Z);
        (float sin, float cos) = MathF.SinCos(angle);

        Row0.X = axisSq.X + cos * (1.0f - axisSq.X);
        Row1.Y = axisSq.Y + cos * (1.0f - axisSq.Y);
        Row2.Z = axisSq.Z + cos * (1.0f - axisSq.Z);

        float t = 1.0f - cos;

        float xyzt = axis.X * axis.Y * t;
        float zyxs = axis.Z * sin;
        Row0.Y = xyzt - zyxs;
        Row1.X = xyzt + zyxs;

        xyzt = axis.X * axis.Z * t;
        zyxs = axis.Y * sin;
        Row0.Z = xyzt + zyxs;
        Row2.X = xyzt - zyxs;

        xyzt = axis.Y * axis.Z * t;
        zyxs = axis.X * sin;
        Row1.Z = xyzt - zyxs;
        Row2.Y = xyzt + zyxs;
    }
    public Basis(Vector3 column0, Vector3 column1, Vector3 column2)
    {
        Row0 = new Vector3(column0.X, column1.X, column2.X);
        Row1 = new Vector3(column0.Y, column1.Y, column2.Y);
        Row2 = new Vector3(column0.Z, column1.Z, column2.Z);
        // Same as:
        // Column0 = column0;
        // Column1 = column1;
        // Column2 = column2;
        // We need to assign the struct fields here first so we can't do it that way...
    }
    public Basis(float xx, float yx, float zx, float xy, float yy, float zy, float xz, float yz, float zz)
    {
        Row0 = new Vector3(xx, yx, zx);
        Row1 = new Vector3(xy, yy, zy);
        Row2 = new Vector3(xz, yz, zz);
    }
    public readonly Basis Scaled(Vector3 scale)
    {
        Basis b = this;
        b.Row0 *= scale.X;
        b.Row1 *= scale.Y;
        b.Row2 *= scale.Z;
        return b;
    }
    public static Basis operator *(Basis left, Basis right)
    {
        return new Basis
        (
            right.Tdotx(left.Row0), right.Tdoty(left.Row0), right.Tdotz(left.Row0),
            right.Tdotx(left.Row1), right.Tdoty(left.Row1), right.Tdotz(left.Row1),
            right.Tdotx(left.Row2), right.Tdoty(left.Row2), right.Tdotz(left.Row2)
        );
    }
    public static Vector3 operator *(Basis basis, Vector3 vector)
    {
        return new Vector3
        (
            basis.Row0.Dot(vector),
            basis.Row1.Dot(vector),
            basis.Row2.Dot(vector)
        );
    }
}
