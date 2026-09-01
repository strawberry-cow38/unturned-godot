using Godot;
using System.Collections.Generic;
using System.Linq;

namespace UnturnedGodot.Testing
{
    // SHOOTABLE LAMPS (strawberry 2026-09-01: "add the ability to shoot out headlights and tail lights. they
    // simply stay off when broken, can be repaired from the mechanics ui like the windows can.")
    //
    // Written against the two failure modes the glass feature actually shipped with, because a lamp system can
    // reproduce both:
    //   * an INVISIBLE feature -- the spec named a mesh and nothing appeared, which reads identically to "the
    //     effect is subtle". So assert on nodes in the tree and on state that changed, never on spec strings.
    //   * CROSSED LABELS -- 'windshield' named the rear window for a week because every check only asked whether
    //     a label EXISTED, and it existed either way. So the positional checks below are the load-bearing ones:
    //     a left/right or head/tail mix-up passes every count and every exists-check.
    public sealed class VehicleLampTests : GameTest
    {
        public override string Name => "vehicle.lamps";

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            yield return Ticks(2);

            var car = Vehicle.BuildByName("sedan");
            World.AddChild(car);
            car.GlobalPosition = new Vector3(60f, 1.2f, 0f);
            yield return Ticks(20);

            // ---- 1. THE LAMPS EXIST AS NODES, split per side.
            T.Check($"the sedan builds 4 lamps ({car.LampCount})", car.LampCount == 4);
            var labels = new List<string>();
            for (int i = 0; i < car.LampCount; i++) labels.Add(car.LampLabel(i));
            T.Check($"...both headlights and both taillights ({string.Join(",", labels)})",
                    labels.Contains("headlight_l") && labels.Contains("headlight_r") &&
                    labels.Contains("taillight_l") && labels.Contains("taillight_r"));
            int nodes = 0;
            foreach (var ch in car.GetChildren())
                if (ch is MeshInstance3D mi && mi.Name.ToString().StartsWith("Lamp_")) nodes++;
            T.Check($"...each lamp is a real MeshInstance3D child ({nodes})", nodes == car.LampCount);

            // ---- 2. THE LABELS POINT THE RIGHT WAY. Front is -Z on every road car here (verified from the specs:
            // every SpotPos sits at negative z, every TailPos at positive). A head/tail swap would satisfy every
            // check above, and would mean shooting a headlight killed a taillight.
            var hl = car.GetNodeOrNull<MeshInstance3D>("Lamp_headlight_l");
            var hr = car.GetNodeOrNull<MeshInstance3D>("Lamp_headlight_r");
            var tl = car.GetNodeOrNull<MeshInstance3D>("Lamp_taillight_l");
            T.Check("the lamp nodes are findable by name", hl != null && hr != null && tl != null);
            if (hl == null || hr == null || tl == null) yield break;
            float hlZ = hl.Position.Z + hl.GetAabb().GetCenter().Z;
            float tlZ = tl.Position.Z + tl.GetAabb().GetCenter().Z;
            T.Check($"the headlight sits FORWARD of the taillight (head {hlZ:F2} < tail {tlZ:F2})", hlZ < tlZ);
            T.Check($"...and on the front half of the car (head z {hlZ:F2} < 0)", hlZ < 0f);
            float hlX = hl.Position.X + hl.GetAabb().GetCenter().X;
            float hrX = hr.Position.X + hr.GetAabb().GetCenter().X;
            T.Check($"the left headlight is to the LEFT of the right one ({hlX:F2} < {hrX:F2})", hlX < hrX);

            // ---- 3. ALL WORKING AT SPAWN.
            T.Check($"a fresh car has no shot-out lamps ({car.LampBrokenCount})", car.LampBrokenCount == 0);

            // ---- 4. HIT RESOLUTION, both halves. A resolver that returned an index for everything would "work"
            // in play -- shoot the car anywhere, a lamp dies -- and be completely wrong.
            int hlIdx = labels.IndexOf("headlight_l");
            var hlCentre = hl.GlobalTransform * hl.GetAabb().GetCenter();
            T.Check($"a hit on the left headlight resolves to it ({car.ResolveHitLamp(hlCentre)} vs {hlIdx})",
                    car.ResolveHitLamp(hlCentre) == hlIdx);
            T.Check("a hit 8 m away resolves to no lamp",
                    car.ResolveHitLamp(car.GlobalPosition + new Vector3(0f, 0f, 8f)) < 0);

            // ---- 5. SHOOTING ONE KILLS ONLY THAT ONE. The whole reason lamps are per side.
            bool broke = car.BreakLamp(hlIdx);
            yield return Ticks(2);
            T.Check("shooting the left headlight reports success", broke);
            T.Check("...it reads as shot out", car.IsLampBroken(hlIdx));
            T.Check($"...exactly one lamp is out ({car.LampBrokenCount})", car.LampBrokenCount == 1);
            int hrIdx = labels.IndexOf("headlight_r");
            T.Check("...the OTHER headlight still works", !car.IsLampBroken(hrIdx));
            T.Check("...shooting it again does nothing", !car.BreakLamp(hlIdx));
            T.Check($"...and still only one is out ({car.LampBrokenCount})", car.LampBrokenCount == 1);
            T.Check("...a broken lamp no longer resolves as a hit target", car.ResolveHitLamp(hlCentre) != hlIdx);

            // ---- 6. IT STAYS OUT. "they simply stay off when broken" -- no self-heal, and specifically no
            // relighting when the headlights are switched on again, which is the state a naive implementation
            // gets wrong: the toggle rewrites every lamp's emission from one shared flag.
            yield return Ticks(120);
            T.Check("...still out two seconds later", car.IsLampBroken(hlIdx));
            car.SetLightsForTest(true);
            yield return Ticks(4);
            T.Check("...still out after the lights are switched ON", car.IsLampBroken(hlIdx));
            var deadLight = car.LampLightForTest(hlIdx);
            T.Check("...and its emitter is dark while the working side is lit",
                    deadLight == null || !deadLight.Visible);
            var liveLight = car.LampLightForTest(hrIdx);
            T.Check("...the surviving headlight IS lit", liveLight != null && liveLight.Visible);
            car.SetLightsForTest(false);
            yield return Ticks(2);

            // ---- 7. REPAIR FROM THE MECHANICS UI.
            T.Check("repairing it reports success", car.RepairLamp(hlIdx));
            T.Check("...it reads as working", !car.IsLampBroken(hlIdx));
            T.Check($"...and nothing is out ({car.LampBrokenCount})", car.LampBrokenCount == 0);
            T.Check("...repairing an intact lamp does nothing", !car.RepairLamp(hlIdx));

            // ---- 8. OTHER VEHICLES GET THEM TOO, from their own meshes.
            var truck = Vehicle.BuildByName("truck");
            World.AddChild(truck); truck.GlobalPosition = new Vector3(100f, 1.2f, 0f);
            yield return Ticks(20);
            T.Check($"the truck has lamps ({truck.LampCount})", truck.LampCount > 0);
            var tLabels = new List<string>();
            for (int i = 0; i < truck.LampCount; i++) tLabels.Add(truck.LampLabel(i));
            T.Check($"...including a left and right side ({string.Join(",", tLabels)})",
                    tLabels.Any(l => l.EndsWith("_l")) && tLabels.Any(l => l.EndsWith("_r")));
        }
    }
}
