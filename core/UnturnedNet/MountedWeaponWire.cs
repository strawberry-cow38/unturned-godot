using System;
using SDG.NetPak;
using UnityEngine;

namespace UnturnedGodot.Net
{
    public enum MountedAimMode : byte { None, Look, Point }

    /// <summary>Desired control only. No gun, ammunition, muzzle or damage comes
    /// from the client. The host resolves the currently accepted seat each tick.</summary>
    public struct MountedInputCommand
    {
        public uint VehicleNetId;
        public ushort Sequence;
        public byte Slot;
        public MountedAimMode AimMode;
        public float Yaw, Pitch;
        public Vector3 Target;
        public bool Trigger, HeadOut;
        public bool IsValid => VehicleNetId != 0 && Slot < VehicleMountReplication.MaxMounts
            && AimMode <= MountedAimMode.Point && float.IsFinite(Yaw) && Math.Abs(Yaw) <= 360
            && float.IsFinite(Pitch) && Math.Abs(Pitch) <= 180 && MountedWire.Finite(Target);
        public void Write(NetPakWriter w)
        {
            if (!IsValid) throw new ArgumentException("Invalid mounted input");
            w.WriteUInt32(VehicleNetId); w.WriteUInt16(Sequence); w.WriteUInt8(Slot); w.WriteUInt8((byte)AimMode);
            w.WriteFloat(Yaw); w.WriteFloat(Pitch); MountedWire.WriteVector(w, Target);
            w.WriteBit(Trigger); w.WriteBit(HeadOut);
        }
        public static bool TryRead(NetPakReader r, out MountedInputCommand value)
        {
            value = default;
            var next = new MountedInputCommand();
            if (!r.ReadUInt32(out next.VehicleNetId) || !r.ReadUInt16(out next.Sequence)
                || !r.ReadUInt8(out next.Slot) || !r.ReadUInt8(out byte mode)
                || !r.ReadFloat(out next.Yaw) || !r.ReadFloat(out next.Pitch)
                || !MountedWire.ReadVector(r, out next.Target) || !r.ReadBit(out next.Trigger)
                || !r.ReadBit(out next.HeadOut)) return false;
            next.AimMode = (MountedAimMode)mode;
            if (!next.IsValid) return false;
            value = next; return true;
        }
    }

    public struct MountedShotEvent
    {
        public uint VehicleNetId, Tick;
        public byte Mount;
        public ushort RemainingAmmo, Sequence;
        public bool Npc;
        public PlayerFiredEvent Shot;
        public bool IsValid => VehicleNetId != 0 && Mount < VehicleMountReplication.MaxMounts
            && (Npc ? Shot.PlayerId == 0 : Shot.PlayerId != 0)
            && MountedWire.ValidShot(Shot);
        public void Write(NetPakWriter w)
        {
            if (!IsValid) throw new ArgumentException("Invalid accepted mounted shot");
            w.WriteUInt32(VehicleNetId); w.WriteUInt8(Mount); w.WriteUInt32(Tick);
            w.WriteUInt16(RemainingAmmo); w.WriteUInt16(Sequence); w.WriteBit(Npc); Shot.Write(w);
        }
        public static bool TryRead(NetPakReader r, out MountedShotEvent value)
        {
            value = default; var next = new MountedShotEvent();
            if (!r.ReadUInt32(out next.VehicleNetId) || !r.ReadUInt8(out next.Mount) || !r.ReadUInt32(out next.Tick)
                || !r.ReadUInt16(out next.RemainingAmmo) || !r.ReadUInt16(out next.Sequence) || !r.ReadBit(out next.Npc)
                || !PlayerFiredEvent.TryRead(r, out next.Shot) || !next.IsValid) return false;
            value = next; return true;
        }
    }

    internal static class MountedWire
    {
        public static bool ValidShot(PlayerFiredEvent shot) => Finite(shot.Origin) && Finite(shot.Dir)
            && Math.Abs(shot.Dir.sqrMagnitude-1)<.01f
            && !string.IsNullOrEmpty(shot.Gun) && shot.Gun.Length<=PlayerFiredEvent.MaxGunNameLength
            && System.Linq.Enumerable.All(shot.Gun,c=>c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
        // Raw finite coordinates preserve source muzzle geometry and distant
        // integrated aim targets. Only host state turns an aim point into a shot.
        public static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z)
            && Math.Abs(v.x) <= 1e7f && Math.Abs(v.y) <= 1e7f && Math.Abs(v.z) <= 1e7f;
        public static void WriteVector(NetPakWriter w, Vector3 v) { w.WriteFloat(v.x); w.WriteFloat(v.y); w.WriteFloat(v.z); }
        public static bool ReadVector(NetPakReader r, out Vector3 v)
        {
            v = default;
            if (!r.ReadFloat(out float x) || !r.ReadFloat(out float y) || !r.ReadFloat(out float z)) return false;
            v = new(x, y, z); return Finite(v);
        }
    }
}
