namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal enum WinGetCliBackendKind
{
    SystemWinGet,
    BundledPinget,
}

internal enum WinGetCliBackendPreference
{
    Auto,
    PreferSystemWinGet,
    PreferBundledPinget,
    BundledPingetOnly,
}

internal enum WinGetNativeApiPolicy
{
    Auto,
    Enabled,
    Disabled,
}
