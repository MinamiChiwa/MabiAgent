namespace MabiAgent.Workflows;

public static class RuntimeIssuePresentation
{
    public static bool RequiresPlayerAction(string reason,AutomationJobStatus? job=null)
    {
        if(job?.PendingConsumption==true)return true;
        if(job?.Status is "complete" or "running" or "waiting")return false;
        return reason is not ("restart" or "manual" or "closed" or "inventory_sync" or "inventory_sync_complete"
            or "device_batch_not_ready" or "device_full" or "materials_unknown" or "materials_unavailable"
            or "waiting" or "production_first" or "backpack_production_first")
            && !reason.Contains("等待完整抓包",StringComparison.Ordinal);
    }
}
