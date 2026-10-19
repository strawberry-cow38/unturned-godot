using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>⭐ "STORIES" -- environmental storytelling along the roads (strawberry 2026-10-19: "along road splines / along
    /// streets / in parking lots, you may find a 'story' - something that happened. wrecked cars, police blockades, working
    /// cars, car crashes, military blockades, etc etc").
    ///
    /// A story is a small SEEDED scene -- a cluster of props and/or vehicles -- dropped at a point on a road or in a parking
    /// lot, oriented to the road, telling what happened there. They are rare and spaced out, so coming across one reads as a
    /// find rather than set dressing. Deterministic from the town seed; the same town always tells the same stories.
    ///
    /// ⚠ PIECES, not new assets: the game's cars (Vehicle.BuildByName), the Wreck_0 prop for a static wreck, and the barrier
    /// catalogue (Block_Road, Barrel, Spikes, Military_Sign/Tower). Vehicles are real drivable entities in a town you PLAY;
    /// wrecks are the static prop so they always look wrecked and cost no physics.</summary>
    public sealed partial class TownRoadGrid
    {
        /// <summary>Whether the town drops stories along its roads. UG_TOWNNOSTORIES=1 off.</summary>
        public bool Stories = System.Environment.GetEnvironmentVariable("UG_TOWNNOSTORIES") != "1";
        /// <summary>Average metres between candidate story sites along a road, and the chance a candidate becomes a story.</summary>
        public float StorySpacing = 170f, StoryChance = 0.55f;
        /// <summary>A cap on drivable vehicles the stories place, so a big town does not spawn a hundred cars.</summary>
        public int StoryVehicleCap = 40;
        public int StorySites, StoryVehicles, StoryProps;
        /// <summary>Where the first story landed, for a camera.</summary>
        public Vector3 FirstStoryAt;

        /// <summary>The scene a site tells. Weighted in PickStory -- ordinary things common, a military checkpoint rare.</summary>
        enum StoryKind { AbandonedCar, Breakdown, Wreck, Crash, PoliceBlock, MilitaryCheck }
        /// <summary>UG_TOWNSTORY=abandoned|breakdown|wreck|crash|police|military forces every site to one kind (a test aid).</summary>
        static readonly string StoryForce = (System.Environment.GetEnvironmentVariable("UG_TOWNSTORY") ?? "").ToLowerInvariant();
        readonly int[] _kindCount = new int[6];

        static readonly string[] CivilianCars = { "sedan", "hatchback", "offroader", "van", "wagon", "jeep", "truck" };
        const string WreckProp = "Wreck_0", Barrier = "Block_Road_0", Barrel = "Barrel_0", Spikes = "Spikes_0", MilSign = "Military_Sign_0";

        /// <summary>Drop the stories. Call after the props and the power lines (they stand clear of the carriageway, and a
        /// blockade wants the finished road). `world` is the node drivable vehicles are added to (the editor / the play world);
        /// `play` lifts a car a little more so play-mode physics settles it instead of flinging it off the collider.</summary>
        public int BuildStories(RoadField roads, EditorObjects objs, Terrain terr, Godot.Node world, bool play)
        {
            StorySites = StoryVehicles = StoryProps = 0;
            if (!Stories || roads == null || objs == null) return 0;

            // ---- 1. sites along the roads: every ~StorySpacing, a candidate, kept off the junctions, taken with StoryChance
            foreach (int r in Built)
            {
                float L = roads.RoadLength(r);
                if (L < 55f) continue;                                   // too short to stage anything on
                int n = Mathf.Max(1, Mathf.RoundToInt(L / StorySpacing));
                for (int k = 0; k < n; k++)
                {
                    ulong h = Mix(((ulong)(uint)Seed << 32) ^ Mix(((ulong)(uint)r << 20) ^ (uint)k ^ 0x570A1EUL));
                    if (Unit(h, 0) >= StoryChance) continue;
                    float frac = (k + 0.2f + Unit(h, 12) * 0.6f) / n;        // jittered within its slice
                    float d = L * frac;
                    if (d < 14f || d > L - 14f) continue;                    // clear of both junctions
                    if (!roads.EvaluateAlong(r, d, out var p, out var t, snapTerrain: false)) continue;
                    var along = new Vector3(t.X, 0f, t.Z).Normalized();
                    float half = roads.RoadHalfWidth(r);
                    PlaceStory(PickStory(h, half), roads, objs, terr, world, play, new Vector3(p.X, 0f, p.Z), along, half, h);
                }
            }

            // ---- 2. a lonely car in some parking lots (an abandoned one, left behind)
            foreach (var lot in ParkingLotsLaid)
            {
                if (lot?.Stalls == null || lot.Stalls.Count == 0) continue;
                ulong h = Mix(((ulong)(uint)Seed << 32) ^ Mix((ulong)(uint)Mathf.RoundToInt(lot.Lot.Front.X) ^ ((ulong)(uint)Mathf.RoundToInt(lot.Lot.Front.Z) << 20) ^ 0x10757UL));
                if (Unit(h, 0) >= 0.5f) continue;
                var (at, _, into) = lot.Stalls[(int)(Unit(h, 16) * lot.Stalls.Count) % lot.Stalls.Count];
                var dir = new Vector3(into.X, 0f, into.Z).Normalized();
                if (dir.LengthSquared() < 0.1f) dir = Vector3.Forward;
                PlaceVehicle(world, terr, CivilianCars[(int)(Unit(h, 32) * CivilianCars.Length) % CivilianCars.Length], (int)(Unit(h, 40) * 4) % 4,
                             new Vector3(at.X, 0f, at.Z), ProcIsland.YawForDir(dir.X, dir.Z), play);
                StorySites++;
            }

            Log.Print($"[towngrid] stories: {StorySites} site(s) -- {StoryVehicles} vehicle(s), {StoryProps} prop(s)"
                    + $" [abandoned {_kindCount[0]}, breakdown {_kindCount[1]}, wreck {_kindCount[2]}, crash {_kindCount[3]}, police {_kindCount[4]}, military {_kindCount[5]}]"
                    + (StorySites > 0 ? $"; first at ({FirstStoryAt.X:0.0}, {FirstStoryAt.Z:0.0})" : ""));
            return StorySites;
        }

        /// <summary>Which scene, weighted: ordinary first, a military checkpoint rare. A blockade needs a carriageway wide
        /// enough to be worth blocking.</summary>
        StoryKind PickStory(ulong h, float half)
        {
            switch (StoryForce)
            {
                case "abandoned": return StoryKind.AbandonedCar;
                case "breakdown": return StoryKind.Breakdown;
                case "wreck":     return StoryKind.Wreck;
                case "crash":     return StoryKind.Crash;
                case "police":    return StoryKind.PoliceBlock;
                case "military":  return StoryKind.MilitaryCheck;
            }
            float u = Unit(h, 24);
            bool wide = half >= 6f;      // a main-ish street; a narrow lane gets no blockade
            // abandoned 30 | breakdown 18 | wreck 20 | crash 15 | police 12 (wide) | military 5 (wide)
            if (u < 0.30f) return StoryKind.AbandonedCar;
            if (u < 0.48f) return StoryKind.Breakdown;
            if (u < 0.68f) return StoryKind.Wreck;
            if (u < 0.83f) return StoryKind.Crash;
            if (wide && u < 0.95f) return StoryKind.PoliceBlock;
            if (wide) return StoryKind.MilitaryCheck;
            return StoryKind.Wreck;      // narrow street fallback
        }

        void PlaceStory(StoryKind kind, RoadField roads, EditorObjects objs, Terrain terr, Godot.Node world, bool play,
                        Vector3 at, Vector3 along, float half, ulong h)
        {
            var right = new Vector3(-along.Z, 0f, along.X);          // the road's right in plan
            int side = Unit(h, 2) < 0.5f ? 1 : -1;
            float shoulder = half + PavementGap + 1.2f;             // just off the carriageway, on the verge/kerb
            if (StorySites == 0) FirstStoryAt = at;

            switch (kind)
            {
                case StoryKind.AbandonedCar:
                {
                    // parked along the kerb, nosed the way the road runs, a touch askew
                    var pos = at + right * (side * shoulder);
                    float yaw = ProcIsland.YawForDir(along.X, along.Z) + (Unit(h, 10) - 0.5f) * 16f;
                    PlaceVehicle(world, terr, Civ(h), Var(h), pos, yaw, play);
                    break;
                }
                case StoryKind.Breakdown:
                {
                    // on the shoulder facing the traffic's way, a barrel set out behind it as a warning
                    var pos = at + right * (side * shoulder);
                    float yaw = ProcIsland.YawForDir(along.X, along.Z);
                    PlaceVehicle(world, terr, Civ(h), Var(h), pos, yaw, play);
                    PlaceProp(objs, terr, Barrel, pos - along * 4f + right * (side * 0.3f), 0f);
                    break;
                }
                case StoryKind.Wreck:
                {
                    // a static wreck half off the road, flung to any angle
                    var pos = at + right * (side * (shoulder - 0.6f));
                    PlaceProp(objs, terr, WreckProp, pos, Unit(h, 10) * 360f);
                    if (Unit(h, 14) < 0.5f) PlaceProp(objs, terr, Barrel, pos + along * (2.5f + Unit(h, 18) * 2f) - right * (side * 0.8f), Unit(h, 20) * 360f);
                    break;
                }
                case StoryKind.Crash:
                {
                    // a wreck skewed into the lane, a car nosed into it, debris -- the moment it happened
                    var wreckAt = at + right * (side * (half * 0.4f));
                    PlaceProp(objs, terr, WreckProp, wreckAt, ProcIsland.YawForDir(along.X, along.Z) + 55f);
                    var carAt = at - along * 5.5f + right * (side * (half * 0.2f));
                    PlaceVehicle(world, terr, Civ(h), Var(h), carAt, ProcIsland.YawForDir(along.X, along.Z) - 18f, play);
                    PlaceProp(objs, terr, Barrel, at + right * (side * (half + 0.5f)) - along * 2f, Unit(h, 22) * 360f);
                    break;
                }
                case StoryKind.PoliceBlock:
                {
                    // barriers across the far half, a gap this side, two cruisers angled behind them, barrels
                    BarrierLine(objs, terr, at, along, right, half, side, h);
                    float yaw = ProcIsland.YawForDir(along.X, along.Z);
                    PlaceVehicle(world, terr, "police", Var(h), at - along * 7f + right * (side * (half * 0.5f)), yaw + 35f, play);
                    PlaceVehicle(world, terr, "police", Var(h ^ 0x9UL), at - along * 7f - right * (side * (half * 0.5f)), yaw - 35f, play);
                    PlaceProp(objs, terr, Barrel, at + right * (side * (half - 1f)), 0f);
                    break;
                }
                case StoryKind.MilitaryCheck:
                {
                    // barriers + a spike strip across, a sign, and an APC or humvee pulled up beside
                    BarrierLine(objs, terr, at, along, right, half, side, h);
                    PlaceProp(objs, terr, Spikes, at - along * 2.5f, ProcIsland.YawForDir(right.X, -right.Z));
                    PlaceProp(objs, terr, MilSign, at + right * (side * (half + 1.5f)) + along * 3f, ProcIsland.YawForDir(-along.X, -along.Z));
                    PlaceVehicle(world, terr, Unit(h, 26) < 0.5f ? "apc" : "humvee", Var(h), at - along * 9f - right * (side * (half + 1.5f)),
                                 ProcIsland.YawForDir(along.X, along.Z) + 20f, play);
                    break;
                }
            }
            _kindCount[(int)kind]++;
            StorySites++;
        }

        /// <summary>A CHICANE of concrete barriers across the carriageway (strawberry 2026-10-19: "shouldnt they have a
        /// different layout? like alternating 2 lanes blocked so important traffic can still pass through ... and its also
        /// going onto the sidewalk a bit"). TWO staggered rows a few metres apart, each blocking from one kerb and leaving a
        /// gap at the other, the gaps on OPPOSITE sides -- so a vehicle weaves through. The barriers stay INSIDE the
        /// carriageway: a 3 m block centred at `lat` reaches lat +-1.5, so centres are clamped to +-(half - 1.5) and the ends
        /// sit at the kerb, never on the pavement.</summary>
        void BarrierLine(EditorObjects objs, Terrain terr, Vector3 at, Vector3 along, Vector3 right, float half, int side, ulong h)
        {
            // ⚠ barriers SPAN the road: their 3 m length runs along `right` (across the carriageway), not along it. Same
            // convention as EditorBlockRoad -- YawForDir(d.X, -d.Z) orients a Block_Road_0's length along d -- fed the ACROSS
            // direction. My first cut dropped the -Z and the blocks ran WITH the road line (strawberry 2026-10-19).
            float yaw = ProcIsland.YawForDir(right.X, -right.Z);
            const float Len = 3f, HalfLen = Len * 0.5f, Lane = 3.5f;   // Block_Road_0 is 3 m; the weave lane left open
            float cLo = -half + HalfLen, cHi = half - HalfLen;         // centres that keep a block's ENDS inside the kerbs
            if (cHi - cLo < Len) { PlaceProp(objs, terr, Barrier, at, yaw); return; }   // too narrow for a chicane: one block
            for (int row = 0; row < 2; row++)
            {
                // the two rows sit a car-length apart along the road; row 0's gap is on +side, row 1's on -side
                var rowAt = at + along * (row == 0 ? -3f : 3f);
                bool gapPlus = (row == 0) == (side > 0);
                float a = gapPlus ? cLo : cLo + Lane;                  // block from one kerb...
                float b = gapPlus ? cHi - Lane : cHi;                  // ...to Lane short of the other
                for (float lat = a; lat <= b + 1e-3f; lat += Len)
                    PlaceProp(objs, terr, Barrier, rowAt + right * Mathf.Min(lat, cHi), yaw);
            }
        }

        string Civ(ulong h) => CivilianCars[(int)(Unit(h, 30) * CivilianCars.Length) % CivilianCars.Length];
        static int Var(ulong h) => (int)(Unit(h, 44) * 4) % 4;

        void PlaceProp(EditorObjects objs, Terrain terr, string name, Vector3 at, float yawDeg)
        {
            at.Y = terr != null ? terr.SampleHeight(at.X, at.Z) : 0f;
            var basis = SplineTiling.SeatedBasis(terr, at, yawDeg, 8f, out _);
            if (objs.Place(name, at, basis) != null) StoryProps++;
        }

        void PlaceVehicle(Godot.Node world, Terrain terr, string name, int variant, Vector3 at, float yawDeg, bool play)
        {
            if (world == null || StoryVehicles >= StoryVehicleCap) return;
            var v = Vehicle.BuildByName(name, variant);
            if (v == null) return;
            world.AddChild(v);
            float gy = terr != null ? terr.SampleHeight(at.X, at.Z) : 0f;
            // a car dropped exactly on the collider can interpenetrate and get flung in play; lift it a little so it settles.
            // In the editor (no physics) the lift is small enough to read as parked.
            v.GlobalPosition = new Vector3(at.X, gy + (play ? 1.0f : 0.25f), at.Z);
            v.RotationDegrees = new Vector3(0f, yawDeg, 0f);
            StoryVehicles++;
        }
    }
}
