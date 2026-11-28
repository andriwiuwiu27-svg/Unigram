//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.Views.Users;
using Windows.ApplicationModel.Contacts;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Telegram.Controls.Messages.Content
{
    public sealed partial class ContactContent : HyperlinkButton, IContent
    {
        private MessageViewModel _message;
        public MessageViewModel Message => _message;

        // A contact on Telegram can be written to, and added when it is not in the address book
        // yet; one that is not can only be shown.
        private bool _canMessage;
        private bool _canAdd;

        public ContactContent(MessageViewModel message)
        {
            _message = message;

            DefaultStyleKey = typeof(ContactContent);
        }

        #region InitializeComponent

        private DashPath AccentDash;
        private MessageReplyPattern Pattern;
        private ProfilePicture Photo;
        private TextBlock Title;
        private TextBlock Subtitle;
        private Button Primary;
        private TextBlock PrimaryText;
        private Button Secondary;
        private bool _templateApplied;

        protected override void OnApplyTemplate()
        {
            AccentDash = GetTemplateChild(nameof(AccentDash)) as DashPath;
            Photo = GetTemplateChild(nameof(Photo)) as ProfilePicture;
            Title = GetTemplateChild(nameof(Title)) as TextBlock;
            Subtitle = GetTemplateChild(nameof(Subtitle)) as TextBlock;
            Primary = GetTemplateChild(nameof(Primary)) as Button;
            PrimaryText = GetTemplateChild(nameof(PrimaryText)) as TextBlock;
            Secondary = GetTemplateChild(nameof(Secondary)) as Button;

            Click += Root_Click;
            Primary.Click += Primary_Click;
            Secondary.Click += Secondary_Click;

            _templateApplied = true;

            if (_message != null)
            {
                UpdateMessage(_message);
            }
        }

        #endregion

        public void UpdateMessage(MessageViewModel message)
        {
            _message = message;

            var contact = message.Content as MessageContact;
            if (contact == null || !_templateApplied)
            {
                return;
            }

            message.ClientService.TryGetUser(contact.Contact.UserId, out User user);

            if (user != null)
            {
                Photo.Source = ProfilePictureSource.User(message.ClientService, user);
            }
            else
            {
                Photo.Source = ProfilePictureSourceText.GetNameForUser(contact.Contact.FirstName, contact.Contact.LastName);
            }

            Title.Text = contact.Contact.GetFullName();
            Subtitle.Text = PhoneNumber.Format(contact.Contact.PhoneNumber);

            AutomationProperties.SetName(this, Title.Text + ", " + Subtitle.Text);

            _canMessage = user != null;
            _canAdd = user is { IsContact: false };

            PrimaryText.Text = _canMessage
                ? Strings.SharedContactMessage
                : Strings.ViewDetails;

            Grid.SetColumnSpan(Primary, _canAdd ? 1 : 2);
            Secondary.Visibility = _canAdd
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateAccent(message, user);
        }

        // The colours are the contact's own, as the name and picture are. An outgoing bubble keeps
        // the theme's, which are the ones made to read on it.
        private void UpdateAccent(MessageViewModel message, User user)
        {
            var outgoing = message.IsOutgoing && !message.IsChannelPost;
            if (outgoing)
            {
                ClearValue(BackgroundProperty);
                ClearValue(HeaderBrushProperty);
                ClearValue(BorderBrushProperty);

                AccentDash.Stripe1 = default;
                AccentDash.Stripe2 = default;

                SetPattern(message.ClientService, 0, 0);
                return;
            }

            var giftColors = user?.UpgradedGiftColors;
            if (giftColors != null)
            {
                var colors = giftColors.ForTheme(ActualTheme, out var accentColor);
                Background =
                    HeaderBrush = new SolidColorBrush(accentColor);

                BorderBrush = new SolidColorBrush(colors[0]);

                AccentDash.Stripe1 = colors.Count > 1 ? colors[1] : default;
                AccentDash.Stripe2 = colors.Count > 2 ? colors[2] : default;

                SetPattern(message.ClientService, giftColors.SymbolCustomEmojiId, giftColors.ModelCustomEmojiId);
                return;
            }

            // Someone who is not on Telegram has no colour of their own, and takes the first one.
            var accent = message.ClientService.GetAccentColor(user?.AccentColorId ?? 0);
            if (accent != null)
            {
                var colors = accent.ForTheme(ActualTheme);
                Background =
                    HeaderBrush =
                    BorderBrush = new SolidColorBrush(colors[0]);

                AccentDash.Stripe1 = colors.Count > 1 ? colors[1] : default;
                AccentDash.Stripe2 = colors.Count > 2 ? colors[2] : default;
            }
            else
            {
                ClearValue(BackgroundProperty);
                ClearValue(HeaderBrushProperty);
                ClearValue(BorderBrushProperty);

                AccentDash.Stripe1 = default;
                AccentDash.Stripe2 = default;
            }

            SetPattern(message.ClientService, user?.BackgroundCustomEmojiId ?? 0, 0);
        }

        private void SetPattern(IClientService clientService, long source, long model)
        {
            if (Pattern == null)
            {
                if (!_templateApplied || (source == 0 && model == 0))
                {
                    return;
                }

                Pattern = GetTemplateChild(nameof(Pattern)) as MessageReplyPattern;
            }

            Pattern.Update(clientService, source, model);
        }

        public void Recycle()
        {
            _message = null;
        }

        public bool IsValid(MessageContent content, bool primary)
        {
            return content is MessageContact;
        }

        // With a single button the whole card does what it does; with two, it opens the profile.
        private void Root_Click(object sender, RoutedEventArgs e)
        {
            var contact = _message?.Content as MessageContact;
            if (contact == null)
            {
                return;
            }

            if (_canAdd)
            {
                _message.Delegate.OpenUser(contact.Contact.UserId);
            }
            else
            {
                Primary_Click(sender, e);
            }
        }

        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            var contact = _message?.Content as MessageContact;
            if (contact == null)
            {
                return;
            }

            if (_canMessage)
            {
                _message.Delegate.NavigationService.NavigateToUser(contact.Contact.UserId, true);
            }
            else
            {
                ShowContactCard(contact.Contact);
            }
        }

        private void Secondary_Click(object sender, RoutedEventArgs e)
        {
            var contact = _message?.Content as MessageContact;
            if (contact == null)
            {
                return;
            }

            _message.Delegate.NavigationService.Navigate(typeof(UserEditPage), contact.Contact.UserId);
        }

        private async void ShowContactCard(Td.Api.Contact contact)
        {
            try
            {
                Windows.ApplicationModel.Contacts.Contact system;

                if (string.IsNullOrEmpty(contact.Vcard))
                {
                    system = new Windows.ApplicationModel.Contacts.Contact
                    {
                        FirstName = contact.FirstName,
                        LastName = contact.LastName
                    };

                    system.Phones.Add(new ContactPhone
                    {
                        Number = contact.PhoneNumber
                    });
                }
                else
                {
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                        {
                            writer.WriteString(contact.Vcard);
                            await writer.StoreAsync();
                        }

                        var reference = RandomAccessStreamReference.CreateFromStream(stream);
                        system = await ContactManager.ConvertVCardToContactAsync(reference);
                    }
                }

                var transform = TransformToVisual(null);
                var point = transform.TransformPoint(new Point());

                ContactManager.ShowContactCard(system, new Rect(point.X, point.Y, (float)ActualWidth, (float)ActualHeight));
            }
            catch
            {
                // All the remote procedure calls must be wrapped in a try-catch block
            }
        }

        #region HeaderBrush

        public Brush HeaderBrush
        {
            get { return (Brush)GetValue(HeaderBrushProperty); }
            set { SetValue(HeaderBrushProperty, value); }
        }

        public static readonly DependencyProperty HeaderBrushProperty =
            DependencyProperty.Register("HeaderBrush", typeof(Brush), typeof(ContactContent), new PropertyMetadata(null));

        #endregion
    }
}
