using System;
using System.Collections.Generic;
using System.Diagnostics;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    // Deliberately limited to the already supported XY/+Z canonical model.
    // Native measurements are shared with Phase 4; interpretation is data-only.
    internal static class CadValidator
    {
        internal static ValidationResult Read(IModelDoc2 model, CadState state, Dictionary<string,double> expected,
            bool compareBindings, bool checkEntityReferences)
        {
            Stopwatch clock=Stopwatch.StartNew();
            ValidationResult result=new ValidationResult();
            try
            {
                result.Add("document.active_sketch",false,model.GetActiveSketch2()!=null,model.GetActiveSketch2()==null,"MODEL_NOT_READY");
                EditContext context=new EditContext(model,state);
                Dictionary<string,string> types=new Dictionary<string,string>();
                foreach (SemanticFeature feature in state.Features.All())
                {
                    IFeature native=context.Feature(feature.SemanticId);
                    types.Add(feature.SemanticId,native.GetTypeName2());
                    HashSet<int> ancestors=new HashSet<int>(); CollectAncestors(native,ancestors);
                    foreach (string parent in feature.Dependencies)
                    {
                        bool preceding=state.Dependencies.Exists(delegate(DesignDependency d){return d.ParentSemanticId==parent && d.ChildSemanticId==feature.SemanticId && d.Kind=="preceding_body_feature";});
                        bool matches=preceding ? Position(model,context.Feature(parent).GetID())<Position(model,native.GetID()) : ancestors.Contains(context.Feature(parent).GetID());
                        result.Add("dependency.native."+parent+"->"+feature.SemanticId,preceding?"earlier in feature history":"ancestor",matches,matches,"FEATURE_STRUCTURE_INVALID");
                    }
                }
                types.Add("base_plate.profile_sketch",context.Feature("base_plate.profile_sketch").GetTypeName2());
                types.Add("mounting_hole_seed.profile_sketch",context.Feature("mounting_hole_seed.profile_sketch").GetTypeName2());
                ReferenceSnapshot references=ReferenceAudit.Observe(model,state);
                EditConfirmationResult measurements=EditConfirmation.Read(context);
                ValidationResult evaluated=Evaluate(state,measurements,types,references,expected,compareBindings,checkEntityReferences);
                foreach (ValidationCheck check in evaluated.Checks)
                    result.Add(check.Name,check.Expected,check.Actual,check.Status=="passed",check.FailureClass);
                result.Measurements=measurements; result.References=references;
                if (checkEntityReferences && evaluated.Success)
                {
                    CadState projected=CanonicalValues.Project(state,expected);
                    foreach (SemanticEntityReference entity in projected.SemanticEntities)
                        if (CanonicalEntityResolver.Supports(entity.SemanticId))
                        {
                            string[] actual=CanonicalEntityResolver.CandidateReferences(model,projected,entity);
                            bool same=new HashSet<string>(actual,StringComparer.Ordinal).SetEquals(entity.PersistentReferences);
                            result.Add("semantic_identity."+entity.SemanticId,actual,entity.PersistentReferences,same,"SEMANTIC_REFERENCE_MISMATCH");
                        }
                    ISketchPoint center=context.SeedCenter();
                    int fixedRelations=0;
                    object[] relations=center.GetRelations() as object[];
                    if(relations!=null) foreach(ISketchRelation relation in relations) if(relation.GetRelationType()==(int)swConstraintType_e.swConstraintType_FIXED) fixedRelations++;
                    result.Add("seed.center_constraint","one fixed relation",fixedRelations,fixedRelations==1 && state.Features.MountingHoleSeed.Parameters.CenterConstraint=="fixed_sketch_point","FEATURE_STRUCTURE_INVALID");
                    ISldWorks app=(ISldWorks)System.Runtime.InteropServices.Marshal.GetActiveObject("SldWorks.Application");
                    IMathPoint point=(IMathPoint)((IMathUtility)app.GetMathUtility()).CreatePoint(new double[] {center.X,center.Y,center.Z});
                    ISketch sketch=(ISketch)context.Feature("mounting_hole_seed.profile_sketch").GetSpecificFeature2();
                    point=(IMathPoint)point.MultiplyTransform(((IMathTransform)sketch.ModelToSketchTransform).Inverse());
                    double[] coordinates=(double[])point.ArrayData;
                    result.Add("semantic_identity.seed_center",new double[] {-expected["mounting_holes.spacing"]/2,0,expected["base_plate.thickness"]},
                        new double[] {coordinates[0]*1000,coordinates[1]*1000,coordinates[2]*1000},
                        Near(coordinates[0]*1000,-expected["mounting_holes.spacing"]/2) && Near(coordinates[1]*1000,0) && Near(coordinates[2]*1000,expected["base_plate.thickness"]),"SEMANTIC_REFERENCE_MISMATCH");
                }
            }
            catch (Exception error)
            {
                Program.PhaseFailure failure=error as Program.PhaseFailure;
                result.ErrorCode=failure==null ? "VALIDATION_READ_FAILED" : failure.Code;
                string classification=FailureClassifier.Classify(result.ErrorCode,null);
                result.Add("native.read","readable canonical model",error.Message,false,classification);
            }
            clock.Stop(); result.ValidationMs=clock.Elapsed.TotalMilliseconds;
            BenchmarkTelemetry.Validation(result.ValidationMs);
            if (!result.Success && result.ErrorCode==null) result.ErrorCode="VALIDATION_FAILED";
            return result;
        }

        private static void CollectAncestors(IFeature feature,HashSet<int> seen)
        {
            object[] parents=feature.GetParents() as object[];
            if (parents!=null) foreach (IFeature parent in parents) if (seen.Add(parent.GetID())) CollectAncestors(parent,seen);
        }
        private static int Position(IModelDoc2 model,int id)
        {
            int index=0;
            for(IFeature f=(IFeature)model.FirstFeature();f!=null;f=(IFeature)f.GetNextFeature(),index++) if(f.GetID()==id) return index;
            throw new Program.PhaseFailure("VALIDATION_FEATURE_HISTORY_INVALID","Managed feature absent from native history.");
        }

        internal static ValidationResult Evaluate(CadState state,EditConfirmationResult m,Dictionary<string,string> types,
            ReferenceSnapshot references,Dictionary<string,double> expected,bool compareBindings,bool checkEntityReferences)
        {
            ValidationResult r=new ValidationResult { Measurements=m,References=references };
            // Rebuild failures take priority over secondary missing-geometry symptoms.
            r.Add("document.needs_rebuild",0,m.NeedsRebuild,m.NeedsRebuild==0,"FEATURE_REBUILD_FAILED");
            foreach (KeyValuePair<string,int> error in m.FeatureErrors)
                r.Add("feature.error."+error.Key,0,error.Value,error.Value==0,"FEATURE_REBUILD_FAILED");
            foreach (SemanticFeature feature in state.Features.All())
            {
                string type; int id;
                bool exists=types.TryGetValue(feature.SemanticId,out type) && m.FeatureIds.TryGetValue(feature.SemanticId,out id);
                r.Add("feature.exists."+feature.SemanticId,true,exists,exists,"FEATURE_STRUCTURE_INVALID");
                r.Add("feature.type."+feature.SemanticId,feature.FeatureType,type,type==feature.FeatureType && AllowedType(feature.SemanticId,type),"FEATURE_STRUCTURE_INVALID");
                r.Add("feature.identity."+feature.SemanticId,feature.SwFeatureIdAtCapture,m.FeatureIds.ContainsKey(feature.SemanticId)?(object)m.FeatureIds[feature.SemanticId]:null,
                    m.FeatureIds.ContainsKey(feature.SemanticId) && m.FeatureIds[feature.SemanticId]==feature.SwFeatureIdAtCapture,"FEATURE_STRUCTURE_INVALID");
            }
            foreach (string sketch in new[] {"base_plate.profile_sketch","mounting_hole_seed.profile_sketch"})
            {
                string type; types.TryGetValue(sketch,out type);
                r.Add("feature.type."+sketch,"ProfileFeature",type,type=="ProfileFeature","FEATURE_STRUCTURE_INVALID");
            }
            string[] parents={"base_plate","mounting_hole_seed","base_plate","mounting_holes"};
            string[] children={"mounting_hole_seed","mounting_holes","outer_edge_fillet","outer_edge_fillet"};
            HashSet<string> graph=new HashSet<string>();
            foreach (DesignDependency d in state.Dependencies) graph.Add(d.ParentSemanticId+"->"+d.ChildSemanticId);
            HashSet<string> required=new HashSet<string>();
            for(int i=0;i<parents.Length;i++) required.Add(parents[i]+"->"+children[i]);
            r.Add("state.dependencies",new List<string>(required),new List<string>(graph),graph.SetEquals(required) && state.Dependencies.Count==4,"STATE_MODEL_MISMATCH");
            string[] kinds={"host","pattern_seed","edge_owner","preceding_body_feature"};
            for(int i=0;i<parents.Length;i++)
            {
                string parent=parents[i],child=children[i],kind=kinds[i];
                r.Add("state.dependency_kind."+parent+"->"+child,kind,null,state.Dependencies.Exists(delegate(DesignDependency d){return d.ParentSemanticId==parent && d.ChildSemanticId==child && d.Kind==kind;}),"STATE_MODEL_MISMATCH");
            }
            foreach (SemanticFeature f in state.Features.All())
            {
                HashSet<string> needed=new HashSet<string>();
                for(int i=0;i<parents.Length;i++) if(children[i]==f.SemanticId) needed.Add(parents[i]);
                r.Add("state.feature_dependencies."+f.SemanticId,new List<string>(needed),f.Dependencies,needed.SetEquals(f.Dependencies),"STATE_MODEL_MISMATCH");
            }
            foreach(KeyValuePair<string,double> e in expected)
            {
                r.Add("state.numeric."+e.Key,"finite positive mm",e.Value,e.Value>0 && !Double.IsInfinity(e.Value) && !Double.IsNaN(e.Value),"STATE_MODEL_MISMATCH");
                double actual;
                bool found=m.DimensionsMm.TryGetValue(e.Key,out actual);
                r.Add("dimension."+e.Key,e.Value,found?(object)actual:null,found && Near(actual,e.Value),"PARAMETER_NOT_APPLIED");
            }
            string[] ids={"base_plate.width","base_plate.height","base_plate.thickness","mounting_hole_seed.diameter","mounting_holes.spacing","outer_edge_fillet.radius"};
            string[] owners={"base_plate.profile_sketch","base_plate.profile_sketch","base_plate","mounting_hole_seed.profile_sketch","mounting_holes","outer_edge_fillet"};
            string[] names={"D1","D2","D1","D1","D3","D1"};
            for(int i=0;i<ids.Length;i++)
            {
                string parameter=ids[i]; DimensionBinding b=state.DimensionBindings.Find(delegate(DimensionBinding x){return x.ParameterSemanticId==parameter;});
                r.Add("binding.identity."+ids[i],owners[i]+"."+names[i],b==null?null:b.OwnerObjectSemanticId+"."+b.LocalDimensionName,
                    b!=null && b.OwnerObjectSemanticId==owners[i] && b.LocalDimensionName==names[i],"STATE_MODEL_MISMATCH");
                if(compareBindings) r.Add("state.binding_value."+ids[i],expected[ids[i]],b==null?null:(object)(b.CurrentValueMm??b.ValueMmAtCapture),
                    b!=null && Near(b.CurrentValueMm??b.ValueMmAtCapture,expected[ids[i]]),"STATE_MODEL_MISMATCH");
            }
            r.Add("body.solid_count",1,m.SolidBodyCount,m.SolidBodyCount==1,"GEOMETRY_INVALID");
            r.Add("body.all_count",1,m.AllBodyCount,m.AllBodyCount==1,"GEOMETRY_INVALID");
            double w=expected[ids[0]],h=expected[ids[1]],t=expected[ids[2]],diameter=expected[ids[3]],spacing=expected[ids[4]],radius=expected[ids[5]];
            if(compareBindings)
            {
                r.Add("state.seed_center_x",-spacing/2,state.Features.MountingHoleSeed.Parameters.CenterXmm,Near(state.Features.MountingHoleSeed.Parameters.CenterXmm,-spacing/2),"STATE_MODEL_MISMATCH");
                r.Add("state.seed_center_y",0,state.Features.MountingHoleSeed.Parameters.CenterYmm,Near(state.Features.MountingHoleSeed.Parameters.CenterYmm,0),"STATE_MODEL_MISMATCH");
            }
            double[] box={-w/2,-h/2,0,w/2,h/2,t};
            for(int i=0;i<6;i++) r.Add("bounding_box."+i,box[i],m.ExactBoxMm==null?null:(object)m.ExactBoxMm[i],m.ExactBoxMm!=null && Near(m.ExactBoxMm[i],box[i]),"GEOMETRY_INVALID");
            r.Add("pattern.count",state.Features.MountingHoles.Parameters.Count,m.PatternCount,m.PatternCount==2 && m.PatternCount==state.Features.MountingHoles.Parameters.Count,"FEATURE_STRUCTURE_INVALID");
            r.Add("pattern.spacing",spacing,m.PatternSpacingMm,Near(m.PatternSpacingMm,spacing),"PARAMETER_NOT_APPLIED");
            r.Add("pattern.direction","+X",m.PatternReverseDirection,!m.PatternReverseDirection && state.Features.MountingHoles.Parameters.Direction=="+X" && !state.Features.MountingHoles.Parameters.ReverseDirection,"STATE_MODEL_MISMATCH");
            r.Add("pattern.geometry_pattern",state.Features.MountingHoles.Parameters.GeometryPattern,m.PatternGeometryPattern,m.PatternGeometryPattern==state.Features.MountingHoles.Parameters.GeometryPattern,"STATE_MODEL_MISMATCH");
            r.Add("seed.depth_mode","through_all",m.SeedEndCondition,m.SeedEndCondition==(int)swEndConditions_e.swEndCondThroughAll && state.Features.MountingHoleSeed.Parameters.DepthMode=="through_all" && state.Features.MountingHoleSeed.Parameters.NativeEndCondition==m.SeedEndCondition,"FEATURE_STRUCTURE_INVALID");
            r.Add("fillet.definition_radius",radius,m.FilletDefinitionRadiusMm,Near(m.FilletDefinitionRadiusMm,radius),"PARAMETER_NOT_APPLIED");
            r.Add("hole.count",2,m.Holes.Count,m.Holes.Count==2,"GEOMETRY_INVALID");
            r.Add("hole.spacing",spacing,m.MeasuredHoleSpacingMm,m.MeasuredHoleSpacingMm.HasValue && Near(m.MeasuredHoleSpacingMm.Value,spacing),"GEOMETRY_INVALID");
            for(int i=0;i<m.Holes.Count;i++)
            {
                CylinderMeasurement hole=m.Holes[i];
                r.Add("hole.diameter."+i,diameter,hole.RadiusMm*2,Near(hole.RadiusMm*2,diameter),"GEOMETRY_INVALID");
                r.Add("hole.center."+i,new double[]{(i==0?-1:1)*spacing/2,0},hole.AxisPointMm,Near(hole.AxisPointMm[0],(i==0?-1:1)*spacing/2) && Near(hole.AxisPointMm[1],0),"GEOMETRY_INVALID");
                r.Add("hole.through_geometry."+i,new double[]{0,t},hole.ZRangeMm,Near(Math.Abs(hole.AxisDirection[2]),1) && Near(hole.ZRangeMm[0],0) && Near(hole.ZRangeMm[1],t) && hole.FullCircleBoundaryEdges==2,"GEOMETRY_INVALID");
            }
            r.Add("fillet.face_count",4,m.Fillets.Count,m.Fillets.Count==4,"GEOMETRY_INVALID");
            HashSet<string> corners=new HashSet<string>();
            foreach(CylinderMeasurement fillet in m.Fillets)
            {
                string corner=Math.Sign(fillet.AxisPointMm[0])+":"+Math.Sign(fillet.AxisPointMm[1]); corners.Add(corner);
                r.Add("fillet.geometry."+corner,radius,fillet.RadiusMm,Near(fillet.RadiusMm,radius) && Near(Math.Abs(fillet.AxisPointMm[0]),w/2-radius) && Near(Math.Abs(fillet.AxisPointMm[1]),h/2-radius) && Near(Math.Abs(fillet.AxisDirection[2]),1) && Near(fillet.ZRangeMm[0],0) && Near(fillet.ZRangeMm[1],t),"GEOMETRY_INVALID");
            }
            r.Add("fillet.distinct_corners",4,corners.Count,corners.Count==4,"GEOMETRY_INVALID");
            foreach(ReferenceObservation reference in references.Items)
                if(checkEntityReferences || reference.Kind=="feature") r.Add("reference."+reference.SemanticId+"["+reference.Index+"]","valid",reference.NativeStatusName,reference.Valid,"STALE_REFERENCE");
            r.Add("references.manifest_count",24,references.Total,references.Total==24 && references.Items.Count==24,"STALE_REFERENCE");
            return r;
        }
        private static bool AllowedType(string id,string type)
        { return id=="base_plate"?type=="Extrusion":id=="mounting_hole_seed"?(type=="Cut"||type=="ICE"):id=="mounting_holes"?type=="LPattern":type=="Fillet"; }
        internal static bool Near(double a,double b) { return !Double.IsNaN(a) && !Double.IsInfinity(a) && !Double.IsNaN(b) && !Double.IsInfinity(b) && Math.Abs(a-b)<0.0001; }
    }
}
