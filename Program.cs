using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tesseract;
class Program
{

    static readonly string imagesToBeProcessed = @"D:\FilesToBeCracked";


    static void Main()
    {
        Directory.CreateDirectory(imagesToBeProcessed);

        // Start watcher if you want it running at the same time (optional)
        var p = new Program();
        System.Threading.Tasks.Task.Run(() => p.StartWatcher()); // uncomment if you want

        Application.Run(new HotkeyForm(() =>
        {
            // This runs whenever you press C anywhere
            p.ScreenCapture();
            Console.WriteLine("Captured via global C.");
        }));

        Console.WriteLine("Exited.");
    }


    public void StartWatcher()
    {
        //Speify directory to monitor
        using var watcher = new FileSystemWatcher(imagesToBeProcessed);

        //Enable filter
        watcher.Filter = "*.png";

        watcher.Created += OnCreated;

        watcher.EnableRaisingEvents = true;

        Console.WriteLine("Press any key to exit.");
        Console.ReadLine();
    }

    private static void OnCreated(object sender, FileSystemEventArgs e)
    {
        //Wait untill file in done being written.
        if (!WaitForFileReady(e.FullPath, 5000))
            return;
        string text = Ocr.ReadTextFromPng(e.FullPath);
        Console.WriteLine($"OCR for {Path.GetFileName(e.FullPath)}:");
        Console.WriteLine(text);
       
    }
    public void ScreenCapture()
    {
        //Define screen bounds
        Rectangle bounds = Screen.PrimaryScreen.Bounds;

        Rectangle chromeContent = new Rectangle(
            8,
            130,
            1904,
            900);
        Directory.CreateDirectory(imagesToBeProcessed);

        string fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
        string fullPath = Path.Combine(imagesToBeProcessed, fileName);

        using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    chromeContent.X,
                    chromeContent.Y,
                    0,
                    0,
                    chromeContent.Size);
            }
            using (Graphics g = Graphics.FromImage(bitmap))
            using (Pen pen = new Pen(Color.Red, 5))
            {
                g.DrawRectangle(pen, 0, 0, bitmap.Width - 1, bitmap.Height - 1);
                g.DrawString($"{chromeContent}", new Font("Arial", 16), Brushes.Red, new PointF(10, 10));
            }
            // Scale 2x
            int scaleFactor = 2;

            using Bitmap scaled = new Bitmap(
                bitmap.Width * scaleFactor,
                bitmap.Height * scaleFactor
            );

            using (Graphics g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bitmap, 0, 0, scaled.Width, scaled.Height);
            }

            scaled.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    private static bool WaitForFileReady(string path, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (IOException) { System.Threading.Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(50); }
        }
        return false;
    }

   
 
}

class Ocr
{
    public static string ReadTextFromPng(string imagePath)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Image not found", imagePath);

        string tessDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

        using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
        using var image = Pix.LoadFromFile(imagePath);
        using var page = engine.Process(image);

        return page.GetText();
    }
}
class HotkeyForm : Form
{
    private readonly Action _onHotkey;

    public HotkeyForm(Action onHotkey)
    {
        _onHotkey = onHotkey;

        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Opacity = 0; // hide it
        Load += (_, __) => Hide();

        // Register plain 'C' globally
        RegisterHotKey(this.Handle, HOTKEY_ID, MOD_NONE, VK_C);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(this.Handle, HOTKEY_ID);
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            _onHotkey();

        base.WndProc(ref m);
    }

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 1;

    private const uint MOD_NONE = 0x0000;
    private const uint VK_C = 0x43;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
