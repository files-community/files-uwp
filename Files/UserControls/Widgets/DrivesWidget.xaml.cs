using Files.DataModels.NavigationControlItems;
using Files.Filesystem;
using Files.Helpers;
using Files.ViewModels;
using Files.ViewModels.Widgets;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Windows.Foundation.Collections;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Files.UserControls.Widgets
{
    public sealed partial class DrivesWidget : UserControl, IWidgetItemModel, INotifyPropertyChanged
    {
        public SettingsViewModel AppSettings => App.AppSettings;

        public delegate void DrivesWidgetInvokedEventHandler(object sender, DrivesWidgetInvokedEventArgs e);

        public event DrivesWidgetInvokedEventHandler DrivesWidgetInvoked;

        public delegate void DrivesWidgetNewPaneInvokedEventHandler(object sender, DrivesWidgetInvokedEventArgs e);

        public event DrivesWidgetNewPaneInvokedEventHandler DrivesWidgetNewPaneInvoked;

        public event PropertyChangedEventHandler PropertyChanged;

        public static ObservableCollection<INavigationControlItem> ItemsAdded = new ObservableCollection<INavigationControlItem>();

        private IShellPage associatedInstance;

        public IShellPage AppInstance
        {
            get => associatedInstance;
            set
            {
                if (value != associatedInstance)
                {
                    associatedInstance = value;
                    NotifyPropertyChanged(nameof(AppInstance));
                }
            }
        }

        public string WidgetName => nameof(DrivesWidget);

        public bool IsWidgetSettingEnabled => App.AppSettings.ShowDrivesWidget;

        public DrivesWidget()
        {
            InitializeComponent();
        }

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private async void EjectDevice_Click(object sender, RoutedEventArgs e)
        {
            var item = ((MenuFlyoutItem)sender).DataContext as DriveItem;
            await DriveHelpers.EjectDeviceAsync(item.Path);
        }

        private async void OpenInNewTab_Click(object sender, RoutedEventArgs e)
        {
            var item = ((MenuFlyoutItem)sender).DataContext as DriveItem;
            await NavigationHelpers.OpenPathInNewTab(item.Path);
        }

        private async void OpenInNewWindow_Click(object sender, RoutedEventArgs e)
        {
            var item = ((MenuFlyoutItem)sender).DataContext as DriveItem;
            await NavigationHelpers.OpenPathInNewWindowAsync(item.Path);
        }

        private async void OpenDriveProperties_Click(object sender, RoutedEventArgs e)
        {
            var item = ((MenuFlyoutItem)sender).DataContext as DriveItem;
            await FilePropertiesHelpers.OpenPropertiesWindowAsync(item, associatedInstance);
        }

        private async void Button_Click(object sender, RoutedEventArgs e)
        {
            string NavigationPath = ""; // path to navigate
            string ClickedCard = (sender as Button).Tag.ToString();

            NavigationPath = ClickedCard;

            var ctrlPressed = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            if (ctrlPressed)
            {
                await NavigationHelpers.OpenPathInNewTab(NavigationPath);
                return;
            }

            DrivesWidgetInvoked?.Invoke(this, new DrivesWidgetInvokedEventArgs()
            {
                Path = NavigationPath
            });
        }

        private async void Button_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed) // check middle click
            {
                string navigationPath = (sender as Button).Tag.ToString();
                await NavigationHelpers.OpenPathInNewTab(navigationPath);
            }
        }

        public class DrivesWidgetInvokedEventArgs : EventArgs
        {
            public string Path { get; set; }
        }

        public bool ShowMultiPaneControls
        {
            get => AppInstance.PaneHolder?.IsMultiPaneEnabled ?? false;
        }

        private void OpenInNewPane_Click(object sender, RoutedEventArgs e)
        {
            var item = ((MenuFlyoutItem)sender).DataContext as DriveItem;
            DrivesWidgetNewPaneInvoked?.Invoke(this, new DrivesWidgetInvokedEventArgs()
            {
                Path = item.Path
            });
        }

        private void MenuFlyout_Opening(object sender, object e)
        {
            var newPaneMenuItem = (sender as MenuFlyout).Items.Single(x => x.Name == "OpenInNewPane");
            newPaneMenuItem.Visibility = ShowMultiPaneControls ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void MapNetworkDrive_Click(object sender, RoutedEventArgs e)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection != null)
            {
                await connection.SendMessageAsync(new ValueSet()
                {
                    { "Arguments", "NetworkDriveOperation" },
                    { "netdriveop", "OpenMapNetworkDriveDialog" },
                    { "HWND", NativeWinApiHelper.CoreWindowHandle.ToInt64() }
                });
            }
        }

        private async void DisconnectNetworkDrive_Click(object sender, RoutedEventArgs e)
        {
            var item = ((MenuFlyoutItem)sender).DataContext as DriveItem;
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection != null)
            {
                await connection.SendMessageAsync(new ValueSet()
                {
                    { "Arguments", "NetworkDriveOperation" },
                    { "netdriveop", "DisconnectNetworkDrive" },
                    { "drive", item.Path }
                });
            }
        }

        private async void GoToStorageSense_Click(object sender, RoutedEventArgs e)
        {
            await Launcher.LaunchUriAsync(new Uri("ms-settings:storagesense"));
        }

        public void Dispose()
        {
        }
    }
}