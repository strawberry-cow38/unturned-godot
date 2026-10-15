using System;
using SDG.NetPak;
using SDG.Unturned;

namespace UnturnedGodot.Net
{
    // v62: intents identify server-owned item instances; no client ammo count,
    // attachment assignment or sender identity is accepted in these messages.
    public enum EquipmentAction : byte { Install, Remove, ChooseShell, Unload, Rack, Firemode }
    public enum EquipmentSlot : byte { Sight, Barrel, Grip, Tactical, Magazine }
    public enum EquipmentSlotKind : byte { Attachment, Magazine, Shell }
    public enum EquipmentFactKind : byte { Rejected, Changed, Reload, Rechamber, ReloadComplete, ReloadCancelled, EquipRefused, DryFire, BarrelBroken }

    public readonly record struct EquipmentAddress(byte Page, byte X, byte Y, ushort Id)
    {
        public void Write(NetPakWriter w) { w.WriteUInt8(Page); w.WriteUInt8(X); w.WriteUInt8(Y); w.WriteUInt16(Id); }
        public static bool TryRead(NetPakReader r, out EquipmentAddress value)
        {
            value = default;
            if (!r.ReadUInt8(out byte page) || !r.ReadUInt8(out byte x) || !r.ReadUInt8(out byte y)
                || !r.ReadUInt16(out ushort id) || page >= PlayerInventory.OWNPAGES) return false;
            value = new(page, x, y, id); return true;
        }
    }

    public struct SelectHeldCommand
    {
        public EquipmentAddress Address;
        public ulong Token;
        public bool Equip;
        public void Write(NetPakWriter w) { Address.Write(w); w.WriteUInt64(Token); w.WriteBit(Equip); }
        public static bool TryRead(NetPakReader r, out SelectHeldCommand cmd)
        {
            cmd = default;
            if (!EquipmentAddress.TryRead(r, out var address) || !r.ReadUInt64(out ulong token) || !r.ReadBit(out bool equip)) return false;
            cmd = new() { Address = address, Token = token, Equip = equip }; return true;
        }
    }

    public struct WeaponIntentCommand
    {
        public EquipmentAction Action;
        public EquipmentSlot Slot;
        public ulong GunToken, SelectionVersion, IncomingToken;
        public EquipmentAddress Incoming;
        public ushort ShellId;
        public byte Firemode;
        public void Write(NetPakWriter w)
        {
            w.WriteUInt8((byte)Action); w.WriteUInt8((byte)Slot);
            w.WriteUInt64(GunToken); w.WriteUInt64(SelectionVersion); w.WriteUInt64(IncomingToken);
            Incoming.Write(w); w.WriteUInt16(ShellId); w.WriteUInt8(Firemode);
        }
        public static bool TryRead(NetPakReader r, out WeaponIntentCommand cmd)
        {
            cmd = default;
            if (!r.ReadUInt8(out byte action) || action > (byte)EquipmentAction.Firemode
                || !r.ReadUInt8(out byte slot) || slot > (byte)EquipmentSlot.Magazine
                || !r.ReadUInt64(out ulong gun) || gun == 0 || !r.ReadUInt64(out ulong version)
                || !r.ReadUInt64(out ulong incomingToken) || !EquipmentAddress.TryRead(r, out var incoming)
                || !r.ReadUInt16(out ushort shell) || !r.ReadUInt8(out byte mode) || mode > 3
                || (action == (byte)EquipmentAction.Install && incomingToken == 0)) return false;
            cmd = new() { Action = (EquipmentAction)action, Slot = (EquipmentSlot)slot, GunToken = gun,
                SelectionVersion = version, IncomingToken = incomingToken, Incoming = incoming, ShellId = shell, Firemode = mode };
            return true;
        }
    }

    public struct AttachmentOptionsCommand
    {
        public uint RequestId;
        public void Write(NetPakWriter w) => w.WriteUInt32(RequestId);
        public static bool TryRead(NetPakReader r, out AttachmentOptionsCommand cmd)
        { cmd = default; if (!r.ReadUInt32(out uint id)) return false; cmd.RequestId = id; return true; }
    }

    public readonly record struct EquipmentChoice(EquipmentAddress Address, ulong Token, ushort Amount, byte Quality)
    {
        public void Write(NetPakWriter w) { Address.Write(w); w.WriteUInt64(Token); w.WriteUInt16(Amount); w.WriteUInt8(Quality); }
        public static bool TryRead(NetPakReader r, out EquipmentChoice value)
        {
            value = default;
            if (!EquipmentAddress.TryRead(r, out var address) || !r.ReadUInt64(out ulong token) || token == 0
                || !r.ReadUInt16(out ushort amount) || !r.ReadUInt8(out byte quality)) return false;
            value = new(address, token, amount, quality); return true;
        }
    }
    public readonly record struct EquipmentShell(ushort Id, uint Count, bool Selected);

    public sealed class EquipmentSlotOptions
    {
        public EquipmentSlot Slot;
        public EquipmentSlotKind Kind;
        public bool Visible, Available, Detachable;
        public int InstalledId;
        public float[] Color = new float[4];
        public EquipmentChoice[] Choices = Array.Empty<EquipmentChoice>();
        public EquipmentShell[] Shells = Array.Empty<EquipmentShell>();
        public void Write(NetPakWriter w)
        {
            if (Color == null || Color.Length != 4 || Choices.Length > EquipmentWire.MaxChoices || Shells.Length > EquipmentWire.MaxChoices)
                throw new ArgumentException("Invalid equipment option bounds");
            w.WriteUInt8((byte)Slot); w.WriteUInt8((byte)Kind); w.WriteBit(Visible); w.WriteBit(Available); w.WriteBit(Detachable);
            w.WriteInt32(InstalledId); foreach (float channel in Color) w.WriteFloat(channel);
            w.WriteUInt16((ushort)Choices.Length); foreach (var choice in Choices) choice.Write(w);
            w.WriteUInt16((ushort)Shells.Length);
            foreach (var shell in Shells) { w.WriteUInt16(shell.Id); w.WriteUInt32(shell.Count); w.WriteBit(shell.Selected); }
        }
        public static bool TryRead(NetPakReader r, out EquipmentSlotOptions value)
        {
            value = null;
            if (!r.ReadUInt8(out byte slot) || slot > (byte)EquipmentSlot.Magazine
                || !r.ReadUInt8(out byte kind) || kind > (byte)EquipmentSlotKind.Shell
                || !r.ReadBit(out bool visible) || !r.ReadBit(out bool available) || !r.ReadBit(out bool detachable)
                || !r.ReadInt32(out int installed)) return false;
            var color = new float[4];
            for (int i = 0; i < color.Length; i++)
                if (!r.ReadFloat(out color[i]) || !float.IsFinite(color[i]) || color[i] < 0 || color[i] > 1) return false;
            if (!r.ReadUInt16(out ushort count) || count > EquipmentWire.MaxChoices) return false;
            var choices = new EquipmentChoice[count];
            for (int i = 0; i < count; i++) if (!EquipmentChoice.TryRead(r, out choices[i])) return false;
            if (!r.ReadUInt16(out ushort shellsCount) || shellsCount > EquipmentWire.MaxChoices) return false;
            var shells = new EquipmentShell[shellsCount];
            for (int i = 0; i < shellsCount; i++)
            {
                if (!r.ReadUInt16(out ushort id) || !r.ReadUInt32(out uint amount) || !r.ReadBit(out bool selected)) return false;
                shells[i] = new(id, amount, selected);
            }
            value = new() { Slot = (EquipmentSlot)slot, Kind = (EquipmentSlotKind)kind, Visible = visible, Available = available,
                Detachable = detachable, InstalledId = installed, Color = color, Choices = choices, Shells = shells };
            return true;
        }
    }

    public struct AttachmentOptionsEvent
    {
        public uint RequestId;
        public bool Supported, HasGun, Busy, CanRack;
        public ulong GunToken, SelectionVersion;
        public EquipmentAddress Gun;
        public string GunName;
        public EquipmentSlotOptions[] Slots;
        public void Write(NetPakWriter w)
        {
            w.WriteUInt32(RequestId); w.WriteBit(Supported); w.WriteBit(HasGun); w.WriteBit(Busy); w.WriteBit(CanRack);
            w.WriteUInt64(GunToken); w.WriteUInt64(SelectionVersion); Gun.Write(w); EquipmentWire.WriteText(w, GunName);
            int count = Slots?.Length ?? 0;
            if (count > 5) throw new ArgumentException("Equipment has only five slots");
            w.WriteUInt8((byte)count); if (Slots != null) foreach (var slot in Slots) slot.Write(w);
        }
        public static bool TryRead(NetPakReader r, out AttachmentOptionsEvent evt)
        {
            evt = default;
            if (!r.ReadUInt32(out uint id) || !r.ReadBit(out bool supported) || !r.ReadBit(out bool hasGun)
                || !r.ReadBit(out bool busy) || !r.ReadBit(out bool canRack) || !r.ReadUInt64(out ulong token)
                || !r.ReadUInt64(out ulong version) || !EquipmentAddress.TryRead(r, out var gun)
                || !EquipmentWire.ReadText(r, out string name) || !r.ReadUInt8(out byte count) || count > 5) return false;
            var slots = new EquipmentSlotOptions[count]; int seen = 0;
            for (int i = 0; i < count; i++)
            {
                if (!EquipmentSlotOptions.TryRead(r, out slots[i])) return false;
                int bit = 1 << (int)slots[i].Slot; if ((seen & bit) != 0) return false; seen |= bit;
            }
            if (hasGun && (token == 0 || gun.Id == 0)) return false;
            evt = new() { RequestId = id, Supported = supported, HasGun = hasGun, Busy = busy, CanRack = canRack,
                GunToken = token, SelectionVersion = version, Gun = gun, GunName = name, Slots = slots }; return true;
        }
    }

    public struct EquipmentFactEvent
    {
        public EquipmentFactKind Kind;
        public EquipmentAction Action;
        public EquipmentSlot Slot;
        public ushort ItemId, Ammo, BarrelId;
        public ulong SelectionVersion;
        public uint Tick;
        public float Seconds, Delay, Speed;
        public bool ShellByShell, MagazineAction, Unloading;
        public string Reason;
        public void Write(NetPakWriter w)
        {
            w.WriteUInt8((byte)Kind); w.WriteUInt8((byte)Action); w.WriteUInt8((byte)Slot);
            w.WriteUInt16(ItemId); w.WriteUInt16(Ammo); w.WriteUInt16(BarrelId); w.WriteUInt64(SelectionVersion); w.WriteUInt32(Tick);
            w.WriteFloat(Seconds); w.WriteFloat(Delay); w.WriteFloat(Speed);
            w.WriteBit(ShellByShell); w.WriteBit(MagazineAction); w.WriteBit(Unloading); EquipmentWire.WriteText(w, Reason);
        }
        public static bool TryRead(NetPakReader r, out EquipmentFactEvent evt)
        {
            evt = default;
            if (!r.ReadUInt8(out byte kind) || kind > (byte)EquipmentFactKind.BarrelBroken
                || !r.ReadUInt8(out byte action) || action > (byte)EquipmentAction.Firemode
                || !r.ReadUInt8(out byte slot) || slot > (byte)EquipmentSlot.Magazine
                || !r.ReadUInt16(out ushort item) || !r.ReadUInt16(out ushort ammo) || !r.ReadUInt16(out ushort barrel) || !r.ReadUInt64(out ulong version)
                || !r.ReadUInt32(out uint tick) || !r.ReadFloat(out float seconds) || !r.ReadFloat(out float delay)
                || !r.ReadFloat(out float speed) || !float.IsFinite(seconds) || seconds < 0 || !float.IsFinite(delay) || delay < 0
                || !float.IsFinite(speed) || speed < 0 || !r.ReadBit(out bool shells) || !r.ReadBit(out bool magazine)
                || !r.ReadBit(out bool unloading) || !EquipmentWire.ReadText(r, out string reason)) return false;
            evt = new() { Kind = (EquipmentFactKind)kind, Action = (EquipmentAction)action, Slot = (EquipmentSlot)slot,
                ItemId = item, Ammo = ammo, BarrelId = barrel, SelectionVersion = version, Tick = tick, Seconds = seconds, Delay = delay,
                Speed = speed, ShellByShell = shells, MagazineAction = magazine, Unloading = unloading, Reason = reason }; return true;
        }
    }

    static class EquipmentWire
    {
        public static readonly int MaxChoices = PlayerInventory.OWNPAGES * byte.MaxValue;
        // Ten length bits bound UTF-8 allocation before reading; the source
        // names/reasons then have their separate character bound checked.
        public const int MaxTextChars = 192;
        public static void WriteText(NetPakWriter w, string value)
        {
            value ??= "";
            if (value.Length > MaxTextChars) throw new ArgumentException("Equipment text exceeds its wire bound");
            w.WriteString(value, 10);
        }
        public static bool ReadText(NetPakReader r, out string value) => r.ReadString(out value, 10) && value.Length <= MaxTextChars;
    }
}
