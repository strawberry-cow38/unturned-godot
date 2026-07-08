// Faithful port of Unturned's physics layer table + ray bitmasks (U3-SDK ELayerMask/LayerMasks/RayMasks),
// grounded against the retail ProjectSettings TagManager m_Layers (all 32 slots verified). Combat raycasts,
// movement ground checks, and object placement all key off these -- so they must match the binary exactly.
namespace SDG.Unturned
{
    // Layer INDICES (0..31), exactly the retail TagManager order.
    public static class LayerMasks
    {
        public const int DEFAULT = 0;
        public const int TRANSPARENT_FX = 1;
        public const int IGNORE_RAYCAST = 2;
        public const int BUILTIN_3 = 3;
        public const int WATER = 4;
        public const int UI = 5;
        public const int BUILTIN_6 = 6;
        public const int BUILTIN_7 = 7;
        public const int LOGIC = 8;
        public const int PLAYER = 9;
        public const int ENEMY = 10;
        public const int VIEWMODEL = 11;
        public const int DEBRIS = 12;
        public const int ITEM = 13;
        public const int RESOURCE = 14;
        public const int LARGE = 15;
        public const int MEDIUM = 16;
        public const int SMALL = 17;
        public const int SKY = 18;
        public const int ENVIRONMENT = 19;
        public const int GROUND = 20;
        public const int CLIP = 21;
        public const int NAVMESH = 22;
        public const int ENTITY = 23;
        public const int AGENT = 24;
        public const int LADDER = 25;
        public const int VEHICLE = 26;
        public const int BARRICADE = 27;
        public const int STRUCTURE = 28;
        public const int TIRE = 29;
        public const int TRAP = 30;
        public const int GROUND2 = 31;
    }

    // Single-layer bitmasks (1 << index). Composite/combat masks (DAMAGE_*, BLOCK_*) get ported with the gun.
    public static class RayMasks
    {
        public const int DEFAULT = 1 << LayerMasks.DEFAULT;
        public const int TRANSPARENT_FX = 1 << LayerMasks.TRANSPARENT_FX;
        public const int IGNORE_RAYCAST = 1 << LayerMasks.IGNORE_RAYCAST;
        public const int WATER = 1 << LayerMasks.WATER;
        public const int UI = 1 << LayerMasks.UI;
        public const int LOGIC = 1 << LayerMasks.LOGIC;
        public const int PLAYER = 1 << LayerMasks.PLAYER;
        public const int ENEMY = 1 << LayerMasks.ENEMY;
        public const int VIEWMODEL = 1 << LayerMasks.VIEWMODEL;
        public const int DEBRIS = 1 << LayerMasks.DEBRIS;
        public const int ITEM = 1 << LayerMasks.ITEM;
        public const int RESOURCE = 1 << LayerMasks.RESOURCE;
        public const int LARGE = 1 << LayerMasks.LARGE;
        public const int MEDIUM = 1 << LayerMasks.MEDIUM;
        public const int SMALL = 1 << LayerMasks.SMALL;
        public const int SKY = 1 << LayerMasks.SKY;
        public const int ENVIRONMENT = 1 << LayerMasks.ENVIRONMENT;
        public const int GROUND = 1 << LayerMasks.GROUND;
        public const int CLIP = 1 << LayerMasks.CLIP;
        public const int NAVMESH = 1 << LayerMasks.NAVMESH;
        public const int ENTITY = 1 << LayerMasks.ENTITY;
        public const int AGENT = 1 << LayerMasks.AGENT;
        public const int LADDER = 1 << LayerMasks.LADDER;
        public const int VEHICLE = 1 << LayerMasks.VEHICLE;
        public const int BARRICADE = 1 << LayerMasks.BARRICADE;
        public const int STRUCTURE = 1 << LayerMasks.STRUCTURE;
        public const int TIRE = 1 << LayerMasks.TIRE;
        public const int TRAP = 1 << LayerMasks.TRAP;
        public const int GROUND2 = 1 << LayerMasks.GROUND2;
    }
}
