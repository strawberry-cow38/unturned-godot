namespace UnturnedGodot
{
    // Shared physics collision layers. WorldLayer was ZombieNav.WorldLayer before the zombie system was
    // removed -- it is LOS-blocking world geometry (terrain + solid buildings) and is used by the player's
    // sight/raycast queries, so it outlived the navmesh it was named for.
    public static class WorldLayers
    {
        public const uint World = 1u << 0;   // terrain + solid buildings (LOS raycast target)
    }
}
