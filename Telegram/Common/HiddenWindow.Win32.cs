//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.InteropServices;
using Telegram.Host;
using Telegram.Services;

namespace Telegram.Common
{
    /// <summary>
    /// A top-level window that is never shown and never destroyed, for what has to belong to a
    /// window but must not go with one: the media transport controls and the web proxy carriers'
    /// WebView2 controllers. Closing to the notification area icon destroys the main window on
    /// WinUI, and anything bound to its HWND dies with it and stays dead when a new one opens.
    ///
    /// Created on the UI thread as the host starts, because a window's messages are pumped by the
    /// thread that creates it.
    /// </summary>
    internal static class HiddenWindow
    {
        private static WndProc _wndProc;   // field, not a lambda: the thunk outlives the window

        public static IntPtr Handle { get; private set; }

        public static void Create()
        {
            if (Handle != IntPtr.Zero)
            {
                return;
            }

            _wndProc = OnMessage;

            var className = Marshal.StringToHGlobalUni("TelegramHiddenWindow");
            var wc = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = Win32.GetModuleHandleW(IntPtr.Zero),
                lpszClassName = className
            };

            Win32.RegisterClassExW(ref wc);

            // A real top-level window rather than HWND_MESSAGE, like the hidden one Chromium binds
            // its transport controls to. A message-only window was never tried.
            Handle = Win32.CreateWindowExW(0, className, IntPtr.Zero,
                Win32.WS_OVERLAPPED, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);

            if (Handle == IntPtr.Zero)
            {
                Logger.Error($"CreateWindowExW failed: {Marshal.GetLastPInvokeError()}");
            }
        }

        /// <summary>
        /// Being top level and never destroyed, this is the one window sure to hear that the
        /// session is ending, with or without a main window or the notification area icon. Windows
        /// may end the process any time after WM_ENDSESSION returns, and Main never resumes, so
        /// TDLib is closed before it does.
        /// </summary>
        private static IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Win32.WM_ENDSESSION && wParam != IntPtr.Zero)
            {
                // An exception cannot cross back into user32.
                try
                {
                    LifetimeService.Current.CloseAll();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
            }

            return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
