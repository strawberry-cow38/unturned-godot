using Godot;
using System.Linq;
using SDG.Unturned;

namespace UnturnedGodot
{
    // F1 dev console (master). Press F1 to open a command bar at the top of the screen:
    //   give <item id|name>      -> spawns a WorldItem at the player's look-orb
    //   vehicle <id|name>        -> spawns that vehicle at the look-orb
    // TAB autocompletes (verb first, then the item/vehicle name); ENTER runs; ESC / F1 closes.
    // While it's open the mouse is freed, which gates player look/movement the same way the inventory UI does.
    public partial class DevConsole : CanvasLayer
    {
        public PlayerController Player;

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
        bool _resultHooked;

        LineEdit _input;
        Label _log;
        static readonly string[] Verbs = { "give", "vehicle", "teleport", "plant", "skill", "xp", "hold", "deploy", "unarmed", "survival", "toggleGlobalPower", "infFuel", "wear", "unwear", "fluid" };
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
            if (k.Keycode == Key.F1) { Toggle(); GetViewport().SetInputAsHandled(); }
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
            else { _input.ReleaseFocus(); _input.Clear(); Input.MouseMode = Input.MouseModeEnum.Captured; }
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

            // infFuel [on|off]  -- dev/playtesting: ALL cars stop burning fuel. ON by DEFAULT (master 2026-07-20);
            // bare form FLIPS it. Handled above the arg guard so the no-arg toggle works. SP-local static, not networked.
            if (verb == "inffuel")
            {
                string f = arg.ToLowerInvariant();
                Vehicle.InfiniteFuel = f == "on" || f == "1" || f == "true" ? true
                                     : f == "off" || f == "0" || f == "false" ? false
                                     : !Vehicle.InfiniteFuel;
                Log($"infFuel {(Vehicle.InfiniteFuel ? "ON -- cars won't burn fuel" : "OFF -- fuel drains normally")}");
                return;
            }

            if (arg.Length == 0) { Log("usage: give <item> | vehicle <name>"); return; }

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
            else if (verb == "vehicle" || verb == "veh")
            {
                string name = Vehicle.SpecNames.FirstOrDefault(n => n.Equals(arg, System.StringComparison.OrdinalIgnoreCase))
                           ?? Vehicle.SpecNames.FirstOrDefault(n => n.StartsWith(arg, System.StringComparison.OrdinalIgnoreCase));
                if (name == null) { Log($"no vehicle '{arg}' (try: {string.Join(", ", Vehicle.SpecNames)})"); return; }
                var v = Vehicle.BuildByName(name, (int)(GD.Randi() % 8));
                (Player?.GetParent() ?? GetTree().Root).AddChild(v);
                v.GlobalPosition = at + Vector3.Up * 1.5f;
                Log($"spawned {name}");
            }
            else if (verb == "teleport" || verb == "tp")
            {
                var m = MapNodes.Locations.Where(n => n.Name.Replace(" ", "").StartsWith(arg.Replace(" ", ""), System.StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Name.Length).ToList();
                if (m.Count == 0) { Log($"no location '{arg}'"); return; }
                Player?.TeleportTo(m[0].Pos + Vector3.Up * 3f);   // teleport the vehicle if driving, else the player (master)
                Log($"teleported to {m[0].Name}");
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
        void Log(string msg) { _log.Text = msg; GD.Print("[console] " + msg); }
    }

    // PEI's real named LOCATION nodes (towns/POIs), ripped byte-exact from Maps/PEI/Environment/Nodes.dat -> content/nodes.tsv
    // (tools/parse_nodes.py). Used by the F1 `teleport` command and the map.
    public static class MapNodes
    {
        public static readonly System.Collections.Generic.List<(string Name, Vector3 Pos)> Locations = Load();
        static System.Collections.Generic.List<(string, Vector3)> Load()
        {
            var list = new System.Collections.Generic.List<(string, Vector3)>();
            string path = ProjectSettings.GlobalizePath("res://content/nodes.tsv");
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
