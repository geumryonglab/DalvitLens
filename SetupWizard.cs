using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DalvitInstaller;

static class SetupNative {
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
}

class SetupWizardForm : Form {
    int currentStep = 0; // 0: Env & Path, 1: Model Select, 2: Installing, 3: Complete
    GpuCheckResult gpuInfo = new GpuCheckResult { Name = "확인 중…" };
    bool ocrOk = true;

    readonly Label[] stepPills = new Label[4];
    readonly Panel pageHost = new Panel { Dock = DockStyle.Fill, BackColor = InstallerTheme.Surface };
    readonly Panel[] pages = new Panel[4];

    // Page 0 controls
    Label gpuStatusLabel, ocrStatusLabel, pathWarnLabel;
    InstallerButton openLangSettingsBtn;
    TextBox installDirBox;
    CheckBox desktopShortcutCheck, startMenuCheck;

    // Page 1 controls
    readonly CheckBox[] modelChecks = new CheckBox[ModelCatalog.All.Length];
    Label totalSizeLabel;

    // Page 2 controls
    Label installPhaseLabel, installDetailLabel, installLogLabel;
    SmoothProgressBar installBar;
    ModelDownloadService activeDownloader;
    bool skipModelsRequested;

    // Page 3 controls
    Label doneSummaryLabel;
    CheckBox launchNowCheck;
    string finalInstalledDir = "";
    string finalBackend = "google-free";

    // Footer buttons
    InstallerButton backBtn = new InstallerButton { Text = "이전" };
    InstallerButton nextBtn = new InstallerButton { Text = "다음  →", Filled = true };
    InstallerButton cancelBtn = new InstallerButton { Text = "취소" };

    public SetupWizardForm() {
        Text = "DalvitLens 설치 마법사";
        ClientSize = new Size(780, 640);
        MinimumSize = new Size(760, 620);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = InstallerTheme.Surface;
        ForeColor = InstallerTheme.Text;
        Font = new Font("맑은 고딕", 9.5f);

        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(26, 22, 26, 20) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // Top Header + Step Indicator
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 14) };
        header.Controls.Add(new Label {
            Text = "DALVITLENS  /  INSTALLER & MODEL WIZARD",
            AutoSize = true,
            Font = new Font("맑은 고딕", 8.75f, FontStyle.Bold),
            ForeColor = InstallerTheme.Primary
        });
        header.Controls.Add(new Label {
            Text = "달빛렌즈(DalvitLens) 설치 마법사",
            AutoSize = true,
            Font = new Font("맑은 고딕", 19f, FontStyle.Bold),
            ForeColor = InstallerTheme.Text,
            Margin = new Padding(0, 2, 0, 10)
        });

        var stepBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false, Margin = Padding.Empty };
        string[] stepNames = { "1. 환경 점검 · 설치 경로", "2. AI 번역 모델 선택", "3. 본체 및 모델 설치", "4. 설치 완료" };
        for (int i = 0; i < 4; i++) {
            var pill = new Label {
                Text = stepNames[i],
                AutoSize = true,
                Padding = new Padding(12, 6, 12, 6),
                Margin = new Padding(0, 0, 8, 0),
                Font = new Font("맑은 고딕", 8.75f, FontStyle.Bold)
            };
            stepPills[i] = pill;
            stepBar.Controls.Add(pill);
        }
        header.Controls.Add(stepBar);
        root.Controls.Add(header, 0, 0);

        // Build 4 pages
        pages[0] = BuildPage0();
        pages[1] = BuildPage1();
        pages[2] = BuildPage2();
        pages[3] = BuildPage3();
        foreach (var p in pages) { p.Dock = DockStyle.Fill; p.Visible = false; pageHost.Controls.Add(p); }
        root.Controls.Add(pageHost, 0, 1);

        // Footer
        var footer = new Panel { Dock = DockStyle.Top, Height = 48, Margin = new Padding(0, 10, 0, 0) };
        footer.Paint += (s, e) => {
            using (var pen = new Pen(InstallerTheme.Outline, 1))
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var btnFlow = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        nextBtn.Click += OnNextClicked;
        backBtn.Click += (s, e) => { if (currentStep == 1) ShowStep(0); };
        cancelBtn.Click += OnCancelClicked;
        btnFlow.Controls.Add(nextBtn);
        btnFlow.Controls.Add(backBtn);
        btnFlow.Controls.Add(cancelBtn);
        footer.Controls.Add(btnFlow);
        root.Controls.Add(footer, 0, 2);

        ShowStep(0);

        Shown += (s, e) => RunEnvironmentProbeAsync();
        FormClosing += (s, e) => {
            if (currentStep == 2 && activeDownloader != null) {
                var r = MessageBox.Show(this, "현재 모델 다운로드가 진행 중입니다. 설치를 중단하시겠습니까?", "DalvitLens 설치", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                activeDownloader.CancelRequested = true;
            }
        };
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        int dark = 1;
        try { SetupNative.DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
    }

    Panel MakeCard(int height, int bottomMargin = 12) {
        var p = new Panel { Dock = DockStyle.Top, Height = height, BackColor = InstallerTheme.Card, Margin = new Padding(0, 0, 0, bottomMargin) };
        p.Paint += (s, e) => {
            using (var pen = new Pen(InstallerTheme.Outline, 1))
                e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        return p;
    }

    Panel BuildPage0() {
        var host = new TableLayoutPanel { ColumnCount = 1, AutoSize = true };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 1. Hardware & OCR check card
        var envCard = MakeCard(132);
        envCard.Controls.Add(new Label { Text = "내 PC 실행 환경 자동 점검", AutoSize = true, Font = new Font("맑은 고딕", 11f, FontStyle.Bold), ForeColor = InstallerTheme.Text, Location = new Point(18, 14) });
        gpuStatusLabel = new Label { Text = "• 그래픽카드(GPU): 확인 중…", AutoSize = false, Size = new Size(685, 22), Location = new Point(20, 48), Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold), ForeColor = InstallerTheme.Secondary };
        envCard.Controls.Add(gpuStatusLabel);

        ocrStatusLabel = new Label { Text = "• Windows 영어(en-US) OCR 언어팩: 확인 중…", AutoSize = false, Size = new Size(510, 22), Location = new Point(20, 78), Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold), ForeColor = InstallerTheme.Secondary };
        envCard.Controls.Add(ocrStatusLabel);

        openLangSettingsBtn = new InstallerButton { Text = "Windows 언어 설정 열기", Visible = false, Location = new Point(525, 70), Padding = new Padding(12, 6, 12, 6) };
        openLangSettingsBtn.Click += (s, e) => { try { Process.Start("ms-settings:regionlanguage"); } catch { } };
        envCard.Controls.Add(openLangSettingsBtn);
        envCard.Controls.Add(new Label { Text = "로컬 GPU가 없어도 'Google 간편 연결' 모드로 모든 게임 화면 번역을 무료로 사용할 수 있습니다.", AutoSize = true, Location = new Point(20, 105), Font = new Font("맑은 고딕", 8.5f), ForeColor = InstallerTheme.Muted });
        host.Controls.Add(envCard);

        // 2. Install directory card
        var dirCard = MakeCard(142);
        dirCard.Controls.Add(new Label { Text = "설치 폴더 선택", AutoSize = true, Font = new Font("맑은 고딕", 11f, FontStyle.Bold), ForeColor = InstallerTheme.Text, Location = new Point(18, 14) });
        var boxBg = new Panel { Location = new Point(18, 46), Size = new Size(555, 36), BackColor = InstallerTheme.Field };
        installDirBox = new TextBox {
            Text = EnvironmentChecker.GetDefaultInstallDir(),
            BorderStyle = BorderStyle.None,
            BackColor = InstallerTheme.Field,
            ForeColor = InstallerTheme.Text,
            Font = new Font("맑은 고딕", 10f),
            Location = new Point(10, 8),
            Width = 535
        };
        boxBg.Controls.Add(installDirBox);
        dirCard.Controls.Add(boxBg);

        var browseBtn = new InstallerButton { Text = "폴더 변경…", Location = new Point(582, 43) };
        browseBtn.Click += (s, e) => {
            using (var fbd = new FolderBrowserDialog { Description = "DalvitLens를 설치할 영문 경로 폴더를 선택하세요.", SelectedPath = installDirBox.Text }) {
                if (fbd.ShowDialog(this) == DialogResult.OK) {
                    string sel = fbd.SelectedPath;
                    if (!sel.EndsWith("DalvitLens", StringComparison.OrdinalIgnoreCase))
                        sel = Path.Combine(sel, "DalvitLens");
                    installDirBox.Text = sel;
                }
            }
        };
        dirCard.Controls.Add(browseBtn);

        pathWarnLabel = new Label {
            Text = "✔ 영문(ASCII) 경로 확인됨 · 로컬 CUDA AI 엔진이 안정적으로 동작합니다.",
            AutoSize = false,
            Size = new Size(685, 38),
            Location = new Point(18, 92),
            Font = new Font("맑은 고딕", 8.75f),
            ForeColor = InstallerTheme.Green
        };
        installDirBox.TextChanged += (s, e) => ValidateInstallPathLabel();
        dirCard.Controls.Add(pathWarnLabel);
        host.Controls.Add(dirCard);

        // 3. Shortcut options card
        var optCard = MakeCard(110, 0);
        optCard.Controls.Add(new Label { Text = "바로가기 옵션", AutoSize = true, Font = new Font("맑은 고딕", 11f, FontStyle.Bold), ForeColor = InstallerTheme.Text, Location = new Point(18, 14) });
        desktopShortcutCheck = new CheckBox { Text = "바탕화면에 DalvitLens 실행 아이콘 만들기", Checked = true, AutoSize = true, Location = new Point(22, 46), ForeColor = InstallerTheme.Text, Font = new Font("맑은 고딕", 9.5f) };
        startMenuCheck = new CheckBox { Text = "시작 메뉴 프로그램 목록에 DalvitLens 등록하기", Checked = true, AutoSize = true, Location = new Point(22, 74), ForeColor = InstallerTheme.Text, Font = new Font("맑은 고딕", 9.5f) };
        optCard.Controls.Add(desktopShortcutCheck);
        optCard.Controls.Add(startMenuCheck);
        host.Controls.Add(optCard);

        return host;
    }

    Panel BuildPage1() {
        var host = new TableLayoutPanel { ColumnCount = 1, AutoSize = true };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        host.Controls.Add(new Label {
            Text = "함께 다운로드할 로컬 AI 번역 모델을 선택하세요. (선택하지 않고 넘어가면 본체만 즉시 설치되며, Google 간편 연결 모드로 바로 플레이하거나 나중에 프로그램 안에서도 모델을 내려받을 수 있습니다.)",
            AutoSize = false,
            Size = new Size(720, 40),
            ForeColor = InstallerTheme.Muted,
            Font = new Font("맑은 고딕", 9f),
            Margin = new Padding(0, 0, 0, 8)
        });

        for (int i = 0; i < ModelCatalog.All.Length; i++) {
            var spec = ModelCatalog.All[i];
            var card = MakeCard(94, 8);
            var cb = new CheckBox {
                Text = spec.Title + "   [" + spec.ApproxSizeText + "]",
                AutoSize = true,
                Font = new Font("맑은 고딕", 10.5f, FontStyle.Bold),
                ForeColor = InstallerTheme.Text,
                Location = new Point(16, 11),
                Checked = (i == 0)
            };
            cb.CheckedChanged += (s, e) => UpdateTotalDownloadSummary();
            modelChecks[i] = cb;
            card.Controls.Add(cb);

            card.Controls.Add(new Label {
                Text = spec.Badge,
                AutoSize = true,
                Location = new Point(495, 13),
                Font = new Font("맑은 고딕", 8.75f, FontStyle.Bold),
                ForeColor = i == 0 ? InstallerTheme.Gold : InstallerTheme.Secondary
            });
            card.Controls.Add(new Label {
                Text = spec.Description,
                AutoSize = false,
                Size = new Size(675, 20),
                Location = new Point(36, 40),
                Font = new Font("맑은 고딕", 8.85f),
                ForeColor = InstallerTheme.Muted
            });
            card.Controls.Add(new Label {
                Text = "공식 배포처: Hugging Face (" + spec.HfRepo + ")  ·  " + spec.LicenseLabel,
                AutoSize = false,
                Size = new Size(675, 18),
                Location = new Point(36, 64),
                Font = new Font("맑은 고딕", 8f),
                ForeColor = Color.FromArgb(148, 138, 174)
            });
            host.Controls.Add(card);
        }

        var sumCard = MakeCard(58, 0);
        totalSizeLabel = new Label {
            Text = "",
            AutoSize = false,
            Size = new Size(690, 38),
            Location = new Point(16, 10),
            Font = new Font("맑은 고딕", 9.25f, FontStyle.Bold),
            ForeColor = InstallerTheme.Secondary
        };
        sumCard.Controls.Add(totalSizeLabel);
        host.Controls.Add(sumCard);

        UpdateTotalDownloadSummary();
        return host;
    }

    Panel BuildPage2() {
        var host = new TableLayoutPanel { ColumnCount = 1, AutoSize = true };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var pCard = MakeCard(240, 12);
        pCard.Controls.Add(new Label {
            Text = "DalvitLens 설치 및 AI 엔진 구성 중",
            AutoSize = true,
            Font = new Font("맑은 고딕", 13f, FontStyle.Bold),
            ForeColor = InstallerTheme.Text,
            Location = new Point(22, 20)
        });

        installPhaseLabel = new Label {
            Text = "설치 준비 중…",
            AutoSize = false,
            Size = new Size(680, 24),
            Location = new Point(22, 66),
            Font = new Font("맑은 고딕", 10.25f, FontStyle.Bold),
            ForeColor = InstallerTheme.Secondary
        };
        pCard.Controls.Add(installPhaseLabel);

        installBar = new SmoothProgressBar { Location = new Point(22, 100), Size = new Size(680, 18) };
        pCard.Controls.Add(installBar);

        installDetailLabel = new Label {
            Text = "",
            AutoSize = false,
            Size = new Size(680, 22),
            Location = new Point(22, 128),
            Font = new Font("맑은 고딕", 9f),
            ForeColor = InstallerTheme.Muted
        };
        pCard.Controls.Add(installDetailLabel);

        installLogLabel = new Label {
            Text = "• 대용량 AI 모델은 공식 서버에서 직접 내려받으며 SHA-256 해시 검증을 수행합니다.\n• 지금 모델 다운로드를 건너뛰어도 본체 설치는 완료되며, 앱 안의 [AI 모델 설치 · 관리]에서 이어받을 수 있습니다.",
            AutoSize = false,
            Size = new Size(680, 52),
            Location = new Point(22, 168),
            Font = new Font("맑은 고딕", 8.5f),
            ForeColor = Color.FromArgb(155, 146, 180)
        };
        pCard.Controls.Add(installLogLabel);
        host.Controls.Add(pCard);

        return host;
    }

    Panel BuildPage3() {
        var host = new TableLayoutPanel { ColumnCount = 1, AutoSize = true };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var doneCard = MakeCard(255, 12);
        doneCard.Controls.Add(new Label {
            Text = "✔ DalvitLens 설치가 완료되었습니다!",
            AutoSize = true,
            Font = new Font("맑은 고딕", 15f, FontStyle.Bold),
            ForeColor = InstallerTheme.Green,
            Location = new Point(22, 20)
        });

        doneSummaryLabel = new Label {
            Text = "",
            AutoSize = false,
            Size = new Size(680, 86),
            Location = new Point(22, 62),
            Font = new Font("맑은 고딕", 9.5f),
            ForeColor = InstallerTheme.Text
        };
        doneCard.Controls.Add(doneSummaryLabel);

        doneCard.Controls.Add(new Label {
            Text = "핵심 단축키 안내\n"
                 + "  • [F8] (또는 Shift+F8) : 마우스 커서 주변 스킬·아이템 툴팁 즉시 번역\n"
                 + "  • [F9] : 현재 게임 창 전체 번역    • [F7] : 다음 묶음 넘김    • [F10] : 번역 숨김\n"
                 + "  • [Ctrl + Alt + F8] : 내 게임에 맞는 캡처 영역 드래그 지정",
            AutoSize = false,
            Size = new Size(680, 92),
            Location = new Point(22, 152),
            Font = new Font("맑은 고딕", 9f),
            ForeColor = InstallerTheme.Secondary
        });
        host.Controls.Add(doneCard);

        var runCard = MakeCard(62, 0);
        launchNowCheck = new CheckBox {
            Text = "설치 마법사를 닫을 때 DalvitLens 바로 실행하기",
            Checked = true,
            AutoSize = true,
            Font = new Font("맑은 고딕", 10.25f, FontStyle.Bold),
            ForeColor = InstallerTheme.Text,
            Location = new Point(22, 18)
        };
        runCard.Controls.Add(launchNowCheck);
        host.Controls.Add(runCard);

        return host;
    }

    void ValidateInstallPathLabel() {
        string p = installDirBox.Text.Trim();
        if (!EnvironmentChecker.IsAsciiPath(p)) {
            pathWarnLabel.Text = "⚠ 경로에 한글/특수문자가 포함되어 있습니다. 로컬 GPU 엔진(llama.cpp) 호환을 위해 C:\\DalvitLens 같은 영문 경로를 권장합니다.";
            pathWarnLabel.ForeColor = InstallerTheme.Gold;
        } else {
            pathWarnLabel.Text = "✔ 영문(ASCII) 경로 확인됨 · 로컬 CUDA AI 엔진이 안정적으로 동작합니다.";
            pathWarnLabel.ForeColor = InstallerTheme.Green;
        }
    }

    void UpdateTotalDownloadSummary() {
        double gb = 0;
        int cnt = 0;
        for (int i = 0; i < modelChecks.Length; i++) {
            if (modelChecks[i] != null && modelChecks[i].Checked) {
                gb += ModelCatalog.All[i].ApproxGb;
                cnt++;
            }
        }
        if (cnt == 0) {
            totalSizeLabel.Text = "선택한 로컬 모델 없음 (추가 다운로드 0 MB)\n→ 본체만 1초 만에 설치하고 'Google 간편 연결' 모드로 시작합니다. (설치 후 앱 내 모델 추가 가능)";
            totalSizeLabel.ForeColor = InstallerTheme.Gold;
        } else {
            string dir = installDirBox != null ? installDirBox.Text.Trim() : "";
            bool hasRt = !String.IsNullOrEmpty(dir) && ModelCatalog.IsRuntimeInstalled(dir);
            if (!hasRt) gb += ModelCatalog.RuntimeApproxGb;
            totalSizeLabel.Text = "선택한 모델 " + cnt + "종 · 예상 다운로드 용량: 약 " + gb.ToString("0.1") + " GB" + (hasRt ? "" : " (필수 CUDA 12.4 엔진 포함)")
                + "\n→ 다운로드 중 끊기더라도 이어받기와 SHA-256 무결성 검증이 자동 적용됩니다.";
            totalSizeLabel.ForeColor = InstallerTheme.Secondary;
        }
    }

    void RunEnvironmentProbeAsync() {
        new Thread(() => {
            var g = EnvironmentChecker.DetectGpu();
            bool ocr = EnvironmentChecker.CheckEnglishOcrInstalled();
            try {
                BeginInvoke((Action)(() => {
                    gpuInfo = g;
                    ocrOk = ocr;
                    gpuStatusLabel.Text = (g.HasNvidia ? "✔ 그래픽카드: " : "⚠ 그래픽카드: ") + g.Summary;
                    gpuStatusLabel.ForeColor = g.HasNvidia ? InstallerTheme.Green : InstallerTheme.Gold;

                    if (ocr) {
                        ocrStatusLabel.Text = "✔ Windows 영어(en-US) OCR 언어팩: 정상 설치됨";
                        ocrStatusLabel.ForeColor = InstallerTheme.Green;
                        openLangSettingsBtn.Visible = false;
                    } else {
                        ocrStatusLabel.Text = "⚠ 영어(en-US) 기본 입력/OCR 언어 기능 설치가 필요합니다.";
                        ocrStatusLabel.ForeColor = InstallerTheme.Warn;
                        openLangSettingsBtn.Visible = true;
                    }

                    // Smart model preset based on GPU VRAM
                    if (!g.HasNvidia) {
                        for (int i = 0; i < modelChecks.Length; i++) modelChecks[i].Checked = false;
                    } else if (g.VramGb > 0.5 && g.VramGb < 5.2) {
                        modelChecks[0].Checked = false;
                        modelChecks[1].Checked = true; // Hy-MT2 1.8B for 2~4GB VRAM cards
                    }
                    UpdateTotalDownloadSummary();
                }));
            } catch { }
        }) { IsBackground = true }.Start();
    }

    void ShowStep(int step) {
        currentStep = step;
        for (int i = 0; i < 4; i++) {
            pages[i].Visible = (i == step);
            bool active = (i == step);
            bool done = (i < step);
            stepPills[i].BackColor = active ? InstallerTheme.Primary : (done ? Color.FromArgb(38, 58, 48) : InstallerTheme.Field);
            stepPills[i].ForeColor = active ? InstallerTheme.Surface : (done ? InstallerTheme.Green : InstallerTheme.Muted);
        }

        if (step == 0) {
            backBtn.Visible = false;
            nextBtn.Visible = true;
            nextBtn.Text = "다음  →";
            cancelBtn.Visible = true;
            cancelBtn.Text = "취소";
        } else if (step == 1) {
            UpdateTotalDownloadSummary();
            backBtn.Visible = true;
            nextBtn.Visible = true;
            nextBtn.Text = "설치 시작";
            cancelBtn.Visible = true;
            cancelBtn.Text = "취소";
        } else if (step == 2) {
            backBtn.Visible = false;
            nextBtn.Visible = false;
            cancelBtn.Visible = true;
            cancelBtn.Text = "모델 다운로드 건너뛰기";
        } else if (step == 3) {
            backBtn.Visible = false;
            cancelBtn.Visible = false;
            nextBtn.Visible = true;
            nextBtn.Text = "마침";
        }
    }

    void OnNextClicked(object sender, EventArgs e) {
        if (currentStep == 0) {
            string dir = installDirBox.Text.Trim();
            if (dir.Length < 3) {
                MessageBox.Show(this, "올바른 설치 폴더 경로를 입력해 주세요.", "DalvitLens 설치", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!EnvironmentChecker.IsAsciiPath(dir)) {
                var r = MessageBox.Show(this,
                    "선택하신 경로에 한글 또는 비영문 문자가 포함되어 있습니다:\n" + dir + "\n\n"
                    + "로컬 GPU 엔진(llama.cpp) 오류 방지를 위해 영문 경로(예: C:\\DalvitLens)로 변경할까요?\n([예] 클릭 시 C:\\DalvitLens로 자동 변경)",
                    "영문 경로 권장", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Yes) installDirBox.Text = @"C:\DalvitLens";
            }
            ShowStep(1);
        } else if (currentStep == 1) {
            ShowStep(2);
            BeginInstallationAsync();
        } else if (currentStep == 3) {
            if (launchNowCheck.Checked) {
                string exe = Path.Combine(finalInstalledDir, "DalvitLens.exe");
                if (File.Exists(exe)) {
                    try { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = finalInstalledDir, UseShellExecute = true }); } catch { }
                }
            }
            Close();
        }
    }

    void OnCancelClicked(object sender, EventArgs e) {
        if (currentStep == 2) {
            if (activeDownloader != null) {
                skipModelsRequested = true;
                activeDownloader.CancelRequested = true;
                installPhaseLabel.Text = "모델 다운로드를 건너뛰고 본체 설치를 마무리하는 중…";
                cancelBtn.Enabled = false;
            }
            return;
        }
        Close();
    }

    void BeginInstallationAsync() {
        string targetDir = installDirBox.Text.Trim();
        bool makeDesktop = desktopShortcutCheck.Checked;
        bool makeStartMenu = startMenuCheck.Checked;
        var selectedModels = new List<ModelSpec>();
        for (int i = 0; i < modelChecks.Length; i++)
            if (modelChecks[i].Checked) selectedModels.Add(ModelCatalog.All[i]);

        cancelBtn.Visible = selectedModels.Count > 0;
        cancelBtn.Enabled = true;
        skipModelsRequested = false;

        new Thread(() => {
            try {
                BeginInvoke((Action)(() => {
                    installPhaseLabel.Text = "DalvitLens 프로그램 본체 파일 설치 중…";
                    installDetailLabel.Text = targetDir;
                    installBar.Percent = 8;
                }));

                SetupInstallerCore.ExtractEmbeddedPayload(targetDir);

                if (makeDesktop) {
                    string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    SetupInstallerCore.CreateShortcut(Path.Combine(desk, "DalvitLens.lnk"), Path.Combine(targetDir, "DalvitLens.exe"), targetDir, Path.Combine(targetDir, "assets", "DalvitLens.ico"), "DalvitLens · 실시간 게임 화면 번역");
                }
                if (makeStartMenu) {
                    string sm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DalvitLens.lnk");
                    SetupInstallerCore.CreateShortcut(sm, Path.Combine(targetDir, "DalvitLens.exe"), targetDir, Path.Combine(targetDir, "assets", "DalvitLens.ico"), "DalvitLens · 실시간 게임 화면 번역");
                }
                SetupInstallerCore.WriteUninstallHelper(targetDir);

                BeginInvoke((Action)(() => { installBar.Percent = 18; }));

                bool modelsCompleted = false;
                if (selectedModels.Count > 0) {
                    var svc = new ModelDownloadService();
                    activeDownloader = svc;
                    try {
                        svc.InstallModels(targetDir, selectedModels, info => {
                            try {
                                BeginInvoke((Action)(() => {
                                    installPhaseLabel.Text = info.StepTitle;
                                    installDetailLabel.Text = info.DetailText;
                                    installBar.Percent = info.Percent;
                                }));
                            } catch { }
                        });
                        modelsCompleted = true;
                    } catch (OperationCanceledException) {
                        if (!skipModelsRequested) throw;
                    } finally {
                        activeDownloader = null;
                    }
                }

                // Determine best default backend based on what is actually installed on disk
                string chosenBackend = "google-free";
                foreach (var spec in new[] { ModelCatalog.All[0], ModelCatalog.All[2], ModelCatalog.All[1] }) {
                    if (ModelCatalog.IsRuntimeInstalled(targetDir) && ModelCatalog.IsModelInstalled(targetDir, spec)) {
                        chosenBackend = spec.Kind;
                        break;
                    }
                }
                if (selectedModels.Count > 0 && modelsCompleted)
                    chosenBackend = selectedModels[0].Kind;

                SetupInstallerCore.EnsureInitialSettings(targetDir, chosenBackend);

                BeginInvoke((Action)(() => {
                    finalInstalledDir = targetDir;
                    finalBackend = chosenBackend;
                    string engineName = chosenBackend == "exaone" ? "EXAONE 3.5 7.8B (로컬 GPU)"
                        : chosenBackend == "gemma" ? "TranslateGemma 4B (로컬 GPU)"
                        : chosenBackend == "hymt" ? "Hy-MT2 1.8B (로컬 GPU)"
                        : "Google 간편 연결 (API 키 불필요 · 앱 내 [AI 모델 설치 · 관리]로 언제든 로컬 모델 추가 가능)";
                    doneSummaryLabel.Text = "• 설치 위치: " + targetDir + "\n"
                        + "• 기본 번역 엔진: " + engineName + "\n"
                        + (ocrOk ? "• 영어 OCR 엔진: 정상 확인됨" : "• 주의: Windows 설정 > 시간 및 언어 > 언어 및 지역에서 '영어(미국)' 기본 입력을 꼭 추가해 주세요.");
                    ShowStep(3);
                }));
            } catch (Exception ex) {
                activeDownloader = null;
                try {
                    BeginInvoke((Action)(() => {
                        MessageBox.Show(this, "설치 중 오류가 발생했습니다:\n" + ex.Message, "DalvitLens 설치 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        ShowStep(1);
                    }));
                } catch { }
            }
        }) { IsBackground = true }.Start();
    }
}

static class SetupInstallerCore {
    internal static void StopRunningProcessesInDir(string targetDir) {
        string full = Path.GetFullPath(targetDir).TrimEnd('\\') + "\\";
        foreach (string pName in new[] { "DalvitLens", "GuildLens", "llama-server" }) {
            try {
                foreach (var p in Process.GetProcessesByName(pName)) {
                    try {
                        string mf = p.MainModule.FileName;
                        if (mf != null && mf.StartsWith(full, StringComparison.OrdinalIgnoreCase)) {
                            p.Kill();
                            p.WaitForExit(2000);
                        }
                    } catch { } finally { p.Dispose(); }
                }
            } catch { }
        }
    }

    internal static void ExtractEmbeddedPayload(string targetDir) {
        Directory.CreateDirectory(targetDir);
        StopRunningProcessesInDir(targetDir);

        string fullRoot = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var preserveIfExisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "glossary.json", "settings.json", "translation-cache.json"
        };

        using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("DalvitLens.Payload.zip")) {
            if (res == null) throw new Exception("설치 패키지 내장 리소스(DalvitLens.Payload.zip)를 찾을 수 없습니다.");
            using (var archive = new ZipArchive(res, ZipArchiveMode.Read)) {
                foreach (var entry in archive.Entries) {
                    if (String.IsNullOrEmpty(entry.Name)) continue;
                    string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string dst = Path.GetFullPath(Path.Combine(targetDir, rel));
                    if (!dst.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    if (preserveIfExisting.Contains(rel) && File.Exists(dst)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    using (var es = entry.Open())
                    using (var fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                        es.CopyTo(fs);
                }
            }
        }
    }

    internal static void EnsureInitialSettings(string targetDir, string defaultBackend) {
        string settingsPath = Path.Combine(targetDir, "settings.json");
        if (File.Exists(settingsPath)) return;
        string json = "{\"backend\":\"" + defaultBackend + "\",\"apiKey\":\"\",\"fontSize\":13,\"nearWidth\":900,\"nearHeight\":650,\"nearFixed\":false,\"tooltipLine\":true,\"tooltipLineOpacity\":73,\"tooltipOpacity\":85,\"obsTooltipOpacity\":48,\"nearRegionX\":0,\"nearRegionY\":0,\"nearRegionW\":0,\"nearRegionH\":0,\"nearMonitor\":\"\"}";
        File.WriteAllText(settingsPath, json, new UTF8Encoding(false));
    }

    internal static void CreateShortcut(string lnkPath, string targetExe, string workDir, string iconPath, string description) {
        try {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            Type scType = sc.GetType();
            scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { targetExe });
            scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            if (File.Exists(iconPath))
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { iconPath });
            scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        } catch { }
    }

    internal static void WriteUninstallHelper(string targetDir) {
        try {
            string cmd = "@echo off\r\n"
                + "chcp 65001 >nul\r\n"
                + "echo DalvitLens 바로가기를 정리합니다...\r\n"
                + "taskkill /F /IM DalvitLens.exe >nul 2>&1\r\n"
                + "taskkill /F /IM llama-server.exe >nul 2>&1\r\n"
                + "del /F /Q \"%USERPROFILE%\\Desktop\\DalvitLens.lnk\" >nul 2>&1\r\n"
                + "del /F /Q \"%APPDATA%\\Microsoft\\Windows\\Start Menu\\Programs\\DalvitLens.lnk\" >nul 2>&1\r\n"
                + "echo 바로가기가 삭제되었습니다. 이 폴더(" + targetDir + ")를 삭제하면 완전히 제거됩니다.\r\n"
                + "pause\r\n";
            File.WriteAllText(Path.Combine(targetDir, "Uninstall-DalvitLens.cmd"), cmd, new UTF8Encoding(false));
        } catch { }
    }
}

static class SetupProgram {
    [STAThread]
    static int Main(string[] args) {
        if (args != null && args.Length >= 2 && args[0] == "--extract-only") {
            try {
                string dir = args[1];
                SetupInstallerCore.ExtractEmbeddedPayload(dir);
                SetupInstallerCore.EnsureInitialSettings(dir, "google-free");
                SetupInstallerCore.WriteUninstallHelper(dir);
                Console.WriteLine("EXTRACTED_OK: " + dir);
                return 0;
            } catch (Exception ex) {
                Console.Error.WriteLine("EXTRACT_ERR: " + ex.Message);
                return 1;
            }
        }

        SetupNative.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupWizardForm());
        return 0;
    }
}
