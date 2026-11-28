//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Gallery;
using Telegram.Services;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.ViewModels.Gallery;
using Telegram.Views;
using Telegram.Views.Authorization;
using Telegram.Views.Popups;
using Telegram.Views.Settings;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Telegram.Navigation.Services
{
    public interface INavigationService
    {
        void GoBack(NavigationState state = null, PageTransition transition = PageTransition.Default);
        void GoBackAt(int index, bool back = true);
        void GoBackTo(Type page, object parameter = null, NavigationState state = null);
        void GoForward();

        NavigationPage Content { get; }

        XamlRoot XamlRoot { get; }

        bool Navigate(Type page, object parameter = null, NavigationState state = null, PageTransition transition = PageTransition.Default);
        void GoBack(NavigatingEventArgs args);

        event EventHandler<NavigatedEventArgs> Navigated;

        bool CanGoBack { get; }
        bool CanGoForward { get; }

        void Suspend();
        void Resume();

        void Block();


        Task<WindowContext> OpenAsync(ViewServiceOptions parameters);
        Task<WindowContext> OpenAsync(Type page, object parameter = null, string title = null, Size size = default);
        Task<ModalPopupResult> ShowPopupAsync(ModalPopup popup, object parameter = null, ElementTheme requestedTheme = ElementTheme.Default);
        void ShowPopup(ModalPopup popup, object parameter = null, ElementTheme requestedTheme = ElementTheme.Default);

        Task<ModalPopupResult> ShowPopupAsync(string message, string title = null, string primary = null, string secondary = null, string tertiary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default);
        //Task<ModalPopupResult> ShowPopupAsync(FrameworkElement target, string message, string title = null, string primary = null, string secondary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default);
        Task<ModalPopupResult> ShowPopupAsync(FormattedText message, string title = null, string primary = null, string secondary = null, string tertiary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default);
        ////Task<ModalPopupResult> ShowPopupAsync(FrameworkElement target, FormattedText message, string title = null, string primary = null, string secondary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default);
        void ShowPopup(string message, string title = null, string primary = null, string secondary = null, string tertiary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default);
        void ShowPopup(FormattedText message, string title = null, string primary = null, string secondary = null, string tertiary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default);
        Task<InputPopupResult> ShowInputAsync(InputPopupType type, string message, string title = null, string placeholderText = null, string primary = null, string secondary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default);
        //Task<InputPopupResult> ShowInputAsync(FrameworkElement target, InputPopupType type, string message, string title = null, string placeholderText = null, string primary = null, string secondary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default)

        void HidePopup(Type type);

        ToastPopup ShowToast(string text, ElementTheme requestedTheme = ElementTheme.Dark, TimeSpan? dismissAfter = null);
        ToastPopup ShowToast(string text, ToastPopupIcon icon, ElementTheme requestedTheme = ElementTheme.Dark, TimeSpan? dismissAfter = null);
        ToastPopup ShowToast(FormattedText text, ElementTheme requestedTheme = ElementTheme.Dark, TimeSpan? dismissAfter = null);
        ToastPopup ShowToast(FormattedText text, ToastPopupIcon icon, ElementTheme requestedTheme = ElementTheme.Dark, TimeSpan? dismissAfter = null);
        void ShowToast(Error error);

        void ShowGallery(GalleryViewModelBase parameter, FrameworkElement closing = null, double timestamp = 0);

        object CurrentPageParam { get; }
        Type CurrentPageType { get; }

        IDispatcherContext Dispatcher { get; }

        void ClearCache(bool removeCachedPagesInBackStack = false);

        NavigationFrame Frame { get; }

        WindowContext Window { get; }

        ISession Session { get; }

        IReadOnlyList<NavigationStackItem> BackStack { get; }
        IReadOnlyList<NavigationStackItem> ForwardStack { get; }

        void AddToBackStack(Type type, object parameter = null);
        void InsertToBackStack(int index, Type type, object parameter = null);
        void RemoveFromBackStack(int index);
        void ClearBackStack();
        void ClearForwardStack();

        void RaiseBackRequested(BackRequestedRoutedEventArgs args);
        void RaiseForwardRequested(HandledEventArgs args);
        void RaiseShortcutInvoked(ShortcutInvokedEventArgs args);
    }

    /// <summary>
    /// A history entry. Title and position are the page's own, taken when it was left.
    /// </summary>
    public partial class NavigationStackItem : BindableBase
    {
        public NavigationStackItem(Type sourcePageType, object parameter, NavigationState state = null, PageTransition transition = PageTransition.Default)
        {
            SourcePageType = sourcePageType;
            Parameter = parameter;
            State = state ?? new NavigationState();
            Transition = transition;
            Mode = HostedNavigationMode.Child;
        }

        public Type SourcePageType { get; }

        public object Parameter { get; internal set; }

        /// <summary>
        /// What the entry's view model is handed when navigated to: filled by whoever navigates to it,
        /// forward or back.
        /// </summary>
        public NavigationState State { get; }

        /// <summary>
        /// How the entry was entered, played in reverse to leave it on back.
        /// </summary>
        public PageTransition Transition { get; }

        private string _title;
        public string Title
        {
            get => _title;
            set => Set(ref _title, value);
        }

        public HostedNavigationMode Mode { get; private set; }

        public HostedPagePositionBase Position { get; private set; }

        internal void Capture(NavigationPage page)
        {
            if (page is HostedPage hosted)
            {
                Title = hosted.GetTitle();
                Position = hosted.GetPosition();
                Mode = hosted.NavigationMode;
            }
        }

        public override string ToString()
        {
            return Title;
        }
    }

    public class ShortcutInvokedEventArgs : HandledEventArgs
    {
        public ShortcutInvokedEventArgs(InvokedShortcut shortcut, VirtualKeyModifiers modifiers)
        {
            Shortcut = shortcut;
            Modifiers = modifiers;
        }

        public InvokedShortcut Shortcut { get; }

        public VirtualKeyModifiers Modifiers { get; }
    }

    public partial class NavigationService : INavigationService
    {
        // Never gone forward into: a password screen is left behind on back.
        private static readonly HashSet<Type> _unallowedTypes = new()
        {
            typeof(SettingsPasswordPage),
            typeof(SettingsPasscodePage)
        };

        // Steps of a flow that ends elsewhere: kept in neither stack.
        private static readonly HashSet<Type> _authorizationTypes = new()
        {
            typeof(AuthorizationCodePage),
            typeof(AuthorizationEmailAddressPage),
            typeof(AuthorizationEmailCodePage),
            typeof(AuthorizationPasswordPage),
            //typeof(AuthorizationRecoveryPage),
            typeof(AuthorizationRegistrationPage)
        };

        private readonly IViewService viewService = new ViewService();

        // Both end with the entry next in their direction, as the platform's stacks do.
        private readonly List<NavigationStackItem> _backStack = new();
        private readonly List<NavigationStackItem> _forwardStack = new();

        private NavigationStackItem _current;

        // The navigation in progress, for the frame's handlers.
        private NavigationStackItem _pendingEntry;
        private bool _pendingReplace;

        private bool _blocked;

        public NavigationService(ISession session, WindowContext window, NavigationFrame frame, string id)
        {
            Window = window;
            Dispatcher = window?.Dispatcher;
            Session = session;
            FrameId = id;
            Frame = frame;

            // A service that only shows popups has no frame.
            if (frame != null)
            {
                frame.NavigationService = this;
                frame.Navigating += OnNavigating;
                frame.Preparing += OnPreparing;
                frame.Navigated += OnNavigated;
                frame.Released += OnReleased;
            }
        }

        public WindowContext Window { get; }

        public NavigationFrame Frame { get; }

        public string FrameId { get; }

        public NavigationPage Content => Frame?.Content;

        public XamlRoot XamlRoot => Window.XamlRoot;

        public IDispatcherContext Dispatcher { get; }

        public ISession Session { get; private set; }

        public BootStrapper.BackButton BackButtonHandling { get; internal set; }

        public IReadOnlyList<NavigationStackItem> BackStack => _backStack;

        public IReadOnlyList<NavigationStackItem> ForwardStack => _forwardStack;

        public event EventHandler BackStackChanged;

        public bool IsNavigating { get; private set; }

        public Type CurrentPageType => _current?.SourcePageType;

        public object CurrentPageParam => _current?.Parameter;

        public NavigationStackItem CurrentEntry => _current;

        private Type _rootPageType;

        /// <summary>
        /// The page the history always starts from: an entry for it goes under any page that would
        /// otherwise have nothing to go back to. The detail pane's empty page.
        /// </summary>
        public Type RootPageType
        {
            get => _rootPageType;
            set
            {
                _rootPageType = value;
                EnsureRoot();
            }
        }

        private void EnsureRoot()
        {
            if (_rootPageType != null && _current != null && _backStack.Count == 0 && _current.SourcePageType != _rootPageType)
            {
                _backStack.Add(new NavigationStackItem(_rootPageType, null));
            }
        }

        #region Routing

        public event EventHandler<BackRequestedRoutedEventArgs> BackRequested;
        public void RaiseBackRequested(BackRequestedRoutedEventArgs args)
        {
            BackRequested?.Invoke(this, args);

            if (BackButtonHandling == BootStrapper.BackButton.Attach && !args.Handled && (args.Handled = CanGoBack))
            {
                GoBack();
            }
        }

        public event EventHandler<HandledEventArgs> ForwardRequested;
        public void RaiseForwardRequested(HandledEventArgs args)
        {
            ForwardRequested?.Invoke(this, args);

            if (!args.Handled && CanGoForward)
            {
                GoForward();
            }
        }

        public event EventHandler<ShortcutInvokedEventArgs> ShortcutInvoked;
        public void RaiseShortcutInvoked(ShortcutInvokedEventArgs args)
        {
            ShortcutInvoked?.Invoke(this, args);
        }

        #endregion

        #region History

        public void GoBackAt(int index, bool back = true)
        {
            while (_backStack.Count > index + 1)
            {
                RemoveFromBackStack(index + 1);
            }

            if (CanGoBack && back)
            {
                GoBack();
            }
            else
            {
                BackStackChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Drops the history above its first entry and goes back to <paramref name="page"/>, as if it
        /// had been under the current page all along. Neither the current page nor anything ahead of
        /// it is kept.
        /// </summary>
        public void GoBackTo(Type page, object parameter = null, NavigationState state = null)
        {
            if (_blocked)
            {
                return;
            }

            while (_backStack.Count > 1)
            {
                _backStack.RemoveAt(1);
            }

            NavigateCore(new NavigationStackItem(page, parameter, state, PageTransition.Backward), PageTransition.Backward, NavigationMode.Back, true);
        }

        public void InsertToBackStack(int index, Type type, object parameter = null)
        {
            _backStack.Insert(index, new NavigationStackItem(type, parameter));
        }

        public void AddToBackStack(Type type, object parameter = null)
        {
            _backStack.Add(new NavigationStackItem(type, parameter));
        }

        public void RemoveFromBackStack(int index)
        {
            if (_backStack.Count > index)
            {
                _backStack.RemoveAt(index);
            }
        }

        public void ClearBackStack()
        {
            _backStack.Clear();
        }

        public void ClearForwardStack()
        {
            _forwardStack.Clear();
        }

        public void ClearCache(bool removeCachedPagesInBackStack = false)
        {
            Logger.Info($"Frame: {FrameId}");
            Frame?.ClearCache();
        }

        #endregion

        #region Navigator

        public virtual bool Navigate(Type page, object parameter = null, NavigationState state = null, PageTransition transition = PageTransition.Default)
        {
            Logger.Info($"Page: {page}, Parameter: {parameter}, Transition: {transition}");

            if (page == null)
            {
                throw new ArgumentNullException(nameof(page));
            }

            if (_blocked)
            {
                return false;
            }

            // Not again to the page already shown.
            if (page == CurrentPageType && (parameter == CurrentPageParam || (parameter?.Equals(CurrentPageParam) ?? false)))
            {
                return false;
            }

            return NavigateCore(new NavigationStackItem(page, parameter, state, transition), transition, NavigationMode.New, false);
        }

        /// <summary>
        /// Navigates to <paramref name="entry"/>. With <paramref name="replace"/>, the entry takes the
        /// current one's place: that one is dropped rather than kept for going back, and the forward
        /// stack goes too.
        /// </summary>
        protected bool NavigateCore(NavigationStackItem entry, PageTransition transition, NavigationMode mode, bool replace)
        {
            _pendingEntry = entry;
            _pendingReplace = replace;

            try
            {
                IsNavigating = true;
                return Frame.Navigate(entry.SourcePageType, entry.Parameter, transition, mode);
            }
            finally
            {
                IsNavigating = false;

                _pendingEntry = null;
                _pendingReplace = false;
            }
        }

        public void GoBack(NavigationState state = null, PageTransition transition = PageTransition.Default)
        {
            if (!CanGoBack || _blocked)
            {
                return;
            }

            var entry = _backStack[^1];

            if (state != null)
            {
                foreach (var item in state)
                {
                    entry.State[item.Key] = item.Value;
                }
            }

            if (transition == PageTransition.Default)
            {
                transition = Reverse(_current?.Transition ?? PageTransition.Default);
            }

            NavigateCore(entry, transition, NavigationMode.Back, false);
        }

        public void GoForward()
        {
            if (!CanGoForward || _blocked)
            {
                return;
            }

            var entry = _forwardStack[^1];
            NavigateCore(entry, entry.Transition, NavigationMode.Forward, false);
        }

        /// <summary>
        /// Replays a navigation that a view model cancelled, once it has decided to let it through.
        /// </summary>
        public void GoBack(NavigatingEventArgs args)
        {
            if (args == null || args.NavigationMode == NavigationMode.Back)
            {
                GoBack();
            }
            else if (args.NavigationMode == NavigationMode.Forward)
            {
                GoForward();
            }
            else if (args.NavigationMode == NavigationMode.New)
            {
                Navigate(args.TargetPageType, args.TargetPageParameter, transition: args.Transition);
            }
            else
            {
                Logger.Info("Unhandled: " + args.NavigationMode);
            }
        }

        // Back plays the way in backwards. An entry that came in without motion leaves without it.
        private static PageTransition Reverse(PageTransition transition)
        {
            return transition switch
            {
                PageTransition.None => PageTransition.None,
                PageTransition.Default => PageTransition.Default,
                PageTransition.Entrance => PageTransition.Exit,
                _ => PageTransition.Backward
            };
        }

        public bool CanGoBack => _backStack.Count > 0;

        public bool CanGoForward => _forwardStack.Count > 0;

        private void OnNavigating(object sender, NavigatingEventArgs e)
        {
            if (e.Content is not NavigationPage page)
            {
                return;
            }

            // Before anything changes, the view model included: the entry being left keeps the
            // page's title and scroll position, and a page reused in place would answer for the
            // next chat.
            if (e.NavigationMode is NavigationMode.New or NavigationMode.Forward)
            {
                _current?.Capture(page);
            }

            // The logical leave: the view model is told now, the page is torn down once released.
            if (page.DataContext is INavigable dataContext)
            {
                e.Cancel = !NavigatingFrom(dataContext, e.SourcePageType, e.Parameter, false, e.NavigationMode, e.Transition);

                if (e.Cancel)
                {
                    return;
                }

                NavigateFrom(dataContext, false);
            }
        }

        private void OnPreparing(object sender, NavigatedEventArgs e)
        {
            var page = e.Content;

            try
            {
                page.XamlRoot ??= XamlRoot;
            }
            catch
            {
                // Guard against navigation failure
            }

            if (page is IActivablePage activable)
            {
                activable.Activate(this);
            }
            else if (page is BlankPage blank)
            {
                blank.Activate(this);
            }

            // Before the page enters the tree, so that it is never laid out without one.
            var dataContext = ViewModelForPage(page, true);
            if (dataContext != null)
            {
                // A view model that already has a service keeps it: MainPage hands its children the
                // detail one, and the root frame must not take it over.
                dataContext.NavigationService ??= this;
                dataContext.Dispatcher ??= Dispatcher;
            }
        }

        private async void OnNavigated(object sender, NavigatedEventArgs e)
        {
            var entry = _pendingEntry ?? new NavigationStackItem(e.SourcePageType, e.Parameter);
            var replace = _pendingReplace;

            _pendingEntry = null;
            _pendingReplace = false;

            var previous = _current;
            _current = entry;

            UpdateStacks(previous, entry, e, replace);

            Navigated?.Invoke(this, e);
            OverlayWindow.TryHide(XamlRoot, ModalPopupResult.None);

            if (e.Content.DataContext is INavigable dataContext)
            {
                await dataContext.NavigatedToAsync(e.Parameter, e.NavigationMode, entry.State);
            }
        }

        private void UpdateStacks(NavigationStackItem previous, NavigationStackItem entry, NavigatedEventArgs e, bool replace)
        {
            var keep = previous != null && !replace && !_authorizationTypes.Contains(previous.SourcePageType);

            if (e.NavigationMode == NavigationMode.Back)
            {
                if (_backStack.Count > 0 && _backStack[^1] == entry)
                {
                    _backStack.RemoveAt(_backStack.Count - 1);
                }

                if (replace || (previous != null && _unallowedTypes.Contains(previous.SourcePageType)))
                {
                    _forwardStack.Clear();
                }
                else if (keep)
                {
                    _forwardStack.Add(previous);
                }

                // MasterDetailView animates the header from where the list was.
                e.VerticalOffset = entry.Position switch
                {
                    HostedPageScrollViewerPosition scrollViewerPosition => scrollViewerPosition.ScrollPosition,
                    HostedPageListViewPosition listViewPosition => listViewPosition.ScrollPosition,
                    _ => 0
                };

                if (entry.Position != null && e.Content is HostedPage page && entry.SourcePageType == page.GetType())
                {
                    page.SetPosition(entry.Position);
                }
            }
            else
            {
                if (e.NavigationMode == NavigationMode.Forward)
                {
                    if (_forwardStack.Count > 0 && _forwardStack[^1] == entry)
                    {
                        _forwardStack.RemoveAt(_forwardStack.Count - 1);
                    }
                }
                else
                {
                    _forwardStack.Clear();
                }

                if (keep)
                {
                    _backStack.Add(previous);
                }
            }

            EnsureRoot();
        }

        private void OnReleased(object sender, NavigationPageReleasedEventArgs e)
        {
            if (e.Page is IActivablePage activable)
            {
                activable.Deactivate(e.Navigation);
            }
        }

        /// <summary>
        /// Releases every page and unhooks the frame, for a host that is going away.
        /// </summary>
        public void Detach()
        {
            if (Frame == null)
            {
                return;
            }

            Frame.Clear();

            Frame.Navigating -= OnNavigating;
            Frame.Preparing -= OnPreparing;
            Frame.Navigated -= OnNavigated;
            Frame.Released -= OnReleased;
            Frame.NavigationService = null;
        }

        public async void Resume()
        {
            var page = Content;
            if (page != null)
            {
                if (page is IActivablePage activable)
                {
                    activable.Activate(this);
                }

                var dataContext = ViewModelForPage(page);
                if (dataContext != null)
                {
                    dataContext.NavigationService ??= this;
                    dataContext.Dispatcher ??= Dispatcher;
                    await dataContext.NavigatedToAsync(CurrentPageParam, NavigationMode.New, _current?.State ?? new NavigationState());
                }
            }
        }

        public void Suspend()
        {
            var page = Content;
            if (page != null)
            {
                var dataContext = ViewModelForPage(page);
                if (dataContext != null)
                {
                    NavigateFrom(dataContext, true);
                }

                if (page is IActivablePage activable)
                {
                    activable.Deactivate(false);
                }
            }
        }

        public void Block()
        {
            _blocked = true;
        }

        private INavigable ViewModelForPage(NavigationPage page, bool allowCreate = false)
        {
            if (page.DataContext is not INavigable or null && allowCreate)
            {
                // to support dependency injection, but keeping it optional.
                var viewModel = BootStrapper.Current.ViewModelForPage(page, Session);
                if (viewModel != null)
                {
                    page.DataContext = viewModel;
                    return viewModel;
                }
            }
            return page.DataContext as INavigable;
        }

        // before navigate (cancellable)
        private bool NavigatingFrom(INavigable dataContext, Type targetPageType, object targetPageParameter, bool suspending, NavigationMode mode, PageTransition transition)
        {
            Logger.Info($"Suspending: {suspending}");

            dataContext.NavigationService ??= this;
            dataContext.Dispatcher ??= Dispatcher;

            var args = new NavigatingEventArgs
            {
                NavigationMode = mode,
                Transition = transition,
                SourcePageType = CurrentPageType,
                Parameter = CurrentPageParam,
                Suspending = suspending,
                TargetPageType = targetPageType,
                TargetPageParameter = targetPageParameter
            };
            dataContext.NavigatingFrom(args);
            return !args.Cancel;
        }

        // after navigate
        private void NavigateFrom(INavigable dataContext, bool suspending)
        {
            Logger.Info($"Suspending: {suspending}");

            dataContext.NavigationService ??= this;
            dataContext.Dispatcher ??= Dispatcher;

            dataContext.NavigatedFrom(_current?.State, suspending);
        }

        #endregion

        public Task<WindowContext> OpenAsync(ViewServiceOptions parameters) => viewService.OpenAsync(parameters);

        public Task<WindowContext> OpenAsync(Type page, object parameter = null, string title = null, Size size = default)
        {
            Logger.Info($"Page: {page}, Parameter: {parameter}, Title: {title}, Size: {size}");
            return viewService.OpenAsync(Session, page, parameter, title, size);
        }

        public void ShowPopup(ModalPopup popup, object parameter = null, ElementTheme requestedTheme = ElementTheme.Default)
        {
            _ = ShowPopupAsync(popup, parameter, requestedTheme);
        }

        public Task<ModalPopupResult> ShowPopupAsync(ModalPopup popup, object parameter = null, ElementTheme requestedTheme = ElementTheme.Default)
        {
            if (requestedTheme != ElementTheme.Default)
            {
                popup.RequestedTheme = requestedTheme;
            }

            var viewModel = BootStrapper.Current.ViewModelForPage(popup, Session);
            if (viewModel != null)
            {
                viewModel.NavigationService = this;
                viewModel.Dispatcher = Dispatcher;

                void OnClosed(ModalPopup sender, ModalPopupClosedEventArgs args)
                {
                    viewModel.NavigatedFrom(null, false);
                    popup.OnNavigatedFrom();
                    popup.Closed -= OnClosed;
                }

                popup.DataContext = viewModel;

                _ = viewModel.NavigatedToAsync(parameter, NavigationMode.New, null);
                popup.OnNavigatedTo(parameter);
                popup.Closed += OnClosed;
            }

            return popup.ShowAsync(XamlRoot);
        }

        public Task<ModalPopupResult> ShowPopupAsync(string message, string title = null, string primary = null, string secondary = null, string tertiary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default)
        {
            if (ModalPopup.IsAnyPopupOpen(XamlRoot))
            {
                return MessagePopup.ShowNestedAsync(XamlRoot, message, title, primary, secondary, tertiary, destructive, requestedTheme);
            }

            return MessagePopup.ShowAsync(XamlRoot, message, title, primary, secondary, tertiary, destructive, requestedTheme);
        }

        //public Task<ModalPopupResult> ShowPopupAsync(FrameworkElement target, string message, string title = null, string primary = null, string secondary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default)
        //{
        //    return MessagePopup.ShowAsync(target, message, title, primary, secondary, destructive, requestedTheme);
        //}

        public Task<ModalPopupResult> ShowPopupAsync(FormattedText message, string title = null, string primary = null, string secondary = null, string tertiary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default)
        {
            return MessagePopup.ShowAsync(XamlRoot, message, title, primary, secondary, tertiary, destructive, requestedTheme);
        }

        ////public Task<ModalPopupResult> ShowPopupAsync(FrameworkElement target, FormattedText message, string title = null, string primary = null, string secondary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default)
        ////{
        ////    return MessagePopup.ShowAsync(target, message, title, primary, secondary, destructive, requestedTheme);
        ////}

        public void ShowPopup(string message, string title = null, string primary = null, string secondary = null, string tertiary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default)
        {
            if (ModalPopup.IsAnyPopupOpen(XamlRoot))
            {
                _ = MessagePopup.ShowNestedAsync(XamlRoot, message, title, primary, secondary, tertiary, destructive, requestedTheme);
                return;
            }

            _ = MessagePopup.ShowAsync(XamlRoot, message, title, primary, secondary, tertiary, destructive, requestedTheme);
        }

        public void ShowPopup(FormattedText message, string title = null, string primary = null, string secondary = null, string tertiary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default)
        {
            if (ModalPopup.IsAnyPopupOpen(XamlRoot))
            {
                _ = MessagePopup.ShowNestedAsync(XamlRoot, message, title, primary, secondary, tertiary, destructive, requestedTheme);
                return;
            }

            _ = MessagePopup.ShowAsync(XamlRoot, message, title, primary, secondary, tertiary, destructive, requestedTheme);
        }

        public Task<InputPopupResult> ShowInputAsync(InputPopupType type, string message, string title = null, string placeholderText = null, string primary = null, string secondary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default)
        {
            return InputPopup.ShowAsync(XamlRoot, type, message, title, placeholderText, primary, secondary, destructive, requestedTheme);
        }

        //public Task<InputPopupResult> ShowInputAsync(FrameworkElement target, InputPopupType type, string message, string title = null, string placeholderText = null, string primary = null, string secondary = null, bool destructive = false, ElementTheme requestedTheme = ElementTheme.Default)
        //{
        //    return InputPopup.ShowAsync(target, type, message, title, placeholderText, primary, secondary, destructive, requestedTheme);
        //}

        public void HidePopup(Type type)
        {
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            {
                if (popup.Child is ModalPopup modal && type == modal.GetType())
                {
                    modal.Hide();
                }
            }
        }

        public ToastPopup ShowToast(string text, ElementTheme requestedTheme = ElementTheme.Dark, TimeSpan? dismissAfter = null)
        {
            return ToastPopup.Show(XamlRoot, ClientEx.ParseMarkdown(text), null, requestedTheme, dismissAfter);
        }

        public ToastPopup ShowToast(string text, ToastPopupIcon icon, ElementTheme requestedTheme = ElementTheme.Dark, TimeSpan? dismissAfter = null)
        {
            return ToastPopup.Show(XamlRoot, ClientEx.ParseMarkdown(text), icon, requestedTheme, dismissAfter);
        }

        public ToastPopup ShowToast(FormattedText text, ElementTheme requestedTheme = ElementTheme.Dark, TimeSpan? dismissAfter = null)
        {
            return ToastPopup.Show(XamlRoot, text, null, requestedTheme, dismissAfter);
        }

        public ToastPopup ShowToast(FormattedText text, ToastPopupIcon icon, ElementTheme requestedTheme = ElementTheme.Dark, TimeSpan? dismissAfter = null)
        {
            return ToastPopup.Show(XamlRoot, text, icon, requestedTheme, dismissAfter);
        }

        public void ShowToast(Error error)
        {
            ToastPopup.ShowError(XamlRoot, error);
        }

        public void ShowGallery(GalleryViewModelBase parameter, FrameworkElement closing = null, double timestamp = 0)
        {
            parameter.NavigationService = this;
            _ = GalleryWindow.ShowAsync(XamlRoot, parameter, closing, timestamp);
        }

        public event EventHandler<NavigatedEventArgs> Navigated;
    }
}
