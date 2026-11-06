// 手柄：XInput 在独立线程上 250 Hz 轮询（照 src/input.ts 的映射与手感参数）。
// 十字键 / 左摇杆 = 方向导航（按住按间隔连走），右摇杆 = 平移相机，A 打开，B 返回，LB / RB 换页签，
// X 显示 / 隐藏信息栏，Y 游戏设置，≡ 开关控制中心，视图键开性能读数。只在本窗口在前台时响应。
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ClawLibrary.Engine;
using ClawLibrary.Stage;
using Microsoft.UI.Dispatching;

namespace ClawLibrary.Input;

public sealed unsafe class GamepadPoller : IDisposable
{
    private const double RepeatFirstMs = 320;
    private const double RepeatMs = 110;
    private const double StickDead = 0.35;
    private const double PanDead = 0.14;
    /// <summary>右摇杆推满时的平移速度（屏幕 px/s）。</summary>
    private const double PanSpeed = 1700;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint index, XInputState* state);

    private const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
    private const ushort Menu = 0x0010, Back = 0x0020, LB = 0x0100, RB = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    private readonly DispatcherQueue _dq;
    private readonly WallStage _stage;
    private readonly Func<bool> _foreground;
    private readonly Func<uint, Snapshot?>? _readState;
    private int _rescan;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly EventWaitHandle _sleepStop = new(false, EventResetMode.ManualReset);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern SafeWaitHandle OpenEventW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern uint WaitForMultipleObjects(uint count, nint[] handles, bool all, uint milliseconds);
    private readonly Dictionary<Direction, long> _held = new();
    private ushort _prev;
    private int _index = -1;
    private long _nextScan;
    private Vector2 _smooth;
    private readonly object _gate = new();
    private bool _wasPanning;
    public volatile bool Panning;
    /// <summary>有手柄连着（控制中心「手柄」卡的读数）。</summary>
    public bool Connected => _index >= 0;
    /// <summary>左摇杆也当方向键用（默认）。手柄页关掉：那里要看摇杆的实时输入，转摇杆不能把焦点带跑。</summary>
    public volatile bool StickNavigates = true;

    /// <summary>手柄此刻的原始状态（按键位、扳机 0–255、摇杆 ±32767），给手柄页的实时输入用；不在前台或没连着时是空的。</summary>
    public readonly record struct Snapshot(ushort Buttons, byte LT, byte RT, short LX, short LY, short RX, short RY);
    private Snapshot _last;
    public Snapshot Last { get { lock (_gate) return _last; } }

    public GamepadPoller(DispatcherQueue dq, WallStage stage, Func<bool> foreground, Func<uint, Snapshot?>? readState = null)
    {
        _dq = dq; _stage = stage; _foreground = foreground; _readState = readState;
    }

    public void RequestRescan() => Interlocked.Exchange(ref _rescan, 1);

    private XInputGamepad? Read(uint index)
    {
        if (_readState != null)
        {
            var sample = _readState(index);
            return sample is { } s ? new XInputGamepad { Buttons = s.Buttons, LeftTrigger = s.LT, RightTrigger = s.RT,
                ThumbLX = s.LX, ThumbLY = s.LY, ThumbRX = s.RX, ThumbRY = s.RY } : null;
        }
        XInputState state;
        return XInputGetState(index, &state) == 0 ? state.Gamepad : null;
    }

    private static bool Active(XInputGamepad g) => g.Buttons != 0 || g.LeftTrigger > 30 || g.RightTrigger > 30
        || Math.Abs(Axis(g.ThumbLX)) > StickDead || Math.Abs(Axis(g.ThumbLY)) > StickDead
        || Math.Abs(Axis(g.ThumbRX)) > PanDead || Math.Abs(Axis(g.ThumbRY)) > PanDead;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "gamepad", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>UI 线程每帧取一次平滑后的右摇杆速度。</summary>
    public void ApplyPan(WallStage stage)
    {
        if (stage.GamepadInputBlocked) { stage.PanVelocity = Vector2.Zero; return; }
        Vector2 v;
        lock (_gate) v = _smooth;
        if (Panning || _wasPanning) stage.PanVelocity = v;
        _wasPanning = Panning;
    }

    private void Post(Action a) => _dq.TryEnqueue(() => { try { if (!_stop && _foreground() && !_stage.GamepadInputBlocked) a(); } catch (Exception ex) { Log.Error("pad action", ex); } });

    private bool _activityActive;
    private long _activityNext;
    private void ReportActivity(bool active, long now)
    {
        if (active == _activityActive && (!active || now < _activityNext)) return;
        _activityActive = active;
        _activityNext = now + 250;
        _dq.TryEnqueue(() => _stage.GamepadActivity(active));
    }

    private void Loop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long last = 0;
        bool fg = true;
        long nextFg = 0;
        int connected = 0;
        var pads = new XInputGamepad[4];
        SafeWaitHandle? awake = null; long nextSleepCheck = 0;
        try
        {
        while (!_stop)
        {
            long now = clock.ElapsedMilliseconds;
            double dt = Math.Max(1, now - last);
            last = now;
            try
            {
                if (awake == null && now >= nextSleepCheck)
                {
                    nextSleepCheck = now + 2000;
                    var candidate = OpenEventW(0x100000, false, @"Global\ClawEnhancedSleep.Awake-" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
                    if (candidate.IsInvalid) candidate.Dispose(); else awake = candidate;
                }
                if (awake != null && WaitForSingleObject(awake, 0) == 258)
                {
                    Release(dt);
                    WaitForMultipleObjects(2, [awake.DangerousGetHandle(), _sleepStop.SafeWaitHandle.DangerousGetHandle()], false, 1000);
                    continue;
                }
                if (now >= nextFg) { fg = _foreground(); nextFg = now + 16; }
                if (Interlocked.Exchange(ref _rescan, 0) != 0)
                {
                    connected = 0; _index = -1; _nextScan = 0;
                    Release(dt);
                    Log.Info("gamepad: refreshing XInput slots after backend reconnect");
                }
                bool scan = now >= _nextScan;
                if (scan) _nextScan = now + 2000;
                int active = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (!scan && (connected & (1 << i)) == 0) continue;
                    if (Read((uint)i) is { } pad)
                    {
                        connected |= 1 << i; pads[i] = pad;
                        if (Active(pad)) active |= 1 << i;
                    }
                    else connected &= ~(1 << i);
                }
                int selected = GamepadSourceChoice.Select(connected, active, _index);
                if (selected != _index)
                {
                    Release(dt); _index = selected;
                    Log.Info($"gamepad source slot {selected}, connected=0x{connected:X}, active=0x{active:X}");
                }
                if (_index >= 0 && fg) Handle(pads[_index], now, dt);
                else Release(dt);
            }
            catch (Exception ex) { Log.Error("gamepad", ex); Thread.Sleep(500); }
            Thread.Sleep(4);
        }
        }
        finally { awake?.Dispose(); }
    }

    private void Release(double dt)
    {
        ReportActivity(false, 0);
        lock (_gate) _last = default;
        _held.Clear();
        _prev = 0;
        Smooth(Vector2.Zero, dt);
    }

    private static double Axis(short v) => Math.Clamp(v / 32767.0, -1, 1);

    private void Handle(XInputGamepad g, long now, double dt)
    {
        double lx = Axis(g.ThumbLX), ly = -Axis(g.ThumbLY), rx = Axis(g.ThumbRX), ry = -Axis(g.ThumbRY);
        ushort b = g.Buttons;
        ReportActivity(b != 0 || g.LeftTrigger > 30 || g.RightTrigger > 30 ||
            Math.Abs(lx) > StickDead || Math.Abs(ly) > StickDead || Math.Abs(rx) > PanDead || Math.Abs(ry) > PanDead, now);
        lock (_gate) _last = new Snapshot(b, g.LeftTrigger, g.RightTrigger, g.ThumbLX, g.ThumbLY, g.ThumbRX, g.ThumbRY);
        bool stick = StickNavigates;
        Repeat(Direction.Up, (b & DpadUp) != 0 || stick && ly < -StickDead, now);
        Repeat(Direction.Down, (b & DpadDown) != 0 || stick && ly > StickDead, now);
        Repeat(Direction.Left, (b & DpadLeft) != 0 || stick && lx < -StickDead, now);
        Repeat(Direction.Right, (b & DpadRight) != 0 || stick && lx > StickDead, now);
        bool Edge(ushort m) => (b & m) != 0 && (_prev & m) == 0;
        if (Edge(A)) Post(_stage.Activate);
        if (Edge(B)) Post(_stage.Back);
        if (Edge(LB)) Post(() => _stage.SwitchTab(-1));
        if (Edge(RB)) Post(() => _stage.SwitchTab(1));
        if (Edge(Y)) Post(_stage.PadY);
        if (Edge(Menu)) Post(_stage.ToggleControlCenter);
        if (Edge(X)) Post(_stage.PadX);
        if (Edge(Back)) Post(_stage.PadView);
        _prev = b;
        // 右摇杆：死区外按平方曲线给速度，再用与滚轮同一类的指数平滑追上去
        static double Shape(double v) => Math.Abs(v) < PanDead ? 0 : Math.Sign(v) * Math.Pow((Math.Abs(v) - PanDead) / (1 - PanDead), 2);
        Smooth(new Vector2((float)(Shape(rx) * PanSpeed), (float)(Shape(ry) * PanSpeed)), dt);
    }

    private void Smooth(Vector2 want, double dt)
    {
        float f = (float)(1 - Math.Exp(-dt / Motion.PanTimeConstantMs));
        Vector2 s;
        lock (_gate)
        {
            _smooth += (want - _smooth) * f;
            if (_smooth.Length() < 4 && want == Vector2.Zero) _smooth = Vector2.Zero;
            s = _smooth;
        }
        bool panning = s != Vector2.Zero;
        if (panning && !Panning) { Panning = true; Post(_stage.RequestPanLoop); }
        else if (!panning) Panning = false;
    }

    private void Repeat(Direction d, bool active, long now)
    {
        if (!active) { _held.Remove(d); return; }
        if (!_held.TryGetValue(d, out long next)) { Post(() => _stage.Move(d)); _held[d] = now + (long)RepeatFirstMs; }
        else if (now >= next) { Post(() => _stage.Move(d)); _held[d] = now + (long)RepeatMs; }
    }

    /// <summary>测试用：模拟按下一个键，走和真手柄完全相同的投递路径（Post 到 UI 线程）。</summary>
    public void InjectPress(ushort button)
    {
        if (button == A) Post(_stage.Activate);
        else if (button == B) Post(_stage.Back);
        else if (button == Y) Post(_stage.PadY);
        else if (button == Menu) Post(_stage.ToggleControlCenter);
        else if (button == X) Post(_stage.PadX);
    }

    public const ushort PadA = A, PadB = B, PadMenu = Menu, PadXButton = X;

    public void Dispose()
    {
        _stop = true;
        _sleepStop.Set();
        _thread?.Join(200);
    }
}
