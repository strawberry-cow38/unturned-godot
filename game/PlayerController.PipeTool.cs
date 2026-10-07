using Godot;
using System.Collections.Generic;
using UnturnedGodot.Net;

namespace UnturnedGodot
{
    // --- INDUSTRIAL PIPE TOOL (v56, item 9215; strawberry 2026-10-06: "new industrial pipe tool") ---
    //
    // The hose tool's UX, which she accepted as the feel ("pipes feel like the hose tool"): look at a socket to
    // read it, LMB an OUT socket to start, LMB the ground to lay bends (with a live green/red preview against the
    // same 20-node / 40 m budget), LMB an IN socket to finish. RMB undoes a bend while routing; on a piped
    // socket, HOLD RMB cuts the pipe and a TAP picks it back up to re-route.
    //
    // ⚠ NOT the hose's netcode. A hose is client-only -- never sent, replicated or saved. A pipe is the opposite:
    // the server owns it, so finishing a route SENDS a request (NetConnectPipe) and draws nothing; the committed
    // pipe appears when PipeConnected comes back through DeployableReplicaView. There is no local fallback,
    // because singleplayer IS the loopback server -- a null seam means a harness with no server, and there the
    // honest answer is that pipe cannot be laid.
    //
    // The look-ray masks ItemPortNode.PortLayer ONLY, so this tool cannot see a power or fluid socket, and the
    // wire/hose rays cannot see an item one. That is the whole "the pipe tool must not connect power ports" rule.
    public partial class PlayerController
    {
        const float PipeReach = 5.5f, PipePlaceReach = 6f;            // look-at reach / bend-place reach (the hose tool's)
        const float PipeClearTime = 1.0f, PipeClickMax = 0.28f;       // hold this long on a piped socket to cut; release within this = tap (re-route)
        ItemPortNode _pipePort;                                       // the socket under the crosshair
        bool _piping; ItemPortNode _pipeSrc;                          // mid-route from this OUT socket
        readonly List<Vector3> _pipeNodes = new();                    // the bends laid so far (world points)
        ItemPipe _pipePreview;
        PhysicsRayQueryParameters3D _pipeRayQ, _pipePlaceRayQ;
        CanvasLayer _pipeHudLayer; Label _pipeHudLabel;
        ItemPortNode _clearPipePort; float _pipeClearHold;
        bool _pipeArrowsOn;
        ItemDevicePanel _itemPanel;

        public ItemPortNode PipeLookPort => _pipePort;                  // L1 probe
        public bool DebugPiping => _piping;
        public int DebugPipeNodeCount => _pipeNodes.Count;
        public string DebugPipeHud => _pipeHudLabel != null && _pipeHudLabel.Visible ? _pipeHudLabel.Text : null;
        public ItemDevicePanel DebugItemPanel => _itemPanel;
        public bool ItemPanelOpen => _itemPanel != null && IsInstanceValid(_itemPanel) && _itemPanel.Visible;
        // Test seams that drive the REAL tool methods (the look scan, the click), not copies of them.
        public void DebugPipeLook() => UpdatePipeLook();
        public void DebugPipeClick() => PipeLmb();
        public void DebugPipeRmb() => PipeRmb();
        public void DebugOpenItemDeviceConfig(Deployable d) => OpenItemDeviceConfig(d);

        bool PipeInputLive => !_dead && _driving == null
                              && (Input.MouseMode == Input.MouseModeEnum.Captured || DebugForceLookScan);

        /// <summary>Can a route started at <paramref name="start"/> finish on <paramref name="target"/>? The client half
        /// of CanConnectPipe: Out -> In, two different devices, the target socket free. Length and node budgets are
        /// checked by the caller against the route; reach and the rest are the server's.</summary>
        bool PipeCanComplete(ItemPortNode start, ItemPortNode target)
            => IsInstanceValid(start) && IsInstanceValid(target) && target.Usable && target != start
               && start.Dir == ItemPortDir.Out && target.Dir == ItemPortDir.In
               && !ReferenceEquals(start.Owner, target.Owner) && !PortPiped(target);

        /// <summary>The committed pipe on this socket, from the replica view's nodes (null = free).</summary>
        ItemPipe PipeOnPort(ItemPortNode p)
        {
            if (p == null) return null;
            foreach (var n in GetTree().GetNodesInGroup("item_pipes"))
                if (n is ItemPipe ip && IsInstanceValid(ip) && (ip.Src == p || ip.Dst == p)) return ip;
            return null;
        }

        bool PortPiped(ItemPortNode p) => PipeOnPort(p) != null;

        // Per-frame while the tool is out: pick the aimed socket, drive the preview + the HUD.
        void UpdatePipeLook()
        {
            if (!HoldingPipeTool)
            {
                if (_piping) CancelPipe();
                if (IsInstanceValid(_pipePort)) _pipePort.SetHighlight(ItemPortNode.PortHi.None);
                _pipePort = null; PipeHudSet(null); return;
            }
            ItemPortNode port = null;
            if (_cam != null && PipeInputLive)
            {
                var space = GetWorld3D().DirectSpaceState;
                Vector3 from = _cam.GlobalPosition, fwd = -_cam.GlobalTransform.Basis.Z;
                _pipeRayQ ??= new PhysicsRayQueryParameters3D { CollisionMask = ItemPortNode.PortLayer };
                _pipeRayQ.From = from; _pipeRayQ.To = from + fwd * PipeReach;
                var hit = space.IntersectRay(_pipeRayQ);
                if (hit.Count > 0 && hit["collider"].As<GodotObject>() is ItemPortNode ip && IsInstanceValid(ip)) port = ip;
            }
            if (port != _pipePort)
            {
                if (IsInstanceValid(_pipePort) && _pipePort != _pipeSrc) _pipePort.SetHighlight(ItemPortNode.PortHi.None);
                _pipePort = port;
            }

            if (_piping)
            {
                if (!IsInstanceValid(_pipeSrc)) { CancelPipe(); PipeHudSet(null); return; }
                bool ok = PipeCanComplete(_pipeSrc, _pipePort);
                if (IsInstanceValid(_pipePort) && _pipePort != _pipeSrc)
                    _pipePort.SetHighlight(ok ? ItemPortNode.PortHi.PipeOk : ItemPortNode.PortHi.PipeBad);
                var pts = new List<Vector3> { _pipeSrc.GlobalPosition };
                pts.AddRange(_pipeNodes);
                pts.Add(ok ? _pipePort.GlobalPosition : PipePlacePoint());
                float len = PolyLen(pts);
                bool overLimit = _pipeNodes.Count > ItemPipeRules.MaxNodes || len > ItemPipeRules.MaxLength;
                _pipePreview?.SetPoints(pts, valid: !overLimit);
                if (overLimit) PipeHudSet($"bends {_pipeNodes.Count}/{ItemPipeRules.MaxNodes}    {len:0.0}/{ItemPipeRules.MaxLength:0}m   -- LIMIT");
                else if (IsInstanceValid(_pipePort) && _pipePort != _pipeSrc && !ok) PipeHudSet(PipeRefusal(_pipeSrc, _pipePort));
                else PipeHudSet($"bends {_pipeNodes.Count}/{ItemPipeRules.MaxNodes}    {len:0.0}/{ItemPipeRules.MaxLength:0}m" + (ok ? "    [LMB] connect" : ""));
            }
            else
            {
                if (IsInstanceValid(_pipePort)) _pipePort.SetHighlight(ItemPortNode.PortHi.Focus);
                if (!IsInstanceValid(_pipePort)) { PipeHudSet(null); return; }
                bool piped = PortPiped(_pipePort);
                string hint = piped ? "   ([RMB] hold: cut · tap: re-route)"
                            : _pipePort.Dir == ItemPortDir.Out ? "   ([LMB] start a pipe)" : "   (finish a pipe here)";
                PipeHudSet(_pipePort.InfoLine(piped) + hint);
            }
        }

        /// <summary>Why the target under the crosshair will not take the pipe -- said, not just shown red.</summary>
        string PipeRefusal(ItemPortNode start, ItemPortNode target)
        {
            if (!target.Usable) return "that device is wrecked";
            if (ReferenceEquals(start.Owner, target.Owner)) return "a device cannot pipe into itself";
            if (target.Dir != ItemPortDir.In) return "pipes run OUT (amber) -> IN (violet)";
            if (PortPiped(target)) return "that socket already has a pipe";
            return "can't connect there";
        }

        // The free end / a bend = the look point on the world, excluding every deployable body so a run passes
        // straight through boxes instead of sticking to their faces (the hose tool's rule).
        Vector3 PipePlacePoint()
        {
            if (_cam == null) return GlobalPosition;
            var space = GetWorld3D().DirectSpaceState;
            Vector3 from = _cam.GlobalPosition, fwd = -_cam.GlobalTransform.Basis.Z;
            _pipePlaceRayQ ??= new PhysicsRayQueryParameters3D { CollisionMask = (1u << 0) | (1u << 6) };
            _pipePlaceRayQ.From = from; _pipePlaceRayQ.To = from + fwd * PipePlaceReach;
            var exclude = new Godot.Collections.Array<Rid> { GetRid() };
            foreach (var n in GetTree().GetNodesInGroup("deployables"))
                if (n is CollisionObject3D co && IsInstanceValid(co)) exclude.Add(co.GetRid());
            _pipePlaceRayQ.Exclude = exclude;
            var hit = space.IntersectRay(_pipePlaceRayQ);
            return hit.Count > 0 ? (Vector3)hit["position"] : from + fwd * PipePlaceReach;
        }

        // LMB: start on a free OUT socket, lay a bend, or finish on a legal IN socket -- finishing and bending both
        // gated on the same 20-bend / 40 m budget the preview shows and the server enforces.
        void PipeLmb()
        {
            if (_dead) return;
            if (!_piping)
            {
                if (IsInstanceValid(_pipePort) && _pipePort.Usable && _pipePort.Dir == ItemPortDir.Out && !PortPiped(_pipePort))
                {
                    _piping = true; _pipeSrc = _pipePort; _pipeNodes.Clear();
                    _pipeSrc.SetHighlight(ItemPortNode.PortHi.Focus);
                    _pipePreview = new ItemPipe(); GetParent().AddChild(_pipePreview);
                    Log.Print($"[pipe] started from {_pipeSrc.InfoLine(false)}");
                }
                return;
            }
            if (PipeCanComplete(_pipeSrc, _pipePort))
            {
                var cpts = new List<Vector3> { _pipeSrc.GlobalPosition }; cpts.AddRange(_pipeNodes); cpts.Add(_pipePort.GlobalPosition);
                if (_pipeNodes.Count <= ItemPipeRules.MaxNodes && PolyLen(cpts) <= ItemPipeRules.MaxLength) CompletePipe(_pipePort);
                return;
            }
            Vector3 lp = PipePlacePoint();
            var pts = new List<Vector3> { _pipeSrc.GlobalPosition }; pts.AddRange(_pipeNodes); pts.Add(lp);
            if (_pipeNodes.Count >= ItemPipeRules.MaxNodes || PolyLen(pts) > ItemPipeRules.MaxLength) return;   // the limit blocks the bend
            _pipeNodes.Add(lp);
        }

        void PipeRmb()
        {
            if (!_piping) return;
            if (_dead || _pipeNodes.Count == 0) CancelPipe();
            else _pipeNodes.RemoveAt(_pipeNodes.Count - 1);
        }

        void CompletePipe(ItemPortNode target)
        {
            if (!IsInstanceValid(_pipeSrc)) { CancelPipe(); return; }
            if (NetConnectPipe == null || _pipeSrc.OwnerNetId == 0 || target.OwnerNetId == 0)
            {
                // No server to ask (a --direct harness), or a device the server never registered: say so rather
                // than draw a pipe that does not exist anywhere that matters.
                PipeHudSet("no server to connect through");
                Log.Print("[pipe] connect refused locally: no NetConnectPipe seam / unreplicated device");
                CancelPipe();
                return;
            }
            var route = new UnityEngine.Vector3[_pipeNodes.Count];
            for (int i = 0; i < route.Length; i++) route[i] = new UnityEngine.Vector3(_pipeNodes[i].X, _pipeNodes[i].Y, _pipeNodes[i].Z);
            NetConnectPipe(_pipeSrc.OwnerNetId, _pipeSrc.Index, target.OwnerNetId, target.Index, route);
            Log.Print($"[pipe] connect requested {_pipeSrc.ProviderName}#{_pipeSrc.Index} -> {target.ProviderName}#{target.Index} ({route.Length} bends)");
            if (IsInstanceValid(target)) target.SetHighlight(ItemPortNode.PortHi.None);
            CancelPipe();   // the committed pipe is the server's echo, drawn by the replica view
        }

        void CancelPipe()
        {
            if (_pipePreview != null && IsInstanceValid(_pipePreview)) _pipePreview.QueueFree();
            _pipePreview = null;
            if (IsInstanceValid(_pipeSrc)) _pipeSrc.SetHighlight(ItemPortNode.PortHi.None);
            _piping = false; _pipeSrc = null; _pipeNodes.Clear();
        }

        // RMB PRESS while not routing: arm a cut / re-route on the piped socket under the crosshair.
        void PipeManageArm()
        {
            if (!HoldingPipeTool || !PipeInputLive) return;
            if (PipeOnPort(_pipePort) != null) { _clearPipePort = _pipePort; _pipeClearHold = 0f; }
        }

        // Per-frame: an armed RMB on a piped socket -- held to PipeClearTime CUTS the pipe; released quickly
        // (<= PipeClickMax) cuts it and picks it back up from its source with the bends kept, to re-route.
        // Both are a REMOVE request: the server drops the pipe and every client's replica follows.
        void UpdatePipeManage(float delta)
        {
            if (_clearPipePort == null) return;
            bool active = HoldingPipeTool && !_piping && PipeInputLive;
            var pipe = PipeOnPort(_clearPipePort);
            if (!active || _pipePort != _clearPipePort || pipe == null) { _clearPipePort = null; _pipeClearHold = 0f; return; }
            if (Input.IsMouseButtonPressed(MouseButton.Right))
            {
                _pipeClearHold += delta;
                if (_pipeClearHold >= PipeClearTime) { CutPipe(pipe, reroute: false); _clearPipePort = null; _pipeClearHold = 0f; PipeHudSet(null); return; }
                PipeHudSet($"cutting pipe... {Mathf.Clamp((int)(_pipeClearHold / PipeClearTime * 100f), 0, 99)}%");
            }
            else { if (_pipeClearHold <= PipeClickMax) CutPipe(pipe, reroute: true); _clearPipePort = null; _pipeClearHold = 0f; }
        }

        /// <summary>Ask the server to drop this pipe; with <paramref name="reroute"/>, start a new route from its OUT
        /// socket carrying the old bends, so a tap is "pick it back up" exactly like the hose.</summary>
        void CutPipe(ItemPipe pipe, bool reroute)
        {
            if (pipe == null || !IsInstanceValid(pipe) || pipe.NetId == 0 || NetRemovePipe == null) return;
            NetRemovePipe(pipe.NetId);
            Log.Print($"[pipe] remove requested (pipe {pipe.NetId}{(reroute ? ", re-routing" : "")})");
            if (!reroute || !IsInstanceValid(pipe.Src)) return;
            _piping = true; _pipeSrc = pipe.Src; _pipeNodes.Clear();
            for (int i = 1; i < pipe.Points.Count - 1; i++) _pipeNodes.Add(pipe.Points[i]);   // keep the bends; drop the two socket ends
            _pipeSrc.SetHighlight(ItemPortNode.PortHi.Focus);
            _pipePreview = new ItemPipe(); GetParent().AddChild(_pipePreview);
        }

        // Every item socket shows (cube + arrow) only while the pipe tool is out, the hose tool's rule: they are a
        // piping UI and have no business on screen otherwise. Blue arrow = free, red = piped or on a wreck.
        void UpdatePipeArrows()
        {
            bool show = HoldingPipeTool && PipeInputLive;
            if (!show)
            {
                if (_pipeArrowsOn)
                {
                    foreach (var n in GetTree().GetNodesInGroup("item_ports"))
                        if (n is ItemPortNode p && IsInstanceValid(p)) { p.Visible = false; p.SetArrowState(false, false); }
                    _pipeArrowsOn = false;
                }
                return;
            }
            _pipeArrowsOn = true;
            foreach (var n in GetTree().GetNodesInGroup("item_ports"))
                if (n is ItemPortNode p && IsInstanceValid(p) && p.Usable)
                {
                    p.Visible = true;
                    p.SetArrowState(true, !PortPiped(p));
                }
        }

        void PipeHudSet(string text)
        {
            if (string.IsNullOrEmpty(text)) { if (_pipeHudLabel != null) _pipeHudLabel.Visible = false; return; }
            if (_pipeHudLabel == null)
            {
                _pipeHudLayer = new CanvasLayer { Layer = 40 }; AddChild(_pipeHudLayer);
                _pipeHudLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
                _pipeHudLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
                _pipeHudLabel.AnchorLeft = 0.5f; _pipeHudLabel.AnchorRight = 0.5f; _pipeHudLabel.OffsetTop = 120f; _pipeHudLabel.OffsetLeft = -300f; _pipeHudLabel.OffsetRight = 300f;
                _pipeHudLabel.AddThemeFontSizeOverride("font_size", 26);
                _pipeHudLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
                _pipeHudLabel.AddThemeConstantOverride("outline_size", 6);
                _pipeHudLayer.AddChild(_pipeHudLabel);
            }
            _pipeHudLabel.Text = text; _pipeHudLabel.Visible = true;
        }

        // ---- the F panel (splitter mode / mover rate) ----

        /// <summary>Open the config panel on a splitter or mover, on the server's CURRENT values. Every change is
        /// sent as it is made (NetConfigureItemDevice); nothing is applied locally.</summary>
        void OpenItemDeviceConfig(Deployable d)
        {
            if (d == null || !IsInstanceValid(d) || d.Def == null || !d.Def.IsItemConfigurable) return;
            if (d.NetId == 0 || NetConfigureItemDevice == null)
            {
                Log.Print("[pipe] config panel needs the server (no NetConfigureItemDevice seam / unreplicated device)");
                return;
            }
            var current = NetItemConfigOf?.Invoke(d.NetId) ?? new ItemDeviceConfig();
            if (_itemPanel != null && IsInstanceValid(_itemPanel)) _itemPanel.QueueFree();
            uint netId = d.NetId;
            _itemPanel = new ItemDevicePanel { Kind = d.Def.ItemDevice, Title = d.Def.Name, NetId = netId, Config = current.Clone() };
            _itemPanel.Changed = cfg => { NetConfigureItemDevice?.Invoke(netId, cfg); Log.Print($"[pipe] configure {netId}: mode {cfg.Mode} weights {cfg.Weights[0]}/{cfg.Weights[1]}/{cfg.Weights[2]} rate {cfg.Rate}/s"); };
            AddChild(_itemPanel);
            _itemPanel.Open(netId, d.Def.Name, d.Def.ItemDevice, current);
        }

        /// <summary>While the panel is up it owns F/Esc (close) and swallows clicks that would otherwise fire or
        /// place through it. Returns true if the event was consumed.</summary>
        bool ItemPanelInput(InputEvent @event)
        {
            if (!ItemPanelOpen) return false;
            bool close = (@event is InputEventKey { Keycode: Key.Escape, Pressed: true }) || Keybinds.JustPressed(GameAction.Interact, @event);
            if (close) { _itemPanel.Close(); GetViewport().SetInputAsHandled(); return true; }
            if (@event is InputEventMouseButton) { GetViewport().SetInputAsHandled(); return true; }
            return false;
        }
    }
}
