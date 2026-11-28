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
using Telegram.Controls.Media;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Streams;
using Telegram.Td.Api;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;

namespace Telegram.Views.Popups
{
    public sealed partial class TextEditorPopup : ModalPopup
    {
        private readonly IClientService _clientService;
        private readonly INavigationService _navigationService;
        private readonly Chat _chat;
        private readonly FormattedText _text;

        // Results by request key. Null while the request is in flight; a failure is not kept, so
        // coming back to it asks again.
        private readonly Dictionary<string, FormattedText> _translations = new();
        private readonly Dictionary<string, FormattedText> _styles = new();

        private string _translateKey;
        private string _styleKey;

        private string _translateToLanguage;
        private Run _translateToName;

        private bool _fixRequested;
        private FixedText _fix;

        // Per popup rather than per tab: a failed request turns Apply into OK, and the daily limit
        // replaces it with Increase Limit, until a later request clears them.
        private bool _failed;
        private bool _limitReached;

        public TextEditorPopup(IClientService clientService, INavigationService navigationService, Chat chat, FormattedText text)
        {
            InitializeComponent();

            _clientService = clientService;
            _navigationService = navigationService;
            _chat = chat;
            _text = text;

            _translateToLanguage = navigationService.Session.Settings.Translate.To;

            TabStyleItems.ItemsSource = clientService.TextCompositionStyles;
            TabStyleOutput.SetText(clientService, text);

            Navigation.SelectedIndex = 1;
            Navigation.SelectionChanged += Navigation_SelectionChanged;

            // Chat parameters are not supported in secret chats.
            if (chat != null && chat.Type is not ChatTypeSecret)
            {
                AlwaysUse.Visibility = Visibility.Visible;
                Preselect(chat.TextCompositionParameters);
            }

            Title = Strings.AIEditor;
            PrimaryButtonText = Strings.AIEditorApply;

            Closed += OnClosed;

            clientService.Session.Aggregator.Subscribe<UpdateTextCompositionStyles>(this, Handle);
        }

        public FormattedText Result { get; private set; }

        /// <summary>Whether applying turned on the chat's "Always use in this chat", which was off.</summary>
        public bool TurnedOnAlwaysUse { get; private set; }

        private void Handle(UpdateTextCompositionStyles update)
        {
            this.BeginOnUIThread(() =>
            {
                // An edited style keeps its name, so what it produced before is stale.
                if (TabStyleItems.ItemsSource is IList<TextCompositionStyle> previous)
                {
                    foreach (var style in update.Styles)
                    {
                        var old = previous.FirstOrDefault(x => x.Name == style.Name);
                        if (old != null && old.Prompt != style.Prompt)
                        {
                            _styles.Remove(StyleKey(style.Name, false));
                            _styles.Remove(StyleKey(style.Name, true));
                        }
                    }
                }

                var selected = TabStyleItems.SelectedItem as TextCompositionStyle;

                TabStyleItems.ItemsSource = update.Styles;
                TabStyleItems.SelectedItem = update.Styles.FirstOrDefault(x => x.Name == selected?.Name);
            });
        }

        private void OnClosed(ModalPopup sender, ModalPopupClosedEventArgs args)
        {
            Closed -= OnClosed;
            _clientService.Session.Aggregator.Unsubscribe(this);
        }

        private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TabTranslate.Visibility = Navigation.SelectedIndex == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            TabStyle.Visibility = Navigation.SelectedIndex == 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            TabFix.Visibility = Navigation.SelectedIndex == 2
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (Navigation.SelectedIndex == 0 && _translateToName == null)
            {
                InitializeTranslate();
            }
            else if (Navigation.SelectedIndex == 2 && !_fixRequested)
            {
                InitializeFix();
            }

            UpdateAlwaysUse();
        }

        #region Always use

        private void Preselect(TextCompositionParameters parameters)
        {
            if (parameters == null)
            {
                return;
            }

            // The tabs hold one choice each, so parameters that combine them open on the first
            // that applies, in the order the tabs are checked here.
            if (parameters.FixText)
            {
                Navigation.SelectedIndex = 2;
            }
            else if (parameters.TranslateToLanguageCode.Length > 0)
            {
                _translateToLanguage = parameters.TranslateToLanguageCode;
                TabTranslateEmoji.IsChecked = parameters.AddEmojis;
                Navigation.SelectedIndex = 0;
            }
            else if (parameters.StyleName.Length > 0)
            {
                TabStyleEmoji.IsChecked = parameters.AddEmojis;
                TabStyleItems.SelectedItem = _clientService.TextCompositionStyles.FirstOrDefault(x => x.Name == parameters.StyleName);
            }

            // A style that has since been removed leaves nothing to check.
            if (GetParameters() != null)
            {
                AlwaysUseCheck.IsChecked = true;
            }

            UpdateAlwaysUse();
        }

        private TextCompositionParameters GetParameters()
        {
            switch (Navigation.SelectedIndex)
            {
                case 0:
                    return new TextCompositionParameters(_translateToLanguage, string.Empty, TabTranslateEmoji.IsChecked is true, false);
                case 1 when TabStyleItems.SelectedItem is TextCompositionStyle style:
                    return new TextCompositionParameters(string.Empty, style.Name, TabStyleEmoji.IsChecked is true, false);
                case 2:
                    return new TextCompositionParameters(string.Empty, string.Empty, false, true);
                default:
                    return null;
            }
        }

        private void UpdateAlwaysUse()
        {
            string selection;
            bool addEmojis;

            switch (Navigation.SelectedIndex)
            {
                case 0:
                    selection = TranslateService.LanguageName(_translateToLanguage) ?? _translateToLanguage;
                    addEmojis = TabTranslateEmoji.IsChecked is true;
                    break;
                case 1 when TabStyleItems.SelectedItem is TextCompositionStyle style:
                    selection = style.Title;
                    addEmojis = TabStyleEmoji.IsChecked is true;
                    break;
                case 2:
                    selection = Strings.AIEditorTabFix;
                    addEmojis = false;
                    break;
                default:
                    AlwaysUseSelection.Visibility = Visibility.Collapsed;
                    return;
            }

            AlwaysUseSelection.Text = addEmojis
                ? "→ " + selection + " + " + Strings.Emoji
                : "→ " + selection;
            AlwaysUseSelection.Visibility = Visibility.Visible;
        }

        private void AlwaysUseCheck_Checked(object sender, RoutedEventArgs e)
        {
            if (GetParameters() == null)
            {
                AlwaysUseCheck.IsChecked = false;
                VisualUtilities.ShakeView(AlwaysUseCheck);
            }
        }

        private void SaveParameters()
        {
            if (AlwaysUse.Visibility == Visibility.Collapsed)
            {
                return;
            }

            TextCompositionParameters parameters = null;

            if (AlwaysUseCheck.IsChecked is true)
            {
                parameters = GetParameters();

                // Checked with nothing chosen on this tab: leave the chat as it was.
                if (parameters == null)
                {
                    return;
                }
            }

            if (AreEqual(_chat.TextCompositionParameters, parameters))
            {
                return;
            }

            TurnedOnAlwaysUse = _chat.TextCompositionParameters == null;
            _clientService.Send(new SetChatTextCompositionParameters(_chat.Id, parameters));
        }

        private static bool AreEqual(TextCompositionParameters x, TextCompositionParameters y)
        {
            if (x == null || y == null)
            {
                return x == y;
            }

            return x.TranslateToLanguageCode == y.TranslateToLanguageCode
                && x.StyleName == y.StyleName
                && x.AddEmojis == y.AddEmojis
                && x.FixText == y.FixText;
        }

        #endregion

        #region Translate

        private void InitializeTranslate()
        {
            TabTranslateOriginal.SetText(_clientService, _text);
            TabTranslateOutput.SetText(_clientService, _text);

            _translateToName = new Run();

            var hyperlink = new Hyperlink();
            hyperlink.Click += TabTranslateLanguage_Click;
            hyperlink.UnderlineStyle = UnderlineStyle.None;
            hyperlink.Inlines.Add(_translateToName);

            var format = Strings.AIEditorTo;
            var index = format.IndexOf("{0}");

            TabTranslateTo.Inlines.Clear();
            TabTranslateTo.Inlines.Add(format.Substring(0, index));
            TabTranslateTo.Inlines.Add(hyperlink);
            TabTranslateTo.Inlines.Add(format.Substring(index + 3));

            UpdateTranslateLanguage(_translateToLanguage);
        }

        private void UpdateTranslateLanguage(string languageId)
        {
            _translateToLanguage = languageId;
            _translateToName.Text = TranslateService.LanguageName(languageId) ?? languageId;

            UpdateTranslate();
        }

        private void UpdateTranslate()
        {
            var addEmojis = TabTranslateEmoji.IsChecked is true;
            _translateKey = _translateToLanguage + "_" + addEmojis;

            Compose(_translations, _translateKey, TabTranslateOutput, _translateToLanguage, string.Empty, addEmojis);
            UpdateAlwaysUse();
        }

        private async void TabTranslateLanguage_Click(Hyperlink sender, HyperlinkClickEventArgs args)
        {
            var popup = new TranslateToPopup();

            var confirm = await _navigationService.ShowPopupAsync(popup);
            if (confirm == ModalPopupResult.Primary && popup.SelectedItem != null)
            {
                UpdateTranslateLanguage(popup.SelectedItem);
            }
        }

        private void TabTranslateEmoji_Checked(object sender, RoutedEventArgs e)
        {
            // Preselection sets it before the tab has been opened.
            if (_translateToName != null)
            {
                UpdateTranslate();
            }
        }

        #endregion

        #region Style

        private static string StyleKey(string name, bool addEmojis)
        {
            return name + "_" + addEmojis;
        }

        private void TabStyleItems_ChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TopNavViewItem
                {
                    ContentTemplate = sender.ItemTemplate,
                    Style = sender.ItemContainerStyle
                };

                args.ItemContainer.ContextRequested += OnContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void TabStyleItems_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }
            else if (args.ItemContainer.ContentTemplateRoot is Grid content && args.Item is TextCompositionStyle style)
            {
                var animated = content.Children[0] as AnimatedImage;
                var text = content.Children[1] as TextBlock;

                animated.Source = new CustomEmojiFileSource(_clientService, style.CustomEmojiId);
                text.Text = style.Title;
            }

            args.Handled = true;
        }

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var style = TabStyleItems.ItemFromContainer(sender) as TextCompositionStyle;
            if (style == null)
            {
                return;
            }

            var flyout = new MenuFlyout();

            if (style.IsCreator)
            {
                flyout.CreateFlyoutItem(EditStyle, style, Strings.AIEditorEditStyle, Icons.Edit);
            }

            flyout.CreateFlyoutItem(ShareStyle, style, Strings.AIEditorShareStyle, Icons.Share);
            flyout.CreateFlyoutItem(DeleteStyle, style, style.IsCreator ? Strings.AIEditorDeleteStyle : Strings.AIEditorRemoveStyle, Icons.Delete, destructive: true);

            flyout.ShowAt(sender, args);
        }

        private void EditStyle(TextCompositionStyle style)
        {
            _navigationService.ShowPopup(new TextStylePopup(_clientService, _navigationService, style));
        }

        private void ShareStyle(TextCompositionStyle style)
        {
            _navigationService.ShowPopup(new ChooseChatsPopup(), new ChooseChatsConfigurationPostLink(new InternalLinkTypeTextCompositionStyle(style.Name)));
        }

        private async void DeleteStyle(TextCompositionStyle style)
        {
            if (style.IsCreator)
            {
                var confirm = await _navigationService.ShowPopupAsync(Strings.AIEditorDeleteStyleText, Strings.AIEditorDeleteStyle, Strings.Delete, Strings.Cancel, destructive: true);
                if (confirm == ModalPopupResult.Primary)
                {
                    _clientService.Send(new DeleteTextCompositionStyle(style.Name));
                }
            }
            else
            {
                _clientService.Send(new RemoveTextCompositionStyle(style.Name));
            }
        }

        private void TabStyleCreate_Click(object sender, RoutedEventArgs e)
        {
            _navigationService.ShowPopup(new TextStylePopup(_clientService, _navigationService));
        }

        private void TabStyleItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateStyle();
        }

        private void TabStyleEmoji_Checked(object sender, RoutedEventArgs e)
        {
            UpdateStyle();
        }

        private void UpdateStyle()
        {
            UpdateAlwaysUse();

            if (TabStyleItems.SelectedItem is not TextCompositionStyle style)
            {
                _styleKey = null;
                return;
            }

            var addEmojis = TabStyleEmoji.IsChecked is true;
            _styleKey = StyleKey(style.Name, addEmojis);

            Compose(_styles, _styleKey, TabStyleOutput, string.Empty, style.Name, addEmojis);
        }

        #endregion

        private async void Compose(Dictionary<string, FormattedText> results, string key, FormattedTextBlock output, string languageCode, string styleName, bool addEmojis)
        {
            if (results.TryGetValue(key, out FormattedText cached))
            {
                ShowResult(output, cached);
                return;
            }

            results[key] = null;
            ShowResult(output, null);

            OnRequestStarted();

            var response = await _clientService.SendAsync(new ComposeTextWithAi(_text, languageCode, styleName, addEmojis));
            OnRequestCompleted(response);

            // A failure leaves the skeleton up: the toast says why, and the button what to do.
            if (response is FormattedText text)
            {
                results[key] = text;

                // The language, style or emoji toggle may have moved on while this was in flight.
                var current = results == _translations ? _translateKey : _styleKey;
                if (key == current)
                {
                    ShowResult(output, text);
                }
            }
            else
            {
                results.Remove(key);
            }
        }

        private void OnRequestStarted()
        {
            _failed = false;
            UpdatePrimaryButton();
        }

        private void OnRequestCompleted(Object response)
        {
            if (response is Error error)
            {
                if (IsLimitError(error))
                {
                    _limitReached = true;
                    _navigationService.ShowToast(string.Format("**{0}**\n{1}", Strings.AIEditorLimitTitle, Strings.AIEditorLimitText), ToastPopupIcon.Premium);
                }
                else
                {
                    _limitReached = false;
                    _failed = true;
                    _navigationService.ShowToast(error);
                }
            }
            else
            {
                _limitReached = false;
            }

            UpdatePrimaryButton();
        }

        public static bool IsLimitError(Error error)
        {
            return string.Equals(error.Message, "AICOMPOSE_FLOOD_PREMIUM", StringComparison.OrdinalIgnoreCase)
                || string.Equals(error.Message, "SUMMARY_FLOOD_PREMIUM", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdatePrimaryButton()
        {
            PrimaryButtonText = _limitReached
                ? Strings.AIEditorLimitButton
                : _failed
                ? Strings.OK
                : Strings.AIEditorApply;
        }

        private void ShowResult(FormattedTextBlock output, FormattedText text)
        {
            if (text == null)
            {
                output.ShowHideSkeleton(true);

                // The skeleton is sized in ArrangeOverride, and the text it covers has not changed.
                output.InvalidateArrange();
                return;
            }

            if (output == TabStyleOutput)
            {
                TabStyleState.Text = Strings.AIEditorResult;
            }

            output.ShowHideSkeleton(false);
            output.SetText(_clientService, text);
        }

        #region Fix

        private async void InitializeFix()
        {
            _fixRequested = true;

            TabFixOriginal.SetText(_clientService, _text);

            TabFixLoading.Visibility = Visibility.Visible;
            TabFixLoading.ShowHideSkeleton(true);
            TabFixLoading.SetText(_clientService, _text);

            OnRequestStarted();

            var response = await _clientService.SendAsync(new FixTextWithAi(_text));
            OnRequestCompleted(response);

            if (response is FixedText text)
            {
                _fix = text;

                TabFixLoading.Visibility = Visibility.Collapsed;

                // What was taken out is struck through, what was put in is underlined.
                var entities = new TextEntity[text.DiffText.Entities.Count];

                for (int i = 0; i < entities.Length; i++)
                {
                    var diff = text.DiffText.Entities[i];
                    entities[i] = new TextEntity(diff.Offset, diff.Length, diff.Type is DiffEntityTypeDelete
                        ? new TextEntityTypeStrikethrough()
                        : new TextEntityTypeUnderline());
                }

                TabFixResult.SetText(_clientService, new FormattedText(text.DiffText.Text, entities));
            }
            else
            {
                // Coming back to the tab asks again.
                _fixRequested = false;
            }
        }

        #endregion

        private void OnPrimaryButtonClick(ModalPopup sender, ModalPopupButtonClickEventArgs args)
        {
            if (_limitReached)
            {
                args.Cancel = true;
                _navigationService.ShowPromo(new PremiumFeatureTextComposition());
                return;
            }
            else if (_failed)
            {
                return;
            }

            var result = Navigation.SelectedIndex switch
            {
                0 => GetResult(_translations, _translateKey),
                1 => GetResult(_styles, _styleKey),
                2 => _fix?.Text,
                _ => null
            };

            if (result != null)
            {
                Result = result;
                SaveParameters();
            }
            else
            {
                args.Cancel = true;
            }
        }

        private static FormattedText GetResult(Dictionary<string, FormattedText> results, string key)
        {
            if (key != null && results.TryGetValue(key, out FormattedText text))
            {
                return text;
            }

            return null;
        }
    }
}
