//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Views.Stars.Popups
{
    /// <summary>
    /// The last step before an on-chain gift transaction is signed: what it does, what it costs,
    /// and a button that signs and sends it while the popup waits.
    /// </summary>
    public sealed partial class GiftOnChainConfirmPopup : ModalPopup
    {
        private readonly Func<Task<bool>> _send;

        private DispatcherTimer _expiration;
        private int _expirationDate;

        private bool _sending;

        /// <param name="send">Signs and sends; true when the transaction left, which closes the
        /// popup. False leaves it open, after whatever went wrong has been said.</param>
        public GiftOnChainConfirmPopup(string title, string text, string primary, Func<Task<bool>> send)
        {
            InitializeComponent();

            _send = send;

            Title = title;
            PrimaryButtonText = primary;
            SecondaryButtonText = Strings.Cancel;

            if (string.IsNullOrEmpty(text))
            {
                // An empty TextBlock still measures a line.
                MessageText.Visibility = Visibility.Collapsed;
            }
            else
            {
                TextBlockHelper.SetMarkdown(MessageText, text);
            }
        }

        /// <summary>
        /// True when the popup closed because <see cref="CloseAt"/> was reached.
        /// </summary>
        public bool IsExpired { get; private set; }

        public void AddRow(string header, string value)
        {
            Table.Items.Add(new TableViewItem
            {
                Header = header,
                Content = value
            });

            Table.Visibility = Visibility.Visible;
        }

        public void SetNote(string text)
        {
            NoteText.Text = text;
            NoteText.Visibility = Visibility.Visible;
        }

        public void SetFee(long nanograms)
        {
            if (nanograms > 0)
            {
                FeeText.Text = string.Format(Strings.GiftOnchainNetworkFee, GiftHelper.FormatGrams(nanograms));
                FeeText.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Closes the popup at <paramref name="date"/>, unless it is sending by then.
        /// </summary>
        public void CloseAt(int date)
        {
            _expirationDate = date;
        }

        protected override void OnLoaded()
        {
            base.OnLoaded();

            if (_expirationDate > 0)
            {
                _expiration ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _expiration.Tick += OnExpirationTick;
                _expiration.Start();
            }
        }

        protected override void OnUnloaded()
        {
            base.OnUnloaded();

            if (_expiration != null)
            {
                _expiration.Stop();
                _expiration.Tick -= OnExpirationTick;
            }
        }

        private void OnExpirationTick(object sender, object e)
        {
            if (_sending || DateTime.Now.ToUnixTimeSeconds() < _expirationDate)
            {
                return;
            }

            _expiration.Stop();

            IsExpired = true;
            Hide();
        }

        private async void OnPrimaryButtonClick(ModalPopup sender, ModalPopupButtonClickEventArgs args)
        {
            // A second click while the first is signing would send it twice.
            args.Cancel = _sending;

            if (_sending)
            {
                return;
            }

            var deferral = args.GetDeferral();

            _sending = true;
            IsPrimaryButtonPending = true;

            if (!await _send())
            {
                _sending = false;

                IsPrimaryButtonPending = false;
                args.Cancel = true;
            }

            deferral.Complete();
        }
    }
}
