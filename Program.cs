using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

using System;
using System.Collections.Generic;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

class Program
{
    static readonly string imagesToBeProcessed = @"D:\FilesToBeProcessed";

    // BIG "safe" capture region (tune these if needed)
    static readonly Rectangle CaptureRegion = new Rectangle(
        0,    // X
        75,   // Y
        1200, // Width
        850   // Height
    );

    // Tile grid: start with 2x3; if still messy, try 3x3
    const int TileCols = 1;
    const int TileRows = 8;

    static SerialPort arduino;
    static readonly object serialLock = new object();

    static readonly SemaphoreSlim pipelineGate = new SemaphoreSlim(1, 1);

    [STAThread]
    static void Main()
    {
        Directory.CreateDirectory(imagesToBeProcessed);

        InitializeSerial();
        SendToArduino("READY");

        AppDomain.CurrentDomain.ProcessExit += (_, __) => CleanupSerial();
        Console.CancelKeyPress += (_, e) => { CleanupSerial(); e.Cancel = false; };

        Application.Run(new HotkeyForm(() =>
        {
            _ = Task.Run(async () =>
            {
                if (!await pipelineGate.WaitAsync(0))
                    return;

                try
                {
                    SendToArduino("CAPTURING...");

                    // 1) Capture tiles (saved as PNG files)
                    var tilePaths = ScreenCaptureTiles(imagesToBeProcessed, CaptureRegion, TileCols, TileRows);


                    // 2) Ask OpenAI
                    SendToArduino("PROCESSING...");
                    string answer = await ContactOpenAI(tilePaths);

                    // 3) Enforce LCD limit hard
                    answer = SanitizeTo32(answer);

                    // 4) Display
                    SendToArduino(answer);

                    Console.WriteLine("AI:");
                    Console.WriteLine(answer);

                    // Optional: delete tiles after each run (keeps folder clean)
                    SafeDeleteFiles(tilePaths);

                    
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    SendToArduino("ERROR");
                }
                finally
                {
                    pipelineGate.Release();
                }
            });
        }));
    }

    // ------------------- Capture (Tiles) -------------------

    static List<string> ScreenCaptureTiles(string folder, Rectangle region, int cols, int rows)
    {
        Directory.CreateDirectory(folder);

        int tileW = Math.Max(1, region.Width / cols);
        int tileH = Math.Max(1, region.Height / rows);

        var paths = new List<string>(cols * rows);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int x = region.X + c * tileW;
                int y = region.Y + r * tileH;

                // last row/col takes remainder so we fully cover region
                int w = (c == cols - 1) ? (region.X + region.Width - x) : tileW;
                int h = (r == rows - 1) ? (region.Y + region.Height - y) : tileH;

                Rectangle tile = new Rectangle(x, y, w, h);

                string fileName = $"tile_{r}_{c}_{stamp}.png";
                string fullPath = Path.Combine(folder, fileName);

                using (Bitmap bmp = new Bitmap(tile.Width, tile.Height, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(tile.X, tile.Y, 0, 0, tile.Size, CopyPixelOperation.SourceCopy);
                    bmp.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
                }

                paths.Add(fullPath);
            }
        }

        return paths;
    }

    static void SafeDeleteFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch { /* ignore */ }
        }
    }

    // ------------------- Serial / LCD -------------------

    static void InitializeSerial()
    {
        arduino = new SerialPort("COM15", 9600)
        {
            NewLine = "\n",
            Encoding = Encoding.ASCII
        };
         arduino.Open();
    }

    static void CleanupSerial()
    {
        lock (serialLock)
        {
            try
            {
                if (arduino != null)
                {
                    if (arduino.IsOpen) arduino.Close();
                    arduino.Dispose();
                }
            }
            catch { /* ignore */ }
        }
    }

    static void SendToArduino(string msg)
    {
        lock (serialLock)
        {
            if (arduino == null || !arduino.IsOpen)
                return;

            msg = SanitizeTo32(msg);

            string line1 = msg.Length > 16 ? msg.Substring(0, 16) : msg.PadRight(16);
            string line2 = msg.Length > 16 ? msg.Substring(16).PadRight(16) : new string(' ', 16);

            arduino.WriteLine("CLR");
            arduino.WriteLine(line1);
            arduino.WriteLine(line2);
        }
    }

    static string SanitizeTo32(string s)
    {
        s = (s ?? "").Trim().Replace("\r", "").Replace("\n", " ");
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        if (s.Length > 32) s = s.Substring(0, 32);
        if (s.Length == 0) s = "UNSURE";
        return s;
    }

    // ------------------- OpenAI -------------------

    static async Task<string> ContactOpenAI(List<string> imagePaths)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");

        ChatClient client = new(model: "gpt-4o", apiKey: apiKey);

        var parts = new List<ChatMessageContentPart>
    {
        ChatMessageContentPart.CreateTextPart(
            "You are answering a question from one or more screenshot images.\n\n" +
            "Output MUST be 32 characters or fewer.\n" +
            "Return ONLY the final answer. No explanation.\n\n" +
            "Rules:\n" +
            "- If multiple choice with letters, return only the letter.\n" +
            "- If choices are unlabeled, return only the choice number (1,2,3...).\n" +
            "- If 'select all that apply', return letters or numbers separated by commas.\n" +
            "- If True/False, return only T or F.\n" +
            "- If numeric, return only the number.\n" +
            "- If fill-in, return the shortest correct phrase.\n" +
            "- If the images are unclear, return UNSURE.\n\n" +
            "The screenshots may be split into vertical tiles. Treat them as parts of the same screen."
        )
    };

        foreach (string path in imagePaths)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            BinaryData imageBytes = BinaryData.FromBytes(bytes);
            parts.Add(ChatMessageContentPart.CreateImagePart(imageBytes, "image/png"));
        }

        List<ChatMessage> messages =
        [
            new UserChatMessage(parts.ToArray())
        ];

        ChatCompletion completion = await client.CompleteChatAsync(messages);

        string answer = completion.Content.FirstOrDefault()?.Text ?? "";
        return SanitizeTo32(answer);
    }
}

// ------------------- Windows OCR (Tiles) -------------------



// ------------------- Global Hotkey (suppressed '\') -------------------
// This intercepts '\' globally and prevents it from typing anywhere.

class HotkeyForm : Form
{
    private readonly Action _onHotkey;

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc _proc;

    public HotkeyForm(Action onHotkey)
    {
        _onHotkey = onHotkey;

        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Opacity = 0;
        Load += (_, __) => Hide();

        _proc = HookCallback;
        _hookId = SetHook(_proc);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        base.OnFormClosed(e);
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    // Backslash key (\) / pipe (|) key on US layout
    private const int VK_OEM_5 = 0xDC;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            if (info.vkCode == VK_OEM_5)
            {
                // Don't run heavy work on the hook thread; enqueue onto UI thread
                BeginInvoke(new Action(() => _onHotkey()));

                // Suppress the key so nothing receives '\'
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}