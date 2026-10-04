using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace CadHarness.Cli
{
    internal sealed class BenchmarkRuntimeReply
    {internal int ExitCode;internal object Result;}

    internal static class BenchmarkStep
    {
        internal static BenchmarkStepResult Execute(ICadPlanner planner,string command,string statePath,PlannedIntent expected,bool rejectOnly,
            Func<PlannedIntent,BenchmarkRuntimeReply> runtime)
        {
            Stopwatch wall=Stopwatch.StartNew();
            BenchmarkStepResult step=new BenchmarkStepResult {RunId=Guid.NewGuid().ToString("D"),StartedUtc=DateTime.UtcNow.ToString("o"),Command=command,
                Mode=rejectOnly?"plan":(expected.Mode==PlannedMode.Create?"create":"edit"),ExpectedOutcome=rejectOnly?"unsupported":"verified runtime result",Runtime=new BenchmarkRuntimeMetrics()};
            try
            {
                CadState before=File.Exists(statePath)?StateSerializer.Load(statePath):null;
                step.StateSha256Before=File.Exists(statePath)?BenchmarkFiles.Hash(statePath):null;step.RevisionBefore=before==null?(int?)null:before.EditRevision;
                PlannerResult plan=planner.PlanAsync(command,before,CancellationToken.None).GetAwaiter().GetResult();step.Planner=plan.Metrics;
                if(step.Planner!=null){step.LlmWallMs=step.Planner.Calls>0?step.Planner.LatencyMs:0;step.TotalTokens=step.Planner.InputTokens.HasValue && step.Planner.OutputTokens.HasValue?step.Planner.InputTokens+step.Planner.OutputTokens:null;}
                if(!plan.Success)
                {
                    step.ErrorCode=plan.ErrorCode;step.Error=plan.Error;
                    step.NormalizedFailureClass=FailureClassifier.Classify(plan.ErrorCode,null);
                    step.ExpectedOutcomePassed=rejectOnly && plan.ErrorCode=="PLANNER_UNSUPPORTED_INTENT";
                }
                else
                {
                    step.ValidatedIntent=IntentSchema.Document(plan.Intent);step.TypedIntent=plan.Intent.Mode==PlannedMode.Create?"CadIntent":"ParameterEditIntent";
                    // Local schema validation already ran in PlannerBase; expectation matching is
                    // a benchmark safety gate, never a new planner or a runtime fallback.
                    if(rejectOnly || !Matches(plan.Intent,expected))
                    {step.SemanticSafetyFailure=true;step.ErrorCode="BENCHMARK_PLANNER_SEMANTIC_MISMATCH";step.NormalizedFailureClass="SEMANTIC_SAFETY_FAILURE";step.Error="Planner returned a legal intent inconsistent with the fixed benchmark instruction; Runtime was withheld.";}
                    else
                    {
                        Stopwatch runtimeClock=Stopwatch.StartNew();
                        using(BenchmarkTelemetry telemetry=new BenchmarkTelemetry())
                        {
                            step.Runtime=telemetry.Metrics;step.RuntimeEntered=true;
                            BenchmarkRuntimeReply reply;
                            try{reply=runtime(plan.Intent);}finally{runtimeClock.Stop();step.Runtime.RuntimeWallMs=runtimeClock.Elapsed.TotalMilliseconds;}
                            step.RuntimeExitCode=reply.ExitCode;step.RuntimeResult=reply.Result;
                            step.RuntimeSuccess=reply.ExitCode==0;
                        }
                        if(plan.Intent.Mode==PlannedMode.Edit)
                        {
                            ParameterEditEvidence edit=BenchmarkFiles.Json.Deserialize<ParameterEditEvidence>(BenchmarkFiles.Json.Serialize(step.RuntimeResult));
                            step.Runtime.RecoveryAttempts=edit.RecoveryAttempts==null?0:edit.RecoveryAttempts.Count;
                            step.RollbackSuccess=edit.RollbackAttempted?(bool?)edit.RollbackSucceeded:null;
                            step.ErrorCode=edit.ErrorCode;step.Error=edit.Error;step.NormalizedFailureClass=edit.FailureClass;
                            step.RuntimeSuccess=step.RuntimeSuccess && edit.Success && edit.StateCommitted;
                        }
                        step.ExpectedOutcomePassed=step.RuntimeSuccess;
                    }
                }
            }
            catch(Exception error)
            {
                Program.PhaseFailure known=error as Program.PhaseFailure;PlannerFailure planning=error as PlannerFailure;
                step.ErrorCode=known!=null?known.Code:(planning!=null?planning.Code:"BENCHMARK_STEP_FAILED");
                step.Error=known!=null || planning!=null?error.Message:"Benchmark step failed; inspect structured RuntimeResult and state/evidence paths.";
                step.ExpectedOutcomePassed=false;
            }
            finally
            {
                try
                {step.StateSha256After=File.Exists(statePath)?BenchmarkFiles.Hash(statePath):null;if(File.Exists(statePath))step.RevisionAfter=StateSerializer.Load(statePath).EditRevision;}
                catch{step.ExpectedOutcomePassed=false;step.ErrorCode="BENCHMARK_STATE_AUDIT_FAILED";step.Error="Post-command state could not be read or hashed.";}
                wall.Stop();step.WallMs=wall.Elapsed.TotalMilliseconds;
            }
            return step;
        }
        internal static bool Matches(PlannedIntent actual,PlannedIntent expected)
        {
            return actual!=null && expected!=null && actual.Mode==expected.Mode &&
                (actual.Mode==PlannedMode.Create?actual.Create!=null && actual.Edit==null:
                    actual.Edit!=null && actual.Create==null && actual.Edit.ParameterSemanticId==expected.Edit.ParameterSemanticId && CadValidator.Near(actual.Edit.ValueMm,expected.Edit.ValueMm));
        }
        internal static BenchmarkRuntimeReply InvokeRuntime(PlannedIntent plan,string path)
        {
            TextWriter previous=Console.Out;int exit;string text;
            using(StringWriter capture=new StringWriter())
            {try{Console.SetOut(capture);exit=PlanRuntimeBridge.Execute(plan,path);}finally{Console.SetOut(previous);}text=capture.ToString();}
            return new BenchmarkRuntimeReply {ExitCode=exit,Result=BenchmarkFiles.Json.DeserializeObject(text)};
        }
    }
}
