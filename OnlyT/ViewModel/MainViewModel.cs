namespace OnlyT.ViewModel
{
    // ReSharper disable CatchAllClause
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Interop;
    using System.Windows.Media;
    using System.Windows.Threading;
    using EventArgs;
    using GalaSoft.MvvmLight;
    using GalaSoft.MvvmLight.Messaging;
    using GalaSoft.MvvmLight.Threading;
    using MaterialDesignThemes.Wpf;
    using Messages;
    using OnlyT.Common.Services.DateTime;
    using OnlyT.Models;
    using OnlyT.Services.OutputDisplays;
    using OnlyT.Services.Snackbar;
    using Serilog;
    using Services.CommandLine;
    using Services.CountdownTimer;
    using Services.Options;
    using Services.Timer;
    using WebServer;
    using Windows;

    /// <inheritdoc />
    /// <summary>
    /// View model for the main page (which is a placeholder for the Operator or Settings page)
    /// </summary>
    // ReSharper disable once ClassNeverInstantiated.Global
    public class MainViewModel : ViewModelBase
    {
        private readonly Dictionary<string, FrameworkElement> _pages = new Dictionary<string, FrameworkElement>();
        private readonly IOptionsService _optionsService;
        private readonly ICountdownTimerTriggerService _countdownTimerTriggerService;
        private readonly ITalkTimerService _timerService;
        private readonly ICommandLineService _commandLineService;
        private readonly IDateTimeService _dateTimeService;
        private readonly ITimerOutputDisplayService _timerOutputDisplayService;
        private readonly ICountdownOutputDisplayService _countdownDisplayService;
        private readonly IHttpServer _httpServer;
        private readonly ISnackbarService _snackbarService;
        private DispatcherTimer _heartbeatTimer;
        private FrameworkElement _currentPage;
        private DateTime _lastRefreshedSchedule = DateTime.MinValue;

        public MainViewModel(
           IOptionsService optionsService,
           ITalkTimerService timerService,
           ISnackbarService snackbarService,
           IHttpServer httpServer,
           ICommandLineService commandLineService,
           ICountdownTimerTriggerService countdownTimerTriggerService,
           IDateTimeService dateTimeService,
           ITimerOutputDisplayService timerOutputDisplayService,
           ICountdownOutputDisplayService countdownDisplayService)
        {
            _commandLineService = commandLineService;
            _dateTimeService = dateTimeService;
            _timerOutputDisplayService = timerOutputDisplayService;
            _countdownDisplayService = countdownDisplayService;

            if (commandLineService.NoGpu || ForceSoftwareRendering())
            {
                // disable hardware (GPU) rendering so that it's all done by the CPU...
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            }

            _snackbarService = snackbarService;
            _optionsService = optionsService;
            _httpServer = httpServer;
            _timerService = timerService;
            _countdownTimerTriggerService = countdownTimerTriggerService;

            _httpServer.RequestForTimerDataEvent += OnRequestForTimerData;
            
            // subscriptions...
            Messenger.Default.Register<NavigateMessage>(this, OnNavigate);
            Messenger.Default.Register<TimerMonitorChangedMessage>(this, OnTimerMonitorChanged);
            Messenger.Default.Register<CountdownMonitorChangedMessage>(this, OnCountdownMonitorChanged);
            Messenger.Default.Register<AlwaysOnTopChangedMessage>(this, OnAlwaysOnTopChanged);
            Messenger.Default.Register<HttpServerChangedMessage>(this, OnHttpServerChanged);
            Messenger.Default.Register<StopCountDownMessage>(this, OnStopCountdown);

            InitHttpServer();

            // should really create a "page service" rather than create views in the main view model!
            _pages.Add(OperatorPageViewModel.PageName, new OperatorPage());

            Messenger.Default.Send(new NavigateMessage(null, OperatorPageViewModel.PageName, null));

            // (fire and forget)
            Task.Run(LaunchTimerWindowAsync);
            
            InitHeartbeatTimer();
        }

        public ISnackbarMessageQueue TheSnackbarMessageQueue => _snackbarService.TheSnackbarMessageQueue;

        public FrameworkElement CurrentPage
        {
            get => _currentPage;
            set
            {
                if (!ReferenceEquals(_currentPage, value))
                {
                    _currentPage = value;
                    RaisePropertyChanged();
                }
            }
        }

        public bool AlwaysOnTop =>
            _optionsService.Options.AlwaysOnTop ||
            _timerOutputDisplayService.IsWindowVisible() ||
            _countdownDisplayService.IsWindowVisible();

        public string CurrentPageName { get; private set; }

        private bool CountDownActive => _countdownDisplayService.IsCountingDown;

        public void Closing(CancelEventArgs e)
        {
            e.Cancel = _timerService.IsRunning;
            if (!e.Cancel)
            {
                Messenger.Default.Send(new ShutDownMessage(CurrentPageName));
                CloseTimerWindow();
                CloseCountdownWindow();
            }
        }

        private void CloseCountdownWindow()
        {
            try
            {
                _countdownDisplayService.Close();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not close countdown window");
            }
        }

        private void InitSettingsPage()
        {
            // we only init the settings page when first used.
            if (!_pages.ContainsKey(SettingsPageViewModel.PageName))
            {
                _pages.Add(SettingsPageViewModel.PageName, new SettingsPage(_commandLineService));
            }
        }

        private void OnHttpServerChanged(HttpServerChangedMessage msg)
        {
            _httpServer.Stop();
            InitHttpServer();
        }

        private void InitHttpServer()
        {
            if (_optionsService.Options.IsWebClockEnabled || _optionsService.Options.IsApiEnabled)
            {
                _httpServer.Start(_optionsService.Options.HttpServerPort);
            }
        }

        private void OnRequestForTimerData(object sender, TimerInfoEventArgs timerData)
        {
            // we received a web request for the timer clock info...
            var info = _timerService.GetClockRequestInfo();

            timerData.Use24HrFormat = _optionsService.Use24HrClockFormat();

            if (info == null || !info.IsRunning)
            {
                timerData.Mode = ClockServerMode.TimeOfDay;
            }
            else
            {
                timerData.Mode = ClockServerMode.Timer;

                timerData.TargetSecs = info.TargetSeconds;
                timerData.Mins = (int)info.ElapsedTime.TotalMinutes;
                timerData.Secs = info.ElapsedTime.Seconds;
                timerData.Millisecs = info.ElapsedTime.Milliseconds;

                timerData.IsCountingUp = info.IsCountingUp;
            }
        }

        /// <summary>
        /// Responds to change in the application's "Always on top" option.
        /// </summary>
        /// <param name="message">AlwaysOnTopChangedMessage message.</param>
        private void OnAlwaysOnTopChanged(AlwaysOnTopChangedMessage message)
        {
            RaisePropertyChanged(nameof(AlwaysOnTop));
        }

        /// <summary>
        /// Responds to a change in timer monitor.
        /// </summary>
        /// <param name="message">TimerMonitorChangedMessage message.</param>
        private void OnTimerMonitorChanged(TimerMonitorChangedMessage message)
        {
            try
            {
                if (message.Change == MonitorChangeDescription.WindowToNone ||
                    message.Change == MonitorChangeDescription.WindowToMonitor)
                {
                    _timerOutputDisplayService.SaveWindowedPos();
                }

                switch (message.Change)
                {
                    case MonitorChangeDescription.MonitorToMonitor:
                        RelocateTimerWindow();
                        break;

                    case MonitorChangeDescription.WindowToMonitor:
                    case MonitorChangeDescription.NoneToMonitor:
                        _timerOutputDisplayService.OpenWindowInMonitor();
                        break;

                    case MonitorChangeDescription.MonitorToWindow:
                    case MonitorChangeDescription.NoneToWindow:
                        _timerOutputDisplayService.OpenWindowWindowed();
                        break;

                    case MonitorChangeDescription.WindowToNone:
                    case MonitorChangeDescription.MonitorToNone:
                        _timerOutputDisplayService.HideWindow();
                        break;

                    default:
                        throw new NotImplementedException();
                }

                if (CountDownActive)
                {
                    // ensure countdown remains topmost if running
                    _countdownDisplayService.Activate();
                }

                RaisePropertyChanged(nameof(AlwaysOnTop));
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not change monitor");
            }
        }

        /// <summary>
        /// Responds to a change in countdown monitor.
        /// </summary>
        /// <param name="message">CountdownMonitorChangedMessage message.</param>
        private void OnCountdownMonitorChanged(CountdownMonitorChangedMessage message)
        {
            try
            {
                if (message.Change == MonitorChangeDescription.WindowToNone ||
                    message.Change == MonitorChangeDescription.WindowToMonitor)
                {
                    _countdownDisplayService.SaveWindowedPos();
                }

                switch (message.Change)
                {
                    case MonitorChangeDescription.MonitorToMonitor:
                        _countdownDisplayService.RelocateWindow();
                        break;

                    case MonitorChangeDescription.WindowToMonitor:
                    case MonitorChangeDescription.NoneToMonitor:
                        _countdownDisplayService.OpenWindowInMonitor();
                        break;

                    case MonitorChangeDescription.MonitorToWindow:
                    case MonitorChangeDescription.NoneToWindow:
                        _countdownDisplayService.OpenWindowWindowed();
                        break;

                    case MonitorChangeDescription.WindowToNone:
                    case MonitorChangeDescription.MonitorToNone:
                        _countdownDisplayService.Hide();
                        break;

                    default:
                        throw new NotImplementedException();
                }

                RaisePropertyChanged(nameof(AlwaysOnTop));
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not change monitor");
            }
        }

        private async Task LaunchTimerWindowAsync()
        {
            if (!IsInDesignMode && _optionsService.CanDisplayTimerWindow)
            {
                // on launch we display the timer window after a short delay (for aesthetics only)
                await Task.Delay(1000).ConfigureAwait(true);

                DispatcherHelper.CheckBeginInvokeOnUI(OpenTimerWindow);
            }
        }

        private void InitHeartbeatTimer()
        {
            if (!IsInDesignMode)
            {
                _heartbeatTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };

                _heartbeatTimer.Tick += HeartbeatTimerTick;
                _heartbeatTimer.Start();
            }
        }

        private void HeartbeatTimerTick(object sender, EventArgs e)
        {
            _heartbeatTimer.Stop();
            try
            {
                ManageCountdownOnHeartbeat();
                ManageScheduleOnHeartbeat();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Error during heartbeat");
            }
            finally
            {
                _heartbeatTimer.Start();
            }
        }

        private void ManageScheduleOnHeartbeat()
        {
            if ((_dateTimeService.Now() - _lastRefreshedSchedule).Seconds > 10)
            {
                _lastRefreshedSchedule = _dateTimeService.Now();
                Messenger.Default.Send(new RefreshScheduleMessage());
            }
        }

        private void ManageCountdownOnHeartbeat()
        {
            if (_optionsService.CanDisplayCountdownWindow && 
                !CountDownActive && 
                !_countdownDisplayService.IsCountdownDone && 
                _countdownTimerTriggerService.IsInCountdownPeriod(out var secondsOffset))
            {
                StartCountdown(secondsOffset);
            }
        }

        private void OnStopCountdown(StopCountDownMessage message)
        {
            _countdownDisplayService.Stop();
        }
        
        /// <summary>
        /// Responds to the NavigateMessage and swaps out one page for another.
        /// </summary>
        /// <param name="message">NavigateMessage message.</param>
        private void OnNavigate(NavigateMessage message)
        {
            if (message.TargetPageName.Equals(SettingsPageViewModel.PageName))
            {
                // we only init the settings page when first used...
                InitSettingsPage();
            }

            CurrentPage = _pages[message.TargetPageName];
            CurrentPageName = message.TargetPageName;

            var page = (IPage)CurrentPage.DataContext;
            page.Activated(message.State);
        }

        /// <summary>
        /// If the timer window is open when we change the timer display then relocate it;
        /// otherwise open it
        /// </summary>
        private void RelocateTimerWindow()
        {
            if (_timerOutputDisplayService.IsWindowAvailable())
            {
                _timerOutputDisplayService.RelocateWindow();
            }
            else
            {
                OpenTimerWindow();
            }
        }

        private void OpenTimerWindow()
        {
            try
            {
                if (_optionsService.Options.MainMonitorIsWindowed)
                {
                    _timerOutputDisplayService.OpenWindowWindowed();                    
                }
                else
                {
                    _timerOutputDisplayService.OpenWindowInMonitor();
                    RaisePropertyChanged(nameof(AlwaysOnTop));
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not open timer window");
            }
        }

        private void CloseTimerWindow()
        {
            try
            {
                _timerOutputDisplayService.Close();
                RaisePropertyChanged(nameof(AlwaysOnTop));
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not close timer window");
            }
        }

        /// <summary>
        /// Starts the countdown (pre-meeting) timer
        /// </summary>
        /// <param name="offsetSeconds">
        /// The offset in seconds (the timer already started offsetSeconds ago).
        /// </param>
        private void StartCountdown(int offsetSeconds)
        {
            if (!IsInDesignMode && _optionsService.CanDisplayCountdownWindow)
            {
                Log.Logger.Information("Launching countdown timer");

                _countdownDisplayService.Start(offsetSeconds);

                bool launched = _optionsService.Options.CountdownMonitorIsWindowed 
                    ? _countdownDisplayService.OpenWindowWindowed() 
                    : _countdownDisplayService.OpenWindowInMonitor();

                if (launched)
                {
                    Task.Delay(1000).ContinueWith(t =>
                    {
                        if (CountdownAndTimerShareSameMonitor())
                        {
                            // timer monitor and countdown monitor are the same.

                            // hide the timer window after a short delay (so that it doesn't appear 
                            // as another top-level window during alt-TAB)...
                            DispatcherHelper.CheckBeginInvokeOnUI(_timerOutputDisplayService.HideWindow);
                        }
                    });
                }
            }
        }

        private bool CountdownAndTimerShareSameMonitor()
        {
            if (_optionsService.Options.MainMonitorIsWindowed || _optionsService.Options.CountdownMonitorIsWindowed)
            {
                return false;
            }

            return _optionsService.Options.TimerMonitorId == _optionsService.Options.CountdownMonitorId;
        }

        private bool ForceSoftwareRendering()
        {
            // https://blogs.msdn.microsoft.com/jgoldb/2010/06/22/software-rendering-usage-in-wpf/
            // renderingTier values:
            // 0 => No graphics hardware acceleration available for the application on the device
            //      and DirectX version level is less than version 7.0
            // 1 => Partial graphics hardware acceleration available on the video card. This 
            //      corresponds to a DirectX version that is greater than or equal to 7.0 and 
            //      less than 9.0.
            // 2 => A rendering tier value of 2 means that most of the graphics features of WPF 
            //      should use hardware acceleration provided the necessary system resources have 
            //      not been exhausted. This corresponds to a DirectX version that is greater 
            //      than or equal to 9.0.
            int renderingTier = RenderCapability.Tier >> 16;
            return renderingTier == 0;
        }
    }
}