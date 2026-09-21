using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

class AlwaysOnTop : Form
{
    const int HOTKEY_TOGGLE = 9111;
    const int HOTKEY_UNPINALL = 9112;
    const uint MOD_ALT = 0x0001;
    const uint MOD_CONTROL = 0x0002;
    const uint MOD_SHIFT = 0x0004;
    const uint MOD_WIN = 0x0008;
    const uint MOD_NOREPEAT = 0x4000;
    const int WM_HOTKEY = 0x0312;
    const int WM_APP_TOGGLE = 0x8000 + 1;
    const int WM_APP_UNPINALL = 0x8000 + 3;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;

    const int WH_MOUSE_LL = 14;
    const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C;

    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    static readonly Color Accent = Color.FromArgb(0, 120, 212);

    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string lpModuleName);

    static Mutex _gate;
    static bool _openSettingsOnStart;

    NotifyIcon _tray;
    LowLevelMouseProc _mouseProc;
    IntPtr _hook = IntPtr.Zero;
    int _swallowUp = -1;

    HotkeySpec _toggle = HotkeySpec.DefaultToggle();
    HotkeySpec _unpinAll = HotkeySpec.DefaultUnpinAll();
    bool _regToggle, _regUnpinAll;

    readonly HashSet<IntPtr> _pinned = new HashSet<IntPtr>();

    // 捕获快捷键期间，钩子必须完全放行，否则用户按的组合会被自己吞掉
    internal bool Capturing { get; set; }

    [STAThread]
    static void Main()
    {
        bool first;
        _gate = new Mutex(true, "AlwaysOnTop_Bor_7f3a", out first);
        if (!first) return;

        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)) _openSettingsOnStart = true;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new AlwaysOnTop());
    }

    AlwaysOnTop()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;

        string sToggle, sUnpin;
        Config.Load(out sToggle, out sUnpin);
        _toggle = HotkeySpec.Parse(sToggle, true);
        _unpinAll = HotkeySpec.Parse(sUnpin, false);

        _tray = new NotifyIcon();
        _tray.Icon = MakeTrayIcon();
        _tray.Visible = true;

        var menu = new ContextMenuStrip();
        menu.Items.Add("钉住 / 取消当前窗口", null, delegate { Toggle(); });
        menu.Items.Add("取消全部置顶", null, delegate { UnpinAll(true); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置快捷键…", null, delegate { OpenSettings(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { Close(); });
        _tray.ContextMenuStrip = menu;

        IntPtr force = Handle;
    }

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _mouseProc = MouseHook;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);

        bool ok = RegisterAll(_toggle, _unpinAll);
        UpdateTrayText(ok);

        _tray.BalloonTipTitle = "窗口置顶已启动";
        _tray.BalloonTipText = "钉住 / 取消：" + _toggle + "\n取消全部置顶：" + _unpinAll
            + (ok ? "" : "\n有组合已被别的程序占用，请右键托盘图标改一个。")
            + "\n右键托盘图标可以修改快捷键。";
        _tray.ShowBalloonTip(5000);

        if (_openSettingsOnStart)
        {
            _openSettingsOnStart = false;
            BeginInvoke((MethodInvoker)delegate { OpenSettings(); });
        }
    }

    // ---------------------------------------------------------------- 快捷键

    void UpdateTrayText(bool ok)
    {
        string t = "钉住：" + _toggle + "  全部取消：" + _unpinAll;
        if (!ok) t = "有快捷键注册失败 - " + t;
        _tray.Text = t.Length > 63 ? t.Substring(0, 60) + "…" : t;
    }

    // 鼠标组合不需要注册（靠钩子匹配），键盘组合注册失败会返回 false
    bool RegisterAll(HotkeySpec toggle, HotkeySpec unpin)
    {
        if (_regToggle) { UnregisterHotKey(Handle, HOTKEY_TOGGLE); _regToggle = false; }
        if (_regUnpinAll) { UnregisterHotKey(Handle, HOTKEY_UNPINALL); _regUnpinAll = false; }

        bool a = true, b = true;
        if (!toggle.IsMouse)
        {
            a = RegisterHotKey(Handle, HOTKEY_TOGGLE, toggle.Modifiers() | MOD_NOREPEAT, toggle.Vk);
            _regToggle = a;
        }
        if (!unpin.IsMouse)
        {
            b = RegisterHotKey(Handle, HOTKEY_UNPINALL, unpin.Modifiers() | MOD_NOREPEAT, unpin.Vk);
            _regUnpinAll = b;
        }
        return a && b;
    }

    void ApplyHotkeys(HotkeySpec toggle, HotkeySpec unpin)
    {
        if (toggle.SameAs(unpin))
        {
            MessageBox.Show("两个快捷键不能设成同一个组合。", "设置快捷键",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var oldToggle = _toggle;
        var oldUnpin = _unpinAll;
        if (!RegisterAll(toggle, unpin))
        {
            RegisterAll(oldToggle, oldUnpin);   // 新的注册不上，整体退回原样
            MessageBox.Show("有组合已经被别的程序占用了，换一个试试。", "无法注册",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _toggle = toggle;
        _unpinAll = unpin;
        Config.Save(toggle.ToString(), unpin.ToString());
        UpdateTrayText(true);
    }

    void OpenSettings()
    {
        using (var dlg = new SettingsForm(this, _toggle, _unpinAll))
        {
            if (dlg.ShowDialog() == DialogResult.OK) ApplyHotkeys(dlg.ResultToggle, dlg.ResultUnpinAll);
        }
    }

    // ---------------------------------------------------------------- 钩子

    static int ButtonOfDown(int msg, uint mouseData)
    {
        switch (msg)
        {
            case WM_LBUTTONDOWN: return 0;
            case WM_MBUTTONDOWN: return 1;
            case WM_RBUTTONDOWN: return 2;
            case WM_XBUTTONDOWN: return (mouseData >> 16) == 1 ? 3 : 4;
            default: return -1;
        }
    }

    static bool IsButtonDownMsg(int msg)
    {
        return msg == WM_LBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_XBUTTONDOWN;
    }

    static bool IsButtonUpMsg(int msg)
    {
        return msg == WM_LBUTTONUP || msg == WM_MBUTTONUP || msg == WM_RBUTTONUP || msg == WM_XBUTTONUP;
    }

    static int UpMessageOf(int button)
    {
        switch (button)
        {
            case 0: return WM_LBUTTONUP;
            case 1: return WM_MBUTTONUP;
            case 2: return WM_RBUTTONUP;
            default: return WM_XBUTTONUP;
        }
    }

    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !Capturing)
        {
            int msg = wParam.ToInt32();

            if (IsButtonDownMsg(msg))
            {
                var data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                int btn = ButtonOfDown(msg, data.mouseData);
                int action = -1;

                if (_toggle.IsMouse && btn == _toggle.MouseButton && _toggle.ModifiersHeld()) action = WM_APP_TOGGLE;
                else if (_unpinAll.IsMouse && btn == _unpinAll.MouseButton && _unpinAll.ModifiersHeld()) action = WM_APP_UNPINALL;

                if (action >= 0)
                {
                    _swallowUp = UpMessageOf(btn);
                    PostMessage(Handle, action, IntPtr.Zero, IntPtr.Zero);
                    return (IntPtr)1;
                }
            }
            else if (IsButtonUpMsg(msg) && _swallowUp == msg)
            {
                _swallowUp = -1;
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_TOGGLE) { Toggle(); return; }
            if (id == HOTKEY_UNPINALL) { UnpinAll(true); return; }
        }
        if (m.Msg == WM_APP_TOGGLE) { Toggle(); return; }
        if (m.Msg == WM_APP_UNPINALL) { UnpinAll(true); return; }
        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- 置顶

    void Balloon(string text)
    {
        _tray.BalloonTipTitle = "窗口置顶";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(1200);
    }

    void Toggle()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero || h == Handle || h == GetShellWindow()) return;

        PruneDead();

        if (_pinned.Contains(h)) { Unpin(h, true); Balloon("已取消置顶"); }
        else { Pin(h); Balloon("已钉在最前"); }
    }

    void Pin(IntPtr h)
    {
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        _pinned.Add(h);
    }

    void Unpin(IntPtr h, bool restoreTop)
    {
        if (!_pinned.Remove(h)) return;
        if (restoreTop && IsWindow(h))
        {
            SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }
    }

    void UnpinAll(bool notify)
    {
        PruneDead();
        if (_pinned.Count == 0) { if (notify) Balloon("当前没有被钉住的窗口"); return; }
        int n = _pinned.Count;
        var keys = new List<IntPtr>(_pinned);
        foreach (var h in keys) Unpin(h, true);
        if (notify) Balloon("已取消全部置顶（" + n + " 个）");
    }

    // 清掉已关闭窗口的句柄，免得句柄被复用后误判成"这个窗口已经钉住了"
    void PruneDead()
    {
        if (_pinned.Count == 0) return;
        var dead = new List<IntPtr>();
        foreach (var h in _pinned) { if (!IsWindow(h)) dead.Add(h); }
        foreach (var h in dead) _pinned.Remove(h);
    }

    static Icon MakeTrayIcon()
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var pen = new Pen(Accent, 3f))
                using (var brush = new SolidBrush(Accent))
                {
                    g.DrawEllipse(pen, 7, 3, 18, 18);
                    g.FillEllipse(brush, 13, 9, 6, 6);
                    g.DrawLine(pen, 16, 21, 16, 30);
                }
            }
            IntPtr raw = bmp.GetHicon();
            try
            {
                using (var tmp = Icon.FromHandle(raw)) { return (Icon)tmp.Clone(); }
            }
            finally { DestroyIcon(raw); }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        UnpinAll(false);
        if (_regToggle) { try { UnregisterHotKey(Handle, HOTKEY_TOGGLE); } catch { } }
        if (_regUnpinAll) { try { UnregisterHotKey(Handle, HOTKEY_UNPINALL); } catch { } }
        if (_hook != IntPtr.Zero) { try { UnhookWindowsHookEx(_hook); } catch { } _hook = IntPtr.Zero; }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        base.OnFormClosing(e);
    }

    // ================================================================ 设置窗口

    class SettingsForm : Form
    {
        readonly AlwaysOnTop _owner;
        HotkeySpec _main, _unpin;
        int _capturing = -1;   // -1 未捕获；0 主快捷键；1 取消全部

        readonly Button _capMain = new Button();
        readonly Button _capUnpin = new Button();
        readonly Label _hint = new Label();

        public HotkeySpec ResultToggle { get { return _main; } }
        public HotkeySpec ResultUnpinAll { get { return _unpin; } }

        public SettingsForm(AlwaysOnTop owner, HotkeySpec toggle, HotkeySpec unpin)
        {
            _owner = owner;
            _main = toggle;
            _unpin = unpin;

            Text = "设置快捷键";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 336);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            BackColor = SystemColors.Window;
            KeyPreview = true;

            var title = new Label();
            title.Text = "点击下面的框，然后按下你想要的快捷键";
            title.SetBounds(20, 16, 440, 24);
            title.Font = new Font(Font, FontStyle.Bold);
            Controls.Add(title);

            MakeRow("钉住 / 取消当前窗口", 56, _capMain, 0);
            MakeRow("取消全部置顶", 116, _capUnpin, 1);

            _hint.SetBounds(20, 172, 440, 116);
            _hint.ForeColor = SystemColors.GrayText;
            Controls.Add(_hint);

            var reset = new Button();
            reset.Text = "恢复默认";
            reset.SetBounds(20, 294, 100, 28);
            reset.Click += delegate
            {
                _capturing = -1;
                _owner.Capturing = false;
                _main = HotkeySpec.DefaultToggle();
                _unpin = HotkeySpec.DefaultUnpinAll();
                UpdateDisplay();
            };
            Controls.Add(reset);

            var ok = new Button();
            ok.Text = "确定";
            ok.SetBounds(288, 294, 84, 28);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            var cancel = new Button();
            cancel.Text = "取消";
            cancel.SetBounds(382, 294, 84, 28);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            UpdateDisplay();
        }

        void MakeRow(string label, int y, Button cap, int index)
        {
            var lb = new Label();
            lb.Text = label;
            lb.SetBounds(20, y + 8, 176, 24);
            Controls.Add(lb);

            cap.SetBounds(202, y, 264, 40);
            cap.FlatStyle = FlatStyle.System;
            cap.UseVisualStyleBackColor = true;
            cap.Click += delegate { BeginCapture(index); };
            cap.MouseDown += CaptureMouseDown;
            cap.Tag = index;
            Controls.Add(cap);
        }

        void BeginCapture(int index)
        {
            _capturing = index;
            _owner.Capturing = true;
            _capMain.Text = index == 0 ? "请按下组合键…（Esc 取消）" : _main.ToString();
            _capUnpin.Text = index == 1 ? "请按下组合键…（Esc 取消）" : _unpin.ToString();
            _hint.ForeColor = SystemColors.GrayText;
            _hint.Text = "可以设成键盘组合，也可以设成鼠标组合。\n设鼠标组合时，先按住修饰键，再在被捕获的框上点一下鼠标键。";
        }

        void EndCapture()
        {
            _capturing = -1;
            _owner.Capturing = false;
        }

        void UpdateDisplay()
        {
            _capMain.Text = _main.ToString();
            _capUnpin.Text = _unpin.ToString();
            _hint.ForeColor = SystemColors.GrayText;
            _hint.Text = "键盘组合：修饰键 + 任意键，如 Ctrl+Alt+P\n"
                + "鼠标组合：修饰键 + 鼠标键，如 Ctrl+鼠标中键\n"
                + "鼠标侧键可以单独使用；鼠标左/中/右键必须搭配至少一个修饰键。";
        }

        void CaptureMouseDown(object sender, MouseEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null || !btn.Focused) return;
            if (_capturing != (int)btn.Tag) return;

            int index;
            switch (e.Button)
            {
                case MouseButtons.Left: index = 0; break;
                case MouseButtons.Middle: index = 1; break;
                case MouseButtons.Right: index = 2; break;
                case MouseButtons.XButton1: index = 3; break;
                case MouseButtons.XButton2: index = 4; break;
                default: return;
            }
            Accept(HotkeySpec.FromMouse(ModifierKeys, index));
        }

        // 用 ProcessCmdKey 才能抓到 Tab / 方向键这类会被控件吃掉的键
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_capturing >= 0)
            {
                if (keyData == Keys.Escape) { EndCapture(); UpdateDisplay(); return true; }
                if ((keyData & Keys.KeyCode) != Keys.None) { Accept(HotkeySpec.FromKeyboard(keyData)); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Accept(HotkeySpec spec)
        {
            string err = spec.Validate();
            if (err != null) { _hint.ForeColor = Color.Firebrick; _hint.Text = err; return; }

            var other = _capturing == 0 ? _unpin : _main;
            if (spec.SameAs(other))
            {
                _hint.ForeColor = Color.Firebrick;
                _hint.Text = "两个快捷键不能设成同一个组合，换一个。";
                return;
            }

            if (_capturing == 0) _main = spec; else _unpin = spec;
            EndCapture();
            UpdateDisplay();
        }
    }

    // ================================================================ 快捷键规格

    class HotkeySpec
    {
        public bool Ctrl, Alt, Shift, Win;
        public bool IsMouse;
        public int MouseButton;   // 0 左 1 中 2 右 3 侧键1 4 侧键2
        public uint Vk;

        public static HotkeySpec DefaultToggle()
        {
            var s = new HotkeySpec();
            s.Ctrl = true; s.IsMouse = true; s.MouseButton = 1;   // Ctrl + 鼠标中键
            return s;
        }

        public static HotkeySpec DefaultUnpinAll()
        {
            var s = new HotkeySpec();
            s.Ctrl = true; s.Alt = true; s.IsMouse = false; s.Vk = 0x4C;   // Ctrl + Alt + L
            return s;
        }

        public static HotkeySpec FromMouse(Keys mods, int button)
        {
            var s = new HotkeySpec();
            s.Ctrl = (mods & Keys.Control) != 0;
            s.Alt = (mods & Keys.Alt) != 0;
            s.Shift = (mods & Keys.Shift) != 0;
            s.IsMouse = true;
            s.MouseButton = button;
            return s;
        }

        public static HotkeySpec FromKeyboard(Keys keyData)
        {
            var s = new HotkeySpec();
            s.Ctrl = (keyData & Keys.Control) != 0;
            s.Alt = (keyData & Keys.Alt) != 0;
            s.Shift = (keyData & Keys.Shift) != 0;
            s.IsMouse = false;
            s.Vk = (uint)(keyData & Keys.KeyCode);
            return s;
        }

        public uint Modifiers()
        {
            uint m = 0;
            if (Ctrl) m |= MOD_CONTROL;
            if (Alt) m |= MOD_ALT;
            if (Shift) m |= MOD_SHIFT;
            if (Win) m |= MOD_WIN;
            return m;
        }

        public bool ModifiersHeld()
        {
            if (Ctrl && (GetAsyncKeyState(0x11) & 0x8000) == 0) return false;
            if (Alt && (GetAsyncKeyState(0x12) & 0x8000) == 0) return false;
            if (Shift && (GetAsyncKeyState(0x10) & 0x8000) == 0) return false;
            if (Win && (GetAsyncKeyState(0x5B) & 0x8000) == 0 && (GetAsyncKeyState(0x5C) & 0x8000) == 0) return false;
            return true;
        }

        public bool SameAs(HotkeySpec o)
        {
            if (o == null) return false;
            return Ctrl == o.Ctrl && Alt == o.Alt && Shift == o.Shift && Win == o.Win
                && IsMouse == o.IsMouse && MouseButton == o.MouseButton && Vk == o.Vk;
        }

        public string Validate()
        {
            if (IsMouse && !Ctrl && !Alt && !Shift && !Win && MouseButton <= 2)
                return "鼠标左/中/右键必须搭配至少一个修饰键，否则会干扰正常操作。\n（侧键可以单独使用）";
            if (!IsMouse && Vk == 0) return "请按下一个有效的键。";
            return null;
        }

        static readonly string[] MouseNames = { "鼠标左键", "鼠标中键", "鼠标右键", "鼠标侧键1", "鼠标侧键2" };

        public override string ToString()
        {
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Win) parts.Add("Win");
            parts.Add(IsMouse ? MouseNames[MouseButton] : KeyName(Vk));
            return string.Join(" + ", parts.ToArray());
        }

        static string KeyName(uint vk)
        {
            if (vk == 0) return "?";
            try { return ((Keys)vk).ToString(); }
            catch { }
            return "0x" + vk.ToString("X2");
        }

        // 存成可读文本，用户也可以直接手改配置文件
        public static HotkeySpec Parse(string text, bool isToggle)
        {
            var fallback = isToggle ? DefaultToggle() : DefaultUnpinAll();
            if (string.IsNullOrEmpty(text)) return fallback;

            var s = new HotkeySpec();
            var tokens = text.Split('+');
            var last = tokens[tokens.Length - 1].Trim();
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                var t = tokens[i].Trim().ToLowerInvariant();
                if (t == "ctrl" || t == "control") s.Ctrl = true;
                else if (t == "alt") s.Alt = true;
                else if (t == "shift") s.Shift = true;
                else if (t == "win") s.Win = true;
            }

            var low = last.ToLowerInvariant();
            if (low.StartsWith("鼠标") || low.StartsWith("mouse") || low == "m")
            {
                s.IsMouse = true;
                if (low.Contains("中") || low.Contains("middle") || low.Contains("m1")) s.MouseButton = 1;
                else if (low.Contains("右") || low.Contains("right") || low.Contains("m2")) s.MouseButton = 2;
                else if (low.Contains("侧1") || low.Contains("x1")) s.MouseButton = 3;
                else if (low.Contains("侧2") || low.Contains("x2")) s.MouseButton = 4;
                else s.MouseButton = 0;
                return s.Validate() == null ? s : fallback;
            }

            Keys k;
            if (Enum.TryParse<Keys>(last, true, out k))
            {
                s.Vk = (uint)(k & Keys.KeyCode);
                if (s.Vk != 0) return s;
            }
            if (last.Length == 1)
            {
                var up = last.ToUpperInvariant();
                if ((up[0] >= 'A' && up[0] <= 'Z') || (up[0] >= '0' && up[0] <= '9')) { s.Vk = up[0]; return s; }
            }
            return fallback;
        }
    }

    // ================================================================ 配置

    static class Config
    {
        static string Path_
        {
            get
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlwaysOnTop");
                Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "config.txt");
            }
        }

        public static void Load(out string toggle, out string unpinAll)
        {
            toggle = null; unpinAll = null;
            try
            {
                var f = Path_;
                if (!File.Exists(f)) return;
                foreach (var line in File.ReadAllLines(f))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim().ToLowerInvariant();
                    var v = line.Substring(i + 1).Trim();
                    if (k == "hotkey") toggle = v;
                    else if (k == "unpinall") unpinAll = v;
                }
            }
            catch { }
        }

        public static void Save(string toggle, string unpinAll)
        {
            try
            {
                File.WriteAllText(Path_,
                    "hotkey=" + toggle + Environment.NewLine +
                    "unpinall=" + unpinAll + Environment.NewLine);
            }
            catch { }
        }
    }
}
