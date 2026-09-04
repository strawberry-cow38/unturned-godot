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
                // FLUID and DOOR devices are still local, and still deliberately: this is one class at a
                // time, and each needs its own client materializer (a fluid tank and a door are not a
                // StorageCrate). Keeping them out makes ServerPlace no-op their ids (no phantom replica)
                // while OnPlaceDeployable still SPENDS the item -> their place routes the spend server-side
                // without a spawn.
                if (def.Fluid != null || def.DoorProp != null) continue;
                                                    // server-replicated deployables. Keeping them out of the schema makes the server's
                                                    // ServerPlace no-op a fluid id (no phantom replica) while OnPlaceDeployable still
                                                    // SPENDS the item -> the fluid place routes its spend server-side without a spawn.
                var ports = new DeployablePortSpec[def.Ports.Length];
                for (int i = 0; i < def.Ports.Length; i++)
                    ports[i] = new DeployablePortSpec { Kind = (byte)Kind(def.Ports[i].Kind), Watts = def.Ports[i].Watts };
                schema.Register(new DeployableNetDef
                {
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
                    StorageWidth = def.IsStorage ? Refrigerator.GridW : (byte)0,
                    StorageHeight = def.IsStorage ? Refrigerator.GridH : (byte)0,
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
