using Godot;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>A placed Radio_0 / Radio_1 prop as a working set: F toggles it, it hisses static while it is on AND
    /// fed, and it takes power either from the mains or from its own wired input -- the same two-source gate a
    /// television uses (strawberry 2026-09-04: "has power io input. works off global power like a tv").
    ///
    /// Deliberately a much smaller device than <see cref="TVDevice"/>, and the difference is the point: a television
    /// carves a screen sub-mesh off the body, reprojects its UVs and drives a shader. A radio has no screen. Sharing
    /// the power/interact CONTRACT with the TV is what strawberry asked for; sharing its rendering machinery would be
    /// copying 1900 lines to switch a hiss on and off.
    ///
    /// DEFAULTS TO OFF, where televisions default to on. A room of lit screens reads as "someone left the TV on"; a
    /// map of radios all hissing broadband noise at once is just noise, and static carries the annoyance a picture
    /// does not. The player turns it on.</summary>
    public partial class RadioDevice : Node3D, IPowerDevice
    {
        // The two ripped radio props. Kept next to the device that implements them, the way TVDevice.IsDeviceProp
        // sits next to its kind table -- SmartProps calls into here rather than keeping a second list that can drift.
        public static bool IsRadioProp(string name) => name == "Radio_0" || name == "Radio_1";

        /// <summary>Collider meta carrying the device, so a look-ray landing on a Radio_0/1 body finds it.
        /// Mirrors <see cref="TVDevice.HitMeta"/>; WorldBuilder stamps it on the body.</summary>
        public static readonly StringName HitMeta = "radiodevice";

        const float RadioWatts = 12f;      // a mains transistor set; a TV draws 55-120 (TVDevice.WattsFor)
        const float StaticVolume = 0.30f;  // matches the TV's snow, which was tuned down from the tone deliberately
        const float StaticUnitSize = 1.2f; // and so does its falloff: broadband hiss must not carry two rooms
        const float StaticMaxDist = 6f;

        public string PropName = "Radio_0";

        bool _on;                // player toggle state, independent of the grid
        bool _broken;            // prop smashed -> stays dead through any grid sweep
        bool _playing;           // last EFFECTIVE state actually applied to the audio
        bool _plugWasPowered;    // for the feed poll below

        AudioStreamPlayer3D _static, _onClick, _offClick;
        MeshInstance3D _outline;
        Vector3 _bodyCenterLocal;

        readonly List<ConnectionPort> _ports = new();
        ConnectionPort _plug;

        // ---- IPowerDevice ------------------------------------------------------------------------------------------
        public bool PowerProducing => false;
        public bool PowerOnFire => false;
        public uint PowerNetId => 0;   // SP-local map fixture, as TVDevice
        public IReadOnlyList<ConnectionPort> PowerPorts => _ports;

        public bool PlugPowered => _plug != null && GodotObject.IsInstanceValid(_plug) && _plug.Powered;

        /// <summary>The two-source gate, identical to TVDevice.HasFeed: the mains, or its own wired input. A set fed
        /// through its port keeps playing through a blackout; a mains-fed one dies with the grid.</summary>
        public bool HasFeed => PowerNet.MainsLive || PlugPowered;

        /// <summary>Is it actually making noise right now? The effective state, not the switch position.</summary>
        public bool Playing => _playing;
        public bool SwitchedOn => _on;

        public static RadioDevice Make(MeshInstance3D bodyMi, string propName)
        {
            var r = new RadioDevice { PropName = propName, Transform = bodyMi.Transform };
            r.Build(bodyMi.Mesh);
            return r;
        }

        void Build(Mesh body)
        {
            if (body == null) { Log.Err($"[radio] {PropName}: no body mesh"); return; }
            var aabb = body.GetAabb();
            _bodyCenterLocal = aabb.Position + aabb.Size * 0.5f;

            // Whole-prop look-focus outline (the F affordance), same recipe as TVDevice/ObjectDoor.
            _outline = OutlineOverlay.MakeOutline(body);
            AddChild(_outline);

            BuildAudio();
            BuildForecastBoard(aabb);
            BuildPlug(aabb);
            AddToGroup("radiodevices");   // swept on a grid change, like "tvdevices"
            Refresh();
        }

        // ---- THE FORECAST BOARD (strawberry 2026-10-10) --------------------------------------------------
        /// <summary>"the radio/stereo prop should get a billboard text that says the weather for today and
        /// tomorrow". Floats just above the set, billboarded, and reads WeatherManager's per-day outlook.
        ///
        /// ⭐ IT ONLY SHOWS WHEN THE RADIO IS ON AND POWERED, which is the whole reason to hang it on this
        /// prop rather than the HUD: a forecast is something you go and switch on, and a dead radio in a
        /// looted house should tell you nothing.
        ///
        /// ⚠ TEXT IS SET FROM A POLL, NOT ON A SIGNAL. The outlook changes when the DAY turns, which is a
        /// WeatherManager-internal event with no signal to subscribe to; a one-shot read at Build would be
        /// frozen on day 0 forever. Cheap because it only rebuilds the string when the day actually changes.</summary>
        Label3D _board;
        int _boardDay = int.MinValue;
        bool _boardShown;

        /// <summary>⚠ SIZED AGAINST THE SET, not picked to look right in a close-up. The longest line is about
        /// 24 characters, and at FontSize 44 a character is ~0.5 em wide, so this puts the board a little under
        /// a metre across -- roughly the radio plus a margin. The first value was 0.0038 and rendered the text
        /// three times the width of the prop: fine in a 900 px bake of just the radio, absurd in a room.</summary>
        public const float BoardPixelSize = 0.0016f;
        public string BoardTextForTest => _board?.Text ?? "";
        public bool BoardVisibleForTest => _board != null && _board.Visible;
        public string BoardStateForTest => $"on={_on} feed={HasFeed} mains={PowerNet.MainsLive} board={(_board != null)} shown={_boardShown} wm={(WeatherManager.Current != null)}";

        void BuildForecastBoard(Aabb aabb)
        {
            _board = new Label3D
            {
                Text = "",
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                PixelSize = BoardPixelSize,
                Position = _bodyCenterLocal + new Vector3(0f, aabb.Size.Y * 0.5f + 0.22f, 0f),
                Modulate = new Color(0.62f, 0.90f, 1f),
                NoDepthTest = false,   // ⚠ unlike the loot labels: this is world furniture, not a pick affordance,
                                       // and NoDepthTest would print it through the wall of the house it is in
                FontSize = 44,
                OutlineSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visible = false,
            };
            AddChild(_board);
        }

        /// <summary>Called from the device's own tick. Keeps the board in step with the radio's state and the
        /// day, and does nothing at all on the frames where neither changed.</summary>
        public void UpdateForecastBoard()
        {
            if (_board == null) return;
            bool show = _on && HasFeed && !_broken;   // a smashed set shows nothing, same gate the audio uses
            if (show != _boardShown) { _boardShown = show; _board.Visible = show; }
            if (!show) return;

            var wm = WeatherManager.Current;
            if (wm == null) { if (_board.Text.Length > 0) _board.Text = ""; return; }
            int day = wm.DayNumber;
            if (day == _boardDay && _board.Text.Length > 0) return;
            _boardDay = day;
            _board.Text = wm.ForecastLine;
        }

        void BuildAudio()
        {
            // Reuses the television's snow rather than shipping a second broadband-noise wav: it is the same sound,
            // and one file that both devices load is one file in the resource cache.
            var loop = PlayerController.LoadWavOneShot("res://content/sounds/tv_static.wav", loop: true);
            if (loop != null)
            {
                _static = new AudioStreamPlayer3D
                {
                    Stream = loop,
                    VolumeDb = Mathf.LinearToDb(StaticVolume),
                    UnitSize = StaticUnitSize,
                    MaxDistance = StaticMaxDist,
                    Position = _bodyCenterLocal,
                };
                AddChild(_static);
            }
            var on = PlayerController.LoadWavOneShot("res://content/sounds/tv_on.wav");
            if (on != null) { _onClick = new AudioStreamPlayer3D { Stream = on, VolumeDb = Mathf.LinearToDb(0.7f), UnitSize = 3f, MaxDistance = 16f, Position = _bodyCenterLocal }; AddChild(_onClick); }
            var off = PlayerController.LoadWavOneShot("res://content/sounds/tv_off.wav");
            if (off != null) { _offClick = new AudioStreamPlayer3D { Stream = off, VolumeDb = Mathf.LinearToDb(0.7f), UnitSize = 3f, MaxDistance = 16f, Position = _bodyCenterLocal }; AddChild(_offClick); }
        }

        // ---- power plug --------------------------------------------------------------------------------------------
        void BuildPlug(Aabb bodyLocal)
        {
            if (_plug != null) return;
            // Off the back face, a third of the way up -- derived from the body's own bounds rather than a measured
            // constant, because Radio_0 and Radio_1 are different sizes (TVDevice.PlugLocal makes the same argument
            // about its four cabinets).
            var pos = new Vector3(_bodyCenterLocal.X,
                                  bodyLocal.Position.Y + bodyLocal.Size.Y * 0.33f,
                                  bodyLocal.Position.Z);
            _plug = ConnectionPort.Create(this, new DeployableDef.Port
            {
                Kind = DeployableDef.PortKind.Consumer,
                Pos = pos,
                Watts = RadioWatts,
            }, "Radio");
            AddChild(_plug);
            _ports.Add(_plug);
            PowerNet.MarkDirty();
        }

        /// <summary>Player F-interact: flip the switch, click, refresh. The click plays with no power at all -- you
        /// still hear the switch on a dead set -- but the static only comes up if something is feeding it.</summary>
        public void Toggle()
        {
            // A smashed set takes no input. Without this the press LOOKS ignored (Refresh keeps it silent because
            // _broken gates the effective state) while still flipping _on, so the radio silently arms itself and
            // switches on by itself when the rubble resets. Exactly the trap TVDevice.Toggle documents.
            if (_broken) return;
            _on = !_on;
            (_on ? _onClick : _offClick)?.Play();
            Refresh();
        }

        /// <summary>Apply the effective state. Idempotent: safe to call from a grid sweep every tick.</summary>
        public void Refresh()
        {
            bool eff = _on && HasFeed && !_broken;
            _plugWasPowered = HasFeed;   // stamped BEFORE the early-out, so the feed poll cannot re-fire forever
            // ⚠ BOARD UPDATES HERE, BEFORE THE EARLY-OUT, and not only on the hub tick. Driving it from the
            // 30 Hz poll alone meant switching the set on left the display blank until the hub got round to
            // this node -- invisible on an idle machine, and a hard failure under load (caught by the test
            // passing alone and failing in a batch). A switch should light its own display.
            UpdateForecastBoard();
            if (eff == _playing) return;
            _playing = eff;
            if (_static == null) return;
            // Play() only works once the node is in the tree; _Ready starts it for a set that came up already on.
            if (_playing) { if (IsInsideTree() && !_static.Playing) _static.Play(); }
            else _static.Stop();
        }

        /// <summary>The prop was destroyed. Dead through any later grid sweep until the prop itself resets.</summary>
        public void SetBroken(bool broken)
        {
            if (_broken == broken) return;
            _broken = broken;
            Refresh();
        }

        public void SetLookFocused(bool on) => OutlineOverlay.ShowOutline(on, Colors.White, _outline);

        public override void _EnterTree() { TickHub.Add(this, HubTick, 30f); }
        public override void _ExitTree() { TickHub.Remove(this); }

        public override void _Ready()
        {
            if (_playing && _static != null && !_static.Playing) _static.Play();   // could not start before the tree
            AddToGroup("deployables");   // PowerNet gathers this group by IPowerDevice
            if (GetTree() is SceneTree tr && tr.GetNodesInGroup("powermgr").Count == 0)
            {
                var pm = new PowerManager();
                AddChild(pm);
                pm.AddToGroup("powermgr");
            }
            PowerNet.MarkDirty();
        }

        /// <summary>Poll the feed. The grid can move without anybody telling this device -- a generator running dry,
        /// PowerNet.SetGlobalPower, a wire cut -- and TVDevice learned the same lesson: a blackout left every set
        /// happily lit because nothing pushed the change down. 30 Hz off the hub is cheap.</summary>
        void HubTick(double dt)
        {
            if (HasFeed != _plugWasPowered) Refresh();
            UpdateForecastBoard();   // 30 Hz hub tick; the body early-outs unless the day or the switch changed
        }
    }
}
