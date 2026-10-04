using System.Collections.Generic;

namespace CadHarness.Cli
{
    public sealed class BenchmarkStepResult
    {
        public string RunId { get; set; }
        public string Command { get; set; }
        public string Mode { get; set; }
        public string ExpectedOutcome { get; set; }
        public string StartedUtc { get; set; }
        public double WallMs { get; set; }
        public double LlmWallMs { get; set; }
        public PlannerMetrics Planner { get; set; }
        public long? TotalTokens { get; set; }
        public object ValidatedIntent { get; set; }
        public string TypedIntent { get; set; }
        public bool RuntimeEntered { get; set; }
        public int? RuntimeExitCode { get; set; }
        public bool RuntimeSuccess { get; set; }
        public bool ExpectedOutcomePassed { get; set; }
        public bool SemanticSafetyFailure { get; set; }
        public string ErrorCode { get; set; }
        public string NormalizedFailureClass { get; set; }
        public string Error { get; set; }
        public string StateSha256Before { get; set; }
        public string StateSha256After { get; set; }
        public int? RevisionBefore { get; set; }
        public int? RevisionAfter { get; set; }
        public bool? RollbackSuccess { get; set; }
        public BenchmarkRuntimeMetrics Runtime { get; set; }
        public object RuntimeResult { get; set; }
    }
    public sealed class BenchmarkCaseResult
    {
        public string CaseId { get; set; }
        public string Scenario { get; set; }
        public int Iteration { get; set; }
        public bool Warmup { get; set; }
        public string Status { get; set; }
        public string ErrorCode { get; set; }
        public string Error { get; set; }
        public string StatePath { get; set; }
        public string PartTitle { get; set; }
        public string DocumentId { get; set; }
        public double WallMs { get; set; }
        public double SetupMs { get; set; }
        public double AuditValidationMs { get; set; }
        public bool SettingsRestored { get; set; }
        public bool ControlledReferenceInvalidation { get; set; }
        public bool? SemanticFaceRecovered { get; set; }
        public int? NaturallyInvalidReferencesAfterEdit { get; set; }
        public double[] SemanticFaceBoxBeforeMm { get; set; }
        public double[] SemanticFaceBoxAfterMm { get; set; }
        public object RecoveryProbe { get; set; }
        public ValidationResult FinalValidation { get; set; }
        public List<BenchmarkStepResult> Steps { get; set; }
        public BenchmarkCaseResult(){Steps=new List<BenchmarkStepResult>();}
    }
    public sealed class BenchmarkSummary
    {
        public int RequiredCases { get; set; }
        public int PassedCases { get; set; }
        public int FailedCases { get; set; }
        public int BlockedCases { get; set; }
        public double TotalSuccess { get; set; }
        public int EditCommands { get; set; }
        public int SuccessfulEdits { get; set; }
        public double? EditSuccess { get; set; }
        public int RollbackAttempts { get; set; }
        public int SuccessfulRollbacks { get; set; }
        public double? RollbackSuccess { get; set; }
        public int LlmCalls { get; set; }
        public double LlmWallMs { get; set; }
        public int ParseFailures { get; set; }
        public int UnsupportedIntents { get; set; }
        public int SemanticSafetyFailures { get; set; }
        public int Rebuilds { get; set; }
        public int DirectSolidWorksApiOperations { get; set; }
        public int RecoveryAttempts { get; set; }
    }
    public sealed class BenchmarkSuiteResult
    {
        public string BenchmarkVersion { get; set; }
        public string RunId { get; set; }
        public string SuiteId { get; set; }
        public string ManifestPath { get; set; }
        public string ManifestSha256 { get; set; }
        public string RuntimeExeSha256 { get; set; }
        public string StartedUtc { get; set; }
        public string CompletedUtc { get; set; }
        public string Planner { get; set; }
        public string SolidWorksRevision { get; set; }
        public string ResultPath { get; set; }
        public bool RunnerCompleted { get; set; }
        public bool AllRequiredCasesPassed { get; set; }
        public bool OriginalDocumentRestored { get; set; }
        public string Status { get; set; }
        public string ErrorCode { get; set; }
        public string Error { get; set; }
        public double WallMs { get; set; }
        public List<BenchmarkCaseResult> Cases { get; set; }
        public BenchmarkSummary Summary { get; set; }
        public BenchmarkSuiteResult(){BenchmarkVersion="0.1";Cases=new List<BenchmarkCaseResult>();}
    }
}
