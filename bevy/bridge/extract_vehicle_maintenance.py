"""Source pane identity/display and the original fixed geometry/cooldown rules."""
import re

def extract(read, out, match, method):
    s = read('game/Vehicle.cs')
    labels = re.sub(r'//[^\n]*', '', match(r'public static readonly string\[\] GlassPaneLabels\s*=\s*\{.*?\};', s))
    assert len(re.findall(r'"[^"]+"', labels)) == 17, 'Glass pane canonical wire IDs need review'
    display = match(r'public static string GlassPaneDisplay\(string label\) => label switch\s*\{.*?\};', s)
    tolerance = re.search(r'ResolveHitGlass\(Vector3 world, float tol = ([\d.]+f)\)', s).group(1)
    cooldown = re.search(r'_hornCooldown = ([\d.]+f);', s).group(1)
    (out/'VehicleMaintenanceRules.g.cs').write_text('// Generated from game/Vehicle.cs.\nnamespace UnturnedBevy { public static class VehicleMaintenanceRules {\n'
        + labels + '\n' + display + '\npublic const float GlassTolerance = ' + tolerance + ';\npublic const float HornCooldown = ' + cooldown + ';\n}}')
