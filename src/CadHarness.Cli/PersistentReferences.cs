using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    internal static class PersistentReferences
    {
        internal static string Capture(IModelDoc2 model, object nativeObject)
        {
            byte[] bytes = model.Extension.GetPersistReference3(nativeObject) as byte[];
            if (bytes == null || bytes.Length == 0)
                throw new Program.PhaseFailure("PERSISTENT_REFERENCE_UNAVAILABLE", "SOLIDWORKS did not provide a persistent reference for a required object.");
            return Convert.ToBase64String(bytes);
        }

        internal static object Resolve(IModelDoc2 model, string reference, string kind, string semanticId)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(reference); }
            catch (FormatException) { throw new Program.PhaseFailure("STATE_REFERENCE_INVALID", "Invalid Base64 reference: " + semanticId); }
            int status;
            object value = model.Extension.GetObjectByPersistReference3(bytes, out status);
            if (status != (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok || value == null)
                throw new Program.PhaseFailure("PERSISTENT_REFERENCE_UNRESOLVED", semanticId + ": native persistent-reference status " + status + ". No name or geometry fallback is used.");
            bool matches = (kind == "feature" && value is IFeature) ||
                ((kind == "face" || kind == "face_set") && value is IFace2) ||
                ((kind == "edge" || kind == "edge_set") && value is IEdge) ||
                (kind == "sketch_point" && value is ISketchPoint);
            if (!matches)
                throw new Program.PhaseFailure("REFERENCE_KIND_MISMATCH", "Restored object kind differs for " + semanticId + ".");
            return value;
        }
    }
}
