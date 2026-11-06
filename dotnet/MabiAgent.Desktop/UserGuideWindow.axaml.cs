using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ursa.Controls;

namespace MabiAgent.Desktop;

public partial class UserGuideWindow : UrsaWindow
{
    private readonly UiLanguageService language = UiLanguageService.Shared;
    private string currentDocument = "guide";
    private bool closed;
    private int renderVersion;

    public UserGuideWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        Document.LinkClicked += (_, e) => { e.Handled = true; Navigate(e.Url); };
        language.LanguageChanged += LanguageChanged;
        Opened += (_, _) => Controls.WindowSizing.FitToWorkingArea(this, Owner as Window);
        Closed += (_, _) => { closed = true; language.LanguageChanged -= LanguageChanged; };
        Render(false);
    }

    private void LanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Render(true);
        else Dispatcher.UIThread.Post(() => { if (!closed) Render(true); });
    }

    private void GoHome(object? sender, RoutedEventArgs e)
    {
        currentDocument = "guide";
        Render(false);
    }

    private void Render(bool keepPosition)
    {
        var old = Document.Content as ScrollViewer;
        var fraction = keepPosition && old is not null && old.Extent.Height > old.Viewport.Height
            ? old.Offset.Y / (old.Extent.Height - old.Viewport.Height) : 0;
        var version = ++renderVersion;
        LinkError.IsVisible = false;
        HomeButton.IsVisible = currentDocument != "guide";
        RuntimeLocalization.RenderOriginalDocument(() => Document.Markdown = UserGuideDocument.Read(language.Culture, currentDocument));
        Dispatcher.UIThread.Post(() =>
        {
            if (closed || version != renderVersion || Document.Content is not ScrollViewer scroll) return;
            UpdateLayout();
            scroll.Offset = new Vector(0, fraction * Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        }, DispatcherPriority.Loaded);
    }

    internal void Navigate(string link)
    {
        LinkError.IsVisible = false;
        try
        {
            if (link.StartsWith('#')) { Document.ScrollToAnchor(Uri.UnescapeDataString(link[1..])); return; }
            if (UserGuideDocument.RelativeDocument(link) is { } destination)
            {
                if (destination is "zh-CN" or "zh-TW")
                {
                    currentDocument = "guide";
                    if (language.Culture != destination && DataContext is MainViewModel model) model.ChangeLanguage(destination);
                    else Render(true);
                }
                else { currentDocument = destination; Render(false); }
                var fragment = link.IndexOf('#');
                if (fragment >= 0) Dispatcher.UIThread.Post(() => { if (!closed) Document.ScrollToAnchor(Uri.UnescapeDataString(link[(fragment + 1)..])); }, DispatcherPriority.Loaded);
                return;
            }
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            LinkError.Text = RuntimeLocalization.Translate("无法打开链接") + "：" + error.Message;
            LinkError.IsVisible = true;
        }
    }
}
