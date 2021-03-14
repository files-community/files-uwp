﻿using Files.ViewModels;
using Files.Views;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The Content Dialog item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace Files.Dialogs
{
    public sealed partial class ElevateConfirmDialog : ContentDialog
    {
        public SettingsViewModel AppSettings => App.AppSettings;

        public ElevateConfirmDialog()
        {
            this.InitializeComponent();
        }
    }
}