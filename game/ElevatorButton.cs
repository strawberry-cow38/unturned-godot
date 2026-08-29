using Godot;

namespace UnturnedGodot
{
    /// <summary>A single floor-call button on the elevator's panel. Look at it + Interact (F) and it sends the car to
    /// its floor via Elevator.GoToFloor. Focus + white outline + F mirror the elevator/TV/lamp interactables. Master
    /// 2026-08-29: "make the button panel the interactable spot instead of the whole thing. add buttons that call the
    /// elevator to each floor". Parented to the elevator so the panel rides along with the car.</summary>
    public partial class ElevatorButton : StaticBody3D
    {
        public Elevator Lift; public int Floor;
        MeshInstance3D _mi;

        public static ElevatorButton Make(Elevator lift, int floor, Color face)
        {
            var b = new ElevatorButton { Lift = lift, Floor = floor };   // default layer bit1 -- same as the elevator body the look-ray already finds
            b._mi = new MeshInstance3D {
                Mesh = new BoxMesh { Size = new Vector3(0.34f, 0.34f, 0.07f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = face, Roughness = 0.4f,
                    EmissionEnabled = true, Emission = face, EmissionEnergyMultiplier = 0.9f },   // lit-button glow so the panel reads
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            b.AddChild(b._mi);
            b.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.42f, 0.42f, 0.18f) } });
            return b;
        }

        /// <summary>F pressed on this button -> send the car to this button's floor.</summary>
        public void Press() { if (Lift != null && IsInstanceValid(Lift)) Lift.GoToFloor(Floor); }

        public void SetLookFocused(bool on)
        {
            if (_mi == null || !IsInstanceValid(_mi)) return;
            _mi.Layers = on ? (_mi.Layers | OutlineOverlay.OutlineLayer) : (_mi.Layers & ~OutlineOverlay.OutlineLayer);
        }
    }
}
