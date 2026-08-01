using Godot;
namespace UnturnedGodot
{
    // Placeable-storage spawn seam, mirroring FluidDeploy: a DeployableDef with IsStorage spawns a Refrigerator node
    // LOCALLY (SP; device MP replication = fast-follow), not a plain Deployable.
    public static class FridgeDeploy
    {
        public static Node3D SpawnFor(DeployableDef def, Node parent, Vector3 pos, float yawDeg)
        {
            if (def?.IsStorage != true || parent == null) return null;
            return Refrigerator.Spawn(parent, pos, yawDeg: yawDeg);
        }
    }
}
