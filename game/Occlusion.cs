using Godot;

namespace UnturnedGodot
{
    /// <summary>OCCLUSION CULLING, switched on by the first occluder and never before (strawberry 2026-10-16: "make sure actual
    /// culling of props happens ... since we make the walls / floors we can probably do more advanced culling").
    ///
    /// Godot culls against OccluderInstance3Ds by ray-casting them (Embree, the raycast module) into a small depth buffer every
    /// frame and testing each instance's bounds against it. That is CPU work every frame, and "our issue is always CPU"
    /// (strawberry), so it is only turned on for a viewport whose world actually has occluders: the generated buildings' walls
    /// (WallSurface.RebuildOccluder) call EnsureOn as they make their first one. A map with none -- PEI, the test rigs -- never
    /// pays for the buffer.
    ///
    /// UG_NOOCCLUDE=1 is the A/B control: no occluder is made and the viewport is never switched on, so the one binary renders
    /// both sides of the comparison. A dedicated server has no renderer and makes none either.</summary>
    public static class Occlusion
    {
        public static readonly bool Disabled = System.Environment.GetEnvironmentVariable("UG_NOOCCLUDE") == "1";

        /// <summary>Should surfaces make occluders at all: not under UG_NOOCCLUDE, not on a headless (dedicated) process.</summary>
        public static bool Wanted => !Disabled && DisplayServer.GetName() != "headless";

        static bool _logged;

        /// <summary>Turn occlusion culling on for the viewport an occluder lives in (idempotent).</summary>
        public static void EnsureOn(Viewport vp)
        {
            if (!Wanted || vp == null || vp.UseOcclusionCulling) return;
            vp.UseOcclusionCulling = true;
            if (_logged) return;
            _logged = true;
            Log.Print($"[occlusion] on for viewport '{vp.Name}': the generated buildings' walls occlude (UG_NOOCCLUDE=1 is the control)");
        }
    }
}
