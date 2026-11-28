//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.ViewModels.Stories;

namespace Telegram.Td.Api
{
    // Raised by IStoryService once a story's view model has taken an updateStory. A subscriber to
    // updateStory itself may run before the view model has it.
    public partial class UpdateStoryChanged
    {
        public StoryViewModel Story { get; }

        public UpdateStoryChanged(StoryViewModel story)
        {
            Story = story;
        }
    }
}
