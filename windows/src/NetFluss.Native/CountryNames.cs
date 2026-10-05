// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;

namespace NetFluss.Native;

/// <summary>
/// A country's name in the app's language — the macOS <c>Locale.localizedString(forRegionCode:)</c>.
/// .NET's <see cref="RegionInfo.DisplayName"/> cannot do this: built from a bare code it is
/// always the country's own name ("Deutschland" in an English UI). Windows' ICU (icu.dll,
/// part of Windows 10 1903 and later, and what .NET itself uses) names a country in any
/// language.
/// </summary>
public static unsafe class CountryNames
{
    private static readonly ConcurrentDictionary<(string Code, string Language), string> Cache = new();

    /// <summary>"Germany" for "DE" in English, "Deutschland" in German; the code when unknown.</summary>
    public static string Display(string code, CultureInfo language)
        => Cache.GetOrAdd((code.ToUpperInvariant(), language.Name), static key => Lookup(key.Code, key.Language));

    private static string Lookup(string code, string language)
    {
        try
        {
            if (Icu(code, language) is { } name)
            {
                return name;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows before 1903: no system ICU.
        }

        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    private static string? Icu(string code, string language)
    {
        var display = language.Length == 0 ? "en" : language.Replace('-', '_');
        var buffer = stackalloc char[128];
        var status = 0;
        var length = uloc_getDisplayCountry("_" + code, display, buffer, 128, &status);

        // ICU answers a region it has no name for with the code itself; RegionInfo may know better.
        var name = status <= 0 && length > 0 && length < 128 ? new string(buffer, 0, length) : null;
        return name == code ? null : name;
    }

    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int uloc_getDisplayCountry(
        [MarshalAs(UnmanagedType.LPStr)] string locale,
        [MarshalAs(UnmanagedType.LPStr)] string displayLocale,
        char* country,
        int capacity,
        int* status);
}
