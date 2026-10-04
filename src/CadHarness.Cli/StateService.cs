using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SolidWorks.Interop.sldworks;

namespace CadHarness.Cli
{
    internal static class StateService
    {
        internal static object CaptureAndSave(ISldWorks app, IModelDoc2 model, string requestedPath)
        {
            CadState state = StateCapture.Capture(app, model);
            ValidationResult validation=CadValidator.Read(model,state,CanonicalValues.From(state),true,true);
            validation.Require(); // Never persist unvalidated creation/adoption state.
            string path = Path.GetFullPath(requestedPath ?? DefaultPath(state.Document.Id));
            bool identityAdded = DocumentIdentityStore.Read(model) == null;
            try
            {
                if (identityAdded) DocumentIdentityStore.Add(model, state.Document.Id);
                StateSerializer.Save(path, state);
            }
            catch
            {
                if (identityAdded) DocumentIdentityStore.RemoveNewIdentity(model, state.Document.Id);
                throw;
            }
            int entityCount = 0;
            foreach (SemanticEntityReference entity in state.SemanticEntities) entityCount += entity.PersistentReferences.Length;
            return new {
                success = true, phase = 3, operation = "capture-state", processId = Process.GetCurrentProcess().Id,
                statePath = path, documentId = state.Document.Id, title = model.GetTitle(), configuration = state.Document.Configuration,
                featureIdentities = 4, semanticEntityGroups = state.SemanticEntities.Count, entityReferences = entityCount,
                totalPersistentReferences = entityCount + 4, dimensionBindings = state.DimensionBindings.Count,
                geometryChanged = false, documentIdentityAdded = identityAdded, cadDocumentSaved = false, validation = validation
            };
        }

        internal static object LoadAndResolve(IModelDoc2 model, string requestedPath)
        {
            string id = DocumentIdentityStore.Read(model);
            if (requestedPath == null && id == null)
                throw new Program.PhaseFailure("DOCUMENT_NOT_MANAGED", "The active document has no Harness identity; capture the verified canonical model first.");
            string path = Path.GetFullPath(requestedPath ?? DefaultPath(id));
            CadState state = StateSerializer.Load(path);
            DocumentIdentityStore.RequireMatch(model, state);
            if (model.GetActiveSketch2() != null)
                throw new Program.PhaseFailure("MODEL_IS_EDITING_SKETCH", "Resolve state against the completed current model.");

            Dictionary<string, object> nativeObjects = new Dictionary<string, object>(StringComparer.Ordinal);
            List<object> featureResults = new List<object>(), entityResults = new List<object>(), bindingResults = new List<object>();
            int featureCount = 0, faceCount = 0, edgeCount = 0, sketchPointCount = 0;
            foreach (SemanticFeature feature in state.Features.All())
            {
                IFeature restored = (IFeature)PersistentReferences.Resolve(model, feature.PersistentReference, "feature", feature.SemanticId);
                nativeObjects.Add(feature.SemanticId, restored);
                featureResults.Add(new {
                    semanticId = feature.SemanticId, nativeStatus = 0, resolvedName = restored.Name,
                    capturedName = feature.SwFeatureNameAtCapture, resolvedType = restored.GetTypeName2(),
                    nativeFeatureId = restored.GetID(), capturedFeatureId = feature.SwFeatureIdAtCapture
                });
                featureCount++;
            }
            foreach (SemanticEntityReference entity in state.SemanticEntities)
            {
                List<object> items = new List<object>();
                for (int i = 0; i < entity.PersistentReferences.Length; i++)
                {
                    object restored = PersistentReferences.Resolve(model, entity.PersistentReferences[i], entity.Kind, entity.SemanticId + "[" + i + "]");
                    if (entity.PersistentReferences.Length == 1) nativeObjects.Add(entity.SemanticId, restored);
                    if (restored is IFeature) featureCount++;
                    else if (restored is IFace2) faceCount++;
                    else if (restored is IEdge) edgeCount++;
                    else if (restored is ISketchPoint) sketchPointCount++;
                    items.Add(Describe(restored));
                }
                entityResults.Add(new { semanticId = entity.SemanticId, kind = entity.Kind, owner = entity.OwnerSemanticId, nativeStatus = 0, restoredCount = items.Count, items = items });
            }
            foreach (DimensionBinding binding in state.DimensionBindings)
            {
                object ownerObject;
                if (!nativeObjects.TryGetValue(binding.OwnerObjectSemanticId, out ownerObject) || !(ownerObject is IFeature))
                    throw new Program.PhaseFailure("DIMENSION_OWNER_UNRESOLVED", "No restored feature owner for " + binding.ParameterSemanticId + ".");
                double value = StateCapture.ReadDimension((IFeature)ownerObject, binding.LocalDimensionName);
                bindingResults.Add(new { parameterSemanticId = binding.ParameterSemanticId,
                    ownerSemanticId = binding.OwnerObjectSemanticId, localDimension = binding.LocalDimensionName,
                    valueMm = value, capturedValueMm = binding.ValueMmAtCapture, accessMethod = binding.AccessMethod });
            }
            return new {
                success = true, phase = 3, operation = "resolve-state", processId = Process.GetCurrentProcess().Id,
                statePath = path, documentId = state.Document.Id, title = model.GetTitle(), configuration = state.Document.Configuration,
                resolvedFeatures = featureCount, resolvedFaces = faceCount, resolvedEdges = edgeCount, resolvedSketchPoints = sketchPointCount,
                resolvedPersistentReferences = featureCount + faceCount + edgeCount + sketchPointCount,
                resolvedDimensionBindings = bindingResults.Count, featureResults = featureResults, entityResults = entityResults,
                dimensionResults = bindingResults, nameFallbackUsed = false, geometryFallbackUsed = false, modelModified = false
            };
        }

        internal static string DefaultPath(string documentId)
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "artifacts", "state", documentId + ".json"));
        }

        private static object Describe(object restored)
        {
            IFeature feature = restored as IFeature;
            if (feature != null) return new { type = "Feature", name = feature.Name, nativeType = feature.GetTypeName2(), nativeFeatureId = feature.GetID() };
            IFace2 face = restored as IFace2;
            if (face != null)
            {
                ISurface surface = (ISurface)face.GetSurface();
                double[] cylinder = surface.IsCylinder() ? (double[])surface.CylinderParams : null;
                return new {
                    type = "Face", surface = surface.IsPlane() ? "plane" : (cylinder != null ? "cylinder" : "other"),
                    boxMm = ToMm((double[])face.GetBox()), radiusMm = cylinder == null ? (double?)null : cylinder[6] * 1000,
                    axisPointMm = cylinder == null ? null : new double[] { cylinder[0] * 1000, cylinder[1] * 1000, cylinder[2] * 1000 }
                };
            }
            IEdge edge = restored as IEdge;
            if (edge != null)
            {
                IVertex start = (IVertex)edge.GetStartVertex(), end = (IVertex)edge.GetEndVertex();
                return new { type = "Edge", isLine = ((ICurve)edge.GetCurve()).IsLine(),
                    startMm = start == null ? null : ToMm((double[])start.GetPoint()), endMm = end == null ? null : ToMm((double[])end.GetPoint()) };
            }
            ISketchPoint point = (ISketchPoint)restored;
            return new { type = "SketchPoint", sketchCoordinatesMm = new double[] { point.X * 1000, point.Y * 1000, point.Z * 1000 } };
        }

        private static double[] ToMm(double[] values)
        {
            double[] result = new double[values.Length];
            for (int i = 0; i < result.Length; i++) result[i] = values[i] * 1000;
            return result;
        }
    }
}
