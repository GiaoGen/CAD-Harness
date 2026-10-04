using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using CadHarness.Cli;

namespace CadHarness.Tests
{
    internal static class Phase7FinalSmokeTests
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string root=Path.GetFullPath(args[0]),statePath=Path.Combine(root,"artifacts/phase7/live/3-optimized-state.json");
            JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=8388608};
            ISldWorks app=(ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
            IModelDoc2 original=(IModelDoc2)app.ActiveDoc;
            bool success=false;string error=null;object runtime=null,plateResult=null;double wall=0,plateWall=0;
            double[] measuredPlateBox=null;
            Dictionary<string,string> hashes=new Dictionary<string,string>();
            string[] paths={"artifacts/state/9af37689-f21c-4096-af17-63b878455f3d.json","artifacts/phase5/live-model-state.json","artifacts/phase6/live/model-state.json"};
            try
            {
                foreach(string relative in paths)hashes[relative]=Hash(Path.Combine(root,relative));
                CadState state=StateSerializer.Load(statePath);IModelDoc2 part=null;
                foreach(IModelDoc2 candidate in (object[])app.GetDocuments())if(DocumentIdentityStore.Read(candidate)==state.Document.Id)part=candidate;
                Require(part!=null,"Verified Phase 7 test Part is not open");Activate(app,part);
                NativeBatchSettings flags=new NativeBatchSettings(app,part);Dictionary<string,bool> before=new Dictionary<string,bool>();
                foreach(string n in new[]{"CommandInProgress","EnableFeatureTree","EnableFeatureTreeWindow","EnableGraphicsUpdate"})before[n]=flags.Read(n);
                Stopwatch timer=Stopwatch.StartNew();
                using(Process child=new Process())
                {
                    child.StartInfo=new ProcessStartInfo(Path.Combine(root,"bin/CadHarness.Cli.exe"),"edit outer_edge_fillet.radius 4 \""+statePath+"\"")
                    {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=root};
                    child.Start();string output=child.StandardOutput.ReadToEnd(),stderr=child.StandardError.ReadToEnd();child.WaitForExit();timer.Stop();wall=timer.Elapsed.TotalMilliseconds;
                    ParameterEditEvidence e=json.Deserialize<ParameterEditEvidence>(output);runtime=e;
                    Require(child.ExitCode==0 && String.IsNullOrEmpty(stderr) && e.ProcessId!=Process.GetCurrentProcess().Id && e.Success && e.StateCommitted && e.RevisionAfter==state.EditRevision+1 && e.Batch.Restored && e.Batch.ExplicitRebuildCalls==1 && e.ReferencesAfterRecovery.Valid==24,"Fresh production CLI failed");
                }
                foreach(KeyValuePair<string,bool> entry in before)Require(flags.Read(entry.Key)==entry.Value,"Native flag changed: "+entry.Key);
                state=StateSerializer.Load(statePath);ValidationResult validation=CadValidator.Read(part,state,CanonicalValues.From(state),true,true);validation.Require();
                File.WriteAllText(Path.Combine(root,"artifacts/phase7/final-production-validation.json"),json.Serialize(validation),new UTF8Encoding(false));
                // Also exercise the existing Phase 1 entry: it must retain its own rebuild.
                IModelDoc2 plate=(IModelDoc2)app.NewDocument(args[1],0,0,0);Require(plate!=null,"Direct plate test NewDocument failed");
                NativeBatchSettings plateFlags=new NativeBatchSettings(app,plate);Dictionary<string,bool> plateBefore=new Dictionary<string,bool>();
                foreach(string n in before.Keys)plateBefore[n]=plateFlags.Read(n);
                bool dimensionPrompt=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
                timer=Stopwatch.StartNew();
                using(Process child=new Process())
                {
                    child.StartInfo=new ProcessStartInfo(Path.Combine(root,"bin/CadHarness.Cli.exe"),"create-plate")
                    {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=root};
                    child.Start();string output=child.StandardOutput.ReadToEnd(),stderr=child.StandardError.ReadToEnd();child.WaitForExit();timer.Stop();plateWall=timer.Elapsed.TotalMilliseconds;
                    Dictionary<string,object> result=json.Deserialize<Dictionary<string,object>>(output);plateResult=result;
                    Dictionary<string,object> batch=(Dictionary<string,object>)result["batch"];
                    Require(child.ExitCode==0 && String.IsNullOrEmpty(stderr) && (bool)result["success"] && (bool)batch["Restored"] && Convert.ToInt32(batch["ExplicitRebuildCalls"])==1,"Fresh Phase 1 create-plate failed");
                }
                foreach(KeyValuePair<string,bool> entry in plateBefore)Require(plateFlags.Read(entry.Key)==entry.Value,"Plate native setting changed");
                Require(app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate)==dimensionPrompt,"Dimension prompt setting changed");
                object[] bodies=((IPartDoc)plate).GetBodies2((int)swBodyType_e.swAllBodies,false) as object[];
                Require(bodies!=null && bodies.Length==1 && plate.Extension.NeedsRebuild2==0,"Plate bodies/rebuild invalid");
                IBody2 body=(IBody2)bodies[0];double[] low={-40,-25,0},high={40,25,10};
                measuredPlateBox=new double[6];
                for(int axis=0;axis<3;axis++)foreach(int sign in new[]{-1,1})
                {
                    double[] direction=new double[3];direction[axis]=sign;double x,y,z;
                    Require(body.GetExtremePoint(direction[0],direction[1],direction[2],out x,out y,out z),"Plate extrema unavailable");
                    measuredPlateBox[sign<0?axis:axis+3]=new[]{x,y,z}[axis]*1000;
                    Require(CadValidator.Near(measuredPlateBox[sign<0?axis:axis+3],sign<0?low[axis]:high[axis]),"Wrong plate bounding box");
                }
                foreach(string relative in paths)Require(hashes[relative]==Hash(Path.Combine(root,relative)),"Protected state changed");success=true;
            }
            catch(Exception e){error=e.ToString();}
            finally {Activate(app,original);}
            object evidence=new{success=success,error=error,runtime=runtime,commandWallMs=wall,plate=plateResult,plateWallMs=plateWall,measuredPlateBoxMm=measuredPlateBox,expectedPlateBoxMm=new[]{-40,-25,0,40,25,10},protectedHashes=hashes,exeSha256=Hash(Path.Combine(root,"bin/CadHarness.Cli.exe")),freshProcess=true};
            File.WriteAllText(Path.Combine(root,"artifacts/phase7/final-production-smoke.json"),json.Serialize(evidence),new UTF8Encoding(false));
            Console.WriteLine(json.Serialize(new{success=success,error=error,wallMs=wall}));return success?0:1;
        }
        private static void Activate(ISldWorks app,IModelDoc2 part){int status=0;IModelDoc2 active=(IModelDoc2)app.ActivateDoc3(part.GetTitle(),false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref status);Require(active!=null && status==0,"Direct activation failed");}
        private static string Hash(string path){using(System.Security.Cryptography.SHA256 sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}
        private static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
    }
}
