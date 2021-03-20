﻿using Microsoft.Toolkit.Mvvm.Input;
using System;
using System.Windows.Input;
using Windows.UI.Xaml;

namespace Files.Interacts
{
    public class BaseLayoutCommandsViewModel : IDisposable
    {
        #region Private Members

        private readonly IBaseLayoutCommandImplementationModel commandsModel;

        #endregion

        #region Constructor

        public BaseLayoutCommandsViewModel(IBaseLayoutCommandImplementationModel commandsModel)
        {
            this.commandsModel = commandsModel;

            InitializeCommands();
        }

        #endregion

        #region Command Initialization

        private void InitializeCommands()
        {
            RenameItemCommand = new RelayCommand<RoutedEventArgs>(commandsModel.RenameItem);
            CreateShortcutCommand = new RelayCommand<RoutedEventArgs>(commandsModel.CreateShortcut);
            SetAsLockscreenBackgroundItemCommand = new RelayCommand<RoutedEventArgs>(commandsModel.SetAsLockscreenBackgroundItem);
            SetAsDesktopBackgroundItemCommand = new RelayCommand<RoutedEventArgs>(commandsModel.SetAsDesktopBackgroundItem);
            RunAsAdminCommand = new RelayCommand<RoutedEventArgs>(commandsModel.RunAsAdmin);
            RunAsAnotherUserCommand = new RelayCommand<RoutedEventArgs>(commandsModel.RunAsAnotherUser);
            SidebarPinItemCommand = new RelayCommand<RoutedEventArgs>(commandsModel.SidebarPinItem);
            SidebarUnpinItemCommand = new RelayCommand<RoutedEventArgs>(commandsModel.SidebarUnpinItem);
            UnpinDirectoryFromSidebarCommand = new RelayCommand<RoutedEventArgs>(commandsModel.UnpinDirectoryFromSidebar);
            OpenItemCommand = new RelayCommand<RoutedEventArgs>(commandsModel.OpenItem);
            EmptyRecycleBinCommand = new RelayCommand<RoutedEventArgs>(commandsModel.EmptyRecycleBin);
            QuickLookCommand = new RelayCommand<RoutedEventArgs>(commandsModel.QuickLook);
            CopyItemCommand = new RelayCommand<RoutedEventArgs>(commandsModel.CopyItem);
            CutItemCommand = new RelayCommand<RoutedEventArgs>(commandsModel.CutItem);
            RestoreItemCommand = new RelayCommand<RoutedEventArgs>(commandsModel.RestoreItem);
            DeleteItemCommand = new RelayCommand<RoutedEventArgs>(commandsModel.DeleteItem);
            ShowFolderPropertiesCommand = new RelayCommand<RoutedEventArgs>(commandsModel.ShowFolderProperties);
            ShowPropertiesCommand = new RelayCommand<RoutedEventArgs>(commandsModel.ShowProperties);
            OpenFileLocationCommand = new RelayCommand<RoutedEventArgs>(commandsModel.OpenFileLocation);
            OpenItemWithApplicationPickerCommand = new RelayCommand<RoutedEventArgs>(commandsModel.OpenItemWithApplicationPicker);
            OpenDirectoryInNewTabCommand = new RelayCommand<RoutedEventArgs>(commandsModel.OpenDirectoryInNewTab);
            OpenDirectoryInNewPaneCommand = new RelayCommand<RoutedEventArgs>(commandsModel.OpenDirectoryInNewPane);
            OpenInNewWindowItemCommand = new RelayCommand<RoutedEventArgs>(commandsModel.OpenInNewWindowItem);
        }

        #endregion

        #region Commands

        public ICommand RenameItemCommand { get; private set; }

        public ICommand CreateShortcutCommand { get; private set; }

        public ICommand SetAsLockscreenBackgroundItemCommand { get; private set; }

        public ICommand SetAsDesktopBackgroundItemCommand { get; private set; }

        public ICommand RunAsAdminCommand { get; private set; }

        public ICommand RunAsAnotherUserCommand { get; private set; }

        public ICommand SidebarPinItemCommand { get; private set; }

        public ICommand SidebarUnpinItemCommand { get; private set; }

        public ICommand OpenItemCommand { get; private set; }

        public ICommand UnpinDirectoryFromSidebarCommand { get; private set; }

        public ICommand EmptyRecycleBinCommand { get; private set; }

        public ICommand QuickLookCommand { get; private set; }
        
        public ICommand CopyItemCommand { get; private set; }

        public ICommand CutItemCommand { get; private set; }

        public ICommand RestoreItemCommand { get; private set; }

        public ICommand DeleteItemCommand { get; private set; }

        public ICommand ShowFolderPropertiesCommand { get; private set; }

        public ICommand ShowPropertiesCommand { get; private set; }

        public ICommand OpenFileLocationCommand { get; private set; }

        public ICommand OpenItemWithApplicationPickerCommand { get; private set; }

        public ICommand OpenDirectoryInNewTabCommand { get; private set; }

        public ICommand OpenDirectoryInNewPaneCommand { get; private set; }

        public ICommand OpenInNewWindowItemCommand { get; private set; }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            commandsModel?.Dispose();
        }

        #endregion
    }
}
