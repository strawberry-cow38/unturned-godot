"""Keep InventoryUI's pure placement/presentation helpers at their source."""
def extract(read, out, match, method):
    source = read('game/inventory/InventoryUI.cs')
    methods = []
    for signature in [
        'bool ResolveMoveDest(ItemJar jar, byte dest, out byte page, out byte x, out byte y, out byte rot)',
        'byte StoreTarget(ItemJar jar)',
        'bool FirstFreeOwnCell(ItemAsset a, out byte page, out byte x, out byte y)',
        'static string SalvageTip(BlueprintDef bp)']:
        methods.append('public ' + method(source, signature))
    quality = method(read('game/inventory/ItemTool.cs'), 'public static Godot.Color QualityColor(float q01)')
    # Color is a plain bridge value; retain source lerp arithmetic without loading
    # an engine assembly. There is no source Item/Inventory mutation in this unit.
    quality = quality.replace('r.Lerp(y, q01 * 2f)', 'Lerp(r, y, q01 * 2f)').replace('y.Lerp(g, (q01 - 0.5f) * 2f)', 'Lerp(y, g, (q01 - 0.5f) * 2f)')
    methods.extend([quality, 'public static Godot.Color Lerp(Godot.Color a, Godot.Color b, float t) => new(a.R+(b.R-a.R)*t,a.G+(b.G-a.G)*t,a.B+(b.B-a.B)*t,a.A+(b.A-a.A)*t);'])
    (out/'InventoryPresentation.g.cs').write_text(
        '// Generated from InventoryUI.cs / ItemTool.cs; only visibility/scene inputs adapted.\n'
        'using SDG.Unturned; using UnturnedGodot; using System.Collections.Generic; namespace UnturnedBevy { public sealed class SourceInventoryUI {\n'
        'readonly PlayerInventory Inv; public SourceInventoryUI(PlayerInventory inventory) { Inv=inventory; }\n'
        + '\n'.join(methods) + '\n}}\n')
    fluid = read('game/FluidTank.cs')
    start = fluid.index('    public static class FluidDef')
    end = fluid.index('    // A quantity of fluid a prop can hold')
    fluid = fluid[start:end]
    natural = match(r'public static WaterQuality NaturalWater => Terrain.MapDir switch\s*\{.*?\};', fluid)
    fluid = fluid.replace(natural, '')  # The pure UI projection does not query a scene terrain singleton.
    (out/'FluidPresentation.g.cs').write_text('using Godot; namespace UnturnedGodot {\n' + fluid + '}\n')
