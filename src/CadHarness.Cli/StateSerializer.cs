using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CadHarness.Cli
{
    internal static class StateSerializer
    {
        internal const string Version = "0.1";

        internal static CadState Load(string path)
        {
            if (!File.Exists(path)) throw new Program.PhaseFailure("STATE_NOT_FOUND", "CADState file does not exist: " + path);
            CadState state;
            try { state = new JavaScriptSerializer().Deserialize<CadState>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (ArgumentException error) { throw new Program.PhaseFailure("STATE_SCHEMA_INVALID", error.Message); }
            RequireSchema(state);
            return state;
        }

        internal static void Save(string path, CadState state)
        {
            RequireSchema(state);
            path = Path.GetFullPath(path);
            if (File.Exists(path))
            {
                CadState existing = Load(path);
                if (existing.Document.Id != state.Document.Id || existing.Document.Configuration != state.Document.Configuration)
                    throw new Program.PhaseFailure("STATE_FILE_OWNERSHIP_MISMATCH", "The destination state file belongs to another document or configuration.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(state), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void RequireSchema(CadState state)
        {
            if (state == null || state.SchemaVersion != Version)
                throw new Program.PhaseFailure("STATE_VERSION_UNSUPPORTED", "CADState schema version must be " + Version + ".");
            Guid documentId;
            if (state.Units != "mm" || state.Document == null || !Guid.TryParse(state.Document.Id, out documentId) ||
                state.Document.IdentityProperty != DocumentIdentityStore.PropertyName || String.IsNullOrEmpty(state.Document.Configuration) ||
                state.Features == null || state.Features.BasePlate == null || state.Features.MountingHoleSeed == null ||
                state.Features.MountingHoles == null || state.Features.OuterEdgeFillet == null ||
                state.Features.BasePlate.Parameters == null || state.Features.MountingHoleSeed.Parameters == null ||
                state.Features.MountingHoles.Parameters == null || state.Features.OuterEdgeFillet.Parameters == null ||
                state.Dependencies == null || state.SemanticEntities == null || state.DimensionBindings == null)
                throw new Program.PhaseFailure("STATE_SCHEMA_INVALID", "Required canonical CADState fields are missing.");
            string[] required = { "base_plate", "mounting_hole_seed", "mounting_holes", "outer_edge_fillet" };
            SemanticFeature[] features = state.Features.All();
            HashSet<string> identities = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < features.Length; i++)
            {
                SemanticFeature feature = features[i];
                if (feature.SemanticId != required[i] || feature.Dependencies == null)
                    throw new Program.PhaseFailure("STATE_SCHEMA_INVALID", "Canonical feature identity is invalid.");
                identities.Add(feature.SemanticId);
                RequireReference(feature.PersistentReference);
            }
            foreach (SemanticFeature feature in features)
                foreach (string dependency in feature.Dependencies)
                    if (!identities.Contains(dependency)) throw new Program.PhaseFailure("STATE_SCHEMA_INVALID", "Unknown feature dependency.");
            foreach (DesignDependency dependency in state.Dependencies)
                if (dependency == null || !identities.Contains(dependency.ParentSemanticId) || !identities.Contains(dependency.ChildSemanticId))
                    throw new Program.PhaseFailure("STATE_SCHEMA_INVALID", "Invalid design dependency.");
            foreach (SemanticEntityReference entity in state.SemanticEntities)
            {
                if (entity == null || String.IsNullOrEmpty(entity.SemanticId) || !identities.Contains(entity.OwnerSemanticId) ||
                    !identities.Add(entity.SemanticId) || entity.PersistentReferences == null || entity.PersistentReferences.Length == 0 ||
                    (entity.Kind != "feature" && entity.Kind != "face" && entity.Kind != "edge" && entity.Kind != "face_set" && entity.Kind != "edge_set" && entity.Kind != "sketch_point") ||
                    ((entity.Kind == "feature" || entity.Kind == "face" || entity.Kind == "edge" || entity.Kind == "sketch_point") && entity.PersistentReferences.Length != 1))
                    throw new Program.PhaseFailure("STATE_SCHEMA_INVALID", "Invalid semantic entity reference.");
                foreach (string reference in entity.PersistentReferences) RequireReference(reference);
            }
            foreach (DimensionBinding binding in state.DimensionBindings)
                if (binding == null || String.IsNullOrEmpty(binding.ParameterSemanticId) || !identities.Contains(binding.OwnerObjectSemanticId) ||
                    String.IsNullOrEmpty(binding.LocalDimensionName) || binding.AccessMethod != "feature.Parameter")
                    throw new Program.PhaseFailure("STATE_SCHEMA_INVALID", "Invalid dimension binding.");

            // Fixed Phase 3 manifest, not a general model/geometry validator.
            string[] entityIds = { "base_plate.profile_sketch", "mounting_hole_seed.profile_sketch", "mounting_hole_seed.center_point",
                "base_plate.top_face", "base_plate.bottom_face", "mounting_hole_seed.wall_face", "mounting_holes.instance_2.wall_face",
                "outer_edge_fillet.rounded_faces", "mounting_holes.direction_edge", "outer_edge_fillet.vertical_tangent_edges" };
            string[] kinds = { "feature", "feature", "sketch_point", "face", "face", "face", "face", "face_set", "edge", "edge_set" };
            int[] counts = { 1, 1, 1, 1, 1, 1, 1, 4, 1, 8 };
            if (state.SemanticEntities.Count != entityIds.Length)
                throw new Program.PhaseFailure("STATE_SCHEMA_INVALID", "The canonical semantic entity manifest is incomplete.");
            for (int i = 0; i < entityIds.Length; i++)
            {
                SemanticEntityReference found = state.SemanticEntities.Find(delegate(SemanticEntityReference entity) { return entity.SemanticId == entityIds[i]; });
                if (found == null || found.Kind != kinds[i] || found.PersistentReferences.Length != counts[i])
                    throw new Program.PhaseFailure("STATE_SCHEMA_INVALID", "The required entity manifest entry is invalid: " + entityIds[i]);
            }
            string[] bindingIds = { "base_plate.width", "base_plate.height", "base_plate.thickness", "mounting_hole_seed.diameter", "mounting_holes.spacing", "outer_edge_fillet.radius" };
            HashSet<string> bindingIdentities = new HashSet<string>(StringComparer.Ordinal);
            foreach (DimensionBinding binding in state.DimensionBindings)
                if (!bindingIdentities.Add(binding.ParameterSemanticId)) throw new Program.PhaseFailure("STATE_SCHEMA_INVALID", "Duplicate dimension binding.");
            if (!bindingIdentities.SetEquals(bindingIds)) throw new Program.PhaseFailure("STATE_SCHEMA_INVALID", "The required dimension bindings are incomplete.");
        }

        private static void RequireReference(string value)
        {
            try { if (String.IsNullOrEmpty(value) || Convert.FromBase64String(value).Length == 0) throw new FormatException(); }
            catch (FormatException) { throw new Program.PhaseFailure("STATE_REFERENCE_INVALID", "A required persistent reference is empty or malformed."); }
        }
    }
}
