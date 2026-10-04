using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CadHarness.Cli
{
    internal abstract class PlannerBase : ICadPlanner
    {
        protected abstract string Provider { get; }
        protected virtual string Model { get { return null; } }
        protected abstract Task<string> ProduceAsync(string command, CadState state, PlannerMetrics metrics, CancellationToken cancellationToken);

        public async Task<PlannerResult> PlanAsync(string userCommand, CadState currentState, CancellationToken cancellationToken)
        {
            PlannerResult result = new PlannerResult { Metrics = new PlannerMetrics { Provider=Provider, Model=Model, PlannerCalls=1 } };
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (String.IsNullOrWhiteSpace(userCommand) || userCommand.Length > 4096)
                    throw new PlannerFailure("PLANNER_INPUT_INVALID", "Command must contain 1 to 4096 characters.", false, false);
                string output = await ProduceAsync(userCommand, currentState, result.Metrics, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (output != null) result.Metrics.OutputSha256 = ReferenceAudit.Hash(output);
                result.Intent = IntentSchema.Validate(output);
            }
            catch (PlannerFailure failure)
            {
                result.ErrorCode = failure.Code; result.Error = failure.Message;
                result.Metrics.ParseFailures = failure.ParseFailure ? 1 : 0;
                result.Metrics.UnsupportedIntents = failure.Unsupported ? 1 : 0;
            }
            catch (OperationCanceledException)
            {
                result.ErrorCode = cancellationToken.IsCancellationRequested ? "PLANNER_CANCELLED" : "PLANNER_TIMEOUT";
                result.Error = cancellationToken.IsCancellationRequested ? "Planning was cancelled." : "The planner request timed out.";
            }
            catch (Exception)
            {
                // Do not leak credentials, provider bodies, or endpoint query strings in errors.
                result.ErrorCode = Provider == "llm" ? "PLANNER_TRANSPORT_FAILED" : "PLANNER_FAILED";
                result.Error = "The planner failed before a validated intent was available.";
            }
            finally { timer.Stop(); result.Metrics.LatencyMs = timer.Elapsed.TotalMilliseconds; }
            return result;
        }
    }
}
