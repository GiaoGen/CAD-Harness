using System;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CadHarness.Cli
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if(args.Length>0 && args[0]=="benchmark")return BenchmarkCli.Run(args);
            if (args.Length > 0 && (args[0] == "plan" || args[0] == "run")) return PlannerCli.Run(args);
            if (args.Length > 0 && (args[0] == "validate" || args[0] == "recover-state")) return ValidationCli.Run(args);
            bool stateCommand = args.Length > 0 && (args[0] == "capture-state" || args[0] == "resolve-state" || args[0] == "state");
            bool createCanonical = args.Length > 0 && args[0] == "create-canonical";
            bool editCommand = args.Length > 0 && args[0] == "edit";
            bool simpleCommand = args.Length == 1 && (args[0] == "inspect" || args[0] == "create-plate");
            if ((!simpleCommand && !stateCommand && !createCanonical && !editCommand) ||
                (editCommand ? (args.Length < 3 || args.Length > 4) : args.Length > 2) ||
                (args.Length == 1 && args[0] == "state"))
            {
                Console.Error.WriteLine("Usage: CadHarness.Cli.exe inspect | create-plate | create-canonical [state-file] | capture-state [state-file] | resolve-state [state-file] | state <state-file> | edit <semantic-parameter> <mm> [state-file]");
                return 2;
            }

            bool mutationStarted = false;
            CanonicalCreationTransaction creationTransaction=null;
            SolidWorksBatchScope batch=null;
            string step = "connect";
            try
            {
                string statePath = editCommand ? (args.Length == 4 ? args[3] : null) : (args.Length == 2 ? args[1] : null);
                if (editCommand) step = "parse-edit";
                ParameterEditIntent editIntent = editCommand ? ParameterEditIntent.ParseCli(args[1], args[2]) : null;
                if (args[0] == "state")
                {
                    step = "load-state";
                    Print(StateSerializer.Load(statePath));
                    return 0; // Pure disk read: no SOLIDWORKS connection.
                }
                // Attach only. Never launch another SOLIDWORKS instance.
                step = "connect";
                ISldWorks app = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                step = "active-document";
                IModelDoc2 model = app.ActiveDoc as IModelDoc2;
                if (model == null)
                    throw new PhaseFailure("NO_ACTIVE_DOCUMENT", "Open an empty Part document in the running SOLIDWORKS instance.");
                if (model.GetType() != (int)swDocumentTypes_e.swDocPART)
                    throw new PhaseFailure("ACTIVE_DOCUMENT_NOT_PART", "The active document must be a Part.");

                IConfiguration configuration = (IConfiguration)model.GetActiveConfiguration();
                if (editCommand)
                {
                    step = "edit";
                    ParameterEditEvidence result = ParameterEditRunner.Execute(app, model, editIntent, statePath);
                    Print(result);
                    return result.Success ? 0 : 1;
                }
                if (args[0] == "inspect")
                {
                    Print(new {
                        success = true, phase = 0, solidWorksRevision = app.RevisionNumber(),
                        title = model.GetTitle(), path = model.GetPathName(),
                        documentType = "Part", configuration = configuration.Name
                    });
                    return 0;
                }

                if (args[0] == "capture-state")
                {
                    step = "capture-state";
                    Print(StateService.CaptureAndSave(app, model, statePath));
                    return 0;
                }
                if (args[0] == "resolve-state")
                {
                    step = "resolve-state";
                    Print(StateService.LoadAndResolve(model, statePath));
                    return 0;
                }

                step = "preflight";
                RequireEmptyPart(model);
                IFeature plane = FindFirstReferencePlane(model);
                if (plane == null)
                    throw new PhaseFailure("REFERENCE_PLANE_NOT_FOUND", "No native reference plane is available.");

                if (args[0] == "create-canonical")
                {
                    step = "create-canonical";
                    creationTransaction=new CanonicalCreationTransaction(model);
                    batch=new SolidWorksBatchScope(app,model);
                    CadIntent intent = CadIntent.Canonical();
                    string[] featureNames = CanonicalExecutor.Create(app, model, plane, intent, ref mutationStarted);
                    batch.Dispose(); // Restore before validation/persistence can commit state.
                    step = "capture-state";
                    object persistence = StateService.CaptureAndSave(app, model, statePath);
                    creationTransaction.Committed=true;
                    Print(new {
                        success = true, phase = 3, title = model.GetTitle(), features = featureNames, cadState = persistence,
                        widthMm = intent.BasePlate.WidthMm, heightMm = intent.BasePlate.HeightMm,
                        thicknessMm = intent.BasePlate.ThicknessMm, holeDiameterMm = intent.HoleSeed.DiameterMm,
                        holeCount = intent.HolePattern.Count, holeSpacingMm = intent.HolePattern.SpacingMm,
                        filletRadiusMm = intent.OuterFillet.RadiusMm,
                        saved = false, execution = "direct-solidworks-api", batch = batch.Evidence
                    });
                    return 0;
                }

                step = "create-plate";
                batch=new SolidWorksBatchScope(app,model);
                IFeature boss = CreatePlate(app, model, plane, new BasePlateIntent(80, 50, 10), ref mutationStarted);
                batch.Dispose();
                Print(new {
                    success = true, phase = 1, title = model.GetTitle(),
                    featureName = boss.Name, featureType = boss.GetTypeName2(),
                    widthMm = 80, heightMm = 50, thicknessMm = 10,
                    saved = false, execution = "direct-solidworks-api", batch = batch.Evidence
                });
                return 0;
            }
            catch (Exception error)
            {
                PhaseFailure known = error as PhaseFailure;
                RecoveryAttempt creationRollback=creationTransaction!=null && !creationTransaction.Committed && mutationStarted ? creationTransaction.Rollback() : null;
                string batchCleanupError=null;
                try {if(batch!=null)batch.Dispose();}catch(Exception cleanupError){batchCleanupError=cleanupError.Message;}
                Print(new {
                    success = false, step = step,
                    code = known != null ? known.Code : (step == "connect" ? "SOLIDWORKS_ATTACH_FAILED" :
                        (error is System.IO.IOException || error is UnauthorizedAccessException ? "STATE_IO_FAILED" : "DIRECT_API_FAILED")),
                    message = error.Message, failureClass = FailureClassifier.Classify(known == null ? null : known.Code, null), hresult = "0x" + error.HResult.ToString("X8"),
                    partialModelMayRemain = mutationStarted && (creationRollback==null || !creationRollback.Success), creationRollback = creationRollback,
                    batch = batch==null?null:batch.Evidence, batchCleanupError = batchCleanupError
                });
                return 1;
            }
            finally
            {
                // Also covers failures while reporting/rolling back an earlier error.
                try {if(batch!=null)batch.Dispose();}
                catch(Exception cleanupError) {Console.Error.WriteLine("Batch cleanup failed: "+cleanupError.Message);}
            }
        }

        internal static int ExecuteVerifiedRuntime(string[] args) { return Main(args); }

        internal static IFeature CreatePlate(ISldWorks app, IModelDoc2 model, IFeature plane, BasePlateIntent intent, ref bool mutationStarted, bool deferRebuild = false)
        {
            ISketchManager sketches = model.SketchManager;
            bool previousDimensionPrompt = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            bool sketchOpened = false;
            try
            {
                // Direct dimension creation must not open a dimension value dialog.
                app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
                model.ClearSelection2(true);
                if (!plane.Select2(false, 0))
                    throw new PhaseFailure("PLANE_SELECTION_FAILED", "The reference plane could not be selected through the API.");

                mutationStarted = true;
                BenchmarkTelemetry.Call("ISketchManager.InsertSketch",delegate{sketches.InsertSketch(true);});
                sketchOpened = model.GetActiveSketch2() != null;
                if (!sketchOpened)
                    throw new PhaseFailure("SKETCH_CREATION_FAILED", "The API did not enter a new sketch.");
                ISketch sketch = (ISketch)model.GetActiveSketch2();
                IFeature sketchFeature = (IFeature)sketch;
                object[] segments = BenchmarkTelemetry.Call("ISketchManager.CreateCenterRectangle",delegate{return sketches.CreateCenterRectangle(0, 0, 0, Mm(intent.WidthMm / 2), Mm(intent.HeightMm / 2), 0);}) as object[];
                if (segments == null || segments.Length < 4)
                    throw new PhaseFailure("RECTANGLE_CREATION_FAILED", "CreateCenterRectangle returned no usable rectangle.");

                ISketchSegment horizontal = null;
                ISketchSegment vertical = null;
                foreach (object item in segments)
                {
                    ISketchSegment segment = (ISketchSegment)item;
                    if (segment.ConstructionGeometry) continue;
                    ISketchLine line = segment as ISketchLine;
                    if (line == null) continue;
                    ISketchPoint start = (ISketchPoint)line.GetStartPoint2();
                    ISketchPoint end = (ISketchPoint)line.GetEndPoint2();
                    if (Math.Abs(start.Y - end.Y) < 1e-9) horizontal = segment;
                    if (Math.Abs(start.X - end.X) < 1e-9) vertical = segment;
                }
                AddSizeDimension(model, horizontal, true, intent.WidthMm);
                AddSizeDimension(model, vertical, false, intent.HeightMm);
                model.ClearSelection2(true);
                BenchmarkTelemetry.Call("ISketchManager.InsertSketch",delegate{sketches.InsertSketch(true);});
                sketchOpened = false;

                if (!sketchFeature.Select2(false, 0))
                    throw new PhaseFailure("PROFILE_SELECTION_FAILED", "The completed sketch could not be selected through the API.");
                IFeatureManager features = model.FeatureManager;
                IFeature boss = BenchmarkTelemetry.Call("IFeatureManager.FeatureExtrusion3",delegate{return features.FeatureExtrusion3(
                    true, false, false,
                    (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind,
                    Mm(intent.ThicknessMm), 0,
                    false, false, false, false, 0, 0,
                    false, false, false, false,
                    true, true, true,
                    (int)swStartConditions_e.swStartSketchPlane, 0, false);});
                if (boss == null)
                    throw new PhaseFailure("EXTRUSION_CREATION_FAILED", "FeatureExtrusion3 returned no feature.");
                if (!deferRebuild && !SolidWorksBatchScope.Rebuild(model))
                    throw new PhaseFailure("REBUILD_FAILED", "SOLIDWORKS reported a rebuild failure.");
                return boss;
            }
            finally
            {
                // This is scope cleanup, not a transaction/rollback implementation.
                try
                {
                    if (sketchOpened && model.GetActiveSketch2() != null)
                        BenchmarkTelemetry.Call("ISketchManager.InsertSketch",delegate{sketches.InsertSketch(true);});
                    model.ClearSelection2(true);
                }
                finally
                {
                    app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, previousDimensionPrompt);
                }
            }
        }

        private static void AddSizeDimension(IModelDoc2 model, ISketchSegment segment, bool horizontal, double sizeMm)
        {
            if (segment == null)
                throw new PhaseFailure("RECTANGLE_EDGE_NOT_FOUND", "A rectangle edge could not be found.");
            model.ClearSelection2(true);
            if (!segment.Select4(false, null))
                throw new PhaseFailure("EDGE_SELECTION_FAILED", "A sketch edge could not be selected through the API.");
            IDisplayDimension display = (IDisplayDimension)(horizontal
                ? BenchmarkTelemetry.Call("IModelDoc2.AddHorizontalDimension2",delegate{return model.AddHorizontalDimension2(0, -Mm(35), 0);})
                : BenchmarkTelemetry.Call("IModelDoc2.AddVerticalDimension2",delegate{return model.AddVerticalDimension2(Mm(50), 0, 0);}));
            if (display == null)
                throw new PhaseFailure("DIMENSION_CREATION_FAILED", "The API did not create the requested size dimension.");
            IDimension dimension = display.GetDimension2(0);
            int result = BenchmarkTelemetry.Call("IDimension.SetSystemValue3",delegate{return dimension.SetSystemValue3(Mm(sizeMm), (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);});
            if (result != (int)swSetValueReturnStatus_e.swSetValue_Successful)
                throw new PhaseFailure("DIMENSION_SET_FAILED", "SetSystemValue3 returned status " + result + ".");
        }

        private static void RequireEmptyPart(IModelDoc2 model)
        {
            if (model.GetActiveSketch2() != null)
                throw new PhaseFailure("SKETCH_ALREADY_ACTIVE", "Finish the current sketch before creating a part.");
            IPartDoc part = (IPartDoc)model;
            Array bodies = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as Array;
            if (bodies != null && bodies.Length > 0)
                throw new PhaseFailure("PART_NOT_EMPTY", "Creation requires an empty Part; existing bodies will not be changed.");
            for (IFeature feature = (IFeature)model.FirstFeature(); feature != null; feature = (IFeature)feature.GetNextFeature())
            {
                string type = feature.GetTypeName2();
                // Only default document containers and reference geometry are accepted.
                if (type != "RefPlane" && type != "OriginProfileFeature" && type != "HistoryFolder" &&
                    type != "CommentsFolder" && type != "FavoriteFolder" && type != "SelectionSetFolder" &&
                    type != "SensorFolder" && type != "DetailCabinet" && type != "SolidBodyFolder" &&
                    type != "SurfaceBodyFolder" && type != "MaterialFolder" && type != "NotesAreaFtrFolder" &&
                    type != "DocsFolder" && type != "EnvFolder" && type != "EqnFolder" &&
                    type != "InkMarkupFolder" && type != "ConfigTableFolder")
                    throw new PhaseFailure("PART_NOT_EMPTY", "Unexpected existing feature: " + feature.Name + " (" + type + "). Use an empty Part.");
            }
        }

        private static IFeature FindFirstReferencePlane(IModelDoc2 model)
        {
            // Object selection avoids localized plane names and coordinate-based selection.
            for (IFeature feature = (IFeature)model.FirstFeature(); feature != null; feature = (IFeature)feature.GetNextFeature())
                if (feature.GetTypeName2() == "RefPlane") return feature;
            return null;
        }

        internal static double Mm(double value) { return value / 1000.0; }
        private static void Print(object value) { Console.WriteLine(new JavaScriptSerializer().Serialize(value)); }

        internal sealed class PhaseFailure : Exception
        {
            internal readonly string Code;
            internal PhaseFailure(string code, string message) : base(message) { Code = code; }
        }
    }
}
