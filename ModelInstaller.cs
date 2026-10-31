using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DalvitInstaller {
    internal static class InstallerTheme {
        internal static readonly Color Primary = Color.FromArgb(179, 115, 255);
        internal static readonly Color Secondary = Color.FromArgb(90, 191, 255);
        internal static readonly Color Gold = Color.FromArgb(255, 214, 74);
        internal static readonly Color Green = Color.FromArgb(72, 228, 152);
        internal static readonly Color Warn = Color.FromArgb(255, 138, 112);
        internal static readonly Color Card = Color.FromArgb(22, 20, 38);
        internal static readonly Color Surface = Color.FromArgb(14, 12, 25);
        internal static readonly Color Field = Color.FromArgb(34, 30, 52);
        internal static readonly Color Text = Color.FromArgb(241, 235, 252);
        internal static readonly Color Muted = Color.FromArgb(183, 175, 199);
        internal static readonly Color Outline = Color.FromArgb(69, 59, 90);

        internal static GraphicsPath Rounded(Rectangle r, int radius) {
            var p = new GraphicsPath();
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal class InstallerButton : Button {
        internal bool Filled;
        public InstallerButton() {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20, 10, 20, 10);
            Margin = new Padding(6, 0, 0, 0);
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
            Font = new Font("맑은 고딕", 9.75f, FontStyle.Bold);
        }
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? InstallerTheme.Surface : Parent.BackColor);
            Color fill = !Enabled
                ? Color.FromArgb(42, 38, 58)
                : (Filled ? InstallerTheme.Primary : InstallerTheme.Field);
            Color fg = !Enabled
                ? Color.FromArgb(120, 114, 138)
                : (Filled ? InstallerTheme.Surface : InstallerTheme.Text);
            using (var p = InstallerTheme.Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 20))
            using (var b = new SolidBrush(fill))
            using (var pen = new Pen(Focused ? InstallerTheme.Primary : InstallerTheme.Outline, Focused ? 2 : 1)) {
                e.Graphics.FillPath(b, p);
                if (!Filled || !Enabled) e.Graphics.DrawPath(pen, p);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal class SmoothProgressBar : Control {
        double percent;
        public double Percent {
            get { return percent; }
            set { percent = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }
        public SmoothProgressBar() {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Height = 16;
            BackColor = InstallerTheme.Card;
        }
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? InstallerTheme.Card : Parent.BackColor);
            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var tp = InstallerTheme.Rounded(track, 8))
            using (var tb = new SolidBrush(InstallerTheme.Field))
            using (var pen = new Pen(InstallerTheme.Outline, 1)) {
                e.Graphics.FillPath(tb, tp);
                e.Graphics.DrawPath(pen, tp);
            }
            int fw = (int)Math.Round((Width - 2) * (percent / 100.0));
            if (fw >= 6) {
                var fillRect = new Rectangle(1, 1, fw, Height - 3);
                using (var fp = InstallerTheme.Rounded(fillRect, 7))
                using (var grad = new LinearGradientBrush(fillRect, InstallerTheme.Primary, InstallerTheme.Secondary, LinearGradientMode.Horizontal)) {
                    e.Graphics.FillPath(grad, fp);
                }
            }
        }
    }

    internal class GpuCheckResult {
        internal bool HasNvidia;
        internal string Name = "감지된 그래픽카드 없음";
        internal double VramGb;
        internal string Summary {
            get {
                if (HasNvidia && VramGb > 0.5)
                    return Name + " (" + VramGb.ToString("0.0") + " GB VRAM · 로컬 AI GPU 가속 지원)";
                if (HasNvidia)
                    return Name + " (로컬 AI GPU 가속 지원)";
                return Name + " (NVIDIA CUDA 미지원 · Google 간편 연결 모드 권장)";
            }
        }
    }

    internal static class EnvironmentChecker {
        internal static bool IsAsciiPath(string path) {
            if (String.IsNullOrEmpty(path)) return false;
            foreach (char c in path) if (c > 127) return false;
            return true;
        }

        internal static string GetDefaultInstallDir() {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!String.IsNullOrEmpty(localApp) && IsAsciiPath(localApp))
                return Path.Combine(localApp, "DalvitLens");
            return @"C:\DalvitLens";
        }

        internal static GpuCheckResult DetectGpu() {
            var res = new GpuCheckResult();
            // 1. Prefer nvidia-smi for exact >4GB VRAM reporting
            try {
                var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits") {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi)) {
                    string line = p.StandardOutput.ReadToEnd().Trim().Split('\n')[0].Trim();
                    p.WaitForExit(1500);
                    if (p.ExitCode == 0 && line.Length > 0) {
                        var parts = line.Split(',');
                        res.HasNvidia = true;
                        res.Name = parts[0].Trim();
                        double mb;
                        if (parts.Length > 1 && Double.TryParse(parts[1].Trim(), out mb) && mb > 0)
                            res.VramGb = mb / 1024.0;
                        return res;
                    }
                }
            } catch { }

            // 2. Fallback to WMI Win32_VideoController
            try {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController")) {
                    foreach (ManagementObject mo in searcher.Get()) {
                        string name = Convert.ToString(mo["Name"] ?? "").Trim();
                        if (name.Length == 0) continue;
                        double gb = 0;
                        try {
                            ulong ram = Convert.ToUInt64(mo["AdapterRAM"]);
                            if (ram > 0) gb = ram / (1024.0 * 1024.0 * 1024.0);
                        } catch { }
                        bool nv = name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (nv || res.Name == "감지된 그래픽카드 없음") {
                            res.Name = name;
                            res.HasNvidia = nv;
                            res.VramGb = gb;
                            if (nv) break;
                        }
                    }
                }
            } catch { }
            return res;
        }

        internal static bool CheckEnglishOcrInstalled() {
            try {
                string cmd = "Add-Type -AssemblyName System.Runtime.WindowsRuntime; "
                    + "$null=[Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]; "
                    + "$null=[Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]; "
                    + "if([Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('en-US'))){'OK'}else{'NO'}";
                var psi = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + cmd + "\"") {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi)) {
                    string outStr = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(3500);
                    return outStr.Contains("OK");
                }
            } catch { return true; }
        }
    }

    internal class ModelSpec {
        internal string Kind;
        internal string Title;
        internal string Badge;
        internal string Description;
        internal string ApproxSizeText;
        internal double ApproxGb;
        internal int RecommendedVramGb;
        internal string HfRepo;
        internal string HfFilePattern;
        internal string LocalFileName;
        internal string LicenseFileInRepo;
        internal string LocalLicenseFile;
        internal string LicenseLabel;
        internal string ExtraTemplateUrl;
        internal string ExtraTemplateLocal;
        internal string ManifestFile;
    }

    internal class DownloadProgressInfo {
        internal string StepTitle = "";
        internal string DetailText = "";
        internal double Percent;
    }

    internal static class ModelCatalog {
        internal const double RuntimeApproxGb = 0.64;

        internal static readonly ModelSpec[] All = new[] {
            new ModelSpec {
                Kind = "exaone",
                Title = "EXAONE 3.5 7.8B  (Q4_K_M)",
                Badge = "기본 추천 · 최고 번역 품질",
                Description = "한국어 자연스러움과 RPG 조건문·수치 이해도가 가장 뛰어난 주력 모델 (권장 VRAM 6GB+)",
                ApproxSizeText = "약 4.5 GB",
                ApproxGb = 4.45,
                RecommendedVramGb = 6,
                HfRepo = "LGAI-EXAONE/EXAONE-3.5-7.8B-Instruct-GGUF",
                HfFilePattern = "^EXAONE-3\\.5-7\\.8B-Instruct-Q4_K_M\\.gguf$",
                LocalFileName = "EXAONE-3.5-7.8B-Q4_K_M.gguf",
                LicenseFileInRepo = "LICENSE",
                LocalLicenseFile = "LICENSE-EXAONE.txt",
                LicenseLabel = "EXAONE AI Model License 1.1 - NC (비상업적 용도 허용)",
                ManifestFile = "exaone-manifest.json"
            },
            new ModelSpec {
                Kind = "hymt",
                Title = "Hy-MT2 1.8B  (Q4_K_M)",
                Badge = "초경량 · 고사양 게임 병행용",
                Description = "전용 VRAM 약 1.5GB만 사용하여 고사양 3D 게임과 동시에 실행하기 좋은 빠른 모델 (권장 VRAM 2GB+)",
                ApproxSizeText = "약 1.1 GB",
                ApproxGb = 1.06,
                RecommendedVramGb = 2,
                HfRepo = "tencent/Hy-MT2-1.8B-GGUF",
                HfFilePattern = "^Hy-MT2-1\\.8B-Q4_K_M\\.gguf$",
                LocalFileName = "Hy-MT2-1.8B-Q4_K_M.gguf",
                LicenseFileInRepo = "LICENSE.txt",
                LocalLicenseFile = "LICENSE-HyMT2.txt",
                LicenseLabel = "Tencent Hunyuan / Hy-MT2 Official License",
                ManifestFile = "local-model-manifest.json"
            },
            new ModelSpec {
                Kind = "gemma",
                Title = "TranslateGemma 4B  (Q4_K_M)",
                Badge = "중간 체급 · 번역 특화",
                Description = "Google Gemma 기반의 다국어 번역 특화 중간 체급 로컬 모델 (권장 VRAM 4GB+)",
                ApproxSizeText = "약 2.3 GB",
                ApproxGb = 2.32,
                RecommendedVramGb = 4,
                HfRepo = "mradermacher/translategemma-4b-it-GGUF",
                HfFilePattern = "(?i)\\.Q4_K_M\\.gguf$",
                LocalFileName = "TranslateGemma-4B-Q4_K_M.gguf",
                LicenseFileInRepo = "",
                LocalLicenseFile = "",
                LicenseLabel = "Google Gemma Terms of Use (ai.google.dev/gemma/terms)",
                ExtraTemplateUrl = "https://raw.githubusercontent.com/ggml-org/llama.cpp/b11946/models/templates/google-translategemma-4b-it.jinja",
                ExtraTemplateLocal = "translategemma.jinja",
                ManifestFile = "translategemma-manifest.json"
            }
        };

        internal static bool IsRuntimeInstalled(string root) {
            return File.Exists(Path.Combine(root, "runtime", "llama", "llama-server.exe"));
        }

        internal static bool IsModelInstalled(string root, ModelSpec spec) {
            string p = Path.Combine(root, "models", spec.LocalFileName);
            if (!File.Exists(p)) return false;
            try { return new FileInfo(p).Length > 100 * 1024 * 1024; } catch { return false; }
        }
    }

    internal class ModelDownloadService {
        internal volatile bool CancelRequested;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static ModelDownloadService() {
            try {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)768;
                ServicePointManager.DefaultConnectionLimit = 8;
            } catch { }
        }

        string HttpGetString(string url) {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "DalvitLens-Installer/1.0";
            req.Timeout = 25000;
            req.ReadWriteTimeout = 25000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        void DownloadSmallFile(string url, string destPath) {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "DalvitLens-Installer/1.0";
            req.Timeout = 20000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                s.CopyTo(fs);
        }

        static string ComputeSha256(string filePath, string stepTitle, Action<DownloadProgressInfo> report, ref bool cancelFlag) {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 256)) {
                byte[] buf = new byte[1024 * 512];
                long total = Math.Max(1, fs.Length), readTotal = 0;
                var sw = Stopwatch.StartNew();
                long lastTick = 0;
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) {
                    if (cancelFlag) throw new OperationCanceledException("사용자가 설치를 취소했습니다.");
                    sha.TransformBlock(buf, 0, n, null, 0);
                    readTotal += n;
                    if (sw.ElapsedMilliseconds - lastTick >= 140 || readTotal == total) {
                        lastTick = sw.ElapsedMilliseconds;
                        double pct = readTotal * 100.0 / total;
                        report(new DownloadProgressInfo {
                            StepTitle = stepTitle,
                            DetailText = "파일 무결성(SHA-256) 검증 중… " + pct.ToString("0") + "%",
                            Percent = pct
                        });
                    }
                }
                sha.TransformFinalBlock(buf, 0, 0);
                var sb = new StringBuilder(64);
                foreach (byte b in sha.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        void DownloadLargeVerified(string url, string destPath, string expectedSha256, long expectedBytes, string stepTitle, Action<DownloadProgressInfo> report) {
            if (CancelRequested) throw new OperationCanceledException("사용자가 설치를 취소했습니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(destPath));

            if (File.Exists(destPath)) {
                bool cancelLocal = CancelRequested;
                if (String.IsNullOrEmpty(expectedSha256)) return;
                var fi = new FileInfo(destPath);
                if (expectedBytes <= 0 || fi.Length == expectedBytes) {
                    string existingHash = ComputeSha256(destPath, stepTitle, report, ref cancelLocal);
                    if (String.Equals(existingHash, expectedSha256, StringComparison.OrdinalIgnoreCase)) return;
                }
            }

            string partial = destPath + ".partial";
            long resumeFrom = 0;
            if (File.Exists(partial)) {
                long plen = new FileInfo(partial).Length;
                if (expectedBytes > 0 && plen > expectedBytes) File.Delete(partial);
                else if (expectedBytes > 0 && plen == expectedBytes) resumeFrom = plen;
                else resumeFrom = plen;
            }

            if (expectedBytes <= 0 || resumeFrom < expectedBytes) {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "DalvitLens-Installer/1.0";
                req.AllowAutoRedirect = true;
                req.Timeout = 25000;
                req.ReadWriteTimeout = 30000;
                if (resumeFrom > 0) req.AddRange(resumeFrom);

                HttpWebResponse resp = null;
                try {
                    resp = (HttpWebResponse)req.GetResponse();
                } catch (WebException wex) {
                    var hresp = wex.Response as HttpWebResponse;
                    if (hresp != null && (int)hresp.StatusCode == 416 && resumeFrom > 0) {
                        hresp.Close();
                        File.Delete(partial);
                        resumeFrom = 0;
                        req = (HttpWebRequest)WebRequest.Create(url);
                        req.UserAgent = "DalvitLens-Installer/1.0";
                        req.Timeout = 25000;
                        req.ReadWriteTimeout = 30000;
                        resp = (HttpWebResponse)req.GetResponse();
                    } else throw;
                }

                using (resp) {
                    bool isPartial = resp.StatusCode == HttpStatusCode.PartialContent;
                    if (!isPartial) resumeFrom = 0;
                    long totalBytes = expectedBytes > 0
                        ? expectedBytes
                        : (resp.ContentLength > 0 ? resumeFrom + resp.ContentLength : 0);

                    using (var net = resp.GetResponseStream())
                    using (var fs = new FileStream(partial, isPartial ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 256)) {
                        byte[] buf = new byte[1024 * 256];
                        long downloaded = resumeFrom;
                        long sessionBytes = 0;
                        var sw = Stopwatch.StartNew();
                        long lastReport = -200;
                        int n;
                        while ((n = net.Read(buf, 0, buf.Length)) > 0) {
                            if (CancelRequested) throw new OperationCanceledException("사용자가 설치를 취소했습니다.");
                            fs.Write(buf, 0, n);
                            downloaded += n;
                            sessionBytes += n;
                            long nowMs = sw.ElapsedMilliseconds;
                            if (nowMs - lastReport >= 120) {
                                lastReport = nowMs;
                                double sec = Math.Max(0.1, nowMs / 1000.0);
                                double mbps = (sessionBytes / (1024.0 * 1024.0)) / sec;
                                double pct = totalBytes > 0 ? (downloaded * 100.0 / totalBytes) : 0;
                                string sizePart = totalBytes >= 1024L * 1024 * 1024
                                    ? (downloaded / (1024.0 * 1024 * 1024)).ToString("0.00") + " / " + (totalBytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB"
                                    : (downloaded / (1024.0 * 1024)).ToString("0.0") + (totalBytes > 0 ? " / " + (totalBytes / (1024.0 * 1024)).ToString("0.0") + " MB" : " MB");
                                string etaPart = "";
                                if (totalBytes > downloaded && mbps > 0.2) {
                                    int etaSec = (int)Math.Round(((totalBytes - downloaded) / (1024.0 * 1024.0)) / mbps);
                                    etaPart = etaSec >= 60 ? " · 약 " + (etaSec / 60) + "분 " + (etaSec % 60) + "초 남음" : " · 약 " + etaSec + "초 남음";
                                }
                                report(new DownloadProgressInfo {
                                    StepTitle = stepTitle,
                                    DetailText = sizePart + "  (" + pct.ToString("0.0") + "% · " + mbps.ToString("0.1") + " MB/s" + etaPart + ")",
                                    Percent = pct
                                });
                            }
                        }
                    }
                }
            }

            if (!String.IsNullOrEmpty(expectedSha256)) {
                bool cancelLocal = CancelRequested;
                string actualHash = ComputeSha256(partial, stepTitle, report, ref cancelLocal);
                if (!String.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase)) {
                    try { File.Delete(partial); } catch { }
                    throw new Exception("다운로드 파일의 SHA-256 해시가 일치하지 않습니다. 네트워크 상태를 확인 후 다시 시도해 주세요.");
                }
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(partial, destPath);
        }

        internal static void ExtractZipOverwrite(string zipPath, string destDir) {
            Directory.CreateDirectory(destDir);
            string fullRoot = Path.GetFullPath(destDir);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
                fullRoot += Path.DirectorySeparatorChar;

            using (var archive = ZipFile.OpenRead(zipPath)) {
                foreach (var entry in archive.Entries) {
                    if (String.IsNullOrEmpty(entry.Name)) continue;
                    string target = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                    if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var es = entry.Open())
                    using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        es.CopyTo(fs);
                }
            }
        }

        internal void EnsureCudaRuntime(string root, int stepIndex, int totalSteps, Action<DownloadProgressInfo> report) {
            if (ModelCatalog.IsRuntimeInstalled(root)) return;
            string prefix = "[" + stepIndex + "/" + totalSteps + "] CUDA GPU 구동 엔진 (llama.cpp) ";
            report(new DownloadProgressInfo { StepTitle = prefix + "릴리스 정보 확인 중…", DetailText = "ggml-org/llama.cpp 공식 패키지 조회 중", Percent = 2 });

            string rawJson = HttpGetString("https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=10");
            var releases = json.Deserialize<ArrayList>(rawJson);
            Dictionary<string, object> chosenRelease = null;
            var matchedAssets = new List<Dictionary<string, object>>();

            var rxLlama = new Regex("^llama-.*-bin-win-cuda-12\\.4-x64\\.zip$", RegexOptions.IgnoreCase);
            var rxCudart = new Regex("^cudart-llama-bin-win-cuda-12\\.4-x64\\.zip$", RegexOptions.IgnoreCase);

            foreach (object rObj in releases) {
                var rel = rObj as Dictionary<string, object>;
                if (rel == null || !rel.ContainsKey("assets")) continue;
                var assets = rel["assets"] as ArrayList;
                if (assets == null) continue;
                bool hasLlama = false;
                var temp = new List<Dictionary<string, object>>();
                foreach (object aObj in assets) {
                    var a = aObj as Dictionary<string, object>;
                    if (a == null) continue;
                    string name = Convert.ToString(a["name"] ?? "");
                    if (rxLlama.IsMatch(name)) { hasLlama = true; temp.Add(a); }
                    else if (rxCudart.IsMatch(name)) temp.Add(a);
                }
                if (hasLlama) { chosenRelease = rel; matchedAssets = temp; break; }
            }

            if (chosenRelease == null || matchedAssets.Count == 0)
                throw new Exception("공식 llama.cpp Windows CUDA 12.4 릴리스를 찾지 못했습니다.");

            string dlDir = Path.Combine(root, "downloads");
            string rtDir = Path.Combine(root, "runtime", "llama");
            Directory.CreateDirectory(dlDir);
            Directory.CreateDirectory(rtDir);

            int pkgIdx = 0;
            foreach (var asset in matchedAssets) {
                pkgIdx++;
                string aName = Convert.ToString(asset["name"]);
                string aUrl = Convert.ToString(asset["browser_download_url"]);
                long aSize = asset.ContainsKey("size") ? Convert.ToInt64(asset["size"]) : 0;
                string digest = asset.ContainsKey("digest") && asset["digest"] != null ? Convert.ToString(asset["digest"]) : "";
                string sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7) : "";

                string zipDest = Path.Combine(dlDir, aName);
                DownloadLargeVerified(aUrl, zipDest, sha, aSize, prefix + "다운로드 (" + pkgIdx + "/" + matchedAssets.Count + ": " + aName + ")", report);

                report(new DownloadProgressInfo {
                    StepTitle = prefix + "압축 해제 중…",
                    DetailText = aName + " 압축을 푸는 중입니다…",
                    Percent = 96
                });
                ExtractZipOverwrite(zipDest, rtDir);
            }

            string serverExe = Path.Combine(rtDir, "llama-server.exe");
            if (!File.Exists(serverExe)) {
                var found = Directory.GetFiles(rtDir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) {
                    string subDir = Path.GetDirectoryName(found);
                    foreach (var f in Directory.GetFiles(subDir)) {
                        string dst = Path.Combine(rtDir, Path.GetFileName(f));
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(f, dst);
                    }
                }
            }
            if (!File.Exists(serverExe))
                throw new Exception("CUDA 런타임 압축 해제 후 llama-server.exe를 찾을 수 없습니다.");
        }

        internal void InstallSingleModel(string root, ModelSpec spec, int stepIndex, int totalSteps, Action<DownloadProgressInfo> report) {
            string prefix = "[" + stepIndex + "/" + totalSteps + "] " + spec.Title + " ";
            report(new DownloadProgressInfo {
                StepTitle = prefix + "메타데이터 조회 중…",
                DetailText = "Hugging Face 공식 저장소(" + spec.HfRepo + ") 확인 중",
                Percent = 1
            });

            string metaRaw = HttpGetString("https://huggingface.co/api/models/" + spec.HfRepo + "?blobs=true");
            var meta = json.Deserialize<Dictionary<string, object>>(metaRaw);
            string commitSha = Convert.ToString(meta["sha"]);
            var siblings = meta["siblings"] as ArrayList;
            Dictionary<string, object> matchedFile = null;
            var rx = new Regex(spec.HfFilePattern);

            if (siblings != null) {
                foreach (object sObj in siblings) {
                    var s = sObj as Dictionary<string, object>;
                    if (s == null) continue;
                    string rfn = Convert.ToString(s["rfilename"] ?? "");
                    if (rx.IsMatch(rfn)) { matchedFile = s; break; }
                }
            }
            if (matchedFile == null)
                throw new Exception(spec.Title + " 양자화 모델 파일을 저장소에서 찾을 수 없습니다.");

            string remoteName = Convert.ToString(matchedFile["rfilename"]);
            long expectedSize = matchedFile.ContainsKey("size") ? Convert.ToInt64(matchedFile["size"]) : 0;
            string expectedSha = "";
            if (matchedFile.ContainsKey("lfs") && matchedFile["lfs"] is Dictionary<string, object>) {
                var lfs = (Dictionary<string, object>)matchedFile["lfs"];
                if (lfs.ContainsKey("sha256")) expectedSha = Convert.ToString(lfs["sha256"]);
                if (expectedSize <= 0 && lfs.ContainsKey("size")) expectedSize = Convert.ToInt64(lfs["size"]);
            }

            string modelDir = Path.Combine(root, "models");
            Directory.CreateDirectory(modelDir);
            string destModel = Path.Combine(modelDir, spec.LocalFileName);
            string downloadUrl = "https://huggingface.co/" + spec.HfRepo + "/resolve/" + commitSha + "/" + remoteName + "?download=true";

            DownloadLargeVerified(downloadUrl, destModel, expectedSha, expectedSize, prefix + "다운로드 중…", report);

            try {
                if (!String.IsNullOrEmpty(spec.LicenseFileInRepo) && !String.IsNullOrEmpty(spec.LocalLicenseFile)) {
                    string licUrl = "https://huggingface.co/" + spec.HfRepo + "/raw/" + commitSha + "/" + spec.LicenseFileInRepo;
                    DownloadSmallFile(licUrl, Path.Combine(modelDir, spec.LocalLicenseFile));
                }
                if (!String.IsNullOrEmpty(spec.ExtraTemplateUrl) && !String.IsNullOrEmpty(spec.ExtraTemplateLocal)) {
                    DownloadSmallFile(spec.ExtraTemplateUrl, Path.Combine(modelDir, spec.ExtraTemplateLocal));
                }
                var manifest = new Dictionary<string, object> {
                    { "repo", spec.HfRepo },
                    { "revision", commitSha },
                    { "sourceFile", remoteName },
                    { "installedFile", spec.LocalFileName },
                    { "sha256", expectedSha },
                    { "license", spec.LicenseLabel },
                    { "installedAt", DateTime.UtcNow.ToString("o") }
                };
                File.WriteAllText(Path.Combine(root, spec.ManifestFile), json.Serialize(manifest), Encoding.UTF8);
            } catch { }

            report(new DownloadProgressInfo {
                StepTitle = prefix + "설치 완료!",
                DetailText = spec.LocalFileName + " 검증 및 설치 완료",
                Percent = 100
            });
        }

        internal void InstallModels(string root, IList<ModelSpec> selectedModels, Action<DownloadProgressInfo> report) {
            if (selectedModels == null || selectedModels.Count == 0) return;
            bool needRuntime = !ModelCatalog.IsRuntimeInstalled(root);
            int totalSteps = selectedModels.Count + (needRuntime ? 1 : 0);
            int curStep = 1;

            if (needRuntime) {
                EnsureCudaRuntime(root, curStep++, totalSteps, report);
            }
            foreach (var spec in selectedModels) {
                if (CancelRequested) throw new OperationCanceledException("사용자가 설치를 취소했습니다.");
                InstallSingleModel(root, spec, curStep++, totalSteps, report);
            }
        }
    }

    internal class ModelInstallerDialog : Form {
        readonly string root;
        readonly CheckBox[] checks = new CheckBox[ModelCatalog.All.Length];
        readonly Label[] statusBadges = new Label[ModelCatalog.All.Length];
        readonly SmoothProgressBar bar = new SmoothProgressBar();
        readonly Label stepLabel = new Label();
        readonly Label detailLabel = new Label();
        readonly InstallerButton installBtn = new InstallerButton { Text = "선택한 모델 다운로드 및 설치", Filled = true };
        readonly InstallerButton closeBtn = new InstallerButton { Text = "닫기" };
        ModelDownloadService activeService;
        internal bool InstalledAny { get; private set; }
        internal string LastInstalledKind { get; private set; }

        public ModelInstallerDialog(string rootDir, string preselectKind = null) {
            root = rootDir;
            Text = "DalvitLens · 로컬 AI 모델 설치 및 관리";
            ClientSize = new Size(720, 650);
            MinimumSize = new Size(680, 620);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = InstallerTheme.Surface;
            ForeColor = InstallerTheme.Text;
            Font = new Font("맑은 고딕", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            var ico = Path.Combine(root, "assets", "DalvitLens.ico");
            if (File.Exists(ico)) Icon = new Icon(ico, 48, 48);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(24) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            // Header + GPU Info
            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 14) };
            header.Controls.Add(new Label { Text = "로컬 AI 번역 모델 설치 및 관리", AutoSize = true, Font = new Font("맑은 고딕", 16f, FontStyle.Bold), ForeColor = InstallerTheme.Text, Margin = new Padding(0, 0, 0, 6) });
            var gpu = EnvironmentChecker.DetectGpu();
            header.Controls.Add(new Label {
                Text = "감지된 그래픽카드: " + gpu.Summary,
                AutoSize = true,
                Font = new Font("맑은 고딕", 9.25f, FontStyle.Bold),
                ForeColor = gpu.HasNvidia ? InstallerTheme.Green : InstallerTheme.Warn,
                Margin = new Padding(0, 0, 0, 4)
            });
            bool rtOk = ModelCatalog.IsRuntimeInstalled(root);
            header.Controls.Add(new Label {
                Text = rtOk
                    ? "✔ CUDA 구동 엔진(llama-server) 설치됨 · 추가 모델만 빠르게 내려받습니다."
                    : "• 로컬 모델을 처음 설치할 때 필수 CUDA 12.4 구동 엔진(약 640 MB)이 자동으로 함께 설치됩니다.",
                AutoSize = true,
                ForeColor = InstallerTheme.Muted
            });
            layout.Controls.Add(header, 0, 0);

            // Model cards
            var cardsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
            cardsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < ModelCatalog.All.Length; i++) {
                var spec = ModelCatalog.All[i];
                bool installed = ModelCatalog.IsModelInstalled(root, spec);
                var card = new Panel { Dock = DockStyle.Top, Height = 108, BackColor = InstallerTheme.Card, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 10) };
                card.Paint += (s, e) => {
                    using (var pen = new Pen(InstallerTheme.Outline, 1))
                        e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                };

                var cb = new CheckBox {
                    Text = spec.Title + "  —  " + spec.ApproxSizeText,
                    AutoSize = true,
                    Font = new Font("맑은 고딕", 10.5f, FontStyle.Bold),
                    ForeColor = InstallerTheme.Text,
                    Location = new Point(16, 12),
                    Checked = !installed && (spec.Kind == (preselectKind ?? "exaone"))
                };
                checks[i] = cb;
                card.Controls.Add(cb);

                var badge = new Label {
                    AutoSize = true,
                    Font = new Font("맑은 고딕", 8.75f, FontStyle.Bold),
                    Location = new Point(460, 14),
                    Text = installed ? "✔ 설치 완료" : spec.Badge,
                    ForeColor = installed ? InstallerTheme.Green : InstallerTheme.Gold
                };
                statusBadges[i] = badge;
                card.Controls.Add(badge);

                card.Controls.Add(new Label {
                    Text = spec.Description,
                    AutoSize = false,
                    Size = new Size(630, 22),
                    Location = new Point(36, 42),
                    ForeColor = InstallerTheme.Muted,
                    Font = new Font("맑은 고딕", 9f)
                });
                card.Controls.Add(new Label {
                    Text = "약관 고지: " + spec.LicenseLabel,
                    AutoSize = false,
                    Size = new Size(630, 20),
                    Location = new Point(36, 68),
                    ForeColor = Color.FromArgb(148, 138, 172),
                    Font = new Font("맑은 고딕", 8.25f)
                });
                cardsPanel.Controls.Add(card);
            }
            layout.Controls.Add(cardsPanel, 0, 1);

            // Progress area
            var progBox = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = InstallerTheme.Card, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 4, 0, 14) };
            stepLabel.Text = "설치할 모델을 체크하고 [선택한 모델 다운로드 및 설치]를 눌러주세요.";
            stepLabel.AutoSize = false;
            stepLabel.Size = new Size(635, 20);
            stepLabel.Location = new Point(14, 10);
            stepLabel.Font = new Font("맑은 고딕", 9.25f, FontStyle.Bold);
            stepLabel.ForeColor = InstallerTheme.Secondary;
            progBox.Controls.Add(stepLabel);

            bar.Size = new Size(638, 14);
            bar.Location = new Point(14, 34);
            progBox.Controls.Add(bar);

            detailLabel.Text = "공식 Hugging Face 및 llama.cpp 배포처에서 직접 내려받으며 끊겨도 이어받기가 지원됩니다.";
            detailLabel.AutoSize = false;
            detailLabel.Size = new Size(635, 18);
            detailLabel.Location = new Point(14, 53);
            detailLabel.Font = new Font("맑은 고딕", 8.5f);
            detailLabel.ForeColor = InstallerTheme.Muted;
            progBox.Controls.Add(detailLabel);
            layout.Controls.Add(progBox, 0, 2);

            // Footer buttons
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            closeBtn.Click += (s, e) => {
                if (activeService != null) {
                    activeService.CancelRequested = true;
                    stepLabel.Text = "다운로드 취소 요청 중…";
                } else {
                    Close();
                }
            };
            installBtn.Click += (s, e) => StartDownload();
            footer.Controls.Add(closeBtn);
            footer.Controls.Add(installBtn);
            layout.Controls.Add(footer, 0, 3);

            FormClosing += (s, e) => {
                if (activeService != null) {
                    activeService.CancelRequested = true;
                    e.Cancel = true;
                }
            };
        }

        void RefreshInstalledBadges() {
            for (int i = 0; i < ModelCatalog.All.Length; i++) {
                var spec = ModelCatalog.All[i];
                bool inst = ModelCatalog.IsModelInstalled(root, spec);
                statusBadges[i].Text = inst ? "✔ 설치 완료" : spec.Badge;
                statusBadges[i].ForeColor = inst ? InstallerTheme.Green : InstallerTheme.Gold;
                if (inst) checks[i].Checked = false;
            }
        }

        void StartDownload() {
            var selected = new List<ModelSpec>();
            for (int i = 0; i < checks.Length; i++)
                if (checks[i].Checked) selected.Add(ModelCatalog.All[i]);

            if (selected.Count == 0) {
                stepLabel.Text = "다운로드할 모델을 하나 이상 체크해 주세요.";
                stepLabel.ForeColor = InstallerTheme.Gold;
                return;
            }

            installBtn.Enabled = false;
            foreach (var c in checks) c.Enabled = false;
            closeBtn.Text = "다운로드 취소";
            stepLabel.ForeColor = InstallerTheme.Secondary;

            var svc = new ModelDownloadService();
            activeService = svc;

            new Thread(() => {
                try {
                    svc.InstallModels(root, selected, info => {
                        try {
                            BeginInvoke((Action)(() => {
                                stepLabel.Text = info.StepTitle;
                                detailLabel.Text = info.DetailText;
                                bar.Percent = info.Percent;
                            }));
                        } catch { }
                    });
                    BeginInvoke((Action)(() => {
                        activeService = null;
                        InstalledAny = true;
                        LastInstalledKind = selected[0].Kind;
                        bar.Percent = 100;
                        stepLabel.Text = "✔ 선택한 AI 모델 설치 및 검증이 모두 완료되었습니다!";
                        stepLabel.ForeColor = InstallerTheme.Green;
                        detailLabel.Text = "이제 닫기를 누르면 GPU 번역 엔진이 즉시 시작됩니다.";
                        RefreshInstalledBadges();
                        foreach (var c in checks) c.Enabled = true;
                        installBtn.Enabled = true;
                        closeBtn.Text = "완료 및 닫기";
                    }));
                } catch (OperationCanceledException) {
                    BeginInvoke((Action)(() => {
                        activeService = null;
                        stepLabel.Text = "다운로드가 취소되었습니다. (다음에 이어받기 가능)";
                        stepLabel.ForeColor = InstallerTheme.Gold;
                        foreach (var c in checks) c.Enabled = true;
                        installBtn.Enabled = true;
                        closeBtn.Text = "닫기";
                    }));
                } catch (Exception ex) {
                    BeginInvoke((Action)(() => {
                        activeService = null;
                        stepLabel.Text = "설치 오류: " + ex.Message;
                        stepLabel.ForeColor = InstallerTheme.Warn;
                        foreach (var c in checks) c.Enabled = true;
                        installBtn.Enabled = true;
                        closeBtn.Text = "닫기";
                    }));
                }
            }) { IsBackground = true }.Start();
        }
    }
}
