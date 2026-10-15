using System.Collections.Generic;
using Godot;

namespace UnturnedGodot
{
    // ==============================================================================================
    // YARD FENCES, BUILT (strawberry 2026-10-14: "work on getting fences as splines, might wanna split the models into
    // the individual pieces first and then solving them in engine?"). PlanFences says where; FenceSpline lays them.
    // ==============================================================================================
    public sealed partial class TownRoadGrid
    {
        /// <summary>Lay the yard fences as real fences. UG_TOWNFENCES=0 keeps the old designation line instead.</summary>
        public static bool BuildRealFences = System.Environment.GetEnvironmentVariable("UG_TOWNFENCES") != "0";

        /// <summary>The fence a lot's yard gets, steady per lot and seed. A house mostly gets the board privacy fence
        /// (Fence_Wood_0, PEI's commonest: 317 placed), sometimes white pickets or a ranch rail; a shop's back is mostly
        /// chain link, as is half the apartment blocks' (Fence_Metal_0, PEI's next: 206).</summary>
        FenceSpline.Style FenceStyleFor(Plot pl, int index)
        {
            ulong h = Mix(((ulong)(uint)Seed << 32) ^ ((ulong)(uint)index * 0x9E3779B97F4A7C15UL) ^ 0xFE4CE5UL);
            float u = Unit(h, 16);
            return pl.Kind switch
            {
                PlotKind.Shop => u < 0.7f ? FenceSpline.ChainLink : FenceSpline.WoodPrivacy,
                PlotKind.Apartments => u < 0.5f ? FenceSpline.ChainLink : FenceSpline.WoodPrivacy,
                _ => u < 0.55f ? FenceSpline.WoodPrivacy : u < 0.8f ? FenceSpline.WoodPicket
                   : u < 0.9f ? FenceSpline.WoodRanch : FenceSpline.ChainLink,
            };
        }

        /// <summary>Build every yard fence the last PlanPlots designated, each in its lot's style; runs of one style that
        /// meet at a corner share the corner post. Returns the cells made.</summary>
        public int BuildFences(Node3D parent, EditorObjects objs)
        {
            if (!BuildRealFences || Fences.Count == 0 || parent == null || objs == null) return 0;
            var terr = Terrain.Active;
            var b = new FenceSpline.Builder(terr != null ? (x, z) => terr.SampleHeight(x, z) : null);
            var index = new Dictionary<Plot, int>();
            for (int i = 0; i < Plots.Count; i++) index[Plots[i]] = i;
            foreach (var f in Fences)
                b.AddLine(FenceStyleFor(f.Plot, index.TryGetValue(f.Plot, out int k) ? k : 0), new[] { f.A, f.B });
            return b.Build(parent, objs, "Town_Fences");
        }
    }
}
