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
  public string Id{get;set;}public string Title{get;set;}public List<ChatTurn> Turns{get;set;}
  public override string ToString(){return Title;}
 }
 internal static class ChatHistory {
  internal static string FilePath=Path.Combine(DataRoot,"chat-history.json");
  internal static List<ChatConversation> Load(){
   try{
    if(!File.Exists(FilePath))return new List<ChatConversation>();
    var result=new JavaScriptSerializer{MaxJsonLength=10000000}.Deserialize<List<ChatConversation>>(File.ReadAllText(FilePath,Encoding.UTF8));
    return result==null?new List<ChatConversation>():result.Where(x=>x!=null&&ValidId(x.Id)).Select(x=>{x.Turns=x.Turns??new List<ChatTurn>();return x;}).ToList();
   }catch{return new List<ChatConversation>();}
  }
  static bool ValidId(string id){Guid parsed;return Guid.TryParse(id,out parsed);}
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
   static readonly Color Back=Color.FromArgb(8,9,9),Card=Color.FromArgb(16,17,17),Ink=Color.FromArgb(239,241,239),Muted=Color.FromArgb(161,169,161),Accent=Color.FromArgb(169,213,180);
  readonly ListBox conversations=new ListBox();readonly RichTextBox transcript=new RichTextBox();readonly TextBox composer=new TextBox();
  readonly Label heading=new Label(),status=new Label();readonly Button send=new Button(),cancel=new Button(),delete=new Button();
  readonly Timer poll=new Timer();readonly List<ChatConversation> history=ChatHistory.Load();
  ChatConversation current;string jobId,lastPreview;bool busy,polling,closing;
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
   catch(WebException ex){try{using(var reader=new StreamReader(ex.Response.GetResponseStream(),Encoding.UTF8)){var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());var error=data.ContainsKey("error")?data["error"] as Dictionary<string,object>:null;throw new Exception(error!=null?Convert.ToString(error["message"]):ex.Message);}}catch(NullReferenceException){throw new Exception("공통 대기열에 연결할 수 없습니다.");}}
  }
  static string Value(Dictionary<string,object> data,string key){return data!=null&&data.ContainsKey(key)&&data[key]!=null?Convert.ToString(data[key]):"";}
  public ChatWindow(){
   Text="Qwen · 직접 대화";ClientSize=new Size(1060,740);MinimumSize=new Size(760,540);Font=new Font("Malgun Gothic",10);BackColor=Back;ForeColor=Ink;StartPosition=FormStartPosition.CenterScreen;
    var sidebar=new Panel{Dock=DockStyle.Left,Width=250,BackColor=Back,Padding=new Padding(14)};sidebar.Paint+=(s,e)=>{using(var pen=new Pen(Muted,1))e.Graphics.DrawLine(pen,sidebar.Width-1,0,sidebar.Width-1,sidebar.Height);};Controls.Add(sidebar);
    var newChat=ButtonStyle("＋ 새 대화",Back,Accent);newChat.Dock=DockStyle.Top;newChat.Height=42;newChat.Click+=(s,e)=>NewConversation();sidebar.Controls.Add(newChat);
    delete=ButtonStyle("대화 삭제",Back,Ink);delete.Dock=DockStyle.Bottom;delete.Height=38;delete.Click+=async(s,e)=>await DeleteConversation();sidebar.Controls.Add(delete);
    conversations.Dock=DockStyle.Fill;conversations.BorderStyle=BorderStyle.None;conversations.BackColor=sidebar.BackColor;conversations.ForeColor=Ink;conversations.Font=new Font(Font.FontFamily,10);conversations.IntegralHeight=false;conversations.DrawMode=DrawMode.OwnerDrawFixed;conversations.ItemHeight=36;conversations.DrawItem+=(s,e)=>{if(e.Index<0)return;bool selected=(e.State&DrawItemState.Selected)!=0;using(var background=new SolidBrush(selected?Card:sidebar.BackColor))e.Graphics.FillRectangle(background,e.Bounds);using(var brush=new SolidBrush(selected?Accent:Ink))e.Graphics.DrawString(conversations.Items[e.Index].ToString(),conversations.Font,brush,new RectangleF(e.Bounds.Left+11,e.Bounds.Top+8,e.Bounds.Width-17,e.Bounds.Height-8));if(selected)using(var pen=new Pen(Muted,1))e.Graphics.DrawRectangle(pen,e.Bounds.Left,e.Bounds.Top,e.Bounds.Width-1,e.Bounds.Height-1);};conversations.SelectedIndexChanged+=(s,e)=>SelectConversation();sidebar.Controls.Add(conversations);sidebar.Controls.SetChildIndex(conversations,0);
   var body=new Panel{Dock=DockStyle.Fill,Padding=new Padding(20,16,20,16),BackColor=Back};Controls.Add(body);body.BringToFront();
    var header=new Panel{Dock=DockStyle.Top,Height=68};header.Paint+=(s,e)=>{using(var pen=new Pen(Muted,1))e.Graphics.DrawLine(pen,0,header.Height-1,header.Width,header.Height-1);};body.Controls.Add(header);
   heading.Text="직접 대화";heading.Font=new Font(Font.FontFamily,18,FontStyle.Bold);heading.ForeColor=Ink;heading.Dock=DockStyle.Top;heading.Height=38;header.Controls.Add(heading);
   status.ForeColor=Muted;status.Dock=DockStyle.Bottom;status.Height=26;status.Text="같은 대화에서는 앞선 내용을 이어갑니다.";header.Controls.Add(status);
   var composePanel=new Panel{Dock=DockStyle.Bottom,Height=155,Padding=new Padding(0,8,0,0)};body.Controls.Add(composePanel);
   composePanel.Controls.Add(new Label{Text="메시지 입력  ·  Enter 전송  ·  Shift+Enter 줄바꿈",Dock=DockStyle.Top,Height=26,ForeColor=Muted,Font=new Font("Malgun Gothic",9)});
   var actions=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=43,FlowDirection=FlowDirection.RightToLeft};composePanel.Controls.Add(actions);
    send=ButtonStyle("보내기  ↵",Back,Accent);send.Width=116;send.Click+=async(s,e)=>await Send();actions.Controls.Add(send);
    cancel=ButtonStyle("중단",Back,Ink);cancel.Width=92;cancel.Visible=false;cancel.Click+=async(s,e)=>await Cancel();actions.Controls.Add(cancel);
   composer.Multiline=true;composer.AcceptsReturn=true;composer.ScrollBars=ScrollBars.Vertical;composer.Dock=DockStyle.Fill;composer.BackColor=Card;composer.ForeColor=Ink;composer.BorderStyle=BorderStyle.FixedSingle;composer.Font=new Font(Font.FontFamily,11);composer.KeyDown+=async(s,e)=>{if(e.KeyCode==Keys.Enter&&!e.Shift){e.SuppressKeyPress=true;await Send();}};composePanel.Controls.Add(composer);composer.BringToFront();
   transcript.Dock=DockStyle.Fill;transcript.ReadOnly=true;transcript.BorderStyle=BorderStyle.None;transcript.BackColor=Back;transcript.ForeColor=Ink;transcript.Font=new Font(Font.FontFamily,11);transcript.ScrollBars=RichTextBoxScrollBars.Vertical;transcript.DetectUrls=true;body.Controls.Add(transcript);transcript.BringToFront();
   poll.Interval=600;poll.Tick+=async(s,e)=>await Poll();poll.Start();
   foreach(var item in history)conversations.Items.Add(item);
   if(conversations.Items.Count==0)NewConversation();else conversations.SelectedIndex=0;
   FormClosing+=(s,e)=>{if(!closing&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
   FormClosed+=(s,e)=>poll.Dispose();
  }
   static Button ButtonStyle(string label,Color background,Color foreground){var button=new Button{Text=label,BackColor=background,ForeColor=foreground,FlatStyle=FlatStyle.Flat,Height=36,Margin=new Padding(4),Font=new Font("Malgun Gothic",10,FontStyle.Bold)};button.FlatAppearance.BorderColor=foreground==Accent?Accent:Muted;button.FlatAppearance.MouseOverBackColor=Card;return button;}
   void SetBusy(bool value){busy=value;send.Visible=!value;cancel.Visible=value;delete.Enabled=!value;conversations.Enabled=!value;composer.Enabled=!value;}
  void NewConversation(){
   if(busy)return;
   if(current!=null&&current.Turns.Count==0){conversations.SelectedItem=current;composer.Focus();return;}
   var item=new ChatConversation{Id=Guid.NewGuid().ToString("N"),Title="새 대화",Turns=new List<ChatTurn>()};history.Insert(0,item);conversations.Items.Insert(0,item);conversations.SelectedIndex=0;ChatHistory.Save(history);composer.Focus();
  }
  void SelectConversation(){if(busy)return;current=conversations.SelectedItem as ChatConversation;Render();}
  void Render(string pending=null){
   transcript.Clear();if(current==null)return;heading.Text=current.Title;
   if(current.Turns.Count==0&&pending==null){transcript.SelectionColor=Muted;transcript.AppendText("새 대화가 준비됐습니다.\n아래에 질문을 입력하면 Qwen이 답합니다. 같은 대화에서 이어서 질문할 수 있습니다.");return;}
   foreach(var turn in current.Turns){Append(turn.Role=="user"?"나":"Qwen",turn.Text,turn.Role=="user"?Accent:Ink);}
   if(pending!=null)Append("Qwen",pending,Ink);
   transcript.SelectionStart=transcript.TextLength;transcript.ScrollToCaret();
  }
  void Append(string role,string content,Color color){
   int start=transcript.TextLength;transcript.AppendText(role+Environment.NewLine);
   transcript.Select(start,role.Length);transcript.SelectionColor=role=="나"?Accent:Muted;transcript.SelectionFont=new Font(Font.FontFamily,9,FontStyle.Bold);
   start=transcript.TextLength;transcript.AppendText(content+Environment.NewLine+Environment.NewLine);
   transcript.Select(start,content.Length);transcript.SelectionColor=color;transcript.SelectionFont=new Font(Font.FontFamily,11);
  }
  async Task Send(){
   if(busy||current==null||string.IsNullOrWhiteSpace(composer.Text))return;
   string prompt=composer.Text.Trim(),session=current.Id;composer.Clear();lastPreview=null;SetBusy(true);status.Text="요청을 보내는 중…";
   try{
    var result=await Task.Run(()=>Api("POST","/jobs",new{mode="chat",session=session,prompt=prompt}));
    jobId=Value(result,"id");if(string.IsNullOrEmpty(jobId))throw new Exception("작업 ID가 없습니다.");
    current.Turns.Add(new ChatTurn{Role="user",Text=prompt});if(current.Title=="새 대화")current.Title=prompt.Length>28?prompt.Substring(0,28)+"…":prompt;
    ChatHistory.Save(history);conversations.Items[conversations.SelectedIndex]=current;Render("답변 생성 중…");status.Text="Qwen 응답 대기 중…";
   }catch(Exception ex){SetBusy(false);status.Text="요청 실패: "+ex.Message;composer.Text=prompt;jobId=null;}
  }
  async Task Poll(){
   if(!busy||polling||string.IsNullOrEmpty(jobId))return;polling=true;
   try{
    string id=jobId;var result=await Task.Run(()=>Api("GET","/jobs/"+id));
    string phase=Value(result,"status"),output=Value(result,"output");
    if(phase=="running"||phase=="queued"){
     status.Text=phase=="queued"?"공통 대기열에서 대기 중…":Value(result,"phase")==""?"답변 생성 중…":Value(result,"phase");
     string preview=string.IsNullOrEmpty(output)?"답변 생성 중…":output;
     if(preview!=lastPreview){lastPreview=preview;Render(preview);}return;
    }
    jobId=null;lastPreview=null;SetBusy(false);
    if(phase=="completed"||phase=="incomplete"){
     current.Turns.Add(new ChatTurn{Role="assistant",Text=output});ChatHistory.Save(history);
     status.Text=phase=="completed"?"완료 · 대화 맥락 저장됨":"출력 한도에 도달했습니다 · 이어서 질문할 수 있습니다";
    }else status.Text=phase=="cancelled"?"요청 중단됨":"응답 실패: "+Value(result,"error");
    Render(phase=="completed"||phase=="incomplete"?null:output);composer.Focus();
   }catch(Exception ex){status.Text="연결 확인 필요: "+ex.Message;jobId=null;SetBusy(false);}
   finally{polling=false;}
  }
  async Task Cancel(){if(!busy||string.IsNullOrEmpty(jobId))return;try{string id=jobId;await Task.Run(()=>Api("DELETE","/jobs/"+id));status.Text="중단 요청을 보냈습니다.";}catch(Exception ex){status.Text="중단 실패: "+ex.Message;}}
  async Task DeleteConversation(){
   if(busy||current==null)return;
   if(MessageBox.Show("이 대화와 기록을 삭제할까요?","Qwen 대화 삭제",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
   try{string id=current.Id;if(current.Turns.Count>0)await Task.Run(()=>Api("DELETE","/conversations/"+id));int index=conversations.SelectedIndex;history.Remove(current);conversations.Items.RemoveAt(index);current=null;ChatHistory.Save(history);if(conversations.Items.Count==0)NewConversation();else conversations.SelectedIndex=Math.Min(index,conversations.Items.Count-1);status.Text="대화를 삭제했습니다.";}
   catch(Exception ex){status.Text="삭제 실패: "+ex.Message;}
  }
  public void Shutdown(){closing=true;Close();}
  internal static void Test(){
   string old=ChatHistory.FilePath;string path=Path.Combine(Path.GetTempPath(),"qwen-chat-test-"+Guid.NewGuid().ToString("N")+".json");
   try{ChatHistory.FilePath=path;var sample=new List<ChatConversation>{new ChatConversation{Id=Guid.NewGuid().ToString("N"),Title="맥락 테스트",Turns=new List<ChatTurn>{new ChatTurn{Role="user",Text="안녕"},new ChatTurn{Role="assistant",Text="안녕하세요"}}}};ChatHistory.Save(sample);var loaded=ChatHistory.Load();if(loaded.Count!=1||loaded[0].Turns.Count!=2||loaded[0].Turns[1].Text!="안녕하세요")throw new Exception("Chat history roundtrip failed");using(var window=new ChatWindow()){window.Show();Application.DoEvents();if(window.current==null||!window.transcript.Text.Contains("안녕하세요"))throw new Exception("Chat transcript rendering failed");using(var image=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save(Path.Combine(DataRoot,"chat-ui-preview.png"));}window.Shutdown();}File.WriteAllText(Path.Combine(DataRoot,"chat-test.txt"),"PASS: persistent conversation and chat rendering");}
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
