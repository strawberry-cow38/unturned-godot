using NUnit.Framework;
using SDG.Unturned;
using UnityEngine;
using UnturnedGodot.Net;

namespace UnturnedNet.Tests
{
    /// <summary>The server and the client must compute the SAME dose from the same ground.
    ///
    /// This exists because they did not, and the way they did not is the interesting part: ServerDeadzones
    /// handed its step's radiation to the vitals sim as raw INFECTION. That was exactly right for as long as
    /// radiation WAS infection under another name. The moment radiation became a carried quantity that scales
    /// the scarring (dose x InfectionPerRadiationSecond x dt), the server kept adding the zone's rate straight
    /// to the virus while the client ran the new formula -- two different mechanics wearing one name, and
    /// nothing failed, because each side was internally consistent and no test compared them.
    ///
    /// The same commit gave the server the edge falloff the client already had. So these tests are driven at
    /// a point NEAR THE BOUNDARY on purpose: at the centre of a volume the intensity is 1 and a server that
    /// ignores falloff agrees with the client anyway. A parity test run in the middle of the box would have
    /// passed against the broken code.</summary>
    [TestFixture]
    public class DeadzoneServerParityTests
    {
        static readonly Vector3 Center = new Vector3(0f, 0f, 0f);
        static readonly Vector3 Half = new Vector3(10f, 10f, 10f);

        // 0.5 m inside the +X face: intensity ~0.27, nowhere near the 1.0 a falloff-blind server would use.
        static readonly Vector3 NearTheEdge = new Vector3(9.5f, 0f, 0f);

        const ushort Pid = 7;
        const float Dt = 0.1f;
        const int Steps = 400;   // 40 s -- well past the entry grace, long enough for the dose to matter

        static DeadzoneVolumeDef Volume() => new DeadzoneVolumeDef
        {
            Center = Center,
            HalfExtent = Half,
            Zone = DeadzoneDef.Default(),
        };

        /// <summary>What the CLIENT does, transcribed from DeadzoneField.Apply (which cannot be referenced
        /// here -- it is a Godot node). If that method changes shape, this stops being a parity test and the
        /// comment is the only thing that will say so.</summary>
        static PlayerVitalsSim RunClient(Vector3 pos, RadiationGear gear)
        {
            var volume = Volume();
            var sim = new DeadzoneSim();
            var vitals = new PlayerVitalsSim();
            for (int i = 0; i < Steps; i++)
            {
                float intensity = volume.Intensity(pos);
                var r = sim.Step(volume.Zone, gear, Dt, intensity);
                if (r.Radiation > 0f) vitals.AbsorbDose(r.Radiation, Dt);
            }
            return vitals;
        }

        static PlayerVitalsSim RunServer(Vector3 pos, RadiationGear gear)
        {
            var dz = new ServerDeadzones();
            dz.AddVolume(Center, Half);
            var repl = new PlayerVitalsReplication();
            var e = repl.ServerAdd(Pid, 0);
            dz.GearOf = _ => gear;
            dz.RadiationSink = (pid, dose, dt) => repl.ServerAbsorbDose(pid, dose, dt, 0);
            for (int i = 0; i < Steps; i++) dz.Apply(Pid, pos, Dt);
            return e.Sim;
        }

        [Test]
        public void Server_And_Client_Absorb_The_Same_Dose_Near_The_Edge()
        {
            var gear = new RadiationGear();   // unprotected
            var client = RunClient(NearTheEdge, gear);
            var server = RunServer(NearTheEdge, gear);

            Assert.That(server.Radiation, Is.EqualTo(client.Radiation).Within(1e-5f),
                "the dose is the same physics on both sides of the wire");
            Assert.That(server.Infection, Is.EqualTo(client.Infection).Within(1e-5f),
                "and so is the scarring it causes");
            // Guard the guard: if the dose were zero, the two would agree vacuously.
            Assert.That(client.Radiation, Is.GreaterThan(0.01f), "the fixture has to actually irradiate someone");
        }

        [Test]
        public void The_Server_Applies_The_Edge_Falloff()
        {
            // The direct statement of the second half of the bug, independent of the client transcription
            // above: standing at the boundary must cost the server-side player LESS than standing in the
            // middle. A falloff-blind server scores these identically.
            var gear = new RadiationGear();
            float edge = RunServer(NearTheEdge, gear).Radiation;
            float middle = RunServer(Center, gear).Radiation;

            Assert.That(edge, Is.LessThan(middle * 0.6f), "the edge has to be tamer on the server too");
            Assert.That(edge, Is.GreaterThan(0f), "tamer, not free -- the boundary still builds");
        }

        [Test]
        public void A_Suit_Is_Honoured_Identically_On_Both_Sides()
        {
            var suit = new RadiationGear { MaskProofs = true, MaskQuality = 100, ShirtProofs = true, PantsProofs = true };
            var client = RunClient(NearTheEdge, suit);
            var server = RunServer(NearTheEdge, suit);

            Assert.That(server.Radiation, Is.EqualTo(client.Radiation).Within(1e-5f));
            Assert.That(client.Radiation, Is.GreaterThan(0f), "a suit slows the dose, it does not stop it");
            Assert.That(client.Radiation, Is.LessThan(RunClient(NearTheEdge, new RadiationGear()).Radiation),
                "...and it has to actually be slower than wearing nothing, or the fixture proves nothing");
        }

        [Test]
        public void The_Dose_Crosses_The_Wire_Intact()
        {
            // Radiation is hidden from the HUD, so the ONLY way a client learns its own dose is this block.
            var repl = new PlayerVitalsReplication();
            var e = repl.ServerAdd(Pid, 0);
            e.Sim.Radiation = 0.75f;
            e.StampIfChanged(0);

            var w = new SDG.NetPak.NetPakWriter { buffer = new byte[NetProtocol.MaxDatagramBytes] };
            w.Reset();
            repl.WriteFull(w, new ReplicationContext(0L, Pid, default));
            w.Flush();

            var sent = new byte[w.writeByteIndex];
            System.Array.Copy(w.buffer, sent, sent.Length);
            var replica = new PlayerVitalsReplication();
            var r = new SDG.NetPak.NetPakReader();
            r.SetBuffer(sent);
            replica.ReadSnapshot(r, full: true);

            Assert.That(replica.TryGet(Pid, out var got), Is.True);
            Assert.That(got.Sim.Radiation, Is.EqualTo(0.75f).Within(1f / 256f),
                "8-bit quantized, so within one grain");
            Assert.That(got.Sim.MajorlyIrradiated, Is.True, "and the sprint gate reads the same on both ends");
        }
    }
}
