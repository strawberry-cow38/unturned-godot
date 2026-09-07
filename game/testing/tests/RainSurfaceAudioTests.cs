using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Rain on metal and on canvas (master 2026-09-07: "dig back up the rain sound variants you got
    /// but never wired, and then wire them to their relevant places").
    ///
    /// rain_metal_roof.wav and rain_tarp.wav sat on disk for a week referenced by nothing, behind a note that
    /// said they were blocked on confirming which WallPlan.Material palette indices are metal. That question
    /// has no answer -- that palette is building COLOUR, not construction material -- so the checks here are
    /// aimed at the two things that actually decide whether these clips are ever heard:
    ///
    /// WHETHER THE FILES BECOME EMITTERS AT ALL. A wav that fails to load leaves a null player and the
    /// feature is silently absent, which is indistinguishable from the week they spent unwired.
    ///
    /// AND WHETHER THE INDEX SURVIVES THE BUILD. RainSurfaces is FILLED by the placement loop and CLEARED
    /// once per build, and I first put the clear next to Bed.ResetForNewWorld -- which sits AFTER that loop,
    /// because beds are spawned after it. That would have emptied the index on every single build and left no
    /// symptom but silence. The ordering is asserted at the source, because no runtime assertion short of a
    /// full world build can see it.</summary>
    public sealed class RainSurfaceAudioTests : GameTest
    {
        public override string Name => "rain.surface_audio";
        public override double TimeoutSimSeconds => 20;

        static string ReadText(string resPath)
        {
            try { string p = ProjectSettings.GlobalizePath(resPath); return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : ""; }
            catch { return ""; }
        }

        public override IEnumerable<Step> Run()
        {
            // ---- THE CLIPS EXIST AND LOAD. Both were on disk all along; the question is whether anything
            // turns them into sound.
            foreach (var clip in new[] { "rain_metal_roof.wav", "rain_tarp.wav" })
            {
                string path = ProjectSettings.GlobalizePath("res://content/" + clip);
                T.Check($"{clip} is on disk", System.IO.File.Exists(path));
                var w = System.IO.File.Exists(path) ? AudioStreamWav.LoadFromFile(path) : null;
                T.Check($"...and loads as audio ({(w != null ? w.GetLength().ToString("0.00") + "s" : "null")})",
                        w != null && w.GetLength() > 0.1);
            }

            var rma = new RainMaterialAudio();
            World.AddChild(rma);
            yield return Ticks(2);
            // Four emitters, one per material. Counted rather than named because the names are private -- what
            // matters is that wiring two more clips produced two more players, not two more nulls.
            // -1 when the layout is not loaded (the usual case here). Recorded rather than assumed either
            // way, so the runtime bus check below runs when it can answer and stays quiet when it cannot.
            int rainBus = AudioServer.GetBusIndex("Rain");
            int players = 0;
            foreach (var c in rma.GetChildren()) if (c is AudioStreamPlayer3D) players++;
            T.Check($"four material emitters exist, not two ({players})", players == 4);
            foreach (var c in rma.GetChildren())
                if (c is AudioStreamPlayer3D pl)
                {
                    // The rain-bed trap, and it is worth re-checking per emitter: an AudioStreamWav with
                    // LoopMode set but LoopEnd left at 0 loops a ZERO-LENGTH region -- it plays silent while
                    // Playing stays true, so it looks wired and is inaudible.
                    var w = pl.Stream as AudioStreamWav;
                    T.Check($"{pl.Name}: loops a real region, not a zero-length one",
                            w != null && w.LoopMode == AudioStreamWav.LoopModeEnum.Forward && w.LoopEnd > 0);
                    // THE BUS IS CHECKED AT THE SOURCE, NOT OFF THE NODE, and the first version of this got
                    // it wrong: Godot silently falls back to Master when a named bus is absent, and the L1
                    // harness boots without the game's bus layout -- so reading pl.Bus back here measured the
                    // HARNESS's audio config and failed against a perfectly correct build. Asserted below
                    // instead, structurally, where the answer does not depend on what the runner loaded.
                    if (rainBus >= 0)
                        T.Check($"{pl.Name}: on the Rain bus, never SoundBus (that one is zombie HEARING)",
                                pl.Bus == "Rain");
                }

            // ---- CLASSIFICATION. These names are the only thing in the data that says what a prop is made
            // of, so a wrong table is the whole feature being wrong, quietly.
            T.Check("shipping containers are metal-roofed", RainSurfaces.IsMetalRoof("Container_2"));
            T.Check("...so are sheds, hangars, silos and the metal garage",
                    RainSurfaces.IsMetalRoof("Shed_0") && RainSurfaces.IsMetalRoof("Hangar_1")
                    && RainSurfaces.IsMetalRoof("Silo_Grain_0") && RainSurfaces.IsMetalRoof("Garage_Metal"));
            T.Check("tents are canvas", RainSurfaces.IsTarp("Tent_0") && RainSurfaces.IsTarp("Tent_3"));
            // The half that would otherwise pass by saying yes to everything.
            T.Check("a wooden garage is NOT metal", !RainSurfaces.IsMetalRoof("Garage_Birch"));
            T.Check("a house is not metal and not canvas",
                    !RainSurfaces.IsMetalRoof("House_00") && !RainSurfaces.IsTarp("House_00"));
            T.Check("a tent is not ALSO metal", !RainSurfaces.IsMetalRoof("Tent_0"));
            T.Check("a null name classifies as nothing rather than crashing",
                    !RainSurfaces.IsMetalRoof(null) && !RainSurfaces.IsTarp(null));

            // ---- THE INDEX. Add/Clear, and that nearest-of-each-kind is what a scan would find.
            RainSurfaces.Clear();
            T.Check("the index starts empty", RainSurfaces.Count == 0);
            RainSurfaces.Add(new Vector3(0f, 0f, 0f), tarp: false);
            RainSurfaces.Add(new Vector3(50f, 0f, 0f), tarp: false);
            RainSurfaces.Add(new Vector3(5f, 0f, 0f), tarp: true);
            T.Check($"...and records what it is given ({RainSurfaces.Count})", RainSurfaces.Count == 3);
            int metal = 0, tarp = 0;
            foreach (var (_, isTarp) in RainSurfaces.All) { if (isTarp) tarp++; else metal++; }
            T.Check($"...keeping the two kinds apart ({metal} metal, {tarp} tarp)", metal == 2 && tarp == 1);
            RainSurfaces.Clear();
            T.Check("Clear empties it, so a second map inherits nothing", RainSurfaces.Count == 0);

            // ---- THE TIER. RainMaterialAudio calls metal tier 2, off StructureCatalog's ordering. If a tier
            // is ever inserted below metal, every metal roof silently becomes whatever lands on 2 -- so the
            // constant is checked against the table it was read from rather than trusted.
            T.Check($"StructureCatalog tier 2 is still metal ({StructureCatalog.TierAt(2).Name})",
                    StructureCatalog.TierAt(2).Name == "metal");
            T.Check("...and tier 0 is still wood, so the ordering has not been rotated",
                    StructureCatalog.TierAt(0).Name == "wood");

            // ---- THE ORDERING BUG, pinned at the source. RainSurfaces.Clear() must run BEFORE the placement
            // loop fills it. Nothing short of a full world build can catch this at runtime, and a build is far
            // too heavy for L1 -- but the file is right here and the question is a line order.
            string wb = "";
            foreach (var cand in new[] { "res://WorldBuilder.cs", "res://../WorldBuilder.cs" })
            { wb = ReadText(cand); if (wb.Length > 0) break; }
            T.Check($"WorldBuilder.cs was readable ({wb.Length} chars)", wb.Length > 500);
            if (wb.Length > 0)
            {
                int clearAt = wb.IndexOf("RainSurfaces.Clear()", System.StringComparison.Ordinal);
                int addAt = wb.IndexOf("RainSurfaces.Add(", System.StringComparison.Ordinal);
                T.Check("the build clears the index and fills it", clearAt >= 0 && addAt >= 0);
                T.Check($"...and CLEARS BEFORE IT FILLS (clear@{clearAt}, add@{addAt})",
                        clearAt >= 0 && addAt >= 0 && clearAt < addAt);
            }

            // ...and the structural half, which holds whatever the runner loaded: every emitter comes out of
            // ONE factory, and that factory names the Rain bus. One construction path is what makes "all four
            // are on Rain" true by construction rather than by four separate correct decisions.
            string src = ReadText("res://RainMaterialAudio.cs");
            if (src.Length == 0) src = ReadText("res://../RainMaterialAudio.cs");
            T.Check($"RainMaterialAudio.cs was readable ({src.Length} chars)", src.Length > 500);
            if (src.Length > 0)
            {
                int busRain = 0, idx = 0;
                while ((idx = src.IndexOf("Bus = \"Rain\"", idx, System.StringComparison.Ordinal)) >= 0) { busRain++; idx++; }
                T.Check($"exactly one place names the bus ({busRain})", busRain == 1);
                T.Check("...and it is inside MakeEmitter, which every emitter goes through",
                        src.IndexOf("MakeEmitter", System.StringComparison.Ordinal) >= 0
                        && src.IndexOf("Bus = \"Rain\"", System.StringComparison.Ordinal) > src.IndexOf("AudioStreamPlayer3D MakeEmitter", System.StringComparison.Ordinal));
                T.Check("no emitter here is wired to SoundBus", !src.Contains("Bus = \"SoundBus\""));
                T.Check("all four clips are loaded by that one factory",
                        src.Contains("MakeEmitter(\"res://content/rain_car.wav\")")
                        && src.Contains("MakeEmitter(\"res://content/rain_foliage.wav\")")
                        && src.Contains("MakeEmitter(\"res://content/rain_metal_roof.wav\")")
                        && src.Contains("MakeEmitter(\"res://content/rain_tarp.wav\")"));
            }

            rma.QueueFree();
        }
    }
}
