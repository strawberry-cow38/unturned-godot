using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // OTHER PLAYERS' LAMPS LIGHT UP (strawberry 2026-09-10: "fix puppets in mp", after "should only glow when they
    // are on, in 3p too").
    //
    // The emitters were already on the puppet -- its glasses and its melee mesh get the same derived lens mask the
    // local body does. What never crossed the wire was the SWITCH, so every other player's nightvision, headlamp
    // and torch stayed dark no matter what they were doing.
    //
    // Drives the real loopback rather than the serialiser, for the reason mp.attachments_replicate does: the risk
    // is a missing link in publish -> dirty -> wire -> apply, and a Write/Read test passes with the publisher
    // never running.
    public sealed class PuppetLightTests : GameTest
    {
        public override string Name => "mp.puppet_lights";

        public override IEnumerable<Step> Run()
        {
            bool nvWas = NightVision.Active;
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);

            var loop = new MpLoopback { Player = player, Driver = driver };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Client.CombatState.TryGet(loop.Client.PlayerId, out _), 15);
            ushort pid = loop.Client.PlayerId;

            // ---- CONTROL FIRST: dark is the default, so "it went true" cannot pass on a field stuck at true.
            NightVision.Active = false;
            yield return Ticks(12);
            loop.Client.CombatState.TryGet(pid, out var off);
            T.Check($"a player with no light on replicates dark (worn {off.WornLightOn}, held {off.HeldLightOn})",
                !off.WornLightOn && !off.HeldLightOn);

            // ---- switch the goggles on: the bit has to reach the other end.
            NightVision.Active = true;
            yield return Until(() => loop.Client.CombatState.TryGet(pid, out var c) && c.WornLightOn, 15);
            loop.Client.CombatState.TryGet(pid, out var on);
            T.Check($"switching a worn light on replicates ({on.WornLightOn})", on.WornLightOn);

            // ⭐ AND THE TWO BITS ARE INDEPENDENT. This is the whole reason there are two: one "a light is on" flag
            // would light a puppet's goggles AND its torch together, so a player wearing nightvision and holding an
            // unlit torch would glow at both ends. If this ever reads true, that has happened.
            T.Check($"...without lighting the torch they are not holding ({on.HeldLightOn})", !on.HeldLightOn);

            // ---- and it goes back off, so the puppet does not keep a lamp lit forever.
            NightVision.Active = false;
            yield return Until(() => loop.Client.CombatState.TryGet(pid, out var c) && !c.WornLightOn, 15);
            loop.Client.CombatState.TryGet(pid, out var back);
            T.Check($"switching it off replicates too ({back.WornLightOn})", !back.WornLightOn);

            NightVision.Active = nvWas;
            yield break;
        }
    }
}
