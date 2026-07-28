using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedSim.Tests
{
    // L0 for the safezone rules. Engine-free, so the whole of the gameplay contract is testable
    // without a running game -- which matters because the ONE thing a safezone must never do is
    // fail open (protect nobody) or fail shut (protect a zone whose generator is dead).
    [TestFixture]
    public class SafezoneSimTests
    {
        static SafezoneSim WithZone(out int idx, float radius = 20f)
        {
            var s = new SafezoneSim();
            idx = s.Add(new Vector3(0f, 0f, 0f), radius);
            return s;
        }

        [Test]
        public void A_Point_Inside_The_Radius_Is_Protected()
        {
            var s = WithZone(out _);
            Assert.That(s.Contains(new Vector3(5f, 0f, 5f)), Is.True);
            Assert.That(s.BlocksDamageAt(new Vector3(5f, 0f, 5f)), Is.True);
        }

        [Test]
        public void A_Point_Outside_The_Radius_Is_Not()
        {
            var s = WithZone(out _);
            Assert.That(s.Contains(new Vector3(25f, 0f, 0f)), Is.False);
            Assert.That(s.BlocksDamageAt(new Vector3(25f, 0f, 0f)), Is.False);
        }

        [Test]
        public void The_Zone_Is_A_Sphere_So_A_Roof_Inside_It_Is_Still_Safe()
        {
            // A cylinder test would protect someone 200 m overhead; a flat test would leave a player
            // on a roof unprotected. Retail's is a sphere, so height counts toward the distance.
            var s = WithZone(out _);
            Assert.That(s.Contains(new Vector3(0f, 15f, 0f)), Is.True, "inside the sphere, above the ground");
            Assert.That(s.Contains(new Vector3(0f, 25f, 0f)), Is.False, "beyond the radius, straight up");
            Assert.That(s.Contains(new Vector3(15f, 15f, 0f)), Is.False, "diagonal distance exceeds the radius");
        }

        [Test]
        public void An_Unpowered_Zone_Protects_Nobody()
        {
            // The bubble is a consequence of the power grid: cutting the generator must remove the
            // protection, or a safezone becomes permanent the moment it is built once.
            var s = WithZone(out int idx);
            Assert.That(s.Contains(Vector3.zero), Is.True, "test setup: live to begin with");
            s.SetActive(idx, false);
            Assert.That(s.Contains(Vector3.zero), Is.False, "an unpowered zone must protect nobody");
            Assert.That(s.BlocksDamageAt(Vector3.zero), Is.False);
            Assert.That(s.BlocksBuildingAt(Vector3.zero), Is.False);
            s.SetActive(idx, true);
            Assert.That(s.Contains(Vector3.zero), Is.True, "and it comes back when power returns");
        }

        [Test]
        public void An_Inactive_Zone_Still_Exists_For_The_View_To_Draw()
        {
            var s = WithZone(out int idx);
            s.SetActive(idx, false);
            Assert.That(s.Count, Is.EqualTo(1), "deactivating must not delete the zone");
            Assert.That(s.ZoneAt(0).Active, Is.False);
        }

        [Test]
        public void A_Zombie_Inside_Is_Ejected_Horizontally_To_Just_Outside_The_Edge()
        {
            var s = WithZone(out _, radius: 20f);
            var inside = new Vector3(3f, 7f, 4f);          // 5 m out horizontally, 7 m up
            var target = s.EjectionTarget(inside, margin: 0.5f);

            float flat = new Vector2(target.x, target.z).magnitude;
            Assert.That(flat, Is.EqualTo(20.5f).Within(0.01f), "pushed to just beyond the boundary");
            Assert.That(target.y, Is.EqualTo(inside.y).Within(1e-4f),
                "height must be untouched -- a vertical shove launches it into the air or the floor");
            Assert.That(s.Contains(target), Is.False, "and the result is genuinely outside the zone");
        }

        [Test]
        public void A_Zombie_Exactly_At_The_Centre_Is_Still_Ejected_Somewhere()
        {
            // No outward direction exists at dead centre. Must pick one deterministically rather than
            // divide by zero and teleport it to NaN.
            var s = WithZone(out _, radius: 20f);
            var target = s.EjectionTarget(Vector3.zero);
            Assert.That(float.IsNaN(target.x) || float.IsNaN(target.z), Is.False, "no NaN");
            Assert.That(s.Contains(target), Is.False, "and it does leave the zone");
        }

        [Test]
        public void A_Point_Outside_Every_Zone_Is_Left_Where_It_Is()
        {
            var s = WithZone(out _);
            var outside = new Vector3(100f, 0f, 100f);
            Assert.That(s.EjectionTarget(outside), Is.EqualTo(outside));
        }

        [Test]
        public void Overlapping_Zones_Protect_If_ANY_Of_Them_Is_Live()
        {
            var s = new SafezoneSim();
            int a = s.Add(new Vector3(0f, 0f, 0f), 20f);
            s.Add(new Vector3(10f, 0f, 0f), 20f);
            s.SetActive(a, false);
            Assert.That(s.Contains(new Vector3(5f, 0f, 0f)), Is.True,
                "one dead generator must not switch off an overlapping live zone");
        }
    }
}
