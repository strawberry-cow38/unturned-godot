using Godot;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Places a FLUID device from its DeployableDef (the Fluid marker). The power placement rail (EquipHeldDeployable ->
    // DeployablePlacer ghost -> LMB place) calls this instead of Deployable.Spawn when def.Fluid != null, so fluid
    // containers ride the exact same held-item + ghost + place flow as power deployables — but spawn a FluidContainer /
    // FluidPump (the fluid IO system), not a power Deployable. SP-local for now (MP replication is a fast-follow).
    public static class FluidDeploy
    {
        public static Node3D SpawnFor(DeployableDef def, Node parent, Vector3 pos, float yawDeg)
        {
            if (def?.Fluid == null || parent == null) return null;
            FluidContainer c = def.Fluid.Value switch
            {
                FluidRole.Source      => FluidContainer.Make(FluidRole.Source, new FluidTank(def.FluidType, def.FluidCapacity, def.FluidCapacity), def.FluidRate),   // starts FULL
                FluidRole.Storage     => FluidContainer.Make(FluidRole.Storage, new FluidTank(def.FluidType, def.FluidCapacity, 0f), def.FluidRate),                 // starts empty, adopts
                FluidRole.Consumer    => FluidContainer.Make(FluidRole.Consumer, new FluidTank(def.FluidType, def.FluidCapacity, 0f), def.FluidRate),
                FluidRole.Splitter    => FluidContainer.MakeFitting(FluidRole.Splitter, def.FluidWays),
                FluidRole.Combiner    => FluidContainer.MakeFitting(FluidRole.Combiner, def.FluidWays),
                FluidRole.Pump        => FluidPump.Make(),
                FluidRole.Valve       => FluidContainer.MakeValve(),
                FluidRole.Transformer => FluidContainer.MakeTransformer(def.FluidType, def.FluidOut, def.FluidRate, 1f),
                _                     => FluidContainer.Make(FluidRole.Storage, new FluidTank(FluidType.None, def.FluidCapacity, 0f), def.FluidRate),
            };
            c.Infinite = def.FluidInfinite;   // a submersible inlet: never depletes
            c.NoHead = def.FluidNoHead;       // ...and has no head -> its output needs a pump
            c.Def = def;                      // remember the item def so hold-F pickup returns the right item
            c.Position = pos;
            c.RotationDegrees = new Vector3(0f, yawDeg, 0f);
            parent.AddChild(c);
            // make sure a FluidManager is ticking the net (one per world; created lazily on the first fluid placement)
            if (c.GetTree() != null && c.GetTree().GetNodesInGroup("fluid_managers").Count == 0) parent.AddChild(new FluidManager());
            return c;
        }
    }
}
