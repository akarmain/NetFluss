// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using Xunit;

namespace NetFluss.Native.Tests;

public class CountryNamesTests
{
    [Theory]
    [InlineData("DE", "en", "Germany")]
    [InlineData("DE", "de", "Deutschland")]
    [InlineData("ch", "en", "Switzerland")]
    [InlineData("CH", "de", "Schweiz")]
    [InlineData("DE", "zh-Hans", "德国")]
    [InlineData("TW", "zh-Hant", "台灣")]
    public void NamesTheCountryInTheAppLanguage(string code, string language, string expected)
        => Assert.Equal(expected, CountryNames.Display(code, CultureInfo.GetCultureInfo(language)));

    [Fact]
    public void UnknownCode_IsNeverEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(CountryNames.Display("QQ", CultureInfo.GetCultureInfo("en"))));
}
