using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Web.Script.Serialization;
class TooltipStyleChecks {
    static Bitmap Frame(Overlay o,bool obs){var b=new Bitmap(640,400,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(b))o.DrawContent(g,obs);return b;}
    [STAThread] static void Main(){try{Check();}catch(Exception e){Console.WriteLine(e.GetType().FullName);Console.WriteLine(e.Message);Console.WriteLine(e.StackTrace);Environment.ExitCode=1;}}
    static void Check(){
        var json=new JavaScriptSerializer();var defaults=json.Deserialize<Settings>("{\"fontSize\":14}");
        if(!defaults.tooltipLine||defaults.tooltipOpacity!=100||defaults.obsTooltipOpacity!=48)throw new Exception("Old settings defaults lost");
        using(var o=new Overlay()){
            o.cards.Add(new Card{bounds=new Rectangle(180,140,300,140),source=new Rectangle(20,20,100,30),number=1,text="1. Tooltip text"});
            o.ConfigureStyle(defaults);
            using(var desktop=Frame(o,false))using(var obs=Frame(o,true)){
                if(desktop.GetPixel(300,250).A!=255||obs.GetPixel(300,250).A>=255||obs.GetPixel(300,250).A<122)throw new Exception("Default background alpha changed");
            }
            defaults.tooltipOpacity=0;defaults.obsTooltipOpacity=0;defaults.tooltipLineOpacity=100;defaults.tooltipLine=true;o.ConfigureStyle(defaults);
            using(var line=Frame(o,false)){
                defaults.tooltipLine=false;o.ConfigureStyle(defaults);
                using(var off=Frame(o,false))if(line.GetPixel(300,140).A<=off.GetPixel(300,140).A)throw new Exception("Line OFF did not remove outline");
                defaults.tooltipLine=true;defaults.tooltipLineOpacity=25;o.ConfigureStyle(defaults);
                using(var faint=Frame(o,false))if(faint.GetPixel(300,140).A>=line.GetPixel(300,140).A)throw new Exception("Line opacity did not change");
            }
            defaults.tooltipOpacity=25;defaults.obsTooltipOpacity=75;o.ConfigureStyle(defaults);
            using(var desktop=Frame(o,false))using(var obs=Frame(o,true)){
                // Soft shadows beneath the panel also contribute to final alpha.
                if(desktop.GetPixel(300,250).A<64||desktop.GetPixel(300,250).A>=obs.GetPixel(300,250).A||obs.GetPixel(300,250).A<191||obs.GetPixel(300,250).A>=255)throw new Exception("Independent opacity failed");
            }
        }
        Console.WriteLine("PASS: legacy settings defaults, LINE ON/OFF, line alpha, independent desktop/OBS background alpha.");
    }
}
