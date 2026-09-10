using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Vehicle spraypaint (master 2026-09-10: "do vehicle spraypaint").
    ///
    /// The paint SYSTEM already existed -- vehicle_paint.gdshader tints a body palette's paintable texels and
    /// Vehicle.SpawnPaint ports getDefaultPaintColor -- so what landed was the 32 CANS, the largest item type
    /// the port had no concept of. Everything outside the 22-value EItemType falls to GENERIC, so all 32
    /// equipped as nothing.
    ///
    /// Checks are against the retail hexes rather than against the loader agreeing with itself, and the
    /// distinctness check is the one that matters: a hex parse that silently failed would give 32 cans one
    /// colour and every other assertion here would still pass.</summary>
    public sealed class VehiclePaintTests : GameTest
    {
        public override string Name => "vehicle.spraypaint";
        public override double TimeoutSimSeconds => 20;

        static string ReadSrc(string rel)
        {
            foreach (var c in new[] { "res://" + rel, "res://game/" + rel })
            {
                string p = ProjectSettings.GlobalizePath(c);
                if (System.IO.File.Exists(p)) return System.IO.File.ReadAllText(p);
            }
            return "";
        }

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);

            T.Check($"all 32 retail spraypaints load ({VehiclePaints.Count})", VehiclePaints.Count == 32);

            // ---- THE COLOURS ARE THE RETAIL ONES, off each can's own .dat PaintColor.
            var want = new (ushort Id, string Hex)[]
            {
                (1840, "0a0a0a"),   // Midnight Black
                (1841, "e6e6e6"),   // Brilliant White
                (1842, "25c891"),   // Turquoise
                (1845, "ec2a20"),   // Hotrod Red
                (1875, "437c44"),   // Forest Military -- the hex PaintMat's sRGB note is written about
            };
            foreach (var (id, hex) in want)
            {
                var c = VehiclePaints.For(id);
                T.Check($"{id} is a spraypaint", c.HasValue);
                if (c.HasValue)
                    T.Check($"{id} = #{hex} (got {c.Value.ToHtml(false)})",
                            c.Value.ToHtml(false).ToLowerInvariant() == hex);
            }

            // ---- AND THEY ARE 32 DIFFERENT COLOURS. A hex parse that failed would hand every can the same
            // value and leave every check above still passing on the handful it got right.
            var seen = new HashSet<string>();
            for (ushort id = 1840; id <= 1877; id++)
            {
                var c = VehiclePaints.For(id);
                if (c.HasValue) seen.Add(c.Value.ToHtml(false).ToLowerInvariant());
            }
            T.Check($"the cans are {seen.Count} distinct colours", seen.Count == 32);

            // ---- A CAN IS A CAN AND NOTHING ELSE IS.
            T.Check("beans are not a spraypaint", !VehiclePaints.Is(13));
            T.Check("a gas can is not a spraypaint", !VehiclePaints.Is(28));
            T.Check("id 0 is not a spraypaint", !VehiclePaints.Is(0));
            T.Check("an unknown id has no colour", VehiclePaints.For(65535) == null);

            // ---- THE HELD ITEM CANNOT LEAK INTO THE NEXT THING YOU HOLD. Every Equip* clears the other held
            // items by hand; miss one and you are spraying cars while holding a rifle. Counted at the source
            // because no runtime assertion short of equipping all eleven can see a site that was forgotten,
            // and this is exactly the shape that left ClearHeldOptic() unrun three times in this file.
            string pc = ReadSrc("PlayerController.cs");
            if (pc.Length > 0)
            {
                int fuel = 0, paint = 0, idx = 0;
                while ((idx = pc.IndexOf("_heldFuelItem = null;", idx, System.StringComparison.Ordinal)) >= 0) { fuel++; idx++; }
                idx = 0;
                while ((idx = pc.IndexOf("_heldPaintItem = null;", idx, System.StringComparison.Ordinal)) >= 0) { paint++; idx++; }
                T.Check($"every site that clears the gas can also clears the spraypaint ({paint} vs {fuel})", paint >= fuel);
                T.Check("the spraypaint has an equip path", pc.Contains("EquipHeldSpraypaint"));
                T.Check("...reached from the equip dispatcher", pc.Contains("VehiclePaints.Is(asset.id)"));
                T.Check("...and LMB sprays the vehicle you are aimed at", pc.Contains("TrySprayVehicle()"));
            }

            // ---- THE VEHICLE SIDE EXISTS AND IS NOT A NO-OP. SetPaint must write the shader parameter the
            // paint shader actually reads; a setter that only stores the colour would repaint nothing.
            string vs = ReadSrc("Vehicle.cs");
            if (vs.Length > 0)
            {
                T.Check("Vehicle.SetPaint exists", vs.Contains("public void SetPaint(Color c)"));
                T.Check("...and writes the shader's paint_color", vs.Contains("sm.SetShaderParameter(\"paint_color\""));
                T.Check("...converting sRGB to linear, as PaintMat does", vs.Contains("c.SrgbToLinear()"));
            }
        }
    }
}
