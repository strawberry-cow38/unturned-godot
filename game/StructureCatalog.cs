using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    // Structure geometry + tiers for the real building system.
    //
    // GEOMETRY IS THE PORT; the tier table is ours. The constants below are the retail ones, read off
    // HousingConnections.cs:220-266 in the SDK reference and reimplemented here -- they are what makes a
    // player-built base line up with retail's, and getting them wrong is invisible until two pieces refuse to
    // connect. The per-material HEALTH values are OURS: retail carries them per-asset in .dat files we do not
    // have locally, so rather than invent numbers and dress them as ported, they are declared here, pinned by
    // a test, and marked for replacement the moment the real .dat values are available.
    //
    // The single most load-bearing correction: a structure tile edge is SIX metres, not three. BuildTool's
    // stand-in used `GRID = 3f` with the comment "Unturned's structure tile size" -- that is HALF_EDGE_LENGTH,
    // the half-step used for pivot maths, not the tile. Everything built on a 3 m lattice sits on a grid retail
    // has no concept of, so nothing a player builds could ever align with a real foundation.
    public enum EConstruct { Floor, Wall, Rampart, Roof, Pillar, Post, Doorway }

    public static class StructureCatalog
    {
        // ---- geometry (SDK HousingConnections.cs:220-266) --------------------------------------------------
        public const float EdgeLength = 6.0f;          // a structure tile is 6 m on a side
        public const float HalfEdge = 3.0f;
        public const float WallHeight = 4.25f;
        public const float HalfWallHeight = 2.125f;
        public const float WallPivotOffset = HalfWallHeight;  // a wall's pivot sits at its middle
        public const float RampartPivotOffset = 0.9f;         // ramparts are short: pivot much lower than a wall
        public const float FoundationHeight = 10.25f;
        public const float FoundationCenterOffset = -4.875f;   // foundations hang BELOW their anchor point
        public const float LinkTolerance = 0.02f;              // 2 cm — two edges within this are "the same edge"
        public const float MaxPlacementDistance = 16.0f;       // how far you can place from the eye
        public const float MaxSlotSearchDistance = 8.0f;       // how far the snap search looks for a free slot
        public const float MinSlotSearchCosine = 0.9f;         // the slot must be roughly in front of you
        public const float OverlapPadding = 0.02f;             // 2 cm of slack before two pieces count as clashing. NOT YET USED: placement validates slot occupancy + support only, so this is the retail value waiting for a geometric overlap test rather than a live constant.
        public const float RoofThickness = 0.5f;               // ROOF_THICKNESS (HousingConnections.cs:295); the HALF is 0.25
        public const float HalfRoofThickness = 0.25f;          // HALF_ROOF_THICKNESS (HousingConnections.cs:296)

        /// <summary>Vertical offset from a piece's anchor point to its visual/collision centre. Retail keeps
        /// these apart per construct (a wall pivots at its middle, a rampart near its foot, a foundation hangs
        /// below), which is why a single "snap to the grid point" cannot place all of them.</summary>
        public static float PivotOffset(EConstruct c) => c switch
        {
            EConstruct.Wall => WallPivotOffset,
            EConstruct.Doorway => WallPivotOffset,   // a doorway IS a wall with a hole: same slot, same pivot
            EConstruct.Rampart => RampartPivotOffset,
            EConstruct.Pillar => HalfWallHeight,
            EConstruct.Post => HalfWallHeight,
            _ => 0f,                                    // floors/roofs sit ON the plane they snap to
        };

        /// <summary>Does this construct occupy a tile FACE (floor/roof) rather than a tile EDGE (wall/rampart)?
        /// Faces snap to tile centres; edges snap to the midpoint of a tile side. Conflating the two is what
        /// makes walls appear to float half a tile away from the floor they were meant to sit on.</summary>
        public static bool IsFace(EConstruct c) => c == EConstruct.Floor || c == EConstruct.Roof;

        /// <summary>Pillars and posts stand at tile CORNERS, not on faces or edges. Without this they snapped
        /// like walls, to side midpoints -- so a pillar meant to hold up the junction of four tiles landed
        /// halfway along one wall instead, which is both wrong and impossible to build a frame out of. A
        /// corner is where four tiles meet, so it is the one slot a single piece can support all of them
        /// from.</summary>
        public static bool IsCorner(EConstruct c) => c == EConstruct.Pillar || c == EConstruct.Post;

        /// <summary>Vertical extent, used for the overlap check and for the render/collision box.</summary>
        public static Vector3 Extents(EConstruct c) => c switch
        {
            EConstruct.Floor => new Vector3(EdgeLength, RoofThickness, EdgeLength),
            EConstruct.Roof => new Vector3(EdgeLength, RoofThickness, EdgeLength),
            EConstruct.Wall => new Vector3(EdgeLength, WallHeight, 0.25f),
            EConstruct.Doorway => new Vector3(EdgeLength, WallHeight, 0.25f),
            EConstruct.Rampart => new Vector3(EdgeLength, RampartPivotOffset * 2f, 0.25f),
            EConstruct.Pillar => new Vector3(0.4f, WallHeight, 0.4f),
            _ => new Vector3(0.4f, WallHeight, 0.4f),
        };

        // ---- tiers (OURS, not retail values) --------------------------------------------------------------
        public readonly struct Tier
        {
            public readonly string Name;
            public readonly int Health;          // OURS -- see the header note
            public readonly bool RequiresPillars;
            public readonly bool Vulnerable;     // retail's isVulnerable: can melee/bullets hurt it at all
            public readonly Color Tint;
            /// <summary>Retail's Salvage_Duration_Multiplier (ItemStructureAsset.cs:79,246): how much LONGER
            /// than the base time this takes to take back down. Sturdier tiers are slower to salvage, which is
            /// what stops a raider quietly dismantling a metal wall as fast as a wooden one.</summary>
            public readonly float SalvageDurationMultiplier;
            public Tier(string name, int health, bool requiresPillars, bool vulnerable, Color tint, float salvageMul = 1f)
            { Name = name; Health = health; RequiresPillars = requiresPillars; Vulnerable = vulnerable; Tint = tint; SalvageDurationMultiplier = salvageMul; }
        }

        // Ordered weakest -> strongest; the order IS the upgrade path.
        public static readonly Tier[] Tiers =
        {
            new Tier("wood",   300,  true,  true,  new Color(0.52f, 0.37f, 0.20f), 1.0f),
            new Tier("brick",  600,  true,  false, new Color(0.55f, 0.31f, 0.26f), 1.5f),
            new Tier("metal", 1000,  false, false, new Color(0.55f, 0.57f, 0.60f), 2.0f),
        };

        public static Tier TierAt(int i) => Tiers[Mathf.Clamp(i, 0, Tiers.Length - 1)];
        public static int TierCount => Tiers.Length;

        public static readonly EConstruct[] Buildable =
        {
            EConstruct.Floor, EConstruct.Wall, EConstruct.Doorway, EConstruct.Pillar, EConstruct.Rampart, EConstruct.Roof,
        };

        /// <summary>Snap a world point to the structure lattice for this construct. FACES land on tile centres;
        /// EDGES land on the midpoint of the nearest tile side, and carry the facing that side implies -- which
        /// is why this returns a rotation as well as a position.</summary>
        /// <param name="anchorY">The Y the storey lattice is measured FROM -- normally the Y of the base you are
        /// building onto. This used to be hardcoded to world zero (`Round(world.Y / WallHeight) * WallHeight`),
        /// which anchors every storey in the world to sea level: a floor on terrain at y=12 snapped to 12.75,
        /// missed its own ground check, and refused to place. You could not found a wood or brick base anywhere
        /// more than ~2.1 m above sea level, and any piece that did place floated or buried itself by up to
        /// half a storey. Pass float.NaN for "no base here yet" -- the piece then takes the terrain height it
        /// was aimed at, which is how a foundation gets to sit on the ground it was placed on.</param>
        public static (Vector3 Pos, float YawDeg) Snap(Vector3 world, EConstruct c, float anchorY = 0f)
        {
            float cx = Mathf.Round(world.X / EdgeLength) * EdgeLength;
            float cz = Mathf.Round(world.Z / EdgeLength) * EdgeLength;
            float y = float.IsNaN(anchorY)
                ? world.Y                                                              // first piece: free, sits where you put it
                : anchorY + Mathf.Round((world.Y - anchorY) / WallHeight) * WallHeight; // storeys measured off the base

            if (IsFace(c)) return (new Vector3(cx, y, cz), 0f);

            // CORNER: tile centres sit on multiples of the edge length, so the corners between them sit on the
            // ODD multiples of the half edge -- 3, 9, 15 ... Round to that lattice directly rather than to a
            // centre and then nudge, which double-rounds and lands a pillar a whole tile away near a boundary.
            if (IsCorner(c))
            {
                float qx = Mathf.Round((world.X - HalfEdge) / EdgeLength) * EdgeLength + HalfEdge;
                float qz = Mathf.Round((world.Z - HalfEdge) / EdgeLength) * EdgeLength + HalfEdge;
                return (new Vector3(qx, y, qz), 0f);
            }

            // EDGE: pick whichever of the four tile sides the point is nearest, and face along it.
            // Mathf.Sign(0) is 0, not 1 -- so a point exactly ON a tile centre (Snap(floor.Pos, Wall), which is
            // a natural thing for a caller to write) produced cx + 0*HalfEdge: a wall bisecting its own tile,
            // off-lattice, and invisible to every probe that only ever issues keys at cx +- HalfEdge. Ties go to
            // the positive side deterministically instead.
            float dx = world.X - cx, dz = world.Z - cz;
            if (Mathf.Abs(dx) >= Mathf.Abs(dz))
                return (new Vector3(cx + (dx < 0f ? -HalfEdge : HalfEdge), y, cz), 90f);
            return (new Vector3(cx, y, cz + (dz < 0f ? -HalfEdge : HalfEdge)), 0f);
        }

        /// <summary>The lattice key a piece occupies. Two pieces sharing a key are the same slot -- the cheap
        /// form of retail's LINK_TOLERANCE edge match, quantised so floating-point drift cannot make two pieces
        /// 2 cm apart read as different slots.</summary>
        // ---- doorway opening (OURS, not retail) ------------------------------------------------------------
        // Retail keeps a doorway's hole in the asset mesh, which we do not have locally. Rather than guess a
        // number and present it as ported, these are declared here and pinned by a test -- the same treatment
        // the tier health values get. Sized so a player capsule fits with clearance.
        public const float DoorOpeningWidth = 2.0f;
        public const float DoorOpeningHeight = 3.0f;

        /// <summary>Is this construct a wall-class piece -- something that fills a tile EDGE? Doorways share the
        /// wall's slot deliberately: an edge holds a wall OR a doorway, never both, and because SlotKey maps
        /// every non-face non-corner construct to the same "E" namespace, that mutual exclusion is automatic
        /// rather than a rule someone has to remember to write.</summary>
        public static bool IsWallClass(EConstruct c) => c == EConstruct.Wall || c == EConstruct.Doorway;

        public static string SlotKey(Vector3 snapped, EConstruct c)
        {
            int q(float v) => Mathf.RoundToInt(v / LinkTolerance);
            // Roof gets its OWN namespace. It snaps like a face (tile centres) but it is not the same SLOT as a
            // floor: sharing "F" meant a roof capping level 0 and the floor of level 1 were the same key, so
            // roofing a tile permanently blocked building a storey above it -- a dead end you could walk into
            // and not undo without salvaging the roof.
            string kind = c == EConstruct.Roof ? "R" : IsFace(c) ? "F" : IsCorner(c) ? "C" : "E";
            return $"{kind}:{q(snapped.X)}:{q(snapped.Y)}:{q(snapped.Z)}";
        }
    }
}
