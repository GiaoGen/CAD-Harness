using System;
using System.Collections.Generic;
using System.IO;

namespace CadHarness.Cli
{
    internal sealed class BenchmarkDefinition
    {
        internal string SuiteId,Planner,PartTemplate,ManifestPath,ManifestSha256;
        internal int Iterations,WarmupIterations;
        internal readonly List<string> Cases=new List<string>();
        internal static readonly string[] Scenarios={"canonical_plate","rectangular_mounting_plate","invalid_fillet","stale_reference_recovery","unsupported_intent"};
        internal static BenchmarkDefinition Load(string path)
        {
            string fullPath=Path.GetFullPath(path);
            string text=File.ReadAllText(fullPath);
            Dictionary<string,object> root;
            try {root=StrictJson.Parse(text) as Dictionary<string,object>;}
            catch(PlannerFailure){throw Invalid("Benchmark manifest must be strict JSON with unique keys.");}
            Keys(root,new[]{"benchmarkVersion","suiteId","planner","iterations","warmupIterations","partTemplate","cases"});
            if(!(root["benchmarkVersion"] is string) || (string)root["benchmarkVersion"]!="0.1")throw Invalid("Unknown benchmark version.");
            string suite=root["suiteId"] as string,planner=root["planner"] as string,template=root["partTemplate"] as string;
            if(String.IsNullOrWhiteSpace(suite) || suite.Length>80)throw Invalid("suiteId must be 1..80 characters.");
            if(planner!="rule" && planner!="llm")throw Invalid("planner must be rule or llm.");
            if(String.IsNullOrWhiteSpace(template))throw Invalid("An explicit native Part template path is required.");
            List<object> cases=root["cases"] as List<object>;
            if(cases==null || cases.Count!=5)throw Invalid("Declare each required PRD case A..E exactly once.");
            BenchmarkDefinition definition=new BenchmarkDefinition {SuiteId=suite,Planner=planner,ManifestPath=fullPath,
                ManifestSha256=BenchmarkFiles.Hash(fullPath),PartTemplate=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath),template)),
                Iterations=Integer(root["iterations"],1,20),WarmupIterations=Integer(root["warmupIterations"],0,3)};
            foreach(object item in cases)
            {
                Dictionary<string,object> entry=item as Dictionary<string,object>;Keys(entry,new[]{"id","scenario"});
                string id=entry["id"] as string,scenario=entry["scenario"] as string;
                int index=id==null?-1:Array.IndexOf(new[]{"A","B","C","D","E"},id);
                if(index<0 || scenario!=Scenarios[index] || definition.Cases.Contains(id))throw Invalid("Case IDs/scenarios must match the fixed PRD A..E suite without duplicates.");
                definition.Cases.Add(id);
            }
            return definition;
        }
        private static int Integer(object value,int minimum,int maximum)
        {
            if(!(value is long) && !(value is double))throw Invalid("Iterations must be JSON integers.");
            double number=Convert.ToDouble(value);
            if(number<minimum || number>maximum || number!=Math.Truncate(number))throw Invalid("iterations=1..20, warmupIterations=0..3.");
            return (int)number;
        }
        private static void Keys(Dictionary<string,object> value,string[] keys)
        {if(value==null || value.Count!=keys.Length)throw Invalid("Unknown or missing manifest fields.");foreach(string key in keys)if(!value.ContainsKey(key))throw Invalid("Unknown or missing manifest fields.");}
        private static Program.PhaseFailure Invalid(string message){return new Program.PhaseFailure("BENCHMARK_MANIFEST_INVALID",message);}
    }

    internal static class BenchmarkFiles
    {
        internal static readonly System.Web.Script.Serialization.JavaScriptSerializer Json=new System.Web.Script.Serialization.JavaScriptSerializer {MaxJsonLength=16777216};
        internal static string Hash(string path)
        {using(System.Security.Cryptography.SHA256 sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}
        internal static void Save(string path,object value)
        {
            string temporary=path+".tmp";
            try
            {
                File.WriteAllText(temporary,Json.Serialize(value),new System.Text.UTF8Encoding(false));
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
            }
            finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
