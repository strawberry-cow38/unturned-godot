namespace UnturnedGodot
{
    // Shared retail spawn-point parses (PEI_CLIENT_PLAN §3 C2: "promote C1's LoadRegularSpawns to a shared
    // static"). One parse, three callers: WorldBuilder Playable (local player spawn), WorldBuilder Client
    // (the C1 overhead-cam anchor / C3 shell spawn), and DedicatedServer (NetWorldServer.SpawnProvider for
    // remote peers). Behavior-identical to the C1 local function it replaces.
    public static class LevelSpawns
    {
        // PEI's REAL regular spawn points (Spawns/Players.dat = u8 ver, u8 count, per point Vector3 + u8 angle*2
        // + bool isAlt if v>3; source LevelPlayers.getSpawn picks a random NON-alt spawn). Coordinates are
        // returned in port space (negate-Z layout, negated yaw). Empty list when the file is missing (fallback
        // worlds / CI) -- callers keep their existing no-map behavior.
        public static System.Collections.Generic.List<(float x, float z, float yaw)> PlayerSpawns(string mapRoot)
        {
            var regs = new System.Collections.Generic.List<(float x, float z, float yaw)>();
            string ppath = mapRoot + "/Spawns/Players.dat";
            if (!System.IO.File.Exists(ppath)) return regs;
            var pd = System.IO.File.ReadAllBytes(ppath); int pp = 0;
            byte pver = pd[pp++]; byte pcount = pd[pp++];
            for (int i = 0; i < pcount; i++)
            {
                float px = System.BitConverter.ToSingle(pd, pp); pp += 8;   // point.x (skip point.y)
                float pz = System.BitConverter.ToSingle(pd, pp); pp += 4;   // point.z
                float pang = pd[pp++] * 2f;
                bool isAlt = pver > 3 && pd[pp++] != 0;
                if (!isAlt) regs.Add((px, -pz, -pang));   // regular spawn -> port negate-Z, negate yaw
            }
            return regs;
        }
    }
}
