using Files.Common;
using Files.DataModels.NavigationControlItems;
using Files.Filesystem;
using Files.Helpers;
using Files.UserControls;
using Files.Views;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Windows.UI.Core;
using Windows.UI.Xaml;
namespace Files.ViewModels
{
    public class SidebarViewModel : ObservableObject, IDisposable
    {
        public static async System.Threading.Tasks.Task<IEnumerable<IconFileInfo>> LoadSidebarIconResources()
        {
            const string imageres = @"C:\Windows\System32\imageres.dll";
            var imageResList = await UIHelpers.LoadSelectedIconsAsync(imageres, new List<int>() {
                    Constants.ImageRes.RecycleBin,
                    Constants.ImageRes.NetworkDrives,
                    Constants.ImageRes.Libraries,
                    Constants.ImageRes.ThisPC,
                    Constants.ImageRes.CloudDrives,
                    Constants.ImageRes.Folder
                }, 32, false);

            const string shell32 = @"C:\Windows\System32\shell32.dll";
            var shell32List = await UIHelpers.LoadSelectedIconsAsync(shell32, new List<int>() {
                    Constants.Shell32.QuickAccess
                }, 32, false);

            if (shell32List != null && imageResList != null)
            {
                return imageResList.Concat(shell32List);
            }
            else if (shell32List != null && imageResList == null)
            {
                return shell32List;
            }
            else if (shell32List == null && imageResList != null)
            {
                return imageResList;
            }
            else
            {
                return null;
            }
        }

        public ICommand EmptyRecycleBinCommand { get; private set; }

        private IPaneHolder paneHolder;
        public IPaneHolder PaneHolder
        {
            get => paneHolder;
            set => SetProperty(ref paneHolder, value);
        }

        public IFilesystemHelpers FilesystemHelpers => PaneHolder?.FilesystemHelpers;

        public static readonly GridLength CompactSidebarWidth = SidebarControl.GetSidebarCompactSize();

        private NavigationViewDisplayMode sidebarDisplayMode;

        public NavigationViewDisplayMode SidebarDisplayMode
        {
            get => sidebarDisplayMode;
            set
            {
                if (SetProperty(ref sidebarDisplayMode, value))
                {
                    OnPropertyChanged(nameof(IsSidebarCompactSize));
                    UpdateTabControlMargin();
                }
            }
        }

        public bool IsSidebarCompactSize => SidebarDisplayMode == NavigationViewDisplayMode.Compact || SidebarDisplayMode == NavigationViewDisplayMode.Minimal;

        public void NotifyInstanceRelatedPropertiesChanged(string arg)
        {
            UpdateSidebarSelectedItemFromArgs(arg);

            OnPropertyChanged(nameof(SidebarSelectedItem));
        }

        public void UpdateSidebarSelectedItemFromArgs(string arg)
        {
            var value = arg;

            INavigationControlItem item = null;
            List<INavigationControlItem> sidebarItems = UserControls.SidebarControl.SideBarItems
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .Concat(UserControls.SidebarControl.SideBarItems.Where(x => (x as LocationItem)?.ChildItems != null).SelectMany(x => (x as LocationItem).ChildItems).Where(x => !string.IsNullOrWhiteSpace(x.Path)))
                .ToList();

            if (string.IsNullOrEmpty(value))
            {
                //SidebarSelectedItem = sidebarItems.FirstOrDefault(x => x.Path.Equals("Home"));
                return;
            }

            item = sidebarItems.FirstOrDefault(x => x.Path.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                item = sidebarItems.FirstOrDefault(x => x.Path.Equals(value + "\\", StringComparison.OrdinalIgnoreCase));
            }
            if (item == null)
            {
                item = sidebarItems.FirstOrDefault(x => value.StartsWith(x.Path, StringComparison.OrdinalIgnoreCase));
            }
            if (item == null)
            {
                item = sidebarItems.FirstOrDefault(x => x.Path.Equals(Path.GetPathRoot(value), StringComparison.OrdinalIgnoreCase));
            }
            if (item == null)
            {
                if (value == "NewTab".GetLocalized())
                {
                    item = sidebarItems.FirstOrDefault(x => x.Path.Equals("Home"));
                }
            }

            if (SidebarSelectedItem != item)
            {
                SidebarSelectedItem = item;
            }
        }

        public bool IsSidebarOpen
        {
            get => App.AppSettings.IsSidebarOpen;
            set
            {
                if (App.AppSettings.IsSidebarOpen != value)
                {
                    App.AppSettings.IsSidebarOpen = value;
                    OnPropertyChanged(nameof(IsSidebarOpen));
                }
            }
        }

        private INavigationControlItem selectedSidebarItem;

        public INavigationControlItem SidebarSelectedItem
        {
            get => selectedSidebarItem;
            set => SetProperty(ref selectedSidebarItem, value);
        }

        public SidebarViewModel()
        {
            EmptyRecycleBinCommand = new RelayCommand<RoutedEventArgs>(EmptyRecycleBin);
            App.AppSettings.PropertyChanged += AppSettings_PropertyChanged;
        }

        public async void EmptyRecycleBin(RoutedEventArgs e)
        {
            await RecycleBinHelpers.S_EmptyRecycleBin();
        }

        private void AppSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(App.AppSettings.IsSidebarOpen):
                    if (App.AppSettings.IsSidebarOpen != IsSidebarOpen)
                    {
                        OnPropertyChanged(nameof(IsSidebarOpen));
                    }
                    break;
            }
        }

        public void Dispose()
        {
            App.AppSettings.PropertyChanged -= AppSettings_PropertyChanged;
        }

        public void SidebarControl_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        {
            SidebarDisplayMode = args.DisplayMode;
        }


        public void UpdateTabControlMargin()
        {
            TabControlMargin = SidebarDisplayMode switch
            {
                // This prevents the pane toggle button from overlapping the tab control in minimal mode
                NavigationViewDisplayMode.Minimal => new GridLength(44, GridUnitType.Pixel),
                _ => new GridLength(0, GridUnitType.Pixel),
            };
        }

        private GridLength tabControlMargin;
        public GridLength TabControlMargin
        {
            get => tabControlMargin;
            set => SetProperty(ref tabControlMargin, value);
        }
    }
}