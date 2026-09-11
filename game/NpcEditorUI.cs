using Godot;
using SDG.Unturned;
using System.Collections.Generic;

namespace UnturnedGodot
{
    /// <summary>The character editor (master 2026-09-11: "separate npc editor for clothes, appearance, dialogue,
    /// trades etc"). Opened from the pencil on a row of the NPC tab's roster.
    ///
    /// EDITING A PRESET MAKES A COPY. The ripped characters come out of content/npcs.json, which
    /// tools/extract_npcs.py overwrites wholesale -- anything authored into it dies at the next re-rip. So the
    /// shipped roster is read-only here and Save always writes to npcs_custom.json under a key of its own. You
    /// cannot damage the rip from this window, and "re-rip the NPCs" stays a safe thing to do.
    ///
    /// The preview is a REAL NpcCharacter in its own little world, rebuilt on every change. An editor for
    /// clothes whose preview is a drawing of clothes is an editor for the drawing: if a hat does not sit right
    /// on the rig, this is where that has to be visible, and it is the same build the game will do.</summary>
    public partial class NpcEditorUI : CanvasLayer
    {
        Control _root;
        PanelContainer _panel;
        LineEdit _name;
        Label _origin, _status;
        SubViewport _vp;
        Node3D _pivot;
        NpcCharacter _preview;
        VBoxContainer _fields;

        NpcCharacterDef _def;          // the working copy -- never a catalog instance
        string _sourceKey = "";
        // ⚠ VANILLA IS READ-ONLY (master 2026-09-11: "lock all the vanilla game npcs from edits"). Not just
        // "Save writes elsewhere" -- the CONTROLS are dead. A window whose steppers move while the thing they
        // edit cannot change teaches you it works and then throws the work away at Save.
        bool _locked;
        readonly List<Button> _stepButtons = new();
        Button _saveBtn, _dupBtn;
        float _spin = 200f;            // preview yaw; the rig's -Z means 180 faces the camera

        const int PanelW = 1120, PreviewW = 360, PreviewH = 560, RowW = 680;

        /// <summary>Raised after a successful Save, with the key that now exists. The NPC tab listens so its
        /// roster picks up a character created while it was open.</summary>
        [Signal] public delegate void SavedEventHandler(string key);

        // ---- the pick lists, built once ---------------------------------------------------------------------
        static readonly EItemType[] Slots =
        {
            EItemType.SHIRT, EItemType.PANTS, EItemType.HAT,
            EItemType.VEST, EItemType.MASK, EItemType.GLASSES, EItemType.BACKPACK,
        };
        static readonly string[] SlotNames = { "Shirt", "Pants", "Hat", "Vest", "Mask", "Glasses", "Backpack" };
        static Dictionary<EItemType, List<ushort>> _byType;
        static List<int> _dialogueIds;
        static List<string> _vendorKeys;
        static List<string> _skins, _hairs;

        static void BuildLists()
        {
            if (_byType != null) return;
            // ⚠ THE MAP EDITOR NEVER REGISTERS ITEMS. ItemCatalog.RegisterAll runs on the play and dedicated
            // paths; the editor has never needed an item BY ID, so every clothes list here came out as just
            // "(none)" and the chef's own trousers rendered as "#231". The render caught it -- an empty picker
            // looks exactly like a picker whose one option you happen to be on.
            //
            // Only when it is EMPTY: RegisterAll clears before it fills, so calling it on a populated catalog
            // is a hole rather than a no-op (see the note at Main.cs's dedicated path).
            bool anyItems = false;
            foreach (var _ in Assets.all()) { anyItems = true; break; }
            if (!anyItems)
            {
                ItemCatalog.RegisterAll();
                int n = 0; foreach (var _ in Assets.all()) n++;
                Log.Print($"[npceditor] item catalog was empty -- registered {n} items for the clothes lists");
            }
            _byType = new Dictionary<EItemType, List<ushort>>();
            foreach (var t in Slots) _byType[t] = new List<ushort> { 0 };   // index 0 is always "(none)" -- an empty slot is a real state
            foreach (var a in Assets.all())
                if (a != null && _byType.TryGetValue(a.type, out var l)) l.Add(a.id);
            foreach (var t in Slots) _byType[t].Sort();

            NpcCatalog.Load();
            _dialogueIds = new List<int> { 0 };
            foreach (var d in NpcCatalog.Dialogues) if (d.Id != 0) _dialogueIds.Add(d.Id);
            _dialogueIds.Sort();

            _vendorKeys = new List<string> { "" };
            foreach (var v in NpcCatalog.Vendors) _vendorKeys.Add(v.Key);
            _vendorKeys.Sort(System.StringComparer.OrdinalIgnoreCase);

            // Skin and hair palettes are the colours the RIPPED CHARACTERS ACTUALLY USE, not a colour wheel.
            // Forty people already answer "what does a plausible skin tone look like in this game" and a free
            // picker mostly answers it wrong; this also guarantees a custom character sits next to the shipped
            // ones without looking pasted in.
            var sk = new SortedSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var ha = new SortedSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var c in NpcCatalog.Characters)
            {
                if (!string.IsNullOrEmpty(c.Skin)) sk.Add(c.Skin);
                if (!string.IsNullOrEmpty(c.Hair)) ha.Add(c.Hair);
            }
            _skins = new List<string>(sk);
            _hairs = new List<string>(ha);
            if (_skins.Count == 0) _skins.Add("#B5876B");
            if (_hairs.Count == 0) _hairs.Add("#191919");
        }

        public override void _Ready()
        {
            Layer = 95;   // over the dashboard (60) -- this is a modal window on top of the editor
            BuildLists();

            _root = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(_root);
            var dim = new ColorRect { Color = UITheme.Scrim, MouseFilter = Control.MouseFilterEnum.Stop };
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _root.AddChild(dim);

            _panel = new PanelContainer();
            UITheme.Panel(_panel, solid: true);
            _root.AddChild(_panel);
            var pad = new MarginContainer();
            foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                pad.AddThemeConstantOverride(side, 18);
            _panel.AddChild(pad);
            var col = new VBoxContainer { CustomMinimumSize = new Vector2(PanelW, 0) };
            col.AddThemeConstantOverride("separation", 8);
            pad.AddChild(col);

            // ---- header ----
            var head = new HBoxContainer();
            head.AddThemeConstantOverride("separation", 10);
            col.AddChild(head);
            head.AddChild(UITheme.Label(new Label { Text = "Character", VerticalAlignment = VerticalAlignment.Center }, UITheme.FontTitle, UITheme.Accent));
            _name = new LineEdit { CustomMinimumSize = new Vector2(360, 0), PlaceholderText = "name" };
            _name.TextChanged += t => { if (_def != null) _def.Name = t; };
            head.AddChild(_name);
            _origin = UITheme.Label(new Label { VerticalAlignment = VerticalAlignment.Center }, UITheme.FontLabel, UITheme.TextDim);
            head.AddChild(_origin);
            col.AddChild(new HSeparator());

            // ---- body: preview | fields ----
            var body = new HBoxContainer();
            body.AddThemeConstantOverride("separation", 16);
            col.AddChild(body);

            body.AddChild(BuildPreview());

            var scroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(RowW, PreviewH),
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                ClipContents = true,
            };
            body.AddChild(scroll);
            _fields = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _fields.AddThemeConstantOverride("separation", 3);
            scroll.AddChild(_fields);
            BuildFields();

            col.AddChild(new HSeparator());

            // ---- footer ----
            var foot = new HBoxContainer();
            foot.AddThemeConstantOverride("separation", 10);
            col.AddChild(foot);
            _status = UITheme.Label(new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center },
                                    UITheme.FontLabel, UITheme.TextDim);
            foot.AddChild(_status);
            _dupBtn = new Button { Text = "Duplicate to custom", TooltipText = "Copy this vanilla character into an editable one" };
            _dupBtn.Pressed += Duplicate;
            foot.AddChild(_dupBtn);
            _saveBtn = new Button { Text = "Save" };
            _saveBtn.Pressed += Save;
            foot.AddChild(_saveBtn);
            var revert = new Button { Text = "Revert" };
            revert.Pressed += () => { if (_sourceKey.Length > 0) Open(_sourceKey); else OpenNew(); };
            foot.AddChild(revert);
            var close = new Button { Text = "Close  (Esc)" };
            close.Pressed += Close;
            foot.AddChild(close);

            GetViewport().SizeChanged += Layout;
            _panel.Resized += Layout;
        }

        Control BuildPreview()
        {
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(PreviewW, 0) };
            box.AddThemeConstantOverride("separation", 4);

            var frame = new PanelContainer { CustomMinimumSize = new Vector2(PreviewW, PreviewH) };
            UITheme.Strip(frame);
            box.AddChild(frame);

            _vp = new SubViewport
            {
                Size = new Vector2I(PreviewW, PreviewH),
                OwnWorld3D = true,           // isolated: the map behind must not leak into the portrait
                TransparentBg = true,
                Msaa3D = Viewport.Msaa.Msaa4X,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
            };
            AddChild(_vp);

            // A starting pose only -- FrameCamera recomputes both from the built body on every Rebuild.
            var cam = new Camera3D { Fov = 34f, Current = true, Position = new Vector3(0f, 1.0f, 4.2f) };
            _vp.AddChild(cam);
            // Two lights for the same reason the prop stage has two: one leaves the shadow side pure black, and
            // these models are mostly flat-shaded boxes.
            _vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-28f, 150f, 0f), LightEnergy = 1.15f });
            _vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-8f, -35f, 0f), LightEnergy = 0.55f });
            _vp.AddChild(new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color,
                    BackgroundColor = new Color(0f, 0f, 0f, 0f),
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = new Color(0.55f, 0.55f, 0.55f),
                    AmbientLightEnergy = 1.0f,
                    TonemapMode = Godot.Environment.ToneMapper.Aces,
                },
            });
            _pivot = new Node3D();
            _vp.AddChild(_pivot);

            var tex = new TextureRect
            {
                Texture = _vp.GetTexture(),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            frame.AddChild(tex);
            // Drag to turn them round. A character editor where you can only see the front is one where the
            // backpack is decided by faith.
            tex.GuiInput += e =>
            {
                if (e is InputEventMouseMotion m && (m.ButtonMask & MouseButtonMask.Left) != 0)
                { _spin = Mathf.Wrap(_spin - m.Relative.X * 0.6f, 0f, 360f); ApplySpin(); }
            };

            var turn = new HBoxContainer();
            turn.AddThemeConstantOverride("separation", 6);
            box.AddChild(turn);
            var l = new Button { Text = "↺", CustomMinimumSize = new Vector2(44, 26) };
            l.Pressed += () => { _spin = Mathf.Wrap(_spin - 45f, 0f, 360f); ApplySpin(); };
            turn.AddChild(l);
            turn.AddChild(UITheme.Label(new Label { Text = "drag to turn", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                                                    HorizontalAlignment = HorizontalAlignment.Center,
                                                    VerticalAlignment = VerticalAlignment.Center }, UITheme.FontSmall, UITheme.TextDim));
            var r = new Button { Text = "↻", CustomMinimumSize = new Vector2(44, 26) };
            r.Pressed += () => { _spin = Mathf.Wrap(_spin + 45f, 0f, 360f); ApplySpin(); };
            turn.AddChild(r);
            return box;
        }

        void ApplySpin()
        {
            if (_preview != null && GodotObject.IsInstanceValid(_preview))
                _preview.RotationDegrees = new Vector3(0f, _spin, 0f);
        }

        // ---- rows -------------------------------------------------------------------------------------------
        readonly List<(string label, System.Func<string> value, System.Action<int> step)> _rows = new();
        readonly List<Label> _valueLabels = new();

        void Section(string title)
        {
            if (_fields.GetChildCount() > 0) _fields.AddChild(new HSeparator());
            _fields.AddChild(UITheme.Label(new Label { Text = title }, UITheme.FontHeading, UITheme.Accent));
        }

        /// <summary>One ◀ value ▶ row. Everything in this editor is "pick the next one along a list", so there
        /// is exactly one widget for it -- a per-field control would be seven chances for the clothes rows to
        /// behave differently from each other.</summary>
        void Row(string label, System.Func<string> value, System.Action<int> step)
        {
            var h = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            h.AddThemeConstantOverride("separation", 6);
            _fields.AddChild(h);

            h.AddChild(UITheme.Label(new Label { Text = label, CustomMinimumSize = new Vector2(104, 26), VerticalAlignment = VerticalAlignment.Center },
                                     UITheme.FontBody, UITheme.TextBody));
            var back = new Button { Text = "◀", CustomMinimumSize = new Vector2(32, 26) };
            back.Pressed += () => { step(-1); Refresh(); };
            h.AddChild(back);
            _stepButtons.Add(back);
            var v = UITheme.Label(new Label
            {
                Text = value(),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            }, UITheme.FontBody, UITheme.Text);
            h.AddChild(v);
            var fwd = new Button { Text = "▶", CustomMinimumSize = new Vector2(32, 26) };
            fwd.Pressed += () => { step(+1); Refresh(); };
            h.AddChild(fwd);
            _stepButtons.Add(fwd);

            _rows.Add((label, value, step));
            _valueLabels.Add(v);
        }

        static string ItemName(ushort id) => id == 0 ? "(none)" : Assets.find(id)?.itemName ?? $"#{id}";

        static int Step(List<ushort> list, ushort cur, int by)
        {
            int i = list.IndexOf(cur);
            if (i < 0) i = 0;
            return ((i + by) % list.Count + list.Count) % list.Count;
        }

        void BuildFields()
        {
            Section("Appearance");
            Row("Face", () => _def == null ? "-" : $"{_def.Face}", d =>
            {
                if (_def == null) return;
                _def.Face = ((_def.Face + d) % RiggedCharacter.FaceCount + RiggedCharacter.FaceCount) % RiggedCharacter.FaceCount;
                Rebuild();
            });
            Row("Skin", () => _def?.Skin ?? "-", d => { if (_def != null) { _def.Skin = Cycle(_skins, _def.Skin, d); Rebuild(); } });
            Row("Hair", () => _def?.Hair ?? "-", d => { if (_def != null) { _def.Hair = Cycle(_hairs, _def.Hair, d); Rebuild(); } });

            Section("Clothes");
            for (int i = 0; i < Slots.Length; i++)
            {
                int si = i;
                Row(SlotNames[si], () => ItemName(SlotValue(si)), d =>
                {
                    if (_def == null) return;
                    var list = _byType[Slots[si]];
                    SetSlot(si, list[Step(list, SlotValue(si), d)]);
                    Rebuild();
                });
            }

            Section("Dialogue");
            Row("Dialogue", () =>
            {
                if (_def == null) return "-";
                if (_def.Dialogue == 0) return "(none)";
                var d = NpcCatalog.Dialogue(_def.Dialogue);
                return d == null ? $"{_def.Dialogue} (missing)" : $"{_def.Dialogue}  ·  {d.Responses.Length} responses";
            }, delta =>
            {
                if (_def == null) return;
                int i = _dialogueIds.IndexOf(_def.Dialogue);
                if (i < 0) i = 0;
                _def.Dialogue = _dialogueIds[((i + delta) % _dialogueIds.Count + _dialogueIds.Count) % _dialogueIds.Count];
            });
            // What they actually SAY, under the id. A numeric dialogue id tells you nothing about whether you
            // picked the chef's greeting or a quest hand-in.
            _fields.AddChild(_firstLine = UITheme.Label(new Label
            {
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(RowW - 24, 34),
            }, UITheme.FontLabel, UITheme.TextDim));

            Section("Trade");
            Row("Shop", () =>
            {
                if (_def == null || string.IsNullOrEmpty(_def.Shop)) return "(none)";
                var v = NpcCatalog.VendorByKey(_def.Shop);
                return v == null ? _def.Shop + " (missing)" : $"{TradeRules.PlainText(v.Name)}  ·  {v.Selling.Length}/{v.Buying.Length}";
            }, delta =>
            {
                if (_def == null) return;
                _def.Shop = Cycle(_vendorKeys, _def.Shop ?? "", delta);
            });
            _fields.AddChild(UITheme.Label(new Label
            {
                Text = "A shop set here is offered directly. Ripped characters reach theirs through a dialogue response instead; both work.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(RowW - 24, 0),
            }, UITheme.FontSmall, UITheme.TextDim));
        }

        Label _firstLine;

        static string Cycle(List<string> list, string cur, int by)
        {
            if (list == null || list.Count == 0) return cur;
            int i = list.IndexOf(cur ?? "");
            if (i < 0) i = 0;
            return list[((i + by) % list.Count + list.Count) % list.Count];
        }

        ushort SlotValue(int i) => i switch
        {
            0 => _def?.Shirt ?? 0, 1 => _def?.Pants ?? 0, 2 => _def?.Hat ?? 0, 3 => _def?.Vest ?? 0,
            4 => _def?.Mask ?? 0, 5 => _def?.Glasses ?? 0, _ => _def?.Backpack ?? 0,
        };

        void SetSlot(int i, ushort v)
        {
            if (_def == null) return;
            switch (i)
            {
                case 0: _def.Shirt = v; break;
                case 1: _def.Pants = v; break;
                case 2: _def.Hat = v; break;
                case 3: _def.Vest = v; break;
                case 4: _def.Mask = v; break;
                case 5: _def.Glasses = v; break;
                default: _def.Backpack = v; break;
            }
        }

        // ---- open / refresh / save --------------------------------------------------------------------------
        public bool IsOpen => _root != null && _root.Visible;

        public void Open(string key)
        {
            BuildLists();
            var src = NpcCatalog.CharacterByKey(key);
            if (src == null) return;
            _sourceKey = key;
            // A WORKING COPY, always. Editing the catalog instance would change the character everywhere it is
            // already placed the moment you touched a stepper, including on a Revert you never confirmed.
            _def = new NpcCharacterDef
            {
                Id = src.Id, Key = src.Key, Name = src.Name,
                Shirt = src.Shirt, Pants = src.Pants, Hat = src.Hat, Vest = src.Vest,
                Mask = src.Mask, Glasses = src.Glasses, Backpack = src.Backpack,
                Face = src.Face, Skin = src.Skin, Hair = src.Hair,
                Dialogue = src.Dialogue, Shop = src.Shop,
            };
            _name.Text = _def.Name;
            SetLocked(!NpcCatalog.IsCustom(key));
            _status.Text = "";
            _root.Visible = true;
            Rebuild();
            Refresh();
        }

        /// <summary>A character from nothing (master: "is there a 'create new npc' button?"). Starts BARE rather
        /// than as a copy of somebody: a new character that arrives wearing the chef's whites is one you will
        /// forget to undress, and the roster fills up with chefs called Guard.</summary>
        public void OpenNew()
        {
            BuildLists();
            _sourceKey = "";
            _def = new NpcCharacterDef
            {
                Key = "", Name = "New Character", Id = 0,
                Face = 0,
                Skin = _skins.Count > 0 ? _skins[_skins.Count / 2] : "#B5876B",
                Hair = _hairs.Count > 0 ? _hairs[0] : "#191919",
            };
            _name.Text = _def.Name;
            SetLocked(false);
            _status.Text = "not saved yet";
            _root.Visible = true;
            Rebuild();
            Refresh();
        }

        void SetLocked(bool locked)
        {
            _locked = locked;
            foreach (var b in _stepButtons) b.Disabled = locked;
            _name.Editable = !locked;
            if (_saveBtn != null) { _saveBtn.Disabled = locked; _saveBtn.Text = _sourceKey.Length > 0 && !locked ? "Save" : "Save as new"; }
            if (_dupBtn != null) _dupBtn.Disabled = !locked;   // the only thing you CAN do to a vanilla character
            _origin.Text = locked
                ? "vanilla — locked. Duplicate it to make one you can edit."
                : (_sourceKey.Length > 0 ? "custom — Save overwrites it" : "new custom character");
            _origin.AddThemeColorOverride("font_color", locked ? UITheme.Warn : UITheme.TextDim);
        }

        /// <summary>Copy the vanilla character being viewed into an editable custom one, and stay on it. This is
        /// the whole reason the locked view is still worth opening: starting from the medic is a real workflow,
        /// it just must not write back into the rip.</summary>
        void Duplicate()
        {
            if (_def == null) return;
            _sourceKey = "";
            _def.Key = "";
            _def.Id = 0;
            _def.Name = (_def.Name ?? "Character") + " copy";
            _name.Text = _def.Name;
            SetLocked(false);
            _status.Text = "duplicated — press Save to keep it";
            Refresh();
        }

        void Rebuild()
        {
            if (_preview != null && GodotObject.IsInstanceValid(_preview)) _preview.QueueFree();
            _preview = null;
            if (_def == null || _pivot == null) return;
            _preview = NpcCharacter.Spawn(_pivot, _def, Vector3.Zero, _spin);
            if (_preview == null) return;
            _preview.CollisionLayer = 0;
            _preview.CollisionMask = 0;
            // ⚠ NO NAMEPLATE IN A PORTRAIT. Nameplate._Process re-shows itself by camera distance, so the
            // Visible=false it is spawned with does not stick -- it came back and sat over the head. It is also
            // 2.24 m WIDE billboarded, which dominated the bounds the camera frames against. The name is in the
            // field directly above this panel anyway.
            FindPlate(_preview)?.QueueFree();
            CallDeferred(nameof(FrameCamera));
        }

        static Node FindPlate(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is Nameplate) return c;
                var f = FindPlate(c);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>Put the camera where the whole person fits, COMPUTED from what was actually built rather than
        /// hand-tuned. Two goes at picking a distance by eye both cropped, because the thing being framed changes
        /// with the outfit -- a tall hat or a backpack moves the bounds. Fitting the measured box cannot be wrong
        /// about a hat it has never seen.</summary>
        void FrameCamera()
        {
            var cam = _vp?.GetCamera3D();
            if (cam == null || _preview == null || !GodotObject.IsInstanceValid(_preview)) return;

            // ⚠⚠ DO NOT MEASURE A SKINNED MESH WITH GetAabb. It reports the BIND POSE, and the bind pose is a
            // T-pose: walking the visuals here returned 2.25 m of width for a standing person and parked the
            // camera seven metres back. (The nameplate was the first suspect, and freeing it changed nothing --
            // QueueFree is deferred anyway. It was the arms all along, held out sideways in a pose nothing has
            // been in since Tick first ran.) So the body's extent is KNOWN, and only the part that genuinely
            // varies is measured: how far above the head the hat goes, via the same reader the plate uses.
            const float ShoulderW = 1.0f;   // shoulders plus sleeve, standing
            float topY = 1.85f;
            var rig = _preview.BodyForFraming;
            if (rig != null) topY = Mathf.Max(topY, rig.HeadwearTopLocalY() + 0.06f);

            float half = Mathf.Tan(Mathf.DegToRad(cam.Fov) * 0.5f);
            float aspect = _vp.Size.Y > 0 ? (float)_vp.Size.X / _vp.Size.Y : 1f;
            float need = Mathf.Max(topY * 0.5f / half, ShoulderW * 0.5f / (half * aspect));
            float dist = need * 1.16f + 0.4f;   // 16% air, plus a little for depth (a backpack, a long hat brim)
            float midY = topY * 0.5f;

            cam.Position = new Vector3(0f, midY, dist);
            cam.LookAt(new Vector3(0f, midY, 0f), Vector3.Up);   // ⚠ after it is in the tree; LookAt outside one is a silent no-op
            if (System.Environment.GetEnvironmentVariable("UG_UIGEOM") == "1")
                Log.Print($"[npceditor] fit: top {topY:0.##} -> cam y {midY:0.##} z {dist:0.##} (vp {_vp.Size})");
        }

        void Refresh()
        {
            for (int i = 0; i < _rows.Count && i < _valueLabels.Count; i++) _valueLabels[i].Text = _rows[i].value();
            if (_firstLine != null)
            {
                var d = _def == null ? null : NpcCatalog.Dialogue(_def.Dialogue);
                var msg = d == null ? null : (d.Messages.Length > 0 ? d.Messages[0] : null);
                _firstLine.Text = msg != null && msg.Pages.Length > 0 ? "\u201C" + TradeRules.PlainText(msg.Pages[0]) + "\u201D" : "";
            }
        }

        void Save()
        {
            if (_def == null || _locked) return;   // belt and braces: the button is disabled too
            string name = string.IsNullOrWhiteSpace(_name.Text) ? _def.Name : _name.Text.Trim();
            _def.Name = name;
            if (_sourceKey.Length == 0 || !NpcCatalog.IsCustom(_sourceKey))
            {
                // A NEW key, derived from the name so the roster stays readable, and uniquified rather than
                // silently overwriting somebody -- two characters called "Guard" is a thing a map author will
                // absolutely do.
                _def.Key = UniqueKey(Slug(name));
                _def.Id = 0;   // ids belong to the rip; a custom character is addressed by key
            }
            NpcCatalog.SaveCustom(_def);
            _sourceKey = _def.Key;
            SetLocked(false);
            _status.Text = $"saved as {_def.Key}";
            EmitSignal(SignalName.Saved, _def.Key);
        }

        static string Slug(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "")
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
            string o = sb.ToString().Trim('_');
            return o.Length > 0 ? o : "Character";
        }

        static string UniqueKey(string baseKey)
        {
            string k = baseKey;
            for (int n = 2; NpcCatalog.CharacterByKey(k) != null; n++) k = $"{baseKey}_{n}";
            return k;
        }

        void Layout()
        {
            if (_panel == null) return;
            var vp = GetViewport().GetVisibleRect().Size;
            var sz = _panel.Size;
            _panel.Position = new Vector2((vp.X - sz.X) * 0.5f, Mathf.Max(16f, (vp.Y - sz.Y) * 0.5f));
            if (System.Environment.GetEnvironmentVariable("UG_UIGEOM") == "1")
                Log.Print($"[npceditor] viewport {vp.X}x{vp.Y}  panel {sz.X}x{sz.Y} at {_panel.Position}");
        }

        public void Close()
        {
            if (_root != null) _root.Visible = false;
            if (_preview != null && GodotObject.IsInstanceValid(_preview)) { _preview.QueueFree(); _preview = null; }
        }

        public override void _Input(InputEvent e)
        {
            if (!IsOpen) return;
            if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            { Close(); GetViewport().SetInputAsHandled(); }
        }

        // ---- test seams -------------------------------------------------------------------------------------
        public string DebugKey => _def?.Key ?? "";
        public string DebugRow(int i) => i >= 0 && i < _valueLabels.Count ? $"{_rows[i].label}={_valueLabels[i].Text}" : "";
        public int DebugRowCount => _rows.Count;
        public void DebugStep(int row, int by) { if (row >= 0 && row < _rows.Count) { _rows[row].step(by); Refresh(); } }
        public void DebugSetName(string n) { _name.Text = n; if (_def != null) _def.Name = n; }
        public void DebugSave() => Save();
        public void DebugOpenNew() => OpenNew();
        public void DebugDuplicate() => Duplicate();
        public bool DebugLocked => _locked;
    }
}
