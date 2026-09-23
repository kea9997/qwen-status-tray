using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

partial class QwenStatus {
 InsightsWindow insightsWindow;
 MiniWindow miniWindow;
 int miniStatus;
 double miniSpeed=double.NaN,miniRunning,miniWaiting;

 void OpenInsights(){
  if(insightsWindow==null||insightsWindow.IsDisposed)insightsWindow=new InsightsWindow(this);
  insightsWindow.RefreshUsage();insightsWindow.Show();insightsWindow.WindowState=FormWindowState.Normal;insightsWindow.Activate();
 }
 void ToggleMini(){
  if(miniWindow==null||miniWindow.IsDisposed)miniWindow=new MiniWindow();
  if(miniWindow.Visible){miniWindow.Hide();return;}
  miniWindow.UpdateState(miniStatus,Backend(),miniSpeed,miniRunning,miniWaiting,gpuText);
  miniWindow.Show();miniWindow.Activate();
 }
 void UpdateMini(int status,double speed,double running,double waiting){
  miniStatus=status;miniSpeed=speed;miniRunning=running;miniWaiting=waiting;
  if(miniWindow!=null&&!miniWindow.IsDisposed&&miniWindow.Visible)miniWindow.UpdateState(status,Backend(),speed,running,waiting,gpuText);
 }

 class MiniWindow : Form {
  internal readonly Label state=new Label();readonly Label detail=new Label(),gpu=new Label();
  bool closing;
  public MiniWindow(){
   Text="Qwen 작은 상태창";ClientSize=new Size(360,150);MinimumSize=MaximumSize=new Size(376,189);Font=new Font("Malgun Gothic",9);BackColor=Color.FromArgb(248,250,253);
   FormBorderStyle=FormBorderStyle.FixedToolWindow;ShowInTaskbar=false;TopMost=true;
   var area=Screen.PrimaryScreen.WorkingArea;Location=new Point(area.Right-Width-18,area.Bottom-Height-18);StartPosition=FormStartPosition.Manual;
   state.Bounds=new Rectangle(16,12,325,32);state.Font=new Font(Font.FontFamily,15,FontStyle.Bold);Controls.Add(state);
   detail.Bounds=new Rectangle(17,55,325,38);Controls.Add(detail);
   gpu.Bounds=new Rectangle(17,100,325,28);gpu.ForeColor=Color.DimGray;Controls.Add(gpu);
   var pin=new CheckBox{Text="항상 위",Checked=true,Bounds=new Rectangle(260,128,95,22)};pin.CheckedChanged+=(s,e)=>TopMost=pin.Checked;Controls.Add(pin);
   FormClosing+=(s,e)=>{if(!closing&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
  }
  public void Shutdown(){closing=true;Close();}
  public void UpdateState(int status,string backend,double speed,double running,double waiting,string gpuText){
   string[] names={"꺼짐","준비 중","대기","작업 중","상태 확인 필요"};
   status=Math.Max(0,Math.Min(4,status));state.Text=names[status]+" · "+backend;
   state.ForeColor=new[]{Color.SlateGray,Color.RoyalBlue,Color.SeaGreen,Color.DarkOrange,Color.Firebrick}[status];
   string rate=double.IsNaN(speed)?"속도 측정 대기":speed.ToString("0.0")+" tok/s";
   detail.Text=string.Format("{0}  ·  처리 {1:0}건 / 대기 {2:0}건",rate,double.IsNaN(running)?0:running,double.IsNaN(waiting)?0:waiting);
   gpu.Text=gpuText;
  }
 }

 partial class InsightsWindow : Form {
  readonly QwenStatus owner;
  internal readonly TabControl tabs=new TabControl();
  readonly ComboBox recent=new ComboBox(),benchmarkBackend=new ComboBox();
  readonly Panel timeline=new Panel();
  readonly Label timelineSummary=new Label(),comparisonNote=new Label(),usageSummary=new Label();
  readonly TextBox diagnostics=new TextBox();
  readonly ListView benchmarks=new ListView(),sourceList=new ListView();
  readonly PictureBox cardPreview=new PictureBox();
  readonly List<BenchRecord> benchmarkRecords=new List<BenchRecord>();
  readonly string benchmarkIndex=Path.Combine(DataRoot,"benchmark-index.json");
  string timelinePath;
  public InsightsWindow(QwenStatus app){
   owner=app;Text="Qwen 분석 센터";ClientSize=new Size(920,650);MinimumSize=new Size(760,560);Font=new Font("Malgun Gothic",9);BackColor=Color.FromArgb(245,247,250);
   tabs.Dock=DockStyle.Fill;tabs.Padding=new Point(16,8);Controls.Add(tabs);
   BuildTimeline();BuildDiagnostics();BuildBenchmarks();BuildSources();BuildCard();BuildOperations();
   LoadBenchmarks();ReloadTimeline();RefreshUsage();
   tabs.SelectedIndexChanged+=(s,e)=>{if(tabs.SelectedIndex==1)ReloadDiagnostics();if(tabs.SelectedIndex==3)RefreshUsage();if(tabs.SelectedIndex==4)RefreshCard();if(tabs.SelectedIndex==5)ReloadQueue();if(tabs.SelectedIndex==6)RefreshArchiveSources();if(tabs.SelectedIndex==7)RefreshEnergy();};
  }
  static TabPage Page(string title){return new TabPage(title){BackColor=Color.FromArgb(245,247,250),Padding=new Padding(16)};}
  static Button ActionButton(string title){return new Button{Text=title,AutoSize=true,Height=34,Margin=new Padding(0,0,10,0)};}
  static Label Hint(string text){return new Label{Text=text,Dock=DockStyle.Top,Height=44,ForeColor=Color.DimGray};}
  static string LimitStatus(string status){return status=="completed"?"완료":status=="incomplete"?"출력 한도":status=="failed"?"실패":status=="queued"?"대기":status=="running"?"진행 중":status;}
  static string Duration(double seconds){return double.IsNaN(seconds)?"—":seconds<1?(seconds*1000).ToString("0")+"ms":seconds.ToString("0.00")+"초";}

  void BuildTimeline(){
   var page=Page("요청 흐름");tabs.TabPages.Add(page);
   var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42};page.Controls.Add(bar);
   recent.Width=520;recent.DropDownStyle=ComboBoxStyle.DropDownList;recent.SelectedIndexChanged+=(s,e)=>ShowTimeline();bar.Controls.Add(recent);
   var refresh=ActionButton("새로고침");refresh.Click+=(s,e)=>ReloadTimeline();bar.Controls.Add(refresh);
   timelineSummary.Dock=DockStyle.Top;timelineSummary.Height=82;timelineSummary.Padding=new Padding(4,10,4,0);timelineSummary.Font=new Font(Font.FontFamily,10);page.Controls.Add(timelineSummary);
   timeline.Dock=DockStyle.Fill;timeline.BackColor=Color.White;timeline.Paint+=(s,e)=>DrawTimeline(e.Graphics,timeline.ClientRectangle);page.Controls.Add(timeline);
   page.Controls.SetChildIndex(timeline,0);page.Controls.SetChildIndex(timelineSummary,1);page.Controls.SetChildIndex(bar,2);
  }
  void ReloadTimeline(){
   string selected=timelinePath;recent.BeginUpdate();recent.Items.Clear();
   try{if(Directory.Exists(SharedRequests))foreach(var file in new DirectoryInfo(SharedRequests).GetFiles("*.json").Where(f=>Regex.IsMatch(f.Name,@"^[a-fA-F0-9-]{36}\.json$")).OrderByDescending(f=>f.CreationTimeUtc).Take(100))recent.Items.Add(new TimelineItem{Path=file.FullName,Title=file.CreationTime.ToString("MM-dd HH:mm:ss")+" · "+file.Name.Substring(0,8)});}
   catch(Exception ex){timelineSummary.Text="기록 확인 실패: "+ex.Message;}
   finally{recent.EndUpdate();}
   int index=-1;for(int i=0;i<recent.Items.Count;i++)if(((TimelineItem)recent.Items[i]).Path==selected){index=i;break;}
   if(recent.Items.Count>0)recent.SelectedIndex=index>=0?index:0;else {timelinePath=null;timelineSummary.Text="공통 대기열 요청 기록이 없습니다.";timeline.Invalidate();}
  }
  class TimelineItem {public string Path,Title;public override string ToString(){return Title;}}
  void ShowTimeline(){
   var item=recent.SelectedItem as TimelineItem;if(item==null)return;timelinePath=item.Path;
   try{var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(item.Path));
    var u=d.ContainsKey("usage")?d["usage"] as Dictionary<string,object>:null;
    string source=SourceName(Field(d,"source")),status=LimitStatus(Field(d,"status"));
    double input=Number(u,"prompt_tokens"),output=Number(u,"completion_tokens"),queue=Number(d,"queue_seconds"),first=Number(d,"first_token_seconds"),generation=Number(d,"generation_seconds");
    timelineSummary.Text=string.Format("{0} · {1} · 입력 {2} / 출력 {3} 토큰\n대기 {4} · 첫 토큰 {5} · 생성 {6} · 생성 속도 {7}",source,status,double.IsNaN(input)?"—":input.ToString("N0"),double.IsNaN(output)?"—":output.ToString("N0"),Duration(queue),Duration(first),Duration(generation),!double.IsNaN(output)&&!double.IsNaN(generation)&&generation>0?(output/generation).ToString("0.0")+" tok/s":"—");
   }catch(Exception ex){timelineSummary.Text="기록을 읽지 못했습니다: "+ex.Message;}
   timeline.Invalidate();
  }
  void DrawTimeline(Graphics g,Rectangle bounds){
   g.Clear(Color.White);if(timelinePath==null)return;
   try{var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(timelinePath));
    var labels=new[]{"공통 대기열","첫 토큰 준비","답변 생성"};var values=new[]{Number(d,"queue_seconds"),Number(d,"first_token_seconds"),Number(d,"generation_seconds")};var colors=new[]{Color.FromArgb(90,126,204),Color.FromArgb(232,165,65),Color.FromArgb(61,164,119)};
    double max=values.Where(v=>!double.IsNaN(v)&&v>=0).DefaultIfEmpty(1).Max();if(max<=0)max=1;
    using(var font=new Font("Malgun Gothic",11))using(var brush=new SolidBrush(Color.FromArgb(38,48,64))){
     int width=Math.Max(80,bounds.Width-250);
     for(int i=0;i<3;i++){int y=42+i*93;g.DrawString(labels[i],font,brush,18,y);g.FillRectangle(Brushes.Gainsboro,170,y-2,width,28);
      if(!double.IsNaN(values[i])&&values[i]>=0){using(var fill=new SolidBrush(colors[i]))g.FillRectangle(fill,170,y-2,Math.Max(2,(int)(width*values[i]/max)),28);g.DrawString(Duration(values[i]),font,brush,178,y+34);}
      else g.DrawString("측정 없음",font,brush,178,y+34);
     }
     using(var small=new Font("Malgun Gothic",9))g.DrawString("각 구간은 별도 측정값입니다. 길이는 이 요청에서 가장 긴 구간을 기준으로 표시합니다.",small,brush,18,335);
    }
   }catch(Exception ex){g.DrawString("타임라인 표시 실패: "+ex.Message,Font,Brushes.Firebrick,16,20);}
  }

  void BuildDiagnostics(){
   var page=Page("연결 진단");tabs.TabPages.Add(page);page.Controls.Add(diagnostics);
   diagnostics.Dock=DockStyle.Fill;diagnostics.Multiline=true;diagnostics.ReadOnly=true;diagnostics.WordWrap=true;diagnostics.ScrollBars=ScrollBars.Vertical;diagnostics.Font=new Font("Consolas",11);diagnostics.BackColor=Color.White;
   var refresh=ActionButton("다시 확인");refresh.Dock=DockStyle.Top;refresh.Click+=(s,e)=>ReloadDiagnostics();page.Controls.Add(refresh);
  }
  async void ReloadDiagnostics(){
   diagnostics.Text="서버와 공통 대기열을 확인하는 중…";
   string result=await Task.Run(()=>Diagnose());if(!IsDisposed)diagnostics.Text=result;
  }
  static string Probe(string url){
   try{var request=(HttpWebRequest)WebRequest.Create(url);request.Timeout=2000;request.ReadWriteTimeout=2000;
    using(var response=(HttpWebResponse)request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream())){string body=reader.ReadToEnd();bool notReady=false;
     try{var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(body);notReady=data.ContainsKey("ready")&&!Convert.ToBoolean(data["ready"]);}catch{}
     return "HTTP "+(int)response.StatusCode+(notReady?" · 준비 안 됨":"");}}
   catch(WebException ex){return ex.Status==WebExceptionStatus.ConnectFailure?"연결 안 됨":ex.Status==WebExceptionStatus.Timeout?"응답 지연":ex.Message;}
   catch(Exception ex){return ex.Message;}
  }
  static string QueueSnapshot(){
   try{var request=(HttpWebRequest)WebRequest.Create(QueueUrl);request.Timeout=2000;request.ReadWriteTimeout=2000;
    using(var response=request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream())){
     var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
     return string.Format("처리 {0:0}건 · 대기 {1:0}건",Number(d,"running"),Number(d,"waiting"));
    }
   }catch{return "요청 통계를 읽지 못했습니다.";}
  }
  static string Diagnose(){
   var lines=new List<string>();string server=Probe(ServerUrl+"health"),queueHealth;
   try{queueHealth=Probe(new Uri(new Uri(QueueUrl).GetLeftPart(UriPartial.Authority)+"/health").AbsoluteUri);}catch(Exception ex){queueHealth="설정 오류: "+ex.Message;}
   lines.Add("Qwen 서버: "+server);lines.Add("공통 대기열: "+queueHealth);
   if(queueHealth=="HTTP 200")lines.Add("대기열 요청: "+QueueSnapshot());
   lines.Add("선택한 백엔드: "+Backend());lines.Add("요청 기록: "+(Directory.Exists(SharedRequests)?"연결됨":"폴더 없음"));
   lines.Add("서버 시작 스크립트: "+(File.Exists(Path.Combine(Root,"start.sh"))?"있음":"없음 (수동 실행만 가능)"));
   lines.Add("서버 종료 스크립트: "+(File.Exists(Path.Combine(Root,"stop.sh"))?"있음":"없음 (수동 종료만 가능)"));
   if(server=="HTTP 200"&&queueHealth!="HTTP 200")lines.Add("\r\n진단: 모델은 살아 있지만 공통 대기열이 준비되지 않았습니다. 대기열 상태를 확인하세요.");
   else if(server!="HTTP 200"&&queueHealth=="HTTP 200")lines.Add("\r\n진단: 대기열은 살아 있지만 모델 서버가 응답하지 않습니다. 모델이 꺼졌거나 준비 중일 수 있습니다.");
   else if(server=="HTTP 200"&&queueHealth=="HTTP 200")lines.Add("\r\n진단: 기본 연결이 정상입니다.");
   else lines.Add("\r\n진단: 두 연결 모두 응답하지 않습니다. 서버 상태를 확인하세요.");
   lines.Add("\r\n이 화면은 서버를 시작하거나 중지하지 않습니다. 인증 토큰과 대화 원문도 표시하지 않습니다.");
   return string.Join("\r\n",lines);
  }

  class BenchRecord {public string Path {get;set;}public string Backend {get;set;}public string Added {get;set;}}
  public class BenchSummary {public string Profile,Status;public int Runs,RecallPassed,RecallTotal;public double WarmupTtft=double.NaN,MeanTtft=double.NaN,MeanSpeed=double.NaN,MaxInput=double.NaN;}
  void BuildBenchmarks(){
   var page=Page("백엔드 비교");tabs.TabPages.Add(page);
   var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42};page.Controls.Add(bar);
   benchmarkBackend.DropDownStyle=ComboBoxStyle.DropDownList;benchmarkBackend.Width=110;benchmarkBackend.Items.AddRange(new object[]{"vLLM","ninfer"});benchmarkBackend.SelectedItem=Backend();bar.Controls.Add(benchmarkBackend);
   var test=ActionButton("토큰 테스트 열기");test.Click+=(s,e)=>{if(owner!=null)owner.OpenTokenTest();};bar.Controls.Add(test);
   var add=ActionButton("최근 테스트 결과 등록");add.Click+=(s,e)=>AddLatestBenchmark();bar.Controls.Add(add);
   var remove=ActionButton("선택 기록 제거");remove.Click+=(s,e)=>RemoveBenchmark();bar.Controls.Add(remove);
   comparisonNote.Dock=DockStyle.Top;comparisonNote.Height=82;comparisonNote.ForeColor=Color.DimGray;comparisonNote.Text="서버를 수동으로 켜고 같은 프로필을 각각 테스트한 뒤 결과를 등록하세요. 워밍업 행은 실제 콜드 스타트를 보장하지 않습니다.";page.Controls.Add(comparisonNote);
   benchmarks.Dock=DockStyle.Fill;benchmarks.View=View.Details;benchmarks.FullRowSelect=true;benchmarks.GridLines=true;benchmarks.HideSelection=false;
   foreach(var column in new[]{new ColumnHeader{Text="등록 시각",Width=122},new ColumnHeader{Text="백엔드",Width=78},new ColumnHeader{Text="프로필",Width=78},new ColumnHeader{Text="워밍업 첫 토큰",Width=126},new ColumnHeader{Text="측정 첫 토큰",Width=125},new ColumnHeader{Text="측정 생성 속도",Width=134},new ColumnHeader{Text="최대 입력",Width=90},new ColumnHeader{Text="상태",Width=95}})benchmarks.Columns.Add(column);
   page.Controls.Add(benchmarks);page.Controls.SetChildIndex(benchmarks,0);page.Controls.SetChildIndex(comparisonNote,1);page.Controls.SetChildIndex(bar,2);
  }
  void LoadBenchmarks(){
   try{if(File.Exists(benchmarkIndex)){var rows=new JavaScriptSerializer().Deserialize<List<BenchRecord>>(File.ReadAllText(benchmarkIndex));if(rows!=null)benchmarkRecords.AddRange(rows.Where(x=>x!=null&&x.Path!=null));}}
   catch(Exception ex){comparisonNote.Text="비교 기록을 읽지 못했습니다: "+ex.Message;}
   RefreshBenchmarks();
  }
  void SaveBenchmarks(){Directory.CreateDirectory(DataRoot);File.WriteAllText(benchmarkIndex,new JavaScriptSerializer().Serialize(benchmarkRecords));}
  void AddLatestBenchmark(){
   string directory=Path.Combine(GatewayRoot,"token-tests");
   if(!Directory.Exists(directory)){MessageBox.Show("토큰 테스트 결과 폴더가 없습니다. 먼저 토큰 테스트를 실행하세요.","Qwen");return;}
   var file=new DirectoryInfo(directory).GetFiles("*.json").OrderByDescending(x=>x.LastWriteTimeUtc).FirstOrDefault();
   if(file==null){MessageBox.Show("등록할 JSON 결과가 없습니다.","Qwen");return;}
   try{var summary=ReadBenchmark(file.FullName);string backend=benchmarkBackend.SelectedItem.ToString();
    if(benchmarkRecords.Any(x=>string.Equals(x.Path,file.FullName,StringComparison.OrdinalIgnoreCase))){MessageBox.Show("이 결과는 이미 등록되어 있습니다.","Qwen");return;}
    var answer=MessageBox.Show(string.Format("{0}\n{1} · {2} · 측정 {3}회\n\n이 결과를 {4} 기록으로 등록할까요?",file.Name,file.LastWriteTime.ToString("g"),summary.Profile,summary.Runs,backend),"토큰 테스트 결과 등록",MessageBoxButtons.YesNo);
    if(answer!=DialogResult.Yes)return;
    benchmarkRecords.Add(new BenchRecord{Path=file.FullName,Backend=backend,Added=DateTime.Now.ToString("yyyy-MM-dd HH:mm")});SaveBenchmarks();RefreshBenchmarks();
   }catch(Exception ex){MessageBox.Show("테스트 결과를 읽지 못했습니다: "+ex.Message,"Qwen");}
  }
  void RemoveBenchmark(){var row=benchmarks.SelectedItems.Count>0?benchmarks.SelectedItems[0]:null;if(row==null)return;var record=row.Tag as BenchRecord;if(record==null)return;benchmarkRecords.Remove(record);SaveBenchmarks();RefreshBenchmarks();}
  void RefreshBenchmarks(){
   benchmarks.BeginUpdate();benchmarks.Items.Clear();int missing=0;var profiles=new HashSet<string>();var backends=new HashSet<string>();
   foreach(var record in benchmarkRecords.OrderByDescending(x=>x.Added)){
    if(!File.Exists(record.Path)){missing++;continue;}
    try{var s=ReadBenchmark(record.Path);profiles.Add(s.Profile);backends.Add(record.Backend);
     var row=new ListViewItem(new[]{record.Added,record.Backend,s.Profile,BenchNumber(s.WarmupTtft,"초"),BenchNumber(s.MeanTtft,"초"),BenchNumber(s.MeanSpeed," tok/s"),double.IsNaN(s.MaxInput)?"—":s.MaxInput.ToString("N0"),s.Status});row.Tag=record;benchmarks.Items.Add(row);
    }catch{missing++;}
   }
   benchmarks.EndUpdate();
   comparisonNote.Text=benchmarkRecords.Count==0?"서버를 수동으로 켜고 같은 프로필을 각각 테스트한 뒤 결과를 등록하세요. 워밍업은 실제 콜드 스타트를 보장하지 않습니다.":
    string.Format("등록 {0}건 · 표시 {1}건{2}. {3}\n입력 길이·모델·서버 설정과 프로필이 같아야 공정하게 비교할 수 있습니다. 워밍업은 콜드 스타트 보장이 아닙니다.",benchmarkRecords.Count,benchmarks.Items.Count,missing>0?" · 결과 파일 없음/손상 "+missing+"건":"",backends.Count>=2&&profiles.Count==1?"두 백엔드의 같은 프로필이 있습니다.":"조건을 맞춘 두 백엔드 기록이 필요합니다.");
  }
  static string BenchNumber(double value,string suffix){return double.IsNaN(value)?"—":value.ToString("0.0")+suffix;}
  static double Value(Dictionary<string,object> data,string key){return Number(data,key);}
  public static BenchSummary ReadBenchmark(string path){
   var d=new JavaScriptSerializer{MaxJsonLength=16*1024*1024}.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
   if(!d.ContainsKey("rows"))throw new InvalidDataException("rows 필드가 없습니다.");
   var rows=d["rows"] as IEnumerable;if(rows==null)throw new InvalidDataException("rows 형식이 잘못됐습니다.");
   var s=new BenchSummary{Profile=Field(d,"profile"),Status=Field(d,"status")};double ttft=0,speed=0;int ttftCount=0,speedCount=0;
   foreach(var item in rows){var row=item as Dictionary<string,object>;if(row==null||Field(row,"status")!="completed")continue;
    bool warm=row.ContainsKey("warmup")&&Convert.ToBoolean(row["warmup"]);double t=Value(row,"ttft_seconds"),v=Value(row,"decode_tok_s"),input=Value(row,"input_tokens");
    if(!double.IsNaN(input)&&(double.IsNaN(s.MaxInput)||input>s.MaxInput))s.MaxInput=input;
    if(warm){if(!double.IsNaN(t))s.WarmupTtft=t;continue;}
    if(row.ContainsKey("recall_pass")&&row["recall_pass"]!=null){s.RecallTotal++;if(Convert.ToBoolean(row["recall_pass"]))s.RecallPassed++;}
    if(row.ContainsKey("quality_only")&&Convert.ToBoolean(row["quality_only"]))continue;
    s.Runs++;if(!double.IsNaN(t)){ttft+=t;ttftCount++;}if(!double.IsNaN(v)){speed+=v;speedCount++;}
   }
   if(ttftCount>0)s.MeanTtft=ttft/ttftCount;if(speedCount>0)s.MeanSpeed=speed/speedCount;
   return s;
  }

  void BuildSources(){
   var page=Page("출처별 사용량");tabs.TabPages.Add(page);
   var bar=ActionButton("집계 새로고침");bar.Dock=DockStyle.Top;bar.Click+=(s,e)=>RefreshUsage();page.Controls.Add(bar);
   usageSummary.Dock=DockStyle.Top;usageSummary.Height=68;usageSummary.ForeColor=Color.DimGray;page.Controls.Add(usageSummary);
   sourceList.Dock=DockStyle.Fill;sourceList.View=View.Details;sourceList.FullRowSelect=true;sourceList.GridLines=true;
   foreach(var col in new[]{new ColumnHeader{Text="출처",Width=160},new ColumnHeader{Text="요청",Width=66},new ColumnHeader{Text="입력 토큰",Width=118},new ColumnHeader{Text="출력 토큰",Width=118},new ColumnHeader{Text="오늘",Width=105},new ColumnHeader{Text="최근 7일",Width=118},new ColumnHeader{Text="전체",Width=118}})sourceList.Columns.Add(col);
   page.Controls.Add(sourceList);page.Controls.SetChildIndex(sourceList,0);page.Controls.SetChildIndex(usageSummary,1);page.Controls.SetChildIndex(bar,2);
  }
  public void RefreshUsage(){
   if(sourceList.IsDisposed)return;var ledger=owner==null?null:owner.usageLedger;
   sourceList.BeginUpdate();sourceList.Items.Clear();
   if(ledger==null){usageSummary.Text="요청 기록을 계산하는 중입니다.";sourceList.EndUpdate();return;}
   foreach(var s in ledger.SourceStats())sourceList.Items.Add(new ListViewItem(new[]{SourceName(s.Name),s.Count.ToString("N0"),s.Input.ToString("N0"),s.Output.ToString("N0"),s.Today.ToString("N0"),s.Week.ToString("N0"),(s.Input+s.Output).ToString("N0")}));
   sourceList.EndUpdate();usageSummary.Text=string.Format("기록된 요청 {0:N0}건 · 입력 {1:N0} / 출력 {2:N0} 토큰\n'연결 앱'은 기존 기록만으로 Hermes·Codex 등을 더 세분할 수 없습니다. 토큰 테스트도 별도 출처로 표시합니다.",ledger.Count,ledger.Input,ledger.Output);
  }
  public void OnLedgerUpdated(){if(tabs.SelectedIndex==3)RefreshUsage();else if(tabs.SelectedIndex==4)RefreshCard();else if(tabs.SelectedIndex==6)RefreshArchiveSources();}

  void BuildCard(){
   var page=Page("공유 카드");tabs.TabPages.Add(page);
   var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=43};page.Controls.Add(bar);
   var refresh=ActionButton("미리보기 갱신");refresh.Click+=(s,e)=>RefreshCard();bar.Controls.Add(refresh);
   var save=ActionButton("PNG 저장");save.Click+=(s,e)=>SaveCard();bar.Controls.Add(save);
   var hint=Hint("로컬 Qwen 처리량만 표시합니다. OpenAI 토큰 절약량이나 대화 원문은 포함하지 않습니다.");page.Controls.Add(hint);
   cardPreview.Dock=DockStyle.Fill;cardPreview.SizeMode=PictureBoxSizeMode.Zoom;cardPreview.BackColor=Color.FromArgb(223,230,241);page.Controls.Add(cardPreview);
   page.Controls.SetChildIndex(cardPreview,0);page.Controls.SetChildIndex(hint,1);page.Controls.SetChildIndex(bar,2);
  }
  void RefreshCard(){var image=RenderCard(owner==null?null:owner.usageLedger);var previous=cardPreview.Image;cardPreview.Image=image;if(previous!=null)previous.Dispose();}
  void SaveCard(){
   using(var dialog=new SaveFileDialog{Filter="PNG 이미지|*.png",DefaultExt="png",FileName="qwen-local-usage-"+DateTime.Today.ToString("yyyyMMdd")+".png",InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),OverwritePrompt=true}){
    if(dialog.ShowDialog(this)!=DialogResult.OK)return;
    using(var image=RenderCard(owner==null?null:owner.usageLedger))image.Save(dialog.FileName,System.Drawing.Imaging.ImageFormat.Png);
    MessageBox.Show("공유 카드를 저장했습니다.","Qwen");
   }
  }
  public static Bitmap RenderCard(UsageLedger ledger){
   var bitmap=new Bitmap(1200,675);using(var g=Graphics.FromImage(bitmap)){
    g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAlias;
    using(var background=new LinearGradientBrush(new Rectangle(0,0,1200,675),Color.FromArgb(13,28,55),Color.FromArgb(31,78,110),35f))g.FillRectangle(background,0,0,1200,675);
    using(var titleFont=new Font("Malgun Gothic",34,FontStyle.Bold))using(var numberFont=new Font("Malgun Gothic",48,FontStyle.Bold))using(var smallFont=new Font("Malgun Gothic",18))using(var tinyFont=new Font("Malgun Gothic",14)){
     g.DrawString("Qwen · 로컬 AI 기록",titleFont,Brushes.White,62,46);
     g.DrawString(DateTime.Today.ToString("yyyy.MM.dd")+" 기준 · 공통 대기열 기록",tinyFont,Brushes.LightSkyBlue,65,125);
     long total=ledger==null?0:ledger.Input+ledger.Output;g.DrawString(total.ToString("N0"),numberFont,Brushes.White,61,184);
     g.DrawString("처리한 토큰",smallFont,Brushes.LightSkyBlue,67,278);
     string[] captions={"입력","출력","요청","최고 생성 속도","최장 입력"};
     string[] values={ledger==null?"—":ledger.Input.ToString("N0"),ledger==null?"—":ledger.Output.ToString("N0"),ledger==null?"—":ledger.Count.ToString("N0"),ledger==null||double.IsNaN(ledger.PeakSpeed())?"—":ledger.PeakSpeed().ToString("0.0")+" tok/s",ledger==null?"—":ledger.LongestInput().ToString("N0")+" 토큰"};
     for(int i=0;i<5;i++){int x=i<3?65+i*360:65+(i-3)*540,y=i<3?369:475;g.DrawString(captions[i],tinyFont,Brushes.LightSkyBlue,x,y);g.DrawString(values[i],smallFont,Brushes.White,x,y+32);}
     g.DrawString("로컬 모델 처리량입니다 · OpenAI 토큰 절약량이 아닙니다 · 테스트 요청 포함",tinyFont,Brushes.Gainsboro,65,616);
    }
   }return bitmap;
  }
 }

 static void InsightsTest(){
  string directory=Path.Combine(Path.GetTempPath(),"qwen-insights-"+Guid.NewGuid());Directory.CreateDirectory(directory);
  try{
   File.WriteAllText(Path.Combine(directory,Guid.NewGuid()+".json"),"{\"source\":\"direct-chat\",\"created_at\":\"2026-09-23T00:00:00+09:00\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":50},\"generation_seconds\":2}");
   File.WriteAllText(Path.Combine(directory,Guid.NewGuid()+".json"),"{\"source\":\"token-test\",\"created_at\":\"2026-09-23T00:00:00+09:00\",\"usage\":{\"prompt_tokens\":200,\"completion_tokens\":20},\"generation_seconds\":1}");
   File.WriteAllText(Path.Combine(directory,Guid.NewGuid()+".json"),"{\"source\":\"hermes-agent\",\"created_at\":\"2026-09-23T00:00:00+09:00\",\"usage\":{\"prompt_tokens\":40,\"completion_tokens\":10},\"generation_seconds\":1}");
   var ledger=UsageLedger.Scan(directory);var rows=ledger.SourceStats();if(rows.Length!=3||ledger.Input!=340||ledger.Output!=80||ledger.LongestInput()!=200||ledger.PeakSpeed()!=25||SourceName("hermes-agent")!="Hermes 에이전트")throw new Exception("Source usage summary failed");
   string benchmark=Path.Combine(directory,"benchmark.json");File.WriteAllText(benchmark,"{\"profile\":\"quick\",\"status\":\"completed\",\"rows\":[{\"warmup\":true,\"status\":\"completed\",\"ttft_seconds\":2,\"input_tokens\":80},{\"warmup\":false,\"status\":\"completed\",\"ttft_seconds\":1,\"decode_tok_s\":40,\"input_tokens\":100}]}");
   var result=InsightsWindow.ReadBenchmark(benchmark);if(result.Runs!=1||result.WarmupTtft!=2||result.MeanTtft!=1||result.MeanSpeed!=40||result.MaxInput!=100)throw new Exception("Benchmark summary failed");
   Directory.CreateDirectory(DataRoot);using(var card=InsightsWindow.RenderCard(ledger))card.Save(Path.Combine(DataRoot,"insights-card-preview.png"));
   File.WriteAllText(Path.Combine(DataRoot,"insights-test.txt"),"PASS: source usage, benchmark summary, share card");
  }finally{Directory.Delete(directory,true);}
 }
 static void InsightsUiTest(){
  Directory.CreateDirectory(DataRoot);
  string fixtureDir=Path.Combine(Path.GetTempPath(),"qwen-insights-ui-"+Guid.NewGuid());Directory.CreateDirectory(fixtureDir);
  try{File.WriteAllText(Path.Combine(fixtureDir,Guid.NewGuid()+".json"),"{\"source\":\"direct-chat\",\"created_at\":\""+DateTimeOffset.Now.ToString("o")+"\",\"usage\":{\"prompt_tokens\":1200,\"completion_tokens\":320},\"generation_seconds\":8}");
  using(var app=new QwenStatus()){
   app.usageLedger=UsageLedger.Scan(fixtureDir);
   app.OpenInsights();Application.DoEvents();
   if(app.insightsWindow==null||app.insightsWindow.tabs.TabPages.Count!=9)throw new Exception("Analysis tabs not created");
   using(var image=new Bitmap(app.insightsWindow.Width,app.insightsWindow.Height)){app.insightsWindow.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(DataRoot,"insights-ui-preview.png"));}
   foreach(int tab in new[]{1,2,3,4,5,6,7,8}){
    app.insightsWindow.tabs.SelectedIndex=tab;Application.DoEvents();
    using(var image=new Bitmap(app.insightsWindow.Width,app.insightsWindow.Height)){app.insightsWindow.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(DataRoot,"insights-tab-"+tab+".png"));}
   }
   app.ToggleMini();app.UpdateMini(3,42,1,2);Application.DoEvents();
   if(app.miniWindow==null||!app.miniWindow.Visible||!app.miniWindow.state.Text.Contains("작업 중"))throw new Exception("Mini panel failed");
   using(var image=new Bitmap(app.miniWindow.Width,app.miniWindow.Height)){app.miniWindow.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(DataRoot,"mini-preview.png"));}
   app.quitting=true;app.Close();
  }}finally{Directory.Delete(fixtureDir,true);}
  File.WriteAllText(Path.Combine(DataRoot,"insights-ui-test.txt"),"PASS: nine tabs, mini panel, window rendering");
 }
}
