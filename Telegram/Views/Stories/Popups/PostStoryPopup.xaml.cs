//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Chats;
using Telegram.Controls.Drawers;
using Telegram.Controls.Media;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Services.Settings;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.ViewModels.Drawers;
using Telegram.Views.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;

namespace Telegram.Views.Stories.Popups
{
    /// <summary>
    /// What is asked before a story is posted: its caption, who can view it, whether it can be
    /// saved, whether it stays on the profile, and how long it lasts.
    /// </summary>
    public sealed partial class PostStoryPopup : ModalPopup, IViewWithAutocomplete
    {
        // The caption's counter shows once fewer than this many characters are left.
        private const int CounterThreshold = 25;

        private readonly int _captionLimit;
        private int _captionLength;
        private bool _captionLimitToast;

        private CaptionController _caption;
        private IAutocompleteCollection _autocomplete;

        // In hours. Only Premium picks one other than a day.
        private static readonly int[] Periods = { 6, 12, 24, 48 };
        private const int DefaultPeriod = 24;

        private readonly IClientService _clientService;
        private readonly INavigationService _navigationService;
        private readonly StorySettings _settings;

        private List<long> _exceptFromEveryone;
        private List<long> _exceptFromContacts;
        private List<long> _selectedContacts;
        private List<long> _closeFriends;

        private int _period = DefaultPeriod;

        // The chat the story is posted as: the current user's own, or a supergroup or channel.
        private long _chatId;
        private readonly bool _canChangeChat;
        private readonly Func<long, Task<bool>> _canPostStory;

        // Set while the selection is changed from code, so that it is not taken as a choice.
        private bool _changingChat;

        // The albums of the chat they were loaded for, or null while they load.
        private List<StoryAlbum> _albums;
        private long _albumsChatId;
        private readonly List<int> _albumIds = new();

        // The people the current user's stories are hidden from: the first page of them, and how
        // many there are in all.
        private List<long> _blockList = new();
        private int _blockListCount;

        private bool _accepting;

        /// <param name="canChangeChat">Whether the story can be posted as a chat other than <paramref name="chatId"/>.</param>
        /// <param name="canPostStory">Whether a story can be posted as the chat now, having said why not if it cannot.</param>
        public PostStoryPopup(IClientService clientService, INavigationService navigationService, long chatId, bool canChangeChat, Func<long, Task<bool>> canPostStory)
        {
            InitializeComponent();

            _clientService = clientService;
            _navigationService = navigationService;
            _settings = clientService.Session.Resolve<ISettingsService>().Stories;

            _chatId = chatId;
            _canChangeChat = canChangeChat;
            _canPostStory = canPostStory;

            Title = Strings.StoryPrivacyAlertTitle;

            EmojiPanel.DataContext = EmojiDrawerViewModel.Create(clientService.Session);

            // Not a hard limit: past it the counter turns negative and the story cannot be posted,
            // so a pasted caption can still be trimmed rather than being cut off.
            _captionLimit = (int)clientService.Options.StoryCaptionLengthMax;

            if (!clientService.Options.CanUseTextEntitiesInStoryCaption)
            {
                CaptionInput.AllowedEntities = FormattedTextEntity.None;
            }

            // A period other than a day is Premium's, so there is nothing to choose where it
            // cannot be bought.
            if (!clientService.IsPremium && !clientService.IsPremiumAvailable)
            {
                PeriodRoot.Visibility = Visibility.Collapsed;
            }

            var periods = new List<string>();

            foreach (var hours in Periods)
            {
                periods.Add(Locale.Declension(Strings.R.Hours, hours));
            }

            Period.ItemsSource = periods;

            // Starting from what the last story was posted with. A period other than a day stays
            // Premium's, whatever was saved.
            var period = clientService.IsPremium && Array.IndexOf(Periods, _settings.Period) != -1
                ? _settings.Period
                : DefaultPeriod;

            Period.SelectedIndex = Array.IndexOf(Periods, period);

            AllowScreenshots.IsChecked = _settings.AllowScreenshots;
            Keep.IsChecked = _settings.Keep;

            _exceptFromEveryone = _settings.ExceptFromEveryone;
            _exceptFromContacts = _settings.ExceptFromContacts;
            _selectedContacts = _settings.SelectedContacts;

            var option = _settings.Privacy switch
            {
                StoryPrivacyMode.Contacts => Contacts,
                StoryPrivacyMode.CloseFriends => CloseFriends,
                StoryPrivacyMode.SelectedContacts => SelectedContacts,
                _ => Everyone
            };

            option.IsChecked = true;

            UpdateList();
            UpdateBlockList();
            UpdateChat();

            LoadChats();
            LoadCloseFriends();
            LoadBlockList();
        }

        public long ChatId => _chatId;

        public FormattedText Caption => CaptionInput.GetFormattedText();

        // Albums are on the page the story is kept on, so a story that is not kept is in none.
        public List<int> AlbumIds => IsPostedToChatPage ? _albumIds : new List<int>();

        private bool IsSelf => _chatId == _clientService.Options.MyId;

        private bool IsChannel => _clientService.TryGetChat(_chatId, out Chat chat) && chat.Type is ChatTypeSupergroup { IsChannel: true };

        public StoryPrivacySettings PrivacySettings
        {
            get
            {
                // TDLib ignores it for a chat's story, which whoever sees the chat sees.
                if (!IsSelf)
                {
                    return new StoryPrivacySettingsEveryone(Vector<long>.Empty);
                }
                else if (Contacts.IsChecked == true)
                {
                    return new StoryPrivacySettingsContacts(new Vector<long>(_exceptFromContacts));
                }
                else if (CloseFriends.IsChecked == true)
                {
                    return new StoryPrivacySettingsCloseFriends();
                }
                else if (SelectedContacts.IsChecked == true)
                {
                    return new StoryPrivacySettingsSelectedUsers(new Vector<long>(_selectedContacts));
                }

                return new StoryPrivacySettingsEveryone(new Vector<long>(_exceptFromEveryone));
            }
        }

        // In seconds.
        public int ActivePeriod => _period * 3600;

        public bool IsPostedToChatPage => Keep.IsChecked == true;

        public bool ProtectContent => AllowScreenshots.IsChecked != true;

        #region Chat

        private async void LoadChats()
        {
            var myId = _clientService.Options.MyId;
            var items = new List<ComboBoxItem>();

            if (_canChangeChat && _clientService.TryGetUser(myId, out User self))
            {
                items.Add(CreateChatItem(myId, ProfilePictureSource.User(_clientService, self), self.FullName()));

                var response = await _clientService.SendAsync(new GetChatsToPostStories());
                if (response is Td.Api.Chats chats)
                {
                    foreach (var chat in _clientService.GetChats(chats.ChatIds))
                    {
                        items.Add(CreateChatItem(chat.Id, ProfilePictureSource.Chat(_clientService, chat), chat.Title));
                    }
                }
            }
            else if (!_canChangeChat && _clientService.TryGetChat(_chatId, out Chat chat))
            {
                items.Add(CreateChatItem(chat.Id, ProfilePictureSource.Chat(_clientService, chat), chat.Title));
            }

            // Nothing to show where the current user has no one else to post as.
            if (items.Count == 0 || (items.Count == 1 && IsSelf))
            {
                return;
            }

            _changingChat = true;

            foreach (var item in items)
            {
                PublishAs.Items.Add(item);
            }

            PublishAs.SelectedIndex = IndexOfChat(_chatId);
            PublishAs.IsEnabled = _canChangeChat;

            _changingChat = false;

            PublishAsRoot.Visibility = Visibility.Visible;
        }

        private static ComboBoxItem CreateChatItem(long chatId, ProfilePictureSource source, string title)
        {
            var photo = new ProfilePicture
            {
                Source = source,
                Size = 24,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var text = new TextBlock
            {
                Text = title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(text, 1);

            var content = new Grid();
            content.ColumnDefinitions.Add(1, GridUnitType.Auto);
            content.ColumnDefinitions.Add(1, GridUnitType.Star);
            content.Children.Add(photo);
            content.Children.Add(text);

            return new ComboBoxItem
            {
                Content = content,
                Tag = chatId
            };
        }

        private int IndexOfChat(long chatId)
        {
            for (int i = 0; i < PublishAs.Items.Count; i++)
            {
                if (PublishAs.Items[i] is ComboBoxItem { Tag: long id } && id == chatId)
                {
                    return i;
                }
            }

            return -1;
        }

        // A chat is taken only once a story can be posted as it; otherwise the selection goes back.
        private async void PublishAs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_changingChat || PublishAs.SelectedItem is not ComboBoxItem { Tag: long chatId } || chatId == _chatId)
            {
                return;
            }

            var canPost = await _canPostStory(chatId);
            if (canPost)
            {
                _chatId = chatId;

                // Albums are the chat's own.
                _albumIds.Clear();

                UpdateChat();
            }
            else
            {
                _changingChat = true;
                PublishAs.SelectedIndex = IndexOfChat(_chatId);
                _changingChat = false;
            }
        }

        private void UpdateChat()
        {
            PrivacyRoot.Visibility = IsSelf
                ? Visibility.Visible
                : Visibility.Collapsed;

            Keep.Content = IsSelf
                ? Strings.StoryKeep
                : IsChannel
                ? Strings.StoryKeepChannel
                : Strings.StoryKeepGroup;

            UpdateKeepInfo();
            UpdateAlbums();
        }

        private void UpdateKeepInfo()
        {
            var key = IsSelf
                ? Strings.R.StoryKeepInfo
                : IsChannel
                ? Strings.R.StoryKeepChannelInfo
                : Strings.R.StoryKeepGroupInfo;

            KeepInfo.Text = Locale.Declension(key, _period);
        }

        #endregion

        #region Albums

        private void Keep_Click(object sender, RoutedEventArgs e)
        {
            UpdateAlbums();
        }

        private async void UpdateAlbums()
        {
            if (Keep.IsChecked != true)
            {
                AlbumsList.Visibility = Visibility.Collapsed;
                return;
            }

            var chatId = _chatId;
            if (_albumsChatId != chatId)
            {
                _albums = null;
                _albumsChatId = chatId;

                AlbumsList.Visibility = Visibility.Collapsed;

                var response = await _clientService.SendAsync(new GetChatStoryAlbums(chatId));
                if (chatId != _chatId)
                {
                    return;
                }

                _albums = response is StoryAlbums albums
                    ? albums.Albums.ToList()
                    : new List<StoryAlbum>();
            }

            // Still loading: the load shows the link once it is done.
            if (_albums == null)
            {
                return;
            }

            AlbumsList.Visibility = _albums.Count > 0 || CanCreateAlbum()
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateAlbumsList();
        }

        private void UpdateAlbumsList()
        {
            if (_albumIds.Count == 0)
            {
                AlbumsList.Content = Strings.StoriesAlbumNameAllStories;
            }
            else if (_albumIds.Count < 3)
            {
                var names = _albums
                    .Where(x => _albumIds.Contains(x.Id))
                    .Select(x => x.Name);

                AlbumsList.Content = string.Join(", ", names);
            }
            else
            {
                AlbumsList.Content = Locale.Declension(Strings.R.StoriesAlbumCount, _albumIds.Count);
            }
        }

        // A chat's albums are made by whoever can edit its stories.
        private bool CanCreateAlbum()
        {
            if (_albums == null || _albums.Count >= _clientService.Options.StoryAlbumCountMax)
            {
                return false;
            }

            return IsSelf || (_clientService.TryGetChat(_chatId, out Chat chat) && chat.CanEditStories(_clientService));
        }

        private void AlbumsList_Click(object sender, RoutedEventArgs e)
        {
            if (_albums == null)
            {
                return;
            }

            var flyout = new MenuFlyout();

            if (CanCreateAlbum())
            {
                flyout.CreateFlyoutItem(CreateAlbum, Strings.StoriesAlbumNewAlbum, Icons.FolderAdd);

                if (_albums.Count > 0)
                {
                    flyout.CreateFlyoutSeparator();
                }
            }

            foreach (var album in _albums)
            {
                var toggle = new ToggleMenuFlyoutItem
                {
                    Text = album.Name,
                    Icon = MenuFlyoutHelper.CreateIcon(Icons.Folder),
                    IsChecked = _albumIds.Contains(album.Id),
                    Tag = album.Id
                };

                // The item lives only as long as the menu, so nothing outlives the subscription.
                toggle.Click += Album_Click;
                flyout.Items.Add(toggle);
            }

            flyout.ShowAt(AlbumsList, FlyoutPlacementMode.BottomEdgeAlignedLeft);
        }

        private void Album_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleMenuFlyoutItem { Tag: int albumId } toggle)
            {
                return;
            }

            if (toggle.IsChecked)
            {
                if (!_albumIds.Contains(albumId))
                {
                    _albumIds.Add(albumId);
                }
            }
            else
            {
                _albumIds.Remove(albumId);
            }

            UpdateAlbumsList();
        }

        // Made at once and chosen, so that the story is posted into it.
        private async void CreateAlbum()
        {
            var popup = new InputPopup(InputPopupType.Text)
            {
                Title = Strings.StoriesAlbumCreateNew,
                Header = Strings.StoriesAlbumAddHint,
                PlaceholderText = Strings.StoriesAlbumTitleInputHint,
                PrimaryButtonText = Strings.Create,
                PrimaryButtonStyle = BootStrapper.Current.Resources["AccentButtonStyle"] as Style,
                SecondaryButtonText = Strings.Cancel,
                MinLength = 1,
                MaxLength = 12
            };

            var chatId = _chatId;

            var confirm = await _navigationService.ShowPopupAsync(popup, requestedTheme: ElementTheme.Dark);
            if (confirm != ModalPopupResult.Primary)
            {
                return;
            }

            var response = await _clientService.SendAsync(new CreateStoryAlbum(chatId, popup.Text, Vector<int>.Empty));
            if (response is StoryAlbum album && chatId == _chatId && _albums != null)
            {
                _albums.Add(album);
                _albumIds.Add(album.Id);

                UpdateAlbums();
            }
            else if (response is Error error)
            {
                ToastPopup.ShowError(XamlRoot, error);
            }
        }

        #endregion

        #region Block list

        private async void LoadBlockList()
        {
            var response = await _clientService.SendAsync(new GetBlockedMessageSenders(new BlockListStories(), 0, 100));
            if (response is MessageSenders senders)
            {
                _blockList = senders.Senders
                    .OfType<MessageSenderUser>()
                    .Select(x => x.UserId)
                    .ToList();

                _blockListCount = senders.TotalCount;
                UpdateBlockList();
            }
        }

        private void UpdateBlockList()
        {
            BlockList.Text = _blockListCount > 0
                ? Locale.Declension(Strings.R.StoryBlockList, _blockListCount)
                : Strings.StoryBlockListEmpty;
        }

        // The list is the account's, not the story's, so it changes as soon as it is picked.
        private async void BlockList_Click(object sender, TextUrlClickEventArgs e)
        {
            var previous = _blockList;

            var users = await PickUsersAsync(Strings.StoryPrivacyAlertBlocklistTitle, previous);
            if (users == null)
            {
                return;
            }

            var blockList = new List<long>(users);

            foreach (var userId in users.Except(previous))
            {
                _clientService.Send(new SetMessageSenderBlockList(new MessageSenderUser(userId), new BlockListStories()));
            }

            foreach (var userId in previous.Except(users))
            {
                // The picker shows only contacts, so anyone else on the list could not have been
                // taken off it.
                if (_clientService.TryGetUser(userId, out User user) && !user.IsContact)
                {
                    blockList.Add(userId);
                    continue;
                }

                _clientService.Send(new SetMessageSenderBlockList(new MessageSenderUser(userId), null));
            }

            _blockListCount += blockList.Count - previous.Count;
            _blockList = blockList;

            UpdateBlockList();
        }

        #endregion

        private async void LoadCloseFriends()
        {
            var response = await _clientService.SendAsync(new GetCloseFriends());
            if (response is Td.Api.Users users)
            {
                _closeFriends = users.UserIds.ToList();
                UpdateList();
            }
        }

        private void UpdateList()
        {
            EveryoneList.Content = _exceptFromEveryone.Count > 0
                ? Locale.Declension(Strings.R.StoryPrivacyOptionExcludePeople, _exceptFromEveryone.Count)
                : Strings.StoryPrivacyOptionContactsDetail;

            ContactsList.Content = _exceptFromContacts.Count > 0
                ? Locale.Declension(Strings.R.StoryPrivacyOptionExcludePeople, _exceptFromContacts.Count)
                : Strings.StoryPrivacyOptionContactsDetail;

            CloseFriendsList.Content = _closeFriends is { Count: > 0 }
                ? Locale.Declension(Strings.R.StoryPrivacyOptionPeople, _closeFriends.Count)
                : Strings.StoryPrivacyOptionCloseFriendsDetail;

            SelectedContactsList.Content = _selectedContacts.Count > 0
                ? Locale.Declension(Strings.R.StoryPrivacyOptionPeople, _selectedContacts.Count)
                : Strings.StoryPrivacyOptionSelectedContactsDetail;
        }

        // Editing an option's list chooses the option too.
        private void List_Click(object sender, RoutedEventArgs e)
        {
            var option = sender == EveryoneList ? Everyone
                : sender == ContactsList ? Contacts
                : sender == CloseFriendsList ? CloseFriends
                : SelectedContacts;

            option.IsChecked = true;
            EditList(option);
        }

        private async void EditList(RadioButton option)
        {
            string title;
            List<long> list;

            if (option == Everyone)
            {
                title = Strings.StoryPrivacyAlertExcludeFromEveryoneTitle;
                list = _exceptFromEveryone;
            }
            else if (option == Contacts)
            {
                title = Strings.StoryPrivacyAlertExcludedContactsTitle;
                list = _exceptFromContacts;
            }
            else if (option == CloseFriends)
            {
                title = Strings.StoryPrivacyAlertCloseFriendsTitle;
                list = _closeFriends ?? new List<long>();
            }
            else
            {
                title = Strings.StoryPrivacyAlertSelectContactsTitle;
                list = _selectedContacts;
            }

            var users = await PickUsersAsync(title, list);
            if (users == null)
            {
                return;
            }

            if (option == Everyone)
            {
                _exceptFromEveryone = users;
            }
            else if (option == Contacts)
            {
                _exceptFromContacts = users;
            }
            else if (option == CloseFriends)
            {
                // The list is the account's, not the story's.
                _closeFriends = users;
                _clientService.Send(new SetCloseFriends(new Vector<long>(users)));
            }
            else
            {
                _selectedContacts = users;
            }

            UpdateList();
        }

        // Contacts, starting from the given ones; null if the picker was cancelled.
        private async Task<List<long>> PickUsersAsync(string title, List<long> selected)
        {
            // The picker preselects chats: a user's private chat has the user's id, once it is loaded.
            foreach (var userId in selected)
            {
                await _clientService.SendAsync(new CreatePrivateChat(userId, true));
            }

            var chats = await ChooseChatsPopup.PickChatsAsync(_navigationService, title, selected.ToArray(), ChooseChatsOptions.Contacts, ListViewSelectionMode.Multiple, true);
            if (chats == null)
            {
                return null;
            }

            var users = new List<long>();

            foreach (var chat in chats)
            {
                if (chat.Type is ChatTypePrivate privateChat)
                {
                    users.Add(privateChat.UserId);
                }
            }

            return users;
        }

        private void Period_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Period.SelectedIndex < 0)
            {
                return;
            }

            var hours = Periods[Period.SelectedIndex];

            // A locked period is not taken: the selection goes back to what was there.
            if (hours != DefaultPeriod && !_clientService.IsPremium)
            {
                ToastPopup.ShowPromo(_navigationService, Locale.Declension(Strings.R.StoryPeriodPremium, hours), Strings.OptionPremiumRequiredButton, new PremiumSourceStoryFeature(new PremiumStoryFeatureCustomExpirationDuration()));

                Period.SelectedIndex = Array.IndexOf(Periods, _period);
                return;
            }

            _period = hours;
            UpdateKeepInfo();
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            Accept();
        }

        private void CaptionInput_Submitted(object sender, EventArgs e)
        {
            Accept();
        }

        #region Caption

        public IAutocompleteCollection Autocomplete
        {
            get => _autocomplete;
            set
            {
                _autocomplete = value;

                ListAutocomplete.ItemsSource = value;
                ListAutocomplete.Visibility = value != null
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                if (value != null)
                {
                    ListAutocomplete.Orientation = value.Orientation;
                }
            }
        }

        private void CaptionInput_Loaded(object sender, RoutedEventArgs e)
        {
            _caption ??= new CaptionController(CaptionInput, CaptionInput, this, _clientService)
            {
                ListAutocomplete = ListAutocomplete
            };
        }

        private void CaptionInput_Unloaded(object sender, RoutedEventArgs e)
        {
            _caption?.Detach();
            _caption = null;

            Autocomplete = null;
        }

        private void Autocomplete_ItemClick(object sender, ItemClickEventArgs e)
        {
            _caption?.Accept(e.ClickedItem);
        }

        private void Autocomplete_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

            if (args.Item is User user && args.ItemContainer.ContentTemplateRoot is Grid content)
            {
                var photo = content.Children[0] as ProfilePicture;
                var title = content.Children[1] as TextBlock;

                var name = title.Inlines[0] as Run;
                var username = title.Inlines[1] as Run;

                name.Text = user.FullName();
                username.Text = user.HasActiveUsername(out string usernameValue)
                    ? $" @{usernameValue}"
                    : string.Empty;

                photo.Source = ProfilePictureSource.User(_clientService, user);
            }
            else if (args.Item is string hashtag && args.ItemContainer.ContentTemplateRoot is Grid hashtagContent)
            {
                var title = hashtagContent.Children[0] as TextBlock;
                title.Text = hashtag;
            }

            args.Handled = true;
        }

        // Counted in code points, as the server does.
        private void CaptionInput_ContentChanged(object sender, EventArgs e)
        {
            var text = CaptionInput.GetFormattedText()?.Text ?? string.Empty;
            var length = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsLowSurrogate(text[i]))
                {
                    length++;
                }
            }

            var previous = _captionLength;
            _captionLength = length;

            if (_captionLimit <= 0)
            {
                return;
            }

            var left = _captionLimit - length;
            var full = left <= 0;

            CaptionCounter.Visibility = left < CounterThreshold
                ? Visibility.Visible
                : Visibility.Collapsed;

            CaptionCounterLeft.Text = CaptionCounterOver.Text = left.ToString();
            CaptionCounterLeft.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            CaptionCounterOver.Visibility = full ? Visibility.Visible : Visibility.Collapsed;

            Send.IsEnabled = left >= 0;

            if (left >= 0)
            {
                _captionLimitToast = false;
            }
            else if (length > previous && !_clientService.IsPremium && _clientService.IsPremiumAvailable)
            {
                ShowCaptionLimit();
            }
        }

        // Once each time the caption goes over, while it grows: Premium allows a longer one.
        private async void ShowCaptionLimit()
        {
            VisualUtilities.ShakeView(CaptionCounter);

            if (_captionLimitToast)
            {
                return;
            }

            _captionLimitToast = true;

            var response = await _clientService.SendAsync(new GetPremiumLimit(new PremiumLimitTypeStoryCaptionLength()));
            if (response is not PremiumLimit limit || limit.DefaultValue <= 0 || _captionLength >= limit.PremiumValue)
            {
                return;
            }

            var times = (int)Math.Round((double)limit.PremiumValue / limit.DefaultValue);
            var text = $"**{Strings.CaptionPremiumTitle}**\n" + Locale.Declension(Strings.R.CaptionPremiumSubtitle, times, limit.PremiumValue);

            ToastPopup.ShowPromo(_navigationService, text, Strings.PremiumMore, new PremiumSourceLimitExceeded(new PremiumLimitTypeStoryCaptionLength()));
        }

        #endregion

        private async void Accept()
        {
            if (_accepting)
            {
                return;
            }

            if (_captionLimit > 0 && _captionLength > _captionLimit)
            {
                VisualUtilities.ShakeView(CaptionCounter);
                return;
            }

            // Typed formatting is already refused, but a contact picked as a mention still adds an entity.
            if (!_clientService.Options.CanUseTextEntitiesInStoryCaption && Caption?.Entities?.Count > 0)
            {
                ToastPopup.ShowFeaturePromo(_navigationService, Strings.StoryPremiumFormatting, new PremiumFeatureUpgradedStories());
                return;
            }

            if (IsSelf)
            {
                // A story for selected contacts needs some to be for.
                if (SelectedContacts.IsChecked == true && _selectedContacts.Count == 0)
                {
                    EditList(SelectedContacts);
                    return;
                }

                _accepting = true;

                var restricted = await GetRestrictedMentionsAsync();
                if (restricted.Count > 0)
                {
                    var users = string.Join(", ", restricted.Take(2));
                    var confirm = await _navigationService.ShowPopupAsync(string.Format(Strings.StoryRestrictionsInfo, users), Strings.StoryRestrictions, Strings.Proceed, Strings.Cancel, requestedTheme: ElementTheme.Dark);

                    if (confirm != ModalPopupResult.Primary)
                    {
                        _accepting = false;
                        return;
                    }
                }

                _settings.Privacy = Contacts.IsChecked == true ? StoryPrivacyMode.Contacts
                    : CloseFriends.IsChecked == true ? StoryPrivacyMode.CloseFriends
                    : SelectedContacts.IsChecked == true ? StoryPrivacyMode.SelectedContacts
                    : StoryPrivacyMode.Everyone;

                _settings.ExceptFromEveryone = _exceptFromEveryone;
                _settings.ExceptFromContacts = _exceptFromContacts;
                _settings.SelectedContacts = _selectedContacts;
            }

            _settings.AllowScreenshots = AllowScreenshots.IsChecked == true;
            _settings.Keep = Keep.IsChecked == true;
            _settings.Period = _period;

            _accepting = false;
            Hide(ModalPopupResult.Primary);
        }

        // The people mentioned in the caption whom the chosen privacy keeps from seeing the story,
        // as the caption names them.
        private async Task<List<string>> GetRestrictedMentionsAsync()
        {
            var restricted = new List<string>();

            var caption = Caption;
            if (string.IsNullOrEmpty(caption?.Text))
            {
                return restricted;
            }

            var myId = _clientService.Options.MyId;
            var seen = new HashSet<long>();

            // Picked mentions are entities of the caption; typed ones are only its text.
            var entities = (caption.Entities ?? Enumerable.Empty<TextEntity>())
                .Concat(ClientEx.GetTextEntities(caption.Text))
                .ToList();

            foreach (var entity in entities)
            {
                User user = null;

                if (entity.Type is TextEntityTypeMentionName mentionName)
                {
                    _clientService.TryGetUser(mentionName.UserId, out user);
                }
                else if (entity.Type is TextEntityTypeMention && entity.Length > 1)
                {
                    var username = caption.Text.Substring(entity.Offset + 1, entity.Length - 1);
                    var response = await _clientService.SendAsync(new SearchPublicChat(username));
                    if (response is Chat chat)
                    {
                        _clientService.TryGetUser(chat, out user);
                    }
                }

                if (user == null || user.Id == myId || user.Type is UserTypeBot || !seen.Add(user.Id))
                {
                    continue;
                }

                if (!CanViewStory(user))
                {
                    restricted.Add(user.HasActiveUsername(out string usernameValue)
                        ? $"**@{usernameValue}**"
                        : $"**{user.FullName()}**");
                }
            }

            return restricted;
        }

        private bool CanViewStory(User user)
        {
            if (Contacts.IsChecked == true)
            {
                return user.IsContact && !_exceptFromContacts.Contains(user.Id);
            }
            else if (CloseFriends.IsChecked == true)
            {
                return _closeFriends?.Contains(user.Id) ?? user.IsCloseFriend;
            }
            else if (SelectedContacts.IsChecked == true)
            {
                return _selectedContacts.Contains(user.Id);
            }

            return !_exceptFromEveryone.Contains(user.Id);
        }

        private void Emoji_Click(object sender, RoutedEventArgs e)
        {
            EmojiPanel.ViewModel.Update();
            EmojiFlyout.ShowAt(CaptionPanel, FlyoutPlacementMode.Top, FlyoutShowMode.Transient, source: EmojiButton);
        }

        private void Emoji_ItemClick(object sender, EmojiDrawerItemClickEventArgs e)
        {
            if (e.ClickedItem is EmojiData emoji)
            {
                CaptionInput.InsertText(emoji.Value);
                CaptionInput.FocusInput(FocusState.Programmatic);
            }
            else if (e.ClickedItem is StickerViewModel sticker)
            {
                CaptionInput.InsertEmoji(sticker);
                CaptionInput.FocusInput(FocusState.Programmatic);
            }
        }
    }
}
