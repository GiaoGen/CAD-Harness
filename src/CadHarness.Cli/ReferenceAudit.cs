using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using SolidWorks.Interop.sldworks;

namespace CadHarness.Cli
{
    public sealed class ReferenceObservation
    {
        public string SemanticId { get; set; }
        public string Kind { get; set; }
        public int Index { get; set; }
        public string ReferenceSha256 { get; set; }
        public int? NativeStatus { get; set; }
        public string NativeStatusName { get; set; }
        public bool ObjectReturned { get; set; }
        public bool KindMatches { get; set; }
        public bool Valid { get; set; }
        public string ActualKind { get; set; }
        public int? NativeFeatureId { get; set; }
        public string Error { get; set; }
        public string Hresult { get; set; }
    }

    public sealed class ReferenceSnapshot
    {
        public string ObservedUtc { get; set; }
        public int Total { get; set; }
        public int Valid { get; set; }
        public int Invalid { get; set; }
        public List<ReferenceObservation> Items { get; set; }
    }

    internal static class ReferenceAudit
    {
        // Observe EVERY old reference independently. Invalid geometry references never cause a fallback.
        internal static ReferenceSnapshot Observe(IModelDoc2 model, CadState state)
        {
            ReferenceSnapshot result = new ReferenceSnapshot { ObservedUtc = DateTime.UtcNow.ToString("o"), Items = new List<ReferenceObservation>() };
            foreach (SemanticFeature feature in state.Features.All())
                result.Items.Add(ObserveOne(model, feature.SemanticId, "feature", 0, feature.PersistentReference));
            foreach (SemanticEntityReference entity in state.SemanticEntities)
                for (int i = 0; i < entity.PersistentReferences.Length; i++)
                    result.Items.Add(ObserveOne(model, entity.SemanticId, entity.Kind, i, entity.PersistentReferences[i]));
            result.Total = result.Items.Count;
            foreach (ReferenceObservation item in result.Items) if (item.Valid) result.Valid++;
            result.Invalid = result.Total - result.Valid;
            return result;
        }

        private static ReferenceObservation ObserveOne(IModelDoc2 model, string id, string kind, int index, string reference)
        {
            ReferenceObservation observation = new ReferenceObservation { SemanticId = id, Kind = kind, Index = index, ReferenceSha256 = Hash(reference) };
            try
            {
                int status;
                object value = model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(reference), out status);
                observation.NativeStatus = status;
                observation.NativeStatusName = status == 0 ? "Ok" : (status == 1 ? "Invalid" : (status == 2 ? "Suppressed" : (status == 4 ? "Deleted" : "Other")));
                observation.ObjectReturned = value != null;
                observation.ActualKind = value is IFeature ? "Feature" : (value is IFace2 ? "Face" : (value is IEdge ? "Edge" : (value is ISketchPoint ? "SketchPoint" : (value == null ? null : "Other"))));
                observation.KindMatches = (kind == "feature" && value is IFeature) ||
                    ((kind == "face" || kind == "face_set") && value is IFace2) ||
                    ((kind == "edge" || kind == "edge_set") && value is IEdge) || (kind == "sketch_point" && value is ISketchPoint);
                observation.Valid = status == 0 && observation.ObjectReturned && observation.KindMatches;
                if (value is IFeature) observation.NativeFeatureId = ((IFeature)value).GetID();
            }
            catch (Exception error) { observation.Error = error.Message; observation.Hresult = "0x" + error.HResult.ToString("X8"); observation.NativeStatusName = "Exception"; }
            return observation;
        }

        internal static string Hash(string text)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
    }
}
