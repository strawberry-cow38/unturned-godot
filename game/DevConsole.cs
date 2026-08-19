using Godot;
using System.Linq;
using SDG.Unturned;

namespace UnturnedGodot
{
    // Dev console (master). Press ` (backquote) to open a command bar at the top of the screen:
    //   give <item id|name>      -> spawns a WorldItem at the player's look-orb
    //   vehicle <id|name>        -> spawns that vehicle at the look-orb
    // TAB autocompletes (verb first, then the item/vehicle name); ENTER runs; ESC / ` closes.
    // While it's open the mouse is freed, which gates player look/movement the same way the inventory UI does.
    public partial class DevConsole : CanvasLayer
    {
        public PlayerController Player;

        // The world's Terrain, found by walking the tree rather than threaded in. There are two DevConsole
        // construction sites (WorldBuilder's playable path and AttachPlayerShell, which is static and has no
        // terrain in scope), and a joined client has neither -- so a lookup is one place that works on all of them
        // and cannot go stale when a third path appears.
        Terrain _terrCache;
        Terrain WorldTerrain
        {
            get
            {
                if (_terrCache != null && IsInstanceValid(_terrCache)) return _terrCache;
                Terrain Find(Node n)
                {
                    if (n is Terrain t) return t;
                    foreach (var c in n.GetChildren()) { var r = Find(c); if (r != null) return r; }
                    return null;
                }
                return _terrCache = Find(GetTree().Root);
            }
        }
        RoadField _roadsCache;
        RoadField WorldRoads
        {
            get
            {
                if (_roadsCache != null && IsInstanceValid(_roadsCache)) return _roadsCache;
                RoadField Find(Node n)
                {
                    if (n is RoadField r) return r;
                    foreach (var c in n.GetChildren()) { var f = Find(c); if (f != null) return f; }
                    return null;
                }
                return _roadsCache = Find(GetTree().Root);
            }
        }

        // MP (Phase 6, §2.3 "all state mutation goes through commands -- including DevConsole"): when this
        // process is a REMOTE client of some server, the state-mutating cheat verbs are sent as a
        // ConsoleCommand and the SERVER validates + applies them against its authoritative state; the
        // ConsoleResult event echoes the verdict back into this log. SP and the listen-server host leave it
        // null -- the local process IS the authority there, so the direct paths below stay byte-identical.
        public static UnturnedGodot.Net.NetWorldClient RemoteClient;
        // A3: the grid mains toggle is server-authoritative when a RemoteClient is set (joined client / consuming
        // loopback) -- it routes over the console plane like the cheats, but it's a legit mechanic the server runs
        // BEFORE its AllowCheats gate. The early toggleGlobalPower branch below does the routing; membership here
        // documents that the local process-global flip only happens on the pure-direct SP path (RemoteClient == null).
        static readonly string[] ServerGatedVerbs = { "give", "xp", "skill", "teleport", "tp", "toggleglobalpower", "globalpower", "grid" };
        // Verbs below the arg guard that are legal with NO argument. Keep this in step when adding one, or the
        // guard silently swallows it and the verb becomes unreachable from the console.
        static readonly string[] NoArgVerbs = { "unarmed", "fridge", "fluid", "survival", "spawnmagnetablecontainer", "magcontainer", "heliphys" };
        bool _resultHooked;

        LineEdit _input;
        Label _log;
        static readonly string[] Verbs = { "give", "vehicle", "spawnMagnetableContainer", "spawnheli", "spawntrain", "teleport", "plant", "skill", "xp", "hold", "deploy", "unarmed", "survival", "toggleGlobalPower", "toggleGlobalWater", "toggleBbat", "infFuel", "infAmmo", "wear", "unwear", "fluid", "date", "dateset", "whenBlackout", "triggerGlobalBrownout", "hurtmain", "killmain", "hurttail", "killtail", "profiler", "renderscale", "zshadows", "freezerigs", "vertexlight", "weather", "fridge", "fill", "empty", "units", "simspeed", "time", "timeset", "timeadd", "timespeed", "daylength", "hitbox", "heliphys" };
        static readonly EItemType[] ClothingTypes = { EItemType.SHIRT, EItemType.PANTS, EItemType.HAT, EItemType.VEST, EItemType.MASK, EItemType.GLASSES, EItemType.BACKPACK };
        readonly System.Collections.Generic.List<string> _history = new();
        int _histIdx;

        public override void _Ready()
        {
            Layer = 100;
            _log = new Label { Position = new Vector2(14, 10), Modulate = new Color(0.72f, 1f, 0.72f), Visible = false };
            _log.AddThemeFontSizeOverride("font_size", 15);
            AddChild(_log);
            _input = new LineEdit
            {
                PlaceholderText = "give <item> | vehicle <name>     (Tab autocompletes, Esc closes)",
                Visible = false,
                Size = new Vector2(820, 30),
                Position = new Vector2(14, 34),
            };
            AddChild(_input);
            _input.TextSubmitted += OnSubmit;
        }

        public override void _Input(InputEvent e)
        {
            if (e is not InputEventKey { Pressed: true } k) return;
            // Backquote, not F1: the function keys are vehicle seat selection (strawberry 2026-08-16,
            // "move the dev tools to be console commands instead"). The console cannot be a console
            // command that opens itself, so it is the one dev tool that still needs a key -- and ` is
            // where every other game puts it.
            if (k.Keycode == Key.Quoteleft) { Toggle(); GetViewport().SetInputAsHandled(); }
            else if (_input.Visible && k.Keycode == Key.Tab) { Autocomplete(); GetViewport().SetInputAsHandled(); }
            else if (_input.Visible && k.Keycode == Key.Escape) { Toggle(); GetViewport().SetInputAsHandled(); }
            else if (_input.Visible && k.Keycode == Key.Up) { HistoryNav(-1); GetViewport().SetInputAsHandled(); }     // up = older command
            else if (_input.Visible && k.Keycode == Key.Down) { HistoryNav(1); GetViewport().SetInputAsHandled(); }    // down = newer (or a blank line at the end)
        }

        void Toggle()
        {
            bool open = !_input.Visible;
            _input.Visible = open;
            _log.Visible = open;
            if (open) { _input.GrabFocus(); Input.MouseMode = Input.MouseModeEnum.Visible; }
            else
            {
                _input.ReleaseFocus(); _input.Clear();
                // Only recapture if nothing ELSE still wants the cursor. This used to recapture unconditionally,
                // so opening the inventory (Tab) then toggling the console twice left the mouse Captured with the
                // dashboard still drawn: the cursor vanished so the grid could not be clicked, while
                // PlayerController's polled input -- which gates on `MouseMode != Captured` -- came back to life
                // and walked the player around, auto-firing through the open UI on a held LMB. Review 2026-08-16.
                if (!(Player?.AnyBlockingUiOpen ?? false)) Input.MouseMode = Input.MouseModeEnum.Captured;
            }
        }

        void OnSubmit(string text)
        {
            var t = text.Trim();
            if (t.Length > 0 && (_history.Count == 0 || _history[^1] != t)) _history.Add(t);   // record for up/down recall (skip blanks + immediate dupes)
            _histIdx = _history.Count;   // reset the browse cursor to "past the newest"
            Run(t);
            _input.Clear();
            _input.GrabFocus();   // stay open for the next command
        }

        /// <summary>Test seam: run a command exactly as typing it would. Deliberately routed through Run rather
        /// than letting tests call the dev tools directly -- the thing most likely to break is the WIRING between
        /// a typed verb and the tool it drives, and a direct call skips precisely that.</summary>
        public void DebugRun(string cmd) => Run(cmd);

        /// <summary>Run a console line as if typed. Exists so tests can exercise the REAL dispatch -- including the
        /// no-arg guard, which silently swallowed spawnMagnetableContainer and made it look like an unknown command.
        /// "It compiles" and "the console runs it" are different claims and only this one checks the second.</summary>
        public void RunForTest(string cmd) => Run(cmd);

        void Run(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return;
            var parts = cmd.Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            // toggleGlobalPower [on|off]  -- SP grid mains switch (OFF by default); bare form FLIPS it. Mirrors
            // `survival`, but handled ABOVE the arg-length guard so the natural no-arg toggle works (the guard below
            // would otherwise short-circuit any no-arg command to a usage line). Not server-gated -- SP-only feature.
            if (verb == "toggleglobalpower" || verb == "globalpower" || verb == "grid")
            {
                // A3 (SP/MP-unify): on a joined client / consuming loopback (RemoteClient set) the mains toggle
                // is server-authoritative -- send it over the console plane so the server flips every GridSource
                // fixture's replicated ToggledOn + broadcasts, and each source node derives producing from that.
                // Pure-direct SP (RemoteClient == null) flips the process-global PowerNet flag locally, as before.
                if (RemoteClient != null)
                {
                    if (!_resultHooked)
                    {
                        _resultHooked = true;
                        RemoteClient.ConsoleResult += e => { if (IsInstanceValid(this)) Log(e.Text); };
                    }
                    Log(RemoteClient.SendConsole(cmd) ? "-> sent to server" : "not connected");
                    return;
                }
                string g = arg.ToLowerInvariant();
                bool on = g == "on" || g == "1" || g == "true" ? true
                        : g == "off" || g == "0" || g == "false" ? false
                        : !PowerNet.GlobalPower;
                PowerNet.SetGlobalPower(on);   // flips the flag + MarkDirty()s so the graph recomputes (Circuit_0 sources turn on/off)
                Log($"grid power {(PowerNet.GlobalPower ? "ON" : "OFF")}");
                return;
            }

            // hurtmain / killmain / hurttail / killtail  -- rotor damage on the heli you are CURRENTLY FLYING
            // (VoX 2026-08-16). Above the arg guard because all four take no argument. Deliberately requires
            // you to be in the seat: the point is to feel a rotor fail mid-flight, and a version that damaged
            // whatever you happened to be looking at would mostly be used from the ground, where a dead tail
            // rotor does nothing worth seeing.
            if (verb is "hurtmain" or "killmain" or "hurttail" or "killtail")
            {
                var heli = Player?.Driving;
                if (heli == null || !heli.IsHeli) { Log($"{verb}: you're not flying a helicopter"); return; }
                bool tail = verb.EndsWith("tail");
                if (verb.StartsWith("kill")) { if (tail) heli.KillTailRotor(); else heli.KillMainRotor(); }
                else if (tail) heli.DamageTailRotor(heli.TailRotorHealth * 0.5f + 1f);
                else heli.DamageMainRotor(heli.MainRotorHealth * 0.5f + 1f);
                Log($"{verb}: main {heli.MainRotorHealth:0} ({heli.MainRotorNorm * 100f:0}%), tail {heli.TailRotorHealth:0} ({heli.TailRotorNorm * 100f:0}%)"
                    + (heli.MainRotorDead ? " -- MAIN DEAD, no lift" : "") + (heli.TailRotorDead ? " -- TAIL DEAD, spinning" : ""));
                return;
            }

            // vertexlight [on|off] -- flip every material between per-pixel and per-vertex shading, to A/B
            // lighting cost on REAL hardware (this dev box is software-rasterised, so the measurement is
            // meaningless here). Reports how many materials changed, because a switch that silently matched
            // nothing looks exactly like one that worked.
            if (verb is "vertexlight" or "vertexlighting")
            {
                string vl = arg.ToLowerInvariant();
                GraphicsOptions.VertexShading = vl == "on" || vl == "1" || vl == "true" ? true
                                              : vl == "off" || vl == "0" || vl == "false" ? false
                                              : !GraphicsOptions.VertexShading;
                int changed = GraphicsOptions.ApplyShading(GetTree()?.Root);
                // Report what could NOT be converted alongside what was. A ShaderMaterial has no ShadingMode to
                // flip, and the terrain and grass are both ShaderMaterial -- so a big `changed` number on its own
                // still reads as "vertex lighting measured" when a large share of the frame never moved.
                int shaderOnly = GraphicsOptions.CountShaderMaterialRenderers(GetTree()?.Root);
                Log($"vertex lighting {(GraphicsOptions.VertexShading ? "ON" : "OFF")} -- {changed} material(s) changed"
                    + (changed == 0 ? " (nothing matched -- the switch did nothing, do not read the fps as a result)" : "")
                    + (shaderOnly > 0 ? $"; {shaderOnly} shader-material renderer(s) UNCHANGED (terrain/grass -- their lighting is in the shader, so this is a partial A/B)" : ""));
                return;
            }

            // profiler / renderscale / zshadows / freezerigs -- the old F3/F4/F5/F6 dev tools, moved to the
            // console so the function keys are free for vehicle seats (strawberry 2026-08-16). Above the arg
            // guard because every one of them is a bare no-arg toggle.
            if (verb is "profiler" or "fps")
            {
                var pr = Profiler.Instance;
                if (pr == null) { Log("profiler: no profiler in this scene"); return; }
                Log($"profiler overlay {(pr.ToggleOverlay() ? "ON" : "OFF")}");
                return;
            }
            if (verb is "renderscale" or "res3d")
            {
                var pr = Profiler.Instance;
                if (pr == null) { Log("renderscale: no profiler in this scene"); return; }
                Log($"3D render scale -> {pr.CycleRenderScale():0.##}x");
                return;
            }
            if (verb == "zshadows")
            {
                var pr = Profiler.Instance;
                if (pr == null) { Log("zshadows: no profiler in this scene"); return; }
                Log($"zombie rig shadows {(pr.ToggleZombieShadows() ? "ON" : "OFF")}");
                return;
            }
            if (verb is "freezerigs" or "animcut")
            {
                var ac = ZombieAnimCut.Instance;
                if (ac == null) { Log("freezerigs: no anim-cut overlay in this scene"); return; }
                Log($"rig animation {(ac.ToggleFrozen() ? "FROZEN" : "running")}");
                return;
            }

            // infFuel [on|off]  -- dev/playtesting: ALL cars stop burning fuel. ON by DEFAULT (master 2026-07-20);
            // bare form FLIPS it. Handled above the arg guard so the no-arg toggle works. SP-local static, not networked.
            // structures [count|save|load|clear]  -- inspect and drive the structure system without having to
            // build a base by hand every time you want to check persistence. Bare form reports the count, which
            // is the fastest way to tell "load did nothing" apart from "load worked and the base is elsewhere".
            if (verb == "structures" || verb == "struct")
            {
                var sm = StructureManager.Instance;
                if (sm == null) { Log("structures: no StructureManager in this world (build mode provisions one -- press B)"); return; }
                string a = arg.Trim().ToLowerInvariant();
                if (a == "save") { Log(sm.SaveToDisk() ? $"structures: saved {sm.Count} piece(s)" : "structures: SAVE FAILED"); return; }
                if (a == "load") { int n = sm.LoadFromDisk(); Log($"structures: loaded {n} piece(s)"); return; }
                if (a == "clear") { int had = sm.Count; sm.Clear(); Log($"structures: cleared {had} piece(s)"); return; }
                var byTier = new int[StructureCatalog.TierCount];
                foreach (var pc in sm.All) byTier[Mathf.Clamp(pc.Tier, 0, byTier.Length - 1)]++;
                var tierBits = new System.Collections.Generic.List<string>();
                for (int i = 0; i < byTier.Length; i++) if (byTier[i] > 0) tierBits.Add($"{byTier[i]} {StructureCatalog.TierAt(i).Name}");
                Log($"structures: {sm.Count} piece(s){(tierBits.Count > 0 ? " -- " + string.Join(", ", tierBits) : "")}");
                return;
            }

            if (verb == "inffuel")
            {
                string f = arg.ToLowerInvariant();
                Vehicle.InfiniteFuel = f == "on" || f == "1" || f == "true" ? true
                                     : f == "off" || f == "0" || f == "false" ? false
                                     : !Vehicle.InfiniteFuel;
                Log($"infFuel {(Vehicle.InfiniteFuel ? "ON -- cars won't burn fuel" : "OFF -- fuel drains normally")}");
                return;
            }

            // infAmmo [on|off]  -- dev/playtesting: the held gun's magazine refills after InfAmmoIdle seconds of not
            // firing (master: "refills your mag of the held weapon after 0.5s of not firing"). OFF by default, unlike
            // infFuel. Bare form FLIPS it, same shape as infFuel above. SP-local static, not networked, and consumes
            // nothing from the bag.
            if (verb == "infammo")
            {
                string a2 = arg.ToLowerInvariant();
                PlayerController.InfiniteAmmo = a2 == "on" || a2 == "1" || a2 == "true" ? true
                                              : a2 == "off" || a2 == "0" || a2 == "false" ? false
                                              : !PlayerController.InfiniteAmmo;
                Log($"infAmmo {(PlayerController.InfiniteAmmo ? $"ON -- held gun refills after {PlayerController.InfAmmoIdle:0.#}s without firing" : "OFF -- ammo drains normally")}");
                return;
            }

            if (verb == "units" || verb == "measurement")   // global client measurement system: metric | imperial | both (default both)
            {
                if (arg.Length > 0 && !Units.TrySet(arg)) { Log("usage: units <metric|imperial|both>"); return; }
                Log($"units = {Units.System}   (e.g. {Units.Length(100)} · {Units.SpeedKph(100)} · {Units.Temperature(20)})");
                return;
            }

            // toggleGlobalWater [on|off]  -- the MAINS. While ON, fire hydrants / water towers / sinks are infinite
            // supplies; while OFF they go inert and you are back to rain, rivers and storage (strawberry). ON by
            // default; bare form FLIPS it. Above the arg guard so the no-arg toggle works. SP-local static, not
            // networked -- the fluid fixtures are SP-local too (MP replication of map fluid IO is fast-follow).
            if (verb == "toggleglobalwater" || verb == "globalwater" || verb == "water")
            {
                string w = arg.ToLowerInvariant();
                FluidNet.SetGlobalWater(w == "on" || w == "1" || w == "true" ? true
                                      : w == "off" || w == "0" || w == "false" ? false
                                      : !FluidNet.GlobalWater);
                Log(FluidNet.GlobalWater
                    ? "mains water ON -- hydrants (tainted, 4 outlets) / towers (tainted) / sinks (clean) are infinite"
                    : "mains water OFF -- every municipal source is dead; rain, rivers and what you stored");
                return;
            }

            // toggleBbat [on|off]  -- battery back-up in the signal cabinets. TRAFFIC LIGHTS ONLY (strawberry: "i
            // dont want bback for street lights and lamps. its JUST for traffic lights") -- a real BBS is a signal
            // cabinet thing, street lighting just goes out with the grid. ON by default; bare form FLIPS it.
            // Above the arg guard so the no-arg toggle works. SP-local static, not networked.
            if (verb == "togglebbat")
            {
                string b = arg.ToLowerInvariant();
                TrafficLight.BatteryBackup = b == "on" || b == "1" || b == "true" ? true
                                           : b == "off" || b == "0" || b == "false" ? false
                                           : !TrafficLight.BatteryBackup;
                Log(TrafficLight.BatteryBackup
                    ? $"signal battery backup ON -- junctions breathe amber for {TrafficLight.BatteryDays:0.#} in-game days after a blackout, then flicker out"
                    : "signal battery backup OFF -- junctions die with the grid");
                return;
            }

            // --- time of day + sim speed (strawberry). Handled above the arg guard so bare `time` / `simSpeed` report state. ---
            // LIVE FLIGHT-MODEL KNOBS (VoX: "Can we test removing those please"). Reports the DERIVED
            // consequence of each setting, not just the number set -- "heave x0.5" means nothing on its own,
            // "terminal fall 43.6 m/s" is the thing being decided. Defaults are the shipping calibration, so
            // `heliphys reset` always returns the fleet to how it flies in a build nobody has typed into.
            if (verb == "heliphys")
            {
                var hpArgs = arg.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (hpArgs.Length == 0) { Log(HeliPhysStatus()); return; }
                string k = hpArgs[0].ToLowerInvariant();
                string val = hpArgs.Length > 1 ? hpArgs[1].ToLowerInvariant() : "";
                bool on = val is "on" or "1" or "true" or "yes";
                bool off = val is "off" or "0" or "false" or "no";
                switch (k)
                {
                    case "reset":
                        Vehicle.HeaveDampScale = 1f; Vehicle.DragScale = 1f;
                        Vehicle.BackstopEnabled = true; Vehicle.ShaftAlignedDescent = true;   // reset means the SHIPPING default, which is now on
                        Vehicle.HeaveRedirect = 0f;
                        Log("reset to shipping calibration\n" + HeliPhysStatus());
                        return;
                    case "redirect":
                        if (!float.TryParse(val, out float r) || r < 0f)
                        { Log("usage: heliphys redirect <0..1>   (0 = shipped, 1 = full sink-to-speed conversion)"); return; }
                        Vehicle.HeaveRedirect = r;
                        Log(HeliPhysStatus()); return;
                    case "heave":
                    case "drag":
                        // UPPER BOUND, because the damper is explicit Euler: heave is applied once per tick as
                        // -heave*vy, so the discrete pole is 1 - 0.45*x*dt. Past x ~ 133 at 60 Hz the vertical axis
                        // rings, and past ~267 it diverges outright -- a debug knob should not be able to blow up
                        // the integrator on a typo.
                        if (!float.TryParse(val, out float x) || x < 0f || x > 50f)
                        { Log($"usage: heliphys {k} <scale 0..50>   (1 = shipping, 0 = off entirely)"); return; }
                        if (k == "heave") Vehicle.HeaveDampScale = x; else Vehicle.DragScale = x;
                        Log(HeliPhysStatus()); return;
                    case "backstop":
                    case "shaft":
                        if (!on && !off) { Log($"usage: heliphys {k} <on|off>"); return; }
                        if (k == "backstop") Vehicle.BackstopEnabled = on; else Vehicle.ShaftAlignedDescent = on;
                        Log(HeliPhysStatus()); return;
                }
                Log("usage: heliphys [heave <scale> | drag <scale> | redirect <0..1> | backstop <on|off> | shaft <on|off> | reset]");
                return;
            }
            if (verb == "simspeed")
            {
                if (arg.Length == 0) { Log($"simSpeed = {(float)Engine.TimeScale:0.##}x   (usage: simSpeed <multiplier>)"); return; }
                if (!float.TryParse(arg, out float x) || x < 0f) { Log("usage: simSpeed <multiplier>  (e.g. 0.5, 1, 5)"); return; }
                Engine.TimeScale = x;
                Log($"sim speed = {x:0.##}x (whole simulation)");
                return;
            }
            if (verb == "time" || verb == "timeset" || verb == "timeadd" || verb == "timespeed" || verb == "daylength")
            {
                var dnc = DayNight();
                if (dnc == null) { Log("no day/night cycle in this scene"); return; }
                if (verb == "time")
                {
                    Log($"time {FormatTime(dnc.Time)}  ·  dayLength {dnc.DayLength / 60f:0.#} min  ·  timeSpeed {dnc.Speed:0.##}x  ·  simSpeed {(float)Engine.TimeScale:0.##}x");
                    return;
                }
                if (arg.Length == 0) { Log($"usage: {verb} <value>"); return; }
                if (verb == "timeset")
                {
                    if (!ParseClock(arg, out float h, allowNeg: false)) { Log($"bad time '{arg}' (try: noon, midnight, 8am, 6pm, 1800, 18:30)"); return; }
                    dnc.Time = Mathf.PosMod(h / 24f, 1f);
                    Log($"time set to {FormatTime(dnc.Time)}");
                }
                else if (verb == "timeadd")
                {
                    if (!ParseClock(arg, out float dh, allowNeg: true)) { Log($"bad amount '{arg}' (try: 2, 1:30, -3, 0200)"); return; }
                    int d0 = dnc.Day;
                    dnc.Advance(dh / 24f);   // laps midnight -> bumps Day -> fast-forwards food spoilage that many days
                    int laps = dnc.Day - d0;
                    Log($"time -> {FormatTime(dnc.Time)}  ({(dh >= 0 ? "+" : "")}{dh:0.##} h{(laps > 0 ? $", +{laps} day{(laps == 1 ? "" : "s")}" : "")})");
                }
                else if (verb == "timespeed")
                {
                    if (!float.TryParse(arg, out float sp) || sp < 0f) { Log("usage: timeSpeed <multiplier>  (0 = freeze the clock)"); return; }
                    dnc.Speed = sp;
                    Log($"time speed = {sp:0.##}x (day/night clock{(sp <= 0f ? " -- FROZEN" : "")})");
                }
                else // daylength
                {
                    if (!int.TryParse(arg, out int min) || min < 1) { Log("usage: dayLength <minutes>  (a whole minute count)"); return; }
                    dnc.DayLength = min * 60f;
                    Log($"day length = {min} min ({(min == 1 ? "1 minute" : $"{min} minutes")} per full cycle)");
                }
                return;
            }
            // date / dateset <day> / whenBlackout / triggerGlobalBrownout (strawberry) -- the lore blackout. Above the
            // arg guard so bare forms work: date=show the day, whenBlackout=report the doom day, triggerGlobalBrownout=
            // dip the whole grid off->on now, dateset=jump the day (to test the brownouts/blackout).
            if (verb == "date" || verb == "dateset" || verb == "whenblackout" || verb == "triggerglobalbrownout")
            {
                var dnc = DayNight();
                if (dnc == null) { Log("no day/night cycle in this scene"); return; }
                if (verb == "date") { Log($"day {dnc.Day}  {FormatTime(dnc.Time)}"); return; }
                if (verb == "whenblackout")
                {
                    int away = dnc.BlackoutDay - dnc.Day;
                    Log($"global blackout on day {dnc.BlackoutDay}  ({(away > 0 ? $"{away} day{(away == 1 ? "" : "s")} away" : away == 0 ? "TODAY" : "already happened")})");
                    return;
                }
                if (verb == "triggerglobalbrownout")
                {
                    dnc.TriggerGlobalBrownout();
                    Log("brownout -- streetlights flicker");
                    return;
                }
                if (arg.Length == 0) { Log($"usage: dateset <day>  (currently day {dnc.Day})"); return; }
                if (!int.TryParse(arg, out int nd) || nd < 0) { Log("usage: dateset <whole day number>"); return; }
                dnc.Day = nd;
                Log($"date set to day {nd}");
                return;
            }

            // fill <fluid>[:<flag>] [amount] / empty -- set the contents of the HELD fluid container, else the looked-at
            // placed tank (strawberry). Held bottle wins if both; a fitting (splitter/pump) has no tank -> not a target.
            if (verb == "fill" || verb == "empty")
            {
                if (Player == null) { Log("no player"); return; }
                bool held = Player.HoldingFluidContainer;
                bool tank = !held && Player.FocusedFluidTank != null;
                string tgt = held ? "held container" : "tank";
                if (!held && !tank) { Log("hold a fluid container, or look at a tank"); return; }
                if (verb == "empty") { if (held) Player.EmptyHeldContainer(); else Player.EmptyFocusedTank(); Log($"emptied the {tgt}"); return; }
                if (arg.Length == 0) { Log("usage: fill <fluid>[:<flag>] [amount]  (e.g. fill water:dirty 500, fill soda 1L)"); return; }
                var fp = arg.Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);
                var fq = fp[0].Split(':', 2);
                if (!FluidDef.TryParse(fq[0], out var type))
                { Log($"unknown fluid '{fq[0]}' (water, soda, cola, fuel, oil, gas, orangejuice, milk, coconut, energy, apple, grape, syrup, glue, chemicals)"); return; }
                var quality = WaterQuality.Clean;
                if (fq.Length > 1 && fq[1].Length > 0)
                {
                    switch (fq[1].Trim().ToLowerInvariant())
                    {
                        case "clean": quality = WaterQuality.Clean; break;
                        case "tainted": quality = WaterQuality.Tainted; break;
                        case "dirty": quality = WaterQuality.Dirty; break;
                        default: Log($"unknown flag '{fq[1]}' (clean, tainted, dirty)"); return;
                    }
                }
                float ml = -1f;   // no amount -> fill to capacity
                if (fp.Length > 1 && !ParseVolume(fp[1], out ml)) { Log($"bad amount '{fp[1]}' (mL, or with an L suffix: 500, 1.5L)"); return; }
                if (held) Player.FillHeldContainer(type, quality, ml); else Player.FillFocusedTank(type, quality, ml);
                Log($"filled the {tgt}: {(ml < 0f ? "full" : FluidDef.Litres(ml))} of {FluidDef.WaterName(type, quality)}");
                return;
            }

            // NO-ARG VERBS LIVE BELOW THIS LINE, so the guard only fires for verbs that genuinely need an argument.
            // It used to sit above `unarmed`, `fridge`, `fluid` and `survival`, which take none -- typing `unarmed`
            // printed "usage: give <item> | vehicle <name>" and EquipUnarmed was unreachable from the console
            // entirely. The verbs handled above the guard already carry a comment about this exact hazard; these
            // four were added underneath it. Review 2026-08-16.
            if (arg.Length == 0 && System.Array.IndexOf(NoArgVerbs, verb) < 0)
            {
                // NAME THE VERB. The old text was a fixed "usage: give | vehicle" line, so a no-arg verb missing from
                // NoArgVerbs looked to the user like the console simply did not know the command -- which is exactly
                // how strawberry reported it ("i dont think the console is recognizing the command") when
                // spawnMagnetableContainer landed here. Echoing the verb back makes the failure self-diagnosing:
                // "it wants an argument" and "it does not exist" stop looking identical.
                Log(System.Array.IndexOf(Verbs, verb) >= 0 || Verbs.Any(v => v.ToLowerInvariant() == verb)
                    ? $"{verb}: needs an argument (or add it to NoArgVerbs if it should run bare)"
                    : $"unknown command '{verb}' -- try: give <item> | vehicle <name>");
                return;
            }

            if (RemoteClient != null && System.Array.IndexOf(ServerGatedVerbs, verb) >= 0)
            {
                // teleport (#27): the server is authoritative for position but has no map/location table
                // (MapNodes is game-side), so resolve the name -> coords HERE and send the numeric form
                // RunConsole parses. The old local Player.TeleportTo never moved the server entity, so the
                // reconciler dragged the shell straight back -- the MP "teleport doesn't stick" snapback.
                if ((verb == "teleport" || verb == "tp") && !TryResolveTeleport(arg, out cmd, out _))
                { Log($"no location '{arg}'"); return; }
                if (!_resultHooked)
                {
                    _resultHooked = true;
                    RemoteClient.ConsoleResult += e => { if (IsInstanceValid(this)) Log(e.Text); };
                }
                Log(RemoteClient.SendConsole(cmd) ? "-> sent to server" : "not connected");
                return;
            }

            Vector3 at = Player?.LookPoint() ?? Vector3.Zero;

            if (verb == "give")
            {
                var asset = ResolveItem(arg);
                if (asset == null) { Log($"no item matching '{arg}'"); return; }
                var item = SDG.Unturned.Assets.makeLoot(asset.id);   // magazines come full, etc.
                if (Player?.Inventory != null && Player.Inventory.tryAddItem(item))   // into the bag if there's room (master)
                    Log($"gave {asset.itemName} (#{asset.id}) -> bag");
                else { Player?.DropWorldItem(item, at + Vector3.Up * 2f); Log($"gave {asset.itemName} (#{asset.id}) -> dropped in the air above the orb"); }   // else spawn it in the air over the look-orb
            }
            else if (verb == "fridge")   // demo: a Refrigerator wired + powered by its own generator + a plain Crate, each seeded with perishables, to see preservation
            {
                var world = Player?.GetParent() ?? GetTree().Root;
                var fridge = Refrigerator.Spawn(world, at + Vector3.Right * 0.8f);
                var crate  = StorageCrate.Spawn(world, at - Vector3.Right * 0.8f);
                var gen = Deployable.Spawn(world, DeployableDef.Generator, at + Vector3.Back * 3f, 0f);   // a generator to power the fridge
                var genOut = gen.Ports.Find(pp => pp.Kind == DeployableDef.PortKind.Output);
                if (genOut != null && fridge.ConsumerPort != null)
                {
                    var wr = new Wire(); world.AddChild(wr); wr.Source = genOut; wr.Consumer = fridge.ConsumerPort; wr.AddToGroup("wires");
                    wr.SetPoints(new System.Collections.Generic.List<Vector3> { genOut.GlobalPosition, fridge.ConsumerPort.GlobalPosition }, valid: true);
                }
                gen.TogglePower(); PowerNet.Recompute(GetTree());
                for (int i = 0; i < 3; i++) { fridge.Add(new SDG.Unturned.Item(329, 1, 100)); crate.Add(new SDG.Unturned.Item(329, 1, 100)); }   // 3 fresh carrots each
                Log($"spawned a Refrigerator wired to + powered by a generator (powered={fridge.Preserves}) + a plain Crate, each with 3 fresh carrots. timeAdd 240 then F-open both: the crate's rot, the fridge's stay fresh (F the generator to cut its power and the fridge spoils too).");
            }
            else if (verb == "vehicle" || verb == "veh")
            {
                string name = Vehicle.SpecNames.FirstOrDefault(n => n.Equals(arg, System.StringComparison.OrdinalIgnoreCase))
                           ?? Vehicle.SpecNames.FirstOrDefault(n => n.StartsWith(arg, System.StringComparison.OrdinalIgnoreCase));
                if (name == null) { Log($"no vehicle '{arg}' (try: {string.Join(", ", Vehicle.SpecNames)})"); return; }
                var v = Vehicle.BuildByName(name, (int)(GD.Randi() % 8));
                (Player?.GetParent() ?? GetTree().Root).AddChild(v);
                // A wheeled vehicle is DROPPED and lets its suspension sort out the landing. A helicopter has no
                // suspension: seat it exactly on its skids instead, or it either bangs down from 1.5 m or spawns
                // with them already through the terrain (which the solver answers by launching it).
                if (v.IsHeli || (v.IsPlane && v.HasWheels)) v.PlaceOnGround(at);   // no suspension (heli) / a long low airframe on gear (wheeled plane): SEAT it exactly instead of dropping 1.5m, which bounced/clipped it. A FLOATPLANE keeps the drop (buoyancy catches it on the water; PlaceOnGround could seat it under the surface).
                else v.GlobalPosition = at + Vector3.Up * 1.5f;
                Log($"spawned {name}" + (v.IsHeli ? $" (seated on skids, {v.GroundClearance:0.##} m clearance -- F to board, W/S collective, A/D yaw, mouse to fly)"
                                                 : v.IsPlane ? " (F to board -- W/S throttle, A/D rudder, mouse pitch/roll, hold Ctrl to taxi; floatplane needs water, wheeled needs runway)" : ""));
            }
            else if (verb == "spawnheli")
            {
                string hname = Vehicle.SpecNames.FirstOrDefault(n => n.Equals(arg, System.StringComparison.OrdinalIgnoreCase))
                            ?? Vehicle.SpecNames.FirstOrDefault(n => n.StartsWith(arg, System.StringComparison.OrdinalIgnoreCase));
                if (hname == null) { Log($"no vehicle '{arg}'"); return; }
                var probe = Vehicle.BuildByName(hname);
                bool isHeli = probe.IsHeli; probe.QueueFree();
                if (!isHeli) { Log($"'{hname}' is not a helicopter"); return; }
                var ai = NpcHeli.Spawn(Player?.GetParent() ?? GetTree().Root, hname, WorldTerrain, at);
                if (ai == null) { Log("no map nodes to fly to (nodes.tsv empty?)"); return; }
                Log($"npc {hname} inbound from the map edge -> {ai.TargetName}, holding {NpcHeli.CanopyClearance:0} m over the terrain, will circle at {NpcHeli.OrbitRadius:0} m");
            }
            else if (verb == "spawntrain")
            {
                var roads = WorldRoads;
                if (roads == null) { Log("no road network in this world"); return; }
                Vector3 near = Player?.GlobalPosition ?? at;
                string ttype = string.IsNullOrWhiteSpace(arg) ? "engine" : arg;
                var tr = Train.Spawn(Player?.GetParent() ?? GetTree().Root, roads, near, ttype);
                if (tr == null) { Log(Train.ResolveType(ttype) == null ? $"unknown car '''{arg}''' -- types: {Train.TypeList}" : "no train track nearby -- only Yukon has rails (tracks = road material 4)"); return; }
                Log($"spawned a {Train.ResolveType(ttype)} car on the nearest track (drive an engine into it to couple)");
            }
            else if (verb == "spawnmagnetablecontainer" || verb == "magcontainer")
            {
                // Spawned like a vehicle (dropped just above the ground and left to settle) rather than seated:
                // it is a plain rigid body with no suspension or skids to place on, so a short drop is the honest
                // way in and matches how the wheeled vehicles arrive.
                // `at` is the LOOK POINT, and the container is 7.5 m long CENTRED on its origin -- so spawning it
                // straight there drops half of it behind the aim point and through whoever typed the command. Push it
                // out along the look direction by half its length (plus clearance) so the near face lands beyond the
                // aim rather than on the player.
                Vector3 eye = Player?.GlobalPosition ?? Vector3.Zero;
                Vector3 outward = new Vector3(at.X - eye.X, 0f, at.Z - eye.Z);
                outward = outward.LengthSquared() > 0.01f ? outward.Normalized() : Vector3.Forward;
                Vector3 spot = at + outward * 4.5f + Vector3.Up * 1.2f;
                var c = MagnetableContainer.Spawn(Player?.GetParent() ?? GetTree().Root, spot);
                Log($"spawned magnetable container ({MagnetableContainer.ContainerMass:0} kg) {spot.DistanceTo(eye):0.#} m ahead -- doors + a fixed magnet point on the roof centre; fly the skycrane over it and hit Shift");
            }
            else if (verb == "teleport" || verb == "tp")
            {
                // Every other verb guards on a null Player; this one used `Player?.` and then logged success
                // UNCONDITIONALLY -- so "teleported to X" printed whether the move happened, whether it was undone a
                // tick later, or whether nothing was called at all. strawberry could only report "it doesn't work",
                // because the console said the same thing in every failure mode.
                if (Player == null) { Log("no player"); return; }
                var m = MapNodes.Locations.Where(n => n.Name.Replace(" ", "").StartsWith(arg.Replace(" ", ""), System.StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Name.Length).ToList();
                if (m.Count == 0) { Log($"no location '{arg}' (in {MapNodes.MapNodeFile})"); return; }
                var dest = m[0].Pos + Vector3.Up * 3f;
                var from = Player.TruePhysicsPosition;
                Player.TeleportTo(dest);                          // teleport the vehicle if driving, else the player (master)
                // Report what ACTUALLY happened, against the physics-truth position rather than the interpolated
                // visual one. A teleport that gets dragged back reports the snap-back instead of claiming success, and
                // the node file is named so a location resolved from the WRONG MAP's table is visible on sight.
                var now = Player.TruePhysicsPosition;
                string where = $"{m[0].Name} ({dest.X:0}, {dest.Y:0}, {dest.Z:0})";
                if (now.DistanceTo(dest) > 1.5f)
                    Log($"teleport to {where} DID NOT TAKE -- still at ({now.X:0}, {now.Y:0}, {now.Z:0}); riding={Player.IsRidingForDebug}");
                else
                    Log($"teleported to {where} from ({from.X:0}, {from.Y:0}, {from.Z:0})  ·  {MapNodes.MapNodeFile}");
            }
            else if (verb == "plant")
            {
                // plant <crop> [grown]  -- spawn a growing crop at the look point (or already grown, for harvest testing)
                var pp = arg.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                bool grown = pp.Length > 1 && (pp[1].Equals("grown", System.StringComparison.OrdinalIgnoreCase) || pp[1] == "1");
                if (!CropManager.Active)
                {
                    // A4 joined client: no CropManager (the server owns growth) -- route the plant over the wire.
                    // Resolve the crop name -> seed id HERE (CropRegistry is game-side) and send the seed the
                    // server's schema plants. `grown` is a listen-server/UG_FARMSPEED aid, N/A here -> ignored.
                    if (RemoteClient != null)
                    {
                        if (!CropRegistry.TryByName(pp[0].ToLowerInvariant(), out var cd) || cd.SeedId == 0)
                        { Log($"no crop '{pp[0]}' (try: carrot, corn, wheat, potato, tomato, pumpkin...)"); return; }
                        Log(RemoteClient.SendPlantCrop(cd.SeedId, new UnityEngine.Vector3(at.X, at.Y, at.Z))
                            ? $"-> plant {pp[0]} sent to server" : "not connected");
                        return;
                    }
                    Log("no crop manager in this scene"); return;
                }
                var crop = CropManager.Plant(pp[0], at, grown);
                if (crop == null) { Log($"no crop '{pp[0]}' (try: carrot, corn, wheat, potato, tomato, pumpkin...)"); return; }
                Log($"planted {pp[0]}{(grown ? " (grown)" : "")} -- UG_FARMSPEED speeds growth; E near a grown crop to harvest");
            }
            else if (verb == "skill")
            {
                // skill <name> [level]  -- set a skill's level (e.g. `skill crafting 3`, `skill agriculture`) for testing gates/effects
                if (Player?.Skills == null) { Log("no player skills"); return; }
                var pp = arg.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (!Player.Skills.TryFind(pp[0], out var sk, out var label)) { Log($"no skill '{pp[0]}' (try: crafting, agriculture, sharpshooter, strength...)"); return; }
                int target = pp.Length > 1 && int.TryParse(pp[1], out var lv) ? lv : sk.level + 1;
                sk.level = (byte)Mathf.Clamp(target, 0, sk.max);
                Log($"{label} skill -> level {sk.level}/{sk.max}");
            }
            else if (verb == "hold")
            {
                // hold <consumable>  -- equip a consumable (food/drink) to the hands; LMB eats/drinks it. (mesh must be extracted)
                var asset = ResolveItem(arg);
                if (asset == null) { Log($"no item '{arg}'"); return; }
                string mesh = asset.itemName.ToLowerInvariant().Replace(" ", "_");
                Player.EquipHeldConsumable(asset, mesh);
                Log($"holding {asset.itemName} -- LMB to eat/drink");
            }
            else if (verb == "xp")
            {
                // xp <n>  -- grant n experience to spend in the skills menu (J). For testing the leveling economy.
                if (Player?.Skills == null) { Log("no player skills"); return; }
                if (!uint.TryParse(arg.Split(' ')[0], out var amt)) { Log("usage: xp <amount>"); return; }
                Player.Skills.AwardExperience(amt);
                Log($"+{amt} XP (now {Player.Skills.experience}) -- open skills with J");
            }
            else if (verb == "deploy")
            {
                // deploy <name|id>  -- hold any registered deployable; aim shows a blue(valid)/red(invalid) ghost, LMB plants it
                string a = arg.Trim().ToLowerInvariant().Replace(" ", "");
                DeployableDef def = a.StartsWith("gen") ? DeployableDef.Generator
                                  : a.StartsWith("spot") ? DeployableDef.Spotlight
                                  : (a == "split2" || a == "splitter2") ? DeployableDef.Splitter2
                                  : (a == "split3" || a == "splitter3") ? DeployableDef.Splitter3
                                  : (a == "split4" || a == "splitter4") ? DeployableDef.Splitter4
                                  : (a == "combine2" || a == "combiner2") ? DeployableDef.Combiner2
                                  : DeployableDef.All.FirstOrDefault(d => a == d.Id.ToString() || d.Name.ToLowerInvariant().Replace(" ", "").Contains(a));   // any deployable by id or name (battery/switch/windturbine/future)
                if (def == null) { Log("usage: deploy <" + string.Join("|", DeployableDef.All.Select(d => d.Name.ToLowerInvariant().Replace(" ", ""))) + ">"); return; }
                if (Player == null) { Log("no player"); return; }
                Player.EquipHeldDeployable(def);
                Log($"holding {def.Name} -- aim (blue=ok / red=blocked), LMB to place");
            }
            else if (verb == "weather")
            {
                // weather [clear|rain|heavy|lightning]  -- src CommandWeather. No arg = report the forecast.
                // Weather is normally SCHEDULED off PEI's Weather_Types table, so this is how you see it on demand
                // instead of waiting out a 2.3-5.6 day-cycle forecast.
                var wm = GetTree().GetNodesInGroup("weather").Count > 0 ? GetTree().GetNodesInGroup("weather")[0] as WeatherManager : null;
                if (wm == null || wm.Sim == null) { Log("no weather manager in this world"); return; }
                string a = (arg ?? "").Trim();
                if (a.Length == 0)
                {
                    var act = wm.Sim.Active;
                    Log(act == null
                        ? $"clear -- next weather in {wm.Sim.ForecastTimer:0}s"
                        : $"{act.Value.Name} -- stage {wm.Sim.Stage}, blend {wm.Sim.BlendAlpha:0.00}, {wm.Sim.ActiveTimer:0}s left");
                    return;
                }
                if (wm.ApplyCommand(a)) Log($"weather -> {a}");
                else Log("usage: weather [clear|rain|heavy|lightning]");
            }
            else if (verb == "unarmed")
            {
                // unarmed  -- go to the bare-fists state (LMB weak / RMB strong punch)
                if (Player == null) { Log("no player"); return; }
                Player.EquipUnarmed();
                Log("unarmed -> fists (LMB/RMB to punch)");
            }
            else if (verb == "fluid")
            {
                // fluid [split|combine]  -- debug fluid-IO rigs, then equip the hose tool. Aim a green/cyan port -> LMB ->
                // aim an orange port -> LMB. Bare = source + empty + water tanks; split = source->splitter->2 tanks;
                // combine = 2 sources->combiner->1 tank. Fittings sit between (source ABOVE fitting ABOVE tanks, gravity).
                if (Player == null) { Log("no player"); return; }
                var world = Player.GetParent();
                if (world == null) { Log("no world"); return; }
                if (GetTree().GetNodesInGroup("fluid_managers").Count == 0) world.AddChild(new FluidManager());
                Vector3 p = Player.GlobalPosition;
                Vector3 fwd = -Player.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
                Vector3 right = Player.GlobalTransform.Basis.X; right.Y = 0f; right = right.Normalized();
                string fa = arg.Trim().ToLowerInvariant();
                if (fa.StartsWith("valve"))
                {
                    // valve rig (WORLD X): a high SOURCE + a VALVE + a low tank. Gravity feeds it; hose source->valve->tank,
                    // then RMB the valve's port (with the hose tool out) to open/close it — the handle goes green/red.
                    Vector3 c = p + fwd * 5f; Vector3 wx = Vector3.Right;
                    var valve = FluidContainer.MakeValve(); valve.Position = c + Vector3.Up * 1.0f; world.AddChild(valve);
                    SpawnFluidRig(world, FluidRole.Source, FluidType.Fuel, 2000f, c - wx * 4f + Vector3.Up * 1.6f, valve.Position);   // source WEST, high
                    SpawnFluidRig(world, FluidRole.Storage, FluidType.None, 0f, c + wx * 4f, valve.Position);   // tank EAST, low
                    Log("valve rig (WORLD X): a high fuel SOURCE + a VALVE + a low empty tank. hose source->the valve's LEFT (orange) input, then its RIGHT (cyan) output->the tank. RMB the valve's port to open/close it (handle = green open / red closed).");
                }
                else if (fa.StartsWith("refine") || fa.StartsWith("sluice"))
                {
                    // transformer rig (WORLD X): a high source of the INPUT fluid + a refinery/sluice + a low output tank.
                    // refine = oil->gas; sluice = water->dirty water. Hose source->transformer input, then output->the tank.
                    bool sl = fa.StartsWith("sluice");
                    var inT = sl ? FluidType.Water : FluidType.Oil;
                    var outT = sl ? FluidType.Water : FluidType.Gas;   // sluice now outputs DIRTY-flagged WATER (via DirtiesWater), not a DirtyWater type
                    Vector3 c = p + fwd * 5f; Vector3 wx = Vector3.Right;
                    var xf = FluidContainer.MakeTransformer(inT, outT, 50f, 1f); xf.DirtiesWater = sl; xf.Position = c + Vector3.Up * 1.0f; world.AddChild(xf);
                    SpawnFluidRig(world, FluidRole.Source,  inT,          2000f, c - wx * 4f + Vector3.Up * 2.4f, xf.Position);   // input source WEST, high
                    SpawnFluidRig(world, FluidRole.Storage, FluidType.None,  0f, c + wx * 4f, xf.Position);                       // output tank EAST, low (adopts out fluid)
                    Log($"{(sl ? "sluice" : "refine")} rig (WORLD X): a {FluidDef.Name(inT)} SOURCE (west, high) + a TRANSFORMER + an empty tank (east, low). hose source->the transformer's LEFT (orange) input, then its RIGHT (green) output->the tank. it deletes {FluidDef.Name(inT)} and makes {FluidDef.Name(outT)}.");
                }
                else if (fa.StartsWith("pump"))
                {
                    // pump rig (WORLD X): a low SOURCE + an electric PUMP + a HIGH tank + a generator wired to the pump.
                    // Once the generator spins up the pump lifts fluid UPHILL to the high tank. Hose src->pump, pump->tank.
                    Vector3 c = p + fwd * 5f; Vector3 wx = Vector3.Right;
                    var pump = FluidPump.Make(6f); pump.Position = c; world.AddChild(pump);
                    SpawnFluidRig(world, FluidRole.Source,  FluidType.Fuel, 2000f, c - wx * 4f, c);             // source WEST, low
                    SpawnFluidRig(world, FluidRole.Storage, FluidType.None,    0f, c + wx * 4f + Vector3.Up * 3f, c);   // tank EAST, HIGH (3m up)
                    var gen = Deployable.Spawn(world, DeployableDef.Generator, c + Vector3.Back * 3f, 0f);      // a generator to power the pump
                    var genOut = gen.Ports.Find(pp => pp.Kind == DeployableDef.PortKind.Output);
                    if (genOut != null && pump.PowerPorts.Count > 0)
                    {
                        var wr = new Wire(); world.AddChild(wr); wr.Source = genOut; wr.Consumer = pump.PowerPorts[0]; wr.AddToGroup("wires");
                        wr.SetPoints(new System.Collections.Generic.List<Vector3> { genOut.GlobalPosition, pump.PowerPorts[0].GlobalPosition }, valid: true);
                    }
                    gen.TogglePower(); PowerNet.Recompute(GetTree());
                    Log("pump rig (WORLD X): low fuel SOURCE (west) + an electric PUMP + a HIGH empty tank (east, 3m up) + a wired generator. once the gen spins up (~1s) the pump lifts fluid UPHILL. hose source->pump input, then pump output->the high tank.");
                }
                else if (fa.StartsWith("purif"))
                {
                    // purify rig (WORLD X): a TAINTED water SOURCE (west) + a powered PURIFIER + an empty tank (east) + a
                    // wired generator. Once the gen powers the purifier, tainted water in -> CLEAN water out. Hose src->purifier, purifier->tank.
                    Vector3 c = p + fwd * 5f; Vector3 wx = Vector3.Right;
                    var purifier = FluidPurifier.Make(); purifier.Position = c + Vector3.Up * 1.0f; world.AddChild(purifier);
                    SpawnFluidRig(world, FluidRole.Source,  FluidType.Water, 2000f, c - wx * 4f + Vector3.Up * 2.0f, purifier.Position, WaterQuality.Tainted);   // TAINTED source WEST, high
                    SpawnFluidRig(world, FluidRole.Storage, FluidType.None,     0f, c + wx * 4f, purifier.Position);   // clean tank EAST, low
                    var gen = Deployable.Spawn(world, DeployableDef.Generator, c + Vector3.Back * 3f, 0f);
                    var genOut = gen.Ports.Find(pp => pp.Kind == DeployableDef.PortKind.Output);
                    if (genOut != null && purifier.PowerPorts.Count > 0)
                    {
                        var wr = new Wire(); world.AddChild(wr); wr.Source = genOut; wr.Consumer = purifier.PowerPorts[0]; wr.AddToGroup("wires");
                        wr.SetPoints(new System.Collections.Generic.List<Vector3> { genOut.GlobalPosition, purifier.PowerPorts[0].GlobalPosition }, valid: true);
                    }
                    gen.TogglePower(); PowerNet.Recompute(GetTree());
                    Log("purify rig (WORLD X): a TAINTED water SOURCE (west) + a powered PURIFIER + an empty tank (east) + a wired generator. hose source->the purifier's LEFT (orange) input, then its RIGHT (green) output->the tank -> CLEAN water. cut the gen's power and it stops cleaning.");
                }
                else if (fa.StartsWith("split") || fa.StartsWith("combine"))
                {
                    // fittings have fixed local ±X port faces (input -X / output +X), so lay the rig along WORLD X near
                    // the player: source(s) WEST + high, fitting centre, storage(s) EAST + low. Look west/east to aim.
                    Vector3 c = p + fwd * 5f; Vector3 wx = Vector3.Right, wz = Vector3.Back;
                    Vector3 fit = c + Vector3.Up * 1.0f;
                    if (fa.StartsWith("split"))
                    {
                        SpawnFluidFitting(world, FluidRole.Splitter, 2, fit);
                        SpawnFluidRig(world, FluidRole.Source,  FluidType.Fuel, 2000f, c - wx * 4f + Vector3.Up * 2.4f, fit);
                        SpawnFluidRig(world, FluidRole.Storage, FluidType.None,    0f, c + wx * 4f + wz * -1.5f, fit);
                        SpawnFluidRig(world, FluidRole.Storage, FluidType.None,    0f, c + wx * 4f + wz *  1.5f, fit);
                        Log("split rig (laid along WORLD X): fuel SOURCE (west, high) + a SPLITTER (cyan outputs) + two empty STORAGES (east, low). hose source->splitter input, then each cyan output->a tank.");
                    }
                    else
                    {
                        SpawnFluidFitting(world, FluidRole.Combiner, 2, fit);
                        SpawnFluidRig(world, FluidRole.Source,  FluidType.Fuel, 2000f, c - wx * 4f + Vector3.Up * 2.4f + wz * -1.5f, fit);
                        SpawnFluidRig(world, FluidRole.Source,  FluidType.Fuel, 2000f, c - wx * 4f + Vector3.Up * 2.4f + wz *  1.5f, fit);
                        SpawnFluidRig(world, FluidRole.Storage, FluidType.None,    0f, c + wx * 4f, fit);
                        Log("combine rig (laid along WORLD X): two fuel SOURCES (west, high) + a COMBINER + an empty STORAGE (east, low). hose each source->a combiner input, then the cyan output->the tank.");
                    }
                }
                else
                {
                    SpawnFluidRig(world, FluidRole.Source,  FluidType.Fuel,  1000f, p + fwd * 4f + Vector3.Up * 1.6f, p);   // raised so gravity feeds
                    SpawnFluidRig(world, FluidRole.Storage, FluidType.None,     0f, p + fwd * 4f + right * 2f, p);          // empty -> adopts + fills
                    SpawnFluidRig(world, FluidRole.Storage, FluidType.Water,  200f, p + fwd * 4f - right * 2f, p);          // water -> "cannot mix fluids"
                    Log("fluid rig: raised fuel SOURCE + empty STORAGE + water STORAGE. hose tool equipped -> LMB a green port then an orange port (water tank rejects: 'cannot mix fluids').");
                }
                Player.EquipHoseTool();
            }
            else if (verb == "survival" || verb == "hunger")
            {
                // survival [on|off]  -- toggle hunger/thirst drain (OFF by default). Bare `survival` flips it.
                string a = arg.Trim().ToLowerInvariant();
                PlayerController.SurvivalDrain = a == "on" || a == "1" || a == "true" ? true
                                               : a == "off" || a == "0" || a == "false" ? false
                                               : !PlayerController.SurvivalDrain;
                Log($"hunger/thirst {(PlayerController.SurvivalDrain ? "ENABLED" : "disabled")}");
            }
            else if (verb == "wear")
            {
                // wear <clothing item id|name>  -- equip clothing (shirt/pants -> body paint; hat/vest/... -> bone mesh)
                var asset = ResolveItem(arg);
                if (asset == null) { Log($"no item matching '{arg}'"); return; }
                if (System.Array.IndexOf(ClothingTypes, asset.type) < 0) { Log($"{asset.itemName} (#{asset.id}) is {asset.type}, not clothing"); return; }
                Player?.WearClothing(new Item(asset.id));
                Log($"wearing {asset.itemName} (#{asset.id}) [{asset.type}]");
            }
            else if (verb == "unwear")
            {
                // unwear <slot>  -- remove a worn slot (shirt|pants|hat|vest|mask|glasses|backpack)
                if (!System.Enum.TryParse<EItemType>(arg.Trim(), true, out var slot) || System.Array.IndexOf(ClothingTypes, slot) < 0)
                { Log("usage: unwear <shirt|pants|hat|vest|mask|glasses|backpack>"); return; }
                Player?.UnwearClothing(slot);
                Log($"removed {slot.ToString().ToLowerInvariant()}");
            }
            else if (verb == "hitbox" || verb == "hb")
            {
                // hitbox client|server|off -- collision wireframe overlays (cyan = this process's colliders,
                // magenta = the server's, reconstructed from replicas). Client-LOCAL debug: never server-gated.
                Log(HitboxDebugOverlay.Console(arg, GetTree()));
            }
            else Log($"unknown command '{verb}' -- give / vehicle / teleport / plant / skill / xp / hold / deploy / unarmed / survival / toggleGlobalPower / wear / unwear");
        }

        /// <summary>MP teleport (#27): location name -> the numeric `teleport <x> <y> <z>` wire form
        /// ServerTransactions.RunConsole parses (coords over the EXISTING console command -- no protocol
        /// change). Same prefix match as the local path, and the same +3 up drop-height so the server-side
        /// landing matches SP.</summary>
        public static bool TryResolveTeleport(string arg, out string serverCmd, out string locationName)
        {
            serverCmd = locationName = null;
            var m = MapNodes.Locations.Where(n => n.Name.Replace(" ", "").StartsWith(arg.Replace(" ", ""), System.StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Name.Length).ToList();
            if (m.Count == 0) return false;
            var pos = m[0].Pos + Vector3.Up * 3f;
            locationName = m[0].Name;
            serverCmd = string.Format(System.Globalization.CultureInfo.InvariantCulture, "teleport {0:0.##} {1:0.##} {2:0.##}", pos.X, pos.Y, pos.Z);
            return true;
        }

        // Spawn one debug fluid container at `pos`, its hose port cube on the face toward `facePlayer` so it's aimable.
        void SpawnFluidRig(Node world, FluidRole role, FluidType type, float amount, Vector3 pos, Vector3 facePlayer, WaterQuality quality = WaterQuality.Clean)
        {
            var c = FluidContainer.Make(role, new FluidTank(type, 2000f, amount, quality), 50f);
            c.Position = pos;
            Vector3 toP = facePlayer - pos; toP.Y = 0f;
            if (toP.LengthSquared() > 1e-4f) { toP = toP.Normalized(); c.PortLocalPos = new Vector3(toP.X * 0.55f, 0.9f, toP.Z * 0.55f); }
            world.AddChild(c);
        }

        // Spawn a tankless FITTING (splitter/combiner) at `pos`, unrotated — its input(-X)/output(+X) faces align to world X.
        void SpawnFluidFitting(Node world, FluidRole role, int ways, Vector3 pos)
        {
            var c = FluidContainer.MakeFitting(role, ways);
            c.Position = pos;
            world.AddChild(c);
        }

        /// <summary>What the current knobs actually MEAN, in the units the decision gets made in. "heave x0.5"
        /// tells you nothing on its own; "terminal fall 43.6 m/s" is the thing being judged.</summary>
        static string HeliPhysStatus()
        {
            float hd = 0.45f * Vehicle.HeaveDampScale;
            string fall = hd > 0.001f ? $"{9.8f / hd:0.#} m/s" : "unlimited (no vertical resistance)";
            // cos^2(45 deg) = 0.5, so a 45 deg dive halves the resistance and roughly doubles terminal fall.
            string dive = !Vehicle.ShaftAlignedDescent ? "identical at any attitude (world-aligned)"
                        : hd > 0.001f ? $"{9.8f / (hd * 0.5f):0.#} m/s at 45 deg nose-down" : "unlimited";
            string top = Vehicle.DragScale > 0.001f
                ? $"x{1f / Mathf.Sqrt(Vehicle.DragScale):0.##} of spec (goes as 1/sqrt(drag))"
                : "no drag at all -- only the backstop limits you";
            return $"heliphys: heave x{Vehicle.HeaveDampScale:0.##}  drag x{Vehicle.DragScale:0.##}  " +
                   $"backstop {(Vehicle.BackstopEnabled ? "on" : "off")}  shaftDescent {(Vehicle.ShaftAlignedDescent ? "on" : "off")}  " +
                   $"redirect x{Vehicle.HeaveRedirect:0.##}\n" +
                   $"  sink->speed conversion: {(Vehicle.HeaveRedirect > 0f ? $"{Vehicle.HeaveRedirect * 100f:0}% of the sink is pushed along the disc instead of destroyed" : "NONE -- vertical velocity cannot affect horizontal speed at all")}\n" +
                   $"  terminal fall, level: {fall}\n" +
                   $"  terminal fall, diving: {dive}\n" +
                   $"  level top speed: {top}\n" +
                   $"  MP: server validates horizontal at SpeedMax x1.25 and climb at HeliClimbMax with ZERO slack --\n" +
                   $"  past those it rolls the pilot back, so a feel tuned here needs the spec moved to match.";
        }

        static ItemAsset ResolveItem(string arg)
        {
            if (ushort.TryParse(arg, out var id)) return Assets.find(id);
            string a = arg.Replace(" ", "");
            var named = Assets.all().Where(x => !string.IsNullOrEmpty(x.itemName));
            // exact name, then name-starts-with, then name-CONTAINS (so `give battery`/`switch`/`turbine` hit the multi-word
            // "Vehicle Battery"/"Power Switch"/"Wind Turbine"); shortest name wins = most specific.
            return named.FirstOrDefault(x => string.Equals(x.itemName, arg, System.StringComparison.OrdinalIgnoreCase))
                ?? named.Where(x => x.itemName.Replace(" ", "").StartsWith(a, System.StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.itemName.Length).FirstOrDefault()
                ?? named.Where(x => x.itemName.Replace(" ", "").Contains(a, System.StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.itemName.Length).FirstOrDefault();
        }

        void Autocomplete()
        {
            var parts = _input.Text.Split(' ', 2, System.StringSplitOptions.None);
            if (parts.Length < 2)
            {
                var v = Verbs.FirstOrDefault(x => x.StartsWith(_input.Text.Trim(), System.StringComparison.OrdinalIgnoreCase));
                if (v != null) SetInput(v + " ");
                return;
            }
            string verb = parts[0].ToLowerInvariant(), pre = parts[1].Replace(" ", "");
            string match = verb.StartsWith("veh") ? Vehicle.SpecNames.FirstOrDefault(n => n.StartsWith(pre, System.StringComparison.OrdinalIgnoreCase))
                : (verb == "teleport" || verb == "tp") ? MapNodes.Locations.Select(n => n.Name).Where(n => n.Replace(" ", "").StartsWith(pre, System.StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length).FirstOrDefault()
                : verb == "hitbox" ? new[] { "client", "server", "off" }.FirstOrDefault(n => n.StartsWith(pre, System.StringComparison.OrdinalIgnoreCase))
                : verb == "hitbox" ? new[] { "client", "server", "off" }.FirstOrDefault(n => n.StartsWith(pre, System.StringComparison.OrdinalIgnoreCase))
                : Assets.all().Select(x => x.itemName).Where(n => !string.IsNullOrEmpty(n) && n.Replace(" ", "").StartsWith(pre, System.StringComparison.OrdinalIgnoreCase))
                            .OrderBy(n => n.Length).FirstOrDefault();
            if (match != null) SetInput(parts[0] + " " + match);
        }

        void HistoryNav(int dir)   // up/down arrow: browse this session's command history (bash-style)
        {
            if (_history.Count == 0) return;
            _histIdx = Mathf.Clamp(_histIdx + dir, 0, _history.Count);
            SetInput(_histIdx < _history.Count ? _history[_histIdx] : "");   // one-past-the-end = a fresh blank line
        }
        void SetInput(string s) { _input.Text = s; _input.CaretColumn = s.Length; }
        // the world's day/night cycle (joins group "daynight" in its _Ready) -- drives the time/timeSpeed/dayLength cmds
        DayNightCycle DayNight()
        {
            foreach (var n in GetTree().GetNodesInGroup("daynight")) if (n is DayNightCycle d && IsInstanceValid(d)) return d;
            return null;
        }

        // named times of day -> hour (0..24), for `timeSet noon` etc.
        static readonly System.Collections.Generic.Dictionary<string, float> NamedTimes = new()
        {
            ["midnight"] = 0f, ["night"] = 22f, ["dawn"] = 6f, ["sunrise"] = 6f, ["morning"] = 8f,
            ["noon"] = 12f, ["midday"] = 12f, ["afternoon"] = 15f, ["dusk"] = 18f, ["sunset"] = 18f, ["evening"] = 19f,
        };

        // Parse a time/duration into HOURS: a name (noon/midnight/dawn/...), 12-hour (8am, 6:30pm), 24-hour HH:MM (18:30),
        // military HHMM (1800, 0830), or a bare number (18, 1.5). timeSet reads it as an absolute clock; timeAdd as a delta
        // (allowNeg). Returns false on garbage.
        public static bool ParseClock(string s, out float hours, bool allowNeg)
        {
            hours = 0f;
            s = s.Trim().ToLowerInvariant();
            if (s.Length == 0) return false;
            if (NamedTimes.TryGetValue(s, out hours)) return true;
            bool am = false, pm = false;
            if (s.EndsWith("am")) { am = true; s = s[..^2].Trim(); }
            else if (s.EndsWith("pm")) { pm = true; s = s[..^2].Trim(); }
            float h, m = 0f;
            if (s.Contains(':'))
            {
                var p = s.Split(':');
                if (p.Length != 2 || !float.TryParse(p[0], out h) || !float.TryParse(p[1], out m)) return false;
            }
            else if (!am && !pm && s.Length >= 3 && s.Length <= 4 && s.All(char.IsDigit))
            {
                int v = int.Parse(s); h = v / 100; m = v % 100;   // military HHMM: last two digits = minutes
            }
            else if (!float.TryParse(s, out h)) return false;
            if (m < 0f || m >= 60f) return false;
            if (am || pm) { if (h == 12f) h = 0f; if (pm) h += 12f; }   // 12am=0, 12pm=12, 8pm=20
            hours = h + m / 60f;
            return allowNeg || hours >= 0f;
        }

        // Parse a fluid volume for `fill`: a number in mL, or with an "L" suffix -> litres (500 -> 500 mL, 1.5L -> 1500 mL).
        public static bool ParseVolume(string s, out float ml)
        {
            ml = 0f;
            s = (s ?? "").Trim().ToLowerInvariant();
            bool litres = false;
            if (s.EndsWith("ml")) s = s[..^2].Trim();
            else if (s.EndsWith("l")) { litres = true; s = s[..^1].Trim(); }
            if (!float.TryParse(s, out float v) || v < 0f) return false;
            ml = litres ? v * 1000f : v;
            return true;
        }

        // 0..1 time-of-day -> "HH:MM (label)" for the `time` readout
        public static string FormatTime(float t01)
        {
            float hrs = Mathf.PosMod(t01, 1f) * 24f;
            int H = (int)hrs; int M = (int)Mathf.Round((hrs - H) * 60f);
            if (M >= 60) { M = 0; H = (H + 1) % 24; }
            string name = H switch { 0 => " (midnight)", 6 => " (dawn)", 12 => " (noon)", 18 => " (dusk)", _ => "" };
            return $"{H:00}:{M:00}{name}";
        }

        void Log(string msg) { _log.Text = msg; GD.Print("[console] " + msg); }
    }

    // PEI's real named LOCATION nodes (towns/POIs), ripped byte-exact from Maps/PEI/Environment/Nodes.dat -> content/nodes.tsv
    // (tools/parse_nodes.py). Used by the console `teleport` command and the map.
    public static class MapNodes
    {
        // Which baked location file to read: nodes.tsv for PEI (Nodes.dat -> parse_nodes.py), nodes_<key>.tsv for a
        // modern map whose locations live in Level.hierarchy (parse_hierarchy_locations.py) -- same key scheme as
        // placements. Main sets it while resolving --map=/UG_MAP, which runs BEFORE the first map-UI/teleport access,
        // and Locations is lazy so the read picks up that value instead of racing the class initializer.
        public static string MapNodeFile = "nodes.tsv";
        static System.Collections.Generic.List<(string Name, Vector3 Pos)> _cache;
        public static System.Collections.Generic.List<(string Name, Vector3 Pos)> Locations => _cache ??= Load();
        public static void Reload() => _cache = null;   // drop the cache so a mid-session map switch re-reads
        static System.Collections.Generic.List<(string, Vector3)> Load()
        {
            var list = new System.Collections.Generic.List<(string, Vector3)>();
            string path = ProjectSettings.GlobalizePath("res://content/" + MapNodeFile);
            if (!System.IO.File.Exists(path)) return list;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var line in System.IO.File.ReadAllLines(path))
            {
                var c = line.Split('\t');
                if (c.Length < 2) continue;
                var q = c[1].Split(',');
                if (q.Length < 3) continue;
                list.Add((c[0], new Vector3(float.Parse(q[0], ci), float.Parse(q[1], ci), float.Parse(q[2], ci))));
            }
            return list;
        }
    }
}
