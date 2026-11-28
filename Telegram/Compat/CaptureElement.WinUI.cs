//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Telegram.Compat
{
    /// <summary>
    /// WinUI 3 has no CaptureElement: a camera preview is a MediaPlayerElement playing one of the
    /// capture's frame sources, so MediaCapture.StartPreviewAsync has nothing to render into and
    /// is not called on this flavour.
    ///
    /// A Grid rather than a bare Control so that it lays out like the real one where it is added to
    /// a Grid's children. RenderTargetBitmap cannot read video content, so the last frame comes from
    /// GetLastFrame instead.
    ///
    /// GlobalUsings.WinUI.cs aliases the name onto this type.
    /// </summary>
    public partial class CaptureElement : Grid
    {
        private readonly MediaPlayerElement _presenter = new();
        private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        private MediaCapture _source;
        private MediaPlayer _player;
        private MediaSource _mediaSource;
        private MediaFrameReader _reader;

        public CaptureElement()
        {
            Children.Add(_presenter);
        }

        public MediaCapture Source
        {
            get => _source;
            set
            {
                if (_source == value)
                {
                    return;
                }

                Close();
                _source = value;

                if (value != null)
                {
                    Open(value);
                }
            }
        }

        public Stretch Stretch
        {
            get => _presenter.Stretch;
            set => _presenter.Stretch = value;
        }

        private void Open(MediaCapture capture)
        {
            try
            {
                var frameSource = FindVideoSource(capture);
                if (frameSource == null)
                {
                    Logger.Info("No video frame source was found for the preview");
                    return;
                }

                _mediaSource = MediaSource.CreateFromMediaFrameSource(frameSource);
                _player = new MediaPlayer
                {
                    RealTimePlayback = true,
                    IsMuted = true,
                    Source = _mediaSource
                };

                // Otherwise the preview registers with the system media controls like any player.
                _player.CommandManager.IsEnabled = false;

                // Its swap chain is opaque black until the first frame, which would cover the last
                // frame the caller shows behind it.
                _presenter.Opacity = 0;
                _presenter.SetMediaPlayer(_player);
                _player.Play();

                OpenReader(capture, frameSource);
            }
            catch (Exception ex)
            {
                // The capture can be disposed under us, as the recording starts and stops asynchronously.
                Logger.Error("The camera preview couldn't start", ex);
                Close();
            }
        }

        // For GetLastFrame, and for the first frame that reveals the presenter. Past that it has no
        // FrameArrived handler, so a realtime reader just keeps replacing the latest frame.
        private async void OpenReader(MediaCapture capture, MediaFrameSource frameSource)
        {
            try
            {
                var reader = await capture.CreateFrameReaderAsync(frameSource);
                if (_source != capture)
                {
                    reader.Dispose();
                    return;
                }

                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                reader.FrameArrived += OnFirstFrameArrived;

                _reader = reader;

                await reader.StartAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("The camera frame reader couldn't start", ex);

                if (_source == capture)
                {
                    _presenter.Opacity = 1;
                }
            }
        }

        // On a capture thread, where Close may be disposing the reader at the same time.
        private void OnFirstFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            try
            {
                sender.FrameArrived -= OnFirstFrameArrived;
            }
            catch
            {
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_reader == sender)
                {
                    _presenter.Opacity = 1;
                }
            });
        }

        /// <summary>
        /// The latest camera frame as BGRA8, unmirrored, or null if none has arrived. Read it before
        /// the recording stops: the engine disposes the capture right after.
        /// </summary>
        public SoftwareBitmap GetLastFrame()
        {
            try
            {
                using var frame = _reader?.TryAcquireLatestFrame();

                var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
                if (bitmap != null)
                {
                    return SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("The last camera frame couldn't be read", ex);
            }

            return null;
        }

        private void Close()
        {
            _presenter.SetMediaPlayer(null);

            if (_reader != null)
            {
                _reader.FrameArrived -= OnFirstFrameArrived;
                _reader.Dispose();
                _reader = null;
            }

            _player?.Dispose();
            _player = null;

            _mediaSource?.Dispose();
            _mediaSource = null;
        }

        // The preview stream where the camera has one, otherwise the record stream it is encoding from.
        private static MediaFrameSource FindVideoSource(MediaCapture capture)
        {
            MediaFrameSource record = null;

            foreach (var item in capture.FrameSources)
            {
                var info = item.Value.Info;
                if (info.SourceKind != MediaFrameSourceKind.Color)
                {
                    continue;
                }

                if (info.MediaStreamType == MediaStreamType.VideoPreview)
                {
                    return item.Value;
                }
                else if (info.MediaStreamType == MediaStreamType.VideoRecord)
                {
                    record ??= item.Value;
                }
            }

            return record;
        }
    }
}
