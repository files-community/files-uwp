using Files.Common;
using Files.Enums;
using Files.Extensions;
using Files.Filesystem.FilesystemHistory;
using Files.Helpers;
using Files.Interacts;
using Microsoft.Toolkit.Uwp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Xaml.Controls;
using FileAttributes = System.IO.FileAttributes;

namespace Files.Filesystem
{
    public enum ImpossibleActionResponseTypes
    {
        Skip,
        Abort
    }

    public class FilesystemOperations : IFilesystemOperations
    {
        #region Private Members

        private IShellPage associatedInstance;

        private ItemManipulationModel itemManipulationModel => associatedInstance.SlimContentPage?.ItemManipulationModel;

        private RecycleBinHelpers recycleBinHelpers;

        #endregion Private Members

        #region Constructor

        public FilesystemOperations(IShellPage associatedInstance)
        {
            this.associatedInstance = associatedInstance;
            recycleBinHelpers = new RecycleBinHelpers();
        }

        #endregion Constructor

        #region IFilesystemOperations

        public async Task<(IStorageHistory, IStorageItem)> CreateAsync(IStorageItemWithPath source, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            IStorageItem item = null;
            try
            {
                switch (source.ItemType)
                {
                    case FilesystemItemType.File:
                        {
                            var newEntryInfo = await RegistryHelper.GetNewContextMenuEntryForType(Path.GetExtension(source.Path));
                            if (newEntryInfo == null)
                            {
                                StorageFolder folder = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(Path.GetDirectoryName(source.Path));
                                item = await folder.CreateFileAsync(Path.GetFileName(source.Path));
                            }
                            else
                            {
                                item = (await newEntryInfo.Create(source.Path, associatedInstance)).Result;
                            }

                            break;
                        }

                    case FilesystemItemType.Directory:
                        {
                            StorageFolder folder = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(Path.GetDirectoryName(source.Path));
                            item = await folder.CreateFolderAsync(Path.GetFileName(source.Path));

                            break;
                        }

                    case FilesystemItemType.Symlink:
                        {
                            Debugger.Break();
                            throw new NotImplementedException();
                        }

                    default:
                        Debugger.Break();
                        break;
                }

                errorCode?.Report(FileSystemStatusCode.Success);
                return (new StorageHistory(FileOperationType.CreateNew, source.CreateEnumerable(), null), item);
            }
            catch (Exception e)
            {
                errorCode?.Report(FilesystemTasks.GetErrorCode(e));
                return (null, null);
            }
        }

        public async Task<IStorageHistory> CopyAsync(IStorageItem source,
                                                     string destination,
                                                     NameCollisionOption collision,
                                                     IProgress<float> progress,
                                                     IProgress<FileSystemStatusCode> errorCode,
                                                     CancellationToken cancellationToken)
        {
            return await CopyAsync(source.FromStorageItem(),
                                                    destination,
                                                    collision,
                                                    progress,
                                                    errorCode,
                                                    cancellationToken);
        }

        public async Task<IStorageHistory> CopyAsync(IStorageItemWithPath source,
                                                     string destination,
                                                     NameCollisionOption collision,
                                                     IProgress<float> progress,
                                                     IProgress<FileSystemStatusCode> errorCode,
                                                     CancellationToken cancellationToken)
        {
            if (destination.StartsWith(App.AppSettings.RecycleBinPath))
            {
                errorCode?.Report(FileSystemStatusCode.Unauthorized);
                progress?.Report(100.0f);

                // Do not paste files and folders inside the recycle bin
                await DialogDisplayHelper.ShowDialogAsync(
                    "ErrorDialogThisActionCannotBeDone".GetLocalized(),
                    "ErrorDialogUnsupportedOperation".GetLocalized());
                return null;
            }

            IStorageItem copiedItem = null;
            //long itemSize = await FilesystemHelpers.GetItemSize(await source.ToStorageItem(associatedInstance));

            if (source.ItemType == FilesystemItemType.Directory)
            {
                if (!string.IsNullOrWhiteSpace(source.Path) &&
                    Path.GetDirectoryName(destination).IsSubPathOf(source.Path)) // We check if user tried to copy anything above the source.ItemPath
                {
                    var destinationName = destination.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Last();
                    var sourceName = source.Path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Last();
                    ContentDialog dialog = new ContentDialog()
                    {
                        Title = "ErrorDialogThisActionCannotBeDone".GetLocalized(),
                        Content = $"{"ErrorDialogTheDestinationFolder".GetLocalized()} ({destinationName}) {"ErrorDialogIsASubfolder".GetLocalized()} (sourceName)",
                        //PrimaryButtonText = "ErrorDialogSkip".GetLocalized(),
                        CloseButtonText = "ErrorDialogCancel".GetLocalized()
                    };

                    ContentDialogResult result = await dialog.ShowAsync();

                    if (result == ContentDialogResult.Primary)
                    {
                        progress?.Report(100.0f);
                        errorCode?.Report(FileSystemStatusCode.InProgress | FileSystemStatusCode.Success);
                    }
                    else
                    {
                        progress?.Report(100.0f);
                        errorCode?.Report(FileSystemStatusCode.InProgress | FileSystemStatusCode.Generic);
                    }
                    return null;
                }
                else
                {
                    // CopyFileFromApp only works on file not directories
                    var fsSourceFolder = await source.ToStorageItemResult(associatedInstance);
                    var fsDestinationFolder = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(Path.GetDirectoryName(destination));
                    var fsResult = (FilesystemResult)(fsSourceFolder.ErrorCode | fsDestinationFolder.ErrorCode);

                    if (fsResult)
                    {
                        var fsCopyResult = await FilesystemTasks.Wrap(() => CloneDirectoryAsync((StorageFolder)fsSourceFolder, (StorageFolder)fsDestinationFolder, fsSourceFolder.Result.Name, collision.Convert()));

                        if (fsCopyResult == FileSystemStatusCode.AlreadyExists)
                        {
                            errorCode?.Report(FileSystemStatusCode.AlreadyExists);
                            progress?.Report(100.0f);
                            return null;
                        }

                        if (fsCopyResult)
                        {
                            if (FolderHelpers.CheckFolderForHiddenAttribute(source.Path))
                            {
                                // The source folder was hidden, apply hidden attribute to destination
                                NativeFileOperationsHelper.SetFileAttribute(fsCopyResult.Result.Path, FileAttributes.Hidden);
                            }
                            copiedItem = (StorageFolder)fsCopyResult;
                        }
                        fsResult = fsCopyResult;
                    }
                    if (fsResult == FileSystemStatusCode.Unauthorized)
                    {
                        fsResult = await PerformAdminOperation(new ValueSet()
                        {
                            { "Arguments", "FileOperation" },
                            { "fileop", "CopyItem" },
                            { "operationID", Guid.NewGuid().ToString() },
                            { "filepath", source.Path },
                            { "destpath", destination },
                            { "overwrite", collision == NameCollisionOption.ReplaceExisting }
                        });
                    }
                    errorCode?.Report(fsResult.ErrorCode);
                    if (!fsResult)
                    {
                        return null;
                    }
                }
            }
            else if (source.ItemType == FilesystemItemType.File)
            {
                var fsResult = (FilesystemResult)await Task.Run(() => NativeFileOperationsHelper.CopyFileFromApp(source.Path, destination, true));

                if (!fsResult)
                {
                    Debug.WriteLine(System.Runtime.InteropServices.Marshal.GetLastWin32Error());

                    FilesystemResult<StorageFolder> destinationResult = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(Path.GetDirectoryName(destination));
                    var sourceResult = await source.ToStorageItemResult(associatedInstance);
                    fsResult = sourceResult.ErrorCode | destinationResult.ErrorCode;

                    if (fsResult)
                    {
                        var file = (StorageFile)sourceResult;
                        var fsResultCopy = await FilesystemTasks.Wrap(() => file.CopyAsync(destinationResult.Result, Path.GetFileName(file.Name), collision).AsTask());

                        if (fsResultCopy == FileSystemStatusCode.AlreadyExists)
                        {
                            errorCode?.Report(FileSystemStatusCode.AlreadyExists);
                            progress?.Report(100.0f);
                            return null;
                        }

                        if (fsResultCopy)
                        {
                            copiedItem = fsResultCopy.Result;
                        }
                        fsResult = fsResultCopy;
                    }
                    if (fsResult == FileSystemStatusCode.Unauthorized)
                    {
                        fsResult = await PerformAdminOperation(new ValueSet()
                        {
                            { "Arguments", "FileOperation" },
                            { "fileop", "CopyItem" },
                            { "operationID", Guid.NewGuid().ToString() },
                            { "filepath", source.Path },
                            { "destpath", destination },
                            { "overwrite", collision == NameCollisionOption.ReplaceExisting }
                        });
                    }
                }
                errorCode?.Report(fsResult.ErrorCode);
                if (!fsResult)
                {
                    return null;
                }
            }

            if (Path.GetDirectoryName(destination) == associatedInstance.FilesystemViewModel.WorkingDirectory.TrimPath())
            {
                await Windows.ApplicationModel.Core.CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                {
                    await Task.Delay(50); // Small delay for the item to appear in the file list
                    List<ListedItem> copiedListedItems = associatedInstance.FilesystemViewModel.FilesAndFolders
                        .Where(listedItem => destination.Contains(listedItem.ItemPath)).ToList();

                    if (copiedListedItems.Count > 0)
                    {
                        itemManipulationModel.AddSelectedItems(copiedListedItems);
                        itemManipulationModel.FocusSelectedItems();
                    }
                }, Windows.System.DispatcherQueuePriority.Low);
            }

            progress?.Report(100.0f);

            if (collision == NameCollisionOption.ReplaceExisting)
            {
                errorCode?.Report(FileSystemStatusCode.Success);

                return null; // Cannot undo overwrite operation
            }

            var pathWithType = copiedItem.FromStorageItem(destination, source.ItemType);

            return new StorageHistory(FileOperationType.Copy, source, pathWithType);
        }

        public async Task<IStorageHistory> MoveAsync(IStorageItem source,
                                                     string destination,
                                                     NameCollisionOption collision,
                                                     IProgress<float> progress,
                                                     IProgress<FileSystemStatusCode> errorCode,
                                                     CancellationToken cancellationToken)
        {
            return await MoveAsync(source.FromStorageItem(),
                                                    destination,
                                                    collision,
                                                    progress,
                                                    errorCode,
                                                    cancellationToken);
        }

        public async Task<IStorageHistory> MoveAsync(IStorageItemWithPath source,
                                                     string destination,
                                                     NameCollisionOption collision,
                                                     IProgress<float> progress,
                                                     IProgress<FileSystemStatusCode> errorCode,
                                                     CancellationToken cancellationToken)
        {
            if (source.Path == destination)
            {
                progress?.Report(100.0f);
                errorCode?.Report(FileSystemStatusCode.Success);
                return null;
            }

            if (string.IsNullOrWhiteSpace(source.Path))
            {
                // Can't move (only copy) files from MTP devices because:
                // StorageItems returned in DataPackageView are read-only
                // The item.Path property will be empty and there's no way of retrieving a new StorageItem with R/W access
                return await CopyAsync(source, destination, collision, progress, errorCode, cancellationToken);
            }

            if (destination.StartsWith(App.AppSettings.RecycleBinPath))
            {
                errorCode?.Report(FileSystemStatusCode.Unauthorized);
                progress?.Report(100.0f);

                // Do not paste files and folders inside the recycle bin
                await DialogDisplayHelper.ShowDialogAsync(
                    "ErrorDialogThisActionCannotBeDone".GetLocalized(),
                    "ErrorDialogUnsupportedOperation".GetLocalized());
                return null;
            }

            IStorageItem movedItem = null;
            //long itemSize = await FilesystemHelpers.GetItemSize(await source.ToStorageItem(associatedInstance));

            if (source.ItemType == FilesystemItemType.Directory)
            {
                if (!string.IsNullOrWhiteSpace(source.Path) &&
                    Path.GetDirectoryName(destination).IsSubPathOf(source.Path)) // We check if user tried to move anything above the source.ItemPath
                {
                    var destinationName = destination.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Last();
                    var sourceName = source.Path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Last();
                    ContentDialog dialog = new ContentDialog()
                    {
                        Title = "ErrorDialogThisActionCannotBeDone".GetLocalized(),
                        Content = "ErrorDialogTheDestinationFolder".GetLocalized() + " (" + destinationName + ") " + "ErrorDialogIsASubfolder".GetLocalized() + " (" + sourceName + ")",
                        //PrimaryButtonText = "ErrorDialogSkip".GetLocalized(),
                        CloseButtonText = "ErrorDialogCancel".GetLocalized()
                    };

                    ContentDialogResult result = await dialog.ShowAsync();

                    if (result == ContentDialogResult.Primary)
                    {
                        progress?.Report(100.0f);
                        errorCode?.Report(FileSystemStatusCode.InProgress | FileSystemStatusCode.Success);
                    }
                    else
                    {
                        progress?.Report(100.0f);
                        errorCode?.Report(FileSystemStatusCode.InProgress | FileSystemStatusCode.Generic);
                    }
                    return null;
                }
                else
                {
                    var fsResult = (FilesystemResult)await Task.Run(() => NativeFileOperationsHelper.MoveFileFromApp(source.Path, destination));

                    if (!fsResult)
                    {
                        Debug.WriteLine(System.Runtime.InteropServices.Marshal.GetLastWin32Error());

                        var fsSourceFolder = await source.ToStorageItemResult(associatedInstance);
                        var fsDestinationFolder = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(Path.GetDirectoryName(destination));
                        fsResult = fsSourceFolder.ErrorCode | fsDestinationFolder.ErrorCode;

                        if (fsResult)
                        {
                            var fsResultMove = await FilesystemTasks.Wrap(() => MoveDirectoryAsync((StorageFolder)fsSourceFolder, (StorageFolder)fsDestinationFolder, fsSourceFolder.Result.Name, collision.Convert(), true));

                            if (fsResultMove == FileSystemStatusCode.AlreadyExists)
                            {
                                progress?.Report(100.0f);
                                errorCode?.Report(FileSystemStatusCode.AlreadyExists);
                                return null;
                            }

                            if (fsResultMove)
                            {
                                if (FolderHelpers.CheckFolderForHiddenAttribute(source.Path))
                                {
                                    // The source folder was hidden, apply hidden attribute to destination
                                    NativeFileOperationsHelper.SetFileAttribute(fsResultMove.Result.Path, FileAttributes.Hidden);
                                }
                                movedItem = (StorageFolder)fsResultMove;
                            }
                            fsResult = fsResultMove;
                        }
                        if (fsResult == FileSystemStatusCode.Unauthorized || fsResult == FileSystemStatusCode.ReadOnly)
                        {
                            fsResult = await PerformAdminOperation(new ValueSet()
                            {
                                { "Arguments", "FileOperation" },
                                { "fileop", "MoveItem" },
                                { "operationID", Guid.NewGuid().ToString() },
                                { "filepath", source.Path },
                                { "destpath", destination },
                                { "overwrite", collision == NameCollisionOption.ReplaceExisting }
                            });
                        }
                    }
                    errorCode?.Report(fsResult.ErrorCode);
                }
            }
            else if (source.ItemType == FilesystemItemType.File)
            {
                var fsResult = (FilesystemResult)await Task.Run(() => NativeFileOperationsHelper.MoveFileFromApp(source.Path, destination));

                if (!fsResult)
                {
                    Debug.WriteLine(System.Runtime.InteropServices.Marshal.GetLastWin32Error());

                    FilesystemResult<StorageFolder> destinationResult = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(Path.GetDirectoryName(destination));
                    var sourceResult = await source.ToStorageItemResult(associatedInstance);
                    fsResult = sourceResult.ErrorCode | destinationResult.ErrorCode;

                    if (fsResult)
                    {
                        var file = (StorageFile)sourceResult;
                        var fsResultMove = await FilesystemTasks.Wrap(() => file.MoveAsync(destinationResult.Result, Path.GetFileName(file.Name), collision).AsTask());

                        if (fsResultMove == FileSystemStatusCode.AlreadyExists)
                        {
                            progress?.Report(100.0f);
                            errorCode?.Report(FileSystemStatusCode.AlreadyExists);
                            return null;
                        }

                        if (fsResultMove)
                        {
                            movedItem = file;
                        }
                        fsResult = fsResultMove;
                    }
                    if (fsResult == FileSystemStatusCode.Unauthorized || fsResult == FileSystemStatusCode.ReadOnly)
                    {
                        fsResult = await PerformAdminOperation(new ValueSet()
                        {
                            { "Arguments", "FileOperation" },
                            { "fileop", "MoveItem" },
                            { "operationID", Guid.NewGuid().ToString() },
                            { "filepath", source.Path },
                            { "destpath", destination },
                            { "overwrite", collision == NameCollisionOption.ReplaceExisting }
                        });
                    }
                }
                errorCode?.Report(fsResult.ErrorCode);
            }

            if (Path.GetDirectoryName(destination) == associatedInstance.FilesystemViewModel.WorkingDirectory.TrimPath())
            {
                await Windows.ApplicationModel.Core.CoreApplication.MainView.DispatcherQueue.EnqueueAsync(async () =>
                {
                    await Task.Delay(50); // Small delay for the item to appear in the file list
                    List<ListedItem> movedListedItems = associatedInstance.FilesystemViewModel.FilesAndFolders
                        .Where(listedItem => destination.Contains(listedItem.ItemPath)).ToList();

                    if (movedListedItems.Count > 0)
                    {
                        itemManipulationModel.AddSelectedItems(movedListedItems);
                        itemManipulationModel.FocusSelectedItems();
                    }
                }, Windows.System.DispatcherQueuePriority.Low);
            }

            progress?.Report(100.0f);

            if (collision == NameCollisionOption.ReplaceExisting)
            {
                return null; // Cannot undo overwrite operation
            }

            var pathWithType = movedItem.FromStorageItem(destination, source.ItemType);

            return new StorageHistory(FileOperationType.Move, source, pathWithType);
        }

        public async Task<IStorageHistory> DeleteAsync(IStorageItem source,
                                                       IProgress<float> progress,
                                                       IProgress<FileSystemStatusCode> errorCode,
                                                       bool permanently,
                                                       CancellationToken cancellationToken)
        {
            return await DeleteAsync(source.FromStorageItem(),
                                                      progress,
                                                      errorCode,
                                                      permanently,
                                                      cancellationToken);
        }

        public async Task<IStorageHistory> DeleteAsync(IStorageItemWithPath source,
                                                       IProgress<float> progress,
                                                       IProgress<FileSystemStatusCode> errorCode,
                                                       bool permanently,
                                                       CancellationToken cancellationToken)
        {
            bool deleteFromRecycleBin = recycleBinHelpers.IsPathUnderRecycleBin(source.Path);

            FilesystemResult fsResult = FileSystemStatusCode.InProgress;

            errorCode?.Report(fsResult);
            progress?.Report(0.0f);

            if (permanently)
            {
                fsResult = (FilesystemResult)NativeFileOperationsHelper.DeleteFileFromApp(source.Path);
            }
            if (!fsResult)
            {
                if (source.ItemType == FilesystemItemType.File)
                {
                    fsResult = await associatedInstance.FilesystemViewModel.GetFileFromPathAsync(source.Path)
                        .OnSuccess((t) => t.DeleteAsync(permanently ? StorageDeleteOption.PermanentDelete : StorageDeleteOption.Default).AsTask());
                }
                else if (source.ItemType == FilesystemItemType.Directory)
                {
                    fsResult = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(source.Path)
                        .OnSuccess((t) => t.DeleteAsync(permanently ? StorageDeleteOption.PermanentDelete : StorageDeleteOption.Default).AsTask());
                }
            }

            errorCode?.Report(fsResult);

            if (fsResult == FileSystemStatusCode.Unauthorized)
            {
                // Try again with fulltrust process (non admin: for shortcuts and hidden files)
                var connection = await AppServiceConnectionHelper.Instance;
                if (connection != null)
                {
                    var (status, response) = await connection.SendMessageForResponseAsync(new ValueSet()
                    {
                        { "Arguments", "FileOperation" },
                        { "fileop", "DeleteItem" },
                        { "operationID", Guid.NewGuid().ToString() },
                        { "filepath", source.Path },
                        { "permanently", permanently }
                    });
                    fsResult = (FilesystemResult)(status == AppServiceResponseStatus.Success
                        && response.Get("Success", false));
                }
                if (!fsResult)
                {
                    fsResult = await PerformAdminOperation(new ValueSet()
                    {
                        { "Arguments", "FileOperation" },
                        { "fileop", "DeleteItem" },
                        { "operationID", Guid.NewGuid().ToString() },
                        { "filepath", source.Path },
                        { "permanently", permanently }
                    });
                }
            }
            else if (fsResult == FileSystemStatusCode.InUse)
            {
                // TODO: retry or show dialog
                await DialogDisplayHelper.ShowDialogAsync("FileInUseDeleteDialog/Title".GetLocalized(), "FileInUseDeleteDialog/Text".GetLocalized());
            }

            if (deleteFromRecycleBin)
            {
                // Recycle bin also stores a file starting with $I for each item
                string iFilePath = Path.Combine(Path.GetDirectoryName(source.Path), Path.GetFileName(source.Path).Replace("$R", "$I"));
                await associatedInstance.FilesystemViewModel.GetFileFromPathAsync(iFilePath)
                    .OnSuccess(t => t.DeleteAsync(StorageDeleteOption.PermanentDelete).AsTask());
            }
            errorCode?.Report(fsResult);
            progress?.Report(100.0f);

            if (fsResult)
            {
                await associatedInstance.FilesystemViewModel.RemoveFileOrFolderAsync(source.Path);

                if (!permanently)
                {
                    // Enumerate Recycle Bin
                    List<ShellFileItem> nameMatchItems, items = await recycleBinHelpers.EnumerateRecycleBin();

                    // Get name matching files
                    if (Path.GetExtension(source.Path) == ".lnk" || Path.GetExtension(source.Path) == ".url") // We need to check if it is a shortcut file
                    {
                        nameMatchItems = items.Where((item) => item.FilePath == Path.Combine(Path.GetDirectoryName(source.Path), Path.GetFileNameWithoutExtension(source.Path))).ToList();
                    }
                    else
                    {
                        nameMatchItems = items.Where((item) => item.FilePath == source.Path).ToList();
                    }

                    // Get newest file
                    ShellFileItem item = nameMatchItems.Where((item) => item.RecycleDate != null).OrderBy((item) => item.RecycleDate).FirstOrDefault();

                    return new StorageHistory(FileOperationType.Recycle, source, StorageItemHelpers.FromPathAndType(item?.RecyclePath, source.ItemType));
                }

                return new StorageHistory(FileOperationType.Delete, source, null);
            }
            else
            {
                // Stop at first error
                return null;
            }
        }

        public async Task<IStorageHistory> RenameAsync(IStorageItem source,
                                                       string newName,
                                                       NameCollisionOption collision,
                                                       IProgress<FileSystemStatusCode> errorCode,
                                                       CancellationToken cancellationToken)
        {
            return await RenameAsync(StorageItemHelpers.FromStorageItem(source), newName, collision, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> RenameAsync(IStorageItemWithPath source,
                                                       string newName,
                                                       NameCollisionOption collision,
                                                       IProgress<FileSystemStatusCode> errorCode,
                                                       CancellationToken cancellationToken)
        {
            if (Path.GetFileName(source.Path) == newName && collision == NameCollisionOption.FailIfExists)
            {
                errorCode?.Report(FileSystemStatusCode.AlreadyExists);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(newName)
                && !FilesystemHelpers.ContainsRestrictedCharacters(newName)
                && !FilesystemHelpers.ContainsRestrictedFileName(newName))
            {
                var renamed = await source.ToStorageItemResult(associatedInstance)
                    .OnSuccess(async (t) =>
                    {
                        if (t.Name.Equals(newName, StringComparison.CurrentCultureIgnoreCase))
                        {
                            await t.RenameAsync(newName, NameCollisionOption.ReplaceExisting);
                        }
                        else
                        {
                            await t.RenameAsync(newName, collision);
                        }
                        return t;
                    });

                if (renamed)
                {
                    errorCode?.Report(FileSystemStatusCode.Success);
                    return new StorageHistory(FileOperationType.Rename, source, renamed.Result.FromStorageItem());
                }
                else if (renamed == FileSystemStatusCode.Unauthorized)
                {
                    // Try again with MoveFileFromApp
                    var destination = Path.Combine(Path.GetDirectoryName(source.Path), newName);
                    if (NativeFileOperationsHelper.MoveFileFromApp(source.Path, destination))
                    {
                        errorCode?.Report(FileSystemStatusCode.Success);
                        return new StorageHistory(FileOperationType.Rename, source, StorageItemHelpers.FromPathAndType(destination, source.ItemType));
                    }
                    else
                    {
                        var fsResult = await PerformAdminOperation(new ValueSet()
                        {
                            { "Arguments", "FileOperation" },
                            { "fileop", "RenameItem" },
                            { "operationID", Guid.NewGuid().ToString() },
                            { "filepath", source.Path },
                            { "newName", newName },
                            { "overwrite", collision == NameCollisionOption.ReplaceExisting }
                        });
                        if (fsResult)
                        {
                            errorCode?.Report(FileSystemStatusCode.Success);
                            return new StorageHistory(FileOperationType.Rename, source, StorageItemHelpers.FromPathAndType(destination, source.ItemType));
                        }
                    }
                }
                else if (renamed == FileSystemStatusCode.NotAFile || renamed == FileSystemStatusCode.NotAFolder)
                {
                    await DialogDisplayHelper.ShowDialogAsync("RenameError/NameInvalid/Title".GetLocalized(), "RenameError/NameInvalid/Text".GetLocalized());
                }
                else if (renamed == FileSystemStatusCode.NameTooLong)
                {
                    await DialogDisplayHelper.ShowDialogAsync("RenameError/TooLong/Title".GetLocalized(), "RenameError/TooLong/Text".GetLocalized());
                }
                else if (renamed == FileSystemStatusCode.InUse)
                {
                    // TODO: proper dialog, retry
                    await DialogDisplayHelper.ShowDialogAsync("FileInUseDeleteDialog/Title".GetLocalized(), "");
                }
                else if (renamed == FileSystemStatusCode.NotFound)
                {
                    await DialogDisplayHelper.ShowDialogAsync("RenameError/ItemDeleted/Title".GetLocalized(), "RenameError/ItemDeleted/Text".GetLocalized());
                }
                else if (renamed == FileSystemStatusCode.AlreadyExists)
                {
                    var ItemAlreadyExistsDialog = new ContentDialog()
                    {
                        Title = "ItemAlreadyExistsDialogTitle".GetLocalized(),
                        Content = "ItemAlreadyExistsDialogContent".GetLocalized(),
                        PrimaryButtonText = "ItemAlreadyExistsDialogPrimaryButtonText".GetLocalized(),
                        SecondaryButtonText = "ItemAlreadyExistsDialogSecondaryButtonText".GetLocalized(),
                        CloseButtonText = "ItemAlreadyExistsDialogCloseButtonText".GetLocalized()
                    };

                    if (UIHelpers.IsAnyContentDialogOpen())
                    {
                        // Only a single ContentDialog can be open at any time.
                        return null;
                    }
                    ContentDialogResult result = await ItemAlreadyExistsDialog.ShowAsync();

                    if (result == ContentDialogResult.Primary)
                    {
                        return await RenameAsync(source, newName, NameCollisionOption.GenerateUniqueName, errorCode, cancellationToken);
                    }
                    else if (result == ContentDialogResult.Secondary)
                    {
                        return await RenameAsync(source, newName, NameCollisionOption.ReplaceExisting, errorCode, cancellationToken);
                    }
                }
                errorCode?.Report(renamed);
            }

            return null;
        }

        public async Task<IStorageHistory> RestoreFromTrashAsync(IStorageItemWithPath source,
                                                                 string destination,
                                                                 IProgress<float> progress,
                                                                 IProgress<FileSystemStatusCode> errorCode,
                                                                 CancellationToken cancellationToken)
        {
            FilesystemResult fsResult = FileSystemStatusCode.InProgress;
            errorCode?.Report(fsResult);

            fsResult = (FilesystemResult)await Task.Run(() => NativeFileOperationsHelper.MoveFileFromApp(source.Path, destination));

            if (!fsResult)
            {
                if (source.ItemType == FilesystemItemType.Directory)
                {
                    FilesystemResult<StorageFolder> sourceFolder = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(source.Path);
                    FilesystemResult<StorageFolder> destinationFolder = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(Path.GetDirectoryName(destination));

                    fsResult = sourceFolder.ErrorCode | destinationFolder.ErrorCode;
                    errorCode?.Report(fsResult);

                    if (fsResult)
                    {
                        fsResult = await FilesystemTasks.Wrap(() => MoveDirectoryAsync(sourceFolder.Result, destinationFolder.Result, Path.GetFileName(destination),
                            CreationCollisionOption.FailIfExists, true));
                        // TODO: we could use here FilesystemHelpers with registerHistory false?
                    }
                    errorCode?.Report(fsResult);
                }
                else
                {
                    FilesystemResult<StorageFile> sourceFile = await associatedInstance.FilesystemViewModel.GetFileFromPathAsync(source.Path);
                    FilesystemResult<StorageFolder> destinationFolder = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(Path.GetDirectoryName(destination));

                    fsResult = sourceFile.ErrorCode | destinationFolder.ErrorCode;
                    errorCode?.Report(fsResult);

                    if (fsResult)
                    {
                        fsResult = await FilesystemTasks.Wrap(() => sourceFile.Result.MoveAsync(destinationFolder.Result, Path.GetFileName(destination), NameCollisionOption.GenerateUniqueName).AsTask());
                    }
                    errorCode?.Report(fsResult);
                }
                if (fsResult == FileSystemStatusCode.Unauthorized || fsResult == FileSystemStatusCode.ReadOnly)
                {
                    fsResult = await PerformAdminOperation(new ValueSet()
                    {
                        { "Arguments", "FileOperation" },
                        { "fileop", "MoveItem" },
                        { "operationID", Guid.NewGuid().ToString() },
                        { "filepath", source.Path },
                        { "destpath", destination },
                        { "overwrite", false }
                    });
                }
            }

            if (fsResult)
            {
                // Recycle bin also stores a file starting with $I for each item
                string iFilePath = Path.Combine(Path.GetDirectoryName(source.Path), Path.GetFileName(source.Path).Replace("$R", "$I"));
                await associatedInstance.FilesystemViewModel.GetFileFromPathAsync(iFilePath)
                    .OnSuccess(iFile => iFile.DeleteAsync().AsTask());
            }

            errorCode?.Report(fsResult);
            if (fsResult != FileSystemStatusCode.Success)
            {
                if (((FileSystemStatusCode)fsResult).HasFlag(FileSystemStatusCode.Unauthorized))
                {
                    await DialogDisplayHelper.ShowDialogAsync("AccessDeniedDeleteDialog/Title".GetLocalized(), "AccessDeniedDeleteDialog/Text".GetLocalized());
                }
                else if (((FileSystemStatusCode)fsResult).HasFlag(FileSystemStatusCode.Unauthorized))
                {
                    await DialogDisplayHelper.ShowDialogAsync("FileNotFoundDialog/Title".GetLocalized(), "FileNotFoundDialog/Text".GetLocalized());
                }
                else if (((FileSystemStatusCode)fsResult).HasFlag(FileSystemStatusCode.AlreadyExists))
                {
                    await DialogDisplayHelper.ShowDialogAsync("ItemAlreadyExistsDialogTitle".GetLocalized(), "ItemAlreadyExistsDialogContent".GetLocalized());
                }
            }

            return new StorageHistory(FileOperationType.Restore, source, StorageItemHelpers.FromPathAndType(destination, source.ItemType));
        }

        #endregion IFilesystemOperations

        #region Helpers

        private async static Task<StorageFolder> CloneDirectoryAsync(IStorageFolder sourceFolder, IStorageFolder destinationFolder, string sourceRootName, CreationCollisionOption collision = CreationCollisionOption.FailIfExists)
        {
            StorageFolder createdRoot = await destinationFolder.CreateFolderAsync(sourceRootName, collision);
            destinationFolder = createdRoot;

            foreach (IStorageFile fileInSourceDir in await sourceFolder.GetFilesAsync())
            {
                await fileInSourceDir.CopyAsync(destinationFolder, fileInSourceDir.Name, NameCollisionOption.GenerateUniqueName);
            }

            foreach (IStorageFolder folderinSourceDir in await sourceFolder.GetFoldersAsync())
            {
                await CloneDirectoryAsync(folderinSourceDir, destinationFolder, folderinSourceDir.Name);
            }

            return createdRoot;
        }

        private static async Task<StorageFolder> MoveDirectoryAsync(IStorageFolder sourceFolder, IStorageFolder destinationDirectory, string sourceRootName, CreationCollisionOption collision = CreationCollisionOption.FailIfExists, bool deleteSource = false)
        {
            StorageFolder createdRoot = await destinationDirectory.CreateFolderAsync(sourceRootName, collision);
            destinationDirectory = createdRoot;

            foreach (StorageFile fileInSourceDir in await sourceFolder.GetFilesAsync())
            {
                await fileInSourceDir.MoveAsync(destinationDirectory, fileInSourceDir.Name, NameCollisionOption.GenerateUniqueName);
            }

            foreach (StorageFolder folderinSourceDir in await sourceFolder.GetFoldersAsync())
            {
                await MoveDirectoryAsync(folderinSourceDir, destinationDirectory, folderinSourceDir.Name, collision, false);
            }

            if (deleteSource)
            {
                await sourceFolder.DeleteAsync(StorageDeleteOption.Default);
            }

            App.JumpList.RemoveFolder(sourceFolder.Path);

            return createdRoot;
        }

        private async Task<FilesystemResult> PerformAdminOperation(ValueSet operation)
        {
            var elevateConfirmDialog = new Files.Dialogs.ElevateConfirmDialog();
            var elevateConfirmResult = await elevateConfirmDialog.ShowAsync();
            if (elevateConfirmResult == ContentDialogResult.Primary)
            {
                var connection = await AppServiceConnectionHelper.Instance;
                if (connection != null && await connection.Elevate())
                {
                    // Try again with fulltrust process (admin)
                    connection = await AppServiceConnectionHelper.Instance;
                    if (connection != null)
                    {
                        var (status, response) = await connection.SendMessageForResponseAsync(operation);
                        return (FilesystemResult)(status == AppServiceResponseStatus.Success
                            && response.Get("Success", false));
                    }
                }
            }
            return (FilesystemResult)false;
        }

        #endregion Helpers

        #region IDisposable

        public void Dispose()
        {
            recycleBinHelpers = null;
            associatedInstance = null;
        }

        #endregion IDisposable

        public async Task<IStorageHistory> CopyItemsAsync(IEnumerable<IStorageItem> source, IEnumerable<string> destination, IEnumerable<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await CopyItemsAsync(source.Select((item) => item.FromStorageItem()).ToList(), destination, collisions, progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> CopyItemsAsync(IEnumerable<IStorageItemWithPath> source, IEnumerable<string> destination, IEnumerable<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken token)
        {
            var rawStorageHistory = new List<IStorageHistory>();

            for (int i = 0; i < source.Count(); i++)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (collisions.ElementAt(i) != FileNameConflictResolveOptionType.Skip)
                {
                    rawStorageHistory.Add(await CopyAsync(
                        source.ElementAt(i),
                        destination.ElementAt(i),
                        collisions.ElementAt(i).Convert(),
                        null,
                        errorCode,
                        token));
                }

                progress?.Report(i / (float)source.Count() * 100.0f);
            }

            if (rawStorageHistory.Any() && rawStorageHistory.TrueForAll((item) => item != null))
            {
                return new StorageHistory(
                    rawStorageHistory[0].OperationType,
                    rawStorageHistory.SelectMany((item) => item.Source).ToList(),
                    rawStorageHistory.SelectMany((item) => item.Destination).ToList());
            }
            return null;
        }

        public async Task<IStorageHistory> MoveItemsAsync(IEnumerable<IStorageItem> source, IEnumerable<string> destination, IEnumerable<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await MoveItemsAsync(source.Select((item) => item.FromStorageItem()).ToList(), destination, collisions, progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> MoveItemsAsync(IEnumerable<IStorageItemWithPath> source, IEnumerable<string> destination, IEnumerable<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken token)
        {
            var rawStorageHistory = new List<IStorageHistory>();

            for (int i = 0; i < source.Count(); i++)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (collisions.ElementAt(i) != FileNameConflictResolveOptionType.Skip)
                {
                    rawStorageHistory.Add(await MoveAsync(
                        source.ElementAt(i),
                        destination.ElementAt(i),
                        collisions.ElementAt(i).Convert(),
                        null,
                        errorCode,
                        token));
                }

                progress?.Report(i / (float)source.Count() * 100.0f);
            }

            if (rawStorageHistory.Any() && rawStorageHistory.TrueForAll((item) => item != null))
            {
                return new StorageHistory(
                    rawStorageHistory[0].OperationType,
                    rawStorageHistory.SelectMany((item) => item.Source).ToList(),
                    rawStorageHistory.SelectMany((item) => item.Destination).ToList());
            }
            return null;
        }

        public async Task<IStorageHistory> DeleteItemsAsync(IEnumerable<IStorageItem> source, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, bool permanently, CancellationToken cancellationToken)
        {
            return await DeleteItemsAsync(source.Select((item) => item.FromStorageItem()), progress, errorCode, permanently, cancellationToken);
        }

        public async Task<IStorageHistory> DeleteItemsAsync(IEnumerable<IStorageItemWithPath> source, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, bool permanently, CancellationToken token)
        {
            bool originalPermanently = permanently;
            var rawStorageHistory = new List<IStorageHistory>();

            for (int i = 0; i < source.Count(); i++)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (recycleBinHelpers.IsPathUnderRecycleBin(source.ElementAt(i).Path))
                {
                    permanently = true;
                }
                else
                {
                    permanently = originalPermanently;
                }

                rawStorageHistory.Add(await DeleteAsync(source.ElementAt(i), null, errorCode, permanently, token));
                progress?.Report((float)i / source.Count() * 100.0f);
            }

            if (rawStorageHistory.Any() && rawStorageHistory.TrueForAll((item) => item != null))
            {
                return new StorageHistory(
                    rawStorageHistory[0].OperationType,
                    rawStorageHistory.SelectMany((item) => item.Source).ToList(),
                    rawStorageHistory.SelectMany((item) => item.Destination).ToList());
            }
            return null;
        }
    }
}