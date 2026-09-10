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

        public static Vector3 Anchor(ushort id, DeployableDef.SwitchRole role = DeployableDef.SwitchRole.None)
            => new(role == DeployableDef.SwitchRole.TurnOn ? -TriggerX :
                   role == DeployableDef.SwitchRole.TurnOff ? TriggerX : 0f,
                   id == 9115 ? ValveY : PoweredY, id == 9115 ? ValveZ : PoweredZ);
    }
}
