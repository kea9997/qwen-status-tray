using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Net;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Web.Script.Serialization;

partial class QwenStatus : Form {
 [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string id);
 static readonly string Root=SourceRoot();
 static string SourceRoot(){
  string directory=AppDomain.CurrentDomain.GetData("QwenStatusRoot") as string;
  return Path.GetFullPath(string.IsNullOrWhiteSpace(directory)?AppDomain.CurrentDomain.BaseDirectory:directory).TrimEnd(Path.DirectorySeparatorChar);
 }
 static readonly string DataRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QwenStatus");
 static readonly Dictionary<string,object> Settings=LoadSettings();
 internal static readonly string GatewayRoot=SettingPath("gatewayDirectory",Path.Combine(Root,"..","qwen-gateway"));
 static readonly string BackendFile=SettingPath("backendFile",Path.Combine(Root,"backend.txt"));
 static readonly string SharedRequests=Path.Combine(GatewayRoot,"requests");
 static readonly string ServerUrl=Setting("serverUrl","http://127.0.0.1:18021/").TrimEnd('/')+"/";
 static readonly string QueueUrl=Setting("queueUrl","http://127.0.0.1:18022/queue");
 static readonly string PrivacyFile=Path.Combine(DataRoot,"hide-content.txt");
 static readonly string HermesConfig=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"hermes","config.yaml");
 Label state=new Label(), note=new Label(), usage=new Label(), rate=new Label(); Button usageButton=new Button(); CheckBox hideContentBox=new CheckBox();
 Button start=new Button(), stop=new Button();
 static readonly string Jobs=SettingPath("jobsDirectory",Path.Combine(Root,"..","codex-local-worker","jobs"));
 static Dictionary<string,object> LoadSettings(){try{string path=Path.Combine(DataRoot,"settings.json");return File.Exists(path)?new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path)):new Dictionary<string,object>();}catch{return new Dictionary<string,object>();}}
 static string Setting(string key,string fallback){object value;return Settings.TryGetValue(key,out value)&&value!=null&&!string.IsNullOrWhiteSpace(value.ToString())?value.ToString():fallback;}
 static string SettingPath(string key,string fallback){string value=Environment.ExpandEnvironmentVariables(Setting(key,fallback));return Path.GetFullPath(Path.IsPathRooted(value)?value:Path.Combine(Root,value));}
 ComboBox history=new ComboBox(); CheckBox follow=new CheckBox();
 TextBox inputText=new TextBox(), outputText=new TextBox(); Label jobInfo=new Label(); ProgressBar contextBar=new ProgressBar();
 TokenTestWindow tokenWindow; ActivityWindow activityWindow;
 class JobItem { public string Path,Title; public override string ToString(){return Title;} }
 NotifyIcon tray=new NotifyIcon(); System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
 Icon[] icons=new Icon[5]; bool polling,action,quitting; Process server;
 DateTime requested=DateTime.MinValue, previousTime=DateTime.MinValue; double previousTokens=-1;
 Task<string> gpuTask; DateTime lastGpuPoll=DateTime.MinValue; string gpuText="GPU 정보 확인 중…";
 readonly object jobLock=new object(); string latestJobPath; DateTime latestJobCreated=DateTime.MinValue;
 FileSystemWatcher[] jobWatchers; Task<string> initialJobScan;
 readonly ConcurrentQueue<string> dirtyUsage=new ConcurrentQueue<string>(); Task<UsageLedger> usageScan; UsageLedger usageLedger; UsageWindow usageWindow;
 class UsageLedger {
  class Entry {public long Input,Output;public DateTime Day;public string Source;public double Speed;}
  public class SourceStat {public string Name;public long Input,Output,Today,Week,Count;}
  readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>(StringComparer.OrdinalIgnoreCase);
  readonly HashSet<string> unreadable=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<DateTime,long> daily=new Dictionary<DateTime,long>();
  public long Input,Output;public DateTime FirstDay=DateTime.MaxValue;public string Error;public int Count{get{return entries.Count;}}public int Unreadable{get{return unreadable.Count;}}
  public long DayTotal(DateTime day){long value;return daily.TryGetValue(day.Date,out value)?value:0;}
  public void AddFile(string path){
   if(!Regex.IsMatch(Path.GetFileName(path),@"^[a-fA-F0-9-]{36}\.json$"))return;
   try{var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
    var usage=data.ContainsKey("usage")?data["usage"] as Dictionary<string,object>:null;
    double input=Number(usage,"prompt_tokens"),output=Number(usage,"completion_tokens");
    if(double.IsNaN(input)||double.IsNaN(output)||input<0||output<0)return;
    unreadable.Remove(path);
    DateTimeOffset created;DateTime day=DateTimeOffset.TryParse(Field(data,"created_at"),out created)?created.LocalDateTime.Date:File.GetCreationTime(path).Date;
    double generation=Number(data,"generation_seconds"),speed=!double.IsNaN(generation)&&generation>0?output/generation:double.NaN;
    var entry=new Entry{Input=(long)input,Output=(long)output,Day=day,Source=Field(data,"source"),Speed=speed};Entry previous;
    if(entries.TryGetValue(path,out previous)){
    if(previous.Input==entry.Input&&previous.Output==entry.Output&&previous.Day==entry.Day&&previous.Source==entry.Source&&(previous.Speed==entry.Speed||(double.IsNaN(previous.Speed)&&double.IsNaN(entry.Speed))))return;
     Input-=previous.Input;Output-=previous.Output;daily[previous.Day]-=previous.Input+previous.Output;
    }
    entries[path]=entry;Input+=entry.Input;Output+=entry.Output;
    long old;daily.TryGetValue(day,out old);daily[day]=old+entry.Input+entry.Output;
    if(day<FirstDay)FirstDay=day;
   }catch(Exception){unreadable.Add(path);}
  }
  public static UsageLedger Scan(string directory){var result=new UsageLedger();try{if(Directory.Exists(directory))foreach(var file in Directory.EnumerateFiles(directory,"*.json"))result.AddFile(file);}catch(Exception ex){result.Error=ex.Message;}return result;}
  public SourceStat[] SourceStats(){
   var groups=new Dictionary<string,SourceStat>(StringComparer.OrdinalIgnoreCase);DateTime today=DateTime.Today,week=today.AddDays(-6);
   foreach(var entry in entries.Values){string key=string.IsNullOrWhiteSpace(entry.Source)?"출처 미확인":entry.Source;SourceStat row;
    if(!groups.TryGetValue(key,out row)){row=new SourceStat{Name=key};groups[key]=row;}
    row.Input+=entry.Input;row.Output+=entry.Output;row.Count++;if(entry.Day==today)row.Today+=entry.Input+entry.Output;if(entry.Day>=week&&entry.Day<=today)row.Week+=entry.Input+entry.Output;
   }
   return groups.Values.OrderByDescending(x=>x.Input+x.Output).ToArray();
  }
  public double PeakSpeed(){double max=double.NaN;foreach(var entry in entries.Values)if(!double.IsNaN(entry.Speed)&&(double.IsNaN(max)||entry.Speed>max))max=entry.Speed;return max;}
  public long LongestInput(){long max=0;foreach(var entry in entries.Values)max=Math.Max(max,entry.Input);return max;}
 }
 class UsageWindow : Form {
  Label total=new Label(),detail=new Label(),period=new Label(),today=new Label();Panel chart=new Panel();UsageLedger ledger;
  public UsageWindow(){
   Text="Qwen 누적 사용량";ClientSize=new Size(720,505);MinimumSize=MaximumSize=new Size(736,544);Font=new Font("Malgun Gothic",10);BackColor=Color.FromArgb(245,247,250);
   Controls.Add(new Label{Text="Qwen 처리량",Bounds=new Rectangle(24,20,660,33),Font=new Font("Malgun Gothic",18,FontStyle.Bold)});
   total.Bounds=new Rectangle(24,66,660,52);total.Font=new Font("Malgun Gothic",25,FontStyle.Bold);total.ForeColor=Color.FromArgb(37,89,182);Controls.Add(total);
   detail.Bounds=new Rectangle(26,125,660,46);Controls.Add(detail);
   period.Bounds=new Rectangle(26,171,660,26);period.ForeColor=Color.DimGray;Controls.Add(period);
   Controls.Add(new Label{Text="최근 7일 · 하루 총 처리 토큰",Bounds=new Rectangle(26,212,660,25),Font=new Font("Malgun Gothic",11,FontStyle.Bold)});
   chart.Bounds=new Rectangle(24,242,672,195);chart.BackColor=Color.White;chart.BorderStyle=BorderStyle.FixedSingle;chart.Paint+=(s,e)=>DrawChart(e.Graphics);Controls.Add(chart);
   today.Bounds=new Rectangle(26,451,660,38);today.ForeColor=Color.DimGray;Controls.Add(today);
   UpdateStats(null);
  }
  public void UpdateStats(UsageLedger data){ledger=data;if(data==null){total.Text="계산 중…";detail.Text="저장된 요청 기록을 읽고 있습니다.";period.Text="";today.Text="";}else if(data.Error!=null){total.Text="기록 확인 필요";detail.Text=data.Error;period.Text="";today.Text="";}else{
    total.Text=string.Format("{0:N0} 토큰",data.Input+data.Output);
    detail.Text=string.Format("입력 {0:N0}   +   출력 {1:N0}   ·   사용량 기록 {2:N0}건",data.Input,data.Output,data.Count);
    period.Text="공통 대기열 기록 기간: "+(data.FirstDay==DateTime.MaxValue?"기록 없음":data.FirstDay.ToString("yyyy-MM-dd"))+"부터 · 토큰 테스트 포함"+(data.Unreadable>0?" · 읽기 실패 "+data.Unreadable+"건":"");
    today.Text=string.Format("오늘 {0:N0} 토큰  ·  Qwen 처리량이며 OpenAI 토큰 절약량은 아닙니다.",data.DayTotal(DateTime.Today));
   }chart.Invalidate();
  }
  void DrawChart(Graphics g){g.Clear(Color.White);if(ledger==null)return;long[] values=new long[7];long max=1;for(int i=0;i<7;i++){values[i]=ledger.DayTotal(DateTime.Today.AddDays(i-6));max=Math.Max(max,values[i]);}
   using(var brush=new SolidBrush(Color.FromArgb(76,139,230)))using(var labelBrush=new SolidBrush(Color.FromArgb(55,65,80)))using(var font=new Font("Malgun Gothic",8)){
    for(int i=0;i<7;i++){int x=23+i*94,h=(int)Math.Round(113.0*values[i]/max);g.FillRectangle(brush,x,132-h,56,h);string day=DateTime.Today.AddDays(i-6).ToString("M/d");g.DrawString(day,font,labelBrush,x+7,149);string value=values[i]>=1000000?(values[i]/1000000.0).ToString("0.#")+"M":values[i]>=1000?(values[i]/1000.0).ToString("0.#")+"K":values[i].ToString();g.DrawString(value,font,labelBrush,x+3,Math.Max(3,128-h-20));}
   }
  }
 }
 class ActivityWindow : Form {
  readonly ListView list=new ListView();readonly Label summary=new Label();
  public ActivityWindow(){
   Text="Qwen 최근 작업";ClientSize=new Size(960,550);MinimumSize=new Size(780,400);Font=new Font("Malgun Gothic",9);BackColor=Color.FromArgb(245,247,250);
   Controls.Add(new Label{Text="최근 작업",Bounds=new Rectangle(20,18,340,34),Font=new Font("Malgun Gothic",18,FontStyle.Bold)});
   var refresh=new Button{Text="새로고침",Bounds=new Rectangle(824,22,110,32),Anchor=AnchorStyles.Top|AnchorStyles.Right};refresh.Click+=(s,e)=>Reload();Controls.Add(refresh);
   summary.Bounds=new Rectangle(20,65,900,46);summary.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;summary.ForeColor=Color.DimGray;Controls.Add(summary);
   list.Bounds=new Rectangle(20,117,914,405);list.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;list.View=View.Details;list.FullRowSelect=true;list.GridLines=true;list.HideSelection=false;
   foreach(var column in new[]{new ColumnHeader{Text="시각",Width=142},new ColumnHeader{Text="출처",Width=115},new ColumnHeader{Text="상태",Width=165},new ColumnHeader{Text="입력",Width=96,TextAlign=HorizontalAlignment.Right},new ColumnHeader{Text="출력",Width=96,TextAlign=HorizontalAlignment.Right},new ColumnHeader{Text="출력 속도",Width=125,TextAlign=HorizontalAlignment.Right},new ColumnHeader{Text="전체 시간",Width=100,TextAlign=HorizontalAlignment.Right}})list.Columns.Add(column);
   Controls.Add(list);Reload();
  }
  public void Reload(){
   list.BeginUpdate();list.Items.Clear();int skipped=0;
   try{if(!Directory.Exists(SharedRequests)){summary.Text="공통 대기열 기록 폴더가 없습니다. Qwen 요청이 기록되면 여기에 표시됩니다.";return;}
    var files=new DirectoryInfo(SharedRequests).GetFiles("*.json").Where(f=>Regex.IsMatch(f.Name,@"^[a-fA-F0-9-]{36}\.json$")).OrderByDescending(f=>f.CreationTimeUtc).Take(100).ToArray();
    foreach(var file in files){try{
     var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(file.FullName));
     var u=data.ContainsKey("usage")?data["usage"] as Dictionary<string,object>:null;
     double input=Number(u,"prompt_tokens"),output=Number(u,"completion_tokens"),seconds=Number(data,"generation_seconds"),total=Number(data,"request_seconds");
     DateTimeOffset when;string time=DateTimeOffset.TryParse(Field(data,"created_at"),out when)?when.ToLocalTime().ToString("MM-dd HH:mm:ss"):file.CreationTime.ToString("MM-dd HH:mm:ss");
     double speedSeconds=!double.IsNaN(seconds)&&seconds>0?seconds:total;
     string speed=double.IsNaN(output)||double.IsNaN(speedSeconds)||speedSeconds<=0?"—":(output/speedSeconds).ToString("0.0")+" tok/s"+(double.IsNaN(seconds)?" 전체":"");
     string status=Field(data,"status");var row=new ListViewItem(new[]{time,SourceName(Field(data,"source")),Phase(status,File.Exists(Path.ChangeExtension(file.FullName,"live.txt"))),double.IsNaN(input)?"—":input.ToString("N0"),double.IsNaN(output)?"—":output.ToString("N0"),speed,double.IsNaN(total)?"—":Seconds(total)});
     if(status=="failed")row.ForeColor=Color.Firebrick;else if(status=="running")row.ForeColor=Color.DarkOrange;
     list.Items.Add(row);
    }catch{skipped++;}}
    summary.Text=string.Format("최근 {0}건 · 입력/출력 원문은 이 화면에 표시하지 않습니다.{1}",list.Items.Count,skipped>0?" · 읽지 못한 기록 "+skipped+"건":"");
   }catch(Exception ex){summary.Text="요청 기록 확인 필요: "+ex.Message;}finally{list.EndUpdate();}
  }
 }
 public QwenStatus(){
  Text="Qwen · 확인 중";ClientSize=new Size(700,900);MinimumSize=MaximumSize=new Size(716,939);
  Font=new Font("Malgun Gothic",10);BackColor=Color.FromArgb(245,247,250);StartPosition=FormStartPosition.CenterScreen;
  MaximizeBox=false;ShowInTaskbar=false;
  Color[] colors={Color.SlateGray,Color.RoyalBlue,Color.SeaGreen,Color.DarkOrange,Color.Firebrick};
  string[] glyphs={"−","~","Q",">","?"};
  for(int i=0;i<5;i++)icons[i]=MakeIcon(colors[i],glyphs[i]);
  Label title=new Label{Text="Qwen 서버",Bounds=new Rectangle(24,18,640,32),Font=new Font(Font.FontFamily,18,FontStyle.Bold)};Controls.Add(title);
  state.Bounds=new Rectangle(24,60,640,40);state.Font=new Font(Font.FontFamily,21,FontStyle.Bold);Controls.Add(state);
  note.Bounds=new Rectangle(26,106,640,38);note.ForeColor=Color.DimGray;Controls.Add(note);
  start.Text="▶ 서버 켜기";start.Bounds=new Rectangle(24,151,310,43);start.Click+=async(s,e)=>await StartServer();Controls.Add(start);
  stop.Text="■ 서버 끄기";stop.Bounds=new Rectangle(354,151,310,43);stop.Click+=async(s,e)=>await StopServer();Controls.Add(stop);
  usage.Bounds=new Rectangle(24,213,515,57);usage.Font=new Font("Malgun Gothic",9);Controls.Add(usage);
  usageButton.Text="누적 통계";usageButton.Bounds=new Rectangle(551,218,113,44);usageButton.Click+=(s,e)=>OpenUsage();Controls.Add(usageButton);
  rate.Bounds=new Rectangle(24,278,640,42);rate.ForeColor=Color.DimGray;Controls.Add(rate);
  jobInfo.Bounds=new Rectangle(24,326,640,76);jobInfo.BorderStyle=BorderStyle.FixedSingle;jobInfo.BackColor=Color.White;jobInfo.Padding=new Padding(8,5,8,5);jobInfo.Font=new Font("Malgun Gothic",9);jobInfo.Text="최근 요청을 확인하는 중…";Controls.Add(jobInfo);
  contextBar.Bounds=new Rectangle(24,407,640,10);contextBar.Maximum=1000;Controls.Add(contextBar);
  Controls.Add(new Label{Text="최근 입력",Bounds=new Rectangle(24,426,150,22),ForeColor=Color.DimGray});
  hideContentBox.Text="화면 내용 가리기";hideContentBox.Bounds=new Rectangle(517,425,148,24);hideContentBox.Checked=HidePreference();
  hideContentBox.CheckedChanged+=(s,e)=>{try{File.WriteAllText(PrivacyFile,hideContentBox.Checked?"hidden":"visible");}catch(Exception ex){MessageBox.Show("화면 가리기 설정을 저장하지 못했습니다: "+ex.Message,"Qwen");}RefreshJobs();};Controls.Add(hideContentBox);
  inputText.Multiline=true;inputText.ReadOnly=true;inputText.WordWrap=true;inputText.ScrollBars=ScrollBars.Vertical;inputText.Bounds=new Rectangle(24,449,640,95);Controls.Add(inputText);
  Controls.Add(new Label{Text="최근 출력",Bounds=new Rectangle(24,557,150,22),ForeColor=Color.DimGray});
  outputText.Multiline=true;outputText.ReadOnly=true;outputText.WordWrap=true;outputText.ScrollBars=ScrollBars.Vertical;outputText.Bounds=new Rectangle(24,580,640,148);Controls.Add(outputText);
  var chat=new Button{Text="직접 대화 창 열기",Bounds=new Rectangle(24,743,310,45)};chat.Click+=(s,e)=>OpenRequest(false);Controls.Add(chat);
  var agent=new Button{Text="Hermes 에이전트",Bounds=new Rectangle(354,743,310,45)};agent.Click+=(s,e)=>OpenRequest(true);Controls.Add(agent);
  var activity=new Button{Text="최근 작업 내역",Bounds=new Rectangle(24,803,310,45)};activity.Click+=(s,e)=>OpenActivity();Controls.Add(activity);
  var tokenTest=new Button{Text="토큰 속도 · 문맥 테스트",Bounds=new Rectangle(354,803,310,45)};tokenTest.Click+=(s,e)=>OpenTokenTest();Controls.Add(tokenTest);
  var insights=new Button{Text="분석 센터 · 진단 · 비교 · 공유",Bounds=new Rectangle(24,856,640,34)};insights.Click+=(s,e)=>OpenInsights();Controls.Add(insights);
  var menu=new ContextMenuStrip();menu.Items.Add("상태창 열기",null,(s,e)=>Restore());
  menu.Items.Add("직접 대화",null,(s,e)=>OpenRequest(false));menu.Items.Add("Hermes 에이전트",null,(s,e)=>OpenRequest(true));
  menu.Items.Add("최근 작업 내역",null,(s,e)=>OpenActivity());
  menu.Items.Add("토큰 테스트",null,(s,e)=>OpenTokenTest());
  menu.Items.Add("분석 센터",null,(s,e)=>OpenInsights());menu.Items.Add("작은 상태창",null,(s,e)=>ToggleMini());
  menu.Items.Add("다음 시작: ninfer 240K",null,(s,e)=>ChooseBackend("ninfer"));
  menu.Items.Add("다음 시작: vLLM 64K",null,(s,e)=>ChooseBackend("vllm"));
  menu.Items.Add("서버 켜기",null,async(s,e)=>await StartServer());menu.Items.Add("서버 끄기",null,async(s,e)=>await StopServer());
  menu.Items.Add("상태 앱만 종료",null,(s,e)=>{quitting=true;Close();});
  tray.ContextMenuStrip=menu;tray.Icon=icons[4];tray.Text="Qwen 상태";tray.Visible=true;tray.MouseClick+=(s,e)=>{if(e.Button==MouseButtons.Left)Restore();};
  FormClosing+=(s,e)=>{if(!quitting&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
  Resize+=(s,e)=>{if(WindowState==FormWindowState.Minimized)Hide();};
  FormClosed+=(s,e)=>{timer.Stop();if(jobWatchers!=null)foreach(var watcher in jobWatchers)if(watcher!=null)watcher.Dispose();if(usageWindow!=null&&!usageWindow.IsDisposed)usageWindow.Close();if(activityWindow!=null&&!activityWindow.IsDisposed)activityWindow.Close();if(insightsWindow!=null&&!insightsWindow.IsDisposed)insightsWindow.Close();if(miniWindow!=null&&!miniWindow.IsDisposed)miniWindow.Shutdown();if(tokenWindow!=null)tokenWindow.Shutdown();tray.Dispose();foreach(var icon in icons)icon.Dispose();};
  Directory.CreateDirectory(DataRoot);
  jobWatchers=new[]{WatchJobs(Jobs),WatchJobs(SharedRequests)};
  initialJobScan=Task.Run(()=>LatestJob(Jobs));
  usageScan=Task.Run(()=>UsageLedger.Scan(SharedRequests));
  timer.Interval=2000;timer.Tick+=async(s,e)=>await Poll();timer.Start();
 }
 void OpenTokenTest(){if(tokenWindow==null||tokenWindow.IsDisposed)tokenWindow=new TokenTestWindow();tokenWindow.Show();tokenWindow.WindowState=FormWindowState.Normal;tokenWindow.Activate();}
 void OpenActivity(){if(activityWindow==null||activityWindow.IsDisposed)activityWindow=new ActivityWindow();else activityWindow.Reload();activityWindow.Show();activityWindow.WindowState=FormWindowState.Normal;activityWindow.Activate();}
 static bool HidePreference(){try{string path=File.Exists(PrivacyFile)?PrivacyFile:Path.Combine(Root,"hide-content.txt");return !File.Exists(path)||File.ReadAllText(path).Trim()!="visible";}catch{return true;}}
 void OpenUsage(){if(usageWindow==null||usageWindow.IsDisposed)usageWindow=new UsageWindow();usageWindow.UpdateStats(usageLedger);usageWindow.Show();usageWindow.WindowState=FormWindowState.Normal;usageWindow.Activate();}
 static void UiLog(string text){try{Directory.CreateDirectory(DataRoot);File.AppendAllText(Path.Combine(DataRoot,"ui-activation.log"),DateTime.UtcNow.ToString("o")+" "+text+Environment.NewLine);}catch{}}
 void Restore(){ UiLog("restore requested");
  if(IsDisposed||quitting)return;
  Show();WindowState=FormWindowState.Normal;
  // Recover a hidden/off-screen window after display changes before refreshing data.
  var area=Screen.FromPoint(Cursor.Position).WorkingArea;
  if(!Screen.AllScreens.Any(screen=>screen.WorkingArea.Contains(new Rectangle(Left,Top,Math.Min(Width,160),Math.Min(Height,40)))))
   Location=new Point(area.Left+Math.Max(0,(area.Width-Width)/2),area.Top+Math.Max(0,(area.Height-Height)/2));
  BringToFront();Activate();UiLog("visible="+Visible+" bounds="+Bounds+" state="+WindowState);
  BeginInvoke((Action)(()=>{if(!IsDisposed&&!quitting&&Visible)RefreshJobs();}));
 }
 static string PowerShellExe(){string configured=Environment.GetEnvironmentVariable("QWEN_PWSH_EXE");if(!string.IsNullOrWhiteSpace(configured))return configured;string bundled=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),@".cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe");return File.Exists(bundled)?bundled:"pwsh.exe";}
 void OpenRequest(bool agent){
  string script=Path.Combine(Root,agent?"open-hermes.cmd":"qwen-console.ps1");
  if(!File.Exists(script)){MessageBox.Show("실행 도구를 찾지 못했습니다: "+script,"Qwen");return;}
  if(!agent&&!File.Exists(Path.Combine(GatewayRoot,"ui-token.txt"))){MessageBox.Show("공통 대기열의 UI 토큰 파일이 없습니다. settings.json의 gatewayDirectory와 대기열 설치를 확인하세요.","Qwen");return;}
  string work=Path.GetFullPath(Path.Combine(Root,"..","QwenWork"));if(!Directory.Exists(work))work=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
  var info=agent?new ProcessStartInfo("cmd.exe","/k \"\""+script+"\"\""){UseShellExecute=false,CreateNoWindow=false,WindowStyle=ProcessWindowStyle.Normal,WorkingDirectory=work}:
   new ProcessStartInfo("cmd.exe","/k chcp 65001>nul & \""+PowerShellExe()+"\" -NoProfile -File \""+script+"\" -Mode chat"){UseShellExecute=true,CreateNoWindow=false,WindowStyle=ProcessWindowStyle.Normal,WorkingDirectory=work};
  try{Process.Start(info);}catch(Exception ex){MessageBox.Show("대화 창을 열지 못했습니다: "+ex.Message,"Qwen");}
 }
 static string Field(Dictionary<string,object> data,string key){object value;return data.TryGetValue(key,out value)&&value!=null?value.ToString():"";}
 static string ReadBounded(string path){using(var reader=new StreamReader(path)){char[] buffer=new char[100000];int n=reader.ReadBlock(buffer,0,buffer.Length);return new string(buffer,0,n)+(reader.Peek()>=0?"\r\n[표시 한도 초과: 원본 파일에서 확인]":"");}}
 FileSystemWatcher WatchJobs(string directory){
  if(!Directory.Exists(directory))return null;
  var watcher=new FileSystemWatcher(directory,"*.json"){NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite};
  watcher.Created+=(s,e)=>JobChanged(e.FullPath);watcher.Changed+=(s,e)=>JobChanged(e.FullPath);
  watcher.Renamed+=(s,e)=>JobChanged(e.FullPath);
  watcher.Error+=(s,e)=>{initialJobScan=Task.Run(()=>LatestJob(Jobs));};
  watcher.EnableRaisingEvents=true;return watcher;
 }
 void JobChanged(string path){ConsiderLatest(path);if(Path.GetDirectoryName(path).Equals(SharedRequests,StringComparison.OrdinalIgnoreCase))dirtyUsage.Enqueue(path);}
 void ConsiderLatest(string path){
  if(!Regex.IsMatch(Path.GetFileName(path),@"^[a-fA-F0-9-]{36}\.json$"))return;
  try{DateTime created=File.GetCreationTimeUtc(path);lock(jobLock){if(created>=latestJobCreated){latestJobCreated=created;latestJobPath=path;}}}catch{}
 }
 void RefreshJobs(){
  try {
   var scan=initialJobScan;if(scan!=null&&scan.IsCompleted){initialJobScan=null;if(scan.Status==TaskStatus.RanToCompletion&&scan.Result!=null)ConsiderLatest(scan.Result);}
   string path;lock(jobLock)path=latestJobPath;
   if(path==null){ClearLive();return;}
   var selected=history.SelectedItem as JobItem;
   if(selected==null||selected.Path!=path){history.Items.Clear();history.Items.Add(new JobItem{Path=path,Title=Path.GetFileNameWithoutExtension(path)});history.SelectedIndex=0;}
   ShowJob();
  }catch(Exception ex){jobInfo.Text="요청 기록 확인 필요: "+ex.Message;}
 }
 static string LatestJob(string directory){
  // A pointer can be absent for old workers, queued jobs or requests that failed before inference.
  var files=Directory.Exists(directory)?new DirectoryInfo(directory).GetFiles("*.json").AsEnumerable():Enumerable.Empty<FileInfo>();
  if(directory==Jobs&&Directory.Exists(SharedRequests))files=files.Concat(new DirectoryInfo(SharedRequests).GetFiles("*.json"));
  return files.Where(f=>Regex.IsMatch(f.Name,@"^[a-fA-F0-9-]{36}\.json$")).OrderByDescending(f=>f.CreationTimeUtc).Select(f=>f.FullName).FirstOrDefault();
 }
 void ClearLive(){inputText.Clear();outputText.Clear();jobInfo.Text=Directory.Exists(SharedRequests)?"현재 요청 없음 · 다음 요청을 기다립니다.":"공통 대기열 기록 폴더 미연결 · settings.json의 gatewayDirectory를 확인하세요.";contextBar.Value=0;}
 static double Number(Dictionary<string,object> data,string key){object value;double number;return data!=null&&data.TryGetValue(key,out value)&&value!=null&&double.TryParse(value.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out number)?number:double.NaN;}
 static string Seconds(double value){return double.IsNaN(value)?"—":value.ToString("0.0")+"초";}
 static string Phase(string status,bool hasLive){return status=="queued"?"공통 대기열에서 기다리는 중":status=="running"?(hasLive?"답변 생성 중":"첫 토큰 준비 중"):status=="completed"?"완료":status=="incomplete"?"출력 한도 도달 · 미완성":status=="failed"?"실패":status=="cancelled"?"취소됨":status;}
 static string SourceName(string source){return source=="direct-chat"?"직접 대화":source=="token-test"?"토큰 테스트":source=="client"?"연결 앱":source=="hermes-agent"?"Hermes 에이전트":source=="hermes-telegram"?"Hermes 텔레그램":source=="Codex local_qwen"||source=="codex-worker"?"Codex 위임":source=="chat-summary"?"대화 요약":string.IsNullOrEmpty(source)?"출처 미확인":source;}
 void ShowJob(){
  var item=history.SelectedItem as JobItem;if(item==null){jobInfo.Text="아직 요청 기록이 없습니다.";return;}
  try{var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(item.Path));
   string status=Field(data,"status"),request=System.IO.Path.ChangeExtension(item.Path,"request.txt"),output=System.IO.Path.ChangeExtension(item.Path,"md");
   string input="이전 요청의 입력 원문은 저장되지 않았습니다.";
   if(!hideContentBox.Checked){if(File.Exists(request))input=ReadBounded(request);else if(data.ContainsKey("input")){var pending=data["input"] as Dictionary<string,object>;if(pending!=null)input=Field(pending,"task")+"\r\n\r\n[전송 준비 중: 원문은 전송 시 표시됩니다.]";}}
   else input="화면 내용 가리기가 켜져 있습니다. 기록 파일은 그대로 보관됩니다.";
   string live=System.IO.Path.ChangeExtension(item.Path,"live.txt");bool hasLive=File.Exists(live);string result=hideContentBox.Checked?"화면 내용 가리기가 켜져 있습니다. 기록 파일은 그대로 보관됩니다.":File.Exists(output)?ReadBounded(output):hasLive?ReadBounded(live):(status=="failed"?Field(data,"error"):status=="queued"?"공통 대기열에서 기다리는 중…":"첫 응답 토큰을 기다리는 중…");
   if(inputText.Text!=input)inputText.Text=input;if(outputText.Text!=result)outputText.Text=result;
   var usageData=data.ContainsKey("usage")?data["usage"] as Dictionary<string,object>:null;
   double prompt=Number(usageData,"prompt_tokens"),completion=Number(usageData,"completion_tokens");
   int limit=Backend()=="ninfer"?245760:65536;
   contextBar.Value=double.IsNaN(prompt)?0:(int)Math.Max(0,Math.Min(1000,Math.Round(prompt/limit*1000)));
   double generation=Number(data,"generation_seconds"),duration=Number(data,"request_seconds");
   double tokps=double.IsNaN(completion)?double.NaN:(!double.IsNaN(generation)&&generation>0?completion/generation:(!double.IsNaN(duration)&&duration>0?completion/duration:double.NaN));
   string speed=double.IsNaN(tokps)?"속도 —":(!double.IsNaN(generation)&&generation>0?"생성 속도 ":"전체 속도 ")+tokps.ToString("0.0")+" tok/s";
   string tokens=double.IsNaN(prompt)?"입력 토큰 —":string.Format("입력 {0:N0} / {1:N0} 토큰 ({2:0.#}%)",prompt,limit,prompt/limit*100);
   tokens+=" · 출력 "+(double.IsNaN(completion)?"—":completion.ToString("N0"))+" 토큰";
   DateTimeOffset created;string when=DateTimeOffset.TryParse(Field(data,"created_at"),out created)?created.ToLocalTime().ToString("HH:mm:ss"):"시간 미확인";
   jobInfo.Text="최근 요청 · "+SourceName(Field(data,"source"))+" · "+Phase(status,hasLive)+" · "+when+"\r\n"+tokens+"\r\n대기 "+Seconds(Number(data,"queue_seconds"))+" · 첫 토큰 "+Seconds(Number(data,"first_token_seconds"))+" · "+speed;
  }catch(Exception ex){jobInfo.Text="기록을 다시 읽는 중: "+ex.Message;}
 }
 public static string Timing(Dictionary<string,object> d){
  if(d==null)return "시간 측정 대기";
  Func<string,string> sec=k=>d.ContainsKey(k)&&d[k]!=null?Convert.ToDouble(d[k]).ToString("0.00")+"초":"—";
  return "대기 "+sec("queue_seconds")+" · 첫 응답 "+sec("first_token_seconds")+" · 생성 "+sec("generation_seconds");
 }
 static Icon MakeIcon(Color color,string glyph){using(var b=new Bitmap(32,32))using(var g=Graphics.FromImage(b))using(var brush=new SolidBrush(color))using(var f=new Font("Arial",17,FontStyle.Bold))using(var fmt=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center}){g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.Transparent);g.FillEllipse(brush,1,1,30,30);g.DrawString(glyph,f,Brushes.White,new RectangleF(0,0,32,32),fmt);IntPtr h=b.GetHicon();Icon result=(Icon)Icon.FromHandle(h).Clone();DestroyIcon(h);return result;}}
 static string GpuStatus(){
  try{using(var process=Process.Start(new ProcessStartInfo("nvidia-smi.exe","--query-gpu=memory.used,memory.total,temperature.gpu,utilization.gpu --format=csv,noheader,nounits"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})){
   if(!process.WaitForExit(3000)){process.Kill();return "GPU 정보 응답 지연";}
   if(process.ExitCode!=0)return "GPU 정보 사용 불가";
   string[] values=process.StandardOutput.ReadToEnd().Split(new[]{',','\r','\n'},StringSplitOptions.RemoveEmptyEntries);
   if(values.Length<4)return "GPU 정보 사용 불가";
   double used=double.Parse(values[0],System.Globalization.CultureInfo.InvariantCulture),total=double.Parse(values[1],System.Globalization.CultureInfo.InvariantCulture);
   return string.Format("GPU 전체 {0}% · VRAM {1:0.0}/{2:0.0} GiB ({3:0}%) · {4}°C",values[3].Trim(),used/1024,total/1024,used/total*100,values[2].Trim());
  }}catch{return "GPU 정보 사용 불가";}
 }
 static string Get(string suffix){var req=(HttpWebRequest)WebRequest.Create(ServerUrl+suffix);req.Timeout=1000;req.ReadWriteTimeout=1000;using(var res=req.GetResponse())using(var reader=new StreamReader(res.GetResponseStream()))return reader.ReadToEnd();}
 static string Backend(){try{return File.ReadAllText(BackendFile).Trim()=="ninfer"?"ninfer":"vLLM";}catch{return "vLLM";}}
 void ChooseBackend(string name){
  try{Get("health");MessageBox.Show("현재 서버를 먼저 종료한 뒤 모델을 선택하세요.","Qwen");return;}catch{}
  try{
   File.WriteAllText(BackendFile,name=="ninfer"?"ninfer":"vllm");
   bool hermesUpdated=false;
   if(File.Exists(HermesConfig)){
    string config=File.ReadAllText(HermesConfig);
    if(Regex.Matches(config,@"(?m)^(  context_length: )\d+(?=\r?$)").Count==1){File.WriteAllText(HermesConfig,Regex.Replace(config,@"(?m)^(  context_length: )\d+(?=\r?$)","${1}"+(name=="ninfer"?"245760":"65536")));hermesUpdated=true;}
   }
   note.Text="다음 서버 시작: "+Backend()+(hermesUpdated?" · Hermes 재시작 후 문맥 설정 적용":" · Hermes 문맥 설정은 변경되지 않음");
  }catch(Exception error){MessageBox.Show(error.Message,"모델 선택 실패");}
 }
 static double Metric(string body,string name){var matches=Regex.Matches(body,@"^vllm:"+name+@"(?:\{[^\r\n]*\})?\s+([0-9.eE+\-]+)",RegexOptions.Multiline);if(matches.Count==0)return double.NaN;double sum=0;foreach(Match m in matches)sum+=double.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);return sum;}
 static int Classify(bool healthy,bool loading,double running,double waiting){return !healthy?(loading?1:0):(!double.IsNaN(running)&&!double.IsNaN(waiting)?(running+waiting>0?3:2):4);}
 void Display(int status,double input,double output,double speed,double running,double waiting){
  string[] names={"꺼짐","준비 중","대기","작업 중","상태 확인 필요"};
  Text="Qwen "+Backend()+" · "+names[status];state.Text=names[status]+" · "+Backend();state.ForeColor=new Color[]{Color.SlateGray,Color.RoyalBlue,Color.SeaGreen,Color.DarkOrange,Color.Firebrick}[status];
  Icon=icons[status];tray.Icon=icons[status];tray.Text=Text;
  note.Text=status==0?"Codex가 직접 처리합니다.":status==1?"모델을 준비하고 있습니다.":status==3?string.Format("처리 {0}건 · 대기 {1}건",running,waiting):status==2?"필요한 작업을 Codex가 Qwen에 위임합니다.":"서버는 응답하지만 요청 통계를 읽지 못했습니다.";
  start.Enabled=!action&&status==0&&File.Exists(Path.Combine(Root,"start.sh"));stop.Enabled=!action&&status!=0&&File.Exists(Path.Combine(Root,"stop.sh"));
  UpdateUsageText(input,output);
  rate.Text=(double.IsNaN(speed)?"서버 출력 속도 —":string.Format("서버 출력 속도 {0:N1} tok/s",speed))+"\n"+gpuText;
  UpdateMini(status,speed,running,waiting);
 }
 void UpdateUsageText(double serverInput,double serverOutput){
  usage.Text=!Directory.Exists(SharedRequests)?"공통 대기열 기록 폴더 미연결":usageLedger==null?"기록 누적 계산 중…":usageLedger.Error!=null?"기록 누적 확인 필요":string.Format("기록 누적 {0:N0} 토큰 · {1:N0}건\n입력 {2:N0} · 출력 {3:N0}",usageLedger.Input+usageLedger.Output,usageLedger.Count,usageLedger.Input,usageLedger.Output);
  usage.Text+="\n이번 서버 "+(double.IsNaN(serverInput)||double.IsNaN(serverOutput)?"—":string.Format("{0:N0} 토큰",serverInput+serverOutput));
 }
 async Task Poll(){if(polling)return;polling=true;try{
  double[] data=await Task.Run(()=>{try{Get("health");try{string m=Get("metrics");double running=Metric(m,"num_requests_running"),waiting=Metric(m,"num_requests_waiting");try{using(var wc=new WebClient()){var q=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(wc.DownloadString(QueueUrl));running=Math.Max(running,Convert.ToDouble(q["running"]));waiting+=Convert.ToDouble(q["waiting"]);}}catch{}return new[]{1.0,running,waiting,Metric(m,"prompt_tokens_total"),Metric(m,"generation_tokens_total")};}catch{return new[]{1.0,double.NaN,double.NaN,double.NaN,double.NaN};}}catch{return new[]{0.0,0.0,0.0,double.NaN,double.NaN};}});
  bool live=data[0]==1;bool loading=requested!=DateTime.MinValue&&(DateTime.UtcNow-requested).TotalMinutes<10;
  if(server!=null&&server.HasExited&&!live)loading=false;if(live)requested=DateTime.MinValue;
  double speed=double.NaN;DateTime now=DateTime.UtcNow;if(live&&!double.IsNaN(data[4])){if(previousTokens>=0&&data[4]>=previousTokens)speed=(data[4]-previousTokens)/(now-previousTime).TotalSeconds;previousTokens=data[4];previousTime=now;}else previousTokens=-1;
  if(gpuTask==null&&(now-lastGpuPoll).TotalSeconds>=10){lastGpuPoll=now;gpuTask=Task.Run(()=>GpuStatus());}
  if(gpuTask!=null&&gpuTask.IsCompleted){gpuText=gpuTask.Status==TaskStatus.RanToCompletion?gpuTask.Result:"GPU 정보 사용 불가";gpuTask=null;}
  bool ledgerChanged=false;
  if(usageScan!=null&&usageScan.IsCompleted){if(usageScan.Status==TaskStatus.RanToCompletion){usageLedger=usageScan.Result;ledgerChanged=true;}usageScan=null;}
  if(usageLedger!=null){string path;int n=0;while(n++<100&&dirtyUsage.TryDequeue(out path)){usageLedger.AddFile(path);ledgerChanged=true;}if(usageWindow!=null&&!usageWindow.IsDisposed&&usageWindow.Visible)usageWindow.UpdateStats(usageLedger);}
  if(ledgerChanged&&insightsWindow!=null&&!insightsWindow.IsDisposed&&insightsWindow.Visible)insightsWindow.OnLedgerUpdated();
  Display(Classify(live,loading,data[1],data[2]),data[3],data[4],speed,data[1],data[2]);
  if(Visible)RefreshJobs();
 }catch(Exception ex){note.Text=ex.Message;}finally{polling=false;}}
 static string WslPath(string path){if(path.Length<3||path[1]!=':'||path[2]!='\\')throw new Exception("WSL에서 사용할 Windows 드라이브 경로가 아닙니다.");return "/mnt/"+char.ToLowerInvariant(path[0])+path.Substring(2).Replace('\\','/');}
 static Process Run(string script){string distro=Environment.GetEnvironmentVariable("QWEN_WSL_DISTRIBUTION")??"Ubuntu-24.04",user=Environment.GetEnvironmentVariable("QWEN_WSL_USER")??"qwen";if(!Regex.IsMatch(distro,@"^[a-zA-Z0-9._-]+$")||!Regex.IsMatch(user,@"^[a-zA-Z0-9._-]+$"))throw new Exception("WSL 설정 이름을 확인하세요.");return Process.Start(new ProcessStartInfo("wsl.exe","-d "+distro+" -u "+user+" --exec bash \""+script+"\""){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});}
 async Task StartServer(){if(action)return;action=true;try{
  bool exists=await Task.Run(()=>{try{Get("health");return true;}catch{return false;}});
  if(!exists){string script=Path.Combine(Root,"start.sh");if(!File.Exists(script))throw new Exception("서버 시작 스크립트가 설정되지 않았습니다.");server=Run(WslPath(script));requested=DateTime.UtcNow;}
 }catch(Exception ex){MessageBox.Show(ex.Message,"Qwen");}finally{action=false;}await Poll();}
 async Task StopServer(){if(action)return;action=true;start.Enabled=stop.Enabled=false;note.Text="서버 종료 중…";try{string script=Path.Combine(Root,"stop.sh");if(!File.Exists(script))throw new Exception("서버 종료 스크립트가 설정되지 않았습니다.");using(var p=Run(WslPath(script))){bool ended=await Task.Run(()=>p.WaitForExit(45000));if(!ended||p.ExitCode!=0)throw new Exception("서버 종료를 확인하지 못했습니다.");}requested=DateTime.MinValue;}catch(Exception ex){MessageBox.Show(ex.Message,"Qwen");}finally{action=false;}await Poll();}
 static void CoreTest(){
  if(Classify(false,false,0,0)!=0||Classify(false,true,0,0)!=1||Classify(true,false,0,0)!=2||Classify(true,false,1,0)!=3||Classify(true,false,double.NaN,0)!=4)throw new Exception("State classification failed");
  if(Metric("vllm:generation_tokens_total{engine=\"0\"} 12\nvllm:generation_tokens_total{engine=\"1\"} 8","generation_tokens_total")!=20)throw new Exception("Metric aggregation failed");
  string directory=Path.Combine(Path.GetTempPath(),"qwen-core-"+Guid.NewGuid());Directory.CreateDirectory(directory);
  try{
   string completed=Path.Combine(directory,Guid.NewGuid()+".json"),incomplete=Path.Combine(directory,Guid.NewGuid()+".json"),queued=Path.Combine(directory,Guid.NewGuid()+".json");
   File.WriteAllText(completed,"{\"status\":\"completed\",\"created_at\":\"2026-09-21T12:00:00Z\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20}}");
   File.WriteAllText(incomplete,"{\"status\":\"incomplete\",\"created_at\":\"2026-09-22T12:00:00Z\",\"usage\":{\"prompt_tokens\":200,\"completion_tokens\":40}}");
   File.WriteAllText(queued,"{\"status\":\"queued\"}");
   var ledger=UsageLedger.Scan(directory);if(ledger.Count!=2||ledger.Input!=300||ledger.Output!=60)throw new Exception("Usage scan failed");
   File.WriteAllText(completed,"{\"status\":\"completed\",\"created_at\":\"2026-09-21T12:00:00Z\",\"usage\":{\"prompt_tokens\":110,\"completion_tokens\":20}}");ledger.AddFile(completed);ledger.AddFile(completed);
   if(ledger.Count!=2||ledger.Input!=310||ledger.Output!=60)throw new Exception("Incremental usage correction failed");
  }finally{Directory.Delete(directory,true);}
 }
 [STAThread] static void Main(string[] args){
  SetCurrentProcessExplicitAppUserModelID("Local.QwenStatus.App");Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  Directory.CreateDirectory(DataRoot);
  if(args.Length>0&&args[0]=="--core-test"){try{CoreTest();File.WriteAllText(Path.Combine(DataRoot,"core-test-result.txt"),"PASS");}catch(Exception ex){File.WriteAllText(Path.Combine(DataRoot,"core-test-result.txt"),"FAIL: "+ex);Environment.ExitCode=1;}return;}
  if(args.Length>0&&args[0]=="--insights-test"){InsightsTest();return;}
  if(args.Length>0&&args[0]=="--insights-ui-test"){InsightsUiTest();return;}
  if(args.Length>0&&args[0]=="--token-test-ui"){TokenTestWindow.Test();return;}
  if(args.Length>0&&args[0]=="--usage-scan"){
   var ledger=UsageLedger.Scan(SharedRequests);
   File.WriteAllText(Path.Combine(DataRoot,"usage-scan-result.txt"),string.Format("input={0};output={1};total={2};count={3};first={4:yyyy-MM-dd}",ledger.Input,ledger.Output,ledger.Input+ledger.Output,ledger.Count,ledger.FirstDay));return;
  }
  if(args.Length>0&&args[0]=="--usage-test"){
   string directory=Path.Combine(Path.GetTempPath(),"qwen-usage-"+Guid.NewGuid().ToString());Directory.CreateDirectory(directory);
   string a=Path.Combine(directory,Guid.NewGuid().ToString()+".json"),b=Path.Combine(directory,Guid.NewGuid().ToString()+".json"),pending=Path.Combine(directory,Guid.NewGuid().ToString()+".json");
   try{
    File.WriteAllText(a,"{\"status\":\"completed\",\"created_at\":\"2026-09-21T00:00:00+09:00\",\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20}}");
    File.WriteAllText(b,"{\"status\":\"incomplete\",\"created_at\":\"2026-09-22T00:00:00+09:00\",\"usage\":{\"prompt_tokens\":200,\"completion_tokens\":40}}");
    File.WriteAllText(pending,"{\"status\":\"queued\"}");
    var ledger=UsageLedger.Scan(directory);if(ledger.Count!=2||ledger.Input!=300||ledger.Output!=60||ledger.DayTotal(new DateTime(2026,9,21))!=120)throw new Exception("Historical totals failed");
    File.WriteAllText(a,"{\"status\":\"completed\",\"created_at\":\"2026-09-21T00:00:00+09:00\",\"usage\":{\"prompt_tokens\":110,\"completion_tokens\":20}}");ledger.AddFile(a);ledger.AddFile(a);
    if(ledger.Count!=2||ledger.Input!=310||ledger.Output!=60||ledger.DayTotal(new DateTime(2026,9,21))!=130)throw new Exception("Incremental count or idempotency failed");
    using(var window=new UsageWindow()){window.UpdateStats(ledger);window.Show();Application.DoEvents();using(var bmp=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(bmp,new Rectangle(0,0,bmp.Width,bmp.Height));bmp.Save(Path.Combine(DataRoot,"usage-preview.png"));}window.Close();}
   }finally{foreach(var file in Directory.GetFiles(directory))File.Delete(file);Directory.Delete(directory);}
   File.WriteAllText(Path.Combine(DataRoot,"usage-test.txt"),"PASS: historical totals, incomplete usage, queued exclusion, incremental correction and no double counting");return;
  }
  if(args.Length>0&&args[0]=="--status-test"){
   string directory=Path.Combine(Path.GetTempPath(),"qwen-status-"+Guid.NewGuid().ToString());Directory.CreateDirectory(directory);
   string fixture=Path.Combine(directory,Guid.NewGuid().ToString()+".json");
   try{using(var app=new QwenStatus()){
    app.hideContentBox.Checked=false;
    app.history.Items.Add(new JobItem{Path=fixture,Title="status fixture"});app.history.SelectedIndex=0;
    File.WriteAllText(fixture,"{\"status\":\"queued\",\"source\":\"direct-chat\",\"created_at\":\"2026-09-23T00:00:00Z\"}");app.ShowJob();
    if(!app.jobInfo.Text.Contains("공통 대기열")||!app.jobInfo.Text.Contains("직접 대화"))throw new Exception("Queued phase failed");
    File.WriteAllText(fixture,"{\"status\":\"running\",\"source\":\"direct-chat\"}");app.ShowJob();
    if(!app.jobInfo.Text.Contains("첫 토큰 준비 중"))throw new Exception("First token phase failed");
    File.WriteAllText(Path.ChangeExtension(fixture,"live.txt"),"생성 중인 답변");app.ShowJob();
    if(!app.jobInfo.Text.Contains("답변 생성 중")||!app.outputText.Text.Contains("생성 중인 답변"))throw new Exception("Streaming phase failed");
   long halfContext=Backend()=="ninfer"?122880:32768;
   File.WriteAllText(fixture,"{\"status\":\"completed\",\"source\":\"direct-chat\",\"usage\":{\"prompt_tokens\":"+halfContext+",\"completion_tokens\":100},\"queue_seconds\":1,\"first_token_seconds\":2,\"generation_seconds\":4}");app.ShowJob();
   if(app.contextBar.Value!=500||!app.jobInfo.Text.Contains("25.0 tok/s")||!app.jobInfo.Text.Contains("50%"))throw new Exception("Usage display failed");
    using(var watcher=app.WatchJobs(directory)){
     string next=Path.Combine(directory,Guid.NewGuid().ToString()+".json");File.WriteAllText(next,"{\"status\":\"queued\"}");
     if(!SpinWait.SpinUntil(()=>{lock(app.jobLock)return app.latestJobPath==next;},2000))throw new Exception("Live request detection failed");
    }
    app.usageLedger=UsageLedger.Scan(directory);app.Display(2,153,190,0,0,0);
    app.Show();Application.DoEvents();using(var bmp=new Bitmap(app.Width,app.Height)){app.DrawToBitmap(bmp,new Rectangle(0,0,bmp.Width,bmp.Height));bmp.Save(Path.Combine(DataRoot,"status-preview.png"));}
    app.quitting=true;app.Close();
   }}finally{foreach(var file in Directory.GetFiles(directory))File.Delete(file);Directory.Delete(directory);}
   File.WriteAllText(Path.Combine(DataRoot,"status-test.txt"),"PASS: queue, first token, streaming and completion phases; context bar; output speed");return;
  }
  if(args.Length>0&&args[0]=="--self-test"){
   if(Classify(false,false,0,0)!=0||Classify(false,true,0,0)!=1||Classify(true,false,0,0)!=2||Classify(true,false,1,0)!=3||Classify(true,false,0,1)!=3||Classify(true,false,double.NaN,0)!=4)throw new Exception("State test failed");
   if(Metric("vllm:generation_tokens_total{engine=\"0\"} 12\nvllm:generation_tokens_total{engine=\"1\"} 8","generation_tokens_total")!=20)throw new Exception("Metric test failed");
   using(var app=new QwenStatus()){app.Display(3,153,190,51.6,1,0);string fixture=Path.Combine(DataRoot,"io-selftest.json");File.WriteAllText(fixture,"{\"status\":\"completed\"}");File.WriteAllText(Path.ChangeExtension(fixture,"request.txt"),"입력 테스트: 함수 작성");File.WriteAllText(Path.ChangeExtension(fixture,"md"),"출력 테스트: return 42;");app.history.Items.Add(new JobItem{Path=fixture,Title="입출력 표시 검사"});app.history.SelectedIndex=0;app.ShowJob();if(!app.inputText.Text.Contains("입력 테스트")||!app.outputText.Text.Contains("return 42"))throw new Exception("IO display failed");File.Delete(fixture);File.Delete(Path.ChangeExtension(fixture,"request.txt"));File.Delete(Path.ChangeExtension(fixture,"md"));app.Show();Application.DoEvents();using(var bmp=new Bitmap(app.Width,app.Height)){app.DrawToBitmap(bmp,new Rectangle(0,0,bmp.Width,bmp.Height));bmp.Save(Path.Combine(DataRoot,"preview.png"));}app.quitting=true;app.Close();}File.WriteAllText(Path.Combine(DataRoot,"test-result.txt"),"PASS: state transitions, metric aggregation and form rendering");return;
  }
  bool created;
  using(var showRequest=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\QwenStatusApp.Show"))
  using(var mutex=new Mutex(true,"Local\\QwenStatusApp",out created)){
   if(!created){UiLog("secondary signal");showRequest.Set();return;} UiLog("primary startup");
   using(var app=new QwenStatus())using(var showTimer=new System.Windows.Forms.Timer()){
    // Keep a real window handle on the UI thread even during tray-only startup.
    IntPtr handle=app.Handle; UiLog("window handle ready");
    var context=new ApplicationContext();app.FormClosed+=(s,e)=>context.ExitThread();
    showTimer.Interval=200;showTimer.Tick+=(s,e)=>{if(showRequest.WaitOne(0))app.Restore();};showTimer.Start();
    if(args.Contains("--show"))app.Restore();
    Application.Run(context);
   }
  }
 }
}

