"""Original furniture factories and scene order as values, without authority."""
import re


def extract(read, out, match, method):
    def once(text, before, after=''):
        assert text.count(before) == 1, before
        return text.replace(before, after)

    source = read('game/RuntimeDecorGenerators.cs')
    source = once(source, 'namespace UnturnedGodot;', 'namespace UnturnedBevy;')
    source = source.replace('RuntimeDecorGenerators', 'BuildingDecor')
    source = source.replace('ArrayMesh', 'BuildingDecorMesh').replace('Aabb', 'BuildingAabb')
    # Keep the original Material() line; only its engine enums become values.
    source = once(source, 'static StandardMaterial3D Material() => new(){VertexColorUseAsAlbedo=true,Roughness=1,CullMode=BaseMaterial3D.CullModeEnum.Disabled};',
                  'static BuildingDecorMaterial Material() => new(){VertexColorUseAsAlbedo=true,Roughness=1,DoubleSided=true};')
    a = source.index('        public BuildingDecorMesh Mesh(StandardMaterial3D material)')
    b = source.index('\n    static BuildingDecorMaterial Material()', a)
    source = source[:a] + '''        public BuildingDecorMesh Mesh(BuildingDecorMaterial material)
            => new(V.ToArray(), N.ToArray(), UV.ToArray(), Colors.ToArray(), material);
    }''' + source[b:]
    source = once(source, 'var image=Image.CreateEmpty(w,h,false,Image.Format.Rgba8);',
                  'var image=new BuildingDecorPixels(w,h);')
    source = once(source, 'material.AlbedoTexture=ImageTexture.CreateFromImage(image);material.TextureFilter=BaseMaterial3D.TextureFilterEnum.Nearest;',
                  'material.AlbedoTexture=image;material.NearestFilter=true;')
    # Value shims deliberately stay narrow; these are the component-wise Godot
    # Vector3.Min/Max operations in Builder.Bounds, not a new bounds algorithm.
    source = once(source, 'lo=lo.Min(p);hi=hi.Max(p);',
                  'lo=new(Mathf.Min(lo.X,p.X),Mathf.Min(lo.Y,p.Y),Mathf.Min(lo.Z,p.Z));'
                  'hi=new(Mathf.Max(hi.X,p.X),Mathf.Max(hi.Y,p.Y),Mathf.Max(hi.Z,p.Z));')
    source = source.replace('Colors.White', 'new Color(1,1,1)')
    (out / 'BuildingDecor.g.cs').write_text(
        '// Original RuntimeDecorGenerators; mesh/material/image sinks are values.\n' + source)

    editor = read('game/EditorBuildings.cs')
    show = method(editor, 'void ShowFurniture()')
    show = once(show, '            foreach (var n in _furnitureNodes) if (IsInstanceValid(n)) n.QueueFree();\n')
    show = once(show, 'var objects = _editor?.Objects;', 'var objects = this;')
    show = once(show, '            foreach (var m in Furniture)\n            {',
                '            sourceIndex=-1;\n            foreach (var m in Furniture)\n            {\n                sourceIndex++;')
    switch = method(editor, 'RoomLightSwitch BuildSwitch(PropDesignation m, EditorObjects objects)')
    switch = switch.replace('EditorObjects objects', 'BuildingFurniture objects')
    switch = switch.replace('ContentProvider.ParseObj("res://content/', 'MeshPath("')
    outlet = method(editor, 'GridPowerSource BuildOutlet(PropDesignation m, MeshInstance3D mi, Mesh mesh)')
    outlet = outlet.replace('Mesh mesh)', 'BuildingFurnitureMesh mesh)')
    start = outlet.index('            var body = new StaticBody3D')
    outlet = outlet[:start] + '            return src;\n        }'
    runtime = method(editor, 'public static MeshInstance3D RuntimeDecorNode(PropDesignation m)')
    runtime = runtime.replace('ArrayMesh mesh', 'BuildingDecorMesh mesh')
    runtime = once(runtime, 'Mesh = mesh, Transform = RuntimeDecorTransform(m)',
                   'Mesh = new BuildingFurnitureMesh(null,null,mesh), Transform = RuntimeDecorTransform(m)')
    transform = method(editor, 'public static Transform3D RuntimeDecorTransform(PropDesignation m)')
    tint = match(r'public static Color\? CurtainTint\(string name\) => name switch\s*\{[^}]+\};', editor)
    ports = match(r'static readonly Vector3 OutletPort =[^;]+;', editor)
    upright = match(r'public static Basis Upright\(float yawDeg\) =>[^;]+;', read('game/EditorObjects.cs'))
    shelf = match(r'public const string StoreShelfName[^;]+;', read('game/EditorObjects.cs'))
    output = '\n'.join([show,switch,outlet,runtime,transform,tint,ports,upright,shelf])
    for before, after in [
        ('RoomLightSwitch','BuildingFurnitureSwitch'),('LampLight','BuildingFurnitureLamp'),
        ('GridPowerSource','BuildingFurnitureOutlet'),('MeshInstance3D','BuildingFurnitureNode'),
        ('Node3D','BuildingFurnitureNode'),('Transform3D','BuildingTransform'),
        ('EditorObjects.Upright','Upright'),('RuntimeDecorGenerators','BuildingDecor'),
        ('DoodadKitView','Doodads'),('Log.Err(','UnturnedGodot.Log.Err('),
    ]:
        output = output.replace(before,after)
    light = read('game/LampLight.cs')
    lamp_constants = '\n'.join(match(r'public static (?:Color|float) ' + name + r'[^;]+;', light)
                               for name in ['BulbColor','Range','Energy','FixtureEmission'])
    switch_source = read('game/RoomLightSwitch.cs')
    switch_constants = '\n'.join([
        match(r'public static readonly Vector3 RockerPivot[^;]+;',switch_source),
        match(r'public const float RockerDownDegrees[^;]+;',switch_source),
    ])
    switch_make = method(switch_source,'public static RoomLightSwitch Make(Mesh baseMesh, Mesh rockerMesh, Material material)')
    sw_look = re.search(r'CollisionShape3D \{ Position = (new Vector3\([^)]*\)).*?Size = (new Vector3\([^)]*\))',switch_make,re.S)
    outlet_make = method(editor,'GridPowerSource BuildOutlet(PropDesignation m, MeshInstance3D mi, Mesh mesh)')
    out_look = re.search(r'Name = "OutletLook".*?Position = (new Vector3\([^)]*\)).*?Size = (new Vector3\([^)]*\))',outlet_make,re.S)
    assert sw_look and out_look
    def look_value(m):
        return f'public BuildingFurnitureLook Look => new({m[1]},{m[2]},1u << 6,0);\n'
    jitter = method(read('game/GridLight.cs'),'protected void InitJitter(Vector3 pos)').replace('protected void','void',1)
    (out / 'BuildingFurniture.g.cs').write_text(
        '// Original EditorBuildings.ShowFurniture branching/order/binding; resource sinks are values.\n'
        'using Godot;using UnturnedSim;using System.Collections.Generic;namespace UnturnedBevy {\n'
        'public sealed partial class BuildingFurniture {\n'+output+'\n}\n'
        'public sealed partial class BuildingFurnitureSwitch {\n'+switch_constants+'\n'+look_value(sw_look)+'\n}\n'
        'public sealed partial class BuildingFurnitureOutlet {\n'+look_value(out_look)+'\n}\n'
        'public sealed partial class BuildingFurnitureLamp {\n'+lamp_constants+'\n'+jitter+'\n}\n}\n')
