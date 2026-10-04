using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    // Only used after RequireEmptyPart. Never deletes an existing user's feature.
    internal sealed class CanonicalCreationTransaction
    {
        private readonly IModelDoc2 model;
        private readonly HashSet<int> existing=new HashSet<int>();
        internal bool Committed;
        internal CanonicalCreationTransaction(IModelDoc2 part)
        {
            model=part;
            foreach(IFeature feature in Features()) existing.Add(feature.GetID());
        }
        internal RecoveryAttempt Rollback()
        {
            RecoveryAttempt result=new RecoveryAttempt {RecoveryClass="R3",Action="remove only newly created canonical features in previously empty Part"};
            try
            {
                List<int> created=new List<int>();
                foreach(IFeature feature in Features()) if(!existing.Contains(feature.GetID()) && ModelingType(feature.GetTypeName2())) created.Add(feature.GetID());
                created.Reverse();
                foreach(int id in created)
                {
                    IFeature target=Features().Find(delegate(IFeature feature){return feature.GetID()==id;});
                    if(target==null) continue; // Already absorbed by deletion of its new parent.
                    model.ClearSelection2(true);
                    if(!target.Select2(false,0) || !BenchmarkTelemetry.Call("IModelDocExtension.DeleteSelection2",delegate{return model.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);}))
                        throw new Program.PhaseFailure("CREATE_ROLLBACK_FAILED","Could not remove a newly created feature.");
                }
                if(!SolidWorksBatchScope.Rebuild(model)) throw new Program.PhaseFailure("CREATE_ROLLBACK_FAILED","Rollback rebuild failed.");
                Array bodies=((IPartDoc)model).GetBodies2((int)swBodyType_e.swAllBodies,false) as Array;
                if((bodies!=null && bodies.Length!=0) || Features().Exists(delegate(IFeature feature){return !existing.Contains(feature.GetID()) && ModelingType(feature.GetTypeName2());}))
                    throw new Program.PhaseFailure("CREATE_ROLLBACK_FAILED","New geometry remains after rollback.");
                result.Success=true;
            }
            catch(Exception error) {result.Error=error.Message;}
            finally {model.ClearSelection2(true);}
            return result;
        }
        private List<IFeature> Features()
        {
            List<IFeature> result=new List<IFeature>();
            for(IFeature f=(IFeature)model.FirstFeature();f!=null;f=(IFeature)f.GetNextFeature()) result.Add(f);
            return result;
        }
        private static bool ModelingType(string type)
        {return type=="Extrusion" || type=="ICE" || type=="Cut" || type=="LPattern" || type=="Fillet" || type=="ProfileFeature";}
    }
}
