using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using CadHarness.Cli;

namespace CadHarness.Tests
{
    // Opt-in acceptance tests. Creates a separate Part; never edits protected Phase 4/5 Parts.
    internal static class Phase6LiveTests
    {
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=8388608};
        private static readonly List<object> Results=new List<object>();
        private static string root,statePath,directory;
        private static string explicitTemplate;
        private static ISldWorks app;
        private static IModelDoc2 testPart;
        private static int failures;
        [STAThread]
        private static int Main(string[] args)
        {
            root=Path.GetFullPath(args[0]); directory=Path.Combine(root,"artifacts/phase6/live"); Directory.CreateDirectory(directory);
            bool supplement=args.Length>1 && args[1]=="supplement";
            explicitTemplate=!supplement && args.Length>1?Path.GetFullPath(args[1]):null;
            statePath=Path.Combine(directory,"model-state.json");
            if(supplement) return Supplement();
            if(File.Exists(statePath)) {Console.WriteLine("Live state already exists; refusing to replace a previous verification Part.");return 1;}
            string originalPath=Path.Combine(root,"artifacts/state/9af37689-f21c-4096-af17-63b878455f3d.json");
            string phase5Path=Path.Combine(root,"artifacts/phase5/live-model-state.json");
            string originalHash=Hash(originalPath),phase5Hash=Hash(phase5Path);
            IModelDoc2 original=null;
            ValidationResult originalBefore=null,phase5Before=null,originalAfter=null,phase5After=null;
            bool protectedUnchanged=false;
            try
            {
                app=(ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                original=Find(StateSerializer.Load(originalPath).Document.Id);
                IModelDoc2 phase5=Find(StateSerializer.Load(phase5Path).Document.Id);
                Activate(original); originalBefore=Validate(originalPath); originalBefore.Require(); Save("original-before",originalBefore);
                Activate(phase5); phase5Before=Validate(phase5Path); phase5Before.Require(); Save("phase5-before",phase5Before);
                testPart=NewPart();
                Case("canonical creation validates before state commit",delegate {
                    int exit; Dictionary<string,object> result=Command(new[]{"create-canonical",statePath},"create",out exit);
                    Require(exit==0 && (bool)result["success"],"Create failed");
                    ValidationResult v=Validate(statePath);v.Require();Save("create-validate",v);
                    Require(StateSerializer.Load(statePath).EditRevision==0 && v.References.Valid==24,"Wrong initial state");
                });
                if(!File.Exists(statePath)) throw new Exception("Creation blocked; no live edit test can run.");
                string[] selectors={"mounting_holes.spacing","base_plate.thickness","mounting_hole_seed.diameter","outer_edge_fillet.radius"};
                string[] values={"50","12","10","4"};
                for(int i=0;i<4;i++)
                {
                    string selector=selectors[i],value=values[i];
                    Case("canonical edit "+selector,delegate {ParameterEditEvidence e=Edit(selector,value,null,"edit-"+selector);Require(e.Success && e.ValidationAfter.Success && e.StateCommitted && e.RevisionAfter==e.RevisionBefore+1 && e.ReferencesAfterRecovery.Valid==24,"Edit/validation failed");});
                }
                Case("strict validate refuses stale refs without modifying state/model",delegate {
                    CadState state=StateSerializer.Load(statePath); ValidationResult before=Validate(statePath);
                    foreach(SemanticEntityReference entity in state.SemanticEntities) if(CanonicalEntityResolver.Supports(entity.SemanticId)) for(int i=0;i<entity.PersistentReferences.Length;i++)entity.PersistentReferences[i]="AQIDBA==";
                    StateSerializer.Save(statePath,state); string hash=Hash(statePath);
                    int exit;Dictionary<string,object> record=Command(new[]{"validate",statePath},"stale-validate",out exit);
                    Require(exit==1 && !(bool)record["Success"] && Hash(statePath)==hash,"Stale validation mutated state");
                    Require(Same(before.Measurements,CadValidator.Read(testPart,state,CanonicalValues.From(state),true,false).Measurements),"Geometry changed");
                });
                Case("R1 repairs 17 stale Face/Edge refs and atomically commits",delegate {
                    CadState before=StateSerializer.Load(statePath);int exit;
                    Dictionary<string,object> record=Command(new[]{"recover-state",statePath},"recover-17-refs",out exit);
                    Require(exit==0 && (bool)record["Success"] && (bool)record["StateCommitted"],"Reference recovery failed");
                    CadState after=StateSerializer.Load(statePath);ValidationResult v=Validate(statePath);v.Require();Save("recovered-validate",v);
                    Require(after.EditRevision==before.EditRevision+1 && v.References.Valid==24,"Recovery state invalid");
                });
                Case("R1 recovery is idempotent",delegate {
                    string hash=Hash(statePath);int exit;Dictionary<string,object> r=Command(new[]{"recover-state",statePath},"recover-idempotent",out exit);
                    Require(exit==0 && !(bool)r["StateCommitted"] && Hash(statePath)==hash,"Unnecessary state commit");
                });
                Case("R1 integrated edit commits refs only with successful edit",delegate {
                    CadState state=StateSerializer.Load(statePath);
                    state.SemanticEntities.Find(delegate(SemanticEntityReference e){return e.SemanticId=="base_plate.top_face";}).PersistentReferences[0]="AQIDBA==";
                    state.SemanticEntities.Find(delegate(SemanticEntityReference e){return e.SemanticId=="mounting_holes.direction_edge";}).PersistentReferences[0]="AQIDBA==";
                    StateSerializer.Save(statePath,state);
                    ParameterEditEvidence r=Edit("mounting_holes.spacing","55",null,"r1-edit");
                    Require(r.Success && r.Before.Invalid==2 && r.AfterAttempt.Invalid==2 && r.ReferencesAfterRecovery.Valid==24 && r.RecoveryAttempts.Count==2,"R1 edit did not preserve original audit/repair");
                });
                Case("R2 one dropped native write recovers with fresh dimension",delegate {
                    DroppedWrites writes=new DroppedWrites(1);ParameterEditEvidence r=Edit("mounting_holes.spacing","60",writes,"r2-dropped-write");
                    Require(r.Success && writes.Calls==2 && r.RecoveryAttempts.Count==1 && r.RecoveryAttempts[0].RecoveryClass=="R2" && r.RecoveryAttempts[0].Success,"Bounded R2 failed");
                });
                Case("R2 exhausted recovery rolls back without state commit",delegate {
                    string hash=Hash(statePath);ValidationResult before=Validate(statePath);DroppedWrites writes=new DroppedWrites(2);
                    ParameterEditEvidence r=Edit("outer_edge_fillet.radius","5",writes,"r2-exhausted");
                    Require(!r.Success && writes.Calls==2 && r.RollbackSucceeded && !r.StateCommitted && Hash(statePath)==hash && Same(before.Measurements,Validate(statePath).Measurements),"R2 failed transaction changed state/model");
                });
                Case("R3 impossible spacing rolls back parameter and seed center",delegate {
                    string hash=Hash(statePath);ValidationResult before=Validate(statePath);
                    ParameterEditEvidence r=Edit("mounting_holes.spacing","200",null,"r3-spacing-failure");
                    Require(!r.Success && r.FailureClass=="FEATURE_REBUILD_FAILED" && r.RollbackSucceeded && r.ValidationRollback.Success && Hash(statePath)==hash && Same(before.Measurements,Validate(statePath).Measurements),"R3 rollback failed");
                });
                Case("R4 impossible fillet reports invalid geometry and rolls back",delegate {
                    string hash=Hash(statePath);ValidationResult before=Validate(statePath);
                    ParameterEditEvidence r=Edit("outer_edge_fillet.radius","100",null,"r4-fillet-failure");
                    Require(!r.Success && r.FailureClass=="GEOMETRY_IMPOSSIBLE" && r.RollbackSucceeded && r.ValidationRollback.Success && Hash(statePath)==hash && Same(before.Measurements,Validate(statePath).Measurements),"R4 rollback failed");
                });
                Case("R5 unsupported intent does not enter Runtime",delegate {
                    string hash=Hash(statePath);ValidationResult before=Validate(statePath);int exit;
                    Dictionary<string,object> r=Command(new[]{"run","rule","Create an aerodynamic turbine blade with optimized cooling passages.",statePath},"r5-unsupported",out exit);
                    Require(exit==1 && !(bool)r["RuntimeEntered"] && (string)r["FailureClass"]=="UNSUPPORTED_INTENT" && Hash(statePath)==hash && Same(before.Measurements,Validate(statePath).Measurements),"Unsupported intent mutated model");
                });
                Case("R1 refuses feature identity loss; no name fallback",delegate {
                    CadState state=StateSerializer.Load(statePath);state.Features.BasePlate.PersistentReference="AQIDBA==";
                    string path=Path.Combine(directory,"lost-feature-state.json");StateSerializer.Save(path,state);string hash=Hash(path);int exit;
                    Dictionary<string,object> r=Command(new[]{"recover-state",path},"r1-feature-rejected",out exit);
                    Require(exit==1 && !(bool)r["StateCommitted"] && Hash(path)==hash,"Feature fallback/commit occurred");Validate(statePath).Require();
                });
                Case("valid persistent reference with wrong semantic geometry rejected",delegate {
                    CadState state=StateSerializer.Load(statePath);
                    state.SemanticEntities.Find(delegate(SemanticEntityReference e){return e.SemanticId=="base_plate.top_face";}).PersistentReferences[0]=state.SemanticEntities.Find(delegate(SemanticEntityReference e){return e.SemanticId=="base_plate.bottom_face";}).PersistentReferences[0];
                    string path=Path.Combine(directory,"wrong-semantic-state.json");StateSerializer.Save(path,state);string hash=Hash(path);int exit;
                    Dictionary<string,object> r=Command(new[]{"recover-state",path},"wrong-semantic-rejected",out exit);
                    Require(exit==1 && (string)r["FailureClass"]=="SEMANTIC_REFERENCE_MISMATCH" && Hash(path)==hash,"Wrong semantics accepted");Validate(statePath).Require();
                });
                Case("invalid binding rejected before mutation",delegate {
                    CadState state=StateSerializer.Load(statePath);state.DimensionBindings[4].OwnerObjectSemanticId="base_plate";
                    string path=Path.Combine(directory,"bad-binding-state.json");StateSerializer.Save(path,state);string hash=Hash(path);
                    ParameterEditEvidence r=ParameterEditRunner.Execute(app,testPart,ParameterEditIntent.ParseCli("mounting_holes.spacing","65"),path);Save("bad-binding-rejected",r);
                    Require(!r.Success && !r.MutationStarted && !r.StateCommitted && Hash(path)==hash,"Bad binding entered mutation");Validate(statePath).Require();
                });
                Case("creation persistence failure removes only new features",delegate {
                    string hash=Hash(statePath);IModelDoc2 failedPart=NewPart();
                    try
                    {
                        int exit;Dictionary<string,object> r=Command(new[]{"create-canonical",statePath},"create-ownership-failure",out exit);
                        Dictionary<string,object> rollback=r["creationRollback"] as Dictionary<string,object>;
                        Require(exit==1 && rollback!=null && (bool)rollback["Success"] && !(bool)r["partialModelMayRemain"] && Hash(statePath)==hash,"Create rollback failed");
                        Array bodies=((IPartDoc)failedPart).GetBodies2((int)swBodyType_e.swAllBodies,false) as Array;
                        Require((bodies==null || bodies.Length==0) && DocumentIdentityStore.Read(failedPart)==null,"Partial created model/identity remains");
                    }
                    finally {app.CloseDoc(failedPart.GetTitle());Activate(testPart);}
                });
                Case("display-name changes preserve semantic execution",delegate {
                    CadState state=StateSerializer.Load(statePath);EditContext context=new EditContext(testPart,state);
                    foreach(SemanticFeature f in state.Features.All())context.Feature(f.SemanticId).Name="Phase6Renamed_"+f.SwFeatureIdAtCapture;
                    ParameterEditEvidence r=Edit("outer_edge_fillet.radius","4",null,"renamed-edit");Require(r.Success,"Name-dependent execution");
                });
                int finalExit;Command(new[]{"validate",statePath},"final-cli-validation",out finalExit);
                Require(finalExit==0,"Final native validation failed");
                Save("final-resolve",StateService.LoadAndResolve(testPart,statePath));
            }
            catch(Exception error) {failures++;Results.Add(new {name="live prerequisite/execution",success=false,error=error.Message});}
            finally
            {
                if(original!=null)
                {
                    try
                    {
                        Activate(Find(StateSerializer.Load(phase5Path).Document.Id));phase5After=Validate(phase5Path);phase5After.Require();Save("phase5-after",phase5After);
                        Activate(original);originalAfter=Validate(originalPath);originalAfter.Require();Save("original-after",originalAfter);
                        protectedUnchanged=Hash(originalPath)==originalHash && Hash(phase5Path)==phase5Hash && Same(originalBefore.Measurements,originalAfter.Measurements) && Same(phase5Before.Measurements,phase5After.Measurements);
                    }
                    catch(Exception error){failures++;Results.Add(new {name="protected original verification",success=false,error=error.Message});}
                }
                if(!protectedUnchanged){failures++;Results.Add(new {name="protected state/model unchanged",success=false});}
                Save("summary",new {success=failures==0,total=Results.Count,failed=failures,results=Results,phase4OriginalStateSha256Before=originalHash,phase4OriginalStateSha256After=Hash(originalPath),phase5StateSha256Before=phase5Hash,phase5StateSha256After=Hash(phase5Path),protectedModelsUnchanged=protectedUnchanged,statePath=statePath,
                    testDocumentId=File.Exists(statePath)?StateSerializer.Load(statePath).Document.Id:null,
                    notes="R1 uses controlled invalid bytes in stored Face/Edge references, native API restoration and real geometry fallback. R2 uses a test-only dropped-write adapter with real second write/rebuild/validation. R3/R4 use actual impossible geometry through production API. No LLM call, UI automation, batching or regeneration."});
            }
            Console.WriteLine(Json.Serialize(new {success=failures==0,total=Results.Count,failed=failures,protectedModelsUnchanged=protectedUnchanged}));return failures==0?0:1;
        }
        private sealed class DroppedWrites:IParameterMutation
        {
            private readonly int drop;internal int Calls;
            internal DroppedWrites(int count){drop=count;}
            public void Set(EditContext context,ParameterEditIntent intent){Calls++;if(Calls>drop)new NativeParameterMutation().Set(context,intent);}
        }
        private sealed class FailedFirstWrite:IParameterMutation
        {
            internal int Calls;
            public void Set(EditContext context,ParameterEditIntent intent)
            {Calls++;if(Calls==1)throw new Program.PhaseFailure("EDIT_DIMENSION_SET_FAILED","Controlled first-write status failure in verification adapter.");new NativeParameterMutation().Set(context,intent);}
        }
        private static int Supplement()
        {
            string originalPath=Path.Combine(root,"artifacts/state/9af37689-f21c-4096-af17-63b878455f3d.json"),phase5Path=Path.Combine(root,"artifacts/phase5/live-model-state.json");
            string originalHash=Hash(originalPath),phase5Hash=Hash(phase5Path);IModelDoc2 original=null;
            ValidationResult originalBefore=null,phase5Before=null;bool protectedUnchanged=false;
            try
            {
                app=(ISldWorks)Marshal.GetActiveObject("SldWorks.Application");original=Find(StateSerializer.Load(originalPath).Document.Id);
                Activate(original);originalBefore=Validate(originalPath);originalBefore.Require();
                Activate(Find(StateSerializer.Load(phase5Path).Document.Id));phase5Before=Validate(phase5Path);phase5Before.Require();
                testPart=Find(StateSerializer.Load(statePath).Document.Id);Activate(testPart);
                Case("R1 repaired refs are not committed when later edit fails",delegate {
                    ValidationResult before=Validate(statePath);CadState state=StateSerializer.Load(statePath);
                    state.SemanticEntities.Find(delegate(SemanticEntityReference entity){return entity.SemanticId=="base_plate.top_face";}).PersistentReferences[0]="AQIDBA==";
                    string path=Path.Combine(directory,"stale-failed-edit-state.json");StateSerializer.Save(path,state);string hash=Hash(path);
                    ParameterEditEvidence r=ParameterEditRunner.Execute(app,testPart,ParameterEditIntent.ParseCli("outer_edge_fillet.radius","100"),path);Save("r1-failed-edit-no-commit",r);
                    Require(!r.Success && r.RollbackSucceeded && !r.StateCommitted && Hash(path)==hash && r.Before.Invalid==1 && r.AfterRollback.Invalid==1 && r.ReferencesAfterRecovery.Valid==24 && Same(before.Measurements,Validate(statePath).Measurements),"Failed edit committed repaired references or lost original audit");
                });
                Case("R1 SketchPoint loss remains stale reference failure",delegate {
                    CadState state=StateSerializer.Load(statePath);
                    state.SemanticEntities.Find(delegate(SemanticEntityReference entity){return entity.SemanticId=="mounting_hole_seed.center_point";}).PersistentReferences[0]="AQIDBA==";
                    string path=Path.Combine(directory,"lost-sketchpoint-state.json");StateSerializer.Save(path,state);string hash=Hash(path);int exit;
                    Dictionary<string,object> r=Command(new[]{"recover-state",path},"r1-sketchpoint-rejected",out exit);
                    Require(exit==1 && (string)r["FailureClass"]=="STALE_REFERENCE" && !(bool)r["StateCommitted"] && Hash(path)==hash,"SketchPoint identity was guessed");Validate(statePath).Require();
                });
                Case("R2 native write-status failure uses one bounded fresh write",delegate {
                    FailedFirstWrite writes=new FailedFirstWrite();ParameterEditEvidence r=Edit("base_plate.thickness","14",writes,"r2-status-failure");
                    Require(r.Success && writes.Calls==2 && r.RecoveryAttempts.Count==1 && r.RecoveryAttempts[0].Success && r.ReferencesAfterRecovery.Valid==24,"Write-status recovery failed");
                });
                Case("R3 spacing rollback recheck with CAD measurement tolerance",delegate {
                    string hash=Hash(statePath);ValidationResult before=Validate(statePath);
                    ParameterEditEvidence r=Edit("mounting_holes.spacing","200",null,"r3-tolerance-recheck");
                    Require(!r.Success && r.FailureClass=="FEATURE_REBUILD_FAILED" && r.RollbackSucceeded && r.ValidationRollback.Success && Hash(statePath)==hash && Same(before.Measurements,Validate(statePath).Measurements),"R3 rollback failed within canonical tolerance");
                });
                Case("R4 failure classification propagates through planner CLI",delegate {
                    string hash=Hash(statePath);int exit;
                    Dictionary<string,object> r=Command(new[]{"run","rule","fillet R100",statePath},"r4-rule-runtime",out exit);
                    Dictionary<string,object> runtime=r["RuntimeResult"] as Dictionary<string,object>;
                    Require(exit==1 && (bool)r["RuntimeEntered"] && (string)r["FailureClass"]=="GEOMETRY_IMPOSSIBLE" && runtime!=null && (bool)runtime["RollbackSucceeded"] && Hash(statePath)==hash,"Runtime failure classification/rollback lost at planner boundary");Validate(statePath).Require();
                });
                int exitFinal;Command(new[]{"validate",statePath},"supplement-final-cli-validation",out exitFinal);Require(exitFinal==0,"Final current-source validation failed");
                Save("supplement-final-resolve",StateService.LoadAndResolve(testPart,statePath));
            }
            catch(Exception error){failures++;Results.Add(new {name="supplement prerequisite/execution",success=false,error=error.Message});}
            finally
            {
                if(original!=null)
                {
                    try
                    {
                        Activate(Find(StateSerializer.Load(phase5Path).Document.Id));ValidationResult p5=Validate(phase5Path);p5.Require();Save("supplement-phase5-after",p5);
                        Activate(original);ValidationResult p4=Validate(originalPath);p4.Require();Save("supplement-original-after",p4);
                        protectedUnchanged=Hash(originalPath)==originalHash && Hash(phase5Path)==phase5Hash && Same(originalBefore.Measurements,p4.Measurements) && Same(phase5Before.Measurements,p5.Measurements);
                    }
                    catch(Exception error){failures++;Results.Add(new {name="supplement protection",success=false,error=error.Message});}
                }
                if(!protectedUnchanged)failures++;
                Save("supplement-summary",new {success=failures==0,total=Results.Count,failed=failures,results=Results,protectedModelsUnchanged=protectedUnchanged,statePath=statePath,
                    finalRevision=StateSerializer.Load(statePath).EditRevision,finalValues=CanonicalValues.From(StateSerializer.Load(statePath)),phase4Sha256=Hash(originalPath),phase5Sha256=Hash(phase5Path)});
            }
            Console.WriteLine(Json.Serialize(new {supplement=true,success=failures==0,total=Results.Count,failed=failures}));return failures==0?0:1;
        }
        private static ParameterEditEvidence Edit(string selector,string value,IParameterMutation mutation,string label)
        {ParameterEditEvidence r=mutation==null?ParameterEditRunner.Execute(app,testPart,ParameterEditIntent.ParseCli(selector,value),statePath):ParameterEditRunner.Execute(app,testPart,ParameterEditIntent.ParseCli(selector,value),statePath,mutation);Save(label,r);return r;}
        private static ValidationResult Validate(string path)
        {CadState s=StateSerializer.Load(path);DocumentIdentityStore.RequireMatch((IModelDoc2)app.ActiveDoc,s);return CadValidator.Read((IModelDoc2)app.ActiveDoc,s,CanonicalValues.From(s),true,true);}
        private static Dictionary<string,object> Command(string[] args,string label,out int exit)
        {
            TextWriter previous=Console.Out;string text;
            using(StringWriter capture=new StringWriter()) {try {Console.SetOut(capture);exit=Program.ExecuteVerifiedRuntime(args);}finally{Console.SetOut(previous);}text=capture.ToString();}
            Dictionary<string,object> r=Json.Deserialize<Dictionary<string,object>>(text);Save(label,r);return r;
        }
        private static IModelDoc2 NewPart()
        {string template=explicitTemplate??app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);if(String.IsNullOrEmpty(template)||!File.Exists(template))throw new Exception("No native Part template available; pass the previously verified template explicitly.");IModelDoc2 part=(IModelDoc2)app.NewDocument(template,0,0,0);if(part==null)throw new Exception("Direct NewDocument returned null.");return part;}
        private static IModelDoc2 Find(string id)
        {foreach(IModelDoc2 document in (object[])app.GetDocuments())if(DocumentIdentityStore.Read(document)==id)return document;throw new Exception("Protected managed Part is not open: "+id);}
        private static void Activate(IModelDoc2 model)
        {int status=0;IModelDoc2 active=(IModelDoc2)app.ActivateDoc3(model.GetTitle(),false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref status);if(status!=0||active==null)throw new Exception("Direct document activation failed.");}
        private static string Hash(string path)
        {using(System.Security.Cryptography.SHA256 sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}
        private static bool Same(object a,object b){return SameValue(Json.DeserializeObject(Json.Serialize(a)),Json.DeserializeObject(Json.Serialize(b)));}
        private static bool SameValue(object a,object b)
        {
            if(a==null || b==null)return a==null && b==null;
            Dictionary<string,object> left=a as Dictionary<string,object>,right=b as Dictionary<string,object>;
            if(left!=null || right!=null)
            {if(left==null || right==null || left.Count!=right.Count)return false;foreach(KeyValuePair<string,object> item in left){object value;if(!right.TryGetValue(item.Key,out value)||!SameValue(item.Value,value))return false;}return true;}
            object[] arrayA=a as object[],arrayB=b as object[];
            if(arrayA!=null || arrayB!=null)
            {if(arrayA==null || arrayB==null || arrayA.Length!=arrayB.Length)return false;for(int i=0;i<arrayA.Length;i++)if(!SameValue(arrayA[i],arrayB[i]))return false;return true;}
            if((a is int || a is long || a is double || a is decimal) && (b is int || b is long || b is double || b is decimal))return CadValidator.Near(Convert.ToDouble(a),Convert.ToDouble(b));
            return a.Equals(b);
        }
        private static void Save(string label,object value){File.WriteAllText(Path.Combine(directory,label+".json"),Json.Serialize(value),new UTF8Encoding(false));}
        private static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
        private static void Case(string name,Action action)
        {try{action();Results.Add(new {name=name,success=true});Console.WriteLine(Json.Serialize(new {name=name,success=true}));}catch(Exception error){failures++;Results.Add(new {name=name,success=false,error=error.Message});Console.WriteLine(Json.Serialize(new {name=name,success=false,error=error.Message}));}}
    }
}
