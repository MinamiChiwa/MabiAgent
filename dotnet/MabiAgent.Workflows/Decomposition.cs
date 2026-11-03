using System.Text.Json.Nodes;
namespace MabiAgent.Workflows;

public interface IDecompositionPort
{
    JsonObject Snapshot();
    bool Request(long inputQuantity);
    string Tick();
    void ObserveOnly();
    bool ReleaseNavigation();
}
