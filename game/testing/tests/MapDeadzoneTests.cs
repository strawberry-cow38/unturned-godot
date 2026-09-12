using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    /// <summary>PEI's authored deadzone actually reaches a DeadzoneField.
    ///
    /// The whole hazard existed and was unreachable: DeadzoneField, DeadzoneSim, the server seed, the HUD
    /// icon and the overlay were all finished, the demo volume was deleted on 2026-08-19 as "real deadzones
    /// belong in map data", and nothing ever read map data. Every deadzone test passed throughout, because
    /// each one adds its own volume — the same blind spot the foliage panel had.
    ///
    /// So this asserts the SHIPPED content file, parsed by the real loader, lands a usable volume in a real
    /// field. It does not (and cannot) prove WorldBuilder calls it — that one line is verified by booting
    /// PEI and reading the [deadzone] log line, because a test that builds its own field could never see it.</summary>
    public class MapDeadzoneLoads : GameTest
    {
        public override string Name => "deadzone.map_volumes_load";
        public override double TimeoutSimSeconds => 20;

        // PEI's zone, as authored: Level.hierarchy gives Unity (506.368, 32.28622, -720.7485) scale 32.
        static readonly Vector3 Centre = new Vector3(506.368f, 32.28622f, 720.7485f);
        const float R = 16f;

        public override IEnumerable<Step> Run()
        {
            var field = new DeadzoneField();
            World.AddChild(field);
            yield return Ticks(1);

            T.Check("a fresh field is empty", field.VolumeCount == 0);

            int n = DeadzoneMap.Populate(field, "deadzones.tsv");
            T.Check($"PEI's deadzones.tsv loaded exactly one volume (got {n})", n == 1);
            T.Check($"the field holds it (VolumeCount={field.VolumeCount})", field.VolumeCount == 1);

            var v = field.Volumes[0];
            T.Check($"it is a SPHERE (got {v.Shape})", v.Shape == DeadzoneShape.Sphere);
            T.Check($"radius is 16 — half of the hierarchy's scale 32 (got {v.HalfExtent.x:0.##})",
                    Mathf.Abs(v.HalfExtent.x - R) < 0.01f);

            // Z NEGATED into Godot space, like every other hierarchy parser. Getting this wrong puts the
            // zone 1441 m away on the other side of the island, which no rate assertion would ever notice.
            T.Check($"centre X ({v.Center.x:0.##})", Mathf.Abs(v.Center.x - Centre.X) < 0.01f);
            T.Check($"centre Y ({v.Center.y:0.##})", Mathf.Abs(v.Center.y - Centre.Y) < 0.01f);
            T.Check($"centre Z is +720.75, not -720.75 (got {v.Center.z:0.##})",
                    Mathf.Abs(v.Center.z - Centre.Z) < 0.01f);

            // Behaviour, not just numbers: standing at the centre is inside, and the bounding cube's corner
            // — which a box-shaped import would have made hot — is not.
            T.Check("the centre is inside", field.IsInside(Centre));
            var corner = new Vector3(Centre.X + R * 0.99f, Centre.Y + R * 0.99f, Centre.Z + R * 0.99f);
            T.Check("the bounding cube's corner is NOT inside", !field.IsInside(corner));
            T.Check("a point 100 m away is not inside", !field.IsInside(Centre + new Vector3(100f, 0f, 0f)));

            // Rates stay OURS. The hierarchy says 6.25/s; DeadzoneDef's tuned unprotected rate is 0.020
            // because ours are dose rates against a different infection model. Importing retail's number
            // verbatim would be 312x, so assert we did not.
            T.Check($"the zone uses our tuned rate, not retail's 6.25 (got {v.Zone.UnprotectedRadiationPerSecond:0.###})",
                    Mathf.Abs(v.Zone.UnprotectedRadiationPerSecond - DeadzoneDef.Default().UnprotectedRadiationPerSecond) < 1e-6f);

            // A map with no deadzone file gets none — never another map's.
            var empty = new DeadzoneField();
            World.AddChild(empty);
            yield return Ticks(1);
            T.Check("a map with no deadzone file gets zero, not PEI's",
                    DeadzoneMap.Populate(empty, "deadzones_no_such_map.tsv") == 0);

            // AND IT ACTUALLY DOSES SOMEBODY. Everything above is geometry; this is the gameplay claim the
            // whole change exists to make — a player standing where PEI authored a deadzone takes radiation.
            // Placed at the zone's REAL centre, not a convenient origin, so the coordinate transform is
            // load-bearing here: get the Z sign wrong and this player is 1441 m away and takes nothing.
            Rigs.Ground(World);
            var pl = Rigs.Player(World, Centre);
            yield return Ticks(4);
            T.Check($"the player is inside PEI's zone at {Centre}", field.IsInside(pl.GlobalPosition));

            float d0 = pl.Radiation;
            field.Apply(pl, 0.2f);                     // inside the entry grace
            T.Check($"the grace window costs nothing (dose {pl.Radiation:0.####})", Mathf.IsEqualApprox(pl.Radiation, d0));
            field.Apply(pl, 1.0f);                     // past it
            T.Check($"standing in the MAP's deadzone doses you ({d0:0.####} -> {pl.Radiation:0.####})", pl.Radiation > d0);

            // The corner of the bounding cube must be safe ground. This is the sphere import earning its
            // keep in gameplay terms rather than in geometry terms: as a box this player would be dosed
            // while standing 27 m from a 16 m zone.
            pl.TeleportTo(corner);
            yield return Ticks(2);
            T.Check($"the bounding cube's corner is outside the zone (at {pl.GlobalPosition})", !field.IsInside(pl.GlobalPosition));
            float dc = pl.Radiation;
            field.Apply(pl, 1.0f);
            T.Check($"...and takes no further dose there ({dc:0.####} -> {pl.Radiation:0.####})", pl.Radiation <= dc);
        }
    }
}
