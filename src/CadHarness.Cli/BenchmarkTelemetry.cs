using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CadHarness.Cli
{
    public sealed class BenchmarkApiObservation
    {
        public string Api { get; set; }
        public int Calls { get; set; }
        public int Exceptions { get; set; }
        public double WallMs { get; set; }
    }
    public sealed class BenchmarkRuntimeMetrics
    {
        public double CompileMs { get; set; }
        public string CompilationMode { get; set; }
        public double RuntimeWallMs { get; set; }
        public double SolidWorksExecutionMs { get; set; }
        public double RebuildMs { get; set; }
        public int Rebuilds { get; set; }
        public double ValidationMs { get; set; }
        public int ValidationCalls { get; set; }
        public int DirectSolidWorksApiOperations { get; set; }
        public int RecoveryAttempts { get; set; }
        public string ApiCountCoverage { get; set; }
        public List<BenchmarkApiObservation> ApiOperations { get; set; }
        public BenchmarkRuntimeMetrics()
        {
            CompilationMode="prebuilt typed Runtime; no per-command C# compilation";
            ApiCountCoverage="instrumented modeling, parameter mutation, sketch relation, explicit rebuild and final tree/redraw invocations; excludes selection, metadata, property getters/setters, validation queries and implicit native operations; not a total COM round-trip count";
            ApiOperations=new List<BenchmarkApiObservation>();
        }
    }
    internal sealed class BenchmarkTelemetry : IDisposable
    {
        [ThreadStatic] private static BenchmarkTelemetry active;
        private readonly BenchmarkTelemetry parent;
        private bool disposed;
        internal readonly BenchmarkRuntimeMetrics Metrics=new BenchmarkRuntimeMetrics();
        internal BenchmarkTelemetry() {parent=active;active=this;}
        internal static T Call<T>(string api,Func<T> invoke)
        {
            if(active==null)return invoke();
            BenchmarkTelemetry observer=active;
            Stopwatch timer=Stopwatch.StartNew();bool failed=false;
            try{return invoke();}catch{failed=true;throw;}
            finally
            {
                timer.Stop();BenchmarkApiObservation entry=observer.Metrics.ApiOperations.Find(delegate(BenchmarkApiObservation item){return item.Api==api;});
                if(entry==null){entry=new BenchmarkApiObservation{Api=api};observer.Metrics.ApiOperations.Add(entry);}
                entry.Calls++;if(failed)entry.Exceptions++;entry.WallMs+=timer.Elapsed.TotalMilliseconds;
                observer.Metrics.DirectSolidWorksApiOperations++;observer.Metrics.SolidWorksExecutionMs+=timer.Elapsed.TotalMilliseconds;
                if(api=="IModelDoc2.EditRebuild3"){observer.Metrics.Rebuilds++;observer.Metrics.RebuildMs+=timer.Elapsed.TotalMilliseconds;}
            }
        }
        internal static void Call(string api,Action invoke) {Call<int>(api,delegate{invoke();return 0;});}
        internal static void Validation(double milliseconds) {if(active!=null){active.Metrics.ValidationCalls++;active.Metrics.ValidationMs+=milliseconds;}}
        public void Dispose() {if(disposed)return;disposed=true;active=parent;}
    }
}
