using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

partial class QwenStatus {
 InstallerWindow installerWindow;
 void OpenInstaller(){if(installerWindow==null||installerWindow.IsDisposed)installerWindow=new InstallerWindow(this);installerWindow.Show();installerWindow.WindowState=FormWindowState.Normal;installerWindow.Activate();}
 static string ManagedRoot(){return SettingPath("managedInstallRoot",Root);}
 static bool ManagedInstall(){return File.Exists(Path.Combine(ManagedRoot(),"setup-state.json"))&&File.Exists(Path.Combine(ManagedRoot(),"setup","setup.ps1"));}
 static string ServerScript(string name){return Path.Combine(ManagedRoot(),name);}
 static readonly object managedLogLock=new object();
 static string ManagedLog(){return Path.Combine(ManagedRoot(),"setup-logs","controller.log");}
 internal static string ConfiguredNode(){return Setting("localNodeCommand",Environment.GetEnvironmentVariable("QWEN_NODE_EXE")??(File.Exists(Path.Combine(ManagedRoot(),"dependencies","node","node.exe"))?Path.Combine(ManagedRoot(),"dependencies","node","node.exe"):"node.exe"));}
 static string SetupShell(){
  string configured=Setting("setupPowerShell","");if(!string.IsNullOrWhiteSpace(configured))return configured;
  string installed=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PowerShell","7","pwsh.exe");if(File.Exists(installed))return installed;
  foreach(string path in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)){try{string file=Path.Combine(path,"pwsh.exe");if(File.Exists(file))return file;}catch(ArgumentException){}}
  return "powershell.exe";
 }
 static Process RunManaged(string action){
  var info=new ProcessStartInfo(SetupShell(),"-NoProfile -File \""+Path.Combine(ManagedRoot(),"setup","setup.ps1")+"\" -Action "+action+" -InstallRoot \""+ManagedRoot()+"\" -Backend "+(Backend()=="strata"?"strata":Backend()=="ninfer"?"ninfer":"vllm")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
  Directory.CreateDirectory(Path.GetDirectoryName(ManagedLog()));
  lock(managedLogLock)File.AppendAllText(ManagedLog(),Environment.NewLine+DateTime.UtcNow.ToString("o")+" "+action+Environment.NewLine,Encoding.UTF8);
  info.RedirectStandardOutput=info.RedirectStandardError=true;info.StandardOutputEncoding=info.StandardErrorEncoding=Encoding.UTF8;
  var process=new Process{StartInfo=info};DataReceivedEventHandler record=(s,e)=>{if(e.Data!=null)try{lock(managedLogLock)File.AppendAllText(ManagedLog(),e.Data+Environment.NewLine,Encoding.UTF8);}catch(IOException){}};
  process.OutputDataReceived+=record;process.ErrorDataReceived+=record;process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();return process;
 }
 static bool OpenManagedHermes(){
  if(!File.Exists(Path.Combine(ManagedRoot(),"hermes","install-result.json")))return false;
  Process.Start(new ProcessStartInfo(SetupShell(),"-NoProfile -NoExit -File \""+Path.Combine(ManagedRoot(),"setup","open-hermes.ps1")+"\" -InstallRoot \""+ManagedRoot()+"\""){UseShellExecute=false,CreateNoWindow=false,WindowStyle=ProcessWindowStyle.Normal,WorkingDirectory=ManagedRoot()});return true;
 }
 static string InstallPrompt(){
  string path=Path.Combine(Root,"AI-INSTALL-PROMPT.md");
  if(!File.Exists(path))throw new FileNotFoundException("AI 설치 안내 파일이 없습니다. 앱 소스를 업데이트하세요.",path);
  return File.ReadAllText(path,Encoding.UTF8);
 }
 sealed class InstallerWindow : Form {
  readonly ComboBox backend=new ComboBox(),model=new ComboBox();readonly CheckBox hermes=new CheckBox(),configure=new CheckBox();
  readonly TextBox installRoot=new TextBox(),log=new TextBox(),tokenFile=new TextBox(),url=new TextBox(),modelId=new TextBox(),source=new TextBox(),python=new TextBox(),config=new TextBox(),engineHash=new TextBox(),gpu=new TextBox(),context=new TextBox(),workers=new TextBox(),ram=new TextBox(),prefill=new TextBox();
  readonly Label state=new Label(),help=new Label();readonly ProgressBar progress=new ProgressBar();readonly FlowLayoutPanel buttons=new FlowLayoutPanel();readonly TableLayoutPanel fields=new TableLayoutPanel(),native=new TableLayoutPanel(),layout=new TableLayoutPanel();
  Button startBackend,stopBackend;bool busy;bool Strata {get{return backend.SelectedIndex<2;}}bool Native {get{return backend.SelectedIndex==1;}}
  internal InstallerWindow(QwenStatus app){
   Text="Qwen 설치 도우미";ClientSize=new Size(820,720);MinimumSize=new Size(760,620);Font=new Font("Malgun Gothic",10);BackColor=Color.FromArgb(245,247,250);StartPosition=FormStartPosition.CenterScreen;
   layout.Dock=DockStyle.Fill;layout.ColumnCount=1;layout.RowCount=5;layout.Padding=new Padding(14);Controls.Add(layout);
   layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,68));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,80));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,90));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50));
   var header=new Panel{Dock=DockStyle.Fill};header.Controls.Add(new Label{Text="로컬 AI 설치 · AI에게 설치 맡기기",Font=new Font(Font.FontFamily,16,FontStyle.Bold),Dock=DockStyle.Top,Height=36});header.Controls.Add(new Label{Text="기존 서버에 연결하거나 준비된 모델을 관리합니다. 설치만으로 서버를 켜지 않습니다.",Dock=DockStyle.Bottom,Height=26});layout.Controls.Add(header,0,0);
   var scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true};layout.Controls.Add(scroll,0,1);
   fields.Dock=DockStyle.Top;fields.AutoSize=true;fields.ColumnCount=2;fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,128));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));scroll.Controls.Add(fields);
   installRoot.Text=ManagedRoot();AddField(fields,"설치 폴더",installRoot);
   backend.DropDownStyle=ComboBoxStyle.DropDownList;backend.Items.AddRange(new object[]{"기존 Strata FN 연결","Strata FN · 네이티브 준비","기존 vLLM 연결","기존 ninfer 연결","vLLM · 64K 설치","ninfer · 224K 설치","vLLM + ninfer 설치"});AddField(fields,"구성",backend);
   model.DropDownStyle=ComboBoxStyle.DropDownList;AddField(fields,"모델 / 프로필",model);DrawSelection(backend);DrawSelection(model);
   url.Text=Backend()=="strata"?ServerUrl.TrimEnd('/') : "http://127.0.0.1:8088";AddField(fields,"기존 서버 URL",url);
   modelId.Text="";AddField(fields,"실제 모델 ID",modelId);
   native.AutoSize=true;native.Dock=DockStyle.Top;native.ColumnCount=2;native.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,128));native.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
   source.Text="";AddField(native,"Strata 소스",source);AddField(native,"전용 Python",python);AddField(native,"SC117 설정",config);AddField(native,"엔진 SHA256",engineHash);
   gpu.Text="";context.Text="262144";workers.Text="10";ram.Text="26";prefill.Text="auto";
   AddField(native,"NVIDIA GPU 번호",gpu);AddField(native,"문맥 용량",context);AddField(native,"CPU workers",workers);AddField(native,"RAM 상주 GiB",ram);AddField(native,"Prefill",prefill);
   fields.RowCount++;fields.Controls.Add(native,0,fields.RowCount-1);fields.SetColumnSpan(native,2);
   AddField(fields,"HF 토큰 파일",tokenFile);
   hermes.Text="Hermes 함께 설치";hermes.AutoSize=true;AddField(fields,"선택 구성",hermes);
   configure.Text="이 PC 앱 연결도 변경 (기존 설정 백업)";configure.AutoSize=true;AddField(fields,"앱 설정",configure);
   help.AutoSize=true;help.MaximumSize=new Size(710,0);help.ForeColor=Color.DimGray;help.Margin=new Padding(3,8,3,12);fields.RowCount++;fields.Controls.Add(help,0,fields.RowCount-1);fields.SetColumnSpan(help,2);
   backend.SelectedIndexChanged+=(s,e)=>SelectBackend();model.SelectedIndexChanged+=(s,e)=>{if(Strata)prefill.Text=model.SelectedIndex==1?"16384":"auto";};backend.SelectedIndex=0;
   log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;log.WordWrap=true;log.Dock=DockStyle.Fill;log.BackColor=Color.White;log.Font=new Font("Malgun Gothic",9);layout.Controls.Add(log,0,2);
   buttons.Dock=DockStyle.Fill;buttons.WrapContents=true;buttons.AutoScroll=true;layout.Controls.Add(buttons,0,3);
   AddButton("설치 전 확인",()=>Run("Plan"));AddButton("선택 항목 준비",()=>Run("Install"));AddButton("연결 검증",()=>Run("Verify"));
   AddButton("대기열 켜기",()=>Run("StartGateway"));AddButton("대기열 끄기",()=>Run("StopGateway"));startBackend=AddButton("모델 켜기",()=>Run("StartBackend"));stopBackend=AddButton("모델 끄기",()=>Run("StopBackend"));
   var prompt=new Button{Text="AI 설치 프롬프트 복사",AutoSize=true,Height=34};prompt.Click+=(s,e)=>{try{Clipboard.SetText(InstallPrompt());state.Text="설치 프롬프트를 복사했습니다. 로컬 도구가 있는 AI에게 전달하세요.";}catch(Exception ex){state.Text=ex.Message;}};buttons.Controls.Add(prompt);
   var statusPanel=new Panel{Dock=DockStyle.Fill};layout.Controls.Add(statusPanel,0,4);progress.Dock=DockStyle.Bottom;progress.Height=8;statusPanel.Controls.Add(progress);state.Dock=DockStyle.Fill;state.Text="먼저 설치 전 확인을 누르세요. 현재 서버를 자동으로 바꾸거나 종료하지 않습니다.";statusPanel.Controls.Add(state);state.BringToFront();
   SelectBackend();SizeChanged+=(s,e)=>ResizeFields();FormClosing+=(s,e)=>{if(busy&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
  }
  static void AddField(TableLayoutPanel panel,string title,Control control){int row=panel.RowCount++;panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.Controls.Add(new Label{Text=title,AutoSize=true,Margin=new Padding(3,8,8,5)},0,row);control.Dock=DockStyle.Top;control.Margin=new Padding(3,3,3,5);panel.Controls.Add(control,1,row);}
  static void DrawSelection(ComboBox box){box.DrawMode=DrawMode.OwnerDrawFixed;box.ItemHeight=24;box.DrawItem+=(s,e)=>{e.DrawBackground();int index=e.Index<0?box.SelectedIndex:e.Index;if(index>=0&&index<box.Items.Count)TextRenderer.DrawText(e.Graphics,box.Items[index].ToString(),box.Font,e.Bounds,box.Enabled?e.ForeColor:SystemColors.GrayText,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);e.DrawFocusRectangle();};}
  void ShowField(Control control,bool visible){int row=fields.GetRow(control);foreach(Control item in fields.Controls)if(fields.GetRow(item)==row)item.Visible=visible;while(fields.RowStyles.Count<=row)fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));fields.RowStyles[row].SizeType=visible?SizeType.AutoSize:SizeType.Absolute;fields.RowStyles[row].Height=0;}
  void ResizeFields(){layout.RowStyles[1].SizeType=Native?SizeType.Percent:SizeType.Absolute;layout.RowStyles[1].Height=Native?100:Math.Min(330,Math.Max(220,ClientSize.Height-layout.Padding.Vertical-68-90-50-64));layout.RowStyles[2].SizeType=Native?SizeType.Absolute:SizeType.Percent;layout.RowStyles[2].Height=100;}
  void SelectBackend(){
   int chosen=model.SelectedIndex;model.Items.Clear();model.Items.AddRange(Strata?new object[]{"SC117 IQ3_XXS · 예시 프로필","SC117 IQ2_XS · RAM/속도 선택"}:new object[]{"표준 모델","무검열 모델 · 접근 동의 필요"});model.SelectedIndex=chosen==1?1:0;
   if(!Strata&&url.Text=="http://127.0.0.1:8088")url.Text="http://127.0.0.1:18021";
   bool attach=backend.SelectedIndex==0||backend.SelectedIndex==2||backend.SelectedIndex==3;
   model.Enabled=!busy&&(Native||!Strata);ShowField(native,Native);ShowField(tokenFile,!Strata);url.Enabled=!busy&&attach;modelId.Enabled=!busy&&attach;tokenFile.Enabled=!busy&&!Strata;
   ResizeFields();
   if(startBackend!=null)startBackend.Enabled=!busy&&backend.SelectedIndex!=0&&backend.SelectedIndex!=2&&backend.SelectedIndex!=3;
   if(stopBackend!=null)stopBackend.Enabled=startBackend.Enabled;
   help.Text=Native?"검증된 Strata 소스·전용 Python·SC117 모델 설정이 먼저 필요합니다. Docker/WSL은 사용하지 않습니다. 아래 항목은 측정 예시이며 메모리와 작업에 맞게 조정하세요. 사진은 이 준비 경로에서 끕니다.":Strata?"기존 loopback URL을 명시하세요. 모델 ID를 비우면 준비 시 health/models로 확인합니다. Strata 게이트웨이는 텍스트 전용입니다. 외부 서버 시작·종료는 원래 도구에서 수행하세요.":"기존 vLLM/ninfer 호환 경로입니다. 새 서버 설치에는 Docker/WSL이 필요합니다. gated 모델만 본인 접근 동의 후 HF 토큰 파일의 경로를 입력하세요.";
  }
  Button AddButton(string title,Func<Task> action){var button=new Button{Text=title,AutoSize=true,Height=34,Margin=new Padding(3,5,5,3)};button.Click+=async(s,e)=>await action();buttons.Controls.Add(button);return button;}
  static string Quote(string text){if(text.IndexOfAny(new[]{'"','\r','\n'})>=0)throw new ArgumentException("경로에 따옴표 또는 줄바꿈을 넣을 수 없습니다.");return "\""+text.TrimEnd('\\')+"\"";}
  void Append(string line){if(IsDisposed||!IsHandleCreated||line==null)return;try{BeginInvoke((Action)(()=>{if(IsDisposed)return;if(log.TextLength>150000)log.Text=log.Text.Substring(log.TextLength-100000);log.AppendText(line+Environment.NewLine);var match=System.Text.RegularExpressions.Regex.Match(line,@"\[(\d+)/(\d+)\]");if(match.Success){progress.Style=ProgressBarStyle.Blocks;progress.Value=Math.Min(100,int.Parse(match.Groups[1].Value)*100/int.Parse(match.Groups[2].Value));}}));}catch(InvalidOperationException){}}
  string Arguments(string action){
   string root=Path.GetFullPath(installRoot.Text.Trim()),script=Path.Combine(Root,"setup","setup.ps1");if(!File.Exists(script))throw new FileNotFoundException("설치 스크립트가 없습니다. 앱 소스를 업데이트하세요.");
   string args="-NoProfile -File "+Quote(script)+" -Action "+action+" -InstallRoot "+Quote(root)+" -Backend "+new[]{"existing","strata","existing","existing","vllm","ninfer","both"}[backend.SelectedIndex]+" -ExistingBackend "+(Strata?"strata":backend.SelectedIndex==3?"ninfer":"vllm")+" -Model "+(!Strata&&model.SelectedIndex==1?"uncensored":"standard")+(hermes.Checked?" -IncludeHermes":"")+(configure.Checked?" -ConfigureApp":"");
   if(backend.SelectedIndex==0||backend.SelectedIndex==2||backend.SelectedIndex==3){if(!string.IsNullOrWhiteSpace(url.Text))args+=" -UpstreamUrl "+Quote(url.Text.Trim());if(!string.IsNullOrWhiteSpace(modelId.Text))args+=" -ModelId "+Quote(modelId.Text.Trim());}
   if(!Strata&&!string.IsNullOrWhiteSpace(tokenFile.Text))args+=" -HfTokenFile "+Quote(tokenFile.Text.Trim());
   if(Native){
    args+=" -StrataProfile "+(model.SelectedIndex==1?"IQ2_XS":"IQ3_XXS");
    foreach(var pair in new[]{new[]{"StrataSource",source.Text},new[]{"StrataPython",python.Text},new[]{"StrataConfig",config.Text},new[]{"EngineSha256",engineHash.Text},new[]{"GpuIndex",gpu.Text},new[]{"ContextTokens",context.Text},new[]{"PoolWorkers",workers.Text},new[]{"ResidentBudgetGiB",ram.Text},new[]{"Prefill",prefill.Text}})if(!string.IsNullOrWhiteSpace(pair[1]))args+=" -"+pair[0]+" "+Quote(pair[1].Trim());
   }
   return args;
  }
  async Task Run(string action){
   if(busy||OfflineTest)return;
   string args;try{args=Arguments(action);if((action=="Install"||action=="StartBackend"||action=="StopBackend")&&MessageBox.Show(this,action=="Install"?"선택한 구성만 준비합니다. 기존 SC117 파일은 해시 검증하며 서버는 시작하지 않습니다.\n선택한 경우에만 앱 연결을 백업 후 변경합니다.\n\n진행할까요?":"선택한 관리 설치의 모델을 "+(action=="StartBackend"?"시작":"종료")+"할까요?","선택 항목 실행",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;}catch(Exception ex){state.Text=ex.Message;return;}
   busy=true;buttons.Enabled=false;fields.Enabled=false;progress.Style=ProgressBarStyle.Marquee;state.Text=action=="Install"?"준비 중 · 파일 검증에 시간이 걸릴 수 있습니다. 창을 닫아도 계속 진행합니다.":"선택한 동작 확인 중…";log.Clear();
   try{
    int code=await Task.Run(()=>{var info=new ProcessStartInfo(SetupShell(),args){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};using(var process=new Process{StartInfo=info}){process.OutputDataReceived+=(s,e)=>Append(e.Data);process.ErrorDataReceived+=(s,e)=>Append(e.Data);process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();process.WaitForExit();return process.ExitCode;}});
    if(!IsDisposed)state.Text=code==0?(action=="Install"?"준비 완료 · 모델 미시작 · 연결 검증으로 실제 loaded 상태를 확인하세요.":"동작 완료 · 항목별 결과를 확인하세요."):code==2?"아직 loaded 상태가 아닙니다 · 기존 프로세스와 로그를 확인한 뒤 연결 검증을 다시 누르세요.":"완료되지 않았습니다 · 위 로그의 조치 후 다시 시도하세요. 종료 코드 "+code;
   }catch(Exception ex){if(!IsDisposed)state.Text="설치 도구 실행 실패: "+ex.Message;}
   finally{busy=false;if(!IsDisposed){buttons.Enabled=true;fields.Enabled=true;SelectBackend();progress.Style=ProgressBarStyle.Blocks;}}
  }
  internal static void Test(){
   object prior;bool existed=Settings.TryGetValue("managedInstallRoot",out prior);string selected=Path.Combine(Path.GetTempPath(),"qwen-owned-test");
   try{Settings["managedInstallRoot"]=selected;if(ManagedRoot()!=selected||ServerScript("start.sh")!=Path.Combine(selected,"start.sh"))throw new Exception("Managed controls did not follow selected install root");}finally{if(existed)Settings["managedInstallRoot"]=prior;else Settings.Remove("managedInstallRoot");}
   if(Quote(@"C:\My Apps\Qwen")!="\"C:\\My Apps\\Qwen\"")throw new Exception("Install path quoting failed");
   bool rejected=false;try{Quote("bad\"path");}catch(ArgumentException){rejected=true;}if(!rejected)throw new Exception("Install path accepted quote");
   using(var window=new InstallerWindow(null)){
    window.Show();Application.DoEvents();if(window.configure.Checked||window.backend.SelectedIndex!=0||window.startBackend.Enabled||window.stopBackend.Enabled)throw new Exception("Installer modifies existing setup by default");
    foreach(int choice in new[]{0,1,4,5})foreach(bool minimum in new[]{false,true}){
     window.backend.SelectedIndex=choice;window.Size=minimum?window.MinimumSize:new Size(836,759);Application.DoEvents();
     if(choice==1&&(!window.native.Visible||!window.help.Text.Contains("Docker/WSL")))throw new Exception("Native Strata controls missing");
     string args=window.Arguments("Plan");if(choice==0&&(!args.Contains("-ExistingBackend strata")||args.Contains("-HfTokenFile")))throw new Exception("Strata attach arguments incorrect");
     if(window.backend.DrawMode!=DrawMode.OwnerDrawFixed||window.model.DrawMode!=DrawMode.OwnerDrawFixed||window.backend.SelectedItem==null||window.model.SelectedItem==null)throw new Exception("Installer selected labels are not drawn");
     if(choice==0&&!minimum&&(!window.configure.Visible||window.help.Bottom>window.fields.Parent.ClientSize.Height))throw new Exception("Attach settings/help hidden behind unnecessary scrolling");
     using(var image=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(DataRoot,"installer-"+choice+(minimum?"-minimum":"-normal")+".png"));}
    }
    window.busy=true;window.buttons.Enabled=false;if(window.startBackend.CanSelect)throw new Exception("Busy actions remain selectable");window.busy=false;window.buttons.Enabled=true;window.backend.SelectedIndex=0;window.state.Text="연결 오류 · URL 확인 후 연결 검증을 다시 누르세요.";
    window.Close();
   }
   File.WriteAllText(Path.Combine(DataRoot,"install-test.txt"),"PASS: Strata attach/native and legacy controls, explicit settings selection, quoting, busy/external actions, normal/minimum offline previews");
  }
 }
}
