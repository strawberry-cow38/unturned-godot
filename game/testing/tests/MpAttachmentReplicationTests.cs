using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;
using SDG.Unturned;

namespace UnturnedGodot.Testing
{
    // OTHER PLAYERS' GUNS WEAR THEIR ATTACHMENTS (strawberry 2026-09-10: "fix for mp", on remote players showing a
    // bare factory weapon).
    //
    // The Item never crosses the wire -- only its id does -- so a puppet had no way to know the gun had a scope on
    // it. Three ids were added to the appearance block for exactly the three slots the mount renders.
    //
    // This drives the REAL loopback (the same one unify.appearance_replicates uses) rather than calling the
    // serialiser: the failure this guards against is not "the bytes do not round-trip", it is any of the four
    // links between the server's inventory and the puppet's gun going missing -- publish, dirty-detect, wire,
    // apply. A direct Write/Read test would have passed with the publisher never running.
    public sealed class MpAttachmentReplicationTests : GameTest
    {
        public override string Name => "mp.attachments_replicate";

        public override IEnumerable<Step> Run()
        {
            ItemCatalog.RegisterAll();
            Rigs.Ground(World);
            var driver = new SimDriver();
            World.AddChild(driver);
            var player = Rigs.Player(World, new Vector3(0f, 1f, 0f));
            yield return Ticks(2);

            var loop = new MpLoopback { Player = player, Driver = driver };
            World.AddChild(loop);
            yield return Until(() => loop.Client.State == NetSessionState.Connected
                                     && loop.Server.Inventories.TryGet(loop.Client.PlayerId, out _), 15);
            ushort pid = loop.Client.PlayerId;

            // An Eaglefire with a scope and a drum, in the SERVER's holster page -- where an equipped weapon lives.
            loop.Server.Inventories.TryGet(pid, out var sinv);
            var gun = new SDG.Unturned.Item(4);
            AttachmentFit.SetInstalledId(gun, "Sight", 146);       // a real optic id
            AttachmentFit.SetInstalledId(gun, "Magazine", 17);     // the drum
            T.Check("the gun goes into the server's holster page", sinv.Inventory.items[0].tryAddItem(gun));

            // ...and the client reports holding it, which is what the publisher keys off.
            player.DebugSetHeldItem(gun);
            yield return Ticks(2);

            yield return Until(() => loop.Client.CombatState.TryGet(pid, out var c) && c.HeldSight == 146, 15);
            loop.Client.CombatState.TryGet(pid, out var ce);
            T.Check($"the held gun's id replicated ({ce.HeldId})", ce.HeldId == 4);
            T.Check($"...its SIGHT replicated ({ce.HeldSight})", ce.HeldSight == 146);
            T.Check($"...its MAGAZINE replicated ({ce.HeldMagazine})", ce.HeldMagazine == 17);
            // CONTROL: an unfitted slot must come through as 0, not as whatever the last write left. Without this
            // a publisher that stamped every slot with the same id would pass the two checks above.
            T.Check($"...and the empty BARREL slot stays 0 ({ce.HeldBarrel})", ce.HeldBarrel == 0);

            // ⭐ A CHANGE MUST RE-PUBLISH. HeldId does not move when you fit a scope, and the appearance block is
            // dirty-only -- so if the dirty check does not look at these fields, the first set replicates and no
            // later one ever does. That failure looks exactly like success until someone swaps an optic.
            AttachmentFit.SetInstalledId(gun, "Sight", 8);
            yield return Until(() => loop.Client.CombatState.TryGet(pid, out var c) && c.HeldSight == 8, 15);
            loop.Client.CombatState.TryGet(pid, out var ce2);
            T.Check($"swapping the optic re-publishes with the gun id unchanged ({ce2.HeldSight}, held {ce2.HeldId})",
                ce2.HeldSight == 8 && ce2.HeldId == 4);
            yield break;
        }
    }
}
