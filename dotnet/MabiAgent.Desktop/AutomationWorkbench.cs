using MabiAgent.Desktop.Editors;

namespace MabiAgent.Desktop;

public sealed partial class AutomationService
{
    public Task RunWorkbenchTestAsync(WorkbenchDocument document,WorkbenchChain chain,IReadOnlyCollection<string>? selected,bool recognizeOnly,IProgress<WorkbenchRunRow> progress,CancellationToken cancellation)
    {
        // Recognition remains read-only during reset recovery; input tests are fail-closed.
        if(!recognizeOnly)ThrowIfStopResetPending();
        if(IsRunning)throw new InvalidOperationException("请先暂停任务，再启动工作台测试。");
        CancellationTokenSource linked;lock(lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token,executionLifetime.Token);workbenchCancellation=linked;
        }
        return Task.Run(async()=>
        {
            using(linked)try
            {
                await activity.AcquireAsync(gate,"工作台测试",linked.Token).ConfigureAwait(false);
                using var stage=activity.Enter("工作台测试",true);
                try
                {
                    ObjectDisposedException.ThrowIf(disposed,this);
                    if(IsRunning)throw new InvalidOperationException("请先暂停任务，再启动工作台测试。");
                    await WorkbenchTestExecution.RunAsync(Repository,selection,()=>IsRunning,GetOcr,document,chain,selected,recognizeOnly,progress,linked.Token,()=>CaptureStatus,Accounts).ConfigureAwait(false);
                }
                finally{gate.Release();}
            }
            catch(Exception error)when(error is not OperationCanceledException)
            {activity.Fault(error,"工作台测试",scope:"workbench");throw;}
            finally{lock(lifecycle)if(ReferenceEquals(workbenchCancellation,linked))workbenchCancellation=null;}
        });
    }
}
