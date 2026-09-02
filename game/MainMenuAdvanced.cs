using Godot;

namespace UnturnedGodot
{
    // "Advanced" -> the FULL vanilla gameplay config. Retail's singleplayer Config button (MenuPlayConfigUI)
    // reflects over ModeConfigData (the per-difficulty gameplay settings) and renders every field grouped by
    // category. We mirror that here as a data table (the real field names, from PlayConfigData.cs), so our
    // inline gameplay options stay the visible common set and the "Advanced" button reveals the rest -> we have
    // AT LEAST the vanilla options. Controls are DUMMY for now (bool -> CheckButton, numeric -> value field);
    // difficulty/zombies etc. that actually do something live in the main map-select panel.
    public partial class MainMenu
    {
        Control _advancedPanel;

        // the real ModeConfigData schema (category -> fields), field type: 'b'=bool, 'f'=float, 'i'=int/uint.
        // Transcribed from Unturned/Settings/PlayConfigData.cs (the 10 categories MenuPlayConfigUI reflects over).
        static readonly (string cat, (string n, char t)[] fields)[] AdvancedSchema =
        {
            ("Items", new (string, char)[] {
                ("Spawn_Chance",'f'),("Despawn_Dropped_Time",'f'),("Despawn_Natural_Time",'f'),("Respawn_Time",'f'),
                ("Quality_Full_Chance",'f'),("Quality_Multiplier",'f'),("Gun_Bullets_Full_Chance",'f'),("Gun_Bullets_Multiplier",'f'),
                ("Magazine_Bullets_Full_Chance",'f'),("Magazine_Bullets_Multiplier",'f'),("Crate_Bullets_Full_Chance",'f'),("Crate_Bullets_Multiplier",'f'),
                ("Has_Durability",'b'),("Food_Spawns_At_Full_Quality",'b'),("Water_Spawns_At_Full_Quality",'b'),("Clothing_Spawns_At_Full_Quality",'b'),
                ("Weapons_Spawn_At_Full_Quality",'b'),("Default_Spawns_At_Full_Quality",'b'),("Clothing_Has_Durability",'b'),("Weapons_Have_Durability",'b') }),
            ("Vehicles", new (string, char)[] {
                ("Decay_Time",'f'),("Decay_Damage_Per_Second",'f'),("Has_Battery_Chance",'f'),("Min_Battery_Charge",'f'),("Max_Battery_Charge",'f'),
                ("Has_Tire_Chance",'f'),("Respawn_Time",'f'),("Unlocked_After_Seconds_In_Safezone",'f'),("Armor_Multiplier",'f'),("Child_Explosion_Armor_Multiplier",'f'),
                ("Gun_Lowcal_Damage_Multiplier",'f'),("Gun_Highcal_Damage_Multiplier",'f'),("Melee_Damage_Multiplier",'f'),("Melee_Repair_Multiplier",'f'),
                ("Max_Instances_Tiny",'i'),("Max_Instances_Small",'i'),("Max_Instances_Medium",'i'),("Max_Instances_Large",'i'),("Max_Instances_Insane",'i'),("Min_Natural_Vehicles",'i') }),
            ("Zombies", new (string, char)[] {
                ("Spawn_Chance",'f'),("Loot_Chance",'f'),("Crawler_Chance",'f'),("Sprinter_Chance",'f'),("Flanker_Chance",'f'),("Burner_Chance",'f'),("Acid_Chance",'f'),
                ("Boss_Electric_Chance",'f'),("Boss_Wind_Chance",'f'),("Boss_Fire_Chance",'f'),("Spirit_Chance",'f'),("DL_Red_Volatile_Chance",'f'),("DL_Blue_Volatile_Chance",'f'),
                ("Boss_Elver_Stomper_Chance",'f'),("Boss_Kuwait_Chance",'f'),("Respawn_Day_Time",'f'),("Respawn_Night_Time",'f'),("Respawn_Beacon_Time",'f'),("Quest_Boss_Respawn_Interval",'f'),
                ("Damage_Multiplier",'f'),("Armor_Multiplier",'f'),("Backstab_Multiplier",'f'),("NonHeadshot_Armor_Multiplier",'f'),("Beacon_Experience_Multiplier",'f'),("Full_Moon_Experience_Multiplier",'f'),
                ("Min_Drops",'i'),("Max_Drops",'i'),("Min_Mega_Drops",'i'),("Max_Mega_Drops",'i'),("Min_Boss_Drops",'i'),("Max_Boss_Drops",'i'),
                ("Slow_Movement",'b'),("Can_Stun",'b'),("Only_Critical_Stuns",'b'),("Weapons_Use_Player_Damage",'b'),
                ("Can_Target_Barricades",'b'),("Can_Target_Structures",'b'),("Can_Target_Vehicles",'b'),("Can_Target_Objects",'b'),
                ("Beacon_Max_Rewards",'i'),("Beacon_Max_Participants",'i'),("Beacon_Rewards_Multiplier",'f') }),
            ("Animals", new (string, char)[] {
                ("Respawn_Time",'f'),("Damage_Multiplier",'f'),("Armor_Multiplier",'f'),
                ("Max_Instances_Tiny",'i'),("Max_Instances_Small",'i'),("Max_Instances_Medium",'i'),("Max_Instances_Large",'i'),("Max_Instances_Insane",'i'),("Weapons_Use_Player_Damage",'b') }),
            ("Barricades", new (string, char)[] {
                ("Decay_Time",'i'),("Armor_Lowtier_Multiplier",'f'),("Armor_Hightier_Multiplier",'f'),("Gun_Lowcal_Damage_Multiplier",'f'),("Gun_Highcal_Damage_Multiplier",'f'),
                ("Melee_Damage_Multiplier",'f'),("Melee_Repair_Multiplier",'f'),("Allow_Item_Placement_On_Vehicle",'b'),("Allow_Trap_Placement_On_Vehicle",'b'),
                ("Max_Item_Distance_From_Hull",'f'),("Max_Trap_Distance_From_Hull",'f') }),
            ("Structures", new (string, char)[] {
                ("Decay_Time",'i'),("Armor_Lowtier_Multiplier",'f'),("Armor_Hightier_Multiplier",'f'),("Gun_Lowcal_Damage_Multiplier",'f'),
                ("Gun_Highcal_Damage_Multiplier",'f'),("Melee_Damage_Multiplier",'f'),("Melee_Repair_Multiplier",'f') }),
            ("Players", new (string, char)[] {
                ("Health_Default",'i'),("Health_Regen_Min_Food",'i'),("Health_Regen_Min_Water",'i'),("Health_Regen_Ticks",'i'),
                ("Food_Default",'i'),("Food_Use_Ticks",'i'),("Food_Damage_Ticks",'i'),("Water_Default",'i'),("Water_Use_Ticks",'i'),("Water_Damage_Ticks",'i'),
                ("Virus_Default",'i'),("Virus_Infect",'i'),("Virus_Use_Ticks",'i'),("Virus_Damage_Ticks",'i'),("Leg_Regen_Ticks",'i'),("Bleed_Damage_Ticks",'i'),("Bleed_Regen_Ticks",'i'),
                ("Armor_Multiplier",'f'),("Experience_Multiplier",'f'),("Detect_Radius_Multiplier",'f'),("Ray_Aggressor_Distance",'f'),
                ("Lose_Skills_PvP",'f'),("Lose_Skills_PvE",'f'),("Lose_Skill_Levels_PvP",'i'),("Lose_Skill_Levels_PvE",'i'),("Lose_Experience_PvP",'f'),("Lose_Experience_PvE",'f'),("Skill_Cost_Multiplier",'f'),
                ("Lose_Items_PvP",'f'),("Lose_Items_PvE",'f'),("Lose_Clothes_PvP",'b'),("Lose_Clothes_PvE",'b'),("Lose_Weapons_PvP",'b'),("Lose_Weapons_PvE",'b'),
                ("Can_Hurt_Legs",'b'),("Can_Break_Legs",'b'),("Can_Fix_Legs",'b'),("Can_Start_Bleeding",'b'),("Can_Stop_Bleeding",'b'),
                ("Spawn_With_Max_Skills",'b'),("Spawn_With_Stamina_Skills",'b'),("Prevent_Level_Skill_Overrides",'b'),("Allow_Instakill_Headshots",'b'),("Allow_Per_Character_Saves",'b') }),
            ("Objects", new (string, char)[] {
                ("Binary_State_Reset_Multiplier",'f'),("Fuel_Reset_Multiplier",'f'),("Water_Reset_Multiplier",'f'),("Resource_Reset_Multiplier",'f'),
                ("Resource_Drops_Multiplier",'f'),("Rubble_Reset_Multiplier",'f'),("Allow_Holiday_Drops",'b'),("Items_Obstruct_Tree_Respawns",'b') }),
            ("Events", new (string, char)[] {
                ("Rain_Frequency_Min",'f'),("Rain_Frequency_Max",'f'),("Rain_Duration_Min",'f'),("Rain_Duration_Max",'f'),
                ("Snow_Frequency_Min",'f'),("Snow_Frequency_Max",'f'),("Snow_Duration_Min",'f'),("Snow_Duration_Max",'f'),
                ("Weather_Frequency_Multiplier",'f'),("Weather_Duration_Multiplier",'f'),("Airdrop_Frequency_Min",'f'),("Airdrop_Frequency_Max",'f'),("Airdrop_Speed",'f'),("Airdrop_Force",'f'),
                ("Arena_Min_Players",'i'),("Arena_Compactor_Damage",'i'),("Arena_Compactor_Extra_Damage_Per_Second",'f'),("Arena_Clear_Timer",'i'),("Arena_Finale_Timer",'i'),("Arena_Restart_Timer",'i'),
                ("Arena_Compactor_Delay_Timer",'i'),("Arena_Compactor_Pause_Timer",'i'),("Use_Airdrops",'b'),("Arena_Use_Compactor_Pause",'b'),
                ("Arena_Compactor_Speed_Tiny",'f'),("Arena_Compactor_Speed_Small",'f'),("Arena_Compactor_Speed_Medium",'f'),("Arena_Compactor_Speed_Large",'f'),("Arena_Compactor_Speed_Insane",'f'),("Arena_Compactor_Shrink_Factor",'f') }),
            ("Gameplay", new (string, char)[] {
                ("Repair_Level_Max",'i'),("Hitmarkers",'b'),("Crosshair",'b'),("Ballistics",'b'),("Chart",'b'),("Satellite",'b'),("Compass",'b'),
                ("Group_Map",'b'),("Group_HUD",'b'),("Group_Player_List",'b'),("Allow_Static_Groups",'b'),("Allow_Dynamic_Groups",'b'),("Allow_Lobby_Groups",'b'),
                ("Allow_Shoulder_Camera",'b'),("Can_Suicide",'b'),("Friendly_Fire",'b'),("Bypass_Buildable_Mobility",'b'),("Bypass_No_Building_Zones",'b'),("Bypass_Building_In_Safezones",'b'),
                ("Allow_Freeform_Buildables",'b'),("Allow_Freeform_Buildables_On_Vehicles",'b'),("Enable_Damage_Flinch",'b'),("Enable_Explosion_Camera_Shake",'b') }),
        };

        // the "Advanced" toggle that lives in the map-select gameplay options (below Permadeath).
        Button AdvancedButton()
        {
            var b = new Button { Text = "⚙  Advanced  (full vanilla config)", CustomMinimumSize = new Vector2(340f, 34f), Alignment = HorizontalAlignment.Center };
            b.AddThemeFontSizeOverride("font_size", 14);
            b.Pressed += ToggleAdvanced;
            return b;
        }

        void ToggleAdvanced()
        {
            if (_advancedPanel == null) return;
            _advancedPanel.Visible = !_advancedPanel.Visible;
            // Only grab input while it is open, so Esc keeps its normal meaning everywhere else in the menu.
            SetProcessInput(_advancedPanel.Visible);
        }

        // Esc closes it too -- the second way out. A close button alone is one control away from the same trap
        // if a later layout change ever covers it.
        public override void _Input(InputEvent e)
        {
            if (_advancedPanel == null || !_advancedPanel.Visible) return;
            if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
            {
                _advancedPanel.Visible = false;
                SetProcessInput(false);
                GetViewport().SetInputAsHandled();
            }
        }

        // the full-config overlay: every vanilla ModeConfigData field, grouped by category, 2 columns, scrollable.
        void BuildAdvancedPanel(CanvasLayer layer)
        {
            // Fixed rect via explicit anchors+offsets (container min-size propagation was collapsing the panel to
            // ~content height on the CanvasLayer). All anchors 0 -> offsets are absolute: (240,110)-(1010,640) = 770x530.
            var advPanel = new Panel { Visible = false };
            advPanel.AnchorLeft = 0f; advPanel.AnchorTop = 0f; advPanel.AnchorRight = 0f; advPanel.AnchorBottom = 0f;
            advPanel.OffsetLeft = 240f; advPanel.OffsetTop = 100f; advPanel.OffsetRight = 1140f; advPanel.OffsetBottom = 1015f;   // base 2560x1440 coords (renders at 0.5x) -> tall enough to fully cover the map panel
            var _bg = new StyleBoxFlat { BgColor = new Color(0.10f, 0.11f, 0.12f, 1f) };
            _bg.SetBorderWidthAll(1);
            _bg.BorderColor = new Color(0f, 0f, 0f, 0.7f);
            advPanel.AddThemeStyleboxOverride("panel", _bg);
            _advancedPanel = advPanel;

            var head = Header("ADVANCED — FULL GAMEPLAY CONFIG", 20);
            head.Position = new Vector2(16f, 12f);
            advPanel.AddChild(head);

            // A CLOSE BUTTON, because the panel is deliberately sized to cover the map panel and the only other
            // way out was the Advanced button UNDERNEATH it (master: "its also impossible to close the advanced
            // menu"). A toggle whose off-switch is covered by the thing it toggles is a one-way door.
            var close = new Button { Text = "✕", Flat = true };
            close.AnchorLeft = 1f; close.AnchorRight = 1f; close.AnchorTop = 0f; close.AnchorBottom = 0f;
            close.OffsetLeft = -52f; close.OffsetTop = 8f; close.OffsetRight = -12f; close.OffsetBottom = 44f;
            close.AddThemeFontSizeOverride("font_size", 22);
            close.TooltipText = "Close (Esc)";
            close.Pressed += ToggleAdvanced;
            advPanel.AddChild(close);
            var note = new Label { Text = "every vanilla ModeConfigData option (per-difficulty). controls are placeholders — the live ones are in the map panel.", Position = new Vector2(16f, 40f) };
            note.AddThemeFontSizeOverride("font_size", 12);
            note.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
            advPanel.AddChild(note);

            // scroll fills the panel below the header (anchors 0..1 against the now-concrete panel rect)
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            scroll.AnchorLeft = 0f; scroll.AnchorTop = 0f; scroll.AnchorRight = 1f; scroll.AnchorBottom = 1f;
            scroll.OffsetLeft = 16f; scroll.OffsetTop = 62f; scroll.OffsetRight = -16f; scroll.OffsetBottom = -14f;
            advPanel.AddChild(scroll);
            var body = new VBoxContainer { CustomMinimumSize = new Vector2(860f, 0f) };
            body.AddThemeConstantOverride("separation", 4);
            scroll.AddChild(body);

            foreach (var (cat, fields) in AdvancedSchema)
            {
                var catHead = new Label { Text = "▸ " + cat.ToUpper() };
                catHead.AddThemeFontSizeOverride("font_size", 15);
                catHead.AddThemeColorOverride("font_color", new Color(0.92f, 0.86f, 0.62f));
                body.AddChild(catHead);
                var grid = new GridContainer { Columns = 2 };
                grid.AddThemeConstantOverride("h_separation", 22);
                grid.AddThemeConstantOverride("v_separation", 2);
                body.AddChild(grid);
                foreach (var (n, t) in fields) grid.AddChild(AdvRow(n, t));
            }

            layer.AddChild(_advancedPanel);
        }

        // one config field row: prettified label + a dummy control by type.
        Control AdvRow(string name, char type)
        {
            var row = new HBoxContainer { CustomMinimumSize = new Vector2(350f, 26f) };
            row.AddThemeConstantOverride("separation", 6);
            var l = new Label { Text = name.Replace('_', ' '), CustomMinimumSize = new Vector2(250f, 0f), VerticalAlignment = VerticalAlignment.Center };
            l.AddThemeFontSizeOverride("font_size", 12);
            l.AddThemeColorOverride("font_color", new Color(0.82f, 0.82f, 0.82f));
            row.AddChild(l);
            if (type == 'b')
            {
                var c = new CheckButton { ButtonPressed = true, CustomMinimumSize = new Vector2(60f, 24f) };
                row.AddChild(c);
            }
            else
            {
                var e = new LineEdit { Text = type == 'f' ? "1.0" : "1", CustomMinimumSize = new Vector2(70f, 24f), Alignment = HorizontalAlignment.Center };
                e.AddThemeFontSizeOverride("font_size", 12);
                row.AddChild(e);
            }
            return row;
        }
    }
}
