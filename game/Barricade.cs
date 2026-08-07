using Godot;

namespace UnturnedGodot
{
    // A barricade = a Deployable mounted on a structure surface (wall / floor / ceiling) rather than only flat ground.
    // We reuse Deployable wholesale — Deployable.Spawn gives us the body, box collider, health, damage / fire / wreck
    // lifecycle, ports and look-at billboard (Deployable.cs:162) — then re-seat the node for the mount family. The
    // collider + mesh are children in the same flat frame, so they rotate with the node and a wall barricade gets a
    // correctly-oriented collider for free.
    //
    // HP is per-asset from the def (src ItemBarricadeAsset.health) — a barricade's own number, NOT any structure-tier
    // value. This mirrors the src split: BarricadeManager owns barricade HP, StructureManager owns structure HP.
    public static class Barricade
    {
        // Place a barricade at a surface hit. point/normal come from a BarricadePlacer aim (or a placement message);
        // yawDeg is the FINAL yaw (for Wall, the caller passes BarricadePlacer.YawFacing(normal) + any manual spin —
        // the placer already resolves that into placer.Yaw). Returns the live Deployable node (parented + in the tree).
        public static Deployable PlaceOnSurface(Node parent, DeployableDef def, Vector3 point, Vector3 normal, float yawDeg,
                                                BarricadeMount mount = BarricadeMount.Wall, SDG.Unturned.Item backing = null)
        {
            var d = Deployable.Spawn(parent, def, point, yawDeg, backing);   // full lifecycle; HP = def.Health, seats upright on the point
            normal = normal.Normalized();
            var tmp = Deployable.BuildMesh(def, out Aabb ab);   // throwaway: recover the base-to-origin lift (a def/mesh property)
            tmp.QueueFree();
            // seat per mount family: Floor stands the base on the point along up; Wall/Sticky hug the surface by a
            // small standoff along the normal (DeployableDef.Offset is a GROUND clearance, too big as a wall standoff
            // -- see BarricadePlacer.WallStandoff; src point = hit + normal*offset, UseableBarricade.cs:817).
            Vector3 origin = mount == BarricadeMount.Floor
                ? point + Vector3.Up * (def.Upright ? -ab.Position.Y : DeployableDef.GroundLift(ab))
                : point + normal * Mathf.Min(def.Offset, 0.1f);
            d.GlobalTransform = new Transform3D(BarricadePlacer.MountBasis(mount, normal, yawDeg), origin);
            d.AddToGroup("barricades");   // a surface barricade (still a "deployable" too — look-at / repair target it either way)
            return d;
        }
    }
}
