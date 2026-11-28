//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Numerics;
using Telegram.Common;
using Telegram.Native.Controls;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Streams;
using Telegram.Td.Api;
using Windows.Foundation;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;

namespace Telegram.Controls.Chats
{
    public sealed partial class ChatTextFlyout : GridEx
    {
        private readonly FormattedTextBox _textBox;
        private readonly IAutocompleteTarget _target;

        private readonly ZoomableListHandler _zoomer;
        private readonly AnimatedListHandler _handler;

        public ViewModelBase ViewModel => _textBox?.DataContext as ViewModelBase;

        private IClientService ClientService => _target != null ? _target.ClientService : ViewModel?.ClientService;

        public ChatTextFlyout(FormattedTextBox textBox, AutocompleteCollection itemsSource)
            : this(itemsSource)
        {
            _textBox = textBox;
        }

        /// <summary>For an editor that is not RichEdit: a picked item is handed to <paramref name="target"/>.</summary>
        public ChatTextFlyout(IAutocompleteTarget target, AutocompleteCollection itemsSource)
            : this(itemsSource)
        {
            _target = target;
        }

        private ChatTextFlyout(AutocompleteCollection itemsSource)
        {
            InitializeComponent();

            // TODO: this might need to change depending on context
            _handler = new AnimatedListHandler(ScrollingHost, AnimatedListType.Stickers);

            _zoomer = new ZoomableListHandler(ScrollingHost);
            _zoomer.Opening += Zoomer_Opening;
            _zoomer.Closing += Zoomer_Closing;

            ScrollingHost.ItemsSource = itemsSource;

            MinWidth = 40;
            MaxWidth = 184;
            MinHeight = 40;
        }

        private void Zoomer_Opening(object sender, EventArgs e)
        {
            _handler.Suspend();
        }

        private void Zoomer_Closing(object sender, EventArgs e)
        {
            _handler.Resume();
        }

        public ListViewBase ControlledList => ScrollingHost;

        public static MenuFlyout CreateFlyout(ChatTextFlyout content)
        {
            var presenter = new MenuFlyoutContent
            {
                Content = content,
                Padding = new Thickness(0)
            };

            var style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));

            var flyout = new MenuFlyout
            {
                AllowFocusOnInteraction = false,
                MenuFlyoutPresenterStyle = style
            };

            flyout.Items.Add(presenter);

            return flyout;
        }

        /// <summary>Opens <paramref name="flyout"/> with its bottom-left corner at <paramref name="position"/>.</summary>
        public static void ShowAt(MenuFlyout flyout, FrameworkElement target, Point position)
        {
            // A menu ignores Placement and opens with its top-left corner at the point. Placement
            // still picks the side it is pushed to out of the exclusion rect, so excluding the target
            // from the point down lifts the menu above the point, and keeps it there as it resizes.
            flyout.ShowAt(target, new FlyoutShowOptions
            {
                Position = position,
                ExclusionRect = new Rect(0, (float)position.Y, (float)target.ActualWidth, (float)Math.Max(1, target.ActualHeight - position.Y)),
                Placement = FlyoutPlacementMode.TopEdgeAlignedLeft,
                ShowMode = FlyoutShowMode.Transient
            });
        }

        public static bool TryResolveFlyout(MenuFlyout flyout, out ChatTextFlyout presenter)
        {
            if (flyout?.Items.Count > 0 && flyout.Items[0] is MenuFlyoutContent content)
            {
                presenter = content.Content as ChatTextFlyout;
                return presenter != null;
            }

            presenter = null;
            return false;
        }

        public IAutocompleteCollection ItemsSource
        {
            get
            {
                if (ScrollingHost.ItemsSource is AutocompleteCollection collection)
                {
                    return collection.Source;
                }

                return null;
            }
        }

        public void Update(IAutocompleteCollection source)
        {
            if (ScrollingHost.ItemsSource is AutocompleteCollection collection)
            {
                collection.Replace(source);
            }
        }

        protected override void OnUnloaded()
        {
            _handler.UnloadItems();
        }

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TextGridViewItem();
                args.ItemContainer.Style = sender.ItemContainerStyle;

                _zoomer.ElementPrepared(args.ItemContainer);
            }

            if (args.Item is EmojiData or Sticker)
            {
                args.ItemContainer.Margin = new Thickness(4, 4, 0, 4);
                args.ItemContainer.CornerRadius = new CornerRadius(4, 4, 4, 4);
            }
            else
            {
                args.ItemContainer.Margin = new Thickness();
                args.ItemContainer.CornerRadius = new CornerRadius();
            }

            args.ItemContainer.MinHeight = 0;

            args.ItemContainer.ContentTemplate = sender.ItemTemplateSelector.SelectTemplate(args.Item, args.ItemContainer);
            args.IsContainerPrepared = true;
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

            if (args.Item is Sticker sticker)
            {
                var content = args.ItemContainer.ContentTemplateRoot as Grid;

                var animated = content.Children[0] as AnimatedImage;
                animated.Source = DelayedFileSource.FromSticker(ClientService, sticker);

                AutomationProperties.SetName(args.ItemContainer, sticker.Emoji);
            }
            else if (args.Item is EmojiData emoji)
            {
                AutomationProperties.SetName(args.ItemContainer, emoji.Value);
            }

            args.Handled = true;
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (_target != null)
            {
                _target.Accept(e.ClickedItem);
                return;
            }

            var selection = _textBox.Document.Selection.GetClone();
            var entity = AutocompleteEntityFinder.Search(selection, out string result, out int index);

            void InsertText(string insert)
            {
                var range = _textBox.Document.GetRange(index, _textBox.Document.Selection.StartPosition);
                range.SetText(TextSetOptions.None, insert);

                _textBox.Document.Selection.StartPosition = index + insert.Length;
            }

            if (_textBox is ChatTextBox chatTextBox)
            {
                chatTextBox.HideEmoji();
            }

            if (e.ClickedItem is EmojiData emoji)
            {
                ViewModel.Settings.RecentEmoji.AddRecentEmoji(emoji);
                InsertText($"{emoji.Value}");
            }
            else if (e.ClickedItem is Sticker sticker && sticker.FullType is StickerFullTypeCustomEmoji customEmoji)
            {
                ViewModel.Settings.RecentEmoji.AddRecentEmoji(sticker.Emoji, customEmoji.CustomEmojiId);

                var range = _textBox.Document.GetRange(index, _textBox.Document.Selection.StartPosition);

                _textBox.InsertEmoji(range, sticker.Emoji, customEmoji.CustomEmojiId);
                _textBox.Document.Selection.StartPosition = range.EndPosition + 1;

                var precedingRange = _textBox.Document.GetRange(index, index);
                var offset = index;

                // Let's see if the current emoji is preceded by the same emoji and replace all the occurrences
                while (AutocompleteEntityFinder.TrySearch(precedingRange, out AutocompleteEntity precedingEntity, out string precedingResult, out int precedingIndex))
                {
                    if (precedingEntity != entity || precedingResult != result)
                    {
                        break;
                    }

                    precedingRange = _textBox.Document.GetRange(precedingIndex, offset);
                    _textBox.InsertEmoji(precedingRange, sticker.Emoji, customEmoji.CustomEmojiId);

                    precedingRange = _textBox.Document.GetRange(precedingIndex, precedingIndex);
                    offset = precedingIndex;
                }
            }
        }
    }

    /// <summary>Where <see cref="ChatTextFlyout"/> puts what is picked, when that is not a RichEdit box.</summary>
    public interface IAutocompleteTarget
    {
        IClientService ClientService { get; }

        void Accept(object item);
    }
}
