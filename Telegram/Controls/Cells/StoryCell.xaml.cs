//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Td.Api;
using Telegram.ViewModels.Stories;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Telegram.Controls.Cells
{
    public sealed partial class StoryCell : Control
    {
        private StoryViewModel _viewModel;
        public StoryViewModel ViewModel => _viewModel;

        private bool _pinned;
        private string _glyph;

        private ThumbnailController _thumbnailController;
        private GlyphShadow _glyphShadow;

        public StoryCell()
        {
            DefaultStyleKey = typeof(StoryCell);
        }

        #region InitializeComponent

        private AspectView LayoutRoot;
        private ImageBrush ThumbnailTexture;
        private ImageBrush Texture;
        private Border GlyphShadowHost;
        private TextBlock Glyph;
        private Border Overlay;
        private TextBlock Subtitle;
        private bool _templateApplied;

        protected override void OnApplyTemplate()
        {
            LayoutRoot = GetTemplateChild(nameof(LayoutRoot)) as AspectView;
            ThumbnailTexture = GetTemplateChild(nameof(ThumbnailTexture)) as ImageBrush;
            Texture = GetTemplateChild(nameof(Texture)) as ImageBrush;
            GlyphShadowHost = GetTemplateChild(nameof(GlyphShadowHost)) as Border;
            Glyph = GetTemplateChild(nameof(Glyph)) as TextBlock;
            Overlay = GetTemplateChild(nameof(Overlay)) as Border;
            Subtitle = GetTemplateChild(nameof(Subtitle)) as TextBlock;

            LayoutRoot.Constraint = new Size(256, 320);

            _glyph = null;
            _glyphShadow = null;
            _thumbnailController = null;

            _templateApplied = true;

            if (_viewModel != null)
            {
                Update(_viewModel, _pinned);
            }
        }

        #endregion

        public void Update(StoryViewModel story, bool pinned = false)
        {
            _viewModel = story;
            _pinned = pinned;

            if (!_templateApplied)
            {
                return;
            }

            var glyph = pinned
                ? Icons.PinFilled16
                : story.PrivacySettings switch
                {
                    StoryPrivacySettingsCloseFriends => Icons.StarFilled16,
                    StoryPrivacySettingsSelectedUsers => Icons.PeopleFilled16,
                    StoryPrivacySettingsContacts => Icons.PersonCircleFilled16,
                    _ => null
                };

            if (glyph != _glyph)
            {
                _glyph = glyph;

                if (glyph != null)
                {
                    Glyph.Text = glyph;

                    _glyphShadow ??= new GlyphShadow(GlyphShadowHost);
                    _glyphShadow.Show(glyph, Glyph);
                }
                else
                {
                    Glyph.Text = string.Empty;
                    _glyphShadow?.Hide();
                }
            }

            if (story.Content is StoryContentPhoto photo)
            {
                Overlay.Visibility = Visibility.Collapsed;

                var file = photo.Photo.GetBig();
                if (file != null)
                {
                    UpdateFile(story, file.Photo, true);
                }

                var thumbnail = photo.Photo.GetSmall();
                UpdateThumbnail(story, thumbnail?.Photo, photo.Photo.Minithumbnail, true);
            }
            else if (story.Content is StoryContentVideo video)
            {
                Overlay.Visibility = Visibility.Visible;
                Subtitle.Text = video.Video.GetDuration();

                Texture.ImageSource = null;
                UpdateManager.Unsubscribe(this, ref _fileToken);

                UpdateThumbnail(story, video.Video.Thumbnail?.File, video.Video.Minithumbnail, true);
            }
        }

        private long _fileToken;
        private long _thumbnailToken;

        private void UpdateThumbnail(File file)
        {
            UpdateThumbnail(_viewModel, file, null, false);
        }

        private void UpdateFile(File file)
        {
            UpdateFile(_viewModel, file, false);
        }

        private void UpdateThumbnail(StoryViewModel story, File file, Minithumbnail minithumbnail, bool download)
        {
            _thumbnailController ??= new ThumbnailController(ThumbnailTexture);

            if (file != null)
            {
                if (file.Local.IsDownloadingCompleted)
                {
                    _thumbnailController.Bitmap(file.Local.Path, hashCode: HashCode.Combine(story.PosterChatId, story.Id));
                }
                else
                {
                    if (download)
                    {
                        if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
                        {
                            story.ClientService.DownloadFile(file.Id, 1);
                        }

                        UpdateManager.Subscribe(this, story.ClientService, file, ref _thumbnailToken, UpdateThumbnail, true);
                    }

                    if (minithumbnail != null)
                    {
                        _thumbnailController.Blur(minithumbnail.Data, 3, HashCode.Combine(story.PosterChatId, story.Id));
                    }
                    else
                    {
                        _thumbnailController.Recycle();
                    }
                }
            }
            else if (minithumbnail != null)
            {
                _thumbnailController.Blur(minithumbnail.Data, 3, HashCode.Combine(story.PosterChatId, story.Id));
            }
            else
            {
                _thumbnailController.Recycle();
            }
        }

        private void UpdateFile(StoryViewModel item, File file, bool download)
        {
            if (file.Local.IsDownloadingCompleted)
            {
                UpdateManager.Unsubscribe(this, ref _fileToken);
                Texture.ImageSource = UriEx.ToBitmap(file.Local.Path, 0, 0);
            }
            else if (download)
            {
                Texture.ImageSource = null;

                if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
                {
                    item.ClientService.DownloadFile(file.Id, 1);
                }

                UpdateManager.Subscribe(this, _viewModel.ClientService, file, ref _fileToken, UpdateFile, true);
            }
        }
    }
}
