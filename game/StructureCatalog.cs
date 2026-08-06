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
    public enum EConstruct { Floor, Wall, Rampart, Roof, Pillar, Post }

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
        public const float OverlapPadding = 0.02f;             // 2 cm of slack before two pieces count as clashing

        /// <summary>Vertical offset from a piece's anchor point to its visual/collision centre. Retail keeps
        /// these apart per construct (a wall pivots at its middle, a rampart near its foot, a foundation hangs
        /// below), which is why a single "snap to the grid point" cannot place all of them.</summary>
        public static float PivotOffset(EConstruct c) => c switch
        {
            EConstruct.Wall => WallPivotOffset,
            EConstruct.Rampart => RampartPivotOffset,
            EConstruct.Pillar => HalfWallHeight,
            EConstruct.Post => HalfWallHeight,
            _ => 0f,                                    // floors/roofs sit ON the plane they snap to
        };

        /// <summary>Does this construct occupy a tile FACE (floor/roof) rather than a tile EDGE (wall/rampart)?
        /// Faces snap to tile centres; edges snap to the midpoint of a tile side. Conflating the two is what
        /// makes walls appear to float half a tile away from the floor they were meant to sit on.</summary>
        public static bool IsFace(EConstruct c) => c == EConstruct.Floor || c == EConstruct.Roof;

        /// <summary>Vertical extent, used for the overlap check and for the render/collision box.</summary>
        public static Vector3 Extents(EConstruct c) => c switch
        {
            EConstruct.Floor => new Vector3(EdgeLength, 0.25f, EdgeLength),
            EConstruct.Roof => new Vector3(EdgeLength, 0.25f, EdgeLength),
            EConstruct.Wall => new Vector3(EdgeLength, WallHeight, 0.25f),
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
            public Tier(string name, int health, bool requiresPillars, bool vulnerable, Color tint)
            { Name = name; Health = health; RequiresPillars = requiresPillars; Vulnerable = vulnerable; Tint = tint; }
        }

        // Ordered weakest -> strongest; the order IS the upgrade path.
        public static readonly Tier[] Tiers =
        {
            new Tier("wood",   300,  true,  true,  new Color(0.52f, 0.37f, 0.20f)),
            new Tier("brick",  600,  true,  false, new Color(0.55f, 0.31f, 0.26f)),
            new Tier("metal", 1000,  false, false, new Color(0.55f, 0.57f, 0.60f)),
        };

        public static Tier TierAt(int i) => Tiers[Mathf.Clamp(i, 0, Tiers.Length - 1)];
        public static int TierCount => Tiers.Length;

        public static readonly EConstruct[] Buildable =
        {
            EConstruct.Floor, EConstruct.Wall, EConstruct.Pillar, EConstruct.Rampart, EConstruct.Roof,
        };

        /// <summary>Snap a world point to the structure lattice for this construct. FACES land on tile centres;
        /// EDGES land on the midpoint of the nearest tile side, and carry the facing that side implies -- which
        /// is why this returns a rotation as well as a position.</summary>
        public static (Vector3 Pos, float YawDeg) Snap(Vector3 world, EConstruct c)
        {
            float cx = Mathf.Round(world.X / EdgeLength) * EdgeLength;
            float cz = Mathf.Round(world.Z / EdgeLength) * EdgeLength;
            float y = Mathf.Round(world.Y / WallHeight) * WallHeight;

            if (IsFace(c)) return (new Vector3(cx, y, cz), 0f);

            // EDGE: pick whichever of the four tile sides the point is nearest, and face along it.
            float dx = world.X - cx, dz = world.Z - cz;
            if (Mathf.Abs(dx) >= Mathf.Abs(dz))
                return (new Vector3(cx + Mathf.Sign(dx) * HalfEdge, y, cz), 90f);
            return (new Vector3(cx, y, cz + Mathf.Sign(dz) * HalfEdge), 0f);
        }

        /// <summary>The lattice key a piece occupies. Two pieces sharing a key are the same slot -- the cheap
        /// form of retail's LINK_TOLERANCE edge match, quantised so floating-point drift cannot make two pieces
        /// 2 cm apart read as different slots.</summary>
        public static string SlotKey(Vector3 snapped, EConstruct c)
        {
            int q(float v) => Mathf.RoundToInt(v / LinkTolerance);
            return $"{(IsFace(c) ? "F" : "E")}:{q(snapped.X)}:{q(snapped.Y)}:{q(snapped.Z)}";
        }
    }
}
