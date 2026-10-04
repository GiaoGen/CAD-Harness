using System;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    internal static class DocumentIdentityStore
    {
        internal const string PropertyName = "CAD_HARNESS_DOCUMENT_ID";

        internal static string Read(IModelDoc2 model)
        {
            string raw, resolved;
            bool wasResolved, linked;
            int result = model.Extension.get_CustomPropertyManager("").Get6(PropertyName, false, out raw, out resolved, out wasResolved, out linked);
            if (result == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent) return null;
            Guid id;
            if (linked || !Guid.TryParse(raw, out id))
                throw new Program.PhaseFailure("DOCUMENT_ID_INVALID", "The Harness document identity property is not a literal GUID.");
            return id.ToString("D");
        }

        internal static void Add(IModelDoc2 model, string id)
        {
            int result = model.Extension.get_CustomPropertyManager("").Add3(PropertyName, (int)swCustomInfoType_e.swCustomInfoText,
                id, (int)swCustomPropertyAddOption_e.swCustomPropertyOnlyIfNew);
            if (result != (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged || Read(model) != id)
                throw new Program.PhaseFailure("DOCUMENT_ID_WRITE_FAILED", "Could not attach the Harness identity to the document.");
        }

        internal static void RemoveNewIdentity(IModelDoc2 model, string id)
        {
            if (Read(model) == id) model.Extension.get_CustomPropertyManager("").Delete2(PropertyName);
        }

        internal static void RequireMatch(IModelDoc2 model, CadState state)
        {
            if (Read(model) != state.Document.Id)
                throw new Program.PhaseFailure("DOCUMENT_ID_MISMATCH", "Active Part is not the document owned by this CADState.");
            string configuration = ((IConfiguration)model.GetActiveConfiguration()).Name;
            if (configuration != state.Document.Configuration)
                throw new Program.PhaseFailure("CONFIGURATION_MISMATCH", "Active configuration differs from the saved CADState.");
            if (!String.IsNullOrEmpty(state.Document.Path) &&
                !String.Equals(state.Document.Path, model.GetPathName(), StringComparison.OrdinalIgnoreCase))
                throw new Program.PhaseFailure("DOCUMENT_PATH_MISMATCH", "Active document path differs from the saved CADState.");
        }
    }
}
