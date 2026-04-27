using System.Diagnostics.CodeAnalysis;
using Devolutions.Pinget.Core;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal interface IPingetPackageDetailsProvider
{
    void LoadPackageDetails(IPackageDetails details, INativeTaskLogger logger);
}

internal sealed class PingetPackageDetailsProvider : IPingetPackageDetailsProvider
{
    private readonly Func<PackageQuery, ShowResult> _showPackage;
    private readonly Func<Uri, long> _installerSizeResolver;

    public PingetPackageDetailsProvider()
        : this(ShowWithRepository) { }

    internal PingetPackageDetailsProvider(
        Func<PackageQuery, ShowResult> showPackage,
        Func<Uri, long>? installerSizeResolver = null
    )
    {
        _showPackage = showPackage;
        _installerSizeResolver = installerSizeResolver ?? CoreTools.GetFileSizeAsLong;
    }

    public void LoadPackageDetails(IPackageDetails details, INativeTaskLogger logger)
    {
        try
        {
            PackageQuery query = CreateQuery(details.Package);
            logger.Log("Loading WinGet installer metadata with Pinget");
            logger.Log($" Query: {FormatQueryForLog(query)}");

            ShowResult result = _showPackage(query);

            foreach (string warning in result.Warnings)
            {
                logger.Log(" Pinget warning: " + warning);
            }

            ApplyShowResult(details, result, _installerSizeResolver);
        }
        catch (Exception ex)
        {
            Logger.Warn(
                "Could not load WinGet installer metadata with Pinget for package "
                    + $"{details.Package.Id}: {ex.Message}"
            );
            logger.Error(ex);
        }
    }

    internal static PackageQuery CreateQuery(IPackage package)
    {
        string id = package.Id.TrimEnd('…');
        string name = package.Name.TrimEnd('…');

        PackageQuery query;
        if (!string.IsNullOrWhiteSpace(id))
        {
            query = new PackageQuery
            {
                Id = id,
                Exact = !package.Id.EndsWith('…'),
            };
        }
        else
        {
            query = new PackageQuery
            {
                Name = name,
                Exact = !package.Name.EndsWith('…'),
            };
        }

        if (!package.Source.IsVirtualManager && !string.IsNullOrWhiteSpace(package.Source.Name))
        {
            query = query with { Source = package.Source.Name };
        }

        return query;
    }

    internal static void ApplyShowResult(
        IPackageDetails details,
        ShowResult result,
        Func<Uri, long>? installerSizeResolver = null
    )
    {
        installerSizeResolver ??= CoreTools.GetFileSizeAsLong;
        Manifest manifest = result.Manifest;
        Installer? installer = result.SelectedInstaller ?? manifest.Installers.FirstOrDefault();

        SetIfMissing(value => details.Author = value, details.Author, manifest.Author);
        SetIfMissing(value => details.Description = value, details.Description, manifest.Description);
        SetIfMissing(value => details.License = value, details.License, manifest.License);
        SetIfMissing(value => details.Publisher = value, details.Publisher, manifest.Publisher);
        SetIfMissing(value => details.ReleaseNotes = value, details.ReleaseNotes, manifest.ReleaseNotes);

        SetUriIfMissing(uri => details.HomepageUrl = uri, details.HomepageUrl, manifest.PackageUrl);
        SetUriIfMissing(uri => details.LicenseUrl = uri, details.LicenseUrl, manifest.LicenseUrl);
        SetUriIfMissing(
            uri => details.ReleaseNotesUrl = uri,
            details.ReleaseNotesUrl,
            manifest.ReleaseNotesUrl
        );

        if (details.Tags.Length == 0 && manifest.Tags.Count > 0)
        {
            details.Tags = manifest.Tags.ToArray();
        }

        if (installer is not null)
        {
            SetIfPresent(value => details.InstallerHash = value, installer.Sha256);
            SetIfPresent(value => details.InstallerType = value, installer.InstallerType);
            SetIfPresent(value => details.UpdateDate = value, installer.ReleaseDate);

            if (TryCreateUri(installer.Url, out Uri? installerUri))
            {
                details.InstallerUrl = installerUri;
                details.InstallerSize = installerSizeResolver(installerUri);
            }
        }

        details.Dependencies.Clear();
        foreach (string dependency in manifest.PackageDependencies.Concat(
                     installer?.PackageDependencies ?? []
                 ).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryCreateDependency(dependency, out IPackageDetails.Dependency parsedDependency))
            {
                details.Dependencies.Add(parsedDependency);
            }
        }
    }

    private static ShowResult ShowWithRepository(PackageQuery query)
    {
        using Repository repository = OpenRepository();
        return repository.Show(query);
    }

    private static Repository OpenRepository()
    {
        return Repository.Open(
            new RepositoryOptions
            {
                AppRoot = Path.Join(CoreData.UniGetUIDataDirectory, "Pinget"),
                UserAgent = CoreData.UserAgentString,
            }
        );
    }

    private static string FormatQueryForLog(PackageQuery query)
    {
        return string.Join(
            ", ",
            new[]
            {
                $"Id={query.Id}",
                $"Name={query.Name}",
                $"Source={query.Source}",
                $"Exact={query.Exact}",
            }.Where(part => !part.EndsWith("="))
        );
    }

    private static void SetIfMissing(
        Action<string> setValue,
        string? currentValue,
        string? newValue
    )
    {
        if (string.IsNullOrWhiteSpace(currentValue) && !string.IsNullOrWhiteSpace(newValue))
        {
            setValue(newValue);
        }
    }

    private static void SetIfPresent(Action<string> setValue, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            setValue(value);
        }
    }

    private static void SetUriIfMissing(Action<Uri> setValue, Uri? currentValue, string? value)
    {
        if (currentValue is null && TryCreateUri(value, out Uri? uri))
        {
            setValue(uri);
        }
    }

    private static bool TryCreateUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri);
    }

    private static bool TryCreateDependency(
        string value,
        out IPackageDetails.Dependency dependency
    )
    {
        dependency = default;
        string trimmedValue = value.Trim();
        if (trimmedValue == "")
            return false;

        string name = trimmedValue;
        string version = "";
        int versionStart = trimmedValue.IndexOf('[', StringComparison.Ordinal);
        if (versionStart >= 0)
        {
            name = trimmedValue[..versionStart].Trim();
            version = trimmedValue[(versionStart + 1)..].TrimEnd(']').Trim();
        }

        if (name.Contains(' '))
        {
            name = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }

        if (name == "")
            return false;

        dependency = new IPackageDetails.Dependency
        {
            Name = name,
            Version = version,
            Mandatory = true,
        };
        return true;
    }
}
