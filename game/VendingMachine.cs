using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>A drinks machine you can actually buy from (strawberry 2026-09-15: "add interaction to vending
    /// machines. power io input/globalpower, with at least 1 dollar in your inventory, consume the dollar, shake
    /// the machine a bit and then spawn a soda (blue machine) or a cola (red machine) in front of them").
    ///
    /// Built on the RadioDevice shape -- a world prop that becomes an IPowerDevice, tagged on its collider so the
    /// look-ray finds it, hub-ticked rather than _Process'd.
    ///
    /// ⭐ WHICH DRINK COMES OUT IS READ OFF THE PROP'S OWN TEXTURE, not guessed from its number. Vendor_0's 2x2
    /// palette is #802020 (red) and Vendor_1's is #204080 (blue), so red is the cola machine and blue is the soda
    /// machine -- which is the way round master described them, but the numbering gives no hint either way and a
    /// coin-flip here would have been wrong half the time and unnoticeable until someone bought one.</summary>
    public partial class VendingMachine : Node3D, IPowerDevice
    {
        public const ushort ColaId = 80;    // Canned Cola  -- Vendor_0, red
        public const ushort SodaId = 465;   // Canned Soda  -- Vendor_1, blue
        public const int Price = 1;         // dollars; Currency.StackId's amount IS dollars
        const float ShakeSeconds = 0.45f;
        const float VendWatts = 40f;        // a refrigerated drinks machine; a radio draws 12, a TV 55-120

        /// <summary>Collider meta carrying the device, so a look-ray landing on a Vendor body finds it.
        /// Mirrors <see cref="RadioDevice.HitMeta"/>; WorldBuilder stamps it on the body.</summary>
        public static readonly StringName HitMeta = "vendingmachine";

        public static bool IsVendorProp(string name) => name == "Vendor_0" || name == "Vendor_1";

        public string PropName { get; private set; }
        /// <summary>Red machine pours cola, blue pours soda. See the class note for where the colours came from.</summary>
        public ushort DrinkId => PropName == "Vendor_1" ? SodaId : ColaId;

        readonly List<ConnectionPort> _ports = new();
        ConnectionPort _plug;
        MeshInstance3D _outline;
        Vector3 _bodyCenterLocal;
        Aabb _bodyLocal;
        MeshInstance3D _bodyMi;    // the placed prop mesh; lives under the world root, not under this node
        Vector3 _restPos, _bodyRestPos;
        float _shake;              // seconds of wobble left
        float _cooldown;           // one can at a time -- seconds until the machine will serve again
        ushort _pendingDrink;      // paid for, not yet dropped -- the can waits for the shake to finish
        PlayerController _pendingFor;
        AudioStreamPlayer3D _clunk;

        public IReadOnlyList<ConnectionPort> PowerPorts => _ports;
        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => 0;   // SP-local map fixture, as RadioDevice/TVDevice
        public bool PlugPowered => _plug != null && GodotObject.IsInstanceValid(_plug) && _plug.Powered;

        /// <summary>Wired feed OR the mains. "power io input/globalpower" in the ask -- a machine on the street grid
        /// works without anybody running a cable to it, and a wired one works in a blackout.</summary>
        public bool HasFeed => PowerNet.MainsLive || PlugPowered;

        public static VendingMachine Make(MeshInstance3D bodyMi, string propName)
        {
            if (bodyMi == null || !GodotObject.IsInstanceValid(bodyMi)) return null;
            var v = new VendingMachine { Name = "VendingMachine", PropName = propName, Transform = bodyMi.Transform };
            v._bodyMi = bodyMi;   // ⚠ the PROP MESH, which is NOT a child of this node -- see HubTick
            v.Build(bodyMi.Mesh);
            return v;
        }

        void Build(Mesh body)
        {
            if (body == null) { Log.Err($"[vending] {PropName}: no body mesh"); return; }
            _bodyLocal = body.GetAabb();
            _bodyCenterLocal = _bodyLocal.Position + _bodyLocal.Size * 0.5f;
            _outline = OutlineOverlay.MakeOutline(body);
            if (_outline != null) AddChild(_outline);
            var snd = PlayerController.LoadWavOneShot("res://content/sounds/inv_heavy_01.wav");
            if (snd != null)
            {
                _clunk = new AudioStreamPlayer3D { Stream = snd, VolumeDb = Mathf.LinearToDb(0.9f), UnitSize = 3f, MaxDistance = 20f, Position = _bodyCenterLocal };
                AddChild(_clunk);
            }
            BuildPlug(_bodyLocal);
        }

        void BuildPlug(Aabb bodyLocal)
        {
            if (_plug != null) return;
            // Off the back face, low -- a drinks machine is plugged in behind itself. Derived from the body's own
            // bounds rather than a constant, since Vendor_0 and Vendor_1 need not be the same size.
            var pos = new Vector3(_bodyCenterLocal.X,
                                  bodyLocal.Position.Y + bodyLocal.Size.Y * 0.12f,
                                  bodyLocal.Position.Z);
            _plug = ConnectionPort.Create(this, new DeployableDef.Port
            {
                Kind = DeployableDef.PortKind.Consumer,
                Pos = pos,
                Watts = VendWatts,
            }, "Vending Machine");
            AddChild(_plug);
            _ports.Add(_plug);
            PowerNet.MarkDirty();
        }

        /// <summary>Why a press did nothing, or null when it would work. Returned as TEXT because every one of
        /// these is something the player can fix, and a machine that just ignores you reads as broken -- the
        /// same argument the connect-reject reasons make.</summary>
        public string RefusalFor(PlayerController p)
        {
            if (_cooldown > 0f) return "Dispensing...";   // one can at a time (strawberry 2026-09-15)
            if (!HasFeed) return "No power";
            if (p?.Inventory == null) return "No power";
            return p.Inventory.getItemCount(Currency.StackId) < Price ? $"Need ${Price}" : null;
        }

        /// <summary>Where the can comes out: the machine's own dispensing tray, in MESH-LOCAL coordinates and
        /// then through its transform (strawberry 2026-09-15: "should spawn relative to the machine, not the
        /// player"). It used to drop in front of the BUYER, which followed you around the machine.
        ///
        /// ⭐ WHICH WAY IS FRONT WAS MEASURED, not assumed. Vendor_0 is 1.6 x 1.145 x 2.5, authored +Z up, so
        /// depth is Y -- and the +Y face carries 124 of its 212 vertices against the far face's 16. That is the
        /// window, the frame and the buttons; a flat back panel needs almost none. It also agrees with the screen
        /// convention TVDevice derives for its cabinets, which is two independent reasons rather than a guess.</summary>
        Vector3 DispensePoint()
        {
            var local = new Vector3(_bodyLocal.Position.X + _bodyLocal.Size.X * 0.5f,   // centred across the front
                                    _bodyLocal.Position.Y + _bodyLocal.Size.Y + 0.35f,  // ...and clear of it
                                    _bodyLocal.Position.Z + 0.30f);                     // tray height, not the roof
            return GlobalTransform * local;
        }

        /// <summary>Buy one: the coin now, the can when it has finished shaking.</summary>
        public bool Vend(PlayerController p)
        {
            if (p == null || RefusalFor(p) != null) return false;
            if (!p.RequestVendPay()) return false;   // no coin taken -> no shake, no can

            _pendingFor = p;
            _pendingDrink = DrinkId;
            _shake = ShakeSeconds;
            _cooldown = ShakeSeconds + 0.55f;   // the wobble, then a beat before it will take another dollar
            _clunk?.Play();
            return true;
        }

        public void SetLookFocused(bool on) => OutlineOverlay.ShowOutline(on, Colors.White, _outline);

        public override void _EnterTree() { TickHub.Add(this, HubTick, 30f); }
        public override void _ExitTree() { TickHub.Remove(this); }

        public override void _Ready()
        {
            _restPos = Position;
            if (_bodyMi != null && GodotObject.IsInstanceValid(_bodyMi)) _bodyRestPos = _bodyMi.Position;
            AddToGroup("deployables");   // PowerNet gathers this group by IPowerDevice
            if (GetTree() is SceneTree tr && tr.GetNodesInGroup("powermgr").Count == 0)
            {
                var pm = new PowerManager();
                AddChild(pm);
                pm.AddToGroup("powermgr");
            }
            PowerNet.MarkDirty();
        }

        /// <summary>The wobble. A decaying side-to-side shudder off the rest position, so the machine settles back
        /// exactly where it started rather than walking across the pavement one purchase at a time.</summary>
        void HubTick(double dt)
        {
            if (_cooldown > 0f) _cooldown = Mathf.Max(0f, _cooldown - (float)dt);
            if (_shake <= 0f) return;   // NOTE: the drop below runs on the tick the shake REACHES zero, not after
            _shake = Mathf.Max(0f, _shake - (float)dt);
            float amp = 0.018f * (_shake / ShakeSeconds);           // decays to nothing
            float ph = _shake * 46f;                                // ~7 shudders over the window
            var off = new Vector3(Mathf.Sin(ph) * amp, 0f, Mathf.Cos(ph * 0.7f) * amp * 0.5f);

            // ⚠ THE PROP MESH IS NOT A CHILD OF THIS NODE. WorldBuilder adds the mesh under the world root and
            // this device beside it wearing the same transform, so moving `Position` moved only what IS parented
            // here -- the look-outline. Master saw the outline shudder while the machine stood perfectly still.
            // Both are moved, off their own remembered rest poses.
            Position = _restPos + off;
            if (_bodyMi != null && GodotObject.IsInstanceValid(_bodyMi)) _bodyMi.Position = _bodyRestPos + off;
            if (_shake <= 0f)   // land exactly home, never a drifted approximation
            {
                Position = _restPos;
                if (_bodyMi != null && GodotObject.IsInstanceValid(_bodyMi)) _bodyMi.Position = _bodyRestPos;

                // THE CAN, now the machine has finished rattling. Cleared before the call, so a drop that throws
                // or is refused cannot leave a pending can to fall out again on the next tick.
                if (_pendingDrink != 0)
                {
                    ushort drink = _pendingDrink; var buyer = _pendingFor;
                    _pendingDrink = 0; _pendingFor = null;
                    if (buyer != null && GodotObject.IsInstanceValid(buyer)) buyer.RequestVendDrop(drink, DispensePoint());
                }
            }
        }
    }
}
