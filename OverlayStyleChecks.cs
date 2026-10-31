using System;
using System.Drawing;
using System.Drawing.Imaging;
class OverlayStyleChecks {
    [STAThread] static void Main(){Native.SetProcessDPIAware();
        using(var overlay=new Overlay())using(var frame=new Bitmap(640,480,PixelFormat.Format32bppArgb))using(var g=Graphics.FromImage(frame)){
            overlay.Bounds=new Rectangle(0,0,640,480);
            string text="1. 방어력이 50 증가합니다.\n8초 동안 지속됩니다.";int height;
            using(var font=new Font("맑은 고딕",overlay.FontSize))height=(int)Math.Ceiling(g.MeasureString(text,font,312).Height)+30;
            overlay.cards.Add(new Card{source=new Rectangle(30,40,100,60),bounds=new Rectangle(180,180,340,height),number=1,text=text});
            overlay.DrawContent(g);
            if(frame.GetPixel(90,40).A!=64)throw new Exception("Guide opacity must be 64/255");
            if(frame.GetPixel(90,39).A!=0||frame.GetPixel(90,41).A!=0)throw new Exception("Guide is thicker than one pixel");
            var shadow=frame.GetPixel(177,220).A;if(shadow<=0||shadow>=100)throw new Exception("Soft shadow missing or too strong");
            if(frame.GetPixel(10,300).A!=0)throw new Exception("Background is not transparent");
            overlay.ApplyAlphaFrame(frame);
            frame.Save("overlay-style-verification.png");
            Console.WriteLine("PASS: 25% guide alpha, 1px guide, soft shadow, transparent background, native per-pixel alpha upload.");
        }
    }
}
