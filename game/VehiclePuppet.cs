using Godot;

namespace UnturnedGodot
{
    // Client-side vehicle replica visual (MP_PLAN §3.6): a plain Node3D carrying the ripped body/parts/
    // wheel meshes -- NO VehicleBody3D, no wheels physics, no collision, no audio. VehicleReplicaView
    // dead-reckons its transform from snapshots; DressWheels applies the replicated steer angle plus a
    // spin derived from the replicated forward speed (rolling wheels need no spin on the wire).
    // Built by Vehicle.BuildPuppetByName so puppet and server node share the same Spec data.
    public partial class VehiclePuppet : Node3D, IPuppetFocusable, ITowNode
    {
        public sealed class WheelDress
        {
            public Node3D Pivot;
            public bool Steer;
            public float Radius;
            public float Spin;   // accumulated roll angle (radians)
        }

        public string SpecKey = "jeep";
        public WheelDress[] Wheels = System.Array.Empty<WheelDress>();

        // A6 rope tow: bumper-height rope attach points (front = -Z / the towed end, rear = +Z / the tower
        // end), set by BuildPuppetByName with the IDENTICAL formula the real Vehicle.Build uses so the
        // cosmetic client rope hangs off the same spots the host's physics rope does. VehicleReplicaView
        // draws a TowRope between the tower puppet's RearTowWorld and the towed puppet's FrontTowWorld.
        public Vector3 FrontTowLocal, RearTowLocal;
        public Vector3 FrontTowWorld => ToGlobal(FrontTowLocal);
        public Vector3 RearTowWorld => ToGlobal(RearTowLocal);

        // B11 ITowNode: a joined client scans PUPPETS (its real cars are RemoveFromGroup'd) and sends a tie/untie
        // intent by NetId -- the server owns the physics + validation. TowRoped/TowScannable stay loose here
        // (the puppet has no local rope/wreck state; the server re-validates not-already-roped + not-a-wreck at
        // the choke point). Nub visuals mirror the real Vehicle's (built lazily on first show, same 0.16m cubes).
        public uint TowNetId => NetId;
        public bool TowRoped => false;
        public bool Exploded;   // review #10: mirrored from the replicated entity by VehicleReplicaView each tick
        public bool TowScannable => !Exploded;   // exclude WRECKS, matching SP Vehicle.TowScannable = !Exploded (server re-validates, but the client must not highlight/attempt-tie a wreck)

        MeshInstance3D _towFrontNub, _towRearNub;
        static MeshInstance3D MakeTowNub(Color c, Vector3 pos)
        {
            var m = new StandardMaterial3D { AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel, Metallic = 0f, Roughness = 0.6f };
            return new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * 0.16f }, MaterialOverride = m, Position = pos, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        }
        public void SetTowNodesVisible(bool on)
        {
            if (on && _towFrontNub == null)   // build once, on the first show while a rope tool is out
            {
                _towFrontNub = MakeTowNub(new Color(0.30f, 0.85f, 1.0f), FrontTowLocal);   // front / towed end -> cyan
                _towRearNub = MakeTowNub(new Color(1.0f, 0.72f, 0.20f), RearTowLocal);     // rear / tower end -> amber
                AddChild(_towFrontNub); AddChild(_towRearNub);
            }
            if (_towFrontNub != null) _towFrontNub.Visible = on;
            if (_towRearNub != null) _towRearNub.Visible = on;
        }
        public void SetTowNubHighlighted(bool rear, bool on)
        {
            var nub = rear ? _towRearNub : _towFrontNub;
            if (nub != null && nub.MaterialOverride is StandardMaterial3D m) { m.EmissionEnabled = on; m.Emission = m.AlbedoColor; m.EmissionEnergyMultiplier = on ? 1.0f : 0f; }
        }

        // Interior steering wheel model (#38): the puppet analogue of the SP _steerPivot/_steerAxis
        // (Vehicle.cs Build) -- built by BuildPuppetByName from the spec's steer part / SteerModel and
        // rotated 1:1 with the replicated steer angle in DressWheels. Null when the spec has no steer
        // model (SteerAxis == Zero, e.g. the trailer).
        public Node3D SteerPivot;
        public Vector3 SteerAxis;

        // look-at focus (client-only): the same screen-space outline the real Vehicle draws -- add every mesh to
        // OutlineOverlay's layer so the offscreen mask cam picks them up as ONE silhouette. Detection is the bit-5
        // box collider added in Vehicle.BuildPuppetByName; PlayerController.UpdateLookFocus drives this on/off.
        public Color OutlineColor = new Color(0.82f, 0.83f, 0.90f);   // default vehicle tint; BuildPuppetByName overrides from spec rarity
        bool _lookFocused;
        System.Collections.Generic.List<MeshInstance3D> _outlineMeshes;
        Label3D _nameLabel;
        const float InfoH = 1.1f;   // name tag sits at cabin height (matches the real Vehicle's InfoBillboard)

        /// <summary>Attach the look-at name tag (hidden until focused). A billboard child at cabin height, so it
        /// follows the puppet as it's dead-reckoned (unlike the item tag, the car moves).</summary>
        public void SetNameLabel(string name, Color color)
        {
            _nameLabel = new Label3D
            {
                Text = string.IsNullOrEmpty(name) ? "?" : name,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = color.Lerp(Colors.White, 0.35f),
                PixelSize = 0.01f, NoDepthTest = true, FontSize = 64, OutlineSize = 10,
                Visible = false, Position = new Vector3(0, InfoH, 0),
            };
            AddChild(_nameLabel);
        }

        public void SetLookFocused(bool on)
        {
            if (_lookFocused == on) return;
            _lookFocused = on;
            if (on || _outlineMeshes == null)   // (re)collect on FOCUS so newly-dressed wheel meshes are covered
            {
                _outlineMeshes = new System.Collections.Generic.List<MeshInstance3D>();
                CollectMeshes(this, _outlineMeshes);
            }
            foreach (var mi in _outlineMeshes)
                if (IsInstanceValid(mi))
                    mi.Layers = on ? (mi.Layers | OutlineOverlay.OutlineLayer) : (mi.Layers & ~OutlineOverlay.OutlineLayer);
            if (on) WorldItem.FocusColor = OutlineColor;   // OutlineOverlay tints the rim with this
            if (_nameLabel != null && IsInstanceValid(_nameLabel)) _nameLabel.Visible = on;
        }

        static void CollectMeshes(Node n, System.Collections.Generic.List<MeshInstance3D> list)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is MeshInstance3D mi) list.Add(mi);   // body + wheels -> one combined silhouette
                CollectMeshes(c, list);
            }
        }

        // C6 ride mode (PEI_CLIENT_PLAN §3 C6): the puppet is the shell's ENTER TARGET and drive-cam anchor.
        public uint NetId;                    // the replicated vehicle entity id (set by VehicleReplicaView at spawn) -- what SendEnterVehicle takes
        public Vector3 DriverEyeLocal = new Vector3(-0.4f, 1.85f, 0.4f);   // FP ride-cam eye; same default + per-spec override as Vehicle
        public Vector3 SeatOffset;            // driver seat (prefab Seat_0) for the 3rd-person seated body pose

        float _meshSize;
        /// <summary>Bounding diagonal for the chase-cam auto-zoom (the Vehicle.WorldMeshAabb analogue).
        /// The Body mesh alone -- it spans the vehicle's footprint, and the cam distance clamps anyway.</summary>
        public float MeshSize
        {
            get
            {
                if (_meshSize <= 0f)
                    _meshSize = GetNodeOrNull<MeshInstance3D>("Body")?.GetAabb().Size.Length() ?? 6f;
                return _meshSize;
            }
        }

        /// <summary>Wheel dressing: front wheels yaw to the replicated steer, every wheel rolls at the
        /// rolling-contact rate for the replicated forward speed (forward = -Z -> negative roll about X,
        /// the VehicleWheel3D convention). The interior steering wheel turns with the same angle.</summary>
        public void DressWheels(float steerDegrees, float forwardSpeed, float dt)
        {
            float steerRad = Mathf.DegToRad(steerDegrees);
            foreach (var wd in Wheels)
            {
                if (wd.Pivot == null || !IsInstanceValid(wd.Pivot)) continue;
                wd.Spin -= (forwardSpeed / Mathf.Max(0.05f, wd.Radius)) * dt;
                var basis = new Basis(Vector3.Right, wd.Spin);
                if (wd.Steer) basis = new Basis(Vector3.Up, steerRad) * basis;
                wd.Pivot.Basis = basis;
            }
            if (SteerPivot != null && IsInstanceValid(SteerPivot) && SteerAxis != Vector3.Zero)
                SteerPivot.Basis = new Basis(SteerAxis, steerRad);   // the SP steering-wheel rotation (Vehicle.cs _steerPivot), fed by the wire's steer angle
        }
    }
}
