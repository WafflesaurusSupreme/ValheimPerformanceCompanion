namespace Companion.Core;

public sealed record ModuleIdentity(string Assembly, string Version, string Mvid, string FileSha256, string Owners, string LocationKind);
public sealed record PatchIdentity(string Target, string Kind, string Owner, string Assembly, string PatchMethod, string TargetMvid, string TargetToken, string PatchMvid, string PatchToken);
public sealed record CodeMethod(string Assembly, string Mvid, string Token, string Type, string Method, string Signature, string IlSha256, string IlBase64, string Instructions, string Locals, string ExceptionClauses);
public sealed record GcPhase(double Seconds, int ThreadId, int Phase, int Generation);
public sealed record GcPause(double Start, double End, int ThreadId, string Measurement);
public sealed record ThreadWait(double Start, double End, int ThreadId, string Reason, string State);
public sealed record DiskObservation(double Seconds, int ThreadId, double DurationMs, long Bytes, string Operation);
public sealed record StackEvidence(int ThreadId, string ThreadRole, int Samples, string Stack, IReadOnlyList<string> CodeIds);
public sealed record MethodEvidence(string Name, double Start, double End, double MainMs, long Calls, int SampleEvery);
public sealed record DiagnosticIssue(string Id, string Category, string Status, string Summary, string Mechanism,
    IReadOnlyList<int> FrameIndices, double Start, double End, IReadOnlyList<StackEvidence> Stacks,
    IReadOnlyList<MethodEvidence> Methods, IReadOnlyList<string> Evidence, IReadOnlyList<string> Gaps, IReadOnlyList<string> NextSteps);
public sealed class DiagnosticReport
{
    public int Schema { get; init; } = 2;
    public List<DiagnosticIssue> Issues { get; } = [];
    public List<CodeMethod> Code { get; } = [];
    public List<ModuleIdentity> Modules { get; } = [];
    public List<PatchIdentity> Patches { get; } = [];
    public List<string> Coverage { get; } = [];
    public string AttributionRule { get; init; } = "Measured execution, allocation, or waiting identifies work; it does not by itself prove a defect. Patch presence is not causal attribution. Inspect the matching code and validate any proposed change with a controlled reproduction. Source IL is original managed assembly code; Harmony replacements and native code may be unresolved.";
}
