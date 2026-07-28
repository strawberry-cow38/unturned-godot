using Godot;
using System.Collections.Generic;
using SDG.Unturned;

namespace UnturnedGodot
{
    /// <summary>
    /// A sign a player can write on, and everyone else can read.
    ///
    /// The text is the only player-authored content this game shows other players verbatim, so the
    /// node deliberately owns none of the rules about what may be written -- SignText does, engine-free,
    /// and this class simply routes through it. Setting text locally still sanitises, so a listen-server
    /// host cannot store something a dedicated server would have rejected.
    ///
    /// A StaticBody3D like Door, so the look-at ray finds it the same way and it can be shot.
    /// </summary>
    public partial class Sign : StaticBody3D
    {
        /// <summary>Signs are barricades: destructible, like doors. A sign nobody can take down is a
        /// permanent billboard on someone else's base.</summary>
        public float Health = 90f, HealthMax = 90f;

        public string Text { get; private set; } = string.Empty;
        public ulong Owner { get; private set; }

        Label3D _label;
        Vector3 _size = new Vector3(1.2f, 0.6f, 0.06f);

        // --- net identity: same registry shape as Door, so the wire can name a sign ---------------

        uint _netId;
        static readonly Dictionary<uint, Sign> _byNetId = new();

        public uint NetId
        {
            get => _netId;
            set
            {
                if (_netId != 0) _byNetId.Remove(_netId);
                _netId = value;
                if (value != 0) _byNetId[value] = this;
            }
        }

        public static bool TryGetByNetId(uint netId, out Sign sign)
        {
            if (_byNetId.TryGetValue(netId, out sign) && IsInstanceValid(sign)) return true;
            _byNetId.Remove(netId);      // the node died without clearing its id
            sign = null;
            return false;
        }

        public override void _ExitTree() { if (_netId != 0) _byNetId.Remove(_netId); }

        /// <summary>Every live sign, for the server publisher to walk.</summary>
        public static IEnumerable<Sign> All
        {
            get
            {
                foreach (var kv in _byNetId)
                    if (IsInstanceValid(kv.Value)) yield return kv.Value;
            }
        }

        // --- construction --------------------------------------------------------------------------

        public static Sign Spawn(Node parent, Vector3 basePos, float yawDeg, ulong owner, string text = "")
        {
            var s = new Sign { Owner = owner };
            parent.AddChild(s);
            s.GlobalPosition = basePos;
            s.RotationDegrees = new Vector3(0f, yawDeg, 0f);
            s.Build();
            s.SetTextLocal(text);
            return s;
        }

        void Build()
        {
            AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = _size } });
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = _size },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.31f, 0.20f) },
            });

            // Drawn just proud of the board's front face so it never z-fights with it.
            _label = new Label3D
            {
                Position = new Vector3(0f, 0f, _size.Z * 0.5f + 0.01f),
                PixelSize = 0.0035f,
                FontSize = 96,
                Modulate = new Color(0.05f, 0.04f, 0.03f),
                OutlineSize = 0,
                Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // No shading and no depth-test trickery: a sign should read as paint on the board, and
                // should NOT be visible through walls (which a no-depth-test label would be).
                Shaded = false,
                DoubleSided = false,
            };
            AddChild(_label);
        }

        // --- text ----------------------------------------------------------------------------------

        /// <summary>Set the text on THIS machine. Sanitises on the way in, because the stored string is
        /// what replicates and persists -- cleaning at draw time would leave the dirty value on the
        /// wire and in the save. Returns what was actually stored.</summary>
        public string SetTextLocal(string raw)
        {
            Text = SignText.Sanitize(raw);
            if (_label != null) _label.Text = Text;
            return Text;
        }

        /// <summary>Damage a sign; returns true if this blow destroyed it.</summary>
        public bool TakeDamage(float amount)
        {
            if (amount <= 0f) return false;
            Health -= amount;
            if (Health > 0f) return false;
            Health = 0f;
            QueueFree();
            return true;
        }
    }
}
