"""Original editor object and road file formats, with scene nodes replaced by records."""
import re


def extract(read, out, match, method):
    def once(text, before, after=''):
        assert text.count(before) == 1, before
        return text.replace(before, after)
    editor = read('game/EditorObjects.cs')
    save = method(editor, 'public int Save()')
    save = once(save, 'public int Save()', 'public int SaveProps(string path)')
    save = once(save, 'SavePath, false', 'path, false')
    save = once(save, '_placed', 'placements')
    save = once(save, 'p.HasMeta("guid") ? (string)p.GetMeta("guid") : ""', 'nameToGuid.GetValueOrDefault(p.Name, "")')
    save = once(save, 'p.GlobalPosition', 'p.Position')
    save = once(save, 'p.GlobalTransform.Basis', 'p.Basis')
    save = once(save, 'p.HasMeta("mat_name") ? (string)p.GetMeta("mat_name") : null', 'p.Material')
    for name in ('SaveLootCrates', 'SaveStoreShelves', 'SaveGridPower', 'SaveGasPump', 'SaveSigns', 'SaveLaneSigns', 'SaveTunnels', 'SaveBakeOmit'):
        save = once(save, f'            {name}();')
    save = once(save, '{SavePath}', '{path}')
    decompose = method(editor, 'static (float ex, float ey, float ez) DecomposeEuler(Basis b)')
    decompose = once(decompose, 'b.GetEuler(EulerOrder.Yxz)', 'b.GetEuler()')
    load = method(editor, match(r'        void LoadSaved\(\)[^\n]*', editor).strip())
    load = once(load, 'void LoadSaved()', 'public void LoadProps(string path)')
    load = once(load, '            LoadCustomPlaceables();')
    assert load.count('SavePath') == 2
    load = load.replace('SavePath', 'path').replace('_guidToName', 'guidToName')
    (out / 'FiniteIslandObjectsSave.g.cs').write_text(
        '// Original main prop file writer/reader. Custom-placeable sidecars are separate consumers.\n'
        'using Godot; using UnturnedGodot; namespace UnturnedBevy {\n'
        'public sealed partial class FiniteIslandObjects {\n'+save+'\n'+load+'\n'+decompose+'\n}}\n')
    road = read('game/RoadField.cs')
    parser = method(road, 'List<RoadData> ParsePathsDat(string path)')
    g = match(r'        static Vector3 G\([^\n]+', road)
    (out / 'FiniteIslandRoadsSave.g.cs').write_text(
        '// Original saved road reader, inverse of the existing Paths.dat writer.\n'
        'using Godot; namespace UnturnedBevy { public sealed partial class FiniteIslandRoads {\n'+
        parser+'\n'+g+'\n}}\n')
