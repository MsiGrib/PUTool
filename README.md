# PUTool

PUTool is a cross-platform Avalonia desktop utility for inspecting NuGet dependencies in .NET solutions and preparing controlled package updates. It helps developers understand where package versions are declared, compare package versions across projects, and review update recommendations before changing repository files.

## Use Cases

1. **Dependency Auditing**: Scan a repository containing one or more `.csproj` projects and inspect direct and restored transitive NuGet packages.
2. **Central Package Management**: Identify projects using Central Package Management, local package references, overrides, or mixed version management.
3. **Package Updates**: Check package versions against a selected target framework, prepare an update plan, export it to Markdown, and optionally apply eligible changes.

## Features

- **Solution Scanning**: Finds `.sln`, `.slnx`, and `.csproj` files under a selected directory.
- **Parallel Analysis**: Evaluates projects concurrently, using a worker limit based on the device's logical processor count. The analysis status reports the number of threads.
- **MSBuild Evaluation**: Reads evaluated project properties and items, including imported props/targets and centrally managed package versions.
- **Package Views**:
  - **All usages**: Shows every package reference occurrence, including duplicates across projects and target frameworks.
  - **Package catalog**: Shows one detailed, aggregated row per package ID without project paths.
  - **By project**: Groups packages by project and removes duplicates within each project.
  - **Unique package IDs**: Shows a compact unique package-name list with usage counts.
- **Version Details**: Displays requested and resolved versions, target frameworks, management modes, and available compatible-version recommendations.
- **Update Planning**: Builds a dependency-aware update plan and explains why a proposed change may require manual review.
- **Safe XML Editing**: Patches only the version text in eligible declarations. Existing XML formatting, whitespace, line endings, attribute order, and quote style are preserved.
- **Backups and Verification**: Creates backups by default, then runs `dotnet restore` and `dotnet build`. Restore or Build failures do not silently revert saved edits.
- **Markdown Export**: Opens a system Save File dialog so the update plan can be saved to a chosen location.
- **Help Page**: Explains scan messages, package management modes, package list views, update statuses, and the safe update workflow.
- **Dark UI**: Provides a cross-platform dark interface built with Avalonia and MVVM.

## Usage Instructions

1. Launch PUTool and select the root folder of your repository, a `.sln`/`.slnx` file, or a folder containing `.csproj` projects.
2. Select **Scan repository**. Projects are evaluated in parallel; warnings are shown on the Overview page.
3. Open **Projects** and **All packages** to inspect target frameworks, package versions, and version management sources.
4. Enter the target TFM to use for package compatibility recommendations (for example, `net11.0`). This value is for analysis only; PUTool does not change `TargetFramework`, `TargetFrameworks`, or `global.json`.
5. Select **Check NuGet updates**, then review the proposed versions and any blocked reasons on the Update plan page.
6. Export the plan to Markdown or select **Apply changes**. Backups are enabled by default and can be disabled with the checkbox.
7. Review the Restore/Build output and inspect the repository diff before committing the changes.

## Package Status and Safety Notes

- **Central**: The package version is provided by Central Package Management through an imported props file.
- **Local**: The package version is declared locally in the project.
- **Override**: A project-level `VersionOverride` takes precedence over the central version.
- **Mixed**: The project uses more than one package version management approach.
- **Transitive**: The package is introduced by another package rather than a direct `PackageReference`.
- **Unknown**: MSBuild metadata was insufficient to classify the declaration.
- **Applicable**: A literal version and an unambiguous source declaration were found, so the version text can be safely patched.
- **Needs review**: Automatic editing is blocked, for example because a version uses an MSBuild expression, a condition is present, or multiple source declarations are possible.

A metadata-based compatibility recommendation is not a guarantee that the entire solution builds. NuGet Restore and project Build remain the final validation steps. The selected target TFM is not a migration command; developers must update project target frameworks separately.

## Technical Details

- **UI**: Avalonia UI with MVVM and CommunityToolkit.Mvvm.
- **Project Analysis**: `dotnet msbuild` evaluation and `project.assets.json` inspection.
- **NuGet Metadata**: NuGet V3 flat-container endpoints and NuGet framework/version libraries.
- **File Updates**: Text-level XML patching to avoid reformatting unrelated project content.
- **Application Target**: `net10.0`. PUTool can analyze projects that target other installed SDK frameworks, including `net11.0` when the matching SDK and targeting packs are available.

## Requirements

- .NET 10 SDK to build and run the application from source.
- An installed SDK and targeting packs for any framework that must be fully evaluated or built.
- Network access to `api.nuget.org` for public package version recommendations.

## Creating a Publish Version

Run the commands from the repository root. Each command publishes a self-contained application for the selected runtime identifier.

# 🪟 WINDOWS x64
dotnet publish .\src\PUTool.Desktop\PUTool.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\releases\PUTool-Windows-x64

# 🪟 WINDOWS ARM64
dotnet publish .\src\PUTool.Desktop\PUTool.Desktop.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\releases\PUTool-Windows-ARM64

# 🐧 LINUX x64
dotnet publish .\src\PUTool.Desktop\PUTool.Desktop.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\releases\PUTool-Linux-x64

# 🐧 LINUX ARM64
dotnet publish .\src\PUTool.Desktop\PUTool.Desktop.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\releases\PUTool-Linux-ARM64

# 🍎 macOS Intel (x64)
dotnet publish .\src\PUTool.Desktop\PUTool.Desktop.csproj -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\releases\PUTool-macOS-Intel

# 🍎 macOS Apple Silicon (ARM64)
dotnet publish .\src\PUTool.Desktop\PUTool.Desktop.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\releases\PUTool-macOS-AppleSilicon