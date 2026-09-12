using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>GPU particles must still render offline, because every way we LOOK at this game is offline.
    ///
    /// RainSystem3D.cs carried this for months: "CpuParticles3D, not GpuParticles3D -- GPU particles do NOT
    /// render in Godot's movie-maker / offline render pipeline". It WAS true. It is not any more (fixed
    /// somewhere before 4.6.2), and 16 files paid main-thread cost for a limitation that had gone.
    ///
    /// The visual-golden tier was scrapped on 2026-09-11, so this no longer guards a gate -- it guards the
    /// thing that replaced it. This box has no display: `--shot` and `--write-movie` under xvfb are how a
    /// change is verified by eye, how footage is captured, and how bug reports are filmed. If an engine bump
    /// reintroduced the limitation, particles would vanish from all of it silently, and a render with no
    /// smoke in it looks exactly like a scene that has no smoke.
    ///
    /// ⚠ RUN IT ON THE WEAK RENDERER. cow tools verified GPU particles offline on a 4080S; this box is
    /// xvfb + lavapipe SOFTWARE rendering, and "works on the good GPU" is not the question. Measured here:
    /// viewport 3587 px GPU / 3093 px CPU, and in the extracted AVI frame 22032 px / 18996 px.
    ///
    /// The CPU emitter is the CONTROL: if blue is missing too, the probe broke rather than the engine.</summary>
    public class GpuParticleMovieTests : GameTest
    {
        public override string Name => "particles.gpu_renders_offline";
        public override double TimeoutSimSeconds => 20;

        public override IEnumerable<Step> Run()
        {
            var cam = new Camera3D { Current = true, Position = new Vector3(0, 1.2f, 4f) };
            World.AddChild(cam);
            cam.LookAt(new Vector3(0, 1.2f, 0), Vector3.Up);
            World.AddChild(new WorldEnvironment { Environment = new Godot.Environment {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0, 0, 0),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White, AmbientLightEnergy = 1f } });

            Mesh quad = new QuadMesh { Size = new Vector2(0.25f, 0.25f) };
            StandardMaterial3D Mat(Color c) => new StandardMaterial3D {
                AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled };

            // RED = GPU, on the left
            var gpu = new GpuParticles3D {
                Position = new Vector3(-1.2f, 1.2f, 0), Amount = 64, Lifetime = 4f, Explosiveness = 0f,
                DrawPass1 = quad, MaterialOverride = Mat(new Color(1, 0, 0)), Emitting = true,
                VisibilityAabb = new Aabb(new Vector3(-5, -5, -5), new Vector3(10, 10, 10)),
                ProcessMaterial = new ParticleProcessMaterial {
                    Direction = new Vector3(0, 1, 0), Spread = 25f,
                    InitialVelocityMin = 0.4f, InitialVelocityMax = 0.8f, Gravity = Vector3.Zero } };
            World.AddChild(gpu);

            // BLUE = CPU, on the right (the control: if this is missing too, the probe itself is broken)
            var cpu = new CpuParticles3D {
                Position = new Vector3(1.2f, 1.2f, 0), Amount = 64, Lifetime = 4f,
                Mesh = quad, MaterialOverride = Mat(new Color(0, 0.4f, 1f)), Emitting = true,
                Direction = new Vector3(0, 1, 0), Spread = 25f,
                InitialVelocityMin = 0.4f, InitialVelocityMax = 0.8f, Gravity = Vector3.Zero,
                VisibilityAabb = new Aabb(new Vector3(-5, -5, -5), new Vector3(10, 10, 10)) };
            World.AddChild(cpu);

            for (int i = 0; i < 40; i++) yield return Ticks(5);   // let both fill

            // ⚠ HEADLESS CANNOT ANSWER THIS, and saying so beats passing quietly. L1 boots with --headless,
            // which has no render target at all -- the pixel check below would throw, and a version that
            // swallowed that would report green while verifying nothing. The real run is the xvfb path:
            //   xvfb-run godot --path game --rendering-driver vulkan -- --tests=particles.gpu_renders_offline
            RenderingServer.ForceDraw();
            var vpTex = World.GetViewport()?.GetTexture();
            if (vpTex == null)
            {
                GD.Print("[gpuprobe] NO RENDER TARGET (headless) -- offline-particle rendering NOT verified here; run under xvfb");
                T.Check("both emitters constructed and emitting (headless: the only claim available)",
                        gpu.Emitting && cpu.Emitting && gpu.ProcessMaterial != null);
                yield break;
            }
            var img = vpTex.GetImage();
            if (img == null || img.IsEmpty())
            {
                GD.Print("[gpuprobe] viewport texture yields NO IMAGE (headless) -- offline-particle rendering NOT verified here; run under xvfb");
                T.Check("both emitters constructed and emitting (headless: the only claim available)",
                        gpu.Emitting && cpu.Emitting && gpu.ProcessMaterial != null);
                yield break;
            }
            int red = 0, blue = 0;
            for (int y = 0; y < img.GetHeight(); y += 2)
                for (int x = 0; x < img.GetWidth(); x += 2)
                {
                    var p = img.GetPixel(x, y);
                    if (p.R > 0.45f && p.G < 0.3f && p.B < 0.3f) red++;
                    if (p.B > 0.45f && p.R < 0.3f) blue++;
                }
            GD.Print($"[gpuprobe] viewport: GPU(red)={red} px   CPU(blue)={blue} px");
            T.Check($"CPU particles render (control, rendered mode) -- {blue} px", blue > 20);
            T.Check($"GPU particles render offline under THIS renderer -- {red} px", red > 20);
        }
    }
}
