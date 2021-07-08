using Files.Common;
using Files.Filesystem;
using Files.Helpers;
using Files.UserControls.MultitaskingControl;
using Files.Views;
using Microsoft.AppCenter;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Files.ViewModels
{
    public class MainPageViewModel : ObservableObject
    {
        public IMultitaskingControl MultitaskingControl { get; set; }
        public List<IMultitaskingControl> MultitaskingControls { get; } = new List<IMultitaskingControl>();

        public static ObservableCollection<TabItem> AppInstances { get; private set; } = new ObservableCollection<TabItem>();

        private TabItem selectedTabItem;

        public TabItem SelectedTabItem
        {
            get => selectedTabItem;
            set => SetProperty(ref selectedTabItem, value);
        }

        public ICommand NavigateToNumberedTabKeyboardAcceleratorCommand { get; private set; }

        public ICommand OpenNewWindowAcceleratorCommand { get; private set; }

        public ICommand CloseSelectedTabKeyboardAcceleratorCommand { get; private set; }

        public ICommand AddNewInstanceAcceleratorCommand { get; private set; }

        public MainPageViewModel()
        {
            // Create commands
            NavigateToNumberedTabKeyboardAcceleratorCommand = new RelayCommand<KeyboardAcceleratorInvokedEventArgs>(NavigateToNumberedTabKeyboardAccelerator);
            OpenNewWindowAcceleratorCommand = new RelayCommand<KeyboardAcceleratorInvokedEventArgs>(OpenNewWindowAccelerator);
            CloseSelectedTabKeyboardAcceleratorCommand = new RelayCommand<KeyboardAcceleratorInvokedEventArgs>(CloseSelectedTabKeyboardAccelerator);
            AddNewInstanceAcceleratorCommand = new RelayCommand<KeyboardAcceleratorInvokedEventArgs>(AddNewInstanceAccelerator);

            StartAppCenter();
        }

        private void NavigateToNumberedTabKeyboardAccelerator(KeyboardAcceleratorInvokedEventArgs e)
        {
            int indexToSelect = 0;

            switch (e.KeyboardAccelerator.Key)
            {
                case VirtualKey.Number1:
                    indexToSelect = 0;
                    break;

                case VirtualKey.Number2:
                    indexToSelect = 1;
                    break;

                case VirtualKey.Number3:
                    indexToSelect = 2;
                    break;

                case VirtualKey.Number4:
                    indexToSelect = 3;
                    break;

                case VirtualKey.Number5:
                    indexToSelect = 4;
                    break;

                case VirtualKey.Number6:
                    indexToSelect = 5;
                    break;

                case VirtualKey.Number7:
                    indexToSelect = 6;
                    break;

                case VirtualKey.Number8:
                    indexToSelect = 7;
                    break;

                case VirtualKey.Number9:
                    // Select the last tab
                    indexToSelect = AppInstances.Count - 1;
                    break;

                case VirtualKey.Tab:
                    bool shift = e.KeyboardAccelerator.Modifiers.HasFlag(VirtualKeyModifiers.Shift);

                    if (!shift) // ctrl + tab, select next tab
                    {
                        if ((App.MainViewModel.TabStripSelectedIndex + 1) < AppInstances.Count)
                        {
                            indexToSelect = App.MainViewModel.TabStripSelectedIndex + 1;
                        }
                        else
                        {
                            indexToSelect = 0;
                        }
                    }
                    else // ctrl + shift + tab, select previous tab
                    {
                        if ((App.MainViewModel.TabStripSelectedIndex - 1) >= 0)
                        {
                            indexToSelect = App.MainViewModel.TabStripSelectedIndex - 1;
                        }
                        else
                        {
                            indexToSelect = AppInstances.Count - 1;
                        }
                    }
                    
                    break;
            }

            // Only select the tab if it is in the list
            if (indexToSelect < AppInstances.Count)
            {
                App.MainViewModel.TabStripSelectedIndex = indexToSelect;
            }
            e.Handled = true;
        }

        private async void OpenNewWindowAccelerator(KeyboardAcceleratorInvokedEventArgs e)
        {
            e.Handled = true;
            Uri filesUWPUri = new Uri("files-uwp:");
            await Launcher.LaunchUriAsync(filesUWPUri);
        }

        private void CloseSelectedTabKeyboardAccelerator(KeyboardAcceleratorInvokedEventArgs e)
        {
            if (App.MainViewModel.TabStripSelectedIndex >= AppInstances.Count)
            {
                TabItem tabItem = AppInstances[AppInstances.Count - 1];
                MultitaskingControl?.CloseTab(tabItem);
            }
            else
            {
                TabItem tabItem = AppInstances[App.MainViewModel.TabStripSelectedIndex];
                MultitaskingControl?.CloseTab(tabItem);
            }
            e.Handled = true;
        }

        private async void AddNewInstanceAccelerator(KeyboardAcceleratorInvokedEventArgs e)
        {
            bool shift = e.KeyboardAccelerator.Modifiers.HasFlag(VirtualKeyModifiers.Shift);

            if (!shift)
            {
                await AddNewTabAsync();
            }
            else // ctrl + shift + t, restore recently closed tab
            {
                ((BaseMultitaskingControl)MultitaskingControl).ReopenClosedTab(null, null);
            }
            e.Handled = true;
        }

        public static async Task AddNewTabByPathAsync(Type type, string path, int atIndex = -1)
        {
            Microsoft.UI.Xaml.Controls.FontIconSource fontIconSource = new Microsoft.UI.Xaml.Controls.FontIconSource();
            fontIconSource.FontFamily = App.MainViewModel.FontName;

            if (string.IsNullOrEmpty(path))
            {
                path = "NewTab".GetLocalized();
            }

            TabItem tabItem = new TabItem()
            {
                Header = null,
                IconSource = fontIconSource,
                Description = null
            };
            tabItem.Control.NavigationArguments = new TabItemArguments()
            {
                InitialPageType = type,
                NavigationArg = path
            };
            tabItem.Control.ContentChanged += Control_ContentChanged;
            await UpdateTabInfo(tabItem, path);
            AppInstances.Insert(atIndex == -1 ? AppInstances.Count : atIndex, tabItem);
        }

        public static async Task UpdateTabInfo(TabItem tabItem, object navigationArg)
        {
            tabItem.AllowStorageItemDrop = true;
            if (navigationArg is PaneNavigationArguments paneArgs)
            {
                if (!string.IsNullOrEmpty(paneArgs.LeftPaneNavPathParam) && !string.IsNullOrEmpty(paneArgs.RightPaneNavPathParam))
                {
                    var leftTabInfo = await GetSelectedTabInfoAsync(paneArgs.LeftPaneNavPathParam);
                    var rightTabInfo = await GetSelectedTabInfoAsync(paneArgs.RightPaneNavPathParam);
                    tabItem.Header = $"{leftTabInfo.tabLocationHeader} | {rightTabInfo.tabLocationHeader}";
                    tabItem.IconSource = leftTabInfo.tabIcon;
                }
                else
                {
                    (tabItem.Header, tabItem.IconSource) = await GetSelectedTabInfoAsync(paneArgs.LeftPaneNavPathParam);
                }
            }
            else if (navigationArg is string pathArgs)
            {
                (tabItem.Header, tabItem.IconSource) = await GetSelectedTabInfoAsync(pathArgs);
            }
        }

        public static async Task<(string tabLocationHeader, Microsoft.UI.Xaml.Controls.IconSource tabIcon)> GetSelectedTabInfoAsync(string currentPath)
        {
            string tabLocationHeader;
            Microsoft.UI.Xaml.Controls.FontIconSource fontIconSource = new Microsoft.UI.Xaml.Controls.FontIconSource();
            fontIconSource.FontFamily = App.MainViewModel.FontName;

            if (currentPath == null || currentPath == "SidebarSettings/Text".GetLocalized())
            {
                tabLocationHeader = "SidebarSettings/Text".GetLocalized();
                fontIconSource.Glyph = "\xE713";
            }
            else if (currentPath == null || currentPath == "NewTab".GetLocalized() || currentPath == "Home")
            {
                tabLocationHeader = "NewTab".GetLocalized();
                fontIconSource.Glyph = "\xE8A1";
            }
            else if (currentPath.Equals(App.AppSettings.DesktopPath, StringComparison.OrdinalIgnoreCase))
            {
                tabLocationHeader = "SidebarDesktop".GetLocalized();
                fontIconSource.Glyph = "\xE8FC";
            }
            else if (currentPath.Equals(App.AppSettings.DownloadsPath, StringComparison.OrdinalIgnoreCase))
            {
                tabLocationHeader = "SidebarDownloads".GetLocalized();
                fontIconSource.Glyph = "\xE896";
            }
            else if (currentPath.Equals(App.AppSettings.RecycleBinPath, StringComparison.OrdinalIgnoreCase))
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                tabLocationHeader = localSettings.Values.Get("RecycleBin_Title", "Recycle Bin");
                fontIconSource.FontFamily = Application.Current.Resources["RecycleBinIcons"] as FontFamily;
                fontIconSource.Glyph = "\xEF87";
            }
            else if (currentPath.Equals(App.AppSettings.NetworkFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                tabLocationHeader = "SidebarNetworkDrives".GetLocalized();
                fontIconSource.Glyph = "\uE8CE";
            }
            else if (App.LibraryManager.TryGetLibrary(currentPath, out LibraryLocationItem library))
            {
                var libName = System.IO.Path.GetFileNameWithoutExtension(library.Path);
                switch (libName)
                {
                    case "Documents":
                    case "Pictures":
                    case "Music":
                    case "Videos":
                        // Show localized name
                        tabLocationHeader = $"Sidebar{libName}".GetLocalized();
                        break;

                    default:
                        // Show original name
                        tabLocationHeader = library.Text;
                        break;
                }
            }
            else
            {
                var matchingCloudDrive = App.CloudDrivesManager.Drives.FirstOrDefault(x => PathNormalization.NormalizePath(currentPath).Equals(PathNormalization.NormalizePath(x.Path), StringComparison.OrdinalIgnoreCase));
                if (matchingCloudDrive != null)
                {
                    fontIconSource.Glyph = "\xE753";
                    tabLocationHeader = matchingCloudDrive.Text;
                }
                else if (PathNormalization.NormalizePath(PathNormalization.GetPathRoot(currentPath)) == PathNormalization.NormalizePath(currentPath)) // If path is a drive's root
                {
                    var matchingNetDrive = App.NetworkDrivesManager.Drives.FirstOrDefault(x => PathNormalization.NormalizePath(currentPath).Contains(PathNormalization.NormalizePath(x.Path), StringComparison.OrdinalIgnoreCase));
                    if (matchingNetDrive != null)
                    {
                        fontIconSource.Glyph = "\uE8CE";
                        tabLocationHeader = matchingNetDrive.Text;
                    }
                    else
                    {
                        try
                        {
                            List<System.IO.DriveInfo> drives = System.IO.DriveInfo.GetDrives().ToList();
                            System.IO.DriveInfo matchingDrive = drives.FirstOrDefault(x => PathNormalization.NormalizePath(currentPath).Contains(PathNormalization.NormalizePath(x.Name)));

                            if (matchingDrive != null)
                            {
                                // Go through types and set the icon according to type
                                string type = DriveHelpers.GetDriveTypeIcon(matchingDrive);
                                if (!string.IsNullOrWhiteSpace(type))
                                {
                                    fontIconSource.Glyph = type;
                                }
                                else
                                {
                                    fontIconSource.Glyph = "\xEDA2"; // Drive icon
                                }
                            }
                            else
                            {
                                fontIconSource.Glyph = "\xE74E"; // Floppy icon
                            }
                        }
                        catch (Exception)
                        {
                            fontIconSource.Glyph = "\xEDA2"; // Fallback
                        }

                        tabLocationHeader = PathNormalization.NormalizePath(currentPath);
                    }
                }
                else
                {
                    fontIconSource.Glyph = "\xE8B7"; // Folder icon
                    tabLocationHeader = currentPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).Split('\\', StringSplitOptions.RemoveEmptyEntries).Last();

                    FilesystemResult<IStorageFolder> rootItem = await FilesystemTasks.Wrap(() => DrivesManager.GetRootFromPathAsync(currentPath));
                    if (rootItem)
                    {
                        IStorageFolder currentFolder = (await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderFromPathAsync(currentPath, rootItem.Result))).Result;
                        if (currentFolder is StorageFolder folder && !string.IsNullOrEmpty(folder.DisplayName))
                        {
                            tabLocationHeader = folder.DisplayName;
                        }
                    }
                }
            }

            return (tabLocationHeader, fontIconSource);
        }

        public async void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.NavigationMode != NavigationMode.Back)
            {
                //Initialize the static theme helper to capture a reference to this window
                //to handle theme changes without restarting the app
                ThemeHelper.Initialize();

                if (e.Parameter == null || (e.Parameter is string eventStr && string.IsNullOrEmpty(eventStr)))
                {
                    try
                    {
                        if (App.AppSettings.ResumeAfterRestart)
                        {
                            App.AppSettings.ResumeAfterRestart = false;

                            foreach (string tabArgsString in App.AppSettings.LastSessionPages)
                            {
                                var tabArgs = TabItemArguments.Deserialize(tabArgsString);
                                await AddNewTabByParam(tabArgs.InitialPageType, tabArgs.NavigationArg);
                            }

                            if (!App.AppSettings.ContinueLastSessionOnStartUp)
                            {
                                App.AppSettings.LastSessionPages = null;
                            }
                        }
                        else if (App.AppSettings.OpenASpecificPageOnStartup)
                        {
                            if (App.AppSettings.PagesOnStartupList != null)
                            {
                                foreach (string path in App.AppSettings.PagesOnStartupList)
                                {
                                    await AddNewTabByPathAsync(typeof(PaneHolderPage), path);
                                }
                            }
                            else
                            {
                                await AddNewTabAsync();
                            }
                        }
                        else if (App.AppSettings.ContinueLastSessionOnStartUp)
                        {
                            if (App.AppSettings.LastSessionPages != null)
                            {
                                foreach (string tabArgsString in App.AppSettings.LastSessionPages)
                                {
                                    var tabArgs = TabItemArguments.Deserialize(tabArgsString);
                                    await AddNewTabByParam(tabArgs.InitialPageType, tabArgs.NavigationArg);
                                }
                                var defaultArg = new TabItemArguments() { InitialPageType = typeof(PaneHolderPage), NavigationArg = "NewTab".GetLocalized() };
                                App.AppSettings.LastSessionPages = new string[] { defaultArg.Serialize() };
                            }
                            else
                            {
                                await AddNewTabAsync();
                            }
                        }
                        else
                        {
                            await AddNewTabAsync();
                        }
                    }
                    catch (Exception)
                    {
                        await AddNewTabAsync();
                    }
                }
                else
                {
                    if (e.Parameter is string navArgs)
                    {
                        await AddNewTabByPathAsync(typeof(PaneHolderPage), navArgs);
                    }
                    else if (e.Parameter is TabItemArguments tabArgs)
                    {
                        await AddNewTabByParam(tabArgs.InitialPageType, tabArgs.NavigationArg);
                    }
                }

                // Check for required updates
                AppUpdater updater = new AppUpdater();
                updater.CheckForUpdatesAsync();

                // Initial setting of SelectedTabItem
                SelectedTabItem = AppInstances[App.MainViewModel.TabStripSelectedIndex];
            }
        }

        public static async Task AddNewTabAsync()
        {
            await AddNewTabByPathAsync(typeof(PaneHolderPage), "NewTab".GetLocalized());
            App.MainViewModel.TabStripSelectedIndex = AppInstances.Count - 1;
        }

        public async void AddNewTab()
        {
            await AddNewTabAsync();
        }

        public static async void AddNewTabAtIndex(object sender, RoutedEventArgs e)
        {
            await AddNewTabAsync();
        }

        public static async void DuplicateTabAtIndex(object sender, RoutedEventArgs e)
        {
            var tabItem = ((FrameworkElement)sender).DataContext as TabItem;
            var index = AppInstances.IndexOf(tabItem);

            if (AppInstances[index].TabItemArguments != null)
            {
                var tabArgs = AppInstances[index].TabItemArguments;
                await AddNewTabByParam(tabArgs.InitialPageType, tabArgs.NavigationArg, index + 1);
            }
            else
            {
                await AddNewTabByPathAsync(typeof(PaneHolderPage), "NewTab".GetLocalized());
            }
        }

        public static async Task AddNewTabByParam(Type type, object tabViewItemArgs, int atIndex = -1)
        {
            Microsoft.UI.Xaml.Controls.FontIconSource fontIconSource = new Microsoft.UI.Xaml.Controls.FontIconSource();
            fontIconSource.FontFamily = App.MainViewModel.FontName;

            TabItem tabItem = new TabItem()
            {
                Header = null,
                IconSource = fontIconSource,
                Description = null
            };
            tabItem.Control.NavigationArguments = new TabItemArguments()
            {
                InitialPageType = type,
                NavigationArg = tabViewItemArgs
            };
            tabItem.Control.ContentChanged += Control_ContentChanged;
            await UpdateTabInfo(tabItem, tabViewItemArgs);
            AppInstances.Insert(atIndex == -1 ? AppInstances.Count : atIndex, tabItem);
        }

        public static async void Control_ContentChanged(object sender, TabItemArguments e)
        {
            TabItem matchingTabItem = MainPageViewModel.AppInstances.SingleOrDefault(x => x.Control == sender);
            if (matchingTabItem == null)
            {
                return;
            }
            await UpdateTabInfo(matchingTabItem, e.NavigationArg);
        }

        private async void StartAppCenter()
        {
            JObject obj;
            try
            {
                StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(@"ms-appx:///Resources/AppCenterKey.txt"));
                var lines = await FileIO.ReadTextAsync(file);
                obj = JObject.Parse(lines);
            }
            catch
            {
                return;
            }

            AppCenter.Start((string)obj.SelectToken("key"), typeof(Analytics), typeof(Crashes));
        }
    }
}