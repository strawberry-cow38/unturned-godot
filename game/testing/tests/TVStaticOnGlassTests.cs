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
    /// Checked at the source: no line of code in screen.gdshader may read FRAGCOORD. Comments may mention it.</summary>
    public sealed class TVStaticOnGlassTests : GameTest
    {
        public override string Name => "tv.static_on_the_glass";

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
        }
    }
}
