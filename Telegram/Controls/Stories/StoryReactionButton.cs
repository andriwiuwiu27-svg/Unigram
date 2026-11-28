//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls.Messages;
using Telegram.Converters;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels.Stories;
using Windows.UI.Xaml;

namespace Telegram.Controls.Stories
{
    /// <summary>
    /// Either the like button of a story, standing for whichever reaction is chosen on it, or a
    /// suggested reaction area, standing for its own. A click changes nothing here: TDLib applies a
    /// story reaction locally, and the viewer redraws the buttons from the update that follows.
    /// </summary>
    public partial class StoryReactionButton : ReactionButtonBase
    {
        private StoryViewModel _story;
        private ReactionType _type;
        private bool _suggested;
        private bool _chosen;
        private int _count;

        public StoryReactionButton()
        {
            DefaultStyleKey = typeof(StoryReactionButton);

            Click += OnClick;
        }

        protected override IClientService ClientService => _story?.ClientService;

        protected override int EmojiSize => _suggested ? 70 : 32;

        protected override int CustomEmojiSize => _suggested ? 52 : 20;

        // Nothing scrolls these out of view in the stories viewer.
        protected override bool IsCustomEmojiViewportAware => false;

        public override string GetAutomationName()
        {
            return _type switch
            {
                ReactionTypeEmoji emoji => Locale.Declension(Strings.R.AccDescrNumberOfPeopleReactions, _count, emoji.Emoji),
                ReactionTypeCustomEmoji => Locale.Declension(Strings.R.AccDescrNumberOfPeopleReactions, _count, Strings.AccDescrCustomEmoji2),
                _ => null
            };
        }

        public void SetReaction(StoryViewModel story)
        {
            Update(story, story.ChosenReactionType ?? new ReactionTypeEmoji("❤"), false);
        }

        public void SetSuggestedReaction(StoryViewModel story, ReactionType reactionType)
        {
            Update(story, reactionType, true);
        }

        private void Update(StoryViewModel story, ReactionType reactionType, bool suggested)
        {
            if (!IsTemplateApplied)
            {
                _story = story;
                _type = reactionType;
                _suggested = suggested;
                return;
            }

            var sameStory = story.Id == _story?.Id
                && story.PosterChatId == _story?.PosterChatId;

            if (!sameStory)
            {
                StopAnimation();
            }

            // The like button stays the same button when the chosen reaction changes type.
            var recycled = sameStory && (!suggested || reactionType.AreTheSame(_type));
            var chosen = story.ChosenReactionType != null && story.ChosenReactionType.AreTheSame(reactionType);

            _story = story;
            _type = reactionType;
            _suggested = suggested;

            IsChecked = chosen;
            UpdateCount(story, reactionType, suggested, recycled && chosen != _chosen);

            _chosen = chosen;

            UpdateIcon(story.ClientService, reactionType);
        }

        private void UpdateCount(StoryViewModel story, ReactionType reactionType, bool suggested, bool animate)
        {
            var count = 0;

            if (suggested)
            {
                if (story.Areas != null)
                {
                    foreach (var area in story.Areas)
                    {
                        if (area.Type is StoryAreaTypeSuggestedReaction suggestedReaction && suggestedReaction.ReactionType.AreTheSame(reactionType))
                        {
                            count = suggestedReaction.TotalCount;
                            break;
                        }
                    }
                }
            }
            else
            {
                count = story.InteractionInfo?.ReactionCount ?? 0;
            }

            _count = count;

            if (count > 0)
            {
                Count.Visibility = Visibility.Visible;
                Count.SetText(Formatter.ShortNumber(count), animate);
            }
            else
            {
                Count.Visibility = Visibility.Collapsed;
            }
        }

        protected override void OnTemplateApplied()
        {
            if (_type != null)
            {
                Update(_story, _type, _suggested);
            }
        }

        private void OnClick(object sender, RoutedEventArgs e)
        {
            var story = _story;
            if (story == null || !IsTemplateApplied)
            {
                return;
            }

            if (_suggested)
            {
                // An area sets its reaction and never takes it back. Its effect plays on every click,
                // on one's own story too, which can't be reacted to.
                if (!_chosen && story.PosterChatId != story.ClientService.Options.MyId)
                {
                    story.ClientService.Send(new SetStoryReaction(story.PosterChatId, story.Id, _type, false));
                }

                Animate();
            }
            else if (story.ChosenReactionType != null)
            {
                story.ClientService.Send(new SetStoryReaction(story.PosterChatId, story.Id, null, false));
            }
            else
            {
                Animate();
                story.ClientService.Send(new SetStoryReaction(story.PosterChatId, story.Id, _type, false));
            }
        }
    }
}
