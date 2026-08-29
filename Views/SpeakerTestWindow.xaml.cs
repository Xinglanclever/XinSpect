using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace XinSpect;

/// <summary>喇叭檢測：即時合成左右聲道測試音，用來確認接線、聲道對調與明顯的頻段異常。</summary>
/// <remarks>
/// 零外部相依：PCM 波形在記憶體裡算出來，交給 <see cref="SoundPlayer"/>（winmm）送往系統預設輸出裝置。
/// <para>
/// 本視窗只做「產生訊號」這件事——畫面上亮起的是<b>送出去的聲道</b>，不是量到的音量。
/// 程式沒有麥克風也沒有回路，聽不聽得見只能由使用者判斷，因此不提供任何「通過／失敗」結論。
/// </para>
/// </remarks>
public partial class SpeakerTestWindow : Window
{
    public SpeakerTestWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Focus();     // 全螢幕視窗要自己抓焦點，否則按鍵進不來
    }

    private const int Rate = 44100;      // CD 取樣率：任何輸出裝置都吃得下，不必猜裝置能力
    private const double Amp = 0.32;     // 保守振幅：測試音不該震到耳朵，音量交給系統音量鍵

    private SoundPlayer? _player;
    private MemoryStream? _pcm;
    private DispatcherTimer? _seq;       // 有限長度序列的指示燈節拍（單聲道／雙聲道是循環播放，不需要）

    private static readonly SolidColorBrush ConeIdle = Frozen(0x1C, 0x20, 0x29);
    private static readonly SolidColorBrush ConeLit = Frozen(0x1F, 0x6F, 0xEB);
    private static readonly SolidColorBrush EdgeIdle = Frozen(0x3A, 0x41, 0x50);
    private static readonly SolidColorBrush EdgeLit = Frozen(0x5E, 0x9C, 0xFF);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.D1 or Key.NumPad1: Tone(left: true, right: false, "左聲道　只有左邊應該出聲"); break;
            case Key.D2 or Key.NumPad2: Tone(left: false, right: true, "右聲道　只有右邊應該出聲"); break;
            case Key.D3 or Key.NumPad3: Tone(left: true, right: true, "雙聲道　兩邊音量與音色應相近"); break;
            case Key.D4 or Key.NumPad4: Alternating(); break;
            case Key.D5 or Key.NumPad5: Sweep(); break;
            case Key.S: Stop("已停止"); break;
        }
    }

    private void Window_Closed(object sender, EventArgs e) => Stop(null);

    // ── 測試音 ────────────────────────────────────────────────

    /// <summary>單／雙聲道提示音：1.5 秒一輪（0.75 秒 440 Hz + 0.75 秒靜音）循環，方便邊聽邊繞到喇叭旁確認。</summary>
    private void Tone(bool left, bool right, string caption)
    {
        Start(Wav(1.5, t =>
        {
            double v = Beep(t, 0, 0.75, 440);
            return (left ? v : 0d, right ? v : 0d);
        }), loop: true);

        Light(left, right);
        StatusText.Text = caption + "（循環播放，按 S 停止）";
    }

    /// <summary>左右交替：L→R→L→R 共四聲，用來一次確認聲道有沒有接反。單次播放，約 6 秒。</summary>
    private void Alternating()
    {
        Start(Wav(6.0, t =>
        {
            int slot = (int)(t / 0.75);                       // 0=左 1=靜 2=右 3=靜，四格一輪
            double v = Beep(t, slot * 0.75, 0.75, 440);
            return slot % 4 == 0 ? (v, 0d) : slot % 4 == 2 ? (0d, v) : (0d, 0d);
        }), loop: false);

        Sequence(8, 0.75, i => Light(i % 4 == 0, i % 4 == 2), "左右交替結束");
        StatusText.Text = "左右交替　順序為 左→右→左→右；若與畫面指示相反，就是聲道接反了（單次，約 6 秒）";
    }

    /// <summary>對數掃頻 20 Hz → 20 kHz：聽破音、共振與整段消失的頻段。單次播放，約 8 秒。</summary>
    private void Sweep()
    {
        const double f0 = 20, f1 = 20000, span = 8.0;
        double k = f1 / f0, lnk = Math.Log(k);

        Start(Wav(span, t =>
        {
            // 相位是瞬時頻率的積分：直接把 f(t) 塞進 sin 會得到錯的頻率軌跡
            double phase = 2 * Math.PI * f0 * span / lnk * (Math.Pow(k, t / span) - 1);
            double v = Amp * Math.Min(1, Math.Min(t, span - t) / 0.05) * Math.Sin(phase);
            return (v, v);
        }), loop: false);

        Sequence(1, span, _ => Light(true, true), "掃頻結束");
        StatusText.Text = "掃頻 20 Hz → 20 kHz　由低到高連續掃過；留意破音、雜音與突然安靜的頻段（單次，約 8 秒）";
    }

    /// <summary>單顆提示音，含 25 ms 淡入淡出——直接切斷會產生喀聲，容易被誤判成喇叭破音。</summary>
    private static double Beep(double t, double start, double dur, double freq)
    {
        double x = t - start;
        if (x < 0 || x >= dur) return 0;
        double env = Math.Min(1, Math.Min(x, dur - x) / 0.025);
        return Amp * env * Math.Sin(2 * Math.PI * freq * x);
    }

    // ── 合成與播放 ────────────────────────────────────────────

    /// <summary>就地合成 PCM WAV：44 位元組標頭 + 交錯的 16 位元左右聲道樣本。</summary>
    private static byte[] Wav(double seconds, Func<double, (double L, double R)> gen)
    {
        int frames = (int)(seconds * Rate);
        int data = frames * 4;                               // 2 聲道 × 16 位元 = 每格 4 位元組
        var ms = new MemoryStream(44 + data);
        var w = new BinaryWriter(ms);

        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(data);

        for (int i = 0; i < frames; i++)
        {
            var (l, r) = gen(i / (double)Rate);
            w.Write(Sample(l));
            w.Write(Sample(r));
        }
        w.Flush();
        return ms.ToArray();

        static short Sample(double v) => (short)Math.Clamp(v * short.MaxValue, short.MinValue, short.MaxValue);
    }

    private void Start(byte[] wav, bool loop)
    {
        Stop(null);
        _pcm = new MemoryStream(wav, writable: false);
        _player = new SoundPlayer(_pcm);
        _player.Load();                                      // 資料已在記憶體，同步載入不會卡畫面
        if (loop) _player.PlayLooping(); else _player.Play();
    }

    /// <summary>停止播放並復位指示燈；<paramref name="caption"/> 為 <c>null</c> 表示只是收拾（換曲目或關窗）。</summary>
    private void Stop(string? caption)
    {
        _seq?.Stop();
        _seq = null;
        _player?.Stop();
        _player?.Dispose();
        _player = null;
        _pcm?.Dispose();
        _pcm = null;

        if (caption is null) return;
        Light(false, false);
        StatusText.Text = caption + "　按 1～5 重新選擇測試音";
    }

    /// <summary>
    /// 有限長度序列的指示燈節拍：與音訊同時起跑，數秒內的計時器抖動看不出來；
    /// 循環播放的模式刻意不用它——長時間累積的漂移會讓畫面說出音訊沒在做的事。
    /// </summary>
    private void Sequence(int slots, double slotSeconds, Action<int> onSlot, string doneCaption)
    {
        int i = 0;
        onSlot(0);
        _seq = new DispatcherTimer { Interval = TimeSpan.FromSeconds(slotSeconds) };
        _seq.Tick += (_, _) =>
        {
            if (++i >= slots) Stop(doneCaption);
            else onSlot(i);
        };
        _seq.Start();
    }

    private void Light(bool left, bool right)
    {
        LeftCone.Fill = left ? ConeLit : ConeIdle;
        LeftCone.Stroke = left ? EdgeLit : EdgeIdle;
        LeftBox.BorderBrush = left ? EdgeLit : EdgeIdle;
        RightCone.Fill = right ? ConeLit : ConeIdle;
        RightCone.Stroke = right ? EdgeLit : EdgeIdle;
        RightBox.BorderBrush = right ? EdgeLit : EdgeIdle;
    }
}
