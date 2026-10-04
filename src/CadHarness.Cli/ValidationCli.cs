using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    public sealed class ValidationRunEvidence
    {
        public string RunId { get; set; }
        public string Operation { get; set; }
        public string StatePath { get; set; }
        public string EvidencePath { get; set; }
        public bool Success { get; set; }
        public bool ModelModified { get; set; }
        public bool StateCommitted { get; set; }
        public int RevisionBefore { get; set; }
        public int RevisionAfter { get; set; }
        public string StateSha256Before { get; set; }
        public string StateSha256After { get; set; }
        public string ErrorCode { get; set; }
        public string Error { get; set; }
        public string FailureClass { get; set; }
        public ValidationResult Validation { get; set; }
        public ReferenceSnapshot ReferencesBefore { get; set; }
        public List<RecoveryAttempt> RecoveryAttempts { get; set; }
    }

    internal static class ValidationCli
    {
        internal static int Run(string[] args)
        {
            if(args.Length>2) { Console.Error.WriteLine("Usage: validate|recover-state [state-file]"); return 2; }
            ValidationRunEvidence e=new ValidationRunEvidence { RunId=Guid.NewGuid().ToString("D"),Operation=args[0],RecoveryAttempts=new List<RecoveryAttempt>() };
            e.EvidencePath=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","artifacts","validation",e.RunId+".json"));
            try
            {
                ISldWorks app=(ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                IModelDoc2 model=app.ActiveDoc as IModelDoc2;
                if(model==null || model.GetType()!=(int)swDocumentTypes_e.swDocPART) throw new Program.PhaseFailure("ACTIVE_DOCUMENT_NOT_PART","An active managed Part is required.");
                string id=DocumentIdentityStore.Read(model);
                if(args.Length==1 && id==null) throw new Program.PhaseFailure("DOCUMENT_NOT_MANAGED","No Harness identity exists.");
                e.StatePath=Path.GetFullPath(args.Length==2?args[1]:StateService.DefaultPath(id));
                if(args[0]=="recover-state")
                {
                    using(FileStream stateLock=new FileStream(e.StatePath+".edit.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                    { Execute(model,e,true); }
                }
                else Execute(model,e,false);
            }
            catch(Exception error)
            {
                Program.PhaseFailure known=error as Program.PhaseFailure;
                e.ErrorCode=known==null?"VALIDATION_LOCAL_FAILED":known.Code; e.Error=error.Message;
                e.FailureClass=e.Validation!=null && !e.Validation.Success?e.Validation.FailureClass:FailureClassifier.Classify(e.ErrorCode,null);
            }
            if(e.StatePath!=null && File.Exists(e.StatePath)) e.StateSha256After=Hash(e.StatePath);
            try { Save(e); }
            catch(Exception) { if(!e.StateCommitted) e.Success=false; e.ErrorCode="VALIDATION_EVIDENCE_WRITE_FAILED"; }
            Console.WriteLine(new JavaScriptSerializer().Serialize(e)); return e.Success?0:1;
        }
        private static void Execute(IModelDoc2 model,ValidationRunEvidence e,bool repair)
        {
            CadState state=StateSerializer.Load(e.StatePath); DocumentIdentityStore.RequireMatch(model,state);
            e.StateSha256Before=Hash(e.StatePath); e.RevisionBefore=state.EditRevision; e.RevisionAfter=state.EditRevision;
            e.ReferencesBefore=ReferenceAudit.Observe(model,state);
            Save(e); // Evidence must be writable before a state transaction.
            if(repair) LocalRecovery.RepairEntities(model,state,CanonicalValues.From(state),e.RecoveryAttempts);
            e.Validation=CadValidator.Read(model,state,CanonicalValues.From(state),true,true); e.Validation.Require();
            if(repair && e.RecoveryAttempts.Count>0)
            {
                state.EditRevision++; state.UpdatedUtc=DateTime.UtcNow.ToString("o"); state.LastEditId=e.RunId;
                state.ReferenceHealth=e.Validation.References;
                Save(e);
                if(Hash(e.StatePath)!=e.StateSha256Before) throw new Program.PhaseFailure("EDIT_STATE_CHANGED_EXTERNALLY","State changed during reference repair.");
                StateSerializer.Save(e.StatePath,state); e.StateCommitted=true; e.RevisionAfter=state.EditRevision;
            }
            e.Success=true;
        }
        private static string Hash(string path) { return ReferenceAudit.Hash(File.ReadAllText(path,Encoding.UTF8)); }
        private static void Save(ValidationRunEvidence e)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(e.EvidencePath)); string tmp=e.EvidencePath+".tmp";
            try
            {
                File.WriteAllText(tmp,new JavaScriptSerializer().Serialize(e),new UTF8Encoding(false));
                if(File.Exists(e.EvidencePath)) File.Replace(tmp,e.EvidencePath,null); else File.Move(tmp,e.EvidencePath);
            }
            finally { if(File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}
