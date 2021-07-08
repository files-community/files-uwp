using Files.Common;
using Files.Enums;
using Files.Extensions;
using Files.Filesystem;
using Files.Filesystem.Cloud;
using Files.Filesystem.StorageEnumerators;
using Files.Helpers;
using Files.Helpers.FileListCache;
using Microsoft.Toolkit.Uwp;
using Microsoft.Toolkit.Uwp.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using static Files.Helpers.NativeDirectoryChangesHelper;
using static Files.Helpers.NativeFindStorageItemHelper;
using FileAttributes = System.IO.FileAttributes;

namespace Files.ViewModels
{
    public class ItemViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly SemaphoreSlim enumFolderSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim loadExtendedPropsSemaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
        private readonly ConcurrentQueue<(uint Action, string FileName)> operationQueue = new ConcurrentQueue<(uint Action, string FileName)>();
        private readonly ManualResetEventSlim operationEvent = new ManualResetEventSlim();
        private IntPtr hWatchDir;
        private IAsyncAction aWatcherAction;

        // files and folders list for manipulating
        private List<ListedItem> filesAndFolders;

        // only used for Binding and ApplyFilesAndFoldersChangesAsync, don't manipulate on this!
        public BulkConcurrentObservableCollection<ListedItem> FilesAndFolders { get; }

        private SettingsViewModel AppSettings => App.AppSettings;
        private FolderSettingsViewModel folderSettings = null;
        private bool shouldDisplayFileExtensions = false;
        public ListedItem CurrentFolder { get; private set; }
        public CollectionViewSource viewSource;
        private CancellationTokenSource addFilesCTS, semaphoreCTS, loadPropsCTS;

        public event PropertyChangedEventHandler PropertyChanged;

        public event EventHandler DirectoryInfoUpdated;

        private IFileListCache fileListCache = FileListCacheController.GetInstance();

        private NamedPipeAsAppServiceConnection connection;
        private NamedPipeAsAppServiceConnection Connection
        {
            get => connection;
            set
            {
                if (connection != null)
                {
                    connection.RequestReceived -= Connection_RequestReceived;
                }
                connection = value;
                if (connection != null)
                {
                    connection.RequestReceived += Connection_RequestReceived;
                }
            }
        }

        public string WorkingDirectory
        {
            get; private set;
        }

        private IStorageFolder currentStorageFolder;
        private IStorageFolder workingRoot;

        public delegate void WorkingDirectoryModifiedEventHandler(object sender, WorkingDirectoryModifiedEventArgs e);

        public event WorkingDirectoryModifiedEventHandler WorkingDirectoryModified;

        public delegate void PageTypeUpdatedEventHandler(object sender, PageTypeUpdatedEventArgs e);

        public event PageTypeUpdatedEventHandler PageTypeUpdated;

        public delegate void ItemLoadStatusChangedEventHandler(object sender, ItemLoadStatusChangedEventArgs e);

        public event ItemLoadStatusChangedEventHandler ItemLoadStatusChanged;

        public async Task SetWorkingDirectoryAsync(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            bool isLibrary = false;
            string name = null;
            if (App.LibraryManager.TryGetLibrary(value, out LibraryLocationItem library))
            {
                isLibrary = true;
                name = library.Text;
            }

            WorkingDirectoryModified?.Invoke(this, new WorkingDirectoryModifiedEventArgs { Path = value, IsLibrary = isLibrary, Name = name });

            if (isLibrary || !Path.IsPathRooted(value))
            {
                workingRoot = null;
                currentStorageFolder = null;
            }
            else if (!Path.IsPathRooted(WorkingDirectory) || Path.GetPathRoot(WorkingDirectory) != Path.GetPathRoot(value))
            {
                workingRoot = await FilesystemTasks.Wrap(() => DrivesManager.GetRootFromPathAsync(value));
            }

            if (value == "Home" || value == "NewTab".GetLocalized())
            {
                currentStorageFolder = null;
            }
            else
            {
                App.JumpList.AddFolderToJumpList(value);
            }

            WorkingDirectory = value;
            NotifyPropertyChanged(nameof(WorkingDirectory));
        }

        public async Task<FilesystemResult<StorageFolder>> GetFolderFromPathAsync(string value)
        {
            return await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderFromPathAsync(value, workingRoot, currentStorageFolder));
        }

        public async Task<FilesystemResult<StorageFile>> GetFileFromPathAsync(string value)
        {
            return await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFileFromPathAsync(value, workingRoot, currentStorageFolder));
        }

        public async Task<FilesystemResult<IStorageFolder>> GetFolderWithPathFromPathAsync(string value)
        {
            return await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderWithPathFromPathAsync(value, workingRoot, currentStorageFolder));
        }

        public async Task<FilesystemResult<IStorageFile>> GetFileWithPathFromPathAsync(string value)
        {
            return await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFileWithPathFromPathAsync(value, workingRoot, currentStorageFolder));
        }

        private bool isFolderEmptyTextDisplayed;

        public bool IsFolderEmptyTextDisplayed
        {
            get => isFolderEmptyTextDisplayed;
            set
            {
                if (value != isFolderEmptyTextDisplayed)
                {
                    isFolderEmptyTextDisplayed = value;
                    NotifyPropertyChanged(nameof(IsFolderEmptyTextDisplayed));
                }
            }
        }

        public async void UpdateSortOptionStatus()
        {
            NotifyPropertyChanged(nameof(IsSortedByName));
            NotifyPropertyChanged(nameof(IsSortedByDate));
            NotifyPropertyChanged(nameof(IsSortedByType));
            NotifyPropertyChanged(nameof(IsSortedBySize));
            NotifyPropertyChanged(nameof(IsSortedByOriginalPath));
            NotifyPropertyChanged(nameof(IsSortedByDateDeleted));
            NotifyPropertyChanged(nameof(IsSortedByDateCreated));
            NotifyPropertyChanged(nameof(IsSortedBySyncStatus));
            await OrderFilesAndFoldersAsync();
            await ApplyFilesAndFoldersChangesAsync();
        }

        public async void UpdateSortDirectionStatus()
        {
            NotifyPropertyChanged(nameof(IsSortedAscending));
            NotifyPropertyChanged(nameof(IsSortedDescending));
            await OrderFilesAndFoldersAsync();
            await ApplyFilesAndFoldersChangesAsync();
        }

        public bool IsSortedByName
        {
            get => folderSettings.DirectorySortOption == SortOption.Name;
            set
            {
                if (value)
                {
                    folderSettings.DirectorySortOption = SortOption.Name;
                    NotifyPropertyChanged(nameof(IsSortedByName));
                }
            }
        }

        public bool IsSortedByOriginalPath
        {
            get => folderSettings.DirectorySortOption == SortOption.OriginalPath;
            set
            {
                if (value)
                {
                    folderSettings.DirectorySortOption = SortOption.OriginalPath;
                    NotifyPropertyChanged(nameof(IsSortedByOriginalPath));
                }
            }
        }

        public bool IsSortedByDateDeleted
        {
            get => folderSettings.DirectorySortOption == SortOption.DateDeleted;
            set
            {
                if (value)
                {
                    folderSettings.DirectorySortOption = SortOption.DateDeleted;
                    NotifyPropertyChanged(nameof(IsSortedByDateDeleted));
                }
            }
        }

        public bool IsSortedByDate
        {
            get => folderSettings.DirectorySortOption == SortOption.DateModified;
            set
            {
                if (value)
                {
                    folderSettings.DirectorySortOption = SortOption.DateModified;
                    NotifyPropertyChanged(nameof(IsSortedByDate));
                }
            }
        }

        public bool IsSortedByDateCreated
        {
            get => folderSettings.DirectorySortOption == SortOption.DateCreated;
            set
            {
                if (value)
                {
                    folderSettings.DirectorySortOption = SortOption.DateCreated;
                    NotifyPropertyChanged(nameof(IsSortedByDateCreated));
                }
            }
        }

        public bool IsSortedByType
        {
            get => folderSettings.DirectorySortOption == SortOption.FileType;
            set
            {
                if (value)
                {
                    folderSettings.DirectorySortOption = SortOption.FileType;
                    NotifyPropertyChanged(nameof(IsSortedByType));
                }
            }
        }

        public bool IsSortedBySize
        {
            get => folderSettings.DirectorySortOption == SortOption.Size;
            set
            {
                if (value)
                {
                    folderSettings.DirectorySortOption = SortOption.Size;
                    NotifyPropertyChanged(nameof(IsSortedBySize));
                }
            }
        }

        public bool IsSortedBySyncStatus
        {
            get => folderSettings.DirectorySortOption == SortOption.SyncStatus;
            set
            {
                if (value)
                {
                    folderSettings.DirectorySortOption = SortOption.SyncStatus;
                    NotifyPropertyChanged(nameof(IsSortedBySyncStatus));
                }
            }
        }

        public bool IsSortedAscending
        {
            get => folderSettings.DirectorySortDirection == SortDirection.Ascending;
            set
            {
                folderSettings.DirectorySortDirection = value ? SortDirection.Ascending : SortDirection.Descending;
                NotifyPropertyChanged(nameof(IsSortedAscending));
                NotifyPropertyChanged(nameof(IsSortedDescending));
            }
        }

        public bool IsSortedDescending
        {
            get => !IsSortedAscending;
            set
            {
                folderSettings.DirectorySortDirection = value ? SortDirection.Descending : SortDirection.Ascending;
                NotifyPropertyChanged(nameof(IsSortedAscending));
                NotifyPropertyChanged(nameof(IsSortedDescending));
            }
        }

        public ItemViewModel(FolderSettingsViewModel folderSettingsViewModel)
        {
            folderSettings = folderSettingsViewModel;
            filesAndFolders = new List<ListedItem>();
            FilesAndFolders = new BulkConcurrentObservableCollection<ListedItem>();
            addFilesCTS = new CancellationTokenSource();
            semaphoreCTS = new CancellationTokenSource();
            loadPropsCTS = new CancellationTokenSource();
            shouldDisplayFileExtensions = App.AppSettings.ShowFileExtensions;

            AppServiceConnectionHelper.ConnectionChanged += AppServiceConnectionHelper_ConnectionChanged;
        }

        private async void AppServiceConnectionHelper_ConnectionChanged(object sender, Task<NamedPipeAsAppServiceConnection> e)
        {
            Connection = await e;
        }

        private async void Connection_RequestReceived(object sender, Dictionary<string, object> message)
        {
            // The fulltrust process signaled that something in the recycle bin folder has changed
            if (message.ContainsKey("FileSystem"))
            {
                var folderPath = (string)message["FileSystem"];
                var itemPath = (string)message["Path"];
                var changeType = (string)message["Type"];
                var newItem = JsonConvert.DeserializeObject<ShellFileItem>(message.Get("Item", ""));
                Debug.WriteLine("{0}: {1}", folderPath, changeType);
                // If we are currently displaying the reycle bin lets refresh the items
                if (CurrentFolder?.ItemPath == folderPath)
                {
                    switch (changeType)
                    {
                        case "Created":
                            var newListedItem = AddFileOrFolderFromShellFile(newItem);
                            if (newListedItem != null)
                            {
                                await AddFileOrFolderAsync(newListedItem);
                                await OrderFilesAndFoldersAsync();
                                await ApplySingleFileChangeAsync(newListedItem);
                            }
                            break;

                        case "Deleted":
                            var removedItem = await RemoveFileOrFolderAsync(itemPath);
                            if (removedItem != null)
                            {
                                await ApplySingleFileChangeAsync(removedItem);
                            }
                            break;

                        default:
                            await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
                            {
                                RefreshItems(null);
                            });
                            break;
                    }
                }
            }
            // The fulltrust process signaled that a drive has been connected/disconnected
            else if (message.ContainsKey("DeviceID"))
            {
                var deviceId = (string)message["DeviceID"];
                var eventType = (DeviceEvent)(long)message["EventType"];
                await App.DrivesManager.HandleWin32DriveEvent(eventType, deviceId);
            }
            else if (message.ContainsKey("Library"))
            {
                await App.LibraryManager.HandleWin32LibraryEvent(JsonConvert.DeserializeObject<ShellLibraryItem>(message.Get("Item", "")), message.Get("OldPath", ""));
            }
        }

        public void CancelLoadAndClearFiles()
        {
            Debug.WriteLine("CancelLoadAndClearFiles");
            CloseWatcher();
            if (IsLoadingItems)
            {
                addFilesCTS.Cancel();
            }
            CancelExtendedPropertiesLoading();
            filesAndFolders.Clear();
            FilesAndFolders.Clear();
        }

        public void CancelExtendedPropertiesLoading()
        {
            loadPropsCTS.Cancel();
            loadPropsCTS = new CancellationTokenSource();
        }

        public async Task ApplySingleFileChangeAsync(ListedItem item)
        {
            var newIndex = filesAndFolders.IndexOf(item);
            await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
            {
                FilesAndFolders.Remove(item);
                if (newIndex != -1)
                {
                    FilesAndFolders.Insert(Math.Min(newIndex, FilesAndFolders.Count), item);
                }
                IsFolderEmptyTextDisplayed = FilesAndFolders.Count == 0;
                DirectoryInfoUpdated?.Invoke(this, EventArgs.Empty);
            });
        }

        // apply changes immediately after manipulating on filesAndFolders completed
        public async Task ApplyFilesAndFoldersChangesAsync()
        {
            try
            {
                if (filesAndFolders == null || filesAndFolders.Count == 0)
                {
                    Action action = () =>
                    {
                        FilesAndFolders.Clear();
                        IsFolderEmptyTextDisplayed = FilesAndFolders.Count == 0;
                        DirectoryInfoUpdated?.Invoke(this, EventArgs.Empty);
                    };
                    if (CoreApplication.MainView.DispatcherQueue.HasThreadAccess)
                    {
                        action();
                    }
                    else
                    {
                        await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(action);
                    }
                    return;
                }

                // CollectionChanged will cause UI update, which may cause significant performance degradation,
                // so suppress CollectionChanged event here while loading items heavily.

                // Note that both DataGrid and GridView don't support multi-items changes notification, so here
                // we have to call BeginBulkOperation to suppress CollectionChanged and call EndBulkOperation
                // in the end to fire a CollectionChanged event with NotifyCollectionChangedAction.Reset
                FilesAndFolders.BeginBulkOperation();

                // After calling BeginBulkOperation, ObservableCollection.CollectionChanged is suppressed
                // so modifies to FilesAndFolders won't trigger UI updates, hence below operations can be
                // run safely without needs of dispatching to UI thread
                Action applyChangesAction = () =>
                {
                    var startIndex = -1;
                    var tempList = new List<ListedItem>();

                    void ApplyBulkInsertEntries()
                    {
                        if (startIndex != -1)
                        {
                            FilesAndFolders.ReplaceRange(startIndex, tempList);
                            startIndex = -1;
                            tempList.Clear();
                        }
                    }

                    for (var i = 0; i < filesAndFolders.Count; i++)
                    {
                        if (addFilesCTS.IsCancellationRequested)
                        {
                            return;
                        }

                        if (i < FilesAndFolders.Count)
                        {
                            if (FilesAndFolders[i] != filesAndFolders[i])
                            {
                                if (startIndex == -1)
                                {
                                    startIndex = i;
                                }
                                tempList.Add(filesAndFolders[i]);
                            }
                            else
                            {
                                ApplyBulkInsertEntries();
                            }
                        }
                        else
                        {
                            ApplyBulkInsertEntries();
                            FilesAndFolders.InsertRange(i, filesAndFolders.Skip(i));
                            break;
                        }
                    }

                    ApplyBulkInsertEntries();
                    if (FilesAndFolders.Count > filesAndFolders.Count)
                    {
                        FilesAndFolders.RemoveRange(filesAndFolders.Count, FilesAndFolders.Count - filesAndFolders.Count);
                    }

                    if (folderSettings.DirectoryGroupOption != GroupOption.None)
                    {
                        OrderGroups();
                    }
                };

                Action updateUIAction = () =>
                {
                    // trigger CollectionChanged with NotifyCollectionChangedAction.Reset
                    // once loading is completed so that UI can be updated
                    FilesAndFolders.EndBulkOperation();
                    IsFolderEmptyTextDisplayed = FilesAndFolders.Count == 0;
                    DirectoryInfoUpdated?.Invoke(this, EventArgs.Empty);
                };

                if (CoreApplication.MainView.DispatcherQueue.HasThreadAccess)
                {
                    await Task.Run(applyChangesAction);
                    updateUIAction();
                }
                else
                {
                    applyChangesAction();
                    await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(updateUIAction);
                }
            }
            catch (Exception ex)
            {
                App.Logger.Warn(ex, ex.Message);
            }
        }

        private Task OrderFilesAndFoldersAsync()
        {
            // Sorting group contents is handled elsewhere
            if (folderSettings.DirectoryGroupOption != GroupOption.None)
            {
                return Task.CompletedTask;
            }

            Action action = () =>
            {
                if (filesAndFolders.Count == 0)
                {
                    return;
                }

                filesAndFolders = SortingHelper.OrderFileList(filesAndFolders, folderSettings.DirectorySortOption, folderSettings.DirectorySortDirection).ToList();
            };

            if (CoreApplication.MainView.DispatcherQueue.HasThreadAccess)
            {
                return Task.Run(action);
            }
            else
            {
                action();
                return Task.CompletedTask;
            }
        }

        private void OrderGroups(CancellationToken token = default)
        {
            var gps = FilesAndFolders.GroupedCollection?.Where(x => !x.IsSorted);
            if (gps is null)
            {
                return;
            }
            foreach (var gp in gps)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                gp.Order(list => SortingHelper.OrderFileList(list, folderSettings.DirectorySortOption, folderSettings.DirectorySortDirection));
            }
            if (!FilesAndFolders.GroupedCollection.IsSorted)
            {
                FilesAndFolders.GroupedCollection.Order(x => x.OrderBy(y => y.Model.SortIndexOverride).ThenBy(y => y.Model.Text));
                FilesAndFolders.GroupedCollection.IsSorted = true;
            }
        }

        public async Task GroupOptionsUpdated(CancellationToken token)
        {
            try
            {
                // Conflicts will occur if re-grouping is run while items are still being enumerated, so wait for enumeration to complete first
                await enumFolderSemaphore.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                FilesAndFolders.BeginBulkOperation();
                UpdateGroupOptions();
                if (FilesAndFolders.IsGrouped)
                {
                    await Task.Run(() =>
                    {
                        FilesAndFolders.ResetGroups(token);
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }
                        OrderGroups();
                    });
                }
                else
                {
                    await OrderFilesAndFoldersAsync();
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
                {
                    FilesAndFolders.EndBulkOperation();
                });
            }
            catch (Exception ex)
            {
                App.Logger.Warn(ex, ex.Message);
            }
            finally
            {
                enumFolderSemaphore.Release();
            }
        }

        public async Task ReloadItemGroupHeaderImagesAsync()
        {
            // this is needed to update the group icons for file type groups
            if (folderSettings.DirectoryGroupOption == GroupOption.FileType && FilesAndFolders.GroupedCollection != null)
            {
                await Task.Run(async () =>
                {
                    foreach (var gp in FilesAndFolders.GroupedCollection)
                    {
                        var img = await GetItemTypeGroupIcon(gp.FirstOrDefault());
                        await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
                        {
                            gp.Model.ImageSource = img;
                        }, Windows.System.DispatcherQueuePriority.Low);
                    }
                });
            }
        }

        public void UpdateGroupOptions()
        {
            FilesAndFolders.ItemGroupKeySelector = GroupingHelper.GetItemGroupKeySelector(folderSettings.DirectoryGroupOption);
            var groupInfoSelector = GroupingHelper.GetGroupInfoSelector(folderSettings.DirectoryGroupOption);
            FilesAndFolders.GetGroupHeaderInfo = groupInfoSelector.Item1;
            FilesAndFolders.GetExtendedGroupHeaderInfo = groupInfoSelector.Item2;
        }

        private bool isLoadingItems = false;

        public bool IsLoadingItems
        {
            get
            {
                return isLoadingItems;
            }
            set
            {
                isLoadingItems = value;
            }
        }

        List<(ListedItem item, uint size)> itemLoadQueue = new List<(ListedItem, uint)>();

        public async void UnloadItemThumbnail(ListedItem item)
        {
            Action action = () =>
            {
                item.FileImage = null;
                item.CustomIcon = null;
                item.LoadFileIcon = false;
                if (item.PrimaryItemAttribute == StorageItemTypes.Folder)
                {
                    item.LoadFolderGlyph = true;
                }
                else
                {
                    item.LoadUnknownTypeGlyph = true;
                }
            };
            if (CoreApplication.MainView.DispatcherQueue.HasThreadAccess)
            {
                action();
            }
            else
            {
                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(action);
            }
        }

        public async Task LoadItemThumbnail(ListedItem item, uint thumbnailSize = 20, IStorageItem matchingStorageItem = null, bool forceReload = false)
        {
            if (item.IsLibraryItem || item.PrimaryItemAttribute == StorageItemTypes.File)
            {
                if (!forceReload && item.CustomIconData != null)
                {
                    await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                    {
                        item.FileImage = await item.CustomIconData.ToBitmapAsync();
                        item.LoadUnknownTypeGlyph = false;
                        item.LoadWebShortcutGlyph = false;
                        item.LoadFileIcon = true;
                    }, Windows.System.DispatcherQueuePriority.Low);
                }
                else
                {
                    if (!item.IsShortcutItem && !item.IsHiddenItem && !item.ItemPath.StartsWith("ftp:"))
                    {
                        var matchingStorageFile = (StorageFile)matchingStorageItem ?? await GetFileFromPathAsync(item.ItemPath);
                        if (matchingStorageFile != null)
                        {
                            using var Thumbnail = await matchingStorageFile.GetThumbnailAsync(ThumbnailMode.ListView, thumbnailSize, ThumbnailOptions.ResizeThumbnail);
                            if (!(Thumbnail == null || Thumbnail.Size == 0 || Thumbnail.OriginalHeight == 0 || Thumbnail.OriginalWidth == 0))
                            {
                                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                                {
                                    item.CustomIconData = await Thumbnail.ToByteArrayAsync();
                                    item.FileImage = await item.CustomIconData.ToBitmapAsync();
                                    item.LoadUnknownTypeGlyph = false;
                                    item.LoadWebShortcutGlyph = false;
                                    item.LoadFileIcon = true;
                                }, Windows.System.DispatcherQueuePriority.Low);
                            }

                            var overlayInfo = await FileThumbnailHelper.LoadOverlayAsync(item.ItemPath);
                            if (overlayInfo != null)
                            {
                                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                                {
                                    item.IconOverlay = await overlayInfo.ToBitmapAsync();
                                }, Windows.System.DispatcherQueuePriority.Low);
                            }
                        }
                    }
                }

                if (!item.LoadFileIcon)
                {
                    var iconInfo = await FileThumbnailHelper.LoadIconAndOverlayAsync(item.ItemPath, thumbnailSize);
                    if (iconInfo.IconData != null)
                    {
                        await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                        {
                            item.FileImage = await iconInfo.IconData.ToBitmapAsync();
                            item.CustomIconData = iconInfo.IconData;
                            item.LoadFileIcon = true;
                            item.LoadUnknownTypeGlyph = false;
                            item.LoadWebShortcutGlyph = false;
                        }, Windows.System.DispatcherQueuePriority.Low);
                    }

                    if (iconInfo.OverlayData != null)
                    {
                        await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                        {
                            item.IconOverlay = await iconInfo.OverlayData.ToBitmapAsync();
                        }, Windows.System.DispatcherQueuePriority.Low);
                    }
                }
            }
            else
            {
                if (!forceReload && item.CustomIconData != null)
                {
                    await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                    {
                        item.FileImage = await item.CustomIconData.ToBitmapAsync();
                        item.LoadUnknownTypeGlyph = false;
                        item.LoadWebShortcutGlyph = false;
                        item.LoadFolderGlyph = false;
                        item.LoadFileIcon = true;
                    }, Windows.System.DispatcherQueuePriority.Low);
                }
                else
                {
                    if (!item.IsShortcutItem && !item.IsHiddenItem && !item.ItemPath.StartsWith("ftp:"))
                    {
                        var matchingStorageFolder = (StorageFolder)matchingStorageItem ?? await GetFolderFromPathAsync(item.ItemPath);
                        if (matchingStorageFolder != null)
                        {
                            using var Thumbnail = await matchingStorageFolder.GetThumbnailAsync(ThumbnailMode.ListView, thumbnailSize, ThumbnailOptions.ReturnOnlyIfCached);
                            if (!(Thumbnail == null || Thumbnail.Size == 0 || Thumbnail.OriginalHeight == 0 || Thumbnail.OriginalWidth == 0))
                            {
                                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                                {
                                    item.CustomIconData = await Thumbnail.ToByteArrayAsync();
                                    item.FileImage = await item.CustomIconData.ToBitmapAsync();
                                    item.LoadUnknownTypeGlyph = false;
                                    item.LoadWebShortcutGlyph = false;
                                    item.LoadFolderGlyph = false;
                                    item.LoadFileIcon = true;
                                }, Windows.System.DispatcherQueuePriority.Low);
                            }

                            var overlayInfo = await FileThumbnailHelper.LoadOverlayAsync(item.ItemPath);
                            if (overlayInfo != null)
                            {
                                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                                {
                                    item.IconOverlay = await overlayInfo.ToBitmapAsync();
                                }, Windows.System.DispatcherQueuePriority.Low);
                            }
                        }
                    }
                }

                if (!item.LoadFileIcon)
                {
                    var iconInfo = await FileThumbnailHelper.LoadIconAndOverlayAsync(item.ItemPath, thumbnailSize);

                    if (iconInfo.IconData != null)
                    {
                        await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                        {
                            item.FileImage = await iconInfo.IconData.ToBitmapAsync();
                            item.CustomIconData = iconInfo.IconData;
                            item.LoadFileIcon = true;
                            item.LoadFolderGlyph = false;
                            item.LoadUnknownTypeGlyph = false;
                            item.LoadWebShortcutGlyph = false;
                        }, Windows.System.DispatcherQueuePriority.Low);
                    }

                    if (iconInfo.OverlayData != null)
                    {
                        await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                        {
                            item.IconOverlay = await iconInfo.OverlayData.ToBitmapAsync();
                        }, Windows.System.DispatcherQueuePriority.Low);
                    }
                }
            }
        }

        // This works for recycle bin as well as GetFileFromPathAsync/GetFolderFromPathAsync work
        // for file inside the recycle bin (but not on the recycle bin folder itself)
        public async Task LoadExtendedItemProperties(ListedItem item, uint thumbnailSize = 20)
        {
            // Don't load extended item properties while enumeration is in progress to improve enumeration speed
            if (IsLoadingItems)
            {
                itemLoadQueue.Add((item, thumbnailSize));
                return;
            }

            await Task.Run(async () =>
            {
                if (item == null)
                {
                    return;
                }

                try
                {
                    await loadExtendedPropsSemaphore.WaitAsync(loadPropsCTS.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var wasSyncStatusLoaded = false;
                ImageSource groupImage = null;
                bool loadGroupHeaderInfo = false;
                GroupedCollection<ListedItem> gp = null;
                try
                {
                    bool isFileTypeGroupMode = folderSettings.DirectoryGroupOption == GroupOption.FileType;
                    StorageFile matchingStorageFile = null;

                    if (item.Key != null && FilesAndFolders.IsGrouped && FilesAndFolders.GetExtendedGroupHeaderInfo != null)
                    {
                        gp = FilesAndFolders.GroupedCollection.Where(x => x.Model.Key == item.Key).FirstOrDefault();
                        loadGroupHeaderInfo = !(gp is null) && !gp.Model.Initialized && !(gp.GetExtendedGroupHeaderInfo is null);
                    }

                    if (item.IsLibraryItem || item.PrimaryItemAttribute == StorageItemTypes.File)
                    {
                        if (!item.IsShortcutItem && !item.IsHiddenItem && !item.ItemPath.StartsWith("ftp:"))
                        {
                            matchingStorageFile = await GetFileFromPathAsync(item.ItemPath);
                            if (matchingStorageFile != null)
                            {
                                await LoadItemThumbnail(item, thumbnailSize, matchingStorageFile, true);
                                var syncStatus = await CheckCloudDriveSyncStatusAsync(matchingStorageFile);
                                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
                                {
                                    item.FolderRelativeId = matchingStorageFile.FolderRelativeId;
                                    item.ItemType = matchingStorageFile.DisplayType;
                                    item.SyncStatusUI = CloudDriveSyncStatusUI.FromCloudDriveSyncStatus(syncStatus);
                                }, Windows.System.DispatcherQueuePriority.Low);
                                wasSyncStatusLoaded = true;
                            }
                        }
                        if (!wasSyncStatusLoaded)
                        {
                            await LoadItemThumbnail(item, thumbnailSize, null, true);
                        }
                    }
                    else
                    {
                        if (!item.IsShortcutItem && !item.IsHiddenItem && !item.ItemPath.StartsWith("ftp:"))
                        {
                            StorageFolder matchingStorageItem = await GetFolderFromPathAsync(item.ItemPath);
                            if (matchingStorageItem != null)
                            {
                                await LoadItemThumbnail(item, thumbnailSize, matchingStorageFile, true);
                                if (matchingStorageItem.DisplayName != item.ItemName && !matchingStorageItem.DisplayName.StartsWith("$R"))
                                {
                                    await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
                                    {
                                        item.ItemName = matchingStorageItem.DisplayName;
                                    });
                                    await fileListCache.SaveFileDisplayNameToCache(item.ItemPath, matchingStorageItem.DisplayName);
                                    if (folderSettings.DirectorySortOption == SortOption.Name && !isLoadingItems)
                                    {
                                        await OrderFilesAndFoldersAsync();
                                        await ApplySingleFileChangeAsync(item);
                                    }
                                }

                                var syncStatus = await CheckCloudDriveSyncStatusAsync(matchingStorageItem);
                                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
                                {
                                    item.FolderRelativeId = matchingStorageItem.FolderRelativeId;
                                    item.ItemType = matchingStorageItem.DisplayType;
                                    item.SyncStatusUI = CloudDriveSyncStatusUI.FromCloudDriveSyncStatus(syncStatus);
                                }, Windows.System.DispatcherQueuePriority.Low);
                                wasSyncStatusLoaded = true;
                            }
                        }
                        if (!wasSyncStatusLoaded)
                        {
                            await LoadItemThumbnail(item, thumbnailSize, null, true);
                        }
                    }

                    if (loadGroupHeaderInfo && isFileTypeGroupMode)
                    {
                        groupImage = await GetItemTypeGroupIcon(item, matchingStorageFile);
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    if (!wasSyncStatusLoaded)
                    {
                        await FilesystemTasks.Wrap(() => CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
                        {
                            item.SyncStatusUI = new CloudDriveSyncStatusUI() { LoadSyncStatus = false }; // Reset cloud sync status icon
                        }, Windows.System.DispatcherQueuePriority.Low));
                    }

                    if (loadGroupHeaderInfo)
                    {
                        await FilesystemTasks.Wrap(() => CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
                        {
                            gp.Model.ImageSource = groupImage;
                            gp.InitializeExtendedGroupHeaderInfoAsync();
                        }));
                    }

                    loadExtendedPropsSemaphore.Release();
                }
            });
        }

        private async Task<ImageSource> GetItemTypeGroupIcon(ListedItem item, StorageFile matchingStorageItem = null)
        {
            ImageSource groupImage = null;
            if (item.PrimaryItemAttribute != StorageItemTypes.Folder)
            {
                (byte[] iconData, byte[] overlayData) headerIconInfo = await FileThumbnailHelper.LoadIconAndOverlayAsync(item.ItemPath, 76);

                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                {
                    if (headerIconInfo.iconData != null && !item.IsShortcutItem)
                    {
                        groupImage = await headerIconInfo.iconData.ToBitmapAsync();
                    }
                }, Windows.System.DispatcherQueuePriority.Low);

                if (!item.IsShortcutItem && !item.IsHiddenItem && !item.ItemPath.StartsWith("ftp:"))
                {
                    if (groupImage == null) // Loading icon from fulltrust process failed
                    {
                        matchingStorageItem ??= await GetFileFromPathAsync(item.ItemPath);

                        if (matchingStorageItem != null)
                        {
                            using var headerThumbnail = await matchingStorageItem.GetThumbnailAsync(ThumbnailMode.DocumentsView, 36, ThumbnailOptions.UseCurrentScale);
                            if (headerThumbnail != null)
                            {
                                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                                {
                                    var bmp = new BitmapImage();
                                    await bmp.SetSourceAsync(headerThumbnail);
                                    groupImage = bmp;
                                });
                            }
                        }
                    }
                }
            }
            // This prevents both the shortcut glyph and folder icon being shown
            else if (!item.IsShortcutItem)
            {
                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() => groupImage = new SvgImageSource(new Uri("ms-appx:///Assets/FolderIcon2.svg"))
                {
                    RasterizePixelHeight = 128,
                    RasterizePixelWidth = 128,
                }, Windows.System.DispatcherQueuePriority.Low);
            }

            return groupImage;
        }

        public void RefreshItems(string previousDir, Action postLoadCallback = null)
        {
            RapidAddItemsToCollectionAsync(WorkingDirectory, previousDir, postLoadCallback);
        }

        private async void RapidAddItemsToCollectionAsync(string path, string previousDir, Action postLoadCallback)
        {
            ItemLoadStatusChanged?.Invoke(this, new ItemLoadStatusChangedEventArgs() { Status = ItemLoadStatusChangedEventArgs.ItemLoadStatus.Starting });

            CancelLoadAndClearFiles();

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                // Only one instance at a time should access this function
                // Wait here until the previous one has ended
                // If we're waiting and a new update request comes through
                // simply drop this instance
                await enumFolderSemaphore.WaitAsync(semaphoreCTS.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                // Drop all the other waiting instances
                semaphoreCTS.Cancel();
                semaphoreCTS = new CancellationTokenSource();

                IsLoadingItems = true;

                filesAndFolders.Clear();
                FilesAndFolders.Clear();

                ItemLoadStatusChanged?.Invoke(this, new ItemLoadStatusChangedEventArgs() { Status = ItemLoadStatusChangedEventArgs.ItemLoadStatus.InProgress });

                Connection ??= await AppServiceConnectionHelper.Instance;

                if (path.ToLower().EndsWith(ShellLibraryItem.EXTENSION))
                {
                    if (App.LibraryManager.TryGetLibrary(path, out LibraryLocationItem library) && !library.IsEmpty)
                    {
                        var libItem = new LibraryItem(library);
                        foreach (var folder in library.Folders)
                        {
                            await RapidAddItemsToCollection(folder, libItem);
                        }
                    }
                }
                else
                {
                    await RapidAddItemsToCollection(path);
                }

                ItemLoadStatusChanged?.Invoke(this, new ItemLoadStatusChangedEventArgs() { Status = ItemLoadStatusChangedEventArgs.ItemLoadStatus.Complete, PreviousDirectory = previousDir, Path = path });
                IsLoadingItems = false;

                AdaptiveLayoutHelpers.PredictLayoutMode(folderSettings, this);
            }
            finally
            {
                enumFolderSemaphore.Release();

                // Load items in the queue after enumeration has completed to improve enumeration speed
                foreach (var item in itemLoadQueue)
                {
                    _ = LoadExtendedItemProperties(item.item, item.size);
                }
                itemLoadQueue.Clear();
            }

            postLoadCallback?.Invoke();
        }

        private async Task RapidAddItemsToCollection(string path, LibraryItem library = null)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            var isRecycleBin = path.StartsWith(AppSettings.RecycleBinPath);
            if (isRecycleBin ||
                path.StartsWith(AppSettings.NetworkFolderPath) ||
                path.StartsWith("ftp:"))
            {
                // Recycle bin and network are enumerated by the fulltrust process
                PageTypeUpdated?.Invoke(this, new PageTypeUpdatedEventArgs() { IsTypeCloudDrive = false, IsTypeRecycleBin = isRecycleBin });
                await EnumerateItemsFromSpecialFolderAsync(path);
            }
            else
            {
                var enumerated = await EnumerateItemsFromStandardFolderAsync(path, folderSettings.GetLayoutType(path, false), addFilesCTS.Token, library);
                IsLoadingItems = false; // Hide progressbar after enumeration
                switch (enumerated)
                {
                    case 0: // Enumerated with FindFirstFileExFromApp
                        // Is folder synced to cloud storage?
                        currentStorageFolder ??= await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderWithPathFromPathAsync(path));
                        var syncStatus = await CheckCloudDriveSyncStatusAsync(currentStorageFolder?.Item);
                        PageTypeUpdated?.Invoke(this, new PageTypeUpdatedEventArgs() { IsTypeCloudDrive = syncStatus != CloudDriveSyncStatus.NotSynced && syncStatus != CloudDriveSyncStatus.Unknown });

                        WatchForDirectoryChanges(path, syncStatus);
                        break;

                    case 1: // Enumerated with StorageFolder
                        PageTypeUpdated?.Invoke(this, new PageTypeUpdatedEventArgs() { IsTypeCloudDrive = false });
                        currentStorageFolder ??= await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderWithPathFromPathAsync(path));
                        WatchForStorageFolderChanges(currentStorageFolder?.Folder);
                        break;

                    case -1: // Enumeration failed
                    default:
                        break;
                }
            }

            if (addFilesCTS.IsCancellationRequested)
            {
                addFilesCTS = new CancellationTokenSource();
                IsLoadingItems = false;
                return;
            }

            stopwatch.Stop();
            Debug.WriteLine($"Loading of items in {path} completed in {stopwatch.ElapsedMilliseconds} milliseconds.\n");
        }

        public void CloseWatcher()
        {
            if (aWatcherAction != null)
            {
                aWatcherAction?.Cancel();

                if (aWatcherAction.Status != AsyncStatus.Started)
                {
                    aWatcherAction = null;  // Prevent duplicate execution of this block
                    Debug.WriteLine("watcher canceled");
                    CancelIoEx(hWatchDir, IntPtr.Zero);
                    Debug.WriteLine("watcher handle closed");
                    CloseHandle(hWatchDir);
                }
            }
            if (watchedItemsOperation != null)
            {
                itemQueryResult.ContentsChanged -= ItemQueryResult_ContentsChanged;
                watchedItemsOperation?.Cancel();
                watchedItemsOperation = null;
            }
        }

        public async Task EnumerateItemsFromSpecialFolderAsync(string path)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string returnformat = Enum.Parse<TimeStyle>(localSettings.Values[Constants.LocalSettings.DateTimeFormat].ToString()) == TimeStyle.Application ? "D" : "g";

            CurrentFolder = new ListedItem(null, returnformat)
            {
                PrimaryItemAttribute = StorageItemTypes.Folder,
                ItemPropertiesInitialized = true,
                ItemName = ApplicationData.Current.LocalSettings.Values.Get("RecycleBin_Title", "Recycle Bin"),
                ItemDateModifiedReal = DateTimeOffset.Now, // Fake for now
                ItemDateCreatedReal = DateTimeOffset.Now, // Fake for now
                ItemType = "FileFolderListItem".GetLocalized(),
                LoadFolderGlyph = true,
                FileImage = null,
                LoadFileIcon = false,
                ItemPath = AppSettings.RecycleBinPath,
                LoadUnknownTypeGlyph = false,
                FileSize = null,
                FileSizeBytes = 0
            };

            if (Connection != null)
            {
                await Task.Run(async () =>
                {
                    var sampler = new IntervalSampler(500);
                    var value = new ValueSet();
                    value.Add("Arguments", "ShellFolder");
                    value.Add("action", "Enumerate");
                    value.Add("folder", path);
                    // Send request to fulltrust process to enumerate recyclebin items
                    var (status, response) = await Connection.SendMessageForResponseAsync(value);
                    // If the request was canceled return now
                    if (addFilesCTS.IsCancellationRequested)
                    {
                        return;
                    }
                    if (status == AppServiceResponseStatus.Success
                        && response.ContainsKey("Enumerate"))
                    {
                        var folderContentsList = JsonConvert.DeserializeObject<List<ShellFileItem>>((string)response["Enumerate"]);
                        for (int count = 0; count < folderContentsList.Count; count++)
                        {
                            var item = folderContentsList[count];
                            var listedItem = AddFileOrFolderFromShellFile(item, returnformat);
                            if (listedItem != null)
                            {
                                filesAndFolders.Add(listedItem);
                            }
                            if (count == 32 || count == folderContentsList.Count - 1 || sampler.CheckNow())
                            {
                                await OrderFilesAndFoldersAsync();
                                await ApplyFilesAndFoldersChangesAsync();
                            }
                        }
                    }
                });
            }
        }

        public async Task<int> EnumerateItemsFromStandardFolderAsync(string path, Type sourcePageType, CancellationToken cancellationToken, LibraryItem library = null)
        {
            // Flag to use FindFirstFileExFromApp or StorageFolder enumeration
            bool enumFromStorageFolder =
                path == App.CloudDrivesManager.Drives.FirstOrDefault(x => x.Text == "Box")?.Path?.TrimEnd('\\'); // Use storage folder for Box Drive (#4629)

            StorageFolder rootFolder = null;

            if (FolderHelpers.CheckFolderAccessWithWin32(path))
            {
                // Will enumerate with FindFirstFileExFromApp, rootFolder only used for Bitlocker
                currentStorageFolder = null;
            }
            else if (workingRoot != null)
            {
                var res = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderWithPathFromPathAsync(path, workingRoot, currentStorageFolder));
                if (!res)
                {
                    return -1;
                }
                currentStorageFolder = res.Result;
                rootFolder = currentStorageFolder.Folder;
                enumFromStorageFolder = true;
            }
            else
            {
                var res = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderWithPathFromPathAsync(path));
                if (res)
                {
                    currentStorageFolder = res.Result;
                    rootFolder = currentStorageFolder.Folder;
                }
                else if (res == FileSystemStatusCode.Unauthorized)
                {
                    //TODO: proper dialog
                    await DialogDisplayHelper.ShowDialogAsync(
                        "AccessDeniedDeleteDialog/Title".GetLocalized(),
                        "SubDirectoryAccessDenied".GetLocalized());
                    return -1;
                }
                else if (res == FileSystemStatusCode.NotFound)
                {
                    await DialogDisplayHelper.ShowDialogAsync(
                        "FolderNotFoundDialog/Title".GetLocalized(),
                        "FolderNotFoundDialog/Text".GetLocalized());
                    return -1;
                }
                else
                {
                    await DialogDisplayHelper.ShowDialogAsync("DriveUnpluggedDialog/Title".GetLocalized(), res.ErrorCode.ToString());
                    return -1;
                }
            }

            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string returnformat = Enum.Parse<TimeStyle>(localSettings.Values[Constants.LocalSettings.DateTimeFormat].ToString()) == TimeStyle.Application ? "D" : "g";

            if (Path.IsPathRooted(path) && Path.GetPathRoot(path) == path)
            {
                rootFolder ??= await FilesystemTasks.Wrap(() => StorageFolder.GetFolderFromPathAsync(path).AsTask());
                if (await FolderHelpers.CheckBitlockerStatusAsync(rootFolder, WorkingDirectory))
                {
                    if (Connection != null)
                    {
                        var value = new ValueSet();
                        value.Add("Arguments", "InvokeVerb");
                        value.Add("FilePath", Path.GetPathRoot(path));
                        value.Add("Verb", "unlock-bde");
                        _ = await Connection.SendMessageForResponseAsync(value);
                    }
                }
            }

            if (enumFromStorageFolder)
            {
                var basicProps = await rootFolder.GetBasicPropertiesAsync();
                var currentFolder = library ?? new ListedItem(rootFolder.FolderRelativeId, returnformat)
                {
                    PrimaryItemAttribute = StorageItemTypes.Folder,
                    ItemPropertiesInitialized = true,
                    ItemName = rootFolder.DisplayName,
                    ItemDateModifiedReal = basicProps.DateModified,
                    ItemType = rootFolder.DisplayType,
                    LoadFolderGlyph = true,
                    FileImage = null,
                    LoadFileIcon = false,
                    ItemPath = string.IsNullOrEmpty(rootFolder.Path) ? currentStorageFolder.Path : rootFolder.Path,
                    LoadUnknownTypeGlyph = false,
                    FileSize = null,
                    FileSizeBytes = 0,
                };
                if (library == null)
                {
                    var extraProps = await basicProps.RetrievePropertiesAsync(new[] { "System.DateCreated" });
                    if (DateTimeOffset.TryParse(extraProps["System.DateCreated"] as string, out var dateCreated))
                    {
                        currentFolder.ItemDateCreatedReal = dateCreated;
                    }
                }
                CurrentFolder = currentFolder;
                await EnumFromStorageFolderAsync(path, currentFolder, rootFolder, currentStorageFolder, sourcePageType, cancellationToken);
                return 1;
            }
            else
            {
                (IntPtr hFile, WIN32_FIND_DATA findData) = await Task.Run(() =>
                {
                    FINDEX_INFO_LEVELS findInfoLevel = FINDEX_INFO_LEVELS.FindExInfoBasic;
                    int additionalFlags = FIND_FIRST_EX_LARGE_FETCH;
                    IntPtr hFileTsk = FindFirstFileExFromApp(path + "\\*.*", findInfoLevel, out WIN32_FIND_DATA findDataTsk, FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero,
                        additionalFlags);
                    return (hFileTsk, findDataTsk);
                }).WithTimeoutAsync(TimeSpan.FromSeconds(5));

                var itemModifiedDate = DateTime.Now;
                var itemCreatedDate = DateTime.Now;
                try
                {
                    FileTimeToSystemTime(ref findData.ftLastWriteTime, out var systemModifiedTimeOutput);
                    itemModifiedDate = new DateTime(
                        systemModifiedTimeOutput.Year, systemModifiedTimeOutput.Month, systemModifiedTimeOutput.Day,
                        systemModifiedTimeOutput.Hour, systemModifiedTimeOutput.Minute, systemModifiedTimeOutput.Second, systemModifiedTimeOutput.Milliseconds,
                        DateTimeKind.Utc);

                    FileTimeToSystemTime(ref findData.ftCreationTime, out SYSTEMTIME systemCreatedTimeOutput);
                    itemCreatedDate = new DateTime(
                        systemCreatedTimeOutput.Year, systemCreatedTimeOutput.Month, systemCreatedTimeOutput.Day,
                        systemCreatedTimeOutput.Hour, systemCreatedTimeOutput.Minute, systemCreatedTimeOutput.Second, systemCreatedTimeOutput.Milliseconds,
                        DateTimeKind.Utc);
                }
                catch (ArgumentException) { }

                bool isHidden = (((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden);
                double opacity = 1;

                if (isHidden)
                {
                    opacity = Constants.UI.DimItemOpacity;
                }

                var currentFolder = library ?? new ListedItem(null, returnformat)
                {
                    PrimaryItemAttribute = StorageItemTypes.Folder,
                    ItemPropertiesInitialized = true,
                    ItemName = Path.GetFileName(path.TrimEnd('\\')),
                    ItemDateModifiedReal = itemModifiedDate,
                    ItemDateCreatedReal = itemCreatedDate,
                    ItemType = "FileFolderListItem".GetLocalized(),
                    LoadFolderGlyph = true,
                    FileImage = null,
                    IsHiddenItem = isHidden,
                    Opacity = opacity,
                    LoadFileIcon = false,
                    ItemPath = path,
                    LoadUnknownTypeGlyph = false,
                    FileSize = null,
                    FileSizeBytes = 0,
                };
                CurrentFolder = currentFolder;

                if (hFile == IntPtr.Zero)
                {
                    await DialogDisplayHelper.ShowDialogAsync("DriveUnpluggedDialog/Title".GetLocalized(), "");
                    return -1;
                }
                else if (hFile.ToInt64() == -1)
                {
                    await EnumFromStorageFolderAsync(path, currentFolder, rootFolder, currentStorageFolder, sourcePageType, cancellationToken);
                    return 1;
                }
                else
                {
                    List<ListedItem> fileList = await Win32StorageEnumerator.ListEntries(path, returnformat, hFile, findData, Connection, cancellationToken, -1, intermediateAction: async (intermediateList) =>
                    {
                        filesAndFolders.AddRange(intermediateList);
                        await OrderFilesAndFoldersAsync();
                        await ApplyFilesAndFoldersChangesAsync();
                    });

                    filesAndFolders.AddRange(fileList);
                    await OrderFilesAndFoldersAsync();
                    await ApplyFilesAndFoldersChangesAsync();
                    return 0;
                }
            }
        }

        private async Task EnumFromStorageFolderAsync(string path, ListedItem currentFolder, StorageFolder rootFolder, IStorageFolder currentStorageFolder, Type sourcePageType, CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string returnformat = Enum.Parse<TimeStyle>(localSettings.Values[Constants.LocalSettings.DateTimeFormat].ToString()) == TimeStyle.Application ? "D" : "g";

            List<ListedItem> finalList = await UniversalStorageEnumerator.ListEntries(
                rootFolder,
                currentStorageFolder,
                returnformat,
                sourcePageType,
                cancellationToken,
                -1,
                async (intermediateList) =>
            {
                filesAndFolders.AddRange(intermediateList);
                await OrderFilesAndFoldersAsync();
                await ApplyFilesAndFoldersChangesAsync();
            });
            filesAndFolders.AddRange(finalList);
            await OrderFilesAndFoldersAsync();
            await ApplyFilesAndFoldersChangesAsync();

            stopwatch.Stop();
            Debug.WriteLine($"Enumerating items in {path} (device) completed in {stopwatch.ElapsedMilliseconds} milliseconds.\n");
        }

        private async Task<CloudDriveSyncStatus> CheckCloudDriveSyncStatusAsync(IStorageItem item)
        {
            int? syncStatus = null;
            if (item is StorageFile)
            {
                IDictionary<string, object> extraProperties = await ((StorageFile)item).Properties.RetrievePropertiesAsync(new string[] { "System.FilePlaceholderStatus" });
                syncStatus = (int?)(uint?)extraProperties["System.FilePlaceholderStatus"];
            }
            else if (item is StorageFolder)
            {
                IDictionary<string, object> extraProperties = await ((StorageFolder)item).Properties.RetrievePropertiesAsync(new string[] { "System.FilePlaceholderStatus", "System.FileOfflineAvailabilityStatus" });
                syncStatus = (int?)(uint?)extraProperties["System.FileOfflineAvailabilityStatus"];
                // If no FileOfflineAvailabilityStatus, check FilePlaceholderStatus
                syncStatus = syncStatus ?? (int?)(uint?)extraProperties["System.FilePlaceholderStatus"];
            }
            if (syncStatus == null || !Enum.IsDefined(typeof(CloudDriveSyncStatus), syncStatus))
            {
                return CloudDriveSyncStatus.Unknown;
            }
            return (CloudDriveSyncStatus)syncStatus;
        }

        private StorageItemQueryResult itemQueryResult;

        private IAsyncOperation<IReadOnlyList<IStorageItem>> watchedItemsOperation;

        private async void WatchForStorageFolderChanges(StorageFolder rootFolder)
        {
            if (rootFolder == null)
            {
                return;
            }
            await Task.Run(() =>
            {
                var options = new QueryOptions()
                {
                    FolderDepth = FolderDepth.Shallow,
                    IndexerOption = IndexerOption.OnlyUseIndexerAndOptimizeForIndexedProperties
                };
                options.SetPropertyPrefetch(PropertyPrefetchOptions.None, null);
                options.SetThumbnailPrefetch(ThumbnailMode.ListView, 0, ThumbnailOptions.ReturnOnlyIfCached);
                itemQueryResult = rootFolder.CreateItemQueryWithOptions(options);
                itemQueryResult.ContentsChanged += ItemQueryResult_ContentsChanged;
                watchedItemsOperation = itemQueryResult.GetItemsAsync(0, 1); // Just get one item to start getting notifications
            });
        }

        private async void ItemQueryResult_ContentsChanged(IStorageQueryResultBase sender, object args)
        {
            //query options have to be reapplied otherwise old results are returned
            var options = new QueryOptions()
            {
                FolderDepth = FolderDepth.Shallow,
                IndexerOption = IndexerOption.OnlyUseIndexerAndOptimizeForIndexedProperties
            };
            options.SetPropertyPrefetch(PropertyPrefetchOptions.None, null);
            options.SetThumbnailPrefetch(ThumbnailMode.ListView, 0, ThumbnailOptions.ReturnOnlyIfCached);

            sender.ApplyNewQueryOptions(options);

            await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
            {
                RefreshItems(null);
            });
        }

        private void WatchForDirectoryChanges(string path, CloudDriveSyncStatus syncStatus)
        {
            Debug.WriteLine("WatchForDirectoryChanges: {0}", path);
            hWatchDir = NativeFileOperationsHelper.CreateFileFromApp(path, 1, 1 | 2 | 4,
                IntPtr.Zero, 3, (uint)NativeFileOperationsHelper.File_Attributes.BackupSemantics | (uint)NativeFileOperationsHelper.File_Attributes.Overlapped, IntPtr.Zero);
            if (hWatchDir.ToInt64() == -1)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _ = Windows.System.Threading.ThreadPool.RunAsync((x) => ProcessOperationQueue(cts.Token));

            aWatcherAction = Windows.System.Threading.ThreadPool.RunAsync((x) =>
            {
                byte[] buff = new byte[4096];
                var rand = Guid.NewGuid();
                buff = new byte[4096];
                int notifyFilters = FILE_NOTIFY_CHANGE_DIR_NAME | FILE_NOTIFY_CHANGE_FILE_NAME;

                if (syncStatus != CloudDriveSyncStatus.NotSynced && syncStatus != CloudDriveSyncStatus.Unknown)
                {
                    notifyFilters |= FILE_NOTIFY_CHANGE_ATTRIBUTES;
                }

                OVERLAPPED overlapped = new OVERLAPPED();
                overlapped.hEvent = CreateEvent(IntPtr.Zero, false, false, null);
                const uint INFINITE = 0xFFFFFFFF;

                while (x.Status != AsyncStatus.Canceled)
                {
                    unsafe
                    {
                        fixed (byte* pBuff = buff)
                        {
                            ref var notifyInformation = ref Unsafe.As<byte, FILE_NOTIFY_INFORMATION>(ref buff[0]);
                            if (x.Status != AsyncStatus.Canceled)
                            {
                                NativeDirectoryChangesHelper.ReadDirectoryChangesW(hWatchDir, pBuff,
                                4096, false,
                                notifyFilters, null,
                                ref overlapped, null);
                            }
                            else
                            {
                                break;
                            }

                            Debug.WriteLine("waiting: {0}", rand);
                            if (x.Status == AsyncStatus.Canceled)
                            {
                                break;
                            }
                            var rc = WaitForSingleObjectEx(overlapped.hEvent, INFINITE, true);
                            Debug.WriteLine("wait done: {0}", rand);

                            uint offset = 0;
                            ref var notifyInfo = ref Unsafe.As<byte, FILE_NOTIFY_INFORMATION>(ref buff[offset]);
                            if (x.Status == AsyncStatus.Canceled)
                            {
                                break;
                            }

                            do
                            {
                                notifyInfo = ref Unsafe.As<byte, FILE_NOTIFY_INFORMATION>(ref buff[offset]);
                                string FileName = null;
                                unsafe
                                {
                                    fixed (char* name = notifyInfo.FileName)
                                    {
                                        FileName = Path.Combine(path, new string(name, 0, (int)notifyInfo.FileNameLength / 2));
                                    }
                                }

                                uint action = notifyInfo.Action;

                                Debug.WriteLine("action: {0}", action);

                                operationQueue.Enqueue((action, FileName));

                                operationEvent.Set();

                                offset += notifyInfo.NextEntryOffset;
                            } while (notifyInfo.NextEntryOffset != 0 && x.Status != AsyncStatus.Canceled);

                            //ResetEvent(overlapped.hEvent);
                            Debug.WriteLine("Task running...");
                        }
                    }
                }
                CloseHandle(overlapped.hEvent);
                operationQueue.Clear();
                cts.Cancel();
                Debug.WriteLine("aWatcherAction done: {0}", rand);
            });

            Debug.WriteLine("Task exiting...");
        }

        private async void ProcessOperationQueue(CancellationToken cancellationToken)
        {
            ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
            string returnformat = Enum.Parse<TimeStyle>(localSettings.Values[Constants.LocalSettings.DateTimeFormat].ToString()) == TimeStyle.Application ? "D" : "g";

            const uint FILE_ACTION_ADDED = 0x00000001;
            const uint FILE_ACTION_REMOVED = 0x00000002;
            const uint FILE_ACTION_MODIFIED = 0x00000003;
            const uint FILE_ACTION_RENAMED_OLD_NAME = 0x00000004;
            const uint FILE_ACTION_RENAMED_NEW_NAME = 0x00000005;

            var sampler = new IntervalSampler(500);
            bool anyEdits = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (operationEvent.Wait(500, cancellationToken))
                    {
                        operationEvent.Reset();
                        while (operationQueue.TryDequeue(out var operation))
                        {
                            if (cancellationToken.IsCancellationRequested) break;
                            try
                            {
                                switch (operation.Action)
                                {
                                    case FILE_ACTION_ADDED:
                                    case FILE_ACTION_RENAMED_NEW_NAME:
                                        await AddFileOrFolderAsync(operation.FileName, returnformat);
                                        anyEdits = true;
                                        break;

                                    case FILE_ACTION_MODIFIED:
                                        await UpdateFileOrFolderAsync(operation.FileName);
                                        break;

                                    case FILE_ACTION_REMOVED:
                                    case FILE_ACTION_RENAMED_OLD_NAME:
                                        await RemoveFileOrFolderAsync(operation.FileName);
                                        anyEdits = true;
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                App.Logger.Warn(ex, ex.Message);
                            }

                            if (anyEdits && sampler.CheckNow())
                            {
                                await OrderFilesAndFoldersAsync();
                                await ApplyFilesAndFoldersChangesAsync();
                                anyEdits = false;
                            }
                        }
                    }

                    if (anyEdits && sampler.CheckNow())
                    {
                        await OrderFilesAndFoldersAsync();
                        await ApplyFilesAndFoldersChangesAsync();
                        anyEdits = false;
                    }
                }
            }
            catch
            {
                // Prevent disposed cancellation token
            }
        }

        public ListedItem AddFileOrFolderFromShellFile(ShellFileItem item, string dateReturnFormat = null)
        {
            if (dateReturnFormat == null)
            {
                ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;
                dateReturnFormat = Enum.Parse<TimeStyle>(localSettings.Values[Constants.LocalSettings.DateTimeFormat].ToString()) == TimeStyle.Application ? "D" : "g";
            }

            if (item.IsFolder)
            {
                // Folder
                return new RecycleBinItem(null, dateReturnFormat)
                {
                    PrimaryItemAttribute = StorageItemTypes.Folder,
                    ItemName = item.FileName,
                    ItemDateModifiedReal = item.ModifiedDate,
                    ItemDateCreatedReal = item.CreatedDate,
                    ItemDateDeletedReal = item.RecycleDate,
                    ItemType = item.FileType,
                    IsHiddenItem = false,
                    Opacity = 1,
                    LoadFolderGlyph = true,
                    FileImage = null,
                    LoadFileIcon = false,
                    ItemPath = item.RecyclePath, // this is the true path on disk so other stuff can work as is
                    ItemOriginalPath = item.FilePath,
                    LoadUnknownTypeGlyph = false,
                    FileSize = null,
                    FileSizeBytes = 0,
                    //FolderTooltipText = tooltipString,
                };
            }
            else
            {
                // File
                string itemName;
                if (App.AppSettings.ShowFileExtensions && !item.FileName.EndsWith(".lnk") && !item.FileName.EndsWith(".url"))
                {
                    itemName = item.FileName; // never show extension for shortcuts
                }
                else
                {
                    if (item.FileName.StartsWith("."))
                    {
                        itemName = item.FileName; // Always show full name for dotfiles.
                    }
                    else
                    {
                        itemName = Path.GetFileNameWithoutExtension(item.FileName);
                    }
                }

                string itemFileExtension = null;
                if (item.FileName.Contains('.'))
                {
                    itemFileExtension = Path.GetExtension(item.FileName);
                }
                return new RecycleBinItem(null, dateReturnFormat)
                {
                    PrimaryItemAttribute = StorageItemTypes.File,
                    FileExtension = itemFileExtension,
                    LoadUnknownTypeGlyph = true,
                    FileImage = null,
                    LoadFileIcon = false,
                    LoadFolderGlyph = false,
                    IsHiddenItem = false,
                    Opacity = 1,
                    ItemName = itemName,
                    ItemDateModifiedReal = item.ModifiedDate,
                    ItemDateCreatedReal = item.CreatedDate,
                    ItemDateDeletedReal = item.RecycleDate,
                    ItemType = item.FileType,
                    ItemPath = item.RecyclePath, // this is the true path on disk so other stuff can work as is
                    ItemOriginalPath = item.FilePath,
                    FileSize = item.FileSize,
                    FileSizeBytes = (long)item.FileSizeBytes
                };
            }
        }

        private async Task AddFileOrFolderAsync(ListedItem item)
        {
            try
            {
                await enumFolderSemaphore.WaitAsync(semaphoreCTS.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (item != null)
                {
                    filesAndFolders.Add(item);
                }
            }
            finally
            {
                enumFolderSemaphore.Release();
            }
        }

        private async Task AddFileOrFolderAsync(string fileOrFolderPath, string dateReturnFormat)
        {
            FINDEX_INFO_LEVELS findInfoLevel = FINDEX_INFO_LEVELS.FindExInfoBasic;
            int additionalFlags = FIND_FIRST_EX_CASE_SENSITIVE;

            IntPtr hFile = FindFirstFileExFromApp(fileOrFolderPath, findInfoLevel, out WIN32_FIND_DATA findData, FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero,
                                                  additionalFlags);
            if (hFile.ToInt64() == -1)
            {
                // If we cannot find the file (probably since it doesn't exist anymore)
                // simply exit without adding it
                return;
            }

            FindClose(hFile);

            var isSystem = ((FileAttributes)findData.dwFileAttributes & FileAttributes.System) == FileAttributes.System;
            var isHidden = ((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden;
            if (isHidden && (!AppSettings.AreHiddenItemsVisible || (isSystem && AppSettings.AreSystemItemsHidden)))
            {
                // Do not add to file list if hidden/system attribute is set and system/hidden file are not to be shown
                return;
            }

            ListedItem listedItem;
            if ((findData.dwFileAttributes & 0x10) > 0) // FILE_ATTRIBUTE_DIRECTORY
            {
                listedItem = await Win32StorageEnumerator.GetFolder(findData, Directory.GetParent(fileOrFolderPath).FullName, dateReturnFormat, addFilesCTS.Token);
            }
            else
            {
                listedItem = await Win32StorageEnumerator.GetFile(findData, Directory.GetParent(fileOrFolderPath).FullName, dateReturnFormat, Connection, addFilesCTS.Token);
            }

            if (listedItem != null)
            {
                filesAndFolders.Add(listedItem);
            }
        }

        private async Task UpdateFileOrFolderAsync(ListedItem item)
        {
            IStorageItem storageItem = null;
            if (item.PrimaryItemAttribute == StorageItemTypes.File)
            {
                storageItem = (await GetFileFromPathAsync(item.ItemPath)).Result;
            }
            else if (item.PrimaryItemAttribute == StorageItemTypes.Folder)
            {
                storageItem = (await GetFolderFromPathAsync(item.ItemPath)).Result;
            }
            if (storageItem != null)
            {
                var syncStatus = await CheckCloudDriveSyncStatusAsync(storageItem);
                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
                {
                    item.SyncStatusUI = CloudDriveSyncStatusUI.FromCloudDriveSyncStatus(syncStatus);
                });
            }
        }

        private async Task UpdateFileOrFolderAsync(string path)
        {
            try
            {
                await enumFolderSemaphore.WaitAsync(semaphoreCTS.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var matchingItem = filesAndFolders.FirstOrDefault(x => x.ItemPath.Equals(path, StringComparison.OrdinalIgnoreCase));

                if (matchingItem != null)
                {
                    await UpdateFileOrFolderAsync(matchingItem);
                }
            }
            finally
            {
                enumFolderSemaphore.Release();
            }
        }

        private async Task RemoveFileOrFolderAsync(ListedItem item)
        {
            filesAndFolders.Remove(item);
            await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() =>
            {
                App.JumpList.RemoveFolder(item.ItemPath);
            });
        }

        public async Task<ListedItem> RemoveFileOrFolderAsync(string path)
        {
            try
            {
                await enumFolderSemaphore.WaitAsync(semaphoreCTS.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            try
            {
                var matchingItem = filesAndFolders.FirstOrDefault(x => x.ItemPath.Equals(path, StringComparison.OrdinalIgnoreCase));

                if (matchingItem != null)
                {
                    await RemoveFileOrFolderAsync(matchingItem);
                    return matchingItem;
                }
            }
            finally
            {
                enumFolderSemaphore.Release();
            }
            return null;
        }

        public async Task AddSearchResultsToCollection(ObservableCollection<ListedItem> searchItems, string currentSearchPath)
        {
            filesAndFolders.Clear();
            foreach (ListedItem li in searchItems)
            {
                filesAndFolders.Add(li);
            }
            await OrderFilesAndFoldersAsync();
            await ApplyFilesAndFoldersChangesAsync();
        }

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            CancelLoadAndClearFiles();
            if (Connection != null)
            {
                Connection.RequestReceived -= Connection_RequestReceived;
            }
            AppServiceConnectionHelper.ConnectionChanged -= AppServiceConnectionHelper_ConnectionChanged;
        }
    }

    public class PageTypeUpdatedEventArgs
    {
        public bool IsTypeCloudDrive { get; set; }
        public bool IsTypeRecycleBin { get; set; }
    }

    public class WorkingDirectoryModifiedEventArgs : EventArgs
    {
        public string Path { get; set; }

        public string Name { get; set; }

        public bool IsLibrary { get; set; }
    }

    public class ItemLoadStatusChangedEventArgs : EventArgs
    {
        public enum ItemLoadStatus
        {
            Starting,
            InProgress,
            Complete
        }

        public ItemLoadStatus Status { get; set; }

        /// <summary>
        /// This property may not be provided consistently if Status is not Complete
        /// </summary>
        public string PreviousDirectory { get; set; }

        /// <summary>
        /// This property may not be provided consistently if Status is not Complete
        /// </summary>
        public string Path { get; set; }
    }
}