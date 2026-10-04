using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;

namespace CadHarness.Cli
{
    public sealed class ParameterEditEvidence
    {
        public string EditId { get; set; }
        public int ProcessId { get; set; }
        public string StartedUtc { get; set; }
        public string CompletedUtc { get; set; }
        public string DocumentId { get; set; }
        public string StatePath { get; set; }
        public string EvidencePath { get; set; }
        public string ParameterSemanticId { get; set; }
        public string TargetSemanticId { get; set; }
        public double RequestedValueMm { get; set; }
        public double? OldValueMm { get; set; }
        public double? ActualValueMm { get; set; }
        public int? TargetFeatureId { get; set; }
        public int? DimensionOwnerFeatureId { get; set; }
        public string DimensionOwnerSemanticId { get; set; }
        public string LocalDimensionName { get; set; }
        public string StateHashBefore { get; set; }
        public string StateHashAfter { get; set; }
        public int RevisionBefore { get; set; }
        public int RevisionAfter { get; set; }
        public string Status { get; set; }
        public bool Success { get; set; }
        public bool MutationStarted { get; set; }
        public bool StateCommitted { get; set; }
        public bool? RebuildSucceeded { get; set; }
        public bool RollbackAttempted { get; set; }
        public bool RollbackSucceeded { get; set; }
        public string ErrorCode { get; set; }
        public string Error { get; set; }
        public string RollbackError { get; set; }
        public string EvidenceWriteError { get; set; }
        public ReferenceSnapshot Before { get; set; }
        public ReferenceSnapshot AfterAttempt { get; set; }
        public ReferenceSnapshot AfterRollback { get; set; }
        public EditConfirmationResult ConfirmationBefore { get; set; }
        public EditConfirmationResult ConfirmationAfter { get; set; }
        public EditConfirmationResult ConfirmationRollback { get; set; }
        public double[] SeedLocalCoordinatesBeforeMm { get; set; }
        public double[] SeedLocalCoordinatesAfterMm { get; set; }
        public string FailureClass { get; set; }
        public ValidationResult ValidationBefore { get; set; }
        public ValidationResult ValidationAfter { get; set; }
        public ValidationResult ValidationRollback { get; set; }
        public List<RecoveryAttempt> RecoveryAttempts { get; set; }
        public ReferenceSnapshot ReferencesAfterRecovery { get; set; }
        public double RebuildMs { get; set; }
        public BatchExecutionEvidence Batch { get; set; }
        public string BatchCleanupError { get; set; }
    }

    internal static class ParameterEditRunner
    {
        internal static ParameterEditEvidence Execute(ISldWorks app, IModelDoc2 model, ParameterEditIntent intent, string requestedPath)
        { return Execute(app,model,intent,requestedPath,new NativeParameterMutation()); }

        internal static ParameterEditEvidence Execute(ISldWorks app, IModelDoc2 model, ParameterEditIntent intent, string requestedPath, IParameterMutation mutation)
        {
            string documentId = DocumentIdentityStore.Read(model);
            if (requestedPath == null && documentId == null) throw new Program.PhaseFailure("DOCUMENT_NOT_MANAGED", "No Harness document identity exists.");
            string path = Path.GetFullPath(requestedPath ?? StateService.DefaultPath(documentId));
            // One controller writer per state path. Leave the empty lock file to avoid unlink races.
            using (FileStream editLock = new FileStream(path + ".edit.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                CadState state = StateSerializer.Load(path);
                DocumentIdentityStore.RequireMatch(model, state);
                ParameterEditEvidence evidence = new ParameterEditEvidence {
                    EditId = Guid.NewGuid().ToString("D"), ProcessId = Process.GetCurrentProcess().Id, StartedUtc = DateTime.UtcNow.ToString("o"),
                    DocumentId = state.Document.Id, StatePath = path, ParameterSemanticId = intent.ParameterSemanticId, TargetSemanticId = intent.TargetSemanticId,
                    RequestedValueMm = intent.ValueMm, StateHashBefore = DiskHash(path), RevisionBefore = state.EditRevision, RevisionAfter = state.EditRevision, Status = "Started",
                    RecoveryAttempts = new List<RecoveryAttempt>()
                };
                evidence.EvidencePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "edits", evidence.EditId + ".json"));
                EditContext context = new EditContext(model, state);
                CadState auditState=CanonicalValues.Project(state,CanonicalValues.From(state));
                Dictionary<string, double> original = null;
                double[] centerBefore = null;
                SolidWorksBatchScope batch=null;
                evidence.Before = ReferenceAudit.Observe(model, state);
                SaveEvidence(evidence); // No mutation unless the initial audit can be recorded.
                try
                {
                    if (model.GetActiveSketch2() != null || model.Extension.NeedsRebuild2 != 0)
                        throw new Program.PhaseFailure("EDIT_MODEL_NOT_READY", "Finish sketch editing and rebuild before editing parameters.");
                    original = StateValues(state);
                    LocalRecovery.RepairEntities(model,state,original,evidence.RecoveryAttempts);
                    evidence.ValidationBefore=CadValidator.Read(model,state,original,true,true);
                    evidence.ValidationBefore.Require();
                    evidence.TargetFeatureId = context.Feature(intent.TargetSemanticId).GetID();
                    DimensionBinding binding = context.Binding(intent.ParameterSemanticId);
                    evidence.DimensionOwnerSemanticId = binding.OwnerObjectSemanticId;
                    evidence.LocalDimensionName = binding.LocalDimensionName;
                    evidence.DimensionOwnerFeatureId = context.Feature(binding.OwnerObjectSemanticId).GetID();
                    original = StateValues(state);
                    evidence.ConfirmationBefore = EditConfirmation.Read(context);
                    EditConfirmation.Require(evidence.ConfirmationBefore, original, state);
                    foreach (DimensionBinding entry in state.DimensionBindings)
                        if (!EditConfirmation.Near(entry.CurrentValueMm ?? entry.ValueMmAtCapture, original[entry.ParameterSemanticId]))
                            throw new Program.PhaseFailure("EDIT_STATE_VALUE_MISMATCH", "Dimension binding and typed state disagree.");
                    evidence.OldValueMm = original[intent.ParameterSemanticId];
                    if (intent.Kind == ParameterEditKind.HoleSpacing)
                    {
                        ISketchPoint center = context.SeedCenter();
                        centerBefore = new double[] { center.X, center.Y, center.Z };
                        evidence.SeedLocalCoordinatesBeforeMm = ToMm(centerBefore);
                    }
                    batch=new SolidWorksBatchScope(app,model);
                    evidence.Batch=batch.Evidence;
                    evidence.MutationStarted = true;
                    bool initialSetFailed=false;
                    try { mutation.Set(context,intent); }
                    catch (Program.PhaseFailure setFailure)
                    {
                        if(setFailure.Code!="EDIT_DIMENSION_SET_FAILED") throw;
                        initialSetFailed=true;
                    }
                    if (intent.Kind == ParameterEditKind.HoleSpacing)
                    {
                        ISketch sketch = (ISketch)context.Feature("mounting_hole_seed.profile_sketch").GetSpecificFeature2();
                        IMathPoint point = (IMathPoint)((IMathUtility)app.GetMathUtility()).CreatePoint(new double[] {
                            Program.Mm(-intent.ValueMm/2), 0, Program.Mm(original["base_plate.thickness"]) });
                        point = (IMathPoint)point.MultiplyTransform(sketch.ModelToSketchTransform);
                        context.MoveFixedSeedCenter((double[])point.ArrayData);
                        ISketchPoint center = context.SeedCenter();
                        evidence.SeedLocalCoordinatesAfterMm = ToMm(new double[] { center.X, center.Y, center.Z });
                    }
                    Stopwatch rebuildClock=Stopwatch.StartNew();
                    evidence.RebuildSucceeded = SolidWorksBatchScope.Rebuild(model);
                    rebuildClock.Stop(); evidence.RebuildMs+=rebuildClock.Elapsed.TotalMilliseconds;
                    evidence.AfterAttempt = ReferenceAudit.Observe(model, auditState);
                    evidence.ActualValueMm = context.Read(intent.ParameterSemanticId);
                    evidence.ConfirmationAfter = EditConfirmation.Read(context);
                    if (evidence.RebuildSucceeded != true) throw new Program.PhaseFailure("EDIT_REBUILD_FAILED", "EditRebuild3 returned false.");
                    Dictionary<string, double> expected = new Dictionary<string, double>(original);
                    expected[intent.ParameterSemanticId] = intent.ValueMm;
                    evidence.ValidationAfter=CadValidator.Read(model,state,expected,false,false);
                    if((initialSetFailed || !CadValidator.Near(evidence.ActualValueMm.Value,intent.ValueMm)) &&
                        evidence.ValidationAfter.FailureClass!="FEATURE_REBUILD_FAILED")
                    {
                        RecoveryAttempt retry=LocalRecovery.RetryParameter(intent.ParameterSemanticId,
                            delegate { mutation.Set(new EditContext(model,state),intent); },
                            delegate { Stopwatch timer=Stopwatch.StartNew(); bool ok=SolidWorksBatchScope.Rebuild(model); timer.Stop(); evidence.RebuildMs+=timer.Elapsed.TotalMilliseconds; evidence.RebuildSucceeded=ok; return ok; },
                            delegate { return CadValidator.Read(model,state,expected,false,false); });
                        evidence.RecoveryAttempts.Add(retry);
                        if(!retry.Success) throw new Program.PhaseFailure(retry.ErrorCode=="EDIT_REBUILD_FAILED"?retry.ErrorCode:"EDIT_PARAMETER_NOT_APPLIED","One bounded parameter recovery failed: "+retry.Error);
                        evidence.ValidationAfter=retry.Validation;
                        evidence.ActualValueMm=context.Read(intent.ParameterSemanticId);
                        evidence.ConfirmationAfter=EditConfirmation.Read(context);
                    }
                    evidence.ValidationAfter.Require();
                    LocalRecovery.RepairEntities(model,state,expected,evidence.RecoveryAttempts);
                    evidence.ValidationAfter=CadValidator.Read(model,state,expected,false,true);
                    evidence.ValidationAfter.Require();
                    EditConfirmation.Require(evidence.ConfirmationAfter, expected, state);
                    batch.Dispose(); // Restoration failures take the existing rollback path before commit.
                    UpdateValues(state, evidence.ConfirmationAfter);
                    state.EditRevision++;
                    state.UpdatedUtc = DateTime.UtcNow.ToString("o"); state.LastEditId = evidence.EditId;
                    evidence.ReferencesAfterRecovery=ReferenceAudit.Observe(model,state);
                    state.ReferenceHealth = evidence.ReferencesAfterRecovery;
                    evidence.Status = "VerifiedPendingCommit";
                    SaveEvidence(evidence); // Full before/after audit exists before atomic state replacement.
                    if (DiskHash(path) != evidence.StateHashBefore)
                        throw new Program.PhaseFailure("EDIT_STATE_CHANGED_EXTERNALLY", "The state file changed during this edit.");
                    StateSerializer.Save(path, state);
                    evidence.StateCommitted = true; evidence.Success = true; evidence.Status = "Committed";
                    evidence.RevisionAfter = state.EditRevision;
                }
                catch (Exception error)
                {
                    Program.PhaseFailure known = error as Program.PhaseFailure;
                    evidence.ErrorCode = known != null ? known.Code : (error is IOException || error is UnauthorizedAccessException ? "EDIT_STATE_IO_FAILED" : "EDIT_DIRECT_API_FAILED");
                    evidence.Error = error.Message;
                    evidence.FailureClass=FailureClassifier.Classify(evidence.ErrorCode,intent);
                    if(evidence.ErrorCode=="VALIDATION_FAILED")
                    {
                        ValidationResult failed=evidence.MutationStarted?evidence.ValidationAfter:evidence.ValidationBefore;
                        if(failed!=null) evidence.FailureClass=failed.FailureClass;
                        if(intent.Kind==ParameterEditKind.FilletRadius && (evidence.FailureClass=="FEATURE_REBUILD_FAILED" || evidence.FailureClass=="GEOMETRY_INVALID")) evidence.FailureClass="GEOMETRY_IMPOSSIBLE";
                    }
                    if (evidence.AfterAttempt == null) evidence.AfterAttempt = ReferenceAudit.Observe(model, auditState);
                    try { evidence.ActualValueMm = context.Read(intent.ParameterSemanticId); } catch (Exception readError) { evidence.Error += " Readback: " + readError.Message; }
                    if (evidence.MutationStarted && !evidence.StateCommitted)
                    {
                        evidence.RollbackAttempted = true;
                        try
                        {
                            EditContext.Set(context.Dimension(intent.ParameterSemanticId), original[intent.ParameterSemanticId]);
                            if (centerBefore != null)
                            {
                                ISketchPoint center = context.SeedCenter();
                                if (Math.Abs(center.X-centerBefore[0]) > 1e-9 || Math.Abs(center.Y-centerBefore[1]) > 1e-9 || Math.Abs(center.Z-centerBefore[2]) > 1e-9)
                                    context.MoveFixedSeedCenter(centerBefore);
                            }
                            Stopwatch rollbackClock=Stopwatch.StartNew();
                            bool rollbackRebuilt=SolidWorksBatchScope.Rebuild(model);
                            rollbackClock.Stop();evidence.RebuildMs+=rollbackClock.Elapsed.TotalMilliseconds;
                            if (!rollbackRebuilt) throw new Program.PhaseFailure("EDIT_ROLLBACK_REBUILD_FAILED", "Rollback rebuild returned false.");
                            evidence.ConfirmationRollback = EditConfirmation.Read(context);
                            EditConfirmation.Require(evidence.ConfirmationRollback, original, state);
                            evidence.ValidationRollback=CadValidator.Read(model,state,original,false,false);
                            evidence.ValidationRollback.Require();
                            evidence.RollbackSucceeded = true;
                        }
                        catch (Exception rollbackError) { evidence.RollbackError = rollbackError.Message; }
                        finally { evidence.AfterRollback = ReferenceAudit.Observe(model, auditState); evidence.ReferencesAfterRecovery=ReferenceAudit.Observe(model,state); }
                        evidence.RecoveryAttempts.Add(new RecoveryAttempt {RecoveryClass=evidence.FailureClass=="GEOMETRY_IMPOSSIBLE"?"R4":"R3",
                            Action="restore original parameter and seed center, rebuild, validate; preserve disk state",SemanticId=intent.ParameterSemanticId,
                            Success=evidence.RollbackSucceeded,Error=evidence.RollbackError,Validation=evidence.ValidationRollback});
                    }
                    evidence.Status = evidence.RollbackAttempted ? (evidence.RollbackSucceeded ? "FailedRolledBack" : "FailedRollbackIncomplete") : "RejectedBeforeMutation";
                }
                finally
                {
                    try {if(batch!=null)batch.Dispose();}
                    catch(Exception cleanupError) {evidence.BatchCleanupError=cleanupError.Message;evidence.Error+=" Batch cleanup: "+cleanupError.Message;}
                }
                evidence.CompletedUtc = DateTime.UtcNow.ToString("o");
                try { evidence.StateHashAfter = DiskHash(path); } catch (Exception error) { evidence.EvidenceWriteError = error.Message; }
                // Logging failure after commit does not undo a successfully committed CAD edit.
                try { SaveEvidence(evidence); } catch (Exception error) { evidence.EvidenceWriteError = error.Message; }
                return evidence;
            }
        }

        private static Dictionary<string, double> StateValues(CadState state)
        {
            return new Dictionary<string, double> {
                { "base_plate.width", state.Features.BasePlate.Parameters.WidthMm }, { "base_plate.height", state.Features.BasePlate.Parameters.HeightMm },
                { "base_plate.thickness", state.Features.BasePlate.Parameters.ThicknessMm }, { "mounting_hole_seed.diameter", state.Features.MountingHoleSeed.Parameters.DiameterMm },
                { "mounting_holes.spacing", state.Features.MountingHoles.Parameters.SpacingMm }, { "outer_edge_fillet.radius", state.Features.OuterEdgeFillet.Parameters.RadiusMm }
            };
        }
        private static void UpdateValues(CadState state, EditConfirmationResult confirmation)
        {
            Dictionary<string, double> values = confirmation.DimensionsMm;
            state.Features.BasePlate.Parameters.ThicknessMm = values["base_plate.thickness"];
            state.Features.MountingHoleSeed.Parameters.DiameterMm = values["mounting_hole_seed.diameter"];
            state.Features.MountingHoleSeed.Parameters.CenterXmm = confirmation.Holes[0].AxisPointMm[0];
            state.Features.MountingHoleSeed.Parameters.CenterYmm = confirmation.Holes[0].AxisPointMm[1];
            state.Features.MountingHoles.Parameters.SpacingMm = values["mounting_holes.spacing"];
            state.Features.OuterEdgeFillet.Parameters.RadiusMm = values["outer_edge_fillet.radius"];
            foreach (DimensionBinding binding in state.DimensionBindings) binding.CurrentValueMm = values[binding.ParameterSemanticId];
        }
        private static string DiskHash(string path) { return ReferenceAudit.Hash(File.ReadAllText(path, Encoding.UTF8)); }
        private static double[] ToMm(double[] coordinates) { return new double[] { coordinates[0]*1000, coordinates[1]*1000, coordinates[2]*1000 }; }
        private static void SaveEvidence(ParameterEditEvidence evidence)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(evidence.EvidencePath));
            string temporary = evidence.EvidencePath + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(evidence), new UTF8Encoding(false));
                if (File.Exists(evidence.EvidencePath)) File.Replace(temporary, evidence.EvidencePath, null);
                else File.Move(temporary, evidence.EvidencePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
