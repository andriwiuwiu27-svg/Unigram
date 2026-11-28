//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Native;
using Telegram.Streams;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls.Chats
{
    public partial class ChatSendButton : GlyphButton
    {
        private TextBlock ExpiresInLabel;

        private Grid StarsRoot;
        private AnimatedImage Stars;
        private ProgressRingIndeterminate RewriteRing;

        private bool _hasTextComposition;
        private bool _isRewriting;

        public ChatSendButton()
        {
            DefaultStyleKey = typeof(ChatSendButton);
        }

        protected override void OnApplyTemplate()
        {
            ExpiresInLabel = GetTemplateChild(nameof(ExpiresInLabel)) as TextBlock;

            StarsRoot = GetTemplateChild(nameof(StarsRoot)) as Grid;
            Stars = GetTemplateChild(nameof(Stars)) as AnimatedImage;
            RewriteRing = GetTemplateChild(nameof(RewriteRing)) as ProgressRingIndeterminate;

            OnSlowModeDelayChanged(SlowModeDelay, SlowModeDelayExpiresIn);
            OnReadOnlyChanged(IsReadOnly);
            UpdateStars();
            UpdateRewriteRing();

            base.OnApplyTemplate();
        }

        /// <summary>Whether the chat rewrites what is sent with its text composition settings, which the stars say.</summary>
        public bool HasTextComposition
        {
            get => _hasTextComposition;
            set
            {
                if (_hasTextComposition != value)
                {
                    _hasTextComposition = value;
                    UpdateStars();
                }
            }
        }

        /// <summary>Whether a rewrite is in flight, which the ring inside the button says.</summary>
        public bool IsRewriting
        {
            get => _isRewriting;
            set
            {
                if (_isRewriting != value)
                {
                    _isRewriting = value;
                    UpdateRewriteRing();
                }
            }
        }

        private void UpdateStars()
        {
            if (StarsRoot == null || Stars == null)
            {
                return;
            }

            if (_hasTextComposition && !IsReadOnly)
            {
                StarsRoot.Visibility = Visibility.Visible;
                Stars.Source ??= new ParticlesImageSource(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), ParticlesType.Premium);
            }
            else
            {
                // Without a source the image stops drawing frames, which collapsing it alone does not promise.
                StarsRoot.Visibility = Visibility.Collapsed;
                Stars.Source = null;
            }
        }

        private void UpdateRewriteRing()
        {
            RewriteRing?.Visibility = _isRewriting ? Visibility.Visible : Visibility.Collapsed;
        }

        #region SlowModeDelay

        public int SlowModeDelay
        {
            get => (int)GetValue(SlowModeDelayProperty);
            set => SetValue(SlowModeDelayProperty, value);
        }

        public static readonly DependencyProperty SlowModeDelayProperty =
            DependencyProperty.Register("SlowModeDelay", typeof(int), typeof(ChatSendButton), new PropertyMetadata(0, OnSlowModeDelayChanged));

        private static void OnSlowModeDelayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ChatSendButton)d).OnSlowModeDelayChanged((int)e.NewValue, ((ChatSendButton)d).SlowModeDelayExpiresIn);
        }

        #endregion

        #region SlowModeDelayExpiresIn

        public double SlowModeDelayExpiresIn
        {
            get => (double)GetValue(SlowModeDelayExpiresInProperty);
            set => SetValue(SlowModeDelayExpiresInProperty, value);
        }

        public static readonly DependencyProperty SlowModeDelayExpiresInProperty =
            DependencyProperty.Register("SlowModeDelayExpiresIn", typeof(double), typeof(ChatSendButton), new PropertyMetadata(0d, OnSlowModeDelayExpiresInChanged));

        private static void OnSlowModeDelayExpiresInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ChatSendButton)d).OnSlowModeDelayChanged(((ChatSendButton)d).SlowModeDelay, (double)e.NewValue);
        }

        #endregion

        private void OnSlowModeDelayChanged(int delay, double expiresIn)
        {
            if (ExpiresInLabel == null)
            {
                return;
            }

            ExpiresInLabel.Text = TimeSpan.FromSeconds(expiresIn).ToString("mm\\:ss");
            VisualStateManager.GoToState(this, expiresIn > 0 ? "ExpiresIn" : "Expired", false);
        }

        #region IsReadOnly

        public bool IsReadOnly
        {
            get { return (bool)GetValue(IsReadOnlyProperty); }
            set { SetValue(IsReadOnlyProperty, value); }
        }

        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(ChatSendButton), new PropertyMetadata(false, OnReadOnlyChanged));

        private static void OnReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ChatSendButton)d).OnReadOnlyChanged((bool)e.NewValue);
        }

        private void OnReadOnlyChanged(bool newValue)
        {
            VisualStateManager.GoToState(this, newValue ? "ReadOnly" : "NotReadOnly", false);
            UpdateStars();
        }

        #endregion
    }
}
