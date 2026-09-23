using System;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

class TokenTestWindow : Form {
 static readonly string Root=QwenStatus.GatewayRoot;
 static string NodeExe(){string configured=Environment.GetEnvironmentVariable("QWEN_NODE_EXE");if(!string.IsNullOrWhiteSpace(configured))return configured;string bundled=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),@".cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe");return System.IO.File.Exists(bundled)?bundled:"node.exe";}
 ComboBox profile=new ComboBox();Button start=new Button(),cancel=new Button();TextBox output=new TextBox();Process runner;
 public TokenTestWindow(){
  Text="Qwen · 토큰 테스트";ClientSize=new Size(850,550);MinimumSize=new Size(700,440);Font=new Font("Malgun Gothic",10);
  var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12),ColumnCount=1,RowCount=3};Controls.Add(layout);
  layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,62));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
  var bar=new FlowLayoutPanel{Dock=DockStyle.Fill};layout.Controls.Add(bar);
  profile.DropDownStyle=ComboBoxStyle.DropDownList;profile.Width=205;profile.Items.AddRange(new object[]{"빠른 속도 검사 (3회)","8K 문맥 검사","32K 문맥 검사","64K 문맥 검사","224K 문맥 검사 (ninfer)"});profile.SelectedIndex=0;bar.Controls.Add(profile);
  start.Text="테스트 시작";start.AutoSize=true;start.Click+=(s,e)=>Start();bar.Controls.Add(start);
  cancel.Text="취소";cancel.Enabled=false;cancel.Click+=(s,e)=>Cancel();bar.Controls.Add(cancel);
  var folder=new Button{Text="결과 폴더",AutoSize=true};folder.Click+=(s,e)=>{string path=System.IO.Path.Combine(Root,"token-tests");if(System.IO.Directory.Exists(path))Process.Start("explorer.exe",path);else MessageBox.Show("아직 토큰 테스트 결과 폴더가 없습니다.","Qwen");};bar.Controls.Add(folder);
  layout.Controls.Add(new Label{Dock=DockStyle.Fill,Text="서버를 수동으로 켜 둔 뒤 실행하세요. 다른 요청이 있으면 중단합니다.\r\n224K는 ninfer 모드 전용이며 첫 입력 처리에 10분 이상 걸릴 수 있습니다.\r\n전체 속도에는 입력 처리·대기 시간이 포함됩니다. 생성 속도는 스트리밍 기반 추정입니다."});
  output.Multiline=true;output.ReadOnly=true;output.WordWrap=true;output.ScrollBars=ScrollBars.Vertical;output.Dock=DockStyle.Fill;layout.Controls.Add(output);
  FormClosing+=(s,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}else Shutdown();};
 }
 void Append(string text){if(IsDisposed||!IsHandleCreated)return;try{BeginInvoke((Action)(()=>{if(!IsDisposed)output.AppendText(text+Environment.NewLine);}));}catch(InvalidOperationException){}}
 void Start(){
  if(runner!=null)return;output.Clear();
  if(!System.IO.File.Exists(System.IO.Path.Combine(Root,"token-test.mjs"))){Append("토큰 테스트 도구를 찾지 못했습니다. 설정된 대기열 설치를 확인하세요.");return;}
  var info=new ProcessStartInfo(NodeExe(),"\""+Root+"\\token-test.mjs\" "+new[]{"quick","8k","32k","64k","224k"}[profile.SelectedIndex]+" --ui"){WorkingDirectory=Root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,StandardErrorEncoding=Encoding.UTF8,StandardOutputEncoding=Encoding.UTF8};
  var p=new Process{StartInfo=info,EnableRaisingEvents=true};runner=p;start.Enabled=profile.Enabled=false;cancel.Enabled=true;
  p.OutputDataReceived+=(s,e)=>{if(e.Data!=null)Append(e.Data);};p.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)Append(e.Data);};
  p.Exited+=(s,e)=>{int code=p.ExitCode;Append("실행 종료 코드: "+code);if(IsHandleCreated&&!IsDisposed)try{BeginInvoke((Action)(()=>{if(runner==p){runner=null;start.Enabled=profile.Enabled=true;cancel.Enabled=false;}p.Dispose();}));}catch(InvalidOperationException){}};
  try{p.Start();p.BeginOutputReadLine();p.BeginErrorReadLine();}catch(Exception ex){runner=null;p.Dispose();start.Enabled=profile.Enabled=true;cancel.Enabled=false;Append(ex.Message);}
 }
 void Cancel(){try{if(runner!=null&&!runner.HasExited){runner.StandardInput.WriteLine("cancel");runner.StandardInput.Flush();cancel.Enabled=false;Append("취소 요청을 보냈습니다. 서버는 계속 켜져 있습니다.");}}catch(Exception ex){Append(ex.Message);}}
 public void Shutdown(){Cancel();try{if(runner!=null)runner.StandardInput.Close();}catch{} }
 public static void Test(){string data=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"QwenStatus");System.IO.Directory.CreateDirectory(data);using(var window=new TokenTestWindow()){window.Show();Application.DoEvents();if(window.profile.Items.Count!=5||!window.output.WordWrap||window.output.ScrollBars!=ScrollBars.Vertical)throw new Exception("Token test layout failed");using(var b=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height));b.Save(System.IO.Path.Combine(data,"token-test-preview.png"));}}System.IO.File.WriteAllText(System.IO.Path.Combine(data,"token-test-ui.txt"),"PASS: five profiles, wrapped vertical output, form rendering");}
}
