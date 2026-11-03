using System;
using System.ComponentModel;
using System.Linq;
using OpenBox.Apps;
using OpenBox.Diagnostics;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace OpenBox.Store
{
    /// <summary>
    /// The store's apps. A adds one to the homescreen, or offers to remove it if it's there. The
    /// Store is part of openbox, not an app, so B is plain back navigation: it goes home.
    /// </summary>
    public sealed partial class StorePage : Page, IBackHandler
    {
        private bool _busy;

        public StorePage()
        {
            this.InitializeComponent();
            Hints.Show("A", "Add or remove", "B", "Back");
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
            if (_busy)
            {
                return;
            }
            _busy = true;
            try
            {
                if (!item.IsInstalled)
                {
                    await AppLibrary.InstallAsync(item.App);
                    item.IsInstalled = true;
                    return;
                }
                var dialog = new ContentDialog
                {
                    Title = "Remove " + item.App.Name + "?",
                    Content = "It comes off your homescreen. You can add it again from here.",
                    PrimaryButtonText = "Remove",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await AppLibrary.UninstallAsync(item.App.Id);
                    item.IsInstalled = false;
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusOpacity)));
            }
        }

        public string Status
        {
            get { return _isInstalled ? "✓ On your homescreen" : "Add"; }
        }

        public double StatusOpacity
        {
            get { return _isInstalled ? 0.6 : 1.0; }
        }
    }
}
