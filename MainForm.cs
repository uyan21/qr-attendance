using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Media;

namespace QrAttendance;

class MainForm : Form
{
    enum Kind { Primary, Secondary }

    readonly Camera cam = new();
    readonly List<Panel> pages = new();
    readonly List<Button> navs = new();
    Panel content;
    RoundedLabel result;
    Label todayLbl, rosterLbl;
    RoundButton camBtn;
    ComboBox gridBox;
    CheckBox allBox;
    readonly HashSet<long> unpicked = new();
    bool syncing;
    string snapshotDir;
    DataGridView todayGrid, rosterGrid;
    DateTimePicker d1, d2;

    Form fs;
    PictureBox fsVideo;
    Label fsResult;
    readonly Stopwatch guideClock = Stopwatch.StartNew();
    Color flashColor = Color.White;
    DateTime flashAt;
    string lastScan = "";
    DateTime lastScanAt;

    int S(int v) => v * DeviceDpi / 96;

    public MainForm()
    {
        Text = "QR 출석";
        using (var ico = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico")) Icon = new Icon(ico);
        Font = new Font(Core.FontName, 10.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        var area = Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(Math.Min(S(1120), area.Width * 92 / 100), Math.Min(S(700), area.Height * 88 / 100));
        MinimumSize = new Size(Math.Min(S(900), area.Width), Math.Min(S(560), area.Height));
        StartPosition = FormStartPosition.CenterScreen;

        content = new Panel { Dock = DockStyle.Fill };
        Controls.Add(content);
        Controls.Add(BuildSidebar());
        BuildCheck();
        BuildRoster();
        BuildExport();

        ShowPage(0);
        FormClosing += (_, _) => cam.Stop();
    }

    // 셀프테스트용: 각 화면을 PNG로 저장
    public void Snapshot(string dir)
    {
        void Save(Control c, string name)
        {
            Application.DoEvents();
            using var bmp = new Bitmap(c.Width, c.Height);
            c.DrawToBitmap(bmp, new Rectangle(Point.Empty, c.Size));
            bmp.Save(Path.Combine(dir, name + ".png"));
        }
        for (int i = 0; i < pages.Count; i++)
        {
            ShowPage(i);
            if (i == 0) Check(Core.People()[0].Token);
            Save(this, "page" + i);
        }
        var many = Enumerable.Repeat(Core.People(), 6).SelectMany(p => p).ToList();
        foreach (int n in new[] { 2, 3, 4 })
        {
            using var doc = CardDoc(many, n);
            var pc = new PreviewPrintController();
            doc.PrintController = pc;
            doc.Print();
            var pages = pc.GetPreviewPageInfo();
            pages[0].Image.Save(Path.Combine(dir, $"print{n}_of{pages.Length}pages.png"));
        }
        snapshotDir = dir;
        ShowRosterHelp();
        snapshotDir = null;
        OpenFullscreen();
        var gray = new Bitmap(1280, 720);
        using (var g = Graphics.FromImage(gray)) g.Clear(Color.FromArgb(120, 130, 140));
        fsVideo.Image = gray;
        Save(fs, "fullscreen");
        fs.Close();
    }

    // --- 공통 ---
    Panel BuildSidebar()
    {
        var side = new Panel { Dock = DockStyle.Left, Width = S(210), BackColor = Theme.Side };
        side.Controls.Add(new Label
        {
            Text = "개인정보는 이 PC에만\n저장됩니다", Dock = DockStyle.Bottom, Height = S(64), ForeColor = Theme.Muted,
            Font = new Font(Core.FontName, 9), TextAlign = ContentAlignment.MiddleCenter,
        });
        string[] names = { "출석 체크", "명단 · QR 생성", "전체 출석일 내보내기" };
        for (int i = names.Length - 1; i >= 0; i--)
        {
            int index = i;
            var b = new Button
            {
                Text = names[i], Dock = DockStyle.Top, Height = S(52), FlatStyle = FlatStyle.Flat, ForeColor = Theme.SideText,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(22), 0, 0, 0), Cursor = Cursors.Hand,
                Font = new Font(Core.FontName, 11.5f, FontStyle.Bold), TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.SideActive;
            b.FlatAppearance.MouseDownBackColor = Theme.SideActive;
            b.Click += (_, _) => ShowPage(index);
            navs.Insert(0, b);
            side.Controls.Add(b);
        }
        side.Controls.Add(new Label
        {
            Text = "QR 출석", Dock = DockStyle.Top, Height = S(92), ForeColor = Color.White,
            Font = new Font(Core.FontName, 20, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(20), 0, 0, 0),
        });
        return side;
    }

    Panel NewPage(string title)
    {
        var page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(28), S(14), S(28), S(24)), Visible = false };
        content.Controls.Add(page);
        pages.Add(page);
        page.Tag = new Label
        {
            Text = title, Dock = DockStyle.Top, Height = S(58), Font = new Font(Core.FontName, 18, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        return page;
    }

    void ShowPage(int index)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].Visible = i == index;
            navs[i].BackColor = i == index ? Theme.SideActive : Theme.Side;
            navs[i].ForeColor = i == index ? Color.White : Theme.SideText;
        }
        if (index == 0) RefreshToday();
        else if (index == 1) RefreshRoster();
    }

    RoundButton Btn(string text, Action click, Kind kind = Kind.Primary)
    {
        var b = new RoundButton
        {
            Text = text, Padding = new Padding(S(14), S(7), S(14), S(7)), Margin = new Padding(0, 0, S(8), 0),
            Font = new Font(Core.FontName, 10.5f, FontStyle.Bold),
        };
        if (kind != Kind.Primary)
        {
            b.Fill = Color.White;
            b.Hover = Theme.Bg;
            b.TextColor = Theme.Text;
            b.Border = Color.FromArgb(203, 213, 225);
        }
        b.Click += (_, _) => click();
        return b;
    }

    DataGridView Grid(params (string text, int weight)[] cols)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Theme.Line, EnableHeadersVisualStyles = false,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, ColumnHeadersHeight = S(42),
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        };
        g.RowTemplate.Height = S(38);
        var head = g.ColumnHeadersDefaultCellStyle;
        head.BackColor = head.SelectionBackColor = Color.FromArgb(248, 250, 252);
        head.ForeColor = head.SelectionForeColor = Theme.Muted;
        head.Font = new Font(Core.FontName, 10, FontStyle.Bold);
        head.Alignment = DataGridViewContentAlignment.MiddleCenter;
        var cell = g.DefaultCellStyle;
        cell.Alignment = DataGridViewContentAlignment.MiddleCenter;
        cell.ForeColor = cell.SelectionForeColor = Theme.Text;
        cell.SelectionBackColor = Theme.AccentSoft;
        foreach (var (text, weight) in cols)
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = text, FillWeight = weight });
        return g;
    }

    static void Fill(DataGridView g, IEnumerable<(long id, object[] cells)> rows)
    {
        g.Rows.Clear();
        foreach (var (id, cells) in rows) g.Rows[g.Rows.Add(cells)].Tag = id;
        g.CurrentCell = null; // 첫 행이 자동 선택되지 않게
        g.ClearSelection();
    }

    static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    // --- 출석 체크 ---
    void BuildCheck()
    {
        var page = NewPage("출석 체크");

        result = new RoundedLabel
        {
            Text = "웹캠을 켜고 QR을 대세요", Dock = DockStyle.Top, Height = S(170), Font = new Font(Core.FontName, 30, FontStyle.Bold),
            BackColor = Color.White, ForeColor = Theme.Muted,
        };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(68), Padding = new Padding(0, S(16), 0, 0), WrapContents = false };
        camBtn = Btn("웹캠 전체화면 시작", ToggleCam);
        bar.Controls.Add(camBtn);
        bar.Controls.Add(new Label { Text = "출석 요일", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(S(14), S(9), S(4), 0) });
        var saved = Core.GetDays();
        var boxes = new List<CheckBox>();
        foreach (var (text, day) in new[] { ("월", DayOfWeek.Monday), ("화", DayOfWeek.Tuesday), ("수", DayOfWeek.Wednesday), ("목", DayOfWeek.Thursday),
                                            ("금", DayOfWeek.Friday), ("토", DayOfWeek.Saturday), ("일", DayOfWeek.Sunday) })
        {
            var cb = new CheckBox { Text = text, Tag = day, Checked = saved.Contains(day), AutoSize = true, Margin = new Padding(S(4), S(8), 0, 0) };
            cb.CheckedChanged += (_, _) => Core.SetDays(boxes.Where(b => b.Checked).Select(b => (DayOfWeek)b.Tag));
            boxes.Add(cb);
            bar.Controls.Add(cb);
        }

        var listHead = new Panel { Dock = DockStyle.Top, Height = S(52), Padding = new Padding(0, S(6), 0, S(8)) };
        todayLbl = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Core.FontName, 13, FontStyle.Bold) };
        var cancel = Btn("선택한 출석 취소", CancelAttendance, Kind.Secondary);
        cancel.Dock = DockStyle.Right;
        listHead.Controls.Add(todayLbl);
        listHead.Controls.Add(cancel);

        todayGrid = Grid(("시간", 20), ("이름", 35), ("핸드폰번호", 45));
        page.Controls.Add(todayGrid);
        page.Controls.Add(listHead);
        page.Controls.Add(bar);
        page.Controls.Add(result);
        page.Controls.Add((Control)page.Tag);
    }

    void Show(string text, Color bg, bool ok)
    {
        result.Text = text;
        result.BackColor = bg;
        result.ForeColor = Color.White;
        (flashColor, flashAt) = (bg, DateTime.Now);
        if (fs != null)
        {
            fsResult.Text = text;
            fsResult.BackColor = bg;
        }
        (ok ? SystemSounds.Asterisk : SystemSounds.Hand).Play();
    }

    void Check(string raw)
    {
        if (!Core.IsAttendanceDay(DateTime.Today))
        {
            Show("오늘은 출석일이 아닙니다", Theme.Err, false);
            return;
        }
        var p = Core.FindByToken(raw);
        if (p == null) Show("등록되지 않은 QR", Theme.Err, false);
        else Mark(p.Id, p.Name);
    }

    void Mark(long id, string name)
    {
        if (Core.Mark(id, out var time) == MarkResult.Already)
        {
            Show($"{name}님\n이미 출석 ({time[..5]})", Theme.Warn, false);
            return;
        }
        Show($"{name}님\n출석 완료 {time[..5]}", Theme.Ok, true);
        RefreshToday();
    }

    void RefreshToday()
    {
        var day = DateTime.Today.ToString("yyyy-MM-dd");
        var rows = Core.Query(@"SELECT a.id, a.at, p.name, p.phone FROM attendance a JOIN people p ON p.id=a.person_id
                                WHERE a.day=@p0 ORDER BY a.at DESC", day);
        Fill(todayGrid, rows.Select(r => ((long)r[0], new[] { r[1], r[2], r[3] })));
        todayLbl.Text = $"오늘({day}) 출석  {rows.Count} / {Core.Scalar("SELECT COUNT(*) FROM people")}명";
    }

    void CancelAttendance()
    {
        var ids = todayGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => (long)r.Tag).ToList();
        if (ids.Count == 0)
        {
            MessageBox.Show("아래 목록에서 취소할 출석을 눌러 선택하세요.", "출석 취소", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show($"선택한 {ids.Count}건의 오늘 출석을 취소할까요?", "출석 취소",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var id in ids) Core.Exec("DELETE FROM attendance WHERE id=@p0", id);
        RefreshToday();
    }

    // --- 웹캠 전체화면 ---
    void OpenFullscreen()
    {
        fs = new Form
        {
            Text = "QR 출석", Icon = Icon, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Bounds = Screen.FromControl(this).Bounds, BackColor = Color.Black, KeyPreview = true, ShowInTaskbar = false,
        };
        fsVideo = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom };
        fsVideo.Paint += PaintGuide;
        fsResult = new Label
        {
            Text = "QR을 사각형 안에 맞춰 주세요", Dock = DockStyle.Top, Height = S(170), TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Core.FontName, 40, FontStyle.Bold), BackColor = Theme.Side, ForeColor = Color.White,
        };
        var esc = new Label
        {
            Text = "ESC 또는 더블클릭: 전체화면 종료", Dock = DockStyle.Bottom, Height = S(34),
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gray,
        };
        fs.Controls.Add(fsVideo);
        fs.Controls.Add(esc);
        fs.Controls.Add(fsResult);
        fsVideo.DoubleClick += (_, _) => fs?.Close();
        fs.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) fs?.Close();
        };
        // 스캔선은 카메라 프레임 속도와 무관하게 따로 그려서 부드럽게 움직이게 함
        var guideTimer = new System.Windows.Forms.Timer { Interval = 15 };
        guideTimer.Tick += (_, _) => fsVideo.Invalidate(Rectangle.Inflate(GuideBox(), S(8), S(8)));
        guideTimer.Start();
        fs.FormClosed += (_, _) =>
        {
            guideTimer.Dispose();
            fsVideo.Image?.Dispose();
            if (fs == null) return;
            fs = null;
            if (cam.On) ToggleCam();
        };
        fs.Show(this);
    }

    // 가운데 맞춤 가이드: 바깥은 어둡게, 모서리 표시 + 위아래로 움직이는 스캔선
    Rectangle GuideBox()
    {
        var r = fsVideo.ClientRectangle;
        int side = Math.Min(r.Width, r.Height) * 62 / 100;
        return new Rectangle((r.Width - side) / 2, (r.Height - side) / 2, side, side);
    }

    void PaintGuide(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = fsVideo.ClientRectangle;
        var box = GuideBox();
        int side = box.Width;

        using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
        using (var outside = new Region(r))
        {
            outside.Exclude(box);
            g.FillRegion(dim, outside);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool flash = (DateTime.Now - flashAt).TotalSeconds < 1.5;
        int len = side / 7;
        using (var pen = new Pen(flash ? flashColor : Color.White, S(7)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            foreach (var (x, dx) in new[] { (box.Left, 1), (box.Right, -1) })
                foreach (var (y, dy) in new[] { (box.Top, 1), (box.Bottom, -1) })
                    g.DrawLines(pen, new[] { new Point(x + dx * len, y), new Point(x, y), new Point(x, y + dy * len) });
        }

        // 양 끝에서 천천히 방향을 바꾸는 왕복 운동
        double t = (1 - Math.Cos(2 * Math.PI * guideClock.Elapsed.TotalSeconds / 2.6)) / 2;
        int lineY = box.Top + S(10) + (int)(t * (box.Height - S(20)));
        using (var glow = new Pen(Color.FromArgb(70, 129, 140, 248), S(9)))
            g.DrawLine(glow, box.Left + S(14), lineY, box.Right - S(14), lineY);
        using (var line = new Pen(Color.FromArgb(230, 165, 180, 252), S(3)))
            g.DrawLine(line, box.Left + S(14), lineY, box.Right - S(14), lineY);
    }

    void ToggleCam()
    {
        if (cam.On)
        {
            cam.Stop();
            camBtn.Text = "웹캠 전체화면 시작";
            var f = fs;
            fs = null;
            f?.Close();
        }
        else
        {
            camBtn.Text = "웹캠 끄기";
            OpenFullscreen();
            cam.Start(0,
                (bmp, text) => Ui(() => OnFrame(bmp, text)),
                msg => Ui(() =>
                {
                    if (cam.On) ToggleCam();
                    MessageBox.Show(msg, "웹캠", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
        }
    }

    void Ui(Action a)
    {
        try { BeginInvoke(a); } catch (InvalidOperationException) { } // 창이 닫히는 중
    }

    void OnFrame(Bitmap bmp, string text)
    {
        if (!cam.On || fs == null)
        {
            bmp.Dispose();
            return;
        }
        var old = fsVideo.Image;
        fsVideo.Image = bmp;
        old?.Dispose();
        if (text == "") return;
        // 같은 QR을 계속 대고 있어도 3초에 한 번만 처리
        var now = DateTime.Now;
        if (text != lastScan || (now - lastScanAt).TotalSeconds > 3) Check(text);
        (lastScan, lastScanAt) = (text, now);
    }

    // --- 명단 ---
    void BuildRoster()
    {
        var page = NewPage("명단 · QR 생성");
        var title = (Label)page.Tag;
        var help = Btn("?", ShowRosterHelp, Kind.Secondary);
        help.AutoSize = false;
        help.Padding = Padding.Empty;
        help.Size = new Size(S(30), S(30));
        help.Font = new Font(Core.FontName, 12, FontStyle.Bold);
        help.Location = new Point(TextRenderer.MeasureText(title.Text, title.Font).Width + S(6), (title.Height - S(30)) / 2);
        title.Controls.Add(help);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        bar.ControlAdded += (_, e) => e.Control.Margin = new Padding(0, 0, S(8), S(8));
        bar.Controls.Add(Btn("① 엑셀 양식 받기", SaveTemplate));
        bar.Controls.Add(Btn("② 엑셀 업로드", Upload));
        bar.Controls.Add(Btn("③ QR코드 인쇄", PrintCards));
        gridBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(150), Font = new Font(Core.FontName, 10.5f) };
        gridBox.Items.AddRange(new object[] { "2 X 2 (4개)", "3 X 3 (9개)", "4 X 4 (16개)" });
        gridBox.SelectedIndex = 0;
        var perPage = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        perPage.Controls.Add(new Label { Text = "A4 1장당", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, S(8), S(4), 0) });
        perPage.Controls.Add(gridBox);
        gridBox.Margin = new Padding(0, S(4), S(6), 0);
        bar.Controls.Add(perPage);
        bar.Controls.Add(Btn("QR JPG 저장", SaveCards, Kind.Secondary));

        // 체크한 사람만 인쇄/저장. 처음엔 전원 체크
        var pick = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(40), WrapContents = false, Padding = new Padding(S(12), S(6), 0, 0) };
        allBox = new CheckBox { Text = "전체 선택", AutoSize = true, Checked = true, Font = new Font(Core.FontName, 10.5f, FontStyle.Bold), Margin = new Padding(0, S(2), S(16), 0) };
        allBox.CheckedChanged += (_, _) =>
        {
            if (syncing) return;
            foreach (DataGridViewRow row in rosterGrid.Rows) SetPicked(row, allBox.Checked);
            UpdatePick();
        };
        rosterLbl = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, S(4), 0, 0) };
        pick.Controls.Add(allBox);
        pick.Controls.Add(rosterLbl);

        rosterGrid = Grid(("이름", 28), ("핸드폰번호", 32), ("출석일수", 16), ("최근 출석", 24));
        rosterGrid.Columns.Insert(0, new DataGridViewCheckBoxColumn { HeaderText = "선택", FillWeight = 10 });
        rosterGrid.DefaultCellStyle.SelectionBackColor = Color.White; // 행 강조 대신 체크박스로만 선택을 표시
        rosterGrid.Cursor = Cursors.Hand;
        void Toggle(object s, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var row = rosterGrid.Rows[e.RowIndex];
            SetPicked(row, !(bool)row.Cells[0].Value);
            UpdatePick();
        }
        rosterGrid.CellClick += Toggle;
        rosterGrid.CellDoubleClick += Toggle;
        page.Controls.Add(rosterGrid);
        page.Controls.Add(pick);
        page.Controls.Add(bar);
        page.Controls.Add((Control)page.Tag);
    }

    void ShowRosterHelp()
    {
        (string head, string body)[] steps =
        {
            ("1. 엑셀 양식 받기", "[① 엑셀 양식 받기]를 누르면 양식 파일이 열립니다.\n이름과 핸드폰번호를 한 줄에 한 명씩 적고 저장하세요."),
            ("2. 엑셀 업로드", "[② 엑셀 업로드]를 누르고 저장한 파일을 고르면 명단에 등록됩니다.\n이미 등록된 사람(이름과 번호가 같은 사람)은 건너뛰고, 기존 QR은 바뀌지 않습니다.\n이름이 같은 사람이 있으면 핸드폰번호를 꼭 적어야 서로 구분됩니다."),
            ("3. QR을 만들 사람 고르기", "명단 왼쪽의 체크가 켜진 사람만 QR이 만들어집니다. 처음에는 모두 켜져 있습니다.\n한 사람씩 누르면 켜고 끌 수 있고, [전체 선택]으로 한꺼번에 켜고 끕니다."),
            ("4. QR코드 인쇄", "[A4 1장당]에서 한 장에 몇 개를 넣을지 고른 뒤 [③ QR코드 인쇄]를 누릅니다.\n미리보기 창이 뜨면 왼쪽 위의 프린터 그림을 눌러 인쇄합니다."),
            ("5. QR JPG 저장", "[QR JPG 저장]을 누르고 폴더를 고르면 한 사람당 그림 파일 하나(이름.jpg)로 저장됩니다.\n핸드폰에 넣어 두고 화면을 카메라에 대도 출석이 됩니다."),
        };
        using var dlg = new Form
        {
            Text = "명단 · QR 생성 사용법", Icon = Icon, Font = Font, BackColor = Color.White, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent,
        };
        var list = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Location = new Point(S(26), S(18)) };
        foreach (var (head, body) in steps)
        {
            list.Controls.Add(new Label { Text = head, AutoSize = true, Font = new Font(Core.FontName, 12, FontStyle.Bold), ForeColor = Theme.Accent, Margin = new Padding(0, S(8), 0, S(4)) });
            list.Controls.Add(new Label { Text = body, AutoSize = true, MaximumSize = new Size(S(600), 0), ForeColor = Theme.Text, Margin = new Padding(S(2), 0, 0, S(8)) });
        }
        var close = Btn("닫기", dlg.Close);
        close.Margin = new Padding(0, S(10), 0, 0);
        list.Controls.Add(close);
        dlg.Controls.Add(list);
        var size = list.GetPreferredSize(Size.Empty);
        dlg.ClientSize = new Size(size.Width + S(52), size.Height + S(38));
        dlg.AcceptButton = dlg.CancelButton = close;
        if (snapshotDir != null)
        {
            // 셀프테스트: 창 모양만 저장하고 닫음
            dlg.Shown += (_, _) =>
            {
                using var bmp = new Bitmap(dlg.Width, dlg.Height);
                dlg.DrawToBitmap(bmp, new Rectangle(Point.Empty, dlg.Size));
                bmp.Save(Path.Combine(snapshotDir, "help.png"));
                dlg.Close();
            };
        }
        dlg.ShowDialog(this);
    }

    void RefreshRoster()
    {
        var rows = Core.Query(@"SELECT p.id, p.name, p.phone, COUNT(a.id), COALESCE(MAX(a.day), '')
                                FROM people p LEFT JOIN attendance a ON a.person_id=p.id
                                GROUP BY p.id ORDER BY p.name, p.phone");
        Fill(rosterGrid, rows.Select(r => ((long)r[0], new[] { !unpicked.Contains((long)r[0]), r[1], r[2], r[3], r[4] })));
        UpdatePick();
    }

    void SetPicked(DataGridViewRow row, bool on)
    {
        row.Cells[0].Value = on;
        if (on) unpicked.Remove((long)row.Tag);
        else unpicked.Add((long)row.Tag);
    }

    List<long> PickedIds() => rosterGrid.Rows.Cast<DataGridViewRow>().Where(r => (bool)r.Cells[0].Value).Select(r => (long)r.Tag).ToList();

    void UpdatePick()
    {
        int total = rosterGrid.Rows.Count, picked = PickedIds().Count;
        syncing = true;
        allBox.Checked = total > 0 && picked == total;
        syncing = false;
        rosterLbl.Text = $"체크한 사람의 QR만 인쇄·저장됩니다.   선택 {picked}명 / 총 {total}명";
    }

    void SaveTemplate()
    {
        using var dlg = new SaveFileDialog { Filter = "Excel|*.xlsx", FileName = "출석명단_양식.xlsx" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Core.WriteTemplate(dlg.FileName);
        }
        catch (IOException)
        {
            MessageBox.Show("파일이 엑셀에서 열려 있습니다. 닫고 다시 시도하세요.", "양식", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        OpenPath(dlg.FileName);
    }

    void Upload()
    {
        using var dlg = new OpenFileDialog { Filter = "Excel|*.xlsx;*.xlsm" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var (added, dup) = Core.ImportExcel(dlg.FileName);
            RefreshRoster();
            MessageBox.Show($"새로 등록 {added}명\n이미 등록되어 건너뜀 {dup}명", "업로드", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "업로드 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    List<Person> CardTargets()
    {
        var ids = PickedIds();
        if (ids.Count == 0)
        {
            MessageBox.Show(rosterGrid.Rows.Count == 0 ? "명단이 비어 있습니다. 먼저 엑셀을 업로드하세요." : "QR을 만들 사람을 체크하세요.",
                "QR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return new List<Person>();
        }
        return Core.People(ids);
    }

    void PrintCards()
    {
        var people = CardTargets();
        if (people.Count == 0) return;
        using var doc = CardDoc(people, gridBox.SelectedIndex + 2);
        using var dlg = new PrintPreviewDialog { Document = doc, Icon = Icon, Text = "QR코드 인쇄", Width = S(800), Height = S(700), StartPosition = FormStartPosition.CenterParent };
        dlg.ShowDialog(this);
    }

    /// <param name="grid">2 = 2X2, 3 = 3X3, 4 = 4X4 (A4 세로)</param>
    static PrintDocument CardDoc(List<Person> people, int grid)
    {
        int index = 0, perPage = grid * grid, cols = grid, rows = grid;
        var doc = new PrintDocument { DocumentName = "QR코드" };
        var a4 = doc.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(s => s.Kind == PaperKind.A4);
        if (a4 != null) doc.DefaultPageSettings.PaperSize = a4;

        // 단위 1/100 inch. 카드 비율(54:86)을 유지한 채 칸에 맞는 최대 크기
        const float ratio = 86f / 54f, margin = 30, gap = 12;
        var paper = doc.DefaultPageSettings.PaperSize;
        float w = Math.Min((paper.Width - 2 * margin - (cols - 1) * gap) / cols,
                           (paper.Height - 2 * margin - (rows - 1) * gap) / rows / ratio);
        float h = w * ratio;
        int scale = grid == 2 ? 2 : 1;

        doc.DefaultPageSettings.Landscape = false;
        doc.BeginPrint += (_, _) => index = 0;
        doc.PrintPage += (s, e) =>
        {
            var page = e.PageBounds;
            float x0 = (page.Width - cols * w - (cols - 1) * gap) / 2, y0 = (page.Height - rows * h - (rows - 1) * gap) / 2;
            if (!((PrintDocument)s).PrintController.IsPreview)
            {
                // 실제 인쇄는 원점이 인쇄 가능 영역 기준
                x0 -= e.PageSettings.HardMarginX;
                y0 -= e.PageSettings.HardMarginY;
            }
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            for (int k = 0; k < perPage && index < people.Count; k++, index++)
            {
                using var card = Core.MakeCard(people[index], scale);
                e.Graphics.DrawImage(card, x0 + (k % cols) * (w + gap), y0 + (k / cols) * (h + gap), w, h);
            }
            e.HasMorePages = index < people.Count;
        };
        return doc;
    }

    void SaveCards()
    {
        var people = CardTargets();
        if (people.Count == 0) return;
        using var dlg = new FolderBrowserDialog { Description = "QR 이미지를 저장할 폴더 선택", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Core.SaveCards(people, dlg.SelectedPath);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        OpenPath(dlg.SelectedPath);
    }

    // --- 내보내기 ---
    void BuildExport()
    {
        var page = NewPage("전체 출석일 내보내기");
        var first = Core.Scalar("SELECT MIN(day) FROM attendance") as string;
        // 날짜 글자 폭에 딱 맞춰서 상자 안에서 한쪽으로 쏠려 보이지 않게 함
        var dateFont = new Font(Core.FontName, 12);
        int dateWidth = TextRenderer.MeasureText("2026-10-10", dateFont).Width + SystemInformation.VerticalScrollBarWidth + S(14);
        DateTimePicker Picker(DateTime value) => new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Font = dateFont, Width = dateWidth, Value = value, Margin = new Padding(0),
        };
        d1 = Picker(first != null ? DateTime.Parse(first) : DateTime.Today);
        d2 = Picker(DateTime.Today);
        // 기본 날짜 상자는 글자가 왼쪽 테두리에 붙어 나오므로, 테두리를 잘라내고 여백 있는 상자로 감쌈
        Panel Boxed(DateTimePicker p)
        {
            const int b = 2;
            // 기본 달력·화살표 버튼은 서로 붙어 있어서 잘라내고, 간격을 둔 버튼을 직접 그림
            int textW = TextRenderer.MeasureText("2026-10-10", dateFont).Width + S(2);
            var clip = new Panel { Size = new Size(textW, p.Height - 2 * b), Location = new Point(S(9), S(4)), BackColor = Color.White };
            p.Location = new Point(-b, -b);
            clip.Controls.Add(p);
            var open = new Panel { Size = new Size(S(46), clip.Height), Location = new Point(clip.Right, clip.Top), BackColor = Color.White, Cursor = Cursors.Hand };
            open.Paint += (_, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(Theme.Muted, S(1));
                using var brush = new SolidBrush(Theme.Muted);
                int w = S(14), h = S(13), x = S(4), y = (open.Height - h) / 2;
                g.DrawRectangle(pen, x, y, w, h);
                g.FillRectangle(brush, x, y, w, S(4));
                for (int r = 0; r < 2; r++)
                    for (int c = 0; c < 3; c++)
                        g.FillRectangle(brush, x + S(3) + c * S(4), y + S(6) + r * S(3), S(2), S(2));
                int ax = x + w + S(12), ay = open.Height / 2 - S(2);
                g.FillPolygon(brush, new[] { new Point(ax, ay), new Point(ax + S(8), ay), new Point(ax + S(4), ay + S(5)) });
            };
            open.Click += (_, _) =>
            {
                p.Focus();
                SendKeys.Send("%{DOWN}");
            };
            var box = new Panel { Size = new Size(open.Right + S(2), clip.Height + S(8)), BackColor = Color.White, Margin = new Padding(0) };
            box.Paint += (_, e) =>
            {
                using var pen = new Pen(Color.FromArgb(148, 163, 184));
                e.Graphics.DrawRectangle(pen, 0, 0, box.Width - 1, box.Height - 1);
            };
            box.Controls.Add(clip);
            box.Controls.Add(open);
            return box;
        }
        Panel box1 = Boxed(d1), box2 = Boxed(d2);
        Label RowLabel(string text, FontStyle style, int side) => new Label
        {
            Text = text, AutoSize = false, Width = TextRenderer.MeasureText(text, new Font(Core.FontName, 12, style)).Width + S(side * 2), Height = box1.Height,
            TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Core.FontName, 12, style), Margin = new Padding(0),
        };

        var card = new Panel { Dock = DockStyle.Top, Height = S(150), BackColor = Color.White, Padding = new Padding(S(22), S(20), S(22), S(16)) };
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(50), WrapContents = false };
        var period = RowLabel("기간", FontStyle.Bold, 0);
        period.Margin = new Padding(0, 0, S(12), 0);
        row.Controls.Add(period);
        row.Controls.Add(box1);
        row.Controls.Add(RowLabel("~", FontStyle.Regular, 8));
        row.Controls.Add(box2);
        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        btnRow.Controls.Add(Btn("엑셀로 내보내기", Export));
        card.Controls.Add(btnRow);
        card.Controls.Add(row);

        var info = new Label
        {
            Dock = DockStyle.Fill, ForeColor = Theme.Muted, Padding = new Padding(S(4), S(20), 0, 0),
            Text = "이름이 세로(행), 날짜가 가로(열)로 저장됩니다. 출석한 날은 출석 시간, 안 한 날은 - 로 표시됩니다.\n\n" +
                   $"데이터 저장 위치 (이 PC 안에만 저장되며 인터넷으로 전송하지 않습니다)\n{Core.DbPath}\n" +
                   "백업하려면 위 파일을 복사해 두세요.",
        };
        page.Controls.Add(info);
        page.Controls.Add(card);
        page.Controls.Add((Control)page.Tag);
    }

    void Export()
    {
        string a = d1.Value.ToString("yyyy-MM-dd"), b = d2.Value.ToString("yyyy-MM-dd");
        using var dlg = new SaveFileDialog { Filter = "Excel|*.xlsx", FileName = $"출석_{a}_{b}.xlsx" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int days;
        try
        {
            days = Core.ExportExcel(dlg.FileName, a, b);
        }
        catch (IOException)
        {
            MessageBox.Show("파일이 엑셀에서 열려 있습니다. 닫고 다시 시도하세요.", "내보내기", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        MessageBox.Show($"출석이 있는 날 {days}일치를 저장했습니다.", "내보내기", MessageBoxButtons.OK, MessageBoxIcon.Information);
        OpenPath(dlg.FileName);
    }
}
