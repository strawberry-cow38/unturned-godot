using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // Sphere-shaped deadzone volumes. PEI's only authored deadzone is a SPHERE, and until this shape existed
    // the field could only hold boxes -- so wiring the map's zone in would have quietly inflated it to its
    // bounding cube. These tests are written around that exact failure rather than around the happy path.
    [TestFixture]
    public class DeadzoneSphereTests
    {
        // PEI's real values, from Level.hierarchy via tools/parse_hierarchy_deadzones.py:
        // Sphere, Unity centre (506.368, 32.28622, -720.7485), scale 32 -> Godot centre with Z negated, r=16.
        const float R = 16f;
        static readonly Vector3 Centre = new Vector3(506.368f, 32.28622f, 720.7485f);

        static DeadzoneVolumeDef Sphere() => new DeadzoneVolumeDef
        {
            Center = Centre,
            HalfExtent = new Vector3(R, R, R),
            Zone = DeadzoneDef.Default(),
            Shape = DeadzoneShape.Sphere,
        };

        static DeadzoneVolumeDef AsBox()
        {
            var v = Sphere();
            v.Shape = DeadzoneShape.Box;
            return v;
        }

        [Test]
        public void The_Centre_Is_Inside()
        {
            Assert.That(Sphere().Contains(Centre), Is.True);
        }

        [Test]
        public void A_Point_Just_Inside_The_Radius_Is_Inside()
        {
            var p = new Vector3(Centre.x + R - 0.5f, Centre.y, Centre.z);
            Assert.That(Sphere().Contains(p), Is.True);
        }

        [Test]
        public void A_Point_Just_Past_The_Radius_Is_Outside()
        {
            var p = new Vector3(Centre.x + R + 0.5f, Centre.y, Centre.z);
            Assert.That(Sphere().Contains(p), Is.False);
        }

        // THE ONE THAT MATTERS. The bounding cube's corner is r*sqrt(3) = 27.7 m from the centre -- well
        // outside a 16 m sphere, and standing on it is the difference between "clean ground" and "taking a
        // dose 27 m from the zone". A box approximation calls this inside; the sphere must not.
        [Test]
        public void The_Bounding_Cubes_Corner_Is_Outside_The_Sphere_But_Inside_The_Box()
        {
            var corner = new Vector3(Centre.x + R * 0.99f, Centre.y + R * 0.99f, Centre.z + R * 0.99f);
            Assert.That(AsBox().Contains(corner), Is.True,  "the bounding box contains its own corner");
            Assert.That(Sphere().Contains(corner), Is.False, "the sphere must NOT -- it is 27.4 m from the centre");
        }

        [Test]
        public void Intensity_Is_Zero_Outside_And_Full_At_The_Centre()
        {
            var v = Sphere();
            Assert.That(v.Intensity(new Vector3(Centre.x + R + 1f, Centre.y, Centre.z)), Is.EqualTo(0f));
            Assert.That(v.Intensity(Centre), Is.EqualTo(1f).Within(1e-4f));
        }

        // The edge still builds dose -- that is the whole point of EdgeFloor, and a radial falloff that
        // reached zero would make the boundary a free perch exactly as a box one would.
        [Test]
        public void The_Boundary_Still_Builds_But_Tamer_Than_The_Middle()
        {
            var v = Sphere();
            float edge = v.Intensity(new Vector3(Centre.x + R - 0.01f, Centre.y, Centre.z));
            float mid  = v.Intensity(new Vector3(Centre.x + R * 0.3f, Centre.y, Centre.z));
            Assert.That(edge, Is.GreaterThan(0f), "the boundary is a warning, not a safe perch");
            Assert.That(edge, Is.EqualTo(DeadzoneVolumeDef.EdgeFloor).Within(0.02f));
            Assert.That(mid, Is.GreaterThan(edge), "deeper in is worse");
        }

        // Intensity must fall off by DISTANCE, not by worst-axis. Two points at the same radius on different
        // bearings are equally deep in a sphere; the box rule would score the diagonal one as shallower.
        [Test]
        public void Two_Points_At_The_Same_Radius_Score_The_Same()
        {
            var v = Sphere();
            float axis = v.Intensity(new Vector3(Centre.x + 8f, Centre.y, Centre.z));
            float diag = v.Intensity(new Vector3(Centre.x + 8f / Mathf.Sqrt(2f), Centre.y, Centre.z + 8f / Mathf.Sqrt(2f)));
            Assert.That(diag, Is.EqualTo(axis).Within(1e-3f), "a sphere has no preferred axis");
        }

        // Box behaviour is untouched: Shape defaults to Box, so every volume built before this existed
        // means exactly what it did.
        [Test]
        public void A_Default_Constructed_Volume_Is_Still_A_Box()
        {
            var v = new DeadzoneVolumeDef { Center = Centre, HalfExtent = new Vector3(R, R, R), Zone = DeadzoneDef.Default() };
            Assert.That(v.Shape, Is.EqualTo(DeadzoneShape.Box));
            var corner = new Vector3(Centre.x + R * 0.99f, Centre.y + R * 0.99f, Centre.z + R * 0.99f);
            Assert.That(v.Contains(corner), Is.True);
        }
    }
}
