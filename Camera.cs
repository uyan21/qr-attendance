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
            while (on)
            {
                if (!cap.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(50);
                    continue;
                }
                string text = "";
                try { text = det.DetectAndDecode(frame, out _) ?? ""; } catch (OpenCVException) { }
                onFrame(BitmapConverter.ToBitmap(frame), text);
            }
        }
        catch (Exception e)
        {
            onError(e.Message);
        }
    }

    public static string Decode(System.Drawing.Bitmap bmp)
    {
        using var mat = BitmapConverter.ToMat(bmp);
        using var det = new QRCodeDetector();
        return det.DetectAndDecode(mat, out _) ?? "";
    }
}
