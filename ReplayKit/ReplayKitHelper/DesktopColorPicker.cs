using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ReplayKitHelper
{
    internal static class DesktopColorPicker
    {
        private const int MouseHook = 14;
        private const int KeyboardHook = 13;
        private const int LeftDown = 0x0201;
        private const int RightDown = 0x0204;
        private const int Escape = 0x1B;
        private static readonly object Gate = new object();
        private static bool active;

        private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardData
        {
            public uint VirtualKey;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hook, HookCallback callback, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr context);

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr context, int x, int y);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

        // win10 1607+. older builds just keep the process-wide (unaware) context, which still samples correctly on a single-scale desktop.
        private static IntPtr TrySetDpiContext(IntPtr context)
        {
            try { return SetThreadDpiAwarenessContext(context); }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
            catch (DllNotFoundException) { return IntPtr.Zero; }
        }

        public static string Pick()
        {
            lock (Gate)
            {
                if (active) throw new InvalidOperationException("A color picker is already active.");
                active = true;
            }

            string result = "";
            try
            {
                StaRunner.Run(() => result = RunPicker());
                return result;
            }
            finally
            {
                lock (Gate) active = false;
            }
        }

        private static string RunPicker()
        {
            string result = "";
            bool finished = false;
            IntPtr mouseHandle = IntPtr.Zero;
            IntPtr keyboardHandle = IntPtr.Zero;

            void Finish(string color)
            {
                if (finished) return;
                finished = true;
                result = color ?? "";
                Application.ExitThread();
            }

            HookCallback mouseCallback = (code, message, data) =>
            {
                if (code >= 0 && message.ToInt32() == LeftDown)
                {
                    Finish(ReadScreenColor());
                    return new IntPtr(1);
                }
                if (code >= 0 && message.ToInt32() == RightDown)
                {
                    Finish("");
                    return new IntPtr(1);
                }
                return CallNextHookEx(mouseHandle, code, message, data);
            };
            HookCallback keyboardCallback = (code, message, data) =>
            {
                if (code >= 0 && Marshal.PtrToStructure<KeyboardData>(data).VirtualKey == Escape)
                {
                    Finish("");
                    return new IntPtr(1);
                }
                return CallNextHookEx(keyboardHandle, code, message, data);
            };

            IntPtr module = GetModuleHandle(null);
            mouseHandle = SetWindowsHookEx(MouseHook, mouseCallback, module, 0);
            keyboardHandle = SetWindowsHookEx(KeyboardHook, keyboardCallback, module, 0);
            if (mouseHandle == IntPtr.Zero || keyboardHandle == IntPtr.Zero)
            {
                if (mouseHandle != IntPtr.Zero) UnhookWindowsHookEx(mouseHandle);
                if (keyboardHandle != IntPtr.Zero) UnhookWindowsHookEx(keyboardHandle);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the desktop color picker.");
            }

            using (var timeout = new Timer { Interval = 60000 })
            {
                timeout.Tick += (sender, args) => Finish("");
                timeout.Start();
                try { Application.Run(); }
                finally
                {
                    timeout.Stop();
                    UnhookWindowsHookEx(mouseHandle);
                    UnhookWindowsHookEx(keyboardHandle);
                    GC.KeepAlive(mouseCallback);
                    GC.KeepAlive(keyboardCallback);
                }
            }
            return result;
        }

        // the helper process is dpi-unaware, so windows virtualises both the cursor position and the screen dc for it. those two lies agree on a single-scale desktop but drift apart the moment monitors run different scale factors, which lands the sample on the wrong pixel -- the "doesnt work on multi monitor" case. asking for real coordinates for the duration of the read fixes it, and the context is per-thread so nothing else in the helper changes.
        private static string ReadScreenColor()
        {
            IntPtr previousDpi = TrySetDpiContext(PerMonitorAwareV2);
            try
            {
                if (!GetCursorPos(out Point point)) throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr context = GetDC(IntPtr.Zero);
                if (context == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    uint color = GetPixel(context, point.X, point.Y);
                    if (color == uint.MaxValue) throw new Win32Exception("Could not read the selected desktop pixel.");
                    int red = (int)(color & 0xFF);
                    int green = (int)((color >> 8) & 0xFF);
                    int blue = (int)((color >> 16) & 0xFF);
                    return "#" + red.ToString("X2") + green.ToString("X2") + blue.ToString("X2");
                }
                finally { ReleaseDC(IntPtr.Zero, context); }
            }
            finally { if (previousDpi != IntPtr.Zero) TrySetDpiContext(previousDpi); }
        }
    }
}
