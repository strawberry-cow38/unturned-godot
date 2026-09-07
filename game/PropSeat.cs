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
    /// MULTIPLAYER (wire v35): a seat carries a NetId assigned in world-build order by InteractableNetSync,
    /// the same trick doors and beds use -- every peer runs the identical WorldBuilder, so the nth seat is
    /// the nth seat everywhere and no id has to be minted or sent. The SERVER owns who is in which chair
    /// (ServerInteractables), because two clients each deciding they took the same seat is exactly the
    /// "multiple people can't get in a car" failure the vehicle occupancy check exists to stop.</summary>
    public partial class PropSeat : Node3D
    {
        /// <summary>Meta key on the PROP's body collider, holding a Godot Array of the PropSeats it carries.
        /// An array rather than a single seat because most seat props have more than one, and which one you
        /// get should be the one you are looking at.</summary>
        public static readonly StringName HitMeta = "propseat";

        /// <summary>Replication id, 0 in singleplayer. Assigned by InteractableNetSync from world-build
        /// order. Held in a lookup so an arriving SeatOccupiedEvent can find the node without a tree walk --
        /// and registered on ENTERING the tree rather than only in _Ready, because _Ready fires once and a
        /// re-parented seat would otherwise deregister on exit and never come back (the same shape Door and
        /// Bed both needed).</summary>
        public uint NetId
        {
            get => _netId;
            set
            {
                if (_netId != 0) _byNetId.Remove(_netId);
                _netId = value;
                if (value != 0) _byNetId[value] = this;
            }
        }
        uint _netId;

        static readonly System.Collections.Generic.Dictionary<uint, PropSeat> _byNetId
            = new System.Collections.Generic.Dictionary<uint, PropSeat>();

        public static bool TryGetByNetId(uint netId, out PropSeat seat)
        {
            if (_byNetId.TryGetValue(netId, out seat) && IsInstanceValid(seat)) return true;
            _byNetId.Remove(netId);   // the node died without clearing its id
            seat = null;
            return false;
        }

        /// <summary>Drop every id. Called between world builds, or a second map's seat 1 collides with the
        /// first map's and an event lands on a freed node.</summary>
        public static void ResetNetIds() => _byNetId.Clear();

        public override void _EnterTree() { if (_netId != 0) _byNetId[_netId] = this; }
        public override void _ExitTree()
        {
            if (_netId != 0 && _byNetId.TryGetValue(_netId, out var held) && held == this) _byNetId.Remove(_netId);
        }

        /// <summary>Who is sitting here, or null. A plain reference rather than a bool: the seat has to be
        /// released when its occupant dies or is teleported away, and "is it me" is the question the F key
        /// asks.</summary>
        public Node3D Occupant;

        /// <summary>The player id sitting here according to the SERVER, or 0. Separate from Occupant because
        /// a remote sitter has no local node to point at -- we know a chair is busy without owning whoever is
        /// in it -- and conflating the two would either hide remote occupancy or make Occupant lie about what
        /// it holds. Set only from SeatOccupiedEvent / the snapshot table; always 0 in singleplayer.</summary>
        public ushort NetOccupant;

        public bool Free => (Occupant == null || !IsInstanceValid(Occupant)) && NetOccupant == 0;

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
