// Additional Godot4.6 Basis operations used by EditorObjects' saved-map format.
// Same Godot MIT notice as IslandBasis.cs applies to these adapted operations.
namespace Godot;
public partial struct Basis
{
    public readonly float Determinant()
    {
        float cofac00 = Row1.Y*Row2.Z - Row1.Z*Row2.Y;
        float cofac10 = Row1.Z*Row2.X - Row1.X*Row2.Z;
        float cofac20 = Row1.X*Row2.Y - Row1.Y*Row2.X;
        return Row0.X*cofac00 + Row0.Y*cofac10 + Row0.Z*cofac20;
    }
    public readonly Vector3 Scale => Mathf.Sign(Determinant()) * new Vector3(X.Length(),Y.Length(),Z.Length());
    public readonly Basis Orthonormalized()
    {
        Vector3 column0=X, column1=Y, column2=Z;
        column0=column0.Normalized();
        column1=column1-column0*column0.Dot(column1);
        column1=column1.Normalized();
        column2=column2-column0*column0.Dot(column2)-column1*column1.Dot(column2);
        column2=column2.Normalized();
        return new Basis(column0,column1,column2);
    }
    public static Basis FromScale(Vector3 scale) => new(scale.X,0,0,0,scale.Y,0,0,0,scale.Z);
    // Only YXZ is required by the original EditorObjects save convention.
    public readonly Vector3 GetEuler()
    {
        // Euler angles in YXZ convention.
        // See https://en.wikipedia.org/wiki/Euler_angles#Rotation_matrix
        //
        // rot =  cy*cz+sy*sx*sz    cz*sy*sx-cy*sz        cx*sy
        //        cx*sz             cx*cz                 -sx
        //        cy*sx*sz-cz*sy    cy*cz*sx+sy*sz        cy*cx
        Vector3 euler;
        float m12 = Row1.Z;
        if (m12 < (1 - 1e-6f))
        {
            if (m12 > -(1 - 1e-6f))
            {
                // is this a pure X rotation?
                if (Row1.X == 0 && Row0.Y == 0 && Row0.Z == 0 && Row2.X == 0 && Row0.X == 1)
                {
                    // return the simplest form (human friendlier in editor and scripts)
                    euler.X = Mathf.Atan2(-m12, Row1.Y);
                    euler.Y = 0;
                    euler.Z = 0;
                }
                else
                {
                    euler.X = Mathf.Asin(-m12);
                    euler.Y = Mathf.Atan2(Row0.Z, Row2.Z);
                    euler.Z = Mathf.Atan2(Row1.X, Row1.Y);
                }
            }
            else
            { // m12 == -1
                euler.X = Mathf.Tau / 4.0f;
                euler.Y = Mathf.Atan2(Row0.Y, Row0.X);
                euler.Z = 0;
            }
        }
        else
        { // m12 == 1
            euler.X = -Mathf.Tau / 4.0f;
            euler.Y = -Mathf.Atan2(Row0.Y, Row0.X);
            euler.Z = 0;
        }

        return euler;
    }
}
