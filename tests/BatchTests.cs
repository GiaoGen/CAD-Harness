using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using CadHarness.Cli;

namespace CadHarness.Tests
{
    internal static class BatchTests
    {
        private static int count,failed;
        private static readonly List<object> results=new List<object>();
        private static int Main(string[] args)
        {
            Check("normal restoration and one final refresh",delegate {
                Fake f=new Fake();SolidWorksBatchScope s=new SolidWorksBatchScope(f);
                Require(f.Values["CommandInProgress"] && !f.Values["EnableFeatureTree"] && !f.Values["EnableFeatureTreeWindow"] && !f.Values["EnableGraphicsUpdate"]);
                s.Dispose();Require(f.Original() && s.Evidence.Restored && f.Tree==1 && f.Graphics==1);
                s.Dispose();Require(f.Tree==1 && f.Graphics==1);
            });
            Check("exception body restores all settings",delegate {
                Fake f=new Fake();try {using(new SolidWorksBatchScope(f))throw new InvalidOperationException("body");}catch(InvalidOperationException){}
                Require(f.Original() && f.Tree==1 && f.Graphics==1);
            });
            Check("nested scopes preserve outer batch",delegate {
                Fake f=new Fake();using(new SolidWorksBatchScope(f))
                {using(new SolidWorksBatchScope(f)){}Require(f.Values["CommandInProgress"] && f.Tree==0 && f.Graphics==0);}
                Require(f.Original() && f.Tree==1 && f.Graphics==1);
            });
            Check("already-disabled settings remain disabled",delegate {
                Fake f=new Fake();foreach(string name in new List<string>(f.Values.Keys))f.Values[name]=name=="CommandInProgress";
                SolidWorksBatchScope s=new SolidWorksBatchScope(f);s.Dispose();Require(s.Evidence.ChangedSettings.Count==0 && f.Tree==0 && f.Graphics==0 && f.Values["CommandInProgress"]);
            });
            string[] names={"CommandInProgress","EnableFeatureTree","EnableFeatureTreeWindow","EnableGraphicsUpdate"};
            foreach(string item in names)
            {
                string name=item;
                Check("partial initialization read failure "+name,delegate {
                    Fake f=new Fake();f.FailRead=name;bool caught=false;
                    try {using(new SolidWorksBatchScope(f)){} }catch(InvalidOperationException){caught=true;}
                    Require(caught && f.Original());
                });
                Check("setter mutates then throws "+name,delegate {
                    Fake f=new Fake();f.FailWrite=name;bool caught=false;
                    try {using(new SolidWorksBatchScope(f)){} }catch(InvalidOperationException){caught=true;}
                    Require(caught && f.Original());
                });
                Check("restore failure is visible and remaining restores run "+name,delegate {
                    Fake f=new Fake();SolidWorksBatchScope s=new SolidWorksBatchScope(f);f.FailWrite=name;bool caught=false;
                    try{s.Dispose();}catch(Program.PhaseFailure e){caught=e.Code=="BATCH_RESTORE_FAILED";}
                    Require(caught && s.Evidence.RestoreErrors.Count==1 && f.Original() && f.Tree==0 && f.Graphics==0);
                });
            }
            Check("final tree failure still attempts redraw",delegate {
                Fake f=new Fake();SolidWorksBatchScope s=new SolidWorksBatchScope(f);f.FailTree=true;
                bool caught=false;try{s.Dispose();}catch(Program.PhaseFailure){caught=true;}
                Require(caught && f.Original() && f.Graphics==1);
            });
            Directory.CreateDirectory(Path.Combine(args[0],"artifacts/phase7"));
            string json=new JavaScriptSerializer().Serialize(new{success=failed==0,total=count,failed=failed,results=results});
            File.WriteAllText(Path.Combine(args[0],"artifacts/phase7/unit-tests.json"),json);Console.WriteLine(json);
            return failed==0?0:1;
        }
        private static void Check(string name,Action body){count++;try{body();results.Add(new{name=name,success=true});}catch(Exception e){failed++;results.Add(new{name=name,success=false,error=e.Message});}}
        private static void Require(bool ok){if(!ok)throw new Exception("Assertion failed");}
        private sealed class Fake : IBatchSettings
        {
            internal readonly Dictionary<string,bool> Values=new Dictionary<string,bool>{{"CommandInProgress",false},{"EnableFeatureTree",true},{"EnableFeatureTreeWindow",true},{"EnableGraphicsUpdate",true}};
            internal string FailRead,FailWrite;
            internal bool FailTree;
            internal int Tree,Graphics;
            public bool Read(string name){if(FailRead==name){FailRead=null;throw new InvalidOperationException("read");}return Values[name];}
            public void Write(string name,bool value){Values[name]=value;if(FailWrite==name){FailWrite=null;throw new InvalidOperationException("write after mutation");}}
            public void UpdateTree(){Tree++;if(FailTree)throw new InvalidOperationException("tree");}
            public void Redraw(){Graphics++;}
            internal bool Original(){return !Values["CommandInProgress"] && Values["EnableFeatureTree"] && Values["EnableFeatureTreeWindow"] && Values["EnableGraphicsUpdate"];}
        }
    }
}
