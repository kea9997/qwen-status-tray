using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

partial class QwenStatus {
 internal static bool OfflineTest {get{return SelfTestRequested();}}
 internal static string TestDataRoot {get{return DataRoot;}}
 static bool SelfTestRequested(){
  string[] supported={"--core-test","--insights-test","--operations-test","--experience-test","--chat-test","--delegation-test","--install-test","--strata-test","--status-test","--self-test","--usage-test","--token-test-ui","--insights-ui-test"};
  return Environment.GetCommandLineArgs().Any(x=>supported.Contains(x))||AppDomain.CurrentDomain.GetData("QwenStatusOfflineTest") as string=="1";
 }
 static string StatusDataRoot(){
  string normal=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QwenStatus"),test=Environment.GetEnvironmentVariable("QWEN_SELF_TEST_DIR");
  if(!OfflineTest)return normal;
  if(string.IsNullOrWhiteSpace(test)||!Path.IsPathRooted(test))throw new InvalidOperationException("Offline tests require an absolute QWEN_SELF_TEST_DIR (never actual QwenStatus data).");
  test=Path.GetFullPath(test).TrimEnd(Path.DirectorySeparatorChar);
  if(test.Equals(normal,StringComparison.OrdinalIgnoreCase)||test.StartsWith(normal+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Self-test directory must not be actual QwenStatus data.");
  return test;
 }
 static Dictionary<string,object> ObjectAt(Dictionary<string,object> data,string key){object value;return data!=null&&data.TryGetValue(key,out value)?value as Dictionary<string,object>:null;}
 static double StrataMetric(Dictionary<string,object> data,string name){
  var live=ObjectAt(data,"live");var totals=ObjectAt(data,"totals");
  if(name=="prompt_tokens_total")return Number(totals,"prompt_tokens");
  if(name=="generation_tokens_total")return Number(totals,"output_tokens");
  if(name=="num_requests_waiting")return Number(live,"queued");
  if(name=="generation_tokens_per_second"){double n=Number(live,"tok_s_mean");return double.IsNaN(n)?Number(live,"tok_s"):n;}
  if(name=="prompt_tokens_per_second")return Number(live,"prefill_tok_s_mean");
  if(name=="num_requests_running"){
   double n=Number(live,"running");if(!double.IsNaN(n))return n;
   string s=live==null?"":Field(live,"state");return s=="reading"||s=="generating"?1:s=="idle"||s=="unloaded"?0:double.NaN;
  }
  return double.NaN;
 }
 static bool HealthLoaded(string body){return HealthLoaded(body,Backend()=="strata",Setting("modelId",""));}
 static bool HealthLoaded(string body,bool strata,string expected){
  if(string.IsNullOrWhiteSpace(body)||!body.TrimStart().StartsWith("{"))return !strata;
  try{
   var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(body);
   if(Field(data,"service")!="strata")return !strata;
   object loaded,raw;string model=Field(data,"model");double context=Number(data,"max_context");
   if(strata&&(!data.TryGetValue("max_context",out raw)||!(raw is int||raw is long||raw is decimal||raw is double)))return false;
   if(strata&&(string.IsNullOrWhiteSpace(model)||model.IndexOfAny(new[]{'\r','\n'})>=0||(!string.IsNullOrEmpty(expected)&&model!=expected)||double.IsNaN(context)||double.IsInfinity(context)||context<=4096||Math.Floor(context)!=context))return false;
   return data.TryGetValue("loaded",out loaded)&&loaded is bool&&(bool)loaded;
  }catch{return false;}
 }
 static bool ProbeReady(string body,bool isModelHealth){
  if(isModelHealth)return HealthLoaded(body);
  try{
   var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(body);object ready;
   return data!=null&&(!data.TryGetValue("ready",out ready)||(ready is bool&&(bool)ready));
  }catch{return false;}
 }
 static bool ExternalStrata(){return Backend()=="strata"&&Setting("serverOwnership","external")!="managed";}
 static string BackendLabel(){return Backend()=="strata"?"Strata FN":Backend();}
 internal static string[] TokenProfiles(){
  if(Backend()!="strata")return new[]{"quick","8k","32k","64k","224k"};
  double capacity;double.TryParse(Setting("contextTokens","0"),out capacity);
  var result=new List<string>{"quick"};string[] names={"8k","32k","64k","224k"};int[] targets={7680,32256,65024,228864};for(int i=0;i<names.Length;i++)if(targets[i]+512<=capacity)result.Add(names[i]);return result.ToArray();
 }
 internal static void RuntimeEnvironment(System.Diagnostics.ProcessStartInfo info){
  string backend=Backend()=="vLLM"?"vllm":Backend();info.EnvironmentVariables["QWEN_BACKEND"]=backend;info.EnvironmentVariables["QWEN_BACKEND_FILE"]=BackendFile;
  info.EnvironmentVariables["QWEN_QUEUE_URL"]=new Uri(QueueUrl).GetLeftPart(UriPartial.Authority);info.EnvironmentVariables["QWEN_UPSTREAM_URL"]=ServerUrl.TrimEnd('/');
  info.EnvironmentVariables["QWEN_MODEL_ID"]=Setting("modelId",backend=="strata"?"":"qwen3.8-27b");info.EnvironmentVariables["QWEN_CONTEXT_TOKENS"]=backend=="strata"?Setting("contextTokens",""):"";info.EnvironmentVariables["QWEN_GATEWAY_DATA_DIR"]=GatewayRoot;
  string receipt=Path.Combine(ManagedRoot(),"setup-state.json");
  if(File.Exists(receipt)){var c=ObjectAt(new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(receipt)),"connection");if(c!=null&&Field(c,"backend")==backend){info.EnvironmentVariables["QWEN_MODEL_ID"]=Field(c,"model");info.EnvironmentVariables["QWEN_UPSTREAM_URL"]=Field(c,"upstream");info.EnvironmentVariables["QWEN_CONTEXT_TOKENS"]=backend=="strata"?Field(c,"contextTokens"):"";}}
 }
 double reportedContext=double.NaN,nativeInputSpeed=double.NaN;string reportedModel="";
 static void StrataTest(){
  const string idle="{\"engine\":{\"model\":\"fixture-flash-next\",\"max_context\":32768},\"live\":{\"state\":\"idle\",\"queued\":0,\"tok_s\":null},\"totals\":{\"prompt_tokens\":123,\"output_tokens\":45}}";
  if(Metric(idle,"generation_tokens_total")!=45||Metric(idle,"prompt_tokens_total")!=123||Metric(idle,"num_requests_running")!=0||!double.IsNaN(Metric(idle,"generation_tokens_per_second"))||!double.IsNaN(Metric(idle,"gpu_cache_usage_perc")))throw new Exception("Strata JSON mapping fabricated or lost metrics");
  const string health="{\"service\":\"strata\",\"model\":\"fixture-flash-next\",\"max_context\":32768,\"loaded\":true}";
  if(!HealthLoaded(health,true,"fixture-flash-next")||HealthLoaded(health.Replace("true","false"),true,"fixture-flash-next"))throw new Exception("Reachable unloaded Strata treated as ready");
  foreach(string invalid in new[]{"", "not JSON", "{", "null", "{}", "{\"loaded\":true}",health.Replace("strata","foreign"),health.Replace("32768","null"),health.Replace("32768","\"32768\""),health.Replace("32768","true"),health.Replace("true","\"true\""),health.Replace("fixture-flash-next","wrong-model")})if(HealthLoaded(invalid,true,"fixture-flash-next"))throw new Exception("Invalid/foreign/wrong-model health treated as Strata ready: "+invalid);
  if(!HealthLoaded("ok",false,"")||!HealthLoaded("{\"status\":\"ok\"}",false,""))throw new Exception("Legacy non-Strata health regressed");
  Settings["backend"]="strata";Settings["serverOwnership"]="external";Settings["modelId"]="fixture-flash-next";
  foreach(string gateway in new[]{"{\"ready\":true}","{\"running\":0,\"waiting\":0,\"ui_running\":0}","{\"running\":1,\"waiting\":2,\"ui_running\":1}","{\"active\":null,\"pending\":[]}"})if(!ProbeReady(gateway,false))throw new Exception("Healthy gateway treated as unloaded Strata: "+gateway);
  foreach(string gateway in new[]{"{\"ready\":false}","{\"ready\":\"true\"}","{","null","not JSON",""})if(ProbeReady(gateway,false))throw new Exception("Invalid/not-ready gateway accepted: "+gateway);
  if(!ProbeReady(health,true))throw new Exception("Valid configured Strata health rejected by diagnostics");
  foreach(string invalid in new[]{"{\"ready\":true}","{\"running\":0,\"waiting\":0}","{",health.Replace("strata","foreign"),health.Replace("fixture-flash-next","wrong-model"),health.Replace("true","false")})if(ProbeReady(invalid,true))throw new Exception("Invalid/foreign/unloaded model accepted by diagnostics: "+invalid);
  using(var app=new QwenStatus()){
   app.reportedModel="fixture-flash-next";app.reportedContext=32768;
   foreach(int s in new[]{2,1,4,0,3}){
    app.Display(s,123,45,double.NaN,s==3?1:0,0);
    if(app.start.Enabled||app.stop.Enabled)throw new Exception("External Strata lifecycle enabled");
    if(!app.note.Text.Contains(32768.ToString("N0")))throw new Exception("Actual Strata context missing from status");
    app.Show();Application.DoEvents();using(var image=new Bitmap(app.Width,app.Height)){app.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(DataRoot,"strata-status-"+s+".png"));}
   }
   Settings["serverOwnership"]="managed";app.action=true;app.Display(1,double.NaN,double.NaN,double.NaN,0,0);if(app.start.Enabled||app.stop.Enabled)throw new Exception("Actions enabled during loading operation");app.action=false;
   app.quitting=true;app.Close();
  }
  Settings.Remove("backend");Settings.Remove("serverOwnership");Settings.Remove("modelId");
  File.WriteAllText(Path.Combine(DataRoot,"strata-test.txt"),"PASS: Strata JSON metrics, missing native rates, loadedfalse, endpoint-aware model/gateway readiness, external lifecycle, loading actions and offline state previews");
 }
}
