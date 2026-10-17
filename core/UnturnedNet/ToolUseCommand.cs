using SDG.NetPak;

namespace UnturnedGodot.Net
{
    /// <summary>v80 Command101 (row 15A): hold or release the held repair tool. The source shell steps its
    /// blowtorch locally against its own look ray; the host already does that per owner
    /// (bridge BodyCombatServices.ToolUse/StepTool), so the wire carries only the intent. The tool, the
    /// target and the repair rate are all resolved on the host from the SENDER's accepted held item and
    /// pose -- never from this payload. One bit, padded to a byte by the message framing.</summary>
    public struct ToolUseCommand
    {
        public bool Active;
        public void Write(NetPakWriter w) => w.WriteBit(Active);
        public static bool TryRead(NetPakReader r, out ToolUseCommand cmd)
        {
            cmd = default;
            if (!r.ReadBit(out bool active)) return false;
            cmd = new ToolUseCommand { Active = active };
            return true;
        }
    }

    public sealed partial class NetWorldClient
    {
        public bool SendToolUse(bool active) =>
            SendCommand(ReplicationIds.CommandToolUse, new ToolUseCommand { Active = active }.Write);
    }
}
