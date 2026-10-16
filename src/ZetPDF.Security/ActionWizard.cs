using ZetPDF.Core.Contracts;

namespace ZetPDF.Security;

/// <summary>ZAŚLEPKA szkieletu – do zastąpienia implementacją przez właściciela modułu.</summary>
public sealed class ActionWizard : IActionWizard
{
    // ---- IActionWizard
    public Task<IReadOnlyList<ActionDefinition>> GetActionsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task SaveActionAsync(ActionDefinition action, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteActionAsync(string actionId, CancellationToken ct = default) => throw new NotImplementedException();
    public IReadOnlyDictionary<string, string> StepTypes => throw new NotImplementedException();
    public Task<ActionRunReport> RunAsync(ActionDefinition action, IReadOnlyList<string> inputFiles, ActionRunOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) => throw new NotImplementedException();
}
