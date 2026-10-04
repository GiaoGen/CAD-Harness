using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;

namespace CadHarness.Cli
{
    public sealed class RecoveryAttempt
    {
        public string RecoveryClass { get; set; }
        public string Action { get; set; }
        public string SemanticId { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public string ErrorCode { get; set; }
        public string[] BeforeReferences { get; set; }
        public string[] AfterReferences { get; set; }
        public ValidationResult Validation { get; set; }
    }

    internal static class FailureClassifier
    {
        internal static string Classify(string code,ParameterEditIntent intent)
        {
            if(code==null) return null;
            if(code=="PERSISTENT_REFERENCE_UNRESOLVED" || code=="REFERENCE_KIND_MISMATCH" || code=="RECOVERY_REFERENCE_UNSUPPORTED" || code=="RECOVERY_REFERENCE_AMBIGUOUS") return "STALE_REFERENCE";
            if(code=="SEMANTIC_REFERENCE_MISMATCH") return "SEMANTIC_REFERENCE_MISMATCH";
            if(code=="EDIT_MODEL_NOT_READY") return "MODEL_NOT_READY";
            if(code.IndexOf("AMBIGUOUS",StringComparison.Ordinal)>=0) return "AMBIGUOUS_INTENT";
            if(code.IndexOf("UNSUPPORTED",StringComparison.Ordinal)>=0) return "UNSUPPORTED_INTENT";
            if(code=="EDIT_DIMENSION_SET_FAILED" || code=="EDIT_PARAMETER_NOT_APPLIED") return "PARAMETER_NOT_APPLIED";
            if(code=="EDIT_REBUILD_FAILED" || code=="REBUILD_FAILED")
                return intent!=null && intent.Kind==ParameterEditKind.FilletRadius ? "GEOMETRY_IMPOSSIBLE" : "FEATURE_REBUILD_FAILED";
            if(code.IndexOf("STATE",StringComparison.Ordinal)>=0 || code.IndexOf("IDENTITY",StringComparison.Ordinal)>=0) return "STATE_MODEL_MISMATCH";
            return "DIRECT_API_FAILURE";
        }
    }

    // This boundary also allows a verification harness to simulate one failed write.
    // Production CLI always uses NativeParameterMutation, with no injection flags.
    internal interface IParameterMutation
    { void Set(EditContext context,ParameterEditIntent intent); }
    internal sealed class NativeParameterMutation : IParameterMutation
    {
        public void Set(EditContext context,ParameterEditIntent intent)
        { EditContext.Set(context.Dimension(intent.ParameterSemanticId),intent.ValueMm); }
    }

    internal static class LocalRecovery
    {
        internal static RecoveryAttempt RetryParameter(string parameter,Action setFresh,Func<bool> rebuild,Func<ValidationResult> validate)
        {
            RecoveryAttempt attempt=new RecoveryAttempt { RecoveryClass="R2",Action="re-resolve dimension, set once, rebuild, validate",SemanticId=parameter };
            try
            {
                setFresh();
                if(!rebuild()) throw new Program.PhaseFailure("EDIT_REBUILD_FAILED","Recovery rebuild failed.");
                attempt.Validation=validate(); attempt.Success=attempt.Validation.Success;
                if(!attempt.Success) {attempt.Error=attempt.Validation.FailureClass;attempt.ErrorCode=attempt.Validation.ErrorCode;}
            }
            catch(Exception error) { attempt.Error=error.Message;Program.PhaseFailure known=error as Program.PhaseFailure;attempt.ErrorCode=known==null?"RECOVERY_DIRECT_API_FAILED":known.Code; }
            return attempt;
        }

        internal static void RepairEntities(IModelDoc2 model,CadState state,Dictionary<string,double> expected,List<RecoveryAttempt> attempts)
        {
            ReferenceSnapshot before=ReferenceAudit.Observe(model,state);
            if(before.Invalid==0) return;
            // No fallback for feature or sketch identities; model/parameters must already be healthy.
            CadValidator.Read(model,state,expected,false,false).Require();
            CadState projected=CanonicalValues.Project(state,expected);
            List<SemanticEntityReference> changed=new List<SemanticEntityReference>();
            List<string[]> replacements=new List<string[]>();
            foreach(SemanticEntityReference entity in state.SemanticEntities)
            {
                List<ReferenceObservation> observations=before.Items.FindAll(delegate(ReferenceObservation r){return r.SemanticId==entity.SemanticId;});
                if(!observations.Exists(delegate(ReferenceObservation r){return !r.Valid;})) continue;
                RecoveryAttempt attempt=new RecoveryAttempt { RecoveryClass="R1",SemanticId=entity.SemanticId,
                    Action="bounded canonical geometry resolution",BeforeReferences=(string[])entity.PersistentReferences.Clone() };
                attempts.Add(attempt);
                try
                {
                    if(!CanonicalEntityResolver.Supports(entity.SemanticId)) throw new Program.PhaseFailure("RECOVERY_REFERENCE_UNSUPPORTED","No geometry fallback for feature or SketchPoint identity.");
                    string[] candidates=CanonicalEntityResolver.CandidateReferences(model,projected,entity);
                    HashSet<string> remaining=new HashSet<string>(candidates,StringComparer.Ordinal);
                    string[] repaired=(string[])entity.PersistentReferences.Clone();
                    foreach(ReferenceObservation observation in observations)
                        if(observation.Valid && !remaining.Remove(repaired[observation.Index]))
                            throw new Program.PhaseFailure("SEMANTIC_REFERENCE_MISMATCH","A valid reference has the wrong semantic geometry.");
                    List<string> available=new List<string>(remaining); available.Sort(StringComparer.Ordinal);
                    int next=0;
                    foreach(ReferenceObservation observation in observations) if(!observation.Valid) repaired[observation.Index]=available[next++];
                    if(next!=available.Count) throw new Program.PhaseFailure("RECOVERY_REFERENCE_AMBIGUOUS","Candidate count is ambiguous.");
                    attempt.AfterReferences=repaired; changed.Add(entity); replacements.Add(repaired);
                }
                catch(Exception error) { attempt.Error=error.Message; throw; }
            }
            // Install only after every group has a unique complete replacement. Disk remains unchanged.
            for(int i=0;i<changed.Count;i++) changed[i].PersistentReferences=replacements[i];
            foreach(RecoveryAttempt attempt in attempts) if(attempt.RecoveryClass=="R1" && attempt.AfterReferences!=null) attempt.Success=true;
        }
    }
}
