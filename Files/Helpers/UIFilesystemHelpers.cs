using Files.DataModels;
using Files.Dialogs;
using Files.Enums;
using Files.Filesystem;
using Files.Interacts;
using Microsoft.Toolkit.Uwp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace Files.Helpers
{
    public static class UIFilesystemHelpers
    {
        public static async void CutItem(IShellPage associatedInstance)
        {
            DataPackage dataPackage = new DataPackage()
            {
                RequestedOperation = DataPackageOperation.Move
            };
            List<IStorageItem> items = new List<IStorageItem>();
            FilesystemResult result = (FilesystemResult)false;

            if (associatedInstance.SlimContentPage.IsItemSelected)
            {
                // First, reset DataGrid Rows that may be in "cut" command mode
                associatedInstance.SlimContentPage.ItemManipulationModel.RefreshItemsOpacity();

                foreach (ListedItem listedItem in associatedInstance.SlimContentPage.SelectedItems)
                {
                    // Dim opacities accordingly
                    listedItem.Opacity = Constants.UI.DimItemOpacity;

                    if (listedItem.PrimaryItemAttribute == StorageItemTypes.File)
                    {
                        result = await associatedInstance.FilesystemViewModel.GetFileFromPathAsync(listedItem.ItemPath)
                            .OnSuccess(t => items.Add(t));
                        if (!result)
                        {
                            break;
                        }
                    }
                    else
                    {
                        result = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(listedItem.ItemPath)
                            .OnSuccess(t => items.Add(t));
                        if (!result)
                        {
                            break;
                        }
                    }
                }
                if (result.ErrorCode == FileSystemStatusCode.NotFound)
                {
                    associatedInstance.SlimContentPage.ItemManipulationModel.RefreshItemsOpacity();
                    return;
                }
                else if (result.ErrorCode == FileSystemStatusCode.Unauthorized)
                {
                    // Try again with fulltrust process
                    var connection = await AppServiceConnectionHelper.Instance;
                    if (connection != null)
                    {
                        string filePaths = string.Join('|', associatedInstance.SlimContentPage.SelectedItems.Select(x => x.ItemPath));
                        AppServiceResponseStatus status = await connection.SendMessageAsync(new ValueSet()
                        {
                            { "Arguments", "FileOperation" },
                            { "fileop", "Clipboard" },
                            { "filepath", filePaths },
                            { "operation", (int)DataPackageOperation.Move }
                        });
                        if (status == AppServiceResponseStatus.Success)
                        {
                            return;
                        }
                    }
                    associatedInstance.SlimContentPage.ItemManipulationModel.RefreshItemsOpacity();
                    return;
                }
            }

            if (!items.Any())
            {
                return;
            }
            dataPackage.SetStorageItems(items);
            try
            {
                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();
            }
            catch
            {
                dataPackage = null;
            }
        }

        public static async Task CopyItem(IShellPage associatedInstance)
        {
            DataPackage dataPackage = new DataPackage()
            {
                RequestedOperation = DataPackageOperation.Copy
            };
            List<IStorageItem> items = new List<IStorageItem>();

            string copySourcePath = associatedInstance.FilesystemViewModel.WorkingDirectory;
            FilesystemResult result = (FilesystemResult)false;

            if (associatedInstance.SlimContentPage.IsItemSelected)
            {
                foreach (ListedItem listedItem in associatedInstance.SlimContentPage.SelectedItems)
                {
                    if (listedItem.PrimaryItemAttribute == StorageItemTypes.File)
                    {
                        result = await associatedInstance.FilesystemViewModel.GetFileFromPathAsync(listedItem.ItemPath)
                            .OnSuccess(t => items.Add(t));
                        if (!result)
                        {
                            break;
                        }
                    }
                    else
                    {
                        result = await associatedInstance.FilesystemViewModel.GetFolderFromPathAsync(listedItem.ItemPath)
                            .OnSuccess(t => items.Add(t));
                        if (!result)
                        {
                            break;
                        }
                    }
                }
                if (result.ErrorCode == FileSystemStatusCode.Unauthorized)
                {
                    // Try again with fulltrust process
                    var connection = await AppServiceConnectionHelper.Instance;
                    if (connection != null)
                    {
                        string filePaths = string.Join('|', associatedInstance.SlimContentPage.SelectedItems.Select(x => x.ItemPath));
                        await connection.SendMessageAsync(new ValueSet()
                        {
                            { "Arguments", "FileOperation" },
                            { "fileop", "Clipboard" },
                            { "filepath", filePaths },
                            { "operation", (int)DataPackageOperation.Copy }
                        });
                    }
                    return;
                }
            }

            if (items?.Count > 0)
            {
                dataPackage.SetStorageItems(items);
                try
                {
                    Clipboard.SetContent(dataPackage);
                    Clipboard.Flush();
                }
                catch
                {
                    dataPackage = null;
                }
            }
        }

        public static async Task PasteItemAsync(string destinationPath, IShellPage associatedInstance)
        {
            DataPackageView packageView = await FilesystemTasks.Wrap(() => Task.FromResult(Clipboard.GetContent()));
            if (packageView != null)
            {
                await associatedInstance.FilesystemHelpers.PerformOperationTypeAsync(packageView.RequestedOperation, packageView, destinationPath, false, true);
                associatedInstance.SlimContentPage.ItemManipulationModel.RefreshItemsOpacity();
            }
        }

        public static async Task<bool> RenameFileItemAsync(ListedItem item, string oldName, string newName, IShellPage associatedInstance)
        {
            if (oldName == newName)
            {
                return true;
            }

            ReturnResult renamed = ReturnResult.InProgress;
            if (item.PrimaryItemAttribute == StorageItemTypes.Folder)
            {
                renamed = await associatedInstance.FilesystemHelpers.RenameAsync(StorageItemHelpers.FromPathAndType(item.ItemPath, FilesystemItemType.Directory),
                    newName, NameCollisionOption.FailIfExists, true);
            }
            else
            {
                if (item.IsShortcutItem || !App.AppSettings.ShowFileExtensions)
                {
                    newName += item.FileExtension;
                }

                renamed = await associatedInstance.FilesystemHelpers.RenameAsync(StorageItemHelpers.FromPathAndType(item.ItemPath, FilesystemItemType.File),
                    newName, NameCollisionOption.FailIfExists, true);
            }

            if (renamed == ReturnResult.Success)
            {
                associatedInstance.NavToolbarViewModel.CanGoForward = false;
                return true;
            }
            return false;
        }

        public static async void CreateFileFromDialogResultType(AddItemType itemType, ShellNewEntry itemInfo, IShellPage associatedInstance)
        {
            _ = await CreateFileFromDialogResultTypeForResult(itemType, itemInfo, associatedInstance);
        }

        public static async Task<IStorageItem> CreateFileFromDialogResultTypeForResult(AddItemType itemType, ShellNewEntry itemInfo, IShellPage associatedInstance)
        {
            string currentPath = null;
            if (associatedInstance.SlimContentPage != null)
            {
                currentPath = associatedInstance.FilesystemViewModel.WorkingDirectory;
            }

            // Show rename dialog
            DynamicDialog dialog = DynamicDialogFactory.GetFor_RenameDialog();
            await dialog.ShowAsync();

            if (dialog.DynamicResult != DynamicDialogResult.Primary)
            {
                return null;
            }

            // Create file based on dialog result
            string userInput = dialog.ViewModel.AdditionalData as string;
            var folderRes = await associatedInstance.FilesystemViewModel.GetFolderWithPathFromPathAsync(currentPath);
            FilesystemResult<(ReturnResult, IStorageItem)> created = null;
            if (folderRes)
            {
                switch (itemType)
                {
                    case AddItemType.Folder:
                        userInput = !string.IsNullOrWhiteSpace(userInput) ? userInput : "NewFolder".GetLocalized();
                        created = await FilesystemTasks.Wrap(async () =>
                        {
                            return await associatedInstance.FilesystemHelpers.CreateAsync(
                                StorageItemHelpers.FromPathAndType(System.IO.Path.Combine(folderRes.Result.Path, userInput), FilesystemItemType.Directory),
                                true);
                        });
                        break;

                    case AddItemType.File:
                        userInput = !string.IsNullOrWhiteSpace(userInput) ? userInput : itemInfo?.Name ?? "NewFile".GetLocalized();
                        created = await FilesystemTasks.Wrap(async () =>
                        {
                            return await associatedInstance.FilesystemHelpers.CreateAsync(
                                StorageItemHelpers.FromPathAndType(System.IO.Path.Combine(folderRes.Result.Path, userInput + itemInfo?.Extension), FilesystemItemType.File),
                                true);
                        });
                        break;
                }
            }

            if (created == FileSystemStatusCode.Unauthorized)
            {
                await DialogDisplayHelper.ShowDialogAsync("AccessDeniedCreateDialog/Title".GetLocalized(), "AccessDeniedCreateDialog/Text".GetLocalized());
            }

            return created.Result.Item2;
        }

        public static async Task CreateFolderWithSelectionAsync(IShellPage associatedInstance)
        {
            try
            {
                await CopyItem(associatedInstance);
                var folder = await CreateFileFromDialogResultTypeForResult(AddItemType.Folder, null, associatedInstance);
                if (folder == null)
                {
                    return;
                }
                await associatedInstance.FilesystemHelpers.MoveItemsFromClipboard(Clipboard.GetContent(), folder.Path, false, true);
            }
            catch (Exception ex)
            {
                App.Logger.Warn(ex);
            }
        }

        /// <summary>
        /// Set a single file or folder to hidden or unhidden an refresh the
        /// view after setting the flag
        /// </summary>
        /// <param name="item"></param>
        /// <param name="isHidden"></param>
        public static void SetHiddenAttributeItem(ListedItem item, bool isHidden, ItemManipulationModel itemManipulationModel)
        {
            item.IsHiddenItem = isHidden;
            itemManipulationModel.RefreshItemsOpacity();
        }
    }
}