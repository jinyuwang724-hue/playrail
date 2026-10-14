// 手柄：XInput 在独立线程上 250 Hz 轮询（照 src/input.ts 的映射与手感参数）。
// 十字键 / 左摇杆 = 方向导航（按住按间隔连走），右摇杆 = 平移相机，A 打开，B 返回，LB / RB 换页签，
// X 切换标题样式，Y 游戏设置（占位），视图键开性能读数。只在本窗口在前台时响应。
using System.Numerics;
using System.Runtime.InteropServices;
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
    private const ushort Back = 0x0020, LB = 0x0100, RB = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    private readonly DispatcherQueue _dq;
    private readonly WallStage _stage;
    private readonly Func<bool> _foreground;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly Dictionary<Direction, long> _held = new();
    private ushort _prev;
    private int _index = -1;
    private long _nextScan;
    private Vector2 _smooth;
    private readonly object _gate = new();
    private bool _wasPanning;
    public volatile bool Panning;

    public GamepadPoller(DispatcherQueue dq, WallStage stage, Func<bool> foreground)
    {
        _dq = dq; _stage = stage; _foreground = foreground;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "gamepad", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>UI 线程每帧取一次平滑后的右摇杆速度。</summary>
    public void ApplyPan(WallStage stage)
    {
        Vector2 v;
        lock (_gate) v = _smooth;
        if (Panning || _wasPanning) stage.PanVelocity = v;
        _wasPanning = Panning;
    }

    private void Post(Action a) => _dq.TryEnqueue(() => { try { a(); } catch (Exception ex) { Log.Error("pad action", ex); } });

    private void Loop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long last = 0;
        bool fg = true;
        long nextFg = 0;
        while (!_stop)
        {
            long now = clock.ElapsedMilliseconds;
            double dt = Math.Max(1, now - last);
            last = now;
            try
            {
                if (now >= nextFg) { fg = _foreground(); nextFg = now + 250; }
                XInputState st;
                bool ok = false;
                if (_index >= 0) ok = XInputGetState((uint)_index, &st) == 0;
                else st = default;
                if (!ok)
                {
                    _index = -1;
                    // 没连着的槽查询很慢，所以两秒才扫一次
                    if (now >= _nextScan)
                    {
                        _nextScan = now + 2000;
                        for (uint i = 0; i < 4; i++)
                            if (XInputGetState(i, &st) == 0) { _index = (int)i; ok = true; Log.Info($"gamepad on slot {i}"); break; }
                    }
                }
                if (ok && fg) Handle(st.Gamepad, now, dt);
                else Release(dt);
            }
            catch (Exception ex) { Log.Error("gamepad", ex); Thread.Sleep(500); }
            Thread.Sleep(4);
        }
    }

    private void Release(double dt)
    {
        _held.Clear();
        _prev = 0;
        Smooth(Vector2.Zero, dt);
    }

    private static double Axis(short v) => Math.Clamp(v / 32767.0, -1, 1);

    private void Handle(XInputGamepad g, long now, double dt)
    {
        double lx = Axis(g.ThumbLX), ly = -Axis(g.ThumbLY), rx = Axis(g.ThumbRX), ry = -Axis(g.ThumbRY);
        ushort b = g.Buttons;
        Repeat(Direction.Up, (b & DpadUp) != 0 || ly < -StickDead, now);
        Repeat(Direction.Down, (b & DpadDown) != 0 || ly > StickDead, now);
        Repeat(Direction.Left, (b & DpadLeft) != 0 || lx < -StickDead, now);
        Repeat(Direction.Right, (b & DpadRight) != 0 || lx > StickDead, now);
        bool Edge(ushort m) => (b & m) != 0 && (_prev & m) == 0;
        if (Edge(A)) Post(_stage.Activate);
        if (Edge(B)) Post(_stage.Back);
        if (Edge(LB)) Post(() => _stage.SwitchTab(-1));
        if (Edge(RB)) Post(() => _stage.SwitchTab(1));
        if (Edge(Y)) Post(() => _stage.Toast("游戏设置（原型里还没有）"));
        if (Edge(X)) Post(_stage.ToggleTitleStyle);
        if (Edge(Back)) Post(_stage.ToggleHud);
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

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(200);
    }
}
