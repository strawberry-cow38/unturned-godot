using Godot;
using SDG.Unturned;
using System.Collections.Generic;
using System.Text.Json;

namespace UnturnedGodot
{
    /// <summary>content/npcs.json -> the core model. Ripped by tools/extract_npcs.py from retail's
    /// Bundles/NPCs: 40 characters, 142 dialogues, 8 vendors.
    ///
    /// Loaded ONCE and held, like ItemCatalog: the file is 135 KB and a dialogue is opened every time somebody
    /// talks to anyone.</summary>
    public static class NpcCatalog
    {
        static readonly Dictionary<int, NpcCharacterDef> _chars = new();
        static readonly Dictionary<string, NpcCharacterDef> _charsByKey = new();
        static readonly Dictionary<int, NpcDialogue> _dialogues = new();
        static readonly Dictionary<string, NpcVendorDef> _vendors = new();   // keyed by GUID: vendors have no numeric id
        static bool _loaded;

        public static int CharacterCount { get { Load(); return _chars.Count; } }
        public static int DialogueCount { get { Load(); return _dialogues.Count; } }
        public static int VendorCount { get { Load(); return _vendors.Count; } }
        public static IEnumerable<NpcCharacterDef> Characters { get { Load(); return _charsByKey.Values; } }
        public static IEnumerable<NpcDialogue> Dialogues { get { Load(); return _dialogues.Values; } }

        public static NpcCharacterDef Character(int id) { Load(); return _chars.TryGetValue(id, out var c) ? c : null; }
        public static NpcCharacterDef CharacterByKey(string key) { Load(); return _charsByKey.TryGetValue(key ?? "", out var c) ? c : null; }
        public static NpcDialogue Dialogue(int id) { Load(); return _dialogues.TryGetValue(id, out var d) ? d : null; }
        public static NpcVendorDef Vendor(string guid) { Load(); return _vendors.TryGetValue((guid ?? "").ToLowerInvariant(), out var v) ? v : null; }
        public static IEnumerable<NpcVendorDef> Vendors { get { Load(); return _vendors.Values; } }
        /// <summary>A vendor by its FILE key ("Chef_Fresh_Food_Market") rather than its guid. Dialogue links by
        /// guid -- that is the real relationship -- but a human naming one out loud uses the key, so the console
        /// needs this and nothing else does.</summary>
        public static NpcVendorDef VendorByKey(string key)
        {
            Load();
            string k = (key ?? "").ToLowerInvariant();
            if (k.Length == 0) return null;
            foreach (var v in _vendors.Values) if (v.Key.ToLowerInvariant() == k) return v;
            foreach (var v in _vendors.Values) if (v.Key.ToLowerInvariant().Contains(k)) return v;   // near miss: 8 of them, a prefix is unambiguous enough
            return null;
        }

        /// <summary>Where the editor's own characters live. SEPARATE FROM npcs.json, which is a rip: the
        /// extractor overwrites that file wholesale, so anything authored into it is destroyed by the next run
        /// of tools/extract_npcs.py. Keeping them apart is what makes "re-rip the NPCs" a safe thing to do.</summary>
        public static string CustomPath => ProjectSettings.GlobalizePath("res://content/npcs_custom.json");

        /// <summary>Custom characters, layered OVER the ripped ones. Same key space on purpose -- a placement
        /// stores a key, and the game has to be able to spawn a custom person from one exactly as it spawns
        /// Chef Leonard, with no second path and no "is this one of ours" test at the spawn site.</summary>
        static void LoadCustom()
        {
            if (!System.IO.File.Exists(CustomPath)) return;
            JsonElement root;
            try { root = JsonDocument.Parse(System.IO.File.ReadAllText(CustomPath)).RootElement; }
            catch (System.Exception e) { Log.Print($"[npc] npcs_custom.json failed to parse: {e.Message}"); return; }
            if (!root.TryGetProperty("characters", out var cs)) return;
            int n = 0;
            foreach (var c in cs.EnumerateArray())
            {
                var def = new NpcCharacterDef
                {
                    Id = Int(c, "id"), Key = Str(c, "key"), Name = Str(c, "name"),
                    Shirt = (ushort)Int(c, "shirt"), Pants = (ushort)Int(c, "pants"), Hat = (ushort)Int(c, "hat"),
                    Vest = (ushort)Int(c, "vest"), Mask = (ushort)Int(c, "mask"), Glasses = (ushort)Int(c, "glasses"),
                    Backpack = (ushort)Int(c, "backpack"),
                    Face = Int(c, "face"), Skin = Str(c, "skin"), Hair = Str(c, "hair"),
                    Dialogue = Int(c, "dialogue"), Shop = Str(c, "shop"),
                };
                if (def.Key.Length == 0) continue;
                if (def.Id != 0) _chars[def.Id] = def;
                _charsByKey[def.Key] = def;
                _custom.Add(def.Key);
                n++;
            }
            if (n > 0) Log.Print($"[npc] {n} custom character(s) from npcs_custom.json");
        }

        static readonly HashSet<string> _custom = new();
        public static bool IsCustom(string key) { Load(); return _custom.Contains(key ?? ""); }

        /// <summary>Write one character into the custom file, replacing any with the same key, and make it live
        /// in this session. Rewrites the WHOLE file rather than appending: a half-written array is a file that
        /// parses as nothing, and losing every custom character to save one is not a trade worth taking.</summary>
        public static void SaveCustom(NpcCharacterDef def)
        {
            Load();
            if (def == null || string.IsNullOrEmpty(def.Key)) return;
            _charsByKey[def.Key] = def;
            if (def.Id != 0) _chars[def.Id] = def;
            _custom.Add(def.Key);

            var sb = new System.Text.StringBuilder();
            sb.Append("{\n  \"characters\": [\n");
            bool first = true;
            foreach (var key in _custom)
            {
                if (!_charsByKey.TryGetValue(key, out var d)) continue;
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("    {")
                  .Append($"\"key\": {Q(d.Key)}, \"id\": {d.Id}, \"name\": {Q(d.Name)}, ")
                  .Append($"\"shirt\": {d.Shirt}, \"pants\": {d.Pants}, \"hat\": {d.Hat}, \"vest\": {d.Vest}, ")
                  .Append($"\"mask\": {d.Mask}, \"glasses\": {d.Glasses}, \"backpack\": {d.Backpack}, ")
                  .Append($"\"face\": {d.Face}, \"skin\": {Q(d.Skin)}, \"hair\": {Q(d.Hair)}, ")
                  .Append($"\"dialogue\": {d.Dialogue}, \"shop\": {Q(d.Shop)}")
                  .Append('}');
            }
            sb.Append("\n  ]\n}\n");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CustomPath));
            System.IO.File.WriteAllText(CustomPath, sb.ToString());
            Log.Print($"[npc] saved custom character '{def.Key}' ({_custom.Count} total) -> {CustomPath}");
        }

        /// <summary>JSON string literal. Hand-rolled because the writer above is hand-rolled, and a name with a
        /// quote or a backslash in it must not be able to produce a file that will not parse next launch.</summary>
        static string Q(string s)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        static int Int(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        /// <summary>⚠ A .dat value is TEXT, so a Flag_Bool's value arrives as "True" and a number as "61".
        /// Parsing only the numeric form would silently make every boolean flag 0 -- which reads as "the flag
        /// is unset" and is indistinguishable from a condition that legitimately failed.</summary>
        static short AsShort(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return 0;
            if (string.Equals(s, "True", System.StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(s, "False", System.StringComparison.OrdinalIgnoreCase)) return 0;
            return short.TryParse(s, out short v) ? v : (short)0;
        }

        static T Enum<T>(string s, T dv) where T : struct
            => System.Enum.TryParse<T>((s ?? "").Trim(), true, out var v) ? v : dv;

        static NpcCondition[] Conditions(JsonElement parent, string prop)
        {
            if (!parent.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return System.Array.Empty<NpcCondition>();
            var list = new List<NpcCondition>();
            foreach (var c in arr.EnumerateArray())
                list.Add(new NpcCondition
                {
                    Type = Enum(Str(c, "Type"), ENpcConditionType.None),
                    Id = (ushort)AsShort(Str(c, "ID")),
                    Value = AsShort(Str(c, "Value")),
                    Logic = Enum(Str(c, "Logic"), ENpcLogic.Equal),      // absent Logic means Equal -- retail's own default
                    Status = Enum(Str(c, "Status"), ENpcQuestStatus.None),
                    Text = Str(c, "Holiday"),
                });
            return list.ToArray();
        }

        static NpcReward[] Rewards(JsonElement parent, string prop)
        {
            if (!parent.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return System.Array.Empty<NpcReward>();
            var list = new List<NpcReward>();
            foreach (var r in arr.EnumerateArray())
                list.Add(new NpcReward
                {
                    Type = Enum(Str(r, "Type"), ENpcRewardType.None),
                    Id = (ushort)AsShort(Str(r, "ID")),
                    Value = AsShort(Str(r, "Value")),
                    Modification = Enum(Str(r, "Modification"), ENpcModification.Assign),   // absent means Assign, 70 of 90
                });
            return list.ToArray();
        }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            string path = ProjectSettings.GlobalizePath("res://content/npcs.json");
            if (!System.IO.File.Exists(path)) { Log.Print("[npc] no content/npcs.json -- no NPCs will exist"); return; }
            JsonElement root;
            try { root = JsonDocument.Parse(System.IO.File.ReadAllText(path)).RootElement; }
            catch (System.Exception e) { Log.Print($"[npc] npcs.json failed to parse: {e.Message}"); return; }

            if (root.TryGetProperty("characters", out var cs))
                foreach (var c in cs.EnumerateArray())
                {
                    var def = new NpcCharacterDef
                    {
                        Id = Int(c, "id"), Key = Str(c, "key"), Name = Str(c, "name"),
                        Shirt = (ushort)Int(c, "shirt"), Pants = (ushort)Int(c, "pants"), Hat = (ushort)Int(c, "hat"),
                        Vest = (ushort)Int(c, "vest"), Mask = (ushort)Int(c, "mask"), Glasses = (ushort)Int(c, "glasses"),
                        Backpack = (ushort)Int(c, "backpack"),
                        Face = Int(c, "face"), Skin = Str(c, "skin"), Hair = Str(c, "hair"),
                        Dialogue = Int(c, "dialogue"),
                    };
                    if (def.Id != 0) _chars[def.Id] = def;
                    if (def.Key.Length > 0) _charsByKey[def.Key] = def;
                }

            LoadCustom();

            if (root.TryGetProperty("dialogues", out var ds))
                foreach (var d in ds.EnumerateArray())
                {
                    var msgs = new List<NpcMessage>();
                    if (d.TryGetProperty("messages", out var ms))
                        foreach (var m in ms.EnumerateArray())
                        {
                            var pages = new List<string>();
                            if (m.TryGetProperty("pages", out var ps))
                                foreach (var p in ps.EnumerateArray()) pages.Add(p.GetString() ?? "");
                            msgs.Add(new NpcMessage { Pages = pages.ToArray(), Conditions = Conditions(m, "conditions") });
                        }
                    var rs = new List<NpcResponse>();
                    if (d.TryGetProperty("responses", out var rr))
                        foreach (var r in rr.EnumerateArray())
                            rs.Add(new NpcResponse
                            {
                                Text = Str(r, "text"),
                                Dialogue = Int(r, "dialogue"),
                                Vendor = Str(r, "vendor"),
                                Quest = Int(r, "quest"),
                                Conditions = Conditions(r, "conditions"),
                                Rewards = Rewards(r, "rewards"),
                            });
                    var dlg = new NpcDialogue { Id = Int(d, "id"), Key = Str(d, "key"), Messages = msgs.ToArray(), Responses = rs.ToArray() };
                    if (dlg.Id != 0) _dialogues[dlg.Id] = dlg;
                }

            if (root.TryGetProperty("vendors", out var vs))
                foreach (var v in vs.EnumerateArray())
                {
                    NpcTradeLine[] Side(string prop)
                    {
                        if (!v.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return System.Array.Empty<NpcTradeLine>();
                        var list = new List<NpcTradeLine>();
                        foreach (var s in arr.EnumerateArray())
                            list.Add(new NpcTradeLine
                            {
                                Type = Str(s, "type"), Item = (ushort)Int(s, "item"), Guid = Str(s, "guid"),
                                Cost = Int(s, "cost"), Spawnpoint = Str(s, "spawnpoint"), Paint = Str(s, "paint"),
                            });
                        return list.ToArray();
                    }
                    var def = new NpcVendorDef
                    {
                        Guid = Str(v, "guid"), Key = Str(v, "key"), Name = Str(v, "name"), Description = Str(v, "description"),
                        Selling = Side("selling"), Buying = Side("buying"),
                    };
                    if (def.Guid.Length > 0) _vendors[def.Guid.ToLowerInvariant()] = def;
                }

            Log.Print($"[npc] {_chars.Count} characters, {_dialogues.Count} dialogues, {_vendors.Count} vendors");
        }
    }
}
