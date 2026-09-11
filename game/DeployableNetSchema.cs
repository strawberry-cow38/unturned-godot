using SDG.Unturned;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // Bridges the game's DeployableDef table into a core DeployableSchema (MP_PLAN §3.1: only the defId
    // crosses the wire -- both sides rebuild ports/watts/health/fuel from the SAME def table, which the
    // content-hash handshake guarantees matches). Every net host (server or client) that replicates
    // deployables registers through here; L0 core tests register their own fixtures instead.
    public static class DeployableNetSchema
    {
        public static void RegisterAll(DeployableSchema schema)
        {
            foreach (var def in DeployableDef.All)
            {
                // STORAGE is no longer excluded (strawberry 2026-09-03: "not networking the smart containers
                // (fridges etc)"). It was in this list because device replication was a fast-follow, which
                // meant a placed fridge existed only on the machine that placed it -- nobody else could see
                // it, never mind open it. It replicates like any other deployable now, and carries its grid
                // dimensions so the server can register a crate under its NetId at placement.
                //
                // Every deployable is REGISTERED, never skipped. Leaving a class out of the schema was once
                // used to stop ServerPlace spawning a phantom replica, and it did -- but the schema is the
                // whole server def table, so an absent id ALSO fails CanPlace, which is the place command's
                // validator. The place was then rejected before OnPlaceDeployable could spend the item,
                // while the client had already skipped its own local spend expecting the server to do it.
                // Free deployables. LocalOnly is the honest way to say "no server body": it keeps the def
                // in the table (so CanPlace passes and the item is spent) and only suppresses the spawn.
                //
                // DOORS are still LocalOnly -- one class at a time, and a door needs its own client
                // materializer in DeployableReplicaView the way a fluid device now has one.
                //
                // FLUID devices are NOT any more. LocalOnly meant no server entity, so a placed pump had no
                // NetId, so there was nothing for a pickup to be validated against -- PickupFluid handed out
                // an item on the client's say-so and the next owner echo deleted it (strawberry 2026-09-10:
                // "the items i get from picking up deployables are phantom"). They are server-tracked now and
                // materialize through FluidDeploy in the replica view, the same migration the fridge made.
                bool localOnly = def.DoorProp != null;
                var ports = new DeployablePortSpec[def.Ports.Length];
                for (int i = 0; i < def.Ports.Length; i++)
                    ports[i] = new DeployablePortSpec { Kind = (byte)Kind(def.Ports[i].Kind), Watts = def.Ports[i].Watts };
                schema.Register(new DeployableNetDef
                {
                    LocalOnly = localOnly,
                    DefId = def.Id,
                    Health = def.Health,
                    FuelCapacity = def.Fuel,
                    Range = def.Range,
                    FixtureKind = def.Fixture,   // A3/A2: carry the server-placed world-fixture kind onto the net def table
                    Ports = ports,
                    // Deployable.Salvage yields 2x Metal Scrap (67); a ShatterOnDeath def leaves no wreck to salvage
                    SalvageItemId = def.ShatterOnDeath ? (ushort)0 : (ushort)67,
                    SalvageCount = def.ShatterOnDeath ? (byte)0 : (byte)2,
                    // The fridge's own grid, from Refrigerator's constants rather than repeated here: the
                    // server registers the authoritative crate at these dimensions and the client
                    // materializes the visible one at the same, so a literal in two places is a silent
                    // desync waiting for someone to change one of them.
                    // The fridge keeps taking its dimensions from Refrigerator's own constants (a literal in two
                    // places is a silent desync waiting for someone to change one). Anything else declares its
                    // own grid on the def -- the campfire's 3x2.
                    StorageWidth = def.IsStorage ? Refrigerator.GridW : def.StorageW,
                    StorageHeight = def.IsStorage ? Refrigerator.GridH : def.StorageH,
                    CookerKind = def.Cooker.HasValue ? (byte)def.Cooker.Value : (byte)255,
                });
            }
        }

        static PowerPortKind Kind(DeployableDef.PortKind k) => k switch
        {
            DeployableDef.PortKind.Output => PowerPortKind.Output,
            DeployableDef.PortKind.Consumer => PowerPortKind.Consumer,
            _ => PowerPortKind.Passthrough,
        };
    }
}
