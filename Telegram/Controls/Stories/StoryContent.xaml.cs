//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Telegram.Collections;
using Telegram.Common;
using Telegram.Controls.Cells;
using Telegram.Controls.Media;
using Telegram.Controls.Messages;
using Telegram.Controls.Messages.Service;
using Telegram.Controls.Stories.Widgets;
using Telegram.Converters;
using Telegram.Native;
using Telegram.Native.Calls;
using Telegram.Native.Composition;
using Telegram.Native.Media;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Services.Calls;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.ViewModels.Stories;
using Telegram.Views.Popups;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Telegram.Controls.Stories
{
    public partial class StoryEventArgs : EventArgs
    {
        public ActiveStoriesViewModel ActiveStories { get; }

        public StoryEventArgs(ActiveStoriesViewModel activeStories)
        {
            ActiveStories = activeStories;
        }
    }

    public enum StoryType
    {
        Photo,
        Video,
    }

    // TODO: Live story no stream state
    public sealed partial class StoryContent : UserControlEx
    {
        private volatile bool _unloaded;

        private readonly LayerVisual _messagesVisual;

        public StoryContent()
        {
            InitializeComponent();

            _texture = Texture1;

            _timer = new StoryContentPhotoTimer();
            _timer.Tick += OnTick;

            _messagesVisual = CompositionDevice.GetElementLayerVisual(MessagesHost);

            // Handled ones too: a press on a link or on selectable text is handled by the text
            // before it gets here, and every press still has to stop short of ActiveRoot.
            CaptionPanel.AddHandler(PointerPressedEvent, new PointerEventHandler(CaptionPanel_PointerPressed), true);
            ElementComposition.GetElementVisual(CaptionOverlay).Opacity = 0;

            SizeChanged += OnSizeChanged;
            Disconnected += OnUnloaded;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (IsLooping)
            {
                _timer.Start();
                Progress.Restart();
                return;
            }

            Completed?.Invoke(this, EventArgs.Empty);
        }

        // A story still being posted or edited, or whose post failed, plays again rather than
        // moving on, so that its state stays on screen until it settles.
        private bool IsLooping => _viewModel?.SelectedItem is { IsBeingPosted: true } or { IsBeingEdited: true } or { IsPostFailed: true };

        public event EventHandler<StoryEventArgs> MoreClick;

        public event EventHandler Completed;

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _unloaded = true;
            StopSkeleton();
            Logger.Info();

            if (_player != null)
            {
                _player.VideoOut -= OnVout;
                _player.Buffering -= OnBuffering;
                _player.EndReached -= OnEndReached;
                _player.PositionChanged -= OnPositionChanged;
                _player.Close();
            }

            DiscardGroupCall();

            _captionSelection?.Detach();
            _captionSelection = null;
        }

        private void DiscardGroupCall()
        {
            if (_unifiedVideo != null)
            {
                _unifiedVideo.FrameReceived -= OnFrameReceived;
                _unifiedVideo.Stop();
                _unifiedVideo = null;
            }

            if (_call != null)
            {
                _call.NetworkStateChanged -= OnNetworkStateChanged;
                _call.MessagesChanged -= OnMessagesChanged;
                _call.PinnedMessagesChanged -= OnPinnedMessagesChanged;
                _call.ReactionsChanged -= OnReactionsChanged;
                _call.TopDonorsChanged -= OnTopDonorsChanged;
                _call.StreamerChanged -= OnStreamerChanged;
                _call.Changed -= OnChanged;
                _call.Discard();
                _call = null;
            }
        }

        private void CollapseCaption()
        {
            _captionExpanded = false;
            _captionAnimate = false;
            _state &= ~StoryPauseSource.Caption;

            CaptionScroll.Visibility = Visibility.Collapsed;
            CaptionCollapsed.IsHitTestVisible = true;
            CaptionOverlay.IsHitTestVisible = false;

            var collapsed = ElementComposition.GetElementVisual(CaptionCollapsed);
            collapsed.StopAnimation("Opacity");
            collapsed.Opacity = 1;

            var overlay = ElementComposition.GetElementVisual(CaptionOverlay);
            overlay.StopAnimation("Opacity");
            overlay.Opacity = 0;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var root = ElementComposition.GetElementVisual(Content);
            var compositor = root.Compositor;

            var rect1 = CanvasGeometry.CreateRoundedRectangle(null, 0, 0, ActualSize.X, ActualSize.Y, _open ? 8 : 8 * 2.5f, _open ? 8 : 8 * 2.5f);

            var geometry1 = compositor.CreatePathGeometry(new CompositionPath(rect1));
            var clip1 = compositor.CreateGeometricClip(geometry1);
            root.Clip = clip1;

            if (_loading)
            {
                ShowSkeleton();
            }

            if (_captionExpanded)
            {
                UpdateCaptionMaxHeight();
            }

            UpdateAreas();
        }

        public event RoutedEventHandler Click
        {
            add => InactiveRoot.Click += value;
            remove => InactiveRoot.Click -= value;
        }

        private ActiveStoriesViewModel _viewModel;
        public ActiveStoriesViewModel ViewModel => _viewModel;

        private long _chatId;
        private int _storyId;

        private long _openedChatId;
        private int _openedStoryId;

        private bool _open;
        private int _index;

        public void Update(ActiveStoriesViewModel activeStories, bool open, int index)
        {
            Logger.Info("Start");

            _viewModel = activeStories;
            _index = index;

            if (open)
            {
                _unloaded = false;
            }

            var chat = activeStories.Chat;
            if (chat != null && chat.Id != _chatId)
            {
                _chatId = chat.Id;
                _storyId = 0;

                _state = StoryPauseSource.None;

                if (activeStories.ClientService.TryGetUser(chat, out User user))
                {
                    if (activeStories.IsMyStory)
                    {
                        TitleMini.Text = Strings.SelfStoryTitle;
                        Title.Text = Strings.SelfStoryTitle;

                        Identity.ClearStatus();
                    }
                    else
                    {
                        TitleMini.Text = user.FirstName;
                        Title.Text = user.FirstName;

                        Identity.SetStatus(activeStories.ClientService, user);
                    }

                    PhotoMini.Source = ProfilePictureSource.User(activeStories.ClientService, user);
                    Photo.Source = ProfilePictureSource.User(activeStories.ClientService, user);

                    if (activeStories.SelectedItem?.Content is StoryContentLive && user.ProfilePhoto != null)
                    {
                        BackgroundPhoto.SetSource(activeStories.ClientService, user.ProfilePhoto.Big, user.ProfilePhoto.Minithumbnail, blurRadius: 15);
                    }
                    else
                    {
                        BackgroundPhoto.Clear();
                    }
                }
                else
                {
                    TitleMini.Text = chat.Title;
                    Title.Text = chat.Title;

                    Identity.SetStatus(activeStories.ClientService, chat);

                    PhotoMini.Source = ProfilePictureSource.Chat(activeStories.ClientService, chat);
                    Photo.Source = ProfilePictureSource.Chat(activeStories.ClientService, chat);

                    if (activeStories.SelectedItem?.Content is StoryContentLive && chat.Photo != null)
                    {
                        BackgroundPhoto.SetSource(activeStories.ClientService, chat.Photo.Big, chat.Photo.Minithumbnail, blurRadius: 15);
                    }
                    else
                    {
                        BackgroundPhoto.Clear();
                    }
                }

                if (activeStories.Item != null)
                {
                    SegmentsInactive.UpdateActiveStories(activeStories.Item, 48, true);
                }

                _player?.Context.Clear();
            }

            var story = activeStories.SelectedItem;
            if (story != null && story.Id != _storyId)
            {
                _timer.Stop();
                _state = StoryPauseSource.None;

                var position = activeStories.Items.IndexOf(story);
                var limit = Math.Min(position + 10, activeStories.Items.Count);

                // We go reverse, as download queue is LIFO
                for (int i = activeStories.Items.Count - 1; i >= 0; i--)
                {
                    if (i > position && i < limit)
                    {
                        activeStories.Items[i].Prepare();
                    }
                    else
                    {
                        activeStories.Items[i].Load();
                    }
                }

                Update(story);
            }

            Canvas.SetZIndex(ActiveRoot, open ? 1 : 0);

            var root = ElementComposition.GetElementVisual(Content);
            var mini = ElementComposition.GetElementVisual(MiniInside);

            var inactive = ElementComposition.GetElementVisual(InactiveRoot);
            var active = ElementComposition.GetElementVisual(ActiveRoot);

            var visual = ElementComposition.GetElementVisual(MiniInside);
            visual.CenterPoint = new Vector3(MiniInside.ActualSize / 2, 0);

            mini.Opacity = 1;

            ElementCompositionPreview.SetIsTranslationEnabled(MiniInside, true);

            var compositor = visual.Compositor;

            inactive.Opacity = open ? 0 : 1;
            active.Opacity = open ? 1 : 0;
            visual.Scale = new Vector3(open ? 1 : 2.5f);

            var rect1 = CanvasGeometry.CreateRoundedRectangle(null, 0, 0, ActualSize.X, ActualSize.Y, open ? 8 : 8 * 2.5f, open ? 8 : 8 * 2.5f);

            var geometry1 = compositor.CreatePathGeometry(new CompositionPath(rect1));
            var clip1 = compositor.CreateGeometricClip(geometry1);
            root.Clip = clip1;

            if (story == null || _unloaded)
            {
                return;
            }

            if (open && (story.Id != _storyId || !_open))
            {
                if (story.PosterChatId != _openedChatId || story.Id != _openedStoryId)
                {
                    _viewModel.ClientService.Send(new CloseStory(_openedChatId, _openedStoryId));
                    _openedChatId = 0;
                    _openedStoryId = 0;
                }

                Activate(story);
            }
            else if (_open && !open)
            {
                Deactivate(story);
            }

            _open = open;
            _storyId = story.Id;
        }

        public void UpdateQuality()
        {
            var story = ViewModel?.SelectedItem;
            if (story != null)
            {
                Activate(story);
            }
        }

        /// <summary>
        /// Shows a change in the post state of the story on screen, which keeps its id when it fails.
        /// </summary>
        public void UpdatePostState()
        {
            var story = _viewModel?.SelectedItem;
            if (story != null && story.Id == _storyId)
            {
                UpdateSubtitle(story);
            }
        }

        private void UpdateSubtitle(StoryViewModel story)
        {
            SourceButton.Visibility = Visibility.Collapsed;
            Subtitle.Visibility = Visibility.Visible;

            if (story.Content is StoryContentLive live && story.ClientService.TryGetGroupCall(live.GroupCallId, out GroupCall groupCall))
            {
                LiveBadge.Visibility = Visibility.Visible;
                Subtitle.Text = Locale.Declension(Strings.R.LiveStoryWatching, groupCall.ParticipantCount);
            }
            else if (story.IsPostFailed)
            {
                LiveBadge.Visibility = Visibility.Collapsed;
                Subtitle.Text = Strings.FailedToUploadStory;
            }
            else if (story.IsBeingPosted)
            {
                LiveBadge.Visibility = Visibility.Collapsed;
                Subtitle.Text = Strings.UploadingStory;
            }
            else if (story.Date == 0)
            {
                LiveBadge.Visibility = Visibility.Collapsed;
                Subtitle.Text = string.Format("{0}/{1}", 1 + ViewModel.Items.IndexOf(story), ViewModel.Items.Count);
            }
            else if (UpdateSource(story))
            {
                LiveBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                LiveBadge.Visibility = Visibility.Collapsed;
                Subtitle.Text = story.IsEdited
                    ? Locale.FormatRelativeShort(story.Date) + " · " + Strings.EditedMessage
                    : Locale.FormatRelativeShort(story.Date);
            }

            PinnedIcon.Visibility = ViewModel.IsPinned?.Invoke(story) == true
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (ViewModel.TotalCount != null)
            {
                Position.Text = string.Format("{0}/{1}", 1 + ViewModel.Items.IndexOf(story), ViewModel.TotalCount());
                Position.Visibility = Visibility.Visible;
            }
            else
            {
                Position.Visibility = Visibility.Collapsed;
            }
        }

        // Where the story came from takes the date's place under the poster's name: the story
        // it reposts, the channel post it shares, or, in a group, the member who posted it.
        private bool UpdateSource(StoryViewModel story)
        {
            var clientService = story.ClientService;
            var repost = true;

            string name = null;
            ProfilePictureSource photo = null;

            if (story.RepostInfo?.Origin is StoryOriginPublicStory publicStory)
            {
                if (clientService.TryGetChat(publicStory.ChatId, out Chat chat))
                {
                    name = clientService.GetTitle(chat);
                    photo = ProfilePictureSource.Chat(clientService, chat);
                }
                else
                {
                    name = string.Empty;
                }
            }
            else if (story.RepostInfo?.Origin is StoryOriginHiddenUser hiddenUser)
            {
                name = hiddenUser.PosterName;
            }
            else if (TryGetPostArea(story, out Chat postChat, out _))
            {
                name = clientService.GetTitle(postChat);
                photo = ProfilePictureSource.Chat(clientService, postChat);
            }
            else if (story.PosterId != null && story.Chat?.Type is ChatTypeSupergroup { IsChannel: false })
            {
                repost = false;
                name = clientService.GetTitle(story.PosterId);
                photo = ProfilePictureSource.MessageSender(clientService, story.PosterId);
            }

            if (name == null)
            {
                return false;
            }

            SourceIcon.Visibility = repost
                ? Visibility.Visible
                : Visibility.Collapsed;

            SourcePhoto.Visibility = photo != null
                ? Visibility.Visible
                : Visibility.Collapsed;

            SourcePhoto.Source = photo;
            SourceText.Text = name + " · " + Locale.FormatRelativeShort(story.Date);

            SourceButton.Visibility = Visibility.Visible;
            Subtitle.Visibility = Visibility.Collapsed;
            return true;
        }

        // A story that shares a channel post carries it as an area; only one whose chat is known counts.
        private static bool TryGetPostArea(StoryViewModel story, out Chat chat, out StoryAreaTypeMessage post)
        {
            post = null;

            if (story.Areas != null)
            {
                foreach (var area in story.Areas)
                {
                    if (area.Type is StoryAreaTypeMessage typeMessage)
                    {
                        post = typeMessage;
                    }
                }
            }

            chat = null;
            return post != null && story.ClientService.TryGetChat(post.ChatId, out chat);
        }

        private void Source_Click(object sender, RoutedEventArgs e)
        {
            var story = _viewModel?.SelectedItem;
            if (story == null)
            {
                return;
            }

            if (story.RepostInfo?.Origin is StoryOriginPublicStory publicStory)
            {
                OpenSender(new MessageSenderChat(publicStory.ChatId));
            }
            else if (story.RepostInfo?.Origin is StoryOriginHiddenUser)
            {
                ShowHiddenAccount(SourceButton, ToastPlacementMode.BottomLeft);
            }
            else if (TryGetPostArea(story, out _, out StoryAreaTypeMessage post))
            {
                OpenPost(post);
            }
            else if (story.PosterId != null)
            {
                OpenSender(story.PosterId);
            }
        }

        private void OpenSender(MessageSender sender)
        {
            var parent = this.GetParent<StoriesWindow>();
            if (parent != null)
            {
                parent.TryHide(ModalPopupResult.Primary);
                ViewModel.NavigationService.NavigateToSender(sender);
            }
        }

        private void OpenPost(StoryAreaTypeMessage post)
        {
            var parent = this.GetParent<StoriesWindow>();
            if (parent != null)
            {
                parent.TryHide(ModalPopupResult.Primary);
                ViewModel.NavigationService.NavigateToChat(post.ChatId, message: post.MessageId);
            }
        }

        private void ShowHiddenAccount(FrameworkElement element, ToastPlacementMode placement)
        {
            var window = element.GetParent<StoriesWindow>();
            window?.ShowToast(element, Strings.StoryHidAccount, placement);
        }

        private void Update(StoryViewModel story)
        {
            UpdateSubtitle(story);

            switch (story.PrivacySettings)
            {
                case StoryPrivacySettingsCloseFriends:
                    Privacy.Background = App.Current.Resources["StoryPrivacyCloseFriendsBrush"] as Brush;
                    PrivacyIcon.Text = Icons.StarFilled16;
                    break;
                case StoryPrivacySettingsSelectedUsers:
                    Privacy.Background = App.Current.Resources["StoryPrivacySelectedContactsBrush"] as Brush;
                    PrivacyIcon.Text = Icons.PeopleFilled16;
                    break;
                case StoryPrivacySettingsContacts:
                    Privacy.Background = App.Current.Resources["StoryPrivacyContactsBrush"] as Brush;
                    PrivacyIcon.Text = Icons.PersonCircleFilled16;
                    break;
            }

            PrivacyButton.Visibility =
                Privacy.Visibility = story.PrivacySettings is StoryPrivacySettingsEveryone or null
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            if (story.Content is StoryContentPhoto photoContent)
            {
                var file = photoContent.Photo.GetBig();
                if (file != null)
                {
                    UpdatePhoto(story, file.Photo, true);
                }

                var thumbnail = photoContent.Photo.GetSmall();
                UpdateThumbnail(story, thumbnail?.Photo, photoContent.Photo.Minithumbnail, true);

                Mute.Visibility = Visibility.Collapsed;
                MutePlaceholder.Visibility = Visibility.Collapsed;
            }
            else if (story.Content is StoryContentVideo videoContent)
            {
                var video = SelectVideoFile(videoContent);

                var thumbnail = video.Thumbnail;
                UpdateThumbnail(story, thumbnail?.File, video.Minithumbnail, true);
                UpdateVideo(story, /*video.AlternativeVideo?.Video ??*/ video.Video, true);

                Mute.Visibility = Visibility.Visible;
                MutePlaceholder.Visibility = video.IsAnimation
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                Mute.IsEnabled = !video.IsAnimation;
                Mute.IsChecked = !video.IsAnimation && !AppSettings.VolumeMuted;
            }
            else if (story.Content is StoryContentLive)
            {
                Mute.Visibility = Visibility.Collapsed;
                MutePlaceholder.Visibility = Visibility.Collapsed;
            }
            else if (story.Content is StoryContentUnsupported)
            {
                UpdateUnsupported(story);

                Mute.Visibility = Visibility.Collapsed;
                MutePlaceholder.Visibility = Visibility.Collapsed;
            }

            if (story.Content is StoryContentUnsupported)
            {
                FindName(nameof(UnsupportedPanel));
                UnsupportedPanel.Visibility = Visibility.Visible;
            }
            else if (UnsupportedPanel != null)
            {
                UnsupportedPanel.Visibility = Visibility.Collapsed;
            }

            var empty = string.IsNullOrEmpty(story.Caption?.Text);
            var repost = UpdateRepostPanel(story, empty);

            if (empty)
            {
                CaptionRoot.Visibility = repost
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                Caption.Clear();
                CaptionCollapsed.Clear();

                _captionSource = null;
                _captionText = null;
                _captionTrimmed = false;
            }
            else if (story.Caption != _captionSource)
            {
                _captionSource = story.Caption;
                _captionText = TextStyleRun.GetText(story.Caption);
                _captionClientService = story.ClientService;
                _captionWidth = 0;

                CaptionRoot.Visibility = Visibility.Visible;
                Caption.SetText(story.ClientService, _captionText);

                // Before the panel has a width there is nothing to cut against, and the
                // previous story's caption must not show meanwhile: the first SizeChanged cuts it.
                CaptionCollapsed.Clear();
                UpdateCollapsedCaption(CaptionPanel.ActualWidth - CaptionPanel.Padding.Left - CaptionPanel.Padding.Right);
            }

            UpdateAreas();
        }

        private void UpdateAreas()
        {
            AreasPanel.Children.Clear();

            var story = _viewModel?.SelectedItem;
            if (story?.Areas == null)
            {
                return;
            }

            foreach (var area in story.Areas)
            {
                FrameworkElement element;

                if (area.Type is StoryAreaTypeSuggestedReaction suggestedReaction)
                {
                    var widget = new StoryReactionWidget
                    {
                        IsDark = suggestedReaction.IsDark,
                        IsFlipped = suggestedReaction.IsFlipped
                    };

                    // The button is laid out at a fixed size and scaled into the widget's slot.
                    var button = new StoryReactionButton
                    {
                        Width = 70,
                        Height = 70,
                        Foreground = new SolidColorBrush(widget.TintColor),
                        Style = Resources["SuggestedReactionButtonStyle"] as Style
                    };

                    button.SetSuggestedReaction(story, suggestedReaction.ReactionType);

                    widget.Child = new Viewbox
                    {
                        Child = button
                    };

                    element = widget;
                }
                else if (area.Type is StoryAreaTypeWeather weather)
                {
                    var widget = new StoryWeatherWidget();
                    widget.Update(story.ClientService, weather.Emoji, weather.Temperature, weather.BackgroundColor.ToColor(true));

                    element = new Viewbox
                    {
                        Child = widget
                    };
                }
                else
                {
                    var button = new HyperlinkButton();
                    button.Click += Area_Click;

                    element = button;
                }

                element.Width = area.Position.WidthPercentage / 100 * ActualWidth;
                element.Height = area.Position.HeightPercentage / 100 * ActualHeight;
                element.RenderTransformOrigin = new Point(0.5f, 0.5f);
                element.RenderTransform = new RotateTransform
                {
                    Angle = area.Position.RotationAngle
                };

                if (element is Control control)
                {
                    control.CornerRadius = new CornerRadius(area.Position.CornerRadiusPercentage / 100 * ActualWidth);
                }

                Canvas.SetLeft(element, area.Position.XPercentage / 100 * ActualWidth - element.Width / 2);
                Canvas.SetTop(element, area.Position.YPercentage / 100 * ActualHeight - element.Height / 2);

                AreasPanel.Children.Add(element);

                element.Tag = area;
            }
        }

        /// <summary>
        /// Redraws the suggested reactions of the story shown, in place, so that an effect playing
        /// on one carries on.
        /// </summary>
        public void UpdateSuggestedReactions(StoryViewModel story)
        {
            if (story != _viewModel?.SelectedItem)
            {
                return;
            }

            foreach (var element in AreasPanel.Children)
            {
                if (element is StoryReactionWidget widget && widget.Child is Viewbox { Child: StoryReactionButton button } && widget.Tag is StoryArea { Type: StoryAreaTypeSuggestedReaction suggestedReaction })
                {
                    button.SetSuggestedReaction(story, suggestedReaction.ReactionType);
                }
            }
        }

        private async void Area_Click(object sender, RoutedEventArgs e)
        {
            if (sender is HyperlinkButton element && element.Tag is StoryArea area)
            {
                var target = element.Content as FrameworkElement ?? element;

                if (area.Type is StoryAreaTypeLocation or StoryAreaTypeVenue)
                {
                    var text = new TextBlock();
                    text.Inlines.Add(new Run
                    {
                        Text = Strings.StoryViewLocation,
                        Foreground = new SolidColorBrush(Colors.White)
                    });

                    var window = element.GetParent<StoriesWindow>();
                    var result = await window?.ShowActionAsync(target, text, ToastPlacementMode.Top);

                    if (result == ModalPopupResult.Primary)
                    {
                        try
                        {
                            Location location = area.Type switch
                            {
                                StoryAreaTypeLocation typeLocation => typeLocation.Location,
                                StoryAreaTypeVenue typeVenue => typeVenue.Venue.Location,
                                _ => null
                            };

                            await Windows.System.Launcher.LaunchUriAsync(new Uri(string.Format(CultureInfo.InvariantCulture, "https://www.google.com/maps/search/?api=1&query={0},{1}", location.Latitude, location.Longitude)));
                        }
                        catch
                        {
                            // All the remote procedure calls must be wrapped in a try-catch block
                        }
                    }
                }
                else if (area.Type is StoryAreaTypeMessage typeMessage)
                {
                    var text = new TextBlock();
                    text.Inlines.Add(new Run
                    {
                        Text = Strings.StoryViewMessage,
                        Foreground = new SolidColorBrush(Colors.White)
                    });

                    var window = element.GetParent<StoriesWindow>();
                    var result = await window?.ShowActionAsync(target, text, ToastPlacementMode.Top);

                    if (result == ModalPopupResult.Primary)
                    {
                        ViewModel.NavigationService.NavigateToChat(typeMessage.ChatId, message: typeMessage.MessageId);
                    }
                }
                else if (area.Type is StoryAreaTypeLink typeLink)
                {
                    var text = new TextBlock();
                    text.Inlines.Add(new Run
                    {
                        Text = Strings.StoryOpenLink,
                        Foreground = new SolidColorBrush(Colors.White)
                    });
                    text.Inlines.Add(new LineBreak());
                    text.Inlines.Add(new Run
                    {
                        Text = typeLink.Url,
                        FontSize = 12,
                        FontWeight = FontWeights.Normal,
                        Foreground = BootStrapper.Current.Resources["SystemControlDisabledChromeDisabledLowBrush"] as Brush
                    });

                    var window = element.GetParent<StoriesWindow>();
                    var result = await window?.ShowActionAsync(target, text, ToastPlacementMode.Top);

                    if (result == ModalPopupResult.Primary)
                    {
                        MessageHelper.OpenUrl(ViewModel.ClientService, ViewModel.NavigationService, typeLink.Url);
                    }
                }
                else if (area.Type is StoryAreaTypeUpgradedGift typeUpgradedGift)
                {
                    var text = new TextBlock();
                    text.Inlines.Add(new Run
                    {
                        Text = Strings.StoryViewGift,
                        Foreground = new SolidColorBrush(Colors.White)
                    });

                    var window = element.GetParent<StoriesWindow>();
                    var result = await window?.ShowActionAsync(target, text, ToastPlacementMode.Top);

                    if (result == ModalPopupResult.Primary)
                    {
                        MessageHelper.OpenTelegramUrl(ViewModel.ClientService, ViewModel.NavigationService, new InternalLinkTypeUpgradedGift(typeUpgradedGift.GiftName));
                    }
                }
            }
        }

        public void Animate(int from, int to)
        {
            if (from != 3 && to != 3)
            {
                return;
            }

            Canvas.SetZIndex(ActiveRoot, to == 3 ? 1 : 0);

            var root = ElementComposition.GetElementVisual(Content);
            var mini = ElementComposition.GetElementVisual(MiniInside);

            var inactive = ElementComposition.GetElementVisual(InactiveRoot);
            var active = ElementComposition.GetElementVisual(ActiveRoot);

            var visual = ElementComposition.GetElementVisual(MiniInside);
            visual.CenterPoint = new Vector3(MiniInside.ActualSize / 2, 0);

            mini.Opacity = 1;

            ElementCompositionPreview.SetIsTranslationEnabled(MiniInside, true);

            var compositor = visual.Compositor;

            var opacityOut = compositor.CreateScalarKeyFrameAnimation();
            //opacityOut.InsertKeyFrame(to == 3 ? 0 : 1, 1);
            //opacityOut.InsertKeyFrame(to == 3 ? 1 : 0, 0);

            opacityOut.InsertKeyFrame(1, to == 3 ? 0 : 1);

            var opacityIn = compositor.CreateScalarKeyFrameAnimation();
            //opacityIn.InsertKeyFrame(to == 3 ? 0 : 1, 0);
            //opacityIn.InsertKeyFrame(to == 3 ? 1 : 0, 1);

            opacityIn.InsertKeyFrame(1, to == 3 ? 1 : 0);

            inactive.StartAnimation("Opacity", opacityOut);
            active.StartAnimation("Opacity", opacityIn);

            var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
            //scale.InsertKeyFrame(to == 3 ? 1 : 0, new Vector3(36f / 48f));
            //scale.InsertKeyFrame(to == 3 ? 0 : 1, new Vector3(2.5f));

            scale.InsertKeyFrame(1, new Vector3(to == 3 ? 36f / 48f : 2.5f));

            //scale.Duration = TimeSpan.FromSeconds(1);

            visual.StartAnimation("Scale", scale);

            var rect1 = CanvasGeometry.CreateRoundedRectangle(null, 0, 0, ActualSize.X, ActualSize.Y, 8, 8);
            var rect2 = CanvasGeometry.CreateRoundedRectangle(null, 0, 0, ActualSize.X, ActualSize.Y, 8 * 2.5f, 8 * 2.5f);

            var geometry1 = compositor.CreatePathGeometry(new CompositionPath(rect1));
            var clip1 = compositor.CreateGeometricClip(geometry1);

            var pathAnimation = compositor.CreatePathKeyFrameAnimation();
            pathAnimation.InsertKeyFrame(to == 3 ? 1 : 0, new CompositionPath(rect1));
            pathAnimation.InsertKeyFrame(to == 3 ? 0 : 1, new CompositionPath(rect2));

            geometry1.StartAnimation("Path", pathAnimation);
            root.Clip = clip1;
        }

        private void Play(StoryVideo stream)
        {
            if (_player == null || Video == null)
            {
                InitializeVideo();
            }

            if (_player != null && !_unloaded && _viewModel != null && stream != null)
            {
                // The player reports no buffering while it opens, only once it is ready to play,
                // and a preloaded prefix does not mean it is.
                ShowSkeleton();

                _player.Play(new RemoteFileSource(_viewModel.ClientService, stream.Video));
            }
        }

        // The live story's call belongs to this viewer, not to the story, which other windows share.
        private VoipGroupCall _call;
        public VoipGroupCall GroupCall => _call;

        private ObservableCollection<GroupCallMessage> _messages;
        private SortedObservableCollection<GroupCallMessage> _pinnedMessages;
        private Dictionary<MessageSender, int> _topDonors;

        private void Activate(StoryViewModel story)
        {
            CollapseCaption();
            DiscardGroupCall();

            _buffering = false;

            if (story.Content is StoryContentVideo videoContent && !_unloaded)
            {
                var video = SelectVideoFile(videoContent);

                _progressStarted = false;

                UpdateProgress(video.Duration);
                Progress.Suspend();
                Play(video);

                MessagesRoot.Visibility = Visibility.Collapsed;
            }
            else if (story.Content is StoryContentLive live && story.ClientService.TryGetGroupCall(live.GroupCallId, out GroupCall groupCall) && !_unloaded)
            {
                _timer.Stop();
                Progress.Update(-1, 1, 0);

                Suspend(StoryPauseSource.Live);

                _call = new VoipGroupCall(ViewModel.ClientService, ViewModel.Settings, ViewModel.Aggregator, XamlRoot, ViewModel.Chat, groupCall, null, null, true);
                _call.NetworkStateChanged += OnNetworkStateChanged;
                _call.MessagesChanged += OnMessagesChanged;
                _call.PinnedMessagesChanged += OnPinnedMessagesChanged;
                _call.ReactionsChanged += OnReactionsChanged;
                _call.TopDonorsChanged += OnTopDonorsChanged;
                _call.StreamerChanged += OnStreamerChanged;
                _call.Changed += OnChanged;

                if (_call.IsRtmpStream)
                {
                    _call.AddIncomingVideoOutput("unified", _unifiedVideo = VoipVideoOutput.CreateSink(LayoutRoot, uniformToFill: true));
                    _unifiedVideo.FrameReceived += OnFrameReceived;
                }
                else
                {
                    UpdateStreamer(_call.Streamer);
                }

                _topDonors = new Dictionary<MessageSender, int>(new MessageSenderEqualityComparer());

                _messages = new ObservableCollection<GroupCallMessage>(_call.Messages);
                MessagesHost.ItemsSource = _messages;

                _pinnedMessages = new SortedObservableCollection<GroupCallMessage>(_call.PinnedMessages, new GroupCallMessageComparer());
                PinnedMessagesHost.ItemsSource = _pinnedMessages;

                MessagesRoot.Visibility = Visibility.Visible;

                ShowSkeleton();
            }
            else if (!_loading && !_unloaded)
            {
                _timer.Stop();
                _timer.Start();
                UpdateProgress(StoryContentPhotoTimer.Duration);

                MessagesRoot.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateProgress(double duration)
        {
            var items = _viewModel.Items;
            var index = items.IndexOf(_viewModel.SelectedItem);

            var group = _viewModel.SegmentGroup;
            if (group == null || index == -1)
            {
                Progress.Update(index, items.Count, duration);
                return;
            }

            // Lists are in date order with pinned stories first, so a group is one run of the list.
            var key = group(items[index]);
            var first = index;
            var last = index;

            while (first > 0 && group(items[first - 1]) == key)
            {
                first--;
            }

            while (last < items.Count - 1 && group(items[last + 1]) == key)
            {
                last++;
            }

            Progress.Update(index - first, last - first + 1, duration);
        }

        private void OnFrameReceived(VoipVideoOutputSink sender, FrameReceivedEventArgs args)
        {
            _unifiedVideo?.FrameReceived -= OnFrameReceived;

            this.BeginOnUIThread(HideSkeleton);
        }

        private VoipVideoOutputSink _unifiedVideo;

        private void OnNetworkStateChanged(VoipGroupCall sender, VoipGroupCallNetworkStateChangedEventArgs args)
        {
            // TODO
        }

        private void OnMessagesChanged(VoipGroupCall sender, VoipGroupCallMessagesChangedEventArgs args)
        {
            this.BeginOnUIThread(() => UpdateMessages(args.Message, args.Deleted));
        }

        private void OnPinnedMessagesChanged(VoipGroupCall sender, VoipGroupCallMessagesChangedEventArgs args)
        {
            this.BeginOnUIThread(() => UpdatePinnedMessages(args.Message, args.Deleted));
        }

        private void OnReactionsChanged(VoipGroupCall sender, VoipGroupCallReactionsChangedEventArgs args)
        {
            this.BeginOnUIThread(() => ReactionsHost.Add(sender.ClientService, args.SenderId, args.StarCount));
        }

        private void OnTopDonorsChanged(VoipGroupCall sender, VoipGroupCallTopDonorsChangedEventArgs args)
        {
            this.BeginOnUIThread(() => UpdateTopDonors(args.Donors));
        }

        private void OnStreamerChanged(VoipGroupCall sender, VoipGroupCallStreamerChangedEventArgs args)
        {
            this.BeginOnUIThread(() => UpdateStreamer(args.Streamer));
        }

        private void UpdateStreamer(GroupCallParticipant participant)
        {
            if (_unifiedVideo != null)
            {
                _unifiedVideo.FrameReceived -= OnFrameReceived;
                _unifiedVideo.Stop();
                _unifiedVideo = null;
            }

            if (participant?.VideoInfo != null)
            {
                var channelInfo = new VoipVideoChannelInfo(participant.AudioSourceId, participant.ParticipantId.ToId(), participant.VideoInfo.EndpointId, participant.VideoInfo.SourceGroups.ToCalls(), VoipVideoChannelQuality.Full, VoipVideoChannelQuality.Full);

                // A List, not a collection expression: the compiler synthesises its own read-only
                // list type for those, and CsWinRT has no CCW for it to hand across as IVector<T>.
                _call.SetRequestedVideoChannels(new List<VoipVideoChannelInfo> { channelInfo });
                _call.AddIncomingVideoOutput(channelInfo.EndpointId, _unifiedVideo = VoipVideoOutput.CreateSink(LayoutRoot, uniformToFill: true));

                _unifiedVideo.FrameReceived += OnFrameReceived;
            }
        }

        private void OnChanged(object sender, EventArgs e)
        {
            if (sender is VoipGroupCall groupCall)
            {
                this.BeginOnUIThread(() =>
                {
                    Subtitle.Text = Locale.Declension(Strings.R.LiveStoryWatching, groupCall.ParticipantCount);
                });
            }
        }

        private void UpdateMessages(GroupCallMessage message, bool expired)
        {
            if (expired)
            {
                _messages.Remove(message);
            }
            else
            {
                _messages.Add(message);

                //if (message.PaidMessageStarCount > 0)
                //{
                //    ReactionsHost.Add(ViewModel.ClientService, message.SenderId, message.PaidMessageStarCount);
                //}
            }
        }

        private void UpdatePinnedMessages(GroupCallMessage message, bool expired)
        {
            if (expired)
            {
                _pinnedMessages.Remove(message);
            }
            else
            {
                _pinnedMessages.Add(message);
            }
        }

        private void UpdateTopDonors(Vector<PaidReactor> donors)
        {
            var diff = new Dictionary<MessageSender, int>(_topDonors.Count + donors.Count, new MessageSenderEqualityComparer());

            foreach (var donor in _topDonors)
            {
                diff[donor.Key] = 0;
            }

            for (int i = 0; i < donors.Count; i++)
            {
                var donor = donors[i].SenderId;
                var newPosition = i + 1;

                if (_topDonors.TryGetValue(donor, out int oldPosition))
                {
                    if (oldPosition == newPosition)
                    {
                        diff.Remove(donor);
                    }
                    else
                    {
                        diff[donor] = newPosition;
                    }
                }
                else
                {
                    diff[donor] = newPosition;
                }
            }

            _topDonors.Clear();

            for (int i = 0; i < donors.Count; i++)
            {
                _topDonors[donors[i].SenderId] = i + 1;
            }

            MessagesHost.ForEach<LiveStoryMessageCell, GroupCallMessage>((content, message) =>
            {
                if (diff.TryGetValue(message.SenderId, out int position))
                {
                    content.Update(ViewModel.ClientService, message, position);
                }
            });

            PinnedMessagesHost.ForEach<LiveStoryPinnedMessageCell, GroupCallMessage>((content, message) =>
            {
                if (diff.TryGetValue(message.SenderId, out int position))
                {
                    content.Update(ViewModel.ClientService, message, position);
                }
            });
        }

        private void Deactivate(StoryViewModel story)
        {
            _player?.Stop();

            //UnloadVideo();
            DiscardGroupCall();
            CollapseCaption();

            if (_openedChatId == story.PosterChatId && _openedStoryId == story.Id)
            {
                _viewModel.ClientService.Send(new CloseStory(story.PosterChatId, story.Id));
                _openedChatId = 0;
                _openedStoryId = 0;
            }
        }

        private void Photo_Click(object sender, RoutedEventArgs e)
        {
            var parent = this.GetParent<StoriesWindow>();
            if (parent != null)
            {
                parent.TryHide(ModalPopupResult.Primary);
                parent.ViewModel.OpenPoster(ViewModel);
            }
        }



        private ThumbnailController _thumbnailController;

        private long _fileToken;
        private long _thumbnailToken;

        private void UpdateThumbnail(File file)
        {
            UpdateThumbnail(_viewModel.SelectedItem, file, null, false);
        }

        private void UpdatePhoto(File file)
        {
            UpdatePhoto(_viewModel.SelectedItem, file, false);
        }

        private void UpdateVideo(File file)
        {
            UpdateVideo(_viewModel.SelectedItem, file, false);
        }

        private StoryVideo SelectVideoFile(StoryContentVideo video)
        {
            //if (video.AlternativeVideo == null || (AppSettings.Playback.HighQuality && _viewModel.IsPremium))
            {
                return video.Video;
            }

            return video.AlternativeVideo;
        }

        private void UpdateThumbnail(StoryViewModel story, File file, Minithumbnail minithumbnail, bool download)
        {
            if (file?.Id == _thumbnailId && download)
            {
                return;
            }

            _thumbnailId = file?.Id ?? 0;
            _thumbnailController ??= new ThumbnailController(ThumbnailTexture);

            if (file != null)
            {
                if (file.Local.IsDownloadingCompleted)
                {
                    _thumbnailController.Blur(file.Local.Path, 3, 0);
                }
                else
                {
                    if (download)
                    {
                        if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
                        {
                            story.ClientService.DownloadFile(file.Id, 1);
                        }

                        UpdateManager.Subscribe(this, story.ClientService, file, ref _thumbnailToken, UpdateThumbnail, true);
                    }

                    if (minithumbnail != null)
                    {
                        _thumbnailController.Blur(minithumbnail.Data, 3, 0);
                    }
                    else
                    {
                        _thumbnailController.Recycle();
                    }
                }
            }
            else if (minithumbnail != null)
            {
                _thumbnailController.Blur(minithumbnail.Data, 3, 0);
            }
            else
            {
                _thumbnailController.Recycle();
            }
        }

        private void UpdatePhoto(StoryViewModel story, File file, bool download)
        {
            if (file.Id == _fileId && download)
            {
                return;
            }

            UpdateManager.Unsubscribe(this, ref _fileToken);

            _fileId = file.Id;
            Logger.Info();

            _player?.Stop();

            if (_type == StoryType.Photo)
            {
                _player?.Context.Clear();
            }

            if (file.Local.IsDownloadingCompleted)
            {
                var next = _texture == Texture1 ? Texture2 : Texture1;
                var prev = _texture;

                Canvas.SetZIndex(next, 1);
                Canvas.SetZIndex(prev, _type == StoryType.Photo ? 0 : -1);
                Canvas.SetZIndex(VideoPanel, _type == StoryType.Video ? 0 : -1);

                next.Source = UriEx.ToBitmap(file.Local.Path, 0, 0);
            }
            else if (download)
            {
                ShowSkeleton();

                // The thumbnail stands in until the photo is downloaded: see UpdateVideo.
                Texture1.Source = null;
                Texture2.Source = null;

                _player?.Context.Clear();

                if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
                {
                    story.ClientService.DownloadFile(file.Id, 1);
                }

                UpdateManager.Subscribe(this, _viewModel.ClientService, file, ref _fileToken, UpdatePhoto, true);
            }

            _type = StoryType.Photo;
        }

        // Nothing to draw, so nothing of the story before may stay on screen: it runs on the
        // photo timer, as a story that can't be played still has its turn.
        private void UpdateUnsupported(StoryViewModel story)
        {
            UpdateManager.Unsubscribe(this, ref _fileToken);

            _fileId = 0;

            Texture1.Source = null;
            Texture2.Source = null;

            _player?.Stop();
            _player?.Context.Clear();

            UpdateThumbnail(story, null, null, true);
            HideSkeleton();

            _type = StoryType.Photo;
        }

        private void Unsupported_Click(object sender, RoutedEventArgs e)
        {
            _ = MessageUnsupportedContent.CheckForUpdatesAsync(XamlRoot, _viewModel?.ClientService);
        }

        private StoryType _type;

        private int _fileId;
        private int _thumbnailId;

        private void UpdateVideo(StoryViewModel story, File file, bool download)
        {
            if (file.Id == _fileId && download)
            {
                return;
            }

            UpdateManager.Unsubscribe(this, ref _fileToken);

            _fileId = file.Id;
            //Player.Source = null;

            Logger.Info();

            //_player?.Stop();

            //if (_mediaStream != null)
            //{
            //    _mediaStream.Dispose();
            //    _mediaStream = null;
            //}

            var videoContent = story.Content as StoryContentVideo;
            var video = SelectVideoFile(videoContent);

            //_loading = true;
            //ShowSkeleton();

            var prev = _texture == Texture1 ? Texture2 : Texture1;
            var next = _texture;

            Canvas.SetZIndex(VideoPanel, 1);
            Canvas.SetZIndex(next, 0);
            Canvas.SetZIndex(prev, -1);

            // The thumbnail stands in until the first frame. It is LayoutRoot's background, which
            // every child draws over whatever its ZIndex, so the story before must leave nothing
            // behind: its player is stopped first, or it keeps presenting frames until Play
            // replaces it, over the panel just cleared.
            Texture1.Source = null;
            Texture2.Source = null;

            _player?.Stop();
            _player?.Context.Clear();

            story.ClientService.DownloadFile(file.Id, 32, 0, video.PreloadPrefixSize);

            _type = StoryType.Video;
        }

        private bool _hidden = false;

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed)
            {
                _hidden = true;

                var active = ElementComposition.GetElementVisual(ActiveRoot);
                var opacity = active.Compositor.CreateScalarKeyFrameAnimation();
                opacity.InsertKeyFrame(0, 1);
                opacity.InsertKeyFrame(1, 0);

                active.StartAnimation("Opacity", opacity);

                Suspend(StoryPauseSource.Interaction);
            }
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_hidden)
            {
                _hidden = false;
                var active = ElementComposition.GetElementVisual(ActiveRoot);
                var opacity = active.Compositor.CreateScalarKeyFrameAnimation();
                opacity.InsertKeyFrame(0, 0);
                opacity.InsertKeyFrame(1, 1);

                active.StartAnimation("Opacity", opacity);

                Resume(StoryPauseSource.Interaction);
            }
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            MoreClick?.Invoke(sender, new StoryEventArgs(ViewModel));
        }

        private void ShowSkeleton()
        {
            _loading = true;
            StopSkeleton();

            if (ActualSize.X == 0 || ActualSize.Y == 0)
            {
                return;
            }

            Logger.Debug("ShowSkeleton " + _viewModel.ChatId);

            var compositor = BootStrapper.Current.Compositor;
            var rectangle = compositor.CreateRoundedRectangleGeometry();
            rectangle.Size = new Vector2(ActualSize.X - 2, ActualSize.Y - 2);
            rectangle.Offset = new Vector2(1, 1);
            rectangle.CornerRadius = new Vector2(8);

            var stroke = compositor.CreateLinearGradientBrush();
            stroke.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Color.FromArgb(0x00, 0xff, 0xff, 0xff)));
            stroke.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, Color.FromArgb(0x55, 0xff, 0xff, 0xff)));
            stroke.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Color.FromArgb(0x00, 0xff, 0xff, 0xff)));

            var fill = compositor.CreateLinearGradientBrush();
            fill.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Color.FromArgb(0x00, 0xff, 0xff, 0xff)));
            fill.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, Color.FromArgb(0x22, 0xff, 0xff, 0xff)));
            fill.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Color.FromArgb(0x00, 0xff, 0xff, 0xff)));

            var shape = compositor.CreateSpriteShape();
            shape.Geometry = rectangle;
            shape.FillBrush = fill;
            shape.StrokeBrush = stroke;
            shape.StrokeThickness = 2;

            var visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2(ActualSize.X, ActualSize.Y);
            visual.Shapes.Add(shape);

            var endless = compositor.CreateScalarKeyFrameAnimation();
            endless.InsertKeyFrame(0, -ActualSize.X);
            endless.InsertKeyFrame(1, +ActualSize.X);
            endless.IterationBehavior = AnimationIterationBehavior.Forever;
            endless.Duration = TimeSpan.FromMilliseconds(2000);

            stroke.StartAnimation("Offset.X", endless);
            fill.StartAnimation("Offset.X", endless);

            _skeletonStroke = stroke;
            _skeletonFill = fill;

            ElementCompositionPreview.SetElementChildVisual(ActiveRoot, visual);
        }

        private void HideSkeleton()
        {
            _loading = false;
            StopSkeleton();
            ElementCompositionPreview.SetElementChildVisual(ActiveRoot, BootStrapper.Current.Compositor.CreateSpriteVisual());
        }

        // Replacing the child visual does not stop these: they run until the brushes are finalized.
        private CompositionLinearGradientBrush _skeletonStroke;
        private CompositionLinearGradientBrush _skeletonFill;

        private void StopSkeleton()
        {
            _skeletonStroke?.StopAnimation("Offset.X");
            _skeletonFill?.StopAnimation("Offset.X");

            _skeletonStroke = null;
            _skeletonFill = null;
        }

        private bool _loading;

        private void Texture_ImageOpened(object sender, RoutedEventArgs e)
        {
            Logger.Debug("ImageOpened " + _viewModel.ChatId);

            HideSkeleton();

            _player?.Context.Clear();

            if (_open)
            {
                _timer.Stop();
                _timer.Start();

                if (_viewModel?.SelectedItem != null && _viewModel.SelectedItem.PosterChatId != _openedChatId && _viewModel.SelectedItem.Id != _openedStoryId)
                {
                    _openedChatId = _viewModel.SelectedItem.PosterChatId;
                    _openedStoryId = _viewModel.SelectedItem.Id;
                    _viewModel.ClientService.Send(new OpenStory(_openedChatId, _openedStoryId));
                }
            }
        }

        public void TryStart(StoryOpenOrigin ciccio, Windows.Foundation.Rect origin, bool show = true)
        {
            var transform = TransformToVisual(null);
            var point = transform.TransformVector2();

            if (origin.IsEmpty && XamlRoot.Content is FrameworkElement root)
            {
                origin = new Windows.Foundation.Rect((float)(root.ActualWidth / 2), (float)root.ActualHeight, 48, 48);
            }

            var reoffset = new Vector2((float)origin.X, (float)origin.Y);
            var resize = new Vector2((float)origin.Width, (float)origin.Height);
            var relativeX = reoffset.X - (point.X + 8);
            var relativeY = reoffset.Y - (point.Y + 18);

            var photo = ElementComposition.GetElementVisual(Photo);
            var layout = ElementComposition.GetElementVisual(LayoutRoot);
            var caption = ElementComposition.GetElementVisual(CaptionPanel);
            var visual = ElementComposition.GetElementVisual(Content);
            ElementCompositionPreview.SetIsTranslationEnabled(CaptionPanel, true);
            ElementCompositionPreview.SetIsTranslationEnabled(Content, true);

            if (ciccio == StoryOpenOrigin.ProfilePhoto)
            {
                visual.Properties.InsertVector3("Translation", new Vector3(relativeX, relativeY, 0));
                //layout.CenterPoint = new Vector3(8 + 16, 18 + 16, 0);
                layout.CenterPoint = new Vector3(8, 18, 0);
                layout.Scale = new Vector3(resize.X / (ActualSize.X - 8));

                photo.Scale = new Vector3(resize.X / 32f, resize.Y / 32f, 0);

                var compositor = BootStrapper.Current.Compositor;

                var rect = compositor.CreateRoundedRectangleGeometry();
                rect.Size = new Vector2(resize.X, resize.Y);
                rect.Offset = new Vector2(8, 18);
                rect.CornerRadius = resize / 2;

                visual.Clip = compositor.CreateGeometricClip(rect);

                var size = compositor.CreateVector2KeyFrameAnimation();
                size.InsertKeyFrame(show ? 0 : 1, new Vector2(resize.X, resize.Y));
                size.InsertKeyFrame(show ? 1 : 0, new Vector2(ActualSize.X, ActualSize.Y));
                //size.Duration = TimeSpan.FromSeconds(5);

                var offset = compositor.CreateVector2KeyFrameAnimation();
                offset.InsertKeyFrame(show ? 0 : 1, new Vector2(8, 18));
                offset.InsertKeyFrame(show ? 1 : 0, new Vector2(0, 0));
                //offset.Duration = TimeSpan.FromSeconds(5);

                var cornerRadius = compositor.CreateVector2KeyFrameAnimation();
                cornerRadius.InsertKeyFrame(show ? 0 : 1, new Vector2(resize.X / 2));
                cornerRadius.InsertKeyFrame(show ? 1 : 0, new Vector2(8, 8));
                //cornerRadius.Duration = TimeSpan.FromSeconds(5);

                var translation = compositor.CreateVector3KeyFrameAnimation();
                translation.InsertKeyFrame(show ? 0 : 1, new Vector3(relativeX, relativeY, 0));
                translation.InsertKeyFrame(show ? 1 : 0, new Vector3());
                //translation.Duration = TimeSpan.FromSeconds(5);

                var entranceY = compositor.CreateScalarKeyFrameAnimation();
                entranceY.InsertKeyFrame(show ? 0 : 1, -CaptionCollapsed.ActualSize.Y * 5);
                entranceY.InsertKeyFrame(show ? 1 : 0, 0);
                //entranceY.Duration = TimeSpan.FromSeconds(5);

                var scale = compositor.CreateVector3KeyFrameAnimation();
                scale.InsertKeyFrame(show ? 0 : 1, new Vector3(resize.X / 32f));
                scale.InsertKeyFrame(show ? 1 : 0, new Vector3(1));
                //scale.Duration = TimeSpan.FromSeconds(5);

                var scale2 = compositor.CreateVector3KeyFrameAnimation();
                scale2.InsertKeyFrame(show ? 0 : 1, new Vector3(resize.X / (ActualSize.X - 8)));
                scale2.InsertKeyFrame(show ? 1 : 0, new Vector3(1));
                //scale2.Duration = TimeSpan.FromSeconds(5);

                rect.StartAnimation("Size", size);
                rect.StartAnimation("Offset", offset);
                rect.StartAnimation("CornerRadius", cornerRadius);
                visual.StartAnimation("Translation", translation);
                caption.StartAnimation("Translation.Y", entranceY);
                layout.StartAnimation("Scale", scale2);
                photo.StartAnimation("Scale", scale);
            }
            else
            {
                visual.Properties.InsertVector3("Translation", new Vector3(relativeX + 8 + resize.X / 2 - ActualSize.X / 2, relativeY + 18 + resize.Y / 2 - ActualSize.Y / 2, 0));

                layout.CenterPoint = new Vector3(ActualSize / 2, 0);
                layout.Scale = new Vector3(resize.X / ActualSize.X, resize.X / ActualSize.X, 1);

                var compositor = BootStrapper.Current.Compositor;

                var rect = compositor.CreateRoundedRectangleGeometry();
                rect.Size = new Vector2(resize.X, resize.Y);
                rect.Offset = (ActualSize - rect.Size) / 2;
                rect.CornerRadius = ciccio == StoryOpenOrigin.Mention ? resize / 2 : new Vector2(4, 4);

                visual.Clip = compositor.CreateGeometricClip(rect);

                var size = compositor.CreateVector2KeyFrameAnimation();
                size.InsertKeyFrame(show ? 0 : 1, new Vector2(resize.X, resize.Y));
                size.InsertKeyFrame(show ? 1 : 0, new Vector2(ActualSize.X, ActualSize.Y));
                //size.Duration = TimeSpan.FromSeconds(5);

                var offset = compositor.CreateVector2KeyFrameAnimation();
                offset.InsertKeyFrame(show ? 0 : 1, (ActualSize - rect.Size) / 2);
                offset.InsertKeyFrame(show ? 1 : 0, new Vector2(0, 0));
                //offset.Duration = TimeSpan.FromSeconds(5);

                var cornerRadius = compositor.CreateVector2KeyFrameAnimation();
                cornerRadius.InsertKeyFrame(show ? 0 : 1, ciccio == StoryOpenOrigin.Mention ? resize / 2 : new Vector2(4, 4));
                cornerRadius.InsertKeyFrame(show ? 1 : 0, new Vector2(8, 8));
                //cornerRadius.Duration = TimeSpan.FromSeconds(5);

                var translation = compositor.CreateVector3KeyFrameAnimation();
                translation.InsertKeyFrame(show ? 0 : 1, new Vector3(relativeX + 8 + resize.X / 2 - ActualSize.X / 2, relativeY + 18 + resize.Y / 2 - ActualSize.Y / 2, 0));
                translation.InsertKeyFrame(show ? 1 : 0, new Vector3());
                //translation.Duration = TimeSpan.FromSeconds(5);

                var entranceY = compositor.CreateScalarKeyFrameAnimation();
                entranceY.InsertKeyFrame(show ? 0 : 1, -CaptionCollapsed.ActualSize.Y * 5);
                entranceY.InsertKeyFrame(show ? 1 : 0, 0);
                //entranceY.Duration = TimeSpan.FromSeconds(5);

                var scale2 = compositor.CreateVector3KeyFrameAnimation();
                scale2.InsertKeyFrame(show ? 0 : 1, new Vector3(resize.X / ActualSize.X, resize.X / ActualSize.X, 1));
                scale2.InsertKeyFrame(show ? 1 : 0, new Vector3(1));
                //scale2.Duration = TimeSpan.FromSeconds(5);

                rect.StartAnimation("Size", size);
                rect.StartAnimation("Offset", offset);
                rect.StartAnimation("CornerRadius", cornerRadius);
                //visual.StartAnimation("Translation.X", translationX);
                //visual.StartAnimation("Translation.Y", translationY);
                visual.StartAnimation("Translation", translation);
                caption.StartAnimation("Translation.Y", entranceY);
                layout.StartAnimation("Scale", scale2);
            }
        }

        private void InitializeVideo()
        {
            if (_unloaded)
            {
                return;
            }

            Logger.Info();

            FindName(nameof(Video));

            var options = new AsyncMediaPlayerOptions
            {
                CreateSwapChain = true,
                Mute = AppSettings.VolumeMuted,
                Volume = AppSettings.VolumeLevel,
            };

            try
            {
                _player = new AsyncMediaPlayer(options);
            }
            catch (Exception ex)
            {
                // Out of memory, most likely. Every member of this class already tolerates a null
                // player, so the story stays on its cover image instead of faulting on the next
                // call into it.
                Logger.Error(ex);
                return;
            }

            _player.VideoOut += OnVout;
            _player.Buffering += OnBuffering;
            _player.EndReached += OnEndReached;
            _player.PositionChanged += OnPositionChanged;

            _player.Context.Attach(Video, true);
        }

        private void OnVout(AsyncMediaPlayer sender, object e)
        {
            HideSkeleton();

            Texture1.Source = null;
            Texture2.Source = null;
        }

        private void OnEndReached(AsyncMediaPlayer sender, object e)
        {
            if (IsLooping)
            {
                // Play restarts an ended player by itself.
                sender.Play();

                _progressStarted = false;
                Progress.Restart();
                Progress.Suspend();
                return;
            }

            Completed?.Invoke(this, EventArgs.Empty);
        }

        private void OnBuffering(AsyncMediaPlayer sender, AsyncMediaPlayerBufferingEventArgs e)
        {
            //Logger.Debug(e.Cache);

            // Not gated on _loading: the first frame hides the skeleton before preroll completes.
            if (e.Cache == 100)
            {
                if (_loading)
                {
                    HideSkeleton();
                }

                if (_viewModel?.SelectedItem != null && _viewModel.SelectedItem.PosterChatId != _openedChatId && _viewModel.SelectedItem.Id != _openedStoryId)
                {
                    _openedChatId = _viewModel.SelectedItem.PosterChatId;
                    _openedStoryId = _viewModel.SelectedItem.Id;
                    _viewModel.ClientService.Send(new OpenStory(_openedChatId, _openedStoryId));
                }
            }
            else if (e.Cache < 100 && !_loading)
            {
                ShowSkeleton();
            }

            // The progress is a timed animation, so it has to stop while the video does and
            // pick up from where the player really is.
            if (e.Cache == 100)
            {
                if (_buffering)
                {
                    _buffering = false;

                    if (_state == StoryPauseSource.None && _progressStarted)
                    {
                        SynchronizeProgress();
                        Progress.Resume();
                    }
                }
            }
            else if (!_buffering)
            {
                _buffering = true;
                Progress.Suspend();
            }
        }

        // Each report comes with a frame the player rendered, about four a second, so the bar
        // starts only once the video does: while the player opens and prerolls it reports no
        // buffering, and nothing else says it hasn't started. In between reports the timed
        // animation fills in, and each report puts it back where the video is.
        private void OnPositionChanged(AsyncMediaPlayer sender, AsyncMediaPlayerPositionChangedEventArgs e)
        {
            if (_type != StoryType.Video || sender.Duration <= 0)
            {
                return;
            }

            Progress.Seek(e.Position / sender.Duration);

            if (!_progressStarted && !_buffering && _state == StoryPauseSource.None)
            {
                _progressStarted = true;
                Progress.Resume();
            }
        }

        private void SynchronizeProgress()
        {
            if (_player != null && _player.Duration > 0)
            {
                Progress.Seek(_player.Position / _player.Duration);
            }
        }

        private bool _buffering;
        private bool _progressStarted;

        private Image _texture;

        private AsyncMediaPlayer _player;

        private StoryContentPhotoTimer _timer;
        private bool _paused;

        private StoryPauseSource _state;

        private void MutePlaceholder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                var window = element.GetParent<StoriesWindow>();
                window?.ShowToast(element, Strings.StoryNoSound, ToastPlacementMode.BottomLeft);
            }
        }

        private void Privacy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                string message;
                if (_viewModel.IsMyStory)
                {
                    message = _viewModel.SelectedItem?.PrivacySettings switch
                    {
                        StoryPrivacySettingsCloseFriends => Strings.CloseFriendsHintSelf,
                        StoryPrivacySettingsSelectedUsers => Strings.StorySelectedContactsHintSelf,
                        StoryPrivacySettingsContacts => Strings.StoryContactsHintSelf,
                        _ => string.Empty
                    };
                }
                else if (_viewModel.ClientService.TryGetUser(_viewModel.Chat, out User user))
                {
                    message = _viewModel.SelectedItem?.PrivacySettings switch
                    {
                        StoryPrivacySettingsCloseFriends => Strings.CloseFriendsHint,
                        StoryPrivacySettingsSelectedUsers => Strings.StorySelectedContactsHint,
                        StoryPrivacySettingsContacts => Strings.StoryContactsHint,
                        _ => string.Empty
                    };

                    message = string.Format(message, user.FirstName);
                }
                else
                {
                    return;
                }

                var window = element.GetParent<StoriesWindow>();
                window?.ShowToast(element, message, ToastPlacementMode.BottomLeft);
            }
        }

        public void Suspend(StoryPauseSource source)
        {
            var none = _state == StoryPauseSource.None;

            _state |= source;

            if (none)
            {
                if (_type == StoryType.Video)
                {
                    _player?.Pause(true);
                }
                else
                {
                    _timer.Pause();
                }

                Progress.Suspend();
            }
        }

        public void Resume(StoryPauseSource source)
        {
            _state &= ~source;

            if (_state == StoryPauseSource.None)
            {
                if (_type == StoryType.Video)
                {
                    _player?.Pause(false);
                    SynchronizeProgress();
                }
                else
                {
                    _timer.Start();
                }

                if (!_buffering && (_type != StoryType.Video || _progressStarted))
                {
                    Progress.Resume();
                }
            }
        }

        public void Toggle()
        {
            if (_state != StoryPauseSource.None)
            {
                Resume(StoryPauseSource.Interaction);
            }
            else
            {
                Suspend(StoryPauseSource.Interaction);
            }
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.VolumeMuted = Mute.IsChecked is false;

            _player?.Mute = AppSettings.VolumeMuted;
        }

        /// <summary>
        /// Steps the shared playback volume of a video with sound. While muted, any step unmutes
        /// instead, and stepping down to nothing mutes at full volume, as the gallery does.
        /// </summary>
        public void ChangeVolume(double amount)
        {
            if (_player == null || Mute.Visibility != Visibility.Visible || !Mute.IsEnabled)
            {
                return;
            }

            var volume = AppSettings.VolumeLevel;
            var muted = false;

            if (!AppSettings.VolumeMuted)
            {
                volume = Math.Clamp(Math.Round(volume + amount, 1), 0, 1);

                if (volume == 0)
                {
                    volume = 1;
                    muted = true;
                }
            }

            AppSettings.VolumeLevel = volume;
            AppSettings.VolumeMuted = muted;

            _player.Volume = volume;
            _player.Mute = muted;

            Mute.IsChecked = !muted;
        }

        #region Caption

        private const int CaptionMaxLines = 3;

        // How far below the top of the story an open caption stops, scrolling past that.
        private const double CaptionExpandedInset = 64;

        // What a quote or a code block takes from the width of its text: the 3 px bar of
        // BlockQuote's template and the 8 + 24 horizontal padding MessageTextBlock gives it.
        private const double CaptionBlockChrome = 3 + 8 + 24;

        // DirectWrite measures the cut and XAML draws it, and the two round differently: a
        // token that fits to the pixel in one can wrap in the other.
        private const double CaptionTokenSlack = 2;

        private FormattedText _captionSource;
        private StyledText _captionText;
        private IClientService _captionClientService;
        private double _captionWidth;
        private bool _captionTrimmed;
        private bool _captionExpanded;
        private bool _captionAnimate;
        private bool _captionEntityClicked;
        private TextSelectionManager _captionSelection;

        private void UpdateCollapsedCaption(double width)
        {
            if (_captionText == null || width <= 0 || width == _captionWidth)
            {
                return;
            }

            _captionWidth = width;

            var trimmed = TrimCaption(_captionText, width, CaptionMaxLines);
            _captionTrimmed = trimmed != null;

            if (trimmed != null)
            {
                CaptionCollapsed.SetText(_captionClientService, trimmed);
            }
            else
            {
                CaptionCollapsed.SetText(_captionClientService, _captionText);
            }
        }

        // The caption cut to maxLines lines, the last of them ending in "… Show more", or null
        // when it fits. Every line of text costs one wherever it is, a quote's included; a code
        // block's language header is not a line. The block the cut falls in keeps its box, and
        // the token goes inside it, at the end of that block's last visible line.
        private static FormattedText TrimCaption(StyledText styled, double width, int maxLines)
        {
            var scale = BootStrapper.Current.TextScaleFactor;
            var messageSize = AppSettings.Appearance.MessageFontSize * scale;
            var quoteSize = AppSettings.Appearance.CaptionFontSize * scale;

            var paragraphs = styled.Paragraphs;
            var remaining = maxLines;

            for (int i = 0; i < paragraphs.Count; i++)
            {
                var paragraph = paragraphs[i];
                var size = paragraph.Type is TextParagraphTypeQuote ? quoteSize : messageSize;
                var layoutWidth = paragraph.Type != null ? width - CaptionBlockChrome : width;
                var rtl = paragraph.Direction == TextDirectionality.RightToLeft;

                TextFormat format = null;
                var lines = 1;

                if (paragraph.Length > 0)
                {
                    format = Direct2D.Current.CreateTextFormat2(paragraph.Text, paragraph.Parts ?? TextStyleRun.NoParts, size, layoutWidth);
                    lines = Math.Max(1, format.LineMetrics(0, paragraph.Length, size, layoutWidth, rtl, true).Length);
                }

                if (lines < remaining || (lines == remaining && i == paragraphs.Count - 1))
                {
                    remaining -= lines;
                    continue;
                }

                var monospace = paragraph.Type is TextParagraphTypeMonospace;
                var reserve = MeasureCaptionToken(size, monospace) + CaptionTokenSlack;

                var cut = format?.TrimLine(size, layoutWidth, rtl, remaining - 1, reserve) ?? 0;
                if (cut < 0)
                {
                    cut = paragraph.Length;
                }

                while (cut > 0 && char.IsWhiteSpace(paragraph.Text[cut - 1]))
                {
                    cut--;
                }

                return AppendCaptionToken(styled.Substring(0, paragraph.Offset + cut), cut == 0);
            }

            return null;
        }

        private static FormattedText AppendCaptionToken(FormattedText text, bool emptyLine)
        {
            var ellipsis = emptyLine ? string.Empty : "… ";
            var token = ellipsis + Strings.ShowMore;
            var end = text.Text.Length;

            var entities = new MutableVector<TextEntity>(text.Entities.Count + 1);

            foreach (var entity in text.Entities)
            {
                // The quote or code block the cut ends in has to hold the token, or the token
                // would be a paragraph of its own under the box.
                if (entity.Offset + entity.Length == end && entity.Type is TextEntityTypeBlockQuote or TextEntityTypeExpandableBlockQuote or TextEntityTypePre or TextEntityTypePreCode)
                {
                    entities.Add(new TextEntity(entity.Offset, entity.Length + token.Length, entity.Type));
                }
                else
                {
                    entities.Add(entity);
                }
            }

            entities.Add(new TextEntity(end + ellipsis.Length, Strings.ShowMore.Length, new TextEntityTypeBold()));

            return new FormattedText(text.Text + token, entities);
        }

        private static double MeasureCaptionToken(double fontSize, bool monospace)
        {
            var ellipsis = "… ";
            var token = ellipsis + Strings.ShowMore;

            var parts = new List<TextStylePart>(2)
            {
                new TextStylePart
                {
                    Offset = ellipsis.Length,
                    Length = Strings.ShowMore.Length,
                    Type = monospace ? TextStyle.Bold | TextStyle.Monospace : TextStyle.Bold
                }
            };

            if (monospace)
            {
                parts.Add(new TextStylePart
                {
                    Offset = 0,
                    Length = ellipsis.Length,
                    Type = TextStyle.Monospace
                });
            }

            var format = Direct2D.Current.CreateTextFormat2(token, parts, fontSize, 100000);
            return format.ContentWidths(fontSize, 100000, false).Y;
        }

        private void UpdateCaptionMaxHeight()
        {
            var padding = CaptionPanel.Padding;
            var repost = RepostPanel.Visibility == Visibility.Visible
                ? RepostPanel.ActualHeight + RepostPanel.Margin.Bottom
                : 0;

            CaptionScroll.MaxHeight = Math.Max(0, CaptionRoot.ActualHeight - CaptionExpandedInset - padding.Top - padding.Bottom - repost);
        }

        private StoryRepostInfo _repostInfo;

        // A repost names its source above the caption, in a pill that grows to a quote of the
        // original caption once that has loaded. A story sharing a channel post names the channel.
        private bool UpdateRepostPanel(StoryViewModel story, bool emptyCaption)
        {
            var clientService = story.ClientService;
            string title = null;

            if (story.RepostInfo?.Origin is StoryOriginPublicStory publicStory)
            {
                title = clientService.TryGetChat(publicStory.ChatId, out Chat chat)
                    ? GetRepostIcon(chat) + Icons.Spacing + clientService.GetTitle(chat)
                    : Icons.PersonFilled16;
            }
            else if (story.RepostInfo?.Origin is StoryOriginHiddenUser hiddenUser)
            {
                title = Icons.PersonFilled16 + Icons.Spacing + hiddenUser.PosterName;
            }
            else if (TryGetPostArea(story, out Chat postChat, out _))
            {
                title = GetRepostIcon(postChat) + Icons.Spacing + clientService.GetTitle(postChat);
            }

            if (title == null)
            {
                _repostInfo = null;
                RepostPanel.Visibility = Visibility.Collapsed;
                return false;
            }

            RepostPanel.Margin = new Thickness(0, 0, 0, emptyCaption ? 0 : 8);
            RepostPanel.Visibility = Visibility.Visible;
            RepostTitle.Text = title;

            // TDLib hands out a new info with every change to the story, the only time the
            // original's caption has to be fetched again.
            if (story.RepostInfo == null)
            {
                _repostInfo = null;
                UpdateRepostText(null);
            }
            else if (story.RepostInfo != _repostInfo)
            {
                _repostInfo = story.RepostInfo;
                UpdateRepostText(null);

                if (story.RepostInfo.Origin is StoryOriginPublicStory origin)
                {
                    LoadRepostText(story, origin);
                }
            }

            return true;
        }

        private static string GetRepostIcon(Chat chat)
        {
            return chat.Type switch
            {
                ChatTypeSupergroup { IsChannel: true } => Icons.MegaphoneFilled16,
                ChatTypeSupergroup or ChatTypeBasicGroup => Icons.PeopleFilled16,
                _ => Icons.PersonFilled16
            };
        }

        private async void LoadRepostText(StoryViewModel story, StoryOriginPublicStory origin)
        {
            var response = await story.ClientService.SendAsync(new GetStory(origin.ChatId, origin.StoryId, false));
            if (response is Story source && story.RepostInfo?.Origin == origin && _viewModel?.SelectedItem == story && !string.IsNullOrEmpty(source.Caption?.Text))
            {
                // The panel grows at the top, as the caption does when it opens.
                _captionAnimate = true;

                UpdateRepostText(source.Caption.Text);
                FadeCaptionPart(RepostText, true);
            }
        }

        private void UpdateRepostText(string text)
        {
            var large = text != null;

            RepostText.Text = text ?? string.Empty;
            RepostText.Visibility = large
                ? Visibility.Visible
                : Visibility.Collapsed;

            RepostBackground.Height = large ? 42 : 22;
            RepostBackground.CornerRadius = new CornerRadius(large ? 5 : 11);
            RepostBackground.BorderThickness = new Thickness(large ? 3 : 0, 0, 0, 0);
        }

        private async void RepostPanel_Click(object sender, RoutedEventArgs e)
        {
            // Inside the caption, a click that opens the caption too otherwise.
            _captionEntityClicked = true;

            var story = _viewModel?.SelectedItem;
            if (story == null)
            {
                return;
            }

            if (story.RepostInfo?.Origin is StoryOriginPublicStory origin)
            {
                var response = await story.ClientService.SendAsync(new GetStory(origin.ChatId, origin.StoryId, false));
                if (response is Story source)
                {
                    var parent = this.GetParent<StoriesWindow>();
                    var navigationService = ViewModel?.NavigationService;

                    if (parent != null && navigationService != null)
                    {
                        parent.TryHide(ModalPopupResult.Primary);
                        StoriesWindow.ShowStory(navigationService, source, StoryOpenOrigin.Card, Windows.Foundation.Rect.Empty, null);
                    }
                }
                else
                {
                    var window = RepostPanel.GetParent<StoriesWindow>();
                    window?.ShowToast(RepostPanel, Strings.StoryNotFound, ToastPlacementMode.Top);
                }
            }
            else if (story.RepostInfo != null)
            {
                ShowHiddenAccount(RepostPanel, ToastPlacementMode.Top);
            }
            else if (TryGetPostArea(story, out _, out StoryAreaTypeMessage post))
            {
                OpenPost(post);
            }
        }

        private void ExpandCaption()
        {
            if (_captionExpanded || !_captionTrimmed)
            {
                return;
            }

            _captionExpanded = true;

            UpdateCaptionMaxHeight();
            CaptionScroll.ChangeView(null, 0, null, true);
            CaptionScroll.Visibility = Visibility.Visible;
            CaptionCollapsed.IsHitTestVisible = false;
            CaptionOverlay.IsHitTestVisible = true;

            _captionSelection ??= new TextSelectionManager(this, Caption, handleContextMenu: true);

            AnimateCaption(true);
            Suspend(StoryPauseSource.Caption);
        }

        /// <summary>
        /// Closes the caption if it is open, which is what going back does first.
        /// </summary>
        public bool TryCollapseCaption()
        {
            if (!_captionExpanded)
            {
                return false;
            }

            _captionExpanded = false;

            CaptionScroll.Visibility = Visibility.Collapsed;
            CaptionCollapsed.IsHitTestVisible = true;
            CaptionOverlay.IsHitTestVisible = false;

            AnimateCaption(false);
            Resume(StoryPauseSource.Caption);
            return true;
        }

        // The two copies share their first lines, so fading one into the other while the panel
        // slides to its new height reads as the text staying put and the rest appearing.
        private void AnimateCaption(bool expand)
        {
            _captionAnimate = true;

            FadeCaptionPart(CaptionOverlay, expand);
            FadeCaptionPart(CaptionCollapsed, !expand);

            if (expand)
            {
                FadeCaptionPart(CaptionScroll, true);
            }
        }

        private static void FadeCaptionPart(UIElement element, bool show)
        {
            var visual = ElementComposition.GetElementVisual(element);

            var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, show ? 0 : 1);
            opacity.InsertKeyFrame(1, show ? 1 : 0);
            opacity.Duration = TimeSpan.FromMilliseconds(250);

            visual.StartAnimation("Opacity", opacity);
        }

        private void CaptionPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var padding = CaptionPanel.Padding;
            UpdateCollapsedCaption(e.NewSize.Width - padding.Left - padding.Right);

            if (_captionAnimate)
            {
                _captionAnimate = false;

                // Anchored at the bottom, the panel grows and shrinks at the top: it starts
                // where its top was and slides there.
                ElementCompositionPreview.SetIsTranslationEnabled(CaptionPanel, true);

                var visual = ElementComposition.GetElementVisual(CaptionPanel);

                var translation = visual.Compositor.CreateScalarKeyFrameAnimation();
                translation.InsertKeyFrame(0, (float)(e.NewSize.Height - e.PreviousSize.Height));
                translation.InsertKeyFrame(1, 0);
                translation.Duration = TimeSpan.FromMilliseconds(250);

                visual.StartAnimation("Translation.Y", translation);
            }
        }

        private void CaptionPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _captionEntityClicked = false;

            // Held anywhere else on the story, a press hides its chrome until released.
            e.Handled = true;
        }

        private void CaptionPanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            // The window moves between stories on the wheel. Over an open caption the wheel
            // scrolls it, and at either end of it does nothing rather than leave the story.
            if (_captionExpanded)
            {
                e.Handled = true;
            }
        }

        private void CaptionPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Open, a click on the text is the start or the end of a selection. Tapped follows
            // the Click of the link under the pointer, which has opened it.
            if (_captionExpanded || _captionEntityClicked)
            {
                _captionEntityClicked = false;
                return;
            }

            ExpandCaption();
        }

        private void CaptionOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void CaptionOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            TryCollapseCaption();
            e.Handled = true;
        }

        #endregion

        private void InactivePanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var visual = ElementComposition.GetElementVisual(MiniInside);
            visual.CenterPoint = new Vector3(MiniInside.ActualSize / 2, 0);
        }

        public void CopyLink(StoryViewModel story)
        {
            if (story.TryGetLinkUsername(out string username))
            {
                MessageHelper.CopyLink(story.ClientService, XamlRoot, new InternalLinkTypeStory(username, story.Id));
            }
        }

        private void Caption_TextEntityClick(object sender, TextEntityClickEventArgs e)
        {
            _captionEntityClicked = true;

            if (e.Type is TextEntityTypeBotCommand && e.Text is string command)
            {
                ViewModel.Delegate.SendBotCommand(command);
            }
            else if (e.Type is TextEntityTypeEmailAddress)
            {
                ViewModel.Delegate.OpenUrl("mailto:" + e.Text, false);
            }
            else if (e.Type is TextEntityTypePhoneNumber)
            {
                ViewModel.Delegate.OpenUrl("tel:" + e.Text, false);
            }
            else if (e.Type is TextEntityTypeHashtag or TextEntityTypeCashtag && e.Text is string hashtag)
            {
                ViewModel.Delegate.OpenHashtag(hashtag);
            }
            else if (e.Type is TextEntityTypeMention && e.Text is string username)
            {
                ViewModel.Delegate.OpenUsername(username);
            }
            else if (e.Type is TextEntityTypeMentionName mentionName)
            {
                ViewModel.Delegate.OpenUser(mentionName.UserId);
            }
            else if (e.Type is TextEntityTypeTextUrl textUrl)
            {
                ViewModel.Delegate.OpenUrl(textUrl.Url, true);
            }
            else if (e.Type is TextEntityTypeUrl && e.Text is string url)
            {
                ViewModel.Delegate.OpenUrl(url, false);
            }
            else if (e.Type is TextEntityTypeBankCardNumber && e.Text is string cardNumber)
            {
                ViewModel.Delegate.OpenBankCardNumber(cardNumber);
            }
            else if (e.Type is TextEntityTypeMediaTimestamp mediaTimestamp)
            {
                // Never happens here
            }
        }

        private void MessagesHost_ChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new ListViewItem
                {
                    Style = sender.ItemContainerStyle,
                    ContentTemplate = sender.ItemTemplate
                };

                args.ItemContainer.ContextRequested += MessagesHost_ContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void MessagesHost_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var flyout = new MenuFlyout();
            var message = MessagesHost.ItemFromContainer(sender) as GroupCallMessage;

            var textBlock = new TextBlock();
            textBlock.Text = Formatter.SentDate(message.Date);
            textBlock.FontSize = 12;

            var placeholder = new MenuFlyoutContent();
            placeholder.Content = textBlock;
            placeholder.FontSize = 12;
            placeholder.Padding = new Thickness(12, 4, 12, 4);
            placeholder.HorizontalAlignment = HorizontalAlignment.Left;

            flyout.Items.Add(placeholder);
            flyout.CreateFlyoutSeparator();
            flyout.CreateFlyoutItem(OpenMessage, message, Strings.OpenProfile, Icons.PersonCircle);
            flyout.CreateFlyoutItem(CopyMessage, message, Strings.Copy, Icons.Copy);

            var translate = ViewModel.Session.Resolve<ITranslateService>();
            if (translate.CanTranslateText(message.Text.Text))
            {
                flyout.CreateFlyoutItem(TranslateMessage, message, Strings.TranslateMessage, Icons.Translate);
            }

            if (message.CanBeDeleted)
            {
                flyout.CreateFlyoutItem(DeleteMessage, message, Strings.Delete, Icons.Delete, destructive: true);
            }

            flyout.ShowAt(sender, args);
        }

        private void OpenMessage(GroupCallMessage message)
        {
            ViewModel.NavigationService.NavigateToSender(message.SenderId);
        }

        private void CopyMessage(GroupCallMessage message)
        {
            MessageHelper.CopyText(XamlRoot, message.Text);
        }

        private void TranslateMessage(GroupCallMessage message)
        {
            var language = LanguageIdentification.IdentifyLanguage(message.Text.Text);
            var translate = ViewModel.Session.Resolve<ITranslateService>();

            var popup = new TranslatePopup(translate, message.Text, language, _viewModel.Settings.Translate.To, false)
            {
                RequestedTheme = ElementTheme.Dark
            };

            ViewModel.ShowPopup(popup);
        }

        public async void DeleteMessage(GroupCallMessage message)
        {
            if (ViewModel.SelectedItem?.Content is not StoryContentLive live || !ViewModel.ClientService.TryGetGroupCall(live.GroupCallId, out GroupCall groupCall))
            {
                return;
            }

            var popup = new DeleteMessagesPopup(ViewModel.ClientService, groupCall, message);
            popup.RequestedTheme = ElementTheme.Dark;

            var confirm = await ViewModel.ShowPopupAsync(popup);
            if (confirm == ModalPopupResult.Primary)
            {
                if (popup.DeleteAllMessages.Any())
                {
                    ViewModel.ClientService.Send(new DeleteGroupCallMessagesBySender(_call.Id, message.SenderId, popup.ReportSpam.Any()));
                }
                else
                {
                    ViewModel.ClientService.Send(new DeleteGroupCallMessages(_call.Id, [message.MessageId], popup.ReportSpam.Any()));
                }

                if (popup.BanUser.Any())
                {
                    if (ViewModel.Chat.Type is ChatTypePrivate)
                    {
                        ViewModel.ClientService.Send(new SetMessageSenderBlockList(message.SenderId, new BlockListMain()));
                    }
                    else
                    {
                        ViewModel.ClientService.Send(new SetChatMemberStatus(ViewModel.ChatId, message.SenderId, new ChatMemberStatusBanned()));
                    }
                }
            }
        }

        private void MessagesHost_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }
            else if (args.ItemContainer.ContentTemplateRoot is LiveStoryMessageCell content && args.Item is GroupCallMessage message)
            {
                // Not _call: Deactivate discards it while this list stays in the tree and keeps
                // realizing containers through the move to the next story.
                _topDonors.TryGetValue(message.SenderId, out int position);
                content.Update(_viewModel.ClientService, message, position);
            }
        }

        private void MessagesHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var layerVisual = _messagesVisual;
            if (layerVisual == null)
            {
                return;
            }

            var compositor = layerVisual.Compositor;

            var alphaMask = new AlphaMaskEffect
            {
                Name = "AlphaMask",
                Source = new CompositionEffectSourceParameter("source"),
                AlphaMask = new CompositionEffectSourceParameter("mask")
            };

            var next = e.NewSize.ToVector2();
            var top = 24 / next.Y;
            var bottom = (next.Y - 8) / next.Y;

            var gradientBrush = compositor.CreateLinearGradientBrush();
            gradientBrush.StartPoint = new(0, 0);
            gradientBrush.EndPoint = new(0, 1);
            gradientBrush.MappingMode = CompositionMappingMode.Relative;
            //gradientBrush.ExtendMode = CompositionGradientExtendMode.Clamp;

            gradientBrush.ColorStops.Add(compositor.CreateColorGradientStop(0, Colors.Transparent));
            gradientBrush.ColorStops.Add(compositor.CreateColorGradientStop(top, Colors.White));
            gradientBrush.ColorStops.Add(compositor.CreateColorGradientStop(bottom, Colors.White));
            gradientBrush.ColorStops.Add(compositor.CreateColorGradientStop(1, Colors.Transparent));

            var effectFactory = compositor.CreateEffectFactory(alphaMask);
            var effectBrush = effectFactory.CreateBrush();
            effectBrush.SetSourceParameter("mask", gradientBrush);
            layerVisual.Effect = effectBrush;
        }

        private void PinnedMessagesHost_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }
            else if (args.ItemContainer.ContentTemplateRoot is LiveStoryPinnedMessageCell content && args.Item is GroupCallMessage message)
            {
                _topDonors.TryGetValue(message.SenderId, out int position);
                content.Update(_viewModel.ClientService, message, position);
            }
        }

        private void ReactionsHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var visual = ElementComposition.GetElementVisual(MessagesHost);
            ElementCompositionPreview.SetIsTranslationEnabled(MessagesHost, true);

            var prev = e.PreviousSize.ToVector2();
            var next = e.NewSize.ToVector2();

            var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0, next.Y - prev.Y);
            animation.InsertKeyFrame(1, 0);

            visual.StartAnimation("Translation.Y", animation);
        }

        private async void PinnedMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not LiveStoryPinnedMessageCell pinnedContent || pinnedContent.Message == null)
            {
                return;
            }

            await MessagesHost.ScrollToItem2(pinnedContent.Message, VerticalAlignment.Center);

            var container = MessagesHost.ContainerFromItem(pinnedContent.Message) as SelectorItem;
            if (container?.ContentTemplateRoot is LiveStoryMessageCell content)
            {
                content.Highlight();
            }
        }

        private bool _messagesCollapsed;

        public void ShowHideMessages(bool show)
        {
            if (_messagesCollapsed != show)
            {
                return;
            }

            _messagesCollapsed = !show;
            MessagesHost.Visibility = Visibility.Visible;
            MessagesShadow.Visibility = Visibility.Visible;

            var visual = ElementComposition.GetElementVisual(MessagesHost);
            var shadow = ElementComposition.GetElementVisual(MessagesShadow);
            ElementCompositionPreview.SetIsTranslationEnabled(MessagesHost, true);

            var compositor = visual.Compositor;

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, args) =>
            {
                if (_messagesCollapsed)
                {
                    MessagesHost.Visibility = Visibility.Collapsed;
                    MessagesShadow.Visibility = Visibility.Collapsed;
                }
            };

            var translation = compositor.CreateScalarKeyFrameAnimation();
            translation.InsertKeyFrame(0, show ? MessagesShadow.ActualSize.Y : 0);
            translation.InsertKeyFrame(1, show ? 0 : MessagesShadow.ActualSize.Y);

            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, show ? 0 : 1);
            opacity.InsertKeyFrame(1, show ? 1 : 0);

            visual.StartAnimation("Translation.Y", translation);
            visual.StartAnimation("Opacity", opacity);
            shadow.StartAnimation("Opacity", opacity);

            batch.End();
        }
    }

    public partial class StoryContentPhotoTimer
    {
        /// <summary>
        /// How long a photo story stays on screen, in seconds.
        /// </summary>
        public const double Duration = 10;

        private readonly Stopwatch _watch;
        private readonly DispatcherTimer _timer;

        private readonly TimeSpan _interval;

        public StoryContentPhotoTimer()
        {
            _interval = TimeSpan.FromSeconds(Duration);

            _watch = new Stopwatch();

            _timer = new DispatcherTimer();
            _timer.Interval = _interval;
            _timer.Tick += OnTick;
        }

        public void Start()
        {
            _watch.Start();
            _timer.Start();
        }

        public void Pause()
        {
            _watch.Stop();
            _timer.Stop();

            _timer.Interval = _interval - _watch.Elapsed;
        }

        public void Stop()
        {
            _watch.Reset();

            _timer.Stop();
            _timer.Interval = _interval;
        }

        public event EventHandler Tick;

        private void OnTick(object sender, object e)
        {
            Stop();
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }

    public partial class StoryProgress : Grid
    {
        private CompositionPropertySet _progressPropertySet;
        private AnimationController _progressController;

        public void Suspend()
        {
            _progressController?.Pause();
        }

        public void Resume()
        {
            _progressController?.Resume();
        }

        public void Seek(double progress)
        {
            _progressController?.Progress = (float)Math.Clamp(progress, 0, 1);
        }

        private double _duration;

        /// <summary>
        /// Plays the current segment again from the start.
        /// </summary>
        public void Restart()
        {
            if (_progressPropertySet != null)
            {
                _progressPropertySet.InsertScalar("Progress", 0);
                StartProgress();
            }
        }

        private void StartProgress()
        {
            var compositor = _progressPropertySet.Compositor;

            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1.0f, 1.0f, compositor.CreateLinearEasingFunction());
            animation.Duration = TimeSpan.FromSeconds(_duration);

            _progressPropertySet.StartAnimation("Progress", animation);
            _progressController = _progressPropertySet.TryGetAnimationController("Progress");
        }

        public void Update(int index, int count, double duration)
        {
            _duration = duration;

            Children.Clear();
            ColumnDefinitions.Clear();

            // The gaps narrow as the segments do, down to a single physical pixel past 100.
            var gap = count > 100
                ? 1 / (XamlRoot?.RasterizationScale ?? 1)
                : count >= 50 ? 1 : 2;

            for (int i = 0; i < count; i++)
            {
                var rectangle = new Rectangle
                {
                    Margin = new Thickness(i == 0 ? 0 : gap, 2, 0, 2),
                    Height = 2,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = new SolidColorBrush(Colors.White)
                    {
                        Opacity = i < index ? 1 : 0.3
                    }
                };

                if (i == index)
                {
                    var compositor = BootStrapper.Current.Compositor;

                    _progressPropertySet = compositor.CreatePropertySet();
                    _progressPropertySet.InsertScalar("Progress", 0);

                    var ellipse = compositor.CreateRoundedRectangleGeometry();
                    ellipse.CornerRadius = new Vector2(1);
                    ellipse.Size = new Vector2(20, 2);

                    var shape2 = compositor.CreateSpriteShape();
                    shape2.Geometry = ellipse;
                    shape2.FillBrush = compositor.CreateColorBrush(Colors.White);

                    var visual = compositor.CreateShapeVisual();
                    visual.Shapes.Add(shape2);
                    visual.Size = new Vector2(20, 2);

                    rectangle.SizeChanged += (s, args) =>
                    {
                        visual.Size = args.NewSize.ToVector2();
                    };

                    ElementCompositionPreview.SetElementChildVisual(rectangle, visual);

                    ExpressionAnimation expression = compositor.CreateExpressionAnimation();
                    expression.SetReferenceParameter("_", _progressPropertySet);
                    expression.SetReferenceParameter("V", visual);
                    expression.Expression = "Vector2(_.Progress * V.Size.X, 2)";

                    // Apply the expression to the point visual's Offset property
                    ellipse.StartAnimation("Size", expression);

                    StartProgress();
                }

                ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(rectangle, i);

                Children.Add(rectangle);
            }
        }
    }
}
