using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using CadHarness.Cli;

namespace CadHarness.Tests
{
    // Fixed Phase 7 acceptance measurement, not the Phase 8 benchmark runner.
    internal static class Phase7LiveTests
    {
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=16777216};
        private static readonly List<object> cases=new List<object>(),samples=new List<object>();
        private static readonly Dictionary<string,EditConfirmationResult> geometry=new Dictionary<string,EditConfirmationResult>();
        private static readonly string[] protectedPaths={"artifacts/state/9af37689-f21c-4096-af17-63b878455f3d.json","artifacts/phase5/live-model-state.json","artifacts/phase6/live/model-state.json"};
        private static readonly string[] selectors={"mounting_holes.spacing","base_plate.thickness","mounting_hole_seed.diameter","outer_edge_fillet.radius"};
        private static readonly string[] values={"50","12","10","4"};
        private static string root,directory,template;
        private static ISldWorks app;
        private static MethodInfo baseline;
        private static IModelDoc2 lastPart;
        private static string lastState;
        private static int failed;
        [STAThread]
        private static int Main(string[] args)
        {
            root=Path.GetFullPath(args[0]);template=args[1];directory=Path.Combine(root,"artifacts/phase7/live");
            if(Directory.Exists(directory)){Console.WriteLine("Prior Phase 7 live evidence exists; refusing to overwrite.");return 1;}
            Directory.CreateDirectory(directory);
            Dictionary<string,string> beforeHashes=new Dictionary<string,string>();
            Dictionary<string,EditConfirmationResult> beforeGeometry=new Dictionary<string,EditConfirmationResult>();
            Dictionary<string,bool> originalFlags=null;
            bool protectedUnchanged=false;
            IModelDoc2 original=null;
            try
            {
                app=(ISldWorks)Marshal.GetActiveObject("SldWorks.Application");original=Find(StateSerializer.Load(Path.Combine(root,protectedPaths[0])).Document.Id);
                foreach(string relative in protectedPaths)
                {
                    string path=Path.Combine(root,relative);beforeHashes.Add(relative,Hash(path));
                    IModelDoc2 part=Find(StateSerializer.Load(path).Document.Id);Activate(part);
                    ValidationResult v=Validate(part,path);v.Require();beforeGeometry.Add(relative,v.Measurements);Save("protected-before-"+beforeGeometry.Count,v);
                }
                Activate(original);originalFlags=Flags(original);
                Assembly assembly=Assembly.LoadFrom(Path.Combine(root,"artifacts/phase7/baseline/CadHarness.Cli.exe"));
                baseline=assembly.GetType("CadHarness.Cli.Program").GetMethod("ExecuteVerifiedRuntime",BindingFlags.NonPublic|BindingFlags.Static);
                // One unmeasured warm-up pair, then three alternating paired observations.
                for(int pair=0;pair<4;pair++)
                {
                    string[] modes=pair%2==0?new[]{"baseline","optimized"}:new[]{"optimized","baseline"};
                    foreach(string mode in modes)RunSample(pair,mode);
                    Check("pair "+pair+" canonical and four edits have equivalent geometry",delegate {
                        foreach(string operation in new[]{"create","edit-0","edit-1","edit-2","edit-3"})
                            Require(Same(Comparable(geometry[pair+"-baseline-"+operation]),Comparable(geometry[pair+"-optimized-"+operation])),"Different geometry: "+operation);
                    });
                }
                Activate(lastPart);
                Check("native nested scope and exception restore exact flags",delegate {
                    Dictionary<string,bool> before=Flags(lastPart);SolidWorksBatchScope scope=null;
                    try
                    {
                        using(scope=new SolidWorksBatchScope(app,lastPart))
                        {
                            Dictionary<string,bool> inside=Flags(lastPart);Save("native-scope-inside",inside);
                            Require(inside["CommandInProgress"] && !inside["EnableFeatureTree"] && !inside["EnableFeatureTreeWindow"] && !inside["EnableGraphicsUpdate"],"Batch flags were not applied");
                            using(new SolidWorksBatchScope(app,lastPart)){}
                            Require(!Flags(lastPart)["EnableGraphicsUpdate"],"Inner scope enabled graphics");
                            throw new InvalidOperationException("controlled body failure");
                        }
                    }
                    catch(InvalidOperationException){}
                    Save("native-exception-scope",scope.Evidence);
                    Require(Same(before,Flags(lastPart)) && scope.Evidence.Restored && scope.Evidence.TreeRefreshCalls==1 && scope.Evidence.GraphicsRedrawCalls==1,"Exception cleanup failed");
                });
                Check("native pre-disabled flags survive scope",delegate {
                    NativeBatchSettings settings=new NativeBatchSettings(app,lastPart);Dictionary<string,bool> before=Flags(lastPart);
                    try
                    {
                        settings.Write("CommandInProgress",true);settings.Write("EnableFeatureTree",false);settings.Write("EnableFeatureTreeWindow",false);settings.Write("EnableGraphicsUpdate",false);
                        Dictionary<string,bool> disabled=Flags(lastPart);SolidWorksBatchScope scope=new SolidWorksBatchScope(app,lastPart);scope.Dispose();
                        Require(Same(disabled,Flags(lastPart)) && scope.Evidence.TreeRefreshCalls==0 && scope.Evidence.GraphicsRedrawCalls==0,"Disabled originals changed");Save("native-disabled-scope",scope.Evidence);
                    }
                    finally {RestoreFlags(settings,before);}
                });
                Check("native sketch batch restores flags after exception",delegate {
                    Dictionary<string,bool> before=Flags(lastPart);
                    try {using(new SketchBatchScope(lastPart.SketchManager)) {Require(lastPart.SketchManager.AddToDB && !lastPart.SketchManager.DisplayWhenAdded,"Sketch flags not set");throw new InvalidOperationException("controlled sketch failure");}}
                    catch(InvalidOperationException){}
                    Require(Same(before,Flags(lastPart)),"Sketch flags not restored");
                });
                RollbackCase("mounting_holes.spacing","200","R3");
                RollbackCase("outer_edge_fillet.radius","100","R4");
                Check("bounded R2 still rebuilds and atomically commits",delegate {
                    Dictionary<string,bool> flags=Flags(lastPart);DroppedWrites mutation=new DroppedWrites(1);
                    ParameterEditEvidence e=ParameterEditRunner.Execute(app,lastPart,ParameterEditIntent.ParseCli("base_plate.thickness","14"),lastState,mutation);Save("r2-retry",e);
                    Require(e.Success && e.StateCommitted && e.RevisionAfter==e.RevisionBefore+1 && mutation.Calls==2 && e.Batch.ExplicitRebuildCalls==2 && e.Batch.Restored && Same(flags,Flags(lastPart)),"R2 changed contract");Validate(lastPart,lastState).Require();
                });
                Check("exhausted R2 rollback keeps disk state unchanged",delegate {
                    string hash=Hash(lastState);Dictionary<string,bool> flags=Flags(lastPart);EditConfirmationResult before=Validate(lastPart,lastState).Measurements;DroppedWrites mutation=new DroppedWrites(2);
                    ParameterEditEvidence e=ParameterEditRunner.Execute(app,lastPart,ParameterEditIntent.ParseCli("outer_edge_fillet.radius","5"),lastState,mutation);Save("r2-exhausted",e);
                    Require(!e.Success && !e.StateCommitted && e.RollbackSucceeded && mutation.Calls==2 && e.Batch.ExplicitRebuildCalls==3 && e.Batch.Restored && Hash(lastState)==hash && Same(flags,Flags(lastPart)) && Same(Comparable(before),Comparable(Validate(lastPart,lastState).Measurements)),"R2 rollback changed contract");
                });
                Check("creation persistence failure rolls back only new features",delegate {
                    IModelDoc2 part=NewPart();Dictionary<string,bool> flags=Flags(part);int count=FeatureCount(part);
                    string invalidPath=Path.Combine(directory,"directory-not-state");Directory.CreateDirectory(invalidPath);
                    double wall;int exit;Dictionary<string,object> r=Command(false,new[]{"create-canonical",invalidPath},"creation-io-failure",out wall,out exit);
                    Dictionary<string,object> rollback=(Dictionary<string,object>)r["creationRollback"];
                    Require(exit==1 && (bool)rollback["Success"] && FeatureCount(part)==count && DocumentIdentityStore.Read(part)==null && Same(flags,Flags(part)),"Creation rollback failed");
                    Activate(lastPart);
                });
                Save("final-test-validation",Validate(lastPart,lastState));
            }
            catch(Exception e){failed++;cases.Add(new{name="live prerequisite/execution",success=false,error=e.ToString()});Console.WriteLine(e.Message);}
            finally
            {
                try
                {
                    protectedUnchanged=beforeHashes.Count==3;
                    foreach(string relative in protectedPaths)
                    {
                        string path=Path.Combine(root,relative);IModelDoc2 part=Find(StateSerializer.Load(path).Document.Id);Activate(part);
                        ValidationResult v=Validate(part,path);v.Require();Save("protected-after-"+Array.IndexOf(protectedPaths,relative),v);
                        protectedUnchanged=protectedUnchanged && beforeHashes[relative]==Hash(path) && Same(Comparable(beforeGeometry[relative]),Comparable(v.Measurements));
                    }
                    if(original!=null){Activate(original);Require(originalFlags==null || Same(originalFlags,Flags(original)),"Original application/view settings changed");}
                    Require(protectedUnchanged,"Protected models/state changed");
                }
                catch(Exception e){failed++;cases.Add(new{name="protected models and final activation",success=false,error=e.Message});}
                Save("verification",new{success=failed==0,failed=failed,cases=cases,samples=samples,protectedModelsUnchanged=protectedUnchanged,protectedHashesBefore=beforeHashes,
                    baselineExeSha256=Hash(Path.Combine(root,"artifacts/phase7/baseline/CadHarness.Cli.exe")),optimizedExeSha256=Hash(Path.Combine(root,"bin/CadHarness.Cli.exe")),
                    solidWorksRevision=app==null?null:app.RevisionNumber(),measurement="In-process CLI entry, existing out-of-process COM Runtime; wall includes validation, serialization and state/evidence I/O. Warm-up pair excluded. Explicit rebuild counts exclude native implicit rebuilds."});
            }
            Console.WriteLine(Json.Serialize(new{success=failed==0,failed=failed,cases=cases.Count,samples=samples.Count}));return failed==0?0:1;
        }
        private static void RunSample(int pair,string mode)
        {
            bool old=mode=="baseline";IModelDoc2 part=NewPart();string prefix=pair+"-"+mode;
            string statePath=Path.Combine(directory,prefix+"-state.json");Dictionary<string,bool> flags=Flags(part);
            double wall;int exit;
            Dictionary<string,object> create=Command(old,new[]{"create-canonical",statePath},prefix+"-create",out wall,out exit);
            Require(exit==0 && (bool)create["success"],"Creation failed: "+prefix);
            ValidationResult v=Validate(part,statePath);v.Require();geometry[prefix+"-create"]=v.Measurements;
            Require(v.References.Valid==24 && StateSerializer.Load(statePath).EditRevision==0 && Same(flags,Flags(part)),"Creation state/flags invalid");
            if(!old)Require((bool)((Dictionary<string,object>)create["batch"])["Restored"] && Convert.ToInt32(((Dictionary<string,object>)create["batch"])["ExplicitRebuildCalls"])==1,"Create batch/rebuild invalid");
            Save(prefix+"-create-validation",v);
            samples.Add(new{pair=pair,warmup=pair==0,mode=mode,operation="create",wallMs=wall,validationMs=((Dictionary<string,object>)((Dictionary<string,object>)create["cadState"])["validation"])["ValidationMs"],
                explicitRebuildCalls=old?2:1,rebuildCountSource=old?"verified baseline source call sites (not native event count)":"scope instrumentation",batch=old?null:create["batch"],statePath=statePath,validReferences=v.References.Valid});
            for(int i=0;i<selectors.Length;i++)
            {
                Dictionary<string,object> edit=Command(old,new[]{"edit",selectors[i],values[i],statePath},prefix+"-edit-"+i,out wall,out exit);
                Require(exit==0 && (bool)edit["Success"] && (bool)edit["StateCommitted"] && Convert.ToInt32(edit["RevisionAfter"])==i+1,"Edit failed: "+prefix+" "+selectors[i]);
                v=Validate(part,statePath);v.Require();geometry[prefix+"-edit-"+i]=v.Measurements;
                Require(v.References.Valid==24 && Same(flags,Flags(part)),"Edit refs/flags invalid");
                if(!old)Require((bool)((Dictionary<string,object>)edit["Batch"])["Restored"] && Convert.ToInt32(((Dictionary<string,object>)edit["Batch"])["ExplicitRebuildCalls"])==1,"Edit batch/rebuild invalid");
                samples.Add(new{pair=pair,warmup=pair==0,mode=mode,operation=selectors[i],wallMs=wall,rebuildMs=edit["RebuildMs"],validationMs=((Dictionary<string,object>)edit["ValidationAfter"])["ValidationMs"],
                    explicitRebuildCalls=1,rebuildCountSource=old?"verified baseline source call sites (not native event count)":"scope instrumentation",batch=old?null:edit["Batch"],statePath=statePath,validReferences=v.References.Valid});
            }
            if(!old){lastPart=part;lastState=statePath;}
            Console.WriteLine(Json.Serialize(new{sample=prefix,success=true,refs=24,revision=4}));
        }
        private static void RollbackCase(string selector,string value,string recovery)
        {
            Check(recovery+" rollback restores geometry/state/flags",delegate {
                Dictionary<string,bool> flags=Flags(lastPart);string hash=Hash(lastState);EditConfirmationResult before=Validate(lastPart,lastState).Measurements;
                ParameterEditEvidence e=ParameterEditRunner.Execute(app,lastPart,ParameterEditIntent.ParseCli(selector,value),lastState);Save("rollback-"+recovery,e);
                Require(!e.Success && !e.StateCommitted && e.RollbackSucceeded && e.ValidationRollback.Success && e.Batch.ExplicitRebuildCalls==2 && e.Batch.Restored && Hash(lastState)==hash && Same(flags,Flags(lastPart)),"Rollback contract failed");
                ValidationResult v=Validate(lastPart,lastState);v.Require();Require(v.References.Valid==24 && Same(Comparable(before),Comparable(v.Measurements)),"Rollback geometry/refs failed");
            });
        }
        private static Dictionary<string,object> Command(bool old,string[] args,string label,out double wall,out int exit)
        {
            TextWriter previous=Console.Out;string text;Stopwatch timer=new Stopwatch();
            using(StringWriter capture=new StringWriter())
            {try {Console.SetOut(capture);timer.Start();exit=old?(int)baseline.Invoke(null,new object[]{args}):Program.ExecuteVerifiedRuntime(args);timer.Stop();}finally{Console.SetOut(previous);}text=capture.ToString();}
            wall=timer.Elapsed.TotalMilliseconds;Dictionary<string,object> result=Json.Deserialize<Dictionary<string,object>>(text);Save(label,result);return result;
        }
        private static ValidationResult Validate(IModelDoc2 part,string path)
        {CadState state=StateSerializer.Load(path);DocumentIdentityStore.RequireMatch(part,state);return CadValidator.Read(part,state,CanonicalValues.From(state),true,true);}
        private static Dictionary<string,bool> Flags(IModelDoc2 part)
        {NativeBatchSettings s=new NativeBatchSettings(app,part);Dictionary<string,bool> values=new Dictionary<string,bool>();foreach(string n in new[]{"CommandInProgress","EnableFeatureTree","EnableFeatureTreeWindow","EnableGraphicsUpdate"})values[n]=s.Read(n);values["AddToDB"]=part.SketchManager.AddToDB;values["DisplayWhenAdded"]=part.SketchManager.DisplayWhenAdded;return values;}
        private static void RestoreFlags(NativeBatchSettings s,Dictionary<string,bool> values)
        {foreach(string n in new[]{"EnableGraphicsUpdate","EnableFeatureTreeWindow","EnableFeatureTree","CommandInProgress"})s.Write(n,values[n]);s.UpdateTree();s.Redraw();}
        private static IModelDoc2 NewPart(){IModelDoc2 p=(IModelDoc2)app.NewDocument(template,0,0,0);Require(p!=null,"Direct NewDocument failed");return p;}
        private static IModelDoc2 Find(string id){foreach(IModelDoc2 p in (object[])app.GetDocuments())if(DocumentIdentityStore.Read(p)==id)return p;throw new Exception("Protected model is not open: "+id);}
        private static void Activate(IModelDoc2 p){int status=0;IModelDoc2 active=(IModelDoc2)app.ActivateDoc3(p.GetTitle(),false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref status);Require(active!=null && status==0,"Direct activation failed");}
        private static int FeatureCount(IModelDoc2 p){int count=0;for(IFeature f=(IFeature)p.FirstFeature();f!=null;f=(IFeature)f.GetNextFeature())count++;return count;}
        private static object Comparable(EditConfirmationResult m)
        {return new{dimensions=m.DimensionsMm,errors=m.FeatureErrors,warnings=m.FeatureWarnings,needsRebuild=m.NeedsRebuild,bodies=m.SolidBodyCount,allBodies=m.AllBodyCount,box=m.ExactBoxMm,count=m.PatternCount,spacing=m.PatternSpacingMm,reverse=m.PatternReverseDirection,geometryPattern=m.PatternGeometryPattern,endCondition=m.SeedEndCondition,radius=m.FilletDefinitionRadiusMm,holes=Cylinders(m.Holes),fillets=Cylinders(m.Fillets),measuredSpacing=m.MeasuredHoleSpacingMm};}
        private static object[] Cylinders(List<CylinderMeasurement> values){List<object> list=new List<object>();foreach(CylinderMeasurement c in values)list.Add(new{radius=c.RadiusMm,point=c.AxisPointMm,direction=c.AxisDirection,z=c.ZRangeMm,circles=c.FullCircleBoundaryEdges});return list.ToArray();}
        private static bool Same(object a,object b){return SameValue(Json.DeserializeObject(Json.Serialize(a)),Json.DeserializeObject(Json.Serialize(b)));}
        private static bool SameValue(object a,object b)
        {
            if(a==null||b==null)return a==null&&b==null;Dictionary<string,object> da=a as Dictionary<string,object>,db=b as Dictionary<string,object>;
            if(da!=null||db!=null){if(da==null||db==null||da.Count!=db.Count)return false;foreach(KeyValuePair<string,object> e in da){object value;if(!db.TryGetValue(e.Key,out value)||!SameValue(e.Value,value))return false;}return true;}
            object[] aa=a as object[],ab=b as object[];if(aa!=null||ab!=null){if(aa==null||ab==null||aa.Length!=ab.Length)return false;for(int i=0;i<aa.Length;i++)if(!SameValue(aa[i],ab[i]))return false;return true;}
            if((a is int||a is long||a is double||a is decimal)&&(b is int||b is long||b is double||b is decimal))return CadValidator.Near(Convert.ToDouble(a),Convert.ToDouble(b));return a.Equals(b);
        }
        private static string Hash(string path){using(System.Security.Cryptography.SHA256 sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}
        private static void Save(string name,object value){File.WriteAllText(Path.Combine(directory,name+".json"),Json.Serialize(value),new UTF8Encoding(false));}
        private static void Require(bool ok,string reason){if(!ok)throw new Exception(reason);}
        private static void Check(string name,Action action){try{action();cases.Add(new{name=name,success=true});Console.WriteLine(name+": passed");}catch(Exception e){failed++;cases.Add(new{name=name,success=false,error=e.Message});Console.WriteLine(name+": "+e.Message);}}
        private sealed class DroppedWrites : IParameterMutation
        {private int remaining;internal int Calls;internal DroppedWrites(int count){remaining=count;}public void Set(EditContext c,ParameterEditIntent i){Calls++;if(remaining>0){remaining--;return;}EditContext.Set(c.Dimension(i.ParameterSemanticId),i.ValueMm);}}
    }
}
