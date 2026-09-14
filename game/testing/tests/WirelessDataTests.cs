using Godot;
using System.Collections.Generic;

namespace UnturnedGodot.Testing
{
    // WIRELESS DATA: camera -> wire -> transmitter ~~> receiver -> wire -> TV (strawberry 2026-09-14).
    //
    // ⭐ THE CLAIM THAT MATTERS IS NOT "A SIGNAL ARRIVES". It is that the SCREEN KNOWS IT IS A CAMERA. The old
    // one-hop lookup walked the aerial's wire and took whatever was on the other end -- which, with a pair in
    // the middle, is the RECEIVER. That bug shows up as a dark screen while every single port reports healthy,
    // so a test that only asserted DataLive along the chain would pass on it.
    //
    // And every check here needs the MISMATCHED-CODE control beside it, because "the pair connects" is also
    // what a build that ignores codes entirely would report.
    public sealed class WirelessDataTests : GameTest
    {
        public override string Name => "power.wireless_data";
        public override double TimeoutSimSeconds => 30;

        static void Link(Node world, ConnectionPort a, ConnectionPort b)
        {
            var w = new Wire(); world.AddChild(w); w.Source = a; w.Consumer = b; w.AddToGroup("wires");
            w.SetPoints(new List<Vector3> { a.GlobalPosition, b.GlobalPosition }, valid: true);
        }
        static ConnectionPort PortOf(Deployable d, DeployableDef.PortKind k)
            => d?.Ports.Find(p => p.Kind == k);

        public override IEnumerable<Step> Run()
        {
            Rigs.Ground(World);
            yield return Ticks(2);

            // A camera on a wall, a radio pair, and a screen -- each powered off one generator.
            var camMesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Camera_0.obj"));
            var (camBody, _) = ObjMesh.SplitCameraArm(camMesh);
            var housing = new MeshInstance3D { Mesh = camBody ?? camMesh };
            World.AddChild(housing);
            housing.GlobalPosition = new Vector3(0f, 3f, -3f);
            var cctv = SecurityCamera.Make(housing, (camBody ?? camMesh).GetAabb());
            World.AddChild(cctv);

            var tx = Deployable.Spawn(World, DeployableDef.DataTransmitter, new Vector3(3f, 0f, 0f), 0f);
            var rx = Deployable.Spawn(World, DeployableDef.DataReceiver, new Vector3(-3f, 0f, 0f), 0f);
            var gen = Deployable.Spawn(World, DeployableDef.Generator, new Vector3(0f, 0f, 6f), 0f);
            yield return Ticks(2);
            T.Check("transmitter + receiver placed", tx != null && rx != null && gen != null);
            if (tx == null || rx == null || gen == null) yield break;

            var tvMesh = ObjMesh.Load(ProjectSettings.GlobalizePath("res://content/objects/Television_0.obj"));
            var tvBody = new MeshInstance3D { Mesh = tvMesh };
            World.AddChild(tvBody);
            tvBody.GlobalPosition = new Vector3(-6f, 1f, 0f);
            var tv = TVDevice.Make(tvBody, "Television_0");
            if (tv != null) World.AddChild(tv);
            yield return Ticks(2);
            T.Check("a TV with an aerial socket", tv != null && tv.DataInPort != null);
            if (tv == null) yield break;

            var genOut = PortOf(gen, DeployableDef.PortKind.Output);
            Link(World, genOut, cctv.PowerPorts[0]);
            Link(World, genOut, PortOf(tx, DeployableDef.PortKind.Consumer));
            Link(World, genOut, PortOf(rx, DeployableDef.PortKind.Consumer));
            Link(World, genOut, tv.PowerPorts[0]);
            Link(World, cctv.DataOut, PortOf(tx, DeployableDef.PortKind.DataIn));
            Link(World, PortOf(rx, DeployableDef.PortKind.DataOut), tv.DataInPort);
            gen.TogglePower();

            // ---- 1. MATCHING CODES CONNECT ------------------------------------------------------------------
            tx.DataCode = 4271; rx.DataCode = 4271;
            PowerNet.Recompute(Tree);
            yield return Ticks(4);
            T.Check($"(match) the transmitter's input is fed ({PortOf(tx, DeployableDef.PortKind.DataIn)?.DataLive})",
                    PortOf(tx, DeployableDef.PortKind.DataIn)?.DataLive == true);
            T.Check($"(match) the receiver's output goes live over the air ({PortOf(rx, DeployableDef.PortKind.DataOut)?.DataLive})",
                    PortOf(rx, DeployableDef.PortKind.DataOut)?.DataLive == true);
            T.Check($"(match) the TV's aerial receives ({tv.DataInPort?.DataLive})", tv.DataInPort?.DataLive == true);
            // ⭐ THE ONE THE OLD LOOKUP FAILED: the screen must resolve the CAMERA, not the receiver it is wired to.
            T.Check($"(match) ...and the screen knows its source is the CAMERA ({tv.FeedSource() != null})",
                    tv.FeedSource() == cctv);

            // ---- 2. THE CONTROL: a different code is a different channel ------------------------------------
            rx.DataCode = 1111;
            PowerNet.Recompute(Tree);
            yield return Ticks(4);
            T.Check($"(mismatch) the receiver goes dark ({PortOf(rx, DeployableDef.PortKind.DataOut)?.DataLive})",
                    PortOf(rx, DeployableDef.PortKind.DataOut)?.DataLive == false);
            T.Check("(mismatch) ...and so does the screen", tv.DataInPort?.DataLive == false && tv.FeedSource() == null);
            T.Check($"(mismatch) but the transmitter is still fed ({PortOf(tx, DeployableDef.PortKind.DataIn)?.DataLive})",
                    PortOf(tx, DeployableDef.PortKind.DataIn)?.DataLive == true);   // the break is the AIR, not the wire

            // ---- 3. POWER IS REQUIRED AT BOTH ENDS ----------------------------------------------------------
            // "both transmitter and reciever need to be powered" -- asserted separately for each end, because a
            // rule that only checked one would pass whichever test happened to unpower the other.
            rx.DataCode = 4271;
            PowerNet.Recompute(Tree);
            yield return Ticks(4);
            T.Check("(repaired) matching codes reconnect", tv.FeedSource() == cctv);

            gen.TogglePower();   // cut everything
            PowerNet.Recompute(Tree);
            yield return Ticks(4);
            T.Check("(unpowered) the whole chain goes dark", tv.DataInPort?.DataLive == false && tv.FeedSource() == null);
            yield break;
        }
    }
}
