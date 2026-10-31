using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
class ModelSwitchChecks {
    static string Ascii(string s){var b=new StringBuilder();foreach(char c in s)if(c>127)b.Append("\\u"+((int)c).ToString("x4"));else b.Append(c);return b.ToString();}
    [STAThread] static void Main(){try{Run();}catch(Exception ex){Console.WriteLine(ex.GetType().Name+": "+ex.Message);Console.WriteLine(ex.StackTrace);Environment.ExitCode=1;}}
    static void Run(){
        string root=AppDomain.CurrentDomain.BaseDirectory,configPath=Path.Combine(root,"settings.json");
        byte[] original=File.ReadAllBytes(configPath);var json=new JavaScriptSerializer();var config=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(configPath));
        string dir=Path.Combine(root,"benchmark","switch-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        int armor=100000+(int)(DateTime.UtcNow.Ticks%900000);string input=Path.Combine(dir,"test.png");
        using(var image=new Bitmap(1100,180))using(var g=Graphics.FromImage(image))using(var font=new Font("Arial",20)){g.Clear(Color.FromArgb(22,20,38));g.DrawString("Gain +"+armor+" Armor for 17 seconds.",font,Brushes.White,30,40);image.Save(input,ImageFormat.Png);}
        Process worker=null;var report=new List<object>();
        using(var lifetime=new HelperLifetime())using(var engine=new LocalTranslationEngine(root,lifetime))try{
            var psi=new ProcessStartInfo("powershell.exe","-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+Path.Combine(root,"Worker.ps1")+"\" -Listen"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true};worker=Process.Start(psi);lifetime.Add(worker);
            int previousPid=0;int phase=0;
            foreach(string kind in new[]{"hymt","gemma","exaone","hymt"}){
                phase++;config["backend"]=kind;File.WriteAllText(configPath,json.Serialize(config),new UTF8Encoding(false));
                engine.Start(kind);var load=Stopwatch.StartNew();while(!engine.IsReady&&load.Elapsed.TotalSeconds<35)Thread.Sleep(40);if(!engine.IsReady)throw new Exception(engine.State);
                int pid=int.Parse(File.ReadAllText(Path.Combine(root,"local-server.pid")));
                if(previousPid!=0&&previousPid!=pid){try{using(var old=Process.GetProcessById(previousPid)){if(!old.HasExited)throw new Exception("Previous model is still running");}}catch(ArgumentException){}}
                previousPid=pid;
                using(var web=new System.Net.WebClient()){
                    web.Headers["Authorization"]="Bearer "+File.ReadAllText(Path.Combine(root,"local-model.key")).Trim();
                    string models=web.DownloadString("http://127.0.0.1:18766/v1/models");string expected=kind=="exaone"?"dalvit-exaone-7.8b":kind=="gemma"?"dalvit-gemma-4b":"dalvit-hymt2-1.8b";if(!models.Contains(expected))throw new Exception("Wrong active model");
                }
                string output=Path.Combine(dir,phase+".json");var watch=Stopwatch.StartNew();worker.StandardInput.WriteLine(Ascii(json.Serialize(new{inputPath=input,outputPath=output,ocrOnly=false})));worker.StandardInput.Flush();
                while(!File.Exists(output)&&watch.Elapsed.TotalSeconds<30){if(worker.HasExited)throw new Exception("OCR worker exited");Thread.Sleep(10);}if(!File.Exists(output))throw new Exception("Worker timed out");watch.Stop();
                var result=json.Deserialize<Result>(File.ReadAllText(output));if(!String.IsNullOrEmpty(result.error))throw new Exception(result.error);
                if(result.blocks==null||result.blocks.Length!=1)throw new Exception("Expected one OCR block");
                string text=result.blocks[0].translated;string compact=text.Replace(",","");if(!compact.Contains(armor.ToString())||!compact.Contains("17"))throw new Exception("Numeric content lost");
                if(!System.Text.RegularExpressions.Regex.IsMatch(text,"[가-힣]"))throw new Exception("Missing Korean translation");
                int expectedHits=phase<=3?0:1;if(result.cacheHits!=expectedHits)throw new Exception("Caches are not model-specific");
                report.Add(new{phase=phase,model=kind,pid=pid,workerMs=result.totalMs,wallMs=Math.Round(watch.Elapsed.TotalMilliseconds),cacheHits=result.cacheHits,text=text});
                Console.WriteLine("PASS phase "+phase+": "+kind+", cache hits "+result.cacheHits+", "+Math.Round(watch.Elapsed.TotalMilliseconds)+" ms -> "+text);
            }
            File.WriteAllText(Path.Combine(root,"model-switch-checks.json"),json.Serialize(report),Encoding.UTF8);
            Console.WriteLine("PASS: Hy-MT2 -> Gemma -> EXAONE -> Hy-MT2; one model at a time; persistent OCR; separate caches; numeric preservation.");
        }finally{
            if(worker!=null){try{if(!worker.HasExited){worker.StandardInput.WriteLine("quit");worker.StandardInput.Flush();if(!worker.WaitForExit(1500))worker.Kill();}}catch{}worker.Dispose();}
            File.WriteAllBytes(configPath,original);
        }
    }
}
