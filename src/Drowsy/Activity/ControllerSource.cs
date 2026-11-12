using Drowsy.Interop;

namespace Drowsy.Activity;

/// <summary>
/// XInput (Xbox-compatible) controller activity. Compares a quantised snapshot of each pad so
/// that analog-stick drift and trigger noise on an idle controller don't keep the PC awake.
/// </summary>
internal sealed unsafe class ControllerSource
{
    private const int Slots = 4;
    private const int StickDeadzone = 8000;   // ~XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE
    private const int TriggerThreshold = 30;  // XINPUT_GAMEPAD_TRIGGER_THRESHOLD
    private const long DisconnectedPollMs = 5_000; // polling empty slots is comparatively slow

    private readonly ulong?[] _snapshot = new ulong?[Slots];
    private long _nextDisconnectedPoll;
    private bool _available = true;

    /// <summary>Returns the slot number of a controller that changed state, or null.</summary>
    public int? Poll()
    {
        if (!_available) return null;

        long now = ActivityMonitor.Now;
        bool pollEmpty = now >= _nextDisconnectedPoll;
        if (pollEmpty) _nextDisconnectedPoll = now + DisconnectedPollMs;

        int? active = null;
        for (int i = 0; i < Slots; i++)
        {
            if (_snapshot[i] is null && !pollEmpty) continue;

            XINPUT_STATE state;
            uint result;
            try
            {
                result = Native.XInputGetState((uint)i, &state);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Log.Info("XInput is not available; controller detection disabled");
                _available = false;
                return null;
            }

            if (result != 0)
            {
                _snapshot[i] = null;
                continue;
            }

            ulong snap = Snapshot(state);
            if (_snapshot[i] is { } previous && previous != snap) active = i;
            _snapshot[i] = snap;
        }
        return active;
    }

    private static ulong Snapshot(in XINPUT_STATE s)
    {
        // 0 = centred (inside deadzone); 1..8 = coarse position bucket outside it.
        static ulong Stick(short v) => (ulong)(Math.Abs((int)v) < StickDeadzone ? 0 : (v / 8192) + 5) & 0xF;

        ulong snap = s.wButtons;
        snap |= (ulong)(s.bLeftTrigger > TriggerThreshold ? 1 : 0) << 16;
        snap |= (ulong)(s.bRightTrigger > TriggerThreshold ? 1 : 0) << 17;
        snap |= Stick(s.sThumbLX) << 20;
        snap |= Stick(s.sThumbLY) << 24;
        snap |= Stick(s.sThumbRX) << 28;
        snap |= Stick(s.sThumbRY) << 32;
        return snap;
    }
}
