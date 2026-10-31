using System;
using System.Drawing;
using System.Linq;
class LayoutChecks {
    static void Check(Size size,Block[] blocks,Point origin,Graphics g,Font font) {
        int hidden;
        var capture=new Rectangle(origin,size);
        for(int start=0;start<blocks.Length;start++) {
            var cards=CardLayout.Arrange(size,blocks,capture,origin,g,font,out hidden,start);
            if(cards.Count+hidden!=blocks.Length)throw new Exception("Lost blocks");
            foreach(var c in cards) {
                if(!new Rectangle(Point.Empty,size).Contains(c.bounds))throw new Exception("Off screen");
                foreach(var b in blocks) {
                    var source=Rectangle.Inflate(new Rectangle((int)b.x,(int)b.y,Math.Max(28,(int)b.w),Math.Max(20,(int)b.h)),10,10);
                    if(source.IntersectsWith(c.bounds))throw new Exception("Translation covers source");
                }
                foreach(var other in cards)if(other!=c && Rectangle.Inflate(c.bounds,10,10).IntersectsWith(other.bounds))throw new Exception("Cards overlap");
                if(g.MeasureString(c.text,font,c.bounds.Width-28).Height>c.bounds.Height-24)throw new Exception("Clipped text");
            }
        }
    }
    static void Main(){
        using(var bmp=new Bitmap(1920,1080))using(var g=Graphics.FromImage(bmp))using(var font=new Font("맑은 고딕",13)) {
            var blocks=new[]{
                new Block{x=281,y=190,w=200,h=22,translated="2026년 10월 21일"},
                new Block{x=263,y=395,w=540,h=42,translated="이것은 내가 가장 좋아하는 자동 전투 로그라이크일 것입니다.\n레트로메이션"},
                new Block{x=263,y=475,w=415,h=42,translated="내가 오랫동안 플레이한 최고의 자동 전투 게임 중 하나입니다!\n원더봇"},
                new Block{x=263,y=556,w=540,h=45,translated="올해 가장 큰 로그라이크 중 하나이며 갑자기 등장했습니다."},
                new Block{x=263,y=679,w=90,h=22,translated="게임 정보"},
                new Block{x=747,y=734,w=60,h=20,translated="방어력 50 증가. 8초 동안 지속됩니다."}};
            Check(new Size(1039,1073),blocks,Point.Empty,g,font);
            Check(new Size(1920,1080),blocks,new Point(-1920,0),g,font);
            Check(new Size(800,600),new[]{new Block{x=0,y=0,w=800,h=600,translated="빈 공간이 없는 경우"}},Point.Empty,g,font);
            int hidden;var cards=CardLayout.Arrange(new Size(1920,1080),blocks,new Rectangle(0,0,1920,1080),Point.Empty,g,font,out hidden);
            if(cards.Count!=blocks.Length)throw new Exception("Spacious screen should show all blocks");
            g.Clear(Color.FromArgb(32,44,60));
            foreach(var b in blocks)g.DrawRectangle(Pens.Gray,(float)b.x,(float)b.y,(float)b.w,(float)b.h);
            foreach(var c in cards){g.FillRectangle(Brushes.Black,c.bounds);g.DrawRectangle(Pens.Aquamarine,c.bounds);g.DrawString(c.text,font,Brushes.White,new RectangleF(c.bounds.X+14,c.bounds.Y+12,c.bounds.Width-28,c.bounds.Height-24));}
            bmp.Save("layout-verification.png");
            Console.WriteLine("PASS: source avoidance, card spacing, text fit, pagination starts, negative monitor origin, no-space fallback.");
        }
    }
}
