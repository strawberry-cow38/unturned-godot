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
                if (def.Fluid != null) continue;   // FLUID devices spawn LOCALLY (device replication = fast-follow), so they're NOT
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
                    IsSwitch = def.IsSwitch,   // gates the Passthrough server-side, exactly as Deployable.PowerConducting does in SP
                    Range = def.Range,
                    FixtureKind = def.Fixture,   // A3/A2: carry the server-placed world-fixture kind onto the net def table
                    Ports = ports,
                    // straight off the def -- the same numbers Deployable.Salvage spawns. A ShatterOnDeath def
                    // leaves no wreck to salvage, so it yields nothing regardless of what the def carries.
                    SalvageItemId = def.ShatterOnDeath ? (ushort)0 : def.SalvageItemId,
                    SalvageCount = def.ShatterOnDeath ? (byte)0 : def.SalvageCount,
                });
            }
        }

        static PowerPortKind Kind(DeployableDef.PortKind k) => DeployableDef.ToSolverKind(k);
    }
}
