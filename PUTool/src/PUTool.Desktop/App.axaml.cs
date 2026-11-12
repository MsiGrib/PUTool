using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PUTool.Desktop.ViewModels;
using PUTool.Infrastructure.Execution;
using PUTool.Infrastructure.NuGet;
using PUTool.Infrastructure.Planning;
using PUTool.Infrastructure.Processes;
using PUTool.Infrastructure.Scanning;

namespace PUTool.Desktop;

public partial class App : Application
{
    public override void Initialize()
        => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var processRunner = new ProcessRunner();
            var evaluator = new MsBuildProjectEvaluator(processRunner);
            var environment = new DotNetEnvironmentService(processRunner);
            var scanner = new SolutionScanner(evaluator, environment);
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PUTool/0.2.0");

            var nuget = new NuGetFlatContainerClient(httpClient);
            var planner = new UpdatePlanner();
            var executor = new UpdateExecutor(processRunner);
            var exporter = new PlanExporter();
            var viewModel = new ShellViewModel(scanner, nuget, planner, executor, exporter);

            desktop.MainWindow = new MainWindow(viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }
}