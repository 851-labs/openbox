using System;
using System.ComponentModel;
using System.Linq;
using OpenBox.Apps;
using OpenBox.Diagnostics;
using OpenBox.Packages;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace OpenBox.Store
{
    /// <summary>
    /// The store's apps. A adds one to the homescreen (a package app is downloaded and installed
    /// on the console first), or offers to uninstall it if it's there. The Store is part of
    /// openbox, not an app, so B is plain back navigation: it goes home.
    /// </summary>
    public sealed partial class StorePage : Page, IBackHandler
    {
        private bool _busy;

        public StorePage()
        {
            this.InitializeComponent();
            Hints.Show("A", "Install or uninstall", "B", "Back");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadAsync();
        }

        bool IBackHandler.HandleBack()
        {
            App.GoHome();
            return true;
        }

        private async void LoadAsync()
        {
            AppList.ItemsSource = null;
            StatusPanel.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
            StatusText.Text = "Loading the store...";
            RetryButton.Visibility = Visibility.Collapsed;
            try
            {
                var apps = await StoreIndex.LoadAsync();
                await AppLibrary.UpdateFromStoreAsync(apps);
                await AppLibrary.GetInstalledAsync();
                AppList.ItemsSource = apps.Select(app => new StoreItem(app, AppLibrary.IsInstalled(app.Id))).ToList();
                foreach (StoreItem item in AppList.ItemsSource as System.Collections.IEnumerable)
                {
                    // Still installing from an earlier visit.
                    var installation = item.App.IsPackage ? PackageApps.GetInstallation(item.App) : null;
                    if (installation != null && !item.IsInstalled)
                    {
                        item.Follow(installation);
                    }
                }
                if (apps.Count == 0)
                {
                    ShowStatus("There are no apps in the store yet.", false);
                    return;
                }
                StatusPanel.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = false;
                AppList.UpdateLayout();
                (AppList.ContainerFromIndex(0) as Control)?.Focus(FocusState.Keyboard);
            }
            catch (Exception ex)
            {
                Log.Error("Loading the store failed", ex);
                ShowStatus("Couldn't reach the store. Check the console's internet connection and try again.\n" + ex.Message, true);
            }
        }

        private void ShowStatus(string text, bool canRetry)
        {
            StatusPanel.Visibility = Visibility.Visible;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            StatusText.Text = text;
            RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
            if (canRetry)
            {
                RetryButton.Focus(FocusState.Keyboard);
            }
        }

        private async void AppList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = (StoreItem)e.ClickedItem;
            if (_busy || item.IsInstalling)
            {
                return;
            }
            _busy = true;
            try
            {
                if (item.IsInstalled)
                {
                    if (await AppActions.UninstallAsync(item.App, true))
                    {
                        item.IsInstalled = false;
                    }
                }
                else if (item.App.IsPackage)
                {
                    item.Follow(PackageApps.Install(item.App));
                }
                else
                {
                    await AppLibrary.InstallAsync(item.App);
                    item.IsInstalled = true;
                }
            }
            finally
            {
                _busy = false;
            }
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            LoadingRing.Visibility = Visibility.Visible;
            LoadAsync();
        }
    }

    public sealed class StoreItem : INotifyPropertyChanged
    {
        private bool _isInstalled;
        private Installation _installation;

        public StoreItem(AppEntry app, bool isInstalled)
        {
            App = app;
            _isInstalled = isInstalled;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public AppEntry App { get; private set; }

        public bool IsInstalled
        {
            get { return _isInstalled; }
            set
            {
                _isInstalled = value;
                Changed();
            }
        }

        public bool IsInstalling
        {
            get { return _installation != null && !_installation.Done && !_installation.Failed; }
        }

        public string Status
        {
            get
            {
                if (_installation != null && !_isInstalled)
                {
                    return _installation.Status;
                }
                return _isInstalled ? "\u2713 On your homescreen" : App.IsPackage ? "Install" : "Add";
            }
        }

        public double StatusOpacity
        {
            get { return _isInstalled ? 0.6 : 1.0; }
        }

        /// <summary>Shows a package app's installation as it goes.</summary>
        internal void Follow(Installation installation)
        {
            if (_installation != null)
            {
                _installation.Changed -= OnInstallationChanged;
            }
            _installation = installation;
            _installation.Changed += OnInstallationChanged;
            OnInstallationChanged(installation, EventArgs.Empty);
        }

        private void OnInstallationChanged(object sender, EventArgs e)
        {
            if (_installation.Done)
            {
                _isInstalled = true;
            }
            Changed();
        }

        private void Changed()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusOpacity)));
        }
    }
}
