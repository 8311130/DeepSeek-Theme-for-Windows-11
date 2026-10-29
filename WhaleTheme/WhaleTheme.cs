// WhaleTheme — 鲸鱼娘 · 未至之境（Windows 11 桌面主题）
// 编译：csc（.NET Framework 4.8.1 自带），见「编译.cmd」
// 素材版权：CC BY-NC-SA 4.0，原作者链见「关于」菜单
//
// 本文件按 C# 5 语法编写（系统自带编译器只支持到 C# 5），
// 因此不使用字符串插值、null 传播运算符、表达式体成员等新语法。
//
// 结构：
//   启动 → 快照 + 改视觉 + 桌面层循环（静音）
//        → 全屏片头窗口播放 boot-intro.mp4（带音轨）
//        → 片头结束/鼠标点击 → 窗口透明度渐隐 → 露出桌面雨夜循环
//   托盘 → 暂停 / 停止还原 / 重播片头 / 应用主题 / 自启 / 音效 / 音量 / 关于 / 退出

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WhaleTheme
{
    // ───────────────────────────── 路径 ─────────────────────────────
    internal static class P
    {
        public static string ExeDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public static string Assets
        {
            get { return Path.Combine(ExeDir, "assets"); }
        }

        public static string Data
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WhaleTheme");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string MpvDir
        {
            get { return Path.Combine(Data, "mpv"); }
        }

        public static string MpvExe
        {
            get { return Path.Combine(MpvDir, "mpv.exe"); }
        }

        public static string Snapshot
        {
            get { return Path.Combine(Data, "snapshot.txt"); }
        }

        public static string Config
        {
            get { return Path.Combine(Data, "config.ini"); }
        }

        public static string LogFile
        {
            get { return Path.Combine(Data, "log.txt"); }
        }

        // 桌面背景循环（2.58 秒眨眼循环，无声）
        public static string Loop
        {
            get { return Path.Combine(Assets, "whale-loop.mp4"); }
        }

        // 开机片头（8.06 秒，自带音轨，结尾已做淡出）
        public static string Intro
        {
            get { return Path.Combine(Assets, "boot-intro.mp4"); }
        }

        public static string FirstFrame
        {
            get { return Path.Combine(Assets, "desktop-firstframe.jpg"); }
        }

        // 片头最后一帧的静态图：mpv 窗口消失时由它无缝顶上，避免黑闪
        public static string IntroLastFrame
        {
            get { return Path.Combine(Assets, "intro-lastframe.jpg"); }
        }

        public static string IconFile
        {
            get { return Path.Combine(Assets, "whale.ico"); }
        }
    }

    // ───────────────────────────── 日志 ─────────────────────────────
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void W(string msg)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(P.LogFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ───────────────────────────── Win32 ─────────────────────────────
    internal static class N
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);

        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
            string lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int SystemParametersInfo(int action, int param, string value, int winIni);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT rc);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        public const int GWL_STYLE = -16;
        public const uint WS_CHILD = 0x40000000;
        public const uint WS_CAPTION = 0x00C00000;
        public const uint WS_THICKFRAME = 0x00040000;
        public const uint WS_POPUP = 0x80000000;
        public const uint SWP_FRAMECHANGED = 0x0020;

        /// <summary>把窗口收进桌面层后，去掉标题栏/边框，设成子窗口。</summary>
        public static void MakeWChild(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            try
            {
                int style = GetWindowLong(h, GWL_STYLE);
                uint s = unchecked((uint)style);
                s &= ~(WS_CAPTION | WS_THICKFRAME);
                s &= ~WS_POPUP;
                s |= WS_CHILD;
                SetWindowLong(h, GWL_STYLE, unchecked((int)s));
            }
            catch (Exception ex)
            {
                Log.W("改窗口样式失败: " + ex.Message);
            }
        }

        public const uint WM_SETTINGCHANGE = 0x001A;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOOWNERZORDER = 0x0200;

        public const int SPI_SETDESKWALLPAPER = 20;
        public const int SPIF_UPDATEINIFILE = 0x01;
        public const int SPIF_SENDCHANGE = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        public static string ClassOf(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            return sb.ToString();
        }

        public static void Broadcast()
        {
            IntPtr r;
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet",
                SMTO_ABORTIFHUNG, 1000, out r);
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "Environment",
                SMTO_ABORTIFHUNG, 1000, out r);
        }

        public static void SetWallpaper(string path)
        {
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
    }

    // ──────────────────────── 桌面层（图标后面那层） ────────────────────────
    internal static class DesktopLayer
    {
        /// <summary>找到可以挂视频的桌面宿主窗口：优先独立 WorkerW，退化到 Progman。</summary>
        public static IntPtr FindHost()
        {
            IntPtr progman = N.FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
            {
                Log.W("找不到 Progman");
                return IntPtr.Zero;
            }

            // 让 Windows 把图标层挪走，露出一块干净的 WorkerW
            IntPtr dummy;
            N.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out dummy);
            N.SendMessageTimeout(progman, 0x052C, new IntPtr(0x0D), IntPtr.Zero, 0, 1000, out dummy);
            N.SendMessageTimeout(progman, 0x052C, new IntPtr(0x0D), new IntPtr(0x01), 0, 1000, out dummy);
            Thread.Sleep(300);   // 给 Windows 一点时间把 WorkerW 生出来

            // ① Win10 经典结构：独立的顶层 WorkerW，SHELLDLL_DefView 在它里面
            IntPtr worker = IntPtr.Zero;
            N.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (N.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    IntPtr sib = N.FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
                    if (sib != IntPtr.Zero)
                    {
                        worker = sib;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (worker != IntPtr.Zero)
            {
                Log.W("桌面宿主 = 0x" + worker.ToString("X") + " (顶层 WorkerW，经典结构)");
                return worker;
            }

            // ② Win11 结构：Progman 下面挂着一个空的 WorkerW，
            //    它的 z 序在 SHELLDLL_DefView（图标层）之下 —— 这才是该放视频的地方。
            IntPtr inner = N.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            if (inner != IntPtr.Zero)
            {
                N.RECT rc;
                N.GetClientRect(inner, out rc);
                Log.W("桌面宿主 = 0x" + inner.ToString("X") + " (Progman 下的空 WorkerW，" +
                      (rc.Right - rc.Left) + "x" + (rc.Bottom - rc.Top) + ")");
                return inner;
            }

            // ③ 实在没有，退回 Progman，靠 z 序插到图标层下面
            Log.W("桌面宿主 = 0x" + progman.ToString("X") + " (Progman，兜底)");
            return progman;
        }

        /// <summary>
        /// 计算"应该排在谁下面"。返回 IntPtr.Zero 表示置顶。
        /// host 是 Progman 时必须排到 SHELLDLL_DefView 下面，否则会盖住图标；
        /// host 是专用 WorkerW 时置顶即可（里面只有我们一个窗口）。
        /// </summary>
        public static IntPtr ZOrderBelow(IntPtr host)
        {
            IntPtr progman = N.FindWindow("Progman", null);
            if (host == progman && progman != IntPtr.Zero)
            {
                IntPtr dv = N.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) return dv;
            }
            return IntPtr.Zero;
        }

        /// <summary>按进程号在宿主里找 mpv 建出来的子窗口。</summary>
        public static IntPtr FindChildByPid(IntPtr parent, int pid)
        {
            IntPtr found = IntPtr.Zero;
            N.EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
            {
                uint p;
                N.GetWindowThreadProcessId(h, out p);
                if ((int)p == pid)
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>把子窗口铺满父窗口，并按 insertAfter 指定 z 序（IntPtr.Zero = 置顶）。</summary>
        public static void FitChild(IntPtr parent, IntPtr child, IntPtr insertAfter)
        {
            if (parent == IntPtr.Zero || child == IntPtr.Zero) return;

            N.RECT rc;
            if (!N.GetClientRect(parent, out rc))
                N.GetWindowRect(parent, out rc);

            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0)
            {
                w = Screen.PrimaryScreen.Bounds.Width;
                h = Screen.PrimaryScreen.Bounds.Height;
            }

            N.SetWindowPos(child, insertAfter, 0, 0, w, h, N.SWP_NOACTIVATE | N.SWP_NOOWNERZORDER);
            Log.W("子窗口 0x" + child.ToString("X") + " 铺到 " + w + "x" + h +
                  "；z 序 = " + (insertAfter == IntPtr.Zero ? "置顶" : "排在 " + Describe(insertAfter) + " 之下"));
        }

        /// <summary>把一个窗口的状态摊成人能看懂的一行，方便查日志。</summary>
        public static string Describe(IntPtr h)
        {
            if (h == IntPtr.Zero) return "(空句柄)";
            N.RECT rc;
            N.GetWindowRect(h, out rc);
            return "0x" + h.ToString("X")
                 + " class=" + N.ClassOf(h)
                 + " rect=" + rc.Left + "," + rc.Top + "," + (rc.Right - rc.Left) + "x" + (rc.Bottom - rc.Top)
                 + " visible=" + N.IsWindowVisible(h)
                 + " parent=" + N.ClassOf(N.GetParent(h));
        }

        /// <summary>
        /// 把 mpv 的画面挂进桌面层。
        /// 先认 --wid 建出来的子窗口；如果 mpv 没听话（自己开了个顶层窗口），
        /// 就把它抓过来 SetParent 收编。
        /// </summary>
        public static IntPtr TryMount(IntPtr host, Process proc, bool verbose)
        {
            if (host == IntPtr.Zero || proc == null) return IntPtr.Zero;

            IntPtr child = FindChildByPid(host, proc.Id);
            if (child != IntPtr.Zero)
            {
                if (verbose) Log.W("命中 --wid 子窗口：" + Describe(child));
                return child;
            }

            IntPtr top = IntPtr.Zero;
            try
            {
                proc.Refresh();
                top = proc.MainWindowHandle;
            }
            catch (Exception ex)
            {
                if (verbose) Log.W("读 MainWindowHandle 失败: " + ex.Message);
            }
            if (top == IntPtr.Zero)
            {
                if (verbose) Log.W("既没有子窗口，也没有顶层窗口");
                return IntPtr.Zero;
            }

            if (verbose) Log.W("未命中子窗口，尝试收编顶层窗口：" + Describe(top));
            try
            {
                N.SetParent(top, host);
                N.MakeWChild(top);
                N.SetWindowPos(top, IntPtr.Zero, 0, 0, 0, 0,
                    N.SWP_NOMOVE | N.SWP_NOSIZE | N.SWP_NOACTIVATE | N.SWP_FRAMECHANGED);
                if (verbose) Log.W("收编后：" + Describe(top));
                return top;
            }
            catch (Exception ex)
            {
                Log.W("收编顶层窗口失败: " + ex.Message);
                return IntPtr.Zero;
            }
        }

    }

    // ───────────────────────── mpv 控制 ─────────────────────────
    internal sealed class Mpv : IDisposable
    {
        private readonly string _pipeName;
        private Process _proc;
        private readonly object _gate = new object();

        public Mpv(string pipeName)
        {
            _pipeName = pipeName;
        }

        public bool Running
        {
            get
            {
                lock (_gate) { return _proc != null && !_proc.HasExited; }
            }
        }

        public int Pid
        {
            get { lock (_gate) { return _proc == null ? 0 : _proc.Id; } }
        }

        public Process Proc
        {
            get { lock (_gate) { return _proc; } }
        }

        /// <summary>桌面背景：无限循环、静音、挂进桌面层窗口。</summary>
        public void StartLoop(IntPtr host, string file)
        {
            Stop();
            string args = string.Format(
                "--wid={0} --no-config --no-audio --no-osc --no-osd-bar " +
                "--no-input-default-bindings --no-input-cursor --no-border " +
                "--no-window-dragging --keep-open=yes --really-quiet " +
                "--hwdec=auto --vo=gpu --panscan=1.0 --loop-file=inf " +
                "--input-ipc-server=\\\\.\\pipe\\{1} \"{2}\"",
                host.ToInt64(), _pipeName, file);
            Launch(args);
        }

        /// <summary>全屏片头：只播一遍、带音轨、挂进片头窗口。</summary>
        public void StartIntro(IntPtr wid, string file, int volume, bool mute)
        {
            Stop();
            string args = string.Format(
                "--wid={0} --no-config --no-osc --no-osd-bar " +
                "--no-input-default-bindings --no-input-cursor --no-border " +
                "--no-window-dragging --keep-open=no --really-quiet " +
                "--hwdec=auto --vo=gpu --panscan=1.0 --audio-file-auto=no " +
                "--volume={1}{2} " +
                "--input-ipc-server=\\\\.\\pipe\\{3} \"{4}\"",
                wid.ToInt64(), volume, (mute ? " --mute=yes" : ""), _pipeName, file);
            Launch(args);
        }

        private void Launch(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(P.MpvExe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = P.MpvDir;

            Log.W("启动 mpv: " + args);
            lock (_gate)
            {
                _proc = Process.Start(psi);
            }
        }

        public void Send(string json)
        {
            try
            {
                using (NamedPipeClientStream pipe =
                    new NamedPipeClientStream(".", _pipeName, PipeDirection.Out))
                {
                    pipe.Connect(600);
                    byte[] buf = Encoding.UTF8.GetBytes(json + "\n");
                    pipe.Write(buf, 0, buf.Length);
                    pipe.Flush();
                }
            }
            catch (Exception ex)
            {
                Log.W("IPC 失败: " + ex.Message);
            }
        }

        public void SetPaused(bool paused)
        {
            Send("{\"command\":[\"set_property\",\"pause\"," + (paused ? "true" : "false") + "]}");
            Log.W(paused ? "已暂停动画" : "已继续动画");
        }

        /// <summary>
        /// 通过 IPC 向 mpv 发一条命令（只写不读）。
        /// 注意：不要再指望用 IPC 判断"播完了"——实测 --wid 嵌入模式下
        /// mpv 的生命周期和 IPC 回复都不稳定，时长一律由 Bootstrap.GetMp4Duration 读文件得出。
        /// </summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_proc == null) return;
                try
                {
                    if (!_proc.HasExited)
                    {
                        _proc.Kill();
                        _proc.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    Log.W("停止 mpv 出错: " + ex.Message);
                }
                finally
                {
                    try { _proc.Dispose(); } catch { }
                    _proc = null;
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }

    // ─────────────────────── 注册表快照 / 还原 ───────────────────────
    internal sealed class SnapshotItem
    {
        public string Key;
        public string Name;
        public string Kind;   // Str / Dword / Bin / Missing
        public string Value;  // Bin 用 base64
    }

    internal static class Snapshot
    {
        // 只记录壁纸相关。用户要求：除背景和开机动画外不碰系统外观，
        // 所以这里不再快照深色模式 / 强调色 / 主题文件。
        private static readonly string[][] Targets = new string[][]
        {
            new string[] { @"Control Panel\Desktop", "Wallpaper" },
            new string[] { @"Control Panel\Desktop", "WallpaperStyle" },
            new string[] { @"Control Panel\Desktop", "TileWallpaper" },
        };

        public static bool Exists()
        {
            return File.Exists(P.Snapshot);
        }

        public static void Capture()
        {
            if (Exists())
            {
                Log.W("快照已存在，不覆盖");
                return;
            }

            List<SnapshotItem> items = new List<SnapshotItem>();
            foreach (string[] t in Targets)
            {
                SnapshotItem it = new SnapshotItem();
                it.Key = t[0];
                it.Name = t[1];
                it.Kind = "Missing";
                it.Value = "";
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(t[0], false))
                    {
                        if (k != null)
                        {
                            object v = k.GetValue(t[1], null);
                            if (v != null)
                            {
                                RegistryValueKind kind = k.GetValueKind(t[1]);
                                if (kind == RegistryValueKind.Binary)
                                {
                                    it.Kind = "Bin";
                                    it.Value = Convert.ToBase64String((byte[])v);
                                }
                                else if (kind == RegistryValueKind.DWord)
                                {
                                    it.Kind = "Dword";
                                    it.Value = ((int)v).ToString();
                                }
                                else
                                {
                                    it.Kind = "Str";
                                    it.Value = Convert.ToString(v);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.W("读取注册表失败 " + t[0] + "\\" + t[1] + ": " + ex.Message);
                }
                items.Add(it);
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# WhaleTheme snapshot v1  (请勿手改，删掉会导致无法还原)");
            foreach (SnapshotItem it in items)
            {
                sb.AppendLine(it.Kind + "\t" + it.Key + "\t" + it.Name + "\t" + it.Value);
            }
            File.WriteAllText(P.Snapshot, sb.ToString(), Encoding.UTF8);
            Log.W("已保存快照：" + P.Snapshot);
        }

        public static void Restore()
        {
            if (!Exists())
            {
                Log.W("没有快照，跳过注册表还原");
                return;
            }

            string[] lines = File.ReadAllLines(P.Snapshot, Encoding.UTF8);
            foreach (string raw in lines)
            {
                if (raw.Length == 0 || raw.StartsWith("#")) continue;
                string[] f = raw.Split('\t');
                if (f.Length < 4) continue;

                string kind = f[0];
                string key = f[1];
                string name = f[2];
                string val = f[3];

                try
                {
                    if (kind == "Missing")
                    {
                        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key, true))
                        {
                            if (k != null) k.DeleteValue(name, false);
                        }
                        continue;
                    }

                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key))
                    {
                        if (kind == "Bin")
                            k.SetValue(name, Convert.FromBase64String(val), RegistryValueKind.Binary);
                        else if (kind == "Dword")
                            k.SetValue(name, int.Parse(val), RegistryValueKind.DWord);
                        else
                            k.SetValue(name, val, RegistryValueKind.String);
                    }
                }
                catch (Exception ex)
                {
                    Log.W("还原失败 " + key + "\\" + name + ": " + ex.Message);
                }
            }

            N.Broadcast();
            Log.W("注册表已按快照还原");
        }

        public static void Discard()
        {
            try { if (File.Exists(P.Snapshot)) File.Delete(P.Snapshot); }
            catch (Exception ex) { Log.W("删除快照失败: " + ex.Message); }
        }
    }

    // ─────────────────────── 视觉应用（只碰壁纸） ───────────────────────
    // 用户要求：除背景和开机动画外，不改系统的任何外观设置。
    // 所以这里不再动深色模式、强调色、任务栏配色、透明效果。
    internal static class Visuals
    {
        public static void Apply()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    if (k != null)
                    {
                        k.SetValue("WallpaperStyle", "10", RegistryValueKind.String); // 填充
                        k.SetValue("TileWallpaper", "0", RegistryValueKind.String);
                    }
                }

                // 壁纸用当前循环视频的首帧：换了自定义背景，这张也跟着换
                string frame = Media.LoopFirstFrame;
                if (File.Exists(frame))
                    N.SetWallpaper(frame);

                Log.W("壁纸已接管（未改动深色模式/强调色等其他外观）");
            }
            catch (Exception ex)
            {
                Log.W("应用壁纸失败: " + ex.Message);
            }
        }
    }

    // ─────────────────────── 自启 ───────────────────────
    // 两条路：
    //   ① 任务计划程序的"登录时触发"任务 —— 实测比 Run 键早得多
    //   ② HKCU\...\Run 键 —— 兜底，任务注册失败时用
    // 只会启用其中一个，避免开机起两份。
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "WhaleTheme";
        private const string TaskName = "WhaleTheme";

        private static string CurrentValue()
        {
            return "\"" + Application.ExecutablePath + "\" --boot";
        }

        // ── ① 计划任务 ──
        /// <summary>注册"登录时触发"的计划任务。成功返回 true。</summary>
        public static bool RegisterLogonTask()
        {
            try
            {
                Type svcType = Type.GetTypeFromProgID("Schedule.Service");
                if (svcType == null)
                {
                    Log.W("拿不到 Schedule.Service，无法用计划任务");
                    return false;
                }

                dynamic svc = Activator.CreateInstance(svcType);
                svc.Connect();
                dynamic root = svc.GetFolder("\\");
                dynamic td = svc.NewTask(0);

                td.RegistrationInfo.Description = "WhaleTheme 鲸鱼娘桌面主题：登录后播放片头";
                td.RegistrationInfo.Author = "WhaleTheme";

                dynamic trig = td.Triggers.Create(9);          // 9 = 登录时触发
                trig.UserId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                trig.Enabled = true;

                dynamic act = td.Actions.Create(0);            // 0 = 启动程序
                act.Path = Application.ExecutablePath;
                act.Arguments = "--boot";
                act.WorkingDirectory = P.ExeDir;

                td.Principal.RunLevel = 0;                     // 普通权限
                td.Principal.LogonType = 3;                    // 交互式令牌

                td.Settings.Enabled = true;
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.MultipleInstances = 0;             // 忽略新实例
                td.Settings.ExecutionTimeLimit = "PT0S";       // 不限时长
                td.Settings.StartWhenAvailable = true;

                // 6 = 创建或更新；3 = 交互式令牌登录类型
                root.RegisterTaskDefinition(TaskName, td, 6, null, null, 3);

                Log.W("已注册登录计划任务：" + Application.ExecutablePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.W("注册计划任务失败: " + ex.Message);
                return false;
            }
        }

        public static bool TaskExists()
        {
            try
            {
                Type svcType = Type.GetTypeFromProgID("Schedule.Service");
                if (svcType == null) return false;
                dynamic svc = Activator.CreateInstance(svcType);
                svc.Connect();
                dynamic root = svc.GetFolder("\\");
                dynamic task = root.GetTask(TaskName);
                return task != null;
            }
            catch { return false; }
        }

        public static void DeleteLogonTask()
        {
            try
            {
                Type svcType = Type.GetTypeFromProgID("Schedule.Service");
                if (svcType == null) return;
                dynamic svc = Activator.CreateInstance(svcType);
                svc.Connect();
                dynamic root = svc.GetFolder("\\");
                root.DeleteTask(TaskName, 0);
                Log.W("已删除登录计划任务");
            }
            catch (Exception ex)
            {
                Log.W("删除计划任务失败（可能本来就没有）: " + ex.Message);
            }
        }

        // ── ② Run 键 ──
        public static string Read()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return null;
                    object v = k.GetValue(Name, null);
                    return v == null ? null : Convert.ToString(v);
                }
            }
            catch { return null; }
        }

        private static bool WriteRunValue(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(Name, CurrentValue(), RegistryValueKind.String);
                    else k.DeleteValue(Name, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.W("写 Run 键失败: " + ex.Message);
                return false;
            }
        }

        // ── 对外 ──
        public static bool IsOn()
        {
            return TaskExists() || Read() != null;
        }

        /// <summary>优先用计划任务；失败才退回 Run 键。返回是否成功。</summary>
        public static bool Set(bool on)
        {
            if (!on)
            {
                DeleteLogonTask();
                WriteRunValue(false);
                bool off = (Read() == null);
                Log.W("开机自启 = 关；Run 键=" + (off ? "已清空" : "仍存在") +
                      "；任务=" + (TaskExists() ? "仍存在" : "已删除"));
                return off;
            }

            bool taskOk = RegisterLogonTask();
            if (taskOk)
            {
                WriteRunValue(false);   // 有任务就不要 Run 键，免得启动两次
                bool ok = TaskExists();
                Log.W("开机自启 = 开（计划任务）；校验 = " + (ok ? "通过" : "失败"));
                return ok;
            }

            bool runOk = WriteRunValue(true);
            bool good = runOk && Read() != null;
            Log.W("开机自启 = 开（退回 Run 键）；校验 = " + (good ? "通过" : "失败"));
            return good;
        }

        /// <summary>
        /// 启动时自检：路径变了就修正；能用计划任务就自动升级，并清掉 Run 键避免双启。
        /// </summary>
        public static void EnsureBestMode()
        {
            bool hasTask = TaskExists();
            string run = Read();

            if (!hasTask && run == null) return;   // 本来就没开，不擅自打开

            if (hasTask)
            {
                // 任务已存在：确保指向当前 exe（exe 改名/移动后能自愈）
                RegisterLogonTask();
                if (run != null) WriteRunValue(false);
                Log.W("自启状态：使用计划任务，路径已对齐");
                return;
            }

            // 只有 Run 键：试试升级成计划任务
            Log.W("自启状态：目前只有 Run 键（" + run + "），尝试升级为计划任务");
            if (RegisterLogonTask())
            {
                WriteRunValue(false);
                Log.W("已从 Run 键升级为计划任务");
            }
            else if (run != CurrentValue())
            {
                WriteRunValue(true);
                Log.W("计划任务不可用，Run 键路径已修正");
            }
        }
    }

    // ─────────────────────── 配置 ───────────────────────
    internal sealed class Config
    {
        public bool IntroSound = true;   // 片头是否出声
        public int Volume = 62;

        // 自定义素材开关：为 true 且文件确实存在时，才用用户自己传的那套；
        // 文件被删掉会自动回退到 assets 里的默认素材。
        public bool UseCustomBoot = false;
        public bool UseCustomLoop = false;

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (File.Exists(P.Config))
                {
                    foreach (string line in File.ReadAllLines(P.Config, Encoding.UTF8))
                    {
                        string[] kv = line.Split('=');
                        if (kv.Length != 2) continue;
                        string k = kv[0].Trim();
                        string v = kv[1].Trim();
                        if (k == "IntroSound" || k == "BootSound") c.IntroSound = (v == "1");
                        else if (k == "UseCustomBoot") c.UseCustomBoot = (v == "1");
                        else if (k == "UseCustomLoop") c.UseCustomLoop = (v == "1");
                        else if (k == "Volume")
                        {
                            int n;
                            if (int.TryParse(v, out n)) c.Volume = Math.Max(0, Math.Min(100, n));
                        }
                    }
                }
            }
            catch (Exception ex) { Log.W("读配置失败: " + ex.Message); }
            return c;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("IntroSound=" + (IntroSound ? "1" : "0"));
                sb.AppendLine("Volume=" + Volume);
                sb.AppendLine("UseCustomBoot=" + (UseCustomBoot ? "1" : "0"));
                sb.AppendLine("UseCustomLoop=" + (UseCustomLoop ? "1" : "0"));
                File.WriteAllText(P.Config, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.W("写配置失败: " + ex.Message); }
        }
    }

    // ─────────────────── 自定义动画素材（托盘 → 修改动画） ───────────────────
    // 设计原则：assets 里的默认素材一律不碰、不覆盖。
    // 用户上传的视频统统复制到 %LOCALAPPDATA%\WhaleTheme\custom\ 下，
    // 用 config.ini 的两个开关决定"用默认还是用自定义"，
    // 开关开着但文件被删掉时会自动回退到默认，不会变成黑屏。
    internal static class Media
    {
        public const string BootBase = "boot";              // 自定义开机动画的固定基名
        public const string LoopBase = "background";        // 自定义背景动画的固定基名

        // 由托盘程序启动时注入，静态方法要查开关只能靠它
        public static Config Cfg;

        private static readonly string[] VideoExts = new string[]
        {
            ".mp4", ".mov", ".m4v", ".mkv", ".webm", ".avi",
            ".wmv", ".flv", ".ts", ".mpg", ".mpeg", ".3gp"
        };

        public static bool IsVideoExt(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            return Array.IndexOf(VideoExts, ext.ToLowerInvariant()) >= 0;
        }

        public static string CustomDir
        {
            get
            {
                string d = Path.Combine(P.Data, "custom");
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string CustomBootFile { get { return Find(BootBase); } }
        public static string CustomLoopFile { get { return Find(LoopBase); } }

        public static bool BootIsCustom
        {
            get { return Cfg != null && Cfg.UseCustomBoot && Find(BootBase) != null; }
        }

        public static bool LoopIsCustom
        {
            get { return Cfg != null && Cfg.UseCustomLoop && Find(LoopBase) != null; }
        }

        /// <summary>实际要播的片头文件：开着自定义且文件在就用它，否则用默认。</summary>
        public static string BootFile
        {
            get
            {
                string c = Find(BootBase);
                return (Cfg != null && Cfg.UseCustomBoot && c != null) ? c : P.Intro;
            }
        }

        /// <summary>实际要循环播放的桌面背景文件。</summary>
        public static string LoopFile
        {
            get
            {
                string c = Find(LoopBase);
                return (Cfg != null && Cfg.UseCustomLoop && c != null) ? c : P.Loop;
            }
        }

        /// <summary>片头渐隐用的最后一帧：自定义片头单独抽一张，别覆盖默认那张。</summary>
        public static string BootLastFrame
        {
            get
            {
                string c = Find(BootBase);
                if (Cfg != null && Cfg.UseCustomBoot && c != null)
                    return Path.Combine(CustomDir, "boot-lastframe.jpg");
                return P.IntroLastFrame;
            }
        }

        /// <summary>循环视频的首帧（当壁纸用，盖住旧桌面）。</summary>
        public static string LoopFirstFrame
        {
            get
            {
                string c = Find(LoopBase);
                if (Cfg != null && Cfg.UseCustomLoop && c != null)
                    return Path.Combine(CustomDir, "background-firstframe.jpg");
                return P.FirstFrame;
            }
        }

        /// <summary>按基名在自定义目录里找视频，找不到返回 null。</summary>
        private static string Find(string baseName)
        {
            try
            {
                string dir = CustomDir;
                string[] files = Directory.GetFiles(dir);
                foreach (string f in files)
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (!string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsVideoExt(Path.GetExtension(f))) continue;
                    return f;
                }
            }
            catch (Exception ex)
            {
                Log.W("扫描自定义素材失败: " + ex.Message);
            }
            return null;
        }

        // ── 安装 / 清除 ──

        public static string InstallBoot(string src)
        {
            string err = Install(src, BootBase);
            if (err != null) return err;

            if (Cfg != null) { Cfg.UseCustomBoot = true; Cfg.Save(); }

            // 片头结尾要靠"最后一帧"顶住，否则渐隐那一瞬间会闪黑。
            string file = Find(BootBase);
            string dest = Path.Combine(CustomDir, "boot-lastframe.jpg");
            double dur = Durations.Read(file);
            double at = dur > 0.5 ? Math.Max(0, dur - 0.12) : 0;
            if (!Bootstrap.ExtractFrame(file, dest, at))
            {
                // 末尾抽不到就退而求其次抽第一帧，总比纯黑好
                if (!Bootstrap.ExtractFrame(file, dest, 0))
                    Log.W("自定义片头的收尾帧没抽出来，渐隐时会短暂变黑（不影响播放）");
            }
            Log.W("开机动画已切换为自定义：" + file);
            return null;
        }

        public static string InstallLoop(string src)
        {
            string err = Install(src, LoopBase);
            if (err != null) return err;

            if (Cfg != null) { Cfg.UseCustomLoop = true; Cfg.Save(); }
            // 首帧壁纸跟视频文件时间戳比对，EnsureFirstFrame 会自动重抽
            Log.W("背景动画已切换为自定义：" + Find(LoopBase));
            return null;
        }

        private static string Install(string src, string baseName)
        {
            try
            {
                if (string.IsNullOrEmpty(src) || !File.Exists(src))
                    return "找不到这个文件，可能已经被移动或删除了。";

                string ext = Path.GetExtension(src);
                if (!IsVideoExt(ext))
                    return "这个格式（" + (string.IsNullOrEmpty(ext) ? "无扩展名" : ext) + "）播放器不一定认。\r\n" +
                           "建议先用格式工厂 / 剪映 / ffmpeg 转成 MP4（H.264）再试。";

                double dur = Durations.Read(src);
                if (dur > 0 && dur < 0.4)
                    return "这个视频只有 " + dur.ToString("0.00") + " 秒，太短了，换一个吧。";

                string dest = Path.Combine(CustomDir, baseName + ext.ToLowerInvariant());
                string tmp = dest + ".incoming";

                // 用户选的恰好就是已经在用的那个文件：直接算成功，别再复制
                if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest),
                        StringComparison.OrdinalIgnoreCase))
                    return null;

                // 先复制到临时名，成功之后再替换，避免复制失败把旧动画弄丢
                File.Copy(src, tmp, true);

                if (File.Exists(dest) && !DeleteWithRetry(dest))
                {
                    try { File.Delete(tmp); } catch { }
                    return "目标文件被占用，写不进去。\r\n请稍等两秒再试一次。";
                }
                File.Move(tmp, dest);

                // 换了扩展名就把旧的那个删掉，免得 Find 抓到两个
                string old = Find(baseName);
                if (old != null && !string.Equals(old, dest, StringComparison.OrdinalIgnoreCase))
                    DeleteWithRetry(old);

                // 缓存图必须一起作废：File.Copy 会保留源文件的时间戳，
                // 靠"谁比谁新"判断会误判成不用重抽。
                if (baseName == LoopBase)
                    DeleteWithRetry(Path.Combine(CustomDir, "background-firstframe.jpg"));

                return null;
            }
            catch (Exception ex)
            {
                Log.W("导入自定义动画失败: " + ex.Message);
                return "复制文件失败：" + ex.Message;
            }
        }

        public static void ClearBoot()
        {
            if (Cfg != null) { Cfg.UseCustomBoot = false; Cfg.Save(); }
            DeleteWithRetry(Path.Combine(CustomDir, "boot-lastframe.jpg"));
            string f = Find(BootBase);
            if (f != null) DeleteWithRetry(f);
            Log.W("开机动画已切回默认");
        }

        public static void ClearLoop()
        {
            if (Cfg != null) { Cfg.UseCustomLoop = false; Cfg.Save(); }
            DeleteWithRetry(Path.Combine(CustomDir, "background-firstframe.jpg"));
            string f = Find(LoopBase);
            if (f != null) DeleteWithRetry(f);
            Log.W("背景动画已切回默认");
        }

        /// <summary>mpv 被杀掉之后文件句柄不一定会立刻释放，所以多试几次。</summary>
        private static bool DeleteWithRetry(string path)
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (!File.Exists(path)) return true;
                    File.Delete(path);
                    return true;
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
            return !File.Exists(path);
        }
    }

    // ─────────────────────── 视频时长探测 ───────────────────────
    // mp4/mov 读 mvhd（准、快）；mkv/webm 读 EBML 的 Duration；
    // 其余格式让 mpv 自己报一次（慢一点，但能用）。
    internal static class Durations
    {
        public static double Read(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;

            double d = Bootstrap.GetMp4Duration(path);
            if (d > 0) return d;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".mkv" || ext == ".webm")
            {
                d = Ebml(path);
                if (d > 0) return d;
            }

            d = MpvProbe(path);
            if (d > 0) return d;

            Log.W("时长探测失败（按未知处理）: " + path);
            return 0;
        }

        // ── Matroska / WebM ──
        private static double Ebml(string path)
        {
            try
            {
                byte[] b = new byte[4 * 1024 * 1024];
                int n;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    n = fs.Read(b, 0, b.Length);
                if (n < 64) return 0;

                // TimecodeScale：默认 1 000 000 ns
                long scale = 1000000;
                int si = IndexOf(b, n, new byte[] { 0x2A, 0xD7, 0xB1 });
                if (si >= 0)
                {
                    int p = si + 3;
                    long sz = ReadVint(b, n, ref p);
                    if (sz > 0 && sz <= 8 && p + sz <= n)
                        scale = ReadUInt(b, p, (int)sz);
                }

                // Duration：浮点数，单位是 TimecodeScale
                int di = IndexOf(b, n, new byte[] { 0x44, 0x89 });
                if (di < 0) return 0;
                int q = di + 2;
                long dsz = ReadVint(b, n, ref q);
                double val;
                if (dsz == 4) val = ReadFloat(b, q, 4);
                else if (dsz == 8) val = ReadFloat(b, q, 8);
                else return 0;

                double sec = val * scale / 1000000000.0;
                if (sec <= 0.1 || sec > 3600) return 0;
                return sec;
            }
            catch (Exception ex)
            {
                Log.W("解析 EBML 时长失败: " + ex.Message);
                return 0;
            }
        }

        private static int IndexOf(byte[] b, int n, byte[] pat)
        {
            for (int i = 0; i + pat.Length <= n; i++)
            {
                bool hit = true;
                for (int j = 0; j < pat.Length; j++)
                {
                    if (b[i + j] != pat[j]) { hit = false; break; }
                }
                if (hit) return i;
            }
            return -1;
        }

        private static long ReadVint(byte[] b, int n, ref int pos)
        {
            if (pos >= n) return -1;
            byte first = b[pos];
            int len = 1;
            byte mask = 0x80;
            while (len <= 8 && (first & mask) == 0) { mask >>= 1; len++; }
            if (len > 8 || pos + len > n) return -1;

            long v = first & (byte)(mask - 1);
            for (int i = 1; i < len; i++) v = (v << 8) | b[pos + i];
            pos += len;
            return v;
        }

        private static long ReadUInt(byte[] b, int pos, int len)
        {
            long v = 0;
            for (int i = 0; i < len; i++) v = (v << 8) | b[pos + i];
            return v;
        }

        private static double ReadFloat(byte[] b, int pos, int len)
        {
            if (pos + len > b.Length) return 0;
            if (len == 4) return BitConverter.ToSingle(ReverseIfNeeded(b, pos, 4), 0);
            return BitConverter.ToDouble(ReverseIfNeeded(b, pos, 8), 0);
        }

        private static byte[] ReverseIfNeeded(byte[] b, int pos, int len)
        {
            byte[] tmp = new byte[len];
            Array.Copy(b, pos, tmp, 0, len);
            if (BitConverter.IsLittleEndian) Array.Reverse(tmp);
            return tmp;
        }

        // ── 让 mpv 自己报一次时长（覆盖 avi / wmv / flv / ts 等） ──
        private static double MpvProbe(string path)
        {
            if (!File.Exists(P.MpvExe)) return 0;
            try
            {
                string args = "--no-config --no-audio --vo=null --ao=null --frames=1 --idle=no " +
                              "--term-playing-msg=DUR=${duration} \"" + path + "\"";
                ProcessStartInfo psi = new ProcessStartInfo(P.MpvExe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.WorkingDirectory = P.MpvDir;

                using (Process pr = Process.Start(psi))
                {
                    string outp = pr.StandardOutput.ReadToEnd();
                    if (!pr.WaitForExit(10000))
                    {
                        try { pr.Kill(); } catch { }
                        return 0;
                    }
                    int k = outp.IndexOf("DUR=");
                    if (k < 0) return 0;
                    string rest = outp.Substring(k + 4);
                    int cut = rest.IndexOfAny(new char[] { '\r', '\n', ' ' });
                    if (cut >= 0) rest = rest.Substring(0, cut);
                    double d;
                    if (double.TryParse(rest.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out d)
                        && d > 0.1 && d < 3600)
                        return d;
                }
            }
            catch (Exception ex)
            {
                Log.W("mpv 探测时长失败: " + ex.Message);
            }
            return 0;
        }
    }

    // ─────────────────────── 首次运行解压内核 ───────────────────────
    internal static class Bootstrap
    {
        public static bool NeedExtract()
        {
            return !File.Exists(P.MpvExe) || new FileInfo(P.MpvExe).Length < 1024 * 1024;
        }

        public static void Extract()
        {
            Directory.CreateDirectory(P.MpvDir);
            Log.W("首次运行：解压播放内核…");

            using (Stream res = typeof(Bootstrap).Assembly.GetManifestResourceStream("mpv.exe.gz"))
            {
                if (res == null) throw new Exception("可执行文件里没有找到内嵌的播放内核 mpv.exe.gz");
                string tmp = P.MpvExe + ".part";
                using (Stream gz = new System.IO.Compression.GZipStream(
                           res, System.IO.Compression.CompressionMode.Decompress))
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    byte[] buf = new byte[262144];
                    int n;
                    while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                        fs.Write(buf, 0, n);
                }
                if (File.Exists(P.MpvExe)) File.Delete(P.MpvExe);
                File.Move(tmp, P.MpvExe);
            }
            Log.W("播放内核就绪：" + new FileInfo(P.MpvExe).Length + " 字节");
        }

        /// <summary>
        /// 直接读 MP4 文件头里的时长（mvhd 盒）。
        /// 为什么不用 mpv 的 eof-reached：--wid 嵌入模式下它的生命周期不可靠，
        /// 实测有时播完不退出、IPC 也不一定回得上。自己读文件最确定。
        /// </summary>
        public static double GetMp4Duration(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] buf = new byte[2 * 1024 * 1024];
                    int n = fs.Read(buf, 0, buf.Length);
                    if (n < 32) return 0;

                    int idx = -1;
                    for (int i = 0; i + 3 < n; i++)
                    {
                        if (buf[i] == 0x6D && buf[i + 1] == 0x76 && buf[i + 2] == 0x68 && buf[i + 3] == 0x64)
                        {
                            idx = i;
                            break;
                        }
                    }
                    if (idx < 0) return 0;

                    int ver = buf[idx + 4];
                    uint timescale;
                    ulong duration;

                    if (ver == 0)
                    {
                        timescale = ReadU32(buf, idx + 16);
                        duration = ReadU32(buf, idx + 20);
                    }
                    else
                    {
                        timescale = ReadU32(buf, idx + 24);
                        duration = ReadU64(buf, idx + 28);
                    }

                    if (timescale == 0) return 0;
                    double sec = (double)duration / timescale;
                    if (sec <= 0 || sec > 3600) return 0;
                    return sec;
                }
            }
            catch (Exception ex)
            {
                Log.W("读取视频时长失败: " + ex.Message);
                return 0;
            }
        }

        private static uint ReadU32(byte[] b, int i)
        {
            return ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];
        }

        private static ulong ReadU64(byte[] b, int i)
        {
            ulong hi = ReadU32(b, i);
            ulong lo = ReadU32(b, i + 4);
            return (hi << 32) | lo;
        }

        /// <summary>
        /// 用自带的 mpv 从循环视频里抽第一帧，作为"接管壁纸"。
        /// 默认素材和用户自定义素材各抽各的，互不覆盖。
        /// </summary>
        public static void EnsureFirstFrame()
        {
            try
            {
                string loop = Media.LoopFile;
                string frame = Media.LoopFirstFrame;
                if (!File.Exists(loop)) return;

                bool need = !File.Exists(frame);
                if (!need)
                    need = File.GetLastWriteTime(loop) > File.GetLastWriteTime(frame);
                if (!need) return;

                Log.W("循环视频（" + Path.GetFileName(loop) + "）比首帧新，重新抽取首帧…");
                if (ExtractFrame(loop, frame, 0))
                    Log.W("首帧壁纸已更新：" + new FileInfo(frame).Length + " 字节");
                else
                    Log.W("抽取首帧失败，壁纸会停在旧图（视频照常播放）");
            }
            catch (Exception ex)
            {
                Log.W("生成首帧失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 从视频里抽一帧存成 jpg。at 是秒数，0 表示第一帧。
        /// 成功返回 true。抽出来的图先落在临时目录，成功后才替换目标文件。
        /// </summary>
        public static bool ExtractFrame(string video, string dest, double at)
        {
            if (string.IsNullOrEmpty(video) || !File.Exists(video)) return false;
            if (!File.Exists(P.MpvExe)) return false;

            string dir = Path.Combine(P.Data, "frame");
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
            try { Directory.CreateDirectory(dir); }
            catch { return false; }

            try
            {
                string seek = "";
                if (at > 0.05)
                    seek = " --start=" + at.ToString("0.000",
                        System.Globalization.CultureInfo.InvariantCulture) + " --hr-seek=yes";

                string args = string.Format(
                    "--no-config --no-audio --no-osc --no-osd-bar --really-quiet " +
                    "--frames=1 --vo=image --vo-image-format=jpg --vo-image-jpeg-quality=92 " +
                    "--vo-image-outdir=\"{0}\"{1} \"{2}\"", dir, seek, video);

                ProcessStartInfo psi = new ProcessStartInfo(P.MpvExe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = P.MpvDir;

                using (Process pr = Process.Start(psi))
                {
                    if (!pr.WaitForExit(15000))
                    {
                        try { pr.Kill(); } catch { }
                        Log.W("抽帧超时（" + at + " 秒处）");
                        return false;
                    }
                }

                string[] outs = Directory.GetFiles(dir, "*.jpg");
                if (outs.Length == 0)
                {
                    Log.W("抽帧失败：mpv 没有输出图片（" + at + " 秒处）");
                    return false;
                }

                try { if (File.Exists(dest)) File.Delete(dest); }
                catch { }
                File.Move(outs[0], dest);
                return true;
            }
            catch (Exception ex)
            {
                Log.W("抽帧失败: " + ex.Message);
                return false;
            }
        }
    }

    // ─────────────────────── 首次运行提示窗 ───────────────────────
    internal sealed class SplashForm : Form
    {
        public SplashForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(360, 110);
            BackColor = Color.FromArgb(10, 14, 30);
            ShowInTaskbar = false;
            TopMost = true;

            Label label = new Label();
            label.Text = "首次运行，正在准备播放内核…\r\n（约需十几秒，只需一次）";
            label.ForeColor = Color.FromArgb(200, 214, 245);
            label.Font = new Font("Microsoft YaHei UI", 9.5f);
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.Dock = DockStyle.Fill;
            Controls.Add(label);
        }
    }

    // ══════════════════════ 全屏片头窗口 ══════════════════════
    internal sealed class IntroForm : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private readonly Mpv _mpv;
        private readonly bool _mute;
        private readonly int _volume;
        private readonly string _file;

        private System.Windows.Forms.Timer _pollTimer;
        private System.Windows.Forms.Timer _topTimer;
        private System.Windows.Forms.Timer _fadeTimer;

        private DateTime _startedAt;
        private DateTime _videoStartedAt = DateTime.MinValue;
        private double _dur;
        private Bitmap _lastFrame;
        private bool _skipped;
        private bool _fading;
        private bool _cursorHidden;

        public event EventHandler Finished;

        public IntroForm(Mpv mpv, string file, int volume, bool mute)
        {
            _mpv = mpv;
            _file = file;
            _volume = volume;
            _mute = mute;

            // 自己从文件头读时长，不指望 mpv 告诉我们
            _dur = Durations.Read(file);
            Log.W("片头时长读出：" + (_dur > 0 ? _dur.ToString("0.00") + "s" : "未知（将退回超时判定）"));

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;
            BackColor = Color.Black;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = false;
            DoubleBuffered = true;

            // 预置"最后一帧"当底图：mpv 的子窗口一消失，立刻由它顶上，
            // 不会闪一下黑，随后整体渐隐露出桌面。
            // 注意：这里不用 BackgroundImage —— 子窗口销毁后父窗口不一定重绘，
            // 靠它会出现一段黑屏。改成自己在 OnPaint 里画，配合强制重绘最稳。
            try
            {
                string lf = Media.BootLastFrame;
                if (File.Exists(lf))
                {
                    byte[] bytes = File.ReadAllBytes(lf);
                    using (MemoryStream ms = new MemoryStream(bytes))
                    using (Image raw = Image.FromStream(ms))
                    {
                        _lastFrame = new Bitmap(raw);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.W("片头底图加载失败: " + ex.Message);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_lastFrame != null)
            {
                e.Graphics.InterpolationMode =
                    System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                e.Graphics.DrawImage(_lastFrame, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            try
            {
                Cursor.Hide();
                _cursorHidden = true;
            }
            catch { }

            // 鼠标点一下就跳过
            MouseDown += delegate { Skip(); };

            ReassertTopmost();

            try
            {
                _mpv.StartIntro(Handle, _file, _volume, _mute);
            }
            catch (Exception ex)
            {
                Log.W("启动片头失败: " + ex.Message);
                Close();
                return;
            }

            _startedAt = DateTime.Now;

            // 把 mpv 子窗口铺满这个 Form
            System.Windows.Forms.Timer fit = new System.Windows.Forms.Timer();
            fit.Interval = 300;
            int fitTries = 0;
            fit.Tick += delegate
            {
                fitTries++;
                bool ok = false;
                try
                {
                    Process[] all = Process.GetProcessesByName("mpv");
                    foreach (Process p in all)
                    {
                        IntPtr child = DesktopLayer.FindChildByPid(Handle, p.Id);
                        if (child != IntPtr.Zero)
                        {
                            DesktopLayer.FitChild(Handle, child, IntPtr.Zero);
                            if (_videoStartedAt == DateTime.MinValue)
                            {
                                _videoStartedAt = DateTime.Now;
                                Log.W("片头画面已就位，开始计时");
                            }
                            ok = true;
                            break;
                        }
                    }
                }
                catch { }
                if (ok || fitTries >= 15)
                {
                    fit.Stop();
                    fit.Dispose();
                    if (!ok) Log.W("片头：没找到 mpv 子窗口");
                }
            };
            fit.Start();

            // 压制任务栏（它也是置顶窗口）
            _topTimer = new System.Windows.Forms.Timer();
            _topTimer.Interval = 500;
            _topTimer.Tick += delegate { ReassertTopmost(); };
            _topTimer.Start();

            // 等片头播完
            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = 80;
            _pollTimer.Tick += delegate { CheckDone(); };
            _pollTimer.Start();
        }

        private void ReassertTopmost()
        {
            try
            {
                N.SetWindowPos(Handle, N.HWND_TOPMOST, 0, 0, 0, 0,
                    N.SWP_NOMOVE | N.SWP_NOSIZE | N.SWP_NOACTIVATE);
            }
            catch { }
        }

        private void CheckDone()
        {
            if (_fading) return;

            // 主判据：按视频自己的时长走，提前 0.3 秒开始渐隐。
            // 这样视频还在播的时候就切到"最后一帧底图"，绝不会出现黑场。
            bool byTime = false;
            if (_dur > 1.0)
            {
                DateTime t0 = (_videoStartedAt == DateTime.MinValue)
                    ? _startedAt.AddMilliseconds(600)
                    : _videoStartedAt;
                byTime = (DateTime.Now - t0).TotalSeconds >= (_dur - 0.3);
            }

            // 辅助判据：mpv 自己退了（有些情况下它确实会退）
            bool byExit = !_mpv.Running;

            // 兜底：怎么都判不出来时别无限等
            double guard = (_dur > 1.0) ? _dur + 5.0 : 14.0;
            bool byGuard = (DateTime.Now - _startedAt).TotalSeconds > guard;

            if (byTime || byExit || byGuard)
            {
                string why = byTime ? "按时间轴" : (byExit ? "mpv 已退出" : "超时兜底");
                Log.W("片头结束（" + why + "，已播 " +
                      (DateTime.Now - _startedAt).TotalSeconds.ToString("0.0") + "s）");
                BeginFade();
            }
        }

        public void Skip()
        {
            if (_fading || _skipped) return;
            _skipped = true;
            Log.W("片头被鼠标点击跳过");
            BeginFade();
        }

        private void BeginFade()
        {
            if (_fading) return;
            _fading = true;

            if (_pollTimer != null) { _pollTimer.Stop(); _pollTimer.Dispose(); _pollTimer = null; }
            if (_topTimer != null) { _topTimer.Stop(); _topTimer.Dispose(); _topTimer = null; }

            try { _mpv.Stop(); } catch { }

            // 关键：mpv 子窗口被销毁后，父窗口不一定会收到重绘。
            // 不主动刷一下就会露出 BackColor 的纯黑，看起来就是"一段黑屏"。
            try
            {
                if (_lastFrame != null)
                {
                    Invalidate(true);
                    Update();      // 同步重绘，不等消息循环
                }
            }
            catch (Exception ex)
            {
                Log.W("片头底图重绘失败: " + ex.Message);
            }

            // 跳过时想更快收场
            int stepMs = _skipped ? 15 : 25;
            double step = _skipped ? 0.09 : 0.075;

            _fadeTimer = new System.Windows.Forms.Timer();
            _fadeTimer.Interval = stepMs;
            _fadeTimer.Tick += delegate
            {
                try
                {
                    double next = Opacity - step;
                    if (next <= 0.01)
                    {
                        _fadeTimer.Stop();
                        _fadeTimer.Dispose();
                        _fadeTimer = null;
                        Finish();
                        return;
                    }
                    Opacity = next;
                }
                catch
                {
                    _fadeTimer.Stop();
                    _fadeTimer.Dispose();
                    _fadeTimer = null;
                    Finish();
                }
            };
            _fadeTimer.Start();
        }

        private void Finish()
        {
            try
            {
                if (_cursorHidden)
                {
                    Cursor.Show();
                    _cursorHidden = false;
                }
            }
            catch { }

            try
            {
                if (Finished != null) Finished(this, EventArgs.Empty);
            }
            catch { }

            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                if (_pollTimer != null) { _pollTimer.Stop(); _pollTimer.Dispose(); }
                if (_topTimer != null) { _topTimer.Stop(); _topTimer.Dispose(); }
                if (_fadeTimer != null) { _fadeTimer.Stop(); _fadeTimer.Dispose(); }
            }
            catch { }

            try
            {
                if (_cursorHidden)
                {
                    Cursor.Show();
                    _cursorHidden = false;
                }
            }
            catch { }

            base.OnFormClosed(e);
        }
    }

    // ══════════════════ 修改动画（托盘 → 弹窗） ══════════════════
    // 只负责"把用户选的视频收进数据目录 + 切开关"，不发号施令：
    // 换背景动画时由托盘回调先停 mpv、换完再重连，避免文件被占用。
    internal sealed class AnimationForm : Form
    {
        private readonly Action _stopBackground;
        private readonly Action _applyBackground;

        private Label _lblBoot;
        private Label _lblLoop;
        private bool _busy;
        private bool _preview;

        /// <summary>用户点了「预览片头」：窗口关掉之后由托盘去播，避免弹窗挡住片头。</summary>
        public bool PreviewRequested { get { return _preview; } }

        public AnimationForm(Action stopBackground, Action applyBackground)
        {
            _stopBackground = stopBackground;
            _applyBackground = applyBackground;

            Text = "修改动画";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(460, 404);

            // 标题栏图标：Form.Icon 不填的话会显示 WinForms 自带的通用图标，
            // 跟 exe 文件图标（编译时 /win32icon 嵌入的那只绿眼睛）对不上。
            try
            {
                if (File.Exists(P.IconFile)) Icon = new Icon(P.IconFile);
            }
            catch { }

            BuildUi();
            RefreshInfo();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { Activate(); BringToFront(); }
            catch { }
        }

        private void BuildUi()
        {
            // ── 开机动画 ──
            GroupBox gbBoot = new GroupBox();
            gbBoot.Text = "开机动画（全屏片头，登录后播一次）";
            gbBoot.Bounds = new Rectangle(12, 10, 436, 140);
            Controls.Add(gbBoot);

            _lblBoot = new Label();
            _lblBoot.Bounds = new Rectangle(14, 24, 408, 42);
            gbBoot.Controls.Add(_lblBoot);

            Button selBoot = MakeButton("选择视频…", 14, 74, 108);
            selBoot.Click += delegate { PickVideo(true); };
            gbBoot.Controls.Add(selBoot);

            Button prevBoot = MakeButton("预览片头", 130, 74, 92);
            prevBoot.Click += delegate { RequestPreview(); };
            gbBoot.Controls.Add(prevBoot);

            Button defBoot = MakeButton("恢复默认", 230, 74, 92);
            defBoot.Click += delegate { ResetBoot(); };
            gbBoot.Controls.Add(defBoot);

            Label hintBoot = new Label();
            hintBoot.Text = "建议 16:9、5~20 秒；带音轨也行，声音受「片头音效 / 音量」控制";
            hintBoot.ForeColor = Color.FromArgb(110, 110, 110);
            hintBoot.Font = new Font("Microsoft YaHei UI", 8f);
            hintBoot.Bounds = new Rectangle(14, 108, 410, 20);
            gbBoot.Controls.Add(hintBoot);

            // ── 背景动画 ──
            GroupBox gbLoop = new GroupBox();
            gbLoop.Text = "背景动画（桌面无限循环，静音）";
            gbLoop.Bounds = new Rectangle(12, 158, 436, 140);
            Controls.Add(gbLoop);

            _lblLoop = new Label();
            _lblLoop.Bounds = new Rectangle(14, 24, 408, 42);
            gbLoop.Controls.Add(_lblLoop);

            Button selLoop = MakeButton("选择视频…", 14, 74, 108);
            selLoop.Click += delegate { PickVideo(false); };
            gbLoop.Controls.Add(selLoop);

            Button defLoop = MakeButton("恢复默认", 130, 74, 92);
            defLoop.Click += delegate { ResetLoop(); };
            gbLoop.Controls.Add(defLoop);

            Label hintLoop = new Label();
            hintLoop.Text = "选好立刻生效；2~15 秒的无缝循环最耐看，太长的循环会显卡";
            hintLoop.ForeColor = Color.FromArgb(110, 110, 110);
            hintLoop.Font = new Font("Microsoft YaHei UI", 8f);
            hintLoop.Bounds = new Rectangle(14, 108, 410, 20);
            gbLoop.Controls.Add(hintLoop);

            // ── 说明 + 关闭 ──
            Label note = new Label();
            note.Text = "上传的视频会复制到 %LOCALAPPDATA%\\WhaleTheme\\custom，原文件不动；\r\n" +
                        "默认素材始终保留，随时可以点「恢复默认」切回去。";
            note.ForeColor = Color.FromArgb(110, 110, 110);
            note.Font = new Font("Microsoft YaHei UI", 8f);
            note.Bounds = new Rectangle(16, 306, 430, 46);
            Controls.Add(note);

            Button close = MakeButton("关闭", 352, 362, 96);
            close.Click += delegate { Close(); };
            Controls.Add(close);
        }

        private Button MakeButton(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Bounds = new Rectangle(x, y, w, 28);
            b.UseVisualStyleBackColor = true;
            return b;
        }

        // ── 状态显示 ──
        private void RefreshInfo()
        {
            _lblBoot.Text = Describe(Media.BootFile, Media.BootIsCustom, "boot-intro.mp4");
            _lblLoop.Text = Describe(Media.LoopFile, Media.LoopIsCustom, "whale-loop.mp4");
        }

        private static string Describe(string file, bool custom, string defaultName)
        {
            string head = custom ? "当前：自定义" : "当前：默认";
            if (!File.Exists(file))
                return head + " · 文件缺失（" + defaultName + "），播放时会跳过";

            double dur = Durations.Read(file);
            string second = (dur > 0 ? dur.ToString("0.00") + " 秒" : "时长未知") + " · " + SizeText(file);
            return head + " · " + Path.GetFileName(file) + "\r\n" + second;
        }

        private static string SizeText(string file)
        {
            try
            {
                double mb = new FileInfo(file).Length / 1048576.0;
                if (mb >= 1) return mb.ToString("0.0") + " MB";
                return (mb * 1024).ToString("0") + " KB";
            }
            catch { return "大小未知"; }
        }

        // ── 选择 / 重置 / 预览 ──
        private void PickVideo(bool boot)
        {
            if (_busy) return;

            string src;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = boot ? "选择开机动画视频" : "选择背景动画视频";
                d.Filter = "视频文件|*.mp4;*.mov;*.m4v;*.mkv;*.webm;*.avi;*.wmv;*.flv;*.ts" +
                           "|所有文件|*.*";
                d.CheckFileExists = true;
                d.Multiselect = false;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                src = d.FileName;
            }

            long len = 0;
            try { len = new FileInfo(src).Length; } catch { }
            if (len > 800L * 1024 * 1024)
            {
                MessageBox.Show(this,
                    "这个文件有 " + (len / 1048576) + " MB，超过 800MB 上限了。\r\n" +
                    "建议先压缩或剪短一点再导入。", "文件太大",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _busy = true;
            UseWaitCursor = true;
            string err;
            try
            {
                if (!boot) _stopBackground();     // 先放掉正在播的文件，才删得掉/覆盖得动
                err = boot ? Media.InstallBoot(src) : Media.InstallLoop(src);
            }
            catch (Exception ex)
            {
                Log.W("导入动画异常: " + ex.Message);
                err = "导入时出错：" + ex.Message;
            }
            finally
            {
                if (!boot) _applyBackground();    // 成功失败都把桌面接回去，不留黑屏
                UseWaitCursor = false;
                _busy = false;
            }

            RefreshInfo();

            if (err != null)
                MessageBox.Show(this, err, "没有成功", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                Log.W((boot ? "开机动画" : "背景动画") + "已更新，来源：" + src);
        }

        private void ResetBoot()
        {
            if (_busy) return;
            if (!Media.BootIsCustom && Media.CustomBootFile == null)
            {
                MessageBox.Show(this, "现在用的就是默认开机动画。", "无需操作",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _busy = true;
            UseWaitCursor = true;
            try { Media.ClearBoot(); }
            catch (Exception ex) { Log.W("恢复默认开机动画失败: " + ex.Message); }
            finally { UseWaitCursor = false; _busy = false; }

            RefreshInfo();
        }

        private void ResetLoop()
        {
            if (_busy) return;
            if (!Media.LoopIsCustom && Media.CustomLoopFile == null)
            {
                MessageBox.Show(this, "现在用的就是默认背景动画。", "无需操作",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _busy = true;
            UseWaitCursor = true;
            try
            {
                _stopBackground();
                Media.ClearLoop();
                _applyBackground();     // 切回默认之后重新接桌面
            }
            catch (Exception ex)
            {
                Log.W("恢复默认背景动画失败: " + ex.Message);
            }
            finally { UseWaitCursor = false; _busy = false; }

            RefreshInfo();
        }

        private void RequestPreview()
        {
            if (_busy) return;
            if (!File.Exists(Media.BootFile))
            {
                MessageBox.Show(this, "找不到要播放的视频文件。", "无法预览",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _preview = true;
            Close();
        }
    }

    // ─────────────────────── 托盘主程序 ───────────────────────
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly Mpv _deskMpv = new Mpv("whaletheme-desk");
        private readonly Mpv _introMpv = new Mpv("whaletheme-intro");
        private readonly Config _cfg;

        private ToolStripMenuItem _miPause;
        private ToolStripMenuItem _miStop;
        private ToolStripMenuItem _miApply;
        private ToolStripMenuItem _miReplay;
        private ToolStripMenuItem _miAutoStart;
        private ToolStripMenuItem _miSound;
        private ToolStripMenuItem _miVolume;

        private IntPtr _host = IntPtr.Zero;
        private IntroForm _introForm;
        private bool _applied;
        private bool _paused;

        public TrayApp(bool bootMode)
        {
            _cfg = Config.Load();
            Media.Cfg = _cfg;          // 让 Media 这类静态工具能查到"用默认还是用自定义"
            AutoStart.EnsureBestMode();

            _tray = new NotifyIcon();
            _tray.Text = "鲸鱼娘 · 未至之境";
            try
            {
                if (File.Exists(P.IconFile)) _tray.Icon = new Icon(P.IconFile);
                else _tray.Icon = SystemIcons.Application;
            }
            catch (Exception ex)
            {
                Log.W("图标加载失败: " + ex.Message);
                _tray.Icon = SystemIcons.Application;
            }
            _tray.Visible = true;
            _tray.DoubleClick += delegate { TogglePause(); };
            _tray.ContextMenuStrip = BuildMenu();

            // 先把桌面层接好（静音循环在跑），再播片头
            ConnectDesktop();
            PlayIntro();

            Log.W("启动完成（boot=" + bootMode + "）");
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = new Font("Microsoft YaHei UI", 9f);
            m.ShowImageMargin = false;

            ToolStripMenuItem head = new ToolStripMenuItem("鲸鱼娘 · 未至之境");
            head.Enabled = false;
            m.Items.Add(head);
            m.Items.Add(new ToolStripSeparator());

            // 「修改动画…」放在「暂停动画」上面，符合"时间上最先要用的在最上面"
            ToolStripMenuItem editAnim = new ToolStripMenuItem("修改动画…");
            editAnim.Click += delegate { ShowAnimationDialog(); };
            m.Items.Add(editAnim);

            _miPause = new ToolStripMenuItem("暂停动画");
            _miPause.Click += delegate { TogglePause(); };
            m.Items.Add(_miPause);

            _miStop = new ToolStripMenuItem("停止并还原");
            _miStop.Click += delegate { StopAndRestore(true); };
            m.Items.Add(_miStop);

            m.Items.Add(new ToolStripSeparator());

            _miReplay = new ToolStripMenuItem("重播片头");
            _miReplay.Click += delegate
            {
                if (!_applied) ConnectDesktop();
                PlayIntro();
            };
            m.Items.Add(_miReplay);

            _miApply = new ToolStripMenuItem("应用主题（不播片头）");
            _miApply.Click += delegate { ConnectDesktop(); };
            m.Items.Add(_miApply);

            m.Items.Add(new ToolStripSeparator());

            _miAutoStart = new ToolStripMenuItem("开机自启");
            _miAutoStart.CheckOnClick = false;
            _miAutoStart.Checked = AutoStart.IsOn();
            _miAutoStart.Click += delegate
            {
                bool target = !AutoStart.IsOn();
                bool ok = AutoStart.Set(target);
                _miAutoStart.Checked = AutoStart.IsOn();

                if (!ok)
                {
                    Balloon("开机自启没设置成功", "写注册表之后回读不到，详情看 log.txt");
                }
                else if (target)
                {
                    Balloon("开机自启已开启", "下次登录 Windows 时会自动播放主题。");
                }
                else
                {
                    Balloon("开机自启已关闭", "下次开机不会再自动播放。");
                }
            };
            m.Items.Add(_miAutoStart);

            _miSound = new ToolStripMenuItem("片头音效");
            _miSound.Checked = _cfg.IntroSound;
            _miSound.Click += delegate
            {
                _cfg.IntroSound = !_cfg.IntroSound;
                _cfg.Save();
                _miSound.Checked = _cfg.IntroSound;
            };
            m.Items.Add(_miSound);

            _miVolume = new ToolStripMenuItem("片头音量");
            int[] levels = new int[] { 25, 50, 75, 100 };
            foreach (int lv in levels)
            {
                ToolStripMenuItem it = new ToolStripMenuItem(lv + "%");
                it.Tag = lv;
                it.Checked = (_cfg.Volume == lv);
                it.Click += delegate(object s, EventArgs e)
                {
                    ToolStripMenuItem self = (ToolStripMenuItem)s;
                    _cfg.Volume = (int)self.Tag;
                    _cfg.Save();
                    foreach (ToolStripItem sub in _miVolume.DropDownItems)
                    {
                        ToolStripMenuItem mi = sub as ToolStripMenuItem;
                        if (mi != null) mi.Checked = ((int)mi.Tag == _cfg.Volume);
                    }
                };
                _miVolume.DropDownItems.Add(it);
            }
            m.Items.Add(_miVolume);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem open = new ToolStripMenuItem("打开主题文件夹");
            open.Click += delegate
            {
                try { Process.Start("explorer.exe", "\"" + P.ExeDir + "\""); }
                catch (Exception ex) { Log.W("打开文件夹失败: " + ex.Message); }
            };
            m.Items.Add(open);

            ToolStripMenuItem about = new ToolStripMenuItem("关于 / 署名");
            about.Click += delegate { ShowAbout(); };
            m.Items.Add(about);

            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem quit = new ToolStripMenuItem("退出");
            quit.Click += delegate
            {
                StopAndRestore(false);
                _tray.Visible = false;
                ExitThread();
            };
            m.Items.Add(quit);

            return m;
        }

        // ── 接桌面层（静音循环） ──
        private void ConnectDesktop()
        {
            try
            {
                if (!File.Exists(Media.LoopFile))
                {
                    Balloon("缺少背景动画", "找不到要循环播放的视频，桌面会停在静态壁纸。");
                    return;
                }

                Bootstrap.EnsureFirstFrame();
                Snapshot.Capture();
                Visuals.Apply();

                _host = DesktopLayer.FindHost();
                _deskMpv.StartLoop(_host, Media.LoopFile);

                _applied = true;
                _paused = false;

                // 等 mpv 建好窗口，挂进桌面层并铺满（最多重试 20 次，共 10 秒）
                int tries = 0;
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 500;
                t.Tick += delegate
                {
                    tries++;
                    bool done = false;
                    try
                    {
                        IntPtr child = DesktopLayer.TryMount(_host, _deskMpv.Proc, tries >= 5);
                        if (child != IntPtr.Zero)
                        {
                            DesktopLayer.FitChild(_host, child, DesktopLayer.ZOrderBelow(_host));
                            Log.W("挂载成功：" + DesktopLayer.Describe(child));
                            done = true;
                        }
                    }
                    catch (Exception ex) { Log.W("挂载桌面层失败: " + ex.Message); }

                    if (done || tries >= 20)
                    {
                        t.Stop();
                        t.Dispose();
                        if (!done) Log.W("20 次重试后仍未挂上，桌面会停在静态首帧");
                    }
                };
                t.Start();

                UpdateMenuState();
                Log.W("桌面层已连接");
            }
            catch (Exception ex)
            {
                Log.W("连接桌面层失败: " + ex.Message);
                Balloon("应用失败", ex.Message);
            }
        }

        // ── 播片头 ──
        private void PlayIntro()
        {
            try
            {
                if (!File.Exists(Media.BootFile))
                {
                    Balloon("缺少开机动画", "找不到要播放的片头视频，已跳过。");
                    return;
                }

                if (_introForm != null)
                {
                    Log.W("已有片头在播，忽略本次请求");
                    return;
                }

                bool mute = !_cfg.IntroSound;
                string file = Media.BootFile;
                IntroForm f = new IntroForm(_introMpv, file, _cfg.Volume, mute);
                f.Finished += delegate
                {
                    _introForm = null;
                    Log.W("片头窗口已关闭，桌面已接管");
                };
                _introForm = f;
                f.Show();
                Log.W("片头开始（" + Path.GetFileName(file) + "，静音=" + mute + "，音量=" + _cfg.Volume + "）");
            }
            catch (Exception ex)
            {
                Log.W("播放片头失败: " + ex.Message);
                _introForm = null;
            }
        }

        // ── 暂停 ──
        private void TogglePause()
        {
            if (!_applied || !_deskMpv.Running)
            {
                ConnectDesktop();
                return;
            }
            _paused = !_paused;
            _deskMpv.SetPaused(_paused);
            UpdateMenuState();
        }

        // ── 修改动画（弹窗） ──
        private void ShowAnimationDialog()
        {
            try
            {
                bool preview;
                using (AnimationForm f = new AnimationForm(
                    delegate { _deskMpv.Stop(); },      // 换文件之前先放开正在播的那个
                    delegate { ReloadBackground(); }))  // 换完再按新文件接回去
                {
                    f.ShowDialog();
                    preview = f.PreviewRequested;
                }
                if (preview) PlayIntro();
            }
            catch (Exception ex)
            {
                Log.W("打开修改动画窗口失败: " + ex.Message);
                Balloon("打不开设置窗口", ex.Message);
            }
        }

        /// <summary>
        /// 背景动画换过之后：本来就在跑就重连一次（新文件立刻生效）；
        /// 处于"已停止"状态就只把首帧壁纸准备好，不擅自把主题打开。
        /// </summary>
        private void ReloadBackground()
        {
            try
            {
                if (_applied) ConnectDesktop();
                else Bootstrap.EnsureFirstFrame();
            }
            catch (Exception ex)
            {
                Log.W("切换背景动画失败: " + ex.Message);
                Balloon("切换背景动画失败", ex.Message);
            }
        }

        // ── 停止并还原 ──
        private void StopAndRestore(bool notify)
        {
            try
            {
                if (_introForm != null)
                {
                    try { _introForm.Close(); } catch { }
                    _introForm = null;
                }
                _introMpv.Stop();
                _deskMpv.Stop();

                Snapshot.Restore();

                try
                {
                    string wp = null;
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false))
                    {
                        if (k != null) wp = Convert.ToString(k.GetValue("Wallpaper", null));
                    }
                    if (!string.IsNullOrEmpty(wp) && File.Exists(wp))
                    {
                        N.SetWallpaper(wp);
                        Log.W("壁纸已还原: " + wp);
                    }
                    else
                    {
                        N.SetWallpaper("");
                        N.Broadcast();
                        Log.W("原壁纸为空或文件已不存在，已清空壁纸路径");
                    }
                }
                catch (Exception ex)
                {
                    Log.W("刷回壁纸失败: " + ex.Message);
                }

                Snapshot.Discard();

                _applied = false;
                _paused = false;
                UpdateMenuState();
                Log.W("已停止并还原");

                if (notify)
                {
                    string extra = LoginBackground.IsApplied()
                        ? "\n\n注意：登录界面背景仍是鲸鱼娘，\n请右键「tools\\还原登录界面.cmd」→ 以管理员身份运行。"
                        : "";
                    Balloon("已还原", "桌面壁纸已恢复原状。" + extra);
                }
            }
            catch (Exception ex)
            {
                Log.W("停止还原失败: " + ex.Message);
                Balloon("还原失败", ex.Message);
            }
        }

        private void UpdateMenuState()
        {
            if (_miPause != null)
                _miPause.Text = _paused ? "继续动画" : "暂停动画";
            if (_miStop != null)
                _miStop.Enabled = _applied;
            if (_miApply != null)
                _miApply.Text = _applied ? "重新连接桌面" : "应用主题（不播片头）";
            if (_tray != null)
                _tray.Text = _applied
                    ? (_paused ? "鲸鱼娘 · 已暂停" : "鲸鱼娘 · 运行中")
                    : "鲸鱼娘 · 已停止";
        }

        private void Balloon(string title, string text)
        {
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(6000);
            }
            catch { }
        }

        private void ShowAbout()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Windows 11 主题软件制作：");
            sb.AppendLine("    抖音：上山吃老虎");
            sb.AppendLine("    E-mail: 156116422@qq.com");
            sb.AppendLine();
            sb.AppendLine("素材来源与许可：");
            sb.AppendLine("    上善              角色形象原作");
            sb.AppendLine("    ZipZipPipe        加入 DeepSeek 元素的二次设计");
            sb.AppendLine("    Small-tailqwq     dsh-deep-whale 皮肤家族的再设计");
            sb.AppendLine("    上山吃老虎        原素材动画超分再渲染");
            sb.AppendLine();
            sb.AppendLine("许可证：CC BY-NC-SA 4.0");
            sb.AppendLine("    自己电脑上用，随便用");
            sb.AppendLine("    不许商用、不许拿去卖");
            sb.AppendLine("    分享给别人必须署名上面这几位，并沿用同一协议");
            sb.AppendLine();
            sb.AppendLine("播放内核：mpv（GPL v2+）");
            sb.AppendLine("    本程序内嵌的 mpv 遵循 GNU GPL v2 或更高版本许可");

            MessageBox.Show(sb.ToString(), "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _deskMpv.Dispose(); } catch { }
                try { _introMpv.Dispose(); } catch { }
                try { _tray.Visible = false; _tray.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    // ─────────────── 登录界面背景（需管理员，单独脚本调用） ───────────────
    internal static class LoginBackground
    {
        private const string PolicyKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\Background";

        public static bool IsApplied()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(PolicyKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue("OEMBackground", null);
                    if (v == null) return false;
                    return (int)v == 1;
                }
            }
            catch { return false; }
        }
    }

    // ───────────────────────────── 入口 ─────────────────────────────
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            bool boot = false;
            bool stopOnly = false;
            bool autoOn = false;
            bool autoOff = false;
            foreach (string a in args)
            {
                if (string.Equals(a, "--boot", StringComparison.OrdinalIgnoreCase)) boot = true;
                else if (string.Equals(a, "--stop", StringComparison.OrdinalIgnoreCase)) stopOnly = true;
                else if (string.Equals(a, "--autostart-on", StringComparison.OrdinalIgnoreCase)) autoOn = true;
                else if (string.Equals(a, "--autostart-off", StringComparison.OrdinalIgnoreCase)) autoOff = true;
            }

            // 静默设置自启后立刻退出，不建任何窗口（供命令行/诊断使用）
            if (autoOn || autoOff)
            {
                bool ok = AutoStart.Set(autoOn);
                Log.W("命令行设置自启 = " + (autoOn ? "开" : "关") + "，结果 = " + (ok ? "成功" : "失败"));
                return;
            }

            bool createdNew = true;
            using (Mutex mutex = new Mutex(true, "Local\\WhaleTheme.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // 开机时可能被计划任务和 Run 键各拉起一次，第二份静默退出，别弹窗
                    if (boot) Log.W("已有实例在跑，本次开机启动静默退出");
                    else
                        MessageBox.Show("鲸鱼娘主题已经在运行了，看右下角托盘图标。",
                            "鲸鱼娘 · 未至之境", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try { SetProcessDPIAware(); } catch { }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Log.W("=== 启动，参数: " + string.Join(" ", args) + " ===");

                double gap = GapFromExplorer();
                if (gap >= 0)
                    Log.W("本次启动距 explorer 起来 " + gap.ToString("0.0") + " 秒（越小＝起得越早）");

                if (Bootstrap.NeedExtract())
                {
                    using (SplashForm sp = new SplashForm())
                    {
                        sp.Show();
                        sp.Refresh();
                        Application.DoEvents();
                        try
                        {
                            Bootstrap.Extract();
                        }
                        catch (Exception ex)
                        {
                            Log.W("解压失败: " + ex.Message);
                            MessageBox.Show("准备播放内核失败：\r\n" + ex.Message,
                                "鲸鱼娘 · 未至之境", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        sp.Close();
                    }
                }

                if (stopOnly)
                {
                    Snapshot.Restore();
                    Snapshot.Discard();
                    Log.W("--stop 完成");
                    return;
                }

                // 开机自启时，程序往往比桌面 shell 先起来。
                // 不等一下的话，Progman / WorkerW 还没建好，视频就挂不上，
                // 表现就是"开机自启没效果"。
                if (boot) WaitForShell();

                Application.Run(new TrayApp(boot));
                GC.KeepAlive(mutex);
            }
        }

        /// <summary>本进程比 explorer 晚多少秒起来，用来看自启到底起得早不早。</summary>
        private static double GapFromExplorer()
        {
            try
            {
                Process[] ex = Process.GetProcessesByName("explorer");
                if (ex.Length == 0) return -1;
                DateTime e = ex[0].StartTime;
                DateTime me = Process.GetCurrentProcess().StartTime;
                double s = (me - e).TotalSeconds;
                return s < 0 ? -1 : s;
            }
            catch { return -1; }
        }

        /// <summary>等桌面 shell 就绪（Progman 出现），最多 20 秒。</summary>
        private static void WaitForShell()
        {
            for (int i = 0; i < 100; i++)
            {
                if (N.FindWindow("Progman", null) != IntPtr.Zero)
                {
                    Log.W("桌面 shell 就绪，等待了 " + (i * 200) + " ms");
                    Thread.Sleep(600);   // 再给 explorer 一点时间把 WorkerW 建好
                    return;
                }
                Thread.Sleep(200);
            }
            Log.W("等了 20 秒仍未见 Progman，照常继续");
        }
    }
}
