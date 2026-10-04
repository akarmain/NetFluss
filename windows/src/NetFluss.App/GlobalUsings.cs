// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

// WPF has its own System.Windows.Localization type, which every file that imports
// System.Windows would otherwise have to disambiguate by hand.
global using Localization = NetFluss.Core.Localization;
