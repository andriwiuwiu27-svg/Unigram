//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Telegram.Common;
using Telegram.Navigation.Services;
using Windows.Foundation;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Telegram.Navigation
{
    /// <summary>
    /// How a page enters. <see cref="Default"/> lets the frame choose from the navigation mode and
    /// the pages' own <see cref="NavigationPage.Transition"/>.
    /// </summary>
    public enum PageTransition
    {
        Default,
        None,

        /// <summary>The incoming page slides in from the trailing edge, on top.</summary>
        Forward,

        /// <summary>The outgoing page slides out to the trailing edge, on top.</summary>
        Backward,

        /// <summary>The incoming page rises from below, on top: a top-level destination rather than a step into one.</summary>
        Entrance,

        /// <summary>The outgoing page sinks, on top: back from an <see cref="Entrance"/>.</summary>
        Exit
    }

    /// <summary>
    /// What a <see cref="NavigationFrame"/> does with a page once it stops showing it.
    /// </summary>
    public enum PageRetention
    {
        /// <summary>Dropped, and created again the next time.</summary>
        Discard,

        /// <summary>Kept out of the tree, one per type, while the frame's <see cref="NavigationFrame.CacheSize"/> allows.</summary>
        Cache,

        /// <summary>Kept out of the tree, one per type, whatever the cache size.</summary>
        Keep,

        /// <summary>Kept in the tree, collapsed, one per type: it never unloads.</summary>
        Resident
    }

    public sealed class PageTransitionContext
    {
        public PageTransitionContext(NavigationPage from, NavigationPage to, PageTransition transition, CompositionPropertySet properties)
        {
            From = from;
            To = to;
            Transition = transition;
            Properties = properties;
        }

        public NavigationPage From { get; }

        public NavigationPage To { get; }

        /// <summary>
        /// Forward, Backward, or None when the pages swap at once.
        /// </summary>
        public PageTransition Transition { get; }

        /// <summary>
        /// Its "Progress" scalar goes from 0 to 1 over the transition, already eased: anything that
        /// moves with the pages binds an expression to it. It is 1 when the transition is None.
        /// </summary>
        public CompositionPropertySet Properties { get; }
    }

    public sealed class NavigationPageReleasedEventArgs : EventArgs
    {
        public NavigationPageReleasedEventArgs(NavigationPage page, bool navigation)
        {
            Page = page;
            Navigation = navigation;
        }

        public NavigationPage Page { get; }

        /// <summary>
        /// False when the page is reused in place for the navigation that released it, rather than
        /// left: the meaning of <see cref="IActivablePage.Deactivate(bool)"/>'s argument.
        /// </summary>
        public bool Navigation { get; }
    }

    /// <summary>
    /// Shows one <see cref="NavigationPage"/> at a time and moves between them. It knows nothing of
    /// history: <see cref="Services.NavigationService"/> keeps that, and the tab hosts need none.
    /// </summary>
    /// <remarks>
    /// Pages are the panel's own children and stay where they were added until they are released,
    /// so a page is never reparented, and the one leaving stays live, drawn and laid out, until its
    /// transition ends. Nothing waits for a frame: the incoming page is added and the transition
    /// starts in the same pass.
    /// </remarks>
    public partial class NavigationFrame : Panel
    {
        // A 48 px slide and a cross-fade over 150 ms, decelerating: 1 - (1 - t)^3.
        private const float Distance = 48;
        private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(150);

        private readonly Dictionary<Type, NavigationPage> _cache = new();
        private readonly List<Type> _cacheOrder = new();

        private NavigationPage _content;
        private NavigationPage _leaving;
        private bool _releasePending;
        private bool _animated;
        private int _version;

        private NavigationPage _focusFrom;

        private CompositionPropertySet _properties;
        private ScalarKeyFrameAnimation _progress;
        private ExpressionAnimation _fadeIn;
        private ExpressionAnimation _fadeOut;
        private ExpressionAnimation _slideIn;
        private ExpressionAnimation _slideOut;

        /// <summary>
        /// How many <see cref="PageRetention.Cache"/> pages are kept.
        /// </summary>
        public int CacheSize { get; set; } = 10;

        public NavigationPage Content => _content;

        /// <summary>
        /// The service whose history this frame shows; null for a frame driven directly, such as a tab host.
        /// </summary>
        public INavigationService NavigationService { get; internal set; }

        public Type SourcePageType { get; private set; }

        public object Parameter { get; private set; }

        /// <summary>
        /// Before anything changes; cancellable. The current page's own OnNavigatingFrom runs after it.
        /// </summary>
        public event EventHandler<NavigatingEventArgs> Navigating;

        /// <summary>
        /// The incoming page, resolved and not yet in the tree: where its view model is set.
        /// </summary>
        public event EventHandler<NavigatedEventArgs> Preparing;

        /// <summary>
        /// The incoming page is the content, in the tree. The outgoing page's OnNavigatedFrom and the
        /// incoming page's OnNavigatedTo run after it.
        /// </summary>
        public event EventHandler<NavigatedEventArgs> Navigated;

        public event EventHandler<PageTransitionContext> TransitionStarting;

        /// <summary>
        /// A page stopped showing, at the end of its exit, or at once when there was none, or was
        /// reused in place.
        /// </summary>
        public event EventHandler<NavigationPageReleasedEventArgs> Released;

        public bool Navigate(Type sourcePageType, object parameter = null, PageTransition transition = PageTransition.Default, NavigationMode mode = NavigationMode.New)
        {
            if (sourcePageType == null)
            {
                throw new ArgumentNullException(nameof(sourcePageType));
            }

            var previous = _content;
            var navigating = new NavigatingEventArgs
            {
                NavigationMode = mode,
                Transition = transition,
                SourcePageType = sourcePageType,
                Parameter = parameter,
                TargetPageType = sourcePageType,
                TargetPageParameter = parameter,
                Content = previous
            };

            Navigating?.Invoke(this, navigating);

            if (navigating.Cancel)
            {
                return false;
            }

            if (previous != null)
            {
                previous.RaiseNavigatingFrom(navigating);

                if (navigating.Cancel)
                {
                    return false;
                }
            }

            // A navigation during a transition ends it first, so the page leaving now is the only one.
            // This is also what runs the pending release of a page that is being entered again.
            CompleteTransition();

            var page = Resolve(sourcePageType);
            var inPlace = page == previous;

            if (inPlace)
            {
                Released?.Invoke(this, new NavigationPageReleasedEventArgs(page, false));
            }

            var navigated = new NavigatedEventArgs
            {
                NavigationMode = mode,
                Transition = transition,
                SourcePageType = sourcePageType,
                Parameter = parameter,
                Content = page
            };

            Preparing?.Invoke(this, navigated);

            _content = page;
            SourcePageType = sourcePageType;
            Parameter = parameter;

            if (inPlace is false)
            {
                Show(previous, page, Resolve(transition, mode, previous, page));
            }

            Navigated?.Invoke(this, navigated);

            previous?.RaiseNavigatedFrom(navigated);
            page.RaiseNavigatedTo(navigated);

            if (_releasePending)
            {
                CompleteTransition();
            }

            return true;
        }

        /// <summary>
        /// Releases every page, the current one included, and forgets the cache: the frame shows
        /// nothing after it. For a host that is going away.
        /// </summary>
        public void Clear()
        {
            CompleteTransition();

            var content = _content;
            if (content != null)
            {
                _content = null;
                SourcePageType = null;
                Parameter = null;

                // Out of the cache first, so that it is released rather than parked.
                if (_cache.Remove(content.GetType()))
                {
                    _cacheOrder.Remove(content.GetType());
                }

                content.IsLeaving = true;
                Release(content, true);
            }

            ClearCache(true);

            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i] is NavigationPage child)
                {
                    child.Frame = null;
                }
            }

            Children.Clear();
        }

        /// <summary>
        /// Drops the cached pages, except the current one; with <paramref name="all"/>, the kept and
        /// resident ones too.
        /// </summary>
        public void ClearCache(bool all = false)
        {
            for (int i = _cacheOrder.Count - 1; i >= 0; i--)
            {
                var type = _cacheOrder[i];
                var page = _cache[type];

                if (page == _content || (all is false && page.Retention != PageRetention.Cache))
                {
                    continue;
                }

                _cache.Remove(type);
                _cacheOrder.RemoveAt(i);

                // A parked page was only deactivated as one reused in place; this is its last release.
                if (page.Retention == PageRetention.Resident && page != _leaving && page.Frame == this)
                {
                    Released?.Invoke(this, new NavigationPageReleasedEventArgs(page, true));
                    Children.Remove(page);
                    page.Frame = null;
                }
            }
        }

        private NavigationPage Resolve(Type type)
        {
            if (_cache.TryGetValue(type, out NavigationPage cached))
            {
                var index = _cacheOrder.IndexOf(type);
                if (index != _cacheOrder.Count - 1)
                {
                    _cacheOrder.RemoveAt(index);
                    _cacheOrder.Add(type);
                }

                return cached;
            }

            var page = Create(type);

            if (page.Retention != PageRetention.Discard)
            {
                _cache[type] = page;
                _cacheOrder.Add(type);

                TrimCache();
            }

            return page;
        }

        private void TrimCache()
        {
            var count = 0;

            for (int i = _cacheOrder.Count - 1; i >= 0; i--)
            {
                var type = _cacheOrder[i];
                var page = _cache[type];

                if (page.Retention != PageRetention.Cache || page == _content || page == _leaving)
                {
                    continue;
                }

                if (++count > CacheSize)
                {
                    _cache.Remove(type);
                    _cacheOrder.RemoveAt(i);
                }
            }
        }

        private static NavigationPage Create(Type type)
        {
            // What Frame does: the XAML type information has an activator for every page, and it
            // needs no reflection metadata on .NET Native or NativeAOT.
            if (Application.Current is IXamlMetadataProvider provider && provider.GetXamlType(type) is IXamlType xamlType)
            {
                if (xamlType.ActivateInstance() is NavigationPage page)
                {
                    return page;
                }
            }

            return Activator.CreateInstance(type) as NavigationPage
                ?? throw new ArgumentException(type + " is not a NavigationPage", nameof(type));
        }

        private PageTransition Resolve(PageTransition transition, NavigationMode mode, NavigationPage previous, NavigationPage page)
        {
            if (previous == null || PowerSavingPolicy.AreSmoothTransitionsEnabled is false)
            {
                return PageTransition.None;
            }

            if (transition != PageTransition.Default)
            {
                return transition;
            }

            // Forward, the page entering decides; back, the page leaving, as it entered.
            if (mode == NavigationMode.Back)
            {
                return previous.Transition switch
                {
                    PageTransition.None => PageTransition.None,
                    PageTransition.Entrance => PageTransition.Exit,
                    _ => PageTransition.Backward
                };
            }

            return page.Transition switch
            {
                PageTransition.None => PageTransition.None,
                PageTransition.Entrance => PageTransition.Entrance,
                _ => PageTransition.Forward
            };
        }

        private void Show(NavigationPage previous, NavigationPage page, PageTransition transition)
        {
            if (page.Frame == this)
            {
                page.Visibility = Visibility.Visible;
            }
            else
            {
                page.Frame = this;
                Children.Add(page);
            }

            _properties ??= BootStrapper.Current.Compositor.CreatePropertySet();

            if (previous == null)
            {
                _properties.InsertScalar("Progress", 1);
                TransitionStarting?.Invoke(this, new PageTransitionContext(null, page, PageTransition.None, _properties));
                return;
            }

            previous.IsLeaving = true;
            previous.IsHitTestVisible = false;

            _leaving = previous;
            _version++;

            MoveFocus(previous, page);

            if (transition == PageTransition.None)
            {
                _properties.InsertScalar("Progress", 1);
                _releasePending = true;

                TransitionStarting?.Invoke(this, new PageTransitionContext(previous, page, PageTransition.None, _properties));
                return;
            }

            var compositor = _properties.Compositor;
            if (_progress == null)
            {
                _progress = compositor.CreateScalarKeyFrameAnimation();
                _progress.InsertKeyFrame(0, 0);
                _progress.InsertKeyFrame(1, 1, compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1), new Vector2(0.68f, 1)));
                _progress.Duration = Duration;

                _fadeIn = compositor.CreateExpressionAnimation("P.Progress");
                _fadeOut = compositor.CreateExpressionAnimation("1 - P.Progress");
                _slideIn = compositor.CreateExpressionAnimation("Vector3(X * (1 - P.Progress), Y * (1 - P.Progress), 0)");
                _slideOut = compositor.CreateExpressionAnimation("Vector3(X * P.Progress, Y * P.Progress, 0)");

                _fadeIn.SetReferenceParameter("P", _properties);
                _fadeOut.SetReferenceParameter("P", _properties);
                _slideIn.SetReferenceParameter("P", _properties);
                _slideOut.SetReferenceParameter("P", _properties);
            }

            // Sideways for a step, mirrored in RTL; up and down for a top-level destination.
            var vertical = transition is PageTransition.Entrance or PageTransition.Exit;
            var x = vertical ? 0 : FlowDirection == FlowDirection.RightToLeft ? -Distance : Distance;
            var y = vertical ? Distance : 0;

            _properties.InsertScalar("Progress", 0);

            var incoming = Prepare(page);
            var outgoing = Prepare(previous);

            _animated = true;

            // The page moving is the one on top: the incoming one forward, the outgoing one back.
            if (transition is PageTransition.Backward or PageTransition.Exit)
            {
                Canvas.SetZIndex(previous, 1);
                Canvas.SetZIndex(page, 0);

                _slideOut.SetScalarParameter("X", x);
                _slideOut.SetScalarParameter("Y", y);
                outgoing.StartAnimation("Translation", _slideOut);
            }
            else
            {
                Canvas.SetZIndex(previous, 0);
                Canvas.SetZIndex(page, 1);

                _slideIn.SetScalarParameter("X", x);
                _slideIn.SetScalarParameter("Y", y);
                incoming.StartAnimation("Translation", _slideIn);
            }

            incoming.StartAnimation("Opacity", _fadeIn);
            outgoing.StartAnimation("Opacity", _fadeOut);

            TransitionStarting?.Invoke(this, new PageTransitionContext(previous, page, transition, _properties));

            // Only the key frame is in the batch: expressions never complete.
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += new Completion(this, _version).OnCompleted;

            _properties.StartAnimation("Progress", _progress);

            batch.End();
        }

        private static Visual Prepare(NavigationPage page)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(page, true);
            return ElementComposition.GetElementVisual(page);
        }

        private static void Rest(NavigationPage page)
        {
            var visual = ElementComposition.GetElementVisual(page);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.Opacity = 1;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);

            Canvas.SetZIndex(page, 0);
        }

        /// <summary>
        /// Ends the running transition, if any: the incoming page comes to rest, and the outgoing one
        /// is released.
        /// </summary>
        private void CompleteTransition()
        {
            var leaving = _leaving;
            if (leaving == null)
            {
                return;
            }

            var animated = _animated;

            _leaving = null;
            _releasePending = false;
            _animated = false;
            _version++;

            if (animated)
            {
                _properties.StopAnimation("Progress");
                _properties.InsertScalar("Progress", 1);

                if (_content != null)
                {
                    Rest(_content);
                }

                Rest(leaving);
            }

            Release(leaving, true);
        }

        private void Release(NavigationPage page, bool navigation)
        {
            var park = page.Retention == PageRetention.Resident && _cache.ContainsKey(page.GetType());

            // Raised while the page is still in the tree: releasing is what deactivates it, and a
            // page tears its list down where it can still lay it out. A parked page is deactivated
            // as one reused in place is, since it waits for the next navigation to it.
            Released?.Invoke(this, new NavigationPageReleasedEventArgs(page, navigation && !park));

            page.IsLeaving = false;
            page.IsHitTestVisible = true;

            if (park)
            {
                page.RaiseParked();
                page.Visibility = Visibility.Collapsed;
            }
            else
            {
                Children.Remove(page);
                page.Frame = null;
            }

            // It may have outlived the cache size while it was the content.
            TrimCache();
        }

        private void MoveFocus(NavigationPage previous, NavigationPage page)
        {
            // Null until the frame is in the live tree, and then nothing in it can have focus.
            var xamlRoot = XamlRoot;
            if (xamlRoot == null || FocusManagerEx.TryGetFocusedElement(xamlRoot) is not DependencyObject focused || !IsWithin(focused, previous))
            {
                return;
            }

            _focusFrom = previous;

            // A resident page is shown again rather than added, and is not loaded again.
            if (page.IsLoaded)
            {
                FocusFirst(page);
            }
            else
            {
                page.Loaded += OnIncomingLoaded;
            }
        }

        private void OnIncomingLoaded(object sender, RoutedEventArgs e)
        {
            var page = sender as NavigationPage;
            page.Loaded -= OnIncomingLoaded;

            if (page == _content)
            {
                FocusFirst(page);
            }
        }

        private void FocusFirst(NavigationPage page)
        {
            var from = _focusFrom;
            _focusFrom = null;

            var xamlRoot = XamlRoot;
            if (xamlRoot == null)
            {
                return;
            }

            // Whatever the page focused itself wins: only focus left behind in the page that went is moved.
            if (FocusManagerEx.TryGetFocusedElement(xamlRoot) is DependencyObject focused && !IsWithin(focused, from))
            {
                return;
            }

            if (FocusManager.FindFirstFocusableElement(page) is Control control)
            {
                control.Focus(FocusState.Programmatic);
            }
        }

        private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
        {
            while (element != null)
            {
                if (element == ancestor)
                {
                    return true;
                }

                element = VisualTreeHelper.GetParent(element);
            }

            return false;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = 0;
            double height = 0;

            var children = Children;

            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                child.Measure(availableSize);

                width = Math.Max(width, child.DesiredSize.Width);
                height = Math.Max(height, child.DesiredSize.Height);
            }

            return new Size((float)width, (float)height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var children = Children;
            var bounds = new Rect(0, 0, (float)finalSize.Width, (float)finalSize.Height);

            for (int i = 0; i < children.Count; i++)
            {
                children[i].Arrange(bounds);
            }

            return finalSize;
        }

        private sealed class Completion
        {
            private readonly NavigationFrame _frame;
            private readonly int _version;

            public Completion(NavigationFrame frame, int version)
            {
                _frame = frame;
                _version = version;
            }

            public void OnCompleted(object sender, CompositionBatchCompletedEventArgs args)
            {
                ((CompositionScopedBatch)sender).Completed -= OnCompleted;

                // A navigation that started meanwhile has completed this one already.
                if (_frame._version == _version)
                {
                    _frame.CompleteTransition();
                }
            }
        }
    }
}
