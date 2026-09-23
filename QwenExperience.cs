using System;
using System.Drawing;
using System.Windows.Forms;
using System.Net;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

partial class QwenStatus {
 static readonly string NotifyPreference=Path.Combine(DataRoot,"notify-important.txt");
 bool notifyEnabled=LoadNotifyPreference();int lastAlertState=-1,offlineChecks,hotChecks;DateTime suppressOfflineUntil=DateTime.MinValue;
 readonly Dictionary<string,DateTime> alerted=new Dictionary<string,DateTime>();
 SetupWindow setupWindow;
 static bool LoadNotifyPreference(){try{return !File.Exists(NotifyPreference)||File.ReadAllText(NotifyPreference).Trim()!="off";}catch{return true;}}
 void SetNotifyPreference(bool enabled){notifyEnabled=enabled;try{Directory.CreateDirectory(DataRoot);File.WriteAllText(NotifyPreference,enabled?"on":"off");}catch(Exception ex){MessageBox.Show(ex.Message,"알림 설정 저장 실패");}}
 void NotifyOnce(string key,string title,string message,ToolTipIcon icon){
  if(!notifyEnabled||!tray.Visible)return;DateTime old;if(alerted.TryGetValue(key,out old)&&(DateTime.UtcNow-old).TotalMinutes<30)return;
  alerted[key]=DateTime.UtcNow;if(alerted.Count>500)foreach(var expired in alerted.Where(x=>(DateTime.UtcNow-x.Value).TotalHours>1).Select(x=>x.Key).ToArray())alerted.Remove(expired);
  tray.BalloonTipTitle=title;tray.BalloonTipText=message;tray.BalloonTipIcon=icon;tray.ShowBalloonTip(4500);
 }
 void CheckImportantEvents(int status){
  if(status==0&&(lastAlertState==2||lastAlertState==3))offlineChecks=1;
  else if(status==0&&offlineChecks>0)offlineChecks++;
  else if(status!=0)offlineChecks=0;
  if(offlineChecks==3&&DateTime.UtcNow>=suppressOfflineUntil)NotifyOnce("server-down","Qwen 서버 연결 끊김","모델 서버가 응답하지 않습니다. 서버 시작·종료는 수동입니다.",ToolTipIcon.Warning);
  if((status==2||status==3)&&(lastAlertState==0||lastAlertState==1))NotifyOnce("server-ready","Qwen 서버 준비 완료","모델이 요청을 받을 수 있습니다.",ToolTipIcon.Info);
  lastAlertState=status;
 }
 void ObserveGpuHeat(GpuReading reading){if(!double.IsNaN(reading.Temperature)&&reading.Temperature>=88)hotChecks++;else hotChecks=0;
  if(hotChecks==2)NotifyOnce("gpu-hot","GPU 온도 높음","GPU 온도가 88°C 이상입니다. 냉각 상태를 확인하세요.",ToolTipIcon.Warning);
 }
 void OpenSetup(){if(setupWindow==null||setupWindow.IsDisposed)setupWindow=new SetupWindow(this);setupWindow.Show();setupWindow.WindowState=FormWindowState.Normal;setupWindow.Activate();}
 static void OpenSupport(){try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/kea9997/qwen-status-tray/blob/main/SUPPORT.md"){UseShellExecute=true});}catch(Exception ex){MessageBox.Show(ex.Message,"프로젝트 안내 열기 실패");}}
 static bool IsLoopbackHttp(string text,string path){Uri uri;return Uri.TryCreate(text,UriKind.Absolute,out uri)&&uri.Scheme=="http"&&(uri.Host=="127.0.0.1"||uri.Host=="localhost")&&uri.AbsolutePath.TrimEnd('/').Equals(path,StringComparison.OrdinalIgnoreCase)&&uri.UserInfo==""&&uri.Query==""&&uri.Fragment=="";}
 class SetupWindow : Form {
  readonly TextBox serverBox=new TextBox(),queueBox=new TextBox(),folderBox=new TextBox();readonly Label result=new Label();
  public SetupWindow(QwenStatus owner=null){
   Text="Qwen 첫 연결 설정";ClientSize=new Size(720,510);MinimumSize=new Size(680,480);Font=new Font("Malgun Gothic",10);BackColor=Color.FromArgb(245,247,250);
   Controls.Add(new Label{Text="서버와 공통 대기열 연결",Bounds=new Rectangle(24,18,640,37),Font=new Font("Malgun Gothic",17,FontStyle.Bold)});
   Controls.Add(new Label{Text="모델 서버 URL",Bounds=new Rectangle(26,76,220,25)});serverBox.Bounds=new Rectangle(26,103,660,30);serverBox.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;serverBox.Text=ServerUrl.TrimEnd('/');Controls.Add(serverBox);
   Controls.Add(new Label{Text="공통 대기열 URL",Bounds=new Rectangle(26,145,220,25)});queueBox.Bounds=new Rectangle(26,172,660,30);queueBox.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;queueBox.Text=QueueUrl;Controls.Add(queueBox);
   Controls.Add(new Label{Text="공통 대기열 폴더",Bounds=new Rectangle(26,214,220,25)});folderBox.Bounds=new Rectangle(26,241,660,30);folderBox.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;folderBox.Text=GatewayRoot;Controls.Add(folderBox);
   var check=new Button{Text="연결 확인",Bounds=new Rectangle(26,290,145,36)};check.Click+=(s,e)=>CheckConnection();Controls.Add(check);
   var save=new Button{Text="설정 저장",Bounds=new Rectangle(184,290,145,36)};save.Click+=(s,e)=>SaveSettings();Controls.Add(save);
   var delegation=new Button{Text="AI 위임 설정",Bounds=new Rectangle(342,290,160,36),Enabled=owner!=null};delegation.Click+=(s,e)=>{if(owner!=null)owner.OpenDelegation();};Controls.Add(delegation);
   var install=new Button{Text="설치 도우미",Bounds=new Rectangle(516,290,170,36),Enabled=owner!=null};install.Click+=(s,e)=>{if(owner!=null)owner.OpenInstaller();};Controls.Add(install);
   result.Bounds=new Rectangle(26,345,660,145);result.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;result.ForeColor=Color.FromArgb(45,55,75);
   result.Text="처음이라면 설치 도우미에서 공통 대기열·모델 서버를 준비하세요.\n서버만 연결해도 상태·GPU 지표는 볼 수 있습니다.\n연결 확인은 서버를 자동으로 시작하지 않습니다.";Controls.Add(result);
  }
  static string ProbeLocal(string url,string tokenFile=null){
   try{var request=(HttpWebRequest)WebRequest.Create(url);request.Timeout=2000;request.ReadWriteTimeout=2000;if(tokenFile!=null)request.Headers["x-ui-token"]=File.ReadAllText(tokenFile).Trim();
    using(var response=(HttpWebResponse)request.GetResponse())return "연결됨 (HTTP "+(int)response.StatusCode+")";
   }catch(WebException ex){return ex.Status==WebExceptionStatus.ConnectFailure?"연결되지 않음":ex.Status==WebExceptionStatus.Timeout?"응답 지연":"연결 확인 필요: "+ex.Status;}catch(Exception ex){return "확인 실패: "+ex.Message;}
  }
  async void CheckConnection(){
   if(!IsLoopbackHttp(serverBox.Text.Trim().TrimEnd('/')+"/health","/health")){result.Text="모델 서버는 로컬 HTTP 기본 주소여야 합니다.";return;}
   if(!IsLoopbackHttp(queueBox.Text,"/queue")){result.Text="공통 대기열은 로컬 HTTP /queue 주소여야 합니다.";return;}
   result.Text="연결 확인 중…";string server=serverBox.Text.Trim().TrimEnd('/')+"/health",queue=queueBox.Text.Trim(),folder=folderBox.Text.Trim();
   string status=await Task.Run(()=>{string model=ProbeLocal(server),gateway=ProbeLocal(queue),details="사용 불가";
    string tokenFile=Path.Combine(folder,"ui-token.txt");if(File.Exists(tokenFile))details=ProbeLocal(new Uri(new Uri(queue).GetLeftPart(UriPartial.Authority)+"/ui/queue").AbsoluteUri,tokenFile);
    return "모델: "+model+"\n공통 대기열: "+gateway+"\n요청별 대기열: "+details+"\n기록 폴더: "+(Directory.Exists(Path.Combine(folder,"requests"))?"연결됨":"없음")+"\n\n대기열이 없다면 상태·GPU 지표만 사용할 수 있습니다.";});
   if(!IsDisposed)result.Text=status;
  }
  void SaveSettings(){
   string server=serverBox.Text.Trim().TrimEnd('/')+"/",queue=queueBox.Text.Trim();
   if(!IsLoopbackHttp(server+"health","/health")||!IsLoopbackHttp(queue,"/queue")){MessageBox.Show("로컬 HTTP 모델 서버와 /queue 주소를 확인하세요.","Qwen");return;}
   try{string folder=Path.GetFullPath(Environment.ExpandEnvironmentVariables(folderBox.Text.Trim()));
    var values=File.Exists(Path.Combine(DataRoot,"settings.json"))?new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(DataRoot,"settings.json"))):new Dictionary<string,object>();
    values["serverUrl"]=server;values["queueUrl"]=queue;values["uiUrl"]=new Uri(new Uri(queue).GetLeftPart(UriPartial.Authority)+"/ui").AbsoluteUri;values["gatewayDirectory"]=folder;
    Directory.CreateDirectory(DataRoot);File.WriteAllText(Path.Combine(DataRoot,"settings.json"),new JavaScriptSerializer().Serialize(values));
    result.Text="설정을 저장했습니다. 상태 앱을 재시작하면 적용됩니다. Qwen 서버는 그대로 둡니다.";
   }catch(Exception ex){MessageBox.Show(ex.Message,"설정 저장 실패");}
  }
 }

 class TrendRow {public DateTime Day;public string Source,Backend;public int Count,Failures;public readonly List<double> Queue=new List<double>(),Ttft=new List<double>(),Speed=new List<double>();}
 static double Percentile(IEnumerable<double> source,double fraction){var values=source.Where(x=>!double.IsNaN(x)&&x>=0).OrderBy(x=>x).ToArray();return values.Length==0?double.NaN:values[Math.Max(0,Math.Min(values.Length-1,(int)Math.Ceiling(values.Length*fraction)-1))];}
 static TrendRow[] ReadTrends(string directory,int days){
  var groups=new Dictionary<string,TrendRow>();if(!Directory.Exists(directory))return new TrendRow[0];DateTime cutoff=DateTime.Today.AddDays(1-days);
  foreach(var file in Directory.EnumerateFiles(directory,"*.json")){
   if(!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file),@"^[a-fA-F0-9-]{36}\.json$"))continue;
   try{var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));DateTimeOffset created;
    if(!DateTimeOffset.TryParse(Field(d,"created_at"),out created))continue;DateTime day=created.LocalDateTime.Date;if(day<cutoff||day>DateTime.Today)continue;
    string status=Field(d,"status");if(status=="queued"||status=="running")continue;
    string source=Field(d,"source"),backend=Field(d,"backend");if(string.IsNullOrEmpty(backend))backend="미기록";
    string key=day.ToString("yyyy-MM-dd")+"|"+source+"|"+backend;TrendRow row;
    if(!groups.TryGetValue(key,out row)){row=new TrendRow{Day=day,Source=source,Backend=backend};groups[key]=row;}
    row.Count++;if(status=="failed"||status=="cancelled")row.Failures++;
    double q=Number(d,"queue_seconds"),t=Number(d,"first_token_seconds"),generation=Number(d,"generation_seconds");
    if(!double.IsNaN(q))row.Queue.Add(q);if(!double.IsNaN(t))row.Ttft.Add(t);
    var usage=d.ContainsKey("usage")?d["usage"] as Dictionary<string,object>:null;double output=Number(usage,"completion_tokens");
    if(!double.IsNaN(output)&&!double.IsNaN(generation)&&generation>0)row.Speed.Add(output/generation);
   }catch(IOException){}catch(ArgumentException){}catch(InvalidOperationException){}
  }
  return groups.Values.OrderByDescending(x=>x.Day).ThenBy(x=>x.Source).ToArray();
 }
 static void ExperienceTest(){
  if(!IsLoopbackHttp("http://127.0.0.1:18022/queue","/queue")||IsLoopbackHttp("https://example.com/queue","/queue"))throw new Exception("Setup URL validation failed");
  string folder=Path.Combine(Path.GetTempPath(),"qwen-trends-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  try{string today=DateTimeOffset.Now.ToString("o");
   File.WriteAllText(Path.Combine(folder,Guid.NewGuid()+".json"),"{\"created_at\":\""+today+"\",\"status\":\"completed\",\"source\":\"direct-chat\",\"backend\":\"vLLM\",\"queue_seconds\":1,\"first_token_seconds\":2,\"generation_seconds\":4,\"usage\":{\"completion_tokens\":40}}");
   File.WriteAllText(Path.Combine(folder,Guid.NewGuid()+".json"),"{\"created_at\":\""+today+"\",\"status\":\"failed\",\"source\":\"direct-chat\",\"backend\":\"vLLM\",\"queue_seconds\":3,\"first_token_seconds\":4}");
   var rows=ReadTrends(folder,7);if(rows.Length!=1||rows[0].Count!=2||rows[0].Failures!=1||Percentile(rows[0].Queue,.95)!=3||Percentile(rows[0].Speed,.5)!=10)throw new Exception("Trend aggregation failed");
  }finally{Directory.Delete(folder,true);}
  using(var app=new QwenStatus()){
   app.notifyEnabled=false;app.CheckImportantEvents(2);app.CheckImportantEvents(0);app.CheckImportantEvents(0);app.CheckImportantEvents(0);
   app.ObserveGpuHeat(new GpuReading{Temperature=89});app.ObserveGpuHeat(new GpuReading{Temperature=89});
   if(app.offlineChecks!=3||app.hotChecks!=2)throw new Exception("Important-event debounce failed");
   app.quitting=true;app.Close();
  }
  Directory.CreateDirectory(DataRoot);using(var window=new SetupWindow()){window.CreateControl();using(var image=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(DataRoot,"setup-preview.png"));}}
  File.WriteAllText(Path.Combine(DataRoot,"experience-test.txt"),"PASS: setup validation, setup window, trend aggregation and alert debounce");
 }

 partial class InsightsWindow {
  readonly ComboBox trendDays=new ComboBox();readonly Label trendNote=new Label();readonly ListView trendList=new ListView();internal bool trendLoading;
  void BuildTrends(){
   var page=Page("성능 추세");tabs.TabPages.Add(page);var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42};page.Controls.Add(bar);
   trendDays.DropDownStyle=ComboBoxStyle.DropDownList;trendDays.Width=120;trendDays.Items.AddRange(new object[]{"최근 7일","최근 30일"});trendDays.SelectedIndex=0;bar.Controls.Add(trendDays);
   var refresh=ActionButton("추세 계산");refresh.Click+=(s,e)=>ReloadTrends();bar.Controls.Add(refresh);
   trendNote.Dock=DockStyle.Top;trendNote.Height=59;trendNote.ForeColor=Color.DimGray;trendNote.Text="요청 기록의 대기·첫 토큰·생성 속도를 날짜와 출처별로 집계합니다. 과거 기록의 백엔드가 없으면 미기록으로 표시합니다.";page.Controls.Add(trendNote);
   trendList.Dock=DockStyle.Fill;trendList.View=View.Details;trendList.FullRowSelect=true;trendList.GridLines=true;
   foreach(var c in new[]{new ColumnHeader{Text="날짜",Width=95},new ColumnHeader{Text="출처",Width=150},new ColumnHeader{Text="백엔드",Width=85},new ColumnHeader{Text="요청",Width=60},new ColumnHeader{Text="대기 P95",Width=92},new ColumnHeader{Text="첫 토큰 P95",Width=105},new ColumnHeader{Text="생성 중앙값",Width=116},new ColumnHeader{Text="실패/취소",Width=85}})trendList.Columns.Add(c);
   page.Controls.Add(trendList);page.Controls.SetChildIndex(trendList,0);page.Controls.SetChildIndex(trendNote,1);page.Controls.SetChildIndex(bar,2);
  }
  async void ReloadTrends(){if(trendLoading)return;trendLoading=true;trendNote.Text="요청 기록을 계산하는 중…";
   try{int days=trendDays.SelectedIndex==1?30:7;var rowsTask=Task.Run(()=>ReadTrends(SharedRequests,days));var metricTask=Task.Run(()=>TrendMetrics());await Task.WhenAll(rowsTask,metricTask);var rows=rowsTask.Result;if(IsDisposed)return;
    trendList.BeginUpdate();trendList.Items.Clear();foreach(var row in rows){double q=Percentile(row.Queue,.95),t=Percentile(row.Ttft,.95),s=Percentile(row.Speed,.5);
     trendList.Items.Add(new ListViewItem(new[]{row.Day.ToString("yyyy-MM-dd"),SourceName(row.Source),row.Backend,row.Count.ToString("N0"),double.IsNaN(q)?"—":q.ToString("0.00")+"초",double.IsNaN(t)?"—":t.ToString("0.00")+"초",double.IsNaN(s)?"—":s.ToString("0.0")+" tok/s",row.Failures.ToString("N0")}));}
    trendList.EndUpdate();trendNote.Text=days+"일 · "+rows.Sum(x=>x.Count).ToString("N0")+"건. "+metricTask.Result+"\n요청 수가 적은 날의 P95는 흔들릴 수 있습니다. 정리된 원문 기록은 제외됩니다.";
   }catch(Exception ex){if(!IsDisposed)trendNote.Text="추세 계산 실패: "+ex.Message;}finally{trendLoading=false;}
  }
  static string TrendMetrics(){
   try{string body=Get("metrics");double kv=Metric(body,"gpu_cache_usage_perc"),preempt=Metric(body,"num_preemptions_total"),hits=Metric(body,"prefix_cache_hits"),queries=Metric(body,"prefix_cache_queries");
    if(double.IsNaN(kv)&&double.IsNaN(preempt)&&double.IsNaN(hits)&&double.IsNaN(queries))return "현재 백엔드의 캐시·선점 지표는 미제공";
    return "현재 KV "+(double.IsNaN(kv)?"—":(kv*100).ToString("0.#")+"%")+" · 선점 "+(double.IsNaN(preempt)?"—":preempt.ToString("N0"))+"회 · 접두 캐시 "+(double.IsNaN(hits)||double.IsNaN(queries)||queries<=0?"—":(hits/queries*100).ToString("0.#")+"%");
   }catch{return "현재 서버 지표를 읽지 못했습니다.";}
  }
 }
}
