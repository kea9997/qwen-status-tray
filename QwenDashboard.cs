using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

partial class QwenStatus {
 readonly Queue<double> speedHistory=new Queue<double>();
 Panel sparkline,contextMeter;
 Label totalCountLabel,speedCountLabel;
 static readonly Color DashboardBack=Color.FromArgb(8,9,9),DashboardCard=Color.FromArgb(15,16,16),DashboardInk=Color.FromArgb(239,241,239),DashboardMuted=Color.FromArgb(161,169,161),DashboardLine=Color.FromArgb(106,111,106),DashboardAccent=Color.FromArgb(169,213,180);

 void BuildDashboard(Button chat,Button agent,Button activity,Button tokenTest,Button insights){
  MaximumSize=Size.Empty;MinimumSize=new Size(760,640);ClientSize=new Size(920,760);MaximizeBox=true;BackColor=DashboardBack;ForeColor=DashboardInk;
  foreach(Control control in Controls.Cast<Control>().ToArray())Controls.Remove(control);

  var header=new Panel{Dock=DockStyle.Top,Height=150,Padding=new Padding(22,10,22,10),BackColor=DashboardBack};header.Paint+=(s,e)=>Outline(e.Graphics,header.ClientRectangle);Controls.Add(header);
  var brand=new Label{Text="◈  QWEN  /  LOCAL STATUS",Bounds=new Rectangle(24,17,500,26),ForeColor=DashboardInk,Font=new Font("Consolas",11,FontStyle.Bold)};header.Controls.Add(brand);
  state.Parent=header;state.Bounds=new Rectangle(25,50,640,48);state.Font=new Font("Malgun Gothic",23,FontStyle.Bold);state.BackColor=DashboardBack;
  note.Parent=header;note.Bounds=new Rectangle(26,105,640,30);note.ForeColor=DashboardMuted;note.BackColor=DashboardBack;
  start.Parent=header;start.Bounds=new Rectangle(690,30,202,38);StyleDashboardButton(start,true);
  stop.Parent=header;stop.Bounds=new Rectangle(690,78,202,38);StyleDashboardButton(stop,false);
  var disabledStart=new Label{Text=start.Text,TextAlign=ContentAlignment.MiddleCenter,ForeColor=DashboardMuted,BackColor=DashboardBack};var disabledStop=new Label{Text=stop.Text,TextAlign=ContentAlignment.MiddleCenter,ForeColor=DashboardMuted,BackColor=DashboardBack};header.Controls.Add(disabledStart);header.Controls.Add(disabledStop);
  foreach(var label in new[]{disabledStart,disabledStop})label.Paint+=(s,e)=>Outline(e.Graphics,label.ClientRectangle);
  Action syncButtons=()=>{disabledStart.Visible=!start.Enabled;disabledStop.Visible=!stop.Enabled;start.Visible=start.Enabled;stop.Visible=stop.Enabled;};start.EnabledChanged+=(s,e)=>syncButtons();stop.EnabledChanged+=(s,e)=>syncButtons();
  Action positionHeader=()=>{int x=header.ClientSize.Width-228;start.Left=x;stop.Left=x;disabledStart.Bounds=start.Bounds;disabledStop.Bounds=stop.Bounds;state.Width=Math.Max(270,x-50);note.Width=state.Width;};header.Resize+=(s,e)=>positionHeader();positionHeader();syncButtons();

  var metrics=new TableLayoutPanel{Dock=DockStyle.Top,Height=132,Padding=new Padding(17,11,17,10),ColumnCount=2,RowCount=1,BackColor=DashboardBack};metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));Controls.Add(metrics);
  var usageCard=Frame();var rateCard=Frame();metrics.Controls.Add(usageCard,0,0);metrics.Controls.Add(rateCard,1,0);
  usageCard.Controls.Add(Caption("01  /  누적 처리량"));rateCard.Controls.Add(Caption("02  /  실시간 성능"));
  totalCountLabel=new Label{Text="—",Bounds=new Rectangle(17,31,270,42),ForeColor=DashboardInk,Font=new Font("Consolas",23,FontStyle.Bold)};usageCard.Controls.Add(totalCountLabel);
  usage.Parent=usageCard;usage.Bounds=new Rectangle(18,75,310,35);usage.ForeColor=DashboardMuted;usage.Font=new Font("Malgun Gothic",8);
  usageButton.Parent=usageCard;usageButton.Size=new Size(88,30);usageButton.Text="자세히";StyleDashboardButton(usageButton,false);
  usageCard.Resize+=(s,e)=>usageButton.Left=usageCard.ClientSize.Width-105;usageButton.Left=usageCard.ClientSize.Width-105;usageButton.Top=43;
  speedCountLabel=new Label{Text="— tok/s",Bounds=new Rectangle(17,31,290,42),ForeColor=DashboardAccent,Font=new Font("Consolas",23,FontStyle.Bold)};rateCard.Controls.Add(speedCountLabel);
  rate.Parent=rateCard;rate.Bounds=new Rectangle(18,74,390,23);rate.ForeColor=DashboardMuted;rate.Font=new Font("Malgun Gothic",9);
  sparkline=new Panel{BackColor=DashboardCard,Height=18,Dock=DockStyle.Bottom};sparkline.Paint+=(s,e)=>DrawSparkline(e.Graphics,sparkline.ClientRectangle);rateCard.Controls.Add(sparkline);

  var request=new Panel{Dock=DockStyle.Top,Height=101,Padding=new Padding(22,0,22,12),BackColor=DashboardBack};Controls.Add(request);
  var requestFrame=Frame();requestFrame.Dock=DockStyle.Fill;request.Controls.Add(requestFrame);
  requestFrame.Controls.Add(Caption("03  /  현재·최근 요청"));
  jobInfo.Parent=requestFrame;jobInfo.Bounds=new Rectangle(16,29,800,47);jobInfo.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;jobInfo.BorderStyle=BorderStyle.None;jobInfo.BackColor=DashboardCard;jobInfo.ForeColor=DashboardInk;jobInfo.Padding=new Padding(0,0,0,0);jobInfo.Font=new Font("Malgun Gothic",9);
  requestFrame.Resize+=(s,e)=>jobInfo.Width=requestFrame.ClientSize.Width-32;
  contextBar.Parent=requestFrame;contextBar.Visible=false;
  contextMeter=new Panel{Height=2,BackColor=DashboardCard};contextMeter.Paint+=(s,e)=>{using(var muted=new SolidBrush(DashboardLine))e.Graphics.FillRectangle(muted,0,0,contextMeter.Width,2);int filled=(int)(contextMeter.Width*contextBar.Value/(double)Math.Max(1,contextBar.Maximum));using(var accent=new SolidBrush(DashboardAccent))e.Graphics.FillRectangle(accent,0,0,filled,2);};requestFrame.Controls.Add(contextMeter);
  Action placeMeter=()=>contextMeter.Bounds=new Rectangle(16,requestFrame.ClientSize.Height-8,Math.Max(0,requestFrame.ClientSize.Width-32),2);requestFrame.Resize+=(s,e)=>placeMeter();placeMeter();

  var footer=new TableLayoutPanel{Dock=DockStyle.Bottom,Height=112,Padding=new Padding(17,7,17,13),ColumnCount=3,RowCount=2,BackColor=DashboardBack};for(int i=0;i<3;i++)footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/3));for(int i=0;i<2;i++)footer.RowStyles.Add(new RowStyle(SizeType.Percent,50));Controls.Add(footer);
  Button[] actions={chat,agent,tokenTest,activity,insights};string[] labels={"직접 대화", "Hermes 에이전트", "토큰 테스트", "최근 작업", "분석 센터"};
  for(int i=0;i<actions.Length;i++){actions[i].Text=labels[i];actions[i].Dock=DockStyle.Fill;actions[i].Margin=new Padding(5,4,5,4);StyleDashboardButton(actions[i],i==0);footer.Controls.Add(actions[i],i%3,i/3);}
  footer.Controls.Add(new Label{Text="시계 옆 아이콘에서 상태 확인",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=DashboardMuted,Font=new Font("Malgun Gothic",8)},2,1);

  var io=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(17,0,17,0),RowCount=2,ColumnCount=1,BackColor=DashboardBack};io.RowStyles.Add(new RowStyle(SizeType.Percent,45));io.RowStyles.Add(new RowStyle(SizeType.Percent,55));io.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(io);
  var inputFrame=Frame();var outputFrame=Frame();inputFrame.Margin=new Padding(5,3,5,5);outputFrame.Margin=new Padding(5,2,5,4);io.Controls.Add(inputFrame,0,0);io.Controls.Add(outputFrame,0,1);
  inputFrame.Controls.Add(Caption("04  /  최근 입력"));outputFrame.Controls.Add(Caption("05  /  최근 출력"));
  hideContentBox.Parent=inputFrame;hideContentBox.Size=new Size(148,24);hideContentBox.ForeColor=DashboardMuted;hideContentBox.BackColor=DashboardCard;hideContentBox.Text="입출력 가리기";hideContentBox.Anchor=AnchorStyles.Top|AnchorStyles.Right;
  inputFrame.Resize+=(s,e)=>hideContentBox.Left=inputFrame.ClientSize.Width-160;hideContentBox.Left=inputFrame.ClientSize.Width-160;hideContentBox.Top=6;
  AddTextArea(inputFrame,inputText);AddTextArea(outputFrame,outputText);
  Controls.SetChildIndex(io,0);Controls.SetChildIndex(footer,1);Controls.SetChildIndex(request,2);Controls.SetChildIndex(metrics,3);Controls.SetChildIndex(header,4);
 }
 static Panel Frame(){var panel=new Panel{Dock=DockStyle.Fill,Margin=new Padding(5,0,5,0),BackColor=DashboardCard};panel.Paint+=(s,e)=>Outline(e.Graphics,panel.ClientRectangle);return panel;}
 static Label Caption(string text){return new Label{Text=text,Bounds=new Rectangle(16,8,360,22),ForeColor=DashboardMuted,Font=new Font("Consolas",9,FontStyle.Bold),BackColor=DashboardCard};}
 static void Outline(Graphics g,Rectangle bounds){if(bounds.Width<2||bounds.Height<2)return;using(var pen=new Pen(DashboardLine,1))g.DrawRectangle(pen,0,0,bounds.Width-1,bounds.Height-1);}
 static void AddTextArea(Panel frame,TextBox box){box.Parent=frame;box.Bounds=new Rectangle(15,34,frame.ClientSize.Width-30,frame.ClientSize.Height-46);box.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;box.BackColor=DashboardCard;box.ForeColor=DashboardInk;box.BorderStyle=BorderStyle.None;box.Font=new Font("Malgun Gothic",10);frame.Resize+=(s,e)=>{box.Width=frame.ClientSize.Width-30;box.Height=Math.Max(30,frame.ClientSize.Height-46);};}
 static void StyleDashboardButton(Button button,bool primary){button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderSize=1;button.FlatAppearance.BorderColor=primary?DashboardAccent:DashboardLine;button.FlatAppearance.MouseOverBackColor=Color.FromArgb(31,34,31);button.BackColor=DashboardBack;button.ForeColor=primary?DashboardAccent:DashboardInk;button.Font=new Font("Malgun Gothic",9,FontStyle.Bold);}
 void TrackSpeed(double speed){if(!double.IsNaN(speed)&&speed>=0){speedHistory.Enqueue(speed);while(speedHistory.Count>60)speedHistory.Dequeue();}if(sparkline!=null)sparkline.Invalidate();}
 void DrawSparkline(Graphics g,Rectangle bounds){var values=speedHistory.ToArray();if(values.Length<2)return;g.SmoothingMode=SmoothingMode.AntiAlias;double maximum=Math.Max(1,values.Max());var points=values.Select((value,index)=>new PointF(17+(bounds.Width-34)*index/(float)(values.Length-1),bounds.Height-3-(float)(value/maximum)*(bounds.Height-7))).ToArray();using(var pen=new Pen(DashboardAccent,1.5f))g.DrawLines(pen,points);}
}
