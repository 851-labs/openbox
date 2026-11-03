using System;
using System.Text.RegularExpressions;
using OpenBox.Apps;
using Windows.Data.Json;

namespace OpenBox.Packages
{
    /// <summary>
    /// The app package a package app installs: where its developer publishes it, how to check
    /// the download, and which installed app the tile launches. See apps/README.md.
    /// </summary>
    internal sealed class AppPackage
    {
        private static readonly Regex FamilyNamePattern = new Regex("^[A-Za-z0-9.-]{3,50}_[0-9a-z]{13}$");

        /// <summary>The .appx or .msix, downloaded from where its developer publishes it.</summary>
        public Uri Url { get; private set; }
        /// <summary>A file whose first word is the package's SHA-256, published with it. Optional.</summary>
        public Uri Sha256Url { get; private set; }
        /// <summary>The installed package's family name, e.g. BanditVault.Launcher_r9d4qta1rpjdr.</summary>
        public string FamilyName { get; private set; }
        /// <summary>The Application Id in the package's manifest that the tile launches.</summary>
        public string AppId { get; private set; }

        public static AppPackage FromJson(JsonObject json)
        {
            Uri url, sha256Url = null;
            var sha256 = AppEntry.GetString(json, "sha256Url");
            var familyName = AppEntry.GetString(json, "familyName");
            if (!Uri.TryCreate(AppEntry.GetString(json, "url"), UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps ||
                (sha256 != null && (!Uri.TryCreate(sha256, UriKind.Absolute, out sha256Url) || sha256Url.Scheme != Uri.UriSchemeHttps)) ||
                familyName == null || !FamilyNamePattern.IsMatch(familyName))
            {
                return null;
            }
            return new AppPackage
            {
                Url = url,
                Sha256Url = sha256Url,
                FamilyName = familyName,
                AppId = AppEntry.GetString(json, "appId") ?? "App",
            };
        }

        public JsonObject ToJson()
        {
            var json = new JsonObject();
            json.SetNamedValue("url", JsonValue.CreateStringValue(Url.AbsoluteUri));
            if (Sha256Url != null)
            {
                json.SetNamedValue("sha256Url", JsonValue.CreateStringValue(Sha256Url.AbsoluteUri));
            }
            json.SetNamedValue("familyName", JsonValue.CreateStringValue(FamilyName));
            json.SetNamedValue("appId", JsonValue.CreateStringValue(AppId));
            return json;
        }
    }
}
