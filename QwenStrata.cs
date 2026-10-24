using System;
using System.Collections.Generic;
using System.Diagnostics;
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
 static bool HealthLoaded(string body){return Backend()=="strata"&&HealthLoaded(body,true,Setting("modelId",""));}
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
  if(Backend()!="strata")return new string[0];
  double capacity;double.TryParse(Setting("contextTokens","0"),out capacity);
  var result=new List<string>{"quick"};string[] names={"8k","32k","64k","224k"};int[] targets={7680,32256,65024,228864};for(int i=0;i<names.Length;i++)if(targets[i]+512<=capacity)result.Add(names[i]);return result.ToArray();
 }
 internal static void RuntimeEnvironment(System.Diagnostics.ProcessStartInfo info){
  string backend=Backend();if(backend!="strata")throw new InvalidOperationException("Strata만 지원합니다. 설치 도우미에서 새 전용 폴더에 연결을 준비하세요.");info.EnvironmentVariables["QWEN_BACKEND"]=backend;info.EnvironmentVariables["QWEN_BACKEND_FILE"]=BackendFile;
  info.EnvironmentVariables["QWEN_QUEUE_URL"]=new Uri(QueueUrl).GetLeftPart(UriPartial.Authority);info.EnvironmentVariables["QWEN_UPSTREAM_URL"]=ServerUrl.TrimEnd('/');
  info.EnvironmentVariables["QWEN_MODEL_ID"]=Setting("modelId","");info.EnvironmentVariables["QWEN_CONTEXT_TOKENS"]=Setting("contextTokens","");info.EnvironmentVariables["QWEN_GATEWAY_DATA_DIR"]=GatewayRoot;
  string receipt=Path.Combine(ManagedRoot(),"setup-state.json");
  if(File.Exists(receipt)){var c=ObjectAt(new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(receipt)),"connection");if(c!=null&&Field(c,"backend")==backend){info.EnvironmentVariables["QWEN_MODEL_ID"]=Field(c,"model");info.EnvironmentVariables["QWEN_UPSTREAM_URL"]=Field(c,"upstream");info.EnvironmentVariables["QWEN_CONTEXT_TOKENS"]=Field(c,"contextTokens");}}
 }
 double reportedContext=double.NaN,nativeInputSpeed=double.NaN;string reportedModel="";
 static void ScriptSettingsTest(QwenStatus app){
  string[] keys={"hermesLaunchScript","externalControllerScript","setupPowerShell","serverOwnership","backend","managedInstallRoot","startQueueWithApp"};
  var prior=keys.Where(Settings.ContainsKey).ToDictionary(key=>key,key=>Settings[key]);
  string directory=Path.Combine(DataRoot,"script fixtures "+Guid.NewGuid().ToString("N")),script=Path.Combine(directory,"existing launcher.PS1"),other=Path.Combine(directory,"not-a-script.txt"),owned=Path.Combine(directory,"owned");
  Directory.CreateDirectory(Path.Combine(owned,"setup"));
  File.WriteAllText(script,"# Offline path fixture only. Never execute.");File.WriteAllText(other,"Offline path fixture only.");
  File.WriteAllText(Path.Combine(owned,"setup-state.json"),"{}");File.WriteAllText(Path.Combine(owned,"setup","setup.ps1"),"# Offline ownership fixture only. Never execute.");
  Action<Action,string> rejects=(check,message)=>{bool refused=false;try{check();}catch(ArgumentException){refused=true;}if(!refused)throw new Exception(message);};
  try{
   Settings["backend"]="strata";Settings["serverOwnership"]="external";Settings["setupPowerShell"]="fixture-shell.exe";Settings["managedInstallRoot"]=owned;
   Settings.Remove("startQueueWithApp");object enabled;Settings.TryGetValue("startQueueWithApp",out enabled);
   if(QueueStartupAllowed(enabled,Backend(),ManagedInstall(),false))throw new Exception("Queue startup enabled by default");
   foreach(object value in new object[]{null,false,"true","false",1,0})if(QueueStartupAllowed(value,Backend(),ManagedInstall(),false))throw new Exception("Queue startup accepted non-boolean opt-in or false");
   Settings["startQueueWithApp"]=true;Settings.TryGetValue("startQueueWithApp",out enabled);
   if(!ManagedInstall()||!QueueStartupAllowed(enabled,Backend(),ManagedInstall(),false))throw new Exception("Explicit queue startup opt-in rejected for valid external attachment");
   if(QueueStartupAllowed(enabled,"unsupported",true,false)||QueueStartupAllowed(enabled,Backend(),false,false)||QueueStartupAllowed(enabled,Backend(),ManagedInstall(),true)||QueueStartupAllowed(enabled,Backend(),ManagedInstall(),OfflineTest))throw new Exception("Queue startup ignored backend/install/offline guards");
   Settings["managedInstallRoot"]=directory;if(ManagedInstall()||QueueStartupAllowed(enabled,Backend(),ManagedInstall(),false))throw new Exception("Queue startup accepted incomplete install");Settings["managedInstallRoot"]=owned;
   if(app.queueStartupChecked)throw new Exception("Queue startup was scheduled by offline constructor");
   Settings.Remove("hermesLaunchScript");Settings.Remove("externalControllerScript");
   if(HermesLaunchInfo()!=null||ExternalControlAvailable())throw new Exception("Optional scripts enabled without explicit settings");
   app.Display(0,double.NaN,double.NaN,double.NaN,0,0);if(app.start.Enabled||app.stop.Enabled||app.startMenu.Enabled||app.stopMenu.Enabled)throw new Exception("No-config external controls enabled by managed receipt");
   Settings["hermesLaunchScript"]="";Settings["externalControllerScript"]="";
   if(HermesLaunchInfo()!=null||ExternalControlAvailable())throw new Exception("Empty optional scripts enabled");
   foreach(string invalid in new[]{Path.GetFileName(script),@"C:launcher.ps1",@"\launcher.ps1",Path.Combine(directory,"missing.ps1"),directory,other,script+"\" -Mode auto",script+"'",script+"\r",script+"\n",script+" -Mode paused"}){
    Settings["hermesLaunchScript"]=invalid;Settings["externalControllerScript"]=invalid;
    rejects(()=>{HermesLaunchInfo();},"Invalid Hermes script accepted: "+invalid);
    rejects(()=>{ExternalControllerInfo("auto");},"Invalid controller script accepted: "+invalid);
    if(ExternalControlAvailable())throw new Exception("Invalid external controller enabled lifecycle");
    app.Display(0,double.NaN,double.NaN,double.NaN,0,0);
    if(app.start.Enabled||app.stop.Enabled||app.startMenu.Enabled||app.stopMenu.Enabled)throw new Exception("Invalid script enabled external controls despite managed receipt");
   }
   Settings["hermesLaunchScript"]=script;Settings["externalControllerScript"]=script;
   var hermes=HermesLaunchInfo();
   if(hermes.FileName!="fixture-shell.exe"||hermes.Arguments!="-NoLogo -NoProfile -NoExit -File \""+script+"\""||hermes.WorkingDirectory!=directory||hermes.UseShellExecute||hermes.CreateNoWindow||hermes.WindowStyle!=ProcessWindowStyle.Normal)throw new Exception("Explicit Hermes terminal arguments/visibility changed");
   foreach(string mode in new[]{"auto","paused"}){
    var info=ExternalControllerInfo(mode);
    if(info.FileName!="fixture-shell.exe"||info.Arguments!="-NoProfile -File \""+script+"\" -Mode "+mode||info.WorkingDirectory!=directory||info.UseShellExecute||!info.CreateNoWindow||info.WindowStyle!=ProcessWindowStyle.Hidden)throw new Exception("External controller arguments/visibility changed");
   }
   foreach(string invalid in new[]{"","Auto","stop","auto -Mode paused","paused\r\n","paused; whoami"})rejects(()=>{ExternalControllerInfo(invalid);},"Unsupported controller mode accepted");
   if(!ExternalControlAvailable()||HealthLoaded("{\"service\":\"strata\",\"model\":\"fixture-flash-next\",\"max_context\":32768,\"loaded\":false}"))throw new Exception("Controller availability fabricated model readiness");
   foreach(int status in new[]{0,1,2,3,4}){
    app.Display(status,double.NaN,double.NaN,double.NaN,0,0);
    if(!app.start.Enabled||!app.stop.Enabled||!app.startMenu.Enabled||!app.stopMenu.Enabled||!app.start.Text.Contains("자동 관리 재개")||!app.stop.Text.Contains("자동 관리 일시정지")||app.startMenu.Text!=app.start.Text||app.stopMenu.Text!=app.stop.Text||!app.note.Text.Contains("loaded"))throw new Exception("Explicit external lifecycle UI incorrect");
    if(status==1&&!app.state.Text.StartsWith("준비 중"))throw new Exception("Unloaded external model shown ready");
    using(var image=new Bitmap(app.Width,app.Height)){app.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(DataRoot,"strata-external-controller-"+status+".png"));}
   }
   app.action=true;app.Display(1,double.NaN,double.NaN,double.NaN,0,0);
   if(app.start.Enabled||app.stop.Enabled||app.startMenu.Enabled||app.stopMenu.Enabled||!app.note.Text.Contains("응답 대기"))throw new Exception("External controls enabled while busy");app.action=false;
   Settings["serverOwnership"]="managed";
   if(ExternalControlAvailable())throw new Exception("External controller used for native ownership");
   bool refused=false;try{ExternalControllerInfo("auto");}catch(InvalidOperationException){refused=true;}if(!refused)throw new Exception("Native install accepted external controller");
   foreach(int status in new[]{0,2}){app.Display(status,double.NaN,double.NaN,double.NaN,0,0);if(app.start.Enabled!=(status==0)||app.stop.Enabled!=(status!=0)||app.start.Text.Contains("자동 관리"))throw new Exception("Native owned lifecycle changed");}
   Settings["backend"]="unsupported";app.Display(0,double.NaN,double.NaN,double.NaN,0,0);if(ExternalControlAvailable()||app.start.Enabled||app.stop.Enabled)throw new Exception("Controller enabled unsupported backend");
  }finally{app.action=false;foreach(string key in keys){if(prior.ContainsKey(key))Settings[key]=prior[key];else Settings.Remove(key);}}
 }
 static void StrataTest(){
  const string idle="{\"engine\":{\"model\":\"fixture-flash-next\",\"max_context\":32768},\"live\":{\"state\":\"idle\",\"queued\":0,\"tok_s\":null},\"totals\":{\"prompt_tokens\":123,\"output_tokens\":45}}";
  if(Metric(idle,"generation_tokens_total")!=45||Metric(idle,"prompt_tokens_total")!=123||Metric(idle,"num_requests_running")!=0||!double.IsNaN(Metric(idle,"generation_tokens_per_second"))||!double.IsNaN(Metric(idle,"gpu_cache_usage_perc")))throw new Exception("Strata JSON mapping fabricated or lost metrics");
  const string health="{\"service\":\"strata\",\"model\":\"fixture-flash-next\",\"max_context\":32768,\"loaded\":true}";
  if(!HealthLoaded(health,true,"fixture-flash-next")||HealthLoaded(health.Replace("true","false"),true,"fixture-flash-next"))throw new Exception("Reachable unloaded Strata treated as ready");
  foreach(string invalid in new[]{"", "not JSON", "{", "null", "{}", "{\"loaded\":true}",health.Replace("strata","foreign"),health.Replace("32768","null"),health.Replace("32768","\"32768\""),health.Replace("32768","true"),health.Replace("true","\"true\""),health.Replace("fixture-flash-next","wrong-model")})if(HealthLoaded(invalid,true,"fixture-flash-next"))throw new Exception("Invalid/foreign/wrong-model health treated as Strata ready: "+invalid);
  if(HealthLoaded("ok",true,"")||HealthLoaded("{\"status\":\"ok\"}",true,""))throw new Exception("Non-Strata health accepted");
  Settings["backend"]="strata";Settings["serverOwnership"]="external";Settings["modelId"]="fixture-flash-next";
  Settings["contextTokens"]=32768;if(!TokenProfiles().SequenceEqual(new[]{"quick","8k","32k"}))throw new Exception("Token profiles ignored verified Strata context");
  Settings["contextTokens"]=262144;if(TokenProfiles().Length!=5)throw new Exception("Large Strata profiles missing");
  Settings["backend"]="unsupported";if(TokenProfiles().Length!=0||HealthLoaded(health))throw new Exception("Unsupported backend accepted");
  bool refused=false;try{RuntimeEnvironment(new System.Diagnostics.ProcessStartInfo());}catch(InvalidOperationException){refused=true;}if(!refused)throw new Exception("Unsupported runtime environment accepted");
  Settings["backend"]="strata";
  foreach(string gateway in new[]{"{\"ready\":true}","{\"running\":0,\"waiting\":0,\"ui_running\":0}","{\"running\":1,\"waiting\":2,\"ui_running\":1}","{\"active\":null,\"pending\":[]}"})if(!ProbeReady(gateway,false))throw new Exception("Healthy gateway treated as unloaded Strata: "+gateway);
  foreach(string gateway in new[]{"{\"ready\":false}","{\"ready\":\"true\"}","{","null","not JSON",""})if(ProbeReady(gateway,false))throw new Exception("Invalid/not-ready gateway accepted: "+gateway);
  if(!ProbeReady(health,true))throw new Exception("Valid configured Strata health rejected by diagnostics");
  foreach(string invalid in new[]{"{\"ready\":true}","{\"running\":0,\"waiting\":0}","{",health.Replace("strata","foreign"),health.Replace("fixture-flash-next","wrong-model"),health.Replace("true","false")})if(ProbeReady(invalid,true))throw new Exception("Invalid/foreign/unloaded model accepted by diagnostics: "+invalid);
  using(var app=new QwenStatus()){
   app.reportedModel="fixture-flash-next";app.reportedContext=32768;
   foreach(int s in new[]{2,1,4,0,3}){
    app.Display(s,123,45,double.NaN,s==3?1:0,0);
    if(app.start.Enabled||app.stop.Enabled||app.startMenu.Enabled||app.stopMenu.Enabled)throw new Exception("External Strata lifecycle enabled");
    if(!app.note.Text.Contains(32768.ToString("N0")))throw new Exception("Actual Strata context missing from status");
    app.Show();Application.DoEvents();using(var image=new Bitmap(app.Width,app.Height)){app.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(DataRoot,"strata-status-"+s+".png"));}
   }
   ScriptSettingsTest(app);app.Display(0,double.NaN,double.NaN,double.NaN,0,0);if(app.start.Enabled||app.stop.Enabled||app.startMenu.Enabled||app.stopMenu.Enabled||ExternalControlAvailable())throw new Exception("No-config external lifecycle changed after script fixtures");
   Settings["serverOwnership"]="managed";app.action=true;app.Display(1,double.NaN,double.NaN,double.NaN,0,0);if(app.start.Enabled||app.stop.Enabled)throw new Exception("Actions enabled during loading operation");app.action=false;
   app.quitting=true;app.Close();
  }
  Settings.Remove("backend");Settings.Remove("serverOwnership");Settings.Remove("modelId");
  File.WriteAllText(Path.Combine(DataRoot,"strata-test.txt"),"PASS: Strata JSON metrics, missing native rates, loadedfalse, endpoint-aware model/gateway readiness, no-config external lifecycle, optional script path/argument/visibility guards, fixed external modes, native ownership, explicit boolean queue-startup/backend/install/offline guards, busy controls and offline state previews (fixture scripts never executed)");
 }
}
