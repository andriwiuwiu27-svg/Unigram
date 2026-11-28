//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Telegram.Common;
using Telegram.Composition;
using Telegram.Converters;
using Telegram.Native.Calls;
using Telegram.Navigation.Services;
using Telegram.Services.Calls;
using Telegram.Td.Api;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Telegram.Controls.Calls
{
    /// <summary>
    /// The ongoing call, in the title bar: a lens that moves with the call's audio, its title,
    /// how long it has run and a mute toggle.
    /// </summary>
    public sealed partial class CallTitleBarIndicator : UserControlEx
    {
        private static readonly Vector2 LensSize = new(28, 16);

        private INavigationService _navigationService;
        private VoipCallBase _call;

        private CompositionLensVisual _lens;
        private CallIndicatorStyle _style;

        private DispatcherTimer _clock;

        private string _callState;

        // NeutralInk's brush, watched for the theme recolouring it in place, which its Background
        // does not see change.
        private SolidColorBrush _neutralBrush;
        private long _neutralBrushToken;
        private long _neutralToken;

        public CallTitleBarIndicator()
        {
            InitializeComponent();
        }

        protected override void OnLoaded()
        {
            _neutralToken = NeutralInk.RegisterPropertyChangedCallback(Border.BackgroundProperty, OnNeutralInkChanged);
            WatchNeutralBrush();

            _lens?.Start();

            // Update can run before the first layout, when going to a state may not take.
            _callState = null;
            UpdateState(false);
        }

        protected override void OnUnloaded()
        {
            NeutralInk.UnregisterPropertyChangedCallback(Border.BackgroundProperty, _neutralToken);
            _neutralBrush?.UnregisterColorChangedCallback(ref _neutralBrushToken);
            _neutralBrush = null;

            _lens?.Stop();
            StopClock();
        }

        public void Update(INavigationService navigationService, VoipCallBase value, CallIndicatorStyle style)
        {
            _navigationService = navigationService;

            if (_call is VoipCall oldPrivateCall)
            {
                oldPrivateCall.StateChanged -= OnStateChanged;
                oldPrivateCall.MediaStateChanged -= OnMediaStateChanged;
                oldPrivateCall.AudioLevelUpdated -= OnAudioLevelUpdated;
            }
            else if (_call is VoipGroupCall oldGroupCall)
            {
                oldGroupCall.Changed -= OnGroupCallChanged;
                oldGroupCall.NetworkStateChanged -= OnNetworkStateChanged;
                oldGroupCall.MutedChanged -= OnGroupCallChanged;
                oldGroupCall.AudioLevelsUpdated -= OnAudioLevelsUpdated;
            }

            // The colours fade between calls, but the first ones have nothing to fade from.
            var animated = _call != null;

            _call = value;

            if (value == null)
            {
                _lens?.Stop();
                StopClock();
                return;
            }

            if (_lens == null || _style != style)
            {
                _lens?.Stop();
                _lens = new CompositionLensVisual(Lens, LensSize, style == CallIndicatorStyle.LayeredLens);
                _style = style;
                animated = false;

                if (IsConnected)
                {
                    _lens.Start();
                }
            }

            if (value is VoipCall newPrivateCall)
            {
                newPrivateCall.StateChanged += OnStateChanged;
                newPrivateCall.MediaStateChanged += OnMediaStateChanged;
                newPrivateCall.AudioLevelUpdated += OnAudioLevelUpdated;

                try
                {
                    if (newPrivateCall.ClientService.TryGetUser(newPrivateCall.UserId, out User user))
                    {
                        TitleLabel.Text = user.FullName();
                    }
                }
                catch
                {
                    // The call can be torn down while it is read.
                }
            }
            else if (value is VoipGroupCall newGroupCall)
            {
                newGroupCall.Changed += OnGroupCallChanged;
                newGroupCall.NetworkStateChanged += OnNetworkStateChanged;
                newGroupCall.MutedChanged += OnGroupCallChanged;
                newGroupCall.AudioLevelsUpdated += OnAudioLevelsUpdated;

                TitleLabel.Text = newGroupCall.GetTitle();
            }

            // Nobody speaks in a stream.
            Audio.Visibility = value is VoipGroupCall { IsRtmpStream: true }
                ? Visibility.Collapsed
                : Visibility.Visible;

            UpdateState(animated);
        }

        private void UpdateState(bool animated)
        {
            var connected = false;
            var muted = true;
            var cantSpeak = false;
            var scheduled = false;
            var late = false;
            var remaining = 0;

            if (_call is VoipCall privateCall)
            {
                connected = privateCall.IsConnected;
                muted = privateCall.AudioState == VoipAudioState.Muted;
            }
            else if (_call is VoipGroupCall groupCall)
            {
                scheduled = groupCall.ScheduledStartDate > 0;
                remaining = (int)(groupCall.ScheduledStartDate - groupCall.ClientService.UnixTime);

                connected = scheduled || groupCall.IsConnected;
                muted = groupCall.IsMuted;
                cantSpeak = scheduled || groupCall.CurrentUser is { IsMutedForAllUsers: true, CanUnmuteSelf: false };
                late = scheduled && remaining < 0;
            }
            else
            {
                return;
            }

            // The first that holds decides.
            var state = !connected
                ? "Connecting"
                : late
                ? "Late"
                : cantSpeak
                ? "CantSpeak"
                : muted
                ? "Muted"
                : "Unmuted";

            if (_callState != state)
            {
                _callState = state;
                VisualStateManager.GoToState(this, state, false);
            }

            // Window activation changes nothing here: the pill is how the call is found from
            // another window, so it has to look the same there.
            if (_lens != null)
            {
                _lens.Motion = !connected
                    ? LensMotion.Sweep
                    : scheduled
                    ? LensMotion.Calm
                    : LensMotion.Voice;

                if (!connected)
                {
                    var neutral = NeutralInk.Background is SolidColorBrush brush ? brush.Color : default;
                    _lens.SetColors(neutral, neutral, animated);
                }
                else if (late)
                {
                    _lens.SetColors(ColorEx.FromHex(0xFFAA56A6), ColorEx.FromHex(0xFFEF476F), animated);
                }
                else if (cantSpeak)
                {
                    _lens.SetColors(ColorEx.FromHex(0xFF3252EF), ColorEx.FromHex(0xFFEF436C), animated);
                }
                else if (muted)
                {
                    _lens.SetColors(ColorEx.FromHex(0xFF007FFF), ColorEx.FromHex(0xFF00AFFE), animated);
                }
                else
                {
                    _lens.SetColors(ColorEx.FromHex(0xFF33C659), ColorEx.FromHex(0xFF00A0B9), animated);
                }
            }

            Audio.IsChecked = !muted;
            Audio.IsEnabled = !cantSpeak;
            Automation.SetToolTip(Audio, muted ? Strings.VoipGroupUnmute : Strings.VoipGroupMute);

            UpdateStatus(connected, scheduled, late, remaining);
        }

        private void UpdateStatus(bool connected, bool scheduled, bool late, int remaining)
        {
            string status = null;
            var ticking = false;

            if (!connected)
            {
                status = Strings.VoipConnecting;
            }
            else if (late)
            {
                status = string.Format("{0} {1}", Strings.VoipChatLateBy, Formatter.Duration(-remaining));
                ticking = true;
            }
            else if (scheduled)
            {
                status = string.Format("{0} {1}", Strings.VoipChatStartsIn, Formatter.Duration(remaining));
                ticking = true;
            }
            else if (_call is VoipCall privateCall)
            {
                // A video chat has no running duration to show: VoipGroupCall only knows it once
                // the call has ended.
                status = Formatter.Duration(privateCall.Duration);
                ticking = true;
            }

            StatusLabel.Text = status ?? string.Empty;
            StatusLabel.Visibility = Separator.Visibility = status != null
                ? Visibility.Visible
                : Visibility.Collapsed;

            AutomationProperties.SetName(Root, status != null
                ? string.Format("{0}, {1}", TitleLabel.Text, status)
                : TitleLabel.Text);

            if (ticking && IsConnected)
            {
                StartClock();
            }
            else
            {
                StopClock();
            }
        }

        private void StartClock()
        {
            if (_clock == null)
            {
                _clock = new DispatcherTimer();
                _clock.Interval = TimeSpan.FromSeconds(1);
                _clock.Tick += OnClockTick;
                _clock.Start();
            }
        }

        private void StopClock()
        {
            if (_clock != null)
            {
                _clock.Stop();
                _clock.Tick -= OnClockTick;
                _clock = null;
            }
        }

        // Also what turns a scheduled chat late when its start passes.
        private void OnClockTick(object sender, object e)
        {
            UpdateState(true);
        }

        private void WatchNeutralBrush()
        {
            _neutralBrush?.UnregisterColorChangedCallback(ref _neutralBrushToken);
            _neutralBrush = NeutralInk.Background as SolidColorBrush;
            _neutralBrush?.RegisterColorChangedCallback(OnNeutralColorChanged, ref _neutralBrushToken);
        }

        private void OnNeutralInkChanged(DependencyObject sender, DependencyProperty dp)
        {
            WatchNeutralBrush();
            UpdateState(true);
        }

        private void OnNeutralColorChanged(DependencyObject sender, DependencyProperty dp)
        {
            UpdateState(true);
        }

        private void OnStateChanged(VoipCall sender, VoipCallStateChangedEventArgs args)
        {
            this.BeginOnUIThread(() => UpdateState(true));
        }

        private void OnMediaStateChanged(VoipCall sender, VoipCallMediaStateChangedEventArgs args)
        {
            this.BeginOnUIThread(() => UpdateState(true));
        }

        private void OnNetworkStateChanged(VoipGroupCall sender, VoipGroupCallNetworkStateChangedEventArgs args)
        {
            this.BeginOnUIThread(() => UpdateState(true));
        }

        // Also raised when an admin mutes the current user, which has no MutedChanged of its own.
        private void OnGroupCallChanged(object sender, EventArgs e)
        {
            this.BeginOnUIThread(() =>
            {
                if (_call is VoipGroupCall groupCall)
                {
                    TitleLabel.Text = groupCall.GetTitle();
                    UpdateState(true);
                }
            });
        }

        private void OnAudioLevelUpdated(VoipCall sender, VoipCallAudioLevelUpdatedEventArgs args)
        {
            _lens?.UpdateLevel(args.AudioLevel);
        }

        private void OnAudioLevelsUpdated(VoipGroupCall sender, IList<VoipGroupParticipant> args)
        {
            var loudest = 0f;

            foreach (var level in args)
            {
                if (level.IsMuted)
                {
                    continue;
                }

                // The 1.5 cancels the lens's maxLevel, so your own voice can use its full range.
                loudest = MathF.Max(loudest, level.AudioSource == 0 ? level.Level * 1.5f : level.Level);
            }

            _lens?.UpdateLevel(loudest);
        }

        private void Root_Click(object sender, RoutedEventArgs e)
        {
            _call?.Show(_navigationService);
        }

        private void Audio_Click(object sender, RoutedEventArgs e)
        {
            if (_call is VoipCall privateCall)
            {
                privateCall.AudioState = privateCall.AudioState == VoipAudioState.Muted
                    ? VoipAudioState.Active
                    : VoipAudioState.Muted;
            }
            else if (_call is VoipGroupCall groupCall)
            {
                groupCall.IsMuted = !groupCall.IsMuted;
            }
        }
    }
}
