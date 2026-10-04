// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using Xunit;

namespace NetFluss.Core.Tests;

/// <summary>
/// <see cref="Localization"/> holds the chosen language in static state, and xUnit runs
/// test classes in parallel. Every class that switches the language joins this collection so
/// they run one after another — otherwise one class's German leaks into another's English
/// assertion, and the failure moves around from run to run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LocalizationCollection
{
    public const string Name = "Localization (static language state)";
}
