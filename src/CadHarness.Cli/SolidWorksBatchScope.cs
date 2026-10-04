using System;
using System.Collections.Generic;
using System.Diagnostics;
using SolidWorks.Interop.sldworks;

namespace CadHarness.Cli
{
    public sealed class BatchExecutionEvidence
    {
        public List<string> ChangedSettings { get; set; }
        public List<string> RestoreErrors { get; set; }
        public bool Restored { get; set; }
        public int ExplicitRebuildCalls { get; set; }
        public double RebuildMs { get; set; }
        public int TreeRefreshCalls { get; set; }
        public int GraphicsRedrawCalls { get; set; }
        public double ScopeMs { get; set; }
        public BatchExecutionEvidence() { ChangedSettings=new List<string>(); RestoreErrors=new List<string>(); }
    }

    internal interface IBatchSettings
    {
        bool Read(string setting);
        void Write(string setting, bool value);
        void UpdateTree();
        void Redraw();
    }

    // Snapshot before each write, including a setter that changes state and then throws.
    // Cleanup attempts ALL restores. Restoration failure is visible before state commit.
    internal sealed class SolidWorksBatchScope : IDisposable
    {
        [ThreadStatic] private static SolidWorksBatchScope current;
        private readonly SolidWorksBatchScope parent;
        private readonly IBatchSettings settings;
        private readonly List<KeyValuePair<string,bool>> originals=new List<KeyValuePair<string,bool>>();
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private bool disposed;
        private bool updateTree, redraw;
        internal readonly BatchExecutionEvidence Evidence=new BatchExecutionEvidence();

        internal SolidWorksBatchScope(ISldWorks app, IModelDoc2 model) : this(new NativeBatchSettings(app,model)) { }
        internal SolidWorksBatchScope(IBatchSettings adapter)
        {
            settings=adapter; parent=current;
            try
            {
                Change("CommandInProgress",true);
                updateTree=settings.Read("EnableFeatureTree");
                Change("EnableFeatureTree",false);
                Change("EnableFeatureTreeWindow",false);
                redraw=settings.Read("EnableGraphicsUpdate");
                Change("EnableGraphicsUpdate",false);
                current=this;
            }
            catch (Exception startError)
            {
                try { Dispose(); }
                catch (Exception restoreError) { throw new AggregateException(startError,restoreError); }
                throw;
            }
        }
        private void Change(string name,bool value)
        {
            bool original=settings.Read(name);
            if(original==value) return;
            originals.Add(new KeyValuePair<string,bool>(name,original));
            Evidence.ChangedSettings.Add(name);
            settings.Write(name,value);
            if(settings.Read(name)!=value) throw new Program.PhaseFailure("BATCH_SETTING_FAILED",name+" did not accept its batch setting.");
        }
        internal static bool Rebuild(IModelDoc2 model)
        {
            Stopwatch timer=Stopwatch.StartNew();
            try { return BenchmarkTelemetry.Call("IModelDoc2.EditRebuild3",delegate{return model.EditRebuild3();}); }
            finally
            {
                timer.Stop();
                if(current!=null) {current.Evidence.ExplicitRebuildCalls++;current.Evidence.RebuildMs+=timer.Elapsed.TotalMilliseconds;}
            }
        }
        public void Dispose()
        {
            if(disposed) return;
            disposed=true; current=parent;
            for(int i=originals.Count-1;i>=0;i--)
            {
                KeyValuePair<string,bool> entry=originals[i];
                try
                {
                    settings.Write(entry.Key,entry.Value);
                    if(settings.Read(entry.Key)!=entry.Value) throw new InvalidOperationException("Restored value did not match snapshot.");
                }
                catch(Exception error) { Evidence.RestoreErrors.Add(entry.Key+": "+error.Message); }
            }
            Evidence.Restored=Evidence.RestoreErrors.Count==0;
            // Respect a caller's already-disabled refresh settings and nested scopes.
            if(Evidence.Restored)
            {
                try {if(updateTree) {Evidence.TreeRefreshCalls++;settings.UpdateTree();}}
                catch(Exception error) {Evidence.RestoreErrors.Add("UpdateFeatureTree: "+error.Message);}
                try {if(redraw) {Evidence.GraphicsRedrawCalls++;settings.Redraw();}}
                catch(Exception error) {Evidence.RestoreErrors.Add("GraphicsRedraw2: "+error.Message);}
            }
            clock.Stop(); Evidence.ScopeMs=clock.Elapsed.TotalMilliseconds;
            if(Evidence.RestoreErrors.Count!=0)
                throw new Program.PhaseFailure("BATCH_RESTORE_FAILED",String.Join("; ",Evidence.RestoreErrors));
        }
    }

    internal sealed class NativeBatchSettings : IBatchSettings
    {
        private readonly ISldWorks app;
        private readonly IModelDoc2 model;
        private readonly IFeatureManager features;
        private readonly IModelView view;
        internal NativeBatchSettings(ISldWorks application,IModelDoc2 document)
        {
            app=application;model=document;features=model.FeatureManager;view=model.ActiveView as IModelView;
            if(view==null) throw new Program.PhaseFailure("BATCH_VIEW_UNAVAILABLE","No native model view is available for batch execution.");
        }
        public bool Read(string name)
        {
            switch(name)
            {
                case "CommandInProgress": return app.CommandInProgress;
                case "EnableFeatureTree": return features.EnableFeatureTree;
                case "EnableFeatureTreeWindow": return features.EnableFeatureTreeWindow;
                case "EnableGraphicsUpdate": return view.EnableGraphicsUpdate;
                default: throw new ArgumentException(name);
            }
        }
        public void Write(string name,bool value)
        {
            switch(name)
            {
                case "CommandInProgress": app.CommandInProgress=value;break;
                case "EnableFeatureTree": features.EnableFeatureTree=value;break;
                case "EnableFeatureTreeWindow": features.EnableFeatureTreeWindow=value;break;
                case "EnableGraphicsUpdate": view.EnableGraphicsUpdate=value;break;
                default: throw new ArgumentException(name);
            }
        }
        public void UpdateTree() {BenchmarkTelemetry.Call("IFeatureManager.UpdateFeatureTree",delegate{features.UpdateFeatureTree();});}
        public void Redraw() {BenchmarkTelemetry.Call("IModelDoc2.GraphicsRedraw2",delegate{model.GraphicsRedraw2();});}
    }

    // Only the isolated circle primitive uses AddToDB. Rectangle relations and edit
    // constraints keep the existing solver/inference behavior.
    internal sealed class SketchBatchScope : IDisposable
    {
        private readonly ISketchManager sketches;
        private readonly bool add, display;
        private bool disposed;
        internal SketchBatchScope(ISketchManager manager)
        {
            sketches=manager;add=sketches.AddToDB;display=sketches.DisplayWhenAdded;
            try {sketches.AddToDB=true;sketches.DisplayWhenAdded=false;}
            catch(Exception startError)
            {try {Dispose();}catch(Exception restoreError){throw new AggregateException(startError,restoreError);}throw;}
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            List<string> errors=new List<string>();
            // DisplayWhenAdded requires AddToDB=true; restore display first.
            try {sketches.DisplayWhenAdded=display;if(sketches.DisplayWhenAdded!=display)throw new InvalidOperationException("Readback mismatch.");}
            catch(Exception error){errors.Add("DisplayWhenAdded: "+error.Message);}
            try {sketches.AddToDB=add;if(sketches.AddToDB!=add)throw new InvalidOperationException("Readback mismatch.");}
            catch(Exception error){errors.Add("AddToDB: "+error.Message);}
            if(errors.Count!=0)throw new Program.PhaseFailure("BATCH_SKETCH_RESTORE_FAILED",String.Join("; ",errors));
        }
    }
}
