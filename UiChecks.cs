using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
class UiChecks {
    static void Dump(Control c,int level,StringBuilder s){
        s.AppendLine(new string(' ',level*2)+c.GetType().Name+" bounds="+c.Bounds+" preferred="+c.GetPreferredSize(new Size(c.Width,0))+" autosize="+c.AutoSize+" dock="+c.Dock);
        foreach(Control child in c.Controls)Dump(child,level+1,s);
    }
    [STAThread] static void Main(){Native.SetProcessDPIAware();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);var form=new MainForm();var timer=new Timer{Interval=700};timer.Tick+=(s,e)=>{timer.Stop();var b=new StringBuilder();Dump(form,0,b);File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-layout.txt"),b.ToString());Application.Exit();};form.Shown+=(s,e)=>timer.Start();Application.Run(form);}
}
