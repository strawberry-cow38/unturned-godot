using System;
using System.Collections.Generic;

namespace UnturnedSim
{
    /// <summary>What a whole building is FOR. One per building, and the volume it applies to is DERIVED from
    /// the walls rather than drawn -- strawberry_cow: "volume(s) that perfectly encompass the building, not
    /// stretching past". Deriving it is not a shortcut: a hand-drawn box goes stale the moment a wall moves,
    /// and "perfectly encompasses" is a property nothing but the walls can answer.</summary>
    public enum BuildingKind { Residential, Commercial, Industrial, Misc }

    /// <summary>What one ROOM is for. Extending this is a one-line change and safe: designations persist by
    /// NAME, never by ordinal, so adding or reordering members cannot silently re-label existing saves.
    ///
    /// An enum rather than a free string, deliberately. A free string rots -- "Bedroom", "bedroom" and
    /// "bed room" become three kinds nobody notices until something keys off them -- and this list is going
    /// to be read by the furniture rules later, which is exactly the code a typo breaks silently.</summary>
    public enum RoomKind
    {
        Unassigned,
        Bedroom, Bathroom, Kitchen, LivingRoom, DiningRoom,
        Hall, Stairwell, Closet,
        Garage, Workshop, Storage,
        Office, Shop, Warehouse,
    }

    /// <summary>A room designation as it is STORED: a kind plus a point inside the room it labels.
    ///
    /// THE ANCHOR IS THE WHOLE DESIGN. Rooms are DERIVED -- RoomEnclosure recomputes them from the walls
    /// every time -- so there is no stable room identity to hang an id on. Move one wall and the room a
    /// designation was attached to stops existing. Storing a point INSIDE the room instead lets the label
    /// re-find its room on load by asking which enclosure now contains that point, which survives nudging a
    /// wall, adding a partition elsewhere, or renumbering.
    ///
    /// It also fails LEGIBLY: delete the room and its anchor matches nothing, so the designation is visibly
    /// orphaned rather than silently reassigned to whatever room inherited the index.</summary>
    public struct RoomDesignation
    {
        public RoomKind Kind;
        /// <summary>A point inside the room, in the same plan space RoomEnclosure works in.</summary>
        public float X, Z;

        public RoomDesignation(RoomKind kind, float x, float z) { Kind = kind; X = x; Z = z; }
    }

    /// <summary>Framework for building/room designation. NO placement logic -- strawberry_cow: "we dont have
    /// to do the stories system yet. just the framework". What this owns is the part that is expensive to
    /// change later: how a designation is anchored, how it re-finds its room, and how it persists.</summary>
    public static class RoomDesignations
    {
        /// <summary>Is this plan point inside the room? Even-odd ray cast against the outline.
        ///
        /// Counts crossings of a ray going in +X. A vertex exactly on the ray is the classic double-count,
        /// so the test is asymmetric on Z (one endpoint inclusive, the other exclusive) -- which makes a
        /// vertex belong to exactly one of its two edges.</summary>
        public static bool Contains(IReadOnlyList<RoomEnclosure.PlanPoint> outline, float x, float z)
        {
            if (outline == null || outline.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
            {
                var a = outline[i];
                var b = outline[j];
                if ((a.Z > z) == (b.Z > z)) continue;                 // the edge does not straddle the ray
                float t = (z - a.Z) / (b.Z - a.Z);
                if (x < a.X + t * (b.X - a.X)) inside = !inside;
            }
            return inside;
        }

        /// <summary>Re-attach stored designations to the rooms that currently exist.
        ///
        /// Returns one entry per ROOM (index into <paramref name="rooms"/>), and the designations that
        /// matched no room are handed back separately rather than dropped -- an orphan is information (a
        /// room was deleted or opened up) and silently discarding it is how a save quietly loses work.
        ///
        /// First match wins. Rooms do not overlap by construction (they are faces of one planar graph), so
        /// a point is inside at most one -- but if two designations land in the SAME room the later one is
        /// an orphan too, rather than overwriting: two labels for one room is a conflict the caller should
        /// see, not something to resolve by arrival order.</summary>
        public static void Resolve(
            IReadOnlyList<RoomEnclosure.Room> rooms,
            IReadOnlyList<RoomDesignation> stored,
            out RoomKind[] byRoom,
            out List<RoomDesignation> orphans)
        {
            byRoom = new RoomKind[rooms?.Count ?? 0];
            orphans = new List<RoomDesignation>();
            if (rooms == null || stored == null) return;

            for (int i = 0; i < byRoom.Length; i++) byRoom[i] = RoomKind.Unassigned;

            foreach (var d in stored)
            {
                int hit = -1;
                for (int r = 0; r < rooms.Count; r++)
                    if (Contains(rooms[r].Outline, d.X, d.Z)) { hit = r; break; }

                if (hit < 0 || byRoom[hit] != RoomKind.Unassigned) { orphans.Add(d); continue; }
                byRoom[hit] = d.Kind;
            }
        }

        /// <summary>Where to anchor a NEW designation for a room: its centroid, nudged onto the room if the
        /// centroid falls outside.
        ///
        /// A centroid is outside its own polygon for any concave shape -- an L-shaped room is the common
        /// case here, not a curiosity -- and an anchor outside the room it labels matches nothing on the
        /// very next load. When that happens, fall back to the centre of the room's largest slab, which is
        /// inside by construction because the slabs are a cover OF the room.</summary>
        public static bool AnchorFor(RoomEnclosure.Room room, out float x, out float z)
        {
            x = z = 0f;
            if (room == null || room.Outline.Count < 3) return false;

            float sx = 0f, sz = 0f;
            foreach (var p in room.Outline) { sx += p.X; sz += p.Z; }
            x = sx / room.Outline.Count;
            z = sz / room.Outline.Count;
            if (Contains(room.Outline, x, z)) return true;

            // Concave: take the biggest axis-aligned piece the room was decomposed into.
            float best = 0f;
            bool found = false;
            foreach (var s in room.Slabs)
                if (s.Area > best) { best = s.Area; x = (s.MinX + s.MaxX) * 0.5f; z = (s.MinZ + s.MaxZ) * 0.5f; found = true; }
            return found;
        }

        /// <summary>Parse a kind by NAME, case-insensitively. Unknown names become Unassigned rather than
        /// throwing: a save written by a newer editor with a kind this build has never heard of should lose
        /// that one label, not fail to load the building.</summary>
        public static RoomKind ParseRoom(string s)
            => Enum.TryParse<RoomKind>(s, true, out var k) ? k : RoomKind.Unassigned;

        public static BuildingKind ParseBuilding(string s)
            => Enum.TryParse<BuildingKind>(s, true, out var k) ? k : BuildingKind.Misc;
    }
}
