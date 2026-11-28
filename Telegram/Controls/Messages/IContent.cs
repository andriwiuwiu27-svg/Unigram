//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Td.Api;
using Telegram.ViewModels;
using Windows.UI.Composition;

namespace Telegram.Controls.Messages
{
    public interface IContent
    {
        MessageViewModel Message { get; }

        void UpdateMessage(MessageViewModel message);

        void Recycle();

        bool IsValid(MessageContent content, bool primary);
    }

    public interface IContentWithFile : IContent
    {
        void UpdateMessageContentOpened(MessageViewModel message);
    }

    public interface IContentWithMask
    {
        CompositionBrush GetAlphaMask();
    }

    public interface IContentWithPlayback
    {
        IPlayerView GetPlaybackElement();
    }

    /// <summary>
    /// Content whose last line the footer can share when it fits beside it, and goes under otherwise.
    /// </summary>
    public interface IContentWithWrap
    {
        /// <summary>
        /// Where the last line ends, inside the content's padding, or -1 when the footer cannot share
        /// it. Asked during the bubble's measure, so it can only answer from desired sizes.
        /// </summary>
        float ContentEnd();
    }
}
