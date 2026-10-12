using System.Drawing.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using QRCoder;

namespace QrAttendance;

record Person(long Id, string Name, string Phone, string Token);

enum MarkResult { Ok, Already }

static class Core
{
    public static string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    public static string DbPath => Path.Combine(DataDir, "attendance.db");
    public const string FontName = "맑은 고딕";

    // QR 카드 54 x 86 비율, 300dpi
    public const int CardW = 638, CardH = 1016;

    static SqliteConnection con;

    // QR 카드 이름용 글꼴(주아체, OFL). exe에 내장해서 다른 PC에서도 같은 모양으로 나옴
    static readonly PrivateFontCollection cardFonts = new();
    static FontFamily CardFont
    {
        get
        {
            if (cardFonts.Families.Length == 0)
            {
                using var s = typeof(Core).Assembly.GetManifestResourceStream("Jua.ttf");
                var bytes = new byte[s.Length];
                s.ReadExactly(bytes);
                var mem = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(bytes.Length); // GDI+가 계속 참조하므로 해제하지 않음
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, mem, bytes.Length);
                cardFonts.AddMemoryFont(mem, bytes.Length);
            }
            return cardFonts.Families[0];
        }
    }

    // ---------- DB ----------
    public static void Open()
    {
        Directory.CreateDirectory(DataDir);
        con = new SqliteConnection($"Data Source={DbPath}");
        con.Open();
        Exec("PRAGMA foreign_keys=ON");
        Exec(@"
            CREATE TABLE IF NOT EXISTS people(
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                phone TEXT NOT NULL DEFAULT '',
                token TEXT NOT NULL UNIQUE,
                created TEXT NOT NULL,
                UNIQUE(name, phone)
            );
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS attendance(
                id INTEGER PRIMARY KEY,
                person_id INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
                day TEXT NOT NULL,
                at TEXT NOT NULL,
                UNIQUE(person_id, day)
            );");
        // 예전 버전이 난수로 만든 QR 값을 이름+번호 다이제스트로 바꿈
        foreach (var p in People())
            if (p.Token != Token(p.Name, p.Phone))
                Exec("UPDATE people SET token=@p0 WHERE id=@p1", Token(p.Name, p.Phone), p.Id);
    }

    /// <summary>QR에 넣는 값: SHA-256(이름|번호 숫자)에서 뽑은 16자리 숫자. 어느 PC에서 등록해도 같은 값</summary>
    public static string Token(string name, string phone)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim() + "|" + Regex.Replace(phone ?? "", @"\D", "")));
        return (BitConverter.ToUInt64(hash, 0) % 10_000_000_000_000_000UL).ToString("D16");
    }

    public static void Close()
    {
        con?.Dispose();
        con = null;
        SqliteConnection.ClearAllPools();
    }

    static SqliteCommand Cmd(string sql, object[] args)
    {
        var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("@p" + i, args[i]);
        return cmd;
    }

    public static int Exec(string sql, params object[] args)
    {
        using var cmd = Cmd(sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static object Scalar(string sql, params object[] args)
    {
        using var cmd = Cmd(sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public static List<object[]> Query(string sql, params object[] args)
    {
        using var cmd = Cmd(sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<object[]>();
        while (r.Read())
        {
            var row = new object[r.FieldCount];
            r.GetValues(row);
            list.Add(row);
        }
        return list;
    }

    // 출석 요일: 한 번 정하면 바꿀 때까지 매주 적용. 하나도 안 고르면 요일 제한 없음
    public static HashSet<DayOfWeek> GetDays()
    {
        var v = Scalar("SELECT value FROM settings WHERE key='days'") as string ?? "";
        return v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => (DayOfWeek)int.Parse(s)).ToHashSet();
    }

    public static void SetDays(IEnumerable<DayOfWeek> days) =>
        Exec("INSERT OR REPLACE INTO settings(key, value) VALUES('days', @p0)", string.Join(",", days.Select(d => (int)d)));

    public static bool IsAttendanceDay(DateTime date)
    {
        var days = GetDays();
        return days.Count == 0 || days.Contains(date.DayOfWeek);
    }

    public static List<Person> People(IEnumerable<long> ids = null)
    {
        var sql = "SELECT id, name, phone, token FROM people";
        if (ids != null) sql += $" WHERE id IN ({string.Join(",", ids)})";
        return Query(sql + " ORDER BY name, phone")
            .Select(r => new Person((long)r[0], (string)r[1], (string)r[2], (string)r[3])).ToList();
    }

    public static Person FindByToken(string raw)
    {
        var token = Regex.Replace(raw ?? "", @"\D", "");
        if (token == "") return null;
        var r = Query("SELECT id, name, phone, token FROM people WHERE token=@p0", token).FirstOrDefault();
        return r == null ? null : new Person((long)r[0], (string)r[1], (string)r[2], (string)r[3]);
    }

    /// <summary>하루 1회만 기록. time = 기록된(또는 기존) 출석 시각</summary>
    public static MarkResult Mark(long personId, out string time)
    {
        var now = DateTime.Now;
        var day = now.ToString("yyyy-MM-dd");
        var prev = Scalar("SELECT at FROM attendance WHERE person_id=@p0 AND day=@p1", personId, day) as string;
        if (prev != null)
        {
            time = prev;
            return MarkResult.Already;
        }
        time = now.ToString("HH:mm:ss");
        Exec("INSERT INTO attendance(person_id, day, at) VALUES(@p0,@p1,@p2)", personId, day, time);
        return MarkResult.Ok;
    }

    // ---------- 명단 ----------
    public static string NormPhone(string v)
    {
        var s = Regex.Replace(v ?? "", @"\D", "");
        if ((s.Length == 9 || s.Length == 10) && !s.StartsWith('0')) s = "0" + s; // 엑셀이 숫자로 읽어 앞자리 0이 빠진 경우
        if (s.Length == 11) return $"{s[..3]}-{s[3..7]}-{s[7..]}";
        if (s.Length == 10) return $"{s[..3]}-{s[3..6]}-{s[6..]}";
        return s;
    }

    /// <summary>이미 있으면 false</summary>
    public static bool AddPerson(string name, string phone)
    {
        name = (name ?? "").Trim();
        phone = NormPhone(phone);
        if (Scalar("SELECT 1 FROM people WHERE name=@p0 AND phone=@p1", name, phone) != null) return false;
        Exec("INSERT INTO people(name, phone, token, created) VALUES(@p0,@p1,@p2,@p3)",
            name, phone, Token(name, phone), DateTime.Now.ToString("s"));
        return true;
    }

    /// <summary>같은 이름+번호인 다른 사람이 있으면 false. 이름이나 번호가 바뀌면 QR 값도 바뀜</summary>
    public static bool UpdatePerson(long id, string name, string phone)
    {
        name = (name ?? "").Trim();
        phone = NormPhone(phone);
        if (Scalar("SELECT 1 FROM people WHERE name=@p0 AND phone=@p1 AND id<>@p2", name, phone, id) != null) return false;
        Exec("UPDATE people SET name=@p0, phone=@p1, token=@p2 WHERE id=@p3", name, phone, Token(name, phone), id);
        return true;
    }

    /// <summary>출석 기록도 함께 삭제</summary>
    public static void DeletePeople(IEnumerable<long> ids)
    {
        foreach (var id in ids)
        {
            Exec("DELETE FROM attendance WHERE person_id=@p0", id);
            Exec("DELETE FROM people WHERE id=@p0", id);
        }
    }

    public static (int added, int dup) ImportExcel(string path)
    {
        var rows = new List<string[]>();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var wb = new XLWorkbook(fs))
        {
            var ws = wb.Worksheet(1);
            int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            foreach (var row in ws.RowsUsed())
                rows.Add(Enumerable.Range(1, lastCol).Select(c => row.Cell(c).GetString().Trim()).ToArray());
        }

        string[] phoneKeys = { "핸드폰", "휴대폰", "전화", "연락처", "번호" };
        int ni = 0, pi = 1, start = 0;
        for (int i = 0; i < Math.Min(10, rows.Count); i++)
        {
            var cells = rows[i].Select(c => c.Replace(" ", "")).ToArray();
            int n = Array.FindIndex(cells, c => c.Contains("이름") || c.Contains("성명"));
            if (n < 0) continue;
            int p = Array.FindIndex(cells, c => phoneKeys.Any(c.Contains));
            (ni, pi, start) = (n, p >= 0 ? p : n + 1, i + 1);
            break;
        }

        int added = 0, dup = 0;
        Exec("BEGIN");
        try
        {
            foreach (var r in rows.Skip(start))
            {
                var name = ni < r.Length ? r[ni] : "";
                if (name == "") continue;
                if (AddPerson(name, NormPhone(pi < r.Length ? r[pi] : ""))) added++;
                else dup++;
            }
            Exec("COMMIT");
        }
        catch
        {
            Exec("ROLLBACK");
            throw;
        }
        return (added, dup);
    }

    static void StyleHeader(IXLWorksheet ws, int cols)
    {
        var h = ws.Range(1, 1, 1, cols);
        h.Style.Font.Bold = true;
        h.Style.Fill.BackgroundColor = XLColor.FromHtml("#DDEBF7");
        h.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.SheetView.FreezeRows(1);
    }

    public static void WriteTemplate(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("명단");
        ws.Cell(1, 1).Value = "이름";
        ws.Cell(1, 2).Value = "핸드폰번호";
        ws.Column(1).Width = 16;
        ws.Column(2).Width = 20;
        ws.Column(2).Style.NumberFormat.Format = "@"; // 텍스트 서식: 010의 0이 지워지지 않게
        StyleHeader(ws, 2);
        wb.SaveAs(path);
    }

    /// <returns>표에 들어간 날짜 수</returns>
    public static int ExportExcel(string path, string d1, string d2)
    {
        var days = Query("SELECT DISTINCT day FROM attendance WHERE day BETWEEN @p0 AND @p1 ORDER BY day", d1, d2)
            .Select(r => (string)r[0]).ToList();
        // 출석 요일이 정해져 있으면 아무도 안 온 출석일도 열로 넣음 (기존 기록이 있는 날은 요일과 무관하게 유지)
        var weekdays = GetDays();
        if (weekdays.Count > 0)
        {
            for (var d = DateTime.Parse(d1); d <= DateTime.Parse(d2); d = d.AddDays(1))
                if (weekdays.Contains(d.DayOfWeek)) days.Add(d.ToString("yyyy-MM-dd"));
            days = days.Distinct().OrderBy(s => s).ToList();
        }
        var att = Query("SELECT person_id, day, at FROM attendance WHERE day BETWEEN @p0 AND @p1", d1, d2)
            .ToDictionary(r => ((long)r[0], (string)r[1]), r => (string)r[2]);

        // 행: 이름, 열: 날짜. 출석했으면 시간, 안 했으면 "-"
        var people = People();
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("출석표");
        ws.Cell(1, 1).Value = "이름";
        for (int c = 0; c < days.Count; c++) ws.Cell(1, 2 + c).Value = days[c];
        for (int r = 0; r < people.Count; r++)
        {
            ws.Cell(2 + r, 1).Value = people[r].Name;
            for (int c = 0; c < days.Count; c++)
                ws.Cell(2 + r, 2 + c).Value = att.TryGetValue((people[r].Id, days[c]), out var t) ? t[..5] : "-";
        }
        ws.Column(1).Width = 14;
        if (days.Count > 0) ws.Columns(2, 1 + days.Count).Width = 12;
        ws.Range(1, 1, 1 + people.Count, 1 + days.Count).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        StyleHeader(ws, 1 + days.Count);
        ws.SheetView.FreezeColumns(1);

        wb.SaveAs(path);
        return days.Count;
    }

    // ---------- QR 카드 ----------
    /// <param name="scale">크게 인쇄할 때 글자가 뭉개지지 않도록 해상도 배율</param>
    public static Bitmap MakeCard(Person p, int scale = 1)
    {
        var bmp = new Bitmap(CardW * scale, CardH * scale);
        bmp.SetResolution(300 * scale, 300 * scale);
        using var g = Graphics.FromImage(bmp);
        g.ScaleTransform(scale, scale);
        g.Clear(Color.White);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var pen = new Pen(Color.FromArgb(150, 150, 150), 3))
            g.DrawRectangle(pen, 1, 1, CardW - 3, CardH - 3);

        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };
        float size = 140;
        var font = new Font(CardFont, size, FontStyle.Regular, GraphicsUnit.Pixel);
        while (size > 30 && g.MeasureString(p.Name, font).Width > CardW - 50)
        {
            font.Dispose();
            size -= 6;
            font = new Font(CardFont, size, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        g.DrawString(p.Name, font, Brushes.Black, new RectangleF(0, 100, CardW, 240), sf);
        font.Dispose();

        using (var gen = new QRCodeGenerator())
        using (var data = gen.CreateQrCode(p.Token, QRCodeGenerator.ECCLevel.M))
        {
            var m = data.ModuleMatrix; // 여백(quiet zone) 포함
            int n = m.Count, cell = 540 / n, x0 = (CardW - cell * n) / 2, y0 = 390 + (540 - cell * n) / 2;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (m[y][x]) g.FillRectangle(Brushes.Black, x0 + x * cell, y0 + y * cell, cell, cell);
        }
        return bmp;
    }

    public static void SaveCards(IEnumerable<Person> people, string dir)
    {
        Directory.CreateDirectory(dir);
        var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using var quality = new System.Drawing.Imaging.EncoderParameters(1);
        quality.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
        var used = new Dictionary<string, int>();
        foreach (var p in people)
        {
            var name = Regex.Replace(p.Name, @"[\\/:*?""<>|]", "_");
            // 동명이인은 덮어쓰지 않게 _2, _3
            int n = used[name] = used.GetValueOrDefault(name) + 1;
            using var bmp = MakeCard(p);
            bmp.Save(Path.Combine(dir, (n == 1 ? name : $"{name}_{n}") + ".jpg"), jpeg, quality);
        }
    }
}
