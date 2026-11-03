using System;
using System.Collections.Generic;
using OpenBox.Apps;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace OpenBox.Home
{
    /// <summary>The homescreen: a tile for each app. A opens it; B in an app comes back here.</summary>
    public sealed partial class HomePage : Page
    {
        private readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private int _focusedIndex;

        public HomePage()
        {
            this.InitializeComponent();
            _clock.Tick += (s, e) => UpdateClock();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            UpdateClock();
            _clock.Start();
            // Store apps may have been added or removed since last time.
            List<AppEntry> apps = await AppLibrary.GetHomeAppsAsync();
            Tiles.ItemsSource = apps;
            Tiles.UpdateLayout();
            var index = Math.Min(_focusedIndex, apps.Count - 1);
            (Tiles.ContainerFromIndex(index) as Control)?.Focus(FocusState.Keyboard);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _clock.Stop();
        }

        private void UpdateClock()
        {
            Clock.Text = DateTime.Now.ToString("t");
        }

        private void Tiles_ItemClick(object sender, ItemClickEventArgs e)
        {
            var app = (AppEntry)e.ClickedItem;
            _focusedIndex = Math.Max(0, Tiles.Items.IndexOf(app));
            App.Open(app);
        }
    }
}
