using Godot;
using System.Collections.Generic;
using UnturnedSim;

namespace UnturnedGodot.Testing
{
    // Does the core shelter query agree with the ENGINE about where a surface is?
    //
    // RainShelter.Frame is a hand-derived copy of the transform Godot applies when a surface is spawned with
    // RotationDegrees = (pitch, yaw, 0). The L0 tests around it are all self-consistent: they use that same
    // derivation to work out where the cover should be, so if the derivation is wrong they are wrong
    // together and green. This is the only test that can tell -- it asks Godot.
    //
    // That is the same failure that has already bitten twice in this session (a signature compared against
    // itself, a snapshot compared against another snapshot from the same function). A derivation checked
    // only against itself is the same shape.
    public class RainShelterMatchesTheEngineTransform : GameTest
    {
        public override string Name => "buildtool.rain_shelter_matches_engine";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            eb.RestoreAll(new List<WallPlan>());
            yield return Step.Ticks(1);

            // Awkward angles on purpose. Axis-aligned cases agree under almost any sign error -- a mirrored
            // or transposed basis reproduces yaw 0 and pitch -90 exactly, which is every case the L0 tests
            // use and every case a floor tool produces.
            float worst = 0f;
            string worstAt = "";
            foreach (float yaw in new[] { 0f, 37f, 90f, 143f, -66f })
            foreach (float pitch in new[] { 0f, -25f, -45f, -90f, -132f })
            {
                var w = eb.AddWall(new Vector3(3f, eb.ActiveFloorY, -7f), yaw, 9f);
                w.Height = 6f;
                w.RotationDegrees = new Vector3(pitch, yaw, 0f);
                w.Rebuild();
                yield return Step.Ticks(1);

                RainShelter.Frame(yaw, pitch, out var ax, out var ay, out var n);

                // The engine's own answer for the same (u,v) -> world map.
                var origin = w.UVToWorld(0f, 0f);
                var eX = (w.UVToWorld(1f, 0f) - origin);
                var eY = (w.UVToWorld(0f, 1f) - origin);

                float d = Mathf.Max(
                    (eX - new Vector3(ax.X, ax.Y, ax.Z)).Length(),
                    (eY - new Vector3(ay.X, ay.Y, ay.Z)).Length());
                var eN = eX.Cross(eY).Normalized();
                d = Mathf.Max(d, Mathf.Min((eN - new Vector3(n.X, n.Y, n.Z)).Length(),
                                           (eN + new Vector3(n.X, n.Y, n.Z)).Length()));
                if (d > worst) { worst = d; worstAt = $"yaw {yaw} pitch {pitch}"; }

                eb.RemoveWall(w);
                yield return Step.Ticks(1);
            }

            // BREAK IT: transpose the basis in RainShelter.Frame, or flip a sign in ay -> every L0 test
            // still passes and this reports a metre of error.
            T.Check($"core Frame matches the engine basis at every angle (worst {worst:0.0000} at {worstAt})",
                    worst < 1e-3f);

            eb.QueueFree(); ed.QueueFree();
        }
    }

    public class RainShelterMatchesARealRaycast : GameTest
    {
        public override string Name => "buildtool.rain_shelter_matches_raycast";

        public override IEnumerable<Step> Run()
        {
            // The other half: the core says "covered at height Y", the physics says a ray going up hits
            // something at height Y. Agreement here is what lets the framework be trusted without a
            // raycast per drop later.
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            eb.RestoreAll(new List<WallPlan>());
            yield return Step.Ticks(1);

            float y = eb.ActiveFloorY;
            RoomOf(eb, 0f, 0f, 12f, y);
            eb.SolveCorners();
            eb.AutoFitRooms(withFoundations: false);

            // A CEILING, not just a floor. Auto-fit lays the slab at the storey you are ON, which is under
            // the sample points, so the first version of this had a building with no roof and sampled zero
            // sheltered points -- and then reported perfect agreement between "never sheltered" and "never
            // hit anything". Duplicating the storey puts that slab overhead, which is what shelter means.
            eb.DuplicateFloor();
            eb.ChangeFloor(-1);
            yield return Step.Ticks(2);

            var plans = eb.Snapshot();
            int agree = 0, disagree = 0, covered = 0, open = 0;
            string first = null;

            // OFF THE WALL LINES. The walls stand at x = 0 and 12 and z = 0 and -12, and a ray fired from a
            // corner hits the wall itself -- which the core correctly does not call shelter, so the two
            // disagree for a reason that has nothing to do with the geometry under test. Offsetting the grid
            // by half a step keeps every sample either properly inside or properly outside.
            //
            // The alternative -- filtering the physics hit by its normal -- would encode the core's own
            // occlusion rule on both sides of the comparison, which is how a test comes to agree with itself.
            for (float x = -4.5f; x <= 16.5f; x += 3f)
            for (float z = 4.5f; z >= -16.5f; z -= 3f)
            {
                var from = new Vector3(x, y + 0.5f, z);
                bool core = RainShelter.CoverAbove(plans, x, y + 0.5f, z, out float coverY);

                var space = World.GetWorld3D().DirectSpaceState;
                var q = new PhysicsRayQueryParameters3D
                {
                    From = from, To = from + new Vector3(0f, 40f, 0f), CollisionMask = 1u << 0,
                };
                var hit = space.IntersectRay(q);
                bool phys = hit.Count > 0;

                if (core) covered++; else open++;
                if (core == phys)
                {
                    agree++;
                    if (core && hit.Count > 0)
                    {
                        float py = ((Vector3)hit["position"]).Y;
                        // The slab has thickness, so the ray meets its underside slightly below the plane
                        // the core reports. Half a slab is the honest tolerance, not a fudge.
                        if (Mathf.Abs(py - coverY) > EditorBuildings.SlabThickness + 0.2f)
                        { disagree++; first ??= $"({x},{z}) core {coverY:0.00} vs ray {py:0.00}"; }
                    }
                }
                else { disagree++; first ??= $"({x},{z}) core {core} vs physics {phys}"; }
            }

            // BOTH STATES, or this proves nothing. A grid that happened to miss the building entirely
            // would report perfect agreement between "never sheltered" and "never hit anything" -- a pass
            // that looks exactly like the pass a correct implementation gives.
            T.Check($"the grid sampled sheltered points ({covered})", covered >= 4);
            T.Check($"...and open ones ({open})", open >= 4);
            T.Check($"core and physics agree everywhere ({disagree} disagreements"
                    + (first == null ? ")" : $", first: {first})"), disagree == 0);

            eb.QueueFree(); ed.QueueFree();
        }

        static void RoomOf(EditorBuildings eb, float x, float z, float w, float y)
        {
            eb.AddWall(new Vector3(x, y, z), 0f, w);
            eb.AddWall(new Vector3(x + w, y, z), 90f, w);
            eb.AddWall(new Vector3(x + w, y, z - w), 180f, w);
            eb.AddWall(new Vector3(x, y, z - w), 270f, w);
        }
    }
}

namespace UnturnedGodot.Testing
{
    // The engine-side probe: the half that gameplay uses, where there are no wall plans to consult.
    public class ShelterProbeSeesPastWalls : GameTest
    {
        public override string Name => "buildtool.shelter_probe_sees_past_walls";

        public override IEnumerable<Step> Run()
        {
            var ed = new Editor(); World.AddChild(ed);
            var eb = new EditorBuildings(); World.AddChild(eb);
            eb.Setup(ed, null, null);
            eb.RestoreAll(new List<WallPlan>());
            yield return Step.Ticks(1);

            float y = eb.ActiveFloorY;
            eb.AddWall(new Vector3(0f, y, 0f), 0f, 12f);
            eb.AddWall(new Vector3(12f, y, 0f), 90f, 12f);
            eb.AddWall(new Vector3(12f, y, -12f), 180f, 12f);
            eb.AddWall(new Vector3(0f, y, -12f), 270f, 12f);
            eb.SolveCorners();
            eb.AutoFitRooms(withFoundations: false);
            eb.DuplicateFloor();
            eb.ChangeFloor(-1);
            yield return Step.Ticks(3);

            var w3 = World.GetWorld3D();

            // Out in the open: no cover. Assert this FIRST -- a probe that always returned null would pass
            // every sheltered-is-false check and fail nothing.
            T.Check("open ground is not sheltered",
                    !ShelterProbe.IsSheltered(w3, new Vector3(60f, y + 0.5f, -60f)));

            T.Check("the middle of the room is sheltered",
                    ShelterProbe.IsSheltered(w3, new Vector3(6f, y + 0.5f, -6f)));
            // Right up against a wall, which is where a naive probe would report the wall as the roof --
            // it does not, because a vertical ray cannot hit a vertical face at all.
            T.Check("and so is a spot tucked against a wall",
                    ShelterProbe.IsSheltered(w3, new Vector3(0.6f, y + 0.5f, -6f)));

            // The height it reports should be the ceiling, not the wall top.
            var at = ShelterProbe.CoverAbove(w3, new Vector3(6f, y + 0.5f, -6f));
            T.Check("cover height came back", at.HasValue);
            if (at.HasValue)
                T.Check($"and it is the ceiling a storey up ({at.Value - y:0.00} above the floor)",
                        at.Value - y > EditorBuildings.StoreyHeight * 0.5f);

            // A null world must not throw -- weather ticks before a world exists during boot.
            T.Check("a null world is simply not sheltered", !ShelterProbe.IsSheltered(null, Vector3.Zero));

            eb.QueueFree(); ed.QueueFree();
        }
    }
}
