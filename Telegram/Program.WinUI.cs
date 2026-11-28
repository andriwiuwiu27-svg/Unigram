//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Settings;
using Microsoft.Win32.SafeHandles;
using Microsoft.Windows.AppLifecycle;
using Telegram.Common;
using Telegram.Host;
using Telegram.Navigation;
using Telegram.Services;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace Telegram
{
    /// <summary>
    /// The entry point the XAML compiler would otherwise generate, taken over for one reason:
    /// <see cref="XamlOptionalChanges"/> has to be set before XAML initialises, and the generated
    /// Main gives no room to do it.
    ///
    /// <c>DXamlCore::InitializeImpl</c> calls <c>XamlOptionalChanges::LockInternal()</c> as its
    /// first statement - their comment calls it "the single choke point for both
    /// Application.Start() and WindowsXamlManager.InitializeForCurrentThread()" - so anything
    /// enabled from the App constructor arrives after the bitmask is locked and is silently
    /// dropped. Hence DISABLE_XAML_GENERATED_MAIN, and hence the body below is the generated one
    /// verbatim apart from the four calls.
    /// </summary>
    public static class Program
    {
        // A running instance answers a redirect on a pool thread, whatever its UI thread is doing,
        // so one that has not answered by now is not going to.
        private static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(10);

        private static DispatcherQueue _dispatcher;

        // Redirected launches the dispatcher has queued and not run yet, and whether the process
        // has decided to end. One lock, so neither can change while the other is read.
        private static readonly object _exitLock = new();
        private static int _pendingLaunches;
        private static bool _exiting;

        // Reset while the process is ending. A launch that arrives then waits on it in
        // OnRedirected and never returns, unless the exit is called off.
        private static readonly ManualResetEventSlim _takingLaunches = new(true);

        // Set while no share window is open or queued, and waited on by every process that
        // redirected a share here. Under _exitLock, with the counts it is set from.
        private static EventWaitHandle _sharesIdle;
        private static int _openShares;

        /// <summary>
        /// This process started after the user ended a running instance that took no launches.
        /// Read by App, which reports it once the crash reporter is up.
        /// </summary>
        public static bool EndedUnresponsiveInstance { get; private set; }

        [STAThread]
        static void Main(string[] args)
        {
#if !CSWINRT3_0
            WinRT.ComWrappersSupport.InitializeComWrappers();
#endif

            // One instance, which the UWP app model gave the other flavours for free and this one
            // has to ask for: launching a packaged desktop app again simply starts a second
            // process. That second process then opens its own window over a database the first one
            // holds, which is the empty window you get from the Start menu while the tray icon is
            // up. Registering a key here makes every later launch redirect into this process.
            //
            // First, before anything this process would only do for itself.
            var keyInstance = AppInstance.FindOrRegisterForKey("Telegram");

            while (!keyInstance.IsCurrent)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (RedirectActivationTo(activation, keyInstance))
                {
                    if (activation.Data is ShareTargetActivatedEventArgs)
                    {
                        WaitForShares(keyInstance);
                    }

                    GC.KeepAlive(activation);
                    return;
                }

                // The instance that held the key has exited, so it is free - unless another launch
                // took it first, and then this one redirects to that.
                keyInstance = AppInstance.FindOrRegisterForKey("Telegram");
            }

            // Before any window: the updater waits for this process to be gone, then starts the
            // new version itself.
            if (UnpackagedUpdates.TryStartUpdater())
            {
                return;
            }

            keyInstance.Activated += OnRedirected;

            Toast.RegisterApplication();

            EnableOptionalChanges();

            // The system compositor instead of the in-process one, chosen once per process and
            // before anything creates a Compositor - so ahead of Application.Start. It is behind a
            // Limited Access Feature, and either step failing leaves the in-process engine.
            if (AppSettings.Diagnostics.UseSystemCompositor)
            {
                var unlocked = FeatureTokenGenerator.TryUnlockFeature("com.microsoft.windows.composition.engine");
                var engine = unlocked && CompositionEngine.TrySetProcessEngine(CompositionEngineType.System);
                Logger.Info($"System composition engine: unlocked {unlocked}, set {engine}");
            }

            Application.Start(p =>
            {
                _dispatcher = DispatcherQueue.GetForCurrentThread();

                var context = new DispatcherQueueSynchronizationContext(_dispatcher);
                SynchronizationContext.SetSynchronizationContext(context);

                _ = new App();
            });

            // Every Application.Exit ends up here: Exit on the notification area icon, the last
            // window closing without it, an update. The process exit would otherwise end TDLib in
            // the middle of whatever it was writing.
            BeginExit(force: true);
            LifetimeService.Current.CloseAll();
        }

        /// <summary>
        /// Stops taking launches, because the process is about to end. False, and nothing changes,
        /// when a redirected launch is still queued and about to open a window, unless
        /// <paramref name="force"/> - which a restart is, as it brings the app back anyway.
        /// </summary>
        internal static bool BeginExit(bool force = false)
        {
            lock (_exitLock)
            {
                if (_pendingLaunches > 0 && !force)
                {
                    return false;
                }

                _exiting = true;
                _takingLaunches.Reset();
                return true;
            }
        }

        /// <summary>
        /// The process is not ending after all, so the launches held since
        /// <see cref="BeginExit"/> go through.
        /// </summary>
        internal static void CancelExit()
        {
            lock (_exitLock)
            {
                _exiting = false;
                _takingLaunches.Set();
            }
        }

        /// <summary>
        /// Another launch was handed to this process. It arrives on a background thread. A link or
        /// a share goes through BootStrapper the way the first activation does; anything else only
        /// brings the window back.
        ///
        /// A share is read through the process that redirected it, so this marks the shares busy
        /// before returning, which is what lets that process go: see <see cref="WaitForShares"/>.
        ///
        /// Brought back the way a click on the notification area icon does it, because the window
        /// may be gone: closing to the icon destroys it, and then there is no Main to raise and
        /// no window whose dispatcher could be used, so the UI thread's queue is kept from start.
        ///
        /// A process that is ending cannot take the launch, so this holds it and never returns:
        /// the SDK answers the launching process only once this handler has, and that process
        /// treats this one exiting first as its cue to start in its place - after TDLib has closed,
        /// since that is the last thing this one does.
        /// </summary>
        private static void OnRedirected(object sender, AppActivationArguments args)
        {
            // Outside the lock: Data is a proxy, and every question asked of it is a call into the
            // launching process.
            var activation = args.Data as IActivatedEventArgs;
            var share = activation is ShareTargetActivatedEventArgs;

            if (!share && activation is not IProtocolActivatedEventArgs)
            {
                activation = null;
            }

            while (true)
            {
                _takingLaunches.Wait();

                lock (_exitLock)
                {
                    if (_exiting)
                    {
                        continue;
                    }

                    if (_dispatcher != null && _dispatcher.TryEnqueue(() => RunRedirected(activation)))
                    {
                        _pendingLaunches++;

                        if (share)
                        {
                            _sharesIdle ??= new EventWaitHandle(false, EventResetMode.ManualReset, GetSharesIdleName(Environment.ProcessId));
                            _sharesIdle.Reset();
                        }

                        return;
                    }

                    // The dispatcher is gone, so the process is ending whether it said so or not.
                    _exiting = true;
                    _takingLaunches.Reset();
                }
            }
        }

        private static void RunRedirected(IActivatedEventArgs activation)
        {
            try
            {
                lock (_exitLock)
                {
                    _pendingLaunches--;

                    // Only a restart exits with a launch queued, and it brings the app back itself.
                    if (_exiting)
                    {
                        return;
                    }
                }

                if (activation != null)
                {
                    BootStrapper.Current.ActivateRedirected(activation);
                }
                else
                {
                    BootStrapper.Current.ActivateFromNotifyIcon();
                }
            }
            finally
            {
                // Only now, with the share's window open and counted: setting it any earlier would
                // let the launching process go between the two.
                lock (_exitLock)
                {
                    UpdateSharesIdle();
                }
            }
        }

        /// <summary>
        /// Every share window counts, including one this process was started with, so a closing
        /// window never needs to know how its share arrived.
        /// </summary>
        internal static void TrackShare(WindowContext window)
        {
            lock (_exitLock)
            {
                _openShares++;
            }

            window.Closed += OnShareClosed;
        }

        private static void OnShareClosed(object sender, EventArgs e)
        {
            ((WindowContext)sender).Closed -= OnShareClosed;

            lock (_exitLock)
            {
                _openShares--;
                UpdateSharesIdle();
            }
        }

        private static void UpdateSharesIdle()
        {
            if (_pendingLaunches == 0 && _openShares == 0)
            {
                _sharesIdle?.Set();
            }
        }

        private static string GetSharesIdleName(int processId)
        {
            return $"Local\\Telegram.SharesIdle.{processId}";
        }

        /// <summary>
        /// The share now runs in the instance it was redirected to, but not without this process:
        /// the SDK hands the arguments over as a COM proxy into it, and the share operation, its
        /// data and the files in it are all read through that proxy. So this process stays until
        /// that instance has no share left open, or has exited.
        /// </summary>
        private static void WaitForShares(AppInstance keyInstance)
        {
            // Missing when the instance never took the share - the user gave up on one that was
            // not responding - and then there is nothing to wait for.
            if (!EventWaitHandle.TryOpenExisting(GetSharesIdleName((int)keyInstance.ProcessId), out var idle))
            {
                return;
            }

            using (idle)
            {
                Process owner;

                try
                {
                    owner = Process.GetProcessById((int)keyInstance.ProcessId);
                }
                catch (ArgumentException)
                {
                    return;
                }

                using (owner)
                using (var exited = new ProcessWaitHandle(owner))
                {
                    WaitHandle.WaitAny([idle, exited]);
                }
            }
        }

        private sealed class ProcessWaitHandle : WaitHandle
        {
            public ProcessWaitHandle(Process process)
            {
                // Not owned: the Process closes it.
                SafeWaitHandle = new SafeWaitHandle(process.Handle, false);
            }
        }

        /// <summary>
        /// True when this process is done: the running instance has the activation, or the user
        /// chose to leave it be. False once that instance has exited, and the key is free to take.
        ///
        /// The redirect runs on a pool thread rather than this one: it is a cross-process COM
        /// call, and blocking the STA that is about to become the UI thread on it deadlocks. There
        /// is no window yet, so there is nothing to pump.
        ///
        /// The SDK's own wait for the running instance has no timeout and does not watch its
        /// process, so one that stops taking requests, or exits before taking this one, would hold
        /// this process there for good. Every later launch would then queue up behind it.
        /// </summary>
        private static bool RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
        {
            Process owner;

            try
            {
                // Its PID cannot be reused while keyInstance lives: the SDK holds a handle to it.
                owner = Process.GetProcessById((int)keyInstance.ProcessId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            using (owner)
            {
                // The user launched this process, so it may take the foreground and the running
                // one, in the background, may not: its window would only flash on the taskbar.
                // Handed over rather than used here, because the window to raise may not exist
                // until the running process reopens it from the notification area icon.
                Win32.AllowSetForegroundWindow(keyInstance.ProcessId);

                var redirected = Task.Run(async () => await keyInstance.RedirectActivationToAsync(args));
                var exited = owner.WaitForExitAsync();

                switch (Task.WaitAny([redirected, exited], RedirectTimeout))
                {
                    case 0:
                        return true;
                    case 1:
                        return false;
                }

                // Not through Strings: the language pack is a database the running instance has
                // open, and this process must not touch its data while it lives.
                var text = LocaleFallback.GetString(nameof(Strings.InstanceNotResponding));
                var choice = Win32.MessageBoxW(IntPtr.Zero, text, "Telegram", Win32.MB_YESNO | Win32.MB_ICONWARNING | Win32.MB_SETFOREGROUND);

                // The question can outlive its reason: the instance may have answered meanwhile, or
                // been ended by another launch that asked the same thing.
                if (choice != Win32.IDYES || redirected.IsCompleted)
                {
                    return true;
                }

                if (exited.IsCompleted)
                {
                    return false;
                }

                try
                {
                    owner.Kill();
                }
                catch (Exception)
                {
                    // Exited on its own in the meantime, or out of reach. The wait settles which.
                }

                if (owner.WaitForExit(RedirectTimeout))
                {
                    EndedUnresponsiveInstance = true;
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// All four of them, which is every optional change WinUI 3 2.4 has. Each returns whether
        /// it took, and none is checked: a change this build does not know about is not a reason
        /// to refuse to start, and a locked bitmask would mean the call above moved.
        ///
        /// What they are, in the order they are enabled:
        ///
        /// - DefaultStyleOptimizations swaps the SDK's style set for its *_perf2026.xaml twins,
        ///   which are the same styles with VisualState Setters in place of Storyboards. Worth
        ///   less here than it sounds - about 93% of this app's visual state groups enter an empty
        ///   state at load - so it is an interaction-time win rather than a realization one.
        /// - OptimizeApplyStyles skips applying a style at parse time for an element that is not
        ///   live, and realizes setter values on demand instead of all of them up front. This is
        ///   the one UWP has no equivalent for.
        /// - IconNoGridOptimization parents a FontIcon's TextBlock directly rather than through a
        ///   Grid, which this app realizes a great many of.
        /// - DeferContextFlyoutInit stops every element entering the tree from resolving its
        ///   default ContextFlyout, which for TextBlock and RichTextBlock is a resource-dictionary
        ///   walk per element.
        /// </summary>
        private static void EnableOptionalChanges()
        {
            // An off switch that needs no rebuild: create a file called
            // "xaml-optional-changes.off" in the app's LocalState and relaunch. These four change
            // when styles are applied and what the default ones look like, so they are the first
            // thing to rule out when a control comes up with no template - and ruling them out is
            // otherwise a fifteen minute round trip.
            //
            // Empty turns all four off. Otherwise each line names one to leave off, so a suspect
            // can be confirmed on its own rather than as one of four.
            var disabled = Array.Empty<string>();

            try
            {
                var path = Path.Combine(AppPaths.LocalPath, "xaml-optional-changes.off");
                if (File.Exists(path))
                {
                    disabled = File.ReadAllLines(path)
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .ToArray();

                    if (disabled.Length == 0)
                    {
                        return;
                    }
                }
            }
            catch
            {
                // No identity, or no LocalState yet. Neither is a reason not to start.
            }

            void Enable(XamlChangeId change)
            {
                if (!disabled.Contains(change.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    XamlOptionalChanges.EnableChange(change);
                }
            }

            Enable(XamlChangeId.DefaultStyleOptimizations);
            Enable(XamlChangeId.OptimizeApplyStyles);
            Enable(XamlChangeId.IconNoGridOptimization);
            Enable(XamlChangeId.DeferContextFlyoutInit);
        }
    }
}
