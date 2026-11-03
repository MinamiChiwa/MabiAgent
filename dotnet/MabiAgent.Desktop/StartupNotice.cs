using System.IO;
using Ursa.Controls;
namespace MabiAgent.Desktop;
// Risk notice shown once per data directory before the tool can touch the game.
internal static class StartupNotice
{
    public const int Version=1;
    public const string Text=
        "MabiAgent 只用于技术研究和功能测试，与游戏官方无关。\n\n"+
        "· 使用第三方自动化工具可能违反游戏用户协议，账号可能被警告、限制或封禁；识别错误也可能导致道具被误用或误卖。以上风险由你自行承担。\n"+
        "· 建议只在测试账号或可接受损失的角色上使用。\n"+
        "· 本工具禁止任何商业用途，包括付费代挂、出售或出租。\n\n"+
        "点击“确定”表示你已阅读并同意以上内容；点击“取消”将退出程序。";
    static string MarkerPath(string dataPath)=>Path.Combine(dataPath,"notice-accepted.json");
    public static bool IsAccepted(string dataPath)
    {
        try{using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(MarkerPath(dataPath)));return json.RootElement.GetProperty("version").GetInt32()>=Version;}
        catch(Exception){return false;}
    }
    public static async Task<bool> ConfirmAsync(string dataPath)
    {
        if(IsAccepted(dataPath))return true;
        if(await MessageBox.ShowAsync(Text,"使用前须知",MessageBoxIcon.Warning,MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return false;
        Directory.CreateDirectory(dataPath);
        File.WriteAllText(MarkerPath(dataPath),System.Text.Json.JsonSerializer.Serialize(new{version=Version,accepted_at=DateTimeOffset.Now}));
        return true;
    }
}
