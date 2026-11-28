//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;
using Telegram.Controls;
using Telegram.Navigation.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;

namespace Telegram.Navigation
{
    /// <summary>
    /// A page a <see cref="NavigationFrame"/> shows.
    /// </summary>
    public partial class NavigationPage : UserControl
    {
        private bool _loaded;
        private bool _unloaded;

        /// <summary>
        /// How the page comes in when the caller does not say, and goes out again on back: None swaps
        /// it without motion, Entrance rises it from below, anything else slides it.
        /// </summary>
        public PageTransition Transition { get; set; }

        public PageRetention Retention { get; set; }

        /// <summary>
        /// Between leaving and being released: the page is still on screen, playing its exit, but it
        /// is no longer the one navigated to, and its view model has been told so.
        /// </summary>
        public bool IsLeaving { get; internal set; }

        /// <summary>
        /// The frame showing the page, null once it has been released. Assigned by the frame rather
        /// than read from Parent, which is null until the frame is in the live tree, and the first
        /// navigation of a window happens before that.
        /// </summary>
        public NavigationFrame Frame { get; internal set; }

        public bool IsConnected => _loaded;
        public bool IsDisconnected => _unloaded;

        // Loaded and Unloaded are only subscribed while someone listens: most pages never do.
        private event RoutedEventHandler _connected;
        public event RoutedEventHandler Connected
        {
            add
            {
                if (_connected == null && _disconnected == null)
                {
                    Loaded += OnChanged;
                    Unloaded += OnChanged;
                }

                _connected += value;
            }
            remove
            {
                _connected -= value;

                if (_connected == null && _disconnected == null)
                {
                    Loaded -= OnChanged;
                    Unloaded -= OnChanged;
                }
            }
        }

        private event RoutedEventHandler _disconnected;
        public event RoutedEventHandler Disconnected
        {
            add
            {
                if (_connected == null && _disconnected == null)
                {
                    Loaded += OnChanged;
                    Unloaded += OnChanged;
                }

                _disconnected += value;
            }
            remove
            {
                _disconnected -= value;

                if (_connected == null && _disconnected == null)
                {
                    Loaded -= OnChanged;
                    Unloaded -= OnChanged;
                }
            }
        }

        private void OnChanged(object sender, RoutedEventArgs e)
        {
            // Parent is null for a template root, and under an ancestor that left the live tree:
            // see the top of FrameworkElementEx.cs.
            var parent = this.GetParent();
            if (parent != null && !_loaded)
            {
                _loaded = true;
                _unloaded = false;
                _connected?.Invoke(this, e);
            }
            else if (parent == null && _loaded)
            {
                _loaded = false;
                _unloaded = true;
                _disconnected?.Invoke(sender, e);
            }
        }

        /// <summary>
        /// Before leaving; setting Cancel stops the navigation.
        /// </summary>
        protected virtual void OnNavigatingFrom(NavigatingEventArgs e)
        {
        }

        protected virtual void OnNavigatedFrom(NavigatedEventArgs e)
        {
        }

        protected virtual void OnNavigatedTo(NavigatedEventArgs e)
        {
        }

        /// <summary>
        /// A <see cref="PageRetention.Resident"/> page was released and is about to be collapsed. It
        /// stays loaded, so whatever unloading would have done is done here.
        /// </summary>
        protected virtual void OnParked()
        {
        }

        internal void RaiseNavigatingFrom(NavigatingEventArgs e) => OnNavigatingFrom(e);

        internal void RaiseNavigatedFrom(NavigatedEventArgs e) => OnNavigatedFrom(e);

        internal void RaiseNavigatedTo(NavigatedEventArgs e) => OnNavigatedTo(e);

        internal void RaiseParked() => OnParked();

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new NavigationPageAutomationPeer(this);
        }

        private sealed partial class NavigationPageAutomationPeer : FrameworkElementAutomationPeer
        {
            private readonly NavigationPage _owner;

            public NavigationPageAutomationPeer(NavigationPage owner)
                : base(owner)
            {
                _owner = owner;
            }

            // A page playing its exit is still drawn, and still in the tree, for 150 ms.
            protected override IList<AutomationPeer> GetChildrenCore()
            {
                return _owner.IsLeaving ? null : base.GetChildrenCore();
            }

            protected override bool IsOffscreenCore()
            {
                return _owner.IsLeaving || base.IsOffscreenCore();
            }
        }
    }
}
