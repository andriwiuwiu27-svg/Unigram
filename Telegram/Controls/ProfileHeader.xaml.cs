//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls.Cells;
using Telegram.Controls.Gallery;
using Telegram.Controls.Media;
using Telegram.Converters;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Services.Updates;
using Telegram.Streams;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.Views.Popups;
using Telegram.Views.Wallet.Popups;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Controls
{
    public sealed partial class ProfileHeader : UserControl
    {
        public ProfileViewModel ViewModel => DataContext as ProfileViewModel;

        public ProfileHeader()
        {
            InitializeComponent();
            DescriptionLabel.AddHandler(ContextRequestedEvent, new ContextRequestedHandler(About_ContextRequested), true);

            //HeaderRoot.CreateInsetClip();

            ActualThemeChanged += OnActualThemeChanged;
            SizeChanged += OnSizeChanged;

            Properties = BootStrapper.Current.Compositor.CreatePropertySet();
            Properties.InsertScalar("HeaderActualHeight", HeaderRoot.ActualSize.Y);
            Properties.InsertScalar("ActualHeight", ActualSize.Y - 48 + 16);
            Properties.InsertScalar("RemovedHeight", 0);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            Properties.InsertScalar("HeaderActualHeight", HeaderRoot.ActualSize.Y);
            Properties.InsertScalar("ActualHeight", ActualSize.Y - 48 + 16);

            if (ViewModel.IsSavedMessages)
            {
                Properties.InsertScalar("RemovedHeight", HeaderRoot.ActualSize.Y - 48);
                HeaderRoot.Margin = new Thickness(0, -HeaderRoot.ActualHeight + 48, 0, 0);
            }
        }

        public void AnimateEntrance()
        {
            try
            {
                ConnectedAnimationServiceEx.TryStart("Photo", HeaderPhoto, new BasicConnectedAnimationConfiguration());
                ConnectedAnimationServiceEx.TryStart("Title", TitleRoot, new BasicConnectedAnimationConfiguration());
                ConnectedAnimationServiceEx.TryStart("Subtitle", SubtitleRoot, new BasicConnectedAnimationConfiguration());
            }
            catch
            {
                //
            }
        }

        public void PrepareExit()
        {
            try
            {
                ConnectedAnimationServiceEx.PrepareToAnimate("Photo", HeaderPhoto);
                ConnectedAnimationServiceEx.PrepareToAnimate("Title", TitleRoot);
                ConnectedAnimationServiceEx.PrepareToAnimate("Subtitle", SubtitleRoot);
            }
            catch
            {
                //
            }
        }

        public CompositionPropertySet Properties { get; }

        public double OccludedHeight => ViewModel.IsSavedMessages ? 0 : HeaderRoot.ActualHeight - 48;

        public float HeaderHeight => HeaderRoot.ActualSize.Y;

        public ElementTheme HeaderTheme => HeaderRoot.RequestedTheme;

        private void OnActualThemeChanged(FrameworkElement sender, object args)
        {
            if (_actualTheme == sender.ActualTheme || _actualTheme == ElementTheme.Default)
            {
                return;
            }

            TitleRoot.RequestedTheme = !_backgroundCollapsed ? sender.ActualTheme : HeaderTheme;
            SubtitleRoot.RequestedTheme = !_backgroundCollapsed ? sender.ActualTheme : HeaderTheme;

            UpdateChatAccentColors(ViewModel.Chat);
        }

        private void Segments_Click(object sender, RoutedEventArgs e)
        {
            var chat = ViewModel.Chat;
            if (chat == null || sender is not ActiveStoriesSegments segments)
            {
                return;
            }

            if (segments.HasActiveStories)
            {
                segments.Open(ViewModel.NavigationService, ViewModel.ClientService, chat, 140, story =>
                {
                    var transform = Segments.TransformToVisual(null);
                    var point = transform.TransformPoint(new Point());

                    return new Rect(point.X + 4, point.Y + 4, 132, 132);
                });
            }
            else
            {
                OpenPhoto();
            }
        }

        private void Segments_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var flyout = new MenuFlyout();
            flyout.CreateFlyoutItem(OpenPhoto, Strings.OpenPhoto, Icons.Image);
            flyout.ShowAt(sender, args);
        }

        private void OpenPhoto()
        {
            GalleryWindow.ShowAsync(ViewModel, ViewModel.StorageService, ViewModel.Chat, Photo);
        }

        private float _verticalOffset;

        public void ViewChanged(ScrollViewer scrollingHost, float verticalOffset)
        {
            _verticalOffset = verticalOffset;
            Pattern.TransitionFraction = verticalOffset / (32 + 140 + 384);
            GiftsCover.TransitionFraction = verticalOffset / (32 + 140 + 96);

            ShowHideBackground(verticalOffset >= HeaderRoot.ActualHeight - 48);
            ShowHideSubtitle(verticalOffset >= ActualHeight - 48);
        }

        private bool _subtitleCollapsed = true;

        private void ShowHideSubtitle(bool show)
        {
            if (_subtitleCollapsed != show)
            {
                return;
            }

            _subtitleCollapsed = !show;
            SubtitleTab.Visibility = Visibility.Visible;

            HeaderRoot.IsHitTestVisible = !show;

            var subtitleTab = ElementComposition.GetElementVisual(SubtitleTab);
            var subtitlePro = ElementComposition.GetElementVisual(SubtitleMain);

            var opacityIn = subtitlePro.Compositor.CreateScalarKeyFrameAnimation();
            opacityIn.InsertKeyFrame(0, show ? 0 : 1);
            opacityIn.InsertKeyFrame(1, show ? 1 : 0);

            var opacityOut = subtitlePro.Compositor.CreateScalarKeyFrameAnimation();
            opacityOut.InsertKeyFrame(0, show ? 1 : 0);
            opacityOut.InsertKeyFrame(1, show ? 0 : 1);

            subtitleTab.StartAnimation("Opacity", opacityIn);
            subtitlePro.StartAnimation("Opacity", opacityOut);
        }

        private bool _backgroundCollapsed = true;

        private void ShowHideBackground(bool show)
        {
            if (_backgroundCollapsed != show)
            {
                return;
            }

            _backgroundCollapsed = !show;
            HeaderPhotoRoot.IsHitTestVisible = !show;
            Buttons.IsHitTestVisible = !show;
            FooterRoot.IsHitTestVisible = !show;

            if (HeaderTheme != ElementTheme.Default)
            {
                UpdateTitleForeground(!show);

                TitleRoot.RequestedTheme = show ? ActualTheme : HeaderTheme;
                SubtitleRoot.RequestedTheme = show ? ActualTheme : HeaderTheme;
            }

            var headerBackground = ElementComposition.GetElementVisual(HeaderBackground);
            var headerGlow = ElementComposition.GetElementVisual(HeaderGlow);

            var opacityOut = headerBackground.Compositor.CreateScalarKeyFrameAnimation();
            opacityOut.InsertKeyFrame(0, show ? 1 : 0);
            opacityOut.InsertKeyFrame(1, show ? 0 : 1);

            headerBackground.StartAnimation("Opacity", opacityOut);
            headerGlow.StartAnimation("Opacity", opacityOut);
        }

        private SolidColorBrush _titleForeground;
        private SolidColorBrush _ratingStroke;

        // White over a coloured header, the theme's own once the background has faded out.
        private void UpdateTitleForeground(bool white)
        {
            if (white)
            {
                _titleForeground ??= new SolidColorBrush(Colors.White);
                _ratingStroke ??= new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));

                Identity.Foreground = _titleForeground;
                BotVerified.ReplacementColor = _titleForeground;
                Rating.Fill = _titleForeground;
                Rating.Stroke = _ratingStroke;
            }
            else
            {
                Identity.ClearValue(ForegroundProperty);
                BotVerified.ClearValue(AnimatedImage.ReplacementColorProperty);
                Rating.ClearValue(ProfileRating.FillProperty);
                Rating.ClearValue(ProfileRating.StrokeProperty);
            }
        }

        public void InitializeScrolling(CompositionPropertySet properties)
        {
            var target = ElementComposition.GetElementVisual(this);
            var controls = ElementComposition.GetElementVisual(ControlsRoot);
            var background = ElementComposition.GetElementVisual(ClipperBackground);
            var title = ElementComposition.GetElementVisual(TitleRoot);
            var subtitle = ElementComposition.GetElementVisual(SubtitleRoot);
            var buttons = ElementComposition.GetElementVisual(Buttons);
            var root = ElementComposition.GetElementVisual(HeaderRoot);
            var photoRoot = ElementComposition.GetElementVisual(HeaderPhotoRoot);
            var photo = ElementComposition.GetElementVisual(HeaderPhoto);
            var audio = ElementComposition.GetElementVisual(FooterRoot);

            ElementCompositionPreview.SetIsTranslationEnabled(Buttons, true);
            ElementCompositionPreview.SetIsTranslationEnabled(HeaderRoot, true);
            ElementCompositionPreview.SetIsTranslationEnabled(ClipperBackground, true);
            ElementCompositionPreview.SetIsTranslationEnabled(TitleRoot, true);
            ElementCompositionPreview.SetIsTranslationEnabled(SubtitleRoot, true);
            ElementCompositionPreview.SetIsTranslationEnabled(HeaderPhotoRoot, true);

            var translationExp = "(scrollViewer.Translation.Y - _.RemovedHeight)";

            //var rootExp = "clamp(-scrollViewer.Translation.Y - (this.Target.Size.Y - 48 + 0), 0, target.Size.Y - this.Target.Size.Y)";
            var rootExp = $"{translationExp} < 0 ? clamp(-{translationExp} - (this.Target.Size.Y - 48), 0, 2147483647) : -{translationExp}";
            var rootTranslation = root.Compositor.CreateExpressionAnimation(rootExp);
            rootTranslation.SetReferenceParameter("scrollViewer", properties);
            rootTranslation.SetReferenceParameter("target", target);
            rootTranslation.SetReferenceParameter("_", Properties);

            var photoExp = $"clamp(1 - -{translationExp} / root.Size.Y, 0, 1)";
            var photoScale = root.Compositor.CreateExpressionAnimation($"vector3({photoExp}, {photoExp}, 1)");
            photoScale.SetReferenceParameter("scrollViewer", properties);
            photoScale.SetReferenceParameter("root", root);
            photoScale.SetReferenceParameter("_", Properties);

            var photoTranslation = root.Compositor.CreateExpressionAnimation($"clamp(-{translationExp} * 0.2, 0, 140)");
            photoTranslation.SetReferenceParameter("scrollViewer", properties);
            photoTranslation.SetReferenceParameter("root", root);
            photoTranslation.SetReferenceParameter("_", Properties);

            //var rootExp = "clamp(-scrollViewer.Translation.Y - (this.Target.Size.Y - 48 + 0), 0, target.Size.Y - this.Target.Size.Y)";
            var controlsExp = $"-{translationExp} - (target.Size.Y - 40)";
            var controlsClip = root.Compositor.CreateExpressionAnimation(controlsExp);
            controlsClip.SetReferenceParameter("scrollViewer", properties);
            controlsClip.SetReferenceParameter("target", root);
            controlsClip.SetReferenceParameter("_", Properties);

            //var buttonsExp = "clamp(-scrollViewer.Translation.Y - (target.Size.Y - this.Target.Size.Y - 56), 0, 72)";
            var buttonsExp = $"clamp(-{translationExp} - (target.Size.Y - this.Target.Size.Y - 56 - audio.Size.Y), 0, 72)";
            var buttonsTranslation = root.Compositor.CreateExpressionAnimation(buttonsExp);
            buttonsTranslation.SetReferenceParameter("scrollViewer", properties);
            buttonsTranslation.SetReferenceParameter("target", root);
            buttonsTranslation.SetReferenceParameter("audio", audio);
            buttonsTranslation.SetReferenceParameter("_", Properties);

            var buttonsOpacity = root.Compositor.CreateExpressionAnimation($"clamp(1 - {buttonsExp} / this.Target.Size.Y, 0, 1)");
            buttonsOpacity.SetReferenceParameter("scrollViewer", properties);
            buttonsOpacity.SetReferenceParameter("target", root);
            buttonsOpacity.SetReferenceParameter("audio", audio);
            buttonsOpacity.SetReferenceParameter("_", Properties);

            var audioExp = $"clamp(-{translationExp} - (target.Size.Y - buttons.Size.Y - 72 - this.Target.Size.Y), 0, 72)";
            var audioOpacity = root.Compositor.CreateExpressionAnimation($"clamp(1 - {audioExp} / this.Target.Size.Y, 0, 1)");
            audioOpacity.SetReferenceParameter("scrollViewer", properties);
            audioOpacity.SetReferenceParameter("target", root);
            audioOpacity.SetReferenceParameter("buttons", buttons);
            audioOpacity.SetReferenceParameter("_", Properties);

            //var titleExp = "clamp(-scrollViewer.Translation.Y - 168 - 8, 0, 86)";
            var titleExp = $"clamp(-{translationExp} - 182, 0, (buttons.Size.Y > 0 ? 86 : 11) + audio.Size.Y)";
            var titleTranslation = root.Compositor.CreateExpressionAnimation(titleExp);
            titleTranslation.SetReferenceParameter("scrollViewer", properties);
            titleTranslation.SetReferenceParameter("buttons", buttons);
            titleTranslation.SetReferenceParameter("audio", audio);
            titleTranslation.SetReferenceParameter("_", Properties);

            //var titleScaleExp = "max(diff, 1 - clamp((-scrollViewer.Translation.Y - 184) / 32, 0, 1) * diff)";
            var titleScaleExp = $"clamp(1 - ((-{translationExp} - 124) / 68) * 0.3, 0.7, 1)";
            var titleScale = root.Compositor.CreateExpressionAnimation($"vector3({titleScaleExp}, {titleScaleExp}, 1)");
            titleScale.SetReferenceParameter("scrollViewer", properties);
            titleScale.SetReferenceParameter("_", Properties);

            //var subtitleScaleExp = "max(diff, 1 - clamp((-scrollViewer.Translation.Y - 184) / 32, 0, 1) * diff)";
            var subtitleScaleExp = $"clamp(1 - ((-{translationExp} - 124) / 68) * 0.143, 0.857, 1)";
            var subtitleScale = root.Compositor.CreateExpressionAnimation($"vector3({subtitleScaleExp}, {subtitleScaleExp}, 1)");
            subtitleScale.SetReferenceParameter("scrollViewer", properties);
            subtitleScale.SetReferenceParameter("_", Properties);

            // TODO: too tall
            if (ViewModel.IsSavedMessages)
            {
                ClipperBackground.Margin = new Thickness(0, -48, 0, -8);

                var clipperTranslation = root.Compositor.CreateExpressionAnimation($"-scrollViewer.Translation.Y + 32");
                clipperTranslation.SetReferenceParameter("scrollViewer", properties);

                background.StartAnimation("Translation.Y", clipperTranslation);
            }
            else
            {
                var clipperExpBranch1 = $"-{translationExp} - ((root.Size.Y - 80))";
                var clipperExpBranch2 = $"-{translationExp} - (root.Size.Y - 48)";
                var clipperExpDiff = $"{clipperExpBranch2} + -((target.Size.Y - 48) - -{translationExp}) / 64 * 256";
                var clipperExpClamp = $"min(target.Size.Y - root.Size.Y + 56, {clipperExpDiff})";
                var clipperTranslation = root.Compositor.CreateExpressionAnimation($"{translationExp} < 0 ? -{translationExp} > root.Size.Y - 48 && -{translationExp} < target.Size.Y - 48 ? {clipperExpBranch2} : -{translationExp} < target.Size.Y - 48 ? 0 : -{translationExp} < target.Size.Y - 24 ? {clipperExpClamp} : {clipperExpBranch1} : -{translationExp}");
                clipperTranslation.SetReferenceParameter("scrollViewer", properties);
                clipperTranslation.SetReferenceParameter("_", Properties);
                clipperTranslation.SetReferenceParameter("root", root);
                clipperTranslation.SetReferenceParameter("target", target);

                background.StartAnimation("Translation.Y", clipperTranslation);
            }

            controls.Clip = properties.Compositor.CreateInsetClip();
            controls.Clip.StartAnimation("TopInset", controlsClip);
            root.StartAnimation("Translation.Y", rootTranslation);
            buttons.StartAnimation("Translation.Y", buttonsTranslation);
            buttons.StartAnimation("Opacity", buttonsOpacity);
            audio.StartAnimation("Opacity", audioOpacity);
            title.StartAnimation("Translation.Y", titleTranslation);
            title.StartAnimation("Scale", titleScale);
            subtitle.StartAnimation("Translation.Y", titleTranslation);
            subtitle.StartAnimation("Scale", subtitleScale);
            photo.StartAnimation("Scale", photoScale);
            photoRoot.StartAnimation("Translation.Y", photoTranslation);
            photo.CenterPoint = new Vector3(70, 140, 0);
        }

        #region Delegate

        public void UpdateChatGifts(Chat chat)
        {
            GiftsCover.TransitionFraction = _verticalOffset / (32 + 140 + 96);
        }

        public void UpdateChatAccentColors(Chat chat)
        {
            _actualTheme = ViewModel.NavigationService.Window.ActualTheme;

            var upgradedGift = chat.EmojiStatus?.Type as EmojiStatusTypeUpgradedGift;
            var patternId = upgradedGift?.SymbolCustomEmojiId ?? chat.ProfileBackgroundCustomEmojiId;

            if (chat.ProfileAccentColorId != -1 || upgradedGift != null)
            {
                ProfileColors colors;
                if (upgradedGift != null)
                {
                    colors = new ProfileColors(new ProfileAccentColors(Array.Empty<int>(), new[] { upgradedGift.BackdropColors.EdgeColor, upgradedGift.BackdropColors.CenterColor }, Array.Empty<int>()));
                }
                else if (ViewModel.ClientService.TryGetProfileColor(chat.ProfileAccentColorId, out ProfileColor color))
                {
                    colors = color.ForTheme(_actualTheme);
                }
                else
                {
                    return;
                }

                UpdateTitleForeground(true);

                HeaderRoot.RequestedTheme = ElementTheme.Dark;

                if (colors.BackgroundColors.Count > 1)
                {
                    var gradient = new LinearGradientBrush();
                    gradient.StartPoint = new Point(0, 0);
                    gradient.EndPoint = new Point(0, 1);
                    gradient.GradientStops.Add(new GradientStop
                    {
                        Color = colors.BackgroundColors[1],
                        Offset = 0
                    });

                    gradient.GradientStops.Add(new GradientStop
                    {
                        Color = colors.BackgroundColors[0],
                        Offset = 1
                    });

                    HeaderBackground.Background = gradient;
                }
                else
                {
                    HeaderBackground.Background = new SolidColorBrush(colors.BackgroundColors[0]);
                }

                UpdateProfileBackgroundCustomEmoji(colors, patternId != 0);
                UpdateIcons(chat, true);
            }
            else
            {
                UpdateTitleForeground(false);

                HeaderBackground.ClearValue(Panel.BackgroundProperty);
                HeaderRoot.RequestedTheme = ElementTheme.Default;

                UpdateProfileBackgroundCustomEmoji(null, patternId != 0);
                UpdateIcons(chat, false);
            }

            Pattern.Source = patternId != 0
                ? new CustomEmojiFileSource(ViewModel.ClientService, patternId)
                : null;
        }

        // One per UI thread: each window's thread has its own compositor.
        [ThreadStatic]
        private static CompositionEffectFactory _glowFactory;

        private void UpdateProfileBackgroundCustomEmoji(ProfileColors colors, bool pattern)
        {
            if (!_glowBackground)
            {
                _glowBackground = true;

                var radial2 = new RadialGradientBrush();
                radial2.Center = new Point(0.5f, 0.3f);
                radial2.RadiusX = 0.4;
                radial2.RadiusY = 0.6;
                radial2.GradientStops.Add(new GradientStop { Color = Color.FromArgb(50, 255, 255, 255) });
                radial2.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 1 });

                HeaderGlow.Background = radial2;
            }

            // The glow is masked by the pattern, so with no pattern there is nothing to draw, and a
            // visual surface left behind would still be rendered on every frame.
            if (!pattern)
            {
                if (_glowVisual)
                {
                    _glowVisual = false;
                    ElementCompositionPreview.SetElementChildVisual(HeaderGlow, null);
                }

                return;
            }

            var compositor = BootStrapper.Current.Compositor;

            // Create a VisualSurface positioned at the same location as this control and feed that
            // through the color effect.
            var surfaceBrush = compositor.CreateSurfaceBrush();
            surfaceBrush.Stretch = CompositionStretch.None;
            var surface = compositor.CreateVisualSurface();

            // Select the source visual and the offset/size of this control in that element's space.
            surface.SourceVisual = ElementComposition.GetElementVisual(Pattern);
            surface.SourceOffset = new Vector2(0, 0);
            surface.SourceSize = new Vector2(1000, 320);
            surfaceBrush.Surface = surface;
            surfaceBrush.Stretch = CompositionStretch.None;

            CompositionBrush brush;
            if (colors == null)
            {
                brush = compositor.CreateColorBrush(_actualTheme == ElementTheme.Light
                    ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF));
            }
            else if (colors.BackgroundColors.Count > 1)
            {
                var linear = compositor.CreateLinearGradientBrush();
                linear.StartPoint = new Vector2();
                linear.EndPoint = new Vector2(0, 1);
                linear.ColorStops.Add(compositor.CreateColorGradientStop(0, colors.BackgroundColors[1]));
                linear.ColorStops.Add(compositor.CreateColorGradientStop(1, colors.BackgroundColors[0]));

                brush = linear;
            }
            else
            {
                brush = compositor.CreateColorBrush(colors.BackgroundColors[0]);
            }

            var radial = compositor.CreateRadialGradientBrush();
            //radial.CenterPoint = new Vector2(0.5f, 0.0f);
            radial.EllipseCenter = new Vector2(0.5f, 0.3f);
            radial.EllipseRadius = new Vector2(0.4f, 0.6f);
            radial.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(200, 0, 0, 0)));
            radial.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, 0, 0, 0)));

            if (_glowFactory?.Compositor != compositor)
            {
                var blend = new BlendEffect
                {
                    Background = new CompositionEffectSourceParameter("Background"),
                    Foreground = new CompositionEffectSourceParameter("Foreground"),
                    Mode = BlendEffectMode.SoftLight
                };

                _glowFactory = compositor.CreateEffectFactory(blend);
            }

            var borderEffectBrush = _glowFactory.CreateBrush();
            borderEffectBrush.SetSourceParameter("Foreground", brush);
            borderEffectBrush.SetSourceParameter("Background", radial); // compositor.CreateColorBrush(Color.FromArgb(80, 0x00, 0x00, 0x00)));

            CompositionMaskBrush maskBrush = compositor.CreateMaskBrush();
            maskBrush.Source = borderEffectBrush; // Set source to content that is to be masked 
            maskBrush.Mask = surfaceBrush; // Set mask to content that is the opacity mask 

            var visual = compositor.CreateSpriteVisual();
            visual.Size = new Vector2(1000, 320);
            visual.Offset = new Vector3(0, 0, 0);
            visual.Brush = maskBrush;

            ElementCompositionPreview.SetElementChildVisual(HeaderGlow, visual);
            _glowVisual = true;
        }

        private bool _glowBackground;
        private bool _glowVisual;

        private ElementTheme _actualTheme = ElementTheme.Default;
        private bool _filledIcons = true;

        private void UpdateIcons(Chat chat, bool filled)
        {
            if (_filledIcons == filled)
            {
                return;
            }

            _filledIcons = filled;

            if (filled)
            {
                OpenChat.Glyph = Icons.ChatEmptyFilled;
                Call.Glyph = Icons.CallFilled;
                VideoChat.Glyph = Icons.VideoChatFilled;
                VideoCall.Glyph = Icons.VideoFilled;
                Search.Glyph = Icons.SearchFilled;
                Edit.Glyph = Icons.EditFilled;
                Join.Glyph = Icons.ArrowEnterFilled;
                Leave.Glyph = Icons.ArrowExitFilled;
                Menu.Glyph = Icons.MoreHorizontalFilled;
            }
            else
            {
                OpenChat.Glyph = Icons.ChatEmpty;
                Call.Glyph = Icons.Call;
                VideoChat.Glyph = Icons.VideoChat;
                VideoCall.Glyph = Icons.Video;
                Search.Glyph = Icons.Search;
                Edit.Glyph = Icons.Edit;
                Join.Glyph = Icons.ArrowEnter;
                Leave.Glyph = Icons.ArrowExit;
                Menu.Glyph = Icons.MoreHorizontal;
            }

            UpdateChatNotificationSettings(chat);
        }

        public void UpdateChat(Chat chat)
        {
            if (ViewModel.ClientService.IsSavedMessages(chat))
            {
                if (ViewModel.MyProfile)
                {
                    Buttons.Visibility = Visibility.Collapsed;
                }
                else
                {
                    Buttons.Visibility = Visibility.Collapsed;
                    ControlsRoot.Visibility = Visibility.Collapsed;
                    //Visibility = Visibility.Collapsed;
                    //return;
                }
            }

            UpdateChatTitle(chat);
            UpdateChatPhoto(chat);
            UpdateChatEmojiStatus(chat);
            UpdateChatAccentColors(chat);

            UpdateChatActiveStories(chat);

            UpdateChatNotificationSettings(chat);
            UpdateChatActionBar(chat);

            if (AppSettings.Diagnostics.ShowIds)
            {
                if (ChatId == null)
                {
                    FindName(nameof(ChatId));
                }

                ChatId.Visibility = Visibility.Visible;

                if (chat.Type is ChatTypePrivate privata)
                {
                    ChatId.Content = privata.UserId;
                }
                else
                {
                    ChatId.Content = chat.Id;
                }
            }
            else
            {
                ChatId?.Visibility = Visibility.Collapsed;
            }

            // Both are shown by a binding on the view model's value, which is only ever set before
            // the profile is first updated.
            if (DeleteReaction == null && ViewModel.DeleteReactions != null)
            {
                FindName(nameof(DeleteReaction));
            }

            if (BanAndReport == null && ViewModel.ReportReactions != null)
            {
                FindName(nameof(BanAndReport));
            }
        }

        public void UpdateChatTitle(Chat chat)
        {
            if (chat.Id == ViewModel.LinkedChatId)
            {
                PersonalChannel?.UpdateChatTitle(chat);
            }
            else if (ViewModel.ForumTopic != null)
            {
                Title.Text = ViewModel.ForumTopic.Info.Name;
            }
            else if (ViewModel.SavedMessagesTopic != null)
            {
                Title.Text = ViewModel.ClientService.GetTitle(ViewModel.SavedMessagesTopic);
            }
            else if (chat.Id == ViewModel.ClientService.Options.MyId && !ViewModel.IsSavedMessages)
            {
                Title.Text = chat.Title;
            }
            else
            {
                Title.Text = ViewModel.ClientService.GetTitle(chat);
            }
        }

        public void UpdateChatPhoto(Chat chat)
        {
            if (chat.Id == ViewModel.LinkedChatId)
            {
                PersonalChannel?.UpdateChatPhoto(chat);
            }
            else if (ViewModel.ForumTopic != null)
            {
                if (ViewModel.ForumTopic.Info.Icon.CustomEmojiId != 0)
                {
                    if (Icon == null)
                    {
                        FindName(nameof(Icon));
                    }

                    Icon.Source = new CustomEmojiFileSource(ViewModel.ClientService, ViewModel.ForumTopic.Info.Icon.CustomEmojiId);
                    TopicIconRoot?.Visibility = Visibility.Collapsed;
                    TopicIconGeneral?.Visibility = Visibility.Collapsed;
                }
                else if (ViewModel.ForumTopic.Info.IsGeneral)
                {
                    if (TopicIconGeneral == null)
                    {
                        FindName(nameof(TopicIconGeneral));
                    }

                    Icon?.Source = null;
                    TopicIconRoot?.Visibility = Visibility.Collapsed;
                    TopicIconGeneral.Visibility = Visibility.Visible;
                }
                else
                {
                    if (TopicIconRoot == null)
                    {
                        FindName(nameof(TopicIconRoot));
                    }

                    Icon?.Source = null;
                    TopicIconRoot.Visibility = Visibility.Visible;
                    TopicIconGeneral?.Visibility = Visibility.Collapsed;

                    var brush = ForumTopicCell.GetIconGradient(ViewModel.ForumTopic.Info.Icon);

                    TopicIconPath.Fill = brush;
                    TopicIconPath.Stroke = new SolidColorBrush(brush.GradientStops[1].Color);
                    TopicIconText.Text = InitialNameStringConverter.Convert(ViewModel.ForumTopic.Info.Name);
                }
            }
            else
            {
                Icon?.Source = null;

                if (chat.Id == ViewModel.ClientService.Options.MyId && !ViewModel.IsSavedMessages && ViewModel.ClientService.TryGetUser(chat, out User user))
                {
                    Photo.Source = ProfilePictureSource.User(ViewModel.ClientService, user);
                }
                else
                {
                    Photo.Source = ProfilePictureSource.Chat(ViewModel.ClientService, chat);
                }

                PhotoBorder.CornerRadius = new CornerRadius(Photo.ComputedShape == ProfilePictureShape.Superellipse ? 35 : 70);
            }
        }

        public void UpdateChatLastMessage(Chat chat)
        {
            if (chat.Id == ViewModel.LinkedChatId)
            {
                PersonalChannel?.UpdateChatLastMessage(chat, updateChatLists: false);
            }
        }

        public void UpdateChatEmojiStatus(Chat chat)
        {
            if (!ViewModel.IsSavedMessages && ViewModel.ClientService.TryGetUser(chat, out User user))
            {
                Identity.SetStatus(ViewModel.ClientService, user, BotVerified);
            }
            else
            {
                Identity.SetStatus(ViewModel.ClientService, chat, BotVerified);
            }

            // The gift worn as the status leaves the cover.
            GiftsCover.TransitionFraction = _verticalOffset / (32 + 140 + 96);
        }

        public void UpdateChatActiveStories(Chat chat)
        {
            if (ViewModel.Topic == null)
            {
                Segments.SetChat(ViewModel.ClientService, chat, 140);
            }
            else
            {
                Segments.Clear();
            }
        }

        public void UpdateChatNotificationSettings(Chat chat)
        {
            var muted = ViewModel.ClientService.Notifications.IsMuted(chat);
            Notifications.Content = muted ? Strings.ChatsUnmute : Strings.ChatsMute;
            Notifications.Glyph = muted
                ? (_filledIcons ? Icons.AlertOffFilled : Icons.AlertOff)
                : (_filledIcons ? Icons.AlertFilled : Icons.Alert);
        }

        public void UpdateChatActionBar(Chat chat)
        {
            if (chat.ActionBar is ChatActionBarBotInvite botInvite && ViewModel.ClientService.TryGetUser(botInvite.InviterUserId, out User inviter))
            {
                var text = FormatUserLink(Strings.ProfileBotInvitedBy, inviter);

                if (ViewModel.ClientService.TryGetUser(botInvite.OwnerUserId, out User owner))
                {
                    text = FormattedText.Concat(text, "\n", FormatUserLink(Strings.ProfileBotCurrentOwner, owner));
                }

                if (BotInvite == null)
                {
                    FindName(nameof(BotInvite));
                }

                BotInviteLabel.SetText(ViewModel.ClientService, text);
                BotInvite.Visibility = Visibility.Visible;
            }
            else
            {
                BotInvite?.Visibility = Visibility.Collapsed;
            }
        }

        private static FormattedText FormatUserLink(string format, User user)
        {
            // The markup is parsed before the name goes in, so markdown in a name stays literal,
            // and a run the translation wraps around the placeholder grows to cover the name.
            var builder = new FormattedTextBuilder(ClientEx.ParseMarkdown(format));
            builder.Substitute("{0}", user.FullName(), new TextEntityTypeMentionName(user.Id));

            return builder.ToFormattedText();
        }

        public void UpdateUserFullInfo(Chat chat, User user, UserFullInfo fullInfo, bool secret, bool accessToken)
        {
            UpdateUserStatus(chat, user);
            UpdateUserPhone(user.PhoneNumber);

            if (user.HasActiveUsername(out string username))
            {
                Username.Content = username;
                Username.Visibility = Visibility.Visible;
                UsernameQrCode.Visibility = Visibility.Visible;
            }
            else
            {
                Username.Visibility = Visibility.Collapsed;
            }

            UpdateUsernames(user.Usernames);

            Description.Description = user.Type is UserTypeBot ? Strings.DescriptionPlaceholder : Strings.UserBio;

            if (secret is false)
            {
                MiscPanel?.Visibility = Visibility.Collapsed;
            }

            OpenChat.Content = Strings.VoipGroupOpenChat;

            if (user.Type is UserTypeBot userTypeBot)
            {
                Call.Visibility = Visibility.Collapsed;
                VideoCall.Visibility = Visibility.Collapsed;

                if (userTypeBot.CanBeEdited)
                {
                    Edit.Visibility = Visibility.Visible;
                    Search.Visibility = Visibility.Visible;
                    Grid.SetColumn(Search, 2);
                    Grid.SetColumn(Edit, 1);
                }
                else
                {
                    Edit.Visibility = Visibility.Collapsed;
                }

                ShowStatistics(userTypeBot.CanBeEdited);
                AffiliateProgram?.Visibility = Visibility.Collapsed;

                if (userTypeBot.HasMainWebApp)
                {
                    if (BotMainApp == null)
                    {
                        FindName(nameof(BotMainApp));
                    }

                    BotMainApp.Visibility = Visibility.Visible;
                    InfoPanel.Footer = Strings.ProfileBotOpenAppInfo;
                }
                else
                {
                    BotMainApp?.Visibility = Visibility.Collapsed;
                    InfoPanel.Footer = string.Empty;
                }

                if (userTypeBot.CanBeEdited || userTypeBot.CanJoinGroups)
                {
                    if (BotActionsRoot == null)
                    {
                        FindName(nameof(BotActionsRoot));
                    }

                    InviteToBot.Visibility = userTypeBot.CanBeEdited
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    AddToGroup.Visibility = userTypeBot.CanJoinGroups
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                    BotActionsRoot.Footer = userTypeBot.CanJoinGroups
                        ? Strings.BotAddToGroupOrChannelInfo
                        : string.Empty;
                    BotActionsRoot.Visibility = Visibility.Visible;
                }
                else
                {
                    BotActionsRoot?.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                Edit.Visibility = Visibility.Collapsed;
                BotMainApp?.Visibility = Visibility.Collapsed;
                ShowStatistics(false);
                AffiliateProgram?.Visibility = Visibility.Collapsed;
                BotActionsRoot?.Visibility = Visibility.Collapsed;
            }

            // Unused:
            Location?.Visibility = Visibility.Collapsed;

            VideoChat.Visibility = Visibility.Collapsed;
            Join.Visibility = Visibility.Collapsed;
            Leave.Visibility = Visibility.Collapsed;

            if (AdminPanel != null)
            {
                Admins.Visibility = Visibility.Collapsed;
                Members.Visibility = Visibility.Collapsed;
                ChannelSettings.Visibility = Visibility.Collapsed;
            }

            if (fullInfo == null)
            {
                return;
            }

            if (fullInfo.UsesUnofficialApp)
            {
                if (UnofficialRoot == null)
                {
                    FindName(nameof(UnofficialRoot));
                }

                UnofficialRoot.Visibility = Visibility.Visible;
                UnofficialText.Text = string.Format(Strings.ProfileUnofficialSecurityRisk, user.FullName(true));
            }
            else
            {
                UnofficialRoot?.Visibility = Visibility.Collapsed;
            }

            if (fullInfo.Rating != null)
            {
                Rating.Visibility = Visibility.Visible;
                Rating.Value = fullInfo.Rating.Level;
            }
            else
            {
                Rating.Visibility = Visibility.Collapsed;
            }

            if (fullInfo.Note != null)
            {
                if (UserNote == null)
                {
                    FindName(nameof(UserNote));
                    UserNote.Description = string.Format("{0} ({1})", Strings.ProfileNotes, Strings.ProfileNotesInfo);
                }

                UserNote.Visibility = Visibility.Visible;
                UserNoteLabel.SetText(ViewModel.ClientService, fullInfo.Note);
            }
            else
            {
                UserNote?.Visibility = Visibility.Collapsed;
            }

            var footerVisible = false;
            var balanceVisible = false;

            if (fullInfo.TonWalletCard != null)
            {
                if (UserBalanceRoot == null)
                {
                    FindName(nameof(UserBalanceRoot));
                }

                footerVisible = true;
                balanceVisible = true;
                UserBalanceRoot.Visibility = Visibility.Visible;

                TextBlockHelper.SetMarkdown(UserBalance, string.Format(Strings.WalletBalanceAmount, $"**{Icons.Gram14} {Formatter.GramSummary(fullInfo.TonWalletCard.GramAmount).Join()}**"));
            }
            else
            {
                UserBalanceRoot?.Visibility = Visibility.Collapsed;
            }

            if (fullInfo.FirstProfileAudio != null)
            {
                if (UserFirstAudioRoot == null)
                {
                    FindName(nameof(UserFirstAudioRoot));
                }

                footerVisible = true;
                UserFirstAudioRoot.Visibility = Visibility.Visible;

                if (balanceVisible)
                {
                    UserFirstAudioRoot.Margin = new Thickness(0, 0, 8, 6);
                }
                else
                {
                    UserFirstAudioRoot.Margin = new Thickness(0, 0, 0, 6);
                }

                if (fullInfo.FirstProfileAudio.Title.Length > 0)
                {
                    UserFirstAudioTitle.Text = fullInfo.FirstProfileAudio.Title;

                    if (fullInfo.FirstProfileAudio.Performer.Length > 0)
                    {
                        UserFirstAudioSubtitle.Text = "- " + fullInfo.FirstProfileAudio.Performer;
                    }
                    else
                    {
                        UserFirstAudioSubtitle.Text = string.Empty;
                    }
                }
                else
                {
                    UserFirstAudioTitle.Text = fullInfo.FirstProfileAudio.FileName;
                    UserFirstAudioSubtitle.Text = string.Empty;
                }

                AutomationProperties.SetName(UserFirstAudioRoot, UserFirstAudioText.Text);
            }
            else
            {
                UserFirstAudioRoot?.Visibility = Visibility.Collapsed;
            }

            FooterRoot.Visibility = footerVisible
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateAnimatedPhoto(fullInfo.PersonalPhoto != null
                ? fullInfo.PersonalPhoto.SmallAnimation ?? fullInfo.PersonalPhoto.Animation
                : fullInfo.Photo?.SmallAnimation ?? fullInfo.Photo?.Animation);

            if (user.Type is UserTypeBot && fullInfo.BotInfo != null)
            {
                GetEntities(fullInfo.BotInfo.ShortDescription);
                Description.Visibility = string.IsNullOrEmpty(fullInfo.BotInfo.ShortDescription) ? Visibility.Collapsed : Visibility.Visible;

                ShowStatistics(fullInfo.BotInfo.CanGetRevenueStatistics);

                if (fullInfo.BotInfo.AffiliateProgram != null)
                {
                    if (AffiliateProgramRoot == null)
                    {
                        FindName(nameof(AffiliateProgramRoot));
                    }

                    AffiliateProgram.Visibility = Visibility.Visible;
                    AffiliateProgram.Badge = fullInfo.BotInfo.AffiliateProgram.Parameters.CommissionPercent();
                    AffiliateProgramRoot.Footer = user.Type is UserTypeBot { CanBeEdited: true }
                        ? string.Format(Strings.ProfileBotAffiliateProgramInfoOwner, user.FirstName, fullInfo.BotInfo.AffiliateProgram.Parameters.CommissionPercent())
                        : string.Format(Strings.ProfileBotAffiliateProgramInfo, user.FirstName, fullInfo.BotInfo.AffiliateProgram.Parameters.CommissionPercent());
                }
                else
                {
                    AffiliateProgram?.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                ReplaceEntities(fullInfo.Bio);
                Description.Visibility = string.IsNullOrEmpty(fullInfo.Bio.Text) ? Visibility.Collapsed : Visibility.Visible;

                ShowStatistics(false);
                AffiliateProgram?.Visibility = Visibility.Collapsed;
            }

            if (user.Type is UserTypeBot { CanBeEdited: true })
            {
            }
            else
            {
                if (user.CanBeCalled(ViewModel.ClientService))
                {
                    Call.Visibility = Visibility.Visible;
                    Call.Content = Strings.Call;
                }
                else
                {
                    Call.Visibility = Visibility.Collapsed;
                }
                VideoCall.Visibility = fullInfo.CanBeCalled && fullInfo.SupportsVideoCalls ? Visibility.Visible : Visibility.Collapsed;
                Search.Visibility = fullInfo.CanBeCalled && fullInfo.SupportsVideoCalls ? Visibility.Collapsed : Visibility.Visible;
                Grid.SetColumn(Search, 2);
            }

            // fullInfo is ClientService's cached instance, which updateUserFullInfo overwrites field
            // by field on the TDLib thread, so BusinessInfo is read once rather than per access.
            var businessInfo = fullInfo.BusinessInfo;

            var businessLocation = businessInfo?.Location;
            if (businessLocation != null)
            {
                if (Location == null)
                {
                    FindName(nameof(Location));
                }

                Location.Visibility = Visibility.Visible;
                Location.Content = businessLocation.Address;

                if (businessLocation.Location != null)
                {
                    LocationMap.Visibility = Visibility.Visible;
                    LocationMap.XamlRoot = ViewModel.XamlRoot;
                    LocationMap.SetSource(ViewModel.ClientService, businessLocation.Location, 44, 44, chat.Id);
                }
                else
                {
                    LocationMap.Visibility = Visibility.Collapsed;
                }
            }

            if (fullInfo.Birthdate != null)
            {
                var years = fullInfo.Birthdate.ToYears();
                var today = fullInfo.Birthdate.Day == DateTime.Today.Day && fullInfo.Birthdate.Month == DateTime.Today.Month;

                if (today)
                {
                    UserBirthday.Description = Strings.ProfileBirthdayToday;
                    UserBirthday.Content = years != 0
                        ? Locale.Declension(Strings.R.ProfileBirthdayTodayValueYear, years, Formatter.Birthdate(fullInfo.Birthdate))
                        : string.Format(Strings.ProfileBirthdayTodayValue, Formatter.Birthdate(fullInfo.Birthdate));
                }
                else
                {
                    UserBirthday.Description = Strings.ProfileBirthday;
                    UserBirthday.Content = years != 0
                        ? Locale.Declension(Strings.R.ProfileBirthdayValueYear, years, Formatter.Birthdate(fullInfo.Birthdate))
                        : string.Format(Strings.ProfileBirthdayValue, Formatter.Birthdate(fullInfo.Birthdate));
                }

                UserBirthday.Visibility = Visibility.Visible;
            }
            else
            {
                UserBirthday.Visibility = Visibility.Collapsed;
            }

            UpdatePersonalChannel(ViewModel.ClientService.TryGetChat(fullInfo.PersonalChatId, out Chat personalChat) ? personalChat : null);

            var openingHours = businessInfo?.OpeningHours;
            if (openingHours != null)
            {
                if (BusinessHours == null)
                {
                    FindName(nameof(BusinessHours));
                }

                BusinessHours.Visibility = Visibility.Visible;
                BusinessHours.UpdateHours(ViewModel.ClientService, openingHours);
            }
            else
            {
                BusinessHours?.Visibility = Visibility.Collapsed;
            }

            UpdateBotVerification(fullInfo.BotVerification);
        }

        private void UpdateUserPhone(string phoneNumber)
        {
            // The parsed number only changes with the number itself, and asking TDLib for it is a
            // synchronous call on every update of the user.
            if (_phoneNumber == phoneNumber)
            {
                return;
            }

            _phoneNumber = phoneNumber;

            UserPhone.Content = PhoneNumber.Format(phoneNumber);
            UserPhone.Visibility = string.IsNullOrEmpty(phoneNumber) ? Visibility.Collapsed : Visibility.Visible;

            if (phoneNumber.Length > 0)
            {
                var info = Client.Execute(new GetPhoneNumberInfoSync("en", phoneNumber)) as PhoneNumberInfo;
                if (info != null)
                {
                    AnonymousNumber.Visibility = info.IsAnonymous ? Visibility.Visible : Visibility.Collapsed;
                    AnonymousNumberSeparator.Visibility = info.IsAnonymous ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private string _phoneNumber;

        private void UpdateAnimatedPhoto(AnimatedChatPhoto animation)
        {
            if (animation != null)
            {
                if (AnimatedPhoto == null)
                {
                    FindName(nameof(AnimatedPhoto));
                }

                AnimatedPhoto.Source = new DelayedFileSource(ViewModel.ClientService, animation.File)
                {
                    SeekToSeconds = animation.MainFrameTimestamp
                };
            }
            else
            {
                AnimatedPhoto?.Source = null;
            }
        }

        // Statistics is the only row of the panel a bot shows, and the first one a channel does.
        private void ShowStatistics(bool show)
        {
            if (show)
            {
                if (AdminPanel == null)
                {
                    FindName(nameof(AdminPanel));
                }

                Statistics.Visibility = Visibility.Visible;
            }
            else
            {
                Statistics?.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdatePersonalChannel(Chat personalChat)
        {
            if (personalChat != null)
            {
                if (PersonalChannelRoot == null)
                {
                    FindName(nameof(PersonalChannelRoot));
                }

                PersonalChannelRoot.Visibility = Visibility.Visible;
                PersonalChannelFooter.Text = Locale.Declension(Strings.R.Subscribers, ViewModel.ClientService.GetMembersCount(personalChat));
                PersonalChannel.UpdateChat(ViewModel.ClientService, personalChat, new ChatListFolder(int.MaxValue));
            }
            else
            {
                PersonalChannelRoot?.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateBotVerification(BotVerification verification)
        {
            if (verification != null && ViewModel.ClientService.TryGetUser(verification.BotUserId, out User verifierBotUser))
            {
                if (BotVerifiedRoot == null)
                {
                    FindName(nameof(BotVerifiedRoot));
                }

                // The constructor starts the icon's download.
                _ = new CustomEmojiFileSource(ViewModel.ClientService, verification.IconCustomEmojiId);

                var text = verification.CustomDescription.Text.Length > 0
                    ? verification.CustomDescription
                    : string.Format(Strings.BotVerifierRepresentatives, verifierBotUser.FirstName).AsFormattedText();

                BotVerifiedText.SetText(ViewModel.ClientService, ClientEx.Format("{0} {1}", ClientEx.CustomEmoji(verification.IconCustomEmojiId), text));
                BotVerifiedText.SetQuery(string.Empty);

                BotVerifiedRoot.Visibility = Visibility.Visible;
            }
            else
            {
                BotVerifiedRoot?.Visibility = Visibility.Collapsed;
            }
        }

        public void UpdateUserStatus(Chat chat, User user)
        {
            Subtitle.Text = LastSeenConverter.GetLabel(user, true);

            var when = user.Status switch
            {
                UserStatusLastMonth lastMonth => lastMonth.ByMyPrivacySettings,
                UserStatusLastWeek lastWeek => lastWeek.ByMyPrivacySettings,
                UserStatusRecently recently => recently.ByMyPrivacySettings,
                _ => false
            };

            if (when)
            {
                if (SubtitleWhen == null)
                {
                    FindName(nameof(SubtitleWhen));
                }

                SubtitleWhen.Visibility = Visibility.Visible;
            }
            else
            {
                SubtitleWhen?.Visibility = Visibility.Collapsed;
            }
        }



        public void UpdateSecretChat(Chat chat, SecretChat secretChat)
        {
            if (secretChat.State is SecretChatStateReady)
            {
                if (MiscPanel == null)
                {
                    FindName(nameof(MiscPanel));
                }

                SecretLifetime.Badge = chat.MessageAutoDeleteTime > 0 ? Locale.FormatTtl(chat.MessageAutoDeleteTime) : Strings.ShortMessageLifetimeForever;
                //SecretIdenticon.Source = Direct2D.GetIdenticon(secretChat.KeyHash, 24);

                MiscPanel.Visibility = Visibility.Visible;
                SecretLifetime.Visibility = Visibility.Visible;
                SecretHashKey.Visibility = Visibility.Visible;
            }
            else
            {
                MiscPanel?.Visibility = Visibility.Collapsed;
            }
        }



        public void UpdateBasicGroupFullInfo(Chat chat, BasicGroup group, BasicGroupFullInfo fullInfo)
        {
            Subtitle.Text = Locale.Declension(Strings.R.Members, group.MemberCount);
            SubtitleWhen?.Visibility = Visibility.Collapsed;

            RatingRoot.Visibility = Visibility.Collapsed;

            Description.Description = Strings.DescriptionPlaceholder;

            UserPhone.Visibility = Visibility.Collapsed;
            _phoneNumber = null;
            Location?.Visibility = Visibility.Collapsed;
            Username.Visibility = Visibility.Collapsed;

            Description.Visibility = Visibility.Collapsed;

            //UserCommonChats.Visibility = Visibility.Collapsed;
            MiscPanel?.Visibility = Visibility.Collapsed;

            if (AdminPanel != null)
            {
                Admins.Visibility = Visibility.Collapsed;
                Members.Visibility = Visibility.Collapsed;
                Statistics.Visibility = Visibility.Collapsed;
                ChannelSettings.Visibility = Visibility.Collapsed;
            }

            AffiliateProgram?.Visibility = Visibility.Collapsed;
            UnofficialRoot?.Visibility = Visibility.Collapsed;

            if (chat.Permissions.CanChangeInfo || group.Status is ChatMemberStatusCreator || group.Status is ChatMemberStatusAdministrator)
            {
                Edit.Visibility = Visibility.Visible;
                Join.Visibility = Visibility.Collapsed;
                Leave.Visibility = Visibility.Collapsed;
            }
            else
            {
                Edit.Visibility = Visibility.Collapsed;
                Join.Visibility = Visibility.Collapsed;
                Leave.Visibility = Visibility.Visible;
            }

            OpenChat.Content = Strings.VoipGroupOpenGroup;

            if (chat.VideoChat.GroupCallId != 0 || group.CanManageVideoChats())
            {
                VideoChat.Visibility = Visibility.Visible;
                Search.Visibility = Visibility.Collapsed;
            }
            else
            {
                VideoChat.Visibility = Visibility.Collapsed;
                Search.Visibility = Visibility.Visible;

                Grid.SetColumn(Search, 1);
            }

            // Unused:
            Call.Visibility = Visibility.Collapsed;
            VideoCall.Visibility = Visibility.Collapsed;

            BotMainApp?.Visibility = Visibility.Collapsed;
            BotActionsRoot?.Visibility = Visibility.Collapsed;

            AnonymousNumber.Visibility = Visibility.Collapsed;
            AnonymousNumberSeparator.Visibility = Visibility.Collapsed;

            PersonalChannelRoot?.Visibility = Visibility.Collapsed;
            UserBirthday.Visibility = Visibility.Collapsed;

            BusinessHours?.Visibility = Visibility.Collapsed;

            if (fullInfo == null)
            {
                return;
            }

            UpdateAnimatedPhoto(fullInfo.Photo?.SmallAnimation ?? fullInfo.Photo?.Animation);
            GetEntities(fullInfo.Description);

            var description = !string.IsNullOrEmpty(fullInfo.Description);

            Description.Visibility = description
                ? Visibility.Visible
                : Visibility.Collapsed;

            InfoPanel.Visibility = description || ChatId?.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }



        public void UpdateSupergroupFullInfo(Chat chat, Supergroup group, SupergroupFullInfo fullInfo)
        {
            if (ViewModel.ForumTopic != null)
            {
                Subtitle.Text = string.Format(Strings.TopicProfileStatus, chat.Title);
            }
            else if (fullInfo != null)
            {
                Subtitle.Text = Locale.Declension(group.IsChannel ? Strings.R.Subscribers : Strings.R.Members, fullInfo.MemberCount);
            }
            else
            {
                Subtitle.Text = Locale.Declension(group.IsChannel ? Strings.R.Subscribers : Strings.R.Members, group.MemberCount);
            }

            SubtitleWhen?.Visibility = Visibility.Collapsed;

            RatingRoot.Visibility = Visibility.Collapsed;
            UnofficialRoot?.Visibility = Visibility.Collapsed;

            Description.Description = Strings.DescriptionPlaceholder;

            if (ViewModel.ForumTopic != null)
            {
                ViewModel.ClientService.Send(new GetForumTopicLink(chat.Id, ViewModel.ForumTopic.Info.ForumTopicId), result =>
                {
                    if (result is MessageLink link)
                    {
                        this.BeginOnUIThread(() =>
                        {
                            Username.Content = link.Link;
                            Username.Visibility = Visibility.Visible;

                            // The code encodes the forum's username, which a private forum does not have.
                            UsernameQrCode.Visibility = link.IsPublic
                                ? Visibility.Visible
                                : Visibility.Collapsed;

                            SetUsernamesText(link.IsPublic ? Strings.InviteLink : Strings.InviteLinkPrivate);
                        });
                    }
                });
            }
            else
            {
                if (group.HasActiveUsername(out string username))
                {
                    Username.Content = username;
                    Username.Visibility = Visibility.Visible;
                    UsernameQrCode.Visibility = Visibility.Visible;
                }
                else
                {
                    Username.Visibility = Visibility.Collapsed;
                }

                UpdateUsernames(group.Usernames);
            }

            if (group.HasLocation)
            {
                if (Location == null)
                {
                    FindName(nameof(Location));
                }

                Location.Visibility = Visibility.Visible;
            }
            else
            {
                Location?.Visibility = Visibility.Collapsed;
            }

            if (chat.VideoChat.GroupCallId != 0 || group.CanManageVideoChats())
            {
                VideoChat.Visibility = Visibility.Visible;
                Search.Visibility = Visibility.Collapsed;
            }
            else
            {
                VideoChat.Visibility = Visibility.Collapsed;
                Search.Visibility = Visibility.Visible;

                Grid.SetColumn(Search, 1);
            }

            Call.Visibility = Visibility.Collapsed;
            VideoCall.Visibility = Visibility.Collapsed;

            if (group.Status is ChatMemberStatusCreator or ChatMemberStatusAdministrator)
            {
                Edit.Visibility = Visibility.Visible;
                Join.Visibility = Visibility.Collapsed;
                Leave.Visibility = Visibility.Collapsed;
            }
            else
            {
                Edit.Visibility = Visibility.Collapsed;

                if (group.CanJoin())
                {
                    Join.Visibility = Visibility.Visible;
                    Leave.Visibility = Visibility.Collapsed;
                }
                else
                {
                    Join.Visibility = Visibility.Collapsed;
                    Leave.Visibility = Visibility.Visible;
                }
            }

            OpenChat.Content = group.IsChannel
                ? Strings.VoipGroupOpenChannel
                : Strings.VoipGroupOpenGroup;

            // Unused:
            BotMainApp?.Visibility = Visibility.Collapsed;
            BotActionsRoot?.Visibility = Visibility.Collapsed;
            MiscPanel?.Visibility = Visibility.Collapsed;
            UserPhone.Visibility = Visibility.Collapsed;
            _phoneNumber = null;
            //UserCommonChats.Visibility = Visibility.Collapsed;

            AnonymousNumber.Visibility = Visibility.Collapsed;
            AnonymousNumberSeparator.Visibility = Visibility.Collapsed;

            UserBirthday.Visibility = Visibility.Collapsed;

            BusinessHours?.Visibility = Visibility.Collapsed;

            if (fullInfo == null || ViewModel.ForumTopic != null)
            {
                return;
            }

            UpdateAnimatedPhoto(fullInfo.Photo?.SmallAnimation ?? fullInfo.Photo?.Animation);

            GetEntities(fullInfo.Description);
            Description.Visibility = string.IsNullOrEmpty(fullInfo.Description) ? Visibility.Collapsed : Visibility.Visible;

            if (fullInfo.Location != null)
            {
                if (Location == null)
                {
                    FindName(nameof(Location));
                }

                Location.Visibility = Visibility.Visible;
                Location.Content = fullInfo.Location.Address;
            }
            else if (Location != null)
            {
                Location.Visibility = Visibility.Collapsed;
                Location.Content = null;
            }

            if (group.IsChannel && group.Status is ChatMemberStatusCreator or ChatMemberStatusAdministrator)
            {
                if (AdminPanel == null)
                {
                    FindName(nameof(AdminPanel));
                }

                Admins.Visibility = Visibility.Visible;
                Members.Visibility = Visibility.Visible;
                ChannelSettings.Visibility = Visibility.Visible;

                Admins.Badge = fullInfo.AdministratorCount.ToString("N0");
                Members.Badge = fullInfo.MemberCount.ToString("N0");
            }
            else if (AdminPanel != null)
            {
                Admins.Visibility = Visibility.Collapsed;
                Members.Visibility = Visibility.Collapsed;
                ChannelSettings.Visibility = Visibility.Collapsed;
            }

            ShowStatistics(fullInfo.CanGetRevenueStatistics || fullInfo.CanGetStarRevenueStatistics);
            AffiliateProgram?.Visibility = Visibility.Collapsed;

            UpdatePersonalChannel(group.IsChannel is false && ViewModel.ClientService.TryGetChat(fullInfo.LinkedChatId, out Chat linkedChat) && linkedChat.LastMessage != null ? linkedChat : null);
            UpdateBotVerification(fullInfo.BotVerification);
        }

        // 0 when the text is unknown, 1 when it is the plain label, 2 when it lists other usernames.
        private int _usernamesState;

        private void UpdateUsernames(Usernames usernames)
        {
            if (usernames?.ActiveUsernames.Count > 1)
            {
                ClearUsernames();
                _usernamesState = 2;

                ActiveUsernames.Inlines.Add(new Run { Text = string.Format(Strings.UsernameAlso, string.Empty) });

                for (int i = 1; i < usernames.ActiveUsernames.Count; i++)
                {
                    if (i > 1)
                    {
                        ActiveUsernames.Inlines.Add(new Run { Text = ", " });
                    }

                    var hyperlink = new Hyperlink();
                    hyperlink.Inlines.Add(new Run { Text = $"@{usernames.ActiveUsernames[i]}" });
                    hyperlink.UnderlineStyle = UnderlineStyle.None;
                    hyperlink.Click += Username_Click;

                    ActiveUsernames.Inlines.Add(hyperlink);
                }
            }
            else if (_usernamesState != 1)
            {
                SetUsernamesText(Strings.Username);
                _usernamesState = 1;
            }
        }

        private void SetUsernamesText(string text)
        {
            ClearUsernames();
            _usernamesState = 0;

            ActiveUsernames.Inlines.Add(new Run { Text = text });
        }

        private void ClearUsernames()
        {
            if (_usernamesState == 2)
            {
                foreach (var inline in ActiveUsernames.Inlines)
                {
                    if (inline is Hyperlink hyperlink)
                    {
                        hyperlink.Click -= Username_Click;
                    }
                }
            }

            ActiveUsernames.Inlines.Clear();
        }

        private void Username_Click(Hyperlink sender, HyperlinkClickEventArgs args)
        {
            if (sender.Inlines.Count > 0 && sender.Inlines[0] is Run run && run.Text.Length > 1)
            {
                ViewModel.OpenUsernameInfo(run.Text.Substring(1));
            }
        }

        #endregion

        #region Context menu

        private void About_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            MessageHelper.Hyperlink_ContextRequested(ViewModel.NavigationService, ViewModel.TranslateService, sender, args, null);
        }

        private void Description_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var description = ViewModel.CopyDescription();
            if (description != null)
            {
                MessageHelper.Hyperlink_ContextRequested(ViewModel.TranslateService, sender, description, args);
            }
        }

        private void UserNote_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            void Copy()
            {
                if (ViewModel.ClientService.TryGetUserFull(ViewModel.Chat, out UserFullInfo fullInfo))
                {
                    MessageHelper.CopyText(XamlRoot, fullInfo.Note);
                }
            }

            async void Remove()
            {
                var confirm = await ViewModel.ShowPopupAsync(Strings.ProfileNotesRemoveText, Strings.ProfileNotesRemoveTitle, Strings.Delete, Strings.Cancel, destructive: true);
                if (confirm == ModalPopupResult.Primary && ViewModel.ClientService.TryGetUser(ViewModel.Chat, out User user))
                {
                    ViewModel.ClientService.Send(new SetUserNote(user.Id, string.Empty.AsFormattedText()));
                }
            }

            var flyout = new MenuFlyout();
            flyout.CreateFlyoutItem(Copy, Strings.Copy, Icons.Copy);
            flyout.CreateFlyoutItem(ViewModel.AddToContacts, Strings.Edit, Icons.Edit);
            flyout.CreateFlyoutItem(Remove, Strings.Remove, Icons.Delete, destructive: true);
            flyout.ShowAt(sender, args);
        }

        private void Menu_ContextRequested(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            var chat = ViewModel.Chat;
            if (chat == null)
            {
                return;
            }

            var user = chat.Type is ChatTypePrivate or ChatTypeSecret ? ViewModel.ClientService.GetUser(chat) : null;

            if (chat.CanChangeInfo(ViewModel.ClientService) || (user != null && user.Id != ViewModel.ClientService.Options.MyId && chat.Id != ViewModel.ClientService.Options.TelegramServiceNotificationsChatId))
            {
                var icon = chat.MessageAutoDeleteTime switch
                {
                    60 * 60 * 24 => Icons.AutoDeleteDay,
                    60 * 60 * 24 * 7 => Icons.AutoDeleteWeek,
                    60 * 60 * 24 * 31 => Icons.AutoDeleteMonth,
                    _ => Icons.Timer
                };

                var autodelete = new MenuFlyoutSubItem();
                autodelete.Text = Strings.AutoDeletePopupTitle;
                autodelete.Icon = MenuFlyoutHelper.CreateIcon(icon);

                void AddToggle(int value, int? parameter, string text, string icon)
                {
                    var item = new ToggleMenuFlyoutItem();
                    item.Text = text;
                    item.IsChecked = parameter != null && value == parameter;
                    item.CommandParameter = parameter;
                    item.Command = ViewModel.SetTimerCommand;
                    item.Icon = MenuFlyoutHelper.CreateIcon(icon);

                    autodelete.Items.Add(item);
                }

                AddToggle(chat.MessageAutoDeleteTime, 0, Strings.ShortMessageLifetimeForever, Icons.AutoDeleteOff);

                autodelete.CreateFlyoutSeparator();

                AddToggle(chat.MessageAutoDeleteTime, 60 * 60 * 24, Locale.FormatTtl(60 * 60 * 24), Icons.AutoDeleteDay);
                AddToggle(chat.MessageAutoDeleteTime, 60 * 60 * 24 * 7, Locale.FormatTtl(60 * 60 * 24 * 7), Icons.AutoDeleteWeek);
                AddToggle(chat.MessageAutoDeleteTime, 60 * 60 * 24 * 31, Locale.FormatTtl(60 * 60 * 24 * 31), Icons.AutoDeleteMonth);
                AddToggle(chat.MessageAutoDeleteTime, null, Strings.AutoDownloadCustom, Icons.Options);

                flyout.Items.Add(autodelete);
                flyout.CreateFlyoutSeparator();
            }

            if (chat.Type is ChatTypePrivate or ChatTypeSecret && user != null)
            {
                var userId = chat.Type is ChatTypePrivate privata ? privata.UserId : chat.Type is ChatTypeSecret secret ? secret.UserId : 0;
                if (userId != ViewModel.ClientService.Options.MyId)
                {
                    var fullInfo = ViewModel.ClientService.GetUserFull(userId);
                    if (fullInfo == null)
                    {
                        return;
                    }

                    //if (fullInfo.CanBeCalled)
                    //{
                    //    callItem = menu.addItem(call_item, R.drawable.ic_call_white_24dp);
                    //}
                    if (user.IsContact)
                    {
                        flyout.CreateFlyoutItem(ViewModel.Share, Strings.ShareContact, Icons.Share);
                        flyout.CreateFlyoutItem(chat.BlockList is BlockListMain ? ViewModel.Unblock : ViewModel.Block, chat.BlockList is BlockListMain ? Strings.Unblock : Strings.BlockContact, chat.BlockList is BlockListMain ? Icons.Block : Icons.Block);
                        flyout.CreateFlyoutItem(ViewModel.Edit, Strings.EditContact, Icons.Edit);
                        flyout.CreateFlyoutItem(ViewModel.Delete, Strings.DeleteContact, Icons.Delete, destructive: true);
                    }
                    else
                    {
                        if (user.Type is UserTypeBot)
                        {
                            flyout.CreateFlyoutItem(() => { }, Strings.BotShare, Icons.Share);
                            flyout.CreateFlyoutItem(ViewModel.PrivacyPolicy, Strings.BotPrivacyPolicy, Icons.ShieldCheckmark);
                        }
                        else
                        {
                            flyout.CreateFlyoutItem(ViewModel.AddToContacts, Strings.AddContact, Icons.PersonAdd);
                        }

                        if (user.PhoneNumber.Length > 0)
                        {
                            flyout.CreateFlyoutItem(ViewModel.Share, Strings.ShareContact, Icons.Share);
                            flyout.CreateFlyoutItem(chat.BlockList is BlockListMain ? ViewModel.Unblock : ViewModel.Block, chat.BlockList is BlockListMain ? Strings.Unblock : Strings.BlockContact, chat.BlockList is BlockListMain ? Icons.Block : Icons.Block);
                        }
                        else
                        {
                            if (user.Type is UserTypeBot)
                            {
                                flyout.CreateFlyoutItem(chat.BlockList is BlockListMain ? ViewModel.Unblock : ViewModel.Block, chat.BlockList is BlockListMain ? Strings.BotRestart : Strings.BotStop, chat.BlockList is BlockListMain ? Icons.Block : Icons.Block);
                            }
                            else
                            {
                                flyout.CreateFlyoutItem(chat.BlockList is BlockListMain ? ViewModel.Unblock : ViewModel.Block, chat.BlockList is BlockListMain ? Strings.Unblock : Strings.BlockContact, chat.BlockList is BlockListMain ? Icons.Block : Icons.Block);
                            }
                        }
                    }

                    if (user.Type is UserTypeRegular && ViewModel.IsPremiumAvailable)
                    {
                        flyout.CreateFlyoutItem(ViewModel.GiftPremium, Strings.SendAGift, Icons.GiftPremium);
                    }

                    if (user.Type is UserTypeRegular && ViewModel.ClientService.Options.CanUseTonWallet)
                    {
                        flyout.CreateFlyoutItem(ViewModel.SendMoney, Strings.WalletAttachMoney, Icons.Gram);
                    }

                    if (user.Type is UserTypeRegular && !user.IsSupport)
                    {
                        flyout.CreateFlyoutItem(ViewModel.CreateSecretChat, Strings.StartEncryptedChat, Icons.LockClosed);
                        flyout.CreateFlyoutItem(ViewModel.ToggleProtectedContent, chat.HasProtectedContent ? Strings.EnableSharing : Strings.DisableSharing, chat.HasProtectedContent ? Icons.Share : Icons.ShareOff);
                    }
                }
                else
                {
                    flyout.CreateFlyoutItem(ViewModel.Share, Strings.ShareContact, Icons.Share);
                }
            }
            //if (writeButton != null)
            //{
            //    boolean isChannel = ChatObject.isChannel(currentChat);
            //    if (isChannel && !ChatObject.canChangeChatInfo(currentChat) || !isChannel && !currentChat.admin && !currentChat.creator && currentChat.admins_enabled)
            //    {
            //        writeButton.setImageResource(R.drawable.floating_message);
            //        writeButton.setPadding(0, AndroidUtilities.dp(3), 0, 0);
            //    }
            //    else
            //    {
            //        writeButton.setImageResource(R.drawable.floating_camera);
            //        writeButton.setPadding(0, 0, 0, 0);
            //    }
            //}
            if (ViewModel.ClientService.TryGetSupergroup(chat, out Supergroup supergroup))
            {
                var fullInfo = ViewModel.ClientService.GetSupergroupFull(supergroup.Id);

                if (supergroup.Status is ChatMemberStatusCreator or ChatMemberStatusAdministrator)
                {
                    if (supergroup.IsChannel)
                    {
                        //flyout.CreateFlyoutItem(ViewModel.EditCommand, Strings.ManageChannelMenu, Icons.Edit);
                    }
                    else if (supergroup.CanInviteUsers(chat))
                    {
                        flyout.CreateFlyoutItem(ViewModel.Invite, Strings.AddMember, Icons.PersonAdd);
                    }
                }

                if (fullInfo != null && fullInfo.CanGetStatistics)
                {
                    flyout.CreateFlyoutItem(ViewModel.OpenStatistics, Strings.Statistics, Icons.DataUsage);
                }

                if (chat.CanPostStories(ViewModel.ClientService))
                {
                    flyout.CreateFlyoutItem(ViewModel.AddStory, Strings.AddStory, Icons.StoriesAdd);
                }

                if (supergroup.CanEditStories())
                {
                    flyout.CreateFlyoutItem(ViewModel.OpenArchivedStories, Strings.ArchivedStories, Icons.Archive);
                }

                if (supergroup.IsChannel && supergroup.HasLinkedChat)
                {
                    flyout.CreateFlyoutItem(ViewModel.Discuss, Strings.ViewDiscussion, Icons.ChatEmpty);
                }

                if (supergroup.Status is ChatMemberStatusMember or ChatMemberStatusRestricted)
                {
                    flyout.CreateFlyoutItem(ViewModel.DeleteChat, supergroup.IsChannel ? Strings.LeaveChannelMenu : Strings.LeaveMegaMenu, Icons.Delete, destructive: true);
                }

                if (fullInfo != null && fullInfo.CanSendGift)
                {
                    flyout.CreateFlyoutItem(ViewModel.GiftPremium, Strings.SendAGift, Icons.GiftPremium);
                }
            }
            else if (ViewModel.ClientService.TryGetBasicGroup(chat, out BasicGroup basicGroup))
            {
                if (basicGroup.Status is ChatMemberStatusCreator || (basicGroup.Status is ChatMemberStatusAdministrator administrator && administrator.Rights.CanInviteUsers) || chat.Permissions.CanInviteUsers)
                {
                    flyout.CreateFlyoutItem(ViewModel.Invite, Strings.AddMember, Icons.PersonAdd);
                }

                if (basicGroup.Status is ChatMemberStatusMember or ChatMemberStatusRestricted)
                {
                    flyout.CreateFlyoutItem(ViewModel.DeleteChat, Strings.DeleteAndExit, Icons.Delete, destructive: true);
                }
            }

            if (ApiInfo.HasMultipleViews)
            {
                flyout.CreateFlyoutSeparator();
                flyout.CreateFlyoutItem(ViewModel.OpenChat, Strings.OpenInNewWindow, Icons.WindowNew);
            }

            //flyout.CreateFlyoutItem(null, Strings.AddShortcut, Icons.Pin);

            MenuTarget.RequestedTheme = ActualTheme;

            if (flyout.Items.Count > 0)
            {
                flyout.ShowAt(MenuTarget, FlyoutPlacementMode.BottomEdgeAlignedRight);
            }
        }

        #endregion

        #region Entities

        private void GetEntities(string text)
        {
            DescriptionLabel.SetText(ViewModel.ClientService, text, ClientEx.GetTextEntities(text));
            Description.BadgeLabel = text;
        }

        private void ReplaceEntities(FormattedText text)
        {
            DescriptionLabel.SetText(ViewModel.ClientService, text);
            Description.BadgeLabel = text.Text;
        }

        private void About_TextEntityClick(object sender, TextEntityClickEventArgs e)
        {
            if (e.Type is TextEntityTypeBotCommand)
            {

            }
            else if (e.Type is TextEntityTypeEmailAddress)
            {
                ViewModel.OpenUrl("mailto:" + e.Text, false);
            }
            else if (e.Type is TextEntityTypePhoneNumber)
            {
                ViewModel.OpenUrl("tel:" + e.Text, false);
            }
            else if (e.Type is TextEntityTypeHashtag or TextEntityTypeCashtag)
            {
                ViewModel.OpenSearch(e.Text);
            }
            else if (e.Type is TextEntityTypeMention)
            {
                ViewModel.OpenUsername(e.Text);
            }
            else if (e.Type is TextEntityTypeMentionName mentionName)
            {
                ViewModel.OpenUser(mentionName.UserId);
            }
            else if (e.Type is TextEntityTypeTextUrl textUrl)
            {
                ViewModel.OpenUrl(textUrl.Url, true);
            }
            else if (e.Type is TextEntityTypeUrl)
            {
                ViewModel.OpenUrl(e.Text, false);
            }
        }

        #endregion

        #region Binding

        private string ConvertCryptoCount(long count)
        {
            return string.Format("{0:N3}", count / Constants.ToncoinMin);
        }

        public string ConvertStarCount(StarAmount amount)
        {
            if (amount != null)
            {
                return amount.ToValue();
            }

            return null;
        }

        public Visibility ConvertStarVisibility(StarAmount amount)
        {
            if (amount?.StarCount > 0 || amount?.NanostarCount > 0)
            {
                return Visibility.Visible;
            }

            return Visibility.Collapsed;
        }

        #endregion

        private void Notifications_Click(object sender, RoutedEventArgs e)
        {
            var chat = ViewModel.Chat;
            if (chat == null)
            {
                return;
            }

            var muted = ViewModel.ClientService.Notifications.IsMuted(chat);
            if (muted)
            {
                ViewModel.Unmute();
            }
            else
            {
                var silent = ViewModel.ClientService.Notifications.IsSilent(chat);

                var flyout = new MenuFlyout();

                if (muted is false)
                {
                    flyout.CreateFlyoutItem(ViewModel.SetSound, !silent,
                        silent ? Strings.SoundOn : Strings.SoundOff,
                        silent ? Icons.MusicNote2 : Icons.MusicNoteOff2);
                }

                flyout.CreateFlyoutItem<int?>(ViewModel.MuteFor, 60 * 60, Strings.MuteFor1h, Icons.ClockAlarmHour);
                flyout.CreateFlyoutItem<int?>(ViewModel.MuteFor, null, Strings.MuteForPopup, Icons.AlertSnooze);

                var toggle = flyout.CreateFlyoutItem(
                    muted ? ViewModel.Unmute : ViewModel.Mute,
                    muted ? Strings.UnmuteNotifications : Strings.MuteNotifications,
                    muted ? Icons.Speaker3 : Icons.SpeakerOff, destructive: !muted);

                NotificationsTarget.RequestedTheme = ActualTheme;

                flyout.ShowAt(NotificationsTarget, FlyoutPlacementMode.Bottom);
            }
        }

        private async void Birthday_Click(object sender, RoutedEventArgs e)
        {
            var fullInfo = ViewModel.ClientService.GetUserFull(ViewModel.Chat);
            if (fullInfo?.Birthdate == null)
            {
                return;
            }

            var effect = await GetEffectAsync();
            var digits = await GetDigitsAsync(fullInfo.Birthdate.ToYears());

            if (effect == null || digits == null || !this.IsConnected())
            {
                return;
            }

            foreach (var popup2 in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            {
                popup2.IsOpen = false;
            }

            var player = new AnimatedImage();
            player.Width = 320;
            player.Height = 320;
            player.LoopCount = 1;
            player.IsCachingEnabled = true;
            player.IsHitTestVisible = false;
            player.FrameSize = new Size(320, 320);
            player.DecodeFrameType = DecodePixelType.Logical;
            player.AutoPlay = true;
            player.Source = new DelayedFileSource(ViewModel.ClientService, effect.StickerValue);
            player.RenderTransformOrigin = new Point(0.5f, 0.5f);
            player.RenderTransform = new ScaleTransform
            {
                ScaleX = -1
            };

            var center = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(16 + 20 + 10 - 10),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Colors.Black)
            };

            var popup = new Popup();
            var content = new Grid();

            var transform = UserBirthday.TransformToVisual(null);
            var point = transform.TransformPoint(new Point());

            var panel = new StackPanel();
            panel.Orientation = Orientation.Horizontal;
            panel.VerticalAlignment = VerticalAlignment.Center;

            var root = ElementCompositionPreview.GetElementVisual(panel);
            ElementCompositionPreview.SetIsTranslationEnabled(panel, true);

            var easingX = root.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.32f, 0), new Vector2(0.67f, 1));
            //var easingY = root.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.35f, -0.15f), new Vector2(1, 0.45f));
            var easingY = root.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, -0.15f), new Vector2(0.99f, 0.08f));

            content.IsHitTestVisible = false;
            //content.Background = new SolidColorBrush(Color.FromArgb(0, 0xff, 0, 0));
            content.Width = 320;
            content.Height = 320;
            //content.Children.Add(center);
            content.Children.Add(player);
            content.Children.Add(panel);

            var size = Math.Min(120f, 270f / digits.Count);
            var offset = 0f;

            for (int i = 0; i < digits.Count; i++)
            {
                Sticker digit = digits[i];
                var pippo = new AnimatedImage();
                pippo.Width = size;
                pippo.Height = size;
                pippo.LoopCount = 1;
                pippo.IsCachingEnabled = true;
                pippo.IsHitTestVisible = false;
                pippo.FrameSize = new Size(size, size);
                pippo.DecodeFrameType = DecodePixelType.Logical;
                pippo.AutoPlay = true;
                pippo.Source = new DelayedFileSource(ViewModel.ClientService, digit.StickerValue);

                if (i > 0)
                {
                    pippo.Margin = new Thickness(-(size * (digit.Emoji == "\u0031\uFE0F\u20E3" ? 0.35 : 0.25)), 0, 0, 0);
                }

                var visual = ElementComposition.GetElementVisual(pippo);
                visual.CenterPoint = new Vector3(-offset + 16 + 20 + 10, size * 0.25f, 0);

                var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
                scale.InsertKeyFrame(0, Vector3.Zero);
                scale.InsertKeyFrame(1, Vector3.One);
                scale.Duration = TimeSpan.FromSeconds(1.33);
                scale.DelayTime = TimeSpan.FromSeconds(i * 0.25);
                scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

                visual.StartAnimation("Scale", scale);

                panel.Children.Add(pippo);
                offset += size * (digit.Emoji == "\u0031\uFE0F\u20E3" ? 0.35f : 0.25f);
            }

            var translateX = root.Compositor.CreateScalarKeyFrameAnimation();
            translateX.InsertKeyFrame(0, 0, easingX);
            translateX.InsertKeyFrame(1, 120, easingX);
            translateX.Duration = TimeSpan.FromSeconds(3);
            translateX.DelayTime = TimeSpan.FromSeconds(0);

            var translateY = root.Compositor.CreateScalarKeyFrameAnimation();
            translateY.InsertKeyFrame(0, 0, easingY);
            translateY.InsertKeyFrame(1, (float)-point.Y - size, easingY);
            translateY.Duration = TimeSpan.FromSeconds(2);

            root.StartAnimation("Translation.X", translateX);
            root.StartAnimation("Translation.Y", translateY);

            popup.Width = 320;
            popup.Height = 320;
            popup.HorizontalOffset = point.X - 16;
            popup.VerticalOffset = point.Y - 8 - (320 - UserBirthday.ActualHeight) / 2;
            popup.Child = content;
            popup.IsHitTestVisible = false;
            popup.XamlRoot = XamlRoot;
            popup.IsOpen = true;

            var dispatcher = DispatcherQueue.GetForCurrentThread();

            player.LoopCompleted += (s, args) =>
            {
                dispatcher.TryEnqueue(() => popup.IsOpen = false);
            };

            ViewModel.Aggregator.Publish(new UpdateConfetti());
        }

        private int _effect;

        private async Task<Sticker> GetEffectAsync()
        {
            var response = await ViewModel.ClientService.SendAsync(new SearchStickerSet("EmojiAnimations", false));
            if (response is StickerSet stickerSet)
            {
                var stickers = stickerSet.Stickers
                    .Where(x => x.Emoji is /*"\U0001F389" or "\U0001F386" or*/ "\U0001F388" or "\U0001F973")
                    .ToList();

                if (stickers.Count > 0)
                {
                    return stickers[_effect++ % stickers.Count];
                }
            }

            return null;
        }

        private async Task<IList<Sticker>> GetDigitsAsync(int years)
        {
            var response = await ViewModel.ClientService.SendAsync(new SearchStickerSet("FestiveFontEmoji", false));
            if (response is StickerSet stickerSet)
            {
                var text = years.ToString();
                var map = stickerSet.Stickers
                    .DistinctBy(x => x.Emoji)
                    .ToDictionary(x => x.Emoji);

                var result = new List<Sticker>();

                foreach (var c in text)
                {
                    if (map.TryGetValue(c + "\uFE0F\u20E3", out Sticker sticker))
                    {
                        result.Add(sticker);
                    }
                }

                return result.Count > 0 ? result : null;
            }

            return null;
        }

        private void Identity_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ShowPromo();
        }

        private void HeaderPhoto_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var visual = ElementComposition.GetElementVisual(HeaderPhoto);
            visual.CenterPoint = new Vector3(HeaderPhoto.ActualSize / 2, 0);
        }

        private void TitleRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var title = ElementComposition.GetElementVisual(TitleRoot);
            title.CenterPoint = new Vector3(TitleRoot.ActualSize.X / 2, TitleRoot.ActualSize.Y, 0);
        }

        private void SubtitleRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var subtitle = ElementComposition.GetElementVisual(SubtitleRoot);
            subtitle.CenterPoint = new Vector3(SubtitleRoot.ActualSize.X / 2, 0, 0);
        }

        private void Pattern_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel.IsSavedMessages)
            {
                Pattern.TransitionFraction = float.MaxValue;
                GiftsCover.TransitionFraction = float.MaxValue;

                ShowHideSubtitle(true);
                ShowHideBackground(true);
            }
        }

        private void GiftsCover_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            GiftsCover.TransitionFraction = _verticalOffset / (32 + 140 + 96);
        }

        private void Rating_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ShowRating();
        }

        private void UserFirstAudio_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ClientService.TryGetUser(ViewModel.Chat, out User user)
                && ViewModel.ClientService.TryGetUserFull(user.Id, out UserFullInfo userFull))
            {
                if (userFull.FirstProfileAudio != null)
                {
                    LifetimeService.Current.Playback.Play(XamlRoot, new AudioWithOwner(ViewModel.ClientService, user.Id, userFull.FirstProfileAudio));
                    ViewModel.ShowPopup(new PlaybackPopup(ViewModel.ClientService, ViewModel.NavigationService));
                }
            }
        }

        private void Location_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ClientService.TryGetUserFull(ViewModel.Chat, out UserFullInfo fullInfo))
            {
                var location = fullInfo.BusinessInfo?.Location?.Location;
                if (location != null)
                {
                    try
                    {
                        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(string.Format(CultureInfo.InvariantCulture, "https://www.google.com/maps/search/?api=1&query={0},{1}", location.Latitude, location.Longitude)));
                    }
                    catch
                    {
                        // All the remote procedure calls must be wrapped in a try-catch block
                    }
                }
            }
        }

        private async void UserBalance_Click(object sender, RoutedEventArgs e)
        {
            var clientService = ViewModel.ClientService;
            if (clientService.TryGetUser(ViewModel.Chat, out User user)
                && clientService.TryGetUserFull(user.Id, out UserFullInfo fullInfo)
                && fullInfo.TonWalletCard != null)
            {
                var confirm = await ViewModel.ShowPopupAsync(new WalletBalancePopup(clientService, user, fullInfo.TonWalletCard, UserBalanceRoot));
                if (confirm == ModalPopupResult.Primary)
                {
                    ViewModel.SendMoney();
                }
            }
        }
    }

    public partial class ProfileButtonsGrid : Grid
    {
        private int _columns;

        protected override Size MeasureOverride(Size availableSize)
        {
            var columns = 0;

            foreach (var child in Children)
            {
                if (child.Visibility == Visibility.Collapsed)
                {
                    continue;
                }

                columns++;
            }

            var column = new Size((float)Math.Max(0, (availableSize.Width - ColumnSpacing * (columns - 1)) / columns), availableSize.Height);
            var height = 0d;

            foreach (var child in Children)
            {
                child.Measure(column);

                if (child.Visibility == Visibility.Visible)
                {
                    height = Math.Max(height, child.DesiredSize.Height);
                }
            }

            _columns = columns;
            return new Size(availableSize.Width, (float)height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var x = 0d;
            var column = Math.Max(0, (finalSize.Width - ColumnSpacing * (_columns - 1)) / _columns);

            foreach (var child in Children)
            {
                child.Arrange(new Rect((float)x, 0, (float)column, finalSize.Height));

                if (child.Visibility == Visibility.Visible)
                {
                    x += column + ColumnSpacing;
                }
            }

            return finalSize;
        }
    }
}
