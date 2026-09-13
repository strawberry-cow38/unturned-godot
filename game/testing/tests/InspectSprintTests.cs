using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // INSPECTING AND SPRINTING ARE MUTUALLY EXCLUSIVE (strawberry 2026-09-13: "prevent inspecting things while
    // sprinting, when starting a sprint, cancel any inspect in flight").
    //
    // Two halves that fail in opposite directions, so both are pinned:
    //   - starting an inspect AT a run must be refused outright;
    //   - breaking into a run DURING an inspect must drop the inspect.
    //
    // ⚠ THE SECOND HALF LOOKED FINE BEFORE AND WAS NOT. _wantSprint already excluded _inspecting, so an inspect
    // in flight simply BLOCKED the sprint pose -- the player ran at full speed with the gun still held up being
    // examined, which is a legible animation rather than a visibly broken one. "Nothing looks obviously wrong" is
    // exactly the state a test is for.
    public sealed class InspectSprintTests : GameTest
    {
        public override string Name => "viewmodel.inspect_sprint";

        static Viewmodel Gun() => new Viewmodel { GunName = "eaglefire" };

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();

            // ---- the CONTROL first: standing still, an inspect starts. Without this every assertion below is
            // satisfied by an inspect that never works at all.
            var vm = Gun();
            World.AddChild(vm);
            yield return Ticks(4);
            vm.SetLocomotion(false, EPlayerStance.STAND);
            vm.PlayInspect();
            T.Check("standing still, an inspect STARTS (the control)", vm.IsInspecting);

            // ---- ...and breaking into a run cancels it.
            vm.SetLocomotion(true, EPlayerStance.SPRINT);
            vm.HubProcess(0.05);   // the REAL per-frame path: _Process is off (SetProcess(false)); TickHub drives HubProcess
            T.Check("breaking into a run cancels the inspect in flight", !vm.IsInspecting);
            vm.QueueFree();
            yield return Ticks(2);

            // ---- starting one AT a run is refused outright.
            var vm2 = Gun();
            World.AddChild(vm2);
            yield return Ticks(4);
            vm2.SetLocomotion(true, EPlayerStance.SPRINT);
            vm2.PlayInspect();
            T.Check("you cannot START an inspect at a run", !vm2.IsInspecting);

            // ⚠ SPRINT means RUNNING, not the lowered-weapon pose. _wantSprint also fires on `safe`, and lowering
            // your weapon is close to the opposite of a reason to refuse an inspect -- if the rule keyed off that
            // condition instead, walking with the gun down would silently stop working.
            vm2.SetLocomotion(false, EPlayerStance.STAND, safe: true);
            vm2.PlayInspect();
            T.Check("...but a LOWERED weapon still inspects (safe is not sprinting)", vm2.IsInspecting);

            // ---- stopping lets it work again: the rule is a gate, not a latch.
            vm2.SetLocomotion(true, EPlayerStance.SPRINT);
            vm2.HubProcess(0.05);
            T.Check("running cancels that one too", !vm2.IsInspecting);
            vm2.SetLocomotion(false, EPlayerStance.STAND);
            vm2.PlayInspect();
            T.Check("and once stopped, inspecting works again", vm2.IsInspecting);

            // ---- T OUTRANKS F (strawberry 2026-09-13: "have the T inspect state override the F inspect state").
            //
            // ⚠ BOTH DIRECTIONS, because "override" is not one rule. Opening the attach view must CANCEL an
            // inspect in flight, and an inspect must not be startable underneath an open attach view -- pin only
            // the first and F could still barge in a frame later and fight the attach pose.
            vm2.SetLocomotion(false, EPlayerStance.STAND);
            vm2.PlayInspect();
            T.Check("inspecting again (the control for the T case)", vm2.IsInspecting);
            vm2.EnterAttachView();
            T.Check("opening the T attach view CANCELS the inspect", !vm2.IsInspecting);
            T.Check("...and the attach view is actually up", vm2.InAttachView);

            // ⚠ The refusal this replaces was not a no-op: AttachmentMenu.Open sets Visible BEFORE calling in, so
            // the old guard opened the MENU while the gun stayed in the inspect pose -- slot icons projected onto
            // a weapon that is not where they think it is.
            vm2.PlayInspect();
            T.Check("...and F cannot start an inspect underneath it", !vm2.IsInspecting && vm2.InAttachView);

            vm2.ExitAttachView();
            vm2.PlayInspect();
            T.Check("closing the attach view hands F back", vm2.IsInspecting);

            vm2.QueueFree();
            yield break;
        }
    }
}
