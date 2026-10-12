using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace QrAttendance;

class Camera
{
    volatile bool on;

    public bool On => on;

    /// <summary>onFrame/onError는 카메라 스레드에서 호출됨</summary>
    public void Start(int index, Action<System.Drawing.Bitmap, string> onFrame, Action<string> onError)
    {
        on = true;
        new Thread(() => Loop(index, onFrame, onError)) { IsBackground = true }.Start();
    }

    public void Stop() => on = false;

    void Loop(int index, Action<System.Drawing.Bitmap, string> onFrame, Action<string> onError)
    {
        try
        {
            using var cap = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
            if (!cap.IsOpened())
            {
                onError($"카메라 {index}번을 열 수 없습니다.");
                return;
            }
            cap.Set(VideoCaptureProperties.FrameWidth, 1280);
            cap.Set(VideoCaptureProperties.FrameHeight, 720);
            using var det = new QRCodeDetector();
            using var frame = new Mat();
            int n = 0;
            while (on)
            {
                if (!cap.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(50);
                    continue;
                }
                string text = Read(det, frame, n++);
                Cv2.Flip(frame, frame, FlipMode.Y);
                onFrame(BitmapConverter.ToBitmap(frame), text);
            }
        }
        catch (Exception e)
        {
            onError(e.Message);
        }
    }

    static readonly ZXing.BarcodeReaderGeneric zxing = new()
    {
        Options = { PossibleFormats = new[] { ZXing.BarcodeFormat.QR_CODE }, TryHarder = true },
    };

    // 빠른 ZXing으로 먼저 읽고, 못 읽으면 OpenCV로 한 번 더. OpenCV는 느려서(프레임당 수십 ms) 화면이 밀리지 않게 3프레임에 한 번만
    static string Read(QRCodeDetector det, Mat frame, int n = 0)
    {
        var text = ReadZxing(frame);
        if (text != "" || n % 3 != 0) return text;
        try { return det.DetectAndDecode(frame, out _) ?? ""; }
        catch (OpenCVException) { return ""; }
    }

    public static string ReadZxing(Mat frame)
    {
        using var gray = new Mat();
        if (frame.Channels() == 1) frame.CopyTo(gray);
        else Cv2.CvtColor(frame, gray, frame.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        var bytes = new byte[gray.Rows * gray.Cols];
        for (int y = 0; y < gray.Rows; y++)
            System.Runtime.InteropServices.Marshal.Copy(gray.Ptr(y), bytes, y * gray.Cols, gray.Cols);
        var src = new ZXing.RGBLuminanceSource(bytes, gray.Cols, gray.Rows, ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
        try { return zxing.Decode(src)?.Text ?? ""; }
        catch (Exception) { return ""; }
    }

    public static string Decode(System.Drawing.Bitmap bmp)
    {
        using var mat = BitmapConverter.ToMat(bmp);
        using var det = new QRCodeDetector();
        return Read(det, mat);
    }

    // 셀프테스트: 웹캠 화면처럼 작게·흐리게·뒤집어 놓고 두 인식기가 각각 읽는지와 걸린 시간
    public static string Probe(System.Drawing.Bitmap card, string token)
    {
        using var src = BitmapConverter.ToMat(card);
        using var det = new QRCodeDetector();
        var log = new System.Text.StringBuilder();
        foreach (var (name, height, blur, mirror) in new[] { ("big", 600, 0, false), ("mirror", 600, 0, true), ("small", 220, 0, false), ("small+blur", 220, 5, false), ("tiny+blur", 150, 3, false), ("blur+mirror", 300, 7, true) })
        {
            using var frame = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(140, 130, 120));
            using var small = new Mat();
            Cv2.Resize(src, small, new OpenCvSharp.Size(src.Cols * height / src.Rows, height), 0, 0, InterpolationFlags.Area);
            if (small.Channels() == 4) Cv2.CvtColor(small, small, ColorConversionCodes.BGRA2BGR);
            small.CopyTo(new Mat(frame, new Rect(500, 60, small.Cols, small.Rows)));
            if (blur > 0) Cv2.GaussianBlur(frame, frame, new OpenCvSharp.Size(blur, blur), 0);
            if (mirror) Cv2.Flip(frame, frame, FlipMode.Y);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string a = "";
            try { a = det.DetectAndDecode(frame, out _) ?? ""; } catch (OpenCVException) { }
            long ta = sw.ElapsedMilliseconds;
            sw.Restart();
            string b = ReadZxing(frame);
            log.AppendLine($"probe {name}: opencv={a == token} {ta}ms / zxing={b == token} {sw.ElapsedMilliseconds}ms");
        }
        using (var empty = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(140, 130, 120)))
        {
            foreach (var kind in new[] { "plain", "noise", "noise+blur" })
            {
                if (kind == "noise") Cv2.Randn(empty, new Scalar(128, 128, 128), new Scalar(50, 50, 50));
                if (kind == "noise+blur") Cv2.GaussianBlur(empty, empty, new OpenCvSharp.Size(9, 9), 0);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 5; i++) det.DetectAndDecode(empty, out _);
                long ta = sw.ElapsedMilliseconds / 5;
                sw.Restart();
                for (int i = 0; i < 5; i++) ReadZxing(empty);
                log.AppendLine($"probe no-qr {kind}: opencv={ta}ms / zxing={sw.ElapsedMilliseconds / 5}ms");
            }
        }
        return log.ToString();
    }
}
