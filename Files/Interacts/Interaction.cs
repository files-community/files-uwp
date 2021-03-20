using Files.Common;
using Files.DataModels;
using Files.Dialogs;
using Files.Enums;
using Files.Filesystem;
using Files.Helpers;
using Files.ViewModels;
using Files.Views;
using Microsoft.Toolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp;
using Microsoft.Toolkit.Uwp.Notifications;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Search;
using Windows.Storage.Streams;
using Windows.System;
using Windows.System.UserProfile;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.Popups;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using static Files.Views.Properties;

namespace Files.Interacts
{
    public class Interaction
    {
        private NamedPipeAsAppServiceConnection Connection => AssociatedInstance?.ServiceConnection;
        private string jumpString = "";
        private readonly DispatcherTimer jumpTimer = new DispatcherTimer();
        private readonly IShellPage AssociatedInstance;

        public SettingsViewModel AppSettings => App.AppSettings;
        public IFilesystemHelpers FilesystemHelpers => AssociatedInstance.FilesystemHelpers;
        public FolderSettingsViewModel FolderSettings => AssociatedInstance?.InstanceViewModel.FolderSettings;

        public Interaction(IShellPage appInstance)
        {
            AssociatedInstance = appInstance;
            jumpTimer.Interval = TimeSpan.FromSeconds(0.8);
            jumpTimer.Tick += JumpTimer_Tick;
        }

        public string JumpString
        {
            get => jumpString;
            set
            {
                // If current string is "a", and the next character typed is "a",
                // search for next file that starts with "a" (a.k.a. _jumpString = "a")
                if (jumpString.Length == 1 && value == jumpString + jumpString)
                {
                    value = jumpString;
                }
                if (value != "")
                {
                    ListedItem jumpedToItem = null;
                    ListedItem previouslySelectedItem = null;

                    // use FilesAndFolders because only displayed entries should be jumped to
                    var candidateItems = AssociatedInstance.FilesystemViewModel.FilesAndFolders.Where(f => f.ItemName.Length >= value.Length && f.ItemName.Substring(0, value.Length).ToLower() == value);

                    if (AssociatedInstance.SlimContentPage != null && AssociatedInstance.SlimContentPage.IsItemSelected)
                    {
                        previouslySelectedItem = AssociatedInstance.SlimContentPage.SelectedItem;
                    }

                    // If the user is trying to cycle through items
                    // starting with the same letter
                    if (value.Length == 1 && previouslySelectedItem != null)
                    {
                        // Try to select item lexicographically bigger than the previous item
                        jumpedToItem = candidateItems.FirstOrDefault(f => f.ItemName.CompareTo(previouslySelectedItem.ItemName) > 0);
                    }
                    if (jumpedToItem == null)
                    {
                        jumpedToItem = candidateItems.FirstOrDefault();
                    }

                    if (jumpedToItem != null)
                    {
                        AssociatedInstance.SlimContentPage.SetSelectedItemOnUi(jumpedToItem);
                        AssociatedInstance.SlimContentPage.ScrollIntoView(jumpedToItem);
                    }

                    // Restart the timer
                    jumpTimer.Start();
                }
                jumpString = value;
            }
        }

        public async void SetAsBackground(WallpaperType type, string filePath)
        {
            if (UserProfilePersonalizationSettings.IsSupported())
            {
                // Get the path of the selected file
                StorageFile sourceFile = await StorageItemHelpers.ToStorageItem<StorageFile>(filePath, AssociatedInstance);
                if (sourceFile == null)
                {
                    return;
                }

                // Get the app's local folder to use as the destination folder.
                StorageFolder localFolder = ApplicationData.Current.LocalFolder;

                // the file to the destination folder.
                // Generate unique name if the file already exists.
                // If the file you are trying to set as the wallpaper has the same name as the current wallpaper,
                // the system will ignore the request and no-op the operation
                var file = (StorageFile)await FilesystemTasks.Wrap(() => sourceFile.CopyAsync(localFolder, sourceFile.Name, NameCollisionOption.GenerateUniqueName).AsTask());
                if (file == null)
                {
                    return;
                }

                UserProfilePersonalizationSettings profileSettings = UserProfilePersonalizationSettings.Current;
                if (type == WallpaperType.Desktop)
                {
                    // Set the desktop background
                    await profileSettings.TrySetWallpaperImageAsync(file);
                }
                else if (type == WallpaperType.LockScreen)
                {
                    // Set the lockscreen background
                    await profileSettings.TrySetLockScreenImageAsync(file);
                }
            }
        }

        public RelayCommand AddNewTabToMultitaskingControl => new RelayCommand(() => OpenNewTab());

        private async void OpenNewTab()
        {
            await MainPage.AddNewTabByPathAsync(typeof(PaneHolderPage), "NewTab".GetLocalized());
        }

        public RelayCommand OpenNewPane => new RelayCommand(() => OpenNewPaneCommand());

        public void OpenNewPaneCommand()
        {
            AssociatedInstance.PaneHolder?.OpenPathInNewPane("NewTab".GetLocalized());
        }

        public void ItemPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed)
            {
                if ((e.OriginalSource as FrameworkElement)?.DataContext is ListedItem Item && Item.PrimaryItemAttribute == StorageItemTypes.Folder)
                {
                    if (Item.IsShortcutItem)
                    {
                        OpenPathInNewTab(((e.OriginalSource as FrameworkElement)?.DataContext as ShortcutItem)?.TargetPath ?? Item.ItemPath);
                    }
                    else
                    {
                        OpenPathInNewTab(Item.ItemPath);
                    }
                }
            }
        }

        public static async void OpenPathInNewTab(string path)
        {
            await MainPage.AddNewTabByPathAsync(typeof(PaneHolderPage), path);
        }

        public static async Task<bool> OpenPathInNewWindowAsync(string path)
        {
            var folderUri = new Uri($"files-uwp:?folder={Uri.EscapeDataString(path)}");
            return await Launcher.LaunchUriAsync(folderUri);
        }

        public static async Task<bool> OpenTabInNewWindowAsync(string tabArgs)
        {
            var folderUri = new Uri($"files-uwp:?tab={Uri.EscapeDataString(tabArgs)}");
            return await Launcher.LaunchUriAsync(folderUri);
        }

        public RelayCommand OpenDirectoryInDefaultTerminal => new RelayCommand(() => OpenDirectoryInTerminal(AssociatedInstance.FilesystemViewModel.WorkingDirectory));

        private async void OpenDirectoryInTerminal(string workingDir)
        {
            var terminal = AppSettings.TerminalController.Model.GetDefaultTerminal();

            if (Connection != null)
            {
                var value = new ValueSet
                {
                    { "WorkingDirectory", workingDir },
                    { "Application", terminal.Path },
                    { "Arguments", string.Format(terminal.Arguments,
                       Helpers.PathNormalization.NormalizePath(workingDir)) }
                };
                await Connection.SendMessageAsync(value);
            }
        }

        public async Task InvokeWin32ComponentAsync(string applicationPath, string arguments = null, bool runAsAdmin = false, string workingDir = null)
        {
            await InvokeWin32ComponentsAsync(new List<string>() { applicationPath }, arguments, runAsAdmin, workingDir);
        }

        public async Task InvokeWin32ComponentsAsync(List<string> applicationPaths, string arguments = null, bool runAsAdmin = false, string workingDir = null)
        {
            Debug.WriteLine("Launching EXE in FullTrustProcess");
            if (Connection != null)
            {
                var value = new ValueSet
                {
                    { "WorkingDirectory", string.IsNullOrEmpty(workingDir) ? AssociatedInstance?.FilesystemViewModel?.WorkingDirectory : workingDir },
                    { "Application", applicationPaths.FirstOrDefault() },
                    { "ApplicationList", JsonConvert.SerializeObject(applicationPaths) },
                };

                if (runAsAdmin)
                {
                    value.Add("Arguments", "runas");
                }
                else
                {
                    value.Add("Arguments", arguments);
                }

                await Connection.SendMessageAsync(value);
            }
        }

        public async Task OpenShellCommandInExplorerAsync(string shellCommand)
        {
            Debug.WriteLine("Launching shell command in FullTrustProcess");
            if (Connection != null)
            {
                var value = new ValueSet();
                value.Add("ShellCommand", shellCommand);
                value.Add("Arguments", "ShellCommand");
                await Connection.SendMessageAsync(value);
            }
        }

        public async void GrantAccessPermissionHandler(IUICommand command)
        {
            await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-broadfilesystemaccess"));
        }

        public static bool IsAnyContentDialogOpen()
        {
            var openedPopups = VisualTreeHelper.GetOpenPopups(Window.Current);
            return openedPopups.Any(popup => popup.Child is ContentDialog);
        }

        /// <summary>
        /// Navigates to a directory or opens file
        /// </summary>
        /// <param name="path"></param>
        /// <param name="itemType"></param>
        /// <param name="openSilent">Determines whether history of opened item is saved (... to Recent Items/Windows Timeline/opening in background)</param>
        /// <param name="openViaApplicationPicker">Determines whether open file using application picker</param>
        /// <param name="selectItems">List of filenames that are selected upon navigation</param>
        public async Task<bool> OpenPath(string path, FilesystemItemType? itemType = null, bool openSilent = false, bool openViaApplicationPicker = false, IEnumerable<string> selectItems = null)
        // TODO: This function reliability has not been extensively tested
        {
            string previousDir = AssociatedInstance.FilesystemViewModel.WorkingDirectory;
            bool isHiddenItem = NativeFileOperationsHelper.HasFileAttribute(path, System.IO.FileAttributes.Hidden);
            bool isShortcutItem = path.EndsWith(".lnk") || path.EndsWith(".url"); // Determine
            FilesystemResult opened = (FilesystemResult)false;

            // Shortcut item variables
            string shortcutTargetPath = null;
            string shortcutArguments = null;
            string shortcutWorkingDirectory = null;
            bool shortcutRunAsAdmin = false;
            bool shortcutIsFolder = false;

            if (itemType == null || isShortcutItem || isHiddenItem)
            {
                if (isShortcutItem)
                {
                    var (status, response) = await Connection.SendMessageForResponseAsync(new ValueSet()
                    {
                        { "Arguments", "FileOperation" },
                        { "fileop", "ParseLink" },
                        { "filepath", path }
                    });

                    if (status == AppServiceResponseStatus.Success)
                    {
                        shortcutTargetPath = response.Get("TargetPath", string.Empty);
                        shortcutArguments = response.Get("Arguments", string.Empty);
                        shortcutWorkingDirectory = response.Get("WorkingDirectory", string.Empty);
                        shortcutRunAsAdmin = response.Get("RunAsAdmin", false);
                        shortcutIsFolder = response.Get("IsFolder", false);

                        itemType = shortcutIsFolder ? FilesystemItemType.Directory : FilesystemItemType.File;
                    }
                    else
                    {
                        return false;
                    }
                }
                else if (isHiddenItem)
                {
                    itemType = NativeFileOperationsHelper.HasFileAttribute(path, System.IO.FileAttributes.Directory) ? FilesystemItemType.Directory : FilesystemItemType.File;
                }
                else
                {
                    itemType = await StorageItemHelpers.GetTypeFromPath(path);
                }
            }

            var mostRecentlyUsed = Windows.Storage.AccessCache.StorageApplicationPermissions.MostRecentlyUsedList;

            if (itemType == FilesystemItemType.Directory) // OpenDirectory
            {
                if (isShortcutItem)
                {
                    if (string.IsNullOrEmpty(shortcutTargetPath))
                    {
                        await InvokeWin32ComponentAsync(path);
                        return true;
                    }
                    else
                    {
                        AssociatedInstance.NavigationToolbar.PathControlDisplayText = shortcutTargetPath;
                        AssociatedInstance.NavigateWithArguments(AssociatedInstance.InstanceViewModel.FolderSettings.GetLayoutType(shortcutTargetPath), new NavigationArguments()
                        {
                            NavPathParam = shortcutTargetPath,
                            AssociatedTabInstance = AssociatedInstance,
                            SelectItems = selectItems
                        });

                        return true;
                    }
                }
                else if (isHiddenItem)
                {
                    AssociatedInstance.NavigationToolbar.PathControlDisplayText = path;
                    AssociatedInstance.NavigateWithArguments(AssociatedInstance.InstanceViewModel.FolderSettings.GetLayoutType(path), new NavigationArguments()
                    {
                        NavPathParam = path,
                        AssociatedTabInstance = AssociatedInstance
                    });

                    return true;
                }
                else
                {
                    opened = await AssociatedInstance.FilesystemViewModel.GetFolderWithPathFromPathAsync(path)
                        .OnSuccess(childFolder =>
                        {
                            // Add location to MRU List
                            mostRecentlyUsed.Add(childFolder.Folder, childFolder.Path);
                        });
                    if (!opened)
                    {
                        opened = (FilesystemResult)FolderHelpers.CheckFolderAccessWithWin32(path);
                    }
                    if (!opened)
                    {
                        opened = (FilesystemResult)path.StartsWith("ftp:");
                    }
                    if (opened)
                    {
                        AssociatedInstance.NavigationToolbar.PathControlDisplayText = path;
                        AssociatedInstance.NavigateWithArguments(AssociatedInstance.InstanceViewModel.FolderSettings.GetLayoutType(path), new NavigationArguments()
                        {
                            NavPathParam = path,
                            AssociatedTabInstance = AssociatedInstance,
                            SelectItems = selectItems
                        });
                    }
                }
            }
            else if (itemType == FilesystemItemType.File) // OpenFile
            {
                if (isShortcutItem)
                {
                    if (string.IsNullOrEmpty(shortcutTargetPath))
                    {
                        await InvokeWin32ComponentAsync(path);
                    }
                    else
                    {
                        if (!path.EndsWith(".url"))
                        {
                            StorageFileWithPath childFile = await AssociatedInstance.FilesystemViewModel.GetFileWithPathFromPathAsync(shortcutTargetPath);
                            if (childFile != null)
                            {
                                // Add location to MRU List
                                mostRecentlyUsed.Add(childFile.File, childFile.Path);
                            }
                        }
                        await InvokeWin32ComponentAsync(shortcutTargetPath, shortcutArguments, shortcutRunAsAdmin, shortcutWorkingDirectory);
                    }
                    opened = (FilesystemResult)true;
                }
                else if (isHiddenItem)
                {
                    await InvokeWin32ComponentAsync(path);
                }
                else
                {
                    opened = await AssociatedInstance.FilesystemViewModel.GetFileWithPathFromPathAsync(path)
                        .OnSuccess(async childFile =>
                        {
                            // Add location to MRU List
                            mostRecentlyUsed.Add(childFile.File, childFile.Path);

                            if (openViaApplicationPicker)
                            {
                                LauncherOptions options = new LauncherOptions
                                {
                                    DisplayApplicationPicker = true
                                };
                                await Launcher.LaunchFileAsync(childFile.File, options);
                            }
                            else
                            {
                                //try using launcher first
                                bool launchSuccess = false;

                                StorageFileQueryResult fileQueryResult = null;

                                //Get folder to create a file query (to pass to apps like Photos, Movies & TV..., needed to scroll through the folder like what Windows Explorer does)
                                StorageFolder currFolder = await AssociatedInstance.FilesystemViewModel.GetFolderFromPathAsync(Path.GetDirectoryName(path));

                                if (currFolder != null)
                                {
                                    QueryOptions queryOptions = new QueryOptions(CommonFileQuery.DefaultQuery, null);

                                    //We can have many sort entries
                                    SortEntry sortEntry = new SortEntry()
                                    {
                                        AscendingOrder = FolderSettings.DirectorySortDirection == Microsoft.Toolkit.Uwp.UI.SortDirection.Ascending
                                    };

                                    //Basically we tell to the launched app to follow how we sorted the files in the directory.

                                    var sortOption = FolderSettings.DirectorySortOption;

                                    switch (sortOption)
                                    {
                                        case Enums.SortOption.Name:
                                            sortEntry.PropertyName = "System.ItemNameDisplay";
                                            queryOptions.SortOrder.Clear();
                                            queryOptions.SortOrder.Add(sortEntry);
                                            break;

                                        case Enums.SortOption.DateModified:
                                            sortEntry.PropertyName = "System.DateModified";
                                            queryOptions.SortOrder.Clear();
                                            queryOptions.SortOrder.Add(sortEntry);
                                            break;

                                        case Enums.SortOption.DateCreated:
                                            sortEntry.PropertyName = "System.DateCreated";
                                            queryOptions.SortOrder.Clear();
                                            queryOptions.SortOrder.Add(sortEntry);
                                            break;

                                        //Unfortunately this is unsupported | Remarks: https://docs.microsoft.com/en-us/uwp/api/windows.storage.search.queryoptions.sortorder?view=winrt-19041
                                        //case Enums.SortOption.Size:

                                        //sortEntry.PropertyName = "System.TotalFileSize";
                                        //queryOptions.SortOrder.Clear();
                                        //queryOptions.SortOrder.Add(sortEntry);
                                        //break;

                                        //Unfortunately this is unsupported | Remarks: https://docs.microsoft.com/en-us/uwp/api/windows.storage.search.queryoptions.sortorder?view=winrt-19041
                                        //case Enums.SortOption.FileType:

                                        //sortEntry.PropertyName = "System.FileExtension";
                                        //queryOptions.SortOrder.Clear();
                                        //queryOptions.SortOrder.Add(sortEntry);
                                        //break;

                                        //Handle unsupported
                                        default:
                                            //keep the default one in SortOrder IList
                                            break;
                                    }

                                    fileQueryResult = currFolder.CreateFileQueryWithOptions(queryOptions);

                                    var options = new LauncherOptions
                                    {
                                        NeighboringFilesQuery = fileQueryResult
                                    };

                                    // Now launch file with options.
                                    launchSuccess = await Launcher.LaunchFileAsync(childFile.File, options);
                                }

                                if (!launchSuccess)
                                {
                                    await InvokeWin32ComponentAsync(path);
                                }
                            }
                        });
                }
            }

            if (opened.ErrorCode == FileSystemStatusCode.NotFound && !openSilent)
            {
                await DialogDisplayHelper.ShowDialogAsync("FileNotFoundDialog/Title".GetLocalized(), "FileNotFoundDialog/Text".GetLocalized());
                AssociatedInstance.NavigationToolbar.CanRefresh = false;
                await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    var ContentOwnedViewModelInstance = AssociatedInstance.FilesystemViewModel;
                    ContentOwnedViewModelInstance?.RefreshItems(previousDir);
                });
            }

            return opened;
        }

        public async void OpenSelectedItems(bool openViaApplicationPicker = false)
        {
            if (AssociatedInstance.FilesystemViewModel.WorkingDirectory.StartsWith(AppSettings.RecycleBinPath))
            {
                // Do not open files and folders inside the recycle bin
                return;
            }
            if (AssociatedInstance.SlimContentPage == null)
            {
                return;
            }
            foreach (ListedItem item in AssociatedInstance.SlimContentPage.SelectedItems)
            {
                var type = item.PrimaryItemAttribute == StorageItemTypes.Folder ?
                    FilesystemItemType.Directory : FilesystemItemType.File;
                await OpenPath(item.ItemPath, type, false, openViaApplicationPicker);
            }
        }

        public RelayCommand OpenNewWindow => new RelayCommand(() => LaunchNewWindow());

        public async void LaunchNewWindow()
        {
            var filesUWPUri = new Uri("files-uwp:");
            await Launcher.LaunchUriAsync(filesUWPUri);
        }

        public void ShareItem_Click(object sender, RoutedEventArgs e)
        {
            DataTransferManager manager = DataTransferManager.GetForCurrentView();
            manager.DataRequested += new TypedEventHandler<DataTransferManager, DataRequestedEventArgs>(Manager_DataRequested);
            DataTransferManager.ShowShareUI();
        }

        public async void ShowProperties()
        {
            if (AssociatedInstance.SlimContentPage.IsItemSelected)
            {
                if (AssociatedInstance.SlimContentPage.SelectedItems.Count > 1)
                {
                    await OpenPropertiesWindowAsync(AssociatedInstance.SlimContentPage.SelectedItems);
                }
                else
                {
                    await OpenPropertiesWindowAsync(AssociatedInstance.SlimContentPage.SelectedItem);
                }
            }
            else
            {
                if (!Path.GetPathRoot(AssociatedInstance.FilesystemViewModel.CurrentFolder.ItemPath)
                    .Equals(AssociatedInstance.FilesystemViewModel.CurrentFolder.ItemPath, StringComparison.OrdinalIgnoreCase))
                {
                    await OpenPropertiesWindowAsync(AssociatedInstance.FilesystemViewModel.CurrentFolder);
                }
                else
                {
                    await OpenPropertiesWindowAsync(App.DrivesManager.Drives
                        .SingleOrDefault(x => x.Path.Equals(AssociatedInstance.FilesystemViewModel.CurrentFolder.ItemPath)));
                }
            }
        }

        public async Task OpenPropertiesWindowAsync(object item)
        {
            if (item == null)
            {
                return;
            }
            if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
            {
                CoreApplicationView newWindow = CoreApplication.CreateNewView();
                ApplicationView newView = null;

                await newWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    Frame frame = new Frame();
                    frame.Navigate(typeof(Properties), new PropertiesPageNavigationArguments()
                    {
                        Item = item,
                        AppInstanceArgument = AssociatedInstance
                    }, new SuppressNavigationTransitionInfo());
                    Window.Current.Content = frame;
                    Window.Current.Activate();

                    newView = ApplicationView.GetForCurrentView();
                    newWindow.TitleBar.ExtendViewIntoTitleBar = true;
                    newView.Title = "PropertiesTitle".GetLocalized();
                    newView.PersistedStateId = "Properties";
                    newView.SetPreferredMinSize(new Size(400, 550));
                    newView.Consolidated += delegate
                    {
                        Window.Current.Close();
                    };
                });

                bool viewShown = await ApplicationViewSwitcher.TryShowAsStandaloneAsync(newView.Id);
                // Set window size again here as sometimes it's not resized in the page Loaded event
                newView.TryResizeView(new Size(400, 550));
            }
            else
            {
                var propertiesDialog = new PropertiesDialog();
                propertiesDialog.propertiesFrame.Tag = propertiesDialog;
                propertiesDialog.propertiesFrame.Navigate(typeof(Properties), new PropertiesPageNavigationArguments()
                {
                    Item = item,
                    AppInstanceArgument = AssociatedInstance
                }, new SuppressNavigationTransitionInfo());
                await propertiesDialog.ShowAsync(ContentDialogPlacement.Popup);
            }
        }

        public void PinDirectoryToSidebar(object sender, RoutedEventArgs e)
        {
            App.SidebarPinnedController.Model.AddItem(AssociatedInstance.FilesystemViewModel.WorkingDirectory);
        }

        private async void Manager_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            DataRequestDeferral dataRequestDeferral = args.Request.GetDeferral();
            List<IStorageItem> items = new List<IStorageItem>();
            DataRequest dataRequest = args.Request;

            /*dataRequest.Data.Properties.Title = "Data Shared From Files";
            dataRequest.Data.Properties.Description = "The items you selected will be shared";*/

            foreach (ListedItem item in AssociatedInstance.SlimContentPage.SelectedItems)
            {
                if (item.IsShortcutItem)
                {
                    if (item.IsLinkItem)
                    {
                        dataRequest.Data.Properties.Title = string.Format("ShareDialogTitle".GetLocalized(), items.First().Name);
                        dataRequest.Data.Properties.Description = "ShareDialogSingleItemDescription".GetLocalized();
                        dataRequest.Data.SetWebLink(new Uri(((ShortcutItem)item).TargetPath));
                        dataRequestDeferral.Complete();
                        return;
                    }
                }
                else if (item.PrimaryItemAttribute == StorageItemTypes.Folder)
                {
                    if (await StorageItemHelpers.ToStorageItem<StorageFolder>(item.ItemPath, AssociatedInstance) is StorageFolder folder)
                    {
                        items.Add(folder);
                    }
                }
                else
                {
                    if (await StorageItemHelpers.ToStorageItem<StorageFile>(item.ItemPath, AssociatedInstance) is StorageFile file)
                    {
                        items.Add(file);
                    }
                }
            }

            if (items.Count == 1)
            {
                dataRequest.Data.Properties.Title = string.Format("ShareDialogTitle".GetLocalized(), items.First().Name);
                dataRequest.Data.Properties.Description = "ShareDialogSingleItemDescription".GetLocalized();
            }
            else if (items.Count == 0)
            {
                dataRequest.FailWithDisplayText("ShareDialogFailMessage".GetLocalized());
                dataRequestDeferral.Complete();
                return;
            }
            else
            {
                dataRequest.Data.Properties.Title = string.Format("ShareDialogTitleMultipleItems".GetLocalized(), items.Count,
                    "ItemsCount.Text".GetLocalized());
                dataRequest.Data.Properties.Description = "ShareDialogMultipleItemsDescription".GetLocalized();
            }

            dataRequest.Data.SetStorageItems(items);
            dataRequestDeferral.Complete();
        }

        public async Task<bool> RenameFileItemAsync(ListedItem item, string oldName, string newName)
        {
            if (oldName == newName)
            {
                return true;
            }

            var renamed = ReturnResult.InProgress;
            if (item.PrimaryItemAttribute == StorageItemTypes.Folder)
            {
                renamed = await FilesystemHelpers.RenameAsync(StorageItemHelpers.FromPathAndType(item.ItemPath, FilesystemItemType.Directory),
                    newName, NameCollisionOption.FailIfExists, true);
            }
            else
            {
                if (item.IsShortcutItem || !AppSettings.ShowFileExtensions)
                {
                    newName += item.FileExtension;
                }

                renamed = await FilesystemHelpers.RenameAsync(StorageItemHelpers.FromPathAndType(item.ItemPath, FilesystemItemType.File),
                    newName, NameCollisionOption.FailIfExists, true);
            }

            if (renamed == ReturnResult.Success)
            {
                AssociatedInstance.NavigationToolbar.CanGoForward = false;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Set a single file or folder to hidden or unhidden an refresh the
        /// view after setting the flag
        /// </summary>
        /// <param name="item"></param>
        /// <param name="isHidden"></param>
        public void SetHiddenAttributeItem(ListedItem item, bool isHidden)
        {
            item.IsHiddenItem = isHidden;
            AssociatedInstance.SlimContentPage.ResetItemOpacity();
        }

        public RelayCommand CopyPathOfSelectedItem => new RelayCommand(() => CopyLocation());

        private void CopyLocation()
        {
            try
            {
                if (AssociatedInstance.SlimContentPage != null)
                {
                    DataPackage data = new DataPackage();
                    data.SetText(AssociatedInstance.SlimContentPage.SelectedItem.ItemPath);
                    Clipboard.SetContent(data);
                    Clipboard.Flush();
                }
            }
            catch
            {
            }
        }

        public RelayCommand CopyPathOfWorkingDirectory => new RelayCommand(() => CopyWorkingLocation());

        private void CopyWorkingLocation()
        {
            try
            {
                if (AssociatedInstance.SlimContentPage != null)
                {
                    DataPackage data = new DataPackage();
                    data.SetText(AssociatedInstance.FilesystemViewModel.WorkingDirectory);
                    Clipboard.SetContent(data);
                    Clipboard.Flush();
                }
            }
            catch
            {
            }
        }

        public RelayCommand PasteItemsFromClipboard => new RelayCommand(async () => await PasteItemAsync(AssociatedInstance.FilesystemViewModel.WorkingDirectory));

        public async Task PasteItemAsync(string destinationPath)
        {
            DataPackageView packageView = await FilesystemTasks.Wrap(() => Task.FromResult(Clipboard.GetContent()));
            if (packageView != null)
            {
                await FilesystemHelpers.PerformOperationTypeAsync(packageView.RequestedOperation, packageView, destinationPath, true);
                AssociatedInstance.SlimContentPage.ResetItemOpacity();
            }
        }

        public async void CreateFileFromDialogResultType(AddItemType itemType, ShellNewEntry itemInfo)
        {
            string currentPath = null;
            if (AssociatedInstance.SlimContentPage != null)
            {
                currentPath = AssociatedInstance.FilesystemViewModel.WorkingDirectory;
            }

            // Show rename dialog
            DynamicDialog dialog = DynamicDialogFactory.GetFor_RenameDialog();
            await dialog.ShowAsync();

            if (dialog.DynamicResult != DynamicDialogResult.Primary)
            {
                return;
            }

            // Create file based on dialog result
            string userInput = dialog.ViewModel.AdditionalData as string;
            var folderRes = await AssociatedInstance.FilesystemViewModel.GetFolderWithPathFromPathAsync(currentPath);
            FilesystemResult created = folderRes;
            if (folderRes)
            {
                switch (itemType)
                {
                    case AddItemType.Folder:
                        userInput = !string.IsNullOrWhiteSpace(userInput) ? userInput : "NewFolder".GetLocalized();
                        created = await FilesystemTasks.Wrap(async () =>
                        {
                            return await FilesystemHelpers.CreateAsync(
                                StorageItemHelpers.FromPathAndType(Path.Combine(folderRes.Result.Path, userInput), FilesystemItemType.Directory),
                                true);
                        });
                        break;

                    case AddItemType.File:
                        userInput = !string.IsNullOrWhiteSpace(userInput) ? userInput : itemInfo?.Name ?? "NewFile".GetLocalized();
                        created = await FilesystemTasks.Wrap(async () =>
                        {
                            return await FilesystemHelpers.CreateAsync(
                                StorageItemHelpers.FromPathAndType(Path.Combine(folderRes.Result.Path, userInput + itemInfo?.Extension), FilesystemItemType.File),
                                true);
                        });
                        break;
                }
            }
            if (created == FileSystemStatusCode.Unauthorized)
            {
                await DialogDisplayHelper.ShowDialogAsync("AccessDeniedCreateDialog/Title".GetLocalized(), "AccessDeniedCreateDialog/Text".GetLocalized());
            }
        }

        public RelayCommand CreateNewFolder => new RelayCommand(() => NewFolder());
        public RelayCommand<ShellNewEntry> CreateNewFile => new RelayCommand<ShellNewEntry>((itemType) => NewFile(itemType));

        private void NewFolder()
        {
            CreateFileFromDialogResultType(AddItemType.Folder, null);
        }

        private void NewFile(ShellNewEntry itemType)
        {
            CreateFileFromDialogResultType(AddItemType.File, itemType);
        }

        public RelayCommand SelectAllContentPageItems => new RelayCommand(() => SelectAllItems(AssociatedInstance.SlimContentPage));

        public void SelectAllItems(IBaseLayout contentPage) => contentPage.SelectAllItems();

        public RelayCommand InvertContentPageSelction => new RelayCommand(() => InvertAllItems(AssociatedInstance.SlimContentPage));

        public void InvertAllItems(IBaseLayout contentPage) => contentPage.InvertSelection();

        public RelayCommand ClearContentPageSelection => new RelayCommand(() => ClearAllItems(AssociatedInstance.SlimContentPage));

        public void ClearAllItems(IBaseLayout contentPage) => contentPage.ClearSelection();

        public void PushJumpChar(char letter)
        {
            JumpString += letter.ToString().ToLower();
        }

        private void JumpTimer_Tick(object sender, object e)
        {
            jumpString = "";
            jumpTimer.Stop();
        }
    }
}