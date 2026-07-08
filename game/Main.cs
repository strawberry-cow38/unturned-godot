using Godot;
using SDG.NetPak;
using SDG.Unturned;   // UnturnedDat (DatParser etc.)

namespace UnturnedGodot
{
    // Phase-0 smoke + GATE. Smoke: the three ported engine-agnostic libs load + run INSIDE Godot and
    // the SDG.Compat<->Godot adapter round-trips. GATE: a REAL ripped Unturned prop instantiates in a
    // Godot scene resolved through ContentProvider BY ITS ORIGINAL UNITY GUID.
    public partial class Main : Node
    {
        // Aprix_Mask_0 -- ripped from core.masterbundle; guid from its AssetRipper .meta.
        const string GateGuid = "fb9428c7b8df82e4eb9642dacfaf9567";

        public override void _Ready()
        {
            // 1) NetPak (netcode) runs in-engine.
            var w = new NetPakWriter { buffer = new byte[64] };
            w.Reset(); w.WriteBits(0xABCu, 12); w.Flush();
            var r = new NetPakReader();
            r.SetBuffer(w.buffer); r.ReadBits(12, out uint got);

            // 2) UnturnedDat (data/mod layer) parses a .dat in-engine.
            var dict = new DatParser().Parse("Health 55\nName Test_Item");

            // 3) Unity<->Godot adapter round-trips a vector.
            var v = new UnityEngine.Vector3(1f, 2f, 3f);
            Godot.Vector3 gv = v.ToGodot();

            GD.Print($"[UnturnedGodot] core live in Godot {Engine.GetVersionInfo()["string"]}: " +
                     $"NetPak 0x{got:X}==0xABC:{got == 0xABCu} | Dat keys={dict.Count} hasHealth={dict.ContainsKey("Health")} | " +
                     $"adapter {v}->{gv}");

            // 4) THE GATE: resolve a real ripped prop by its original Unity GUID and put it in the scene.
            var content = new ContentProvider();
            AddChild(content);
            content.LoadManifest();
            var mesh = content.LoadMesh(GateGuid);
            if (mesh == null)
            {
                GD.PrintErr($"[GATE] FAILED: could not resolve GUID {GateGuid}");
            }
            else
            {
                var inst = new MeshInstance3D { Mesh = mesh };
                AddChild(inst);
                var aabb = mesh.GetAabb();
                int vcount = 0;
                var arrays = mesh.SurfaceGetArrays(0);
                if (arrays.Count > 0 && arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.Nil)
                    vcount = ((Vector3[])arrays[(int)Mesh.ArrayType.Vertex]).Length;
                GD.Print($"[GATE] PASS: ContentProvider({content.Count} guid) -> mesh by GUID {GateGuid[..8]}.. " +
                         $"instantiated as MeshInstance3D. verts={vcount} aabb.size=({aabb.Size.X:F3},{aabb.Size.Y:F3},{aabb.Size.Z:F3}) " +
                         $"scene-children={GetChildCount()}");
            }
            GetTree().Quit();
        }
    }
}
