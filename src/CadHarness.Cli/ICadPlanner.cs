using System;
using System.Threading;
using System.Threading.Tasks;

namespace CadHarness.Cli
{
    // Planning consumes data only. No planner is given a COM object or runtime callback.
    internal interface ICadPlanner
    {
        Task<PlannerResult> PlanAsync(string userCommand, CadState currentState, CancellationToken cancellationToken);
    }

    internal enum PlannedMode { Create, Edit }

    internal sealed class PlannedIntent
    {
        internal readonly PlannedMode Mode;
        internal readonly CadIntent Create;
        internal readonly ParameterEditIntent Edit;
        private PlannedIntent(PlannedMode mode, CadIntent create, ParameterEditIntent edit)
        { Mode = mode; Create = create; Edit = edit; }
        internal static PlannedIntent Canonical() { return new PlannedIntent(PlannedMode.Create, CadIntent.Canonical(), null); }
        internal static PlannedIntent Parameter(ParameterEditIntent edit) { return new PlannedIntent(PlannedMode.Edit, null, edit); }
    }

    public sealed class PlannerMetrics
    {
        public string Provider { get; set; }
        public string Model { get; set; }
        public int PlannerCalls { get; set; }
        public int Calls { get; set; } // Actual LLM HTTP attempts; RulePlanner always reports zero.
        public double LatencyMs { get; set; }
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public int ParseFailures { get; set; }
        public int UnsupportedIntents { get; set; }
        public string OutputSha256 { get; set; }
    }

    internal sealed class PlannerResult
    {
        internal PlannedIntent Intent;
        internal PlannerMetrics Metrics;
        internal string ErrorCode;
        internal string Error;
        internal bool Success { get { return Intent != null && ErrorCode == null; } }
    }

    internal sealed class PlannerFailure : Exception
    {
        internal readonly string Code;
        internal readonly bool ParseFailure, Unsupported;
        internal PlannerFailure(string code, string message, bool parseFailure, bool unsupported) : base(message)
        { Code = code; ParseFailure = parseFailure; Unsupported = unsupported; }
    }
}
