using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MabiAgent.Desktop.Controls;

/// <summary>
/// Reserves layout space for an expensive settings card and builds its data template
/// once the card actually intersects the local viewport. A visible card is built in one
/// bounded dispatcher turn and retained for the lifetime of the settings page so
/// scrolling back does not recreate its controls.
/// </summary>
public sealed class DeferredSettingsSection : Decorator
{
    private static readonly Queue<WeakReference<DeferredSettingsSection>> Pending = [];
    private static bool drainScheduled;
    internal static bool HasPendingBuilds => drainScheduled || Pending.Count > 0;

    public static readonly StyledProperty<IDataTemplate?> SectionTemplateProperty =
        AvaloniaProperty.Register<DeferredSettingsSection, IDataTemplate?>(nameof(SectionTemplate));

    public static readonly StyledProperty<double> EstimatedHeightProperty =
        AvaloniaProperty.Register<DeferredSettingsSection, double>(nameof(EstimatedHeight), 180);

    public static readonly StyledProperty<string?> PlaceholderTitleProperty =
        AvaloniaProperty.Register<DeferredSettingsSection, string?>(nameof(PlaceholderTitle));

    public static readonly StyledProperty<string?> PlaceholderCaptionProperty =
        AvaloniaProperty.Register<DeferredSettingsSection, string?>(nameof(PlaceholderCaption));

    private Rect viewport;
    private ScrollViewer? viewportOwner;
    private bool buildQueued;
    private bool built;
    private int placeholderMeasureCount;
    private int builtMeasureCount;
    private int builtArrangeCount;
    private double placeholderMeasureTotalMs;
    private double builtMeasureTotalMs;
    private double maxBuiltMeasureMs;
    private double builtArrangeTotalMs;
    private double maxBuiltArrangeMs;
    private double latestBuildMs;
    private double buildToNextArrangeMs=-1;
    private int placeholderRenderCount;
    private double placeholderRenderTotalMs;
    private double maxPlaceholderRenderMs;
    private Stopwatch? postBuildLayoutWatch;
    private bool contentBuilt;

    public IDataTemplate? SectionTemplate
    {
        get => GetValue(SectionTemplateProperty);
        set => SetValue(SectionTemplateProperty, value);
    }

    public double EstimatedHeight
    {
        get => GetValue(EstimatedHeightProperty);
        set => SetValue(EstimatedHeightProperty, value);
    }

    public string? PlaceholderTitle
    {
        get => GetValue(PlaceholderTitleProperty);
        set => SetValue(PlaceholderTitleProperty, value);
    }

    public string? PlaceholderCaption
    {
        get => GetValue(PlaceholderCaptionProperty);
        set => SetValue(PlaceholderCaptionProperty, value);
    }

    public bool IsBuilt => built;
    public Control? Content { get=>Child; private set=>Child=value; }
    internal event EventHandler? ContentBuilt;
    protected override Type StyleKeyOverride => typeof(Decorator);
    public DeferredSettingsSection()
    {

        EffectiveViewportChanged += (_, e) =>
        {
            viewport = e.EffectiveViewport;
            QueueBuildIfVisible(Bounds.Size);
        };
        AttachedToVisualTree += (_, _) =>
        {
            WarmPlaceholderTypography();
            AttachViewport();
            QueueBuildIfVisible(Bounds.Size);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            DetachViewport();
            CancelPendingBuild();
        };
    }

    private void WarmPlaceholderTypography()
    {
        if (built) return;
        var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal);
        _ = new FormattedText(PlaceholderTitle ?? "正在准备设置", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, 15, Brushes.Gray);
        typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal);
        _ = new FormattedText(PlaceholderCaption ?? "进入可见区域后加载此卡内容。", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, 12, Brushes.Gray);
    }

    private void AttachViewport()
    {
        DetachViewport();
        viewportOwner = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (viewportOwner is not null) viewportOwner.ScrollChanged += ViewportScrollChanged;
    }

    private void DetachViewport()
    {
        if (viewportOwner is not null) viewportOwner.ScrollChanged -= ViewportScrollChanged;
        viewportOwner = null;
    }

    private void ViewportScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (built) return;
        Dispatcher.UIThread.Post(() => QueueBuildIfVisible(Bounds.Size), DispatcherPriority.Background);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var watch=Stopwatch.StartNew();
        if(contentBuilt)
        {
            try{return base.MeasureOverride(availableSize);}
            finally
            {
                var elapsed=watch.Elapsed.TotalMilliseconds;
                builtMeasureCount++;builtMeasureTotalMs+=elapsed;maxBuiltMeasureMs=Math.Max(maxBuiltMeasureMs,elapsed);
            }
        }

        // Before realization this control is a single measured rectangle. The card and
        // title are drawn directly, avoiding a themed Border/StackPanel/TextBlock tree.
        try
        {
            var width=double.IsFinite(availableSize.Width)?availableSize.Width:0;
            return new Size(Math.Max(0,width),Math.Max(0,EstimatedHeight));
        }
        finally
        {
            var elapsed=watch.Elapsed.TotalMilliseconds;
            placeholderMeasureCount++;placeholderMeasureTotalMs+=elapsed;
        }
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var watch=Stopwatch.StartNew();
        try
        {
            var arranged=base.ArrangeOverride(finalSize);
            // Bounds can be zero during the first viewport notification. Arrange supplies
            // the valid local size and retries with the already received viewport.
            QueueBuildIfVisible(finalSize);
            if(contentBuilt&&postBuildLayoutWatch is { } pending)
            {
                buildToNextArrangeMs=pending.Elapsed.TotalMilliseconds;
                pending.Stop();
                postBuildLayoutWatch=null;
            }
            return arranged;
        }
        finally
        {
            if(contentBuilt)
            {
                var elapsed=watch.Elapsed.TotalMilliseconds;
                builtArrangeCount++;builtArrangeTotalMs+=elapsed;maxBuiltArrangeMs=Math.Max(maxBuiltArrangeMs,elapsed);
            }
        }
    }
    private void QueueBuildIfVisible(Size boundsSize)
    {
        if (built || SectionTemplate is null || !ShouldBuild(viewport, boundsSize))
        {
            CancelPendingBuild();
            return;
        }

        if (buildQueued) return;
        buildQueued = true;
        Pending.Enqueue(new WeakReference<DeferredSettingsSection>(this));
        if (drainScheduled) return;
        drainScheduled = true;
        Dispatcher.UIThread.Post(DrainOne, DispatcherPriority.Background);
    }

    private static void DrainOne()
    {
        drainScheduled = false;
        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException("延迟设置卡只能在 Avalonia UI 线程构建。");

        // One card is one bounded UI chunk. A following card is only considered on
        // the next dispatcher turn, so one priority batch cannot concatenate every card.
        while (Pending.TryDequeue(out var reference))
        {
            if (!reference.TryGetTarget(out var section)) continue;
            section.RunQueuedBuild();
            if (Pending.Count == 0) return;
            drainScheduled = true;
            Dispatcher.UIThread.Post(DrainOne, DispatcherPriority.Background);
            return;
        }
    }

    private void RunQueuedBuild()
    {
        if (!buildQueued) return;
        buildQueued = false;
        if (built || !ShouldBuild(viewport, Bounds.Size)) return;
        var template = SectionTemplate ?? throw new InvalidOperationException($"设置卡 {Name ?? GetType().Name} 缺少数据模板。");
        BuildContent(template);
    }

    internal void EnsureBuilt()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(EnsureBuilt, DispatcherPriority.Normal);
            return;
        }
        if (built) return;
        buildQueued = false;
        if (!contentBuilt)
        {
            var template = SectionTemplate ?? throw new InvalidOperationException($"设置卡 {Name ?? GetType().Name} 缺少数据模板。");
            BuildContent(template);
        }
        InvalidateMeasure();
    }

    private void BuildContent(IDataTemplate template)
    {
        var watch=Stopwatch.StartNew();
        try
        {
            var content=template.Build(DataContext)
                ??throw new InvalidOperationException($"设置卡 {Name??GetType().Name} 的数据模板未返回控件。");
            // The estimate reserves the unloaded slot; realized content must grow
            // with themed controls and wrapped captions to keep every row in its card.
            content.MinHeight=Math.Max(0,EstimatedHeight);
            contentBuilt=true;
            built=true;
            Child=content;
            ContentBuilt?.Invoke(this,EventArgs.Empty);
            InvalidateMeasure();
            postBuildLayoutWatch=Stopwatch.StartNew();
        }
        catch
        {
            contentBuilt=false;
            built=false;
            Child=null;
            postBuildLayoutWatch=null;
            throw;
        }
        finally
        {
            latestBuildMs=watch.Elapsed.TotalMilliseconds;
        }
    }

    private void CancelPendingBuild()
    {
        // The queued weak reference is harmless: DrainOne skips targets whose token
        // was cleared after a page switch or visual-tree detach.
        buildQueued = false;
    }

    private bool ShouldBuild(Rect currentViewport, Size boundsSize)
    {
        if (boundsSize.Width <= 0 || boundsSize.Height <= 0) return false;
        var scrollViewer = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is not null
            && this.TranslatePoint(new Point(0, 0), scrollViewer) is { } localTopLeft)
        {
            // An offscreen card may not have received an EffectiveViewport yet.
            // Its rendered position against the owning ScrollViewer is still authoritative.
            var viewportSize = scrollViewer.Viewport;
            if (viewportSize.Width <= 0 || viewportSize.Height <= 0) return false;
            var card = new Rect(localTopLeft, boundsSize);
            // Do not realize the next card ahead of the user's viewport. A settings
            // card can be expensive to measure; exact visibility keeps first entry
            // responsive and lets scrolling provide the natural realization trigger.
            var viewport = new Rect(-24, -24, viewportSize.Width + 48, viewportSize.Height + 48);
            return card.Intersects(viewport);
        }
        return IsEffectivelyVisible && currentViewport.Width > 0 && currentViewport.Height > 0
            && currentViewport.X + currentViewport.Width > 0
            && currentViewport.Y + currentViewport.Height > 0
            && currentViewport.X < boundsSize.Width
            && currentViewport.Y < boundsSize.Height;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SectionTemplateProperty)
        {
            CancelPendingBuild();
            if (!built) QueueBuildIfVisible(Bounds.Size);
        }
        else if (!built && change.Property is { } property
            && (property == PlaceholderTitleProperty || property == PlaceholderCaptionProperty || property == EstimatedHeightProperty))
        {
            InvalidateVisual();
        }
    }

    internal sealed record Profile(string Name,int PlaceholderMeasures,double PlaceholderMeasureMs,int PlaceholderRenders,double PlaceholderRenderMs,double MaxPlaceholderRenderMs,int StagedReveals,double StagedRevealMs,double MaxStagedRevealToArrangeMs,int BuiltMeasures,double BuiltMeasureMs,double MaxBuiltMeasureMs,int BuiltArranges,double BuiltArrangeMs,double MaxBuiltArrangeMs,double BuildMs,double BuildToNextArrangeMs,bool Built);
    internal Profile SnapshotProfile()=>new(Name??GetType().Name,placeholderMeasureCount,placeholderMeasureTotalMs,placeholderRenderCount,placeholderRenderTotalMs,maxPlaceholderRenderMs,0,0,0,builtMeasureCount,builtMeasureTotalMs,maxBuiltMeasureMs,builtArrangeCount,builtArrangeTotalMs,maxBuiltArrangeMs,latestBuildMs,buildToNextArrangeMs,built);

    public override void Render(DrawingContext context)
    {
        if(contentBuilt)
        {
            base.Render(context);
            return;
        }
        var watch=Stopwatch.StartNew();
        try
        {
            if(Bounds.Width<=0||Bounds.Height<=0)return;
            var background=ResourceBrush("WorkspaceSettingsSurface",Brushes.Transparent);
            var border=ResourceBrush("WorkspaceBorder",Brushes.Transparent);
            var muted=ResourceBrush("WorkspaceMuted",Brushes.Gray);
            var height=Math.Min(Bounds.Height,Math.Max(0,EstimatedHeight));
            context.DrawRectangle(background,new Pen(border,1),new Rect(0,0,Bounds.Width,height),20,20,default);
            var maxWidth=Math.Max(0,Bounds.Width-36);
            var title=new FormattedText(PlaceholderTitle??"正在准备设置",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(FontFamily.Default,FontStyle.Normal,FontWeight.SemiBold,FontStretch.Normal),15,muted){MaxTextWidth=maxWidth};
            context.DrawText(title,new Point(18,16));
            var caption=new FormattedText(PlaceholderCaption??"进入可见区域后加载此卡内容。",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(FontFamily.Default,FontStyle.Normal,FontWeight.Normal,FontStretch.Normal),12,muted){MaxTextWidth=maxWidth};
            context.DrawText(caption,new Point(18,44));
        }
        finally
        {
            var elapsed=watch.Elapsed.TotalMilliseconds;
            placeholderRenderCount++;placeholderRenderTotalMs+=elapsed;maxPlaceholderRenderMs=Math.Max(maxPlaceholderRenderMs,elapsed);
        }
    }

    private IBrush ResourceBrush(string key,IBrush fallback)=>TryGetResource(key,ActualThemeVariant,out var value)&&value is IBrush brush?brush:fallback;
}
