using PUTool.Core.Enums;
using PUTool.Core.Models;

namespace PUTool.Infrastructure.Models;

internal sealed record PackageInspection(CompatibilityStatus Status, IReadOnlyList<PackageDependencyInfo> Dependencies);