using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>A computer tower as a data machine (strawberry 2026-10-11: "wire up the computer tower props as
    /// data io machines. a power input, and 8 data ports, which act as data i/o depending on whats connected").
    ///
    /// ⭐ WHAT IS ACTUALLY NEW HERE IS THE PORT KIND, not the tower. The data layer already existed for CCTV:
    /// PortKind.DataOut/DataIn, solved by PowerNet separately from watts, a camera sourcing and a television
    /// sinking. But every one of those is one-way BY DECLARATION, which is right for a camera and wrong for a
    /// computer -- the same socket should take a feed in from one machine and push one out to another. So these
    /// eight are PortKind.DataIo and their direction is resolved per wire at solve time
    /// (PowerNet.ResolveDataLink).
    ///
    /// ⚠ POWER AND SIGNAL STAY SEPARATE, which is the existing rule and worth not breaking: data ports never
    /// enter the power solve, so wiring eight machines together cannot brown out the circuit they sit on. The
    /// tower draws its watts through ONE ordinary Consumer port, and an unpowered tower's ports all go dark --
    /// a box with no electricity is not a hub.</summary>
    public partial class ComputerDevice : Node3D, IPowerDevice
    {
        /// <summary>Master asked for eight. It is also what fits the case face at a readable cube size, and it
        /// is the number the monitor panel is laid out for (2 columns x 4).</summary>
        public const int PortCount = 8;
        /// <summary>Watts the tower pulls. Enough that running one off a small battery is a decision, not
        /// enough to matter next to a floodlight.</summary>
        public const float Draw = 45f;

        readonly List<ConnectionPort> _ports = new();
        ConnectionPort _power;
        readonly ConnectionPort[] _data = new ConnectionPort[PortCount];

        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => 0;
        public IReadOnlyList<ConnectionPort> PowerPorts => _ports;

        /// <summary>Is the tower actually running? Both halves: it needs its own electricity before any socket
        /// means anything.</summary>
        public bool Running => _power != null && _power.Powered;
        public ConnectionPort PowerPort => _power;
        public ConnectionPort DataPort(int i) => (uint)i < PortCount ? _data[i] : null;

        /// <summary>What one socket is doing right now, for the monitor panel and for tests.</summary>
        public enum PortState { Idle, Receiving, Sending }

        /// <summary>⚠ READ OFF THE SOLVE, never stored. A port's direction is a property of what it is wired to
        /// this frame, so caching it is how the panel ends up showing a link that was unplugged.</summary>
        public PortState StateOf(int i)
        {
            var p = DataPort(i);
            if (p == null || !Running || !p.Occupied) return PortState.Idle;
            // Live means a stream is ARRIVING here. A live socket on a running tower is an input; an occupied
            // but dark one is either sending (the peer is lit from us) or connected to something idle -- and
            // those two are only distinguishable by asking the peer, which PowerNet already did when it chose
            // a direction. DataSource being us is the tell.
            if (p.DataLive && p.DataSource != this) return PortState.Receiving;
            return SourcingOn(p) ? PortState.Sending : PortState.Idle;
        }

        /// <summary>Is this socket feeding its peer? True when the tower has a stream to give and the wire
        /// resolved outward -- PowerNet stamps the peer's DataSource with ours.</summary>
        bool SourcingOn(ConnectionPort p) => p.DataLive && p.DataSource == this;

        /// <summary>Does the tower have a stream to publish at all? It does once ANY socket is receiving one --
        /// a tower with nothing plugged into it is a box, and a tower fed by a camera is a repeater. This is
        /// what makes the eight sockets a HUB rather than eight unrelated holes.</summary>
        public bool HasStream
        {
            get
            {
                if (!Running) return false;
                foreach (var p in _data) if (p != null && p.DataLive && p.DataSource != this) return true;
                return false;
            }
        }

        public static bool IsDeviceProp(string name) => name != null && name.StartsWith("Computer_");

        /// <summary>Build the device on a placed tower. Sockets are laid out on the case's BACK face, which is
        /// where they are on a real machine and, more usefully, means the eight cubes do not cover the front the
        /// player is looking at.</summary>
        public static ComputerDevice Make(MeshInstance3D bodyMi, string propName)
        {
            if (bodyMi?.Mesh == null) return null;
            var dev = new ComputerDevice { Name = "ComputerDevice" };
            var box = bodyMi.Mesh.GetAabb();
            // The case's own axes, measured rather than assumed: the tower props are Z-up like every other prop,
            // so "back" is -Y in prop space and "up" is +Z.
            float backY = box.Position.Y - 0.02f;
            float midX = box.Position.X + box.Size.X * 0.5f;
            float baseZ = box.Position.Z;

            dev._power = ConnectionPort.Create(dev, new DeployableDef.Port
            {
                Kind = DeployableDef.PortKind.Consumer,
                Pos = new Vector3(midX, backY, baseZ + box.Size.Z * 0.12f),
                Watts = Draw,
            }, "computer");
            dev.AddChild(dev._power);
            dev._ports.Add(dev._power);

            // Eight sockets in two columns of four up the back, clear of the power inlet below them.
            for (int i = 0; i < PortCount; i++)
            {
                float col = (i % 2 == 0) ? -1f : 1f;
                float row = i / 2;
                var port = ConnectionPort.Create(dev, new DeployableDef.Port
                {
                    Kind = DeployableDef.PortKind.DataIo,
                    Pos = new Vector3(midX + col * box.Size.X * 0.18f,
                                      backY,
                                      baseZ + box.Size.Z * (0.34f + row * 0.17f)),
                    Watts = 0f,
                }, $"computer port {i + 1}");
                dev._data[i] = port;
                dev.AddChild(port);
                dev._ports.Add(port);
            }
            PowerNet.MarkDirty();
            return dev;
        }
    }
}
