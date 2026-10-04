using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    internal sealed class BenchmarkRunner
    {
        private readonly BenchmarkDefinition definition;
        private readonly string directory;
        private readonly ICadPlanner planner;
        private readonly ISldWorks app;
        private readonly IModelDoc2 original;
        private readonly Dictionary<string,bool> originalFlags;
        internal BenchmarkRunner(BenchmarkDefinition definition,string directory,ICadPlanner planner)
        {
            this.definition=definition;this.directory=directory;this.planner=planner;
            if(!File.Exists(definition.PartTemplate))throw new Program.PhaseFailure("BENCHMARK_TEMPLATE_MISSING","The configured native Part template does not exist.");
            app=(ISldWorks)Marshal.GetActiveObject("SldWorks.Application");original=app.ActiveDoc as IModelDoc2;
            if(original==null || original.GetType()!=(int)swDocumentTypes_e.swDocPART || original.GetActiveSketch2()!=null || app.CommandInProgress)
                throw new Program.PhaseFailure("BENCHMARK_MODEL_NOT_READY","An idle active Part is required; benchmark creates separate test Parts.");
            originalFlags=Flags(original);
        }
        internal void Execute(BenchmarkSuiteResult suite)
        {
            suite.SolidWorksRevision=app.RevisionNumber();
            try
            {
                for(int iteration=-definition.WarmupIterations;iteration<definition.Iterations;iteration++)foreach(string id in definition.Cases)
                {
                    BenchmarkCaseResult result=RunCase(id,iteration);suite.Cases.Add(result);
                    BenchmarkFiles.Save(suite.ResultPath,suite); // Completed cases survive interruption.
                    if(!result.SettingsRestored)throw new Program.PhaseFailure("BENCHMARK_SETTINGS_NOT_RESTORED","Native update settings did not restore; remaining cases were stopped.");
                }
                suite.RunnerCompleted=true;
            }
            finally
            {
                int status=0;IModelDoc2 active=(IModelDoc2)app.ActivateDoc3(original.GetTitle(),false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref status);
                suite.OriginalDocumentRestored=active!=null && status==0 && Same(originalFlags,Flags(original));
                if(!suite.OriginalDocumentRestored)throw new Program.PhaseFailure("BENCHMARK_ORIGINAL_RESTORE_FAILED","Original document/update state could not be restored through direct API.");
            }
        }
        private BenchmarkCaseResult RunCase(string id,int iteration)
        {
            Stopwatch clock=Stopwatch.StartNew();
            BenchmarkCaseResult result=new BenchmarkCaseResult {CaseId=id,Scenario=BenchmarkDefinition.Scenarios[id[0]-'A'],Iteration=iteration<0?iteration+definition.WarmupIterations:iteration,
                Warmup=iteration<0,Status="RUNNING",StatePath=Path.Combine(directory,(iteration<0?"warmup-":"iteration-")+Math.Abs(iteration)+"-"+id+"-state.json")};
            IModelDoc2 part=null;Dictionary<string,bool> flags=null;
            try
            {
                part=(IModelDoc2)app.NewDocument(definition.PartTemplate,0,0,0);
                if(part==null)throw new Program.PhaseFailure("BENCHMARK_NEW_PART_FAILED","Direct NewDocument did not return a Part.");
                result.PartTitle=part.GetTitle();flags=Flags(part);result.SetupMs=clock.Elapsed.TotalMilliseconds;
                if(id=="B" || id=="E")
                {
                    int features=Features(part);string beforeId=DocumentIdentityStore.Read(part);
                    string command=id=="B"?"Create a 100 x 60 x 8 mm plate with 4 diameter 6 mm through holes in rectangular symmetric placement.":"Create an aerodynamic turbine blade with optimized cooling passages.";
                    BenchmarkStepResult rejected=Step(result,command,null,true);
                    Require(rejected.ExpectedOutcomePassed && !rejected.RuntimeEntered && Features(part)==features && BodyCount(part)==0 && !File.Exists(result.StatePath) && DocumentIdentityStore.Read(part)==beforeId,"Unsupported instruction was not safely rejected without state/geometry changes.");
                    if(id=="B")
                    {result.Status="BLOCKED_CAPABILITY";result.ErrorCode="BENCHMARK_CASE_B_UNSUPPORTED";result.Error="Case B requires a rectangular four-hole intent, semantic state and validation unsupported by the Phase 1–7 canonical-only Runtime. No geometry support was added solely for this case.";}
                    else result.Status="PASSED";
                }
                else
                {
                    RequireStep(result,"Create canonical part",PlannedIntent.Canonical());
                    if(id=="A")
                    {
                        string[] parameters=IntentSchema.Parameters;double[] values={50,12,10,4};
                        for(int i=0;i<parameters.Length;i++)RequireStep(result,"Set "+parameters[i]+" to "+values[i].ToString(System.Globalization.CultureInfo.InvariantCulture)+" mm",Edit(parameters[i],values[i]));
                        ValidationResult final=Validate(part,result);final.Require();
                        CadState state=StateSerializer.Load(result.StatePath);
                        Require(state.EditRevision==4 && final.References.Valid==24,"Case A revision/references failed.");
                        foreach(SemanticFeature feature in state.Features.All())
                            Require(final.Measurements.FeatureIds[feature.SemanticId]==feature.SwFeatureIdAtCapture,"An existing feature was regenerated.");
                    }
                    else if(id=="C")
                    {
                        ValidationResult before=Validate(part,result);string hash=BenchmarkFiles.Hash(result.StatePath);int revision=StateSerializer.Load(result.StatePath).EditRevision;
                        BenchmarkStepResult failed=Step(result,"Set outer_edge_fillet.radius to 100 mm",Edit("outer_edge_fillet.radius",100),false);
                        ParameterEditEvidence evidence=BenchmarkFiles.Json.Deserialize<ParameterEditEvidence>(BenchmarkFiles.Json.Serialize(failed.RuntimeResult));
                        ValidationResult after=Validate(part,result);after.Require();
                        bool passed=failed.RuntimeEntered && !failed.RuntimeSuccess && evidence.FailureClass=="GEOMETRY_IMPOSSIBLE" && evidence.RollbackAttempted && evidence.RollbackSucceeded && !evidence.StateCommitted &&
                            BenchmarkFiles.Hash(result.StatePath)==hash && StateSerializer.Load(result.StatePath).EditRevision==revision && Same(before.Measurements,after.Measurements) && after.References.Valid==24;
                        failed.ExpectedOutcome="controlled GEOMETRY_IMPOSSIBLE with verified rollback and unchanged disk state";failed.ExpectedOutcomePassed=passed;
                        Require(passed,"Invalid fillet did not preserve the previous model/state.");
                    }
                    else
                    {
                        CadState state=StateSerializer.Load(result.StatePath);string oldReference=TopReference(state);
                        result.SemanticFaceBoxBeforeMm=TopBox(part,oldReference);
                        RequireStep(result,"Set base_plate.thickness to 12 mm",Edit("base_plate.thickness",12));
                        ReferenceSnapshot references=ReferenceAudit.Observe(part,state);state=StateSerializer.Load(result.StatePath);
                        result.NaturallyInvalidReferencesAfterEdit=references.Invalid;
                        result.SemanticFaceBoxAfterMm=TopBox(part,TopReference(state));
                        Require(CadValidator.Near(result.SemanticFaceBoxBeforeMm[2],10) && CadValidator.Near(result.SemanticFaceBoxAfterMm[2],12),"Required semantic top face did not track regenerated geometry.");
                        // Native edits on this model can preserve all refs. Force ONLY a disk
                        // Face reference stale for a separate, honestly labeled fallback probe.
                        result.ControlledReferenceInvalidation=true;
                        using(FileStream stateLock=new FileStream(result.StatePath+".edit.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                        {state.SemanticEntities.Find(delegate(SemanticEntityReference entity){return entity.SemanticId=="base_plate.top_face";}).PersistentReferences[0]="AQIDBA==";StateSerializer.Save(result.StatePath,state);}
                        Stopwatch recoveryClock=Stopwatch.StartNew();
                        ValidationRunEvidence probe;
                        using(BenchmarkTelemetry telemetry=new BenchmarkTelemetry())
                        {
                            TextWriter previous=Console.Out;string text;int exit;
                            using(StringWriter capture=new StringWriter())
                            {try{Console.SetOut(capture);exit=Program.ExecuteVerifiedRuntime(new[]{"recover-state",result.StatePath});}finally{Console.SetOut(previous);}text=capture.ToString();}
                            probe=BenchmarkFiles.Json.Deserialize<ValidationRunEvidence>(text);recoveryClock.Stop();telemetry.Metrics.RuntimeWallMs=recoveryClock.Elapsed.TotalMilliseconds;
                            result.RecoveryProbe=new {controlledInvalidation=true,wallMs=recoveryClock.Elapsed.TotalMilliseconds,exitCode=exit,runtime=telemetry.Metrics,result=probe};
                            Require(exit==0 && probe.Success && probe.StateCommitted && probe.ReferencesBefore.Invalid==1 && probe.RecoveryAttempts.Count==1 && probe.RecoveryAttempts[0].RecoveryClass=="R1" && probe.RecoveryAttempts[0].Success && probe.RevisionAfter==probe.RevisionBefore+1,"Bounded semantic Face fallback failed.");
                        }
                        ValidationResult final=Validate(part,result);final.Require();
                        result.SemanticFaceRecovered=final.References.Valid==24 && CadValidator.Near(TopBox(part,TopReference(StateSerializer.Load(result.StatePath)))[2],12);
                        Require(result.SemanticFaceRecovered==true,"Recovered Face has wrong semantic geometry.");
                    }
                    result.DocumentId=StateSerializer.Load(result.StatePath).Document.Id;result.Status="PASSED";
                }
            }
            catch(Exception error)
            {result.Status="FAILED";Program.PhaseFailure known=error as Program.PhaseFailure;result.ErrorCode=known==null?"BENCHMARK_CASE_FAILED":known.Code;result.Error=error.Message;}
            finally
            {
                try{result.SettingsRestored=part!=null && flags!=null && Same(flags,Flags(part));}catch{result.SettingsRestored=false;}
                clock.Stop();result.WallMs=clock.Elapsed.TotalMilliseconds;
                BenchmarkFiles.Save(Path.Combine(directory,(result.Warmup?"warmup-":"iteration-")+Math.Abs(iteration)+"-"+id+".json"),result);
            }
            return result;
        }
        private BenchmarkStepResult Step(BenchmarkCaseResult result,string command,PlannedIntent expected,bool rejectOnly)
        {
            BenchmarkStepResult step=BenchmarkStep.Execute(planner,command,result.StatePath,expected,rejectOnly,delegate(PlannedIntent plan){return BenchmarkStep.InvokeRuntime(plan,result.StatePath);});
            result.Steps.Add(step);return step;
        }
        private void RequireStep(BenchmarkCaseResult result,string command,PlannedIntent expected)
        {
            BenchmarkStepResult step=Step(result,command,expected,false);Require(step.ExpectedOutcomePassed,"Required planning/runtime command failed: "+command);
            CadState state=StateSerializer.Load(result.StatePath);
            Require(state.EditRevision==(step.RevisionBefore.HasValue?step.RevisionBefore.Value+1:0),"Atomic state revision did not match successful command.");
            if(expected.Mode==PlannedMode.Edit)Require(CadValidator.Near(CanonicalValues.From(state)[expected.Edit.ParameterSemanticId],expected.Edit.ValueMm),"Committed parameter does not match the benchmark intent.");
        }
        private ValidationResult Validate(IModelDoc2 part,BenchmarkCaseResult result)
        {CadState state=StateSerializer.Load(result.StatePath);DocumentIdentityStore.RequireMatch(part,state);ValidationResult validation=CadValidator.Read(part,state,CanonicalValues.From(state),true,true);result.AuditValidationMs+=validation.ValidationMs;result.FinalValidation=validation;return validation;}
        private static PlannedIntent Edit(string parameter,double value){return PlannedIntent.Parameter(ParameterEditIntent.ParseCli(parameter,value.ToString("R",System.Globalization.CultureInfo.InvariantCulture)));}
        private Dictionary<string,bool> Flags(IModelDoc2 part)
        {NativeBatchSettings settings=new NativeBatchSettings(app,part);Dictionary<string,bool> result=new Dictionary<string,bool>();foreach(string name in new[]{"CommandInProgress","EnableFeatureTree","EnableFeatureTreeWindow","EnableGraphicsUpdate"})result[name]=settings.Read(name);result["AddToDB"]=part.SketchManager.AddToDB;result["DisplayWhenAdded"]=part.SketchManager.DisplayWhenAdded;result["DimensionPrompt"]=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);return result;}
        private static string TopReference(CadState state){return state.SemanticEntities.Find(delegate(SemanticEntityReference entity){return entity.SemanticId=="base_plate.top_face";}).PersistentReferences[0];}
        private static double[] TopBox(IModelDoc2 part,string reference){IFace2 face=(IFace2)PersistentReferences.Resolve(part,reference,"face","base_plate.top_face");double[] box=(double[])face.GetBox();for(int i=0;i<box.Length;i++)box[i]*=1000;return box;}
        private static int BodyCount(IModelDoc2 part){Array bodies=((IPartDoc)part).GetBodies2((int)swBodyType_e.swAllBodies,false) as Array;return bodies==null?0:bodies.Length;}
        private static int Features(IModelDoc2 part){int count=0;for(IFeature feature=(IFeature)part.FirstFeature();feature!=null;feature=(IFeature)feature.GetNextFeature())count++;return count;}
        private static void Require(bool passed,string message){if(!passed)throw new Program.PhaseFailure("BENCHMARK_EXPECTATION_FAILED",message);}
        internal static bool Same(object a,object b){return SameValue(BenchmarkFiles.Json.DeserializeObject(BenchmarkFiles.Json.Serialize(a)),BenchmarkFiles.Json.DeserializeObject(BenchmarkFiles.Json.Serialize(b)));}
        private static bool SameValue(object a,object b)
        {
            if(a==null || b==null)return a==null && b==null;
            Dictionary<string,object> left=a as Dictionary<string,object>,right=b as Dictionary<string,object>;
            if(left!=null || right!=null){if(left==null || right==null || left.Count!=right.Count)return false;foreach(KeyValuePair<string,object> item in left){object other;if(!right.TryGetValue(item.Key,out other) || !SameValue(item.Value,other))return false;}return true;}
            object[] leftArray=a as object[],rightArray=b as object[];
            if(leftArray!=null || rightArray!=null){if(leftArray==null || rightArray==null || leftArray.Length!=rightArray.Length)return false;for(int i=0;i<leftArray.Length;i++)if(!SameValue(leftArray[i],rightArray[i]))return false;return true;}
            if((a is int || a is long || a is double || a is decimal) && (b is int || b is long || b is double || b is decimal))return CadValidator.Near(Convert.ToDouble(a),Convert.ToDouble(b));
            return a.Equals(b);
        }
    }
}
