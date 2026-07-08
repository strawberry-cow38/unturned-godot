using Godot;
using SDG.NetPak;
using SDG.Unturned;   // UnturnedDat (DatParser etc.)

namespace UnturnedGodot
{
    // Phase-0b smoke node: proves the three ported engine-agnostic libs load + run INSIDE Godot,
    // and the SDG.Compat<->Godot adapter round-trips. Not gameplay — the "the core lives in the engine" proof.
    public partial class Main : Node
    {
        public override void _Ready()
        {
            // 1) NetPak (netcode) runs in-engine: write bits, read them back (real U3-SDK API).
            var w = new NetPakWriter { buffer = new byte[64] };
            w.Reset(); w.WriteBits(0xABCu, 12); w.Flush();
            var r = new NetPakReader();
            r.SetBuffer(w.buffer); r.ReadBits(12, out uint got);

            // 2) UnturnedDat (data/mod layer) parses a .dat in-engine.
            var dict = new DatParser().Parse("Health 55\nName Test_Item");

            // 3) The Unity<->Godot adapter round-trips a vector.
            var v = new UnityEngine.Vector3(1f, 2f, 3f);
            Godot.Vector3 gv = v.ToGodot();

            GD.Print($"[UnturnedGodot] core live in Godot {Engine.GetVersionInfo()["string"]}: " +
                     $"NetPak 0x{got:X}==0xABC:{got == 0xABCu} | Dat keys={dict.Count} hasHealth={dict.ContainsKey("Health")} | " +
                     $"adapter {v}->{gv}");
            GetTree().Quit();
        }
    }
}
