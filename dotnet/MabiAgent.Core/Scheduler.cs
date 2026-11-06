namespace MabiAgent.Core;
public sealed record WorkCandidate(string Id,string Kind,string? Device,bool Ready,int FreeSlots,int UserOrder,
    double Coverage=0,bool Collectible=false,bool Automatic=false,bool BufferSatisfied=false,bool Blocked=false);
public sealed record SchedulingContext(string? ActiveId,bool NativeBatchOrTransaction,string? CurrentDevice,bool BackpackRelief=false);

// Chooses work only. No input authority, screen observation or persistence.
public sealed class Scheduler
{
    public WorkCandidate? Select(IEnumerable<WorkCandidate> candidates,SchedulingContext state)
    {
        var rows=candidates.ToArray();
        if(state.NativeBatchOrTransaction) return rows.FirstOrDefault(r=>r.Id==state.ActiveId);
        var eligible=rows.Where(r=>r.Ready && !r.Blocked && !(r.Automatic && r.BufferSatisfied && !r.Collectible)).ToArray();
        bool Production(WorkCandidate r)=>r.Kind=="production" && r.FreeSlots>0;
        return eligible.Where(r=>Production(r) && r.Device==state.CurrentDevice).OrderBy(r=>r.UserOrder).FirstOrDefault()
            ?? eligible.Where(r=>r.Collectible).OrderBy(r=>r.UserOrder).FirstOrDefault()
            ?? eligible.Where(Production).OrderBy(r=>r.UserOrder).FirstOrDefault()
            ?? (state.BackpackRelief ? null : eligible.Where(r=>r.Kind=="withdrawal").OrderBy(r=>r.UserOrder).FirstOrDefault()
                ?? eligible.Where(r=>r.Kind=="purchase").OrderBy(r=>r.UserOrder).FirstOrDefault()
                ?? eligible.Where(r=>r.Kind=="gathering").OrderBy(r=>r.Coverage).ThenBy(r=>r.UserOrder).FirstOrDefault()
                ?? eligible.Where(r=>r.Kind=="idle_fishing").OrderBy(r=>r.UserOrder).FirstOrDefault());
    }
}

