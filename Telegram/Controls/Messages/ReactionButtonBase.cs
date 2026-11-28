//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Native.Controls;
using Telegram.Services;
using Telegram.Streams;
using Telegram.Td.Api;
using Windows.Foundation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Controls.Messages
{
    /// <summary>
    /// The icon, the count and the effect of a reaction button, for whatever the reaction is on.
    /// The icon is a static frame of the reaction, swapped for its animation while the effect
    /// plays and back once it pauses.
    /// </summary>
    public abstract partial class ReactionButtonBase : ToggleButtonEx
    {
        private CustomEmojiIcon Icon;
        private Popup Overlay;
        protected AnimatedTextBlock Count;

        protected ReactionType _reactionType;

        // Bumped by StopAnimation, so that an Animate still awaiting TDLib can tell it no longer
        // belongs to what it started for: the button may be showing something else by then.
        private int _generation;

        private AnimatedImage _around;

        protected abstract IClientService ClientService { get; }

        protected virtual int EmojiSize => 32;

        protected virtual int CustomEmojiSize => 20;

        protected virtual bool IsCustomEmojiViewportAware => true;

        protected bool IsTemplateApplied => Icon != null;

        /// <summary>
        /// The reaction the icon shows, kept after the button lets go of its message.
        /// </summary>
        internal ReactionType IconType => _reactionType;

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new ReactionButtonAutomationPeer(this);
        }

        public virtual string GetAutomationName()
        {
            return null;
        }

        internal virtual AutomationControlType AutomationControlType => AutomationControlType.Button;

        protected abstract void OnTemplateApplied();

        protected virtual void OnIconReady()
        {
        }

        protected override void OnApplyTemplate()
        {
            if (Icon != null)
            {
                Icon.Ready -= OnReady;
                Icon.Paused -= OnPaused;
            }

            Count = GetTemplateChild(nameof(Count)) as AnimatedTextBlock;
            Overlay = GetTemplateChild(nameof(Overlay)) as Popup;
            Icon = GetTemplateChild(nameof(Icon)) as CustomEmojiIcon;
            Icon.Ready += OnReady;
            Icon.Paused += OnPaused;

            OnTemplateApplied();

            base.OnApplyTemplate();
        }

        protected override void OnToggle()
        {
            //base.OnToggle();
        }

        protected void UpdateIcon(IClientService clientService, ReactionType type)
        {
            if (type.AreTheSame(_reactionType))
            {
                return;
            }

            _reactionType = type;

            using (Icon.BeginBatchUpdate())
            {
                var custom = type is ReactionTypeCustomEmoji;
                var size = type is ReactionTypeCustomEmoji or ReactionTypePaid ? CustomEmojiSize : EmojiSize;

                Icon.Width = Icon.Height = size;
                Icon.FrameSize = new Size(size, size);
                Icon.LoopCount = custom ? 3 : 1;
                Icon.IsCachingEnabled = type is not ReactionTypePaid;
                Icon.IsViewportAware = custom && IsCustomEmojiViewportAware;

                Icon.Source = new ReactionFileSource(clientService, type)
                {
                    UseCenterAnimation = true,
                    IsAnimated = false
                };
            }
        }

        private void OnReady(object sender, EventArgs e)
        {
            OnIconReady();
        }

        private void OnPaused(object sender, EventArgs e)
        {
            this.BeginOnUIThread(OnPaused);
        }

        private void OnPaused()
        {
            if (Icon?.Source is ReactionFileSource reaction && Icon.Source.IsAnimated && IsConnected)
            {
                Icon.Source = reaction.Clone(false);
            }
        }

        protected void StopAnimation()
        {
            _generation++;
            _around = null;

            if (Overlay != null)
            {
                Overlay.IsOpen = false;
                Overlay.Child = null;
            }

            // OnPaused does not swap back once the button is out of the tree, and UpdateIcon keeps
            // the source of a button reused for the same reaction, so the next Animate would find
            // it animated already and skip the fresh clone it plays from.
            if (Icon?.Source is ReactionFileSource reaction && reaction.IsAnimated)
            {
                Icon.Source = reaction.Clone(false);
            }
        }

        protected async void Animate()
        {
            var clientService = ClientService;
            if (clientService == null)
            {
                return;
            }

            var generation = _generation;

            if (_reactionType is ReactionTypeEmoji emoji)
            {
                var response = await clientService.SendAsync(new GetEmojiReaction(emoji.Emoji));
                if (response is EmojiReaction reaction && reaction.AroundAnimation != null)
                {
                    var around = await clientService.DownloadFileAsync(reaction.AroundAnimation.StickerValue, 32);
                    if (around.Local.IsDownloadingCompleted && IsConnected && generation == _generation)
                    {
                        this.BeginOnUIThread(() => Animate(around, true));
                    }
                }
            }
            else if (_reactionType is ReactionTypeCustomEmoji customEmoji)
            {
                var response = await clientService.SendAsync(new GetCustomEmojiReactionAnimations());
                if (response is Stickers stickers)
                {
                    var random = new Random();
                    var next = random.Next(0, stickers.StickersValue.Count);

                    var around = await clientService.DownloadFileAsync(stickers.StickersValue[next].StickerValue, 32);
                    if (around.Local.IsDownloadingCompleted && IsConnected && generation == _generation)
                    {
                        this.BeginOnUIThread(() => Animate(around, true));
                    }
                }
            }
            else if (_reactionType is ReactionTypePaid)
            {
                var random = new Random();
                var next = random.Next(1, 6);

                var around = TdExtensions.GetLocalFile($"Assets\\Animations\\PaidReactionAround{next}.tgs");
                if (around.Local.IsDownloadingCompleted && IsConnected)
                {
                    this.BeginOnUIThread(() => Animate(around, false));
                }
            }
        }

        private void Animate(File around, bool cache)
        {
            if (Icon?.Source is ReactionFileSource reaction && !Icon.Source.IsAnimated)
            {
                Icon.Source = reaction.Clone(true);
            }

            Icon?.Play();

            var popup = Overlay;
            var size = EmojiSize * 3;

            var aroundView = new AnimatedImage();
            aroundView.Width = size;
            aroundView.Height = size;
            aroundView.LoopCount = 1;
            aroundView.FrameSize = new Size(size, size);
            aroundView.DecodeFrameType = DecodePixelType.Logical;
            aroundView.IsCachingEnabled = cache;
            aroundView.AutoPlay = true;
            aroundView.Source = new LocalFileSource(around);
            aroundView.LoopCompleted += OnAroundLoopCompleted;

            _around = aroundView;

            var root = new Grid();
            root.Name = "ReactionAnimation";
            root.Width = size;
            root.Height = size;
            root.Children.Add(aroundView);

            popup.Child = root;
            popup.XamlRoot = XamlRoot;
            popup.IsOpen = true;
        }

        private void OnAroundLoopCompleted(object sender, AnimatedImageLoopCompletedEventArgs e)
        {
            // Raised off the UI thread through a ?.Invoke that the shipping compiler reads twice, so
            // the handler can only be removed safely from here, on the thread that raises it.
            var view = sender as AnimatedImage;
            view.LoopCompleted -= OnAroundLoopCompleted;

            this.BeginOnUIThread(() => Continue(view));
        }

        private void Continue(AnimatedImage view)
        {
            Logger.Info();

            var popup = Overlay;
            if (popup == null || view != _around)
            {
                return;
            }

            _around = null;

            popup.IsOpen = false;
            popup.Child = null;
        }
    }

    public partial class ReactionButtonAutomationPeer : ToggleButtonAutomationPeer
    {
        private readonly ReactionButtonBase _owner;

        public ReactionButtonAutomationPeer(ReactionButtonBase owner)
            : base(owner)
        {
            _owner = owner;
        }

        protected override string GetNameCore()
        {
            return _owner.GetAutomationName() ?? base.GetNameCore();
        }

        protected override AutomationControlType GetAutomationControlTypeCore()
        {
            return _owner.AutomationControlType;
        }
    }
}
