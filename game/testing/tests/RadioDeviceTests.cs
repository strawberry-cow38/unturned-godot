using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // THE RADIO SET (strawberry 2026-09-04: "make the stereo/radio prop interactable, turn it on/off. plays static
    // noise. has power io input. works off global power like a tv").
    //
    // What is under test is the SWITCH-vs-FEED state machine, which is the whole of the feature: a radio is playing
    // only when the player has switched it on AND something is feeding it. Those are two independent inputs and the
    // interesting cases are the ones where they disagree.
    //
    // Radio_0/1 ship with Unturned and this box has no install, so Make() cannot load the real prop mesh here. It is
    // driven with a plain BoxMesh instead, which is enough to exercise the real construction path (bounds -> plug
    // position, audio build, first Refresh) rather than a bare `new RadioDevice` that skips all of it.
    public sealed class RadioDeviceTests : GameTest
    {
        public override string Name => "radio.switch_and_feed";

        static RadioDevice Build()
        {
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.3f, 0.25f) } };
            return RadioDevice.Make(mi, "Radio_0");
        }

        public override IEnumerable<Step> Run()
        {
            bool gridWas = PowerNet.GlobalPower;
            PowerNet.SetGlobalPower(true);

            T.Check("Radio_0 and Radio_1 are radio props", RadioDevice.IsRadioProp("Radio_0") && RadioDevice.IsRadioProp("Radio_1"));
            T.Check("a television is NOT", !RadioDevice.IsRadioProp("Television_1"));
            T.Check("SmartProps routes the prop to the Radio behaviour",
                    (SmartProps.KindsFor("Radio_0") & SmartKind.Radio) != 0);
            T.Check("...and a radio therefore needs its own mesh node", SmartProps.NeedsOwnNode("Radio_0"));

            var r = Build();
            World.AddChild(r);
            yield return Ticks(2);

            // DEFAULTS OFF, unlike a television. A deliberate choice, so it is pinned: a map that comes up with every
            // radio hissing is the thing this is avoiding, and "it defaults off" is exactly the sort of decision that
            // gets silently flipped by someone copying TVDevice.Make's `_on = true`.
            T.Check($"a radio starts switched OFF and silent (on={r.SwitchedOn} playing={r.Playing})", !r.SwitchedOn && !r.Playing);

            // ---- the two inputs, agreeing ----
            r.Toggle();
            yield return Ticks(2);
            T.Check($"switched on with the mains live, it plays (on={r.SwitchedOn} playing={r.Playing})", r.SwitchedOn && r.Playing);

            // ---- the two inputs, disagreeing: feed drops with the switch still on ----
            // Refresh is driven explicitly, exactly as TVBrokenTests does: SetGlobalPower only marks the net dirty,
            // and the group sweep that pushes it down lives in DayNightCycle.DriveStreetlights, which needs a
            // ticking cycle node this suite does not build. The device's own hub poll is no help either -- TickHub
            // runs it on _Process and the harness steps _PhysicsProcess. What is under test here is the state
            // machine; that it is REACHED in the real game is the group assertion at the end.
            PowerNet.SetGlobalPower(false);
            r.Refresh();
            yield return Ticks(2);
            T.Check($"the mains dying stops it WITHOUT a toggle (on={r.SwitchedOn} playing={r.Playing})", r.SwitchedOn && !r.Playing);

            PowerNet.SetGlobalPower(true);
            r.Refresh();
            yield return Ticks(2);
            T.Check($"the mains returning resumes it, switch state remembered (on={r.SwitchedOn} playing={r.Playing})", r.SwitchedOn && r.Playing);

            // ---- switched off while fed ----
            r.Toggle();
            yield return Ticks(2);
            T.Check($"switching it off silences a fed set (on={r.SwitchedOn} playing={r.Playing})", !r.SwitchedOn && !r.Playing);

            // ---- broken ----
            r.Toggle();               // back on
            yield return Ticks(2);
            T.Check("fed + on again before the break", r.Playing);
            r.SetBroken(true);
            yield return Ticks(2);
            T.Check($"smashing it silences it (playing={r.Playing})", !r.Playing);

            // BROKEN IS STATE, NOT A ONE-SHOT. Refresh runs on every grid sweep; a set that merely stopped would
            // start hissing again at the next one. Drive a real sweep rather than trusting a single Stop().
            PowerNet.SetGlobalPower(false);
            r.Refresh();
            PowerNet.SetGlobalPower(true);
            r.Refresh();
            yield return Ticks(2);
            T.Check($"a smashed radio stays silent through a grid sweep (playing={r.Playing})", !r.Playing);

            // THE SILENT-ARMING TRAP, copied from TVDevice.Toggle's comment because it is invisible while the prop is
            // rubble: pressing F on a broken set must not flip the switch underneath. If it does, the radio turns
            // itself on the moment the prop resets -- and nothing on screen tells you it happened.
            bool onBeforePress = r.SwitchedOn;
            r.Toggle();
            yield return Ticks(2);
            T.Check($"F on a smashed set does not flip the switch underneath (was {onBeforePress}, now {r.SwitchedOn})",
                    r.SwitchedOn == onBeforePress);

            r.SetBroken(false);
            yield return Ticks(2);
            T.Check($"...so it comes back in the state it was smashed in, not armed by the dead press (on={r.SwitchedOn} playing={r.Playing})",
                    r.SwitchedOn == onBeforePress && r.Playing == onBeforePress);

            // ---- the power port ----
            T.Check($"it exposes exactly one power port ({r.PowerPorts.Count})", r.PowerPorts.Count == 1);
            T.Check("it is a CONSUMER, not a source", r.PowerPorts.Count == 1 && r.PowerPorts[0].Kind == DeployableDef.PortKind.Consumer);
            T.Check("a radio produces nothing and is not on fire", !r.PowerProducing && !r.PowerOnFire);

            // THE WIRING, not the state machine. Everything above drives Refresh() by hand, which proves the machine
            // is right and says nothing about whether the game ever CALLS it. It did not: RadioDevice joined
            // "radiodevices" with a comment claiming the group was swept on a grid change, and no sweeper existed --
            // the radio was relying entirely on its _Process hub poll, silently out of step with every other mains
            // fixture. DayNightCycle.DriveStreetlights sweeps this group now. This pins the name both sides share.
            T.Check("it registers in the group DayNightCycle sweeps on a grid change", r.IsInGroup("radiodevices"));
            T.Check("...and in the group PowerNet gathers devices from", r.IsInGroup("deployables"));

            PowerNet.SetGlobalPower(gridWas);
            yield return Ticks(2);
        }
    }
}
