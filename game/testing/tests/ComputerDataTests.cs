using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    /// <summary>Computer towers as data machines (strawberry 2026-10-11: "wire up the computer tower props as
    /// data io machines. a power input, and 8 data ports, which act as data i/o depending on whats connected").
    ///
    /// ⚠ THE CHECKS THAT MATTER ARE THE DIRECTION ONES. A data port that is wired and powered is easy to get
    /// right; what is new here is a socket with NO declared direction, which has to resolve per wire from what
    /// the other end is. Every failure in that logic produces a port that simply reads Idle -- no error, no
    /// crash, just a panel that never lights.</summary>
    public sealed class ComputerDataTests : GameTest
    {
        public override string Name => "power.computer_data_ports";
        public override double TimeoutSimSeconds => 30;

        static ConnectionPort Port(DeployableDef.PortKind k)
            => new ConnectionPort { Kind = k };

        public override IEnumerable<Step> Run()
        {
            yield return Ticks(1);

            // ---- 1. THE KIND TABLE. DataIo is the only kind that can do both; everything else stays one-way,
            // which is what keeps a camera from ever being wired as a sink.
            T.Check("DataOut sources only",
                DeployableDef.CanSourceData(DeployableDef.PortKind.DataOut) && !DeployableDef.CanSinkData(DeployableDef.PortKind.DataOut));
            T.Check("DataIn sinks only",
                !DeployableDef.CanSourceData(DeployableDef.PortKind.DataIn) && DeployableDef.CanSinkData(DeployableDef.PortKind.DataIn));
            T.Check("DataIo does both",
                DeployableDef.CanSourceData(DeployableDef.PortKind.DataIo) && DeployableDef.CanSinkData(DeployableDef.PortKind.DataIo));
            T.Check("...and all three count as data, so none of them reach the power solve",
                DeployableDef.IsDataPort(DeployableDef.PortKind.DataIo)
                && DeployableDef.IsDataPort(DeployableDef.PortKind.DataOut)
                && DeployableDef.IsDataPort(DeployableDef.PortKind.DataIn));

            // ---- 2. DIRECTION RESOLUTION, which is the whole feature.
            var cam = Port(DeployableDef.PortKind.DataOut);
            var tv = Port(DeployableDef.PortKind.DataIn);
            var pcA = Port(DeployableDef.PortKind.DataIo);
            var pcB = Port(DeployableDef.PortKind.DataIo);

            T.Check("a one-way peer pins the direction: computer -> television",
                PowerNet.ResolveDataLink(pcA, tv, out var o1, out var i1) && o1 == pcA && i1 == tv);
            T.Check("...and camera -> computer, the same socket the other way round",
                PowerNet.ResolveDataLink(cam, pcA, out var o2, out var i2) && o2 == cam && i2 == pcA);
            // ⭐ THE SAME PORT OBJECT resolved both ways in the two checks above. That is the literal reading of
            // "act as data i/o depending on whats connected", and no other port kind can do it.

            T.Check("a wire the tool would refuse (two sinks) resolves to nothing",
                !PowerNet.ResolveDataLink(tv, Port(DeployableDef.PortKind.DataIn), out _, out _));

            // ---- 3. TWO BIDIRECTIONAL ENDS: genuinely ambiguous, broken by liveness.
            pcA.DataLive = false; pcB.DataLive = false;
            T.Check("two idle computers invent no direction", !PowerNet.ResolveDataLink(pcA, pcB, out _, out _));
            pcA.DataLive = true;
            T.Check("...the one carrying a stream becomes the sender",
                PowerNet.ResolveDataLink(pcA, pcB, out var o3, out var i3) && o3 == pcA && i3 == pcB);
            pcA.DataLive = false; pcB.DataLive = true;
            T.Check("...and it flips when the other end is the live one",
                PowerNet.ResolveDataLink(pcA, pcB, out var o4, out var i4) && o4 == pcB && i4 == pcA);
            pcA.DataLive = true; pcB.DataLive = true;
            T.Check("two already-fed ends do not feed each other (no loop)",
                !PowerNet.ResolveDataLink(pcA, pcB, out _, out _));

            foreach (var p in new[] { cam, tv, pcA, pcB }) p.QueueFree();

            // ---- 4. THE TOWER ITSELF: one power inlet and eight sockets, built off a real prop mesh.
            var mesh = ContentProvider.ParseObj("res://content/objects/Computer_0.obj");
            T.Check("Computer_0.obj loads", mesh != null);
            if (mesh != null)
            {
                var mi = new MeshInstance3D { Mesh = mesh };
                World.AddChild(mi);
                var pc = ComputerDevice.Make(mi, "Computer_0");
                T.Check("a tower builds a device", pc != null);
                if (pc != null)
                {
                    World.AddChild(pc);
                    yield return Ticks(1);
                    T.Check($"...with {ComputerDevice.PortCount} data sockets + 1 power inlet ({pc.PowerPorts.Count})",
                        pc.PowerPorts.Count == ComputerDevice.PortCount + 1);
                    T.Check("...the inlet is a power CONSUMER, so the tower draws watts like any appliance",
                        pc.PowerPort != null && pc.PowerPort.Kind == DeployableDef.PortKind.Consumer && pc.PowerPort.Watts > 0f);
                    int io = 0;
                    for (int i = 0; i < ComputerDevice.PortCount; i++)
                        if (pc.DataPort(i)?.Kind == DeployableDef.PortKind.DataIo) io++;
                    T.Check($"...and all {ComputerDevice.PortCount} sockets are bidirectional ({io})", io == ComputerDevice.PortCount);
                    // ⚠⚠ UNPOWERED MEANS DEAD -- and this check has to FORCE the condition to mean anything.
                    // The first version just asserted StateOf(0) == Idle on a fresh tower, which passes whether
                    // or not the power gate exists: nothing is wired, so DataLive is false and the port reads
                    // Idle anyway. Mutation-testing caught it (deleting the `!Running` guard left the test
                    // green). So: make a socket that WOULD read live, then confirm the gate still kills it.
                    var sock = pc.DataPort(0);
                    sock.Occupied = true;
                    sock.DataLive = true;
                    sock.DataSource = new Node3D();   // a peer that is not us -> would be "Receiving" if powered
                    T.Check($"an unpowered tower reports a WIRED, LIVE socket as Idle ({pc.StateOf(0)})",
                        !pc.Running && pc.StateOf(0) == ComputerDevice.PortState.Idle);
                    T.Check("...and claims no stream to pass on", !pc.HasStream);
                    sock.DataLive = false; sock.Occupied = false; sock.DataSource = null;
                    T.Check("the prop name maps to the device", ComputerDevice.IsDeviceProp("Computer_0")
                        && ComputerDevice.IsDeviceProp("Computer_3") && !ComputerDevice.IsDeviceProp("Chair_0"));
                }
                World.RemoveChild(mi); mi.QueueFree();
            }
        }
    }
}
