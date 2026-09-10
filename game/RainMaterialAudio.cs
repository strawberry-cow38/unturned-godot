using Godot;

namespace UnturnedGodot
{
    // Positional rain-on-material audio (master 2026-08-30): a material prop near the player EMITS its own
    // rain-on-that-material sound from itself, 3D-positioned + gated on rain intensity + faded over a radius. Walk near
    // a car -> rain drums on the car; near trees -> the canopy hiss; near a metal shed -> the tin drum. Instead of an
    // emitter on every prop (there are thousands of trees), ONE pooled 3D emitter per material snaps to the NEAREST
    // prop of that material inside the radius each poll -- cost is O(one sphere query), not O(props).
    //
    // ⚠⚠ on the "Rain" bus (NOT SoundBus -- that's the zombie-HEARING path; a rain loop through it = permanent aggro).
    // Materials wired: car (Vehicle), foliage (TreeTrunk), metal (metal ROOFS -- both player-built and the map's
    // own), tarp (tents). freesound CC0 layers, see content/CREDITS.md.
    //
    // THE NOTE THAT USED TO SIT HERE said metal and tarp were blocked on confirming which WallPlan.Material
    // palette indices are metal. That was the wrong question and it parked two extracted clips for a week:
    // WallPlan.Material indexes the retail BUILDING COLOUR palette (content/wall_palettes.tsv -- Airport_0,
    // House_00, Bank_0), which is map authoring and contains no construction material at all. The index it was
    // waiting for does not exist. (cow tools, who wrote the note, found this and handed over the real source.)
    //
    // The two real sources, and they are different in kind:
    //   PLAYER-BUILT -- StructureManager.PieceForCollider gives the exact Piece behind a physics hit, and
    //     metal is Tier 2 (StructureCatalog.Tiers: wood 0, brick 1, metal 2) with Construct == Roof. Roof,
    //     not any metal piece: rain lands on things from ABOVE, and a metal WALL beside you is not what the
    //     clip is of. These are on layer bit0, which the sphere query below already masks.
    //   THE MAP'S OWN -- 98 placed props are metal-roofed (shipping containers, sheds, hangars, grain silos)
    //     and 8 are canvas (tents), and those are where a player actually stands. They are NOT reachable by
    //     the sphere query: a prop body only lands on bit0 when it blocks line of sight, everything else is
    //     on 6|8 (WorldBuilder), and widening the mask would flood a 48-hit cap with fence posts. So the
    //     world build records their positions in RainSurfaces as it places them -- a prop does not move, so
    //     the nearest one is a scan of ~106 vectors, cheaper than the query it replaces.
    public partial class RainMaterialAudio : Node
    {
        public float Intensity;   // rint 0..1 (WeatherManager drives it)
        public Camera3D Cam;      // listener position (the player camera)
        public float CanopyShelter = 1f;   // 1 = open sky .. 0 = under the nearest canopy's centre (WeatherManager reads it for the muffle)

        /// <summary>Is the CAR layer actually sounding? The emitters are private and a dead one is silent, which
        /// is indistinguishable from "no car nearby" -- the exact ambiguity that let the collision-layer move
        /// kill this layer unnoticed. Exposed so a test can stand a jeep next to the listener and demand noise.</summary>
        public bool DebugCarPlaying => _car != null && GodotObject.IsInstanceValid(_car) && _car.Playing;
        public Vector3 DebugCarPosition => _car != null && GodotObject.IsInstanceValid(_car) ? _car.GlobalPosition : Vector3.Zero;

        const float Radius = 16f;         // "a radius where the material sound is produced from" (master) -- audible range per prop
        const float PineRadius = 28f;     // pines carry a bigger canopy -> a wider foliage radius (master: expand the pine's foliage rain radius)
        const float PollSeconds = 0.25f;  // re-scan for the nearest material prop 4x/sec (props don't teleport; cheap)

        AudioStreamPlayer3D _car, _foliage, _metal, _tarp;   // one emitter per material, re-homed to the nearest prop of that material
        float _poll;

        public override void _Ready()
        {
            TickHub.AddProcess(this, HubProcess); SetProcess(false);   // PERF: hub-ticked (see TickHub.AddProcess)
            _car = MakeEmitter("res://content/rain_car.wav");
            _foliage = MakeEmitter("res://content/rain_foliage.wav");
            _metal = MakeEmitter("res://content/rain_metal_roof.wav");
            _tarp = MakeEmitter("res://content/rain_tarp.wav");
        }

        AudioStreamPlayer3D MakeEmitter(string res)
        {
            var p = ProjectSettings.GlobalizePath(res);
            if (!System.IO.File.Exists(p) || AudioStreamWav.LoadFromFile(p) is not AudioStreamWav w) return null;
            w.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            w.LoopEnd = (int)(w.GetLength() * w.MixRate + 0.5f);   // ⚠ else LoopEnd defaults 0 -> plays silent (the rain-bed trap)
            var pl = new AudioStreamPlayer3D
            {
                Stream = w, Bus = "Rain", VolumeDb = -80f, Autoplay = false,
                UnitSize = Radius * 0.6f, MaxDistance = Radius, MaxDb = 0f,   // full-ish within the prop's reach, silent past the radius
            };
            AddChild(pl);
            return pl;
        }

        public override void _Process(double delta) => HubProcess(delta);   // forwarder for direct callers; the engine's callback is off (SetProcess(false) in _Ready) -- TickHub ticks HubProcess
        public void HubProcess(double delta)
        {
            float rint = Mathf.Clamp(Intensity, 0f, 1f);
            if (rint < 0.02f || Cam == null) { Silence(_car); Silence(_foliage); Silence(_metal); Silence(_tarp); RenderingServer.GlobalShaderParameterSet("rain_canopy", new Vector4(0f, 0f, 1f, 0f)); CanopyShelter = 1f; return; }
            _poll -= (float)delta;
            if (_poll > 0f) return;
            _poll = PollSeconds;

            Vector3 at = Cam.GlobalPosition;

            // CARS COME OFF Vehicle.Live, NOT THE SPHERE QUERY BELOW (strawberry 2026-09-07 "wire up the car
            // rain sound when near or inside a car" -- it had stopped working entirely).
            //
            // It was found by the query, on collision bit 0, and that was correct when this file was written.
            // Then the mesh hitbox landed: Vehicle.FinaliseHitboxLayers moves the chassis OFF bit 0 and bit 5
            // onto ChassisBit, and the hull mesh that replaces it sits on HitMeshBit -- so a vehicle is on
            // neither of the bits this mask names, and the car layer has been silently dead since. Nothing
            // failed; the emitter simply never had a position to play from, which sounds exactly like rain
            // that has no car in it.
            //
            // Widening the mask would fix today and rot the same way tomorrow, and it would also spend the
            // 48-hit cap on vehicle panes, turret bodies and door bodies -- several colliders per car. The
            // live list is the same answer this file already reached for the map's props: a registry that
            // cannot be invalidated by a collision-layer change, scanned in O(vehicles in the world).
            //
            // Distance is to the vehicle's ORIGIN, which is inside its hull, so "inside a car" is ~0 m and
            // plays loudest -- the other half of what was asked for. A car's own length sits well inside
            // Radius, so a long body is not worth an AABB here.
            Vector3? carPos = null;
            float carD = float.MaxValue;
            foreach (var veh in Vehicle.Live)
            {
                if (veh == null || !GodotObject.IsInstanceValid(veh)) continue;
                float d = veh.GlobalPosition.DistanceTo(at);
                if (d < carD) { carD = d; carPos = veh.GlobalPosition; }
            }
            if (carD > Radius) { carPos = null; carD = float.MaxValue; }

            // The rest of the materials still need the physics query. If there is no space state (a headless
            // harness with no physics world), the car layer must still run -- it no longer depends on one.
            if (Cam.GetWorld3D()?.DirectSpaceState is not PhysicsDirectSpaceState3D space)
            {
                Drive(_car, carPos, rint);
                Silence(_foliage); Silence(_metal); Silence(_tarp);
                return;
            }

            // one sphere query for everything nearby on the world layer, classified by node type -> nearest per material
            var q = new PhysicsShapeQueryParameters3D
            {
                Shape = new SphereShape3D { Radius = PineRadius },   // widest radius (pines); per-emitter MaxDistance attenuates the rest
                Transform = new Transform3D(Basis.Identity, at),
                CollisionMask = 1u << 0, CollideWithBodies = true, CollideWithAreas = false,
            };
            var hits = space.IntersectShape(q, 48);
            Vector3? folPos = null, metalPos = null, tarpPos = null;
            float folD = float.MaxValue, metalD = float.MaxValue, tarpD = float.MaxValue;
            TreeTrunk folTree = null;
            foreach (var h in hits)
            {
                if (h["collider"].As<GodotObject>() is not Node3D n) continue;
                float d = n.GlobalPosition.DistanceTo(at);
                if (FindAncestor<Vehicle>(n) != null) continue;   // cars are handled off Vehicle.Live above; skip so one on bit0 (mesh hitbox off) is not classified as something else
                if (StructureManager.Instance?.PieceForCollider(n) is StructureManager.Piece piece)
                {
                    // A player-built METAL ROOF. Construct AND tier both matter: rain falls from above, so a
                    // metal wall next to you is not this sound, and a wooden roof is not this sound either.
                    if (piece.Construct == EConstruct.Roof && piece.Tier == MetalTier && d < metalD) { metalD = d; metalPos = n.GlobalPosition; }
                }
                else { var tt = FindAncestor<TreeTrunk>(n); if (tt != null && d < folD) { folD = d; folPos = n.GlobalPosition; folTree = tt; } }
            }
            // pine foliage reaches further than other trees (master: expand the pine's radius only)
            bool pine = folTree?.TreeName?.Contains("Pine") ?? false;
            if (_foliage != null) { float fr = pine ? PineRadius : Radius; _foliage.MaxDistance = fr; _foliage.UnitSize = fr * 0.6f; }
            // canopy rain shadow + shelter: the nearest tree's leaves occlude the rain BELOW them (the streak shader reads
            // rain_canopy) and muffle the sound while you're under -- but the rain still falls OUTSIDE the canopy circle.
            if (folPos is Vector3 fp)
            {
                float cr = pine ? 6f : 4f;   // canopy shadow radius (pines broader)
                RenderingServer.GlobalShaderParameterSet("rain_canopy", new Vector4(fp.X, fp.Z, cr, 1f));
                float dc = new Vector2(at.X - fp.X, at.Z - fp.Z).Length();
                CanopyShelter = Mathf.Clamp(dc / cr, 0f, 1f);   // 0 = under the centre (occluded + muffled) .. 1 = outside
            }
            else { RenderingServer.GlobalShaderParameterSet("rain_canopy", new Vector4(0f, 0f, 1f, 0f)); CanopyShelter = 1f; }
            // The MAP's own metal roofs and canvas, from the placement index rather than the physics query --
            // see the header. Nearest wins between a player-built roof and a shipping container, because what
            // the player hears should be whatever is actually closest to them.
            foreach (var (pos, tarp) in RainSurfaces.All)
            {
                float d = pos.DistanceTo(at);
                if (d > Radius) continue;
                if (tarp) { if (d < tarpD) { tarpD = d; tarpPos = pos; } }
                else if (d < metalD) { metalD = d; metalPos = pos; }
            }
            Drive(_car, carPos, rint);
            Drive(_foliage, folPos, rint);
            Drive(_metal, metalPos, rint);
            Drive(_tarp, tarpPos, rint);
            if (System.Environment.GetEnvironmentVariable("UG_RAINMATDBG") != null)
                Log.Print($"[rainmat] car={carPos.HasValue}({carD:0.0}m) foliage={folPos.HasValue}({folD:0.0}m) metal={metalPos.HasValue}({metalD:0.0}m) tarp={tarpPos.HasValue}({tarpD:0.0}m) canopyShelter={CanopyShelter:0.00} rint={rint:0.00} surfaces={RainSurfaces.Count}");
        }

        /// <summary>StructureCatalog.Tiers is ordered weakest -> strongest and the order IS the upgrade path,
        /// so metal is index 2. Named rather than spelled 2 inline, because a fourth tier inserted anywhere
        /// below it would silently turn every metal roof into whatever landed on 2.</summary>
        const int MetalTier = 2;

        static T FindAncestor<T>(Node n) where T : Node { for (; n != null; n = n.GetParent()) if (n is T t) return t; return null; }

        static void Drive(AudioStreamPlayer3D pl, Vector3? pos, float rint)
        {
            if (pl == null) return;
            if (pos is Vector3 p)
            {
                pl.GlobalPosition = p;
                if (!pl.Playing)
                {
                    double len = (pl.Stream as AudioStreamWav)?.GetLength() ?? 0.0;
                    pl.Play(len > 0.0 ? (float)(GD.Randf() * len) : 0f);   // random start so a row of same-material props doesn't phase-lock
                }
                pl.VolumeDb = Mathf.Lerp(-14f, -2f, rint);   // louder in heavier rain
            }
            else Silence(pl);
        }

        static void Silence(AudioStreamPlayer3D pl) { if (pl != null && pl.Playing) pl.Stop(); }
    }

    /// <summary>Where the MAP's rain-audible surfaces are, recorded by the world build as it places them.
    ///
    /// A prop's body only lands on collision bit0 when it blocks line of sight; everything else is on 6|8
    /// (WorldBuilder.PlaceObject), so RainMaterialAudio's sphere query cannot see a shipping container or a
    /// tent at all -- and widening its mask would flood a 48-hit cap with fence posts and bollards to find
    /// the one thing that matters. Props do not move, so their positions are simply recorded once and the
    /// nearest is a scan of about a hundred vectors, which is cheaper than the query it sidesteps.
    ///
    /// The tables are NAMED PROPS on purpose. The asset name is the only thing that says what a prop is made
    /// of -- it never reaches the node, the meshes carry no material class, and the palette is colour, not
    /// substance -- so matching it here at build time, where the name IS in hand, is the one place the answer
    /// exists. Metal: shipping containers, sheds, hangars, grain silos, the metal garage (98 placed on PEI).
    /// Tarp: tents (8). Both counted against placements.txt, not guessed at.</summary>
    public static class RainSurfaces
    {
        static readonly System.Collections.Generic.List<(Vector3 Pos, bool Tarp)> _all = new();

        public static System.Collections.Generic.IReadOnlyList<(Vector3 Pos, bool Tarp)> All => _all;
        public static int Count => _all.Count;

        /// <summary>Cleared per world build, or a second map inherits the first one's roofs and it rains on
        /// metal in the middle of a field.</summary>
        public static void Clear() => _all.Clear();

        public static void Add(Vector3 pos, bool tarp) => _all.Add((pos, tarp));

        /// <summary>Metal-roofed map props. StartsWith rather than exact names so a re-rip that adds
        /// Container_7 is covered without anyone remembering to come back here.</summary>
        public static bool IsMetalRoof(string name) =>
            name != null && (name.StartsWith("Container_") || name.StartsWith("Shed_")
                          || name.StartsWith("Hangar_") || name.StartsWith("Silo_Grain_")
                          || name == "Garage_Metal");

        /// <summary>Canvas. A tent is the only tarp in the world -- there is no tarp construction tier and no
        /// tarp deployable, so this is the whole of it rather than a first entry.</summary>
        public static bool IsTarp(string name) => name != null && name.StartsWith("Tent_");
    }
}
