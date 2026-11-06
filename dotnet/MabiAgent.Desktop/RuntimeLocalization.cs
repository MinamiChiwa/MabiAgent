using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using MabiAgent.Desktop.Controls;

namespace MabiAgent.Desktop;

public static class RuntimeLocalization
{
    private static readonly ConditionalWeakTable<AvaloniaObject, TextSources> Sources = new();
    private static readonly List<WeakReference<AvaloniaObject>> Tracked = [];
    private static readonly object Gate = new();
    private static bool applying;
    private static bool renderingDocument;
    private static readonly HashSet<(AvaloniaObject Target,AvaloniaProperty Property)> ApplyingProperties = [];
    private static bool initialized;

    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        Editors.WorkbenchLabels.Localize=UiLanguageService.Shared.WorkbenchText;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((control, change) => Observe(control, change.Property, change.NewValue));
        Run.TextProperty.Changed.AddClassHandler<Run>((control, change) => Observe(control, change.Property, change.NewValue));
        DataGridColumn.HeaderProperty.Changed.AddClassHandler<DataGridColumn>((column, change) => Observe(column, change.Property, change.NewValue));
        ContentControl.ContentProperty.Changed.AddClassHandler<ContentControl>((control, change) => Observe(control, change.Property, change.NewValue));
        HeaderedContentControl.HeaderProperty.Changed.AddClassHandler<HeaderedContentControl>((control, change) => Observe(control, change.Property, change.NewValue));
        TextBox.PlaceholderTextProperty.Changed.AddClassHandler<TextBox>((control, change) => Observe(control, change.Property, change.NewValue));
        Window.TitleProperty.Changed.AddClassHandler<Window>((control, change) => Observe(control, change.Property, change.NewValue));
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, change) => Observe(control, change.Property, change.NewValue));
        AutomationProperties.NameProperty.Changed.AddClassHandler<Control>((control, change) => Observe(control, change.Property, change.NewValue));
        HelpButton.MessageProperty.Changed.AddClassHandler<HelpButton>((control, change) => Observe(control, change.Property, change.NewValue));
        DeferredSettingsSection.PlaceholderTitleProperty.Changed.AddClassHandler<DeferredSettingsSection>((control, change) => Observe(control, change.Property, change.NewValue));
        DeferredSettingsSection.PlaceholderCaptionProperty.Changed.AddClassHandler<DeferredSettingsSection>((control, change) => Observe(control, change.Property, change.NewValue));
        UiLanguageService.Shared.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(RefreshAll, DispatcherPriority.Background);
    }

    public static string Translate(string source) => UiLanguageService.Shared.TranslateText(source);

    public static void RefreshAll()
    {
        if (applying) return;
        applying = true;
        try
        {
            WeakReference<AvaloniaObject>[] snapshot;
            lock (Gate)
            {
                Tracked.RemoveAll(reference=>!reference.TryGetTarget(out _));
                snapshot = Tracked.ToArray();
            }
            foreach (var reference in snapshot)
            {
                if (!reference.TryGetTarget(out var target)) continue;
                if (Sources.TryGetValue(target, out var values))
                {
                    foreach (var pair in values.Values.ToArray()) Apply(target, pair.Key, pair.Value);
                }
            }
        }
        finally { applying = false; }
    }

    public static string SourceValue(AvaloniaObject target, AvaloniaProperty property)
        => Sources.TryGetValue(target, out var values) && values.Values.TryGetValue(property, out var source)
            ? UiLanguageService.Shared.TranslateText(source)
            : target.GetValue(property)?.ToString() ?? "";

    internal static string OriginalValue(AvaloniaObject target,AvaloniaProperty property,string fallback)
        => Sources.TryGetValue(target,out var values)&&values.Values.TryGetValue(property,out var source)?source:fallback;

    // Markdown already has authored translations; retain its text verbatim.
    internal static void RenderOriginalDocument(Action render)
    {
        var previous = renderingDocument;
        renderingDocument = true;
        try { render(); }
        finally { renderingDocument = previous; }
    }

    private static void Observe(AvaloniaObject target, AvaloniaProperty property, object? value)
    {
        if (renderingDocument || ApplyingProperties.Contains((target,property)) || value is not string text) return;
        if (!Sources.TryGetValue(target, out var values))
        {
            lock (Gate)
            {
                if (!Sources.TryGetValue(target, out values))
                {
                    values = new TextSources();
                    Sources.Add(target, values);
                    Tracked.Add(new WeakReference<AvaloniaObject>(target));
                }
            }
        }
        values.Values[property] = text;
        Apply(target, property, text);
    }

    private sealed class TextSources
    {
        public Dictionary<AvaloniaProperty, string> Values { get; } = [];
    }

    private static void Apply(AvaloniaObject target, AvaloniaProperty property, string source)
    {
        if (target.GetValue(property) is not string current) return;
        var translated = UiLanguageService.Shared.TranslateText(source);
        if (current == translated) return;
        if(!ApplyingProperties.Add((target,property)))return;
        try
        {
            target.SetCurrentValue(property, translated);
        }
        finally { ApplyingProperties.Remove((target,property)); }
    }
}
