namespace MabiAgent.Desktop;

public static class ProductInfo
{
    public static string Version=>typeof(ProductInfo).Assembly.GetName().Version!.ToString(3);
    public static string Title=>"MabiAgent "+Version;
}
