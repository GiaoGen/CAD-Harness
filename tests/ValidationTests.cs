using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using CadHarness.Cli;

namespace CadHarness.Tests
{
    internal static class ValidationTests
    {
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
        private static readonly List<object> Results=new List<object>();
        private static CadState baseline;
        private static EditConfirmationResult measurements;
        private static ReferenceSnapshot references;
        private static int failures;
        private static int Main(string[] args)
        {
            string root=Path.GetFullPath(args[0]);
            baseline=StateSerializer.Load(Path.Combine(root,"artifacts/state/9af37689-f21c-4096-af17-63b878455f3d.json"));
            Dictionary<string,object> probe=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"artifacts/phase5/real-e2e/original-probe-after.json")));
            measurements=Json.Deserialize<EditConfirmationResult>(Json.Serialize(probe["confirmation"]));
            references=Json.Deserialize<ReferenceSnapshot>(Json.Serialize(probe["references"]));
            Case("valid previously measured canonical model",delegate {Require(Evaluate(Clone(baseline),Clone(measurements),Clone(references),null).Success,"Valid model rejected");});
            Changed("wrong feature type","feature.type.mounting_holes",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){t["mounting_holes"]="Cut";});
            Changed("changed feature identity","feature.identity.base_plate",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.FeatureIds["base_plate"]++;});
            Changed("missing feature","feature.exists.mounting_holes",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){t.Remove("mounting_holes");});
            Changed("sketch type mismatch","feature.type.base_plate.profile_sketch",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){t["base_plate.profile_sketch"]="Extrusion";});
            Changed("unbuilt document","document.needs_rebuild",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.NeedsRebuild=1;});
            Changed("feature rebuild error","feature.error.outer_edge_fillet",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.FeatureErrors["outer_edge_fillet"]=42;});
            Changed("extra solid body","body.solid_count",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.SolidBodyCount=2;});
            Changed("extra surface body","body.all_count",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.AllBodyCount=2;});
            Changed("missing box with no body","bounding_box.0",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.ExactBoxMm=null;});
            Changed("bounding box drift","bounding_box.5",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.ExactBoxMm[5]+=1;});
            Changed("requested dimension not applied","dimension.mounting_holes.spacing",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.DimensionsMm["mounting_holes.spacing"]=40;});
            Changed("hole diameter differs despite dimension","hole.diameter.0",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.Holes[0].RadiusMm=4;});
            Changed("hole count drift","hole.count",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.Holes.RemoveAt(1);});
            Changed("asymmetric hole centers","hole.center.0",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.Holes[0].AxisPointMm[0]+=1;});
            Changed("blind hole geometry","hole.through_geometry.0",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.Holes[0].ZRangeMm[1]=6;});
            Changed("blind end condition","seed.depth_mode",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.SeedEndCondition=0;});
            Changed("pattern count drift","pattern.count",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.PatternCount=3;});
            Changed("pattern spacing drift","pattern.spacing",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.PatternSpacingMm=40;});
            Changed("fillet parameter differs","fillet.definition_radius",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.FilletDefinitionRadiusMm=3;});
            Changed("missing fillet geometry","fillet.face_count",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){m.Fillets.RemoveAt(3);});
            Changed("stale reference","reference.base_plate[0]",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){r.Items[0].Valid=false;});
            Changed("incomplete reference manifest","references.manifest_count",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){r.Total=23;r.Items.RemoveAt(23);});
            Changed("broken dependency graph","state.dependencies",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){s.Dependencies.RemoveAt(0);});
            Changed("wrong dependency kind","state.dependency_kind.base_plate->mounting_hole_seed",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){s.Dependencies[0].Kind="preceding_body_feature";});
            Changed("binding ownership tampering","binding.identity.mounting_holes.spacing",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){s.DimensionBindings[4].OwnerObjectSemanticId="base_plate";});
            Changed("binding value drift","state.binding_value.mounting_holes.spacing",delegate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t){s.DimensionBindings[4].CurrentValueMm=40;});
            Case("R2 succeeds with one set/rebuild/validation",delegate {
                int sets=0,rebuilds=0,validations=0;
                RecoveryAttempt a=LocalRecovery.RetryParameter("mounting_holes.spacing",delegate{sets++;},delegate{rebuilds++;return true;},delegate{validations++;return new ValidationResult();});
                Require(a.Success && sets==1 && rebuilds==1 && validations==1,"Recovery count wrong");
            });
            Case("R2 failure never loops",delegate {
                int sets=0,rebuilds=0;
                RecoveryAttempt a=LocalRecovery.RetryParameter("mounting_holes.spacing",delegate{sets++;},delegate{rebuilds++;return true;},delegate{ValidationResult r=new ValidationResult();r.Add("parameter",50,40,false,"PARAMETER_NOT_APPLIED");return r;});
                Require(!a.Success && sets==1 && rebuilds==1,"Unbounded recovery");
            });
            Case("R2 rebuild failure stops before validation",delegate {
                int validations=0;
                RecoveryAttempt a=LocalRecovery.RetryParameter("mounting_holes.spacing",delegate{},delegate{return false;},delegate{validations++;return new ValidationResult();});
                Require(!a.Success && validations==0 && a.ErrorCode=="EDIT_REBUILD_FAILED","Validated failed rebuild or lost failure code");
            });
            Case("R1 never guesses feature/sketchpoint identity",delegate{Require(!CanonicalEntityResolver.Supports("mounting_holes") && !CanonicalEntityResolver.Supports("mounting_hole_seed.center_point"),"Identity fallback enabled");});
            Case("R1 unsupported reference remains stale class",delegate{Require(FailureClassifier.Classify("RECOVERY_REFERENCE_UNSUPPORTED",null)=="STALE_REFERENCE","Reference loss misclassified as unsupported user intent");});
            Case("R3 and R4 distinct failure classes",delegate{Require(FailureClassifier.Classify("EDIT_REBUILD_FAILED",ParameterEditIntent.ParseCli("base_plate.thickness","12"))=="FEATURE_REBUILD_FAILED" && FailureClassifier.Classify("EDIT_REBUILD_FAILED",ParameterEditIntent.ParseCli("outer_edge_fillet.radius","100"))=="GEOMETRY_IMPOSSIBLE","Wrong classification");});
            Case("R5 unsupported/ambiguous classification",delegate{Require(FailureClassifier.Classify("PLANNER_UNSUPPORTED_INTENT",null)=="UNSUPPORTED_INTENT" && FailureClassifier.Classify("PLANNER_AMBIGUOUS_INTENT",null)=="AMBIGUOUS_INTENT","Wrong intent classification");});
            File.WriteAllText(Path.Combine(root,"artifacts/phase6/validation-test-results.json"),Json.Serialize(new {passed=Results.Count-failures,failed=failures,total=Results.Count,source="data-only mutation tests over previously recorded real native measurements; no live COM or LLM",results=Results}));
            Console.WriteLine(Json.Serialize(new {total=Results.Count,failed=failures})); return failures==0?0:1;
        }
        private delegate void Change(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t);
        private static void Changed(string label,string check,Change change)
        {
            Case(label,delegate {CadState s=Clone(baseline);EditConfirmationResult m=Clone(measurements);ReferenceSnapshot r=Clone(references);Dictionary<string,string> t=Types(s);change(s,m,r,t);ValidationResult v=Evaluate(s,m,r,t);Require(!v.Success && v.Checks.Exists(delegate(ValidationCheck c){return c.Name==check && c.Status=="failed";}),"Expected failure missing: "+check);});
        }
        private static ValidationResult Evaluate(CadState s,EditConfirmationResult m,ReferenceSnapshot r,Dictionary<string,string> t)
        {return CadValidator.Evaluate(s,m,t??Types(s),r,CanonicalValues.From(s),true,true);}
        private static Dictionary<string,string> Types(CadState s)
        {Dictionary<string,string> t=new Dictionary<string,string>();foreach(SemanticFeature f in s.Features.All())t.Add(f.SemanticId,f.FeatureType);t.Add("base_plate.profile_sketch","ProfileFeature");t.Add("mounting_hole_seed.profile_sketch","ProfileFeature");return t;}
        private static T Clone<T>(T value) {return Json.Deserialize<T>(Json.Serialize(value));}
        private static void Require(bool value,string reason) {if(!value)throw new Exception(reason);}
        private static void Case(string label,Action action)
        {try{action();Results.Add(new {name=label,success=true});}catch(Exception error){failures++;Results.Add(new {name=label,success=false,error=error.Message});}}
    }
}
