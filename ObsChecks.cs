using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
class ObsChecks {
    static Bitmap Fetch(){using(var w=new WebClient())using(var m=new MemoryStream(w.DownloadData(ObsOverlayServer.Url+"overlay.png")))return new Bitmap(m);}
    static void Main(){
        using(var server=new ObsOverlayServer()){
            using(var b=Fetch()){if(b.Width!=1920||b.Height!=1080||b.GetPixel(100,100).A!=0)throw new Exception("Not transparent 1080p");}
            using(var large=new Bitmap(3840,2160))using(var g=Graphics.FromImage(large))using(var small=new Bitmap(1920,1080))using(var sg=Graphics.FromImage(small))using(var m=new MemoryStream()){
                g.FillRectangle(Brushes.Aquamarine,1000,600,400,200);sg.DrawImage(large,new Rectangle(0,0,1920,1080));small.Save(m,ImageFormat.Png);server.SetFrame(m.ToArray());
            }
            using(var b=Fetch()){if(b.GetPixel(550,350).A!=255||b.GetPixel(499,299).A!=0)throw new Exception("4K mapping incorrect");b.Save("obs-verification.png");}
            using(var w=new WebClient()){
                if(!w.DownloadString(ObsOverlayServer.Url).Contains("visibility='hidden'"))throw new Exception("Missing disconnect clearing");
                try{w.DownloadString(ObsOverlayServer.Url+"settings.json");throw new Exception("Settings exposed");}catch(WebException e){if(((HttpWebResponse)e.Response).StatusCode!=HttpStatusCode.NotFound)throw;}
            }
            server.Clear();using(var b=Fetch()){if(b.GetPixel(550,350).A!=0)throw new Exception("Hide failed");}
            Console.WriteLine("PASS: transparent 1920x1080, 4K half-scale coordinates, live frame, clear, private paths denied.");
        }
    }
}
