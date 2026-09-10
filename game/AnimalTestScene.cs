using Godot;
using System;
using System.Globalization;
using System.Linq;

namespace UnturnedGodot
{
    public partial class Main
    {
        // Calibrated render, never GetAabb(): skin/culling bounds are not visible feet.
        // ANIMAL=horse,cow,deer tools/shot.py animal; UG_ANIMALCAM=side|other|front|rear|top|quarter.
        void BuildAnimalTest(string species)
        {
            string Env(string key, string fallback) => System.Environment.GetEnvironmentVariable(key) ?? fallback;
            float Number(string key, float fallback) => float.TryParse(Env(key, ""), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var n) ? n : fallback;
            string view = Env("UG_ANIMALCAM", "side"), clip = Env("UG_ANIMALCLIP", "Idle");
            if (!new[] { "side", "other", "front", "rear", "top", "quarter", "threequarter", "low", "lowrear" }.Contains(view))
            { Log.Err($"[animaltest] unknown view {view}"); GetTree().Quit(1); return; }
            float yaw = Number("UG_ANIMALYAW", 270f), time = Number("UG_ANIMALTIME", 0f);
            var names = species.Split(',');
            bool group = names.Length > 1;
            bool endView = view == "front" || view == "rear";
            AddChild(new WorldEnvironment { Environment = new Godot.Environment {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.42f, 0.55f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.6f, 0.62f), AmbientLightEnergy = 0.9f } });
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55f, -35f, 0f), LightEnergy = 1.2f, ShadowEnabled = true });
            var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(25f, 25f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.34f, 0.28f) } };
            AddChild(ground);
            void Bar(Vector3 p, Vector3 size, Color c)
            {
                AddChild(new MeshInstance3D { Position = p, Mesh = new BoxMesh { Size = size },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } });
            }
            void Label(string text, Vector3 p)
            {
                AddChild(new Label3D { Text = text, Position = p, FontSize = 30, PixelSize = 0.003f,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true });
            }
            for (int i = 0; i < names.Length; i++)
            {
                var def = AnimalCatalog.All.FirstOrDefault(k => k.Rig == names[i]);
                if (def.Rig == null) { Log.Err($"[animaltest] unknown animal {names[i]}"); GetTree().Quit(1); return; }
                float z = (i - (names.Length - 1) * 0.5f) * 3.5f;
                var offset = endView ? new Vector3(z, 0f, 0f) : new Vector3(0f, 0f, z);
                var holder = new Node3D { Position = offset + new Vector3(0f, Number("UG_ANIMALFOOT", 0f), 0f) };
                AddChild(holder);
                var rc = RiggedCharacter.Build($"res://content/{def.Rig}_rig.json", Colors.White, false, $"res://content/objects/{def.Tex}", null);
                if (rc == null) { GetTree().Quit(1); return; }
                holder.AddChild(rc);
                rc.RotationDegrees = new Vector3(0f, yaw, 0f);
                if (clip != "rest" && !rc.ClipNames.Contains(clip))
                { Log.Err($"[animaltest] {def.Rig} has no clip {clip}"); GetTree().Quit(1); return; }
                rc.Play(clip);
                // Rig animations are manually advanced. Freeze at a reproducible, explicit clip time.
                var ap = rc.GetChildren().OfType<AnimationPlayer>().First();
                if (clip != "rest") ap.Seek(time, true);
                Log.Print($"[animaltest] {def.Rig} origin={holder.Position} yaw={yaw} clip={clip} time={time}; clips={string.Join(',', rc.ClipNames)}");
                Label(def.Rig, offset + new Vector3(0f, -0.22f, 0f));
                // White ground datum and 0.25 m ruler in the side plane, behind the animal.
                if (!endView && view != "top")
                {
                    Bar(new Vector3(-0.75f, 0f, z), new Vector3(0.008f, 0.008f, 3.2f), Colors.White);
                    Bar(new Vector3(-0.75f, 1.25f, z + 1.55f), new Vector3(0.008f, 2.5f, 0.008f), Colors.White);
                    for (int t = 0; t <= 10; t++)
                    {
                        Bar(new Vector3(-0.75f, t * 0.25f, z + 1.55f), new Vector3(0.008f, 0.008f, 0.09f), Colors.White);
                        if (t % 2 == 0) Label($"{t * 0.25f:0.0}", new Vector3(-0.75f, t * 0.25f, z + 1.72f));
                    }
                }
                // Top view axes: BLUE +X, RED -Z (travel); both exactly 1 m from their common base.
                var basePos = offset + new Vector3(-1.0f, 0.015f, 1.35f);
                Bar(basePos + new Vector3(0.5f, 0f, 0f), new Vector3(1f, 0.025f, 0.035f), Colors.DodgerBlue);
                Bar(basePos + new Vector3(0f, 0f, -0.5f), new Vector3(0.035f, 0.025f, 1f), Colors.Red);
                if (view == "top") { Label("+X 1m", basePos + new Vector3(1.15f, 0f, 0f)); Label("-Z 1m", basePos + new Vector3(0f, 0f, -1.15f)); }
            }
            var cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = group ? 5.4f : 3.4f, Current = true };
            AddChild(cam);
            if (view == "top") cam.LookAtFromPosition(new Vector3(0f, 12f, 0f), Vector3.Zero, Vector3.Left);
            else if (view == "other") cam.LookAtFromPosition(new Vector3(-10f, 1.2f, 0f), new Vector3(0f, 1.2f, 0f), Vector3.Up);
            else if (view == "rear") cam.LookAtFromPosition(new Vector3(0f, 1.2f, 10f), new Vector3(0f, 1.2f, 0f), Vector3.Up);
            else if (view == "front") cam.LookAtFromPosition(new Vector3(0f, 1.2f, -10f), new Vector3(0f, 1.2f, 0f), Vector3.Up);
            else if (view == "low") cam.LookAtFromPosition(new Vector3(10f, 0.4f, -4f), new Vector3(0f, 1.1f, 0f), Vector3.Up);
            else if (view == "lowrear") cam.LookAtFromPosition(new Vector3(-8f, 0.4f, 6f), new Vector3(0f, 1.1f, 0f), Vector3.Up);
            else if (view == "threequarter" || view == "quarter") cam.LookAtFromPosition(new Vector3(9f, 6f, -4f), new Vector3(0f, 1.1f, 0f), Vector3.Up);
            else cam.LookAtFromPosition(new Vector3(10f, 1.2f, 0f), new Vector3(0f, 1.2f, 0f), Vector3.Up);
            // Log screen calibration after the window resize has reached the viewport.
            Callable.From(() => Log.Print($"[animalcal] view={view} viewport={GetViewport().GetVisibleRect().Size} origin_px={cam.UnprojectPosition(Vector3.Zero)} y1_px={cam.UnprojectPosition(Vector3.Up)} z1_px={cam.UnprojectPosition(Vector3.Back)} x1_px={cam.UnprojectPosition(Vector3.Right)}")).CallDeferred();
        }
    }
}
