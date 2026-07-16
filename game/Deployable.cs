using Godot;

namespace UnturnedGodot
{
    // A placed deployable in the world (the result of planting a held barricade). Mesh + a box collider
    // hugging it + health, in group "deployables". First pass: it's an inert solid object -- no power/light
    // behaviour yet (that's the next pass; src runtime = InteractableGenerator / InteractableSpot).
    public partial class Deployable : StaticBody3D
    {
        public DeployableDef Def;
        public float Health;

        // Build the mesh + material for a def, returning the MeshInstance and its local AABB (in the flat
        // authored frame, before the -90 X stand-up). Shared by the placed object and the placement ghost.
        public static MeshInstance3D BuildMesh(DeployableDef def, out Aabb localAabb)
        {
            var mesh = def.LoadMesh();
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = def.MakeMaterial() };
            localAabb = mesh != null ? mesh.GetAabb() : new Aabb();
            return mi;
        }

        // `surface` = the ground contact point (the raycast hit); the model is lifted so its base sits there.
        public static Deployable Spawn(Node parent, DeployableDef def, Vector3 surface, float yawDeg)
        {
            var d = new Deployable { Def = def, Health = def.Health };
            var mi = BuildMesh(def, out Aabb ab);
            d.AddChild(mi);
            // collider hugs the real mesh (in the same flat frame as the mesh, so it stands up with the node)
            d.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = ab.Size == Vector3.Zero ? def.Size : ab.Size },
                Position = ab.GetCenter(),
            });
            d.Position = surface + Vector3.Up * DeployableDef.GroundLift(ab);   // base sits on the surface
            d.Basis = DeployableDef.StandBasis(yawDeg);   // yaw + the stand-up
            d.AddToGroup("deployables");
            parent.AddChild(d);
            return d;
        }
    }
}
