using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Loopback-only, read-only OBS endpoint. No desktop capture, credentials or files are served.
class ObsOverlayServer : IDisposable {
    System.Net.Sockets.TcpListener listener;
    readonly object sync=new object(); byte[] frame; volatile bool stopped;
    internal const string Url="http://127.0.0.1:18765/";
    internal ObsOverlayServer(){
        Clear();listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,18765);listener.Start();
        new System.Threading.Thread(Serve){IsBackground=true,Name="DalvitLens OBS"}.Start();
    }
    internal void SetFrame(byte[] png){lock(sync)frame=png;}
    internal void Clear(){using(var b=new Bitmap(1920,1080))using(var m=new MemoryStream()){b.Save(m,ImageFormat.Png);SetFrame(m.ToArray());}}
    void Serve(){
        const string html="<!doctype html><html><meta charset='utf-8'><style>html,body{margin:0;background:transparent;overflow:hidden;width:100%;height:100%}img{width:100%;height:100%;object-fit:contain}</style><img id='overlay'><script>const img=document.getElementById('overlay');let previous;async function tick(){const ctl=new AbortController();const timeout=setTimeout(()=>ctl.abort(),1200);try{const r=await fetch('/overlay.png',{cache:'no-store',signal:ctl.signal});if(!r.ok)throw Error();const blob=await r.blob();const next=URL.createObjectURL(blob);await new Promise((ok,no)=>{img.onload=ok;img.onerror=no;img.src=next});if(previous)URL.revokeObjectURL(previous);previous=next;img.style.visibility='visible'}catch(e){img.style.visibility='hidden'}finally{clearTimeout(timeout);setTimeout(tick,250)}}tick();</script></html>";
        while(!stopped)try{
            using(var client=listener.AcceptTcpClient()){
                client.ReceiveTimeout=1500;client.SendTimeout=1500;
                using(var stream=client.GetStream()){
                    var request=new MemoryStream();int ch;while(request.Length<8192 && (ch=stream.ReadByte())>=0){request.WriteByte((byte)ch);var a=request.GetBuffer();int n=(int)request.Length;if(n>=4&&a[n-4]==13&&a[n-3]==10&&a[n-2]==13&&a[n-1]==10)break;}
                    var line=Encoding.ASCII.GetString(request.ToArray()).Split('\n')[0].Trim();
                    byte[] body; string type,status;
                    if(line=="GET / HTTP/1.1"||line=="GET / HTTP/1.0"){body=Encoding.UTF8.GetBytes(html);type="text/html; charset=utf-8";status="200 OK";}
                    else if(line.StartsWith("GET /overlay.png ")){lock(sync)body=frame;type="image/png";status="200 OK";}
                    else{body=Encoding.UTF8.GetBytes("Not found");type="text/plain";status="404 Not Found";}
                    var header=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+"\r\nContent-Type: "+type+"\r\nContent-Length: "+body.Length+"\r\nCache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n");stream.Write(header,0,header.Length);stream.Write(body,0,body.Length);
                }
            }
        }catch{if(stopped)return;}
    }
    public void Dispose(){stopped=true;if(listener!=null)listener.Stop();}
}

static class Native {
    [StructLayout(LayoutKind.Sequential)] internal struct POINT {public int x,y;public POINT(int x,int y){this.x=x;this.y=y;}}
    [StructLayout(LayoutKind.Sequential)] internal struct SIZE {public int cx,cy;public SIZE(int x,int y){cx=x;cy=y;}}
    [StructLayout(LayoutKind.Sequential,Pack=1)] internal struct BLEND {public byte op,flags,alpha,format;}
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool UpdateLayeredWindow(IntPtr h,IntPtr dst,ref POINT pos,ref SIZE size,IntPtr src,ref POINT origin,int key,ref BLEND blend,int flags);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr h,IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int L,T,R,B; }
}
// Kill owned inference/OCR helpers even if the UI process is forcibly closed.
class HelperLifetime : IDisposable {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs,string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job,int info,ref LIMITS limits,uint length);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] struct BASIC {public long processTime,jobTime;public uint flags;public UIntPtr min,max;public uint count;public UIntPtr affinity;public uint priority,scheduling;}
    [StructLayout(LayoutKind.Sequential)] struct IO {public ulong readOps,writeOps,otherOps,readBytes,writeBytes,otherBytes;}
    [StructLayout(LayoutKind.Sequential)] struct LIMITS {public BASIC basic;public IO io;public UIntPtr processMemory,jobMemory,peakProcessMemory,peakJobMemory;}
    IntPtr handle;
    internal HelperLifetime(){handle=CreateJobObject(IntPtr.Zero,null);var limits=new LIMITS();limits.basic.flags=0x2000;if(handle!=IntPtr.Zero&&!SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf(limits))){CloseHandle(handle);handle=IntPtr.Zero;}}
    internal void Add(Process p){if(handle!=IntPtr.Zero)AssignProcessToJobObject(handle,p.Handle);}
    public void Dispose(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
}
class LocalTranslationEngine : IDisposable {
    readonly string root;readonly HelperLifetime lifetime;readonly object logLock=new object();Process process;int version;string currentKind;
    internal volatile string State="로컬 번역 엔진 대기 중";
    internal volatile bool IsReady;
    internal LocalTranslationEngine(string root,HelperLifetime lifetime){this.root=root;this.lifetime=lifetime;}
    internal void Start(string kind="hymt"){
        kind=kind=="exaone"?"exaone":kind=="gemma"?"gemma":"hymt";
        if(process!=null&&!process.HasExited&&currentKind==kind)return;
        if(process!=null)Stop();IsReady=false;
        string displayName=kind=="exaone"?"EXAONE 3.5 7.8B":kind=="gemma"?"TranslateGemma 4B":"Hy-MT2 1.8B";
        string filename=kind=="exaone"?"EXAONE-3.5-7.8B-Q4_K_M.gguf":kind=="gemma"?"TranslateGemma-4B-Q4_K_M.gguf":"Hy-MT2-1.8B-Q4_K_M.gguf";
        string alias=kind=="exaone"?"dalvit-exaone-7.8b":kind=="gemma"?"dalvit-gemma-4b":"dalvit-hymt2-1.8b";
        string exe=Path.Combine(root,"runtime","llama","llama-server.exe"),model=Path.Combine(root,"models",filename);
        if(!File.Exists(exe)||!File.Exists(model)){State=displayName+" 미설치 · 아래 [AI 모델 설치 · 관리] 버튼으로 내려받으세요.";return;}
        string keyPath=Path.Combine(root,"local-model.key");
        if(!File.Exists(keyPath)){var bytes=new byte[32];using(var random=System.Security.Cryptography.RandomNumberGenerator.Create())random.GetBytes(bytes);File.WriteAllText(keyPath,Convert.ToBase64String(bytes),Encoding.ASCII);}
        // Adopt only our own executable from the recorded PID; never stop unrelated servers.
        try{int pid;if(int.TryParse(File.ReadAllText(Path.Combine(root,"local-server.pid")).Trim(),out pid)){var candidate=Process.GetProcessById(pid);if(!candidate.HasExited&&String.Equals(candidate.MainModule.FileName,exe,StringComparison.OrdinalIgnoreCase)){
            string recorded=File.Exists(Path.Combine(root,"local-server-model.txt"))?File.ReadAllText(Path.Combine(root,"local-server-model.txt")).Trim():"hymt";
            if(recorded==kind)process=candidate;else{candidate.Kill();candidate.WaitForExit(2000);candidate.Dispose();}
        }else candidate.Dispose();}}catch{}
        if(process==null||process.HasExited){
            // Native key-file parsing does not accept Korean paths; use ASCII relative paths.
            var psi=new ProcessStartInfo(exe,"-m models\\"+filename+" -ngl 99 -c 4096 -np 1 -b 512 -ub 256 -fa on --cache-ram 0 --host 127.0.0.1 --port 18766 --alias "+alias+" --no-webui --api-key-file local-model.key");
            psi.WorkingDirectory=root;psi.UseShellExecute=false;psi.CreateNoWindow=true;psi.RedirectStandardOutput=true;psi.RedirectStandardError=true;
            process=new Process{StartInfo=psi};process.OutputDataReceived+=Log;process.ErrorDataReceived+=Log;
            process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();File.WriteAllText(Path.Combine(root,"local-server.pid"),process.Id.ToString());
            File.WriteAllText(Path.Combine(root,"local-server-model.txt"),kind);
        }
        currentKind=kind;lifetime.Add(process);State=displayName+" · GPU 준비 중…";int ticket=++version;var startedProcess=process;
        new System.Threading.Thread(()=>{
            for(int i=0;i<120&&ticket==version;i++){
                try{if(startedProcess.HasExited){if(ticket==version)State=displayName+" 시작 실패 · local-engine.log 확인";return;}
                    var request=(System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:18766/health");request.Timeout=500;request.Proxy=null;
                    using(var response=request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream())){if(reader.ReadToEnd().Contains("ok")){if(ticket==version){IsReady=true;State=displayName+" · GPU 준비 완료";}return;}}
                }catch{}System.Threading.Thread.Sleep(300);
            }
            if(ticket==version)State=displayName+" 준비 시간 초과 · 엔진 다시 시작을 눌러주세요.";
        }){IsBackground=true}.Start();
    }
    void Log(object sender,DataReceivedEventArgs e){if(e.Data==null)return;try{lock(logLock)File.AppendAllText(Path.Combine(root,"local-engine.log"),e.Data+Environment.NewLine);}catch{}}
    internal void Stop(){version++;IsReady=false;if(process!=null){try{if(!process.HasExited){process.Kill();process.WaitForExit(2000);}}catch{}process.Dispose();process=null;}currentKind=null;State="로컬 번역 엔진 중지됨";}
    public void Dispose(){Stop();}
}
public class Settings {
    public string backend="exaone", apiKey="", sourceLang="en", hudPosition="top-center", nearAnchor="center";
    public int fontSize=13, nearWidth=1280, nearHeight=860;
    public bool nearFixed=false;
    public bool showCaptureFlash=true;
    public int guideOpacity=30;
    public bool tooltipLine=true;
    public int tooltipLineOpacity=73, tooltipOpacity=100, obsTooltipOpacity=48;
    public double nearRegionX,nearRegionY,nearRegionW,nearRegionH;
    public string nearMonitor="";
    internal static string NormalizeAnchor(string a){
        if(String.IsNullOrEmpty(a))return "center";
        string n=a.Trim().ToLowerInvariant();
        return (n=="right"||n=="left"||n=="top"||n=="bottom")?n:"center";
    }
    internal static string AnchorLabel(string a){
        switch(NormalizeAnchor(a)){
            case "right":  return "커서 오른쪽 →";
            case "left":   return "← 커서 왼쪽";
            case "top":    return "↑ 커서 위쪽(상)";
            case "bottom": return "↓ 커서 아래쪽(하)";
            default:       return "커서 정중앙";
        }
    }
    internal static Rectangle GetCursorRelativeBox(Point cur, int w, int h, string anchorMode){
        string a=NormalizeAnchor(anchorMode);
        int padX=Math.Min(72,Math.Max(20,w/10));
        int padY=Math.Min(64,Math.Max(18,h/10));
        switch(a){
            case "right":  return new Rectangle(cur.X-padX, cur.Y-h/2, w, h);
            case "left":   return new Rectangle(cur.X-w+padX, cur.Y-h/2, w, h);
            case "top":    return new Rectangle(cur.X-w/2, cur.Y-h+padY, w, h);
            case "bottom": return new Rectangle(cur.X-w/2, cur.Y-padY, w, h);
            default:       return new Rectangle(cur.X-w/2, cur.Y-h/2, w, h);
        }
    }
}
class RegionPicker : Form {
    readonly Bitmap background;readonly Rectangle desktop;Rectangle selected;Point down;Rectangle initial;int handle=-1;bool dragging;Rectangle dragScreen;
    internal bool ReleaseToCursorRequested;
    internal Rectangle SelectedRegion {get{return new Rectangle(desktop.X+selected.X,desktop.Y+selected.Y,selected.Width,selected.Height);}}
    internal RegionPicker(Settings cfg, Point triggerCursor){
        desktop=SystemInformation.VirtualScreen;background=new Bitmap(desktop.Width,desktop.Height);
        using(var g=Graphics.FromImage(background))g.CopyFromScreen(desktop.Location,Point.Empty,desktop.Size);
        Text="DalvitLens · F8 영역 지정";AutoScaleMode=AutoScaleMode.None;StartPosition=FormStartPosition.Manual;FormBorderStyle=FormBorderStyle.None;Bounds=desktop;TopMost=true;ShowInTaskbar=false;DoubleBuffered=true;KeyPreview=true;Cursor=Cursors.Cross;
        var monitor=Screen.FromPoint(triggerCursor).Bounds;
        dragScreen=monitor;dragScreen.Offset(-desktop.X,-desktop.Y);
        if(cfg!=null&&cfg.nearFixed&&cfg.nearRegionW>0){
            int fx=dragScreen.X+(int)Math.Round(cfg.nearRegionX*dragScreen.Width);
            int fy=dragScreen.Y+(int)Math.Round(cfg.nearRegionY*dragScreen.Height);
            int fw=Math.Max(60,(int)Math.Round(cfg.nearRegionW*dragScreen.Width));
            int fh=Math.Max(60,(int)Math.Round(cfg.nearRegionH*dragScreen.Height));
            selected=ClampToMonitor(new Rectangle(fx,fy,fw,fh),dragScreen);
        }else{
            selected=Rectangle.Empty;
        }
        var toolbar=new FlowLayoutPanel{AutoSize=true,BackColor=MaterialTheme.Card,Padding=new Padding(14,10,14,10),Location=new Point(monitor.X-desktop.X+24,monitor.Y-desktop.Y+24)};
        toolbar.Controls.Add(new Label{AutoSize=true,Text="🎯 F8 영역 지정 모드  ·  마우스로 번역할 영역을 드래그한 뒤 [영역 지정 저장 (Enter)]",ForeColor=MaterialTheme.Number,Font=new Font("맑은 고딕",10.5f,FontStyle.Bold),Margin=new Padding(4,11,12,0)});
        var save=new MaterialButton{Text="📌 영역 지정 저장 (Enter)",Filled=true};save.Click+=(s,e)=>AcceptRegion();toolbar.Controls.Add(save);
        var release=new MaterialButton{Text="🔓 영역 지정 해제 · 커서 기준으로 번역 (R)"};release.Click+=(s,e)=>ReleaseRegion();toolbar.Controls.Add(release);
        var cancel=new MaterialButton{Text="취소 (Esc)"};cancel.Click+=(s,e)=>{DialogResult=DialogResult.Cancel;Close();};toolbar.Controls.Add(cancel);Controls.Add(toolbar);
        KeyDown+=(s,e)=>{
            if(e.KeyCode==Keys.Escape){DialogResult=DialogResult.Cancel;Close();}
            else if(e.KeyCode==Keys.Enter)AcceptRegion();
            else if(e.KeyCode==Keys.R||e.KeyCode==Keys.Delete||e.KeyCode==Keys.Back)ReleaseRegion();
        };
    }
    void ReleaseRegion(){
        ReleaseToCursorRequested=true;
        DialogResult=DialogResult.OK;
        Close();
    }
    static Rectangle ClampToMonitor(Rectangle r, Rectangle mon){
        int w=Math.Max(48,Math.Min(mon.Width,r.Width));
        int h=Math.Max(48,Math.Min(mon.Height,r.Height));
        int x=Math.Max(mon.Left,Math.Min(r.X,mon.Right-w));
        int y=Math.Max(mon.Top,Math.Min(r.Y,mon.Bottom-h));
        return new Rectangle(x,y,w,h);
    }
    protected override void OnMouseWheel(MouseEventArgs e){
        base.OnMouseWheel(e);
        if(selected.IsEmpty)return;
        int stepW=e.Delta>0?80:-80;
        int stepH=e.Delta>0?54:-54;
        int cx=selected.X+selected.Width/2, cy=selected.Y+selected.Height/2;
        int nw=Math.Max(80,Math.Min(dragScreen.Width,selected.Width+stepW));
        int nh=Math.Max(60,Math.Min(dragScreen.Height,selected.Height+stepH));
        selected=ClampToMonitor(new Rectangle(cx-nw/2,cy-nh/2,nw,nh),dragScreen);
        Invalidate();
    }
    void AcceptRegion(){if(selected.Width<48||selected.Height<48){System.Media.SystemSounds.Beep.Play();return;}ReleaseToCursorRequested=false;DialogResult=DialogResult.OK;Close();}
    static Rectangle Rect(Point a,Point b){return Rectangle.FromLTRB(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));}
    Rectangle[] Handles(){if(selected.IsEmpty)return new Rectangle[0];int x=selected.X,y=selected.Y,r=selected.Right,b=selected.Bottom,cx=x+selected.Width/2,cy=y+selected.Height/2;return new[]{new Point(x,y),new Point(cx,y),new Point(r,y),new Point(r,cy),new Point(r,b),new Point(cx,b),new Point(x,b),new Point(x,cy)}.Select(p=>new Rectangle(p.X-10,p.Y-10,20,20)).ToArray();}
    Rectangle CenterMoveGrip(){if(selected.IsEmpty)return Rectangle.Empty;return new Rectangle(selected.X+selected.Width/2-22,selected.Y+selected.Height/2-22,44,44);}
    int Hit(Point p){
        if(selected.IsEmpty)return -1;
        var handles=Handles();
        for(int i=0;i<handles.Length;i++)if(handles[i].Contains(p))return i;
        if(CenterMoveGrip().Contains(p))return 8;
        return -1;
    }
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Right){DialogResult=DialogResult.Cancel;Close();return;}if(e.Button!=MouseButtons.Left)return;down=e.Location;initial=selected;handle=Hit(down);dragging=true;Capture=true;dragScreen=Screen.FromPoint(PointToScreen(down)).Bounds;dragScreen.Offset(-desktop.X,-desktop.Y);if(handle<0)selected=new Rectangle(down,Size.Empty);Invalidate();}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);
        if(!dragging){int h=Hit(e.Location);Cursor=h==8?Cursors.SizeAll:(h==0||h==4)?Cursors.SizeNWSE:(h==2||h==6)?Cursors.SizeNESW:(h==1||h==5)?Cursors.SizeNS:(h==3||h==7)?Cursors.SizeWE:Cursors.Cross;return;}
        var p=new Point(Math.Max(dragScreen.Left,Math.Min(e.X,dragScreen.Right)),Math.Max(dragScreen.Top,Math.Min(e.Y,dragScreen.Bottom)));
        if(handle<0)selected=Rect(down,p);
        else if(handle==8){selected=initial;selected.Offset(p.X-down.X,p.Y-down.Y);selected.X=Math.Max(dragScreen.Left,Math.Min(selected.X,dragScreen.Right-selected.Width));selected.Y=Math.Max(dragScreen.Top,Math.Min(selected.Y,dragScreen.Bottom-selected.Height));}
        else {int l=initial.Left,t=initial.Top,r=initial.Right,b=initial.Bottom;if(handle==0||handle==6||handle==7)l=p.X;if(handle==0||handle==1||handle==2)t=p.Y;if(handle==2||handle==3||handle==4)r=p.X;if(handle==4||handle==5||handle==6)b=p.Y;selected=Rect(new Point(l,t),new Point(r,b));}
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=false;Capture=false;Invalidate();}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.DrawImageUnscaled(background,0,0);using(var dim=new SolidBrush(Color.FromArgb(145,0,0,0)))e.Graphics.FillRectangle(dim,ClientRectangle);
        using(var pen=new Pen(MaterialTheme.Primary,2))using(var guidePen=new Pen(Color.FromArgb(130,MaterialTheme.Secondary),1){DashStyle=System.Drawing.Drawing2D.DashStyle.Dash})using(var accent=new SolidBrush(MaterialTheme.Secondary))using(var handleBg=new SolidBrush(MaterialTheme.Card))using(var shadow=new Pen(Color.FromArgb(140,0,0,0),4))using(var f=new Font("맑은 고딕",11,FontStyle.Bold)){
            if(selected.Width<4||selected.Height<4){
                string guide="[F8 영역 지정 중]  마우스를 드래그하여 고정 번역할 영역을 그리세요  ·  [R] 키: 영역 지정 해제(커서 기준 번역으로 전환)";
                var gs=e.Graphics.MeasureString(guide,f);
                int gx=dragScreen.X+(dragScreen.Width-(int)gs.Width-32)/2, gy=dragScreen.Y+110;
                using(var bg=new SolidBrush(MaterialTheme.Card)){e.Graphics.FillRectangle(bg,gx,gy,gs.Width+32,gs.Height+18);e.Graphics.DrawRectangle(pen,gx,gy,gs.Width+32,gs.Height+18);e.Graphics.DrawString(guide,f,Brushes.White,gx+16,gy+9);}
                return;
            }
            e.Graphics.DrawImage(background,selected,selected,GraphicsUnit.Pixel);
            var under=selected;under.Offset(1,2);e.Graphics.DrawRectangle(shadow,under);e.Graphics.DrawRectangle(pen,selected);
            int cx=selected.X+selected.Width/2, cy=selected.Y+selected.Height/2;
            e.Graphics.DrawLine(guidePen,selected.Left+8,cy,selected.Right-8,cy);
            e.Graphics.DrawLine(guidePen,cx,selected.Top+8,cx,selected.Bottom-8);
            var grip=CenterMoveGrip();
            e.Graphics.FillEllipse(handleBg,grip);e.Graphics.DrawEllipse(pen,grip);
            e.Graphics.FillEllipse(accent,cx-5,cy-5,10,10);
            foreach(var r in Handles()){e.Graphics.FillRectangle(handleBg,r.X+3,r.Y+3,14,14);e.Graphics.DrawRectangle(pen,r.X+3,r.Y+3,14,14);var dot=new Rectangle(r.X+7,r.Y+7,6,6);e.Graphics.FillRectangle(accent,dot);}
            var text="[지정 영역] "+selected.Width+" × "+selected.Height+" px  ·  [Enter] 이 영역 고정 저장  /  [R] 영역 해제(커서 기준 전환)  /  빈 곳 드래그 시 새로 그리기";
            var size=e.Graphics.MeasureString(text,f);
            int x=Math.Max(8,Math.Min(selected.X,Width-(int)size.Width-24)),y=selected.Bottom+14;if(y+size.Height+16>Height)y=Math.Max(8,selected.Top-(int)size.Height-24);
            using(var bg=new SolidBrush(MaterialTheme.Card)){e.Graphics.FillRectangle(bg,x,y,size.Width+20,size.Height+12);e.Graphics.DrawRectangle(pen,x,y,size.Width+20,size.Height+12);e.Graphics.DrawString(text,f,Brushes.White,x+10,y+6);}
        }
    }
    protected override void Dispose(bool disposing){if(disposing)background.Dispose();base.Dispose(disposing);}
}
class RangeGuideOverlay : Form {
    internal bool Pinned;
    internal int GuideOpacity=30;
    internal DateTime FlashUntil=DateTime.MinValue;
    internal DateTime CaptureFlashUntil=DateTime.MinValue;
    internal Rectangle CaptureFlashBox=Rectangle.Empty;
    internal Point CaptureFlashCursor=Point.Empty;
    internal bool CaptureFlashFixed;
    internal string CaptureFlashAnchor="center";
    Rectangle lastBounds=Rectangle.Empty;
    int lastW,lastH,lastCurX=-1,lastCurY=-1,lastOpacity=-1; bool lastFixed,lastCapturing; string lastAnchor="";
    public RangeGuideOverlay(){
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
        BackColor=Color.Black;DoubleBuffered=true;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get { var p=base.CreateParams; p.ExStyle |= 0x80000|0x20|0x8000000|0x80; return p; }
    }
    internal void ShowCaptureFlash(Rectangle capturedBox, Point cursorScreen, bool isFixed, string anchorMode, double durationSec=1.4){
        if(capturedBox.Width<40||capturedBox.Height<40)return;
        CaptureFlashBox=capturedBox;
        CaptureFlashCursor=cursorScreen;
        CaptureFlashFixed=isFixed;
        CaptureFlashAnchor=Settings.NormalizeAnchor(anchorMode);
        CaptureFlashUntil=DateTime.UtcNow.AddSeconds(durationSec);
        lastBounds=Rectangle.Empty;
        UpdateGuide(capturedBox,cursorScreen,capturedBox.Width,capturedBox.Height,isFixed,CaptureFlashAnchor,true);
    }
    internal void UpdateGuide(Rectangle box, Point cursorScreen, int rawW, int rawH, bool isFixed, string anchorMode, bool isCapturing=false){
        if(box.Width<40||box.Height<40)return;
        string normAnc=Settings.NormalizeAnchor(anchorMode);
        int pct=Math.Max(0,Math.Min(95,GuideOpacity));
        int cx=isFixed?(box.Width/2):Math.Max(10,Math.Min(box.Width-10,cursorScreen.X-box.X));
        int cy=isFixed?(box.Height/2):Math.Max(10,Math.Min(box.Height-10,cursorScreen.Y-box.Y));
        bool needRedraw=(box.Width!=lastBounds.Width||box.Height!=lastBounds.Height||rawW!=lastW||rawH!=lastH||pct!=lastOpacity||isFixed!=lastFixed||isCapturing!=lastCapturing||normAnc!=lastAnchor||Math.Abs(cx-lastCurX)>4||Math.Abs(cy-lastCurY)>4||!Visible);
        Bounds=box;
        if(!Visible)Show();
        Native.SetWindowPos(Handle,new IntPtr(-1),box.X,box.Y,box.Width,box.Height,0x10);
        if(needRedraw){
            lastW=rawW;lastH=rawH;lastOpacity=pct;lastFixed=isFixed;lastCapturing=isCapturing;lastAnchor=normAnc;lastCurX=cx;lastCurY=cy;
            Color mainBorderCol=isCapturing?MaterialTheme.Secondary:MaterialTheme.Primary;
            Color baseGlassCol=Color.FromArgb(255,22,20,38);
            byte layerAlpha=(byte)Math.Round(pct*255.0/100.0);
            using(var bmp=new Bitmap(box.Width,box.Height,PixelFormat.Format32bppArgb)){
                using(var g=Graphics.FromImage(bmp)){
                    g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using(var baseFill=new SolidBrush(baseGlassCol))
                    using(var border=new Pen(Color.FromArgb(255,mainBorderCol),isCapturing?3f:2.5f))
                    using(var cornerPen=new Pen(Color.FromArgb(255,MaterialTheme.Number),4f))
                    using(var crossPen=new Pen(Color.FromArgb(255,mainBorderCol),1.3f){DashStyle=System.Drawing.Drawing2D.DashStyle.Dash})
                    using(var badgeBg=new SolidBrush(baseGlassCol))
                    using(var f=new Font("맑은 고딕",10f,FontStyle.Bold))
                    using(var curFont=new Font("맑은 고딕",8.5f,FontStyle.Bold)){
                        var rect=new Rectangle(1,1,box.Width-3,box.Height-3);
                        g.FillRectangle(baseFill,rect);
                        g.DrawRectangle(border,rect);
                        int cl=Math.Min(32,Math.Min(box.Width,box.Height)/5);
                        g.DrawLines(cornerPen,new[]{new Point(2,2+cl),new Point(2,2),new Point(2+cl,2)});
                        g.DrawLines(cornerPen,new[]{new Point(box.Width-3-cl,2),new Point(box.Width-3,2),new Point(box.Width-3,2+cl)});
                        g.DrawLines(cornerPen,new[]{new Point(2,box.Height-3-cl),new Point(2,box.Height-3),new Point(2+cl,box.Height-3)});
                        g.DrawLines(cornerPen,new[]{new Point(box.Width-3-cl,box.Height-3),new Point(box.Width-3,box.Height-3),new Point(box.Width-3,box.Height-3-cl)});
                        g.DrawLine(crossPen,10,cy,box.Width-10,cy);
                        g.DrawLine(crossPen,cx,10,cx,box.Height-10);
                        using(var cb=new SolidBrush(MaterialTheme.Number))g.FillEllipse(cb,cx-5,cy-5,10,10);
                        using(var cp=new Pen(MaterialTheme.Surface,1.5f))g.DrawEllipse(cp,cx-5,cy-5,10,10);
                        if(!isFixed){
                            string cTag=isCapturing?"캡처 시점 커서":"커서 기준점";
                            var cts=g.MeasureString(cTag,curFont);
                            float ctx=Math.Min(box.Width-cts.Width-14,Math.Max(8,cx+10));
                            float cty=Math.Min(box.Height-cts.Height-12,Math.Max(44,cy+8));
                            g.FillRectangle(badgeBg,ctx,cty,cts.Width+10,cts.Height+4);
                            g.DrawString(cTag,curFont,new SolidBrush(MaterialTheme.Number),ctx+5,cty+2);
                        }
                        string ancTitle=isFixed?"지정 영역":Settings.AnchorLabel(normAnc);
                        string label=isCapturing
                            ? ("[F8 캡처 영역]  "+box.Width+" × "+box.Height+" px  ·  "+ancTitle+" 번역 중")
                            : ("[F8 "+ancTitle+" 가이드]  "+rawW+" × "+rawH+" px");
                        var ts=g.MeasureString(label,f);
                        float bx=Math.Max(8,(box.Width-ts.Width-24)/2f), by=10;
                        g.FillRectangle(badgeBg,bx,by,ts.Width+24,ts.Height+10);
                        g.DrawRectangle(border,bx,by,ts.Width+24,ts.Height+10);
                        g.DrawString(label,f,isCapturing?new SolidBrush(MaterialTheme.Number):Brushes.White,bx+12,by+5);
                    }
                }
                ApplyFrame(bmp,layerAlpha);
            }
        }
        lastBounds=box;
    }
    void ApplyFrame(Bitmap frame, byte alpha){
        IntPtr dc=Native.GetDC(IntPtr.Zero),memory=IntPtr.Zero,bitmap=IntPtr.Zero,previous=IntPtr.Zero;
        try{
            memory=Native.CreateCompatibleDC(dc);bitmap=frame.GetHbitmap(Color.FromArgb(0));previous=Native.SelectObject(memory,bitmap);
            var position=new Native.POINT(Left,Top);var size=new Native.SIZE(frame.Width,frame.Height);var source=new Native.POINT(0,0);
            var blend=new Native.BLEND{op=0,flags=0,alpha=alpha,format=1};
            Native.UpdateLayeredWindow(Handle,dc,ref position,ref size,memory,ref source,0,ref blend,2);
        }finally{
            if(previous!=IntPtr.Zero)Native.SelectObject(memory,previous);
            if(bitmap!=IntPtr.Zero)Native.DeleteObject(bitmap);
            if(memory!=IntPtr.Zero)Native.DeleteDC(memory);
            if(dc!=IntPtr.Zero)Native.ReleaseDC(IntPtr.Zero,dc);
        }
    }
}
class ProgressHudOverlay : Form {
    internal DateTime ToastUntil=DateTime.MinValue;
    int animStep;
    string lastStaticText="";
    Rectangle lastStaticBox=Rectangle.Empty;
    const int HudW=550, HudH=56;
    public ProgressHudOverlay(){
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
        BackColor=Color.Black;DoubleBuffered=true;Size=new Size(HudW,HudH);
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get { var p=base.CreateParams; p.ExStyle |= 0x80000|0x20|0x8000000|0x80; return p; }
    }
    internal void ShowToast(Rectangle mon, string position, string msg, bool warn=false, double seconds=1.6){
        if(position=="off"){if(Visible)Hide();return;}
        ToastUntil=DateTime.UtcNow.AddSeconds(seconds);
        lastStaticText="";
        RenderHud(mon,position,msg,warn,false,0);
    }
    internal void RenderFixedModeBanner(Rectangle mon, string position, int w, int h){
        if(position=="off"){if(Visible)Hide();return;}
        string text="◈ [F8 영역 지정 중: "+w+"×"+h+" px]   F8 지정영역 번역  ·  해제(커서 기준): Ctrl+Alt+F9";
        int x=position=="bottom-right"?(mon.Right-HudW-28):(mon.X+(mon.Width-HudW)/2);
        int y=position=="bottom-right"?(mon.Bottom-HudH-58):(mon.Y+28);
        var box=new Rectangle(x,y,HudW,HudH);
        if(Visible && text==lastStaticText && box==lastStaticBox)return;
        lastStaticText=text;
        lastStaticBox=box;
        RenderHud(mon,position,text,false,false,0);
    }
    internal void RenderProgress(Rectangle mon, string position, bool near, bool isFixed, string langCode, double elapsedSec){
        if(position=="off"){if(Visible)Hide();return;}
        ToastUntil=DateTime.MinValue;
        lastStaticText="";
        animStep=(animStep+1)%12;
        string langTag=langCode=="ja"?"JA":langCode=="zh"?"ZH":"EN";
        string modeTag=near?(isFixed?"지정영역":"커서기준"):"F9 전체";
        string text="달빛렌즈 번역 중…  ["+modeTag+" · "+langTag+"]   "+elapsedSec.ToString("0.0")+"초   (F10 취소)";
        RenderHud(mon,position,text,false,true,animStep);
    }
    void RenderHud(Rectangle mon, string position, string text, bool warn, bool spinning, int step){
        int x=position=="bottom-right"?(mon.Right-HudW-28):(mon.X+(mon.Width-HudW)/2);
        int y=position=="bottom-right"?(mon.Bottom-HudH-58):(mon.Y+28);
        var box=new Rectangle(x,y,HudW,HudH);
        Bounds=box;
        if(!Visible)Show();
        Native.SetWindowPos(Handle,new IntPtr(-1),box.X,box.Y,box.Width,box.Height,0x10);
        using(var bmp=new Bitmap(HudW,HudH,PixelFormat.Format32bppArgb)){
            using(var g=Graphics.FromImage(bmp)){
                g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var pillRect=new Rectangle(2,2,HudW-5,HudH-5);
                Color accentColor=warn?Color.FromArgb(255,190,60):(spinning?MaterialTheme.Primary:MaterialTheme.Secondary);
                using(var path=MaterialTheme.Shape(pillRect,24))
                using(var bg=new SolidBrush(Color.FromArgb(236,22,20,38)))
                using(var pen=new Pen(Color.FromArgb(235,accentColor),2f))
                using(var f=new Font("맑은 고딕",9.5f,FontStyle.Bold)){
                    g.FillPath(bg,path);
                    g.DrawPath(pen,path);
                    int cx=26, cy=HudH/2;
                    if(spinning){
                        using(var trackPen=new Pen(Color.FromArgb(65,MaterialTheme.Secondary),3f))
                        using(var arcPen=new Pen(MaterialTheme.Number,3.2f)){
                            g.DrawEllipse(trackPen,cx-10,cy-10,20,20);
                            g.DrawArc(arcPen,cx-10,cy-10,20,20,(step*30)%360,115);
                        }
                        int barW=100;
                        int barTravel=HudW-56-barW;
                        double phase=(step%12)/11.0;
                        int bx=28+(int)Math.Round(Math.Sin(phase*Math.PI)*barTravel);
                        using(var barBrush=new SolidBrush(Color.FromArgb(190,MaterialTheme.Secondary)))
                            g.FillRectangle(barBrush,bx,HudH-7,barW,3);
                    }else{
                        using(var dotB=new SolidBrush(accentColor))
                            g.FillEllipse(dotB,cx-6,cy-6,12,12);
                    }
                    var textRect=new RectangleF(46,4,HudW-58,HudH-8);
                    var sf=new StringFormat{Alignment=StringAlignment.Near,LineAlignment=StringAlignment.Center,FormatFlags=StringFormatFlags.NoWrap};
                    g.DrawString(text,f,Brushes.White,textRect,sf);
                }
            }
            ApplyFrame(bmp);
        }
    }
    void ApplyFrame(Bitmap frame){
        IntPtr dc=Native.GetDC(IntPtr.Zero),memory=IntPtr.Zero,bitmap=IntPtr.Zero,previous=IntPtr.Zero;
        try{
            memory=Native.CreateCompatibleDC(dc);bitmap=frame.GetHbitmap(Color.FromArgb(0));previous=Native.SelectObject(memory,bitmap);
            var position=new Native.POINT(Left,Top);var size=new Native.SIZE(frame.Width,frame.Height);var source=new Native.POINT(0,0);
            var blend=new Native.BLEND{op=0,flags=0,alpha=255,format=1};
            Native.UpdateLayeredWindow(Handle,dc,ref position,ref size,memory,ref source,0,ref blend,2);
        }finally{
            if(previous!=IntPtr.Zero)Native.SelectObject(memory,previous);
            if(bitmap!=IntPtr.Zero)Native.DeleteObject(bitmap);
            if(memory!=IntPtr.Zero)Native.DeleteDC(memory);
            if(dc!=IntPtr.Zero)Native.ReleaseDC(IntPtr.Zero,dc);
        }
    }
}
class NearRangePreviewControl : Control {
    internal int RangeWidth=1280, RangeHeight=860, MonitorWidth=1920, MonitorHeight=1080;
    internal bool IsFixed;
    internal double FixedRegionX, FixedRegionY, FixedRegionW, FixedRegionH;
    internal string AnchorMode="center";
    internal event Action<int,int> RangeChanged;
    int dragHandle=-1; Point dragStart; int startW,startH;
    public NearRangePreviewControl(){
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        Height=200;Dock=DockStyle.Top;BackColor=MaterialTheme.Field;Cursor=Cursors.SizeAll;
        Margin=new Padding(0,4,0,12);
    }
    Rectangle GetMonitorCanvas(){
        int padX=18, padY=14;
        int availW=Math.Max(120,Width-padX*2), availH=Math.Max(80,Height-padY*2);
        double scale=Math.Min((double)availW/Math.Max(1,MonitorWidth),(double)availH/Math.Max(1,MonitorHeight));
        int mw=(int)Math.Round(MonitorWidth*scale), mh=(int)Math.Round(MonitorHeight*scale);
        return new Rectangle((Width-mw)/2,(Height-mh)/2,mw,mh);
    }
    Point GetCursorOnCanvas(Rectangle mon){
        string a=Settings.NormalizeAnchor(AnchorMode);
        int cx=mon.X+mon.Width/2, cy=mon.Y+mon.Height/2;
        if(a=="right")cx=mon.X+mon.Width*26/100;
        else if(a=="left")cx=mon.X+mon.Width*74/100;
        else if(a=="top")cy=mon.Y+mon.Height*74/100;
        else if(a=="bottom")cy=mon.Y+mon.Height*26/100;
        return new Point(cx,cy);
    }
    Rectangle GetBoxOnCanvas(Rectangle mon){
        if(IsFixed && FixedRegionW>0 && FixedRegionH>0){
            int fx=mon.X+(int)Math.Round(FixedRegionX*mon.Width);
            int fy=mon.Y+(int)Math.Round(FixedRegionY*mon.Height);
            int fw=Math.Max(18,Math.Min(mon.Width,(int)Math.Round(FixedRegionW*mon.Width)));
            int fh=Math.Max(14,Math.Min(mon.Height,(int)Math.Round(FixedRegionH*mon.Height)));
            fx=Math.Max(mon.Left,Math.Min(fx,mon.Right-fw));
            fy=Math.Max(mon.Top,Math.Min(fy,mon.Bottom-fh));
            return new Rectangle(fx,fy,fw,fh);
        }
        double sx=(double)mon.Width/Math.Max(1,MonitorWidth);
        double sy=(double)mon.Height/Math.Max(1,MonitorHeight);
        int bw=Math.Max(18,Math.Min(mon.Width,(int)Math.Round(RangeWidth*sx)));
        int bh=Math.Max(14,Math.Min(mon.Height,(int)Math.Round(RangeHeight*sy)));
        Point cur=GetCursorOnCanvas(mon);
        string a=Settings.NormalizeAnchor(AnchorMode);
        int padX=Math.Min(12,Math.Max(4,bw/10));
        int padY=Math.Min(10,Math.Max(4,bh/10));
        int bx=cur.X-bw/2, by=cur.Y-bh/2;
        if(a=="right")bx=cur.X-padX;
        else if(a=="left")bx=cur.X-bw+padX;
        else if(a=="top")by=cur.Y-bh+padY;
        else if(a=="bottom")by=cur.Y-padY;
        bx=Math.Max(mon.Left,Math.Min(bx,mon.Right-bw));
        by=Math.Max(mon.Top,Math.Min(by,mon.Bottom-bh));
        return new Rectangle(bx,by,bw,bh);
    }
    Rectangle[] GetHandles(Rectangle box){
        int x=box.X,y=box.Y,r=box.Right,b=box.Bottom,cx=x+box.Width/2,cy=y+box.Height/2;
        return new[]{new Point(x,y),new Point(cx,y),new Point(r,y),new Point(r,cy),new Point(r,b),new Point(cx,b),new Point(x,b),new Point(x,cy)}.Select(p=>new Rectangle(p.X-6,p.Y-6,12,12)).ToArray();
    }
    int HitHandle(Point p){
        var mon=GetMonitorCanvas();var box=GetBoxOnCanvas(mon);var hs=GetHandles(box);
        for(int i=0;i<hs.Length;i++)if(hs[i].Contains(p))return i;
        if(box.Contains(p))return 8;
        return -1;
    }
    protected override void OnMouseDown(MouseEventArgs e){
        base.OnMouseDown(e);Focus();
        if(e.Button!=MouseButtons.Left)return;
        dragHandle=HitHandle(e.Location);
        if(dragHandle>=0){dragStart=e.Location;startW=RangeWidth;startH=RangeHeight;Capture=true;}
    }
    protected override void OnMouseMove(MouseEventArgs e){
        base.OnMouseMove(e);
        var mon=GetMonitorCanvas();
        if(dragHandle<0){
            int h=HitHandle(e.Location);
            Cursor=(h==0||h==4)?Cursors.SizeNWSE:(h==2||h==6)?Cursors.SizeNESW:(h==1||h==5)?Cursors.SizeNS:(h==3||h==7)?Cursors.SizeWE:h==8?Cursors.Hand:Cursors.Default;
            return;
        }
        double scaleX=(double)MonitorWidth/Math.Max(1,mon.Width);
        double scaleY=(double)MonitorHeight/Math.Max(1,mon.Height);
        int dx=(int)Math.Round((e.X-dragStart.X)*scaleX*2);
        int dy=(int)Math.Round((e.Y-dragStart.Y)*scaleY*2);
        int nw=startW, nh=startH;
        if(dragHandle==0||dragHandle==6||dragHandle==7)nw=startW-dx;
        else if(dragHandle==2||dragHandle==3||dragHandle==4)nw=startW+dx;
        else if(dragHandle==8)nw=startW+dx/2;
        if(dragHandle==0||dragHandle==1||dragHandle==2)nh=startH-dy;
        else if(dragHandle==4||dragHandle==5||dragHandle==6)nh=startH+dy;
        else if(dragHandle==8)nh=startH+dy/2;
        SetRangeClamped(nw,nh);
    }
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragHandle=-1;Capture=false;}
    protected override void OnMouseWheel(MouseEventArgs e){
        if((ModifierKeys&Keys.Control)==0){base.OnMouseWheel(e);return;}
        int stepW=e.Delta>0?80:-80;
        int stepH=e.Delta>0?54:-54;
        SetRangeClamped(RangeWidth+stepW,RangeHeight+stepH);
        var h=e as HandledMouseEventArgs;if(h!=null)h.Handled=true;
    }
    void SetRangeClamped(int w, int h){
        int nw=Math.Max(48,Math.Min(MonitorWidth,((w+10)/20)*20));
        int nh=Math.Max(48,Math.Min(MonitorHeight,((h+10)/20)*20));
        if(nw==RangeWidth&&nh==RangeHeight)return;
        RangeWidth=nw;RangeHeight=nh;Invalidate();
        if(RangeChanged!=null)RangeChanged(nw,nh);
    }
    protected override void OnPaint(PaintEventArgs e){
        var g=e.Graphics;g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(BackColor);
        var mon=GetMonitorCanvas();
        using(var monBg=new SolidBrush(Color.FromArgb(16,14,28)))
        using(var monBorder=new Pen(MaterialTheme.Outline,1.5f))
        using(var gridPen=new Pen(Color.FromArgb(30,255,255,255),1f)){
            g.FillRectangle(monBg,mon);
            for(int i=1;i<4;i++){
                int gx=mon.X+mon.Width*i/4, gy=mon.Y+mon.Height*i/4;
                g.DrawLine(gridPen,gx,mon.Top,gx,mon.Bottom);
                g.DrawLine(gridPen,mon.Left,gy,mon.Right,gy);
            }
            g.DrawRectangle(monBorder,mon);
        }
        var box=GetBoxOnCanvas(mon);
        Point cur=IsFixed ? new Point(box.X+box.Width/2,box.Y+box.Height/2) : GetCursorOnCanvas(mon);
        int cx=Math.Max(box.Left+6,Math.Min(box.Right-6,cur.X));
        int cy=Math.Max(box.Top+6,Math.Min(box.Bottom-6,cur.Y));
        using(var boxFill=new SolidBrush(IsFixed?Color.FromArgb(64,255,205,40):Color.FromArgb(52,179,115,255)))
        using(var boxPen=new Pen(IsFixed?MaterialTheme.Number:MaterialTheme.Primary,2f))
        using(var crossPen=new Pen(Color.FromArgb(165,MaterialTheme.Secondary),1.2f){DashStyle=System.Drawing.Drawing2D.DashStyle.Dot})
        using(var handleBrush=new SolidBrush(IsFixed?MaterialTheme.Number:MaterialTheme.Secondary))
        using(var fBold=new Font("맑은 고딕",9f,FontStyle.Bold))
        using(var fSmall=new Font("맑은 고딕",8f,FontStyle.Bold)){
            g.FillRectangle(boxFill,box);
            g.DrawRectangle(boxPen,box);
            g.DrawLine(crossPen,box.Left+4,cy,box.Right-4,cy);
            g.DrawLine(crossPen,cx,box.Top+4,cx,box.Bottom-4);
            using(var centerDot=new SolidBrush(MaterialTheme.Number))g.FillEllipse(centerDot,cx-5,cy-5,10,10);
            using(var ringPen=new Pen(MaterialTheme.Surface,1.5f))g.DrawEllipse(ringPen,cx-5,cy-5,10,10);
            g.DrawString(IsFixed?"고정":"커서",fSmall,new SolidBrush(MaterialTheme.Number),Math.Min(mon.Right-30,cx+6),Math.Max(mon.Top+22,cy-14));
            foreach(var h in GetHandles(box)){
                g.FillRectangle(handleBrush,h.X+2,h.Y+2,8,8);
            }
            int pctW=(int)Math.Round(RangeWidth*100.0/Math.Max(1,MonitorWidth));
            int pctH=(int)Math.Round(RangeHeight*100.0/Math.Max(1,MonitorHeight));
            string topInfo=(IsFixed?"◈ [영역 지정 중] ":"◎ [커서 기준 모드] ")+"모니터 ("+MonitorWidth+"×"+MonitorHeight+") 대비 F8 범위: "+RangeWidth+" × "+RangeHeight+" px ("+pctW+"% × "+pctH+"%)";
            string subInfo=IsFixed
                ? "지정 영역 고정 활성  ·  [영역 지정 해제 (Ctrl+Alt+F9)] 또는 방향 버튼 클릭 시 커서 기준 번역으로 전환"
                : "배치: ["+Settings.AnchorLabel(AnchorMode)+"]  ·  박스를 드래그하거나 아래 버튼으로 방향/크기를 조절하세요";
            g.DrawString(topInfo,fBold,IsFixed?new SolidBrush(MaterialTheme.Number):Brushes.White,mon.X+8,mon.Y+6);
            g.DrawString(subInfo,fSmall,new SolidBrush(MaterialTheme.Secondary),mon.X+8,mon.Bottom-20);
        }
    }
}
public class Block {
    public string text, translated;
    public double x,y,w,h;
}
public class Result { public Block[] blocks; public string error; public int count,cacheHits;public double ocrMs,totalMs; }
class Card { public Rectangle bounds, source; public string text; public int number; }

static class CardLayout {
    internal const int Gap=12;
    internal static List<Card> Arrange(Size screen, Block[] blocks, Rectangle capture, Point origin, Graphics g, Font font, out int hidden, int start=0) {
        var cards=new List<Card>(); hidden=0;
        var sources=blocks.Select(b=>new Rectangle(capture.X+(int)b.x-origin.X,capture.Y+(int)b.y-origin.Y,Math.Max(1,(int)Math.Ceiling(b.w)),Math.Max(1,(int)Math.Ceiling(b.h)))).ToArray();
        // Include the source badges, borders and a clear gap around every OCR block.
        var obstacles=sources.Select(s=>Rectangle.Inflate(new Rectangle(s.X,s.Y,Math.Max(28,s.Width),Math.Max(20,s.Height)),Gap,Gap)).ToArray();
        var safe=new Rectangle(Gap,Gap,screen.Width-Gap*2,screen.Height-72);
        for(int offset=0;offset<blocks.Length;offset++) {
            int i=(offset+start)%blocks.Length;
            string text=(i+1)+". "+blocks[i].translated;
            Rectangle best=Rectangle.Empty; double bestScore=double.MaxValue;
            foreach(int desiredWidth in new[]{340,430,520,240,220}) {
                int width=Math.Min(desiredWidth,safe.Width);
                // Use the same Graphics, font and text width as DrawString; reserve extra descent space.
                int height=Math.Max(48,(int)Math.Ceiling(g.MeasureString(text,font,width-28).Height)+30);
                if(height>safe.Height)continue;
                var xs=new List<int>{safe.Left,safe.Right-width,sources[i].Right+Gap*2,sources[i].Left-width-Gap*2};
                var ys=new List<int>{safe.Top,safe.Bottom-height,sources[i].Top};
                foreach(var obstacle in obstacles.Concat(cards.Select(c=>Rectangle.Inflate(c.bounds,Gap,Gap)))) {
                    xs.Add(obstacle.Right);xs.Add(obstacle.Left-width);
                    ys.Add(obstacle.Bottom);ys.Add(obstacle.Top-height);
                }
                foreach(int x in xs.Distinct())foreach(int y in ys.Distinct()) {
                    var box=new Rectangle(x,y,width,height);
                    if(!safe.Contains(box) || obstacles.Any(o=>o.IntersectsWith(box)) || cards.Any(c=>Rectangle.Inflate(c.bounds,Gap,Gap).IntersectsWith(box)))continue;
                    double dx=box.X+box.Width/2.0-(sources[i].X+sources[i].Width/2.0);
                    double dy=box.Y+box.Height/2.0-(sources[i].Y+sources[i].Height/2.0);
                    double score=dx*dx+dy*dy+width*height*.1+Math.Max(0,340-width)*2000;
                    if(score<bestScore){best=box;bestScore=score;}
                }
            }
            if(best.IsEmpty){hidden++;continue;}
            cards.Add(new Card{bounds=best,source=sources[i],text=text,number=i+1});
        }
        return cards;
    }
}

class Overlay : Form {
    internal ObsOverlayServer Obs;
    internal List<Card> cards = new List<Card>();
    internal int FontSize=13;
    internal bool TooltipLine=true;
    internal int LineOpacity=73, BackgroundOpacity=100, ObsBackgroundOpacity=48;
    internal void ConfigureStyle(Settings settings){
        TooltipLine=settings.tooltipLine;LineOpacity=settings.tooltipLineOpacity;
        BackgroundOpacity=settings.tooltipOpacity;ObsBackgroundOpacity=settings.obsTooltipOpacity;
        FontSize=settings.fontSize;
        if(Visible && pageBlocks!=null)RenderPage();
    }
    static int Alpha(int percent){return (int)Math.Round(Math.Max(0,Math.Min(100,percent))*255.0/100);}
    internal string Notice="";
    Block[] pageBlocks; Rectangle pageScreen,pageCapture; int pageStart;
    public Overlay() {
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true;
        BackColor=Color.Black; DoubleBuffered=true;
        VisibleChanged+=(s,e)=>{if(!Visible && Obs!=null)Obs.Clear();};
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get { var p=base.CreateParams; p.ExStyle |= 0x80000|0x20|0x8000000|0x80; return p; }
    }
    internal void Present(Rectangle screen, Block[] blocks, Rectangle capture, Point cursor, bool near) {
        pageBlocks=blocks;pageScreen=screen;pageCapture=capture;pageStart=0;
        RenderPage();
    }
    internal void NextPage() {
        if(pageBlocks==null || cards.Count==0)return;
        pageStart=cards[cards.Count-1].number%pageBlocks.Length;
        RenderPage();
    }
    internal void RenderPage() {
        var screen=pageScreen;var blocks=pageBlocks;var capture=pageCapture;
        Bounds=screen; cards.Clear(); Notice="";
        using(var g=CreateGraphics()) using(var font=new Font("맑은 고딕",FontSize)) {
            int hidden;
            cards=CardLayout.Arrange(screen.Size,blocks,capture,screen.Location,g,font,out hidden,pageStart);
            if(hidden>0)Notice="나머지 "+hidden+"개: F7 다음 묶음 · 전체 내용은 번역 기록";
        }
        Show(); Native.SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x13);
        using(var local=new Bitmap(Width,Height,PixelFormat.Format32bppArgb)){
            using(var live=CreateGraphics())local.SetResolution(live.DpiX,live.DpiY);
            using(var g=Graphics.FromImage(local)){DrawContent(g);DrawHint(g);}
            ApplyAlphaFrame(local);
        }
        if(Obs!=null) {
            // Paint exactly the same cards, then scale the whole 4K coordinate space to 1080p.
            using(var desktop=new Bitmap(Width,Height)) {
                using(var live=CreateGraphics())desktop.SetResolution(live.DpiX,live.DpiY);
                using(var g=Graphics.FromImage(desktop))DrawContent(g,true);
                using(var output=new Bitmap(1920,1080))using(var og=Graphics.FromImage(output))using(var m=new MemoryStream()) {
                    og.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    og.DrawImage(desktop,new Rectangle(0,0,1920,1080),new Rectangle(0,0,Width,Height),GraphicsUnit.Pixel);
                    output.Save(m,ImageFormat.Png);Obs.SetFrame(m.ToArray());
                }
            }
        }
    }
    protected override void OnPaint(PaintEventArgs e) {
        // Pixels are supplied through UpdateLayeredWindow, preserving per-pixel alpha.
    }
    internal void ApplyAlphaFrame(Bitmap frame){
        IntPtr dc=Native.GetDC(IntPtr.Zero),memory=IntPtr.Zero,bitmap=IntPtr.Zero,previous=IntPtr.Zero;
        try{memory=Native.CreateCompatibleDC(dc);bitmap=frame.GetHbitmap(Color.FromArgb(0));previous=Native.SelectObject(memory,bitmap);var position=new Native.POINT(Left,Top);var size=new Native.SIZE(frame.Width,frame.Height);var source=new Native.POINT(0,0);var blend=new Native.BLEND{op=0,flags=0,alpha=255,format=1};
            if(!Native.UpdateLayeredWindow(Handle,dc,ref position,ref size,memory,ref source,0,ref blend,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }finally{if(previous!=IntPtr.Zero)Native.SelectObject(memory,previous);if(bitmap!=IntPtr.Zero)Native.DeleteObject(bitmap);if(memory!=IntPtr.Zero)Native.DeleteDC(memory);if(dc!=IntPtr.Zero)Native.ReleaseDC(IntPtr.Zero,dc);}
    }
    void DrawHint(Graphics graphics){
        using(var small=new Font("맑은 고딕",10))using(var bg=new SolidBrush(Color.FromArgb(23,29,43))){
            string tip="DalvitLens  ·  F7 다음  /  F8 주변  /  F9 전체  /  F10 숨김"+(Notice.Length>0 ? "  ·  "+Notice : "");
            var size=graphics.MeasureString(tip,small,Width-40);
            graphics.FillRectangle(bg,12,Height-size.Height-20,Math.Min(Width-24,size.Width+20),size.Height+12);
            graphics.DrawString(tip,small,Brushes.White,22,Height-size.Height-14);
        }
    }
    internal void DrawContent(Graphics graphics,bool broadcast=false) {
        graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using(var font=new Font("맑은 고딕",FontSize)) using(var small=new Font("맑은 고딕",10))
        using(var bg=new SolidBrush(Color.FromArgb(Alpha(broadcast?ObsBackgroundOpacity:BackgroundOpacity),22,20,38))) using(var pen=new Pen(Color.FromArgb(Alpha(LineOpacity),MaterialTheme.Primary),1)) using(var secondary=new Pen(Color.FromArgb(64,MaterialTheme.Secondary),1)) using(var badge=new SolidBrush(MaterialTheme.Number)) {
            // Source markers first. No crossing connector lines: match by number.
            foreach(var c in cards) {
                graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.None;
                graphics.DrawRectangle(secondary,c.source);
                graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.FillRectangle(bg,c.source.X,c.source.Y,28,20);
                graphics.DrawString(c.number.ToString(),small,badge,c.source.Location);
            }
            // Render all soft shadows behind cards; keep their reach within layout clearance.
            foreach(var c in cards)for(int spread=6;spread>=1;spread--){var r=c.bounds;r.Offset(0,3);r.Inflate(spread,spread);using(var shape=MaterialTheme.Shape(r,14+spread))using(var shade=new SolidBrush(Color.FromArgb(7,0,0,0)))graphics.FillPath(shade,shape);}
            foreach(var c in cards) {
                using(var shape=MaterialTheme.Shape(c.bounds,14)){graphics.FillPath(bg,shape);if(TooltipLine)graphics.DrawPath(pen,shape);}
                graphics.DrawString(c.text,font,Brushes.White,new RectangleF(c.bounds.X+14,c.bounds.Y+12,c.bounds.Width-28,c.bounds.Height-24));
            }
        }
    }
}

static class MaterialTheme {
    // DALBIT signature colors: keep these exact; tonal surfaces are derived separately.
    internal static readonly Color Primary=Color.FromArgb(179,115,255),Secondary=Color.FromArgb(90,191,255),Number=Color.FromArgb(255,230,0),Card=Color.FromArgb(22,20,38);
    internal static readonly Color Surface=Color.FromArgb(14,12,25),Field=Color.FromArgb(34,30,52),Text=Color.FromArgb(241,235,252),Muted=Color.FromArgb(183,175,199),Outline=Color.FromArgb(69,59,90);
    internal static System.Drawing.Drawing2D.GraphicsPath Shape(Rectangle r,int radius){
        var p=new System.Drawing.Drawing2D.GraphicsPath();int d=Math.Max(2,Math.Min(radius*2,Math.Min(r.Width,r.Height)));
        p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
    internal static Label Label(string text,float size,bool heading=false){return new Label{Text=text,AutoSize=true,Dock=DockStyle.Top,Font=new Font("맑은 고딕",size,heading?FontStyle.Bold:FontStyle.Regular),ForeColor=heading?Text:Muted,BackColor=Color.Transparent,Margin=new Padding(0,0,0,8)};}
    internal static MaterialCard CardPanel(){return new MaterialCard{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,Margin=new Padding(0,0,0,16)};}
}
class MaterialCard : TableLayoutPanel {
    public MaterialCard(){DoubleBuffered=true;BackColor=MaterialTheme.Card;ForeColor=MaterialTheme.Text;Padding=new Padding(22);ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));}
    protected override void OnControlAdded(ControlEventArgs e){base.OnControlAdded(e);while(RowStyles.Count<Controls.Count)RowStyles.Add(new RowStyle(SizeType.AutoSize));}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);if(Width>0&&Height>0)using(var p=MaterialTheme.Shape(new Rectangle(0,0,Width,Height),20)){var old=Region;Region=new Region(p);if(old!=null)old.Dispose();}}
}
class MaterialButton : Button {
    internal bool Filled;
    public MaterialButton(){FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;Padding=new Padding(22,11,22,11);Margin=new Padding(8,0,0,0);Cursor=Cursors.Hand;UseVisualStyleBackColor=false;Font=new Font("맑은 고딕",10,FontStyle.Bold);}
    protected override void OnPaint(PaintEventArgs e){
        e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent==null?MaterialTheme.Surface:Parent.BackColor);
        using(var p=MaterialTheme.Shape(new Rectangle(1,1,Width-3,Height-3),24))using(var b=new SolidBrush(Filled?MaterialTheme.Primary:MaterialTheme.Field))using(var pen=new Pen(Focused?MaterialTheme.Primary:MaterialTheme.Outline,Focused?2:1)){e.Graphics.FillPath(b,p);if(!Filled)e.Graphics.DrawPath(pen,p);}
        TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Filled?MaterialTheme.Surface:MaterialTheme.Text,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
    }
}
class SlimScrollBar : Control {
    internal int Total=1,View=1,Value; int dragY=-1,dragStart; bool hover;
    internal event Action<int> Moved;
    public SlimScrollBar(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);Width=12;Cursor=Cursors.Hand;AccessibleRole=AccessibleRole.ScrollBar;AccessibleName="콘텐츠 스크롤";TabStop=false;}
    Rectangle Thumb {get{int h=Math.Min(Height,Math.Max(36,(int)((double)Height*View/Math.Max(1,Total))));int y=(int)((Height-h)*(double)Value/Math.Max(1,Total-View));return new Rectangle(3,y,6,Math.Max(1,h));}}
    internal void SetRange(int total,int view,int value){Total=Math.Max(1,total);View=Math.Max(1,view);Value=Math.Max(0,Math.Min(value,Total-View));Enabled=Total>View;Invalidate();}
    void MoveTo(int value){Value=Math.Max(0,Math.Min(value,Total-View));if(Moved!=null)Moved(Value);Invalidate();}
    protected override void OnPaint(PaintEventArgs e){e.Graphics.Clear(BackColor);if(!Enabled||Height<1)return;e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;using(var path=MaterialTheme.Shape(Thumb,3))using(var brush=new SolidBrush(hover||dragY>=0?MaterialTheme.Primary:Color.FromArgb(113,93,145)))e.Graphics.FillPath(brush,path);}
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(!Enabled)return;if(!Thumb.Contains(e.Location))MoveTo((int)((e.Y-Thumb.Height/2.0)/Math.Max(1,Height-Thumb.Height)*(Total-View)));dragY=e.Y;dragStart=Value;Capture=true;Invalidate();}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragY>=0)MoveTo(dragStart+(int)((e.Y-dragY)*(double)(Total-View)/Math.Max(1,Height-Thumb.Height)));}
    protected override void OnMouseUp(MouseEventArgs e){dragY=-1;Capture=false;Invalidate();base.OnMouseUp(e);}
}
class ThemedScrollHost : UserControl {
    readonly Panel viewport=new Panel(); readonly SlimScrollBar bar=new SlimScrollBar();Control content;int offset;bool updating;
    public ThemedScrollHost(){BackColor=MaterialTheme.Surface;viewport.Dock=DockStyle.Fill;viewport.BackColor=BackColor;bar.Dock=DockStyle.Right;bar.BackColor=BackColor;Controls.Add(viewport);Controls.Add(bar);bar.Moved+=SetOffset;viewport.SizeChanged+=(s,e)=>RefreshExtent();MouseWheel+=Wheel;viewport.MouseWheel+=Wheel;}
    internal void SetContent(Control value){content=value;content.Dock=DockStyle.None;content.AutoSize=false;viewport.Controls.Add(content);content.Layout+=(s,e)=>RefreshExtent();WireWheel(content);RefreshExtent();}
    void WireWheel(Control control){control.MouseWheel+=Wheel;foreach(Control child in control.Controls)WireWheel(child);}
    void Wheel(object sender,MouseEventArgs e){SetOffset(offset-(int)(e.Delta/120.0*72));var handled=e as HandledMouseEventArgs;if(handled!=null)handled.Handled=true;}
    void SetOffset(int value){if(content==null)return;offset=Math.Max(0,Math.Min(value,Math.Max(0,content.Height-viewport.Height)));content.Location=new Point(0,-offset);bar.SetRange(content.Height,viewport.Height,offset);}
    internal void RefreshExtent(){if(content==null||updating||viewport.Width<1)return;updating=true;try{content.Width=viewport.Width;content.Height=Math.Max(1,content.GetPreferredSize(new Size(viewport.Width,0)).Height);SetOffset(offset);}finally{updating=false;}}
    protected override void OnBackColorChanged(EventArgs e){base.OnBackColorChanged(e);if(viewport!=null)viewport.BackColor=BackColor;if(bar!=null)bar.BackColor=BackColor;}
}
class HistoryPanel : ThemedScrollHost {
    readonly Label textBody=new Label();string value="";
    public HistoryPanel(){textBody.AutoSize=false;textBody.Padding=new Padding(14);textBody.ForeColor=MaterialTheme.Text;textBody.BackColor=MaterialTheme.Field;textBody.Font=new Font("맑은 고딕",10);SetContent(textBody);}
    public override string Text {get{return value;}set{this.value=value??"";if(textBody!=null){textBody.Text=this.value;RefreshExtent();}}}
}
class MainForm : Form {
    readonly string root=AppDomain.CurrentDomain.BaseDirectory;
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    Settings settings;
    Overlay overlay=new Overlay();
    RangeGuideOverlay rangeGuide=new RangeGuideOverlay();
    ProgressHudOverlay progressHud=new ProgressHudOverlay();
    NearRangePreviewControl rangePreview;
    NumericUpDown nearWidthInput,nearHeightInput,guideOpacityInput;
    MaterialButton presetSmallBtn,presetStdBtn,presetLargeBtn,presetXLargeBtn,toggleGuideBtn,releaseRegionBtn;
    MaterialButton anchorCenterBtn,anchorRightBtn,anchorLeftBtn,anchorTopBtn,anchorBottomBtn;
    MaterialButton hudTopBtn,hudBottomRightBtn,hudOffBtn;
    ToolStripMenuItem trayHudTop,trayHudBottomRight,trayHudOff,trayCaptureFlash;
    bool syncingRangeControls,captureNearFlag;
    ComboBox backend=new ComboBox(); TextBox key=new TextBox();
    NumericUpDown font=new NumericUpDown(); Label status=new Label();
    CheckBox tooltipLine;NumericUpDown lineOpacity,tooltipOpacity,obsTooltipOpacity;
    HistoryPanel history=new HistoryPanel(); NotifyIcon tray; Timer timer=new Timer();
    Block[] lastBlocks=new Block[0]; ComboBox correctionSelect=new ComboBox(); TextBox correctionInput=new TextBox(),engTermInput=new TextBox(),korTermInput=new TextBox(); Label glossaryStatus=new Label();
    MaterialButton langEnBtn,langJaBtn,langZhBtn,installOcrPackBtn; Label ocrLangStatus; ToolStripMenuItem trayLangEn,trayLangJa,trayLangZh;
    readonly HelperLifetime helpers=new HelperLifetime();LocalTranslationEngine localEngine;Label engineState;Process workerHost;
    Process worker; string temp,output; Rectangle capture,display; Point anchor; IntPtr target;
    bool nearMode; double imageScale=1; int generation=0,jobGeneration; DateTime started,shown;
    bool selectingRegion;RegionPicker picker;Label regionSummary;CheckBox fixedRegion,showCaptureFlashChk;
    public MainForm() {
        SuspendLayout();AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        Text="DalvitLens v1.3.0 · 게임 화면 번역";ClientSize=new Size(860,920);MinimumSize=new Size(620,600);StartPosition=FormStartPosition.CenterScreen;
        var iconPath=Path.Combine(root,"assets","DalvitLens.ico");if(File.Exists(iconPath))Icon=new Icon(iconPath,48,48);
        Font=new Font("맑은 고딕",10);BackColor=MaterialTheme.Surface;ForeColor=MaterialTheme.Text;
        Shown+=(s,e)=>BeginInvoke((Action)(()=>{var area=Screen.FromControl(this).WorkingArea;Size=new Size(Math.Min(Width,area.Width-48),Math.Min(Height,area.Height-48));CenterToScreen();Native.ShowWindow(Handle,5);}));
        try { settings=json.Deserialize<Settings>(File.ReadAllText(Path.Combine(root,"settings.json"))); } catch { settings=new Settings(); }
        if(settings==null)settings=new Settings();
        if(String.IsNullOrEmpty(settings.sourceLang))settings.sourceLang="en";
        if(String.IsNullOrEmpty(settings.hudPosition))settings.hudPosition="top-center";
        settings.nearAnchor=Settings.NormalizeAnchor(settings.nearAnchor);
        if(settings.nearWidth<=0)settings.nearWidth=1280;
        if(settings.nearHeight<=0)settings.nearHeight=860;
        settings.guideOpacity=Math.Max(0,Math.Min(95,settings.guideOpacity));
        if(rangeGuide!=null)rangeGuide.GuideOpacity=settings.guideOpacity;
        localEngine=new LocalTranslationEngine(root,helpers);
        var menu=new ContextMenuStrip();menu.Items.Add("설정 / 번역 기록",null,(s,e)=>{Show();WindowState=FormWindowState.Normal;Activate();});
        menu.Items.Add(new ToolStripSeparator());
        trayLangEn=new ToolStripMenuItem("원문 언어: 영어 (EN)",null,(s,e)=>SelectSourceLang("en",true));
        trayLangJa=new ToolStripMenuItem("원문 언어: 일본어 (JA)",null,(s,e)=>SelectSourceLang("ja",true));
        trayLangZh=new ToolStripMenuItem("원문 언어: 중국어 (ZH)",null,(s,e)=>SelectSourceLang("zh",true));
        menu.Items.Add(trayLangEn);menu.Items.Add(trayLangJa);menu.Items.Add(trayLangZh);
        menu.Items.Add(new ToolStripSeparator());
        trayHudTop=new ToolStripMenuItem("번역 진행 표시: 화면 중앙 상단",null,(s,e)=>SelectHudPosition("top-center",true));
        trayHudBottomRight=new ToolStripMenuItem("번역 진행 표시: 화면 우측 하단",null,(s,e)=>SelectHudPosition("bottom-right",true));
        trayHudOff=new ToolStripMenuItem("번역 진행 표시: 숨김",null,(s,e)=>SelectHudPosition("off",true));
        menu.Items.Add(trayHudTop);menu.Items.Add(trayHudBottomRight);menu.Items.Add(trayHudOff);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("📌 F8 고정 영역 지정 (Ctrl+Alt+F8)",null,(s,e)=>ChooseRegion());
        menu.Items.Add("🔓 영역 지정 해제 · 커서 기준으로 번역 (Ctrl+Alt+F9)",null,(s,e)=>ReleaseFixedRegionToCursorMode());
        trayCaptureFlash=new ToolStripMenuItem("📸 F8 누를 때 캡처 영역 테두리 잠시 표시",null,(s,e)=>ToggleCaptureFlashOption());
        menu.Items.Add(trayCaptureFlash);
        menu.Items.Add("실제 화면 F8 가이드 박스 상시 ON/OFF",null,(s,e)=>ToggleLiveGuide());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("번역 숨김",null,(s,e)=>Dismiss());menu.Items.Add("종료",null,(s,e)=>{tray.Visible=false;Application.Exit();});
        BuildModernUi();SelectSourceLang(settings.sourceLang,false);SelectHudPosition(settings.hudPosition,false);SyncRangeUi();ResumeLayout(true);
        Shown+=(s,e)=>{try{EnsureOcrHost();if(IsLocalBackend(settings.backend))localEngine.Start(settings.backend);}catch(Exception ex){status.Text="번역 엔진 준비 실패: "+ex.Message;}};
        tray=new NotifyIcon{Icon=Icon,Text="DalvitLens v1.3.0 · F8 주변 / F9 전체 / F10 숨김",ContextMenuStrip=menu,Visible=true};tray.DoubleClick+=(s,e)=>{Show();Activate();};
        timer.Interval=120;timer.Tick+=Tick;timer.Start();
        FormClosing+=(s,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
        FormClosed+=(s,e)=>{timer.Stop();CancelWorker();if(workerHost!=null){try{if(!workerHost.HasExited)workerHost.Kill();}catch{}workerHost.Dispose();}localEngine.Dispose();helpers.Dispose();if(overlay.Obs!=null)overlay.Obs.Dispose();overlay.Dispose();if(rangeGuide!=null)rangeGuide.Dispose();if(progressHud!=null)progressHud.Dispose();tray.Dispose();for(int id=1;id<=10;id++)Native.UnregisterHotKey(Handle,id);};
        // Decrypt only in memory; never print or log credentials.
        if(settings.apiKey.StartsWith("dpapi:")) try { key.Text=Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(settings.apiKey.Substring(6)),null,System.Security.Cryptography.DataProtectionScope.CurrentUser)); } catch { key.Text="";status.Text="저장된 키를 읽지 못했습니다. 다시 입력하세요."; }
    }
    void BuildModernUi(){
        var rootPanel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(24),BackColor=MaterialTheme.Surface};
        rootPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));rootPanel.RowStyles.Add(new RowStyle(SizeType.Percent,100));rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));Controls.Add(rootPanel);
        var header=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1,Margin=new Padding(0,0,0,20)};header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        var brand=MaterialTheme.Label("DALVITLENS v1.3.0  /  GAME TRANSLATOR",9,true);brand.ForeColor=MaterialTheme.Primary;header.Controls.Add(brand);
        header.Controls.Add(MaterialTheme.Label("플레이의 흐름을, 한글로",23,true));header.Controls.Add(MaterialTheme.Label("필요한 순간에 번역하고, 보는 그대로 방송에 공유하세요.",10));
        var chips=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,WrapContents=true,Margin=new Padding(0,6,0,0)};
        foreach(var s in new[]{"F7  다음 묶음","F8  주변/지정 번역","F9  전체 번역","F10  숨김","Ctrl+Alt+F8  영역 지정","Ctrl+Alt+F9  영역 해제(커서 기준)"}){var chip=MaterialTheme.Label(s,9,true);chip.Dock=DockStyle.None;chip.AutoSize=true;chip.BackColor=MaterialTheme.Field;chip.ForeColor=MaterialTheme.Number;chip.Padding=new Padding(12,7,12,7);chip.Margin=new Padding(0,0,8,6);chips.Controls.Add(chip);}header.Controls.Add(chips);rootPanel.Controls.Add(header,0,0);
        var scroll=new ThemedScrollHost{Dock=DockStyle.Fill,Margin=Padding.Empty};rootPanel.Controls.Add(scroll,0,1);
        var content=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,Margin=Padding.Empty,Padding=new Padding(0,0,10,0)};content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        var translate=MaterialTheme.CardPanel();content.Controls.Add(translate);translate.Controls.Add(MaterialTheme.Label("번역 설정",14,true));translate.Controls.Add(MaterialTheme.Label("전처리 대비 강화 OCR · 영/일/중 다국어 스위치 · 문단별 번역 · 클릭 통과 오버레이",9));
        translate.Controls.Add(MaterialTheme.Label("원문 언어 스위치 (영 / 일 / 중)",9,true));
        var langRow=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Margin=new Padding(0,0,0,8)};
        langEnBtn=new MaterialButton{Text="영어 (EN)",Filled=true,Margin=new Padding(0,0,8,4)};langEnBtn.Click+=(s,e)=>SelectSourceLang("en",true);langRow.Controls.Add(langEnBtn);
        langJaBtn=new MaterialButton{Text="일본어 (JA)",Margin=new Padding(0,0,8,4)};langJaBtn.Click+=(s,e)=>SelectSourceLang("ja",true);langRow.Controls.Add(langJaBtn);
        langZhBtn=new MaterialButton{Text="중국어 (ZH)",Margin=new Padding(0,0,8,4)};langZhBtn.Click+=(s,e)=>SelectSourceLang("zh",true);langRow.Controls.Add(langZhBtn);
        installOcrPackBtn=new MaterialButton{Text="일/중 OCR 언어팩 설치",Margin=new Padding(4,0,0,4)};installOcrPackBtn.Click+=(s,e)=>InstallOcrLanguagePack();langRow.Controls.Add(installOcrPackBtn);
        translate.Controls.Add(langRow);
        ocrLangStatus=MaterialTheme.Label("전처리 대비 강화 OCR (DalvitOcrEnhancer: 2x 업스케일·대비 스트레칭·언샤프 마스크) 가동 중",9);ocrLangStatus.ForeColor=MaterialTheme.Secondary;ocrLangStatus.Margin=new Padding(0,0,0,14);translate.Controls.Add(ocrLangStatus);
        translate.Controls.Add(MaterialTheme.Label("번역 진행 중 표시 위치 (인게임 HUD 상태 알림)",9,true));
        var hudRow=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Margin=new Padding(0,0,0,14)};
        hudTopBtn=new MaterialButton{Text="화면 중앙 상단",Filled=true,Margin=new Padding(0,0,8,4)};hudTopBtn.Click+=(s,e)=>SelectHudPosition("top-center",true);hudRow.Controls.Add(hudTopBtn);
        hudBottomRightBtn=new MaterialButton{Text="화면 우측 하단",Margin=new Padding(0,0,8,4)};hudBottomRightBtn.Click+=(s,e)=>SelectHudPosition("bottom-right",true);hudRow.Controls.Add(hudBottomRightBtn);
        hudOffBtn=new MaterialButton{Text="표시 안 함",Margin=new Padding(0,0,0,4)};hudOffBtn.Click+=(s,e)=>SelectHudPosition("off",true);hudRow.Controls.Add(hudOffBtn);
        translate.Controls.Add(hudRow);
        backend.DropDownStyle=ComboBoxStyle.DropDownList;backend.FlatStyle=FlatStyle.Flat;backend.BackColor=MaterialTheme.Field;backend.ForeColor=MaterialTheme.Text;backend.Dock=DockStyle.Fill;backend.Margin=new Padding(0,0,0,16);
        backend.DrawMode=DrawMode.OwnerDrawFixed;backend.ItemHeight=30;backend.DrawItem+=(s,e)=>{if(e.Index<0)return;using(var b=new SolidBrush((e.State&DrawItemState.Selected)!=0?MaterialTheme.Outline:MaterialTheme.Field))e.Graphics.FillRectangle(b,e.Bounds);TextRenderer.DrawText(e.Graphics,backend.Items[e.Index].ToString(),backend.Font,new Rectangle(e.Bounds.X+10,e.Bounds.Y,e.Bounds.Width-20,e.Bounds.Height),MaterialTheme.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);};
        backend.Items.AddRange(new object[]{"Google 간편 연결 · 키 없이 사용","Google Cloud · 공식 API","Hy-MT2 1.8B · 빠른 로컬 번역 / GPU","TranslateGemma 4B · 로컬 번역 / GPU","EXAONE 3.5 7.8B · 고품질 로컬 번역 / GPU"});backend.SelectedIndex=settings.backend=="exaone"?4:settings.backend=="gemma"?3:settings.backend=="hymt"?2:settings.backend=="google-official"?1:0;
        translate.Controls.Add(MaterialTheme.Label("번역 엔진",9));translate.Controls.Add(backend);
        engineState=MaterialTheme.Label("로컬 Q4 모델 · 설정 저장 후 자동 실행 · GPU에는 한 모델만 유지",9);engineState.ForeColor=MaterialTheme.Secondary;translate.Controls.Add(engineState);
        var engineActions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Margin=new Padding(0,0,0,16)};
        var installModels=new MaterialButton{Text="AI 모델 설치 · 관리",Filled=true,Margin=new Padding(0,0,8,0)};
        installModels.Click+=(s,e)=>{
            string pre=backend.SelectedIndex==3?"gemma":backend.SelectedIndex==2?"hymt":"exaone";
            using(var dlg=new DalvitInstaller.ModelInstallerDialog(root,pre)){
                dlg.ShowDialog(this);
                if(dlg.InstalledAny){
                    if(backend.SelectedIndex<2)backend.SelectedIndex=dlg.LastInstalledKind=="gemma"?3:dlg.LastInstalledKind=="hymt"?2:4;
                    Save();
                }
            }
        };
        engineActions.Controls.Add(installModels);
        var restartEngine=new MaterialButton{Text="로컬 엔진 다시 시작",Margin=Padding.Empty};restartEngine.Click+=(s,e)=>{try{Save();localEngine.Stop();localEngine.Start(settings.backend);}catch(Exception ex){status.Text="엔진 시작 실패: "+ex.Message;}};
        engineActions.Controls.Add(restartEngine);translate.Controls.Add(engineActions);
        translate.Controls.Add(MaterialTheme.Label("F8 캡처 영역 · 가시적 범위 에디터",12,true));
        regionSummary=MaterialTheme.Label("",9);regionSummary.ForeColor=MaterialTheme.Secondary;translate.Controls.Add(regionSummary);
        var monBounds=Screen.PrimaryScreen!=null?Screen.PrimaryScreen.Bounds:new Rectangle(0,0,1920,1080);
        rangePreview=new NearRangePreviewControl{MonitorWidth=monBounds.Width,MonitorHeight=monBounds.Height,RangeWidth=settings.nearWidth,RangeHeight=settings.nearHeight,AnchorMode=settings.nearAnchor};
        rangePreview.RangeChanged+=(nw,nh)=>ApplyRangeSize(nw,nh,true);
        translate.Controls.Add(rangePreview);
        translate.Controls.Add(MaterialTheme.Label("커서 기준 캡처 영역 배치 (마우스 커서 대비 번역 박스 방향)",9));
        var anchorRow=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Margin=new Padding(0,0,0,10)};
        anchorCenterBtn=new MaterialButton{Text="정중앙",Margin=new Padding(0,0,8,4)};anchorCenterBtn.Click+=(s,e)=>SelectNearAnchor("center",true);anchorRow.Controls.Add(anchorCenterBtn);
        anchorRightBtn=new MaterialButton{Text="오른쪽 →",Margin=new Padding(0,0,8,4)};anchorRightBtn.Click+=(s,e)=>SelectNearAnchor("right",true);anchorRow.Controls.Add(anchorRightBtn);
        anchorLeftBtn=new MaterialButton{Text="← 왼쪽",Margin=new Padding(0,0,8,4)};anchorLeftBtn.Click+=(s,e)=>SelectNearAnchor("left",true);anchorRow.Controls.Add(anchorLeftBtn);
        anchorTopBtn=new MaterialButton{Text="↑ 상 (위)",Margin=new Padding(0,0,8,4)};anchorTopBtn.Click+=(s,e)=>SelectNearAnchor("top",true);anchorRow.Controls.Add(anchorTopBtn);
        anchorBottomBtn=new MaterialButton{Text="↓ 하 (아래)",Margin=new Padding(0,0,0,4)};anchorBottomBtn.Click+=(s,e)=>SelectNearAnchor("bottom",true);anchorRow.Controls.Add(anchorBottomBtn);
        translate.Controls.Add(anchorRow);
        translate.Controls.Add(MaterialTheme.Label("빠른 크기 프리셋 (마우스 기준 좌우·상하 번역 범위)",9));
        var presetRow=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Margin=new Padding(0,0,0,10)};
        presetSmallBtn=new MaterialButton{Text="소형 900×650",Margin=new Padding(0,0,8,4)};presetSmallBtn.Click+=(s,e)=>ApplyRangeSize(900,650,true);presetRow.Controls.Add(presetSmallBtn);
        presetStdBtn=new MaterialButton{Text="표준 1280×860",Margin=new Padding(0,0,8,4)};presetStdBtn.Click+=(s,e)=>ApplyRangeSize(1280,860,true);presetRow.Controls.Add(presetStdBtn);
        presetLargeBtn=new MaterialButton{Text="대형 1600×1050",Margin=new Padding(0,0,8,4)};presetLargeBtn.Click+=(s,e)=>ApplyRangeSize(1600,1050,true);presetRow.Controls.Add(presetLargeBtn);
        presetXLargeBtn=new MaterialButton{Text="초대형 2000×1300",Margin=new Padding(0,0,8,4)};presetXLargeBtn.Click+=(s,e)=>ApplyRangeSize(2000,1300,true);presetRow.Controls.Add(presetXLargeBtn);
        var shrinkBtn=new MaterialButton{Text="－ 축소",Margin=new Padding(0,0,6,4)};shrinkBtn.Click+=(s,e)=>ApplyRangeSize(settings.nearWidth-120,settings.nearHeight-80,true);presetRow.Controls.Add(shrinkBtn);
        var expandBtn=new MaterialButton{Text="＋ 확대",Margin=new Padding(0,0,0,4)};expandBtn.Click+=(s,e)=>ApplyRangeSize(settings.nearWidth+120,settings.nearHeight+80,true);presetRow.Controls.Add(expandBtn);
        translate.Controls.Add(presetRow);
        var sizeGrid=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Margin=new Padding(0,0,0,12)};
        sizeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));sizeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        sizeGrid.Controls.Add(MaterialTheme.Label("가로 범위 (Width px · 마우스 좌우 합계)",9),0,0);
        sizeGrid.Controls.Add(MaterialTheme.Label("세로 범위 (Height px · 마우스 상하 합계)",9),1,0);
        nearWidthInput=new NumericUpDown{Minimum=48,Maximum=3840,Increment=40,Value=Math.Max(48,Math.Min(3840,settings.nearWidth)),BackColor=MaterialTheme.Field,ForeColor=MaterialTheme.Text,BorderStyle=BorderStyle.FixedSingle,Font=Font,Dock=DockStyle.Fill,Margin=new Padding(0,0,8,0)};
        nearHeightInput=new NumericUpDown{Minimum=48,Maximum=2160,Increment=40,Value=Math.Max(48,Math.Min(2160,settings.nearHeight)),BackColor=MaterialTheme.Field,ForeColor=MaterialTheme.Text,BorderStyle=BorderStyle.FixedSingle,Font=Font,Dock=DockStyle.Fill,Margin=new Padding(8,0,0,0)};
        nearWidthInput.ValueChanged+=(s,e)=>{if(!syncingRangeControls)ApplyRangeSize((int)nearWidthInput.Value,(int)nearHeightInput.Value,true);};
        nearHeightInput.ValueChanged+=(s,e)=>{if(!syncingRangeControls)ApplyRangeSize((int)nearWidthInput.Value,(int)nearHeightInput.Value,true);};
        sizeGrid.Controls.Add(nearWidthInput,0,1);sizeGrid.Controls.Add(nearHeightInput,1,1);
        translate.Controls.Add(sizeGrid);
        var regionActions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Margin=new Padding(0,0,0,12)};
        var chooseRegion=new MaterialButton{Text="📌 화면에서 고정 영역 지정 (Ctrl+Alt+F8)",Filled=true,Margin=new Padding(0,0,8,4)};chooseRegion.Click+=(s,e)=>ChooseRegion();regionActions.Controls.Add(chooseRegion);
        releaseRegionBtn=new MaterialButton{Text="🔓 영역 지정 해제 · 커서 기준으로 번역 (Ctrl+Alt+F9)",Margin=new Padding(0,0,8,4)};releaseRegionBtn.Click+=(s,e)=>ReleaseFixedRegionToCursorMode();regionActions.Controls.Add(releaseRegionBtn);
        toggleGuideBtn=new MaterialButton{Text="실제 화면 가이드 박스: OFF",Margin=new Padding(0,0,8,4)};toggleGuideBtn.Click+=(s,e)=>ToggleLiveGuide();regionActions.Controls.Add(toggleGuideBtn);
        var resetRegion=new MaterialButton{Text="표준 크기 초기화",Margin=new Padding(0,0,0,4)};resetRegion.Click+=(s,e)=>{settings.nearRegionW=0;settings.nearRegionH=0;settings.nearFixed=false;if(fixedRegion!=null)fixedRegion.Checked=false;ApplyRangeSize(1280,860,true);};regionActions.Controls.Add(resetRegion);translate.Controls.Add(regionActions);
        showCaptureFlashChk=new CheckBox{Text="📸 F8 누를 때 캡처되는 영역 테두리를 잠시 보여주고 번역 (어떤 영역이 캡처되는지 화면에서 즉시 인지)",AutoSize=true,Dock=DockStyle.Top,ForeColor=MaterialTheme.Secondary,Checked=settings.showCaptureFlash,Margin=new Padding(0,0,0,8)};
        showCaptureFlashChk.CheckedChanged+=(s,e)=>{if(syncingRangeControls)return;settings.showCaptureFlash=showCaptureFlashChk.Checked;Save();SyncRangeUi();status.Text=settings.showCaptureFlash?"📸 F8 입력 시 캡처되는 영역 테두리를 잠시 표시하고 번역합니다.":"F8 입력 시 캡처 영역 테두리 표시를 껐습니다.";if(settings.showCaptureFlash&&rangeGuide!=null)rangeGuide.FlashUntil=DateTime.UtcNow.AddSeconds(1.8);};
        translate.Controls.Add(showCaptureFlashChk);
        fixedRegion=new CheckBox{Text="선택한 위치에 고정 [영역 지정 중] (체크 해제 또는 Ctrl+Alt+F9 입력 시 마우스 커서 기준 번역으로 전환)",AutoSize=true,Dock=DockStyle.Top,ForeColor=MaterialTheme.Text,Checked=settings.nearFixed,Margin=new Padding(0,0,0,10)};fixedRegion.CheckedChanged+=(s,e)=>{if(syncingRangeControls)return;if(!fixedRegion.Checked){ReleaseFixedRegionToCursorMode();return;}if(settings.nearRegionW<=0){var mb=Screen.FromControl(this).Bounds;settings.nearRegionW=Math.Min(1.0,settings.nearWidth/(double)mb.Width);settings.nearRegionH=Math.Min(1.0,settings.nearHeight/(double)mb.Height);settings.nearRegionX=(1.0-settings.nearRegionW)/2.0;settings.nearRegionY=(1.0-settings.nearRegionH)/2.0;settings.nearMonitor=Screen.FromControl(this).DeviceName;}settings.nearFixed=true;Save();SyncRangeUi();};translate.Controls.Add(fixedRegion);
        translate.Controls.Add(MaterialTheme.Label("F8 캡처/가이드 전체 불투명도 (라인색·배경색 동일 Opacity % · 기본 30%)",9));
        guideOpacityInput=new NumericUpDown{Minimum=0,Maximum=95,Increment=5,Value=Math.Max(0,Math.Min(95,settings.guideOpacity)),BackColor=MaterialTheme.Field,ForeColor=MaterialTheme.Text,BorderStyle=BorderStyle.FixedSingle,Font=Font,Dock=DockStyle.Top,Margin=new Padding(0,0,0,12)};
        guideOpacityInput.ValueChanged+=(s,e)=>{if(syncingRangeControls)return;settings.guideOpacity=(int)guideOpacityInput.Value;if(rangeGuide!=null){rangeGuide.GuideOpacity=settings.guideOpacity;rangeGuide.FlashUntil=DateTime.UtcNow.AddSeconds(1.8);}Save();status.Text="F8 캡처/가이드 전체 불투명도(라인색·배경색)를 "+settings.guideOpacity+"%로 설정했습니다.";};
        translate.Controls.Add(guideOpacityInput);
        translate.Controls.Add(MaterialTheme.Label("• 게임 중 F8을 누르면 먼저 깨끗한 화면을 캡처한 직후, 실제 캡처된 영역 테두리(📸 F8 캡처 영역)를 화면에 잠시 보여주며 번역을 진행합니다.\n• Ctrl + Alt + F8로 원하는 구역을 드래그해 저장하면 [📌 영역 지정 중] 상태가 되어 항상 그 영역만 F8로 번역합니다.\n• 고정 영역을 해제하고 다시 마우스 커서 기준(정중앙/오른쪽/왼쪽/상/하)으로 번역하려면 [Ctrl + Alt + F9]를 누르거나 위 [🔓 영역 지정 해제] 버튼을 누르세요.",9));
        key.UseSystemPasswordChar=true;key.Text=settings.apiKey;
        translate.Controls.Add(MaterialTheme.Label("Google Cloud API 키",9));translate.Controls.Add(InputSurface(key));
        font.Minimum=10;font.Maximum=24;font.Value=Math.Max(10,Math.Min(24,settings.fontSize));font.BackColor=MaterialTheme.Field;font.ForeColor=MaterialTheme.Text;font.BorderStyle=BorderStyle.FixedSingle;font.Font=Font;font.Size=new Size(100,font.PreferredHeight);font.Dock=DockStyle.Top;font.Margin=new Padding(0,0,0,12);
        translate.Controls.Add(MaterialTheme.Label("번역 글자 크기",9));translate.Controls.Add(font);
        translate.Controls.Add(MaterialTheme.Label("번역 툴팁 표시",11,true));
        tooltipLine=new CheckBox{Text="LINE ON · 보라색 테두리 표시",AutoSize=true,Dock=DockStyle.Top,ForeColor=MaterialTheme.Primary,Checked=settings.tooltipLine,Margin=new Padding(0,0,0,12)};
        translate.Controls.Add(tooltipLine);
        lineOpacity=OpacityInput(translate,"보라색 라인 불투명도 (%)",settings.tooltipLineOpacity);
        tooltipOpacity=OpacityInput(translate,"내 화면 · 툴팁 배경 불투명도 (%)",settings.tooltipOpacity);
        obsTooltipOpacity=OpacityInput(translate,"OBS · 툴팁 배경 불투명도 (%)",settings.obsTooltipOpacity);
        tooltipLine.CheckedChanged+=(s,e)=>{tooltipLine.Text=tooltipLine.Checked?"LINE ON · 보라색 테두리 표시":"LINE OFF · 보라색 테두리 숨김";lineOpacity.Enabled=tooltipLine.Checked;};
        tooltipLine.Text=tooltipLine.Checked?"LINE ON · 보라색 테두리 표시":"LINE OFF · 보라색 테두리 숨김";lineOpacity.Enabled=tooltipLine.Checked;
        translate.Controls.Add(MaterialTheme.Label("0%는 완전 투명, 100%는 불투명입니다. 글자는 선명하게 유지됩니다.\n설정 저장을 누르면 현재 번역과 OBS에도 반영됩니다.",9));
        translate.Controls.Add(MaterialTheme.Label("Hy-MT2 · TranslateGemma · EXAONE 3.5는 내 PC에서 처리합니다 (RPG 용어집·OCR 정제 적용).\nGoogle은 인식한 영문만 전송합니다. 간편 연결은 비공식 방식이며, Cloud API는 사용 요금이 발생할 수 있습니다.",9));
        backend.SelectedIndexChanged+=(s,e)=>{key.Enabled=backend.SelectedIndex==1;restartEngine.Visible=backend.SelectedIndex>=2;engineState.Visible=backend.SelectedIndex>=2;};key.Enabled=backend.SelectedIndex==1;restartEngine.Visible=backend.SelectedIndex>=2;engineState.Visible=backend.SelectedIndex>=2;
        var obs=MaterialTheme.CardPanel();content.Controls.Add(obs);obs.Controls.Add(MaterialTheme.Label("OBS에 함께 보여주기",14,true));
        obs.Controls.Add(MaterialTheme.Label("1920 × 1080 출력  ·  4K 좌표 자동 축소  ·  투명 배경",9));
        var address=new TextBox{Text=ObsOverlayServer.Url,ReadOnly=true,AccessibleName="OBS 브라우저 소스 주소"};obs.Controls.Add(InputSurface(address));address.ForeColor=MaterialTheme.Secondary;
        var copy=new MaterialButton{Text="주소 복사",Dock=DockStyle.Left,Margin=new Padding(0,0,0,12)};copy.Click+=(s,e)=>{Clipboard.SetText(ObsOverlayServer.Url);status.Text="OBS 브라우저 소스용 주소를 복사했습니다.";};obs.Controls.Add(copy);
        obs.Controls.Add(MaterialTheme.Label("OBS 브라우저 소스: 너비 1920 / 높이 1080\nF7 페이지 변경과 F10 숨김이 방송 화면에도 함께 반영됩니다.",9));
        var log=MaterialTheme.CardPanel();content.Controls.Add(log);log.Controls.Add(MaterialTheme.Label("마지막 번역 · 교정 및 자동 학습",14,true));
        glossaryStatus=MaterialTheme.Label("",9);glossaryStatus.ForeColor=MaterialTheme.Secondary;UpdateGlossarySummary();log.Controls.Add(glossaryStatus);
        history.BackColor=MaterialTheme.Field;history.ForeColor=MaterialTheme.Text;history.Dock=DockStyle.Top;history.Height=210;history.Margin=new Padding(0,4,0,12);log.Controls.Add(history);
        var copyHistory=new MaterialButton{Text="번역 기록 복사",Dock=DockStyle.Left,Margin=new Padding(0,0,0,16)};copyHistory.Click+=(s,e)=>{if(history.Text.Length>0){Clipboard.SetText(history.Text);status.Text="번역 기록을 복사했습니다.";}};log.Controls.Add(copyHistory);
        log.Controls.Add(MaterialTheme.Label("문단 선택 후 한글 번역을 수정하면 화면·OBS·캐시와 용어집(glossary.json)에 자동 학습됩니다.",9));
        correctionSelect.DropDownStyle=ComboBoxStyle.DropDownList;correctionSelect.FlatStyle=FlatStyle.Flat;correctionSelect.BackColor=MaterialTheme.Field;correctionSelect.ForeColor=MaterialTheme.Text;correctionSelect.Dock=DockStyle.Fill;correctionSelect.Margin=new Padding(0,0,0,12);
        correctionSelect.DrawMode=DrawMode.OwnerDrawFixed;correctionSelect.ItemHeight=28;correctionSelect.DrawItem+=(s,e)=>{if(e.Index<0)return;using(var b=new SolidBrush((e.State&DrawItemState.Selected)!=0?MaterialTheme.Outline:MaterialTheme.Field))e.Graphics.FillRectangle(b,e.Bounds);TextRenderer.DrawText(e.Graphics,correctionSelect.Items[e.Index].ToString(),correctionSelect.Font,new Rectangle(e.Bounds.X+10,e.Bounds.Y,e.Bounds.Width-20,e.Bounds.Height),MaterialTheme.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);};
        correctionSelect.SelectedIndexChanged+=(s,e)=>{int idx=correctionSelect.SelectedIndex;if(idx>=0&&idx<lastBlocks.Length)correctionInput.Text=lastBlocks[idx].translated;};
        log.Controls.Add(MaterialTheme.Label("교정할 문단 선택",9));log.Controls.Add(correctionSelect);
        log.Controls.Add(MaterialTheme.Label("번역문 수정 (어색한 단어를 고치면 원문과 비교해 용어집에 자동 등록)",9));log.Controls.Add(InputSurface(correctionInput));
        log.Controls.Add(MaterialTheme.Label("또는 고정할 용어 직접 추가 (선택 사항 · 예: Shuriken / 手裏剣 / 手里剑 → 표창)",9));
        var termRow=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Margin=new Padding(0,0,0,8)};termRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));termRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        var engWrap=InputSurface(engTermInput);engWrap.Margin=new Padding(0,0,6,8);var korWrap=InputSurface(korTermInput);korWrap.Margin=new Padding(6,0,0,8);
        termRow.Controls.Add(engWrap,0,0);termRow.Controls.Add(korWrap,1,0);log.Controls.Add(termRow);
        var learnBtn=new MaterialButton{Text="교정 적용 · 용어집 학습",Filled=true,Dock=DockStyle.Left,Margin=Padding.Empty};learnBtn.Click+=(s,e)=>ApplyCorrection();log.Controls.Add(learnBtn);
        scroll.SetContent(content);
        var footer=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1,Padding=new Padding(0,14,0,0),Margin=Padding.Empty};footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));rootPanel.Controls.Add(footer,0,2);
        status.AutoSize=true;status.Dock=DockStyle.Fill;status.ForeColor=MaterialTheme.Primary;status.Margin=new Padding(0,0,0,12);status.Text="준비됐어요. 게임에서 F8을 눌러주세요.";footer.Controls.Add(status);
        try{overlay.Obs=new ObsOverlayServer();}catch{status.Text="OBS 포트를 열지 못했습니다. 게임 번역은 사용할 수 있습니다.";}
        var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,FlowDirection=FlowDirection.RightToLeft,Margin=Padding.Empty};
        var start=new MaterialButton{Text="게임으로 돌아가기",Filled=true};start.Click+=(s,e)=>{Save();Hide();};actions.Controls.Add(start);
        var save=new MaterialButton{Text="설정 저장"};save.Click+=(s,e)=>Save();actions.Controls.Add(save);footer.Controls.Add(actions);
        footer.Controls.Add(MaterialTheme.Label("창을 닫아도 트레이에서 계속 실행됩니다.",8));
    }
    Control InputSurface(TextBox input){
        input.BorderStyle=BorderStyle.None;input.BackColor=MaterialTheme.Field;input.ForeColor=MaterialTheme.Text;input.Dock=DockStyle.Top;input.Margin=Padding.Empty;
        var p=new Panel{BackColor=MaterialTheme.Field,Dock=DockStyle.Top,Height=input.PreferredHeight+24,Padding=new Padding(14,12,14,12),Margin=new Padding(0,0,0,16)};p.Controls.Add(input);return p;
    }
    NumericUpDown OpacityInput(Control parent,string title,int value){
        parent.Controls.Add(MaterialTheme.Label(title,9));
        var input=new NumericUpDown{Minimum=0,Maximum=100,Value=Math.Max(0,Math.Min(100,value)),Increment=5,BackColor=MaterialTheme.Field,ForeColor=MaterialTheme.Text,BorderStyle=BorderStyle.FixedSingle,Font=Font,Dock=DockStyle.Top,Margin=new Padding(0,0,0,12)};
        parent.Controls.Add(input);return input;
    }
    void UpdateRegionSummary(){
        if(regionSummary==null)return;
        if(settings.nearFixed){
            regionSummary.Text="📌 [영역 지정 중] 고정 위치 ("+settings.nearWidth+" × "+settings.nearHeight+" px)  ·  해제하고 커서 기준으로 전환: Ctrl+Alt+F9";
            regionSummary.ForeColor=MaterialTheme.Number;
        }else{
            regionSummary.Text="🖱 [커서 기준 번역 모드] "+settings.nearWidth+" × "+settings.nearHeight+" px  ·  배치: ["+Settings.AnchorLabel(settings.nearAnchor)+"]";
            regionSummary.ForeColor=MaterialTheme.Secondary;
        }
    }
    void SyncRangeUi(){
        syncingRangeControls=true;
        try{
            string anc=Settings.NormalizeAnchor(settings.nearAnchor);
            settings.nearAnchor=anc;
            UpdateRegionSummary();
            if(fixedRegion!=null)fixedRegion.Checked=settings.nearFixed;
            if(showCaptureFlashChk!=null)showCaptureFlashChk.Checked=settings.showCaptureFlash;
            if(trayCaptureFlash!=null)trayCaptureFlash.Checked=settings.showCaptureFlash;
            if(guideOpacityInput!=null)guideOpacityInput.Value=Math.Max(guideOpacityInput.Minimum,Math.Min(guideOpacityInput.Maximum,settings.guideOpacity));
            if(rangeGuide!=null)rangeGuide.GuideOpacity=settings.guideOpacity;
            if(releaseRegionBtn!=null){releaseRegionBtn.Filled=settings.nearFixed;releaseRegionBtn.Invalidate();}
            if(nearWidthInput!=null)nearWidthInput.Value=Math.Max(nearWidthInput.Minimum,Math.Min(nearWidthInput.Maximum,settings.nearWidth));
            if(nearHeightInput!=null)nearHeightInput.Value=Math.Max(nearHeightInput.Minimum,Math.Min(nearHeightInput.Maximum,settings.nearHeight));
            if(rangePreview!=null){
                var mb=Screen.FromControl(this).Bounds;
                rangePreview.MonitorWidth=mb.Width;rangePreview.MonitorHeight=mb.Height;
                rangePreview.RangeWidth=settings.nearWidth;rangePreview.RangeHeight=settings.nearHeight;
                rangePreview.IsFixed=settings.nearFixed;
                rangePreview.FixedRegionX=settings.nearRegionX;rangePreview.FixedRegionY=settings.nearRegionY;
                rangePreview.FixedRegionW=settings.nearRegionW;rangePreview.FixedRegionH=settings.nearRegionH;
                rangePreview.AnchorMode=anc;
                rangePreview.Invalidate();
            }
            if(anchorCenterBtn!=null){anchorCenterBtn.Filled=(!settings.nearFixed&&anc=="center");anchorCenterBtn.Invalidate();}
            if(anchorRightBtn!=null){anchorRightBtn.Filled=(!settings.nearFixed&&anc=="right");anchorRightBtn.Invalidate();}
            if(anchorLeftBtn!=null){anchorLeftBtn.Filled=(!settings.nearFixed&&anc=="left");anchorLeftBtn.Invalidate();}
            if(anchorTopBtn!=null){anchorTopBtn.Filled=(!settings.nearFixed&&anc=="top");anchorTopBtn.Invalidate();}
            if(anchorBottomBtn!=null){anchorBottomBtn.Filled=(!settings.nearFixed&&anc=="bottom");anchorBottomBtn.Invalidate();}
            if(presetSmallBtn!=null){presetSmallBtn.Filled=(settings.nearWidth==900&&settings.nearHeight==650);presetSmallBtn.Invalidate();}
            if(presetStdBtn!=null){presetStdBtn.Filled=(settings.nearWidth==1280&&settings.nearHeight==860);presetStdBtn.Invalidate();}
            if(presetLargeBtn!=null){presetLargeBtn.Filled=(settings.nearWidth==1600&&settings.nearHeight==1050);presetLargeBtn.Invalidate();}
            if(presetXLargeBtn!=null){presetXLargeBtn.Filled=(settings.nearWidth==2000&&settings.nearHeight==1300);presetXLargeBtn.Invalidate();}
            if(toggleGuideBtn!=null){
                toggleGuideBtn.Filled=(rangeGuide!=null&&rangeGuide.Pinned);
                toggleGuideBtn.Text=(rangeGuide!=null&&rangeGuide.Pinned)?"실제 화면 가이드 박스: ON":"실제 화면 가이드 박스: OFF";
                toggleGuideBtn.Invalidate();
            }
        }finally{syncingRangeControls=false;}
    }
    void ReleaseFixedRegionToCursorMode(){
        settings.nearFixed=false;
        if(settings.nearWidth<360||settings.nearHeight<240){
            settings.nearWidth=1280;
            settings.nearHeight=860;
        }
        SyncRangeUi();
        Save();
        status.Text="🔓 고정 영역 지정을 해제했습니다. 이제 마우스 커서 기준(["+Settings.AnchorLabel(settings.nearAnchor)+"])으로 F8 번역을 수행합니다.";
        if(rangeGuide!=null)rangeGuide.FlashUntil=DateTime.UtcNow.AddSeconds(2.2);
        if(progressHud!=null)progressHud.ShowToast(Screen.FromPoint(Cursor.Position).Bounds,settings.hudPosition,"◎ 영역 지정 해제 완료  →  [커서 기준 번역 ("+Settings.AnchorLabel(settings.nearAnchor)+")] 전환",false,2.2);
    }
    void SelectNearAnchor(string anchorMode, bool saveNow){
        bool wasFixed=settings.nearFixed;
        settings.nearAnchor=Settings.NormalizeAnchor(anchorMode);
        if(settings.nearFixed){
            settings.nearFixed=false;
            if(settings.nearWidth<360||settings.nearHeight<240){settings.nearWidth=1280;settings.nearHeight=860;}
        }
        SyncRangeUi();
        if(saveNow){
            Save();
            status.Text=(wasFixed?"🔓 고정 영역을 해제하고 ":"")+"F8 캡처 영역을 커서 기준 ["+Settings.AnchorLabel(settings.nearAnchor)+"]으로 설정했습니다.";
            if(rangeGuide!=null)rangeGuide.FlashUntil=DateTime.UtcNow.AddSeconds(2.2);
            if(wasFixed&&progressHud!=null)progressHud.ShowToast(Screen.FromPoint(Cursor.Position).Bounds,settings.hudPosition,"◎ 영역 지정 해제  →  [커서 기준 번역 ("+Settings.AnchorLabel(settings.nearAnchor)+")] 전환",false,2.0);
        }
    }
    void ApplyRangeSize(int w, int h, bool flashGuide){
        settings.nearWidth=Math.Max(48,Math.Min(3840,w));
        settings.nearHeight=Math.Max(48,Math.Min(2160,h));
        if(settings.nearFixed&&settings.nearRegionW>0){
            var mb=Screen.FromControl(this).Bounds;
            double cx=settings.nearRegionX+settings.nearRegionW/2.0;
            double cy=settings.nearRegionY+settings.nearRegionH/2.0;
            double nw=Math.Min(1.0,settings.nearWidth/(double)mb.Width);
            double nh=Math.Min(1.0,settings.nearHeight/(double)mb.Height);
            settings.nearRegionW=nw;settings.nearRegionH=nh;
            settings.nearRegionX=Math.Max(0.0,Math.Min(1.0-nw,cx-nw/2.0));
            settings.nearRegionY=Math.Max(0.0,Math.Min(1.0-nh,cy-nh/2.0));
        }
        SyncRangeUi();
        Save();
        status.Text="F8 번역 범위를 "+settings.nearWidth+" × "+settings.nearHeight+" px로 설정했습니다.";
        if(flashGuide&&rangeGuide!=null)rangeGuide.FlashUntil=DateTime.UtcNow.AddSeconds(2.2);
    }
    void ToggleLiveGuide(){
        if(rangeGuide==null)return;
        rangeGuide.Pinned=!rangeGuide.Pinned;
        if(!rangeGuide.Pinned){rangeGuide.FlashUntil=DateTime.MinValue;rangeGuide.Hide();}
        SyncRangeUi();
        status.Text=rangeGuide.Pinned?"실제 화면에 마우스 기준 F8 번역 범위 가이드 박스를 표시합니다. (마우스 클릭 통과)":"실제 화면 F8 가이드 박스를 숨겼습니다.";
    }
    void ToggleCaptureFlashOption(){
        settings.showCaptureFlash=!settings.showCaptureFlash;
        SyncRangeUi();
        Save();
        status.Text=settings.showCaptureFlash?"📸 F8 입력 시 캡처되는 영역 테두리를 잠시 표시하고 번역합니다.":"F8 입력 시 캡처 영역 테두리 표시를 껐습니다.";
        if(settings.showCaptureFlash&&rangeGuide!=null)rangeGuide.FlashUntil=DateTime.UtcNow.AddSeconds(1.8);
    }
    void SelectHudPosition(string pos, bool saveNow){
        string norm=String.IsNullOrEmpty(pos)?"top-center":pos.Trim().ToLowerInvariant();
        if(norm!="bottom-right"&&norm!="off")norm="top-center";
        settings.hudPosition=norm;
        if(hudTopBtn!=null){hudTopBtn.Filled=(norm=="top-center");hudTopBtn.Invalidate();}
        if(hudBottomRightBtn!=null){hudBottomRightBtn.Filled=(norm=="bottom-right");hudBottomRightBtn.Invalidate();}
        if(hudOffBtn!=null){hudOffBtn.Filled=(norm=="off");hudOffBtn.Invalidate();}
        if(trayHudTop!=null)trayHudTop.Checked=(norm=="top-center");
        if(trayHudBottomRight!=null)trayHudBottomRight.Checked=(norm=="bottom-right");
        if(trayHudOff!=null)trayHudOff.Checked=(norm=="off");
        if(saveNow){
            Save();
            string posLabel=norm=="bottom-right"?"화면 우측 하단":norm=="off"?"표시 안 함":"화면 중앙 상단";
            status.Text="번역 진행 중 표시 위치를 ["+posLabel+"]으로 설정했습니다.";
            if(progressHud!=null){
                if(norm=="off")progressHud.Hide();
                else progressHud.ShowToast(Screen.FromControl(this).Bounds,norm,"✔ 번역 진행 중 표시 위치 미리보기  ["+posLabel+"]",false,1.8);
            }
        }
    }
    void SelectSourceLang(string lang, bool saveNow){
        string norm = String.IsNullOrEmpty(lang) ? "en" : lang.Trim().ToLowerInvariant();
        if(norm!="ja"&&norm!="zh")norm="en";
        settings.sourceLang=norm;
        if(langEnBtn!=null){langEnBtn.Filled=(norm=="en");langEnBtn.Invalidate();}
        if(langJaBtn!=null){langJaBtn.Filled=(norm=="ja");langJaBtn.Invalidate();}
        if(langZhBtn!=null){langZhBtn.Filled=(norm=="zh");langZhBtn.Invalidate();}
        if(trayLangEn!=null)trayLangEn.Checked=(norm=="en");
        if(trayLangJa!=null)trayLangJa.Checked=(norm=="ja");
        if(trayLangZh!=null)trayLangZh.Checked=(norm=="zh");
        string labelName=norm=="ja"?"일본어 (JA)":norm=="zh"?"중국어 (ZH)":"영어 (EN)";
        if(ocrLangStatus!=null)ocrLangStatus.Text="전처리 대비 강화 OCR (DalvitOcrEnhancer: 2x 업스케일·대비 스트레칭·언샤프 마스크) · 현재 원문: "+labelName;
        if(saveNow){
            Save();
            status.Text="원문 언어를 "+labelName+" 모드로 전환했습니다.";
        }
    }
    void InstallOcrLanguagePack(){
        try{
            var res=MessageBox.Show(
                "일본어(ja-JP) 및 중국어(zh-CN) Windows OCR 언어팩 자동 설치를 시작할까요?\n\n" +
                "• [예(Y)]: 관리자 권한(UAC) PowerShell로 일/중 OCR 언어팩을 즉시 설치합니다.\n" +
                "• [아니오(N)]: Windows '언어 및 지역' 설정 창을 엽니다.",
                "DalvitLens · 일/중 OCR 언어팩 설치",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Information);
            if(res==DialogResult.Cancel)return;
            if(res==DialogResult.Yes){
                string cmd="Write-Host '[DalvitLens] 일본어(ja-JP) 및 중국어(zh-CN) Windows OCR 언어팩을 설치합니다...' -ForegroundColor Cyan; " +
                           "Add-WindowsCapability -Online -Name 'Language.OCR~~~ja-JP~0.0.1.0' -ErrorAction SilentlyContinue; " +
                           "Add-WindowsCapability -Online -Name 'Language.OCR~~~zh-CN~0.0.1.0' -ErrorAction SilentlyContinue; " +
                           "Write-Host '설치가 완료되었습니다. 창을 닫고 DalvitLens에서 F8로 번역하세요!' -ForegroundColor Green; Read-Host '엔터를 누르면 닫힙니다'";
                Process.Start(new ProcessStartInfo("powershell.exe","-NoLogo -NoProfile -ExecutionPolicy Bypass -Command \""+cmd+"\""){Verb="runas",UseShellExecute=true});
                status.Text="관리자 권한으로 일/중 OCR 언어팩 설치 창을 실행했습니다.";
            } else {
                Process.Start(new ProcessStartInfo("ms-settings:regionlanguage"){UseShellExecute=true});
                status.Text="Windows 언어 및 지역 설정 창을 열었습니다.";
            }
        }catch(Exception ex){
            try{Process.Start(new ProcessStartInfo("ms-settings:regionlanguage"){UseShellExecute=true});}catch{}
            status.Text="언어 설정 열기 안내: "+ex.Message;
        }
    }
    void UpdateGlossarySummary(){
        if(glossaryStatus==null)return;
        try{
            string gPath=Path.Combine(root,"glossary.json");
            if(!File.Exists(gPath)){glossaryStatus.Text="자동 학습 용어집 준비 중";return;}
            var gObj=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(gPath,Encoding.UTF8));
            int exactCount=gObj.ContainsKey("exact")&&gObj["exact"] is Dictionary<string,object>?((Dictionary<string,object>)gObj["exact"]).Count:0;
            int termCount=gObj.ContainsKey("terms")&&gObj["terms"] is System.Collections.ICollection?((System.Collections.ICollection)gObj["terms"]).Count:0;
            int postCount=gObj.ContainsKey("postReplace")&&gObj["postReplace"] is System.Collections.ICollection?((System.Collections.ICollection)gObj["postReplace"]).Count:0;
            glossaryStatus.Text="자동 학습 용어집 활성 · UI 고정 "+exactCount+"개 / RPG 용어 "+termCount+"개 / 후처리 "+postCount+"개 (총 "+(exactCount+termCount+postCount)+"개)";
        }catch{glossaryStatus.Text="자동 학습 용어집 활성";}
    }
    void PopulateCorrectionList(Block[] blocks){
        lastBlocks=blocks??new Block[0];
        correctionSelect.Items.Clear();
        for(int i=0;i<lastBlocks.Length;i++){
            string preview=(i+1)+". "+lastBlocks[i].text.Replace("\r"," ").Replace("\n"," ")+" → "+lastBlocks[i].translated.Replace("\r"," ").Replace("\n"," ");
            correctionSelect.Items.Add(preview);
        }
        if(lastBlocks.Length>0)correctionSelect.SelectedIndex=0;
        else correctionInput.Text="";
    }
    void ApplyCorrection(){
        try{
            int idx=correctionSelect.SelectedIndex;
            string src="",oldTrn="",newTrn=correctionInput.Text.Trim(),engT=engTermInput.Text.Trim(),korT=korTermInput.Text.Trim();
            if(idx>=0&&idx<lastBlocks.Length){
                src=lastBlocks[idx].text;oldTrn=lastBlocks[idx].translated;
                if(newTrn.Length>0)lastBlocks[idx].translated=newTrn;
            }
            if(String.IsNullOrEmpty(src)&&String.IsNullOrEmpty(engT)){status.Text="교정할 문단을 선택하거나 추가할 용어를 입력해주세요.";return;}
            string script=". \""+Path.Combine(root,"Glossary.ps1")+"\"; $r = Learn-GlossaryCorrection -Source ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+Convert.ToBase64String(Encoding.UTF8.GetBytes(src))+"'))) -OldTranslation ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+Convert.ToBase64String(Encoding.UTF8.GetBytes(oldTrn))+"'))) -NewTranslation ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+Convert.ToBase64String(Encoding.UTF8.GetBytes(newTrn))+"'))) -EngTerm ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+Convert.ToBase64String(Encoding.UTF8.GetBytes(engT))+"'))) -KorTerm ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+Convert.ToBase64String(Encoding.UTF8.GetBytes(korT))+"'))); $r | ConvertTo-Json -Compress";
            var psi=new ProcessStartInfo("powershell.exe","-NoLogo -NoProfile -ExecutionPolicy Bypass -Command \""+script.Replace("\"","\\\"")+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,StandardOutputEncoding=Encoding.UTF8};
            string outText="";
            using(var p=Process.Start(psi)){outText=p.StandardOutput.ReadToEnd();p.WaitForExit(5000);}
            if(lastBlocks.Length>0){
                history.Text=String.Join(Environment.NewLine+Environment.NewLine,lastBlocks.Select((b,i)=>(i+1)+". "+b.text+Environment.NewLine+b.translated));
                int keep=idx;PopulateCorrectionList(lastBlocks);if(keep>=0&&keep<correctionSelect.Items.Count)correctionSelect.SelectedIndex=keep;
                if(overlay.Visible)overlay.RenderPage();
            }
            engTermInput.Text="";korTermInput.Text="";
            UpdateGlossarySummary();
            string learned="";
            try{var res=json.Deserialize<Dictionary<string,object>>(outText.Trim());if(res!=null&&res.ContainsKey("learned"))learned=Convert.ToString(res["learned"]);}catch{}
            status.Text=String.IsNullOrEmpty(learned)?"번역 교정 및 캐시 반영 완료":"교정 완료 · 용어집 자동 학습: "+learned;
        }catch(Exception ex){status.Text="교정 저장 실패: "+ex.Message;}
    }
    void ChooseRegion(){
        if(selectingRegion)return;bool restore=Visible;selectingRegion=true;
        Point triggerCursor=Cursor.Position;
        if(rangeGuide!=null&&rangeGuide.Visible)rangeGuide.Hide();
        if(progressHud!=null&&progressHud.Visible)progressHud.Hide();
        Dismiss();Hide();
        var delay=new Timer{Interval=250};delay.Tick+=(s,e)=>{delay.Stop();delay.Dispose();try{Native.DwmFlush();using(picker=new RegionPicker(settings,triggerCursor)){
            if(picker.ShowDialog()==DialogResult.OK){
                if(picker.ReleaseToCursorRequested){
                    ReleaseFixedRegionToCursorMode();
                }else{
                    var r=picker.SelectedRegion;
                    var monitor=Screen.FromPoint(new Point(r.X+r.Width/2,r.Y+r.Height/2));
                    var b=monitor.Bounds;
                    settings.nearWidth=Math.Max(48,r.Width);
                    settings.nearHeight=Math.Max(48,r.Height);
                    settings.nearRegionX=(r.X-b.X)/(double)b.Width;
                    settings.nearRegionY=(r.Y-b.Y)/(double)b.Height;
                    settings.nearRegionW=settings.nearWidth/(double)b.Width;
                    settings.nearRegionH=settings.nearHeight/(double)b.Height;
                    settings.nearMonitor=monitor.DeviceName;
                    settings.nearFixed=true;
                    Save();
                    SyncRangeUi();
                    status.Text="📌 [영역 지정 중] F8 고정 영역을 "+settings.nearWidth+" × "+settings.nearHeight+" px로 저장했습니다. (영역 해제: Ctrl+Alt+F9)";
                    if(rangeGuide!=null)rangeGuide.FlashUntil=DateTime.UtcNow.AddSeconds(2.4);
                    if(progressHud!=null)progressHud.ShowToast(b,settings.hudPosition,"◈ F8 영역 지정 완료 ("+settings.nearWidth+"×"+settings.nearHeight+" px 고정)  ·  영역 해제: Ctrl+Alt+F9",false,2.2);
                }
            }
            else status.Text="영역 지정을 취소했습니다. 기존 설정("+((settings.nearFixed)?"영역 지정 중":"커서 기준 번역")+")이 유지됩니다.";
        }}catch(Exception ex){status.Text="영역 선택 실패: "+ex.Message;}finally{picker=null;selectingRegion=false;if(restore){Show();Activate();}}};delay.Start();
    }
    protected override void OnHandleCreated(EventArgs e){
        base.OnHandleCreated(e);int dark=1;Native.DwmSetWindowAttribute(Handle,20,ref dark,4);bool ok=true;
        uint[] keys={0x77,0x78,0x79,0x76}; // F8, F9, F10, F7
        for(int i=0;i<keys.Length;i++){
            ok &= Native.RegisterHotKey(Handle,i+1,0x4000,keys[i]);
            // RegisterHotKey requires an exact modifier match. Keep Shift held
            // in the game so its expanded tooltip remains visible at capture.
            ok &= Native.RegisterHotKey(Handle,i+6,0x4004,keys[i]);
        }
        ok &= Native.RegisterHotKey(Handle,5,0x4003,0x77);  // Ctrl+Alt+F8: 고정 영역 지정
        ok &= Native.RegisterHotKey(Handle,10,0x4003,0x78); // Ctrl+Alt+F9: 영역 지정 해제 -> 커서 기준 번역 전환
        if(!ok)status.Text="일부 단축키 등록 실패: 다른 프로그램의 F7~F10 / Ctrl+Alt+F8~F9 단축키 충돌을 확인하세요.";
    }
    protected override void WndProc(ref Message m){
        if(m.Msg==0x312){
            int id=m.WParam.ToInt32();
            if(id>=6&&id<=9)id-=5;
            if(id==10){
                if(selectingRegion&&picker!=null){picker.ReleaseToCursorRequested=true;picker.DialogResult=DialogResult.OK;picker.Close();}
                else ReleaseFixedRegionToCursorMode();
            }else if(id>=1&&id<=5){
                if(selectingRegion){if(id==3&&picker!=null)picker.Close();}
                else if(id==5)ChooseRegion();
                else if(id==3)Dismiss();
                else if(id==4){if(overlay.Visible){overlay.NextPage();shown=DateTime.UtcNow;}}
                else BeginCapture(id==1);
            }
        }
        base.WndProc(ref m);
    }
    void Save(){
        string nextBackend=backend.SelectedIndex==4?"exaone":backend.SelectedIndex==3?"gemma":backend.SelectedIndex==2?"hymt":backend.SelectedIndex==1?"google-official":"google-free";
        if(settings.backend!=nextBackend)Dismiss();
        settings.backend=nextBackend;settings.fontSize=(int)font.Value;
        settings.tooltipLine=tooltipLine.Checked;settings.tooltipLineOpacity=(int)lineOpacity.Value;
        settings.tooltipOpacity=(int)tooltipOpacity.Value;settings.obsTooltipOpacity=(int)obsTooltipOpacity.Value;
        settings.apiKey=key.Text.Length==0?"":"dpapi:"+Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Text),null,System.Security.Cryptography.DataProtectionScope.CurrentUser));
        File.WriteAllText(Path.Combine(root,"settings.json"),json.Serialize(settings),Encoding.UTF8); status.Text="설정을 저장했습니다.";
        overlay.ConfigureStyle(settings);
        try{if(IsLocalBackend(settings.backend))localEngine.Start(settings.backend);else localEngine.Stop();}catch(Exception ex){status.Text="설정 저장됨 · 엔진 시작 실패: "+ex.Message;}
    }
    static bool IsLocalBackend(string name){return name=="hymt"||name=="gemma"||name=="exaone";}
    void Dismiss(){generation++;overlay.Hide();if(rangeGuide!=null){rangeGuide.CaptureFlashUntil=DateTime.MinValue;if(!rangeGuide.Pinned)rangeGuide.Hide();}if(progressHud!=null){progressHud.ToastUntil=DateTime.MinValue;progressHud.Hide();}CancelWorker();status.Text="번역을 숨겼습니다. F8/F9로 다시 번역하세요.";}
    void EnsureOcrHost(){if(workerHost!=null&&!workerHost.HasExited)return;if(workerHost!=null)workerHost.Dispose();var psi=new ProcessStartInfo("powershell.exe","-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+Path.Combine(root,"Worker.ps1")+"\" -Listen");psi.UseShellExecute=false;psi.CreateNoWindow=true;psi.RedirectStandardInput=true;workerHost=Process.Start(psi);helpers.Add(workerHost);}
    static string AsciiJson(string value){var text=new StringBuilder();foreach(char c in value)if(c>127)text.Append("\\u"+((int)c).ToString("x4"));else text.Append(c);return text.ToString();}
    void FinishJob(){worker=null;CleanTemp();}
    void CancelWorker(){if(worker!=null){try{if(!worker.HasExited)worker.Kill();}catch{}worker.Dispose();if(worker==workerHost)workerHost=null;worker=null;}CleanTemp();}
    void CleanTemp(){try{if(temp!=null && Directory.Exists(temp))Directory.Delete(temp,true);}catch{}}
    void BeginCapture(bool near){
        if(worker!=null){status.Text="번역 중입니다. F10으로 취소할 수 있습니다.";return;}
        try {
            started=DateTime.UtcNow;
            captureNearFlag=near;
            if(IsLocalBackend(settings.backend)){
                localEngine.Start(settings.backend);
                if(!localEngine.IsReady){
                    status.Text="번역 모델 준비 중입니다. GPU 준비 완료 후 다시 눌러주세요.";
                    if(progressHud!=null)progressHud.ShowToast(Screen.FromPoint(Cursor.Position).Bounds,settings.hudPosition,"⏳ AI 번역 모델 GPU 준비 중… 잠시 후 다시 눌러주세요.",true,2.2);
                    return;
                }
            }
            target=Native.GetForegroundWindow(); if(target==Handle){status.Text="먼저 게임 창을 선택한 뒤 단축키를 눌러주세요.";return;}
            if(rangeGuide!=null){rangeGuide.CaptureFlashUntil=DateTime.MinValue;if(rangeGuide.Visible)rangeGuide.Hide();}
            if(progressHud!=null&&progressHud.Visible)progressHud.Hide();
            overlay.Hide();Native.DwmFlush();generation++;jobGeneration=generation;nearMode=near&&!settings.nearFixed;anchor=Cursor.Position;
            display=Screen.FromPoint(anchor).Bounds; Native.RECT wr;
            var area=display;
            if(Native.GetWindowRect(target,out wr)) {var bounds=Rectangle.FromLTRB(wr.L,wr.T,wr.R,wr.B);var intersection=Rectangle.Intersect(display,bounds);if(intersection.Width>200 && intersection.Height>150) area=intersection;}
            if(near&&settings.nearFixed&&settings.nearRegionW>0){
                var fixedMon=Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==settings.nearMonitor)??Screen.FromPoint(anchor);
                display=fixedMon.Bounds;
                var fixedRect=new Rectangle(
                    display.X+(int)Math.Round(settings.nearRegionX*display.Width),
                    display.Y+(int)Math.Round(settings.nearRegionY*display.Height),
                    Math.Max(48,(int)Math.Round(settings.nearRegionW*display.Width)),
                    Math.Max(48,(int)Math.Round(settings.nearRegionH*display.Height)));
                capture=Rectangle.Intersect(display,fixedRect);
            }else{
                var nearRect=Settings.GetCursorRelativeBox(anchor,settings.nearWidth,settings.nearHeight,settings.nearAnchor);
                capture=near ? Rectangle.Intersect(area,nearRect) : area;
            }
            if(capture.Width<10||capture.Height<10) throw new Exception("캡처 영역이 없습니다. 게임 창 안에 마우스를 놓거나 영역 지정을 확인하세요.");
            temp=Path.Combine(Path.GetTempPath(),"GuildLens-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);output=Path.Combine(temp,"result.json");
            imageScale=Math.Min(1,2400.0/Math.Max(capture.Width,capture.Height));
            using(var image=new Bitmap(capture.Width,capture.Height))using(var g=Graphics.FromImage(image)){
                g.CopyFromScreen(capture.Location,Point.Empty,capture.Size);
                if(imageScale<1)using(var resized=new Bitmap(image,new Size((int)(capture.Width*imageScale),(int)(capture.Height*imageScale))))resized.Save(Path.Combine(temp,"capture.png"),ImageFormat.Png);
                else image.Save(Path.Combine(temp,"capture.png"),ImageFormat.Png);
            }
            if(near && settings.showCaptureFlash && rangeGuide!=null){
                rangeGuide.ShowCaptureFlash(capture,anchor,settings.nearFixed,settings.nearAnchor,1.45);
            }
            if(progressHud!=null)progressHud.RenderProgress(display,settings.hudPosition,near,settings.nearFixed,settings.sourceLang,0.0);
            EnsureOcrHost();worker=workerHost;worker.StandardInput.WriteLine(AsciiJson(json.Serialize(new{inputPath=Path.Combine(temp,"capture.png"),outputPath=output,ocrOnly=false})));worker.StandardInput.Flush();status.Text="화면 인식 / 번역 중… F10으로 취소";
        }catch(Exception ex){if(progressHud!=null&&progressHud.Visible)progressHud.Hide();CancelWorker();status.Text="캡처 실패: "+ex.Message;tray.ShowBalloonTip(4000,"DalvitLens",status.Text,ToolTipIcon.Warning);}
    }
    void Tick(object sender,EventArgs e){
        if(engineState!=null&&IsLocalBackend(settings.backend)&&engineState.Text!=localEngine.State)engineState.Text=localEngine.State;
        if(rangeGuide!=null){
            var cBox=rangeGuide.CaptureFlashBox;
            bool showCaptureFlash=!selectingRegion && settings.showCaptureFlash && cBox.Width>=40 && ((worker!=null && captureNearFlag && !overlay.Visible) || DateTime.UtcNow<rangeGuide.CaptureFlashUntil);
            bool showGuide=!selectingRegion && worker==null && !overlay.Visible && (rangeGuide.Pinned || DateTime.UtcNow<rangeGuide.FlashUntil);
            if(showCaptureFlash){
                rangeGuide.UpdateGuide(cBox,rangeGuide.CaptureFlashCursor,cBox.Width,cBox.Height,rangeGuide.CaptureFlashFixed,rangeGuide.CaptureFlashAnchor,true);
            }else if(showGuide){
                Point cur=Cursor.Position;
                var mon=Screen.FromPoint(cur).Bounds;
                var raw=Settings.GetCursorRelativeBox(cur,settings.nearWidth,settings.nearHeight,settings.nearAnchor);
                if(settings.nearFixed&&settings.nearRegionW>0){
                    var fixedMon=Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==settings.nearMonitor)??Screen.FromPoint(cur);
                    mon=fixedMon.Bounds;
                    raw=new Rectangle(
                        mon.X+(int)Math.Round(settings.nearRegionX*mon.Width),
                        mon.Y+(int)Math.Round(settings.nearRegionY*mon.Height),
                        Math.Max(48,(int)Math.Round(settings.nearRegionW*mon.Width)),
                        Math.Max(48,(int)Math.Round(settings.nearRegionH*mon.Height)));
                }
                var clamped=Rectangle.Intersect(mon,raw);
                if(clamped.Width>=40&&clamped.Height>=40)rangeGuide.UpdateGuide(clamped,cur,settings.nearWidth,settings.nearHeight,settings.nearFixed,settings.nearAnchor,false);
            }else if(rangeGuide.Visible){
                rangeGuide.Hide();
            }
        }
        if(worker!=null){
            if(DateTime.UtcNow-started>TimeSpan.FromSeconds(120)){
                if(progressHud!=null)progressHud.ShowToast(display,settings.hudPosition,"⚠ 번역 시간이 초과됐습니다. 모델 상태를 확인하세요.",true,2.5);
                CancelWorker();status.Text="번역 시간이 초과됐습니다. 연결과 모델 설정을 확인하세요.";tray.ShowBalloonTip(4000,"DalvitLens",status.Text,ToolTipIcon.Warning);return;
            }
            if(!File.Exists(output)&&!worker.HasExited){
                if(progressHud!=null)progressHud.RenderProgress(display,settings.hudPosition,captureNearFlag,settings.nearFixed,settings.sourceLang,(DateTime.UtcNow-started).TotalSeconds);
                return;
            }
            if(progressHud!=null&&progressHud.Visible)progressHud.Hide();
            try {
                if(!File.Exists(output))throw new Exception("OCR 처리 결과가 없습니다. Worker.ps1 실행 환경을 확인하세요.");
                var result=json.Deserialize<Result>(File.ReadAllText(output,Encoding.UTF8));
                if(result.blocks==null)result.blocks=new Block[0];
                foreach(var b in result.blocks){b.x/=imageScale;b.y/=imageScale;b.w/=imageScale;b.h/=imageScale;}
                history.Text=String.Join(Environment.NewLine+Environment.NewLine,result.blocks.Select((b,i)=>(i+1)+". "+b.text+Environment.NewLine+b.translated));
                PopulateCorrectionList(result.blocks);
                UpdateGlossarySummary();
                string langEmpty=settings.sourceLang=="ja"?"일본어 텍스트를 찾지 못했습니다. F9로 범위를 넓히거나 원문 언어 스위치를 확인하세요.":settings.sourceLang=="zh"?"중국어 텍스트를 찾지 못했습니다. F9로 범위를 넓히거나 원문 언어 스위치를 확인하세요.":"영문을 찾지 못했습니다. F9로 범위를 넓히거나 원문 언어(영/일/중) 스위치를 확인하세요.";
                if(result.blocks.Length==0){
                    status.Text=String.IsNullOrEmpty(result.error)?langEmpty:result.error;
                    if(captureNearFlag&&settings.showCaptureFlash&&rangeGuide!=null)rangeGuide.CaptureFlashUntil=DateTime.UtcNow.AddSeconds(1.8);
                    if(progressHud!=null)progressHud.ShowToast(display,settings.hudPosition,"⚠ "+(String.IsNullOrEmpty(result.error)?"영역 내 원문 텍스트를 찾지 못했습니다 (F9 전체 권장)":result.error),true,2.2);
                    tray.ShowBalloonTip(5000,"DalvitLens",status.Text,ToolTipIcon.Warning);
                }
                else if(jobGeneration==generation && Native.GetForegroundWindow()==target && (!nearMode || Distance(Cursor.Position,anchor)<480)){
                    if(rangeGuide!=null&&DateTime.UtcNow<rangeGuide.CaptureFlashUntil)rangeGuide.CaptureFlashUntil=DateTime.UtcNow.AddSeconds(0.55);
                    overlay.ConfigureStyle(settings);overlay.Present(display,result.blocks,capture,anchor,nearMode);shown=DateTime.UtcNow;
                    status.Text=String.IsNullOrEmpty(result.error)?result.blocks.Length+"개 문단 · "+Math.Round((DateTime.UtcNow-started).TotalMilliseconds)+" ms · 저장된 번역 "+result.cacheHits+"개":result.error;
                    if(!String.IsNullOrEmpty(result.error))tray.ShowBalloonTip(4000,"DalvitLens",result.error,ToolTipIcon.Warning);
                }else status.Text="화면/마우스 위치가 바뀌어 표시를 생략했습니다. 번역 기록은 남아 있습니다.";
            }catch(Exception ex){status.Text="처리 실패: "+ex.Message;tray.ShowBalloonTip(4000,"DalvitLens",status.Text,ToolTipIcon.Warning);}
            if(worker.HasExited)CancelWorker();else FinishJob();
        }else if(progressHud!=null){
            if(DateTime.UtcNow<progressHud.ToastUntil){
                // Keep active toast on screen
            }else if(settings.nearFixed && !selectingRegion && !overlay.Visible){
                var fixedMon=Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==settings.nearMonitor)??Screen.FromPoint(Cursor.Position);
                progressHud.RenderFixedModeBanner(fixedMon.Bounds,settings.hudPosition,settings.nearWidth,settings.nearHeight);
            }else if(progressHud.Visible){
                progressHud.Hide();
            }
        }
        if(overlay.Visible && (Native.GetForegroundWindow()!=target || Native.IsIconic(target) || (nearMode && Distance(Cursor.Position,anchor)>260) || DateTime.UtcNow-shown>TimeSpan.FromSeconds(45)))overlay.Hide();
    }
    static double Distance(Point a,Point b){return Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));}
}
static class Program {
    [STAThread] static void Main(string[] args){
        Native.SetProcessDPIAware();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        using(var mutex=new System.Threading.Mutex(true,"Local\\DalbitGuildLens",out created)){
            if(!created){MessageBox.Show("DalvitLens가 이미 실행 중입니다. 작업 표시줄 트레이를 확인하세요.");return;}
            try { Application.Run(new MainForm()); }
            catch(Exception ex) {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"startup-error.txt"),ex.ToString(),Encoding.UTF8);
                MessageBox.Show("시작 중 오류가 발생했습니다. 폴더의 startup-error.txt를 확인하세요.\n"+ex.Message,"DalvitLens");
            }
        }
    }
    static bool created;
}
