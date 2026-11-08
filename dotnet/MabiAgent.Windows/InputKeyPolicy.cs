namespace MabiAgent.Windows;

internal static class InputKeyPolicy
{
    internal static bool IsShortcut(ushort key)=>key is 13 or 27 or 32 or 65 or 68 or 73 or 75 or 87 or 83 or 88;
    internal static bool IsSupported(ushort key)=>key==8||IsShortcut(key);
}
