using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>TV snow belongs to the GLASS, not the monitor (strawberry 2026-10-04: "the tv's are like.. a portal to
    /// another dimension with the static channel. its not like a flat image on the screen").
    ///
    /// screen.gdshader hashed its snow off FRAGCOORD -- the display's pixel -- so the noise stayed put on your monitor
    /// while the set moved under it. Measured on two identical sets (same seed, same time) side by side: their snow
    /// correlated -0.005 (two different patches of the monitor), and 0.594 once the grain grid sits on the UVs.
    /// Mean luma unchanged (152.2 vs 152.0), so TVDevice.MeanLuma still describes what is drawn.
    ///
    /// Checked at the source: no line of code in screen.gdshader may read FRAGCOORD. Comments may mention it.
    ///
    /// Then "should be a fixed size, not 1px exactly" and "the OSD ... should be a fixed aspect ratio (the one on the crt
    /// monitor is perfect)": both need the screen's REAL aspect, since UV is 0..1 on every glass. So the wiring is checked
    /// on real props: the shader must be told what TVDevice measured (1.0703 CRT monitor, 1.9722 flatscreen TV), not
    /// left at its default 1.0.</summary>
    public sealed class TVStaticOnGlassTests : GameTest
    {
        public override string Name => "tv.static_on_the_glass";
        public override double TimeoutSimSeconds => 30;
        static readonly Transform3D StandUp = new(Basis.FromEuler(new Vector3(-Mathf.Pi * 0.5f, 0f, 0f)), Vector3.Zero);

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);
            string src = "";
            foreach (var c in new[] { "res://content/screen.gdshader", "res://game/content/screen.gdshader" })
            {
                string p = ProjectSettings.GlobalizePath(c);
                if (System.IO.File.Exists(p)) { src = System.IO.File.ReadAllText(p); break; }
            }
            T.Check("screen.gdshader found", src.Length > 0);
            bool readsFragcoord = false;
            foreach (var line in src.Split('\n'))
                if (line.Split("//")[0].Contains("FRAGCOORD")) readsFragcoord = true;
            T.Check("no code line reads FRAGCOORD (the snow would be pinned to the monitor)", !readsFragcoord);

            var aspects = new Dictionary<string, float>();
            foreach (var prop in new[] { "Computer_0", "Television_0" })
            {
                var mesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/") + prop + ".obj");
                T.Check($"{prop}.obj loads", mesh != null);
                if (mesh == null) continue;
                var mi = new MeshInstance3D { Mesh = mesh, Transform = StandUp };
                World.AddChild(mi);
                var dev = TVDevice.Make(mi, prop);
                T.Check($"{prop} builds a screen", dev != null);
                if (dev == null) continue;
                World.AddChild(dev);
                yield return Ticks(2);
                float told = dev.ScreenAspectUniformForTest;
                T.Check($"{prop}: the shader is told the measured aspect ({told:0.0000} vs {dev.ScreenAspect:0.0000})",
                        Mathf.Abs(told - dev.ScreenAspect) < 1e-4f);
                aspects[prop] = told;
            }
            if (aspects.Count == 2)
                T.Check($"...and they differ the way the glass does (flatscreen {aspects["Television_0"]:0.00} > CRT monitor {aspects["Computer_0"]:0.00})",
                        aspects["Television_0"] > aspects["Computer_0"] * 1.5f);
        }
    }
}
