using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutomationTool;

public sealed class HotkeyManager : IDisposable
{
    public const int EmergencyStopId = 900;
    public const int RecordingStopId = 901;
    private const int FirstMacroId = 1000;
    private readonly Dictionary<int, Guid> _registeredMacros = new();
    private readonly List<int> _registeredIds = new();
    private IntPtr _windowHandle;

    public IReadOnlyDictionary<int, Guid> RegisteredMacros => _registeredMacros;

    public void RegisterAll(
        IntPtr windowHandle,
        IEnumerable<Macro> macros,
        HotkeyGesture emergencyStopHotkey,
        HotkeyGesture recordingStopHotkey)
    {
        UnregisterAll();
        _windowHandle = windowHandle;

        if (!emergencyStopHotkey.IsEmpty)
        {
            Register(windowHandle, EmergencyStopId, emergencyStopHotkey);
        }

        if (!recordingStopHotkey.IsEmpty)
        {
            Register(windowHandle, RecordingStopId, recordingStopHotkey);
        }

        var id = FirstMacroId;
        foreach (var macro in macros.Where(m => m.IsEnabled && !m.Hotkey.IsEmpty))
        {
            Register(windowHandle, id, macro.Hotkey);
            _registeredMacros[id] = macro.Id;
            id++;
        }
    }

    public bool TryGetMacroId(int hotkeyId, out Guid macroId)
    {
        return _registeredMacros.TryGetValue(hotkeyId, out macroId);
    }

    private void Register(IntPtr windowHandle, int id, HotkeyGesture gesture)
    {
        var modifiers = NativeMethods.HotKeyModifiers.NoRepeat;
        if (gesture.Ctrl) modifiers |= NativeMethods.HotKeyModifiers.Control;
        if (gesture.Alt) modifiers |= NativeMethods.HotKeyModifiers.Alt;
        if (gesture.Shift) modifiers |= NativeMethods.HotKeyModifiers.Shift;
        if (gesture.Win) modifiers |= NativeMethods.HotKeyModifiers.Win;

        if (!NativeMethods.RegisterHotKey(windowHandle, id, modifiers, (uint)gesture.Key))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to register hotkey {gesture}.");
        }

        _registeredIds.Add(id);
    }

    public void UnregisterAll()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            foreach (var id in _registeredIds)
            {
                NativeMethods.UnregisterHotKey(_windowHandle, id);
            }
        }

        _registeredIds.Clear();
        _registeredMacros.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
    }
}
