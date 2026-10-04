using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    internal sealed class EditContext
    {
        internal readonly IModelDoc2 Model;
        internal readonly CadState State;
        internal EditContext(IModelDoc2 model, CadState state) { Model = model; State = state; }

        internal IFeature Feature(string semanticId)
        {
            foreach (SemanticFeature feature in State.Features.All())
                if (feature.SemanticId == semanticId)
                    return (IFeature)PersistentReferences.Resolve(Model, feature.PersistentReference, "feature", semanticId);
            SemanticEntityReference entity = State.SemanticEntities.Find(delegate(SemanticEntityReference entry) { return entry.SemanticId == semanticId; });
            if (entity == null || entity.Kind != "feature" || entity.PersistentReferences.Length != 1)
                throw new Program.PhaseFailure("EDIT_FEATURE_ID_UNKNOWN", "No stable feature identity for " + semanticId + ".");
            return (IFeature)PersistentReferences.Resolve(Model, entity.PersistentReferences[0], "feature", semanticId);
        }

        internal DimensionBinding Binding(string parameterId)
        {
            DimensionBinding binding = State.DimensionBindings.Find(delegate(DimensionBinding entry) { return entry.ParameterSemanticId == parameterId; });
            if (binding == null) throw new Program.PhaseFailure("EDIT_BINDING_NOT_FOUND", "No dimension binding for " + parameterId + ".");
            return binding;
        }

        internal IDimension Dimension(string parameterId)
        {
            DimensionBinding binding = Binding(parameterId);
            IDimension dimension = Feature(binding.OwnerObjectSemanticId).Parameter(binding.LocalDimensionName) as IDimension;
            if (dimension == null) throw new Program.PhaseFailure("EDIT_DIMENSION_UNRESOLVED", "The bound local dimension cannot be resolved: " + parameterId);
            return dimension;
        }

        internal double Read(string parameterId)
        {
            DimensionBinding binding = Binding(parameterId);
            return StateCapture.ReadDimension(Feature(binding.OwnerObjectSemanticId), binding.LocalDimensionName);
        }

        internal Dictionary<string, double> ReadAll()
        {
            Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (DimensionBinding binding in State.DimensionBindings) values.Add(binding.ParameterSemanticId, Read(binding.ParameterSemanticId));
            return values;
        }

        internal ISketchPoint SeedCenter()
        {
            SemanticEntityReference entity = State.SemanticEntities.Find(delegate(SemanticEntityReference entry) { return entry.SemanticId == "mounting_hole_seed.center_point"; });
            return (ISketchPoint)PersistentReferences.Resolve(Model, entity.PersistentReferences[0], "sketch_point", entity.SemanticId);
        }

        internal static void Set(IDimension dimension, double valueMm)
        {
            int status = BenchmarkTelemetry.Call("IDimension.SetSystemValue3",delegate{return dimension.SetSystemValue3(Program.Mm(valueMm), (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);});
            if (status != (int)swSetValueReturnStatus_e.swSetValue_Successful)
                throw new Program.PhaseFailure("EDIT_DIMENSION_SET_FAILED", "SetSystemValue3 returned status " + status + ".");
        }

        internal void MoveFixedSeedCenter(double[] coordinates)
        {
            IFeature profile = Feature("mounting_hole_seed.profile_sketch");
            ISketchPoint center = SeedCenter();
            double[] old = new double[] { center.X, center.Y, center.Z };
            ISketch sketch = (ISketch)profile.GetSpecificFeature2();
            ISketchRelationManager relations = sketch.RelationManager;
            List<ISketchRelation> fixedRelations = new List<ISketchRelation>();
            object[] existing = center.GetRelations() as object[];
            if (existing != null)
                foreach (ISketchRelation relation in existing)
                    if (relation.GetRelationType() == (int)swConstraintType_e.swConstraintType_FIXED) fixedRelations.Add(relation);
            if (fixedRelations.Count != 1)
                throw new Program.PhaseFailure("EDIT_CENTER_CONSTRAINT_UNSUPPORTED", "Expected one existing fixed-point relation on the persisted seed center.");
            bool opened = false, removed = false, fixedAgain = false;
            try
            {
                Model.ClearSelection2(true);
                if (!profile.Select2(false, 0)) throw new Program.PhaseFailure("EDIT_SKETCH_SELECTION_FAILED", "Could not select the persisted seed sketch.");
                BenchmarkTelemetry.Call("ISketchManager.InsertSketch",delegate{Model.SketchManager.InsertSketch(true);});
                opened = Model.GetActiveSketch2() != null;
                if (!opened || ((IFeature)Model.GetActiveSketch2()).GetID() != profile.GetID())
                    throw new Program.PhaseFailure("EDIT_SKETCH_ACTIVATION_FAILED", "The persisted seed sketch was not activated.");
                removed = BenchmarkTelemetry.Call("ISketchRelationManager.DeleteRelation",delegate{return relations.DeleteRelation((SketchRelation)fixedRelations[0]);});
                if (!removed) throw new Program.PhaseFailure("EDIT_CENTER_UNFIX_FAILED", "Could not temporarily release the seed point's fixed relation.");
                if (!BenchmarkTelemetry.Call("ISketchPoint.SetCoords",delegate{return center.SetCoords(coordinates[0], coordinates[1], coordinates[2]);}))
                    throw new Program.PhaseFailure("EDIT_CENTER_MOVE_FAILED", "SetCoords did not move the persisted seed center.");
                FixCenter(center);
                fixedAgain = true;
            }
            catch
            {
                if (removed && !fixedAgain)
                {
                    BenchmarkTelemetry.Call("ISketchPoint.SetCoords",delegate{return center.SetCoords(old[0], old[1], old[2]);});
                    FixCenter(center);
                }
                throw;
            }
            finally
            {
                try { if (opened && Model.GetActiveSketch2() != null) BenchmarkTelemetry.Call("ISketchManager.InsertSketch",delegate{Model.SketchManager.InsertSketch(true);}); }
                finally { Model.ClearSelection2(true); }
            }
        }

        private void FixCenter(ISketchPoint center)
        {
            Model.ClearSelection2(true);
            if (!center.Select4(false, null)) throw new Program.PhaseFailure("EDIT_CENTER_SELECTION_FAILED", "Could not select the persisted center for its fixed relation.");
            BenchmarkTelemetry.Call("IModelDoc2.SketchAddConstraints",delegate{Model.SketchAddConstraints("sgFIXED");});
            object[] relations = center.GetRelations() as object[];
            int fixedCount = 0;
            if (relations != null) foreach (ISketchRelation relation in relations)
                if (relation.GetRelationType() == (int)swConstraintType_e.swConstraintType_FIXED) fixedCount++;
            if (fixedCount != 1) throw new Program.PhaseFailure("EDIT_CENTER_FIX_FAILED", "The center's fixed-point relation was not restored.");
        }
    }
}
