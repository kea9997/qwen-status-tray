using System;
using System.Drawing;
using System.Windows.Forms;
using System.Net;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Text;

partial class QwenStatus {
 static readonly string ArchiveFile=Path.Combine(DataRoot,"archived-usage.json");
 class GpuReading {public string Text;public double Watts=double.NaN,Temperature=double.NaN;}
 GpuReading lastGpuReading;
 static void OperationsTest(){
  string root=Path.Combine(Path.GetTempPath(),"qwen-operations-"+Guid.NewGuid().ToString("N"));
  string requests=Path.Combine(root,"requests"),archive=Path.Combine(root,"archived-usage.json");Directory.CreateDirectory(requests);
  try{
   string oldId=Guid.NewGuid().ToString(),newId=Guid.NewGuid().ToString();
   string oldFile=Path.Combine(requests,oldId+".json"),newFile=Path.Combine(requests,newId+".json");
   File.WriteAllText(oldFile,"{\"status\":\"completed\",\"created_at\":\"2020-01-01T00:00:00+09:00\",\"source\":\"direct-chat\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20},\"generation_seconds\":2}");
   File.WriteAllText(Path.Combine(requests,oldId+".request.txt"),"private prompt");
   File.WriteAllText(newFile,"{\"status\":\"completed\",\"created_at\":\""+DateTimeOffset.Now.ToString("o")+"\",\"source\":\"token-test\",\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":10}}");
   var before=UsageLedger.Scan(requests,archive);var preview=UsageLedger.PreviewArchive(requests,archive,DateTime.Today.AddDays(-30),"direct-chat");
   if(preview.Count!=1||preview.Input!=100||preview.Output!=20||before.Count!=2)throw new Exception("Archive preview failed");
   if(UsageLedger.ArchiveRecords(requests,archive,DateTime.Today.AddDays(-30),"direct-chat")!=1)throw new Exception("Archive count failed");
   var after=UsageLedger.Scan(requests,archive);
   if(File.Exists(oldFile)||File.Exists(Path.Combine(requests,oldId+".request.txt"))||after.Count!=2||after.Input!=130||after.Output!=30||after.SourceStats().Length!=2)throw new Exception("Archive totals or raw cleanup failed");
   File.WriteAllText(oldFile,"{\"status\":\"completed\",\"created_at\":\"2020-01-01T00:00:00+09:00\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20}}");
   if(UsageLedger.Scan(requests,archive).Count!=2)throw new Exception("Archived duplicate counted twice");
   var sample=Path.Combine(root,"quality.json");File.WriteAllText(sample,"{\"profile\":\"quick\",\"status\":\"completed\",\"rows\":[{\"status\":\"completed\",\"warmup\":false,\"ttft_seconds\":1,\"decode_tok_s\":40,\"input_tokens\":50},{\"status\":\"completed\",\"quality_only\":true,\"recall_pass\":true,\"decode_tok_s\":5}]}");
   var bench=InsightsWindow.ReadBenchmark(sample);if(bench.Runs!=1||bench.RecallTotal!=1||bench.RecallPassed!=1||bench.MeanSpeed!=40)throw new Exception("Quality result filtering failed");
   TelemetryTest();
   Directory.CreateDirectory(DataRoot);File.WriteAllText(Path.Combine(DataRoot,"operations-test.txt"),"PASS: archive totals, raw cleanup, duplicate protection, quality comparison data, GPU energy integration");
  }finally{Directory.Delete(root,true);}
 }
 void RestartSourceApp(){
  string script=Path.Combine(Root,"run-source.ps1");if(!File.Exists(script)){MessageBox.Show("소스 실행 스크립트를 찾지 못했습니다.","Qwen");return;}
  try{
   string command="Start-Sleep -Milliseconds 1500; & '"+script.Replace("'","''")+"' --show";
   string encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
   Process.Start(new ProcessStartInfo("powershell.exe","-NoProfile -STA -ExecutionPolicy RemoteSigned -EncodedCommand "+encoded){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
   quitting=true;Close();
  }catch(Exception ex){MessageBox.Show("상태 앱 재시작 실패: "+ex.Message,"Qwen");}
 }
 partial class UsageLedger {
  public class ArchivedRow {public string Id {get;set;}public long Input {get;set;}public long Output {get;set;}public string Day {get;set;}public string Source {get;set;}public double Speed {get;set;}}
  readonly HashSet<string> archivedIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  void LoadArchive(string archivePath,string requestsDirectory){
   if(string.IsNullOrEmpty(archivePath))return;
   var rows=File.Exists(archivePath)?new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<List<ArchivedRow>>(File.ReadAllText(archivePath)):new List<ArchivedRow>();
   RecoverStaging(Path.GetDirectoryName(archivePath),requestsDirectory,(rows??new List<ArchivedRow>()).Where(x=>x!=null).Select(x=>x.Id));
   foreach(var row in rows??new List<ArchivedRow>()){
    DateTime day;if(row==null||string.IsNullOrEmpty(row.Id)||!DateTime.TryParse(row.Day,out day)||row.Input<0||row.Output<0||!archivedIds.Add(row.Id))continue;
    var entry=new Entry{Input=row.Input,Output=row.Output,Day=day.Date,Source=row.Source,Speed=row.Speed};
    entries["archive:"+row.Id]=entry;Input+=row.Input;Output+=row.Output;
    long old;daily.TryGetValue(entry.Day,out old);daily[entry.Day]=old+row.Input+row.Output;
    if(entry.Day<FirstDay)FirstDay=entry.Day;
   }
  }
  static void RecoverStaging(string archiveDirectory,string requestsDirectory,IEnumerable<string> archived){
   string staging=Path.Combine(archiveDirectory,"record-staging");if(!Directory.Exists(staging))return;
   var ids=new HashSet<string>(archived,StringComparer.OrdinalIgnoreCase);
   foreach(var batch in Directory.GetDirectories(staging)){
    foreach(var file in Directory.GetFiles(batch)){
     string id=Path.GetFileName(file).Split('.')[0];
     if(ids.Contains(id))File.Delete(file);
     else{string original=Path.Combine(requestsDirectory,Path.GetFileName(file));if(!File.Exists(original))File.Move(file,original);}
    }
    if(!Directory.EnumerateFileSystemEntries(batch).Any())Directory.Delete(batch);
   }
  }
  public class ArchivePreview {public int Count;public long Input,Output;}
  static ArchivedRow ReadArchiveRow(string file,DateTime cutoff,string source){
   var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
   string status=Field(data,"status");if(status!="completed"&&status!="incomplete")return null;
   string actualSource=Field(data,"source");if(source!=null&&!string.Equals(source,actualSource,StringComparison.OrdinalIgnoreCase))return null;
   DateTimeOffset created;if(!DateTimeOffset.TryParse(Field(data,"created_at"),out created)||created.LocalDateTime>=cutoff)return null;
   var usage=data.ContainsKey("usage")?data["usage"] as Dictionary<string,object>:null;
   double input=Number(usage,"prompt_tokens"),output=Number(usage,"completion_tokens"),seconds=Number(data,"generation_seconds");
   if(double.IsNaN(input)||double.IsNaN(output)||input<0||output<0)return null;
   return new ArchivedRow{Id=Path.GetFileNameWithoutExtension(file),Input=(long)input,Output=(long)output,Day=created.LocalDateTime.Date.ToString("yyyy-MM-dd"),Source=actualSource,Speed=!double.IsNaN(seconds)&&seconds>0?output/seconds:0};
  }
  static List<ArchivedRow> Candidates(string directory,string archivePath,DateTime cutoff,string source){
   var archived=File.Exists(archivePath)?new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<List<ArchivedRow>>(File.ReadAllText(archivePath)):new List<ArchivedRow>();
   var ids=new HashSet<string>((archived??new List<ArchivedRow>()).Where(x=>x!=null).Select(x=>x.Id),StringComparer.OrdinalIgnoreCase);
   var rows=new List<ArchivedRow>();if(!Directory.Exists(directory))return rows;
   foreach(var file in Directory.EnumerateFiles(directory,"*.json")){
    if(!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file),@"^[a-fA-F0-9-]{36}\.json$")||ids.Contains(Path.GetFileNameWithoutExtension(file)))continue;
    try{var row=ReadArchiveRow(file,cutoff,source);if(row!=null)rows.Add(row);}catch(IOException){}catch(ArgumentException){}catch(InvalidOperationException){}
   }
   return rows;
  }
  public static ArchivePreview PreviewArchive(string directory,string archivePath,DateTime cutoff,string source){
   var rows=Candidates(directory,archivePath,cutoff,source);return new ArchivePreview{Count=rows.Count,Input=rows.Sum(x=>x.Input),Output=rows.Sum(x=>x.Output)};
  }
  public static int ArchiveRecords(string directory,string archivePath,DateTime cutoff,string source){
   var rows=Candidates(directory,archivePath,cutoff,source);if(rows.Count==0)return 0;
   string stage=Path.Combine(Path.GetDirectoryName(archivePath),"record-staging",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
   bool committed=false;
   try{
    foreach(var row in rows){foreach(var file in Directory.GetFiles(directory,row.Id+".*")){string target=Path.Combine(stage,Path.GetFileName(file));File.Move(file,target);}}
    var all=File.Exists(archivePath)?new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<List<ArchivedRow>>(File.ReadAllText(archivePath)):new List<ArchivedRow>();
    all.AddRange(rows);string temp=archivePath+".tmp";File.WriteAllText(temp,new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(all));
    if(File.Exists(archivePath))File.Replace(temp,archivePath,null);else File.Move(temp,archivePath);committed=true;
    Directory.Delete(stage,true);return rows.Count;
   }catch{
    if(!committed)foreach(var file in Directory.GetFiles(stage)){string target=Path.Combine(directory,Path.GetFileName(file));if(!File.Exists(target))File.Move(file,target);}
    throw;
   }finally{if(Directory.Exists(stage)&&!Directory.EnumerateFileSystemEntries(stage).Any())Directory.Delete(stage);}
  }
 }

 partial class InsightsWindow {
  readonly ListView queueList=new ListView(),energyList=new ListView();readonly Label queueNote=new Label(),archiveNote=new Label(),updateNote=new Label(),energyNote=new Label(),regressionNote=new Label();
  readonly ComboBox retention=new ComboBox(),archiveSource=new ComboBox();
  readonly System.Windows.Forms.Timer queueTimer=new System.Windows.Forms.Timer();
  bool queueLoading;
  void BuildOperations(){BuildQueue();BuildArchive();BuildEnergy();BuildUpdates();AddRegressionControl();}
  void BuildQueue(){
   var page=Page("대기열");tabs.TabPages.Add(page);
   var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42};page.Controls.Add(bar);
   var refresh=ActionButton("새로고침");refresh.Click+=(s,e)=>ReloadQueue();bar.Controls.Add(refresh);
   var cancel=ActionButton("선택한 직접 대화 취소");cancel.Click+=(s,e)=>CancelQueueSelection();bar.Controls.Add(cancel);
   queueNote.Dock=DockStyle.Top;queueNote.Height=64;queueNote.ForeColor=Color.DimGray;page.Controls.Add(queueNote);
   queueList.Dock=DockStyle.Fill;queueList.View=View.Details;queueList.FullRowSelect=true;queueList.GridLines=true;
   foreach(var c in new[]{new ColumnHeader{Text="순서",Width=65},new ColumnHeader{Text="출처",Width=170},new ColumnHeader{Text="상태",Width=100},new ColumnHeader{Text="시작/접수",Width=150},new ColumnHeader{Text="경과",Width=95},new ColumnHeader{Text="취소",Width=120}})queueList.Columns.Add(c);
   page.Controls.Add(queueList);page.Controls.SetChildIndex(queueList,0);page.Controls.SetChildIndex(queueNote,1);page.Controls.SetChildIndex(bar,2);
   queueTimer.Interval=2500;queueTimer.Tick+=(s,e)=>{if(!IsDisposed&&tabs.SelectedTab==page)ReloadQueue();};queueTimer.Start();FormClosed+=(s,e)=>queueTimer.Dispose();
  }
  static string UiEndpoint(string suffix){return Setting("uiUrl","http://127.0.0.1:18022/ui").TrimEnd('/')+suffix;}
  static string UiCall(string method,string suffix){
   var url=new Uri(UiEndpoint(suffix));if(url.Host!="127.0.0.1"&&url.Host!="localhost")throw new InvalidOperationException("UI API는 로컬 주소만 사용할 수 있습니다.");
   var request=(HttpWebRequest)WebRequest.Create(url);request.Method=method;request.Timeout=1800;request.ReadWriteTimeout=1800;
   request.Headers["x-ui-token"]=File.ReadAllText(Path.Combine(GatewayRoot,"ui-token.txt")).Trim();
   using(var response=request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream()))return reader.ReadToEnd();
  }
  async void ReloadQueue(){
   if(queueLoading)return;queueLoading=true;
   try{
   string data=null,error=null;await Task.Run(()=>{try{data=UiCall("GET","/queue");}catch(Exception ex){error=ex.Message;}});if(IsDisposed)return;
   queueList.Items.Clear();if(error!=null){queueNote.Text="대기열 상세 정보 사용 불가: "+error+"\n게이트웨이가 이전 버전이면 재시작 후 사용할 수 있습니다.";return;}
   var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(data);var rows=new List<Dictionary<string,object>>();
   var active=d.ContainsKey("active")?d["active"] as Dictionary<string,object>:null;if(active!=null)rows.Add(active);
   var pending=d.ContainsKey("pending")?d["pending"] as System.Collections.IEnumerable:null;if(pending!=null)foreach(var item in pending){var row=item as Dictionary<string,object>;if(row!=null)rows.Add(row);}
   foreach(var row in rows){DateTimeOffset created;string time=DateTimeOffset.TryParse(Field(row,"created_at"),out created)?created.ToLocalTime().ToString("HH:mm:ss"):"—";
    string age=DateTimeOffset.TryParse(Field(row,"created_at"),out created)?Math.Max(0,(int)(DateTimeOffset.Now-created).TotalSeconds)+"초":"—";
    bool cancellable=!string.IsNullOrEmpty(Field(row,"ui_job_id"))&&Field(row,"source")=="direct-chat";
    var item=new ListViewItem(new[]{Number(row,"position").ToString("0"),SourceName(Field(row,"source")),Field(row,"status")=="running"?"실행 중":"대기",time,age,cancellable?"직접 대화":"표시만"});item.Tag=cancellable?Field(row,"ui_job_id"):null;queueList.Items.Add(item);
   }
   queueNote.Text=string.Format("실행 {0}건 · 대기 {1}건. 예상 대기 시간은 요청 길이를 알 수 없어 표시하지 않습니다.\n상태창에서 시작한 직접 대화만 취소할 수 있습니다. 서버와 다른 앱의 요청은 그대로 둡니다.",active==null?0:1,rows.Count-(active==null?0:1));
   }catch(Exception ex){if(!IsDisposed)queueNote.Text="대기열 표시 실패: "+ex.Message;}finally{queueLoading=false;}
  }
  async void CancelQueueSelection(){
   if(queueList.SelectedItems.Count==0)return;string id=queueList.SelectedItems[0].Tag as string;if(string.IsNullOrEmpty(id)){MessageBox.Show("이 요청은 상태창에서 취소할 수 없습니다.","Qwen");return;}
   if(MessageBox.Show("선택한 직접 대화를 취소할까요?","Qwen",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
   try{await Task.Run(()=>UiCall("DELETE","/jobs/"+Uri.EscapeDataString(id)));ReloadQueue();}catch(Exception ex){MessageBox.Show(ex.Message,"취소 실패");}
  }
  void BuildArchive(){
   var page=Page("기록 정리");tabs.TabPages.Add(page);
   var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42};page.Controls.Add(bar);
   retention.DropDownStyle=ComboBoxStyle.DropDownList;retention.Width=130;retention.Items.AddRange(new object[]{"7일 이전","30일 이전","90일 이전"});retention.SelectedIndex=1;bar.Controls.Add(retention);
   archiveSource.DropDownStyle=ComboBoxStyle.DropDownList;archiveSource.Width=180;archiveSource.Items.Add("전체 출처");archiveSource.SelectedIndex=0;bar.Controls.Add(archiveSource);
   var preview=ActionButton("정리 대상 확인");preview.Click+=(s,e)=>PreviewArchiveUi();bar.Controls.Add(preview);
   var clean=ActionButton("원문 기록 삭제");clean.Click+=(s,e)=>ArchiveUi();bar.Controls.Add(clean);
   archiveNote.Dock=DockStyle.Fill;archiveNote.Font=new Font("Malgun Gothic",11);archiveNote.Padding=new Padding(10,22,10,10);archiveNote.Text="완료된 요청 중 선택 기간보다 오래된 원문 기록을 정리합니다.\n토큰·날짜·출처만 집계 파일에 남겨 누적 통계는 유지합니다.\n진행 중인 요청과 대화 세션은 삭제하지 않습니다.";page.Controls.Add(archiveNote);
   page.Controls.SetChildIndex(archiveNote,0);page.Controls.SetChildIndex(bar,1);
  }
  DateTime ArchiveCutoff(){return DateTime.Today.AddDays(retention.SelectedIndex==0?-7:retention.SelectedIndex==2?-90:-30);}
  string ArchiveSource(){return archiveSource.SelectedIndex<=0?null:archiveSource.SelectedItem.ToString();}
  void RefreshArchiveSources(){string selected=archiveSource.SelectedItem as string;archiveSource.Items.Clear();archiveSource.Items.Add("전체 출처");if(owner!=null&&owner.usageLedger!=null)foreach(var stat in owner.usageLedger.SourceStats())archiveSource.Items.Add(stat.Name);archiveSource.SelectedItem=selected!=null&&archiveSource.Items.Contains(selected)?selected:"전체 출처";}
  async void PreviewArchiveUi(){
   DateTime cutoff=ArchiveCutoff();string source=ArchiveSource();archiveNote.Text="정리 대상을 계산하는 중…";
   try{var result=await Task.Run(()=>UsageLedger.PreviewArchive(SharedRequests,ArchiveFile,cutoff,source));if(!IsDisposed)archiveNote.Text=string.Format("{0:yyyy-MM-dd} 이전 · {1}\n정리 대상 {2:N0}건 · 입력 {3:N0} / 출력 {4:N0} 토큰\n원문 파일은 삭제되지만 이 수치와 날짜·출처는 누적 통계에 남습니다.",cutoff,source??"전체 출처",result.Count,result.Input,result.Output);}catch(Exception ex){archiveNote.Text="정리 대상을 확인하지 못했습니다: "+ex.Message;}
  }
  async void ArchiveUi(){
   DateTime cutoff=ArchiveCutoff();string source=ArchiveSource();UsageLedger.ArchivePreview preview;
   try{preview=await Task.Run(()=>UsageLedger.PreviewArchive(SharedRequests,ArchiveFile,cutoff,source));}catch(Exception ex){MessageBox.Show(ex.Message,"기록 확인 실패");return;}
   if(preview.Count==0){archiveNote.Text="정리 대상이 없습니다.";return;}
   if(MessageBox.Show(string.Format("{0:N0}건의 입력·출력 원문 파일을 삭제합니다.\n누적 토큰 집계는 유지됩니다. 계속할까요?",preview.Count),"원문 기록 삭제",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
   archiveNote.Text="기록을 안전하게 정리하는 중…";
   try{int count=await Task.Run(()=>UsageLedger.ArchiveRecords(SharedRequests,ArchiveFile,cutoff,source));if(IsDisposed)return;owner.usageScan=Task.Run(()=>UsageLedger.Scan(SharedRequests));archiveNote.Text=count.ToString("N0")+"건의 원문 기록을 삭제했습니다. 누적 통계를 다시 계산합니다.";}
   catch(Exception ex){archiveNote.Text="기록 정리 실패: "+ex.Message;}
  }
  void BuildEnergy(){
   var page=Page("노트북 효율");tabs.TabPages.Add(page);
   energyNote.Dock=DockStyle.Top;energyNote.Height=115;energyNote.Font=new Font("Malgun Gothic",10);energyNote.Padding=new Padding(8,10,8,0);page.Controls.Add(energyNote);
   energyList.Dock=DockStyle.Fill;energyList.View=View.Details;energyList.FullRowSelect=true;energyList.GridLines=true;
   foreach(var c in new[]{new ColumnHeader{Text="시각",Width=105},new ColumnHeader{Text="출처",Width=160},new ColumnHeader{Text="출력",Width=80},new ColumnHeader{Text="시간",Width=80},new ColumnHeader{Text="GPU 전체",Width=110},new ColumnHeader{Text="유휴 제외",Width=110},new ColumnHeader{Text="J/토큰",Width=100}})energyList.Columns.Add(c);
   page.Controls.Add(energyList);page.Controls.SetChildIndex(energyList,0);page.Controls.SetChildIndex(energyNote,1);RefreshEnergy();
  }
  internal void RefreshEnergy(){if(owner==null)return;energyNote.Text=owner.EnergySummary();energyList.BeginUpdate();energyList.Items.Clear();
   foreach(var r in owner.energyRecords.OrderByDescending(x=>x.At).Take(30))energyList.Items.Add(new ListViewItem(new[]{r.At.ToString("MM-dd HH:mm"),SourceName(r.Source),r.Output.ToString("N0"),r.Seconds.ToString("0.00")+"초",r.GrossJ.ToString("0.0")+" J",double.IsNaN(r.NetJ)?"기준 없음":r.NetJ.ToString("0.0")+" J",double.IsNaN(r.NetJ)||r.Output<=0?"—":(r.NetJ/r.Output).ToString("0.0")}));
   energyList.EndUpdate();
  }
  void BuildUpdates(){
   var page=Page("업데이트");tabs.TabPages.Add(page);
   var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42};page.Controls.Add(bar);
   var check=ActionButton("새 버전 확인");check.Click+=(s,e)=>RunUpdater("Check");bar.Controls.Add(check);
   var install=ActionButton("업데이트 설치");install.Click+=(s,e)=>RunUpdater("Install");bar.Controls.Add(install);
  var rollback=ActionButton("이전 버전 복구");rollback.Click+=(s,e)=>RunUpdater("Rollback");bar.Controls.Add(rollback);
   var restart=ActionButton("상태 앱 재시작");restart.Click+=(s,e)=>{if(owner!=null)owner.RestartSourceApp();};bar.Controls.Add(restart);
   updateNote.Dock=DockStyle.Fill;updateNote.Font=new Font("Malgun Gothic",10);updateNote.Padding=new Padding(12,22,12,10);
   updateNote.Text="GitHub 원본을 확인하고 상태 앱 소스만 업데이트합니다.\n설치 전 백업을 만들고 컴파일 검사에 실패하면 적용하지 않습니다.\n변경 내용은 상태 앱 재시작 후 적용됩니다. Qwen 서버는 건드리지 않습니다.";page.Controls.Add(updateNote);
   page.Controls.SetChildIndex(updateNote,0);page.Controls.SetChildIndex(bar,1);
  }
  async void RunUpdater(string mode){
   string script=Path.Combine(Root,"update-source.ps1");if(!File.Exists(script)){updateNote.Text="업데이트 스크립트를 찾지 못했습니다: "+script;return;}
   if(mode=="Install"&&MessageBox.Show("GitHub 최신 소스를 검사하고 상태 앱에 적용할까요?","Qwen 업데이트",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
   if(mode=="Rollback"&&MessageBox.Show("마지막 업데이트 이전 상태 앱 소스로 복구할까요?","Qwen 복구",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
   updateNote.Text="작업 중…";
   try{string result=await Task.Run(()=>{var info=new ProcessStartInfo("powershell.exe","-NoProfile -ExecutionPolicy RemoteSigned -File \""+script+"\" -Mode "+mode+" -Target \""+Root+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};using(var p=Process.Start(info)){string output=p.StandardOutput.ReadToEnd(),error=p.StandardError.ReadToEnd();p.WaitForExit(60000);if(!p.HasExited){p.Kill();throw new TimeoutException("업데이트 확인 시간이 초과됐습니다.");}if(p.ExitCode!=0)throw new Exception(error+"\n"+output);return output;}});if(!IsDisposed)updateNote.Text=result+"\n상태 앱을 종료했다가 다시 실행하면 새 소스가 적용됩니다.";}
   catch(Exception ex){if(!IsDisposed)updateNote.Text="업데이트 작업 실패: "+ex.Message;}
  }
  void AddRegressionControl(){
   var bar=benchmarks.Parent.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();if(bar==null)return;
   var compare=ActionButton("선택한 두 기록 비교");compare.Click+=(s,e)=>CompareSelected();bar.Controls.Add(compare);
  }
  void CompareSelected(){
   if(benchmarks.SelectedItems.Count!=2){MessageBox.Show("비교할 테스트 기록 두 개를 선택하세요 (Ctrl+클릭).","Qwen");return;}
   try{var first=benchmarks.SelectedItems[0].Tag as BenchRecord;var second=benchmarks.SelectedItems[1].Tag as BenchRecord;
    var a=ReadBenchmark(first.Path);var b=ReadBenchmark(second.Path);if(a.Profile!=b.Profile){MessageBox.Show("같은 테스트 프로필을 선택해야 합니다.","Qwen");return;}
    var older=string.Compare(first.Added,second.Added,StringComparison.Ordinal)<=0?a:b;var newer=older==a?b:a;
    var oldRecord=older==a?first:second;var newRecord=older==a?second:first;
    string speed=double.IsNaN(older.MeanSpeed)||double.IsNaN(newer.MeanSpeed)||older.MeanSpeed<=0?"측정 없음":((newer.MeanSpeed/older.MeanSpeed-1)*100).ToString("+0.0;-0.0;0")+"%";
    string ttft=double.IsNaN(older.MeanTtft)||double.IsNaN(newer.MeanTtft)||older.MeanTtft<=0?"측정 없음":((newer.MeanTtft/older.MeanTtft-1)*100).ToString("+0.0;-0.0;0")+"%";
    string quality=older.RecallTotal==0||newer.RecallTotal==0?"검사 없음":string.Format("{0}/{1} → {2}/{3}",older.RecallPassed,older.RecallTotal,newer.RecallPassed,newer.RecallTotal);
    var warnings=new List<string>();if(!double.IsNaN(older.MeanSpeed)&&older.MeanSpeed>0&&!double.IsNaN(newer.MeanSpeed)&&newer.MeanSpeed<older.MeanSpeed*.85)warnings.Add("생성 속도 15% 이상 하락");
    if(!double.IsNaN(older.MeanTtft)&&older.MeanTtft>0&&!double.IsNaN(newer.MeanTtft)&&newer.MeanTtft>older.MeanTtft*1.25)warnings.Add("첫 토큰 시간 25% 이상 증가");
    if(older.RecallTotal>0&&newer.RecallTotal>0&&newer.RecallPassed<older.RecallPassed)warnings.Add("정확성 검사 통과 수 감소");
    string warning=warnings.Count>0?"\n주의: "+string.Join(", ",warnings):"\n눈에 띄는 회귀 신호 없음";
    MessageBox.Show(string.Format("같은 프로필: {0}\n{1} {2} → {3} {4}\n생성 속도: {5}\n첫 토큰 시간: {6}\n정확성 검사: {7}{8}\n\n캐시와 서버 부하에 따라 차이가 날 수 있습니다. 여러 번 비교하세요.",a.Profile,oldRecord.Backend,oldRecord.Added,newRecord.Backend,newRecord.Added,speed,ttft,quality,warning),"업데이트 전후 비교");
   }catch(Exception ex){MessageBox.Show(ex.Message,"비교 실패");}
  }
 }
}
