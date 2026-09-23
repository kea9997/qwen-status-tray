using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

partial class QwenStatus {
 ChatWindow chatWindow;
 void OpenChat(){
  if(chatWindow==null||chatWindow.IsDisposed)chatWindow=new ChatWindow();
  chatWindow.Show();chatWindow.WindowState=FormWindowState.Normal;chatWindow.Activate();
 }

 internal sealed class ChatTurn {public string Role{get;set;}public string Text{get;set;}}
 internal sealed class ChatConversation {
  public string Id{get;set;}public string Title{get;set;}public string Draft{get;set;}public List<ChatTurn> Turns{get;set;}
  public override string ToString(){return Title;}
 }
 internal static class ChatHistory {
  internal static string FilePath=Path.Combine(DataRoot,"chat-history.json");
  internal static List<ChatConversation> Load(){
   try{
    if(!File.Exists(FilePath))return new List<ChatConversation>();
    var result=new JavaScriptSerializer{MaxJsonLength=10000000}.Deserialize<List<ChatConversation>>(File.ReadAllText(FilePath,Encoding.UTF8));
    return result==null?new List<ChatConversation>():result.Where(x=>x!=null&&ValidId(x.Id)).Select(x=>{x.Turns=(x.Turns??new List<ChatTurn>()).Where(t=>t!=null).ToList();return x;}).ToList();
   }catch{return new List<ChatConversation>();}
  }
  static bool ValidId(string id){Guid parsed;return Guid.TryParse(id,out parsed);}
  internal static bool Matches(ChatConversation c,string query){
   if(c==null)return false;if(string.IsNullOrWhiteSpace(query))return true;query=query.Trim();
   return (c.Title??"").IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0||(c.Turns??new List<ChatTurn>()).Any(t=>t!=null&&(t.Text??"").IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0);
  }
  internal static string ToMarkdown(ChatConversation c){
   if(c==null)return "";var text=new StringBuilder();text.Append("# ").AppendLine((c.Title??"Qwen 대화").Replace("\r"," ").Replace("\n"," ")).AppendLine();
   foreach(var turn in c.Turns??new List<ChatTurn>()){if(turn==null)continue;text.AppendLine(turn.Role=="user"?"## 나":"## Qwen").AppendLine().AppendLine(turn.Text??"").AppendLine();}return text.ToString();
  }
  internal static string ExportFileName(ChatConversation c){
   char[] invalid=Path.GetInvalidFileNameChars();string title=new string(((c==null?null:c.Title)??"").Where(ch=>!invalid.Contains(ch)).ToArray()).Trim();
   if(title.Length>60)title=title.Substring(0,60);title=title.TrimEnd(' ','.');return "qwen-chat"+(title.Length==0?"":"-"+title)+".md";
  }
  internal static void Save(List<ChatConversation> history){
   Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
   string temp=FilePath+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{
    File.WriteAllText(temp,new JavaScriptSerializer{MaxJsonLength=10000000}.Serialize(history),new UTF8Encoding(false));
    if(File.Exists(FilePath))File.Replace(temp,FilePath,null);else File.Move(temp,FilePath);
   }finally{if(File.Exists(temp))File.Delete(temp);}
  }
 }
 internal sealed class ChatWindow : Form {
   static readonly Color Back=Color.FromArgb(245,247,250),Card=Color.White,Ink=Color.FromArgb(38,48,64),Muted=Color.DimGray,Accent=Color.RoyalBlue,Line=Color.FromArgb(216,223,232);
  readonly ListBox conversations=new ListBox();readonly RichTextBox transcript=new RichTextBox();readonly TextBox composer=new TextBox(),search=new TextBox();
  readonly Label heading=new Label(),status=new Label();readonly Button send=new Button(),cancel=new Button(),delete=new Button(),retry=new Button(),rename=new Button(),export=new Button(),copy=new Button(),newChat=new Button();
  readonly Timer poll=new Timer();readonly List<ChatConversation> history=ChatHistory.Load();
  readonly Func<string,string,object,Dictionary<string,object>> request;
  readonly Font roleFont=new Font("Malgun Gothic",9,FontStyle.Bold),messageFont=new Font("Malgun Gothic",11);
  ChatConversation current;string jobId,lastPreview;bool busy,polling,closing,filtering,pollPaused,draftSavePending;
  sealed class MissingJobException : Exception {internal MissingJobException():base("서버에 해당 요청이 없습니다. 서버 재시작 또는 기록 정리 여부를 확인하세요.") {}}
  static string ApiBase(){
   string value=Setting("uiUrl","http://127.0.0.1:18022/ui").TrimEnd('/');Uri uri;
   if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||uri.Scheme!="http"||!uri.IsLoopback||uri.AbsolutePath!="/ui")throw new Exception("연결 설정의 UI 주소는 로컬 HTTP /ui여야 합니다.");
   return value;
  }
  static Dictionary<string,object> Api(string method,string path,object payload=null){
   string tokenPath=Path.Combine(GatewayRoot,"ui-token.txt");
   if(!File.Exists(tokenPath))throw new Exception("공통 대기열의 UI 토큰이 없습니다. 연결 설정을 확인하세요.");
   var req=(HttpWebRequest)WebRequest.Create(ApiBase()+path);req.Method=method;req.Timeout=8000;req.ReadWriteTimeout=8000;req.Headers["X-UI-Token"]=File.ReadAllText(tokenPath).Trim();
   if(payload!=null){byte[] bytes=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(payload));req.ContentType="application/json; charset=utf-8";req.ContentLength=bytes.Length;using(var stream=req.GetRequestStream())stream.Write(bytes,0,bytes.Length);}
   try{using(var response=(HttpWebResponse)req.GetResponse())using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8))return new JavaScriptSerializer{MaxJsonLength=10000000}.Deserialize<Dictionary<string,object>>(reader.ReadToEnd());}
   catch(WebException ex){var response=ex.Response as HttpWebResponse;if(response!=null&&response.StatusCode==HttpStatusCode.NotFound&&path.StartsWith("/jobs/",StringComparison.Ordinal)){response.Dispose();throw new MissingJobException();}try{using(var reader=new StreamReader(ex.Response.GetResponseStream(),Encoding.UTF8)){var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());var error=data.ContainsKey("error")?data["error"] as Dictionary<string,object>:null;throw new Exception(error!=null?Convert.ToString(error["message"]):ex.Message);}}catch(NullReferenceException){throw new Exception("공통 대기열에 연결할 수 없습니다.");}}
  }
  static string Value(Dictionary<string,object> data,string key){return data!=null&&data.ContainsKey(key)&&data[key]!=null?Convert.ToString(data[key]):"";}
  public ChatWindow():this(Api){}
  internal ChatWindow(Func<string,string,object,Dictionary<string,object>> transport){
   request=transport;
   Text="Qwen · 직접 대화";ClientSize=new Size(1060,740);MinimumSize=new Size(760,540);Font=new Font("Malgun Gothic",10);BackColor=Back;ForeColor=Ink;StartPosition=FormStartPosition.CenterScreen;
    var sidebar=new Panel{Dock=DockStyle.Left,Width=250,BackColor=Card,Padding=new Padding(14)};sidebar.Paint+=(s,e)=>{using(var pen=new Pen(Line,1))e.Graphics.DrawLine(pen,sidebar.Width-1,0,sidebar.Width-1,sidebar.Height);};Controls.Add(sidebar);
    var sidebarTop=new Panel{Dock=DockStyle.Top,Height=111};sidebar.Controls.Add(sidebarTop);
    newChat=ButtonStyle("＋ 새 대화",Card,Accent);newChat.Dock=DockStyle.Top;newChat.Height=42;newChat.Click+=(s,e)=>NewConversation();sidebarTop.Controls.Add(newChat);
    sidebarTop.Controls.Add(new Label{Text="대화 검색 · 제목과 내용",Top=51,Left=0,Width=220,Height=23,ForeColor=Muted,Font=new Font("Malgun Gothic",9)});
    search.SetBounds(0,77,220,29);search.Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right;search.AccessibleName="대화 검색";search.TextChanged+=(s,e)=>RefreshConversations(current);sidebarTop.Controls.Add(search);
    delete=ButtonStyle("대화 삭제",Card,Ink);delete.Dock=DockStyle.Bottom;delete.Height=38;delete.Click+=async(s,e)=>await DeleteConversation();sidebar.Controls.Add(delete);
    conversations.Dock=DockStyle.Fill;conversations.BorderStyle=BorderStyle.None;conversations.BackColor=sidebar.BackColor;conversations.ForeColor=Ink;conversations.Font=new Font(Font.FontFamily,10);conversations.IntegralHeight=false;conversations.DrawMode=DrawMode.OwnerDrawFixed;conversations.ItemHeight=36;conversations.DrawItem+=(s,e)=>{if(e.Index<0)return;bool selected=(e.State&DrawItemState.Selected)!=0;using(var background=new SolidBrush(selected?Color.FromArgb(232,239,252):sidebar.BackColor))e.Graphics.FillRectangle(background,e.Bounds);using(var brush=new SolidBrush(selected?Accent:Ink))e.Graphics.DrawString(conversations.Items[e.Index].ToString(),conversations.Font,brush,new RectangleF(e.Bounds.Left+11,e.Bounds.Top+8,e.Bounds.Width-17,e.Bounds.Height-8));if(selected)using(var pen=new Pen(Line,1))e.Graphics.DrawRectangle(pen,e.Bounds.Left,e.Bounds.Top,e.Bounds.Width-1,e.Bounds.Height-1);};conversations.SelectedIndexChanged+=(s,e)=>SelectConversation();sidebar.Controls.Add(conversations);sidebar.Controls.SetChildIndex(conversations,0);
   var body=new Panel{Dock=DockStyle.Fill,Padding=new Padding(20,16,20,16),BackColor=Back};Controls.Add(body);body.BringToFront();
    var header=new Panel{Dock=DockStyle.Top,Height=113};header.Paint+=(s,e)=>{using(var pen=new Pen(Line,1))e.Graphics.DrawLine(pen,0,header.Height-1,header.Width,header.Height-1);};body.Controls.Add(header);
   heading.Text="직접 대화";heading.Font=new Font(Font.FontFamily,18,FontStyle.Bold);heading.AutoEllipsis=true;heading.ForeColor=Ink;heading.Dock=DockStyle.Top;heading.Height=38;header.Controls.Add(heading);
   status.ForeColor=Muted;status.Dock=DockStyle.Bottom;status.Height=26;status.Text="같은 대화에서는 앞선 내용을 이어갑니다.";header.Controls.Add(status);
   var tools=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};header.Controls.Add(tools);tools.BringToFront();
   rename=ButtonStyle("이름 변경",Card,Ink);rename.Width=98;rename.Click+=(s,e)=>RenameConversation();tools.Controls.Add(rename);
   export=ButtonStyle("대화 저장",Card,Ink);export.Width=98;export.Click+=(s,e)=>ExportConversation();tools.Controls.Add(export);
   copy=ButtonStyle("답변 복사",Card,Ink);copy.Width=98;copy.Click+=(s,e)=>CopyAnswer();tools.Controls.Add(copy);
   retry=ButtonStyle("조회 재시도",Card,Accent);retry.Width=112;retry.Visible=false;retry.Click+=async(s,e)=>{pollPaused=false;retry.Visible=false;status.Text="기존 요청을 다시 확인하는 중…";await Poll();};tools.Controls.Add(retry);
   var composePanel=new Panel{Dock=DockStyle.Bottom,Height=155,Padding=new Padding(0,8,0,0)};body.Controls.Add(composePanel);
   composePanel.Controls.Add(new Label{Text="메시지 입력  ·  Enter 전송  ·  Shift+Enter 줄바꿈",Dock=DockStyle.Top,Height=26,ForeColor=Muted,Font=new Font("Malgun Gothic",9)});
   var actions=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=43,FlowDirection=FlowDirection.RightToLeft};composePanel.Controls.Add(actions);
    send=ButtonStyle("보내기  ↵",Card,Accent);send.Width=116;send.Click+=async(s,e)=>await Send();actions.Controls.Add(send);
    cancel=ButtonStyle("중단",Card,Ink);cancel.Width=92;cancel.Visible=false;cancel.Click+=async(s,e)=>await Cancel();actions.Controls.Add(cancel);
   composer.Multiline=true;composer.AcceptsReturn=true;composer.ScrollBars=ScrollBars.Vertical;composer.Dock=DockStyle.Fill;composer.BackColor=Card;composer.ForeColor=Ink;composer.BorderStyle=BorderStyle.FixedSingle;composer.Font=new Font(Font.FontFamily,11);composer.KeyDown+=async(s,e)=>{if(e.KeyCode==Keys.Enter&&!e.Shift){e.SuppressKeyPress=true;await Send();}};composePanel.Controls.Add(composer);composer.BringToFront();
   transcript.Dock=DockStyle.Fill;transcript.ReadOnly=true;transcript.BorderStyle=BorderStyle.FixedSingle;transcript.BackColor=Card;transcript.ForeColor=Ink;transcript.Font=new Font(Font.FontFamily,11);transcript.ScrollBars=RichTextBoxScrollBars.Vertical;transcript.DetectUrls=true;body.Controls.Add(transcript);transcript.BringToFront();
   poll.Interval=600;poll.Tick+=async(s,e)=>await Poll();poll.Start();
   RefreshConversations(null);if(history.Count==0)NewConversation();
   FormClosing+=(s,e)=>{SaveDraft();if(!closing&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
   FormClosed+=(s,e)=>{poll.Dispose();roleFont.Dispose();messageFont.Dispose();};
  }
   static Button ButtonStyle(string label,Color background,Color foreground){var button=new Button{Text=label,BackColor=background,ForeColor=foreground,FlatStyle=FlatStyle.Flat,Height=36,Margin=new Padding(4),Font=new Font("Malgun Gothic",10,FontStyle.Bold)};button.FlatAppearance.BorderColor=foreground==Accent?Accent:Line;button.FlatAppearance.MouseOverBackColor=Color.FromArgb(232,239,252);return button;}
   void SetBusy(bool value){busy=value;send.Visible=!value;cancel.Visible=value;delete.Enabled=!value&&current!=null;conversations.Enabled=!value;composer.Enabled=!value&&current!=null;search.Enabled=!value;newChat.Enabled=!value;rename.Enabled=!value&&current!=null;export.Enabled=!value&&current!=null;copy.Enabled=!value&&current!=null&&current.Turns.Any(t=>t.Role=="assistant");if(!value){pollPaused=false;retry.Visible=false;}}
  void SaveDraft(){if(current!=null&&current.Draft!=composer.Text){current.Draft=composer.Text;draftSavePending=true;}if(!draftSavePending)return;try{ChatHistory.Save(history);draftSavePending=false;}catch(Exception ex){status.Text="임시 입력 저장 실패: "+ex.Message;}}
  void RefreshConversations(ChatConversation preferred){
   if(busy||filtering)return;SaveDraft();filtering=true;conversations.BeginUpdate();
   try{conversations.Items.Clear();foreach(var item in history.Where(c=>ChatHistory.Matches(c,search.Text)))conversations.Items.Add(item);if(preferred!=null&&conversations.Items.Contains(preferred))conversations.SelectedItem=preferred;else if(conversations.Items.Count>0)conversations.SelectedIndex=0;}
   finally{conversations.EndUpdate();filtering=false;}SelectConversation();
  }
  void NewConversation(){
   if(busy)return;
   if(current!=null&&current.Turns.Count==0&&string.IsNullOrWhiteSpace(composer.Text)){conversations.SelectedItem=current;composer.Focus();return;}
   SaveDraft();search.Clear();var item=new ChatConversation{Id=Guid.NewGuid().ToString("N"),Title="새 대화",Turns=new List<ChatTurn>()};history.Insert(0,item);RefreshConversations(item);ChatHistory.Save(history);composer.Focus();
  }
  void SelectConversation(){if(busy||filtering)return;var next=conversations.SelectedItem as ChatConversation;if(current!=next){SaveDraft();current=next;composer.Text=current==null?"":current.Draft??"";}SetBusy(false);Render();}
  void RenameConversation(){
   if(busy||current==null)return;
   using(var dialog=new Form{Text="대화 이름 변경",ClientSize=new Size(400,116),FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false,Font=Font}){
    var input=new TextBox{Text=current.Title,MaxLength=80,Bounds=new Rectangle(16,16,368,28)};dialog.Controls.Add(input);
    var ok=new Button{Text="저장",DialogResult=DialogResult.OK,Bounds=new Rectangle(198,67,88,32)};var back=new Button{Text="취소",DialogResult=DialogResult.Cancel,Bounds=new Rectangle(296,67,88,32)};dialog.Controls.Add(ok);dialog.Controls.Add(back);dialog.AcceptButton=ok;dialog.CancelButton=back;
    if(dialog.ShowDialog(this)!=DialogResult.OK||string.IsNullOrWhiteSpace(input.Text))return;
    try{current.Title=input.Text.Trim();ChatHistory.Save(history);RefreshConversations(current);status.Text="대화 이름을 저장했습니다.";}catch(Exception ex){status.Text="이름 저장 실패: "+ex.Message;}
   }
  }
  void ExportConversation(){
   if(busy||current==null)return;using(var dialog=new SaveFileDialog{Title="대화 저장",Filter="Markdown 문서 (*.md)|*.md",FileName=ChatHistory.ExportFileName(current),DefaultExt="md",AddExtension=true}){
    if(dialog.ShowDialog(this)!=DialogResult.OK)return;try{File.WriteAllText(dialog.FileName,ChatHistory.ToMarkdown(current),new UTF8Encoding(false));status.Text="현재 대화를 Markdown으로 저장했습니다.";}catch(Exception ex){status.Text="대화 저장 실패: "+ex.Message;}
   }
  }
  void CopyAnswer(){var answer=current==null?null:current.Turns.LastOrDefault(t=>t.Role=="assistant");if(answer==null||string.IsNullOrEmpty(answer.Text))return;try{Clipboard.SetText(answer.Text);status.Text="마지막 답변을 복사했습니다.";}catch(Exception ex){status.Text="복사 실패: "+ex.Message;}}
  void Render(string pending=null){
   transcript.Clear();if(current==null){heading.Text="검색 결과 없음";return;}heading.Text=current.Title;
   if(current.Turns.Count==0&&pending==null){transcript.SelectionColor=Muted;transcript.AppendText("새 대화가 준비됐습니다.\n아래에 질문을 입력하면 Qwen이 답합니다. 같은 대화에서 이어서 질문할 수 있습니다.");return;}
   foreach(var turn in current.Turns){Append(turn.Role=="user"?"나":"Qwen",turn.Text,turn.Role=="user"?Accent:Ink);}
   if(pending!=null)Append("Qwen",pending,Ink);
   transcript.SelectionStart=transcript.TextLength;transcript.ScrollToCaret();
  }
  void Append(string role,string content,Color color){
   int start=transcript.TextLength;transcript.AppendText(role+Environment.NewLine);
   transcript.Select(start,role.Length);transcript.SelectionColor=role=="나"?Accent:Muted;transcript.SelectionFont=roleFont;
   start=transcript.TextLength;transcript.AppendText(content+Environment.NewLine+Environment.NewLine);
   transcript.Select(start,(content??"").Length);transcript.SelectionColor=color;transcript.SelectionFont=messageFont;
  }
  async Task Send(){
   if(busy||current==null||string.IsNullOrWhiteSpace(composer.Text))return;
   string prompt=composer.Text.Trim(),session=current.Id;composer.Clear();lastPreview=null;SetBusy(true);status.Text="요청을 보내는 중…";
   try{
    var result=await Task.Run(()=>request("POST","/jobs",new{mode="chat",session=session,prompt=prompt}));
    jobId=Value(result,"id");if(string.IsNullOrEmpty(jobId))throw new Exception("작업 ID가 없습니다.");
    current.Draft="";current.Turns.Add(new ChatTurn{Role="user",Text=prompt});if(current.Title=="새 대화")current.Title=prompt.Length>28?prompt.Substring(0,28)+"…":prompt;
    try{ChatHistory.Save(history);}catch(Exception ex){status.Text="요청은 접수됐지만 기록 저장 실패: "+ex.Message;}
    conversations.Items[conversations.SelectedIndex]=current;Render("답변 생성 중…");status.Text="Qwen 응답 대기 중…";
   }catch(Exception ex){SetBusy(false);status.Text="요청 실패: "+ex.Message;composer.Text=prompt;jobId=null;}
  }
  async Task Poll(){
   if(!busy||polling||pollPaused||string.IsNullOrEmpty(jobId))return;polling=true;
   try{
    string id=jobId;var result=await Task.Run(()=>request("GET","/jobs/"+id,null));if(id!=jobId||IsDisposed)return;
    string phase=Value(result,"status"),output=Value(result,"output");
    if(phase=="running"||phase=="queued"){
     status.Text=phase=="queued"?"공통 대기열에서 대기 중…":Value(result,"phase")==""?"답변 생성 중…":Value(result,"phase");
     string preview=string.IsNullOrEmpty(output)?"답변 생성 중…":output;
     if(preview!=lastPreview){lastPreview=preview;Render(preview);}return;
    }
    if(phase!="completed"&&phase!="incomplete"&&phase!="cancelled"&&phase!="failed")throw new Exception("작업 상태를 확인하지 못했습니다.");
    lastPreview=null;
    if(phase=="completed"||phase=="incomplete"){
     current.Turns.Add(new ChatTurn{Role="assistant",Text=output});
     status.Text=phase=="completed"?"완료 · 대화 맥락 저장됨":"출력 한도에 도달했습니다 · 이어서 질문할 수 있습니다";
     try{ChatHistory.Save(history);}catch(Exception ex){status.Text="답변은 받았지만 기록 저장 실패 · 대화 저장으로 보관하세요: "+ex.Message;}
    }else status.Text=phase=="cancelled"?"요청 중단됨":"응답 실패: "+Value(result,"error");
    jobId=null;SetBusy(false);
    Render(phase=="completed"||phase=="incomplete"?null:output);composer.Focus();
   }catch(MissingJobException ex){ForgetMissingJob(ex.Message);}
   catch(Exception ex){status.Text="응답 조회 중단 · 조회 재시도로 기존 요청 확인: "+ex.Message;pollPaused=true;retry.Visible=true;}
   finally{polling=false;}
  }
  void ForgetMissingJob(string message){jobId=null;lastPreview=null;SetBusy(false);status.Text=message;Render();}
  async Task Cancel(){if(!busy||string.IsNullOrEmpty(jobId))return;try{string id=jobId;await Task.Run(()=>request("DELETE","/jobs/"+id,null));pollPaused=false;retry.Visible=false;status.Text="중단 요청을 보냈습니다.";}catch(MissingJobException ex){ForgetMissingJob(ex.Message);}catch(Exception ex){status.Text="중단 실패: "+ex.Message;}}
  async Task DeleteConversation(){
   if(busy||current==null)return;
   if(MessageBox.Show("이 대화와 기록을 삭제할까요?","Qwen 대화 삭제",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
   var selected=current;SetBusy(true);
   try{if(selected.Turns.Count>0)await Task.Run(()=>request("DELETE","/conversations/"+selected.Id,null));history.Remove(selected);current=null;ChatHistory.Save(history);SetBusy(false);RefreshConversations(null);if(history.Count==0)NewConversation();status.Text="대화를 삭제했습니다.";}
   catch(Exception ex){status.Text="삭제 실패: "+ex.Message;}
   finally{SetBusy(false);}
  }
  public void Shutdown(){closing=true;Close();}
  internal static void Test(){
   string old=ChatHistory.FilePath;string path=Path.Combine(Path.GetTempPath(),"qwen-chat-test-"+Guid.NewGuid().ToString("N")+".json");
   try{
    ChatHistory.FilePath=path;
    var sample=new List<ChatConversation>{new ChatConversation{Id=Guid.NewGuid().ToString("N"),Title="맥락 테스트",Draft="아직 보내지 않은 질문",Turns=new List<ChatTurn>{new ChatTurn{Role="user",Text="안녕"},new ChatTurn{Role="assistant",Text="안녕하세요"}}},new ChatConversation{Id=Guid.NewGuid().ToString("N"),Title="코드 검토",Turns=new List<ChatTurn>{new ChatTurn{Role="assistant",Text="```csharp\nreturn 42;\n```"}}}};
    ChatHistory.Save(sample);var loaded=ChatHistory.Load();if(loaded.Count!=2||loaded[0].Turns.Count!=2||loaded[0].Turns[1].Text!="안녕하세요"||loaded[0].Draft!=sample[0].Draft)throw new Exception("Chat history roundtrip failed");
    if(!ChatHistory.Matches(loaded[0]," 안녕하세요 ")||!ChatHistory.Matches(loaded[1],"CSHARP")||ChatHistory.Matches(loaded[0],"없는말"))throw new Exception("Conversation search failed");
    string markdown=ChatHistory.ToMarkdown(loaded[1]);if(!markdown.Contains("## Qwen")||!markdown.Contains("```csharp\nreturn 42;\n```")||markdown.Contains(loaded[1].Id))throw new Exception("Markdown export lost content or exposed ID");
    foreach(string title in new[]{"CON","NUL",":/<>|?*","한글 제목. ",new string('x',90)}){string name=ChatHistory.ExportFileName(new ChatConversation{Title=title});if(name.IndexOfAny(Path.GetInvalidFileNameChars())>=0||!name.StartsWith("qwen-chat")||!name.EndsWith(".md")||name.Length>73)throw new Exception("Unsafe export filename");}
    int posts=0,gets=0;bool missing=false;string requestedId="mock-job";
    using(var window=new ChatWindow((method,url,payload)=>{
     if(method=="POST"){posts++;return new Dictionary<string,object>{{"id",requestedId}};}
     if(method=="GET"){gets++;if(missing)throw new MissingJobException();if(gets==1)throw new IOException("simulated disconnect");if(url!="/jobs/"+requestedId)throw new Exception("Lost original job ID");return new Dictionary<string,object>{{"status","completed"},{"output","재연결 후 받은 답변"}};}
     throw new Exception("Unexpected test request");
    })){
     window.poll.Stop();Exception testError=null;
     using(var timeout=new Timer{Interval=15000}){
     timeout.Tick+=(s,e)=>{testError=new Exception("Chat UI test timed out");window.Shutdown();};
     window.Shown+=async(s,e)=>{try{
     if(window.current==null||!window.transcript.Text.Contains("안녕하세요")||window.composer.Text!=sample[0].Draft)throw new Exception("Chat transcript/draft rendering failed");
     window.composer.Text="다른 대화를 보기 전 입력";window.search.Text="CSHARP";
     if(window.conversations.Items.Count!=1||window.current.Title!="코드 검토")throw new Exception("Conversation filtering failed");
     window.search.Text="안녕하세요";if(window.composer.Text!="다른 대화를 보기 전 입력")throw new Exception("Switching conversations lost draft");
     window.search.Clear();window.composer.Text="연결 복구 검사";await window.Send();await window.Poll();
     if(!window.busy||!window.pollPaused||window.jobId!=requestedId||!window.retry.Visible)throw new Exception("Transient polling failure discarded active job");
     window.pollPaused=false;await window.Poll();
     if(window.busy||window.jobId!=null||posts!=1||gets!=2||!window.transcript.Text.Contains("재연결 후 받은 답변")||window.current.Turns.Last().Text!="재연결 후 받은 답변"||!window.copy.Enabled)throw new Exception("Resume polling resubmitted or lost answer");
     using(var image=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(DataRoot,"chat-ui-preview.png"));}
     window.ClientSize=new Size(760,540);Application.DoEvents();
     using(var image=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(DataRoot,"chat-ui-small-preview.png"));}
     missing=true;window.composer.Text="사라진 요청 검사";await window.Send();await window.Poll();if(window.busy||window.jobId!=null||!window.send.Visible)throw new Exception("Missing job left chat locked");
     string blocked=path+".folder";Directory.CreateDirectory(blocked);try{ChatHistory.FilePath=blocked;window.composer.Text="저장 실패 후 다시 저장할 입력";window.SaveDraft();if(!window.draftSavePending)throw new Exception("Failed draft was marked saved");ChatHistory.FilePath=path;window.SaveDraft();if(window.draftSavePending||ChatHistory.Load()[0].Draft!=window.composer.Text)throw new Exception("Draft retry lost input");}finally{ChatHistory.FilePath=path;Directory.Delete(blocked);}
     }catch(Exception ex){testError=ex;}finally{timeout.Stop();window.Shutdown();}};
     timeout.Start();Application.Run(window);if(testError!=null)throw testError;
     }
    }
    File.WriteAllText(Path.Combine(DataRoot,"chat-test.txt"),"PASS: history, draft, search, Markdown, safe filenames, retry without resubmission and chat rendering");
   }
   finally{ChatHistory.FilePath=old;if(File.Exists(path))File.Delete(path);}
  }
  internal static void LiveTest(){
   string old=ChatHistory.FilePath,path=Path.Combine(Path.GetTempPath(),"qwen-chat-live-"+Guid.NewGuid().ToString("N")+".json"),session=null;
   try{
    ChatHistory.FilePath=path;
    using(var window=new ChatWindow()){
     window.Show();Application.DoEvents();session=window.current.Id;
     window.composer.Text="기억할 숫자는 73184입니다. 알겠다고만 답하세요.";Pump(window.Send());PumpUntilDone(window);
     if(window.current.Turns.Count!=2)throw new Exception("First chat turn was not saved");
     window.composer.Text="방금 기억하라고 한 숫자는 무엇인가요? 숫자만 답하세요.";Pump(window.Send());PumpUntilDone(window);
     if(window.current.Turns.Count!=4||!window.current.Turns[3].Text.Contains("73184"))throw new Exception("Conversation context was not preserved");
     if(ChatHistory.Load()[0].Turns.Count!=4)throw new Exception("Conversation was not persisted");
     window.Shutdown();
    }
    File.WriteAllText(Path.Combine(DataRoot,"chat-live-test.txt"),"PASS: UI API, two-turn context and saved transcript");
   }finally{
    if(session!=null)try{Api("DELETE","/conversations/"+session);}catch{}
    ChatHistory.FilePath=old;if(File.Exists(path))File.Delete(path);
   }
  }
  static void Pump(Task task){var until=DateTime.UtcNow.AddSeconds(12);while(!task.IsCompleted&&DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(30);}if(!task.IsCompleted)throw new Exception("Chat request timed out");task.GetAwaiter().GetResult();}
  static void PumpUntilDone(ChatWindow window){var until=DateTime.UtcNow.AddSeconds(40);while(window.busy&&DateTime.UtcNow<until){Application.DoEvents();System.Threading.Thread.Sleep(50);}if(window.busy)throw new Exception("Chat response timed out");}
 }
}
