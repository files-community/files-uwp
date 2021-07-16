using Files.Common;
using Files.DataModels;
using Files.Dialogs;
using Files.Enums;
using Files.Extensions;
using Files.Filesystem.FilesystemHistory;
using Files.Helpers;
using Files.Interacts;
using Files.ViewModels;
using Files.ViewModels.Dialogs;
using Microsoft.Toolkit.Uwp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation.Collections;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI.Xaml.Controls;
using static Files.Helpers.NativeFindStorageItemHelper;
using FileAttributes = System.IO.FileAttributes;

namespace Files.Filesystem
{
    public class FilesystemHelpers : IFilesystemHelpers
    {
        #region Private Members

        private IShellPage associatedInstance;

        private IFilesystemOperations filesystemOperations;

        private ItemManipulationModel itemManipulationModel => associatedInstance.SlimContentPage?.ItemManipulationModel;

        private RecycleBinHelpers recycleBinHelpers;

        private readonly CancellationToken cancellationToken;

        private Task<NamedPipeAsAppServiceConnection> ServiceConnection => AppServiceConnectionHelper.Instance;

        private StatusCenterViewModel statusCenterViewModel => App.StatusCenterViewModel;

        #region Helpers Members

        private static readonly List<string> RestrictedFileNames = new List<string>()
        {
                "CON", "PRN", "AUX",
                "NUL", "COM1", "COM2",
                "COM3", "COM4", "COM5",
                "COM6", "COM7", "COM8",
                "COM9", "LPT1", "LPT2",
                "LPT3", "LPT4", "LPT5",
                "LPT6", "LPT7", "LPT8", "LPT9"
        };

        #endregion Helpers Members

        #endregion Private Members

        #region Constructor

        public FilesystemHelpers(IShellPage associatedInstance, CancellationToken cancellationToken)
        {
            this.associatedInstance = associatedInstance;
            this.cancellationToken = cancellationToken;
            this.filesystemOperations = new ShellFilesystemOperations(this.associatedInstance);
            this.recycleBinHelpers = new RecycleBinHelpers();
        }

        #endregion Constructor

        #region IFilesystemHelpers

        #region Create

        public async Task<(ReturnResult, IStorageItem)> CreateAsync(IStorageItemWithPath source, bool registerHistory)
        {
            var returnCode = FileSystemStatusCode.InProgress;
            var errorCode = new Progress<FileSystemStatusCode>();
            errorCode.ProgressChanged += (s, e) => returnCode = e;

            var result = await filesystemOperations.CreateAsync(source, errorCode, cancellationToken);

            if (registerHistory && !string.IsNullOrWhiteSpace(source.Path))
            {
                App.HistoryWrapper.AddHistory(result.Item1);
            }

            await Task.Yield();
            return (returnCode.ToStatus(), result.Item2);
        }

        #endregion Create

        #region Delete

        public async Task<ReturnResult> DeleteItemsAsync(IEnumerable<IStorageItemWithPath> source, bool showDialog, bool permanently, bool registerHistory)
        {
            var sourceDir = PathNormalization.GetParentDir(source.FirstOrDefault()?.Path);
            PostedStatusBanner banner = null;
            var returnStatus = ReturnResult.InProgress;

            var deleteFromRecycleBin = source.Select(item => item.Path).Any(path => recycleBinHelpers.IsPathUnderRecycleBin(path));

            if (App.AppSettings.ShowConfirmDeleteDialog && showDialog) // Check if the setting to show a confirmation dialog is on
            {
                List<FilesystemItemsOperationItemModel> incomingItems = new List<FilesystemItemsOperationItemModel>();

                for (int i = 0; i < source.Count(); i++)
                {
                    incomingItems.Add(new FilesystemItemsOperationItemModel(FilesystemOperationType.Delete, source.ElementAt(i).Path ?? source.ElementAt(i).Item.Path, null));
                }

                FilesystemOperationDialog dialog = await FilesystemOperationDialogViewModel.GetDialog(new FilesystemItemsOperationDataModel(
                    FilesystemOperationType.Delete,
                    false,
                    !deleteFromRecycleBin ? permanently : deleteFromRecycleBin,
                    !deleteFromRecycleBin,
                    incomingItems,
                    new List<FilesystemItemsOperationItemModel>()));

                ContentDialogResult result = await dialog.ShowAsync();

                if (result != ContentDialogResult.Primary)
                {
                    return ReturnResult.Cancelled; // Return if the result isn't delete
                }

                // Delete selected items if the result is Yes
                permanently = dialog.ViewModel.PermanentlyDelete;
            }

            // post the status banner
            if (permanently)
            {
                // deleting items from <x>
                banner = statusCenterViewModel.PostOperationBanner(string.Empty,
                    string.Format(source.Count() > 1 ? "StatusDeletingItemsDetails_Plural".GetLocalized() : "StatusDeletingItemsDetails_Singular".GetLocalized(), source.Count(), sourceDir),
                    0,
                    ReturnResult.InProgress,
                    FileOperationType.Delete,
                    new CancellationTokenSource());
            }
            else
            {
                // "Moving items from <x> to recycle bin"
                banner = statusCenterViewModel.PostOperationBanner(string.Empty,
                    string.Format(source.Count() > 1 ? "StatusMovingItemsDetails_Plural".GetLocalized() : "StatusMovingItemsDetails_Singular".GetLocalized(), source.Count(), sourceDir, "TheRecycleBin".GetLocalized()),
                    0,
                    ReturnResult.InProgress,
                    FileOperationType.Recycle,
                    new CancellationTokenSource());
            }

            banner.ErrorCode.ProgressChanged += (s, e) => returnStatus = e.ToStatus();

            var token = banner.CancellationToken;

            var sw = new Stopwatch();
            sw.Start();

            bool originalPermanently = permanently;

            IStorageHistory history = await filesystemOperations.DeleteItemsAsync(source, banner.Progress, banner.ErrorCode, permanently, cancellationToken);
            ((IProgress<float>)banner.Progress).Report(100.0f);
            await Task.Yield();

            if (!permanently && registerHistory)
            {
                App.HistoryWrapper.AddHistory(history);
            }
            var itemsDeleted = history?.Source.Count() ?? 0;

            banner.Remove();
            sw.Stop();

            if (token.IsCancellationRequested)
            {
                if (permanently)
                {
                    statusCenterViewModel.PostBanner(
                        "StatusDeletionCancelled".GetLocalized(),
                        string.Format(source.Count() > 1 ?
                            itemsDeleted > 1 ? "StatusDeleteCanceledDetails_Plural".GetLocalized() : "StatusDeleteCanceledDetails_Plural2".GetLocalized()
                            : "StatusDeleteCanceledDetails_Singular".GetLocalized(), source.Count(), sourceDir, itemsDeleted),
                        0,
                        ReturnResult.Cancelled,
                        FileOperationType.Delete);
                }
                else
                {
                    statusCenterViewModel.PostBanner(
                        "StatusRecycleCancelled".GetLocalized(),
                        string.Format(source.Count() > 1 ?
                            itemsDeleted > 1 ? "StatusMoveCanceledDetails_Plural".GetLocalized() : "StatusMoveCanceledDetails_Plural2".GetLocalized()
                            : "StatusMoveCanceledDetails_Singular".GetLocalized(), source.Count(), sourceDir, "TheRecycleBin".GetLocalized(), itemsDeleted),
                        0,
                        ReturnResult.Cancelled,
                        FileOperationType.Recycle);
                }
            }
            else if (returnStatus == ReturnResult.Success)
            {
                if (permanently)
                {
                    statusCenterViewModel.PostBanner(
                        "StatusDeletionComplete".GetLocalized(),
                        string.Format(source.Count() > 1 ? "StatusDeletedItemsDetails_Plural".GetLocalized() : "StatusDeletedItemsDetails_Singular".GetLocalized(), source.Count(), sourceDir, itemsDeleted),
                        0,
                        ReturnResult.Success,
                        FileOperationType.Delete);
                }
                else
                {
                    statusCenterViewModel.PostBanner(
                        "StatusRecycleComplete".GetLocalized(),
                        string.Format(source.Count() > 1 ? "StatusMovedItemsDetails_Plural".GetLocalized() : "StatusMovedItemsDetails_Singular".GetLocalized(), source.Count(), sourceDir, "TheRecycleBin".GetLocalized()),
                        0,
                        ReturnResult.Success,
                        FileOperationType.Recycle);
                }
            }
            else
            {
                if (permanently)
                {
                    statusCenterViewModel.PostBanner(
                        "StatusDeletionFailed".GetLocalized(),
                        string.Format(source.Count() > 1 ? "StatusDeletionFailedDetails_Plural".GetLocalized() : "StatusDeletionFailedDetails_Singular".GetLocalized(), source.Count(), sourceDir),
                        0,
                        ReturnResult.Failed,
                        FileOperationType.Delete);
                }
                else
                {
                    statusCenterViewModel.PostBanner(
                        "StatusRecycleFailed".GetLocalized(),
                        string.Format(source.Count() > 1 ? "StatusMoveFailedDetails_Plural".GetLocalized() : "StatusMoveFailedDetails_Singular".GetLocalized(), source.Count(), sourceDir, "TheRecycleBin".GetLocalized()),
                        0,
                        ReturnResult.Failed,
                        FileOperationType.Recycle);
                }
            }

            return returnStatus;
        }

        public async Task<ReturnResult> DeleteItemAsync(IStorageItemWithPath source, bool showDialog, bool permanently, bool registerHistory)
        {
            PostedStatusBanner banner;
            bool deleteFromRecycleBin = recycleBinHelpers.IsPathUnderRecycleBin(source.Path);

            if (deleteFromRecycleBin)
            {
                permanently = true;
            }

            if (permanently)
            {
                banner = statusCenterViewModel.PostBanner(string.Empty,
                associatedInstance.FilesystemViewModel.WorkingDirectory,
                0,
                ReturnResult.InProgress,
                FileOperationType.Delete);
            }
            else
            {
                banner = statusCenterViewModel.PostBanner(string.Empty,
                associatedInstance.FilesystemViewModel.WorkingDirectory,
                0,
                ReturnResult.InProgress,
                FileOperationType.Recycle);
            }

            var returnStatus = ReturnResult.InProgress;

            banner.ErrorCode.ProgressChanged += (s, e) => returnStatus = e.ToStatus();

            if (App.AppSettings.ShowConfirmDeleteDialog && showDialog) // Check if the setting to show a confirmation dialog is on
            {
                List<FilesystemItemsOperationItemModel> incomingItems = new List<FilesystemItemsOperationItemModel>
                {
                    new FilesystemItemsOperationItemModel(FilesystemOperationType.Delete, source.Path ?? source.Item.Path, null)
                };

                FilesystemOperationDialog dialog = await FilesystemOperationDialogViewModel.GetDialog(new FilesystemItemsOperationDataModel(
                    FilesystemOperationType.Delete,
                    false,
                    !deleteFromRecycleBin ? permanently : deleteFromRecycleBin,
                    !deleteFromRecycleBin,
                    incomingItems,
                    new List<FilesystemItemsOperationItemModel>()));

                ContentDialogResult result = await dialog.ShowAsync();

                if (result != ContentDialogResult.Primary)
                {
                    banner.Remove();
                    return ReturnResult.Cancelled; // Return if the result isn't delete
                }

                // Delete selected item if the result is Yes
                permanently = dialog.ViewModel.PermanentlyDelete;
            }

            var sw = new Stopwatch();
            sw.Start();

            IStorageHistory history = await filesystemOperations.DeleteAsync(source, banner.Progress, banner.ErrorCode, permanently, cancellationToken);
            ((IProgress<float>)banner.Progress).Report(100.0f);
            await Task.Yield();

            if (!permanently && registerHistory)
            {
                App.HistoryWrapper.AddHistory(history);
            }

            banner.Remove();
            sw.Stop();

            PostBannerHelpers.PostBanner_Delete(returnStatus, permanently ? FileOperationType.Delete : FileOperationType.Recycle, sw, associatedInstance);
            return returnStatus;
        }

        public async Task<ReturnResult> DeleteItemsAsync(IEnumerable<IStorageItem> source, bool showDialog, bool permanently, bool registerHistory)
        {
            return await DeleteItemsAsync(source.Select((item) => item.FromStorageItem()), showDialog, permanently, registerHistory);
        }

        public async Task<ReturnResult> DeleteItemAsync(IStorageItem source, bool showDialog, bool permanently, bool registerHistory)
        {
            PostedStatusBanner banner;
            bool deleteFromRecycleBin = recycleBinHelpers.IsPathUnderRecycleBin(source.Path);

            if (deleteFromRecycleBin)
            {
                permanently = true;
            }

            if (permanently)
            {
                banner = statusCenterViewModel.PostBanner(string.Empty,
                associatedInstance.FilesystemViewModel.WorkingDirectory,
                0,
                ReturnResult.InProgress,
                FileOperationType.Delete);
            }
            else
            {
                banner = statusCenterViewModel.PostBanner(string.Empty,
                associatedInstance.FilesystemViewModel.WorkingDirectory,
                0,
                ReturnResult.InProgress,
                FileOperationType.Recycle);
            }

            var returnStatus = ReturnResult.InProgress;
            banner.ErrorCode.ProgressChanged += (s, e) => returnStatus = e.ToStatus();

            if (App.AppSettings.ShowConfirmDeleteDialog && showDialog) // Check if the setting to show a confirmation dialog is on
            {
                List<FilesystemItemsOperationItemModel> incomingItems = new List<FilesystemItemsOperationItemModel>
                {
                    new FilesystemItemsOperationItemModel(FilesystemOperationType.Delete, source.Path, null)
                };

                FilesystemOperationDialog dialog = await FilesystemOperationDialogViewModel.GetDialog(new FilesystemItemsOperationDataModel(
                    FilesystemOperationType.Delete,
                    false,
                    !deleteFromRecycleBin ? permanently : deleteFromRecycleBin,
                    !deleteFromRecycleBin,
                    incomingItems,
                    new List<FilesystemItemsOperationItemModel>()));

                ContentDialogResult result = await dialog.ShowAsync();

                if (result != ContentDialogResult.Primary)
                {
                    banner.Remove();
                    return ReturnResult.Cancelled; // Return if the result isn't delete
                }

                // Delete selected item if the result is Yes
                permanently = dialog.ViewModel.PermanentlyDelete;
            }

            var sw = new Stopwatch();
            sw.Start();

            IStorageHistory history = await filesystemOperations.DeleteAsync(source, banner.Progress, banner.ErrorCode, permanently, cancellationToken);
            ((IProgress<float>)banner.Progress).Report(100.0f);
            await Task.Yield();

            if (!permanently && registerHistory)
            {
                App.HistoryWrapper.AddHistory(history);
            }

            banner.Remove();
            sw.Stop();

            PostBannerHelpers.PostBanner_Delete(returnStatus, permanently ? FileOperationType.Delete : FileOperationType.Recycle, sw, associatedInstance);

            return returnStatus;
        }

        #endregion Delete

        public async Task<ReturnResult> RestoreFromTrashAsync(IStorageItemWithPath source, string destination, bool registerHistory)
        {
            var returnCode = FileSystemStatusCode.InProgress;
            var errorCode = new Progress<FileSystemStatusCode>();
            errorCode.ProgressChanged += (s, e) => returnCode = e;

            IStorageHistory history = await filesystemOperations.RestoreFromTrashAsync(source, destination, null, errorCode, cancellationToken);

            if (registerHistory && !string.IsNullOrWhiteSpace(source.Path))
            {
                App.HistoryWrapper.AddHistory(history);
            }

            await Task.Yield();
            return returnCode.ToStatus();
        }

        public async Task<ReturnResult> PerformOperationTypeAsync(DataPackageOperation operation,
                                                                  DataPackageView packageView,
                                                                  string destination,
                                                                  bool showDialog,
                                                                  bool registerHistory,
                                                                  bool isTargetExecutable = false)
        {
            try
            {
                if (destination == null)
                {
                    return default;
                }
                if (destination.StartsWith(App.AppSettings.RecycleBinPath))
                {
                    return await RecycleItemsFromClipboard(packageView, destination, showDialog, registerHistory);
                }
                else if (operation.HasFlag(DataPackageOperation.Copy))
                {
                    return await CopyItemsFromClipboard(packageView, destination, showDialog, registerHistory);
                }
                else if (operation.HasFlag(DataPackageOperation.Move))
                {
                    return await MoveItemsFromClipboard(packageView, destination, showDialog, registerHistory);
                }
                else if (operation.HasFlag(DataPackageOperation.Link))
                {
                    // Open with piggybacks off of the link operation, since there isn't one for it
                    if (isTargetExecutable)
                    {
                        var items = await packageView.GetStorageItemsAsync();
                        NavigationHelpers.OpenItemsWithExecutable(associatedInstance, items.ToList(), destination);
                    }

                    // TODO: Support link creation
                    return default;
                }
                else if (operation.HasFlag(DataPackageOperation.None))
                {
                    return await CopyItemsFromClipboard(packageView, destination, showDialog, registerHistory);
                }
                else
                {
                    return default;
                }
            }
            finally
            {
                packageView.ReportOperationCompleted(operation);
            }
        }

        #region Copy

        public async Task<ReturnResult> CopyItemsAsync(IEnumerable<IStorageItem> source, IEnumerable<string> destination, bool showDialog, bool registerHistory)
        {
            return await CopyItemsAsync(source.Select((item) => item.FromStorageItem()).ToList(), destination, showDialog, registerHistory);
        }

        public async Task<ReturnResult> CopyItemAsync(IStorageItem source, string destination, bool showDialog, bool registerHistory)
        {
            return await CopyItemAsync(source.FromStorageItem(), destination, showDialog, registerHistory);
        }

        public async Task<ReturnResult> CopyItemsAsync(IEnumerable<IStorageItemWithPath> source, IEnumerable<string> destination, bool showDialog, bool registerHistory)
        {
            var sourceDir = PathNormalization.GetParentDir(source.FirstOrDefault()?.Path);
            var destinationDir = PathNormalization.GetParentDir(destination.FirstOrDefault());

            PostedStatusBanner banner = statusCenterViewModel.PostOperationBanner(
                string.Empty,
                string.Format(source.Count() > 1 ? "StatusCopyingItemsDetails_Plural".GetLocalized() : "StatusCopyingItemsDetails_Singular".GetLocalized(), source.Count(), destinationDir),
                0,
                ReturnResult.InProgress,
                FileOperationType.Copy, new CancellationTokenSource());

            var token = banner.CancellationToken;

            var returnStatus = ReturnResult.InProgress;
            banner.ErrorCode.ProgressChanged += (s, e) => returnStatus = e.ToStatus();

            var (collisions, cancelOperation) = await GetCollision(FilesystemOperationType.Copy, source, destination, showDialog);

            if (cancelOperation)
            {
                banner.Remove();
                return ReturnResult.Cancelled;
            }

            var sw = new Stopwatch();
            sw.Start();

            itemManipulationModel.ClearSelection();

            IStorageHistory history = await filesystemOperations.CopyItemsAsync(source, destination, collisions, banner.Progress, banner.ErrorCode, token);
            ((IProgress<float>)banner.Progress).Report(100.0f);
            await Task.Yield();

            if (registerHistory && source.Any((item) => !string.IsNullOrWhiteSpace(item.Path)))
            {
                App.HistoryWrapper.AddHistory(history);
            }
            var itemsCopied = history?.Source.Count() ?? 0;

            banner.Remove();
            sw.Stop();

            if (!token.IsCancellationRequested)
            {
                statusCenterViewModel.PostBanner(
                    "StatusCopyComplete".GetLocalized(),
                    string.Format(source.Count() > 1 ? "StatusCopiedItemsDetails_Plural".GetLocalized() : "StatusCopiedItemsDetails_Singular".GetLocalized(), source.Count(), destinationDir, itemsCopied),
                    0,
                    ReturnResult.Success,
                    FileOperationType.Copy);
            }
            else
            {
                statusCenterViewModel.PostBanner(
                    "StatusCopyCanceled".GetLocalized(),
                    string.Format(source.Count() > 1 ?
                        itemsCopied > 1 ? "StatusCopyCanceledDetails_Plural".GetLocalized() : "StatusCopyCanceledDetails_Plural2".GetLocalized() :
                        "StatusCopyCanceledDetails_Singular".GetLocalized(), source.Count(), destinationDir, itemsCopied),
                    0,
                    ReturnResult.Cancelled,
                    FileOperationType.Copy);
            }

            return returnStatus;
        }

        public async Task<ReturnResult> CopyItemAsync(IStorageItemWithPath source, string destination, bool showDialog, bool registerHistory)
        {
            PostedStatusBanner banner = statusCenterViewModel.PostBanner(
                string.Empty,
                associatedInstance.FilesystemViewModel.WorkingDirectory,
                0,
                ReturnResult.InProgress,
                FileOperationType.Copy);

            var returnStatus = ReturnResult.InProgress;
            banner.ErrorCode.ProgressChanged += (s, e) => returnStatus = e.ToStatus();

            var (collisions, cancelOperation) = await GetCollision(FilesystemOperationType.Copy, source.CreateEnumerable(), destination.CreateEnumerable(), showDialog);

            if (cancelOperation)
            {
                banner.Remove();
                return ReturnResult.Cancelled;
            }

            var sw = new Stopwatch();
            sw.Start();

            itemManipulationModel.ClearSelection();

            IStorageHistory history = null;
            if (collisions.First() != FileNameConflictResolveOptionType.Skip)
            {
                history = await filesystemOperations.CopyAsync(source, destination, collisions.First().Convert(), banner.Progress, banner.ErrorCode, cancellationToken);
                ((IProgress<float>)banner.Progress).Report(100.0f);
                await Task.Yield();
            }
            else
            {
                ((IProgress<float>)banner.Progress).Report(100.0f);
                return ReturnResult.Cancelled;
            }

            if (registerHistory && !string.IsNullOrWhiteSpace(source.Path))
            {
                App.HistoryWrapper.AddHistory(history);
            }

            banner.Remove();
            sw.Stop();

            if (sw.Elapsed.TotalSeconds >= 10)
            {
                statusCenterViewModel.PostBanner(
                    "StatusCopyComplete".GetLocalized(),
                    "StatusOperationCompleted".GetLocalized(),
                    0,
                    ReturnResult.Success,
                    FileOperationType.Copy);
            }

            return returnStatus;
        }

        public async Task<ReturnResult> RecycleItemsFromClipboard(DataPackageView packageView, string destination, bool showDialog, bool registerHistory)
        {
            if (!packageView.Contains(StandardDataFormats.StorageItems))
            {
                // Happens if you copy some text and then you Ctrl+V in Files
                return ReturnResult.BadArgumentException;
            }

            IReadOnlyList<IStorageItem> source;
            try
            {
                source = await packageView.GetStorageItemsAsync();
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80040064 || (uint)ex.HResult == 0x8004006A)
            {
                // Not supported
                return ReturnResult.Failed;
            }
            catch (Exception ex)
            {
                App.Logger.Warn(ex, ex.Message);
                return ReturnResult.UnknownException;
            }
            ReturnResult returnStatus = ReturnResult.InProgress;

            source = source.Where(x => !recycleBinHelpers.IsPathUnderRecycleBin(x.Path)).ToList(); // Can't recycle items already in recyclebin
            returnStatus = await DeleteItemsAsync(source, showDialog, false, registerHistory);

            return returnStatus;
        }

        public async Task<ReturnResult> CopyItemsFromClipboard(DataPackageView packageView, string destination, bool showDialog, bool registerHistory)
        {
            if (packageView.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> source;
                try
                {
                    source = await packageView.GetStorageItemsAsync();
                }
                catch (Exception ex) when ((uint)ex.HResult == 0x80040064 || (uint)ex.HResult == 0x8004006A)
                {
                    var connection = await ServiceConnection;
                    if (connection != null)
                    {
                        var (status, response) = await connection.SendMessageForResponseAsync(new ValueSet() {
                            { "Arguments", "FileOperation" },
                            { "fileop", "DragDrop" },
                            { "droppath", associatedInstance.FilesystemViewModel.WorkingDirectory } });
                        return (status == AppServiceResponseStatus.Success && response.Get("Success", false)) ? ReturnResult.Success : ReturnResult.Failed;
                    }
                    return ReturnResult.Failed;
                }
                catch (Exception ex)
                {
                    App.Logger.Warn(ex, ex.Message);
                    return ReturnResult.UnknownException;
                }
                ReturnResult returnStatus = ReturnResult.InProgress;

                var destinations = new List<string>();
                List<ShellFileItem> binItems = null;
                foreach (IStorageItem item in source)
                {
                    if (recycleBinHelpers.IsPathUnderRecycleBin(item.Path))
                    {
                        binItems ??= await recycleBinHelpers.EnumerateRecycleBin();
                        var matchingItem = binItems.FirstOrDefault(x => x.RecyclePath == item.Path); // Get original file name
                        destinations.Add(Path.Combine(destination, matchingItem?.FileName ?? item.Name));
                    }
                    else
                    {
                        destinations.Add(Path.Combine(destination, item.Name));
                    }
                }

                returnStatus = await CopyItemsAsync(source, destinations, showDialog, registerHistory);

                return returnStatus;
            }

            if (packageView.Contains(StandardDataFormats.Bitmap))
            {
                try
                {
                    var imgSource = await packageView.GetBitmapAsync();
                    using var imageStream = await imgSource.OpenReadAsync();
                    var folder = await StorageFolder.GetFolderFromPathAsync(destination);
                    // Set the name of the file to be the current time and date
                    var file = await folder.CreateFileAsync($"{DateTime.Now:mm-dd-yy-HHmmss}.png", CreationCollisionOption.GenerateUniqueName);

                    SoftwareBitmap softwareBitmap;

                    // Create the decoder from the stream
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(imageStream);

                    // Get the SoftwareBitmap representation of the file
                    softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                    await Helpers.SaveImageToFile.SaveSoftwareBitmapToFile(softwareBitmap, file, BitmapEncoder.PngEncoderId);
                    return ReturnResult.Success;
                }
                catch (Exception)
                {
                    return ReturnResult.UnknownException;
                }
            }

            // Happens if you copy some text and then you Ctrl+V in Files
            return ReturnResult.BadArgumentException;
        }

        #endregion Copy

        #region Move

        public async Task<ReturnResult> MoveItemsAsync(IEnumerable<IStorageItem> source, IEnumerable<string> destination, bool showDialog, bool registerHistory)
        {
            return await MoveItemsAsync(source.Select((item) => item.FromStorageItem()).ToList(), destination, showDialog, registerHistory);
        }

        public async Task<ReturnResult> MoveItemAsync(IStorageItem source, string destination, bool showDialog, bool registerHistory)
        {
            return await MoveItemAsync(source.FromStorageItem(), destination, showDialog, registerHistory);
        }

        public async Task<ReturnResult> MoveItemsAsync(IEnumerable<IStorageItemWithPath> source, IEnumerable<string> destination, bool showDialog, bool registerHistory)
        {
            var sourceDir = PathNormalization.GetParentDir(source.FirstOrDefault()?.Path);
            var destinationDir = PathNormalization.GetParentDir(destination.FirstOrDefault());

            PostedStatusBanner banner = statusCenterViewModel.PostOperationBanner(
                string.Empty,
                string.Format(source.Count() > 1 ? "StatusMovingItemsDetails_Plural".GetLocalized() : "StatusMovingItemsDetails_Singular".GetLocalized(), source.Count(), sourceDir, destinationDir),
                0,
                ReturnResult.InProgress,
                FileOperationType.Move, new CancellationTokenSource());

            var token = banner.CancellationToken;

            var returnStatus = ReturnResult.InProgress;
            banner.ErrorCode.ProgressChanged += (s, e) => returnStatus = e.ToStatus();

            var (collisions, cancelOperation) = await GetCollision(FilesystemOperationType.Move, source, destination, showDialog);

            if (cancelOperation)
            {
                banner.Remove();
                return ReturnResult.Cancelled;
            }

            var sw = new Stopwatch();
            sw.Start();

            itemManipulationModel.ClearSelection();

            IStorageHistory history = await filesystemOperations.MoveItemsAsync(source, destination, collisions, banner.Progress, banner.ErrorCode, token);
            ((IProgress<float>)banner.Progress).Report(100.0f);
            await Task.Yield();

            if (registerHistory && source.Any((item) => !string.IsNullOrWhiteSpace(item.Path)))
            {
                App.HistoryWrapper.AddHistory(history);
            }
            int itemsMoved = history?.Source.Count() ?? 0;

            banner.Remove();
            sw.Stop();

            if (!token.IsCancellationRequested)
            {
                statusCenterViewModel.PostBanner(
                    "StatusMoveComplete".GetLocalized(),
                    string.Format(source.Count() > 1 ? "StatusMovedItemsDetails_Plural".GetLocalized() : "StatusMovedItemsDetails_Singular".GetLocalized(), source.Count(), sourceDir, destinationDir, itemsMoved),
                    0,
                    ReturnResult.Success,
                    FileOperationType.Move);
            }
            else
            {
                statusCenterViewModel.PostBanner(
                    "StatusMoveCanceled".GetLocalized(),
                    string.Format(source.Count() > 1 ?
                        itemsMoved > 1 ? "StatusMoveCanceledDetails_Plural".GetLocalized() : "StatusMoveCanceledDetails_Plural2".GetLocalized()
                        : "StatusMoveCanceledDetails_Singular".GetLocalized(), source.Count(), sourceDir, destinationDir, itemsMoved),
                    0,
                    ReturnResult.Cancelled,
                    FileOperationType.Move);
            }

            return returnStatus;
        }

        public async Task<ReturnResult> MoveItemAsync(IStorageItemWithPath source, string destination, bool showDialog, bool registerHistory)
        {
            PostedStatusBanner banner = statusCenterViewModel.PostBanner(
                string.Empty,
                associatedInstance.FilesystemViewModel.WorkingDirectory,
                0,
                ReturnResult.InProgress,
                FileOperationType.Move);

            var returnStatus = ReturnResult.InProgress;
            banner.ErrorCode.ProgressChanged += (s, e) => returnStatus = e.ToStatus();

            var (collisions, cancelOperation) = await GetCollision(FilesystemOperationType.Move, source.CreateEnumerable(), destination.CreateEnumerable(), showDialog);

            if (cancelOperation)
            {
                banner.Remove();
                return ReturnResult.Cancelled;
            }

            if (cancelOperation)
            {
                banner.Remove();
                return ReturnResult.Cancelled;
            }

            var sw = new Stopwatch();
            sw.Start();

            itemManipulationModel.ClearSelection();

            IStorageHistory history = null;

            if (collisions.First() != FileNameConflictResolveOptionType.Skip)
            {
                history = await filesystemOperations.MoveAsync(source, destination, collisions.First().Convert(), banner.Progress, banner.ErrorCode, cancellationToken);
                ((IProgress<float>)banner.Progress).Report(100.0f);
                await Task.Yield();
            }
            else
            {
                ((IProgress<float>)banner.Progress).Report(100.0f);
                return ReturnResult.Cancelled;
            }

            if (registerHistory && !string.IsNullOrWhiteSpace(source.Path))
            {
                App.HistoryWrapper.AddHistory(history);
            }

            banner.Remove();
            sw.Stop();

            if (sw.Elapsed.TotalSeconds >= 10)
            {
                statusCenterViewModel.PostBanner(
                    "StatusMoveComplete".GetLocalized(),
                    "StatusOperationCompleted".GetLocalized(),
                    0,
                    ReturnResult.Success,
                    FileOperationType.Move);
            }

            return returnStatus;
        }

        public async Task<ReturnResult> MoveItemsFromClipboard(DataPackageView packageView, string destination, bool showDialog, bool registerHistory)
        {
            if (!packageView.Contains(StandardDataFormats.StorageItems))
            {
                // Happens if you copy some text and then you Ctrl+V in Files
                return ReturnResult.BadArgumentException;
            }

            IReadOnlyList<IStorageItem> source;
            try
            {
                source = await packageView.GetStorageItemsAsync();
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80040064 || (uint)ex.HResult == 0x8004006A)
            {
                // Not supported
                return ReturnResult.Failed;
            }
            catch (Exception ex)
            {
                App.Logger.Warn(ex, ex.Message);
                return ReturnResult.UnknownException;
            }
            ReturnResult returnStatus = ReturnResult.InProgress;

            var destinations = new List<string>();
            List<ShellFileItem> binItems = null;
            foreach (IStorageItem item in source)
            {
                if (recycleBinHelpers.IsPathUnderRecycleBin(item.Path))
                {
                    binItems ??= await recycleBinHelpers.EnumerateRecycleBin();
                    var matchingItem = binItems.FirstOrDefault(x => x.RecyclePath == item.Path); // Get original file name
                    destinations.Add(Path.Combine(destination, matchingItem?.FileName ?? item.Name));
                }
                else
                {
                    destinations.Add(Path.Combine(destination, item.Name));
                }
            }

            returnStatus = await MoveItemsAsync(source, destinations, showDialog, registerHistory);

            return returnStatus;
        }

        #endregion Move

        #region Rename

        public async Task<ReturnResult> RenameAsync(IStorageItem source, string newName, NameCollisionOption collision, bool registerHistory)
        {
            var returnCode = FileSystemStatusCode.InProgress;
            var errorCode = new Progress<FileSystemStatusCode>();
            errorCode.ProgressChanged += (s, e) => returnCode = e;

            IStorageHistory history = await filesystemOperations.RenameAsync(source, newName, collision, errorCode, cancellationToken);

            if (registerHistory && !string.IsNullOrWhiteSpace(source.Path))
            {
                App.HistoryWrapper.AddHistory(history);
            }

            await Task.Yield();
            return returnCode.ToStatus();
        }

        public async Task<ReturnResult> RenameAsync(IStorageItemWithPath source, string newName, NameCollisionOption collision, bool registerHistory)
        {
            var returnCode = FileSystemStatusCode.InProgress;
            var errorCode = new Progress<FileSystemStatusCode>();
            errorCode.ProgressChanged += (s, e) => returnCode = e;

            IStorageHistory history = null;

            switch (source.ItemType)
            {
                case FilesystemItemType.Directory:
                    history = await filesystemOperations.RenameAsync(source, newName, collision, errorCode, cancellationToken);
                    break;

                case FilesystemItemType.File:

                    /* Only prompt user when extension has changed,
                       not when file name has changed */
                    if (Path.GetExtension(source.Path) != Path.GetExtension(newName))
                    {
                        var yesSelected = await DialogDisplayHelper.ShowDialogAsync("RenameFileDialogTitle".GetLocalized(), "RenameFileDialog/Text".GetLocalized(), "ButtonYes/Content".GetLocalized(), "ButtonNo/Content".GetLocalized());
                        if (yesSelected)
                        {
                            history = await filesystemOperations.RenameAsync(source, newName, collision, errorCode, cancellationToken);
                            break;
                        }

                        break;
                    }

                    history = await filesystemOperations.RenameAsync(source, newName, collision, errorCode, cancellationToken);
                    break;

                default:
                    history = await filesystemOperations.RenameAsync(source, newName, collision, errorCode, cancellationToken);
                    break;
            }

            if (registerHistory && !string.IsNullOrWhiteSpace(source.Path))
            {
                App.HistoryWrapper.AddHistory(history);
            }

            await Task.Yield();
            return returnCode.ToStatus();
        }

        #endregion Rename

        #endregion IFilesystemHelpers

        private static async Task<(List<FileNameConflictResolveOptionType> collisions, bool cancelOperation)> GetCollision(FilesystemOperationType operationType, IEnumerable<IStorageItemWithPath> source, IEnumerable<string> destination, bool forceDialog)
        {
            List<FilesystemItemsOperationItemModel> incomingItems = new List<FilesystemItemsOperationItemModel>();
            List<FilesystemItemsOperationItemModel> conflictingItems = new List<FilesystemItemsOperationItemModel>();

            Dictionary<string, FileNameConflictResolveOptionType> collisions = new Dictionary<string, FileNameConflictResolveOptionType>();

            for (int i = 0; i < source.Count(); i++)
            {
                incomingItems.Add(new FilesystemItemsOperationItemModel(operationType, source.ElementAt(i).Path ?? source.ElementAt(i).Item.Path, destination.ElementAt(i)));
                collisions.Add(incomingItems.ElementAt(i).SourcePath, FileNameConflictResolveOptionType.GenerateNewName);

                if (destination.Count() > 0 && StorageItemHelpers.Exists(destination.ElementAt(i))) // Same item names in both directories
                {
                    conflictingItems.Add(incomingItems.ElementAt(i));
                }
            }

            bool mustResolveConflicts = conflictingItems.Count > 0;

            if (mustResolveConflicts || forceDialog)
            {
                FilesystemOperationDialog dialog = await FilesystemOperationDialogViewModel.GetDialog(new FilesystemItemsOperationDataModel(
                    operationType,
                    mustResolveConflicts,
                    false,
                    false,
                    incomingItems,
                    conflictingItems));

                ContentDialogResult result = await dialog.ShowAsync();

                if (mustResolveConflicts) // If there were conflicts, result buttons are different
                {
                    if (result != ContentDialogResult.Primary) // Operation was cancelled
                    {
                        return (new List<FileNameConflictResolveOptionType>(), true);
                    }
                }

                collisions.Clear();
                List<IFilesystemOperationItemModel> itemsResult = dialog.ViewModel.GetResult();
                foreach (var item in itemsResult)
                {
                    collisions.Add(item.SourcePath, item.ConflictResolveOption);
                }
            }

            // Since collisions are scrambled, we need to sort them PATH--PATH
            List<FileNameConflictResolveOptionType> newCollisions = new List<FileNameConflictResolveOptionType>();

            for (int i = 0; i < collisions.Count; i++)
            {
                for (int j = 0; j < source.Count(); j++)
                {
                    if (collisions.ElementAt(i).Key == (source.ElementAt(j).Path ?? source.ElementAt(j).Item.Path))
                    {
                        newCollisions.Add(collisions.ElementAt(j).Value);
                    }
                }
            }

            return (newCollisions, false);
        }

        #region Public Helpers

        public static async Task<long> GetItemSize(IStorageItem item)
        {
            if (item == null)
            {
                return 0L;
            }

            if (item.IsOfType(StorageItemTypes.Folder))
            {
                return await CalculateFolderSizeAsync(item.Path);
            }
            else
            {
                return CalculateFileSize(item.Path);
            }
        }

        public static async Task<long> CalculateFolderSizeAsync(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                // In MTP devices calculating folder size would be too slow
                // Also should use StorageFolder methods instead of FindFirstFileExFromApp
                return 0;
            }

            long size = 0;
            FINDEX_INFO_LEVELS findInfoLevel = FINDEX_INFO_LEVELS.FindExInfoBasic;
            int additionalFlags = FIND_FIRST_EX_LARGE_FETCH;

            IntPtr hFile = FindFirstFileExFromApp(
                path + "\\*.*",
                findInfoLevel,
                out WIN32_FIND_DATA findData,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero,
                additionalFlags);

            int count = 0;
            if (hFile.ToInt64() != -1)
            {
                do
                {
                    if (((FileAttributes)findData.dwFileAttributes & FileAttributes.Directory) != FileAttributes.Directory)
                    {
                        size += findData.GetSize();
                        ++count;
                    }
                    else if (((FileAttributes)findData.dwFileAttributes & FileAttributes.Directory) == FileAttributes.Directory)
                    {
                        if (findData.cFileName != "." && findData.cFileName != "..")
                        {
                            string itemPath = Path.Combine(path, findData.cFileName);

                            size += await CalculateFolderSizeAsync(itemPath);
                            ++count;
                        }
                    }
                } while (FindNextFile(hFile, out findData));
                FindClose(hFile);
                return size;
            }
            else
            {
                return 0;
            }
        }

        public static long CalculateFileSize(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                // In MTP devices calculating folder size would be too slow
                // Also should use StorageFolder methods instead of FindFirstFileExFromApp
                return 0;
            }

            long size = 0;
            FINDEX_INFO_LEVELS findInfoLevel = FINDEX_INFO_LEVELS.FindExInfoBasic;
            int additionalFlags = FIND_FIRST_EX_LARGE_FETCH;

            IntPtr hFile = FindFirstFileExFromApp(path, findInfoLevel, out WIN32_FIND_DATA findData, FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero,
                                                  additionalFlags);

            if (hFile.ToInt64() != -1)
            {
                if (((FileAttributes)findData.dwFileAttributes & FileAttributes.Directory) != FileAttributes.Directory)
                {
                    size += findData.GetSize();
                }
                FindClose(hFile);
                Debug.WriteLine($"Individual file size for Progress UI will be reported as: {size} bytes");
                return size;
            }
            else
            {
                return 0;
            }
        }

        public static bool ContainsRestrictedCharacters(string input)
        {
            Regex regex = new Regex("\\\\|\\/|\\:|\\*|\\?|\\\"|\\<|\\>|\\|"); // Restricted symbols for file names
            MatchCollection matches = regex.Matches(input);

            if (matches.Count > 0)
            {
                return true;
            }

            return false;
        }

        public static bool ContainsRestrictedFileName(string input)
        {
            foreach (string name in RestrictedFileNames)
            {
                Regex regex = new Regex($"^{name}($|\\.)(.+)?");
                MatchCollection matches = regex.Matches(input.ToUpper());

                if (matches.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        public async Task OpenShellCommandInExplorerAsync(string shellCommand, NamedPipeAsAppServiceConnection connection)
        {
            Debug.WriteLine("Launching shell command in FullTrustProcess");
            if (connection != null)
            {
                ValueSet value = new ValueSet()
                {
                    { "ShellCommand", shellCommand },
                    { "Arguments", "ShellCommand" }
                };
                await connection.SendMessageAsync(value);
            }
        }

        #endregion Public Helpers

        #region IDisposable

        public void Dispose()
        {
            filesystemOperations?.Dispose();

            associatedInstance = null;
            filesystemOperations = null;
            recycleBinHelpers = null;
        }

        #endregion IDisposable
    }
}