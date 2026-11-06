using System.Text.Json.Nodes;
using MabiAgent.Workflows;

namespace MabiAgent.Windows;

/// <summary>Fast re-entry: ESC menu, appearance/rebirth entry, rebirth page, ESC exit menu, space exit.</summary>
public sealed class ScreenQuickReentry
{
    readonly ScreenWorkflowSession session;readonly JsonObject configuration;readonly QuickReentryScript script;
    readonly Dictionary<string,JsonObject> steps;
    ScreenMaintenancePort? preparation;DateTimeOffset preparationDeadline;bool prepared;
    readonly string fieldExitTarget;readonly string fieldExitPage;readonly TimeSpan fieldExitTimeout;
    DateTimeOffset? fieldExitStartedAt;bool fieldExitDispatched;
    int appearanceAttempts;DateTimeOffset appearanceRetryAt;DateTimeOffset appearanceClickedAt;DateTimeOffset? pendingClickAt;PixelPoint? stableAppearancePoint;
    string? dialogKind;
    (PixelBox Box,string Text,string Label,int Threshold)? menuEntry;
    internal PixelPoint? LastAppearancePoint{get;private set;}
    internal PixelBox? LastAppearanceBox{get;private set;}
    const int MaxAppearanceAttempts=3;
    static readonly TimeSpan AppearanceRetryDelay=TimeSpan.FromMilliseconds(2500);
    // Background touch reports the click as delivered even when the game drops it. After the
    // last retry, give the screen this long to change before declaring the input channel dead.
    static readonly TimeSpan AppearanceIneffectiveGrace=TimeSpan.FromMilliseconds(2000);
    // A click that closed the menu but opened nothing leaves the HUD back on screen; wait this
    // long before treating that as "the tap landed outside the entry".
    static readonly TimeSpan AppearanceSettledDelay=TimeSpan.FromMilliseconds(1200);
    public QuickReentryStatus Status=>script.Status;
    public string? Failure=>script.Failure;
    public string? CurrentStep=>script.Current?.Id;
    public ScreenQuickReentry(ScreenWorkflowSession session,Func<DateTimeOffset>? completedAt=null)
    {
        this.session=session;configuration=session.Routes.Configuration["quick_reentry"]?.DeepClone()?.AsObject()??throw new InvalidDataException("缺少快速重进脚本配置");
        var rows=(configuration["steps"] as JsonArray??throw new InvalidDataException("缺少快速重进步骤")).OfType<JsonObject>().ToArray();
        steps=rows.ToDictionary(s=>s["id"]!.GetValue<string>(),StringComparer.Ordinal);
        var fieldExit=configuration["field_exit"] as JsonObject;
        fieldExitTarget=fieldExit?["exit_target"]?.GetValue<string>()??"storage.room_exit";
        fieldExitPage=fieldExit?["confirmation_page"]?.GetValue<string>()??"storage_room_exit";
        fieldExitTimeout=TimeSpan.FromSeconds(Math.Clamp(fieldExit?["timeout_s"]?.GetValue<int>()??45,1,120));
        script=new(new(rows.Select(s=>new QuickReentryStep(s["id"]!.GetValue<string>(),s["kind"]!.GetValue<string>() switch{"key"=>QuickReentryStepKind.PressKey,"click_text"=>QuickReentryStepKind.ClickText,"click"=>QuickReentryStepKind.Click,"wait"=>QuickReentryStepKind.WaitForStable,_=>throw new InvalidDataException("快速重进步骤类型无效")},s["id"]!.GetValue<string>(),TimeSpan.FromMilliseconds(s["delay_ms"]?.GetValue<int>()??500)){Timeout=TimeSpan.FromSeconds(s["timeout_s"]?.GetValue<int>()??30)}).ToArray()),completedAt??(()=>DateTimeOffset.UtcNow));
    }
    public void Start(DateTimeOffset now){session.BeginRecoveryRecognition();preparationDeadline=now.AddSeconds(120);script.Start(now);}
    public void Pause()=>script.Pause();
    public bool Tick(DateTimeOffset now)
    {
        if((Status is QuickReentryStatus.Running or QuickReentryStatus.Complete)&&(CurrentStep is null or "loaded"))
        {if(session.DismissNotice(notificationsOnly:true))return false;}
        if(!prepared&&Status==QuickReentryStatus.Running)
        {
            var observed=session.Observe();
            if(observed.View.Page.StartsWith("notification_",StringComparison.Ordinal))
            {session.DismissNotice();return false;}
            var stableScene=observed.Stable&&observed.View.Page=="scene";
            var inField=stableScene&&observed.View.Targets.ContainsKey(fieldExitTarget);
            var onFieldExitPage=observed.Stable&&observed.View.Page==fieldExitPage;
            if(stableScene&&!inField)
            {
                if(fieldExitStartedAt is not null)session.TraceNavigation("quick_reentry","town_ready; field_exit_cleared","wait");
                fieldExitStartedAt=null;prepared=true;script.Pause();script.Continue(now);
            }
            else if(fieldExitStartedAt is not null||inField||onFieldExitPage)
            {
                preparation??=session.CreateMaintenancePort("quick-reentry-prepare",new PreparationStore());
                if(fieldExitStartedAt is null)
                {
                    fieldExitStartedAt=now;fieldExitDispatched=false;
                    session.TraceNavigation("quick_reentry",$"field_exit_required; target={fieldExitTarget}","wait");
                }
                if(now-fieldExitStartedAt.Value>=fieldExitTimeout)
                {
                    script.Fail("地图出口超时：未确认回到城镇，未发送重生");
                    return false;
                }
                // Once an exit was dispatched, any non-town frame (including an unknown load
                // screen) stays inside the exit timeout; it must never fall through to ESC.
                if(inField&&!fieldExitDispatched)
                {
                    preparation.Observe();
                    try
                    {
                        if(preparation.ReleaseFieldExit(fieldExitTarget))
                        {
                            fieldExitDispatched=true;
                            session.TraceNavigation("quick_reentry",$"field_exit_dispatched; target={fieldExitTarget}","input_dispatched");
                        }
                    }
                    catch(Exception error) when(error is InvalidOperationException or InputRejectedBeforeDispatchException)
                    {
                        script.Fail("地图出口未确认："+error.Message);
                    }
                }
                else if(onFieldExitPage)
                {
                    preparation.Observe();
                    try
                    {
                        if(preparation.ReleaseNavigation())
                        {
                            var after=session.LastObservation;
                            if(after is{Stable:true}&&after.View.Page=="scene"&&!after.View.Targets.ContainsKey(fieldExitTarget))
                            {
                                fieldExitStartedAt=null;prepared=true;
                                session.TraceNavigation("quick_reentry","town_ready; field_exit_cleared","wait");
                                script.Pause();script.Continue(now.AddSeconds(2));
                            }
                        }
                    }
                    catch(Exception error) when(error is InvalidOperationException or InputRejectedBeforeDispatchException)
                    {
                        script.Fail("地图出口未确认："+error.Message);
                    }
                }
                return false;
            }
            else if(now<preparationDeadline)
            {
                preparation??=session.CreateMaintenancePort("quick-reentry-prepare",new PreparationStore());
                preparation.Observe();
                if(preparation.ReleaseNavigation())
                {session.BeginRecoveryRecognition();prepared=true;script.Pause();script.Continue(now.AddSeconds(2));}
                return false;
            }
            else
            {
                // Do not send ESC from an unknown screen. The script fails closed at the first step.
                prepared=true;script.Pause();script.Continue(now);
            }
        }
        var before=CurrentStep;var prior=Status;
        var advanced=script.Tick(now,step=>
        {
            var source=steps[step.Id];if(step.Kind==QuickReentryStepKind.WaitForStable)return true;
            if(step.Kind==QuickReentryStepKind.ClickText)
            {
                // A missed click is not a failure: the menu still shows the entry, so stay on this
                // step until the locator succeeds or the step timeout fails the run closed.
                var capture=session.Guard.Capture();
                var searchConfig=source["search"] as JsonObject??throw new InvalidDataException("缺少文字定位配置");
                var hit=LocateEntry(capture.Image,searchConfig);
                if(hit is null)return false;
                PixelPoint? point=EntryPoint(capture.Image,hit.Value);LastAppearanceBox=hit.Value.Box;
                if(point is null)return false;
                // The menu slides in, so a single hit can be mid-animation. Click only after two
                // scans agree on the same spot.
                if(stableAppearancePoint is{} previous&&Math.Abs(previous.X-point.Value.X)+Math.Abs(previous.Y-point.Value.Y)>12)
                {stableAppearancePoint=point;return false;}
                stableAppearancePoint=point;LastAppearancePoint=point;
                session.Guard.ClickAt(ToClient(point.Value));
                appearanceAttempts++;
                appearanceRetryAt=now+AppearanceRetryDelay;appearanceClickedAt=now;
                session.TraceNavigation("quick_reentry",$"appearance_clicked; attempt={appearanceAttempts}; point={point.Value.X},{point.Value.Y}","input_dispatched");
                return true;
            }
            if(step.Kind==QuickReentryStepKind.PressKey)
            {
                // ESC from the appearance screen lands on the rebirth prompt, and only the second
                // ESC opens the cancel prompt. Skip the extra press when the cancel prompt is
                // already up so the chain never re-enters the appearance screen.
                if(step.Id=="escape_if_confirm"&&dialogKind=="cancel")return true;
                var key=checked((ushort)source["key"]!.GetValue<int>());
                var hold=Math.Clamp(source["hold_ms"]?.GetValue<int>()??80,20,500);
                session.Guard.PressKey(key,TimeSpan.FromMilliseconds(hold));
                return true;
            }
            if(step.Kind==QuickReentryStepKind.Click)
            {
                var point=source["point"]?.AsArray()??throw new InvalidDataException("缺少点击坐标");
                session.Guard.ClickAt(ToClient(new(point[0]!.GetValue<int>(),point[1]!.GetValue<int>())));
                return true;
            }
            throw new InputBlockedException("未审核的快速重进动作");
        },target=>
        {
            if(target=="loaded")return session.Observe().Stable&&session.CurrentPage=="scene";
            ClientCapture capture;
            try{capture=session.Guard.Capture();}
            catch(InputBlockedException error)when(error.CaptureTimedOut)
            {
                // 加载慢帧直接丢弃，沿用当前等待期限；不发送输入或重放前一步。
                session.TraceNavigation("quick_reentry",$"capture_expired; step={target}","wait");return false;
            }
            return target switch
            {
                // The ESC overlay is often reported as an unknown, unstable page while its list is
                // still settling. Readable menu text is stronger evidence than the page verdict.
                "menu_ready"=>MenuVisible(capture.Image),
                // The rebirth entry asks for confirmation first; sending the exit keys without
                // waiting for it would leave the dialog open and the whole chain stuck.
                "rebirth_ready"=>RebirthReady(now,capture),
                // Distinguishes the two stacked prompts so the chain presses ESC exactly once more
                // when it is still on the rebirth prompt.
                "dialog_kind"=>ClassifyDialog(capture.Image),
                "cancel_ready"=>TextVisible(capture.Image,"cancel_search"),
                "exit_menu_ready"=>TextVisible(capture.Image,"cancel_search"),
                _=>throw new InvalidDataException("缺少快速重进等待条件："+target)
            };
        });
        if(advanced)session.TraceNavigation("quick_reentry",before??"complete","script_step_dispatched");
        if(before!=CurrentStep||prior!=Status)session.TraceNavigation("quick_reentry",$"step={CurrentStep}; status={Status}; failure={Failure}","script_progress");
        return advanced;
    }
    // The in-game ESC menu is not the logout menu: its exit/settings anchors never match here.
    // Accept the logout anchors as well, but require the menu text itself for the in-game menu.
    private bool MenuVisible(RgbImage image)
        =>MenuChromeVisible(image)||MenuText(image) is not null;
    // The structural ESC-menu buttons survive while the animated entry text is unreadable.
    private bool MenuChromeVisible(RgbImage image)
        =>session.Matcher.Anchor(image,"relogin.menu_exit") is not null
          ||session.Matcher.Anchor(image,"relogin.menu_settings") is not null;
    private PixelPoint? MenuText(RgbImage image)
    {
        if(configuration["menu_search"] is JsonObject search)
        {
            menuEntry=LocateEntry(image,search);
            return menuEntry is{} hit?EntryPoint(image,hit):null;
        }
        var source=steps.TryGetValue("appearance",out var step)?step["search"] as JsonObject:null;
        return source is null?null:FindText(image,source);
    }
    private (PixelBox Box,string Text,string Label,int Threshold)? LocateEntry(RgbImage image,JsonObject search)
    {
        if(configuration["menu_icon"]?.GetValue<string>() is{} icon&&session.Matcher.Anchor(image,icon) is{} hit)
            return (hit.Box,"重生","重生",180);
        if(menuEntry is{} prior)
        {
            var text=session.QuantityReader.ColorText(image,prior.Box,search["minimum_confidence"]?.GetValue<double>()??.6);
            if(text?.Replace(" ","",StringComparison.Ordinal).Contains(prior.Label.Replace(" ","",StringComparison.Ordinal),StringComparison.Ordinal)==true)
                return (prior.Box,text,prior.Label,prior.Threshold);
        }
        return FindTextBox(image,search);
    }
    private bool ClassifyDialog(RgbImage image)
    {
        if(TextVisible(image,"cancel_search")){dialogKind="cancel";return true;}
        if(TextVisible(image,"confirm_search")){dialogKind="confirm";return true;}
        return false;
    }
    private bool TextVisible(RgbImage image,string key)
        =>configuration[key] is JsonObject search&&FindText(image,search) is not null;
    private bool RebirthReady(DateTimeOffset now,ClientCapture capture)
    {
        // The rebirth screen animates, so frame equality never settles. Its own fixed bottom
        // button is the only stable evidence, together with the menu being gone.
        if(TextVisible(capture.Image,"confirm_search")||TextVisible(capture.Image,"rebirth_search"))return true;
        RebirthConfirm(now,capture.Image);return false;
    }
    private bool RebirthConfirm(DateTimeOffset now,RgbImage image)
    {
        if(TextVisible(image,"confirm_search"))return true;
        if(pendingClickAt is{} due&&now>=due)ReclickAppearanceEntry(now);
        // The entry is still readable where the click was aimed: the input was dispatched but the
        // game never took it. Background touch can be dropped without any error, so re-click the
        // same spot instead of waiting out the whole step timeout on a menu that never moved.
        // The animated entry text may be unreadable while the structural ESC menu is still there.
        var entryVisible=MenuEntryVisible(image);
        if(entryVisible||MenuChromeVisible(image)&&LastAppearancePoint is not null)
        {
            if(appearanceAttempts<MaxAppearanceAttempts){if(now>=appearanceRetryAt)RetryAppearancePoint(now);return false;}
            if(now-appearanceClickedAt>=AppearanceIneffectiveGrace)
                throw new InputBlockedException($"變更外貌点击未生效：入口仍在菜单上，已补点 {appearanceAttempts} 次；疑似后台输入通道失效，请重启游戏客户端后重试");
            return false;
        }
        // The tap closed the menu without opening the rebirth screen: it landed outside the entry.
        if(now-appearanceClickedAt>=AppearanceSettledDelay&&AtMainScene(image))
        {
            if(appearanceAttempts<MaxAppearanceAttempts){if(now>=appearanceRetryAt)ReopenAppearanceEntry(now);return false;}
            if(now-appearanceClickedAt>=AppearanceIneffectiveGrace)
                throw new InputBlockedException($"變更外貌点击后菜单被关闭但未进入重生界面：已重试 {appearanceAttempts} 次，点击位置可能落在入口外，请检查菜单布局");
        }
        return false;
    }
    private void RetryAppearancePoint(DateTimeOffset now)
    {
        if(LastAppearancePoint is not{} point)return;
        session.Guard.ClickAt(ToClient(point));
        appearanceAttempts++;appearanceClickedAt=now;appearanceRetryAt=now+AppearanceRetryDelay;
        session.TraceNavigation("quick_reentry",$"appearance_retouch; attempt={appearanceAttempts}; point={point.X},{point.Y}","input_dispatched");
    }
    private void ReopenAppearanceEntry(DateTimeOffset now)
    {
        // Closing the menu without acting is recoverable: press ESC again and click once the
        // panel has finished sliding in.
        session.Guard.PressKey(0x1B,TimeSpan.FromMilliseconds(80));
        pendingClickAt=now.AddMilliseconds(1500);
        appearanceClickedAt=now;appearanceRetryAt=now+AppearanceRetryDelay;
        session.TraceNavigation("quick_reentry","appearance_reopen; esc dispatched","input_dispatched");
    }
    private void ReclickAppearanceEntry(DateTimeOffset now)
    {
        pendingClickAt=null;
        if(LastAppearancePoint is not{} point)return;
        session.Guard.ClickAt(ToClient(point));
        appearanceAttempts++;appearanceClickedAt=now;appearanceRetryAt=now+AppearanceRetryDelay;
        session.TraceNavigation("quick_reentry",$"appearance_reopen_click; attempt={appearanceAttempts}; point={point.X},{point.Y}","input_dispatched");
    }
    private bool AtMainScene(RgbImage image)
        =>session.Routes.Profile.Templates.ContainsKey("scene_menu")&&session.Matcher.Anchor(image,"scene_menu") is not null;
    // One crop around the clicked entry is enough to tell "still on the menu" from "screen moved
    // on". A full menu scan here would cost more than the timeout it replaces.
    private bool MenuEntryVisible(RgbImage image)
    {
        if(configuration["menu_icon"]?.GetValue<string>() is{} icon&&session.Matcher.Anchor(image,icon) is not null)return true;
        if(LastAppearancePoint is not{} point)return false;
        var box=new PixelBox(Math.Max(0,point.X-70),Math.Max(0,point.Y-26),Math.Min(image.Width,point.X+150),Math.Min(image.Height,point.Y+58));
        var text=session.QuantityReader.ColorText(image,box,.5);
        return text is not null&&(text.Contains("外貌",StringComparison.Ordinal)||text.Contains("重生",StringComparison.Ordinal));
    }
    // OCR over the menu block is the expensive part. Cache per frame hash so a static menu is
    // scanned once instead of on every tick, and so two different searches never share a hit.
    readonly Dictionary<(string Hash,JsonObject Search),PixelPoint?> located=[];
    private PixelPoint? FindText(RgbImage image,JsonObject search)
    {
        var key=(image.Sha256,search);
        if(located.TryGetValue(key,out var cached))return cached;
        PixelPoint? point=FindTextBox(image,search) is{} hit?EntryPoint(image,hit):(PixelPoint?)null;
        if(located.Count>=16)located.Clear();
        located[key]=point;return point;
    }
    // A wide crop can read the label plus the neighbouring entry, so the crop centre may sit past
    // the label. When the label starts the recognised text, aim at its glyph run instead.
    private PixelPoint EntryPoint(RgbImage image,(PixelBox Box,string Text,string Label,int Threshold) hit)
    {
        var normalized=hit.Text.Replace(" ","",StringComparison.Ordinal);
        if(!normalized.StartsWith(hit.Label.Replace(" ","",StringComparison.Ordinal),StringComparison.Ordinal))return hit.Box.Center;
        return GlyphPoint(image,hit.Box)??hit.Box.Center;
    }
    // Menu labels are near-white but item icons are brighter, and the translucent panel lifts its
    // background with the scenery behind. Threshold from the crop's own distribution, then take
    // the thin text band (icons fill whole columns) and click the middle of its glyphs. The old
    // "first bright column + 45" clamped to the crop edge and clicked past the label.
    internal static PixelPoint? GlyphPoint(RgbImage image,PixelBox box)
    {
        var width=box.Width;var height=box.Height;
        if(width<24||height<12)return null;
        var levels=new byte[width*height];var histogram=new int[256];
        for(var y=0;y<height;y++)for(var x=0;x<width;x++)
        {
            var offset=((box.Top+y)*image.Width+box.Left+x)*3;
            var level=Math.Min(image.Pixels[offset],Math.Min(image.Pixels[offset+1],image.Pixels[offset+2]));
            levels[y*width+x]=level;histogram[level]++;
        }
        var background=Percentile(histogram,levels.Length,.5);
        var bright=Percentile(histogram,levels.Length,.99);
        var threshold=Math.Clamp(background+(bright-background)*11/20,120,210);
        PixelPoint? best=null;var bestScore=int.MaxValue;
        for(var row=0;row<height;)
        {
            if(RowInk(levels,width,row,threshold)<3){row++;continue;}
            var end=row;var ink=0;
            while(end<height){var line=RowInk(levels,width,end,threshold);if(line<3)break;ink+=line;end++;}
            var bandHeight=end-row;
            if(bandHeight is>=8 and<=34&&ink>=20)
            {
                var (left,right,peak,all)=LongestRun(levels,width,row,end,threshold);
                if(all>=24&&peak<=26)
                {
                    // Text strokes never fill a column the way an icon does.
                    var score=Math.Abs((row+end)/2-height/2);
                    if(score<bestScore){bestScore=score;best=new(box.Left+(left+right)/2,box.Top+(row+end)/2);}
                }
            }
            row=end+1;
        }
        return best;
    }
    private static int RowInk(byte[] levels,int width,int row,int threshold)
    {
        var start=row*width;var count=0;
        for(var x=0;x<width;x++)if(levels[start+x]>=threshold)count++;
        return count;
    }
    // Runs of glyph columns inside one text band. The crop usually reaches back over the previous
    // menu cell, so the label is the right-most text run; neighbours read dimmer and are dropped
    // by the threshold.
    private static (int Left,int Right,int Peak,int Length) LongestRun(byte[] levels,int width,int top,int bottom,int threshold)
    {
        var columns=new int[width];
        for(var y=top;y<bottom;y++){var start=y*width;for(var x=0;x<width;x++)if(levels[start+x]>=threshold)columns[x]++;}
        var bestLeft=-1;var bestRight=-1;var bestPeak=0;var longestLeft=-1;var longestRight=-1;var longestPeak=0;
        for(var x=0;x<width;)
        {
            if(columns[x]<2){x++;continue;}
            var end=x;var gap=0;var peak=0;
            for(var probe=x;probe<width;probe++)
            {
                if(columns[probe]>=2){end=probe;gap=0;peak=Math.Max(peak,columns[probe]);}
                else if(++gap>12)break;
            }
            // The label is the brightest content in the crop; neighbours read dimmer and never pass
            // the threshold, so the left-most qualifying run is the entry itself.
            if(end-x>=24&&bestLeft<0){bestLeft=x;bestRight=end;bestPeak=peak;}
            if(end-x>longestRight-longestLeft){longestLeft=x;longestRight=end;longestPeak=peak;}
            x=end+1;
        }
        if(bestLeft<0)return (longestLeft,longestRight,longestPeak,longestRight-longestLeft);
        return (bestLeft,bestRight,bestPeak,bestRight-bestLeft);
    }
    private static int Percentile(int[] histogram,int total,double fraction)
    {
        var target=total*fraction;var seen=0;
        for(var level=0;level<256;level++){seen+=histogram[level];if(seen>target)return level;}
        return 255;
    }
    private (PixelBox Box,string Text,string Label,int Threshold)? FindTextBox(RgbImage image,JsonObject search)
    {
        var labels=(search["labels"] as JsonArray??throw new InvalidDataException("缺少文字候选")).Select(n=>n!.GetValue<string>()).Where(n=>!string.IsNullOrWhiteSpace(n)).ToArray();
        if(labels.Length==0)throw new InvalidDataException("文字候选不能为空");
        var region=session.Box(search["region"]??throw new InvalidDataException("缺少文字搜索区域"));
        var width=Math.Clamp(search["crop_width"]?.GetValue<int>()??180,40,400);
        var height=Math.Clamp(search["crop_height"]?.GetValue<int>()??44,20,100);
        var stepX=Math.Clamp(search["step_x"]?.GetValue<int>()??12,4,80);
        var stepY=Math.Clamp(search["step_y"]?.GetValue<int>()??8,4,60);
        var minimum=Math.Clamp(search["minimum_confidence"]?.GetValue<double>()??.75,.5,1);
        var threshold=Math.Clamp(search["light_threshold"]?.GetValue<int>()??140,0,255);
        var minimumInk=Math.Clamp(search["minimum_ink"]?.GetValue<int>()??8,1,width*height);
        for(var y=region.Top;y+height<=region.Bottom;y+=stepY)
        {
            // Cheap row gate: a menu row without enough bright pixels cannot hold the label.
            if(LightPixels(image,new PixelBox(region.Left,y,region.Right,y+height),threshold)<minimumInk*4)continue;
            for(var x=region.Left;x+width<=region.Right;x+=stepX)
            {
                var box=new PixelBox(x,y,x+width,y+height);if(LightPixels(image,box,threshold)<minimumInk)continue;
                var text=session.QuantityReader.ColorText(image,box,minimum);
                if(text is null)continue;
                var normalized=text.Replace(" ","",StringComparison.Ordinal);
                foreach(var label in labels)
                {
                    if(!normalized.Contains(label.Replace(" ","",StringComparison.Ordinal),StringComparison.Ordinal))continue;
                    return (box,text,label,threshold);
                }
            }
            // A text line is shorter than the crop, so one scanned band covers its neighbours.
            y+=height/2;
        }
        return null;
    }
    private static int LightPixels(RgbImage image,PixelBox box,int threshold)
    {
        var count=0;var pixels=image.Pixels;
        for(var y=box.Top;y<box.Bottom;y++)for(var x=box.Left;x<box.Right;x++)
        {
            var i=(y*image.Width+x)*3;if(Math.Max(pixels[i],Math.Max(pixels[i+1],pixels[i+2]))>=threshold)count++;
        }
        return count;
    }
    private PixelPoint ToClient(PixelPoint point)
    {
        var geometry=session.Guard.ReadGeometry();var size=configuration["client_size"]!.AsArray();
        return new((int)Math.Round(point.X*(double)geometry.Width/size[0]!.GetValue<int>()),(int)Math.Round(point.Y*(double)geometry.Height/size[1]!.GetValue<int>()));
    }
    sealed class PreparationStore:IWorkflowStore
    {public JsonObject? Read()=>null;public void Flush(JsonObject state)=>throw new InvalidOperationException("快速重进准备只允许可逆导航");}
}
