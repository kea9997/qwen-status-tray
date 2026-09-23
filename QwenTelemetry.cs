using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

partial class QwenStatus {
 static class Nvml {
  [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] public static extern int nvmlInit_v2();
  [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] public static extern int nvmlShutdown();
  [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index,out IntPtr handle);
  [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] public static extern int nvmlDeviceGetPowerUsage(IntPtr handle,out uint milliwatts);
 }
 class PowerPoint {public DateTime At;public double Watts;public bool Idle;}
 class EnergyRecord {public string Id,Source;public DateTime At;public long Output;public double GrossJ,NetJ,Seconds;public int Samples;}
 readonly List<PowerPoint> powerSamples=new List<PowerPoint>();
 readonly Dictionary<string,DateTime> energyPending=new Dictionary<string,DateTime>(StringComparer.OrdinalIgnoreCase);
 readonly ConcurrentQueue<string> energyChanged=new ConcurrentQueue<string>();
 readonly List<EnergyRecord> energyRecords=new List<EnergyRecord>();
 System.Windows.Forms.Timer powerTimer;IntPtr nvmlHandle;bool nvmlReady,powerBusy;double latestPower=double.NaN;
 void StartPowerMonitor(){
  try{if(Nvml.nvmlInit_v2()==0){nvmlReady=Nvml.nvmlDeviceGetHandleByIndex_v2(0,out nvmlHandle)==0;if(!nvmlReady)Nvml.nvmlShutdown();}}
  catch(DllNotFoundException){}catch(EntryPointNotFoundException){}catch(BadImageFormatException){}
  if(!nvmlReady)return;
  powerTimer=new System.Windows.Forms.Timer{Interval=250};powerTimer.Tick+=(s,e)=>SamplePower();powerTimer.Start();SamplePower();
 }
 void StopPowerMonitor(){if(powerTimer!=null){powerTimer.Stop();powerTimer.Dispose();powerTimer=null;}if(nvmlReady){try{Nvml.nvmlShutdown();}catch{}nvmlReady=false;}}
 void SamplePower(){
  if(!nvmlReady)return;uint milliwatts;
  try{if(Nvml.nvmlDeviceGetPowerUsage(nvmlHandle,out milliwatts)!=0)return;}catch{return;}
  DateTime now=DateTime.UtcNow;latestPower=milliwatts/1000.0;
  powerSamples.Add(new PowerPoint{At=now,Watts=latestPower,Idle=!powerBusy});
  while(powerSamples.Count>0&&(now-powerSamples[0].At).TotalMinutes>10)powerSamples.RemoveAt(0);
 }
 static double IdleBaseline(IList<PowerPoint> samples,DateTime at){
  var values=samples.Where(x=>x.Idle&&x.At<=at&&(at-x.At).TotalMinutes<=3).Select(x=>x.Watts).OrderBy(x=>x).ToArray();
  if(values.Length<8)return double.NaN;
  // Lower quartile is robust against the UI learning that a request started one poll late.
  return values[Math.Min(values.Length-1,values.Length/4)];
 }
 static double AtPower(IList<PowerPoint> samples,DateTime at){
  for(int i=1;i<samples.Count;i++)if(samples[i].At>=at){
   var a=samples[i-1];var b=samples[i];double span=(b.At-a.At).TotalSeconds;
   if(span<=0||span>1||(at-a.At).TotalSeconds>0.75||(b.At-at).TotalSeconds>0.75)return double.NaN;
   return a.Watts+(b.Watts-a.Watts)*(at-a.At).TotalSeconds/span;
  }
  return double.NaN;
 }
 static double IntegratePower(IList<PowerPoint> samples,DateTime start,DateTime end,double baseline,out int count){
  count=0;if(end<=start||samples.Count<2)return double.NaN;
  double begin=AtPower(samples,start),finish=AtPower(samples,end);if(double.IsNaN(begin)||double.IsNaN(finish))return double.NaN;
  var points=new List<PowerPoint>{new PowerPoint{At=start,Watts=begin}};
  foreach(var p in samples)if(p.At>start&&p.At<end){points.Add(p);count++;}
  points.Add(new PowerPoint{At=end,Watts=finish});double joules=0;
  for(int i=1;i<points.Count;i++){
   double a=double.IsNaN(baseline)?points[i-1].Watts:Math.Max(0,points[i-1].Watts-baseline);
   double b=double.IsNaN(baseline)?points[i].Watts:Math.Max(0,points[i].Watts-baseline);
   joules+=(a+b)/2*(points[i].At-points[i-1].At).TotalSeconds;
  }
  return joules;
 }
 void ProcessEnergyRequests(){
  string path;int added=0;while(added++<100&&energyChanged.TryDequeue(out path))if(Regex.IsMatch(Path.GetFileName(path),@"^[a-fA-F0-9-]{36}\.json$")&&!energyPending.ContainsKey(path))energyPending[path]=DateTime.UtcNow;
  foreach(var item in energyPending.ToArray()){
   if((DateTime.UtcNow-item.Value).TotalMinutes>35){energyPending.Remove(item.Key);continue;}
   try{
    var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(item.Key));
    string status=Field(data,"status");if(status=="queued"||status=="running")continue;
    DateTimeOffset finished;double duration=Number(data,"request_seconds");
    if(status=="failed")NotifyOnce("request-"+Path.GetFileNameWithoutExtension(item.Key),"Qwen 요청 실패","요청이 실패했습니다. 최근 작업 내역에서 원인을 확인하세요.",ToolTipIcon.Warning);
    if(!DateTimeOffset.TryParse(Field(data,"finished_at"),out finished)||double.IsNaN(duration)||duration<=0){energyPending.Remove(item.Key);continue;}
    DateTime end=finished.UtcDateTime;if((DateTime.UtcNow-end).TotalSeconds<1)continue;
    energyPending.Remove(item.Key);
    var usage=data.ContainsKey("usage")?data["usage"] as Dictionary<string,object>:null;double output=Number(usage,"completion_tokens");
    if(double.IsNaN(output)||output<=0)continue;
    DateTime start=end.AddSeconds(-duration);double idle=IdleBaseline(powerSamples,start);int samples;
    double gross=IntegratePower(powerSamples,start,end,double.NaN,out samples);if(double.IsNaN(gross))continue;
    double net=double.IsNaN(idle)?double.NaN:IntegratePower(powerSamples,start,end,idle,out samples);
    energyRecords.Add(new EnergyRecord{Id=Path.GetFileNameWithoutExtension(item.Key),Source=Field(data,"source"),At=end.ToLocalTime(),Output=(long)output,GrossJ=gross,NetJ=net,Seconds=duration,Samples=samples});
    if(energyRecords.Count>100)energyRecords.RemoveAt(0);
    if(insightsWindow!=null&&!insightsWindow.IsDisposed&&insightsWindow.Visible&&insightsWindow.tabs.SelectedIndex==7)insightsWindow.RefreshEnergy();
   }catch(IOException){}catch(ArgumentException){}catch(InvalidOperationException){}
  }
 }
 string EnergySummary(){
  string supply=SystemInformation.PowerStatus.PowerLineStatus==PowerLineStatus.Online?"전원 연결":SystemInformation.PowerStatus.PowerLineStatus==PowerLineStatus.Offline?"배터리 사용":"전원 상태 확인 불가";
  string current=double.IsNaN(latestPower)?"전력 센서 사용 불가":latestPower.ToString("0.0")+" W";
  string temperature=lastGpuReading==null||double.IsNaN(lastGpuReading.Temperature)?"—":lastGpuReading.Temperature.ToString("0")+"°C";
  var measured=energyRecords.Where(x=>!double.IsNaN(x.NetJ)).ToArray();long tokens=measured.Sum(x=>x.Output);double net=measured.Sum(x=>x.NetJ);
  string efficiency=tokens>0?(net/tokens).ToString("0.0")+" J/출력 토큰":"측정 대기";
  return "현재 상태: "+supply+" · GPU "+current+" · "+temperature+"\n요청별 유휴 전력 제외 효율: "+efficiency+" · 측정 "+measured.Length+"건\n\n250ms 간격 GPU 전력 샘플을 요청 시간과 맞춰 계산합니다. 센서 또는 요청 로그가 없거나 샘플이 부족하면 값을 만들지 않습니다. GPU 전력만 포함한 추정치입니다.";
 }
 static void TelemetryTest(){
  DateTime t=new DateTime(2026,9,23,0,0,0,DateTimeKind.Utc);var samples=new List<PowerPoint>();
  for(int i=0;i<16;i++)samples.Add(new PowerPoint{At=t.AddMilliseconds(i*250),Watts=i<8?10:50,Idle=i<8});
  double baseline=IdleBaseline(samples,t.AddSeconds(2));int count;
  double gross=IntegratePower(samples,t.AddSeconds(2),t.AddSeconds(3),double.NaN,out count);
  double net=IntegratePower(samples,t.AddSeconds(2),t.AddSeconds(3),baseline,out count);
  if(baseline!=10||Math.Abs(gross-50)>.01||Math.Abs(net-40)>.01||count<3)throw new Exception("Per-request energy integration failed");
  if(!double.IsNaN(IntegratePower(samples,t.AddSeconds(-2),t.AddSeconds(-1),baseline,out count)))throw new Exception("Uncovered interval should not be estimated");
 }
}
