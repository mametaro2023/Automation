using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutomationTool;

public sealed class HotkeyHoldMonitor : IDisposable
{
    private readonly NativeMethods.HookProc _keyboardProc;
    private readonly object _sync = new();
    private List<HashSet<Keys>> _requiredKeyGroups = new();
    private Action? _released;
    private IntPtr _keyboardHook;
    private bool _releaseNotified;

    public HotkeyHoldMonitor()
    {
        _keyboardProc = KeyboardHookCallback;
    }

    public bool IsMonitoring
    {
        get
        {
            lock (_sync)
            {
                return _keyboardHook != IntPtr.Zero;
            }
        }
    }

    public void Start(HotkeyGesture gesture, Action released)
    {
        if (gesture.IsEmpty)
        {
            throw new ArgumentException("Hotkey is empty.", nameof(gesture));
        }

        Stop();
        lock (_sync)
        {
            _requiredKeyGroups = CreateRequiredKeyGroups(gesture);
            _released = released;
            _releaseNotified = false;
            _keyboardHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _keyboardProc,
                IntPtr.Zero,
                0);
            if (_keyboardHook == IntPtr.Zero)
            {
                _requiredKeyGroups.Clear();
                _released = null;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install hold-hotkey release hook.");
            }
        }
    }

    public void Stop()
    {
        IntPtr hook;
        lock (_sync)
        {
            hook = _keyboardHook;
            _keyboardHook = IntPtr.Zero;
            _requiredKeyGroups.Clear();
            _released = null;
            _releaseNotified = false;
        }

        if (hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(hook);
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var message = wParam.ToInt32();
        if (nCode >= 0
            && (message == NativeMethods.WM_KEYUP || message == NativeMethods.WM_SYSKEYUP))
        {
            var data = Marshal.PtrToStructure<NativeMethods.KeyboardHookStruct>(lParam);
            if ((data.Flags & NativeMethods.LLKHF_INJECTED) == 0)
            {
                NotifyIfRequiredKeyReleased((Keys)data.VkCode);
            }
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void NotifyIfRequiredKeyReleased(Keys releasedKey)
    {
        Action? callback = null;
        lock (_sync)
        {
            if (_releaseNotified || _keyboardHook == IntPtr.Zero)
            {
                return;
            }

            if (_requiredKeyGroups.Any(group => group.Contains(releasedKey)))
            {
                _releaseNotified = true;
                callback = _released;
            }
        }

        if (callback is not null)
        {
            ThreadPool.QueueUserWorkItem(_ => callback());
        }
    }

    private static List<HashSet<Keys>> CreateRequiredKeyGroups(HotkeyGesture gesture)
    {
        var groups = new List<HashSet<Keys>>
        {
            new() { gesture.Key }
        };

        if (gesture.Ctrl)
        {
            groups.Add(new HashSet<Keys> { Keys.ControlKey, Keys.LControlKey, Keys.RControlKey });
        }

        if (gesture.Alt)
        {
            groups.Add(new HashSet<Keys> { Keys.Menu, Keys.LMenu, Keys.RMenu });
        }

        if (gesture.Shift)
        {
            groups.Add(new HashSet<Keys> { Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey });
        }

        if (gesture.Win)
        {
            groups.Add(new HashSet<Keys> { Keys.LWin, Keys.RWin });
        }

        return groups;
    }

    public void Dispose()
    {
        Stop();
    }
}
