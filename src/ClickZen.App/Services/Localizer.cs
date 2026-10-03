using System.Globalization;
using ClickZen.Core.Settings;
using Microsoft.Windows.ApplicationModel.Resources;

namespace ClickZen.App.Services;

/// <summary>Access to localized strings from Strings/&lt;lang&gt;/Resources.resw.</summary>
public interface ILocalizer
{
    string this[string key] { get; }
    string Format(string key, params object?[] args);
}

/// <summary>
/// MRT Core-backed localizer. The UI language is chosen once at startup from settings
/// (changing it requires a restart, which the Settings page tells the user).
/// </summary>
public sealed class Localizer : ILocalizer
{
    private readonly ResourceLoader _loader;

    public Localizer()
    {
        _loader = new ResourceLoader();
    }

    public string this[string key]
    {
        get
        {
            var v = _loader.GetString(key.Replace('.', '/'));
            return string.IsNullOrEmpty(v) ? key : v;
        }
    }

    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, this[key], args);

    /// <summary>BCP-47 tag for a preference, or empty for "follow system".</summary>
    public static string LanguageTag(LanguagePreference pref) => pref switch
    {
        LanguagePreference.ZhHans => "zh-CN",
        LanguagePreference.English => "en-US",
        _ => "",
    };

    /// <summary>Must run before any XAML is loaded.</summary>
    public static void ApplyLanguage(LanguagePreference pref)
    {
        var tag = LanguageTag(pref);
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = tag;
        }
        catch (Exception)
        {
            // Not fatal – fall back to OS language.
        }

        if (tag.Length > 0)
        {
            var culture = new CultureInfo(tag);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
    }
}
