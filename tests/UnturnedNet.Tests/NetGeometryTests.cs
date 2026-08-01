using System;
using NUnit.Framework;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    // DUPLICATE_AUDIT 2.11 + 2.13. These formulas were inline in four places between them, and one of the
    // copies shipped with the sign flipped -- `(+sin, +cos)` instead of `(-sin, -cos)` -- so server-side
    // melee swings landed 180 degrees BEHIND the attacker. Four characters, no compiler complaint, and
    // nothing in the type system to notice.
    //
    // So the point of this fixture is not that the arithmetic is hard. It is that the sign convention now
    // has exactly one home and a test that fails the moment someone "tidies" it.
    [TestFixture]
    public class NetGeometryTests
    {
        const float Eps = 1e-5f;

        [Test]
        public void forward_at_yaw_zero_is_MINUS_Z()
        {
            // The whole bug in one assertion: a body at yaw 0 faces -Z in the Godot frame.
            var f = NetGeometry.ForwardFromYaw(0f);
            Assert.That(f.x, Is.EqualTo(0f).Within(Eps));
            Assert.That(f.z, Is.EqualTo(-1f).Within(Eps), "yaw 0 faces -Z; +1 here is the inverted-melee bug");
            Assert.That(f.y, Is.EqualTo(0f).Within(Eps), "forward is horizontal");
        }

        [Test]
        public void forward_turns_the_right_way_round()
        {
            // yaw +90 must swing to -X, not +X. Gets the handedness, not just the zero case.
            var f = NetGeometry.ForwardFromYaw(90f);
            Assert.That(f.x, Is.EqualTo(-1f).Within(Eps));
            Assert.That(f.z, Is.EqualTo(0f).Within(Eps));

            var back = NetGeometry.ForwardFromYaw(180f);
            Assert.That(back.z, Is.EqualTo(1f).Within(Eps), "180 degrees round faces +Z");
        }

        [Test]
        public void forward_and_right_are_perpendicular_and_unit()
        {
            // Guards the pair against someone copying one into the other -- the exact failure mode that
            // produced the duplicates in the first place.
            foreach (float yaw in new[] { -270f, -33.3f, 0f, 17f, 90f, 180f, 359.9f })
            {
                var f = NetGeometry.ForwardFromYaw(yaw);
                var r = NetGeometry.RightFromYaw(yaw);
                Assert.That(f.magnitude, Is.EqualTo(1f).Within(Eps), $"forward is unit at yaw {yaw}");
                Assert.That(r.magnitude, Is.EqualTo(1f).Within(Eps), $"right is unit at yaw {yaw}");
                Assert.That(Vector3.Dot(f, r), Is.EqualTo(0f).Within(Eps), $"forward _|_ right at yaw {yaw}");
                Assert.That(f, Is.Not.EqualTo(r), $"they are different vectors at yaw {yaw}");
            }
        }

        [Test]
        public void exit_spot_is_beside_the_vehicle_not_inside_it()
        {
            // 2.13: the server and the client fallback used to derive this separately, and drifting apart
            // against a frozen replica is what docs/EXIT_POSITION_ROOTCAUSE.md is about.
            var pos = new Vector3(10f, 5f, -3f);
            var spot = NetGeometry.ExitSpotBeside(pos, 0f);

            Assert.That(spot.y, Is.EqualTo(pos.y + NetGeometry.ExitUpOffset).Within(Eps), "lifted clear of the hull");
            var flat = new Vector3(spot.x - pos.x, 0f, spot.z - pos.z);
            Assert.That(flat.magnitude, Is.EqualTo(NetGeometry.ExitSideOffset).Within(Eps),
                        "placed exactly the side offset away, horizontally");
            Assert.That(Vector3.Dot(flat / flat.magnitude, NetGeometry.ForwardFromYaw(0f)), Is.EqualTo(0f).Within(Eps),
                        "beside, not in front of or behind");
        }

        [Test]
        public void exit_spot_follows_the_vehicle_yaw()
        {
            // Rotating the car must rotate where you step out; a yaw-independent spot would put a driver
            // inside the hull half the time.
            var pos = Vector3.zero;
            var a = NetGeometry.ExitSpotBeside(pos, 0f);
            var b = NetGeometry.ExitSpotBeside(pos, 180f);
            Assert.That(a.x, Is.EqualTo(-b.x).Within(Eps), "180 degrees puts you out the other side");
            Assert.That(a.y, Is.EqualTo(b.y).Within(Eps), "same height either way");
        }

        [Test]
        public void Deg2Rad_is_bit_identical_to_the_hand_written_constant()
        {
            // The audit claimed these differed and that swapping them was a numeric change. It is not:
            // same float32 bits, and the products agree on every yaw. Pinned so the claim stays dead and
            // nobody reintroduces `Mathf.PI / 180f` copies to be "safe".
            Assert.That(BitConverter.SingleToInt32Bits(Mathf.Deg2Rad),
                        Is.EqualTo(BitConverter.SingleToInt32Bits(Mathf.PI / 180f)),
                        "Deg2Rad (PI*2/360) and PI/180 are the same float32 constant");

            for (int i = -3600; i <= 3600; i++)
            {
                float deg = i / 10f;
                Assert.That(BitConverter.SingleToInt32Bits(deg * Mathf.Deg2Rad),
                            Is.EqualTo(BitConverter.SingleToInt32Bits(deg * (Mathf.PI / 180f))),
                            $"products agree at {deg} degrees");
            }
        }
    }
}
