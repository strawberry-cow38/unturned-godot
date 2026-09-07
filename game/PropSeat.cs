using Godot;

namespace UnturnedGodot
{
    /// <summary>A place on a prop where a player can sit (master 2026-09-07: "wire up sitting on couches,
    /// chairs, benches, etc").
    ///
    /// This node is only the ANCHOR -- a world transform, an occupancy flag, and the outline that says the
    /// prop is interactable. The sitting itself lives in PlayerController, next to the vehicle-seat code it
    /// mirrors, because that is where the camera, the collider and the animated body already are.
    ///
    /// There is no collider here on purpose. The prop already has a trimesh StaticBody from PlaceObject, and
    /// that body is tagged with the seats it carries (HitMeta, an ARRAY -- a couch has two, a picnic table
    /// eight). So looking anywhere at a chair finds its seat, exactly the way looking anywhere at a doored
    /// prop finds its door, and a bench does not sprout eight invisible boxes for the look ray to snag on.
    ///
    /// SEAT ANCHORS ARE MEASURED, not authored: tools/extract_seats.py finds the up-facing plane in the
    /// 0.40..0.95 m band and splits it into islands. See seats.txt.
    ///
    /// SINGLEPLAYER ONLY, deliberately and like the prop doors above it (WorldBuilder's door branch is gated
    /// the same way): a seat has no NetId, no replication and no server-side occupancy, so on a dedicated
    /// server two players would sit in the same chair and neither would see the other do it. Spawned only in
    /// WorldMode.Playable, so that state cannot exist rather than existing and being wrong.</summary>
    public partial class PropSeat : Node3D
    {
        /// <summary>Meta key on the PROP's body collider, holding a Godot Array of the PropSeats it carries.
        /// An array rather than a single seat because most seat props have more than one, and which one you
        /// get should be the one you are looking at.</summary>
        public static readonly StringName HitMeta = "propseat";

        /// <summary>Who is sitting here, or null. A plain reference rather than a bool: the seat has to be
        /// released when its occupant dies or is teleported away, and "is it me" is the question the F key
        /// asks.</summary>
        public Node3D Occupant;
        public bool Free => Occupant == null || !IsInstanceValid(Occupant);

        /// <summary>Where the sitter goes: origin at the seat surface, -Z along the facing (Godot's forward),
        /// Y up. Built once at spawn from the placement basis, since a world prop never moves.</summary>
        public Transform3D Anchor { get; private set; }

        /// <summary>The prop's whole-mesh outline, shown while this seat is the look target -- the same
        /// affordance a doored prop gets, and the same reason: an interaction with no visible cue is one
        /// nobody finds. Shared by every seat on the prop, so it is set here, not owned here.</summary>
        public MeshInstance3D BodyOutline;

        /// <summary>Spawn a seat from a catalog row. `propXform` is the placement (basis + world position)
        /// that the prop's own mesh was placed with, and `posLocal`/`faceLocal` are in the raw obj space that
        /// seats.txt stores -- the identical convention ObjectDoor.Spawn takes its pivot and axis in, so a
        /// rotated prop's seats rotate with it for free.</summary>
        public static PropSeat Spawn(Node parent, Transform3D propXform, Vector3 posLocal, Vector3 faceLocal)
        {
            var origin = propXform * posLocal;
            var fwd = (propXform.Basis * faceLocal).Normalized();
            // A seat's facing is a YAW: the catalog's direction is horizontal by construction, but a prop
            // placed on a slope tilts its basis, and a sitter leaning 8 degrees into a hillside looks broken.
            // Flatten it, and fall back to the prop's own forward if the seat somehow faces straight up.
            fwd.Y = 0f;
            if (fwd.LengthSquared() < 1e-4f)
            {
                fwd = -propXform.Basis.Z; fwd.Y = 0f;
                if (fwd.LengthSquared() < 1e-4f) fwd = Vector3.Forward;
            }
            fwd = fwd.Normalized();
            var s = new PropSeat { Name = "PropSeat" };
            // LookingAt wants a TARGET, and Godot's forward is -Z, so aim it a metre along the facing.
            s.Anchor = new Transform3D(Basis.Identity, origin).LookingAt(origin + fwd, Vector3.Up);
            parent.AddChild(s);
            s.GlobalTransform = s.Anchor;
            s.AddToGroup("propseats");
            return s;
        }

        // Through ShowOutline rather than by setting Visible: it claims the rim COLOUR in the same call, so a
        // seat cannot light a silhouette without saying what colour it is -- the exact mistake that overlay's
        // API exists to make impossible.
        public void SetLookFocused(bool on) => OutlineOverlay.ShowOutline(on, Colors.White, BodyOutline);

        /// <summary>Height of the rig's hips above its own root, straight out of rig.json: the Spine bone's
        /// rest position is (0, 0.735, 0) on the Skeleton root, and Idle_Sit moves no bone's POSITION -- only
        /// rotations -- so a seated character's pelvis is still exactly this far above wherever the body node
        /// is put. Sitting therefore means: put the player's origin one hip-height BELOW the cushion, and the
        /// pelvis lands on it.
        ///
        /// It also makes the camera free. The standing eye sits at 1.6 above the origin, i.e. 0.865 above the
        /// hips; with the hips on the cushion that same 1.6 IS the seated eye height, so nothing has to move
        /// the camera and there is no second number to keep in step with the first.</summary>
        public const float HipRest = 0.735f;

        /// <summary>The Y the prop is standing on -- its placement origin, recorded at spawn so ExitSpot puts
        /// you on the floor rather than at cushion height.</summary>
        public float GroundY;

        /// <summary>Where a player stands up to: a step out along the seat's facing, dropped to the prop's own
        /// floor. Taking the seat's height instead would stand you up 0.75 m in the air on a couch and let you
        /// fall back through it. The caller still runs this through SafeSpot -- it only says which way "off"
        /// is, not that the spot is clear.</summary>
        public Vector3 ExitSpot(float footClearance = 0.1f)
        {
            var p = Anchor.Origin + -Anchor.Basis.Z * 1.1f;
            p.Y = GroundY + footClearance;
            return p;
        }
    }
}
