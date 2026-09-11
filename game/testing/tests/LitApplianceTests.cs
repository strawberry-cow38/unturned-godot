using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    /// <summary>A lit appliance looks lit, to everyone. strawberry 2026-09-07: "make the red bbq lid stay open
    /// when its on. both bbqs should emit smoke when on too", then "so make cooking state serverside".
    ///
    /// The point of the v38 bit is that this is visible to a player who has NEVER opened the barbecue, so the
    /// test drives the SERVER's switch and then reads the VISUAL on the replicated node -- never touching the
    /// shelf directly. Calling SetCookerOn on the node would assert that a setter sets, which was never in
    /// doubt; what was in doubt is whether the fact crosses the wire at all, since the state it comes from used
    /// to be unicast to the one player with the panel open.</summary>
    public class LitApplianceTests : GameTest
    {
        public override string Name => "cook.lit_appliance";
        public override double TimeoutSimSeconds => 90;

        public override IEnumerable<Step> Run()
        {
            var driver = new SimDriver();
            World.AddChild(driver);
            var dayNight = new DayNightCycle { VisualsEnabled = false };
            World.AddChild(dayNight);
            var resources = new ResourceField { VisualInstances = false };
            World.AddChild(resources);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);
            ItemCatalog.RegisterAll();

            // The RED barbecue (Barbecue_1) is the one with a lid -- it is the only bbq with a doors.txt row,
            // so "the red one" is a mesh fact rather than a colour I matched by eye. Barbecue_0 rides along to
            // prove the smoke is not lid-dependent: it has no leaf at all and must still plume.
            var manifest = new List<(string mesh, int table, bool display, string label, Vector3 pos, float yaw)>
            {
                ("Barbecue_1", 6, false, "BBQ", new Vector3(2f, 0f, 0f), 0f),
                ("Barbecue_0", 6, false, "BBQ", new Vector3(5f, 0f, 0f), 0f),
            };
            var loop = new MpLoopback { Player = player, Driver = driver, DayNight = dayNight, Resources = resources,
                                        Containers = manifest, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected, 20);
            yield return Until(() => loop.Server.Cooking.Count >= 2 && loop.Client.Containers.Count >= 2, 20);
            T.Check($"both barbecues registered and replicated ({loop.Server.Cooking.Count} cookers)", loop.Server.Cooking.Count >= 2);

            uint redId = 0, plainId = 0;
            foreach (var e in loop.Client.Containers.All)
            {
                var kind = ContainerSchema.Get(e.KindId);
                if (kind.Mesh == "Barbecue_1") redId = e.NetIdValue;
                else if (kind.Mesh == "Barbecue_0") plainId = e.NetIdValue;
            }
            T.Check($"found the red bbq ({redId}) and the plain one ({plainId})", redId != 0 && plainId != 0);
            if (redId == 0 || plainId == 0) yield break;

            yield return Until(() => loop.Storage != null && loop.Storage.TryGetNode(redId, out _) && loop.Storage.TryGetNode(plainId, out _), 20);
            loop.Storage.TryGetNode(redId, out var red);
            loop.Storage.TryGetNode(plainId, out var plain);
            T.Check("both barbecues materialized as nodes", red != null && plain != null);
            if (red == null || plain == null) yield break;

            T.Check($"the red bbq has a lid to raise ({red.HasDoors})", red.HasDoors);
            float shutSwing = red.DebugDoorSwing();
            GD.Print($"[lit] before: red lid swing {shutSwing:0.00}, red smoking {red.DebugSmoking}, plain smoking {plain.DebugSmoking}");
            T.Check($"...and it starts shut ({shutSwing:0.00})", shutSwing < 0.5f);
            T.Check("neither is smoking while cold", !red.DebugSmoking && !plain.DebugSmoking);

            // FUEL FIRST, and the first cut of this test did not -- it lit two empty barbecues, which correctly
            // switched themselves off on the very next server step ("out of fuel it is a cold grill, so it
            // switches ITSELF off"). The lid and the smoke then read as broken when what was actually broken was
            // the fixture: a bbq I never gave charcoal to is not evidence about anything.
            loop.Server.Inventories.TryGetCrate(redId, out var redCrate);
            loop.Server.Inventories.TryGetCrate(plainId, out var plainCrate);
            T.Check("charcoal into the red bbq", redCrate.Storage.tryAddItem(new Item(Cooking.CharcoalId)) != null);
            T.Check("charcoal into the plain bbq", plainCrate.Storage.tryAddItem(new Item(Cooking.CharcoalId)) != null);

            // LIGHT THEM ON THE SERVER, with nobody's panel open. This is the whole claim.
            T.Check("the server lit the red bbq", loop.Server.Cooking.SetOn(redId, true));
            T.Check("the server lit the plain bbq", loop.Server.Cooking.SetOn(plainId, true));
            yield return Until(() => red.DebugCookerOn && plain.DebugCookerOn, 10);
            T.Check($"the lit bit crossed the wire to a player who never opened them (red {red.DebugCookerOn}, plain {plain.DebugCookerOn})",
                    red.DebugCookerOn && plain.DebugCookerOn);

            for (int i = 0; i < 60; i++) { red.TickDoorsForTest(1.0 / 60.0); yield return Ticks(1); }
            float litSwing = red.DebugDoorSwing();
            GD.Print($"[lit] after:  red lid swing {litSwing:0.00}, red smoking {red.DebugSmoking}, plain smoking {plain.DebugSmoking}");
            T.Check($"the red bbq's lid is UP because it is lit, with nobody in it ({litSwing:0.00})", litSwing > 0.5f);
            T.Check("both barbecues are smoking", red.DebugSmoking && plain.DebugSmoking);

            // ...AND SHUTTING THE PANEL DOES NOT SHUT THE LID. The lid has two independent reasons to be up and
            // one hinge; a writer that pushes the leaf directly closes it on behalf of the other reason. This is
            // the check that fails if SetDoorsOpen ever stops going through the OR.
            red.SetDoorsOpen(true);
            for (int i = 0; i < 30; i++) { red.TickDoorsForTest(1.0 / 60.0); yield return Ticks(1); }
            red.SetDoorsOpen(false);
            for (int i = 0; i < 60; i++) { red.TickDoorsForTest(1.0 / 60.0); yield return Ticks(1); }
            float afterPanel = red.DebugDoorSwing();
            GD.Print($"[lit] after opening AND closing the panel while lit: swing {afterPanel:0.00}");
            T.Check($"closing the panel leaves a lit bbq's lid UP ({afterPanel:0.00})", afterPanel > 0.5f);

            // Out it goes, and the lid drops.
            T.Check("the server doused the red bbq", loop.Server.Cooking.SetOn(redId, false));
            yield return Until(() => !red.DebugCookerOn, 10);
            for (int i = 0; i < 60; i++) { red.TickDoorsForTest(1.0 / 60.0); yield return Ticks(1); }
            GD.Print($"[lit] doused: swing {red.DebugDoorSwing():0.00}, smoking {red.DebugSmoking}");
            T.Check($"...and the lid comes down when it goes out ({red.DebugDoorSwing():0.00})", red.DebugDoorSwing() < 0.5f);
            T.Check("...and the smoke stops", !red.DebugSmoking);

            // A LIT APPLIANCE IS ALSO A HEAT SOURCE. Asserted through ThermalField rather than on the
            // ThermalSource's own flag, because the flag being false is a setter working and the question is
            // whether a player STANDING NEXT TO IT is warmed -- which is range, line of sight and the falloff
            // curve as well. This rides the same replicated bit the lid and the smoke do, so it is visible to
            // a player who never opened the barbecue.
            T.Check("the doused bbq still owns its heat source", red.DebugHasHeatSource);
            T.Check("...but it is off", !red.DebugHeatActive);

            // DOUSE THE OTHER ONE TOO before asserting zero. The first version of this check did not, and it
            // read 19.6 C off a dead fire -- because the plain barbecue is still lit a few metres away and
            // ThermalField SUMS every source in range, which is correct and is the whole point of it. The
            // assertion was the thing that did not isolate its subject.
            T.Check("the server doused the plain bbq too", loop.Server.Cooking.SetOn(plainId, false));
            yield return Until(() => !plain.DebugCookerOn, 10);
            yield return Ticks(4);
            float gap = red.GlobalPosition.DistanceTo(plain.GlobalPosition);
            float cold = ThermalField.NetC(red, red.GlobalPosition + new Vector3(1.5f, 0f, 0f));
            GD.Print($"[lit] both out: {cold:0.000} C beside the red bbq (plain one is {gap:0.0} m away)");
            T.Check($"two dead fires warm nobody ({cold:0.000} C, other bbq {gap:0.0} m away)", Mathf.IsZeroApprox(cold));

            T.Check("the server re-lit it", loop.Server.Cooking.SetOn(redId, true));
            yield return Until(() => red.DebugCookerOn, 10);
            yield return Ticks(4);
            float warm = ThermalField.NetC(red, red.GlobalPosition + new Vector3(1.5f, 0f, 0f));
            GD.Print($"[lit] heat beside the relit bbq: {warm:0.0} C");
            T.Check($"standing beside a lit bbq warms you ({warm:0.0} C)", warm > 5f);

            // CONTROL: out of range is off, not merely faint -- so the check above cannot pass on a field
            // that returns something everywhere.
            float away = ThermalField.NetC(red, red.GlobalPosition + new Vector3(40f, 0f, 0f));
            T.Check($"...and across the map it is nothing ({away:0.000} C)", Mathf.IsZeroApprox(away));
        }
    }
}
