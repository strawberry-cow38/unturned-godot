using System;

namespace UnturnedGodot.Net
{
    public sealed partial class NetWorldServer
    {
        public readonly VehicleMountReplication VehicleMounts = new();
        public Func<ushort, MountedInputCommand, bool> MountedInputAllowed;
        public Action<ushort, MountedInputCommand> MountedInputRequested;
        void RegisterMountedCommands() => Commands.Register<MountedInputCommand>(ReplicationIds.CommandMountedInput,
            MountedInputCommand.TryRead, (owner, cmd) => MountedInputRequested(owner, cmd),
            (owner, cmd) => MountedInputRequested != null && CombatState.IsAlive(owner)
                && MountedInputAllowed?.Invoke(owner, cmd) == true);
    }
    public sealed partial class NetWorldClient
    {
        public readonly VehicleMountReplication VehicleMounts = new();
        public event Action<MountedShotEvent> MountedShot;
        ushort _mountedSequence;
        void RegisterMountedEvents() => Events.Register<MountedShotEvent>(ReplicationIds.EventMountedShot,
            MountedShotEvent.TryRead, value => MountedShot?.Invoke(value));
        public bool SendMountedInput(MountedInputCommand command)
        {
            if (State != NetSessionState.Connected || !command.IsValid) return false;
            command.Sequence = ++_mountedSequence;
            Session.SendUnreliableSequenced(NetMessagePak.Pack(ReplicationIds.CommandMountedInput, command.Write));
            return true;
        }
    }
}
