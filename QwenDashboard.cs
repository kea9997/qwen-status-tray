using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

partial class QwenStatus {
 readonly Queue<double> speedHistory=new Queue<double>();
 Panel sparkline;
 Label totalCountLabel,speedCountLabel;
 static readonly Color DashboardBack=Color.FromArgb(13,20,32),DashboardCard=Color.FromArgb(24,35,52),DashboardInk=Color.FromArgb(236,243,250),DashboardMuted=Color.FromArgb(160,179,199),DashboardAccent=Color.FromArgb(72,207,185);
 void BuildDashboard(Button chat,Button agent,Button activity,Button tokenTest,Button insights){
  MaximumSize=Size.Empty;MinimumSize=new Size(760,620);ClientSize=new Size(920,730);MaximizeBox=true;BackColor=DashboardBack;ForeColor=DashboardInk;
  foreach(Control control in Controls.Cast<Control>().ToArray())Controls.Remove(control);
  var header=new Panel{Dock=DockStyle.Top,Height=158,Padding=new Padding(26,18,26,12)};
  header.Paint+=(s,e)=>{using(var brush=new LinearGradientBrush(header.ClientRectangle,Color.FromArgb(31,57,81),DashboardBack,LinearGradientMode.Horizontal))e.Graphics.FillRectangle(brush,header.ClientRectangle);};Controls.Add(header);
  var brand=new Label{Text="QWEN  /  LOCAL AI",Font=new Font("Segoe UI",10,FontStyle.Bold),ForeColor=DashboardAccent,BackColor=Color.Transparent,Bounds=new Rectangle(28,18,400,26)};header.Controls.Add(brand);
  state.Parent=header;state.Bounds=new Rectangle(26,51,630,49);state.Font=new Font("Malgun Gothic",25,FontStyle.Bold);state.BackColor=Color.Transparent;state.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
  note.Parent=header;note.Bounds=new Rectangle(28,105,630,34);note.ForeColor=DashboardMuted;note.BackColor=Color.Transparent;note.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
  start.Parent=header;start.Bounds=new Rectangle(686,27,200,42);start.Anchor=AnchorStyles.Top|AnchorStyles.Right;StyleDashboardButton(start,true);
  stop.Parent=header;stop.Bounds=new Rectangle(686,77,200,42);stop.Anchor=AnchorStyles.Top|AnchorStyles.Right;StyleDashboardButton(stop,false);
  header.Resize+=(s,e)=>{int x=header.ClientSize.Width-226;start.Left=x;stop.Left=x;state.Width=Math.Max(270,x-56);note.Width=state.Width;};
  int buttonX=header.ClientSize.Width-226;start.Left=buttonX;stop.Left=buttonX;state.Width=Math.Max(270,buttonX-56);note.Width=state.Width;

  var metrics=new TableLayoutPanel{Dock=DockStyle.Top,Height=139,Padding=new Padding(19,12,19,10),ColumnCount=2,RowCount=1,BackColor=DashboardBack};metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));Controls.Add(metrics);
  var usageCard=MetricCard("누적 처리량");var rateCard=MetricCard("실시간 성능");metrics.Controls.Add(usageCard,0,0);metrics.Controls.Add(rateCard,1,0);
  totalCountLabel=new Label{Text="—",Bounds=new Rectangle(17,34,290,40),ForeColor=DashboardInk,Font=new Font("Segoe UI",22,FontStyle.Bold),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};usageCard.Controls.Add(totalCountLabel);
  usage.Parent=usageCard;usage.Bounds=new Rectangle(18,76,330,39);usage.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;usage.ForeColor=DashboardMuted;usage.Font=new Font("Malgun Gothic",8);
  usageButton.Parent=usageCard;usageButton.Size=new Size(96,31);usageButton.Location=new Point(usageCard.Width-107,42);usageButton.Anchor=AnchorStyles.Top|AnchorStyles.Right;usageButton.Text="통계 보기";StyleDashboardButton(usageButton,false);
  speedCountLabel=new Label{Text="— tok/s",Bounds=new Rectangle(17,33,300,42),ForeColor=DashboardAccent,Font=new Font("Segoe UI",22,FontStyle.Bold),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};rateCard.Controls.Add(speedCountLabel);
  rate.Parent=rateCard;rate.Bounds=new Rectangle(18,77,390,28);rate.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;rate.ForeColor=DashboardMuted;rate.Font=new Font("Malgun Gothic",9);
  sparkline=new Panel{BackColor=DashboardCard,Height=24,Dock=DockStyle.Bottom};sparkline.Paint+=(s,e)=>DrawSparkline(e.Graphics,sparkline.ClientRectangle);rateCard.Controls.Add(sparkline);

  var requestCard=new Panel{Dock=DockStyle.Top,Height=102,Padding=new Padding(22,5,22,13),BackColor=DashboardBack};Controls.Add(requestCard);
  var requestTitle=new Label{Text="현재 / 최근 요청",Dock=DockStyle.Top,Height=26,ForeColor=DashboardMuted,Font=new Font("Malgun Gothic",9,FontStyle.Bold)};requestCard.Controls.Add(requestTitle);
  jobInfo.Parent=requestCard;jobInfo.Dock=DockStyle.Fill;jobInfo.BorderStyle=BorderStyle.None;jobInfo.BackColor=DashboardCard;jobInfo.ForeColor=DashboardInk;jobInfo.Padding=new Padding(12,8,8,5);
  contextBar.Parent=requestCard;contextBar.Dock=DockStyle.Bottom;contextBar.Height=7;
  requestCard.Controls.SetChildIndex(requestTitle,0);requestCard.Controls.SetChildIndex(contextBar,1);requestCard.Controls.SetChildIndex(jobInfo,2);

  var footer=new TableLayoutPanel{Dock=DockStyle.Bottom,Height=116,Padding=new Padding(19,7,19,13),ColumnCount=3,RowCount=2,BackColor=DashboardBack};
  for(int i=0;i<3;i++)footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/3));for(int i=0;i<2;i++)footer.RowStyles.Add(new RowStyle(SizeType.Percent,50));Controls.Add(footer);
  Button[] actions={chat,agent,tokenTest,activity,insights};string[] labels={"직접 대화","Hermes 에이전트","토큰 테스트","최근 작업","분석 센터"};
  for(int i=0;i<actions.Length;i++){actions[i].Text=labels[i];actions[i].Dock=DockStyle.Fill;actions[i].Margin=new Padding(5,4,5,4);StyleDashboardButton(actions[i],i==0);footer.Controls.Add(actions[i],i%3,i/3);}
  var mini=new Label{Text="상태는 시계 옆 아이콘에서도 확인할 수 있습니다.",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=DashboardMuted,Font=new Font("Malgun Gothic",8)};footer.Controls.Add(mini,2,1);

  var privacy=new Panel{Dock=DockStyle.Top,Height=35,Padding=new Padding(24,0,24,0),BackColor=DashboardBack};Controls.Add(privacy);
  hideContentBox.Parent=privacy;hideContentBox.Dock=DockStyle.Right;hideContentBox.Width=170;hideContentBox.ForeColor=DashboardMuted;hideContentBox.Text="화면 내용 가리기";
  var section=new Label{Text="최근 입출력",Dock=DockStyle.Left,Width=170,ForeColor=DashboardMuted,TextAlign=ContentAlignment.MiddleLeft,Font=new Font("Malgun Gothic",9,FontStyle.Bold)};privacy.Controls.Add(section);
  var ioTabs=new TabControl{Dock=DockStyle.Fill,Font=new Font("Malgun Gothic",10),Padding=new Point(18,7)};Controls.Add(ioTabs);
  var inputPage=new TabPage("입력"){BackColor=DashboardCard,Padding=new Padding(12)};var outputPage=new TabPage("출력"){BackColor=DashboardCard,Padding=new Padding(12)};ioTabs.TabPages.Add(inputPage);ioTabs.TabPages.Add(outputPage);
  inputText.Parent=inputPage;inputText.Dock=DockStyle.Fill;outputText.Parent=outputPage;outputText.Dock=DockStyle.Fill;
  foreach(var box in new[]{inputText,outputText}){box.BackColor=DashboardCard;box.ForeColor=DashboardInk;box.BorderStyle=BorderStyle.None;box.Font=new Font("Malgun Gothic",10);}
  // Dock order: the tab body receives space left by the fixed header, cards and actions.
  Controls.SetChildIndex(ioTabs,0);Controls.SetChildIndex(privacy,1);Controls.SetChildIndex(footer,2);Controls.SetChildIndex(requestCard,3);Controls.SetChildIndex(metrics,4);Controls.SetChildIndex(header,5);
 }
 static Panel MetricCard(string title){
  var panel=new Panel{Dock=DockStyle.Fill,Margin=new Padding(5,0,5,0),BackColor=DashboardCard};
  panel.Controls.Add(new Label{Text=title,Bounds=new Rectangle(17,10,210,24),ForeColor=DashboardMuted,Font=new Font("Malgun Gothic",9,FontStyle.Bold)});
  return panel;
 }
 static void StyleDashboardButton(Button button,bool primary){button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderSize=primary?0:1;button.FlatAppearance.BorderColor=Color.FromArgb(58,78,98);button.BackColor=primary?DashboardAccent:DashboardCard;button.ForeColor=primary?DashboardBack:DashboardInk;button.Font=new Font("Malgun Gothic",9,FontStyle.Bold);}
 void TrackSpeed(double speed){if(!double.IsNaN(speed)&&speed>=0){speedHistory.Enqueue(speed);while(speedHistory.Count>60)speedHistory.Dequeue();}if(sparkline!=null)sparkline.Invalidate();}
 void DrawSparkline(Graphics g,Rectangle bounds){
  var values=speedHistory.ToArray();if(values.Length<2)return;g.SmoothingMode=SmoothingMode.AntiAlias;double maximum=Math.Max(1,values.Max());
  var points=values.Select((value,index)=>new PointF(17+(bounds.Width-34)*index/(float)(values.Length-1),bounds.Height-3-(float)(value/maximum)*(bounds.Height-8))).ToArray();
  using(var pen=new Pen(DashboardAccent,2))g.DrawLines(pen,points);
 }
}
