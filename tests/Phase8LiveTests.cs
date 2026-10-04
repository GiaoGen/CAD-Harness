using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using CadHarness.Cli;

namespace CadHarness.Tests
{
    internal static class Phase8LiveTests
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string root=Path.GetFullPath(args[0]),directory=Path.Combine(root,"artifacts/phase8/live");
            if(Directory.Exists(directory)){Console.WriteLine("Prior Phase 8 live verification exists; refusing to overwrite.");return 1;}
            Directory.CreateDirectory(directory);
            string[] paths={"artifacts/state/9af37689-f21c-4096-af17-63b878455f3d.json","artifacts/phase5/live-model-state.json","artifacts/phase6/live/model-state.json","artifacts/phase7/live/3-optimized-state.json"};
            Dictionary<string,string> beforeHashes=new Dictionary<string,string>(),afterHashes=new Dictionary<string,string>();
            Dictionary<string,EditConfirmationResult> beforeGeometry=new Dictionary<string,EditConfirmationResult>();
            ISldWorks app=null;IModelDoc2 original=null;BenchmarkSuiteResult suite=null;
            bool success=false,protectedUnchanged=false;string error=null;
            try
            {
                app=(ISldWorks)Marshal.GetActiveObject("SldWorks.Application");original=(IModelDoc2)app.ActiveDoc;
                foreach(string relative in paths)
                {
                    string path=Path.Combine(root,relative);beforeHashes[relative]=BenchmarkFiles.Hash(path);IModelDoc2 part=Find(app,StateSerializer.Load(path).Document.Id);Activate(app,part);
                    ValidationResult v=Validate(part,path);v.Require();beforeGeometry[relative]=v.Measurements;BenchmarkFiles.Save(Path.Combine(directory,"protected-before-"+beforeGeometry.Count+".json"),v);
                }
                Activate(app,original);
                string manifest=Path.Combine(directory,"two-iterations.json");
                File.WriteAllText(manifest,File.ReadAllText(Path.Combine(root,"tests/benchmark-cases/mvp.json")).Replace("\"iterations\": 1","\"iterations\": 2"),new UTF8Encoding(false));
                using(Process child=new Process())
                {
                    child.StartInfo=new ProcessStartInfo(Path.Combine(root,"bin/CadHarness.Cli.exe"),"benchmark \""+manifest+"\" \""+Path.Combine(directory,"runs")+"\"")
                    {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=root};
                    child.Start();string stdout=child.StandardOutput.ReadToEnd(),stderr=child.StandardError.ReadToEnd();child.WaitForExit();
                    File.WriteAllText(Path.Combine(directory,"cli-output.json"),stdout,new UTF8Encoding(false));
                    suite=BenchmarkFiles.Json.Deserialize<BenchmarkSuiteResult>(stdout);
                    Require(child.ExitCode==3 && String.IsNullOrEmpty(stderr) && suite.RunnerCompleted && suite.OriginalDocumentRestored && !suite.AllRequiredCasesPassed && suite.Status=="BLOCKED_CAPABILITY","Runner did not truthfully report capability blocking");
                }
                Require(suite.Cases.Count==10 && suite.Summary.PassedCases==8 && suite.Summary.BlockedCases==2 && suite.Summary.FailedCases==0 && suite.Summary.TotalSuccess==0.8 && suite.Summary.LlmCalls==0 && suite.Summary.ParseFailures==0,"Incorrect suite aggregates");
                HashSet<string> identities=new HashSet<string>(),statePaths=new HashSet<string>();
                foreach(BenchmarkCaseResult c in suite.Cases)
                {
                    Require(c.SettingsRestored && statePaths.Add(c.StatePath),"Case settings or path isolation failed");
                    if(c.DocumentId!=null)Require(identities.Add(c.DocumentId),"A test Part was reused across cases/iterations");
                    foreach(BenchmarkStepResult step in c.Steps)
                    {
                        Require(step.ExpectedOutcomePassed && step.WallMs>=step.Runtime.RuntimeWallMs && step.Planner!=null && step.Planner.Calls==0 && step.TotalTokens==null,"Invalid command result/metrics");
                        int count=0;foreach(BenchmarkApiObservation api in step.Runtime.ApiOperations)count+=api.Calls;
                        Require(count==step.Runtime.DirectSolidWorksApiOperations,"API counters are not measured observations");
                        if(step.RuntimeEntered)Require(step.Runtime.Rebuilds>0 && step.Runtime.ValidationCalls>0 && step.Runtime.ValidationMs>0,"Missing runtime/rebuild/validation instrumentation");
                        else Require(step.Runtime.DirectSolidWorksApiOperations==0,"Unsupported input entered modeling APIs");
                    }
                    if(c.CaseId=="A")Require(c.FinalValidation.Success && c.FinalValidation.References.Valid==24 && StateSerializer.Load(c.StatePath).EditRevision==4,"Case A state invalid");
                    if(c.CaseId=="C")Require(c.Steps[1].RollbackSuccess==true && c.Steps[1].StateSha256Before==c.Steps[1].StateSha256After && c.FinalValidation.Success,"Case C rollback failed");
                    if(c.CaseId=="D")Require(c.ControlledReferenceInvalidation && c.SemanticFaceRecovered==true && c.FinalValidation.References.Valid==24 && StateSerializer.Load(c.StatePath).EditRevision==2,"Case D recovery failed");
                    if(c.CaseId=="B" || c.CaseId=="E")Require(!c.Steps[0].RuntimeEntered && !File.Exists(c.StatePath),"Unsupported case created state/geometry");
                }
                Require(suite.Summary.RollbackAttempts==2 && suite.Summary.SuccessfulRollbacks==2 && suite.Summary.EditCommands==12 && suite.Summary.SuccessfulEdits==10,"Success/failure denominators are wrong");
                success=true;
            }
            catch(Exception e){error=e.ToString();}
            finally
            {
                try
                {
                    protectedUnchanged=beforeHashes.Count==4;
                    foreach(string relative in paths)
                    {
                        string path=Path.Combine(root,relative);afterHashes[relative]=BenchmarkFiles.Hash(path);IModelDoc2 part=Find(app,StateSerializer.Load(path).Document.Id);Activate(app,part);
                        ValidationResult v=Validate(part,path);v.Require();BenchmarkFiles.Save(Path.Combine(directory,"protected-after-"+afterHashes.Count+".json"),v);
                        protectedUnchanged=protectedUnchanged && beforeHashes[relative]==afterHashes[relative] && BenchmarkRunner.Same(beforeGeometry[relative],v.Measurements);
                    }
                    if(original!=null)Activate(app,original);Require(protectedUnchanged,"Protected model or CADState changed");
                }
                catch(Exception e){success=false;error=(error??"")+" Protection: "+e.Message;}
                BenchmarkFiles.Save(Path.Combine(directory,"verification.json"),new{success=success,error=error,runnerVerified=success,allPrdCasesPassed=suite!=null && suite.AllRequiredCasesPassed,
                    protectedModelsUnchanged=protectedUnchanged,hashesBefore=beforeHashes,hashesAfter=afterHashes,resultPath=suite==null?null:suite.ResultPath,
                    summary=suite==null?null:suite.Summary,caseBBlocked=true,realLlmInferenceVerified=false});
            }
            Console.WriteLine(BenchmarkFiles.Json.Serialize(new{success=success,error=error,protectedModelsUnchanged=protectedUnchanged,allPrdCasesPassed=false}));return success?0:1;
        }
        private static IModelDoc2 Find(ISldWorks app,string id){foreach(IModelDoc2 part in (object[])app.GetDocuments())if(DocumentIdentityStore.Read(part)==id)return part;throw new Exception("Protected Part is not open: "+id);}
        private static void Activate(ISldWorks app,IModelDoc2 part){int status=0;IModelDoc2 active=(IModelDoc2)app.ActivateDoc3(part.GetTitle(),false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref status);Require(active!=null && status==0,"Direct activation failed");}
        private static ValidationResult Validate(IModelDoc2 part,string path){CadState state=StateSerializer.Load(path);DocumentIdentityStore.RequireMatch(part,state);return CadValidator.Read(part,state,CanonicalValues.From(state),true,true);}
        private static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
    }
}
