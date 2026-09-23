using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

partial class QwenStatus {
 DelegationWindow delegationWindow;
 static readonly string DelegationPolicyPath=Path.Combine(DataRoot,"delegation-policy.md");
 static void InitializeDelegationPolicy(){
  string config=Path.Combine(DataRoot,"delegation.json");
  if(!File.Exists(config)){DelegationProfile.Defaults().Save(DataRoot);return;}
  if(File.Exists(DelegationPolicyPath))return;
  string warning;var profile=DelegationProfile.Load(DataRoot,out warning);
  if(warning.Length>0)throw new InvalidOperationException(warning);
  Directory.CreateDirectory(DataRoot);DelegationProfile.AtomicWrite(DelegationPolicyPath,profile.Policy());
 }
 void CloseDelegation(){if(delegationWindow!=null&&!delegationWindow.IsDisposed)delegationWindow.Close();}
 void OpenDelegation(){
  if(delegationWindow==null||delegationWindow.IsDisposed)delegationWindow=new DelegationWindow(this);
  delegationWindow.Show();delegationWindow.WindowState=FormWindowState.Normal;delegationWindow.BringToFront();delegationWindow.Activate();
 }

 sealed class DelegationProfile {
  internal string Mode="auto",Client="codex",WorkerScript,NodeCommand;
  internal static readonly string[] Modes={"auto","maximum","minimum","manual"};
  internal static readonly string[] ModeLabels={"자동 (기본값)","최대 위임","최소 위임","수동 위임"};
  internal static readonly string[] Clients={"codex","claude-desktop","claude-code","web"};
  internal static readonly string[] ClientLabels={"Codex · 이 PC","Claude Desktop · 이 PC","Claude Code · 이 PC","ChatGPT / Claude · 웹"};
  internal static DelegationProfile Defaults(){
   string node=Environment.GetEnvironmentVariable("QWEN_NODE_EXE");
   return new DelegationProfile{WorkerScript=Setting("localWorkerScript",Path.Combine(RuntimePath("worker","codex-local-worker"),"server.mjs")),NodeCommand=string.IsNullOrWhiteSpace(node)?ConfiguredNode():node};
  }
  internal static string Description(string mode){
   switch(mode){
    case "maximum":return "범위와 검증 기준이 명확한 요약·추출·반복 처리·코드 초안을 적극적으로 위임합니다. 아주 작은 명령이나 인계·검토 비용만 늘어나는 작업은 직접 처리합니다.";
    case "minimum":return "크거나 반복되는 작업에서 인계·대기·검토를 포함해 분명한 이득이 예상될 때만 위임합니다. 그 외에는 메인 AI가 직접 처리합니다.";
    case "manual":return "사용자가 해당 작업의 로컬 위임을 명시적으로 요청할 때만 위임합니다. 요청이 없으면 메인 AI가 직접 처리합니다.";
    default:return "인계·대기·검토 비용과 예상 이득을 함께 판단합니다. 대표 작업으로 적합성을 확인한 요약·추출·초안 등 범위가 명확한 작업을 위임합니다.";
   }
  }
  internal string Policy(){
   return "# 로컬 Qwen 위임 정책\r\n\r\n"+
    "모드: "+ModeLabels[Array.IndexOf(Modes,Mode)]+" ("+Mode+")\r\n"+Description(Mode)+"\r\n"+
    "일반적인 적극 위임 안내와 충돌하면 이 프로필의 현재 모드를 우선한다. 사용자의 현재 명시적 지시는 항상 우선한다.\r\n\r\n"+
    "- 사용자가 선택한 메인 AI의 모델과 추론 수준을 유지한다. 복잡한 설계·디버깅·권한 판단·최종 통합과 검증은 메인 AI가 맡는다.\r\n"+
    "- 넓게 위임하기 전에 작고 대표적인 작업을 인수 기준으로 검사한다. 기초 정확성·속도 검사는 일반 능력 인증이 아니다.\r\n"+
    "- 로컬 서버의 시작·재시작·종료는 사용자의 명시적 요청이 있을 때만 한다. 이미 실행 중인 공유 대기열 127.0.0.1:18022만 쓰며 백엔드에 직접 우회하지 않는다.\r\n"+
    "- 비밀·자격 증명을 보내지 않는다. 필요한 절대 파일 경로와 정확한 줄 범위, 인터페이스, 제약, 인수 기준만 전달한다.\r\n"+
    "- qwen_submit 후 독립 작업을 계속하고 약 30초 뒤 기존 작업 ID로 qwen_result를 확인한다. 한 담당자만 추적하며 실행 중인 작업을 재제출하지 않는다.\r\n"+
    "- 반환된 산출물과 근거를 메인 AI가 검토·통합·테스트한다. 저비용 수정은 한 번만 요청한 뒤 직접 고친다. 주장한 테스트 결과만으로 통과시키지 않는다.\r\n"+
    "- 도구 또는 서버가 오프라인이면 이번 턴에 재확인하거나 자동으로 서버를 켜지 않고 직접 진행한다.\r\n"+
    "- 비교 기준 없이 시간·비용·OpenAI 토큰 절약량을 주장하지 않는다. 로컬 토큰과 메인 AI 사용량을 혼동하지 않는다.\r\n\r\n"+
    "이 파일은 로컬 프로필이다. 실제 적용에는 메인 AI가 이 정책을 읽거나 대화에 붙여넣어야 한다. 모드 변경 후에도 다시 읽기 또는 붙여넣기가 필요하며 모든 AI에 실시간으로 강제 적용되지 않는다.\r\n";
  }
  internal string Prompt(bool policyOnly){
   bool web=Client=="web";
   var b=new StringBuilder();
   if(policyOnly){
    b.AppendLine("이 대화에 아래 로컬 Qwen 위임 정책을 적용해 주세요. 먼저 qwen_submit/qwen_result 도구 연결 여부를 확인하고, 연결되지 않았거나 서버가 꺼져 있으면 직접 진행하세요. 자동 설치나 서버 시작은 요청하지 않습니다.");
   }else if(web){
    b.AppendLine("웹 대화에서 로컬 Qwen을 활용하기 위한 준비 안내입니다. 웹 ChatGPT/Claude는 이 PC의 127.0.0.1:18022에 직접 접근할 수 없습니다. 별도로 명시적 승인을 받은 연결 수단이 필요합니다. 이 문구만으로 연결되거나 도구가 설치되지 않습니다.");
    b.AppendLine("자동으로 터널을 설치하거나 권한을 추가하지 마세요. 이미 별도 승인된 연결로 qwen_submit/qwen_result가 제공되는 경우에만 아래 정책을 적용하고, 그렇지 않으면 메인 AI가 직접 작업하세요.");
   }else{
    b.AppendLine(ClientLabels[Array.IndexOf(Clients,Client)]+"에서 로컬 Qwen 위임 도구 연결을 준비해 주세요. 먼저 기존 설정과 아래 worker 파일, Node 실행 파일을 확인하고 현재 클라이언트의 지원 형식에 맞는 변경안을 검토하세요.");
    b.AppendLine("관련 없는 설정·기존 MCP 서버·선택 모델·추론 수준은 보존하세요. 같은 worker가 이미 등록되어 있으면 재사용하세요. 아래 예시를 전체 설정 파일에 덮어쓰지 말고 필요한 항목만 병합하여 qwen_submit/qwen_result를 제공하는 로컬 worker를 등록하세요. 도구가 실제로 노출되는지 확인하기 전에는 설치·연결 완료로 보고하지 마세요.");
    if(Client=="claude-desktop")b.AppendLine("Claude Desktop은 claude_desktop_config.json의 기존 mcpServers 항목을 확인하세요. 실제 파일 위치와 이 설치에서의 지원 여부를 먼저 확인하세요.");
    if(Client=="claude-code")b.AppendLine("Claude Code는 적용 범위(로컬·프로젝트·사용자)를 먼저 확인하세요. 아래 JSON은 프로젝트 .mcp.json 구조 예시이며 Desktop 설정 파일과 구분하세요.");
    b.AppendLine("설정 예시 (앱은 클라이언트 설정을 수정하거나 명령을 실행하지 않았습니다):");
    if(Client=="codex"){
     b.AppendLine("```toml");
     b.AppendLine("[mcp_servers.local_qwen]");
     b.AppendLine("command = "+TomlString(NodeCommand));
     b.AppendLine("args = ["+TomlString(WorkerScript)+"]");
     b.AppendLine("```");
    }else{
     b.AppendLine("```json");
     b.AppendLine(new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"mcpServers",new Dictionary<string,object>{{"local_qwen",new Dictionary<string,object>{{"command",NodeCommand},{"args",new[]{WorkerScript}}}}}}}));
     b.AppendLine("```");
    }
    b.AppendLine("worker는 이미 실행 중인 공유 대기열 127.0.0.1:18022만 사용해야 합니다. 서버 시작·재시작·종료는 수동이며 명시적 요청 없이는 하지 마세요. 작은 대표 작업을 실제 인수 기준으로 검증한 뒤 위임 범위를 넓히세요.");
   }
   if(web&&policyOnly)b.AppendLine("웹 ChatGPT/Claude는 이 PC의 127.0.0.1:18022에 직접 접근할 수 없습니다. 별도 승인된 연결이 필요하며 자동 터널 설치·권한 추가는 하지 마세요.");
   b.AppendLine();b.Append(Policy());
   return b.ToString();
  }
  internal static string TomlString(string value){
   var b=new StringBuilder("\"");
   foreach(char c in value??""){
    switch(c){
     case '\\':b.Append("\\\\");break;case '"':b.Append("\\\"");break;
     case '\b':b.Append("\\b");break;case '\t':b.Append("\\t");break;case '\n':b.Append("\\n");break;
     case '\f':b.Append("\\f");break;case '\r':b.Append("\\r");break;
     default:if(c<32||c==127)b.Append("\\u"+((int)c).ToString("X4"));else b.Append(c);break;
    }
   }
   return b.Append('"').ToString();
  }
  internal static string NormalizeWorkerPath(string path){
   string expanded=Environment.ExpandEnvironmentVariables((path??"").Trim());
   if(expanded.Length==0)throw new ArgumentException("worker 스크립트 경로를 입력해 주세요.");
   return Path.GetFullPath(Path.IsPathRooted(expanded)?expanded:Path.Combine(Root,expanded));
  }
  internal static string WorkerStatus(string path){
   try{string normalized=NormalizeWorkerPath(path);return File.Exists(normalized)?"파일 있음 · 내용·실행 가능성·도구 연결은 메인 AI가 별도로 검증해야 합니다.":"파일 없음 · 경로를 수정하거나 메인 AI와 worker 설치를 준비하세요.";}catch(Exception ex){return "경로 확인 필요: "+ex.Message;}
  }
  internal static void AtomicWrite(string path,string text){
   string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{
    File.WriteAllText(temporary,text,new UTF8Encoding(false));
    if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
   }finally{if(File.Exists(temporary))File.Delete(temporary);}
  }
  internal void Save(string directory){
   if(Array.IndexOf(Modes,Mode)<0||Array.IndexOf(Clients,Client)<0)throw new ArgumentException("지원하지 않는 프로필 값입니다.");
   if(string.IsNullOrWhiteSpace(NodeCommand)||NodeCommand.IndexOfAny(new[]{'\r','\n','\0'})>=0)throw new ArgumentException("Node 실행 파일 또는 명령을 한 줄로 입력해 주세요. 추가 인수는 입력하지 마세요.");
   WorkerScript=NormalizeWorkerPath(WorkerScript);NodeCommand=NodeCommand.Trim();
   Directory.CreateDirectory(directory);
   string json=new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"version",1},{"mode",Mode},{"client",Client},{"localWorkerScript",WorkerScript},{"nodeCommand",NodeCommand}});
   AtomicWrite(Path.Combine(directory,"delegation-policy.md"),Policy());
   AtomicWrite(Path.Combine(directory,"delegation.json"),json);
  }
  internal static DelegationProfile Load(string directory,out string warning){
   var result=Defaults();warning="";string path=Path.Combine(directory,"delegation.json");
   if(!File.Exists(path))return result;
   try{
    var values=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));object value;
    if(values==null)throw new FormatException("설정 내용이 비어 있습니다.");
    if(values.TryGetValue("mode",out value)&&value is string&&Array.IndexOf(Modes,(string)value)>=0)result.Mode=(string)value;else throw new FormatException("지원하지 않는 위임 모드입니다.");
    if(values.TryGetValue("client",out value)&&value is string&&Array.IndexOf(Clients,(string)value)>=0)result.Client=(string)value;
    if(values.TryGetValue("localWorkerScript",out value)&&value is string&&!string.IsNullOrWhiteSpace((string)value))result.WorkerScript=(string)value;
    if(values.TryGetValue("nodeCommand",out value)&&value is string&&!string.IsNullOrWhiteSpace((string)value))result.NodeCommand=(string)value;
   }catch(Exception ex){warning="프로필을 읽지 못해 기본값을 표시합니다: "+ex.Message;return Defaults();}
   return result;
  }
 }

 sealed class DelegationWindow : Form {
  readonly QwenStatus app;
  readonly ComboBox mode=new ComboBox(),client=new ComboBox(),purpose=new ComboBox();
  readonly TextBox worker=new TextBox(),node=new TextBox(),preview=new TextBox();
  readonly Label description=new Label(),validation=new Label(),saved=new Label();
  bool initializing,previewReady;
  internal DelegationWindow(QwenStatus app):this(app,null){}
  internal DelegationWindow(QwenStatus app,DelegationProfile testProfile){
   this.app=app;initializing=true;
   string warning="";DelegationProfile profile=testProfile??DelegationProfile.Load(DataRoot,out warning);
   Text="Qwen 위임 설정 · 로컬 프로필";ClientSize=new Size(940,828);MinimumSize=new Size(900,760);StartPosition=FormStartPosition.CenterScreen;
   Font=new Font("Malgun Gothic",9);BackColor=Color.FromArgb(245,247,250);AutoScaleMode=AutoScaleMode.Dpi;
   var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20,16,20,16),ColumnCount=1,RowCount=8,BackColor=BackColor};
   foreach(float height in new[]{38f,55f,104f,135f,66f,25f})layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
   layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,104));Controls.Add(layout);
   layout.Controls.Add(new Label{Text="로컬 Qwen 위임 설정",Dock=DockStyle.Fill,Font=new Font("Malgun Gothic",18,FontStyle.Bold)},0,0);
   layout.Controls.Add(new Label{Text="여기서는 이 PC의 프로필과 안내문만 저장합니다. AI에 적용하려면 아래 내용을 전달하고 도구 연결을 확인하세요.\r\n모드 변경 후 정책을 다시 읽거나 붙여넣어야 합니다. 서버는 수동으로 관리합니다.",Dock=DockStyle.Fill,ForeColor=Color.FromArgb(65,78,96),Padding=new Padding(0,4,0,0)},0,1);
   var modes=new GroupBox{Text="1. 위임 수준",Dock=DockStyle.Fill,Padding=new Padding(12,20,12,8)};layout.Controls.Add(modes,0,2);
   mode.DropDownStyle=ComboBoxStyle.DropDownList;mode.Items.AddRange(DelegationProfile.ModeLabels);mode.Bounds=new Rectangle(14,30,175,28);mode.SelectedIndex=Array.IndexOf(DelegationProfile.Modes,profile.Mode);modes.Controls.Add(mode);
   description.Bounds=new Rectangle(206,26,650,64);description.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;modes.Controls.Add(description);
   var paths=new GroupBox{Text="2. 이 PC의 worker 경로",Dock=DockStyle.Fill};layout.Controls.Add(paths,0,3);
   paths.Controls.Add(new Label{Text="worker 스크립트",Bounds=new Rectangle(14,27,115,25)});
   worker.Bounds=new Rectangle(136,24,634,25);worker.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;worker.Text=profile.WorkerScript;paths.Controls.Add(worker);
   var browse=new Button{Text="찾기…",Bounds=new Rectangle(780,22,88,29),Anchor=AnchorStyles.Top|AnchorStyles.Right};browse.Click+=(s,e)=>{using(var dialog=new OpenFileDialog{Title="로컬 worker 스크립트 선택",Filter="JavaScript 파일|*.mjs;*.js;*.cjs|모든 파일|*.*",CheckFileExists=true}){if(dialog.ShowDialog(this)==DialogResult.OK)worker.Text=dialog.FileName;}};paths.Controls.Add(browse);
   paths.Controls.Add(new Label{Text="Node 실행 명령",Bounds=new Rectangle(14,60,115,25)});
   node.Bounds=new Rectangle(136,57,732,25);node.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;node.Text=profile.NodeCommand;paths.Controls.Add(node);
   validation.Bounds=new Rectangle(14,90,854,37);validation.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;validation.ForeColor=Color.DimGray;paths.Controls.Add(validation);
   var choices=new Panel{Dock=DockStyle.Fill};layout.Controls.Add(choices,0,4);
   choices.Controls.Add(new Label{Text="3. 사용할 클라이언트",Bounds=new Rectangle(0,9,180,21)});
   client.DropDownStyle=ComboBoxStyle.DropDownList;client.Items.AddRange(DelegationProfile.ClientLabels);client.Bounds=new Rectangle(0,33,300,26);client.SelectedIndex=Array.IndexOf(DelegationProfile.Clients,profile.Client);choices.Controls.Add(client);
   choices.Controls.Add(new Label{Text="전달할 내용",Bounds=new Rectangle(320,9,250,21)});
   purpose.DropDownStyle=ComboBoxStyle.DropDownList;purpose.Items.AddRange(new[]{"처음 연결할 때 · 설정 안내 + 정책","이미 연결된 대화 · 정책만 전달"});purpose.SelectedIndex=0;purpose.Bounds=new Rectangle(320,33,360,26);choices.Controls.Add(purpose);
   layout.Controls.Add(new Label{Text="전달문 미리보기 · 선택/경로 변경 즉시 반영 (외부 AI에는 자동 적용되지 않음)",Dock=DockStyle.Fill,Padding=new Padding(0,4,0,0)},0,5);
   preview.Multiline=true;preview.ReadOnly=true;preview.WordWrap=true;preview.ScrollBars=ScrollBars.Vertical;preview.Dock=DockStyle.Fill;preview.BackColor=Color.White;preview.Font=new Font("Malgun Gothic",9);layout.Controls.Add(preview,0,6);
   var footer=new Panel{Dock=DockStyle.Fill};layout.Controls.Add(footer,0,7);
   var save=new Button{Text="로컬 프로필 저장",Bounds=new Rectangle(0,7,144,33)};save.Click+=(s,e)=>SaveProfile();footer.Controls.Add(save);
   var copy=new Button{Text="전달문 복사",Bounds=new Rectangle(154,7,118,33)};copy.Click+=(s,e)=>{if(!previewReady){MessageBox.Show(this,"경로와 Node 실행 명령을 먼저 확인해 주세요.","Qwen");return;}try{Clipboard.SetText(preview.Text);saved.Text="전달문을 복사했습니다. 대상 AI 대화에 붙여넣어 적용·연결 상태를 확인하세요.";}catch(Exception ex){MessageBox.Show(this,"복사하지 못했습니다: "+ex.Message,"Qwen");}};footer.Controls.Add(copy);
   var export=new Button{Text=".md 내보내기",Bounds=new Rectangle(282,7,132,33)};export.Click+=(s,e)=>ExportPrompt();footer.Controls.Add(export);
   var test=new Button{Text="기초 정확성·속도 검사",Bounds=new Rectangle(428,7,201,33),Enabled=app!=null};test.Click+=(s,e)=>{if(this.app!=null)this.app.OpenTokenTest();};footer.Controls.Add(test);
   footer.Controls.Add(new Label{Text="검사는 일반 능력 인증이 아닙니다. 대표 작업의 결과를 검증한 뒤 위임 범위를 넓히세요.",Bounds=new Rectangle(0,46,880,24),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,ForeColor=Color.DimGray});
   saved.Bounds=new Rectangle(0,73,880,30);saved.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;saved.ForeColor=Color.FromArgb(37,89,182);footer.Controls.Add(saved);
   EventHandler changed=(s,e)=>RefreshPreview(true);mode.SelectedIndexChanged+=changed;client.SelectedIndexChanged+=changed;purpose.SelectedIndexChanged+=changed;worker.TextChanged+=changed;node.TextChanged+=changed;
   initializing=false;RefreshPreview(false);
   saved.Text=warning.Length>0?warning:"저장 위치: "+Path.Combine(DataRoot,"delegation.json")+" · AI 적용 여부는 별도 확인";
  }
  DelegationProfile Current(){return new DelegationProfile{Mode=DelegationProfile.Modes[Math.Max(0,mode.SelectedIndex)],Client=DelegationProfile.Clients[Math.Max(0,client.SelectedIndex)],WorkerScript=worker.Text.Trim(),NodeCommand=node.Text.Trim()};}
  void RefreshPreview(bool dirty){
   if(initializing)return;var selected=Current();description.Text=DelegationProfile.Description(selected.Mode);validation.Text=DelegationProfile.WorkerStatus(selected.WorkerScript);
   previewReady=false;
   try{if(string.IsNullOrWhiteSpace(selected.NodeCommand)||selected.NodeCommand.IndexOfAny(new[]{'\r','\n','\0'})>=0)throw new ArgumentException("Node 실행 파일 또는 명령을 한 줄로 입력해 주세요.");selected.WorkerScript=DelegationProfile.NormalizeWorkerPath(selected.WorkerScript);preview.Text=selected.Prompt(purpose.SelectedIndex==1);previewReady=true;}catch(Exception ex){preview.Text="입력값을 확인하면 전달문을 생성합니다: "+ex.Message;}
   if(dirty)saved.Text="현재 편집값 미리보기 · 로컬 프로필 저장과 실제 AI 적용은 별도입니다.";
  }
  void SaveProfile(){
   try{var selected=Current();selected.Save(DataRoot);worker.Text=selected.WorkerScript;node.Text=selected.NodeCommand;RefreshPreview(false);saved.Text="로컬 프로필 저장 완료. AI는 아직 적용 여부 미확인 · 변경 정책을 다시 읽거나 붙여넣으세요.";}
   catch(Exception ex){MessageBox.Show(this,"프로필을 저장하지 못했습니다: "+ex.Message,"Qwen",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
  }
  void ExportPrompt(){
   if(!previewReady){MessageBox.Show(this,"경로와 Node 실행 명령을 먼저 확인해 주세요.","Qwen");return;}
   try{using(var dialog=new SaveFileDialog{Title="Qwen 전달문 내보내기",Filter="Markdown 파일|*.md",DefaultExt="md",AddExtension=true,FileName="qwen-delegation-handoff.md"}){
    if(dialog.ShowDialog(this)!=DialogResult.OK)return;
    DelegationProfile.AtomicWrite(dialog.FileName,preview.Text);saved.Text="전달문을 내보냈습니다. 대상 AI에 전달해야 하며 도구 연결은 별도로 확인해야 합니다.";
   }}catch(Exception ex){MessageBox.Show(this,"내보내지 못했습니다: "+ex.Message,"Qwen");}
  }
 }

 static void DelegationTest(){
  Directory.CreateDirectory(DataRoot);
  string temp=Path.Combine(Path.GetTempPath(),"QwenDelegationTest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
  var checks=new List<string>();
  try{
   var profile=DelegationProfile.Defaults();var distinct=new HashSet<string>();
   foreach(string mode in DelegationProfile.Modes){profile.Mode=mode;distinct.Add(profile.Policy());DelegationAssert(profile.Policy().Contains("("+mode+")"),"mode is explicit");}
   DelegationAssert(distinct.Count==4,"four distinct policies");checks.Add("PASS distinct automatic / maximum / minimum / manual policies");
   profile.Mode="maximum";profile.Client="claude-code";profile.WorkerScript=Path.Combine(temp,"한글 worker.mjs");profile.NodeCommand=@"C:\Program Files\Node\node.exe";
   File.WriteAllText(profile.WorkerScript,"// validation fixture only; never executed");
   DelegationAssert(DelegationProfile.WorkerStatus(profile.WorkerScript).StartsWith("파일 있음"),"existing worker validation");
   profile.Save(temp);string warning;var loaded=DelegationProfile.Load(temp,out warning);
   DelegationAssert(warning==""&&loaded.Mode==profile.Mode&&loaded.Client==profile.Client&&loaded.NodeCommand==profile.NodeCommand&&loaded.WorkerScript==profile.WorkerScript,"roundtrip values");
   profile.Mode="minimum";profile.Save(temp);loaded=DelegationProfile.Load(temp,out warning);
   DelegationAssert(loaded.Mode=="minimum"&&File.ReadAllText(Path.Combine(temp,"delegation-policy.md"))==profile.Policy(),"atomic replacement contents");
   DelegationAssert(Directory.GetFiles(temp,"*.tmp").Length==0,"atomic temp cleanup");checks.Add("PASS atomic first save / replacement / Unicode path roundtrip / no temp residue");
   string tricky="C:\\한글 space\\quote\"\\tab\tline\nend";
   string encoded=DelegationProfile.TomlString(tricky);
   DelegationAssert(encoded=="\"C:\\\\한글 space\\\\quote\\\"\\\\tab\\tline\\nend\"","TOML escaping");
   DelegationAssert(DelegationProfile.TomlString("\0\u007f")=="\"\\u0000\\u007F\"","TOML control escaping");
   profile.NodeCommand=tricky;profile.WorkerScript=tricky;profile.Client="claude-desktop";
   string prompt=profile.Prompt(false),json=prompt.Substring(prompt.IndexOf("```json\r\n")+9);json=json.Substring(0,json.IndexOf("\r\n```"));
   var root=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
   var servers=(Dictionary<string,object>)root["mcpServers"];var server=(Dictionary<string,object>)servers["local_qwen"];
   DelegationAssert((string)server["command"]==tricky,"JSON command escaping");
   var args=(System.Collections.ArrayList)server["args"];DelegationAssert((string)args[0]==tricky,"JSON path escaping");checks.Add("PASS JSON and TOML backslash / quote / whitespace / Unicode encoding");
   profile.Client="web";string web=profile.Prompt(false),webPolicy=profile.Prompt(true);
   DelegationAssert(web.Contains("직접 접근할 수 없습니다")&&web.Contains("별도로 명시적 승인을 받은 연결")&&!web.Contains("mcpServers")&&!web.Contains("mcp_servers"),"web connection boundary");
   DelegationAssert(webPolicy.Contains("직접 접근할 수 없습니다"),"web policy connection boundary");checks.Add("PASS web localhost limitation and separate authorization are explicit");
   profile=DelegationProfile.Defaults();profile.WorkerScript=Path.Combine(temp,"한글 worker.mjs");
   using(var window=new DelegationWindow(null,profile)){
    window.Show();Application.DoEvents();window.PerformLayout();
    using(var bitmap=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(DataRoot,"delegation-preview.png"),System.Drawing.Imaging.ImageFormat.Png);}
   }
   checks.Add("PASS WinForms preview rendered without app/server/clipboard/config mutation");
   File.WriteAllText(Path.Combine(DataRoot,"delegation-test.txt"),"PASS: delegation profiles, persistence, client instructions and window\r\n"+string.Join("\r\n",checks.ToArray())+"\r\n",new UTF8Encoding(false));
  }finally{
   string resolved=Path.GetFullPath(temp),parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(resolved.StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(resolved).StartsWith("QwenDelegationTest-",StringComparison.Ordinal)&&Directory.Exists(resolved))Directory.Delete(resolved,true);
  }
 }
 static void DelegationAssert(bool condition,string message){if(!condition)throw new InvalidOperationException("Delegation test failed: "+message);}
}
