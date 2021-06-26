﻿using Files.Common;
using FilesFullTrust.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation.Collections;

namespace FilesFullTrust.MessageHandlers
{
    public class FileOperationsHandler : MessageHandler
    {
        public void Initialize(NamedPipeServerStream connection)
        {
        }

        public async Task ParseArgumentsAsync(NamedPipeServerStream connection, Dictionary<string, object> message, string arguments)
        {
            switch (arguments)
            {
                case "FileOperation":
                    await ParseFileOperationAsync(connection, message);
                    break;
            }
        }

        private async Task ParseFileOperationAsync(NamedPipeServerStream connection, Dictionary<string, object> message)
        {
            switch (message.Get("fileop", ""))
            {
                case "Clipboard":
                    await Win32API.StartSTATask(() =>
                    {
                        System.Windows.Forms.Clipboard.Clear();
                        var fileToCopy = (string)message["filepath"];
                        var operation = (DataPackageOperation)(long)message["operation"];
                        var fileList = new System.Collections.Specialized.StringCollection();
                        fileList.AddRange(fileToCopy.Split('|'));
                        if (operation == DataPackageOperation.Copy)
                        {
                            System.Windows.Forms.Clipboard.SetFileDropList(fileList);
                        }
                        else if (operation == DataPackageOperation.Move)
                        {
                            byte[] moveEffect = new byte[] { 2, 0, 0, 0 };
                            MemoryStream dropEffect = new MemoryStream();
                            dropEffect.Write(moveEffect, 0, moveEffect.Length);
                            var data = new System.Windows.Forms.DataObject();
                            data.SetFileDropList(fileList);
                            data.SetData("Preferred DropEffect", dropEffect);
                            System.Windows.Forms.Clipboard.SetDataObject(data, true);
                        }
                        return true;
                    });
                    break;

                case "DragDrop":
                    var dropPath = (string)message["droppath"];
                    var result2 = await Win32API.StartSTATask(() =>
                    {
                        var rdo = new RemoteDataObject(System.Windows.Forms.Clipboard.GetDataObject());

                        foreach (RemoteDataObject.DataPackage package in rdo.GetRemoteData())
                        {
                            try
                            {
                                if (package.ItemType == RemoteDataObject.StorageType.File)
                                {
                                    string directoryPath = Path.GetDirectoryName(dropPath);
                                    if (!Directory.Exists(directoryPath))
                                    {
                                        Directory.CreateDirectory(directoryPath);
                                    }

                                    string uniqueName = Win32API.GenerateUniquePath(Path.Combine(dropPath, package.Name));
                                    using (FileStream stream = new FileStream(uniqueName, FileMode.CreateNew))
                                    {
                                        package.ContentStream.CopyTo(stream);
                                    }
                                }
                                else
                                {
                                    string directoryPath = Path.Combine(dropPath, package.Name);
                                    if (!Directory.Exists(directoryPath))
                                    {
                                        Directory.CreateDirectory(directoryPath);
                                    }
                                }
                            }
                            finally
                            {
                                package.Dispose();
                            }
                        }
                        return true;
                    });
                    await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", result2 } }, message.Get("RequestID", (string)null));
                    break;

                case "DeleteItem":
                    var fileToDeletePath = (string)message["filepath"];
                    var permanently = (bool)message["permanently"];
                    using (var op = new ShellFileOperations())
                    {
                        op.Options = ShellFileOperations.OperationFlags.NoUI;
                        if (!permanently)
                        {
                            op.Options |= ShellFileOperations.OperationFlags.RecycleOnDelete;
                        }
                        using var shi = new ShellItem(fileToDeletePath);
                        op.QueueDeleteOperation(shi);
                        var deleteTcs = new TaskCompletionSource<bool>();
                        op.PostDeleteItem += (s, e) => deleteTcs.TrySetResult(e.Result.Succeeded);
                        op.PerformOperations();
                        var result = await deleteTcs.Task;
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", result } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "RenameItem":
                    var fileToRenamePath = (string)message["filepath"];
                    var newName = (string)message["newName"];
                    var overwriteOnRename = (bool)message["overwrite"];
                    using (var op = new ShellFileOperations())
                    {
                        op.Options = ShellFileOperations.OperationFlags.NoUI;
                        op.Options |= !overwriteOnRename ? ShellFileOperations.OperationFlags.PreserveFileExtensions | ShellFileOperations.OperationFlags.RenameOnCollision : 0;
                        using var shi = new ShellItem(fileToRenamePath);
                        op.QueueRenameOperation(shi, newName);
                        var renameTcs = new TaskCompletionSource<bool>();
                        op.PostRenameItem += (s, e) => renameTcs.TrySetResult(e.Result.Succeeded);
                        op.PerformOperations();
                        var result = await renameTcs.Task;
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", result } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "MoveItem":
                    var fileToMovePath = (string)message["filepath"];
                    var moveDestination = (string)message["destpath"];
                    var overwriteOnMove = (bool)message["overwrite"];
                    using (var op = new ShellFileOperations())
                    {
                        op.Options = ShellFileOperations.OperationFlags.NoUI;
                        op.Options |= !overwriteOnMove ? ShellFileOperations.OperationFlags.PreserveFileExtensions | ShellFileOperations.OperationFlags.RenameOnCollision : 0;
                        using var shi = new ShellItem(fileToMovePath);
                        using var shd = new ShellFolder(Path.GetDirectoryName(moveDestination));
                        op.QueueMoveOperation(shi, shd, Path.GetFileName(moveDestination));
                        var moveTcs = new TaskCompletionSource<bool>();
                        op.PostMoveItem += (s, e) => moveTcs.TrySetResult(e.Result.Succeeded);
                        op.PerformOperations();
                        var result = await moveTcs.Task;
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", result } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "CopyItem":
                    var fileToCopyPath = (string)message["filepath"];
                    var copyDestination = (string)message["destpath"];
                    var overwriteOnCopy = (bool)message["overwrite"];
                    using (var op = new ShellFileOperations())
                    {
                        op.Options = ShellFileOperations.OperationFlags.NoUI;
                        op.Options |= !overwriteOnCopy ? ShellFileOperations.OperationFlags.PreserveFileExtensions | ShellFileOperations.OperationFlags.RenameOnCollision : 0;
                        using var shi = new ShellItem(fileToCopyPath);
                        using var shd = new ShellFolder(Path.GetDirectoryName(copyDestination));
                        op.QueueCopyOperation(shi, shd, Path.GetFileName(copyDestination));
                        var copyTcs = new TaskCompletionSource<bool>();
                        op.PostCopyItem += (s, e) => copyTcs.TrySetResult(e.Result.Succeeded);
                        op.PerformOperations();
                        var result = await copyTcs.Task;
                        await Win32API.SendMessageAsync(connection, new ValueSet() { { "Success", result } }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "ParseLink":
                    var linkPath = (string)message["filepath"];
                    try
                    {
                        if (linkPath.EndsWith(".lnk"))
                        {
                            using var link = new ShellLink(linkPath, LinkResolution.NoUIWithMsgPump, null, TimeSpan.FromMilliseconds(100));
                            await Win32API.SendMessageAsync(connection, new ValueSet()
                            {
                                { "TargetPath", link.TargetPath },
                                { "Arguments", link.Arguments },
                                { "WorkingDirectory", link.WorkingDirectory },
                                { "RunAsAdmin", link.RunAsAdministrator },
                                { "IsFolder", !string.IsNullOrEmpty(link.TargetPath) && link.Target.IsFolder }
                            }, message.Get("RequestID", (string)null));
                        }
                        else if (linkPath.EndsWith(".url"))
                        {
                            var linkUrl = await Win32API.StartSTATask(() =>
                            {
                                var ipf = new Url.IUniformResourceLocator();
                                (ipf as System.Runtime.InteropServices.ComTypes.IPersistFile).Load(linkPath, 0);
                                ipf.GetUrl(out var retVal);
                                return retVal;
                            });
                            await Win32API.SendMessageAsync(connection, new ValueSet()
                            {
                                { "TargetPath", linkUrl },
                                { "Arguments", null },
                                { "WorkingDirectory", null },
                                { "RunAsAdmin", false },
                                { "IsFolder", false }
                            }, message.Get("RequestID", (string)null));
                        }
                    }
                    catch (Exception ex)
                    {
                        // Could not parse shortcut
                        Program.Logger.Warn(ex, ex.Message);
                        await Win32API.SendMessageAsync(connection, new ValueSet()
                            {
                                { "TargetPath", null },
                                { "Arguments", null },
                                { "WorkingDirectory", null },
                                { "RunAsAdmin", false },
                                { "IsFolder", false }
                            }, message.Get("RequestID", (string)null));
                    }
                    break;

                case "CreateLink":
                case "UpdateLink":
                    var linkSavePath = (string)message["filepath"];
                    var targetPath = (string)message["targetpath"];
                    if (linkSavePath.EndsWith(".lnk"))
                    {
                        var arguments = (string)message["arguments"];
                        var workingDirectory = (string)message["workingdir"];
                        var runAsAdmin = (bool)message["runasadmin"];
                        using var newLink = new ShellLink(targetPath, arguments, workingDirectory);
                        newLink.RunAsAdministrator = runAsAdmin;
                        newLink.SaveAs(linkSavePath); // Overwrite if exists
                    }
                    else if (linkSavePath.EndsWith(".url"))
                    {
                        await Win32API.StartSTATask(() =>
                        {
                            var ipf = new Url.IUniformResourceLocator();
                            ipf.SetUrl(targetPath, Url.IURL_SETURL_FLAGS.IURL_SETURL_FL_GUESS_PROTOCOL);
                            (ipf as System.Runtime.InteropServices.ComTypes.IPersistFile).Save(linkSavePath, false); // Overwrite if exists
                            return true;
                        });
                    }
                    break;

                case "GetFilePermissions":
                    var filePathForPerm = (string)message["filepath"];
                    var isFolder = (bool)message["isfolder"];
                    var filePermissions = FilePermissions.FromFilePath(filePathForPerm, isFolder);
                    await Win32API.SendMessageAsync(connection, new ValueSet()
                    {
                        { "FilePermissions", JsonConvert.SerializeObject(filePermissions) }
                    }, message.Get("RequestID", (string)null));
                    break;

                case "SetFilePermissions":
                    var filePermissionsString = (string)message["permissions"];
                    var filePermissionsToSet = JsonConvert.DeserializeObject<FilePermissions>(filePermissionsString);
                    await Win32API.SendMessageAsync(connection, new ValueSet()
                    {
                        { "Success", filePermissionsToSet.SetPermissions() }
                    }, message.Get("RequestID", (string)null));
                    break;

                case "SetFileOwner":
                    var filePathForPerm2 = (string)message["filepath"];
                    var isFolder2 = (bool)message["isfolder"];
                    var ownerSid = (string)message["ownersid"];
                    var fp = FilePermissions.FromFilePath(filePathForPerm2, isFolder2);
                    await Win32API.SendMessageAsync(connection, new ValueSet()
                    {
                        { "Success", fp.SetOwner(ownerSid) }
                    }, message.Get("RequestID", (string)null));
                    break;

                case "SetAccessRuleProtection":
                    var filePathForPerm3 = (string)message["filepath"];
                    var isFolder3 = (bool)message["isfolder"];
                    var isProtected = (bool)message["isprotected"];
                    var preserveInheritance = (bool)message["preserveinheritance"];
                    var fp2 = FilePermissions.FromFilePath(filePathForPerm3, isFolder3);
                    await Win32API.SendMessageAsync(connection, new ValueSet()
                    {
                        { "Success", fp2.SetAccessRuleProtection(isProtected, preserveInheritance) }
                    }, message.Get("RequestID", (string)null));
                    break;

                case "OpenObjectPicker":
                    var hwnd = (long)message["HWND"];
                    var pickedObject = await FilePermissions.OpenObjectPicker(hwnd);
                    await Win32API.SendMessageAsync(connection, new ValueSet()
                    {
                        { "PickedObject", pickedObject }
                    }, message.Get("RequestID", (string)null));
                    break;
            }
        }

        public void Dispose()
        {
        }
    }
}
