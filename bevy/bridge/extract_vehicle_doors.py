"""Original bus door timing/switch bodies; scene holders only are replaced."""
def extract(read, out, match, method):
    source=read('game/Vehicle.cs')
    hold=match(r'const float DoorHoldSeconds\s*=[^;]+;',source)
    bodies=[match(r'public void ToggleDoor\(\) \{[^\n]+?\}',source)]
    bodies += [method(source,n) for n in ['public void CycleDoor()', 'public void SetDoorOpen(bool open)', 'void UpdateDoor(float dt)']]
    bodies[-1]=bodies[-1].replace('void UpdateDoor','public void UpdateDoor').replace('Mathf.MoveToward(', 'Mathf.MoveTowards(')
    (out/'VehicleDoorState.g.cs').write_text('// Generated unchanged original switch/timing bodies from game/Vehicle.cs.\nusing UnityEngine; namespace UnturnedBevy { public sealed partial class VehicleDoorState {\n'+hold+'\n'+'\n'.join(bodies)+'}}')
