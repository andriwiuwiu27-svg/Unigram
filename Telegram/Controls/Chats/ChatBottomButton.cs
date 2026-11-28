//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Numerics;
using Telegram.Common;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;

namespace Telegram.Controls.Chats
{
    public enum ChatBottomButtonAppearance
    {
        Default,
        Accent,
        AccentText,
        AccentWash
    }

    /// <summary>
    /// This button is intended to be placed over the BubbleTextBox
    /// </summary>
    public partial class ChatBottomButton : Button
    {
        private ContentPresenter ContentPresenter1;
        private ContentPresenter ContentPresenter2;

        private Visual _visual1;
        private Visual _visual2;

        private ContentPresenter _label;
        private Visual _visual;

        private Border AccentBackground;

        private ChatBottomButtonAppearance _appearance;
        private double _accentRadius = 20;

        public ChatBottomButton()
        {
            DefaultStyleKey = typeof(ChatBottomButton);
        }

        /// <summary>
        /// The radius of the accent fill, which is inset from the button's edges and so cannot share
        /// its CornerRadius: that one also shapes the focus rectangle.
        /// </summary>
        public double AccentRadius
        {
            get => _accentRadius;
            set
            {
                _accentRadius = value;

                if (AccentBackground != null)
                {
                    AccentBackground.CornerRadius = new CornerRadius(value);
                }
            }
        }

        public void SetAppearance(ChatBottomButtonAppearance appearance, bool animated)
        {
            if (_appearance != appearance)
            {
                _appearance = appearance;
                VisualStateManager.GoToState(this, GetStateName(appearance), animated);
            }
        }

        // Not ToString, which needs reflection metadata the release build does not promise to keep.
        private static string GetStateName(ChatBottomButtonAppearance appearance)
        {
            return appearance switch
            {
                ChatBottomButtonAppearance.Accent => "Accent",
                ChatBottomButtonAppearance.AccentText => "AccentText",
                ChatBottomButtonAppearance.AccentWash => "AccentWash",
                _ => "Default"
            };
        }

        protected override void OnApplyTemplate()
        {
            ContentPresenter1 = GetTemplateChild(nameof(ContentPresenter1)) as ContentPresenter;
            ContentPresenter2 = GetTemplateChild(nameof(ContentPresenter2)) as ContentPresenter;
            AccentBackground = GetTemplateChild(nameof(AccentBackground)) as Border;

            if (AccentBackground != null)
            {
                AccentBackground.CornerRadius = new CornerRadius(_accentRadius);
            }

            VisualStateManager.GoToState(this, GetStateName(_appearance), false);

            _label = ContentPresenter1;

            if (ContentPresenter1 != null && ContentPresenter2 != null)
            {
                _visual1 = _visual = ElementComposition.GetElementVisual(ContentPresenter1);
                _visual2 = ElementComposition.GetElementVisual(ContentPresenter2);

                ContentPresenter2.Content = new object();

                _visual2.Opacity = 0;
                _visual2.Scale = new Vector3();
                _visual2.CenterPoint = new Vector3(10);

                ContentPresenter1.Content = Content ?? new object();

                _visual1.Opacity = 1;
                _visual1.Scale = new Vector3(1);
                _visual1.CenterPoint = new Vector3(10);
            }

            base.OnApplyTemplate();
        }

        protected override async void OnContentChanged(object oldContent, object newContent)
        {
            if (_visual == null || _label == null)
            {
                return;
            }

            var visualShow = _visual == _visual1 ? _visual2 : _visual1;
            var visualHide = _visual == _visual1 ? _visual1 : _visual2;

            var labelShow = _visual == _visual1 ? ContentPresenter2 : ContentPresenter1;
            var labelHide = _visual == _visual1 ? ContentPresenter1 : ContentPresenter2;

            labelShow.Content = newContent ?? new object();

            await this.UpdateLayoutAsync();

            _visual1.CenterPoint = new Vector3(ContentPresenter1.ActualSize / 2f, 0);
            _visual2.CenterPoint = new Vector3(ContentPresenter2.ActualSize / 2f, 0);

            var hide1 = _visual.Compositor.CreateVector3KeyFrameAnimation();
            hide1.InsertKeyFrame(0, new Vector3(1));
            hide1.InsertKeyFrame(1, new Vector3(0));

            var hide2 = _visual.Compositor.CreateScalarKeyFrameAnimation();
            hide2.InsertKeyFrame(0, 1);
            hide2.InsertKeyFrame(1, 0);

            visualHide.StartAnimation("Scale", hide1);
            visualHide.StartAnimation("Opacity", hide2);

            var show1 = _visual.Compositor.CreateVector3KeyFrameAnimation();
            show1.InsertKeyFrame(1, new Vector3(1));
            show1.InsertKeyFrame(0, new Vector3(0));

            var show2 = _visual.Compositor.CreateScalarKeyFrameAnimation();
            show2.InsertKeyFrame(1, 1);
            show2.InsertKeyFrame(0, 0);

            visualShow.StartAnimation("Scale", show1);
            visualShow.StartAnimation("Opacity", show2);

            _visual = visualShow;
            _label = labelShow;
        }
    }
}
