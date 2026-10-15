using UnturnedGodot.Net;

namespace UnturnedBevy;

/// <summary>Only the scene-facing fields/methods around the extracted original
/// Vehicle switch/battery/alarm loop. No local/native writer owns these values.</summary>
public sealed partial class VehicleLightState
{
    public float Battery;
    public bool EngineOn, HasSiren, _exploded, _headlightsOn, _highBeam, _taillightsOn, _sirenOn, _alarmed;
    public int LightbarPattern;
    public float _alarmTimer, _alarmBlip, _alarmCheckT = .3f;
    public bool _alarmLit;
    public VehicleLightState CoupledCab, CoupledTrailer;
    public Func<bool> Proximity;
    public Action Horn;
    void ApplyBeam() { } // native lamps consume accepted high-beam state
    void DoHorn() => Horn?.Invoke();
    void SetHeadlights(bool on) { _headlightsOn = on && Battery > 0; if (!_headlightsOn) _highBeam = false; }
    void SetTaillights(bool on) => _taillightsOn = on;
    public void Extinguish(bool wreck)
    {
        if (wreck) _exploded = true;
        EngineOn = false; SetHeadlights(false); SetTaillights(false); _sirenOn = false;
        _alarmed = false; _alarmTimer = 0; _alarmBlip = 0; _alarmLit = false;
    }
    public byte Flags(byte bodyFlags) => (byte)((bodyFlags & (VehicleReplication.FlagBraking | VehicleReplication.FlagExploded))
        | (EngineOn ? VehicleReplication.FlagEngineOn : 0) | (_headlightsOn ? VehicleReplication.FlagHeadlights : 0)
        | (_taillightsOn ? VehicleReplication.FlagTaillights : 0) | (_sirenOn ? VehicleReplication.FlagSiren : 0)
        | (_alarmed ? VehicleReplication.FlagAlarmed : 0) | (_alarmTimer > 0 ? VehicleReplication.FlagAlarming : 0));
    public void Adopt(byte flags, bool high, int pattern)
    {
        EngineOn = (flags & VehicleReplication.FlagEngineOn) != 0; _exploded = (flags & VehicleReplication.FlagExploded) != 0;
        _headlightsOn = (flags & VehicleReplication.FlagHeadlights) != 0; _taillightsOn = (flags & VehicleReplication.FlagTaillights) != 0;
        _sirenOn = (flags & VehicleReplication.FlagSiren) != 0; _alarmed = (flags & VehicleReplication.FlagAlarmed) != 0;
        _highBeam = high && _headlightsOn; LightbarPattern = pattern;
    }
}
