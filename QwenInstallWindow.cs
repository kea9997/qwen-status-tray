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
  var info=new ProcessStartInfo(SetupShell(),"-NoProfile -File \""+Path.Combine(ManagedRoot(),"setup","setup.ps1")+"\" -Action "+action+" -InstallRoot \""+ManagedRoot()+"\" -Backend "+(Backend()=="ninfer"?"ninfer":"vllm")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
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
  readonly TextBox installRoot=new TextBox(),log=new TextBox(),tokenFile=new TextBox();readonly Label state=new Label();readonly ProgressBar progress=new ProgressBar();readonly FlowLayoutPanel buttons=new FlowLayoutPanel();
  bool busy;
  internal InstallerWindow(QwenStatus app){
   Text="Qwen 설치 도우미";ClientSize=new Size(820,650);MinimumSize=new Size(760,590);Font=new Font("Malgun Gothic",10);BackColor=Color.FromArgb(245,247,250);StartPosition=FormStartPosition.CenterScreen;
   var top=new Panel{Dock=DockStyle.Top,Height=289,Padding=new Padding(18)};Controls.Add(top);
   top.Controls.Add(new Label{Text="로컬 AI 설치 · AI에게 설치 맡기기",Font=new Font(Font.FontFamily,16,FontStyle.Bold),Bounds=new Rectangle(18,14,760,36)});
   top.Controls.Add(new Label{Text="설치할 구성만 선택하세요. 모델 다운로드는 수십 GB 이상이며, 서버 시작은 직접 결정합니다.",Bounds=new Rectangle(18,56,780,26)});
   top.Controls.Add(new Label{Text="설치 폴더",Bounds=new Rectangle(18,96,105,27)});installRoot.Bounds=new Rectangle(124,91,671,30);installRoot.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;installRoot.Text=Root;top.Controls.Add(installRoot);
   backend.DropDownStyle=ComboBoxStyle.DropDownList;backend.Items.AddRange(new object[]{"공통 구성 · 기존 vLLM 연결","공통 구성 · 기존 ninfer 연결","vLLM · 64K 설치","ninfer · 224K 설치","vLLM + ninfer 설치"});backend.SelectedIndex=0;backend.Bounds=new Rectangle(18,137,255,30);top.Controls.Add(backend);
   model.DropDownStyle=ComboBoxStyle.DropDownList;model.Items.AddRange(new object[]{"표준 모델","무검열 모델 · 접근 동의 필요"});model.SelectedIndex=0;model.Bounds=new Rectangle(285,137,298,30);top.Controls.Add(model);
   foreach(var choice in new[]{backend,model}){choice.DrawMode=DrawMode.OwnerDrawFixed;choice.ItemHeight=24;choice.DrawItem+=(sender,e)=>{e.DrawBackground();if(e.Index>=0)TextRenderer.DrawText(e.Graphics,((ComboBox)sender).Items[e.Index].ToString(),Font,e.Bounds,e.ForeColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);e.DrawFocusRectangle();};}
   hermes.Text="Hermes 함께 설치";hermes.AutoSize=true;hermes.Location=new Point(600,141);top.Controls.Add(hermes);
   configure.Text="이 PC 상태 앱 연결 설정도 선택한 설치 폴더로 변경 (기존 설정 백업)";configure.AutoSize=true;configure.Location=new Point(18,181);top.Controls.Add(configure);
   top.Controls.Add(new Label{Text="HF 토큰 파일",Bounds=new Rectangle(18,218,108,27)});tokenFile.Bounds=new Rectangle(128,213,539,30);top.Controls.Add(tokenFile);
   var browse=new Button{Text="파일 선택",Bounds=new Rectangle(678,212,117,32)};browse.Click+=(s,e)=>{using(var dialog=new OpenFileDialog{Title="무검열 모델 접근용 Hugging Face 토큰 파일 선택"}){if(dialog.ShowDialog(this)==DialogResult.OK)tokenFile.Text=dialog.FileName;}};top.Controls.Add(browse);
   top.Controls.Add(new Label{Text="무검열 모델만 토큰 파일이 필요합니다. Docker/WSL·접근 동의·계정 로그인은 사용자 단계입니다.",Bounds=new Rectangle(18,252,775,27),ForeColor=Color.DimGray});
   var bottom=new Panel{Dock=DockStyle.Bottom,Height=177,Padding=new Padding(18,4,18,10)};Controls.Add(bottom);
   buttons.Dock=DockStyle.Top;buttons.Height=84;buttons.WrapContents=true;bottom.Controls.Add(buttons);
   AddButton("설치 전 확인",()=>Run("Plan"));AddButton("선택 항목 설치",()=>Run("Install"));AddButton("연결 검증",()=>Run("Verify"));
   var prompt=new Button{Text="AI 설치 프롬프트 복사",AutoSize=true,Height=34};prompt.Click+=(s,e)=>{try{Clipboard.SetText(InstallPrompt());state.Text="설치 프롬프트를 복사했습니다. 로컬 도구가 있는 AI에게 전달하세요.";}catch(Exception ex){state.Text=ex.Message;}};buttons.Controls.Add(prompt);
   AddButton("대기열 켜기",()=>Run("StartGateway"));AddButton("대기열 끄기",()=>Run("StopGateway"));
   var prerequisites=new LinkLabel{Text="Docker Desktop 설치 안내",AutoSize=true,Location=new Point(18,95)};prerequisites.LinkClicked+=(s,e)=>Process.Start(new ProcessStartInfo("https://docs.docker.com/desktop/setup/install/windows-install/"){UseShellExecute=true});bottom.Controls.Add(prerequisites);
   progress.Dock=DockStyle.Bottom;progress.Height=8;bottom.Controls.Add(progress);
   state.Dock=DockStyle.Bottom;state.Height=46;state.Text="먼저 설치 전 확인을 누르세요. 현재 서버를 자동으로 바꾸거나 종료하지 않습니다.";bottom.Controls.Add(state);
   log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;log.WordWrap=true;log.Dock=DockStyle.Fill;log.BackColor=Color.White;log.Font=new Font("Malgun Gothic",9);Controls.Add(log);log.BringToFront();
   FormClosing+=(s,e)=>{if(busy&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
  }
  void AddButton(string title,Func<Task> action){var button=new Button{Text=title,AutoSize=true,Height=34};button.Click+=async(s,e)=>await action();buttons.Controls.Add(button);}
  static string Quote(string text){if(text.IndexOfAny(new[]{'"','\r','\n'})>=0)throw new ArgumentException("경로에 따옴표 또는 줄바꿈을 넣을 수 없습니다.");return "\""+text.TrimEnd('\\')+"\"";}
  void Append(string line){if(IsDisposed||!IsHandleCreated||line==null)return;try{BeginInvoke((Action)(()=>{if(IsDisposed)return;if(log.TextLength>150000)log.Text=log.Text.Substring(log.TextLength-100000);log.AppendText(line+Environment.NewLine);}));}catch(InvalidOperationException){}}
  async Task Run(string action){
   if(busy)return;
   string args;try{
    string root=Path.GetFullPath(installRoot.Text.Trim()),script=Path.Combine(Root,"setup","setup.ps1");if(!File.Exists(script))throw new FileNotFoundException("설치 스크립트가 없습니다. 앱 소스를 업데이트하세요.");
    args="-NoProfile -File "+Quote(script)+" -Action "+action+" -InstallRoot "+Quote(root)+" -Backend "+new[]{"existing","existing","vllm","ninfer","both"}[backend.SelectedIndex]+" -ExistingBackend "+(backend.SelectedIndex==1?"ninfer":"vllm")+" -Model "+(model.SelectedIndex==0?"standard":"uncensored")+(hermes.Checked?" -IncludeHermes":"")+(configure.Checked?" -ConfigureApp":"")+(string.IsNullOrWhiteSpace(tokenFile.Text)?"":" -HfTokenFile "+Quote(tokenFile.Text.Trim()));
    if(action=="Install"&&MessageBox.Show(this,"선택한 구성 요소와 모델을 다운로드하고 설치합니다.\n모델 준비에는 많은 디스크 공간과 시간이 필요합니다.\n선택한 연결 설정 변경은 백업 후 적용합니다.\n\n설치를 시작할까요?","선택 항목 설치",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
   }catch(Exception ex){state.Text=ex.Message;return;}
   busy=true;buttons.Enabled=false;backend.Enabled=model.Enabled=hermes.Enabled=configure.Enabled=installRoot.Enabled=false;progress.Style=ProgressBarStyle.Marquee;state.Text=action=="Install"?"설치 진행 중 · 창을 닫으면 트레이에서 계속 진행합니다.":"확인 중…";log.Clear();
   try{
    int code=await Task.Run(()=>{
     var info=new ProcessStartInfo(SetupShell(),args){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
     using(var process=new Process{StartInfo=info}){process.OutputDataReceived+=(s,e)=>Append(e.Data);process.ErrorDataReceived+=(s,e)=>Append(e.Data);process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();process.WaitForExit();return process.ExitCode;}
    });
    if(!IsDisposed)state.Text=code==0?(action=="Install"?"설치 단계 완료 · 연결 검증으로 실제 준비 상태를 확인하세요.":"확인 완료 · 항목별 결과를 확인하세요."):"완료되지 않았습니다 · 위 로그의 필요한 조치 후 다시 시도하세요. 종료 코드 "+code;
   }catch(Exception ex){if(!IsDisposed)state.Text="설치 도구 실행 실패: "+ex.Message;}
   finally{busy=false;if(!IsDisposed){buttons.Enabled=true;backend.Enabled=model.Enabled=hermes.Enabled=configure.Enabled=installRoot.Enabled=true;progress.Style=ProgressBarStyle.Blocks;}}
  }
  internal static void Test(){
   object prior;bool existed=Settings.TryGetValue("managedInstallRoot",out prior);string selected=Path.Combine(Path.GetTempPath(),"qwen-owned-test");
   try{Settings["managedInstallRoot"]=selected;if(ManagedRoot()!=selected||ServerScript("start.sh")!=Path.Combine(selected,"start.sh"))throw new Exception("Managed controls did not follow selected install root");}finally{if(existed)Settings["managedInstallRoot"]=prior;else Settings.Remove("managedInstallRoot");}
   if(Quote(@"C:\My Apps\Qwen")!="\"C:\\My Apps\\Qwen\"")throw new Exception("Install path quoting failed");
   bool rejected=false;try{Quote("bad\"path");}catch(ArgumentException){rejected=true;}if(!rejected)throw new Exception("Install path accepted quote");
   using(var window=new InstallerWindow(null)){window.Show();Application.DoEvents();if(window.configure.Checked||window.backend.SelectedIndex!=0)throw new Exception("Installer modifies existing setup by default");using(var image=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(DataRoot,"installer-preview.png"));}window.Close();}
   File.WriteAllText(Path.Combine(DataRoot,"install-test.txt"),"PASS: installer window, existing-server default, explicit settings selection and safe path quoting");
  }
 }
}
