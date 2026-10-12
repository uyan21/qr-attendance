using System.Text;
using ClosedXML.Excel;

namespace QrAttendance;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--selftest")
        {
            SelfTest(args[1]);
            return;
        }
        try
        {
            Core.Open();
        }
        catch (Exception e)
        {
            MessageBox.Show($"데이터 파일을 열 수 없습니다.\n{Core.DbPath}\n\n{e.Message}", "QR 출석", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new MainForm());
        Core.Close();
    }

    // QR출석.exe --selftest <빈 폴더> : 임시 DB로 가져오기/QR/출석/내보내기를 돌려 result.txt에 기록
    static void SelfTest(string dir)
    {
        var log = new StringBuilder();
        try
        {
            Core.DataDir = Path.Combine(dir, "data");
            Core.Open();
            var xlsx = Path.Combine(dir, "명단.xlsx");
            Core.WriteTemplate(xlsx);
            using (var wb = new XLWorkbook(xlsx))
            {
                var ws = wb.Worksheet(1);
                ws.Cell(2, 1).Value = "홍길동"; ws.Cell(2, 2).Value = "010-1234-5678";
                ws.Cell(3, 1).Value = "김철수"; ws.Cell(3, 2).Value = 1098765432;
                ws.Cell(4, 1).Value = "남궁민수황보"; ws.Cell(4, 2).Value = "01011112222";
                ws.Cell(6, 1).Value = "홍길동"; ws.Cell(6, 2).Value = "01012345678";
                wb.Save();
            }
            log.AppendLine($"import {Core.ImportExcel(xlsx)} / re-import {Core.ImportExcel(xlsx)}");
            var people = Core.People();
            foreach (var p in people)
            {
                using var card = Core.MakeCard(p);
                log.AppendLine($"{p.Name} {p.Phone} {p.Token} decode={(Camera.Decode(card) == p.Token)}");
            }
            using (var card = Core.MakeCard(people[0]))
                log.Append(Camera.Probe(card, people[0].Token));
            Core.SaveCards(people, Path.Combine(dir, "qr"));
            var r1 = Core.Mark(people[0].Id, out var t1);
            var r2 = Core.Mark(people[0].Id, out _);
            log.AppendLine($"mark {r1} {t1} / again {r2} / unknown={(Core.FindByToken("999") == null)} / byToken={Core.FindByToken(people[1].Token)?.Name}");
            Core.Exec("INSERT INTO attendance(person_id, day, at) VALUES(@p0,'2026-10-01','09:01:02')", people[1].Id);
            var outp = Path.Combine(dir, "export.xlsx");
            log.AppendLine($"export days={Core.ExportExcel(outp, "2026-01-01", "2099-12-31")}");
            Core.SetDays(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Thursday });
            log.AppendLine($"월화목 days={Core.ExportExcel(outp, "2026-09-28", DateTime.Today.ToString("yyyy-MM-dd"))} today ok={Core.IsAttendanceDay(DateTime.Today)}");
            using (var wb = new XLWorkbook(outp))
                foreach (var ws in wb.Worksheets)
                    foreach (var row in ws.RowsUsed())
                        log.AppendLine($"[{ws.Name}] " + string.Join(" | ", row.Cells(1, ws.LastColumnUsed().ColumnNumber()).Select(c => c.GetString())));
            using (var form = new MainForm())
            {
                form.Show();
                form.Snapshot(dir);
                log.AppendLine("form ok");
            }
            var hong = Core.People().First(p => p.Name == "홍길동");
            log.AppendLine($"add {Core.AddPerson(" 이영희 ", "1055556666")} / add dup {Core.AddPerson("이영희", "010-5555-6666")}");
            log.AppendLine($"update {Core.UpdatePerson(hong.Id, "홍길순", "01012345678")} / update dup {Core.UpdatePerson(hong.Id, "이영희", "01055556666")}");
            var after = Core.People().First(p => p.Id == hong.Id);
            log.AppendLine($"updated {after.Name} {after.Phone} token same={after.Token == hong.Token}");
            Core.DeletePeople(new[] { hong.Id });
            log.AppendLine($"delete people={Core.People().Count} hong att={Core.Scalar("SELECT COUNT(*) FROM attendance WHERE person_id=@p0", hong.Id)} names={string.Join(",", Core.People().Select(p => p.Name))}");
            Core.Close();
        }
        catch (Exception e)
        {
            log.AppendLine("FAIL " + e);
        }
        File.WriteAllText(Path.Combine(dir, "result.txt"), log.ToString(), Encoding.UTF8);
    }
}
