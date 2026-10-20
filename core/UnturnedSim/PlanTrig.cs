using System;

namespace UnturnedSim
{
    /// <summary>Cosine and sine of an angle in degrees, the same on every machine: exact at right angles, and elsewhere a
    /// polynomial in plain double arithmetic. The furnisher's footprints land on its 0.1 m grid's cell boundaries all the
    /// time, so one ulp of difference in a platform's cosf (cos 90 deg is -4.4e-8 from glibc) flips a cell in or out --
    /// and the editor, the plan dumper and strawberry's machine furnished the same seed three different ways.</summary>
    public static class PlanTrig
    {
        public static (float Cos, float Sin) CosSin(float degrees)
        {
            // (the four right angles, which nearly every call is, as the general path below gives them -- its signed zeros
            // too -- without its rounding and remainder)
            if (degrees == 0f) return (1f, 0f);
            if (degrees == 90f) return (-0f, 1f);
            if (degrees == 180f) return (-1f, -0f);
            if (degrees == 270f) return (0f, -1f);
            double q = Math.Round(degrees / 90.0);
            double r = degrees - 90.0 * q;               // within 45 degrees of a right angle
            int k = (int)(((long)q % 4 + 4) % 4);
            double c, s;
            if (Math.Abs(r) < 1e-4) { c = 1.0; s = 0.0; }
            else
            {
                double x = r * (Math.PI / 180.0), x2 = x * x;
                // Taylor to x^11 / x^12: under 1e-11 off for |x| <= pi/4, well past float precision
                s = x * (1 - x2 / 6 * (1 - x2 / 20 * (1 - x2 / 42 * (1 - x2 / 72 * (1 - x2 / 110)))));
                c = 1 - x2 / 2 * (1 - x2 / 12 * (1 - x2 / 30 * (1 - x2 / 56 * (1 - x2 / 90 * (1 - x2 / 132)))));
            }
            return k switch { 0 => ((float)c, (float)s), 1 => ((float)-s, (float)c), 2 => ((float)-c, (float)-s), _ => ((float)s, (float)-c) };
        }
    }
}
