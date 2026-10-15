#!/usr/bin/env python3
"""Extract unchanged data declarations; reject source drift instead of guessing.

The source's DeployableDef mixes a data table and Godot mesh/material methods.
Keep the table verbatim, excluding only those rendering methods. Related enum and
constant declarations are likewise copied from their original owners at build time.
"""
from pathlib import Path
import re
import sys

repo = Path(__file__).resolve().parents[2]
out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)

def read(name):
    return (repo / name).read_text()

def match(pattern, source):
    m = re.search(pattern, source, re.S)
    if not m:
        raise RuntimeError(f'Source declaration moved: {pattern}')
    return m.group(0)

source = read('game/DeployableDef.cs')
marker = '        // The mesh + a nearest-filtered palette material'
assert source.count(marker) == 1
source = source.split(marker)[0] + '        public Vector3 MeshEuler;\n    }\n}\n'
(out / 'DeployableData.g.cs').write_text('// Generated from game/DeployableDef.cs; do not edit.\n' + source)
parts = ['using Godot; namespace UnturnedGodot {']
for filename, name in [('game/FluidTank.cs', 'FluidType'), ('game/FluidTank.cs', 'WaterQuality'),
                       ('game/FluidContainer.cs', 'FluidRole'), ('game/BarricadePlacer.cs', 'BarricadeMount'),
                       ('game/WindowBarricadeMesh.cs', 'WindowBarricadeStyle')]:
    parts.append(match(r'public enum ' + name + r'\s*\{[^}]+\}', read(filename)))
parts.append('public static class LampLight {' + match(r'public enum Kind\s*\{[^}]+\}', read('game/LampLight.cs')) + '}')
parts.append('public static class Refrigerator {' + match(r'public const byte GridW[^;]+;', read('game/inventory/StorageCrate.cs')) + match(r'public const float Watts[^;]+;', read('game/inventory/StorageCrate.cs')) + '}')
for file, name, power in [('game/GridPowerSource.cs', 'GridPowerSource', 'DefaultWatts'), ('game/GasPump.cs', 'GasPump', 'Watts')]:
    parts.append('public static class ' + name + '{' + match(r'public const float ' + power + r'[^;]+;', read(file)) + match(r'public static readonly Vector3 PortLocal[^;]+;', read(file)) + '}')
parts.append('}')
(out / 'CatalogEnums.g.cs').write_text('// Generated original data declarations; do not edit.\n' + '\n'.join(parts))

fluid = read('game/FluidArt.cs')
spec = fluid[fluid.index('        public sealed class Spec'):fluid.index('        /// <summary>The ripped')]
catalog = fluid[fluid.index('        static readonly Lazy<'):fluid.index('        static readonly Dictionary<ushort, StandardMaterial3D>')]
configure = fluid[fluid.index('        public static void Configure'):fluid.index('        static ArrayMesh Load')]
(out / 'FluidArtData.g.cs').write_text('using Godot; using System.Text.Json; namespace UnturnedGodot { public static class FluidArt {\n' + spec + catalog + 'static Vector3 Vec(float[] v) => new(v[0],v[1],v[2]);\n' + configure + '}}')

loot = read('game/inventory/LootField.cs')
body = loot[loot.index('        struct Pt'):loot.index('        // Proximity is ANY')]
body = '\n'.join(line for line in body.splitlines() if 'readonly Dictionary<int, WorldItem> _live' not in line and 'readonly HashSet<int> _taken' not in line)
body += "\n public IEnumerable<(int Index, ushort ItemId, byte Table, float X, float Y, float Z)> Points() { for(int i=0;i<_pts.Count;i++) {var p=_pts[i]; int id=Roll(i,p.Type); if(id>=0) yield return(i,(ushort)id,p.Type,p.X,p.Y,p.Z); } }\n"
(out / 'RetailLootData.g.cs').write_text('using Godot; using System.Collections.Generic; namespace UnturnedGodot { public sealed class RetailLootData {\n' + body + '}}')

day = read('game/DayNightCycle.cs')
weather = read('game/WeatherManager.cs')
parts = ['namespace UnturnedBevy { public static class WorldDefaults {',
         match(r'public const float DefaultDayLength[^;]+;', day),
         match(r'public float Time[^;]+;', day).replace('public float Time', 'public const float StartTime')]
for name in ['SnowLayDays', 'SnowMeltDays', 'MeltRefC']:
    parts.append(match(r'public const float ' + name + r'[^;]+;', weather))
for name in ['FrequencyMultiplier', 'DurationMultiplier']:
    parts.append(match(r'public static float ' + name + r'[^;]+;', weather))
parts.append(match(r'const float PuddleFillSeconds[^;]+;', weather).replace('const float', 'public const float'))
puddle = weather[weather.index('            float pTarget ='):weather.index('            PuddleLevel = _puddleLevel;')]
puddle = puddle.replace('Mathf.', 'UnityEngine.Mathf.').replace('MoveToward(', 'MoveTowards(')
parts.append('public static float StepPuddle(float _puddleLevel, float rint, float dt) {' + puddle + 'return _puddleLevel;}')
parts.append('}}')
(out / 'WorldDefaults.g.cs').write_text('// Generated original environment defaults; do not edit.\n' + '\n'.join(parts))

crop = read('game/CropNode.cs').split('    // A planted farm crop in the WORLD:')[0] + '}\n'
(out / 'CropData.g.cs').write_text(crop)
fluid = read('game/FluidTank.cs')
tank = fluid[fluid.index('    public class FluidTank'):]
(out / 'FluidTankData.g.cs').write_text('using Godot; namespace UnturnedGodot {\n' + tank)
food = read('game/FoodSpoil.cs').split('        // Spoil one day across every PLACED')[0] + '}}\n'
(out / 'FoodSpoilData.g.cs').write_text(food)
shelf = read('game/inventory/StoreShelf.cs')
shelf = shelf[shelf.index('        class Profile'):shelf.index('        static readonly Dictionary<string, ArrayMesh>')]
(out / 'ShelfData.g.cs').write_text('using Godot; using System.Collections.Generic; using SDG.Unturned; namespace UnturnedGodot { public static class StoreShelf {\n' + shelf + '}}\n')
builder = read('game/WorldBuilder.cs')
container_table = match(r'static readonly System.Collections.Generic.Dictionary<string, \(string mesh, int table, bool display, string label\)> ContainerShelf = new\(\)\s*\{.*?\n        \};', builder)
container_kinds = builder[builder.index('        public static System.Collections.Generic.List<(string mesh, bool display, string label)> ContainerKinds()'):builder.index('        // Openable PROP DOORS')]
(out / 'ContainerKinds.g.cs').write_text('namespace UnturnedGodot { public static partial class WorldBuilder {\n' + container_table.replace('static readonly', 'public static readonly', 1) + container_kinds + '}}\n')
sync = read('game/ContainerNetSync.cs')
sync = sync.replace('        readonly Node _host;', '        // No scene node is needed by the original container lifecycle.')
sync = sync.replace('NetWorldServer server, Node host,', 'NetWorldServer server,')
sync = sync.replace('            _host = host;\n', '')
sync = sync.replace('        void RegisterAll(', '        public void RegisterAll(')
(out / 'ContainerNetSync.g.cs').write_text(sync)
main = read('game/Main.cs')
holiday = main[main.index('        public static string ActiveHolidayNow()'):main.index('        // --objects: PEI')]
(out / 'HolidayData.g.cs').write_text('namespace UnturnedGodot { public static class Main {\n' + holiday + '}}\n')
drop = read('game/WorldNetSync.cs')
drop = drop[drop.index('        void DropLoot(int index)'):]
drop = drop.replace('_field.TryGetDrop', 'TryGetDrop').replace('_field.TryGetWorldPos', 'TryGetWorldPos')
(out / 'DestructibleDrops.g.cs').write_text('using Godot; namespace UnturnedBevy { public sealed partial class WorldServices {\n' + drop)

# Catalog and map helpers have no scene dependencies. Extract the original bodies,
# rather than maintaining a second list of rewards, overrides or authored volumes.
rubble = read('game/DestructibleField.cs')
body = rubble[rubble.index('        public readonly struct Rubble'):]
drops = match(r'public static readonly System.Collections.Generic.Dictionary<string, \(ushort Item, byte Min, byte Max\)> Drops = new\(\)\s*\{.*?\n        \};', rubble)
(out / 'RubbleData.g.cs').write_text('using Godot; using System.IO; using System.Collections.Generic; namespace UnturnedGodot { public static class DestructibleField {\n' + drops + body)
deadzone = read('game/DeadzoneMap.cs').split('        /// <summary>Load the current map')[0] + '}}\n'
(out / 'DeadzoneData.g.cs').write_text(deadzone)
resource = read('game/ResourceField.cs')
reward = resource[resource.index('        public const float ForageResetSeconds'):resource.index('        /// <summary>Trunk radius')]
(out / 'ResourceData.g.cs').write_text('namespace UnturnedGodot { public static class ResourceField {\n' + reward + match(r'internal const float TreeSink[^;]+;', resource) + '}}\n')

viewmodel = read('game/Viewmodel.cs')
donor = match(r'public static string AnimationDonorFor\(string gunName\)\s*=>[^;]+;', viewmodel)
(out / 'WeaponClips.g.cs').write_text('namespace UnturnedGodot { public static class WeaponClips {' + donor + '}}\n')

# Source magazine compatibility, richest-mag selection, chamber retention and
# loose-round consumption remain verbatim, hosted on authoritative inventories.
player = read('game/PlayerController.cs')
parts = [player[player.index('        bool UsesMagItem =>'):player.index('        // infAmmo: refill')],
         match(r'float ShotDamage\(\)\s*=>[^;]+;', player),
         player[player.index('        public SDG.Unturned.ItemAsset HeldGunAsset'):player.index('        // Loose ammo (shotgun shells')],
         player[player.index('        bool FeedsLooseRounds =>'):player.index('        /// <summary>Put <paramref name="n"/> loose rounds')],
         player[player.index('        int ConsumeShells(int want)'):player.index('        // --- shotgun ammo-type picker API')]]
(out / 'WeaponInventory.g.cs').write_text('using Godot; using SDG.Unturned; namespace UnturnedBevy { public sealed partial class WeaponState {\n' + '\n'.join(parts) + '}}\n')

# Harvesting keeps the original species table and deterministic reward methods.
# Only scene ownership/spawn plumbing is removed from those methods.
tree_defaults = resource[resource.index('                    bool isMaple ='):resource.index('                    // WIDER TRUNKS')]
ore = resource[resource.index('    public partial class OreRock'):]
ore_defaults = [match(r'public float ' + key + r'[^;]+;', ore).replace('public float', 'public const float').replace(key, 'Ore' + key) for key in ['Health', 'Reset']]
ore_rewards = match(r'public int RewardMin[^;]+;', ore).replace('public int', 'public const int').replace('RewardMin', 'OreRewardMin').replace('RewardMax', 'OreRewardMax')
parts = ['using Godot; namespace UnturnedBevy { public static class ResourceRules {',
         'public record TreeDef(float Health, float Reset, int Min, int Max, ushort Log);',
         'public static TreeDef Tree(string name) {' + tree_defaults + 'return new(treeHp, treeReset, rMin, rMax, logItem);}',
         *ore_defaults, ore_rewards,
         match(r'internal static double DebrisLife[^;]+;', resource).replace('internal static', 'public const'),
         '} public sealed partial class ResourceNode {']
for method in ['DropRewards', 'DropScrap']:
    body = match(r'void ' + method + r'\(\)\s*\{.*?\n        \}', resource)
    body = body.replace('            var parent = GetParent() ?? (Node)this;\n', '').replace('GlobalTransform.Origin', 'Position')
    body = body.replace('WorldItem.Spawn(parent, ', 'Spawn(')
    parts.append(body)
parts.append('}}')
(out / 'ResourceRules.g.cs').write_text('\n'.join(parts))

# Equipment dispatch is ordered source policy, not inferred from EItemType.
hand = player[player.index('        public enum HandKind'):player.index('        // Dispatch-equip an item')]
parts = ['using SDG.Unturned; namespace UnturnedGodot { public static class HandRules {', hand]
for key in ['CarjackItemId', 'TireItemId']:
    parts.append(match(r'public const ushort ' + key + r'[^;]+;', player))
for key in ['ThrowCooldown', 'ThrowSpeed', 'WeakThrowScale', 'ThrowLift', 'ThrowReleaseFraction', 'ThrowClearProbe', 'RepeatedHitInterval', 'LookReach']:
    parts.append(match(r'const float ' + key + r'[^;]+;', player).replace('const float', 'public const float'))
parts.append('}}')
(out / 'HandRules.g.cs').write_text('\n'.join(parts))
# Door and seat catalog parsing has no scene dependency.
doors = builder[builder.index('        public struct DoorCatalogEntry'):builder.index('        /// <summary>Where you can sit on a prop')]
seats = builder[builder.index('        public static System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(Vector3 Pos, Vector3 Face)>> LoadSeatCatalog'):builder.index('        // The full placed world')]
(out / 'DoorCatalog.g.cs').write_text('using Godot; namespace UnturnedGodot { public static partial class WorldBuilder {\n' + doors + seats + '}}')

# Body lifecycle values come from the actual vehicle/deployable node paths.
vehicle = read('game/Vehicle.cs')
deployable = read('game/Deployable.cs')
def number(pattern, text):
    return re.search(pattern, text, re.S).group(1)
rules = {
    'VehicleFuse': number(r'const float ExplodeDelay = ([0-9.]+f)', vehicle),
    'DeployableFuse': number(r'const float ExplodeDelay = ([0-9.]+f)', deployable),
    'CoolSeconds': number(r'WreckSalvageable => _exploded && _burnTime >= ([0-9.]+f)', vehicle),
    'RetireSeconds': number(r'else if \(_burnTime < ([0-9.]+f)\)   // EXTINGUISHED', vehicle),
    'SalvageSeconds': number(r'const float SalvageTime = ([0-9.]+f)', player),
    'VehicleRadius': number(r'void ExplodeDamage\(\).*?const float R = ([0-9.]+f)', vehicle),
    'VehicleDamage': number(r'v.TakeDamage\(SDG.Unturned.ExplosionMath.Linear\(([0-9.]+f)', vehicle),
    'VehiclePlayerDamage': number(r'pl.TakeDamage\(SDG.Unturned.ExplosionMath.Linear\(([0-9.]+f)', vehicle),
    'DeployableRadius': number(r'void ExplodeDamage\(\).*?const float R = ([0-9.]+f)', deployable),
    'DeployableDamage': number(r'v.TakeDamage\(SDG.Unturned.ExplosionMath.Linear\(([0-9.]+f)', deployable),
}
(out / 'BodyRules.g.cs').write_text('namespace UnturnedBevy { public static class BodyRules {\n' + '\n'.join(f'public const float {key} = {value};' for key, value in rules.items()) + '}}')
# Source wildlife behavior: retain the actual methods and constants, replacing
# only scene ownership, collision queries and presentation with explicit adapters.
animal = read('game/AnimalAgent.cs')
field = read('game/AnimalField.cs')
def method(source, signature):
    return match(re.escape(signature) + r'\s*\{.*?\n        \}', source)

def adapt_brain(body):
    body = body.replace('Rig?.Play(', 'Play(')
    body = body.replace('Terr != null && Terrain.IsWater(Terr.SampleDominantLayer(tx, tz))', 'SampleLayer(tx, tz) == 5')
    body = body.replace('public override void _Process(double delta)', 'public void Step(double delta)')
    body = '\n'.join(line for line in body.splitlines() if 'if (Rig != null && !IsInstanceValid(Rig))' not in line)
    body = body.replace('GameAudio.PlayAt(this, GameAudio.Pick("animals", AnimalCatalog.Get(Species).Rig + "_panic"), GlobalPosition, -2f, 8f, 90f);', 'Panic?.Invoke();')
    if 'void CheckVision' in body:
        start = body.index('            var space = GetWorld3D()')
        end = body.index('            FleeFrom(eye.Value);', start)
        body = body[:start] + '            if (Occluded(head, eye.Value)) return;\n' + body[end:]
    if 'void DamageHit' in body:
        body = '\n'.join(line for line in body.splitlines() if 'CollisionLayer = 0;' not in line and 'RemoveFromGroup(' not in line)
        body = body.replace('-GlobalTransform.Basis.Z', 'Forward')
        start = body.index('                Rig?.RagdollStart(')
        end = body.index('                return;', start)
        body = body[:start] + '                Died?.Invoke((f + Vector3.Up * 0.5f).Normalized() * 6f);\n' + body[end:]
    return body

brain_parts = [animal[animal.index('        Vector3 _target;'):animal.index('        public void Begin()')],
    match(r'public const float RigYawFix[^;]+;', animal),
    'public const double CorpseSeconds = ' + number(r'CreateTimer\(([0-9.]+)\)', animal) + ';']
for signature in ['void StartIdle()', 'void PickTarget()', 'public void DamageHit(float amount, Vector3 point, Vector3 dir)',
                  'void FleeFrom(Vector3 threat)', 'void CheckVision(double delta)', 'public override void _Process(double delta)']:
    brain_parts.append(adapt_brain(method(animal, signature)))
(out / 'AnimalBrain.g.cs').write_text('// Generated unchanged behavior from game/AnimalAgent.cs with explicit engine adapters.\nusing Godot; using UnturnedGodot.Net; namespace UnturnedBevy { public sealed partial class AnimalBrain {\n' + '\n'.join(brain_parts) + '}}')

field_parts = [field[field.index('        struct Pt'):field.index('        // Streaming anchors')]]
field_parts.append(match(r'public const int PublishDivisorTicks[^;]+;', read('game/AnimalNetSync.cs')))
console = read('game/DevConsole.cs')
field_parts.append(match(r'const float GoldenAngle[^;]+;', console))
spawn_console = console[console.index('                var kinds = new System.Collections.Generic.List<(string name, ushort id)>(AnimalField.SpawnableKinds());'):console.index('\n            // ---- temperature')]
spawn_console = spawn_console[:spawn_console.rindex('                return;')]
spawn_console = spawn_console.replace('AnimalField.SpawnableKinds()', 'Kinds.Select(k => (k.Value.rig, k.Key))')
spawn_console = spawn_console.replace('                if (Player == null) { Echo("no player to spawn near"); return; }', '')
spawn_console = spawn_console.replace('Player.GlobalPosition', 'playerPosition').replace('af.BuildAnimal(', 'BuildAnimal(').replace('GD.Randi()', '(uint)Random.Shared.NextInt64(0, 1L << 32)')
field_parts.append('void SpawnConsole(string arg, Vector3 playerPosition, Action<string> Echo) {' + spawn_console + '}')
field_parts[0] = field_parts[0].replace('Dictionary<int, Node3D>', 'Dictionary<int, AnimalBrain>')
for signature in ['static float NearestDistSq(List<Vector3> anchors, float x, float z)', 'public void HubProcess(double delta)']:
    body = method(field, signature)
    if 'HubProcess' in signature:
        body = body[:body.index('            if (!_animCam')] + '\n        }'
        body = body.replace(' || Terr == null', ' || !GeometryReady')
        body = body.replace('!IsInstanceValid(kv.Value)', 'kv.Value.Freed')
        body = body.replace('kv.Value.QueueFree()', 'Remove(kv.Value)')
        body = body.replace('Terrain.IsWater(Terr.SampleDominantLayer(p.X, p.Z))', 'SampleLayer(p.X, p.Z) == 5')
    field_parts.append(body)
(out / 'AnimalField.g.cs').write_text('// Generated source fauna tables, deterministic rolls and streaming.\nusing Godot; using UnturnedGodot; using System.Collections.Generic; namespace UnturnedBevy { public sealed partial class WildlifeServices {\n' + '\n'.join(field_parts) + '}}')

# Current zombie rewrite: retain the complete chunk/flow algorithms. The old
# ZombieSim/Controller is not the implementation used by this source branch.
zfield = read('game/ZombieChunkField.cs')
zfield = zfield.replace('namespace UnturnedGodot', 'namespace UnturnedBevy').replace('public partial class ZombieChunkField : Node3D', 'public sealed partial class ZombieServices')
zfield = zfield.replace('ZombieBody', 'ZombieBodyState')
zfield = '\n'.join(line for line in zfield.splitlines() if not any(key in line for key in [
    'public PlayerController Player;', 'public Terrain Terr;', 'BoxShape3D _probe;',
    'System.Action<Vector3, float> _noiseHandler;', 'public override void _EnterTree()', 'public override void _ExitTree()']))
zfield = zfield.replace('public override void _PhysicsProcess(double delta)', 'void SourceStep(double delta)')
zfield = zfield.replace('Terr != null && Terrain.IsWater(Terr.SampleDominantLayer(q.X, q.Z))', 'SampleLayer(q.X, q.Z) == 5')
zfield = zfield.replace('Terr != null && Terrain.IsWater(Terr.SampleDominantLayer(gx, gz))', 'SampleLayer(gx, gz) == 5')
zfield = zfield.replace('Terr != null ? Terr.SampleHeight(gx, gz) : 0f', 'SampleGround(gx, gz)')
zfield = zfield.replace('Terr != null ? Terr.SampleHeight(nx, nz) : z.Pos.Y', 'SampleGround(nx, nz)')
zfield = zfield.replace('else if (Player != null) _anchors.Add(Player.GlobalPosition);\n            else foreach (var p in PlayerRegistry.All) if (GodotObject.IsInstanceValid(p)) _anchors.Add(p.GlobalPosition);',
    'else foreach (var p in _server.Players.All) _anchors.Add(G(p.Pos));')
zfield = zfield.replace('else if (c.Tier == Tier.Frozen && c.Live != null) c.Live = null;',
    'else if (c.Tier == Tier.Frozen && c.Live != null) Freeze(c);')
zfield = zfield.replace('!GodotObject.IsInstanceValid(z.Body)', 'z.Body.Freed')
zfield = zfield.replace('z.Body.QueueFree()', 'Retire(z.Body)')
zfield = zfield.replace('var space = GetWorld3D()?.DirectSpaceState;', '')
zfield = zfield.replace('z.Body = new ZombieBodyState(z.Table, z.Outfit);\n                    AddChild(z.Body);\n                    z.Body.GlobalPosition = z.Pos;', 'z.Body = BuildBody(z.Table, z.Outfit, z.Pos);')
zfield = zfield.replace('SeePlayer(space, z.Pos, out var seen)', 'SeePlayer(z.Pos, out var seen)')
old_see = method(zfield, 'bool SeePlayer(PhysicsDirectSpaceState3D space, Vector3 from, out Vector3 seen)')
new_see = old_see.replace('PhysicsDirectSpaceState3D space, ', '')
new_see = '\n'.join(line for line in new_see.splitlines() if not any(k in line for k in ['if (space == null)', 'var q = PhysicsRayQueryParameters3D', 'q.From = eye;', 'q.To = a +', 'using var hit =']))
new_see = new_see.replace('if (hit.Count == 0)', 'if (!_geometry.RayDetailed(U(eye), U(a + Vector3.Up * 1.0f), out _))')
zfield = zfield.replace(old_see, new_see)
old_walk = method(zfield, 'bool Walkable(int cx, int cz)')
new_walk = '''bool Walkable(int cx, int cz)
        {
            var key = (cx, cz);
            if (_walkCache.TryGetValue(key, out bool cached)) return cached;
            if (!_geometry.HasTerrain) return true;
            float wx = (cx + 0.5f) * ZombieFlowField.Cell, wz = (cz + 0.5f) * ZombieFlowField.Cell;
            float gy = SampleGround(wx, wz);
            bool walk = !_geometry.OverlapsBox(new(wx, gy + 1.35f, wz), new(ZombieFlowField.Cell * 0.9f, 1.7f, ZombieFlowField.Cell * 0.9f));
            _walkCache[key] = walk;
            return walk;
        }'''
# Assert these dimensions remain source authored instead of silently drifting.
for expected in ['gy + 1.35f', 'ZombieFlowField.Cell * 0.9f, 1.7f, ZombieFlowField.Cell * 0.9f']:
    assert expected in old_walk
zfield = zfield.replace(old_walk, new_walk)
(out / 'ZombieField.g.cs').write_text('// Generated original current zombie chunk rules with explicit engine boundaries.\nusing UnturnedGodot;\n' + zfield)
(out / 'ZombieFlow.g.cs').write_text(read('game/ZombieFlowField.cs').replace('public class ZombieFlowField', 'public partial class ZombieFlowField'))
clothing = read('game/inventory/ClothingContent.cs').split('        // Load a res://content-relative PNG')[0] + '}}'
(out / 'ClothingData.g.cs').write_text(clothing)
zbody = read('game/ZombieBody.cs')
wardrobe = zbody[zbody.index('            uint rand = _outfit;'):zbody.index('            // ⚠ FACE 19')]
wardrobe += '\nreturn new(variant, shirtId, pantsId, hatId, gearId);'
zdata = ['using Godot; using UnturnedGodot; namespace UnturnedBevy { public static class ZombieRules {',
    'public record Wardrobe(int Variant, int ShirtId, int PantsId, int HatId, int GearId);',
    match(r'static uint Next\(ref uint r\) \{[^\n]+\}', zbody),
    'public static Wardrobe Roll(byte _table,uint _outfit) {' + wardrobe + '}',
    match(r'const float ClipNaturalSpeed[^;]+;', zbody).replace('const float', 'public const float'),
    'public const double CorpseSeconds = ' + number(r'CreateTimer\(([0-9.]+)\)', zbody) + ';',
    'public const float Health = ' + number(r'public float Health = ([0-9.]+f)', zbody) + ';',
    '}}']
(out / 'ZombieRules.g.cs').write_text('\n'.join(zdata))

# Pure source attachment/noise rules shared by authoritative hearing.
attachment = read('game/AttachmentFit.cs')
parts = attachment[attachment.index('        public readonly struct BarrelDef'):attachment.index('        public static string MeshFor')]
parts += method(attachment, 'public static int InstalledId(Item gun, string slot)')
(out / 'AttachmentNoise.g.cs').write_text('using SDG.Unturned; namespace UnturnedBevy { public static class AttachmentNoise {' + parts + '}}')
noise = read('game/SoundBus.cs')
constants = '\n'.join(re.findall(r'public const float \w+\s*=\s*[0-9.]+f;', noise))
(out / 'NoiseRules.g.cs').write_text('namespace UnturnedBevy { public static class NoiseRules {' + constants + '}}')
