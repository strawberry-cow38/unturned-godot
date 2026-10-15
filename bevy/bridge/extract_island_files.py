"""Original editor naming and player translator, without editor nodes/UI."""


def extract(read, out, match, method):
    def once(text,before,after=''):
        assert text.count(before)==1,before
        return text.replace(before,after)
    names=read('game/EditorMaps.cs')
    sanitise=method(names,'public static string Sanitise(string raw)')
    (out/'FiniteIslandFiles.g.cs').write_text('namespace UnturnedBevy { public sealed partial class FiniteIslandFiles {\n'+sanitise+'\n}}\n')
    source=read('game/EditorSpawns.cs')
    save=method(source,match(r'        public int Save\(\)[^\n]*',source).strip())
    start=save.index('            string sp = TranslatorPath(_category);')
    end=save.index('            Log.Print(' ,start)
    save='public int Save(string sp) {\n'+save[start:end].replace('            string sp = TranslatorPath(_category);','')+'            return Points.Count;\n}'
    save=save.replace('_spawns','Points')
    load=method(source,'void LoadTranslator(string sp)')
    load=once(load,'void LoadTranslator','public void Load')
    load=once(load,'            Log.Print($"[editor-spawns] loaded {_spawns.Count} {_category} spawns (editor translator)");')
    # Original initializer needs mutable fields; a value record captures exactly
    # the same parsed values without editor scene ownership.
    load=once(load,'_spawns.Add(new Spawn { Pos = new Vector3(x, y, z), Yaw = yaw, IsAlt = p[4] == "1", Type = p.Length > 5 && int.TryParse(p[5], out var t) ? t : 0 });',
        'Points.Add(new Spawn(new Vector3(x,y,z),yaw,p[4] == "1",p.Length > 5 && int.TryParse(p[5], out var t) ? t : 0));')
    (out/'FiniteIslandPlayerSpawns.g.cs').write_text('using Godot; namespace UnturnedBevy { public sealed partial class FiniteIslandPlayerSpawns {\n'+save+'\n'+load+'\n}}\n')
