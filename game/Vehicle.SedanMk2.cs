using Godot;
using System;

namespace UnturnedGodot
{
    public partial class Vehicle
    {
        // Partial declaration ordering is unspecified. Access _sedan only when the Lazy is evaluated,
        // after all Vehicle static initializers have completed. Spec is a value type: copy, not alias.
        static readonly Lazy<Spec> _sedanMk2 = new(CreateSedanMk2Spec);
        static Spec SedanMk2Spec => _sedanMk2.Value;
        public static Vehicle BuildSedanMk2(int variant = 0) => Build(SedanMk2Spec, variant, "sedan_mk2");

        static Vector3 Mk2BodyPoint(Vector3 old) => old * 1.06f + new Vector3(0f, 0.021f, 0f);
        // V14 greenhouse-only fallback slab; the lower-body hull and handling stay V13.
        // Normal runtime replaces fitted boxes with frame hulls / HitMesh. Also preserves HasCabin.
        static (Vector3 size, Vector3 center) SedanMk2RoofBox =>
            (new Vector3(2.65f, .11f, 2.4592f), new Vector3(0f, 1.8556869f, .2067f));

        static Spec CreateSedanMk2Spec()
        {
            var s = _sedan; // Retain ALL handling, drivetrain, suspension, sound, paint, fuel and health.
            s.Name = "Sedan Mk II";
            s.Body = "sedan_mk2_frame.txt";
            s.Palette = "sedan_mk2_palette.png";
            s.GlassMesh = "sedan_mk2_glass.txt"; // prefix for six independent panes; no aggregate required
            // Stock 0.6 m tyre, its mesh and texture are intentionally not scaled/copied.
            s.BoxSize = _sedan.BoxSize * 1.06f;
            s.BoxCenter = Mk2BodyPoint(_sedan.BoxCenter);
            s.FifthWheel = Mk2BodyPoint(_sedan.FifthWheel);
            s.SpotPos = Array.ConvertAll(_sedan.SpotPos, Mk2BodyPoint);
            s.OmniPos = Mk2BodyPoint(_sedan.OmniPos);
            s.TailPos = Array.ConvertAll(_sedan.TailPos, Mk2BodyPoint);
            // Original duct outlet, not the stock fallback (which sits above the actual pipe).
            s.ExhaustPos = Mk2BodyPoint(new Vector3(0.7937f, -0.1747f, 2.8269f));
            s.SteerPivot = Mk2BodyPoint(_sedan.SteerPivot) + new Vector3(0f, 0f, 0.50f);
            var oldSeats = SeatTable["sedan"];
            s.Seats = new Vector3[oldSeats.Length];
            for (int i = 0; i < oldSeats.Length; i++)
            {
                var seat = Mk2BodyPoint(oldSeats[i]);
                if (i >= 2) seat.X *= 0.82f; // Narrowed clean rear bench clears the full housing backs.
                s.Seats[i] = seat + new Vector3(0f, 0f, i < 2 ? 0.30f : 0.14f);
            }
            // Preserve sedan's tuned visible-body rise (not the generic 8 cm); origins remain prefab based.
            s.SeatBodyRise = (SeatOf("Sedan").Y - oldSeats[0].Y) * 1.06f;
            // Settled Idle_Drive eye: SeatBodyY (-.0214) + SkullY (1.00716505) + .31
            // = 1.29576505. Express the fallback relative to the explicit driver seat.
            // Do not preserve the default eye Z=+0.4 (over a metre behind the original driver origin).
            s.DriverEye = s.Seats[0] + new Vector3(0f, 1.358505f, 0.059f * 1.06f);
            // Same mount Y as the original sedan: minus the .25 m rest drop gives art centre Y=0.
            s.Wheels = new (float, float, float, bool)[] {
                (-1.09f, 0.25f, -1.9292f, true), (1.09f, 0.25f, -1.9292f, true),
                (-1.09f, 0.25f, 1.949f, false), (1.09f, 0.25f, 1.949f, false) };
            // Fixed solid-colour meshes only. The column filename must NOT contain "steer".
            s.Parts = new (string, Color)[] {
                ("sedan_mk2_stock_front_bumper.txt", new Color(82/255f, 82/255f, 82/255f)),
                ("sedan_mk2_stock_rear_bumper.txt", new Color(82/255f, 82/255f, 82/255f)),
                ("sedan_mk2_stock_exhaust.txt", new Color(97/255f, 96/255f, 96/255f)),
                ("sedan_mk2_stock_hitch.txt", new Color(151/255f, 158/255f, 158/255f)),
                ("sedan_mk2_stock_seats.txt", new Color(64/255f, 64/255f, 64/255f)),
                ("sedan_mk2_stock_steer.txt", new Color(71/255f, 59/255f, 36/255f)),
                ("sedan_mk2_stock_headlights.txt", new Color(239/255f, 227/255f, 186/255f)),
                ("sedan_mk2_stock_taillights.txt", new Color(142/255f, 32/255f, 32/255f)),
                ("sedan_mk2_dashboard.txt", new Color(57/255f, 57/255f, 57/255f)),
                ("sedan_mk2_column_support.txt", new Color(66/255f, 66/255f, 66/255f)) };
            s.PaintedParts = new[] { "sedan_mk2_cabin_floor.txt", "sedan_mk2_floor_tunnel.txt",
                "sedan_mk2_engine_bay_liner.txt", "sedan_mk2_trunk_liner.txt" };
            // Deliberately reordered from manifest: indices are seats, NOT manifest list order.
            s.AuthoredPanels = new[] {
                new AuthoredPanelDef("sedan_mk2_front_door_left.txt", 0, 0, new Vector3(-1.3091f, 1.0598f, -1.39178f), Vector3.Up, -55f, "l_front"),
                new AuthoredPanelDef("sedan_mk2_front_door_right.txt", 1, 1, new Vector3(1.3091f, 1.0598f, -1.39178f), Vector3.Up, 55f, "r_front"),
                new AuthoredPanelDef("sedan_mk2_rear_door_left.txt", 2, 2, new Vector3(-1.3091f, 1.0598f, 0.2279f), Vector3.Up, -55f, "l_rear"),
                new AuthoredPanelDef("sedan_mk2_rear_door_right.txt", 3, 3, new Vector3(1.3091f, 1.0598f, 0.2279f), Vector3.Up, 55f, "r_rear"),
                new AuthoredPanelDef("sedan_mk2_hood.txt", 4, -1, new Vector3(0f, 1.209352463f, -1.5953f), Vector3.Right, 50f),
                new AuthoredPanelDef("sedan_mk2_trunk_lid.txt", 5, -2, new Vector3(0f, 1.204066608f, 1.9451f), Vector3.Right, -50f) };
            return s;
        }
    }
}
