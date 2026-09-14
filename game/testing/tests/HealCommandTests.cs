using Godot;
using System.Collections.Generic;
using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot.Testing
{
    // `heal` -- full HP, full food/water/stamina/breath, no infection, no dose, every condition cleared
    // (strawberry 2026-09-13: "add a heal command ... as well as fixing any health conditions").
    //
    // ⭐ THE CLAIM UNDER TEST IS NOT "the fields are 1". It is that THE SERVER AGREES. Under the loopback --
    // which is how singleplayer runs -- HP and the fine vitals are server-owned and the owner echo re-pins them
    // every tick, so a heal that only wrote locally reads back perfect for exactly one tick and then silently
    // reverts. That failure has bitten this repo THREE times in one day (the throwable spend, the respawn vitals
    // reset, and the paperdoll's room light), and it is invisible to any check that looks at the shell alone.
    //
    // So these assert the SERVER's copy, and use the shell only to prove the echo carried it back.
    public sealed class HealCommandTests : GameTest
    {
        public override string Name => "console.heal";
        public override double TimeoutSimSeconds => 45;

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);

            bool prevDrain = PlayerController.SurvivalDrain;
            PlayerController.SurvivalDrain = true;
            // ⚠ ConsumeDeployables MATTERS HERE, it is not boilerplate. The server-owned seams -- NetHealSelf,
            // NetDamageSink, NetRequestRespawn -- are all wired inside `if (ConsumeDeployables)`, because that is
            // the mode where the SERVER owns the local player's vitals. Without it the player owns them directly
            // and a local heal is the correct behaviour, so there is no seam to find and this test was asserting
            // against a mode it had not asked for. Every other test that reaches for server vitals sets it too.
            var loop = new MpLoopback { Player = player, Driver = driver, ConsumeDeployables = true };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Vitals.TryGet(loop.Client.PlayerId, out _), 15);
            T.Check("loopback connected with a server vitals entry",
                    loop.Server.Vitals.TryGet(loop.Client.PlayerId, out _));
            T.Check("the heal seam is wired by the listen server", player.NetHealSelf != null);

            // Wreck the player ON THE SERVER -- the side that actually owns these.
            loop.Server.Vitals.TryGet(loop.Client.PlayerId, out var ve);
            loop.Server.CombatState.TryGet(loop.Client.PlayerId, out var ce);
            ve.Sim.Food = 0.05f; ve.Sim.Water = 0.04f; ve.Sim.Stamina = 0.10f;
            ve.Sim.Infection = 0.85f; ve.Sim.Radiation = 0.5f; ve.Sim.Oxygen = 0.07f;
            ve.Sim.NotifyDamaged();
            ve.Bleeding = true; ve.Broken = true;
            ce.HealthExact = 23f; ce.Health = 23;
            player.Bleeding = true; player.Broken = true;
            yield return Ticks(10);

            // THE CONTROL. Without it, "everything is 1 afterwards" also passes on a tree where the wreck never
            // landed -- and the wreck landing on the SERVER is the whole premise.
            T.Check($"(control) the server player is wrecked (food {ve.Sim.Food:0.00}, inf {ve.Sim.Infection:0.00}, hp {ce.HealthExact:0})",
                    ve.Sim.Food < 0.2f && ve.Sim.Infection > 0.5f && ce.HealthExact < 40f);

            player.DebugHealFully();
            yield return Ticks(12);   // let the command reach the authority and the echo come back

            loop.Server.Vitals.TryGet(loop.Client.PlayerId, out ve);
            loop.Server.CombatState.TryGet(loop.Client.PlayerId, out ce);
            T.Check($"(server) food restored ({ve.Sim.Food:0.00})", ve.Sim.Food > 0.99f);
            T.Check($"(server) water restored ({ve.Sim.Water:0.00})", ve.Sim.Water > 0.99f);
            T.Check($"(server) stamina restored ({ve.Sim.Stamina:0.00})", ve.Sim.Stamina > 0.99f);
            T.Check($"(server) infection cleared ({ve.Sim.Infection:0.00})", ve.Sim.Infection < 0.01f);
            T.Check($"(server) breath restored ({ve.Sim.Oxygen:0.00})", ve.Sim.Oxygen > 0.99f);
            T.Check($"(server) dose cleared ({ve.Sim.Radiation:0.00})", ve.Sim.Radiation < 0.01f);
            T.Check("(server) bleeding cleared", !ve.Bleeding);
            T.Check("(server) broken legs mended", !ve.Broken);
            T.Check($"(server) HP restored ({ce.HealthExact:0})", ce.HealthExact > 99f);
            // The post-damage regen lock is the one nobody lists and the one that is invisible when missed: heal
            // while it is armed and the new-found health simply refuses to regen for ten seconds, silently.
            T.Check($"(server) the post-damage regen lock is cleared ({ve.Sim.RegenLockDelay:0.0}s)",
                    ve.Sim.RegenLockDelay <= 0.001f);

            // ...AND IT SURVIVES. One tick of "healed" is what the local-only version already achieved; the point
            // is that it is still true after the echo has had many ticks to overwrite it.
            yield return Ticks(40);
            loop.Server.Vitals.TryGet(loop.Client.PlayerId, out ve);
            T.Check($"(server) still healed 40 ticks later, not reverted by the echo ({ve.Sim.Food:0.00})",
                    ve.Sim.Food > 0.9f && ve.Sim.Infection < 0.05f);
            T.Check($"(shell) the owner echo carried it back (hp {player.Health:0}, food {player.Food:0.00})",
                    player.Health > 99f && player.Food > 0.9f);
            T.Check("(shell) conditions cleared locally too", !player.Bleeding && !player.Broken);

            PlayerController.SurvivalDrain = prevDrain;
        }
    }
}
