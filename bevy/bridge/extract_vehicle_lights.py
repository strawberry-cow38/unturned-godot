"""Original Vehicle electrical state machine, stripped only of scene rendering.

All numerical rules and switch/step bodies come from the selected source. Missing
markers are a build error, so updating Vehicle cannot silently leave a stale port.
"""
import re

def extract(read, out, match, method):
    s = read('game/Vehicle.cs')
    assert len(re.findall(r'"[^"]+"', match(r'public static readonly string\[\] LampLabels[^;]+;',s))) == 7, 'Vehicle lamp wire labels changed'
    assert len(re.findall(r'"[^"]+"', match(r'public static readonly string\[\] LightbarPatternNames[^;]+;',s))) == 3, 'Vehicle lightbar wire patterns changed'
    constants = []
    for name in ['BatteryMax','BatteryBurnRate','SirenBurnRate','BatteryChargeRate','BatteryStartMin',
                 'AlarmRadiusSq','HighBeamBurn','LowRange','HighRange']:
        value = match(r'(?:public )?const float '+name+r'\s*=[^;]+;',s)
        constants.append(value if value.startswith('public ') else 'public '+value)
    for name in ['LampKelvinRound','LampKelvinRect']:
        constants.append(match(r'public static float '+name+r'\s*=[^;]+;',s))
    for typ,name in [('string','LampLabels'),('string','LightbarPatternNames'),('float','LightbarSirenPitch')]:
        value=match(r'(?:public )?static readonly '+typ+r'\[\] '+name+r'\s*=[^;]+;',s)
        constants.append(value if value.startswith('public ') else 'public '+value)
    constants.append('public const float LampHitTolerance = '+re.search(r'ResolveHitLamp\(Vector3 world, float tol = ([\d.]+f)\)',s).group(1)+';')
    constants.append('public const float AlarmSpawnChance = '+re.search(r'v\._alarmed = GD.Randf\(\) < ([\d.]+f)',s).group(1)+';')
    (out/'VehicleLightRules.g.cs').write_text('// Generated from game/Vehicle.cs.\nnamespace UnturnedBevy { public static class VehicleLightRules {\n'+'\n'.join(constants)+'}}')
    bodies=[method(s,'public void ToggleHeadlights()'),method(s,'public void SetLightbar(int pattern)')]
    bodies.append(match(r'public void ToggleSiren\(\) \{[^\n]+?\}',s))
    bodies.append(match(r'void TriggerAlarm\(\)[^\n]+',s).split('//')[0].replace('void TriggerAlarm','public void TriggerAlarm'))
    start=s.index('            if (EngineOn)\n',s.index('// BATTERY (strawberry'))
    end=s.index('            if (_exhaust != null)',start)
    step=s[start:end]
    a=step.index('                        bool near;');b=step.index('                        // (enemy proximity',a)
    step=step[:a]+'                        bool near = Proximity?.Invoke() == true;\n'+step[b:]
    a=s.index('            if (_alarmTimer <= 0f && CoupledCab == null)');b=s.index('            if (_hornCd > 0f)',a)
    step+=s[a:b]
    bodies.append('public void Step(double delta) {\n'+step+'}')
    (out/'VehicleLightState.g.cs').write_text('// Generated original switch/battery/alarm/tail bodies.\nusing UnityEngine; using static UnturnedBevy.VehicleLightRules; namespace UnturnedBevy { public sealed partial class VehicleLightState {\n'+'\n'.join(bodies)+'}}')
