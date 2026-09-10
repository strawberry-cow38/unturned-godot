using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>What the ground under your feet is made of (master 2026-09-10: "wire all the audio properly.
    /// how it is done in the source game").
    ///
    /// Terrain.SurfAt was a hardcoded splat-INDEX table, and against the real palette every row but one was
    /// wrong: it called layer 1 sand when layer 1 is a wheat field, layer 3 concrete when layer 3 is gravel,
    /// layer 4 "rock/cliff" when layer 4 is the road (right answer, wrong reason), layer 6 dirt when layer 6
    /// is SNOW, and swept bare dirt and stone into the grass default. That is not a rounding error -- it is
    /// why the snow, gravel and rock banks looked unused, and why Yukon walked on concrete.
    ///
    /// The checks below are against the SHIPPED layers.txt for each map rather than against the new switch,
    /// because the switch agreeing with itself proves nothing. They also pin the two layers whose material
    /// DIFFERS between maps -- 5 and 6 -- since those are what make an index table unfixable rather than
    /// merely wrong.</summary>
    public sealed class TerrainSurfaceTests : GameTest
    {
        public override string Name => "terrain.surface";
        public override double TimeoutSimSeconds => 20;

        static string[] LayersOf(string mapDir)
        {
            string p = ProjectSettings.GlobalizePath($"res://content/{mapDir}/layers.txt");
            if (!System.IO.File.Exists(p)) return System.Array.Empty<string>();
            var outp = new List<string>();
            foreach (var ln in System.IO.File.ReadAllLines(p)) { var t = ln.Trim(); if (t.Length > 0) outp.Add(t); }
            return outp.ToArray();
        }

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);

            // ---- THE NAMES ARE READ, NOT ASSUMED. If a bake ever drops layers.txt the fallback is PEI's, and
            // silently walking on the wrong map's palette is the failure this whole change is about.
            foreach (var map in new[] { "terrain", "terrain_yukon", "terrain_washington" })
            {
                var names = LayersOf(map);
                T.Check($"{map}/layers.txt ships {names.Length} layers", names.Length == 8);
            }

            // ---- EVERY SHIPPED LAYER RESOLVES TO WHAT IT SAYS IT IS, on all three maps.
            var expect = new Dictionary<string, PlayerController.Surf>
            {
                { "Dirt", PlayerController.Surf.Dirt }, { "Dirt 01", PlayerController.Surf.Dirt },
                { "Farm Wheat", PlayerController.Surf.Grass }, { "Farm Corn", PlayerController.Surf.Grass },
                { "Grass", PlayerController.Surf.Grass }, { "Grass 01", PlayerController.Surf.Grass },
                { "Gravel", PlayerController.Surf.Gravel }, { "Gravel Shore", PlayerController.Surf.Gravel },
                { "Road", PlayerController.Surf.Concrete },
                { "Sand 01", PlayerController.Surf.Sand },
                { "Snow", PlayerController.Surf.Snow },
                { "Stone", PlayerController.Surf.Rock }, { "Stone 01", PlayerController.Surf.Rock },
            };
            foreach (var map in new[] { "terrain", "terrain_yukon", "terrain_washington" })
                foreach (var n in LayersOf(map))
                {
                    bool known = expect.TryGetValue(n, out var want);
                    T.Check($"{map}: \"{n}\" is a layer the surface map knows", known);
                    if (known)
                        T.Check($"{map}: \"{n}\" -> {want} (got {Terrain.SurfForMaterial(n)})",
                                Terrain.SurfForMaterial(n) == want);
                }

            // ---- THE ROWS THE OLD INDEX TABLE GOT WRONG. Each of these fails against the value the bug
            // produced, not merely passes against the fix.
            T.Check("a wheat field is not SAND (old layer 1)", Terrain.SurfForMaterial("Farm Wheat") != PlayerController.Surf.Sand);
            T.Check("gravel is not CONCRETE (old layer 3)", Terrain.SurfForMaterial("Gravel") != PlayerController.Surf.Concrete);
            T.Check("SNOW is not dirt (old layer 6 -- this is the Yukon one)", Terrain.SurfForMaterial("Snow") != PlayerController.Surf.Dirt);
            T.Check("bare dirt is not grass (old default)", Terrain.SurfForMaterial("Dirt") != PlayerController.Surf.Grass);
            T.Check("stone is not grass (old default)", Terrain.SurfForMaterial("Stone") != PlayerController.Surf.Grass);
            T.Check("the road really is concrete (the one row that was right)", Terrain.SurfForMaterial("Road") == PlayerController.Surf.Concrete);

            // ---- LAYERS 5 AND 6 ARE DIFFERENT MATERIALS PER MAP. This is why no index table can be correct:
            // PEI's 5 is sand where Yukon's and Washington's is a gravel shore, and PEI/Yukon's 6 is snow
            // where Washington's is a second grass.
            var pei = LayersOf("terrain"); var yuk = LayersOf("terrain_yukon"); var wash = LayersOf("terrain_washington");
            if (pei.Length == 8 && yuk.Length == 8 && wash.Length == 8)
            {
                T.Check("layer 5 differs between PEI and Yukon", pei[5] != yuk[5]);
                T.Check($"...PEI 5 is sand ({pei[5]})", Terrain.SurfForMaterial(pei[5]) == PlayerController.Surf.Sand);
                T.Check($"...Yukon 5 is gravel ({yuk[5]})", Terrain.SurfForMaterial(yuk[5]) == PlayerController.Surf.Gravel);
                T.Check("layer 6 differs between Yukon and Washington", yuk[6] != wash[6]);
                T.Check($"...Yukon 6 is SNOW ({yuk[6]})", Terrain.SurfForMaterial(yuk[6]) == PlayerController.Surf.Snow);
                T.Check($"...Washington 6 is grass ({wash[6]})", Terrain.SurfForMaterial(wash[6]) == PlayerController.Surf.Grass);
            }

            // ---- AND THE NEW SURFACES REACH REAL BANKS. A Surf value whose bank is empty is worse than no
            // value at all: it reads as wired and plays concrete.
            foreach (var s in new[] { PlayerController.Surf.Gravel, PlayerController.Surf.Snow, PlayerController.Surf.Ice, PlayerController.Surf.Rock })
            {
                string foot = GameAudio.FootSurface(s);
                T.Check($"{s}: footstep bank {foot} has clips", GameAudio.Bank("footsteps", foot + "_walk").Length > 0);
                T.Check($"{s}: landing bank {GameAudio.LandSurface(s)} has clips", GameAudio.Bank("landing", GameAudio.LandSurface(s)).Length > 0);
                T.Check($"{s}: bullet bank {GameAudio.BulletSurface(s)} has clips", GameAudio.Bank("bulletimpacts", GameAudio.BulletSurface(s)).Length > 0);
            }
            T.Check("snow walks on snow, not concrete", GameAudio.FootSurface(PlayerController.Surf.Snow) == "snow");
            T.Check("gravel walks on gravel", GameAudio.FootSurface(PlayerController.Surf.Gravel) == "gravel");
            T.Check("a bullet in stone uses the rock bank", GameAudio.BulletSurface(PlayerController.Surf.Rock) == "rock");
            T.Check("...and stone still WALKS like concrete -- there is no rock footstep bank",
                    GameAudio.FootSurface(PlayerController.Surf.Rock) == "concrete");

            // ---- THE ENUM IS APPEND-ONLY. SurfMeta is an int written onto colliders all over the world, so a
            // reorder renames every tagged prop at once and nothing would report it.
            T.Check("Concrete is still 0", (int)PlayerController.Surf.Concrete == 0);
            T.Check("Water is still 6", (int)PlayerController.Surf.Water == 6);
            T.Check("the four new values were appended after it", (int)PlayerController.Surf.Gravel == 7
                    && (int)PlayerController.Surf.Snow == 8 && (int)PlayerController.Surf.Ice == 9 && (int)PlayerController.Surf.Rock == 10);
        }
    }
}
