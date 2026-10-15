using System;

namespace UnturnedGodot.Net
{
    public sealed partial class NetWorldServer
    {
        public readonly OwnerEquipmentReplication Equipment = new();
        // Null-default adapters: core owns transport/admission/owner routing,
        // while a host with the source weapon service supplies the rules.
        public Action<ushort, SelectHeldCommand> SelectHeldRequested;
        public Action<ushort, WeaponIntentCommand> WeaponIntentRequested;
        public Func<ushort, uint, AttachmentOptionsEvent> AttachmentOptionsFor;

        void RegisterEquipmentCommands()
        {
            Commands.Register<SelectHeldCommand>(ReplicationIds.CommandSelectHeld, SelectHeldCommand.TryRead,
                (owner, cmd) => SelectHeldRequested(owner, cmd),
                (owner, unused) => SelectHeldRequested != null && Inventories.TryGet(owner, out _) && CombatState.TryGet(owner, out var life) && life.Alive);
            Commands.Register<WeaponIntentCommand>(ReplicationIds.CommandWeaponIntent, WeaponIntentCommand.TryRead,
                (owner, cmd) => WeaponIntentRequested(owner, cmd),
                (owner, unused) => WeaponIntentRequested != null && Inventories.TryGet(owner, out _) && CombatState.TryGet(owner, out var life) && life.Alive);
            Commands.Register<AttachmentOptionsCommand>(ReplicationIds.CommandAttachmentOptions, AttachmentOptionsCommand.TryRead,
                (owner, cmd) => {
                    var reply = AttachmentOptionsFor?.Invoke(owner, cmd.RequestId) ?? new AttachmentOptionsEvent { RequestId = cmd.RequestId };
                    SendEventTo(owner, NetMessagePak.Pack(ReplicationIds.EventAttachmentOptions, reply.Write));
                }, (owner, unused) => Inventories.TryGet(owner, out _));
        }
    }

    public sealed partial class NetWorldClient
    {
        public readonly OwnerEquipmentReplication Equipment = new();
        public event Action<AttachmentOptionsEvent> AttachmentOptionsReceived;
        public event Action<EquipmentFactEvent> EquipmentFactReceived;
        void RegisterEquipmentEvents()
        {
            Events.Register<AttachmentOptionsEvent>(ReplicationIds.EventAttachmentOptions, AttachmentOptionsEvent.TryRead,
                value => AttachmentOptionsReceived?.Invoke(value));
            Events.Register<EquipmentFactEvent>(ReplicationIds.EventEquipmentFact, EquipmentFactEvent.TryRead,
                value => EquipmentFactReceived?.Invoke(value));
        }
        public bool SendSelectHeld(byte page, byte x, byte y, ushort itemId, ulong token, bool equip = false) =>
            SendCommand(ReplicationIds.CommandSelectHeld, new SelectHeldCommand { Address = new(page, x, y, itemId), Token = token, Equip = equip }.Write);
        public bool SendWeaponIntent(WeaponIntentCommand command) => SendCommand(ReplicationIds.CommandWeaponIntent, command.Write);
        public bool SendAttachmentOptions(uint requestId) => SendCommand(ReplicationIds.CommandAttachmentOptions,
            new AttachmentOptionsCommand { RequestId = requestId }.Write);
    }
}
