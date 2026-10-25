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
 sealed class ServerUsageSnapshot {
  internal static readonly ServerUsageSnapshot Missing=new ServerUsageSnapshot();
  internal readonly bool Available,LiveIncluded,CompletedOnly;internal readonly long Input,Output;
  ServerUsageSnapshot(){}
  internal ServerUsageSnapshot(long input,long output,bool live=false,bool completedOnly=false){Available=true;Input=input;Output=output;LiveIncluded=live;CompletedOnly=completedOnly;}
  internal decimal Cost(ApiPricing price){return price.TotalCost(Input,Output);}
  static string Compact(decimal value){decimal unit=value>=10000000000000000m?10000000000000000m:value>=1000000000000m?1000000000000m:value>=100000000m?100000000m:10000m;return value<10000m?value.ToString("0.######",System.Globalization.CultureInfo.InvariantCulture):(value/unit).ToString("#,0.##",System.Globalization.CultureInfo.InvariantCulture)+(unit==10000000000000000m?"경":unit==1000000000000m?"조":unit==100000000m?"억":"만");}
  internal string Line(ApiPricing price,bool compact=false){
   if(!Available)return "이번 서버 토큰·환산 미확인";
   decimal total=(decimal)Input+Output,won=price.Krw(Cost(price));
   string count=compact?Compact(total):total.ToString("N0",System.Globalization.CultureInfo.InvariantCulture),money=compact&&won>=10000m?Compact(won)+"원":price.Money(Cost(price));
   return "이번 서버 "+(compact?"약 ":"")+count+"토큰 · 환산 "+money+(LiveIncluded?" (진행 포함·추정)":CompletedOnly?" (완료분·병렬 미포함)":"");
  }
  internal string Hint {get{return (Available?"입력 "+Input.ToString("N0",System.Globalization.CultureInfo.InvariantCulture)+" · 출력 "+Output.ToString("N0",System.Globalization.CultureInfo.InvariantCulture)+" 토큰\n":"")+(!Available?"서버 통계 미확인 · 이전 값은 표시하지 않습니다.":LiveIncluded?"서버 완료분 + 현재 요청의 전체 예정 입력/실제 생성 토큰입니다. 프리필 중 입력은 추정입니다. 기록 누적과 합산하지 않습니다.":CompletedOnly?"완료 요청만 포함합니다. 병렬/진행 요청의 전체 합계를 확인할 수 없습니다.":"서버 시작 이후 완료분입니다. 서버 재시작 시 초기화되며 기록 누적과 별도입니다.");}}
 }
 ServerUsageSnapshot serverUsage=ServerUsageSnapshot.Missing;
 static bool ServerTokenCount(Dictionary<string,object> data,string key,out long count){
  count=0;object raw;if(data==null||!data.TryGetValue(key,out raw)||raw==null)return false;
  if(raw is double){double n=(double)raw;if(double.IsNaN(n)||double.IsInfinity(n)||n<0||n>9007199254740991d||Math.Floor(n)!=n)return false;count=(long)n;return true;}
  if(!(raw is int||raw is long||raw is decimal))return false;
  decimal value=Convert.ToDecimal(raw);if(value<0||value>long.MaxValue||decimal.Truncate(value)!=value)return false;count=(long)value;return true;
 }
 static ServerUsageSnapshot ReadServerUsage(string body,bool ready){
  if(!ready)return ServerUsageSnapshot.Missing;
  try{
   var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(body);var totals=ObjectAt(data,"totals");var live=ObjectAt(data,"live");long input,output,prompt,generated,running;
   if(!ServerTokenCount(totals,"prompt_tokens",out input)||!ServerTokenCount(totals,"output_tokens",out output)||live==null)return ServerUsageSnapshot.Missing;
   string state=Field(live,"state");bool active=state=="reading"||state=="generating";
   if(!active&&state!="idle")return ServerUsageSnapshot.Missing;
   // Parallel metrics may expose only the newest request; never add it as a complete live total.
   foreach(var part in new[]{data,ObjectAt(data,"engine"),live})if(part!=null){object flag;if(part.ContainsKey("slots")||part.ContainsKey("replicas")||(part.TryGetValue("parallel",out flag)&&(!(flag is bool)||(bool)flag)))return new ServerUsageSnapshot(input,output,false,true);}
   if(live.ContainsKey("running")){if(!ServerTokenCount(live,"running",out running))return ServerUsageSnapshot.Missing;if(running>1||(!active&&running>0))return new ServerUsageSnapshot(input,output,false,true);if(active&&running!=1)return ServerUsageSnapshot.Missing;}
   if(!active)return new ServerUsageSnapshot(input,output);
   if(!ServerTokenCount(live,"prompt_tokens",out prompt)||!ServerTokenCount(live,"generated",out generated))return ServerUsageSnapshot.Missing;
   return new ServerUsageSnapshot(checked(input+prompt),checked(output+generated),true);
  }catch{return ServerUsageSnapshot.Missing;}
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
 static Dictionary<string,object> ServerUsageFixture(object input,object output,string state,object prompt=null,object generated=null){
  return new Dictionary<string,object>{{"totals",new Dictionary<string,object>{{"since",123},{"prompt_tokens",input},{"output_tokens",output}}},{"live",new Dictionary<string,object>{{"state",state},{"prompt_tokens",prompt},{"generated",generated}}}};
 }
 static void ServerUsageTest(QwenStatus app){
  if(!OfflineTest)throw new InvalidOperationException("Server usage fixtures require isolated offline mode.");
  var json=new JavaScriptSerializer();var price=new ApiPricing();
  Func<Dictionary<string,object>,ServerUsageSnapshot> read=data=>ReadServerUsage(json.Serialize(data),true);
  var completed=read(ServerUsageFixture(1000,100,"idle"));
  var reading=read(ServerUsageFixture(1000,100,"reading",200,0));
  var generating=read(ServerUsageFixture(1000,100,"generating",200,10));
  var finalized=read(ServerUsageFixture(1200,110,"idle"));
  var next=read(ServerUsageFixture(1200,110,"reading",50,0));
  if(!completed.Available||completed.LiveIncluded||completed.CompletedOnly||completed.Input!=1000||completed.Output!=100)throw new Exception("Completed server totals changed");
  if(!reading.Available||!reading.LiveIncluded||reading.Input!=1200||reading.Output!=100||!reading.Line(price).Contains("추정")||!reading.Hint.Contains("전체 예정 입력"))throw new Exception("Prefill planned input was lost or presented as measured prefill");
  if(generating.Input!=1200||generating.Output!=110||price.Krw(generating.Cost(price)-reading.Cost(price))!=0.1325m)throw new Exception("Actual generated tokens did not increase server KRW");
  if(finalized.LiveIncluded||finalized.Input!=generating.Input||finalized.Output!=generating.Output||finalized.Cost(price)!=generating.Cost(price))throw new Exception("Finalize double-counted or dropped live usage");
  if(!next.LiveIncluded||next.Input!=1250||next.Output!=110)throw new Exception("Next request reused prior live tokens");
  var leftover=read(ServerUsageFixture(1200,110,"idle",200,10));
  if(leftover.Cost(price)!=finalized.Cost(price)||leftover.LiveIncluded)throw new Exception("Idle retained stale live counters");
  var resetData=ServerUsageFixture(5,2,"idle");ObjectAt(resetData,"totals")["since"]=456;
  var reset=read(resetData);var zero=read(ServerUsageFixture(0,0,"idle"));
  if(!reset.Available||reset.Input!=5||reset.Output!=2||reset.Cost(price)>=finalized.Cost(price)||!zero.Available||zero.Cost(price)!=0m)throw new Exception("Server epoch reset or confirmed zero was hidden");
  string activeJson=json.Serialize(ServerUsageFixture(1000,100,"generating",200,10));
  if(Metric(activeJson,"prompt_tokens_total")!=1000||Metric(activeJson,"generation_tokens_total")!=100)throw new Exception("Live estimate changed canonical completed counters");
  foreach(string invalid in new[]{"", "not JSON", "{", "null", "[]", "{}", "{\"totals\":{}}"})if(ReadServerUsage(invalid,true).Available)throw new Exception("Malformed server metrics produced a cost: "+invalid);
  if(ReadServerUsage(activeJson,false).Available)throw new Exception("Offline/unloaded server retained usage");
  foreach(string state in new[]{null,"","unloaded","unknown","queued"})if(read(ServerUsageFixture(1000,100,state,200,0)).Available)throw new Exception("Unknown live state was accepted");
  foreach(string section in new[]{"totals","live"}){var data=ServerUsageFixture(1000,100,"reading",200,0);data.Remove(section);if(read(data).Available)throw new Exception("Missing server usage section was accepted");}
  foreach(string section in new[]{"totals","live"})foreach(string key in section=="totals"?new[]{"prompt_tokens","output_tokens"}:new[]{"state","prompt_tokens","generated"}){
   var data=ServerUsageFixture(1000,100,"reading",200,0);ObjectAt(data,section).Remove(key);if(read(data).Available)throw new Exception("Missing server usage field was accepted: "+section+"."+key);
  }
  foreach(object invalid in new object[]{null,-1,-1L,-0.5m,1.5m,"42","NaN",true,long.MaxValue+1m}){
   foreach(string section in new[]{"totals","live"})foreach(string key in section=="totals"?new[]{"prompt_tokens","output_tokens"}:new[]{"prompt_tokens","generated"}){
    var data=ServerUsageFixture(1000,100,"reading",200,0);ObjectAt(data,section)[key]=invalid;if(read(data).Available)throw new Exception("Invalid server token count was accepted: "+section+"."+key);
   }
  }
  long count;
  foreach(object invalid in new object[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,1.25d,9007199254740992d})if(ServerTokenCount(new Dictionary<string,object>{{"n",invalid}},"n",out count))throw new Exception("Unsafe floating server token count accepted");
  foreach(object valid in new object[]{0,42L,42m,42d,9007199254740991d,long.MaxValue})if(!ServerTokenCount(new Dictionary<string,object>{{"n",valid}},"n",out count))throw new Exception("Exact integer server token count rejected");
  if(read(ServerUsageFixture(long.MaxValue,0,"generating",1,0)).Available||read(ServerUsageFixture(0,long.MaxValue,"generating",0,1)).Available)throw new Exception("Overflowing live counter was accepted");
  foreach(string section in new[]{"root","engine","live"})foreach(string key in new[]{"parallel","slots","replicas"}){
   var data=ServerUsageFixture(1000,100,"generating",200,10);data["engine"]=new Dictionary<string,object>();
   var part=section=="root"?data:ObjectAt(data,section);part[key]=key=="parallel"?(object)true:2;
   var partial=read(data);if(!partial.Available||!partial.CompletedOnly||partial.LiveIncluded||partial.Input!=1000||partial.Output!=100||!partial.Line(price).Contains("완료분"))throw new Exception("Partial parallel metrics claimed complete live usage");
  }
  foreach(object flag in new object[]{null,"false",2}){var data=ServerUsageFixture(1000,100,"reading",200,0);data["parallel"]=flag;if(!read(data).CompletedOnly)throw new Exception("Unknown parallel flag claimed complete live usage");}
  var serial=ServerUsageFixture(1000,100,"reading",200,0);serial["parallel"]=false;ObjectAt(serial,"live")["running"]=1;
  if(!read(serial).LiveIncluded)throw new Exception("Explicit single active request rejected");
  ObjectAt(serial,"live")["running"]=2;if(!read(serial).CompletedOnly)throw new Exception("Multiple active requests were summed as one");
  ObjectAt(serial,"live")["state"]="idle";if(!read(serial).CompletedOnly)throw new Exception("Idle with outstanding requests claimed a complete total");
  ObjectAt(serial,"live")["state"]="reading";
  foreach(object invalid in new object[]{0,-1,1.5m,null,"1",true}){ObjectAt(serial,"live")["running"]=invalid;if(read(serial).Available)throw new Exception("Invalid/contradictory running count accepted");}
  var realistic=read(ServerUsageFixture(46758530,360584,"reading",194775,0));
  if(realistic.Input!=46953305||realistic.Output!=360584||price.Krw(realistic.Cost(price))!=129203.99625m)throw new Exception("Realistic server KRW estimate incorrect");
  var huge=read(ServerUsageFixture(long.MaxValue,long.MaxValue,"idle"));
  if(!huge.Available||!huge.Line(price).Contains("18,446,744,073,709,551,614"))throw new Exception("Cross-direction token total overflowed");
  var savedPrice=ApiPricing.Current;var savedLedger=app.usageLedger;var savedSnapshot=app.serverUsage;double savedInput=app.lastServerInput,savedOutput=app.lastServerOutput;
  string directory=Path.Combine(DataRoot,"server-usage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);Directory.CreateDirectory(SharedRequests);
  try{
   ApiPricing.Current=price;
   for(int i=0;i<12;i++)File.WriteAllText(Path.Combine(directory,Guid.NewGuid()+".json"),json.Serialize(new{status="completed",usage=new{prompt_tokens=i==11?4101:4000,completion_tokens=i==11?328:100}}));
   app.usageLedger=UsageLedger.Scan(directory);app.ApplyServerUsage(completed,1000,100);
   string history=string.Join("\n",app.usage.Text.Split('\n').Take(2));
   if(!history.Contains((49529).ToString("N0"))||!history.Contains("146.39원")||app.usageLedger.Count!=12)throw new Exception("Historical usage fixture incorrect");
   Action<ServerUsageSnapshot,string> preview=(sample,name)=>{
    app.ApplyServerUsage(sample,double.NaN,double.NaN);string[] lines=app.usage.Text.Split('\n');
    if(lines.Length!=3||string.Join("\n",lines.Take(2))!=history||app.usageLedger.Input!=48101||app.usageLedger.Output!=1428||app.usageLedger.Count!=12||Directory.GetFiles(directory).Length!=12)throw new Exception("Server sample changed history or usage layout");
    if(TextRenderer.MeasureText(lines[2],app.usage.Font,Size.Empty,TextFormatFlags.SingleLine).Width>app.usage.ClientSize.Width-4||TextRenderer.MeasureText(app.usage.Text,app.usage.Font,new Size(app.usage.ClientSize.Width,1000),TextFormatFlags.WordBreak).Height>app.usage.ClientSize.Height||app.usage.Bounds.IntersectsWith(app.usageButton.Bounds))throw new Exception("Server comparison clipped the existing three-line usage area");
    if(name!=null)using(var image=new Bitmap(app.Width,app.Height)){app.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(DataRoot,"strata-server-usage-"+name+".png"));}
   };
   preview(reading,"reading");preview(generating,"generating");preview(finalized,"finalized");preview(next,null);preview(reset,"reset");preview(zero,null);preview(realistic,"realistic");
   string tip=app.usageTip.GetToolTip(app.usage);
   if(!tip.Contains("47,313,889")||!tip.Contains("46,953,305")||!tip.Contains("360,584")||!tip.Contains("전체 예정 입력")||!tip.Contains("추정")||!tip.Contains(price.Money(realistic.Cost(price))))throw new Exception("Exact server usage or prefill caveat missing from tooltip");
   preview(new ServerUsageSnapshot(1000,100,false,true),"parallel");preview(huge,"huge");
   ApiPricing.Current=new ApiPricing{InputRate=1000,OutputRate=1000,UsdToKrw=ApiPricing.MaxUsdToKrw};
   app.UpdateUsageText(app.lastServerInput,app.lastServerOutput);history=string.Join("\n",app.usage.Text.Split('\n').Take(2));preview(huge,"max-price");
   ApiPricing.Current=price;app.UpdateUsageText(app.lastServerInput,app.lastServerOutput);history=string.Join("\n",app.usage.Text.Split('\n').Take(2));
   foreach(var missing in new[]{ReadServerUsage("{",true),ReadServerUsage(activeJson,false),ServerUsageSnapshot.Missing}){
    preview(generating,null);preview(missing,"missing");if(!app.usage.Text.Split('\n')[2].Contains("미확인")||app.usageTip.GetToolTip(app.usage).Contains("진행 포함"))throw new Exception("Failed/offline/unloaded sample retained stale usage");
   }
   preview(generating,null);var same=app.serverUsage;decimal usd=same.Cost(price);
   ApiPricing.Current=new ApiPricing{InputRate=2,OutputRate=10,UsdToKrw=1400};app.UpdateUsageText(app.lastServerInput,app.lastServerOutput);
   if(!object.ReferenceEquals(same,app.serverUsage)||!app.usage.Text.Contains(ApiPricing.Current.Money(usd)))throw new Exception("FX edit did not reprice the same server sample");
   ApiPricing.Current=new ApiPricing{InputRate=3,OutputRate=12,UsdToKrw=1400};app.UpdateUsageText(app.lastServerInput,app.lastServerOutput);
   if(!object.ReferenceEquals(same,app.serverUsage)||!app.usage.Text.Contains(ApiPricing.Current.Money(same.Cost(ApiPricing.Current)))||app.usageLedger.Count!=12)throw new Exception("Rate edit did not reprice the same server sample");
   ApiPricing.Current=price;app.usageLedger=null;app.ApplyServerUsage(realistic,double.NaN,double.NaN);if(app.usage.Text.Split('\n').Length!=3)throw new Exception("Loading history collapsed the server line");
   app.usageLedger=new UsageLedger{Error="fixture"};app.UpdateUsageText(double.NaN,double.NaN);if(app.usage.Text.Split('\n').Length!=3)throw new Exception("Failed history collapsed the server line");
  }finally{
   ApiPricing.Current=savedPrice;app.usageLedger=savedLedger;app.ApplyServerUsage(savedSnapshot,savedInput,savedOutput);
   foreach(string file in Directory.GetFiles(directory))File.Delete(file);Directory.Delete(directory);
  }
 }
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
   ServerUsageTest(app);
   ScriptSettingsTest(app);app.Display(0,double.NaN,double.NaN,double.NaN,0,0);if(app.start.Enabled||app.stop.Enabled||app.startMenu.Enabled||app.stopMenu.Enabled||ExternalControlAvailable())throw new Exception("No-config external lifecycle changed after script fixtures");
   Settings["serverOwnership"]="managed";app.action=true;app.Display(1,double.NaN,double.NaN,double.NaN,0,0);if(app.start.Enabled||app.stop.Enabled)throw new Exception("Actions enabled during loading operation");app.action=false;
   app.quitting=true;app.Close();
  }
  Settings.Remove("backend");Settings.Remove("serverOwnership");Settings.Remove("modelId");
  File.WriteAllText(Path.Combine(DataRoot,"strata-test.txt"),"PASS: Strata JSON metrics, completed/live server KRW lifecycle, prefill estimate, finalize without double count, epoch reset, invalid counters, offline/unloaded stale clearing, parallel completed-only limits, same-snapshot repricing, unchanged historical ledger, three-line layout and server usage previews; missing native rates, loadedfalse, endpoint-aware model/gateway readiness, no-config external lifecycle, optional script path/argument/visibility guards, fixed external modes, native ownership, explicit boolean queue-startup/backend/install/offline guards, busy controls and offline state previews (fixture scripts never executed)");
 }
}
