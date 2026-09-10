using Godot;

namespace UnturnedGodot
{
    // One front-facing row: ON, power, OFF. The authoring tool reads these constants
    // too, so the panel follows the wire anchors. Unused positions are blank covers.
    public static class FluidElectricalPanel
    {
        public const float TriggerX = 0.32f;
        public const float PoweredY = 1.25f;
        public const float PoweredZ = 0.42f;
        public const float ValveY = 1.2f;
        public const float ValveZ = 0f;
        public const float PumpX = -0.48f;
        public const float PumpY = 1.25f;
        public const float PumpZ = -0.70f;

        public static Vector3 Mount(ushort id)
            => id == 9114 ? new(PumpX, PumpY, PumpZ) :
               id == 9115 ? new(0f, ValveY, ValveZ) : new(0f, PoweredY, PoweredZ);

        public static Vector3 Anchor(ushort id, DeployableDef.SwitchRole role = DeployableDef.SwitchRole.None)
            => Mount(id) + new Vector3(role == DeployableDef.SwitchRole.TurnOn ? -TriggerX :
                   role == DeployableDef.SwitchRole.TurnOff ? TriggerX : 0f, 0f, 0f);
    }
}
