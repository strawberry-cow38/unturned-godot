using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Rain on a CAR, near it and inside it (strawberry 2026-09-07: "wire up the car rain sound when
    /// near or inside a car").
    ///
    /// The clip and the emitter were both already there. RainMaterialAudio has driven a rain_car.wav emitter
    /// since 2026-08-30, and it worked: it found the vehicle with a sphere query on collision bit 0, which is
    /// where a chassis lived at the time.
    ///
    /// Then the mesh hitbox landed. Vehicle.FinaliseHitboxLayers takes bit 0 AND bit 5 off the chassis and puts
    /// it on ChassisBit, and the hull mesh that inherits its job sits on HitMeshBit -- so a vehicle stopped
    /// being on either bit the rain query masks, and the car layer went silent. It is the same class of failure
    /// twice over: a collision-layer move breaks a consumer that names the bit, and the consumer is somewhere
    /// nobody thinks to look because IT still compiles and IT still runs.
    ///
    /// What makes it worth a test rather than a one-line mask fix is that the failure is INAUDIBLE in the only
    /// way that matters -- an emitter with no position to play from sounds precisely like weather that has no
    /// car in it. There is no error, no warning, and no difference in the log. So the checks here demand actual
    /// noise from a jeep the listener is standing next to, and (the half that would otherwise never be covered)
    /// silence when it drives away. A test that only asserted the first would pass just as happily against an
    /// emitter that is always on, which is the same shape of nothing.
    ///
    /// The fix reads Vehicle.Live instead of masking a bit, so the next layer move cannot repeat this.</summary>
    public sealed class RainCarAudioTests : GameTest
    {
        public override string Name => "rain.car_audio";
        public override double TimeoutSimSeconds => 30;

        public override IEnumerable<Step> Run()
        {
            string clip = ProjectSettings.GlobalizePath("res://content/rain_car.wav");
            T.Check("rain_car.wav is on disk", System.IO.File.Exists(clip));

            var cam = new Camera3D();
            World.AddChild(cam);
            cam.GlobalPosition = new Vector3(0f, 1.7f, 0f);

            var rma = new RainMaterialAudio { Cam = cam, Intensity = 1f };
            World.AddChild(rma);
            yield return Ticks(2);

            // ---- NO CAR YET. Establishes that the emitter is not simply always on, so the positive check
            // below is worth something. Without this leg a permanently-playing emitter passes the whole test.
            rma.HubProcess(1.0);
            yield return Ticks(1);
            T.Check("with no vehicle in the world, the car layer is silent", !rma.DebugCarPlaying);

            var jeep = Vehicle.BuildByName("jeep");
            World.AddChild(jeep);
            jeep.GlobalPosition = new Vector3(4f, 0f, 0f);   // parked 4 m away: comfortably inside Radius (16 m)
            yield return Ticks(2);

            T.Check("the jeep registered in Vehicle.Live", System.Linq.Enumerable.Contains(Vehicle.Live, jeep));

            // ---- NEAR THE CAR. HubProcess is called with a delta past PollSeconds so the scan runs on this
            // call rather than whenever the hub next happens to tick.
            rma.HubProcess(1.0);
            yield return Ticks(1);
            T.Check("standing 4 m from a parked jeep, the car layer plays", rma.DebugCarPlaying);
            T.Check($"...and it plays FROM the jeep ({rma.DebugCarPosition} vs {jeep.GlobalPosition})",
                    rma.DebugCarPosition.DistanceTo(jeep.GlobalPosition) < 0.5f);

            // ---- INSIDE THE CAR. The listener sits at the vehicle origin, which is inside the hull. This is
            // the case the sphere query was worst at even before the layer move: a camera inside a body is a
            // question about overlap, whereas a distance to the vehicle's own position is simply ~0.
            cam.GlobalPosition = jeep.GlobalPosition + new Vector3(0f, 0.6f, 0f);
            rma.HubProcess(1.0);
            yield return Ticks(1);
            T.Check("sitting inside the jeep, the car layer plays", rma.DebugCarPlaying);

            // ---- DRIVEN AWAY. The negative leg with a car that exists but is out of range -- distinct from
            // the no-car leg above, because it is the range test rather than the registry test.
            jeep.GlobalPosition = new Vector3(400f, 0f, 0f);
            cam.GlobalPosition = new Vector3(0f, 1.7f, 0f);
            rma.HubProcess(1.0);
            yield return Ticks(1);
            T.Check("with the jeep 400 m away, the car layer stops", !rma.DebugCarPlaying);

            // ---- AND NO RAIN MEANS NO CAR RAIN, whatever is parked next to you.
            jeep.GlobalPosition = new Vector3(4f, 0f, 0f);
            rma.Intensity = 0f;
            rma.HubProcess(1.0);
            yield return Ticks(1);
            T.Check("in dry weather a nearby jeep makes no rain sound", !rma.DebugCarPlaying);

            jeep.QueueFree();
            yield return Ticks(1);
        }
    }
}
