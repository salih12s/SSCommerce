using System.Reflection;
using System.Xml.Linq;
using SSCommerce.Application;
using SSCommerce.Domain;

namespace SSCommerce.Api.Tests;

/// <summary>
/// Katman bağımlılık yönü: Domain -> hiçbiri; Application -> Domain; Infrastructure -> Application, Domain.
/// Asıl kilit csproj / Directory.*.props dosyalarını OKUYAN testlerdir.
/// Assembly-referans kontrolleri zayıf bir ikinci katmandır: derleyici kullanılmayan referansı metadata'ya yazmaz,
/// bu yüzden boş Domain/Application assembly'lerinde bu kontrol neredeyse her zaman geçer. Tek başına güvence sayılmaz.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(DomainAssemblyMarker).Assembly;
    private static readonly Assembly Application = typeof(ApplicationAssemblyMarker).Assembly;

    private static readonly string[] ForbiddenInApplication =
        ["Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql"];

    [Fact]
    public void Domain_has_no_project_or_package_references()
    {
        Assert.Empty(ReferencedProjects("SSCommerce.Domain"));
        Assert.Empty(ReferencedPackages("SSCommerce.Domain"));
        Assert.False(UsesFrameworkReference("SSCommerce.Domain"), "Domain FrameworkReference (ör. AspNetCore) kullanmamalı.");
    }

    [Fact]
    public void Application_references_only_Domain_and_no_infrastructure_packages()
    {
        Assert.Equal(["SSCommerce.Domain"], ReferencedProjects("SSCommerce.Application"));

        var forbidden = ReferencedPackages("SSCommerce.Application")
            .Where(p => ForbiddenInApplication.Any(prefix => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.Empty(forbidden);
        Assert.False(UsesFrameworkReference("SSCommerce.Application"), "Application FrameworkReference kullanmamalı.");
    }

    [Fact]
    public void Infrastructure_does_not_reference_Api()
    {
        Assert.DoesNotContain("SSCommerce.Api", ReferencedProjects("SSCommerce.Infrastructure"));
    }

    [Fact]
    public void Shared_props_cannot_leak_references_into_every_project()
    {
        var buildProps = XDocument.Load(Path.Combine(FindBackendRoot(), "Directory.Build.props"));
        foreach (var element in new[] { "PackageReference", "ProjectReference", "Reference", "FrameworkReference" })
        {
            Assert.Empty(buildProps.Descendants(element));
        }

        Assert.Equal("true", PropertyValue(buildProps, "TreatWarningsAsErrors"));
        Assert.Equal("enable", PropertyValue(buildProps, "Nullable"));
        Assert.Equal("enable", PropertyValue(buildProps, "ImplicitUsings"));

        var packages = XDocument.Load(Path.Combine(FindBackendRoot(), "Directory.Packages.props"));
        Assert.Equal("true", PropertyValue(packages, "ManagePackageVersionsCentrally"));
        // GlobalPackageReference tüm projelere (Domain dahil) paket sokar.
        Assert.Empty(packages.Descendants("GlobalPackageReference"));
    }

    [Fact]
    public void Package_versions_are_pinned_centrally()
    {
        var packages = XDocument.Load(Path.Combine(FindBackendRoot(), "Directory.Packages.props"));
        var pinned = packages.Descendants("PackageVersion")
            .Select(e => (string)e.Attribute("Include")!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var csproj in AllProjectFiles())
        {
            foreach (var reference in XDocument.Load(csproj).Descendants("PackageReference"))
            {
                var name = (string)reference.Attribute("Include")!;
                Assert.Null(reference.Attribute("Version"));
                Assert.Contains(name, pinned);
            }
        }
    }

    [Fact]
    public void Compiled_assemblies_do_not_reference_upper_layers_weak_second_layer()
    {
        // Zayıf kontrol (bkz. sınıf özeti): yalnızca gerçekten kullanılan referansları görür.
        Assert.Empty(SolutionAssemblyReferences(Domain));
        var application = SolutionAssemblyReferences(Application);
        Assert.DoesNotContain("SSCommerce.Infrastructure", application);
        Assert.DoesNotContain("SSCommerce.Api", application);
    }

    private static string[] SolutionAssemblyReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("SSCommerce.", StringComparison.Ordinal))
            .Order()
            .ToArray();

    private static string ProjectFile(string project) =>
        Path.Combine(FindBackendRoot(), "src", project, $"{project}.csproj");

    private static IEnumerable<string> AllProjectFiles() =>
        Directory.EnumerateFiles(Path.Combine(FindBackendRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(FindBackendRoot(), "tests"), "*.csproj", SearchOption.AllDirectories));

    private static string[] ReferencedProjects(string project) =>
        XDocument.Load(ProjectFile(project))
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')))
            .Order()
            .ToArray();

    private static string[] ReferencedPackages(string project) =>
        XDocument.Load(ProjectFile(project))
            .Descendants("PackageReference")
            .Select(e => (string)e.Attribute("Include")!)
            .Order()
            .ToArray();

    private static bool UsesFrameworkReference(string project) =>
        XDocument.Load(ProjectFile(project)).Descendants("FrameworkReference").Any();

    private static string? PropertyValue(XDocument document, string name) =>
        document.Descendants(name).Select(e => e.Value.Trim()).FirstOrDefault();

    private static string FindBackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SSCommerce.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("SSCommerce.slnx bulunamadı.");
    }
}
