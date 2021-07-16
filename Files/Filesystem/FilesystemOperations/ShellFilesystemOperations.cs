using Files.Common;
using Files.Enums;
using Files.Extensions;
using Files.Filesystem.FilesystemHistory;
using Files.Helpers;
using Files.Interacts;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace Files.Filesystem
{
    public class ShellFilesystemOperations : IFilesystemOperations
    {
        #region Private Members

        private IShellPage associatedInstance;

        private ItemManipulationModel itemManipulationModel => associatedInstance.SlimContentPage?.ItemManipulationModel;

        private FilesystemOperations filesystemOperations;

        private RecycleBinHelpers recycleBinHelpers;

        #endregion Private Members

        #region Constructor

        public ShellFilesystemOperations(IShellPage associatedInstance)
        {
            this.associatedInstance = associatedInstance;
            filesystemOperations = new FilesystemOperations(associatedInstance);
            recycleBinHelpers = new RecycleBinHelpers();
        }

        #endregion Constructor

        public async Task<IStorageHistory> CopyAsync(IStorageItem source, string destination, NameCollisionOption collision, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await CopyAsync(source.FromStorageItem(),
                                                    destination,
                                                    collision,
                                                    progress,
                                                    errorCode,
                                                    cancellationToken);
        }

        public async Task<IStorageHistory> CopyAsync(IStorageItemWithPath source, string destination, NameCollisionOption collision, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await CopyItemsAsync(source.CreateEnumerable(), destination.CreateEnumerable(), collision.ConvertBack().CreateEnumerable(), progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> CopyItemsAsync(IEnumerable<IStorageItem> source, IEnumerable<string> destination, IEnumerable<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await CopyItemsAsync(source.Select((item) => item.FromStorageItem()).ToList(), destination, collisions, progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> CopyItemsAsync(IEnumerable<IStorageItemWithPath> source, IEnumerable<string> destination, IEnumerable<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection == null || source.Any(x => string.IsNullOrWhiteSpace(x.Path) || x.Path.StartsWith(@"\\?\")) || destination.Any(x => string.IsNullOrWhiteSpace(x) || x.StartsWith(@"\\?\")))
            {
                // Fallback to builtin file operations
                return await filesystemOperations.CopyItemsAsync(source, destination, collisions, progress, errorCode, cancellationToken);
            }

            source = source.Where((src, index) => collisions.ElementAt(index) != FileNameConflictResolveOptionType.Skip).ToList();
            destination = destination.Where((src, index) => collisions.ElementAt(index) != FileNameConflictResolveOptionType.Skip).ToList();
            collisions = collisions.Where(c => c != FileNameConflictResolveOptionType.Skip).ToList();

            var operationID = Guid.NewGuid().ToString();
            using var _ = cancellationToken.Register(CancelOperation, operationID, false);

            EventHandler<Dictionary<string, object>> handler = (s, e) => OnProgressUpdated(s, e, progress);
            connection.RequestReceived += handler;

            var sourceReplace = source.Where((src, index) => collisions.ElementAt(index) == FileNameConflictResolveOptionType.ReplaceExisting);
            var destinationReplace = destination.Where((src, index) => collisions.ElementAt(index) == FileNameConflictResolveOptionType.ReplaceExisting);
            var sourceRename = source.Where((src, index) => collisions.ElementAt(index) == FileNameConflictResolveOptionType.GenerateNewName);
            var destinationRename = destination.Where((src, index) => collisions.ElementAt(index) == FileNameConflictResolveOptionType.GenerateNewName);

            var result = (FilesystemResult)true;
            var copiedItems = new List<string>();
            var copiedSources = new List<string>();
            if (sourceRename.Any())
            {
                var (status, response) = await connection.SendMessageForResponseAsync(new ValueSet()
                {
                    { "Arguments", "FileOperation" },
                    { "fileop", "CopyItem" },
                    { "operationID", operationID },
                    { "filepath", string.Join('|', sourceRename.Select(s => s.Path)) },
                    { "destpath", string.Join('|', destinationRename) },
                    { "overwrite", false }
                });
                result &= (FilesystemResult)(status == AppServiceResponseStatus.Success
                    && response.Get("Success", false));
                copiedItems.AddRange(JsonConvert.DeserializeObject<IEnumerable<string>>(response["CopiedItems"] as string));
                copiedSources.AddRange(JsonConvert.DeserializeObject<IEnumerable<string>>(response.Get("CopiedSources", "")) ?? Enumerable.Empty<string>());
            }
            if (sourceReplace.Any())
            {
                var (status, response) = await connection.SendMessageForResponseAsync(new ValueSet()
                {
                    { "Arguments", "FileOperation" },
                    { "fileop", "CopyItem" },
                    { "operationID", operationID },
                    { "filepath", string.Join('|', sourceReplace.Select(s => s.Path)) },
                    { "destpath", string.Join('|', destinationReplace) },
                    { "overwrite", true }
                });
                result &= (FilesystemResult)(status == AppServiceResponseStatus.Success
                    && response.Get("Success", false));
                copiedSources.AddRange(JsonConvert.DeserializeObject<IEnumerable<string>>(response.Get("CopiedSources", "")) ?? Enumerable.Empty<string>());
            }

            if (connection != null)
            {
                connection.RequestReceived -= handler;
            }

            if (result)
            {
                progress?.Report(100.0f);
                errorCode?.Report(FileSystemStatusCode.Success);
                if (sourceRename.Any() && copiedItems.Count() == sourceRename.Count())
                {
                    return new StorageHistory(FileOperationType.Copy, sourceRename,
                        copiedItems.Select((item, index) => StorageItemHelpers.FromPathAndType(item, sourceRename.ElementAt(index).ItemType)));
                }
                return null; // Cannot undo overwrite operation
            }
            else
            {
                // Retry failed operations
                var copiedZip = source.Zip(destination, (src, dest) => new { src, dest }).Zip(collisions, (z1, coll) => new { z1.src, z1.dest, coll }).Where(x => !copiedSources.Contains(x.src.Path));
                return await filesystemOperations.CopyItemsAsync(copiedZip.Select(x => x.src), copiedZip.Select(x => x.dest), copiedZip.Select(x => x.coll), progress, errorCode, cancellationToken);
            }
        }

        public async Task<(IStorageHistory, IStorageItem)> CreateAsync(IStorageItemWithPath source, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await filesystemOperations.CreateAsync(source, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> DeleteAsync(IStorageItem source, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, bool permanently, CancellationToken cancellationToken)
        {
            return await DeleteAsync(source.FromStorageItem(),
                                                      progress,
                                                      errorCode,
                                                      permanently,
                                                      cancellationToken);
        }

        public async Task<IStorageHistory> DeleteAsync(IStorageItemWithPath source, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, bool permanently, CancellationToken cancellationToken)
        {
            return await DeleteItemsAsync(source.CreateEnumerable(), progress, errorCode, permanently, cancellationToken);
        }

        public async Task<IStorageHistory> DeleteItemsAsync(IEnumerable<IStorageItem> source, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, bool permanently, CancellationToken cancellationToken)
        {
            return await DeleteItemsAsync(source.Select((item) => item.FromStorageItem()), progress, errorCode, permanently, cancellationToken);
        }

        public async Task<IStorageHistory> DeleteItemsAsync(IEnumerable<IStorageItemWithPath> source, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, bool permanently, CancellationToken cancellationToken)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection == null || source.Any(x => string.IsNullOrWhiteSpace(x.Path) || x.Path.StartsWith(@"\\?\")))
            {
                // Fallback to builtin file operations
                return await filesystemOperations.DeleteItemsAsync(source, progress, errorCode, permanently, cancellationToken);
            }

            var deleleFilePaths = source.Select(s => s.Path);

            var deleteFromRecycleBin = source.Any() ? recycleBinHelpers.IsPathUnderRecycleBin(source.ElementAt(0).Path) : false;
            permanently |= deleteFromRecycleBin;

            if (deleteFromRecycleBin)
            {
                // Recycle bin also stores a file starting with $I for each item
                deleleFilePaths = deleleFilePaths.Concat(source.Select(x => Path.Combine(Path.GetDirectoryName(x.Path), Path.GetFileName(x.Path).Replace("$R", "$I"))));
            }

            var operationID = Guid.NewGuid().ToString();
            using var _ = cancellationToken.Register(CancelOperation, operationID, false);

            EventHandler<Dictionary<string, object>> handler = (s, e) => OnProgressUpdated(s, e, progress);
            connection.RequestReceived += handler;

            var (status, response) = await connection.SendMessageForResponseAsync(new ValueSet()
            {
                { "Arguments", "FileOperation" },
                { "fileop", "DeleteItem" },
                { "operationID", operationID },
                { "filepath", string.Join('|', deleleFilePaths) },
                { "permanently", permanently }
            });
            var result = (FilesystemResult)(status == AppServiceResponseStatus.Success
                && response.Get("Success", false));

            if (connection != null)
            {
                connection.RequestReceived -= handler;
            }

            if (result)
            {
                progress?.Report(100.0f);
                var deletedItems = JsonConvert.DeserializeObject<IEnumerable<string>>(response["DeletedItems"] as string);
                var recycledItems = JsonConvert.DeserializeObject<IEnumerable<string>>(response["RecycledItems"] as string);
                errorCode?.Report(FileSystemStatusCode.Success);
                if (deletedItems != null)
                {
                    foreach (var item in deletedItems)
                    {
                        await associatedInstance.FilesystemViewModel.RemoveFileOrFolderAsync(item);
                    }
                }
                if (!permanently && recycledItems != null && recycledItems.Count() == source.Count())
                {
                    return new StorageHistory(FileOperationType.Recycle, source, recycledItems.Select((item, index) => StorageItemHelpers.FromPathAndType(item, source.ElementAt(index).ItemType)));
                }
                return new StorageHistory(FileOperationType.Delete, source, null);
            }
            else
            {
                // Retry failed operations
                var deletedSources = JsonConvert.DeserializeObject<IEnumerable<string>>(response.Get("DeletedItems", "")) ?? Enumerable.Empty<string>();
                var deletedZip = source.Where(x => !deletedSources.Contains(x.Path));
                return await filesystemOperations.DeleteItemsAsync(deletedZip, progress, errorCode, permanently, cancellationToken);
            }
        }

        public async Task<IStorageHistory> MoveAsync(IStorageItem source, string destination, NameCollisionOption collision, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await MoveAsync(source.FromStorageItem(),
                                                    destination,
                                                    collision,
                                                    progress,
                                                    errorCode,
                                                    cancellationToken);
        }

        public async Task<IStorageHistory> MoveAsync(IStorageItemWithPath source, string destination, NameCollisionOption collision, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await MoveItemsAsync(source.CreateEnumerable(), destination.CreateEnumerable(), collision.ConvertBack().CreateEnumerable(), progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> MoveItemsAsync(IEnumerable<IStorageItem> source, IEnumerable<string> destination, IEnumerable<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await MoveItemsAsync(source.Select((item) => item.FromStorageItem()).ToList(), destination, collisions, progress, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> MoveItemsAsync(IEnumerable<IStorageItemWithPath> source, IEnumerable<string> destination, IEnumerable<FileNameConflictResolveOptionType> collisions, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection == null || source.Any(x => string.IsNullOrWhiteSpace(x.Path) || x.Path.StartsWith(@"\\?\")) || destination.Any(x => string.IsNullOrWhiteSpace(x) || x.StartsWith(@"\\?\")))
            {
                // Fallback to builtin file operations
                return await filesystemOperations.MoveItemsAsync(source, destination, collisions, progress, errorCode, cancellationToken);
            }

            source = source.Where((src, index) => collisions.ElementAt(index) != FileNameConflictResolveOptionType.Skip).ToList();
            destination = destination.Where((src, index) => collisions.ElementAt(index) != FileNameConflictResolveOptionType.Skip).ToList();
            collisions = collisions.Where(c => c != FileNameConflictResolveOptionType.Skip).ToList();

            var operationID = Guid.NewGuid().ToString();
            using var _ = cancellationToken.Register(CancelOperation, operationID, false);

            EventHandler<Dictionary<string, object>> handler = (s, e) => OnProgressUpdated(s, e, progress);
            connection.RequestReceived += handler;

            var sourceReplace = source.Where((src, index) => collisions.ElementAt(index) == FileNameConflictResolveOptionType.ReplaceExisting);
            var destinationReplace = destination.Where((src, index) => collisions.ElementAt(index) == FileNameConflictResolveOptionType.ReplaceExisting);
            var sourceRename = source.Where((src, index) => collisions.ElementAt(index) == FileNameConflictResolveOptionType.GenerateNewName);
            var destinationRename = destination.Where((src, index) => collisions.ElementAt(index) == FileNameConflictResolveOptionType.GenerateNewName);

            var result = (FilesystemResult)true;
            var movedItems = new List<string>();
            var movedSources = new List<string>();
            if (sourceRename.Any())
            {
                var (status, response) = await connection.SendMessageForResponseAsync(new ValueSet()
                {
                    { "Arguments", "FileOperation" },
                    { "fileop", "MoveItem" },
                    { "operationID", operationID },
                    { "filepath", string.Join('|', sourceRename.Select(s => s.Path)) },
                    { "destpath", string.Join('|', destinationRename) },
                    { "overwrite", false }
                });
                result &= (FilesystemResult)(status == AppServiceResponseStatus.Success
                    && response.Get("Success", false));
                movedItems.AddRange(JsonConvert.DeserializeObject<IEnumerable<string>>(response["MovedItems"] as string));
                movedSources.AddRange(JsonConvert.DeserializeObject<IEnumerable<string>>(response.Get("MovedSources", "")) ?? Enumerable.Empty<string>());
            }
            if (sourceReplace.Any())
            {
                var (status, response) = await connection.SendMessageForResponseAsync(new ValueSet()
                {
                    { "Arguments", "FileOperation" },
                    { "fileop", "MoveItem" },
                    { "operationID", operationID },
                    { "filepath", string.Join('|', sourceReplace.Select(s => s.Path)) },
                    { "destpath", string.Join('|', destinationReplace) },
                    { "overwrite", true }
                });
                result &= (FilesystemResult)(status == AppServiceResponseStatus.Success
                    && response.Get("Success", false));
                movedSources.AddRange(JsonConvert.DeserializeObject<IEnumerable<string>>(response.Get("MovedSources", "")) ?? Enumerable.Empty<string>());
            }

            if (connection != null)
            {
                connection.RequestReceived -= handler;
            }

            if (result)
            {
                progress?.Report(100.0f);
                errorCode?.Report(FileSystemStatusCode.Success);
                if (sourceRename.Any() && movedItems.Count() == sourceRename.Count())
                {
                    return new StorageHistory(FileOperationType.Move, sourceRename,
                        movedItems.Select((item, index) => StorageItemHelpers.FromPathAndType(item, sourceRename.ElementAt(index).ItemType)));
                }
                return null; // Cannot undo overwrite operation
            }
            else
            {
                // Retry failed operations
                var movedZip = source.Zip(destination, (src, dest) => new { src, dest }).Zip(collisions, (z1, coll) => new { z1.src, z1.dest, coll }).Where(x => !movedSources.Contains(x.src.Path));
                return await filesystemOperations.MoveItemsAsync(movedZip.Select(x => x.src), movedZip.Select(x => x.dest), movedZip.Select(x => x.coll), progress, errorCode, cancellationToken);
            }
        }

        public async Task<IStorageHistory> RenameAsync(IStorageItem source, string newName, NameCollisionOption collision, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            return await RenameAsync(StorageItemHelpers.FromStorageItem(source), newName, collision, errorCode, cancellationToken);
        }

        public async Task<IStorageHistory> RenameAsync(IStorageItemWithPath source, string newName, NameCollisionOption collision, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection == null || string.IsNullOrWhiteSpace(source.Path) || source.Path.StartsWith(@"\\?\"))
            {
                // Fallback to builtin file operations
                return await filesystemOperations.RenameAsync(source, newName, collision, errorCode, cancellationToken);
            }

            var (status, response) = await connection.SendMessageForResponseAsync(new ValueSet()
            {
                { "Arguments", "FileOperation" },
                { "fileop", "RenameItem" },
                { "operationID", Guid.NewGuid().ToString() },
                { "filepath", source.Path },
                { "newName", newName },
                { "overwrite", collision == NameCollisionOption.ReplaceExisting }
            });
            var result = (FilesystemResult)(status == AppServiceResponseStatus.Success
                && response.Get("Success", false));

            if (result)
            {
                var renamedItems = JsonConvert.DeserializeObject<IEnumerable<string>>(response["RenamedItems"] as string);
                errorCode?.Report(FileSystemStatusCode.Success);
                if (collision != NameCollisionOption.ReplaceExisting && renamedItems != null && renamedItems.Count() == 1)
                {
                    return new StorageHistory(FileOperationType.Rename, source,
                        StorageItemHelpers.FromPathAndType(renamedItems.Single(), source.ItemType));
                }
                return null; // Cannot undo overwrite operation
            }
            else
            {
                // Retry failed operations
                return await filesystemOperations.RenameAsync(source, newName, collision, errorCode, cancellationToken);
            }
        }

        public async Task<IStorageHistory> RestoreFromTrashAsync(IStorageItemWithPath source, string destination, IProgress<float> progress, IProgress<FileSystemStatusCode> errorCode, CancellationToken cancellationToken)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection == null || string.IsNullOrWhiteSpace(source.Path) || source.Path.StartsWith(@"\\?\") || string.IsNullOrWhiteSpace(destination) || destination.StartsWith(@"\\?\"))
            {
                // Fallback to builtin file operations
                return await filesystemOperations.RestoreFromTrashAsync(source, destination, progress, errorCode, cancellationToken);
            }

            var operationID = Guid.NewGuid().ToString();
            using var _ = cancellationToken.Register(CancelOperation, operationID, false);

            EventHandler<Dictionary<string, object>> handler = (s, e) => OnProgressUpdated(s, e, progress);
            connection.RequestReceived += handler;

            var (status, response) = await connection.SendMessageForResponseAsync(new ValueSet()
            {
                { "Arguments", "FileOperation" },
                { "fileop", "MoveItem" },
                { "operationID", operationID },
                { "filepath", source.Path },
                { "destpath", destination },
                { "overwrite", false }
            });
            var result = (FilesystemResult)(status == AppServiceResponseStatus.Success
                && response.Get("Success", false));

            if (connection != null)
            {
                connection.RequestReceived -= handler;
            }

            if (result)
            {
                progress?.Report(100.0f);
                var movedItems = JsonConvert.DeserializeObject<IEnumerable<string>>(response["MovedItems"] as string);
                errorCode?.Report(FileSystemStatusCode.Success);
                if (movedItems != null && movedItems.Count() == 1)
                {
                    // Recycle bin also stores a file starting with $I for each item
                    await DeleteAsync(StorageItemHelpers.FromPathAndType(
                        Path.Combine(Path.GetDirectoryName(source.Path), Path.GetFileName(source.Path).Replace("$R", "$I")), source.ItemType),
                        null, null, true, cancellationToken);
                    return new StorageHistory(FileOperationType.Restore, source,
                        StorageItemHelpers.FromPathAndType(movedItems.Single(), source.ItemType));
                }
                return null; // Cannot undo overwrite operation
            }
            else
            {
                // Retry failed operations
                return await filesystemOperations.RestoreFromTrashAsync(source, destination, progress, errorCode, cancellationToken);
            }
        }

        private void OnProgressUpdated(object sender, Dictionary<string, object> message, IProgress<float> progress)
        {
            if (message.ContainsKey("OperationID"))
            {
                var value = (long)message["Progress"];
                progress?.Report(value);
            }
        }

        private async void CancelOperation(object operationID)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection != null)
            {
                await connection.SendMessageAsync(new ValueSet()
                {
                    { "Arguments", "FileOperation" },
                    { "fileop", "CancelOperation" },
                    { "operationID", (string)operationID }
                });
            }
        }

        #region IDisposable

        public void Dispose()
        {
            filesystemOperations?.Dispose();

            filesystemOperations = null;
            recycleBinHelpers = null;
            associatedInstance = null;
        }

        #endregion IDisposable
    }
}
