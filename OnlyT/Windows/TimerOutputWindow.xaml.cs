﻿using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using OnlyT.Animations;
using OnlyT.AnalogueClock;
using OnlyT.Common.Services.DateTime;
using OnlyT.Services.CommandLine;
using OnlyT.Services.Options;
using OnlyT.Utils;
using OnlyT.ViewModel;
using OnlyT.ViewModel.Messages;

namespace OnlyT.Windows
{
    /// <summary>
    /// Interaction logic for TimerOutputWindow.xaml
    /// </summary>
    public partial class TimerOutputWindow : Window
    {
        private const double DefWindowWidth = 700;
        private const double DefWindowHeight = 500;

        private readonly DispatcherTimer _persistTimer = new(DispatcherPriority.ApplicationIdle);
        private readonly IOptionsService _optionsService;
        private readonly IDateTimeService _dateTimeService;
        private readonly ICommandLineService _commandLineService;
        private bool _persistingTalkDuration;

        public TimerOutputWindow(
            IOptionsService optionsService, 
            IDateTimeService dateTimeService,
            ICommandLineService commandLineService)
        {
            InitializeComponent();

#pragma warning disable CA1416
            if (commandLineService.IsTimerNdi)
            {
                NdiSender.IsSendPaused = false;
                NdiSender.NdiName = "OnlyT"; // required to trigger initilisation of NDI
            }
#pragma warning restore CA1416

            _optionsService = optionsService;
            _dateTimeService = dateTimeService;
            _commandLineService = commandLineService;

            WeakReferenceMessenger.Default.Register<TimerStartMessage>(this, OnTimerStarted);
            WeakReferenceMessenger.Default.Register<TimerStopMessage>(this, OnTimerStopped);
            WeakReferenceMessenger.Default.Register<NavigateMessage>(this, OnNavigate);
            WeakReferenceMessenger.Default.Register<ZoomEventMessage>(this, OnZoomEvent);

            _persistTimer.Tick += HandlePersistTimerTick;
        }

        public void AdjustWindowPositionAndSize()
        {
            if (_commandLineService.IsTimerNdi)
            {
                Height = TimerOutputWindowViewModel.NdiPixelHeight;
                Width = TimerOutputWindowViewModel.NdiPixelWidth;
            }
            else if (!string.IsNullOrEmpty(_optionsService.Options.TimerOutputWindowPlacement))
            {
                this.SetPlacement(_optionsService.Options.TimerOutputWindowPlacement);
                SetWindowSize();
            }
            else
            {
                Left = 10;
                Top = 10;
                Width = DefWindowWidth;
                Height = DefWindowHeight;
            }
        }

        public void SaveWindowPos()
        {
            if (!_commandLineService.IsTimerNdi)
            {
                _optionsService.Options.TimerOutputWindowPlacement = this.GetPlacement();
                _optionsService.Options.TimerWindowSize = new Size(Width, Height);
                _optionsService.Save();
            }
        }

        private void HandlePersistTimerTick(object? sender, System.EventArgs e)
        {
            if (_persistingTalkDuration)
            {
                _persistingTalkDuration = false;
                AdjustDisplayOnTimerStop();
                _persistTimer.Stop();
            }
        }

        private void OnNavigate(object recipient, NavigateMessage message)
        {
            if (message.TargetPageName.Equals(SettingsPageViewModel.PageName))
            {
                // when the settings page is displayed we ensure that the 
                // display is split so that we can easily adjust the split 
                // position...
                var model = (TimerOutputWindowViewModel)DataContext;
                model.TimeString = TimeFormatter.FormatTimerDisplayString(0);
                DisplaySplitScreen();
            }
            else if (message.OriginalPageName.Equals(SettingsPageViewModel.PageName))
            {
                // restore to full screen time of day...
                DisplayFullScreenTimeOfDay();
            }
        }

        private void OnZoomEvent(object recipient, ZoomEventMessage message)
        {
            // This event is raised by the HTTP thread, ensure we are synchronized
            if (!ZoomPanel_Alert.Dispatcher.CheckAccess())
            {
                ZoomPanel_Alert.Dispatcher.Invoke((Action)delegate () {
                    OnZoomEvent(recipient, message);
                });
                return;
            }

            var sb = new Storyboard();

            var fadeZoom = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(300));
            fadeZoom.AutoReverse = true;
            fadeZoom.BeginTime = TimeSpan.Zero;
            fadeZoom.RepeatBehavior = new RepeatBehavior(TimeSpan.FromSeconds(3.0));

            Storyboard.SetTarget(fadeZoom, ZoomPanel_Alert);
            Storyboard.SetTargetProperty(fadeZoom, new PropertyPath(OpacityProperty));

            var fadeFullZoom = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(300));
            fadeFullZoom.BeginTime = TimeSpan.FromSeconds(3.0);
            
            Storyboard.SetTarget(fadeFullZoom, ZoomPanel_Alert);
            Storyboard.SetTargetProperty(fadeFullZoom , new PropertyPath(OpacityProperty));

            var fadeOutZoom = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400));
            fadeOutZoom.BeginTime = TimeSpan.FromSeconds(10.0f);

            Storyboard.SetTarget(fadeOutZoom, ZoomPanel_Alert);
            Storyboard.SetTargetProperty(fadeOutZoom, new PropertyPath(OpacityProperty));

            sb.Children.Add(fadeZoom);
            sb.Children.Add(fadeFullZoom);
            sb.Children.Add(fadeOutZoom);

            sb.Begin(this);
        }

        private void OnTimerStopped(object recipient, TimerStopMessage msg)
        {
            if (msg.PersistFinalTimerValue)
            {
                _persistingTalkDuration = true;
                _persistTimer.Interval = TimeSpan.FromSeconds(_optionsService.Options.PersistDurationSecs);
                _persistTimer.Start();
            }
            else
            {
                AdjustDisplayOnTimerStop();
            }
        }

        private void AdjustDisplayOnTimerStop()
        {
            var model = (TimerOutputWindowViewModel)DataContext;
            if (!model.SplitAndFullScreenModeIdentical())
            {
                // only animate if the user has configured different display
                // layout for timer mode and full-screen mode
                DisplayFullScreenTimeOfDay();
            }
        }

        private void DisplayFullScreenTimeOfDay()
        {
            var sb = new Storyboard();

            // fade out timer...
            var fadeOutTimer = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400));
            Storyboard.SetTarget(fadeOutTimer, TimerPanel);
            Storyboard.SetTargetProperty(fadeOutTimer, new PropertyPath(OpacityProperty));
            fadeOutTimer.BeginTime = TimeSpan.Zero;

            // fade out clock...
            var fadeOutClock = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400));
            Storyboard.SetTarget(fadeOutClock, ClockPanel);
            Storyboard.SetTargetProperty(fadeOutClock, new PropertyPath(OpacityProperty));
            fadeOutClock.BeginTime = TimeSpan.Zero;

            // fade out zoom...
            var fadeOutZoom = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400));
            Storyboard.SetTarget(fadeOutZoom, ZoomPanel);
            Storyboard.SetTargetProperty(fadeOutZoom, new PropertyPath(OpacityProperty));
            fadeOutZoom.BeginTime = TimeSpan.Zero;

            GridLengthAnimation? rowHeightAdjust1 = null;
            GridLengthAnimation? rowHeightAdjust2 = null;

            var model = (TimerOutputWindowViewModel)DataContext;
            switch (model.FullScreenClockMode)
            {
                case FullScreenClockMode.Analogue:
                    rowHeightAdjust1 = new GridLengthAnimation
                    {
                        From = new GridLength(100, GridUnitType.Star),
                        To = new GridLength(100, GridUnitType.Star),
                        BeginTime = TimeSpan.FromMilliseconds(500)
                    };
                    Storyboard.SetTarget(rowHeightAdjust1, ClockGrid.RowDefinitions[0]);
                    Storyboard.SetTargetProperty(rowHeightAdjust1, new PropertyPath(RowDefinition.HeightProperty));

                    rowHeightAdjust2 = new GridLengthAnimation
                    {
                        From = new GridLength(0, GridUnitType.Star),
                        To = new GridLength(0, GridUnitType.Star),
                        BeginTime = TimeSpan.FromMilliseconds(500)
                    };
                    Storyboard.SetTarget(rowHeightAdjust2, ClockGrid.RowDefinitions[1]);
                    Storyboard.SetTargetProperty(rowHeightAdjust2, new PropertyPath(RowDefinition.HeightProperty));
                    break;

                case FullScreenClockMode.Digital:
                    rowHeightAdjust1 = new GridLengthAnimation
                    {
                        From = new GridLength(0, GridUnitType.Star),
                        To = new GridLength(0, GridUnitType.Star),
                        BeginTime = TimeSpan.FromMilliseconds(500)
                    };
                    Storyboard.SetTarget(rowHeightAdjust1, ClockGrid.RowDefinitions[0]);
                    Storyboard.SetTargetProperty(rowHeightAdjust1, new PropertyPath(RowDefinition.HeightProperty));

                    rowHeightAdjust2 = new GridLengthAnimation
                    {
                        From = new GridLength(100, GridUnitType.Star),
                        To = new GridLength(100, GridUnitType.Star),
                        BeginTime = TimeSpan.FromMilliseconds(500)
                    };
                    Storyboard.SetTarget(rowHeightAdjust2, ClockGrid.RowDefinitions[1]);
                    Storyboard.SetTargetProperty(rowHeightAdjust2, new PropertyPath(RowDefinition.HeightProperty));
                    break;

                default:
                    break;
            }

            // change clock panel to use colspan 2...
            var changeColSpan = new Int32Animation(1, 2, TimeSpan.Zero);
            Storyboard.SetTarget(changeColSpan, ClockPanel);
            Storyboard.SetTargetProperty(changeColSpan, new PropertyPath(Grid.ColumnSpanProperty));
            changeColSpan.BeginTime = TimeSpan.FromMilliseconds(500);

            // fade in the clock panel again...
            var fadeInClock = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(400));
            Storyboard.SetTarget(fadeInClock, ClockPanel);
            Storyboard.SetTargetProperty(fadeInClock, new PropertyPath(OpacityProperty));
            fadeInClock.BeginTime = TimeSpan.FromMilliseconds(1000);

            sb.Children.Add(fadeOutTimer);
            sb.Children.Add(fadeOutClock);
            sb.Children.Add(fadeOutZoom);

            if (rowHeightAdjust1 != null)
            {
                sb.Children.Add(rowHeightAdjust1);
            }

            if (rowHeightAdjust2 != null)
            {
                sb.Children.Add(rowHeightAdjust2);
            }

            sb.Children.Add(changeColSpan);
            sb.Children.Add(fadeInClock);

            sb.Begin();
        }

        private void OnTimerStarted(object recipient, TimerStartMessage msg)
        {
            var model = (TimerOutputWindowViewModel)DataContext;
            if (!model.SplitAndFullScreenModeIdentical() && !_persistingTalkDuration)
            {
                model.TextColor = GreenYellowRedSelector.GetGreenBrush();
                
                // only animate if the user has configured different display
                // layout for timer mode and full-screen mode
                DisplaySplitScreen();
            }

            _persistingTalkDuration = false;
        }

        private void DisplaySplitScreen()
        {
            var sb = new Storyboard();

            // fade out clock panel...
            var fadeOutClock = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400));
            Storyboard.SetTarget(fadeOutClock, ClockPanel);
            Storyboard.SetTargetProperty(fadeOutClock, new PropertyPath(OpacityProperty));
            fadeOutClock.BeginTime = TimeSpan.Zero;

            // row heights...
            var rowHeightAdjust1 = new GridLengthAnimation
            {
                From = new GridLength(75, GridUnitType.Star),
                To = new GridLength(75, GridUnitType.Star),
                BeginTime = TimeSpan.FromMilliseconds(500)
            };

            Storyboard.SetTarget(rowHeightAdjust1, ClockGrid.RowDefinitions[0]);
            Storyboard.SetTargetProperty(rowHeightAdjust1, new PropertyPath(RowDefinition.HeightProperty));

            var rowHeightAdjust2 = new GridLengthAnimation
            {
                From = new GridLength(25, GridUnitType.Star),
                To = new GridLength(25, GridUnitType.Star),
                BeginTime = TimeSpan.FromMilliseconds(500)
            };

            Storyboard.SetTarget(rowHeightAdjust2, ClockGrid.RowDefinitions[1]);
            Storyboard.SetTargetProperty(rowHeightAdjust2, new PropertyPath(RowDefinition.HeightProperty));

            // restrict clock panel to column 0...
            var changeColSpan = new Int32Animation(2, 1, TimeSpan.Zero);
            Storyboard.SetTarget(changeColSpan, ClockPanel);
            Storyboard.SetTargetProperty(changeColSpan, new PropertyPath(Grid.ColumnSpanProperty));
            changeColSpan.BeginTime = TimeSpan.FromMilliseconds(500);

            // fade in the clock panel again...
            var fadeInClock = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(400));
            Storyboard.SetTarget(fadeInClock, ClockPanel);
            Storyboard.SetTargetProperty(fadeInClock, new PropertyPath(OpacityProperty));
            fadeInClock.BeginTime = TimeSpan.FromMilliseconds(1000);

            // and fade in the timer...
            var fadeInTimer = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(400));
            Storyboard.SetTarget(fadeInTimer, TimerPanel);
            Storyboard.SetTargetProperty(fadeInTimer, new PropertyPath(OpacityProperty));
            fadeInTimer.BeginTime = TimeSpan.FromMilliseconds(1000);

            // and fade in the zoom event panel
            var fadeInZoom = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(400));
            Storyboard.SetTarget(fadeInZoom, ZoomPanel);
            Storyboard.SetTargetProperty(fadeInZoom, new PropertyPath(OpacityProperty));
            fadeInZoom.BeginTime = TimeSpan.FromMilliseconds(1000);

            sb.Children.Add(fadeOutClock);
            sb.Children.Add(rowHeightAdjust1);
            sb.Children.Add(rowHeightAdjust2);
            sb.Children.Add(changeColSpan);
            sb.Children.Add(fadeInClock);
            sb.Children.Add(fadeInTimer);
            sb.Children.Add(fadeInZoom);

            sb.Begin();
        }

        private void InitFullScreenMode()
        {
            var model = (TimerOutputWindowViewModel)DataContext;
            switch (model.FullScreenClockMode)
            {
                case FullScreenClockMode.Analogue:
                    ClockGrid.RowDefinitions[0].Height = new GridLength(100, GridUnitType.Star);
                    ClockGrid.RowDefinitions[1].Height = new GridLength(0, GridUnitType.Star);
                    break;

                case FullScreenClockMode.Digital:
                    ClockGrid.RowDefinitions[0].Height = new GridLength(0, GridUnitType.Star);
                    ClockGrid.RowDefinitions[1].Height = new GridLength(100, GridUnitType.Star);
                    break;

                case FullScreenClockMode.AnalogueAndDigital:
                    ClockGrid.RowDefinitions[0].Height = new GridLength(75, GridUnitType.Star);
                    ClockGrid.RowDefinitions[1].Height = new GridLength(25, GridUnitType.Star);
                    break;

                default:
                    throw new NotSupportedException();
            }
        }

        private void WindowLoaded(object sender, RoutedEventArgs e)
        {
            InitFullScreenMode();
            TheClock.IsRunning = true;
        }

        private void WindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var model = (TimerOutputWindowViewModel)DataContext;
            if (!model.ApplicationClosing)
            {
                // prevent window from being closed independently of application.
                e.Cancel = true;
            }

            if (model.WindowedOperation)
            {
                SaveWindowPos();
            }
        }

        private void ClockQueryDateTimeEvent(object sender, DateTimeQueryEventArgs e)
        {
            e.DateTime = _dateTimeService.Now();
        }

        private void SetWindowSize()
        {
            var sz = _optionsService.Options.TimerWindowSize;
            if (sz != default)
            {
                Width = sz.Width;
                Height = sz.Height;
            }
            else
            {
                Width = DefWindowWidth;
                Height = DefWindowHeight;
            }
        }

        private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var isWindowed = ((TimerOutputWindowViewModel)DataContext).WindowedOperation;

            // allow drag when no title bar is shown
            if (isWindowed && e.ChangedButton == MouseButton.Left && WindowStyle == WindowStyle.None)
            {
                DragMove();
            }
        }
    }
}
