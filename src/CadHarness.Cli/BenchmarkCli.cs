using System;
using System.Diagnostics;
using System.IO;

namespace CadHarness.Cli
{
    internal static class BenchmarkCli
    {
        internal static int Run(string[] args)
        {
            if(args.Length<2 || args.Length>3){Console.Error.WriteLine("Usage: CadHarness.Cli.exe benchmark <manifest.json> [output-directory]");return 2;}
            BenchmarkSuiteResult result=new BenchmarkSuiteResult {RunId=Guid.NewGuid().ToString("D"),StartedUtc=DateTime.UtcNow.ToString("o"),Status="RUNNING"};
            Stopwatch timer=Stopwatch.StartNew();int declaredCases=0;
            try
            {
                // Complete schema validation before attachment, HTTP or native mutation.
                BenchmarkDefinition definition=BenchmarkDefinition.Load(args[1]);
                declaredCases=definition.Iterations*definition.Cases.Count;
                result.SuiteId=definition.SuiteId;result.ManifestPath=definition.ManifestPath;result.ManifestSha256=definition.ManifestSha256;result.Planner=definition.Planner;
                result.RuntimeExeSha256=BenchmarkFiles.Hash(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string defaultRoot=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","artifacts","benchmarks"));
                string outputRoot=Path.GetFullPath(args.Length==3?args[2]:defaultRoot);Directory.CreateDirectory(defaultRoot);
                // Serialize benchmark controllers sharing this installation/session.
                using(FileStream sessionLock=new FileStream(Path.Combine(defaultRoot,"session.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                {
                    string directory=Path.Combine(outputRoot,result.RunId);Directory.CreateDirectory(directory);result.ResultPath=Path.Combine(directory,"results.json");
                    BenchmarkFiles.Save(result.ResultPath,result); // Confirm output is writable before model creation or LLM use.
                    ICadPlanner planner=definition.Planner=="rule"?(ICadPlanner)new RulePlanner():new LlmPlanner();
                    BenchmarkRunner runner=new BenchmarkRunner(definition,directory,planner);runner.Execute(result);
                }
            }
            catch(Exception error)
            {
                Program.PhaseFailure known=error as Program.PhaseFailure;PlannerFailure planning=error as PlannerFailure;
                result.ErrorCode=known!=null?known.Code:(planning!=null?planning.Code:"BENCHMARK_LOCAL_FAILED");
                result.Error=known!=null || planning!=null?error.Message:"Benchmark attachment, output handling or execution failed; inspect completed case records.";
                result.Status="FAILED";
            }
            timer.Stop();result.WallMs=timer.Elapsed.TotalMilliseconds;result.CompletedUtc=DateTime.UtcNow.ToString("o");
            result.Summary=Summarize(result);if(declaredCases>result.Summary.RequiredCases)result.Summary.RequiredCases=declaredCases;
            result.Summary.TotalSuccess=result.Summary.RequiredCases==0?0:(double)result.Summary.PassedCases/result.Summary.RequiredCases;
            result.AllRequiredCasesPassed=result.RunnerCompleted && result.OriginalDocumentRestored && result.Summary.PassedCases==result.Summary.RequiredCases && !result.Cases.Exists(delegate(BenchmarkCaseResult c){return c.Status=="FAILED";});
            if(result.Status!="FAILED")result.Status=result.AllRequiredCasesPassed?"COMPLETE":(result.Cases.Exists(delegate(BenchmarkCaseResult c){return c.Status=="FAILED";})?"FAILED":(result.Summary.BlockedCases>0?"BLOCKED_CAPABILITY":"INCOMPLETE"));
            if(result.ResultPath!=null)
            {
                try{BenchmarkFiles.Save(result.ResultPath,result);}catch{result.Status="FAILED";result.AllRequiredCasesPassed=false;result.ErrorCode="BENCHMARK_RESULT_WRITE_FAILED";result.Error="Final results could not be persisted.";}
            }
            Console.WriteLine(BenchmarkFiles.Json.Serialize(result));
            return result.AllRequiredCasesPassed?0:(result.Status=="BLOCKED_CAPABILITY"?3:(result.ErrorCode=="BENCHMARK_MANIFEST_INVALID"?2:1));
        }
        internal static BenchmarkSummary Summarize(BenchmarkSuiteResult suite)
        {
            BenchmarkSummary summary=new BenchmarkSummary();
            foreach(BenchmarkCaseResult c in suite.Cases)
            {
                if(c.Warmup)continue;summary.RequiredCases++;
                if(c.Status=="PASSED")summary.PassedCases++;else if(c.Status=="BLOCKED_CAPABILITY")summary.BlockedCases++;else summary.FailedCases++;
                foreach(BenchmarkStepResult step in c.Steps)
                {
                    if(step.Planner!=null){summary.LlmCalls+=step.Planner.Calls;summary.LlmWallMs+=step.LlmWallMs;summary.ParseFailures+=step.Planner.ParseFailures;summary.UnsupportedIntents+=step.Planner.UnsupportedIntents;}
                    if(step.SemanticSafetyFailure)summary.SemanticSafetyFailures++;
                    if(step.Mode=="edit" && step.RuntimeEntered){summary.EditCommands++;if(step.RuntimeSuccess)summary.SuccessfulEdits++;}
                    if(step.RollbackSuccess.HasValue){summary.RollbackAttempts++;if(step.RollbackSuccess==true)summary.SuccessfulRollbacks++;}
                    if(step.Runtime!=null){summary.Rebuilds+=step.Runtime.Rebuilds;summary.DirectSolidWorksApiOperations+=step.Runtime.DirectSolidWorksApiOperations;summary.RecoveryAttempts+=step.Runtime.RecoveryAttempts;}
                }
                if(c.RecoveryProbe!=null)
                {
                    // Include probe recovery/validation work separately from ordinary user commands.
                    System.Collections.Generic.Dictionary<string,object> probe=BenchmarkFiles.Json.DeserializeObject(BenchmarkFiles.Json.Serialize(c.RecoveryProbe)) as System.Collections.Generic.Dictionary<string,object>;
                    BenchmarkRuntimeMetrics metrics=BenchmarkFiles.Json.Deserialize<BenchmarkRuntimeMetrics>(BenchmarkFiles.Json.Serialize(probe["runtime"]));
                    ValidationRunEvidence recovery=BenchmarkFiles.Json.Deserialize<ValidationRunEvidence>(BenchmarkFiles.Json.Serialize(probe["result"]));
                    summary.Rebuilds+=metrics.Rebuilds;summary.DirectSolidWorksApiOperations+=metrics.DirectSolidWorksApiOperations;summary.RecoveryAttempts+=recovery.RecoveryAttempts==null?0:recovery.RecoveryAttempts.Count;
                }
            }
            summary.TotalSuccess=summary.RequiredCases==0?0:(double)summary.PassedCases/summary.RequiredCases;
            summary.EditSuccess=summary.EditCommands==0?(double?)null:(double)summary.SuccessfulEdits/summary.EditCommands;
            summary.RollbackSuccess=summary.RollbackAttempts==0?(double?)null:(double)summary.SuccessfulRollbacks/summary.RollbackAttempts;
            return summary;
        }
    }
}
