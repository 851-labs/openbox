using System;
using OpenBox.Packages;
using OpenBox.Web;
using Windows.Data.Json;
using Windows.UI;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace OpenBox.Apps
{
    /// <summary>
    /// One app on the homescreen. One of:
    ///  - a web app (<see cref="Url"/>), shown full screen in <see cref="WebAppPage"/>;
    ///  - a native app built into openbox (<see cref="PageType"/>), a XAML page of its own;
    ///  - an app package of its own (<see cref="Package"/>), which the Store installs on the console
    ///    and the tile launches (Minecraft, through Bandit Launcher).
    /// Store apps are web apps or packages.
    /// </summary>
    public sealed class AppEntry
    {
        private BitmapImage _iconImage;

        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        /// <summary>The tile's icon: an https address for store apps, ms-appx:/// for built-in ones.</summary>
        public string Icon { get; private set; }
        public Color Color { get; private set; }
        public bool IsBuiltIn { get; private set; }

        /// <summary>The XAML page of a native app; null for web apps.</summary>
        internal Type PageType { get; private set; }

        /// <summary>Where a web app starts.</summary>
        public string Url { get; private set; }
        /// <summary>The user agent a web app needs (TV web apps often check for a TV), or null for WebView2's own.</summary>
        public string UserAgent { get; private set; }
        /// <summary>
        /// True for web apps made for TVs: the controller works as a TV remote (arrow keys, Enter,
        /// Escape). Otherwise it moves the console's mouse pointer, as in a browser.
        /// </summary>
        public bool RemoteInput { get; private set; }
        /// <summary>What a built-in web app adds to its pages (YouTube's ad blocking, say).</summary>
        internal Func<IWebAppExtension> CreateExtension { get; private set; }

        /// <summary>The app package a package app installs, or null.</summary>
        internal AppPackage Package { get; private set; }
        public bool IsPackage
        {
            get { return Package != null; }
        }

        /// <summary>Who made it, with a link, where their terms ask for that. Shown in the Store.</summary>
        public string Credit { get; private set; }
        public bool HasCredit
        {
            get { return !string.IsNullOrEmpty(Credit); }
        }

        public SolidColorBrush Background
        {
            get { return new SolidColorBrush(Color); }
        }

        /// <summary>For the tile's Image.Source, which x:Bind can't set from a string.</summary>
        public ImageSource IconImage
        {
            get
            {
                Uri uri;
                if (_iconImage == null && Uri.TryCreate(Icon, UriKind.Absolute, out uri))
                {
                    _iconImage = new BitmapImage(uri);
                }
                return _iconImage;
            }
        }

        internal static AppEntry Native(string id, string name, string icon, Color color, Type pageType)
        {
            return new AppEntry { Id = id, Name = name, Icon = icon, Color = color, PageType = pageType, IsBuiltIn = true };
        }

        internal static AppEntry BuiltInWeb(string id, string name, string icon, Color color, string url,
            bool remoteInput, Func<IWebAppExtension> createExtension)
        {
            return new AppEntry
            {
                Id = id, Name = name, Icon = icon, Color = color, Url = url, RemoteInput = remoteInput,
                CreateExtension = createExtension, IsBuiltIn = true,
            };
        }

        /// <summary>
        /// A store app, from the store index or from the saved list of installed apps (the same
        /// shape: see apps/README.md). Null if it isn't a valid entry.
        /// </summary>
        internal static AppEntry FromJson(JsonObject json)
        {
            var id = GetString(json, "id");
            var name = GetString(json, "name");
            var url = GetString(json, "url");
            var package = json.GetNamedValue("package", JsonValue.CreateNullValue());
            var app = new AppEntry
            {
                Id = id,
                Name = name,
                Description = GetString(json, "description") ?? string.Empty,
                Icon = GetString(json, "icon"),
                Color = ParseColor(GetString(json, "color")),
                Credit = GetString(json, "credit"),
            };
            if (package.ValueType == JsonValueType.Object)
            {
                app.Package = AppPackage.FromJson(package.GetObject());
                if (app.Package == null)
                {
                    return null;
                }
            }
            else
            {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
                {
                    return null;
                }
                app.Url = url;
                app.UserAgent = GetString(json, "userAgent");
                app.RemoteInput = GetString(json, "input") == "remote";
            }
            return string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) ? null : app;
        }

        internal JsonObject ToJson()
        {
            var json = new JsonObject();
            json.SetNamedValue("id", JsonValue.CreateStringValue(Id));
            json.SetNamedValue("name", JsonValue.CreateStringValue(Name));
            json.SetNamedValue("description", JsonValue.CreateStringValue(Description ?? string.Empty));
            json.SetNamedValue("color", JsonValue.CreateStringValue(string.Format("#{0:X2}{1:X2}{2:X2}", Color.R, Color.G, Color.B)));
            if (Package != null)
            {
                json.SetNamedValue("package", Package.ToJson());
            }
            else
            {
                json.SetNamedValue("url", JsonValue.CreateStringValue(Url));
                json.SetNamedValue("input", JsonValue.CreateStringValue(RemoteInput ? "remote" : "pointer"));
            }
            if (Credit != null)
            {
                json.SetNamedValue("credit", JsonValue.CreateStringValue(Credit));
            }
            if (Icon != null)
            {
                json.SetNamedValue("icon", JsonValue.CreateStringValue(Icon));
            }
            if (UserAgent != null)
            {
                json.SetNamedValue("userAgent", JsonValue.CreateStringValue(UserAgent));
            }
            return json;
        }

        internal static Color ParseColor(string hex)
        {
            uint value;
            if (hex != null && hex.Length == 7 && hex[0] == '#' &&
                uint.TryParse(hex.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out value))
            {
                return Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            }
            return Color.FromArgb(255, 42, 45, 58);
        }

        internal static string GetString(JsonObject json, string name)
        {
            var value = json.GetNamedValue(name, JsonValue.CreateNullValue());
            return value.ValueType == JsonValueType.String ? value.GetString() : null;
        }
    }
}
