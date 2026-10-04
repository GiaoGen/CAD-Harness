using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CadHarness.Cli;

namespace CadHarness.Tests
{
    internal static class BenchmarkTests
    {
        private static readonly List<object> results=new List<object>();
        private static string root,directory,validJson;
        private static int failed;
        private static int Main(string[] args)
        {
            root=Path.GetFullPath(args[0]);directory=Path.Combine(root,"artifacts/phase8/unit");Directory.CreateDirectory(directory);
            validJson=File.ReadAllText(Path.Combine(root,"tests/benchmark-cases/mvp.json"));
            Case("fixed A..E manifest",delegate{BenchmarkDefinition d=Load(validJson);Require(d.Cases.Count==5 && d.Iterations==1 && d.Planner=="rule");});
            Invalid("unknown fields",validJson.Replace("\"benchmarkVersion\":", "\"apiKey\": \"must-not-be-accepted\",\"benchmarkVersion\":"));
            Invalid("duplicate keys",validJson.Replace("\"suiteId\":", "\"suiteId\":\"duplicate\",\"suiteId\":"));
            Invalid("unknown version",validJson.Replace("\"0.1\"","\"0.2\""));
            Invalid("unknown planner",validJson.Replace("\"rule\"","\"fallback\""));
            Invalid("fractional iterations",validJson.Replace("\"iterations\": 1","\"iterations\": 1.5"));
            Invalid("zero iterations",validJson.Replace("\"iterations\": 1","\"iterations\": 0"));
            Invalid("unbounded iterations",validJson.Replace("\"iterations\": 1","\"iterations\": 21"));
            Invalid("excess warmups",validJson.Replace("\"warmupIterations\": 0","\"warmupIterations\": 4"));
            Invalid("duplicate cases",validJson.Replace("\"id\": \"B\", \"scenario\": \"rectangular_mounting_plate\"","\"id\": \"A\", \"scenario\": \"canonical_plate\""));
            Invalid("unknown scenario",validJson.Replace("\"canonical_plate\"","\"arbitrary_geometry\""));
            Invalid("missing case",validJson.Replace("    { \"id\": \"E\", \"scenario\": \"unsupported_intent\" }", ""));
            Case("iteration/warmup bounds and explicit llm",delegate{BenchmarkDefinition d=Load(validJson.Replace("\"iterations\": 1","\"iterations\": 20").Replace("\"warmupIterations\": 0","\"warmupIterations\": 3").Replace("\"rule\"","\"llm\""));Require(d.Iterations==20 && d.WarmupIterations==3 && d.Planner=="llm");});
            Case("valid single typed intent reaches callback once",delegate {
                int calls=0;BenchmarkStepResult s=BenchmarkStep.Execute(new RulePlanner(),"Create canonical part",NoState(),PlannedIntent.Canonical(),false,
                    delegate(PlannedIntent intent){calls++;return new BenchmarkRuntimeReply{ExitCode=0,Result=new{success=true}};});
                Require(calls==1 && s.RuntimeEntered && s.RuntimeSuccess && s.ExpectedOutcomePassed && s.Planner.Calls==0 && s.TotalTokens==null);
            });
            Case("unsupported RulePlanner input never reaches callback",delegate {
                int calls=0;BenchmarkStepResult s=BenchmarkStep.Execute(new RulePlanner(),"Create an aerodynamic turbine blade with optimized cooling passages.",NoState(),null,true,
                    delegate(PlannedIntent intent){calls++;throw new Exception("Must not execute");});
                Require(calls==0 && !s.RuntimeEntered && s.ExpectedOutcomePassed && s.ErrorCode=="PLANNER_UNSUPPORTED_INTENT");
            });
            Case("legal canonical compressed from unsupported input is semantic failure",delegate {
                int calls=0;BenchmarkStepResult s=BenchmarkStep.Execute(new FixedPlanner(PlannedIntent.Canonical()),"unsupported instruction",NoState(),null,true,
                    delegate(PlannedIntent intent){calls++;throw new Exception("Must not execute");});
                Require(calls==0 && !s.RuntimeEntered && !s.ExpectedOutcomePassed && s.SemanticSafetyFailure && s.Planner.Calls==1 && s.TotalTokens==null);
            });
            Case("wrong parameter or value never reaches Runtime",delegate {
                foreach(PlannedIntent wrong in new[]{Edit("base_plate.thickness",55),Edit("mounting_holes.spacing",56)})
                {
                    int calls=0;BenchmarkStepResult s=BenchmarkStep.Execute(new FixedPlanner(wrong),"Set spacing to 55 mm",NoState(),Edit("mounting_holes.spacing",55),false,
                        delegate(PlannedIntent intent){calls++;throw new Exception("Must not execute");});
                    Require(calls==0 && !s.RuntimeEntered && s.SemanticSafetyFailure);
                }
            });
            Case("runtime exception leaves telemetry and Console usable",delegate {
                BenchmarkStepResult s=BenchmarkStep.Execute(new RulePlanner(),"Create canonical part",NoState(),PlannedIntent.Canonical(),false,
                    delegate(PlannedIntent intent){return BenchmarkTelemetry.Call<BenchmarkRuntimeReply>("native-test",delegate{throw new InvalidOperationException("test");});});
                Require(s.RuntimeEntered && !s.RuntimeSuccess && !s.ExpectedOutcomePassed && s.Runtime.ApiOperations.Count==1 && s.Runtime.ApiOperations[0].Exceptions==1 && s.Runtime.RuntimeWallMs>=0);
            });
            Case("telemetry is observational and counts thrown attempts",delegate {
                using(BenchmarkTelemetry t=new BenchmarkTelemetry())
                {
                    Require(BenchmarkTelemetry.Call("native-test",delegate{return 7;})==7);
                    try{BenchmarkTelemetry.Call<int>("native-test",delegate{throw new InvalidOperationException("same exception");});}catch(InvalidOperationException e){Require(e.Message=="same exception");}
                    BenchmarkTelemetry.Validation(3);Require(t.Metrics.DirectSolidWorksApiOperations==2 && t.Metrics.ApiOperations[0].Calls==2 && t.Metrics.ApiOperations[0].Exceptions==1 && t.Metrics.ValidationMs==3);
                }
            });
            Case("nested telemetry does not contaminate parent",delegate {
                using(BenchmarkTelemetry parent=new BenchmarkTelemetry())
                {BenchmarkTelemetry.Call("parent",delegate{return 1;});using(BenchmarkTelemetry inner=new BenchmarkTelemetry()){BenchmarkTelemetry.Call("inner",delegate{return 2;});Require(inner.Metrics.DirectSolidWorksApiOperations==1);}BenchmarkTelemetry.Call("parent",delegate{return 3;});Require(parent.Metrics.DirectSolidWorksApiOperations==2 && parent.Metrics.ApiOperations.Count==1);}
            });
            Case("blocked and failed cases cannot inflate suite success; warmups excluded",delegate {
                BenchmarkSuiteResult suite=new BenchmarkSuiteResult();
                suite.Cases.Add(new BenchmarkCaseResult{CaseId="A",Status="PASSED"});suite.Cases.Add(new BenchmarkCaseResult{CaseId="B",Status="BLOCKED_CAPABILITY"});
                suite.Cases.Add(new BenchmarkCaseResult{CaseId="C",Status="FAILED"});suite.Cases.Add(new BenchmarkCaseResult{CaseId="A",Warmup=true,Status="PASSED"});
                BenchmarkSummary s=BenchmarkCli.Summarize(suite);Require(s.RequiredCases==3 && s.PassedCases==1 && s.BlockedCases==1 && s.FailedCases==1 && Math.Abs(s.TotalSuccess-1.0/3)<1e-9);
            });
            Case("expected invalid edit remains unsuccessful edit but successful rollback",delegate {
                BenchmarkSuiteResult suite=new BenchmarkSuiteResult();BenchmarkCaseResult c=new BenchmarkCaseResult{Status="PASSED"};
                c.Steps.Add(new BenchmarkStepResult{Mode="edit",RuntimeEntered=true,RuntimeSuccess=false,ExpectedOutcomePassed=true,RollbackSuccess=true,Runtime=new BenchmarkRuntimeMetrics{Rebuilds=2,RecoveryAttempts=1}});suite.Cases.Add(c);
                BenchmarkSummary s=BenchmarkCli.Summarize(suite);Require(s.PassedCases==1 && s.SuccessfulEdits==0 && s.EditCommands==1 && s.EditSuccess==0 && s.RollbackSuccess==1 && s.Rebuilds==2);
            });
            Case("atomic result replacement is readable and cleans temporary file",delegate {
                string path=Path.Combine(directory,"atomic.json");BenchmarkFiles.Save(path,new{revision=1});BenchmarkFiles.Save(path,new{revision=2});
                Dictionary<string,object> data=BenchmarkFiles.Json.DeserializeObject(File.ReadAllText(path)) as Dictionary<string,object>;
                Require(Convert.ToInt32(data["revision"])==2 && !File.Exists(path+".tmp"));
            });
            BenchmarkFiles.Save(Path.Combine(directory,"results.json"),new{success=failed==0,total=results.Count,failed=failed,results=results});
            Console.WriteLine(BenchmarkFiles.Json.Serialize(new{total=results.Count,failed=failed}));return failed==0?0:1;
        }
        private static string NoState(){return Path.Combine(directory,"absent-"+Guid.NewGuid().ToString("N")+".json");}
        private static BenchmarkDefinition Load(string text){string path=Path.Combine(directory,"manifest.json");File.WriteAllText(path,text);return BenchmarkDefinition.Load(path);}
        private static void Invalid(string name,string text){Case(name,delegate{bool rejected=false;try{Load(text);}catch(Program.PhaseFailure e){rejected=e.Code=="BENCHMARK_MANIFEST_INVALID";}Require(rejected);});}
        private static PlannedIntent Edit(string parameter,double mm){return PlannedIntent.Parameter(ParameterEditIntent.ParseCli(parameter,mm.ToString("R",System.Globalization.CultureInfo.InvariantCulture)));}
        private static void Require(bool ok){if(!ok)throw new Exception("Assertion failed");}
        private static void Case(string name,Action action){try{action();results.Add(new{name=name,success=true});}catch(Exception e){failed++;results.Add(new{name=name,success=false,error=e.Message});}}
        private sealed class FixedPlanner : ICadPlanner
        {
            private readonly PlannedIntent value;internal FixedPlanner(PlannedIntent intent){value=intent;}
            public Task<PlannerResult> PlanAsync(string command,CadState state,CancellationToken token)
            {return Task.FromResult(new PlannerResult{Intent=value,Metrics=new PlannerMetrics{Provider="unit fixture only",Calls=1,PlannerCalls=1,LatencyMs=1}});}
        }
    }
}
