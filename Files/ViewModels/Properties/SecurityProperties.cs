using Files.Common;
using Files.DataModels.NavigationControlItems;
using Files.Filesystem;
using Files.Filesystem.Permissions;
using Files.Helpers;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation.Collections;

namespace Files.ViewModels.Properties
{
    public class SecurityProperties : ObservableObject
    {
        public ListedItem Item { get; }

        public SecurityProperties(ListedItem item)
        {
            Item = item;
            IsFolder = Item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder && !Item.IsShortcutItem;

            InitCommands();
        }

        public SecurityProperties(DriveItem item)
        {
            Item = new ListedItem()
            {
                ItemName = item.Text,
                ItemPath = item.Path,
                PrimaryItemAttribute = Windows.Storage.StorageItemTypes.Folder
            };

            IsFolder = true;

            InitCommands();
        }

        private void InitCommands()
        {
            EditOwnerCommand = new RelayCommand(EditOwner, () => FilePermissions != null);
            AddRulesForUserCommand = new RelayCommand(AddRulesForUser, () => FilePermissions != null && FilePermissions.CanReadFilePermissions);
            RemoveRulesForUserCommand = new RelayCommand(RemoveRulesForUser, () => FilePermissions != null && FilePermissions.CanReadFilePermissions && SelectedRuleForUser != null);
            AddAccessRuleCommand = new RelayCommand(AddAccessRule, () => FilePermissions != null && FilePermissions.CanReadFilePermissions);
            RemoveAccessRuleCommand = new RelayCommand(RemoveAccessRule, () => FilePermissions != null && FilePermissions.CanReadFilePermissions && SelectedAccessRules != null);
            DisableInheritanceCommand = new RelayCommand(DisableInheritance, () =>
            {
                return FilePermissions != null && FilePermissions.CanReadFilePermissions && (FilePermissions.AreAccessRulesProtected != isProtected);
            });
            SetDisableInheritanceOptionCommand = new RelayCommand<string>(SetDisableInheritanceOption);
            ReplaceChildPermissionsCommand = new RelayCommand(ReplaceChildPermissions, () => FilePermissions != null && FilePermissions.CanReadFilePermissions);
        }

        public RelayCommand EditOwnerCommand { get; set; }
        public RelayCommand AddRulesForUserCommand { get; set; }
        public RelayCommand RemoveRulesForUserCommand { get; set; }
        public RelayCommand AddAccessRuleCommand { get; set; }
        public RelayCommand RemoveAccessRuleCommand { get; set; }
        public RelayCommand DisableInheritanceCommand { get; set; }
        public RelayCommand<string> SetDisableInheritanceOptionCommand { get; set; }
        public RelayCommand ReplaceChildPermissionsCommand { get; set; }

        private FilePermissionsManager filePermissions;
        public FilePermissionsManager FilePermissions
        {
            get => filePermissions;
            set
            {
                if (SetProperty(ref filePermissions, value))
                {
                    EditOwnerCommand.NotifyCanExecuteChanged();
                    AddRulesForUserCommand.NotifyCanExecuteChanged();
                    RemoveRulesForUserCommand.NotifyCanExecuteChanged();
                    AddAccessRuleCommand.NotifyCanExecuteChanged();
                    RemoveAccessRuleCommand.NotifyCanExecuteChanged();
                    DisableInheritanceCommand.NotifyCanExecuteChanged();
                    ReplaceChildPermissionsCommand.NotifyCanExecuteChanged();
                }
            }
        }

        private RulesForUser selectedRuleForUser;
        public RulesForUser SelectedRuleForUser
        {
            get => selectedRuleForUser;
            set
            {
                if (SetProperty(ref selectedRuleForUser, value))
                {
                    RemoveRulesForUserCommand.NotifyCanExecuteChanged();
                }
            }
        }

        private List<FileSystemAccessRuleForUI> selectedAccessRules;
        public List<FileSystemAccessRuleForUI> SelectedAccessRules
        {
            get => selectedAccessRules;
            set
            {
                if (SetProperty(ref selectedAccessRules, value))
                {
                    RemoveAccessRuleCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(SelectedAccessRule));
                }
            }
        }

        public FileSystemAccessRuleForUI SelectedAccessRule => SelectedAccessRules?.FirstOrDefault();

        private bool isFolder;
        public bool IsFolder
        {
            get => isFolder;
            set => SetProperty(ref isFolder, value);
        }

        private bool isProtected;
        private bool preserveInheritance;

        public string DisableInheritanceOption
        {
            get
            {
                if (!isProtected)
                {
                    return "SecurityAdvancedInheritedEnable/Text".GetLocalized();
                }
                else if (preserveInheritance)
                {
                    return "SecurityAdvancedInheritedConvert/Text".GetLocalized();
                }
                else
                {
                    return "SecurityAdvancedInheritedRemove/Text".GetLocalized();
                }
            }
        }

        private async void DisableInheritance()
        {
            if (await SetAccessRuleProtection(isProtected, preserveInheritance))
            {
                GetFilePermissions(); // Refresh file permissions
            }
        }

        private void SetDisableInheritanceOption(string options)
        {
            isProtected = Boolean.Parse(options.Split(',')[0]);
            preserveInheritance = Boolean.Parse(options.Split(',')[1]);
            OnPropertyChanged(nameof(DisableInheritanceOption));
            DisableInheritanceCommand.NotifyCanExecuteChanged();
        }

        private async void ReplaceChildPermissions()
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection != null)
            {
                var value = new ValueSet()
                {
                    { "Arguments", "FileOperation" },
                    { "fileop", "ReplaceChildPermissions" },
                    { "filepath", Item.ItemPath },
                    { "isfolder", Item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder && !Item.IsShortcutItem }
                };
                await connection.SendMessageAsync(value);
            }
        }

        private async void AddAccessRule()
        {
            var pickedObject = await OpenObjectPicker();
            if (pickedObject != null)
            {
                FilePermissions.AccessRules.Add(new FileSystemAccessRuleForUI(IsFolder)
                {
                    AccessControlType = AccessControlType.Allow,
                    FileSystemRights = FileSystemRights.ReadAndExecute,
                    IdentityReference = pickedObject,
                    InheritanceFlags = IsFolder ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                    PropagationFlags = PropagationFlags.None
                });
            }
        }

        private void RemoveAccessRule()
        {
            if (SelectedAccessRules != null)
            {
                foreach (var rule in SelectedAccessRules)
                {
                    FilePermissions.AccessRules.Remove(rule);
                }
            }
        }

        private async void EditOwner()
        {
            var pickedObject = await OpenObjectPicker();
            if (pickedObject != null)
            {
                if (await SetFileOwner(pickedObject))
                {
                    GetFilePermissions(); // Refresh file permissions
                }
            }
        }

        private async void AddRulesForUser()
        {
            var pickedObject = await OpenObjectPicker();
            if (pickedObject != null)
            {
                if (!FilePermissions.RulesForUsers.Any(x => x.UserGroup.Sid == pickedObject))
                {
                    // No existing rules, add user to list
                    FilePermissions.RulesForUsers.Add(RulesForUser.ForUser(FilePermissions.AccessRules, IsFolder, pickedObject));
                }
            }
        }

        private void RemoveRulesForUser()
        {
            if (SelectedRuleForUser != null)
            {
                SelectedRuleForUser.AllowRights = 0;
                SelectedRuleForUser.DenyRights = 0;

                if (!FilePermissions.AccessRules.Any(x => x.IdentityReference == SelectedRuleForUser.UserGroup.Sid))
                {
                    // No remaining rules, remove user from list
                    FilePermissions.RulesForUsers.Remove(SelectedRuleForUser);
                }
            }
        }

        public async void GetFilePermissions()
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection != null)
            {
                var value = new ValueSet()
                {
                    { "Arguments", "FileOperation" },
                    { "fileop", "GetFilePermissions" },
                    { "filepath", Item.ItemPath },
                    { "isfolder", Item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder && !Item.IsShortcutItem }
                };
                var (status, response) = await connection.SendMessageForResponseAsync(value);
                if (status == Windows.ApplicationModel.AppService.AppServiceResponseStatus.Success)
                {
                    var filePermissions = JsonConvert.DeserializeObject<FilePermissions>((string)response["FilePermissions"]);
                    FilePermissions = new FilePermissionsManager(filePermissions);
                }
            }
        }

        public async Task<bool> SetFilePermissions()
        {
            if (FilePermissions == null || !FilePermissions.CanReadFilePermissions)
            {
                return true;
            }

            var connection = await AppServiceConnectionHelper.Instance;
            if (connection != null)
            {
                var value = new ValueSet()
                {
                    { "Arguments", "FileOperation" },
                    { "fileop", "SetFilePermissions" },
                    { "permissions", JsonConvert.SerializeObject(FilePermissions.ToFilePermissions()) }
                };
                var (status, response) = await connection.SendMessageForResponseAsync(value);
                return (status == Windows.ApplicationModel.AppService.AppServiceResponseStatus.Success
                    && response.Get("Success", false));
            }
            return false;
        }

        public async Task<bool> SetFileOwner(string ownerSid)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection != null)
            {
                var value = new ValueSet()
                {
                    { "Arguments", "FileOperation" },
                    { "fileop", "SetFileOwner" },
                    { "filepath", Item.ItemPath },
                    { "isfolder", Item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder && !Item.IsShortcutItem },
                    { "ownersid", ownerSid }
                };
                var (status, response) = await connection.SendMessageForResponseAsync(value);
                return (status == Windows.ApplicationModel.AppService.AppServiceResponseStatus.Success
                    && response.Get("Success", false));
            }
            return false;
        }

        public async Task<string> OpenObjectPicker()
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection != null)
            {
                var value = new ValueSet()
                {
                    { "Arguments", "FileOperation" },
                    { "fileop", "OpenObjectPicker" },
                    { "HWND", NativeWinApiHelper.CoreWindowHandle.ToInt64() }
                };
                var (status, response) = await connection.SendMessageForResponseAsync(value);
                if (status == Windows.ApplicationModel.AppService.AppServiceResponseStatus.Success)
                {
                    return response.Get("PickedObject", (string)null);
                }
            }
            return null;
        }

        public async Task<bool> SetAccessRuleProtection(bool isProtected, bool preserveInheritance)
        {
            var connection = await AppServiceConnectionHelper.Instance;
            if (connection != null)
            {
                var value = new ValueSet()
                {
                    { "Arguments", "FileOperation" },
                    { "fileop", "SetAccessRuleProtection" },
                    { "filepath", Item.ItemPath },
                    { "isfolder", Item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder && !Item.IsShortcutItem },
                    { "isprotected", isProtected },
                    { "preserveinheritance", preserveInheritance }
                };
                var (status, response) = await connection.SendMessageForResponseAsync(value);
                return (status == Windows.ApplicationModel.AppService.AppServiceResponseStatus.Success
                    && response.Get("Success", false));
            }
            return false;
        }
    }
}
