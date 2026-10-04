using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using CadHarness.Cli;

namespace CadHarness.Tests
{
    internal static class PlannerTests
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength=2097152 };
        private static readonly List<object> Results = new List<object>();
        private static int failures;
        private static string root;

        private static int Main(string[] args)
        {
            root=Path.GetFullPath(args.Length > 0 ? args[0] : ".");
            if (args.Length > 1 && args[1] == "serve") return ServeCli();
            string canonical=IntentSchema.CanonicalJson();
            string edit=IntentSchema.EditJson("mounting_holes.spacing",50);
            string[] commands={ "spacing 40 -> 50 mm", "Change plate thickness from 10 to 12 mm", "set hole diameter to 10 mm", "fillet R4" };
            double[] expected={50,12,10,4};
            for (int i=0; i<4; i++)
            {
                int index=i;
                Case("rule edit " + IntentSchema.Parameters[i],delegate {
                    PlannerResult result=Plan(new RulePlanner(),commands[index]);
                    Require(result.Success && result.Intent.Edit.ParameterSemanticId == IntentSchema.Parameters[index] && result.Intent.Edit.ValueMm == expected[index], "Wrong typed edit");
                    Require(result.Metrics.Calls == 0 && result.Metrics.InputTokens == null && result.Metrics.OutputTokens == null,"Invented LLM call/usage");
                    return Describe(result);
                });
            }
            foreach (string command in new[] { "create canonical part", "创建标准板件", "创建80×50×10 mm板，两个Ø8通孔，孔距40 mm，四条外侧竖直边R3圆角" })
            {
                string input=command;
                Case("rule canonical " + input,delegate { PlannerResult result=Plan(new RulePlanner(),input); Require(result.Success && result.Intent.Create.BasePlate.WidthMm==80 && result.Intent.Create.HolePattern.Count==2 && result.Intent.Create.OuterFillet.RadiusMm==3,"Not canonical"); return Describe(result); });
            }
            string[] chinese={ "将孔距从40 mm改为50 mm", "把板厚从10改为12毫米", "孔径改为10 mm", "将圆角半径改为4 mm" };
            for (int i=0; i<4; i++)
            {
                int index=i;
                Case("rule Chinese edit " + index,delegate { PlannerResult result=Plan(new RulePlanner(),chinese[index]); Require(result.Success && result.Intent.Edit.ParameterSemanticId==IntentSchema.Parameters[index] && result.Intent.Edit.ValueMm==expected[index],"Chinese edit mapping failed"); return Describe(result); });
            }
            foreach (string command in new[] { "create a turbine blade", "create canonical part then add a pocket", "spacing 50 mm and thickness 12 mm", "set thickness to 0.5 inch", "increase thickness by 2 mm", "run FeatureCut4()", "thickness R12" })
            {
                string input=command;
                Case("rule reject " + input,delegate { PlannerResult result=Plan(new RulePlanner(),input); Rejected(result,"PLANNER_UNSUPPORTED_INTENT",false,true); return Describe(result); });
            }
            foreach (string command in new[] { "thickness 0", "hole diameter -2", "spacing 1001", "fillet 1e309" })
            {
                string input=command;
                Case("rule numeric reject " + input,delegate { PlannerResult result=Plan(new RulePlanner(),input); Rejected(result,"PLANNER_NUMERIC_INVALID",true,false); return Describe(result); });
            }
            Case("empty command",delegate { PlannerResult result=Plan(new RulePlanner()," "); Require(result.ErrorCode=="PLANNER_INPUT_INVALID" && result.Metrics.Calls==0,"Empty input accepted"); return Describe(result); });
            Case("oversized input",delegate { PlannerResult result=Plan(new RulePlanner(),new string('x',4097)); Require(result.ErrorCode=="PLANNER_INPUT_INVALID","Unbounded input accepted"); return Describe(result); });

            foreach (string output in new[] { canonical, edit, IntentSchema.EditJson(IntentSchema.Parameters[1],12), IntentSchema.EditJson(IntentSchema.Parameters[2],10), IntentSchema.EditJson(IntentSchema.Parameters[3],4) })
            {
                string content=output;
                Case("llm accepts " + output,delegate {
                    FakeTransport transport=new FakeTransport(Envelope(content,true));
                    PlannerResult result=Plan(new LlmPlanner(Options(),transport),"Fixture command");
                    Require(result.Success && result.Metrics.Calls==1 && transport.Calls==1 && result.Metrics.InputTokens==23 && result.Metrics.OutputTokens==17,"LLM success/usage mismatch");
                    Require(transport.Url.AbsolutePath=="/v1/chat/completions" && transport.Authorization=="Bearer test-only-key","Endpoint/auth mapping failed");
                    Dictionary<string,object> request=(Dictionary<string,object>)StrictJson.Parse(transport.Body);
                    Dictionary<string,object> format=(Dictionary<string,object>)request["response_format"];
                    Dictionary<string,object> schemaWrapper=(Dictionary<string,object>)format["json_schema"];
                    Dictionary<string,object> schema=(Dictionary<string,object>)schemaWrapper["schema"];
                    Require((string)format["type"]=="json_schema" && (bool)schemaWrapper["strict"] && !(bool)schema["additionalProperties"],"No strict schema on wire");
                    Require(!request.ContainsKey("tools") && !request.ContainsKey("functions") && (long)request["n"]==1 && !(bool)request["stream"],"Tool/sequence or repeated calls requested");
                    return Describe(result);
                });
            }
            List<Tuple<string,string,string,bool>> badOutputs=new List<Tuple<string,string,string,bool>> {
                Bad("C# text","model.RunCommand(1);","PLANNER_JSON_INVALID",false),
                Bad("Markdown fence","```json\n"+edit+"\n```","PLANNER_JSON_INVALID",false),
                Bad("trailing text",edit+"\nExecute now","PLANNER_JSON_INVALID",false),
                Bad("duplicate key",edit.Replace("\"mode\":\"edit\"","\"mode\":\"create\",\"mode\":\"edit\""),"PLANNER_JSON_INVALID",false),
                Bad("trailing comma",edit.TrimEnd('}')+",}","PLANNER_JSON_INVALID",false),
                Bad("comment","/* note */"+edit,"PLANNER_JSON_INVALID",false),
                Bad("unknown field",edit.TrimEnd('}')+",\"featureName\":\"Pattern.MountingHoles\"}","PLANNER_SCHEMA_INVALID",false),
                Bad("tool sequence",edit.TrimEnd('}')+",\"operations\":[\"FeatureCut4\"]}","PLANNER_SCHEMA_INVALID",false),
                Bad("array of intents","["+edit+","+canonical+"]","PLANNER_SCHEMA_INVALID",false),
                Bad("missing field",edit.Replace("\"units\":\"mm\",",""),"PLANNER_SCHEMA_INVALID",false),
                Bad("version",edit.Replace("\"0.1\"","\"99\""),"PLANNER_SCHEMA_INVALID",false),
                Bad("inch units",edit.Replace("\"mm\"","\"inch\""),"PLANNER_SCHEMA_INVALID",false),
                Bad("unknown mode",edit.Replace("\"mode\":\"edit\"","\"mode\":\"execute_code\""),"PLANNER_UNSUPPORTED_INTENT",true),
                Bad("unknown parameter",edit.Replace("mounting_holes.spacing","chamfer.distance"),"PLANNER_UNSUPPORTED_INTENT",true),
                Bad("feature display name",edit.Replace("mounting_holes.spacing","Pattern.MountingHoles"),"PLANNER_UNSUPPORTED_INTENT",true),
                Bad("noncanonical preset",canonical.Replace("canonical","turbine"),"PLANNER_UNSUPPORTED_INTENT",true),
                Bad("create/edit mixed",edit.Replace("\"createPreset\":null","\"createPreset\":\"canonical\""),"PLANNER_SCHEMA_INVALID",false),
                Bad("create numeric extra",canonical.Replace("\"valueMm\":null","\"valueMm\":50"),"PLANNER_SCHEMA_INVALID",false),
                Bad("string numeric",edit.Replace("\"valueMm\":50","\"valueMm\":\"50\""),"PLANNER_NUMERIC_INVALID",false),
                Bad("boolean numeric",edit.Replace("\"valueMm\":50","\"valueMm\":true"),"PLANNER_NUMERIC_INVALID",false),
                Bad("zero numeric",IntentSchema.EditJson(IntentSchema.Parameters[0],0),"PLANNER_NUMERIC_INVALID",false),
                Bad("negative numeric",IntentSchema.EditJson(IntentSchema.Parameters[0],-1),"PLANNER_NUMERIC_INVALID",false),
                Bad("upper bound",IntentSchema.EditJson(IntentSchema.Parameters[0],1001),"PLANNER_NUMERIC_INVALID",false),
                Bad("null numeric",edit.Replace("\"valueMm\":50","\"valueMm\":null"),"PLANNER_NUMERIC_INVALID",false),
                Bad("NaN",edit.Replace("\"valueMm\":50","\"valueMm\":NaN"),"PLANNER_JSON_INVALID",false),
                Bad("overflow",edit.Replace("\"valueMm\":50","\"valueMm\":1e309"),"PLANNER_JSON_INVALID",false),
                Bad("leading zero",edit.Replace("\"valueMm\":50","\"valueMm\":050"),"PLANNER_JSON_INVALID",false),
                Bad("plus numeric",edit.Replace("\"valueMm\":50","\"valueMm\":+50"),"PLANNER_JSON_INVALID",false),
                Bad("empty output","","PLANNER_JSON_INVALID",false),
                Bad("oversized intent",new string(' ',8193),"PLANNER_SCHEMA_INVALID",false)
            };
            foreach (Tuple<string,string,string,bool> test in badOutputs)
            {
                Tuple<string,string,string,bool> item=test;
                Case("llm rejects " + test.Item1,delegate {
                    FakeTransport transport=new FakeTransport(Envelope(item.Item2,true));
                    PlannerResult result=Plan(new LlmPlanner(Options(),transport),"Fixture command");
                    Rejected(result,item.Item3,!item.Item4,item.Item4);
                    Require(result.Metrics.Calls==1 && transport.Calls==1 && result.Metrics.InputTokens==23,"Failure retried or lost usage");
                    return Describe(result);
                });
            }
            Case("usage absent stays null",delegate { PlannerResult result=Plan(new LlmPlanner(Options(),new FakeTransport(Envelope(edit,false))),"spacing 50"); Require(result.Success && result.Metrics.InputTokens==null && result.Metrics.OutputTokens==null,"Estimated tokens"); return Describe(result); });
            Case("invalid usage stays null",delegate { string response=Envelope(edit,true).Replace("\"prompt_tokens\":23","\"prompt_tokens\":-1").Replace("\"completion_tokens\":17","\"completion_tokens\":\"17\""); PlannerResult result=Plan(new LlmPlanner(Options(),new FakeTransport(response)),"spacing 50"); Require(result.Success && result.Metrics.InputTokens==null && result.Metrics.OutputTokens==null,"Invalid usage accepted"); return Describe(result); });
            Case("explicit json_object compatibility",delegate { LlmPlannerOptions options=Options(); options.OutputMode="json_object"; FakeTransport transport=new FakeTransport(Envelope(edit,false)); PlannerResult result=Plan(new LlmPlanner(options,transport),"spacing 50"); Require(result.Success && transport.Body.Contains("\"type\":\"json_object\""),"Compatibility mode missing"); return Describe(result); });
            Case("json_object still validates",delegate { LlmPlannerOptions options=Options(); options.OutputMode="json_object"; PlannerResult result=Plan(new LlmPlanner(options,new FakeTransport(Envelope(edit.TrimEnd('}')+",\"code\":\"C#\"}",false))),"spacing 50"); Rejected(result,"PLANNER_SCHEMA_INVALID",true,false); return Describe(result); });
            Case("provider tool call rejected",delegate { string response=Json.Serialize(new { choices=new[] {new { finish_reason="tool_calls",message=new {content=edit,tool_calls=new[]{new {type="function",function=new {name="FeatureCut4",arguments="{}"}}}}}} }); PlannerResult result=Plan(new LlmPlanner(Options(),new FakeTransport(response)),"spacing 50"); Rejected(result,"PLANNER_TOOLS_FORBIDDEN",true,false); return Describe(result); });
            Case("provider refusal records unsupported",delegate { string response=Json.Serialize(new { choices=new[] {new {finish_reason="stop",message=new {content=(string)null,refusal="Unsupported turbine"}}}}); PlannerResult result=Plan(new LlmPlanner(Options(),new FakeTransport(response)),"turbine"); Rejected(result,"PLANNER_UNSUPPORTED_INTENT",false,true); return Describe(result); });
            Case("truncated output rejected",delegate { PlannerResult result=Plan(new LlmPlanner(Options(),new FakeTransport(Envelope(edit,true).Replace("\"stop\"","\"length\""))),"spacing 50"); Rejected(result,"PLANNER_OUTPUT_INCOMPLETE",true,false); Require(result.Metrics.OutputTokens==17,"Lost truncation usage"); return Describe(result); });
            Case("provider envelope malformed",delegate { PlannerResult result=Plan(new LlmPlanner(Options(),new FakeTransport("{\"choices\":[]}")),"spacing 50"); Rejected(result,"PLANNER_RESPONSE_INVALID",true,false); return Describe(result); });
            Case("HTTP failure no retry",delegate { FakeTransport transport=new FakeTransport("test-only-key",(HttpStatusCode)429); PlannerResult result=Plan(new LlmPlanner(Options(),transport),"spacing 50"); Require(result.ErrorCode=="PLANNER_HTTP_FAILED" && result.Metrics.Calls==1 && transport.Calls==1 && !result.Error.Contains("test-only-key"),"Retried/leaked error response"); return Describe(result); });
            Case("oversized protocol response",delegate { PlannerResult result=Plan(new LlmPlanner(Options(),new FakeTransport(new string('x',1048577))),"spacing 50"); Rejected(result,"PLANNER_RESPONSE_TOO_LARGE",true,false); return Describe(result); });
            Case("malformed UTF8 counts parse failure",delegate { PlannerResult result=Plan(new LlmPlanner(Options(),new InvalidUtf8Transport()),"spacing 50"); Rejected(result,"PLANNER_RESPONSE_INVALID",true,false); return Describe(result); });
            Case("cancellation before request",delegate { using (CancellationTokenSource cancellation=new CancellationTokenSource()) { cancellation.Cancel(); PlannerResult result=new LlmPlanner(Options(),new FakeTransport(Envelope(edit,false))).PlanAsync("spacing 50",null,cancellation.Token).GetAwaiter().GetResult(); Require(result.ErrorCode=="PLANNER_CANCELLED" && result.Metrics.Calls==0,"Cancelled request sent"); return Describe(result); } });
            Case("cancellation during request",delegate { using (CancellationTokenSource cancellation=new CancellationTokenSource()) { cancellation.CancelAfter(50); PlannerResult result=new LlmPlanner(Options(),new FakeTransport(null,HttpStatusCode.OK,true)).PlanAsync("spacing 50",null,cancellation.Token).GetAwaiter().GetResult(); Require(result.ErrorCode=="PLANNER_CANCELLED" && result.Metrics.Calls==1,"Cancellation failed"); return Describe(result); } });
            Case("timeout bounded no retry",delegate { LlmPlannerOptions options=Options(); options.TimeoutSeconds=1; FakeTransport transport=new FakeTransport(null,HttpStatusCode.OK,true); PlannerResult result=Plan(new LlmPlanner(options,transport),"spacing 50"); Require(result.ErrorCode=="PLANNER_TIMEOUT" && result.Metrics.Calls==1 && transport.Calls==1 && result.Metrics.LatencyMs<5000,"Timeout/retry failed"); return Describe(result); });
            Case("invalid config makes zero calls",delegate { LlmPlannerOptions options=Options(); options.OutputMode="unsafe_text"; FakeTransport transport=new FakeTransport(Envelope(edit,false)); PlannerResult result=Plan(new LlmPlanner(options,transport),"spacing 50"); Require(result.ErrorCode=="PLANNER_CONFIG_INVALID" && result.Metrics.Calls==0 && transport.Calls==0,"Invalid config sent request"); return Describe(result); });
            Case("current state projection excludes names/references",delegate {
                CadState state=StateSerializer.Load(Path.Combine(root,"docs","phase4-state-snapshot.json")); string before=Json.Serialize(state);
                FakeTransport transport=new FakeTransport(Envelope(edit,false)); PlannerResult result=new LlmPlanner(Options(),transport).PlanAsync("spacing 50",state,CancellationToken.None).GetAwaiter().GetResult();
                Require(result.Success && Json.Serialize(state)==before && !transport.Body.Contains("Extrude.Base") && !transport.Body.Contains("PersistentReference") && !transport.Body.Contains(state.Document.Id),"State/COM identity leaked or changed");
                Dictionary<string,object> request=(Dictionary<string,object>)StrictJson.Parse(transport.Body);
                List<object> messages=(List<object>)request["messages"];
                Dictionary<string,object> user=(Dictionary<string,object>)StrictJson.Parse((string)((Dictionary<string,object>)messages[1])["content"]);
                Dictionary<string,object> projected=(Dictionary<string,object>)user["currentState"];
                Require(((Dictionary<string,object>)projected["parameters"]).Count==4,"Unbounded state projection"); return Describe(result);
            });
            Case("actual loopback HTTP transport and env configuration",delegate {
                using (LoopbackEndpoint endpoint=new LoopbackEndpoint(new[] {Envelope(edit,true)}))
                using (EnvironmentScope environment=new EnvironmentScope(endpoint.BaseUrl,"synthetic-local"))
                {
                    PlannerResult result=Plan(new LlmPlanner(),"spacing 50");
                    Require(result.Success && result.Metrics.Calls==1 && result.Metrics.Model=="synthetic-local","Real local HTTP failed");
                    endpoint.Wait(); Require(endpoint.Requests.Count==1 && endpoint.Requests[0].Contains("json_schema"),"Loopback request missing schema"); return Describe(result);
                }
            });
            Directory.CreateDirectory(Path.Combine(root,"artifacts","phase5"));
            File.WriteAllText(Path.Combine(root,"artifacts","phase5","planner-test-results.json"),Json.Serialize(new {
                testKind="Deterministic rules, synthetic HTTP protocol fixtures, and actual loopback HTTP; no real model inference",
                executed=Results.Count, failures=failures, results=Results
            }),new UTF8Encoding(false));
            Console.WriteLine(Json.Serialize(new {executed=Results.Count,failures=failures,evidence="artifacts/phase5/planner-test-results.json"}));
            return failures==0 ? 0 : 1;
        }

        private static int ServeCli()
        {
            string malicious=IntentSchema.EditJson(IntentSchema.Parameters[0],50).TrimEnd('}')+",\"apiCall\":\"RunCommand\"}";
            using (LoopbackEndpoint endpoint=new LoopbackEndpoint(new[] { Envelope(malicious,true), Envelope(IntentSchema.EditJson(IntentSchema.Parameters[3],100),false), Envelope(IntentSchema.CanonicalJson(),true), Envelope(IntentSchema.EditJson(IntentSchema.Parameters[3],4),true) }))
            {
                string addressFile=Path.Combine(root,"artifacts","phase5","loopback-address.txt");
                File.WriteAllText(addressFile,endpoint.BaseUrl,new UTF8Encoding(false));
                Console.WriteLine("Synthetic local endpoint ready; expects 4 CLI requests.");
                endpoint.Wait();
                File.WriteAllText(Path.Combine(root,"artifacts","phase5","loopback-cli-requests.json"),Json.Serialize(new {testKind="synthetic protocol, not model inference",requests=endpoint.Requests}),new UTF8Encoding(false));
            }
            return 0;
        }
        private static Tuple<string,string,string,bool> Bad(string name,string output,string code,bool unsupported) { return Tuple.Create(name,output,code,unsupported); }
        private static PlannerResult Plan(ICadPlanner planner,string text) { return planner.PlanAsync(text,null,CancellationToken.None).GetAwaiter().GetResult(); }
        private static LlmPlannerOptions Options() { return new LlmPlannerOptions {BaseUrl=new Uri("http://127.0.0.1:23456/v1"),ApiKey="test-only-key",Model="synthetic-test",TimeoutSeconds=2}; }
        private static string Envelope(string content,bool usage) { return Json.Serialize(new {choices=new[]{new {finish_reason="stop",message=new {role="assistant",content=content}}},usage=usage ? (object)new {prompt_tokens=23,completion_tokens=17} : null}); }
        private static object Describe(PlannerResult result) { return new {success=result.Success,errorCode=result.ErrorCode,metrics=result.Metrics,intent=result.Intent==null ? null : IntentSchema.Document(result.Intent)}; }
        private static void Rejected(PlannerResult result,string code,bool parse,bool unsupported) { Require(!result.Success && result.Intent==null && result.ErrorCode==code && result.Metrics.ParseFailures==(parse?1:0) && result.Metrics.UnsupportedIntents==(unsupported?1:0),"Expected "+code+" but got "+result.ErrorCode); }
        private static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Case(string name,Func<object> test) { try { Results.Add(new {name=name,passed=true,detail=test()}); } catch (Exception error) { failures++; Results.Add(new {name=name,passed=false,error=error.Message}); Console.Error.WriteLine(name+": "+error.Message); } }

        private sealed class FakeTransport : HttpMessageHandler
        {
            private readonly string response; private readonly HttpStatusCode status; private readonly bool delay;
            internal int Calls; internal string Body,Authorization; internal Uri Url;
            internal FakeTransport(string response,HttpStatusCode status=HttpStatusCode.OK,bool delay=false) { this.response=response; this.status=status; this.delay=delay; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
            {
                Calls++; Url=request.RequestUri; Authorization=request.Headers.Authorization==null ? null : request.Headers.Authorization.ToString();
                Body=await request.Content.ReadAsStringAsync();
                if (delay) await Task.Delay(10000,cancellationToken);
                return new HttpResponseMessage(status) {Content=new StringContent(response??"",Encoding.UTF8,"application/json")};
            }
        }
        private sealed class InvalidUtf8Transport : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
            { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent(new byte[] {255})}); }
        }
        private sealed class EnvironmentScope : IDisposable
        {
            private readonly Dictionary<string,string> original=new Dictionary<string,string>();
            internal EnvironmentScope(string url,string model)
            {
                Set("CAD_HARNESS_LLM_BASE_URL",url); Set("CAD_HARNESS_LLM_MODEL",model); Set("CAD_HARNESS_LLM_API_KEY",null);
                Set("CAD_HARNESS_LLM_OUTPUT_MODE",null); Set("CAD_HARNESS_LLM_TIMEOUT_SECONDS","2"); Set("CAD_HARNESS_LLM_MAX_OUTPUT_TOKENS",null);
            }
            private void Set(string name,string value) { original[name]=Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name,value); }
            public void Dispose() { foreach (KeyValuePair<string,string> item in original) Environment.SetEnvironmentVariable(item.Key,item.Value); }
        }
        private sealed class LoopbackEndpoint : IDisposable
        {
            private readonly TcpListener listener;
            private readonly Task serving;
            internal readonly List<string> Requests=new List<string>();
            internal readonly string BaseUrl;
            internal LoopbackEndpoint(string[] responses)
            {
                listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
                BaseUrl="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/v1";
                serving=Task.Run(delegate {
                    foreach (string response in responses)
                    using (TcpClient connection=listener.AcceptTcpClient())
                    {
                        connection.ReceiveTimeout=5000; connection.SendTimeout=5000;
                        using (NetworkStream stream=connection.GetStream())
                        {
                            List<byte> header=new List<byte>();
                            while (header.Count<65536)
                            {
                                int next=stream.ReadByte(); if (next<0) throw new IOException("Incomplete HTTP header"); header.Add((byte)next);
                                int n=header.Count;
                                if (n>=4 && header[n-4]==13 && header[n-3]==10 && header[n-2]==13 && header[n-1]==10) break;
                            }
                            string headers=Encoding.ASCII.GetString(header.ToArray());
                            int length=0;
                            foreach (string line in headers.Split(new[]{"\r\n"},StringSplitOptions.None)) if (line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)) length=Int32.Parse(line.Substring(15).Trim());
                            if (headers.IndexOf("Expect: 100-continue",StringComparison.OrdinalIgnoreCase)>=0) { byte[] interim=Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); stream.Write(interim,0,interim.Length); }
                            Require(length>0 && length<1048576,"Bad request size"); byte[] body=new byte[length]; int offset=0;
                            while (offset<length) { int read=stream.Read(body,offset,length-offset); if (read==0) throw new IOException("Incomplete HTTP body"); offset+=read; }
                            Requests.Add(Encoding.UTF8.GetString(body));
                            byte[] responseBytes=Encoding.UTF8.GetBytes(response);
                            byte[] responseHeader=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+responseBytes.Length+"\r\nConnection: close\r\n\r\n");
                            stream.Write(responseHeader,0,responseHeader.Length); stream.Write(responseBytes,0,responseBytes.Length);
                        }
                    }
                });
            }
            internal void Wait() { if (!serving.Wait(TimeSpan.FromSeconds(120))) throw new TimeoutException("Loopback fixture requests did not complete"); }
            public void Dispose() { listener.Stop(); }
        }
    }
}
