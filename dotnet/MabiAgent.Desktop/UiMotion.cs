using System.Runtime.InteropServices;

namespace MabiAgent.Desktop;

/// <summary>
/// Reads the Windows "show animations" preference. When it is off, entrance motion
/// is skipped entirely instead of played shorter: the final state is what the user
/// asked for, and a moving card is still motion.
/// </summary>
internal static class UiMotion
{
    private const uint GetClientAreaAnimation = 0x1042;
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out bool value, uint update);
    private static bool? reduced;
    internal static bool Reduced => reduced ??= Read();
    private static bool Read()
    {
        try { return SystemParametersInfo(GetClientAreaAnimation, 0, out var animations, 0) && !animations; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
    /// <summary>Test seam: the self-check must not depend on the desktop settings of the machine.</summary>
    internal static void Override(bool? value) => reduced = value;
}
