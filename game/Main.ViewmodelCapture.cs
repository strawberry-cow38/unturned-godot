using Godot;
using System;
using System.Globalization;
using SDG.Unturned;
using Environment = System.Environment;

namespace UnturnedGodot
{
    public partial class Main
    {
        // Optional deterministic action study for --vm. The regular capture
        // sequence and its frame numbers remain the default.
        string _vmAction;
        int _vmActionAt = 60;
        bool _vmActionStarted, _vmActionStopped;
        double _vmActionElapsed;
        float _vmActionSpeed = 1f;

        void ConfigureViewmodelCapture()
        {
            var caps = Environment.GetEnvironmentVariable("UG_VMCAPS");
            if (!string.IsNullOrEmpty(caps))
            {
                var values = caps.Split(',');
                var frames = new int[values.Length];
                for (int i = 0; i < frames.Length; i++)
                    if (!int.TryParse(values[i], out frames[i]) || frames[i] <= (i == 0 ? 0 : frames[i - 1]))
                        throw new ArgumentException("UG_VMCAPS must contain ascending positive frame numbers.");
                _rigCaptureFrames = frames;
            }
            _vmAction = Environment.GetEnvironmentVariable("UG_VM_ACTION");
            if (_vmAction == null) return;
            if (_vmAction is not ("reload" or "hammer" or "sprint" or "equip" or "ads" or "handling"))
                throw new ArgumentException("UG_VM_ACTION: reload, hammer, sprint, equip, ads or handling.");
            if (int.TryParse(Environment.GetEnvironmentVariable("UG_VM_ACTION_AT"), out int at)) _vmActionAt = at;
            if (float.TryParse(Environment.GetEnvironmentVariable("UG_VM_SPEED"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float speed) && speed > 0f) _vmActionSpeed = speed;
            GD.Print($"[vm-study] action={_vmAction}, at={_vmActionAt}, speed={_vmActionSpeed}, captures={string.Join(',', _rigCaptureFrames)}");
            if (float.TryParse(Environment.GetEnvironmentVariable("UG_VM_SNAPSHOT_TIME"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float poseTime) && poseTime >= 0f)
            {
                _vm.CaptureAnimationPose(_vmAction, poseTime);
                _vmActionStarted = _vmActionStopped = true;
                _vmAction = "snapshot";
                GD.Print($"[vm-study] frozen clip sample at {poseTime:0.###}s");
            }
        }

        bool TickViewmodelActionCapture(double delta)
        {
            if (!_vmTest || _vm == null || _vmAction == null) return false;
            if (!_vmActionStarted && _frame >= _vmActionAt && _vm.IsEquipComplete)
            {
                _vmActionStarted = true;
                _vmActionElapsed = 0;
                switch (_vmAction)
                {
                    case "reload": _vm.SetReloading(true, _vmActionSpeed); break;
                    case "hammer": case "handling": _vm.PlayHammer(_vmActionSpeed); break;
                    case "sprint": _vm.SetLocomotion(true, EPlayerStance.SPRINT); break;
                    case "ads": _vm.SetAiming(true); break;
                }
                GD.Print($"[vm-study] {_vmAction} fired at frame {_frame}, reload={_vm.ReloadLength:0.###}s, hammer={_vm.HammerLength:0.###}s");
            }
            else if (_vmActionStarted)
            {
                _vmActionElapsed += delta * _vmActionSpeed;
                if (_vmAction == "handling" && _frame == _vmActionAt + 48) _vm.SetLocomotion(true, EPlayerStance.SPRINT);
                if (_vmAction == "handling" && _frame == _vmActionAt + 78) _vm.SetLocomotion(false, EPlayerStance.STAND);
                if (!_vmActionStopped && _vmAction == "reload" && _vmActionElapsed >= _vm.ReloadLength)
                { _vm.SetReloading(false); _vmActionStopped = true; }
                if (!_vmActionStopped && _vmAction == "sprint" && _vmActionElapsed >= 1.0)
                { _vm.SetLocomotion(false, EPlayerStance.STAND); _vmActionStopped = true; }
            }
            return true;
        }
    }
}
