namespace MabiAgent.Capture;

public sealed record NpcapAvailability(bool Available, string? Detail)
{
    public const string OfficialDownloadUrl = "https://npcap.com/#download";

    // Load the same native API used by capture and enumerate adapters. Never open a
    // capture handle here: onboarding must not start capture or request elevation.
    public static NpcapAvailability Check(Func<IReadOnlyList<CaptureDevice>>? discover = null)
    {
        try
        {
            var devices = (discover ?? NpcapPacketSource.DiscoverDeviceDetails)();
            return devices.Count > 0
                ? new(true, null)
                : new(false, "Npcap 已加载，但未发现可用抓包网卡。请检查网络连接和驱动状态；若安装不完整，可从官网重新安装。");
        }
        catch (DllNotFoundException)
        {
            return new(false, "未检测到可用的 Npcap 组件。请从官网下载安装 Npcap，完成后重新启动 MabiAgent。");
        }
        catch (Exception error) when (error is BadImageFormatException or EntryPointNotFoundException)
        {
            return new(false, "Npcap 组件不完整或版本不兼容。请从官网安装适用于当前 Windows 的版本，然后重新启动 MabiAgent。");
        }
        catch (Exception error)
        {
            return new(false, "Npcap 暂不可用，请检查驱动和访问权限。检测信息：" + error.Message);
        }
    }
}
