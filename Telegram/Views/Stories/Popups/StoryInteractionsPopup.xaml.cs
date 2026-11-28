//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Cells;
using Telegram.Controls.Media;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;

namespace Telegram.Views.Stories.Popups
{
    public sealed partial class StoryInteractionsPopup : ModalPopup
    {
        public StoryInteractionsViewModel ViewModel => DataContext as StoryInteractionsViewModel;

        public StoryInteractionsPopup()
        {
            InitializeComponent();

            SecondaryButtonText = Strings.Close;
        }

        private void OnOpened(ModalPopup sender, ModalPopupOpenedEventArgs args)
        {
            ShowHideSkeleton();

            ScrollingHost.ItemsPanelRoot?.MinHeight = ScrollingHost.ActualHeight - 24;
        }

        public override void OnNavigatedTo(object parameter)
        {
            var story = ViewModel.Story;
            var premium = story.ClientService.IsPremium;

            // Without Premium, an expired story keeps its reactions and nothing else: with none,
            // there is no list to show.
            var limited = story.HasExpiredViewers && !premium;

            if (story.InteractionInfo != null && story.CanGetInteractions && !(limited && story.InteractionInfo.ReactionCount == 0))
            {
                ViewModel.Items.CollectionChanged += OnCollectionChanged;
                ViewModel.PropertyChanged += OnPropertyChanged;

                var count = limited
                    ? story.InteractionInfo.ReactionCount
                    : story.InteractionInfo.ViewCount;

                if (count >= 9)
                {
                    VerticalContentAlignment = VerticalAlignment.Stretch;
                    ScrollingHost.Height = double.NaN;
                }
                else
                {
                    VerticalContentAlignment = VerticalAlignment.Center;
                    ScrollingHost.Height = count * 48 + 24;
                }

                PrimaryButtonText = string.Empty;

                var channel = story.Chat?.Type is ChatTypeSupergroup;

                SearchField.Visibility = !limited && !channel && story.InteractionInfo.ViewCount >= 15
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                SortBy.Visibility = !limited && story.InteractionInfo.ReactionCount >= 10
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                // Filtering to contacts says nothing when only contacts, close friends or chosen
                // users can see the story.
                if (limited || channel || story.PrivacySettings is not StoryPrivacySettingsEveryone || story.InteractionInfo.ViewCount < 20)
                {
                    Navigation.Visibility = Visibility.Collapsed;
                    Title = limited
                        ? Locale.Declension(Strings.R.Likes, story.InteractionInfo.ReactionCount)
                        : Locale.Declension(Strings.R.Views, story.InteractionInfo.ViewCount);

                    Padding = new Thickness(0, 24, 0, 0);
                    SortBy.Margin = new Thickness(0, -36, 24, 12);

                    ExpiredFooter.Visibility = limited && count < story.InteractionInfo.ViewCount
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
                else
                {
                    Navigation.Visibility = Visibility.Visible;
                    Title = string.Empty;

                    Padding = new Thickness(0);
                    SortBy.Margin = new Thickness(0, 0, 24, 0);

                    ExpiredFooter.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                FindName(nameof(ExpiredRoot));

                VerticalContentAlignment = VerticalAlignment.Center;

                if (ViewModel.IsPremiumAvailable)
                {
                    PrimaryButtonText = Strings.LearnMore;
                    PremiumHint.Visibility = Visibility.Visible;
                }
                else
                {
                    PrimaryButtonText = string.Empty;
                    PremiumHint.Visibility = Visibility.Collapsed;
                }

                Navigation.Visibility = Visibility.Collapsed;
                Title = story.InteractionInfo != null
                    ? Locale.Declension(Strings.R.Views, story.InteractionInfo.ViewCount)
                    : Strings.StatisticViews;

                Padding = new Thickness(0);

                ExpiredFooter.Visibility = Visibility.Collapsed;
            }
        }

        public override void OnNavigatedFrom()
        {
            ViewModel.Items.CollectionChanged -= OnCollectionChanged;
            ViewModel.PropertyChanged -= OnPropertyChanged;

            base.OnNavigatedFrom();
        }

        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.EmptyState))
            {
                ShowHideSkeleton();
            }
        }

        private void OnCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            ShowHideSkeleton();
        }

        // 446.667,
        //  48.6667

        private void OnChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args.ItemContainer == null)
            {
                args.ItemContainer = new TextListViewItem();
                args.ItemContainer.Style = ScrollingHost.ItemContainerStyle;
                args.ItemContainer.ContentTemplate = ScrollingHost.ItemTemplate;
                args.ItemContainer.ContextRequested += OnContextRequested;
            }

            args.IsContainerPrepared = true;
        }

        private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var interaction = ScrollingHost.ItemFromContainer(sender) as StoryInteraction;
            if (interaction == null || !ViewModel.ClientService.TryGetUser(interaction.ActorId, out User user))
            {
                return;
            }

            var flyout = new MenuFlyout();

            if (interaction.BlockList is null)
            {
                flyout.CreateFlyoutItem(HideStories, interaction, string.Format(Strings.StoryHideFrom, user.FirstName), Icons.StoriesOff);
            }
            else if (interaction.BlockList is BlockListStories)
            {
                flyout.CreateFlyoutItem(ShowStories, interaction, string.Format(Strings.StoryShowTo, user.FirstName), Icons.Stories);
            }

            if (user.IsContact)
            {
                flyout.CreateFlyoutItem(DeleteContact, interaction, Strings.DeleteContact, Icons.Delete, destructive: true);
            }
            else if (interaction.BlockList is not BlockListMain)
            {
                flyout.CreateFlyoutItem(BlockUser, interaction, Strings.BlockUser, Icons.HandRight, destructive: true);
            }
            else if (interaction.BlockList is BlockListMain)
            {
                flyout.CreateFlyoutItem(UnblockUser, interaction, Strings.Unblock, Icons.HandRight);
            }

            flyout.ShowAt(sender, args);
        }

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }
            else if (args.ItemContainer.ContentTemplateRoot is Grid content)
            {
                var cell = content.Children[0] as ProfileCell;
                var animated = content.Children[1] as CustomEmojiIcon;

                if (args.Item is StoryInteraction interaction)
                {
                    cell.UpdateStoryViewer(ViewModel.ClientService, args, OnContainerContentChanging);
                    content.Opacity = IsStoryShownTo(interaction) ? 1 : 0.5;

                    var reactionType = interaction.Type is StoryInteractionTypeView view
                        ? view.ChosenReactionType
                        : null;

                    // The center animation paints an emoji in just a part of its frame, and not
                    // quite in its middle, leaving room for an effect: shown at twice the size,
                    // as ReactionButton shows reactions and InteractionsView sizes those of a
                    // message, an emoji reaction is as large as a custom emoji.
                    var emoji = reactionType is ReactionTypeEmoji;

                    using (animated.BeginBatchUpdate())
                    {
                        var size = emoji ? 40 : 20;

                        animated.Width = animated.Height = size;
                        animated.Margin = new Thickness(0, 0, emoji ? 2 : 12, 0);
                        animated.FrameSize = new Size(size, size);

                        animated.Source = reactionType != null
                            ? new ReactionFileSource(ViewModel.ClientService, reactionType)
                            {
                                UseCenterAnimation = true,
                                IsAnimated = !emoji
                            }
                            : null;
                    }
                }
            }
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            Hide();
            ViewModel.OpenChat(e.ClickedItem);
        }

        // Whether the viewer can see the story now: one it has since been hidden from, who was
        // blocked, or who no longer falls within its privacy is dimmed.
        private bool IsStoryShownTo(StoryInteraction interaction)
        {
            if (interaction.BlockList != null)
            {
                return false;
            }

            if (!ViewModel.ClientService.TryGetUser(interaction.ActorId, out User user))
            {
                return true;
            }

            return ViewModel.Story.PrivacySettings switch
            {
                StoryPrivacySettingsEveryone everyone => !everyone.ExceptUserIds.Contains(user.Id),
                StoryPrivacySettingsContacts contacts => user.IsContact && !contacts.ExceptUserIds.Contains(user.Id),
                StoryPrivacySettingsCloseFriends => user.IsCloseFriend,
                StoryPrivacySettingsSelectedUsers selected => selected.UserIds.Contains(user.Id),
                _ => true
            };
        }

        private void UpdateShownTo(StoryInteraction interaction)
        {
            if (ScrollingHost.ContainerFromItem(interaction) is SelectorItem { ContentTemplateRoot: Grid content })
            {
                content.Opacity = IsStoryShownTo(interaction) ? 1 : 0.5;
            }
        }

        private void HideStories(StoryInteraction interaction)
        {
            ViewModel.HideStories(interaction);
            UpdateShownTo(interaction);
        }

        private void ShowStories(StoryInteraction interaction)
        {
            ViewModel.ShowStories(interaction);
            UpdateShownTo(interaction);
        }

        private void UnblockUser(StoryInteraction interaction)
        {
            ViewModel.UnblockUser(interaction);
            UpdateShownTo(interaction);
        }

        private void DeleteContact(StoryInteraction interaction)
        {
            ViewModel.DeleteContact(interaction, ScrollingHost.ContainerFromItem(interaction));
        }

        private async void BlockUser(StoryInteraction interaction)
        {
            await ViewModel.BlockUser(interaction, ScrollingHost.ContainerFromItem(interaction));
            UpdateShownTo(interaction);
        }

        private void SortBy_ContextRequested(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            flyout.CreateFlyoutItem(ViewModel.SortByReaction, Strings.SortByReactions, ViewModel.SortBy == StoryInteractionsSortBy.Reaction ? Icons.HeartFilled : Icons.Heart);
            flyout.CreateFlyoutItem(ViewModel.SortByTime, Strings.SortByTime, ViewModel.SortBy == StoryInteractionsSortBy.Time ? Icons.ClockFilled : Icons.Clock);

            flyout.CreateFlyoutSeparator();
            flyout.Items.Add(new MenuFlyoutLabel
            {
                Padding = new Thickness(12, 4, 12, 4),
                MaxWidth = 178,
                Text = Strings.StoryViewsSortDescription
            });

            flyout.ShowAt(sender as FrameworkElement, FlyoutPlacementMode.BottomEdgeAlignedRight);
        }

        private bool _skeletonCollapsed = true;

        private void ShowHideSkeleton()
        {
            var loading = ViewModel.Items.Count == 0 && ViewModel.EmptyState == StoryInteractionsEmptyState.None;

            if (_skeletonCollapsed && loading && ViewModel.Story != null && ScrollingHost.ItemsPanelRoot != null)
            {
                _skeletonCollapsed = false;
                ShowSkeleton();
            }
            else if (_skeletonCollapsed is false && !loading && ScrollingHost.ItemsPanelRoot != null)
            {
                _skeletonCollapsed = true;
                VisualUtilities.HideSkeleton(ScrollingHost.ItemsPanelRoot);
            }
        }

        private void ShowSkeleton()
        {
            if (ViewModel?.Story?.InteractionInfo == null)
            {
                return;
            }

            var size = ScrollingHost.ActualSize;
            var itemHeight = 6 + 36 + 6;

            var rows = Math.Min(ViewModel.Story.InteractionInfo.ViewCount, Math.Ceiling(size.Y / itemHeight));
            var shapes = new List<CanvasGeometry>();

            var maxWidth = (int)Math.Clamp(size.X - 32 - 12 - 12 - 48 - 12, 80, 280);
            var random = new Random();

            for (int i = 0; i < rows; i++)
            {
                var y = itemHeight * i;

                shapes.Add(CanvasGeometry.CreateEllipse(null, 12 + 18, y + 6 + 18, 18, 18));
                shapes.Add(CanvasGeometry.CreateRoundedRectangle(null, 12 + 36 + 8, y + 6, random.Next(80, maxWidth), 18, 4, 4));
                shapes.Add(CanvasGeometry.CreateRoundedRectangle(null, 12 + 36 + 8, y + 6 + 18 + 4, random.Next(80, maxWidth), 14, 4, 4));
            }

            VisualUtilities.SetSkeleton(ScrollingHost.ItemsPanelRoot, size, shapes.ToArray());
        }

        private Visibility ConvertEmptyVisibility(StoryInteractionsEmptyState state)
        {
            return state == StoryInteractionsEmptyState.None
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private Visibility ConvertEmptyTitleVisibility(StoryInteractionsEmptyState state)
        {
            return state is StoryInteractionsEmptyState.NoViews or StoryInteractionsEmptyState.NoReactions or StoryInteractionsEmptyState.ServerError
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private string ConvertEmptyTitle(StoryInteractionsEmptyState state)
        {
            return state switch
            {
                StoryInteractionsEmptyState.NoViews => Strings.NoViews,
                StoryInteractionsEmptyState.NoReactions => Strings.NoReactions,
                StoryInteractionsEmptyState.ServerError => Strings.ServerErrorViewersTitle,
                _ => string.Empty
            };
        }

        private string ConvertEmptySubtitle(StoryInteractionsEmptyState state)
        {
            return state switch
            {
                StoryInteractionsEmptyState.NoViews => Strings.NoViewsStub,
                StoryInteractionsEmptyState.NoReactions => Strings.NoReactionsStub,
                StoryInteractionsEmptyState.NoResult => Strings.NoResult,
                StoryInteractionsEmptyState.NoContacts => Strings.NoContactsViewed,
                StoryInteractionsEmptyState.ServerError => Strings.ServerErrorViewers,
                _ => string.Empty
            };
        }

        private string ConvertSortBy(StoryInteractionsSortBy sortBy)
        {
            return sortBy switch
            {
                StoryInteractionsSortBy.Reaction => Icons.Heart,
                StoryInteractionsSortBy.Time or _ => Icons.Clock
            };
        }
    }
}
