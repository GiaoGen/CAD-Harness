using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CadHarness.Cli
{
    public sealed class PlannerRunEvidence
    {
        public string RunId { get; set; }
        public string StartedUtc { get; set; }
        public string CompletedUtc { get; set; }
        public string Command { get; set; }
        public string Operation { get; set; }
        public string StatePath { get; set; }
        public string EvidencePath { get; set; }
        public PlannerMetrics Planner { get; set; }
        public object ValidatedIntent { get; set; }
        public string TypedIntent { get; set; }
        public bool Success { get; set; }
        public bool RuntimeEntered { get; set; }
        public int? RuntimeExitCode { get; set; }
        public object RuntimeResult { get; set; }
        public string ErrorCode { get; set; }
        public string Error { get; set; }
        public string EvidenceWriteError { get; set; }
        public string FailureClass { get; set; }
    }

    internal static class PlannerCli
    {
        internal static int Run(string[] args)
        {
            if (args.Length < 3 || args.Length > 4 || (args[1] != "rule" && args[1] != "llm"))
            {
                Console.Error.WriteLine("Usage: CadHarness.Cli.exe plan|run rule|llm \"<command>\" [state-file]"); return 2;
            }
            PlannerRunEvidence evidence = new PlannerRunEvidence {
                RunId=Guid.NewGuid().ToString("D"), StartedUtc=DateTime.UtcNow.ToString("o"), Command=args[2], Operation=args[0],
                StatePath=args.Length == 4 ? args[3] : null,
                Planner=new PlannerMetrics { Provider=args[1] }
            };
            evidence.EvidencePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","artifacts","planner",evidence.RunId+".json"));
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler cancel = delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel=true; cancellation.Cancel(); };
                Console.CancelKeyPress += cancel;
                try
                {
                    if (evidence.StatePath != null) evidence.StatePath=Path.GetFullPath(evidence.StatePath);
                    CadState state = evidence.StatePath != null && File.Exists(evidence.StatePath) ? StateSerializer.Load(evidence.StatePath) : null;
                    ICadPlanner planner = args[1] == "rule" ? (ICadPlanner)new RulePlanner() : new LlmPlanner();
                    PlannerResult result = planner.PlanAsync(args[2],state,cancellation.Token).GetAwaiter().GetResult();
                    evidence.Planner = result.Metrics;
                    evidence.ErrorCode = result.ErrorCode; evidence.Error = result.Error;
                    if (result.Success)
                    {
                        evidence.ValidatedIntent = IntentSchema.Document(result.Intent);
                        evidence.TypedIntent = result.Intent.Mode == PlannedMode.Create ? "CadIntent" : "ParameterEditIntent";
                        if (args[0] == "plan") evidence.Success = true;
                        else
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            Save(evidence); // Planning log must be durable before entering the runtime.
                            TextWriter previous = Console.Out;
                            using (StringWriter runtimeOutput = new StringWriter())
                            {
                                try
                                {
                                    Console.SetOut(runtimeOutput);
                                    evidence.RuntimeEntered = true;
                                    evidence.RuntimeExitCode = PlanRuntimeBridge.Execute(result.Intent,evidence.StatePath);
                                }
                                finally { Console.SetOut(previous); }
                                evidence.RuntimeResult = new JavaScriptSerializer().DeserializeObject(runtimeOutput.ToString());
                            }
                            evidence.Success = evidence.RuntimeExitCode == 0;
                            if (!evidence.Success) { evidence.ErrorCode="PLANNER_RUNTIME_FAILED"; evidence.Error="Validated intent reached the existing runtime, which reported failure. See RuntimeResult."; }
                        }
                    }
                }
                catch (Exception error)
                {
                    Program.PhaseFailure known = error as Program.PhaseFailure;
                    PlannerFailure planning = error as PlannerFailure;
                    evidence.ErrorCode = known != null ? known.Code : (planning != null ? planning.Code :
                        (error is OperationCanceledException ? "PLANNER_CANCELLED" : "PLANNER_LOCAL_FAILED"));
                    evidence.Error = known != null || planning != null ? error.Message : "Planning or local evidence handling failed. Inspect the state path and runtime result.";
                }
                finally { Console.CancelKeyPress -= cancel; }
            }
            evidence.CompletedUtc=DateTime.UtcNow.ToString("o");
            if(!evidence.Success)
            {
                evidence.FailureClass=FailureClassifier.Classify(evidence.ErrorCode,null);
                System.Collections.Generic.Dictionary<string,object> runtime=evidence.RuntimeResult as System.Collections.Generic.Dictionary<string,object>;
                object classification;
                if(runtime!=null && (runtime.TryGetValue("FailureClass",out classification) || runtime.TryGetValue("failureClass",out classification)) && classification is string)
                    evidence.FailureClass=(string)classification;
            }
            try { Save(evidence); }
            catch (Exception) { evidence.EvidenceWriteError="Could not persist the final planner log."; if (!evidence.RuntimeEntered) evidence.Success=false; }
            Console.WriteLine(new JavaScriptSerializer().Serialize(evidence));
            return evidence.Success ? 0 : 1;
        }
        private static void Save(PlannerRunEvidence evidence)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(evidence.EvidencePath));
            string temporary=evidence.EvidencePath+".tmp";
            try
            {
                File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(evidence),new UTF8Encoding(false));
                if (File.Exists(evidence.EvidencePath)) File.Replace(temporary,evidence.EvidencePath,null);
                else File.Move(temporary,evidence.EvidencePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
