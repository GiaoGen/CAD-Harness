using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    internal static class CanonicalEntityResolver
    {
        internal static bool Supports(string id)
        {
            return id=="base_plate.top_face" || id=="base_plate.bottom_face" || id=="mounting_hole_seed.wall_face" ||
                id=="mounting_holes.instance_2.wall_face" || id=="outer_edge_fillet.rounded_faces" ||
                id=="mounting_holes.direction_edge" || id=="outer_edge_fillet.vertical_tangent_edges";
        }
        internal static string[] CandidateReferences(IModelDoc2 model,CadState state,SemanticEntityReference entity)
        {
            if(!Supports(entity.SemanticId)) throw new Program.PhaseFailure("RECOVERY_REFERENCE_UNSUPPORTED","Unsupported semantic fallback.");
            Array bodies=((IPartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody,false) as Array;
            if(bodies==null || bodies.Length!=1) throw new Program.PhaseFailure("RECOVERY_MODEL_INVALID","Fallback requires one canonical solid body.");
            IBody2 body=(IBody2)bodies.GetValue(0);
            BasePlateParameters plate=state.Features.BasePlate.Parameters;
            double w=plate.WidthMm/2,h=plate.HeightMm/2,t=plate.ThicknessMm;
            double radius=state.Features.OuterEdgeFillet.Parameters.RadiusMm,diameter=state.Features.MountingHoleSeed.Parameters.DiameterMm;
            double spacing=state.Features.MountingHoles.Parameters.SpacingMm;
            EditContext context=new EditContext(model,state);
            int baseId=context.Feature("base_plate").GetID(),seedId=context.Feature("mounting_hole_seed").GetID(),patternId=context.Feature("mounting_holes").GetID(),filletId=context.Feature("outer_edge_fillet").GetID();
            List<object> matches=new List<object>();
            foreach(IFace2 face in (object[])body.GetFaces())
            {
                ISurface surface=(ISurface)face.GetSurface(); double[] box=(double[])face.GetBox();
                IFeature owner=(IFeature)face.GetFeature(); int ownerId=owner==null?-1:owner.GetID();
                if(surface.IsPlane() && (entity.SemanticId=="base_plate.top_face" || entity.SemanticId=="base_plate.bottom_face"))
                {
                    bool top=entity.SemanticId=="base_plate.top_face"; double z=top?t:0;
                    double[] normal=(double[])face.Normal;
                    double expectedArea=plate.WidthMm*plate.HeightMm-(4-Math.PI)*radius*radius-2*Math.PI*diameter*diameter/4;
                    if(ownerId==baseId && Near(normal[0],0) && Near(normal[1],0) && Near(normal[2],top?1:-1) &&
                        Near(box[2]*1000,z) && Near(box[5]*1000,z) && Near(face.GetArea()*1000000,expectedArea)) matches.Add(face);
                }
                if(!surface.IsCylinder()) continue;
                double[] c=(double[])surface.CylinderParams;
                if(!Near(Math.Abs(c[5]),1) || !Near(box[2]*1000,0) || !Near(box[5]*1000,t)) continue;
                if(entity.SemanticId=="mounting_hole_seed.wall_face" && ownerId==seedId && Near(c[6]*2000,diameter) && Near(c[0]*1000,-spacing/2) && Near(c[1]*1000,0)) matches.Add(face);
                if(entity.SemanticId=="mounting_holes.instance_2.wall_face" && ownerId==patternId && Near(c[6]*2000,diameter) && Near(c[0]*1000,spacing/2) && Near(c[1]*1000,0)) matches.Add(face);
                if(entity.SemanticId=="outer_edge_fillet.rounded_faces" && ownerId==filletId && Near(c[6]*1000,radius) && Near(Math.Abs(c[0]*1000),w-radius) && Near(Math.Abs(c[1]*1000),h-radius)) matches.Add(face);
            }
            if(entity.Kind=="edge" || entity.Kind=="edge_set") foreach(IEdge edge in (object[])body.GetEdges())
            {
                if(!((ICurve)edge.GetCurve()).IsLine()) continue;
                IVertex start=(IVertex)edge.GetStartVertex(),end=(IVertex)edge.GetEndVertex();
                if(start==null || end==null) continue;
                double[] a=(double[])start.GetPoint(),b=(double[])end.GetPoint();
                for(int i=0;i<3;i++) { a[i]*=1000; b[i]*=1000; }
                if(entity.SemanticId=="mounting_holes.direction_edge" && Near(a[1],-h) && Near(b[1],-h) && Near(a[2],t) && Near(b[2],t) && Near(Math.Min(a[0],b[0]),-w+radius) && Near(Math.Max(a[0],b[0]),w-radius)) matches.Add(edge);
                if(entity.SemanticId=="outer_edge_fillet.vertical_tangent_edges" && Near(a[0],b[0]) && Near(a[1],b[1]) && Near(Math.Min(a[2],b[2]),0) && Near(Math.Max(a[2],b[2]),t) &&
                    ((Near(Math.Abs(a[0]),w) && Near(Math.Abs(a[1]),h-radius)) || (Near(Math.Abs(a[0]),w-radius) && Near(Math.Abs(a[1]),h)))) matches.Add(edge);
            }
            HashSet<string> references=new HashSet<string>(StringComparer.Ordinal);
            foreach(object value in matches) references.Add(PersistentReferences.Capture(model,value));
            if(references.Count!=entity.PersistentReferences.Length) throw new Program.PhaseFailure("RECOVERY_REFERENCE_AMBIGUOUS","Expected "+entity.PersistentReferences.Length+" unique candidates for "+entity.SemanticId+", found "+references.Count+".");
            string[] result=new string[references.Count]; references.CopyTo(result); Array.Sort(result,StringComparer.Ordinal); return result;
        }
        private static bool Near(double a,double b) { return CadValidator.Near(a,b); }
    }
}
